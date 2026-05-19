using System.Security;
using System.Text;

namespace Depscan;

public enum DataFlowExportFormat
{
    Mermaid,
    GraphMl,
    Gexf
}

public static class DataFlowExporter
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
            .ToDictionary(item => item.Id, item => item.MermaidId, StringComparer.Ordinal);

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
        foreach (var key in new[] { "label", "kind", "symbol", "type", "file", "method", "line", "category", "source", "sink", "code" })
        {
            builder.Append("  <key id=\"").Append(Xml(key)).Append("\" for=\"node\" attr.name=\"").Append(Xml(key)).Append("\" attr.type=\"string\" />").AppendLine();
        }
        foreach (var key in new[] { "kind", "label", "file", "line" })
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
            AppendGraphMlData(builder, "file", node.FileName, 6);
            AppendGraphMlData(builder, "method", node.MethodName, 6);
            AppendGraphMlData(builder, "line", node.LineNumber.ToString(), 6);
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
            AppendGraphMlData(builder, "edge_file", edge.FileName, 6);
            AppendGraphMlData(builder, "edge_line", edge.LineNumber.ToString(), 6);
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
        foreach (var key in new[] { "kind", "symbol", "type", "file", "method", "line", "category", "source", "sink", "code" })
        {
            builder.Append("      <attribute id=\"").Append(Xml(key)).Append("\" title=\"").Append(Xml(key)).Append("\" type=\"string\" />").AppendLine();
        }
        builder.AppendLine("    </attributes>");
        builder.AppendLine("    <attributes class=\"edge\">");
        builder.AppendLine("      <attribute id=\"kind\" title=\"kind\" type=\"string\" />");
        builder.AppendLine("      <attribute id=\"label\" title=\"label\" type=\"string\" />");
        builder.AppendLine("    </attributes>");
        builder.AppendLine("    <nodes>");
        foreach (var node in result.Nodes.OrderBy(n => n.Id, StringComparer.Ordinal))
        {
            builder.Append("      <node id=\"").Append(Xml(node.Id)).Append("\" label=\"").Append(Xml(node.Name)).AppendLine("\">");
            builder.AppendLine("        <attvalues>");
            AppendGexfValue(builder, "kind", node.Kind, 10);
            AppendGexfValue(builder, "symbol", node.Symbol, 10);
            AppendGexfValue(builder, "type", node.Type, 10);
            AppendGexfValue(builder, "file", node.FileName, 10);
            AppendGexfValue(builder, "method", node.MethodName, 10);
            AppendGexfValue(builder, "line", node.LineNumber.ToString(), 10);
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
