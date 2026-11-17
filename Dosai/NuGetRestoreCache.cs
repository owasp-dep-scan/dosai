using System.Collections.Immutable;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using System.Globalization;

namespace Depscan;

/// <summary>
///     Summary of one root's restore-output resolution, for diagnostics surfaces that report
///     why package references were (or were not) available to semantic binding.
/// </summary>
public sealed record NuGetRestoreSummary(int AssetsFiles, int ResolvedReferences);

/// <summary>
///     Locates package assemblies from NuGet restore output without a build.
///     <c>project.assets.json</c> names the same assemblies the compiler would reference:
///     <c>packageFolders</c> gives the global packages roots, <c>libraries</c> the package
///     install path, and each target's <c>compile</c> entries the DLL paths inside the
///     package. A restored tree therefore yields full-fidelity Roslyn binding even when no
///     <c>bin/</c> output exists, which is what keeps package reachability above the
///     dependency-only fallback for unbuilt checkouts.
///     The assets file is tree content, so a <c>packageFolders</c> entry is only followed when
///     it is an absolute, non-UNC local path under the scan root, or equals a packages folder
///     the analyst's machine configures (<c>NUGET_PACKAGES</c>, <c>NUGET_FALLBACK_PACKAGES</c>,
///     the default global folder, the user-level NuGet.Config); every other entry is skipped with a diagnostic
///     naming it, and the UNC refusal is a string-shape test that runs before any file system
///     probe.
///     Resolution is best-effort and never throws; per-root results are cached because the
///     four metadata-reference builders in one process scan the same tree.
/// </summary>
public static class NuGetRestoreCache
{
    private const int MaxAssetsFiles = 200;

    private sealed record RootResult(List<string> ReferencePaths, NuGetRestoreSummary Summary, List<string> Diagnostics);

    private static readonly Dictionary<string, RootResult> ResultsByRoot = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object ResultsLock = new();

    /// <summary>Additional trusted package folder roots for the current execution context (a test hook).</summary>
    private static readonly AsyncLocal<string[]?> TrustedPackageFoldersOverride = new();

    /// <summary>
    ///     Test hook: package folders the caller configures for the current execution context
    ///     and the threads it starts, the way an operator's environment configures
    ///     NUGET_PACKAGES. Restores the previous roots on dispose.
    /// </summary>
    internal static IDisposable OverrideTrustedPackageFoldersForTesting(params string[] folders)
    {
        var previous = TrustedPackageFoldersOverride.Value;
        TrustedPackageFoldersOverride.Value = folders;
        return new RestorePrevious(() => TrustedPackageFoldersOverride.Value = previous);
    }

    private sealed class RestorePrevious(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    /// <summary>
    ///     Package assembly paths resolved from <c>project.assets.json</c> files under
    ///     <paramref name="root" />, skipping any file whose name already appears in
    ///     <paramref name="alreadyReferencedPaths" /> so cache references never duplicate
    ///     assemblies the caller found under the scanned path (build output) or in the
    ///     runtime (trusted platform assemblies, framework packs).
    /// </summary>
    public static IReadOnlyList<string> GetReferencePaths(string? root, IEnumerable<string>? alreadyReferencedPaths = null)
    {
        var result = ResolveRoot(root);
        if (alreadyReferencedPaths is null)
        {
            return result.ReferencePaths;
        }
        var knownNames = new HashSet<string>(alreadyReferencedPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFileName)
            .OfType<string>(), StringComparer.OrdinalIgnoreCase);
        return result.ReferencePaths
            .Where(path => !knownNames.Contains(Path.GetFileName(path)))
            .ToList();
    }

    public static NuGetRestoreSummary GetSummary(string? root) => ResolveRoot(root).Summary;

    /// <summary>One-line restore-output diagnostics for the root (empty when nothing was found).</summary>
    public static IReadOnlyList<string> GetDiagnostics(string? root) => ResolveRoot(root).Diagnostics;

