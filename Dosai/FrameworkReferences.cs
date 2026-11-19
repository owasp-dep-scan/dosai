using System.Globalization;
using System.Reflection;
using Microsoft.CodeAnalysis;
using FrameworkVersion = Depscan.FrameworkPreprocessorDefines.FrameworkVersion;

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

    /// <summary>
    ///     Marks the diagnostic of a net4x target whose reference-assemblies package is missing
    ///     from the NuGet cache: only there does the unresolved-call message advise a restore,
    ///     because a package restore auto-fetches (an installed pack does not).
    /// </summary>
    internal const string NetFrameworkPackAbsentMarker = "reference assemblies (package Microsoft.NETFramework.ReferenceAssemblies.";

    private const string FrameworkNamesResource = "Dosai.framework-assemblies.txt";

    /// <summary>The runtime's implementation assemblies (<c>System.Private.CoreLib</c> and friends), which the reference packs expose only through facades.</summary>
    internal const string RuntimeImplementationPrefix = "System.Private.";

    private static readonly Lazy<FrameworkReferenceSet> Resolved = new(Resolve, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly AsyncLocal<FrameworkReferenceSet?> Override = new();
    private static readonly Lock TreeResolveLock = new();
    private static readonly Dictionary<string, FrameworkReferenceSet> TreeResolvedByRoot = new(SafeFileRead.PathComparer);

    /// <summary>Forgets every per-root reference decision (see <see cref="TargetFrameworkDetection.ResetCaches" />). The process-wide <see cref="Current" /> set is immutable and stays.</summary>
    internal static void ResetTreeCache()
    {
        lock (TreeResolveLock)
        {
            TreeResolvedByRoot.Clear();
        }
    }

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

        var detected = TreeFrameworks.Detect(root);
        var moniker = FrameworkPreprocessorDefines.TrySelectRepresentative(TargetFrameworkDetection.Detect(root), out var representative)
            ? representative
            : null;
        // The base follows the family of the tree's representative target (issue #127), whether
        // or not the tree declares a second shared framework: modern .NET resolves a
        // Microsoft.NETCore.App pack, net4x the .NET Framework reference assemblies, and
        // netstandard, netcoreapp and undetectable targets have no reference pack.
        var family = ClassifyTargetForReferencePacks(moniker, out var version);
        if (family == ReferencePackTargetFamily.NetFramework)
        {
            return ResolveForNetFrameworkTree(root, moniker!, version, detected, current);
        }

        if (current.Diagnostic is { } currentDiagnostic)
        {
            notes.Add(currentDiagnostic);
        }

        if (family == ReferencePackTargetFamily.None)
        {
            if (detected.Count > 0)
            {
                notes.Add(string.Create(CultureInfo.InvariantCulture,
                    $"Framework references {string.Join(", ", detected.Select(framework => framework.Name))} were detected, but the tree's representative target '{moniker ?? "<none>"}' has no reference pack; the base framework references come from Dosai's own runtime ({current.Source}, .NET {Environment.Version.Major})."));
            }
            else
            {
                notes.Add(moniker is null
                    ? string.Create(CultureInfo.InvariantCulture,
                        $"No TargetFramework detected under the tree; the base framework references come from Dosai's own runtime ({current.Source}, .NET {Environment.Version.Major}).")
                    : string.Create(CultureInfo.InvariantCulture,
                        $"The tree's representative target '{moniker}' has no reference pack; the base framework references come from Dosai's own runtime ({current.Source}, .NET {Environment.Version.Major})."));
            }

            AddNetFrameworkProjectNote(notes, root, moniker);
            return new FrameworkReferenceSet(current.References, current.Source, JoinNotes(notes), current.Warnings);
        }

        var collector = new TreePackCollector(notes, TreeFrameworks.SourceAssemblyNames(root));
        var monikerName = MonikerOf(moniker!);
        var corePackOwnsCorlib = false;

        // 1. A target-matched base-framework pack, resolved for every modern target (issue
        //    #127): a plain net10.0 tree used to bind Dosai's own runtime silently.
        if (version.Major == Environment.Version.Major)
        {
            // Same major as Dosai's own runtime: the process-wide set already matches the
            // target, and re-deriving it from a pack would only churn the reference list.
            notes.Add(string.Create(CultureInfo.InvariantCulture,
                $"Base framework references for target '{moniker}' come from Dosai's own runtime ({current.Source}, .NET {Environment.Version.Major}), which matches the target's major."));
        }
        else if (ResolvePackDirectory("Microsoft.NETCore.App", monikerName, version.Major, notes) is { } corePack
                 && ParseVersion(corePack.Version).Version.Major != Environment.Version.Major)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture,
                $"Base framework references for target '{moniker}' come from the Microsoft.NETCore.App {corePack.Version} reference pack ({(corePack.ExactMajor ? "exact major" : "nearest installed major")})."));
            collector.AddDirectory(corePack.Directory, $"base framework Microsoft.NETCore.App {corePack.Version} for {moniker} ({(corePack.ExactMajor ? "exact major" : "nearest installed major")})", isBasePack: true);
            // A base reference pack owns the corlib: its System.Runtime declares the primitive
            // types, and Dosai's own System.Private.CoreLib (a different major) beside it makes
            // Roslyn report CS0518 for every predefined type - two competing corlibs. The
            // runtime implementation assemblies are the fallback set's companions, not the
            // pack's; the pack compiles on its own.
            corePackOwnsCorlib = true;
        }
        else
        {
            // No base framework nearer to the target than Dosai's own runtime: say which base
            // references a foreign-major tree got (the tree's packs are layered on it below).
            notes.Add(string.Create(CultureInfo.InvariantCulture,
                $"No {version.Major}.x Microsoft.NETCore.App reference pack is installed for target '{moniker}'; the base framework references come from Dosai's own runtime ({current.Source}, .NET {Environment.Version.Major}), so APIs that differ between .NET {version.Major} and .NET {Environment.Version.Major} bind through a different base than the tree targets."));
        }

        // 2. The tree's other frameworks, sorted by name so several packs are deterministic.
        foreach (var framework in detected.Where(framework => framework.Name != "Microsoft.NETCore.App").OrderBy(framework => framework.Name, StringComparer.Ordinal))
        {
            if (ResolvePackDirectory(framework.Name, monikerName, version.Major, notes) is { } pack)
            {
                collector.AddDirectory(pack.Directory, $"{framework.Name} {pack.Version} via {framework.Evidence} for {moniker} ({(pack.ExactMajor ? "exact major" : $"no {version.Major}.x pack installed")})");
            }
            else
            {
                notes.Add($"No reference pack for '{framework.Name}' ({framework.Evidence}) matching '{monikerName}' was found in the installed dotnet packs, the NuGet cache, or the shared frameworks; calls into {framework.Name} do not bind. Installing the SDK or runtime that ships the pack fixes it - restoring or building the tree does not.");
            }
        }

        AddBuiltFromSourceNote(notes, collector.BuiltFromSource);

        // 3. The process-wide set fills every name the packs did not supply; its dropped
        //    copies are summarized, because filling never changes what the packs claimed.
        var filled = 0;
        foreach (var (key, reference) in current.References)
        {
            var name = Path.GetFileNameWithoutExtension(key);
            if (collector.OriginByName.ContainsKey(name))
            {
                continue;
            }

            if (corePackOwnsCorlib && name.StartsWith(RuntimeImplementationPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            collector.References.Add((key, reference));
            collector.OriginByName.Add(name, (key, $"fallback ({current.Source})"));
            filled++;
        }

        AddNetFrameworkProjectNote(notes, root, moniker);

        if (collector.References.Count == 0 || collector.PackOrigins.Count == 0)
        {
            // Nothing came from a pack: the set is the process-wide one, under its own source
            // name (the self-contained smoke test greps for it), with the notes saying why.
            return new FrameworkReferenceSet(current.References, current.Source, JoinNotes(notes), current.Warnings);
        }

        var source = $"{TreePacksSourcePrefix}[{string.Join(",", collector.PackOrigins)}]+{current.Source}";
        DebugLog.Count($"tree framework reference packs ({root})", collector.PackOrigins.Count);
        DebugLog.Count("tree framework references from packs", collector.References.Count - filled);
        DebugLog.Count("tree framework references from the process-wide fallback", filled);
        return new FrameworkReferenceSet(collector.References, source, JoinNotes(notes), current.Warnings);
    }

    /// <summary>
    ///     A net4x tree binds the .NET Framework reference assemblies from the NuGet cache
    ///     (issue #127): restore auto-fetches the package the SDK compiles net4x against, so
    ///     net48 source binds mscorlib 4.0.0.0 instead of .NET 11 metadata a net48 build would
    ///     reject. The pack owns the whole set - mscorlib is the corlib, and Dosai's own runtime
    ///     assemblies beside it redeclare types into a corlib the compilation does not have - so
    ///     the process-wide set fills nothing and is not layered. Modern shared frameworks the
    ///     tree declares (WindowsDesktop via UseWindowsForms) are not layered either: the net4x
    ///     pack already carries their types. Without the package the tree degrades to the
    ///     process-wide set under a diagnostic naming what is missing, never a failure.
    /// </summary>
    private static FrameworkReferenceSet ResolveForNetFrameworkTree(string root, string moniker, FrameworkVersion version, List<(string Name, string Evidence)> detected, FrameworkReferenceSet current)
    {
        var notes = new List<string>();
        var tfm = MonikerOf(moniker);
        var packageId = $"microsoft.netframework.referenceassemblies.{tfm}";
        var canonicalPackageId = $"Microsoft.NETFramework.ReferenceAssemblies.{tfm}";
        var versionText = VersionText(version);
        var pack = ResolveNetFrameworkReferenceAssemblies(packageId, version, NuGetPackageRoots());
        if (pack is null)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture,
                $"No .NET Framework {versionText} reference assemblies (package {canonicalPackageId}) were found in the NuGet cache; the base framework references come from Dosai's own runtime ({current.Source}, .NET {Environment.Version.Major}), so APIs that differ between .NET Framework {versionText} and .NET {Environment.Version.Major} bind through a different base than the tree targets. Restoring the tree fetches the package."));
            return new FrameworkReferenceSet(current.References, current.Source, JoinNotes(notes), current.Warnings);
        }

        var collector = new TreePackCollector(notes, TreeFrameworks.SourceAssemblyNames(root));
        collector.AddDirectory(pack.Value.Directory, $"base framework .NET Framework {versionText} reference assemblies ({canonicalPackageId} {pack.Value.Version}, NuGet cache)", facadesDirectory: Path.Combine(pack.Value.Directory, "Facades"));
        notes.Add(string.Create(CultureInfo.InvariantCulture,
            $"Base framework references for target '{moniker}' come from the .NET Framework {versionText} reference assemblies ({canonicalPackageId} {pack.Value.Version}, NuGet cache); the process-wide runtime set fills nothing beside them."));
        foreach (var framework in detected.Where(framework => !string.Equals(framework.Name, "Microsoft.NETCore.App", StringComparison.OrdinalIgnoreCase)).OrderBy(framework => framework.Name, StringComparer.Ordinal))
        {
            notes.Add($"{framework.Name} via {framework.Evidence} was not layered over the .NET Framework reference assemblies; the net4x reference assemblies already contain its types.");
        }

        AddBuiltFromSourceNote(notes, collector.BuiltFromSource);

        if (collector.References.Count == 0 || collector.PackOrigins.Count == 0)
        {
            // The package directory exists but holds no readable assembly: the set stays the
            // process-wide one under its own source name, with the notes saying why.
            return new FrameworkReferenceSet(current.References, current.Source, JoinNotes(notes), current.Warnings);
        }

        // The net4x pack owns the whole set: no process-wide fill, and the source names the
        // pack alone (the pack-used shape carries no current.Diagnostic either).
        var source = $"{TreePacksSourcePrefix}[{string.Join(",", collector.PackOrigins)}]";
        DebugLog.Count($"tree framework reference packs ({root})", collector.PackOrigins.Count);
        DebugLog.Count("tree framework references from packs", collector.References.Count);
        return new FrameworkReferenceSet(collector.References, source, JoinNotes(notes), current.Warnings);
    }

    private static void AddBuiltFromSourceNote(List<string> notes, List<string> builtFromSource)
    {
        if (builtFromSource.Count == 0)
        {
            return;
        }

        var distinct = builtFromSource.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList();
        notes.Add($"{distinct.Count} framework pack assembl{(distinct.Count == 1 ? "y was" : "ies were")} left out because the tree builds {(distinct.Count == 1 ? "it" : "them")} from source ({string.Join(", ", distinct.Take(10))}{(distinct.Count > 10 ? ", ..." : string.Empty)}): a metadata copy beside the source makes their extension methods ambiguous.");
    }

    /// <summary>
    ///     The mixed-tree note (issue #127): a net4x project inside a tree whose root-wide base is
    ///     something else binds against that base, and the reader should see it. Scoped to net4x
    ///     projects on purpose: netstandard and netcoreapp targets sit below modern targets in the
    ///     representative ranking by design, so a family comparison would fire for every ordinary
    ///     solution holding a netstandard2.0 library beside a modern app.
    /// </summary>
    private static void AddNetFrameworkProjectNote(List<string> notes, string root, string? moniker)
    {
        var netFrameworkTargets = new List<string>();
        foreach (var project in TargetFrameworkDetection.ProjectsUnder(root, ".csproj").Concat(TargetFrameworkDetection.ProjectsUnder(root, ".fsproj")))
        {
            if (FrameworkPreprocessorDefines.TrySelectRepresentative(project.TargetFrameworks, out var projectRepresentative)
                && ClassifyTargetForReferencePacks(projectRepresentative, out _) == ReferencePackTargetFamily.NetFramework)
            {
                netFrameworkTargets.Add(projectRepresentative);
            }
        }

        if (netFrameworkTargets.Count == 0)
        {
            return;
        }

        var distinct = netFrameworkTargets.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList();
        notes.Add(string.Create(CultureInfo.InvariantCulture,
            $"Base framework references follow the root's representative target '{moniker ?? "<none>"}'; {netFrameworkTargets.Count} net4x project{(netFrameworkTargets.Count == 1 ? "" : "s")} targeting {string.Join(", ", distinct.Take(5))}{(distinct.Count > 5 ? ", ..." : string.Empty)} bind{(netFrameworkTargets.Count == 1 ? "s" : "")} against that base too (Metadata.ProjectGuardTargetFrameworks lists every project's target)."));
    }

    private static string? JoinNotes(List<string> notes) => notes.Count == 0 ? null : string.Join(" | ", notes);

    /// <summary>The tfm moniker reference-pack directories are named by (`net8.0-windows` -> `net8.0`).</summary>
    private static string MonikerOf(string targetFramework)
    {
        var normalized = targetFramework.Trim().ToLowerInvariant();
        var separator = normalized.IndexOf('-', StringComparison.Ordinal);
        return separator >= 0 ? normalized[..separator] : normalized;
    }

    /// <summary>A .NET Framework version rendered as the pack layouts name it (`4.8`, `4.7.2`).</summary>
    private static string VersionText(FrameworkVersion version) => version.Patch == 0
        ? string.Create(CultureInfo.InvariantCulture, $"{version.Major}.{version.Minor}")
        : string.Create(CultureInfo.InvariantCulture, $"{version.Major}.{version.Minor}.{version.Patch}");

    /// <summary>
    ///     Which reference-pack family a target moniker belongs to. netstandard and netcoreapp
    ///     targets, and monikers that parse as no known family, have no reference pack: the
    ///     process-wide set stays. The shared parse is the preprocessor-define one, so `net48` is
    ///     4.8 and never major 48.
    /// </summary>
    internal static ReferencePackTargetFamily ClassifyTargetForReferencePacks(string? moniker, out FrameworkVersion version)
    {
        version = default;
        if (moniker is null)
        {
            return ReferencePackTargetFamily.None;
        }

        var normalized = MonikerOf(moniker);
        if (normalized.StartsWith("netstandard", StringComparison.Ordinal) || normalized.StartsWith("netcoreapp", StringComparison.Ordinal) || !normalized.StartsWith("net", StringComparison.Ordinal))
        {
            return ReferencePackTargetFamily.None;
        }

        return FrameworkPreprocessorDefines.TryParseVersion(normalized["net".Length..], out version)
            ? version.Major >= 5 ? ReferencePackTargetFamily.ModernNet : ReferencePackTargetFamily.NetFramework
            : ReferencePackTargetFamily.None;
    }

    /// <summary>Reference-pack families a tree's representative target can resolve to.</summary>
    internal enum ReferencePackTargetFamily
    {
        /// <summary>netstandard, netcoreapp or nothing parseable: no reference pack, the process-wide set stays.</summary>
        None,

        /// <summary>Modern .NET (net5.0 and later): Microsoft.NETCore.App reference packs.</summary>
        ModernNet,

        /// <summary>.NET Framework (net4x and earlier): the reference-assemblies NuGet package.</summary>
        NetFramework,
    }

    /// <summary>
    ///     The .NET Framework reference assemblies of one net4x target: the exact package
    ///     (<c>microsoft.netframework.referenceassemblies.&lt;tfm&gt;</c>, the moniker without
    ///     separators) under the NuGet cache, highest version, layout
    ///     <c>build/.NETFramework/v&lt;Major.Minor[.Patch]&gt;/</c>. No cross-TFM substitution.
    /// </summary>
    private static (string Directory, string Version)? ResolveNetFrameworkReferenceAssemblies(string packageId, FrameworkVersion version)
        => ResolveNetFrameworkReferenceAssemblies(packageId, version, NuGetPackageRoots());

    /// <summary>Testable core: the package roots are supplied by the caller.</summary>
    internal static (string Directory, string Version)? ResolveNetFrameworkReferenceAssemblies(string packageId, FrameworkVersion version, IEnumerable<string> nugetPackageRoots)
    {
        (string Directory, Version Version, bool IsRelease)? best = null;
        foreach (var root in nugetPackageRoots)
        {
            var packageRoot = Path.Combine(root, packageId);
            if (!Directory.Exists(packageRoot))
            {
                continue;
            }

            foreach (var directory in Directory.EnumerateDirectories(packageRoot).Order(StringComparer.Ordinal))
            {
                var label = Path.GetFileName(directory);
                var (parsed, isRelease) = ParseVersion(label);
                if (parsed <= new Version(0, 0))
                {
                    continue;
                }

                if (best is null || parsed > best.Value.Version || (parsed == best.Value.Version && isRelease && !best.Value.IsRelease))
                {
                    best = (directory, parsed, isRelease);
                }
            }
        }

        if (best is not { } found)
        {
            return null;
        }

        var referenceDirectory = Path.Combine(found.Directory, "build", ".NETFramework", $"v{VersionText(version)}");
        return Directory.Exists(referenceDirectory) ? (referenceDirectory, Path.GetFileName(found.Directory)) : null;
    }

    /// <summary>
    ///     Collects one tree's pack references: exactly one reference per assembly simple name
    ///     claimed in pack order, the built-from-source drop, and one origin record per pack (a
    ///     pack that brings a main directory and a facades directory is recorded once).
    /// </summary>
    private sealed class TreePackCollector(List<string> notes, IReadOnlySet<string> sourceAssemblyNames)
    {
        internal readonly List<(string Key, PortableExecutableReference Reference)> References = [];
        internal readonly Dictionary<string, (string Key, string Origin)> OriginByName = new(StringComparer.OrdinalIgnoreCase);
        internal readonly List<string> PackOrigins = [];
        internal readonly List<string> BuiltFromSource = [];

        internal void AddDirectory(string directory, string origin, bool isBasePack = false, string? facadesDirectory = null)
        {
            var added = 0;
            var dropped = 0;
            foreach (var assemblyPath in EnumerateAssemblies(directory).Concat(facadesDirectory is null ? [] : EnumerateAssemblies(facadesDirectory)))
            {
                var name = Path.GetFileNameWithoutExtension(assemblyPath);
                // The tree compiles this assembly from source: a pack copy would duplicate its
                // types in metadata. The base pack is exempt, because the process-wide set fills
                // the same names anyway and has always stood beside source copies of them.
                if (!isBasePack && sourceAssemblyNames.Contains(name))
                {
                    dropped++;
                    BuiltFromSource.Add(name);
                    continue;
                }

                if (OriginByName.TryGetValue(name, out var existing))
                {
                    dropped++;
                    notes.Add($"framework assembly '{name}' of '{origin}' skipped: already provided by {existing.Origin} (one reference per assembly name; the pack matching the analyzed target wins).");
                    continue;
                }

                if (TryCreateFromFile(assemblyPath, notes) is { } reference)
                {
                    References.Add((assemblyPath, reference));
                    OriginByName.Add(name, (assemblyPath, origin));
                    added++;
                }
            }

            if (added > 0)
            {
                PackOrigins.Add(origin);
                DebugLog.Log($"tree framework reference pack: {origin} -> {added} reference(s), {dropped} dropped as duplicate(s)");
            }
        }

        private static IEnumerable<string> EnumerateAssemblies(string directory) =>
            Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*" + Constants.AssemblyExtension).Order(StringComparer.Ordinal) : [];
    }

    /// <summary>
    ///     The reference-pack (or shared-framework) directory for one framework name. Candidates
    ///     are gathered from the pack directories under every dotnet root and the NuGet cache
    ///     together (a target-matched pack that only restore downloaded must not lose to an
    ///     installed pack of another major), then from the installed shared frameworks only when
    ///     no reference pack of any major exists. A pack of version N carries <c>ref/netN.0</c>,
    ///     so an exact-major pack is read from the analyzed moniker's directory and another
    ///     major's pack from its own. Version choice: the exact major, else the nearest major
    ///     above the target, else the nearest below; within a major the highest version,
    ///     releases over prereleases. A non-exact major always adds a note naming the version.
    /// </summary>
    private static (string Directory, string Version, bool ExactMajor)? ResolvePackDirectory(string frameworkName, string moniker, int targetMajor, List<string> notes)
        => ResolvePackDirectory(frameworkName, moniker, targetMajor, notes, PackDirectories(), NuGetPackageRoots(), InstalledSharedRoots());

    /// <summary>Testable core: the pack, NuGet-cache and shared-framework roots are supplied by the caller.</summary>
    internal static (string Directory, string Version, bool ExactMajor)? ResolvePackDirectory(
        string frameworkName,
        string moniker,
        int targetMajor,
        List<string> notes,
        IEnumerable<string> packDirectories,
        IEnumerable<string>? nugetPackageRoots = null,
        IEnumerable<string>? sharedRoots = null)
    {
        // The desktop sub-frameworks (.WPF/.WindowsForms) share one reference pack and one
        // shared framework.
        var isDesktop = frameworkName.StartsWith("Microsoft.WindowsDesktop.App", StringComparison.OrdinalIgnoreCase);
        var packName = isDesktop ? "Microsoft.WindowsDesktop.App.Ref" : frameworkName + ".Ref";
        var sharedName = isDesktop ? "Microsoft.WindowsDesktop.App" : frameworkName;
        var candidates = new List<VersionedDirectory>();

        void CollectPacks(string packRoot)
        {
            if (!Directory.Exists(packRoot))
            {
                return;
            }

            foreach (var versionDirectory in Directory.EnumerateDirectories(packRoot).Order(StringComparer.Ordinal))
            {
                var label = Path.GetFileName(versionDirectory);
                var (version, isRelease) = ParseVersion(label);
                if (version <= new Version(0, 0))
                {
                    continue;
                }

                var referenceMoniker = version.Major == targetMajor
                    ? moniker
                    : string.Create(CultureInfo.InvariantCulture, $"net{version.Major}.0");
                var referenceDirectory = Path.Combine(versionDirectory, "ref", referenceMoniker);
                if (Directory.Exists(referenceDirectory))
                {
                    candidates.Add(new VersionedDirectory(referenceDirectory, version, isRelease, label));
                }
            }
        }

        foreach (var packsDirectory in packDirectories)
        {
            CollectPacks(Path.Combine(packsDirectory, packName));
        }

        foreach (var nugetRoot in nugetPackageRoots ?? [])
        {
            // The NuGet cache keeps framework ref packs under their package id, lowercased.
            CollectPacks(Path.Combine(nugetRoot, packName.ToLowerInvariant()));
        }

        if (SelectNearestMajor(candidates, targetMajor) is { } pack)
        {
            if (pack.Version.Major != targetMajor)
            {
                notes.Add($"No {targetMajor}.x reference pack for '{frameworkName}' is installed; using reference pack {pack.Label} instead, so APIs that differ between .NET {targetMajor} and .NET {pack.Version.Major} may bind differently.");
            }

            return (pack.Directory, pack.Label, pack.Version.Major == targetMajor);
        }

        // Last resort: an installed shared framework's runtime directory, chosen the same way.
        var shared = new List<VersionedDirectory>();
        foreach (var sharedRoot in sharedRoots ?? [])
        {
            var frameworkRoot = Path.Combine(sharedRoot, sharedName);
            if (!Directory.Exists(frameworkRoot))
            {
                continue;
            }

            foreach (var directory in Directory.EnumerateDirectories(frameworkRoot).Order(StringComparer.Ordinal))
            {
                var label = Path.GetFileName(directory);
                var (version, isRelease) = ParseVersion(label);
                if (version > new Version(0, 0))
                {
                    shared.Add(new VersionedDirectory(directory, version, isRelease, label));
                }
            }
        }

        if (SelectNearestMajor(shared, targetMajor) is { } runtime)
        {
            notes.Add(runtime.Version.Major == targetMajor
                ? $"No reference pack for '{frameworkName}' is installed; using the installed shared framework {runtime.Label} (runtime implementation assemblies) instead."
                : $"No {targetMajor}.x reference pack for '{frameworkName}' is installed; using the installed shared framework {runtime.Label} instead.");
            return (runtime.Directory, runtime.Label, runtime.Version.Major == targetMajor);
        }

        return null;
    }

    private readonly record struct VersionedDirectory(string Directory, Version Version, bool IsRelease, string Label);

    /// <summary>
    ///     Exact major first, then the nearest major above the target, then the nearest below;
    ///     within a major the highest version, releases over prereleases, then the path (so two
    ///     roots holding one version resolve the same way on every run).
    /// </summary>
    private static VersionedDirectory? SelectNearestMajor(List<VersionedDirectory> candidates, int targetMajor)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        // Above the target ranks before the same distance below: a newer framework's API is
        // close to a superset of the target's, an older one misses what the target added.
        static int Distance(int major, int target) => major == target ? 0 : major > target ? ((major - target) * 2) - 1 : (target - major) * 2;
        return candidates
            .OrderBy(candidate => Distance(candidate.Version.Major, targetMajor))
            .ThenByDescending(candidate => candidate.Version)
            .ThenByDescending(candidate => candidate.IsRelease)
            .ThenBy(candidate => candidate.Directory, StringComparer.Ordinal)
            .First();
    }

    /// <summary>NuGet's global packages folder: <c>NUGET_PACKAGES</c> when set, else <c>~/.nuget/packages</c>.</summary>
    private static readonly AsyncLocal<string?> NuGetRootOverride = new();

    /// <summary>Test hook: the NuGet global packages folder for the current execution context and the threads it starts.</summary>
    internal static IDisposable OverrideNuGetRootForTesting(string root)
    {
        var previous = NuGetRootOverride.Value;
        NuGetRootOverride.Value = root;
        return new Restore(() => NuGetRootOverride.Value = previous);
    }

    /// <summary>
    ///     A metadata file for <paramref name="assemblyName" /> outside the analyzed tree: a
    ///     reference pack (<c>packs/*/*/ref/*</c>), an installed shared framework, else the NuGet
    ///     global packages folder (<c>&lt;id&gt;/&lt;version&gt;/lib|ref/&lt;tfm&gt;</c>, the package named
    ///     like the assembly), newest version first. Used to read a callee's parameter names when
    ///     only an assembly references it. Null when none is installed.
    /// </summary>
    internal static string? FindInstalledAssembly(string assemblyName)
    {
        var fileName = assemblyName + ".dll";
        try
        {
            foreach (var packs in PackDirectories())
            {
                var hit = SafeEnumerate(packs, fileName, depth: 4).OrderByDescending(path => path, StringComparer.Ordinal).FirstOrDefault();
                if (hit is not null) return hit;
            }

            foreach (var shared in Dosai.GetDotnetSharedRuntimeRoots().Where(Directory.Exists))
            {
                var hit = SafeEnumerate(shared, fileName, depth: 2).OrderByDescending(path => path, StringComparer.Ordinal).FirstOrDefault();
                if (hit is not null) return hit;
            }

            foreach (var root in NuGetPackageRoots())
            {
                var package = Path.Combine(root, assemblyName.ToLowerInvariant());
                if (!Directory.Exists(package)) continue;
                var hit = Directory.EnumerateDirectories(package)
                    .OrderByDescending(version => Version.TryParse(Path.GetFileName(version).Split('-')[0], out var parsed) ? parsed : new Version(0, 0))
                    .ThenByDescending(version => version, StringComparer.Ordinal)
                    .SelectMany(version => new[] { "lib", "ref" }.Select(kind => Path.Combine(version, kind)).Where(Directory.Exists))
                    .SelectMany(kind => Directory.EnumerateDirectories(kind).OrderByDescending(tfm => tfm, StringComparer.Ordinal))
                    .Select(tfm => Path.Combine(tfm, fileName))
                    .FirstOrDefault(File.Exists);
                if (hit is not null) return hit;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;

        // <dir>/<a>/<b>/.../<file> up to depth levels below: pack and framework layouts are fixed-depth.
        static IEnumerable<string> SafeEnumerate(string directory, string file, int depth)
        {
            IEnumerable<string> level = [directory];
            for (var i = 0; i < depth; i++)
            {
                level = level.SelectMany(path => Directory.Exists(path) ? Directory.EnumerateDirectories(path) : []).ToList();
                foreach (var candidate in level.Select(path => Path.Combine(path, file)).Where(File.Exists).ToList())
                {
                    yield return candidate;
                }
            }
        }
    }

    private static IEnumerable<string> NuGetPackageRoots()
    {
        if (NuGetRootOverride.Value is { Length: > 0 } overridden)
        {
            return [overridden];
        }

        if (Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } configured)
        {
            return [configured];
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { Length: > 0 } home
            ? [Path.Combine(home, ".nuget", "packages")]
            : [];
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
