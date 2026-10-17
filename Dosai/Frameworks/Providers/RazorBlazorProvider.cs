using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Depscan.Frameworks.Providers;

/// <summary>
///     Razor Pages (.cshtml @page) and Blazor (.razor @page, [Route] components). Template files
///     are parsed textually (heuristic tier): @page directives, On{Verb} handler methods including
///     named handlers, @attribute [Authorize], and Blazor interactive render modes.
/// </summary>
public sealed partial class RazorBlazorProvider : IFrameworkProvider
{
    public string Id => "razor-blazor";

    public string DisplayName => "Razor Pages / Blazor";

    public bool AppliesTo(FrameworkContext ctx) => ctx.TemplateFiles.Count > 0 || ctx.CSharp is not null;

    public void Analyze(FrameworkContext ctx, FrameworkResults results)
    {
        foreach (var templateFile in ctx.TemplateFiles)
        {
            string text;
            try
            {
                text = File.ReadAllText(templateFile);
            }
            catch (IOException)
            {
                continue;
            }

            var pageMatches = PageDirectiveRegex().Matches(text);
            var isBlazor = templateFile.EndsWith(".razor", StringComparison.OrdinalIgnoreCase) || text.Contains("@rendermode", StringComparison.Ordinal) || text.Contains("<PageTitle>", StringComparison.Ordinal);
            // Only @page-declared files are routable: _Imports.razor, App.razor, layouts, and
            // partials are reachable templates/components but never URL endpoints.
            if (pageMatches.Count == 0)
            {
                continue;
            }

            // Every @page directive is a route (a Blazor component may declare several). A bare
            // `@page` routes by file location relative to the Pages/ root, with Index mapping to
            // the directory itself; a Razor Page template that is not absolute (`@page "{id:int}"`)
            // is appended to that conventional route. Derived, so confidence stays heuristic.
            var conventional = ConventionalPageRoute(ctx.BasePath, templateFile);
            var pageRoutes = pageMatches
                .Select(match => match.Groups[1].Success && match.Groups[1].Length > 0 ? match.Groups[1].Value : null)
                .Select(template => template is null ? (Template: (string?)null, Route: conventional)
                    : template.StartsWith('/') || template.StartsWith("~/", StringComparison.Ordinal) || isBlazor ? (Template: template, Route: template.TrimStart('~'))
                    : (Template: template, Route: conventional.TrimEnd('/') + "/" + template))
                .DistinctBy(route => route.Route, StringComparer.Ordinal)
                .ToList();

            var fileName = Path.GetFileName(templateFile);
            var rawUrls = ProviderHelpers.ExtractRawUrls(text);
            // The page's PageModel (`@model X`, else the `X.cshtml.cs` code-behind): its handlers
            // are the endpoints' methods, and its attributes their authorization.
            var pageModel = isBlazor ? null : FindPageModel(ctx, templateFile, text);
            var authorize = text.Contains("[Authorize]", StringComparison.Ordinal) || Regex.IsMatch(text, @"@attribute\s*\[Authorize");
            var allowAnonymous = text.Contains("[AllowAnonymous]", StringComparison.Ordinal) || Regex.IsMatch(text, @"@attribute\s*\[AllowAnonymous");
            var renderMode = InteractiveRenderMode(text);
            var firstTemplate = pageRoutes[0].Template;
            var lineNumber = LineOf(text, firstTemplate is null ? "@page" : firstTemplate);

            var serviceId = FrameworkIds.Service("razor-blazor", Path.GetDirectoryName(Path.GetRelativePath(ctx.BasePath, templateFile)), Path.GetFileNameWithoutExtension(templateFile));
            var service = new ServiceComponent
            {
                Id = serviceId,
                Name = Path.GetFileNameWithoutExtension(templateFile),
                Group = Path.GetDirectoryName(Path.GetRelativePath(ctx.BasePath, templateFile)),
                ServiceKind = ServiceKinds.Http,
                Direction = ServiceDirections.Inbound,
                Framework = "razor-blazor",
                Confidence = ConfidenceTiers.Heuristic,
                Location = CodeLocation.From(ctx.BasePath, templateFile, lineNumber),
                Evidence = new AnalysisEvidence { Kind = AnalysisEvidenceKind.FrameworkModel, Source = "razor-blazor", Description = $"@page directive in {fileName}.", Confidence = ConfidenceTiers.Heuristic, FileName = fileName, LineNumber = lineNumber }
            };
            if (renderMode is not null)
            {
                service.Properties["renderMode"] = renderMode;
                if (renderMode.Contains("InteractiveServer", StringComparison.Ordinal) || renderMode.Contains("InteractiveWebAssembly", StringComparison.Ordinal))
                {
                    service.Tags.Add("signalr-circuit");
                }
            }

            results.Services.Add(service);

            // Blazor component routes are reachable via any verb on first navigation; Razor Pages
            // expose one endpoint per On{Verb} handler, declared in the PageModel or in @functions.
            var handlers = HandlerVerbs(text).Select(handler => (handler.Verb, handler.HandlerName, Method: (MethodDeclarationSyntax?)null)).ToList();
            foreach (var handler in pageModel?.Handlers ?? [])
            {
                handlers.RemoveAll(existing => existing.Verb == handler.Verb && existing.HandlerName == handler.HandlerName && existing.Method is null);
                handlers.Add(handler);
            }

            if (handlers.Count == 0)
            {
                handlers.Add(("ANY", null, null));
            }

            foreach (var (template, pageRoute) in pageRoutes)
            {
                var resolved = RouteTemplateResolver.Resolve(pageRoute);
                var pagePath = resolved.Path ?? pageRoute;
                if (!service.Endpoints.Contains(pagePath, StringComparer.Ordinal))
                {
                    service.Endpoints.Add(pagePath);
                }

                var routeLine = LineOf(text, template is null ? "@page" : template);
                foreach (var (verb, handlerName, method) in handlers)
                {
                    var path = pagePath;
                    if (handlerName is not null)
                    {
                        // Named handlers are reached with ?handler=name; reflect that in the emitted path.
                        path = $"{path}?handler={handlerName}";
                    }

                    var methodSymbol = method is null || pageModel is null ? null : pageModel.Value.Model.GetDeclaredSymbol(method);
                    var methodId = methodSymbol is null ? null : Depscan.Dosai.FormatMethodSignature(methodSymbol);
                    var operationId = FrameworkIds.Operation(serviceId, verb, path, handlerName ?? "page");
                    var endpoint = new ApiEndpoint
                    {
                        Path = path,
                        FilePath = CodeLocation.From(ctx.BasePath, templateFile).Path,
                        FileName = fileName,
                        Namespace = methodSymbol?.ContainingType?.ContainingNamespace?.ToDisplayString(),
                        ClassName = pageModel?.Type.Identifier.Text,
                        MethodName = method?.Identifier.Text,
                        HttpMethod = verb,
                        Route = pageRoute,
                        EndpointKind = "RazorPage",
                        RoutingKind = isBlazor ? "Attribute" : "Template",
                        Framework = "razor-blazor",
                        ServiceId = serviceId,
                        OperationId = operationId,
                        Confidence = ConfidenceTiers.Heuristic,
                        LineNumber = routeLine,
                        RawUrls = rawUrls,
                        RouteParameters = resolved.Parameters,
                        Evidence = new AnalysisEvidence { Kind = AnalysisEvidenceKind.FrameworkModel, Source = "razor-blazor", Description = isBlazor ? "Blazor component route." : "Razor Page handler.", Confidence = ConfidenceTiers.Heuristic, FileName = fileName, LineNumber = routeLine }
                    };
                    ApplyPageAuthorization(endpoint, pageModel?.Attributes ?? [], text, authorize, allowAnonymous);
                    results.ApiEndpoints.Add(endpoint);
                    service.Operations.Add(new ServiceOperation
                    {
                        Id = operationId,
                        Name = handlerName ?? "page",
                        HttpMethod = verb,
                        Path = path,
                        RouteTemplate = pageRoute,
                        RouteParameters = resolved.Parameters,
                        Authenticated = endpoint.AuthorizationRequired,
                        Confidence = ConfidenceTiers.Heuristic,
                        Location = CodeLocation.From(ctx.BasePath, templateFile, routeLine)
                    });

                    // A PageModel handler's entry point is the handler method itself, so flows,
                    // call paths and reachability start there; a template-only page keeps the
                    // template's location.
                    var handlerTree = method?.SyntaxTree;
                    var handlerLine = method is null ? routeLine : method.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    results.EntryPoints.Add(new EntryPoint
                    {
                        Id = $"ep:{operationId}",
                        Kind = "HttpRazorPage",
                        MethodId = methodId,
                        MethodName = method?.Identifier.Text,
                        ClassName = endpoint.ClassName,
                        Namespace = endpoint.Namespace,
                        FileName = handlerTree is null ? fileName : Path.GetFileName(handlerTree.FilePath),
                        Path = handlerTree is null ? endpoint.FilePath : CodeLocation.From(ctx.BasePath, handlerTree.FilePath).Path,
                        LineNumber = handlerLine,
                        HttpMethod = verb,
                        Route = path,
                        AuthorizationRequired = endpoint.AuthorizationRequired,
                        AuthorizationPolicies = endpoint.AuthorizationPolicies,
                        Roles = endpoint.Roles,
                        AllowAnonymous = endpoint.AllowAnonymous
                    });

                    if (method is not null && methodSymbol is not null)
                    {
                        SeedHandlerParameters(results, method, methodSymbol, methodId, path, resolved.Parameters.Select(parameter => parameter.Name).ToList());
                    }
                }
            }

            if (pageRoutes.Count > 0)
            {
                var first = results.ApiEndpoints[^1];
                service.AllowAnonymous = first.AllowAnonymous ? true : null;
                service.Authenticated = first.AllowAnonymous ? false : first.AuthorizationRequired;
                service.TrustZone = first.AllowAnonymous ? TrustZones.Public : first.AuthorizationRequired == true ? TrustZones.Authenticated : TrustZones.Unknown;
            }
        }

        AnalyzeBlazorComponents(ctx, results);
    }

