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
    internal const string TreePacksSourcePrefix = "tree-reference-packs";

    private const string FrameworkNamesResource = "Dosai.framework-assemblies.txt";
    private const string RuntimeImplementationPrefix = "System.Private.";

    private static readonly Lazy<FrameworkReferenceSet> Resolved = new(Resolve, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly AsyncLocal<FrameworkReferenceSet?> Override = new();
    private static readonly Lock TreeResolveLock = new();
    private static readonly Dictionary<string, FrameworkReferenceSet> TreeResolvedByRoot = new(SafeFileRead.PathComparer);

    /// <summary>The framework references for this process (or a test override).</summary>
    internal static FrameworkReferenceSet Current => Override.Value ?? Resolved.Value;

    /// <summary>
    ///     The framework references for one analyzed tree: <see cref="Current" /> plus the
    ///     reference packs the tree's frameworks name (issue #74). Cached per scan root.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A web, worker-adjacent or desktop project references shared frameworks beyond
    ///         Microsoft.NETCore.App - Microsoft.AspNetCore.App, Microsoft.WindowsDesktop.App -
    ///         detected from the SDK attribute, <c>&lt;FrameworkReference&gt;</c> items,
    ///         <c>UseWindowsForms</c>/<c>UseWPF</c>, project.assets.json's
    ///         <c>frameworkReferences</c>, and <c>*.runtimeconfig.json</c>. Their reference
    ///         packs are resolved per analyzed target framework, in this order per framework:
    ///         <c>&lt;dotnet root&gt;/packs/&lt;Name&gt;.Ref/&lt;version&gt;/ref/&lt;tfm&gt;</c>,
    ///         the NuGet cache copy, then the installed shared framework. The version matches
    ///         the analyzed target's major first, then the highest patch (releases over
    ///         prereleases); a pack of another major is used only as a fallback and always
    ///         produces a diagnostic naming the version used.
    ///     </para>
    ///     <para>
    ///         Exactly one reference per assembly simple name survives, claimed in a fixed
    ///         order: the target-matched Microsoft.NETCore.App.Ref pack (when the analyzed
    ///         major differs from Dosai's own runtime and that pack is installed), then the
    ///         tree's other framework packs sorted by name, then <see cref="Current" /> filling
    ///         the remaining names. The order matters because .NET 11's base framework ships
    ///         nine <c>Microsoft.Extensions.*</c> assemblies that older ASP.NET Core packs also
    ///         carry: two copies of one name give Roslyn ambiguous types and bind nothing, so
    ///         the pack that matches the analyzed target claims the name and the drop is
    ///         reported. Names <see cref="Current" /> could not supply (it fills, it does not
    ///         displace) are summarized once. A missing pack is a diagnostic, never a failure:
    ///         binding degrades exactly as far as the missing references reach.
    ///     </para>
    /// </summary>
    internal static FrameworkReferenceSet ForTree(string? scanRoot)
    {
        // The test override wins the whole decision: tests that pin a reference set mean it.
        if (Override.Value is { } overridden)
        {
            return overridden;
        }

        if (string.IsNullOrWhiteSpace(scanRoot))
        {
            return Current;
        }

        string root;
        try
        {
            root = TargetFrameworkDetection.ProjectContextRoot(Path.GetFullPath(scanRoot));
        }
        catch (ArgumentException)
        {
            return Current;
        }

        lock (TreeResolveLock)
        {
            if (TreeResolvedByRoot.TryGetValue(root, out var cached))
            {
                return cached;
            }

            var resolved = ResolveForTree(root);
            TreeResolvedByRoot[root] = resolved;
            return resolved;
        }
    }

    private static FrameworkReferenceSet ResolveForTree(string root)
    {
        var current = Current;
        var notes = new List<string>();
        if (current.Diagnostic is { } currentDiagnostic)
        {
            notes.Add(currentDiagnostic);
        }

        var detected = TreeFrameworks.Detect(root);
        if (detected.Count == 0)
        {
            return current;
        }

        var moniker = FrameworkPreprocessorDefines.TrySelectRepresentative(TargetFrameworkDetection.Detect(root), out var representative)
            ? representative
            : null;
        // Reference packs exist for modern .NETCore targets only; anything else (net472,
        // netstandard) keeps the process-wide set.
        var major = moniker is not null && TryParseTargetMajor(moniker, out var parsed) && parsed >= 5 ? parsed : (int?)null;
        if (major is null)
        {
            notes.Add($"Framework references {string.Join(", ", detected.Select(framework => framework.Name))} were detected, but the tree's representative target '{moniker ?? "<none>"}' has no reference pack; keeping the process-wide framework references.");
            return new FrameworkReferenceSet(current.References, current.Source, JoinNotes(notes), current.Warnings);
        }

        var monikerName = MonikerOf(moniker!);
        var references = new List<(string Key, PortableExecutableReference Reference)>();
        var originByName = new Dictionary<string, (string Key, string Origin)>(StringComparer.OrdinalIgnoreCase);
        var packOrigins = new List<string>();
        var corePackOwnsCorlib = false;

        void AddDirectory(string directory, string origin)
        {
            var added = 0;
            var dropped = 0;
            foreach (var assemblyPath in Directory.EnumerateFiles(directory, "*" + Constants.AssemblyExtension).Order(StringComparer.Ordinal))
            {
                var name = Path.GetFileNameWithoutExtension(assemblyPath);
                if (originByName.TryGetValue(name, out var existing))
                {
                    dropped++;
                    notes.Add($"framework assembly '{name}' of '{origin}' skipped: already provided by {existing.Origin} (one reference per assembly name; the pack matching the analyzed target wins).");
                    continue;
                }

                if (TryCreateFromFile(assemblyPath, notes) is { } reference)
                {
                    references.Add((assemblyPath, reference));
                    originByName.Add(name, (assemblyPath, origin));
                    added++;
                }
            }

            if (added > 0)
            {
                packOrigins.Add(origin);
                DebugLog.Log($"tree framework reference pack: {origin} -> {added} reference(s), {dropped} dropped as duplicate(s)");
            }
        }

        // 1. A target-matched base-framework pack when Dosai's own runtime is of a different
        //    major: same-major trees already run on matching references.
        if (major == Environment.Version.Major)
        {
            // Same major as Dosai's own runtime: the process-wide set already matches the
            // target, and re-deriving it from a pack would only churn the reference list.
        }
        else if (ResolvePackDirectory("Microsoft.NETCore.App", monikerName, major.Value, notes) is { } corePack)
        {
            AddDirectory(corePack.Directory, $"pack Microsoft.NETCore.App.Ref {corePack.Version} (matches {moniker})");
            // A base reference pack owns the corlib: its System.Runtime declares the primitive
            // types, and Dosai's own System.Private.CoreLib (a different major) beside it makes
            // Roslyn report CS0518 for every predefined type - two competing corlibs. The
            // runtime implementation assemblies are the fallback set's companions, not the
            // pack's; the pack compiles on its own.
            corePackOwnsCorlib = true;
        }
        else if (detected.Any(framework => framework.Name != "Microsoft.NETCore.App") is false)
        {
            // Only when the tree's frameworks need packs at all (the no-frameworks case returned
            // Current above): say which base references a foreign-major tree got.
            notes.Add($"No {major}.x Microsoft.NETCore.App reference pack is installed for target '{moniker}'; the base framework references come from Dosai's own runtime ({current.Source}, .NET {Environment.Version}), so APIs added after .NET {Environment.Version.Major} bind through a newer base than the tree targets.");
        }

        // 2. The tree's other frameworks, sorted by name so several packs are deterministic.
        foreach (var framework in detected.Where(framework => framework.Name != "Microsoft.NETCore.App").OrderBy(framework => framework.Name, StringComparer.Ordinal))
        {
            if (ResolvePackDirectory(framework.Name, monikerName, major.Value, notes) is { } pack)
            {
                AddDirectory(pack.Directory, $"pack for {framework.Name} {pack.Version} via {framework.Evidence} (matches {moniker}: {(pack.ExactMajor ? "exact major" : $"no {major}.x pack installed, used {pack.Version}")})");
            }
            else
            {
                notes.Add($"No reference pack for '{framework.Name}' ({framework.Evidence}) matching '{monikerName}' was found in the installed dotnet packs, the NuGet cache, or the shared frameworks; calls into {framework.Name} do not bind. Installing the SDK or runtime that ships the pack fixes it - restoring or building the tree does not.");
            }
        }

        // 3. The process-wide set fills every name the packs did not supply; its dropped
        //    copies are summarized, because filling never changes what the packs claimed.
        var filled = 0;
        foreach (var (key, reference) in current.References)
        {
            var name = Path.GetFileNameWithoutExtension(key);
            if (originByName.ContainsKey(name))
            {
                continue;
            }

            if (corePackOwnsCorlib && name.StartsWith(RuntimeImplementationPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            references.Add((key, reference));
            originByName.Add(name, (key, $"fallback ({current.Source})"));
            filled++;
        }

        if (references.Count == 0)
        {
            return current;
        }

        var source = $"{TreePacksSourcePrefix}[{string.Join(",", packOrigins)}]+{current.Source}";
        DebugLog.Count($"tree framework reference packs ({root})", packOrigins.Count);
        DebugLog.Count("tree framework references from packs", references.Count - filled);
        DebugLog.Count("tree framework references from the process-wide fallback", filled);
        return new FrameworkReferenceSet(references, source, JoinNotes(notes), current.Warnings);
    }

    private static string? JoinNotes(List<string> notes) => notes.Count == 0 ? null : string.Join(" | ", notes);

    /// <summary>The tfm moniker reference-pack directories are named by (`net8.0-windows` -> `net8.0`).</summary>
    private static string MonikerOf(string targetFramework)
    {
        var normalized = targetFramework.Trim().ToLowerInvariant();
        var separator = normalized.IndexOf('-', StringComparison.Ordinal);
        return separator >= 0 ? normalized[..separator] : normalized;
    }

    private static bool TryParseTargetMajor(string targetFramework, out int major)
    {
        major = 0;
        var span = targetFramework.AsSpan();
        if (!span.StartsWith("net", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        span = span[3..];
        var digits = 0;
        while (digits < span.Length && char.IsAsciiDigit(span[digits]))
        {
            digits++;
        }

        return digits > 0 && int.TryParse(span[..digits], NumberStyles.Integer, CultureInfo.InvariantCulture, out major);
    }

    /// <summary>
    ///     The reference-pack (or shared-framework) directory for one framework name: pack
    ///     directories under every dotnet root, the NuGet cache, then the newest installed
    ///     shared framework, each filtered to the moniker's ref directory. Version choice:
    ///     exact major first, then the highest version with releases over prereleases; a
    ///     non-exact major adds a note naming the version used.
    /// </summary>
    private static (string Directory, string Version, bool ExactMajor)? ResolvePackDirectory(string frameworkName, string moniker, int targetMajor, List<string> notes)
        => ResolvePackDirectory(frameworkName, moniker, targetMajor, notes, PackDirectories());

    /// <summary>Testable core: the pack directories are supplied by the caller.</summary>
    internal static (string Directory, string Version, bool ExactMajor)? ResolvePackDirectory(string frameworkName, string moniker, int targetMajor, List<string> notes, IEnumerable<string> packDirectories)
    {
        // The desktop sub-frameworks (.WPF/.WindowsForms) share one reference pack.
        var packName = frameworkName.StartsWith("Microsoft.WindowsDesktop.App", StringComparison.OrdinalIgnoreCase)
            ? "Microsoft.WindowsDesktop.App.Ref"
            : frameworkName + ".Ref";
        var candidates = new List<(string Directory, Version Version, bool IsRelease)>();
        foreach (var packsDirectory in packDirectories)
        {
            var packRoot = Path.Combine(packsDirectory, packName);
            if (!Directory.Exists(packRoot))
            {
                continue;
            }

            foreach (var versionDirectory in Directory.EnumerateDirectories(packRoot))
            {
                var referenceDirectory = Path.Combine(versionDirectory, "ref", moniker);
                if (Directory.Exists(referenceDirectory) && ParseVersion(Path.GetFileName(versionDirectory)) is { } version && version.Version > new Version(0, 0))
                {
                    candidates.Add((referenceDirectory, version.Version, version.IsRelease));
                }
            }
        }

        if (candidates.Count == 0)
        {
            // The NuGet cache keeps framework ref packs under their package id (all lowercase).
            var cacheRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages", packName.ToLowerInvariant());
            if (Directory.Exists(cacheRoot))
            {
                foreach (var versionDirectory in Directory.EnumerateDirectories(cacheRoot))
                {
                    var referenceDirectory = Path.Combine(versionDirectory, "ref", moniker);
                    if (Directory.Exists(referenceDirectory) && ParseVersion(Path.GetFileName(versionDirectory)) is { } version && version.Version > new Version(0, 0))
                    {
                        candidates.Add((referenceDirectory, version.Version, version.IsRelease));
                    }
                }
            }
        }

        if (candidates.Count > 0)
        {
            var ordered = candidates
                .OrderByDescending(candidate => candidate.Version.Major == targetMajor)
                .ThenByDescending(candidate => candidate.Version)
                .ThenByDescending(candidate => candidate.IsRelease)
                .ToList();
            var chosen = ordered[0];
            if (chosen.Version.Major != targetMajor)
            {
                notes.Add($"No {targetMajor}.x reference pack for '{frameworkName}' is installed; using version {chosen.Version} ({Path.GetFileName(Path.GetDirectoryName(chosen.Directory))}) instead, so APIs added or removed after .NET {targetMajor} bind incorrectly.");
            }

            return (chosen.Directory, chosen.Version.ToString(), chosen.Version.Major == targetMajor);
        }

        // Last resort: the installed shared framework's runtime directory for this framework.
        foreach (var sharedRoot in InstalledSharedRoots())
        {
            var frameworkRoot = Path.Combine(sharedRoot, frameworkName);
            if (!Directory.Exists(frameworkRoot))
            {
                continue;
            }

            var versions = Directory.EnumerateDirectories(frameworkRoot)
                .Select(directory => (Directory: directory, ParseVersion(Path.GetFileName(directory))))
                .Where(candidate => candidate.Item2.Version > new Version(0, 0))
                .OrderByDescending(candidate => candidate.Item2.Version.Major == targetMajor)
                .ThenByDescending(candidate => candidate.Item2.Version)
                .ThenByDescending(candidate => candidate.Item2.IsRelease)
                .ToList();
            if (versions.Count > 0)
            {
                var chosen = versions[0];
                if (chosen.Item2.Version.Major != targetMajor)
                {
                    notes.Add($"No {targetMajor}.x reference pack for '{frameworkName}' is installed; using shared framework version {chosen.Item2.Version} instead.");
                }

                return (chosen.Directory, chosen.Item2.Version.ToString(), chosen.Item2.Version.Major == targetMajor);
            }
        }

        return null;
    }

    private static IEnumerable<string> PackDirectories()
    {
        var roots = new List<string>();
        foreach (var variable in new[] { "DOTNET_ROOT", "DOTNET_ROOT_ARM64", "DOTNET_ROOT_X64" })
        {
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } dotnetRoot)
            {
                roots.Add(dotnetRoot);
            }
        }

        if (OperatingSystem.IsWindows())
        {
            foreach (var programFiles in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
            {
                if (!string.IsNullOrEmpty(programFiles))
                {
                    roots.Add(Path.Combine(programFiles, "dotnet"));
                }
            }
        }
        else
        {
            roots.AddRange(["/usr/local/share/dotnet", "/usr/share/dotnet", "/usr/lib/dotnet", "/opt/dotnet"]);
            if (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { Length: > 0 } home)
            {
                roots.Add(Path.Combine(home, ".dotnet"));
            }
        }

        // `dotnet --list-runnimes` roots are .../shared directories; their parent is the install.
        roots.AddRange(Dosai.GetDotnetSharedRuntimeRoots().Select(shared => Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(shared, "..")))));
        return roots.Distinct(StringComparer.Ordinal).Select(root => Path.Combine(root, "packs")).Where(Directory.Exists);
    }

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
