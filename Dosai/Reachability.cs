using System.Globalization;
using System.Text.RegularExpressions;

namespace Depscan;

/// <summary>
///     Normalization helpers for graph node ids shared by source/assembly id matching: IL ids
///     embed generic instantiations (<c>Method&lt;args&gt;</c>), source ids use the original definition.
/// </summary>
internal static partial class GraphIdNormalizer
{
    [GeneratedRegex(@"<[^<>]*>", RegexOptions.Compiled)]
    private static partial Regex GenericArgumentGroupRegex();

    [GeneratedRegex(@"`\d+", RegexOptions.Compiled)]
    private static partial Regex GenericArityMarkerRegex();

    public static string StripGenericInstantiation(string typeName)
    {
        var current = typeName.Replace("global::", string.Empty, StringComparison.Ordinal).Replace("Global.", string.Empty, StringComparison.Ordinal).Replace('+', '.').Trim();
        // Remove innermost <...> groups until stable (handles nesting), then arity markers.
        for (var previous = string.Empty; previous != current;)
        {
            previous = current;
            current = GenericArgumentGroupRegex().Replace(current, string.Empty);
        }

        return GenericArityMarkerRegex().Replace(current, string.Empty);
    }

    public static bool HasGenericInstantiation(string id) => id.Contains('<', StringComparison.Ordinal) || id.Contains('`', StringComparison.Ordinal);

    /// <summary>
    ///     Identity of a method id independent of generic instantiation and parameter types:
    ///     <c>Ns.G`1&lt;System.String&gt;.Echo(System.String):System.String</c> and
    ///     <c>Ns.G.Echo(T):T</c> both reduce to <c>Ns.G.Echo</c>. Used only as a fallback for
    ///     ids that provably embed an instantiation, so overloads defined directly (not via
    ///     instantiation) never merge.
    /// </summary>
    public static string MethodIdentityKey(string id)
    {
        var stripped = StripGenericInstantiation(id);
        var parameterStart = stripped.IndexOf('(', StringComparison.Ordinal);
        return parameterStart > 0 ? stripped[..parameterStart] : stripped;
    }
}

/// <summary>
///     Per-node reachability facts computed once over the merged call graph: which entry
///     points can reach the node, how deep it sits, how much code it can reach, plus fan-in/
///     fan-out and SCC membership.
/// </summary>
public sealed class NodeReachability
{
    public required string NodeId { get; set; }

    /// <summary>Ids of the entry points with a forward graph path to this node (bounded set).</summary>
    public List<string> ReachableEntryPoints { get; set; } = [];

    /// <summary>Bucketed size of the forward-reachable set: 1, 10, 100, 1000, or 10000 (upper bound).</summary>
    public int ReachableNodeBucket { get; set; }

    /// <summary>Minimum call distance from the nearest reachable entry point; null when unreachable.</summary>
    public int? DepthFromEntryPoint { get; set; }

    /// <summary>Distinct callers.</summary>
    public int FanIn { get; set; }

    /// <summary>Distinct callees.</summary>
    public int FanOut { get; set; }

    /// <summary>
    ///     True when a forward graph path from a resolvable entry point reaches this node.
    ///     Independent of the bounded <see cref="ReachableEntryPoints"/> list: the list saturates
    ///     at 16 entry points, the flag never does.
    /// </summary>
    public bool Reachable { get; set; }

    /// <summary>
    ///     True when the node is unreachable but must not be reported as dead code:
    ///     reflection or DI/framework-model evidence keeps it callable at runtime.
    /// </summary>
    public bool KeepAlive { get; set; }

    /// <summary>Why the node is kept alive despite being unreachable (evidence kinds).</summary>
    public List<string> KeepAliveReasons { get; set; } = [];

    /// <summary>
    ///     Strongly-connected-component id. Multi-node components share a non-negative id; plain
    ///     singletons are -1; every self-loop gets its own unique negative id so grouping by SccId
    ///     never merges unrelated self-recursive methods.
    /// </summary>
    public int SccId { get; set; } = -1;

    public bool InRecursiveCycle { get; set; }
}

/// <summary>A recursion cluster: an SCC with more than one member, or a self-loop.</summary>
public sealed class RecursionCluster
{
    public required string Id { get; set; }

    public int Size { get; set; }

    /// <summary>Bounded member list (≤ <see cref="ReachabilityAnalyzer.MaxClusterMembers"/>); full membership is derivable from NodeReachability.SccId.</summary>
    public List<string> MemberIds { get; set; } = [];
}

/// <summary>
///     A method or constructor no entry point can reach and no reflection/DI evidence keeps
///     alive, dead code from the attacker's point of view (and from the maintainer's).
/// </summary>
public sealed class DeadCodeEntry
{
    public required string NodeId { get; set; }
    public string? Name { get; set; }
    public string? ClassName { get; set; }
    public string? Namespace { get; set; }
    /// <summary>Method or Constructor.</summary>
    public string Kind { get; set; } = "Method";
    public string? FileName { get; set; }
    public int LineNumber { get; set; }
    public int ColumnNumber { get; set; }
}

public static class ReachabilityAnalyzer
{
    /// <summary>Per-entry-point BFS visited budget; exceeding it emits a diagnostic instead of hanging.</summary>
    public const int MaxVisitedPerEntryPoint = 200_000;

    /// <summary>Total visited-cell budget for the degraded (non-condensed) reachable-count path; large graphs degrade to bucket 0 + a diagnostic.</summary>
    public const long MaxReachableCountBudget = 20_000_000;

    /// <summary>
    ///     Size cap of the exact condensed bucketing, measured as the per-component node bitsets it
    ///     used to allocate (components x nodes bits); beyond it the budgeted fallback runs instead.
    /// </summary>
    public const long MaxBucketBitsetBytes = 256L * 1024 * 1024;

    public const int MaxClusterMembers = 32;

    /// <summary>The walk budgets and the bucketing path cap, as one value so tests can shrink them.</summary>
    internal readonly record struct Limits(int VisitedPerEntryPoint, long ReachableCountBudget, long BucketBitsetBytes)
    {
        public static Limits Default => new(MaxVisitedPerEntryPoint, MaxReachableCountBudget, MaxBucketBitsetBytes);
    }