    /// <summary>Blazor routable components declared in C#: [Route("/x")] on ComponentBase types.</summary>
    private static void AnalyzeBlazorComponents(FrameworkContext ctx, FrameworkResults results)
    {
        if (ctx.CSharp is null)
        {
            return;
        }

foreach (var tree in ctx.CSharpTrees)
        {
            if (!ctx.TextContainsAny(tree, "Route(", "ComponentBase", "IComponent"))
            {
                continue;
            }

            var model = ctx.CSharp.GetSemanticModel(tree);
            var root = tree.GetCompilationUnitRoot();
            foreach (var typeDeclaration in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                var attributes = ProviderHelpers.AttributesOf(typeDeclaration.AttributeLists).ToList();
                var routeAttributes = attributes.Where(attribute => ProviderHelpers.IsNamed(attribute, "Route")).ToList();
                if (routeAttributes.Count == 0)
                {
                    continue;
                }

                var symbol = model.GetDeclaredSymbol(typeDeclaration);
                var isComponent = symbol is not null && (ProviderHelpers.DerivesFromAny(symbol, "ComponentBase") || ProviderHelpers.ImplementsAny(symbol, "IComponent"));
                if (!isComponent && !typeDeclaration.Identifier.Text.Contains("Component", StringComparison.Ordinal))
                {
                    continue;
                }

                var namespaceName = typeDeclaration.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();
                var serviceId = FrameworkIds.Service("razor-blazor", namespaceName, typeDeclaration.Identifier.Text);
                var service = new ServiceComponent
                {
                    Id = serviceId,
                    Name = typeDeclaration.Identifier.Text,
                    Group = namespaceName,
                    ServiceKind = ServiceKinds.Http,
                    Direction = ServiceDirections.Inbound,
                    Framework = "razor-blazor",
                    Confidence = isComponent ? ConfidenceTiers.Semantic : ConfidenceTiers.Syntactic,
                    Location = CodeLocation.From(ctx.BasePath, tree.FilePath),
                    Evidence = new AnalysisEvidence { Kind = AnalysisEvidenceKind.FrameworkModel, Source = "razor-blazor", Description = "Blazor routable component.", Confidence = isComponent ? ConfidenceTiers.Semantic : ConfidenceTiers.Syntactic }
                };
                results.Services.Add(service);

                foreach (var routeAttribute in routeAttributes)
                {
                    var template = ProviderHelpers.RouteTemplateOf(routeAttribute, model);
                    if (template is null)
                    {
                        continue;
                    }

                    var resolved = RouteTemplateResolver.Resolve(template);
                    var lineSpan = typeDeclaration.GetLocation().GetLineSpan().StartLinePosition;
                    var operationId = FrameworkIds.Operation(serviceId, "ANY", resolved.Path, "component");
                    var endpoint = new ApiEndpoint
                    {
                        Path = resolved.Path,
                        FilePath = CodeLocation.From(ctx.BasePath, tree.FilePath).Path,
                        FileName = Path.GetFileName(tree.FilePath),
                        Namespace = namespaceName,
                        ClassName = typeDeclaration.Identifier.Text,
                        HttpMethod = "ANY",
                        Route = template,
                        EndpointKind = "RazorPage",
                        RoutingKind = "Attribute",
                        Framework = "razor-blazor",
                        ServiceId = serviceId,
                        OperationId = operationId,
                        Confidence = isComponent ? ConfidenceTiers.Semantic : ConfidenceTiers.Syntactic,
                        LineNumber = lineSpan.Line + 1,
                        ColumnNumber = lineSpan.Character + 1,
                        Evidence = new AnalysisEvidence { Kind = AnalysisEvidenceKind.FrameworkModel, Source = "razor-blazor", Description = "Blazor component route.", Confidence = isComponent ? ConfidenceTiers.Semantic : ConfidenceTiers.Syntactic }
                    };
                    results.ApiEndpoints.Add(endpoint);
                    if (resolved.Path is not null)
                    {
                        service.Endpoints.Add(resolved.Path);
                    }

                    results.EntryPoints.Add(new EntryPoint
                    {
                        Id = $"ep:{operationId}",
                        Kind = "BlazorComponent",
                        ClassName = typeDeclaration.Identifier.Text,
                        Namespace = namespaceName,
                        FileName = endpoint.FileName,
                        Path = endpoint.FilePath,
                        LineNumber = endpoint.LineNumber,
                        HttpMethod = "ANY",
                        Route = resolved.Path ?? template
                    });
                }
            }
        }
    }

