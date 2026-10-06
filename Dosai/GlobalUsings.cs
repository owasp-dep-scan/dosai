using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.CSharp;

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
///         <item>Windows Forms: <c>Sdks/Microsoft.NET.Sdk.WindowsDesktop/targets/Microsoft.NET.Sdk.WindowsDesktop.WindowsForms.props</c></item>
///     </list>
///     <para>
///         Verified byte-identical between the SDKs installed on the development machines at
///         the time of writing: macOS <c>/usr/local/share/dotnet/sdk/{10.0.302,
///         11.0.100-rc.1.26425.128}</c> and the Windows VM <c>C:\Program Files\dotnet\sdk\
///         {10.0.400, 11.0.100-rc.1.26425.128}</c>, except that only the 11 SDKs add the
///         base <c>System.Net.Http.Json</c> using (gated on .NET 11+), which is applied by
///         target framework here rather than by SDK version. The Razor, Blazor Web Assembly
///         and WPF SDKs add no usings of their own on disk (Razor's
///         <c>Microsoft.Extensions.Validation.Embedded</c> using is opt-in through
///         <c>IncludeEmbeddedValidationGlobalUsing</c> and default-off, so it is not added).
///     </para>
///     <para>
///         Global usings are compilation-wide and Dosai compiles a tree in one compilation
///         (the documented granularity limitation), so per-project sets merge by union. A
///         using one project's set carries that does not resolve in the merged compilation is
///         an error on that directive only and does not stop other bindings - pinned by test.
///         Projects whose final sets disagree produce one diagnostic naming them.
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

    /// <summary>Windows Forms additions (Microsoft.NET.Sdk.WindowsDesktop.WindowsForms.props), gated on UseWindowsForms.</summary>
    private static readonly string[] WindowsFormsSdkUsings = ["System.Drawing", "System.Windows.Forms"];

    private static readonly Lock ResolveLock = new();
    private static readonly Dictionary<string, GlobalUsingsDecision> DecisionsByRoot = new(SafeFileRead.PathComparer);

    public static GlobalUsingsDecision Resolve(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new GlobalUsingsDecision(false, null, []);
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
                return cached;
            }

            var decision = ResolveNoCache(root);
            DecisionsByRoot[root] = decision;
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
            foreach (var project in projects)
            {
                if (ProjectUsings(root, project, diagnostics) is { Count: > 0 } directives)
                {
                    projectSets.Add((Path.GetRelativePath(root, project), directives));
                }
            }

            if (projectSets.Count == 0)
            {
                return new GlobalUsingsDecision(false, null, diagnostics);
            }

            // Union merge, first project's order first: the merged set is what one compilation
            // sees, and a directive that fails to resolve errors alone.
            var merged = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var disagreeing = new List<string>();
            foreach (var (project, directives) in projectSets)
            {
                var additions = directives.Where(directive => seen.Add(directive)).ToList();
                if (additions.Count > 0)
                {
                    merged.AddRange(additions);
                }

                if (!directives.Order(StringComparer.Ordinal).SequenceEqual(projectSets[0].Directives.Order(StringComparer.Ordinal)))
                {
                    disagreeing.Add(project);
                }
            }

            if (disagreeing.Count > 0)
            {
                diagnostics.Add($"Global usings differ between projects ({string.Join(", ", disagreeing)}); the compilation carries the union of every project's set, so files of a project without a using can still bind names through it.");
            }

            if (merged.Count == 0)
            {
                return new GlobalUsingsDecision(false, null, diagnostics);
            }

            var source = string.Join(Environment.NewLine, merged.Select(directive => $"global using {directive};"));
            var tree = CSharpSourceParser.Parse(source, "<implicit-usings>", root);
            return new GlobalUsingsDecision(true, tree, diagnostics);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Best effort: analysis continues with explicit-usings semantics.
            return new GlobalUsingsDecision(false, null, diagnostics);
        }
    }

    /// <summary>The using directives of one project: MSBuild's generated file when it is authoritative, else the SDK list plus the project's Using items.</summary>
    private static List<string> ProjectUsings(string root, string projectFile, List<string> diagnostics)
    {
        if (!SafeFileRead.TryReadAllText(projectFile, out var project))
        {
            return [];
        }

        var projectDirectory = Path.GetDirectoryName(projectFile)!;
        var sdks = ProjectSdks(project);
        var implicitUsings = PropertyWithInheritance(root, projectDirectory, project, "ImplicitUsings");
        if (implicitUsings is not ("enable" or "true"))
        {
            // Disabled or unset (an unevaluable condition counts as unset, noted when present):
            // inventing the global usings would bind calls the project's own compiler rejects.
            return [];
        }

        var targets = ProjectTargets(project);
        var representative = FrameworkPreprocessorDefines.TrySelectRepresentative(targets, out var selected) ? selected : null;
        var isNetFramework = representative?.StartsWith("net4", StringComparison.OrdinalIgnoreCase) == true;

        // MSBuild's own output is authoritative when it exists for the resolved target and is
        // not older than the project file; obj/ is excluded from discovery, so it is read here
        // explicitly (issue #74).
        if (GeneratedUsingsFile(projectDirectory, projectFile, representative, diagnostics) is { } generated)
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

    private static void ApplyUsingItems(string root, string projectFile, string project, string projectDirectory, List<string> directives, HashSet<string> names, List<string> diagnostics)
    {
        // <Using> items apply from the project file and from Directory.Build.props/targets up
        // the tree; the project's own file wins on ordering. Conditions are not evaluated: the
        // item is included and the skipped condition is reported, because an extra using that
        // does not resolve errors alone and never blocks other bindings.
        var items = new List<(string File, Match Match)>();
        foreach (var match in UsingItemRegex().Matches(project).Cast<Match>())
        {
            items.Add((projectFile, match));
        }

        foreach (var buildFile in new[] { "Directory.Build.props", "Directory.Build.targets" })
        {
            for (var directory = projectDirectory; directory is not null; directory = Path.GetDirectoryName(directory))
            {
                var candidate = Path.Combine(directory, buildFile);
                if (File.Exists(candidate) && !IsUnderBuildDirectory(root, candidate))
                {
                    if (SafeFileRead.TryReadAllText(candidate, out var content))
                    {
                        foreach (var match in UsingItemRegex().Matches(content).Cast<Match>())
                        {
                            items.Add((candidate, match));
                        }
                    }

                    break;
                }

                if (string.Equals(directory, root, SafeFileRead.PathComparison))
                {
                    break;
                }
            }
        }

        foreach (var (file, item) in items)
        {
            var attributes = Attributes(item);
            if (attributes.TryGetValue("Condition", out var condition))
            {
                diagnostics.Add($"Using item in '{Path.GetRelativePath(root, file)}' carries the condition '{condition}', which Dosai does not evaluate; the using is applied unconditionally.");
            }

            if (attributes.TryGetValue("Remove", out var remove))
            {
                var pattern = UsingDirectiveName(remove);
                for (var index = directives.Count - 1; index >= 0; index--)
                {
                    if (UsingDirectiveName(directives[index]) == pattern)
                    {
                        names.Remove(UsingDirectiveName(directives[index]));
                        directives.RemoveAt(index);
                    }
                }

                continue;
            }

            if (attributes.TryGetValue("Include", out var include))
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
            diagnostics.Add($"obj output '{chosen.Path}' is older than the project file; global usings were recomputed from the project instead.");
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

    private static List<string> ProjectTargets(string project)
    {
        var targets = new List<string>();
        foreach (Match match in TargetFrameworksElementRegex().Matches(project))
        {
            foreach (var value in match.Groups[1].Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                targets.Add(value);
            }
        }

        return targets;
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
        foreach (Match match in Regex.Matches(project, $@"<(?:{propertyName})\s*((?:Condition\s*=""[^""]*""\s*)?)>([^<]*)</(?:{propertyName})\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
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

    [GeneratedRegex(@"<Using\s+([^>]*?)(?:/>|>(.*?)</Using\s*>)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex UsingItemRegex();

    [GeneratedRegex(@"<Project\b[^>]*?\bSdk\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProjectSdkAttributeRegex();

    [GeneratedRegex(@"<Sdk\b[^>]*?\bName\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProjectSdkElementRegex();

    [GeneratedRegex(@"<TargetFrameworks?\s*>([^<]+)</TargetFrameworks?\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TargetFrameworksElementRegex();

    [GeneratedRegex(@"([\w]+)\s*=\s*""([^""]*)""", RegexOptions.CultureInvariant)]
    private static partial Regex AttributeRegex();
}

/// <summary>Whether a scan root's compilation carries global usings, the synthetic tree for them, and the diagnostics of the resolution.</summary>
internal sealed record GlobalUsingsDecision(bool Enabled, CSharpSyntaxTree? Tree, IReadOnlyList<string> Diagnostics);
