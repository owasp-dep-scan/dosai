namespace Depscan.Frameworks;

/// <summary>
///     Source location of a service, operation, or AI component. Paths are relative to the analyzed
///     root so output is reproducible across machines.
/// </summary>
public sealed class CodeLocation
{
    public string? Path { get; set; }

    public string? FileName { get; set; }

    public int LineNumber { get; set; }

    public int ColumnNumber { get; set; }

    public static CodeLocation From(string? basePath, string? sourceFile, int lineNumber = 0, int columnNumber = 0)
    {
        var relative = sourceFile is null || string.IsNullOrWhiteSpace(basePath)
            ? sourceFile
            : System.IO.Path.GetRelativePath(basePath, sourceFile);
        return new CodeLocation
        {
            Path = relative,
            FileName = sourceFile is null ? null : System.IO.Path.GetFileName(sourceFile),
            LineNumber = lineNumber,
            ColumnNumber = columnNumber
        };
    }
}

/// <summary>A single route parameter with its constraints, optionality, and binding metadata.</summary>
public sealed class RouteParameter
{
    public string Name { get; set; } = string.Empty;

    public List<string> Constraints { get; set; } = [];

    public bool Optional { get; set; }

    public string? DefaultValue { get; set; }

    public bool CatchAll { get; set; }

    /// <summary>Where the value is bound from: Route, Query, Body, Header, Form, Services, or Unknown.</summary>
    public string? BindingSource { get; set; }

    /// <summary>CLR type of the bound parameter when known, e.g. "System.Int32".</summary>
    public string? ClrType { get; set; }
}

/// <summary>
///     Closed set of service kinds. Extend deliberately and document every addition in docs/frameworks.md;
///     consumers map these onto CycloneDX service properties.
/// </summary>
public static class ServiceKinds
{
    public const string Http = "http";
    public const string Grpc = "grpc";
    public const string GraphQl = "graphql";
    public const string OData = "odata";
    public const string Soap = "soap";
    public const string WebSocket = "websocket";
    public const string Queue = "queue";
    public const string Topic = "topic";
    public const string PubSub = "pubsub";
    public const string Function = "function";
    public const string Scheduled = "scheduled";
    public const string Mcp = "mcp";
    public const string AiInference = "ai-inference";
    public const string VectorStore = "vector-store";
    public const string Database = "database";
    public const string Cache = "cache";
    public const string Storage = "storage";
    public const string Identity = "identity";
    public const string Secrets = "secrets";
    public const string Other = "other";
}

public static class ServiceDirections
{
    public const string Inbound = "inbound";

    public const string Outbound = "outbound";

    public const string Bidirectional = "bidirectional";
}

public static class TrustZones
{
    public const string Public = "public";
    public const string Authenticated = "authenticated";
    public const string Internal = "internal";
    public const string External = "external";
    public const string Unknown = "unknown";
}

public static class ConfidenceTiers
{
    /// <summary>Symbol resolved against a real base type or interface: high confidence.</summary>
    public const string Semantic = "high";

    /// <summary>Attribute/name match without symbol resolution: medium confidence.</summary>
    public const string Syntactic = "medium";

    /// <summary>Textual or config-file inference: low confidence.</summary>
    public const string Heuristic = "low";
}

/// <summary>
///     A first-class service inventory entry: one inbound surface (controller, hub, gRPC service,
///     MCP server) or one outbound dependency (inference endpoint, vector store, message broker).
/// </summary>
public sealed class ServiceComponent
{
    /// <summary>Stable bom-ref. See <see cref="FrameworkIds" />; never embeds paths, lines, or timestamps.</summary>
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>Typically the namespace of the implementing type.</summary>
    public string? Group { get; set; }

    public string? Version { get; set; }

    public string ServiceKind { get; set; } = ServiceKinds.Other;

    /// <summary>"inbound", "outbound", or "bidirectional".</summary>
    public string Direction { get; set; } = ServiceDirections.Inbound;

    /// <summary>Id of the framework provider that produced this service, e.g. "aspnetcore-mvc".</summary>
    public string Framework { get; set; } = string.Empty;

    public string? FrameworkVersion { get; set; }

    /// <summary>Package URL of the framework package when detection provided one.</summary>
    public string? Purl { get; set; }

    /// <summary>For outbound services: the concrete provider, e.g. "openai", "azure", "qdrant".</summary>
    public string? Provider { get; set; }

    public List<ServiceOperation> Operations { get; set; } = [];

    /// <summary>Resolved paths or URLs; maps to CycloneDX services[].endpoints.</summary>
    public List<string> Endpoints { get; set; } = [];

