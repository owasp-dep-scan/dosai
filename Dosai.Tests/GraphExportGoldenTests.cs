using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Xunit;

namespace Depscan.Tests;

/// <summary>
///     Issue #75 review: the first streaming implementation of the graph exporters compared the
///     new code with itself (the string overloads had become wrappers over the stream writers),
///     and a dropped closing quote in the data-flow Mermaid labels survived that comparison. The
///     references here are independent of the current code: a verbatim copy of the 7292446
///     exporter implementations (<see cref="GraphExportReference" />) for shapes real analysis
///     never emits, bytes produced by the 7292446 build for the committed fixture, and for the
///     crypto dosai format the pre-#75 serializer call itself.
/// </summary>
public class GraphExportGoldenTests
{
    private static string GoldensRoot => Path.Combine(AppContext.BaseDirectory, "Goldens");

    private static string FixturePath => Path.Combine(GoldensRoot, "graph-labels-fixture");

    private static string GoldenPath(string name) => Path.Combine(GoldensRoot, "graph-exports", name);

    [Fact]
    public void GraphExporters_ProduceTheFrozenMainBytes()
    {
        // Labels carry every escape-relevant character: quotes, apostrophe, <>&, |, [](), {},
        // #, ;, real newlines and tabs, non-ASCII, emoji, and an empty string. The graph also
        // has a dangling edge endpoint (analysis never emits one, but both exporters branch on
        // it), an isolated node, and reachability facts with and without a depth.
        using var tempDirectory = new TemporaryDirectory();
        var nasty = "argv\" 'quoted' <tag> & amp | pipe [br] (pa) {br} #hash;semi\ttab\nnl é😀 \"\" end";
        var dataFlowResult = new DataFlowResult
        {
            Nodes =
            [
                new DataFlowNode { Id = "n0", Kind = "parameter", Name = "isolated", LineNumber = 3 },
                new DataFlowNode { Id = "n1", Kind = "parameter", Name = nasty, IsSource = true, Category = "cli", Code = nasty + "\n\t" + nasty, Symbol = "System.Diagnostics.Process.Start(" + nasty + ")", LineNumber = 4 },
                new DataFlowNode { Id = "n2", Kind = "argument", Name = string.Empty, IsSink = true, Category = "command", Symbol = nasty, LineNumber = 5 },
                new DataFlowNode { Id = "n3", Kind = "local", Name = "日本語ローカル", Code = nasty, LineNumber = 6 }
            ],
            Edges =
            [
                new DataFlowEdge { Id = "e1", SourceId = "n1", TargetId = "n2", Kind = "argument", Label = nasty, LineNumber = 5 },
                new DataFlowEdge { Id = "e2", SourceId = "n1", TargetId = "missing-node", Kind = nasty, Label = "dangling endpoint", LineNumber = 7 }
            ],
            Slices = [new DataFlowSlice { Id = "s1", SourceId = "n1", SinkId = "n2", NodeIds = ["n1", "n2", "n3"], EdgeIds = ["e1"], SinkArgument = nasty }]
        };
        foreach (var format in new[] { DataFlowExportFormat.Mermaid, DataFlowExportFormat.GraphMl, DataFlowExportFormat.Gexf })
        {
            var referencePath = Path.Combine(tempDirectory.Path, $"df-reference{DataFlowExporter.GetDefaultExtension(format)}");
            var writerPath = Path.Combine(tempDirectory.Path, $"df-writer{DataFlowExporter.GetDefaultExtension(format)}");
            File.WriteAllText(referencePath, DataFlowExporterReference.Export(dataFlowResult, format));
            using (var writer = new StreamWriter(writerPath))
            {
                DataFlowExporter.Export(writer, dataFlowResult, format);
            }

            Assert.Equal(File.ReadAllBytes(referencePath), File.ReadAllBytes(writerPath));
        }

        var callGraph = new CallGraph
        {
            Nodes =
            [
                new MethodNode { Id = "Ünïcödé.日本語.A.M():void", Kind = "method", Name = "M", Label = "Ünïcödé.日本語.A.M():void", ClassName = "A", Namespace = "Ünïcödé.日本語", FileName = nasty + ".cs" },
                new MethodNode { Id = "B.N():void", Kind = "method", Name = "N", ClassName = "B", Namespace = "", FileName = "B.cs", Purl = "pkg:nuget/B@1.0.0" }
            ],
            Edges =
            [
                new MethodCallEdge { SourceId = "Ünïcödé.日本語.A.M():void", TargetId = "B.N():void", CallType = CallType.MethodCall, CallSiteCount = 2, CallLocation = new CallLocation { FileName = nasty, LineNumber = 4, ColumnNumber = 3 } },
                new MethodCallEdge { SourceId = "Ünïcödé.日本語.A.M():void", TargetId = "missing-node", CallType = CallType.ConstructorCall, CallLocation = new CallLocation { LineNumber = 9 } }
            ]
        };
        var reachability = new Dictionary<string, NodeReachability>(StringComparer.Ordinal)
        {
            ["Ünïcödé.日本語.A.M():void"] = new NodeReachability { NodeId = "Ünïcödé.日本語.A.M():void", ReachableEntryPoints = ["ep1"], FanIn = 0, FanOut = 2 },
            ["B.N():void"] = new NodeReachability { NodeId = "B.N():void", ReachableEntryPoints = ["ep1", "ep2"], DepthFromEntryPoint = 1, FanIn = 1, FanOut = 0, InRecursiveCycle = true }
        };
        foreach (var format in new[] { CallGraphExportFormat.Mermaid, CallGraphExportFormat.GraphMl, CallGraphExportFormat.Gexf })
        {
            var referencePath = Path.Combine(tempDirectory.Path, $"cg-reference{CallGraphExporter.GetDefaultExtension(format)}");
            var writerPath = Path.Combine(tempDirectory.Path, $"cg-writer{CallGraphExporter.GetDefaultExtension(format)}");
            File.WriteAllText(referencePath, CallGraphExporterReference.Export(callGraph, format, reachability));
            using (var writer = new StreamWriter(writerPath))
            {
                CallGraphExporter.Export(writer, callGraph, format, reachability);
            }

            Assert.Equal(File.ReadAllBytes(referencePath), File.ReadAllBytes(writerPath));
        }
    }

