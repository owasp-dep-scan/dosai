namespace Depscan;

using System.Text;
using Microsoft.CodeAnalysis;

// Roslyn writes syntax text with recursive tree walks, so ToString()/ToFullString() on a
// deeply nested machine-generated expression overflows the stack and kills the process.
// Span length is a cheap shallowness proxy: a node spanning <= 1KB cannot nest deeply
// enough to matter, so it keeps Roslyn's fast recursive writer; larger nodes get an
// explicit-stack extraction whose output may differ from ToString() only for such
// pathological nodes (outer trivia is whitespace-trimmed instead of trivia-aware).
internal static class SafeSyntaxText
{
    private const int RecursiveWriteMaxSpan = 1024;

    public static string Text(SyntaxNode syntax)
        => syntax.FullSpan.Length <= RecursiveWriteMaxSpan ? syntax.ToString() : ExtractIteratively(syntax);

    private static string ExtractIteratively(SyntaxNode syntax)
    {
        var builder = new StringBuilder();
        var pending = new Stack<SyntaxNodeOrToken>();
        pending.Push(syntax);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (current.IsToken)
            {
                builder.Append(current.AsToken().ToFullString());
                continue;
            }

            var children = current.ChildNodesAndTokens();
            for (var i = children.Count - 1; i >= 0; i--)
            {
                pending.Push(children[i]);
            }
        }

        return builder.ToString().Trim();
    }
}
