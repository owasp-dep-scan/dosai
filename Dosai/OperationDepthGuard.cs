using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using CSharpSyntax = Microsoft.CodeAnalysis.CSharp.Syntax;
using VisualBasicSyntax = Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace Depscan;

/// <summary>
///     Keeps members whose syntax nests deeper than the analysis stack can carry away from the
///     Roslyn operation factory. The binder guards its own recursion and degrades to an error,
///     but <c>SemanticModel.GetOperation</c> builds the member's whole operation tree with plain
///     recursion, roughly one frame set per nesting level, so a deep enough member terminates the
///     process even on the <see cref="DedicatedStack" /> thread (a fluent chain of about 180,000
///     calls fills 256 MB). Every <c>GetOperation</c> call in the analyzers goes through
///     <see cref="GetOperation" />, which returns null for a node inside such a member; the
///     affected file is reported through <see cref="Describe" /> so the gap is visible instead of
///     a crash that writes nothing.
///     <para>
///         The check is free for ordinary files: every nesting level consumes at least one
///         character of source, so a tree shorter than the depth limit cannot exceed it and is
///         never walked. Longer trees get one iterative walk, cached per tree.
///     </para>
/// </summary>
internal static class OperationDepthGuard
{
    /// <summary>
    ///     Stack budget per syntax level. 256 MB overflowed at about 180,000 chained calls (two
    ///     syntax levels each), roughly 0.7 KB per level; 2 KB leaves room for heavier constructs.
    /// </summary>
    private const int StackBytesPerSyntaxLevel = 2048;

    /// <summary>Smallest common default thread stack, assumed off the analysis thread.</summary>
    private const int CallerStackSize = 1024 * 1024;

    /// <summary>Levels above any member (compilation unit, namespace, type) that the length bound must absorb.</summary>
    private const int ContainerSlack = 64;

    private static readonly ConditionalWeakTable<SyntaxTree, DeepMembers> Cache = new();
    private static readonly AsyncLocal<int?> MaxSyntaxDepthOverride = new();

    /// <summary>Deepest syntax nesting a member may have for its operation tree to be built.</summary>
    internal static int MaxSyntaxDepth => MaxSyntaxDepthOverride.Value
        ?? (DedicatedStack.IsOnAnalysisThread ? DedicatedStack.AnalysisStackSize : CallerStackSize) / StackBytesPerSyntaxLevel;

    /// <summary><c>model.GetOperation(node)</c>, or null when the member holding <paramref name="node" /> nests too deeply.</summary>
    internal static IOperation? GetOperation(SemanticModel model, SyntaxNode node)
        => IsSafe(node) ? model.GetOperation(node) : null;

    /// <summary>True when an operation tree may be requested for <paramref name="node" />.</summary>
    internal static bool IsSafe(SyntaxNode node)
    {
        var deepMembers = For(node.SyntaxTree);
        if (deepMembers.Spans.Count == 0)
        {
            return true;
        }

        var span = node.Span;
        return !deepMembers.Spans.Any(deep => deep.OverlapsWith(span) || (span.IsEmpty && deep.Contains(span.Start)));
    }

    /// <summary>Diagnostic for a tree with skipped members, or null when nothing was skipped.</summary>
    internal static string? Describe(SyntaxTree tree)
    {
        var deepMembers = For(tree);
        if (deepMembers.Spans.Count == 0)
        {
            return null;
        }

        var firstLine = tree.GetLineSpan(deepMembers.Spans[0]).StartLinePosition.Line + 1;
        return $"{tree.FilePath}: {deepMembers.Spans.Count} member(s) nest deeper than {deepMembers.MaxDepth} syntax levels (first at line {firstLine}); their semantic operations were skipped to keep the analysis within its stack, so calls, data flows and crypto uses inside them are missing.";
    }

    /// <summary>Test hook: lowers the depth limit for the current execution context (and the analysis threads it starts).</summary>
    internal static IDisposable OverrideMaxSyntaxDepth(int maxDepth)
    {
        var previous = MaxSyntaxDepthOverride.Value;
        MaxSyntaxDepthOverride.Value = maxDepth;
        return new Restore(() => MaxSyntaxDepthOverride.Value = previous);
    }

    private static DeepMembers For(SyntaxTree tree)
    {
        var maxDepth = MaxSyntaxDepth;
        if (Cache.TryGetValue(tree, out var cached) && cached.MaxDepth == maxDepth)
        {
            return cached;
        }

        var computed = new DeepMembers(maxDepth, FindDeepMembers(tree, maxDepth));
        Cache.AddOrUpdate(tree, computed);
        return computed;
    }

    /// <summary>
    ///     Spans of the members nesting deeper than <paramref name="maxDepth" />, in document
    ///     order. A member is a non-container child of the compilation unit, a namespace or a type;
    ///     top-level statements share one synthesized entry point, so one deep global statement
    ///     marks all of them.
    /// </summary>
    internal static List<TextSpan> FindDeepMembers(SyntaxTree tree, int maxDepth)
    {
        if (tree.Length + ContainerSlack <= maxDepth)
        {
            return [];
        }

        var root = tree.GetRoot();
        var deep = new HashSet<SyntaxNode>();
        var globalStatementsTooDeep = false;
        var pending = new Stack<(SyntaxNode Node, int Depth, SyntaxNode? Member)>();
        pending.Push((root, 0, null));
        while (pending.Count > 0)
        {
            var (node, depth, member) = pending.Pop();
            member ??= IsContainer(node) ? null : node;
            if (depth > maxDepth)
            {
                if (member is CSharpSyntax.GlobalStatementSyntax)
                {
                    globalStatementsTooDeep = true;
                }

                deep.Add(member ?? root);
                continue;
            }

            if (member is not null && deep.Contains(member))
            {
                continue;
            }

            foreach (var child in node.ChildNodes())
            {
                pending.Push((child, depth + 1, member));
            }
        }

        if (globalStatementsTooDeep && root is CSharpSyntax.CompilationUnitSyntax compilationUnit)
        {
            deep.UnionWith(compilationUnit.Members.OfType<CSharpSyntax.GlobalStatementSyntax>());
        }

        return deep.Select(node => node.Span).OrderBy(span => span.Start).ToList();
    }

    private static bool IsContainer(SyntaxNode node) => node is
        CSharpSyntax.CompilationUnitSyntax or CSharpSyntax.BaseNamespaceDeclarationSyntax or CSharpSyntax.BaseTypeDeclarationSyntax or
        VisualBasicSyntax.CompilationUnitSyntax or VisualBasicSyntax.NamespaceBlockSyntax or VisualBasicSyntax.TypeBlockSyntax or VisualBasicSyntax.EnumBlockSyntax;

    private sealed record DeepMembers(int MaxDepth, List<TextSpan> Spans);

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
