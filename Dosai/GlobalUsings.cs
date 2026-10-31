using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Depscan;

/// <summary>
///     The implicit and explicit global usings one scan root's compilation should carry,
///     resolved from the SDKs its projects use, the projects' <c>&lt;Using&gt;</c> items, and
///     MSBuild's generated <c>GlobalUsings.g.cs</c> (issue #74).
/// </summary>
/// <remarks>
///     <para>
///         The SDK additions are the lists the SDKs declare on disk, not from memory:
///     </para>
///     <list type="bullet">
///         <item>base C#: <c>Sdks/Microsoft.NET.Sdk/targets/Microsoft.NET.Sdk.CSharp.props</c></item>
///         <item>Web: <c>Sdks/Microsoft.NET.Sdk.Web/Targets/Sdk.Server.props</c></item>
///         <item>Worker: <c>Sdks/Microsoft.NET.Sdk.Worker/targets/Microsoft.NET.Sdk.Worker.props</c></item>
///         <item>Blazor WebAssembly: <c>Sdks/Microsoft.NET.Sdk.BlazorWebAssembly/Sdk/Sdk.props</c></item>
///         <item>Windows Forms: <c>Sdks/Microsoft.NET.Sdk.WindowsDesktop/targets/Microsoft.NET.Sdk.WindowsDesktop.WindowsForms.props</c></item>
///     </list>
///     <para>
///         Verified byte-identical between the SDKs installed on the development machines at
///         the time of writing: macOS <c>/usr/local/share/dotnet/sdk/{10.0.302,
///         11.0.100-rc.1.26425.128}</c> and the Windows VM <c>C:\Program Files\dotnet\sdk\
///         {10.0.400, 11.0.100-rc.1.26425.128}</c>, and unchanged in macOS
///         <c>~/.dotnet/sdk/11.0.100-rc.2.26504.105</c>, except that only the 11 SDKs add the
///         base <c>System.Net.Http.Json</c> using (gated on .NET 11+), which is applied by
///         target framework here rather than by SDK version. The Razor and WPF SDKs add no
///         usings of their own on disk (Razor's
///         <c>Microsoft.Extensions.Validation.Embedded</c> using is opt-in through
///         <c>IncludeEmbeddedValidationGlobalUsing</c> and default-off, so it is not added).
///     </para>
///     <para>
///         Global usings are compilation-wide and a tree is one compilation. Projects whose
///         final sets agree share one synthetic global-usings tree. Projects that disagree get
///         no such tree: each file receives its own project's set as file-local directives
///         (<see cref="PerFileUsings" />, issue #110), so one project's usings never make
///         another project's names ambiguous. A using that does not resolve in the
///         compilation is an error on that directive only and does not stop other bindings -
///         pinned by test. Projects whose sets differ produce one diagnostic naming them.
///     </para>
/// </remarks>
internal static partial class GlobalUsings
{
    /// <summary>Microsoft.NET.Sdk's C# implicit usings (Microsoft.NET.Sdk.CSharp.props); System.Net.Http is skipped for .NETFramework targets in the loop below.</summary>
    private static readonly string[] BaseSdkUsings =
    [
        "System",
        "System.Collections.Generic",
        "System.IO",
        "System.Linq",
        "System.Net.Http",
        "System.Threading",
        "System.Threading.Tasks"
    ];

    /// <summary>
    ///     Microsoft.NET.Sdk.Web's additions (Sdk.Server.props, identical in the 10 and 11
    ///     SDKs): gated on ImplicitUsings and C#, added when the TFM is .NETCoreApp 3.0+.
    /// </summary>
    private static readonly string[] WebSdkUsings =
    [
        "System.Net.Http.Json",
        "Microsoft.AspNetCore.Builder",
        "Microsoft.AspNetCore.Hosting",
        "Microsoft.AspNetCore.Http",
        "Microsoft.AspNetCore.Routing",
        "Microsoft.Extensions.Configuration",
        "Microsoft.Extensions.DependencyInjection",
        "Microsoft.Extensions.Hosting",
        "Microsoft.Extensions.Logging"
    ];

    /// <summary>Microsoft.NET.Sdk.Worker's additions (Microsoft.NET.Sdk.Worker.props).</summary>
    private static readonly string[] WorkerSdkUsings =
    [
        "Microsoft.Extensions.Configuration",
        "Microsoft.Extensions.DependencyInjection",
        "Microsoft.Extensions.Hosting",
        "Microsoft.Extensions.Logging"
    ];

    /// <summary>
    ///     Microsoft.NET.Sdk.BlazorWebAssembly's additions (Sdk.props, identical in the 10 and
    ///     11 SDKs): gated on ImplicitUsings and C# like the Web list.
    /// </summary>
    private static readonly string[] BlazorWebAssemblySdkUsings =
    [
        "Microsoft.Extensions.Configuration",
        "Microsoft.Extensions.DependencyInjection",
        "Microsoft.Extensions.Logging"
    ];

