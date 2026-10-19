using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Depscan;
using Xunit;

namespace Dosai.Tests;

// The parallel writer must produce exactly what one serializer call does. Same xunit collection
// as the rest of DosaiTests: these flip the static worker knob.
public partial class DosaiTests
{
    private static JsonSerializerOptions SliceJsonOptions() => new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private static byte[] WriteWithParallelWriter<T>(T value, JsonSerializerOptions options)
    {
        using var stream = new MemoryStream();
        ParallelJsonWriter.Serialize(stream, value, options);
        return stream.ToArray();
    }

    [Fact]
    public void ParallelJsonWriter_IsByteIdenticalToTheSerializerForEveryWorkerCount()
    {
        // Lists on both sides of the parallel threshold and of a chunk boundary, at the root and
        // one object down (CallGraph), null properties left out, and strings the relaxed encoder
        // still escapes.
        var rng = new Random(71);
        string Text(int i) => (i % 5) switch
        {
            0 => $"Ns.T{i}.M(\"q\\u0001\")",
            1 => $"Ns.Ünïcødé{i}<T>.M()",
            2 => $"line\n{i}\ttab",
            _ => $"Ns.T{i}.M()"
        };
        var nodes = Enumerable.Range(0, 5000).Select(i => new MethodNode { Id = Text(i), Name = $"M{i}", ClassName = "T", Namespace = "Ns", FileName = i % 3 == 0 ? "/src/T.cs" : "lib.dll", Purl = i % 4 == 0 ? null : $"pkg:nuget/P{i % 9}" }).ToList();
        var slice = new MethodsSlice
        {
            Metadata = new AnalysisMetadata { InputPath = "/src", TargetFrameworks = ["net11.0"] },
            Dependencies = [],
            Methods = Enumerable.Range(0, 4097).Select(i => new Method { Name = Text(i), LineNumber = i, Parameters = i % 2 == 0 ? null : [new Parameter { Name = "x", Type = "int" }] }).ToList(),
            MethodCalls = Enumerable.Range(0, 9001).Select(i => new MethodCalls { CalledMethod = Text(i), CallType = (CallType)(i % 3), Arguments = i % 7 == 0 ? null : [$"a{i}"] }).ToList(),
            CallGraph = new CallGraph
            {
                Nodes = nodes,
                Edges = Enumerable.Range(0, 8192).Select(i => new MethodCallEdge { SourceId = nodes[i % nodes.Count].Id, TargetId = nodes[rng.Next(nodes.Count)].Id, CallLocation = new CallLocation { FileName = "/src/T.cs", LineNumber = i } }).ToList()
            },
            EntryPoints = [new EntryPoint { Id = "ep0", Kind = "Http", MethodId = nodes[0].Id }],
            Reachability = nodes.Select(node => new NodeReachability { NodeId = node.Id, ReachableNodeBucket = 10, DepthFromEntryPoint = node.Purl is null ? null : 1 }).ToList(),
            Diagnostics = ["a \"quoted\" note", "<html> & 'apostrophes'"]
        };

        var expected = JsonSerializer.SerializeToUtf8Bytes(slice, SliceJsonOptions());
        foreach (var workers in new[] { 1, 4, 16 })
        {
            var actual = WithSymbolAnalysisWorkers(workers, () => WriteWithParallelWriter(slice, SliceJsonOptions()));
            Assert.True(expected.AsSpan().SequenceEqual(actual), $"{workers} workers: output differs from JsonSerializer");
        }
    }