    /// <summary>
    ///     Computes the reachability section for a methods slice: per-node entry points, depths,
    ///     buckets, fan-in/out, SCC ids, and recursion clusters. Reuses the merged (already
    ///     deduplicated and sorted) edge list; every walk runs over a dense int graph
    ///     (<see cref="ReachabilityGraph" />), never over id strings. One bounded BFS per resolvable
    ///     entry point, never per node, on the worker team; the walks fold into the facts in
    ///     entry-point order, so the result does not depend on the worker count.
    ///     <para>
    ///         <c>BudgetExhausted</c> reports whether any BFS hit <see cref="MaxVisitedPerEntryPoint"/>.
    ///         Callers that treat "not visited" as "unreachable" (the dead-code report) must consult
    ///         it rather than pattern-matching the diagnostic text.
    ///     </para>
    /// </summary>
    public static (List<NodeReachability> Nodes, List<RecursionCluster> Clusters, bool BudgetExhausted) Compute(CallGraph callGraph, IEnumerable<EntryPoint> entryPoints, List<string> diagnostics)
        => Compute(callGraph, entryPoints, diagnostics, Limits.Default);

    internal static (List<NodeReachability> Nodes, List<RecursionCluster> Clusters, bool BudgetExhausted) Compute(CallGraph callGraph, IEnumerable<EntryPoint> entryPoints, List<string> diagnostics, Limits limits)
    {
        // The run-length graph build below needs the collapse order (source, target, call type,
        // evidence kind). The pipeline hands over the collapsed list already in that order,
        // which costs one linear check; any other caller gets a sorted copy, so the facts never
        // depend on its edge order and its list is never reordered.
        var edges = SortedByCollapseKey(callGraph.Edges);
        var graph = ReachabilityGraph.Build(callGraph.Nodes, edges);
        var facts = new NodeReachability[graph.NodeCount];
        for (var node = 0; node < facts.Length; node++)
        {
            facts[node] = new NodeReachability { NodeId = graph.Ids[node], FanOut = graph.RowLength[node], FanIn = graph.FanIn[node] };
        }

        var (budgetExhausted, walkedEntryPoints) = WalkEntryPoints(graph, entryPoints, facts, diagnostics, limits.VisitedPerEntryPoint);
        var components = ComputeComponents(graph, facts, out var clusters);
        if (DebugLog.Enabled)
        {
            DebugLog.Count("reachability nodes", facts.Length);
            DebugLog.Count("reachability components", components.Count);
            DebugLog.Count("entry points walked", walkedEntryPoints);
        }

        var nodesById = graph.NodesInIdOrder();
        ComputeReachableBuckets(graph, components, nodesById, facts, diagnostics, limits);
        MarkKeepAlive(callGraph.Nodes, edges, graph, facts);
        var ordered = new List<NodeReachability>(facts.Length);
        foreach (var node in nodesById)
        {
            ordered.Add(facts[node]);
        }

        return (ordered, clusters, budgetExhausted);
    }

    /// <summary>
    ///     The collapsed call graph over dense int vertices: the nodes first, in node order (vertex
    ///     <c>i</c> is <c>nodes[i]</c>), then every edge endpoint that is not a node. The forward
    ///     adjacency is one row per vertex in the sorted edge order, self-loops left out (component
    ///     analysis owns them) and repeated targets merged - the target order the string-keyed
    ///     lists had, which keeps Tarjan's discovery order and so every component id.
    /// </summary>
    private sealed class ReachabilityGraph
    {
        public required int NodeCount { get; init; }
        public required string[] Ids { get; init; }
        public required Dictionary<string, int> VertexOf { get; init; }
        public required int[] RowStart { get; init; }
        public required int[] RowLength { get; init; }
        public required int[] Adjacency { get; init; }

        /// <summary>Distinct callers per vertex (self excluded).</summary>
        public required int[] FanIn { get; init; }

        /// <summary>Target vertex of each edge of the sorted list.</summary>
        public required int[] EdgeTarget { get; init; }

        /// <summary>Vertices with a self-loop edge, in edge order.</summary>
        public required List<int> SelfLoops { get; init; }

        public int VertexCount => Ids.Length;

        public ReadOnlySpan<int> Targets(int vertex) => Adjacency.AsSpan(RowStart[vertex], RowLength[vertex]);

        public bool TryGetNode(string id, out int node) => VertexOf.TryGetValue(id, out node) && node < NodeCount;

        /// <summary>Node vertices ordered by id (ordinal): the output order and the budgeted walk's.</summary>
        public int[] NodesInIdOrder()
        {
            var keys = Ids[..NodeCount];
            var nodes = new int[NodeCount];
            for (var node = 0; node < nodes.Length; node++)
            {
                nodes[node] = node;
            }

            // Node ids are distinct (Build rejects a duplicate), so the unstable sort is exact.
            Array.Sort(keys, nodes, StringComparer.Ordinal);
            return nodes;
        }

        /// <summary>
        ///     One pass over the collapse-ordered edges: an edge source's edges are contiguous and
        ///     sorted by target, so a source's row and its targets' caller counts come from
        ///     comparing each target with the previous one.
        /// </summary>
        public static ReachabilityGraph Build(List<MethodNode> nodes, List<MethodCallEdge> edges)
        {
            var vertexOf = new Dictionary<string, int>(nodes.Count, StringComparer.Ordinal);
            var ids = new List<string>(nodes.Count);
            foreach (var node in nodes)
            {
                // A duplicate node id throws here, as the id-keyed facts dictionary did.
                vertexOf.Add(node.Id, ids.Count);
                ids.Add(node.Id);
            }

            var edgeTarget = new int[edges.Count];
            var runSource = new List<int>();
            var runEnd = new List<int>();
            var index = 0;
            while (index < edges.Count)
            {
                var sourceId = edges[index].SourceId;
                runSource.Add(VertexFor(sourceId));
                string? previousId = null;
                var previous = -1;
                for (; index < edges.Count && string.Equals(edges[index].SourceId, sourceId, StringComparison.Ordinal); index++)
                {
                    var targetId = edges[index].TargetId;
                    if (!string.Equals(targetId, previousId, StringComparison.Ordinal))
                    {
                        previousId = targetId;
                        previous = VertexFor(targetId);
                    }

                    edgeTarget[index] = previous;
                }

                runEnd.Add(index);
            }

            var vertexCount = ids.Count;
            var rowStart = new int[vertexCount];
            var rowLength = new int[vertexCount];
            var fanIn = new int[vertexCount];
            var adjacency = new List<int>(edges.Count);
            var selfLoops = new List<int>();
            var runStart = 0;
            for (var run = 0; run < runSource.Count; run++)
            {
                var source = runSource[run];
                rowStart[source] = adjacency.Count;
                var previous = -1;
                var selfLoop = false;
                for (var edge = runStart; edge < runEnd[run]; edge++)
                {
                    var target = edgeTarget[edge];
                    if (target == source)
                    {
                        selfLoop = true;
                    }
                    else if (target != previous)
                    {
                        adjacency.Add(target);
                        fanIn[target]++;
                        previous = target;
                    }
                }

                rowLength[source] = adjacency.Count - rowStart[source];
                if (selfLoop)
                {
                    selfLoops.Add(source);
                }

                runStart = runEnd[run];
            }

            return new ReachabilityGraph
            {
                NodeCount = nodes.Count,
                Ids = [.. ids],
                VertexOf = vertexOf,
                RowStart = rowStart,
                RowLength = rowLength,
                Adjacency = [.. adjacency],
                FanIn = fanIn,
                EdgeTarget = edgeTarget,
                SelfLoops = selfLoops
            };

            int VertexFor(string id)
            {
                if (!vertexOf.TryGetValue(id, out var vertex))
                {
                    vertex = ids.Count;
                    vertexOf.Add(id, vertex);
                    ids.Add(id);
                }

                return vertex;
            }
        }
    }

