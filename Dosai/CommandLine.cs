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
            Required = true
        };

        var outputFileOption = new Option<string?>("--o")
        {
            Description = $"The output file location and name",
            DefaultValueFactory = parseResult => DefaultOutputFile
        };

        var callGraphFormatOption = new Option<string?>("--callgraph-format")
        {
            Description = "Export call graph separately in one of: mermaid, graphml, gexf"
        };

        var callGraphOutputFileOption = new Option<string?>("--callgraph-out")
        {
            Description = "The call graph output file location and name. Defaults to --o with the format extension."
        };

        var patternsFileOption = new Option<string?>("--patterns")
        {
            Description = "Optional JSON file containing source, sink, and passthrough patterns for data-flow slicing. Built-in .NET web/http/rpc/cli defaults are always included."
        };

        var dataFlowFormatOption = new Option<string?>("--graph-format")
        {
            Description = "Export the data-flow graph separately in one of: mermaid, graphml, gexf"
        };

        var dataFlowGraphOutputFileOption = new Option<string?>("--graph-out")
        {
            Description = "The data-flow graph output file location and name. Defaults to --o with the format extension."
        };

        var printSourcesSinksOption = new Option<bool>("--print-sources-sinks")
        {
            Description = "Print auto-detected data-flow sources and sinks to stdout for pattern diagnostics."
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
            dataFlowFormatOption,
            dataFlowGraphOutputFileOption,
            printSourcesSinksOption
        };

        rootCommand.Subcommands.Add(methodsCommand);
        rootCommand.Subcommands.Add(dataFlowsCommand);

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
                    File.WriteAllText(callGraphOutputFile!, CallGraphExporter.Export(methodsSlice.CallGraph, format));
                }

                return 0;
            });

        dataFlowsCommand.SetAction(parseResult =>
        {
            var path = parseResult.GetValue(pathOption);
            var outputFile = parseResult.GetValue(outputFileOption);
            var patternsFile = parseResult.GetValue(patternsFileOption);
            var graphFormat = parseResult.GetValue(dataFlowFormatOption);
            var graphOutputFile = parseResult.GetValue(dataFlowGraphOutputFileOption);
            var printSourcesSinks = parseResult.GetValue(printSourcesSinksOption);

            var result = DataFlowAnalyzer.GetDataFlows(path!, patternsFile);
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
                File.WriteAllText(graphOutputFile!, DataFlowExporter.Export(dataFlowResult, format));
            }

            return 0;
        });

        return rootCommand.Parse(args).Invoke();
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