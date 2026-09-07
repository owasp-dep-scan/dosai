using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Depscan;

/// <summary>One resolved package fact: which source file produced the purl (S1 Evidence).</summary>
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

    private readonly Dictionary<string, string> _assemblyToPurl = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _packageToPurl = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Version, string Source)> _packageVersions = new(StringComparer.OrdinalIgnoreCase);
    // S1: version conflicts collected during the read and aggregated once per package — a
    // multi-project/multi-TFM solution would otherwise emit thousands of duplicate lines.
    private readonly Dictionary<string, List<(string Version, string Source)>> _versionConflicts = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Prefix, string Purl)> _namespacePrefixes = [];

    private PackageUrlResolver()
    {
    }

    /// <summary>S1: per-source resolution facts (which lock/config file produced each purl) for downstream trust decisions.</summary>
    public List<PackageResolutionFact> ResolutionFacts { get; } = [];

    /// <summary>S1: best-effort diagnostics — files consumed, and version conflicts across sources (the THREAT_MODEL ambiguity item).</summary>
    public List<string> Diagnostics { get; } = [];

    public static PackageUrlResolver Create(string path)
    {
        var resolver = new PackageUrlResolver();
        var root = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return resolver;
        }

        // S1: sources in order of trust. Lock files are reproducible, so they win over restore
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

        resolver._namespacePrefixes.AddRange(resolver._packageToPurl.Keys
            .Where(name => name.Contains('.', StringComparison.Ordinal))
            .OrderByDescending(name => name.Length)
            .Select(name => (Prefix: name, Purl: resolver._packageToPurl[name])));
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
            }).ToList();
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

    public string? Resolve(string? assembly = null, string? module = null, string? symbol = null, string? namespaceName = null, string? typeName = null)
    {
        foreach (var candidate in BuildAssemblyCandidates(assembly, module, symbol, typeName))
        {
            if (_assemblyToPurl.TryGetValue(candidate, out var purl) || _packageToPurl.TryGetValue(candidate, out purl))
            {
                return purl;
            }
        }

        var qualifiedName = FirstNonEmpty(symbol, typeName, namespaceName);
        if (!string.IsNullOrWhiteSpace(qualifiedName))
        {
            qualifiedName = NormalizeSymbol(qualifiedName);
            foreach (var (prefix, purl) in _namespacePrefixes)
            {
                if (qualifiedName.Equals(prefix, StringComparison.OrdinalIgnoreCase) || qualifiedName.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase))
                {
                    return purl;
                }
            }

            if (TryResolveSystemPurl(qualifiedName, out var systemPurl))
            {
                return systemPurl;
            }
        }

        return null;
    }

    private static bool TryResolveSystemPurl(string qualifiedName, out string purl)
    {
        foreach (var (prefix, packageName) in SystemPackagePrefixes)
        {
            if (qualifiedName.Equals(prefix, StringComparison.OrdinalIgnoreCase) || qualifiedName.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase))
            {
                purl = $"pkg:nuget/{EscapePurl(packageName)}";
                return true;
            }
        }

        purl = string.Empty;
        return false;
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
    ///     S1: NuGet lock file (packages.lock.json) — the most reproducible source. Version comes
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

    /// <summary>S1: Paket lock file — `NUGET` section lines like `Newtonsoft.Json (13.0.3)`.</summary>
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

    /// <summary>S1: legacy packages.config — direct id/version pairs.</summary>
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
    ///     S1: direct &lt;PackageReference&gt; parsing for unrestored trees. Lowest confidence —
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
        // S1 ambiguity list: when two sources disagree on the version of the same package, record
        // it for the aggregated per-package diagnostic — the purl keeps the first (most-trusted)
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
        var lastSegment = packageName.Split('.').LastOrDefault();
        if (!string.IsNullOrWhiteSpace(lastSegment))
        {
            _packageToPurl.TryAdd(lastSegment, purl);
        }
    }

    private static IEnumerable<string> BuildAssemblyCandidates(string? assembly, string? module, string? symbol, string? typeName)
    {
        foreach (var candidate in new[] { assembly, module })
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var cleaned = candidate.Split(',')[0].Trim();
            cleaned = Path.GetFileNameWithoutExtension(cleaned);
            if (!string.IsNullOrWhiteSpace(cleaned))
            {
                yield return cleaned;
            }
        }

        foreach (var candidate in new[] { symbol, typeName })
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var cleaned = NormalizeSymbol(candidate);
            var first = cleaned.Split('.', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(first))
            {
                yield return first;
            }
        }
    }

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string NormalizeSymbol(string value) => GenericArityRegex().Replace(value.Replace("global::", string.Empty, StringComparison.Ordinal).Replace("Global.", string.Empty, StringComparison.Ordinal), string.Empty);

    private static string BuildNuGetPurl(string name, string version) => $"pkg:nuget/{EscapePurl(name)}@{EscapePurl(version)}";

    private static string EscapePurl(string value) => Uri.EscapeDataString(value).Replace("%2E", ".", StringComparison.Ordinal).Replace("%2D", "-", StringComparison.Ordinal).Replace("%5F", "_", StringComparison.Ordinal);

    [GeneratedRegex("`[0-9]+")]
    private static partial Regex GenericArityRegex();

    [GeneratedRegex(@"^\s+(?<name>[A-Za-z0-9_.\-]+)\s+\((?<version>[^)\s]+)")]
    private static partial Regex PaketPackageLineRegex();
}
