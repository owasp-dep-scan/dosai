using Microsoft.CodeAnalysis.CSharp;

namespace Depscan;

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
public static class CSharpSourceParser
{
    private static readonly CSharpParseOptions ParseOptions = new(languageVersion: LanguageVersion.Preview);

    public static CSharpSyntaxTree Parse(string content, string path) =>
        (CSharpSyntaxTree)CSharpSyntaxTree.ParseText(content, ParseOptions, path);
}
