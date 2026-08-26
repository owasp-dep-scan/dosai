using System.Text;

namespace Depscan.Frameworks;

/// <summary>
///     Resolved form of an ASP.NET route template: the verbatim template with tokens substituted,
///     constraints/defaults/optionality stripped from the emitted path, and every parameter recorded.
/// </summary>
public sealed record ResolvedRouteTemplate
{
    /// <summary>
    ///     Resolved, normalized path starting with "/". Null when the template could not be resolved
    ///     (null template, unresolved token, or a caller that could not evaluate a non-literal template).
    /// </summary>
    public string? Path { get; init; }

    /// <summary>The template with tokens substituted and parameter constraints/defaults stripped, e.g. "api/Orders/{id}".</summary>
    public string NormalizedTemplate { get; init; } = string.Empty;

    public List<RouteParameter> Parameters { get; init; } = [];

    /// <summary>Bracket tokens found in the template, e.g. ["controller"].</summary>
    public List<string> Tokens { get; init; } = [];

    /// <summary>True when a bracket token had no value and the template could not be resolved to a path.</summary>
    public bool HasUnresolvedTokens { get; init; }

    /// <summary>True when a parameter segment was malformed (unbalanced braces); the segment is kept verbatim.</summary>
    public bool HasMalformedSegment { get; init; }

    public string Confidence => Path is null ? "low" : HasMalformedSegment ? "medium" : "high";
}

/// <summary>Values used to substitute ASP.NET route tokens such as [controller] and [action].</summary>
public sealed record RouteTokenValues
{
    public string? Controller { get; init; }

    public string? Action { get; init; }

    public string? Area { get; init; }
}

/// <summary>
///     ASP.NET Core route template resolution: token substitution ([controller]/[action]/[area]),
///     template combination with override semantics, and parameter normalization with a paren-aware
///     tokenizer that survives regex constraints containing braces. All members are pure functions.
/// </summary>
public static class RouteTemplateResolver
{
    /// <summary>
    ///     Combines a prefix (class route, route group, or path base) with a route using ASP.NET
    ///     override semantics: a route starting with "/" or "~/" replaces the prefix entirely.
    /// </summary>
    public static string Combine(string? prefix, string? route)
    {
        if (string.IsNullOrWhiteSpace(route)) return prefix ?? string.Empty;
        if (route.StartsWith('~') || route.StartsWith('/')) return route.TrimStart('~');
        if (string.IsNullOrWhiteSpace(prefix)) return route;
        return $"{prefix.TrimEnd('/')}/{route.TrimStart('/')}";
    }

