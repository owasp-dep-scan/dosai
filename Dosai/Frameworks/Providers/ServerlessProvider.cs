using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Depscan.Frameworks.Providers;

/// <summary>
///     Azure Functions (in-process [FunctionName] and isolated [Function], all trigger and output
///     bindings, host.json routePrefix) and AWS Lambda (Annotations framework and classic handler
///     signatures). HTTP triggers additionally become HTTP endpoints with the resolved path.
/// </summary>
public sealed class ServerlessProvider : IFrameworkProvider
{
    private static readonly string[] TriggerAttributes =
    [
        "HttpTrigger", "TimerTrigger", "QueueTrigger", "BlobTrigger", "ServiceBusTrigger", "EventHubTrigger", "EventGridTrigger", "CosmosDBTrigger", "KafkaTrigger", "OrchestrationTrigger", "ActivityTrigger", "EntityTrigger"
    ];

    private static readonly string[] OutputBindingAttributes = ["Queue", "QueueOutput", "Blob", "BlobOutput", "Table", "TableOutput", "CosmosDB", "CosmosDBOutput", "SignalR", "SignalROutput", "EventHub", "EventHubOutput", "ServiceBus", "ServiceBusOutput", "EventGrid", "EventGridOutput"];

    public string Id => "azure-functions";

    public string DisplayName => "Serverless (Azure Functions, AWS Lambda)";

    public bool AppliesTo(FrameworkContext ctx) => ctx.CSharp is not null || ctx.Detection.IsDetected("azure-functions") || ctx.Detection.IsDetected("aws-lambda");

    public void Analyze(FrameworkContext ctx, FrameworkResults results)
    {
        var routePrefix = ReadHostJsonRoutePrefix(ctx);
foreach (var tree in ctx.CSharpTrees)
        {
            if (!ctx.TextContainsAny(tree, "FunctionName", "Function(", "HttpTrigger", "TimerTrigger", "QueueTrigger", "LambdaFunction", "ILambdaContext"))
            {
                continue;
            }

            var model = ctx.CSharp!.GetSemanticModel(tree);
            var root = tree.GetCompilationUnitRoot();
            var rawUrls = ctx.RawUrlsFor(tree);

            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                var attributes = ProviderHelpers.AttributesOf(method.AttributeLists).ToList();
                var functionAttribute = attributes.FirstOrDefault(attribute => ProviderHelpers.IsNamed(attribute, "FunctionName") || ProviderHelpers.IsNamed(attribute, "Function"));
                if (functionAttribute is not null)
                {
                    AnalyzeAzureFunction(ctx, results, method, attributes, functionAttribute, model, tree.FilePath, rawUrls, routePrefix);
                    continue;
                }

                var lambdaAttribute = attributes.FirstOrDefault(attribute => ProviderHelpers.IsNamed(attribute, "LambdaFunction"));
                if (lambdaAttribute is not null)
                {
                    AnalyzeLambdaAnnotations(ctx, results, method, attributes, lambdaAttribute, model, tree.FilePath, rawUrls);
                    continue;
                }

                AnalyzeLambdaHandler(ctx, results, method, model, tree.FilePath, rawUrls);
            }
        }

