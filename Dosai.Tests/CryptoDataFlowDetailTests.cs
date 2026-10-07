using System.Collections;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace Depscan.Tests;

/// <summary>
///     Issue #75 review findings: `slices` detail used to leave dangling node/edge ids in the
///     derived collections (PackageReachability pointed at thousands of trimmed ids), and
///     `--crypto-dataflows` combined with `--graph-format` either failed after doing all the
///     work (`none`) or silently trimmed the sidecar (`slices`). These tests pin the contract:
///     every graph-id reference in a trimmed result resolves, and sidecars always carry the
///     full graph.
/// </summary>
public class CryptoDataFlowDetailTests
{
    [Fact]
    public void SlicesDetail_PrunesEveryDanglingGraphReference()
    {
        var dataFlows = new DataFlowResult
        {
            Nodes =
            [
                new DataFlowNode { Id = "n-on-1", Kind = "parameter", Name = "args", IsSource = true },
                new DataFlowNode { Id = "n-on-2", Kind = "argument", Name = "Process.Start", IsSink = true },
                new DataFlowNode { Id = "n-off", Kind = "local", Name = "unreferenced" },
                new DataFlowNode { Id = "n-isolated", Kind = "local", Name = "isolated", IsSource = true }
            ],
            Edges =
            [
                new DataFlowEdge { Id = "e-on", SourceId = "n-on-1", TargetId = "n-on-2", Kind = "argument" },
                new DataFlowEdge { Id = "e-off", SourceId = "n-on-1", TargetId = "n-off", Kind = "argument" }
            ],
            Slices = [new DataFlowSlice { Id = "s1", SourceId = "n-on-1", SinkId = "n-on-2", NodeIds = ["n-on-1", "n-on-2"], EdgeIds = ["e-on"] }],
            PackageReachability =
            [
                new PackageReachability { Purl = "pkg:nuget/Kept@1.0.0", NodeIds = ["n-on-1", "n-off"], EdgeIds = ["e-off", "e-on"], SliceIds = ["s1"] },
                new PackageReachability { Purl = "pkg:nuet/Dropped@1.0.0", NodeIds = ["n-off"], EdgeIds = ["e-off"] },
                new PackageReachability { Purl = "pkg:nuet/SliceEvidenceOnly@1.0.0", SliceIds = ["s1"] }
            ],
            DangerousApiReachability =
            [
                new DangerousApiReachability { Id = "da-kept", Category = "command", NodeIds = ["n-on-2"] },
                new DangerousApiReachability { Id = "da-dropped", Category = "command", NodeIds = ["n-off"] }
            ],
            WeaknessCandidates = [new WeaknessCandidate { Id = "wc1", Kind = "TaintFlow", SourceId = "n-off", SinkId = "n-on-2", SliceId = "s1" }],
            ExploitChains = [new ExploitChain { Id = "ec1", EntryPointId = "ep1", Exposure = "cli", WeaknessId = "wc1", SliceId = "s1", SourceNodeId = "n-off", SinkNodeId = "n-on-2", CallPath = ["A.M():void"] }],
            SanitizedFlows = [new SanitizedFlow { Id = "sf1", Kind = "SanitizerMatch", SourceIds = ["n-on-1", "n-off"], Expression = "args[0]" }],
            EntryPoints = [new EntryPoint { Id = "ep1", Kind = "Cli", MethodId = "A.M():void" }],
            MethodSummaries = [new DataFlowMethodSummary { Method = "A.M():void" }],
            Statistics = new DataFlowStatistics { NodeCount = 4, EdgeCount = 2, SourceCount = 2, SinkCount = 1 }
        };
        var result = new CryptoAnalysisResult { CryptoDataFlows = dataFlows };

        CryptoAnalyzer.ApplyDataFlowDetail(result, CryptoDataFlowDetail.Slices);

        var trimmed = result.CryptoDataFlows!;
        // Exactly the slice-referenced nodes and edges survive, in result order.
        Assert.Equal(["n-on-1", "n-on-2"], trimmed.Nodes.Select(node => node.Id).ToList());
        Assert.Equal(["e-on"], trimmed.Edges.Select(edge => edge.Id).ToList());
        Assert.Equal(2, trimmed.Statistics.NodeCount);
        Assert.Equal(1, trimmed.Statistics.EdgeCount);
        Assert.Equal(1, trimmed.Statistics.SourceCount);
        Assert.Equal(1, trimmed.Statistics.SinkCount);
        // Package reachability keeps entries with surviving evidence, pruned to the retained
        // graph, and drops entries whose id evidence was fully trimmed.
        Assert.Equal(["pkg:nuget/Kept@1.0.0", "pkg:nuet/SliceEvidenceOnly@1.0.0"], trimmed.PackageReachability.Select(package => package.Purl).ToList());
        Assert.Equal(["n-on-1"], trimmed.PackageReachability[0].NodeIds);
        Assert.Equal(["e-on"], trimmed.PackageReachability[0].EdgeIds);
        Assert.Equal(["s1"], trimmed.PackageReachability[1].SliceIds);
        // Dangerous APIs follow the same rule; weaknesses and chains lose trimmed node refs but
        // keep the finding (slice, weakness and entry-point ids are never pruned).
        Assert.Equal(["da-kept"], trimmed.DangerousApiReachability.Select(api => api.Id).ToList());
        Assert.Null(trimmed.WeaknessCandidates[0].SourceId);
        Assert.Equal("n-on-2", trimmed.WeaknessCandidates[0].SinkId);
        Assert.Equal("s1", trimmed.WeaknessCandidates[0].SliceId);
        Assert.Null(trimmed.ExploitChains[0].SourceNodeId);
        Assert.Equal("n-on-2", trimmed.ExploitChains[0].SinkNodeId);
        Assert.Equal(["n-on-1"], trimmed.SanitizedFlows[0].SourceIds);
        Assert.Equal(["A.M():void"], trimmed.ExploitChains[0].CallPath);

        AssertEveryGraphReferenceResolves(result);
    }

