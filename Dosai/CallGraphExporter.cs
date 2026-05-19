using System.Security;
using System.Text;

namespace Depscan;

public enum CallGraphExportFormat
{
    Json,
    Mermaid,
    GraphMl,
    Gexf
}

public static class CallGraphExporter
{
    public static string Export(CallGraph callGraph, CallGraphExportFormat format) => format switch
    {
        CallGraphExportFormat.Mermaid => ToMermaid(callGraph),
        CallGraphExportFormat.GraphMl => ToGraphMl(callGraph),
        CallGraphExportFormat.Gexf => ToGexf(callGraph),
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
            .ToDictionary(x => x.node.Id, x => x.id, StringComparer.Ordinal);

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

    private static string ToGraphMl(CallGraph callGraph)
    {
        var builder = new StringBuilder();
        builder.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        builder.AppendLine("<graphml xmlns=\"http://graphml.graphdrawing.org/xmlns\">");
        builder.AppendLine("  <key id=\"label\" for=\"node\" attr.name=\"label\" attr.type=\"string\" />");
        builder.AppendLine("  <key id=\"kind\" for=\"node\" attr.name=\"kind\" attr.type=\"string\" />");
        builder.AppendLine("  <key id=\"file\" for=\"node\" attr.name=\"file\" attr.type=\"string\" />");
        builder.AppendLine("  <key id=\"purl\" for=\"node\" attr.name=\"purl\" attr.type=\"string\" />");
        builder.AppendLine("  <key id=\"external\" for=\"node\" attr.name=\"external\" attr.type=\"boolean\" />");
        builder.AppendLine("  <key id=\"callType\" for=\"edge\" attr.name=\"callType\" attr.type=\"string\" />");
        builder.AppendLine("  <key id=\"sourcePurl\" for=\"edge\" attr.name=\"sourcePurl\" attr.type=\"string\" />");
        builder.AppendLine("  <key id=\"targetPurl\" for=\"edge\" attr.name=\"targetPurl\" attr.type=\"string\" />");
        builder.AppendLine("  <key id=\"location\" for=\"edge\" attr.name=\"location\" attr.type=\"string\" />");
        builder.AppendLine("  <graph id=\"callgraph\" edgedefault=\"directed\">");

        foreach (var node in callGraph.Nodes.OrderBy(n => n.Id, StringComparer.Ordinal))
        {
            builder.Append("    <node id=\"").Append(Xml(node.Id)).AppendLine("\">");
            AppendGraphMlData(builder, "label", node.Label ?? node.Name, 6);
            AppendGraphMlData(builder, "kind", node.Kind, 6);
            AppendGraphMlData(builder, "file", node.FileName, 6);
            AppendGraphMlData(builder, "purl", node.Purl, 6);
            AppendGraphMlData(builder, "external", node.IsExternal.ToString().ToLowerInvariant(), 6);
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
            builder.AppendLine("    </edge>");
        }

        builder.AppendLine("  </graph>");
        builder.AppendLine("</graphml>");
        return builder.ToString();
    }

    private static string ToGexf(CallGraph callGraph)
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
        builder.AppendLine("    </attributes>");
        builder.AppendLine("    <attributes class=\"edge\">");
        builder.AppendLine("      <attribute id=\"callType\" title=\"callType\" type=\"string\" />");
        builder.AppendLine("      <attribute id=\"sourcePurl\" title=\"sourcePurl\" type=\"string\" />");
        builder.AppendLine("      <attribute id=\"targetPurl\" title=\"targetPurl\" type=\"string\" />");
        builder.AppendLine("      <attribute id=\"location\" title=\"location\" type=\"string\" />");
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

