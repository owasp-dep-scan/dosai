using Depscan;
using Xunit;

namespace Dosai.Tests;

// Issue #111: the methods inventory read an assembly's types from the first copy it met. A class
// library's own bin has no packages next to it, so a type that needs one (here a class
// implementing a package's interface) could not load there, the identity was marked done, and
// the complete copy in the application's output was skipped. Which copy came first depended on
// directory names, so renaming the application changed the output.
public sealed class Issue111CopyOrderTests
{
    private const string PackageSource = """
        namespace Pkg;

        public interface IClock
        {
            long Now();
        }
        """;

    private const string CoreSource = """
        namespace Core;

        public class FixedClock : Pkg.IClock
        {
            public long Now() => 42;
        }

        public static class Greeting
        {
            public static string Hello() => "hello";
        }
        """;

    private const string AppSource = """
        namespace Entry;

        public static class Program
        {
            public static void Main() => System.Console.WriteLine(Core.Greeting.Hello());
        }
        """;

    [Theory]
    [InlineData("App")]
    [InlineData("Web")]
    [InlineData("Zeta")]
    public void Methods_ALibraryTypeThatNeedsAPackage_IsReadWhateverTheAppIsCalled(string appName)
    {
        using var fixture = new TempDir();
        WriteTree(fixture.Path, appName);

        var slice = Depscan.Dosai.GetMethodsSlice(fixture.Path);

        var coreMethods = CoreMethods(slice);
        Assert.Contains("FixedClock.Now", coreMethods);
        Assert.Contains("Greeting.Hello", coreMethods);
        // The IL call graph names the type's method too, on the same identity as the inventory.
        var now = Assert.Single(slice.Methods!, method => method is { ClassName: "FixedClock", Name: "Now" });
        Assert.Contains(slice.CallGraph!.Nodes, node => node.Id == now.AssemblySignature);
        // The copy that loaded completely is the one the records came from.
        Assert.All((slice.Methods ?? []).Where(method => method.FileName == "Core.dll"), method => Assert.Contains($"{appName}", method.Path, StringComparison.Ordinal));
        Assert.Contains(slice.Diagnostics ?? [], line => line.Contains("Core.dll did not load completely", StringComparison.Ordinal) || appName == "App");
    }

    [Fact]
    public void Methods_TheSetOfMethods_DoesNotDependOnTheApplicationsDirectoryName()
    {
        var sets = new List<List<string>>();
        foreach (var appName in new[] { "App", "Web" })
        {
            using var fixture = new TempDir();
            WriteTree(fixture.Path, appName);
            sets.Add(CoreMethods(Depscan.Dosai.GetMethodsSlice(fixture.Path)));
        }

        Assert.Equal(sets[0], sets[1]);
    }

    [Fact]
    public void Methods_WhenNoCopyLoadsCompletely_TheOneWithMostTypesIsUsed()
    {
        // Core alone, with no package anywhere: every copy loads Greeting only. Two copies, the
        // result is the same as one, and the partial-load warning is not repeated per copy.
        using var fixture = new TempDir();
        WriteTree(fixture.Path, "App", withPackageInApp: false);

        var slice = Depscan.Dosai.GetMethodsSlice(fixture.Path);

        Assert.DoesNotContain("FixedClock.Now", CoreMethods(slice));
        Assert.Contains("Greeting.Hello", CoreMethods(slice));
        Assert.Single(CoreMethods(slice), name => name == "Greeting.Hello");
    }

    [Fact]
    public void Methods_NeverReadsAnAssemblyFromOutsideTheScannedTree()
    {
        // The probe root was the parent of the scan path, which for a relative path is the
        // current directory: a package one level up (or wherever the process was started)
        // changed what loaded.
        using var outer = new TempDir();
        var tree = Path.Combine(outer.Path, "tree");
        WriteTree(tree, "App", withPackageInApp: false);
        File.Copy(Path.Combine(tree, "Core", "bin", "Debug", "net8.0", "Core.dll"), Path.Combine(outer.Path, "Core.dll"));
        EmitFixture.Emit(outer.Path, "Pkg", PackageSource, "/_/Pkg/IClock.cs");

        var slice = Depscan.Dosai.GetMethodsSlice(tree);

        Assert.DoesNotContain("FixedClock.Now", CoreMethods(slice));
    }

    private static List<string> CoreMethods(MethodsSlice slice) => (slice.Methods ?? [])
        .Where(method => method.FileName == "Core.dll" && method.Name is not null)
        .Select(method => $"{method.ClassName}.{method.Name}")
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .Where(name => name.StartsWith("FixedClock.", StringComparison.Ordinal) || name.StartsWith("Greeting.", StringComparison.Ordinal))
        .ToList();

    /// <summary>
    ///     Core builds into its own bin without Pkg; the application's output holds Pkg and a
    ///     copy of Core. Directory order puts Core/ before Web/ and after App/.
    /// </summary>
    private static void WriteTree(string root, string appName, bool withPackageInApp = true)
    {
        var coreBin = Path.Combine(root, "Core", "bin", "Debug", "net8.0");
        var appBin = Path.Combine(root, appName, "bin", "Debug", "net8.0");
        Directory.CreateDirectory(coreBin);
        Directory.CreateDirectory(appBin);
        foreach (var project in new[] { "Core", appName })
        {
            File.WriteAllText(Path.Combine(root, project, $"{project}.csproj"), """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>""");
        }

        var scratch = Path.Combine(root, "scratch");
        Directory.CreateDirectory(scratch);
        var pkg = EmitFixture.Emit(scratch, "Pkg", PackageSource, "/_/Pkg/IClock.cs");
        var core = EmitFixture.Emit(coreBin, "Core", CoreSource, "/_/Core/Core.cs", pkg);
        File.Copy(core, Path.Combine(appBin, "Core.dll"));
        EmitFixture.Emit(appBin, appName, AppSource, $"/_/{appName}/Program.cs", core);
        if (withPackageInApp)
        {
            File.Copy(pkg, Path.Combine(appBin, "Pkg.dll"));
        }

        Directory.Delete(scratch, recursive: true);
        File.WriteAllText(Path.Combine(coreBin, "Core.deps.json"), """{"libraries":{"Core/1.0.0":{"type":"project"}}}""");
        File.WriteAllText(Path.Combine(appBin, $"{appName}.deps.json"), """{"libraries":{"APP/1.0.0":{"type":"project"},"Core/1.0.0":{"type":"project"},"Pkg/1.0.0":{"type":"package"}}}""".Replace("APP", appName, StringComparison.Ordinal));
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir() => Directory.CreateDirectory(Path);

        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dosai-issue111-" + Guid.NewGuid().ToString("N"));

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
