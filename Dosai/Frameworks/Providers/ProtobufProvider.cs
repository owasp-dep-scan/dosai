using System.Text.RegularExpressions;

namespace Depscan.Frameworks.Providers;

/// <summary>
///     Protocol buffer IDL analysis: parses .proto files directly (package, service, rpc, message
///     fields, google.api.http annotations, imports) and reads the &lt;Protobuf Include=...
///     GrpcServices="Server|Client|Both" /&gt; csproj metadata that says which side the project
///     implements without any C# analysis. Parsed services are recorded on the context for the
///     gRPC provider to join against implementation classes.
/// </summary>
public sealed partial class ProtobufProvider : IFrameworkProvider
{
    public string Id => "protobuf";

    public string DisplayName => "Protocol Buffers";

    public bool AppliesTo(FrameworkContext ctx) => ctx.ProtoFiles.Count > 0;

    public void Analyze(FrameworkContext ctx, FrameworkResults results)
    {
        var grpcServices = ReadCsprojGrpcServices(ctx);
        foreach (var protoFile in ctx.ProtoFiles)
        {
            string text;
            try
            {
                text = File.ReadAllText(protoFile);
            }
            catch (IOException)
            {
                continue;
            }

            var package = PackageRegex().Match(text) is { Success: true } packageMatch ? packageMatch.Groups[1].Value : null;
            foreach (var (serviceName, rpcs) in ParseServices(text))
            {
                ctx.ProtoServices.Add(new ProtoServiceContract(serviceName, package, protoFile, rpcs));
                var serviceId = FrameworkIds.Service("protobuf", package, serviceName);
                var declaredRole = grpcServices.GetValueOrDefault(Path.GetFileName(protoFile));
                var service = new ServiceComponent
                {
                    Id = serviceId,
                    Name = serviceName,
                    Group = package,
                    ServiceKind = ServiceKinds.Grpc,
                    Direction = declaredRole is "Client" ? ServiceDirections.Outbound : ServiceDirections.Inbound,
                    Framework = "protobuf",
                    Confidence = ConfidenceTiers.Heuristic,
                    Location = CodeLocation.From(ctx.BasePath, protoFile),
                    Evidence = new AnalysisEvidence
                    {
                        Kind = AnalysisEvidenceKind.FrameworkModel,
                        Source = "protobuf",
                        Description = $"service {serviceName} declared in {Path.GetFileName(protoFile)}.",
                        Confidence = ConfidenceTiers.Heuristic,
                        FileName = Path.GetFileName(protoFile)
                    }
                };
                if (declaredRole is not null)
                {
                    service.Properties["grpcServices"] = declaredRole;
                }

                foreach (var rpc in rpcs)
                {
                    var operation = new ServiceOperation
                    {
                        Id = FrameworkIds.Operation(serviceId, null, $"/{package ?? serviceName}.{serviceName}/{rpc.Name}", rpc.Name),
                        Name = rpc.Name,
                        Path = $"/{(package is null ? serviceName : $"{package}.{serviceName}")}/{rpc.Name}",
                        StreamingMode = StreamingModeOf(rpc),
                        RequestType = rpc.InputType,
                        ResponseType = rpc.OutputType,
                        Confidence = ConfidenceTiers.Heuristic,
                        Location = CodeLocation.From(ctx.BasePath, protoFile)
                    };
                    if (rpc.HttpVerb is not null && rpc.HttpPath is not null)
                    {
                        // google.api.http annotation: the rpc is also reachable over plain HTTP
                        // (gRPC JSON transcoding) — surfaced for the gRPC provider to expand.
                        operation.Properties["httpVerb"] = rpc.HttpVerb;
                        operation.Properties["httpPath"] = rpc.HttpPath;
                    }

                    service.Operations.Add(operation);
                    service.Data.Add(new ServiceDataFlow
                    {
                        Flow = ServiceDirections.Inbound,
                        Classification = "unknown",
                        Name = rpc.InputType,
                        Description = "protobuf request message",
                        Destination = [serviceId],
                        Confidence = ConfidenceTiers.Heuristic
                    });
                    if (!string.Equals(rpc.InputType, rpc.OutputType, StringComparison.Ordinal))
                    {
                        service.Data.Add(new ServiceDataFlow
                        {
                            Flow = ServiceDirections.Outbound,
                            Classification = "unknown",
                            Name = rpc.OutputType,
                            Description = "protobuf response message",
                            Source = [serviceId],
                            Confidence = ConfidenceTiers.Heuristic
                        });
                    }
                }

                results.Services.Add(service);
            }
        }
    }

    internal static string? StreamingModeOf(ProtoRpcContract rpc) => (rpc.ClientStreaming, rpc.ServerStreaming) switch
    {
        (false, false) => "unary",
        (true, false) => "client",
        (false, true) => "server",
        _ => "bidi"
    };

