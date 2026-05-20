using System.Text.Json;

namespace Depscan;

internal static class AssemblyScope
{
    private const string ObjSegment = "obj";
    private const string BinSegment = "bin";

    public static List<string> ScopeApplicationAssemblies(string analysisPath, IEnumerable<string> assemblyPaths, Action<string>? diagnostics = null)
    {
        var assemblies = assemblyPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .DistinctBy(Path.GetFullPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (assemblies.Count == 0 || File.Exists(analysisPath))
        {
            return assemblies;
        }

        var applicationAssemblyNames = GetApplicationAssemblyNames(analysisPath, diagnostics);
        return assemblies
            .Where(assemblyPath => applicationAssemblyNames.Count == 0
                ? IsLikelyApplicationAssembly(Path.GetFileName(assemblyPath))
                : applicationAssemblyNames.Contains(Path.GetFileNameWithoutExtension(assemblyPath)))
            .ToList();
    }

    public static List<string> GetAssemblyFiles(string path, bool includeBuildArtifacts, bool excludeBinWhenSourceFilesPresent, Action<string>? diagnostics = null)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return [];
        }

        if (!File.GetAttributes(path).HasFlag(FileAttributes.Directory))
        {
            return IsAssemblyExtension(Path.GetExtension(path)) ? [path] : [];
        }

        var applicationAssemblyNames = GetApplicationAssemblyNames(path, diagnostics);
        var candidates = new List<FileInfo>();
        var hasSourceFiles = false;
        var root = new DirectoryInfo(path);
        foreach (var file in root.EnumerateFiles("*.*", SearchOption.AllDirectories))
        {
            var extension = file.Extension;
            if (IsSupportedSourceExtension(extension))
            {
                hasSourceFiles = true;
                continue;
            }

            if (!IsAssemblyExtension(extension))
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(path, file.FullName);
            if (!includeBuildArtifacts && HasDirectorySegment(relativePath, ObjSegment))
            {
                continue;
            }

            candidates.Add(file);
        }

        return candidates
            .Where(file => includeBuildArtifacts || !excludeBinWhenSourceFilesPresent || !hasSourceFiles || !HasDirectorySegment(Path.GetRelativePath(path, file.FullName), BinSegment))
            .Where(file => applicationAssemblyNames.Count == 0
                ? IsLikelyApplicationAssembly(file.Name)
                : applicationAssemblyNames.Contains(Path.GetFileNameWithoutExtension(file.Name)))
            .ToDictionary(file => Path.GetFullPath(file.FullName), file => file.FullName, StringComparer.OrdinalIgnoreCase)
            .Values
            .ToList();
    }

    public static HashSet<string> GetApplicationAssemblyNames(string directory, Action<string>? diagnostics = null)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(directory))
        {
            return names;
        }

        foreach (var depsFile in Directory.EnumerateFiles(directory, "*.deps.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(depsFile));
                if (!document.RootElement.TryGetProperty("libraries", out var libraries) || libraries.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var library in libraries.EnumerateObject())
                {
                    if (!library.Value.TryGetProperty("type", out var typeElement) || !string.Equals(typeElement.GetString(), "project", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var slashIndex = library.Name.IndexOf('/');
                    names.Add(slashIndex > 0 ? library.Name[..slashIndex] : library.Name);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                diagnostics?.Invoke($"Could not read assembly dependency scope from {depsFile}: {ex.Message}");
            }
        }

        return names;
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
