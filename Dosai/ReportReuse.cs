using System.Text.Json;
using System.Text.Json.Serialization;

namespace Depscan;

/// <summary>
///     Lets <c>crypto</c> consume the <c>methods</c> and <c>dataflows</c> reports a caller has
///     already written for the same tree instead of analysing it again (issue #106). Both
///     cdxgen's research profile and a hand-run sequence call the three commands back to back on
///     one tree, and <c>crypto</c> otherwise rebuilds the methods slice and walks the data flows
///     a second time with the crypto pack.
/// </summary>
/// <remarks>
///     A report is trusted only after it is checked: it must parse, carry a schema version of the
///     major this build writes and this build's analyzer version, name the scan root the command was given (compared normalised,
///     as full paths), and be complete enough for the use (a methods report without
///     <c>--sections</c> omissions; a data-flow report that ran with the <c>crypto</c> pack).
///     A report that fails a check is not used and says so in <c>Diagnostics</c>, and crypto
///     builds the slice or walks the flows itself, so a stale or foreign file never changes what
///     the command reports beyond that line.
/// </remarks>
internal static class ReportReuse
{
    private const string CryptoPack = "crypto";

    /// <summary>The methods sections <c>CryptoReachability.From</c> reads.</summary>
    private static readonly string[] RequiredMethodsSections = ["Methods", "CallGraph", "EntryPoints", "Reachability"];

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>The methods slice in <paramref name="reportPath" />, or null (with a diagnostic) when it cannot stand in for analysing <paramref name="scanPath" />.</summary>
    public static MethodsSlice? LoadMethods(string reportPath, string scanPath, List<string> diagnostics)
    {
        var slice = Read<MethodsSlice>(reportPath, "methods", diagnostics);
        if (slice is null)
        {
            return null;
        }

        if (Reject(slice.Metadata, scanPath) is { } reason)
        {
            diagnostics.Add($"Methods report {reportPath} was not reused: {reason}. The methods slice was built from the source.");
            return null;
        }

        // A report written with --sections (cdxgen's nine) is still complete for crypto when it
        // keeps the four sections its reachability reads.
        if (slice.Metadata?.OmittedSections?.Where(section => RequiredMethodsSections.Contains(section, StringComparer.OrdinalIgnoreCase)).ToList() is { Count: > 0 } omitted)
        {
            diagnostics.Add($"Methods report {reportPath} was not reused: it was written without the sections {string.Join(", ", omitted)}, which crypto reads. The methods slice was built from the source.");
            return null;
        }

        diagnostics.Add($"The methods slice was taken from the report {reportPath} (generated {slice.Metadata?.GeneratedAt:O}) instead of being built again.");
        return slice;
    }

    /// <summary>
    ///     The data-flow report in <paramref name="reportPath" /> restricted to what a walk with
    ///     the crypto pack alone would report, or null (with a diagnostic) when it cannot stand in.
    /// </summary>
    public static DataFlowResult? LoadDataFlows(string reportPath, string scanPath, List<string> diagnostics)
    {
        var report = Read<DataFlowResult>(reportPath, "data-flow", diagnostics);
        if (report is null)
        {
            return null;
        }

        if (Reject(report.Metadata, scanPath) is { } reason)
        {
            diagnostics.Add($"Data-flow report {reportPath} was not reused: {reason}. The crypto data flows were analysed again.");
            return null;
        }

        if (!report.Patterns.PatternPacks.Contains(CryptoPack, StringComparer.OrdinalIgnoreCase))
        {
            diagnostics.Add($"Data-flow report {reportPath} was not reused: it ran with the pattern packs {(report.Patterns.PatternPacks.Count == 0 ? "(none recorded)" : string.Join(", ", report.Patterns.PatternPacks))}, which do not include {CryptoPack}. The crypto data flows were analysed again.");
            return null;
        }

        if (report.Patterns.Sinks.Concat(report.Patterns.Sources).Any(pattern => pattern.Pack == "custom") && report.Slices.Any(slice => slice.Packs?.Contains("custom") == true))
        {
            diagnostics.Add($"Data-flow report {reportPath} was not reused: it ran with custom patterns (--patterns), which the crypto data flows never use. The crypto data flows were analysed again.");
            return null;
        }

        var view = CryptoView(report);
        diagnostics.Add($"The crypto data flows were taken from the data-flow report {reportPath} (generated {report.Metadata.GeneratedAt:O}, pattern packs {string.Join(", ", report.Patterns.PatternPacks)}): {view.Slices.Count} of its {report.Slices.Count} slices are within the crypto pack.");
        return view;
    }

