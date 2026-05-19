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
            Description = "Comma-separated built-in data-flow pattern packs to enable: all, aspnet, data, filesystem, serialization, cloud, rpc, auth. Defaults to all.",
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

        var printSourcesSinksOption = new Option<bool>("--print-sources-sinks")
        {
            Description = "Print auto-detected data-flow sources and sinks to stdout for pattern diagnostics."
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
            printSourcesSinksOption
        };

        var cryptoCommand = new Command("crypto", "Detect cryptographic assets, operations, materials, misuse, and CBOM evidence")
        {
            pathOption,
            outputFileOption,
            cryptoFormatOption
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
            var printSourcesSinks = parseResult.GetValue(printSourcesSinksOption);

            var result = DataFlowAnalyzer.GetDataFlows(path!, patternsFile, patternPacks);
            File.WriteAllText(outputFile!, result);

            var options = new JsonSerializerOptions
            {
                Converters = { new JsonStringEnumConverter() }
            };
            var dataFlowResult = JsonSerializer.Deserialize<DataFlowResult>(result, options);

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
            try
            {
                File.WriteAllText(outputFile, CryptoAnalyzer.GetCryptoAnalysis(path, format));
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