using System.Text.Json;
using Depscan;
using Xunit;

namespace Dosai.Tests;

/// <summary>
///     Integration tests against the real sample apps in ~/sandbox/dosai-corpus (cloned from GitHub
///     at pinned commits and built — run <c>Dosai.Tests/Corpus/setup.sh</c>). Tests SKIP (not pass)
///     when the corpus is absent, so a green CI run cannot hide a corpus that was never analyzed.
///     The numeric floors are pinned from the 4.0.1 release runs to surface analysis regressions.
/// </summary>
public class CorpusTests
{
    private static readonly string CorpusRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "sandbox", "dosai-corpus");

    private static string CorpusPathOrSkip(string relative)
    {
        var path = Path.Combine(CorpusRoot, relative);
        Skip.If(!Directory.Exists(path), $"Corpus app '{relative}' is not present. Run Dosai.Tests/Corpus/setup.sh to clone and build the pinned corpus (see Dosai.Tests/Corpus/README.md).");
        return path;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    [SkippableFact]
    public void Corpus_EShopOnWeb_MethodsReachabilityAndFindings()
    {
        var path = CorpusPathOrSkip("eShopOnWeb/src/Web");
        var slice = JsonSerializer.Deserialize<MethodsSlice>(Depscan.Dosai.GetMethods(path), JsonOptions)!;

        // A real mixed MVC + minimal-api app: dozens of framework entry points
        // (43 at the 4.0.1 release run).
        Assert.True(slice.EntryPoints!.Count >= 40, $"expected >= 40 entry points, got {slice.EntryPoints.Count}");
        Assert.Contains(slice.EntryPoints, entryPoint => entryPoint.Kind is "HttpController" or "HttpMinimalApi");

        // R1: the reachability index is populated and graph-derived depths exist
        // (12,062 indexed nodes at the release run).
        var reachability = slice.Reachability!;
        Assert.True(reachability.Count > 10_000, $"expected a large reachability index, got {reachability.Count}");
        Assert.Contains(reachability, facts => facts.ReachableEntryPoints.Count > 0 && facts.DepthFromEntryPoint is > 0);
        Assert.Contains(reachability, facts => facts.FanIn > 0 && facts.FanOut > 0);

        // R7: repeated call sites collapse into counted edges.
        Assert.Contains(slice.CallGraph!.Edges, edge => edge.CallSiteCount > 1);

        // F1: the endpoint security findings engine produces real findings on a real app
        // (11 antiforgery findings at the release run).
        var antiforgeryFindings = slice.SecurityFindings!.Count(finding => finding.Kind == "StateChangingEndpointWithoutAntiforgery");
        Assert.True(antiforgeryFindings >= 10, $"expected >= 10 antiforgery findings, got {antiforgeryFindings}");

        // S1: a restored tree resolves package purls into the slice.
        Assert.True(slice.PackageReachability!.Count > 25, $"expected resolved purls, got {slice.PackageReachability.Count}");

        // The merged graph stays valid: every edge references an existing node.
        var nodeIds = slice.CallGraph!.Nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        Assert.All(slice.CallGraph.Edges, edge =>
        {
            Assert.Contains(edge.SourceId, nodeIds);
            Assert.Contains(edge.TargetId, nodeIds);
        });
    }

    [SkippableFact]
    public void Corpus_EShopOnWeb_DataFlowsWeaknessesAndPurls()
    {
        var path = CorpusPathOrSkip("eShopOnWeb/src/Web");
        var result = DataFlowAnalyzer.Analyze(path);

        // The W-pack coverage produces CWE-stamped, severity-ranked weaknesses on a real app
        // (130 slices at the release run).
        Assert.True(result.Slices.Count > 100, $"expected real slices, got {result.Slices.Count}");
        Assert.All(result.Slices, slice => Assert.False(string.IsNullOrWhiteSpace(slice.Severity)));
        Assert.True(result.WeaknessCandidates.Count(weakness => weakness.Cwe is not null) > 100,
            $"expected CWE-stamped weaknesses, got {result.WeaknessCandidates.Count(weakness => weakness.Cwe is not null)}");
        Assert.Contains(result.WeaknessCandidates, weakness => weakness.Severity is "high" or "medium" or "low");

        // Every slice's nodes and edges reference ids that exist (AGENTS.md edge contract).
        var nodeIds = result.Nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        Assert.All(result.Edges, edge =>
        {
            Assert.Contains(edge.SourceId, nodeIds);
            Assert.Contains(edge.TargetId, nodeIds);
        });
    }

    [SkippableFact]
    public void Corpus_Orleans1_GrainEndpointsDetected()
    {
        var path = CorpusPathOrSkip("practical-aspnetcore/projects/orleans/orleans-1");
        var slice = JsonSerializer.Deserialize<MethodsSlice>(Depscan.Dosai.GetMethods(path), JsonOptions)!;

        // F2: the minimal-hosting sample declares HelloArchiveGrain : IGrain, IHelloArchive —
        // the Orleans provider surfaces grain methods as GrainMethod entry points with rpc taint
        // seeds, and the GetGrain<T> call site becomes an outbound rpc service.
        Assert.NotEmpty(slice.EntryPoints!.Where(entryPoint => entryPoint.Kind == "GrainMethod").ToList());
        Assert.Contains(slice.Services!, service => service.Framework == "orleans");
    }

    [SkippableFact]
    public void Corpus_EShopOnWeb_CryptoReachabilityUsesGraphFacts()
    {
        var path = CorpusPathOrSkip("eShopOnWeb/src/Web");
        var methodsSlice = Depscan.Dosai.GetMethodsSlice(path);
        var result = CryptoAnalyzer.Analyze(path, methodsSlice);

        // R9: reachability comes from the R1 index (exact graph facts) — no exception, and the
        // gated file-level fallback never claims reachability without a path.
        Assert.All(result.Findings, finding =>
        {
            if (finding.ReachableFromEntryPoint)
            {
                Assert.NotEmpty(finding.EntryPointIds);
            }
        });
    }
}
