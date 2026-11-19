using System.Text.Json;
using Depscan;
using Xunit;

namespace Dosai.Tests;

/// <summary>
///     Integration tests against the real sample apps in the corpus directory (<c>DOSAI_CORPUS_DIR</c>, default ~/sandbox/dosai-corpus; cloned from GitHub
///     at pinned commits and built; run <c>Dosai.Tests/Corpus/setup.sh</c>). Tests SKIP (not pass)
///     when the corpus is absent, so a green CI run cannot hide a corpus that was never analyzed.
///     The numeric floors are pinned from the baseline runs recorded when these tests were added,
///     so analysis regressions surface as assertion failures.
/// </summary>
public class CorpusTests
{
    private static readonly string CorpusRoot = CorpusLocation.Root;

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
        // (12,062 indexed nodes at the release run, while the packages under bin/ counted as
        // application code; 2,082 since issue #107 leaves them out).
        var reachability = slice.Reachability!;
        Assert.True(reachability.Count > 1_500, $"expected a large reachability index, got {reachability.Count}");
        Assert.Contains(reachability, facts => facts.ReachableEntryPoints.Count > 0 && facts.DepthFromEntryPoint is > 0);
        Assert.Contains(reachability, facts => facts.FanIn > 0 && facts.FanOut > 0);

        // Repeated call sites collapse into counted edges.
        Assert.Contains(slice.CallGraph!.Edges, edge => edge.CallSiteCount > 1);

        // The endpoint security findings engine produces real findings on a real app. The
        // ManageController actions carry [ValidateAntiForgeryToken]; until 5.0.0-rc.2 the rule
        // tested `!= false` and reported them too (11 findings), leaving UserController.Logout as
        // the one state-changing MVC action that does not opt in.
        var antiforgeryFindings = slice.SecurityFindings!.Where(finding => finding.Kind == "StateChangingEndpointWithoutAntiforgery").ToList();
        Assert.Contains(antiforgeryFindings, finding => finding.Route == "/User");
        Assert.DoesNotContain(antiforgeryFindings, finding => finding.Route?.StartsWith("/Manage/", StringComparison.Ordinal) == true);

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
        // A slice is the package's own only when every node is: the repro's top-level `args`
        // (`<Main>$`, a CLI source since 5.0.0-rc.2) reaching its own sink, or reaching package
        // sinks through state it stored into package objects, is an application flow.
        var nodes = result.Nodes.ToDictionary(node => node.Id);
        bool TouchesTheApplication(DataFlowSlice slice) => slice.NodeIds.Any(id => !TransparencyBuilder.IsDependencyNode(nodes[id]));
        var packageSlices = result.Slices.Where(slice => !TouchesTheApplication(slice)).ToList();
        Assert.True(packageSlices.Count >= 173, $"expected every package flow to be kept, got {packageSlices.Count}");
        Assert.Equal(packageSlices.Count, result.Statistics.DependencySliceCount);
        Assert.All(packageSlices, slice =>
        {
            Assert.Equal(TransparencyBuilder.DependencyScope, slice.Scope);
            Assert.Contains(slice.Severity, new[] { "info", "low" });
        });
        Assert.All(result.Slices.Where(TouchesTheApplication), slice => Assert.Null(slice.Scope));
        var packageSliceIds = packageSlices.Select(slice => slice.Id).ToHashSet(StringComparer.Ordinal);
        Assert.All(result.WeaknessCandidates.Where(weakness => weakness.SliceId is { } id && packageSliceIds.Contains(id)), weakness => Assert.Equal(TransparencyBuilder.DependencyScope, weakness.Scope));
        Assert.All(result.DangerousApiReachability.Where(api => api.NodeIds.All(id => TransparencyBuilder.IsDependencyNode(nodes[id]))), api => Assert.Equal("Low", api.Confidence));
        // MimeKit is reached by the repro's own `args` flow (MailboxAddress.Parse), and MailKit by
        // flows that pass through the repro's code into SmtpClient, so their reachability is the
        // application's. BouncyCastle only appears in flows confined to the packages. MailKit used
        // to come out Low or High depending on the order the file system listed the tree.
        foreach (var name in new[] { "BouncyCastle.Cryptography", "MailKit", "MimeKit" })
        {
            var package = Assert.Single(result.PackageReachability, package => package.Purl.StartsWith($"pkg:nuget/{name}@", StringComparison.Ordinal));
            Assert.Equal(name == "BouncyCastle.Cryptography" ? "Low" : "High", package.Confidence);
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
        // The repro's own `args` flows (see the test above) are the only application slices.
        Assert.InRange(full.Statistics.CryptoDataFlowSliceCount - full.Statistics.CryptoDependencyDataFlowSliceCount, 0, 8);
        Assert.Equal(0, excluded.Statistics.CryptoDataFlowSliceCount);
    }

