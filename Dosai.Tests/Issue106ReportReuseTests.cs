using System.Text.Json;
using System.Text.Json.Nodes;
using Depscan;
using Xunit;

namespace Dosai.Tests;

// Issue #106: crypto rebuilt the methods slice and walked the data flows a second time with the
// crypto pack, on a tree that `methods` and `dataflows` had just analysed (cdxgen runs the three
// back to back). crypto now takes a methods report and a data-flow report written for the same
// tree, checks them, and says in Diagnostics when it used or refused one.
public sealed class Issue106ReportReuseTests
{
    private const string Project = """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework><OutputType>Exe</OutputType></PropertyGroup></Project>""";

    private const string HasherSource = """
        using System;
        using System.Security.Cryptography;
        using System.Text;

        namespace Vault;

        public static class Hasher
        {
            public static byte[] Digest(string input)
            {
                using var md5 = MD5.Create();
                return md5.ComputeHash(Encoding.UTF8.GetBytes(input));
            }

            public static byte[] Encrypt(byte[] payload)
            {
                var key = Convert.FromBase64String(Console.ReadLine()!);
                using var aes = Aes.Create();
                aes.Key = key;
                using var encryptor = aes.CreateEncryptor();
                return encryptor.TransformFinalBlock(payload, 0, payload.Length);
            }
        }

        public static class Program
        {
            public static void Main(string[] args)
            {
                Console.WriteLine(Convert.ToHexString(Hasher.Digest(args[0])));
                Console.WriteLine(Hasher.Encrypt(Encoding.UTF8.GetBytes(args[0])).Length);
            }
        }
        """;

    [Fact]
    public void Crypto_WithReusedReports_GivesTheSameEvidenceAsFromScratch()
    {
        using var fixture = new TempDir();
        WriteTree(fixture.Path);
        var methodsReport = Path.Combine(fixture.Path, "..", Path.GetFileName(fixture.Path) + "-methods.json");
        var dataFlowsReport = Path.Combine(fixture.Path, "..", Path.GetFileName(fixture.Path) + "-dataflows.json");
        File.WriteAllText(methodsReport, Depscan.Dosai.GetMethods(fixture.Path));
        File.WriteAllText(dataFlowsReport, DataFlowAnalyzer.GetDataFlows(fixture.Path));
        try
        {
            var scratch = CryptoAnalyzer.Analyze(fixture.Path);
            var reused = CryptoAnalyzer.Analyze(fixture.Path, null, BuildPreparationMode.None, CryptoDataFlowDetail.Full, methodsReport, dataFlowsReport);

            Assert.Contains(reused.Diagnostics, line => line.StartsWith("The methods slice was taken from the report", StringComparison.Ordinal));
            Assert.Contains(reused.Diagnostics, line => line.StartsWith("The crypto data flows were taken from the data-flow report", StringComparison.Ordinal));
            Assert.NotEmpty(scratch.Findings);
            Assert.NotEmpty(scratch.CryptoDataFlows!.Slices);

            // The reuse diagnostics and the per-run timestamp are the only differences in
            // everything but the embedded graph, whose slice ids come from the report.
            Assert.Equal(Evidence(scratch), Evidence(reused));
            Assert.Equal(Comparable(scratch.CryptoDataFlows!), Comparable(reused.CryptoDataFlows!));
        }
        finally
        {
            File.Delete(methodsReport);
            File.Delete(dataFlowsReport);
        }
    }

    [Fact]
    public void Crypto_WithAReusedMethodsReport_IsByteIdenticalToFromScratch()
    {
        using var fixture = new TempDir();
        WriteTree(fixture.Path);
        var report = Path.Combine(fixture.Path, "..", Path.GetFileName(fixture.Path) + "-methods.json");
        File.WriteAllText(report, Depscan.Dosai.GetMethods(fixture.Path));
        try
        {
            var scratch = CryptoAnalyzer.Analyze(fixture.Path);
            var reused = CryptoAnalyzer.Analyze(fixture.Path, null, BuildPreparationMode.None, CryptoDataFlowDetail.Full, report, null);

            Assert.Equal(Serialize(scratch, dropReuseDiagnostics: true), Serialize(reused, dropReuseDiagnostics: true));
        }
        finally
        {
            File.Delete(report);
        }
    }

