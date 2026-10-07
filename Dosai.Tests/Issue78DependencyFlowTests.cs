using Depscan;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Dosai.Tests;

// Issue #78: the IL interpreters model the stack effect of the opcodes they do not handle
// explicitly. A generic `stelem` and `calli` popped nothing, so the abstract stack grew on every
// pass round a loop and the method ran into its state budget; the summary interpreter now also
// models array stores, which the leaked stack entries had imitated by accident.
public sealed class Issue78DependencyFlowTests
{
    [Fact]
    public void Summaries_GenericArrayStores_DoNotDriftTheStackOrHitTheBudget()
    {
        // A generic `stelem` pops three values. Modelled as popping none, every pass round the
        // copy loop grew the abstract stack, each pass looked like a new state, and the method
        // ran into the 10000-state budget with a summary that only came out right by accident.
        using var fixture = new TempDir();
        var dll = Emit(fixture.Path, "Arrays", """
            namespace Fixture;

            public static class Arrays
            {
                public static T[] Copy<T>(T[] source)
                {
                    var result = new T[source.Length];
                    for (var index = 0; index < source.Length; index++)
                    {
                        result[index] = source[index];
                    }
                    return result;
                }

                public static T[] Wrap<T>(T value) => new[] { value };

                public static void Fill<T>(T[] target, T value)
                {
                    for (var index = 0; index < target.Length; index++)
                    {
                        target[index] = value;
                    }
                }

                public static string[] Pair(string first, string second)
                {
                    var pair = new string[2];
                    pair[0] = first;
                    pair[1] = second;
                    return pair;
                }
            }
            """);

        var result = DataFlowAnalyzer.Analyze(dll);

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Contains("state budget", StringComparison.Ordinal));
        Assert.Equal([0], ReturnIndexes(result, "Fixture.Arrays.Copy("));
        Assert.Equal([0], ReturnIndexes(result, "Fixture.Arrays.Wrap("));
        Assert.Equal([0, 1], ReturnIndexes(result, "Fixture.Arrays.Pair("));
        // A void method has no return: the old drift left a stored value for `ret` to "return".
        Assert.DoesNotContain(result.MethodSummaries, summary => summary.Method.StartsWith("Fixture.Arrays.Fill(", StringComparison.Ordinal) && summary.ReturnParameterIndexes.Count > 0);
    }

    [Fact]
    public void Summaries_FunctionPointerCalls_PopTheirSignature()
    {
        // `calli` pops the call-site signature's arguments and the function pointer; modelled as
        // popping nothing, a loop of calls drifted the stack into the state budget.
        using var fixture = new TempDir();
        var dll = Emit(fixture.Path, "Pointers", """
            namespace Fixture;

            public static unsafe class Pointers
            {
                private static void Consume(string value) { }

                private static string Echo(string value) => value;

                public static string Loop(string value, int count)
                {
                    delegate*<string, void> consume = &Consume;
                    delegate*<string, string> echo = &Echo;
                    var current = value;
                    for (var index = 0; index < count; index++)
                    {
                        consume(current);
                        current = echo(current);
                    }
                    return value;
                }
            }
            """, allowUnsafe: true);

        var result = DataFlowAnalyzer.Analyze(dll);

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Contains("state budget", StringComparison.Ordinal));
        Assert.Equal([0], ReturnIndexes(result, "Fixture.Pointers.Loop("));
    }

    private static List<int> ReturnIndexes(DataFlowResult result, string methodPrefix) =>
        Assert.Single(result.MethodSummaries, summary => summary.SummaryKind == "AssemblyIL" && summary.Method.StartsWith(methodPrefix, StringComparison.Ordinal))
            .ReturnParameterIndexes.Order().ToList();

    private static string Emit(string outputDirectory, string assemblyName, string source, string? references = null, bool allowUnsafe = false)
    {
        var metadata = FrameworkReferences.Current.References.Select(entry => (MetadataReference)entry.Reference).ToList();
        if (references is not null) metadata.Add(MetadataReference.CreateFromFile(references));
        var compilation = CSharpCompilation.Create(assemblyName,
            [CSharpSyntaxTree.ParseText(source, path: $"{assemblyName}.cs")],
            metadata,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: allowUnsafe, nullableContextOptions: NullableContextOptions.Enable));
        var dllPath = Path.Combine(outputDirectory, $"{assemblyName}.dll");
        var emit = compilation.Emit(dllPath);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));
        return dllPath;
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir() => Directory.CreateDirectory(Path);

        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dosai-issue78-" + Guid.NewGuid().ToString("N"));

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
