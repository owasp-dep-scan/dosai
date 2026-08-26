using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Depscan.Frameworks.Providers;

/// <summary>
///     Messaging consumers and publishers: MassTransit (IConsumer&lt;T&gt;), NServiceBus
///     (IHandleMessages&lt;T&gt;), MediatR (IRequestHandler&lt;TReq,TRes&gt; / INotificationHandler&lt;T&gt;),
///     Rebus, Dapr, Kafka (Confluent), Azure Service Bus, and RabbitMQ. Consumers are inbound
///     services with taint seeds; publishers are outbound services grouped by containing class.
/// </summary>
public sealed class MessagingProvider : IFrameworkProvider
{
    private static readonly (string InterfaceName, string Framework, string HandleMethod)[] ConsumerInterfaces =
    [
        ("IConsumer", "masstransit", "Consume"),
        ("IHandleMessages", "nservicebus", "Handle"),
        ("IRequestHandler", "mediatr", "Handle"),
        ("INotificationHandler", "mediatr", "Handle"),
        ("IHandleRequests", "rebus", "Handle")
    ];

    public string Id => "messaging";

    public string DisplayName => "Messaging (MassTransit, NServiceBus, MediatR, Rebus, Dapr, Kafka, Service Bus, RabbitMQ)";

    public bool AppliesTo(FrameworkContext ctx) => ctx.CSharp is not null;

    public void Analyze(FrameworkContext ctx, FrameworkResults results)
    {
        try
        {
            AnalyzeConsumers(ctx, results);
            AnalyzePublishers(ctx, results);
            AnalyzeRawClients(ctx, results);
        }
        catch (Exception ex)
        {
            ctx.Diagnostics.Add(new FrameworkDiagnostic("messaging", $"Messaging analysis failed: {ex.Message}"));
        }
    }