    /// <summary>
    ///     Builds a metadata reference from file bytes instead of a mapped file: a mapped path
    ///     stays locked for the process lifetime on Windows, and the NuGet packages cache is a
    ///     shared system location no inspection may lock (see the file-locking policy in
    ///     AGENTS.md). Unreadable files return null and are the caller's problem to skip.
    /// </summary>
    public static PortableExecutableReference? TryCreateUnpinnedReference(string assemblyPath)
    {
        try
        {
            using var stream = new FileStream(assemblyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return MetadataReference.CreateFromImage(ImmutableArray.Create(memory.ToArray()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException)
        {
            return null;
        }
    }

    private static RootResult ResolveRoot(string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return new RootResult([], new NuGetRestoreSummary(0, 0), []);
        }
        string canonicalRoot;
        try
        {
            // A single-file root reads the restore output of the file's directory, like every
            // other project-context lookup; enumerating the file itself as a directory resolved
            // nothing and warned that the scan root was unreadable.
            canonicalRoot = TargetFrameworkDetection.ProjectContextRoot(Path.GetFullPath(root));
        }
        catch (ArgumentException)
        {
            return new RootResult([], new NuGetRestoreSummary(0, 0), []);
        }

        lock (ResultsLock)
        {
            if (ResultsByRoot.TryGetValue(canonicalRoot, out var cached))
            {
                return cached;
            }
            var resolved = ResolveUncached(canonicalRoot);
            ResultsByRoot[canonicalRoot] = resolved;
            return resolved;
        }
    }

    private static RootResult ResolveUncached(string root)
    {
        var referencePaths = new List<string>();
        var referenceFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var assetsFiles = 0;
        var declaredPackages = 0;
        var diagnostics = new List<string>();
        // Refusals are reported once per distinct entry per root: every project's assets file
        // of one machine names the same package folders.
        var reportedRefusals = new HashSet<string>(StringComparer.Ordinal);
        var trust = new PackageFolderTrust();
        try
        {
            foreach (var assetsFile in SafeFileRead.EnumerateAllFilesSafe(root, "project.assets.json"))
            {
                if (assetsFiles >= MaxAssetsFiles)
                {
                    break;
                }
                assetsFiles++;
                declaredPackages += ReadAssetsFile(assetsFile, root, trust, reportedRefusals, diagnostics, referencePaths, referenceFileNames);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Discovery is best-effort; whatever was resolved before the failure still counts.
        }

        if (assetsFiles > 0 && referencePaths.Count > 0)
        {
            diagnostics.Add($"Resolved {referencePaths.Count} package assemblies from NuGet restore output ({assetsFiles} project.assets.json).");
        }
        else if (assetsFiles > 0 && declaredPackages > 0)
        {
            // Only when packages are declared: a project with no package dependencies resolves
            // none by definition, and telling its user to restore sends them after a problem
            // that does not exist (issue #67).
            diagnostics.Add("Found project.assets.json but no package assemblies were resolved; the NuGet packages cache may be missing (run dotnet restore).");
        }
        return new RootResult(referencePaths, new NuGetRestoreSummary(assetsFiles, referencePaths.Count), diagnostics);
    }

    /// <summary>Resolves one assets file's package assemblies; returns how many package libraries it declares.</summary>
    private static int ReadAssetsFile(string assetsFile, string scanRoot, PackageFolderTrust trust, HashSet<string> reportedRefusals, List<string> diagnostics, List<string> referencePaths, HashSet<string> referenceFileNames)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(assetsFile));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return 0;
        }
        using (document)
        {
            var libraryPaths = ReadLibraryPaths(document.RootElement);
            var packageFolders = ReadTrustedPackageFolders(document.RootElement, scanRoot, trust, reportedRefusals, diagnostics);
            if (packageFolders.Count == 0 || libraryPaths.Count == 0 || !document.RootElement.TryGetProperty("targets", out var targets) || targets.ValueKind != JsonValueKind.Object)
            {
                return libraryPaths.Count;
            }

            // Targets are visited highest-TFM-first so a multi-targeted project contributes the
            // assemblies its primary (newest) TFM compiles against; with JSON order, a
            // netstandard facade listed first could shadow the net8.0+ assembly for every
            // shared package. Cross-file dedupe stays first-wins, per package.
            var rankedTargets = targets.EnumerateObject()
                .OrderByDescending(target => TargetFrameworkRank(target.Name))
                .ThenBy(target => target.Name, StringComparer.Ordinal);
            foreach (var target in rankedTargets)
            {
                if (target.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                foreach (var library in target.Value.EnumerateObject())
                {
                    // Only package libraries live in the cache; project references resolve to
                    // sibling project output, which only exists after a build.
                    if (!libraryPaths.TryGetValue(library.Name, out var libraryPath) || library.Value.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }
                    // Compile assets are what the compiler references; runtime assets are the
                    // fallback for packages (analyzers, some native packages) that carry none.
                    var assetNames = ReadAssetNames(library.Value, "compile");
                    if (assetNames.Count == 0)
                    {
                        assetNames = ReadAssetNames(library.Value, "runtime");
                    }
                    foreach (var assetName in assetNames)
                    {
                        foreach (var packageFolder in packageFolders)
                        {
                            var candidate = Path.Combine(packageFolder, libraryPath, assetName.Replace('/', Path.DirectorySeparatorChar));
                            var fileName = Path.GetFileName(candidate);
                            if (!fileName.EndsWith(Constants.AssemblyExtension, StringComparison.OrdinalIgnoreCase) || referenceFileNames.Contains(fileName))
                            {
                                break;
                            }
                            if (!File.Exists(candidate))
                            {
                                continue;
                            }
                            referencePaths.Add(candidate);
                            referenceFileNames.Add(fileName);
                            break;
                        }
                    }
                }
            }

            return libraryPaths.Count;
        }
    }

    /// <summary>Rank a target framework name: net major version descending, netstandard lowest, unknown shapes neutral.</summary>
    private static int TargetFrameworkRank(string targetFramework)
    {
        var name = targetFramework.Split('/')[0].Trim();
        if (name.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase))
        {
            return -1;
        }
        var match = TargetFrameworkRegex.Match(name);
        return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major) ? major : 0;
    }

    private static readonly System.Text.RegularExpressions.Regex TargetFrameworkRegex =
        new(@"^net(\d+)\.", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static List<string> ReadPackageFolderEntries(JsonElement root)
    {
        var folders = new List<string>();
        if (root.TryGetProperty("packageFolders", out var packageFolders))
        {
            if (packageFolders.ValueKind == JsonValueKind.Object)
            {
                folders.AddRange(packageFolders.EnumerateObject().Select(folder => folder.Name).Where(name => !string.IsNullOrWhiteSpace(name)));
            }
            else if (packageFolders.ValueKind == JsonValueKind.Array)
            {
                folders.AddRange(packageFolders.EnumerateArray()
                    .Select(folder => folder.ValueKind == JsonValueKind.Object && folder.TryGetProperty("path", out var path) ? path.GetString() : null)
                    .OfType<string>());
            }
        }
        return folders;
    }

    /// <summary>Why a packageFolders entry may not be read; <see cref="None" /> means it may.</summary>
    internal enum PackageFolderRefusal
    {
        None,
        UncPath,
        NotAbsolute,
        UnresolvablePath,
        OutsideTrustedRoots,
    }

    /// <summary>
    ///     Decides whether one non-empty <c>packageFolders</c> entry from project.assets.json
    ///     may be read. The entry names a folder on the analyst's machine, but the file it
    ///     sits in is tree content, so only locations the operator's environment, the scanned
    ///     tree, or its own NuGet.config configure are accepted. The UNC check is a
    ///     string-shape test that runs first, so refusing a network location never touches
    ///     it. Internal for tests.
    /// </summary>
    internal static PackageFolderRefusal ClassifyPackageFolder(string entry, string scanRoot, IReadOnlyList<string>? configuredRoots = null)
    {
        if (IsUncShaped(entry))
        {
            return PackageFolderRefusal.UncPath;
        }

        if (!Path.IsPathRooted(entry))
        {
            return PackageFolderRefusal.NotAbsolute;
        }

        if (NormalizeFolder(entry) is not { } full)
        {
            return PackageFolderRefusal.UnresolvablePath;
        }

        if (IsUnderRoot(scanRoot, full))
        {
            return PackageFolderRefusal.None;
        }

        configuredRoots ??= ConfiguredPackageFolderRoots();
        foreach (var configured in configuredRoots)
        {
            if (string.Equals(full, configured, SafeFileRead.PathComparison))
            {
                return PackageFolderRefusal.None;
            }
        }

        return PackageFolderRefusal.OutsideTrustedRoots;
    }

    /// <summary>
    ///     The package folder entries an assets file declares, filtered to the trusted ones.
    ///     Each refused entry is skipped with a diagnostic naming it, once per distinct entry
    ///     per root, and resolution proceeds with what is left.
    /// </summary>
    private static List<string> ReadTrustedPackageFolders(JsonElement root, string scanRoot, PackageFolderTrust trust, HashSet<string> reportedRefusals, List<string> diagnostics)
    {
        var trusted = new List<string>();
        foreach (var entry in ReadPackageFolderEntries(root))
        {
            var refusal = ClassifyPackageFolder(entry, scanRoot, trust.ConfiguredRoots);
            if (refusal == PackageFolderRefusal.None)
            {
                trusted.Add(entry);
            }
            else if (reportedRefusals.Add(entry))
            {
                diagnostics.Add(RefusalDiagnostic(entry, refusal));
            }
        }

        return trusted;
    }

    private static string RefusalDiagnostic(string entry, PackageFolderRefusal refusal) => refusal switch
    {
        PackageFolderRefusal.UncPath => $"packageFolders entry '{entry}' in project.assets.json was skipped: UNC network paths are not accepted as package folders.",
        PackageFolderRefusal.NotAbsolute => $"packageFolders entry '{entry}' in project.assets.json was skipped: the path is not absolute.",
        PackageFolderRefusal.UnresolvablePath => $"packageFolders entry '{entry}' in project.assets.json was skipped: the path could not be resolved.",
        _ => $"packageFolders entry '{entry}' in project.assets.json was skipped: the folder is outside the scanned tree and is not a packages folder this machine configures (NUGET_PACKAGES, NUGET_FALLBACK_PACKAGES, the default global packages folder, or the user-level NuGet.Config).",
    };

    /// <summary>
    ///     A UNC path shape (<c>\\server\share</c> or <c>//server/share</c>, the
    ///     <c>\\?\UNC\</c> device form included): not a local path. A string test on purpose,
    ///     run before any file system probe, so refusing a network location never touches it.
    /// </summary>
    private static bool IsUncShaped(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal);

    /// <summary>An absolute folder path with trailing separators removed, or null when it cannot be resolved.</summary>
    private static string? NormalizeFolder(string path)
    {
        try
        {
            var trimmed = path.TrimEnd('\\', '/');
            return Path.GetFullPath(trimmed.Length == 0 ? path : trimmed);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool IsUnderRoot(string root, string fullFolder)
    {
        try
        {
            var relative = Path.GetRelativePath(root, fullFolder);
            return relative.Length == 0
                || relative == "."
                || (!relative.StartsWith("../", StringComparison.Ordinal)
                    && !relative.StartsWith(@"..\", StringComparison.Ordinal)
                    && relative != ".."
                    && !Path.IsPathRooted(relative));
        }
        catch (ArgumentException)
        {
            // Another root (a different drive on Windows): not under the scan root.
            return false;
        }
    }

    /// <summary>
    ///     The package folder roots the analyst's own machine configures: <c>NUGET_PACKAGES</c>,
    ///     the folders of <c>NUGET_FALLBACK_PACKAGES</c>, NuGet's default global packages
    ///     folder, the <c>globalPackagesFolder</c> and <c>fallbackPackageFolders</c> of the
    ///     user-level NuGet.Config, the Visual Studio shared offline folder on Windows, and the
    ///     test override. Nothing here is read from the scanned tree: a NuGet.config inside it
    ///     is tree content and could name any folder, so a tree's own fallback folders count
    ///     only when they sit under the scan root, which the caller already accepts.
    ///     Comparison-ready absolute paths; entries that cannot be resolved are dropped.
    /// </summary>
    private static IReadOnlyList<string> ConfiguredPackageFolderRoots()
    {
        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } environmentRoot)
        {
            candidates.Add(environmentRoot);
        }

        if (Environment.GetEnvironmentVariable("NUGET_FALLBACK_PACKAGES") is { Length: > 0 } fallbackList)
        {
            candidates.AddRange(fallbackList.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (userProfile.Length > 0)
        {
            candidates.Add(Path.Combine(userProfile, ".nuget", "packages"));
        }

        if (UserNuGetConfigPath(userProfile) is { } userConfig)
        {
            candidates.AddRange(ReadUserConfigPackageFolders(userConfig));
        }

        if (OperatingSystem.IsWindows()
            && Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) is { Length: > 0 } programFilesX86)
        {
            candidates.Add(Path.Combine(programFilesX86, "Microsoft Visual Studio", "Shared", "NuGetPackages"));
        }

        if (TrustedPackageFoldersOverride.Value is { Length: > 0 } overridden)
        {
            candidates.AddRange(overridden);
        }

        var roots = new List<string>();
        foreach (var candidate in candidates)
        {
            if (!IsUncShaped(candidate) && NormalizeFolder(candidate) is { } normalized)
            {
                roots.Add(normalized);
            }
        }

        return roots;
    }

    /// <summary>The user-level NuGet.Config NuGet itself reads, or null when there is none.</summary>
    private static string? UserNuGetConfigPath(string userProfile)
    {
        var directory = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NuGet")
            : userProfile.Length > 0 ? Path.Combine(userProfile, ".nuget", "NuGet") : null;
        if (directory is null)
        {
            return null;
        }

        foreach (var fileName in new[] { "NuGet.Config", "NuGet.config", "nuget.config" })
        {
            var candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    ///     The <c>config/globalPackagesFolder</c> value and every <c>fallbackPackageFolders</c>
    ///     entry of one user-level NuGet.Config. A relative value resolves against the config
    ///     file's directory, the way NuGet reads it. Internal for tests.
    /// </summary>
    internal static List<string> ReadUserConfigPackageFolders(string configFile)
    {
        try
        {
            var document = XDocument.Load(configFile);
            var directory = Path.GetDirectoryName(configFile);
            var globalFolders = document.Descendants("config")
                .Elements("add")
                .Where(add => string.Equals(add.Attribute("key")?.Value, "globalPackagesFolder", StringComparison.OrdinalIgnoreCase));
            var fallbackFolders = document.Descendants("fallbackPackageFolders").Elements("add");
            return globalFolders.Concat(fallbackFolders)
                .Select(add => add.Attribute("value")?.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => Path.IsPathRooted(value!) || directory is null ? value! : Path.Combine(directory, value!))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            // Best effort: an unreadable or malformed config contributes no folders.
            return [];
        }
    }

    /// <summary>
    ///     The machine's configured package folder roots, read once per resolution and only on
    ///     first use: trees without package folders never read the user-level NuGet.Config.
    /// </summary>
    private sealed class PackageFolderTrust
    {
        private IReadOnlyList<string>? configuredRoots;

        public IReadOnlyList<string> ConfiguredRoots => configuredRoots ??= ConfiguredPackageFolderRoots();
    }

    private static Dictionary<string, string> ReadLibraryPaths(JsonElement root)
    {
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("libraries", out var libraries) && libraries.ValueKind == JsonValueKind.Object)
        {
            foreach (var library in libraries.EnumerateObject())
            {
                if (library.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                var type = library.Value.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
                if (!string.Equals(type, "package", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (library.Value.TryGetProperty("path", out var pathElement) && pathElement.GetString() is { Length: > 0 } path)
                {
                    paths[library.Name] = path;
                }
            }
        }
        return paths;
    }

    private static List<string> ReadAssetNames(JsonElement library, string propertyName)
    {
        if (!library.TryGetProperty(propertyName, out var assets) || assets.ValueKind != JsonValueKind.Object)
        {
            return [];
        }
        return assets.EnumerateObject()
            .Select(asset => asset.Name)
            .Where(name => name.EndsWith(Constants.AssemblyExtension, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }
}
