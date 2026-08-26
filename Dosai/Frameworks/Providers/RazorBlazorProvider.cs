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

            var pageMatch = PageDirectiveRegex().Match(text);
            var hasPageDirective = pageMatch.Success;
            var pageRoute = hasPageDirective && pageMatch.Groups[1].Success && pageMatch.Groups[1].Length > 0
                ? pageMatch.Groups[1].Value
                : null;
            var isBlazor = templateFile.EndsWith(".razor", StringComparison.OrdinalIgnoreCase) || text.Contains("@rendermode", StringComparison.Ordinal) || text.Contains("<PageTitle>", StringComparison.Ordinal);
            // Only @page-declared files are routable: _Imports.razor, App.razor, layouts, and
            // partials are reachable templates/components but never URL endpoints.
            if (!hasPageDirective)
            {
                continue;
            }

            // A bare `@page` routes by file location relative to the Pages/ root, with Index mapping
            // to the directory itself. Derived, so confidence stays heuristic.
            pageRoute ??= hasPageDirective ? ConventionalPageRoute(ctx.BasePath, templateFile) : null;

            var fileName = Path.GetFileName(templateFile);
            var rawUrls = ProviderHelpers.ExtractRawUrls(text);
            var authorize = text.Contains("[Authorize]", StringComparison.Ordinal) || Regex.IsMatch(text, @"@attribute\s*\[Authorize");
            var allowAnonymous = text.Contains("[AllowAnonymous]", StringComparison.Ordinal) || Regex.IsMatch(text, @"@attribute\s*\[AllowAnonymous");
            var renderMode = InteractiveRenderMode(text);
            var pagePath = pageRoute is null ? null : RouteTemplateResolver.Resolve(pageRoute).Path;
            var lineNumber = LineOf(text, pageRoute is null ? "@page" : pageRoute);

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
                AllowAnonymous = allowAnonymous ? true : null,
                Authenticated = allowAnonymous ? false : authorize ? true : null,
                TrustZone = allowAnonymous ? TrustZones.Public : authorize ? TrustZones.Authenticated : TrustZones.Unknown,
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
            if (pagePath is not null && !service.Endpoints.Contains(pagePath, StringComparer.Ordinal))
            {
                service.Endpoints.Add(pagePath);
            }

            // Blazor component routes are reachable via any verb on first navigation; Razor Pages
            // expose one endpoint per On{Verb} handler.
            var handlers = HandlerVerbs(text);
            if (handlers.Count == 0)
            {
                handlers.Add(("ANY", null));
            }

            foreach (var (verb, handlerName) in handlers)
            {
                var path = pagePath ?? "/" + Path.GetFileNameWithoutExtension(templateFile);
                if (handlerName is not null && pageRoute is not null)
                {
                    // Named handlers are reached with ?handler=name; reflect that in the emitted path.
                    path = $"{path}?handler={handlerName}";
                }

                var operationId = FrameworkIds.Operation(serviceId, verb, path, handlerName ?? "page");
                var endpoint = new ApiEndpoint
                {
                    Path = path,
                    FilePath = CodeLocation.From(ctx.BasePath, templateFile).Path,
                    FileName = fileName,
                    HttpMethod = verb,
                    Route = pageRoute,
                    EndpointKind = "RazorPage",
                    RoutingKind = isBlazor ? "Attribute" : "Template",
                    Framework = "razor-blazor",
                    ServiceId = serviceId,
                    OperationId = operationId,
                    Confidence = ConfidenceTiers.Heuristic,
                    LineNumber = lineNumber,
                    RawUrls = rawUrls,
                    AuthorizationRequired = allowAnonymous ? false : authorize ? true : null,
                    AllowAnonymous = allowAnonymous,
                    Evidence = new AnalysisEvidence { Kind = AnalysisEvidenceKind.FrameworkModel, Source = "razor-blazor", Description = isBlazor ? "Blazor component route." : "Razor Page handler.", Confidence = ConfidenceTiers.Heuristic, FileName = fileName, LineNumber = lineNumber }
                };
                results.ApiEndpoints.Add(endpoint);
                service.Operations.Add(new ServiceOperation
                {
                    Id = operationId,
                    Name = handlerName ?? "page",
                    HttpMethod = verb,
                    Path = path,
                    RouteTemplate = pageRoute,
                    Authenticated = endpoint.AuthorizationRequired,
                    Confidence = ConfidenceTiers.Heuristic,
                    Location = CodeLocation.From(ctx.BasePath, templateFile, lineNumber)
                });
                results.EntryPoints.Add(new EntryPoint
                {
                    Id = $"ep:{operationId}",
                    Kind = "HttpRazorPage",
                    FileName = fileName,
                    Path = endpoint.FilePath,
                    LineNumber = lineNumber,
                    HttpMethod = verb,
                    Route = path,
                    AuthorizationRequired = endpoint.AuthorizationRequired,
                    AllowAnonymous = endpoint.AllowAnonymous
                });
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
    ///     conventional <c>Pages/</c>-relative route — which is most of them — invisible.
    /// </summary>
    [GeneratedRegex(@"^\s*@page(?:\s+""([^""]*)"")?", RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex PageDirectiveRegex();

    /// <summary>
    ///     Matches a page handler method declaration. The trailing <c>\(</c> is required so that
    ///     <c>@onclick="OnGetData"</c> and prose mentions do not mint endpoints, and the handler-name
    ///     group is lazy so that <c>OnGetCustomerAsync</c> yields the handler <c>Customer</c> rather
    ///     than <c>CustomerAsync</c> — ASP.NET dispatches <c>?handler=Customer</c>.
    /// </summary>
    [GeneratedRegex(@"\bOn(Get|Post|Put|Delete|Patch|Head|Options)([A-Z]\w*?)?(Async)?\s*\(", RegexOptions.Compiled)]
    private static partial Regex HandlerRegex();

    [GeneratedRegex(@"@rendermode\s+([\w\.]+)", RegexOptions.Compiled)]
    private static partial Regex RenderModeRegex();
}