    /// <summary>
    ///     A worker's BFS state, sized to the graph once and reused for every walk: one stamp per
    ///     vertex (an epoch marks it discovered, the epoch plus one visited, so nothing is cleared
    ///     between walks) and the FIFO of discovered vertices.
    /// </summary>
    private sealed class WalkScratch(int vertexCount)
    {
        public readonly int[] Stamp = new int[vertexCount];
        public readonly int[] Queue = new int[vertexCount];
        private int _epoch = -1;

        /// <summary>Starts a walk: returns its discovered stamp; its visited stamp is one more.</summary>
        public int NextWalk() => _epoch += 2;
    }

    /// <summary>The node vertices one entry point's walk visited, grouped by depth (BFS order).</summary>
    private sealed class EntryWalk
    {
        public int Index;
        public bool Exhausted;
        public int Count;
        public int[] Nodes = new int[256];

        /// <summary>LevelEnds[d] is the end of depth d's nodes in <see cref="Nodes" />.</summary>
        public readonly List<int> LevelEnds = [];

        public void Add(int node, int depth)
        {
            while (LevelEnds.Count <= depth)
            {
                LevelEnds.Add(Count);
            }

            if (Count == Nodes.Length)
            {
                Array.Resize(ref Nodes, Count * 2);
            }

            Nodes[Count++] = node;
            LevelEnds[depth] = Count;
        }
    }

    /// <summary>
    ///     One bounded forward BFS per entry point; every visited node records the entry point id
    ///     (first 16 distinct ids, in entry-point order) and keeps the minimum depth. Entry points
    ///     without a resolvable MethodId contribute nothing here (they are still listed in
    ///     EntryPoints). The budget diagnostic is emitted once for the whole run, naming the first
    ///     entry point, in order, whose walk exhausted it.
    /// </summary>
    private static (bool BudgetExhausted, int Walked) WalkEntryPoints(ReachabilityGraph graph, IEnumerable<EntryPoint> entryPoints, NodeReachability[] facts, List<string> diagnostics, int visitedPerEntryPoint)
    {
        var walks = new List<(EntryPoint EntryPoint, int Start)>();
        foreach (var entryPoint in entryPoints)
        {
            if (!string.IsNullOrWhiteSpace(entryPoint.MethodId) && graph.TryGetNode(entryPoint.MethodId, out var start))
            {
                walks.Add((entryPoint, start));
            }
        }

        var bestDepth = new int[graph.NodeCount];
        Array.Fill(bestDepth, int.MaxValue);
        var reported = false;
        var scratchPool = new System.Collections.Concurrent.ConcurrentBag<WalkScratch>();
        var walkPool = new System.Collections.Concurrent.ConcurrentBag<EntryWalk>();
        // The walks are independent and read only the graph; a tiny graph is not worth a team.
        var workers = (long)walks.Count * graph.VertexCount < 1_000_000 ? 1 : Math.Max(1, Dosai.MaxSymbolAnalysisWorkers);
        DedicatedStack.ForEachInOrder("Dosai reachability walk", workers, walks.Count, index =>
        {
            var scratch = scratchPool.TryTake(out var pooledScratch) ? pooledScratch : new WalkScratch(graph.VertexCount);
            var walk = walkPool.TryTake(out var pooledWalk) ? pooledWalk : new EntryWalk();
            walk.Index = index;
            walk.Count = 0;
            walk.LevelEnds.Clear();
            walk.Exhausted = WalkFrom(graph, scratch, walks[index].Start, walk, visitedPerEntryPoint);
            scratchPool.Add(scratch);
            return walk;
        }, walk =>
        {
            var entryPoint = walks[walk.Index].EntryPoint;
            if (walk.Exhausted && !reported)
            {
                reported = true;
                diagnostics.Add($"Reachability budget of {visitedPerEntryPoint} nodes exhausted while walking from entry point {entryPoint.Id}; deeper reachability facts for this run are incomplete.");
            }

            var levelStart = 0;
            for (var depth = 0; depth < walk.LevelEnds.Count; depth++)
            {
                var levelEnd = walk.LevelEnds[depth];
                for (var position = levelStart; position < levelEnd; position++)
                {
                    var node = walk.Nodes[position];
                    if (depth < bestDepth[node])
                    {
                        bestDepth[node] = depth;
                    }

                    var reachedBy = facts[node].ReachableEntryPoints;
                    if (reachedBy.Count < 16 && !reachedBy.Contains(entryPoint.Id))
                    {
                        reachedBy.Add(entryPoint.Id);
                    }
                }

                levelStart = levelEnd;
            }

            walkPool.Add(walk);
        });

        for (var node = 0; node < facts.Length; node++)
        {
            if (bestDepth[node] != int.MaxValue)
            {
                // Exact reachability flag, set for every visited node, unlike the bounded
                // entry-point list.
                facts[node].Reachable = true;
                facts[node].DepthFromEntryPoint = bestDepth[node];
            }
        }

        return (reported, walks.Count);
    }

