using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Depscan;

internal static class DispatchResolver
{
    internal sealed class SourceIndex
    {
        /// <summary>
        ///     Candidates one lookup keeps, instantiated types first. The call-graph walker takes
        ///     at most 16 of them per call site.
        /// </summary>
        private const int MaxCandidatesPerLookup = 32;

        /// <summary>Resolution failures logged individually under --debug before the log samples them.</summary>
        private const int LoggedFailureLimit = 10;

        private static readonly AsyncLocal<Action<INamedTypeSymbol, IMethodSymbol>?> BeforeInterfaceResolution = new();

        private readonly List<ConcreteTypeEntry> _concreteTypes;
        private readonly bool _hasInstantiationEvidence;
        private readonly Dictionary<ISymbol, List<ConcreteTypeEntry>> _typesByInterface;
        private readonly Dictionary<ISymbol, List<ConcreteTypeEntry>> _typesByBaseType;
        private readonly ConcurrentDictionary<DispatchLookupKey, DispatchLookup> _lookups = new(new DispatchLookupKeyComparer());
        private int _loggedFailures;

        private SourceIndex(List<ConcreteTypeEntry> concreteTypes, bool hasInstantiationEvidence,
            Dictionary<ISymbol, List<ConcreteTypeEntry>> typesByInterface, Dictionary<ISymbol, List<ConcreteTypeEntry>> typesByBaseType)
        {
            _concreteTypes = concreteTypes;
            _hasInstantiationEvidence = hasInstantiationEvidence;
            _typesByInterface = typesByInterface;
            _typesByBaseType = typesByBaseType;
        }

        /// <summary>
        ///     Build the index for <paramref name="compilation" />. Collecting instantiation
        ///     evidence binds every member holding an object creation, a full binding pass over the
        ///     compilation, so the per-tree scan runs on <paramref name="workerCount" /> dedicated
        ///     large-stack workers; per-tree results are unioned, which is order-independent.
        /// </summary>
        public static SourceIndex Create(Compilation compilation, int workerCount = 1)
        {
            var allTypes = new List<INamedTypeSymbol>();
            CollectTypes(compilation.Assembly.GlobalNamespace, allTypes);
            var concreteTypes = allTypes
                .Where(type => type.TypeKind is TypeKind.Class or TypeKind.Struct)
                .Where(type => !type.IsAbstract)
                .Where(type => type.Locations.Any(location => location.IsInSource) || SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, compilation.Assembly))
                .ToList();

            var trees = compilation.SyntaxTrees.ToList();
            var instantiatedPerTree = new HashSet<INamedTypeSymbol>?[trees.Count];
            DedicatedStack.ForEach("Dosai dispatch index", workerCount, trees.Count,
                index => instantiatedPerTree[index] = CollectInstantiatedTypes(compilation, trees[index]));
            var instantiated = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            foreach (var treeTypes in instantiatedPerTree)
            {
                if (treeTypes is not null)
                {
                    instantiated.UnionWith(treeTypes);
                }
            }

            var hasInstantiationEvidence = instantiated.Count > 0;
            var entries = new List<ConcreteTypeEntry>(concreteTypes.Count);
            foreach (var type in concreteTypes)
            {
                // Instantiation evidence is symbol-exact: every type below resolved through
                // this one compilation, so the created-type symbol (and, for a constructed
                // generic, its original definition) identifies exactly the types this
                // compilation instantiated. The string aliases this replaces also matched any
                // same-named type in another namespace, promoting unrelated types to RTA
                // evidence (an "Own.Task" marked System.Threading.Tasks.Task instantiated).
                var isInstantiated = hasInstantiationEvidence
                                     && (instantiated.Contains(type) || (type.IsGenericType && instantiated.Contains(type.OriginalDefinition)));
                entries.Add(new ConcreteTypeEntry(type, isInstantiated));
            }

