using System.Text.Json;
using Depscan;
using Xunit;

namespace Dosai.Tests;

/// <summary>
///     Integration tests against the real sample apps in ~/sandbox/dosai-corpus (cloned from GitHub
///     at pinned commits and built; run <c>Dosai.Tests/Corpus/setup.sh</c>). Tests SKIP (not pass)
///     when the corpus is absent, so a green CI run cannot hide a corpus that was never analyzed.
///     The numeric floors are pinned from the baseline runs recorded when these tests were added,
///     so analysis regressions surface as assertion failures.
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
        // (43 when these floors were pinned).
        Assert.True(slice.EntryPoints!.Count >= 40, $"expected >= 40 entry points, got {slice.EntryPoints.Count}");
        Assert.Contains(slice.EntryPoints, entryPoint => entryPoint.Kind is "HttpController" or "HttpMinimalApi");

        // The reachability index is populated and graph-derived depths exist
        // (12,062 indexed nodes at the release run).
        var reachability = slice.Reachability!;
        Assert.True(reachability.Count > 10_000, $"expected a large reachability index, got {reachability.Count}");
        Assert.Contains(reachability, facts => facts.ReachableEntryPoints.Count > 0 && facts.DepthFromEntryPoint is > 0);
        Assert.Contains(reachability, facts => facts.FanIn > 0 && facts.FanOut > 0);

        // Repeated call sites collapse into counted edges.
        Assert.Contains(slice.CallGraph!.Edges, edge => edge.CallSiteCount > 1);

        // The endpoint security findings engine produces real findings on a real app
        // (11 antiforgery findings at the release run).
        var antiforgeryFindings = slice.SecurityFindings!.Count(finding => finding.Kind == "StateChangingEndpointWithoutAntiforgery");
        Assert.True(antiforgeryFindings >= 10, $"expected >= 10 antiforgery findings, got {antiforgeryFindings}");

        // A restored tree resolves package purls into the slice.
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

        // The minimal-hosting sample declares HelloArchiveGrain : IGrain, IHelloArchive.
        // The Orleans provider surfaces grain methods as GrainMethod entry points with rpc taint
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

        // Reachability comes from the reachability index (exact graph facts), no exception, and the
        // gated file-level fallback never claims reachability without a path.
        Assert.All(result.Findings, finding =>
        {
            if (finding.ReachableFromEntryPoint)
            {
                Assert.NotEmpty(finding.EntryPointIds);
            }
        });
    }

    [SkippableFact]
    public void Corpus_EShopOnWeb_TopLevelEntryPointsDeadCodeAndAttackSurface()
    {
        var path = CorpusPathOrSkip("eShopOnWeb/src/Web");
        var slice = JsonSerializer.Deserialize<MethodsSlice>(Depscan.Dosai.GetMethods(path), JsonOptions)!;

        // EShop's Program.cs uses top-level statements, the synthesized `<Main>$` must be a
        // Cli entry point whose MethodId resolves against a reachable graph node.
        var cli = slice.EntryPoints!.FirstOrDefault(entryPoint => entryPoint is { Kind: "Cli", MethodId: not null } && entryPoint.MethodId.Contains("<Main>$", StringComparison.Ordinal));
        Assert.NotNull(cli);
        Assert.Contains(slice.Reachability!, facts => string.Equals(facts.NodeId, cli.MethodId, StringComparison.Ordinal) && facts.Reachable);

        // Dead code on a real app, non-empty, source-located, bounded, and never a
        // reflection/DI keep-alive target.
        var deadCode = slice.DeadCode!;
        Assert.True(deadCode.Count > 0, $"expected dead code on a real app, got {deadCode.Count}");
        Assert.All(deadCode, entry => Assert.False(string.IsNullOrWhiteSpace(entry.FileName)));
        Assert.True(deadCode.Count <= 500, $"dead-code report exceeded its bound: {deadCode.Count}");
        var keepAliveNodes = slice.Reachability!.Where(facts => facts.KeepAlive).Select(facts => facts.NodeId).ToHashSet(StringComparer.Ordinal);
        Assert.All(deadCode, entry => Assert.DoesNotContain(entry.NodeId, keepAliveNodes));

        // The attack-surface view groups every entry point by exposure on real code.
        var result = DataFlowAnalyzer.Analyze(path);
        Assert.NotEmpty(result.AttackSurface);
        Assert.Equal(result.EntryPoints.Count, result.AttackSurface.Sum(group => group.EntryPointCount));
        Assert.Contains(result.AttackSurface, group => group.Exposure.EndsWith("-http", StringComparison.Ordinal));
    }

    // Issue #78: Web's build output holds the projects (Web, Infrastructure, ApplicationCore,
    // BlazorShared) beside two dozen packages and a Blazor debug-proxy tool that no deps.json
    // names. Every flow is kept; those confined to dependency code are scoped and capped at low.
    [SkippableFact]
    public void Corpus_EShopOnWeb_DependencyFlowsScopedApplicationFlowsKept()
    {
        var path = CorpusPathOrSkip("eShopOnWeb/src/Web");
        var result = DataFlowAnalyzer.Analyze(path);

        // 149 slices at the calibration run, the same set as before the scope existed: 146 inside
        // packages and the debug proxy, 3 in the app (one source, two Web.dll) at high.
        Assert.True(result.Slices.Count >= 140, $"expected the full slice set, got {result.Slices.Count}");
        var applicationSlices = result.Slices.Where(slice => slice.Scope is null).ToList();
        Assert.True(applicationSlices.Count >= 3, $"expected the app's own slices, got {applicationSlices.Count}");
        Assert.Contains(applicationSlices, slice => slice.Severity == "high");
        var dependencySlices = result.Slices.Where(TransparencyBuilder.IsDependencySlice).ToList();
        Assert.Equal(dependencySlices.Count, result.Statistics.DependencySliceCount);
        Assert.True(dependencySlices.Count >= 140, $"expected package slices, got {dependencySlices.Count}");
        Assert.All(dependencySlices, slice => Assert.Contains(slice.Severity, new[] { "info", "low" }));

        // Only the projects' assemblies are application code; the debug-proxy tool (copied by a
        // package, absent from Web.deps.json) is a dependency.
        var scopedAssemblies = result.Nodes.Where(TransparencyBuilder.IsDependencyNode).Select(node => node.Properties["assembly"]).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("NuGet.Packaging.dll", scopedAssemblies);
        Assert.Contains("BrowserDebugHost.dll", scopedAssemblies);
        Assert.DoesNotContain("Web.dll", scopedAssemblies);
        Assert.DoesNotContain("Infrastructure.dll", scopedAssemblies);

        // A package seen only from inside its own IL is Low; one the app's code reaches is not
        // (12 High and 26 Low at the calibration run).
        Assert.Equal("Low", Assert.Single(result.PackageReachability, package => package.Purl.StartsWith("pkg:nuget/NuGet.Packaging@", StringComparison.Ordinal)).Confidence);
        Assert.Equal("High", Assert.Single(result.PackageReachability, package => package.Purl.StartsWith("pkg:nuget/Microsoft.EntityFrameworkCore@", StringComparison.Ordinal)).Confidence);
    }

    // Issue #78 repro, built by setup.sh: a console app whose only package is MailKit. Every
    // data-flow slice starts and ends inside BouncyCastle, MailKit or MimeKit.
    [SkippableFact]
    public void Corpus_Issue78MailKit_DependencyFlowsKeptAtLowSeverity()
    {
        var path = CorpusPathOrSkip("issue78-mailkit");
        var result = DataFlowAnalyzer.Analyze(path);

        // 174 slices at the calibration run: the 173 of the report plus one inside MailKit's NTLM
        // code that the summaries find now that array stores are modelled.
        Assert.True(result.Slices.Count >= 173, $"expected every package flow to be kept, got {result.Slices.Count}");
        Assert.Equal(result.Slices.Count, result.Statistics.DependencySliceCount);
        Assert.All(result.Slices, slice =>
        {
            Assert.Equal(TransparencyBuilder.DependencyScope, slice.Scope);
            Assert.Contains(slice.Severity, new[] { "info", "low" });
        });
        Assert.All(result.WeaknessCandidates, weakness => Assert.Equal(TransparencyBuilder.DependencyScope, weakness.Scope));
        Assert.All(result.DangerousApiReachability, api => Assert.Equal("Low", api.Confidence));
        foreach (var name in new[] { "BouncyCastle.Cryptography", "MailKit", "MimeKit" })
        {
            var package = Assert.Single(result.PackageReachability, package => package.Purl.StartsWith($"pkg:nuget/{name}@", StringComparison.Ordinal));
            Assert.Equal("Low", package.Confidence);
        }

        // The generic `stelem` drift sent every typed-array loop into the state budget
        // (87 summary and 36 interpreter budget hits before; 55 and 36 at the calibration run).
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Contains("Org.BouncyCastle.Utilities.Arrays.Reverse", StringComparison.Ordinal));
        var summaryBudgetHits = result.Diagnostics.Count(diagnostic => diagnostic.StartsWith("IL summary interpreter hit", StringComparison.Ordinal));
        Assert.True(summaryBudgetHits <= 60, $"expected the drift-free budget profile, got {summaryBudgetHits} summary budget hits");
    }

    [SkippableFact]
    public void Corpus_Issue78MailKit_CryptoEvidenceMatchesTheBinExcludedRun()
    {
        var path = CorpusPathOrSkip("issue78-mailkit");
        var full = CryptoAnalyzer.Analyze(path);
        CryptoAnalysisResult excluded;
        using (PathExclusions.Apply(path, ["**/bin/**"]))
        {
            excluded = CryptoAnalyzer.Analyze(path);
        }

        // The crypto evidence comes from Program.cs either way; the package flows only add
        // dependency-scoped slices.
        Assert.Equal(excluded.Statistics.AssetCount, full.Statistics.AssetCount);
        Assert.Equal(excluded.Statistics.OperationCount, full.Statistics.OperationCount);
        Assert.Equal(excluded.Statistics.FindingCount, full.Statistics.FindingCount);
        Assert.Equal(excluded.Findings.Select(finding => (finding.RuleId, finding.Severity)), full.Findings.Select(finding => (finding.RuleId, finding.Severity)));
        Assert.True(full.Statistics.CryptoDataFlowSliceCount > 0);
        Assert.Equal(full.Statistics.CryptoDataFlowSliceCount, full.Statistics.CryptoDependencyDataFlowSliceCount);
        Assert.Equal(0, excluded.Statistics.CryptoDataFlowSliceCount);
    }

    // Added with the target-framework-aware analysis (schema 5.1.0): the corpus previously
    // exercised only net10/net11-era apps, which is how a net8-crashing defect shipped unseen.
    // This is a real .NET 8 LTS app whose TFM comes from src/Directory.Build.props.
    [SkippableFact]
    public void Corpus_ModularMonolith_Net8App_TargetFrameworkDetectedAndSurfaceAnalyzed()
    {
        var path = CorpusPathOrSkip("modular-monolith-with-ddd/src");
        var slice = JsonSerializer.Deserialize<MethodsSlice>(Depscan.Dosai.GetMethods(path), JsonOptions)!;

        // The net8.0 target is detected from the tree and surfaced in metadata - not silently
        // assumed - so the fallback diagnostic must stay silent here.
        Assert.Equal(["net8.0"], slice.Metadata!.TargetFrameworks);
        Assert.DoesNotContain(slice.Diagnostics ?? [], diagnostic => diagnostic.Contains("No TargetFramework detected", StringComparison.Ordinal));

        // 111 entry points / 72,108 methods / 9,806 edges at the calibration run.
        Assert.True(slice.EntryPoints!.Count >= 90, $"expected >= 90 entry points, got {slice.EntryPoints.Count}");
        Assert.True(slice.Methods!.Count >= 60_000, $"expected >= 60k methods, got {slice.Methods.Count}");
        Assert.True(slice.CallGraph!.Edges.Count >= 8_000, $"expected >= 8k call-graph edges, got {slice.CallGraph.Edges.Count}");

        // The merged graph stays valid: every edge references an existing node.
        var nodeIds = slice.CallGraph.Nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        Assert.All(slice.CallGraph.Edges, edge =>
        {
            Assert.Contains(edge.SourceId, nodeIds);
            Assert.Contains(edge.TargetId, nodeIds);
        });
    }

    // A real six-target multi-targeting library (net462;netstandard2.0;netstandard2.1;
    // net8.0;net9.0;net10.0): the union of every target's preprocessor guards is analyzed and
    // the full TFM set is surfaced.
    [SkippableFact]
    public void Corpus_GrpcNetClient_MultiTargetLibrary_RepresentativeTargetAnalyzed()
    {
        var path = CorpusPathOrSkip("grpc-dotnet/src/Grpc.Net.Client");
        var slice = JsonSerializer.Deserialize<MethodsSlice>(Depscan.Dosai.GetMethods(path), JsonOptions)!;

        var targetFrameworks = slice.Metadata!.TargetFrameworks ?? [];
        foreach (var expected in new[] { "net462", "netstandard2.0", "netstandard2.1", "net8.0", "net9.0", "net10.0" })
        {
            Assert.Contains(expected, targetFrameworks);
        }
        Assert.DoesNotContain(slice.Diagnostics ?? [], diagnostic => diagnostic.Contains("No TargetFramework detected", StringComparison.Ordinal));

        // All six targets are detected, but guards resolve against the most modern one, and the
        // slice says so rather than leaving consumers to guess from a declaration-ordered list.
        Assert.Equal("net10.0", slice.Metadata!.GuardTargetFramework);
        Assert.Contains(slice.Diagnostics ?? [], diagnostic => diagnostic.Contains("evaluated against 'net10.0'", StringComparison.Ordinal));

        // 659 methods / 1,929 call sites / 1,547 edges at the calibration run. (The earlier 3,469
        // call sites counted each call once per enclosing block; the distinct sites are unchanged.)
        Assert.True(slice.Methods!.Count >= 500, $"expected >= 500 methods, got {slice.Methods.Count}");
        Assert.True(slice.MethodCalls!.Count >= 1_650, $"expected >= 1.65k call sites, got {slice.MethodCalls.Count}");
        Assert.True(slice.CallGraph!.Edges.Count >= 1_200, $"expected >= 1.2k call-graph edges, got {slice.CallGraph.Edges.Count}");
    }
}
