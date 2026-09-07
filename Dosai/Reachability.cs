using System.Collections;
using System.Text.RegularExpressions;

namespace Depscan;

/// <summary>
///     Normalization helpers for graph node ids shared by source/assembly id matching (R10): IL ids
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
    ///     <c>Ns.G.Echo(T):T</c> both reduce to <c>Ns.G.Echo</c>. Used only as an R10 fallback for
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
///     Per-node reachability facts computed once over the merged call graph (R1): which entry
///     points can reach the node, how deep it sits, how much code it can reach, plus fan-in/
///     fan-out (R7) and SCC membership (R3).
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

    /// <summary>Distinct callers (R7 fan-in).</summary>
    public int FanIn { get; set; }

    /// <summary>Distinct callees (R7 fan-out).</summary>
    public int FanOut { get; set; }

    /// <summary>
    ///     Strongly-connected-component id. Multi-node components share a non-negative id; plain
    ///     singletons are -1; every self-loop gets its own unique negative id so grouping by SccId
    ///     never merges unrelated self-recursive methods.
    /// </summary>
    public int SccId { get; set; } = -1;

    public bool InRecursiveCycle { get; set; }
}

/// <summary>A recursion cluster: an SCC with more than one member, or a self-loop (R3).</summary>
public sealed class RecursionCluster
{
    public required string Id { get; set; }

    public int Size { get; set; }

    /// <summary>Bounded member list (≤ <see cref="ReachabilityAnalyzer.MaxClusterMembers"/>); full membership is derivable from NodeReachability.SccId.</summary>
    public List<string> MemberIds { get; set; } = [];
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
    ///     deduplicated) edge list; builds forward and reverse indexes once; one bounded BFS per
    ///     resolvable entry point — never per node.
    /// </summary>
    public static (List<NodeReachability> Nodes, List<RecursionCluster> Clusters) Compute(CallGraph callGraph, IEnumerable<EntryPoint> entryPoints, List<string> diagnostics)
    {
        var forward = BuildAdjacency(callGraph.Edges, forward: true);
        var reverse = BuildAdjacency(callGraph.Edges, forward: false);
        var facts = callGraph.Nodes.ToDictionary(node => node.Id, node => new NodeReachability { NodeId = node.Id }, StringComparer.Ordinal);

        foreach (var (nodeId, targets) in forward)
        {
            if (facts.TryGetValue(nodeId, out var fact))
            {
                fact.FanOut = targets.Count;
            }
        }

        foreach (var (nodeId, callers) in reverse)
        {
            if (facts.TryGetValue(nodeId, out var fact))
            {
                fact.FanIn = callers.Count;
            }
        }

        // R1: one bounded forward BFS per entry point; every visited node records the entry point
        // id and keeps the minimum depth. Entry points without a resolvable MethodId contribute
        // nothing here (they are still listed in EntryPoints). The budget diagnostic is emitted
        // once for the whole run, not once per entry point.
        var entryBudgetReported = false;
        foreach (var entryPoint in entryPoints)
        {
            if (string.IsNullOrWhiteSpace(entryPoint.MethodId) || !facts.ContainsKey(entryPoint.MethodId))
            {
                continue;
            }

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

                if (fact.ReachableEntryPoints.Count < 16 && !fact.ReachableEntryPoints.Contains(entryPoint.Id))
                {
                    fact.ReachableEntryPoints.Add(entryPoint.Id);
                }

                fact.DepthFromEntryPoint = fact.DepthFromEntryPoint is { } existing ? Math.Min(existing, depth) : depth;
                if (forward.TryGetValue(current, out var targets))
                {
                    foreach (var target in targets.Where(target => !visited.Contains(target)))
                    {
                        queue.Enqueue((target, depth + 1));
                    }
                }
            }
        }