    /// <summary>
    ///     The BFS of one entry point, stopped at <see cref="MaxVisitedPerEntryPoint" /> visited
    ///     vertices (non-node edge endpoints count, and are not expanded). Returns whether the
    ///     budget cut the walk short.
    ///     <para>
    ///         Each vertex is queued once. The walk this replaced queued a vertex again for every
    ///         caller that saw it unvisited and skipped the repeats on dequeue, and it declared the
    ///         budget exhausted when anything at all, repeats included, was still queued after the
    ///         last allowed visit. That is the same as "something was queued after the vertex just
    ///         visited", which is what the attempt counters below answer without holding the
    ///         repeats.
    ///     </para>
    /// </summary>
    private static bool WalkFrom(ReachabilityGraph graph, WalkScratch scratch, int start, EntryWalk walk, int visitedPerEntryPoint)
    {
        var stamp = scratch.Stamp;
        var queue = scratch.Queue;
        var discovered = scratch.NextWalk();
        var visited = discovered + 1;
        stamp[start] = discovered;
        queue[0] = start;
        int head = 0, tail = 1, levelEnd = 1, depth = 0, visitedCount = 0;
        // Every enqueue the old walk made, repeats included, and the count when the last
        // distinct vertex was queued.
        int attempts = 1, lastQueuedAt = 1;
        while (head < tail)
        {
            if (head == levelEnd)
            {
                depth++;
                levelEnd = tail;
            }

            var current = queue[head++];
            stamp[current] = visited;
            visitedCount++;
            if (current < graph.NodeCount)
            {
                walk.Add(current, depth);
                foreach (var target in graph.Targets(current))
                {
                    if (stamp[target] != visited)
                    {
                        attempts++;
                        if (stamp[target] != discovered)
                        {
                            stamp[target] = discovered;
                            queue[tail++] = target;
                            lastQueuedAt = attempts;
                        }
                    }
                }
            }

            if (visitedCount >= visitedPerEntryPoint)
            {
                return head < tail || attempts > lastQueuedAt;
            }
        }

        return false;
    }

    /// <summary>
    ///     Unreachable-but-kept-alive marking. Any node targeted by a reflection or DI/
    ///     framework-model edge (or carrying that evidence itself) stays out of the dead-code
    ///     report: `AddSingleton<Foo>()`, `Activator.CreateInstance(typeof(Foo))`, and
    ///     `[McpServerTool]`-style framework callbacks are invoked without a call site Dosai can
    ///     attribute to an entry point. One linear pass over edges and nodes.
    /// </summary>
    private static void MarkKeepAlive(List<MethodNode> nodes, List<MethodCallEdge> edges, ReachabilityGraph graph, NodeReachability[] facts)
    {
        // Report the evidence kind that actually kept the node alive. The edge's own
        // EvidenceKind is frequently an ordinary call kind while a reflection/framework kind
        // sits in its Evidence list, so naming EvidenceKind unconditionally attributed the
        // decision to the wrong evidence.
        for (var index = 0; index < edges.Count; index++)
        {
            var edge = edges[index];
            var kinds = new KeepAliveKinds();
            kinds.See(edge.EvidenceKind);
            foreach (var evidence in edge.Evidence)
            {
                kinds.See(evidence.Kind);
            }

            kinds.KeepAlive(facts, graph.EdgeTarget[index]);
        }

        for (var node = 0; node < nodes.Count; node++)
        {
            var kinds = new KeepAliveKinds();
            foreach (var evidence in nodes[node].Evidence)
            {
                kinds.See(evidence.Kind);
            }

            if (nodes[node].Identity?.Evidence is { } identityKinds)
            {
                foreach (var kind in identityKinds)
                {
                    kinds.See(kind);
                }
            }

            kinds.KeepAlive(facts, node);
        }
    }

    /// <summary>
    ///     The distinct keep-alive evidence kinds of one edge or node, in first-occurrence order
    ///     (declared kind first, then the evidence list). Exactly two kinds qualify, so two slots
    ///     replace the per-edge <c>Prepend</c>/<c>Where</c>/<c>Distinct</c> chain.
    /// </summary>
    private struct KeepAliveKinds
    {
        private AnalysisEvidenceKind? _first;
        private AnalysisEvidenceKind? _second;

        public void See(AnalysisEvidenceKind kind)
        {
            if (kind is not (AnalysisEvidenceKind.ReflectionHeuristic or AnalysisEvidenceKind.FrameworkModel))
            {
                return;
            }

            if (_first is null)
            {
                _first = kind;
            }
            else if (_second is null && kind != _first)
            {
                _second = kind;
            }
        }

        /// <summary>
        ///     Only unreachable nodes need keeping alive; a reachable node already has its answer.
        ///     A vertex past the facts is an edge endpoint that is not a node.
        /// </summary>
        public readonly void KeepAlive(NodeReachability[] facts, int vertex)
        {
            if (_first is not { } first || vertex >= facts.Length || facts[vertex] is not { Reachable: false } fact)
            {
                return;
            }

            fact.KeepAlive = true;
            AddReason(fact, first);
            if (_second is { } second)
            {
                AddReason(fact, second);
            }
        }

        private static void AddReason(NodeReachability fact, AnalysisEvidenceKind kind)
        {
            var reason = GraphAssembly.EvidenceKindName(kind);
            if (!fact.KeepAliveReasons.Contains(reason, StringComparer.Ordinal))
            {
                fact.KeepAliveReasons.Add(reason);
            }
        }
    }

    /// <summary>
    ///     The dead-code report, source-declared methods and constructors that no entry point
    ///     reaches and no reflection/DI evidence keeps alive. Emitted only for source-mode runs:
    ///     assembly/library trees have no meaningful entry-point roots (library public-API roots are
    ///     a deliberate non-goal), so everything would be flagged. Suppressed when a reachability
    ///     budget was exhausted (an unvisited node is then unknown, not unreachable). Bounded at
    ///     <see cref="MaxDeadCodeEntries"/> with a diagnostic.
    /// </summary>
    public static List<DeadCodeEntry> BuildDeadCode(CallGraph callGraph, List<NodeReachability> reachability, bool sourceMode, bool budgetExhausted, List<string> diagnostics)
    {
        if (!sourceMode)
        {
            return [];
        }

        if (budgetExhausted)
        {
            diagnostics.Add("Dead-code report suppressed: the reachability budget was exhausted, so unvisited nodes are unknown rather than unreachable.");
            return [];
        }

        var factsByNodeId = reachability.ToDictionary(facts => facts.NodeId, StringComparer.Ordinal);
        var deadCode = new List<DeadCodeEntry>();
        var truncated = false;
        foreach (var node in callGraph.Nodes.OrderBy(node => node.Id, StringComparer.Ordinal))
        {
            if (!factsByNodeId.TryGetValue(node.Id, out var facts) ||
                facts.Reachable ||
                facts.KeepAlive ||
                node.IsExternal ||
                node.Kind is not ("Method" or "Constructor") ||
                !IsSourceLocation(node.FileName) ||
                IsCompilerGenerated(node.Name, node.ClassName))
            {
                continue;
            }

            if (deadCode.Count >= MaxDeadCodeEntries)
            {
                truncated = true;
                break;
            }

            deadCode.Add(new DeadCodeEntry
            {
                NodeId = node.Id,
                Name = node.Name,
                ClassName = node.ClassName,
                Namespace = node.Namespace,
                Kind = node.Kind,
                FileName = node.FileName,
                LineNumber = node.LineNumber,
                ColumnNumber = node.ColumnNumber
            });
        }

        if (truncated)
        {
            diagnostics.Add($"Dead-code report truncated at {MaxDeadCodeEntries} entries; query reachability[reachable=false] for the full set.");
        }

        return deadCode;

        // Bin artifacts pulled into a source scan (DLLs under bin/, their PDB-backed twins) carry
        // assembly file names rather than source paths, not reviewable dead code.
        static bool IsSourceLocation(string? fileName) =>
            !string.IsNullOrWhiteSpace(fileName) &&
            (fileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
             fileName.EndsWith(".vb", StringComparison.OrdinalIgnoreCase) ||
             fileName.EndsWith(".fs", StringComparison.OrdinalIgnoreCase));

        // Roslyn-synthesized members are recognised by their *name*, never by the whole node id:
        // the id of any member of a generic type embeds the type arguments (`Box<T>..ctor(T)`), so
        // testing the id for angle brackets silently dropped every generic type from the report.
        // Synthesized member names begin with '<' (`<Main>$`, `<Run>b__0`, `<Read>d__3`) and
        // synthesized containers are `<>`-prefixed (`<>c`, `<>c__DisplayClass0_0`).
        static bool IsCompilerGenerated(string? name, string? className) =>
            name is not null && name.StartsWith('<') ||
            className is not null && className.Contains("<>", StringComparison.Ordinal);
    }

