using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Depscan;

/// <summary>
///     C# 15 union types as the compiler defines them. A union declaration
///     (<c>union Pet(Cat, Dog)</c>) lowers to a struct with one public single-parameter
///     constructor per case and an <c>object?</c> <c>Value</c> property. A hand-written
///     <c>[Union]</c> type takes its case types from the same constructors or, when it implements
///     a nested member-provider interface named <c>IUnionMembers</c>, from that interface's
///     single-parameter static <c>Create</c> methods. Static <c>Create</c> methods declared on
///     the type itself are not creation members (the compiler reports CS9385 for a union that
///     has only those).
/// </summary>
internal static class UnionShapes
{
    /// <summary>The member-provider interface a hand-written union may nest and implement.</summary>
    public const string MemberProviderInterfaceName = "IUnionMembers";

    /// <summary>The factory name the compiler looks for on a member-provider interface.</summary>
    public const string FactoryMethodName = "Create";

    /// <summary>
    ///     The union type <paramref name="type" /> names, looking through <c>Nullable&lt;T&gt;</c>,
    ///     or null when it is not a union.
    /// </summary>
    /// <remarks>
    ///     <c>ITypeSymbol.IsUnion</c> is marked experimental (RSEXPERIMENTAL006) on the
    ///     referenced compiler and is the only union marker its public API surfaces. Replace
    ///     <see cref="CaseTypes" /> with the compiler's own case-type list once a compiler package
    ///     exposes it, which removes the creation-member reading below.
    /// </remarks>
    public static INamedTypeSymbol? AsUnion(ITypeSymbol? type)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T, TypeArguments: [var underlying] })
        {
            type = underlying;
        }

#pragma warning disable RSEXPERIMENTAL006
        return type is INamedTypeSymbol { IsUnion: true } union ? union : null;
#pragma warning restore RSEXPERIMENTAL006
    }

    /// <summary>
    ///     The case types of <paramref name="union" />, in declaration order and without
    ///     duplicates: the parameter types of its public single-parameter instance constructors
    ///     (its own copy constructor excluded), then the parameter types of the single-parameter
    ///     static <c>Create</c> methods of an implemented <c>IUnionMembers</c> interface.
    /// </summary>
    public static IReadOnlyList<ITypeSymbol> CaseTypes(INamedTypeSymbol union)
    {
        var caseTypes = new List<ITypeSymbol>();
        foreach (var constructor in union.InstanceConstructors)
        {
            if (constructor is { DeclaredAccessibility: Accessibility.Public, Parameters: [var parameter] })
            {
                AddCaseType(caseTypes, union, parameter.Type);
            }
        }

        foreach (var provider in union.AllInterfaces)
        {
            if (!string.Equals(provider.Name, MemberProviderInterfaceName, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var member in provider.GetMembers(FactoryMethodName))
            {
                if (member is IMethodSymbol { IsStatic: true, Parameters: [var parameter], ReturnType: var returnType } &&
                    (SymbolEqualityComparer.Default.Equals(returnType, union) || SymbolEqualityComparer.Default.Equals(returnType.OriginalDefinition, union.OriginalDefinition)))
                {
                    AddCaseType(caseTypes, union, parameter.Type);
                }
            }
        }

        return caseTypes;
    }

    private static void AddCaseType(List<ITypeSymbol> caseTypes, INamedTypeSymbol union, ITypeSymbol caseType)
    {
        if (SymbolEqualityComparer.Default.Equals(caseType, union) ||
            caseTypes.Any(existing => SymbolEqualityComparer.Default.Equals(existing, caseType)))
        {
            return;
        }

        caseTypes.Add(caseType);
    }
}

/// <summary>
///     A switch over a union value that the compiler accepts but that can misbehave at run time.
///     <see cref="EmptySlots" /> names, for a switch expression, each position where an empty
///     union matches no arm (<c>value</c> for the switched value itself, else the deconstruction
///     index or member chain inside it); <see cref="MissingCases" /> names, for a switch
///     statement, the case types no clause handles.
/// </summary>
internal sealed record UnionSwitchFinding(string Kind, string Cwe, INamedTypeSymbol Union, IReadOnlyList<ITypeSymbol> MissingCases, IReadOnlyList<string> EmptySlots, SyntaxNode Syntax);

