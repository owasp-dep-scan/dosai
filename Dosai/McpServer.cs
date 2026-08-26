using System.Text.Json;
using System.Text.Json.Serialization;

namespace Depscan;

public static class McpServer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public static int Run(string? defaultPath = null, string? patternsPath = null, string? patternPacks = null, string? rootPath = null, TextReader? input = null, TextWriter? output = null)
    {
        input ??= Console.In;
        output ??= Console.Out;

        // Optional confinement: when set, every path the server touches (tool arguments and the
        // configured default path) must resolve under this directory. Analysis output carries
        // source-derived text, so an unrestricted server lets any connected client read any
        // directory on the host through tool results.
        string? confinedRoot = null;
        if (!string.IsNullOrWhiteSpace(rootPath))
        {
            confinedRoot = Path.GetFullPath(rootPath);
            if (!Directory.Exists(confinedRoot))
            {
                WriteError(output, null, -32001, $"--mcp-root directory does not exist: {confinedRoot}");
                return 2;
            }

            if (!string.IsNullOrWhiteSpace(defaultPath) && !IsUnderRoot(defaultPath, confinedRoot))
            {
                WriteError(output, null, -32001, $"--path must fall under --mcp-root when confinement is enabled.");
                return 2;
            }
        }

        string? line;
        while ((line = input.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonRpcRequest? request = null;
            try
            {
                request = JsonSerializer.Deserialize<JsonRpcRequest>(line, JsonOptions);
                if (request is null)
                {
                    WriteError(output, null, -32700, "Invalid JSON-RPC request.");
                    continue;
                }

                var result = Handle(request, defaultPath, patternsPath, patternPacks, confinedRoot);
                WriteResult(output, request.Id, result);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException or FileNotFoundException)
            {
                WriteError(output, request?.Id, -32000, ex.Message);
            }
        }

        return 0;
    }

    /// <summary>True when <paramref name="candidate" /> resolves inside <paramref name="root" /> (symlink-stripped, case-insensitive on macOS/Windows).</summary>
    internal static bool IsUnderRoot(string candidate, string root)
    {
        var full = Path.GetFullPath(candidate);
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return full.Equals(root, comparison) || full.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }

    private static object Handle(JsonRpcRequest request, string? defaultPath, string? patternsPath, string? patternPacks, string? confinedRoot = null)
    {
        return request.Method switch
        {
            "initialize" => new
            {
                protocolVersion = "2024-11-05",
                serverInfo = new { name = "dosai", version = typeof(Dosai).Assembly.GetName().Version?.ToString() ?? "dev" },
                capabilities = new { tools = new { } }
            },
            "tools/list" => new
            {
                tools = new[]
                {
                    Tool("dosai.methods", "Inspect source/assembly methods, call graph, endpoints, and reachability."),
                    Tool("dosai.dataflows", "Run source-to-sink data-flow slicing."),
                    Tool("dosai.crypto", "Detect cryptographic assets, operations, materials, misuse, and CBOM evidence."),
                    Tool("dosai.agent_context", "Generate compact agent context from data-flow analysis."),
                    Tool("dosai.services", "List detected framework services: inbound endpoints (MVC, minimal APIs, gRPC, hubs, functions) and outbound dependencies (inference endpoints, queues, vector stores), with resolved paths, confidence, trust zones, and data classifications."),
                    Tool("dosai.ai_components", "List AI inventory: models (ids and on-disk artifacts with SHA-256), MCP tools with JSON Schemas, prompts (redacted by default), agents, embeddings."),
                    Tool("dosai.query", "Filter Dosai JSON with queries like slices[sinkCategory=sql].")
                }
            },
            "tools/call" => CallTool(request.Params, defaultPath, patternsPath, patternPacks, confinedRoot),
            _ => throw new ArgumentException($"Unsupported MCP/JSON-RPC method: {request.Method}")
        };
    }

    private static object CallTool(JsonElement? parameters, string? defaultPath, string? patternsPath, string? patternPacks, string? confinedRoot = null)
    {
        if (parameters is null)
        {
            throw new ArgumentException("Missing tool call parameters.");
        }

        var name = GetString(parameters.Value, "name") ?? throw new ArgumentException("Missing tool name.");
        var arguments = GetProperty(parameters.Value, "arguments") ?? default;
        var path = GetString(arguments, "path") ?? defaultPath;
        var localPatterns = GetString(arguments, "patterns") ?? patternsPath;
        var localPatternPacks = GetString(arguments, "patternPacks") ?? patternPacks;
        var inputFile = GetString(arguments, "input");

        if (confinedRoot is not null)
        {
            if (path is not null && !IsUnderRoot(path, confinedRoot))
            {
                throw new ArgumentException($"Path is outside the --mcp-root confinement: {path}");
            }

            if (inputFile is not null && !IsUnderRoot(inputFile, confinedRoot))
            {
                throw new ArgumentException($"Input file is outside the --mcp-root confinement: {inputFile}");
            }
        }

        object payload = name switch
        {
            "dosai.methods" => JsonSerializer.Deserialize<object>(Dosai.GetMethods(RequirePath(path)), JsonOptions)!,
            "dosai.dataflows" => DataFlowAnalyzer.Analyze(RequirePath(path), localPatterns, localPatternPacks),
            "dosai.crypto" => JsonSerializer.Deserialize<object>(CryptoAnalyzer.GetCryptoAnalysis(RequirePath(path), GetString(arguments, "format") ?? "dosai"), JsonOptions)!,
            "dosai.agent_context" => TransparencyBuilder.BuildAgentContext(DataFlowAnalyzer.Analyze(RequirePath(path), localPatterns, localPatternPacks), RequirePath(path)),
            "dosai.services" => ServicesPayload(RequirePath(path)),
            "dosai.ai_components" => AiComponentsPayload(RequirePath(path)),
            "dosai.query" => JsonSerializer.Deserialize<object>(DosaiQueryEngine.QueryJson(LoadQueryInput(arguments, path, localPatterns, localPatternPacks), GetString(arguments, "query") ?? "slices"), JsonOptions)!,
            _ => throw new ArgumentException($"Unsupported tool: {name}")
        };

        return new
        {
            content = new[]
            {
                new { type = "text", text = JsonSerializer.Serialize(payload, JsonOptions) }
            }
        };
    }

    /// <summary>Services payload for dosai.services: services with their operations and frameworks detected.</summary>
    private static object ServicesPayload(string path)
    {
        var slice = Dosai.GetMethodsSlice(path);
        return new
        {
            services = slice.Services,
            frameworks = slice.Frameworks,
            endpoints = slice.ApiEndpoints
        };
    }

    /// <summary>AI inventory payload for dosai.ai_components.</summary>
    private static object AiComponentsPayload(string path)
    {
        var slice = Dosai.GetMethodsSlice(path);
        return new { aiComponents = slice.AiComponents };
    }

    private static string LoadQueryInput(JsonElement arguments, string? path, string? patternsPath, string? patternPacks)
    {
        var inputFile = GetString(arguments, "input");
        if (!string.IsNullOrWhiteSpace(inputFile))
        {
            return File.ReadAllText(inputFile);
        }
        return JsonSerializer.Serialize(DataFlowAnalyzer.Analyze(RequirePath(path), patternsPath, patternPacks), JsonOptions);
    }

    private static string RequirePath(string? path) => string.IsNullOrWhiteSpace(path) ? throw new ArgumentException("A path is required.") : path;

    private static object Tool(string name, string description) => new
    {
        name,
        description,
        inputSchema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["path"] = new { type = "string", description = "File or directory to inspect." },
                ["patterns"] = new { type = "string", description = "Optional data-flow pattern JSON file." },
                ["patternPacks"] = new { type = "string", description = "Comma-separated built-in pattern packs." },
                ["format"] = new { type = "string", description = "Output format for dosai.crypto: dosai, cyclonedx." },
                ["input"] = new { type = "string", description = "Existing Dosai JSON file for dosai.query." },
                ["query"] = new { type = "string", description = "Query expression for dosai.query." }
            }
        }
    };

    private static JsonElement? GetProperty(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        foreach (var property in element.EnumerateObject())
        {
            if (property.NameEquals(name)) return property.Value;
        }
        return null;
    }

    private static string? GetString(JsonElement element, string name)
    {
        var property = GetProperty(element, name);
        return property is { ValueKind: JsonValueKind.String } ? property.Value.GetString() : null;
    }

    private static void WriteResult(TextWriter output, JsonElement? id, object result)
    {
        output.WriteLine(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result }, JsonOptions));
        output.Flush();
    }

    private static void WriteError(TextWriter output, JsonElement? id, int code, string message)
    {
        output.WriteLine(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, error = new { code, message } }, JsonOptions));
        output.Flush();
    }

    private sealed class JsonRpcRequest
    {
        public string Jsonrpc { get; set; } = "2.0";
        public JsonElement? Id { get; set; }
        public required string Method { get; set; }
        public JsonElement? Params { get; set; }
    }
}
