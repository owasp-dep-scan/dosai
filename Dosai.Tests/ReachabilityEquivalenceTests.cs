using System.Text.Json;
using Depscan;
using Xunit;

namespace Dosai.Tests;

// The int-graph reachability walks must produce exactly what the string-keyed walks did. Same
// xunit collection as the rest of DosaiTests: these flip the static worker knob.
public partial class DosaiTests
{
    private static readonly AnalysisEvidenceKind[] RandomEvidenceKinds =
        [AnalysisEvidenceKind.SourceRoslynDirect, AnalysisEvidenceKind.FrameworkModel, AnalysisEvidenceKind.ReflectionHeuristic, AnalysisEvidenceKind.AssemblyIlDirect];

    /// <summary>
    ///     A random graph with everything the walks special-case: edge endpoints that are not
    ///     nodes, self-loops, the same pair under several call types and evidence kinds, cycles,
    ///     ids holding control characters, keep-alive evidence on edges and nodes, and entry
    ///     points that repeat an id, repeat a start, or name no node.
    /// </summary>
    private static (CallGraph Graph, List<EntryPoint> EntryPoints) RandomReachabilityGraph(Random rng, int nodeCount, int edgeCount, int entryCount)
    {
        var ids = Enumerable.Range(0, nodeCount).Select(i => rng.Next(25) == 0 ? $"Ns.T{i % 5}.M{i}\n():void" : $"Ns.T{i % 5}.M{i}():void").ToArray();
        var outside = Enumerable.Range(0, Math.Max(1, nodeCount / 8)).Select(i => $"Ext.X{i}():void").ToArray();
        var nodes = ids.Select((id, i) => (i % 11) switch
        {
            0 => ReachabilityNode(id, new MethodIdentity { Evidence = [AnalysisEvidenceKind.ReflectionHeuristic] }, AnalysisEvidenceKind.FrameworkModel),
            1 => ReachabilityNode(id, null, AnalysisEvidenceKind.ReflectionHeuristic),
            _ => ReachabilityNode(id)
        }).ToList();
        string Endpoint() => rng.Next(7) == 0 ? outside[rng.Next(outside.Length)] : ids[rng.Next(ids.Length)];
        var edges = new List<MethodCallEdge>(edgeCount);
        for (var i = 0; i < edgeCount; i++)
        {
            var source = Endpoint();
            edges.Add(new MethodCallEdge
            {
                Id = $"e{i}",
                SourceId = source,
                TargetId = rng.Next(10) == 0 ? source : Endpoint(),
                CallType = rng.Next(3) == 0 ? CallType.ConstructorCall : CallType.MethodCall,
                EvidenceKind = RandomEvidenceKinds[rng.Next(RandomEvidenceKinds.Length)],
                CallLocation = new CallLocation { FileName = "/src/C.cs", LineNumber = rng.Next(1, 40), ColumnNumber = 1 },
                Evidence = rng.Next(5) == 0 ? [new AnalysisEvidence { Kind = RandomEvidenceKinds[rng.Next(RandomEvidenceKinds.Length)] }] : [],
            });
        }

        var entryPoints = new List<EntryPoint>();
        for (var i = 0; i < entryCount; i++)
        {
            entryPoints.Add(new EntryPoint
            {
                Id = $"ep{rng.Next(Math.Max(1, entryCount - 1))}",
                Kind = "Test",
                MethodId = rng.Next(10) switch
                {
                    0 => null,
                    1 => " ",
                    2 => outside[rng.Next(outside.Length)],
                    3 => "Ns.Unknown()",
                    _ => ids[rng.Next(ids.Length)]
                }
            });
        }

        var graph = new CallGraph { Nodes = nodes, Edges = edges };
        if (rng.Next(2) == 0)
        {
            ReachabilityAnalyzer.CollapseDuplicateCallSites(graph);
        }

        return (graph, entryPoints);
    }

