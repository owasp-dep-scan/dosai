using System.Collections.Generic;
using Microsoft.CodeAnalysis.CSharp;

namespace Depscan;

/// <summary>
///     Preprocessor symbols the analysis treats as defined when interpreting conditional
///     compilation, in C# (parse options) and F# (the line frontend's region tracking) alike:
///     `NET`, the current `NET{n}_0`, and the `NET{x}_0_OR_GREATER` chain down to .NET 5 -
///     exactly what a build against the latest .NET target defines. Multi-target guards
///     (`#if NET8_0_OR_GREATER`) are near-universal in real libraries; parsing with an empty
///     define set turns their bodies into disabled text, and for a security scanner a missed
///     sink in a guarded branch is worse than a declaration the analyzed project's own target
///     would not compile. `DEBUG`/`TRACE` (a Release-shaped build) and the legacy families
///     (`NETFRAMEWORK`, `NETSTANDARD`) stay undefined, matching a modern net target. Bump the
///     ceiling together with <c>TargetFramework</c>.
/// </summary>
internal static class FrameworkPreprocessorDefines
{
    private const int LatestModernNetMajor = 11;

    public static IReadOnlySet<string> ModernNet { get; } = BuildModernNet();

    private static IReadOnlySet<string> BuildModernNet()
    {
        var symbols = new HashSet<string>(StringComparer.Ordinal) { "NET", $"NET{LatestModernNetMajor}_0" };
        for (var major = 5; major <= LatestModernNetMajor; major++)
        {
            symbols.Add($"NET{major}_0_OR_GREATER");
        }

        return symbols;
    }
}

/// <summary>
///     Central place for building C# syntax trees for analysis. Every analyzer parses through
///     this helper so the accepted language version is decided in one spot.
/// </summary>
/// <remarks>
///     Parsing uses <see cref="LanguageVersion.Preview" />, which is the widest grammar the
///     referenced compiler accepts. Analyzed source is not ours to constrain: a project can
///     target any language version, including one newer than the compiler Dosai references, and
///     source that fails to parse silently disappears from the inventory, call graph, and
///     data-flow results instead of reporting an error. C# 15 union declarations are the current
///     example - the Roslyn 5.9.0 line parses them only under Preview, because its
///     <see cref="LanguageVersion.Default" /> is still C# 14. Preview only widens the accepted
///     grammar, it does not change the meaning of source that already parsed.
/// </remarks>
/// <remarks>
///     The <c>FileBasedProgram</c> parser feature accepts the <c>#:</c> directives of file-based
///     apps (<c>#:property</c>, <c>#:package</c>, <c>#:include</c>, <c>#:sdk</c>, <c>#:project</c>).
///     Without it every directive reports CS9298, and while the surrounding members still parse,
///     the errors surface in any diagnostic the trees feed. The directives become trivia, so
///     parsing a project-based file is unaffected - the feature only stops the compiler from
///     rejecting lines that a file-based app owns.
/// </remarks>
/// <remarks>
///     The parse options also define <see cref="FrameworkPreprocessorDefines.ModernNet" />, so
///     `#if NET8_0_OR_GREATER`-style guards analyze as visible code instead of becoming disabled
///     text that no inventory, call graph, or data-flow result ever sees.
/// </remarks>
public static class CSharpSourceParser
{
    private static readonly CSharpParseOptions ParseOptions = new CSharpParseOptions(languageVersion: LanguageVersion.Preview)
        .WithFeatures([new KeyValuePair<string, string>("FileBasedProgram", "true")])
        .WithPreprocessorSymbols(FrameworkPreprocessorDefines.ModernNet);

    public static CSharpSyntaxTree Parse(string content, string path) =>
        (CSharpSyntaxTree)CSharpSyntaxTree.ParseText(content, ParseOptions, path);

    /// <summary>
    ///     The implicit global usings the .NET SDK adds to every compilation with
    ///     <c>ImplicitUsings</c> enabled. Dosai compiles analyzed source without the project's
    ///     MSBuild context, so without these trees the BCL names an implicit-usings project
    ///     relies on (<c>Path</c>, <c>File</c>, <c>Console</c>, LINQ) fail to bind and every
    ///     such call vanishes from the call graph.
    /// </summary>
    private const string ImplicitGlobalUsingsSource = """
        global using System;
        global using System.Collections.Generic;
        global using System.IO;
        global using System.Linq;
        global using System.Net.Http;
        global using System.Threading;
        global using System.Threading.Tasks;
        """;

    /// <summary>
    ///     A syntax tree carrying the SDK's implicit global usings, for projects under
    ///     <paramref name="path" /> that enable <c>ImplicitUsings</c> (detected from csproj or
    ///     Directory.Build.props). Null when no project enables it, so classic projects keep
    ///     their explicit-usings semantics.
    /// </summary>
    public static CSharpSyntaxTree? TryCreateImplicitUsingsTree(string? path)
    {
        if (!ImplicitUsingsEnabled(path))
        {
            return null;
        }
        return Parse(ImplicitGlobalUsingsSource, "<implicit-usings>");
    }

    private static readonly System.Text.RegularExpressions.Regex ImplicitUsingsRegex =
        new(@"<ImplicitUsings\s*>enable</ImplicitUsings\s*>", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly System.Threading.Lock DetectionLock = new();
    private static readonly Dictionary<string, bool> EnabledByRoot = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     True when any project file or Directory.Build.props under the path enables
    ///     ImplicitUsings. Absent or disabled stays false: inventing the global usings for a
    ///     classic project would bind calls its own compiler rejects.
    /// </summary>
    private static bool ImplicitUsingsEnabled(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }
        string root;
        try
        {
            root = System.IO.Path.GetFullPath(path);
        }
        catch (ArgumentException)
        {
            return false;
        }
        lock (DetectionLock)
        {
            if (EnabledByRoot.TryGetValue(root, out var cached))
            {
                return cached;
            }
            var enabled = DetectImplicitUsings(root);
            EnabledByRoot[root] = enabled;
            return enabled;
        }
    }

    private static bool DetectImplicitUsings(string root)
    {
        try
        {
            foreach (var projectFile in SafeFileRead.EnumerateAllFilesSafe(root, "*.csproj"))
            {
                if (ProjectEnablesImplicitUsings(projectFile))
                {
                    return true;
                }
            }
            foreach (var buildProps in SafeFileRead.EnumerateAllFilesSafe(root, "Directory.Build.props"))
            {
                if (ProjectEnablesImplicitUsings(buildProps))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Best-effort detection; analysis continues with explicit-usings semantics.
        }
        return false;
    }

    private static bool ProjectEnablesImplicitUsings(string projectFile)
    {
        try
        {
            return ImplicitUsingsRegex.IsMatch(File.ReadAllText(projectFile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
