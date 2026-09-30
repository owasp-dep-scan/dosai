using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Depscan;

/// <summary>
///     Per-run memoization of every string rendered from a Roslyn symbol during source
///     analysis: method signatures, display strings, interface-name lists. One symbol can be
///     rendered thousands of times without it - a method's signature once per call site it
///     contains, its containing assembly and namespace once per member of its type - and each
///     render both costs CPU and leaves a duplicate string instance behind in
///     <see cref="MethodCalls" />, edges and nodes. Memoizing per symbol returns the same
///     string instance for the same symbol everywhere: downstream graphs hold one string per
///     distinct member instead of one per mention (issue #65 memory scaling).
/// </summary>
/// <remarks>
///     <para>
///         Keyed by symbol <b>reference</b> through <see cref="ConditionalWeakTable{TKey,TValue}" />,
///         deliberately not by <see cref="SymbolEqualityComparer" />. Two reasons. Roslyn's
///         symbol hash code is not cached on the symbol - it walks the containing-symbol chain
///         on every call - so a hash-keyed table made every lookup cost a namespace-chain walk
///         and measurably slowed the worker loop. And a strongly-keyed table is a retention
///         hazard: it pins every symbol ever rendered (and through constructed generics,
///         per-callsite instantiations) for the lifetime of the run; Roslyn's own caches are
///         weak for exactly this reason. Reference identity still dedups everything that
///         matters - one compilation hands out one instance per source and metadata symbol.
///     </para>
///     <para>
///         Created per <see cref="Dosai.GetSourceMethods" /> run and dropped with it. Thread-safe:
///         the per-file symbol loop runs on a worker team, and a value never depends on which
///         thread computed it - Roslyn rendering is deterministic for a fixed symbol and format.
///     </para>
/// </remarks>
internal sealed class SourceRenderCache
{
    private readonly ConditionalWeakTable<ISymbol, string> _displays = new();
    private readonly ConditionalWeakTable<IMethodSymbol, string> _signatures = new();
    private readonly ConditionalWeakTable<INamedTypeSymbol, List<string>> _interfaceNames = new();
    private readonly ConditionalWeakTable<ITypeSymbol, string> _normalizedTypeDisplays = new();
    private readonly ConditionalWeakTable<IMethodSymbol, string> _errorMessageDisplays = new();

    /// <summary>Dosai's stable signature for a method symbol; see <see cref="Dosai.FormatMethodSignature" />.</summary>
    public string Signature(IMethodSymbol? methodSymbol) => methodSymbol is null
        ? string.Empty
        : _signatures.GetValue(methodSymbol, Dosai.FormatMethodSignature);

    /// <summary>The default <c>ToDisplayString()</c> of a symbol, memoized.</summary>
    public string Display(ISymbol? symbol) => symbol is null ? string.Empty : _displays.GetValue(symbol, static s => s.ToDisplayString());

    /// <summary>The fully-qualified display of a type with <c>global::</c>/<c>Global.</c> prefixes stripped, memoized.</summary>
    public string NormalizedFullyQualified(ITypeSymbol? type) => type is null
        ? string.Empty
        : _normalizedTypeDisplays.GetValue(type, static t => Dosai.NormalizeSymbolName(t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));

    /// <summary>The <see cref="SymbolDisplayFormat.CSharpErrorMessageFormat" /> display of a method, normalized, memoized.</summary>
    public string NormalizedErrorMessage(IMethodSymbol methodSymbol) => _errorMessageDisplays.GetValue(methodSymbol,
        static m => Dosai.NormalizeSymbolName(m.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)));

    /// <summary>
    ///     Simple names of every interface a type implements, computed once per type and shared
    ///     by every member of that type. Never mutated after construction (all consumers read).
    /// </summary>
    public List<string> InterfaceNames(INamedTypeSymbol? type)
    {
        if (type is null)
        {
            return [];
        }

        return _interfaceNames.GetValue(type, static t => t.AllInterfaces.Select(i => i.Name).ToList());
    }
}
