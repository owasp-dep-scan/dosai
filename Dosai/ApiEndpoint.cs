using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.VisualBasic;
using VbAttributeSyntax = Microsoft.CodeAnalysis.VisualBasic.Syntax.AttributeSyntax;
using VbMethodBlockSyntax = Microsoft.CodeAnalysis.VisualBasic.Syntax.MethodBlockSyntax;

namespace Depscan;

public sealed class ApiEndpoint
{
    public string? Path { get; set; }
    public string? FileName { get; set; }
    public string? Namespace { get; set; }
    public string? ClassName { get; set; }
    public string? MethodName { get; set; }
    public string? HttpMethod { get; set; }
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
    public List<string> Urls { get; set; } = [];
}

public static partial class ApiEndpointAnalyzer
{
    public static List<ApiEndpoint> GetApiEndpoints(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return [];
        }

        var endpoints = new List<ApiEndpoint>();
        foreach (var sourceFile in GetSourceFiles(path))
        {
            var extension = Path.GetExtension(sourceFile);
            var text = File.ReadAllText(sourceFile);
            if (extension.Equals(Constants.CSharpSourceExtension, StringComparison.OrdinalIgnoreCase))
            {
                AnalyzeCSharp(path, sourceFile, text, endpoints);
            }
            else if (extension.Equals(Constants.VBSourceExtension, StringComparison.OrdinalIgnoreCase))
            {
                AnalyzeVisualBasic(path, sourceFile, text, endpoints);
            }
        }

