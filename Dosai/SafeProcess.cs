namespace Depscan;

/// <summary>
///     Resolves the executables for the child processes Dosai itself starts: dotnet for
///     <c>dotnet --list-runtimes</c> and the opt-in restore/build, Rscript for the R native
///     parser. A bare name leaves the choice to the runtime: on Unix, Process.Start looks for a
///     bare file name in the working directory before PATH, and Dosai's working directory is
///     regularly the scanned tree (cdxgen starts it there). A PATH entry of "." or an empty
///     string resolves the same way, and an absolute entry can point into the tree. Every method here
///     returns an absolute path taken only from explicit environment locations outside the
///     scan root, or null when nothing qualifies; callers treat null as "do not run" and
///     degrade, never throw.
/// </summary>
public static class SafeProcess
{
    /// <summary>
    ///     Resolves the dotnet executable for a scan rooted at <paramref name="scanRoot"/>:
    ///     <c>DOTNET_HOST_PATH</c> first, then the host running this process when it is
    ///     dotnet, then dotnet inside <c>DOTNET_ROOT</c>, then the first absolute PATH
    ///     directory outside the scan root that holds a dotnet executable.
    /// </summary>
    public static string? ResolveDotnet(string? scanRoot) =>
        ResolveDotnet(
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH"),
            Environment.ProcessPath,
            Environment.GetEnvironmentVariable("DOTNET_ROOT"),
            Environment.GetEnvironmentVariable("PATH"),
            scanRoot);

    /// <summary>
    ///     The pure core of <see cref="ResolveDotnet(string?)"/>: every input is a parameter so
    ///     the resolution order stays testable without touching the process environment. The
    ///     host path is honored as the operator's explicit choice; the scan-root rule applies
    ///     to the DOTNET_ROOT and PATH locations.
    /// </summary>
    public static string? ResolveDotnet(string? hostPath, string? processPath, string? dotnetRoot, string? pathValue, string? scanRoot)
    {
        if (AbsoluteFile(hostPath) is { } explicitHost)
        {
            return explicitHost;
        }
        if (processPath is not null && IsDotnetHost(processPath) && File.Exists(processPath))
        {
            return processPath;
        }
        if (UsableDirectory(dotnetRoot, scanRoot) is { } root)
        {
            foreach (var candidate in DotnetCandidates())
            {
                var path = Path.Combine(root, candidate);
                if (File.Exists(path))
                {
                    return path;
                }
            }
        }
        return ResolveFromPath(pathValue, scanRoot, DotnetCandidates());
    }

    /// <summary>
    ///     Resolves an executable by candidate names from the current PATH, searching only
    ///     absolute directories outside <paramref name="scanRoot"/>. ".", empty and relative
    ///     entries are skipped, so where such an entry would resolve at exec time never
    ///     matters.
    /// </summary>
    public static string? ResolveFromPath(IReadOnlyList<string> candidateNames, string? scanRoot) =>
        ResolveFromPath(Environment.GetEnvironmentVariable("PATH"), scanRoot, candidateNames);

    /// <summary>The pure core of <see cref="ResolveFromPath(IReadOnlyList{string}, string?)"/>.</summary>
    public static string? ResolveFromPath(string? pathValue, string? scanRoot, IReadOnlyList<string> candidateNames)
    {
        foreach (var entry in (pathValue ?? string.Empty).Split(Path.PathSeparator))
        {
            if (UsableDirectory(entry, scanRoot) is not { } directory)
            {
                continue;
            }
            foreach (var candidate in candidateNames)
            {
                var path = Path.Combine(directory, candidate);
                if (File.Exists(path))
                {
                    return path;
                }
            }
        }
        return null;
    }

    /// <summary>
    ///     A working directory for child processes that must not run in the scanned tree: the
    ///     temp directory when it exists, else null. A null answer tells the caller to leave the
    ///     parent's directory in place rather than fail the launch; the executable is already an
    ///     absolute path, so the working directory no longer chooses what runs.
    /// </summary>
    public static string? NeutralWorkingDirectory()
    {
        try
        {
            var temp = Path.GetTempPath();
            return Directory.Exists(temp) ? temp : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>An existing file at an absolute path, fully qualified; anything else is not usable.</summary>
    private static string? AbsoluteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
        {
            return null;
        }
        try
        {
            var full = Path.GetFullPath(path);
            return File.Exists(full) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    ///     A PATH or DOTNET_ROOT entry worth searching: absolute, outside the scan root and an
    ///     existing directory. Relative entries (".", empty, or any path rooted at the working
    ///     directory) are rejected outright, because where they resolve depends on the child's
    ///     working directory at exec time.
    /// </summary>
    private static string? UsableDirectory(string? entry, string? scanRoot)
    {
        if (string.IsNullOrWhiteSpace(entry) || entry is "." or ".." || !Path.IsPathRooted(entry))
        {
            return null;
        }
        string full;
        try
        {
            full = Path.GetFullPath(entry);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return null;
        }
        return !IsInsideRoot(full, scanRoot) && Directory.Exists(full) ? full : null;
    }

    private static bool IsInsideRoot(string absolutePath, string? scanRoot)
    {
        if (string.IsNullOrWhiteSpace(scanRoot))
        {
            return false;
        }
        string root;
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(scanRoot));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return false;
        }
        if (root.Length == 0)
        {
            // The scan root is the file system root, so nothing is outside it.
            return true;
        }
        // Windows and macOS file names differ in case independently of the locale, so the
        // comparison ignores case there and stays byte-exact on Linux.
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return absolutePath.Equals(root, comparison)
            || absolutePath.StartsWith(root + Path.DirectorySeparatorChar, comparison)
            || absolutePath.StartsWith(root + Path.AltDirectorySeparatorChar, comparison);
    }

    private static bool IsDotnetHost(string path)
    {
        var fileName = Path.GetFileName(path);
        return fileName.Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase);
    }

    // CreateProcess appends .exe to a bare name on Windows, so the pre-resolution file check
    // has to look for it; other platforms keep the exact name.
    private static string[] DotnetCandidates() => OperatingSystem.IsWindows() ? ["dotnet", "dotnet.exe"] : ["dotnet"];
}
