using System.Text.Json;
using System.Text.Json.Serialization;

namespace Depscan;

public sealed class AnalysisMetadata
{
    public string SchemaVersion { get; set; } = "4.1.0";
    public string AnalyzerVersion { get; set; } = typeof(Dosai).Assembly.GetName().Version?.ToString() ?? "4.1.0";
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? InputPath { get; set; }
    public string Tool { get; set; } = "Dosai";
}

public sealed class EntryPoint
{
    public required string Id { get; set; }
    public required string Kind { get; set; }
    public string? MethodId { get; set; }
    public string? MethodName { get; set; }
    public string? ClassName { get; set; }
    public string? Namespace { get; set; }
    public string? FileName { get; set; }
    public string? Path { get; set; }
    public int LineNumber { get; set; }
    public int ColumnNumber { get; set; }
    public string? HttpMethod { get; set; }
    public string? Route { get; set; }
    public bool? AuthorizationRequired { get; set; }
    public List<string> AuthorizationPolicies { get; set; } = [];
    public List<string> Roles { get; set; } = [];
    public bool AllowAnonymous { get; set; }
    public List<string> AuthenticationSchemes { get; set; } = [];
    public List<string> RequiredClaims { get; set; } = [];
    public List<string> RequiredScopes { get; set; } = [];
    public List<string> CorsPolicies { get; set; } = [];
    public bool? AntiForgeryRequired { get; set; }
    public List<string> RawUrls { get; set; } = [];
    public List<string> InputNames { get; set; } = [];
}

public sealed class ReachabilityLocation
{
    public string? Path { get; set; }
    public string? FileName { get; set; }
    public int LineNumber { get; set; }
    public int ColumnNumber { get; set; }
    public string Kind { get; set; } = "Unknown";
}

public sealed class PackageReachability
{
    public required string Purl { get; set; }
    public bool Reachable { get; set; }
    public string ReachabilityKind { get; set; } = "Unknown";
    public string Confidence { get; set; } = "Medium";
    public List<string> ConfidenceReasons { get; set; } = [];
    public List<AnalysisEvidenceKind> EvidenceKinds { get; set; } = [];
    public List<string> NodeIds { get; set; } = [];
    public List<string> EdgeIds { get; set; } = [];
    public List<string> SliceIds { get; set; } = [];
    public List<string> EntryPointIds { get; set; } = [];
    public List<string> Categories { get; set; } = [];
    public List<ReachabilityLocation> SourceLocations { get; set; } = [];
}

public sealed class DangerousApiReachability
{
    public required string Id { get; set; }
    public required string Category { get; set; }
    public string? Symbol { get; set; }
    public string? Purl { get; set; }
    public List<string> EntryPointIds { get; set; } = [];
    public List<string> SliceIds { get; set; } = [];
    public List<string> NodeIds { get; set; } = [];
    public string Confidence { get; set; } = "Medium";
    public List<string> Evidence { get; set; } = [];
}

public sealed class WeaknessCandidate
{
    public required string Id { get; set; }
    public required string Kind { get; set; }
    public string? Cwe { get; set; }
    public string Confidence { get; set; } = "Medium";
    /// <summary>Derived severity (info/low/medium/high/critical) from the sink category or pattern override.</summary>
    public string Severity { get; set; } = "medium";
    public List<string> ConfidenceReasons { get; set; } = [];
    public string? SliceId { get; set; }
    public string? SourceId { get; set; }
    public string? SinkId { get; set; }
    public string? SourceCategory { get; set; }
    public string? SinkCategory { get; set; }
    public string? SourceLocation { get; set; }
    public string? SinkLocation { get; set; }
    public string? EntryPointId { get; set; }
    public string? Route { get; set; }
    public List<string> Purls { get; set; } = [];
    public List<string> Evidence { get; set; } = [];
    public string? Summary { get; set; }
}

public sealed class AgentContext
{
    public AnalysisMetadata Metadata { get; set; } = new();
    public string Summary { get; set; } = string.Empty;
    public List<EntryPoint> EntryPoints { get; set; } = [];
    public List<WeaknessCandidate> HighRiskWeaknesses { get; set; } = [];
    public List<DataFlowSlice> HighRiskSlices { get; set; } = [];
    public List<PackageReachability> ReachablePackages { get; set; } = [];
    /// <summary>F6: bounded attack-surface groups for top-down triage.</summary>
    public List<AttackSurfaceGroup> AttackSurface { get; set; } = [];
    public List<string> RelevantFiles { get; set; } = [];
    public List<string> SuggestedNextCommands { get; set; } = [];
}

/// <summary>
///     F6: one row per entry point in the attack-surface view — what reaches it (exploit chains,
///     weakness candidates, CWEs) so an analyst can triage top-down by exposure.
/// </summary>
public sealed class AttackSurfaceEntry
{
    public required string EntryPointId { get; set; }
    public required string Exposure { get; set; }
    public string? Kind { get; set; }
    public string? HttpMethod { get; set; }
    public string? Route { get; set; }
    public string? FileName { get; set; }
    public int LineNumber { get; set; }
    public bool AllowAnonymous { get; set; }
    public int ExploitChainCount { get; set; }
    public int WeaknessCount { get; set; }
    public int HighSeverityWeaknessCount { get; set; }
    public List<string> WeaknessKinds { get; set; } = [];
    public List<string> Cwes { get; set; } = [];
    public List<string> SinkCategories { get; set; } = [];
}

/// <summary>
///     F6: entry points grouped by exposure (anonymous-http, authenticated-http, rpc, cli, queue,
///     mcp, …) with per-group rollups. The group is the triage unit: anonymous routes with
///     high-severity chains sort to the top of a review.
/// </summary>
public sealed class AttackSurfaceGroup
{
    public required string Exposure { get; set; }
    public int EntryPointCount { get; set; }
    public int ExploitChainCount { get; set; }
    public int WeaknessCount { get; set; }
    public int HighSeverityWeaknessCount { get; set; }

    /// <summary>
    ///     True when <see cref="EntryPoints"/> lists fewer rows than <see cref="EntryPointCount"/>.
    ///     The rollup counts above always cover every entry point in the group; only the row list
    ///     is capped, so a consumer needs this to tell a short list from a complete one.
    /// </summary>
    public bool EntryPointsTruncated { get; set; }

    public List<AttackSurfaceEntry> EntryPoints { get; set; } = [];
}

public static class TransparencyBuilder
{
    public static AnalysisMetadata CreateMetadata(string inputPath) => new() { InputPath = inputPath };

    /// <summary>
    ///     Maps an endpoint kind (Attribute, MinimalApi, and the framework kinds added in schema
    ///     4.0.0) onto the entry-point kind. HTTP entry points no longer use "HttpEndpoint";
    ///     controllers are "HttpController".
    /// </summary>
    private static string EntryPointKindFor(ApiEndpoint endpoint) => endpoint.EndpointKind switch
    {
        "MinimalApi" => "HttpMinimalApi",
        "Grpc" => "Grpc",
        "SignalRHub" => "SignalRHub",
        "GrainMethod" => "GrainMethod",
        "Soap" => "Soap",
        "GraphQL" => "GraphQL",
        "OData" => "OData",
        "RazorPage" => "HttpRazorPage",
        "AzureFunction" => "AzureFunction",
        "LambdaFunction" => "LambdaFunction",
        "MessageConsumer" => "MessageConsumer",
        "ScheduledJob" or "HostedService" => "HostedService",
        "McpTool" => "McpTool",
        "McpPrompt" => "McpPrompt",
        "McpResource" => "McpResource",
        _ => "HttpController"
    };

    /// <summary>
    ///     R6: CLI entry-point method names. `<Main>$` is the compiler-synthesized entry point for
    ///     top-level statements (the default `dotnet new console` template); declared entry points —
    ///     including the `async Task`/`Task&lt;int&gt;`/`int` variants — are all literally named Main.
    /// </summary>
    internal static bool IsCliEntryPointName(string? name) => name is "Main" or "<Main>$";