    /// <summary>Null means unknown. Never emit false just because no authorization was analyzed.</summary>
    public bool? Authenticated { get; set; }

    public List<string> AuthenticationSchemes { get; set; } = [];

    public List<string> AuthorizationPolicies { get; set; } = [];

    public List<string> Roles { get; set; } = [];

    public bool? AllowAnonymous { get; set; }

    public string TrustZone { get; set; } = TrustZones.Unknown;

    public bool? CrossesTrustBoundary { get; set; }

    public List<ServiceDataFlow> Data { get; set; } = [];

    public string Confidence { get; set; } = ConfidenceTiers.Syntactic;

    public AnalysisEvidence Evidence { get; set; } = new();

    public CodeLocation Location { get; set; } = new();

    public List<string> Tags { get; set; } = [];

    public Dictionary<string, string> Properties { get; set; } = [];

    public List<string> EntryPointIds { get; set; } = [];

    public List<string> MethodIds { get; set; } = [];
}

public sealed class ServiceOperation
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>HTTP verb; null for non-HTTP operations.</summary>
    public string? HttpMethod { get; set; }

    /// <summary>Resolved path with a leading "/"; null when the template could not be resolved.</summary>
    public string? Path { get; set; }

    /// <summary>Verbatim route template as declared.</summary>
    public string? RouteTemplate { get; set; }

    public List<RouteParameter> RouteParameters { get; set; } = [];

    /// <summary>"unary", "client" (client-streaming), "server" (server-streaming), or "bidi".</summary>
    public string? StreamingMode { get; set; }

    public string? RequestType { get; set; }

    public string? ResponseType { get; set; }

    public List<string> ContentTypes { get; set; } = [];

    /// <summary>Links to the Methods[] entry implementing this operation.</summary>
    public string? MethodId { get; set; }

    public bool? Authenticated { get; set; }

    public string? Deprecated { get; set; }

    public string Confidence { get; set; } = ConfidenceTiers.Syntactic;

    public CodeLocation Location { get; set; } = new();

    public Dictionary<string, string> Properties { get; set; } = [];
}

/// <summary>
///     Data classification for a service boundary, mapped to CycloneDX services[].data[].
///     Classifications default to "unknown" and never guess "public"; every non-"unknown"
///     classification names the member that triggered it in Description.
/// </summary>
public sealed class ServiceDataFlow
{
    /// <summary>"inbound", "outbound", "bi-directional", or "unknown".</summary>
    public string Flow { get; set; } = "unknown";

    /// <summary>pii, credential, financial, health, internal, public, or unknown.</summary>
    public string Classification { get; set; } = "unknown";

    /// <summary>The CLR or protobuf type crossing the boundary.</summary>
    public string? Name { get; set; }

    public string? Description { get; set; }

    public List<string> Source { get; set; } = [];

    public List<string> Destination { get; set; } = [];

    public string Confidence { get; set; } = ConfidenceTiers.Heuristic;
}

/// <summary>Stable, reproducible bom-ref builders for services, operations, and AI components.</summary>
public static class FrameworkIds
{
    /// <summary>e.g. svc:aspnetcore-mvc:MyApp.Controllers/WeatherForecast</summary>
    public static string Service(string framework, string? group, string name)
    {
        return $"svc:{framework.ToLowerInvariant()}:{Sanitize(group)}/{SanitizeName(name)}";
    }

    /// <summary>e.g. op:svc:grpc:pkg/Greeter#/greet.Greeter/SayHello</summary>
    public static string Operation(string serviceId, string? httpMethod, string? path, string operationName)
    {
        var discriminator = string.IsNullOrWhiteSpace(httpMethod) || string.Equals(httpMethod, "ANY", StringComparison.OrdinalIgnoreCase)
            ? operationName
            : $"{httpMethod.ToUpperInvariant()}:{path ?? operationName}";
        return $"op:{serviceId}#{discriminator}";
    }

    /// <summary>e.g. ai:model:openai/gpt-4o</summary>
    public static string Ai(string kind, string? provider, string name)
    {
        return $"ai:{kind.ToLowerInvariant()}:{Sanitize(provider)}/{SanitizeName(name)}";
    }

    private static string Sanitize(string? value)
    {
        var trimmed = value?.Trim('/');
        return string.IsNullOrWhiteSpace(trimmed) ? "_" : trimmed.Replace('/', '.');
    }

    private static string SanitizeName(string name)
    {
        return string.IsNullOrWhiteSpace(name) ? "_" : name.Trim().Replace(' ', '_');
    }
}
