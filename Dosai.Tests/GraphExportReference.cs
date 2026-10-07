// Frozen byte-identity reference for the graph exporters, copied VERBATIM from main at
// 7292446 (the last commit before the issue-#75 TextWriter refactor). Do not edit: these
// StringBuilder implementations are the pre-refactor bytes the stream writers must reproduce
// exactly; test GraphExporters_ProduceTheFrozenMainBytes drives them over nasty labels and
// compares the files byte for byte.
//
// Generated from:
//   git show 7292446:Dosai/DataFlowExporter.cs
//   git show 7292446:Dosai/CallGraphExporter.cs
// with only the class renames (DataFlowExporter -> DataFlowExporterReference,
// CallGraphExporter -> CallGraphExporterReference), visibility dropped to internal, and the
// shared enum declarations removed.

using System.Globalization;
using System.Security;
using System.Text;

namespace Depscan.Tests;

internal static class DataFlowExporterReference
{
    public static string Export(DataFlowResult result, DataFlowExportFormat format) => format switch
    {
        DataFlowExportFormat.Mermaid => ToMermaid(result),
        DataFlowExportFormat.GraphMl => ToGraphMl(result),
        DataFlowExportFormat.Gexf => ToGexf(result),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported data-flow export format")
    };

    public static bool TryParseFormat(string? value, out DataFlowExportFormat format)
    {
        format = DataFlowExportFormat.GraphMl;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "mermaid" or "mmd" => Set(DataFlowExportFormat.Mermaid, out format),
            "graphml" or "graph-ml" => Set(DataFlowExportFormat.GraphMl, out format),
            "gexf" => Set(DataFlowExportFormat.Gexf, out format),
            _ => false
        };
    }

    public static string GetDefaultExtension(DataFlowExportFormat format) => format switch
    {
        DataFlowExportFormat.Mermaid => ".mmd",
        DataFlowExportFormat.GraphMl => ".graphml",
        DataFlowExportFormat.Gexf => ".gexf",
        _ => ".txt"
    };

    private static bool Set(DataFlowExportFormat value, out DataFlowExportFormat format)
    {
        format = value;
        return true;
    }

    private static string ToMermaid(DataFlowResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("flowchart LR");
        var ids = result.Nodes
            .OrderBy(n => n.Id, StringComparer.Ordinal)
            .Select((node, index) => new { node.Id, MermaidId = $"df{index + 1}" })
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().MermaidId, StringComparer.Ordinal);

        foreach (var node in result.Nodes.OrderBy(n => n.Id, StringComparer.Ordinal))
        {
            var shape = node.IsSource ? "([" : node.IsSink ? "[[" : "[";
            var endShape = node.IsSource ? "])" : node.IsSink ? "]]" : "]";
            builder.Append("    ").Append(ids[node.Id]).Append(shape).Append('"').Append(EscapeMermaid(node.Name)).Append('"').AppendLine(endShape);
        }

        foreach (var edge in result.Edges.OrderBy(e => e.Id, StringComparer.Ordinal))
        {
            if (!ids.TryGetValue(edge.SourceId, out var sourceId) || !ids.TryGetValue(edge.TargetId, out var targetId))
            {
                continue;
            }
            builder.Append("    ").Append(sourceId).Append(" -->|\"").Append(EscapeMermaid(edge.Kind)).Append("\"| ").AppendLine(targetId);
        }

        return builder.ToString();
    }

    private static string ToGraphMl(DataFlowResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        builder.AppendLine("<graphml xmlns=\"http://graphml.graphdrawing.org/xmlns\">");
        foreach (var key in new[] { "label", "kind", "symbol", "type", "purl", "file", "method", "line", "category", "source", "sink", "code" })
        {
            builder.Append("  <key id=\"").Append(Xml(key)).Append("\" for=\"node\" attr.name=\"").Append(Xml(key)).Append("\" attr.type=\"string\" />").AppendLine();
        }
        foreach (var key in new[] { "kind", "label", "sourcePurl", "targetPurl", "file", "line" })
        {
            builder.Append("  <key id=\"edge_").Append(Xml(key)).Append("\" for=\"edge\" attr.name=\"").Append(Xml(key)).Append("\" attr.type=\"string\" />").AppendLine();
        }
        builder.AppendLine("  <graph id=\"dataflows\" edgedefault=\"directed\">");

        foreach (var node in result.Nodes.OrderBy(n => n.Id, StringComparer.Ordinal))
        {
            builder.Append("    <node id=\"").Append(Xml(node.Id)).AppendLine("\">");
            AppendGraphMlData(builder, "label", node.Name, 6);
            AppendGraphMlData(builder, "kind", node.Kind, 6);
            AppendGraphMlData(builder, "symbol", node.Symbol, 6);
            AppendGraphMlData(builder, "type", node.Type, 6);
            AppendGraphMlData(builder, "purl", node.Purl, 6);
            AppendGraphMlData(builder, "file", node.FileName, 6);
            AppendGraphMlData(builder, "method", node.MethodName, 6);
            AppendGraphMlData(builder, "line", node.LineNumber.ToString(CultureInfo.InvariantCulture), 6);
            AppendGraphMlData(builder, "category", node.Category, 6);
            AppendGraphMlData(builder, "source", node.IsSource.ToString().ToLowerInvariant(), 6);
            AppendGraphMlData(builder, "sink", node.IsSink.ToString().ToLowerInvariant(), 6);
            AppendGraphMlData(builder, "code", node.Code, 6);
            builder.AppendLine("    </node>");
        }

        foreach (var edge in result.Edges.OrderBy(e => e.Id, StringComparer.Ordinal))
        {
            builder.Append("    <edge id=\"").Append(Xml(edge.Id)).Append("\" source=\"").Append(Xml(edge.SourceId)).Append("\" target=\"").Append(Xml(edge.TargetId)).AppendLine("\">");
            AppendGraphMlData(builder, "edge_kind", edge.Kind, 6);
            AppendGraphMlData(builder, "edge_label", edge.Label, 6);
            AppendGraphMlData(builder, "edge_sourcePurl", edge.SourcePurl, 6);
            AppendGraphMlData(builder, "edge_targetPurl", edge.TargetPurl, 6);
            AppendGraphMlData(builder, "edge_file", edge.FileName, 6);
            AppendGraphMlData(builder, "edge_line", edge.LineNumber.ToString(CultureInfo.InvariantCulture), 6);
            builder.AppendLine("    </edge>");
        }

        builder.AppendLine("  </graph>");
        builder.AppendLine("</graphml>");
        return builder.ToString();
    }

    private static string ToGexf(DataFlowResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        builder.AppendLine("<gexf xmlns=\"http://www.gexf.net/1.3\" version=\"1.3\">");
        builder.AppendLine("  <graph mode=\"static\" defaultedgetype=\"directed\">");
        builder.AppendLine("    <attributes class=\"node\">");
        foreach (var key in new[] { "kind", "symbol", "type", "purl", "file", "method", "line", "category", "source", "sink", "code" })
        {
            builder.Append("      <attribute id=\"").Append(Xml(key)).Append("\" title=\"").Append(Xml(key)).Append("\" type=\"string\" />").AppendLine();
        }
        builder.AppendLine("    </attributes>");
        builder.AppendLine("    <attributes class=\"edge\">");
        builder.AppendLine("      <attribute id=\"kind\" title=\"kind\" type=\"string\" />");
        builder.AppendLine("      <attribute id=\"label\" title=\"label\" type=\"string\" />");
        builder.AppendLine("      <attribute id=\"sourcePurl\" title=\"sourcePurl\" type=\"string\" />");
        builder.AppendLine("      <attribute id=\"targetPurl\" title=\"targetPurl\" type=\"string\" />");
        builder.AppendLine("    </attributes>");
        builder.AppendLine("    <nodes>");
        foreach (var node in result.Nodes.OrderBy(n => n.Id, StringComparer.Ordinal))
        {
            builder.Append("      <node id=\"").Append(Xml(node.Id)).Append("\" label=\"").Append(Xml(node.Name)).AppendLine("\">");
            builder.AppendLine("        <attvalues>");
            AppendGexfValue(builder, "kind", node.Kind, 10);
            AppendGexfValue(builder, "symbol", node.Symbol, 10);
            AppendGexfValue(builder, "type", node.Type, 10);
            AppendGexfValue(builder, "purl", node.Purl, 10);
            AppendGexfValue(builder, "file", node.FileName, 10);
            AppendGexfValue(builder, "method", node.MethodName, 10);
            AppendGexfValue(builder, "line", node.LineNumber.ToString(CultureInfo.InvariantCulture), 10);
            AppendGexfValue(builder, "category", node.Category, 10);
            AppendGexfValue(builder, "source", node.IsSource.ToString().ToLowerInvariant(), 10);
            AppendGexfValue(builder, "sink", node.IsSink.ToString().ToLowerInvariant(), 10);
            AppendGexfValue(builder, "code", node.Code, 10);
            builder.AppendLine("        </attvalues>");
            builder.AppendLine("      </node>");
        }
        builder.AppendLine("    </nodes>");
        builder.AppendLine("    <edges>");
        foreach (var edge in result.Edges.OrderBy(e => e.Id, StringComparer.Ordinal))
        {
            builder.Append("      <edge id=\"").Append(Xml(edge.Id)).Append("\" source=\"").Append(Xml(edge.SourceId)).Append("\" target=\"").Append(Xml(edge.TargetId)).AppendLine("\">");
            builder.AppendLine("        <attvalues>");
            AppendGexfValue(builder, "kind", edge.Kind, 10);
            AppendGexfValue(builder, "label", edge.Label, 10);
            AppendGexfValue(builder, "sourcePurl", edge.SourcePurl, 10);
            AppendGexfValue(builder, "targetPurl", edge.TargetPurl, 10);
            builder.AppendLine("        </attvalues>");
            builder.AppendLine("      </edge>");
        }
        builder.AppendLine("    </edges>");
        builder.AppendLine("  </graph>");
        builder.AppendLine("</gexf>");
        return builder.ToString();
    }

    private static void AppendGraphMlData(StringBuilder builder, string key, string? value, int indent)
    {
        builder.Append(' ', indent).Append("<data key=\"").Append(Xml(key)).Append("\">").Append(Xml(value ?? string.Empty)).AppendLine("</data>");
    }

    private static void AppendGexfValue(StringBuilder builder, string key, string? value, int indent)
    {
        builder.Append(' ', indent).Append("<attvalue for=\"").Append(Xml(key)).Append("\" value=\"").Append(Xml(value ?? string.Empty)).AppendLine("\" />");
    }

    private static string EscapeMermaid(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "#quot;", StringComparison.Ordinal)
        .Replace("\r", " ", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal);

    private static string Xml(string? value) => SecurityElement.Escape(value ?? string.Empty) ?? string.Empty;
}