    [Fact]
    public void SlicesDetail_OnAnalyzedTree_KeepsEveryReferenceResolvable()
    {
        using var tempDirectory = new TemporaryDirectory();
        WriteDetailFlowFixture(tempDirectory.Path);

        var full = CryptoAnalyzer.Analyze(tempDirectory.Path);
        Assert.NotNull(full.CryptoDataFlows);
        Assert.True(full.CryptoDataFlows!.Slices.Count >= 1);
        // The untrimmed result must already be referentially complete, otherwise the walk below
        // would be comparing the trim against a broken baseline.
        AssertEveryGraphReferenceResolves(full);

        var slices = CryptoAnalyzer.Analyze(tempDirectory.Path, BuildPreparationMode.None, CryptoDataFlowDetail.Slices);
        Assert.NotNull(slices.CryptoDataFlows);
        Assert.All(slices.Operations.SelectMany(operation => operation.DataFlowSliceIds), sliceId => Assert.Contains(sliceId, slices.CryptoDataFlows!.Slices.Select(slice => slice.Id)));
        AssertEveryGraphReferenceResolves(slices);

        var none = CryptoAnalyzer.Analyze(tempDirectory.Path, BuildPreparationMode.None, CryptoDataFlowDetail.None);
        Assert.Null(none.CryptoDataFlows);
        Assert.Equal(full.Statistics.CryptoDataFlowSliceCount, none.Statistics.CryptoDataFlowSliceCount);
    }

