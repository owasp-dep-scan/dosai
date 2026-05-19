using System.Text.Json;

namespace Depscan;

public static class DosaiQueryEngine
{
    public static string QueryJson(string json, string query)
    {
        using var document = JsonDocument.Parse(json);
        var results = Query(document.RootElement, query).ToList();
        return JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true });
    }

    private static IEnumerable<JsonElement> Query(JsonElement root, string query)
    {
        var (collectionName, filters) = Parse(query);
        if (!TryGetPropertyCaseInsensitive(root, collectionName, out var collection) || collection.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return collection.EnumerateArray().Where(element => Matches(element, filters)).Select(CloneElement);
    }

    private static (string Collection, List<(string Property, string Operator, string Value)> Filters) Parse(string query)
    {
        query = query.Trim();
        var bracket = query.IndexOf('[', StringComparison.Ordinal);
        if (bracket < 0)
        {
            return (NormalizeCollection(query), []);
        }

        var collection = NormalizeCollection(query[..bracket].Trim());
        var end = query.LastIndexOf(']');
        var filterText = end > bracket ? query[(bracket + 1)..end] : string.Empty;
        var filters = filterText.Split("&&", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseFilter)
            .ToList();
        return (collection, filters);
    }

    private static (string Property, string Operator, string Value) ParseFilter(string filter)
    {
        foreach (var op in new[] { "~=", "!=", "=", ">=", "<=", ">", "<" })
        {
            var index = filter.IndexOf(op, StringComparison.Ordinal);
            if (index > 0)
            {
                return (filter[..index].Trim(), op, TrimQuotes(filter[(index + op.Length)..].Trim()));
            }
        }

        return (filter.Trim(), "=", "true");
    }

    private static bool Matches(JsonElement element, IEnumerable<(string Property, string Operator, string Value)> filters)
    {
        foreach (var (property, op, expected) in filters)
        {
            if (!TryResolvePath(element, property, out var value) || !Compare(value, op, expected))
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

    private static string NormalizeCollection(string collection) => collection.ToLowerInvariant() switch
    {
        "node" or "nodes" => "nodes",
        "edge" or "edges" => "edges",
        "slice" or "slices" => "slices",
        "weakness" or "weaknesses" or "weaknesscandidates" => "weaknessCandidates",
        "entrypoint" or "entrypoints" => "entryPoints",
        "package" or "packages" or "packagereachability" => "packageReachability",
        "dangerous" or "dangerousapis" or "dangerousapireachability" => "dangerousApiReachability",
        "summary" or "summaries" or "methodsummaries" => "methodSummaries",
        "cryptoasset" or "cryptoassets" or "assets" => "assets",
        "cryptooperation" or "cryptooperations" or "operations" => "operations",
        "cryptomaterial" or "cryptomaterials" or "materials" => "materials",
        "cryptoprotocol" or "cryptoprotocols" or "protocols" => "protocols",
        "cryptofinding" or "cryptofindings" or "findings" => "findings",
        _ => collection
    };

    private static string TrimQuotes(string value) => value.Trim().Trim('"', '\'');

    private static JsonElement CloneElement(JsonElement element)
    {
        using var document = JsonDocument.Parse(element.GetRawText());
        return document.RootElement.Clone();
    }
}
