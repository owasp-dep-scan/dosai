using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace Depscan;

/// <summary>
///     Models the part of MSBuild evaluation the project-property and item readers need: a
///     project or build file whose <c>&lt;Import&gt;</c> elements are followed, so a property
///     or item set only in an imported file is seen (issue #117). MSBuild evaluation is
///     approximated, not reimplemented: file content is read as text, and only import paths,
///     the two directory-chaining property functions, and the last-write-wins rule for single
///     properties are modelled.
/// </summary>
/// <remarks>
///     <para>
///         An expansion of a file is its text (XML comments stripped) split into ordered
///         segments, with every <c>&lt;Import&gt;</c> replaced in place by the imported file's
///         own expansion, so callers see content in MSBuild evaluation order. Each segment
///         records the file it came from and whether it was reached through a conditioned
///         import: a property defined in such a segment counts as conditioned (unknown), and
///         items from it are applied but reported.
///     </para>
///     <para>
///         Import paths resolve the way MSBuild resolves them: relative to the directory of the
///         file holding the <c>&lt;Import&gt;</c>, with both separators on every OS, the
///         reserved <c>MSBuildThisFile*</c> and <c>MSBuildProjectDirectory</c> properties
///         expanded, and <c>GetPathOfFileAbove</c>/<c>GetDirectoryNameOfFileAbove</c> walking up
///         from the given directory (inclusive). Any other property reference, item reference or
///         wildcard leaves the path unresolved, and the import is skipped with a diagnostic.
///         Only files inside the scan root and outside build-output directories are followed:
///         reading outside the scanned tree is not allowed (docs/THREAT_MODEL.md). Cycles and
///         chains deeper than <see cref="MaxImportDepth" /> are cut with a diagnostic, never an
///         exception.
///     </para>
///     <para>
///         Expansions are memoized per (scan root, project directory, file) in a concurrent
///         dictionary, because expansion runs on the worker team and its result must not depend
///         on which thread computed it. The project directory joins the key because an import
///         path may name <c>$(MSBuildProjectDirectory)</c>, which differs per project.
///     </para>
/// </remarks>
internal static partial class MsBuildImports
{
    /// <summary>Import chains deeper than this are cut; real repositories chain a handful of files.</summary>
    internal const int MaxImportDepth = 32;

    /// <summary>
    ///     One piece of an expansion: the file the text came from (absolute), the text itself,
    ///     and whether the text is only part of the build because an import on the way to it
    ///     carries a condition Dosai does not evaluate.
    /// </summary>
    internal sealed record MsBuildFileSegment(string File, string Content, bool Conditioned);

    /// <summary>An item of one name found in an expansion, with the file it sits in.</summary>
    internal readonly record struct MsBuildItem(string File, Match Match, bool ThroughConditionedImport);

    /// <summary>A file's segments and the import-resolution diagnostics of the file and everything it imports.</summary>
    private sealed record Expansion(MsBuildFileSegment[] Segments, string[] Diagnostics)
    {
        public static readonly Expansion Empty = new([], []);
    }

    private static readonly ConcurrentDictionary<string, Expansion> Expansions = new(SafeFileRead.PathComparer);
    private static readonly ConcurrentDictionary<string, Regex> PropertyRegexes = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, Regex> ItemRegexes = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, Regex> ReservedPropertyRegexes = new(StringComparer.Ordinal);

    private const string KeySeparator = "\u001f";
    private const string FunctionPrefix = "$([MSBuild]::";

    /// <summary>
    ///     The file's expansion: its own text with every <c>&lt;Import&gt;</c> replaced in place
    ///     by the imported file's expansion. A file that does not exist or cannot be read
    ///     expands to nothing.
    /// </summary>
    internal static IReadOnlyList<MsBuildFileSegment> Expand(string root, string file) =>
        ExpandCore(root, Path.GetDirectoryName(Path.GetFullPath(file)) ?? string.Empty, Path.GetFullPath(file), new ExpansionContext()).Segments;

