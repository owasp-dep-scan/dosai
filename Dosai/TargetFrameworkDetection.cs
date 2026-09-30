using System.Text.Json;

namespace Depscan;

/// <summary>
///     Resolves the effective target framework(s) of a scan root, best-effort, by reading
///     project files - <c>*.csproj</c>/<c>*.fsproj</c>/<c>*.vbproj</c> and
///     <c>Directory.Build.props</c>/<c>Directory.Build.targets</c> - via regex over file text,
///     mirroring <see cref="CSharpSourceParser.DetectImplicitUsings" /> (no MSBuild evaluation,
///     no added dependency). For assembly-only trees without a project file it falls back to
///     <c>*.runtimeconfig.json</c> (<c>runtimeOptions.tfm</c> or the framework version).
///     Detection is cached per resolved root under a lock; unreadable or absent files simply
///     contribute nothing, and an empty result means the caller falls back to the default
///     latest-modern-net define set. OS/platform-suffixed TFMs (<c>net8.0-windows</c>) are
///     recorded whole; <see cref="FrameworkPreprocessorDefines" /> strips the suffix for
///     symbol purposes.
/// </summary>
internal static class TargetFrameworkDetection
{
    private static readonly Lock DetectionLock = new();
    private static readonly Dictionary<string, IReadOnlyList<string>> DetectedByRoot = new(SafeFileRead.PathComparer);

    public static IReadOnlyList<string> Detect(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return [];
        }

        string root;
        try
        {
            root = ProjectContextRoot(Path.GetFullPath(path));
        }
        catch (ArgumentException)
        {
            return [];
        }