    // Issue #79 repro, built by setup.sh into out/ twice: a local build, whose PDB names the
    // absolute source path, and a path-mapped one, whose PDB names /_/Program.cs. The call into
    // Newtonsoft.Json is found in source and in IL; both rows and the IL edge name Program.cs.
    [SkippableTheory]
    [InlineData("local")]
    [InlineData("pathmap")]
    public void Corpus_Issue79Newtonsoft_IlCallSitesNameTheSourceRowsFile(string variant)
    {
        var path = CorpusPathOrSkip(Path.Combine("issue79-newtonsoft", variant));
        var slice = JsonSerializer.Deserialize<MethodsSlice>(Depscan.Dosai.GetMethods(path), JsonOptions)!;

        var serializeCalls = slice.MethodCalls!.Where(call => call.ClassName == "JsonConvert" && call.LineNumber == 3).ToList();
        Assert.Contains(serializeCalls, call => call.EvidenceKind == AnalysisEvidenceKind.SourceRoslynDirect);
        Assert.Contains(serializeCalls, call => call.EvidenceKind == AnalysisEvidenceKind.AssemblyIlDirect);
        Assert.All(serializeCalls, call => Assert.Equal("Program.cs", call.Path));
        Assert.Contains(slice.CallGraph!.Edges, edge => edge.CalledMethodName == "SerializeObject" && edge.EvidenceKind == AnalysisEvidenceKind.AssemblyIlDirect && edge.Path == "Program.cs");

        // No record of the scan names the build machine's path (calls no sequence point covers
        // name the assembly under out/).
        Assert.All(slice.MethodCalls!, call => Assert.False(call.Path is not null && (Path.IsPathFullyQualified(call.Path) || call.Path.StartsWith("/_/", StringComparison.Ordinal)), call.Path));
        Assert.All(slice.CallGraph.Edges, edge => Assert.False(edge.Path is not null && Path.IsPathFullyQualified(edge.Path), edge.Path));
        Assert.Contains(slice.MethodCalls!, call => call.Path == Path.Combine("out", "Repro.dll"));
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

        // 111 entry points / 72,108 methods / 9,806 edges at the calibration run, while the
        // packages under bin/ counted as application code; 115 / 10,020 / 10,178 since issue #107
        // leaves them out.
        Assert.True(slice.EntryPoints!.Count >= 90, $"expected >= 90 entry points, got {slice.EntryPoints.Count}");
        Assert.True(slice.Methods!.Count >= 9_000, $"expected >= 9k methods, got {slice.Methods.Count}");
        Assert.True(slice.Methods.Count < 20_000, $"packages under bin/ must not count as application code, got {slice.Methods.Count}");
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

    // Issue #107: a source tree has no *.deps.json in its root, and `methods` took NodaTime's
    // 2,581 methods for application code until one was copied there.
    [SkippableFact]
    public void Corpus_Issue107Nodatime_MethodsLeaveOutPackagesWithOrWithoutARootDepsJson()
    {
        var path = CorpusPathOrSkip("issue107-nodatime");
        using var copy = CorpusCopy.Of(path);

        var before = JsonSerializer.Deserialize<MethodsSlice>(Depscan.Dosai.GetMethods(copy.Path), JsonOptions)!;
        Assert.DoesNotContain(before.Methods!, method => method.FileName == "NodaTime.dll");
        Assert.Contains(before.Methods!, method => method.FileName == "App.dll");
        Assert.Contains(before.Methods!, method => method.FileName == "Lib.dll");
        Assert.True(before.Methods!.Count < 50, $"expected only the application's methods, got {before.Methods.Count}");

        File.Copy(Directory.EnumerateFiles(Path.Combine(copy.Path, "App", "bin"), "App.deps.json", SearchOption.AllDirectories).First(), Path.Combine(copy.Path, "App.deps.json"));
        var after = JsonSerializer.Deserialize<MethodsSlice>(Depscan.Dosai.GetMethods(copy.Path), JsonOptions)!;
        Assert.Equal(
            before.Methods!.Select(method => (method.FileName, method.ClassName, method.Name)).ToList(),
            after.Methods!.Select(method => (method.FileName, method.ClassName, method.Name)).ToList());
        Assert.Equal(before.MethodCalls!.Count, after.MethodCalls!.Count);
    }

    // Issue #107's second report: with no root *.deps.json `dataflows` read MimeKit and kept the
    // application slices into it; with one, the packages and those slices were gone.
    [SkippableFact]
    public void Corpus_Issue107Mailkit_DataFlowsDoNotDependOnARootDepsJson()
    {
        var path = CorpusPathOrSkip("issue107-mailkit");
        using var copy = CorpusCopy.Of(path);

        DataFlowResult Run(DependencyFlowMode mode)
        {
            using var scope = DependencyFlowScope.Apply(mode);
            return DataFlowAnalyzer.Analyze(copy.Path);
        }

        // Each application slice with every node it passes through, by location and name.
        string[] ApplicationSlices(DataFlowResult result)
        {
            var nodes = result.Nodes.ToDictionary(node => node.Id, node => $"{node.FileName}:{node.LineNumber}:{node.ColumnNumber}:{node.Name}", StringComparer.Ordinal);
            return result.Slices
                .Where(slice => slice.Scope != "dependency")
                .Select(slice => $"{nodes[slice.SourceId]}->{nodes[slice.SinkId]}:{slice.SinkArgumentIndex}|{string.Join(",", slice.NodeIds.Select(id => nodes[id]))}")
                .Order(StringComparer.Ordinal)
                .ToArray();
        }

        var without = Run(DependencyFlowMode.Reachable);
        Assert.Contains(without.Slices, slice => slice.Scope != "dependency" && without.Nodes.Single(node => node.Id == slice.SinkId).Name == "Parse");
        Assert.Contains(without.PackageReachability, package => package.Purl.StartsWith("pkg:nuget/MimeKit@", StringComparison.Ordinal));

        // Reachable analyzes a fraction of the package bodies; every application slice it gives
        // also appears in all, nodes through generic package methods included. The promise is a
        // subset (issue #120's dispatch following keeps the bodies a run can execute but the
        // walk takes a call's summary from the member the call names, so an application slice
        // whose value propagation needs a dispatch target's return can still be missing); the
        // test's own subject keeps the exact equality between the two reachable runs below.
        var all = Run(DependencyFlowMode.All);
        Assert.All(ApplicationSlices(without), slice => Assert.Contains(slice, ApplicationSlices(all)));

        File.Copy(Directory.EnumerateFiles(Path.Combine(copy.Path, "Api", "bin"), "Api.deps.json", SearchOption.AllDirectories).First(), Path.Combine(copy.Path, "Api.deps.json"));
        var with = Run(DependencyFlowMode.Reachable);
        Assert.Equal(ApplicationSlices(without), ApplicationSlices(with));
        Assert.Equal(without.Statistics.FilesAnalyzed, with.Statistics.FilesAnalyzed);
        Assert.Equal(without.PackageReachability.Select(package => package.Purl), with.PackageReachability.Select(package => package.Purl));
    }

    // Issue #111: the class library's own bin comes before the app's in one tree and after it in
    // the other, and NodaTime sits only in the app's bin.
    [SkippableTheory]
    [InlineData("App")]
    [InlineData("Web")]
    public void Corpus_Issue111Core_TheLibrarysTypesAreReadWhateverTheAppIsCalled(string app)
    {
        var path = CorpusPathOrSkip(Path.Combine("issue111-core", app));
        var slice = JsonSerializer.Deserialize<MethodsSlice>(Depscan.Dosai.GetMethods(path), JsonOptions)!;

        var core = slice.Methods!.Where(method => method.FileName == "Core.dll").Select(method => $"{method.ClassName}.{method.Name}").ToHashSet(StringComparer.Ordinal);
        Assert.Contains("FixedClock.GetCurrentInstant", core);
        Assert.Contains("FixedClock..ctor", core);
        Assert.Contains("Greeting.Hello", core);
        Assert.Contains(slice.SourceAssemblyMapping!, mapping => mapping.SourceId.StartsWith("Core.FixedClock.GetCurrentInstant", StringComparison.Ordinal));
    }

    // Issue #111 on a real tree: renaming src/API to src/ZAPI moved the app after its libraries
    // and dropped 4,011 application methods. The two trees must now report the same methods;
    // and issue #110's ambiguity count on the same tree.
    [SkippableFact]
    public void Corpus_ModularMonolith_MethodSetDoesNotDependOnADirectoryName()
    {
        var path = CorpusPathOrSkip("modular-monolith-with-ddd/src");
        using var renamed = CorpusCopy.Of(path);
        Directory.Move(Path.Combine(renamed.Path, "API"), Path.Combine(renamed.Path, "ZAPI"));

        var original = JsonSerializer.Deserialize<MethodsSlice>(Depscan.Dosai.GetMethods(path), JsonOptions)!;
        var moved = JsonSerializer.Deserialize<MethodsSlice>(Depscan.Dosai.GetMethods(renamed.Path), JsonOptions)!;

        static List<string> Names(MethodsSlice slice) => slice.Methods!
            .Select(method => $"{method.FileName}|{method.Namespace}|{method.ClassName}|{method.Name}|{method.MetadataToken}")
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(Names(original).Count, Names(moved).Count);
        Assert.Equal(Names(original), Names(moved));
        Assert.Equal(original.SourceAssemblyMapping!.Count, moved.SourceAssemblyMapping!.Count);
        Assert.Equal(original.CallGraph!.Nodes.Count, moved.CallGraph!.Nodes.Count);
        Assert.True(original.Methods!.Count(method => (method.Namespace ?? "").StartsWith("CompanyName.", StringComparison.Ordinal)) > 8_000);
    }

    // Issue #110: the Web SDK API project's Microsoft.Extensions.Logging made `ILogger` ambiguous
    // (79 CS0104 errors) in the libraries that log through Serilog, and their calls stayed
    // unbound: 6 Serilog calls were bound, 92 once <Using Remove> took the using away.
    [SkippableFact]
    public void Corpus_ModularMonolith_SerilogCallsBindBesideTheWebProject()
    {
        var path = CorpusPathOrSkip("modular-monolith-with-ddd/src");
        var slice = JsonSerializer.Deserialize<MethodsSlice>(Depscan.Dosai.GetMethods(path), JsonOptions)!;

        var serilogCalls = slice.MethodCalls!.Count(call => call.CalledMethod.StartsWith("Serilog.ILogger.", StringComparison.Ordinal));
        Assert.True(serilogCalls >= 90, $"expected the Serilog calls to bind, got {serilogCalls}");
        Assert.DoesNotContain(slice.MethodCalls!, call => call.EvidenceKind == AnalysisEvidenceKind.SourceUnresolved && call.CalledMethod is "Information" or "Warning" or "Error" or "Debug");
        Assert.Contains(slice.Diagnostics!, line => line.StartsWith("Global usings differ between projects", StringComparison.Ordinal));
    }

    // Issue #134's whole-directory matrix: the algorithm argument of the one-shot hash APIs is
    // read in every body shape, a decoded MAC argument names the MAC in place of the generic
    // HMAC asset, and the signature probe and the local-algorithm call stay silent.
    [SkippableFact]
    public void Corpus_Issue134WeakHash_TheArgumentIsReadInEveryBodyShape()
    {
        var path = CorpusPathOrSkip("issue134-weakhash");
        var result = CryptoAnalyzer.Analyze(path);

        // (path, line, column, name, family, strength, source) of every asset, and every
        // weak-hash finding line, exactly as measured after the fix.
        (string Path, int Line, int Column, string Name, string Family, string Strength, string Source)[] expectedAssets =
        [
            ("Hashing.cs", 5, 81, "MD5", "hash", "weak", "roslyn"),
            ("Hashing.cs", 9, 49, "MD5", "hash", "weak", "roslyn"),
            ("vb/Hashing.vb", 6, 53, "MD5", "hash", "weak", "roslyn"),
            ("lambda/ParenLambda.cs", 6, 104, "MD5", "hash", "weak", "roslyn"),
            ("hasprobe/HasProbe.cs", 9, 49, "MD5", "hash", "weak", "roslyn"),
            ("boundary/Boundary.cs", 5, 119, "MD5", "hash", "weak", "roslyn"),
            ("boundary/Boundary.cs", 9, 72, "MD5", "hash", "weak", "csharp"),
            ("hmac/Hmac.cs", 6, 105, "HMAC-MD5", "mac", "weak", "roslyn"),
            ("hmac/Hmac.cs", 10, 49, "HMAC-MD5", "mac", "weak", "roslyn"),
            ("hmac/Hmac.cs", 10, 67, "MD5", "hash", "weak", "csharp"),
            ("hmac/Hmac.cs", 13, 98, "HMAC", "mac", "strong", "roslyn"),
        ];
        var actualAssets = result.Assets.Select(asset => (
            asset.Location.Path!, asset.Location.LineNumber, asset.Location.ColumnNumber,
            asset.Name, asset.Family!, asset.Strength!, asset.Properties.TryGetValue("source", out var source) ? source : null)).ToList();
        Assert.Equal(expectedAssets.OrderBy(row => row.Item1, StringComparer.Ordinal).ThenBy(row => row.Item2).ThenBy(row => row.Item3), actualAssets.OrderBy(row => row.Item1, StringComparer.Ordinal).ThenBy(row => row.Item2).ThenBy(row => row.Item3));

        var weakHashLines = result.Findings.Where(finding => finding.RuleId is "DOSAI-CRYPTO-WEAK-HASH-MD5" or "DOSAI-CRYPTO-WEAK-HASH-SHA1")
            .Select(finding => (finding.Location.Path, finding.Location.LineNumber, finding.RuleId)).OrderBy(row => row.Path).ThenBy(row => row.LineNumber).ToList();
        Assert.Equal(
            [("boundary/Boundary.cs", 5, "DOSAI-CRYPTO-WEAK-HASH-MD5"), ("boundary/Boundary.cs", 9, "DOSAI-CRYPTO-WEAK-HASH-MD5"),
             ("Hashing.cs", 5, "DOSAI-CRYPTO-WEAK-HASH-MD5"), ("Hashing.cs", 9, "DOSAI-CRYPTO-WEAK-HASH-MD5"),
             ("hasprobe/HasProbe.cs", 9, "DOSAI-CRYPTO-WEAK-HASH-MD5"),
             ("hmac/Hmac.cs", 10, "DOSAI-CRYPTO-WEAK-HASH-MD5"),
             ("lambda/ParenLambda.cs", 6, "DOSAI-CRYPTO-WEAK-HASH-MD5"),
             ("vb/Hashing.vb", 6, "DOSAI-CRYPTO-WEAK-HASH-MD5")],
            weakHashLines);

        // The negative pins: the signature probe reports nothing, the local-algorithm call
        // reports at its declaration line only, and no generic HMAC sits beside a decoded MAC.
        Assert.DoesNotContain(result.Assets, asset => asset.Location.Path == "hasprobe/HasProbe.cs" && asset.Location.LineNumber == 5);
        Assert.DoesNotContain(result.Findings, finding => finding.Location.Path == "hasprobe/HasProbe.cs" && finding.Location.LineNumber == 5);
        Assert.DoesNotContain(result.Assets, asset => asset.Location.Path == "boundary/Boundary.cs" && asset.Location.LineNumber == 10);
        Assert.DoesNotContain(result.Assets, asset => asset.Location.Path == "hmac/Hmac.cs" && asset.Name == "HMAC" && asset.Location.LineNumber is 6 or 10);

        // Not an empty list (issue #127 adds a base-naming note even to project-less trees);
        // nothing originates from the crypto pass itself.
        Assert.DoesNotContain(result.Diagnostics, line => line.Contains("Crypto reachability fallback", StringComparison.Ordinal) || line.Contains("Crypto data-flow fallback", StringComparison.Ordinal));
    }

    // Issue #130: calls made directly in field and auto-property initializers attribute to
    // the constructors the compiler runs them in, promoting the package to
    // ExternalCallGraphNode without the lambda's help.
    private static MethodsSlice Issue130Slice(string variant)
    {
        var path = CorpusPathOrSkip($"issue130-initcalls/{variant}");
        return JsonSerializer.Deserialize<MethodsSlice>(Depscan.Dosai.GetMethods(path), JsonOptions)!;
    }

    private static List<(int Line, string SourceId, string Called, CallType CallType, AnalysisEvidenceKind Evidence)> Issue130Rows(MethodsSlice slice) => slice.MethodCalls!
        .Where(call => call.CalledMethod is { } called && (called.Contains("SerializeObject", StringComparison.Ordinal) || called.EndsWith("JsonSerializerSettings", StringComparison.Ordinal)))
        .OrderBy(call => call.LineNumber).ThenBy(call => call.SourceId, StringComparer.Ordinal)
        .Select(call => (call.LineNumber, call.SourceId!, call.CalledMethod!, call.CallType, call.EvidenceKind)).ToList();

    [SkippableFact]
    public void Corpus_Issue130_Base_InitializerCallsAttributeToSynthesizedConstructors()
    {
        var slice = Issue130Slice("base");
        Assert.Equal(
            [
                (7, "Repro.Settings..cctor()", "Newtonsoft.Json.JsonConvert.SerializeObject(object?)", CallType.MethodCall, AnalysisEvidenceKind.SourceRoslynDirect),
                (8, "Repro.Settings..ctor()", "JsonSerializerSettings", CallType.ConstructorCall, AnalysisEvidenceKind.SourceRoslynDirect),
                (9, "Repro.Settings..ctor()", "Newtonsoft.Json.JsonConvert.SerializeObject(object?)", CallType.MethodCall, AnalysisEvidenceKind.SourceRoslynDirect),
                (10, "Repro.Settings.<LambdaField>lambda1():string", "Newtonsoft.Json.JsonConvert.SerializeObject(object?)", CallType.MethodCall, AnalysisEvidenceKind.SourceRoslynDirect),
            ],
            Issue130Rows(slice));
        Assert.Equal("ExternalCallGraphNode", SinglePackage(slice, "newtonsoft.json").ReachabilityKind);
        AssertEveryEdgeEndpointExists(slice);
    }

    [SkippableFact]
    public void Corpus_Issue130_NoLambda_ThePackageStaysReachableWithoutTheLambda()
    {
        var slice = Issue130Slice("nolambda");
        Assert.Equal(3, Issue130Rows(slice).Count);
        Assert.All(Issue130Rows(slice), row => Assert.Equal(AnalysisEvidenceKind.SourceRoslynDirect, row.Evidence));
        // The headline regression: the initializer rows alone promote the package (Dependency Low before).
        Assert.Equal("ExternalCallGraphNode", SinglePackage(slice, "newtonsoft.json").ReachabilityKind);
    }

    [SkippableFact]
    public void Corpus_Issue130_NoRestore_InitializerCallsRecordAsSourceUnresolved()
    {
        var slice = Issue130Slice("norestore");
        var rows = Issue130Rows(slice);
        Assert.Equal(4, rows.Count);
        Assert.All(rows, row => Assert.Equal(AnalysisEvidenceKind.SourceUnresolved, row.Evidence));
        Assert.Equal(new[] { "Repro.Settings..cctor()", "Repro.Settings..ctor()", "Repro.Settings..ctor()", "Repro.Settings.<LambdaField>lambda1():string" }.Order(StringComparer.Ordinal), rows.Select(row => row.SourceId).Order(StringComparer.Ordinal));
    }

    [SkippableFact]
    public void Corpus_Issue130_StructInit_StaticAttributed_InstanceDropped()
    {
        var slice = Issue130Slice("structinit");
        var row = Assert.Single(Issue130Rows(slice));
        Assert.Equal((7, "Repro.Settings..cctor()"), (row.Line, row.SourceId));
        // A struct's implicit parameterless constructor never runs initializers, so the line 8
        // instance initializer has no owner and records nothing.
        Assert.Equal("ExternalCallGraphNode", SinglePackage(slice, "newtonsoft.json").ReachabilityKind);
    }

    [SkippableFact]
    public void Corpus_Issue130_AttributeArguments_StayOutOfScope()
    {
        var slice = Issue130Slice("attrarg");
        Assert.Empty(Issue130Rows(slice));
        Assert.Equal("Dependency", SinglePackage(slice, "newtonsoft.json").ReachabilityKind);
    }

    [SkippableFact]
    public void Corpus_Issue130_DeclaredStaticConstructor_OneCctorNodeAndNoPhantomCtor()
    {
        var slice = Issue130Slice("staticctor");
        var row = Assert.Single(Issue130Rows(slice));
        Assert.Equal((11, "Repro.Settings..cctor()"), (row.Line, row.SourceId));
        Assert.Equal(["Repro.Settings..cctor()"], slice.CallGraph!.Nodes.Where(node => node.Id.StartsWith("Repro.Settings..", StringComparison.Ordinal)).Select(node => node.Id).ToList());
        AssertEveryEdgeEndpointExists(slice);
    }

    [SkippableFact]
    public void Corpus_Issue130_Built_TheIlPassAddsNothingToTheInitializerRows()
    {
        var slice = Issue130Slice("built");
        // The IL pass leaves a non-Razor bin/ to source analysis, so the built tree reports the
        // same rows once, no doubling.
        Assert.Equal(4, Issue130Rows(slice).Count);
        Assert.All(Issue130Rows(slice), row => Assert.Equal(AnalysisEvidenceKind.SourceRoslynDirect, row.Evidence));
    }

    [SkippableFact]
    public void Corpus_Issue130_QualifiedNoLambda_BindsThroughTheQualifiedNames()
    {
        var slice = Issue130Slice("qualified-nolambda");
        var rows = Issue130Rows(slice);
        Assert.Equal(3, rows.Count);
        Assert.All(rows, row => Assert.Equal(AnalysisEvidenceKind.SourceRoslynDirect, row.Evidence));
        Assert.Equal("ExternalCallGraphNode", SinglePackage(slice, "newtonsoft.json").ReachabilityKind);
    }

    private static PackageReachability SinglePackage(MethodsSlice slice, string name)
    {
        var packages = slice.PackageReachability!.Where(entry => entry.Purl?.Contains(name, StringComparison.OrdinalIgnoreCase) == true).ToList();
        return Assert.Single(packages);
    }

    // Issue #128: the reachable-mode walk keys members by the declaring type's full nesting
    // chain, so a namespace-less nested type of one package never joins another's. The
    // fixtures hold the app builds (App/bin/Release/net10.0) with LibA/LibB (main, control)
    // and LibP/LibQ (probe) packed as local 1.0.0 packages.
    private static DataFlowResult Issue128Reachable(string tree)
    {
        var path = CorpusPathOrSkip($"issue128-libpacks/{tree}/App/bin/Release/net10.0");
        using var scope = DependencyFlowScope.Apply(DependencyFlowMode.Reachable);
        return DataFlowAnalyzer.Analyze(path);
    }

    [SkippableFact]
    public void Corpus_Issue128_Main_ThePackedLibraryStaysPruned()
    {
        var result = Issue128Reachable("main");

        // Before the qualified keys the note read "5 of 13": LibB's <>c joined LibA's, so
        // LibB.B.NeverCalled was analyzed and its command sink recorded.
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("9 of 13 method bodies in dependency assemblies were not analyzed", StringComparison.Ordinal));
        Assert.DoesNotContain(result.MethodSummaries, summary => summary.Method.Contains("LibB", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Nodes, node => (node.Symbol ?? string.Empty).Contains("LibB", StringComparison.Ordinal));
        Assert.DoesNotContain(result.MethodSummaries, summary => summary.SinkCategories.Contains("command"));
        Assert.Contains(result.MethodSummaries, summary => summary.Method.Contains("LibA.A.Run", StringComparison.Ordinal));
    }

    [SkippableFact]
    public void Corpus_Issue128_Control_NoCollisionStillPrunesTheUncalledPackage()
    {
        var result = Issue128Reachable("control");

        // Qualification must not over-prune where no collision exists: LibA's bodies stay kept.
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("6 of 10 method bodies in dependency assemblies were not analyzed", StringComparison.Ordinal));
        Assert.DoesNotContain(result.MethodSummaries, summary => summary.Method.Contains("LibB", StringComparison.Ordinal));
        Assert.Contains(result.MethodSummaries, summary => summary.Method.Contains("LibA.A.Run", StringComparison.Ordinal));
    }

