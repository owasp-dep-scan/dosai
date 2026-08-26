using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Depscan.Frameworks;

/// <summary>Shared syntax/semantic helpers used by several framework providers.</summary>
internal static partial class ProviderHelpers
{
    /// <summary>Strips namespace qualifiers and the Attribute suffix: "Microsoft.AspNetCore.Mvc.HttpGetAttribute" -> "HttpGet".</summary>
    internal static string NormalizeAttributeName(string attributeName) => attributeName.Split('.').Last().Replace("Attribute", string.Empty, StringComparison.OrdinalIgnoreCase);

    internal static string NormalizeAttributeName(AttributeSyntax attribute) => NormalizeAttributeName(attribute.Name.ToString());

    internal static bool IsNamed(AttributeSyntax attribute, string name) => NormalizeAttributeName(attribute).Equals(name, StringComparison.OrdinalIgnoreCase);

    internal static IEnumerable<AttributeSyntax> AttributesOf(SyntaxList<AttributeListSyntax> lists) => lists.SelectMany(list => list.Attributes);

    /// <summary>
    ///     Extracts a template value from a <em>positional</em> attribute argument: string literals
    ///     first, then nameof/const expressions resolved through the semantic model. Returns null when
    ///     the value cannot be resolved to text; callers then keep the verbatim template and a
    ///     low-confidence path rather than emitting a garbled ToString().
    /// </summary>
    /// <remarks>
    ///     Named arguments are excluded before indexing, which is the whole point of this method.
    ///     Indexing the raw argument list first meant a named argument in leading position was read as
    ///     if it were the positional one: <c>[HttpGet(Name = "GetWeatherForecast")]</c> — the shape the
    ///     stock <c>dotnet new webapi</c> template emits — produced the route
    ///     <c>/api/weather/GetWeatherForecast</c>, and <c>[McpServerTool(Destructive = false)]</c> named
    ///     the tool <c>"false"</c>. <c>Name</c> is a route/tool name, never a template.
    /// </remarks>
    internal static string? AttributeArgumentText(AttributeSyntax attribute, SemanticModel? model, int argumentIndex = 0)
    {
        var argument = attribute.ArgumentList?.Arguments
            .Where(a => a.NameEquals is null)
            .ElementAtOrDefault(argumentIndex);
        if (argument is null)
        {
            return null;
        }

        var expression = argument.Expression;
        if (expression is LiteralExpressionSyntax literal)
        {
            return literal.Token.ValueText;
        }

        if (model is not null)
        {
            var constant = model.GetConstantValue(expression);
            if (constant.HasValue && constant.Value is string text)
            {
                return text;
            }
        }

        // Interpolated or concatenated literals: best-effort join of the literal parts.
        if (expression is InterpolatedStringExpressionSyntax interpolated)
        {
            var parts = interpolated.Contents.Select(content => content switch
            {
                InterpolatedStringTextSyntax text => text.TextToken.ValueText,
                InterpolationSyntax interpolation => interpolation.Expression.ToString(),
                _ => string.Empty
            });
            return string.Concat(parts);
        }

        return null;
    }

