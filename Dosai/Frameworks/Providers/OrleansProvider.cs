using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Depscan.Frameworks.Providers;

/// <summary>
///     Microsoft Orleans: grains as inbound RPC services. Grain-derived types (and types
///     implementing IGrain-with-key interfaces) become services whose public methods are
///     GrainMethod entry points with rpc-message taint seeds, replacing the blunt Orleans
///     namespace-prefix source pattern in the rpc pattern pack, which stays as fallback for
///     assembly-only scans. IGrainFactory.GetGrain&lt;T&gt; call sites become outbound RPC edges,
///     and UseOrleans/AddApplicationParts registrations confirm framework presence.
/// </summary>
public sealed class OrleansProvider : IFrameworkProvider
{
    public string Id => "orleans";

    public string DisplayName => "Microsoft Orleans";

    public bool AppliesTo(FrameworkContext ctx) => ctx.CSharp is not null;

    public void Analyze(FrameworkContext ctx, FrameworkResults results)
    {
        foreach (var tree in ctx.CSharpTrees)
        {
            var model = ctx.CSharp!.GetSemanticModel(tree);
            var root = tree.GetCompilationUnitRoot();
            var rawUrls = ctx.RawUrlsFor(tree);

            // ---- Server-side grains ----
            foreach (var typeDeclaration in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                var symbol = model.GetDeclaredSymbol(typeDeclaration);
                var confidence = ConfidenceTiers.Semantic;
                // Grain-derived classes are the classic shape; grains implementing IGrain directly
                // (common in the minimal-hosting samples) qualify through the interface too.
                var isGrain = symbol is not null && (ProviderHelpers.DerivesFromAny(symbol, "Grain") || ProviderHelpers.ImplementsAny(symbol, "IGrain", "IAddressable", "IGrainWithIntegerKey"));
                if (!isGrain)
                {
                    // Syntactic fallback for unresolved references: base list contains Grain or an
                    // IGrain key interface.
                    var baseList = (typeDeclaration as ClassDeclarationSyntax)?.BaseList;
                    if (baseList is null)
                    {
                        continue;
                    }

                    var grainBase = baseList.Types.Any(baseType =>
                    {
                        var name = baseType.Type.ToString();
                        return name.Equals("Grain", StringComparison.Ordinal) ||
                               name.StartsWith("Grain<", StringComparison.Ordinal) ||
                               name.StartsWith("IGrain", StringComparison.Ordinal);
                    });
                    if (!grainBase)
                    {
                        continue;
                    }

                    confidence = ConfidenceTiers.Syntactic;
                }

                // Interfaces co-located with grains (I*Grain) are contracts, not services.
                if (typeDeclaration is InterfaceDeclarationSyntax)
                {
                    continue;
                }

                var grainName = typeDeclaration.Identifier.Text;
                var namespaceName = typeDeclaration.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();
                var grainDirectory = Path.GetDirectoryName(CodeLocation.From(ctx.BasePath, tree.FilePath).Path)?.Replace(Path.DirectorySeparatorChar, '.');
                var serviceId = FrameworkIds.Service("orleans", string.IsNullOrWhiteSpace(grainDirectory) ? namespaceName : $"{namespaceName}.{grainDirectory}", grainName);
                var lineSpan = typeDeclaration.GetLocation().GetLineSpan().StartLinePosition;

                var service = new ServiceComponent
                {
                    Id = serviceId,
                    Name = grainName,
                    Group = namespaceName,
                    ServiceKind = ServiceKinds.Rpc,
                    Direction = ServiceDirections.Inbound,
                    Framework = "orleans",
                    FrameworkVersion = ctx.Detection["orleans"]?.Version,
                    Purl = ctx.Detection["orleans"]?.Purl,
                    Confidence = confidence,
                    TrustZone = TrustZones.Authenticated,
                    Location = CodeLocation.From(ctx.BasePath, tree.FilePath, lineSpan.Line + 1, lineSpan.Character + 1),
                    Evidence = new AnalysisEvidence
                    {
                        Kind = AnalysisEvidenceKind.FrameworkModel,
                        Source = "orleans",
                        Description = confidence == ConfidenceTiers.Semantic
                            ? "Type derives from Grain (symbol resolved)."
                            : "Base list contains Grain/IGrain (name match; references unresolved).",
                        Confidence = confidence,
                        FileName = Path.GetFileName(tree.FilePath),
                        LineNumber = lineSpan.Line + 1
                    }
                };
                var classAttributes = ProviderHelpers.AttributesOf(typeDeclaration.AttributeLists).ToList();
                if (classAttributes.Any(attribute => ProviderHelpers.IsNamed(attribute, "Reentrant")))
                {
                    service.Properties["reentrant"] = "true";
                }

                var aliasAttribute = classAttributes.FirstOrDefault(attribute => ProviderHelpers.IsNamed(attribute, "Alias"));
                if (aliasAttribute is not null && ProviderHelpers.AttributeArgumentText(aliasAttribute, model) is { } alias)
                {
                    service.Properties["alias"] = alias;
                    service.Endpoints.Add(alias);
                }

                foreach (var method in typeDeclaration.Members.OfType<MethodDeclarationSyntax>())
                {
                    if (!method.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.PublicKeyword)) ||
                        method.Identifier.Text.Equals("Dispose", StringComparison.Ordinal) ||
                        method.Identifier.Text.Equals("OnActivateAsync", StringComparison.Ordinal) ||
                        method.Identifier.Text.Equals("OnDeactivateAsync", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var methodName = method.Identifier.Text;
                    var methodLineSpan = method.GetLocation().GetLineSpan().StartLinePosition;
                    var methodSymbol = model.GetDeclaredSymbol(method);
                    var methodId = methodSymbol is null ? null : Depscan.Dosai.FormatMethodSignature(methodSymbol);
                    var operationId = FrameworkIds.Operation(serviceId, null, null, methodName);

                    service.Operations.Add(new ServiceOperation
                    {
                        Id = operationId,
                        Name = methodName,
                        MethodId = methodId,
                        Confidence = confidence,
                        Location = CodeLocation.From(ctx.BasePath, tree.FilePath, methodLineSpan.Line + 1, methodLineSpan.Character + 1)
                    });
                    if (methodId is not null && !service.MethodIds.Contains(methodId, StringComparer.Ordinal))
                    {
                        service.MethodIds.Add(methodId);
                    }

                    results.ApiEndpoints.Add(new ApiEndpoint
                    {
                        FilePath = CodeLocation.From(ctx.BasePath, tree.FilePath).Path,
                        FileName = Path.GetFileName(tree.FilePath),
                        Namespace = namespaceName,
                        ClassName = grainName,
                        MethodName = methodName,
                        EndpointKind = "GrainMethod",
                        RoutingKind = "Mount",
                        Path = aliasAttribute is not null ? $"grain:{grainName}" : null,
                        Framework = "orleans",
                        ServiceId = serviceId,
                        OperationId = operationId,
                        Confidence = confidence,
                        LineNumber = methodLineSpan.Line + 1,
                        ColumnNumber = methodLineSpan.Character + 1,
                        RawUrls = rawUrls,
                        Evidence = new AnalysisEvidence
                        {
                            Kind = AnalysisEvidenceKind.FrameworkModel,
                            Source = "orleans",
                            Description = "Orleans grain method callable over the silo RPC runtime.",
                            Confidence = confidence,
                            FileName = Path.GetFileName(tree.FilePath),
                            LineNumber = methodLineSpan.Line + 1
                        }
                    });

                    results.EntryPoints.Add(new EntryPoint
                    {
                        Id = $"ep:{operationId}",
                        Kind = "GrainMethod",
                        MethodId = methodId,
                        MethodName = methodName,
                        ClassName = grainName,
                        Namespace = namespaceName,
                        FileName = Path.GetFileName(tree.FilePath),
                        Path = CodeLocation.From(ctx.BasePath, tree.FilePath).Path,
                        LineNumber = methodLineSpan.Line + 1,
                        ColumnNumber = methodLineSpan.Character + 1,
                        Route = $"grain:{grainName}.{methodName}",
                        RawUrls = rawUrls
                    });
                    service.EntryPointIds.Add($"ep:{operationId}");

                    // Grain method parameters arrive from (potentially remote) callers over the
                    // Orleans runtime, rpc-message seeds replace the namespace-prefix heuristic.
                    foreach (var parameter in method.ParameterList.Parameters)
                    {
                        if (parameter.Type?.ToString().Contains("CancellationToken", StringComparison.Ordinal) == true)
                        {
                            continue;
                        }

                        results.TaintSeeds.Add(new FrameworkTaintSeed
                        {
                            MethodName = methodName,
                            ParameterName = parameter.Identifier.Text,
                            ClassName = grainName,
                            Namespace = namespaceName,
                            FileName = Path.GetFileName(tree.FilePath),
                            MethodSignature = methodId,
                            LineNumber = methodLineSpan.Line + 1,
                            BindingSource = "rpc-message",
                            TaintKind = "rpc",
                            FrameworkId = "orleans",
                            EndpointPath = $"grain:{grainName}",
                            Confidence = methodSymbol is not null ? ConfidenceTiers.Semantic : ConfidenceTiers.Syntactic
                        });
                    }
                }

                results.Services.Add(service);
            }

            // ---- Client-side: IGrainFactory.GetGrain<T>(...) ----
            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var name = ProviderHelpers.InvocationName(invocation);
                if (name is not ("GetGrain" or "GetGrainById"))
                {
                    continue;
                }

                var receiver = invocation.Expression as MemberAccessExpressionSyntax;
                var invocationText = receiver?.Name.ToString() ?? string.Empty;
                // GetGrain<IChatGrain> → IChatGrain; the generic argument names the grain contract.
                var grainType = "UnknownGrain";
                var genericStart = invocationText.IndexOf('<');
                if (genericStart >= 0 && invocationText.EndsWith(">", StringComparison.Ordinal))
                {
                    grainType = invocationText[(genericStart + 1)..^1];
                }
                var containingType = invocation.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
                var className = containingType?.Identifier.Text ?? "AnonymousClient";
                var clientDirectory = Path.GetDirectoryName(CodeLocation.From(ctx.BasePath, tree.FilePath).Path)?.Replace(Path.DirectorySeparatorChar, '.');
                var ns = containingType?.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();
                var clientServiceId = FrameworkIds.Service("orleans", string.IsNullOrWhiteSpace(clientDirectory) ? ns : $"{ns}.{clientDirectory}", $"{className}-client");
                if (results.Services.Any(existing => existing.Id == clientServiceId))
                {
                    continue;
                }

                var invocationLine = invocation.GetLocation().GetLineSpan().StartLinePosition;
                var clientService = new ServiceComponent
                {
                    Id = clientServiceId,
                    Name = $"{className} → {grainType}",
                    Group = ns,
                    ServiceKind = ServiceKinds.Rpc,
                    Direction = ServiceDirections.Outbound,
                    Framework = "orleans",
                    Confidence = ConfidenceTiers.Syntactic,
                    Endpoints = [$"grain:{grainType}"],
                    Location = CodeLocation.From(ctx.BasePath, tree.FilePath, invocationLine.Line + 1, invocationLine.Character + 1),
                    Evidence = new AnalysisEvidence
                    {
                        Kind = AnalysisEvidenceKind.FrameworkModel,
                        Source = "orleans",
                        Description = $"Outbound grain reference via IGrainFactory.GetGrain<{grainType}>.",
                        Confidence = ConfidenceTiers.Syntactic,
                        FileName = Path.GetFileName(tree.FilePath),
                        LineNumber = invocationLine.Line + 1
                    }
                };
                clientService.Properties["grainType"] = grainType;
                results.Services.Add(clientService);
            }

            // ---- Host registration: UseOrleans / AddApplicationParts ----
            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var name = ProviderHelpers.InvocationName(invocation);
                if (name is not ("UseOrleans" or "AddApplicationParts" or "AddApplicationPart"))
                {
                    continue;
                }

                var line = invocation.GetLocation().GetLineSpan().StartLinePosition;
                var registrationId = FrameworkIds.Service("orleans", Path.GetDirectoryName(CodeLocation.From(ctx.BasePath, tree.FilePath).Path)?.Replace(Path.DirectorySeparatorChar, '.'), "host");
                var registration = results.Services.FirstOrDefault(existing => existing.Id == registrationId);
                if (registration is null)
                {
                    registration = new ServiceComponent
                    {
                        Id = registrationId,
                        Name = "Orleans silo host",
                        ServiceKind = ServiceKinds.Rpc,
                        Direction = ServiceDirections.Inbound,
                        Framework = "orleans",
                        FrameworkVersion = ctx.Detection["orleans"]?.Version,
                        Purl = ctx.Detection["orleans"]?.Purl,
                        Confidence = ConfidenceTiers.Syntactic,
                        TrustZone = TrustZones.Internal,
                        Location = CodeLocation.From(ctx.BasePath, tree.FilePath, line.Line + 1, line.Character + 1),
                        Evidence = new AnalysisEvidence
                        {
                            Kind = AnalysisEvidenceKind.FrameworkModel,
                            Source = "orleans",
                            Description = $"Orleans silo registration via {name}.",
                            Confidence = ConfidenceTiers.Syntactic,
                            FileName = Path.GetFileName(tree.FilePath),
                            LineNumber = line.Line + 1
                        }
                    };
                    results.Services.Add(registration);
                }

                registration.Properties[$"registration.{name}"] = Path.GetFileName(tree.FilePath);
            }
        }
    }
}
