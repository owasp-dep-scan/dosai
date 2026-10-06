using System.Globalization;
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
    /// <summary>Longest indent the formats use, sliced per write so no per-line padding strings are allocated.</summary>
    private const string Indent = "          ";

    public static string Export(DataFlowResult result, DataFlowExportFormat format)
    {
        using var writer = new StringWriter();
        Export(writer, result, format);
        return writer.ToString();
    }

    /// <summary>
    ///     Writes the export straight to <paramref name="writer" />. The string overload above
    ///     wraps this one; callers with a file (the CLI's graph sidecars) write through a
    ///     <see cref="StreamWriter" /> so the document is never materialised as one string -
    ///     on a large tree that string bounded peak memory for no benefit (issue #75).
    /// </summary>
    public static void Export(TextWriter writer, DataFlowResult result, DataFlowExportFormat format)
    {
        switch (format)
        {
            case DataFlowExportFormat.Mermaid:
                WriteMermaid(writer, result);
                break;
            case DataFlowExportFormat.GraphMl:
                WriteGraphMl(writer, result);
                break;
            case DataFlowExportFormat.Gexf:
                WriteGexf(writer, result);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported data-flow export format");
        }
    }

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

    private static void WriteMermaid(TextWriter writer, DataFlowResult result)
    {
        writer.WriteLine("flowchart LR");
        var ids = result.Nodes
            .OrderBy(n => n.Id, StringComparer.Ordinal)
            .Select((node, index) => new { node.Id, MermaidId = $"df{index + 1}" })
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().MermaidId, StringComparer.Ordinal);

        foreach (var node in result.Nodes.OrderBy(n => n.Id, StringComparer.Ordinal))
        {
            var shape = node.IsSource ? "([" : node.IsSink ? "[[" : "[";
            var endShape = node.IsSource ? "])" : node.IsSink ? "]]" : "]";
            writer.Write("    ");
            writer.Write(ids[node.Id]);
            writer.Write(shape);
            writer.Write('"');
            writer.Write(EscapeMermaid(node.Name));
            writer.Write(endShape);
            writer.WriteLine();
        }

        foreach (var edge in result.Edges.OrderBy(e => e.Id, StringComparer.Ordinal))
        {
            if (!ids.TryGetValue(edge.SourceId, out var sourceId) || !ids.TryGetValue(edge.TargetId, out var targetId))
            {
                continue;
            }
            writer.Write("    ");
            writer.Write(sourceId);
            writer.Write(" -->|\"");
            writer.Write(EscapeMermaid(edge.Kind));
            writer.Write("\"| ");
            writer.Write(targetId);
            writer.WriteLine();
        }
    }

    private static void WriteGraphMl(TextWriter writer, DataFlowResult result)
    {
        writer.WriteLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        writer.WriteLine("<graphml xmlns=\"http://graphml.graphdrawing.org/xmlns\">");
        foreach (var key in new[] { "label", "kind", "symbol", "type", "purl", "file", "method", "line", "category", "source", "sink", "code" })
        {
            writer.Write("  <key id=\"");
            writer.Write(Xml(key));
            writer.Write("\" for=\"node\" attr.name=\"");
            writer.Write(Xml(key));
            writer.Write("\" attr.type=\"string\" />");
            writer.WriteLine();
        }
        foreach (var key in new[] { "kind", "label", "sourcePurl", "targetPurl", "file", "line" })
        {
            writer.Write("  <key id=\"edge_");
            writer.Write(Xml(key));
            writer.Write("\" for=\"edge\" attr.name=\"");
            writer.Write(Xml(key));
            writer.Write("\" attr.type=\"string\" />");
            writer.WriteLine();
        }
        writer.WriteLine("  <graph id=\"dataflows\" edgedefault=\"directed\">");

        foreach (var node in result.Nodes.OrderBy(n => n.Id, StringComparer.Ordinal))
        {
            writer.Write("    <node id=\"");
            writer.Write(Xml(node.Id));
            writer.WriteLine("\">");
            WriteGraphMlData(writer, "label", node.Name, 6);
            WriteGraphMlData(writer, "kind", node.Kind, 6);
            WriteGraphMlData(writer, "symbol", node.Symbol, 6);
            WriteGraphMlData(writer, "type", node.Type, 6);
            WriteGraphMlData(writer, "purl", node.Purl, 6);
            WriteGraphMlData(writer, "file", node.FileName, 6);
            WriteGraphMlData(writer, "method", node.MethodName, 6);
            WriteGraphMlData(writer, "line", node.LineNumber.ToString(CultureInfo.InvariantCulture), 6);
            WriteGraphMlData(writer, "category", node.Category, 6);
            WriteGraphMlData(writer, "source", node.IsSource.ToString().ToLowerInvariant(), 6);
            WriteGraphMlData(writer, "sink", node.IsSink.ToString().ToLowerInvariant(), 6);
            WriteGraphMlData(writer, "code", node.Code, 6);
            writer.WriteLine("    </node>");
        }

        foreach (var edge in result.Edges.OrderBy(e => e.Id, StringComparer.Ordinal))
        {
            writer.Write("    <edge id=\"");
            writer.Write(Xml(edge.Id));
            writer.Write("\" source=\"");
            writer.Write(Xml(edge.SourceId));
            writer.Write("\" target=\"");
            writer.Write(Xml(edge.TargetId));
            writer.WriteLine("\">");
            WriteGraphMlData(writer, "edge_kind", edge.Kind, 6);
            WriteGraphMlData(writer, "edge_label", edge.Label, 6);
            WriteGraphMlData(writer, "edge_sourcePurl", edge.SourcePurl, 6);
            WriteGraphMlData(writer, "edge_targetPurl", edge.TargetPurl, 6);
            WriteGraphMlData(writer, "edge_file", edge.FileName, 6);
            WriteGraphMlData(writer, "edge_line", edge.LineNumber.ToString(CultureInfo.InvariantCulture), 6);
            writer.WriteLine("    </edge>");
        }

        writer.WriteLine("  </graph>");
        writer.WriteLine("</graphml>");
    }

    private static void WriteGexf(TextWriter writer, DataFlowResult result)
    {
        writer.WriteLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        writer.WriteLine("<gexf xmlns=\"http://www.gexf.net/1.3\" version=\"1.3\">");
        writer.WriteLine("  <graph mode=\"static\" defaultedgetype=\"directed\">");
        writer.WriteLine("    <attributes class=\"node\">");
        foreach (var key in new[] { "kind", "symbol", "type", "purl", "file", "method", "line", "category", "source", "sink", "code" })
        {
            writer.Write("      <attribute id=\"");
            writer.Write(Xml(key));
            writer.Write("\" title=\"");
            writer.Write(Xml(key));
            writer.Write("\" type=\"string\" />");
            writer.WriteLine();
        }
        writer.WriteLine("    </attributes>");
        writer.WriteLine("    <attributes class=\"edge\">");
        writer.WriteLine("      <attribute id=\"kind\" title=\"kind\" type=\"string\" />");
        writer.WriteLine("      <attribute id=\"label\" title=\"label\" type=\"string\" />");
        writer.WriteLine("      <attribute id=\"sourcePurl\" title=\"sourcePurl\" type=\"string\" />");
        writer.WriteLine("      <attribute id=\"targetPurl\" title=\"targetPurl\" type=\"string\" />");
        writer.WriteLine("    </attributes>");
        writer.WriteLine("    <nodes>");
        foreach (var node in result.Nodes.OrderBy(n => n.Id, StringComparer.Ordinal))
        {
            writer.Write("      <node id=\"");
            writer.Write(Xml(node.Id));
            writer.Write("\" label=\"");
            writer.Write(Xml(node.Name));
            writer.WriteLine("\">");
            writer.WriteLine("        <attvalues>");
            WriteGexfValue(writer, "kind", node.Kind, 10);
            WriteGexfValue(writer, "symbol", node.Symbol, 10);
            WriteGexfValue(writer, "type", node.Type, 10);
            WriteGexfValue(writer, "purl", node.Purl, 10);
            WriteGexfValue(writer, "file", node.FileName, 10);
            WriteGexfValue(writer, "method", node.MethodName, 10);
            WriteGexfValue(writer, "line", node.LineNumber.ToString(CultureInfo.InvariantCulture), 10);
            WriteGexfValue(writer, "category", node.Category, 10);
            WriteGexfValue(writer, "source", node.IsSource.ToString().ToLowerInvariant(), 10);
            WriteGexfValue(writer, "sink", node.IsSink.ToString().ToLowerInvariant(), 10);
            WriteGexfValue(writer, "code", node.Code, 10);
            writer.WriteLine("        </attvalues>");
            writer.WriteLine("      </node>");
        }
        writer.WriteLine("    </nodes>");
        writer.WriteLine("    <edges>");
        foreach (var edge in result.Edges.OrderBy(e => e.Id, StringComparer.Ordinal))
        {
            writer.Write("      <edge id=\"");
            writer.Write(Xml(edge.Id));
            writer.Write("\" source=\"");
            writer.Write(Xml(edge.SourceId));
            writer.Write("\" target=\"");
            writer.Write(Xml(edge.TargetId));
            writer.WriteLine("\">");
            writer.WriteLine("        <attvalues>");
            WriteGexfValue(writer, "kind", edge.Kind, 10);
            WriteGexfValue(writer, "label", edge.Label, 10);
            WriteGexfValue(writer, "sourcePurl", edge.SourcePurl, 10);
            WriteGexfValue(writer, "targetPurl", edge.TargetPurl, 10);
            writer.WriteLine("        </attvalues>");
            writer.WriteLine("      </edge>");
        }
        writer.WriteLine("    </edges>");
        writer.WriteLine("  </graph>");
        writer.WriteLine("</gexf>");
    }

    private static void WriteGraphMlData(TextWriter writer, string key, string? value, int indent)
    {
        writer.Write(Indent[..indent]);
        writer.Write("<data key=\"");
        writer.Write(Xml(key));
        writer.Write("\">");
        writer.Write(Xml(value ?? string.Empty));
        writer.WriteLine("</data>");
    }

    private static void WriteGexfValue(TextWriter writer, string key, string? value, int indent)
    {
        writer.Write(Indent[..indent]);
        writer.Write("<attvalue for=\"");
        writer.Write(Xml(key));
        writer.Write("\" value=\"");
        writer.Write(Xml(value ?? string.Empty));
        writer.WriteLine("\" />");
    }

    private static string EscapeMermaid(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "#quot;", StringComparison.Ordinal)
        .Replace("\r", " ", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal);

    private static string Xml(string? value) => SecurityElement.Escape(value ?? string.Empty) ?? string.Empty;
}