    /// <summary>Collects every string argument of an attribute, e.g. [AcceptVerbs("GET", "POST")] or [Authorize(Roles = "a,b")].</summary>
    internal static List<string> AttributeStringArguments(AttributeSyntax attribute, string? namedArgument = null)
    {
        var values = new List<string>();
        foreach (var argument in attribute.ArgumentList?.Arguments ?? [])
        {
            if (namedArgument is not null && !string.Equals(argument.NameEquals?.Name.ToString(), namedArgument, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            switch (argument.Expression)
            {
                case LiteralExpressionSyntax literal when literal.Token.Value is string text:
                    values.Add(text);
                    break;
                case ImplicitArrayCreationExpressionSyntax array:
                    values.AddRange((array.Initializer?.Expressions.OfType<LiteralExpressionSyntax>() ?? Enumerable.Empty<LiteralExpressionSyntax>()).Where(e => e.Token.Value is string).Select(e => (string)e.Token.Value!));
                    break;
            }
        }

        return values;
    }

    /// <summary>The (first) template argument of a routing attribute, or null when absent.</summary>
    internal static string? RouteTemplateOf(AttributeSyntax attribute, SemanticModel? model) => AttributeArgumentText(attribute, model);

    /// <summary>True when the symbol's base-type chain contains a type with one of the given names (case-sensitive CLR names).</summary>
    internal static bool DerivesFromAny(INamedTypeSymbol? symbol, params string[] baseNames)
    {
        var current = symbol?.BaseType;
        var depth = 0;
        while (current is not null && depth++ < 12)
        {
            if (baseNames.Contains(current.Name, StringComparer.Ordinal))
            {
                return true;
            }

            current = current.BaseType;
        }

        return false;
    }

    /// <summary>True when the type declares or implements an interface whose name matches (walks AllInterfaces so stub interfaces resolve).</summary>
    internal static bool ImplementsAny(INamedTypeSymbol? symbol, params string[] interfaceNames)
    {
        if (symbol is null)
        {
            return false;
        }

        return symbol.AllInterfaces.Any(face => interfaceNames.Contains(face.Name, StringComparer.Ordinal));
    }

    /// <summary>
    ///     Merges class- and method-level authorization attributes onto the endpoint. Ported from
    ///     the original ApiEndpointAnalyzer behavior so output stays compatible.
    /// </summary>
    internal static void ApplyAuthorizationMetadata(ApiEndpoint endpoint, IEnumerable<AttributeSyntax> attributes)
    {
        var attributeList = attributes.ToList();
        endpoint.AllowAnonymous = attributeList.Any(attribute => NormalizeAttributeName(attribute).Contains("AllowAnonymous", StringComparison.OrdinalIgnoreCase));
        var authorizeAttributes = attributeList.Where(attribute => NormalizeAttributeName(attribute).Contains("Authorize", StringComparison.OrdinalIgnoreCase) && !NormalizeAttributeName(attribute).Contains("AllowAnonymous")).ToList();
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
            foreach (var policy in AttributeStringArguments(attribute)) AddDistinct(endpoint.AuthorizationPolicies, policy);
            foreach (var role in SplitCommaSeparated(AttributeStringArguments(attribute, "Roles"))) AddDistinct(endpoint.Roles, role);
            foreach (var scheme in SplitCommaSeparated(AttributeStringArguments(attribute, "AuthenticationSchemes"))) AddDistinct(endpoint.AuthenticationSchemes, scheme);
            foreach (var policy in AttributeStringArguments(attribute, "Policy")) AddDistinct(endpoint.AuthorizationPolicies, policy);
        }

        foreach (var attribute in attributeList.Where(attribute => NormalizeAttributeName(attribute).Contains("RequiredScope", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var scope in AttributeStringArguments(attribute)) AddDistinct(endpoint.RequiredScopes, scope);
            foreach (var scope in SplitCommaSeparated(AttributeStringArguments(attribute, "Scopes").Concat(AttributeStringArguments(attribute, "Scope")))) AddDistinct(endpoint.RequiredScopes, scope);
        }

        foreach (var attribute in attributeList.Where(attribute => NormalizeAttributeName(attribute).Contains("RequireClaim", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var claim in AttributeStringArguments(attribute)) AddDistinct(endpoint.RequiredClaims, claim);
        }

        foreach (var attribute in attributeList.Where(attribute => NormalizeAttributeName(attribute).Contains("EnableCors", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var policy in AttributeStringArguments(attribute)) AddDistinct(endpoint.CorsPolicies, policy);
        }

        if (attributeList.Any(attribute => NormalizeAttributeName(attribute).Contains("ValidateAntiForgeryToken", StringComparison.OrdinalIgnoreCase) || NormalizeAttributeName(attribute).Contains("AutoValidateAntiforgeryToken", StringComparison.OrdinalIgnoreCase)))
        {
            endpoint.AntiForgeryRequired = true;
        }

        if (attributeList.Any(attribute => NormalizeAttributeName(attribute).Contains("IgnoreAntiforgeryToken", StringComparison.OrdinalIgnoreCase)))
        {
            endpoint.AntiForgeryRequired = false;
        }
    }

    /// <summary>
    ///     Walks the fluent chain above a Map* invocation invocation-by-invocation (not by substring)
    ///     and applies authorization and metadata from RequireAuthorization/AllowAnonymous/RequireCors/etc.
    /// </summary>
    internal static void ApplyMinimalApiMetadata(ApiEndpoint endpoint, InvocationExpressionSyntax mapInvocation)
    {
        foreach (var invocation in FluentChain(mapInvocation))
        {
            var name = InvocationName(invocation);
            switch (name)
            {
                case "AllowAnonymous":
                    endpoint.AllowAnonymous = true;
                    endpoint.AuthorizationRequired = false;
                    break;
                case "RequireAuthorization":
                    endpoint.AuthorizationRequired = true;
                    foreach (var policy in StringArguments(invocation)) AddDistinct(endpoint.AuthorizationPolicies, policy);
                    break;
                case "RequireCors":
                    foreach (var policy in StringArguments(invocation)) AddDistinct(endpoint.CorsPolicies, policy);
                    break;
                case "DisableAntiforgery":
                    endpoint.AntiForgeryRequired = false;
                    break;
                case "RequireAntiforgery":
                    endpoint.AntiForgeryRequired = true;
                    break;
            }
        }
    }

    /// <summary>
    ///     The fluent chain of an endpoint registration: the Map* invocation itself plus every
    ///     enclosing invocation in the same statement (RequireAuthorization(...), WithTags(...), ...).
    ///     Walks node-by-node so nested lambdas are never mistaken for the endpoint's own chain.
    /// </summary>
    internal static IEnumerable<InvocationExpressionSyntax> FluentChain(InvocationExpressionSyntax invocation)
    {
        yield return invocation;
        SyntaxNode? current = invocation;
        var node = invocation.Parent;
        while (node is not null && current is not null)
        {
            if (node is MemberAccessExpressionSyntax member && member.Expression == current)
            {
                current = member;
                node = member.Parent;
                continue;
            }

            if (node is InvocationExpressionSyntax parentInvocation && !parentInvocation.ArgumentList.Arguments.Any(argument => argument.Expression == current || argument.Expression.Contains(current)))
            {
                yield return parentInvocation;
                current = parentInvocation;
                node = parentInvocation.Parent;
                continue;
            }

            yield break;
        }
    }

    /// <summary>The invoked method name: app.MapGet -> "MapGet", MapGet -> "MapGet".</summary>
    internal static string InvocationName(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.Text,
        IdentifierNameSyntax identifier => identifier.Identifier.Text,
        _ => string.Empty
    };

    /// <summary>The receiver text of an invocation: "app" for app.MapGet(...).</summary>
    internal static string? InvocationReceiver(InvocationExpressionSyntax invocation) => (invocation.Expression as MemberAccessExpressionSyntax)?.Expression.ToString();

    /// <summary>Every string literal argument of an invocation.</summary>
    internal static List<string> StringArguments(InvocationExpressionSyntax invocation) => StringArguments(invocation.ArgumentList);

    /// <summary>Every string literal argument of any argument list (invocations, constructions, indexers).</summary>
    internal static List<string> StringArguments(Microsoft.CodeAnalysis.CSharp.Syntax.ArgumentListSyntax argumentList) => argumentList.Arguments
        .Select(argument => argument.Expression)
        .OfType<LiteralExpressionSyntax>()
        .Where(literal => literal.Token.Value is string)
        .Select(literal => (string)literal.Token.Value!)
        .ToList();

    /// <summary>Absolute URLs found in file text: heuristic, file-scoped evidence (RawUrls), sorted and de-duplicated for stable output.</summary>
    internal static List<string> ExtractRawUrls(string text) =>
        AbsoluteUrlRegex().Matches(text)
            .Select(match => match.Value.TrimEnd('.', ',', ';', ')'))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(url => url, StringComparer.Ordinal)
            .ToList();

    internal static void AddDistinct(List<string> target, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && !target.Contains(value, StringComparer.Ordinal))
        {
            target.Add(value);
        }
    }

    internal static IEnumerable<string> SplitCommaSeparated(IEnumerable<string> values)
    {
        foreach (var value in values)
        {
            foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                yield return part;
            }
        }
    }

    [GeneratedRegex(@"https?://[^\s\""'<>]+", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex AbsoluteUrlRegex();
}
