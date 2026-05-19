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

    public static int Run(string? defaultPath = null, string? patternsPath = null, string? patternPacks = null, TextReader? input = null, TextWriter? output = null)
    {
        input ??= Console.In;
        output ??= Console.Out;
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

                var result = Handle(request, defaultPath, patternsPath, patternPacks);
                WriteResult(output, request.Id, result);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException or FileNotFoundException)
            {
                WriteError(output, request?.Id, -32000, ex.Message);
            }
        }

        return 0;
    }

    private static object Handle(JsonRpcRequest request, string? defaultPath, string? patternsPath, string? patternPacks)
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
                    Tool("dosai.agent_context", "Generate compact agent context from data-flow analysis."),
                    Tool("dosai.query", "Filter Dosai JSON with queries like slices[sinkCategory=sql].")
                }
            },
            "tools/call" => CallTool(request.Params, defaultPath, patternsPath, patternPacks),
            _ => throw new ArgumentException($"Unsupported MCP/JSON-RPC method: {request.Method}")
        };
    }

    private static object CallTool(JsonElement? parameters, string? defaultPath, string? patternsPath, string? patternPacks)
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

        object payload = name switch
        {
            "dosai.methods" => JsonSerializer.Deserialize<object>(Dosai.GetMethods(RequirePath(path)), JsonOptions)!,
            "dosai.dataflows" => DataFlowAnalyzer.Analyze(RequirePath(path), localPatterns, localPatternPacks),
            "dosai.agent_context" => TransparencyBuilder.BuildAgentContext(DataFlowAnalyzer.Analyze(RequirePath(path), localPatterns, localPatternPacks), RequirePath(path)),
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