    [Fact]
    public void CryptoCli_GraphSidecarsAlwaysCarryTheFullGraph()
    {
        using var tempDirectory = new TemporaryDirectory();
        WriteDetailFlowFixture(tempDirectory.Path);

        var fullOutput = Path.Combine(tempDirectory.Path, "full.json");
        var noneOutput = Path.Combine(tempDirectory.Path, "none.json");
        var slicesOutput = Path.Combine(tempDirectory.Path, "slices.json");
        // `none` with a sidecar used to run the whole analysis and then exit 1 with "Crypto
        // data-flow result was not generated"; `slices` used to write a trimmed sidecar.
        Assert.Equal(0, CommandLine.Main(["crypto", "--path", tempDirectory.Path, "--o", fullOutput, "--graph-format", "graphml"]));
        Assert.Equal(0, CommandLine.Main(["crypto", "--path", tempDirectory.Path, "--o", noneOutput, "--crypto-dataflows", "none", "--graph-format", "graphml"]));
        Assert.Equal(0, CommandLine.Main(["crypto", "--path", tempDirectory.Path, "--o", slicesOutput, "--crypto-dataflows", "slices", "--graph-format", "graphml"]));

        var fullSidecar = File.ReadAllBytes(Path.Combine(tempDirectory.Path, "full-dataflows.graphml"));
        Assert.Equal(fullSidecar, File.ReadAllBytes(Path.Combine(tempDirectory.Path, "none-dataflows.graphml")));
        Assert.Equal(fullSidecar, File.ReadAllBytes(Path.Combine(tempDirectory.Path, "slices-dataflows.graphml")));

        using (var noneDocument = JsonDocument.Parse(File.ReadAllText(noneOutput)))
        {
            Assert.False(noneDocument.RootElement.TryGetProperty("CryptoDataFlows", out _));
            Assert.True(noneDocument.RootElement.GetProperty("Statistics").GetProperty("CryptoDataFlowSliceCount").GetInt32() >= 1);
        }

        using (var slicesDocument = JsonDocument.Parse(File.ReadAllText(slicesOutput)))
        {
            Assert.True(slicesDocument.RootElement.TryGetProperty("CryptoDataFlows", out _));
        }
    }

    [Fact]
    public void McpCryptoTool_CryptoDataFlowsArgument_ShapesOutputAndRejectsUnknownValues()
    {
        using var tempDirectory = new TemporaryDirectory();
        WriteDetailFlowFixture(tempDirectory.Path);

        var output = new StringWriter();
        McpServer.Run(tempDirectory.Path, null, null, null, new StringReader("""
{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"dosai.crypto","arguments":{"format":"dosai","crypto_dataflows":"none"}}}

"""), output);
        var noneText = output.ToString();
        Assert.DoesNotContain("CryptoDataFlows", noneText, StringComparison.Ordinal);
        Assert.Contains("CryptoDataFlowSliceCount", noneText, StringComparison.Ordinal);

        output = new StringWriter();
        McpServer.Run(tempDirectory.Path, null, null, null, new StringReader("""
{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"dosai.crypto","arguments":{"format":"dosai","crypto_dataflows":"slices"}}}

"""), output);
        Assert.Contains("CryptoDataFlows", output.ToString(), StringComparison.Ordinal);

        output = new StringWriter();
        McpServer.Run(tempDirectory.Path, null, null, null, new StringReader("""
{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"dosai.crypto","arguments":{"crypto_dataflows":"half"}}}

"""), output);
        Assert.Contains("\"error\"", output.ToString(), StringComparison.Ordinal);

        output = new StringWriter();
        McpServer.Run(tempDirectory.Path, null, null, null, new StringReader("{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/list\"}\n"), output);
        Assert.Contains("crypto_dataflows", output.ToString(), StringComparison.Ordinal);
    }

