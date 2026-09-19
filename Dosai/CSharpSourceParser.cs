using System.Collections.Generic;
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
/// <remarks>
///     The <c>FileBasedProgram</c> parser feature accepts the <c>#:</c> directives of file-based
///     apps (<c>#:property</c>, <c>#:package</c>, <c>#:include</c>, <c>#:sdk</c>, <c>#:project</c>).
///     Without it every directive reports CS9298, and while the surrounding members still parse,
///     the errors surface in any diagnostic the trees feed. The directives become trivia, so
///     parsing a project-based file is unaffected - the feature only stops the compiler from
///     rejecting lines that a file-based app owns.
/// </remarks>
public static class CSharpSourceParser
{
    private static readonly CSharpParseOptions ParseOptions = new CSharpParseOptions(languageVersion: LanguageVersion.Preview)
        .WithFeatures([new KeyValuePair<string, string>("FileBasedProgram", "true")]);

    public static CSharpSyntaxTree Parse(string content, string path) =>
        (CSharpSyntaxTree)CSharpSyntaxTree.ParseText(content, ParseOptions, path);
}
