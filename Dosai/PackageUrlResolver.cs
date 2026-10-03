using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Depscan;

/// <summary>
///     One resolved package fact: which kind of source produced the purl, and for which project
///     (the project directory relative to the scan root, <c>.</c> for the root itself; null for a
///     source outside every project, such as a solution-level <c>paket.lock</c>).
/// </summary>
public sealed record PackageResolutionFact(string Name, string Version, string Purl, string Source, string Confidence, string? Project = null);

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

    /// <summary>
    ///     The package tables of one resolution scope - the whole tree, one project, or the sources
    ///     outside every project - with the first answer winning inside the scope, in the order the
    ///     sources are read (most trusted first).
    /// </summary>
    private sealed class PackageTables
    {
        private readonly Dictionary<string, string> _assemblyToPurl = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _packageToPurl = new(StringComparer.OrdinalIgnoreCase);

        // The tables probed by span, so a probe never cuts a substring; and the longest package
        // name registered, past which no dotted prefix can match one.
        private readonly Dictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> _assembliesBySpan;
        private readonly Dictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> _packagesBySpan;
        private int _longestPackageName;

        public PackageTables(string? label)
        {
            Label = label;
            _assembliesBySpan = _assemblyToPurl.GetAlternateLookup<ReadOnlySpan<char>>();
            _packagesBySpan = _packageToPurl.GetAlternateLookup<ReadOnlySpan<char>>();
        }

        /// <summary>The project directory relative to the scan root, or null for the tree-wide and outside-every-project tables.</summary>
        public string? Label { get; }

        /// <summary>The version each package resolves to in this scope, and the source that gave it.</summary>
        public Dictionary<string, (string Version, string Source)> PackageVersions { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Versions this scope's other sources gave a package, against <see cref="PackageVersions" />.</summary>
        public Dictionary<string, List<(string Version, string Source)>> VersionConflicts { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void AddPackage(string packageName, string version, string source, string purl)
        {
            // Two sources of one scope disagreeing on a version: the purl keeps the first (most
            // trusted) source's answer, and the other answers are collected for one diagnostic.
            if (PackageVersions.TryGetValue(packageName, out var existing) && !string.IsNullOrEmpty(existing.Version) && !string.IsNullOrEmpty(version) && !string.Equals(existing.Version, version, StringComparison.OrdinalIgnoreCase))
            {
                if (!VersionConflicts.TryGetValue(packageName, out var conflicts))
                {
                    conflicts = [];
                    VersionConflicts[packageName] = conflicts;
                }

                conflicts.Add((version, source));
            }
            else if (!string.IsNullOrEmpty(version))
            {
                PackageVersions.TryAdd(packageName, (version, source));
            }

            _packageToPurl.TryAdd(packageName, purl);
            _longestPackageName = Math.Max(_longestPackageName, packageName.Length);
            var lastSegment = packageName.Split('.').LastOrDefault();
            if (!string.IsNullOrWhiteSpace(lastSegment))
            {
                _packageToPurl.TryAdd(lastSegment, purl);
            }
        }

        public void AddAssembly(string assemblyName, string purl) => _assemblyToPurl.TryAdd(assemblyName, purl);

        public bool TryGetCandidate(ReadOnlySpan<char> candidate, out string? purl) => _assembliesBySpan.TryGetValue(candidate, out purl) || _packagesBySpan.TryGetValue(candidate, out purl);

        public bool TryGetPackage(ReadOnlySpan<char> name, out string? purl)
        {
            purl = null;
            return name.Length <= _longestPackageName && _packagesBySpan.TryGetValue(name, out purl);
        }
    }

    // Every source feeds the tree-wide tables (the answer for a record outside any project with
    // package sources, and for records shared by projects, such as an external call-graph node)
    // and the tables of the project it belongs to. A record inside a project resolves against
    // that project's tables first, step by step, so two projects restoring two versions of one
    // package each keep their own version (issue #72).
    private readonly PackageTables _tree = new(null);
    private readonly PackageTables _outsideProjects = new(null);
    private readonly Dictionary<string, PackageTables> _projectTables = new(SafeFileRead.PathComparer);
    // Restore-output directories and the project each one named, for an artifacts layout's
    // bin/<project>/ build output, which has no project file above it.
    private readonly Dictionary<string, PackageTables> _tablesByAssetsDirectory = new(SafeFileRead.PathComparer);
    private readonly HashSet<string> _projectDirectories = new(SafeFileRead.PathComparer);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, PackageTables?> _tablesByLocation = new(StringComparer.Ordinal);
    private string _root = string.Empty;

    private PackageUrlResolver()
    {
    }

    /// <summary>Per-source resolution facts (which lock/config file produced each purl, for which project) for downstream trust decisions.</summary>
    public List<PackageResolutionFact> ResolutionFacts { get; } = [];

    /// <summary>Best-effort diagnostics: files consumed, then <see cref="VersionDiagnostics" /> (see docs/THREAT_MODEL.md).</summary>
    public List<string> Diagnostics { get; } = [];

    /// <summary>
    ///     Packages that resolve to different versions in different projects, and sources of one
    ///     project that disagree on a version. Every report carries these.
    /// </summary>
    public List<string> VersionDiagnostics { get; } = [];

    public static PackageUrlResolver Create(string path)
    {
        var resolver = new PackageUrlResolver();
        var root = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return resolver;
        }

        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        resolver._root = root;
        var projectFiles = SafeEnumerateFiles(root, "*.csproj").Concat(SafeEnumerateFiles(root, "*.vbproj")).Concat(SafeEnumerateFiles(root, "*.fsproj")).ToList();
        foreach (var projectFile in projectFiles)
        {
            if (Path.GetDirectoryName(projectFile) is { } projectDirectory)
            {
                resolver._projectDirectories.Add(projectDirectory);
            }
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

        foreach (var projectFile in projectFiles)
        {
            resolver.ReadProjectReferences(projectFile);
        }

        resolver.AddVersionDiagnostics();
        resolver.Diagnostics.AddRange(resolver.VersionDiagnostics);
        return resolver;
    }

    /// <summary>
    ///     One line per package that resolves to more than one version across projects - each
    ///     project's records carry its own version, which a reader of a single purl would not
    ///     guess - and one per package whose sources disagree inside one project (the purl keeps
    ///     the most-trusted source's answer). Per-project lines name at most
    ///     <see cref="MaxProjectsPerVersion" /> projects per version.
    /// </summary>
    private void AddVersionDiagnostics()
    {
        var scopes = _projectTables.Values.OrderBy(tables => tables.Label, StringComparer.Ordinal).Append(_outsideProjects).ToList();
        var packageNames = scopes.SelectMany(tables => tables.PackageVersions.Keys).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
        foreach (var packageName in packageNames)
        {
            var versions = scopes
                .Where(tables => tables.PackageVersions.ContainsKey(packageName))
                .GroupBy(tables => tables.PackageVersions[packageName].Version, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (versions.Count < 2)
            {
                continue;
            }

            var detail = string.Join(", ", versions.Select(group =>
            {
                var labels = group.Select(tables => tables.Label ?? "outside any project").ToList();
                var named = string.Join(", ", labels.Take(MaxProjectsPerVersion));
                return labels.Count > MaxProjectsPerVersion
                    ? string.Create(CultureInfo.InvariantCulture, $"{group.Key} ({named} and {labels.Count - MaxProjectsPerVersion} more)")
                    : $"{group.Key} ({named})";
            }));
            VersionDiagnostics.Add($"Package {packageName} resolves to {versions.Count.ToString(CultureInfo.InvariantCulture)} versions across projects: {detail}. Each project's records carry its own version; records outside those projects, and call-graph nodes shared by them, carry {_tree.PackageVersions.GetValueOrDefault(packageName).Version}.");
        }

        foreach (var tables in scopes)
        {
            foreach (var (packageName, conflicts) in tables.VersionConflicts.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                var detail = string.Join(", ", conflicts.Distinct().Select(conflict => $"{conflict.Source} says {conflict.Version}"));
                var kept = tables.PackageVersions.GetValueOrDefault(packageName);
                VersionDiagnostics.Add($"PURL version ambiguity for {packageName} in {tables.Label ?? "sources outside any project"}: {detail}; keeping {kept.Version} from {kept.Source}.");
            }
        }
    }

    private const int MaxProjectsPerVersion = 10;

    /// <summary>
    ///     The tables of the project a source file belongs to: the project its restore output names
    ///     (when that project is in the tree), else the nearest directory at or above the file that
    ///     holds a project file; the outside-every-project tables when there is none.
    /// </summary>
    private PackageTables TablesForSource(string filePath, string? projectPath = null)
    {
        string? directory = null;
        if (!string.IsNullOrWhiteSpace(projectPath))
        {
            try
            {
                var named = Path.GetDirectoryName(Path.GetFullPath(projectPath));
                directory = named is not null && _projectDirectories.Contains(named) ? named : null;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                directory = null;
            }
        }

        directory ??= NearestProjectDirectory(Path.GetDirectoryName(filePath));
        if (directory is null)
        {
            return _outsideProjects;
        }

        if (!_projectTables.TryGetValue(directory, out var tables))
        {
            var relative = Path.GetRelativePath(_root, directory).Replace('\\', '/');
            tables = new PackageTables(relative);
            _projectTables[directory] = tables;
        }

        return tables;
    }

    /// <summary>
    ///     For build output in an artifacts layout (<c>artifacts/bin/&lt;project&gt;/...</c>), the
    ///     restore-output directory beside it (<c>artifacts/obj/&lt;project&gt;</c>), whose
    ///     <c>project.assets.json</c> names the project; null for any other path.
    /// </summary>
    private string? ArtifactsRestoreDirectory(string filePath)
    {
        var segments = Path.GetRelativePath(_root, filePath).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (var index = segments.Length - 3; index >= 0; index--)
        {
            if (string.Equals(segments[index], "bin", StringComparison.OrdinalIgnoreCase))
            {
                return Path.Combine([_root, .. segments[..index], "obj", segments[index + 1]]);
            }
        }

        return null;
    }

    /// <summary>The nearest directory at or above <paramref name="directory" />, within the scan root, that holds a project file.</summary>
    private string? NearestProjectDirectory(string? directory)
    {
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        for (var current = directory; current is not null; current = Path.GetDirectoryName(current))
        {
            if (_projectDirectories.Contains(current))
            {
                return current;
            }

            if (string.Equals(current, _root, comparison) || !current.StartsWith(_root, comparison))
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>Whether a record at <paramref name="location" /> resolves against its own project's tables.</summary>
    public bool IsProjectScoped(string? location) => TablesForLocation(location) is not null;

    /// <summary>
    ///     The project tables for a record's file (a path relative to the scan root, or absolute),
    ///     memoized per path: null when the tree has no project-scoped sources, the record has no
    ///     file, or its project has no package sources of its own.
    /// </summary>
    private PackageTables? TablesForLocation(string? location)
    {
        if (_projectTables.Count == 0 || string.IsNullOrWhiteSpace(location))
        {
            return null;
        }

        return _tablesByLocation.GetOrAdd(location, static (key, resolver) =>
        {
            try
            {
                var fullPath = Path.GetFullPath(Path.IsPathFullyQualified(key) ? key : Path.Combine(resolver._root, key));
                var directory = resolver.NearestProjectDirectory(Path.GetDirectoryName(fullPath));
                return directory is not null && resolver._projectTables.TryGetValue(directory, out var tables) ? tables : null;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return null;
            }
        }, this);
    }

    private static IEnumerable<string> SafeEnumerateFiles(string root, string searchPattern)
    {
        try
        {
            // Sorted: within a scope the first source read wins, and the file system's enumeration
            // order must not decide which one that is.
            return Directory.EnumerateFiles(root, searchPattern, new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            }).Where(file => !PathExclusions.IsExcluded(file, isDirectory: false)).Order(StringComparer.Ordinal).ToList();
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

    /// <param name="location">
    ///     The record's file, relative to the scan root or absolute: a record inside a project
    ///     with package sources resolves against that project's tables first, at every step, so it
    ///     carries the version its own project restores.
    /// </param>
    /// <remarks>
    ///     Runs for every method, call, node and edge of a scan, millions of times on a large tree,
    ///     so it allocates nothing it does not return: the candidate names are cut out of the
    ///     inputs directly (no split of the whole symbol on every dot), and the namespace-prefix
    ///     tables are probed with the qualified name's own dotted prefixes instead of walking every
    ///     known package with a concatenated <c>prefix + "."</c> per entry. It only reads the
    ///     tables (the per-location memo is concurrent), so enrichment calls it from many threads.
    /// </remarks>
    public string? Resolve(string? assembly = null, string? module = null, string? symbol = null, string? namespaceName = null, string? typeName = null, string? location = null)
    {
        var project = TablesForLocation(location);
        string? normalizedSymbol = null;
        if (TryResolveCandidate(project, AssemblyCandidate(assembly), out var purl)
            || TryResolveCandidate(project, AssemblyCandidate(module), out purl)
            || TryResolveCandidate(project, SymbolCandidate(symbol, out normalizedSymbol), out purl)
            || TryResolveCandidate(project, SymbolCandidate(typeName, out _), out purl))
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
                if (prefix.Contains('.') && (project is not null && project.TryGetPackage(prefix, out var packagePurl) || _tree.TryGetPackage(prefix, out packagePurl)))
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
    private bool TryResolveCandidate(PackageTables? project, ReadOnlySpan<char> candidate, out string? purl)
    {
        purl = null;
        return !candidate.IsEmpty && (project is not null && project.TryGetCandidate(candidate, out purl) || _tree.TryGetCandidate(candidate, out purl));
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

            // Restore output names the project it was restored for; it usually sits in that
            // project's obj/, but an artifacts layout moves it out of the project directory.
            var projectPath = document.RootElement.TryGetProperty("project", out var project) && project.ValueKind == JsonValueKind.Object &&
                              project.TryGetProperty("restore", out var restore) && restore.ValueKind == JsonValueKind.Object &&
                              restore.TryGetProperty("projectPath", out var projectPathElement) && projectPathElement.ValueKind == JsonValueKind.String
                ? projectPathElement.GetString()
                : null;
            var tables = TablesForSource(filePath, projectPath);
            if (tables != _outsideProjects && Path.GetDirectoryName(filePath) is { } assetsDirectory)
            {
                _tablesByAssetsDirectory.TryAdd(assetsDirectory, tables);
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
                AddPackage(tables, parts[0], parts[1], "project.assets.json", purl, "medium");
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

                    AddAssets(tables, library.Value, "compile", purl);
                    AddAssets(tables, library.Value, "runtime", purl);
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

            var tables = TablesForSource(filePath);
            if (tables == _outsideProjects && ArtifactsRestoreDirectory(filePath) is { } restoreDirectory && _tablesByAssetsDirectory.TryGetValue(restoreDirectory, out var restored))
            {
                tables = restored;
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
                AddPackage(tables, parts[0], parts[1], "*.deps.json", purl, "medium");
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

                    AddAssets(tables, library.Value, "runtime", purl);
                    AddAssets(tables, library.Value, "compile", purl);
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

            var tables = TablesForSource(filePath);
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

                    AddPackage(tables, package.Name, version!, "packages.lock.json", BuildNuGetPurl(package.Name, version!), "high");
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

                // A paket.lock pins the whole solution, not one project.
                AddPackage(_outsideProjects, name, version, "paket.lock", BuildNuGetPurl(name, version), "high");
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
            var tables = TablesForSource(filePath);
            var count = 0;
            foreach (var package in document.Descendants("package"))
            {
                var name = package.Attribute("id")?.Value;
                var version = package.Attribute("version")?.Value;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(version))
                {
                    continue;
                }

                AddPackage(tables, name!, version!, "packages.config", BuildNuGetPurl(name!, version!), "high");
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
            var tables = TablesForSource(filePath);
            var count = 0;
            foreach (var reference in document.Descendants("PackageReference"))
            {
                var name = reference.Attribute("Include")?.Value ?? reference.Attribute("Update")?.Value;
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                // Only a literal version names one: an MSBuild property (`$(MoqVersion)`), a range
                // (`[4.0,5.0)`) or a float (`4.*`) does not, so it gives a versionless purl rather
                // than one carrying the expression.
                var version = reference.Attribute("Version")?.Value;
                if (version is not null && version.AsSpan().IndexOfAny("$[](),*") >= 0)
                {
                    version = null;
                }

                var purl = string.IsNullOrWhiteSpace(version) ? $"pkg:nuget/{EscapePurl(name!)}" : BuildNuGetPurl(name!, version!);
                AddPackage(tables, name!, version ?? string.Empty, Path.GetExtension(filePath).TrimStart('.'), purl, string.IsNullOrWhiteSpace(version) ? "low" : "medium");
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

    private void AddAssets(PackageTables tables, JsonElement libraryElement, string propertyName, string purl)
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
                _tree.AddAssembly(assemblyName, purl);
                tables.AddAssembly(assemblyName, purl);
            }
        }
    }

    private void AddPackage(PackageTables tables, string packageName, string version, string source, string purl, string confidence)
    {
        ResolutionFacts.Add(new PackageResolutionFact(packageName, version, purl, source, confidence, tables.Label));
        _tree.AddPackage(packageName, version, source, purl);
        tables.AddPackage(packageName, version, source, purl);
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
