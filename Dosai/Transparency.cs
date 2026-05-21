using System.Text.Json;
using System.Text.Json.Serialization;

namespace Depscan;

public sealed class AnalysisMetadata
{
    public string SchemaVersion { get; set; } = "3.2.0";
    public string AnalyzerVersion { get; set; } = typeof(Dosai).Assembly.GetName().Version?.ToString() ?? "3.0.4";
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
    public List<string> Urls { get; set; } = [];
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
    public List<string> RelevantFiles { get; set; } = [];
    public List<string> SuggestedNextCommands { get; set; } = [];
}

public static class TransparencyBuilder
{
    public static AnalysisMetadata CreateMetadata(string inputPath) => new() { InputPath = inputPath };

    public static List<EntryPoint> BuildEntryPoints(IEnumerable<ApiEndpoint> apiEndpoints, IEnumerable<Method>? methods = null)
    {
        var entries = new List<EntryPoint>();
        var index = 0;
        foreach (var endpoint in apiEndpoints)
        {
            entries.Add(new EntryPoint
            {
                Id = $"ep{++index}",
                Kind = endpoint.EndpointKind == "MinimalApi" ? "HttpMinimalApi" : "HttpEndpoint",
                MethodName = endpoint.MethodName,
                ClassName = endpoint.ClassName,
                Namespace = endpoint.Namespace,
                FileName = endpoint.FileName,
                Path = endpoint.Path,
                LineNumber = endpoint.LineNumber,
                ColumnNumber = endpoint.ColumnNumber,
                HttpMethod = endpoint.HttpMethod,
                Route = endpoint.Route,
                Urls = endpoint.Urls,
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
            foreach (var method in methods.Where(m => m.Name == "Main"))
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

    public static List<WeaknessCandidate> BuildWeaknessCandidates(DataFlowResult result, IEnumerable<EntryPoint>? entryPoints = null)
    {
        var nodes = result.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
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
        _ => null
    };

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
        return string.Join(Environment.NewLine, lines);
    }

    public static string DiffJson(DataFlowResult oldResult, DataFlowResult newResult)
    {
        var oldSlices = oldResult.Slices.Select(SliceKey).ToHashSet(StringComparer.Ordinal);
        var newSlices = newResult.Slices.Select(SliceKey).ToHashSet(StringComparer.Ordinal);
        var diff = new
        {
            AddedSlices = newSlices.Except(oldSlices).Order(StringComparer.Ordinal).ToList(),
            RemovedSlices = oldSlices.Except(newSlices).Order(StringComparer.Ordinal).ToList(),
            OldStatistics = oldResult.Statistics,
            NewStatistics = newResult.Statistics
        };
        return JsonSerializer.Serialize(diff, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string SliceKey(DataFlowSlice slice) => $"{slice.SourceCategory}->{slice.SinkCategory}:{slice.SinkArgument}";
}
