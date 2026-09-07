using System.Text.Json;

namespace Depscan;

/// <summary>
///     Filters Dosai JSON with a compact query expression:
///     <c>collection[prop op value &amp;&amp; …] [sort by prop [desc]] [count]</c>.
///     The collection may be a nested path (<c>callGraph.nodes</c>); filter terms combine with
///     <c>&amp;&amp;</c> (AND of conjuncts) and <c>||</c> (OR of terms inside a conjunct).
/// </summary>
public static class DosaiQueryEngine
{
    public static string QueryJson(string json, string query)
    {
        using var document = JsonDocument.Parse(json);
        var parsed = ParseQuery(query);
        var results = Filter(document.RootElement, parsed).ToList();
        if (parsed.CountMode)
        {
            return JsonSerializer.Serialize(new { count = results.Count }, new JsonSerializerOptions { WriteIndented = true });
        }

        return JsonSerializer.Serialize(ApplySort(results, parsed).Select(CloneElement), new JsonSerializerOptions { WriteIndented = true });
    }

    private sealed record ParsedQuery(
        List<string> PathSegments,
        List<List<(string Property, string Operator, string Value)>> FilterGroups,
        string? SortProperty,
        bool SortDescending,
        bool CountMode);

    private static IEnumerable<JsonElement> Filter(JsonElement root, ParsedQuery query)
    {
        if (!TryResolveCollection(root, query.PathSegments, out var collection) || collection.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return collection.EnumerateArray().Where(element => Matches(element, query.FilterGroups));
    }

    /// <summary>
    ///     Sorts by a (possibly dotted) property; numbers compare numerically, strings
    ///     ordinal-ignore-case, booleans false&lt;true. Elements missing the property sort last in
    ///     both directions; ties keep the input order.
    /// </summary>
    private static IEnumerable<JsonElement> ApplySort(List<JsonElement> results, ParsedQuery query)
    {
        if (query.SortProperty is null)
        {
            return results;
        }

        var keyed = results.Select(element => (Element: element, Key: SortKey(element, query.SortProperty))).ToList();
        var withValue = keyed.Where(pair => pair.Key.HasValue);
        var withoutValue = keyed.Where(pair => !pair.Key.HasValue);
        var ordered = query.SortDescending
            ? withValue.OrderByDescending(pair => pair.Key, SortKeyComparer.Instance)
            : withValue.OrderBy(pair => pair.Key, SortKeyComparer.Instance);
        return ordered.Concat(withoutValue).Select(pair => pair.Element).ToList();
    }

    private sealed class SortKeyComparer : IComparer<(bool HasValue, double Number, string? Text)>
    {
        public static readonly SortKeyComparer Instance = new();

        public int Compare((bool HasValue, double Number, string? Text) x, (bool HasValue, double Number, string? Text) y)
        {
            if (x.Text is null && y.Text is null)
            {
                return x.Number.CompareTo(y.Number);
            }

            if (x.Text is null)
            {
                return -1;
            }

            return y.Text is null ? 1 : string.Compare(x.Text, y.Text, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static (bool HasValue, double Number, string? Text) SortKey(JsonElement element, string property)
    {
        if (!TryResolvePath(element, property, out var value))
        {
            return (false, 0, null);
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => (true, value.GetDouble(), null),
            JsonValueKind.String => (true, 0, value.GetString()),
            JsonValueKind.True => (true, 1, null),
            JsonValueKind.False => (true, 0, null),
            _ => (true, 0, value.ToString())
        };
    }

    private static ParsedQuery ParseQuery(string query)
    {
        query = query.Trim();
        // With brackets the path is everything before '['; without them the path is a single
        // token and any postfix (`count`, `sort by …`) follows after whitespace.
        string head;
        string tail;
        var bracket = query.IndexOf('[', StringComparison.Ordinal);
        if (bracket >= 0)
        {
            head = query[..bracket].Trim();
            tail = query[bracket..];
        }
        else
        {
            var firstSpace = query.IndexOf(' ', StringComparison.Ordinal);
            head = firstSpace < 0 ? query : query[..firstSpace].Trim();
            tail = firstSpace < 0 ? string.Empty : query[firstSpace..];
        }

        var pathSegments = head
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeSegment)
            .ToList();

        // Postfixes after the closing bracket (or after the head when bracket-less): the optional
        // `sort by prop [desc]` and the optional `count` aggregate.
        var end = tail.LastIndexOf(']');
        var filterText = end >= 0 ? tail[1..end] : string.Empty;
        var remainder = end >= 0 ? tail[(end + 1)..].Trim() : tail.Trim();
        var sortProperty = default(string?);
        var sortDescending = false;
        var countMode = false;
        var remainderWords = remainder.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var index = 0; index < remainderWords.Length; index++)
        {
            var word = remainderWords[index];
            if (word.Equals("count", StringComparison.OrdinalIgnoreCase))
            {
                countMode = true;
            }
            else if (word.Equals("sort", StringComparison.OrdinalIgnoreCase) &&
                     index + 1 < remainderWords.Length &&
                     remainderWords[index + 1].Equals("by", StringComparison.OrdinalIgnoreCase) &&
                     index + 2 < remainderWords.Length)
            {
                sortProperty = remainderWords[index + 2];
                index += 2;
                if (index + 1 < remainderWords.Length && remainderWords[index + 1].Equals("desc", StringComparison.OrdinalIgnoreCase))
                {
                    sortDescending = true;
                    index++;
                }
            }
        }

        var filterGroups = new List<List<(string Property, string Operator, string Value)>>();
        foreach (var conjunct in filterText.Split("&&", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // `||` ORs terms inside a conjunct, `severity=high||severity=critical`.
            var terms = conjunct
                .Split("||", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(ParseFilter)
                .ToList();
            if (terms.Count > 0)
            {
                filterGroups.Add(terms);
            }
        }

        return new ParsedQuery(pathSegments, filterGroups, sortProperty, sortDescending, countMode);
    }

    private static (string Property, string Operator, string Value) ParseFilter(string filter)
    {
        foreach (var op in new[] { "~=", "!=", ">=", "<=", "=", ">", "<" })
        {
            var index = filter.IndexOf(op, StringComparison.Ordinal);
            if (index > 0)
            {
                return (filter[..index].Trim(), op, TrimQuotes(filter[(index + op.Length)..].Trim()));
            }
        }

        return (filter.Trim(), "=", "true");
    }

    private static bool Matches(JsonElement element, IEnumerable<List<(string Property, string Operator, string Value)>> filterGroups)
    {
        // Conjuncts AND, terms inside a conjunct OR.
        return filterGroups.All(group => group.Any(term => TryResolvePath(element, term.Property, out var value) && Compare(value, term.Operator, term.Value)));
    }

    private static bool TryResolveCollection(JsonElement root, List<string> segments, out JsonElement collection)
    {
        collection = root;
        foreach (var segment in segments)
        {
            if (!TryGetPropertyCaseInsensitive(collection, segment, out collection))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryResolvePath(JsonElement element, string path, out JsonElement value)
    {
        value = element;
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryGetPropertyCaseInsensitive(value, part, out value))
            {
                return false;
            }
        }
        return true;
    }

    private static bool Compare(JsonElement value, string op, string expected)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            return value.EnumerateArray().Any(item => CompareScalar(item, op, expected));
        }
        return CompareScalar(value, op, expected);
    }

    private static bool CompareScalar(JsonElement value, string op, string expected)
    {
        var actual = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => value.ToString(),
            _ => value.ToString()
        };

        if (double.TryParse(actual, out var actualNumber) && double.TryParse(expected, out var expectedNumber))
        {
            return op switch
            {
                "=" => actualNumber.Equals(expectedNumber),
                "!=" => !actualNumber.Equals(expectedNumber),
                ">" => actualNumber > expectedNumber,
                "<" => actualNumber < expectedNumber,
                ">=" => actualNumber >= expectedNumber,
                "<=" => actualNumber <= expectedNumber,
                "~=" => actual.Contains(expected, StringComparison.OrdinalIgnoreCase),
                _ => false
            };
        }

        return op switch
        {
            "=" => actual.Equals(expected, StringComparison.OrdinalIgnoreCase),
            "!=" => !actual.Equals(expected, StringComparison.OrdinalIgnoreCase),
            "~=" => actual.Contains(expected, StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static bool TryGetPropertyCaseInsensitive(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    /// <summary>
    ///     Per-segment alias normalization. Historical aliases (plurals, crypto prefixes) keep
    ///     working; nested-path segments like <c>callGraph</c>/<c>nodes</c> normalize to their
    ///     canonical property names. Unknown segments pass through literally.
    /// </summary>
    private static string NormalizeSegment(string segment) => segment.ToLowerInvariant() switch
    {
        "node" or "nodes" => "nodes",
        "edge" or "edges" => "edges",
        "slice" or "slices" => "slices",
        "weakness" or "weaknesses" or "weaknesscandidates" => "weaknessCandidates",
        "entrypoint" or "entrypoints" => "entryPoints",
        "package" or "packages" or "packagereachability" => "packageReachability",
        "dangerous" or "dangerousapis" or "dangerousapireachability" => "dangerousApiReachability",
        "summary" or "summaries" or "methodsummaries" => "methodSummaries",
        "exploitchain" or "exploitchains" or "chains" => "exploitChains",
        "sanitizedflow" or "sanitizedflows" => "sanitizedFlows",
        "reachability" or "reachabilityfacts" => "reachability",
        "recursioncluster" or "recursionclusters" or "clusters" => "recursionClusters",
        "deadcode" or "deadcodeentries" => "deadCode",
        "attacksurface" or "surface" => "attackSurface",
        "securityfinding" or "securityfindings" or "endpointfinding" or "endpointfindings" => "securityFindings",
        "method" or "methods" => "methods",
        "callgraph" => "callGraph",
        "cryptoasset" or "cryptoassets" or "assets" => "assets",
        "cryptooperation" or "cryptooperations" or "operations" => "operations",
        "cryptomaterial" or "cryptomaterials" or "materials" => "materials",
        "cryptoprotocol" or "cryptoprotocols" or "protocols" => "protocols",
        "cryptofinding" or "cryptofindings" or "findings" => "findings",
        _ => segment
    };

    private static string TrimQuotes(string value) => value.Trim().Trim('"', '\'');

    private static JsonElement CloneElement(JsonElement element)
    {
        using var document = JsonDocument.Parse(element.GetRawText());
        return document.RootElement.Clone();
    }
}