            // Candidate scanning used to walk every concrete type for every call site, which made
            // symbol analysis super-linear in the file count (issue #65). The buckets hold exactly
            // the types the per-type receiver filter accepted - implementers of an interface's
            // original definition, a class's original definition and everything deriving from it -
            // each once and in type order, so a lookup reads its bucket instead.
            var typesByInterface = new Dictionary<ISymbol, List<ConcreteTypeEntry>>(SymbolEqualityComparer.Default);
            var typesByBaseType = new Dictionary<ISymbol, List<ConcreteTypeEntry>>(SymbolEqualityComparer.Default);
            var implementedDefinitions = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            foreach (var entry in entries)
            {
                // A type implementing two constructions of one generic interface (IHandler<int> and
                // IHandler<string>) shares their original definition; bucketing it per construction
                // made every lookup through that interface visit the type twice and emit the same
                // candidate edge twice.
                implementedDefinitions.Clear();
                foreach (var implemented in entry.Type.AllInterfaces)
                {
                    if (implementedDefinitions.Add(implemented.OriginalDefinition))
                    {
                        AddToBucket(typesByInterface, implemented.OriginalDefinition, entry);
                    }
                }

                AddToBucket(typesByBaseType, entry.Type.OriginalDefinition, entry);
                for (var current = entry.Type.BaseType; current is not null; current = current.BaseType)
                {
                    AddToBucket(typesByBaseType, current.OriginalDefinition, entry);
                }
            }

            return new SourceIndex(entries, hasInstantiationEvidence, typesByInterface, typesByBaseType);
        }

        /// <summary>
        ///     Test hook: <paramref name="probe" /> runs before every interface-member resolution in
        ///     the current execution context (and the analysis threads it starts), so a test can make
        ///     a resolution throw and exercise the containment path.
        /// </summary>
        internal static IDisposable OverrideBeforeInterfaceResolution(Action<INamedTypeSymbol, IMethodSymbol> probe)
        {
            var previous = BeforeInterfaceResolution.Value;
            BeforeInterfaceResolution.Value = probe;
            return new Restore(() => BeforeInterfaceResolution.Value = previous);
        }

        public IEnumerable<IMethodSymbol> FindDispatchCandidates(IMethodSymbol targetMethod, ITypeSymbol? receiverType = null)
            => FindRankedDispatchCandidates(targetMethod, receiverType).Select(candidate => candidate.Method);

        /// <summary>
        ///     Dispatch candidates with per-candidate confidence. A sealed or struct receiver
        ///     devirtualizes to the exact implementation; otherwise candidates are ranked with
        ///     RTA-instantiated types before uninstantiated CHA candidates.
        /// </summary>
        public IEnumerable<(IMethodSymbol Method, string Confidence)> FindRankedDispatchCandidates(IMethodSymbol targetMethod, ITypeSymbol? receiverType = null)
            => Lookup(targetMethod, receiverType).Candidates.Select(candidate => (candidate.Method, candidate.Confidence));

        /// <summary>
        ///     The ranked candidates for a call of <paramref name="targetMethod" /> on
        ///     <paramref name="receiverType" />, with any implementing member Roslyn failed to
        ///     resolve. Memoized per (target, receiver) symbol pair, so repeated call sites of the
        ///     same method pay for the hierarchy walk once (issue #65). The result is shared
        ///     between callers and threads and must not be mutated.
        /// </summary>
        public DispatchLookup Lookup(IMethodSymbol targetMethod, ITypeSymbol? receiverType = null)
        {
            if (targetMethod.IsStatic || targetMethod.MethodKind != MethodKind.Ordinary || targetMethod.ContainingType is null)
            {
                return DispatchLookup.Empty;
            }

            return _lookups.GetOrAdd(new DispatchLookupKey(targetMethod, receiverType), static (key, index) => index.ComputeLookup(key.Target, key.Receiver), this);
        }

        private DispatchLookup ComputeLookup(IMethodSymbol targetMethod, ITypeSymbol? receiverType)
        {
            var normalizedTarget = targetMethod.OriginalDefinition;
            DispatchResolutionFailure? failure = null;

            // A sealed (or struct) receiver has exactly one possible implementation, resolve it
            // directly instead of emitting a candidate set.
            if (receiverType is INamedTypeSymbol namedReceiver && (namedReceiver.IsSealed || namedReceiver.IsValueType || namedReceiver.TypeKind == TypeKind.Struct))
            {
                var exact = ResolveSourceCandidate(namedReceiver, normalizedTarget, targetMethod, ref failure);
                return exact is not null && !SymbolEqualityComparer.Default.Equals(exact.OriginalDefinition, normalizedTarget) && exact.Locations.Any(location => location.IsInSource)
                    ? new DispatchLookup([new DispatchCandidate(exact, "exact")], failure)
                    : new DispatchLookup([], failure);
            }

            var requireInstantiated = _hasInstantiationEvidence;
            var rtaCandidates = new List<DispatchCandidate>();
            var chaCandidates = new List<DispatchCandidate>();
            foreach (var entry in CandidateTypesFor(receiverType))
            {
                var candidate = ResolveSourceCandidate(entry.Type, normalizedTarget, targetMethod, ref failure);
                if (candidate is null || SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, normalizedTarget))
                {
                    continue;
                }

                if (!candidate.Locations.Any(location => location.IsInSource))
                {
                    continue;
                }

                // RTA evidence: the candidate type was instantiated in this compilation.
                if (requireInstantiated && entry.IsInstantiated)
                {
                    rtaCandidates.Add(new DispatchCandidate(candidate, "rta-candidate"));
                }
                else if (!requireInstantiated || MayBeFrameworkInstantiated(entry.Type, receiverType))
                {
                    chaCandidates.Add(new DispatchCandidate(candidate, "cha-candidate"));
                }
            }