    /// <summary>Upper bound on DeadCode list entries; larger graphs get a diagnostic + the query alias instead.</summary>
    public const int MaxDeadCodeEntries = 500;

    /// <summary>
    ///     Annotate each edge with the number of distinct call sites for its (source, target)
    ///     pair and collapse same-pair duplicates into a single edge. Edges with different call
    ///     types or evidence kinds stay separate, a direct edge and a dispatch-candidate edge are
    ///     different facts even between the same nodes. The annotations of dropped duplicates
    ///     (argument expressions, evidence, distinct sites as <see cref="MethodCallEdge.CallSiteCount"/>)
    ///     merge into the keeper; downstream consumers (exploit chains) operate on method ids,
    ///     not per-site locations, so nothing they need is lost.
    /// </summary>
    /// <remarks>
    ///     This used to group through one concatenated key string per edge, materialized and
    ///     retained by <c>GroupBy</c>/<c>OrderBy</c> for the whole step - on a 15.4 M-edge tree
    ///     that is ~7 GB of keys alone and the allocation that ran the scan out of memory
    ///     (issue #70). It now groups through <see cref="GroupByCollapseKey" /> (int arrays, no
    ///     keys held) and orders the groups under <see cref="GraphAssembly.CompareCollapseEdges" />,
    ///     which reproduces the concatenated key's order field by field (separator semantics
    ///     included, see there).
    /// </remarks>
    public static void CollapseDuplicateCallSites(CallGraph callGraph)
    {
        var edges = callGraph.Edges;
        if (edges.Count == 0)
        {
            callGraph.Edges = [];
            return;
        }

        // Only these three int arrays (4 bytes per edge plus 8 per group) stay live through the
        // merge loop below; the hash table, per-edge group ids and sort keys die in the helpers.
        var (placement, offsets, order) = GroupByCollapseKey(edges);
        var keyCount = order.Length;

        var collapsed = new List<MethodCallEdge>(keyCount);
        // Per-group scratch, reused across groups. Tuples instead of the old "file:line:col"
        // strings: distinct counts agree, because the trailing number segments of the rendered
        // form cannot contain the ':' two different tuples would need to collide on. A missing
        // location rendered as "::" and an empty one as ":0:0", hence the flag (not nullable
        // numbers: ValueTuple hashing boxes nullable items, ~190 MB per 4 M edges).
        var sitesSeen = new HashSet<(string FileName, int LineNumber, int ColumnNumber, bool Missing)>();
        var argumentsSeen = new HashSet<string>(StringComparer.Ordinal);
        var argumentsList = new List<string>();
        var expressionsSeen = new HashSet<string>(StringComparer.Ordinal);
        var expressionsList = new List<string>();
        var evidenceSeen = new HashSet<(AnalysisEvidenceKind Kind, string? Source, string? Description, string? FileName, int LineNumber, int ColumnNumber)>();
        var mergedEvidence = new List<AnalysisEvidence>();

        for (var position = 0; position < keyCount; position++)
        {
            var group = order[position];
            var start = offsets[group];
            var stop = offsets[group + 1];

            // Keeper: first in encounter order (placement preserved it) minimal by file, line,
            // column - exactly the OrderBy(...).First() the LINQ form picked.
            var keeper = edges[placement[start]];
            for (var s = start + 1; s < stop; s++)
            {
                var edge = edges[placement[s]];
                var c = string.CompareOrdinal(keeper.CallLocation?.FileName ?? string.Empty, edge.CallLocation?.FileName ?? string.Empty);
                if (c < 0)
                {
                    continue;
                }

                if (c > 0
                    || (edge.CallLocation?.LineNumber ?? 0) < (keeper.CallLocation?.LineNumber ?? 0)
                    || ((edge.CallLocation?.LineNumber ?? 0) == (keeper.CallLocation?.LineNumber ?? 0)
                        && (edge.CallLocation?.ColumnNumber ?? 0) < (keeper.CallLocation?.ColumnNumber ?? 0)))
                {
                    keeper = edge;
                }
            }

            // Scratch was reset at the end of the previous group (or is fresh on the first).
            for (var s = start; s < stop; s++)
            {
                var edge = edges[placement[s]];
                sitesSeen.Add(edge.CallLocation is { } site
                    ? (site.FileName ?? string.Empty, site.LineNumber, site.ColumnNumber, false)
                    : (string.Empty, 0, 0, true));
                if (edge.Arguments is not null)
                {
                    foreach (var argument in edge.Arguments)
                    {
                        if (argumentsSeen.Add(argument))
                        {
                            argumentsList.Add(argument);
                        }
                    }
                }

                if (edge.ArgumentExpressions is not null)
                {
                    foreach (var expression in edge.ArgumentExpressions)
                    {
                        if (expressionsSeen.Add(expression))
                        {
                            expressionsList.Add(expression);
                        }
                    }
                }

                // First occurrence per (kind, source, description, location) wins, DistinctBy's
                // order; read before the keeper's own lists are replaced below.
                foreach (var evidence in edge.Evidence)
                {
                    if (evidenceSeen.Add((evidence.Kind, evidence.Source, evidence.Description, evidence.FileName, evidence.LineNumber, evidence.ColumnNumber)))
                    {
                        mergedEvidence.Add(evidence);
                    }
                }
            }

            keeper.CallSiteCount = sitesSeen.Count;
            // Always assigned (empty, never null), as before: the LINQ form reassigned these for
            // every group, singleton groups included, and deduplicated within a single edge too.
            keeper.Arguments = [.. argumentsList];
            keeper.ArgumentExpressions = [.. expressionsList];
            keeper.Evidence = [.. mergedEvidence];

            // Scratch reset. Clear() is O(capacity), and one group of a thousand call sites
            // (real graphs have them) would make every later group pay for that capacity - a
            // flat tax across millions of mostly-singleton groups. Oversized scratch is dropped
            // and re-grown instead; small ones clear in O(count).
            const int reusedScratchLimit = 64;
            if (sitesSeen.Count > reusedScratchLimit) sitesSeen = []; else sitesSeen.Clear();
            if (evidenceSeen.Count > reusedScratchLimit) evidenceSeen = []; else evidenceSeen.Clear();
            if (argumentsSeen.Count > reusedScratchLimit) argumentsSeen = []; else argumentsSeen.Clear();
            if (expressionsSeen.Count > reusedScratchLimit) expressionsSeen = []; else expressionsSeen.Clear();
            argumentsList.Clear();
            expressionsList.Clear();
            mergedEvidence.Clear();

            collapsed.Add(keeper);
        }

        callGraph.Edges = collapsed;
    }

