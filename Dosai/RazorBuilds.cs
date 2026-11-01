using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Depscan;

/// <summary>
///     The tree's own builds that hold calls no source file shows (issue #98). A <c>.razor</c> or
///     <c>.cshtml</c> template is not C#: the source analysis never reads it, and the build writes
///     the compiled component only into an assembly. The call graph leaves a source tree's
///     <c>bin/</c> to the source analysis (reading it whole lists nearly every <c>.cs</c> call site
///     a second time), so the assemblies that came from templates are picked out here and read for
///     the call sites their PDB places in a template, and for nothing else.
/// </summary>
/// <remarks>
///     A build qualifies when it is the application's own (<see cref="AssemblyScope.SplitAssemblyFiles" />
///     classifies it by the same rule as every other pass: nearest <c>*.deps.json</c>, restore
///     metadata, projects built from source) and its PDB names a template that the tree still
///     holds. A document is placed through <see cref="SourceDocumentPaths" />, which also resolves
///     a <c>/_/</c>-mapped or foreign-file-system path. A template the tree no longer holds (a
///     stale build) or one outside the scan root (a referenced project's build copied into this
///     one's output) gives no rows: a row on a file the scan does not have cannot be followed to
///     anything. Diagnostics count each case, and the templates changed after their build, by
///     distinct document.
/// </remarks>
internal static class RazorBuilds
{
    private static readonly AsyncLocal<bool> Off = new();

    /// <summary>
    ///     Turns the template pass off for the calling context until the scope is disposed, so a test
    ///     can compare a run against the same tree without it (the source call sites must not move).
    /// </summary>
    internal static IDisposable DisableForTesting()
    {
        var previous = Off.Value;
        Off.Value = true;
        return new Scope(previous);
    }

    private sealed class Scope(bool previous) : IDisposable
    {
        public void Dispose() => Off.Value = previous;
    }

    /// <summary>A path of a template file: <c>.razor</c> or <c>.cshtml</c>.</summary>
    public static bool IsTemplate(string? path) =>
        Path.GetExtension(path) is { } extension
        && (extension.Equals(".razor", StringComparison.OrdinalIgnoreCase) || extension.Equals(".cshtml", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    ///     The assemblies of <paramref name="binAssemblies" /> (the application assemblies under a
    ///     <c>bin/</c> directory) whose PDB names a template the tree holds, in the order given.
    /// </summary>
    public static List<string> Find(string path, IReadOnlyList<string> binAssemblies, ICollection<string> diagnostics)
    {
        var builds = new List<string>();
        if (Off.Value || binAssemblies.Count == 0 || SourceDocumentPaths.Root(path) is not { } root)
        {
            return builds;
        }

        // Distinct documents, so the copies of one build in several outputs count once.
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        var outside = new SortedSet<string>(StringComparer.Ordinal);
        var newer = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var assembly in binAssemblies)
        {
            if (Probe(root, assembly, missing, outside, newer))
            {
                builds.Add(assembly);
            }
        }

        if (missing.Count > 0)
        {
            diagnostics.Add(string.Create(CultureInfo.InvariantCulture, $"{missing.Count} .razor/.cshtml file(s) named by the PDB of a build under bin/ no longer exist in the tree (a stale build); the calls placed in them are not reported."));
        }

        if (outside.Count > 0)
        {
            diagnostics.Add(string.Create(CultureInfo.InvariantCulture, $"{outside.Count} .razor/.cshtml file(s) named by the PDB of a build under bin/ lie outside the scanned directory (a referenced project, or a build of another tree); the calls placed in them are not reported. Scan the directory that holds them to list them."));
        }

        if (newer.Count > 0)
        {
            diagnostics.Add(string.Create(CultureInfo.InvariantCulture, $"{newer.Count} .razor/.cshtml file(s) changed after the build that compiled them; the calls placed in them come from that build and their lines may be out of date. Rebuild the project for current lines."));
        }

        return builds;
    }

    /// <summary>
    ///     Whether the assembly's PDB names a template the tree holds. Templates it names that the
    ///     tree does not hold, that sit outside the scan root, or that changed after the build are
    ///     added to the matching set, by their path as the PDB writes it.
    /// </summary>
    private static bool Probe(string root, string assemblyPath, ISet<string> missing, ISet<string> outside, ISet<string> newer)
    {
        try
        {
            using var stream = new FileStream(assemblyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var peReader = new PEReader(stream);
            using var pdb = OpenPdb(assemblyPath, peReader);
            if (pdb is null)
            {
                return false;
            }

            var reader = pdb.Reader;
            var builtFromSource = TreeFrameworks.IsBuiltFromSource(root, assemblyPath);
            var builtAt = File.GetLastWriteTimeUtc(assemblyPath);
            var inTree = false;
            foreach (var handle in reader.Documents)
            {
                var name = reader.GetString(reader.GetDocument(handle).Name);
                if (!IsTemplate(name))
                {
                    continue;
                }

                if (SourceDocumentPaths.InTree(root, name, builtFromSource) is not { } relative)
                {
                    outside.Add(name);
                }
                else if (Path.Join(root, relative) is var template && !File.Exists(template))
                {
                    missing.Add(name);
                }
                else
                {
                    inTree = true;
                    if (File.GetLastWriteTimeUtc(template) > builtAt)
                    {
                        newer.Add(name);
                    }
                }
            }

            return inTree;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    ///     The portable PDB beside <paramref name="assemblyPath" />; when there is none and
    ///     <paramref name="embeddedIn" /> is given, the one embedded in that assembly (a build with
    ///     <c>DebugType=embedded</c>, or a Windows PDB beside it). Null when there is no portable PDB.
    ///     The embedded form is read only for the builds this class picks: the rest of the call
    ///     graph keeps placing calls from a PDB file alone.
    /// </summary>
    public static OpenedPdb? OpenPdb(string assemblyPath, PEReader? embeddedIn)
    {
        var pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
        if (File.Exists(pdbPath))
        {
            Stream? stream = null;
            try
            {
                stream = new FileStream(pdbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var provider = MetadataReaderProvider.FromPortablePdbStream(stream);
                return new OpenedPdb(provider, stream);
            }
            catch (BadImageFormatException)
            {
                // A Windows PDB, not a portable one: the embedded form may still be there.
                stream?.Dispose();
            }
        }

        if (embeddedIn is not null)
        {
            foreach (var entry in embeddedIn.ReadDebugDirectory())
            {
                if (entry.Type == DebugDirectoryEntryType.EmbeddedPortablePdb)
                {
                    return new OpenedPdb(embeddedIn.ReadEmbeddedPortablePdbDebugDirectoryData(entry), null);
                }
            }
        }

        return null;
    }

    /// <summary>A portable PDB's reader with the provider and stream that back it.</summary>
    internal sealed class OpenedPdb(MetadataReaderProvider provider, Stream? stream) : IDisposable
    {
        public MetadataReader Reader { get; } = provider.GetMetadataReader();

        public void Dispose()
        {
            provider.Dispose();
            stream?.Dispose();
        }
    }
}