    /// <summary>A Razor Page's PageModel: its declaration, semantic model, class attributes and On{Verb} handlers.</summary>
    private readonly record struct PageModelInfo(ClassDeclarationSyntax Type, SemanticModel Model, List<AttributeSyntax> Attributes, List<(string Verb, string? HandlerName, MethodDeclarationSyntax? Method)> Handlers);

    /// <summary>
    ///     The PageModel paired with a <c>.cshtml</c> page: the type its <c>@model</c> directive
    ///     names (the code-behind's declaration first when several types share the name), else
    ///     the type deriving from <c>PageModel</c> in the page's <c>X.cshtml.cs</c> code-behind.
    /// </summary>
    private static PageModelInfo? FindPageModel(FrameworkContext ctx, string templateFile, string text)
    {
        if (ctx.CSharp is null)
        {
            return null;
        }

        var modelMatch = ModelDirectiveRegex().Match(text);
        var modelName = modelMatch.Success ? modelMatch.Groups[1].Value : null;
        var modelSimpleName = modelName?.Split('.').Last();
        var codeBehind = Path.GetFullPath(templateFile + ".cs");
        (ClassDeclarationSyntax Type, SyntaxTree Tree)? found = null;
        foreach (var tree in ctx.CSharpTrees)
        {
            var isCodeBehind = !string.IsNullOrEmpty(tree.FilePath) && string.Equals(Path.GetFullPath(tree.FilePath), codeBehind, SafeFileRead.PathComparison);
            if (!isCodeBehind && (modelSimpleName is null || !ctx.TextContainsAny(tree, modelSimpleName)))
            {
                continue;
            }

            foreach (var type in FrameworkContext.Declarations<ClassDeclarationSyntax>(tree))
            {
                var matches = modelSimpleName is not null
                    ? type.Identifier.Text == modelSimpleName && (modelName == modelSimpleName || $"{type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name}.{type.Identifier.Text}".EndsWith(modelName!, StringComparison.Ordinal))
                    : isCodeBehind && (type.BaseList?.Types.Any(baseType => baseType.Type.ToString().EndsWith("PageModel", StringComparison.Ordinal)) ?? false);
                if (matches && (found is null || isCodeBehind))
                {
                    found = (type, tree);
                }
            }
        }

        if (found is not { } pageModel)
        {
            return null;
        }

        var model = ctx.CSharp.GetSemanticModel(pageModel.Tree);
        var handlers = new List<(string, string?, MethodDeclarationSyntax?)>();
        foreach (var method in pageModel.Type.Members.OfType<MethodDeclarationSyntax>())
        {
            var match = HandlerNameRegex().Match(method.Identifier.Text);
            if (!match.Success || !method.Modifiers.Any(SyntaxKind.PublicKeyword))
            {
                continue;
            }

            var verb = match.Groups[1].Value.ToUpperInvariant();
            var handlerName = match.Groups[2].Success && match.Groups[2].Length > 0 ? match.Groups[2].Value : null;
            if (!handlers.Any(existing => existing.Item1 == verb && existing.Item2 == handlerName))
            {
                handlers.Add((verb, handlerName, method));
            }
        }

        return new PageModelInfo(pageModel.Type, model, ProviderHelpers.AttributesOf(pageModel.Type.AttributeLists).ToList(), handlers);
    }

