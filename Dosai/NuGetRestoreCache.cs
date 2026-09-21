using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.CodeAnalysis;

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
///     Resolution is best-effort and never throws; per-root results are cached because the
///     four metadata-reference builders in one process scan the same tree.
/// </summary>
public static class NuGetRestoreCache
{
    private const int MaxAssetsFiles = 200;

    private sealed record RootResult(List<string> ReferencePaths, NuGetRestoreSummary Summary, List<string> Diagnostics);

    private static readonly Dictionary<string, RootResult> ResultsByRoot = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object ResultsLock = new();

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
            canonicalRoot = Path.GetFullPath(root);
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
        try
        {
            foreach (var assetsFile in SafeFileRead.EnumerateAllFilesSafe(root, "project.assets.json"))
            {
                if (assetsFiles >= MaxAssetsFiles)
                {
                    break;
                }
                assetsFiles++;
                ReadAssetsFile(assetsFile, referencePaths, referenceFileNames);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Discovery is best-effort; whatever was resolved before the failure still counts.
        }

        var diagnostics = new List<string>();
        if (assetsFiles > 0 && referencePaths.Count > 0)
        {
            diagnostics.Add($"Resolved {referencePaths.Count} package assemblies from NuGet restore output ({assetsFiles} project.assets.json).");
        }
        else if (assetsFiles > 0)
        {
            diagnostics.Add("Found project.assets.json but no package assemblies were resolved; the NuGet packages cache may be missing (run dotnet restore).");
        }
        return new RootResult(referencePaths, new NuGetRestoreSummary(assetsFiles, referencePaths.Count), diagnostics);
    }

    private static void ReadAssetsFile(string assetsFile, List<string> referencePaths, HashSet<string> referenceFileNames)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(assetsFile));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return;
        }
        using (document)
        {
            var packageFolders = ReadPackageFolders(document.RootElement);
            if (packageFolders.Count == 0)
            {
                return;
            }
            var libraryPaths = ReadLibraryPaths(document.RootElement);
            if (libraryPaths.Count == 0 || !document.RootElement.TryGetProperty("targets", out var targets) || targets.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var target in targets.EnumerateObject())
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
        }
    }

    private static List<string> ReadPackageFolders(JsonElement root)
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