    /// <summary>
    ///     The same expansion with <c>$(MSBuildProjectDirectory)</c> naming the given project's
    ///     directory: the shape a project's evaluation of the file has, used for the nearest
    ///     Directory.Build files a project imports implicitly.
    /// </summary>
    internal static IReadOnlyList<MsBuildFileSegment> Expand(string root, string projectDirectory, string file) =>
        ExpandCore(root, projectDirectory, Path.GetFullPath(file), new ExpansionContext()).Segments;

    /// <summary>
    ///     The evaluation order Dosai's project-level readers need: the nearest
    ///     <c>Directory.Build.props</c> (imported before the project body), the project file's
    ///     own expansion, then the nearest <c>Directory.Build.targets</c> (imported after it).
    ///     A null <paramref name="projectFile" /> (a file-based app's virtual project) makes the
    ///     passed content the project body as a single segment; its imports are not followed
    ///     because there is no file behind it.
    /// </summary>
    internal static IReadOnlyList<MsBuildFileSegment> ExpandProject(string root, string projectDirectory, string? projectFile, string projectContent)
    {
        var segments = new List<MsBuildFileSegment>();
        if (NearestBuildFile(root, projectDirectory, "Directory.Build.props") is { } props)
        {
            segments.AddRange(Expand(root, projectDirectory, props));
        }

        if (projectFile is null)
        {
            segments.Add(new MsBuildFileSegment("<virtual project>", WithoutXmlComments(projectContent), Conditioned: false));
        }
        else
        {
            segments.AddRange(Expand(root, projectDirectory, projectFile));
        }

        if (NearestBuildFile(root, projectDirectory, "Directory.Build.targets") is { } targets)
        {
            segments.AddRange(Expand(root, projectDirectory, targets));
        }

        return segments;
    }

