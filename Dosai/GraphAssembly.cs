
namespace Depscan;

/// <summary>
///     Shared call-graph assembly primitives: call-site de-duplication keys and the edge/node
///     orderings every graph-producing phase ends with.
/// </summary>
/// <remarks>
///     De-duplication and ordering used to run through one concatenated key string per edge
///     (<c>source\u001ftarget\u001ffile:line:col:type:kind</c>). On a tree with millions of call
///     sites that is a ~200-byte string allocated, hashed and retained per edge - gigabytes of
///     transient garbage at exactly the moment the graph is assembled, and the retention was
///     what the issue #65 measurements saw climbing after symbol analysis had finished. The
///     struct key below carries the same identity with no allocation; string legs compare by
///     reference first (ids rendered through <see cref="SourceRenderCache" /> and per-file
///     names are single instances) and fall back to ordinal content comparison, so keys built
///     from non-pooled strings behave exactly like the concatenated form.
/// </remarks>
internal static class GraphAssembly
{
    /// <summary>Enum names as constants, indexed by enum value: interpolation used to call <c>ToString()</c> per key.</summary>
    private static readonly string[] CallTypeNames = NamesByValue<CallType>();

    private static readonly string[] EvidenceKindNames = NamesByValue<AnalysisEvidenceKind>();

    private static string[] NamesByValue<TE>() where TE : struct, Enum
    {
        var max = -1;
        foreach (var value in Enum.GetValues<TE>())
        {
            max = Math.Max(max, (int)(object)value);
        }

        var names = new string[max + 1];
        foreach (var value in Enum.GetValues<TE>())
        {
            names[(int)(object)value] = value.ToString();
        }

        return names;
    }

    internal static string CallTypeName(CallType callType) => CallTypeNames[(int)callType];

    internal static string EvidenceKindName(AnalysisEvidenceKind kind) => EvidenceKindNames[(int)kind];

    /// <summary>
    ///     Identity of a call site: who called whom, where, with which call type and evidence
    ///     kind. Two records with the same key describe the same site and collapse to one edge.
    /// </summary>
    internal readonly struct EdgeSiteKey
    {
        internal readonly string _sourceId;
        internal readonly string _targetId;
        private readonly string? _fileName;
        private readonly int _lineNumber;
        private readonly int _columnNumber;
        private readonly string _tagA;
        private readonly string _tagB;

        public EdgeSiteKey(string sourceId, string targetId, string? fileName, int lineNumber, int columnNumber, string tagA, string tagB)
        {
            _sourceId = sourceId;
            _targetId = targetId;
            _fileName = fileName;
            _lineNumber = lineNumber;
            _columnNumber = columnNumber;
            _tagA = tagA;
            _tagB = tagB;
        }

        public static EdgeSiteKey FromCall(MethodCalls call, AnalysisEvidenceKind evidenceKind) =>
            new(call.SourceId!, call.TargetId!, call.FileName, call.LineNumber, call.ColumnNumber, CallTypeName(call.CallType), EvidenceKindName(evidenceKind));

        public static EdgeSiteKey FromEdge(MethodCallEdge edge) =>
            new(edge.SourceId, edge.TargetId, edge.CallLocation.FileName, edge.CallLocation.LineNumber, edge.CallLocation.ColumnNumber, CallTypeName(edge.CallType), EvidenceKindName(edge.EvidenceKind));

        /// <summary>Key with an explicit trailing tag, for the assembly path's synthesized candidate/delegate identities.</summary>
        public static EdgeSiteKey Tagged(string sourceId, string targetId, string? fileName, int lineNumber, int columnNumber, string tagA, string tagB) =>
            new(sourceId, targetId, fileName, lineNumber, columnNumber, tagA, tagB);

        public bool Equals(EdgeSiteKey other) =>
            _lineNumber == other._lineNumber
            && _columnNumber == other._columnNumber
            && SameString(_tagA, other._tagA)
            && SameString(_tagB, other._tagB)
            && SameString(_sourceId, other._sourceId)
            && SameString(_targetId, other._targetId)
            && SameString(_fileName, other._fileName);

        public override bool Equals([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] object? obj) => obj is EdgeSiteKey other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(
            _lineNumber,
            _columnNumber,
            Hash(_tagA),
            Hash(_tagB),
            Hash(_sourceId),
            Hash(_targetId),
            Hash(_fileName));

        private static bool SameString(string? a, string? b) => ReferenceEquals(a, b) || string.Equals(a, b, StringComparison.Ordinal);

        private static int Hash(string? s) => s?.GetHashCode(StringComparison.Ordinal) ?? 0;
    }

