using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Depscan.Frameworks.Providers;

/// <summary>
///     ASP.NET Core MVC / Web API controllers. Semantic-first: controllers are confirmed through
///     their base-type chain (ControllerBase/Controller) when references resolve; the
///     ASP.NET name-suffix rule (types ending in "Controller" without [NonController]) covers
///     stub and unrestored projects at medium confidence.
/// </summary>
public sealed class AspNetCoreMvcProvider : IFrameworkProvider
{
    private static readonly Dictionary<string, string> VerbAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["HttpGet"] = "GET",
        ["HttpPost"] = "POST",
        ["HttpPut"] = "PUT",
        ["HttpDelete"] = "DELETE",
        ["HttpPatch"] = "PATCH",
        ["HttpHead"] = "HEAD",
        ["HttpOptions"] = "OPTIONS"
    };

    public string Id => "aspnetcore-mvc";

    public string DisplayName => "ASP.NET Core MVC";

    public bool AppliesTo(FrameworkContext ctx) => ctx.CSharp is not null;

    public void Analyze(FrameworkContext ctx, FrameworkResults results)
    {
        var controllers = new List<ControllerCandidate>();
        var conventionalPatterns = new List<(string Name, string Pattern)>();

        foreach (var tree in ctx.CSharpTrees)
        {
            var model = ctx.CSharp!.GetSemanticModel(tree);
            var root = tree.GetCompilationUnitRoot();
            var rawUrls = ctx.RawUrlsFor(tree);

            foreach (var typeDeclaration in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                if (IsControllerCandidate(typeDeclaration, model, out var confidence, out var apiController))
                {
                    // Claim the type so later providers (legacy-web) do not double-report it.
                    ctx.HandledTypeIds.Add($"{tree.FilePath}:{typeDeclaration.Identifier.Text}");
                    controllers.Add(new ControllerCandidate(typeDeclaration, model, tree.FilePath, rawUrls, confidence, apiController));
                }
            }

            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var name = ProviderHelpers.InvocationName(invocation);
                if (name.Equals("MapControllerRoute", StringComparison.Ordinal) || name.Equals("MapRoute", StringComparison.Ordinal))
                {
                    // MapControllerRoute(name, pattern) / MapRoute(name, pattern)
                    var arguments = invocation.ArgumentList.Arguments;
                    var patternArgument = arguments.Count > 1 ? arguments[1].Expression : arguments.Count == 1 ? arguments[0].Expression : null;
                    if (patternArgument is LiteralExpressionSyntax patternLiteral)
                    {
                        conventionalPatterns.Add((arguments.FirstOrDefault().Expression.ToString() ?? "default", patternLiteral.Token.ValueText));
                    }
                }
                else if (name.Equals("MapDefaultControllerRoute", StringComparison.Ordinal))
                {
                    // MapDefaultControllerRoute() takes no arguments; its pattern is fixed.
                    conventionalPatterns.Add(("default", "{controller=Home}/{action=Index}/{id?}"));
                }
            }
        }

        var servicesById = new Dictionary<string, ServiceComponent>(StringComparer.Ordinal);
        foreach (var controller in controllers)
        {
            foreach (var service in AnalyzeController(ctx, results, controller, controllers))
            {
                servicesById[service.Id] = service;
            }
        }

        AnalyzeConventionalRoutes(ctx, results, controllers, conventionalPatterns, servicesById);
    }

    private static bool IsControllerCandidate(TypeDeclarationSyntax typeDeclaration, SemanticModel model, out string confidence, out bool apiController)
    {
        confidence = ConfidenceTiers.Syntactic;
        apiController = false;
        var attributes = ProviderHelpers.AttributesOf(typeDeclaration.AttributeLists).ToList();
        if (attributes.Any(attribute => ProviderHelpers.IsNamed(attribute, "NonController")))
        {
            return false;
        }

        apiController = attributes.Any(attribute => ProviderHelpers.IsNamed(attribute, "ApiController"));
        var symbol = model.GetDeclaredSymbol(typeDeclaration);
        if (symbol is not null && ProviderHelpers.DerivesFromAny(symbol, "ApiController"))
        {
            // Web API 2 (System.Web.Http.ApiController): owned by the legacy-web provider.
            return false;
        }

        var typeName = typeDeclaration.Identifier.Text;

        // Follow DefaultControllerTypeProvider: a non-abstract, non-static, non-generic class without
        // [NonController] that EITHER is named *Controller OR derives from ControllerBase. Matching on
        // the name alone both invented controllers (abstract shared bases, FooController<T>) and missed
        // real ones (`class Things : ControllerBase`).
        //
        // MVC additionally requires the type to be public. That is deliberately NOT enforced: an
        // omitted `public` is far more likely to be terse code than a genuine intent to hide the type,
        // and dropping a controller from an attack-surface inventory is a worse failure than listing
        // one that will not bind. Abstract/static/generic carry no such ambiguity — those types can
        // never be routed, and their actions surface through the derived types instead.
        var derivesFromControllerBase = symbol is not null &&
                                        ProviderHelpers.DerivesFromAny(symbol, "ControllerBase", "Controller");
        var namedController = typeName.EndsWith("Controller", StringComparison.OrdinalIgnoreCase) &&
                              !typeName.Equals("ControllerBase", StringComparison.Ordinal) &&
                              !typeName.EndsWith("ControllerBase", StringComparison.Ordinal);
        if (!namedController && !derivesFromControllerBase)
        {
            return false;
        }

        if (typeDeclaration is not ClassDeclarationSyntax ||
            typeDeclaration.Modifiers.Any(SyntaxKind.AbstractKeyword) ||
            typeDeclaration.Modifiers.Any(SyntaxKind.StaticKeyword) ||
            typeDeclaration.TypeParameterList is not null)
        {
            return false;
        }

        // [RoutePrefix] is the Web API 2 convention; attribute names in System.Web.* mark
        // legacy projects even when references are missing. Those belong to the legacy provider.
        if (attributes.Any(attribute => ProviderHelpers.IsNamed(attribute, "RoutePrefix") ||
                                        attribute.Name.ToString().Contains("System.Web.", StringComparison.Ordinal)))
        {
            return false;
        }

        // A resolved ControllerBase base is symbol evidence; a bare name match is not.
        confidence = derivesFromControllerBase ? ConfidenceTiers.Semantic : ConfidenceTiers.Syntactic;

        return true;
    }

    private static IEnumerable<ServiceComponent> AnalyzeController(FrameworkContext ctx, FrameworkResults results, ControllerCandidate controller, List<ControllerCandidate> allControllers)
    {
        var model = controller.Model;
        var classAttributes = ProviderHelpers.AttributesOf(controller.Type.AttributeLists).ToList();
        var classRoutes = classAttributes
            .Where(attribute => ProviderHelpers.IsNamed(attribute, "Route"))
            .Select(attribute => ProviderHelpers.RouteTemplateOf(attribute, model))
            .Where(route => route is not null)
            .ToList();

        var area = classAttributes.Where(a => ProviderHelpers.IsNamed(a, "Area")).Select(a => ProviderHelpers.AttributeArgumentText(a, model)).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                   ?? AreaFromNamespace(controller.Type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString());
        var controllerName = RouteTemplateResolver.ControllerName(controller.Type.Identifier.Text);
        var namespaceName = controller.Type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();
        var serviceId = FrameworkIds.Service("aspnetcore-mvc", namespaceName, controllerName);
        var service = new ServiceComponent
        {
            Id = serviceId,
            Name = controllerName,
            Group = namespaceName,
            ServiceKind = ServiceKinds.Http,
            Direction = ServiceDirections.Inbound,
            Framework = "aspnetcore-mvc",
            FrameworkVersion = ctx.Detection["aspnetcore-mvc"]?.Version,
            Purl = ctx.Detection["aspnetcore-mvc"]?.Purl,
            Confidence = controller.Confidence,
            Location = CodeLocation.From(ctx.BasePath, controller.FilePath, controller.Type.GetLocation().GetLineSpan().StartLinePosition.Line + 1),
            Evidence = new AnalysisEvidence
            {
                Kind = AnalysisEvidenceKind.FrameworkModel,
                Description = controller.Confidence == ConfidenceTiers.Semantic
                    ? "Type derives from ControllerBase/Controller (symbol resolved)."
                    : "Type name ends in Controller (ASP.NET name-suffix rule; base type unresolved).",
                Confidence = controller.Confidence,
                FileName = Path.GetFileName(controller.FilePath),
                LineNumber = controller.Type.GetLocation().GetLineSpan().StartLinePosition.Line + 1
            }
        };
        results.Services.Add(service);

        foreach (var method in controller.Type.Members.OfType<MethodDeclarationSyntax>())
        {
            var methodAttributes = ProviderHelpers.AttributesOf(method.AttributeLists).ToList();
            if (methodAttributes.Any(attribute => ProviderHelpers.IsNamed(attribute, "NonAction")))
            {
                continue;
            }

            // DefaultApplicationModelProvider.IsAction also rejects static and generic methods; a
            // public static helper on a controller is not an endpoint.
            if (!method.Modifiers.Any(SyntaxKind.PublicKeyword) ||
                method.Modifiers.Any(SyntaxKind.StaticKeyword) ||
                method.TypeParameterList is not null)
            {
                continue;
            }

            var verbs = ActionVerbs(methodAttributes, model);
            if (classRoutes.Count == 0)
            {
                // Conventionally routed controller: without a class-level [Route] prefix every
                // attribute endpoint would collapse to "/", losing the action segment entirely.
                // Actions carrying their own route template ([Route("/x")] or [HttpGet("x")]) are
                // attribute-routed standalone; the rest are expanded by AnalyzeConventionalRoutes
                // below, with their verb hints.
                foreach (var verb in verbs.Where(verb => verb.Template.Text is not null))
                {
                    AddActionEndpoint(ctx, results, service, controller, method, methodAttributes, verb.HttpMethod, verb.Template, null, area, controllerName, namespaceName);
                }

                continue;
            }

            if (verbs.Count == 0)
            {
                // Under attribute routing an action without HTTP method attributes matches any verb.
                verbs.Add(new ActionRoute("ANY", new ActionTemplate(null, true)));
            }

            foreach (var classRoute in classRoutes.DefaultIfEmpty(null))
            {
                foreach (var verb in verbs)
                {
                    AddActionEndpoint(ctx, results, service, controller, method, methodAttributes, verb.HttpMethod, verb.Template, classRoute, area, controllerName, namespaceName);
                }
            }
        }

        yield return service;
    }

    private static List<ActionRoute> ActionVerbs(List<AttributeSyntax> attributes, SemanticModel model)
    {
        var verbs = new List<ActionRoute>();
        foreach (var attribute in attributes)
        {
            var name = ProviderHelpers.NormalizeAttributeName(attribute);
            if (VerbAttributes.TryGetValue(name, out var verb))
            {
                verbs.Add(new ActionRoute(verb, TemplateOf(attribute, model)));
            }
            else if (name.Equals("AcceptVerbs", StringComparison.OrdinalIgnoreCase))
            {
                var accepted = ProviderHelpers.AttributeStringArguments(attribute);
                foreach (var declaredVerb in accepted.Where(v => !string.IsNullOrWhiteSpace(v)))
                {
                    verbs.Add(new ActionRoute(declaredVerb.ToUpperInvariant(), TemplateOf(attribute, model, accepted.Count)));
                }
            }
            else if (name.Equals("Route", StringComparison.OrdinalIgnoreCase))
            {
                verbs.Add(new ActionRoute("ANY", TemplateOf(attribute, model)));
            }
        }

        return verbs;
    }

    /// <summary>
    ///     Template argument of a routing attribute. Unresolvable expressions (non-const fields,
    ///     interpolated values) are kept verbatim with Resolvable = false so the endpoint never
    ///     ships a garbled ToString() as its path.
    /// </summary>
    private static ActionTemplate TemplateOf(AttributeSyntax attribute, SemanticModel? model, int argumentIndex = 0)
    {
        // Positional arguments only, matching AttributeArgumentText. Indexing the raw argument list
        // here made `[HttpGet(Name = "GetWeatherForecast")]` look like it carried a template, so the
        // unresolvable branch below emitted the route name verbatim as a path segment.
        var argument = attribute.ArgumentList?.Arguments
            .Where(candidate => candidate.NameEquals is null)
            .ElementAtOrDefault(argumentIndex);
        if (argument is null)
        {
            return new ActionTemplate(null, true);
        }

        var text = ProviderHelpers.AttributeArgumentText(attribute, model, argumentIndex);
        return text is null ? new ActionTemplate(argument.Expression.ToString(), false) : new ActionTemplate(text, true);
    }

    private sealed record ActionTemplate(string? Text, bool Resolvable);

    private sealed record ActionRoute(string HttpMethod, ActionTemplate Template);

    private static void AddActionEndpoint(
        FrameworkContext ctx,
        FrameworkResults results,
        ServiceComponent service,
        ControllerCandidate controller,
        MethodDeclarationSyntax method,
        List<AttributeSyntax> methodAttributes,
        string httpMethod,
        ActionTemplate template,
        string? classRoute,
        string? area,
        string controllerName,
        string? namespaceName)
    {
        var model = controller.Model;
        var tokens = new RouteTokenValues
        {
            Controller = controllerName,
            Action = ActionNameFrom(methodAttributes, method.Identifier.Text),
            Area = area
        };
        var verbatim = RouteTemplateResolver.Combine(classRoute, template.Text);
        var baseTemplate = template.Resolvable ? RouteTemplateResolver.Resolve(verbatim, tokens) : new ResolvedRouteTemplate { NormalizedTemplate = verbatim ?? string.Empty };
        var lineSpan = method.GetLocation().GetLineSpan().StartLinePosition;
        var methodSymbol = model.GetDeclaredSymbol(method);
        var methodId = methodSymbol is null ? null : Depscan.Dosai.FormatMethodSignature(methodSymbol);
        var controllerAttributes = classAttributes(controller);
        var apiVersions = EffectiveApiVersions(methodAttributes, controllerAttributes);

        // "{version:apiVersion}" has a statically known value domain: emit one concrete endpoint
        // per declared API version instead of shipping the constraint inside the path.
        var versionParameter = baseTemplate.Parameters.FirstOrDefault(parameter => parameter.Constraints.Any(constraint => constraint.Equals("apiVersion", StringComparison.OrdinalIgnoreCase)));
        List<(string? ApiVersion, ResolvedRouteTemplate Template)> expansions;
        if (versionParameter is not null && apiVersions.Count > 0)
        {
            expansions = apiVersions
                .Select(version => ((string?)version, RouteTemplateResolver.SubstituteParameter(baseTemplate, versionParameter.Name, version)))
                .ToList();
        }
        else
        {
            expansions = [((string?)null, baseTemplate)];
        }

        var seedTaint = true;
        foreach (var (apiVersion, resolved) in expansions)
        {
            var declaredVersion = apiVersion ?? (apiVersions.Count == 1 ? apiVersions[0] : null);
            var operationId = FrameworkIds.Operation(service.Id, httpMethod, resolved.Path, method.Identifier.Text);
            var endpointConfidence = resolved.Path is null ? "low" : controller.Confidence;

            var endpoint = new ApiEndpoint
            {
                Path = resolved.Path,
                FilePath = CodeLocation.From(ctx.BasePath, controller.FilePath).Path,
                FileName = Path.GetFileName(controller.FilePath),
                Namespace = namespaceName,
                ClassName = controller.Type.Identifier.Text,
                MethodName = method.Identifier.Text,
                HttpMethod = httpMethod,
                Route = verbatim,
                EndpointKind = "Attribute",
                RoutingKind = "Attribute",
                Framework = "aspnetcore-mvc",
                ServiceId = service.Id,
                OperationId = operationId,
                ApiVersion = declaredVersion,
                Confidence = endpointConfidence,
                LineNumber = lineSpan.Line + 1,
                ColumnNumber = lineSpan.Character + 1,
                RawUrls = controller.RawUrls,
                Evidence = new AnalysisEvidence
                {
                    Kind = AnalysisEvidenceKind.FrameworkModel,
                    Source = "aspnetcore-mvc",
                    Description = resolved.Path is null
                        ? "Route template could not be resolved to a path."
                        : apiVersion is null ? "Attribute-routed controller action." : $"Attribute-routed controller action (API version {apiVersion}).",
                    Confidence = endpointConfidence,
                    FileName = Path.GetFileName(controller.FilePath),
                    LineNumber = lineSpan.Line + 1,
                    ColumnNumber = lineSpan.Character + 1
                }
            };
            BindRouteParameters(endpoint, resolved.Parameters, method, methodAttributes, model);
            ApplyContentTypesAndVersion(endpoint, methodAttributes, controllerAttributes);
            ProviderHelpers.ApplyAuthorizationMetadata(endpoint, controllerAttributes.Concat(methodAttributes));
            results.ApiEndpoints.Add(endpoint);

            var operation = new ServiceOperation
            {
                Id = operationId,
                Name = ActionNameFrom(methodAttributes, method.Identifier.Text),
                HttpMethod = httpMethod,
                Path = resolved.Path,
                RouteTemplate = verbatim,
                RouteParameters = endpoint.RouteParameters,
                RequestType = RequestTypeOf(methodSymbol),
                ResponseType = methodSymbol?.ReturnType?.ToDisplayString(),
                ContentTypes = endpoint.ContentTypes,
                MethodId = methodId,
                Authenticated = endpoint.AuthorizationRequired,
                Confidence = endpoint.Confidence,
                Location = CodeLocation.From(ctx.BasePath, controller.FilePath, lineSpan.Line + 1, lineSpan.Character + 1)
            };
            if (!string.IsNullOrWhiteSpace(declaredVersion))
            {
                operation.Properties["apiVersion"] = declaredVersion;
            }

            if (ctx.ClassifyData && methodSymbol is not null)
            {
                // Classify request/response DTOs from their members; every non-unknown
                // classification names the triggering member (auditable).
                var requestSymbol = methodSymbol.Parameters.FirstOrDefault(parameter => parameter.Type is { SpecialType: SpecialType.None, TypeKind: not TypeKind.Error } && !parameter.Type.Name.Contains("CancellationToken", StringComparison.Ordinal))?.Type;
                if (requestSymbol is not null)
                {
                    service.Data.AddRange(DataClassifier.Describe(requestSymbol, ServiceDirections.Inbound, service.Id, "inbound"));
                }

                if (methodSymbol.ReturnType is { SpecialType: SpecialType.None, TypeKind: not TypeKind.Error })
                {
                    service.Data.AddRange(DataClassifier.Describe(methodSymbol.ReturnType, ServiceDirections.Outbound, service.Id, "outbound"));
                }
            }

            service.Operations.Add(operation);
            if (resolved.Path is not null && !service.Endpoints.Contains(resolved.Path, StringComparer.Ordinal))
            {
                service.Endpoints.Add(resolved.Path);
            }

            if (methodId is not null && !service.MethodIds.Contains(methodId, StringComparer.Ordinal))
            {
                service.MethodIds.Add(methodId);
            }

            // Authorization is aggregated across the controller's actions, never assigned from whichever
            // action happened to be visited last. A single [AllowAnonymous] action on an [Authorize]d
            // controller makes the controller anonymously reachable — that is the fact a reviewer needs —
            // while "authenticated" may only be claimed when every action requires authorization.
            operation.Authenticated = endpoint.AllowAnonymous ? false : endpoint.AuthorizationRequired;
            foreach (var scheme in endpoint.AuthenticationSchemes) ProviderHelpers.AddDistinct(service.AuthenticationSchemes, scheme);
            foreach (var policy in endpoint.AuthorizationPolicies) ProviderHelpers.AddDistinct(service.AuthorizationPolicies, policy);
            foreach (var role in endpoint.Roles) ProviderHelpers.AddDistinct(service.Roles, role);

            var anyAnonymous = service.Operations.Any(op => op.Authenticated == false);
            service.AllowAnonymous = anyAnonymous ? true : null;
            service.Authenticated = anyAnonymous
                ? false
                : service.Operations.All(op => op.Authenticated == true) ? true : null;
            service.TrustZone = anyAnonymous
                ? TrustZones.Public
                : service.Authenticated == true ? TrustZones.Authenticated : TrustZones.Unknown;

            results.EntryPoints.Add(new EntryPoint
            {
                Id = $"ep:{operationId}",
                Kind = "HttpController",
                MethodId = methodId,
                MethodName = method.Identifier.Text,
                ClassName = controller.Type.Identifier.Text,
                Namespace = namespaceName,
                FileName = endpoint.FileName,
                Path = endpoint.FilePath,
                LineNumber = endpoint.LineNumber,
                ColumnNumber = endpoint.ColumnNumber,
                HttpMethod = httpMethod,
                Route = resolved.Path ?? verbatim,
                AuthorizationRequired = endpoint.AuthorizationRequired,
                AuthorizationPolicies = endpoint.AuthorizationPolicies,
                Roles = endpoint.Roles,
                AllowAnonymous = endpoint.AllowAnonymous,
                AuthenticationSchemes = endpoint.AuthenticationSchemes,
                RequiredClaims = endpoint.RequiredClaims,
                RequiredScopes = endpoint.RequiredScopes,
                CorsPolicies = endpoint.CorsPolicies,
                AntiForgeryRequired = endpoint.AntiForgeryRequired,
                RawUrls = endpoint.RawUrls
            });

            if (seedTaint)
            {
                AddTaintSeeds(results, endpoint, method, methodAttributes, model, controller.FilePath, namespaceName, controller.Type.Identifier.Text, methodId);
                seedTaint = false;
            }
        }
    }

    private static void AddTaintSeeds(FrameworkResults results, ApiEndpoint endpoint, MethodDeclarationSyntax method, List<AttributeSyntax> methodAttributes, SemanticModel model, string filePath, string? namespaceName, string className, string? methodId)
    {
        foreach (var parameter in method.ParameterList.Parameters)
        {
            var name = parameter.Identifier.Text;
            var binding = BindingSourceOf(parameter, endpoint.RouteParameters.Select(routeParameter => routeParameter.Name).ToList());
            if (binding is null)
            {
                continue; // DI-injected services are not attacker-controlled
            }

            results.TaintSeeds.Add(new FrameworkTaintSeed
            {
                MethodName = method.Identifier.Text,
                ParameterName = name,
                ClassName = className,
                Namespace = namespaceName,
                FileName = Path.GetFileName(filePath),
                MethodSignature = methodId,
                LineNumber = endpoint.LineNumber,
                BindingSource = binding,
                TaintKind = binding == "http-body" ? "deserialization" : "http",
                FrameworkId = "aspnetcore-mvc",
                EndpointPath = endpoint.Path ?? endpoint.Route ?? string.Empty,
                Confidence = model.GetDeclaredSymbol(method) is null ? ConfidenceTiers.Syntactic : ConfidenceTiers.Semantic
            });
        }
    }

    /// <summary>Binding source per ASP.NET rules: explicit attribute, route-template match, or body for complex types.</summary>
    private static string? BindingSourceOf(ParameterSyntax parameter, IReadOnlyList<string> routeParameterNames)
    {
        var attributes = ProviderHelpers.AttributesOf(parameter.AttributeLists).ToList();
        if (attributes.Any(a => ProviderHelpers.IsNamed(a, "FromServices") || ProviderHelpers.IsNamed(a, "BindNever")))
        {
            return null;
        }

        foreach (var attribute in attributes)
        {
            var name = ProviderHelpers.NormalizeAttributeName(attribute);
            switch (name)
            {
                case "FromRoute": return "http-route";
                case "FromQuery": return "http-query";
                case "FromBody": return "http-body";
                case "FromForm": return "http-form";
                case "FromHeader": return "http-header";
            }
        }

        if (routeParameterNames.Any(routeParameter => routeParameter.Equals(parameter.Identifier.Text, StringComparison.OrdinalIgnoreCase)))
        {
            return "http-route";
        }

        var typeName = parameter.Type?.ToString() ?? string.Empty;
        var isSimple = SimpleTypes.Contains(TrimNullable(typeName));
        return isSimple ? "http-query" : "http-body";
    }

    private static string TrimNullable(string typeName) => typeName.EndsWith("?", StringComparison.Ordinal) ? typeName[..^1] : typeName;

    private static readonly HashSet<string> SimpleTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "string", "int", "long", "short", "byte", "bool", "decimal", "double", "float", "DateTime", "DateTimeOffset", "Guid", "TimeSpan", "char",
        "System.String", "System.Int32", "System.Int64", "System.Int16", "System.Byte", "System.Boolean", "System.Decimal", "System.Double", "System.Single", "System.DateTime", "System.DateTimeOffset", "System.Guid", "System.TimeSpan", "System.Char",
        "IFormFile", "CancellationToken", "Stream"
    };

    private static void BindRouteParameters(ApiEndpoint endpoint, List<RouteParameter> templateParameters, MethodDeclarationSyntax method, List<AttributeSyntax> methodAttributes, SemanticModel model)
    {
        var methodSymbol = model.GetDeclaredSymbol(method);
        var routeParameterNames = templateParameters.Select(parameter => parameter.Name).ToList();
        endpoint.RouteParameters = templateParameters.Select(templateParameter =>
        {
            var parameter = method.ParameterList.Parameters.FirstOrDefault(p => p.Identifier.Text.Equals(templateParameter.Name, StringComparison.OrdinalIgnoreCase));
            var clrType = parameter is null ? null : methodSymbol?.Parameters.FirstOrDefault(p => p.Name == parameter.Identifier.Text)?.Type.ToDisplayString() ?? parameter.Type?.ToString();
            var binding = parameter is null ? "Route" : BindingSourceOf(parameter, routeParameterNames) is { } source && source.StartsWith("http-", StringComparison.Ordinal) ? source["http-".Length..] : "Route";
            return new RouteParameter
            {
                Name = templateParameter.Name,
                Constraints = templateParameter.Constraints,
                Optional = templateParameter.Optional,
                DefaultValue = templateParameter.DefaultValue,
                CatchAll = templateParameter.CatchAll,
                BindingSource = binding is "route" or "Route" ? "Route" : binding is "query" ? "Query" : binding is "body" ? "Body" : binding is "form" ? "Form" : binding is "header" ? "Header" : "Route",
                ClrType = clrType
            };
        }).ToList();
    }

    private static void ApplyContentTypesAndVersion(ApiEndpoint endpoint, List<AttributeSyntax> methodAttributes, List<AttributeSyntax> classAttributes)
    {
        foreach (var attribute in methodAttributes.Concat(classAttributes))
        {
            var name = ProviderHelpers.NormalizeAttributeName(attribute);
            if (name.Equals("Consumes", StringComparison.OrdinalIgnoreCase) || name.Equals("Produces", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var value in ProviderHelpers.AttributeStringArguments(attribute)) ProviderHelpers.AddDistinct(endpoint.ContentTypes, value);
            }
        }
    }

    /// <summary>
    ///     API versions an action serves: [MapToApiVersion] pins an action to specific versions;
    ///     otherwise every controller-level (plus any action-level) [ApiVersion] applies.
    /// </summary>
    private static List<string> EffectiveApiVersions(List<AttributeSyntax> methodAttributes, List<AttributeSyntax> classAttributes)
    {
        var mapped = VersionArguments(methodAttributes, "MapToApiVersion");
        if (mapped.Count > 0)
        {
            return mapped;
        }

        var versions = VersionArguments(classAttributes, "ApiVersion");
        foreach (var version in VersionArguments(methodAttributes, "ApiVersion"))
        {
            ProviderHelpers.AddDistinct(versions, version);
        }

        return versions;
    }

    private static List<string> VersionArguments(List<AttributeSyntax> attributes, string name)
    {
        var versions = new List<string>();
        foreach (var attribute in attributes.Where(attribute => ProviderHelpers.IsNamed(attribute, name)))
        {
            foreach (var version in ProviderHelpers.AttributeStringArguments(attribute))
            {
                ProviderHelpers.AddDistinct(versions, version);
            }
        }

        return versions;
    }

    private static List<AttributeSyntax> classAttributes(ControllerCandidate controller) => ProviderHelpers.AttributesOf(controller.Type.AttributeLists).ToList();

    private static string ActionNameFrom(List<AttributeSyntax> methodAttributes, string methodName)
    {
        var actionName = methodAttributes
            .Where(attribute => ProviderHelpers.IsNamed(attribute, "ActionName"))
            .Select(attribute => ProviderHelpers.RouteTemplateOf(attribute, null))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        return actionName ?? RouteTemplateResolver.ActionName(methodName);
    }

    private static string? AreaFromNamespace(string? namespaceName)
    {
        if (string.IsNullOrWhiteSpace(namespaceName))
        {
            return null;
        }

        var segments = namespaceName.Split('.');
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i].Equals("Areas", StringComparison.Ordinal))
            {
                return segments[i + 1];
            }
        }

        return null;
    }

    private static string? RequestTypeOf(IMethodSymbol? methodSymbol) =>
        methodSymbol?.Parameters.FirstOrDefault(parameter => parameter.Type is { TypeKind: not TypeKind.Error, SpecialType: SpecialType.None } && !parameter.Type.Name.Contains("CancellationToken", StringComparison.Ordinal))?.Type.ToDisplayString();

    private static void AnalyzeConventionalRoutes(FrameworkContext ctx, FrameworkResults results, List<ControllerCandidate> controllers, List<(string Name, string Pattern)> patterns, Dictionary<string, ServiceComponent> servicesById)
    {
        if (patterns.Count == 0)
        {
            return;
        }

        var emittedIds = new HashSet<string>(StringComparer.Ordinal);
        var emitted = 0;
        foreach (var pattern in patterns)
        {
            // Attribute-routed controllers (a [Route] on the class or any action) are removed from
            // conventional routing by MVC itself; expanding them here would double-report every
            // action under both route shapes.
            foreach (var controller in controllers.Where(c => !c.ApiController && !IsAttributeRouted(c)))
            {
                var controllerName = RouteTemplateResolver.ControllerName(controller.Type.Identifier.Text);
                foreach (var method in controller.Type.Members.OfType<MethodDeclarationSyntax>())
                {
                    var methodAttributes = ProviderHelpers.AttributesOf(method.AttributeLists).ToList();
                    if (methodAttributes.Any(a => ProviderHelpers.IsNamed(a, "NonAction")) ||
                        !method.Modifiers.Any(modifier => modifier.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PublicKeyword)))
                    {
                        continue;
                    }

                    // Overloads conventionally route to the same path; one entry point per path.
                    var actionName = ActionNameFrom(methodAttributes, method.Identifier.Text);
                    var resolved = RouteTemplateResolver.ExpandConventional(pattern.Pattern, controllerName, actionName);
                    if (resolved.Path is null || emitted >= ctx.MaxConventionalRoutes)
                    {
                        continue;
                    }

                    var verbs = ActionVerbs(methodAttributes, controller.Model)
                        .Where(verb => verb.Template.Text is null)
                        .Select(verb => verb.HttpMethod)
                        .DefaultIfEmpty("ANY")
                        .Distinct(StringComparer.Ordinal);
                    var lineSpan = method.GetLocation().GetLineSpan().StartLinePosition;
                    foreach (var httpMethod in verbs)
                    {
                        emitted++;
                        var endpoint = new ApiEndpoint
                        {
                            Path = resolved.Path,
                            FilePath = CodeLocation.From(ctx.BasePath, controller.FilePath).Path,
                            FileName = Path.GetFileName(controller.FilePath),
                            Namespace = controller.Type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString(),
                            ClassName = controller.Type.Identifier.Text,
                            MethodName = method.Identifier.Text,
                            HttpMethod = httpMethod,
                            Route = pattern.Pattern,
                            EndpointKind = "Attribute",
                            RoutingKind = "Conventional",
                            Framework = "aspnetcore-mvc",
                            Confidence = ConfidenceTiers.Syntactic,
                            LineNumber = lineSpan.Line + 1,
                            ColumnNumber = lineSpan.Character + 1,
                            RawUrls = controller.RawUrls,
                            RouteParameters = resolved.Parameters,
                            Evidence = new AnalysisEvidence
                            {
                                Kind = AnalysisEvidenceKind.FrameworkModel,
                                Source = "aspnetcore-mvc",
                                Description = $"Expanded conventional route pattern '{pattern.Pattern}'.",
                                Confidence = ConfidenceTiers.Syntactic,
                                FileName = Path.GetFileName(controller.FilePath),
                                LineNumber = lineSpan.Line + 1
                            }
                        };
                        ProviderHelpers.ApplyAuthorizationMetadata(endpoint, ProviderHelpers.AttributesOf(controller.Type.AttributeLists).Concat(methodAttributes));
                        var methodSymbol = controller.Model.GetDeclaredSymbol(method);
                        var methodId = methodSymbol is null ? null : Depscan.Dosai.FormatMethodSignature(methodSymbol);
                        var serviceId = FrameworkIds.Service("aspnetcore-mvc", endpoint.Namespace, controllerName);
                        var operationId = FrameworkIds.Operation(serviceId, httpMethod, resolved.Path, actionName);
                        endpoint.ServiceId = serviceId;
                        endpoint.OperationId = operationId;
                        if (!emittedIds.Add(operationId))
                        {
                            continue;
                        }

                        results.ApiEndpoints.Add(endpoint);
                        // Conventional actions must land on the controller service like attribute
                        // actions do: without the operation, LinkEntryPoints cannot derive the
                        // service's EntryPointIds and every conventional endpoint orphans away
                        // from its service.
                        if (servicesById.TryGetValue(serviceId, out var owner))
                        {
                            owner.Operations.Add(new ServiceOperation
                            {
                                Id = operationId,
                                Name = actionName,
                                HttpMethod = httpMethod,
                                Path = resolved.Path,
                                RouteTemplate = pattern.Pattern,
                                RouteParameters = resolved.Parameters,
                                RequestType = RequestTypeOf(methodSymbol),
                                ResponseType = methodSymbol?.ReturnType?.ToDisplayString(),
                                MethodId = methodId,
                                Authenticated = endpoint.AuthorizationRequired,
                                Confidence = ConfidenceTiers.Syntactic,
                                Location = CodeLocation.From(ctx.BasePath, controller.FilePath, endpoint.LineNumber, endpoint.ColumnNumber)
                            });
                            if (resolved.Path is not null && !owner.Endpoints.Contains(resolved.Path, StringComparer.Ordinal))
                            {
                                owner.Endpoints.Add(resolved.Path);
                            }

                            if (methodId is not null && !owner.MethodIds.Contains(methodId, StringComparer.Ordinal))
                            {
                                owner.MethodIds.Add(methodId);
                            }
                        }

                        results.EntryPoints.Add(new EntryPoint
                        {
                            Id = $"ep:{operationId}",
                            Kind = "HttpController",
                            MethodId = methodId,
                            MethodName = method.Identifier.Text,
                            ClassName = controller.Type.Identifier.Text,
                            Namespace = endpoint.Namespace,
                            FileName = endpoint.FileName,
                            Path = endpoint.FilePath,
                            LineNumber = endpoint.LineNumber,
                            ColumnNumber = endpoint.ColumnNumber,
                            HttpMethod = httpMethod,
                            Route = resolved.Path,
                            AuthorizationRequired = endpoint.AuthorizationRequired,
                            AuthorizationPolicies = endpoint.AuthorizationPolicies,
                            Roles = endpoint.Roles,
                            AllowAnonymous = endpoint.AllowAnonymous,
                            AuthenticationSchemes = endpoint.AuthenticationSchemes,
                            RawUrls = endpoint.RawUrls
                        });
                    }
                }
            }
        }

        if (emitted >= ctx.MaxConventionalRoutes)
        {
            ctx.Diagnostics.Add(new FrameworkDiagnostic("aspnetcore-mvc", $"Conventional route expansion was capped at {ctx.MaxConventionalRoutes} endpoints (configure with --max-conventional-routes)."));
        }
    }

    private static bool IsAttributeRouted(ControllerCandidate controller) =>
        ProviderHelpers.AttributesOf(controller.Type.AttributeLists).Any(attribute => ProviderHelpers.IsNamed(attribute, "Route")) ||
        controller.Type.Members.OfType<MethodDeclarationSyntax>().Any(method => ProviderHelpers.AttributesOf(method.AttributeLists).Any(attribute => ProviderHelpers.IsNamed(attribute, "Route")));

    private sealed record ControllerCandidate(TypeDeclarationSyntax Type, SemanticModel Model, string FilePath, List<string> RawUrls, string Confidence, bool ApiController);
}