        return endpoints
            .OrderBy(endpoint => endpoint.FileName, StringComparer.Ordinal)
            .ThenBy(endpoint => endpoint.LineNumber)
            .ThenBy(endpoint => endpoint.ColumnNumber)
            .ThenBy(endpoint => endpoint.Route, StringComparer.Ordinal)
            .ToList();
    }

    private static void AnalyzeCSharp(string basePath, string sourceFile, string text, List<ApiEndpoint> endpoints)
    {
        var tree = (CSharpSyntaxTree)CSharpSyntaxTree.ParseText(text, path: sourceFile);
        var root = tree.GetCompilationUnitRoot();
        var urls = AbsoluteUrlRegex().Matches(text).Select(match => match.Value).Distinct(StringComparer.Ordinal).ToList();

        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            var attributes = method.AttributeLists.SelectMany(list => list.Attributes).ToList();
            var classAttributes = method.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.AttributeLists.SelectMany(list => list.Attributes).ToList() ?? [];
            var endpointAttributes = attributes
                .Select(attribute => CreateEndpointFromAttribute(basePath, sourceFile, method, attribute))
                .Where(endpoint => endpoint is not null)
                .Cast<ApiEndpoint>()
                .ToList();

            if (endpointAttributes.Count == 0)
            {
                continue;
            }

            var classRoute = method.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.AttributeLists
                .SelectMany(list => list.Attributes)
                .Select(GetRouteTemplate)
                .FirstOrDefault(route => !string.IsNullOrWhiteSpace(route));
            foreach (var endpoint in endpointAttributes)
            {
                endpoint.Namespace = method.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();
                endpoint.ClassName = method.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text;
                endpoint.MethodName = method.Identifier.Text;
                endpoint.Route = CombineRoutes(classRoute, endpoint.Route);
                endpoint.Urls = urls;
                ApplyAuthorizationMetadata(endpoint, classAttributes.Concat(attributes));
                endpoints.Add(endpoint);
            }
        }

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var expression = invocation.Expression.ToString();
            var httpMethod = MinimalApiHttpMethod(expression);
            if (httpMethod is null)
            {
                continue;
            }

            var firstArgument = invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression;
            var route = firstArgument is LiteralExpressionSyntax literal ? literal.Token.ValueText : firstArgument?.ToString().Trim('"');
            var location = invocation.GetLocation().GetLineSpan().StartLinePosition;
            var endpoint = new ApiEndpoint
            {
                Path = Path.GetRelativePath(basePath, sourceFile),
                FileName = Path.GetFileName(sourceFile),
                HttpMethod = httpMethod,
                Route = route,
                EndpointKind = "MinimalApi",
                LineNumber = location.Line + 1,
                ColumnNumber = location.Character + 1,
                Urls = urls
            };
            ApplyMinimalApiAuthorizationMetadata(endpoint, invocation);
            endpoints.Add(endpoint);
        }
    }

    private static void AnalyzeVisualBasic(string basePath, string sourceFile, string text, List<ApiEndpoint> endpoints)
    {
        var tree = (VisualBasicSyntaxTree)VisualBasicSyntaxTree.ParseText(text, path: sourceFile);
        var root = tree.GetCompilationUnitRoot();
        var urls = AbsoluteUrlRegex().Matches(text).Select(match => match.Value).Distinct(StringComparer.Ordinal).ToList();

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

                var location = statement.GetLocation().GetLineSpan().StartLinePosition;
                var endpoint = new ApiEndpoint
                {
                    Path = Path.GetRelativePath(basePath, sourceFile),
                    FileName = Path.GetFileName(sourceFile),
                    MethodName = statement.Identifier.Text,
                    HttpMethod = httpMethod ?? "ANY",
                    Route = route,
                    EndpointKind = "Attribute",
                    LineNumber = location.Line + 1,
                    ColumnNumber = location.Character + 1,
                    Urls = urls
                };
                ApplyVbAuthorizationMetadata(endpoint, attributes);
                endpoints.Add(endpoint);
            }
        }
    }

    private static ApiEndpoint? CreateEndpointFromAttribute(string basePath, string sourceFile, MethodDeclarationSyntax method, AttributeSyntax attribute)
    {
        var name = attribute.Name.ToString();
        var httpMethod = AttributeHttpMethod(name);
        var route = GetRouteTemplate(attribute);
        if (httpMethod is null && !name.EndsWith("Route", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var location = method.GetLocation().GetLineSpan().StartLinePosition;
        return new ApiEndpoint
        {
            Path = Path.GetRelativePath(basePath, sourceFile),
            FileName = Path.GetFileName(sourceFile),
            HttpMethod = httpMethod ?? "ANY",
            Route = route,
            EndpointKind = "Attribute",
            LineNumber = location.Line + 1,
            ColumnNumber = location.Character + 1
        };
    }

    private static string? GetRouteTemplate(AttributeSyntax attribute)
    {
        var firstArgument = attribute.ArgumentList?.Arguments.FirstOrDefault()?.Expression;
        return firstArgument switch
        {
            LiteralExpressionSyntax literal => literal.Token.ValueText,
            null => null,
            _ => firstArgument.ToString().Trim('"')
        };
    }

    private static void ApplyAuthorizationMetadata(ApiEndpoint endpoint, IEnumerable<AttributeSyntax> attributes)
    {
        var attributeList = attributes.ToList();
        endpoint.AllowAnonymous = attributeList.Any(attribute => AttributeName(attribute).Contains("AllowAnonymous", StringComparison.OrdinalIgnoreCase));
        var authorizeAttributes = attributeList.Where(attribute => AttributeName(attribute).Contains("Authorize", StringComparison.OrdinalIgnoreCase)).ToList();
        if (endpoint.AllowAnonymous)
        {
            endpoint.AuthorizationRequired = false;
        }
        else if (authorizeAttributes.Count > 0)
        {
            endpoint.AuthorizationRequired = true;
        }

        foreach (var attribute in authorizeAttributes)
        {
            AddAttributeValues(attribute, endpoint.AuthorizationPolicies, "Policy");
            AddAttributeValues(attribute, endpoint.Roles, "Roles");
            AddAttributeValues(attribute, endpoint.AuthenticationSchemes, "AuthenticationSchemes");
            var firstLiteral = GetRouteTemplate(attribute);
            if (!string.IsNullOrWhiteSpace(firstLiteral))
            {
                AddDistinct(endpoint.AuthorizationPolicies, firstLiteral);
            }
        }

        foreach (var attribute in attributeList.Where(attribute => AttributeName(attribute).Contains("RequiredScope", StringComparison.OrdinalIgnoreCase)))
        {
            AddAttributeValues(attribute, endpoint.RequiredScopes, "RequiredScopes", "Scope", "Scopes");
            var firstLiteral = GetRouteTemplate(attribute);
            if (!string.IsNullOrWhiteSpace(firstLiteral)) AddDistinct(endpoint.RequiredScopes, firstLiteral);
        }

        foreach (var attribute in attributeList.Where(attribute => AttributeName(attribute).Contains("RequireClaim", StringComparison.OrdinalIgnoreCase)))
        {
            var firstLiteral = GetRouteTemplate(attribute);
            if (!string.IsNullOrWhiteSpace(firstLiteral)) AddDistinct(endpoint.RequiredClaims, firstLiteral);
        }

        foreach (var attribute in attributeList.Where(attribute => AttributeName(attribute).Contains("EnableCors", StringComparison.OrdinalIgnoreCase)))
        {
            var firstLiteral = GetRouteTemplate(attribute);
            if (!string.IsNullOrWhiteSpace(firstLiteral)) AddDistinct(endpoint.CorsPolicies, firstLiteral);
        }

        if (attributeList.Any(attribute => AttributeName(attribute).Contains("ValidateAntiForgeryToken", StringComparison.OrdinalIgnoreCase) || AttributeName(attribute).Contains("AutoValidateAntiforgeryToken", StringComparison.OrdinalIgnoreCase)))
        {
            endpoint.AntiForgeryRequired = true;
        }
        if (attributeList.Any(attribute => AttributeName(attribute).Contains("IgnoreAntiforgeryToken", StringComparison.OrdinalIgnoreCase)))
        {
            endpoint.AntiForgeryRequired = false;
        }
    }

    private static void ApplyMinimalApiAuthorizationMetadata(ApiEndpoint endpoint, InvocationExpressionSyntax mapInvocation)
    {
        var chainText = mapInvocation.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().LastOrDefault()?.ToString() ?? mapInvocation.ToString();
        if (chainText.Contains("AllowAnonymous", StringComparison.OrdinalIgnoreCase))
        {
            endpoint.AllowAnonymous = true;
            endpoint.AuthorizationRequired = false;
        }
        else if (chainText.Contains("RequireAuthorization", StringComparison.OrdinalIgnoreCase))
        {
            endpoint.AuthorizationRequired = true;
            foreach (Match match in QuotedStringRegex().Matches(chainText))
            {
                var value = match.Groups[1].Value;
                if (!string.Equals(value, endpoint.Route, StringComparison.Ordinal)) AddDistinct(endpoint.AuthorizationPolicies, value);
            }
        }

        foreach (Match match in Regex.Matches(chainText, @"RequireCors\s*\(\s*""([^""]+)""", RegexOptions.IgnoreCase))
        {
            AddDistinct(endpoint.CorsPolicies, match.Groups[1].Value);
        }
        if (chainText.Contains("DisableAntiforgery", StringComparison.OrdinalIgnoreCase)) endpoint.AntiForgeryRequired = false;
        if (chainText.Contains("RequireAntiforgery", StringComparison.OrdinalIgnoreCase)) endpoint.AntiForgeryRequired = true;
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

    private static string AttributeName(AttributeSyntax attribute) => attribute.Name.ToString();

    private static void AddAttributeValues(AttributeSyntax attribute, List<string> target, params string[] names)
    {
        foreach (var argument in attribute.ArgumentList?.Arguments ?? [])
        {
            var name = argument.NameEquals?.Name.Identifier.Text ?? argument.NameColon?.Name.Identifier.Text;
            if (name is null || !names.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            AddCommaSeparated(target, argument.Expression.ToString().Trim('"'));
        }
    }

    private static void AddCommaSeparated(List<string> target, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        foreach (var item in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) AddDistinct(target, item);
    }

    private static void AddDistinct(List<string> target, string value)
    {
        if (!string.IsNullOrWhiteSpace(value) && !target.Contains(value, StringComparer.Ordinal)) target.Add(value);
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

    private static string? MinimalApiHttpMethod(string expression) => expression.Split('.').Last() switch
    {
        "MapGet" => "GET",
        "MapPost" => "POST",
        "MapPut" => "PUT",
        "MapDelete" => "DELETE",
        "MapPatch" => "PATCH",
        "MapMethods" => "ANY",
        _ => null
    };

    private static string? CombineRoutes(string? classRoute, string? methodRoute)
    {
        if (string.IsNullOrWhiteSpace(classRoute)) return methodRoute;
        if (string.IsNullOrWhiteSpace(methodRoute)) return classRoute;
        return $"{classRoute.TrimEnd('/')}/{methodRoute.TrimStart('/')}";
    }

    private static List<string> GetSourceFiles(string path)
    {
        if (File.Exists(path))
        {
            var extension = Path.GetExtension(path);
            return extension.Equals(Constants.CSharpSourceExtension, StringComparison.OrdinalIgnoreCase) || extension.Equals(Constants.VBSourceExtension, StringComparison.OrdinalIgnoreCase)
                ? [path]
                : [];
        }

        return new DirectoryInfo(path).EnumerateFiles("*.*", SearchOption.AllDirectories)
            .Where(file => file.Extension.Equals(Constants.CSharpSourceExtension, StringComparison.OrdinalIgnoreCase) || file.Extension.Equals(Constants.VBSourceExtension, StringComparison.OrdinalIgnoreCase))
            .Where(file => !file.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(file => !file.FullName.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(file => !file.Name.EndsWith($".g{file.Extension}", StringComparison.OrdinalIgnoreCase))
            .Select(file => file.FullName)
            .ToList();
    }

    [GeneratedRegex(@"https?://[^\s\""'<>]+", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex AbsoluteUrlRegex();

    [GeneratedRegex("\\\"([^\\\"]+)\\\"", RegexOptions.Compiled)]
    private static partial Regex QuotedStringRegex();
}
