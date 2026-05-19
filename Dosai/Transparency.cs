using System.Text.Json;
using System.Text.Json.Serialization;

namespace Depscan;

public sealed class AnalysisMetadata
{
    public string SchemaVersion { get; set; } = "3.2.0";
    public string AnalyzerVersion { get; set; } = typeof(Dosai).Assembly.GetName().Version?.ToString() ?? "3.2.0";
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

public sealed class PackageReachability
{
    public required string Purl { get; set; }
    public bool Reachable { get; set; }
    public string ReachabilityKind { get; set; } = "Unknown";
    public List<string> NodeIds { get; set; } = [];
    public List<string> EdgeIds { get; set; } = [];
    public List<string> SliceIds { get; set; } = [];
    public List<string> EntryPointIds { get; set; } = [];
    public List<string> Categories { get; set; } = [];
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

    public static List<PackageReachability> BuildPackageReachability(CallGraph callGraph, IEnumerable<DataFlowSlice>? slices = null)
    {
        var byPurl = new Dictionary<string, PackageReachability>(StringComparer.Ordinal);

        foreach (var node in callGraph.Nodes)
        {
            Add(node.Purl, node.IsExternal ? "ExternalCallGraphNode" : "InternalCallGraphNode", node.Id);
        }
        foreach (var edge in callGraph.Edges)
        {
            Add(edge.SourcePurl, "CallGraphEdge", edge.SourceId, edge.Id);
            Add(edge.TargetPurl, "CallGraphEdge", edge.TargetId, edge.Id, category: edge.CallType.ToString());
        }
        if (slices is not null)
        {
            foreach (var slice in slices)
            {
                foreach (var purl in slice.Purls)
                {
                    Add(purl, "DataFlowSlice", sliceId: slice.Id, category: slice.SinkCategory);
                }
            }
        }
        return byPurl.Values.OrderBy(p => p.Purl, StringComparer.Ordinal).ToList();

        void Add(string? purl, string kind, string? nodeId = null, string? edgeId = null, string? sliceId = null, string? category = null)
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
        }
    }

    public static List<PackageReachability> BuildPackageReachability(DataFlowResult result)
    {
        var byPurl = new Dictionary<string, PackageReachability>(StringComparer.Ordinal);
        foreach (var node in result.Nodes) Add(node.Purl, "DataFlowNode", node.Id, category: node.Category);
        foreach (var edge in result.Edges)
        {
            Add(edge.SourcePurl, "DataFlowEdge", edge.SourceId, edge.Id);
            Add(edge.TargetPurl, "DataFlowEdge", edge.TargetId, edge.Id);
        }
        foreach (var slice in result.Slices)
        {
            foreach (var purl in slice.Purls) Add(purl, "DataFlowSlice", sliceId: slice.Id, category: slice.SinkCategory);
        }
        return byPurl.Values.OrderBy(p => p.Purl, StringComparer.Ordinal).ToList();

        void Add(string? purl, string kind, string? nodeId = null, string? edgeId = null, string? sliceId = null, string? category = null)
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
        }
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