    /// <summary>GrpcServices item metadata from csproj files: proto file name -> Server|Client|Both|None.</summary>
    private static Dictionary<string, string> ReadCsprojGrpcServices(FrameworkContext ctx)
    {
        var roles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var projectFile in ctx.ConfigFiles.Where(file => file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var text = File.ReadAllText(projectFile);
                foreach (Match item in ProtobufItemRegex().Matches(text))
                {
                    var include = ProtobufIncludeRegex().Match(item.Value);
                    if (!include.Success)
                    {
                        continue;
                    }

                    var roleMatch = ProtobufGrpcServicesRegex().Match(item.Value);
                    // csproj paths use Windows separators regardless of host OS.
                    var protoName = include.Groups["include"].Value.Replace('\\', '/');
                    roles[Path.GetFileName(protoName)] = roleMatch.Success ? roleMatch.Groups["grpc"].Value : "Both";
                }
            }
            catch (IOException)
            {
                // Unreadable project file: best-effort skip.
            }
        }

        return roles;
    }

    /// <summary>
    ///     Parses service and rpc declarations with a brace-balancing scanner: rpc option blocks
    ///     (google.api.http) nest braces two levels deep inside service bodies, beyond what a
    ///     single regex can tolerate.
    /// </summary>
    internal static List<(string Service, List<ProtoRpcContract> Rpcs)> ParseServices(string protoText)
    {
        var services = new List<(string, List<ProtoRpcContract>)>();
        var searchStart = 0;
        while (FindKeywordBlock(protoText, "service", searchStart) is { } serviceBlock)
        {
            var rpcs = new List<ProtoRpcContract>();
            var bodyStart = 0;
            while (FindKeywordBlock(serviceBlock.Body, "rpc", bodyStart) is { } rpcBlock)
            {
                var signature = rpcBlock.Header;
                var match = RpcSignatureRegex().Match(signature);
                if (match.Success)
                {
                    string? httpVerb = null;
                    string? httpPath = null;
                    var httpRule = HttpRuleRegex().Match(rpcBlock.Body);
                    if (httpRule.Success)
                    {
                        httpVerb = httpRule.Groups["verb"].Value.ToUpperInvariant();
                        httpPath = httpRule.Groups["path"].Value;
                    }

                    rpcs.Add(new ProtoRpcContract(
                        match.Groups["name"].Value,
                        match.Groups["input"].Value,
                        match.Groups["output"].Value,
                        match.Groups["inStream"].Success,
                        match.Groups["outStream"].Success,
                        httpVerb,
                        httpPath));
                }

                bodyStart = rpcBlock.End;
            }

            services.Add((serviceBlock.Name, rpcs));
            searchStart = serviceBlock.End;
        }

        return services;
    }

    /// <summary>A keyword block: "keyword Name(...) { body }" or semicolon-terminated "keyword Name(...);", brace-balanced.</summary>
    private static KeywordBlock? FindKeywordBlock(string text, string keyword, int start)
    {
        var keywordIndex = text.IndexOf(keyword + " ", start, StringComparison.Ordinal);
        while (keywordIndex >= 0)
        {
            var nameMatch = System.Text.RegularExpressions.Regex.Match(text[(keywordIndex + keyword.Length)..], @"^\s*(\w+)");
            var openBrace = text.IndexOf('{', keywordIndex);
            var semicolon = text.IndexOf(';', keywordIndex);
            if (nameMatch.Success)
            {
                var name = nameMatch.Groups[1].Value;
                if (openBrace >= 0 && (semicolon < 0 || openBrace < semicolon))
                {
                    var depth = 1;
                    var position = openBrace + 1;
                    while (position < text.Length && depth > 0)
                    {
                        if (text[position] == '{') depth++;
                        else if (text[position] == '}') depth--;
                        position++;
                    }

                    return new KeywordBlock(name, text[keywordIndex..openBrace], text[(openBrace + 1)..(position - 1)], position);
                }

                if (semicolon >= 0 && (openBrace < 0 || semicolon < openBrace))
                {
                    return new KeywordBlock(name, text[keywordIndex..semicolon], string.Empty, semicolon + 1);
                }
            }

            keywordIndex = text.IndexOf(keyword + " ", keywordIndex + 1, StringComparison.Ordinal);
        }

        return null;
    }

    private sealed record KeywordBlock(string Name, string Header, string Body, int End);

    [GeneratedRegex(@"^\s*package\s+([\w\.]+)\s*;", RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex PackageRegex();

    // google.api.http annotation body, e.g. { post: "/v1/echo" body: "*" } or { get: "/v1/x/{id}" }
    [GeneratedRegex(@"(?<verb>get|post|put|patch|delete)\s*:\s*\""(?<path>[^\""]+)\""", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex HttpRuleRegex();

    // rpc Name (stream? Input) returns (stream? Output) — the scanner already stripped braces.
    [GeneratedRegex(@"rpc\s+(?<name>\w+)\s*\(\s*(?<inStream>stream\s+)?(?<input>[\w\.]+)\s*\)\s*returns\s*\(\s*(?<outStream>stream\s+)?(?<output>[\w\.]+)\s*\)", RegexOptions.Compiled)]
    private static partial Regex RpcSignatureRegex();

    [GeneratedRegex(@"<Protobuf\b[^>]*>", RegexOptions.Compiled)]
    private static partial Regex ProtobufItemRegex();

    [GeneratedRegex(@"Include=\""(?<include>[^\""]+)\""", RegexOptions.Compiled)]
    private static partial Regex ProtobufIncludeRegex();

    [GeneratedRegex(@"GrpcServices=\""(?<grpc>[^\""]+)\""", RegexOptions.Compiled)]
    private static partial Regex ProtobufGrpcServicesRegex();
}
