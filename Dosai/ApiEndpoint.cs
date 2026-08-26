using System.Text.RegularExpressions;
using Depscan.Frameworks;
using Microsoft.CodeAnalysis.VisualBasic;
using VbAttributeSyntax = Microsoft.CodeAnalysis.VisualBasic.Syntax.AttributeSyntax;
using VbMethodBlockSyntax = Microsoft.CodeAnalysis.VisualBasic.Syntax.MethodBlockSyntax;

namespace Depscan;

public sealed class ApiEndpoint
{
    /// <summary>
    ///     Resolved, normalized route path starting with "/" (tokens substituted, constraints
    ///     stripped). Null when the template could not be resolved. Since schema 4.0.0 this is the
    ///     path consumers should use for CycloneDX endpoints; <see cref="Route" /> keeps the
    ///     verbatim template and <see cref="FilePath" /> carries the source file location.
    /// </summary>
    public string? Path { get; set; }

    /// <summary>Relative path of the source file declaring the endpoint.</summary>
    public string? FilePath { get; set; }

    public string? FileName { get; set; }
    public string? Namespace { get; set; }
    public string? ClassName { get; set; }
    public string? MethodName { get; set; }
    public string? HttpMethod { get; set; }

    /// <summary>Verbatim (token-preserving) route template as declared, for humans and diffing.</summary>
    public string? Route { get; set; }

    public string? EndpointKind { get; set; }
    public bool? AuthorizationRequired { get; set; }
    public List<string> AuthorizationPolicies { get; set; } = [];
    public List<string> Roles { get; set; } = [];
    public bool AllowAnonymous { get; set; }
    public List<string> AuthenticationSchemes { get; set; } = [];
    public List<string> RequiredClaims { get; set; } = [];
    public List<string> RequiredScopes { get; set; } = [];
    public List<string> CorsPolicies { get; set; } = [];
    public bool? AntiForgeryRequired { get; set; }
    public int LineNumber { get; set; }
    public int ColumnNumber { get; set; }

    /// <summary>
    ///     Absolute URLs found anywhere in the declaring file, de-duplicated and sorted. Heuristic
    ///     file-scope evidence (low confidence), NOT this endpoint's own URLs. Renamed from Urls in
    ///     schema 4.0.0 because the old name read as "this endpoint's URLs".
    /// </summary>
    public List<string> RawUrls { get; set; } = [];

    /// <summary>Route parameters parsed from the template with constraints, defaults, and optionality.</summary>
    public List<RouteParameter> RouteParameters { get; set; } = [];

    /// <summary>"high", "medium", or "low". Syntax-only analysis is capped at medium.</summary>
    public string Confidence { get; set; } = "medium";

    public string? ServiceId { get; set; }

    public string? OperationId { get; set; }

    /// <summary>Id of the framework provider that produced this endpoint, e.g. "aspnetcore-mvc".</summary>
    public string? Framework { get; set; }

    public List<string> ContentTypes { get; set; } = [];

    public string? ApiVersion { get; set; }

    /// <summary>"Attribute", "MinimalApi", "Conventional", or "Mount".</summary>
    public string? RoutingKind { get; set; }

    public AnalysisEvidence? Evidence { get; set; }
}

/// <summary>
///     Best-effort VB.NET endpoint extraction. C# endpoint detection moved to the framework
///     provider model (see Dosai/Frameworks/Providers); VB remains syntax-only because the
///     provider layer is C#-compilation-driven. Documented as best-effort parity in
///     docs/frameworks.md.
/// </summary>
public static partial class ApiEndpointAnalyzer
{
    public static List<ApiEndpoint> GetApiEndpoints(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return [];
        }

        var endpoints = new List<ApiEndpoint>();
        foreach (var sourceFile in GetVisualBasicFiles(path))
        {
            AnalyzeVisualBasic(path, sourceFile, File.ReadAllText(sourceFile), endpoints);
        }

