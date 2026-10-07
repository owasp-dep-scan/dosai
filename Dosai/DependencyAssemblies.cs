using System.Text.Json;

namespace Depscan;

/// <summary>
///     Tells the scanned application's own assemblies from the dependency assemblies a build copies
///     next to them (issue #78). The data-flow IL pass analyzes both, so flows through a package
///     stay in the graph, but a flow that never leaves dependency code is not a finding of the
///     application and is reported at a lower severity.
///     <para>
///         A build output names its own projects: the nearest <c>*.deps.json</c> at or above the
///         assembly's directory (within the scan root) lists the application's assemblies as
///         <c>project</c> libraries, and everything else in that output - packages, runtime packs,
///         <c>&lt;Reference&gt;</c> copies, tools a package drops in a subdirectory - is a dependency.
///         Without a <c>*.deps.json</c> (.NET Framework output, loose copies) an assembly is a
///         dependency only when the tree's restore metadata names a package that ships it, by whole
///         name. An assembly the tree builds from source is always the application's, and so is
///         every assembly when the scan targets a single file: the caller asked about that file.
///     </para>
/// </summary>
internal sealed class DependencyAssemblies
{
    private readonly string? _root;
    private readonly PackageUrlResolver _resolver;
    private readonly Dictionary<string, bool> _isDependencyByPath = new(SafeFileRead.PathComparer);
    private readonly Dictionary<string, BuildOutput?> _outputByDirectory = new(SafeFileRead.PathComparer);

    public DependencyAssemblies(string analysisPath, PackageUrlResolver resolver)
    {
        _resolver = resolver;
        if (Directory.Exists(analysisPath))
        {
            _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(analysisPath));
        }
    }

    /// <summary>Whether <paramref name="assemblyPath" /> is a dependency of the scanned application rather than its own code.</summary>
    public bool IsDependency(string assemblyPath)
    {
        if (_root is null || string.IsNullOrWhiteSpace(assemblyPath))
        {
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(assemblyPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (!_isDependencyByPath.TryGetValue(fullPath, out var isDependency))
        {
            isDependency = Classify(fullPath);
            _isDependencyByPath[fullPath] = isDependency;
        }

        return isDependency;
    }

    private bool Classify(string fullPath)
    {
        if (TreeFrameworks.IsBuiltFromSource(_root!, fullPath))
        {
            return false;
        }

        if (OutputFor(Path.GetDirectoryName(fullPath)) is { } output)
        {
            return !output.IsProjectAssembly(fullPath);
        }

        return _resolver.ResolvePackagedAssembly(fullPath) is not null;
    }

    /// <summary>The build output (a directory holding <c>*.deps.json</c>) at or above <paramref name="directory" /> within the scan root.</summary>
    private BuildOutput? OutputFor(string? directory)
    {
        if (directory is null || !IsWithinRoot(directory))
        {
            return null;
        }

        if (_outputByDirectory.TryGetValue(directory, out var cached))
        {
            return cached;
        }

        var output = BuildOutput.Read(directory) ?? (string.Equals(directory, _root, SafeFileRead.PathComparison) ? null : OutputFor(Path.GetDirectoryName(directory)));
        _outputByDirectory[directory] = output;
        return output;
    }

    private bool IsWithinRoot(string directory) =>
        string.Equals(directory, _root, SafeFileRead.PathComparison) ||
        directory.StartsWith(_root + Path.DirectorySeparatorChar, SafeFileRead.PathComparison);

    /// <summary>The project libraries the <c>*.deps.json</c> files of one output directory name.</summary>
    private sealed class BuildOutput
    {
        private readonly string _directory;
        private readonly HashSet<string> _projectAssets = new(SafeFileRead.PathComparer);
        private readonly HashSet<string> _projectNames = new(StringComparer.OrdinalIgnoreCase);

        private BuildOutput(string directory) => _directory = directory;

        public static BuildOutput? Read(string directory)
        {
            List<string> depsFiles;
            try
            {
                depsFiles = Directory.EnumerateFiles(directory, "*.deps.json", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return null;
            }

            BuildOutput? output = null;
            foreach (var depsFile in depsFiles)
            {
                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(depsFile));
                    if (!document.RootElement.TryGetProperty("libraries", out var libraries) || libraries.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    output ??= new BuildOutput(directory);
                    output.Add(document.RootElement, libraries);
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    // An unreadable deps file names nothing; the next one (or the restore
                    // metadata fallback) still decides.
                }
            }

            return output;
        }

        /// <summary>
        ///     A project library's runtime and satellite assets, relative to the output directory,
        ///     and its name: a build always writes the project's own assembly at the output root
        ///     under that name, even when <c>targets</c> lists no asset for it.
        /// </summary>
        private void Add(JsonElement root, JsonElement libraries)
        {
            var projects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var library in libraries.EnumerateObject())
            {
                if (library.Value.ValueKind == JsonValueKind.Object &&
                    library.Value.TryGetProperty("type", out var type) &&
                    string.Equals(type.GetString(), "project", StringComparison.OrdinalIgnoreCase))
                {
                    projects.Add(library.Name);
                    var slash = library.Name.IndexOf('/');
                    _projectNames.Add(slash > 0 ? library.Name[..slash] : library.Name);
                }
            }

            if (projects.Count == 0 || !root.TryGetProperty("targets", out var targets) || targets.ValueKind != JsonValueKind.Object)
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
                    if (!projects.Contains(library.Name) || library.Value.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    foreach (var assetKind in (ReadOnlySpan<string>)["runtime", "runtimeTargets", "resources"])
                    {
                        if (library.Value.TryGetProperty(assetKind, out var assets) && assets.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var asset in assets.EnumerateObject())
                            {
                                _projectAssets.Add(Normalize(asset.Name));
                            }
                        }
                    }
                }
            }
        }

        public bool IsProjectAssembly(string fullPath)
        {
            var relative = Normalize(Path.GetRelativePath(_directory, fullPath));
            return _projectAssets.Contains(relative) ||
                   !relative.Contains('/', StringComparison.Ordinal) && _projectNames.Contains(Path.GetFileNameWithoutExtension(relative));
        }

        private static string Normalize(string relativePath) => relativePath.Replace('\\', '/');
    }
}
