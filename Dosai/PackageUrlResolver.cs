using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Depscan;

/// <summary>One resolved package fact: which source file produced the purl.</summary>
public sealed record PackageResolutionFact(string Name, string Version, string Purl, string Source, string Confidence);

public sealed partial class PackageUrlResolver
{
    private static readonly (string Prefix, string PackageName)[] SystemPackagePrefixes =
    [
        ("System.Diagnostics.Process", "System.Diagnostics.Process"),
        ("System.IO.FileStream", "System.IO.FileSystem"),
        ("System.IO.File", "System.IO.FileSystem"),
        ("System.IO.Directory", "System.IO.FileSystem"),
        ("System.IO.Path", "System.IO.FileSystem"),
        ("System.Net.Http", "System.Net.Http"),
        ("System.Net.WebUtility", "System.Net.WebUtility"),
        ("System.Reflection.Assembly", "System.Reflection"),
        ("System.Security.Cryptography.X509Certificates", "System.Security.Cryptography.X509Certificates"),
        ("System.Security.Cryptography", "System.Security.Cryptography.Algorithms"),
        ("System.Text.Json", "System.Text.Json"),
        ("System.Text.RegularExpressions", "System.Text.RegularExpressions"),
        ("System.Console", "System.Console"),
        ("System.Uri", "System.Runtime"),
        ("System.Type", "System.Runtime"),
        ("System.String", "System.Runtime"),
        ("System.Collections", "System.Collections"),
        ("System.Linq", "System.Linq"),
        ("System.Threading.Tasks", "System.Threading.Tasks"),
        ("System.Threading", "System.Threading"),
        ("System", "System.Runtime")
    ];

    /// <summary>
    ///     <see cref="SystemPackagePrefixes" /> by prefix, with each entry's list position (the
    ///     first matching entry wins) and its purl built once.
    /// </summary>
    private static readonly Dictionary<string, (int Order, string Purl)> SystemPackagePurls = SystemPackagePrefixes
        .Select((entry, order) => (entry.Prefix, Order: order, Purl: $"pkg:nuget/{EscapePurl(entry.PackageName)}"))
        .ToDictionary(entry => entry.Prefix, entry => (entry.Order, entry.Purl), StringComparer.OrdinalIgnoreCase);

    /// <summary><see cref="SystemPackagePurls" /> probed by span; no prefix longer than its longest key can match.</summary>
    private static readonly Dictionary<string, (int Order, string Purl)>.AlternateLookup<ReadOnlySpan<char>> SystemPackagesBySpan = SystemPackagePurls.GetAlternateLookup<ReadOnlySpan<char>>();

    private static readonly int LongestSystemPrefix = SystemPackagePurls.Keys.Max(key => key.Length);

    private readonly Dictionary<string, string> _assemblyToPurl = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _packageToPurl = new(StringComparer.OrdinalIgnoreCase);

    // The tables probed by span, so a probe never cuts a substring; and the longest package name
    // registered, past which no dotted prefix can match one.
    private readonly Dictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> _assembliesBySpan;
    private readonly Dictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> _packagesBySpan;
    private int _longestPackageName;

    private readonly Dictionary<string, (string Version, string Source)> _packageVersions = new(StringComparer.OrdinalIgnoreCase);
    // Version conflicts collected during the read and aggregated once per package, a
    // multi-project/multi-TFM solution would otherwise emit thousands of duplicate lines.
    private readonly Dictionary<string, List<(string Version, string Source)>> _versionConflicts = new(StringComparer.OrdinalIgnoreCase);