/// <summary>
///     Review checks for switches over C# 15 unions. A union is a struct, so <c>default</c>, an
///     uninitialized field or array element, or a deserialized empty value is a union whose
///     <c>Value</c> is null. The compiler treats a switch expression that names every case type
///     as exhaustive, yet that empty union matches none of the arms and throws
///     <c>SwitchExpressionException</c>; the documented guard is a <c>null</c> arm, which, unlike
///     a discard, still lets the compiler report a case added later. The same holds for a union
///     inside the switched value (a tuple element, a positional member, a property). Switch
///     statements get no exhaustiveness check at all, so a case type with no clause and no
///     <c>default</c> is skipped silently.
/// </summary>
/// <remarks>
///     Both checks report only what the patterns prove. A switch expression is reported for a
///     union position when every unguarded arm rejects an empty union there, whatever the rest
///     of the value holds, so such a value always throws; an arm whose pattern at that position
///     is not understood counts as letting it through. A switch statement is reported for a case
///     type that no unguarded clause, or group of numeric and boolean clauses, accepts in full.
/// </remarks>
internal static class UnionSwitchAnalysis
{
    public const string MissingNullArmKind = "UnionSwitchWithoutNullArm";
    public const string MissingCaseKind = "UnionSwitchMissingCase";
    public const string Category = "union-switch";
    public const string RootSlot = "value";

    private enum Match
    {
        No,
        Yes,
        Unknown
    }

    /// <summary>A switch expression with a union position where an empty union matches no unguarded arm.</summary>
    public static UnionSwitchFinding? Inspect(ISwitchExpressionOperation operation)
    {
        var slots = new List<(string[] Path, INamedTypeSymbol Union)>();
        if (UnionShapes.AsUnion(operation.Value.Type) is { } rootUnion)
        {
            slots.Add(([], rootUnion));
        }

        foreach (var arm in operation.Arms)
        {
            CollectNestedSlots(arm.Pattern, [], slots);
        }

        var emptySlots = new List<string>();
        INamedTypeSymbol? firstUnion = null;
        foreach (var (path, union) in slots)
        {
            if (operation.Arms.Any(arm => arm.Guard is null && LetsEmptyThrough(arm.Pattern, path)))
            {
                continue;
            }

            firstUnion ??= union;
            emptySlots.Add(path.Length == 0 ? RootSlot : string.Join('.', path));
        }

        return firstUnion is null ? null : new UnionSwitchFinding(MissingNullArmKind, "CWE-248", firstUnion, [], emptySlots, operation.Syntax);
    }

    /// <summary>
    ///     A switch statement over a union with no <c>default</c> clause and at least one case
    ///     type that the unguarded clauses do not accept in full.
    /// </summary>
    public static UnionSwitchFinding? Inspect(ISwitchOperation operation)
    {
        if (UnionShapes.AsUnion(operation.Value.Type) is not { } union)
        {
            return null;
        }

        var patterns = new List<IPatternOperation>();
        var constants = new List<IOperation>();
        foreach (var clause in operation.Cases.SelectMany(@case => @case.Clauses))
        {
            switch (clause)
            {
                case IDefaultCaseClauseOperation:
                    return null;
                case IPatternCaseClauseOperation { Guard: null, Pattern: var pattern }:
                    patterns.Add(pattern);
                    break;
                case ISingleValueCaseClauseOperation { Value: { } value }:
                    constants.Add(value);
                    break;
            }
        }

        var missing = UnionShapes.CaseTypes(union)
            .Where(caseType => !IsCovered(caseType, patterns, constants))
            .ToList();
        return missing.Count == 0 ? null : new UnionSwitchFinding(MissingCaseKind, "CWE-478", union, missing, [], operation.Syntax);
    }

    // ----- Empty union inside a switch expression -----

    /// <summary>
    ///     Records every position below the switched value whose pattern input is a union, with
    ///     the steps that lead to it: <c>#i</c> for the i-th deconstruction element and member
    ///     names for property subpatterns (an extended <c>{ A.B: p }</c> gives the same steps as
    ///     <c>{ A: { B: p } }</c>).
    /// </summary>
    private static void CollectNestedSlots(IPatternOperation pattern, string[] path, List<(string[] Path, INamedTypeSymbol Union)> slots)
    {
        switch (pattern)
        {
            case IRecursivePatternOperation recursive:
                for (var index = 0; index < recursive.DeconstructionSubpatterns.Length; index++)
                {
                    VisitSubpattern(recursive.DeconstructionSubpatterns[index], [.. path, string.Create(CultureInfo.InvariantCulture, $"#{index}")], slots);
                }

                foreach (var property in recursive.PropertySubpatterns)
                {
                    if (MemberSteps(property) is { } steps)
                    {
                        VisitSubpattern(property.Pattern, [.. path, .. steps], slots);
                    }
                }

                break;
            case IBinaryPatternOperation binary:
                CollectNestedSlots(binary.LeftPattern, path, slots);
                CollectNestedSlots(binary.RightPattern, path, slots);
                break;
            case INegatedPatternOperation negated:
                CollectNestedSlots(negated.Pattern, path, slots);
                break;
        }
    }

