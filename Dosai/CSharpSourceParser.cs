using System.Collections.Generic;
using System.Globalization;
using Microsoft.CodeAnalysis.CSharp;

namespace Depscan;

/// <summary>
///     Preprocessor symbols the analysis treats as defined when interpreting conditional
///     compilation, in C# (parse options) and F# (the line frontend's region tracking) alike.
///     The set is a function of the analyzed project's detected target framework - what the
///     project's own build would define - rather than one hardcoded latest-net set, so a
///     net8.0 project analyzes its `#if NET8_0` bodies and its `#else` arms instead of the
///     net9+/never-compiled branches it used to pick up (issue #56 follow-up).
///     <para>
///     A multi-target project resolves to the define set of one <b>representative</b> target -
///     the most modern one it declares (see <see cref="TrySelectRepresentative" />) - not the
///     union of all of them. A union looks like the "see more rather than less" choice and is
///     the opposite: defining a symbol because <i>some</i> target defines it hides every
///     `#if !SYMBOL` arm, and negated guards are the most common shape in real multi-target
///     libraries. Hangfire.Core (`net451;net46;netstandard1.3;netstandard2.0`) is the worked
///     example - its `#if !NETSTANDARD1_3` members ship in three of its four assemblies, and a
///     union of the four symbol sets erased them from the inventory and the call graph
///     entirely. One representative target keeps every arm that target compiles, which is a
///     real, self-consistent compilation rather than a mix no build produces.
///     </para>
///     <para>
///     The known limitation of a representative: arms exclusive to a <i>lower</i> target
///     (`#if NETFRAMEWORK` in a `net462;net8.0` library) stay invisible, exactly as they were
///     before target-framework detection existed. Seeing them too would mean analyzing each
///     target's parse separately and unioning the resulting members - correct, and deliberately
///     out of scope here.
///     </para>
///     <para>
///     `DEBUG`/`TRACE` stay undefined - a Release-shaped build - in every family. The
///     <see cref="ModernNet" /> set (the historical hardcoded one) remains as the fallback
///     for scan roots with no detectable target framework, so bare-directory scans behave
///     exactly as before. Bump the ceiling together with <c>TargetFramework</c>.
///     </para>
/// </summary>
internal static class FrameworkPreprocessorDefines
{
    public const int LatestModernNetMajor = 11;

    /// <summary>
    ///     The fallback define set when no project file narrows the target: what a build
    ///     against the latest modern .NET defines (`NET`, `NET{Latest}_0`, the
    ///     `NET5_0_OR_GREATER` chain down to .NET 5). Unchanged from the historical hardcoded
    ///     behavior so no-project-file scans do not regress.
    /// </summary>
    public static IReadOnlySet<string> ModernNet { get; } = BuildModernNet();

    private static IReadOnlySet<string> BuildModernNet()
    {
        var symbols = new HashSet<string>(StringComparer.Ordinal) { "NET", $"NET{LatestModernNetMajor}_0" };
        for (var major = 5; major <= LatestModernNetMajor; major++)
        {
            symbols.Add($"NET{major}_0_OR_GREATER");
        }

        return symbols;
    }

    /// <summary>
    ///     The define set of the representative target framework among those detected - see the
    ///     type remarks for why a representative rather than a union. An unparseable or
    ///     unrecognized TFM is skipped; if none is recognized, falls back to
    ///     <see cref="ModernNet" /> so the result is never emptier than before.
    /// </summary>
    public static IReadOnlySet<string> ForTargetFrameworks(IEnumerable<string> targetFrameworks)
        => TrySelectRepresentative(targetFrameworks, out var representative)
            ? ForTargetFramework(representative)
            : ModernNet;

    /// <summary>
    ///     Picks the target framework whose own build compiles the most of a multi-target
    ///     project's modern surface: ordered by family first - modern .NET, then .NET Core,
    ///     then .NET Standard, then .NET Framework - and by version within a family. For
    ///     `net451;net46;netstandard1.3;netstandard2.0` that is `netstandard2.0`; for
    ///     `net462;net8.0;net9.0;net10.0` it is `net10.0`. Ties keep the first declared.
    ///     Returns false when nothing parses as a known family, leaving the caller on
    ///     <see cref="ModernNet" />.
    /// </summary>
    public static bool TrySelectRepresentative(IEnumerable<string> targetFrameworks, out string representative)
    {
        representative = string.Empty;
        var bestRank = (Family: -1, Version: default(FrameworkVersion));
        foreach (var targetFramework in targetFrameworks)
        {
            if (!TryRank(targetFramework, out var rank))
            {
                continue;
            }

            if (rank.Family > bestRank.Family || (rank.Family == bestRank.Family && rank.Version.CompareTo(bestRank.Version) > 0))
            {
                bestRank = rank;
                representative = targetFramework;
            }
        }

        return representative.Length > 0;
    }