    /// <summary>Windows Forms additions (Microsoft.NET.Sdk.WindowsDesktop.WindowsForms.props), gated on UseWindowsForms.</summary>
    private static readonly string[] WindowsFormsSdkUsings = ["System.Drawing", "System.Windows.Forms"];

    private static readonly Lock ResolveLock = new();
    private static readonly Dictionary<string, GlobalUsingsDecision> DecisionsByRoot = new(SafeFileRead.PathComparer);

    // The decision by the path a caller passed, read without the lock: the parse asks once per
    // file from every worker (issue #110's per-file usings), always with the scan root.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, GlobalUsingsDecision> DecisionsByPath = new(StringComparer.Ordinal);

    /// <summary>Forgets every per-root decision (see <see cref="TargetFrameworkDetection.ResetCaches" />): a build writes GlobalUsings.g.cs and restore writes project.assets.json between two MCP calls.</summary>
    internal static void ResetCache()
    {
        lock (ResolveLock)
        {
            DecisionsByRoot.Clear();
            DecisionsByPath.Clear();
        }
    }

    public static GlobalUsingsDecision Resolve(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new GlobalUsingsDecision(false, null, []);
        }

        if (DecisionsByPath.TryGetValue(path, out var known))
        {
            return known;
        }

        string root;
        try
        {
            root = TargetFrameworkDetection.ProjectContextRoot(Path.GetFullPath(path));
        }
        catch (ArgumentException)
        {
            return new GlobalUsingsDecision(false, null, []);
        }

