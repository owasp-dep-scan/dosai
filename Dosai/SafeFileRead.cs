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
    ///     skipped instead, so cycles terminate. Enumeration is lazy per directory: callers
    ///     filter as they go and never hold a whole tree in memory. Callers with a diagnostics
    ///     channel pass <paramref name="reportDiagnostic" />; the default writes a console
    ///     warning to stderr, keeping stdout free for the MCP server's JSON-RPC stream. Files and
    ///     directories excluded by the active <see cref="PathExclusions" /> scope are skipped.
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
    private static readonly StringComparer PathComparer = OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
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
            // `yield return` cannot live inside a try that has a catch clause.
            fileEntries = directory.EnumerateFiles(searchPattern, SearchOption.TopDirectoryOnly).ToList();
            subdirectories = directory.EnumerateDirectories().ToList();
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