    private static void VisitSubpattern(IPatternOperation subpattern, string[] path, List<(string[] Path, INamedTypeSymbol Union)> slots)
    {
        if (UnionShapes.AsUnion(subpattern.InputType) is { } union &&
            !slots.Any(slot => slot.Path.AsSpan().SequenceEqual(path)))
        {
            slots.Add((path, union));
        }

        CollectNestedSlots(subpattern, path, slots);
    }

    /// <summary>The member names a property subpattern steps through, or null when its member is not a plain property or field chain.</summary>
    private static string[]? MemberSteps(IPropertySubpatternOperation property)
    {
        var steps = new List<string>();
        for (var member = property.Member; member is not null;)
        {
            switch (member)
            {
                case IMemberReferenceOperation reference:
                    steps.Add(reference.Member.Name);
                    member = reference.Instance is IMemberReferenceOperation ? reference.Instance : null;
                    break;
                default:
                    return null;
            }
        }

        steps.Reverse();
        return [.. steps];
    }

    /// <summary>
    ///     Whether some value whose union at <paramref name="path" /> is empty may match the
    ///     pattern. Only a pattern that tests that position, or the whole value, and rejects the
    ///     empty union answers false.
    /// </summary>
    private static bool LetsEmptyThrough(IPatternOperation pattern, ReadOnlySpan<string> path)
    {
        if (path.IsEmpty)
        {
            return MatchesEmptyUnion(pattern) != Match.No;
        }

        switch (pattern)
        {
            case IConstantPatternOperation constant when IsNullConstant(constant):
                // Matches only a null container, which holds no union at all.
                return false;
            case IBinaryPatternOperation { OperatorKind: BinaryOperatorKind.Or } binary:
                return LetsEmptyThrough(binary.LeftPattern, path) || LetsEmptyThrough(binary.RightPattern, path);
            case IBinaryPatternOperation { OperatorKind: BinaryOperatorKind.And } binary:
                return LetsEmptyThrough(binary.LeftPattern, path) && LetsEmptyThrough(binary.RightPattern, path);
            case IRecursivePatternOperation recursive:
                var step = path[0];
                if (step.StartsWith('#'))
                {
                    var index = int.Parse(step.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture);
                    return index >= recursive.DeconstructionSubpatterns.Length || LetsEmptyThrough(recursive.DeconstructionSubpatterns[index], path[1..]);
                }

                foreach (var property in recursive.PropertySubpatterns)
                {
                    if (MemberSteps(property) is not { } steps)
                    {
                        continue;
                    }

                    var shared = 0;
                    while (shared < steps.Length && shared < path.Length && string.Equals(steps[shared], path[shared], StringComparison.Ordinal))
                    {
                        shared++;
                    }

                    // A subpattern on the path, reaching the position: it must let the empty
                    // union through, like every other subpattern there.
                    if (shared == steps.Length && !LetsEmptyThrough(property.Pattern, path[shared..]))
                    {
                        return false;
                    }
                }

                return true;
            default:
                // A type test, a discard, `var`, or a pattern not understood leaves the
                // position unconstrained.
                return true;
        }
    }

