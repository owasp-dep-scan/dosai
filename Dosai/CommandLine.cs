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

        var debugOption = new Option<bool>("--debug")
        {
            Description = "Report progress on stderr: phase starts/ends with elapsed time and memory, discovery and graph size counts. Also enabled with DOSAI_DEBUG=1. Logs paths, counts, and names only - never source text."
        };

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

        var excludeOption = new Option<string[]>("--exclude")
        {
            Description = "Glob of files or directories to skip, relative to --path (repeatable), e.g. 'BuildOutput/**', '**/*.Designer.cs', 'node_modules'",
            Arity = ArgumentArity.OneOrMore,
            AllowMultipleArgumentsPerToken = true
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
            Description = $"Comma-separated built-in data-flow pattern packs to enable: all, {string.Join(", ", DataFlowAnalyzer.DefaultPatternPackNames)}. Defaults to all.",
            Arity = ArgumentArity.ExactlyOne
        };

        var suppressionsFileOption = new Option<string?>("--suppress")
        {
            Description = "Optional suppressions JSON file (file+line, sliceKey, weaknessId, or category entries, each with an optional expiry). Matching, non-expired suppressions remove slices and weakness candidates.",
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
        rootCommand.Options.Add(debugOption);

        var classifyDataOption = new Option<bool>("--classify-data")
        {
            Description = "Classify service request/response data (pii/credential/financial/health) from DTO members (default: on)",
            DefaultValueFactory = _ => true
        };
        var includePromptTextOption = new Option<bool>("--include-prompt-text")
        {
            Description = "Emit full system prompt text in AiComponents (default: SHA-256 and first 200 characters only)"
        };
        var noClassifyDataOption = new Option<bool>("--no-classify-data")
        {
            Description = "Disable service request/response data classification (turns off --classify-data)"
        };
        var maxConventionalRoutesOption = new Option<int>("--max-conventional-routes")
        {
            Description = "Cap for conventional routing pattern expansion (MapControllerRoute cross-products) (default: 500)",
            DefaultValueFactory = _ => 500
        };
        var mcpAllowlistOption = new Option<string?>("--mcp-allowlist")
        {
            Description = "Optional file of policy-approved MCP stdio transport commands (one per line); commands listed here are not flagged by the MCP transport security assessment",
            Arity = ArgumentArity.ExactlyOne
        };
        var restoreOption = new Option<bool>("--restore")
        {
            Description = "Run 'dotnet restore' on the discovered solution or projects before analysis so package assemblies can be resolved from the NuGet cache without build output (opt-in: restore evaluates MSBuild from the target repository)"
        };
        var buildOption = new Option<bool>("--build")
        {
            Description = "Run 'dotnet build' before analysis, implying restore (opt-in: building untrusted code executes MSBuild targets and source generators)"
        };

        var methodsCommand = new Command("methods", "Retrieve details about the methods")
        {
            pathOption,
            excludeOption,
            outputFileOption,
            callGraphFormatOption,
            callGraphOutputFileOption,
            classifyDataOption,
            noClassifyDataOption,
            maxConventionalRoutesOption,
            includePromptTextOption,
            mcpAllowlistOption,
            restoreOption,
            buildOption,
            debugOption
        };

        var dataFlowsCommand = new Command("dataflows", "Create data-flow slices from source patterns to sink patterns")
        {
            pathOption,
            excludeOption,
            outputFileOption,
            patternsFileOption,
            patternPacksOption,
            dataFlowFormatOption,
            dataFlowGraphOutputFileOption,
            printDataFlowsOption,
            printSourcesSinksOption,
            suppressionsFileOption,
            restoreOption,
            buildOption,
            debugOption
        };

        var cryptoCommand = new Command("crypto", "Detect cryptographic assets, operations, materials, misuse, and CBOM evidence")
        {
            pathOption,
            excludeOption,
            outputFileOption,
            cryptoFormatOption,
            cryptoGraphFormatOption,
            cryptoGraphOutputFileOption,
            restoreOption,
            buildOption,
            debugOption
        };

        var agentContextCommand = new Command("agent-context", "Generate compact AI-agent context from data-flow analysis")
        {
            pathOption,
            excludeOption,
            outputFileOption,
            patternsFileOption,
            patternPacksOption,
            suppressionsFileOption,
            restoreOption,
            buildOption,
            debugOption
        };

        var reportCommand = new Command("report", "Generate a Markdown report from data-flow JSON")
        {
            inputFileOption,
            outputFileOption,
            debugOption
        };

        var diffCommand = new Command("diff", "Diff two data-flow JSON files")
        {
            oldInputFileOption,
            newInputFileOption,
            outputFileOption,
            debugOption
        };

        var queryCommand = new Command("query", "Filter Dosai JSON with a compact query expression")
        {
            inputFileOption,
            outputFileOption,
            queryOption,
            debugOption
        };

        var mcpRootOption = new Option<string?>("--mcp-root")
        {
            Description = "Restrict the MCP server to paths under this directory (recommended when the server is exposed to external MCP clients)"
        };
        var mcpCommand = new Command("mcp", "Run an MCP-style JSON-RPC server over stdin/stdout")
        {
            pathOption,
            patternsFileOption,
            patternPacksOption,
            mcpRootOption,
            debugOption
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
            Guard("methods", () => parseResult.GetValue(outputFileOption), () =>
            {
                var path = parseResult.GetValue(pathOption);
                var excludePatterns = parseResult.GetValue(excludeOption);
                using var exclusions = PathExclusions.Apply(path!, excludePatterns);
                var outputFile = parseResult.GetValue(outputFileOption);
                var callGraphFormat = parseResult.GetValue(callGraphFormatOption);
                var callGraphOutputFile = parseResult.GetValue(callGraphOutputFileOption);
                var classifyData = parseResult.GetValue(classifyDataOption) && !parseResult.GetValue(noClassifyDataOption);
                var maxConventionalRoutes = parseResult.GetValue(maxConventionalRoutesOption);
                var includePromptText = parseResult.GetValue(includePromptTextOption);
                var mcpAllowlist = parseResult.GetValue(mcpAllowlistOption);
                var buildPreparation = ParseBuildPreparation(parseResult.GetValue(restoreOption), parseResult.GetValue(buildOption));
                LogScanInput(path!, excludePatterns, outputFile!);
                using var commandPhase = DebugLog.Phase("methods");

                // Stream the JSON straight to the output file and keep the built slice around so the call-graph
                // exporter can reuse it. This avoids materialising the full JSON as a single string (which drove
                // peak RSS into the multi-GB range and overflowed the string allocator on large assembly trees)
                // and avoids a serialize-then-deserialize round trip for the call-graph export.
                MethodsSlice methodsSlice;
                if (Path.GetExtension(path)!.Equals(".nupkg", StringComparison.OrdinalIgnoreCase))
                {
                    methodsSlice = Dosai.WriteMethodsFromNupkg(path!, outputFile!);
                }
                else
                {
                    methodsSlice = Dosai.WriteMethods(path!, outputFile!, new Frameworks.FrameworkAnalysisOptions { ClassifyData = classifyData, MaxConventionalRoutes = maxConventionalRoutes, IncludePromptText = includePromptText, McpAllowlist = LoadMcpAllowlist(mcpAllowlist) }, buildPreparation);
                }

                if (!string.IsNullOrWhiteSpace(callGraphFormat))
                {
                    if (!CallGraphExporter.TryParseFormat(callGraphFormat, out var format))
                    {
                        Console.Error.WriteLine($"Unsupported call graph format: {callGraphFormat}. Supported formats: mermaid, graphml, gexf.");
                        return 1;
                    }

                    if (methodsSlice.CallGraph is null)
                    {
                        Console.Error.WriteLine("Call graph was not generated.");
                        return 1;
                    }

                    // Node reachability facts and fan-in/out ride along as graph attributes.
                    var reachabilityByNode = methodsSlice.Reachability?.ToDictionary(facts => facts.NodeId, StringComparer.Ordinal);
                    callGraphOutputFile ??= Path.ChangeExtension(outputFile!, CallGraphExporter.GetDefaultExtension(format));
                    File.WriteAllText(callGraphOutputFile, CallGraphExporter.Export(methodsSlice.CallGraph, format, reachabilityByNode));
                    LogWrittenBytes("call graph export", callGraphOutputFile);
                }

                return 0;
            }));

        dataFlowsCommand.SetAction(parseResult =>
        Guard("dataFlows", () => parseResult.GetValue(outputFileOption), () =>
        {
            var path = parseResult.GetValue(pathOption);
            var excludePatterns = parseResult.GetValue(excludeOption);
            using var exclusions = PathExclusions.Apply(path!, excludePatterns);
            var outputFile = parseResult.GetValue(outputFileOption);
            var patternsFile = parseResult.GetValue(patternsFileOption);
            var patternPacks = parseResult.GetValue(patternPacksOption);
            var graphFormat = parseResult.GetValue(dataFlowFormatOption);
            var graphOutputFile = parseResult.GetValue(dataFlowGraphOutputFileOption);
            var printDataFlows = parseResult.GetValue(printDataFlowsOption);
            var printSourcesSinks = parseResult.GetValue(printSourcesSinksOption);
            var suppressionsFile = parseResult.GetValue(suppressionsFileOption);
            var buildPreparation = ParseBuildPreparation(parseResult.GetValue(restoreOption), parseResult.GetValue(buildOption));
            LogScanInput(path!, excludePatterns, outputFile!);
            using var commandPhase = DebugLog.Phase("dataflows");

            // Stream the JSON straight to the output file and keep the result around for printing and graph
            // export. This avoids materialising the full JSON as a single string and the serialize-then-
            // deserialize round trip, both of which drive peak memory on large trees.
            var dataFlowResult = DataFlowAnalyzer.WriteDataFlows(path!, outputFile!, patternsFile, patternPacks, suppressionsFile, buildPreparation);

            if (printDataFlows)
            {
                PrintDataFlowTree(dataFlowResult, outputFile!);
            }

            if (printSourcesSinks)
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

                graphOutputFile ??= Path.ChangeExtension(outputFile!, DataFlowExporter.GetDefaultExtension(format));
                File.WriteAllText(graphOutputFile, DataFlowExporter.Export(dataFlowResult, format));
                LogWrittenBytes("data-flow graph export", graphOutputFile);
            }

            return 0;
        }));

        cryptoCommand.SetAction(parseResult =>
        Guard("crypto", () => parseResult.GetValue(outputFileOption), () =>
        {
            var path = parseResult.GetValue(pathOption)!;
            var excludePatterns = parseResult.GetValue(excludeOption);
            using var exclusions = PathExclusions.Apply(path, excludePatterns);
            var outputFile = parseResult.GetValue(outputFileOption)!;
            var format = parseResult.GetValue(cryptoFormatOption);
            var graphFormat = parseResult.GetValue(cryptoGraphFormatOption);
            var graphOutputFile = parseResult.GetValue(cryptoGraphOutputFileOption);
            var buildPreparation = ParseBuildPreparation(parseResult.GetValue(restoreOption), parseResult.GetValue(buildOption));
            LogScanInput(path, excludePatterns, outputFile);
            using var commandPhase = DebugLog.Phase("crypto");
            try
            {
                var result = CryptoAnalyzer.Analyze(path, buildPreparation);
                File.WriteAllText(outputFile, CryptoAnalyzer.Export(result, format));
                LogWrittenBytes("crypto export", outputFile);
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
        }));

        agentContextCommand.SetAction(parseResult =>
        Guard("agentContext", () => parseResult.GetValue(outputFileOption), () =>
        {
            var path = parseResult.GetValue(pathOption)!;
            var excludePatterns = parseResult.GetValue(excludeOption);
            using var exclusions = PathExclusions.Apply(path, excludePatterns);
            var outputFile = parseResult.GetValue(outputFileOption)!;
            var patternsFile = parseResult.GetValue(patternsFileOption);
            var patternPacks = parseResult.GetValue(patternPacksOption);
            var suppressionsFile = parseResult.GetValue(suppressionsFileOption);
            var buildPreparation = ParseBuildPreparation(parseResult.GetValue(restoreOption), parseResult.GetValue(buildOption));
            LogScanInput(path, excludePatterns, outputFile);
            using var commandPhase = DebugLog.Phase("agent-context");
            var result = DataFlowAnalyzer.Analyze(path, patternsFile, patternPacks, suppressionsFile, buildPreparation);
            // Converge crypto misuse findings into the weakness queue so agent-context carries
            // one CWE-stamped list; crypto analysis is best-effort and never blocks the context.
            try
            {
                var cryptoWeaknesses = CryptoAnalyzer.BuildWeaknessCandidates(CryptoAnalyzer.Analyze(path));
                if (cryptoWeaknesses.Count > 0)
                {
                    result.WeaknessCandidates.AddRange(cryptoWeaknesses);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Console.Error.WriteLine($"Crypto weakness merge skipped: {ex.Message}");
            }

            var context = TransparencyBuilder.BuildAgentContext(result, path);
            File.WriteAllText(outputFile, JsonSerializer.Serialize(context, JsonOptions()));
            LogWrittenBytes("agent-context export", outputFile);
            return 0;
        }));

        reportCommand.SetAction(parseResult =>
        Guard("report", () => parseResult.GetValue(outputFileOption), () =>
        {
            var input = parseResult.GetValue(inputFileOption)!;
            var outputFile = parseResult.GetValue(outputFileOption)!;
            LogFilePaths(("input file", input), ("output file", outputFile));
            using var commandPhase = DebugLog.Phase("report");
            var result = JsonSerializer.Deserialize<DataFlowResult>(File.ReadAllText(input), JsonOptions());
            if (result is null)
            {
                Console.Error.WriteLine("Could not read data-flow result.");
                return 1;
            }
            File.WriteAllText(outputFile, TransparencyBuilder.ToMarkdownReport(result));
            LogWrittenBytes("report export", outputFile);
            return 0;
        }));

        diffCommand.SetAction(parseResult =>
        Guard("diff", () => parseResult.GetValue(outputFileOption), () =>
        {
            var oldInput = parseResult.GetValue(oldInputFileOption)!;
            var newInput = parseResult.GetValue(newInputFileOption)!;
            var outputFile = parseResult.GetValue(outputFileOption)!;
            LogFilePaths(("--old", oldInput), ("--new", newInput), ("output file", outputFile));
            using var commandPhase = DebugLog.Phase("diff");
            var oldResult = JsonSerializer.Deserialize<DataFlowResult>(File.ReadAllText(oldInput), JsonOptions());
            var newResult = JsonSerializer.Deserialize<DataFlowResult>(File.ReadAllText(newInput), JsonOptions());
            if (oldResult is null || newResult is null)
            {
                Console.Error.WriteLine("Could not read one or both data-flow results.");
                return 1;
            }
            File.WriteAllText(outputFile, TransparencyBuilder.DiffJson(oldResult, newResult));
            LogWrittenBytes("diff export", outputFile);
            return 0;
        }));

        queryCommand.SetAction(parseResult =>
        Guard("query", () => parseResult.GetValue(inputFileOption), () =>
        {
            var input = parseResult.GetValue(inputFileOption)!;
            var outputFile = parseResult.GetValue(outputFileOption)!;
            var query = parseResult.GetValue(queryOption)!;
            LogFilePaths(("input file", input), ("output file", outputFile));
            using var commandPhase = DebugLog.Phase("query");
            File.WriteAllText(outputFile, DosaiQueryEngine.QueryJson(File.ReadAllText(input), query));
            LogWrittenBytes("query export", outputFile);
            return 0;
        }));

        mcpCommand.SetAction(parseResult =>
        Guard("mcp", null, () =>
        {
            var path = parseResult.GetValue(pathOption);
            var patternsFile = parseResult.GetValue(patternsFileOption);
            var patternPacks = parseResult.GetValue(patternPacksOption);
            var mcpRoot = parseResult.GetValue(mcpRootOption);
            // No session-long phase: the server idles between requests, and a phase here would
            // make the heartbeat report "still in mcp" every interval for as long as it runs.
            // McpServer opens one phase per request instead.
            LogFilePaths(("default path", path), ("mcp root", mcpRoot));
            return McpServer.Run(path, patternsFile, patternPacks, mcpRoot);
        }));

        // Debug logging is configured once, before any command action runs, so every analyzer
        // (and the dedicated assembly-inspection thread) can write through the static DebugLog
        // without a logger parameter threaded through the pipeline. DOSAI_DEBUG lets callers
        // such as cdxgen opt in without changing arguments.
        var parseResult = rootCommand.Parse(args);
        DebugLog.Configure(parseResult.GetValue(debugOption) || DebugLog.IsTruthyEnvironmentValue(Environment.GetEnvironmentVariable("DOSAI_DEBUG")));
        if (DebugLog.Enabled)
        {
            DebugLog.Log($"dosai {typeof(Dosai).Assembly.GetName().Version?.ToString() ?? "dev"}, {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}, {System.Runtime.InteropServices.RuntimeInformation.OSDescription} {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}, {Environment.ProcessorCount} processor(s)");
        }

        return parseResult.Invoke();
    }

    private static JsonSerializerOptions JsonOptions() => new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Debug-only preamble shared by the scan commands: resolved input path, active --exclude patterns, and output file.</summary>
    private static void LogScanInput(string path, string[]? excludePatterns, string outputFile)
    {
        if (!DebugLog.Enabled)
        {
            return;
        }

        DebugLog.Log($"input path: {Path.GetFullPath(path)}");
        DebugLog.Log(excludePatterns is { Length: > 0 } ? $"--exclude: {string.Join(", ", excludePatterns)}" : "--exclude: <none>");
        DebugLog.Log($"output file: {Path.GetFullPath(outputFile)}");
    }

    /// <summary>Debug-only resolved paths for the commands that read and write files rather than scan a tree.</summary>
    private static void LogFilePaths(params (string Label, string? Path)[] paths)
    {
        if (!DebugLog.Enabled)
        {
            return;
        }

        foreach (var (label, path) in paths)
        {
            if (path is not null)
            {
                DebugLog.Log($"{label}: {Path.GetFullPath(path)}");
            }
        }
    }

    /// <summary>Debug-only size report for a written output file.</summary>
    private static void LogWrittenBytes(string what, string path)
    {
        if (DebugLog.Enabled)
        {
            DebugLog.Log($"{what}: wrote {DebugLog.FormatBytes(new FileInfo(path).Length)} to '{path}'");
        }
    }

    /// <summary>Build wins when both flags are given (build implies restore).</summary>
    private static BuildPreparationMode ParseBuildPreparation(bool restore, bool build)
        => build ? BuildPreparationMode.Build : restore ? BuildPreparationMode.Restore : BuildPreparationMode.None;

    /// <summary>
    ///     Top-level guard around one command action (issue-#56 containment): an unhandled
    ///     exception is written to stderr with a non-zero exit code instead of aborting the
    ///     process and leaving no output at all, and when the output file already holds a
    ///     partial result (the methods and dataflows commands stream JSON while they analyze)
    ///     the user is told where it is instead of being left with nothing.
    /// </summary>
    private static int Guard(string commandName, Func<string?>? outputFile, Func<int> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{commandName} failed: {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace ?? "<no stack trace available>");
            var output = outputFile?.Invoke();
            if (!string.IsNullOrWhiteSpace(output) && File.Exists(output))
            {
                Console.Error.WriteLine($"Note: '{output}' was created before the failure and may hold a partial result.");
            }
            return 1;
        }
    }

    /// <summary>Loads the --mcp-allowlist policy file (one command per line); missing file disables the allowlist.</summary>
    private static IReadOnlySet<string>? LoadMcpAllowlist(string? mcpAllowlistPath)
    {
        if (string.IsNullOrWhiteSpace(mcpAllowlistPath))
        {
            return null;
        }

        try
        {
            var commands = File.ReadAllLines(mcpAllowlistPath)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0 && !line.StartsWith('#'))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return commands.Count > 0 ? commands : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not read MCP allowlist {mcpAllowlistPath}: {ex.Message}");
            return null;
        }
    }

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
        var nodesById = result.Nodes.ToDictionaryFirstWins(node => node.Id, StringComparer.Ordinal);
        var edgesById = result.Edges.ToDictionaryFirstWins(edge => edge.Id, StringComparer.Ordinal);
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