    [Fact]
    public void Crypto_AMethodsReportWithoutSectionsItDoesNotRead_IsStillReused()
    {
        using var fixture = new TempDir();
        WriteTree(fixture.Path);
        var report = Path.Combine(fixture.Path, "..", Path.GetFileName(fixture.Path) + "-methods.json");
        File.WriteAllText(report, Depscan.Dosai.GetMethods(fixture.Path, MethodsSections.Parse("Methods,CallGraph,EntryPoints,Reachability,MethodCalls")));
        try
        {
            var reused = CryptoAnalyzer.Analyze(fixture.Path, null, BuildPreparationMode.None, CryptoDataFlowDetail.None, report, null);

            Assert.Contains(reused.Diagnostics, line => line.StartsWith("The methods slice was taken from the report", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(report);
        }
    }

    [Fact]
    public void Crypto_AReportForAnotherPathVersionOrPackSet_IsRefusedWithADiagnostic()
    {
        using var fixture = new TempDir();
        WriteTree(fixture.Path);
        using var other = new TempDir();
        WriteTree(other.Path);
        var scratch = Serialize(CryptoAnalyzer.Analyze(fixture.Path), dropReuseDiagnostics: true);

        void AssertRefused(string methodsJson, string? dataFlowsJson, string expectedFragment)
        {
            var methods = Path.Combine(fixture.Path, "..", Path.GetFileName(fixture.Path) + "-m.json");
            var flows = Path.Combine(fixture.Path, "..", Path.GetFileName(fixture.Path) + "-d.json");
            File.WriteAllText(methods, methodsJson);
            File.WriteAllText(flows, dataFlowsJson ?? "{}");
            try
            {
                var result = CryptoAnalyzer.Analyze(fixture.Path, null, BuildPreparationMode.None, CryptoDataFlowDetail.Full, methods, dataFlowsJson is null ? null : flows);
                Assert.Contains(result.Diagnostics, line => line.Contains("was not reused", StringComparison.Ordinal) && line.Contains(expectedFragment, StringComparison.Ordinal));
                // A refused report changes nothing but that line.
                Assert.Equal(scratch, Serialize(result, dropReuseDiagnostics: true));
            }
            finally
            {
                File.Delete(methods);
                File.Delete(flows);
            }
        }

        var methodsJson = Depscan.Dosai.GetMethods(fixture.Path);
        AssertRefused(Depscan.Dosai.GetMethods(other.Path), null, "it analysed");
        AssertRefused(Edit(methodsJson, node => node["Metadata"]!["SchemaVersion"] = "4.1.0"), null, "schema version");
        AssertRefused(Edit(methodsJson, node => node["Metadata"]!["AnalyzerVersion"] = "4.1.0.0"), null, "not by this build");
        AssertRefused(Edit(methodsJson, node => node["Metadata"]!["OmittedSections"] = new JsonArray("Methods")), null, "without the sections Methods");
        AssertRefused("not json", null, "could not be read");

        var flowsJson = DataFlowAnalyzer.GetDataFlows(fixture.Path, patternPacks: "aspnet");
        AssertRefused(methodsJson, flowsJson, "which do not include crypto");
        AssertRefused(methodsJson, Edit(DataFlowAnalyzer.GetDataFlows(fixture.Path), node => node["Metadata"]!["InputPath"] = other.Path), "it analysed");
    }

    [Fact]
    public void DataFlows_EverySliceRecordsThePacksThatMatchedIt_AndTheReportTheirSet()
    {
        using var fixture = new TempDir();
        WriteTree(fixture.Path);

        var result = DataFlowAnalyzer.Analyze(fixture.Path);

        Assert.Contains("crypto", result.Patterns.PatternPacks);
        Assert.Contains(result.Slices, slice => slice.Packs is not null && slice.Packs.SequenceEqual(["crypto"]));
        Assert.Contains(result.Patterns.Sinks, pattern => pattern.Pack == "crypto");
        Assert.DoesNotContain(result.Patterns.Sinks.Concat(result.Patterns.Sources), pattern => pattern.Pack is "");

        // A run with only the crypto pack finds the same slices the crypto view of the full run keeps.
        var cryptoOnly = DataFlowAnalyzer.Analyze(fixture.Path, patternPacks: "crypto");
        var view = ReportReuse.CryptoView(result);
        Assert.Equal(Comparable(cryptoOnly), Comparable(view));
    }

    private static string Edit(string json, Action<JsonNode> edit)
    {
        var node = JsonNode.Parse(json)!;
        edit(node);
        return node.ToJsonString();
    }

    internal static string Serialize(CryptoAnalysisResult result, bool dropReuseDiagnostics = false)
    {
        using var stream = new MemoryStream();
        CryptoAnalyzer.Export(stream, result, CryptoOutputFormat.Dosai);
        var node = JsonNode.Parse(stream.ToArray())!;
        node["Metadata"]!["GeneratedAt"] = null;
        if (node["CryptoDataFlows"]?["Metadata"] is { } flowMetadata)
        {
            flowMetadata["GeneratedAt"] = null;
        }

        if (dropReuseDiagnostics && node["Diagnostics"] is JsonArray diagnostics)
        {
            for (var index = diagnostics.Count - 1; index >= 0; index--)
            {
                var line = diagnostics[index]!.GetValue<string>();
                if (line.Contains("was not reused", StringComparison.Ordinal) || line.Contains(" were taken from the ", StringComparison.Ordinal) || line.Contains(" was taken from the ", StringComparison.Ordinal))
                {
                    diagnostics.RemoveAt(index);
                }
            }
        }

        return node.ToJsonString();
    }

    /// <summary>Everything the crypto report holds except the embedded graph and the reuse notes.</summary>
    internal static string Evidence(CryptoAnalysisResult result)
    {
        var node = JsonNode.Parse(Serialize(result))!.AsObject();
        node.Remove("CryptoDataFlows");
        node.Remove("Diagnostics");
        node["Statistics"]!.AsObject().Remove("CryptoDataFlowSliceCount");
        foreach (var section in new[] { "Operations", "Materials", "Findings" })
        {
            foreach (var item in node[section]!.AsArray())
            {
                item!["DataFlowSliceIds"] = null;
                if (item["Properties"] is JsonObject properties)
                {
                    properties.Remove("dataFlowSliceIds");
                    properties.Remove("sourceMaterialIds");
                    properties.Remove("sinkOperationIds");
                }
            }
        }

        return node.ToJsonString();
    }

    /// <summary>A data-flow graph by what it says (locations, names, categories), not by the ids a run gave it.</summary>
    private static List<string> Comparable(DataFlowResult graph)
    {
        var nodes = graph.Nodes.ToDictionary(node => node.Id);
        string Describe(string id) => $"{nodes[id].FileName}:{nodes[id].LineNumber}:{nodes[id].ColumnNumber}:{nodes[id].Name}";
        return graph.Slices
            .Select(slice => $"{Describe(slice.SourceId)}->{Describe(slice.SinkId)}|{slice.SinkArgumentIndex}|{slice.Severity}|{slice.Confidence}|{string.Join(",", slice.NodeIds.Select(Describe))}")
            .Concat(graph.Nodes.Select(node => $"node {node.FileName}:{node.LineNumber}:{node.ColumnNumber}:{node.Name}:{node.IsSource}:{node.IsSink}"))
            .Concat(graph.Edges.Select(edge => $"edge {Describe(edge.SourceId)}->{Describe(edge.TargetId)}:{edge.Kind}"))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private static void WriteTree(string root)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Vault.csproj"), Project);
        File.WriteAllText(Path.Combine(root, "Hasher.cs"), HasherSource);
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir() => Directory.CreateDirectory(Path);

        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dosai-issue106-" + Guid.NewGuid().ToString("N"), "tree");

        public void Dispose()
        {
            try
            {
                Directory.Delete(System.IO.Path.GetDirectoryName(Path)!, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
