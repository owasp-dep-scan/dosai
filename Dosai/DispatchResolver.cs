using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Depscan;

internal static class DispatchResolver
{
    internal sealed class SourceIndex
    {
        /// <summary>Maximum candidates one lookup materializes, matching the previous lazy cap.</summary>
        private const int MaxCandidatesPerLookup = 32;

        private readonly List<ConcreteTypeEntry> _concreteTypes;
        private readonly bool _hasInstantiationEvidence;
        private readonly Dictionary<ISymbol, List<ConcreteTypeEntry>> _typesByInterface;
        private readonly Dictionary<ISymbol, List<ConcreteTypeEntry>> _typesByBaseType;
        private readonly ConcurrentDictionary<DispatchLookupKey, IReadOnlyList<DispatchCandidate>> _candidateCache = new(new DispatchLookupKeyComparer());

        private SourceIndex(List<ConcreteTypeEntry> concreteTypes, bool hasInstantiationEvidence,
            Dictionary<ISymbol, List<ConcreteTypeEntry>> typesByInterface, Dictionary<ISymbol, List<ConcreteTypeEntry>> typesByBaseType)
        {
            _concreteTypes = concreteTypes;
            _hasInstantiationEvidence = hasInstantiationEvidence;
            _typesByInterface = typesByInterface;
            _typesByBaseType = typesByBaseType;
        }

        /// <summary>
        ///     Call sites whose interface-member resolution Roslyn failed on (issue #64): the
        ///     candidate is abandoned instead of the whole scan, and the count surfaces through
        ///     the slice diagnostics so the missing edges stay visible.
        /// </summary>
        internal int AbandonedResolutions => Volatile.Read(ref abandonedResolutions);

        private int abandonedResolutions;

        public static SourceIndex Create(Compilation compilation)
        {
            var allTypes = new List<INamedTypeSymbol>();
            CollectTypes(compilation.Assembly.GlobalNamespace, allTypes);
            var concreteTypes = allTypes
                .Where(type => type.TypeKind is TypeKind.Class or TypeKind.Struct)
                .Where(type => !type.IsAbstract)
                .Where(type => type.Locations.Any(location => location.IsInSource) || SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, compilation.Assembly))
                .ToList();
            var instantiated = new HashSet<string>(StringComparer.Ordinal);
            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                var semanticModel = compilation.GetSemanticModel(syntaxTree);
                var root = syntaxTree.GetRoot();
                foreach (var objectCreationNode in root.DescendantNodes().Where(IsObjectCreationSyntax))
                {
                    if (OperationDepthGuard.GetOperation(semanticModel, objectCreationNode) is not IObjectCreationOperation objectCreation)
                    {
                        continue;
                    }

                    if (objectCreation.Type is INamedTypeSymbol type)
                    {
                        AddTypeKeys(instantiated, type);
                    }
                }
            }

            var hasInstantiationEvidence = instantiated.Count > 0;
            var entries = new List<ConcreteTypeEntry>(concreteTypes.Count);
            foreach (var type in concreteTypes)
            {
                // Resolved once here so a lookup never re-renders display strings per candidate:
                // with thousands of call sites this was a large share of symbol-analysis time.
                var isInstantiated = hasInstantiationEvidence && TypeKeys(type).Any(instantiated.Contains);
                entries.Add(new ConcreteTypeEntry(type, isInstantiated));
            }

            // Dispatch candidate scanning used to walk every concrete type for every call site,
            // which made symbol analysis super-linear in the file count (issue #65). Indexing the
            // same membership the per-call filters computed - interface original definitions and
            // base-type original definitions - turns each lookup into a bucket read.
            var typesByInterface = new Dictionary<ISymbol, List<ConcreteTypeEntry>>(SymbolEqualityComparer.Default);
            var typesByBaseType = new Dictionary<ISymbol, List<ConcreteTypeEntry>>(SymbolEqualityComparer.Default);
            foreach (var entry in entries)
            {
                foreach (var implemented in entry.Type.AllInterfaces)
                {
                    AddToBucket(typesByInterface, implemented.OriginalDefinition, entry);
                }

                AddToBucket(typesByBaseType, entry.Type.OriginalDefinition, entry);
                for (var current = entry.Type.BaseType; current is not null; current = current.BaseType)
                {
                    AddToBucket(typesByBaseType, current.OriginalDefinition, entry);
                }
            }

