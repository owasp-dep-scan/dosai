using Microsoft.CodeAnalysis;

namespace Depscan.Frameworks;

/// <summary>A provider that recognizes one framework family and contributes service/AI inventory from it.</summary>
public interface IFrameworkProvider
{
    /// <summary>Stable id, e.g. "aspnetcore-mvc", "grpc", "mcp". Used in Evidence and property names.</summary>
    string Id { get; }

    /// <summary>Display name aligned with cdxgen's data/frameworks-list.json where one exists.</summary>
    string DisplayName { get; }

    /// <summary>
    ///     Cheap gate. Return false to skip entirely. Consult ctx.Detection (PURLs, usings, config
    ///     files) — never scan the whole tree here.
    /// </summary>
    bool AppliesTo(FrameworkContext ctx);

    /// <summary>
    ///     Contribute to any/all of: services, endpoints, AI components, taint seeds. Must never
    ///     throw; report problems via ctx.Diagnostics.
    /// </summary>
    void Analyze(FrameworkContext ctx, FrameworkResults results);
}

/// <summary>Accumulates everything framework providers contribute during one analysis run.</summary>
public sealed class FrameworkResults
{
    public List<ServiceComponent> Services { get; } = [];

    public List<ApiEndpoint> ApiEndpoints { get; } = [];

    public List<AiComponent> AiComponents { get; } = [];

    public List<EntryPoint> EntryPoints { get; } = [];

    public List<FrameworkTaintSeed> TaintSeeds { get; } = [];
}

/// <summary>
///     A parameter of a framework entry point that carries untrusted input, wired into taint analysis
///     as a source node.
/// </summary>
/// <remarks>
///     Seeds are resolved by <see cref="MethodSignature" /> where the provider captured one, falling
///     back to file plus method plus class name where it did not. That fallback is a name match and
///     cannot separate overloads, so providers should always populate <see cref="MethodSignature" />.
/// </remarks>
public sealed class FrameworkTaintSeed
{
    /// <summary>File-anchored method identity: Namespace.Class.Method, stable across compilations.</summary>
    public required string MethodName { get; set; }

    public required string ParameterName { get; set; }

    public string? ClassName { get; set; }

    public string? Namespace { get; set; }

    public string? FileName { get; set; }

    /// <summary>Method signature in Dosai's GenerateMethodSignature format when available.</summary>
    public string? MethodSignature { get; set; }

    public int LineNumber { get; set; }

    /// <summary>Binding source: http-route, http-query, http-body, http-header, http-form, rpc-message, mcp-tool-arg, queue-message, function-payload, websocket-message.</summary>
    public string BindingSource { get; set; } = "unknown";

    public string TaintKind { get; set; } = "http";

    public string FrameworkId { get; set; } = string.Empty;

    public string EndpointPath { get; set; } = string.Empty;

    public string Confidence { get; set; } = ConfidenceTiers.Semantic;
}

/// <summary>A non-fatal problem recorded during framework analysis.</summary>
public sealed record FrameworkDiagnostic(string FrameworkId, string Message, string? FileName = null, int LineNumber = 0);

/// <summary>
///     A framework mounted at an HTTP path by another framework's registration call, e.g.
///     app.MapHub&lt;ChatHub&gt;("/chat") or app.MapGrpcService&lt;GreeterService&gt;().
/// </summary>
/// <summary>A Map*/Use* registration that mounts another framework's surface (route path, source file, line).</summary>
/// <param name="TypeName">Generic type argument when the mount is typed, e.g. <c>MapGrpcService&lt;BasketService&gt;</c>; captured from syntax so consumers never re-read the file from disk.</param>
public sealed record MountPoint(string Kind, string Path, string? FileName, int LineNumber, string? TypeName = null);

/// <summary>One rpc declared in a .proto file, with its streaming directions and optional HTTP annotation.</summary>
public sealed record ProtoRpcContract(string Name, string InputType, string OutputType, bool ClientStreaming, bool ServerStreaming, string? HttpVerb, string? HttpPath);

/// <summary>A service declared in a .proto file, joined to C# implementations by the gRPC provider.</summary>
public sealed record ProtoServiceContract(string Name, string? Package, string FilePath, List<ProtoRpcContract> Methods);