    public static List<EntryPoint> BuildEntryPoints(IEnumerable<ApiEndpoint> apiEndpoints, IEnumerable<Method>? methods = null)
    {
        var entries = new List<EntryPoint>();
        var index = 0;
        foreach (var endpoint in apiEndpoints)
        {
            entries.Add(new EntryPoint
            {
                Id = $"ep{++index}",
                Kind = EntryPointKindFor(endpoint),
                MethodName = endpoint.MethodName,
                ClassName = endpoint.ClassName,
                Namespace = endpoint.Namespace,
                FileName = endpoint.FileName,
                Path = endpoint.FilePath ?? endpoint.Path,
                LineNumber = endpoint.LineNumber,
                ColumnNumber = endpoint.ColumnNumber,
                HttpMethod = endpoint.HttpMethod,
                Route = endpoint.Path ?? endpoint.Route,
                RawUrls = endpoint.RawUrls,
                AuthorizationRequired = endpoint.AuthorizationRequired,
                AuthorizationPolicies = endpoint.AuthorizationPolicies,
                Roles = endpoint.Roles,
                AllowAnonymous = endpoint.AllowAnonymous,
                AuthenticationSchemes = endpoint.AuthenticationSchemes,
                RequiredClaims = endpoint.RequiredClaims,
                RequiredScopes = endpoint.RequiredScopes,
                CorsPolicies = endpoint.CorsPolicies,
                AntiForgeryRequired = endpoint.AntiForgeryRequired
            });
        }

        if (methods is not null)
        {
            foreach (var method in methods.Where(m => IsCliEntryPointName(m.Name)))
            {
                entries.Add(new EntryPoint
                {
                    Id = $"ep{++index}",
                    Kind = "Cli",
                    MethodId = method.SourceSignature ?? method.AssemblySignature,
                    MethodName = method.Name,
                    ClassName = method.ClassName,
                    Namespace = method.Namespace,
                    FileName = method.FileName,
                    Path = method.Path,
                    LineNumber = method.LineNumber,
                    ColumnNumber = method.ColumnNumber,
                    InputNames = method.Parameters?.Select(p => p.Name ?? string.Empty).Where(name => !string.IsNullOrWhiteSpace(name)).ToList() ?? []
                });
            }
        }

        return entries;
    }

    public static List<PackageReachability> BuildPackageReachability(CallGraph callGraph, IEnumerable<DataFlowSlice>? slices = null, IEnumerable<Dependency>? dependencies = null)
    {
        var byPurl = new Dictionary<string, PackageReachability>(StringComparer.Ordinal);

        foreach (var node in callGraph.Nodes)
        {
            Add(node.Purl, node.IsExternal ? "ExternalCallGraphNode" : "InternalCallGraphNode", node.Id, evidenceKinds: NodeEvidenceKinds(node), sourceLocation: SourceLocationFromNode(node, "CallGraphNode"));
        }
        foreach (var edge in callGraph.Edges)
        {
            var evidenceKinds = EdgeEvidenceKinds(edge);
            var sourceLocation = SourceLocationFromEdge(edge, "CallGraphEdge");
            Add(edge.SourcePurl, "CallGraphEdge", edge.SourceId, edge.Id, evidenceKinds: evidenceKinds, sourceLocation: sourceLocation);
            Add(edge.TargetPurl, "CallGraphEdge", edge.TargetId, edge.Id, category: edge.CallType.ToString(), evidenceKinds: evidenceKinds, sourceLocation: sourceLocation);
        }
        if (slices is not null)
        {
            foreach (var slice in slices)
            {
                foreach (var purl in slice.Purls)
                {
                    Add(purl, "DataFlowSlice", sliceId: slice.Id, category: slice.SinkCategory, confidence: slice.Confidence);
                }
            }
        }
        foreach (var dependency in dependencies ?? [])
        {
            Add(dependency.Purl, "Dependency", category: dependency.Name ?? dependency.Namespace, sourceLocation: SourceLocationFromDependency(dependency, "Dependency"));
        }
        FinalizeConfidence(byPurl.Values);
        return byPurl.Values.OrderBy(p => p.Purl, StringComparer.Ordinal).ToList();

        void Add(string? purl, string kind, string? nodeId = null, string? edgeId = null, string? sliceId = null, string? category = null, IEnumerable<AnalysisEvidenceKind>? evidenceKinds = null, string? confidence = null, ReachabilityLocation? sourceLocation = null)
        {
            if (string.IsNullOrWhiteSpace(purl)) return;
            if (!byPurl.TryGetValue(purl, out var reachability))
            {
                reachability = new PackageReachability { Purl = purl, Reachable = true, ReachabilityKind = kind };
                byPurl[purl] = reachability;
            }
            if (nodeId is not null && !reachability.NodeIds.Contains(nodeId)) reachability.NodeIds.Add(nodeId);
            if (edgeId is not null && !reachability.EdgeIds.Contains(edgeId)) reachability.EdgeIds.Add(edgeId);
            if (sliceId is not null && !reachability.SliceIds.Contains(sliceId)) reachability.SliceIds.Add(sliceId);
            if (category is not null && !reachability.Categories.Contains(category)) reachability.Categories.Add(category);
            AddSourceLocation(reachability, sourceLocation);
            foreach (var evidenceKind in evidenceKinds ?? [])
            {
                if (!reachability.EvidenceKinds.Contains(evidenceKind)) reachability.EvidenceKinds.Add(evidenceKind);
            }
            if (!string.IsNullOrWhiteSpace(confidence)) AddConfidenceReason(reachability, $"Data-flow slice confidence is {confidence}.");
            if (kind == "Dependency") AddConfidenceReason(reachability, "Package URL is supported by dependency/import metadata.");
        }
    }

