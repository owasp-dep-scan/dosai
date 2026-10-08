using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace Depscan;

/// <summary>
///     The distinct managed assemblies among the files an IL pass is about to analyze: byte-identical
///     copies of one file collapse to a single analyzed copy (issue #83).
/// </summary>
/// <remarks>
///     <para>
///         A build copies each referenced project's assembly - and, in executable and test projects
///         (<c>CopyLocalLockFileAssemblies</c>), each package's - into the output of every project
///         that references it, so a library, an app and a test project leave three copies of the
///         library under one root. The data-flow IL pass analyzed every copy and reported each flow
///         through it once per copy, with nodes that differed only in their id; <c>crypto</c>'s data
///         flows run the same pass.
///     </para>
///     <para>
///         Copies are found by content. Files of one length and one module version id (read from
///         the metadata the managed-assembly probe opens anyway) are hashed, and only files whose
///         SHA-256 also matches are one assembly: the id alone is not enough, since a patched or
///         tampered file keeps it. Distinct builds - Debug and Release, two target frameworks - are
///         distinct files and are all analyzed. Only files that share a length and an id are read
///         whole, and those are exactly the copies the pass no longer analyzes.
///     </para>
///     <para>
///         The analyzed copy is ranked by what the pass reads from it: a PDB beside it (source
///         locations), then a place inside the directory of a project that builds it from source
///         (its own output: purls resolve in that project's packages), then the first in tree order
///         (path segment by segment, so <c>App/</c> sorts before <c>App.Tests/</c>). The choice
///         depends on the paths alone, never on the order the file system listed them in.
///     </para>
/// </remarks>
internal sealed class AssemblyCopies
{
    private const int MaxGroupsInDiagnostic = 10;
    private const int MaxCopiesPerGroupInDiagnostic = 5;

    private AssemblyCopies(List<string> distinct, List<CopyGroup> groups)
    {
        Distinct = distinct;
        Groups = groups;
    }

    /// <summary>One assembly found in several files: the copy analyzed, and the others in tree order.</summary>
    internal sealed record CopyGroup(string Analyzed, IReadOnlyList<string> Copies);

    /// <summary>The managed assemblies to analyze, in the order they were given: each distinct file once.</summary>
    public IReadOnlyList<string> Distinct { get; }

    /// <summary>The assemblies that had copies, in the order of their analyzed copy.</summary>
    public IReadOnlyList<CopyGroup> Groups { get; }

    /// <summary>How many files were left out as copies of an analyzed one.</summary>
    public int SkippedCopies => Groups.Sum(group => group.Copies.Count);

    /// <param name="root">The scan root: projects under it decide which copy is a project's own output.</param>
    /// <param name="assemblyPaths">Candidate files; files that are not managed assemblies are dropped, as the IL passes always skipped them.</param>
    public static AssemblyCopies Collapse(string root, IReadOnlyList<string> assemblyPaths)
    {
        // The first open of a file is the slow part on a machine with real-time scanning, and
        // the probe is a pure function of the file: it runs on the worker team, by index.
        var probes = new FileProbe[assemblyPaths.Count];
        DedicatedStack.ForEach("Dosai assembly copies", Math.Max(1, Dosai.MaxSymbolAnalysisWorkers), probes.Length, index => probes[index] = Probe(assemblyPaths[index]));

        var candidatesByShape = new Dictionary<(long Length, Guid Mvid), List<int>>();
        for (var index = 0; index < probes.Length; index++)
        {
            if (probes[index] is { IsManaged: true, Mvid: { } mvid } probe)
            {
                ref var members = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(candidatesByShape, (probe.Length, mvid), out _);
                (members ??= []).Add(index);
            }
        }

        var toHash = candidatesByShape.Values.Where(members => members.Count > 1).SelectMany(members => members).Order().ToArray();
        var hashes = new string?[probes.Length];
        DedicatedStack.ForEach("Dosai assembly copies", Math.Max(1, Dosai.MaxSymbolAnalysisWorkers), toHash.Length, item => hashes[toHash[item]] = Hash(assemblyPaths[toHash[item]]));

        var analyzedByCopy = new Dictionary<int, int>();
        var copiesByAnalyzed = new Dictionary<int, List<int>>();
        foreach (var members in candidatesByShape.Values.Where(members => members.Count > 1))
        {
            foreach (var sameContent in members.Where(index => hashes[index] is not null).GroupBy(index => hashes[index]!, StringComparer.Ordinal).Where(group => group.Count() > 1))
            {
                var ranked = sameContent
                    .Select(index => (Index: index, Path: assemblyPaths[index], HasPdb: HasPdbBeside(assemblyPaths[index]), OwnOutput: IsInOwnProject(root, assemblyPaths[index])))
                    .OrderByDescending(copy => copy.HasPdb)
                    .ThenByDescending(copy => copy.OwnOutput)
                    .ThenBy(copy => Path.GetFullPath(copy.Path), TreeOrder.Instance)
                    .Select(copy => copy.Index)
                    .ToList();
                copiesByAnalyzed[ranked[0]] = ranked.Skip(1).OrderBy(copy => Path.GetFullPath(assemblyPaths[copy]), TreeOrder.Instance).ToList();
                foreach (var copy in ranked.Skip(1))
                {
                    analyzedByCopy[copy] = ranked[0];
                }
            }
        }

        var distinct = new List<string>();
        var groups = new List<CopyGroup>();
        for (var index = 0; index < probes.Length; index++)
        {
            if (!probes[index].IsManaged || analyzedByCopy.ContainsKey(index))
            {
                continue;
            }

            distinct.Add(assemblyPaths[index]);
            if (copiesByAnalyzed.TryGetValue(index, out var copies))
            {
                groups.Add(new CopyGroup(assemblyPaths[index], copies.ConvertAll(copy => assemblyPaths[copy])));
            }
        }

        var result = new AssemblyCopies(distinct, groups);
        if (DebugLog.Enabled && result.SkippedCopies > 0)
        {
            DebugLog.Log($"assembly copies: {result.SkippedCopies} byte-identical cop(ies) of {groups.Count} assembl(ies) left out of the IL pass; {toHash.Length} file(s) hashed");
            foreach (var group in groups)
            {
                DebugLog.Log($"assembly copies: analyzed {group.Analyzed}; left out {string.Join(" | ", group.Copies)}");
            }
        }

        return result;
    }