    /// <summary>
    ///     Authorization from the PageModel's attributes and the page's <c>@attribute</c>
    ///     directives (<c>@attribute [Authorize(Roles = "Admin")]</c>): roles, policies and
    ///     <c>[AllowAnonymous]</c> follow ASP.NET Core's rules, anonymous winning.
    /// </summary>
    private static void ApplyPageAuthorization(ApiEndpoint endpoint, List<AttributeSyntax> modelAttributes, string text, bool authorize, bool allowAnonymous)
    {
        ProviderHelpers.ApplyAuthorizationMetadata(endpoint, modelAttributes);
        foreach (Match directive in AuthorizeDirectiveRegex().Matches(text))
        {
            var arguments = directive.Groups[1].Value;
            foreach (Match named in Regex.Matches(arguments, @"\b(Roles|Policy|AuthenticationSchemes)\s*=\s*""([^""]*)"""))
            {
                var values = named.Groups[2].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var target = named.Groups[1].Value switch
                {
                    "Roles" => endpoint.Roles,
                    "Policy" => endpoint.AuthorizationPolicies,
                    _ => endpoint.AuthenticationSchemes
                };
                foreach (var value in values) ProviderHelpers.AddDistinct(target, value);
            }

            if (Regex.Match(arguments, @"^\s*""([^""]*)""") is { Success: true } positional)
            {
                ProviderHelpers.AddDistinct(endpoint.AuthorizationPolicies, positional.Groups[1].Value);
            }
        }

        if (allowAnonymous || endpoint.AllowAnonymous)
        {
            endpoint.AllowAnonymous = true;
            endpoint.AuthorizationRequired = false;
        }
        else if (authorize)
        {
            endpoint.AuthorizationRequired = true;
        }
    }

