using System.Globalization;
using System.Reflection;
using Microsoft.CodeAnalysis;

namespace Depscan;

/// <summary>
///     The .NET framework metadata references every Roslyn compilation of analyzed source starts
///     from (methods, data-flow, crypto and standalone framework analysis), resolved once per
///     process.
/// </summary>
/// <remarks>
///     <para>
///         Three sources, first non-empty wins, never mixed (two framework versions side by side
///         give Roslyn duplicate assembly identities):
///     </para>
///     <list type="number">
///         <item>
///             <b>Trusted platform assemblies</b>: a framework-dependent Dosai runs on an installed
///             shared framework, and the host lists its assemblies - by framework name, never
///             Dosai's own dependencies, which a non-bundled host lists too.
///         </item>
///         <item>
///             <b>The bundled runtime</b>: a self-contained single-file Dosai carries its runtime
///             inside the executable, gives the core library no file location and lists no trusted
///             platform assemblies (issue #67). The framework assembly names are embedded at build
///             time from the reference pack Dosai compiles against; each is loaded from the bundle
///             and referenced through its in-memory metadata, which needs no file on disk and no
///             installed .NET at all.
///         </item>
///         <item>
///             <b>The newest installed shared framework of any version</b>: last resort when
///             neither is available. An older framework still binds most of an analyzed project,
///             where no references bind none of it; the version gap is reported.
///         </item>
///     </list>
///     <para>
///         With none of them the result is empty and <see cref="Diagnostic" /> says so: every
///         framework call then fails to bind, and the slice must say that instead of suggesting a
///         restore of a tree that needs none.
///     </para>
/// </remarks>
internal static class FrameworkReferences
{
    internal const string TrustedPlatformSource = "trusted-platform-assemblies";
    internal const string BundledRuntimeSource = "bundled-runtime";
    internal const string InstalledFrameworkSource = "installed-shared-framework";
    internal const string NoneSource = "none";

    private const string FrameworkNamesResource = "Dosai.framework-assemblies.txt";
    private const string RuntimeImplementationPrefix = "System.Private.";

    private static readonly Lazy<FrameworkReferenceSet> Resolved = new(Resolve, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly AsyncLocal<FrameworkReferenceSet?> Override = new();

    /// <summary>The framework references for this process (or a test override).</summary>
    internal static FrameworkReferenceSet Current => Override.Value ?? Resolved.Value;

    /// <summary>Test hook: replaces <see cref="Current" /> for the current execution context and the analysis threads it starts.</summary>
    internal static IDisposable OverrideForTesting(FrameworkReferenceSet references)
    {
        var previous = Override.Value;
        Override.Value = references;
        return new Restore(() => Override.Value = previous);
    }

    /// <summary>
    ///     A slice diagnostic when the framework references are degraded - none at all, or an
    ///     installed framework older than Dosai's own - and null when they are complete.
    /// </summary>
    internal static string? Diagnostic => Current.Diagnostic;

    private static FrameworkReferenceSet Resolve()
    {
        var warnings = new List<string>();
        var trusted = FromTrustedPlatformAssemblies(warnings);
        if (trusted.Count > 0)
        {
            return new FrameworkReferenceSet(trusted, TrustedPlatformSource, null, warnings);
        }

        var bundled = FromLoadedAssemblies(EmbeddedFrameworkAssemblyNames(), warnings);
        if (bundled.Count > 0)
        {
            return new FrameworkReferenceSet(bundled, BundledRuntimeSource, null, warnings);
        }

        if (SelectNewestInstalledFramework(InstalledSharedRoots()) is { } installed)
        {
            var references = FromDirectory(installed.Directory, warnings);
            if (references.Count > 0)
            {
                var diagnostic = installed.Version.Major < Environment.Version.Major
                    ? string.Create(CultureInfo.InvariantCulture, $"Framework metadata references came from the installed .NET {installed.Version} shared framework ('{installed.Directory}'), older than the .NET {Environment.Version.Major} runtime Dosai targets; calls to newer framework APIs do not bind.")
                    : null;
                return new FrameworkReferenceSet(references, InstalledFrameworkSource, diagnostic, warnings);
            }
        }

        return new FrameworkReferenceSet([], NoneSource,
            "No .NET framework metadata references could be resolved (no trusted platform assemblies, no bundled runtime, no installed shared framework): every call into the framework is unresolved and package reachability for framework assemblies is unreliable. This is a Dosai installation problem, not a property of the scanned tree; restoring or building the tree does not fix it.",
            warnings);
    }

    private static List<(string Key, PortableExecutableReference Reference)> FromTrustedPlatformAssemblies(List<string> warnings)
    {
        var references = new List<(string, PortableExecutableReference)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
#pragma warning disable IL3000
        // Empty in a single-file bundle; a relative combination with the app directory used to
        // make CreateFromFile open the directory itself.
        var coreLibrary = typeof(object).Assembly.Location;
#pragma warning restore IL3000
        var candidates = new List<string>();
        if (Path.IsPathRooted(coreLibrary))
        {
            candidates.Add(coreLibrary);
        }

        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string trustedPlatformAssemblies)
        {
            // A non-bundled host (dotnet run, a local build, the test host) also lists the
            // application's own dependencies - Roslyn, System.CommandLine, ... - which bound an
            // analyzed tree's calls to Dosai's copies of those packages; a self-contained local
            // build even keeps them in the framework's directory. Released builds reference the
            // framework only, so every build keeps the framework's names.
            candidates.AddRange(trustedPlatformAssemblies.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Where(candidate => IsFrameworkAssemblyName(Path.GetFileNameWithoutExtension(candidate))));
        }

        foreach (var candidate in candidates)
        {
            if (seen.Add(candidate) && File.Exists(candidate) && TryCreateFromFile(candidate, warnings) is { } reference)
            {
                references.Add((candidate, reference));
            }
        }

        return references;
    }