    // ---- Consumers (inbound) ----
    private static void AnalyzeConsumers(FrameworkContext ctx, FrameworkResults results)
    {
foreach (var tree in ctx.CSharpTrees)
        {
            if (!ctx.TextContainsAny(tree, "IConsumer", "IHandleMessages", "IRequestHandler", "INotificationHandler", "IBasicConsumer", "ReceiveEndpoint", "ServiceBusProcessor", "BasicConsume", "Publish", "InvokeMethodAsync", "ConsumeContext"))
            {
                continue;
            }

            var model = ctx.CSharp!.GetSemanticModel(tree);
            var root = tree.GetCompilationUnitRoot();
            var rawUrls = ctx.RawUrlsFor(tree);

            foreach (var typeDeclaration in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                var symbol = model.GetDeclaredSymbol(typeDeclaration);
                var typeName = typeDeclaration.Identifier.Text;
                var namespaceName = typeDeclaration.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();
                var filePath = tree.FilePath;

                // Check semantic interfaces
                string? matchedFramework = null;
                string? matchedInterface = null;
                string? handleMethodName = null;
                string? messageType = null;
                var semantic = false;

                if (symbol is not null)
                {
                    foreach (var (ifaceName, framework, handleMethod) in ConsumerInterfaces)
                    {
                        if (ProviderHelpers.ImplementsAny(symbol, ifaceName))
                        {
                            matchedFramework = framework;
                            matchedInterface = ifaceName;
                            handleMethodName = handleMethod;
                            // Get the message type argument
                            var iface = symbol.AllInterfaces.FirstOrDefault(i => i.Name.Equals(ifaceName, StringComparison.Ordinal));
                            if (iface?.TypeArguments.Length > 0)
                            {
                                messageType = iface.TypeArguments[0].ToDisplayString();
                            }
                            else if (iface?.TypeArguments.Length > 1)
                            {
                                messageType = iface.TypeArguments[0].ToDisplayString();
                            }

                            semantic = true;
                            break;
                        }
                    }
                }

                // Syntactic fallback: base list type name ends with interface name
                if (matchedFramework is null)
                {
                    var baseList = (typeDeclaration as ClassDeclarationSyntax)?.BaseList;
                    if (baseList is not null)
                    {
                        foreach (var baseType in baseList.Types)
                        {
                            var baseText = baseType.Type.ToString();
                            foreach (var (ifaceName, framework, interfaceHandleMethod) in ConsumerInterfaces)
                            {
                                // Match IConsumer<T>, IConsumer<,,>, etc. or just IConsumer
                                if (baseText.StartsWith(ifaceName, StringComparison.Ordinal) &&
                                    (baseText.Length == ifaceName.Length || baseText[ifaceName.Length] == '<'))
                                {
                                    matchedFramework = framework;
                                    matchedInterface = ifaceName;
                                    handleMethodName = interfaceHandleMethod;
                                    // Extract type argument text
                                    var ltIndex = baseText.IndexOf('<');
                                    if (ltIndex >= 0 && baseText.EndsWith('>') )
                                    {
                                        messageType = baseText[(ltIndex + 1)..^1].Split(',')[0].Trim();
                                    }
                                    break;
                                }
                            }

                            if (matchedFramework is not null) break;
                        }
                    }
                }

                if (matchedFramework is null)
                {
                    continue;
                }

                var confidence = semantic ? ConfidenceTiers.Semantic : ConfidenceTiers.Syntactic;
                var serviceId = FrameworkIds.Service("messaging", namespaceName, typeName);
                var lineSpan = typeDeclaration.GetLocation().GetLineSpan().StartLinePosition;
                // Claim the type so the raw-client scan below cannot double-report it (Kafka's
                // IConsumer and MassTransit's IConsumer share a name).
                ctx.HandledTypeIds.Add($"{filePath}:{typeName}");

                var service = new ServiceComponent
                {
                    Id = serviceId,
                    Name = typeName,
                    Group = namespaceName,
                    ServiceKind = ServiceKinds.Queue,
                    Direction = ServiceDirections.Inbound,
                    Framework = "messaging",
                    Confidence = confidence,
                    Location = CodeLocation.From(ctx.BasePath, filePath, lineSpan.Line + 1, lineSpan.Character + 1),
                    Evidence = new AnalysisEvidence
                    {
                        Kind = AnalysisEvidenceKind.FrameworkModel,
                        Source = "messaging",
                        Description = $"Implements {matchedInterface} ({matchedFramework}).",
                        Confidence = confidence,
                        FileName = Path.GetFileName(filePath),
                        LineNumber = lineSpan.Line + 1
                    }
                };

                service.Properties["framework"] = matchedFramework;
                if (messageType is not null)
                {
                    service.Properties["messageType"] = messageType;
                }

                // Look for entity name: MassTransit ReceiveEndpoint("name", ...), [EntityName("...")]
                string? entityName = null;
                var fileText = ctx.TextFor(tree);

                // [EntityName("...")] on class
                var entityNameAttr = ProviderHelpers.AttributesOf(typeDeclaration.AttributeLists)
                    .FirstOrDefault(a => ProviderHelpers.IsNamed(a, "EntityName"));
                if (entityNameAttr is not null)
                {
                    entityName = ProviderHelpers.AttributeArgumentText(entityNameAttr, model);
                }

                // Look for ReceiveEndpoint("queue-name", ...) referencing this consumer type in the same file
                if (entityName is null)
                {
                    foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                    {
                        if (ProviderHelpers.InvocationName(invocation).Equals("ReceiveEndpoint", StringComparison.Ordinal))
                        {
                            var queueArg = ProviderHelpers.StringArguments(invocation).FirstOrDefault();
                            if (queueArg is not null)
                            {
                                // Check if the lambda body references this consumer type
                                var invocationText = invocation.ArgumentList.Arguments
                                    .Select(a => a.Expression.ToString())
                                    .Any(text => text.Contains(typeName, StringComparison.Ordinal));
                                if (invocationText)
                                {
                                    entityName = queueArg;
                                    break;
                                }
                            }
                        }
                    }
                }

                if (entityName is not null)
                {
                    service.Properties["entityName"] = entityName;
                    if (!service.Endpoints.Contains($"/queue/{entityName}", StringComparer.Ordinal))
                    {
                        service.Endpoints.Add($"/queue/{entityName}");
                    }
                }

                // Find the Handle/Consume method
                foreach (var method in typeDeclaration.Members.OfType<MethodDeclarationSyntax>())
                {
                    var actualMethodName = handleMethodName ?? method.Identifier.Text;
                    if (!method.Identifier.Text.Equals(actualMethodName, StringComparison.Ordinal) &&
                        !method.Identifier.Text.Equals("Consume", StringComparison.Ordinal) &&
                        !method.Identifier.Text.Equals("Handle", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var methodLineSpan = method.GetLocation().GetLineSpan().StartLinePosition;
                    var methodSymbol = model.GetDeclaredSymbol(method);
                    var methodId = methodSymbol is null ? null : Depscan.Dosai.FormatMethodSignature(methodSymbol);
                    var operationName = handleMethodName ?? method.Identifier.Text;
                    var operationId = FrameworkIds.Operation(serviceId, null, entityName is not null ? $"/queue/{entityName}" : null, operationName);

                    var operation = new ServiceOperation
                    {
                        Id = operationId,
                        Name = operationName,
                        RequestType = messageType ?? methodSymbol?.Parameters.FirstOrDefault()?.Type.ToDisplayString(),
                        MethodId = methodId,
                        Confidence = confidence,
                        Location = CodeLocation.From(ctx.BasePath, filePath, methodLineSpan.Line + 1, methodLineSpan.Character + 1)
                    };
                    service.Operations.Add(operation);

                    if (methodId is not null && !service.MethodIds.Contains(methodId, StringComparer.Ordinal))
                    {
                        service.MethodIds.Add(methodId);
                    }

                    // ApiEndpoint
                    var endpoint = new ApiEndpoint
                    {
                        FilePath = CodeLocation.From(ctx.BasePath, filePath).Path,
                        FileName = Path.GetFileName(filePath),
                        Namespace = namespaceName,
                        ClassName = typeName,
                        MethodName = method.Identifier.Text,
                        HttpMethod = null,
                        EndpointKind = "MessageConsumer",
                        RoutingKind = "Attribute",
                        Path = entityName is not null ? $"/queue/{entityName}" : null,
                        Framework = "messaging",
                        ServiceId = serviceId,
                        OperationId = operationId,
                        Confidence = ConfidenceTiers.Syntactic,
                        LineNumber = methodLineSpan.Line + 1,
                        ColumnNumber = methodLineSpan.Character + 1,
                        RawUrls = rawUrls,
                        Evidence = new AnalysisEvidence
                        {
                            Kind = AnalysisEvidenceKind.FrameworkModel,
                            Source = "messaging",
                            Description = $"Message consumer ({matchedFramework}).",
                            Confidence = confidence,
                            FileName = Path.GetFileName(filePath),
                            LineNumber = methodLineSpan.Line + 1
                        }
                    };
                    results.ApiEndpoints.Add(endpoint);

                    // EntryPoint
                    results.EntryPoints.Add(new EntryPoint
                    {
                        Id = $"ep:{operationId}",
                        Kind = "MessageConsumer",
                        MethodId = methodId,
                        MethodName = method.Identifier.Text,
                        ClassName = typeName,
                        Namespace = namespaceName,
                        FileName = Path.GetFileName(filePath),
                        Path = endpoint.FilePath,
                        LineNumber = methodLineSpan.Line + 1,
                        ColumnNumber = methodLineSpan.Character + 1,
                        Route = entityName is not null ? $"/queue/{entityName}" : null,
                        RawUrls = rawUrls
                    });

                    // Taint seeds: all method parameters are untrusted message payloads
                    foreach (var parameter in method.ParameterList.Parameters)
                    {
                        if (parameter.Type?.ToString().Contains("CancellationToken", StringComparison.Ordinal) == true)
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
                            LineNumber = methodLineSpan.Line + 1,
                            BindingSource = "queue-message",
                            TaintKind = "rpc",
                            FrameworkId = "messaging",
                            EndpointPath = entityName is not null ? $"/queue/{entityName}" : string.Empty,
                            Confidence = methodSymbol is not null ? ConfidenceTiers.Semantic : ConfidenceTiers.Syntactic
                        });
                    }

                    service.EntryPointIds.Add($"ep:{operationId}");
                    break; // One Handle/Consume method per consumer
                }

                results.Services.Add(service);
            }
        }
    }

