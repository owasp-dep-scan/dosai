using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using Depscan;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Dosai.Tests;

// Issue #83: a build copies each referenced project's assembly into the output of every project
// that references it, and the data-flow IL pass (also run by crypto) analyzed every copy, so a
// library referenced by an app and its test project reported each flow three times, with nodes
// that differed only in their id. Byte-identical copies are now analyzed once.
public sealed class Issue83AssemblyCopyTests
{
    private const string StoreSource = """
        using System.IO;

        namespace Lib;

        public static class Store
        {
            public static string Load(string request)
            {
                return File.ReadAllText(request);
            }

            public static string Banner() => "tamper-me";
        }
        """;

    private const string VendorSource = """
        namespace Vendor;

        public static class Shell
        {
            public static void Run(string request)
            {
                System.Diagnostics.Process.Start(request);
            }
        }
        """;

    private static readonly string[] Outputs = ["Lib/bin/Debug/net8.0", "App/bin/Debug/net8.0", "App.Tests/bin/Debug/net8.0"];

    [Fact]
    public void DataFlows_ByteIdenticalCopiesOfAProjectAssembly_ReportEachFlowOnce()
    {
        using var fixture = new TempDir();
        WriteIssueTree(fixture.Path);

        var result = DataFlowAnalyzer.Analyze(fixture.Path);

        var slice = Assert.Single(IlSlices(result));
        Assert.Equal(("request", "ReadAllText"), (Node(result, slice.SourceId).Name, Node(result, slice.SinkId).Name));
        Assert.Single(result.Nodes, node => node is { IsSource: true, Name: "request" } && node.Properties.GetValueOrDefault("analysis") == "assembly-il");
        // One source file and one of the three assemblies.
        Assert.Equal(2, result.Statistics.FilesAnalyzed);
        Assert.Contains("2 assembly file(s) are byte-identical copies of another file in the tree and were not analyzed again, so each flow through them is reported once: Lib.dll analyzed in Lib/bin/Debug/net8.0 (copies in App/bin/Debug/net8.0, App.Tests/bin/Debug/net8.0).", result.Diagnostics);

        // crypto's data flows run the same pass.
        var crypto = CryptoAnalyzer.Analyze(fixture.Path).CryptoDataFlows;
        Assert.NotNull(crypto);
        Assert.Single(IlSlices(crypto));
    }

    [Fact]
    public void AssemblyCopies_AnalyzedCopyDependsOnThePathsAloneNeverOnTheListingOrder()
    {
        using var fixture = new TempDir();
        var copies = WriteIssueTree(fixture.Path);
        var reversed = copies.AsEnumerable().Reverse().ToList();

        // The project's own output, with its PDB, whatever order the files came in.
        foreach (var order in new[] { copies, reversed })
        {
            var collapsed = AssemblyCopies.Collapse(fixture.Path, order);
            var group = Assert.Single(collapsed.Groups);
            Assert.Equal(copies[0], group.Analyzed);
            Assert.Equal([copies[1], copies[2]], group.Copies);
            Assert.Equal([copies[0]], collapsed.Distinct);
        }

        // A copy without a PDB beside it is never the one analyzed (the pass reads its source
        // locations from there); among the others, tree order puts App/ before App.Tests/.
        File.Delete(Path.ChangeExtension(copies[0], ".pdb"));
        foreach (var order in new[] { copies, reversed })
        {
            var group = Assert.Single(AssemblyCopies.Collapse(fixture.Path, order).Groups);
            Assert.Equal(copies[1], group.Analyzed);
            Assert.Equal([copies[2], copies[0]], group.Copies);
        }

        Assert.True(AssemblyCopies.TreeOrder.Instance.Compare(Path.Combine("x", "App", "a.dll"), Path.Combine("x", "App.Tests", "a.dll")) < 0);
        Assert.True(AssemblyCopies.TreeOrder.Instance.Compare(Path.Combine("x", "App"), Path.Combine("x", "App", "a.dll")) < 0);
    }

    [Fact]
    public void DataFlows_ACopyWithTheSameModuleIdButOtherBytes_IsStillAnalyzed()
    {
        // A patched or tampered file keeps the module version id and, here, the length; only the
        // content decides that two files are one assembly.
        using var fixture = new TempDir();
        var copies = WriteIssueTree(fixture.Path);
        var bytes = File.ReadAllBytes(copies[2]);
        var original = Encoding.Unicode.GetBytes("tamper-me");
        var offset = bytes.AsSpan().IndexOf(original);
        Assert.True(offset > 0);
        Encoding.Unicode.GetBytes("tamper-it").CopyTo(bytes, offset);
        File.WriteAllBytes(copies[2], bytes);
        Assert.Equal(new FileInfo(copies[0]).Length, new FileInfo(copies[2]).Length);
        Assert.Equal(ModuleVersionId(copies[0]), ModuleVersionId(copies[2]));

        var result = DataFlowAnalyzer.Analyze(fixture.Path);

        Assert.Equal(2, IlSlices(result).Count);
        Assert.Contains("1 assembly file(s) are byte-identical copies of another file in the tree and were not analyzed again, so each flow through them is reported once: Lib.dll analyzed in Lib/bin/Debug/net8.0 (copies in App/bin/Debug/net8.0).", result.Diagnostics);
    }