        var (components, componentOfNode, clusters) = ComputeComponents(callGraph, forward, facts);
        ComputeReachableBuckets(facts, forward, components, componentOfNode, callGraph.Nodes, diagnostics);
        return (facts.Values.OrderBy(fact => fact.NodeId, StringComparer.Ordinal).ToList(), clusters);
    }

    /// <summary>
    ///     R7: annotate each edge with the number of distinct call sites for its (source, target)
    ///     pair and collapse same-pair duplicates into a single edge. Edges with different call
    ///     types or evidence kinds stay separate — a direct edge and a dispatch-candidate edge are
    ///     different facts even between the same nodes. The annotations of dropped duplicates
    ///     (argument expressions, evidence, distinct sites as <see cref="MethodCallEdge.CallSiteCount"/>)
    ///     merge into the keeper; downstream consumers (R2 exploit chains) operate on method ids,
    ///     not per-site locations, so nothing they need is lost.
    /// </summary>
    public static void CollapseDuplicateCallSites(CallGraph callGraph)
    {
        var collapsed = new List<MethodCallEdge>();
        foreach (var group in callGraph.Edges
                     .GroupBy(edge => $"{edge.SourceId}\u001f{edge.TargetId}\u001f{edge.CallType}\u001f{edge.EvidenceKind}", StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var edges = group.ToList();
            var keeper = edges
                .OrderBy(edge => edge.CallLocation?.FileName ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(edge => edge.CallLocation?.LineNumber ?? 0)
                .ThenBy(edge => edge.CallLocation?.ColumnNumber ?? 0)
                .First();
            keeper.CallSiteCount = edges
                .Select(edge => $"{edge.CallLocation?.FileName}:{edge.CallLocation?.LineNumber}:{edge.CallLocation?.ColumnNumber}")
                .Distinct(StringComparer.Ordinal)
                .Count();

            // Merge the annotations of the dropped duplicates (argument expressions, evidence)
            // so collapsing never discards how the call was made — only where it was repeated.
            keeper.Arguments = edges.SelectMany(edge => edge.Arguments ?? []).Distinct(StringComparer.Ordinal).ToList();
            keeper.ArgumentExpressions = edges.SelectMany(edge => edge.ArgumentExpressions ?? []).Distinct(StringComparer.Ordinal).ToList();
            keeper.Evidence = edges.SelectMany(edge => edge.Evidence)
                .DistinctBy(evidence => (evidence.Kind, evidence.Source, evidence.Description, evidence.FileName, evidence.LineNumber, evidence.ColumnNumber))
                .ToList();
            collapsed.Add(keeper);
        }

        callGraph.Edges = collapsed;
    }

    private static Dictionary<string, List<string>> BuildAdjacency(List<MethodCallEdge> edges, bool forward)
    {
        var adjacency = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var edge in edges)
        {
            var from = forward ? edge.SourceId : edge.TargetId;
            var to = forward ? edge.TargetId : edge.SourceId;
            if (string.Equals(from, to, StringComparison.Ordinal))
            {
                continue; // self-loops are handled by component analysis
            }

            if (!adjacency.TryGetValue(from, out var targets))
            {
                targets = [];
                adjacency[from] = targets;
            }

            targets.Add(to);
        }

        return adjacency.ToDictionary(pair => pair.Key, pair => pair.Value.ToList(), StringComparer.Ordinal);
    }

    /// <summary>
    ///     Iterative Tarjan over the merged graph (R3). Returns every component (including
    ///     singletons, which R12 bucketing needs for the condensation), the node→component map,
    ///     and the recursion clusters. Components come back in Tarjan discovery order — reverse
    ///     topological order of the condensation, so every component's successors precede it.
    /// </summary>
    private static (List<List<string>> Components, Dictionary<string, int> ComponentOfNode, List<RecursionCluster> Clusters) ComputeComponents(CallGraph callGraph, Dictionary<string, List<string>> forward, Dictionary<string, NodeReachability> facts)
    {
        var index = 0;
        var indices = new Dictionary<string, int>(StringComparer.Ordinal);
        var lowLinks = new Dictionary<string, int>(StringComparer.Ordinal);
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        var callStack = new Stack<string>();
        var components = new List<List<string>>();
        var componentOfNode = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var node in callGraph.Nodes)
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

        // Self-loops (A→A) are recursive cycles too; BuildAdjacency excluded them from `forward`.
        // Each gets a unique negative SccId so grouping never merges unrelated self-recursive
        // methods (non-negative ids belong to multi-node components, -1 to plain singletons).
        var nextSelfLoopId = -2;
        foreach (var edge in callGraph.Edges.Where(edge => string.Equals(edge.SourceId, edge.TargetId, StringComparison.Ordinal)).GroupBy(edge => edge.SourceId, StringComparer.Ordinal))
        {
            if (!facts.TryGetValue(edge.Key, out var fact) || fact.InRecursiveCycle)
            {
                continue;
            }

            fact.InRecursiveCycle = true;
            fact.SccId = nextSelfLoopId--;
            clusters.Add(new RecursionCluster { Id = $"scc-self-{edge.Key}", Size = 1, MemberIds = [edge.Key] });
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
    ///     R7/R12: bucketed forward-reachable sizes computed on the SCC condensation in reverse
    ///     topological order (Tarjan discovery order — every component's successors already have
    ///     their set). <c>set(C) = members(C) | ⋃ set(successor components)</c>, so the work is
    ///     O(components × N/8 bytes) bitset merging — near-linear in practice — instead of a full
    ///     BFS per node. Graphs too large for the bitset memory cap fall back to the budgeted
    ///     per-node walk with an explicit diagnostic.
    /// </summary>
    private static void ComputeReachableBuckets(Dictionary<string, NodeReachability> facts, Dictionary<string, List<string>> forward, List<List<string>> components, Dictionary<string, int> componentOfNode, List<MethodNode> nodes, List<string> diagnostics)
    {
        var nodeCount = nodes.Count;
        var bitsetBytes = (long)components.Count * ((nodeCount + 63) / 64 * 8);
        if (bitsetBytes > MaxBucketBitsetBytes)
        {
            diagnostics.Add($"Reachable-node bucketing degraded to the budgeted walk ({components.Count} components × {nodeCount} nodes exceed the bitset cap).");
            ComputeReachableBucketsBudgeted(facts, forward, diagnostics);
            return;
        }

        var nodeIndexById = new Dictionary<string, int>(nodeCount, StringComparer.Ordinal);
        for (var index = 0; index < nodeCount; index++)
        {
            nodeIndexById[nodes[index].Id] = index;
        }

        // Condensation edges, deduplicated.
        var componentSuccessors = new List<HashSet<int>>(components.Count);
        for (var componentIndex = 0; componentIndex < components.Count; componentIndex++)
        {
            componentSuccessors.Add([]);
        }

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
                    componentSuccessors[sourceComponent].Add(targetComponent);
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

            foreach (var successor in componentSuccessors[componentIndex])
            {
                if (componentSets[successor] is { } successorSet)
                {
                    set.Or(successorSet);
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
    }

    /// <summary>
    ///     Budgeted per-node fallback for graphs above the bitset cap; degrades to bucket 0 with a
    ///     diagnostic. The degraded flag is raised inside the walk that exhausts the budget — including
    ///     the walk of the final node — so a truncated count is never reported as a confident bucket.
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
                    foreach (var target in targets.Where(target => !visited.Contains(target)))
                    {
                        queue.Enqueue(target);
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