    // Family ranks are ordered by how much modern surface a target compiles, not by release
    // date: a netstandard2.0 arm of a legacy multi-target library carries the code its modern
    // consumers actually bind against, while its net46 arm does not.
    private static bool TryRank(string targetFramework, out (int Family, FrameworkVersion Version) rank)
    {
        var normalized = Normalize(targetFramework);
        rank = default;
        if (normalized.StartsWith("netstandard", StringComparison.Ordinal))
        {
            if (!TryParseVersion(normalized["netstandard".Length..], out var version)) return false;
            rank = (2, version);
            return true;
        }

        if (normalized.StartsWith("netcoreapp", StringComparison.Ordinal))
        {
            if (!TryParseVersion(normalized["netcoreapp".Length..], out var version)) return false;
            rank = (3, version);
            return true;
        }

        if (normalized.StartsWith("net", StringComparison.Ordinal))
        {
            if (!TryParseVersion(normalized["net".Length..], out var version)) return false;
            rank = (version.Major >= 5 ? 4 : 1, version);
            return true;
        }

        return false;
    }

    /// <summary>
    ///     The define set one target framework implies, mirroring what the .NET SDK defines:
    ///     <c>net8.0</c> → `NET`, `NET8_0`, `NET5_0_OR_GREATER`..`NET8_0_OR_GREATER`,
    ///     `NETCOREAPP` and its `NETCOREAPP*_OR_GREATER` chain; <c>netstandard2.0</c> →
    ///     `NETSTANDARD`, `NETSTANDARD2_0` and the lower chain; <c>net472</c> →
    ///     `NETFRAMEWORK`, `NET472` and the `NET4x_OR_GREATER` chain. An OS/platform suffix
    ///     (`net8.0-windows`) is stripped for symbol purposes. Unrecognized families yield an
    ///     empty set; the caller's union falls back to <see cref="ModernNet" />.
    /// </summary>
    public static IReadOnlySet<string> ForTargetFramework(string targetFramework)
    {
        var normalized = Normalize(targetFramework);
        var symbols = new HashSet<string>(StringComparer.Ordinal);
        if (normalized.StartsWith("netstandard", StringComparison.Ordinal))
        {
            AddNetStandardSymbols(symbols, normalized["netstandard".Length..]);
        }
        else if (normalized.StartsWith("netcoreapp", StringComparison.Ordinal))
        {
            AddNetCoreAppSymbols(symbols, normalized["netcoreapp".Length..]);
        }
        else if (normalized.StartsWith("net", StringComparison.Ordinal))
        {
            if (!TryParseVersion(normalized["net".Length..], out var version))
            {
                return symbols;
            }

            if (version.Major >= 5)
            {
                AddModernNetSymbols(symbols, version);
            }
            else
            {
                AddFrameworkSymbols(symbols, version);
            }
        }

        return symbols;
    }

    /// <summary>Detects the target frameworks for a scan root and returns its define set, falling back to <see cref="ModernNet" />.</summary>
    public static IReadOnlySet<string> ForRoot(string? rootPath)
    {
        var detected = TargetFrameworkDetection.Detect(rootPath);
        return detected.Count > 0 ? ForTargetFrameworks(detected) : ModernNet;
    }

    /// <summary>Trims, lowercases, and strips the OS/platform suffix (`net8.0-windows` → `net8.0`).</summary>
    private static string Normalize(string targetFramework)
    {
        var normalized = targetFramework.Trim().ToLowerInvariant();
        var platformSuffix = normalized.IndexOf('-', StringComparison.Ordinal);
        return platformSuffix >= 0 ? normalized[..platformSuffix] : normalized;
    }

    private static void AddModernNetSymbols(HashSet<string> symbols, FrameworkVersion version)
    {
        symbols.Add("NET");
        symbols.Add(Symbol("NET", version));
        foreach (var candidate in ModernNetVersions)
        {
            if (candidate.CompareTo(version) <= 0)
            {
                symbols.Add($"NET{candidate.Major}_{candidate.Minor}_OR_GREATER");
            }
        }

        // A modern .NET build also defines the .NET Core compatibility symbols.
        symbols.Add("NETCOREAPP");
        foreach (var candidate in NetCoreAppVersions)
        {
            if (candidate.CompareTo(version) <= 0)
            {
                symbols.Add($"NETCOREAPP{candidate.Major}_{candidate.Minor}_OR_GREATER");
            }
        }
    }

