using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Depscan.Frameworks.Providers;

/// <summary>
///     Community HTTP frameworks: FastEndpoints (Endpoint&lt;TReq,TRes&gt; + Configure), ServiceStack
///     ([Route] on DTOs + Service subclasses), and Nancy (NancyModule route registrations).
///     Carter modules register routes through Map* calls and are grouped by the Minimal API
///     provider; here we detect the module types themselves for service inventory.
/// </summary>
public sealed class CommunityHttpProvider : IFrameworkProvider
{
    public string Id => "community-http";

    public string DisplayName => "Community HTTP frameworks (FastEndpoints, ServiceStack, Nancy, Carter)";

    public bool AppliesTo(FrameworkContext ctx) => ctx.CSharp is not null;

    public void Analyze(FrameworkContext ctx, FrameworkResults results)
    {
foreach (var tree in ctx.CSharpTrees)
        {
            if (!ctx.TextContainsAny(tree, "Endpoint<", "NancyModule", "ICarterModule", "ServiceStack", "Configure"))
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

                // FastEndpoints: classes deriving Endpoint<TReq, TRes> with a Configure() body.
                var derivesEndpoint = symbol is not null && ProviderHelpers.DerivesFromAny(symbol, "Endpoint");
                if (derivesEndpoint || BaseListContains(typeDeclaration, "Endpoint<"))
                {
                    AnalyzeFastEndpoint(ctx, results, typeDeclaration, symbol is not null && derivesEndpoint, typeName, namespaceName, tree.FilePath, rawUrls, model);
                }

                // ServiceStack: classes deriving Service whose DTOs carry [Route]. A bare `: Service`
                // base matches any number of user classes, so the file must also mention ServiceStack.
                if (symbol is not null && ProviderHelpers.DerivesFromAny(symbol, "Service") && !derivesEndpoint && ctx.TextContainsAny(tree, "ServiceStack"))
                {
                    AnalyzeServiceStack(ctx, results, typeDeclaration, symbol, typeName, namespaceName, tree.FilePath, rawUrls, model);
                }

                // Nancy: NancyModule subclasses registering Get["/x"]/Post["/x"] in constructors.
                if ((symbol is not null && ProviderHelpers.DerivesFromAny(symbol, "NancyModule")) || typeDeclaration.BaseList?.Types.Any(baseType => baseType.Type.ToString().Contains("NancyModule")) == true)
                {
                    AnalyzeNancyModule(ctx, results, typeDeclaration, typeName, namespaceName, tree.FilePath, rawUrls);
                }

                // Carter: ICarterModule implementations (routes come from Map* invocations).
                if (symbol is not null && ProviderHelpers.ImplementsAny(symbol, "ICarterModule"))
                {
                    var serviceId = FrameworkIds.Service("community-http", namespaceName, typeName);
                    results.Services.Add(new ServiceComponent
                    {
                        Id = serviceId,
                        Name = typeName,
                        Group = namespaceName,
                        ServiceKind = ServiceKinds.Http,
                        Direction = ServiceDirections.Inbound,
                        Framework = "community-http",
                        Confidence = ConfidenceTiers.Semantic,
                        Properties = { ["framework"] = "Carter" },
                        Location = CodeLocation.From(ctx.BasePath, tree.FilePath),
                        Evidence = new AnalysisEvidence { Kind = AnalysisEvidenceKind.FrameworkModel, Source = "community-http", Description = "Carter module (ICarterModule).", Confidence = ConfidenceTiers.Semantic }
                    });
                }
            }
        }
    }

    private static bool BaseListContains(TypeDeclarationSyntax typeDeclaration, string text) =>
        typeDeclaration.BaseList?.Types.Any(baseType => baseType.Type.ToString().Contains(text, StringComparison.Ordinal)) == true;

    private static void AnalyzeFastEndpoint(FrameworkContext ctx, FrameworkResults results, TypeDeclarationSyntax typeDeclaration, bool semantic, string typeName, string? namespaceName, string filePath, List<string> rawUrls, SemanticModel model)
    {
        var configure = typeDeclaration.Members.OfType<MethodDeclarationSyntax>().FirstOrDefault(method => method.Identifier.Text == "Configure");
        if (configure is null)
        {
            return;
        }

        string? route = null;
        var verbs = new List<string>();
        var roles = new List<string>();
        var policies = new List<string>();
        var allowAnonymous = false;
        foreach (var invocation in configure.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var name = ProviderHelpers.InvocationName(invocation);
            switch (name)
            {
                case "Get": case "Post": case "Put": case "Delete": case "Patch":
                    route ??= ProviderHelpers.StringArguments(invocation).FirstOrDefault();
                    verbs.Add(name.ToUpperInvariant());
                    break;
                case "Verbs":
                    verbs.AddRange(ProviderHelpers.StringArguments(invocation).Select(verb => verb.ToUpperInvariant()));
                    break;
                case "AllowAnonymous":
                    allowAnonymous = true;
                    break;
                case "Roles":
                    roles.AddRange(ProviderHelpers.StringArguments(invocation));
                    break;
                case "Policies":
                    policies.AddRange(ProviderHelpers.StringArguments(invocation));
                    break;
            }
        }

        if (route is null)
        {
            return;
        }

        if (verbs.Count == 0)
        {
            verbs.Add("ANY");
        }

        var lineSpan = typeDeclaration.GetLocation().GetLineSpan().StartLinePosition;
        var confidence = semantic ? ConfidenceTiers.Semantic : ConfidenceTiers.Syntactic;
        foreach (var verb in verbs)
        {
            var resolved = RouteTemplateResolver.Resolve(route);
            var serviceId = FrameworkIds.Service("community-http", namespaceName, typeName);
            var operationId = FrameworkIds.Operation(serviceId, verb, resolved.Path, "handle");
            var endpoint = new ApiEndpoint
            {
                Path = resolved.Path,
                FilePath = CodeLocation.From(ctx.BasePath, filePath).Path,
                FileName = Path.GetFileName(filePath),
                Namespace = namespaceName,
                ClassName = typeName,
                HttpMethod = verb,
                Route = route,
                EndpointKind = "Attribute",
                RoutingKind = "Attribute",
                Framework = "community-http",
                ServiceId = serviceId,
                OperationId = operationId,
                Confidence = confidence,
                LineNumber = lineSpan.Line + 1,
                ColumnNumber = lineSpan.Character + 1,
                RawUrls = rawUrls,
                AllowAnonymous = allowAnonymous,
                AuthorizationRequired = allowAnonymous ? false : policies.Count > 0 || roles.Count > 0 ? true : null,
                Roles = roles,
                AuthorizationPolicies = policies,
                Evidence = new AnalysisEvidence { Kind = AnalysisEvidenceKind.FrameworkModel, Source = "community-http", Description = "FastEndpoints Configure() route.", Confidence = confidence, FileName = Path.GetFileName(filePath), LineNumber = lineSpan.Line + 1 }
            };
            results.ApiEndpoints.Add(endpoint);

            var service = results.Services.FirstOrDefault(existing => existing.Id == serviceId);
            if (service is null)
            {
                service = new ServiceComponent
                {
                    Id = serviceId,
                    Name = typeName,
                    Group = namespaceName,
                    ServiceKind = ServiceKinds.Http,
                    Direction = ServiceDirections.Inbound,
                    Framework = "community-http",
                    Confidence = confidence,
                    Properties = { ["framework"] = "FastEndpoints" },
                    Location = CodeLocation.From(ctx.BasePath, filePath),
                    Evidence = new AnalysisEvidence { Kind = AnalysisEvidenceKind.FrameworkModel, Source = "community-http", Description = "FastEndpoints endpoint class.", Confidence = confidence }
                };
                results.Services.Add(service);
            }

            if (endpoint.Path is not null && !service.Endpoints.Contains(endpoint.Path, StringComparer.Ordinal))
            {
                service.Endpoints.Add(endpoint.Path);
            }

            results.EntryPoints.Add(new EntryPoint
            {
                Id = $"ep:{operationId}",
                Kind = "HttpController",
                ClassName = typeName,
                Namespace = namespaceName,
                FileName = endpoint.FileName,
                Path = endpoint.FilePath,
                LineNumber = endpoint.LineNumber,
                ColumnNumber = endpoint.ColumnNumber,
                HttpMethod = verb,
                Route = resolved.Path ?? route,
                AllowAnonymous = allowAnonymous
            });
        }
    }

    private static void AnalyzeServiceStack(FrameworkContext ctx, FrameworkResults results, TypeDeclarationSyntax typeDeclaration, INamedTypeSymbol? symbol, string typeName, string? namespaceName, string filePath, List<string> rawUrls, SemanticModel model)
    {
        var confidence = symbol is not null && ProviderHelpers.DerivesFromAny(symbol, "Service") ? ConfidenceTiers.Semantic : ConfidenceTiers.Syntactic;
        var serviceId = FrameworkIds.Service("community-http", namespaceName, typeName);
        var service = new ServiceComponent
        {
            Id = serviceId,
            Name = typeName,
            Group = namespaceName,
            ServiceKind = ServiceKinds.Http,
            Direction = ServiceDirections.Inbound,
            Framework = "community-http",
            Confidence = confidence,
            Properties = { ["framework"] = "ServiceStack" },
            Location = CodeLocation.From(ctx.BasePath, filePath),
            Evidence = new AnalysisEvidence { Kind = AnalysisEvidenceKind.FrameworkModel, Source = "community-http", Description = "ServiceStack service.", Confidence = confidence }
        };
        results.Services.Add(service);

        // ServiceStack routes are declared as [Route("/x", "GET")] on request DTOs; the DTO name
        // maps to the Any(Post|Get|Put|Delete)(Request) method on the service.
        foreach (var attribute in typeDeclaration.DescendantNodes().OfType<AttributeSyntax>().Where(attribute => ProviderHelpers.IsNamed(attribute, "Route")))
        {
            var template = ProviderHelpers.RouteTemplateOf(attribute, model);
            if (template is null)
            {
                continue;
            }

            var verbArgument = ProviderHelpers.AttributeStringArguments(attribute).Skip(1).FirstOrDefault();
            var verbs = verbArgument is null ? ["ANY"] : verbArgument.Split(',').Select(verb => verb.Trim().ToUpperInvariant()).ToList();
            foreach (var verb in verbs)
            {
                var resolved = RouteTemplateResolver.Resolve(template);
                var operationId = FrameworkIds.Operation(serviceId, verb, resolved.Path, typeName);
                var lineSpan = attribute.GetLocation().GetLineSpan().StartLinePosition;
                results.ApiEndpoints.Add(new ApiEndpoint
                {
                    Path = resolved.Path,
                    FilePath = CodeLocation.From(ctx.BasePath, filePath).Path,
                    FileName = Path.GetFileName(filePath),
                    Namespace = namespaceName,
                    ClassName = typeName,
                    HttpMethod = verb,
                    Route = template,
                    EndpointKind = "Attribute",
                    RoutingKind = "Attribute",
                    Framework = "community-http",
                    ServiceId = serviceId,
                    OperationId = operationId,
                    Confidence = confidence,
                    LineNumber = lineSpan.Line + 1,
                    ColumnNumber = lineSpan.Character + 1,
                    RawUrls = rawUrls,
                    Evidence = new AnalysisEvidence { Kind = AnalysisEvidenceKind.FrameworkModel, Source = "community-http", Description = "ServiceStack DTO route.", Confidence = confidence }
                });
                if (resolved.Path is not null && !service.Endpoints.Contains(resolved.Path, StringComparer.Ordinal))
                {
                    service.Endpoints.Add(resolved.Path);
                }
            }
        }
    }

    private static void AnalyzeNancyModule(FrameworkContext ctx, FrameworkResults results, TypeDeclarationSyntax typeDeclaration, string typeName, string? namespaceName, string filePath, List<string> rawUrls)
    {
        foreach (var elementAccess in typeDeclaration.DescendantNodes().OfType<ElementAccessExpressionSyntax>())
        {
            var indexer = elementAccess.Expression switch
            {
                MemberAccessExpressionSyntax member => member.Name.Identifier.Text,
                IdentifierNameSyntax identifier => identifier.Identifier.Text,
                _ => null
            };
            var verb = indexer switch
            {
                "Get" => "GET",
                "Post" => "POST",
                "Put" => "PUT",
                "Delete" => "DELETE",
                "Patch" => "PATCH",
                "Options" => "OPTIONS",
                _ => null
            };

            if (verb is null)
            {
                continue;
            }

            var route = elementAccess.ArgumentList.Arguments.FirstOrDefault()?.Expression;
            if (route is not LiteralExpressionSyntax literal || literal.Token.Value is not string routeText)
            {
                continue;
            }

            var resolved = RouteTemplateResolver.Resolve(routeText);
            var lineSpan = elementAccess.GetLocation().GetLineSpan().StartLinePosition;
            var serviceId = FrameworkIds.Service("community-http", namespaceName, typeName);
            var operationId = FrameworkIds.Operation(serviceId, verb, resolved.Path, $"nancy-{verb}");
            results.ApiEndpoints.Add(new ApiEndpoint
            {
                Path = resolved.Path,
                FilePath = CodeLocation.From(ctx.BasePath, filePath).Path,
                FileName = Path.GetFileName(filePath),
                Namespace = namespaceName,
                ClassName = typeName,
                HttpMethod = verb,
                Route = routeText,
                EndpointKind = "Attribute",
                RoutingKind = "Attribute",
                Framework = "community-http",
                ServiceId = serviceId,
                OperationId = operationId,
                Confidence = ConfidenceTiers.Syntactic,
                LineNumber = lineSpan.Line + 1,
                ColumnNumber = lineSpan.Character + 1,
                RawUrls = rawUrls,
                Evidence = new AnalysisEvidence { Kind = AnalysisEvidenceKind.FrameworkModel, Source = "community-http", Description = "Nancy module route registration.", Confidence = ConfidenceTiers.Syntactic }
            });

            var service = results.Services.FirstOrDefault(existing => existing.Id == serviceId);
            if (service is null)
            {
                service = new ServiceComponent
                {
                    Id = serviceId,
                    Name = typeName,
                    Group = namespaceName,
                    ServiceKind = ServiceKinds.Http,
                    Direction = ServiceDirections.Inbound,
                    Framework = "community-http",
                    Confidence = ConfidenceTiers.Syntactic,
                    Properties = { ["framework"] = "Nancy" },
                    Location = CodeLocation.From(ctx.BasePath, filePath),
                    Evidence = new AnalysisEvidence { Kind = AnalysisEvidenceKind.FrameworkModel, Source = "community-http", Description = "Nancy module.", Confidence = ConfidenceTiers.Syntactic }
                };
                results.Services.Add(service);
            }

            if (resolved.Path is not null && !service.Endpoints.Contains(resolved.Path, StringComparer.Ordinal))
            {
                service.Endpoints.Add(resolved.Path);
            }
        }
    }
}
