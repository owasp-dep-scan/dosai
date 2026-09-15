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
    ///     Recursive file enumeration that survives hostile trees: unreadable, over-long, or
    ///     disappearing subtrees degrade to the files gathered so far. Analysis inputs can be
    ///     anywhere - including shared temp directories - so discovery must never crash the scan.
    /// </summary>
    public static IReadOnlyList<string> EnumerateAllFilesSafe(string root, string searchPattern = "*.*")
    {
        var files = new List<string>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, searchPattern, SearchOption.AllDirectories))
            {
                files.Add(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PathTooLongException or DirectoryNotFoundException)
        {
        }
        return files;
    }
}