            return new SourceIndex(entries, hasInstantiationEvidence, typesByInterface, typesByBaseType);
        }

        public IEnumerable<IMethodSymbol> FindDispatchCandidates(IMethodSymbol targetMethod, ITypeSymbol? receiverType = null)
            => FindRankedDispatchCandidates(targetMethod, receiverType).Select(candidate => candidate.Method);

        /// <summary>
        ///     Dispatch candidates with per-candidate confidence. A sealed or struct receiver
        ///     devirtualizes to the exact implementation; otherwise candidates are ranked with
        ///     RTA-instantiated types before uninstantiated CHA candidates. Results are memoized
        ///     per (target, receiver) symbol pair, so repeated call sites of the same virtual or
        ///     interface method pay for the hierarchy walk once (issue #65); the returned list is
        ///     shared between callers and must not be mutated.
        /// </summary>
        public IEnumerable<(IMethodSymbol Method, string Confidence)> FindRankedDispatchCandidates(IMethodSymbol targetMethod, ITypeSymbol? receiverType = null)
        {
            if (targetMethod.IsStatic || targetMethod.MethodKind != MethodKind.Ordinary || targetMethod.ContainingType is null)
            {
                return [];
            }

            var key = new DispatchLookupKey(targetMethod, receiverType);
            if (_candidateCache.TryGetValue(key, out var cached))
            {
                return FromCache(cached);
            }

            var computed = ComputeRankedCandidates(targetMethod, receiverType);
            _candidateCache.TryAdd(key, computed);
            return FromCache(computed);

            static IEnumerable<(IMethodSymbol Method, string Confidence)> FromCache(IReadOnlyList<DispatchCandidate> candidates)
                => candidates.Select(candidate => (candidate.Method, candidate.Confidence));
        }

        private IReadOnlyList<DispatchCandidate> ComputeRankedCandidates(IMethodSymbol targetMethod, ITypeSymbol? receiverType)
        {
            var normalizedTarget = targetMethod.OriginalDefinition;

            // A sealed (or struct) receiver has exactly one possible implementation, resolve it
            // directly instead of emitting a candidate set.
            if (receiverType is INamedTypeSymbol namedReceiver && (namedReceiver.IsSealed || namedReceiver.IsValueType || namedReceiver.TypeKind == TypeKind.Struct))
            {
                var exact = ResolveSourceCandidate(namedReceiver, normalizedTarget, targetMethod);
                return exact is not null && !SymbolEqualityComparer.Default.Equals(exact.OriginalDefinition, normalizedTarget) && exact.Locations.Any(location => location.IsInSource)
                    ? [new DispatchCandidate(exact, "exact")]
                    : [];
            }

            var requireInstantiated = _hasInstantiationEvidence;
            var rtaCandidates = new List<DispatchCandidate>();
            var chaCandidates = new List<DispatchCandidate>();
            foreach (var entry in CandidateTypesFor(receiverType))
            {
                var candidate = ResolveSourceCandidate(entry.Type, normalizedTarget, targetMethod);
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
            var result = new List<DispatchCandidate>(rtaCandidates.Count + chaCandidates.Count);
            result.AddRange(rtaCandidates);
            result.AddRange(chaCandidates);
            if (result.Count > MaxCandidatesPerLookup)
            {
                result.RemoveRange(MaxCandidatesPerLookup, result.Count - MaxCandidatesPerLookup);
            }

            return result;
        }

        /// <summary>
        ///     The concrete types a call on <paramref name="receiverType" /> could dispatch to:
        ///     implementers of the receiver interface, or the receiver type and its derived types.
        ///     A non-named receiver (null, type parameter, array) keeps the full scan - the
        ///     bucketed sets must stay exactly what the previous per-type filters computed.
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

        private IMethodSymbol? ResolveSourceCandidate(INamedTypeSymbol type, IMethodSymbol normalizedTarget, IMethodSymbol originalTarget)
        {
            if (normalizedTarget.ContainingType?.TypeKind == TypeKind.Interface)
            {
                // Roslyn throws InvalidOperationException out of FindImplementationForInterfaceMember
                // for some generic-method-on-generic-interface shapes (a constructed method symbol
                // reaching Construct with mismatched arity), and nothing up the stack recovers, so
                // one such call site aborted the whole scan with no output (issue #64). The
                // candidate is abandoned; FindRankedDispatchCandidates skips it and carries on.
                try
                {
                    return type.FindImplementationForInterfaceMember(normalizedTarget) as IMethodSymbol
                        ?? type.FindImplementationForInterfaceMember(originalTarget) as IMethodSymbol;
                }
                catch (Exception failure) when (failure is not OperationCanceledException and not OutOfMemoryException)
                {
                    var seen = Interlocked.Increment(ref abandonedResolutions);
                    if (DebugLog.Enabled && (seen <= 10 || seen % 1000 == 0))
                    {
                        DebugLog.Log(
                            $"dispatch resolution #{seen} failed on '{type.ToDisplayString()}'"
                            + $" for '{normalizedTarget.ToDisplayString()}':"
                            + $" {failure.GetType().Name}: {failure.Message}");
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

        private static void AddTypeKeys(HashSet<string> keys, INamedTypeSymbol type)
        {
            foreach (var key in TypeKeys(type)) keys.Add(key);
        }

        private static IEnumerable<string> TypeKeys(INamedTypeSymbol type)
        {
            yield return type.Name;
            yield return type.MetadataName;
            yield return type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty, StringComparison.Ordinal);
            yield return type.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty, StringComparison.Ordinal);
        }

        private readonly record struct ConcreteTypeEntry(INamedTypeSymbol Type, bool IsInstantiated);

        internal readonly record struct DispatchCandidate(IMethodSymbol Method, string Confidence);

        private readonly record struct DispatchLookupKey(ISymbol Target, ISymbol? Receiver);

        private sealed class DispatchLookupKeyComparer : IEqualityComparer<DispatchLookupKey>
        {
            public bool Equals(DispatchLookupKey x, DispatchLookupKey y) =>
                SymbolEqualityComparer.Default.Equals(x.Target, y.Target) &&
                (x.Receiver is null ? y.Receiver is null : y.Receiver is not null && SymbolEqualityComparer.Default.Equals(x.Receiver, y.Receiver));

            public int GetHashCode(DispatchLookupKey key)
            {
                var hash = SymbolEqualityComparer.Default.GetHashCode(key.Target);
                return key.Receiver is { } receiver ? HashCode.Combine(hash, SymbolEqualityComparer.Default.GetHashCode(receiver)) : hash;
            }
        }
    }

    internal sealed class AssemblyIndex
    {
        private readonly IReadOnlyList<Method> _methods;
        private readonly HashSet<string> _instantiatedTypes;

        private AssemblyIndex(IReadOnlyList<Method> methods, HashSet<string> instantiatedTypes)
        {
            _methods = methods;
            _instantiatedTypes = instantiatedTypes;
        }

        public static AssemblyIndex Create(IEnumerable<Method> methods, IEnumerable<string> instantiatedTypes) =>
            new(methods.Where(method => !string.IsNullOrWhiteSpace(method.AssemblySignature)).ToList(), NormalizeTypeSet(instantiatedTypes));

        public IEnumerable<Method> FindDispatchCandidates(string targetName, string targetContainingType, int targetParameterCount)
        {
            var targetSimpleType = SimpleName(targetContainingType);
            foreach (var method in _methods)
            {
                if (string.IsNullOrWhiteSpace(method.AssemblySignature) || !string.Equals(method.Name, targetName, StringComparison.Ordinal))
                {
                    continue;
                }

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