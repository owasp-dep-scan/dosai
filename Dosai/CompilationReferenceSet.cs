using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;

namespace Depscan;

/// <summary>
///     The metadata references of one source compilation: the tree's framework references, then
///     the managed assemblies under the tree (build output), then the restore output's package
///     assemblies in the NuGet cache - with exactly one reference per assembly simple name
///     (issue #81).
/// </summary>
/// <remarks>
///     <para>
///         Two references with one simple name make Roslyn see every type twice: a call into
///         either does not bind. A built tree routinely holds such a pair - a package that ships
///         a newer copy of a shared-framework assembly
///         (<c>Microsoft.Extensions.Logging.Abstractions</c> 9.0.10 in a net8.0 web app) is copied
///         to <c>bin/</c> beside the reference pack that already has it, and two projects'
///         outputs hold two versions of one package.
///     </para>
///     <para>
///         The highest assembly version wins, which is what the SDK's conflict resolution
///         (<c>ResolvePackageFileConflicts</c>) compiles the project against: the package copy
///         over an older framework copy, the framework over an older package copy. Equal
///         versions keep the earlier claim - framework, then build output, then restore output -
///         and two build-output copies of one version keep the ordinally smaller path, so the
///         choice never depends on the file system's enumeration order. Versions are read only
///         when a name is claimed twice, from the assembly header, and a losing candidate never
///         becomes a reference. Two build-output files of one version with other content are two
///         assemblies, not two copies of one, and that choice is a diagnostic.
///     </para>
/// </remarks>
internal sealed class CompilationReferenceSet
{
    private const int MaxNamesInDiagnostic = 10;

    /// <summary>Claim rank on equal versions: lower claims first and keeps the name.</summary>
    private enum Origin
    {
        Framework = 0,
        BuildOutput = 1,
        RestoreCache = 2
    }

    private sealed class Entry(string key, PortableExecutableReference reference, Origin origin)
    {
        public string Key { get; set; } = key;
        public PortableExecutableReference Reference { get; set; } = reference;
        public Origin Origin { get; set; } = origin;

        public Version? Version { get; set; }
        public bool VersionRead { get; set; }
    }

    // Every kept reference in claim order: Roslyn enumerates a namespace's members in reference
    // order, so the order is the one the dictionary this replaced kept (framework, then the
    // tree in discovery order, then the cache), and a replaced reference keeps its slot.
    private readonly List<Entry> _slots = [];
    private readonly Dictionary<string, Entry> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _offered = new(StringComparer.OrdinalIgnoreCase);
    private readonly SortedDictionary<string, string> _replacedFramework = new(StringComparer.OrdinalIgnoreCase);
    private readonly SortedDictionary<string, string> _droppedOlder = new(StringComparer.OrdinalIgnoreCase);
    private readonly SortedDictionary<string, string> _equalVersionTies = new(StringComparer.OrdinalIgnoreCase);
    private int _droppedOlderCount;
    private int _equalVersionTieCount;
    private int _droppedQuietly;

    /// <param name="framework">The framework references, in their claim order (<see cref="FrameworkReferences.ForTree" />).</param>
    public CompilationReferenceSet(IEnumerable<(string Key, PortableExecutableReference Reference)> framework)
    {
        foreach (var (key, reference) in framework)
        {
            if (!_offered.Add(key))
            {
                continue;
            }

            var entry = new Entry(key, reference, Origin.Framework);
            _slots.Add(entry);
            // The framework set is already one per name; a repeat (none today) stays as it was.
            var name = SimpleName(key);
            if (name.Length > 0)
            {
                _byName.TryAdd(name, entry);
            }
        }
    }

    /// <summary>Every path offered so far, kept or not.</summary>
    public IReadOnlyCollection<string> OfferedPaths => _offered;

    /// <summary>Offers a managed assembly found under the scanned tree (build output).</summary>
    public void AddBuildOutput(string path, Func<string, PortableExecutableReference?> create) => Offer(path, Origin.BuildOutput, create);

    /// <summary>Offers a package assembly the tree's restore output names in the NuGet cache.</summary>
    public void AddRestoreCache(string path, Func<string, PortableExecutableReference?> create) => Offer(path, Origin.RestoreCache, create);

    /// <summary>The references in claim order (see <see cref="_slots" />).</summary>
    public List<PortableExecutableReference> References() => _slots.ConvertAll(entry => entry.Reference);

