namespace Depscan.Frameworks;

/// <summary>One detected framework: identity, version when known, and how it was detected.</summary>
public sealed record DetectedFramework
{
    /// <summary>Provider id, e.g. "aspnetcore-mvc".</summary>
    public required string Id { get; init; }

    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Detected version when available from the package reference.</summary>
    public string? Version { get; init; }

    /// <summary>Package URL of the framework package when detection came from restore metadata.</summary>
    public string? Purl { get; init; }

    /// <summary>"purl", "using", or "config" — how the framework was detected.</summary>
    public required string DetectionKind { get; init; }

    /// <summary>PURL detection is high confidence, using directives medium, config files low.</summary>
    public string Confidence { get; init; } = ConfidenceTiers.Syntactic;
}

/// <summary>
///     Detects which frameworks are present from package URLs (authoritative), imported namespaces,
///     and config/manifest files. Runs once per analysis; providers gate on the result.
/// </summary>
public sealed class FrameworkDetection
{
    private readonly List<DetectedFramework> _frameworks = [];

    public IReadOnlyList<DetectedFramework> Frameworks => _frameworks;

    public bool IsDetected(string frameworkId) => _frameworks.Any(framework => framework.Id.Equals(frameworkId, StringComparison.OrdinalIgnoreCase));

    public DetectedFramework? this[string frameworkId] => _frameworks.FirstOrDefault(framework => framework.Id.Equals(frameworkId, StringComparison.OrdinalIgnoreCase));

    public static FrameworkDetection Detect(FrameworkContext ctx)
    {
        var detection = new FrameworkDetection();
        detection.DetectFromPurls(ctx);
        detection.DetectFromUsings(ctx);
        detection.DetectFromConfigFiles(ctx);
        detection._frameworks.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return detection;
    }

    private void DetectFromPurls(FrameworkContext ctx)
    {
        // The resolver's name-based lookup covers assets/deps files; probe the known framework
        // package names through it so versions and purls come from restore metadata when present.
        foreach (var (packageName, framework) in PackageFrameworkTable)
        {
            var purl = SafeResolve(ctx, packageName);
            if (purl is null || !PurlIdentifies(purl, packageName))
            {
                continue;
            }

            var version = PurlVersion(purl);
            Add(new DetectedFramework
            {
                Id = framework.Id,
                DisplayName = framework.DisplayName,
                Version = version,
                Purl = purl,
                DetectionKind = "purl",
                Confidence = ConfidenceTiers.Semantic
            });
        }
    }

    /// <summary>
    ///     Guards against <see cref="PackageUrlResolver" />'s versionless <c>System.*</c> fallback table,
    ///     whose catch-all <c>("System", "System.Runtime")</c> entry makes <em>every</em> <c>System.*</c>
    ///     probe resolve to <c>pkg:nuget/System.Runtime</c>. Without this check, probing
    ///     <c>System.ServiceModel</c> and <c>System.Web.Http</c> reported WCF and Web API 2 as present —
    ///     at high confidence — in every .NET project analyzed, whether or not either was referenced.
    ///     A detection only counts when the resolved purl actually names the package we probed for.
    /// </summary>
    private static bool PurlIdentifies(string purl, string packageName)
    {
        // pkg:nuget/<Name>[@<Version>] -> <Name>
        var nameStart = purl.LastIndexOf('/');
        if (nameStart < 0)
        {
            return false;
        }

        var name = purl[(nameStart + 1)..];
        var versionStart = name.IndexOf('@');
        if (versionStart >= 0)
        {
            name = name[..versionStart];
        }

        return string.Equals(name, packageName, StringComparison.OrdinalIgnoreCase);
    }

    private static string? SafeResolve(FrameworkContext ctx, string packageName)
    {
        try
        {
            return ctx.PurlResolver.Resolve(assembly: packageName, symbol: packageName, namespaceName: packageName);
        }
        catch
        {
            return null;
        }
    }

    private void DetectFromUsings(FrameworkContext ctx)
    {
        foreach (var (namespacePrefix, framework) in UsingFrameworkTable)
        {
            if (ctx.ImportedNamespaces.Any(imported => imported.StartsWith(namespacePrefix, StringComparison.Ordinal)) && !IsDetected(framework.Id))
            {
                Add(new DetectedFramework
                {
                    Id = framework.Id,
                    DisplayName = framework.DisplayName,
                    DetectionKind = "using",
                    Confidence = ConfidenceTiers.Syntactic
                });
            }
        }
    }