    internal sealed class EdgeSiteKeyComparer : IEqualityComparer<EdgeSiteKey>
    {
        public static readonly EdgeSiteKeyComparer Instance = new();

        public bool Equals(EdgeSiteKey x, EdgeSiteKey y) => x.Equals(y);

        public int GetHashCode(EdgeSiteKey obj) => obj.GetHashCode();
    }

    /// <summary>
    ///     The graph's canonical edge order: source, target, file, line, column. The
    ///     <paramref name="originalOrder" /> tiebreak makes the sort stable, matching the
    ///     LINQ <c>OrderBy</c> chain it replaces; the caller supplies each edge's position
    ///     before sorting.
    /// </summary>
    internal static int CompareEdges(MethodCallEdge x, MethodCallEdge y)
    {
        var c = string.CompareOrdinal(x.SourceId, y.SourceId);
        if (c != 0) return c;
        c = string.CompareOrdinal(x.TargetId, y.TargetId);
        if (c != 0) return c;
        c = string.CompareOrdinal(x.CallLocation.FileName, y.CallLocation.FileName);
        if (c != 0) return c;
        c = x.CallLocation.LineNumber.CompareTo(y.CallLocation.LineNumber);
        if (c != 0) return c;
        return x.CallLocation.ColumnNumber.CompareTo(y.CallLocation.ColumnNumber);
    }

    /// <summary>
    ///     Stable in-place sort of a graph edge list into the canonical order. An index sort
    ///     with an original-position tiebreak, so equal-key edges keep their input order (the
    ///     stability the previous LINQ chain provided) while skipping its key arrays and
    ///     per-element delegates.
    /// </summary>
    internal static void SortEdgesInPlace(List<MethodCallEdge> edges) => StableSortInPlace(edges, CompareEdges);

    /// <summary>
    ///     Stable in-place index sort under an arbitrary edge comparison: the original-position
    ///     tiebreak makes it equivalent to the LINQ <c>OrderBy</c> chain it replaces, without
    ///     per-element key materialization.
    /// </summary>
    internal static void StableSortInPlace(List<MethodCallEdge> edges, Comparison<MethodCallEdge> compare)
    {
        var order = new int[edges.Count];
        for (var i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }

        Array.Sort(order, (i, j) =>
        {
            var c = compare(edges[i], edges[j]);
            return c != 0 ? c : i.CompareTo(j);
        });
        var sorted = new MethodCallEdge[edges.Count];
        for (var i = 0; i < order.Length; i++)
        {
            sorted[i] = edges[order[i]];
        }

        edges.Clear();
        edges.AddRange(sorted);
    }

    /// <summary>The separator the collapsed-edge grouping used to build as one string per edge.</summary>
    private const char CollapseSeparator = '\u001f';

    /// <summary>
    ///     Group identity of a collapsed edge: who called whom, with which call type and evidence
    ///     kind. A struct so grouping and ordering work without materializing the concatenated
    ///     key string the collapse step used to allocate per edge (issue #70: ~447 bytes x 4.1 M
    ///     edges on a large tree, ~7 GB on a 15.4 M-edge one).
    /// </summary>
    internal readonly struct CollapseKey : IEquatable<CollapseKey>
    {
        internal readonly string _sourceId;
        internal readonly string _targetId;
        internal readonly CallType _callType;
        internal readonly AnalysisEvidenceKind _evidenceKind;

        public CollapseKey(string sourceId, string targetId, CallType callType, AnalysisEvidenceKind evidenceKind)
        {
            _sourceId = sourceId;
            _targetId = targetId;
            _callType = callType;
            _evidenceKind = evidenceKind;
        }

        public static CollapseKey From(MethodCallEdge edge) => new(edge.SourceId, edge.TargetId, edge.CallType, edge.EvidenceKind);

        public bool Equals(CollapseKey other) =>
            _callType == other._callType
            && _evidenceKind == other._evidenceKind
            && string.Equals(_sourceId, other._sourceId, StringComparison.Ordinal)
            && string.Equals(_targetId, other._targetId, StringComparison.Ordinal);

        public override bool Equals([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] object? obj) => obj is CollapseKey other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(
            _sourceId.GetHashCode(StringComparison.Ordinal),
            _targetId.GetHashCode(StringComparison.Ordinal),
            _callType,
            _evidenceKind);
    }