    [SkippableFact]
    public void Corpus_Issue128_Probe_UserWrittenNestedTypesSeparate()
    {
        var result = Issue128Reachable("probe");

        // LibP's and LibQ's user-written nested Cache classes shared one legacy key; the probe's
        // never-called LibQ.Q.Cache.Local(string) is a Process.Start sink that stayed analyzed.
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("4 of 6 method bodies in dependency assemblies were not analyzed", StringComparison.Ordinal));
        Assert.DoesNotContain(result.MethodSummaries, summary => summary.Method.Contains("LibQ", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Nodes, node => (node.Symbol ?? string.Empty).Contains("LibQ", StringComparison.Ordinal));
        Assert.DoesNotContain(result.MethodSummaries, summary => summary.SinkCategories.Contains("command"));
        Assert.Contains(result.MethodSummaries, summary => summary.Method.Contains("LibP.P.Run", StringComparison.Ordinal));
    }

    // Issue #120: the reachable walk follows interface and virtual dispatch to the types
    // reached code instantiates, so the flows through the package code a run can execute come
    // back while the dead formatters stay out.
    [SkippableFact]
    public void Corpus_Issue120_Serilog_ReachableFollowsInterfaceDispatch()
    {
        var path = CorpusPathOrSkip("issue120-serilog/App");
        DataFlowResult reachable;
        using (DependencyFlowScope.Apply(DependencyFlowMode.Reachable))
        {
            reachable = DataFlowAnalyzer.Analyze(path);
        }

        // The console sink's package and renderer bodies come back (both were gone before:
        // the walk stopped at the interface call and the diagnostic read 952 of 1190).
        Assert.Contains(reachable.PackageReachability, package => package.Purl == "pkg:nuget/Serilog.Sinks.Console@6.1.1");
        Assert.Equal(2, reachable.Nodes.Count(node => (node.Symbol ?? string.Empty).Contains("ExceptionTokenRenderer.Render", StringComparison.Ordinal)));
        Assert.Contains(reachable.Nodes, node => (node.Symbol ?? string.Empty).Contains("Serilog.", StringComparison.Ordinal));
        // The dead formatters nothing constructs stay out, with no class-hierarchy fallback.
        Assert.DoesNotContain(reachable.Nodes, node => (node.Symbol ?? string.Empty).Contains("MessageTemplateTextFormatter", StringComparison.Ordinal)
            || (node.Symbol ?? string.Empty).Contains("JsonFormatter", StringComparison.Ordinal)
            || (node.Symbol ?? string.Empty).Contains("LogEvent.Copy", StringComparison.Ordinal));
        // The measured prune note: 952 of 1190 before rapid type analysis, 780 with it.
        Assert.Contains(reachable.Diagnostics, diagnostic => diagnostic.Contains("780 of 1190 method bodies in dependency assemblies were not analyzed", StringComparison.Ordinal));

        // Every reachable application slice appears in all, and the anchored set is not empty.
        DataFlowResult all;
        using (DependencyFlowScope.Apply(DependencyFlowMode.All))
        {
            all = DataFlowAnalyzer.Analyze(path);
        }

        string[] ApplicationSlices(DataFlowResult result)
        {
            var nodes = result.Nodes.ToDictionary(node => node.Id, node => $"{node.FileName}:{node.LineNumber}:{node.ColumnNumber}:{node.Name}", StringComparer.Ordinal);
            return result.Slices
                .Where(slice => slice.Scope != "dependency")
                .Select(slice => $"{nodes[slice.SourceId]}->{nodes[slice.SinkId]}:{slice.SinkArgumentIndex}|{string.Join(",", slice.NodeIds.Select(id => nodes[id]))}")
                .Order(StringComparer.Ordinal)
                .ToArray();
        }

        var reachableSlices = ApplicationSlices(reachable);
        // One anchored slice here: the cross-pass fingerprint (issue #131) also collapses the
        // duplicate the IL pass used to emit for the same flow, so the count is the distinct
        // flows, not the passes' copies of them.
        Assert.True(reachableSlices.Length >= 1, $"expected anchored slices, got {reachableSlices.Length}");
        Assert.All(reachableSlices, slice => Assert.Contains(slice, ApplicationSlices(all)));
    }