        lock (DetectionLock)
        {
            if (DetectedByRoot.TryGetValue(root, out var cached))
            {
                return cached;
            }

            var detected = DetectNoCache(root);
            DetectedByRoot[root] = detected;
            return detected;
        }
    }

    /// <summary>
    ///     The directory whose projects describe a scan root: the root itself, or for a
    ///     single-file scan (<c>--path src/App/Program.cs</c>) the file's directory. Enumerating
    ///     project files under a file path finds nothing, which silently evaluated a single
    ///     file's guards against the latest-modern-net fallback instead of its own project.
    ///     Parse options, the implicit-usings decision and the reported target frameworks all
    ///     resolve through this, so they agree for file and directory roots alike.
    /// </summary>
    internal static string ProjectContextRoot(string fullPath) =>
        File.Exists(fullPath) ? Path.GetDirectoryName(fullPath) ?? fullPath : fullPath;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string?> NearestProjectDirectoryCache = new(SafeFileRead.PathComparer);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ProjectTargets?> ProjectTargetsCache = new(SafeFileRead.PathComparer);

    /// <summary>
    ///     The target frameworks governing one source file: those of the nearest project at or
    ///     above the file's directory, up to the scan root - the project that compiles it - and
    ///     the scan root's detection for a file outside every project or under one whose target
    ///     cannot be read without evaluating MSBuild (<c>$(NetCoreAppCurrent)</c>).
    /// </summary>
    /// <remarks>
    ///     Dosai compiles every file of a tree in one compilation, but parse options are per
    ///     file. Resolving one representative for the whole tree evaluated a net48 project's
    ///     guards as net8.0 next to a net8.0 project, dropping its <c>#if NETFRAMEWORK</c> code;
    ///     the per-directory recursive detection before that got a project's own files right but
    ///     not its subdirectories', and cost one recursive enumeration per directory. The nearest
    ///     project is found by walking up, one non-recursive listing per directory, memoized.
    ///     A project's targets come from its own file, else from the nearest
    ///     <c>Directory.Build.props</c>, else <c>Directory.Build.targets</c>, at or above it
    ///     within the scan root.
    /// </remarks>
    /// <param name="projectExtension">The project kind that compiles the file: <c>.csproj</c> for C#, <c>.fsproj</c> for F#.</param>
    internal static ProjectTargets ForFile(string? rootPath, string filePath, string projectExtension)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return new ProjectTargets(null, []);
        }

        string root;
        string? directory;
        try
        {
            root = Path.TrimEndingDirectorySeparator(ProjectContextRoot(Path.GetFullPath(rootPath)));
            directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        }
        catch (ArgumentException)
        {
            return new ProjectTargets(null, []);
        }

        if (directory is not null && IsWithin(root, directory)
            && NearestProjectDirectory(directory, root, projectExtension) is { } projectDirectory
            && TargetsOfProjectIn(projectDirectory, root, projectExtension) is { } project)
        {
            return project;
        }

        return new ProjectTargets(null, Detect(root));
    }

    /// <summary>
    ///     Every project of <paramref name="projectExtension" /> kind under the root whose own
    ///     targets were readable, sorted by path: the per-project guard decisions
    ///     <see cref="ForFile" /> makes, for the slice metadata.
    /// </summary>
    internal static List<ProjectTargets> ProjectsUnder(string? rootPath, string projectExtension)
    {
        var projects = new List<ProjectTargets>();
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return projects;
        }

        string root;
        try
        {
            root = Path.TrimEndingDirectorySeparator(ProjectContextRoot(Path.GetFullPath(rootPath)));
        }
        catch (ArgumentException)
        {
            return projects;
        }

        var projectDirectories = SafeFileRead.EnumerateAllFilesSafe(root, "*" + projectExtension)
            .Where(file => !IsUnderBuildDirectory(root, file))
            .Select(Path.GetDirectoryName)
            .OfType<string>()
            .Distinct(SafeFileRead.PathComparer);
        foreach (var projectDirectory in projectDirectories)
        {
            if (TargetsOfProjectIn(projectDirectory, root, projectExtension) is { } project)
            {
                projects.Add(project);
            }
        }

        projects.Sort((x, y) => string.CompareOrdinal(x.ProjectFile, y.ProjectFile));
        return projects;
    }

    private static bool IsWithin(string root, string directory)
    {
        var relative = Path.GetRelativePath(root, directory);
        return relative == "." || (!Path.IsPathRooted(relative) && !relative.StartsWith("..", StringComparison.Ordinal));
    }

    private static string? NearestProjectDirectory(string directory, string root, string projectExtension)
    {
        var key = projectExtension + "|" + directory;
        if (NearestProjectDirectoryCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        string? nearest;
        if (ProjectFilesIn(directory, root, projectExtension).Count > 0)
        {
            nearest = directory;
        }
        else if (string.Equals(directory, root, SafeFileRead.PathComparison) || Path.GetDirectoryName(directory) is not { } parent)
        {
            nearest = null;
        }
        else
        {
            nearest = NearestProjectDirectory(parent, root, projectExtension);
        }

        NearestProjectDirectoryCache[key] = nearest;
        return nearest;
    }

    private static List<string> ProjectFilesIn(string directory, string root, string projectExtension)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*" + projectExtension, SearchOption.TopDirectoryOnly)
                .Where(file => !IsUnderBuildDirectory(root, file) && !PathExclusions.IsExcluded(file, isDirectory: false))
                .Order(StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    private static ProjectTargets? TargetsOfProjectIn(string projectDirectory, string root, string projectExtension)
    {
        return ProjectTargetsCache.GetOrAdd(projectExtension + "|" + root + "|" + projectDirectory, _ =>
        {
            var projectFiles = ProjectFilesIn(projectDirectory, root, projectExtension);
            if (projectFiles.Count == 0)
            {
                return null;
            }

            var detected = new List<string>();
            foreach (var projectFile in projectFiles)
            {
                CollectTargetFrameworks(projectFile, detected);
            }

            if (detected.Count == 0)
            {
                foreach (var buildFile in new[] { "Directory.Build.props", "Directory.Build.targets" })
                {
                    if (NearestFileUpwards(projectDirectory, root, buildFile) is { } inherited)
                    {
                        CollectTargetFrameworks(inherited, detected);
                        if (detected.Count > 0)
                        {
                            break;
                        }
                    }
                }
            }

            var targets = detected.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return targets.Count == 0 ? null : new ProjectTargets(Path.GetRelativePath(root, projectFiles[0]), targets);
        });
    }

    private static string? NearestFileUpwards(string directory, string root, string fileName)
    {
        for (var current = directory; current is not null; current = Path.GetDirectoryName(current))
        {
            var candidate = Path.Combine(current, fileName);
            if (File.Exists(candidate) && !PathExclusions.IsExcluded(candidate, isDirectory: false))
            {
                return candidate;
            }

            if (string.Equals(current, root, SafeFileRead.PathComparison))
            {
                break;
            }
        }

        return null;
    }

    private static IReadOnlyList<string> DetectNoCache(string root)
    {
        var detected = new List<string>();
        try
        {
            var projectFiles = SafeFileRead.EnumerateAllFilesSafe(root, "*.csproj")
                .Concat(SafeFileRead.EnumerateAllFilesSafe(root, "*.fsproj"))
                .Concat(SafeFileRead.EnumerateAllFilesSafe(root, "*.vbproj"))
                .Concat(SafeFileRead.EnumerateAllFilesSafe(root, "Directory.Build.props"))
                .Concat(SafeFileRead.EnumerateAllFilesSafe(root, "Directory.Build.targets"));
            foreach (var projectFile in projectFiles.Where(file => !IsUnderBuildDirectory(root, file)))
            {
                CollectTargetFrameworks(projectFile, detected);
            }

            // Assembly-only trees: a built output directory carries no project file, but a
            // runtimeconfig names the framework the app runs on.
            if (detected.Count == 0)
            {
                // A runtimeconfig only exists in build output, so unlike project files it is read
                // from bin/obj rather than skipped there.
                foreach (var runtimeConfig in SafeFileRead.EnumerateAllFilesSafe(root, "*.runtimeconfig.json"))
                {
                    if (TryDetectFromRuntimeConfig(runtimeConfig, out var targetFramework))
                    {
                        detected.Add(targetFramework);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Best-effort detection; analysis continues with the default modern-net defines.
        }

        return detected.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    // `TargetFrameworks?` matches both TargetFramework and TargetFrameworks while rejecting
    // TargetFrameworkVersion (the next character after the name is neither `s` nor `>`).
    private static readonly System.Text.RegularExpressions.Regex TargetFrameworkRegex =
        new(@"<TargetFrameworks?\s*>([^<]+)</TargetFrameworks?\s*>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    // Classic, non-SDK projects carry `<TargetFrameworkVersion>v4.7.2</TargetFrameworkVersion>`
    // instead of a TFM. Without this they detect as nothing and fall back to the modern-net
    // defines, which is the one case where the fallback is knowably wrong.
    private static readonly System.Text.RegularExpressions.Regex TargetFrameworkVersionRegex =
        new(@"<TargetFrameworkVersion\s*>\s*v?([0-9]+(?:\.[0-9]+)*)\s*</TargetFrameworkVersion\s*>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static void CollectTargetFrameworks(string projectFile, List<string> detected)
    {
        if (!SafeFileRead.TryReadAllText(projectFile, out var content))
        {
            return;
        }

        foreach (var match in TargetFrameworkRegex.Matches(content).Cast<System.Text.RegularExpressions.Match>())
        {
            foreach (var value in match.Groups[1].Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (IsPlausibleTargetFramework(value))
                {
                    detected.Add(value);
                }
            }
        }

        foreach (var match in TargetFrameworkVersionRegex.Matches(content).Cast<System.Text.RegularExpressions.Match>())
        {
            // `v4.7.2` is the `net472` moniker: digits joined, separators dropped.
            var moniker = "net" + match.Groups[1].Value.Replace(".", string.Empty, StringComparison.Ordinal);
            if (IsPlausibleTargetFramework(moniker))
            {
                detected.Add(moniker);
            }
        }
    }

    private static bool IsUnderBuildDirectory(string root, string filePath)
    {
        foreach (var segment in Path.GetRelativePath(root, filePath).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment is "bin" or "obj" or "node_modules" or "artifacts" or "packages")
            {
                return true;
            }
        }

        return false;
    }

    // Accepts `net8.0`, `net8.0-windows`, `net472`, `netstandard2.0`, `netcoreapp3.1`; rejects
    // MSBuild property references and junk that happens to sit inside the element.
    private static bool IsPlausibleTargetFramework(string value)
    {
        if (value.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase))
        {
            return value.Length > 11 && char.IsAsciiDigit(value[11]);
        }

        if (value.StartsWith("netcoreapp", StringComparison.OrdinalIgnoreCase))
        {
            return value.Length > 10 && char.IsAsciiDigit(value[10]);
        }

        return value.Length > 3 && value.StartsWith("net", StringComparison.OrdinalIgnoreCase) && char.IsAsciiDigit(value[3]);
    }

    private static bool TryDetectFromRuntimeConfig(string runtimeConfigFile, out string targetFramework)
    {
        targetFramework = string.Empty;
        if (!SafeFileRead.TryReadAllText(runtimeConfigFile, out var content))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            var runtimeOptions = document.RootElement.TryGetProperty("runtimeOptions", out var options) ? options : default;
            if (runtimeOptions.ValueKind == JsonValueKind.Object)
            {
                if (runtimeOptions.TryGetProperty("tfm", out var tfm) && tfm.ValueKind == JsonValueKind.String && IsPlausibleTargetFramework(tfm.GetString() ?? string.Empty))
                {
                    targetFramework = tfm.GetString()!;
                    return true;
                }

                if (runtimeOptions.TryGetProperty("framework", out var framework)
                    && framework.ValueKind == JsonValueKind.Object
                    && framework.TryGetProperty("version", out var version)
                    && version.ValueKind == JsonValueKind.String
                    && Version.TryParse(version.GetString(), out var parsed))
                {
                    targetFramework = $"net{parsed.Major}.{parsed.Minor}";
                    return IsPlausibleTargetFramework(targetFramework);
                }
            }
        }
        catch (JsonException)
        {
            // A malformed runtimeconfig contributes nothing.
        }

        return false;
    }
}

/// <summary>
///     The target frameworks one file's guards resolve against, and the project (relative to the
///     scan root) they were read from; <see cref="ProjectFile" /> is null when the scan root's
///     detection applied instead.
/// </summary>
internal sealed record ProjectTargets(string? ProjectFile, IReadOnlyList<string> TargetFrameworks);
