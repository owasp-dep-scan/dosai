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
}