    /// <summary>
    ///     Combines a route-group prefix with a template by plain concatenation. Unlike
    ///     <see cref="Combine" />, a template starting with "/" does NOT override: minimal API
    ///     route groups always apply, e.g. MapGroup("/api").MapGet("/orders") -> /api/orders.
    /// </summary>
    public static string CombinePrefix(string? prefix, string? template)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return template ?? string.Empty;
        if (string.IsNullOrWhiteSpace(template)) return prefix;
        return $"{prefix.TrimEnd('/')}/{template.TrimStart('/')}";
    }

    /// <summary>
    ///     Normalizes a leading prefix (path base or route group) into a path-rooted form:
    ///     "/group" (no trailing slash, always a leading slash).
    /// </summary>
    public static string NormalizePrefix(string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return string.Empty;
        return "/" + prefix.Trim('~', '/').Trim();
    }

    /// <summary>
    ///     Resolves a route template: substitutes bracket tokens, strips constraints, defaults, and
    ///     optionality from the emitted path, and records every parameter. Never throws; malformed
    ///     segments are kept verbatim and flagged.
    /// </summary>
    public static ResolvedRouteTemplate Resolve(string? template, RouteTokenValues? tokens = null)
    {
        if (template is null)
        {
            return new ResolvedRouteTemplate();
        }

        var (substituted, foundTokens, unresolved) = SubstituteTokens(template, tokens);
        var segments = SplitSegments(substituted);
        var parameters = new List<RouteParameter>();
        var normalizedSegments = new List<string>();
        var malformed = false;

        foreach (var segment in segments)
        {
            if (TryNormalizeParameterSegment(segment, out var parameter, out var normalized))
            {
                parameters.Add(parameter);
                normalizedSegments.Add(normalized);
            }
            else if (TryNormalizeEmbeddedParameters(segment, parameters, out normalized))
            {
                normalizedSegments.Add(normalized);
            }
            else
            {
                if (segment.Contains('{') || segment.EndsWith('}'))
                {
                    malformed = true;
                }

                normalizedSegments.Add(segment);
            }
        }

        var normalizedTemplate = string.Join("/", normalizedSegments);
        string? path;
        if (unresolved)
        {
            path = null;
        }
        else if (normalizedSegments.Count == 0 || string.IsNullOrEmpty(normalizedTemplate))
        {
            path = "/";
        }
        else
        {
            path = "/" + normalizedTemplate;
        }

        return new ResolvedRouteTemplate
        {
            Path = path,
            NormalizedTemplate = normalizedTemplate,
            Parameters = parameters,
            Tokens = foundTokens,
            HasUnresolvedTokens = unresolved,
            HasMalformedSegment = malformed
        };
    }

    /// <summary>
    ///     Replaces a resolved parameter (e.g. "{version}") with a concrete value in Path and
    ///     NormalizedTemplate and drops it from Parameters. Used to expand segment-versioned
    ///     routes ("v{version:apiVersion}") into concrete paths ("/v1.0/Orders") once the
    ///     declared API versions are known. A no-op when the parameter is not present.
    /// </summary>
    public static ResolvedRouteTemplate SubstituteParameter(ResolvedRouteTemplate resolved, string parameterName, string value)
    {
        if (resolved.Parameters.All(parameter => !parameter.Name.Equals(parameterName, StringComparison.Ordinal)))
        {
            return resolved;
        }

        var token = $"{{{parameterName}}}";
        return resolved with
        {
            Path = resolved.Path?.Replace(token, value, StringComparison.Ordinal),
            NormalizedTemplate = resolved.NormalizedTemplate.Replace(token, value, StringComparison.Ordinal),
            Parameters = resolved.Parameters.Where(parameter => !parameter.Name.Equals(parameterName, StringComparison.Ordinal)).ToList()
        };
    }

    /// <summary>
    ///     Expands a conventional routing pattern such as "{controller=Home}/{action=Index}/{id?}"
    ///     for one concrete (controller, action) pair. Controller/action segments are replaced with
    ///     the concrete value (or the declared default); other parameter segments are normalized and
    ///     recorded. Returns null when neither a controller nor an action value is available.
    /// </summary>
    public static ResolvedRouteTemplate ExpandConventional(string? pattern, string? controller, string? action)
    {
        if (string.IsNullOrWhiteSpace(pattern) || (controller is null && action is null))
        {
            return new ResolvedRouteTemplate();
        }

        var segments = SplitSegments(pattern);
        var parameters = new List<RouteParameter>();
        var outputSegments = new List<string>();
        var malformed = false;

        foreach (var segment in segments)
        {
            if (TryNormalizeParameterSegment(segment, out var parameter, out var normalized))
            {
                var isController = string.Equals(parameter.Name, "controller", StringComparison.OrdinalIgnoreCase);
                var isAction = string.Equals(parameter.Name, "action", StringComparison.OrdinalIgnoreCase);
                if (isController)
                {
                    outputSegments.Add(controller ?? parameter.DefaultValue ?? parameter.Name);
                    continue;
                }

                if (isAction)
                {
                    outputSegments.Add(action ?? parameter.DefaultValue ?? parameter.Name);
                    continue;
                }

                parameters.Add(parameter);
                // An optional parameter without a default ({id?}) contributes no path segment;
                // it is recorded as a parameter rather than emitted as a mandatory {id} segment.
                if (parameter.Optional && parameter.DefaultValue is null && !parameter.CatchAll)
                {
                    continue;
                }

                if (parameter.DefaultValue is { } inlineDefault)
                {
                    outputSegments.Add($"{normalized}={inlineDefault}");
                    continue;
                }

                outputSegments.Add(normalized);
            }
            else
            {
                if (segment.Contains('{') || segment.EndsWith('}'))
                {
                    malformed = true;
                }

                outputSegments.Add(segment);
            }
        }

        var normalizedTemplate = string.Join("/", outputSegments);
        return new ResolvedRouteTemplate
        {
            Path = "/" + normalizedTemplate,
            NormalizedTemplate = normalizedTemplate,
            Parameters = parameters,
            HasMalformedSegment = malformed
        };
    }

    /// <summary>
    ///     Strips a trailing "Async" suffix the way ASP.NET Core names actions by default
    ///     (SuppressAsyncSuffixInActionNames defaults to false, which removes the suffix).
    /// </summary>
    public static string ActionName(string methodName) => methodName.Length > 5 && methodName.EndsWith("Async", StringComparison.Ordinal) ? methodName[..^5] : methodName;

    /// <summary>Derives the route [controller] token value: the type name minus a trailing "Controller".</summary>
    public static string ControllerName(string className) => className.EndsWith("Controller", StringComparison.Ordinal) ? className[..^"Controller".Length] : className;

    internal static (string Substituted, List<string> Tokens, bool Unresolved) SubstituteTokens(string template, RouteTokenValues? tokens)
    {
        var found = new List<string>();
        var unresolved = false;
        var result = template;
        foreach (var (token, value) in new[]
                 {
                     ("controller", tokens?.Controller),
                     ("action", tokens?.Action),
                     ("area", tokens?.Area)
                 })
        {
            result = ReplaceBracketToken(result, token, value, found, ref unresolved);
        }

        return (result, found, unresolved);
    }

    private static string ReplaceBracketToken(string template, string token, string? value, List<string> found, ref bool unresolved)
    {
        var result = template;
        while (result.Contains($"[{token}]", StringComparison.OrdinalIgnoreCase))
        {
            found.Add(token);
            if (string.IsNullOrEmpty(value))
            {
                unresolved = true;
                break;
            }

            result = ReplaceFirstIgnoreCase(result, $"[{token}]", value);
        }

        return result;
    }

    private static string ReplaceFirstIgnoreCase(string text, string search, string replacement)
    {
        var index = text.IndexOf(search, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return text;
        }

        return text[..index] + replacement + text[(index + search.Length)..];
    }

    /// <summary>
    ///     Splits a template into "/"-separated segments, dropping empty segments caused by duplicate
    ///     slashes or leading/trailing separators.
    /// </summary>
    private static string[] SplitSegments(string template)
    {
        return template.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>
    ///     Parses one parameter segment such as "{id:int:min(1)}", "{id?}", "{page=1}", "{*slug}",
    ///     or "{id:regex(^\d{2,3}$)}". Braces inside parentheses (regex quantifiers/classes) do not
    ///     affect parameter boundaries. Returns false when the segment is not a well-formed parameter.
    /// </summary>
    private static bool TryNormalizeParameterSegment(string segment, out RouteParameter parameter, out string normalized)
    {
        parameter = new RouteParameter();
        normalized = segment;

        var body = segment.Trim();
        if (body.Length < 2 || !body.StartsWith('{') || !body.EndsWith('}'))
        {
            return false;
        }

        // A multi-parameter segment such as "{a}-{b}" also starts with '{' and ends with '}':
        // the single-parameter fast path applies only when the first brace-balanced close (outside
        // parentheses, so regex constraints keep their braces) is the segment's final character.
        if (FindParameterEnd(body, 0) != body.Length - 1)
        {
            return false;
        }

        if (!TryParseParameterContent(body[1..^1], out parameter))
        {
            return false;
        }

        normalized = $"{{{parameter.Name}}}";
        return true;
    }

    /// <summary>
    ///     Normalizes a segment that mixes literals with one or more parameters, such as
    ///     "v{version:apiVersion}" or "{language}-{culture}". Every parameter region is
    ///     constraint-stripped and recorded, the literals stay verbatim. All-or-nothing: any
    ///     unbalanced or unparseable region leaves the whole segment untouched (caller flags it).
    /// </summary>
    private static bool TryNormalizeEmbeddedParameters(string segment, List<RouteParameter> parameters, out string normalized)
    {
        normalized = segment;
        if (!segment.Contains('{'))
        {
            return false;
        }

        var result = new StringBuilder();
        var index = 0;
        var found = 0;
        while (index < segment.Length)
        {
            if (segment[index] != '{')
            {
                result.Append(segment[index]);
                index++;
                continue;
            }

            var end = FindParameterEnd(segment, index);
            if (end < 0 || !TryParseParameterContent(segment[(index + 1)..end], out var parameter))
            {
                return false;
            }

            result.Append('{').Append(parameter.Name).Append('}');
            parameters.Add(parameter);
            found++;
            index = end + 1;
        }

        if (found == 0)
        {
            return false;
        }

        normalized = result.ToString();
        return true;
    }

    /// <summary>
    ///     Index of the '}' closing the parameter opened at <paramref name="openIndex" />, skipping
    ///     braces inside parentheses (regex constraints) and rejecting nested parameter braces.
    /// </summary>
    private static int FindParameterEnd(string segment, int openIndex)
    {
        var parenDepth = 0;
        for (var i = openIndex + 1; i < segment.Length; i++)
        {
            var c = segment[i];
            if (c == '(')
            {
                parenDepth++;
            }
            else if (c == ')')
            {
                parenDepth = Math.Max(0, parenDepth - 1);
            }
            else if (parenDepth == 0)
            {
                if (c == '}')
                {
                    return i;
                }

                if (c == '{')
                {
                    return -1;
                }
            }
        }

        return -1;
    }

    /// <summary>Parses the inside of a parameter's braces: catch-all marker, name, constraints, default.</summary>
    private static bool TryParseParameterContent(string content, out RouteParameter parameter)
    {
        parameter = new RouteParameter();

        var isCatchAll = false;
        if (content.StartsWith("**", StringComparison.Ordinal))
        {
            isCatchAll = true;
            content = content[2..];
        }
        else if (content.StartsWith("*", StringComparison.Ordinal))
        {
            isCatchAll = true;
            content = content[1..];
        }

        if (content.Length == 0)
        {
            return false;
        }

        // Parse name / constraints / default in one pass; separators inside parentheses (regex
        // arguments such as regex(a{2}) or regex(x:y)) never split.
        var constraints = new List<string>();
        string? defaultValue = null;
        var optional = false;
        var nameChars = new List<char>();
        var current = new List<char>();
        var mode = 0; // 0 = name, 1 = constraints, 2 = default
        var parenDepth = 0;
        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];
            if (c == '(')
            {
                parenDepth++;
            }
            else if (c == ')' && parenDepth > 0)
            {
                parenDepth--;
            }

            if (parenDepth == 0 && mode == 0 && (c == ':' || c == '='))
            {
                nameChars.AddRange(current);
                current.Clear();
                mode = c == ':' ? 1 : 2;
                continue;
            }

            if (parenDepth == 0 && mode == 1 && c == '=')
            {
                AddConstraint(constraints, new string(current.ToArray()));
                current.Clear();
                mode = 2;
                continue;
            }

            if (parenDepth == 0 && mode == 1 && c == ':')
            {
                AddConstraint(constraints, new string(current.ToArray()));
                current.Clear();
                continue;
            }

            current.Add(c);
        }

        switch (mode)
        {
            case 0:
                nameChars.AddRange(current);
                break;
            case 1:
                AddConstraint(constraints, new string(current.ToArray()));
                break;
            default:
                defaultValue = new string(current.ToArray());
                break;
        }

        var parameterName = new string(nameChars.ToArray());
        if (parameterName.EndsWith('?'))
        {
            optional = true;
            parameterName = parameterName[..^1];
        }

        if (constraints.LastOrDefault()?.EndsWith('?') == true)
        {
            optional = true;
            constraints[^1] = constraints[^1][..^1];
            if (constraints[^1].Length == 0)
            {
                constraints.RemoveAt(constraints.Count - 1);
            }
        }

        if (defaultValue is not null && defaultValue.EndsWith('?'))
        {
            optional = true;
            defaultValue = defaultValue[..^1];
        }

        if (parameterName.Length == 0)
        {
            return false;
        }

        parameter = new RouteParameter
        {
            Name = parameterName,
            Constraints = constraints,
            Optional = optional,
            DefaultValue = defaultValue,
            CatchAll = isCatchAll
        };
        return true;
    }

    private static void AddConstraint(List<string> constraints, string constraint)
    {
        constraint = constraint.Trim(':').Trim();
        if (constraint.Length > 0 && !constraints.Contains(constraint, StringComparer.Ordinal))
        {
            constraints.Add(constraint);
        }
    }


}