    public static List<PackageReachability> BuildPackageReachability(DataFlowResult result)
    {
        var byPurl = new Dictionary<string, PackageReachability>(StringComparer.Ordinal);
        var nodesById = result.Nodes
            .GroupBy(node => node.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var edgesById = result.Edges
            .GroupBy(edge => edge.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        foreach (var node in result.Nodes) Add(node.Purl, "DataFlowNode", node.Id, category: node.Category, evidenceKinds: node.Evidence.Select(evidence => evidence.Kind), sourceLocation: SourceLocationFromDataFlowNode(node, "DataFlowNode"));
        foreach (var edge in result.Edges)
        {
            var sourceLocation = SourceLocationFromDataFlowEdge(edge, "DataFlowEdge");
            Add(edge.SourcePurl, "DataFlowEdge", edge.SourceId, edge.Id, sourceLocation: sourceLocation);
            Add(edge.TargetPurl, "DataFlowEdge", edge.TargetId, edge.Id, sourceLocation: sourceLocation);
        }
        foreach (var slice in result.Slices)
        {
            foreach (var purl in slice.Purls)
            {
                Add(purl, "DataFlowSlice", sliceId: slice.Id, category: slice.SinkCategory, confidence: slice.Confidence, sourceLocations: SourceLocationsForSlice(slice, purl, nodesById, edgesById));
            }
        }
        FinalizeConfidence(byPurl.Values);
        return byPurl.Values.OrderBy(p => p.Purl, StringComparer.Ordinal).ToList();

        void Add(string? purl, string kind, string? nodeId = null, string? edgeId = null, string? sliceId = null, string? category = null, IEnumerable<AnalysisEvidenceKind>? evidenceKinds = null, string? confidence = null, ReachabilityLocation? sourceLocation = null, IEnumerable<ReachabilityLocation>? sourceLocations = null)
        {
            if (string.IsNullOrWhiteSpace(purl)) return;
            if (!byPurl.TryGetValue(purl, out var reachability))
            {
                reachability = new PackageReachability { Purl = purl, Reachable = true, ReachabilityKind = kind };
                byPurl[purl] = reachability;
            }
            if (nodeId is not null && !reachability.NodeIds.Contains(nodeId)) reachability.NodeIds.Add(nodeId);
            if (edgeId is not null && !reachability.EdgeIds.Contains(edgeId)) reachability.EdgeIds.Add(edgeId);
            if (sliceId is not null && !reachability.SliceIds.Contains(sliceId)) reachability.SliceIds.Add(sliceId);
            if (category is not null && !reachability.Categories.Contains(category)) reachability.Categories.Add(category);
            AddSourceLocation(reachability, sourceLocation);
            foreach (var location in sourceLocations ?? []) AddSourceLocation(reachability, location);
            foreach (var evidenceKind in evidenceKinds ?? [])
            {
                if (!reachability.EvidenceKinds.Contains(evidenceKind)) reachability.EvidenceKinds.Add(evidenceKind);
            }
            if (!string.IsNullOrWhiteSpace(confidence)) AddConfidenceReason(reachability, $"Data-flow slice confidence is {confidence}.");
        }
    }


    private static IEnumerable<ReachabilityLocation> SourceLocationsForSlice(DataFlowSlice slice, string purl, IReadOnlyDictionary<string, DataFlowNode> nodesById, IReadOnlyDictionary<string, DataFlowEdge> edgesById)
    {
        var matchedPurlCarrier = false;
        foreach (var nodeId in slice.NodeIds)
        {
            if (!nodesById.TryGetValue(nodeId, out var node) || !string.Equals(node.Purl, purl, StringComparison.Ordinal)) continue;
            matchedPurlCarrier = true;
            if (SourceLocationFromDataFlowNode(node, "DataFlowSlice") is { } location) yield return location;
        }

        foreach (var edgeId in slice.EdgeIds)
        {
            if (!edgesById.TryGetValue(edgeId, out var edge) ||
                (!string.Equals(edge.SourcePurl, purl, StringComparison.Ordinal) && !string.Equals(edge.TargetPurl, purl, StringComparison.Ordinal))) continue;
            matchedPurlCarrier = true;
            if (SourceLocationFromDataFlowEdge(edge, "DataFlowSlice") is { } location) yield return location;
        }

        if (matchedPurlCarrier) yield break;

        if (string.Equals(slice.SinkPurl, purl, StringComparison.Ordinal) && nodesById.TryGetValue(slice.SinkId, out var sinkNode))
        {
            if (SourceLocationFromDataFlowNode(sinkNode, "DataFlowSlice") is { } sinkLocation) yield return sinkLocation;
            yield break;
        }

        if (string.Equals(slice.SourcePurl, purl, StringComparison.Ordinal) && nodesById.TryGetValue(slice.SourceId, out var sourceNode))
        {
            if (SourceLocationFromDataFlowNode(sourceNode, "DataFlowSlice") is { } sourceLocation) yield return sourceLocation;
            yield break;
        }

        if (nodesById.TryGetValue(slice.SinkId, out var fallbackSinkNode) && fallbackSinkNode.IsSink)
        {
            if (SourceLocationFromDataFlowNode(fallbackSinkNode, "DataFlowSlice") is { } fallbackLocation) yield return fallbackLocation;
        }
    }


    private static ReachabilityLocation? SourceLocationFromNode(MethodNode node, string kind)
    {
        if (!IsSourceFile(node.FileName) || node.LineNumber <= 0) return null;
        return new ReachabilityLocation
        {
            Path = node.FileName,
            FileName = System.IO.Path.GetFileName(node.FileName),
            LineNumber = node.LineNumber,
            ColumnNumber = node.ColumnNumber,
            Kind = kind
        };
    }

    private static ReachabilityLocation? SourceLocationFromEdge(MethodCallEdge edge, string kind)
    {
        var path = edge.Path ?? edge.CallLocation.FileName ?? edge.FileName;
        if (!IsSourceFile(path) || edge.CallLocation.LineNumber <= 0) return null;
        return new ReachabilityLocation
        {
            Path = path,
            FileName = System.IO.Path.GetFileName(path),
            LineNumber = edge.CallLocation.LineNumber,
            ColumnNumber = edge.CallLocation.ColumnNumber,
            Kind = kind
        };
    }

    private static ReachabilityLocation? SourceLocationFromDataFlowNode(DataFlowNode node, string kind)
    {
        var fileName = node.Path ?? node.FileName;
        if (!IsSourceFile(fileName) || node.LineNumber <= 0) return null;
        return new ReachabilityLocation
        {
            Path = fileName,
            FileName = System.IO.Path.GetFileName(fileName),
            LineNumber = node.LineNumber,
            ColumnNumber = node.ColumnNumber,
            Kind = kind
        };
    }

    private static ReachabilityLocation? SourceLocationFromDataFlowEdge(DataFlowEdge edge, string kind)
    {
        var path = edge.Path ?? edge.FileName;
        if (!IsSourceFile(path) || edge.LineNumber <= 0) return null;
        return new ReachabilityLocation
        {
            Path = path,
            FileName = System.IO.Path.GetFileName(path),
            LineNumber = edge.LineNumber,
            ColumnNumber = edge.ColumnNumber,
            Kind = kind
        };
    }

    private static ReachabilityLocation? SourceLocationFromDependency(Dependency dependency, string kind)
    {
        var path = dependency.Path ?? dependency.FileName;
        if (!IsSourceFile(path) || dependency.LineNumber <= 0) return null;
        return new ReachabilityLocation
        {
            Path = path,
            FileName = System.IO.Path.GetFileName(path),
            LineNumber = dependency.LineNumber,
            ColumnNumber = dependency.ColumnNumber,
            Kind = kind
        };
    }

    private static bool IsSourceFile(string? fileName) =>
        !string.IsNullOrWhiteSpace(fileName) &&
        (fileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
         fileName.EndsWith(".csx", StringComparison.OrdinalIgnoreCase) ||
         fileName.EndsWith(".vb", StringComparison.OrdinalIgnoreCase) ||
         fileName.EndsWith(".fs", StringComparison.OrdinalIgnoreCase) ||
         fileName.EndsWith(".fsi", StringComparison.OrdinalIgnoreCase) ||
         fileName.EndsWith(".fsx", StringComparison.OrdinalIgnoreCase) ||
         fileName.EndsWith(".r", StringComparison.OrdinalIgnoreCase) ||
         fileName.EndsWith(".rmd", StringComparison.OrdinalIgnoreCase) ||
         fileName.EndsWith(".qmd", StringComparison.OrdinalIgnoreCase));

    private static void AddSourceLocation(PackageReachability reachability, ReachabilityLocation? location)
    {
        if (location is null) return;
        if (reachability.SourceLocations.Any(existing =>
            string.Equals(existing.Path, location.Path, StringComparison.Ordinal) &&
            existing.LineNumber == location.LineNumber &&
            existing.ColumnNumber == location.ColumnNumber &&
            string.Equals(existing.Kind, location.Kind, StringComparison.Ordinal))) return;
        reachability.SourceLocations.Add(location);
    }

    private static IEnumerable<AnalysisEvidenceKind> NodeEvidenceKinds(MethodNode node)
    {
        foreach (var evidenceKind in node.Identity?.Evidence ?? []) yield return evidenceKind;
        foreach (var evidenceKind in node.Evidence.Select(evidence => evidence.Kind)) yield return evidenceKind;
    }

    private static IEnumerable<AnalysisEvidenceKind> EdgeEvidenceKinds(MethodCallEdge edge)
    {
        if (edge.EvidenceKind != AnalysisEvidenceKind.Unknown) yield return edge.EvidenceKind;
        foreach (var evidenceKind in edge.Evidence.Select(evidence => evidence.Kind)) yield return evidenceKind;
    }

    private static void FinalizeConfidence(IEnumerable<PackageReachability> reachabilityFacts)
    {
        foreach (var reachability in reachabilityFacts)
        {
            if (reachability.EvidenceKinds.Any(kind => EvidenceScore(kind) >= 3))
            {
                reachability.Confidence = "High";
                AddConfidenceReason(reachability, "Reachability is supported by direct source or IL call evidence.");
            }
            else if (reachability.EvidenceKinds.Any(kind => EvidenceScore(kind) >= 2) || reachability.SliceIds.Count > 0)
            {
                reachability.Confidence = "Medium";
                AddConfidenceReason(reachability, "Reachability is supported by summaries, metadata, or data-flow slices.");
            }
            else
            {
                reachability.Confidence = "Low";
                AddConfidenceReason(reachability, "Reachability is inferred from heuristic or unresolved evidence.");
            }
            reachability.EvidenceKinds = reachability.EvidenceKinds.Distinct().OrderBy(kind => kind.ToString(), StringComparer.Ordinal).ToList();
            reachability.ConfidenceReasons = reachability.ConfidenceReasons.Distinct(StringComparer.Ordinal).ToList();
            reachability.SourceLocations = reachability.SourceLocations
                .OrderBy(location => location.Path ?? location.FileName, StringComparer.Ordinal)
                .ThenBy(location => location.LineNumber)
                .ThenBy(location => location.ColumnNumber)
                .ThenBy(location => location.Kind, StringComparer.Ordinal)
                .ToList();
        }
    }

    private static int EvidenceScore(AnalysisEvidenceKind kind) => kind switch
    {
        AnalysisEvidenceKind.SourceRoslynDirect or AnalysisEvidenceKind.AssemblyIlDirect or AnalysisEvidenceKind.AssemblyIlGeneratedState or AnalysisEvidenceKind.AssemblyIlDelegateTarget or AnalysisEvidenceKind.SourceRoslynDelegateTarget => 3,
        AnalysisEvidenceKind.SourceRoslynSummary or AnalysisEvidenceKind.AssemblyIlSummary or AnalysisEvidenceKind.AssemblyReflection or AnalysisEvidenceKind.ExternalSummary or AnalysisEvidenceKind.FrameworkModel => 2,
        AnalysisEvidenceKind.SourceRoslynVirtualCandidate or AnalysisEvidenceKind.AssemblyIlVirtualCandidate or AnalysisEvidenceKind.ReflectionHeuristic or AnalysisEvidenceKind.LanguageFrontend => 1,
        _ => 0
    };

    private static void AddConfidenceReason(PackageReachability reachability, string reason)
    {
        if (!reachability.ConfidenceReasons.Contains(reason, StringComparer.Ordinal)) reachability.ConfidenceReasons.Add(reason);
    }

    /// <summary>
    ///     F6: the attack-surface view — entry points grouped by exposure, each with the exploit
    ///     chains and weakness candidates that reach it (R2 linkage). Presentation over existing
    ///     facts: one linear pass over entry points, chains, and graph-linked weaknesses. Groups
    ///     order most-exposed first; entries order most-findings first.
    /// </summary>
    public static List<AttackSurfaceGroup> BuildAttackSurface(DataFlowResult result)
    {
        var chainsByEntryPointId = result.ExploitChains
            .GroupBy(chain => chain.EntryPointId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var weaknessesByEntryPointId = result.WeaknessCandidates
            .Where(weakness => !string.IsNullOrWhiteSpace(weakness.EntryPointId))
            .GroupBy(weakness => weakness.EntryPointId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

        var groups = new List<AttackSurfaceGroup>();
        foreach (var exposureGroup in result.EntryPoints.GroupBy(ExposureFor, StringComparer.Ordinal))
        {
            var entries = new List<AttackSurfaceEntry>();
            foreach (var entryPoint in exposureGroup)
            {
                var weaknesses = weaknessesByEntryPointId.GetValueOrDefault(entryPoint.Id) ?? [];
                entries.Add(new AttackSurfaceEntry
                {
                    EntryPointId = entryPoint.Id,
                    Exposure = exposureGroup.Key,
                    Kind = entryPoint.Kind,
                    HttpMethod = entryPoint.HttpMethod,
                    Route = entryPoint.Route,
                    FileName = entryPoint.FileName,
                    LineNumber = entryPoint.LineNumber,
                    AllowAnonymous = entryPoint.AllowAnonymous,
                    ExploitChainCount = chainsByEntryPointId.GetValueOrDefault(entryPoint.Id),
                    WeaknessCount = weaknesses.Count,
                    HighSeverityWeaknessCount = weaknesses.Count(weakness => SeverityRank(weakness.Severity) >= SeverityRank("high")),
                    WeaknessKinds = weaknesses.Select(weakness => weakness.Kind).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(MaxAttackSurfaceListEntries).ToList(),
                    Cwes = weaknesses.Select(weakness => weakness.Cwe).Where(cwe => cwe is not null).Select(cwe => cwe!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(MaxAttackSurfaceListEntries).ToList(),
                    SinkCategories = weaknesses.Select(weakness => weakness.SinkCategory).Where(category => category is not null).Select(category => category!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(MaxAttackSurfaceListEntries).ToList()
                });
            }

            groups.Add(new AttackSurfaceGroup
            {
                Exposure = exposureGroup.Key,
                EntryPointCount = entries.Count,
                ExploitChainCount = entries.Sum(entry => entry.ExploitChainCount),
                WeaknessCount = entries.Sum(entry => entry.WeaknessCount),
                HighSeverityWeaknessCount = entries.Sum(entry => entry.HighSeverityWeaknessCount),
                EntryPointsTruncated = entries.Count > MaxAttackSurfaceEntryPointsPerGroup,
                EntryPoints = entries
                    .OrderByDescending(entry => entry.HighSeverityWeaknessCount)
                    .ThenByDescending(entry => entry.WeaknessCount)
                    // Descending like the counts above it: entries are truncated at
                    // MaxAttackSurfaceEntryPointsPerGroup, so sorting chains ascending pushed the
                    // entry points that actually have chains out of the report first.
                    .ThenByDescending(entry => entry.ExploitChainCount)
                    .ThenBy(entry => entry.EntryPointId, StringComparer.Ordinal)
                    .Take(MaxAttackSurfaceEntryPointsPerGroup)
                    .ToList()
            });
        }

        return groups.OrderBy(group => ExposureRank(group.Exposure)).ThenBy(group => group.Exposure, StringComparer.Ordinal).ToList();
    }

    private const int MaxAttackSurfaceListEntries = 8;
    private const int MaxAttackSurfaceEntryPointsPerGroup = 50;

    /// <summary>
    ///     Anonymous surfaces first, authenticated/internal last — the triage order (F6). Every
    ///     value <see cref="ExposureFor"/> can return is ranked explicitly; the bare "anonymous"
    ///     bucket (an unauthenticated entry point of an unclassified kind) must not fall through to
    ///     the default and sort below the authenticated groups.
    /// </summary>
    private static int ExposureRank(string exposure) => exposure switch
    {
        "anonymous-http" => 0,
        "anonymous-rpc" => 1,
        "anonymous" => 2,
        "mcp" => 3,
        "queue" => 4,
        "cli" => 5,
        "authenticated-http" => 6,
        "authenticated-rpc" => 7,
        "internal" => 8,
        _ => 9
    };

    public static List<WeaknessCandidate> BuildWeaknessCandidates(DataFlowResult result, IEnumerable<EntryPoint>? entryPoints = null)
    {
        var nodes = result.Nodes.ToDictionaryFirstWins(n => n.Id, StringComparer.Ordinal);
        var entryPointList = entryPoints?.ToList() ?? [];
        var candidates = new List<WeaknessCandidate>();
        var index = 0;
        foreach (var slice in result.Slices)
        {
            nodes.TryGetValue(slice.SourceId, out var source);
            nodes.TryGetValue(slice.SinkId, out var sink);
            var kind = WeaknessKind(slice.SinkCategory);
            var cwe = WeaknessCwe(kind);
            var matchingEntryPoint = FindEntryPoint(entryPointList, source);
            var confidenceReasons = new List<string>();
            if (source?.IsSource == true) confidenceReasons.Add($"Source matched category '{source.Category}'.");
            if (sink?.IsSink == true) confidenceReasons.Add($"Sink matched category '{sink.Category}'.");
            if (matchingEntryPoint is not null) confidenceReasons.Add($"Flow is near entrypoint '{matchingEntryPoint.Id}'.");
            if (slice.Purls.Count > 0) confidenceReasons.Add("Slice contains package URL metadata.");
            var confidence = matchingEntryPoint is not null && sink?.IsSink == true ? "High" : sink?.IsSink == true ? "Medium" : "Low";
            candidates.Add(new WeaknessCandidate
            {
                Id = $"wc{++index}",
                Kind = kind,
                Cwe = cwe,
                Confidence = confidence,
                ConfidenceReasons = confidenceReasons,
                SliceId = slice.Id,
                SourceId = slice.SourceId,
                SinkId = slice.SinkId,
                SourceCategory = slice.SourceCategory,
                SinkCategory = slice.SinkCategory,
                SourceLocation = FormatLocation(source),
                SinkLocation = FormatLocation(sink),
                EntryPointId = matchingEntryPoint?.Id,
                Route = matchingEntryPoint?.Route,
                Purls = slice.Purls,
                Evidence = BuildEvidence(source, sink, slice),
                Summary = $"{slice.SourceCategory ?? "input"} data reaches {slice.SinkCategory ?? "sink"} sink {sink?.Name ?? slice.SinkId}."
            });
        }
        return candidates;
    }

    public static List<DangerousApiReachability> BuildDangerousApiReachability(DataFlowResult result)
    {
        return result.Nodes
            .Where(n => n.IsSink)
            .Select((node, index) => new DangerousApiReachability
            {
                Id = $"dar{index + 1}",
                Category = node.Category ?? "sink",
                Symbol = node.Symbol,
                Purl = node.Purl,
                NodeIds = [node.Id],
                SliceIds = result.Slices.Where(slice => slice.SinkId == node.Id).Select(slice => slice.Id).ToList(),
                Confidence = node.Symbol is null ? "Medium" : "High",
                Evidence = [FormatLocation(node) ?? node.Id, node.Code ?? node.Name]
            })
            .ToList();
    }

    public static AgentContext BuildAgentContext(DataFlowResult result, string inputPath)
    {
        var highRiskWeaknesses = result.WeaknessCandidates
            .Where(w => w.Confidence == "High" || w.SinkCategory is "command" or "sql" or "file" or "deserialization")
            .Take(25)
            .ToList();
        var highRiskSliceIds = highRiskWeaknesses.Select(w => w.SliceId).Where(id => id is not null).ToHashSet(StringComparer.Ordinal);
        return new AgentContext
        {
            Metadata = CreateMetadata(inputPath),
            Summary = $"Dosai found {result.Statistics.SliceCount} slices, {result.WeaknessCandidates.Count} weakness candidates, and {result.PackageReachability.Count} reachable package facts.",
            EntryPoints = result.EntryPoints.Take(50).ToList(),
            HighRiskWeaknesses = highRiskWeaknesses,
            HighRiskSlices = result.Slices.Where(s => highRiskSliceIds.Contains(s.Id)).Take(25).ToList(),
            ReachablePackages = result.PackageReachability.Take(50).ToList(),
            AttackSurface = BuildAttackSurface(result).Take(10).ToList(),
            RelevantFiles = result.Nodes.Select(n => n.Path ?? n.FileName).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.Ordinal).Take(100).ToList()!,
            SuggestedNextCommands =
            [
                $"dotnet run --project ./Dosai -- dataflows --path {inputPath} --o dataflows.json --print-sources-sinks",
                $"dotnet run --project ./Dosai -- methods --path {inputPath} --o methods.json --callgraph-format graphml --callgraph-out callgraph.graphml"
            ]
        };
    }

    private static EntryPoint? FindEntryPoint(IEnumerable<EntryPoint> entryPoints, DataFlowNode? source)
    {
        if (source is null) return null;
        return entryPoints.FirstOrDefault(ep => string.Equals(ep.FileName, source.FileName, StringComparison.OrdinalIgnoreCase) && (ep.MethodName == source.MethodName || ep.LineNumber <= source.LineNumber));
    }

    /// <summary>
    ///     R2: resolve weakness↔entry-point linkage by graph instead of the same-file line heuristic.
    ///     For each slice, walk the reverse method-call graph (recorded during the operation walk)
    ///     from the taint source's method to the nearest entry point (≤ <paramref name="MaxHops"/>),
    ///     and emit a concrete route → call path → taint slice → sink chain. Also populates the
    ///     previously dead <see cref="DangerousApiReachability.EntryPointIds"/> and upgrades the
    ///     linked weakness candidates' confidence reasons with the actual path.
    /// </summary>
    public static void AttachExploitChains(DataFlowResult result, IReadOnlyDictionary<string, HashSet<string>> methodEdges, IReadOnlyDictionary<string, string> methodIdsByFileMethod)
    {
        if (result.Slices.Count == 0 || result.EntryPoints.Count == 0)
        {
            return;
        }

        var reverseEdges = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (caller, callees) in methodEdges)
        {
            foreach (var callee in callees)
            {
                if (!reverseEdges.TryGetValue(callee, out var callers))
                {
                    callers = [];
                    reverseEdges[callee] = callers;
                }

                if (!callers.Contains(caller))
                {
                    callers.Add(caller);
                }
            }
        }

        var entryPointsByMethodId = new Dictionary<string, EntryPoint>(StringComparer.Ordinal);
        foreach (var entryPoint in result.EntryPoints)
        {
            var methodId = entryPoint.MethodId;
            if (string.IsNullOrWhiteSpace(methodId) && !string.IsNullOrWhiteSpace(entryPoint.FileName) && !string.IsNullOrWhiteSpace(entryPoint.MethodName) && methodIdsByFileMethod.TryGetValue($"{Path.GetFileName(entryPoint.FileName)}|{entryPoint.MethodName}", out var resolved))
            {
                methodId = resolved;
            }

            if (!string.IsNullOrWhiteSpace(methodId))
            {
                entryPointsByMethodId.TryAdd(methodId, entryPoint);
            }
        }

        if (entryPointsByMethodId.Count == 0)
        {
            return;
        }

        var nodesById = result.Nodes.ToDictionaryFirstWins(node => node.Id, StringComparer.Ordinal);
        var weaknessesBySliceId = result.WeaknessCandidates
            .Where(weakness => !string.IsNullOrWhiteSpace(weakness.SliceId))
            .GroupBy(weakness => weakness.SliceId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var entryPointIdsBySinkId = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var chains = new List<ExploitChain>();
        var chainCounter = 0;

        foreach (var slice in result.Slices)
        {
            // R2: resolve the chain from the sink's method (falling back to the source's), walking
            // the reverse graph to the nearest entry point; the call path then spans the whole
            // route the attacker actually takes: entry point → … → sink frame.
            nodesById.TryGetValue(slice.SinkId, out var sinkNode);
            nodesById.TryGetValue(slice.SourceId, out var sourceNode);
            var startMethodId =
                sinkNode?.Properties.GetValueOrDefault("sinkMethodId") as string ??
                sinkNode?.Properties.GetValueOrDefault("methodId") as string ??
                sourceNode?.Properties.GetValueOrDefault("methodId") as string;
            if (string.IsNullOrWhiteSpace(startMethodId) ||
                !TryFindReversePath(reverseEdges, entryPointsByMethodId, startMethodId, out var entryPoint, out var callPath))
            {
                continue;
            }

            // callPath is sink → entry point; store entry → sink for readability.
            callPath.Reverse();
            var exposure = ExposureFor(entryPoint);
            var chain = new ExploitChain
            {
                Id = $"ec{++chainCounter}",
                EntryPointId = entryPoint.Id,
                Exposure = exposure,
                WeaknessId = weaknessesBySliceId.GetValueOrDefault(slice.Id)?.Id,
                SliceId = slice.Id,
                SourceNodeId = slice.SourceId,
                SinkNodeId = slice.SinkId,
                CallPath = callPath,
                HopCount = callPath.Count - 1,
                Confidence = "High",
                Summary = $"{slice.SourceCategory ?? "input"} data reaches {slice.SinkCategory ?? "sink"} sink from {exposure} entry point {entryPoint.Id} in {callPath.Count - 1} call(s)."
            };
            chains.Add(chain);

            if (weaknessesBySliceId.TryGetValue(slice.Id, out var weakness))
            {
                weakness.EntryPointId = entryPoint.Id;
                weakness.Route = entryPoint.Route ?? weakness.Route;
                weakness.Confidence = "High";
                weakness.ConfidenceReasons.Add($"Sink reachable from {exposure} entry point {DescribeEntryPoint(entryPoint)} in {callPath.Count - 1} call(s).");
                weakness.ConfidenceReasons = weakness.ConfidenceReasons.Distinct(StringComparer.Ordinal).ToList();
            }

            if (!entryPointIdsBySinkId.TryGetValue(slice.SinkId, out var entryPointIds))
            {
                entryPointIds = [];
                entryPointIdsBySinkId[slice.SinkId] = entryPointIds;
            }

            if (!entryPointIds.Contains(entryPoint.Id))
            {
                entryPointIds.Add(entryPoint.Id);
            }
        }

        result.ExploitChains = chains;

        // Populate the dead EntryPointIds field on the dangerous-API facts.
        foreach (var dangerousApi in result.DangerousApiReachability)
        {
            foreach (var nodeId in dangerousApi.NodeIds)
            {
                if (entryPointIdsBySinkId.TryGetValue(nodeId, out var entryPointIds))
                {
                    foreach (var entryPointId in entryPointIds.Where(entryPointId => !dangerousApi.EntryPointIds.Contains(entryPointId)))
                    {
                        dangerousApi.EntryPointIds.Add(entryPointId);
                    }
                }
            }
        }
    }

    private const int MaxExploitChainHops = 8;

    private static bool TryFindReversePath(Dictionary<string, List<string>> reverseEdges, Dictionary<string, EntryPoint> entryPointsByMethodId, string startMethodId, out EntryPoint entryPoint, out List<string> callPath)
    {
        // BFS over callers from the taint source's method toward any entry-point method. BFS gives
        // the shortest path; the visited set plus the hop bound keeps this near-linear in the
        // reachable subgraph.
        entryPoint = null!;
        callPath = [];
        if (entryPointsByMethodId.ContainsKey(startMethodId))
        {
            entryPoint = entryPointsByMethodId[startMethodId];
            callPath = [startMethodId];
            return true;
        }

        var parents = new Dictionary<string, string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal) { startMethodId };
        var frontier = new Queue<string>([startMethodId]);
        var depth = new Dictionary<string, int>(StringComparer.Ordinal) { [startMethodId] = 0 };
        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();
            if (depth[current] >= MaxExploitChainHops || !reverseEdges.TryGetValue(current, out var callers))
            {
                continue;
            }

            foreach (var caller in callers)
            {
                if (!visited.Add(caller))
                {
                    continue;
                }

                parents[caller] = current;
                depth[caller] = depth[current] + 1;
                if (entryPointsByMethodId.TryGetValue(caller, out var matched))
                {
                    entryPoint = matched;
                    for (var node = caller; ; node = parents[node])
                    {
                        callPath.Add(node);
                        if (node == startMethodId)
                        {
                            break;
                        }
                    }

                    return true;
                }

                frontier.Enqueue(caller);
            }
        }

        return false;
    }

    private static string DescribeEntryPoint(EntryPoint entryPoint)
    {
        var route = string.IsNullOrWhiteSpace(entryPoint.Route) ? string.Empty : $":{entryPoint.Route}";
        var verb = string.IsNullOrWhiteSpace(entryPoint.HttpMethod) ? string.Empty : $"{entryPoint.HttpMethod} ";
        return $"{entryPoint.Id}({verb}{route})";
    }

    /// <summary>
    ///     Exposure classification derived from the entry-point kind and its auth posture (R2).
    ///     HTTP endpoints are anonymous unless authorization is explicitly required — ASP.NET Core
    ///     serves unannotated endpoints without authentication by default.
    /// </summary>
    public static string ExposureFor(EntryPoint entryPoint)
    {
        var authenticated = entryPoint.AuthorizationRequired == true || entryPoint.Roles.Count > 0 || entryPoint.AuthorizationPolicies.Count > 0 || entryPoint.AuthenticationSchemes.Count > 0;
        return entryPoint.Kind switch
        {
            "HttpController" or "HttpMinimalApi" or "HttpRazorPage" => authenticated ? "authenticated-http" : "anonymous-http",
            "Grpc" or "Soap" or "SignalRHub" or "GraphQL" or "OData" or "GrainMethod" => authenticated ? "authenticated-rpc" : "anonymous-rpc",
            "AzureFunction" or "LambdaFunction" or "MessageConsumer" => "queue",
            "McpTool" or "McpPrompt" or "McpResource" => "mcp",
            "Cli" => "cli",
            _ => authenticated ? "internal" : "anonymous"
        };
    }

    /// <summary>
    ///     T5: apply a suppressions file (file+line, slice key, weakness id, or category — an entry
    ///     matches only when all its present fields match, with an optional expiry). Matching,
    ///     non-expired suppressions remove slices and their weakness candidates; expired
    ///     suppressions re-surface the flow. Weakness-only suppressions (a weaknessId, or a
    ///     category with no matching slice — e.g. statically-derived crypto findings) apply even
    ///     when no slice matched. Best-effort: an unreadable or malformed file becomes a
    ///     diagnostic, never an analysis failure.
    /// </summary>
    public static void ApplySuppressions(DataFlowResult result, string? suppressionsPath)
    {
        if (string.IsNullOrWhiteSpace(suppressionsPath))
        {
            return;
        }

        SuppressionSet suppressions;
        try
        {
            suppressions = SuppressionSet.Load(suppressionsPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            result.Diagnostics.Add($"Could not read suppressions file {suppressionsPath}: {ex.Message}");
            return;
        }

        // file/line and weaknessId matchers resolve against the weakness that owns the slice, so
        // the map must be keyed by SliceId (weakness ids are unrelated to slice ids).
        var weaknessesBySliceId = result.WeaknessCandidates
            .Where(weakness => !string.IsNullOrWhiteSpace(weakness.SliceId))
            .GroupBy(weakness => weakness.SliceId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var suppressedSliceIds = result.Slices
            .Where(slice => suppressions.IsSuppressed(slice, weaknessesBySliceId.GetValueOrDefault(slice.Id)))
            .Select(slice => slice.Id)
            .ToHashSet(StringComparer.Ordinal);
        var suppressedWeaknessIds = result.WeaknessCandidates
            .Where(weakness => (weakness.SliceId is not null && suppressedSliceIds.Contains(weakness.SliceId)) || suppressions.MatchesWeakness(weakness))
            .Select(weakness => weakness.Id)
            .ToHashSet(StringComparer.Ordinal);
        if (suppressedSliceIds.Count == 0 && suppressedWeaknessIds.Count == 0)
        {
            return;
        }

        result.Slices = result.Slices.Where(slice => !suppressedSliceIds.Contains(slice.Id)).ToList();
        result.WeaknessCandidates = result.WeaknessCandidates.Where(weakness => !suppressedWeaknessIds.Contains(weakness.Id)).ToList();
        result.ExploitChains = result.ExploitChains.Where(chain => chain.SliceId is null || !suppressedSliceIds.Contains(chain.SliceId)).ToList();
        result.Statistics.SliceCount = result.Slices.Count;
        result.Diagnostics.Add($"Suppressed {suppressedSliceIds.Count} data-flow slice(s) and {suppressedWeaknessIds.Count} weakness candidate(s) via {suppressionsPath}.");
    }

    private static List<string> BuildEvidence(DataFlowNode? source, DataFlowNode? sink, DataFlowSlice slice)
    {
        var evidence = new List<string>();
        if (source is not null) evidence.Add($"Source {source.Name} at {FormatLocation(source)}: {source.Code}");
        if (sink is not null) evidence.Add($"Sink {sink.Name} at {FormatLocation(sink)}: {sink.Code}");
        evidence.Add($"Slice path has {slice.NodeIds.Count} nodes and {slice.EdgeIds.Count} edges.");
        return evidence;
    }

    private static string? FormatLocation(DataFlowNode? node) => node is null ? null : $"{node.FileName}:{node.LineNumber}:{node.ColumnNumber}";

    private static string WeaknessKind(string? category) => category switch
    {
        "command" => "CommandInjectionCandidate",
        "file" => "PathTraversalOrFileAccessCandidate",
        "sql" => "SqlInjectionCandidate",
        "network" => "SsrfCandidate",
        "redirect" => "OpenRedirectCandidate",
        "deserialization" => "UnsafeDeserializationCandidate",
        "reflection" => "UnsafeReflectionCandidate",
        "rpc" => "RpcDispatchReachabilityCandidate",
        "prompt" => "PromptInjectionCandidate",
        "mcp" => "McpToolInjectionCandidate",
        "mcp-egress" => "McpEgressCandidate",
        // W6: the crypto-family sink categories used to collapse into DangerousDataFlowCandidate
        // with no CWE even though the crypto pack ships them by default.
        "crypto" => "InsecureCryptoUsageCandidate",
        "jwt" => "JwtValidationCandidate",
        "certificate" => "CertificateValidationCandidate",
        "tls" => "TlsValidationCandidate",
        // W1–W5/W8 weakness classes.
        "xss" => "XssCandidate",
        "xxe" => "XxeCandidate",
        "ldap" => "LdapInjectionCandidate",
        "xpath" => "XPathInjectionCandidate",
        "nosql" => "NoSqlInjectionCandidate",
        "log" => "LogInjectionCandidate",
        "header" => "HeaderInjectionCandidate",
        "redos" => "ReDoSCandidate",
        "template" => "TemplateInjectionCandidate",
        _ => "DangerousDataFlowCandidate"
    };

    private static string? WeaknessCwe(string kind) => kind switch
    {
        "CommandInjectionCandidate" => "CWE-78",
        "PathTraversalOrFileAccessCandidate" => "CWE-22",
        "SqlInjectionCandidate" => "CWE-89",
        "SsrfCandidate" => "CWE-918",
        "OpenRedirectCandidate" => "CWE-601",
        "UnsafeDeserializationCandidate" => "CWE-502",
        "UnsafeReflectionCandidate" => "CWE-470",
        "PromptInjectionCandidate" => "CWE-1427",
        "McpToolInjectionCandidate" => "CWE-1427",
        "McpEgressCandidate" => "CWE-1427",
        "InsecureCryptoUsageCandidate" => "CWE-327",
        "JwtValidationCandidate" => "CWE-345",
        "CertificateValidationCandidate" => "CWE-295",
        "TlsValidationCandidate" => "CWE-295",
        "XssCandidate" => "CWE-79",
        "XxeCandidate" => "CWE-611",
        "LdapInjectionCandidate" => "CWE-90",
        "XPathInjectionCandidate" => "CWE-643",
        "NoSqlInjectionCandidate" => "CWE-943",
        "LogInjectionCandidate" => "CWE-117",
        "HeaderInjectionCandidate" => "CWE-113",
        "ReDoSCandidate" => "CWE-1333",
        "TemplateInjectionCandidate" => "CWE-1336",
        _ => null
    };

    /// <summary>
    ///     Default severity per sink category (T5). Slices/weaknesses take the pattern's explicit
    ///     Severity when set, otherwise this table, otherwise "medium". Injection primitives that
    ///     yield code execution are high; noisy-but-low-impact classes stay low so CI gating on
    ///     "new high-severity slices" keeps a useful signal.
    /// </summary>
    public static string SeverityForCategory(string? category) => category switch
    {
        "command" or "sql" or "deserialization" or "file" or "xss" or "xxe" or "ldap" or "xpath" or "nosql" or "template" or "crypto" or "jwt" or "certificate" or "tls" or "reflection" or "network" or "auth" => "high",
        "redirect" or "header" or "prompt" or "mcp" or "mcp-egress" or "rpc" or "eval" or "memory" => "medium",
        "log" or "redos" or "string" or "cli" => "low",
        _ => "medium"
    };

    /// <summary>Assigns severity to every slice and weakness candidate that does not carry a pattern override.</summary>
    public static void ApplySeverity(DataFlowResult result)
    {
        var severityBySliceId = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var slice in result.Slices)
        {
            slice.Severity = string.IsNullOrWhiteSpace(slice.Severity) ? SeverityForCategory(slice.SinkCategory) : slice.Severity;
            severityBySliceId[slice.Id] = slice.Severity;
        }

        foreach (var weakness in result.WeaknessCandidates)
        {
            weakness.Severity = weakness.SliceId is not null && severityBySliceId.TryGetValue(weakness.SliceId, out var severity)
                ? severity
                : SeverityForCategory(weakness.SinkCategory);
        }
    }

    /// <summary>
    ///     T5: resolves a slice's severity, demoting one rank when the matched pattern is
    ///     Low-confidence. The CI gate story is "new high-severity slices" — a Low-confidence
    ///     heuristic match (e.g. a Code-fallback sink) must not trip it on its own.
    /// </summary>
    public static string SeverityForPattern(string? category, string? patternSeverity, string? patternConfidence)
    {
        var severity = (string.IsNullOrWhiteSpace(patternSeverity) ? SeverityForCategory(category) : patternSeverity).ToLowerInvariant();
        if (!string.Equals(patternConfidence, "Low", StringComparison.OrdinalIgnoreCase))
        {
            return severity;
        }

        return severity switch
        {
            "high" => "medium",
            "medium" => "low",
            "low" => "info",
            _ => severity
        };
    }

    public static string ToMarkdownReport(DataFlowResult result)
    {
        var lines = new List<string>
        {
            "# Dosai Analysis Report",
            string.Empty,
            $"- Files analyzed: {result.Statistics.FilesAnalyzed}",
            $"- Sources: {result.Statistics.SourceCount}",
            $"- Sinks: {result.Statistics.SinkCount}",
            $"- Slices: {result.Statistics.SliceCount}",
            $"- Weakness candidates: {result.WeaknessCandidates.Count}",
            string.Empty,
            "## High-priority weakness candidates",
            string.Empty
        };
        foreach (var weakness in result.WeaknessCandidates.Take(50))
        {
            lines.Add($"### {weakness.Id}: {weakness.Kind}");
            lines.Add(string.Empty);
            lines.Add($"- Confidence: {weakness.Confidence}");
            lines.Add($"- Severity: {weakness.Severity}");
            if (!string.IsNullOrWhiteSpace(weakness.Cwe)) lines.Add($"- CWE: {weakness.Cwe}");
            if (!string.IsNullOrWhiteSpace(weakness.Route)) lines.Add($"- Route: {weakness.Route}");
            lines.Add($"- Source: {weakness.SourceLocation}");
            lines.Add($"- Sink: {weakness.SinkLocation}");
            if (weakness.Purls.Count > 0) lines.Add($"- PURLs: {string.Join(", ", weakness.Purls)}");
            lines.Add(string.Empty);
            lines.Add("Evidence:");
            foreach (var evidence in weakness.Evidence) lines.Add($"- `{evidence}`");
            lines.Add(string.Empty);
        }

        if (result.SanitizedFlows.Count > 0)
        {
            lines.Add("## Sanitized flows (negative evidence)");
            lines.Add(string.Empty);
            foreach (var sanitized in result.SanitizedFlows.Take(25))
            {
                lines.Add($"- {sanitized.Id} [{sanitized.Kind}] {sanitized.FileName}:{sanitized.LineNumber} — `{sanitized.Expression}` via {sanitized.SanitizerSymbol ?? sanitized.SanitizerCategory}");
            }

            lines.Add(string.Empty);
        }

        if (result.ExploitChains.Count > 0)
        {
            lines.Add("## Exploit chains (entry point → call path → sink)");
            lines.Add(string.Empty);
            foreach (var chain in result.ExploitChains.Take(25))
            {
                lines.Add($"- {chain.Id}: {chain.Summary}");
            }

            lines.Add(string.Empty);
        }

        if (result.AttackSurface.Count > 0)
        {
            // F6: one table an analyst can triage top-down — groups order most-exposed first and
            // each group's rows order most-findings first.
            lines.Add("## Attack surface (grouped by exposure)");
            lines.Add(string.Empty);
            foreach (var group in result.AttackSurface)
            {
                lines.Add($"### {group.Exposure} — {group.EntryPointCount} entry point(s), {group.ExploitChainCount} chain(s), {group.WeaknessCount} weakness(ies) ({group.HighSeverityWeaknessCount} high-severity)");
                lines.Add(string.Empty);
                foreach (var entry in group.EntryPoints.Take(10))
                {
                    var target = string.IsNullOrWhiteSpace(entry.Route) ? entry.FileName ?? entry.EntryPointId : entry.Route;
                    var verb = string.IsNullOrWhiteSpace(entry.HttpMethod) ? string.Empty : $"{entry.HttpMethod} ";
                    var findings = entry.WeaknessCount == 0
                        ? "no linked findings"
                        : $"{entry.WeaknessCount} weakness(es), {entry.HighSeverityWeaknessCount} high, {entry.ExploitChainCount} chain(s), CWE {string.Join("/", entry.Cwes)}";
                    lines.Add($"- {entry.EntryPointId} `{verb}{target}` ({entry.FileName}:{entry.LineNumber}) — {findings}");
                }

                if (group.EntryPointCount > Math.Min(group.EntryPoints.Count, 10))
                {
                    lines.Add($"- _…{group.EntryPointCount - Math.Min(group.EntryPoints.Count, 10)} more entry point(s) not shown; the counts above cover the whole group._");
                }

                lines.Add(string.Empty);
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    ///     O1: structural diff beyond slices. Keyed comparisons for slices (severity-aware), entry
    ///     points (kind+verb+route), weakness candidates (kind+CWE+sink location), and package purls,
    ///     plus a single CI-decidable RiskDelta summary.
    /// </summary>
    public static string DiffJson(DataFlowResult oldResult, DataFlowResult newResult)
    {
        var oldSeverityByKey = oldResult.Slices.GroupBy(SliceKey, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First().Severity, StringComparer.Ordinal);
        var newSeverityByKey = newResult.Slices.GroupBy(SliceKey, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First().Severity, StringComparer.Ordinal);
        var oldSlices = oldSeverityByKey.Keys.ToHashSet(StringComparer.Ordinal);
        var newSlices = newSeverityByKey.Keys.ToHashSet(StringComparer.Ordinal);
        var addedSlices = newSlices.Except(oldSlices).Order(StringComparer.Ordinal).ToList();
        var removedSlices = oldSlices.Except(newSlices).Order(StringComparer.Ordinal).ToList();

        var oldEntryPoints = oldResult.EntryPoints.Select(EntryPointKey).ToHashSet(StringComparer.Ordinal);
        var newEntryPoints = newResult.EntryPoints.Select(EntryPointKey).ToHashSet(StringComparer.Ordinal);
        var addedEntryPoints = newEntryPoints.Except(oldEntryPoints).Order(StringComparer.Ordinal).ToList();
        var removedEntryPoints = oldEntryPoints.Except(newEntryPoints).Order(StringComparer.Ordinal).ToList();

        var oldWeaknesses = oldResult.WeaknessCandidates.Select(WeaknessKey).ToHashSet(StringComparer.Ordinal);
        var newWeaknesses = newResult.WeaknessCandidates.Select(WeaknessKey).ToHashSet(StringComparer.Ordinal);
        var addedWeaknesses = newWeaknesses.Except(oldWeaknesses).Order(StringComparer.Ordinal).ToList();
        var removedWeaknesses = oldWeaknesses.Except(newWeaknesses).Order(StringComparer.Ordinal).ToList();

        var oldPackages = oldResult.PackageReachability.Select(reachability => reachability.Purl).ToHashSet(StringComparer.Ordinal);
        var newPackages = newResult.PackageReachability.Select(reachability => reachability.Purl).ToHashSet(StringComparer.Ordinal);
        var addedPackages = newPackages.Except(oldPackages).Order(StringComparer.Ordinal).ToList();
        var removedPackages = oldPackages.Except(newPackages).Order(StringComparer.Ordinal).ToList();

        var addedAnonymousEndpoints = newResult.EntryPoints
            .Where(entryPoint => newEntryPoints.Except(oldEntryPoints).Contains(EntryPointKey(entryPoint)))
            .Count(entryPoint => entryPoint.AllowAnonymous || entryPoint.AuthorizationRequired == false);
        var riskDelta = new
        {
            NewHighSeveritySlices = addedSlices.Count(key => SeverityRank(newSeverityByKey.GetValueOrDefault(key)) >= SeverityRank("high")),
            NewMediumSeveritySlices = addedSlices.Count(key => newSeverityByKey.GetValueOrDefault(key) == "medium"),
            NewLowSeveritySlices = addedSlices.Count(key => SeverityRank(newSeverityByKey.GetValueOrDefault(key)) <= SeverityRank("low")),
            NewAnonymousEndpoints = addedAnonymousEndpoints,
            NewWeaknessKinds = addedWeaknesses.Count,
            NewlyReachablePackages = addedPackages.Count
        };

        var diff = new
        {
            AddedSlices = addedSlices,
            RemovedSlices = removedSlices,
            AddedEntryPoints = addedEntryPoints,
            RemovedEntryPoints = removedEntryPoints,
            AddedWeaknesses = addedWeaknesses,
            RemovedWeaknesses = removedWeaknesses,
            AddedPackages = addedPackages,
            RemovedPackages = removedPackages,
            RiskDelta = riskDelta,
            OldStatistics = oldResult.Statistics,
            NewStatistics = newResult.Statistics
        };
        return JsonSerializer.Serialize(diff, new JsonSerializerOptions { WriteIndented = true });
    }

    private static int SeverityRank(string? severity) => severity?.ToLowerInvariant() switch
    {
        "info" => 0,
        "low" => 1,
        "medium" => 2,
        "high" => 3,
        "critical" => 4,
        _ => 2
    };

    public static string SliceKey(DataFlowSlice slice) => $"{slice.SourceCategory}->{slice.SinkCategory}:{slice.SinkArgument}";

    private static string EntryPointKey(EntryPoint entryPoint)
    {
        var target = string.Join('.', new[] { entryPoint.Namespace, entryPoint.ClassName, entryPoint.MethodName }.Where(part => !string.IsNullOrWhiteSpace(part)));
        return $"{entryPoint.Kind}:{entryPoint.HttpMethod ?? string.Empty}:{entryPoint.Route ?? string.Empty}:{target}";
    }

    private static string WeaknessKey(WeaknessCandidate weakness) =>
        $"{weakness.Kind}:{weakness.Cwe}:{weakness.SourceLocation}->{weakness.SinkLocation}:{weakness.Severity}";
}