    /// <summary>
    ///     Whether the pattern matches a union whose <c>Value</c> is null: a discard, a
    ///     <c>var</c> declaration, a <c>null</c> constant and an empty property pattern
    ///     <c>{ }</c> do; a type, relational, constant or list test of a case value does not;
    ///     <c>not</c>, <c>or</c> and <c>and</c> combine those answers.
    /// </summary>
    private static Match MatchesEmptyUnion(IPatternOperation pattern) => pattern switch
    {
        IDiscardPatternOperation => Match.Yes,
        IDeclarationPatternOperation { MatchedType: null } => Match.Yes,
        IConstantPatternOperation constant when IsNullConstant(constant) => Match.Yes,
        IRecursivePatternOperation recursive when IsWholeInput(recursive) => recursive.DeconstructionSubpatterns.IsEmpty && recursive.PropertySubpatterns.IsEmpty ? Match.Yes : Match.Unknown,
        INegatedPatternOperation negated => MatchesEmptyUnion(negated.Pattern) switch
        {
            Match.Yes => Match.No,
            Match.No => Match.Yes,
            _ => Match.Unknown
        },
        IBinaryPatternOperation { OperatorKind: BinaryOperatorKind.Or } binary => (MatchesEmptyUnion(binary.LeftPattern), MatchesEmptyUnion(binary.RightPattern)) switch
        {
            (Match.Yes, _) or (_, Match.Yes) => Match.Yes,
            (Match.No, Match.No) => Match.No,
            _ => Match.Unknown
        },
        IBinaryPatternOperation { OperatorKind: BinaryOperatorKind.And } binary => (MatchesEmptyUnion(binary.LeftPattern), MatchesEmptyUnion(binary.RightPattern)) switch
        {
            (Match.No, _) or (_, Match.No) => Match.No,
            (Match.Yes, Match.Yes) => Match.Yes,
            _ => Match.Unknown
        },
        IDeclarationPatternOperation or ITypePatternOperation or IRecursivePatternOperation or IRelationalPatternOperation or IConstantPatternOperation or IListPatternOperation => Match.No,
        _ => Match.Unknown
    };

    // ----- Case coverage in a switch statement -----

    /// <summary>
    ///     Whether the clauses accept every value of <paramref name="caseType" />: one clause
    ///     accepts all of it, or, for an integral, <c>char</c> or <c>bool</c> case, the values
    ///     the clauses' relational and constant patterns accept add up to the type's range.
    /// </summary>
    private static bool IsCovered(ITypeSymbol caseType, List<IPatternOperation> patterns, List<IOperation> constants)
    {
        if (patterns.Any(pattern => Accepts(pattern, caseType)))
        {
            return true;
        }

        if (IntegralRange.For(caseType) is not { } range)
        {
            return false;
        }

        var accepted = IntegralRange.Empty;
        foreach (var pattern in patterns)
        {
            accepted = IntegralRange.Union(accepted, AcceptedValues(pattern, caseType, range));
        }

        // `case 5:` binds as a single-value clause, not a pattern; its constant counts when it
        // has the case type.
        foreach (var constant in constants)
        {
            if (constant.Type is { } constantType && SymbolEqualityComparer.Default.Equals(constantType, caseType) && IntegralRange.ToInt128(constant.ConstantValue) is { } point)
            {
                accepted = IntegralRange.Union(accepted, [(point, point)]);
            }
        }

        return IntegralRange.Covers(accepted, range);
    }

    /// <summary>Whether the pattern accepts every value of <paramref name="caseType" />.</summary>
    private static bool Accepts(IPatternOperation pattern, ITypeSymbol caseType) => pattern switch
    {
        IDiscardPatternOperation => true,
        IDeclarationPatternOperation { MatchedType: null } => true,
        IDeclarationPatternOperation { MatchedType: { } matched } => IsHandledBy(caseType, matched),
        ITypePatternOperation { MatchedType: var matched } => IsHandledBy(caseType, matched),
        IRecursivePatternOperation recursive when recursive.DeconstructionSubpatterns.All(IsCatchAll) && recursive.PropertySubpatterns.All(property => IsCatchAll(property.Pattern)) =>
            IsWholeInput(recursive) || IsHandledBy(caseType, recursive.MatchedType!),
        INegatedPatternOperation negated => Rejects(negated.Pattern, caseType),
        IBinaryPatternOperation { OperatorKind: BinaryOperatorKind.Or } binary => Accepts(binary.LeftPattern, caseType) || Accepts(binary.RightPattern, caseType),
        IBinaryPatternOperation { OperatorKind: BinaryOperatorKind.And } binary => Accepts(binary.LeftPattern, caseType) && Accepts(binary.RightPattern, caseType),
        _ => false
    };