        return endpoints
            .OrderBy(endpoint => endpoint.FileName, StringComparer.Ordinal)
            .ThenBy(endpoint => endpoint.LineNumber)
            .ThenBy(endpoint => endpoint.ColumnNumber)
            .ThenBy(endpoint => endpoint.Route, StringComparer.Ordinal)
            .ToList();
    }

    private static void AnalyzeVisualBasic(string basePath, string sourceFile, string text, List<ApiEndpoint> endpoints)
    {
        var tree = (VisualBasicSyntaxTree)VisualBasicSyntaxTree.ParseText(text, path: sourceFile);
        var root = tree.GetCompilationUnitRoot();
        var urls = ProviderHelpers.ExtractRawUrls(text);

        foreach (var methodBlock in root.DescendantNodes().OfType<VbMethodBlockSyntax>())
        {
            var statement = methodBlock.SubOrFunctionStatement;
            var attributes = statement.AttributeLists.SelectMany(list => list.Attributes).ToList();
            foreach (var attribute in attributes)
            {
                var name = attribute.Name.ToString();
                var httpMethod = AttributeHttpMethod(name);
                var route = GetVbAttributeLiteral(attribute);
                if (httpMethod is null && !name.EndsWith("Route", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var className = methodBlock.Ancestors().OfType<Microsoft.CodeAnalysis.VisualBasic.Syntax.TypeBlockSyntax>().FirstOrDefault()?.BlockStatement.Identifier.Text;
                var namespaceName = methodBlock.Ancestors().OfType<Microsoft.CodeAnalysis.VisualBasic.Syntax.NamespaceStatementSyntax>().FirstOrDefault()?.Name.ToString();
                var location = statement.GetLocation().GetLineSpan().StartLinePosition;
                var endpoint = new ApiEndpoint
                {
                    FilePath = Path.GetRelativePath(basePath, sourceFile),
                    FileName = Path.GetFileName(sourceFile),
                    Namespace = namespaceName,
                    ClassName = className,
                    MethodName = statement.Identifier.Text,
                    HttpMethod = httpMethod ?? "ANY",
                    Route = route,
                    EndpointKind = "Attribute",
                    RoutingKind = "Attribute",
                    LineNumber = location.Line + 1,
                    ColumnNumber = location.Character + 1,
                    RawUrls = urls
                };
                var tokens = new RouteTokenValues
                {
                    Controller = className is null ? null : RouteTemplateResolver.ControllerName(className),
                    Action = statement.Identifier.Text
                };
                ApplyResolvedRoute(endpoint, tokens);
                ApplyVbAuthorizationMetadata(endpoint, attributes);
                endpoints.Add(endpoint);
            }
        }
    }

    /// <summary>
    ///     Resolves the endpoint's combined template into <see cref="ApiEndpoint.Path" /> with token
    ///     substitution and parameter normalization. Syntax-only analysis is capped at medium
    ///     confidence; an unresolvable template leaves Path null with low confidence.
    /// </summary>
    private static void ApplyResolvedRoute(ApiEndpoint endpoint, RouteTokenValues? tokens)
    {
        var resolved = RouteTemplateResolver.Resolve(endpoint.Route, tokens);
        endpoint.Path = resolved.Path;
        endpoint.RouteParameters = resolved.Parameters
            .Select(parameter => new RouteParameter
            {
                Name = parameter.Name,
                Constraints = parameter.Constraints,
                Optional = parameter.Optional,
                DefaultValue = parameter.DefaultValue,
                CatchAll = parameter.CatchAll,
                BindingSource = "Route"
            })
            .ToList();
        endpoint.Confidence = resolved.Path is null ? "low" : "medium";
    }

    private static void ApplyVbAuthorizationMetadata(ApiEndpoint endpoint, IEnumerable<VbAttributeSyntax> attributes)
    {
        var text = string.Join(" ", attributes.Select(attribute => attribute.ToString()));
        if (text.Contains("AllowAnonymous", StringComparison.OrdinalIgnoreCase))
        {
            endpoint.AllowAnonymous = true;
            endpoint.AuthorizationRequired = false;
        }
        else if (text.Contains("Authorize", StringComparison.OrdinalIgnoreCase))
        {
            endpoint.AuthorizationRequired = true;
        }
    }

    private static string? GetVbAttributeLiteral(VbAttributeSyntax attribute)
    {
        var expression = attribute.ArgumentList?.Arguments.FirstOrDefault()?.GetExpression();
        return expression?.ToString().Trim('"');
    }

    private static string? AttributeHttpMethod(string attributeName)
    {
        attributeName = attributeName.Split('.').Last().Replace("Attribute", string.Empty, StringComparison.OrdinalIgnoreCase);
        return attributeName.ToLowerInvariant() switch
        {
            "httpget" => "GET",
            "httppost" => "POST",
            "httpput" => "PUT",
            "httpdelete" => "DELETE",
            "httppatch" => "PATCH",
            "httphead" => "HEAD",
            "httpoptions" => "OPTIONS",
            _ => null
        };
    }

    private static List<string> GetVisualBasicFiles(string path)
    {
        if (File.Exists(path))
        {
            return Path.GetExtension(path).Equals(Constants.VBSourceExtension, StringComparison.OrdinalIgnoreCase) ? [path] : [];
        }

        if (!Directory.Exists(path))
        {
            return [];
        }

        return new DirectoryInfo(path).EnumerateFiles("*.*", SearchOption.AllDirectories)
            .Where(file => file.Extension.Equals(Constants.VBSourceExtension, StringComparison.OrdinalIgnoreCase))
            .Where(file => !file.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(file => !file.FullName.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(file => !file.Name.EndsWith(".g.vb", StringComparison.OrdinalIgnoreCase))
            .Select(file => file.FullName)
            .ToList();
    }
}
