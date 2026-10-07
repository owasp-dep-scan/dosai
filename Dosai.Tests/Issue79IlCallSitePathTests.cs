using System.Text;
using Depscan;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Dosai.Tests;

// Issue #79: a call site found in assembly IL used to carry the source path its PDB names as it is
// (absolute for a local build, /_/ for a deterministic one) on its MethodCalls row, beside a
// relative path on its edge and on the source analysis row for the same call, so cdxgen listed one
// line of source twice. The fixture is a source tree (src/App) with a build of it under out/, as
// `dotnet build -o out` leaves it, and a package beside it whose PDB names its own sources. Each
// test emits the app's PDB with the document path one kind of build writes.
public sealed class Issue79IlCallSitePathTests
{
    private const string AppSource = """
        using System;
        using System.Diagnostics;

        namespace App;

        public static class Program
        {
            public static void Main(string[] args)
            {
                var payload = new { Name = args.Length > 0 ? args[0] : "world" };
                Console.WriteLine(Vendor.Json.Serialize(payload));
                Action<string> write = Console.WriteLine;
                write(payload.Name);
                Process.Start(Console.ReadLine()!);
            }
        }
        """;

    private const int SerializeLine = 11;
    private const int DelegateLine = 13;

    private const string VendorSource = """
        namespace Vendor;

        public static class Json
        {
            public static string Serialize(object value) => value.ToString() ?? string.Empty;
        }
        """;

    /// <summary>The package's PDB names a file whose tail is also a file of the scanned tree.</summary>
    private const string VendorDocument = "/_/lib/src/App/Program.cs";

    public static TheoryData<string, string> BuildDocuments() => new()
    {
        { "local", "{root}/src/App/Program.cs" },
        { "deterministic", "/_/src/App/Program.cs" },
        { "windows agent", @"D:\a\repo\repo\src\App\Program.cs" },
        { "linux runner", "/home/runner/work/repo/repo/src/App/Program.cs" }
    };

    [Theory]
    [MemberData(nameof(BuildDocuments))]
    public void Methods_IlCallSitesOfTheTreesBuild_CarryTheSourceRowsRelativePath(string build, string document)
    {
        using var fixture = BuildFixture(document);
        var slice = Depscan.Dosai.GetMethodsSlice(fixture.Path);
        var expected = Path.Combine("src", "App", "Program.cs");

        var ilCalls = slice.MethodCalls.Where(IsIlCall).ToList();
        Assert.NotEmpty(ilCalls);
        Assert.All(ilCalls, call => Assert.False(call.Path is not null && Path.IsPathFullyQualified(call.Path), $"{build}: {call.Path}"));

        // The direct and the delegate call sites name the file as the source rows do.
        var serialize = Assert.Single(ilCalls, call => call is { CalledMethod: "Serialize", EvidenceKind: AnalysisEvidenceKind.AssemblyIlDirect });
        Assert.Equal((expected, "Program.cs", SerializeLine), (serialize.Path, serialize.FileName, serialize.LineNumber));
        Assert.Contains(slice.MethodCalls, call => call.EvidenceKind == AnalysisEvidenceKind.SourceRoslynDirect && call.CalledMethod == "Vendor.Json.Serialize(object)" && call.Path == expected && call.LineNumber == SerializeLine);
        var delegateTarget = Assert.Single(ilCalls, call => call.EvidenceKind == AnalysisEvidenceKind.AssemblyIlDelegateTarget && call.LineNumber == DelegateLine);
        Assert.Equal(expected, delegateTarget.Path);

        // Every IL call record and its edge agree on the path.
        var edgePaths = slice.CallGraph!.Edges
            .Where(edge => edge.EvidenceKind is AnalysisEvidenceKind.AssemblyIlDirect or AnalysisEvidenceKind.AssemblyIlDelegateTarget)
            .GroupBy(edge => (edge.SourceId, edge.TargetId, edge.CallLocation.LineNumber))
            .ToDictionary(group => group.Key, group => group.Select(edge => edge.Path).ToHashSet());
        foreach (var call in ilCalls.Where(call => call.EvidenceKind is AnalysisEvidenceKind.AssemblyIlDirect or AnalysisEvidenceKind.AssemblyIlDelegateTarget))
        {
            if (edgePaths.TryGetValue((call.SourceId!, call.TargetId!, call.LineNumber), out var paths))
            {
                Assert.Contains(call.Path, paths);
            }
        }

        Assert.Contains(slice.CallGraph.Edges, edge => edge.CalledMethodName == "Serialize" && edge.EvidenceKind == AnalysisEvidenceKind.AssemblyIlDirect && edge.Path == expected);

        // A call no sequence point covers names the assembly, relative to the root as well.
        Assert.Contains(ilCalls, call => call.Path == Path.Combine("out", "App.dll"));
        Assert.DoesNotContain(ilCalls, call => call.FileName == "App.dll" && call.Path != Path.Combine("out", "App.dll"));

        // The package's own sources have no place in the tree, even where a tail of their path matches.
        var vendorCalls = ilCalls.Where(call => call.CallerNamespace == "Vendor").ToList();
        Assert.NotEmpty(vendorCalls);
        Assert.All(vendorCalls, call => Assert.Equal(((string?)null, "Program.cs"), (call.Path, call.FileName)));
    }