    /// <summary>
    ///     Orders edges the way the concatenated key <c>source\u001ftarget\u001fcallType\u001fevidenceKind</c>
    ///     ordered them, without building that key: the collapse step used to allocate and retain
    ///     one ~447-byte key per edge (issue #70) - 1.7 GB on a tree with 4.1 M merged edges, and
    ///     the allocation that pushed a 15.4 M-edge scan out of memory. Field-by-field ordinal
    ///     comparison is *almost* this order; where one field is a strict prefix of the other, the
    ///     concatenated key continues with the separator, and end-of-string sorts below the
    ///     CR/LF/tab characters rendered ids can contain while U+001F sorts above them.
    ///     <see cref="CompareSeparated" /> encodes exactly that.
    /// </summary>
    internal static int CompareCollapseEdges(MethodCallEdge x, MethodCallEdge y) => CompareCollapseKeys(CollapseKey.From(x), CollapseKey.From(y));

    /// <summary><see cref="CompareCollapseEdges" /> over the group-identity struct.</summary>
    internal static int CompareCollapseKeys(CollapseKey x, CollapseKey y)
    {
        var c = CompareSeparated(x._sourceId ?? string.Empty, y._sourceId ?? string.Empty);
        if (c != 0) return c;
        c = CompareSeparated(x._targetId ?? string.Empty, y._targetId ?? string.Empty);
        if (c != 0) return c;
        c = CompareSeparated(CallTypeName(x._callType), CallTypeName(y._callType));
        if (c != 0) return c;
        return string.CompareOrdinal(EvidenceKindName(x._evidenceKind), EvidenceKindName(y._evidenceKind));
    }

    /// <summary>
    ///     Compares <c>a + separator</c> against <c>b + separator</c> ordinally without
    ///     concatenating. A difference before either string ends decides directly; only a strict
    ///     prefix reaches the separator position, and there the sign flips for continuations
    ///     below U+001F (a CR/LF/tab continuation sorts before the separator, and after the whole
    ///     shorter field). A field that itself contains U+001F falls back to the exact
    ///     character-wise form, where such fields made the old concatenated keys ambiguous
    ///     (different field tuples could collide into one key and be wrongly collapsed together).
    /// </summary>
    private static int CompareSeparated(string a, string b)
    {
        var c = string.CompareOrdinal(a, b);
        if (c == 0)
        {
            return 0;
        }

        string prefix, longer;
        if (c < 0)
        {
            if (a.Length >= b.Length || !b.StartsWith(a, StringComparison.Ordinal))
            {
                return c;
            }

            prefix = a;
            longer = b;
        }
        else
        {
            if (b.Length >= a.Length || !a.StartsWith(b, StringComparison.Ordinal))
            {
                return c;
            }

            prefix = b;
            longer = a;
        }

        var next = longer[prefix.Length];
        if (next == CollapseSeparator)
        {
            return CompareSeparatedExact(a, b);
        }

        var shorterFirst = next > CollapseSeparator ? -1 : 1;
        return ReferenceEquals(prefix, a) ? shorterFirst : -shorterFirst;
    }

    private static int CompareSeparatedExact(string a, string b)
    {
        var la = a.Length + 1;
        var lb = b.Length + 1;
        for (var i = 0; i < Math.Min(la, lb); i++)
        {
            var ca = i < a.Length ? a[i] : CollapseSeparator;
            var cb = i < b.Length ? b[i] : CollapseSeparator;
            if (ca != cb)
            {
                return ca < cb ? -1 : 1;
            }
        }

        return la.CompareTo(lb);
    }

    /// <summary>Sorts nodes by id. Ids are unique per graph (they are dictionary keys), so stability is moot.</summary>
    internal static void SortNodesInPlace(List<MethodNode> nodes) =>
        nodes.Sort(static (x, y) => string.CompareOrdinal(x.Id, y.Id));

    /// <summary>Assigns sequential edge ids (<c>e1..</c> / any prefix) in current list order.</summary>
    internal static void AssignEdgeIds(List<MethodCallEdge> edges, string prefix)
    {
        for (var i = 0; i < edges.Count; i++)
        {
            edges[i].Id = $"{prefix}{i + 1}";
        }
    }

    internal static bool IsBlank(string? s) => string.IsNullOrWhiteSpace(s);
}