internal static class CallGraphExporterReference
{
    public static string Export(CallGraph callGraph, CallGraphExportFormat format, IReadOnlyDictionary<string, NodeReachability>? reachability = null) => format switch
    {
        CallGraphExportFormat.Mermaid => ToMermaid(callGraph),
        CallGraphExportFormat.GraphMl => ToGraphMl(callGraph, reachability),
        CallGraphExportFormat.Gexf => ToGexf(callGraph, reachability),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported call graph export format")
    };

    public static bool TryParseFormat(string? value, out CallGraphExportFormat format)
    {
        format = CallGraphExportFormat.Json;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "mermaid" or "mmd" => Set(CallGraphExportFormat.Mermaid, out format),
            "graphml" or "graph-ml" => Set(CallGraphExportFormat.GraphMl, out format),
            "gexf" => Set(CallGraphExportFormat.Gexf, out format),
            _ => false
        };
    }

    public static string GetDefaultExtension(CallGraphExportFormat format) => format switch
    {
        CallGraphExportFormat.Mermaid => ".mmd",
        CallGraphExportFormat.GraphMl => ".graphml",
        CallGraphExportFormat.Gexf => ".gexf",
        _ => ".txt"
    };

    private static bool Set(CallGraphExportFormat value, out CallGraphExportFormat format)
    {
        format = value;
        return true;
    }

    private static string ToMermaid(CallGraph callGraph)
    {
        var builder = new StringBuilder();
        builder.AppendLine("flowchart LR");

        var mermaidIds = callGraph.Nodes
            .OrderBy(n => n.Id, StringComparer.Ordinal)
            .Select((node, index) => new { node, id = $"n{index + 1}" })
            .GroupBy(x => x.node.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().id, StringComparer.Ordinal);

        foreach (var node in callGraph.Nodes.OrderBy(n => n.Id, StringComparer.Ordinal))
        {
            builder.Append("    ")
                .Append(mermaidIds[node.Id])
                .Append("[\"")
                .Append(EscapeMermaidLabel(node.Label ?? node.Name))
                .AppendLine("\"]");
        }

        foreach (var edge in callGraph.Edges.OrderBy(e => e.SourceId, StringComparer.Ordinal).ThenBy(e => e.TargetId, StringComparer.Ordinal).ThenBy(e => e.CallLocation?.FileName, StringComparer.Ordinal).ThenBy(e => e.CallLocation?.LineNumber).ThenBy(e => e.CallLocation?.ColumnNumber))
        {
            if (!mermaidIds.TryGetValue(edge.SourceId, out var sourceId) || !mermaidIds.TryGetValue(edge.TargetId, out var targetId))
            {
                continue;
            }

            builder.Append("    ")
                .Append(sourceId)
                .Append(" -->|\"")
                .Append(EscapeMermaidLabel(edge.CallType.ToString()))
                .Append("\"| ")
                .AppendLine(targetId);
        }

        return builder.ToString();
    }

    private static string ToGraphMl(CallGraph callGraph, IReadOnlyDictionary<string, NodeReachability>? reachability)
    {
        var builder = new StringBuilder();
        builder.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        builder.AppendLine("<graphml xmlns=\"http://graphml.graphdrawing.org/xmlns\">");
        builder.AppendLine("  <key id=\"label\" for=\"node\" attr.name=\"label\" attr.type=\"string\" />");
        builder.AppendLine("  <key id=\"kind\" for=\"node\" attr.name=\"kind\" attr.type=\"string\" />");
        builder.AppendLine("  <key id=\"file\" for=\"node\" attr.name=\"file\" attr.type=\"string\" />");
        builder.AppendLine("  <key id=\"purl\" for=\"node\" attr.name=\"purl\" attr.type=\"string\" />");
        builder.AppendLine("  <key id=\"external\" for=\"node\" attr.name=\"external\" attr.type=\"boolean\" />");
        builder.AppendLine("  <key id=\"reachableEntryPoints\" for=\"node\" attr.name=\"reachableEntryPoints\" attr.type=\"string\" />");
        builder.AppendLine("  <key id=\"minDepthFromEntryPoint\" for=\"node\" attr.name=\"minDepthFromEntryPoint\" attr.type=\"int\" />");
        builder.AppendLine("  <key id=\"fanIn\" for=\"node\" attr.name=\"fanIn\" attr.type=\"int\" />");
        builder.AppendLine("  <key id=\"fanOut\" for=\"node\" attr.name=\"fanOut\" attr.type=\"int\" />");
        builder.AppendLine("  <key id=\"inRecursiveCycle\" for=\"node\" attr.name=\"inRecursiveCycle\" attr.type=\"boolean\" />");
        builder.AppendLine("  <key id=\"genericInstantiation\" for=\"node\" attr.name=\"genericInstantiation\" attr.type=\"string\" />");
        builder.AppendLine("  <key id=\"callType\" for=\"edge\" attr.name=\"callType\" attr.type=\"string\" />");
        builder.AppendLine("  <key id=\"sourcePurl\" for=\"edge\" attr.name=\"sourcePurl\" attr.type=\"string\" />");
        builder.AppendLine("  <key id=\"targetPurl\" for=\"edge\" attr.name=\"targetPurl\" attr.type=\"string\" />");
        builder.AppendLine("  <key id=\"location\" for=\"edge\" attr.name=\"location\" attr.type=\"string\" />");
        builder.AppendLine("  <key id=\"callSiteCount\" for=\"edge\" attr.name=\"callSiteCount\" attr.type=\"int\" />");
        builder.AppendLine("  <key id=\"dispatchConfidence\" for=\"edge\" attr.name=\"dispatchConfidence\" attr.type=\"string\" />");
        builder.AppendLine("  <graph id=\"callgraph\" edgedefault=\"directed\">");

        foreach (var node in callGraph.Nodes.OrderBy(n => n.Id, StringComparer.Ordinal))
        {
            builder.Append("    <node id=\"").Append(Xml(node.Id)).AppendLine("\">");
            AppendGraphMlData(builder, "label", node.Label ?? node.Name, 6);
            AppendGraphMlData(builder, "kind", node.Kind, 6);
            AppendGraphMlData(builder, "file", node.FileName, 6);
            AppendGraphMlData(builder, "purl", node.Purl, 6);
            AppendGraphMlData(builder, "external", node.IsExternal.ToString().ToLowerInvariant(), 6);
            if (reachability is not null && reachability.TryGetValue(node.Id, out var facts))
            {
                AppendGraphMlData(builder, "reachableEntryPoints", string.Join(",", facts.ReachableEntryPoints), 6);
                if (facts.DepthFromEntryPoint is { } depth)
                {
                    AppendGraphMlData(builder, "minDepthFromEntryPoint", depth.ToString(CultureInfo.InvariantCulture), 6);
                }

                AppendGraphMlData(builder, "fanIn", facts.FanIn.ToString(CultureInfo.InvariantCulture), 6);
                AppendGraphMlData(builder, "fanOut", facts.FanOut.ToString(CultureInfo.InvariantCulture), 6);
                AppendGraphMlData(builder, "inRecursiveCycle", facts.InRecursiveCycle.ToString().ToLowerInvariant(), 6);
            }

            AppendGraphMlData(builder, "genericInstantiation", node.GenericInstantiation, 6);
            builder.AppendLine("    </node>");
        }

        var edgeIndex = 0;
        foreach (var edge in callGraph.Edges.OrderBy(e => e.SourceId, StringComparer.Ordinal).ThenBy(e => e.TargetId, StringComparer.Ordinal).ThenBy(e => e.CallLocation?.FileName, StringComparer.Ordinal).ThenBy(e => e.CallLocation?.LineNumber).ThenBy(e => e.CallLocation?.ColumnNumber))
        {
            builder.Append("    <edge id=\"e").Append(++edgeIndex).Append("\" source=\"").Append(Xml(edge.SourceId)).Append("\" target=\"").Append(Xml(edge.TargetId)).AppendLine("\">");
            AppendGraphMlData(builder, "callType", edge.CallType.ToString(), 6);
            AppendGraphMlData(builder, "sourcePurl", edge.SourcePurl, 6);
            AppendGraphMlData(builder, "targetPurl", edge.TargetPurl, 6);
            AppendGraphMlData(builder, "location", FormatLocation(edge.CallLocation), 6);
            AppendGraphMlData(builder, "callSiteCount", edge.CallSiteCount.ToString(CultureInfo.InvariantCulture), 6);
            AppendGraphMlData(builder, "dispatchConfidence", edge.DispatchConfidence, 6);
            builder.AppendLine("    </edge>");
        }

        builder.AppendLine("  </graph>");
        builder.AppendLine("</graphml>");
        return builder.ToString();
    }

    private static string ToGexf(CallGraph callGraph, IReadOnlyDictionary<string, NodeReachability>? reachability)
    {
        var builder = new StringBuilder();
        builder.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        builder.AppendLine("<gexf xmlns=\"http://www.gexf.net/1.3\" version=\"1.3\">");
        builder.AppendLine("  <graph mode=\"static\" defaultedgetype=\"directed\">");
        builder.AppendLine("    <attributes class=\"node\">");
        builder.AppendLine("      <attribute id=\"kind\" title=\"kind\" type=\"string\" />");
        builder.AppendLine("      <attribute id=\"file\" title=\"file\" type=\"string\" />");
        builder.AppendLine("      <attribute id=\"purl\" title=\"purl\" type=\"string\" />");
        builder.AppendLine("      <attribute id=\"external\" title=\"external\" type=\"boolean\" />");
        builder.AppendLine("      <attribute id=\"reachableEntryPoints\" title=\"reachableEntryPoints\" type=\"string\" />");
        builder.AppendLine("      <attribute id=\"minDepthFromEntryPoint\" title=\"minDepthFromEntryPoint\" type=\"int\" />");
        builder.AppendLine("      <attribute id=\"fanIn\" title=\"fanIn\" type=\"int\" />");
        builder.AppendLine("      <attribute id=\"fanOut\" title=\"fanOut\" type=\"int\" />");
        builder.AppendLine("      <attribute id=\"inRecursiveCycle\" title=\"inRecursiveCycle\" type=\"boolean\" />");
        builder.AppendLine("    </attributes>");
        builder.AppendLine("    <attributes class=\"edge\">");
        builder.AppendLine("      <attribute id=\"callType\" title=\"callType\" type=\"string\" />");
        builder.AppendLine("      <attribute id=\"sourcePurl\" title=\"sourcePurl\" type=\"string\" />");
        builder.AppendLine("      <attribute id=\"targetPurl\" title=\"targetPurl\" type=\"string\" />");
        builder.AppendLine("      <attribute id=\"location\" title=\"location\" type=\"string\" />");
        builder.AppendLine("      <attribute id=\"callSiteCount\" title=\"callSiteCount\" type=\"int\" />");
        builder.AppendLine("      <attribute id=\"dispatchConfidence\" title=\"dispatchConfidence\" type=\"string\" />");
        builder.AppendLine("    </attributes>");
        builder.AppendLine("    <nodes>");

        foreach (var node in callGraph.Nodes.OrderBy(n => n.Id, StringComparer.Ordinal))
        {
            builder.Append("      <node id=\"").Append(Xml(node.Id)).Append("\" label=\"").Append(Xml(node.Label ?? node.Name)).AppendLine("\">");
            builder.AppendLine("        <attvalues>");
            AppendGexfValue(builder, "kind", node.Kind, 10);
            AppendGexfValue(builder, "file", node.FileName, 10);
            AppendGexfValue(builder, "purl", node.Purl, 10);
            AppendGexfValue(builder, "external", node.IsExternal.ToString().ToLowerInvariant(), 10);
            if (reachability is not null && reachability.TryGetValue(node.Id, out var facts))
            {
                AppendGexfValue(builder, "reachableEntryPoints", string.Join(",", facts.ReachableEntryPoints), 10);
                if (facts.DepthFromEntryPoint is { } depth)
                {
                    AppendGexfValue(builder, "minDepthFromEntryPoint", depth.ToString(CultureInfo.InvariantCulture), 10);
                }

                AppendGexfValue(builder, "fanIn", facts.FanIn.ToString(CultureInfo.InvariantCulture), 10);
                AppendGexfValue(builder, "fanOut", facts.FanOut.ToString(CultureInfo.InvariantCulture), 10);
                AppendGexfValue(builder, "inRecursiveCycle", facts.InRecursiveCycle.ToString().ToLowerInvariant(), 10);
            }

            builder.AppendLine("        </attvalues>");
            builder.AppendLine("      </node>");
        }

        builder.AppendLine("    </nodes>");
        builder.AppendLine("    <edges>");
        var edgeIndex = 0;
        foreach (var edge in callGraph.Edges.OrderBy(e => e.SourceId, StringComparer.Ordinal).ThenBy(e => e.TargetId, StringComparer.Ordinal).ThenBy(e => e.CallLocation?.FileName, StringComparer.Ordinal).ThenBy(e => e.CallLocation?.LineNumber).ThenBy(e => e.CallLocation?.ColumnNumber))
        {
            builder.Append("      <edge id=\"e").Append(++edgeIndex).Append("\" source=\"").Append(Xml(edge.SourceId)).Append("\" target=\"").Append(Xml(edge.TargetId)).AppendLine("\">");
            builder.AppendLine("        <attvalues>");
            AppendGexfValue(builder, "callType", edge.CallType.ToString(), 10);
            AppendGexfValue(builder, "sourcePurl", edge.SourcePurl, 10);
            AppendGexfValue(builder, "targetPurl", edge.TargetPurl, 10);
            AppendGexfValue(builder, "location", FormatLocation(edge.CallLocation), 10);
            AppendGexfValue(builder, "callSiteCount", edge.CallSiteCount.ToString(CultureInfo.InvariantCulture), 10);
            AppendGexfValue(builder, "dispatchConfidence", edge.DispatchConfidence, 10);
            builder.AppendLine("        </attvalues>");
            builder.AppendLine("      </edge>");
        }

        builder.AppendLine("    </edges>");
        builder.AppendLine("  </graph>");
        builder.AppendLine("</gexf>");
        return builder.ToString();
    }

    private static void AppendGraphMlData(StringBuilder builder, string key, string? value, int indent)
    {
        builder.Append(' ', indent).Append("<data key=\"").Append(Xml(key)).Append("\">").Append(Xml(value ?? string.Empty)).AppendLine("</data>");
    }

    private static void AppendGexfValue(StringBuilder builder, string key, string? value, int indent)
    {
        builder.Append(' ', indent).Append("<attvalue for=\"").Append(Xml(key)).Append("\" value=\"").Append(Xml(value ?? string.Empty)).AppendLine("\" />");
    }

    private static string FormatLocation(CallLocation? location) => location is null
        ? string.Empty
        : $"{location.FileName}:{location.LineNumber}:{location.ColumnNumber}";

    private static string EscapeMermaidLabel(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "#quot;", StringComparison.Ordinal)
        .Replace("\r", " ", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal);

    private static string Xml(string value) => SecurityElement.Escape(value) ?? string.Empty;
}
