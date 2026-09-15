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
            Console.WriteLine($"Warning: skipping unreadable source file {path}: {ex.Message}");
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
            Console.WriteLine($"Warning: skipping unreadable source file {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    ///     Recursive file enumeration that survives hostile trees: an unreadable, over-long, or
    ///     disappearing subtree is reported, skipped, and enumeration continues with its
    ///     siblings, so only the offending subtree is lost instead of everything after it.
    ///     Analysis inputs can be anywhere - including shared temp directories - so discovery
    ///     must never crash the scan. Reparse points are not followed, preventing symlink
    ///     cycles. Callers with a diagnostics channel pass <paramref name="reportDiagnostic" />;
    ///     the default prints a console warning like the file-read helpers above.
    /// </summary>
    public static IReadOnlyList<string> EnumerateAllFilesSafe(string root, string searchPattern = "*.*", Action<string>? reportDiagnostic = null)
    {
        var files = new List<string>();
        var report = reportDiagnostic ?? (message => Console.WriteLine($"Warning: {message}"));
        EnumerateDirectory(new DirectoryInfo(root), searchPattern, files, report, depth: 0);
        return files;
    }

    private const int MaxEnumerationDepth = 128;

    private static void EnumerateDirectory(DirectoryInfo directory, string searchPattern, List<string> files, Action<string> report, int depth)
    {
        if (depth > MaxEnumerationDepth)
        {
            report($"Directory enumeration depth limit reached at {directory.FullName}; deeper files are skipped.");
            return;
        }

        IEnumerable<FileInfo> fileEntries;
        IEnumerable<DirectoryInfo> subdirectories;
        try
        {
            // Materialize inside the try: the lazy enumerators throw during iteration.
            fileEntries = directory.EnumerateFiles(searchPattern, SearchOption.TopDirectoryOnly).ToList();
            subdirectories = directory.EnumerateDirectories().ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PathTooLongException or DirectoryNotFoundException)
        {
            report($"Skipping unreadable directory {directory.FullName}: {ex.Message}");
            return;
        }

        foreach (var file in fileEntries)
        {
            files.Add(file.FullName);
        }
        foreach (var subdirectory in subdirectories)
        {
            try
            {
                if (subdirectory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException)
            {
                continue;
            }
            EnumerateDirectory(subdirectory, searchPattern, files, report, depth + 1);
        }
    }
}
