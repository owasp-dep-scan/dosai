using System.Text;
using System.Text.RegularExpressions;

namespace Depscan;

/// <summary>
///     Glob patterns for files and directories to leave out of a scan, relative to the scanned
///     root. Applied with <see cref="Apply" />, the patterns hold for every analysis started on
///     the current execution flow until the returned scope is disposed: the shared tree walk
///     (<see cref="SafeFileRead.EnumerateAllFilesSafe" />) prunes an excluded directory without
///     descending into it, and skips excluded files.
/// </summary>
/// <remarks>
///     Pattern syntax follows the gitignore and fast-glob conventions callers such as cdxgen
///     already use:
///     <list type="bullet">
///         <item><c>*</c> matches within one path segment, <c>?</c> one character, <c>**</c> any number of segments.</item>
///         <item>A pattern without a slash matches a file or directory name at any depth (<c>*.Designer.cs</c>, <c>node_modules</c>).</item>
///         <item>A pattern with a slash is anchored at the root (<c>BuildOutput/**</c>, <c>src/Legacy</c>); a leading <c>/</c> or <c>./</c> is ignored.</item>
///         <item>A trailing <c>/</c> restricts the pattern to directories.</item>
///         <item>Excluding a directory excludes everything beneath it.</item>
///     </list>
///     Matching ignores case except on Linux, as the tree walk's own path comparison does.
///     Paths outside the root - shared-framework or package-cache directories an analysis
///     reads for references - are never excluded.
/// </remarks>
public sealed class PathExclusions
{
    private static readonly AsyncLocal<PathExclusions?> CurrentScope = new();

    private readonly string _root;
    private readonly List<(Regex Pattern, bool DirectoriesOnly)> _patterns;

    private PathExclusions(string root, List<(Regex Pattern, bool DirectoriesOnly)> patterns)
    {
        _root = root;
        _patterns = patterns;
    }

    /// <summary>
    ///     Excludes <paramref name="patterns" /> under <paramref name="root" /> until the returned
    ///     scope is disposed. A root that is a file anchors patterns at its directory.
    /// </summary>
    /// <exception cref="ArgumentException">A pattern is empty or uses negation (<c>!</c>), which is not supported.</exception>
    public static IDisposable Apply(string root, IEnumerable<string>? patterns)
    {
        var compiled = (patterns ?? []).Select(Compile).ToList();
        var previous = CurrentScope.Value;
        if (compiled.Count > 0)
        {
            var fullRoot = Path.GetFullPath(root);
            if (File.Exists(fullRoot))
            {
                fullRoot = Path.GetDirectoryName(fullRoot)!;
            }
            CurrentScope.Value = new PathExclusions(fullRoot, compiled);
        }

        return new Scope(previous);
    }

    /// <summary>True when the active scope excludes <paramref name="path" /> or one of its parent directories.</summary>
    internal static bool IsExcluded(string path, bool isDirectory)
        => CurrentScope.Value is { } scope && scope.Matches(path, isDirectory);

    private bool Matches(string path, bool isDirectory)
    {
        var relativePath = Path.GetRelativePath(_root, Path.GetFullPath(path));
        if (relativePath == "." || relativePath.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relativePath))
        {
            return false;
        }

        var segments = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var prefix = new StringBuilder();
        for (var index = 0; index < segments.Length; index++)
        {
            if (index > 0)
            {
                prefix.Append('/');
            }
            prefix.Append(segments[index]);
            var prefixIsDirectory = index < segments.Length - 1 || isDirectory;
            var candidate = prefix.ToString();
            if (_patterns.Any(entry => (prefixIsDirectory || !entry.DirectoriesOnly) && entry.Pattern.IsMatch(candidate)))
            {
                return true;
            }
        }

        return false;
    }

    private static (Regex Pattern, bool DirectoriesOnly) Compile(string pattern)
    {
        var glob = pattern.Trim().Replace('\\', '/');
        if (glob.StartsWith('!'))
        {
            throw new ArgumentException($"Exclude pattern '{pattern}' uses negation, which is not supported.", nameof(pattern));
        }
        while (glob.StartsWith("./", StringComparison.Ordinal))
        {
            glob = glob[2..];
        }
        var anchored = glob.StartsWith('/');
        glob = glob.TrimStart('/');
        var directoriesOnly = glob.EndsWith('/');
        glob = glob.TrimEnd('/');
        if (glob.Length == 0)
        {
            throw new ArgumentException($"Exclude pattern '{pattern}' is empty.", nameof(pattern));
        }
        if (!anchored && !glob.Contains('/'))
        {
            glob = "**/" + glob;
        }

        var regex = new StringBuilder("^");
        for (var index = 0; index < glob.Length; index++)
        {
            var character = glob[index];
            if (character == '*' && index + 1 < glob.Length && glob[index + 1] == '*')
            {
                var atSegmentStart = index == 0 || glob[index - 1] == '/';
                var atSegmentEnd = index + 2 == glob.Length || glob[index + 2] == '/';
                if (atSegmentStart && atSegmentEnd)
                {
                    if (index + 2 == glob.Length)
                    {
                        // A trailing "/**" also matches the directory itself, so the walk can
                        // prune it instead of visiting every file beneath it.
                        if (regex.Length > 1 && regex[^1] == '/')
                        {
                            regex.Length--;
                            regex.Append("(?:/.*)?");
                        }
                        else
                        {
                            regex.Append(".*");
                        }
                    }
                    else
                    {
                        regex.Append("(?:.*/)?");
                        index++; // the '/' after "**"
                    }
                    index++;
                    continue;
                }
            }

            regex.Append(character switch
            {
                '*' => "[^/]*",
                '?' => "[^/]",
                _ => Regex.Escape(character.ToString())
            });
        }
        regex.Append('$');

        var options = RegexOptions.CultureInvariant | (OperatingSystem.IsLinux() ? RegexOptions.None : RegexOptions.IgnoreCase);
        return (new Regex(regex.ToString(), options), directoriesOnly);
    }

    private sealed class Scope(PathExclusions? previous) : IDisposable
    {
        public void Dispose() => CurrentScope.Value = previous;
    }
}
