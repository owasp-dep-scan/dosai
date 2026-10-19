using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Depscan;

/// <summary>
///     The parsed value of the methods command's <c>--sections</c> option: which top-level
///     <see cref="MethodsSlice" /> sections to write. Parsing and validation live here so the
///     CLI option and the MCP tool reject unknown names the same way (mirroring
///     <see cref="CryptoAnalyzer.ParseDataFlowDetail" />). The option is write-only: it filters
///     the serialized contract and never skips analysis work, and the slice a write method
///     returns stays complete for callers such as the call-graph exporter.
/// </summary>
public sealed class MethodsSections
{
    /// <summary>The canonical names of the sections to write, or null for every section.</summary>
    private readonly HashSet<string>? writtenSections;

    private MethodsSections(HashSet<string>? writtenSections) => this.writtenSections = writtenSections;

    /// <summary>Every section: the state of an absent option.</summary>
    public static MethodsSections All { get; } = new(null);

    public bool IsAll => writtenSections is null;

    /// <summary>
    ///     Parses a <c>--sections</c>-style value: one comma-separated list of top-level section
    ///     names. Entries are trimmed, empty entries are skipped, matching is case-insensitive,
    ///     and duplicates collapse. A list that names no section is an error, as is any unknown
    ///     name; both messages list the valid names in contract order. Null means every section.
    /// </summary>
    public static MethodsSections Parse(string? value)
    {
        if (value is null)
        {
            return All;
        }

        var contract = SectionNames();
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in contract)
        {
            byName[name] = name;
        }

        var written = new HashSet<string>(StringComparer.Ordinal) { "Metadata" };
        var unknown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unknownInOrder = new List<string>();
        var named = 0;
        foreach (var entry in value.Split(','))
        {
            var name = entry.Trim();
            if (name.Length == 0)
            {
                continue;
            }

            if (byName.TryGetValue(name, out var canonical))
            {
                written.Add(canonical);
                named++;
            }
            else if (unknown.Add(name))
            {
                unknownInOrder.Add(name);
            }
        }

        if (unknownInOrder.Count > 0)
        {
            throw new ArgumentException($"Unknown methods section(s): {string.Join(", ", unknownInOrder)}. Valid sections: {string.Join(", ", contract)}.");
        }

        if (named == 0)
        {
            throw new ArgumentException($"No methods section was named: \"{value}\". Valid sections: {string.Join(", ", contract)}.");
        }

        return new MethodsSections(written);
    }

    /// <summary>
    ///     Every top-level section name, in contract order, read from the serializer's own
    ///     contract for <see cref="MethodsSlice" /> with the methods options: a section added to
    ///     the slice later becomes selectable with no change here.
    /// </summary>
    public static IReadOnlyList<string> SectionNames() => ContractSections.Value;

    private static readonly Lazy<IReadOnlyList<string>> ContractSections = new(ReadContractSections);

    private static IReadOnlyList<string> ReadContractSections()
    {
        // A copy keeps the shared options instance untouched; MakeReadOnly fills in the
        // reflection resolver the same way ParallelJsonWriter does before reading a contract.
        var options = new JsonSerializerOptions(Dosai.Options);
        options.MakeReadOnly(populateMissingResolver: true);
        return options.GetTypeInfo(typeof(MethodsSlice)).Properties.Select(property => property.Name).ToArray();
    }

    /// <summary>The omitted section names, in contract order. Metadata is never omitted.</summary>
    public IReadOnlyList<string> OmittedSections()
        => IsAll ? [] : SectionNames().Where(name => !writtenSections!.Contains(name)).ToArray();

    /// <summary>
    ///     A copy of <paramref name="options" /> whose <see cref="MethodsSlice" /> contract drops
    ///     the omitted properties, so both <see cref="JsonSerializer" /> and
    ///     <see cref="ParallelJsonWriter" /> write exactly the selected sections in contract
    ///     order. Removing properties keeps the plain-object fast path; a
    ///     <c>ShouldSerialize</c> predicate would not. The same instance comes back when nothing
    ///     is omitted.
    /// </summary>
    public JsonSerializerOptions FilterOptions(JsonSerializerOptions options)
    {
        var omitted = OmittedSections();
        if (omitted.Count == 0)
        {
            return options;
        }

        var omittedSet = omitted.ToHashSet(StringComparer.Ordinal);
        var filtered = new JsonSerializerOptions(options);
        filtered.TypeInfoResolver = ResolverFor(options).WithAddedModifier(typeInfo =>
        {
            if (typeInfo.Type != typeof(MethodsSlice))
            {
                return;
            }

            for (var index = typeInfo.Properties.Count - 1; index >= 0; index--)
            {
                if (omittedSet.Contains(typeInfo.Properties[index].Name))
                {
                    typeInfo.Properties.RemoveAt(index);
                }
            }
        });
        return filtered;
    }

    /// <summary>
    ///     The options' resolver, or the default reflection resolver for options that never
    ///     resolved a contract: a resolver is only materialized when options are sealed, so an
    ///     unused instance is sealed on a throwaway copy to get at it without touching the
    ///     caller's options.
    /// </summary>
    private static IJsonTypeInfoResolver ResolverFor(JsonSerializerOptions options)
    {
        if (options.TypeInfoResolver is { } resolver)
        {
            return resolver;
        }

        var seeded = new JsonSerializerOptions(options);
        seeded.MakeReadOnly(populateMissingResolver: true);
        return seeded.TypeInfoResolver!;
    }

    /// <summary>
    ///     Runs <paramref name="write" /> with the options that serialize <paramref name="slice" />
    ///     for these sections. When a section is omitted, the slice's metadata names the omission
    ///     for the duration of the write only, so the file says it is partial while the slice the
    ///     caller keeps stays a complete, unannotated result. When nothing is omitted, the write
    ///     gets <paramref name="options" /> unchanged and the output is the default one.
    /// </summary>
    public void Write(MethodsSlice slice, JsonSerializerOptions options, Action<JsonSerializerOptions> write)
    {
        var omitted = OmittedSections();
        if (omitted.Count == 0)
        {
            write(options);
            return;
        }

        var metadata = slice.Metadata;
        var previous = metadata?.OmittedSections;
        if (metadata is not null)
        {
            metadata.OmittedSections = [.. omitted];
        }

        try
        {
            write(FilterOptions(options));
        }
        finally
        {
            if (metadata is not null)
            {
                metadata.OmittedSections = previous;
            }
        }
    }
}