    private static void AddNetCoreAppSymbols(HashSet<string> symbols, string versionText)
    {
        if (!TryParseVersion(versionText, out var version))
        {
            return;
        }

        symbols.Add("NETCOREAPP");
        symbols.Add(Symbol("NETCOREAPP", version));
        foreach (var candidate in NetCoreAppVersions)
        {
            if (candidate.CompareTo(version) <= 0)
            {
                symbols.Add($"NETCOREAPP{candidate.Major}_{candidate.Minor}_OR_GREATER");
            }
        }
    }

    private static void AddNetStandardSymbols(HashSet<string> symbols, string versionText)
    {
        if (!TryParseVersion(versionText, out var version))
        {
            return;
        }

        symbols.Add("NETSTANDARD");
        symbols.Add(Symbol("NETSTANDARD", version));
        foreach (var candidate in NetStandardVersions)
        {
            if (candidate.CompareTo(version) <= 0)
            {
                symbols.Add($"NETSTANDARD{candidate.Major}_{candidate.Minor}_OR_GREATER");
            }
        }
    }

    private static void AddFrameworkSymbols(HashSet<string> symbols, FrameworkVersion version)
    {
        symbols.Add("NETFRAMEWORK");
        symbols.Add(NetFrameworkSymbol(version));
        foreach (var candidate in NetFrameworkVersions)
        {
            if (candidate.CompareTo(version) <= 0)
            {
                symbols.Add($"{NetFrameworkSymbol(candidate)}_OR_GREATER");
            }
        }
    }

    // .NET Framework symbols carry no _0 minor separator (`NET472`, `NET48`), matching MSBuild.
    private static string NetFrameworkSymbol(FrameworkVersion version)
    {
        var digits = version.Minor.ToString(CultureInfo.InvariantCulture);
        if (version.Patch > 0)
        {
            digits += version.Patch.ToString(CultureInfo.InvariantCulture);
        }

        return $"NET{version.Major}{digits}";
    }

    private static string Symbol(string prefix, FrameworkVersion version) => $"{prefix}{version.Major}_{version.Minor}";

    private static bool TryParseVersion(string text, out FrameworkVersion version)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            version = default;
            return false;
        }

        // Dotted form (`8.0`, `2.0`, `4.7.2`).
        if (trimmed.Contains('.', StringComparison.Ordinal))
        {
            return TryParseParts(trimmed.Split('.'), out version);
        }

        // SDK-style digit run. Modern targets are major-only (`net8`, `net11`); the framework,
        // netstandard, and netcoreapp families pack minor (and patch) into the run
        // (`net45` → 4.5, `net472` → 4.7.2, `netcoreapp31` → 3.1).
        if (!trimmed.All(char.IsAsciiDigit))
        {
            version = default;
            return false;
        }

        if (trimmed[0] >= '5')
        {
            if (!int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var major))
            {
                version = default;
                return false;
            }

            version = new FrameworkVersion(major, 0, 0);
            return true;
        }

        var segments = trimmed.Select(digit => digit - '0').ToArray();
        version = new FrameworkVersion(segments[0], segments.Length > 1 ? segments[1] : 0, segments.Length > 2 ? segments[2] : 0);
        return segments.Length <= 3;
    }

    private static bool TryParseParts(string[] parts, out FrameworkVersion version)
    {
        version = default;
        if (parts.Length is < 2 or > 3 || parts.Any(part => part.Length == 0))
        {
            return false;
        }

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minor))
        {
            return false;
        }

        var patch = 0;
        if (parts.Length == 3 && !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out patch))
        {
            return false;
        }

        version = new FrameworkVersion(major, minor, patch);
        return true;
    }

    private static readonly FrameworkVersion[] ModernNetVersions =
    [
        new(5, 0, 0), new(6, 0, 0), new(7, 0, 0), new(8, 0, 0), new(9, 0, 0), new(10, 0, 0), new(11, 0, 0)
    ];
    private static readonly FrameworkVersion[] NetCoreAppVersions =
    [
        new(1, 0, 0), new(1, 1, 0), new(2, 0, 0), new(2, 1, 0), new(2, 2, 0), new(3, 0, 0), new(3, 1, 0)
    ];
    private static readonly FrameworkVersion[] NetStandardVersions =
    [
        new(1, 0, 0), new(1, 1, 0), new(1, 2, 0), new(1, 3, 0), new(1, 4, 0), new(1, 5, 0), new(1, 6, 0),
        new(2, 0, 0), new(2, 1, 0)
    ];
    private static readonly FrameworkVersion[] NetFrameworkVersions =
    [
        new(2, 0, 0), new(3, 0, 0), new(3, 5, 0), new(4, 0, 0), new(4, 5, 0), new(4, 5, 1), new(4, 5, 2),
        new(4, 6, 0), new(4, 6, 1), new(4, 6, 2), new(4, 7, 0), new(4, 7, 1), new(4, 7, 2), new(4, 8, 0),
        new(4, 8, 1)
    ];

    private readonly record struct FrameworkVersion(int Major, int Minor, int Patch) : IComparable<FrameworkVersion>
    {
        public int CompareTo(FrameworkVersion other)
        {
            var major = Major.CompareTo(other.Major);
            if (major != 0) return major;
            var minor = Minor.CompareTo(other.Minor);
            return minor != 0 ? minor : Patch.CompareTo(other.Patch);
        }
    }
}

