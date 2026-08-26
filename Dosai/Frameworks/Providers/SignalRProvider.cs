using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Depscan.Frameworks.Providers;

/// <summary>
///     ASP.NET Core SignalR: Hub and Hub&lt;T&gt; server-side hubs with mount-point association
///     from MinimalApiProvider's MapHub recordings, client-side HubConnectionBuilder.WithUrl
///     outbound detection, and IHubContext&lt;T&gt; usage linking.
/// </summary>
public sealed class SignalRProvider : IFrameworkProvider
{
    public string Id => "signalr";

    public string DisplayName => "ASP.NET Core SignalR";

    public bool AppliesTo(FrameworkContext ctx) => ctx.CSharp is not null;

    public void Analyze(FrameworkContext ctx, FrameworkResults results)
    {
        try
        {
            var hubMounts = ctx.MountPoints
                .Where(mp => mp.Kind == "hub")
                .Select(mp => mp.Path)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var hubContextTypes = new HashSet<string>(StringComparer.Ordinal);
            var hubServices = new Dictionary<string, ServiceComponent>(StringComparer.Ordinal);

            foreach (var tree in ctx.CSharpTrees)
            {
                var model = ctx.CSharp!.GetSemanticModel(tree);
                var root = tree.GetCompilationUnitRoot();
                var fileText = ctx.TextFor(tree);
                var rawUrls = ProviderHelpers.ExtractRawUrls(fileText);

                // ---- Server-side hubs ----
                foreach (var typeDeclaration in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
                {
                    var symbol = model.GetDeclaredSymbol(typeDeclaration);
                    if (symbol is null && typeDeclaration is not ClassDeclarationSyntax)
                    {
                        continue;
                    }

                    var isHub = symbol is not null && ProviderHelpers.DerivesFromAny(symbol, "Hub");
                    if (!isHub && symbol is null)
                    {
                        // Syntactic fallback: check base list for Hub or Hub<T>
                        var baseList = (typeDeclaration as ClassDeclarationSyntax)?.BaseList;
                        if (baseList is null)
                        {
                            continue;
                        }

                        isHub = baseList.Types.Any(baseType =>
                        {
                            var name = baseType.Type.ToString();
                            return name.Equals("Hub", StringComparison.Ordinal) ||
                                   name.StartsWith("Hub<", StringComparison.Ordinal);
                        });
                    }

                    if (!isHub)
                    {
                        continue;
                    }

                    var confidence = symbol is not null ? ConfidenceTiers.Semantic : ConfidenceTiers.Syntactic;
                    var hubName = typeDeclaration.Identifier.Text;
                    var namespaceName = typeDeclaration.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();
                    // Doc/sample suites reuse namespace+hub names across projects; the source
                    // directory keeps service ids (and therefore operation/entry-point ids) unique.
                    var hubDirectory = Path.GetDirectoryName(CodeLocation.From(ctx.BasePath, tree.FilePath).Path)?.Replace(Path.DirectorySeparatorChar, '.');
                    var serviceId = FrameworkIds.Service("signalr", string.IsNullOrWhiteSpace(hubDirectory) ? namespaceName : $"{namespaceName}.{hubDirectory}", hubName);
                    var lineSpan = typeDeclaration.GetLocation().GetLineSpan().StartLinePosition;

                    var service = new ServiceComponent
                    {
                        Id = serviceId,
                        Name = hubName,
                        Group = namespaceName,
                        ServiceKind = ServiceKinds.WebSocket,
                        Direction = ServiceDirections.Bidirectional,
                        Framework = "signalr",
                        FrameworkVersion = ctx.Detection["signalr"]?.Version,
                        Purl = ctx.Detection["signalr"]?.Purl,
                        Confidence = confidence,
                        Location = CodeLocation.From(ctx.BasePath, tree.FilePath, lineSpan.Line + 1, lineSpan.Character + 1),
                        Evidence = new AnalysisEvidence
                        {
                            Kind = AnalysisEvidenceKind.FrameworkModel,
                            Source = "signalr",
                            Description = confidence == ConfidenceTiers.Semantic
                                ? "Type derives from Hub (symbol resolved)."
                                : "Base list contains Hub (name match; references unresolved).",
                            Confidence = confidence,
                            FileName = Path.GetFileName(tree.FilePath),
                            LineNumber = lineSpan.Line + 1
                        }
                    };

                    // Authorization from class-level attributes
                    var classAttributes = ProviderHelpers.AttributesOf(typeDeclaration.AttributeLists).ToList();
                    var allowAnonymous = classAttributes.Any(a => ProviderHelpers.IsNamed(a, "AllowAnonymous"));
                    var hasAuthorize = classAttributes.Any(a => ProviderHelpers.IsNamed(a, "Authorize") && !ProviderHelpers.IsNamed(a, "AllowAnonymous"));
                    if (allowAnonymous)
                    {
                        service.AllowAnonymous = true;
                        service.Authenticated = false;
                        service.TrustZone = TrustZones.Public;
                    }
                    else if (hasAuthorize)
                    {
                        service.Authenticated = true;
                        service.TrustZone = TrustZones.Authenticated;
                    }

                    // Mount points: attach all hub mounts (multiple hubs can share the same mount in some patterns)
                    if (hubMounts.Count > 0)
                    {
                        foreach (var mountPath in hubMounts)
                        {
                            if (!service.Endpoints.Contains(mountPath, StringComparer.Ordinal))
                            {
                                service.Endpoints.Add(mountPath);
                            }
                        }
                    }

                    // Public methods become operations
                    foreach (var method in typeDeclaration.Members.OfType<MethodDeclarationSyntax>())
                    {
                        if (!method.Modifiers.Any(m => m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PublicKeyword)))
                        {
                            continue;
                        }

                        // Skip constructor, Dispose, etc.
                        if (method.Identifier.Text.Equals("Dispose", StringComparison.Ordinal) ||
                            typeDeclaration.Identifier.Text.Equals(method.Identifier.Text, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        var methodAttributes = ProviderHelpers.AttributesOf(method.AttributeLists).ToList();
                        var methodName = method.Identifier.Text;

                        // [HubMethodName("alias")] renames the method
                        var hubMethodNameAttr = methodAttributes.FirstOrDefault(a => ProviderHelpers.IsNamed(a, "HubMethodName"));
                        var operationName = hubMethodNameAttr is not null
                            ? ProviderHelpers.AttributeArgumentText(hubMethodNameAttr, model) ?? methodName
                            : methodName;

                        var methodLineSpan = method.GetLocation().GetLineSpan().StartLinePosition;
                        var methodSymbol = model.GetDeclaredSymbol(method);
                        var methodId = methodSymbol is null ? null : Depscan.Dosai.FormatMethodSignature(methodSymbol);
                        var operationId = FrameworkIds.Operation(serviceId, null, null, operationName);

                        var operation = new ServiceOperation
                        {
                            Id = operationId,
                            Name = operationName,
                            MethodId = methodId,
                            Confidence = confidence,
                            Location = CodeLocation.From(ctx.BasePath, tree.FilePath, methodLineSpan.Line + 1, methodLineSpan.Character + 1)
                        };
                        service.Operations.Add(operation);

                        // Method-level auth
                        var methodAllowAnonymous = methodAttributes.Any(a => ProviderHelpers.IsNamed(a, "AllowAnonymous"));
                        var methodHasAuthorize = methodAttributes.Any(a => ProviderHelpers.IsNamed(a, "Authorize") && !ProviderHelpers.IsNamed(a, "AllowAnonymous"));
                        var opAuthenticated = methodAllowAnonymous ? false : methodHasAuthorize ? true : (bool?)null;
                        operation.Authenticated = opAuthenticated;

                        if (methodId is not null && !service.MethodIds.Contains(methodId, StringComparer.Ordinal))
                        {
                            service.MethodIds.Add(methodId);
                        }

                        // ApiEndpoint per hub method
                        var mountPath = hubMounts.Count > 0 ? hubMounts[0] : null;
                        var endpoint = new ApiEndpoint
                        {
                            FilePath = CodeLocation.From(ctx.BasePath, tree.FilePath).Path,
                            FileName = Path.GetFileName(tree.FilePath),
                            Namespace = namespaceName,
                            ClassName = hubName,
                            MethodName = methodName,
                            HttpMethod = null,
                            EndpointKind = "SignalRHub",
                            RoutingKind = "Mount",
                            Path = mountPath,
                            Framework = "signalr",
                            ServiceId = serviceId,
                            OperationId = operationId,
                            Confidence = mountPath is not null ? ConfidenceTiers.Heuristic : confidence,
                            LineNumber = methodLineSpan.Line + 1,
                            ColumnNumber = methodLineSpan.Character + 1,
                            RawUrls = rawUrls,
                            Evidence = new AnalysisEvidence
                            {
                                Kind = AnalysisEvidenceKind.FrameworkModel,
                                Source = "signalr",
                                Description = "SignalR hub method.",
                                Confidence = confidence,
                                FileName = Path.GetFileName(tree.FilePath),
                                LineNumber = methodLineSpan.Line + 1
                            }
                        };
                        results.ApiEndpoints.Add(endpoint);

                        // EntryPoint
                        results.EntryPoints.Add(new EntryPoint
                        {
                            Id = $"ep:{operationId}",
                            Kind = "SignalRHub",
                            MethodId = methodId,
                            MethodName = methodName,
                            ClassName = hubName,
                            Namespace = namespaceName,
                            FileName = Path.GetFileName(tree.FilePath),
                            Path = endpoint.FilePath,
                            LineNumber = methodLineSpan.Line + 1,
                            ColumnNumber = methodLineSpan.Character + 1,
                            Route = mountPath,
                            AuthorizationRequired = opAuthenticated,
                            AllowAnonymous = methodAllowAnonymous,
                            RawUrls = rawUrls
                        });

                        // Taint seeds for hub method parameters (all params are untrusted from the client)
                        foreach (var parameter in method.ParameterList.Parameters)
                        {
                            // CancellationToken is DI-injected, not from client
                            if (parameter.Type?.ToString().Contains("CancellationToken", StringComparison.Ordinal) == true)
                            {
                                continue;
                            }

                            results.TaintSeeds.Add(new FrameworkTaintSeed
                            {
                                MethodName = methodName,
                                ParameterName = parameter.Identifier.Text,
                                ClassName = hubName,
                                Namespace = namespaceName,
                                FileName = Path.GetFileName(tree.FilePath),
                                MethodSignature = methodId,
                                LineNumber = methodLineSpan.Line + 1,
                                BindingSource = "websocket-message",
                                TaintKind = "websocket",
                                FrameworkId = "signalr",
                                EndpointPath = mountPath ?? string.Empty,
                                Confidence = methodSymbol is not null ? ConfidenceTiers.Semantic : ConfidenceTiers.Syntactic
                            });
                        }

                        service.EntryPointIds.Add($"ep:{operationId}");
                    }

                    results.Services.Add(service);
                    hubServices[serviceId] = service;

                    // Record hub type name for IHubContext<T> matching
                    hubContextTypes.Add(hubName);
                }

                // ---- Client-side: HubConnectionBuilder().WithUrl("...") ----
                foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    var name = ProviderHelpers.InvocationName(invocation);
                    if (!name.Equals("WithUrl", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var urlArg = ProviderHelpers.StringArguments(invocation).FirstOrDefault();
                    if (urlArg is null)
                    {
                        continue;
                    }

                    var containingClass = invocation.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
                    var className = containingClass?.Identifier.Text ?? "AnonymousClient";
                    var ns = containingClass?.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();
                    var clientDirectory = Path.GetDirectoryName(CodeLocation.From(ctx.BasePath, tree.FilePath).Path)?.Replace(Path.DirectorySeparatorChar, '.');
                    var clientServiceId = FrameworkIds.Service("signalr", string.IsNullOrWhiteSpace(clientDirectory) ? ns : $"{ns}.{clientDirectory}", className);
                    var existingClient = results.Services.FirstOrDefault(s => s.Id == clientServiceId);
                    if (existingClient is not null)
                    {
                        continue;
                    }

                    var clientService = new ServiceComponent
                    {
                        Id = clientServiceId,
                        Name = className,
                        Group = ns,
                        ServiceKind = ServiceKinds.WebSocket,
                        Direction = ServiceDirections.Outbound,
                        Framework = "signalr",
                        Confidence = ConfidenceTiers.Syntactic,
                        Endpoints = [urlArg],
                        Location = CodeLocation.From(ctx.BasePath, tree.FilePath),
                        Evidence = new AnalysisEvidence
                        {
                            Kind = AnalysisEvidenceKind.FrameworkModel,
                            Source = "signalr",
                            Description = "SignalR client connection via HubConnectionBuilder.WithUrl.",
                            Confidence = ConfidenceTiers.Syntactic,
                            FileName = Path.GetFileName(tree.FilePath)
                        }
                    };
                    clientService.Properties["connectionString"] = urlArg;
                    results.Services.Add(clientService);
                }

                // ---- IHubContext<T> usage ----
                foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    var receiver = invocation.Expression as MemberAccessExpressionSyntax;
                    var receiverTypeName = receiver?.Expression.ToString();
                    if (receiverTypeName is null)
                    {
                        continue;
                    }

                    // Check if the receiver type is IHubContext<T> (syntactic check)
                    foreach (var hubType in hubContextTypes)
                    {
                        if (receiverTypeName.Contains($"IHubContext<{hubType}>", StringComparison.Ordinal) ||
                            receiverTypeName.Contains($"IHubContext<{hubType}.", StringComparison.Ordinal))
                        {
                            // Find the matching hub service and tag it
                            foreach (var kvp in hubServices)
                            {
                                if (kvp.Key.EndsWith($"/{hubType}", StringComparison.Ordinal))
                                {
                                    kvp.Value.Properties["hubContext"] = "true";
                                }
                            }

                            break;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            ctx.Diagnostics.Add(new FrameworkDiagnostic("signalr", $"SignalR analysis failed: {ex.Message}"));
        }
    }
}
