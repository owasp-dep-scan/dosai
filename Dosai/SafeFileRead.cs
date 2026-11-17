namespace Depscan;

// Real-world scans race with builds, editors, and sync clients: a source file can vanish
// or be locked between enumeration and read. Analysis is best-effort, so unreadable files
// are reported and skipped instead of aborting the whole scan.
internal static class SafeFileRead
{
    public static bool TryReadAllText(string path, out string content)
    {
        try
        {
            content = File.ReadAllText(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Warning: skipping unreadable source file {path}: {ex.Message}");
            content = string.Empty;
            return false;
        }
    }

    public static string[]? TryReadAllLines(string path)
    {
        try
        {
            return File.ReadAllLines(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Warning: skipping unreadable source file {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    ///     Recursive file enumeration that survives hostile trees: an unreadable, over-long, or
    ///     disappearing subtree is reported, skipped, and enumeration continues with its
    ///     siblings, so only the offending subtree is lost instead of everything after it.
    ///     Analysis inputs can be anywhere - including shared temp directories - so discovery
    ///     must never crash the scan. Symbolic links and junctions are followed, because linked
    ///     source and dependency directories are a normal repository layout and skipping them
    ///     would silently drop results; a link that re-enters an already visited directory is
    ///     skipped instead, so cycles terminate. The exception is a confinement scope
    ///     (<see cref="ConfineTo" />): while one is active, a link whose final target resolves
    ///     outside the confined root is skipped, so the walk cannot leave the root through it.
    ///     Enumeration is lazy per directory: callers filter as they go and never hold a whole
    ///     tree in memory. Callers with a diagnostics channel pass <paramref name="reportDiagnostic" />;
    ///     the default writes a console warning to stderr, keeping stdout free for the MCP
    ///     server's JSON-RPC stream. Files and directories excluded by the active
    ///     <see cref="PathExclusions" /> scope are skipped.
    /// </summary>
    public static IEnumerable<string> EnumerateAllFilesSafe(string root, string searchPattern = "*.*", Action<string>? reportDiagnostic = null)
    {
        // An iterator body, so the visited-directory and reported-message sets are rebuilt on
        // every enumeration; captured state would make a second pass over the same enumerable
        // return nothing.
        var reported = new HashSet<string>(StringComparer.Ordinal);
        var report = reportDiagnostic ?? ReportToStandardError;
        // One report per distinct message: a hostile directory is a single fact, and several
        // discovery passes walk the same tree.
        foreach (var file in EnumerateDirectory(new DirectoryInfo(root), searchPattern, message =>
        {
            if (reported.Add(message)) report(message);
        }, new HashSet<string>(PathComparer), depth: 0))
        {
            yield return file;
        }
    }

    private const int MaxEnumerationDepth = 128;
    private const int MaxRememberedWarnings = 1024;
    /// <summary>Path equality as the file system decides it: case-sensitive on Linux, not on Windows or macOS.</summary>
    internal static readonly StringComparer PathComparer = OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
    internal static readonly StringComparison PathComparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
    private static readonly HashSet<string> ReportedWarnings = new(StringComparer.Ordinal);

    // Every discovery pass builds its own reporter, so process-wide de-duplication is what keeps
    // one unreadable directory from printing once per pass. Bounded so a tree full of distinct
    // failures cannot grow the set without limit.
    private static void ReportToStandardError(string message)
    {
        lock (ReportedWarnings)
        {
            if (ReportedWarnings.Count < MaxRememberedWarnings && !ReportedWarnings.Add(message))
            {
                return;
            }
        }
        Console.Error.WriteLine($"Warning: {message}");
    }

    /// <summary>
    ///     Confinement root the MCP server sets for its whole request loop, in the same ambient
    ///     style as <see cref="PathExclusions" />: the walk stays free of links that leave this
    ///     root, and every other caller enumerates exactly as before.
    /// </summary>
    private static readonly AsyncLocal<string?> ConfinedRootScope = new();

    /// <summary>
    ///     While the returned scope is active, <see cref="EnumerateAllFilesSafe" /> does not
    ///     follow a symbolic link or junction whose final target resolves outside
    ///     <paramref name="root" />: the link itself is skipped and reported like an excluded
    ///     entry. Links whose target stays inside the root keep being followed, and without a
    ///     scope every link keeps being followed, so scans of linked trees are unchanged. The
    ///     scope flows to the analysis threads through the execution context, like
    ///     <see cref="PathExclusions" />.
    /// </summary>
    internal static IDisposable ConfineTo(string root)
    {
        var resolvedRoot = TryResolvePhysicalPath(root) ?? Path.GetFullPath(root);
        var previous = ConfinedRootScope.Value;
        ConfinedRootScope.Value = resolvedRoot;
        return new ConfinementScope(previous);
    }

    private sealed class ConfinementScope(string? previous) : IDisposable
    {
        public void Dispose() => ConfinedRootScope.Value = previous;
    }

    /// <summary>
    ///     True when <paramref name="entry" /> is a link whose final target resolves outside the
    ///     active confinement root, or a link whose target cannot be resolved at all. False when
    ///     no scope is active, when the entry is not a link, or when its target stays inside.
    /// </summary>
    private static bool LeavesConfinement(FileSystemInfo entry)
    {
        var root = ConfinedRootScope.Value;
        // Enumeration fills the attributes, so a plain file or directory costs no extra call.
        if (root is null || (entry.Attributes & FileAttributes.ReparsePoint) == 0)
        {
            return false;
        }

        try
        {
            var target = entry.ResolveLinkTarget(returnFinalTarget: true);
            if (target is null)
            {
                return false;
            }

            // The target string can still name symlinked ancestors (a target written as
            // /var/... on macOS, where /var is a link), so it is resolved through the same
            // component walk before the comparison.
            var resolvedTarget = TryResolvePhysicalPath(target.FullName);
            return resolvedTarget is null || !IsWithinRoot(resolvedTarget, root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    ///     True when <paramref name="candidate" /> is <paramref name="root" /> itself or lies
    ///     under it, compared the way the file system decides path case. Both paths must already
    ///     be resolved (<see cref="TryResolvePhysicalPath" />).
    /// </summary>
    internal static bool IsWithinRoot(string candidate, string root)
    {
        candidate = WithoutTrailingSeparators(candidate);
        root = WithoutTrailingSeparators(root);
        return candidate.Equals(root, PathComparison)
            || candidate.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);
    }

    private static string WithoutTrailingSeparators(string path)
    {
        var root = Path.GetPathRoot(path) ?? string.Empty;
        var length = path.Length;
        while (length > root.Length && (path[length - 1] == Path.DirectorySeparatorChar || path[length - 1] == Path.AltDirectorySeparatorChar))
        {
            length--;
        }
        return path[..length];
    }

    /// <summary>
    ///     Fully resolved form of <paramref name="path" />: every existing component is
    ///     substituted with its final link target, walking down from the path root, and
    ///     components past the first missing one are kept as written - they cannot be links yet,
    ///     which is the "deepest existing ancestor" rule for paths that do not exist. A
    ///     substitution can introduce ancestors that are themselves links, so the walk restarts
    ///     over the substituted string; the restart budget turns a link cycle into a failure
    ///     instead of a loop. Returns null when resolution fails, so callers treat the path as
    ///     unreachable rather than propagating a file-system exception.
    /// </summary>
    internal static string? TryResolvePhysicalPath(string path)
    {
        try
        {
            var current = Path.GetFullPath(path);
            for (var attempts = 0; attempts < MaxLinkResolutionAttempts; attempts++)
            {
                var (walked, substituted) = WalkComponentsOnce(current);
                if (!substituted)
                {
                    return walked;
                }
                current = walked;
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private const int MaxLinkResolutionAttempts = 32;

    /// <summary>
    ///     One component walk: every existing component is checked for being a link, and the
    ///     first substitution ends the walk early with the substituted target plus the segments
    ///     that came after it; the caller re-walks that string (a substitution can name
    ///     symlinked ancestors of its own, like /var on macOS).
    /// </summary>
    private static (string Walked, bool Substituted) WalkComponentsOnce(string path)
    {
        var root = Path.GetPathRoot(path) ?? path;
        var resolved = root;
        var missing = false;
        var position = root.Length;
        while (position < path.Length)
        {
            while (position < path.Length && (path[position] == Path.DirectorySeparatorChar || path[position] == Path.AltDirectorySeparatorChar))
            {
                position++;
            }
            var start = position;
            while (position < path.Length && path[position] != Path.DirectorySeparatorChar && path[position] != Path.AltDirectorySeparatorChar)
            {
                position++;
            }
            if (position == start)
            {
                break;
            }

            var segment = path[start..position];
            resolved = Path.Combine(resolved, segment);
            if (missing)
            {
                continue;
            }

            if (Directory.Exists(resolved))
            {
                var directoryTarget = new DirectoryInfo(resolved).ResolveLinkTarget(returnFinalTarget: true)?.FullName;
                if (directoryTarget is not null && !string.Equals(directoryTarget, resolved, PathComparison))
                {
                    return (AppendRemainder(directoryTarget, path[position..]), true);
                }
            }
            else if (File.Exists(resolved))
            {
                var fileTarget = new FileInfo(resolved).ResolveLinkTarget(returnFinalTarget: true)?.FullName;
                if (fileTarget is not null && !string.Equals(fileTarget, resolved, PathComparison))
                {
                    return (AppendRemainder(fileTarget, path[position..]), true);
                }
            }
            else
            {
                missing = true;
            }
        }
        return (resolved, false);
    }

    private static string AppendRemainder(string target, string remainder)
    {
        if (remainder.Length == 0)
        {
            return target;
        }
        return target.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + remainder;
    }

    private static IEnumerable<string> EnumerateDirectory(DirectoryInfo directory, string searchPattern, Action<string> report, HashSet<string> visited, int depth)
    {
        if (depth > MaxEnumerationDepth)
        {
            report($"Directory enumeration depth limit reached at {directory.FullName}; deeper files are skipped.");
            yield break;
        }

        if (!visited.Add(ResolveDirectoryIdentity(directory)))
        {
            // A link back into a directory already being walked; its files are reported through
            // the path that reached them first.
            yield break;
        }

        List<FileInfo> fileEntries;
        List<DirectoryInfo> subdirectories;
        try
        {
            // Materialize inside the try: the lazy enumerators throw during iteration, and
            // `yield return` cannot live inside a try that has a catch clause. Sorted by name:
            // file systems list a directory in their own order (hash order on ext4 and APFS,
            // different from one machine to the next), and that order becomes the order of the
            // syntax trees, the analyzed assemblies and every first-wins choice made from them,
            // so the same tree has to be read in the same order everywhere.
            fileEntries = directory.EnumerateFiles(searchPattern, SearchOption.TopDirectoryOnly).OrderBy(file => file.Name, StringComparer.Ordinal).ToList();
            subdirectories = directory.EnumerateDirectories().OrderBy(subdirectory => subdirectory.Name, StringComparer.Ordinal).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PathTooLongException or DirectoryNotFoundException)
        {
            report($"Skipping unreadable directory {directory.FullName}: {ex.Message}");
            yield break;
        }

        foreach (var file in fileEntries)
        {
            if (PathExclusions.IsExcluded(file.FullName, isDirectory: false))
            {
                DebugLog.NoteExcluded(isDirectory: false);
                continue;
            }
            if (LeavesConfinement(file))
            {
                report($"Skipping {file.FullName}: its link target resolves outside the confinement root.");
                continue;
            }
            yield return file.FullName;
        }
        foreach (var subdirectory in subdirectories)
        {
            // An excluded directory is pruned, not filtered: nothing beneath it is read.
            if (PathExclusions.IsExcluded(subdirectory.FullName, isDirectory: true))
            {
                DebugLog.NoteExcluded(isDirectory: true);
                continue;
            }
            if (LeavesConfinement(subdirectory))
            {
                report($"Skipping {subdirectory.FullName}: its link target resolves outside the confinement root.");
                continue;
            }
            foreach (var file in EnumerateDirectory(subdirectory, searchPattern, report, visited, depth + 1))
            {
                yield return file;
            }
        }
    }

    /// <summary>
    ///     Identity used for cycle detection: the final link target where the directory is a
    ///     link, otherwise its own full path. Resolution is best-effort - a broken or racing
    ///     link falls back to the path, which still terminates because the depth limit applies.
    /// </summary>
    private static string ResolveDirectoryIdentity(DirectoryInfo directory)
    {
        try
        {
            return directory.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? directory.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return directory.FullName;
        }
    }
}