    private void DetectFromConfigFiles(FrameworkContext ctx)
    {
        foreach (var file in ctx.ConfigFiles)
        {
            var name = Path.GetFileName(file);
            if (name.Equals("host.json", StringComparison.OrdinalIgnoreCase) || name.Equals("function.json", StringComparison.OrdinalIgnoreCase))
            {
                AddIfMissing(new DetectedFramework
                {
                    Id = "azure-functions",
                    DisplayName = "Azure Functions",
                    DetectionKind = "config",
                    Confidence = ConfidenceTiers.Heuristic
                });
            }

            if (name.Equals("web.config", StringComparison.OrdinalIgnoreCase) || name.Equals("app.config", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    if (File.ReadAllText(file).Contains("system.serviceModel", StringComparison.Ordinal))
                    {
                        AddIfMissing(new DetectedFramework
                        {
                            Id = "legacy-wcf",
                            DisplayName = "WCF",
                            DetectionKind = "config",
                            Confidence = ConfidenceTiers.Heuristic
                        });
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Unreadable config: skip; detection is best-effort.
                }
            }

            if (name.Equals("serverless.template", StringComparison.OrdinalIgnoreCase) || name.Equals("aws-lambda-tools-defaults.json", StringComparison.OrdinalIgnoreCase))
            {
                AddIfMissing(new DetectedFramework
                {
                    Id = "aws-lambda",
                    DisplayName = "AWS Lambda",
                    DetectionKind = "config",
                    Confidence = ConfidenceTiers.Heuristic
                });
            }
        }
    }

    private void Add(DetectedFramework framework)
    {
        if (!_frameworks.Any(existing => existing.Id.Equals(framework.Id, StringComparison.OrdinalIgnoreCase)))
        {
            _frameworks.Add(framework);
        }
    }

    private void AddIfMissing(DetectedFramework framework) => Add(framework);

    internal static string? PurlVersion(string purl)
    {
        var versionIndex = purl.LastIndexOf("@", StringComparison.Ordinal);
        return versionIndex > 0 ? purl[(versionIndex + 1)..] : null;
    }

    private static readonly (string PackageName, (string Id, string DisplayName) Framework)[] PackageFrameworkTable =
    [
        ("Microsoft.AspNetCore.Mvc", ("aspnetcore-mvc", "ASP.NET Core MVC")),
        ("Microsoft.AspNetCore.Http", ("minimal-api", "ASP.NET Core Minimal APIs")),
        ("Microsoft.AspNetCore.Razor.Pages", ("razor-blazor", "Razor Pages / Blazor")),
        ("Microsoft.AspNetCore.SignalR", ("signalr", "ASP.NET Core SignalR")),
        ("Grpc.AspNetCore", ("grpc", "gRPC ASP.NET Core")),
        ("Grpc.Net.Client", ("grpc", "gRPC ASP.NET Core")),
        ("Google.Protobuf", ("protobuf", "Protocol Buffers")),
        ("protobuf-net", ("protobuf", "protobuf-net")),
        ("HotChocolate.AspNetCore", ("graphql", "GraphQL (HotChocolate)")),
        ("GraphQL", ("graphql", "GraphQL .NET")),
        ("Microsoft.AspNetCore.OData", ("odata", "OData")),
        ("System.Web.Http", ("legacy-web", "ASP.NET Web API 2")),
        ("Microsoft.AspNet.Mvc", ("legacy-web", "ASP.NET MVC 5")),
        ("System.ServiceModel", ("legacy-wcf", "WCF")),
        ("System.ServiceModel.Primitives", ("legacy-wcf", "WCF")),
        ("Microsoft.Azure.Functions.Worker", ("azure-functions", "Azure Functions")),
        ("Microsoft.NET.Sdk.Functions", ("azure-functions", "Azure Functions")),
        ("Amazon.Lambda.Core", ("aws-lambda", "AWS Lambda")),
        ("MassTransit", ("messaging", "MassTransit")),
        ("NServiceBus", ("messaging", "NServiceBus")),
        ("Rebus", ("messaging", "Rebus")),
        ("MediatR", ("messaging", "MediatR")),
        ("Dapr.AspNetCore", ("messaging", "Dapr")),
        ("Confluent.Kafka", ("messaging", "Kafka (Confluent)")),
        ("Azure.Messaging.ServiceBus", ("messaging", "Azure Service Bus")),
        ("RabbitMQ.Client", ("messaging", "RabbitMQ")),
        ("Hangfire.AspNetCore", ("background-jobs", "Hangfire")),
        ("Hangfire.Core", ("background-jobs", "Hangfire")),
        ("Quartz", ("background-jobs", "Quartz.NET")),
        ("Coravel", ("background-jobs", "Coravel")),
        ("ModelContextProtocol", ("mcp", "Model Context Protocol")),
        ("ModelContextProtocol.AspNetCore", ("mcp", "Model Context Protocol")),
        ("Microsoft.Extensions.AI", ("llm", "Microsoft.Extensions.AI")),
        ("Microsoft.SemanticKernel", ("llm", "Semantic Kernel")),
        ("OpenAI", ("llm", "OpenAI .NET SDK")),
        ("Azure.AI.OpenAI", ("llm", "Azure OpenAI")),
        ("Anthropic.SDK", ("llm", "Anthropic .NET SDK")),
        ("AWSSDK.BedrockRuntime", ("llm", "AWS Bedrock")),
        ("Microsoft.ML", ("ml-runtime", "ML.NET")),
        ("Microsoft.ML.OnnxRuntime", ("ml-runtime", "ONNX Runtime")),
        ("TorchSharp", ("ml-runtime", "TorchSharp")),
        ("LLamaSharp", ("ml-runtime", "LLamaSharp")),
        ("Qdrant.Client", ("vector-store", "Qdrant")),
        ("Pinecone.NET", ("vector-store", "Pinecone")),
        ("Milvus.Client", ("vector-store", "Milvus")),
        ("FastEndpoints", ("community-http", "FastEndpoints")),
        ("Carter", ("community-http", "Carter")),
        ("ServiceStack", ("community-http", "ServiceStack")),
        ("Nancy", ("community-http", "Nancy"))
    ];

    private static readonly (string NamespacePrefix, (string Id, string DisplayName) Framework)[] UsingFrameworkTable =
    [
        ("Microsoft.AspNetCore.Mvc", ("aspnetcore-mvc", "ASP.NET Core MVC")),
        ("Grpc.Core", ("grpc", "gRPC")),
        ("Grpc.AspNetCore", ("grpc", "gRPC ASP.NET Core")),
        ("Google.Protobuf", ("protobuf", "Protocol Buffers")),
        ("Microsoft.AspNetCore.SignalR", ("signalr", "ASP.NET Core SignalR")),
        ("HotChocolate", ("graphql", "GraphQL (HotChocolate)")),
        ("GraphQL", ("graphql", "GraphQL .NET")),
        ("Microsoft.AspNet.OData", ("odata", "OData")),
        ("Microsoft.AspNetCore.OData", ("odata", "OData")),
        ("System.ServiceModel", ("legacy-wcf", "WCF")),
        ("System.Web.Http", ("legacy-web", "ASP.NET Web API 2")),
        ("System.Web.Mvc", ("legacy-web", "ASP.NET MVC 5")),
        ("MassTransit", ("messaging", "MassTransit")),
        ("NServiceBus", ("messaging", "NServiceBus")),
        ("Rebus", ("messaging", "Rebus")),
        ("MediatR", ("messaging", "MediatR")),
        ("Dapr.Client", ("messaging", "Dapr")),
        ("Dapr.AspNetCore", ("messaging", "Dapr")),
        ("Confluent.Kafka", ("messaging", "Kafka (Confluent)")),
        ("Azure.Messaging.ServiceBus", ("messaging", "Azure Service Bus")),
        ("RabbitMQ.Client", ("messaging", "RabbitMQ")),
        ("Hangfire", ("background-jobs", "Hangfire")),
        ("Quartz", ("background-jobs", "Quartz.NET")),
        ("ModelContextProtocol", ("mcp", "Model Context Protocol")),
        ("Microsoft.Extensions.AI", ("llm", "Microsoft.Extensions.AI")),
        ("Microsoft.SemanticKernel", ("llm", "Semantic Kernel")),
        ("OpenAI", ("llm", "OpenAI .NET SDK")),
        ("Azure.AI.OpenAI", ("llm", "Azure OpenAI")),
        ("Anthropic", ("llm", "Anthropic .NET SDK")),
        ("Microsoft.ML", ("ml-runtime", "ML.NET")),
        ("TorchSharp", ("ml-runtime", "TorchSharp")),
        ("LLamaSharp", ("ml-runtime", "LLamaSharp")),
        ("Qdrant.Client", ("vector-store", "Qdrant"))
    ];
}