    internal static void WriteDetailFlowFixture(string directory)
    {
        File.WriteAllText(Path.Combine(directory, "DetailFlow.cs"), """
using System.Security.Cryptography;
using System.Text;

class DetailFlow
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
    }

    /// <summary>
    ///     Walks every object under a crypto result and asserts that every string or
    ///     <c>List&lt;string&gt;</c> property named <c>*Id</c>/<c>*Ids</c> resolves against the
    ///     ids the result still carries (nodes, edges, slices, entry points, weaknesses, and
    ///     each collection's own ids), so a collection added later that trimming can dangle
    ///     fails here instead of shipping. Method-space ids (<c>MethodId</c>, <c>Method</c>,
    ///     <c>CallPath</c>, <c>MethodIds</c>, <c>MemberIds</c>, <c>AssemblyId</c>) identify
    ///     methods-pipeline symbols, which trimming never removes and which have no collection
    ///     inside a data-flow result; they are out of scope by design.
    /// </summary>
    private static void AssertEveryGraphReferenceResolves(CryptoAnalysisResult result)
    {
        // Two scopes, shared universes. Inside CryptoDataFlows every id reference is strict:
        // nodes, edges, slices, entry points, weaknesses and the derived collections resolve
        // against ids the document still carries. On the crypto evidence, DataFlowSliceIds
        // must resolve into the retained graph, and the asset/operation/material
        // cross-references resolve within the evidence; EntryPointIds name entry points of the
        // methods run (`ep:Cli#::...`), which no crypto document carries, so they are external
        // references and out of scope - the same holds for DataFlowSliceIds once `none` detail
        // has omitted the graph (their statistics and counts survive on purpose).
        var universes = new HashSet<string>(StringComparer.Ordinal);
        var references = new List<(string Where, string Id)>();
        var visited = new HashSet<object>();
        var skips = new HashSet<string>(MethodIdPropertyNames, StringComparer.Ordinal) { "EntryPointIds" };
        Walk(result.CryptoDataFlows, MethodIdPropertyNames, universes, references, visited);
        var evidenceSkips = new HashSet<string>(skips, StringComparer.Ordinal);
        if (result.CryptoDataFlows is null)
        {
            evidenceSkips.Add("DataFlowSliceIds");
        }

        foreach (var evidence in new object?[] { result.Assets, result.Operations, result.Materials, result.Protocols, result.Findings })
        {
            Walk(evidence, evidenceSkips, universes, references, visited);
        }

        var failures = references.Where(reference => !universes.Contains(reference.Id)).Take(20).Select(reference => $"{reference.Where} = '{reference.Id}' resolves to nothing the result still carries").ToList();
        Assert.True(failures.Count == 0, "dangling graph references:\n" + string.Join("\n", failures));
    }

    private static void Walk(object? value, HashSet<string> skippedProperties, HashSet<string> universes, List<(string Where, string Id)> references, HashSet<object> visited)
    {
        switch (value)
        {
            case null:
            case string:
                return;
            case Enum:
            case bool or int or long or double or float or decimal or DateTime or DateTimeOffset:
                return;
        }

        if (value is IEnumerable sequence)
        {
            foreach (var element in sequence)
            {
                Walk(element is DictionaryEntry entry ? entry.Value : element, skippedProperties, universes, references, visited);
            }

            return;
        }

        if (!value.GetType().IsValueType && !visited.Add(value))
        {
            return;
        }

        var type = value.GetType();
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            var propertyValue = property.GetValue(value);
            if (property.Name.Equals("Id", StringComparison.Ordinal))
            {
                if (propertyValue is string ownId && ownId.Length > 0)
                {
                    universes.Add(ownId);
                }

                continue;
            }

            var isIdReference = property.Name.EndsWith("Id", StringComparison.Ordinal) || property.Name.EndsWith("Ids", StringComparison.Ordinal);
            if (isIdReference && !skippedProperties.Contains(property.Name))
            {
                switch (propertyValue)
                {
                    case string id when id.Length > 0:
                        references.Add(($"{type.Name}.{property.Name}", id));
                        continue;
                    case IEnumerable<string> ids:
                        references.AddRange(ids.Where(id => id.Length > 0).Select(id => ($"{type.Name}.{property.Name}", id)));
                        continue;
                }
            }

            Walk(propertyValue, skippedProperties, universes, references, visited);
        }
    }

    /// <summary>
    ///     Id-named properties that are not references into the crypto document: method-space
    ///     ids name methods-pipeline symbols (no collection of them lives in a data-flow
    ///     result), and <c>RuleId</c> is a rule identifier, not a reference.
    /// </summary>
    private static readonly HashSet<string> MethodIdPropertyNames = new(StringComparer.Ordinal)
    {
        "MethodId", "MethodIds", "Method", "CallPath", "MemberIds", "AssemblyId", "RuleId"
    };

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dosai-detail-" + Guid.NewGuid().ToString("N"));
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
