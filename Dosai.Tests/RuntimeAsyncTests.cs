using System.Reflection;
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

// .NET 11 runtime async (`<Features>runtime-async=on</Features>`): the compiler emits an async
// method as one method the runtime suspends, with no state-machine type and no MoveNext. The
// shared framework is itself compiled this way, so assembly-only inputs meet it more and more.
// The IL passes must read such a method like any other: its own sequence points place its
// sources, and its calls - interleaved with AsyncHelpers.Await - start at the method itself.
public sealed class RuntimeAsyncTests
{
    private const string StoreSource = """
        using System;
        using System.IO;
        using System.Threading.Tasks;

        namespace Lib;

        public static class Store
        {
            public static async Task<string> LoadAsync(string request)
            {
                await Task.Yield();
                return File.ReadAllText(request);
            }

            public static async Task<string> RelayAsync(string request)
            {
                var text = await LoadAsync(request);
                return text.Trim();
            }
        }
        """;

    // MethodImplAttributes.Async: the runtime-async marker on a method's implementation flags.
    private const MethodImplAttributes AsyncImplementation = (MethodImplAttributes)0x2000;

    [Fact]
    public void Emit_RuntimeAsyncFeature_ProducesNoStateMachine()
    {
        using var fixture = new TempDir();
        var dllPath = Emit(fixture.Path);

        using var pe = new PEReader(File.OpenRead(dllPath));
        var metadata = pe.GetMetadataReader();
        var store = metadata.TypeDefinitions.Select(metadata.GetTypeDefinition).Single(type => metadata.GetString(type.Name) == "Store");
        Assert.Empty(store.GetNestedTypes());
        var loadAsync = store.GetMethods().Select(metadata.GetMethodDefinition).Single(method => metadata.GetString(method.Name) == "LoadAsync");
        Assert.True((loadAsync.ImplAttributes & AsyncImplementation) != 0, "LoadAsync was not emitted as a runtime-async method.");
    }

    [Fact]
    public void Methods_RuntimeAsyncMethod_OwnsItsCallsAndIsPlacedInItsSource()
    {
        using var fixture = new TempDir();
        Emit(fixture.Path);

        var slice = Depscan.Dosai.GetMethodsSlice(fixture.Path);

        var nodes = slice.CallGraph!.Nodes;
        Assert.DoesNotContain(nodes, node => node.Id.Contains("d__", StringComparison.Ordinal) || node.Id.Contains("MoveNext", StringComparison.Ordinal));
        var loadAsync = Assert.Single(nodes, node => node.Id.StartsWith("Lib.Store.LoadAsync(", StringComparison.Ordinal));
        var relayAsync = Assert.Single(nodes, node => node.Id.StartsWith("Lib.Store.RelayAsync(", StringComparison.Ordinal));
        // RelayAsync is called by nothing, so only its own first sequence point places it.
        Assert.Equal((11, 9), (loadAsync.LineNumber, loadAsync.ColumnNumber));
        Assert.Equal((17, 9), (relayAsync.LineNumber, relayAsync.ColumnNumber));
        var edges = slice.CallGraph.Edges;
        Assert.Contains(edges, edge => edge.SourceId == loadAsync.Id && edge.TargetId.StartsWith("System.IO.File.ReadAllText(", StringComparison.Ordinal) && edge.CallLocation.LineNumber == 12);
        Assert.Contains(edges, edge => edge.SourceId == relayAsync.Id && edge.TargetId == loadAsync.Id && edge.CallLocation.LineNumber == 17);
        Assert.Contains(edges, edge => edge.SourceId == relayAsync.Id && edge.TargetId.StartsWith("System.Runtime.CompilerServices.AsyncHelpers.Await", StringComparison.Ordinal));
        Assert.All(edges.Where(edge => edge.SourceId == loadAsync.Id || edge.SourceId == relayAsync.Id), edge => Assert.Equal(AnalysisEvidenceKind.AssemblyIlDirect, edge.EvidenceKind));
    }

    [Fact]
    public void DataFlows_RuntimeAsyncParameter_ReachesTheFileSinkAcrossAwaits()
    {
        using var fixture = new TempDir();
        Emit(fixture.Path);

        var result = DataFlowAnalyzer.Analyze(fixture.Path);

        var sources = result.Slices
            .Where(slice => slice.Summary?.StartsWith("Assembly IL", StringComparison.Ordinal) == true)
            .Select(slice => (Slice: slice, Source: result.Nodes.Single(node => node.Id == slice.SourceId)))
            .Where(pair => pair.Source.Name == "request")
            .ToList();
        var load = Assert.Single(sources.Where(pair => pair.Source.MethodName == "LoadAsync").DistinctBy(pair => pair.Source.Id));
        // Placed at the method's own first sequence point, in its source file.
        Assert.Equal(("Store.cs", 11), (load.Source.FileName, load.Source.LineNumber));
        Assert.Contains(sources, pair => pair.Source.MethodName == "LoadAsync" && result.Nodes.Single(node => node.Id == pair.Slice.SinkId).Name?.Contains("ReadAllText", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(result.Nodes, node => node.ClassName?.Contains("d__", StringComparison.Ordinal) == true);
    }

    private static string Emit(string outputDirectory)
    {
        var references = FrameworkReferences.Current.References.Select(entry => (MetadataReference)entry.Reference).ToList();
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview)
            .WithFeatures([new KeyValuePair<string, string>("runtime-async", "on")]);
        var compilation = CSharpCompilation.Create("Lib",
            [CSharpSyntaxTree.ParseText(SourceText.From(StoreSource, Encoding.UTF8), parseOptions, path: "/_/Lib/Store.cs")],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));
        var dllPath = Path.Combine(outputDirectory, "Lib.dll");
        using var dll = File.Create(dllPath);
        using var pdb = File.Create(Path.ChangeExtension(dllPath, ".pdb"));
        var emit = compilation.Emit(dll, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));
        return dllPath;
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir() => Directory.CreateDirectory(Path);

        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dosai-runtime-async-" + Guid.NewGuid().ToString("N"));

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
