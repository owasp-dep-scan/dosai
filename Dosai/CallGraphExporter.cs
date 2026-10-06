using System.Globalization;
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
    /// <summary>Longest indent the formats use, sliced per write so no per-line padding strings are allocated.</summary>
    private const string Indent = "          ";

    public static string Export(CallGraph callGraph, CallGraphExportFormat format, IReadOnlyDictionary<string, NodeReachability>? reachability = null)
    {
        using var writer = new StringWriter();
        Export(writer, callGraph, format, reachability);
        return writer.ToString();
    }

    /// <summary>
    ///     Writes the export straight to <paramref name="writer" />. The string overload above
    ///     wraps this one; callers with a file write through a <see cref="StreamWriter" /> so the
    ///     document is never materialised as one string (issue #75).
    /// </summary>
    public static void Export(TextWriter writer, CallGraph callGraph, CallGraphExportFormat format, IReadOnlyDictionary<string, NodeReachability>? reachability = null)
    {
        switch (format)
        {
            case CallGraphExportFormat.Mermaid:
                WriteMermaid(writer, callGraph);
                break;
            case CallGraphExportFormat.GraphMl:
                WriteGraphMl(writer, callGraph, reachability);
                break;
            case CallGraphExportFormat.Gexf:
                WriteGexf(writer, callGraph, reachability);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported call graph export format");
        }
    }

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

    private static void WriteMermaid(TextWriter writer, CallGraph callGraph)
    {
        writer.WriteLine("flowchart LR");

        var mermaidIds = callGraph.Nodes
            .OrderBy(n => n.Id, StringComparer.Ordinal)
            .Select((node, index) => new { node, id = $"n{index + 1}" })
            .GroupBy(x => x.node.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().id, StringComparer.Ordinal);

        foreach (var node in callGraph.Nodes.OrderBy(n => n.Id, StringComparer.Ordinal))
        {
            writer.Write("    ");
            writer.Write(mermaidIds[node.Id]);
            writer.Write("[\"");
            writer.Write(EscapeMermaidLabel(node.Label ?? node.Name));
            writer.WriteLine("\"]");
        }

        foreach (var edge in callGraph.Edges.OrderBy(e => e.SourceId, StringComparer.Ordinal).ThenBy(e => e.TargetId, StringComparer.Ordinal).ThenBy(e => e.CallLocation?.FileName, StringComparer.Ordinal).ThenBy(e => e.CallLocation?.LineNumber).ThenBy(e => e.CallLocation?.ColumnNumber))
        {
            if (!mermaidIds.TryGetValue(edge.SourceId, out var sourceId) || !mermaidIds.TryGetValue(edge.TargetId, out var targetId))
            {
                continue;
            }

            writer.Write("    ");
            writer.Write(sourceId);
            writer.Write(" -->|\"");
            writer.Write(EscapeMermaidLabel(edge.CallType.ToString()));
            writer.Write("\"| ");
            writer.WriteLine(targetId);
        }
    }

    private static void WriteGraphMl(TextWriter writer, CallGraph callGraph, IReadOnlyDictionary<string, NodeReachability>? reachability)
    {
        writer.WriteLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        writer.WriteLine("<graphml xmlns=\"http://graphml.graphdrawing.org/xmlns\">");
        writer.WriteLine("  <key id=\"label\" for=\"node\" attr.name=\"label\" attr.type=\"string\" />");
        writer.WriteLine("  <key id=\"kind\" for=\"node\" attr.name=\"kind\" attr.type=\"string\" />");
        writer.WriteLine("  <key id=\"file\" for=\"node\" attr.name=\"file\" attr.type=\"string\" />");
        writer.WriteLine("  <key id=\"purl\" for=\"node\" attr.name=\"purl\" attr.type=\"string\" />");
        writer.WriteLine("  <key id=\"external\" for=\"node\" attr.name=\"external\" attr.type=\"boolean\" />");
        writer.WriteLine("  <key id=\"reachableEntryPoints\" for=\"node\" attr.name=\"reachableEntryPoints\" attr.type=\"string\" />");
        writer.WriteLine("  <key id=\"minDepthFromEntryPoint\" for=\"node\" attr.name=\"minDepthFromEntryPoint\" attr.type=\"int\" />");
        writer.WriteLine("  <key id=\"fanIn\" for=\"node\" attr.name=\"fanIn\" attr.type=\"int\" />");
        writer.WriteLine("  <key id=\"fanOut\" for=\"node\" attr.name=\"fanOut\" attr.type=\"int\" />");
        writer.WriteLine("  <key id=\"inRecursiveCycle\" for=\"node\" attr.name=\"inRecursiveCycle\" attr.type=\"boolean\" />");
        writer.WriteLine("  <key id=\"genericInstantiation\" for=\"node\" attr.name=\"genericInstantiation\" attr.type=\"string\" />");
        writer.WriteLine("  <key id=\"callType\" for=\"edge\" attr.name=\"callType\" attr.type=\"string\" />");
        writer.WriteLine("  <key id=\"sourcePurl\" for=\"edge\" attr.name=\"sourcePurl\" attr.type=\"string\" />");
        writer.WriteLine("  <key id=\"targetPurl\" for=\"edge\" attr.name=\"targetPurl\" attr.type=\"string\" />");
        writer.WriteLine("  <key id=\"location\" for=\"edge\" attr.name=\"location\" attr.type=\"string\" />");
        writer.WriteLine("  <key id=\"callSiteCount\" for=\"edge\" attr.name=\"callSiteCount\" attr.type=\"int\" />");
        writer.WriteLine("  <key id=\"dispatchConfidence\" for=\"edge\" attr.name=\"dispatchConfidence\" attr.type=\"string\" />");
        writer.WriteLine("  <graph id=\"callgraph\" edgedefault=\"directed\">");

        foreach (var node in callGraph.Nodes.OrderBy(n => n.Id, StringComparer.Ordinal))
        {
            writer.Write("    <node id=\"");
            writer.Write(Xml(node.Id));
            writer.WriteLine("\">");
            WriteGraphMlData(writer, "label", node.Label ?? node.Name, 6);
            WriteGraphMlData(writer, "kind", node.Kind, 6);
            WriteGraphMlData(writer, "file", node.FileName, 6);
            WriteGraphMlData(writer, "purl", node.Purl, 6);
            WriteGraphMlData(writer, "external", node.IsExternal.ToString().ToLowerInvariant(), 6);
            if (reachability is not null && reachability.TryGetValue(node.Id, out var facts))
            {
                WriteGraphMlData(writer, "reachableEntryPoints", string.Join(",", facts.ReachableEntryPoints), 6);
                if (facts.DepthFromEntryPoint is { } depth)
                {
                    WriteGraphMlData(writer, "minDepthFromEntryPoint", depth.ToString(CultureInfo.InvariantCulture), 6);
                }

                WriteGraphMlData(writer, "fanIn", facts.FanIn.ToString(CultureInfo.InvariantCulture), 6);
                WriteGraphMlData(writer, "fanOut", facts.FanOut.ToString(CultureInfo.InvariantCulture), 6);
                WriteGraphMlData(writer, "inRecursiveCycle", facts.InRecursiveCycle.ToString().ToLowerInvariant(), 6);
            }

            WriteGraphMlData(writer, "genericInstantiation", node.GenericInstantiation, 6);
            writer.WriteLine("    </node>");
        }

        var edgeIndex = 0;
        foreach (var edge in callGraph.Edges.OrderBy(e => e.SourceId, StringComparer.Ordinal).ThenBy(e => e.TargetId, StringComparer.Ordinal).ThenBy(e => e.CallLocation?.FileName, StringComparer.Ordinal).ThenBy(e => e.CallLocation?.LineNumber).ThenBy(e => e.CallLocation?.ColumnNumber))
        {
            writer.Write("    <edge id=\"e");
            writer.Write(++edgeIndex);
            writer.Write("\" source=\"");
            writer.Write(Xml(edge.SourceId));
            writer.Write("\" target=\"");
            writer.Write(Xml(edge.TargetId));
            writer.WriteLine("\">");
            WriteGraphMlData(writer, "callType", edge.CallType.ToString(), 6);
            WriteGraphMlData(writer, "sourcePurl", edge.SourcePurl, 6);
            WriteGraphMlData(writer, "targetPurl", edge.TargetPurl, 6);
            WriteGraphMlData(writer, "location", FormatLocation(edge.CallLocation), 6);
            WriteGraphMlData(writer, "callSiteCount", edge.CallSiteCount.ToString(CultureInfo.InvariantCulture), 6);
            WriteGraphMlData(writer, "dispatchConfidence", edge.DispatchConfidence, 6);
            writer.WriteLine("    </edge>");
        }

        writer.WriteLine("  </graph>");
        writer.WriteLine("</graphml>");
    }

    private static void WriteGexf(TextWriter writer, CallGraph callGraph, IReadOnlyDictionary<string, NodeReachability>? reachability)
    {
        writer.WriteLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        writer.WriteLine("<gexf xmlns=\"http://www.gexf.net/1.3\" version=\"1.3\">");
        writer.WriteLine("  <graph mode=\"static\" defaultedgetype=\"directed\">");
        writer.WriteLine("    <attributes class=\"node\">");
        writer.WriteLine("      <attribute id=\"kind\" title=\"kind\" type=\"string\" />");
        writer.WriteLine("      <attribute id=\"file\" title=\"file\" type=\"string\" />");
        writer.WriteLine("      <attribute id=\"purl\" title=\"purl\" type=\"string\" />");
        writer.WriteLine("      <attribute id=\"external\" title=\"external\" type=\"boolean\" />");
        writer.WriteLine("      <attribute id=\"reachableEntryPoints\" title=\"reachableEntryPoints\" type=\"string\" />");
        writer.WriteLine("      <attribute id=\"minDepthFromEntryPoint\" title=\"minDepthFromEntryPoint\" type=\"int\" />");
        writer.WriteLine("      <attribute id=\"fanIn\" title=\"fanIn\" type=\"int\" />");
        writer.WriteLine("      <attribute id=\"fanOut\" title=\"fanOut\" type=\"int\" />");
        writer.WriteLine("      <attribute id=\"inRecursiveCycle\" title=\"inRecursiveCycle\" type=\"boolean\" />");
        writer.WriteLine("    </attributes>");
        writer.WriteLine("    <attributes class=\"edge\">");
        writer.WriteLine("      <attribute id=\"callType\" title=\"callType\" type=\"string\" />");
        writer.WriteLine("      <attribute id=\"sourcePurl\" title=\"sourcePurl\" type=\"string\" />");
        writer.WriteLine("      <attribute id=\"targetPurl\" title=\"targetPurl\" type=\"string\" />");
        writer.WriteLine("      <attribute id=\"location\" title=\"location\" type=\"string\" />");
        writer.WriteLine("      <attribute id=\"callSiteCount\" title=\"callSiteCount\" type=\"int\" />");
        writer.WriteLine("      <attribute id=\"dispatchConfidence\" title=\"dispatchConfidence\" type=\"string\" />");
        writer.WriteLine("    </attributes>");
        writer.WriteLine("    <nodes>");

        foreach (var node in callGraph.Nodes.OrderBy(n => n.Id, StringComparer.Ordinal))
        {
            writer.Write("      <node id=\"");
            writer.Write(Xml(node.Id));
            writer.Write("\" label=\"");
            writer.Write(Xml(node.Label ?? node.Name));
            writer.WriteLine("\">");
            writer.WriteLine("        <attvalues>");
            WriteGexfValue(writer, "kind", node.Kind, 10);
            WriteGexfValue(writer, "file", node.FileName, 10);
            WriteGexfValue(writer, "purl", node.Purl, 10);
            WriteGexfValue(writer, "external", node.IsExternal.ToString().ToLowerInvariant(), 10);
            if (reachability is not null && reachability.TryGetValue(node.Id, out var facts))
            {
                WriteGexfValue(writer, "reachableEntryPoints", string.Join(",", facts.ReachableEntryPoints), 10);
                if (facts.DepthFromEntryPoint is { } depth)
                {
                    WriteGexfValue(writer, "minDepthFromEntryPoint", depth.ToString(CultureInfo.InvariantCulture), 10);
                }

                WriteGexfValue(writer, "fanIn", facts.FanIn.ToString(CultureInfo.InvariantCulture), 10);
                WriteGexfValue(writer, "fanOut", facts.FanOut.ToString(CultureInfo.InvariantCulture), 10);
                WriteGexfValue(writer, "inRecursiveCycle", facts.InRecursiveCycle.ToString().ToLowerInvariant(), 10);
            }

            writer.WriteLine("        </attvalues>");
            writer.WriteLine("      </node>");
        }

        writer.WriteLine("    </nodes>");
        writer.WriteLine("    <edges>");
        var edgeIndex = 0;
        foreach (var edge in callGraph.Edges.OrderBy(e => e.SourceId, StringComparer.Ordinal).ThenBy(e => e.TargetId, StringComparer.Ordinal).ThenBy(e => e.CallLocation?.FileName, StringComparer.Ordinal).ThenBy(e => e.CallLocation?.LineNumber).ThenBy(e => e.CallLocation?.ColumnNumber))
        {
            writer.Write("      <edge id=\"e");
            writer.Write(++edgeIndex);
            writer.Write("\" source=\"");
            writer.Write(Xml(edge.SourceId));
            writer.Write("\" target=\"");
            writer.Write(Xml(edge.TargetId));
            writer.WriteLine("\">");
            writer.WriteLine("        <attvalues>");
            WriteGexfValue(writer, "callType", edge.CallType.ToString(), 10);
            WriteGexfValue(writer, "sourcePurl", edge.SourcePurl, 10);
            WriteGexfValue(writer, "targetPurl", edge.TargetPurl, 10);
            WriteGexfValue(writer, "location", FormatLocation(edge.CallLocation), 10);
            WriteGexfValue(writer, "callSiteCount", edge.CallSiteCount.ToString(CultureInfo.InvariantCulture), 10);
            WriteGexfValue(writer, "dispatchConfidence", edge.DispatchConfidence, 10);
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

    private static string FormatLocation(CallLocation? location) => location is null
        ? string.Empty
        : $"{location.FileName}:{location.LineNumber}:{location.ColumnNumber}";

    private static string EscapeMermaidLabel(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "#quot;", StringComparison.Ordinal)
        .Replace("\r", " ", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal);

    private static string Xml(string value) => SecurityElement.Escape(value ?? string.Empty) ?? string.Empty;
}