    /// <summary>
    ///     Taint seeds for a PageModel handler's bound parameters: route values, query and form
    ///     input. Services (<c>[FromServices]</c>) and infrastructure types are not input.
    /// </summary>
    private static void SeedHandlerParameters(FrameworkResults results, MethodDeclarationSyntax method, IMethodSymbol methodSymbol, string? methodId, string path, List<string> routeParameters)
    {
        foreach (var parameter in methodSymbol.Parameters)
        {
            var typeName = parameter.Type.ToDisplayString();
            if (parameter.GetAttributes().Any(attribute => attribute.AttributeClass?.Name is "FromServicesAttribute" or "FromKeyedServicesAttribute" or "BindNeverAttribute") ||
                typeName is "System.Threading.CancellationToken" or "Microsoft.AspNetCore.Http.HttpContext" ||
                typeName.StartsWith("Microsoft.Extensions.Logging.ILogger", StringComparison.Ordinal))
            {
                continue;
            }

            results.TaintSeeds.Add(new FrameworkTaintSeed
            {
                MethodName = methodSymbol.Name,
                ParameterName = parameter.Name,
                ClassName = methodSymbol.ContainingType?.Name,
                Namespace = methodSymbol.ContainingType?.ContainingNamespace?.ToDisplayString(),
                FileName = Path.GetFileName(method.SyntaxTree.FilePath),
                MethodSignature = methodId,
                LineNumber = method.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                BindingSource = routeParameters.Contains(parameter.Name, StringComparer.OrdinalIgnoreCase) ? "http-route" : parameter.Type.IsValueType || parameter.Type.SpecialType == SpecialType.System_String ? "http-query" : "http-form",
                TaintKind = "http",
                FrameworkId = "razor-pages",
                EndpointPath = path,
                Confidence = ConfidenceTiers.Semantic
            });
        }
    }