    /// <summary>
    ///     The part of a data-flow report that the crypto pack and the base patterns produce: the
    ///     slices whose packs lie within <c>crypto</c>, the nodes whose matched patterns do (and
    ///     any node such a slice passes through), the edges between kept nodes, and statistics
    ///     recounted for them. Ids are the report's own, so they still name the same slice in the
    ///     report the caller holds. The derived lists (weaknesses, package and dangerous-API
    ///     reachability, exploit chains, attack surface) belong to the full pattern set and are
    ///     not rebuilt for this view.
    /// </summary>
    internal static DataFlowResult CryptoView(DataFlowResult report)
    {
        var packByPattern = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var pattern in report.Patterns.Sources.Concat(report.Patterns.Sinks).Concat(report.Patterns.Passthroughs).Concat(report.Patterns.Sanitizers))
        {
            packByPattern.TryAdd(pattern.Pattern, pattern.Pack);
        }

        bool WithinCrypto(string pattern) => !packByPattern.TryGetValue(pattern, out var pack) || pack is null || pack.Equals(CryptoPack, StringComparison.OrdinalIgnoreCase);

        var slices = report.Slices
            .Where(slice => slice.Packs is null || slice.Packs.All(pack => pack.Equals(CryptoPack, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var keptNodeIds = new HashSet<string>(slices.SelectMany(slice => slice.NodeIds), StringComparer.Ordinal);
        var nodes = report.Nodes.Where(node => keptNodeIds.Contains(node.Id) || node.MatchedPatterns.All(WithinCrypto)).ToList();
        var nodeIds = new HashSet<string>(nodes.Select(node => node.Id), StringComparer.Ordinal);
        var edges = report.Edges.Where(edge => nodeIds.Contains(edge.SourceId) && nodeIds.Contains(edge.TargetId)).ToList();

        var view = new DataFlowResult
        {
            Metadata = report.Metadata,
            EntryPoints = report.EntryPoints,
            Nodes = nodes,
            Edges = edges,
            Slices = slices,
            Patterns = report.Patterns,
            MethodSummaries = report.MethodSummaries,
            Diagnostics = [.. report.Diagnostics]
        };
        view.Statistics.FilesAnalyzed = report.Statistics.FilesAnalyzed;
        view.Statistics.NodeCount = nodes.Count;
        view.Statistics.EdgeCount = edges.Count;
        view.Statistics.SourceCount = nodes.Count(node => node.IsSource);
        view.Statistics.SinkCount = nodes.Count(node => node.IsSink);
        view.Statistics.SliceCount = slices.Count;
        view.Statistics.DependencySliceCount = slices.Count(TransparencyBuilder.IsDependencySlice);
        return view;
    }

    private static T? Read<T>(string reportPath, string kind, List<string> diagnostics) where T : class
    {
        try
        {
            using var stream = new FileStream(reportPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var report = JsonSerializer.Deserialize<T>(stream, Options);
            if (report is null)
            {
                diagnostics.Add($"The {kind} report {reportPath} was not reused: it is empty.");
            }

            return report;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            diagnostics.Add($"The {kind} report {reportPath} was not reused: it could not be read ({ex.Message}).");
            return null;
        }
    }

    /// <summary>Why <paramref name="metadata" /> cannot stand in for a run over <paramref name="scanPath" />, or null.</summary>
    private static string? Reject(AnalysisMetadata? metadata, string scanPath)
    {
        if (metadata is null)
        {
            return "it carries no metadata";
        }

        var current = new AnalysisMetadata().SchemaVersion;
        if (MajorOf(metadata.SchemaVersion) != MajorOf(current))
        {
            return $"its schema version {metadata.SchemaVersion} is not of the major version {MajorOf(current)} this build writes ({current})";
        }

        // The schema says what the fields are; the analyzer version says what fills them. A
        // report from another build can hold different flows or graph facts under the same schema.
        var analyzer = new AnalysisMetadata().AnalyzerVersion;
        if (!string.Equals(metadata.AnalyzerVersion, analyzer, StringComparison.Ordinal))
        {
            return $"it was written by Dosai {metadata.AnalyzerVersion}, not by this build ({analyzer})";
        }

        if (string.IsNullOrWhiteSpace(metadata.InputPath))
        {
            return "it does not name the path it analysed";
        }

        if (!string.Equals(Normalize(metadata.InputPath), Normalize(scanPath), SafeFileRead.PathComparison))
        {
            return $"it analysed {metadata.InputPath}, not {scanPath}";
        }

        return null;
    }

    private static string MajorOf(string version) => version.Split('.', 2)[0];

    private static string Normalize(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }
}