            // Instantiated types outrank pure CHA candidates before the cap is applied. Exact
            // resolution stays reserved for sealed/struct static receiver types, RTA singleton
            // promotions would inflate candidate edges to direct evidence.
            var result = new List<DispatchCandidate>(Math.Min(rtaCandidates.Count + chaCandidates.Count, MaxCandidatesPerLookup));
            result.AddRange(rtaCandidates.Take(MaxCandidatesPerLookup));
            result.AddRange(chaCandidates.Take(MaxCandidatesPerLookup - result.Count));
            return new DispatchLookup(result, failure);
        }

        /// <summary>
        ///     The concrete types a call on <paramref name="receiverType" /> could dispatch to:
        ///     implementers of the receiver interface, or the receiver type and its derived types.
        ///     A non-named receiver (null, type parameter, array) keeps the full scan.
        /// </summary>
        private IEnumerable<ConcreteTypeEntry> CandidateTypesFor(ITypeSymbol? receiverType)
        {
            if (receiverType is INamedTypeSymbol named)
            {
                if (named.TypeKind == TypeKind.Interface)
                {
                    return _typesByInterface.TryGetValue(named.OriginalDefinition, out var implementers) ? implementers : [];
                }

                return _typesByBaseType.TryGetValue(named.OriginalDefinition, out var derived) ? derived : [];
            }

            return _concreteTypes;
        }

        private static bool MayBeFrameworkInstantiated(INamedTypeSymbol type, ITypeSymbol? receiverType) =>
            receiverType is not INamedTypeSymbol receiverNamed || receiverNamed.TypeKind == TypeKind.Interface || InheritsFrom(type, receiverNamed);

        private IMethodSymbol? ResolveSourceCandidate(INamedTypeSymbol type, IMethodSymbol normalizedTarget, IMethodSymbol originalTarget, ref DispatchResolutionFailure? failure)
        {
            if (normalizedTarget.ContainingType?.TypeKind == TypeKind.Interface)
            {
                try
                {
                    BeforeInterfaceResolution.Value?.Invoke(type, originalTarget);

                    // FindImplementationForInterfaceMember takes the member as its interface
                    // declares it. A call to a generic method binds the method constructed with the
                    // call's type arguments (IBuilder<Thing>.Join<TEntity>), and Roslyn constructs
                    // the member again while matching the implementation, which throws on an
                    // already-constructed method - on a generic interface, where the definition
                    // lookup below misses and this fallback runs, that aborted the scan (issue #64).
                    // ConstructedFrom is the same member of the same constructed interface before
                    // the call's type arguments, which is the form the API expects.
                    return type.FindImplementationForInterfaceMember(normalizedTarget) as IMethodSymbol
                        ?? type.FindImplementationForInterfaceMember(originalTarget.ConstructedFrom) as IMethodSymbol;
                }
                catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
                {
                    // Containment for shapes Roslyn still fails on: this one candidate is lost,
                    // the call keeps its direct edge and the other candidates, and the walker
                    // reports the call site in the slice diagnostics.
                    failure = failure is null
                        ? new DispatchResolutionFailure(1, type.ToDisplayString(), originalTarget.ToDisplayString(), exception.GetType().Name)
                        : failure with { Count = failure.Count + 1 };
                    var logged = Interlocked.Increment(ref _loggedFailures);
                    if (DebugLog.Enabled && (logged <= LoggedFailureLimit || logged % 1000 == 0))
                    {
                        DebugLog.Log(
                            $"dispatch resolution failure #{logged} on '{type.ToDisplayString()}'"
                            + $" for '{originalTarget.ToDisplayString()}':"
                            + $" {exception.GetType().Name}: {exception.Message}");
                    }

                    return null;
                }
            }

            if (!InheritsFrom(type, normalizedTarget.ContainingType))
            {
                return null;
            }

            return type.GetMembers(normalizedTarget.Name)
                .OfType<IMethodSymbol>()
                .FirstOrDefault(method => method.IsOverride && Overrides(method, normalizedTarget));
        }

        private static HashSet<INamedTypeSymbol>? CollectInstantiatedTypes(Compilation compilation, SyntaxTree syntaxTree)
        {
            HashSet<INamedTypeSymbol>? types = null;
            var semanticModel = compilation.GetSemanticModel(syntaxTree);
            foreach (var objectCreationNode in syntaxTree.GetRoot().DescendantNodes().Where(IsObjectCreationSyntax))
            {
                // GetTypeInfo skips the IOperation factory, but it still binds the creation's
                // whole enclosing statement - for a creation heading a fluent chain, the entire
                // chain - and binding a chain is super-linear in its length (a 400,000-call chain
                // bound for about 14 minutes here). Members the depth guard keeps off the
                // operation factory are kept out of this scan too: the call-graph walker never
                // analyzes them, so their creations were never evidence.
                if (!OperationDepthGuard.IsSafe(objectCreationNode))
                {
                    continue;
                }

                // The created type is known even when its constructor call fails to bind (an
                // argument of an unresolved type), where the operation form was an
                // IInvalidOperation: `new T(unresolved)` still instantiates T.
                if (semanticModel.GetTypeInfo(objectCreationNode).Type is INamedTypeSymbol type)
                {
                    AddInstantiatedTypes(types ??= new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default), type);
                }
            }

            return types;
        }

        private static void CollectTypes(INamespaceSymbol namespaceSymbol, List<INamedTypeSymbol> types)
        {
            foreach (var type in namespaceSymbol.GetTypeMembers()) CollectTypes(type, types);
            foreach (var childNamespace in namespaceSymbol.GetNamespaceMembers()) CollectTypes(childNamespace, types);
        }

        private static void CollectTypes(INamedTypeSymbol type, List<INamedTypeSymbol> types)
        {
            types.Add(type);
            foreach (var nestedType in type.GetTypeMembers()) CollectTypes(nestedType, types);
        }

        private static bool IsObjectCreationSyntax(SyntaxNode node) =>
            node is Microsoft.CodeAnalysis.CSharp.Syntax.ObjectCreationExpressionSyntax
                or Microsoft.CodeAnalysis.CSharp.Syntax.ImplicitObjectCreationExpressionSyntax
                or Microsoft.CodeAnalysis.VisualBasic.Syntax.ObjectCreationExpressionSyntax;

        private static bool InheritsFrom(INamedTypeSymbol? candidate, INamedTypeSymbol? baseType)
        {
            for (var current = candidate?.BaseType; current is not null; current = current.BaseType)
            {
                if (SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, baseType?.OriginalDefinition)) return true;
            }
            return false;
        }

        private static bool Overrides(IMethodSymbol method, IMethodSymbol targetMethod)
        {
            for (var current = method.OverriddenMethod; current is not null; current = current.OverriddenMethod)
            {
                if (SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, targetMethod)) return true;
            }
            return false;
        }

        private static void AddToBucket(Dictionary<ISymbol, List<ConcreteTypeEntry>> buckets, ISymbol key, ConcreteTypeEntry entry)
        {
            if (!buckets.TryGetValue(key, out var bucket))
            {
                bucket = [];
                buckets[key] = bucket;
            }

            bucket.Add(entry);
        }

        private static void AddInstantiatedTypes(HashSet<INamedTypeSymbol> types, INamedTypeSymbol type)
        {
            types.Add(type);
            if (!SymbolEqualityComparer.Default.Equals(type, type.OriginalDefinition))
            {
                types.Add(type.OriginalDefinition);
            }
        }

        private readonly record struct ConcreteTypeEntry(INamedTypeSymbol Type, bool IsInstantiated);

        private readonly record struct DispatchLookupKey(IMethodSymbol Target, ITypeSymbol? Receiver);

        private sealed class DispatchLookupKeyComparer : IEqualityComparer<DispatchLookupKey>
        {
            public bool Equals(DispatchLookupKey x, DispatchLookupKey y) =>
                SymbolEqualityComparer.Default.Equals(x.Target, y.Target) &&
                SymbolEqualityComparer.Default.Equals(x.Receiver, y.Receiver);

            public int GetHashCode(DispatchLookupKey key) =>
                HashCode.Combine(SymbolEqualityComparer.Default.GetHashCode(key.Target), key.Receiver is null ? 0 : SymbolEqualityComparer.Default.GetHashCode(key.Receiver));
        }

        private sealed class Restore(Action restore) : IDisposable
        {
            public void Dispose() => restore();
        }
    }

    internal readonly record struct DispatchCandidate(IMethodSymbol Method, string Confidence);

    /// <summary>
    ///     An implementing-member resolution Roslyn threw on: <see cref="Count" /> candidate types
    ///     failed for one lookup, the first of them <see cref="TypeName" />.
    /// </summary>
    internal sealed record DispatchResolutionFailure(int Count, string TypeName, string MemberName, string ExceptionType);

    /// <summary>Ranked candidates for one (target, receiver) pair, and the resolution failure that thinned them, if any.</summary>
    internal sealed record DispatchLookup(IReadOnlyList<DispatchCandidate> Candidates, DispatchResolutionFailure? Failure)
    {
        public static readonly DispatchLookup Empty = new([], null);
    }

    internal sealed class AssemblyIndex
    {
        // Methods by name, each list in the input order: a lookup visits only the methods that
        // can match, where it used to scan the assembly's every method per virtual call site.
        private readonly Dictionary<string, List<Method>> _methodsByName;
        private readonly HashSet<string> _instantiatedTypes;

        private AssemblyIndex(Dictionary<string, List<Method>> methodsByName, HashSet<string> instantiatedTypes)
        {
            _methodsByName = methodsByName;
            _instantiatedTypes = instantiatedTypes;
        }

        public static AssemblyIndex Create(IEnumerable<Method> methods, IEnumerable<string> instantiatedTypes)
        {
            var methodsByName = new Dictionary<string, List<Method>>(StringComparer.Ordinal);
            foreach (var method in methods)
            {
                if (string.IsNullOrWhiteSpace(method.AssemblySignature) || method.Name is null)
                {
                    continue;
                }

                if (!methodsByName.TryGetValue(method.Name, out var named))
                {
                    named = [];
                    methodsByName.Add(method.Name, named);
                }

                named.Add(method);
            }

            return new AssemblyIndex(methodsByName, NormalizeTypeSet(instantiatedTypes));
        }

        public IEnumerable<Method> FindDispatchCandidates(string targetName, string targetContainingType, int targetParameterCount)
        {
            if (targetName is null || !_methodsByName.TryGetValue(targetName, out var named))
            {
                yield break;
            }

            var targetSimpleType = SimpleName(targetContainingType);
            foreach (var method in named)
            {

                if (targetParameterCount >= 0 && method.Parameters is not null && method.Parameters.Count != targetParameterCount)
                {
                    continue;
                }

                var className = method.ClassName ?? string.Empty;
                if (_instantiatedTypes.Count > 0 && !IsInstantiated(className))
                {
                    continue;
                }

                if (TypeMatches(method, targetContainingType, targetSimpleType))
                {
                    yield return method;
                }
            }
        }

        private bool IsInstantiated(string typeName) => TypeAliases(typeName).Any(alias => _instantiatedTypes.Contains(alias));

        private static bool TypeMatches(Method method, string targetContainingType, string targetSimpleType)
        {
            var className = method.ClassName ?? string.Empty;
            if (TypeAliases(className).Any(alias => alias.Equals(targetContainingType, StringComparison.Ordinal) || alias.Equals(targetSimpleType, StringComparison.Ordinal))) return true;
            if (TypeAliases(method.BaseType ?? string.Empty).Any(alias => alias.Equals(targetContainingType, StringComparison.Ordinal) || alias.Equals(targetSimpleType, StringComparison.Ordinal))) return true;
            return method.ImplementedInterfaces?.SelectMany(TypeAliases).Any(alias => alias.Equals(targetContainingType, StringComparison.Ordinal) || alias.Equals(targetSimpleType, StringComparison.Ordinal)) == true;
        }

        private static HashSet<string> NormalizeTypeSet(IEnumerable<string> types)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (var type in types)
            {
                foreach (var alias in TypeAliases(type)) result.Add(alias);
            }
            return result;
        }

        private static IEnumerable<string> TypeAliases(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName)) yield break;
            var normalized = typeName.Replace('+', '.').Replace('/', '.');
            var tick = normalized.IndexOf('`', StringComparison.Ordinal);
            if (tick >= 0) normalized = normalized[..tick];
            yield return normalized;
            yield return SimpleName(normalized);
        }

        private static string SimpleName(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName)) return string.Empty;
            var normalized = typeName.Replace('+', '.').Replace('/', '.');
            var dot = normalized.LastIndexOf('.');
            var simple = dot >= 0 ? normalized[(dot + 1)..] : normalized;
            var tick = simple.IndexOf('`', StringComparison.Ordinal);
            return tick >= 0 ? simple[..tick] : simple;
        }
    }
}