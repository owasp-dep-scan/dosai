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
            endpoints.Add(new ApiEndpoint
            {
                Path = Path.GetRelativePath(basePath, sourceFile),
                FileName = Path.GetFileName(sourceFile),
                HttpMethod = httpMethod,
                Route = route,
                EndpointKind = "MinimalApi",
                LineNumber = location.Line + 1,
                ColumnNumber = location.Character + 1,
                Urls = urls
            });
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
                endpoints.Add(new ApiEndpoint
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
                });
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
}