    [Fact]
    public void Methods_SingleAssemblyTarget_PlacesOnlyWhatLiesUnderItsDirectory()
    {
        using var fixture = BuildFixture("{root}/src/App/Program.cs");
        var slice = Depscan.Dosai.GetMethodsSlice(Path.Combine(fixture.Path, "out", "App.dll"));
        var ilCalls = slice.MethodCalls.Where(IsIlCall).ToList();

        // The source file lies outside out/, which builds nothing from source: no path, as on the edge.
        var serialize = Assert.Single(ilCalls, call => call is { CalledMethod: "Serialize", EvidenceKind: AnalysisEvidenceKind.AssemblyIlDirect });
        Assert.Equal(((string?)null, "Program.cs", SerializeLine), (serialize.Path, serialize.FileName, serialize.LineNumber));
        Assert.Contains(slice.CallGraph!.Edges, edge => edge.CalledMethodName == "Serialize" && edge.Path is null && edge.CallLocation.FileName == "Program.cs");
        Assert.Contains(ilCalls, call => call.Path == "App.dll");
        Assert.DoesNotContain(ilCalls, call => call.Path is not null && Path.IsPathFullyQualified(call.Path));
    }

    [Theory]
    [MemberData(nameof(BuildDocuments))]
    public void DataFlows_IlNodesOfABuildOutput_NameTheFileLikeTheirEdges(string build, string document)
    {
        using var fixture = BuildFixture(document);
        var result = DataFlowAnalyzer.Analyze(Path.Combine(fixture.Path, "out"));

        // The build output holds no project, so the PDB's file has no place under it and the file
        // name stands in, on nodes as on edges: never a climb out of the root.
        var nodes = result.Nodes.Where(node => node.FileName == "Program.cs").ToList();
        Assert.True(nodes.Count > 0, build);
        Assert.All(nodes, node => Assert.Equal("Program.cs", node.Path));
        Assert.All(result.Edges.Where(edge => edge.FileName == "Program.cs"), edge => Assert.Equal("Program.cs", edge.Path));
        Assert.DoesNotContain(result.Nodes, node => node.Path is not null && (node.Path.StartsWith("..", StringComparison.Ordinal) || Path.IsPathFullyQualified(node.Path)));
        Assert.NotEmpty(result.Slices);
    }

