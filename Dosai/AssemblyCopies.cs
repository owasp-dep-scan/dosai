using System.Buffers.Binary;
using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
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
///         A ReadyToRun image (<c>PublishReadyToRun</c>, the default of a self-contained publish)
///         is not byte-identical to the IL build it was compiled from, though it carries that
///         build's metadata and IL unchanged, module version id included: the compiler adds native
///         code and lays the IL bodies and field data out anew. Distinct files that still share a
///         module version id are therefore compared by what the IL passes read
///         (<see cref="IlContentHash" />), and files whose IL content matches are one assembly too.
///         Only IL-only files and ReadyToRun images take part; a mixed-mode file's native code is
///         its own.
///     </para>
///     <para>
///         The analyzed copy is ranked by what the pass reads from it: a PDB beside it (source
///         locations), then a place inside the directory of a project that builds it from source
///         (its own output: purls resolve in that project's packages), then an IL-only file over a
///         ReadyToRun image of it, then the first in tree order (path segment by segment, so
///         <c>App/</c> sorts before <c>App.Tests/</c>). The choice depends on the files alone,
///         never on the order the file system listed them in.
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

    /// <summary>
    ///     One assembly found in several files: the copy analyzed, the others in tree order, and
    ///     those of them that are not byte-identical to it but carry its metadata and IL.
    /// </summary>
    internal sealed record CopyGroup(string Analyzed, IReadOnlyList<string> Copies, IReadOnlyList<string> SameIlCopies);

    /// <summary>The managed assemblies to analyze, in the order they were given: each distinct file once.</summary>
    public IReadOnlyList<string> Distinct { get; }

    /// <summary>The assemblies that had copies, in the order of their analyzed copy.</summary>
    public IReadOnlyList<CopyGroup> Groups { get; }

    /// <summary>How many files were left out as copies of an analyzed one.</summary>
    public int SkippedCopies => Groups.Sum(group => group.Copies.Count);

    /// <summary>
    ///     Every file of a group with copies, by full path, mapped to the full path of the copy
    ///     analyzed in its place (the analyzed copy maps to itself). Records read from a copy - the
    ///     methods inventory reads one copy per assembly identity, whichever it meets first - belong
    ///     to the analyzed copy: the files carry the same metadata, so tokens agree.
    /// </summary>
    public Dictionary<string, string> AnalyzedByFullPath()
    {
        var analyzedByFullPath = new Dictionary<string, string>(SafeFileRead.PathComparer);
        foreach (var group in Groups)
        {
            if (!TryGetFullPath(group.Analyzed, out var analyzed))
            {
                continue;
            }

            analyzedByFullPath[analyzed] = analyzed;
            foreach (var copy in group.Copies)
            {
                if (TryGetFullPath(copy, out var fullCopy))
                {
                    analyzedByFullPath[fullCopy] = analyzed;
                }
            }
        }

        return analyzedByFullPath;
    }

    private static bool TryGetFullPath(string path, out string fullPath)
    {
        try
        {
            fullPath = Path.GetFullPath(path);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            fullPath = string.Empty;
            return false;
        }
    }

    /// <param name="root">The scan root: projects under it decide which copy is a project's own output.</param>
    /// <param name="assemblyPaths">Candidate files; files that are not managed assemblies are dropped, as the IL passes always skipped them.</param>
    public static AssemblyCopies Collapse(string root, IReadOnlyList<string> assemblyPaths)
    {
        // The first open of a file is the slow part on a machine with real-time scanning, and
        // the probe is a pure function of the file: it runs on the worker team, by index.
        var workers = Math.Max(1, Dosai.MaxSymbolAnalysisWorkers);
        var probes = new FileProbe[assemblyPaths.Count];
        DedicatedStack.ForEach("Dosai assembly copies", workers, probes.Length, index => probes[index] = Probe(assemblyPaths[index]));

        // Byte classes: files of one length and module version id whose SHA-256 matches. A
        // class is named by its first member; every other file is a class of its own.
        var byteClass = new int[probes.Length];
        var candidatesByShape = new Dictionary<(long Length, Guid Mvid), List<int>>();
        for (var index = 0; index < probes.Length; index++)
        {
            byteClass[index] = index;
            if (probes[index] is { IsManaged: true, Mvid: { } mvid } probe)
            {
                ref var members = ref CollectionsMarshal.GetValueRefOrAddDefault(candidatesByShape, (probe.Length, mvid), out _);
                (members ??= []).Add(index);
            }
        }

        var toHash = candidatesByShape.Values.Where(members => members.Count > 1).SelectMany(members => members).Order().ToArray();
        var hashes = new string?[probes.Length];
        DedicatedStack.ForEach("Dosai assembly copies", workers, toHash.Length, item => hashes[toHash[item]] = Hash(assemblyPaths[toHash[item]]));
        foreach (var members in candidatesByShape.Values.Where(members => members.Count > 1))
        {
            foreach (var sameBytes in members.Where(index => hashes[index] is not null).GroupBy(index => hashes[index]!, StringComparer.Ordinal))
            {
                var first = sameBytes.Min();
                foreach (var index in sameBytes)
                {
                    byteClass[index] = first;
                }
            }
        }

        // IL classes: byte classes whose files share a module version id and carry the same
        // metadata and IL (a ReadyToRun image and its IL build). Only files that share an id
        // with a file of other bytes are read for it, so a tree without such files pays nothing.
        var ilClass = (int[])byteClass.Clone();
        var classesByMvid = new Dictionary<Guid, List<int>>();
        for (var index = 0; index < probes.Length; index++)
        {
            if (byteClass[index] == index && probes[index] is { IsManaged: true, Mvid: { } mvid, IsIlComparable: true })
            {
                ref var classes = ref CollectionsMarshal.GetValueRefOrAddDefault(classesByMvid, mvid, out _);
                (classes ??= []).Add(index);
            }
        }

        var toCompare = classesByMvid.Values.Where(classes => classes.Count > 1).SelectMany(classes => classes).Order().ToArray();
        var ilHashes = new string?[probes.Length];
        DedicatedStack.ForEach("Dosai assembly copies", workers, toCompare.Length, item => ilHashes[toCompare[item]] = IlContentHash(assemblyPaths[toCompare[item]]));
        foreach (var classes in classesByMvid.Values.Where(classes => classes.Count > 1))
        {
            foreach (var sameIl in classes.Where(index => ilHashes[index] is not null).GroupBy(index => ilHashes[index]!, StringComparer.Ordinal))
            {
                var first = sameIl.Min();
                foreach (var representative in sameIl)
                {
                    ilClass[representative] = first;
                }
            }
        }

        var membersByClass = new Dictionary<int, List<int>>();
        for (var index = 0; index < probes.Length; index++)
        {
            if (probes[index].IsManaged)
            {
                var name = ilClass[byteClass[index]];
                ref var members = ref CollectionsMarshal.GetValueRefOrAddDefault(membersByClass, name, out _);
                (members ??= []).Add(index);
            }
        }

        var analyzedByCopy = new Dictionary<int, int>();
        var copiesByAnalyzed = new Dictionary<int, List<int>>();
        foreach (var members in membersByClass.Values.Where(members => members.Count > 1))
        {
            var ranked = members
                .Select(index => (Index: index, Path: Path.GetFullPath(assemblyPaths[index]), HasPdb: HasPdbBeside(assemblyPaths[index]), OwnOutput: IsInOwnProject(root, assemblyPaths[index]), probes[index].IsIlOnly))
                .OrderByDescending(copy => copy.HasPdb)
                .ThenByDescending(copy => copy.OwnOutput)
                .ThenByDescending(copy => copy.IsIlOnly)
                .ThenBy(copy => copy.Path, TreeOrder.Instance)
                .Select(copy => copy.Index)
                .ToList();
            copiesByAnalyzed[ranked[0]] = ranked.Skip(1).OrderBy(copy => Path.GetFullPath(assemblyPaths[copy]), TreeOrder.Instance).ToList();
            foreach (var copy in ranked.Skip(1))
            {
                analyzedByCopy[copy] = ranked[0];
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
                var sameIl = copies.Where(copy => byteClass[copy] != byteClass[index]).Select(copy => assemblyPaths[copy]).ToList();
                groups.Add(new CopyGroup(assemblyPaths[index], copies.ConvertAll(copy => assemblyPaths[copy]), sameIl));
            }
        }

        var result = new AssemblyCopies(distinct, groups);
        if (DebugLog.Enabled && result.SkippedCopies > 0)
        {
            DebugLog.Log($"assembly copies: {result.SkippedCopies} cop(ies) of {groups.Count} assembl(ies) left out of the IL pass; {toHash.Length} file(s) hashed, {toCompare.Length} compared by IL content");
            foreach (var group in groups)
            {
                var byteCopies = group.Copies.Except(group.SameIlCopies, StringComparer.Ordinal).ToList();
                DebugLog.Log($"assembly copies: analyzed {group.Analyzed}; byte-identical: {(byteCopies.Count > 0 ? string.Join(" | ", byteCopies) : "none")}; same IL: {(group.SameIlCopies.Count > 0 ? string.Join(" | ", group.SameIlCopies) : "none")}");
            }
        }

        return result;
    }

    /// <summary>One line naming the copies left out and the copy analyzed for each, relative to <paramref name="root" />; null when there were none.</summary>
    /// <param name="root">The scan root.</param>
    /// <param name="reportedOnce">What the pass now reports once (<c>each flow through them is reported once</c>).</param>
    public string? Diagnostic(string root, string reportedOnce)
    {
        if (Groups.Count == 0)
        {
            return null;
        }

        var baseDirectory = Directory.Exists(root) ? root : Path.GetDirectoryName(root) ?? root;
        string Relative(string path) => Path.GetRelativePath(baseDirectory, Path.GetDirectoryName(path) ?? path).Replace('\\', '/');
        static string List(IReadOnlyList<string> paths, Func<string, string> relative)
        {
            var named = string.Join(", ", paths.Take(MaxCopiesPerGroupInDiagnostic).Select(relative));
            return paths.Count > MaxCopiesPerGroupInDiagnostic ? string.Create(CultureInfo.InvariantCulture, $"{named} and {paths.Count - MaxCopiesPerGroupInDiagnostic} more") : named;
        }

        var detail = string.Join("; ", Groups.Take(MaxGroupsInDiagnostic).Select(group =>
        {
            var byteCopies = group.Copies.Except(group.SameIlCopies, StringComparer.Ordinal).ToList();
            var parts = new List<string>(2);
            if (byteCopies.Count > 0) parts.Add($"copies in {List(byteCopies, Relative)}");
            if (group.SameIlCopies.Count > 0) parts.Add($"same IL in {List(group.SameIlCopies, Relative)}");
            return $"{Path.GetFileName(group.Analyzed)} analyzed in {Relative(group.Analyzed)} ({string.Join("; ", parts)})";
        }));
        var moreGroups = Groups.Count > MaxGroupsInDiagnostic ? string.Create(CultureInfo.InvariantCulture, $"; and {Groups.Count - MaxGroupsInDiagnostic} more assemblies") : string.Empty;
        var what = Groups.Any(group => group.SameIlCopies.Count > 0)
            ? "are copies of another file in the tree, byte-identical or with the same metadata and IL (such as a ReadyToRun image of the same build),"
            : "are byte-identical copies of another file in the tree";
        return string.Create(CultureInfo.InvariantCulture, $"{SkippedCopies} assembly file(s) {what} and were not analyzed again, so {reportedOnce}: {detail}{moreGroups}.");
    }

    /// <param name="IsIlOnly">The CLI header's IL-only flag: no native code in the file.</param>
    /// <param name="IsIlComparable">IL-only or a ReadyToRun image, so the IL is all of its managed code.</param>
    private readonly record struct FileProbe(bool IsManaged, long Length, Guid? Mvid, bool IsIlOnly, bool IsIlComparable);

    /// <summary>The <c>READYTORUN_HEADER</c> signature, <c>RTR</c>.</summary>
    private const uint ReadyToRunSignature = 0x00525452;

    /// <summary>Whether the file is a managed assembly (the IL passes' old check, unchanged), with its length, module version id and kind of code.</summary>
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

            var corHeader = peReader.PEHeaders.CorHeader;
            var ilOnly = (corHeader.Flags & CorFlags.ILOnly) != 0;
            return new FileProbe(true, stream.Length, mvid, ilOnly, ilOnly || IsReadyToRunImage(peReader, corHeader));
        }
        catch
        {
            return default;
        }
    }

    /// <summary>Whether the managed native header is a ReadyToRun header (its signature, not only its presence: NGen images used the slot too).</summary>
    private static bool IsReadyToRunImage(PEReader peReader, CorHeader corHeader)
    {
        var directory = corHeader.ManagedNativeHeaderDirectory;
        if (directory.Size < sizeof(uint))
        {
            return false;
        }

        try
        {
            return peReader.GetSectionData(directory.RelativeVirtualAddress).GetReader(0, sizeof(uint)).ReadUInt32() == ReadyToRunSignature;
        }
        catch (Exception ex) when (ex is BadImageFormatException or ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    /// <summary>
    ///     A hash of what the IL passes read from the file, and of the rest of its managed content:
    ///     the metadata (with the columns that locate method bodies and field data zeroed, the only
    ///     part a ReadyToRun compiler changes), every method body in metadata order, the data of
    ///     every field with an RVA, the managed resources and the entry point. Null when any part
    ///     cannot be read or sized; the file then joins byte-identical copies only.
    /// </summary>
    private static string? IlContentHash(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var peReader = new PEReader(stream);
            if (peReader is not { HasMetadata: true, PEHeaders.CorHeader: { } corHeader })
            {
                return null;
            }

            var reader = peReader.GetMetadataReader();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var block = peReader.GetMetadata();
            var metadata = new byte[block.Length];
            block.GetReader().ReadBytes(block.Length, metadata, 0);
            ZeroRvaColumn(reader, metadata, TableIndex.MethodDef);
            ZeroRvaColumn(reader, metadata, TableIndex.FieldRva);
            hash.AppendData(metadata);

            Span<byte> number = stackalloc byte[sizeof(int)];
            void AppendNumber(int value, Span<byte> scratch)
            {
                BinaryPrimitives.WriteInt32LittleEndian(scratch, value);
                hash.AppendData(scratch);
            }

            var buffer = new byte[256];
            void AppendBytes(int rva, int size, Span<byte> scratch)
            {
                // The length first, so two concatenations of different parts never hash alike.
                AppendNumber(size, scratch);
                if (size == 0)
                {
                    return;
                }

                if (buffer.Length < size)
                {
                    buffer = new byte[Math.Max(size, buffer.Length * 2)];
                }

                peReader.GetSectionData(rva).GetReader(0, size).ReadBytes(size, buffer, 0);
                hash.AppendData(buffer, 0, size);
            }

            // A ReadyToRun image keeps the IL entry point token (a native entry point is
            // mixed-mode code, which never gets here).
            AppendNumber(corHeader.EntryPointTokenOrRelativeVirtualAddress, number);
            foreach (var handle in reader.MethodDefinitions)
            {
                var rva = reader.GetMethodDefinition(handle).RelativeVirtualAddress;
                AppendBytes(rva, rva == 0 ? 0 : peReader.GetMethodBody(rva).Size, number);
            }

            foreach (var handle in reader.FieldDefinitions)
            {
                var field = reader.GetFieldDefinition(handle);
                var rva = field.GetRelativeVirtualAddress();
                if (rva == 0)
                {
                    continue;
                }

                if (FieldDataSize(reader, field) is not { } size)
                {
                    return null;
                }

                AppendBytes(rva, size, number);
            }

            var resources = corHeader.ResourcesDirectory;
            AppendBytes(resources.RelativeVirtualAddress, resources.Size, number);
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException or ArgumentOutOfRangeException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Zeroes the 4-byte RVA that leads every row of a <c>MethodDef</c> or <c>FieldRVA</c> table (ECMA-335 II.22.26, II.22.18).</summary>
    private static void ZeroRvaColumn(MetadataReader reader, byte[] metadata, TableIndex table)
    {
        var rows = reader.GetTableRowCount(table);
        if (rows == 0)
        {
            return;
        }

        var offset = reader.GetTableMetadataOffset(table);
        var rowSize = reader.GetTableRowSize(table);
        for (var row = 0; row < rows; row++)
        {
            metadata.AsSpan(offset + row * rowSize, sizeof(uint)).Clear();
        }
    }

    /// <summary>
    ///     The size of a field's RVA data, from its type: a primitive, or a value type of this
    ///     module with an explicit size (<c>__StaticArrayInitTypeSize=N</c>); null for any other
    ///     type, whose data size the metadata does not state.
    /// </summary>
    private static int? FieldDataSize(MetadataReader reader, FieldDefinition field)
    {
        var signature = reader.GetBlobReader(field.Signature);
        if (signature.ReadSignatureHeader().Kind != SignatureKind.Field)
        {
            return null;
        }

        var typeCode = signature.ReadSignatureTypeCode();
        while (typeCode is SignatureTypeCode.RequiredModifier or SignatureTypeCode.OptionalModifier)
        {
            signature.ReadTypeHandle();
            typeCode = signature.ReadSignatureTypeCode();
        }

        return typeCode switch
        {
            SignatureTypeCode.Boolean or SignatureTypeCode.SByte or SignatureTypeCode.Byte => 1,
            SignatureTypeCode.Char or SignatureTypeCode.Int16 or SignatureTypeCode.UInt16 => 2,
            SignatureTypeCode.Int32 or SignatureTypeCode.UInt32 or SignatureTypeCode.Single => 4,
            SignatureTypeCode.Int64 or SignatureTypeCode.UInt64 or SignatureTypeCode.Double => 8,
            SignatureTypeCode.TypeHandle when signature.ReadTypeHandle() is { Kind: HandleKind.TypeDefinition } type
                && reader.GetTypeDefinition((TypeDefinitionHandle)type).GetLayout() is { Size: > 0 } layout => layout.Size,
            _ => null
        };
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