        lock (ResolveLock)
        {
            if (DecisionsByRoot.TryGetValue(root, out var cached))
            {
                DecisionsByPath[path] = cached;
                return cached;
            }

            var decision = ResolveNoCache(root);
            DecisionsByRoot[root] = decision;
            DecisionsByPath[path] = decision;
            return decision;
        }
    }

    private static GlobalUsingsDecision ResolveNoCache(string root)
    {
        var diagnostics = new List<string>();
        try
        {
            var projects = SafeFileRead.EnumerateAllFilesSafe(root, "*.csproj")
                .Where(file => !IsUnderBuildDirectory(root, file))
                .Order(SafeFileRead.PathComparer)
                .ToList();
            var projectSets = new List<(string Project, List<string> Directives)>();
            // Every owner of files - each project, enabling implicit usings or not, and each
            // file-based app - with the set its files compile with.
            var owners = new List<(string Key, bool IsFile, List<string> Directives)>();
            void AddSet(string file, List<string> directives) => projectSets.Add((Path.GetRelativePath(root, file), NormalizeSet(directives)));

            foreach (var project in projects)
            {
                var directives = ProjectUsings(root, project, diagnostics);
                owners.Add((Path.GetDirectoryName(project)!, false, NormalizeSet(directives)));
                if (directives.Count > 0)
                {
                    AddSet(project, directives);
                }
            }

            foreach (var app in FileBasedApps(root, projects))
            {
                var directives = FileBasedAppUsings(root, app.File, app.Text, diagnostics);
                owners.Add((app.File, true, NormalizeSet(directives)));
                if (directives.Count > 0)
                {
                    AddSet(app.File, directives);
                }
            }

            if (projectSets.Count == 0)
            {
                return new GlobalUsingsDecision(false, null, diagnostics);
            }

            // Owners that disagree (a Web SDK project beside a class library, or a classic project
            // beside an implicit-usings one): a global using is compilation-wide, so one union
            // tree would hand each file the other projects' usings and make names such as
            // ILogger ambiguous (issue #110). The usings every owner has stay global, and each
            // file gets the rest of its own owner's set as file-local using directives (see
            // PerFileUsings); the compilation stays one.
            var distinctSets = owners.Select(owner => string.Join('\n', owner.Directives.Order(StringComparer.Ordinal))).Distinct(StringComparer.Ordinal).Count();
            if (distinctSets > 1)
            {
                var differing = owners
                    .Where(owner => !owner.Directives.Order(StringComparer.Ordinal).SequenceEqual(owners[0].Directives.Order(StringComparer.Ordinal)))
                    .Select(owner => Path.GetRelativePath(root, owner.Key).Replace('\\', '/'))
                    .ToList();
                diagnostics.Add($"Global usings differ between projects ({string.Join(", ", differing)}); the usings every project shares stay global, each file gets the rest of its own project's set as file-local using directives, and a file outside every project gets the union of every project's set. The directives are written after the file's last using, else on a blank or comment line before its code, else (code on the first line and no using) before that code, which shifts the columns of that one line.");
                var union = new List<string>();
                var unionSeen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var (_, directives) in projectSets)
                {
                    union.AddRange(directives.Where(directive => unionSeen.Add(directive)));
                }

                var common = owners[0].Directives
                    .Where(directive => owners.All(owner => owner.Directives.Contains(directive, StringComparer.Ordinal)))
                    .ToList();
                var commonTree = common.Count == 0 ? null : GlobalTree(common);
                return new GlobalUsingsDecision(true, commonTree, diagnostics) { PerFile = new PerFileUsings(root, owners, union, common) };
            }

            // Every owner has the same set: one global tree carries it.
            var merged = projectSets[0].Directives.Distinct(StringComparer.Ordinal).ToList();
            return merged.Count == 0
                ? new GlobalUsingsDecision(false, null, diagnostics)
                : new GlobalUsingsDecision(true, GlobalTree(merged), diagnostics);

            CSharpSyntaxTree GlobalTree(List<string> directives) =>
                CSharpSourceParser.ParseWithoutFileUsings(string.Join(Environment.NewLine, directives.Select(directive => $"global using {directive};")), "<implicit-usings>", root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Best effort: analysis continues with explicit-usings semantics.
            return new GlobalUsingsDecision(false, null, diagnostics);
        }
    }

    private static List<string> NormalizeSet(List<string> directives)
    {
        var unique = new HashSet<string>(StringComparer.Ordinal);
        var normalized = new List<string>(directives.Count);
        foreach (var directive in directives)
        {
            if (NormalizeDirective(directive) is { Length: > 0 } spelled && unique.Add(spelled))
            {
                normalized.Add(spelled);
            }
        }

        return normalized;
    }

    /// <summary>The using directives of one project: MSBuild's generated file when it is authoritative, else the SDK list plus the project's Using items.</summary>
    private static List<string> ProjectUsings(string root, string projectFile, List<string> diagnostics) =>
        SafeFileRead.TryReadAllText(projectFile, out var project)
            ? ProjectUsings(root, projectFile, project, null, diagnostics)
            : [];

    /// <summary>
    ///     <paramref name="virtualTargets" /> is set for a file-based app's virtual project: it
    ///     has no file for the target detection to read and no build output in the tree (the SDK
    ///     builds it in a per-user cache), so its targets come from its directives instead.
    /// </summary>
    private static List<string> ProjectUsings(string root, string projectFile, string project, List<string>? virtualTargets, List<string> diagnostics)
    {
        var projectDirectory = Path.GetDirectoryName(projectFile)!;
        var sdks = ProjectSdks(project);
        var implicitUsings = PropertyWithInheritance(root, projectDirectory, project, "ImplicitUsings");
        if (implicitUsings is not ("enable" or "true"))
        {
            // Disabled or unset (an unevaluable condition counts as unset, noted when present):
            // inventing the global usings would bind calls the project's own compiler rejects.
            return [];
        }

        // The project's targets as the guard evaluation sees them (its own file, else the
        // nearest Directory.Build.props/targets): a monorepo commonly sets TargetFramework once
        // in Directory.Build.props, and a regex over the project file alone missed it.
        var targets = virtualTargets ?? TargetFrameworkDetection.ForFile(root, projectFile, ".csproj").TargetFrameworks;
        var representative = FrameworkPreprocessorDefines.TrySelectRepresentative(targets, out var selected) ? selected : null;
        var isNetFramework = representative?.StartsWith("net4", StringComparison.OrdinalIgnoreCase) == true;

        // MSBuild's own output is authoritative when it exists for the resolved target and is
        // not older than the project file; obj/ is excluded from discovery, so it is read here
        // explicitly (issue #74).
        if (virtualTargets is null && GeneratedUsingsFile(projectDirectory, projectFile, representative, diagnostics) is { } generated)
        {
            return generated;
        }

        var directives = new List<string>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        void Add(string name)
        {
            if (names.Add(name))
            {
                directives.Add(name);
            }
        }

        foreach (var name in BaseSdkUsings)
        {
            // Microsoft.NET.Sdk.CSharp.props drops System.Net.Http on .NETFramework targets.
            if (name == "System.Net.Http" && isNetFramework)
            {
                continue;
            }

            Add(name);
        }

        if (!isNetFramework && representative is not null && MajorOf(representative) >= 11)
        {
            // The 11 SDKs add System.Net.Http.Json for .NETCoreApp >= 11.0.
            Add("System.Net.Http.Json");
        }

        if (sdks.Contains("Microsoft.NET.Sdk.Web"))
        {
            foreach (var name in WebSdkUsings)
            {
                Add(name);
            }
        }

        if (sdks.Contains("Microsoft.NET.Sdk.Worker"))
        {
            foreach (var name in WorkerSdkUsings)
            {
                Add(name);
            }
        }

        if (sdks.Contains("Microsoft.NET.Sdk.BlazorWebAssembly"))
        {
            foreach (var name in BlazorWebAssemblySdkUsings)
            {
                Add(name);
            }
        }

        if (PropertyIsTrue(project, "UseWindowsForms", root, projectDirectory))
        {
            foreach (var name in WindowsFormsSdkUsings)
            {
                Add(name);
            }
        }

        ApplyUsingItems(root, projectFile, project, projectDirectory, directives, names, diagnostics);
        return directives;
    }

    /// <summary>
    ///     The file-based apps (<c>dotnet run app.cs</c>) of a scan root: C# files outside every
    ///     project's directory that open with a <c>#!</c> line or <c>#:</c> directives, or that
    ///     hold top-level statements, which only an entry point can. A file inside a project's
    ///     directory is that project's source (the SDK's default compile glob).
    /// </summary>
    private static List<(string File, string Text)> FileBasedApps(string root, List<string> projects)
    {
        var projectDirectories = new HashSet<string>(projects.Select(project => Path.GetDirectoryName(project)!), SafeFileRead.PathComparer);
        var apps = new List<(string File, string Text)>();
        foreach (var file in SafeFileRead.EnumerateAllFilesSafe(root, "*" + Constants.CSharpSourceExtension).Order(SafeFileRead.PathComparer))
        {
            if (IsUnderBuildDirectory(root, file) || IsInsideProject(file) || !SafeFileRead.TryReadAllText(file, out var text))
            {
                continue;
            }

            if (FileBasedAppHead(text).Marked || HasTopLevelStatements(text, file))
            {
                apps.Add((file, text));
            }
        }

        return apps;

        bool IsInsideProject(string file)
        {
            for (var directory = Path.GetDirectoryName(file); directory is not null; directory = Path.GetDirectoryName(directory))
            {
                if (projectDirectories.Contains(directory))
                {
                    return true;
                }

                if (string.Equals(directory, root, SafeFileRead.PathComparison))
                {
                    break;
                }
            }

            return false;
        }

        bool HasTopLevelStatements(string text, string file) =>
            CSharpSourceParser.ParseWithoutFileUsings(text, file, root).GetRoot() is Microsoft.CodeAnalysis.CSharp.Syntax.CompilationUnitSyntax unit &&
            unit.Members.Any(member => member is Microsoft.CodeAnalysis.CSharp.Syntax.GlobalStatementSyntax);
    }

    /// <summary>
    ///     The usings of a file-based app, through the virtual project the SDK generates for it
    ///     (what <c>dotnet project convert</c> writes, checked with the 11.0.100-rc.2 SDK):
    ///     <c>Microsoft.NET.Sdk</c> unless <c>#:sdk</c> names another, <c>ImplicitUsings</c>
    ///     enabled, the SDK's own target framework, then each <c>#:property</c> in file order.
    ///     The nearest Directory.Build.props/targets apply as they do to a project.
    /// </summary>
    private static List<string> FileBasedAppUsings(string root, string file, string text, List<string> diagnostics)
    {
        var head = FileBasedAppHead(text);
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["TargetFramework"] = $"net{FrameworkPreprocessorDefines.LatestModernNetMajor}.0",
            ["ImplicitUsings"] = "enable"
        };
        foreach (var (name, value) in head.Properties)
        {
            properties[name] = value;
        }

        var project = new System.Text.StringBuilder();
        project.Append("<Project Sdk=\"").Append(System.Security.SecurityElement.Escape(string.Join(",", head.Sdks.DefaultIfEmpty("Microsoft.NET.Sdk")))).Append("\"><PropertyGroup>");
        foreach (var (name, value) in properties)
        {
            project.Append('<').Append(name).Append('>').Append(System.Security.SecurityElement.Escape(value)).Append("</").Append(name).Append('>');
        }

        project.Append("</PropertyGroup></Project>");
        var targetFramework = properties["TargetFramework"];
        List<string> targets = targetFramework.Contains("$(", StringComparison.Ordinal) ? [] : [targetFramework.Trim().ToLowerInvariant()];
        return ProjectUsings(root, Path.ChangeExtension(file, ".csproj"), project.ToString(), targets, diagnostics);
    }

    /// <summary>
    ///     The <c>#:</c> directives a file-based app opens with: the compiler accepts them only
    ///     before the first token, after an optional <c>#!</c> line. <c>#:sdk Name@Version</c>
    ///     keeps the name; <c>#:property Name=Value</c> keeps the value, unquoted (quoted values
    ///     arrived in .NET 11 RC 2).
    /// </summary>
    internal static (bool Marked, List<string> Sdks, List<(string Name, string Value)> Properties) FileBasedAppHead(string text)
    {
        var marked = false;
        var sdks = new List<string>();
        var properties = new List<(string Name, string Value)>();
        foreach (var rawLine in text.AsSpan().EnumerateLines())
        {
            var line = rawLine.Trim();
            if (line.IsEmpty || line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.StartsWith("#!", StringComparison.Ordinal))
            {
                marked = true;
                continue;
            }

            if (!line.StartsWith("#:", StringComparison.Ordinal))
            {
                break;
            }

            marked = true;
            var directive = FileBasedDirectiveRegex().Match(line[2..].ToString());
            if (!directive.Success)
            {
                continue;
            }

            var value = directive.Groups["value"].Value.Trim();
            if (directive.Groups["kind"].Value.Equals("sdk", StringComparison.OrdinalIgnoreCase))
            {
                var at = value.IndexOf('@');
                sdks.Add(at > 0 ? value[..at] : value);
            }
            else if (directive.Groups["kind"].Value.Equals("property", StringComparison.OrdinalIgnoreCase) &&
                     PropertyDirectiveRegex().Match(value) is { Success: true } property)
            {
                properties.Add((property.Groups["name"].Value, property.Groups["value"].Value.Trim().Trim('"')));
            }
        }

        return (marked, sdks, properties);
    }

    private static void ApplyUsingItems(string root, string projectFile, string project, string projectDirectory, List<string> directives, HashSet<string> names, List<string> diagnostics)
    {
        // <Using> items in MSBuild evaluation order: the nearest Directory.Build.props (imported
        // before the project body), the project file, then the nearest Directory.Build.targets
        // (imported after it). Order matters for Remove: a project's <Using Remove> drops an
        // Include its Directory.Build.props made, and a Directory.Build.targets Remove drops the
        // project's. Conditions are not evaluated: the item is applied and the skipped condition
        // reported, because an extra using that does not resolve errors alone and never blocks
        // other bindings.
        var items = new List<(string File, Match Match)>();
        void AddItems(string file, string content)
        {
            foreach (var match in UsingItemRegex().Matches(WithoutXmlComments(content)).Cast<Match>())
            {
                items.Add((file, match));
            }
        }

        string? NearestBuildFile(string buildFile)
        {
            for (var directory = projectDirectory; directory is not null; directory = Path.GetDirectoryName(directory))
            {
                var candidate = Path.Combine(directory, buildFile);
                if (File.Exists(candidate) && !IsUnderBuildDirectory(root, candidate))
                {
                    return candidate;
                }

                if (string.Equals(directory, root, SafeFileRead.PathComparison))
                {
                    break;
                }
            }

            return null;
        }

        if (NearestBuildFile("Directory.Build.props") is { } props && SafeFileRead.TryReadAllText(props, out var propsContent))
        {
            AddItems(props, propsContent);
        }

        AddItems(projectFile, project);
        if (NearestBuildFile("Directory.Build.targets") is { } targets && SafeFileRead.TryReadAllText(targets, out var targetsContent))
        {
            AddItems(targets, targetsContent);
        }

        foreach (var (file, item) in items)
        {
            var attributes = Attributes(item);
            if (attributes.TryGetValue("Condition", out var condition))
            {
                diagnostics.Add($"Using item in '{Path.GetRelativePath(root, file)}' carries the condition '{condition}', which Dosai does not evaluate; the using is applied unconditionally.");
            }

            if (attributes.TryGetValue("Remove", out var removeList))
            {
                // An item list: MSBuild splits Include/Remove on ';'.
                foreach (var remove in SplitItemList(removeList))
                {
                    var pattern = UsingDirectiveName(remove);
                    for (var index = directives.Count - 1; index >= 0; index--)
                    {
                        if (UsingDirectiveName(directives[index]) == pattern)
                        {
                            names.Remove(directives[index]);
                            directives.RemoveAt(index);
                        }
                    }
                }

                continue;
            }

            if (!attributes.TryGetValue("Include", out var includeList))
            {
                continue;
            }

            foreach (var include in SplitItemList(includeList))
            {
                var alias = attributes.GetValueOrDefault("Alias");
                var isStatic = attributes.GetValueOrDefault("Static", "").Equals("true", StringComparison.OrdinalIgnoreCase);
                if (alias is { Length: > 0 })
                {
                    var directive = $"{alias} = {include}";
                    if (names.Add(directive))
                    {
                        directives.Add(directive);
                    }
                }
                else
                {
                    var directive = isStatic ? $"static {include}" : include;
                    if (names.Add(directive))
                    {
                        directives.Add(directive);
                    }
                }
            }
        }
    }

    /// <summary>An MSBuild item list split the way MSBuild splits it: on ';', trimmed, empty entries dropped.</summary>
    private static IEnumerable<string> SplitItemList(string value) =>
        value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>A project file's text with its XML comments blanked: a commented-out <c>&lt;Using&gt;</c> is not an item.</summary>
    internal static string WithoutXmlComments(string content) => XmlCommentRegex().Replace(content, string.Empty);

    /// <summary>
    ///     <paramref name="tree" /> with its project's global usings written in as file-local
    ///     directives when the tree's projects disagree (see <see cref="PerFileUsings" />), else
    ///     the same tree. Directives the file already declares are not repeated.
    /// </summary>
    internal static CSharpSyntaxTree WithFileUsings(CSharpSyntaxTree tree, string rootPath)
    {
        if (Resolve(rootPath).PerFile is not { } perFile || tree.GetRoot() is not CompilationUnitSyntax root || (root.Members.Count == 0 && root.AttributeLists.Count == 0))
        {
            return tree;
        }

        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var existing in root.Usings)
        {
            declared.Add(NormalizeDirective(DirectiveText(existing)));
        }

        var missing = perFile.SetFor(tree.FilePath).Where(directive => !declared.Contains(NormalizeDirective(directive))).ToList();
        if (missing.Count == 0)
        {
            return tree;
        }

        var offset = InsertionOffset(root, tree.GetText());
        var insertion = string.Concat(missing.Select(directive => $"using {directive};"));
        var rewritten = (CSharpSyntaxTree)tree.WithChangedText(tree.GetText().WithChanges(new TextChange(new TextSpan(offset, 0), insertion)));
        InsertedSpans.AddOrUpdate(rewritten, new InsertedUsings(new TextSpan(offset, insertion.Length)));
        return rewritten;
    }

    /// <summary>
    ///     The span of the using directives <see cref="WithFileUsings" /> wrote into <paramref name="tree" />,
    ///     or null. A consumer that reports the file's own directives (the <c>Dependencies</c> of
    ///     <c>methods</c>) leaves the directives inside it out: the file does not declare them.
    /// </summary>
    internal static TextSpan? InsertedSpan(SyntaxTree tree) => InsertedSpans.TryGetValue(tree, out var inserted) ? inserted.Span : null;

    private static readonly ConditionalWeakTable<SyntaxTree, InsertedUsings> InsertedSpans = new();

    private sealed record InsertedUsings(TextSpan Span);

    /// <summary>
    ///     Where the directives go, chosen so that no code token moves: right after the last
    ///     <c>extern alias</c> or <c>using</c> (on its line); else at the start of a blank or
    ///     <c>//</c>-comment line before the first assembly attribute or member; else before that
    ///     attribute or member, which shifts the columns of that one line (a top-level
    ///     <c>Program.cs</c> whose code starts on line 1). Lines never move.
    /// </summary>
    private static int InsertionOffset(CompilationUnitSyntax root, SourceText text)
    {
        if (root.Usings.Count > 0)
        {
            return root.Usings[^1].Span.End;
        }

        if (root.Externs.Count > 0)
        {
            return root.Externs[^1].Span.End;
        }

        var first = root.AttributeLists.Count > 0 ? root.AttributeLists[0].GetFirstToken() : root.Members[0].GetFirstToken();
        var firstLine = text.Lines.GetLineFromPosition(first.SpanStart).LineNumber;
        for (var line = 0; line < firstLine; line++)
        {
            var start = text.Lines[line].Start;
            // The line must begin in plain leading trivia: not inside a block comment, disabled
            // text or a directive, where a using would be commented out or break the directive.
            var trivia = root.FindTrivia(start);
            if (trivia.SpanStart != start && !trivia.IsKind(SyntaxKind.WhitespaceTrivia))
            {
                continue;
            }

            if (trivia.IsKind(SyntaxKind.EndOfLineTrivia) || trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) || (trivia.IsKind(SyntaxKind.WhitespaceTrivia) && IsBlankOrComment(text.Lines[line].ToString())))
            {
                return start;
            }
        }

        return first.SpanStart;

        static bool IsBlankOrComment(string line)
        {
            var trimmed = line.TrimStart();
            return trimmed.Length == 0 || (trimmed.StartsWith("//", StringComparison.Ordinal) && !trimmed.StartsWith("///", StringComparison.Ordinal));
        }
    }

    private static string DirectiveText(UsingDirectiveSyntax directive) =>
        (directive.StaticKeyword.RawKind == 0 ? string.Empty : "static ")
        + (directive.Alias is { } alias ? $"{alias.Name} = " : string.Empty)
        + directive.NamespaceOrType;

    /// <summary>
    ///     One spelling per directive: MSBuild's generated file writes <c>global::System</c>,
    ///     the SDK lists and <c>&lt;Using&gt;</c> items write <c>System</c>. A top-level using
    ///     already binds from the global namespace, so the qualifier changes nothing and is
    ///     dropped; otherwise a built project and an unbuilt one would emit the same using twice
    ///     and be reported as disagreeing.
    /// </summary>
    internal static string NormalizeDirective(string directive) =>
        directive.Replace("global::", string.Empty, StringComparison.Ordinal).Trim();

    private static string UsingDirectiveName(string directive)
    {
        // "System.Text" / "static System.Math" / "SB = System.Text.StringBuilder" reduce to
        // their using name (an alias maps to nothing removable by name).
        var span = directive.AsSpan();
        if (span.StartsWith("static ", StringComparison.Ordinal))
        {
            return span["static ".Length..].ToString();
        }

        var equals = span.IndexOf('=');
        return (equals >= 0 ? span[(equals + 1)..] : span).Trim().ToString();
    }

    private static List<string>? GeneratedUsingsFile(string projectDirectory, string projectFile, string? representative, List<string> diagnostics)
    {
        var objDirectory = Path.Combine(projectDirectory, "obj");
        if (!Directory.Exists(objDirectory) || representative is null)
        {
            return null;
        }

        var projectName = Path.GetFileNameWithoutExtension(projectFile);
        var candidates = new List<(string Path, DateTime Written)>();
        try
        {
            foreach (var generated in Directory.EnumerateFiles(objDirectory, projectName + ".GlobalUsings.g.cs", SearchOption.AllDirectories))
            {
                // Only the file built for the project's resolved target is authoritative.
                if (!generated.Contains($"{Path.DirectorySeparatorChar}{representative}{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                    && !generated.Contains($"{Path.DirectorySeparatorChar}{representative}.", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                candidates.Add((generated, File.GetLastWriteTimeUtc(generated)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        candidates.Sort(static (left, right) => string.CompareOrdinal(left.Path, right.Path));
        var chosen = candidates[0];
        if (candidates.Count > 1)
        {
            diagnostics.Add($"Multiple GlobalUsings.g.cs files for '{projectName}' (target '{representative}'); using '{Path.GetFileName(Path.GetDirectoryName(chosen.Path))}' output.");
        }

        if (chosen.Written < File.GetLastWriteTimeUtc(projectFile))
        {
            diagnostics.Add($"obj output '{Path.GetRelativePath(projectDirectory, chosen.Path)}' of '{projectName}' is older than the project file; global usings were recomputed from the project instead.");
            return null;
        }

        if (!SafeFileRead.TryReadAllText(chosen.Path, out var generatedText))
        {
            return null;
        }

        var directives = generatedText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("global using ", StringComparison.OrdinalIgnoreCase) && line.EndsWith(';', StringComparison.Ordinal))
            .Select(line => line["global using ".Length..^1].Trim())
            .ToList();
        return directives;
    }

    private static HashSet<string> ProjectSdks(string project)
    {
        var sdks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match declaration in ProjectSdkAttributeRegex().Matches(project))
        {
            foreach (var sdk in declaration.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                sdks.Add(sdk);
            }
        }

        foreach (Match declaration in ProjectSdkElementRegex().Matches(project))
        {
            sdks.Add(declaration.Groups[1].Value);
        }

        return sdks;
    }

    /// <summary>The property's effective value: the project's own, else the nearest Directory.Build.props/targets above it; unevaluated conditions make the value unusable and reported.</summary>
    private static string? PropertyWithInheritance(string root, string projectDirectory, string project, string propertyName)
    {
        if (ProjectPropertyValue(project, propertyName) is { } own)
        {
            return own;
        }

        foreach (var buildFile in new[] { "Directory.Build.props", "Directory.Build.targets" })
        {
            for (var directory = projectDirectory; directory is not null; directory = Path.GetDirectoryName(directory))
            {
                var candidate = Path.Combine(directory, buildFile);
                if (File.Exists(candidate) && !IsUnderBuildDirectory(root, candidate))
                {
                    return SafeFileRead.TryReadAllText(candidate, out var content)
                        ? ProjectPropertyValue(content, propertyName)
                        : null;
                }

                if (string.Equals(directory, root, SafeFileRead.PathComparison))
                {
                    break;
                }
            }
        }

        return null;
    }

    private static string? ProjectPropertyValue(string project, string propertyName)
    {
        foreach (Match match in Regex.Matches(WithoutXmlComments(project), $@"<(?:{propertyName})\s*((?:Condition\s*=""[^""]*""\s*)?)>([^<]*)</(?:{propertyName})\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            if (match.Groups[1].Value.Length > 0)
            {
                // A conditioned value cannot be evaluated without MSBuild; treat as absent so a
                // false arm does not silently enable (or disable) the usings.
                return null;
            }

            return match.Groups[2].Value.Trim().ToLowerInvariant();
        }

        return null;
    }

    private static bool PropertyIsTrue(string project, string propertyName, string root, string projectDirectory)
        => PropertyWithInheritance(root, projectDirectory, project, propertyName) == "true";

    private static Dictionary<string, string> Attributes(Match item)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match attribute in AttributeRegex().Matches(item.Groups[1].Value))
        {
            attributes[attribute.Groups[1].Value] = attribute.Groups[2].Value;
        }

        return attributes;
    }

    private static int MajorOf(string targetFramework)
    {
        var span = targetFramework.AsSpan();
        if (!span.StartsWith("net", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        span = span[3..];
        var digits = 0;
        while (digits < span.Length && char.IsAsciiDigit(span[digits]))
        {
            digits++;
        }

        return digits > 0 && int.TryParse(span[..digits], NumberStyles.Integer, CultureInfo.InvariantCulture, out var major) ? major : 0;
    }

    private static bool IsUnderBuildDirectory(string root, string filePath)
    {
        foreach (var segment in Path.GetRelativePath(root, filePath).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment is "bin" or "obj" or "node_modules" or "artifacts" or "packages")
            {
                return true;
            }
        }

        return false;
    }

    [GeneratedRegex(@"^\s*(?<kind>[A-Za-z]+)\s+(?<value>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex FileBasedDirectiveRegex();

    // `Name=Value` (the documented form) or `Name Value`; the name must be an MSBuild property name.
    [GeneratedRegex(@"^(?<name>[A-Za-z_][A-Za-z0-9_\-]*)\s*(?:=|\s)\s*(?<value>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex PropertyDirectiveRegex();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex XmlCommentRegex();

    [GeneratedRegex(@"<Using\s+([^>]*?)(?:/>|>(.*?)</Using\s*>)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex UsingItemRegex();

    [GeneratedRegex(@"<Project\b[^>]*?\bSdk\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProjectSdkAttributeRegex();

    [GeneratedRegex(@"<Sdk\b[^>]*?\bName\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProjectSdkElementRegex();

    [GeneratedRegex(@"([\w]+)\s*=\s*""([^""]*)""", RegexOptions.CultureInvariant)]
    private static partial Regex AttributeRegex();
}

/// <summary>Whether a scan root's compilation carries global usings, the synthetic tree for them, and the diagnostics of the resolution.</summary>
internal sealed record GlobalUsingsDecision(bool Enabled, CSharpSyntaxTree? Tree, IReadOnlyList<string> Diagnostics)
{
    /// <summary>
    ///     Set when the projects of the tree disagree on their global usings (issue #110): there is
    ///     no <see cref="Tree" />, and each file receives its own project's set through
    ///     <see cref="GlobalUsings.WithFileUsings" />.
    /// </summary>
    public PerFileUsings? PerFile { get; init; }
}

/// <summary>
///     The global usings each file of a tree compiles with when its projects disagree. A file
///     belongs to the nearest project directory at or above it within the scan root (a file-based
///     app owns its own file); a file outside every project gets the union of the sets.
/// </summary>
/// <remarks>
///     The sets reach the compilation as file-local <c>using</c> directives written into the
///     file's own tree, because a <c>global using</c> applies to every tree of a compilation,
///     which is what made one project's implicit usings ambiguous with another's names. The
///     directives go on the line of an existing using (or of the first member), after any
///     <c>extern alias</c>, so no line moves; only the columns after the insertion point on
///     that one line do. The compilation stays one: symbol identity, the dispatch index, the
///     render cache and every provider that binds a tree work unchanged, and a type's members
///     bind with the usings of the file that declares them wherever they are used.
/// </remarks>
internal sealed class PerFileUsings
{
    private readonly string _root;
    private readonly Dictionary<string, string[]> _setsByDirectory = new(SafeFileRead.PathComparer);
    private readonly Dictionary<string, string[]> _setsByFile = new(SafeFileRead.PathComparer);
    private readonly string[] _union;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string[]> _setByDirectoryMemo = new(SafeFileRead.PathComparer);

    /// <param name="common">The usings every owner has, which stay in the global tree; the per-file sets leave them out.</param>
    public PerFileUsings(string root, List<(string Key, bool IsFile, List<string> Directives)> owners, List<string> union, List<string> common)
    {
        _root = root;
        var global = new HashSet<string>(common, StringComparer.Ordinal);
        _union = [.. union.Where(directive => !global.Contains(directive))];
        foreach (var (key, isFile, directives) in owners)
        {
            var target = isFile ? _setsByFile : _setsByDirectory;
            var own = directives.Where(directive => !global.Contains(directive));
            // Two projects in one directory: both apply to its files.
            target[key] = target.TryGetValue(key, out var existing) ? [.. existing.Concat(own).Distinct(StringComparer.Ordinal)] : [.. own];
        }
    }

    /// <summary>The file-local directives <paramref name="filePath" /> compiles with, beyond the global ones every project shares.</summary>
    public IReadOnlyList<string> SetFor(string filePath)
    {
        string full;
        try
        {
            full = Path.GetFullPath(filePath);
        }
        catch (ArgumentException)
        {
            return _union;
        }

        if (_setsByFile.TryGetValue(full, out var own))
        {
            return own;
        }

        var directory = Path.GetDirectoryName(full);
        return directory is null ? _union : SetForDirectory(directory);
    }

    private string[] SetForDirectory(string directory)
    {
        if (_setByDirectoryMemo.TryGetValue(directory, out var memo))
        {
            return memo;
        }

        string[] set;
        if (_setsByDirectory.TryGetValue(directory, out var own))
        {
            set = own;
        }
        else if (string.Equals(directory, _root, SafeFileRead.PathComparison) || Path.GetDirectoryName(directory) is not { } parent)
        {
            set = _union;
        }
        else
        {
            set = SetForDirectory(parent);
        }

        _setByDirectoryMemo[directory] = set;
        return set;
    }
}
