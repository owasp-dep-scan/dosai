using System.Text;
using Depscan;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Dosai.Tests;

// Issue #84: an async or iterator method is a stub that starts its state machine and has no
// sequence point of its own, so the IL pass placed its parameter sources (added at the method
// entry) at the assembly file, line 1. The PDB names each MoveNext's kickoff method; the stub now
// resolves to MoveNext's first point, as the call graph's state-machine owner node does too.
public sealed class Issue84AsyncParameterSourceTests
{
    private const string StoreSource = """
        using System;
        using System.Collections.Generic;
        using System.IO;
        using System.Threading.Tasks;

        namespace Lib;

        public static class Store
        {
            public static string Load(string request)
            {
                return File.ReadAllText(request);
            }

            public static async Task<string> LoadAsync(string request)
            {
                await Task.Yield();
                return File.ReadAllText(request);
            }

            public static IEnumerable<string> Lines(string request)
            {
                yield return File.ReadAllText(request);
            }

            public static Func<string, Task<string>> Loader = async request =>
            {
                await Task.Yield();
                return File.ReadAllText(request);
            };
        }
        """;

    // Debug builds open MoveNext at the method's `{`; Release builds at its first statement.
    [Theory]
    [InlineData(OptimizationLevel.Debug, 16, 5, 27, 5)]
    [InlineData(OptimizationLevel.Release, 17, 9, 28, 9)]
    public void DataFlows_ParameterSourceOfAStateMachineMethod_IsPlacedInItsSource(OptimizationLevel optimization, int asyncLine, int asyncColumn, int lambdaLine, int lambdaColumn)
    {
        using var fixture = new TempDir();
        Emit(fixture.Path, optimization);

        var result = DataFlowAnalyzer.Analyze(fixture.Path);

        var sources = result.Slices
            .Where(slice => slice.Summary?.StartsWith("Assembly IL", StringComparison.Ordinal) == true)
            .Select(slice => result.Nodes.Single(node => node.Id == slice.SourceId))
            .Where(node => node.Name == "request")
            .ToList();
        Assert.All(sources, source => Assert.Equal(("Store.cs", "Store.cs"), (source.Path, source.FileName)));
        AssertSource("Load", 12, 9);
        AssertSource("LoadAsync", asyncLine, asyncColumn);

        Assert.Contains(sources, source => source.ClassName?.Contains("<>c", StringComparison.Ordinal) == true && (source.LineNumber, source.ColumnNumber) == (lambdaLine, lambdaColumn));

        void AssertSource(string method, int line, int column)
        {
            var source = Assert.Single(sources.Where(source => source.MethodName == method).DistinctBy(source => source.Id));
            Assert.Equal((line, column), (source.LineNumber, source.ColumnNumber));
        }
    }

    [Fact]
    public void Methods_StateMachineOwnerNode_IsPlacedInItsSource()
    {
        using var fixture = new TempDir();
        Emit(fixture.Path, OptimizationLevel.Debug);

        var slice = Depscan.Dosai.GetMethodsSlice(fixture.Path);

        // The node MoveNext's calls are attributed to is the async or iterator method itself.
        foreach (var (method, line, readLine) in new[] { ("Lib.Store.LoadAsync(string)", 16, 18), ("Lib.Store.Lines(string)", 22, 23) })
        {
            var owner = Assert.Single(slice.CallGraph!.Nodes, node => node.Id.StartsWith(method, StringComparison.Ordinal));
            Assert.Equal((line, 5), (owner.LineNumber, owner.ColumnNumber));
            Assert.Contains(slice.CallGraph.Edges, edge => edge.SourceId == owner.Id && edge.TargetId.Contains("ReadAllText", StringComparison.Ordinal) && edge.CallLocation.LineNumber == readLine);
        }
    }

    private static void Emit(string outputDirectory, OptimizationLevel optimization)
    {
        var references = FrameworkReferences.Current.References.Select(entry => (MetadataReference)entry.Reference).ToList();
        var compilation = CSharpCompilation.Create("Lib",
            [CSharpSyntaxTree.ParseText(SourceText.From(StoreSource, Encoding.UTF8), path: "/_/Lib/Store.cs")],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: optimization));
        var dllPath = Path.Combine(outputDirectory, "Lib.dll");
        using var dll = File.Create(dllPath);
        using var pdb = File.Create(Path.ChangeExtension(dllPath, ".pdb"));
        var emit = compilation.Emit(dll, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir() => Directory.CreateDirectory(Path);

        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dosai-issue84-" + Guid.NewGuid().ToString("N"));

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