    private PackageUrlResolver()
    {
        _assembliesBySpan = _assemblyToPurl.GetAlternateLookup<ReadOnlySpan<char>>();
        _packagesBySpan = _packageToPurl.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    /// <summary>Per-source resolution facts (which lock/config file produced each purl) for downstream trust decisions.</summary>
    public List<PackageResolutionFact> ResolutionFacts { get; } = [];

    /// <summary>Best-effort diagnostics, files consumed, and version conflicts across sources (see docs/THREAT_MODEL.md).</summary>
    public List<string> Diagnostics { get; } = [];

    public static PackageUrlResolver Create(string path)
    {
        var resolver = new PackageUrlResolver();
        var root = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return resolver;
        }

        // Sources in order of trust. Lock files are reproducible, so they win over restore
        // outputs; project-file references are lowest (versions may be floating or absent).
        foreach (var lockFile in SafeEnumerateFiles(root, "packages.lock.json"))
        {
            resolver.ReadPackagesLockJson(lockFile);
        }

        foreach (var paketLock in SafeEnumerateFiles(root, "paket.lock"))
        {
            resolver.ReadPaketLock(paketLock);
        }

        foreach (var packagesConfig in SafeEnumerateFiles(root, "packages.config"))
        {
            resolver.ReadPackagesConfig(packagesConfig);
        }

        foreach (var assetsFile in SafeEnumerateFiles(root, "project.assets.json"))
        {
            resolver.ReadProjectAssets(assetsFile);
        }

        foreach (var depsFile in SafeEnumerateFiles(root, "*.deps.json"))
        {
            resolver.ReadDepsJson(depsFile);
        }

        foreach (var projectFile in SafeEnumerateFiles(root, "*.csproj").Concat(SafeEnumerateFiles(root, "*.vbproj")).Concat(SafeEnumerateFiles(root, "*.fsproj")))
        {
            resolver.ReadProjectReferences(projectFile);
        }

        // One ambiguity line per package, listing every disagreeing source.
        foreach (var (packageName, conflicts) in resolver._versionConflicts.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            var detail = string.Join(", ", conflicts.Distinct().Select(conflict => $"{conflict.Source} says {conflict.Version}"));
            resolver.Diagnostics.Add($"PURL version ambiguity for {packageName}: {detail}; keeping {resolver._packageVersions.GetValueOrDefault(packageName).Version}.");
        }

        return resolver;
    }

    private static IEnumerable<string> SafeEnumerateFiles(string root, string searchPattern)
    {
        try
        {
            return Directory.EnumerateFiles(root, searchPattern, new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            }).Where(file => !PathExclusions.IsExcluded(file, isDirectory: false)).ToList();
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
        catch (ArgumentException)
        {
            return [];
        }
    }

    /// <remarks>
    ///     Runs for every method, call, node and edge of a scan, millions of times on a large tree,
    ///     so it allocates nothing it does not return: the candidate names are cut out of the
    ///     inputs directly (no split of the whole symbol on every dot), and the namespace-prefix
    ///     tables are probed with the qualified name's own dotted prefixes instead of walking every
    ///     known package with a concatenated <c>prefix + "."</c> per entry.
    /// </remarks>
    public string? Resolve(string? assembly = null, string? module = null, string? symbol = null, string? namespaceName = null, string? typeName = null)
    {
        string? normalizedSymbol = null;
        if (TryResolveCandidate(AssemblyCandidate(assembly), out var purl)
            || TryResolveCandidate(AssemblyCandidate(module), out purl)
            || TryResolveCandidate(SymbolCandidate(symbol, out normalizedSymbol), out purl)
            || TryResolveCandidate(SymbolCandidate(typeName, out _), out purl))
        {
            return purl;
        }


        var qualifiedName = !string.IsNullOrWhiteSpace(symbol) ? symbol : !string.IsNullOrWhiteSpace(typeName) ? typeName : namespaceName;
        if (!string.IsNullOrWhiteSpace(qualifiedName))
        {
            qualifiedName = ReferenceEquals(qualifiedName, symbol) && normalizedSymbol is not null ? normalizedSymbol : NormalizeSymbol(qualifiedName);
            // The longest matching package prefix wins (the prefixes are ordered by length, and
            // two names of one length cannot both prefix the same name); a package name is a
            // prefix when the qualified name equals it or continues it with a dot.
            for (var end = qualifiedName.Length; end > 0; end = qualifiedName.LastIndexOf('.', end - 1))
            {
                var prefix = qualifiedName.AsSpan(0, end);
                if (end <= _longestPackageName && prefix.Contains('.') && _packagesBySpan.TryGetValue(prefix, out var packagePurl))
                {
                    return packagePurl;
                }
            }

            if (TryResolveSystemPurl(qualifiedName, out var systemPurl))
            {
                return systemPurl;
            }
        }

        return null;
    }

