using System.Text.Json;
using Depscan;
using Xunit;

namespace Dosai.Tests;

// Issue #107: `methods` told a build output's own assemblies from its packages only through a
// *.deps.json in the scan root, which a source tree never has, so a package such as NodaTime
// was inventoried as application code until someone copied a deps.json there. `methods` and
// the data-flow IL pass now share one classification: the nearest *.deps.json at or above each
// assembly, then the restore metadata, and the name heuristic only for what nothing names.
public sealed class Issue107AssemblyScopeTests
{
    private const string AppSource = """
        namespace App;

        public static class Program
        {
            public static void Main(string[] args) => Lib.Greeter.Greet(args[0]);
        }
        """;

    private const string LibSource = """
        namespace Lib;

        public static class Greeter
        {
            public static void Greet(string name) => Vendor.Pkg.Shell.Run(name);
        }
        """;

    // A package whose name no heuristic prefix recognises, like NodaTime or MimeKit.
    private const string VendorSource = """
        namespace Vendor.Pkg;

        public static class Shell
        {
            public static void Run(string request)
            {
                System.Diagnostics.Process.Start(request);
            }

            // No application code calls this one.
            public static void Orphan()
            {
                System.Diagnostics.Process.Start(System.Console.ReadLine()!);
            }
        }
        """;

    [Fact]
    public void Methods_PackageAssembliesInABuildOutput_AreDependenciesWithoutARootDepsJson()
    {
        using var fixture = new TempDir();
        WriteTree(fixture.Path);

        var slice = Depscan.Dosai.GetMethodsSlice(fixture.Path);

        var assemblies = AssemblyNames(slice);
        Assert.Contains("App.dll", assemblies);
        Assert.Contains("Lib.dll", assemblies);
        Assert.DoesNotContain("Vendor.Pkg.dll", assemblies);
        Assert.DoesNotContain(slice.Diagnostics ?? [], line => line.Contains("name heuristic", StringComparison.Ordinal));
    }

    [Fact]
    public void Methods_ACopiedRootDepsJson_DoesNotChangeTheOutput()
    {
        using var fixture = new TempDir();
        WriteTree(fixture.Path);
        var before = Serialize(Depscan.Dosai.GetMethodsSlice(fixture.Path));

        File.Copy(Path.Combine(fixture.Path, "App", "bin", "Debug", "net8.0", "App.deps.json"), Path.Combine(fixture.Path, "App.deps.json"));
        var after = Serialize(Depscan.Dosai.GetMethodsSlice(fixture.Path));

        Assert.Equal(before, after);
    }

    [Fact]
    public void DataFlows_PackageAssembliesStayAnalyzedAsDependencies_WithOrWithoutARootDepsJson()
    {
        using var fixture = new TempDir();
        WriteTree(fixture.Path);

        var without = DataFlowAnalyzer.Analyze(fixture.Path);
        File.Copy(Path.Combine(fixture.Path, "App", "bin", "Debug", "net8.0", "App.deps.json"), Path.Combine(fixture.Path, "App.deps.json"));
        var with = DataFlowAnalyzer.Analyze(fixture.Path);

        // The #78 policy: the package is analyzed, its own flows are scoped to the dependency,
        // and the application's flow into the package-backed Greet survives.
        Assert.Equal(2, without.Slices.Count(slice => slice.Scope == "dependency"));
        Assert.Contains(without.Slices, slice => slice.Scope == "dependency" && without.Nodes.Single(node => node.Id == slice.SourceId).Name == "request");
        Assert.Contains(without.Slices, slice => slice.Scope != "dependency" && without.Nodes.Single(node => node.Id == slice.SinkId).Name == "Greet");
        Assert.Equal(JsonSerializer.Serialize(without.Slices.Select(Key)), JsonSerializer.Serialize(with.Slices.Select(Key)));
        Assert.Equal(without.Statistics.FilesAnalyzed, with.Statistics.FilesAnalyzed);
        Assert.Equal(without.PackageReachability?.Count, with.PackageReachability?.Count);
    }

    [Fact]
    public void DataFlows_ReachableDependencyFlows_KeepEveryApplicationSliceAndDropWhatNothingCalls()
    {
        using var fixture = new TempDir();
        WriteTree(fixture.Path);
        var all = DataFlowAnalyzer.Analyze(fixture.Path);

        DataFlowResult reachable;
        using (DependencyFlowScope.Apply(DependencyFlowMode.Reachable))
        {
            reachable = DataFlowAnalyzer.Analyze(fixture.Path);
        }

        // Orphan's own flow is gone; the flow through Run, which Greet calls, stays.
        Assert.Equal(2, all.Slices.Count(slice => slice.Scope == "dependency"));
        Assert.Single(reachable.Slices, slice => slice.Scope == "dependency");
        Assert.DoesNotContain(reachable.Nodes, node => node.MethodName == "Orphan");
        Assert.Equal(
            all.Slices.Where(slice => slice.Scope != "dependency").Select(slice => all.Nodes.Single(node => node.Id == slice.SinkId).Name),
            reachable.Slices.Where(slice => slice.Scope != "dependency").Select(slice => reachable.Nodes.Single(node => node.Id == slice.SinkId).Name));
        Assert.Contains(reachable.Diagnostics, line => line.Contains("not analyzed because no application code reaches them", StringComparison.Ordinal));
        Assert.DoesNotContain(all.Diagnostics, line => line.Contains("no application code reaches", StringComparison.Ordinal));
        Assert.True(DependencyFlowScope.TryParse("Reachable", out var parsed) && parsed == DependencyFlowMode.Reachable);
        Assert.False(DependencyFlowScope.TryParse("some", out _));
    }