    private static readonly Lazy<HashSet<string>> FrameworkAssemblyNames =
        new(() => new HashSet<string>(EmbeddedFrameworkAssemblyNames(), StringComparer.OrdinalIgnoreCase));

    /// <summary>
    ///     A shared-framework assembly: one the reference pack names, or a runtime implementation
    ///     assembly (<c>System.Private.*</c>) its facades forward to. Every name passes when no
    ///     names were embedded.
    /// </summary>
    internal static bool IsFrameworkAssemblyName(string name) =>
        FrameworkAssemblyNames.Value.Count == 0
        || FrameworkAssemblyNames.Value.Contains(name)
        || name.StartsWith(RuntimeImplementationPrefix, StringComparison.Ordinal);

    /// <summary>
    ///     Framework assembly names embedded at build time: every assembly of the
    ///     Microsoft.NETCore.App reference pack Dosai compiles against (see the
    ///     <c>EmbedFrameworkAssemblyNames</c> target in <c>Dosai.csproj</c>), plus the core library,
    ///     which the reference pack exposes only through facades.
    /// </summary>
    internal static IReadOnlyList<string> EmbeddedFrameworkAssemblyNames()
    {
        using var stream = typeof(FrameworkReferences).Assembly.GetManifestResourceStream(FrameworkNamesResource);
        if (stream is null)
        {
            return [];
        }

        using var reader = new StreamReader(stream);
        var names = reader.ReadToEnd()
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Append("System.Private.CoreLib")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    /// <summary>
    ///     References built from the metadata of the named assemblies and the runtime
    ///     implementation assemblies (<c>System.Private.*</c>) they reference, loading each first.
    ///     In a single-file bundle the framework assemblies load from the executable and expose no
    ///     file; <see cref="System.Reflection.Metadata.AssemblyExtensions.TryGetRawMetadata" /> hands
    ///     Roslyn their metadata where the runtime mapped it, which stays valid for the process
    ///     lifetime because the default load context never unloads. Keys are
    ///     <c>&lt;app directory&gt;/&lt;name&gt;.dll</c> so file-name de-duplication against package
    ///     assemblies works as it does for files.
    /// </summary>
    internal static List<(string Key, PortableExecutableReference Reference)> FromLoadedAssemblies(IEnumerable<string> assemblyNames, List<string> warnings)
    {
        var references = new List<(string, PortableExecutableReference)>();
        // The runtime's System.Runtime, System.Xml.ReaderWriter, ... are facades forwarding to
        // implementation assemblies no reference pack names (System.Private.Uri,
        // System.Private.Xml, ...); without those, Uri or XmlDocument does not bind. Only those
        // are followed: compatibility facades such as System.Configuration also reference
        // out-of-band packages (System.Configuration.ConfigurationManager) that are not part of
        // the shared framework, and in a bundle would resolve to Dosai's own dependencies.
        var pending = new Queue<string>(assemblyNames);
        var seen = new HashSet<string>(pending, StringComparer.OrdinalIgnoreCase);
        while (pending.TryDequeue(out var name))
        {
            Assembly assembly;
            try
            {
                assembly = Assembly.Load(new AssemblyName(name));
            }
            catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
            {
                warnings.Add($"framework assembly '{name}' is not loadable: {ex.GetType().Name}");
                continue;
            }

            if (TryCreateFromMetadata(assembly, name) is { } reference)
            {
                references.Add((Path.Combine(AppContext.BaseDirectory, name + Constants.AssemblyExtension), reference));
            }
            else
            {
                warnings.Add($"framework assembly '{name}' exposes no metadata");
            }

            foreach (var referenced in assembly.GetReferencedAssemblies())
            {
                if (referenced.Name is { } referencedName
                    && referencedName.StartsWith(RuntimeImplementationPrefix, StringComparison.Ordinal)
                    && seen.Add(referencedName))
                {
                    pending.Enqueue(referencedName);
                }
            }
        }

        return references;
    }

    private static unsafe PortableExecutableReference? TryCreateFromMetadata(Assembly assembly, string name)
    {
        if (!System.Reflection.Metadata.AssemblyExtensions.TryGetRawMetadata(assembly, out var blob, out var length))
        {
            return null;
        }

        var module = ModuleMetadata.CreateFromMetadata((IntPtr)blob, length);
        return AssemblyMetadata.Create(module).GetReference(display: name + Constants.AssemblyExtension);
    }

    /// <summary>
    ///     The newest <c>Microsoft.NETCore.App</c> version directory under the given shared
    ///     roots, whatever its version. Only the core framework qualifies: ASP.NET Core's shared
    ///     framework has no core library and would leave every BCL name unbound on its own.
    /// </summary>
    internal static (string Directory, Version Version)? SelectNewestInstalledFramework(IEnumerable<string> sharedRoots)
    {
        (string Directory, Version Version, bool IsRelease)? newest = null;
        foreach (var sharedRoot in sharedRoots.Distinct(StringComparer.Ordinal))
        {
            var frameworkRoot = Path.Combine(sharedRoot, "Microsoft.NETCore.App");
            if (!Directory.Exists(frameworkRoot))
            {
                continue;
            }

            foreach (var versionDirectory in Directory.EnumerateDirectories(frameworkRoot))
            {
                if (!File.Exists(Path.Combine(versionDirectory, "System.Runtime.dll")))
                {
                    continue;
                }

                var (version, isRelease) = ParseVersion(Path.GetFileName(versionDirectory));
                if (newest is null || version > newest.Value.Version || (version == newest.Value.Version && isRelease && !newest.Value.IsRelease))
                {
                    newest = (versionDirectory, version, isRelease);
                }
            }
        }

        return newest is { } found ? (found.Directory, found.Version) : null;
    }

    private static (Version Version, bool IsRelease) ParseVersion(string directoryName)
    {
        var separator = directoryName.IndexOf('-', StringComparison.Ordinal);
        var numeric = separator < 0 ? directoryName : directoryName[..separator];
        return Version.TryParse(numeric, out var version) ? (version, separator < 0) : (new Version(0, 0), false);
    }

    /// <summary>Shared-framework roots: DOTNET_ROOT, the conventional install locations, and <c>dotnet --list-runtimes</c>.</summary>
    private static IEnumerable<string> InstalledSharedRoots()
    {
        var roots = new List<string>();
        foreach (var variable in new[] { "DOTNET_ROOT", "DOTNET_ROOT_ARM64", "DOTNET_ROOT_X64" })
        {
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } dotnetRoot)
            {
                roots.Add(Path.Combine(dotnetRoot, "shared"));
            }
        }