    /// <summary>Whether the pattern accepts no value of <paramref name="caseType" />.</summary>
    private static bool Rejects(IPatternOperation pattern, ITypeSymbol caseType) => pattern switch
    {
        IConstantPatternOperation constant when IsNullConstant(constant) => true,
        IDeclarationPatternOperation { MatchedType: { } matched } => AreDisjoint(caseType, matched),
        ITypePatternOperation { MatchedType: var matched } => AreDisjoint(caseType, matched),
        IRecursivePatternOperation recursive when !IsWholeInput(recursive) => AreDisjoint(caseType, recursive.MatchedType!),
        IRelationalPatternOperation or IConstantPatternOperation or IListPatternOperation => AreDisjoint(caseType, pattern.NarrowedType),
        INegatedPatternOperation negated => Accepts(negated.Pattern, caseType),
        IBinaryPatternOperation { OperatorKind: BinaryOperatorKind.Or } binary => Rejects(binary.LeftPattern, caseType) && Rejects(binary.RightPattern, caseType),
        IBinaryPatternOperation { OperatorKind: BinaryOperatorKind.And } binary => Rejects(binary.LeftPattern, caseType) || Rejects(binary.RightPattern, caseType),
        _ => false
    };

    /// <summary>The values of an integral, <c>char</c> or <c>bool</c> case type that the pattern accepts.</summary>
    private static List<(Int128 Low, Int128 High)> AcceptedValues(IPatternOperation pattern, ITypeSymbol caseType, (Int128 Low, Int128 High) range)
    {
        if (Accepts(pattern, caseType))
        {
            return [range];
        }

        switch (pattern)
        {
            case IConstantPatternOperation constant when IsCaseValue(pattern, caseType) && IntegralRange.ToInt128(constant.Value.ConstantValue) is { } point:
                return [(point, point)];
            case IRelationalPatternOperation relational when IsCaseValue(pattern, caseType) && IntegralRange.ToInt128(relational.Value.ConstantValue) is { } bound:
                return relational.OperatorKind switch
                {
                    BinaryOperatorKind.LessThan => IntegralRange.Clamp(range.Low, bound - 1, range),
                    BinaryOperatorKind.LessThanOrEqual => IntegralRange.Clamp(range.Low, bound, range),
                    BinaryOperatorKind.GreaterThan => IntegralRange.Clamp(bound + 1, range.High, range),
                    BinaryOperatorKind.GreaterThanOrEqual => IntegralRange.Clamp(bound, range.High, range),
                    _ => IntegralRange.Empty
                };
            case INegatedPatternOperation negated:
                return IntegralRange.Complement(AcceptedValues(negated.Pattern, caseType, range), range);
            case IBinaryPatternOperation { OperatorKind: BinaryOperatorKind.Or } binary:
                return IntegralRange.Union(AcceptedValues(binary.LeftPattern, caseType, range), AcceptedValues(binary.RightPattern, caseType, range));
            case IBinaryPatternOperation { OperatorKind: BinaryOperatorKind.And } binary:
                return IntegralRange.Complement(IntegralRange.Union(
                    IntegralRange.Complement(AcceptedValues(binary.LeftPattern, caseType, range), range),
                    IntegralRange.Complement(AcceptedValues(binary.RightPattern, caseType, range), range)), range);
            default:
                return IntegralRange.Empty;
        }
    }

    private static bool IsCaseValue(IPatternOperation pattern, ITypeSymbol caseType) =>
        SymbolEqualityComparer.Default.Equals(pattern.NarrowedType, caseType);

    /// <summary>A pattern that matches every value: a discard or a <c>var</c> declaration.</summary>
    private static bool IsCatchAll(IPatternOperation pattern) =>
        pattern is IDiscardPatternOperation or IDeclarationPatternOperation { MatchedType: null };

    /// <summary>A property or positional pattern that tests the input as a whole (<c>{ }</c>), not one case type.</summary>
    private static bool IsWholeInput(IRecursivePatternOperation recursive) =>
        recursive.MatchedType is null || SymbolEqualityComparer.Default.Equals(recursive.MatchedType, recursive.InputType);

    private static bool IsNullConstant(IConstantPatternOperation constant) =>
        constant.Value.ConstantValue is { HasValue: true, Value: null };