    /// <summary>
    ///     Route for a Razor Page whose <c>@page</c> directive carries no template: the path of the
    ///     file relative to the nearest <c>Pages</c> directory, minus the extension, with <c>Index</c>
    ///     collapsing to its containing directory.
    /// </summary>
    private static string ConventionalPageRoute(string basePath, string templateFile)
    {
        var relative = Path.GetRelativePath(basePath, templateFile).Replace('\\', '/');
        var segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        var pagesRoot = segments.FindLastIndex(segment => segment.Equals("Pages", StringComparison.OrdinalIgnoreCase));
        if (pagesRoot >= 0)
        {
            segments = segments[(pagesRoot + 1)..];
        }

        if (segments.Count == 0)
        {
            return "/";
        }

        segments[^1] = Path.GetFileNameWithoutExtension(segments[^1]);
        if (segments[^1].Equals("Index", StringComparison.OrdinalIgnoreCase))
        {
            segments.RemoveAt(segments.Count - 1);
        }

        return "/" + string.Join('/', segments);
    }

    private static List<(string Verb, string? HandlerName)> HandlerVerbs(string text)
    {
        var handlers = new List<(string, string?)>();
        foreach (Match match in HandlerRegex().Matches(text))
        {
            var verb = match.Groups[1].Value.ToUpperInvariant();
            // Group 2 is the handler name; group 3 absorbs a trailing Async so it is not part of it.
            var handlerName = match.Groups[2].Success && match.Groups[2].Length > 0 ? match.Groups[2].Value : null;
            if (!handlers.Contains((verb, handlerName)))
            {
                handlers.Add((verb, handlerName));
            }
        }

        return handlers;
    }

    private static string? InteractiveRenderMode(string text)
    {
        var match = RenderModeRegex().Match(text);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static int LineOf(string text, string marker)
    {
        var index = text.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
        {
            return 1;
        }

        return 1 + text.AsSpan(0, index).ToString().Count('\n');
    }

    /// <summary>
    ///     Matches an <c>@page</c> directive with or without a route template, and allows leading
    ///     whitespace. Requiring a quoted template made every Razor Page that relies on the
    ///     conventional <c>Pages/</c>-relative route, which is most of them, invisible.
    /// </summary>
    [GeneratedRegex(@"^\s*@page(?:\s+""([^""]*)"")?", RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex PageDirectiveRegex();

    /// <summary>
    ///     Matches a page handler method declaration. The trailing <c>\(</c> is required so that
    ///     <c>@onclick="OnGetData"</c> and prose mentions do not mint endpoints, and the handler-name
    ///     group is lazy so that <c>OnGetCustomerAsync</c> yields the handler <c>Customer</c> rather
    ///     than <c>CustomerAsync</c>, ASP.NET dispatches <c>?handler=Customer</c>.
    /// </summary>
    [GeneratedRegex(@"\bOn(Get|Post|Put|Delete|Patch|Head|Options)([A-Z]\w*?)?(Async)?\s*\(", RegexOptions.Compiled)]
    private static partial Regex HandlerRegex();

    [GeneratedRegex(@"@rendermode\s+([\w\.]+)", RegexOptions.Compiled)]
    private static partial Regex RenderModeRegex();

    /// <summary>The <c>@model</c> directive's type name.</summary>
    [GeneratedRegex(@"^\s*@model\s+([\w\.]+)", RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex ModelDirectiveRegex();

    /// <summary>A page handler method's name: <c>On{Verb}{Handler}[Async]</c>, the handler name without the Async suffix.</summary>
    [GeneratedRegex(@"^On(Get|Post|Put|Delete|Patch|Head|Options)([A-Z]\w*?)?(Async)?$", RegexOptions.Compiled)]
    private static partial Regex HandlerNameRegex();

    /// <summary>An <c>@attribute [Authorize(...)]</c> directive's argument text.</summary>
    [GeneratedRegex(@"@attribute\s*\[\s*(?:Microsoft\.AspNetCore\.Authorization\.)?Authorize(?:Attribute)?\s*(?:\(([^\)]*)\))?", RegexOptions.Compiled)]
    private static partial Regex AuthorizeDirectiveRegex();
}