    [Fact]
    public void SourceDocumentPaths_PlacesDocumentsInTheTree()
    {
        using var fixture = new TempDir();
        var root = Path.Combine(fixture.Path, "repo");
        Directory.CreateDirectory(Path.Combine(root, "src", "App"));
        File.WriteAllText(Path.Combine(root, "src", "App", "Program.cs"), string.Empty);
        File.WriteAllText(Path.Combine(fixture.Path, "outside.cs"), string.Empty);
        var appProgram = Path.Combine("src", "App", "Program.cs");

        Assert.Equal(appProgram, SourceDocumentPaths.InTree(root, Path.Combine(root, "src", "App", "Program.cs"), builtFromSource: false));
        Assert.Equal(appProgram, SourceDocumentPaths.InTree(root, "/_/src/App/Program.cs", builtFromSource: true));
        Assert.Equal(appProgram, SourceDocumentPaths.InTree(root, @"C:\build\src\App\Program.cs", builtFromSource: true));
        // Built in another copy of the tree that still exists: the copy's file, not the original.
        Assert.Equal(appProgram, SourceDocumentPaths.InTree(root, Path.Combine(fixture.Path, "copy", "src", "App", "Program.cs"), builtFromSource: true));

        Assert.Null(SourceDocumentPaths.InTree(root, "/_/src/App/Program.cs", builtFromSource: false));
        Assert.Null(SourceDocumentPaths.InTree(root, "/_/src/App/Missing.cs", builtFromSource: true));
        Assert.Null(SourceDocumentPaths.InTree(root, Path.Combine(fixture.Path, "outside.cs"), builtFromSource: false));
        // A tail never climbs out of the root.
        Assert.Null(SourceDocumentPaths.InTree(root, "/_/../outside.cs", builtFromSource: true));
        Assert.Null(SourceDocumentPaths.InTree(root, @"C:\..\outside.cs", builtFromSource: true));
        Assert.Null(SourceDocumentPaths.InTree(null, "/_/src/App/Program.cs", builtFromSource: true));
        Assert.Null(SourceDocumentPaths.InTree(root, " ", builtFromSource: true));

        // A file name in either file system's form.
        Assert.Equal("Program.cs", SourceDocumentPaths.FileName(@"D:\a\repo\src\App\Program.cs"));
        Assert.Equal("Program.cs", SourceDocumentPaths.FileName("/_/src/App/Program.cs"));
        Assert.Equal("Program.cs", SourceDocumentPaths.FileName("Program.cs"));

        Assert.Equal(Path.GetFullPath(root), SourceDocumentPaths.Root(root));
        Assert.Equal(Path.GetFullPath(Path.Combine(root, "src", "App")), SourceDocumentPaths.Root(Path.Combine(root, "src", "App", "Program.cs")));
    }

    private static bool IsIlCall(MethodCalls call) => call.EvidenceKind is AnalysisEvidenceKind.AssemblyIlDirect or AnalysisEvidenceKind.AssemblyIlDelegateTarget or AnalysisEvidenceKind.AssemblyIlVirtualCandidate or AnalysisEvidenceKind.AssemblyIlGeneratedState;

    /// <summary>
    ///     <c>src/App</c> with its project and source, and <c>out/</c> with the app built from that
    ///     source (its PDB naming <paramref name="appDocument" />, where <c>{root}</c> is the tree)
    ///     and a package built elsewhere.
    /// </summary>
    private static TempDir BuildFixture(string appDocument)
    {
        var fixture = new TempDir();
        var project = Path.Combine(fixture.Path, "src", "App");
        var output = Path.Combine(fixture.Path, "out");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(project, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(project, "Program.cs"), AppSource);
        var vendor = Emit(output, "Vendor", VendorSource, VendorDocument);
        Emit(output, "App", AppSource, appDocument.Replace("{root}", fixture.Path.Replace('\\', '/'), StringComparison.Ordinal), vendor);
        return fixture;
    }

    private static string Emit(string outputDirectory, string assemblyName, string source, string document, string? reference = null)
    {
        var metadata = FrameworkReferences.Current.References.Select(entry => (MetadataReference)entry.Reference).ToList();
        if (reference is not null) metadata.Add(MetadataReference.CreateFromFile(reference));
        var compilation = CSharpCompilation.Create(assemblyName,
            [CSharpSyntaxTree.ParseText(SourceText.From(source, Encoding.UTF8), path: document)],
            metadata,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
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

        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dosai-issue79-" + Guid.NewGuid().ToString("N"));

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