    private static string ReachabilityResult((List<NodeReachability> Nodes, List<RecursionCluster> Clusters, bool BudgetExhausted) result, List<string> diagnostics)
        => JsonSerializer.Serialize(new { result.Nodes, result.Clusters, result.BudgetExhausted, Diagnostics = diagnostics }, JsonStringEnums);

    private static void AssertReachabilityMatchesReference(CallGraph graph, List<EntryPoint> entryPoints, ReachabilityAnalyzer.Limits limits, string context)
    {
        var expectedDiagnostics = new List<string>();
        var expected = ReachabilityResult(ReferenceReachability.Compute(graph, entryPoints, expectedDiagnostics, limits), expectedDiagnostics);
        var actualDiagnostics = new List<string>();
        var actual = ReachabilityResult(ReachabilityAnalyzer.Compute(graph, entryPoints, actualDiagnostics, limits), actualDiagnostics);
        Assert.True(expected == actual, $"{context}: the int-graph walks differ from the string-keyed ones\nexpected {expected}\nactual   {actual}");
    }

    [Fact]
    public void Compute_MatchesTheStringKeyedWalksOnRandomGraphsAndBudgets()
    {
        // Tiny budgets make every cut-off reachable: an entry-point walk or a bucket walk that
        // ends exactly on its budget with nothing, only repeats, or new vertices still queued,
        // and the bucket path decision on either side of the bitset cap.
        for (var seed = 0; seed < 1500; seed++)
        {
            var rng = new Random(seed);
            var (graph, entryPoints) = RandomReachabilityGraph(rng, rng.Next(1, 40), rng.Next(0, 130), rng.Next(0, 9));
            var limits = new ReachabilityAnalyzer.Limits(
                VisitedPerEntryPoint: rng.Next(4) == 0 ? ReachabilityAnalyzer.MaxVisitedPerEntryPoint : rng.Next(1, 16),
                ReachableCountBudget: rng.Next(4) == 0 ? ReachabilityAnalyzer.MaxReachableCountBudget : rng.Next(1, 150),
                BucketBitsetBytes: rng.Next(3) switch { 0 => 0, 1 => long.MaxValue, _ => rng.Next(0, 400) });
            AssertReachabilityMatchesReference(graph, entryPoints, limits, $"seed {seed}");
        }
    }

    [Fact]
    public void Compute_ExactBucketsMatchTheBitsetUnionsOnLargerGraphs()
    {
        // Counts on both sides of every bucket boundary, saturation past 1,000 reached through
        // long chains, cycles and shared tails, and component lists released while still shared.
        for (var seed = 0; seed < 6; seed++)
        {
            var rng = new Random(9000 + seed);
            var (graph, entryPoints) = RandomReachabilityGraph(rng, 2500, 2400 + seed * 400, 3);
            AssertReachabilityMatchesReference(graph, entryPoints, new ReachabilityAnalyzer.Limits(ReachabilityAnalyzer.MaxVisitedPerEntryPoint, ReachabilityAnalyzer.MaxReachableCountBudget, long.MaxValue), $"seed {9000 + seed}");
        }
    }

    [Fact]
    public void Compute_EntryWalksOnTheWorkerTeamMatchTheSequentialStringKeyedWalks()
    {
        // Big enough for the team (walks x vertices past the threshold), with walks cut off by the
        // budget, so the fold-in-entry-order path and the once-only budget diagnostic both run.
        var rng = new Random(65065);
        var (graph, entryPoints) = RandomReachabilityGraph(rng, 6000, 15000, 300);
        var limits = new ReachabilityAnalyzer.Limits(700, 40_000, ReachabilityAnalyzer.MaxBucketBitsetBytes);
        foreach (var workers in new[] { 1, 3, 8 })
        {
            WithSymbolAnalysisWorkers(workers, () =>
            {
                AssertReachabilityMatchesReference(graph, entryPoints, limits, $"{workers} workers");
                return 0;
            });
        }
    }
}
