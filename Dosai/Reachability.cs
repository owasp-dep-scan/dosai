using System.Collections;
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

    /// <summary>Memory cap for the exact condensed bitsets; beyond it the budgeted fallback runs instead.</summary>
    public const long MaxBucketBitsetBytes = 256L * 1024 * 1024;

    public const int MaxClusterMembers = 32;

    /// <summary>
    ///     Computes the reachability section for a methods slice: per-node entry points, depths,
    ///     buckets, fan-in/out, SCC ids, and recursion clusters. Reuses the merged (already
    ///     deduplicated and sorted) edge list; builds the forward index and the distinct-caller
    ///     counts by run-length over it; one bounded BFS per resolvable entry point, never per
    ///     node.
    ///     <para>
    ///         <c>BudgetExhausted</c> reports whether any BFS hit <see cref="MaxVisitedPerEntryPoint"/>.
    ///         Callers that treat "not visited" as "unreachable" (the dead-code report) must consult
    ///         it rather than pattern-matching the diagnostic text.
    ///     </para>
    /// </summary>
    public static (List<NodeReachability> Nodes, List<RecursionCluster> Clusters, bool BudgetExhausted) Compute(CallGraph callGraph, IEnumerable<EntryPoint> entryPoints, List<string> diagnostics)
    {
        // The run-length adjacency and fan-in builds below need the collapse order (source,
        // target, call type, evidence kind). The pipeline hands over the collapsed list already
        // in that order, which costs one linear check; any other caller gets a sorted copy, so
        // the facts never depend on its edge order and its list is never reordered.
        var edges = SortedByCollapseKey(callGraph.Edges);
        var forward = BuildForwardAdjacency(edges);
        var fanIn = CountDistinctCallers(edges);
        var facts = callGraph.Nodes.ToDictionary(node => node.Id, node => new NodeReachability { NodeId = node.Id }, StringComparer.Ordinal);

        foreach (var (nodeId, targets) in forward)
        {
            if (facts.TryGetValue(nodeId, out var fact))
            {
                fact.FanOut = targets.Count;
            }
        }

        foreach (var (nodeId, callers) in fanIn)
        {
            if (facts.TryGetValue(nodeId, out var fact))
            {
                fact.FanIn = callers;
            }
        }

        // One bounded forward BFS per entry point; every visited node records the entry point
        // id and keeps the minimum depth. Entry points without a resolvable MethodId contribute
        // nothing here (they are still listed in EntryPoints). The budget diagnostic is emitted
        // once for the whole run, not once per entry point.
        var entryBudgetReported = false;
        var walkedEntryPoints = 0;
        foreach (var entryPoint in entryPoints)
        {
            if (string.IsNullOrWhiteSpace(entryPoint.MethodId) || !facts.ContainsKey(entryPoint.MethodId))
            {
                continue;
            }

            walkedEntryPoints++;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<(string NodeId, int Depth)>();
            queue.Enqueue((entryPoint.MethodId, 0));
            while (queue.Count > 0)
            {
                var (current, depth) = queue.Dequeue();
                if (visited.Count >= MaxVisitedPerEntryPoint)
                {
                    if (!entryBudgetReported)
                    {
                        entryBudgetReported = true;
                        diagnostics.Add($"Reachability budget of {MaxVisitedPerEntryPoint} nodes exhausted while walking from entry point {entryPoint.Id}; deeper reachability facts for this run are incomplete.");
                    }

                    break;
                }

                if (!visited.Add(current) || !facts.TryGetValue(current, out var fact))
                {
                    continue;
                }

                // Exact reachability flag, set for every visited node, unlike the bounded
                // entry-point list above.
                fact.Reachable = true;

                if (fact.ReachableEntryPoints.Count < 16 && !fact.ReachableEntryPoints.Contains(entryPoint.Id))
                {
                    fact.ReachableEntryPoints.Add(entryPoint.Id);
                }

                fact.DepthFromEntryPoint = fact.DepthFromEntryPoint is { } existing ? Math.Min(existing, depth) : depth;
                if (forward.TryGetValue(current, out var targets))
                {
                    for (var i = 0; i < targets.Count; i++)
                    {
                        var target = targets[i];
                        if (!visited.Contains(target))
                        {
                            queue.Enqueue((target, depth + 1));
                        }
                    }
                }
            }
        }

        var (components, componentOfNode, clusters) = ComputeComponents(callGraph.Nodes, edges, forward, facts);
        if (DebugLog.Enabled)
        {
            DebugLog.Count("reachability nodes", facts.Count);
            DebugLog.Count("reachability components", components.Count);
            DebugLog.Count("entry points walked", walkedEntryPoints);
        }
        ComputeReachableBuckets(facts, forward, components, componentOfNode, callGraph.Nodes, diagnostics);
        MarkKeepAlive(callGraph.Nodes, edges, facts);
        return (facts.Values.OrderBy(fact => fact.NodeId, StringComparer.Ordinal).ToList(), clusters, entryBudgetReported);
    }

    /// <summary>
    ///     Unreachable-but-kept-alive marking. Any node targeted by a reflection or DI/
    ///     framework-model edge (or carrying that evidence itself) stays out of the dead-code
    ///     report: `AddSingleton<Foo>()`, `Activator.CreateInstance(typeof(Foo))`, and
    ///     `[McpServerTool]`-style framework callbacks are invoked without a call site Dosai can
    ///     attribute to an entry point. One linear pass over edges and nodes.
    /// </summary>
    private static void MarkKeepAlive(List<MethodNode> nodes, List<MethodCallEdge> edges, Dictionary<string, NodeReachability> facts)
    {
        // Report the evidence kind that actually kept the node alive. The edge's own
        // EvidenceKind is frequently an ordinary call kind while a reflection/framework kind
        // sits in its Evidence list, so naming EvidenceKind unconditionally attributed the
        // decision to the wrong evidence.
        foreach (var edge in edges)
        {
            var kinds = new KeepAliveKinds();
            kinds.See(edge.EvidenceKind);
            foreach (var evidence in edge.Evidence)
            {
                kinds.See(evidence.Kind);
            }

            kinds.KeepAlive(facts, edge.TargetId);
        }

        foreach (var node in nodes)
        {
            var kinds = new KeepAliveKinds();
            foreach (var evidence in node.Evidence)
            {
                kinds.See(evidence.Kind);
            }

            if (node.Identity?.Evidence is { } identityKinds)
            {
                foreach (var kind in identityKinds)
                {
                    kinds.See(kind);
                }
            }

            kinds.KeepAlive(facts, node.Id);
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

        /// <summary>Only unreachable nodes need keeping alive; a reachable node already has its answer.</summary>
        public readonly void KeepAlive(Dictionary<string, NodeReachability> facts, string nodeId)
        {
            if (_first is not { } first || !facts.TryGetValue(nodeId, out var fact) || fact.Reachable)
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
    ///     distinct across groups, so the unstable sort is deterministic. The keys are copied
    ///     into one contiguous array for the sort, so comparisons do not chase each group's edge.
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

        Array.Sort(keys, order, GraphAssembly.CollapseKeyOrder);
        return order;
    }

    /// <summary>
    ///     Dense group id per edge, numbered in first-encounter order, through an open-addressing
    ///     table: each slot packs a group's key hash with its id, and only a hash match reads the
    ///     group's first edge to compare keys - nothing key-sized is stored. About 24 bytes per
    ///     edge at the peak, where a <c>Dictionary</c> keyed by the struct costs 44 per entry
    ///     (entry plus bucket) and briefly holds both tables on every resize.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static int[] AssignCollapseGroups(List<MethodCallEdge> edges, out int groupCount)
    {
        var count = edges.Count;
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
            var hash = key.GetHashCode();
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
    ///     Forward adjacency from the (source, target)-sorted collapsed edge list, built by
    ///     run-length: no per-node hash sets and no copy step. Consecutive equal targets (the
    ///     same pair surviving under different call types) dedupe by adjacency; self-loops stay
    ///     out, as before - component analysis owns them - and a source with nothing but
    ///     self-loops gets no entry. Each list is allocated at its exact size.
    /// </summary>
    private static Dictionary<string, List<string>> BuildForwardAdjacency(List<MethodCallEdge> edges)
    {
        var adjacency = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var index = 0;
        while (index < edges.Count)
        {
            var source = edges[index].SourceId;
            var end = index;
            var distinct = 0;
            string? previous = null;
            for (; end < edges.Count && string.Equals(edges[end].SourceId, source, StringComparison.Ordinal); end++)
            {
                var target = edges[end].TargetId;
                if (!string.Equals(target, source, StringComparison.Ordinal) && !string.Equals(target, previous, StringComparison.Ordinal))
                {
                    distinct++;
                    previous = target;
                }
            }

            if (distinct > 0)
            {
                var targets = new List<string>(distinct);
                for (; index < end; index++)
                {
                    var target = edges[index].TargetId;
                    if (!string.Equals(target, source, StringComparison.Ordinal)
                        && (targets.Count == 0 || !string.Equals(targets[^1], target, StringComparison.Ordinal)))
                    {
                        targets.Add(target);
                    }
                }

                adjacency.Add(source, targets);
            }

            index = end;
        }

        return adjacency;
    }

    /// <summary>
    ///     Distinct-caller counts per node from the (source, target)-sorted edge list: one count
    ///     per run of an equal pair, a single int-valued dictionary instead of the full reverse
    ///     adjacency graph this used to build (and copy) only to read <c>.Count</c> off it.
    /// </summary>
    private static Dictionary<string, int> CountDistinctCallers(List<MethodCallEdge> edges)
    {
        var fanIn = new Dictionary<string, int>(StringComparer.Ordinal);
        var index = 0;
        while (index < edges.Count)
        {
            var edge = edges[index];
            var source = edge.SourceId;
            var target = edge.TargetId;
            do
            {
                index++;
            }
            while (index < edges.Count
                   && string.Equals(edges[index].SourceId, source, StringComparison.Ordinal)
                   && string.Equals(edges[index].TargetId, target, StringComparison.Ordinal));

            if (!string.Equals(source, target, StringComparison.Ordinal))
            {
                fanIn[target] = fanIn.TryGetValue(target, out var callers) ? callers + 1 : 1;
            }
        }

        return fanIn;
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
    ///     Iterative Tarjan over the merged graph. Returns every component (including
    ///     singletons, which the size bucketing needs for the condensation), the node→component map,
    ///     and the recursion clusters. Components come back in Tarjan discovery order, reverse
    ///     topological order of the condensation, so every component's successors precede it.
    /// </summary>
    private static (List<List<string>> Components, Dictionary<string, int> ComponentOfNode, List<RecursionCluster> Clusters) ComputeComponents(List<MethodNode> nodes, List<MethodCallEdge> edges, Dictionary<string, List<string>> forward, Dictionary<string, NodeReachability> facts)
    {
        var index = 0;
        var indices = new Dictionary<string, int>(StringComparer.Ordinal);
        var lowLinks = new Dictionary<string, int>(StringComparer.Ordinal);
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        var callStack = new Stack<string>();
        var components = new List<List<string>>();
        var componentOfNode = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var node in nodes)
        {
            if (!indices.ContainsKey(node.Id))
            {
                StrongConnect(node.Id);
            }
        }

        var clusters = new List<RecursionCluster>();
        for (var componentIndex = 0; componentIndex < components.Count; componentIndex++)
        {
            var component = components[componentIndex];
            if (component.Count <= 1)
            {
                continue;
            }

            foreach (var memberId in component)
            {
                if (facts.TryGetValue(memberId, out var fact))
                {
                    fact.SccId = componentIndex;
                    fact.InRecursiveCycle = true;
                }
            }

            clusters.Add(new RecursionCluster
            {
                Id = $"scc{componentIndex}",
                Size = component.Count,
                MemberIds = component.OrderBy(id => id, StringComparer.Ordinal).Take(MaxClusterMembers).ToList()
            });
        }

        // Self-loops (A→A) are recursive cycles too; BuildForwardAdjacency excluded them from
        // `forward`. Each gets a unique negative SccId so grouping never merges unrelated
        // self-recursive methods (non-negative ids belong to multi-node components, -1 to plain
        // singletons). One scan in edge order, the order the GroupBy it replaced produced.
        // A repeated self-loop (other call type or evidence kind) finds its node already marked.
        var nextSelfLoopId = -2;
        foreach (var edge in edges)
        {
            if (!string.Equals(edge.SourceId, edge.TargetId, StringComparison.Ordinal))
            {
                continue;
            }

            var selfId = edge.SourceId;
            if (facts.TryGetValue(selfId, out var fact) && !fact.InRecursiveCycle)
            {
                fact.InRecursiveCycle = true;
                fact.SccId = nextSelfLoopId--;
                clusters.Add(new RecursionCluster { Id = $"scc-self-{selfId}", Size = 1, MemberIds = [selfId] });
            }
        }

        return (components, componentOfNode, clusters);

        void StrongConnect(string start)
        {
            var work = new Stack<(string Node, int ChildIndex)>();
            work.Push((start, 0));
            indices[start] = lowLinks[start] = index++;
            callStack.Push(start);
            onStack.Add(start);

            while (work.Count > 0)
            {
                var (current, childIndex) = work.Peek();
                var neighbors = forward.GetValueOrDefault(current) ?? [];
                if (childIndex < neighbors.Count)
                {
                    work.Pop();
                    work.Push((current, childIndex + 1));
                    var next = neighbors[childIndex];
                    if (!indices.ContainsKey(next))
                    {
                        indices[next] = lowLinks[next] = index++;
                        callStack.Push(next);
                        onStack.Add(next);
                        work.Push((next, 0));
                    }
                    else if (onStack.Contains(next))
                    {
                        lowLinks[current] = Math.Min(lowLinks[current], indices[next]);
                    }
                }
                else
                {
                    work.Pop();
                    if (lowLinks[current] == indices[current])
                    {
                        var component = new List<string>();
                        string member;
                        do
                        {
                            member = callStack.Pop();
                            onStack.Remove(member);
                            component.Add(member);
                            componentOfNode[member] = components.Count;
                        } while (member != current);

                        components.Add(component);
                    }

                    if (work.Count > 0)
                    {
                        var (parent, _) = work.Peek();
                        lowLinks[parent] = Math.Min(lowLinks[parent], lowLinks[current]);
                    }
                }
            }
        }
    }

    /// <summary>
    ///     Bucketed forward-reachable sizes computed on the SCC condensation in reverse
    ///     topological order (Tarjan discovery order, every component's successors already have
    ///     their set). <c>set(C) = members(C) | ⋃ set(successor components)</c>, so the work is
    ///     O(components × N/8 bytes) bitset merging, near-linear in practice, instead of a full
    ///     BFS per node. Graphs too large for the bitset memory cap fall back to the budgeted
    ///     per-node walk with an explicit diagnostic.
    /// </summary>
    private static void ComputeReachableBuckets(Dictionary<string, NodeReachability> facts, Dictionary<string, List<string>> forward, List<List<string>> components, Dictionary<string, int> componentOfNode, List<MethodNode> nodes, List<string> diagnostics)
    {
        var bucketWatch = DebugLog.Enabled ? System.Diagnostics.Stopwatch.StartNew() : null;
        var nodeCount = nodes.Count;
        var bitsetBytes = (long)components.Count * ((nodeCount + 63) / 64 * 8);
        if (bitsetBytes > MaxBucketBitsetBytes)
        {
            if (DebugLog.Enabled)
            {
                DebugLog.Log($"reachability bucketing path: budgeted walk ({components.Count} components x {nodeCount} nodes would need {DebugLog.FormatBytes(bitsetBytes)} of bitsets, above the {DebugLog.FormatBytes(MaxBucketBitsetBytes)} cap)");
            }
            diagnostics.Add($"Reachable-node bucketing degraded to the budgeted walk ({components.Count} components × {nodeCount} nodes exceed the bitset cap).");
            ComputeReachableBucketsBudgeted(facts, forward, diagnostics);
            if (bucketWatch is not null)
            {
                DebugLog.Log(string.Create(CultureInfo.InvariantCulture, $"reachability bucketing (budgeted walk) completed in {bucketWatch.Elapsed.TotalSeconds:F3}s"));
            }
            return;
        }

        if (DebugLog.Enabled)
        {
            DebugLog.Log($"reachability bucketing path: condensed bitsets ({components.Count} components x {nodeCount} nodes, {DebugLog.FormatBytes(bitsetBytes)} of bitsets)");
        }

        var nodeIndexById = new Dictionary<string, int>(nodeCount, StringComparer.Ordinal);
        for (var index = 0; index < nodeCount; index++)
        {
            nodeIndexById[nodes[index].Id] = index;
        }

        // Condensation edges, deduplicated. Sets are created lazily: most components have no
        // cross-component successors, and one empty HashSet per component is real memory on
        // large graphs.
        var componentSuccessors = new HashSet<int>?[components.Count];
        foreach (var (source, targets) in forward)
        {
            if (!componentOfNode.TryGetValue(source, out var sourceComponent))
            {
                continue;
            }

            foreach (var target in targets)
            {
                if (componentOfNode.TryGetValue(target, out var targetComponent) && sourceComponent != targetComponent)
                {
                    (componentSuccessors[sourceComponent] ??= []).Add(targetComponent);
                }
            }
        }

        var componentSets = new List<BitArray?>(components.Count);
        for (var componentIndex = 0; componentIndex < components.Count; componentIndex++)
        {
            componentSets.Add(null);
        }

        // Tarjan discovery order is reverse topological: successors are processed first, so a
        // component's bitset is the union of its members and its successors' finished sets.
        for (var componentIndex = 0; componentIndex < components.Count; componentIndex++)
        {
            var set = new BitArray(nodeCount);
            foreach (var memberId in components[componentIndex])
            {
                if (nodeIndexById.TryGetValue(memberId, out var memberNodeIndex))
                {
                    set[memberNodeIndex] = true;
                }
            }

            var successors = componentSuccessors[componentIndex];
            if (successors is not null)
            {
                foreach (var successor in successors)
                {
                    if (componentSets[successor] is { } successorSet)
                    {
                        set.Or(successorSet);
                    }
                }
            }

            componentSets[componentIndex] = set;
        }

        foreach (var (nodeId, fact) in facts)
        {
            if (componentOfNode.TryGetValue(nodeId, out var componentIndex) && componentSets[componentIndex] is { } set)
            {
                fact.ReachableNodeBucket = BucketFor(CountBits(set));
            }
        }

        if (bucketWatch is not null)
        {
            DebugLog.Log(string.Create(CultureInfo.InvariantCulture, $"reachability bucketing (condensed bitsets) completed in {bucketWatch.Elapsed.TotalSeconds:F3}s"));
        }
    }

    /// <summary>
    ///     Budgeted per-node fallback for graphs above the bitset cap; degrades to bucket 0 with a
    ///     diagnostic. The degraded flag is raised inside the walk that exhausts the budget, including
    ///     the walk of the final node, so a truncated count is never reported as a confident bucket.
    /// </summary>
    private static void ComputeReachableBucketsBudgeted(Dictionary<string, NodeReachability> facts, Dictionary<string, List<string>> forward, List<string> diagnostics)
    {
        var budget = MaxReachableCountBudget;
        var degraded = false;
        foreach (var nodeId in facts.Keys.OrderBy(id => id, StringComparer.Ordinal).ToList())
        {
            if (budget <= 0)
            {
                degraded = true;
                break;
            }

            var visited = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<string>([nodeId]);
            var truncated = false;
            while (queue.Count > 0)
            {
                if (budget <= 0)
                {
                    // This node's own walk was cut short, so its count is an undercount. Recording
                    // the flag here (not at the top of the next iteration) is what makes an
                    // exhausted budget on the *final* node reportable instead of silently wrong.
                    truncated = true;
                    degraded = true;
                    break;
                }

                var current = queue.Dequeue();
                if (!visited.Add(current))
                {
                    continue;
                }

                budget--;
                if (forward.TryGetValue(current, out var targets))
                {
                    for (var index = 0; index < targets.Count; index++)
                    {
                        var target = targets[index];
                        if (!visited.Contains(target))
                        {
                            queue.Enqueue(target);
                        }
                    }
                }
            }

            // A truncated walk reports bucket 0 ("unknown") rather than an undercounted bucket that
            // would read as a confident fact.
            facts[nodeId].ReachableNodeBucket = truncated ? 0 : BucketFor(visited.Count);
        }

        if (degraded)
        {
            diagnostics.Add($"Reachable-node-count budget of {MaxReachableCountBudget} exhausted; remaining nodes report bucket 0. Use fan-out and entry-point reachability for ranking instead.");
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

    private static int CountBits(BitArray bits)
    {
        var words = new int[(bits.Count + 31) / 32];
        bits.CopyTo(words, 0);
        var count = 0;
        foreach (var word in words)
        {
            count += PopCount((uint)word);
        }

        return count;
    }

    private static int PopCount(uint value)
    {
        var count = 0;
        while (value != 0)
        {
            value &= value - 1;
            count++;
        }

        return count;
    }
}
