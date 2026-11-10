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

/// <summary>A switch over a union value that the compiler accepts but that can misbehave at run time.</summary>
internal sealed record UnionSwitchFinding(string Kind, string Cwe, INamedTypeSymbol Union, IReadOnlyList<ITypeSymbol> MissingCases, SyntaxNode Syntax);

/// <summary>
///     Review checks for switches over C# 15 unions. A union is a struct, so <c>default</c>, an
///     uninitialized field or array element, or a deserialized empty value is a union whose
///     <c>Value</c> is null. The compiler treats a switch expression that names every case type
///     as exhaustive, yet that empty union matches none of the arms and throws
///     <c>SwitchExpressionException</c>; the documented guard is a <c>null</c> arm, which, unlike
///     a discard, still lets the compiler report a case added later. Switch statements get no
///     exhaustiveness check at all, so a case type with no clause and no <c>default</c> is
///     skipped silently.
/// </summary>
internal static class UnionSwitchAnalysis
{
    public const string MissingNullArmKind = "UnionSwitchWithoutNullArm";
    public const string MissingCaseKind = "UnionSwitchMissingCase";
    public const string Category = "union-switch";

    /// <summary>A switch expression over a union with no unguarded arm that matches the empty union.</summary>
    public static UnionSwitchFinding? Inspect(ISwitchExpressionOperation operation)
    {
        if (UnionShapes.AsUnion(operation.Value.Type) is not { } union ||
            operation.Arms.Any(arm => arm.Guard is null && MatchesEmptyUnion(arm.Pattern)))
        {
            return null;
        }

        return new UnionSwitchFinding(MissingNullArmKind, "CWE-248", union, [], operation.Syntax);
    }

    /// <summary>
    ///     A switch statement over a union with no <c>default</c> clause, no catch-all pattern,
    ///     and at least one case type that no unguarded clause handles.
    /// </summary>
    public static UnionSwitchFinding? Inspect(ISwitchOperation operation)
    {
        if (UnionShapes.AsUnion(operation.Value.Type) is not { } union)
        {
            return null;
        }

        var covered = new List<ITypeSymbol>();
        foreach (var clause in operation.Cases.SelectMany(@case => @case.Clauses))
        {
            switch (clause)
            {
                case IDefaultCaseClauseOperation:
                    return null;
                case IPatternCaseClauseOperation { Guard: null, Pattern: var pattern }:
                    if (MatchesEmptyUnion(pattern) && IsCatchAll(pattern))
                    {
                        return null;
                    }

                    CollectCoveredTypes(pattern, covered);
                    break;
            }
        }

        var missing = UnionShapes.CaseTypes(union)
            .Where(caseType => !covered.Any(handled => IsHandledBy(caseType, handled)))
            .ToList();
        return missing.Count == 0 ? null : new UnionSwitchFinding(MissingCaseKind, "CWE-478", union, missing, operation.Syntax);
    }

    /// <summary>
    ///     Whether the pattern matches a union whose <c>Value</c> is null: a discard, a
    ///     <c>var</c> declaration, a <c>null</c> constant, or a negation other than
    ///     <c>not null</c>, through <c>or</c>/<c>and</c> combinations.
    /// </summary>
    internal static bool MatchesEmptyUnion(IPatternOperation pattern) => pattern switch
    {
        IDiscardPatternOperation => true,
        IDeclarationPatternOperation { MatchedType: null } => true,
        IConstantPatternOperation { Value.ConstantValue: { HasValue: true, Value: null } } => true,
        INegatedPatternOperation negated => !IsNullConstant(negated.Pattern),
        IBinaryPatternOperation { OperatorKind: BinaryOperatorKind.Or } binary => MatchesEmptyUnion(binary.LeftPattern) || MatchesEmptyUnion(binary.RightPattern),
        IBinaryPatternOperation { OperatorKind: BinaryOperatorKind.And } binary => MatchesEmptyUnion(binary.LeftPattern) && MatchesEmptyUnion(binary.RightPattern),
        _ => false
    };

    /// <summary>A pattern that matches every value: a discard or a <c>var</c> declaration.</summary>
    private static bool IsCatchAll(IPatternOperation pattern) =>
        pattern is IDiscardPatternOperation or IDeclarationPatternOperation { MatchedType: null };

    private static bool IsNullConstant(IPatternOperation pattern) =>
        pattern is IConstantPatternOperation { Value.ConstantValue: { HasValue: true, Value: null } };

    /// <summary>
    ///     The types a pattern accepts unconditionally: a type or declaration pattern, or a
    ///     positional/property pattern whose every subpattern is a discard or <c>var</c>.
    /// </summary>
    private static void CollectCoveredTypes(IPatternOperation pattern, List<ITypeSymbol> covered)
    {
        switch (pattern)
        {
            case IDeclarationPatternOperation { MatchedType: { } matched }:
                covered.Add(matched);
                break;
            case ITypePatternOperation { MatchedType: var matched }:
                covered.Add(matched);
                break;
            case IRecursivePatternOperation { MatchedType: { } matched } recursive
                when recursive.DeconstructionSubpatterns.All(IsCatchAll) && recursive.PropertySubpatterns.All(property => IsCatchAll(property.Pattern)):
                covered.Add(matched);
                break;
            case IBinaryPatternOperation { OperatorKind: BinaryOperatorKind.Or } binary:
                CollectCoveredTypes(binary.LeftPattern, covered);
                CollectCoveredTypes(binary.RightPattern, covered);
                break;
        }
    }

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
}