    // Issue #120's boundary probe: the app instantiates MessageTemplateTextFormatter itself and
    // calls Format through ITextFormatter, a flow reachable lost entirely before.
    [SkippableFact]
    public void Corpus_Issue120_Serilog_Probe_InstantiatedFormatterFlowsAnchor()
    {
        var path = CorpusPathOrSkip("issue120-serilog/probe");
        DataFlowResult result;
        using (DependencyFlowScope.Apply(DependencyFlowMode.Reachable))
        {
            result = DataFlowAnalyzer.Analyze(path);
        }

        var nodes = result.Nodes.ToDictionary(node => node.Id, node => node, StringComparer.Ordinal);
        Assert.Contains(result.Slices, slice =>
        {
            if (slice.Scope == "dependency" || nodes[slice.SourceId].FileName?.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) != true)
            {
                return false;
            }

            var sink = nodes[slice.SinkId];
            return (sink.Symbol ?? string.Empty).StartsWith("Serilog.Formatting.ITextFormatter.Format(", StringComparison.Ordinal)
                || (sink.Symbol ?? string.Empty).Contains("MessageTemplateTextFormatter.Format", StringComparison.Ordinal);
        });
    }

    // Issue #131: the source data-flow pass finds the flows of an unbuilt tree that live in
    // initializers, expression bodies and a primary constructor's base arguments, and a built
    // tree reports each flow exactly once: the source pass claims its flows and the assembly
    // pass drops the duplicates through the cross-pass fingerprint.
    private static List<(int SourceLine, int SinkLine, string Evidence)> Issue131Flows(string tree)
    {
        var path = CorpusPathOrSkip(tree);
        var result = JsonSerializer.Deserialize<DataFlowResult>(DataFlowAnalyzer.GetDataFlows(path), JsonOptions)!;
        var nodes = result.Nodes.ToDictionary(node => node.Id, node => node, StringComparer.Ordinal);
        return result.Slices
            .Select(slice => (nodes[slice.SourceId].LineNumber, nodes[slice.SinkId].LineNumber, slice.Summary?.StartsWith("Assembly IL", StringComparison.Ordinal) == true ? "assembly-il" : "source"))
            .Order()
            .ToList();
    }

    [SkippableFact]
    public void Corpus_Issue131_InitFlows_Unbuilt()
    {
        var flows = Issue131Flows("issue131-initflows");
        Assert.Equal(
            [(7, 8, "source"), (9, 9, "source"), (11, 11, "source"), (12, 12, "source"), (13, 13, "source"), (21, 21, "source")],
            flows);
    }

    [SkippableFact]
    public void Corpus_Issue131_InitFlows_Built_EachFlowOnce()
    {
        var flows = Issue131Flows("issue131-initflows-built");
        Assert.Equal(
            [(7, 8, "source"), (9, 9, "source"), (11, 11, "source"), (12, 12, "source"), (13, 13, "source"), (21, 21, "source")],
            flows);
    }

    [SkippableFact]
    public void Corpus_Issue131_InitFlows_Probe_TheCrossMemberFlowAppearsOnceInEachCopy()
    {
        var unbuilt = Issue131Flows("issue131-initflows-probe");
        var built = Issue131Flows("issue131-initflows-probe-built");
        Assert.Equal([(7, 11, "source")], unbuilt);
        // The static field's taint reaches the block-bodied reader from the initializer's own
        // line, with identical lines between the built and unbuilt copies.
        Assert.Equal([(7, 11, "source")], built);
    }

    // Issue #129: after same-site collapsing, every PackageReachability EdgeId names an edge
    // of the final CallGraph; the surviving ids' order and the SourceLocations are unchanged.
    private static void AssertNoDanglingEdgeIds(MethodsSlice slice)
    {
        var edgeIds = slice.CallGraph!.Edges.Select(edge => edge.Id).ToHashSet(StringComparer.Ordinal);
        Assert.All(slice.PackageReachability!, entry => Assert.All(entry.EdgeIds, id => Assert.Contains(id, edgeIds)));
    }

    [SkippableFact]
    public void Corpus_Issue129_Threecalls_EdgeIdsNameFinalEdges()
    {
        var path = CorpusPathOrSkip("issue129-edgeids/threecalls");
        var slice = JsonSerializer.Deserialize<MethodsSlice>(Depscan.Dosai.GetMethods(path), JsonOptions)!;
        var entry = Assert.Single(slice.PackageReachability!, package => package.Purl == "pkg:nuget/Newtonsoft.Json@13.0.3");
        Assert.Equal(["e1", "e2"], entry.EdgeIds);
        Assert.Equal(
            [("Program.cs", 1, 1, "Dependency"), ("Program.cs", 3, 9, "CallGraphEdge"), ("Program.cs", 4, 9, "CallGraphEdge"), ("Program.cs", 5, 9, "CallGraphEdge"), ("Program.cs", 6, 5, "CallGraphEdge")],
            entry.SourceLocations.Select(location => (location.Path!, location.LineNumber, location.ColumnNumber, location.Kind)).ToList());
        Assert.Equal(3, Assert.Single(slice.CallGraph!.Edges, edge => edge.Id == "e2").CallSiteCount);
        AssertNoDanglingEdgeIds(slice);
    }

    [SkippableFact]
    public void Corpus_Issue129_Root_EdgeIdsNameFinalEdges()
    {
        var path = CorpusPathOrSkip("issue129-edgeids");
        var slice = JsonSerializer.Deserialize<MethodsSlice>(Depscan.Dosai.GetMethods(path), JsonOptions)!;
        var entry = Assert.Single(slice.PackageReachability!, package => package.Purl == "pkg:nuget/Newtonsoft.Json@13.0.3");
        Assert.Equal(["e1", "e2"], entry.EdgeIds);
        var locations = entry.SourceLocations
            .Select(location => (NormalizeSeparator(location.Path!), location.LineNumber, location.ColumnNumber, location.Kind))
            .Order()
            .ToList();
        Assert.Equal(
            new[] { ("Program.cs", 1, 1, "Dependency"), ("Program.cs", 3, 9, "CallGraphEdge"), ("Program.cs", 4, 9, "CallGraphEdge"), ("threecalls/Program.cs", 1, 1, "Dependency"), ("threecalls/Program.cs", 5, 9, "CallGraphEdge"), ("threecalls/Program.cs", 6, 5, "CallGraphEdge") }.Order().ToList(),
            locations);
        Assert.Equal("e5", Assert.Single(slice.PackageReachability!, package => package.Purl == "pkg:nuget/System.Console").EdgeIds.Single());
        AssertNoDanglingEdgeIds(slice);

        static string NormalizeSeparator(string path) => path.Replace('\\', '/');
    }

    // Issue #110's repro: with the app and the library in one tree the library's call binds
    // as it does when the library is scanned alone.
    [SkippableFact]
    public void Corpus_Issue110Serilog_TheLibraryCallBindsLikeTheLibraryScannedAlone()
    {
        var path = CorpusPathOrSkip("issue110-serilog");

        string Greet(string scanned) => Assert.Single(
            JsonSerializer.Deserialize<MethodsSlice>(Depscan.Dosai.GetMethods(scanned), JsonOptions)!.MethodCalls!,
            call => call.Path.EndsWith("Greeter.cs", StringComparison.Ordinal) && call.CalledMethod.Contains("Information", StringComparison.Ordinal)).CalledMethod;

        Assert.Equal("Serilog.ILogger.Information<string>(string, string)", Greet(path));
        Assert.Equal(Greet(Path.Combine(path, "Lib")), Greet(path));
    }

    // Issue #106: crypto takes the methods and data-flow reports written for the same tree
    // instead of analysing it again; what it reports does not change.
    [SkippableFact]
    public void Corpus_ModularMonolithUserAccess_CryptoWithReusedReportsMatchesFromScratch()
    {
        var path = CorpusPathOrSkip("modular-monolith-with-ddd/src/Modules/UserAccess");
        var directory = Path.Combine(Path.GetTempPath(), "dosai-corpus-reports-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var methodsReport = Path.Combine(directory, "methods.json");
            var dataFlowsReport = Path.Combine(directory, "dataflows.json");
            File.WriteAllText(methodsReport, Depscan.Dosai.GetMethods(path));
            File.WriteAllText(dataFlowsReport, DataFlowAnalyzer.GetDataFlows(path));

            var scratch = CryptoAnalyzer.Analyze(path);
            var reused = CryptoAnalyzer.Analyze(path, null, BuildPreparationMode.None, CryptoDataFlowDetail.Full, methodsReport, dataFlowsReport);

            Assert.Contains(reused.Diagnostics, line => line.StartsWith("The methods slice was taken from the report", StringComparison.Ordinal));
            Assert.Contains(reused.Diagnostics, line => line.StartsWith("The crypto data flows were taken from the data-flow report", StringComparison.Ordinal));
            Assert.True(scratch.Statistics.AssetCount > 0, "expected crypto evidence in the UserAccess module");
            Assert.Equal(Issue106ReportReuseTests.Evidence(scratch), Issue106ReportReuseTests.Evidence(reused));
            Assert.Equal(scratch.Statistics.CryptoDataFlowSliceCount, reused.Statistics.CryptoDataFlowSliceCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // Issue #98: calls in .razor and .cshtml files exist only in the build. The calls below are the
    // ones the issue's repros add; the lines come from the Razor compiler's PDB mapping, so they
    // are pinned for the SDK that built the corpus (see Corpus/README.md).
    [SkippableTheory]
    [InlineData("issue98/blazor", "Components/Pages/Counter.razor", 10, 14, "ToString", "BuildRenderTree", 21, 9, "SerializeObject", "IncrementCount")]
    [InlineData("issue98/blazorwasm", "Pages/Counter.razor", 9, 14, "ToString", "BuildRenderTree", 20, 9, "SerializeObject", "IncrementCount")]
    [InlineData("issue98/webapp", "Pages/Privacy.cshtml", 10, 14, "ToString", "ExecuteAsync", 17, 9, "SerializeObject", "Snapshot")]
    [InlineData("issue98/mvc", "Views/Home/Privacy.cshtml", 8, 14, "ToString", "ExecuteAsync", 9, 12, "SerializeObject", "ExecuteAsync")]
    public void Corpus_Issue98_TemplateCallsAreListedBesideTheSourceCall(string app, string template, int markupLine, int markupColumn, string markupCall, string markupCaller, int codeLine, int codeColumn, string codeCall, string codeCaller)
    {
        var path = CorpusPathOrSkip(app);
        var slice = JsonSerializer.Deserialize<MethodsSlice>(Depscan.Dosai.GetMethods(path), JsonOptions)!;
        var convert = slice.MethodCalls!.Where(call => call.ClassName == "JsonConvert").ToList();

        // The .cs call the source analysis always listed, once, and exactly the two the issue misses.
        var parse = Assert.Single(convert, call => call.Path == "Services/CountParser.cs");
        Assert.Equal((8, 9, AnalysisEvidenceKind.SourceRoslynDirect), (parse.LineNumber, parse.ColumnNumber, parse.EvidenceKind));
        var rows = convert.Where(call => call.Path == template).OrderBy(call => call.LineNumber).ToList();
        Assert.Equal(2, convert.Count - 1);
        Assert.Equal(
            [(markupLine, markupColumn, markupCall, markupCaller), (codeLine, codeColumn, codeCall, codeCaller)],
            rows.Select(call => (call.LineNumber, call.ColumnNumber, call.CalledMethod, call.CallerMethod)));
        // A view's ExecuteAsync is an async method: its calls come from the generated state machine.
        Assert.All(rows, call => Assert.Equal(
            (call.CallerMethod == "ExecuteAsync" ? AnalysisEvidenceKind.AssemblyIlGeneratedState : AnalysisEvidenceKind.AssemblyIlDirect, Path.GetFileName(template), "pkg:nuget/Newtonsoft.Json@13.0.3"),
            (call.EvidenceKind, call.FileName, call.Purl)));
        // The call graph carries the same rows, on existing nodes.
        foreach (var call in rows)
        {
            Assert.Contains(slice.CallGraph!.Edges, edge => edge.SourceId == call.SourceId && edge.TargetId == call.TargetId && edge.Path == template && edge.CallLocation.LineNumber == call.LineNumber);
        }

        AssertEveryEdgeEndpointExists(slice);
    }

    // Issue #98: a component library copied into the app's bin/ gives its template calls once, a
    // component's .razor.cs code-behind stays a source call listed once, and a method declared
    // in a template has one node.
    [SkippableFact]
    public void Corpus_Issue98Components_LibraryCopiesAndCodeBehindGiveEachCallOnce()
    {
        var path = CorpusPathOrSkip("issue98/components");
        var slice = JsonSerializer.Deserialize<MethodsSlice>(Depscan.Dosai.GetMethods(path), JsonOptions)!;
        var calls = slice.MethodCalls!;

        var behind = Assert.Single(calls, call => call.Path == "Host/Components/Pages/Behind.razor.cs" && call.CalledMethod.Contains("SerializeObject", StringComparison.Ordinal));
        Assert.Equal((11, AnalysisEvidenceKind.SourceRoslynDirect), (behind.LineNumber, behind.EvidenceKind));
        Assert.DoesNotContain(calls, call => call.Path == "Host/Components/Pages/Behind.razor.cs" && call.EvidenceKind.ToString().StartsWith("AssemblyIl", StringComparison.Ordinal));

        (int Line, string Called, string Caller)[] Rows(string template) => calls
            .Where(call => call.Path == template && call.ClassName == "JsonConvert")
            .OrderBy(call => call.LineNumber)
            .Select(call => (call.LineNumber, call.CalledMethod, call.CallerMethod))
            .ToArray();
        Assert.Equal([(8, "ToString", "BuildRenderTree"), (12, "SerializeObject", "Label")], Rows("Host/Components/Pages/Behind.razor"));
        Assert.Equal([(3, "ToString", "BuildRenderTree"), (8, "SerializeObject", "Describe")], Rows("Widgets/Badge.razor"));

        // One node for each method a template declares, whichever copy of the library was read.
        // Label lives only in the template, so its node keeps the IL rendering of the signature.
        Assert.Single(slice.CallGraph!.Nodes, node => node.Id == "Host.Components.Pages.Behind.Label():System.String");
        Assert.Single(slice.CallGraph.Nodes, node => node.Id.StartsWith("Widgets.Badge.Describe()", StringComparison.Ordinal));
        AssertEveryEdgeEndpointExists(slice);
    }

    // Issue #98 on real apps: the template pass adds Razor rows and moves nothing else, whatever the
    // worker count.
    [SkippableTheory]
    [InlineData("eShopOnWeb/src/Web")]
    [InlineData("eShopOnWeb/src/BlazorAdmin")]
    public void Corpus_Issue98RealApps_TemplateRowsAddToTheSourceRowsAndMoveNothing(string app)
    {
        var path = CorpusPathOrSkip(app);
        static bool IsTemplateRow(MethodCalls call) => call.Path is { } p && (p.EndsWith(".razor", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase));
        static string Key(MethodCalls call) => $"{call.Path}|{call.LineNumber}|{call.ColumnNumber}|{call.SourceId}|{call.TargetId}|{call.EvidenceKind}";

        var with = JsonSerializer.Deserialize<MethodsSlice>(Depscan.Dosai.GetMethods(path), JsonOptions)!;
        MethodsSlice without;
        using (RazorBuilds.DisableForTesting())
        {
            without = JsonSerializer.Deserialize<MethodsSlice>(Depscan.Dosai.GetMethods(path), JsonOptions)!;
        }

        Assert.DoesNotContain(without.MethodCalls!, IsTemplateRow);
        Assert.True(with.MethodCalls!.Count(IsTemplateRow) >= 300, $"expected template rows, got {with.MethodCalls!.Count(IsTemplateRow)}");
        // The compiler's hidden plumbing (element and attribute writes, tag-helper scopes) gives no row.
        Assert.DoesNotContain(with.MethodCalls!.Where(IsTemplateRow), call => call.CalledMethod is "OpenElement" or "CloseElement" or "get___tagHelperScopeManager");
        Assert.Equal(without.MethodCalls!.Select(Key), with.MethodCalls!.Where(call => !IsTemplateRow(call)).Select(Key));
        AssertEveryEdgeEndpointExists(with);

        var previous = Depscan.Dosai.MaxSymbolAnalysisWorkers;
        try
        {
            string Run(int workers)
            {
                Depscan.Dosai.MaxSymbolAnalysisWorkers = workers;
                return System.Text.RegularExpressions.Regex.Replace(Depscan.Dosai.GetMethods(path), "\"GeneratedAt\":\"[^\"]*\"", "\"GeneratedAt\":\"\"");
            }

            Assert.Equal(Run(1), Run(Math.Max(2, Environment.ProcessorCount)));
        }
        finally
        {
            Depscan.Dosai.MaxSymbolAnalysisWorkers = previous;
        }
    }

    // Issue #98: `methods` and `dataflows` place a call of a template alike. The data-flow IL pass
    // reads bin/ whole, so its nodes on a template are there already; each one that names a call
    // sits where the methods row for that call sits, on the same relative path.
    [SkippableTheory]
    [InlineData("issue98/blazor")]
    [InlineData("issue98/webapp")]
    public void Corpus_Issue98_DataFlowNodesOnATemplateSitWhereTheMethodsRowsSit(string app)
    {
        var path = CorpusPathOrSkip(app);
        var slice = JsonSerializer.Deserialize<MethodsSlice>(Depscan.Dosai.GetMethods(path), JsonOptions)!;
        var flows = JsonSerializer.Deserialize<DataFlowResult>(DataFlowAnalyzer.GetDataFlows(path), JsonOptions)!;
        static bool IsTemplate(string? p) => RazorBuilds.IsTemplate(p);

        var rows = slice.MethodCalls!.Where(call => IsTemplate(call.Path)).Select(call => (call.Path, call.LineNumber, call.ColumnNumber, call.CalledMethod)).ToHashSet();
        var calledNames = rows.Select(row => row.CalledMethod).ToHashSet(StringComparer.Ordinal);
        var nodes = flows.Nodes
            .Where(node => IsTemplate(node.Path) && node.Properties.GetValueOrDefault("analysis") == "assembly-il" && calledNames.Contains(node.Name))
            .Select(node => (node.Path, node.LineNumber, node.ColumnNumber, node.Name))
            .ToList();

        Assert.NotEmpty(nodes);
        Assert.All(nodes, node => Assert.Contains(node, rows));
    }

    private static void AssertEveryEdgeEndpointExists(MethodsSlice slice)
    {
        var nodeIds = slice.CallGraph!.Nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        Assert.All(slice.CallGraph.Edges, edge => Assert.True(nodeIds.Contains(edge.SourceId) && nodeIds.Contains(edge.TargetId), $"{edge.SourceId} -> {edge.TargetId}"));
    }

    /// <summary>A throwaway copy of a corpus app, so a test can add files to the tree without touching the corpus.</summary>
    private sealed class CorpusCopy : IDisposable
    {
        private CorpusCopy(string path) => Path = path;

        public string Path { get; }

        public static CorpusCopy Of(string source)
        {
            var target = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dosai-corpus-" + Guid.NewGuid().ToString("N"));
            CopyDirectory(source, target);
            return new CorpusCopy(target);
        }

        private static void CopyDirectory(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (var file in Directory.EnumerateFiles(source))
            {
                File.Copy(file, System.IO.Path.Combine(target, System.IO.Path.GetFileName(file)));
            }

            foreach (var directory in Directory.EnumerateDirectories(source))
            {
                CopyDirectory(directory, System.IO.Path.Combine(target, System.IO.Path.GetFileName(directory)));
            }
        }

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
