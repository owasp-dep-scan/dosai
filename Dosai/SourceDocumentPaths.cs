namespace Depscan;

/// <summary>
///     Places a source document an assembly's PDB names in the scanned tree (issue #79), so a call
///     site or data-flow node found in IL carries the path the source analysis gives the same file:
///     relative to the scan root (the inspected directory, or a file's directory).
///     <para>
///         A PDB names its documents as the compiler saw them: absolute for a local build, under a
///         path map such as <c>/_/</c> for a deterministic (CI) build, and in the other file system's
///         form when the build ran on another machine or in another copy of the tree. A document
///         under the root is made relative to it. Any other document of an assembly the tree builds
///         from source is placed by the longest tail of its path that names a file under the root:
///         the build compiled this tree's sources, wherever it ran. Anything else - a package's own
///         sources, a file outside a scanned build output - has no place in the tree, and the
///         caller decides what to write instead.
///     </para>
/// </summary>
internal static class SourceDocumentPaths
{
    private static readonly char[] Separators = ['/', '\\'];

    /// <summary>The full path that tree paths are relative to: the inspected directory, or a file's directory.</summary>
    public static string? Root(string inspectedPath)
    {
        var root = Directory.Exists(inspectedPath) ? inspectedPath : Path.GetDirectoryName(inspectedPath);
        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    ///     The file name of a document path in either file system's form: a Windows build's
    ///     <c>D:\a\src\Program.cs</c> is <c>Program.cs</c> on Linux too, where
    ///     <see cref="Path.GetFileName(string)" /> would keep the whole path.
    /// </summary>
    public static string FileName(string documentPath)
    {
        var separator = documentPath.AsSpan().LastIndexOfAny('/', '\\');
        return separator < 0 ? documentPath : documentPath[(separator + 1)..];
    }

    /// <summary>
    ///     <paramref name="documentPath" /> relative to <paramref name="root" />, or null when it has no
    ///     place in the tree. <paramref name="builtFromSource" /> says whether the assembly whose PDB
    ///     names the document is one the tree builds from source.
    /// </summary>
    public static string? InTree(string? root, string? documentPath, bool builtFromSource)
    {
        if (root is null || string.IsNullOrWhiteSpace(documentPath))
        {
            return null;
        }

        try
        {
            // The scan's own paths (an assembly where no sequence point covers an offset) are
            // relative to the working directory when the inspected path was.
            if (!Path.IsPathFullyQualified(documentPath) && File.Exists(documentPath))
            {
                documentPath = Path.GetFullPath(documentPath);
            }

            if (Path.IsPathFullyQualified(documentPath) && UnderRoot(root, Path.GetFullPath(documentPath)) is { } relative)
            {
                return relative;
            }

            return builtFromSource ? LongestTailInTree(root, documentPath) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? UnderRoot(string root, string fullPath)
    {
        var relative = Path.GetRelativePath(root, fullPath);
        return string.IsNullOrWhiteSpace(relative) || relative == "." || Path.IsPathFullyQualified(relative) || relative == ".." ||
               relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
               relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal)
            ? null
            : relative;
    }

    /// <summary>
    ///     The longest tail of <paramref name="documentPath" />'s segments (split on either separator,
    ///     so a Windows build places on Linux and back) that names a file under the root. A tail never
    ///     holds a drive, a relative step or an invalid name, so it cannot leave the root.
    /// </summary>
    private static string? LongestTailInTree(string root, string documentPath)
    {
        var segments = documentPath.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        var firstUsable = segments.Length;
        while (firstUsable > 0 && IsPlainSegment(segments[firstUsable - 1]))
        {
            firstUsable--;
        }

        for (var start = firstUsable; start < segments.Length; start++)
        {
            var relative = string.Join(Path.DirectorySeparatorChar, segments, start, segments.Length - start);
            if (File.Exists(Path.Join(root, relative)))
            {
                return relative;
            }
        }

        return null;
    }

    private static bool IsPlainSegment(string segment) =>
        segment is not ("." or "..") && segment.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !segment.Contains(':', StringComparison.Ordinal);
}