    /// <summary>
    ///     Candidates are spans into the caller's strings and probe the tables through their span
    ///     lookups: <see cref="Resolve" /> runs for every method, call, node and edge, and a
    ///     substring per probe was most of its allocation. An empty span is no candidate.
    /// </summary>
    private bool TryResolveCandidate(ReadOnlySpan<char> candidate, out string? purl)
    {
        purl = null;
        return !candidate.IsEmpty && (_assembliesBySpan.TryGetValue(candidate, out purl) || _packagesBySpan.TryGetValue(candidate, out purl));
    }

    /// <summary>An assembly or module name without its display-name tail and file extension; empty when none is left.</summary>
    private static ReadOnlySpan<char> AssemblyCandidate(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return [];
        }

        var comma = candidate.IndexOf(',', StringComparison.Ordinal);
        var cleaned = Path.GetFileNameWithoutExtension((comma >= 0 ? candidate.AsSpan(0, comma) : candidate).Trim());
        return cleaned.IsWhiteSpace() ? [] : cleaned;
    }

    /// <summary>The first non-empty dot-separated segment of a normalized symbol; empty when there is none.</summary>
    private static ReadOnlySpan<char> SymbolCandidate(string? candidate, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return [];
        }

        normalized = NormalizeSymbol(candidate);
        var start = 0;
        while (start < normalized.Length && normalized[start] == '.')
        {
            start++;
        }

        if (start == normalized.Length)
        {
            return [];
        }

        var end = normalized.IndexOf('.', start);
        var first = end < 0 ? normalized.AsSpan(start) : normalized.AsSpan(start, end - start);
        return first.IsWhiteSpace() ? [] : first;
    }

    /// <summary>The first <see cref="SystemPackagePrefixes" /> entry the name equals or continues with a dot.</summary>
    private static bool TryResolveSystemPurl(string qualifiedName, out string purl)
    {
        var best = int.MaxValue;
        purl = string.Empty;
        for (var end = qualifiedName.Length; end > 0; end = qualifiedName.LastIndexOf('.', end - 1))
        {
            if (end <= LongestSystemPrefix && SystemPackagesBySpan.TryGetValue(qualifiedName.AsSpan(0, end), out var entry) && entry.Order < best)
            {
                best = entry.Order;
                purl = entry.Purl;
            }
        }

        return best != int.MaxValue;
    }

    private void ReadProjectAssets(string filePath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(filePath));
            if (!document.RootElement.TryGetProperty("libraries", out var libraries) || libraries.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var packagePurls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var library in libraries.EnumerateObject())
            {
                var parts = library.Name.Split('/', 2);
                if (parts.Length != 2)
                {
                    continue;
                }

                var type = library.Value.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
                if (!string.Equals(type, "package", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var purl = BuildNuGetPurl(parts[0], parts[1]);
                packagePurls[library.Name] = purl;
                AddPackage(parts[0], parts[1], "project.assets.json", purl, "medium");
            }

            if (!document.RootElement.TryGetProperty("targets", out var targets) || targets.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var target in targets.EnumerateObject())
            {
                foreach (var library in target.Value.EnumerateObject())
                {
                    if (!packagePurls.TryGetValue(library.Name, out var purl))
                    {
                        continue;
                    }

                    AddAssets(library.Value, "compile", purl);
                    AddAssets(library.Value, "runtime", purl);
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Resolver is best-effort and should never fail analysis.
        }
    }

    private void ReadDepsJson(string filePath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(filePath));
            if (!document.RootElement.TryGetProperty("libraries", out var libraries) || libraries.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var packagePurls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var library in libraries.EnumerateObject())
            {
                var parts = library.Name.Split('/', 2);
                if (parts.Length != 2)
                {
                    continue;
                }

                var type = library.Value.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
                if (!string.Equals(type, "package", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var purl = BuildNuGetPurl(parts[0], parts[1]);
                packagePurls[library.Name] = purl;
                AddPackage(parts[0], parts[1], "*.deps.json", purl, "medium");
            }

            if (!document.RootElement.TryGetProperty("targets", out var targets) || targets.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var target in targets.EnumerateObject())
            {
                foreach (var library in target.Value.EnumerateObject())
                {
                    if (!packagePurls.TryGetValue(library.Name, out var purl))
                    {
                        continue;
                    }

                    AddAssets(library.Value, "runtime", purl);
                    AddAssets(library.Value, "compile", purl);
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Resolver is best-effort and should never fail analysis.
        }
    }

    /// <summary>
    ///     NuGet lock file (packages.lock.json), the most reproducible source. Version comes
    ///     from the per-framework "resolved" pin.
    /// </summary>
    private void ReadPackagesLockJson(string filePath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(filePath));
            if (!document.RootElement.TryGetProperty("dependencies", out var frameworks) || frameworks.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var count = 0;
            foreach (var framework in frameworks.EnumerateObject())
            {
                if (framework.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var package in framework.Value.EnumerateObject())
                {
                    var version = package.Value.TryGetProperty("resolved", out var resolved) ? resolved.GetString() : null;
                    if (string.IsNullOrWhiteSpace(version))
                    {
                        continue;
                    }

                    AddPackage(package.Name, version!, "packages.lock.json", BuildNuGetPurl(package.Name, version!), "high");
                    count++;
                }
            }

            if (count > 0)
            {
                Diagnostics.Add($"Resolved {count} package purl(s) from lock file {Path.GetFileName(Path.GetDirectoryName(filePath))}/{Path.GetFileName(filePath)}.");
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Resolver is best-effort and should never fail analysis.
        }
    }

    /// <summary>Paket lock file, `NUGET` section lines like `Newtonsoft.Json (13.0.3)`.</summary>
    private void ReadPaketLock(string filePath)
    {
        try
        {
            var count = 0;
            var inNugetSection = false;
            foreach (var line in File.ReadLines(filePath))
            {
                if (line.Length == 0 || line[0] != ' ')
                {
                    inNugetSection = line.TrimEnd().Equals("NUGET", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inNugetSection)
                {
                    continue;
                }

                var match = PaketPackageLineRegex().Match(line);
                if (!match.Success)
                {
                    continue;
                }

                var name = match.Groups["name"].Value;
                var version = match.Groups["version"].Value;
                if (string.IsNullOrWhiteSpace(version))
                {
                    continue;
                }

                AddPackage(name, version, "paket.lock", BuildNuGetPurl(name, version), "high");
                count++;
            }

            if (count > 0)
            {
                Diagnostics.Add($"Resolved {count} package purl(s) from paket.lock {Path.GetFileName(filePath)}.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Resolver is best-effort and should never fail analysis.
        }
    }

    /// <summary>Legacy packages.config, direct id/version pairs.</summary>
    private void ReadPackagesConfig(string filePath)
    {
        try
        {
            var document = XDocument.Load(filePath);
            var count = 0;
            foreach (var package in document.Descendants("package"))
            {
                var name = package.Attribute("id")?.Value;
                var version = package.Attribute("version")?.Value;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(version))
                {
                    continue;
                }

                AddPackage(name!, version!, "packages.config", BuildNuGetPurl(name!, version!), "high");
                count++;
            }

            if (count > 0)
            {
                Diagnostics.Add($"Resolved {count} package purl(s) from packages.config {Path.GetFileName(filePath)}.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            // Resolver is best-effort and should never fail analysis.
        }
    }

    /// <summary>
    ///     Direct &lt;PackageReference&gt; parsing for unrestored trees. Lowest confidence:
    ///     versions may be absent (floating) or overridden by a lock file that is read first.
    /// </summary>
    private void ReadProjectReferences(string filePath)
    {
        try
        {
            var document = XDocument.Load(filePath);
            var count = 0;
            foreach (var reference in document.Descendants("PackageReference"))
            {
                var name = reference.Attribute("Include")?.Value ?? reference.Attribute("Update")?.Value;
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var version = reference.Attribute("Version")?.Value;
                var purl = string.IsNullOrWhiteSpace(version) ? $"pkg:nuget/{EscapePurl(name!)}" : BuildNuGetPurl(name!, version!);
                AddPackage(name!, version ?? string.Empty, Path.GetExtension(filePath).TrimStart('.'), purl, string.IsNullOrWhiteSpace(version) ? "low" : "medium");
                count++;
            }

            if (count > 0)
            {
                Diagnostics.Add($"Resolved {count} package purl(s) from project references in {Path.GetFileName(filePath)}.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            // Resolver is best-effort and should never fail analysis.
        }
    }

    private void AddAssets(JsonElement libraryElement, string propertyName, string purl)
    {
        if (!libraryElement.TryGetProperty(propertyName, out var assets) || assets.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var asset in assets.EnumerateObject())
        {
            var assemblyName = Path.GetFileNameWithoutExtension(asset.Name.Replace('/', Path.DirectorySeparatorChar));
            if (!string.IsNullOrWhiteSpace(assemblyName))
            {
                _assemblyToPurl.TryAdd(assemblyName, purl);
            }
        }
    }

    private void AddPackage(string packageName, string version, string source, string purl, string confidence)
    {
        // Ambiguity list: when two sources disagree on the version of the same package, record
        // it for the aggregated per-package diagnostic; the purl keeps the first (most-trusted)
        // source's answer.
        if (_packageVersions.TryGetValue(packageName, out var existing) && !string.IsNullOrEmpty(existing.Version) && !string.IsNullOrEmpty(version) && !string.Equals(existing.Version, version, StringComparison.OrdinalIgnoreCase))
        {
            if (!_versionConflicts.TryGetValue(packageName, out var conflicts))
            {
                conflicts = [];
                _versionConflicts[packageName] = conflicts;
            }

            conflicts.Add((version, source));
        }
        else if (!string.IsNullOrEmpty(version))
        {
            _packageVersions.TryAdd(packageName, (version, source));
        }

        ResolutionFacts.Add(new PackageResolutionFact(packageName, version, purl, source, confidence));
        _packageToPurl.TryAdd(packageName, purl);
        _longestPackageName = Math.Max(_longestPackageName, packageName.Length);
        var lastSegment = packageName.Split('.').LastOrDefault();
        if (!string.IsNullOrWhiteSpace(lastSegment))
        {
            _packageToPurl.TryAdd(lastSegment, purl);
        }
    }

    /// <summary>
    ///     The symbol without <c>global::</c>/<c>Global.</c> qualifiers and generic arity markers
    ///     (a backtick and the ASCII digits after it, as the <c>`[0-9]+</c> pattern this replaces
    ///     matched them). The input itself when there is nothing to strip, which is most symbols:
    ///     the regex pass cost more than the lookups it prepares.
    /// </summary>
    private static string NormalizeSymbol(string value)
    {
        var stripped = value.Replace("global::", string.Empty, StringComparison.Ordinal).Replace("Global.", string.Empty, StringComparison.Ordinal);
        var tick = stripped.IndexOf('`', StringComparison.Ordinal);
        if (tick < 0)
        {
            return stripped;
        }

        var builder = new StringBuilder(stripped.Length);
        builder.Append(stripped, 0, tick);
        for (var index = tick; index < stripped.Length;)
        {
            if (stripped[index] == '`' && index + 1 < stripped.Length && char.IsAsciiDigit(stripped[index + 1]))
            {
                index += 2;
                while (index < stripped.Length && char.IsAsciiDigit(stripped[index]))
                {
                    index++;
                }

                continue;
            }

            builder.Append(stripped[index++]);
        }

        return builder.ToString();
    }

    private static string BuildNuGetPurl(string name, string version) => $"pkg:nuget/{EscapePurl(name)}@{EscapePurl(version)}";

    private static string EscapePurl(string value) => Uri.EscapeDataString(value).Replace("%2E", ".", StringComparison.Ordinal).Replace("%2D", "-", StringComparison.Ordinal).Replace("%5F", "_", StringComparison.Ordinal);

    [GeneratedRegex(@"^\s+(?<name>[A-Za-z0-9_.\-]+)\s+\((?<version>[^)\s]+)")]
    private static partial Regex PaketPackageLineRegex();
}