    [Theory]
    [InlineData("reference", "dependency")]
    [InlineData("project", null)]
    public void DataFlows_CopiesOfOneAssembly_AreClassifiedTogether(string libraryType, string? expectedScope)
    {
        // The analyzed copy (it alone has a PDB) is a loose file nothing names, which on its own is
        // the application's; its copy in a build output is what that output's deps.json says.
        using var fixture = new TempDir();
        var libs = Path.Combine(fixture.Path, "libs");
        var bin = Path.Combine(fixture.Path, "App", "bin", "Debug", "net8.0");
        Directory.CreateDirectory(libs);
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(fixture.Path, "App", "App.csproj"), """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>""");
        var vendor = Emit(libs, "Vendor", VendorSource, "/_/vendor/Shell.cs");
        File.Copy(vendor, Path.Combine(bin, "Vendor.dll"));
        File.WriteAllText(Path.Combine(bin, "App.deps.json"), """
            {"targets":{".NETCoreApp,Version=v8.0":{"App/1.0.0":{"runtime":{"App.dll":{}}},"Vendor/1.0.0.0":{"runtime":{"Vendor.dll":{}}}}},
             "libraries":{"App/1.0.0":{"type":"project"},"Vendor/1.0.0.0":{"type":"LIBRARY_TYPE"}}}
            """.Replace("LIBRARY_TYPE", libraryType, StringComparison.Ordinal));

        var result = DataFlowAnalyzer.Analyze(fixture.Path);

        var slice = Assert.Single(IlSlices(result));
        Assert.Equal(expectedScope, slice.Scope);
        Assert.Contains(result.Diagnostics, line => line.Contains("Vendor.dll analyzed in libs (copies in App/bin/Debug/net8.0)", StringComparison.Ordinal));
    }

    /// <summary>
    ///     The reporter's tree: the library's project and source, and its build copied into the
    ///     outputs of the app and the test project, each with its PDB. Returns the three copies.
    /// </summary>
    private static List<string> WriteIssueTree(string root)
    {
        foreach (var project in new[] { "Lib", "App", "App.Tests" })
        {
            Directory.CreateDirectory(Path.Combine(root, project));
            File.WriteAllText(Path.Combine(root, project, $"{project}.csproj"), """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>""");
        }

        File.WriteAllText(Path.Combine(root, "Lib", "Store.cs"), StoreSource);
        var copies = Outputs.Select(output => Path.Combine([root, .. output.Split('/'), "Lib.dll"])).ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(copies[0])!);
        Emit(Path.GetDirectoryName(copies[0])!, "Lib", StoreSource, "/_/Lib/Store.cs");
        foreach (var copy in copies.Skip(1))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(copies[0], copy);
            File.Copy(Path.ChangeExtension(copies[0], ".pdb"), Path.ChangeExtension(copy, ".pdb"));
        }

        return copies;
    }

    private static List<DataFlowSlice> IlSlices(DataFlowResult result) =>
        result.Slices.Where(slice => slice.Summary?.StartsWith("Assembly IL", StringComparison.Ordinal) == true).ToList();

    private static DataFlowNode Node(DataFlowResult result, string? id) => result.Nodes.Single(node => node.Id == id);

    private static Guid ModuleVersionId(string path)
    {
        using var stream = File.OpenRead(path);
        using var peReader = new PEReader(stream);
        var reader = peReader.GetMetadataReader();
        return reader.GetGuid(reader.GetModuleDefinition().Mvid);
    }

    private static string Emit(string outputDirectory, string assemblyName, string source, string document)
    {
        var references = FrameworkReferences.Current.References.Select(entry => (MetadataReference)entry.Reference).ToList();
        var compilation = CSharpCompilation.Create(assemblyName,
            [CSharpSyntaxTree.ParseText(SourceText.From(source, Encoding.UTF8), path: document)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, deterministic: true));
        var dllPath = Path.Combine(outputDirectory, $"{assemblyName}.dll");
        using (var dll = File.Create(dllPath))
        using (var pdb = File.Create(Path.ChangeExtension(dllPath, ".pdb")))
        {
            var emit = compilation.Emit(dll, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));
            Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));
        }

        return dllPath;
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir() => Directory.CreateDirectory(Path);

        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dosai-issue83-" + Guid.NewGuid().ToString("N"));

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