    /// <summary>
    ///     The kept references the tree's build or restore output gave, by simple name with their
    ///     file: one copy of each assembly for the whole tree, which can be another project's
    ///     version of a package (<see cref="PackageUrlResolver.UnionBindingDiagnostic" />).
    /// </summary>
    public IEnumerable<(string Name, string Path)> TreeBindings() =>
        _slots.Where(entry => entry.Origin != Origin.Framework).Select(entry => (SimpleName(entry.Key), entry.Key));

    /// <summary>
    ///     The slice diagnostics: framework references a newer tree copy replaced, and older build
    ///     output copies that were left out. Equal-version copies (the same assembly) and older
    ///     restore-cache copies (the cache was always deduplicated against the rest) only reach
    ///     the debug log.
    /// </summary>
    public List<string> Diagnostics()
    {
        var diagnostics = new List<string>();
        if (_replacedFramework.Count > 0)
        {
            diagnostics.Add(string.Create(CultureInfo.InvariantCulture, $"{_replacedFramework.Count} framework reference(s) were replaced by a higher-version copy from the tree's build or restore output ({Summarize(_replacedFramework, _replacedFramework.Count)}): one reference per assembly name, the highest version wins, as the SDK's conflict resolution compiles the project."));
        }

        if (_droppedOlderCount > 0)
        {
            diagnostics.Add(string.Create(CultureInfo.InvariantCulture, $"{_droppedOlderCount} build-output assembly reference(s) were left out of the source compilation because a higher version of the same assembly is already referenced ({Summarize(_droppedOlder, _droppedOlder.Count)}): two references of one name make every call into it ambiguous."));
        }

        if (_equalVersionTieCount > 0)
        {
            diagnostics.Add(string.Create(CultureInfo.InvariantCulture, $"{_equalVersionTieCount} build-output assembly reference(s) of the same name and assembly version as a kept one, but other content, were left out of the source compilation ({Summarize(_equalVersionTies, _equalVersionTies.Count)}): calls into that assembly bind against the kept file."));
        }

        if (_droppedQuietly > 0)
        {
            DebugLog.Count("compilation reference copies dropped quietly (same version, or an older restore-cache copy)", _droppedQuietly);
        }

        return diagnostics;

        static string Summarize(SortedDictionary<string, string> details, int count) =>
            string.Join(", ", details.Values.Take(MaxNamesInDiagnostic)) + (count > MaxNamesInDiagnostic ? ", ..." : string.Empty);
    }

    private void Offer(string path, Origin origin, Func<string, PortableExecutableReference?> create)
    {
        if (!_offered.Add(path))
        {
            return;
        }

        var name = SimpleName(path);
        if (name.Length == 0)
        {
            return;
        }

        if (!_byName.TryGetValue(name, out var existing))
        {
            if (create(path) is { } reference)
            {
                var entry = new Entry(path, reference, origin);
                _slots.Add(entry);
                _byName.Add(name, entry);
            }

            return;
        }

        var candidateVersion = ReadVersion(path);
        var existingVersion = VersionOf(existing);
        var comparison = Compare(candidateVersion, existingVersion);
        var wins = comparison > 0
                   || comparison == 0 && (origin < existing.Origin || origin == existing.Origin && string.CompareOrdinal(path, existing.Key) < 0);
        if (!wins)
        {
            if (comparison < 0 && origin == Origin.BuildOutput)
            {
                _droppedOlderCount++;
                _droppedOlder.TryAdd(name, $"{name} {Display(candidateVersion)} under {Display(existingVersion)}");
            }
            else if (!NoteEqualVersionTie(name, origin, existing, comparison, kept: existing.Key, dropped: path, candidateVersion))
            {
                _droppedQuietly++;
            }

            return;
        }

        if (create(path) is not { } winner)
        {
            return;
        }

        var tie = NoteEqualVersionTie(name, origin, existing, comparison, kept: path, dropped: existing.Key, candidateVersion);

        if (existing.Origin == Origin.Framework && comparison > 0)
        {
            _replacedFramework.TryAdd(name, $"{name} {Display(candidateVersion)} over {Display(existingVersion)}");
        }
        else if (comparison > 0 && existing.Origin == Origin.BuildOutput)
        {
            _droppedOlderCount++;
            _droppedOlder.TryAdd(name, $"{name} {Display(existingVersion)} under {Display(candidateVersion)}");
        }
        else if (!tie)
        {
            _droppedQuietly++;
        }

        // The slot is kept, so the name's position in the claim order survives.
        existing.Key = path;
        existing.Reference = winner;
        existing.Origin = origin;
        existing.Version = candidateVersion;
        existing.VersionRead = true;
    }