        AnalyzeFunctionJson(ctx, results);
    }

    private static void AnalyzeAzureFunction(
        FrameworkContext ctx,
        FrameworkResults results,
        MethodDeclarationSyntax method,
        List<AttributeSyntax> attributes,
        AttributeSyntax functionAttribute,
        SemanticModel model,
        string filePath,
        List<string> rawUrls,
        string routePrefix)
    {
        var functionName = ProviderHelpers.AttributeArgumentText(functionAttribute, model) ?? method.Identifier.Text;
        var trigger = attributes.FirstOrDefault(attribute => TriggerAttributes.Any(triggerName => ProviderHelpers.IsNamed(attribute, triggerName)));
        var lineSpan = method.GetLocation().GetLineSpan().StartLinePosition;
        var methodSymbol = model.GetDeclaredSymbol(method);
        var methodId = methodSymbol is null ? null : Depscan.Dosai.FormatMethodSignature(methodSymbol);
        var typeName = method.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text;
        var namespaceName = method.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();
        var serviceId = FrameworkIds.Service("azure-functions", namespaceName, functionName);

        var service = new ServiceComponent
        {
            Id = serviceId,
            Name = functionName,
            Group = namespaceName,
            ServiceKind = ServiceKinds.Function,
            Direction = ServiceDirections.Inbound,
            Framework = "azure-functions",
            FrameworkVersion = ctx.Detection["azure-functions"]?.Version,
            Purl = ctx.Detection["azure-functions"]?.Purl,
            Confidence = ConfidenceTiers.Semantic,
            Location = CodeLocation.From(ctx.BasePath, filePath, lineSpan.Line + 1),
            Evidence = new AnalysisEvidence
            {
                Kind = AnalysisEvidenceKind.FrameworkModel,
                Source = "azure-functions",
                Description = $"Azure Function '{functionName}' with {(trigger is null ? "no" : ProviderHelpers.NormalizeAttributeName(trigger))} trigger.",
                Confidence = ConfidenceTiers.Semantic,
                FileName = Path.GetFileName(filePath),
                LineNumber = lineSpan.Line + 1
            }
        };
        results.Services.Add(service);

        var operation = new ServiceOperation
        {
            Id = FrameworkIds.Operation(serviceId, null, null, functionName),
            Name = functionName,
            MethodId = methodId,
            RequestType = methodSymbol?.Parameters.FirstOrDefault()?.Type.ToDisplayString(),
            Confidence = ConfidenceTiers.Semantic,
            Location = CodeLocation.From(ctx.BasePath, filePath, lineSpan.Line + 1)
        };
        service.Operations.Add(operation);

        // Output bindings are egress edges.
        foreach (var output in attributes.Where(attribute => OutputBindingAttributes.Any(outputName => ProviderHelpers.IsNamed(attribute, outputName))))
        {
            var target = ProviderHelpers.AttributeStringArguments(output).FirstOrDefault() ?? ProviderHelpers.NormalizeAttributeName(output);
            service.Tags.Add($"egress:{target}");
        }

        if (trigger is null)
        {
            return;
        }

        var triggerName = ProviderHelpers.NormalizeAttributeName(trigger);
        service.Properties["trigger"] = triggerName;
        var endpoint = new ApiEndpoint
        {
            FilePath = CodeLocation.From(ctx.BasePath, filePath).Path,
            FileName = Path.GetFileName(filePath),
            Namespace = namespaceName,
            ClassName = typeName,
            MethodName = method.Identifier.Text,
            EndpointKind = "AzureFunction",
            RoutingKind = "Attribute",
            Framework = "azure-functions",
            ServiceId = serviceId,
            OperationId = operation.Id,
            Confidence = ConfidenceTiers.Semantic,
            LineNumber = lineSpan.Line + 1,
            ColumnNumber = lineSpan.Character + 1,
            RawUrls = rawUrls,
            Route = functionName,
            Evidence = new AnalysisEvidence { Kind = AnalysisEvidenceKind.FrameworkModel, Source = "azure-functions", Description = $"Azure Function trigger {triggerName}.", Confidence = ConfidenceTiers.Semantic }
        };
        results.EntryPoints.Add(new EntryPoint
        {
            Id = $"ep:{operation.Id}",
            Kind = "AzureFunction",
            MethodId = methodId,
            MethodName = method.Identifier.Text,
            ClassName = typeName,
            Namespace = namespaceName,
            FileName = endpoint.FileName,
            Path = endpoint.FilePath,
            LineNumber = endpoint.LineNumber,
            ColumnNumber = endpoint.ColumnNumber
        });

        if (triggerName.Equals("HttpTrigger", StringComparison.Ordinal))
        {
            var verbs = ProviderHelpers.AttributeStringArguments(trigger);
            var route = ProviderHelpers.AttributeStringArguments(trigger, "Route").FirstOrDefault();
            var authorizationLevel = verbs.FirstOrDefault(verb => verb.Contains("Anonymous", StringComparison.Ordinal) || verb.Contains("Function", StringComparison.Ordinal) || verb.Contains("Admin", StringComparison.Ordinal) || verb.Contains("User", StringComparison.Ordinal) || verb.Contains("System", StringComparison.Ordinal))
                                     ?? ProviderHelpers.AttributeStringArguments(trigger, "AuthLevel").FirstOrDefault();
            var httpVerbs = verbs.Where(verb => verb is "get" or "post" or "put" or "delete" or "patch" or "head" or "options").Select(verb => verb.ToUpperInvariant()).ToList();
            if (httpVerbs.Count == 0)
            {
                httpVerbs.Add("ANY");
            }

            var path = RouteTemplateResolver.Resolve(RouteTemplateResolver.CombinePrefix(routePrefix, route)).Path;
            endpoint.HttpMethod = httpVerbs.Count == 1 ? httpVerbs[0] : "ANY";
            endpoint.Path = path;
            endpoint.Route = RouteTemplateResolver.CombinePrefix(routePrefix, route);
            operation.Path = path;
            operation.HttpMethod = endpoint.HttpMethod;
            if (path is not null && !service.Endpoints.Contains(path, StringComparer.Ordinal))
            {
                service.Endpoints.Add(path);
            }

            var anonymous = string.Equals(authorizationLevel, "Anonymous", StringComparison.OrdinalIgnoreCase);
            endpoint.AllowAnonymous = anonymous;
            endpoint.AuthorizationRequired = !anonymous;
            operation.Authenticated = !anonymous;
            service.Authenticated = !anonymous;
            service.AllowAnonymous = anonymous;
            service.TrustZone = anonymous ? TrustZones.Public : TrustZones.Authenticated;
            service.Properties["authorizationLevel"] = authorizationLevel ?? "Function";
            if (anonymous)
            {
                service.Tags.Add("finding:anonymous-http-trigger");
            }
        }
        else if (triggerName.Equals("TimerTrigger", StringComparison.Ordinal))
        {
            service.ServiceKind = ServiceKinds.Scheduled;
            var cron = ProviderHelpers.AttributeStringArguments(trigger).FirstOrDefault();
            if (cron is not null)
            {
                service.Properties["cron"] = cron;
            }
        }
        else
        {
            // Queue/blob/service-bus/... triggers: entity name from the first string argument.
            var entity = ProviderHelpers.AttributeStringArguments(trigger).FirstOrDefault();
            if (entity is not null)
            {
                service.Properties["entityName"] = entity;
            }
        }

        results.ApiEndpoints.Add(endpoint);

        // Trigger payloads are untrusted input.
        var boundParameters = method.ParameterList.Parameters.Where(parameter =>
            ProviderHelpers.AttributesOf(parameter.AttributeLists).Any(pAttribute => TriggerAttributes.Any(t => ProviderHelpers.IsNamed(pAttribute, t)))).ToList();
        foreach (var parameter in boundParameters.DefaultIfEmpty(method.ParameterList.Parameters.FirstOrDefault()))
        {
            if (parameter is null)
            {
                continue;
            }

            results.TaintSeeds.Add(new FrameworkTaintSeed
            {
                MethodName = method.Identifier.Text,
                ParameterName = parameter.Identifier.Text,
                ClassName = typeName,
                Namespace = namespaceName,
                FileName = Path.GetFileName(filePath),
                MethodSignature = methodId,
                LineNumber = lineSpan.Line + 1,
                BindingSource = "function-payload",
                TaintKind = triggerName.Equals("HttpTrigger", StringComparison.Ordinal) ? "http" : "rpc",
                FrameworkId = "azure-functions",
                EndpointPath = endpoint.Path ?? functionName,
                Confidence = ConfidenceTiers.Semantic
            });
        }
    }

    private static void AnalyzeLambdaAnnotations(FrameworkContext ctx, FrameworkResults results, MethodDeclarationSyntax method, List<AttributeSyntax> attributes, AttributeSyntax lambdaAttribute, SemanticModel model, string filePath, List<string> rawUrls)
    {
        var lineSpan = method.GetLocation().GetLineSpan().StartLinePosition;
        var methodSymbol = model.GetDeclaredSymbol(method);
        var methodId = methodSymbol is null ? null : Depscan.Dosai.FormatMethodSignature(methodSymbol);
        var typeName = method.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text;
        var namespaceName = method.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();
        var serviceName = ProviderHelpers.AttributeStringArguments(lambdaAttribute).FirstOrDefault() ?? method.Identifier.Text;
        var restApi = attributes.FirstOrDefault(attribute => ProviderHelpers.IsNamed(attribute, "RestApi"));
        var httpApi = attributes.FirstOrDefault(attribute => ProviderHelpers.IsNamed(attribute, "HttpApi"));
        var serviceId = FrameworkIds.Service("aws-lambda", namespaceName, serviceName);

        var service = new ServiceComponent
        {
            Id = serviceId,
            Name = serviceName,
            Group = namespaceName,
            ServiceKind = ServiceKinds.Function,
            Direction = ServiceDirections.Inbound,
            Framework = "aws-lambda",
            Confidence = ConfidenceTiers.Semantic,
            Location = CodeLocation.From(ctx.BasePath, filePath, lineSpan.Line + 1),
            Evidence = new AnalysisEvidence { Kind = AnalysisEvidenceKind.FrameworkModel, Source = "aws-lambda", Description = $"[LambdaFunction] handler '{serviceName}'.", Confidence = ConfidenceTiers.Semantic }
        };
        results.Services.Add(service);

        var operation = new ServiceOperation
        {
            Id = FrameworkIds.Operation(serviceId, null, null, serviceName),
            Name = serviceName,
            MethodId = methodId,
            RequestType = methodSymbol?.Parameters.FirstOrDefault()?.Type.ToDisplayString(),
            Confidence = ConfidenceTiers.Semantic,
            Location = CodeLocation.From(ctx.BasePath, filePath, lineSpan.Line + 1)
        };
        service.Operations.Add(operation);
        results.EntryPoints.Add(new EntryPoint
        {
            Id = $"ep:{operation.Id}",
            Kind = "LambdaFunction",
            MethodId = methodId,
            MethodName = method.Identifier.Text,
            ClassName = typeName,
            Namespace = namespaceName,
            FileName = Path.GetFileName(filePath),
            Path = CodeLocation.From(ctx.BasePath, filePath).Path,
            LineNumber = lineSpan.Line + 1,
            ColumnNumber = lineSpan.Character + 1
        });

        var httpAttribute = restApi ?? httpApi;
        if (httpAttribute is not null)
        {
            var route = ProviderHelpers.AttributeStringArguments(httpAttribute).FirstOrDefault() ?? $"/{serviceName}";
            var verbs = ProviderHelpers.AttributeStringArguments(httpAttribute, "Method").DefaultIfEmpty(restApi is not null ? "ANY" : "ANY");
            var path = RouteTemplateResolver.Resolve(route).Path;
            foreach (var verb in verbs.Select(v => v.ToUpperInvariant()).Distinct())
            {
                results.ApiEndpoints.Add(new ApiEndpoint
                {
                    Path = path,
                    FilePath = CodeLocation.From(ctx.BasePath, filePath).Path,
                    FileName = Path.GetFileName(filePath),
                    Namespace = namespaceName,
                    ClassName = typeName,
                    MethodName = method.Identifier.Text,
                    HttpMethod = verb,
                    Route = route,
                    EndpointKind = "LambdaFunction",
                    RoutingKind = "Attribute",
                    Framework = "aws-lambda",
                    ServiceId = serviceId,
                    OperationId = operation.Id,
                    Confidence = ConfidenceTiers.Semantic,
                    LineNumber = lineSpan.Line + 1,
                    ColumnNumber = lineSpan.Character + 1,
                    RawUrls = rawUrls,
                    Evidence = new AnalysisEvidence { Kind = AnalysisEvidenceKind.FrameworkModel, Source = "aws-lambda", Description = $"Lambda HTTP route {route}.", Confidence = ConfidenceTiers.Semantic }
                });
            }

            operation.Path = path;
            if (path is not null)
            {
                service.Endpoints.Add(path);
            }
        }

        results.TaintSeeds.Add(new FrameworkTaintSeed
        {
            MethodName = method.Identifier.Text,
            ParameterName = methodSymbol?.Parameters.FirstOrDefault()?.Name ?? "input",
            ClassName = typeName,
            Namespace = namespaceName,
            FileName = Path.GetFileName(filePath),
            MethodSignature = methodId,
            LineNumber = lineSpan.Line + 1,
            BindingSource = "function-payload",
            TaintKind = "rpc",
            FrameworkId = "aws-lambda",
            EndpointPath = operation.Path ?? serviceName,
            Confidence = ConfidenceTiers.Semantic
        });
    }

    /// <summary>Classic Lambda handlers: a method whose last parameter is ILambdaContext.</summary>
    private static void AnalyzeLambdaHandler(FrameworkContext ctx, FrameworkResults results, MethodDeclarationSyntax method, SemanticModel model, string filePath, List<string> rawUrls)
    {
        var hasContext = method.ParameterList.Parameters.Any(parameter => parameter.Type?.ToString().Contains("ILambdaContext") == true);
        if (!hasContext)
        {
            return;
        }

        var lineSpan = method.GetLocation().GetLineSpan().StartLinePosition;
        var typeName = method.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text;
        var namespaceName = method.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();
        var serviceId = FrameworkIds.Service("aws-lambda", namespaceName, $"{typeName}-{method.Identifier.Text}");
        if (results.Services.Any(existing => existing.Id == serviceId))
        {
            return;
        }

        var methodSymbol = model.GetDeclaredSymbol(method);
        var requestType = methodSymbol?.Parameters.FirstOrDefault()?.Type.ToDisplayString() ?? method.ParameterList.Parameters.FirstOrDefault()?.Type?.ToString();
        var service = new ServiceComponent
        {
            Id = serviceId,
            Name = $"{typeName}.{method.Identifier.Text}",
            Group = namespaceName,
            ServiceKind = ServiceKinds.Function,
            Direction = ServiceDirections.Inbound,
            Framework = "aws-lambda",
            Confidence = ConfidenceTiers.Semantic,
            Location = CodeLocation.From(ctx.BasePath, filePath, lineSpan.Line + 1),
            Evidence = new AnalysisEvidence { Kind = AnalysisEvidenceKind.FrameworkModel, Source = "aws-lambda", Description = "Lambda handler signature (ILambdaContext parameter).", Confidence = ConfidenceTiers.Semantic }
        };
        service.Properties["requestType"] = requestType ?? "unknown";
        if (requestType?.Contains("APIGateway") == true)
        {
            service.Properties["apiGateway"] = "true";
        }

        results.Services.Add(service);
    }

    /// <summary>host.json extensionBundles/routePrefix: the default route prefix is "api".</summary>
    private static string ReadHostJsonRoutePrefix(FrameworkContext ctx)
    {
        foreach (var hostJson in ctx.ConfigFiles.Where(file => Path.GetFileName(file).Equals("host.json", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(hostJson));
                if (document.RootElement.TryGetProperty("extensions", out var extensions) &&
                    extensions.TryGetProperty("http", out var http) &&
                    http.TryGetProperty("routePrefix", out var routePrefix) &&
                    routePrefix.ValueKind == JsonValueKind.String)
                {
                    return string.IsNullOrEmpty(routePrefix.GetString()) ? string.Empty : routePrefix.GetString()!;
                }
            }
            catch (Exception)
            {
                // Malformed host.json: fall back to the default prefix.
            }
        }

        return "api";
    }

    /// <summary>function.json files (non-C# workers) carry bindings directly.</summary>
    private static void AnalyzeFunctionJson(FrameworkContext ctx, FrameworkResults results)
    {
        foreach (var functionJson in ctx.ConfigFiles.Where(file => Path.GetFileName(file).Equals("function.json", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(functionJson));
                if (!document.RootElement.TryGetProperty("bindings", out var bindings))
                {
                    continue;
                }

                string? httpRoute = null;
                var triggerType = "unknown";
                foreach (var binding in bindings.EnumerateArray())
                {
                    var type = binding.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
                    var direction = binding.TryGetProperty("direction", out var directionElement) ? directionElement.GetString() : null;
                    if (direction == "in" && type?.EndsWith("Trigger", StringComparison.Ordinal) == true)
                    {
                        triggerType = type;
                        if (type == "httpTrigger" && binding.TryGetProperty("route", out var routeElement))
                        {
                            httpRoute = routeElement.GetString();
                        }
                    }
                }

                var directory = Path.GetFileName(Path.GetDirectoryName(functionJson)) ?? "function";
                var serviceId = FrameworkIds.Service("azure-functions", null, directory);
                var service = new ServiceComponent
                {
                    Id = serviceId,
                    Name = directory,
                    ServiceKind = ServiceKinds.Function,
                    Direction = ServiceDirections.Inbound,
                    Framework = "azure-functions",
                    Confidence = ConfidenceTiers.Heuristic,
                    Location = CodeLocation.From(ctx.BasePath, functionJson),
                    Evidence = new AnalysisEvidence { Kind = AnalysisEvidenceKind.FrameworkModel, Source = "azure-functions", Description = $"function.json with {triggerType}.", Confidence = ConfidenceTiers.Heuristic }
                };
                service.Properties["trigger"] = triggerType;
                if (httpRoute is not null)
                {
                    var path = RouteTemplateResolver.Resolve(RouteTemplateResolver.CombinePrefix("api", httpRoute)).Path;
                    service.Endpoints.Add(path ?? $"/api/{httpRoute}");
                }

                results.Services.Add(service);
            }
            catch (Exception)
            {
                // Malformed function.json: best-effort skip.
            }
        }
    }
}