    /// <summary>
    ///     The effective value of one MSBuild property across the evaluation order: the last
    ///     definition wins (the nearest props file, then the project body, then the nearest
    ///     targets file). A definition carrying a <c>Condition</c>, or reached through a
    ///     conditioned import, makes the value unknown, and an unconditioned later definition
    ///     makes it known again. The value is trimmed; callers lower-case where they always did.
    /// </summary>
    internal static string? PropertyValue(string root, string projectDirectory, string? projectFile, string projectContent, string propertyName)
    {
        var regex = PropertyRegexes.GetOrAdd(propertyName, static name => new Regex(
            $@"<(?:{name})\s*((?:Condition\s*=""[^""]*""\s*)?)>([^<]*)</(?:{name})\s*>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
        string? value = null;
        var conditioned = true;
        foreach (var segment in ExpandProject(root, projectDirectory, projectFile, projectContent))
        {
            foreach (Match match in regex.Matches(segment.Content))
            {
                value = match.Groups[2].Value.Trim();
                conditioned = match.Groups[1].Value.Length > 0 || segment.Conditioned;
            }
        }

        return conditioned ? null : value;
    }

    /// <summary>The items of one name across the segments, in order, with the file each sits in.</summary>
    internal static IEnumerable<MsBuildItem> Items(IReadOnlyList<MsBuildFileSegment> segments, string itemName)
    {
        var regex = ItemRegexes.GetOrAdd(itemName, static name => new Regex(
            $@"<{name}\s+([^>]*?)(?:/>|>(.*?)</{name}\s*>)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline));
        foreach (var segment in segments)
        {
            // The regex runs only at starts the scanner has verified, where its lazy quantifiers
            // walk no further than the match itself; the free-position Matches it used to run is
            // quadratic on text that never closes the element.
            var position = 0;
            var memo = new ElementScanMemo();
            int start;
            while ((start = NextElementStart(segment.Content, position, itemName, ref memo)) >= 0)
            {
                var match = regex.Match(segment.Content, start);
                if (!match.Success || match.Index != start)
                {
                    break;
                }

                yield return new MsBuildItem(segment.File, match, segment.Conditioned);
                position = match.Index + match.Length;
            }
        }
    }

    /// <summary>
    ///     The nearest <paramref name="fileName" /> (<c>Directory.Build.props</c> or
    ///     <c>Directory.Build.targets</c>) at or above <paramref name="startDirectory" />, within
    ///     the scan root and outside build-output directories. A parent one is reached only
    ///     through an explicit <c>&lt;Import&gt;</c>, exactly as MSBuild does.
    /// </summary>
    internal static string? NearestBuildFile(string root, string startDirectory, string fileName)
    {
        for (var directory = startDirectory; directory is not null; directory = Path.GetDirectoryName(directory))
        {
            var candidate = Path.Combine(directory, fileName);
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

    internal static bool IsUnderBuildDirectory(string root, string filePath)
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

    /// <summary>An item element's attributes by name; the attribute text is the item regex match's first group.</summary>
    internal static Dictionary<string, string> Attributes(Match item)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match attribute in AttributeRegex().Matches(item.Groups[1].Value))
        {
            attributes[attribute.Groups[1].Value] = attribute.Groups[2].Success ? attribute.Groups[2].Value : attribute.Groups[3].Value;
        }

        return attributes;
    }

    /// <summary>A project file's text with its XML comments blanked: a commented-out import, property or item is not part of the build.</summary>
    /// <remarks>
    ///     A linear scan, not a lazy regular expression: a file of unclosed comment openers made
    ///     every opener walk the remaining text looking for its closer. A comment whose closer
    ///     never comes runs to the end of the text.
    /// </remarks>
    internal static string WithoutXmlComments(string content)
    {
        var open = content.IndexOf("<!--", StringComparison.Ordinal);
        if (open < 0)
        {
            return content;
        }

        var stripped = new StringBuilder(content.Length);
        stripped.Append(content, 0, open);
        while (open >= 0)
        {
            var close = content.IndexOf("-->", open + 4, StringComparison.Ordinal);
            if (close < 0)
            {
                // An unclosed comment runs to the end of the text.
                return stripped.ToString();
            }

            var next = content.IndexOf("<!--", close + 3, StringComparison.Ordinal);
            if (next < 0)
            {
                stripped.Append(content, close + 3, content.Length - close - 3);
                return stripped.ToString();
            }

            stripped.Append(content, close + 3, next - close - 3);
            open = next;
        }

        return stripped.ToString();
    }

    /// <summary>
    ///     The import-resolution diagnostics of a scan root: those of every project file and
    ///     Directory.Build file of the tree, each expanded as its own directory's project would,
    ///     each distinct message once, in ordinal order. The answer is a function of the tree
    ///     alone. It does not depend on which reader expanded which file first, or on which
    ///     worker thread did, so the report stays byte-stable.
    /// </summary>
    internal static IReadOnlyList<string> DiagnosticsFor(string root)
    {
        var messages = new SortedSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var pattern in BuildFilePatterns)
            {
                foreach (var file in SafeFileRead.EnumerateAllFilesSafe(root, pattern).Where(file => !IsUnderBuildDirectory(root, file)))
                {
                    var full = Path.GetFullPath(file);
                    messages.UnionWith(ExpandCore(root, Path.GetDirectoryName(full) ?? string.Empty, full, new ExpansionContext()).Diagnostics);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Best effort: diagnostics never fail analysis.
        }

        return [.. messages];
    }

    /// <summary>Forgets every memoized expansion; the MCP server calls it before each tool call.</summary>
    internal static void ResetCache() => Expansions.Clear();

    private static readonly string[] BuildFilePatterns = ["*.csproj", "*.fsproj", "*.vbproj", "Directory.Build.props", "Directory.Build.targets"];

    private static Expansion ExpandCore(string root, string projectDirectory, string file, ExpansionContext context)
    {
        var key = string.Concat(root, KeySeparator, projectDirectory, KeySeparator, file);
        if (Expansions.TryGetValue(key, out var cached))
        {
            return cached;
        }

        if (!context.InProgress.Add(file))
        {
            // A cycle is cut by the caller, which names the import; reaching here without that
            // check would mean re-entering the file from somewhere else in the same chain.
            context.DoNotMemoize = true;
            return Expansion.Empty;
        }

        try
        {
            var expansion = ExpandFile(root, projectDirectory, file, context);
            // A walk that cut a cycle (or hit the depth cap) depends on where it started, so its
            // result is recomputed for every entry point instead of memoized.
            if (!context.DoNotMemoize)
            {
                Expansions.TryAdd(key, expansion);
            }

            return expansion;
        }
        finally
        {
            context.InProgress.Remove(file);
        }
    }

    private static Expansion ExpandFile(string root, string projectDirectory, string file, ExpansionContext context)
    {
        if (!SafeFileRead.TryReadAllText(file, out var rawContent))
        {
            return Expansion.Empty;
        }

        var content = WithoutXmlComments(rawContent);
        var segments = new List<MsBuildFileSegment>();
        var diagnostics = new List<string>();
        var position = 0;
        var memo = new ElementScanMemo();
        int start;
        while ((start = NextElementStart(content, position, "Import", ref memo)) >= 0)
        {
            // The regex runs only at a start the scanner has verified, where its lazy
            // quantifiers walk no further than the match itself; the free-position Matches it
            // used to run is quadratic on a file of unclosed import tags.
            var import = ImportRegex().Match(content, start);
            if (!import.Success || import.Index != start)
            {
                break;
            }

            AddOwnSegment(segments, file, content[position..import.Index]);
            position = import.Index + import.Length;
            FollowImport(root, projectDirectory, file, import, context, segments, diagnostics);
        }

        AddOwnSegment(segments, file, content[position..]);
        return new Expansion([.. segments], [.. diagnostics]);
    }

    private static void AddOwnSegment(List<MsBuildFileSegment> segments, string file, string content)
    {
        if (content.Length > 0)
        {
            segments.Add(new MsBuildFileSegment(file, content, Conditioned: false));
        }
    }

    private static void FollowImport(string root, string projectDirectory, string importingFile, Match import, ExpansionContext context, List<MsBuildFileSegment> segments, List<string> diagnostics)
    {
        var attributes = Attributes(import);
        var rawProject = attributes.GetValueOrDefault("Project");
        if (string.IsNullOrWhiteSpace(rawProject))
        {
            return;
        }

        // An import naming an SDK (Project="Sdk.props" Sdk="Microsoft.NET.Sdk") or rooted in the
        // MSBuild toolset ($(MSBuildToolsPath)\Microsoft.CSharp.targets in every legacy project)
        // is resolved from the installed tools, not the tree: not a file Dosai may read, and not
        // a broken import either, so it is skipped silently.
        if (attributes.ContainsKey("Sdk") || ToolsetPropertyRegex().IsMatch(rawProject))
        {
            return;
        }

        var conditioned = false;
        var hasExistsCondition = false;
        string existsPath = string.Empty;
        if (attributes.TryGetValue("Condition", out var condition))
        {
            hasExistsCondition = TryParseExistsCondition(condition, out existsPath);
            if (hasExistsCondition)
            {
                if (TryResolvePath(root, projectDirectory, importingFile, existsPath, diagnostics: null, out var existsTarget) && !File.Exists(existsTarget))
                {
                    // Exists(...) false: the import is not part of this build.
                    return;
                }

                if (existsTarget is null)
                {
                    // The Exists argument could not be resolved: the condition is unknown, so the
                    // import is followed and everything it brings is marked conditioned.
                    conditioned = true;
                }
            }
            else
            {
                // Any other condition ('$(Configuration)' == 'Debug', !Exists(...), combinations):
                // followed, but marked, because Dosai cannot know whether it holds.
                conditioned = true;
            }
        }

        if (!TryResolvePath(root, projectDirectory, importingFile, rawProject, diagnostics, out var target))
        {
            return;
        }

        if (!File.Exists(target))
        {
            if (!hasExistsCondition)
            {
                diagnostics.Add($"<Import Project=\"{rawProject}\"> in '{Relative(root, importingFile)}' names '{Relative(root, target)}', which does not exist; the import was skipped.");
            }

            return;
        }

        var targetKey = Path.GetFullPath(target);
        if (context.InProgress.Contains(targetKey))
        {
            context.DoNotMemoize = true;
            diagnostics.Add($"<Import Project=\"{rawProject}\"> in '{Relative(root, importingFile)}' closes an import cycle at '{Relative(root, target)}'; the import was skipped.");
            return;
        }

        // InProgress holds every file of the chain being expanded, so its count is the depth.
        if (context.InProgress.Count >= MaxImportDepth)
        {
            context.DoNotMemoize = true;
            diagnostics.Add($"<Import Project=\"{rawProject}\"> in '{Relative(root, importingFile)}' sits more than {MaxImportDepth} levels deep in the import chain; the import was skipped.");
            return;
        }

        var expansion = ExpandCore(root, projectDirectory, targetKey, context);
        diagnostics.AddRange(expansion.Diagnostics);
        foreach (var imported in expansion.Segments)
        {
            segments.Add(imported.Conditioned || conditioned ? imported with { Conditioned = true } : imported);
        }
    }

    /// <summary>
    ///     Whether the condition is a plain <c>Exists('&lt;path&gt;')</c> (either quote style,
    ///     any whitespace), and the path expression inside it, unquoted.
    /// </summary>
    private static bool TryParseExistsCondition(string condition, out string path)
    {
        var match = ExistsConditionRegex().Match(condition.Trim());
        if (match.Success)
        {
            path = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            return true;
        }

        path = string.Empty;
        return false;
    }

    /// <summary>
    ///     Resolves one import path expression the way MSBuild does, or reports why it cannot.
    ///     <paramref name="diagnostics" /> is null while a condition's Exists argument is probed:
    ///     a condition that cannot be evaluated is the caller's business, not a skipped import.
    /// </summary>
    private static bool TryResolvePath(string root, string projectDirectory, string importingFile, string expression, List<string>? diagnostics, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? resolved)
    {
        resolved = null;
        var importingDirectory = Path.GetDirectoryName(importingFile) ?? projectDirectory;
        var value = expression.Trim();
        if (value.Length == 0)
        {
            return false;
        }

        value = ExpandReservedProperties(value, importingFile, importingDirectory, projectDirectory);
        value = EvaluateDirectoryFunctions(value, importingDirectory);
        if (IsUncShaped(value))
        {
            // A UNC network location named by the tree, refused before any existence probe so
            // the share is never touched to be skipped (the same rule as packageFolders).
            diagnostics?.Add($"<Import Project=\"{expression}\"> in '{Relative(root, importingFile)}' was skipped: the path names a UNC network location, which Dosai does not read.");

            return false;
        }

        if (UnresolvedReferenceRegex().IsMatch(value) || value.Contains('*', StringComparison.Ordinal) || value.Contains('?', StringComparison.Ordinal))
        {
            diagnostics?.Add($"<Import Project=\"{expression}\"> in '{Relative(root, importingFile)}' was skipped: the path holds a property, item or wildcard Dosai cannot expand.");

            return false;
        }

        value = value.Replace('\\', '/');
        string full;
        try
        {
            full = Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(importingDirectory, value));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            diagnostics?.Add($"<Import Project=\"{expression}\"> in '{Relative(root, importingFile)}' was skipped: the path could not be resolved.");

            return false;
        }

        var relative = Relative(root, full);
        if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            diagnostics?.Add($"<Import Project=\"{expression}\"> in '{Relative(root, importingFile)}' resolves to '{relative}', which is outside the scanned tree; the import was skipped.");

            return false;
        }

        if (IsUnderBuildDirectory(root, full))
        {
            // A package's build/ props under packages/ or a restore-generated file under obj/:
            // build output by Dosai's rule everywhere else, so not read, and not a broken import.
            return false;
        }

        resolved = full;
        return true;
    }

    /// <summary>The reserved path properties every import expression may use, expanded case-insensitively.</summary>
    private static string ExpandReservedProperties(string value, string importingFile, string importingDirectory, string projectDirectory)
    {
        var fileName = Path.GetFileName(importingFile);
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(importingFile);
        var withTrailingSeparator = importingDirectory.EndsWith(Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || importingDirectory.EndsWith(Path.AltDirectorySeparatorChar, StringComparison.Ordinal)
            ? importingDirectory
            : importingDirectory + Path.DirectorySeparatorChar;
        value = ReplaceReserved(value, "$(MSBuildThisFileDirectory)", withTrailingSeparator);
        value = ReplaceReserved(value, "$(MSBuildThisFileFullPath)", importingFile);
        value = ReplaceReserved(value, "$(MSBuildThisFile)", fileName);
        value = ReplaceReserved(value, "$(MSBuildThisFileName)", fileNameWithoutExtension);
        value = ReplaceReserved(value, "$(MSBuildProjectDirectory)", projectDirectory);
        return value;
    }

    private static string ReplaceReserved(string value, string property, string replacement) =>
        ReservedPropertyRegexes.GetOrAdd(property, static name => new Regex(Regex.Escape(name), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .Replace(value, replacement.Replace("$", "$$", StringComparison.Ordinal));

    /// <summary>
    ///     The two directory-chaining property functions real repositories use to import the
    ///     parent Directory.Build file: GetPathOfFileAbove(file, startDirectory) and
    ///     GetDirectoryNameOfFileAbove(startDirectory, file), both searching the start directory
    ///     (inclusive) and walking up, the first returning the file's full path, the second the
    ///     directory holding it, both an empty string when the file is not found. GetPathOfFileAbove's
    ///     start directory is optional and defaults to the importing file's own directory. Any
    ///     other function is left in the value, which makes the path unresolved downstream.
    /// </summary>
    private static string EvaluateDirectoryFunctions(string value, string importingDirectory)
    {
        var search = 0;
        while (value.IndexOf(FunctionPrefix, search, StringComparison.OrdinalIgnoreCase) is var found && found >= 0)
        {
            var start = found;
            var nameStart = start + FunctionPrefix.Length;
            var open = value.IndexOf('(', nameStart, StringComparison.Ordinal);
            if (open < 0 || FunctionNameRegex().Match(value[nameStart..open]) is not { Success: true } name)
            {
                break;
            }

            if (!BalancedParentheses(value, open, out var close))
            {
                break;
            }

            // The function call sits inside a property reference: $([MSBuild]::Func(args)) closes
            // twice, so the span to replace ends at the wrapper's paren when it is there.
            var end = close + 1 < value.Length && value[close + 1] == ')' ? close + 1 : close;
            var replacement = EvaluateDirectoryFunction(name.Groups[1].Value, value[(open + 1)..close], importingDirectory);
            value = string.Concat(value[..start], replacement, value[(end + 1)..]);
            search = start + replacement.Length;
        }

        return value;
    }

    private static string EvaluateDirectoryFunction(string name, string arguments, string importingDirectory)
    {
        var isGetPath = name.Equals("GetPathOfFileAbove", StringComparison.OrdinalIgnoreCase);
        var isGetDirectory = name.Equals("GetDirectoryNameOfFileAbove", StringComparison.OrdinalIgnoreCase);
        if (!isGetPath && !isGetDirectory)
        {
            return string.Concat(FunctionPrefix, name, "(", arguments, "))");
        }

        var args = SplitArguments(arguments);
        string? fileName;
        string startDirectory;
        if (isGetPath)
        {
            fileName = args.Count > 0 ? Unquote(args[0]) : string.Empty;
            startDirectory = args.Count > 1 ? Unquote(args[1]) : importingDirectory;
        }
        else
        {
            startDirectory = args.Count > 0 ? Unquote(args[0]) : importingDirectory;
            fileName = args.Count > 1 ? Unquote(args[1]) : string.Empty;
        }

        // A UNC network location named by the tree, refused before the upward walk probes it:
        // on Windows Path.Combine keeps a rooted second argument as-is, so either argument
        // could otherwise put a share path under File.Exists.
        if (fileName.Length == 0 || startDirectory.Length == 0 || IsUncShaped(startDirectory) || IsUncShaped(fileName))
        {
            return string.Empty;
        }

        string start;
        try
        {
            start = Path.GetFullPath(Path.IsPathRooted(startDirectory) ? startDirectory : Path.Combine(importingDirectory, startDirectory));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            return string.Empty;
        }

        for (var directory = start; directory is not null; directory = Path.GetDirectoryName(directory))
        {
            var candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate))
            {
                return isGetPath ? candidate : directory;
            }
        }

        return string.Empty;
    }

    /// <summary>Splits a function argument list on top-level commas, respecting quotes and nested parentheses.</summary>
    private static List<string> SplitArguments(string arguments)
    {
        var args = new List<string>();
        var depth = 0;
        char? quote = null;
        var current = new StringBuilder();
        foreach (var character in arguments)
        {
            if (quote is { } open)
            {
                if (character == open)
                {
                    quote = null;
                }
            }
            else if (character is '\'' or '"')
            {
                quote = character;
            }
            else if (character == '(')
            {
                depth++;
            }
            else if (character == ')')
            {
                depth--;
            }
            else if (character == ',' && depth == 0)
            {
                args.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(character);
        }

        if (current.Length > 0 || args.Count > 0)
        {
            args.Add(current.ToString());
        }

        return args;
    }

    private static string Unquote(string argument)
    {
        var trimmed = argument.Trim();
        return trimmed.Length >= 2 && trimmed[0] is '\'' or '"' && trimmed[^1] == trimmed[0] ? trimmed[1..^1].Trim() : trimmed;
    }

    /// <summary>A path relative to the scan root with forward slashes, so a diagnostic reads the same on every OS.</summary>
    private static string Relative(string root, string path) => SourceDocumentPaths.RelativePath(root, path);

    /// <summary>
    ///     A UNC path shape (<c>\\server\share</c> or <c>//server/share</c>, the
    ///     <c>\\?\UNC\</c> device form included): not a local path. A string test on purpose,
    ///     run before any existence probe on a path read from tree content, so refusing a
    ///     network location never touches it.
    /// </summary>
    private static bool IsUncShaped(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal);

    private static bool BalancedParentheses(string value, int open, out int close)
    {
        var depth = 0;
        char? quote = null;
        for (var index = open; index < value.Length; index++)
        {
            var character = value[index];
            if (quote is { } quoteCharacter)
            {
                if (character == quoteCharacter)
                {
                    quote = null;
                }

                continue;
            }

            if (character is '\'' or '"')
            {
                quote = character;
            }
            else if (character == '(')
            {
                depth++;
            }
            else if (character == ')')
            {
                depth--;
                if (depth == 0)
                {
                    close = index;
                    return true;
                }
            }
        }

        close = -1;
        return false;
    }

    private sealed class ExpansionContext
    {
        public HashSet<string> InProgress { get; } = new(StringComparer.Ordinal);

        /// <summary>Set when a cycle or the depth cap was cut on the way down: the result depends on where the walk started, so it is not memoized.</summary>
        public bool DoNotMemoize { get; set; }
    }

    // Matched only at a start NextElementStart has verified for the same content (ExpandFile):
    // at a known start the lazy quantifiers walk no further than the match itself. A
    // free-position Matches over text that never closes the element is quadratic; keep the
    // scan in the scanner.
    [GeneratedRegex(@"<Import\s+([^>]*?)(?:/>|>.*?</Import\s*>)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex ImportRegex();

    // name="value" or name='value': XML allows either quote, and the value is whichever group matched.
    [GeneratedRegex(@"([\w]+)\s*=\s*(?:""([^""]*)""|'([^']*)')", RegexOptions.CultureInvariant)]
    private static partial Regex AttributeRegex();

    // A property the MSBuild toolset defines: an import rooted in one names a file of the
    // installed tools, never one of the tree.
    [GeneratedRegex(@"\$\(\s*(?:MSBuildToolsPath|MSBuildToolsRoot|MSBuildBinPath|MSBuildExtensionsPath(?:32|64)?|MSBuildSDKsPath|MSBuildFrameworkToolsPath\w*|VSToolsPath|VCTargetsPath|NuGetPackageRoot)\s*\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ToolsetPropertyRegex();

    /// <summary>
    ///     The next position at or after <paramref name="from" /> where the element pattern of
    ///     <paramref name="tag" /> (<c>&lt;tag\s+([^&gt;]*?)(?:/&gt;|&gt;.*?&lt;/tag\s*&gt;)</c>,
    ///     case-insensitive) starts a match, or -1 when none remains. This decides exactly what
    ///     that pattern's free-position scan decides: after the tag name and at least one
    ///     whitespace character the attribute run reaches to the next <c>&gt;</c> (never past
    ///     it), the run ending in <c>/</c> before a <c>&gt;</c> is the self-closing form, and
    ///     any other <c>&gt;</c> opens the element, which needs a <c>&lt;/tag&gt;</c> after it.
    ///     A candidate that fails costs the run to the next <c>&gt;</c> and the search for a
    ///     closing tag, both memoized across the candidates of one scan, so text that never
    ///     closes is walked once, not once per opener.
    /// </summary>
    private static int NextElementStart(string content, int from, string tag, ref ElementScanMemo memo)
    {
        for (var open = content.IndexOf('<', from); open >= 0; open = content.IndexOf('<', open + 1))
        {
            var nameEnd = open + 1 + tag.Length;
            if (nameEnd > content.Length || !EqualsIgnoreCase(content, open + 1, tag))
            {
                continue;
            }

            if (nameEnd >= content.Length || !char.IsWhiteSpace(content[nameEnd]))
            {
                continue;
            }

            var attributes = nameEnd;
            while (attributes < content.Length && char.IsWhiteSpace(content[attributes]))
            {
                attributes++;
            }

            var greater = FirstCharAtOrAfter(content, attributes, '>', ref memo.GreaterFrom, ref memo.GreaterAt);
            if (greater < 0)
            {
                continue;
            }

            if (greater > attributes && content[greater - 1] == '/')
            {
                return open;
            }

            if (FirstElementClose(content, greater + 1, tag, ref memo.CloserFrom, ref memo.CloserAt) >= 0)
            {
                return open;
            }
        }

        return -1;
    }

    /// <summary>Case-insensitive equality of <paramref name="tag" /> with the text at <paramref name="start" />; the caller checks the bounds.</summary>
    private static bool EqualsIgnoreCase(string content, int start, string tag)
    {
        for (var i = 0; i < tag.Length; i++)
        {
            if (char.ToLowerInvariant(content[start + i]) != char.ToLowerInvariant(tag[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    ///     The first index at or after <paramref name="from" /> holding <paramref name="character" />, or -1 when none
    ///     remains, memoized across the candidates of one scan (their queries only move
    ///     forward, so a miss answers every later candidate).
    /// </summary>
    private static int FirstCharAtOrAfter(string content, int from, char character, ref int memoFrom, ref int memoAt)
    {
        if (memoFrom <= from && (memoAt == -1 || memoAt >= from))
        {
            return memoAt;
        }

        memoFrom = from;
        return memoAt = content.IndexOf(character, from);
    }

    /// <summary>
    ///     The first <c>&lt;/tag&gt;</c> (whitespace tolerated before the closing bracket) at or after
    ///     <paramref name="from" />, or -1 when none remains, memoized like
    ///     <see cref="FirstCharAtOrAfter" />.
    /// </summary>
    private static int FirstElementClose(string content, int from, string tag, ref int memoFrom, ref int memoAt)
    {
        if (memoFrom <= from && (memoAt == -1 || memoAt >= from))
        {
            return memoAt;
        }

        var found = -1;
        var nameEnd = 2 + tag.Length;
        for (var close = content.IndexOf('<', from); close >= 0; close = content.IndexOf('<', close + 1))
        {
            if (close + nameEnd < content.Length && content[close + 1] == '/' && EqualsIgnoreCase(content, close + 2, tag))
            {
                var end = close + nameEnd;
                while (end < content.Length && char.IsWhiteSpace(content[end]))
                {
                    end++;
                }

                if (end < content.Length && content[end] == '>')
                {
                    found = close;
                    break;
                }
            }
        }

        memoFrom = from;
        memoAt = found;
        return found;
    }

    /// <summary>The two lookups of one element scan, remembered so the candidates that fail never walk the same text twice.</summary>
    private struct ElementScanMemo
    {
        public int GreaterFrom;
        public int GreaterAt;
        public int CloserFrom;
        public int CloserAt;

        public ElementScanMemo()
        {
            GreaterFrom = 0;
            GreaterAt = -2;
            CloserFrom = 0;
            CloserAt = -2;
        }
    }

    // Exists('...') or Exists("..."), whitespace tolerated around the name, quotes and argument.
    [GeneratedRegex(@"^Exists\s*\(\s*(?:""([^""]*)""|'([^']*)')\s*\)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExistsConditionRegex();

    // A property reference, item reference or property function left after expansion: an import
    // path naming one cannot be resolved without evaluating the build.
    [GeneratedRegex(@"\$\(|@\(", RegexOptions.CultureInvariant)]
    private static partial Regex UnresolvedReferenceRegex();

    [GeneratedRegex(@"^\s*([A-Za-z]+)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex FunctionNameRegex();
}
