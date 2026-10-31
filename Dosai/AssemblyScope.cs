using System.Text.Json;

namespace Depscan;

internal static class AssemblyScope
{
    private const string ObjSegment = "obj";
    private const string BinSegment = "bin";

    /// <summary>The assemblies kept for analysis and how each decision was reached.</summary>
    internal sealed record ScopeResult(List<string> Assemblies, int Dropped, int ByMetadata, int ByNameHeuristic)
    {
        /// <summary>The scoping decision for a debug line.</summary>
        public string Basis(bool singleFile, int candidates) => singleFile
            ? "single-file input; no directory scoping"
            : candidates == 0
                ? "no assembly candidates"
                : ByNameHeuristic == 0
                    ? "build metadata (nearest *.deps.json project libraries, restore metadata, projects built from source)"
                    : $"build metadata for {ByMetadata}, name heuristic for {ByNameHeuristic} ({HeuristicDescription})";

        /// <summary>A report diagnostic when the name heuristic had to decide, else null.</summary>
        public string? Diagnostic => ByNameHeuristic == 0
            ? null
            : $"Assembly scoping used the name heuristic for {ByNameHeuristic} assembly(ies): no *.deps.json at or above them and no restore metadata names them ({HeuristicDescription}).";
    }

    private const string HeuristicDescription = "System./Microsoft./Newtonsoft./FSharp./Humanizer prefixes are treated as dependencies";

    public static List<string> ScopeApplicationAssemblies(string analysisPath, IEnumerable<string> assemblyPaths, Action<string>? diagnostics = null) =>
        Scope(analysisPath, assemblyPaths, diagnostics).Assemblies;

    /// <summary>
    ///     Keeps the application's own assemblies and drops dependency assemblies, by the same
    ///     decision <see cref="DependencyAssemblies" /> gives the data-flow IL pass (issue #107):
    ///     the nearest <c>*.deps.json</c> at or above each assembly (within the scan root), else
    ///     the tree's restore metadata, and a project built from source is always the application's.
    ///     Only an assembly nothing in the tree names falls back to the name heuristic.
    /// </summary>
    internal static ScopeResult Scope(string analysisPath, IEnumerable<string> assemblyPaths, Action<string>? diagnostics = null)
    {
        var assemblies = assemblyPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .DistinctBy(Path.GetFullPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (assemblies.Count == 0 || File.Exists(analysisPath))
        {
            return new ScopeResult(assemblies, 0, 0, 0);
        }

        var classifier = NewClassifier(analysisPath);
        var kept = new List<string>();
        int metadata = 0, heuristic = 0;
        foreach (var assemblyPath in assemblies)
        {
            switch (classifier.EvidenceFor(assemblyPath))
            {
                case AssemblyEvidence.Application:
                    metadata++;
                    kept.Add(assemblyPath);
                    break;
                case AssemblyEvidence.Dependency:
                    metadata++;
                    break;
                default:
                    heuristic++;
                    if (IsLikelyApplicationAssembly(Path.GetFileName(assemblyPath)))
                    {
                        kept.Add(assemblyPath);
                    }

                    break;
            }
        }

        return new ScopeResult(kept, assemblies.Count - kept.Count, metadata, heuristic);
    }

    /// <summary>
    ///     What the data-flow IL pass reads: the application's assemblies, and the packages beside
    ///     them (issue #78) except the platform's own (<c>System.*</c>, <c>Microsoft.*</c>,
    ///     <c>Newtonsoft.*</c>, <c>FSharp.*</c>, <c>Humanizer*</c>), which a build output also
    ///     carries whole and no application flow is about. The answer comes from the build
    ///     metadata and the file name alone, so it does not depend on a root <c>*.deps.json</c>.
    /// </summary>
    private static List<string> WithoutPlatformAssemblies(string path, List<string> files)
    {
        var classifier = NewClassifier(path);
        return files
            .Where(file => IsLikelyApplicationAssembly(Path.GetFileName(file)) || classifier.EvidenceFor(file) == AssemblyEvidence.Application)
            .ToList();
    }

    private static DependencyAssemblies NewClassifier(string analysisPath) =>
        new(analysisPath, new Lazy<PackageUrlResolver>(() => PackageUrlResolver.Create(analysisPath)));

    /// <param name="includeDependencies">
    ///     Keep the packages copied into a build output, bar the platform's own: the data-flow IL
    ///     pass analyzes them and scopes their findings (issue #78), while the methods inventory
    ///     and its call graph are the application's own.
    /// </param>
    public static List<string> GetAssemblyFiles(string path, bool includeBuildArtifacts, bool excludeBinWhenSourceFilesPresent, Action<string>? diagnostics = null, bool includeDependencies = false)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return [];
        }

        if (!File.GetAttributes(path).HasFlag(FileAttributes.Directory))
        {
            return IsAssemblyExtension(Path.GetExtension(path)) ? [path] : [];
        }

        var candidates = new List<string>();
        var hasSourceFiles = false;
        // Discovery is best-effort: unreadable or over-long subtrees are skipped and reported,
        // and the scan continues with the readable remainder.
        foreach (var file in SafeFileRead.EnumerateAllFilesSafe(path, "*.*", diagnostics))
        {
            var extension = Path.GetExtension(file);
            if (IsSupportedSourceExtension(extension))
            {
                hasSourceFiles = true;
                continue;
            }

            if (!IsAssemblyExtension(extension))
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(path, file);
            if (!includeBuildArtifacts && HasDirectorySegment(relativePath, ObjSegment))
            {
                continue;
            }

            candidates.Add(file);
        }

        var files = candidates
            .Where(file => includeBuildArtifacts || !excludeBinWhenSourceFilesPresent || !hasSourceFiles || !HasDirectorySegment(Path.GetRelativePath(path, file), BinSegment))
            .ToDictionary(file => Path.GetFullPath(file), file => file, StringComparer.OrdinalIgnoreCase)
            .Values
            .ToList();
        return includeDependencies ? WithoutPlatformAssemblies(path, files) : Scope(path, files, diagnostics).Assemblies;
    }

    public static bool IsLikelyApplicationAssembly(string fileName) =>
        !fileName.StartsWith("System.", StringComparison.OrdinalIgnoreCase) &&
        !fileName.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) &&
        !fileName.StartsWith("Newtonsoft.", StringComparison.OrdinalIgnoreCase) &&
        !fileName.StartsWith("FSharp.", StringComparison.OrdinalIgnoreCase) &&
        !fileName.StartsWith("Humanizer", StringComparison.OrdinalIgnoreCase);

    public static bool IsSupportedSourceExtension(string extension) =>
        extension.Equals(Constants.CSharpSourceExtension, StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(Constants.VBSourceExtension, StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(Constants.FSharpSourceExtension, StringComparison.OrdinalIgnoreCase);

    private static bool IsAssemblyExtension(string extension) =>
        extension.Equals(Constants.AssemblyExtension, StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(Constants.ExeExtension, StringComparison.OrdinalIgnoreCase);

    private static bool HasDirectorySegment(string relativePath, string segment)
    {
        var parts = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Take(Math.Max(0, parts.Length - 1)).Any(part => part.Equals(segment, StringComparison.OrdinalIgnoreCase));
    }
}