/// <summary>
///     Central place for building C# syntax trees for analysis. Every analyzer parses through
///     this helper so the accepted language version is decided in one spot.
/// </summary>
/// <remarks>
///     Parsing uses <see cref="LanguageVersion.Preview" />, which is the widest grammar the
///     referenced compiler accepts. Analyzed source is not ours to constrain: a project can
///     target any language version, including one newer than the compiler Dosai references, and
///     source that fails to parse silently disappears from the inventory, call graph, and
///     data-flow results instead of reporting an error. C# 15 union declarations are the current
///     example - the Roslyn 5.9.0 line parses them only under Preview, because its
///     <see cref="LanguageVersion.Default" /> is still C# 14. Preview only widens the accepted
///     grammar, it does not change the meaning of source that already parsed.
/// </remarks>
/// <remarks>
///     The <c>FileBasedProgram</c> parser feature accepts the <c>#:</c> directives of file-based
///     apps (<c>#:property</c>, <c>#:package</c>, <c>#:include</c>, <c>#:sdk</c>, <c>#:project</c>).
///     Without it every directive reports CS9298, and while the surrounding members still parse,
///     the errors surface in any diagnostic the trees feed. The directives become trivia, so
///     parsing a project-based file is unaffected - the feature only stops the compiler from
///     rejecting lines that a file-based app owns.
/// </remarks>
/// <remarks>
///     The parse options define the target-framework-aware preprocessor symbol set (see
///     <see cref="FrameworkPreprocessorDefines" />), resolved per scan root, so `#if NET8_0`-style
///     guards in a net8.0 project analyze as visible code instead of becoming disabled text that
///     no inventory, call graph, or data-flow result ever sees.
/// </remarks>
public static class CSharpSourceParser
{
    private static readonly CSharpParseOptions DefaultParseOptions = BuildParseOptions();

    public static CSharpSyntaxTree Parse(string content, string path, string? rootPath = null) =>
        (CSharpSyntaxTree)CSharpSyntaxTree.ParseText(content, GetParseOptions(rootPath ?? TryGetDirectory(path)), path);

    /// <summary>
    ///     Parse options for a scan root: the language-version and FileBasedProgram decisions
    ///     shared with every root, plus preprocessor symbols from the root's detected target
    ///     frameworks. Cached per root the same way the implicit-usings decision is, so callers
    ///     need no new parameter beyond the root path they already have. Null or empty roots
    ///     get the default (latest modern net) set.
    /// </summary>
    internal static CSharpParseOptions GetParseOptions(string? rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return DefaultParseOptions;
        }

        string root;
        try
        {
            root = Path.GetFullPath(rootPath);
        }
        catch (ArgumentException)
        {
            return DefaultParseOptions;
        }

