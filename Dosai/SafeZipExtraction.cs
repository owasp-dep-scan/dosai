using System.IO.Compression;

namespace Depscan;

/// <summary>
/// Outcome of extracting one archive entry: either the destination path it was written to, or
/// the reason it was skipped. A skipped entry is the caller's signal to report the entry name
/// in its diagnostics and carry on.
/// </summary>
internal readonly record struct ZipEntryExtraction(string? DestinationPath, string? SkipReason)
{
    public bool WasSkipped => SkipReason is not null;
}

/// <summary>
/// Containment check for extracting zip archive entries under a destination directory. Entry
/// names come from an untrusted archive and must never reach the file system as a path
/// unchecked: every site that writes an entry routes through <see cref="ExtractEntry"/>, which
/// refuses a name that is rooted, drive-qualified, contains a parent segment or a backslash,
/// or whose resolved destination leaves the root, and writes the rest without overwriting.
/// </summary>
internal static class SafeZipExtraction
{
    /// <summary>
    /// Extract <paramref name="entry"/> under <paramref name="rootDirectory"/>, creating
    /// parent directories as needed. Returns the destination path, or a skip reason when the
    /// entry name is not a plain relative path inside the root or the write itself failed;
    /// extraction of one entry never throws, so a hostile archive cannot abort the analysis.
    /// </summary>
    public static ZipEntryExtraction ExtractEntry(ZipArchiveEntry entry, string rootDirectory)
    {
        try
        {
            if (!TryResolveDestination(rootDirectory, entry.FullName, out var destinationPath, out var skipReason))
            {
                return new ZipEntryExtraction(null, skipReason);
            }
            var destinationDir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(destinationDir))
            {
                Directory.CreateDirectory(destinationDir);
            }
            // No overwrite: an archive that repeats a path (or a re-extraction into a dirty
            // directory) must not replace a file that is already on disk.
            entry.ExtractToFile(destinationPath, overwrite: false);
            return new ZipEntryExtraction(destinationPath, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new ZipEntryExtraction(null, ex.Message);
        }
    }

    /// <summary>
    /// Resolve entry name <paramref name="entryFullName"/> to a destination inside
    /// <paramref name="rootDirectory"/>. Zip entry names are relative forward-slash paths, so
    /// anything absolute or rooted, drive-qualified, or holding a parent segment or a backslash
    /// separator is refused up front; the resolved full path is then required to sit under the
    /// root with an ordinal comparison (case-insensitive on Windows).
    /// </summary>
    private static bool TryResolveDestination(string rootDirectory, string entryFullName, out string destinationPath, out string? skipReason)
    {
        destinationPath = string.Empty;
        skipReason = null;
        if (string.IsNullOrEmpty(entryFullName))
        {
            skipReason = "the entry name is empty";
            return false;
        }
        if (Path.IsPathRooted(entryFullName))
        {
            skipReason = "the entry name is rooted or absolute";
            return false;
        }
        if (entryFullName.Length >= 2 && entryFullName[1] == ':' && char.IsAsciiLetter(entryFullName[0]))
        {
            // C:\x or the drive-relative C:x, which IsPathRooted does not catch. On Unix the
            // colon is refused because the name carries a Windows drive shape either way.
            skipReason = "the entry name is drive-qualified";
            return false;
        }
        if (Array.Exists(entryFullName.Split('/'), segment => segment == ".."))
        {
            skipReason = "the entry name contains a parent segment";
            return false;
        }
        if (entryFullName.Contains('\\', StringComparison.Ordinal))
        {
            skipReason = "the entry name contains a backslash";
            return false;
        }
        if (entryFullName.EndsWith('/', StringComparison.Ordinal))
        {
            skipReason = "the entry is a directory";
            return false;
        }
        string fullRoot;
        string fullPath;
        try
        {
            fullRoot = Path.GetFullPath(rootDirectory);
            fullPath = Path.GetFullPath(Path.Combine(rootDirectory, entryFullName));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            skipReason = $"the entry name is not a valid relative path ({ex.Message})";
            return false;
        }
        var rootPrefix = fullRoot.EndsWith(Path.DirectorySeparatorChar) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!fullPath.StartsWith(rootPrefix, comparison))
        {
            skipReason = "the resolved destination is outside the extraction root";
            return false;
        }
        destinationPath = fullPath;
        return true;
    }
}