    /// <summary>
    ///     Groups edges by <see cref="GraphAssembly.CollapseKey" /> and returns the counting-sort
    ///     layout the merge walks: <c>placement[offsets[g]..offsets[g + 1]]</c> lists group
    ///     <c>g</c>'s edge indexes in encounter order, and <c>order</c> lists the groups in
    ///     collapse-key order. Each stage is its own frame on purpose: the hash table, the
    ///     per-edge group ids and the sort keys are garbage before the merge allocates a single
    ///     keeper list (a once-run method keeps every local live until it returns).
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (int[] Placement, int[] Offsets, int[] Order) GroupByCollapseKey(List<MethodCallEdge> edges)
    {
        var placement = PlaceByCollapseGroup(edges, out var offsets);
        return (placement, offsets, OrderCollapseGroups(edges, placement, offsets));
    }

    /// <summary>Counting-sort placement of the edges by dense group id, encounter order kept within a group.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static int[] PlaceByCollapseGroup(List<MethodCallEdge> edges, out int[] offsets)
    {
        var groupOfEdge = AssignCollapseGroups(edges, out var groupCount);
        offsets = new int[groupCount + 1];
        foreach (var group in groupOfEdge)
        {
            offsets[group + 1]++;
        }

        for (var group = 0; group < groupCount; group++)
        {
            offsets[group + 1] += offsets[group];
        }

        var cursor = offsets[..groupCount];
        var placement = new int[groupOfEdge.Length];
        for (var index = 0; index < groupOfEdge.Length; index++)
        {
            placement[cursor[groupOfEdge[index]]++] = index;
        }

        return placement;
    }

    /// <summary>
    ///     Group ids in collapse-key order. A group's first edge carries its key; the keys are
    ///     distinct across groups, so the order is fully determined. The keys are copied into one
    ///     contiguous array, so comparisons do not chase each group's edge, and the group ids are
    ///     sorted over it on the worker team.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static int[] OrderCollapseGroups(List<MethodCallEdge> edges, int[] placement, int[] offsets)
    {
        var groupCount = offsets.Length - 1;
        var keys = new GraphAssembly.CollapseKey[groupCount];
        var order = new int[groupCount];
        for (var group = 0; group < groupCount; group++)
        {
            keys[group] = GraphAssembly.CollapseKey.From(edges[placement[offsets[group]]]);
            order[group] = group;
        }

        ParallelSort.StableSort(order, (x, y) => GraphAssembly.CompareCollapseKeys(keys[x], keys[y]));
        return order;
    }

    /// <summary>
    ///     Dense group id per edge, numbered in first-encounter order, through an open-addressing
    ///     table: each slot packs a group's key hash with its id, and only a hash match reads the
    ///     group's first edge to compare keys - nothing key-sized is stored. The key hashes (two id
    ///     strings each) are computed up front on the worker team. About 28 bytes per edge at the
    ///     peak, where a <c>Dictionary</c> keyed by the struct costs 44 per entry (entry plus
    ///     bucket) and briefly holds both tables on every resize.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static int[] AssignCollapseGroups(List<MethodCallEdge> edges, out int groupCount)
    {
        var count = edges.Count;
        var hashes = new int[count];
        const int hashChunk = 16384;
        DedicatedStack.ForEach("Dosai collapse hashes", Math.Max(1, Dosai.MaxSymbolAnalysisWorkers), (count + hashChunk - 1) / hashChunk, chunk =>
        {
            var end = Math.Min(count, (chunk + 1) * hashChunk);
            for (var index = chunk * hashChunk; index < end; index++)
            {
                hashes[index] = GraphAssembly.CollapseKey.From(edges[index]).GetHashCode();
            }
        });

        var groupOfEdge = new int[count];
        var firstEdge = new int[count];
        // Load factor at most 2/3 even when every edge is its own group. A slot is
        // (hash << 32) | (group id + 1); zero means empty.
        var slots = new long[(int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Clamp(count + (count >> 1), 16, 1 << 30))];
        var mask = slots.Length - 1;
        var groups = 0;
        for (var index = 0; index < count; index++)
        {
            var key = GraphAssembly.CollapseKey.From(edges[index]);
            var hash = hashes[index];
            var slot = hash & mask;
            while (true)
            {
                var entry = slots[slot];
                if (entry == 0)
                {
                    slots[slot] = ((long)hash << 32) | (uint)(groups + 1);
                    firstEdge[groups] = index;
                    groupOfEdge[index] = groups++;
                    break;
                }

                var group = (int)(uint)entry - 1;
                if ((int)(entry >> 32) == hash && key.Equals(GraphAssembly.CollapseKey.From(edges[firstEdge[group]])))
                {
                    groupOfEdge[index] = group;
                    break;
                }

                slot = (slot + 1) & mask;
            }
        }

        groupCount = groups;
        return groupOfEdge;
    }

    /// <summary>
    ///     The edge list itself when it is already in collapse-key order (one linear check),
    ///     otherwise a stably sorted copy; the caller's list is never reordered.
    /// </summary>
    private static List<MethodCallEdge> SortedByCollapseKey(List<MethodCallEdge> edges)
    {
        for (var index = 1; index < edges.Count; index++)
        {
            if (GraphAssembly.CompareCollapseEdges(edges[index - 1], edges[index]) > 0)
            {
                var sorted = new List<MethodCallEdge>(edges);
                GraphAssembly.StableSortInPlace(sorted, GraphAssembly.CompareCollapseEdges);
                return sorted;
            }
        }

        return edges;
    }