    // ---- Publishers (outbound) ----
    private static void AnalyzePublishers(FrameworkContext ctx, FrameworkResults results)
    {
        var publisherClassServices = new Dictionary<string, ServiceComponent>(StringComparer.Ordinal);

foreach (var tree in ctx.CSharpTrees)
        {
            if (!ctx.TextContainsAny(tree, "IConsumer", "IHandleMessages", "IRequestHandler", "INotificationHandler", "IBasicConsumer", "ReceiveEndpoint", "ServiceBusProcessor", "BasicConsume", "Publish", "InvokeMethodAsync", "ConsumeContext"))
            {
                continue;
            }

            var model = ctx.CSharp!.GetSemanticModel(tree);
            var root = tree.GetCompilationUnitRoot();

            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var name = ProviderHelpers.InvocationName(invocation);
                string? publishFramework = null;
                string? requestType = null;

                // IPublishEndpoint.Publish, ISendEndpoint.Send
                if (name.Equals("Publish", StringComparison.Ordinal) || name.Equals("Send", StringComparison.Ordinal))
                {
                    publishFramework = "masstransit";
                }
                // DaprClient.InvokeMethodAsync
                else if (name.Equals("InvokeMethodAsync", StringComparison.Ordinal))
                {
                    publishFramework = "dapr";
                }
                // producer.Send / kafkaProducer.ProduceAsync
                else if (name.Equals("ProduceAsync", StringComparison.Ordinal))
                {
                    publishFramework = "kafka";
                }
                else if (name.Equals("SendAsync", StringComparison.Ordinal) || name.Equals("ScheduleMessageAsync", StringComparison.Ordinal))
                {
                    publishFramework = "azure-servicebus";
                }

                if (publishFramework is null)
                {
                    continue;
                }

                // Try to get the request type from the generic argument
                var memberAccess = invocation.Expression as MemberAccessExpressionSyntax;
                if (memberAccess?.Name is GenericNameSyntax genericName && genericName.TypeArgumentList.Arguments.Count > 0)
                {
                    requestType = genericName.TypeArgumentList.Arguments[0].ToString();
                }
                else if (invocation.ArgumentList.Arguments.Count > 0)
                {
                    var firstArg = invocation.ArgumentList.Arguments[0].Expression;
                    requestType = firstArg switch
                    {
                        ObjectCreationExpressionSyntax obj => obj.Type.ToString(),
                        InvocationExpressionSyntax inv => (inv.Expression as MemberAccessExpressionSyntax)?.Name.ToString(),
                        _ => firstArg.ToString()
                    };
                }

                // Group by containing class
                var containingClass = invocation.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
                if (containingClass is null)
                {
                    continue;
                }

                var className = containingClass.Identifier.Text;
                var ns = containingClass.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();
                var classKey = $"{ns}.{className}";

                if (!publisherClassServices.TryGetValue(classKey, out var publisherService))
                {
                    var serviceId = FrameworkIds.Service("messaging", ns, className);
                    var lineSpan = containingClass.GetLocation().GetLineSpan().StartLinePosition;
                    publisherService = new ServiceComponent
                    {
                        Id = serviceId,
                        Name = className,
                        Group = ns,
                        ServiceKind = ServiceKinds.PubSub,
                        Direction = ServiceDirections.Outbound,
                        Framework = "messaging",
                        Confidence = ConfidenceTiers.Syntactic,
                        Location = CodeLocation.From(ctx.BasePath, tree.FilePath, lineSpan.Line + 1, lineSpan.Character + 1),
                        Evidence = new AnalysisEvidence
                        {
                            Kind = AnalysisEvidenceKind.FrameworkModel,
                            Source = "messaging",
                            Description = $"Message publisher ({publishFramework}).",
                            Confidence = ConfidenceTiers.Syntactic,
                            FileName = Path.GetFileName(tree.FilePath),
                            LineNumber = lineSpan.Line + 1
                        }
                    };
                    publisherService.Properties["framework"] = publishFramework;
                    results.Services.Add(publisherService);
                    publisherClassServices[classKey] = publisherService;
                }

                var invocationLineSpan = invocation.GetLocation().GetLineSpan().StartLinePosition;
                var operationId = FrameworkIds.Operation(publisherService.Id, null, null, name);

                publisherService.Operations.Add(new ServiceOperation
                {
                    Id = operationId,
                    Name = name,
                    RequestType = requestType,
                    Confidence = ConfidenceTiers.Syntactic,
                    Location = CodeLocation.From(ctx.BasePath, tree.FilePath, invocationLineSpan.Line + 1, invocationLineSpan.Character + 1)
                });

                // Anchor the publishing method so trust-boundary analysis can match call-graph paths.
                var enclosingMethod = model.GetEnclosingSymbol(invocation.SpanStart) as IMethodSymbol;
                if (enclosingMethod is not null)
                {
                    var enclosingId = Depscan.Dosai.FormatMethodSignature(enclosingMethod);
                    if (!publisherService.MethodIds.Contains(enclosingId, StringComparer.Ordinal))
                    {
                        publisherService.MethodIds.Add(enclosingId);
                    }
                }
            }
        }
    }

    // ---- Raw Kafka / RabbitMQ / Azure SB consumers ----
    private static void AnalyzeRawClients(FrameworkContext ctx, FrameworkResults results)
    {
foreach (var tree in ctx.CSharpTrees)
        {
            if (!ctx.TextContainsAny(tree, "IConsumer", "IHandleMessages", "IRequestHandler", "INotificationHandler", "IBasicConsumer", "ReceiveEndpoint", "ServiceBusProcessor", "BasicConsume", "Publish", "InvokeMethodAsync", "ConsumeContext"))
            {
                continue;
            }

            var model = ctx.CSharp!.GetSemanticModel(tree);
            var root = tree.GetCompilationUnitRoot();
            var rawUrls = ctx.RawUrlsFor(tree);

            foreach (var typeDeclaration in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                var symbol = model.GetDeclaredSymbol(typeDeclaration);
                var typeName = typeDeclaration.Identifier.Text;
                var namespaceName = typeDeclaration.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();
                var filePath = tree.FilePath;

                string? rawFramework = null;
                string? entityName = null;

                // Skip types the consumer scan above already claimed.
                if (ctx.HandledTypeIds.Contains($"{filePath}:{typeName}"))
                {
                    continue;
                }

                // Confluent Kafka: IConsumer<TKey, TValue> implementations
                if (symbol is not null && ProviderHelpers.ImplementsAny(symbol, "IConsumer"))
                {
                    rawFramework = "kafka";
                    // Try to extract topic from .Subscribe("topic") calls in the same type
                    foreach (var invocation in typeDeclaration.DescendantNodes().OfType<InvocationExpressionSyntax>())
                    {
                        if (ProviderHelpers.InvocationName(invocation).Equals("Subscribe", StringComparison.Ordinal))
                        {
                            var topicArg = ProviderHelpers.StringArguments(invocation).FirstOrDefault();
                            if (topicArg is not null)
                            {
                                entityName = topicArg;
                                break;
                            }
                        }
                    }
                }

                // RabbitMQ: IModel.BasicConsume(queue: ...) or IBasicConsumer implementations
                if (rawFramework is null && symbol is not null && ProviderHelpers.ImplementsAny(symbol, "IBasicConsumer"))
                {
                    rawFramework = "rabbitmq";
                    foreach (var invocation in typeDeclaration.DescendantNodes().OfType<InvocationExpressionSyntax>())
                    {
                        if (ProviderHelpers.InvocationName(invocation).Equals("BasicConsume", StringComparison.Ordinal))
                        {
                            // Look for named argument "queue" or first string argument
                            var queueArg = invocation.ArgumentList.Arguments
                                .Where(a => string.Equals(a.NameColon?.Name.Identifier.Text, "queue", StringComparison.OrdinalIgnoreCase))
                                .Select(a => a.Expression)
                                .OfType<LiteralExpressionSyntax>()
                                .FirstOrDefault(l => l.Token.Value is string);
                            if (queueArg is not null)
                            {
                                entityName = (string)queueArg.Token.Value!;
                            }
                            else
                            {
                                entityName = ProviderHelpers.StringArguments(invocation).FirstOrDefault();
                            }

                            break;
                        }
                    }
                }

                // ServiceBusProcessor: check for constructor or field with queue name
                if (rawFramework is null)
                {
                    var fileText = ctx.TextFor(tree);
                    var typeText = typeDeclaration.ToFullString();
                    if (typeText.Contains("ServiceBusProcessor", StringComparison.Ordinal) ||
                        typeText.Contains("ServiceBusClient", StringComparison.Ordinal))
                    {
                        // Look for ServiceBusProcessor("queue-name") or similar patterns
                        foreach (var invocation in typeDeclaration.DescendantNodes().OfType<InvocationExpressionSyntax>())
                        {
                            var invokedName = ProviderHelpers.InvocationName(invocation);
                            if (invokedName.Contains("ServiceBusProcessor", StringComparison.Ordinal) ||
                                invokedName.Equals("AddServiceBusClient", StringComparison.Ordinal))
                            {
                                var queueArg = ProviderHelpers.StringArguments(invocation).FirstOrDefault();
                                if (queueArg is not null)
                                {
                                    rawFramework = "azure-servicebus";
                                    entityName = queueArg;
                                    break;
                                }
                            }
                        }
                    }
                }

                if (rawFramework is null)
                {
                    continue;
                }

                var confidence = symbol is not null ? ConfidenceTiers.Semantic : ConfidenceTiers.Syntactic;
                var serviceId = FrameworkIds.Service("messaging", namespaceName, typeName);
                var lineSpan = typeDeclaration.GetLocation().GetLineSpan().StartLinePosition;

                var service = new ServiceComponent
                {
                    Id = serviceId,
                    Name = typeName,
                    Group = namespaceName,
                    ServiceKind = ServiceKinds.Queue,
                    Direction = ServiceDirections.Inbound,
                    Framework = "messaging",
                    Confidence = ConfidenceTiers.Syntactic,
                    Location = CodeLocation.From(ctx.BasePath, filePath, lineSpan.Line + 1, lineSpan.Character + 1),
                    Evidence = new AnalysisEvidence
                    {
                        Kind = AnalysisEvidenceKind.FrameworkModel,
                        Source = "messaging",
                        Description = $"Raw messaging client ({rawFramework}).",
                        Confidence = ConfidenceTiers.Syntactic,
                        FileName = Path.GetFileName(filePath),
                        LineNumber = lineSpan.Line + 1
                    }
                };

                service.Properties["framework"] = rawFramework;
                if (entityName is not null)
                {
                    service.Properties["entityName"] = entityName;
                    if (!service.Endpoints.Contains($"/queue/{entityName}", StringComparer.Ordinal))
                    {
                        service.Endpoints.Add($"/queue/{entityName}");
                    }
                }

                results.Services.Add(service);
            }
        }
    }
}