        lock (ParseOptionsLock)
        {
            if (ParseOptionsByRoot.TryGetValue(root, out var cached))
            {
                return cached;
            }

            var options = BuildParseOptions(TargetFrameworkDetection.Detect(root));
            ParseOptionsByRoot[root] = options;
            return options;
        }
    }

    private static readonly Lock ParseOptionsLock = new();
    private static readonly Dictionary<string, CSharpParseOptions> ParseOptionsByRoot = new(StringComparer.OrdinalIgnoreCase);

    private static CSharpParseOptions BuildParseOptions(IReadOnlyList<string>? detectedTargetFrameworks = null)
    {
        var symbols = detectedTargetFrameworks is { Count: > 0 }
            ? FrameworkPreprocessorDefines.ForTargetFrameworks(detectedTargetFrameworks)
            : FrameworkPreprocessorDefines.ModernNet;
        return new CSharpParseOptions(languageVersion: LanguageVersion.Preview)
            .WithFeatures([new KeyValuePair<string, string>("FileBasedProgram", "true")])
            .WithPreprocessorSymbols(symbols);
    }

    private static string? TryGetDirectory(string path)
    {
        try
        {
            return Path.GetDirectoryName(Path.GetFullPath(path));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    ///     The implicit global usings the .NET SDK adds to every compilation with
    ///     <c>ImplicitUsings</c> enabled. Dosai compiles analyzed source without the project's
    ///     MSBuild context, so without these trees the BCL names an implicit-usings project
    ///     relies on (<c>Path</c>, <c>File</c>, <c>Console</c>, LINQ) fail to bind and every
    ///     such call vanishes from the call graph.
    /// </summary>
    private const string ImplicitGlobalUsingsSource = """
        global using System;
        global using System.Collections.Generic;
        global using System.IO;
        global using System.Linq;
        global using System.Net.Http;
        global using System.Threading;
        global using System.Threading.Tasks;
        """;

    /// <summary>
    ///     A syntax tree carrying the SDK's implicit global usings, for projects under
    ///     <paramref name="path" /> that enable <c>ImplicitUsings</c> (detected from csproj or
    ///     Directory.Build.props/targets). Null when no project enables it, so classic projects
    ///     keep their explicit-usings semantics.
    ///     Granularity limitation: <c>global using</c> directives are compilation-wide in
    ///     Roslyn, and one compilation covers every scanned file, so the decision is per scan
    ///     root — in a mixed monorepo where any project enables ImplicitUsings, files from
    ///     classic sibling projects also receive the synthetic usings, and a BCL name their own
    ///     compiler would reject can bind there. That rare false edge is accepted over the
    ///     alternative (silently dropping every BCL call in the enabling projects, the common
    ///     case); per-file semantics would need one compilation per project.
    /// </summary>
    public static CSharpSyntaxTree? TryCreateImplicitUsingsTree(string? path)
    {
        if (!ImplicitUsingsEnabled(path))
        {
            return null;
        }
        return Parse(ImplicitGlobalUsingsSource, "<implicit-usings>", path);
    }

    // Matches the element with optional attributes (Condition, msbuild metadata), any
    // surrounding whitespace inside the text node, and any case of the value.
    private static readonly System.Text.RegularExpressions.Regex ImplicitUsingsRegex =
        new(@"<ImplicitUsings(?:\s[^>]*)?>\s*enable\s*</ImplicitUsings\s*>", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly System.Threading.Lock DetectionLock = new();
    private static readonly Dictionary<string, bool> EnabledByRoot = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     True when any project file or Directory.Build.props/targets under the path enables
    ///     ImplicitUsings. Absent or disabled stays false: inventing the global usings for a
    ///     classic project would bind calls its own compiler rejects.
    /// </summary>
    private static bool ImplicitUsingsEnabled(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }
        string root;
        try
        {
            root = System.IO.Path.GetFullPath(path);
        }
        catch (ArgumentException)
        {
            return false;
        }
        lock (DetectionLock)
        {
            if (EnabledByRoot.TryGetValue(root, out var cached))
            {
                return cached;
            }
            var enabled = DetectImplicitUsings(root);
            EnabledByRoot[root] = enabled;
            return enabled;
        }
    }

    private static bool DetectImplicitUsings(string root)
    {
        try
        {
            foreach (var projectFile in SafeFileRead.EnumerateAllFilesSafe(root, "*.csproj"))
            {
                if (ProjectEnablesImplicitUsings(projectFile))
                {
                    return true;
                }
            }
            foreach (var buildProps in SafeFileRead.EnumerateAllFilesSafe(root, "Directory.Build.props")
                         .Concat(SafeFileRead.EnumerateAllFilesSafe(root, "Directory.Build.targets")))
            {
                if (ProjectEnablesImplicitUsings(buildProps))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Best-effort detection; analysis continues with explicit-usings semantics.
        }
        return false;
    }

    private static bool ProjectEnablesImplicitUsings(string projectFile)
    {
        try
        {
            return ImplicitUsingsRegex.IsMatch(File.ReadAllText(projectFile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