    /// <summary>
    ///     Strongly connected components of the int graph: component of each vertex, members
    ///     grouped per component (<c>Members[Start[c]..Start[c + 1]]</c>), components in Tarjan
    ///     discovery order.
    /// </summary>
    private sealed class Components
    {
        public required int[] Of { get; init; }
        public required int[] Members { get; init; }
        public required int[] Start { get; init; }

        public int Count => Start.Length - 1;

        public ReadOnlySpan<int> MembersOf(int component) => Members.AsSpan(Start[component], Start[component + 1] - Start[component]);
    }

    /// <summary>
    ///     Iterative Tarjan over the merged graph, rooted at each node in node order and following
    ///     each vertex's row in order (so component ids match the string-keyed walk this replaced).
    ///     Returns every component - singletons and the non-node endpoints the walk reaches
    ///     included, which the size bucketing needs - and the recursion clusters. Components come
    ///     back in Tarjan discovery order, reverse topological order of the condensation, so every
    ///     component's successors precede it.
    /// </summary>
    private static Components ComputeComponents(ReachabilityGraph graph, NodeReachability[] facts, out List<RecursionCluster> clusters)
    {
        var vertexCount = graph.VertexCount;
        var order = new int[vertexCount];
        Array.Fill(order, -1);
        var lowLink = new int[vertexCount];
        var onStack = new bool[vertexCount];
        var stack = new int[vertexCount];
        var stackSize = 0;
        var workVertex = new int[vertexCount];
        var workChild = new int[vertexCount];
        var componentOf = new int[vertexCount];
        Array.Fill(componentOf, -1);
        var members = new int[vertexCount];
        var memberCount = 0;
        var componentStart = new List<int>();
        var nextOrder = 0;

        for (var root = 0; root < graph.NodeCount; root++)
        {
            if (order[root] >= 0)
            {
                continue;
            }

            Discover(root);
            workVertex[0] = root;
            workChild[0] = 0;
            var workSize = 1;
            while (workSize > 0)
            {
                var current = workVertex[workSize - 1];
                var child = workChild[workSize - 1];
                if (child < graph.RowLength[current])
                {
                    workChild[workSize - 1] = child + 1;
                    var next = graph.Adjacency[graph.RowStart[current] + child];
                    if (order[next] < 0)
                    {
                        Discover(next);
                        workVertex[workSize] = next;
                        workChild[workSize] = 0;
                        workSize++;
                    }
                    else if (onStack[next])
                    {
                        lowLink[current] = Math.Min(lowLink[current], order[next]);
                    }
                }
                else
                {
                    workSize--;
                    if (lowLink[current] == order[current])
                    {
                        var component = componentStart.Count;
                        componentStart.Add(memberCount);
                        int member;
                        do
                        {
                            member = stack[--stackSize];
                            onStack[member] = false;
                            members[memberCount++] = member;
                            componentOf[member] = component;
                        } while (member != current);
                    }

                    if (workSize > 0)
                    {
                        var parent = workVertex[workSize - 1];
                        lowLink[parent] = Math.Min(lowLink[parent], lowLink[current]);
                    }
                }
            }
        }

        componentStart.Add(memberCount);
        var components = new Components { Of = componentOf, Members = members, Start = [.. componentStart] };

        clusters = [];
        for (var component = 0; component < components.Count; component++)
        {
            var componentMembers = components.MembersOf(component);
            if (componentMembers.Length <= 1)
            {
                continue;
            }

            var memberIds = new string[componentMembers.Length];
            for (var index = 0; index < componentMembers.Length; index++)
            {
                var member = componentMembers[index];
                memberIds[index] = graph.Ids[member];
                if (member < facts.Length)
                {
                    facts[member].SccId = component;
                    facts[member].InRecursiveCycle = true;
                }
            }

            Array.Sort(memberIds, StringComparer.Ordinal);
            clusters.Add(new RecursionCluster
            {
                Id = string.Create(CultureInfo.InvariantCulture, $"scc{component}"),
                Size = componentMembers.Length,
                MemberIds = [.. memberIds.AsSpan(0, Math.Min(memberIds.Length, MaxClusterMembers))]
            });
        }

        // Self-loops (A→A) are recursive cycles too; the rows leave them out. Each gets a unique
        // negative SccId so grouping never merges unrelated self-recursive methods (non-negative
        // ids belong to multi-node components, -1 to plain singletons), in edge order. A node
        // already in a multi-node component keeps that component.
        var nextSelfLoopId = -2;
        foreach (var vertex in graph.SelfLoops)
        {
            if (vertex < facts.Length && !facts[vertex].InRecursiveCycle)
            {
                var selfId = graph.Ids[vertex];
                facts[vertex].InRecursiveCycle = true;
                facts[vertex].SccId = nextSelfLoopId--;
                clusters.Add(new RecursionCluster { Id = $"scc-self-{selfId}", Size = 1, MemberIds = [selfId] });
            }
        }

        return components;

        void Discover(int vertex)
        {
            order[vertex] = lowLink[vertex] = nextOrder++;
            stack[stackSize++] = vertex;
            onStack[vertex] = true;
        }
    }

    /// <summary>
    ///     Bucketed forward-reachable node counts. Below the size where the old per-component
    ///     bitsets (components x nodes bits) fit <see cref="MaxBucketBitsetBytes" />, the counts are
    ///     exact; above it, the budgeted per-node walk answers, with its diagnostics - the same
    ///     split and the same results as before, at a fraction of the cost (see the two paths).
    /// </summary>
    private static void ComputeReachableBuckets(ReachabilityGraph graph, Components components, int[] nodesById, NodeReachability[] facts, List<string> diagnostics, Limits limits)
    {
        var bucketWatch = DebugLog.Enabled ? System.Diagnostics.Stopwatch.StartNew() : null;
        var nodeCount = graph.NodeCount;
        var bitsetBytes = (long)components.Count * ((nodeCount + 63) / 64 * 8);
        if (bitsetBytes > limits.BucketBitsetBytes)
        {
            if (DebugLog.Enabled)
            {
                DebugLog.Log($"reachability bucketing path: budgeted walk ({components.Count} components x {nodeCount} nodes would need {DebugLog.FormatBytes(bitsetBytes)} of bitsets, above the {DebugLog.FormatBytes(limits.BucketBitsetBytes)} cap)");
            }
            diagnostics.Add($"Reachable-node bucketing degraded to the budgeted walk ({components.Count} components × {nodeCount} nodes exceed the bitset cap).");
            ComputeReachableBucketsBudgeted(graph, nodesById, facts, diagnostics, limits.ReachableCountBudget);
            if (bucketWatch is not null)
            {
                DebugLog.Log(string.Create(CultureInfo.InvariantCulture, $"reachability bucketing (budgeted walk) completed in {bucketWatch.Elapsed.TotalSeconds:F3}s"));
            }
            return;
        }

        if (DebugLog.Enabled)
        {
            DebugLog.Log($"reachability bucketing path: condensed counts ({components.Count} components x {nodeCount} nodes)");
        }

        ComputeReachableBucketsCondensed(graph, components, facts);
        if (bucketWatch is not null)
        {
            DebugLog.Log(string.Create(CultureInfo.InvariantCulture, $"reachability bucketing (condensed counts) completed in {bucketWatch.Elapsed.TotalSeconds:F3}s"));
        }
    }