    [Fact]
    public void ParallelJsonWriter_FilteredMethodsSectionsContract_IsByteIdenticalToTheSerializerForEveryWorkerCount()
    {
        // `--sections` filters the MethodsSlice contract by removing properties, which must keep
        // the parallel fast path (a ShouldSerialize predicate would not), and Metadata carries a
        // set OmittedSections, whose property-level ignore condition makes AnalysisMetadata a
        // non-plain object the writer sends through the serializer whole. Both shapes must still
        // match the serializer byte for byte, with lists above the threshold in the kept sections.
        var rng = new Random(72);
        string Text(int i) => $"Ns.T{i}.M(\"q\\u0001\")";
        var nodes = Enumerable.Range(0, 5000).Select(i => new MethodNode { Id = Text(i), Name = $"M{i}", ClassName = "T", Namespace = "Ns", FileName = "/src/T.cs" }).ToList();
        var slice = new MethodsSlice
        {
            Metadata = new AnalysisMetadata { InputPath = "/src" },
            Methods = Enumerable.Range(0, 4097).Select(i => new Method { Name = Text(i), LineNumber = i }).ToList(),
            MethodCalls = Enumerable.Range(0, 5000).Select(i => new MethodCalls { CalledMethod = Text(i), CallType = CallType.MethodCall, Arguments = [$"a{i}"] }).ToList(),
            CallGraph = new CallGraph
            {
                Nodes = nodes,
                Edges = Enumerable.Range(0, 8192).Select(i => new MethodCallEdge { SourceId = nodes[i % nodes.Count].Id, TargetId = nodes[rng.Next(nodes.Count)].Id, CallLocation = new CallLocation { FileName = "/src/T.cs", LineNumber = i } }).ToList()
            },
            Diagnostics = ["a \"quoted\" note"]
        };

        var sections = MethodsSections.Parse("metadata,callgraph");
        var filtered = sections.FilterOptions(SliceJsonOptions());
        // The filtered root is still a plain contract, so its lists keep the parallel path.
        filtered.MakeReadOnly(populateMissingResolver: true);
        Assert.True(ParallelJsonWriter.Supports(filtered));
        Assert.True(ParallelJsonWriter.IsPlainObject(filtered.GetTypeInfo(typeof(MethodsSlice))));
        Assert.True(ParallelJsonWriter.IsPlainObject(filtered.GetTypeInfo(typeof(CallGraph))));
        slice.Metadata.OmittedSections = [.. sections.OmittedSections()];

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(slice, filtered));
        Assert.Equal(["Metadata", "CallGraph"], document.RootElement.EnumerateObject().Select(property => property.Name).ToList());
        Assert.Contains("Methods", document.RootElement.GetProperty("Metadata").GetProperty("OmittedSections").EnumerateArray().Select(value => value.GetString()));

        var expected = JsonSerializer.SerializeToUtf8Bytes(slice, filtered);
        foreach (var workers in new[] { 1, 4, 16 })
        {
            var actual = WithSymbolAnalysisWorkers(workers, () => WriteWithParallelWriter(slice, filtered));
            Assert.True(expected.AsSpan().SequenceEqual(actual), $"{workers} workers: filtered output differs from JsonSerializer");
        }

        // Write names the omission only while it writes: the caller's slice comes back without it.
        var written = string.Empty;
        slice.Metadata.OmittedSections = null;
        sections.Write(slice, SliceJsonOptions(), options => written = JsonSerializer.Serialize(slice, options));
        Assert.Contains("\"OmittedSections\":[", written, StringComparison.Ordinal);
        Assert.Null(slice.Metadata.OmittedSections);

        // The unfiltered contract skips a null OmittedSections exactly like the serializer.
        var expectedFull = JsonSerializer.SerializeToUtf8Bytes(slice, SliceJsonOptions());
        Assert.DoesNotContain("OmittedSections"u8, expectedFull);
        Assert.True(expectedFull.AsSpan().SequenceEqual(WriteWithParallelWriter(slice, SliceJsonOptions())), "unfiltered output differs from JsonSerializer");
    }

    private sealed class ExplicitOrder
    {
        public string First { get; set; } = "first";

        [JsonPropertyOrder(-1)]
        public List<int> Ordered { get; set; } = [.. Enumerable.Range(0, 5000)];
    }

    private sealed class ConditionalIgnore
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int DefaultSkipped { get; set; }

        [JsonPropertyName("renamed")]
        public List<int> Named { get; set; } = [.. Enumerable.Range(0, 5000)];
    }

    private sealed class NestedIgnore
    {
        public Inner Value { get; set; } = new();

        public sealed class Inner
        {
            [JsonIgnore]
            public string Hidden { get; set; } = "hidden";

            public List<string> Items { get; set; } = [.. Enumerable.Range(0, 6000).Select(i => $"i{i}")];
        }
    }

    [Fact]
    public void ParallelJsonWriter_LeavesContractsItCannotReproduceToTheSerializer()
    {
        // An explicit order, a conditional ignore beside a rename, a plain ignore one level down,
        // and indented options: each either reproduces exactly or falls back whole.
        var options = SliceJsonOptions();
        var indented = new JsonSerializerOptions(SliceJsonOptions()) { WriteIndented = true };
        AssertWritesLikeTheSerializer(new ExplicitOrder(), options);
        AssertWritesLikeTheSerializer(new ConditionalIgnore(), options);
        AssertWritesLikeTheSerializer(new NestedIgnore(), options);
        AssertWritesLikeTheSerializer(new ExplicitOrder(), indented);
    }

    private static void AssertWritesLikeTheSerializer<T>(T value, JsonSerializerOptions options)
        => Assert.Equal(JsonSerializer.Serialize(value, options), System.Text.Encoding.UTF8.GetString(WriteWithParallelWriter(value, options)));
}
