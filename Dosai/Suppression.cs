using System.Text.Json;
using System.Text.Json.Serialization;

namespace Depscan;

/// <summary>
///     A parsed suppressions file: a JSON array of
///     <c>{ "file": "Foo.cs", "line": 12, "sliceKey": "...", "weaknessId": "wc3", "category": "sql", "expires": "2027-12-31", "reason": "..." }</c>.
///     A suppression entry is a security-relevant allowlist, so over-suppression is the worst
///     failure mode: an entry matches only when <em>every</em> field present in the entry matches
///     (AND semantics), and an entry with no matchers matches nothing.
/// </summary>
public sealed class SuppressionSet
{
    private readonly List<Suppression> _suppressions;

    private SuppressionSet(List<Suppression> suppressions) => _suppressions = suppressions;

    public static SuppressionSet Load(string path)
    {
        var json = File.ReadAllText(path);
        var suppressions = JsonSerializer.Deserialize<List<Suppression>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        }) ?? [];
        return new SuppressionSet(suppressions);
    }

    public IReadOnlyList<Suppression> Entries => _suppressions;

    public bool IsSuppressed(DataFlowSlice slice, WeaknessCandidate? weakness)
    {
        var sliceKey = TransparencyBuilder.SliceKey(slice);
        foreach (var suppression in _suppressions)
        {
            if (!suppression.IsExpired && suppression.MatchesSlice(slice, sliceKey, weakness))
            {
                return true;
            }
        }

        return false;
    }

    public bool MatchesWeakness(WeaknessCandidate weakness)
    {
        foreach (var suppression in _suppressions)
        {
            if (!suppression.IsExpired && suppression.MatchesWeakness(weakness))
            {
                return true;
            }
        }

        return false;
    }

    public sealed class Suppression
    {
        public string? File { get; set; }
        public int? Line { get; set; }
        public string? SliceKey { get; set; }
        public string? WeaknessId { get; set; }
        public string? Category { get; set; }
        public DateTime? Expires { get; set; }
        public string? Reason { get; set; }

        public bool IsExpired => Expires is { } expiry && expiry <= DateTime.UtcNow;

        private bool HasAnyMatcher => File is not null || Line is not null || SliceKey is not null || WeaknessId is not null || Category is not null;

        /// <summary>AND semantics: every field present in the entry must match; absent fields are ignored.</summary>
        public bool MatchesSlice(DataFlowSlice slice, string sliceKey, WeaknessCandidate? weakness)
        {
            if (!HasAnyMatcher)
            {
                return false;
            }

            if (SliceKey is not null && !string.Equals(SliceKey, sliceKey, StringComparison.Ordinal))
            {
                return false;
            }

            if (Category is not null && !string.Equals(Category, slice.SinkCategory, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (WeaknessId is not null && (weakness is null || !string.Equals(WeaknessId, weakness.Id, StringComparison.Ordinal)))
            {
                return false;
            }

            if (!MatchesLocation(weakness))
            {
                return false;
            }

            return true;
        }

        public bool MatchesWeakness(WeaknessCandidate weakness)
        {
            if (!HasAnyMatcher)
            {
                return false;
            }

            if (WeaknessId is not null && !string.Equals(WeaknessId, weakness.Id, StringComparison.Ordinal))
            {
                return false;
            }

            if (Category is not null && !string.Equals(Category, weakness.SinkCategory, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!MatchesLocation(weakness))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        ///     Matches the <c>file</c>/<c>line</c> pair against the weakness's source/sink locations
        ///     ("File.cs:line:column"; locations live on the weakness, not the slice). A single
        ///     location must satisfy <em>both</em> matchers, otherwise
        ///     <c>{"file":"A.cs","line":12}</c> would suppress a flow whose source is <c>A.cs:5</c>
        ///     and whose sink is <c>B.cs:12</c>, which is neither location the author named.
        /// </summary>
        private bool MatchesLocation(WeaknessCandidate? weakness)
        {
            if (File is null && Line is null)
            {
                return true;
            }

            return weakness is not null &&
                   (MatchesOneLocation(weakness.SourceLocation) || MatchesOneLocation(weakness.SinkLocation));
        }

        private bool MatchesOneLocation(string? location)
        {
            if (string.IsNullOrWhiteSpace(location))
            {
                return false;
            }

            if (File is not null && !location.StartsWith(File, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return Line is null || (TryLine(location, out var line) && line == Line);
        }

        private static bool TryLine(string? location, out int line)
        {
            // Locations are "File.cs:line:column".
            line = 0;
            if (string.IsNullOrWhiteSpace(location))
            {
                return false;
            }

            var parts = location.Split(':');
            return parts.Length >= 2 && int.TryParse(parts[^2], out line);
        }
    }
}
