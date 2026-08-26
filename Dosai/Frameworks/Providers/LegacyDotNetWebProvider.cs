using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Depscan.Frameworks.Providers;

/// <summary>
///     Legacy .NET web stacks: ASP.NET Web API 2 (ApiController + [RoutePrefix]), MVC 5, WCF
///     ([ServiceContract]/[OperationContract] with optional [WebGet]/[WebInvoke] REST), and ASMX
///     ([WebMethod]). WCF endpoint addresses come from web.config/app.config system.serviceModel.
/// </summary>
public sealed class LegacyDotNetWebProvider : IFrameworkProvider
{
    public string Id => "legacy-web";

    public string DisplayName => "Legacy .NET Web (Web API 2, MVC5, WCF, ASMX)";

    public bool AppliesTo(FrameworkContext ctx) => ctx.CSharp is not null || ctx.ConfigFiles.Any(file => Path.GetFileName(file).Contains(".svc", StringComparison.OrdinalIgnoreCase));

    public void Analyze(FrameworkContext ctx, FrameworkResults results)
    {
        if (ctx.CSharp is not null)
        {
            foreach (var tree in ctx.CSharpTrees)
            {
                var model = ctx.CSharp.GetSemanticModel(tree);
                var root = tree.GetCompilationUnitRoot();
                var rawUrls = ctx.RawUrlsFor(tree);
                foreach (var typeDeclaration in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
                {
                    AnalyzeType(ctx, results, typeDeclaration, model, tree.FilePath, rawUrls);
                }
            }
        }

        AnalyzeServiceModelConfigs(ctx, results);
    }

    private static void AnalyzeType(FrameworkContext ctx, FrameworkResults results, TypeDeclarationSyntax typeDeclaration, SemanticModel model, string filePath, List<string> rawUrls)
    {
        var attributes = ProviderHelpers.AttributesOf(typeDeclaration.AttributeLists).ToList();
        if (attributes.Any(attribute => ProviderHelpers.IsNamed(attribute, "NonController")))
        {
            return;
        }

        var symbol = model.GetDeclaredSymbol(typeDeclaration);
        var typeName = typeDeclaration.Identifier.Text;
        var namespaceName = typeDeclaration.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();

        // ---- WCF: [ServiceContract] on interfaces and classes.
        if (attributes.Any(attribute => ProviderHelpers.IsNamed(attribute, "ServiceContract")))
        {
            AnalyzeWcfContract(ctx, results, typeDeclaration, attributes, symbol, typeName, namespaceName, filePath, rawUrls, model);
        }

        // ---- ASMX: [WebMethod] anywhere in the type.
        if (typeDeclaration.Members.OfType<MethodDeclarationSyntax>().Any(method => ProviderHelpers.AttributesOf(method.AttributeLists).Any(attribute => ProviderHelpers.IsNamed(attribute, "WebMethod"))))
        {
            AnalyzeAsmx(ctx, results, typeDeclaration, symbol, typeName, namespaceName, filePath, rawUrls);
        }

        // Types already handled by the ASP.NET Core MVC provider must not be double-reported here.
        if (ctx.HandledTypeIds.Contains($"{filePath}:{typeName}"))
        {
            return;
        }

        // ---- Web API 2 / MVC 5: ApiController or Controller base (System.Web.Http / System.Web.Mvc).
        var isApiController = symbol is not null && ProviderHelpers.DerivesFromAny(symbol, "ApiController");
        var isLegacyController = symbol is not null && ProviderHelpers.DerivesFromAny(symbol, "Controller") && !ProviderHelpers.DerivesFromAny(symbol, "ControllerBase");
        if (!isApiController && !isLegacyController && !typeName.EndsWith("Controller", StringComparison.Ordinal))
        {
            return;
        }

        // Same discovery rule as the Core provider: abstract shared bases, static helpers, generic
        // and non-public types are never routed, so they must not become services here either.
        if (typeDeclaration is not ClassDeclarationSyntax ||
            typeDeclaration.Modifiers.Any(SyntaxKind.AbstractKeyword) ||
            typeDeclaration.Modifiers.Any(SyntaxKind.StaticKeyword) ||
            typeDeclaration.TypeParameterList is not null)
        {
            return;
        }

        var confidence = isApiController || isLegacyController ? ConfidenceTiers.Semantic : ConfidenceTiers.Syntactic;
        var classRoutePrefix = attributes
            .Where(attribute => ProviderHelpers.IsNamed(attribute, "RoutePrefix") || ProviderHelpers.IsNamed(attribute, "Route"))
            .Select(attribute => ProviderHelpers.RouteTemplateOf(attribute, model))
            .FirstOrDefault(route => !string.IsNullOrWhiteSpace(route));

        foreach (var method in typeDeclaration.Members.OfType<MethodDeclarationSyntax>())
        {
            var methodAttributes = ProviderHelpers.AttributesOf(method.AttributeLists).ToList();
            if (methodAttributes.Any(attribute => ProviderHelpers.IsNamed(attribute, "NonAction")))
            {
                continue;
            }

            if (!method.Modifiers.Any(SyntaxKind.PublicKeyword) ||
                method.Modifiers.Any(SyntaxKind.StaticKeyword) ||
                method.TypeParameterList is not null)
            {
                continue;
            }

            var routed = AttributeRoutedActions(methodAttributes, model);
            if (routed.Count > 0)
            {
                foreach (var (httpMethod, template) in routed)
                {
                    var verbatim = RouteTemplateResolver.Combine(classRoutePrefix, template);
                    var resolved = RouteTemplateResolver.Resolve(verbatim, new RouteTokenValues { Controller = RouteTemplateResolver.ControllerName(typeName), Action = RouteTemplateResolver.ActionName(method.Identifier.Text) });
                    AddLegacyEndpoint(ctx, results, method, methodAttributes, attributes, httpMethod, verbatim, resolved, typeName, namespaceName, filePath, rawUrls, confidence, model);
                }
            }
            else if (isApiController && classRoutePrefix is null)
            {
                // Web API 2's default route is api/{controller}/{id}: the action is selected by the
                // HTTP verb and method-name convention, not by an action path segment. Emitting
                // /api/{controller}/{action} here would fabricate routes the app does not serve.
                var (conventionVerb, hasConvention) = VerbByMethodNamePrefix(method.Identifier.Text);
                if (hasConvention)
                {
                    var controllerName = RouteTemplateResolver.ControllerName(typeName);
                    var resolved = RouteTemplateResolver.Resolve($"/api/{controllerName}/{{id?}}");
                    AddLegacyEndpoint(ctx, results, method, methodAttributes, attributes, conventionVerb, "api/{controller}/{id}", resolved, typeName, namespaceName, filePath, rawUrls, ConfidenceTiers.Syntactic, model);
                }
            }
        }
    }

    private static List<(string HttpMethod, string? Template)> AttributeRoutedActions(List<AttributeSyntax> attributes, SemanticModel model)
    {
        var actions = new List<(string, string?)>();
        foreach (var attribute in attributes)
        {
            var name = ProviderHelpers.NormalizeAttributeName(attribute);
            switch (name)
            {
                case "HttpGet": actions.Add(("GET", ProviderHelpers.RouteTemplateOf(attribute, model))); break;
                case "HttpPost": actions.Add(("POST", ProviderHelpers.RouteTemplateOf(attribute, model))); break;
                case "HttpPut": actions.Add(("PUT", ProviderHelpers.RouteTemplateOf(attribute, model))); break;
                case "HttpDelete": actions.Add(("DELETE", ProviderHelpers.RouteTemplateOf(attribute, model))); break;
                case "HttpPatch": actions.Add(("PATCH", ProviderHelpers.RouteTemplateOf(attribute, model))); break;
                case "HttpHead": actions.Add(("HEAD", ProviderHelpers.RouteTemplateOf(attribute, model))); break;
                case "HttpOptions": actions.Add(("OPTIONS", ProviderHelpers.RouteTemplateOf(attribute, model))); break;
                case "AcceptVerbs":
                    foreach (var verb in ProviderHelpers.AttributeStringArguments(attribute))
                    {
                        actions.Add((verb.ToUpperInvariant(), null));
                    }

                    break;
                case "Route":
                    actions.Add(("ANY", ProviderHelpers.RouteTemplateOf(attribute, model)));
                    break;
            }
        }

        return actions;
    }

    private static (string Verb, bool Found) VerbByMethodNamePrefix(string methodName)
    {
        foreach (var (prefix, verb) in new[] { ("Get", "GET"), ("Post", "POST"), ("Put", "PUT"), ("Delete", "DELETE"), ("Patch", "PATCH"), ("Head", "HEAD"), ("Options", "OPTIONS") })
        {
            if (methodName.StartsWith(prefix, StringComparison.Ordinal))
            {
                return (verb, true);
            }
        }

        return (string.Empty, false);
    }

    private static void AddLegacyEndpoint(
        FrameworkContext ctx,
        FrameworkResults results,
        MethodDeclarationSyntax method,
        List<AttributeSyntax> methodAttributes,
        List<AttributeSyntax> classAttributes,
        string httpMethod,
        string? verbatim,
        ResolvedRouteTemplate resolved,
        string typeName,
        string? namespaceName,
        string filePath,
        List<string> rawUrls,
        string confidence,
        SemanticModel model)
    {
        var lineSpan = method.GetLocation().GetLineSpan().StartLinePosition;
        var methodSymbol = model.GetDeclaredSymbol(method);
        var methodId = methodSymbol is null ? null : Depscan.Dosai.FormatMethodSignature(methodSymbol);
        var serviceId = FrameworkIds.Service("legacy-web", namespaceName, RouteTemplateResolver.ControllerName(typeName));
        var operationId = FrameworkIds.Operation(serviceId, httpMethod, resolved.Path, method.Identifier.Text);

        var endpoint = new ApiEndpoint
        {
            Path = resolved.Path,
            FilePath = CodeLocation.From(ctx.BasePath, filePath).Path,
            FileName = Path.GetFileName(filePath),
            Namespace = namespaceName,
            ClassName = typeName,
            MethodName = method.Identifier.Text,
            HttpMethod = httpMethod,
            Route = verbatim,
            EndpointKind = "Attribute",
            RoutingKind = "Attribute",
            Framework = "legacy-web",
            ServiceId = serviceId,
            OperationId = operationId,
            Confidence = resolved.Path is null ? "low" : confidence,
            LineNumber = lineSpan.Line + 1,
            ColumnNumber = lineSpan.Character + 1,
            RawUrls = rawUrls,
            RouteParameters = resolved.Parameters,
            Evidence = new AnalysisEvidence
            {
                Kind = AnalysisEvidenceKind.FrameworkModel,
                Source = "legacy-web",
                Description = "Legacy ASP.NET web endpoint (Web API 2 / MVC 5).",
                Confidence = resolved.Path is null ? "low" : confidence,
                FileName = Path.GetFileName(filePath),
                LineNumber = lineSpan.Line + 1
            }
        };
        ProviderHelpers.ApplyAuthorizationMetadata(endpoint, classAttributes.Concat(methodAttributes));
        results.ApiEndpoints.Add(endpoint);
        AddServiceAndEntry(ctx, results, serviceId, typeName, namespaceName, endpoint, operationId, filePath, lineSpan, methodId, ServiceKinds.Http);
    }

    private static void AnalyzeWcfContract(FrameworkContext ctx, FrameworkResults results, TypeDeclarationSyntax typeDeclaration, List<AttributeSyntax> classAttributes, INamedTypeSymbol? symbol, string typeName, string? namespaceName, string filePath, List<string> rawUrls, SemanticModel model)
    {
        var serviceId = FrameworkIds.Service("legacy-wcf", namespaceName, typeName);
        var service = new ServiceComponent
        {
            Id = serviceId,
            Name = typeName,
            Group = namespaceName,
            ServiceKind = ServiceKinds.Soap,
            Direction = ServiceDirections.Inbound,
            Framework = "legacy-wcf",
            Confidence = symbol is null ? ConfidenceTiers.Syntactic : ConfidenceTiers.Semantic,
            Location = CodeLocation.From(ctx.BasePath, filePath),
            Evidence = new AnalysisEvidence { Kind = AnalysisEvidenceKind.FrameworkModel, Source = "legacy-wcf", Description = "WCF [ServiceContract] type.", Confidence = symbol is null ? ConfidenceTiers.Syntactic : ConfidenceTiers.Semantic }
        };
        results.Services.Add(service);

        foreach (var method in typeDeclaration.Members.OfType<MethodDeclarationSyntax>())
        {
            var methodAttributes = ProviderHelpers.AttributesOf(method.AttributeLists).ToList();
            if (!methodAttributes.Any(attribute => ProviderHelpers.IsNamed(attribute, "OperationContract")))
            {
                continue;
            }

            var webGet = methodAttributes.FirstOrDefault(attribute => ProviderHelpers.IsNamed(attribute, "WebGet"));
            var webInvoke = methodAttributes.FirstOrDefault(attribute => ProviderHelpers.IsNamed(attribute, "WebInvoke"));
            string? httpMethod = null;
            string? uriTemplate = null;
            if (webGet is not null)
            {
                httpMethod = "GET";
                uriTemplate = ProviderHelpers.AttributeArgumentText(webGet, model) ?? NamedArgument(webGet, "UriTemplate", model);
            }
            else if (webInvoke is not null)
            {
                httpMethod = NamedArgument(webInvoke, "Method", model) ?? "POST";
                uriTemplate = NamedArgument(webInvoke, "UriTemplate", model);
            }

            var lineSpan = method.GetLocation().GetLineSpan().StartLinePosition;
            var methodSymbol = model.GetDeclaredSymbol(method);
            var methodId = methodSymbol is null ? null : Depscan.Dosai.FormatMethodSignature(methodSymbol);
            var resolved = uriTemplate is null ? new ResolvedRouteTemplate() : RouteTemplateResolver.Resolve(uriTemplate);
            var operationId = FrameworkIds.Operation(serviceId, httpMethod, resolved.Path, method.Identifier.Text);

            if (httpMethod is not null)
            {
                var endpoint = new ApiEndpoint
                {
                    Path = resolved.Path,
                    FilePath = CodeLocation.From(ctx.BasePath, filePath).Path,
                    FileName = Path.GetFileName(filePath),
                    Namespace = namespaceName,
                    ClassName = typeName,
                    MethodName = method.Identifier.Text,
                    HttpMethod = httpMethod,
                    Route = uriTemplate,
                    EndpointKind = "Attribute",
                    RoutingKind = "Attribute",
                    Framework = "legacy-wcf",
                    ServiceId = serviceId,
                    OperationId = operationId,
                    Confidence = ConfidenceTiers.Syntactic,
                    LineNumber = lineSpan.Line + 1,
                    ColumnNumber = lineSpan.Character + 1,
                    RawUrls = rawUrls,
                    RouteParameters = resolved.Parameters,
                    Evidence = new AnalysisEvidence { Kind = AnalysisEvidenceKind.FrameworkModel, Source = "legacy-wcf", Description = $"WCF REST operation ({httpMethod}).", Confidence = ConfidenceTiers.Syntactic, FileName = Path.GetFileName(filePath), LineNumber = lineSpan.Line + 1 }
                };
                ProviderHelpers.ApplyAuthorizationMetadata(endpoint, classAttributes.Concat(methodAttributes));
                results.ApiEndpoints.Add(endpoint);
                AddServiceAndEntry(ctx, results, serviceId, typeName, namespaceName, endpoint, operationId, filePath, lineSpan, methodId, ServiceKinds.Http, framework: "legacy-wcf", kindOverride: "Soap");
                service.ServiceKind = ServiceKinds.Http;
            }

            service.Operations.Add(new ServiceOperation
            {
                Id = operationId,
                Name = method.Identifier.Text,
                HttpMethod = httpMethod,
                Path = resolved.Path,
                RouteTemplate = uriTemplate,
                MethodId = methodId,
                RequestType = methodSymbol?.Parameters.FirstOrDefault()?.Type.ToDisplayString(),
                ResponseType = methodSymbol?.ReturnType.ToDisplayString(),
                Confidence = ConfidenceTiers.Syntactic,
                Location = CodeLocation.From(ctx.BasePath, filePath, lineSpan.Line + 1)
            });
        }
    }

    private static void AnalyzeAsmx(FrameworkContext ctx, FrameworkResults results, TypeDeclarationSyntax typeDeclaration, INamedTypeSymbol? symbol, string typeName, string? namespaceName, string filePath, List<string> rawUrls)
    {
        var serviceId = FrameworkIds.Service("legacy-web", namespaceName, typeName);
        foreach (var method in typeDeclaration.Members.OfType<MethodDeclarationSyntax>())
        {
            if (!ProviderHelpers.AttributesOf(method.AttributeLists).Any(attribute => ProviderHelpers.IsNamed(attribute, "WebMethod")))
            {
                continue;
            }

            var lineSpan = method.GetLocation().GetLineSpan().StartLinePosition;
            var resolved = RouteTemplateResolver.Resolve($"/{typeName}/{method.Identifier.Text}");
            var operationId = FrameworkIds.Operation(serviceId, "POST", resolved.Path, method.Identifier.Text);
            var endpoint = new ApiEndpoint
            {
                Path = resolved.Path,
                FilePath = CodeLocation.From(ctx.BasePath, filePath).Path,
                FileName = Path.GetFileName(filePath),
                Namespace = namespaceName,
                ClassName = typeName,
                MethodName = method.Identifier.Text,
                HttpMethod = "POST",
                Route = resolved.NormalizedTemplate,
                EndpointKind = "Attribute",
                RoutingKind = "Attribute",
                Framework = "legacy-web",
                ServiceId = serviceId,
                OperationId = operationId,
                Confidence = ConfidenceTiers.Heuristic,
                LineNumber = lineSpan.Line + 1,
                ColumnNumber = lineSpan.Character + 1,
                RawUrls = rawUrls,
                Evidence = new AnalysisEvidence { Kind = AnalysisEvidenceKind.FrameworkModel, Source = "legacy-web", Description = "ASMX [WebMethod] (SOAP over HTTP POST).", Confidence = ConfidenceTiers.Heuristic, FileName = Path.GetFileName(filePath), LineNumber = lineSpan.Line + 1 }
            };
            results.ApiEndpoints.Add(endpoint);
            AddServiceAndEntry(ctx, results, serviceId, typeName, namespaceName, endpoint, operationId, filePath, lineSpan, null, ServiceKinds.Soap, framework: "legacy-web", kindOverride: "Soap");
        }
    }

    /// <summary>Parses system.serviceModel sections for endpoint addresses and bindings (web.config / app.config).</summary>
    private static void AnalyzeServiceModelConfigs(FrameworkContext ctx, FrameworkResults results)
    {
        foreach (var configFile in ctx.ConfigFiles)
        {
            if (!Path.GetFileName(configFile).EndsWith(".config", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            XDocument document;
            try
            {
                document = XDocument.Load(configFile);
            }
            catch (Exception)
            {
                continue; // Malformed config: best-effort skip.
            }

            var serviceModel = document.Root?.Element("system.serviceModel");
            if (serviceModel is null)
            {
                continue;
            }

            foreach (var endpointElement in serviceModel.Element("services")?.Elements("service").SelectMany(service => service.Elements("endpoint")) ?? [])
            {
                var address = (string?)endpointElement.Attribute("address");
                var binding = (string?)endpointElement.Attribute("binding") ?? "basicHttpBinding";
                if (string.IsNullOrWhiteSpace(address))
                {
                    continue;
                }

                var contract = (string?)endpointElement.Attribute("contract") ?? "unknown";
                var security = endpointElement.Parent?.Element("security") is { } securityElement && (string?)securityElement.Attribute("mode") is { } mode ? mode : "None";
                var serviceId = FrameworkIds.Service("legacy-wcf", null, contract);
                var service = new ServiceComponent
                {
                    Id = serviceId,
                    Name = contract,
                    ServiceKind = binding.Contains("webHttp", StringComparison.Ordinal) ? ServiceKinds.Http : ServiceKinds.Soap,
                    Direction = ServiceDirections.Inbound,
                    Framework = "legacy-wcf",
                    Confidence = ConfidenceTiers.Heuristic,
                    Endpoints = [address],
                    TrustZone = TrustZones.Unknown,
                    Location = CodeLocation.From(ctx.BasePath, configFile),
                    Evidence = new AnalysisEvidence { Kind = AnalysisEvidenceKind.FrameworkModel, Source = "legacy-wcf", Description = "WCF endpoint declared in system.serviceModel config.", Confidence = ConfidenceTiers.Heuristic }
                };
                service.Properties["binding"] = binding;
                service.Properties["securityMode"] = security;
                if (binding.Equals("basicHttpBinding", StringComparison.Ordinal) && string.Equals(security, "None", StringComparison.OrdinalIgnoreCase))
                {
                    service.Tags.Add("finding:basicHttpBinding-without-transport-security");
                }

                results.Services.Add(service);
            }
        }
    }

    private static string? NamedArgument(AttributeSyntax attribute, string name, SemanticModel? model)
    {
        var argument = attribute.ArgumentList?.Arguments.FirstOrDefault(candidate => string.Equals(candidate.NameEquals?.Name.ToString(), name, StringComparison.OrdinalIgnoreCase));
        if (argument is null)
        {
            return null;
        }

        return argument.Expression is LiteralExpressionSyntax literal ? literal.Token.ValueText : null;
    }

    private static void AddServiceAndEntry(
        FrameworkContext ctx,
        FrameworkResults results,
        string serviceId,
        string typeName,
        string? namespaceName,
        ApiEndpoint endpoint,
        string operationId,
        string filePath,
        LinePosition lineSpan,
        string? methodId,
        string serviceKind,
        string framework = "legacy-web",
        string kindOverride = "HttpController")
    {
        var service = results.Services.FirstOrDefault(existing => existing.Id == serviceId);
        if (service is null)
        {
            service = new ServiceComponent
            {
                Id = serviceId,
                Name = RouteTemplateResolver.ControllerName(typeName),
                Group = namespaceName,
                ServiceKind = serviceKind,
                Direction = ServiceDirections.Inbound,
                Framework = framework,
                Confidence = endpoint.Confidence,
                Location = CodeLocation.From(ctx.BasePath, filePath)
            };
            results.Services.Add(service);
        }

        service.Operations.Add(new ServiceOperation
        {
            Id = operationId,
            Name = endpoint.MethodName ?? typeName,
            HttpMethod = endpoint.HttpMethod,
            Path = endpoint.Path,
            RouteTemplate = endpoint.Route,
            RouteParameters = endpoint.RouteParameters,
            MethodId = methodId,
            Authenticated = endpoint.AuthorizationRequired,
            Confidence = endpoint.Confidence,
            Location = CodeLocation.From(ctx.BasePath, filePath, lineSpan.Line + 1, lineSpan.Character + 1)
        });
        if (endpoint.Path is not null && !service.Endpoints.Contains(endpoint.Path, StringComparer.Ordinal))
        {
            service.Endpoints.Add(endpoint.Path);
        }

        results.EntryPoints.Add(new EntryPoint
        {
            Id = $"ep:{operationId}",
            Kind = kindOverride,
            MethodId = methodId,
            MethodName = endpoint.MethodName,
            ClassName = endpoint.ClassName,
            Namespace = endpoint.Namespace,
            FileName = endpoint.FileName,
            Path = endpoint.FilePath,
            LineNumber = endpoint.LineNumber,
            ColumnNumber = endpoint.ColumnNumber,
            HttpMethod = endpoint.HttpMethod,
            Route = endpoint.Path ?? endpoint.Route,
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
    }
}