    [Fact]
    public void CliGraphOutputs_MatchMainGoldens()
    {
        // The goldens are the bytes the 7292446 build wrote for the committed fixture (see
        // Goldens/README.md). The graph files carry no timestamps, so the comparison is exact;
        // the CycloneDX document's serialNumber and metadata.timestamp are per-run values and
        // are masked. The fixture is copied to a temporary directory first: discovery excludes
        // bin/ trees, and the committed copy reaches the tests inside the output directory.
        using var fixture = new TemporaryDirectory();
        foreach (var source in Directory.GetFiles(FixturePath))
        {
            File.Copy(source, Path.Combine(fixture.Path, Path.GetFileName(source)));
        }

        using var output = new TemporaryDirectory();
        RunCliCommands(fixture.Path, output.Path);
        foreach (var expected in new[]
                 {
                     "dataflows.mermaid", "dataflows.graphml", "dataflows.gexf",
                     "callgraph.mermaid", "callgraph.graphml", "callgraph.gexf"
                 })
        {
            Assert.True(File.Exists(GoldenPath(expected)), $"missing golden {expected}");
            Assert.True(File.Exists(Path.Combine(output.Path, expected)), $"CLI did not write {expected}");
            Assert.Equal(File.ReadAllBytes(GoldenPath(expected)), File.ReadAllBytes(Path.Combine(output.Path, expected)));
        }

        Assert.Equal(
            MaskRunIdentity(File.ReadAllText(GoldenPath("crypto-cyclonedx.json"))),
            MaskRunIdentity(File.ReadAllText(Path.Combine(output.Path, "crypto-cyclonedx.json"))));
    }