        if (OperatingSystem.IsWindows())
        {
            foreach (var programFiles in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
            {
                if (!string.IsNullOrEmpty(programFiles))
                {
                    roots.Add(Path.Combine(programFiles, "dotnet", "shared"));
                }
            }
        }
        else
        {
            roots.AddRange(["/usr/local/share/dotnet/shared", "/usr/share/dotnet/shared", "/usr/lib/dotnet/shared", "/opt/dotnet/shared"]);
            if (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { Length: > 0 } home)
            {
                roots.Add(Path.Combine(home, ".dotnet", "shared"));
            }
        }

        roots.AddRange(Dosai.GetDotnetSharedRuntimeRoots());
        return roots.Where(Directory.Exists);
    }

    private static List<(string Key, PortableExecutableReference Reference)> FromDirectory(string frameworkDirectory, List<string> warnings)
    {
        var references = new List<(string, PortableExecutableReference)>();
        foreach (var assemblyPath in Directory.EnumerateFiles(frameworkDirectory, "*" + Constants.AssemblyExtension).Order(StringComparer.Ordinal))
        {
            if (TryCreateFromFile(assemblyPath, warnings) is { } reference)
            {
                references.Add((assemblyPath, reference));
            }
        }

        return references;
    }

    private static PortableExecutableReference? TryCreateFromFile(string path, List<string> warnings)
    {
        try
        {
            return MetadataReference.CreateFromFile(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            warnings.Add($"could not reference '{path}': {ex.Message}");
            return null;
        }
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}

/// <summary>Framework references, where they came from, and the slice diagnostic when they are degraded.</summary>
internal sealed record FrameworkReferenceSet(
    IReadOnlyList<(string Key, PortableExecutableReference Reference)> References,
    string Source,
    string? Diagnostic,
    IReadOnlyList<string> Warnings);