    [Fact]
    public void Methods_AssembliesNothingNames_FallBackToTheNameHeuristicWithADiagnostic()
    {
        using var fixture = new TempDir();
        var bin = Path.Combine(fixture.Path, "loose");
        Directory.CreateDirectory(bin);
        EmitFixture.Emit(bin, "Contoso.Tool", LibSource.Replace("Vendor.Pkg.Shell.Run(name)", "System.Console.WriteLine(name)", StringComparison.Ordinal), Path.Combine(fixture.Path, "x.cs"));
        File.Copy(typeof(object).Assembly.Location, Path.Combine(bin, "System.Runtime.Fake.dll"));

        var result = AssemblyScope.Scope(fixture.Path, SafeFileRead.EnumerateAllFilesSafe(fixture.Path).Where(file => file.EndsWith(".dll", StringComparison.Ordinal)));

        Assert.Equal(["Contoso.Tool.dll"], result.Assemblies.Select(Path.GetFileName));
        Assert.Equal(2, result.ByNameHeuristic);
        Assert.Contains("name heuristic for 2 assembly", result.Diagnostic);
    }

    [Fact]
    public void Scope_UsesTheNearestDepsJson_NotOneInTheRoot()
    {
        using var fixture = new TempDir();
        WriteTree(fixture.Path);
        // A root deps.json that names only Vendor.Pkg as a project must not override the
        // nearest one beside App.dll.
        File.WriteAllText(Path.Combine(fixture.Path, "Other.deps.json"), """{"libraries":{"Vendor.Pkg/1.0.0":{"type":"project"}}}""");

        var result = AssemblyScope.Scope(fixture.Path, SafeFileRead.EnumerateAllFilesSafe(fixture.Path).Where(file => file.EndsWith(".dll", StringComparison.Ordinal)));

        Assert.DoesNotContain("Vendor.Pkg.dll", result.Assemblies.Select(Path.GetFileName));
        Assert.Equal(0, result.ByNameHeuristic);
    }

    private static object Key(DataFlowSlice slice) => new { slice.SourceId, slice.SinkId, slice.Scope, slice.Severity };

    private static string Serialize(MethodsSlice slice) =>
        JsonSerializer.Serialize(new { slice.Methods, slice.MethodCalls, slice.CallGraph, slice.Dependencies });

    private static List<string> AssemblyNames(MethodsSlice slice) => (slice.Methods ?? [])
        .Select(method => method.FileName)
        .Where(name => name.EndsWith(".dll", StringComparison.Ordinal))
        .Distinct(StringComparer.Ordinal)
        .ToList();

    /// <summary>
    ///     A source tree built twice over: App and Lib with their own outputs, and Vendor.Pkg, a
    ///     package, copied into the output of every project that references it.
    /// </summary>
    private static void WriteTree(string root)
    {
        foreach (var project in new[] { "Lib", "App" })
        {
            Directory.CreateDirectory(Path.Combine(root, project));
            File.WriteAllText(Path.Combine(root, project, $"{project}.csproj"), """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>""");
        }

        File.WriteAllText(Path.Combine(root, "App", "Program.cs"), AppSource);
        File.WriteAllText(Path.Combine(root, "Lib", "Greeter.cs"), LibSource);
        var libBin = Path.Combine(root, "Lib", "bin", "Debug", "net8.0");
        var appBin = Path.Combine(root, "App", "bin", "Debug", "net8.0");
        Directory.CreateDirectory(libBin);
        Directory.CreateDirectory(appBin);
        var vendor = EmitFixture.Emit(libBin, "Vendor.Pkg", VendorSource, "/_/Vendor/Shell.cs");
        var lib = EmitFixture.Emit(libBin, "Lib", LibSource, "/_/Lib/Greeter.cs", vendor);
        var app = EmitFixture.Emit(appBin, "App", AppSource, "/_/App/Program.cs", lib);
        File.Copy(lib, Path.Combine(appBin, "Lib.dll"));
        File.Copy(Path.ChangeExtension(lib, ".pdb"), Path.Combine(appBin, "Lib.pdb"));
        File.Copy(vendor, Path.Combine(appBin, "Vendor.Pkg.dll"));
        File.WriteAllText(Path.Combine(libBin, "Lib.deps.json"), Deps("Lib"));
        File.WriteAllText(Path.Combine(appBin, "App.deps.json"), Deps("App"));
        Assert.True(File.Exists(app));
    }

    private static string Deps(string project) => """
        {"targets":{".NETCoreApp,Version=v8.0":{
           "PROJECT/1.0.0":{"runtime":{"PROJECT.dll":{}}},
           "Lib/1.0.0":{"runtime":{"Lib.dll":{}}},
           "Vendor.Pkg/2.0.0":{"runtime":{"lib/net8.0/Vendor.Pkg.dll":{}}}}},
         "libraries":{"PROJECT/1.0.0":{"type":"project"},"Lib/1.0.0":{"type":"project"},"Vendor.Pkg/2.0.0":{"type":"package"}}}
        """.Replace("PROJECT", project, StringComparison.Ordinal);

    private sealed class TempDir : IDisposable
    {
        public TempDir() => Directory.CreateDirectory(Path);

        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dosai-issue107-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