    private static void RunCliCommands(string fixture, string output)
    {
        string Out(string name) => Path.Combine(output, name);
        Assert.Equal(0, CommandLine.Main(["dataflows", "--path", fixture, "--o", Out("dataflows.json"), "--graph-format", "mermaid", "--graph-out", Out("dataflows.mermaid")]));
        Assert.Equal(0, CommandLine.Main(["dataflows", "--path", fixture, "--o", Out("dataflows2.json"), "--graph-format", "graphml", "--graph-out", Out("dataflows.graphml")]));
        Assert.Equal(0, CommandLine.Main(["dataflows", "--path", fixture, "--o", Out("dataflows3.json"), "--graph-format", "gexf", "--graph-out", Out("dataflows.gexf")]));
        Assert.Equal(0, CommandLine.Main(["methods", "--path", fixture, "--o", Out("methods.json"), "--callgraph-format", "mermaid", "--callgraph-out", Out("callgraph.mermaid")]));
        Assert.Equal(0, CommandLine.Main(["methods", "--path", fixture, "--o", Out("methods2.json"), "--callgraph-format", "graphml", "--callgraph-out", Out("callgraph.graphml")]));
        Assert.Equal(0, CommandLine.Main(["methods", "--path", fixture, "--o", Out("methods3.json"), "--callgraph-format", "gexf", "--callgraph-out", Out("callgraph.gexf")]));
        Assert.Equal(0, CommandLine.Main(["crypto", "--path", fixture, "--o", Out("crypto-cyclonedx.json"), "--format", "cyclonedx"]));
    }

    [Fact]
    public void CryptoExport_StreamPathIsByteIdenticalToThePre75Serializer()
    {
        // Pre-#75, the dosai format was exactly JsonSerializer.Serialize(result, JsonOptions)
        // with CryptoBomExporter's options, and the CLI wrote that string with
        // File.WriteAllText. Those options are rebuilt here and the serializer called directly,
        // so the comparison cannot be satisfied by the new code agreeing with itself.
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "StreamIdentity.cs"), """
using System.Security.Cryptography;
using System.Text;

class StreamIdentity
{
    const string ApiKey = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";
    static void Main(string[] args) => Hash(ApiKey + args[0]);
    static byte[] Hash(string secret)
    {
        using var md5 = MD5.Create();
        return md5.ComputeHash(Encoding.UTF8.GetBytes(secret));
    }
}
""");

        var result = CryptoAnalyzer.Analyze(tempDirectory.Path);
        Assert.NotNull(result.CryptoDataFlows);
        Assert.True(result.Statistics.CryptoDataFlowSliceCount >= 1);

        var pre75Options = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };
        var streamPath = Path.Combine(tempDirectory.Path, "stream-dosai.json");
        using (var stream = new FileStream(streamPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 65536))
        {
            CryptoAnalyzer.Export(stream, result, "dosai");
        }

        Assert.Equal(JsonSerializer.Serialize(result, pre75Options), File.ReadAllText(streamPath));
    }

    private static string MaskRunIdentity(string text)
    {
        // serialNumber and metadata.timestamp are per-run values; dosai:inputPath is the scan
        // path, which necessarily differs between the golden run (a repo-relative fixture) and
        // any test run (a temporary copy of it).
        text = Regex.Replace(text, "\"(serialNumber|timestamp)\"\\s*:\\s*\"[^\"]*\"", "\"$1\":\"<masked>\"");
        text = Regex.Replace(text, "(\"name\":\\s*\"dosai:inputPath\",\\s*\"value\":\\s*\")[^\"]*", "$1<masked>");
        return Regex.Replace(text, "urn:uuid:[0-9a-fA-F-]{36}", "urn:uuid:<masked>");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dosai-goldens-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