    /// <summary>Whether a value of <paramref name="caseType" /> matches a type test for <paramref name="handled" />.</summary>
    private static bool IsHandledBy(ITypeSymbol caseType, ITypeSymbol handled)
    {
        if (handled.SpecialType == SpecialType.System_Object)
        {
            return true;
        }

        for (var current = caseType; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, handled))
            {
                return true;
            }
        }

        return caseType.AllInterfaces.Any(implemented => SymbolEqualityComparer.Default.Equals(implemented, handled));
    }

    /// <summary>
    ///     Whether no value of <paramref name="caseType" /> can be a <paramref name="other" />:
    ///     neither derives from the other, and an interface on either side cannot be implemented
    ///     by a subclass of the other (that side is sealed or a value type).
    /// </summary>
    private static bool AreDisjoint(ITypeSymbol caseType, ITypeSymbol other)
    {
        if (caseType.TypeKind is TypeKind.TypeParameter or TypeKind.Error || other.TypeKind is TypeKind.TypeParameter or TypeKind.Error ||
            IsHandledBy(caseType, other) || IsHandledBy(other, caseType))
        {
            return false;
        }

        static bool IsClosed(ITypeSymbol type) => type.IsSealed || type.IsValueType;
        return (caseType.TypeKind, other.TypeKind) switch
        {
            (TypeKind.Interface, TypeKind.Interface) => false,
            (TypeKind.Interface, _) => IsClosed(other),
            (_, TypeKind.Interface) => IsClosed(caseType),
            _ => true
        };
    }
}

/// <summary>
///     Sets of integral values as sorted, disjoint, inclusive intervals over <see cref="Int128" />,
///     wide enough for every integral type, <c>char</c> and <c>bool</c> (false 0, true 1).
/// </summary>
internal static class IntegralRange
{
    public static List<(Int128 Low, Int128 High)> Empty => [];

    public static (Int128 Low, Int128 High)? For(ITypeSymbol type) => type.SpecialType switch
    {
        SpecialType.System_Boolean => (0, 1),
        SpecialType.System_SByte => (sbyte.MinValue, sbyte.MaxValue),
        SpecialType.System_Byte => (byte.MinValue, byte.MaxValue),
        SpecialType.System_Int16 => (short.MinValue, short.MaxValue),
        SpecialType.System_UInt16 => (ushort.MinValue, ushort.MaxValue),
        SpecialType.System_Char => (char.MinValue, char.MaxValue),
        SpecialType.System_Int32 => (int.MinValue, int.MaxValue),
        SpecialType.System_UInt32 => (uint.MinValue, uint.MaxValue),
        SpecialType.System_Int64 => (long.MinValue, long.MaxValue),
        SpecialType.System_UInt64 => (ulong.MinValue, ulong.MaxValue),
        _ => null
    };

    public static Int128? ToInt128(Optional<object?> constant) => constant is { HasValue: true, Value: { } value }
        ? value switch
        {
            bool flag => flag ? 1 : 0,
            sbyte number => number,
            byte number => number,
            short number => number,
            ushort number => number,
            char character => character,
            int number => number,
            uint number => number,
            long number => number,
            ulong number => number,
            _ => null
        }
        : null;

    public static List<(Int128 Low, Int128 High)> Clamp(Int128 low, Int128 high, (Int128 Low, Int128 High) range)
    {
        low = Int128.Max(low, range.Low);
        high = Int128.Min(high, range.High);
        return low > high ? [] : [(low, high)];
    }

    public static List<(Int128 Low, Int128 High)> Union(List<(Int128 Low, Int128 High)> left, List<(Int128 Low, Int128 High)> right)
    {
        var all = left.Concat(right).OrderBy(interval => interval.Low).ToList();
        var merged = new List<(Int128 Low, Int128 High)>();
        foreach (var interval in all)
        {
            if (merged.Count > 0 && interval.Low <= merged[^1].High + 1)
            {
                merged[^1] = (merged[^1].Low, Int128.Max(merged[^1].High, interval.High));
            }
            else
            {
                merged.Add(interval);
            }
        }

        return merged;
    }

    public static List<(Int128 Low, Int128 High)> Complement(List<(Int128 Low, Int128 High)> set, (Int128 Low, Int128 High) range)
    {
        var result = new List<(Int128 Low, Int128 High)>();
        var next = range.Low;
        foreach (var (low, high) in Union(set, []))
        {
            if (low > next)
            {
                result.Add((next, Int128.Min(low - 1, range.High)));
            }

            next = Int128.Max(next, high + 1);
        }

        if (next <= range.High)
        {
            result.Add((next, range.High));
        }

        return result;
    }

    public static bool Covers(List<(Int128 Low, Int128 High)> set, (Int128 Low, Int128 High) range) =>
        Complement(set, range).Count == 0;
}