    /// <summary>
    ///     Records a tie between two build-output files of one name and assembly version whose
    ///     content differs: the ordinally smaller path is kept, which is a choice between two
    ///     assemblies, not two copies of one (two packages can ship a same-named, same-versioned
    ///     file). Copies of one file - the usual tie, one per referencing project's output - stay
    ///     quiet. Content is compared only on such a tie, length first.
    /// </summary>
    /// <returns>Whether the tie was recorded.</returns>
    private bool NoteEqualVersionTie(string name, Origin origin, Entry existing, int comparison, string kept, string dropped, Version? version)
    {
        // A build's reference assembly (obj/.../ref) beside its implementation is one assembly in
        // two shapes, not two assemblies.
        if (comparison != 0 || origin != Origin.BuildOutput || existing.Origin != Origin.BuildOutput || SameContent(kept, dropped) || IsReferenceAssembly(kept) || IsReferenceAssembly(dropped))
        {
            return false;
        }

        _equalVersionTieCount++;
        _equalVersionTies.TryAdd(name, $"{name} {Display(version)}: kept {kept}, left out {dropped}");
        return true;
    }

    /// <summary>Whether the assembly carries <c>ReferenceAssemblyAttribute</c> (metadata only, no implementation); an unreadable file counts as one, so it never adds a note.</summary>
    private static bool IsReferenceAssembly(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var peReader = new PEReader(stream);
            if (!peReader.HasMetadata)
            {
                return true;
            }

            var reader = peReader.GetMetadataReader();
            if (!reader.IsAssembly)
            {
                return true;
            }

            foreach (var handle in reader.GetAssemblyDefinition().GetCustomAttributes())
            {
                var constructor = reader.GetCustomAttribute(handle).Constructor;
                var type = constructor.Kind switch
                {
                    HandleKind.MemberReference => reader.GetMemberReference((MemberReferenceHandle)constructor).Parent,
                    HandleKind.MethodDefinition => reader.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType(),
                    _ => default
                };
                var typeName = type.Kind switch
                {
                    HandleKind.TypeReference => reader.GetString(reader.GetTypeReference((TypeReferenceHandle)type).Name),
                    HandleKind.TypeDefinition => reader.GetString(reader.GetTypeDefinition((TypeDefinitionHandle)type).Name),
                    _ => null
                };
                if (typeName == "ReferenceAssemblyAttribute")
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException)
        {
            return true;
        }
    }

    /// <summary>Whether two files hold the same bytes; an unreadable file counts as the same, so it never adds a note.</summary>
    private static bool SameContent(string left, string right)
    {
        try
        {
            using var leftStream = new FileStream(left, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var rightStream = new FileStream(right, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return leftStream.Length == rightStream.Length
                   && System.Security.Cryptography.SHA256.HashData(leftStream).AsSpan().SequenceEqual(System.Security.Cryptography.SHA256.HashData(rightStream));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>The file name without its assembly extension, as every reference builder keys names.</summary>
    private static string SimpleName(string path) => Path.GetFileNameWithoutExtension(path);

    /// <summary>A missing version (unreadable or not an assembly) loses to any version.</summary>
    private static int Compare(Version? left, Version? right) =>
        left is null ? right is null ? 0 : -1 : right is null ? 1 : left.CompareTo(right);

    private static string Display(Version? version) => version?.ToString() ?? "<unknown version>";

    private static Version? VersionOf(Entry entry)
    {
        if (!entry.VersionRead)
        {
            entry.Version = ReadVersion(entry.Reference) ?? ReadVersion(entry.Key);
            entry.VersionRead = true;
        }

        return entry.Version;
    }

    /// <summary>The assembly version from the file's metadata, opened without blocking the build that owns it.</summary>
    internal static Version? ReadVersion(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var peReader = new PEReader(stream);
            if (!peReader.HasMetadata)
            {
                return null;
            }

            var reader = peReader.GetMetadataReader();
            return reader.IsAssembly ? reader.GetAssemblyDefinition().Version : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The assembly version of an existing reference (in-memory bundled metadata included).</summary>
    private static Version? ReadVersion(PortableExecutableReference reference)
    {
        try
        {
            return reference.GetMetadata() is AssemblyMetadata assembly && assembly.GetModules() is { Length: > 0 } modules && modules[0].GetMetadataReader() is { IsAssembly: true } reader
                ? reader.GetAssemblyDefinition().Version
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }
}