    /// <summary>
    ///     Exact buckets on the SCC condensation, in Tarjan discovery order (every component's
    ///     successors are done before it). A bucket only distinguishes counts up to 1,000, so each
    ///     component keeps the components it reaches - excluding itself and those without nodes -
    ///     only while their node count stays at or below that cap; past it the component is
    ///     saturated (bucket 10000), and so is everything that reaches it. A component's list is
    ///     dropped once its last predecessor has read it. This replaces one bitset of every node per
    ///     component and its unions: the count is the same, components are disjoint, so the
    ///     reachable node count is the sum of the reachable components' node counts.
    /// </summary>
    private static void ComputeReachableBucketsCondensed(ReachabilityGraph graph, Components components, NodeReachability[] facts)
    {
        const int cap = 1000;
        var count = components.Count;
        var weight = new int[count];
        for (var node = 0; node < graph.NodeCount; node++)
        {
            weight[components.Of[node]]++;
        }

        // Distinct predecessor components still to read each component's list.
        var seenFrom = new int[count];
        Array.Fill(seenFrom, -1);
        var readers = new int[count];
        for (var component = 0; component < count; component++)
        {
            foreach (var member in components.MembersOf(component))
            {
                foreach (var target in graph.Targets(member))
                {
                    var successor = components.Of[target];
                    if (successor != component && seenFrom[successor] != component)
                    {
                        seenFrom[successor] = component;
                        readers[successor]++;
                    }
                }
            }
        }

        Array.Fill(seenFrom, -1);
        var counted = new int[count];
        Array.Fill(counted, -1);
        var reach = new int[count][];
        var saturated = new bool[count];
        var buffer = new int[cap + 1];
        for (var component = 0; component < count; component++)
        {
            var total = weight[component];
            var size = 0;
            var full = total > cap;
            counted[component] = component;
            foreach (var member in components.MembersOf(component))
            {
                foreach (var target in graph.Targets(member))
                {
                    var successor = components.Of[target];
                    if (successor == component || seenFrom[successor] == component)
                    {
                        continue;
                    }

                    seenFrom[successor] = component;
                    if (!full)
                    {
                        full = saturated[successor] || !Count(successor) || !CountAll(reach[successor]);
                    }

                    if (--readers[successor] == 0)
                    {
                        reach[successor] = null!;
                    }
                }
            }

            if (full)
            {
                saturated[component] = true;
            }
            else
            {
                reach[component] = size == 0 ? [] : buffer[..size];
            }

            var bucket = full ? BucketFor(cap + 1) : BucketFor(total);
            foreach (var member in components.MembersOf(component))
            {
                if (member < facts.Length)
                {
                    facts[member].ReachableNodeBucket = bucket;
                }
            }

            continue;

            // Adds one reachable component; false once the count passes the cap.
            bool Count(int reached)
            {
                if (counted[reached] == component || weight[reached] == 0)
                {
                    return true;
                }

                counted[reached] = component;
                total += weight[reached];
                buffer[size++] = reached;
                return total <= cap;
            }

            bool CountAll(int[] reached)
            {
                foreach (var other in reached)
                {
                    if (!Count(other))
                    {
                        return false;
                    }
                }

                return true;
            }
        }
    }

    /// <summary>
    ///     The budgeted per-node walk for graphs past the bitset cap, in node id order, against one
    ///     shared budget of <see cref="MaxReachableCountBudget" /> visited vertices: a node whose
    ///     walk the budget cuts short reports bucket 0 ("unknown") rather than an undercount that
    ///     would read as a confident fact, including the walk of the final node, and nodes after
    ///     the budget ran out keep 0. Visited vertices include non-node endpoints, which are
    ///     expanded too. Each vertex is queued once; see <see cref="WalkFrom" /> for why that keeps
    ///     the old cut-off decisions.
    /// </summary>
    private static void ComputeReachableBucketsBudgeted(ReachabilityGraph graph, int[] nodesById, NodeReachability[] facts, List<string> diagnostics, long reachableCountBudget)
    {
        var budget = reachableCountBudget;
        var degraded = false;
        var scratch = new WalkScratch(graph.VertexCount);
        var stamp = scratch.Stamp;
        var queue = scratch.Queue;
        foreach (var node in nodesById)
        {
            if (budget <= 0)
            {
                degraded = true;
                break;
            }

            var discovered = scratch.NextWalk();
            var visited = discovered + 1;
            stamp[node] = discovered;
            queue[0] = node;
            int head = 0, tail = 1, attempts = 1, lastQueuedAt = 1;
            var truncated = false;
            while (head < tail)
            {
                var current = queue[head++];
                stamp[current] = visited;
                budget--;
                foreach (var target in graph.Targets(current))
                {
                    if (stamp[target] != visited)
                    {
                        attempts++;
                        if (stamp[target] != discovered)
                        {
                            stamp[target] = discovered;
                            queue[tail++] = target;
                            lastQueuedAt = attempts;
                        }
                    }
                }

                if (budget <= 0)
                {
                    // Anything still queued would have been cut off: this walk is an undercount.
                    truncated = head < tail || attempts > lastQueuedAt;
                    degraded |= truncated;
                    break;
                }
            }

            facts[node].ReachableNodeBucket = truncated ? 0 : BucketFor(head);
        }

        if (degraded)
        {
            diagnostics.Add($"Reachable-node-count budget of {reachableCountBudget} exhausted; remaining nodes report bucket 0. Use fan-out and entry-point reachability for ranking instead.");
        }
    }

    private static int BucketFor(int count) => count switch
    {
        <= 1 => 1,
        <= 10 => 10,
        <= 100 => 100,
        <= 1000 => 1000,
        _ => 10000
    };
}