    /// <summary>One line naming the copies left out and the copy analyzed for each, relative to <paramref name="root" />; null when there were none.</summary>
    public string? Diagnostic(string root)
    {
        if (Groups.Count == 0)
        {
            return null;
        }

        var baseDirectory = Directory.Exists(root) ? root : Path.GetDirectoryName(root) ?? root;
        string Relative(string path) => Path.GetRelativePath(baseDirectory, Path.GetDirectoryName(path) ?? path).Replace('\\', '/');
        var detail = string.Join("; ", Groups.Take(MaxGroupsInDiagnostic).Select(group =>
        {
            var copies = string.Join(", ", group.Copies.Take(MaxCopiesPerGroupInDiagnostic).Select(Relative));
            var more = group.Copies.Count > MaxCopiesPerGroupInDiagnostic ? string.Create(CultureInfo.InvariantCulture, $" and {group.Copies.Count - MaxCopiesPerGroupInDiagnostic} more") : string.Empty;
            return $"{Path.GetFileName(group.Analyzed)} analyzed in {Relative(group.Analyzed)} (copies in {copies}{more})";
        }));
        var moreGroups = Groups.Count > MaxGroupsInDiagnostic ? string.Create(CultureInfo.InvariantCulture, $"; and {Groups.Count - MaxGroupsInDiagnostic} more assemblies") : string.Empty;
        return string.Create(CultureInfo.InvariantCulture, $"{SkippedCopies} assembly file(s) are byte-identical copies of another file in the tree and were not analyzed again, so each flow through them is reported once: {detail}{moreGroups}.");
    }

    private readonly record struct FileProbe(bool IsManaged, long Length, Guid? Mvid);

    /// <summary>Whether the file is a managed assembly (the IL passes' old check, unchanged), with its length and module version id.</summary>
    private static FileProbe Probe(string path)
    {
        try
        {
            // FileShare.Delete matches the other metadata readers: probing a file must never
            // stop the owning build from replacing or deleting it.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var peReader = new PEReader(stream);
            if (peReader is not { HasMetadata: true, PEHeaders.CorHeader: not null })
            {
                return default;
            }

            Guid? mvid;
            try
            {
                var reader = peReader.GetMetadataReader();
                mvid = reader.GetGuid(reader.GetModuleDefinition().Mvid);
            }
            catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException)
            {
                // Still analyzed (and still reported by the pass when it fails there); it only
                // takes no part in copy detection.
                mvid = null;
            }

            return new FileProbe(true, stream.Length, mvid);
        }
        catch
        {
            return default;
        }
    }

    /// <summary>The file's SHA-256, or null when it cannot be read (the file is then analyzed on its own).</summary>
    private static string? Hash(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The IL passes read a portable PDB from beside the assembly only.</summary>
    private static bool HasPdbBeside(string path) => File.Exists(Path.ChangeExtension(path, ".pdb"));

    /// <summary>Whether the file lies inside the directory of a project that builds an assembly of its name.</summary>
    private static bool IsInOwnProject(string root, string path)
    {
        var fullPath = Path.GetFullPath(path);
        foreach (var projectDirectory in TreeFrameworks.ProjectDirectoriesBuilding(root, Path.GetFileNameWithoutExtension(path)))
        {
            if (fullPath.StartsWith(Path.TrimEndingDirectorySeparator(projectDirectory) + Path.DirectorySeparatorChar, SafeFileRead.PathComparison))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Paths compared segment by segment, ordinally: a directory sorts before its longer
    ///     siblings (<c>App/</c> before <c>App.Tests/</c>) and a path before the paths under it, as
    ///     a tree listing reads, on every platform.
    /// </summary>
    internal sealed class TreeOrder : IComparer<string>
    {
        public static TreeOrder Instance { get; } = new();

        public int Compare(string? x, string? y)
        {
            if (x is null || y is null)
            {
                return x is null ? y is null ? 0 : -1 : 1;
            }

            var left = x.AsSpan();
            var right = y.AsSpan();
            while (true)
            {
                var leftEnd = left.IndexOfAny('/', '\\');
                var rightEnd = right.IndexOfAny('/', '\\');
                var segment = (leftEnd < 0 ? left : left[..leftEnd]).CompareTo(rightEnd < 0 ? right : right[..rightEnd], StringComparison.Ordinal);
                if (segment != 0)
                {
                    return segment;
                }

                if (leftEnd < 0 || rightEnd < 0)
                {
                    return (leftEnd < 0 ? 0 : 1) - (rightEnd < 0 ? 0 : 1);
                }

                left = left[(leftEnd + 1)..];
                right = right[(rightEnd + 1)..];
            }
        }
    }
}
