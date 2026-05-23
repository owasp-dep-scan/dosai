using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Depscan;

public class CommandLine
{
    private const string DefaultOutputFile = "dosai.json";

    public static int Main(string[] args)
    {
        var rootCommand = new RootCommand("Dotnet Source and Assembly Inspector (Dosai) is a tool to list details about the namespaces and methods from sources and assemblies.");

        var pathOption = new Option<string?>("--path")
        {
            Description = "The file or directory to inspect",
            Arity = ArgumentArity.ExactlyOne,
            Required = true
        };

        var outputFileOption = new Option<string?>("--o")
        {
            Description = $"The output file location and name",
            Arity = ArgumentArity.ExactlyOne,
            DefaultValueFactory = _ => DefaultOutputFile
        };

        var callGraphFormatOption = new Option<string?>("--callgraph-format")
        {
            Description = "Export call graph separately in one of: mermaid, graphml, gexf",
            Arity = ArgumentArity.ExactlyOne
        };

        var callGraphOutputFileOption = new Option<string?>("--callgraph-out")
        {
            Description = "The call graph output file location and name. Defaults to --o with the format extension.",
            Arity = ArgumentArity.ExactlyOne
        };

        var patternsFileOption = new Option<string?>("--patterns")
        {
            Description = "Optional JSON file containing source, sink, and passthrough patterns for data-flow slicing. Built-in .NET web/http/rpc/cli defaults are always included.",
            Arity = ArgumentArity.ExactlyOne
        };

        var patternPacksOption = new Option<string?>("--pattern-packs")
        {
            Description = "Comma-separated built-in data-flow pattern packs to enable: all, aspnet, data, filesystem, serialization, cloud, rpc, auth, crypto. Defaults to all.",
            Arity = ArgumentArity.ExactlyOne
        };

        var dataFlowFormatOption = new Option<string?>("--graph-format")
        {
            Description = "Export the data-flow graph separately in one of: mermaid, graphml, gexf",
            Arity = ArgumentArity.ExactlyOne
        };

        var dataFlowGraphOutputFileOption = new Option<string?>("--graph-out")
        {
            Description = "The data-flow graph output file location and name. Defaults to --o with the format extension.",
            Arity = ArgumentArity.ExactlyOne
        };

        var cryptoFormatOption = new Option<string?>("--format")
        {
            Description = "Crypto output format: dosai, cyclonedx. Defaults to dosai.",
            Arity = ArgumentArity.ExactlyOne
        };

        var cryptoGraphFormatOption = new Option<string?>("--graph-format")
        {
            Description = "Export crypto data-flow graph sidecars in one or more comma-separated formats: mermaid, graphml, gexf.",
            Arity = ArgumentArity.ExactlyOne
        };

        var cryptoGraphOutputFileOption = new Option<string?>("--graph-out")
        {
            Description = "Crypto data-flow graph sidecar output file. Only valid with a single --graph-format value; otherwise sidecars default to --o plus -dataflows and the format extension.",
            Arity = ArgumentArity.ExactlyOne
        };

        var printSourcesSinksOption = new Option<bool>("--print-sources-sinks")
        {
            Description = "Print auto-detected data-flow sources and sinks to stdout for pattern diagnostics."
        };

        var printDataFlowsOption = new Option<bool>("--print")
        {
            Description = "Print stack-trace-style data-flow paths to stdout."
        };

        var inputFileOption = new Option<string?>("--input")
        {
            Description = "Input Dosai JSON file",
            Arity = ArgumentArity.ExactlyOne,
            Required = true
        };

        var oldInputFileOption = new Option<string?>("--old")
        {
            Description = "Previous data-flow JSON file",
            Arity = ArgumentArity.ExactlyOne,
            Required = true
        };

        var newInputFileOption = new Option<string?>("--new")
        {
            Description = "New data-flow JSON file",
            Arity = ArgumentArity.ExactlyOne,
            Required = true
        };

        var queryOption = new Option<string>("--query")
        {
            Description = "Query expression, for example: slices[sinkCategory=sql], nodes[isSource=true], weaknesses[confidence=High]",
            Arity = ArgumentArity.ExactlyOne,
            Required = true
        };

        rootCommand.Options.Add(pathOption);
        rootCommand.Options.Add(outputFileOption);
        rootCommand.Options.Add(callGraphFormatOption);
        rootCommand.Options.Add(callGraphOutputFileOption);

        var methodsCommand = new Command("methods", "Retrieve details about the methods")
        {
            pathOption,
            outputFileOption,
            callGraphFormatOption,
            callGraphOutputFileOption
        };

        var dataFlowsCommand = new Command("dataflows", "Create data-flow slices from source patterns to sink patterns")
        {
            pathOption,
            outputFileOption,
            patternsFileOption,
            patternPacksOption,
            dataFlowFormatOption,
            dataFlowGraphOutputFileOption,
            printDataFlowsOption,
            printSourcesSinksOption
        };

        var cryptoCommand = new Command("crypto", "Detect cryptographic assets, operations, materials, misuse, and CBOM evidence")
        {
            pathOption,
            outputFileOption,
            cryptoFormatOption,
            cryptoGraphFormatOption,
            cryptoGraphOutputFileOption
        };

        var agentContextCommand = new Command("agent-context", "Generate compact AI-agent context from data-flow analysis")
        {
            pathOption,
            outputFileOption,
            patternsFileOption,
            patternPacksOption
        };

        var reportCommand = new Command("report", "Generate a Markdown report from data-flow JSON")
        {
            inputFileOption,
            outputFileOption
        };

        var diffCommand = new Command("diff", "Diff two data-flow JSON files")
        {
            oldInputFileOption,
            newInputFileOption,
            outputFileOption
        };

        var queryCommand = new Command("query", "Filter Dosai JSON with a compact query expression")
        {
            inputFileOption,
            outputFileOption,
            queryOption
        };

        var mcpCommand = new Command("mcp", "Run an MCP-style JSON-RPC server over stdin/stdout")
        {
            pathOption,
            patternsFileOption,
            patternPacksOption
        };

        rootCommand.Subcommands.Add(methodsCommand);
        rootCommand.Subcommands.Add(dataFlowsCommand);
        rootCommand.Subcommands.Add(cryptoCommand);
        rootCommand.Subcommands.Add(agentContextCommand);
        rootCommand.Subcommands.Add(reportCommand);
        rootCommand.Subcommands.Add(diffCommand);
        rootCommand.Subcommands.Add(queryCommand);
        rootCommand.Subcommands.Add(mcpCommand);

        methodsCommand.SetAction(parseResult =>
            {
                var path = parseResult.GetValue(pathOption);
                var outputFile = parseResult.GetValue(outputFileOption);
                var callGraphFormat = parseResult.GetValue(callGraphFormatOption);
                var callGraphOutputFile = parseResult.GetValue(callGraphOutputFileOption);
                string result;

                if (Path.GetExtension(path)!.Equals(".nupkg", StringComparison.OrdinalIgnoreCase))
                {
                    result = Dosai.GetMethodsFromNupkg(path!);
                }
                else
                {
                    result = Dosai.GetMethods(path!);
                }

                File.WriteAllText(outputFile!, result);

                if (!string.IsNullOrWhiteSpace(callGraphFormat))
                {
                    if (!CallGraphExporter.TryParseFormat(callGraphFormat, out var format))
                    {
                        Console.Error.WriteLine($"Unsupported call graph format: {callGraphFormat}. Supported formats: mermaid, graphml, gexf.");
                        return 1;
                    }

                    var options = new JsonSerializerOptions
                    {
                        Converters = { new JsonStringEnumConverter() }
                    };
                    var methodsSlice = JsonSerializer.Deserialize<MethodsSlice>(result, options);
                    if (methodsSlice?.CallGraph is null)
                    {
                        Console.Error.WriteLine("Call graph was not generated.");
                        return 1;
                    }

                    callGraphOutputFile ??= Path.ChangeExtension(outputFile!, CallGraphExporter.GetDefaultExtension(format));
                    File.WriteAllText(callGraphOutputFile, CallGraphExporter.Export(methodsSlice.CallGraph, format));
                }

                return 0;
            });

        dataFlowsCommand.SetAction(parseResult =>
        {
            var path = parseResult.GetValue(pathOption);
            var outputFile = parseResult.GetValue(outputFileOption);
            var patternsFile = parseResult.GetValue(patternsFileOption);
            var patternPacks = parseResult.GetValue(patternPacksOption);
            var graphFormat = parseResult.GetValue(dataFlowFormatOption);
            var graphOutputFile = parseResult.GetValue(dataFlowGraphOutputFileOption);
            var printDataFlows = parseResult.GetValue(printDataFlowsOption);
            var printSourcesSinks = parseResult.GetValue(printSourcesSinksOption);

            var result = DataFlowAnalyzer.GetDataFlows(path!, patternsFile, patternPacks);
            File.WriteAllText(outputFile!, result);

            var options = new JsonSerializerOptions
            {
                Converters = { new JsonStringEnumConverter() }
            };
            var dataFlowResult = JsonSerializer.Deserialize<DataFlowResult>(result, options);

            if (printDataFlows && dataFlowResult is not null)
            {
                PrintDataFlowTree(dataFlowResult, outputFile!);
            }

            if (printSourcesSinks && dataFlowResult is not null)
            {
                PrintSourcesAndSinks(dataFlowResult);
            }

            if (!string.IsNullOrWhiteSpace(graphFormat))
            {
                if (!DataFlowExporter.TryParseFormat(graphFormat, out var format))
                {
                    Console.Error.WriteLine($"Unsupported data-flow graph format: {graphFormat}. Supported formats: mermaid, graphml, gexf.");
                    return 1;
                }

                if (dataFlowResult is null)
                {
                    Console.Error.WriteLine("Data-flow result was not generated.");
                    return 1;
                }

                graphOutputFile ??= Path.ChangeExtension(outputFile!, DataFlowExporter.GetDefaultExtension(format));
                File.WriteAllText(graphOutputFile, DataFlowExporter.Export(dataFlowResult, format));
            }

            return 0;
        });

        cryptoCommand.SetAction(parseResult =>
        {
            var path = parseResult.GetValue(pathOption)!;
            var outputFile = parseResult.GetValue(outputFileOption)!;
            var format = parseResult.GetValue(cryptoFormatOption);
            var graphFormat = parseResult.GetValue(cryptoGraphFormatOption);
            var graphOutputFile = parseResult.GetValue(cryptoGraphOutputFileOption);
            try
            {
                var result = CryptoAnalyzer.Analyze(path);
                File.WriteAllText(outputFile, CryptoAnalyzer.Export(result, format));
                if (!string.IsNullOrWhiteSpace(graphFormat))
                {
                    var graphExportResult = WriteCryptoDataFlowGraphSidecars(result, graphFormat, outputFile, graphOutputFile);
                    if (graphExportResult != 0) return graphExportResult;
                }
                return 0;
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        });

        agentContextCommand.SetAction(parseResult =>
        {
            var path = parseResult.GetValue(pathOption)!;
            var outputFile = parseResult.GetValue(outputFileOption)!;
            var patternsFile = parseResult.GetValue(patternsFileOption);
            var patternPacks = parseResult.GetValue(patternPacksOption);
            var result = DataFlowAnalyzer.Analyze(path, patternsFile, patternPacks);
            var context = TransparencyBuilder.BuildAgentContext(result, path);
            File.WriteAllText(outputFile, JsonSerializer.Serialize(context, JsonOptions()));
            return 0;
        });

        reportCommand.SetAction(parseResult =>
        {
            var input = parseResult.GetValue(inputFileOption)!;
            var outputFile = parseResult.GetValue(outputFileOption)!;
            var result = JsonSerializer.Deserialize<DataFlowResult>(File.ReadAllText(input), JsonOptions());
            if (result is null)
            {
                Console.Error.WriteLine("Could not read data-flow result.");
                return 1;
            }
            File.WriteAllText(outputFile, TransparencyBuilder.ToMarkdownReport(result));
            return 0;
        });

        diffCommand.SetAction(parseResult =>
        {
            var oldInput = parseResult.GetValue(oldInputFileOption)!;
            var newInput = parseResult.GetValue(newInputFileOption)!;
            var outputFile = parseResult.GetValue(outputFileOption)!;
            var oldResult = JsonSerializer.Deserialize<DataFlowResult>(File.ReadAllText(oldInput), JsonOptions());
            var newResult = JsonSerializer.Deserialize<DataFlowResult>(File.ReadAllText(newInput), JsonOptions());
            if (oldResult is null || newResult is null)
            {
                Console.Error.WriteLine("Could not read one or both data-flow results.");
                return 1;
            }
            File.WriteAllText(outputFile, TransparencyBuilder.DiffJson(oldResult, newResult));
            return 0;
        });

        queryCommand.SetAction(parseResult =>
        {
            var input = parseResult.GetValue(inputFileOption)!;
            var outputFile = parseResult.GetValue(outputFileOption)!;
            var query = parseResult.GetValue(queryOption)!;
            File.WriteAllText(outputFile, DosaiQueryEngine.QueryJson(File.ReadAllText(input), query));
            return 0;
        });

        mcpCommand.SetAction(parseResult =>
        {
            var path = parseResult.GetValue(pathOption);
            var patternsFile = parseResult.GetValue(patternsFileOption);
            var patternPacks = parseResult.GetValue(patternPacksOption);
            return McpServer.Run(path, patternsFile, patternPacks);
        });

        return rootCommand.Parse(args).Invoke();
    }

    private static JsonSerializerOptions JsonOptions() => new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static void PrintDataFlowTree(DataFlowResult result, string outputFile)
    {
        WriteDataFlowTreeReport(Console.Out, result, outputFile);
    }

    public static string BuildDataFlowTreeReport(DataFlowResult result, string outputFile)
    {
        using var writer = new StringWriter();
        WriteDataFlowTreeReport(writer, result, outputFile);
        return writer.ToString();
    }

    public static void WriteDataFlowTreeReport(TextWriter writer, DataFlowResult result, string outputFile)
    {
        var nodesById = result.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var edgesById = result.Edges.ToDictionary(edge => edge.Id, StringComparer.Ordinal);
        var weaknessesBySliceId = result.WeaknessCandidates
            .Where(weakness => !string.IsNullOrWhiteSpace(weakness.SliceId))
            .GroupBy(weakness => weakness.SliceId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        writer.WriteLine("Dosai Data-flow Analysis");
        writer.WriteLine($"Summary: {result.Statistics.SliceCount} {Pluralize(result.Statistics.SliceCount, "flow")}, {result.Statistics.SourceCount} {Pluralize(result.Statistics.SourceCount, "source")}, {result.Statistics.SinkCount} {Pluralize(result.Statistics.SinkCount, "sink")}, {result.Statistics.FilesAnalyzed} {Pluralize(result.Statistics.FilesAnalyzed, "file")} analyzed, {result.WeaknessCandidates.Count} {Pluralize(result.WeaknessCandidates.Count, "weakness candidate")}");
        writer.WriteLine($"Output: {outputFile}");

        if (result.Slices.Count == 0)
        {
            writer.WriteLine("No data-flow slices found.");
            return;
        }

        writer.WriteLine("Data-flow stack traces:");
        for (var index = 0; index < result.Slices.Count; index++)
        {
            var slice = result.Slices[index];
            nodesById.TryGetValue(slice.SourceId, out var source);
            nodesById.TryGetValue(slice.SinkId, out var sink);
            weaknessesBySliceId.TryGetValue(slice.Id, out var weakness);

            var isLastSlice = index == result.Slices.Count - 1;
            var sliceConnector = isLastSlice ? "└─" : "├─";
            var childPrefix = isLastSlice ? "   " : "│  ";
            var flowTitle = $"DataFlow {slice.Id}: {slice.SourceCategory ?? source?.Category ?? "source"} → {slice.SinkCategory ?? sink?.Category ?? "sink"} ({slice.Confidence})";
            writer.WriteLine($"{sliceConnector} {flowTitle}");
            writer.WriteLine($"{childPrefix}Summary: {weakness?.Summary ?? slice.Summary ?? BuildFlowSummary(source, sink, slice)}");
            if (!string.IsNullOrWhiteSpace(slice.SinkArgument))
            {
                var argumentLabel = slice.SinkArgumentIndex.HasValue ? $"Argument[{slice.SinkArgumentIndex}]" : "Argument";
                writer.WriteLine($"{childPrefix}{argumentLabel}: {TrimConsoleText(slice.SinkArgument)}");
            }
            if (slice.Purls.Count > 0)
            {
                writer.WriteLine($"{childPrefix}PURLs: {string.Join(", ", slice.Purls)}");
            }
            WriteDataPathLines(writer, childPrefix, slice, nodesById, edgesById);
        }
    }

    private static int WriteCryptoDataFlowGraphSidecars(CryptoAnalysisResult result, string graphFormats, string outputFile, string? graphOutputFile)
    {
        if (result.CryptoDataFlows is null)
        {
            Console.Error.WriteLine("Crypto data-flow result was not generated.");
            return 1;
        }

        var requestedFormats = graphFormats
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (requestedFormats.Count == 0)
        {
            Console.Error.WriteLine("No crypto data-flow graph format was provided.");
            return 1;
        }

        if (requestedFormats.Count > 1 && !string.IsNullOrWhiteSpace(graphOutputFile))
        {
            Console.Error.WriteLine("--graph-out can only be used with a single --graph-format value for crypto graph sidecars.");
            return 1;
        }

        foreach (var requestedFormat in requestedFormats)
        {
            if (!DataFlowExporter.TryParseFormat(requestedFormat, out var format))
            {
                Console.Error.WriteLine($"Unsupported crypto data-flow graph format: {requestedFormat}. Supported formats: mermaid, graphml, gexf.");
                return 1;
            }

            var sidecarPath = graphOutputFile ?? BuildCryptoDataFlowSidecarPath(outputFile, format);
            File.WriteAllText(sidecarPath, DataFlowExporter.Export(result.CryptoDataFlows, format));
        }

        return 0;
    }

    private static string BuildCryptoDataFlowSidecarPath(string outputFile, DataFlowExportFormat format)
    {
        var directory = Path.GetDirectoryName(outputFile);
        var fileName = Path.GetFileNameWithoutExtension(outputFile);
        if (string.IsNullOrWhiteSpace(fileName)) fileName = "dosai-crypto";
        var sidecarName = $"{fileName}-dataflows{DataFlowExporter.GetDefaultExtension(format)}";
        return string.IsNullOrWhiteSpace(directory) ? sidecarName : Path.Combine(directory, sidecarName);
    }

    private static string BuildFlowSummary(DataFlowNode? source, DataFlowNode? sink, DataFlowSlice slice) =>
        $"{slice.SourceCategory ?? source?.Category ?? "source"} data reaches {slice.SinkCategory ?? sink?.Category ?? "sink"} sink {sink?.Name ?? slice.SinkId}.";

    private static void WriteDataPathLines(TextWriter writer, string childPrefix, DataFlowSlice slice, IReadOnlyDictionary<string, DataFlowNode> nodesById, IReadOnlyDictionary<string, DataFlowEdge> edgesById)
    {
        writer.WriteLine($"{childPrefix}Stack ({slice.NodeIds.Count} {Pluralize(slice.NodeIds.Count, "frame")}, {slice.EdgeIds.Count} {Pluralize(slice.EdgeIds.Count, "transition")}):");
        var wroteEntry = false;

        foreach (var entry in BuildDataPathEntries(slice, nodesById, edgesById))
        {
            wroteEntry = true;
            foreach (var entryLine in entry.Split(Environment.NewLine))
            {
                writer.WriteLine($"{childPrefix}  {entryLine}");
            }
        }

        if (!wroteEntry)
        {
            writer.WriteLine($"{childPrefix}  <no node or edge details available for this slice>");
        }
    }

    private static IEnumerable<string> BuildDataPathEntries(DataFlowSlice slice, IReadOnlyDictionary<string, DataFlowNode> nodesById, IReadOnlyDictionary<string, DataFlowEdge> edgesById)
    {
        var sliceEdges = slice.EdgeIds
            .Select(edgeId => edgesById.TryGetValue(edgeId, out var edge) ? edge : null)
            .Where(edge => edge is not null)
            .Cast<DataFlowEdge>()
            .ToList();
        var edgesByPair = new Dictionary<(string SourceId, string TargetId), List<DataFlowEdge>>();
        foreach (var edge in sliceEdges)
        {
            var key = (edge.SourceId, edge.TargetId);
            if (!edgesByPair.TryGetValue(key, out var edgesForPair))
            {
                edgesForPair = [];
                edgesByPair[key] = edgesForPair;
            }
            edgesForPair.Add(edge);
        }
        var emittedEdges = new HashSet<string>(StringComparer.Ordinal);

        for (var nodeIndex = 0; nodeIndex < slice.NodeIds.Count; nodeIndex++)
        {
            var nodeId = slice.NodeIds[nodeIndex];
            yield return FormatNodeFrame(nodesById.TryGetValue(nodeId, out var node) ? node : null, nodeId);

            if (nodeIndex + 1 >= slice.NodeIds.Count)
            {
                continue;
            }

            var nextNodeId = slice.NodeIds[nodeIndex + 1];
            if (!edgesByPair.TryGetValue((nodeId, nextNodeId), out var pathEdges))
            {
                continue;
            }

            foreach (var edge in pathEdges)
            {
                emittedEdges.Add(edge.Id);
                yield return FormatEdgeTransition(edge);
            }
        }

        foreach (var edge in sliceEdges.Where(edge => !emittedEdges.Contains(edge.Id)).OrderBy(edge => edge.Id, StringComparer.Ordinal))
        {
            yield return FormatEdgeTransition(edge);
        }
    }

    private static string FormatNodeFrame(DataFlowNode? node, string fallbackId)
    {
        if (node is null)
        {
            return $"at <missing node> [{fallbackId}]";
        }

        var category = string.IsNullOrWhiteSpace(node.Category) ? string.Empty : $"/{node.Category}";
        var location = string.IsNullOrWhiteSpace(node.FileName) ? "<unknown>" : $"{node.FileName}:{node.LineNumber}:{node.ColumnNumber}";
        var symbol = string.IsNullOrWhiteSpace(node.Symbol) ? node.Name : node.Symbol;
        var purl = string.IsNullOrWhiteSpace(node.Purl) ? string.Empty : $" [{node.Purl}]";
        var lines = new List<string>
        {
            $"at {node.Kind}{category} {node.Name} [{node.Id}] in {location}{purl}",
            $"   code: {TrimConsoleText(node.Code ?? node.Name)}"
        };
        if (!string.IsNullOrWhiteSpace(symbol) && !string.Equals(symbol, node.Name, StringComparison.Ordinal))
        {
            lines.Add($"   symbol: {TrimConsoleText(symbol, 120)}");
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatEdgeTransition(DataFlowEdge edge)
    {
        var label = string.IsNullOrWhiteSpace(edge.Label) ? string.Empty : $" label={TrimConsoleText(edge.Label, 64)}";
        var location = string.IsNullOrWhiteSpace(edge.FileName) ? "<unknown>" : $"{edge.FileName}:{edge.LineNumber}:{edge.ColumnNumber}";
        var sourcePurl = string.IsNullOrWhiteSpace(edge.SourcePurl) ? string.Empty : $" sourcePurl={edge.SourcePurl}";
        var targetPurl = string.IsNullOrWhiteSpace(edge.TargetPurl) ? string.Empty : $" targetPurl={edge.TargetPurl}";
        return $"via {edge.Kind} [{edge.Id}] from {edge.SourceId} to {edge.TargetId} in {location}{label}{sourcePurl}{targetPurl}";
    }

    private static string Pluralize(int count, string singular) => count == 1 ? singular : singular + "s";

    private static string TrimConsoleText(string value, int maxLength = 160)
    {
        value = value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
        return value.Length <= maxLength ? value : value[..maxLength] + "…";
    }

    private static void PrintSourcesAndSinks(DataFlowResult result)
    {
        Console.WriteLine($"Data-flow sources: {result.Statistics.SourceCount}");
        foreach (var node in result.Nodes.Where(node => node.IsSource).OrderBy(node => node.FileName, StringComparer.Ordinal).ThenBy(node => node.LineNumber))
        {
            Console.WriteLine($"SOURCE\t{node.Category}\t{node.FileName}:{node.LineNumber}:{node.ColumnNumber}\t{node.Name}\t{node.Purl ?? string.Empty}\t{node.Code}");
        }

        Console.WriteLine($"Data-flow sinks: {result.Statistics.SinkCount}");
        foreach (var node in result.Nodes.Where(node => node.IsSink).OrderBy(node => node.FileName, StringComparer.Ordinal).ThenBy(node => node.LineNumber))
        {
            Console.WriteLine($"SINK\t{node.Category}\t{node.FileName}:{node.LineNumber}:{node.ColumnNumber}\t{node.Name}\t{node.Purl ?? string.Empty}\t{node.Code}");
        }
    }
}