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
///     a <c>/_/</c>-mapped or foreign-file-system path. A stale build, whose PDB names a template
///     that was deleted or renamed since, is not read for it: a row on a file the tree does not
///     have cannot be followed to anything, and the diagnostic says how many were left out.
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
        Off.Value = true;
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        public void Dispose() => Off.Value = false;
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

        var staleDocuments = 0;
        foreach (var assembly in binAssemblies)
        {
            var (inTree, stale) = Probe(root, assembly);
            staleDocuments += stale;
            if (inTree > 0)
            {
                builds.Add(assembly);
            }
        }

        if (staleDocuments > 0)
        {
            diagnostics.Add($"{staleDocuments} .razor/.cshtml document(s) named by PDBs under bin/ are not in the tree (a stale build, or a build of another tree); the calls placed in them are not reported.");
        }

        return builds;
    }

    /// <summary>How many templates the assembly's PDB names that the tree holds, and how many it does not.</summary>
    private static (int InTree, int Stale) Probe(string root, string assemblyPath)
    {
        try
        {
            using var stream = new FileStream(assemblyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var peReader = new PEReader(stream);
            using var pdb = OpenPdb(assemblyPath, peReader);
            if (pdb is null)
            {
                return (0, 0);
            }

            var reader = pdb.Reader;
            var builtFromSource = TreeFrameworks.IsBuiltFromSource(root, assemblyPath);
            int inTree = 0, stale = 0;
            foreach (var handle in reader.Documents)
            {
                var name = reader.GetString(reader.GetDocument(handle).Name);
                if (!IsTemplate(name))
                {
                    continue;
                }

                if (SourceDocumentPaths.InTree(root, name, builtFromSource) is not { } relative || !File.Exists(Path.Join(root, relative)))
                {
                    stale++;
                }
                else
                {
                    inTree++;
                }
            }

            return (inTree, stale);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException)
        {
            return (0, 0);
        }
    }

    /// <summary>
    ///     The portable PDB beside <paramref name="assemblyPath" />; when there is none and
    ///     <paramref name="embeddedIn" /> is given, the one embedded in that assembly (a build with
    ///     <c>DebugType=embedded</c>). Null when neither exists or the file is not a portable PDB.
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
                stream?.Dispose();
                return null;
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
