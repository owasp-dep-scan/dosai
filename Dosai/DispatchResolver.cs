using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Depscan;

internal static class DispatchResolver
{
    internal sealed class SourceIndex
    {
        private readonly List<INamedTypeSymbol> _concreteTypes;
        private readonly HashSet<string> _instantiatedTypeKeys;

        private SourceIndex(List<INamedTypeSymbol> concreteTypes, HashSet<string> instantiatedTypeKeys)
        {
            _concreteTypes = concreteTypes;
            _instantiatedTypeKeys = instantiatedTypeKeys;
        }

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
                    if (semanticModel.GetOperation(objectCreationNode) is not IObjectCreationOperation objectCreation)
                    {
                        continue;
                    }

                    if (objectCreation.Type is INamedTypeSymbol type)
                    {
                        AddTypeKeys(instantiated, type);
                    }
                }
            }
            return new SourceIndex(concreteTypes, instantiated);
        }

        public IEnumerable<IMethodSymbol> FindDispatchCandidates(IMethodSymbol targetMethod, ITypeSymbol? receiverType = null)
        {
            if (targetMethod.IsStatic || targetMethod.MethodKind != MethodKind.Ordinary || targetMethod.ContainingType is null)
            {
                yield break;
            }

            var normalizedTarget = targetMethod.OriginalDefinition;
            var requireInstantiated = _instantiatedTypeKeys.Count > 0;
            var candidateCount = 0;
            foreach (var type in _concreteTypes)
            {
                if (requireInstantiated && !IsInstantiated(type) && !MayBeFrameworkInstantiated(type, receiverType))
                {
                    continue;
                }

                if (receiverType is INamedTypeSymbol receiverNamed && !MayDispatchTo(type, receiverNamed))
                {
                    continue;
                }

                var candidate = ResolveSourceCandidate(type, normalizedTarget, targetMethod);
                if (candidate is null || SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, normalizedTarget))
                {
                    continue;
                }

                if (!candidate.Locations.Any(location => location.IsInSource))
                {
                    continue;
                }

                yield return candidate;
                candidateCount++;
                if (candidateCount >= 32)
                {
                    yield break;
                }
            }
        }

        private bool IsInstantiated(INamedTypeSymbol type) => TypeKeys(type).Any(key => _instantiatedTypeKeys.Contains(key));

        private static bool MayBeFrameworkInstantiated(INamedTypeSymbol type, ITypeSymbol? receiverType) =>
            receiverType is not INamedTypeSymbol receiverNamed || receiverNamed.TypeKind == TypeKind.Interface || InheritsFrom(type, receiverNamed);

        private static bool MayDispatchTo(INamedTypeSymbol candidateType, INamedTypeSymbol receiverType) =>
            receiverType.TypeKind == TypeKind.Interface
                ? candidateType.AllInterfaces.Any(iface => SymbolEqualityComparer.Default.Equals(iface.OriginalDefinition, receiverType.OriginalDefinition))
                : SymbolEqualityComparer.Default.Equals(candidateType.OriginalDefinition, receiverType.OriginalDefinition) || InheritsFrom(candidateType, receiverType);

        private static IMethodSymbol? ResolveSourceCandidate(INamedTypeSymbol type, IMethodSymbol normalizedTarget, IMethodSymbol originalTarget)
        {
            if (normalizedTarget.ContainingType?.TypeKind == TypeKind.Interface)
            {
                return type.FindImplementationForInterfaceMember(normalizedTarget) as IMethodSymbol
                    ?? type.FindImplementationForInterfaceMember(originalTarget) as IMethodSymbol;
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