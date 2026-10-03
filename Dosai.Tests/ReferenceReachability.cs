using System.Collections;
using System.Globalization;
using Depscan;

namespace Dosai.Tests;

/// <summary>
///     The string-keyed reachability computation the int graph replaced (its output on a full
///     dotnet/runtime scan is byte-identical to main's), kept verbatim apart from taking the
///     limits as a parameter. Differential reference for
///     <see cref="ReachabilityAnalyzer.Compute(CallGraph, IEnumerable{EntryPoint}, List{string})" />.
/// </summary>
internal static class ReferenceReachability
{
    public static (List<NodeReachability> Nodes, List<RecursionCluster> Clusters, bool BudgetExhausted) Compute(CallGraph callGraph, IEnumerable<EntryPoint> entryPoints, List<string> diagnostics, ReachabilityAnalyzer.Limits limits)
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
                if (visited.Count >= limits.VisitedPerEntryPoint)
                {
                    if (!entryBudgetReported)
                    {
                        entryBudgetReported = true;
                        diagnostics.Add($"Reachability budget of {limits.VisitedPerEntryPoint} nodes exhausted while walking from entry point {entryPoint.Id}; deeper reachability facts for this run are incomplete.");
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
        ComputeReachableBuckets(facts, forward, components, componentOfNode, callGraph.Nodes, diagnostics, limits);
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
                MemberIds = component.OrderBy(id => id, StringComparer.Ordinal).Take(ReachabilityAnalyzer.MaxClusterMembers).ToList()
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
    private static void ComputeReachableBuckets(Dictionary<string, NodeReachability> facts, Dictionary<string, List<string>> forward, List<List<string>> components, Dictionary<string, int> componentOfNode, List<MethodNode> nodes, List<string> diagnostics, ReachabilityAnalyzer.Limits limits)
    {
        var bucketWatch = DebugLog.Enabled ? System.Diagnostics.Stopwatch.StartNew() : null;
        var nodeCount = nodes.Count;
        var bitsetBytes = (long)components.Count * ((nodeCount + 63) / 64 * 8);
        if (bitsetBytes > limits.BucketBitsetBytes)
        {
            if (DebugLog.Enabled)
            {
                DebugLog.Log($"reachability bucketing path: budgeted walk ({components.Count} components x {nodeCount} nodes would need {DebugLog.FormatBytes(bitsetBytes)} of bitsets, above the {DebugLog.FormatBytes(limits.BucketBitsetBytes)} cap)");
            }
            diagnostics.Add($"Reachable-node bucketing degraded to the budgeted walk ({components.Count} components × {nodeCount} nodes exceed the bitset cap).");
            ComputeReachableBucketsBudgeted(facts, forward, diagnostics, limits);
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
    private static void ComputeReachableBucketsBudgeted(Dictionary<string, NodeReachability> facts, Dictionary<string, List<string>> forward, List<string> diagnostics, ReachabilityAnalyzer.Limits limits)
    {
        var budget = limits.ReachableCountBudget;
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
            diagnostics.Add($"Reachable-node-count budget of {limits.ReachableCountBudget} exhausted; remaining nodes report bucket 0. Use fan-out and entry-point reachability for ranking instead.");
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
    }}
