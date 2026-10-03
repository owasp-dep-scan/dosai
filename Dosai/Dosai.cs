using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.Syntax;
using System.IO.Compression;
using System.Runtime.Loader;
using CompilationUnitSyntax = Microsoft.CodeAnalysis.CSharp.Syntax.CompilationUnitSyntax;
using ExpressionSyntax = Microsoft.CodeAnalysis.CSharp.Syntax.ExpressionSyntax;
using FieldDeclarationSyntax = Microsoft.CodeAnalysis.CSharp.Syntax.FieldDeclarationSyntax;
using IdentifierNameSyntax = Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax;
using InvocationExpressionSyntax = Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax;
using MemberAccessExpressionSyntax = Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax;
using ObjectCreationExpressionSyntax = Microsoft.CodeAnalysis.CSharp.Syntax.ObjectCreationExpressionSyntax;

namespace Depscan;

internal static partial class FSharpRegex
{
    [GeneratedRegex(@"^\s*module\s+([\w\.]+)", RegexOptions.Compiled)]
    internal static partial Regex Module();

    [GeneratedRegex(@"^\s*type\s+(\w+)", RegexOptions.Compiled)]
    internal static partial Regex Type();

    [GeneratedRegex(@"^\s*let\s+(rec\s+)?(\w+)", RegexOptions.Compiled)]
    internal static partial Regex Function();

    [GeneratedRegex(@"^\s*member\s+(\w+|\.)\.(\w+)", RegexOptions.Compiled)]
    internal static partial Regex Member();

    [GeneratedRegex(@"^\s*new\s*\(", RegexOptions.Compiled)]
    internal static partial Regex Constructor();

    [GeneratedRegex(@"^\s*open\s+([\w\.]+)", RegexOptions.Multiline | RegexOptions.Compiled)]
    internal static partial Regex Open();

    [GeneratedRegex(@"(\w+)\s*\(", RegexOptions.Compiled)]
    internal static partial Regex MethodCall();
}

/// <summary>
///     An enhanced AssemblyLoadContext that resolves dependencies from a list of specified
///     search paths. Inspected paths are searched before shared-framework paths, matching the
///     probing order callers expect for an application's own dependencies.
/// </summary>
/// <remarks>
///     Assemblies from the inspected paths are read into memory instead of being loaded from
///     their file path. <see cref="AssemblyLoadContext.LoadFromAssemblyPath" /> memory-maps the
///     file and keeps it open for the lifetime of the context, and unloading a collectible
///     context is asynchronous, so an analyzed assembly stayed locked well after inspection
///     finished. On Windows that made the analyzed build output undeletable for the rest of the
///     process - a caller could not scan its own output directory and then clean or replace it.
///     Shared-framework assemblies keep the mapped path: they are immutable, nobody deletes
///     them, and copying them per inspected assembly would read tens of megabytes each time.
/// </remarks>
internal sealed class InspectionAssemblyLoadContext(IEnumerable<string> inspectedPaths, IEnumerable<string> sharedFrameworkPaths)
    : AssemblyLoadContext(isCollectible: true)
{
    private readonly List<string> _inspectedDirectories = inspectedPaths.Distinct().ToList();
    private readonly List<string> _sharedFrameworkDirectories = sharedFrameworkPaths.Distinct().ToList();

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var fileName = assemblyName.Name + Constants.AssemblyExtension;
        return Probe(_inspectedDirectories, fileName, LoadWithoutLockingFile)
               ?? Probe(_sharedFrameworkDirectories, fileName, LoadFromAssemblyPath);
    }

    /// <summary>Loads an inspected assembly by value so no handle outlives the read.</summary>
    internal Assembly LoadWithoutLockingFile(string assemblyPath)
    {
        using var stream = new FileStream(assemblyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return LoadFromStream(stream);
    }

    private static Assembly? Probe(List<string> directories, string fileName, Func<string, Assembly> load)
    {
        foreach (var directory in directories)
        {
            var candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate))
            {
                return load(candidate);
            }
        }

        return null;
    }
}

/// <summary>
/// Dotnet Source and Assembly Inspector
/// </summary>
public static class Dosai
{
    #region Signature Helpers

    /// <summary>
    /// Generates a unique signature for an IMethodSymbol (Method or Constructor).
    /// Format: Namespace.ClassName.MethodName[[GenericParams]](ParamType1,ParamType2,...):ReturnType
    /// For constructors, ReturnType is typically omitted or the class name.
    /// </summary>
    private static string GenerateMethodSignature(IMethodSymbol? methodSymbol)
    {
        if (methodSymbol is null)
            return string.Empty;

        var method = methodSymbol.ReducedFrom ?? methodSymbol.OriginalDefinition;
        var containingType = NormalizeSymbolName(method.ContainingType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? string.Empty);
        var methodName = method.MethodKind switch
        {
            MethodKind.Constructor => ".ctor",
            _ when IsNestedFunction(method) => NestedFunctionName(method),
            _ => method.MetadataName
        };
        var returnType = method.MethodKind == MethodKind.Constructor ? "" : NormalizeSymbolName(method.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));

        var parameters = method.Parameters.Select(p => NormalizeSymbolName(p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))).ToList();
        var paramString = string.Join(",", parameters);
        var signature = $"{containingType}.{methodName}({paramString})";
        if (!string.IsNullOrEmpty(returnType))
        {
            signature += $":{returnType}";
        }
        return signature;
    }

    /// <summary>
    ///     Signature for an IMethodSymbol in Dosai's stable format. Exposed for framework
    ///     providers so ServiceOperation.MethodId matches Methods[].SourceSignature exactly.
    ///     Uncached: the source pipeline renders through <see cref="SourceRenderCache" />, which
    ///     memoizes per symbol so one member costs one render and one string instance per run.
    /// </summary>
    internal static string FormatMethodSignature(IMethodSymbol methodSymbol) => GenerateMethodSignature(methodSymbol);

    /// <summary>An anonymous function (lambda, anonymous method) or a local function: named after the member that declares it.</summary>
    internal static bool IsNestedFunction(IMethodSymbol method) => method.MethodKind is MethodKind.AnonymousFunction or MethodKind.LocalFunction;

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<INamedTypeSymbol, System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyDictionary<(SyntaxTree Tree, Microsoft.CodeAnalysis.Text.TextSpan Span, bool Query), string>>> NestedFunctionNamesByType = new();

    /// <summary>
    ///     The name of an anonymous or local function, after the member that declares it, the way
    ///     the compiler names the methods it generates for them: <c>&lt;Run&gt;lambda2</c> is the
    ///     second anonymous function in the type's members named <c>Run</c> (every overload and
    ///     partial part, in source order, nested ones included), <c>&lt;Run&gt;Helper</c> a local
    ///     function (<c>&lt;Run&gt;Helper|2</c> for a second one of that name), and
    ///     <c>&lt;Run&gt;query1</c> the lambdas the compiler makes of the first query expression's
    ///     clauses (<c>from</c>, <c>where</c>, <c>select</c>, ...), one node per query. An
    ///     anonymous function has no name and a local function one that is only unique in its
    ///     scope, so their plain signatures merged every lambda of a type with the same shape into
    ///     one call-graph node (<c>Ns.Type.():void</c>), and a local function into a same-named
    ///     member. Ordinals come from syntax alone, so the name is the same in every run and worker.
    /// </summary>
    internal static string NestedFunctionName(IMethodSymbol function)
    {
        ISymbol? member = function.ContainingSymbol;
        while (member is IMethodSymbol enclosing && IsNestedFunction(enclosing))
        {
            member = enclosing.ContainingSymbol;
        }

        // An auto-property initializer belongs to the property's backing field.
        if (member is IFieldSymbol { AssociatedSymbol: IPropertySymbol property })
        {
            member = property;
        }

        var memberName = member?.MetadataName ?? string.Empty;
        // A query clause's lambda is implicit: C# declares it by the clause's expression, Visual
        // Basic only locates it. Either way it belongs to the nearest query expression around
        // that expression - not to a query that is the expression (`select from c in ...`).
        var isQuery = function is { IsImplicitlyDeclared: true, MethodKind: MethodKind.AnonymousFunction };
        SyntaxNode? syntax;
        if (isQuery)
        {
            var clause = function.Locations.FirstOrDefault(location => location.IsInSource) is { SourceTree: { } tree } location
                ? tree.GetRoot().FindNode(location.SourceSpan, getInnermostNodeForTie: true)
                : null;
            syntax = (clause is not null && IsQueryExpression(clause) ? clause.Parent : clause)?.AncestorsAndSelf().FirstOrDefault(IsQueryExpression);
        }
        else
        {
            syntax = function.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
        }

        if (syntax is Microsoft.CodeAnalysis.VisualBasic.Syntax.LambdaHeaderSyntax { Parent: { } lambda })
        {
            syntax = lambda;
        }

        if (member?.ContainingType is { } type && syntax is not null
            && NestedFunctionNamesByType.GetOrCreateValue(type).GetOrAdd(memberName, name => IndexNestedFunctions(type, name)).TryGetValue((syntax.SyntaxTree, syntax.Span, isQuery), out var nestedName))
        {
            return nestedName;
        }

        // Outside its member's declarations - not a shape the compiler produces - the position
        // still tells it apart.
        var position = function.Locations.FirstOrDefault()?.GetLineSpan().StartLinePosition ?? default;
        var kind = function.MethodKind == MethodKind.LocalFunction ? function.MetadataName : "lambda";
        return string.Create(CultureInfo.InvariantCulture, $"<{memberName}>{kind}@{position.Line + 1}_{position.Character + 1}");
    }

    /// <summary>The name of every anonymous and local function declared by the type's members of one name, keyed by syntax.</summary>
    private static IReadOnlyDictionary<(SyntaxTree Tree, Microsoft.CodeAnalysis.Text.TextSpan Span, bool Query), string> IndexNestedFunctions(INamedTypeSymbol type, string memberName)
    {
        var declarations = type.GetMembers(memberName)
            .SelectMany(member => member switch
            {
                IMethodSymbol { PartialImplementationPart: { } implementation } method => method.DeclaringSyntaxReferences.Concat(implementation.DeclaringSyntaxReferences),
                IPropertySymbol { PartialImplementationPart: { } implementation } property => property.DeclaringSyntaxReferences.Concat(implementation.DeclaringSyntaxReferences),
                _ => member.DeclaringSyntaxReferences
            })
            .Select(reference => reference.GetSyntax())
            .Distinct()
            // '/' and '\' sort differently against letters: one separator keeps the ordinals, and so
            // the ids, the same on every OS.
            .OrderBy(declaration => declaration.SyntaxTree.FilePath.Replace('\\', '/'), StringComparer.Ordinal)
            .ThenBy(declaration => declaration.SpanStart);
        var names = new Dictionary<(SyntaxTree, Microsoft.CodeAnalysis.Text.TextSpan, bool), string>();
        var anonymousFunctions = 0;
        var queries = 0;
        var localFunctions = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var root in declarations.SelectMany(NestedFunctionScope))
        {
            foreach (var node in root.DescendantNodesAndSelf())
            {
                switch (node)
                {
                    case AnonymousFunctionExpressionSyntax or Microsoft.CodeAnalysis.VisualBasic.Syntax.LambdaExpressionSyntax:
                        names[(node.SyntaxTree, node.Span, false)] = string.Create(CultureInfo.InvariantCulture, $"<{memberName}>lambda{++anonymousFunctions}");
                        break;
                    case var query when IsQueryExpression(query):
                        names[(node.SyntaxTree, node.Span, true)] = string.Create(CultureInfo.InvariantCulture, $"<{memberName}>query{++queries}");
                        break;
                    case LocalFunctionStatementSyntax local:
                        // Arity-free, like every method id (MetadataName carries no arity for methods).
                        var localName = local.Identifier.ValueText;
                        var occurrence = localFunctions[localName] = localFunctions.GetValueOrDefault(localName) + 1;
                        names[(node.SyntaxTree, node.Span, false)] = occurrence == 1
                            ? $"<{memberName}>{localName}"
                            : string.Create(CultureInfo.InvariantCulture, $"<{memberName}>{localName}|{occurrence}");
                        break;
                }
            }
        }

        return names;
    }

    private static bool IsQueryExpression(SyntaxNode node) =>
        node is Microsoft.CodeAnalysis.CSharp.Syntax.QueryExpressionSyntax or Microsoft.CodeAnalysis.VisualBasic.Syntax.QueryExpressionSyntax;

    /// <summary>
    ///     The syntax a member's nested functions live in: its declaration, except top-level
    ///     statements (<c>&lt;Main&gt;$</c>, declared by the whole file) and a primary constructor
    ///     (declared by the whole type), whose own code is only their statements, parameters and
    ///     base-type arguments. Visual Basic declares a member by its header statement and a
    ///     field by its name, so the scope is the block or declarator around them.
    /// </summary>
    private static IEnumerable<SyntaxNode> NestedFunctionScope(SyntaxNode declaration) => declaration switch
    {
        CompilationUnitSyntax unit => unit.Members.OfType<GlobalStatementSyntax>(),
        TypeDeclarationSyntax type => new SyntaxNode?[] { type.ParameterList, type.BaseList }.OfType<SyntaxNode>(),
        Microsoft.CodeAnalysis.VisualBasic.Syntax.StatementSyntax { Parent: Microsoft.CodeAnalysis.VisualBasic.Syntax.MethodBlockBaseSyntax or Microsoft.CodeAnalysis.VisualBasic.Syntax.PropertyBlockSyntax or Microsoft.CodeAnalysis.VisualBasic.Syntax.EventBlockSyntax } header => [header.Parent!],
        Microsoft.CodeAnalysis.VisualBasic.Syntax.ModifiedIdentifierSyntax { Parent: Microsoft.CodeAnalysis.VisualBasic.Syntax.VariableDeclaratorSyntax declarator } => [declarator],
        _ => [declaration]
    };

    internal static string NormalizeSymbolName(string symbolName) => symbolName
        .Replace("global::", string.Empty, StringComparison.Ordinal)
        .Replace("Global.", string.Empty, StringComparison.Ordinal);

    private static string CreateMemberId(string? namespaceName, string? className, string? memberName, IEnumerable<Parameter>? parameters = null, string? returnType = null)
    {
        var typeName = string.Join('.', new[] { namespaceName, className }.Where(part => !string.IsNullOrWhiteSpace(part)));
        var parameterTypes = parameters?.Select(p => NormalizeSymbolName(p.TypeFullName ?? p.Type ?? string.Empty)) ?? [];
        var id = $"{typeName}.{memberName}({string.Join(",", parameterTypes)})";
        if (!string.IsNullOrWhiteSpace(returnType) && memberName != ".ctor")
        {
            id += $":{NormalizeSymbolName(returnType)}";
        }
        return id;
    }

    /// <summary>
    ///     Formats one Roslyn attribute constructor argument into its structured output shape -
    ///     see <see cref="CustomAttributeArgumentInfo" /> for the encoding. Array-typed constants
    ///     (params string[] constructor arguments such as HttpTriggerAttribute's methods) carry
    ///     their elements in <see cref="TypedConstant.Values" />; reading <see cref="TypedConstant.Value" />
    ///     on them throws. A null-valued constant (<c>[Routes(null)]</c>, issue #56) leaves
    ///     <see cref="TypedConstant.Values" /> at its default, so null is decided before Values
    ///     is ever touched - touching the default array throws NullReferenceException.
    /// </summary>
    private static CustomAttributeArgumentInfo ToAttributeArgumentInfo(TypedConstant constant)
    {
        var info = new CustomAttributeArgumentInfo { Type = constant.Type?.ToDisplayString() };
        if (constant.IsNull)
        {
            info.IsNull = true;
            info.IsArray = constant.Kind == TypedConstantKind.Array;
            return info;
        }

        if (constant.Kind == TypedConstantKind.Array)
        {
            info.IsArray = true;
            info.Elements = constant.Values.IsDefault
                ? []
                : constant.Values.Select(ToAttributeArgumentInfo).ToList();
            return info;
        }

        info.Value = constant.Value switch
        {
            null => null,
            string text => text,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            var other => other.ToString()
        };
        return info;
    }

    /// <summary>
    ///     Named arguments keep their scalar string shape: a null named constant is null, and an
    ///     array-valued named constant flattens comma-joined (a null element flattens to the
    ///     empty string). Named array arguments are rare; constructor arguments, where the
    ///     issue-#56 shapes live, use the lossless structured encoding instead.
    /// </summary>
    private static string? FormatNamedArgumentValue(TypedConstant constant)
    {
        if (constant.IsNull)
        {
            return null;
        }

        return constant.Kind == TypedConstantKind.Array
            ? (constant.Values.IsDefault
                ? string.Empty
                : string.Join(",", constant.Values.Select(value => FormatNamedArgumentValue(value) ?? string.Empty)))
            : constant.Value switch
            {
                null => null,
                string text => text,
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                var other => other.ToString()
            };
    }

    /// <summary>
    ///     Reflection-side counterpart of <see cref="ToAttributeArgumentInfo(Microsoft.CodeAnalysis.TypedConstant)" />:
    ///     produces the same structured encoding for the same attribute so a source scan and an
    ///     assembly scan of one code base agree. A <see cref="CustomAttributeTypedArgument" />
    ///     wraps an array value as a read-only collection of nested arguments - the assembly
    ///     path used to stringify that wrapper as a type name - and a null array arrives as a
    ///     null <see cref="CustomAttributeTypedArgument.Value" /> over an array
    ///     <see cref="CustomAttributeTypedArgument.ArgumentType" />, so null is decided before
    ///     the collection is ever touched. Scalar text is formatted invariantly to match the
    ///     Roslyn side on every machine.
    /// </summary>
    private static CustomAttributeArgumentInfo ToAttributeArgumentInfo(CustomAttributeTypedArgument argument)
    {
        var info = new CustomAttributeArgumentInfo { Type = argument.ArgumentType.ToString() };
        if (argument.Value is null)
        {
            info.IsNull = true;
            info.IsArray = argument.ArgumentType.IsArray;
            return info;
        }

        if (argument.ArgumentType.IsArray && argument.Value is IReadOnlyCollection<CustomAttributeTypedArgument> elements)
        {
            info.IsArray = true;
            info.Elements = elements.Select(ToAttributeArgumentInfo).ToList();
            return info;
        }

        info.Value = Convert.ToString(argument.Value, CultureInfo.InvariantCulture);
        return info;
    }

    /// <summary>Reflection-side counterpart of <see cref="FormatNamedArgumentValue(Microsoft.CodeAnalysis.TypedConstant)" />, same encoding.</summary>
    private static string? FormatNamedArgumentValue(CustomAttributeTypedArgument argument)
    {
        if (argument.Value is null)
        {
            return null;
        }

        if (argument.ArgumentType.IsArray && argument.Value is IReadOnlyCollection<CustomAttributeTypedArgument> elements)
        {
            return string.Join(",", elements.Select(element => FormatNamedArgumentValue(element) ?? string.Empty));
        }

        return Convert.ToString(argument.Value, CultureInfo.InvariantCulture);
    }

    #endregion

    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    ///     Extracts the custom attribute list of one symbol, degrading to an empty list when a
    ///     malformed attribute throws (the issue-#56 containment). One bad attribute costs this
    ///     one symbol's attribute inventory - never the scan - and the failure is appended to
    ///     <paramref name="diagnostics" /> (which reaches <see cref="MethodsSlice.Diagnostics" />)
    ///     instead of vanishing. The catch is scoped to exactly this block; a bug anywhere else
    ///     in the pipeline still surfaces.
    /// </summary>
    /// <summary>
    ///     Reflection-side counterpart of the symbol overload, with the same containment: a
    ///     member whose attribute blob is malformed - unresolvable attribute type, a custom
    ///     attribute whose arguments cannot be decoded - costs that member's attribute inventory
    ///     and a diagnostic, not the assembly. The enclosing per-assembly handler would otherwise
    ///     drop every remaining type in the file.
    /// </summary>
    internal static List<CustomAttributeInfo> ExtractCustomAttributes(MemberInfo member, ICollection<string> diagnostics)
    {
        try
        {
            return member.GetCustomAttributesData().Select(attr => new CustomAttributeInfo
            {
                Name = attr.AttributeType.Name,
                FullName = attr.AttributeType.FullName,
                ConstructorArguments = attr.ConstructorArguments.Select(ToAttributeArgumentInfo).ToList(),
                NamedArguments = attr.NamedArguments.Select(na => new NamedArgumentInfo
                {
                    Name = na.MemberName,
                    Value = FormatNamedArgumentValue(na.TypedValue)
                }).ToList()
            }).ToList();
        }
        catch (Exception ex)
        {
            diagnostics.Add($"Attribute extraction failed for '{member.DeclaringType?.FullName}.{member.Name}': {ex.GetType().Name}: {ex.Message}. This member's attributes are omitted; the rest of the analysis is unaffected.");
            return [];
        }
    }

    internal static List<CustomAttributeInfo> ExtractCustomAttributes(ISymbol symbol, ICollection<string> diagnostics)
    {
        try
        {
            return symbol.GetAttributes().Select(attr => new CustomAttributeInfo
            {
                Name = attr.AttributeClass?.Name,
                FullName = attr.AttributeClass?.ToDisplayString(),
                ConstructorArguments = attr.ConstructorArguments.Select(ToAttributeArgumentInfo).ToList(),
                NamedArguments = attr.NamedArguments.Select(na => new NamedArgumentInfo
                {
                    Name = na.Key,
                    Value = FormatNamedArgumentValue(na.Value)
                }).ToList()
            }).ToList();
        }
        catch (Exception ex)
        {
            diagnostics.Add($"Attribute extraction failed for '{symbol.ToDisplayString()}': {ex.GetType().Name}: {ex.Message}. This symbol's attributes are omitted; the rest of the analysis is unaffected.");
            return [];
        }
    }

    /// <summary>
    /// Get all assembly/source methods for the given path to assembly/source or directory of assemblies/source
    /// </summary>
    /// <param name="path">Filesystem path to assembly/source file or directory containing assembly/source files</param>
    /// <returns>JSON list of assembly/source methods</returns>
    public static string GetMethods(string path)
        => JsonSerializer.Serialize(GetMethodsSlice(path), Options);

    /// <summary>
    /// Build the <see cref="MethodsSlice"/> for the given path without serializing it. Exposed so callers that
    /// write the result to a file can stream the JSON (see <see cref="WriteMethods"/>) instead of materialising a
    /// single multi-hundred-MB string, which is what drove peak RSS into the multi-GB range and eventually
    /// overflowed the string allocator on large assembly trees.
    /// </summary>
    /// <remarks>
    ///     Runs on a dedicated thread with <see cref="DedicatedStack.AnalysisStackSize" /> of stack. The
    ///     source-analysis phases ask Roslyn for <c>IOperation</c> trees, and the operation factory recurses
    ///     roughly one frame set per call in a chain, so one long fluent chain in ordinary C# overflows the
    ///     default main-thread stack inside <c>SemanticModel.GetOperation</c> and terminates the process -
    ///     an uncatchable failure that writes no output and names no file (issue #60). The larger stack
    ///     moves that limit far beyond the chain depth of real code, for every caller of this entry point.
    /// </remarks>
    public static MethodsSlice GetMethodsSlice(string path, Frameworks.FrameworkAnalysisOptions? frameworkOptions = null, BuildPreparationMode buildPreparation = BuildPreparationMode.None)
        => DedicatedStack.Run("Dosai methods analysis", () => BuildMethodsSlice(path, frameworkOptions, buildPreparation));

    private static MethodsSlice BuildMethodsSlice(string path, Frameworks.FrameworkAnalysisOptions? frameworkOptions, BuildPreparationMode buildPreparation)
    {
        DebugLog.Measure("methods.build-preparation", () => BuildPreparation.Prepare(path, buildPreparation));
        var purlResolver = DebugLog.Measure("methods.package-url-resolver", () => PackageUrlResolver.Create(path));
        var assemblyDiagnostics = new List<string>();
        List<Method> methods;
        using (DebugLog.Phase("methods.assembly-inspection"))
        {
            methods = GetAssemblyMethods(path, assemblyDiagnostics);
        }
        DebugLog.Count("assembly methods", methods.Count);

        // Source analysis and every consumer of its compilations (framework analysis, the
        // security analyzer) run inside AnalyzeSourcesAndFrameworks, which returns only their
        // results. The compilations pin every parsed tree - the largest object the pipeline
        // holds - and none of the phases below reads them, so they must be unreachable before
        // the IL call graph, merges, enrichment, reachability and serialization run (the memory
        // wall of issue #65). Scoping them in a helper frame is what makes that true. Holding
        // them in a local here and reassigning it did not: the security-analysis lambda
        // captured the framework context into a closure object, and this method runs once, so
        // it stays at Tier-0, where the JIT reports every IL local (the closure's included)
        // live until the method returns - a heap snapshot at enrichment still held the
        // compilation and every syntax tree.
        var stage = AnalyzeSourcesAndFrameworks(path, methods, purlResolver, frameworkOptions);
        if (CompilationReleaseProbe.Value is { } releaseProbe)
        {
            releaseProbe.RecordAfterRelease();
        }

        if (DebugLog.Enabled)
        {
            GC.Collect(generation: GC.MaxGeneration, mode: GCCollectionMode.Forced, blocking: true, compacting: false);
            DebugLog.Log("released source compilations after framework analysis");
        }

        var sourceMethods = stage.SourceMethods;
        var usings = stage.UsingDirectives;
        var methodCalls = stage.MethodCalls;
        var properties = stage.Properties;
        var fields = stage.Fields;
        var events = stage.Events;
        var constructors = stage.Constructors;
        var callGraph = stage.CallGraph;
        var sourceAssemblyMapping = stage.SourceAssemblyMappings;
        var sourceMode = stage.SourceMode;
        var sourceDiagnostics = stage.Diagnostics;
        var frameworkResult = stage.FrameworkResult;
        var legacyEndpoints = stage.LegacyEndpoints;
        var apiEndpoints = stage.ApiEndpoints;
        var securityFindings = stage.SecurityFindings;

        List<MethodCalls> assemblyMethodCalls;
        CallGraph assemblyCallGraph;
        using (DebugLog.Phase("methods.assembly-call-graph"))
        {
            (assemblyMethodCalls, assemblyCallGraph) = AssemblyCallGraphAnalyzer.Analyze(path, methods);
        }
        DebugLog.Count("call graph (assembly IL) nodes", assemblyCallGraph.Nodes.Count);
        DebugLog.Count("call graph (assembly IL) edges", assemblyCallGraph.Edges.Count);
        NormalizeAssemblyGraphToSourceIds(assemblyMethodCalls, assemblyCallGraph, sourceAssemblyMapping);
        DebugLog.Count("assembly IL call sites normalized to source ids", assemblyMethodCalls.Count);
        methodCalls.AddRange(assemblyMethodCalls);
        MergeCallGraph(callGraph, assemblyCallGraph);
        // Trust boundaries sweep the merged graph's public inbound services; it needs the
        // framework result (already computed) and the merged graph (just built), in that order.
        Frameworks.FrameworkRegistry.ApplyTrustBoundaries(frameworkResult, callGraph);
        DebugLog.Count("call graph (merged) nodes", callGraph.Nodes.Count);
        DebugLog.Count("call graph (merged) edges", callGraph.Edges.Count);
        var assemblyInformation = DebugLog.Measure("methods.assembly-information", () => GetAssemblyInformation(path));
        DebugLog.Count("assembly information entries", assemblyInformation.Count);

        methods.AddRange(sourceMethods);
        using (DebugLog.Phase("methods.enrichment"))
        {
            EnrichPackageUrls(purlResolver, methods, usings, methodCalls, properties, fields, events, constructors, callGraph, assemblyInformation, sourceAssemblyMapping);
        }
        var entryPoints = DebugLog.Measure("methods.entry-points", () => MergeEntryPoints(TransparencyBuilder.BuildEntryPoints(legacyEndpoints, methods), frameworkResult.EntryPoints));
        DebugLog.Count("entry points (merged)", entryPoints.Count);
        using (DebugLog.Phase("methods.method-identities"))
        {
            EnrichMethodIdentities(methods, callGraph, sourceMode);
        }
        var packageReachability = DebugLog.Measure("methods.package-reachability", () => TransparencyBuilder.BuildPackageReachability(callGraph, dependencies: usings));
        DebugLog.Count("package reachability entries", packageReachability.Count);

        // Collapse repeated call sites of the same (source, target, call type, evidence) pair
        // into one edge with a count, then compute the reachability section once over the
        // merged graph. Bounded walks; diagnostics land in the slice.
        List<NodeReachability> reachability;
        List<RecursionCluster> recursionClusters;
        bool budgetExhausted;
        List<DeadCodeEntry> deadCode;
        var reachabilityDiagnostics = new List<string>();
        using (DebugLog.Phase("methods.reachability"))
        {
            ReachabilityAnalyzer.CollapseDuplicateCallSites(callGraph);
            DebugLog.Count("call graph (collapsed) edges", callGraph.Edges.Count);
            (reachability, recursionClusters, budgetExhausted) = ReachabilityAnalyzer.Compute(callGraph, entryPoints, reachabilityDiagnostics);
            deadCode = ReachabilityAnalyzer.BuildDeadCode(callGraph, reachability, sourceMode, budgetExhausted, reachabilityDiagnostics);
        }
        // Reachability node/component counts come from ReachabilityAnalyzer.Compute itself,
        // next to the bucketing-path decision; only the derived reports are counted here.
        DebugLog.Count("recursion clusters", recursionClusters.Count);
        DebugLog.Count("dead code entries", deadCode.Count);

        var sliceDiagnostics = frameworkResult.Diagnostics.Select(diagnostic => $"{diagnostic.FrameworkId}: {diagnostic.Message}").Concat(reachabilityDiagnostics).ToList();
        // Restore-output and unresolved-call diagnostics explain why package reachability may
        // sit at Low confidence: unbuilt trees degrade semantic binding, and that fact should
        // reach consumers instead of hiding behind silently missing call edges.
        sliceDiagnostics.AddRange(NuGetRestoreCache.GetDiagnostics(path));
        sliceDiagnostics.AddRange(sourceDiagnostics);
        sliceDiagnostics.AddRange(assemblyDiagnostics);
        var unresolvedCallCount = methodCalls.Count(call => call.EvidenceKind == AnalysisEvidenceKind.SourceUnresolved);
        if (unresolvedCallCount > 0)
        {
            sliceDiagnostics.Add(FrameworkReferences.Current.References.Count == 0
                ? string.Create(CultureInfo.InvariantCulture, $"Semantic binding failed for {unresolvedCallCount} call sites: Dosai resolved no framework metadata references (see the framework-reference diagnostic), so every framework call is unresolved regardless of the tree's restore state.")
                : string.Create(CultureInfo.InvariantCulture, $"Semantic binding failed for {unresolvedCallCount} call sites: target assemblies were missing or conflicted with other references. Restore or build the tree (--restore/--build) to raise reachability confidence."));
        }

        // Conditional-compilation guards were evaluated against the detected target frameworks;
        // consumers should see what was assumed, and the fallback to the latest modern net must
        // be visible rather than silent.
        var metadata = TransparencyBuilder.CreateMetadata(path);
        var detectedTargetFrameworks = TargetFrameworkDetection.Detect(path);
        if (detectedTargetFrameworks.Count > 0)
        {
            metadata.TargetFrameworks = [.. detectedTargetFrameworks];
            // Which one the guards resolved against is not derivable from the list, and on a
            // multi-target tree it decides which `#if` arms are in the results at all.
            if (detectedTargetFrameworks.Count > 1 && FrameworkPreprocessorDefines.TrySelectRepresentative(detectedTargetFrameworks, out var representative))
            {
                metadata.GuardTargetFramework = representative;
                // Each file's guards resolve against its nearest project's targets; the root-wide
                // representative only covers files outside any project with a readable target.
                var projectGuards = ProjectGuardTargets(path);
                if (projectGuards.Count > 1)
                {
                    metadata.ProjectGuardTargetFrameworks = projectGuards;
                    sliceDiagnostics.Add(string.Create(CultureInfo.InvariantCulture, $"Multiple target frameworks detected ({string.Join(", ", detectedTargetFrameworks)}); conditional-compilation guards were evaluated per project against each project's most modern target (Metadata.ProjectGuardTargetFrameworks, {projectGuards.Count} projects), and against '{representative}' for files outside a project with a readable target. Arms exclusive to a project's other targets are not analyzed."));
                }
                else
                {
                    sliceDiagnostics.Add($"Multiple target frameworks detected ({string.Join(", ", detectedTargetFrameworks)}); conditional-compilation guards were evaluated against '{representative}'. Arms exclusive to the other targets are not analyzed.");
                }
            }
            else if (detectedTargetFrameworks.Count == 1)
            {
                metadata.GuardTargetFramework = detectedTargetFrameworks[0];
            }
        }
        else
        {
            sliceDiagnostics.Add($"No TargetFramework detected in project files or runtimeconfig under '{path}'; conditional-compilation guards assume the latest modern .NET (net{FrameworkPreprocessorDefines.LatestModernNetMajor}_0).");
        }

        return new MethodsSlice
        {
            Metadata = metadata,
            Dependencies = usings,
            Methods = methods,
            MethodCalls = methodCalls,
            Properties = properties,
            Fields = fields,
            Events = events,
            Constructors = constructors,
            CallGraph = callGraph,
            ApiEndpoints = apiEndpoints,
            EntryPoints = entryPoints,
            Reachability = reachability,
            RecursionClusters = recursionClusters,
            DeadCode = deadCode,
            SecurityFindings = securityFindings,
            PackageReachability = packageReachability,
            AssemblyInformation = assemblyInformation,
            SourceAssemblyMapping = sourceAssemblyMapping,
            Services = frameworkResult.Services,
            AiComponents = frameworkResult.AiComponents,
            Frameworks = frameworkResult.Frameworks,
            Diagnostics = sliceDiagnostics
        };
    }

    private static readonly AsyncLocal<ReleaseProbe?> CompilationReleaseProbe = new();

    /// <summary>
    ///     Test hook for the compilation-release contract: weakly observes the source
    ///     compilations when they are built and, once source and framework analysis have
    ///     returned, forces a full collection and records whether any of them survived. Applies
    ///     to the current execution context and the analysis threads it starts.
    /// </summary>
    internal static IDisposable ObserveCompilationRelease(out ReleaseProbe probe)
    {
        var previous = CompilationReleaseProbe.Value;
        var current = new ReleaseProbe();
        probe = current;
        CompilationReleaseProbe.Value = current;
        return new ScopeRestore(() => CompilationReleaseProbe.Value = previous);
    }

    internal sealed class ReleaseProbe
    {
        private readonly List<WeakReference> _compilations = [];

        /// <summary>Compilations observed; zero means source analysis built none.</summary>
        public int Observed => _compilations.Count;

        /// <summary>Compilations still reachable after the release point, or null before it ran.</summary>
        public int? AliveAfterRelease { get; private set; }

        internal void Observe(Frameworks.SourceCompilations compilations)
        {
            if (compilations.CSharp is not null) _compilations.Add(new WeakReference(compilations.CSharp));
            if (compilations.VisualBasic is not null) _compilations.Add(new WeakReference(compilations.VisualBasic));
        }

        internal void RecordAfterRelease()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            AliveAfterRelease = _compilations.Count(reference => reference.IsAlive);
        }
    }

    private sealed class ScopeRestore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    /// <summary>
    ///     The guard target each C# and F# project with a readable target framework resolved to,
    ///     sorted by project path: the per-project decisions the parser made
    ///     (<see cref="TargetFrameworkDetection.ForFile" />).
    /// </summary>
    private static List<ProjectGuardTarget> ProjectGuardTargets(string path)
    {
        var guards = new List<ProjectGuardTarget>();
        foreach (var project in TargetFrameworkDetection.ProjectsUnder(path, ".csproj").Concat(TargetFrameworkDetection.ProjectsUnder(path, ".fsproj")))
        {
            if (project.ProjectFile is not null && FrameworkPreprocessorDefines.TrySelectRepresentative(project.TargetFrameworks, out var guard))
            {
                guards.Add(new ProjectGuardTarget
                {
                    Project = project.ProjectFile.Replace('\\', '/'),
                    TargetFrameworks = [.. project.TargetFrameworks],
                    GuardTargetFramework = guard
                });
            }
        }

        guards.Sort((x, y) => string.CompareOrdinal(x.Project, y.Project));
        return guards;
    }

    /// <summary>
    ///     Everything the methods slice needs from source analysis and its compilation consumers,
    ///     without the compilations themselves.
    /// </summary>
    private sealed record SourceStage(
        List<Method> SourceMethods,
        List<Dependency> UsingDirectives,
        List<MethodCalls> MethodCalls,
        List<PropertyInfo> Properties,
        List<FieldInfo> Fields,
        List<EventInfo> Events,
        List<ConstructorInfo> Constructors,
        CallGraph CallGraph,
        List<SourceAssemblyMapping> SourceAssemblyMappings,
        bool SourceMode,
        List<string> Diagnostics,
        Frameworks.FrameworkAnalysisResult FrameworkResult,
        List<ApiEndpoint> LegacyEndpoints,
        List<ApiEndpoint> ApiEndpoints,
        List<Frameworks.SecurityFinding> SecurityFindings);

    /// <summary>
    ///     Source analysis followed by the only phases that read its compilations: framework
    ///     analysis and the security analyzer (neither reads the IL call graph, so running them
    ///     before it loses nothing). The compilations and the framework context - with its
    ///     per-file text cache - live only in this frame, so they are collectable as soon as it
    ///     returns; see <see cref="BuildMethodsSlice" />. Never inlined, which would move them
    ///     back into the caller's frame.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static SourceStage AnalyzeSourcesAndFrameworks(string path, List<Method> assemblyMethods, PackageUrlResolver purlResolver, Frameworks.FrameworkAnalysisOptions? frameworkOptions)
    {
        var (sourceMethods, usings, methodCalls, properties, fields, events, constructors, callGraph, sourceAssemblyMapping, sourceMode, compilations, sourceDiagnostics) = AnalyzeSourcesWithDebugLogging(path, assemblyMethods);
        CompilationReleaseProbe.Value?.Observe(compilations);
        DebugLog.Count("source methods", sourceMethods.Count);
        DebugLog.Count("call graph (source) nodes", callGraph.Nodes.Count);
        DebugLog.Count("call graph (source) edges", callGraph.Edges.Count);

        Frameworks.FrameworkAnalysisResult frameworkResult;
        List<ApiEndpoint> legacyEndpoints;
        List<ApiEndpoint> apiEndpoints;
        List<Frameworks.SecurityFinding> securityFindings;
        using (DebugLog.Phase("methods.framework-analysis"))
        {
            var frameworkContext = Frameworks.FrameworkContext.FromCompilations(path, compilations.CSharp, compilations.VisualBasic, purlResolver);
            frameworkResult = Frameworks.FrameworkRegistry.Analyze(frameworkContext, frameworkOptions);
            DebugLog.Count("framework api endpoints", frameworkResult.ApiEndpoints.Count);
            DebugLog.Count("framework services", frameworkResult.Services.Count);
            DebugLog.Count("framework ai components", frameworkResult.AiComponents.Count);
            DebugLog.Count("framework entry points", frameworkResult.EntryPoints.Count);
            // ApiEndpointAnalyzer now only covers what no provider owns (VB.NET); the framework providers
            // own every C# endpoint. Entry points are therefore built from the analyzer's endpoints ALONE;
            // feeding it the combined list produced a second, MethodId-less copy of every provider endpoint.
            legacyEndpoints = DebugLog.Measure("methods.legacy-api-endpoints", () => ApiEndpointAnalyzer.GetApiEndpoints(path));
            DebugLog.Count("legacy analyzer api endpoints (VB remainder)", legacyEndpoints.Count);
            apiEndpoints = frameworkResult.ApiEndpoints.Concat(legacyEndpoints).ToList();
            securityFindings = DebugLog.Measure("methods.security-analysis", () => Frameworks.SecurityAnalyzer.Run(frameworkContext, frameworkResult, apiEndpoints));
        }

        return new SourceStage(sourceMethods, usings, methodCalls, properties, fields, events, constructors, callGraph, sourceAssemblyMapping, sourceMode, sourceDiagnostics,
            frameworkResult, legacyEndpoints, apiEndpoints, securityFindings);
    }

    /// <summary>
    ///     <see cref="GetSourceMethods" /> wrapped in its debug phase. The pipeline's twelve-part
    ///     tuple stays inside this helper instead of forcing GetMethodsSlice to pre-declare every
    ///     element just to scope a phase around the call.
    /// </summary>
    private static (List<Method> SourceMethods, List<Dependency> UsingDirectives, List<MethodCalls> MethodCalls, List<PropertyInfo> Properties, List<FieldInfo> Fields, List<EventInfo> Events, List<ConstructorInfo> Constructors, CallGraph CallGraph, List<SourceAssemblyMapping> SourceAssemblyMappings, bool SourceMode, Frameworks.SourceCompilations Compilations, List<string> Diagnostics) AnalyzeSourcesWithDebugLogging(string path, List<Method> assemblyMethods)
    {
        using var phase = DebugLog.Phase("methods.source-analysis");
        return GetSourceMethods(path, assemblyMethods);
    }

    /// <summary>
    ///     Entry points contributed by framework providers keep their stable ids so services can
    ///     reference them. Analyzer-derived entry points are given content-derived ids rather than
    ///     positional <c>epN</c> ones, so that ids stay byte-identical across runs and across machines
    ///     (file discovery order must not change a bom-ref), and any that describe an endpoint a
    ///     provider already claimed are dropped instead of duplicated.
    /// </summary>
    internal static List<EntryPoint> MergeEntryPoints(List<EntryPoint> analyzerEntryPoints, List<EntryPoint> frameworkEntryPoints)
    {
        var merged = new List<EntryPoint>(frameworkEntryPoints);
        var seen = frameworkEntryPoints.Select(EntryPointSignature).ToHashSet(StringComparer.Ordinal);
        foreach (var entryPoint in analyzerEntryPoints)
        {
            var signature = EntryPointSignature(entryPoint);
            if (!seen.Add(signature))
            {
                continue;
            }

            entryPoint.Id = $"ep:{signature}";
            merged.Add(entryPoint);
        }

        return merged;
    }

    /// <summary>
    ///     Identity of an entry point independent of who discovered it: kind, verb, route and the
    ///     method it dispatches to. Deliberately excludes file paths and line numbers so the signature
    ///    , and therefore the derived id, is reproducible.
    /// </summary>
    private static string EntryPointSignature(EntryPoint entryPoint)
    {
        var target = entryPoint.MethodId
                     ?? $"{entryPoint.Namespace}.{entryPoint.ClassName}.{entryPoint.MethodName}";
        return $"{entryPoint.Kind}#{entryPoint.HttpMethod}:{entryPoint.Route}:{target}";
    }

    /// <summary>
    /// Build the methods slice for <paramref name="path"/> and stream it as JSON directly to
    /// <paramref name="outputFile"/>, returning the slice so the caller can reuse it (e.g. for graph export)
    /// without round-tripping through the serialized string. Streaming keeps the JSON out of a single
    /// contiguous string, which bounds peak memory on large assembly trees.
    /// </summary>
    public static MethodsSlice WriteMethods(string path, string outputFile, Frameworks.FrameworkAnalysisOptions? frameworkOptions = null, BuildPreparationMode buildPreparation = BuildPreparationMode.None)
        => StreamSlice(GetMethodsSlice(path, frameworkOptions, buildPreparation), outputFile);

    private static MethodsSlice StreamSlice(MethodsSlice slice, string outputFile)
    {
        using var phase = DebugLog.Phase("methods.serialization");
        using var stream = new FileStream(outputFile, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 65536);
        ParallelJsonWriter.Serialize(stream, slice, Options);
        if (DebugLog.Enabled)
        {
            DebugLog.Log($"methods output: wrote {DebugLog.FormatBytes(stream.Length)} to '{outputFile}'");
        }
        return slice;
    }

    /// <summary>
    /// Gets methods, dependencies, and call graph information from a .nupkg file.
    /// </summary>
    /// <param name="nupkgPath">Path to the .nupkg file</param>
    /// <returns>JSON string containing the results</returns>
    public static string GetMethodsFromNupkg(string nupkgPath)
        => JsonSerializer.Serialize(GetMethodsSliceFromNupkg(nupkgPath), Options);

    /// <summary>
    /// Build the methods slice for a .nupkg and stream it as JSON to <paramref name="outputFile"/>. See
    /// <see cref="WriteMethods"/> for why the result is streamed rather than materialised as a string.
    /// </summary>
    public static MethodsSlice WriteMethodsFromNupkg(string nupkgPath, string outputFile)
        => StreamSlice(GetMethodsSliceFromNupkg(nupkgPath), outputFile);

    private static MethodsSlice GetMethodsSliceFromNupkg(string nupkgPath)
    {
        string? tempExtractionDir = null;
        try
        {
            tempExtractionDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(tempExtractionDir);
            Console.WriteLine($"Extracting NuGet package: {nupkgPath} to {tempExtractionDir}");
            using var extractionPhase = DebugLog.Phase("methods.nupkg-extraction");
            using var archive = ZipFile.OpenRead(nupkgPath);
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.EndsWith("/", StringComparison.OrdinalIgnoreCase) ||
                    entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase) ||
                    entry.FullName.StartsWith("package/", StringComparison.OrdinalIgnoreCase) ||
                    entry.FullName.StartsWith("_rels/", StringComparison.OrdinalIgnoreCase) ||
                    entry.FullName.StartsWith("[Content_Types].xml", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var entryExtension = Path.GetExtension(entry.FullName).ToLowerInvariant();
                if (!IsDotNetExtension(entryExtension)) continue;
                var destinationPath = Path.Combine(tempExtractionDir, entry.FullName);
                var destinationDir = Path.GetDirectoryName(destinationPath);
                if (!Directory.Exists(destinationDir))
                {
                    if (destinationDir is not null) Directory.CreateDirectory(destinationDir);
                }
                entry.ExtractToFile(destinationPath, overwrite: true);
            }
            return GetMethodsSlice(tempExtractionDir);

        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error processing NuGet package {nupkgPath}: {ex.Message}");
            throw; 
        }
        finally
        {
            if (tempExtractionDir is not null && Directory.Exists(tempExtractionDir))
            {
                try
                {
                    Directory.Delete(tempExtractionDir, recursive: true);
                }
                catch (Exception cleanupEx)
                {
                    Console.WriteLine($"Warning: Could not delete temporary directory {tempExtractionDir}: {cleanupEx.Message}");
                }
            }
        }
    }

    private static bool IsDotNetExtension(string extension)
    {
        var relevantExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Constants.AssemblyExtension, Constants.ExeExtension,
            Constants.CSharpSourceExtension, Constants.VBSourceExtension, Constants.FSharpSourceExtension,
            Constants.FSharpSignatureExtension, Constants.FSharpScriptExtension,
            Constants.RSourceExtension, ".rmd", ".qmd",
            Constants.CSourceExtension, Constants.CppSourceExtension, ".cc", ".cxx", Constants.CppHeaderExtension, ".hpp", ".hh"
        };
        return relevantExtensions.Contains(extension);
    }

    /// <summary>
    ///     Package URLs for every method, call, member, node and edge. Each item's purl depends only
    ///     on that item and the resolver's read-only tables, so the lists are resolved in chunks on
    ///     the worker team; millions of independent lookups were one thread's work.
    /// </summary>
    private static void EnrichPackageUrls(
        PackageUrlResolver resolver,
        List<Method> methods,
        List<Dependency> dependencies,
        List<MethodCalls> methodCalls,
        List<PropertyInfo> properties,
        List<FieldInfo> fields,
        List<EventInfo> events,
        List<ConstructorInfo> constructors,
        CallGraph callGraph,
        List<AssemblyInformation> assemblyInformation,
        List<SourceAssemblyMapping> sourceAssemblyMappings)
    {
        ForEachItem(methods, method => method.Purl = resolver.Resolve(method.Assembly, method.Module, method.SourceSignature ?? method.AssemblySignature, method.Namespace, method.ClassName));
        ForEachItem(dependencies, dependency => dependency.Purl = resolver.Resolve(dependency.Assembly, dependency.Module, dependency.Name, dependency.Namespace, null));
        ForEachItem(methodCalls, call =>
        {
            call.Purl = resolver.Resolve(call.Assembly, call.Module, call.TargetId ?? call.CalledMethod, call.Namespace, call.ClassName);
            // Unresolved call sites carry no assembly identity, so the first Resolve pass sees
            // only the bare call name. The namespace recovered from syntax (a using directive
            // or a qualified receiver) is the one fact that still maps to a package.
            if (call.Purl is null && call.EvidenceKind == AnalysisEvidenceKind.SourceUnresolved && !string.IsNullOrWhiteSpace(call.Namespace))
            {
                call.Purl = resolver.Resolve(namespaceName: call.Namespace);
            }
        });
        ForEachItem(properties, property => property.Purl = resolver.Resolve(property.Assembly, property.Module, property.TypeFullName, property.Namespace, property.ClassName));
        ForEachItem(fields, field => field.Purl = resolver.Resolve(field.Assembly, field.Module, field.TypeFullName, field.Namespace, field.ClassName));
        ForEachItem(events, @event => @event.Purl = resolver.Resolve(@event.Assembly, @event.Module, @event.TypeFullName, @event.Namespace, @event.ClassName));
        ForEachItem(constructors, constructor => constructor.Purl = resolver.Resolve(constructor.Assembly, constructor.Module, constructor.Name, constructor.Namespace, constructor.ClassName));
        ForEachItem(assemblyInformation, assembly => assembly.Purl = resolver.Resolve(assembly.Name, assembly.Name, assembly.Name, assembly.Name, assembly.Name));
        ForEachItem(callGraph.Nodes, node =>
        {
            node.Purl = resolver.Resolve(node.Assembly, node.Module, node.Id, node.Namespace, node.ClassName);
            // Unresolved-target nodes carry no assembly identity; the namespace recovered from
            // syntax is the one fact that still maps them to a package (same rule as calls).
            if (node.Purl is null && node.Id.StartsWith("Unresolved:", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(node.Namespace))
            {
                node.Purl = resolver.Resolve(namespaceName: node.Namespace);
            }
        });

        // In node order, so a repeated id keeps its last node's purl, as before.
        var nodePurls = new Dictionary<string, string?>(callGraph.Nodes.Count, StringComparer.Ordinal);
        foreach (var node in callGraph.Nodes)
        {
            nodePurls[node.Id] = node.Purl;
        }

        ForEachItem(callGraph.Edges, edge =>
        {
            edge.SourcePurl = nodePurls.GetValueOrDefault(edge.SourceId);
            edge.TargetPurl = nodePurls.GetValueOrDefault(edge.TargetId) ?? resolver.Resolve(null, null, edge.TargetId, null, edge.TargetName);
        });
        ForEachItem(sourceAssemblyMappings, mapping => mapping.Purl = resolver.Resolve(mapping.AssemblyName, mapping.ModuleName, mapping.AssemblyId ?? mapping.SourceId, mapping.Namespace, mapping.ClassName));

        static void ForEachItem<T>(List<T> items, Action<T> enrich)
        {
            const int chunk = 4096;
            DedicatedStack.ForEach("Dosai package urls", Math.Max(1, MaxSymbolAnalysisWorkers), (items.Count + chunk - 1) / chunk, index =>
            {
                var end = Math.Min(items.Count, (index + 1) * chunk);
                for (var item = index * chunk; item < end; item++)
                {
                    enrich(items[item]);
                }
            });
        }
    }

    private static void MergeCallGraph(CallGraph target, CallGraph source)
    {
        // First-wins node identity, same as the GroupBy/ToDictionary it replaces, without
        // allocating a grouping per node id.
        var nodesById = new Dictionary<string, MethodNode>(target.Nodes.Count, StringComparer.Ordinal);
        foreach (var node in target.Nodes)
        {
            nodesById.TryAdd(node.Id, node);
        }

        foreach (var node in source.Nodes)
        {
            if (!nodesById.TryGetValue(node.Id, out var existingNode))
            {
                target.Nodes.Add(node);
                nodesById[node.Id] = node;
            }
            else
            {
                MergeNodeEvidence(existingNode, node);
            }
        }

        // Struct keys instead of one concatenated string per edge: the merge runs over every
        // edge of both graphs, and at millions of edges the key strings alone were gigabytes.
        var edgeKeys = new HashSet<GraphAssembly.EdgeSiteKey>(target.Edges.Count, GraphAssembly.EdgeSiteKeyComparer.Instance);
        foreach (var edge in target.Edges)
        {
            edgeKeys.Add(GraphAssembly.EdgeSiteKey.FromEdge(edge));
        }

        foreach (var edge in source.Edges)
        {
            if (edgeKeys.Add(GraphAssembly.EdgeSiteKey.FromEdge(edge)))
            {
                target.Edges.Add(edge);
            }
        }

        GraphAssembly.SortNodesInPlace(target.Nodes);
        GraphAssembly.SortEdgesInPlace(target.Edges);
        GraphAssembly.AssignEdgeIds(target.Edges, "e");
    }

    private static AnalysisEvidenceKind ResolveCallEvidenceKind(MethodCalls call)
    {
        if (call.EvidenceKind != AnalysisEvidenceKind.Unknown)
        {
            return call.EvidenceKind;
        }

        return string.Equals(call.Module, "LanguageFrontend", StringComparison.Ordinal)
               || (string.Equals(call.Assembly, "Source", StringComparison.Ordinal) && call.TargetId?.StartsWith("external.", StringComparison.Ordinal) == true)
            ? AnalysisEvidenceKind.LanguageFrontend
            : AnalysisEvidenceKind.Unknown;
    }

    private static AnalysisEvidence CreateDefaultCallEvidence(MethodCalls call, AnalysisEvidenceKind evidenceKind) => new()
    {
        Kind = evidenceKind,
        Source = evidenceKind switch
        {
            AnalysisEvidenceKind.SourceRoslynDirect => "roslyn-source",
            AnalysisEvidenceKind.SourceUnresolved => "roslyn-source-unresolved",
            AnalysisEvidenceKind.LanguageFrontend => "language-frontend",
            _ => "unknown"
        },
        Description = evidenceKind switch
        {
            AnalysisEvidenceKind.SourceRoslynDirect => "Call edge discovered from source semantic operations.",
            AnalysisEvidenceKind.SourceUnresolved => "Call site discovered from syntax; the target assembly was not available to the compilation.",
            AnalysisEvidenceKind.LanguageFrontend => "Call edge discovered from language frontend source scanning.",
            _ => "Call edge discovered without producer-specific evidence metadata."
        },
        FileName = call.FileName,
        LineNumber = call.LineNumber,
        ColumnNumber = call.ColumnNumber
    };

    private static void MergeNodeEvidence(MethodNode target, MethodNode source)
    {
        foreach (var evidence in source.Evidence)
        {
            if (!target.Evidence.Any(item => item.Kind == evidence.Kind && item.Source == evidence.Source && item.FileName == evidence.FileName && item.LineNumber == evidence.LineNumber && item.ColumnNumber == evidence.ColumnNumber))
            {
                target.Evidence.Add(evidence);
            }
        }

        if (source.Identity is null)
        {
            return;
        }

        if (target.Identity is null)
        {
            target.Identity = CloneMethodIdentity(source.Identity);
            return;
        }

        MergeMethodIdentity(target.Identity, source.Identity);
    }

    private static MethodIdentity CloneMethodIdentity(MethodIdentity identity) => new()
    {
        Id = identity.Id,
        SourceSignature = identity.SourceSignature,
        AssemblySignature = identity.AssemblySignature,
        Symbol = identity.Symbol,
        AssemblyName = identity.AssemblyName,
        ModuleName = identity.ModuleName,
        Namespace = identity.Namespace,
        ClassName = identity.ClassName,
        MethodName = identity.MethodName,
        MetadataToken = identity.MetadataToken,
        Purl = identity.Purl,
        Evidence = identity.Evidence.Distinct().ToList()
    };

    private static void MergeMethodIdentity(MethodIdentity target, MethodIdentity source)
    {
        target.Id = FirstNonBlank(target.Id, source.Id);
        target.SourceSignature = FirstNonBlank(target.SourceSignature, source.SourceSignature);
        target.AssemblySignature = FirstNonBlank(target.AssemblySignature, source.AssemblySignature);
        target.Symbol = FirstNonBlank(target.Symbol, source.Symbol);
        target.AssemblyName = FirstNonBlank(target.AssemblyName, source.AssemblyName);
        target.ModuleName = FirstNonBlank(target.ModuleName, source.ModuleName);
        target.Namespace = FirstNonBlank(target.Namespace, source.Namespace);
        target.ClassName = FirstNonBlank(target.ClassName, source.ClassName);
        target.MethodName = FirstNonBlank(target.MethodName, source.MethodName);
        if (target.MetadataToken == 0) target.MetadataToken = source.MetadataToken;
        target.Purl = FirstNonBlank(target.Purl, source.Purl);
        foreach (var evidenceKind in source.Evidence.Where(kind => !target.Evidence.Contains(kind)))
        {
            target.Evidence.Add(evidenceKind);
        }
    }

    private static string? FirstNonBlank(string? preferred, string? fallback) => string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;

    internal static void NormalizeAssemblyGraphToSourceIds(List<MethodCalls> assemblyMethodCalls, CallGraph assemblyCallGraph, List<SourceAssemblyMapping> sourceAssemblyMappings)
    {
        var sourceIdByAssemblyId = sourceAssemblyMappings
            .Where(mapping => mapping.IsMapped && !string.IsNullOrWhiteSpace(mapping.SourceId) && !string.IsNullOrWhiteSpace(mapping.AssemblyId))
            .GroupBy(mapping => mapping.AssemblyId!, StringComparer.Ordinal)
            .ToDictionary(group => group.First().AssemblyId!, group => group.First().SourceId!, StringComparer.Ordinal);
        if (sourceIdByAssemblyId.Count == 0)
        {
            return;
        }

        // Instantiated-generic assembly ids (Method<args>) never match a source id exactly:
        // the instantiation rewrites parameter types too. Index the methods' identity key
        // (namespace.class.method, instantiation and parameters stripped) so instantiated IL nodes
        // normalize onto the source original-definition node, keeping the instantiation as metadata.
        var sourceIdByMethodIdentity = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (assemblyId, sourceId) in sourceIdByAssemblyId)
        {
            sourceIdByMethodIdentity.TryAdd(GraphIdNormalizer.MethodIdentityKey(assemblyId), sourceId);
        }

        string? Resolve(string id) =>
            sourceIdByAssemblyId.TryGetValue(id, out var exact) ? exact :
            GraphIdNormalizer.HasGenericInstantiation(id) && sourceIdByMethodIdentity.TryGetValue(GraphIdNormalizer.MethodIdentityKey(id), out var byIdentity) ? byIdentity :
            null;

        foreach (var call in assemblyMethodCalls)
        {
            if (call.SourceId is not null && Resolve(call.SourceId) is { } sourceId)
            {
                call.SourceId = sourceId;
            }
            if (call.TargetId is not null && Resolve(call.TargetId) is { } targetId)
            {
                call.TargetId = targetId;
            }
        }

        foreach (var edge in assemblyCallGraph.Edges)
        {
            if (Resolve(edge.SourceId) is { } edgeSourceId)
            {
                edge.SourceId = edgeSourceId;
            }
            if (Resolve(edge.TargetId) is { } edgeTargetId)
            {
                edge.TargetId = edgeTargetId;
            }
        }

        var normalizedNodes = new Dictionary<string, MethodNode>(StringComparer.Ordinal);
        foreach (var node in assemblyCallGraph.Nodes)
        {
            var originalNodeId = node.Id;
            var mappedSourceId = Resolve(originalNodeId);
            var normalizedId = mappedSourceId ?? originalNodeId;
            if (!normalizedNodes.TryGetValue(normalizedId, out var existing))
            {
                node.Id = normalizedId;
                if (node.Identity is not null)
                {
                    node.Identity.Id = normalizedId;
                    if (mappedSourceId is not null)
                    {
                        node.Identity.SourceSignature ??= normalizedId;
                        node.Identity.Symbol = normalizedId;
                    }
                }

                // The original instantiated id survives as metadata when normalization kept
                // an instantiated-generic IL node id or merged it onto a source definition.
                node.GenericInstantiation = GraphIdNormalizer.HasGenericInstantiation(originalNodeId) ? originalNodeId : null;
                normalizedNodes[normalizedId] = node;
                continue;
            }

            if (existing.GenericInstantiation is null && GraphIdNormalizer.HasGenericInstantiation(originalNodeId))
            {
                existing.GenericInstantiation = originalNodeId;
            }

            foreach (var evidence in node.Evidence)
            {
                if (!existing.Evidence.Any(item => item.Kind == evidence.Kind && item.Source == evidence.Source && item.FileName == evidence.FileName && item.LineNumber == evidence.LineNumber && item.ColumnNumber == evidence.ColumnNumber))
                {
                    existing.Evidence.Add(evidence);
                }
            }
            if (existing.Identity is not null && node.Identity is not null)
            {
                foreach (var evidenceKind in node.Identity.Evidence.Where(kind => !existing.Identity.Evidence.Contains(kind)))
                {
                    existing.Identity.Evidence.Add(evidenceKind);
                }
                existing.Identity.AssemblySignature ??= node.Identity.AssemblySignature;
                existing.Identity.Purl ??= node.Identity.Purl;
            }
        }

        var normalizedNodeList = normalizedNodes.Values.ToList();
        GraphAssembly.SortNodesInPlace(normalizedNodeList);
        assemblyCallGraph.Nodes = normalizedNodeList;
    }

    private static void EnrichMethodIdentities(List<Method> methods, CallGraph callGraph, bool sourceMode)
    {
        var methodIdentityById = new Dictionary<string, MethodIdentity>(StringComparer.Ordinal);
        foreach (var method in methods)
        {
            var evidenceKind = !string.IsNullOrWhiteSpace(method.SourceSignature)
                ? AnalysisEvidenceKind.SourceRoslynDirect
                : AnalysisEvidenceKind.AssemblyReflection;
            if (sourceMode && evidenceKind == AnalysisEvidenceKind.AssemblyReflection)
            {
                continue;
            }
            method.Identity = MethodIdentityFactory.FromMethod(method, evidenceKind);
            method.Identity.Purl = method.Purl;
            method.Evidence.Add(new AnalysisEvidence
            {
                Kind = evidenceKind,
                Source = evidenceKind == AnalysisEvidenceKind.SourceRoslynDirect ? "roslyn-source" : "assembly-metadata",
                Description = evidenceKind == AnalysisEvidenceKind.SourceRoslynDirect ? "Method discovered from source semantics." : "Method discovered from assembly metadata.",
                FileName = method.FileName,
                LineNumber = method.LineNumber,
                ColumnNumber = method.ColumnNumber
            });
            if (!string.IsNullOrWhiteSpace(method.Identity.Id)) methodIdentityById.TryAdd(method.Identity.Id!, method.Identity);
            if (!string.IsNullOrWhiteSpace(method.SourceSignature)) methodIdentityById.TryAdd(method.SourceSignature!, method.Identity);
            if (!string.IsNullOrWhiteSpace(method.AssemblySignature)) methodIdentityById.TryAdd(method.AssemblySignature!, method.Identity);
        }

        foreach (var node in callGraph.Nodes)
        {
            var existingIdentity = node.Identity;
            var existingEvidenceKinds = node.Identity?.Evidence.ToList() ?? [];
            existingEvidenceKinds.AddRange(node.Evidence.Select(evidence => evidence.Kind));
            if (!methodIdentityById.TryGetValue(node.Id, out var identity))
            {
                // Unresolved-target nodes keep the weakest evidence kind; summary evidence
                // would claim binding that never happened and mask the degraded analysis.
                var evidenceKind = node.IsExternal
                    ? node.Id.StartsWith("Unresolved:", StringComparison.Ordinal) ? AnalysisEvidenceKind.SourceUnresolved : AnalysisEvidenceKind.ExternalSummary
                    : AnalysisEvidenceKind.Unknown;
                identity = MethodIdentityFactory.FromParts(node.Id, null, node.Id, node.Id, node.Assembly, node.Module, node.Namespace, node.ClassName, node.Name, 0, node.Purl, evidenceKind);
            }
            var mergedIdentity = CloneMethodIdentity(identity);
            if (existingIdentity is not null)
            {
                MergeMethodIdentity(mergedIdentity, existingIdentity);
            }
            foreach (var evidenceKind in existingEvidenceKinds.Where(kind => !mergedIdentity.Evidence.Contains(kind)))
            {
                mergedIdentity.Evidence.Add(evidenceKind);
            }
            node.Identity = mergedIdentity;
            node.Identity.Purl ??= node.Purl;
            if (node.Evidence.Count == 0)
            {
                node.Evidence.Add(new AnalysisEvidence
                {
                    Kind = identity.Evidence.FirstOrDefault(),
                    Source = identity.Evidence.Contains(AnalysisEvidenceKind.SourceRoslynDirect) ? "roslyn-source" : identity.Evidence.Contains(AnalysisEvidenceKind.AssemblyReflection) ? "assembly-metadata" : "callgraph",
                    Description = "Call graph method node identity evidence.",
                    FileName = node.FileName,
                    LineNumber = node.LineNumber,
                    ColumnNumber = node.ColumnNumber
                });
            }
        }
    }
    
    /// <summary>
    /// Checks if a DLL is a managed .NET assembly by inspecting its PE header.
    /// This avoids trying to load native DLLs, which would throw a BadImageFormatException.
    /// </summary>
    /// <param name="filePath">The path to the DLL file.</param>
    /// <returns>True if the file is a managed assembly, false otherwise.</returns>
    private static bool IsManagedAssembly(string filePath)
    {
        try
        {
            // FileShare.Delete matches the other metadata readers: probing a file must never
            // stop the owning build from replacing or deleting it.
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var peReader = new PEReader(fs);
            return peReader is { HasMetadata: true, PEHeaders.CorHeader: not null };
        }
        catch
        {
            return false;
        }
    }
    
    /// <summary>
    ///     Shared-framework directories to probe for an inspected assembly's framework
    ///     references, ordered so the running runtime's own version is tried first and the
    ///     remaining installed versions newest-first.
    /// </summary>
    private static List<string> GetSharedFrameworkProbingPaths()
        => GetSharedFrameworkProbingPaths(
            System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(),
            GetDotnetSharedRuntimePaths());

    /// <summary>
    ///     Testable core of <see cref="GetSharedFrameworkProbingPaths" />: the runtime
    ///     directory and the shared roots reported by <c>dotnet --list-runtimes</c> are
    ///     supplied by the caller.
    /// </summary>
    /// <remarks>
    ///     Order is correctness, not a preference. These directories all contain a
    ///     `System.Runtime.dll`, one per installed framework version, and probing stops at the
    ///     first hit. Enumerating them in directory order meant the oldest installed framework
    ///     usually won, so types referencing anything newer failed to load and were dropped from
    ///     the inventory with only a warning - a machine with .NET 10 and 11 side by side
    ///     silently lost every C# 15 union type, because unions implement
    ///     `System.Runtime.CompilerServices.IUnion`, which exists only in 11's `System.Runtime`.
    ///     Newest-first resolves references from a superset framework instead, and the running
    ///     runtime leads because it is the one version guaranteed to be loadable in-process.
    /// </remarks>
    internal static List<string> GetSharedFrameworkProbingPaths(string runtimeDir, IEnumerable<string> dotnetSharedRuntimePaths)
    {
        var sharedRoots = new HashSet<string>(StringComparer.Ordinal);
        var runningSharedRoot = Path.GetFullPath(Path.Combine(runtimeDir, "..", ".."));
        if (Directory.Exists(runningSharedRoot))
        {
            sharedRoots.Add(runningSharedRoot);
        }

        foreach (var sharedRoot in dotnetSharedRuntimePaths.Where(Directory.Exists))
        {
            sharedRoots.Add(sharedRoot);
        }

        var versionDirectories = new List<string>();
        foreach (var frameworkRoot in from sharedRoot in sharedRoots
                 from frameworkName in new[] { "Microsoft.NETCore.App", "Microsoft.AspNetCore.App" }
                 select Path.Combine(sharedRoot, frameworkName))
        {
            if (Directory.Exists(frameworkRoot))
            {
                versionDirectories.AddRange(Directory.GetDirectories(frameworkRoot));
            }
        }

        // A self-contained deployment has no shared/<framework>/<version> layout: the runtime
        // sits directly in the application directory, and the `dotnet` resolved from PATH may
        // belong to an older machine install whose System.Runtime would then shadow the
        // bundled one (a self-contained .NET 11 dosai on a .NET 10-only machine loses every
        // C# 15 union type this way). When the runtime directory is itself a framework
        // directory, probe it too; for a framework-dependent install this is the running
        // version directory, which the ordering below already ranks first, so the duplicate
        // is harmless.
        if (File.Exists(Path.Combine(runtimeDir, "System.Runtime.dll")))
        {
            versionDirectories.Add(runtimeDir);
        }

        var runningVersionDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(runtimeDir));
        var runningVersion = Environment.Version;
        return versionDirectories
            .Select(directory => Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)))
            .Distinct(StringComparer.Ordinal)
            // A shared framework older than the running runtime is never a useful probe: its
            // System.Runtime shadows the one hosting this process and drops types the running
            // runtime understands. A single-file self-contained build keeps the bundled runtime
            // inside the executable (no loose System.Runtime.dll anywhere), and skipping the
            // older installs lets probing miss so the loader falls back to the Default context's
            // already-loaded bundled assemblies instead. The running directory itself is exempt:
            // a self-contained app directory is not named after a framework version.
            .Where(directory => string.Equals(directory, runningVersionDirectory, StringComparison.Ordinal)
                                || ParseFrameworkVersion(Path.GetFileName(directory)).Version >= runningVersion)
            .OrderByDescending(directory => string.Equals(directory, runningVersionDirectory, StringComparison.Ordinal))
            .ThenByDescending(directory => ParseFrameworkVersion(Path.GetFileName(directory)))
            .ToList();
    }

    /// <summary>
    ///     Version of a shared-framework directory name such as "11.0.0" or
    ///     "11.0.0-rc.1.26425.128". A prerelease sorts below the matching release, and an
    ///     unparseable name sorts last so it never displaces a known version.
    /// </summary>
    private static (Version Version, bool IsRelease) ParseFrameworkVersion(string directoryName)
    {
        var separatorIndex = directoryName.IndexOf('-', StringComparison.Ordinal);
        var numericPart = separatorIndex < 0 ? directoryName : directoryName[..separatorIndex];
        return Version.TryParse(numericPart, out var version)
            ? (version, separatorIndex < 0)
            : (new Version(0, 0), false);
    }

    private static readonly Lazy<HashSet<string>> DotnetSharedRuntimeRoots = new(ListDotnetSharedRuntimeRoots, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    ///     The shared roots (<c>.../dotnet/shared</c>) of every installed .NET runtime, from
    ///     <c>dotnet --list-runtimes</c>, run once per process.
    /// </summary>
    internal static IReadOnlyCollection<string> GetDotnetSharedRuntimeRoots() => DotnetSharedRuntimeRoots.Value;

    private static HashSet<string> GetDotnetSharedRuntimePaths() => DotnetSharedRuntimeRoots.Value;

    /// <summary>
    /// Discovers the paths of all installed .NET shared runtimes (like Microsoft.NETCore.App
    /// and Microsoft.AspNetCore.App) by executing 'dotnet --list-runtimes'.
    /// </summary>
    /// <returns>A HashSet of directory paths containing the shared runtime assemblies.</returns>
    private static HashSet<string> ListDotnetSharedRuntimeRoots()
    {
        var runtimePaths = new HashSet<string>();
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "--list-runtimes",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            // The output format is like: Microsoft.NETCore.App 7.0.14 [/usr/local/share/dotnet/shared/Microsoft.NETCore.App]
            var matches = Regex.Matches(output, @"\[(.*?)\]");
            foreach (Match match in matches)
            {
                if (match.Groups.Count <= 1) continue;
                var runtimeDir = Path.GetDirectoryName(match.Groups[1].Value);
                if (runtimeDir is not null && Directory.Exists(runtimeDir))
                {
                    runtimePaths.Add(runtimeDir);
                }
            }
        }
        catch (Exception ex)
        {
            // Not an error: a self-contained Dosai runs with no `dotnet` on PATH by design and
            // references its bundled runtime (FrameworkReferences). This used to print a red
            // "Error:" line to stdout on every such run, into the same stream a caller may be
            // reading JSON from.
            DebugLog.Log($"'dotnet --list-runtimes' unavailable, installed shared frameworks are not probed: {ex.Message}");
        }
        return runtimePaths;
    }
    
    /// <summary>
    /// Get all assembly information for the given path to assembly or directory of assemblies
    /// </summary>
    /// <param name="path">Filesystem path to assembly file or directory containing assembly files</param>
    /// <returns>List of assembly information</returns>
    private static List<AssemblyInformation> GetAssemblyInformation(string path)
    {
        var assembliesToInspect = AssemblyScope.ScopeApplicationAssemblies(path, GetFilesToInspect(path, Constants.AssemblyExtension, Constants.ExeExtension), message => Console.Error.WriteLine($"Warning: {message}"));
        List<AssemblyInformation> assemblyInformation = [];
        List<string> failedAssemblies = [];

        foreach(var assemblyFilePath in assembliesToInspect)
        {
            try
            {
                var fileName = Path.GetFileNameWithoutExtension(assemblyFilePath);

                if (assemblyInformation.Exists(item => item.Name == fileName))
                {
                    continue;
                }

                var fileVersionInfo = FileVersionInfo.GetVersionInfo(assemblyFilePath);
#pragma warning disable CS0472 // The result of the expression is always the same since a value of this type is never equal to 'null'
                var buildPart = $".{fileVersionInfo.FileBuildPart}";
                var privatePart = $".{fileVersionInfo.FilePrivatePart}";
#pragma warning restore CS0472 // The result of the expression is always the same since a value of this type is never equal to 'null'

                var assemblyInfo = new AssemblyInformation
                {
                    Name = fileName,
                    Version = $"{fileVersionInfo.FileMajorPart}.{fileVersionInfo.FileMinorPart}{buildPart}{privatePart}"
                };

                assemblyInformation.Add(assemblyInfo);
            }
            catch (Exception e) when (e is FileLoadException || e is FileNotFoundException || e is BadImageFormatException)
            {
                failedAssemblies.Add(assemblyFilePath);
            }
            catch (Exception e)
            {
                Console.WriteLine($"Unable to process {assemblyFilePath} due to: {e.Message}");
            }
        }

        return assemblyInformation;
    }

    /// <summary>
    /// Get all assembly methods for the given path to assembly or directory of assemblies
    /// </summary>
    /// <param name="path">Filesystem path to assembly file or directory containing assembly files</param>
    /// <returns>List of assembly methods</returns>
    /// <remarks>
    ///     Runs on a dedicated thread with <see cref="DedicatedStack.AnalysisStackSize" /> of stack.
    ///     The runtime type loader resolves a type's base chain recursively, and when a base
    ///     type fails to load - a build-output assembly whose dependency is not shipped next to
    ///     it - native exception handling amplifies the stack used per level
    ///     (dotnet/runtime#131679). On the default main-thread stack (1 MB on Windows, 8 MB on
    ///     Linux and macOS) a deep enough hierarchy overflows inside <c>Assembly.GetTypes()</c>,
    ///     which terminates the process: a stack overflow cannot be caught. The larger stack
    ///     leaves the loader and its output untouched and moves that limit far beyond the
    ///     hierarchy depth of real libraries.
    /// </remarks>
    private static List<Method> GetAssemblyMethods(string path, ICollection<string> diagnostics)
        => DedicatedStack.Run("Dosai assembly inspection", () => InspectAssemblyMethods(path, diagnostics));

    private static List<Method> InspectAssemblyMethods(string path, ICollection<string> diagnostics)
    {
        var candidateAssemblies = GetFilesToInspect(path, Constants.AssemblyExtension, Constants.ExeExtension);
        var assembliesToInspect = AssemblyScope.ScopeApplicationAssemblies(path, candidateAssemblies, message => Console.Error.WriteLine($"Warning: {message}"));
        if (DebugLog.Enabled)
        {
            // Re-derive the scoping basis (deps.json project libraries vs the name heuristic)
            // purely for the debug line; the scoping decision itself was made above.
            var applicationAssemblyNames = AssemblyScope.GetApplicationAssemblyNames(path);
            var scopeReason = File.Exists(path)
                ? "single-file input; no directory scoping"
                : candidateAssemblies.Count == 0
                    ? "no assembly candidates"
                    : applicationAssemblyNames.Count > 0
                        ? $"matched project libraries in deps.json ({applicationAssemblyNames.Count} project name(s))"
                        : "heuristic name filter (System./Microsoft./Newtonsoft./FSharp./Humanizer prefixes; no deps.json project libraries)";
            DebugLog.Log($"assembly scoping: kept {assembliesToInspect.Count}, dropped {candidateAssemblies.Count - assembliesToInspect.Count} of {candidateAssemblies.Count} candidates ({scopeReason})");
        }
        var assemblyMethods = new List<Method>();
        var processedAssemblyIdentities = new HashSet<string>();
        var sharedFrameworkDirs = GetSharedFrameworkProbingPaths();
        // The first open of a file is where a cold machine waits: real-time antivirus scans a
        // file on its first access (Windows Defender: ~60 ms per DLL), which left this phase at a
        // fifth of one core in the issue #65 run. The managed-assembly check is
        // that first open and a pure function of the file, so it runs on the worker team up
        // front; the scans overlap, and the loop below reads warm files in its usual order.
        var isManaged = new bool[assembliesToInspect.Count];
        DedicatedStack.ForEach("Dosai assembly probe", Math.Max(1, MaxSymbolAnalysisWorkers), isManaged.Length, index => isManaged[index] = IsManagedAssembly(assembliesToInspect[index]));
        for (var assemblyIndex = 0; assemblyIndex < assembliesToInspect.Count; assemblyIndex++)
        {
            var assemblyFilePath = assembliesToInspect[assemblyIndex];
            var fileName = Path.GetFileName(assemblyFilePath);
            if (!isManaged[assemblyIndex])
            {
                Console.WriteLine($"Info: Skipping native library or non-assembly file: {assemblyFilePath}");
                continue;
            }
            var inspectedDirs = new List<string> { Path.GetDirectoryName(assemblyFilePath)!, Path.GetDirectoryName(path)! };
            var loadContext = new InspectionAssemblyLoadContext(inspectedDirs, sharedFrameworkDirs);
            // Per-assembly timing exists only for the debug log; the stopwatch is not created
            // when debug is off, so the inspection loop pays nothing.
            var assemblyWatch = DebugLog.Enabled ? Stopwatch.StartNew() : null;
            var membersBefore = assemblyMethods.Count;
            try
            {
                var assemblyName = AssemblyName.GetAssemblyName(assemblyFilePath);
                if (processedAssemblyIdentities.Contains(assemblyName.FullName))
                {
                    continue;
                }
                var assembly = loadContext.LoadFromAssemblyName(assemblyName);
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    Console.WriteLine($"Warning: Could not load all types from {fileName}. Some types will be skipped.");
                    if (ex.LoaderExceptions is not null)
                    {
                        var uniqueLoaderErrors = ex.LoaderExceptions
                            .Where(e => e is not null)
                            .Select(e => e?.Message)
                            .Distinct();

                        foreach (var errorMessage in uniqueLoaderErrors)
                        {
                            Console.WriteLine($"  - {errorMessage}");
                            if (errorMessage is null ||
                                !errorMessage.Contains("The system cannot find the file specified")) continue;
                            Console.WriteLine("    Suggestion: This error often means a .NET Shared Framework is missing. Ensure the machine running this analysis has the necessary .NET SDKs and Runtimes (e.g., ASP.NET Core Runtime) installed. Some projects might require Windows for building.");
                        }
                    }
                    types = ex.Types.Where(t => t is not null).ToArray()!;
                }

                foreach (var type in types)
                {
                    foreach (var method in type.GetMethods())
                    {
                        if ($"{method.Module.Assembly.GetName().Name}{Constants.AssemblyExtension}" != fileName) continue;

                        var parameters = method.GetParameters().Select(p => p.ParameterType.FullName ?? p.ParameterType.Name).ToList();
                        var paramString = string.Join(",", parameters);
                        var returnType = method.ReturnType.FullName ?? method.ReturnType.Name;
                        var className = method.DeclaringType?.Name ?? "UnknownType";
                        var ns = method.DeclaringType?.Namespace ?? "";
                        var assemblySignature = $"{ns}.{className}.{method.Name}({paramString}):{returnType}";
                        if (method.Name is ".ctor" or ".cctor")
                        {
                            assemblySignature = $"{ns}.{className}.{method.Name}({paramString})";
                        }

                        var methodParams = method.GetParameters().Select(p => new Parameter
                        {
                            Name = p.Name,
                            Type = p.ParameterType.FullName ?? p.ParameterType.Name,
                            TypeFullName = p.ParameterType.FullName ?? p.ParameterType.Name,
                            IsGenericParameter = p.ParameterType.IsGenericParameter
                        }).ToList();

                        var genericParameters = method.IsGenericMethodDefinition
                            ? method.GetGenericArguments().Select(t => t.Name).ToList()
                            : [];

                        assemblyMethods.Add(CreateMethodObjectFromMember(
                            method, assemblyFilePath, fileName, method.Attributes.ToString(), method.Name, returnType,
                            methodParams, method.MetadataToken, assemblySignature,
                            method.IsGenericMethod, method.IsGenericMethodDefinition, genericParameters
                        ));
                    }
                    processedAssemblyIdentities.Add(assembly.FullName!);
                    assemblyMethods.AddRange(from ctor in type.GetConstructors() where $"{ctor.Module.Assembly.GetName().Name}{Constants.AssemblyExtension}" == fileName let ctorParams = ctor.GetParameters().Select(p => new Parameter { Name = p.Name, Type = p.ParameterType.FullName }).ToList() let assemblySignature = $"{ctor.DeclaringType?.Name}" select CreateMethodObjectFromMember(ctor, assemblyFilePath, fileName, ctor.Attributes.ToString(), ".ctor", "Void", ctorParams, ctor.MetadataToken, assemblySignature));
                    assemblyMethods.AddRange(from prop in type.GetProperties() where $"{prop.Module.Assembly.GetName().Name}{Constants.AssemblyExtension}" == fileName select CreateMethodObjectFromMember(prop, assemblyFilePath, fileName, "Property", prop.Name, prop.PropertyType.Name, []));
                    assemblyMethods.AddRange(from field in type.GetFields() where $"{field.Module.Assembly.GetName().Name}{Constants.AssemblyExtension}" == fileName select CreateMethodObjectFromMember(field, assemblyFilePath, fileName, field.Attributes.ToString(), field.Name, field.FieldType.Name, []));
                    assemblyMethods.AddRange(from evt in type.GetEvents() where $"{evt.Module.Assembly.GetName().Name}{Constants.AssemblyExtension}" == fileName select CreateMethodObjectFromMember(evt, assemblyFilePath, fileName, evt.Attributes.ToString(), evt.Name, evt.EventHandlerType?.Name ?? string.Empty, []));
                }
            }
            catch (Exception e) when (e is FileLoadException or FileNotFoundException or BadImageFormatException or TypeLoadException or NotSupportedException)
            {
                if (DebugLog.Enabled)
                {
                    DebugLog.Log($"assembly '{fileName}' failed to load: {e.GetType().Name}: {e.Message}");
                }
                Console.WriteLine($"Warning: Skipping assembly {assemblyFilePath} as it could not be fully loaded for inspection.");
                Console.WriteLine($"  - Reason: {e.GetType().Name}: {e.Message}");
            }
            catch (Exception e)
            {
                if (DebugLog.Enabled)
                {
                    DebugLog.Log($"assembly '{fileName}' failed to load: {e.GetType().Name}: {e.Message}");
                }
                Console.WriteLine($"Error: An unexpected error occurred while processing {fileName}. Details: {e.Message}");
            }
            finally
            {
                loadContext.Unload();
                if (assemblyWatch is not null)
                {
                    assemblyWatch.Stop();
                    // Only assemblies that took over a second get a line; per-assembly output is
                    // otherwise noise on large trees.
                    if (assemblyWatch.Elapsed.TotalSeconds >= 1)
                    {
                        DebugLog.Log(string.Create(CultureInfo.InvariantCulture, $"assembly '{fileName}': {assemblyMethods.Count - membersBefore} members in {assemblyWatch.Elapsed.TotalSeconds:F3}s"));
                    }
                }
            }
        }

        return assemblyMethods;

        Method CreateMethodObjectFromMember(
            MemberInfo member, string filePath, string file, string attributes, string name, string returnType,
            List<Parameter> parameters, int metadataToken = 0, string? assemblySignature = null,
            bool isGeneric = false, bool isGenericDef = false, List<string>? genericParams = null)
        {
            var typ = member.DeclaringType;
            var baseType = typ?.BaseType?.Name;
            var implementedInterfaces = typ?.GetInterfaces().Select(i => i.Name).ToList() ?? [];

            return new Method
            {
                Path = filePath,
                FileName = file,
                Module = typ?.Module.ToString(),
                Namespace = typ?.Namespace,
                ClassName = typ?.Name ?? string.Empty,
                Attributes = attributes,
                Name = name,
                ReturnType = returnType,
                Parameters = parameters,
                CustomAttributes = ExtractCustomAttributes(member, diagnostics),
                BaseType = baseType,
                ImplementedInterfaces = implementedInterfaces,
                MetadataToken = metadataToken,
                AssemblySignature = assemblySignature,
                IsGenericMethod = isGeneric,
                IsGenericMethodDefinition = isGenericDef,
                GenericParameters = genericParams ?? []
            };
        }
    }

    private static string GetContainingTypeNameVb(Microsoft.CodeAnalysis.VisualBasic.Syntax.FieldDeclarationSyntax member)
    {
        var parent = member.Parent;
        while (parent is not null)
        {
            if (parent is ClassBlockSyntax classBlock)
                return classBlock.ClassStatement.Identifier.Text;
            if (parent is StructureBlockSyntax structBlock)
                return structBlock.StructureStatement.Identifier.Text;
            parent = parent.Parent;
        }
        return "";
    }
    
    /// <summary>
    /// Get all F# methods for the given path to F# source or directory of F# source
    /// </summary>
    /// <param name="path">Filesystem path to F# source file or directory containing F# source files</param>
    /// <returns>Tuple with List of F# methods, dependencies, and method calls</returns>
    private static (List<Method>, List<Dependency>, List<MethodCalls>) GetFSharpMethods(string path)
    {
        var sourcesToInspect = GetFilesToInspect(path, Constants.FSharpSourceExtension);
        var fsharpMethods = new List<Method>();
        var fsharpDependencies = new List<Dependency>();
        var fsharpMethodCalls = new List<MethodCalls>();

        foreach (var sourceFilePath in sourcesToInspect)
        {
            try
            {
                var fileName = Path.GetFileName(sourceFilePath);
                var fileContent = File.ReadAllText(sourceFilePath);
                var lines = fileContent.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                
                string currentModule = "Global";
                string currentType = "";
                
                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i].Trim();
                    var lineNumber = i + 1;
                    
                    // Extract module declarations
                    var moduleMatch = FSharpRegex.Module().Match(line);
                    if (moduleMatch.Success)
                    {
                        currentModule = moduleMatch.Groups[1].Value;
                    }
                    
                    // Extract type declarations
                    var typeMatch = FSharpRegex.Type().Match(line);
                    if (typeMatch.Success)
                    {
                        currentType = typeMatch.Groups[1].Value;
                    }
                    
                    // Extract function declarations: "let functionName" or "let rec functionName"
                    var functionMatch = FSharpRegex.Function().Match(line);
                    if (functionMatch.Success)
                    {
                        var functionName = functionMatch.Groups[2].Value;
                        // Skip common F# keywords that might match the pattern
                        if (functionName is "rec" or "in" or "and") 
                            continue;
                        // Determine the containing context
                        var containingContext = !string.IsNullOrEmpty(currentType) ? currentType : currentModule;
                        
                        fsharpMethods.Add(new Method
                        {
                            Path = Path.GetRelativePath(path, sourceFilePath),
                            FileName = fileName,
                            Name = functionName,
                            ClassName = containingContext,
                            Namespace = "Unknown",
                            Assembly = "Unknown",
                            Module = "Unknown",
                            Attributes = "Public",
                            ReturnType = "Unknown",
                            LineNumber = lineNumber,
                            ColumnNumber = line.IndexOf(functionName, StringComparison.Ordinal) + 1,
                            Parameters = [],
                            CustomAttributes = []
                        });
                    }
                    // Extract member declarations: "member this.MemberName" or "member _.MemberName"
                    var memberMatch = FSharpRegex.Member().Match(line);
                    if (memberMatch.Success)
                    {
                        var memberName = memberMatch.Groups[2].Value;
                        string containingType = !string.IsNullOrEmpty(currentType) ? currentType : "Unknown";
                        
                        fsharpMethods.Add(new Method
                        {
                            Path = Path.GetRelativePath(path, sourceFilePath),
                            FileName = fileName,
                            Name = memberName,
                            ClassName = containingType,
                            Namespace = "Unknown",
                            Assembly = "Unknown",
                            Module = "Unknown",
                            Attributes = "Public",
                            ReturnType = "Unknown",
                            LineNumber = lineNumber,
                            ColumnNumber = line.IndexOf(memberName, StringComparison.Ordinal) + 1,
                            Parameters = [],
                            CustomAttributes = []
                        });
                    }
                    // Extract constructor declarations: "new(args) ="
                    var constructorMatch = FSharpRegex.Constructor().Match(line);
                    if (constructorMatch.Success)
                    {
                        string containingType = !string.IsNullOrEmpty(currentType) ? currentType : "Unknown";
                        
                        fsharpMethods.Add(new Method
                        {
                            Path = Path.GetRelativePath(path, sourceFilePath),
                            FileName = fileName,
                            Name = ".ctor",
                            ClassName = containingType,
                            Namespace = "Unknown",
                            Assembly = "Unknown",
                            Module = "Unknown",
                            Attributes = "Public",
                            ReturnType = "Void",
                            LineNumber = lineNumber,
                            ColumnNumber = line.IndexOf("new", StringComparison.Ordinal) + 1,
                            Parameters = [],
                            CustomAttributes = []
                        });
                    }
                }
                // Extract dependencies (open statements)
                var openMatches = FSharpRegex.Open().Matches(fileContent);
                foreach (Match match in openMatches)
                {
                    var namespaceName = match.Groups[1].Value;
                    var lineIndex = fileContent.Substring(0, match.Index).Split('\n').Length;
                    
                    fsharpDependencies.Add(new Dependency
                    {
                        Path = Path.GetRelativePath(path, sourceFilePath),
                        FileName = fileName,
                        Name = namespaceName,
                        Namespace = namespaceName,
                        Assembly = "Unknown",
                        Module = "Unknown",
                        LineNumber = lineIndex,
                        ColumnNumber = match.Index - fileContent.LastIndexOf('\n', match.Index) + 1,
                        NamespaceMembers = []
                    });
                }
                // Extract method calls (function calls with parentheses)
                var callMatches = FSharpRegex.MethodCall().Matches(fileContent);
                foreach (Match match in callMatches)
                {
                    var methodName = match.Groups[1].Value;
                    // Skip common keywords
                    if (IsFSharpKeyword(methodName))
                        continue;
                    
                    var lineIndex = fileContent.Substring(0, match.Index).Split('\n').Length;
                    
                    fsharpMethodCalls.Add(new MethodCalls
                    {
                        Path = Path.GetRelativePath(path, sourceFilePath),
                        FileName = fileName,
                        CalledMethod = methodName,
                        ClassName = "Unknown",
                        Namespace = "Unknown",
                        Assembly = "Unknown",
                        Module = "Unknown",
                        LineNumber = lineIndex,
                        ColumnNumber = match.Index - fileContent.LastIndexOf('\n', match.Index) + 1,
                        Arguments = []
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing F# file {sourceFilePath}: {ex.Message}");
            }
        }

        return (fsharpMethods, fsharpDependencies, fsharpMethodCalls);
    }

    private static bool IsFSharpKeyword(string word)
    {
        var keywords = new HashSet<string>
        {
            "if", "then", "else", "elif", "for", "while", "do", "match", "with", "try", 
            "catch", "finally", "let", "rec", "and", "fun", "function", "in", "open",
            "module", "type", "exception", "namespace", "assembly", "begin", "end",
            "abstract", "default", "delegate", "enum", "extern", "fixed", "interface",
            "internal", "lazy", "mutable", "new", "null", "override", "private", 
            "protected", "public", "return", "static", "to", "true", "upcast", "use",
            "virtual", "void", "when", "yield"
        };
        
        return keywords.Contains(word);
    }

    /// <summary>
    ///     Title-cases modifiers and parameter type names through the invariant culture so the
    ///     JSON is identical on every machine: <c>CultureInfo.CurrentCulture.TextInfo</c> applies
    ///     the running locale's casing rules, and a Turkish locale turned "internal" into
    ///     "İnternal" and "int" into "İnt" (issue #63).
    /// </summary>
    private static string TitleCase(string value) => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value);

    /// <summary>
    ///     Upper bound on the parallel symbol-analysis workers (issue #65): the per-file loop and
    ///     the dispatch index's object-creation scan. Defaults to one worker per processor;
    ///     <c>DOSAI_SYMBOL_ANALYSIS_WORKERS</c> overrides it for memory-constrained hosts, since
    ///     each worker holds one file's semantic model at a time. Each worker reserves a large
    ///     analysis stack, so the bound also bounds reserved address space. Internal and mutable
    ///     so tests can pin the count and compare a parallel run against a sequential one.
    /// </summary>
    internal static int MaxSymbolAnalysisWorkers { get; set; } = ResolveDefaultSymbolAnalysisWorkers();

    private static int ResolveDefaultSymbolAnalysisWorkers()
    {
        var raw = Environment.GetEnvironmentVariable("DOSAI_SYMBOL_ANALYSIS_WORKERS");
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0 ? parsed : Environment.ProcessorCount;
    }

    /// <summary>
    ///     Per-file symbol-analysis output. The per-file body of
    ///     <see cref="GetSourceMethods" /> fills one collector per file on whichever worker owns
    ///     it, and the collectors merge in file order afterwards, keeping the aggregated output
    ///     byte-identical to a sequential run (issue #65).
    /// </summary>
    /// <summary>
    ///     Under --debug, the compilation's declaration errors (issue #69): the total, how many
    ///     files carry one, and the full histogram by diagnostic id - a tree whose names mostly
    ///     fail to resolve is otherwise indistinguishable from a healthy one. One
    ///     compilation-wide pass after the symbol loop: a per-tree request completes the whole
    ///     assembly under a location filter each time, which is quadratic in the file count.
    ///     Only under --debug, because even the single pass is costly on a large unbuilt tree and
    ///     --debug never changes the JSON. Roslyn's declaration completion can itself throw on
    ///     hostile input (an InvalidCastException on the dotnet/runtime tree); that is logged,
    ///     never fatal.
    /// </summary>
    private static void LogDeclarationErrors(Compilation compilation, bool hasTrees)
    {
        if (!hasTrees)
        {
            return;
        }

        var language = compilation.Language;
        using var phase = DebugLog.Phase($"methods.declaration-diagnostics ({language})");
        var errors = new Dictionary<string, int>(StringComparer.Ordinal);
        var files = new HashSet<SyntaxTree>();
        try
        {
            foreach (var diagnostic in compilation.GetDeclarationDiagnostics())
            {
                if (diagnostic.Severity == DiagnosticSeverity.Error)
                {
                    errors[diagnostic.Id] = errors.GetValueOrDefault(diagnostic.Id) + 1;
                    if (diagnostic.Location.SourceTree is { } tree)
                    {
                        files.Add(tree);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            // Roslyn 5.9 throws InvalidCastException from
            // SourceNamedTypeSymbol.GetCorrespondingBaseListLocation when top-level statements
            // and a `partial class Program : ISomething` that leaves a member unimplemented meet
            // in one compilation: the synthesized Program's declaration is a compilation unit.
            var inner = ex is AggregateException aggregate ? aggregate.Flatten().InnerExceptions.FirstOrDefault() ?? ex : ex;
            var frame = inner.StackTrace?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            DebugLog.Log($"{language} declaration diagnostics failed inside the compiler, no histogram: {inner.GetType().Name}: {inner.Message} {frame}");
            return;
        }

        DebugLog.Count($"{language} source declaration errors", errors.Values.Sum());
        DebugLog.Count($"{language} source files with declaration errors", files.Count);
        DebugLog.Count($"{language} compiled syntax trees", compilation.SyntaxTrees.Count());
        if (errors.Count > 0)
        {
            DebugLog.Log($"{language} source declaration errors by id: " + string.Join(", ", errors
                .OrderByDescending(entry => entry.Value)
                .ThenBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => string.Create(CultureInfo.InvariantCulture, $"{entry.Key} {entry.Value}"))));
        }
    }

    private sealed class SourceFileSymbols
    {
        public List<Method> Methods { get; } = [];
        public List<Dependency> UsingDirectives { get; } = [];
        public List<MethodCalls> MethodCalls { get; } = [];
        public List<PropertyInfo> Properties { get; } = [];
        public List<FieldInfo> Fields { get; } = [];
        public List<EventInfo> Events { get; } = [];
        public List<ConstructorInfo> Constructors { get; } = [];
        public List<string> Diagnostics { get; } = [];
        public int AbandonedDispatchSites { get; set; }
        public string? FirstAbandonedDispatch { get; set; }
    }

    private static Method CreateMethodFromSymbol(
        IMethodSymbol methodSymbol,
        SemanticModel model,
        string relativePath,
        string fileName,
        int lineNumber,
        int columnNumber,
        List<string> diagnostics,
        SourceRenderCache renderCache)
    {
        if (lineNumber == 0 || columnNumber == 0)
        {
            var location = methodSymbol.Locations.FirstOrDefault();
            var lineSpan = location?.GetLineSpan();
            lineNumber = lineSpan?.StartLinePosition.Line + 1 ?? lineNumber;
            columnNumber = lineSpan?.Span.Start.Character + 1 ?? columnNumber;
        }

        var containingType = methodSymbol.ContainingType;
        var containingNamespace = methodSymbol.ContainingNamespace;
        var assembly = methodSymbol.ContainingAssembly;
        var module = methodSymbol.ContainingModule;

        var modifiers = new List<string>();
        if (methodSymbol.DeclaredAccessibility.HasFlag(Accessibility.Public)) modifiers.Add("Public");
        if (methodSymbol.DeclaredAccessibility.HasFlag(Accessibility.Private)) modifiers.Add("Private");
        if (methodSymbol.DeclaredAccessibility.HasFlag(Accessibility.Protected)) modifiers.Add("Protected");
        if (methodSymbol.DeclaredAccessibility.HasFlag(Accessibility.Internal)) modifiers.Add("Internal");
        if (methodSymbol.IsStatic) modifiers.Add("Static");
        if (methodSymbol.IsVirtual) modifiers.Add("Virtual");
        if (methodSymbol.IsOverride) modifiers.Add("Override");

        var isGenericMethod = methodSymbol.IsGenericMethod;
        var genericParameters = methodSymbol.TypeParameters.Select(tp => tp.Name).ToList();

        var metadataToken = 0;
        if (assembly is not null && SymbolEqualityComparer.Default.Equals(assembly, model.Compilation.Assembly))
        {
            metadataToken = methodSymbol.MetadataToken;
        }

        string sourceSignature = renderCache.Signature(methodSymbol);
        string assemblySignature = renderCache.Display(methodSymbol);

        var baseType = containingType?.BaseType?.Name ?? "Object";
        var implementedInterfaces = renderCache.InterfaceNames(containingType) ?? [];

        return new Method
        {
            Path = relativePath,
            FileName = fileName,
            Assembly = assembly is null ? "" : renderCache.Display(assembly),
            Module = module is null ? "" : renderCache.Display(module),
            Namespace = containingNamespace is null ? "" : renderCache.Display(containingNamespace),
            ClassName = GetNamedContainingTypeName(methodSymbol),
            Attributes = TitleCase(string.Join(", ", modifiers)),
            Name = methodSymbol.Name,
            ReturnType = methodSymbol.ReturnType.ToDisplayString(),
            LineNumber = lineNumber,
            ColumnNumber = columnNumber,
            Parameters = methodSymbol.Parameters.Select(p => new Parameter
            {
                Name = p.Name,
                Type = p.Type.ToDisplayString(),
                TypeFullName = p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                IsGenericParameter = p.Type is ITypeParameterSymbol
            }).ToList(),
            CustomAttributes = ExtractCustomAttributes(methodSymbol, diagnostics),
            BaseType = baseType,
            ImplementedInterfaces = implementedInterfaces,
            MetadataToken = metadataToken,
            SourceSignature = sourceSignature,
            AssemblySignature = assemblySignature,
            IsGenericMethod = isGenericMethod,
            GenericParameters = genericParameters,
        };
    }
    
    /// <summary>
    /// Get all source methods for the given path to C# source or directory of C# source
    /// </summary>
    /// <param name="path">Filesystem path to C# source file or directory containing C# source files</param>
    /// <param name="assemblyMethods">List of assembly methods</param>
    /// <returns>Tuple with List of source methods and using directives</returns>
    private static (List<Method> SourceMethods, List<Dependency> UsingDirectives, List<MethodCalls> MethodCalls, List<PropertyInfo> Properties, List<FieldInfo> Fields, List<EventInfo> Events, List<ConstructorInfo> Constructors, CallGraph CallGraph, List<SourceAssemblyMapping> SourceAssemblyMappings, bool SourceMode, Frameworks.SourceCompilations Compilations, List<string> Diagnostics) GetSourceMethods(string path, List<Method> assemblyMethods)
    {
        var assembliesToInspect = GetFilesToInspect(path, Constants.AssemblyExtension, Constants.ExeExtension);
        var sourcesToInspect = GetFilesToInspect(path, Constants.CSharpSourceExtension);
        sourcesToInspect.AddRange(GetFilesToInspect(path, Constants.VBSourceExtension));
        sourcesToInspect.AddRange(GetFilesToInspect(path, Constants.FSharpSourceExtension));
        var sourceMode = sourcesToInspect.Count > 0;
        // Aggregated across all files (and all parallel workers) after the per-file loop; the
        // per-file locals inside AnalyzeSourceFile keep their historical names.
        var mergedMethods = new List<Method>();
        var mergedUsings = new List<Dependency>();
        var mergedMethodCalls = new List<MethodCalls>();
        var mergedProperties = new List<PropertyInfo>();
        var mergedFields = new List<FieldInfo>();
        var mergedEvents = new List<EventInfo>();
        var mergedConstructors = new List<ConstructorInfo>();
        var sourceAssemblyMappings = new List<SourceAssemblyMapping>();
        // Per-symbol attribute-extraction failures (issue #56 containment) collect here and
        // surface in MethodsSlice.Diagnostics instead of aborting the scan.
        var mergedDiagnostics = new List<string>();
        var dispatchIndexes = new Dictionary<Compilation, DispatchResolver.SourceIndex>();
        var metadataReferences = new Dictionary<string, PortableExecutableReference>(StringComparer.OrdinalIgnoreCase);
        // Framework references: the host's trusted platform assemblies, a self-contained
        // bundle's own runtime, or the newest installed shared framework (issue #67).
        var frameworkReferences = FrameworkReferences.Current;
        foreach (var (key, reference) in frameworkReferences.References)
        {
            metadataReferences.TryAdd(key, reference);
        }

        DebugLog.Count($"framework metadata references ({frameworkReferences.Source})", frameworkReferences.References.Count);
        foreach (var externalAssembly in assembliesToInspect.Where(IsManagedAssembly))
        {
            metadataReferences.TryAdd(externalAssembly, MetadataReference.CreateFromFile(externalAssembly));
        }
        // Restored-but-unbuilt trees: packageFolders plus the per-target compile entries in
        // project.assets.json name the package DLLs inside the NuGet cache, so semantic
        // binding no longer depends on bin/ output. Cache references load from bytes so the
        // shared packages folder is never locked for the process lifetime.
        foreach (var cacheAssembly in NuGetRestoreCache.GetReferencePaths(path, metadataReferences.Keys))
        {
            if (NuGetRestoreCache.TryCreateUnpinnedReference(cacheAssembly) is { } cacheReference)
            {
                metadataReferences.TryAdd(cacheAssembly, cacheReference);
            }
        }

        var referenceList = metadataReferences.Values.ToList();
        DebugLog.Count("roslyn metadata references", referenceList.Count);
        // Parsing dominates wall time on large trees and is embarrassingly parallel - one
        // tree per file, no shared state - so the read+parse pair runs on the same worker
        // team the symbol loop uses. Results land by index, so tree order (and therefore
        // every downstream order) stays the file order a sequential parse produced.
        var csharpSources = sourcesToInspect
            .Where(source => Path.GetExtension(source).Equals(Constants.CSharpSourceExtension, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var parsedCsharpTrees = new CSharpSyntaxTree?[csharpSources.Length];
        var parsedClassifications = new ReferenceSources.Classification?[csharpSources.Length];
        using (DebugLog.Phase("methods.parse-csharp"))
        {
            DedicatedStack.ForEach("Dosai parse csharp", Math.Max(1, MaxSymbolAnalysisWorkers), csharpSources.Length, index =>
            {
                if (SafeFileRead.TryReadAllText(csharpSources[index], out var content))
                {
                    var tree = CSharpSourceParser.Parse(content, csharpSources[index], path);
                    parsedCsharpTrees[index] = tree;
                    parsedClassifications[index] = ReferenceSources.Classify(tree);
                }
            });
        }

        // API-surface stubs compiled beside their implementation declare every member twice
        // and make binding nondeterministic (issue #69); they stay out of the compilation.
        var partition = ReferenceSources.Partition(
            parsedCsharpTrees.OfType<CSharpSyntaxTree>().ToList(),
            parsedClassifications.OfType<ReferenceSources.Classification>().ToList());
        var csharpTrees = partition.Kept;
        DebugLog.Count("reference-assembly sources skipped", partition.Skipped.Count);
        DebugLog.Count("reference-assembly sources trimmed", partition.Trimmed.Count);
        mergedDiagnostics.AddRange(ReferenceSources.Diagnostics(partition, TargetFrameworkDetection.ProjectContextRoot(Path.GetFullPath(path))));
        // Implicit-usings projects rely on global usings their compiler injects; without the
        // synthetic tree every BCL call in them fails to bind and vanishes from the graph.
        if (CSharpSourceParser.TryCreateImplicitUsingsTree(path) is { } implicitUsingsTree)
        {
            csharpTrees.Insert(0, implicitUsingsTree);
        }
        var csharpCompilation = CSharpCompilation.Create(
            "Dosai.SourceAnalysis.CSharp",
            syntaxTrees: csharpTrees,
            references: referenceList,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        DebugLog.Count("csharp syntax trees", csharpTrees.Count);
        var vbSources = sourcesToInspect
            .Where(source => Path.GetExtension(source).Equals(Constants.VBSourceExtension, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var parsedVbTrees = new VisualBasicSyntaxTree?[vbSources.Length];
        using (DebugLog.Phase("methods.parse-visualbasic"))
        {
            DedicatedStack.ForEach("Dosai parse visualbasic", Math.Max(1, MaxSymbolAnalysisWorkers), vbSources.Length,
                index => parsedVbTrees[index] = SafeFileRead.TryReadAllText(vbSources[index], out var content)
                    ? (VisualBasicSyntaxTree)VisualBasicSyntaxTree.ParseText(content, path: vbSources[index])
                    : null);
        }

        var vbTrees = parsedVbTrees.OfType<VisualBasicSyntaxTree>().ToList();
        var vbCompilation = VisualBasicCompilation.Create(
            "Dosai.SourceAnalysis.VisualBasic",
            syntaxTrees: vbTrees,
            references: referenceList,
            options: new VisualBasicCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));
        DebugLog.Count("visualbasic syntax trees", vbTrees.Count);
        if ((csharpTrees.Count > 0 || vbTrees.Count > 0) && frameworkReferences.Diagnostic is { } frameworkDiagnostic)
        {
            mergedDiagnostics.Add(frameworkDiagnostic);
        }

        // Keyed lookups: a linear search per file was quadratic in the file count.
        var csharpTreesByPath = new Dictionary<string, CSharpSyntaxTree>(StringComparer.Ordinal);
        foreach (var tree in csharpTrees)
        {
            csharpTreesByPath.TryAdd(tree.FilePath, tree);
        }
        var vbTreesByPath = new Dictionary<string, VisualBasicSyntaxTree>(StringComparer.Ordinal);
        foreach (var tree in vbTrees)
        {
            vbTreesByPath.TryAdd(tree.FilePath, tree);
        }

        // Dispatch indexes are built once, up front: they are shared read-only state for the
        // per-file workers below. Their instantiation evidence binds every member holding an
        // object creation, so the scan runs on the same worker team and gets its own phase
        // instead of hiding in the first file's slice of the symbol-analysis clock.
        var workerCount = Math.Max(1, MaxSymbolAnalysisWorkers);
        DebugLog.Count("symbol analysis workers", Math.Min(workerCount, Math.Max(1, sourcesToInspect.Count)));
        // Shared per-run memo for every string rendered from a symbol (signatures, display
        // strings, interface-name lists). One render and one retained string instance per
        // distinct symbol, no matter how many members or call sites mention it - the per-call
        // renders it replaces were both the walker's biggest cost and a per-mention duplicate
        // in every downstream MethodCalls/edge/node record (issue #65 memory scaling).
        var renderCache = new SourceRenderCache();
        using (DebugLog.Phase("methods.dispatch-index"))
        {
            if (csharpTrees.Count > 0)
            {
                dispatchIndexes[csharpCompilation] = DispatchResolver.SourceIndex.Create(csharpCompilation, workerCount);
            }

            if (vbTrees.Count > 0)
            {
                dispatchIndexes[vbCompilation] = DispatchResolver.SourceIndex.Create(vbCompilation, workerCount);
            }
        }

        using var symbolAnalysisPhase = DebugLog.Phase("methods.symbol-analysis");

        // The per-file body below is pure with respect to its file: everything it produces goes
        // into the per-file collector, and everything it reads (compilations, tree lookups,
        // dispatch indexes, the base path) is immutable by the time the first file starts. That
        // is what lets the same body run on one thread or on the worker team (issue #65).
        SourceFileSymbols AnalyzeSourceFile(string sourceFilePath)
        {
            var symbols = new SourceFileSymbols();
            var sourceMethods = symbols.Methods;
            var allUsingDirectives = symbols.UsingDirectives;
            var allMethodCalls = symbols.MethodCalls;
            var properties = symbols.Properties;
            var fields = symbols.Fields;
            var events = symbols.Events;
            var constructors = symbols.Constructors;
            var sourceDiagnostics = symbols.Diagnostics;
            var fileName = Path.GetFileName(sourceFilePath);
            // Computed once per file: it was rendered per member and per call site, a pure
            // function of (path, file) that left an identical string behind in every record.
            var relativePath = Path.GetRelativePath(path, sourceFilePath);
            var extn = Path.GetExtension(sourceFilePath);
            SemanticModel? model;
            CompilationUnitSyntax? csRoot = null;
            Microsoft.CodeAnalysis.VisualBasic.Syntax.CompilationUnitSyntax? vbRoot = null;

            if (extn.Equals(Constants.CSharpSourceExtension, StringComparison.OrdinalIgnoreCase))
            {
                if (!csharpTreesByPath.TryGetValue(sourceFilePath, out var tree))
                {
                    // The loop's `continue` over an unparseable file is an empty result here.
                    return symbols;
                }
                csRoot = tree.GetCompilationUnitRoot();
                model = csharpCompilation.GetSemanticModel(tree);
            }
            else if (extn.Equals(Constants.VBSourceExtension, StringComparison.OrdinalIgnoreCase))
            {
                if (!vbTreesByPath.TryGetValue(sourceFilePath, out var tree))
                {
                    return symbols;
                }
                vbRoot = tree.GetCompilationUnitRoot();
                model = vbCompilation.GetSemanticModel(tree);
            }
            else
            {
                return symbols;
            }

            if (OperationDepthGuard.Describe(model.SyntaxTree) is { } depthDiagnostic)
            {
                sourceDiagnostics.Add(depthDiagnostic);
            }


            var csMethodDeclarations = csRoot?.DescendantNodes().OfType<MethodDeclarationSyntax>();
            var vbMethodDeclarations = vbRoot?.DescendantNodes().OfType<MethodStatementSyntax>();

            var csUsingDirectives = csRoot?.DescendantNodes().OfType<UsingDirectiveSyntax>();
            var vbImportsDirectives = vbRoot?.DescendantNodes().OfType<SimpleImportsClauseSyntax>();

            var csPropertyDeclarations = csRoot?.DescendantNodes().OfType<PropertyDeclarationSyntax>();
            var vbPropertyDeclarations = vbRoot?.DescendantNodes().OfType<PropertyStatementSyntax>();

            var csFieldDeclarations = csRoot?.DescendantNodes().OfType<FieldDeclarationSyntax>();
            var vbFieldDeclarations = vbRoot?.DescendantNodes().OfType<Microsoft.CodeAnalysis.VisualBasic.Syntax.FieldDeclarationSyntax>();

            var csEventDeclarations = csRoot?.DescendantNodes().OfType<EventDeclarationSyntax>();
            var csEventFieldDeclarations = csRoot?.DescendantNodes().OfType<EventFieldDeclarationSyntax>();
            var vbEventDeclarations = vbRoot?.DescendantNodes().OfType<EventStatementSyntax>();
            
            var csConstructorDeclarations = csRoot?.DescendantNodes().OfType<ConstructorDeclarationSyntax>();
            var vbConstructorDeclarations = vbRoot?.DescendantNodes().OfType<ConstructorBlockSyntax>();

            // C# method declarations
            if (csMethodDeclarations is not null)
            {
                foreach(var methodDeclaration in csMethodDeclarations)
                {
                    var modifiers = methodDeclaration.Modifiers;
                    var methodSymbol = model.GetDeclaredSymbol(methodDeclaration);
                    var codeSpan = methodDeclaration.SyntaxTree.GetLineSpan(methodDeclaration.Span);
                    var lineNumber = codeSpan.StartLinePosition.Line + 1;
                    var columnNumber = codeSpan.Span.Start.Character + 1;

                    if (methodSymbol is not null)
                    {
                        // Get the containing type for inheritance information
                        var containingType = methodSymbol.ContainingType;
                        var baseType = containingType?.BaseType?.Name;
                        var implementedInterfaces = renderCache.InterfaceNames(containingType);
                        var metadataToken = 0;
                        if (SymbolEqualityComparer.Default.Equals(methodSymbol.ContainingAssembly, model.Compilation.Assembly))
                        {
                            metadataToken = methodSymbol.MetadataToken;
                        }
                        var isGenericMethod = methodSymbol.IsGenericMethod;
                        List<string> genericParameters = methodSymbol.TypeParameters.Select(tp => tp.Name).ToList();
                        sourceMethods.Add(new Method
                        {
                            Path = relativePath,
                            FileName = fileName,
                            Assembly = renderCache.Display(methodSymbol.ContainingAssembly),
                            Module = renderCache.Display(methodSymbol.ContainingModule),
                            Namespace = renderCache.Display(methodSymbol.ContainingNamespace),
                            ClassName = GetNamedContainingTypeName(methodSymbol),
                            Attributes = TitleCase(string.Join(", ", modifiers)),
                            Name = methodSymbol.Name,
                            ReturnType = methodSymbol.ReturnType.ToDisplayString(),
                            LineNumber = lineNumber,
                            ColumnNumber = columnNumber,
                            Parameters = methodSymbol.Parameters.Select(p => new Parameter
                            {
                                Name = p.Name,
                                Type = TitleCase(p.Type.ToString()!),
                                TypeFullName = p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                                IsGenericParameter = p.Type is ITypeParameterSymbol
                            }).ToList(),
                            CustomAttributes = ExtractCustomAttributes(methodSymbol, sourceDiagnostics),
                            BaseType = baseType,
                            ImplementedInterfaces = implementedInterfaces,
                            MetadataToken = metadataToken,
                            SourceSignature = renderCache.Signature(methodSymbol),
                            IsGenericMethod = isGenericMethod,
                            GenericParameters = genericParameters,
                        });
                    }
                }
            }

            // Top-level statements (the default `dotnet new console` template) have no
            // MethodDeclarationSyntax, so the compiler-synthesized `<Main>$` never reached the
            // method inventory, a modern CLI was invisible to methods, entry points, and every
            // consumer of the reachability index. Resolve the synthesized symbol from the first
            // global statement and emit it like a declared method; its SourceSignature matches the
            // call-graph node id the operation walkers already produce for global statements.
            var globalStatements = csRoot?.Members.OfType<GlobalStatementSyntax>().ToList();
            if (globalStatements is { Count: > 0 } && model.GetEnclosingSymbol(globalStatements[0].Statement.SpanStart) is IMethodSymbol { Name: "<Main>$" } topLevelMain)
            {
                var mainSpan = globalStatements[0].Statement.SyntaxTree.GetLineSpan(globalStatements[0].Statement.Span);
                sourceMethods.Add(new Method
                {
                    Path = relativePath,
                    FileName = fileName,
                    Assembly = renderCache.Display(topLevelMain.ContainingAssembly),
                    Module = renderCache.Display(topLevelMain.ContainingModule),
                    Namespace = renderCache.Display(topLevelMain.ContainingNamespace),
                    ClassName = topLevelMain.ContainingType.Name,
                    Attributes = "Static",
                    Name = topLevelMain.Name,
                    ReturnType = topLevelMain.ReturnType.ToDisplayString(),
                    LineNumber = mainSpan.StartLinePosition.Line + 1,
                    ColumnNumber = mainSpan.StartLinePosition.Character + 1,
                    Parameters = topLevelMain.Parameters.Select(p => new Parameter
                    {
                        Name = p.Name,
                        Type = TitleCase(p.Type.ToString()!),
                        TypeFullName = p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                        IsGenericParameter = p.Type is ITypeParameterSymbol
                    }).ToList(),
                    CustomAttributes = [],
                    BaseType = null,
                    ImplementedInterfaces = [],
                    SourceSignature = renderCache.Signature(topLevelMain)
                });
            }

            // VB method declarations
            if (vbMethodDeclarations is not null)
            {
                foreach(var methodDeclaration in vbMethodDeclarations)
                {
                    var modifiers = methodDeclaration.Modifiers;
                    var method = model.GetDeclaredSymbol(methodDeclaration);
                    var codeSpan = methodDeclaration.SyntaxTree.GetLineSpan(methodDeclaration.Span);
                    var lineNumber = codeSpan.StartLinePosition.Line + 1;
                    var columnNumber = codeSpan.Span.Start.Character + 1;

                    if (method is not null)
                    {
                        // Get inheritance information
                        var containingType = method.ContainingType;
                        var baseType = containingType?.BaseType?.Name;
                        var implementedInterfaces = renderCache.InterfaceNames(containingType);
                        var metadataToken = 0;
                        if (SymbolEqualityComparer.Default.Equals(method.ContainingAssembly, model.Compilation.Assembly))
                        {
                            metadataToken = method.MetadataToken;
                        }
                        sourceMethods.Add(new Method
                        {
                            Path = relativePath,
                            FileName = fileName,
                            Assembly = renderCache.Display(method.ContainingAssembly),
                            Module = renderCache.Display(method.ContainingModule),
                            Namespace = renderCache.Display(method.ContainingNamespace),
                            ClassName = method.ContainingType.Name,
                            Attributes = TitleCase(string.Join(", ", modifiers)),
                            Name = method.Name,
                            ReturnType = method.ReturnType.Name,
                            LineNumber = lineNumber,
                            ColumnNumber = columnNumber,
                            Parameters = method.Parameters.Select(p => new Parameter {
                                Name = p.Name,
                                Type = TitleCase(p.Type.ToString()!),
                                TypeFullName = p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                            }).ToList(),
                            CustomAttributes = ExtractCustomAttributes(method, sourceDiagnostics),
                            BaseType = baseType,
                            ImplementedInterfaces = implementedInterfaces,
                            MetadataToken = metadataToken,
                            SourceSignature = renderCache.Signature(method)
                        });
                    }
                }
            }

            // C# property declarations
            if (csPropertyDeclarations is not null)
            {
                foreach(var propertyDeclaration in csPropertyDeclarations)
                {
                    var modifiers = propertyDeclaration.Modifiers;
                    var propertySymbol = model.GetDeclaredSymbol(propertyDeclaration);
                    var codeSpan = propertyDeclaration.SyntaxTree.GetLineSpan(propertyDeclaration.Span);
                    var lineNumber = codeSpan.StartLinePosition.Line + 1;
                    var columnNumber = codeSpan.Span.Start.Character + 1;

                    if (propertySymbol is not null)
                    {
                        // Get inheritance information
                        var containingType = propertySymbol.ContainingType;
                        var baseType = containingType?.BaseType?.Name;
                        var implementedInterfaces = renderCache.InterfaceNames(containingType);
                        var metadataToken = 0;
                        if (SymbolEqualityComparer.Default.Equals(propertySymbol.ContainingAssembly, model.Compilation.Assembly))
                        {
                            metadataToken = propertySymbol.MetadataToken;
                        }
                        properties.Add(new PropertyInfo
                        {
                            Path = relativePath,
                            FileName = fileName,
                            Assembly = renderCache.Display(propertySymbol.ContainingAssembly),
                            Module = renderCache.Display(propertySymbol.ContainingModule),
                            Namespace = renderCache.Display(propertySymbol.ContainingNamespace),
                            ClassName = GetNamedContainingTypeName(propertySymbol),
                            Attributes = TitleCase(string.Join(", ", modifiers)),
                            Name = propertySymbol.Name,
                            Type = propertySymbol.Type.Name,
                            TypeFullName = propertySymbol.Type.ToDisplayString(),
                            LineNumber = lineNumber,
                            ColumnNumber = columnNumber,
                            CustomAttributes = ExtractCustomAttributes(propertySymbol, sourceDiagnostics),
                            HasGetter = propertySymbol.GetMethod is not null,
                            HasSetter = propertySymbol.SetMethod is not null,
                            Implements = propertySymbol.ExplicitInterfaceImplementations.Select(i => i.ToDisplayString()).ToList(),
                            BaseType = baseType,
                            ImplementedInterfaces = implementedInterfaces,
                            MetadataToken = metadataToken
                        });
                        if (propertySymbol.GetMethod is not null)
                        {
                            var getterMethod = CreateMethodFromSymbol(propertySymbol.GetMethod, model, relativePath, fileName, lineNumber, columnNumber, sourceDiagnostics, renderCache);
                            sourceMethods.Add(getterMethod);    
                        }
                        if (propertySymbol.SetMethod is not null)
                        {
                            var setterMethod = CreateMethodFromSymbol(propertySymbol.SetMethod, model, relativePath, fileName, lineNumber, columnNumber, sourceDiagnostics, renderCache);
                            sourceMethods.Add(setterMethod);
                        }
                    }
                }
            }

            // VB property declarations
            if (vbPropertyDeclarations is not null)
            {
                foreach(var propertyDeclaration in vbPropertyDeclarations)
                {
                    var modifiers = propertyDeclaration.Modifiers;
                    var propertySymbol = model.GetDeclaredSymbol(propertyDeclaration);
                    var codeSpan = propertyDeclaration.SyntaxTree.GetLineSpan(propertyDeclaration.Span);
                    var lineNumber = codeSpan.StartLinePosition.Line + 1;
                    var columnNumber = codeSpan.Span.Start.Character + 1;

                    if (propertySymbol is not null)
                    {
                        // Get inheritance information
                        var containingType = propertySymbol.ContainingType;
                        var baseType = containingType?.BaseType?.Name;
                        var implementedInterfaces = renderCache.InterfaceNames(containingType);
                        var metadataToken = 0;
                        if (SymbolEqualityComparer.Default.Equals(propertySymbol.ContainingAssembly, model.Compilation.Assembly))
                        {
                            metadataToken = propertySymbol.MetadataToken;
                        }
                        properties.Add(new PropertyInfo
                        {
                            Path = relativePath,
                            FileName = fileName,
                            Assembly = renderCache.Display(propertySymbol.ContainingAssembly),
                            Module = renderCache.Display(propertySymbol.ContainingModule),
                            Namespace = renderCache.Display(propertySymbol.ContainingNamespace),
                            ClassName = GetNamedContainingTypeName(propertySymbol),
                            Attributes = TitleCase(string.Join(", ", modifiers)),
                            Name = propertySymbol.Name,
                            Type = propertySymbol.Type.Name,
                            TypeFullName = propertySymbol.Type.ToDisplayString(),
                            LineNumber = lineNumber,
                            ColumnNumber = columnNumber,
                            CustomAttributes = ExtractCustomAttributes(propertySymbol, sourceDiagnostics),
                            HasGetter = propertySymbol.GetMethod is not null,
                            HasSetter = propertySymbol.SetMethod is not null,
                            Implements = propertySymbol.ExplicitInterfaceImplementations.Select(i => i.ToDisplayString()).ToList(),
                            BaseType = baseType,
                            ImplementedInterfaces = implementedInterfaces,
                            MetadataToken = metadataToken
                        });
                        if (propertySymbol.GetMethod is not null)
                        {
                            var getterMethod = CreateMethodFromSymbol(propertySymbol.GetMethod, model, relativePath, fileName, lineNumber, columnNumber, sourceDiagnostics, renderCache);
                            sourceMethods.Add(getterMethod);
                        }

                        if (propertySymbol.SetMethod is not null)
                        {
                            var setterMethod = CreateMethodFromSymbol(propertySymbol.SetMethod, model, relativePath, fileName, lineNumber, columnNumber, sourceDiagnostics, renderCache);
                            sourceMethods.Add(setterMethod);
                        }
                    }
                }
            }

            // C# field declarations
            if (csFieldDeclarations is not null)
            {
                foreach(var fieldDeclaration in csFieldDeclarations)
                {
                    var modifiers = fieldDeclaration.Modifiers;
                    var variables = fieldDeclaration.Declaration.Variables;
                    var type = fieldDeclaration.Declaration.Type;
                    var typeSymbol = model.GetSymbolInfo(type).Symbol as ITypeSymbol;
                    // Get inheritance information for the containing type
                    INamedTypeSymbol? containingTypeSymbol = null;
                    if (fieldDeclaration.Parent is not null)
                    {
                        containingTypeSymbol = model.GetDeclaredSymbol(fieldDeclaration.Parent) as INamedTypeSymbol;
                    }
                    var baseType = containingTypeSymbol?.BaseType?.ToDisplayString();
                    var implementedInterfaces = containingTypeSymbol?.AllInterfaces.Select(i => i.ToDisplayString()).ToList();
                    
                    foreach(var variable in variables)
                    {
                        var codeSpan = variable.SyntaxTree.GetLineSpan(variable.Span);
                        var lineNumber = codeSpan.StartLinePosition.Line + 1;
                        var columnNumber = codeSpan.Span.Start.Character + 1;
                        var metadataToken = 0;
                        fields.Add(new FieldInfo
                        {
                            Path = relativePath,
                            FileName = fileName,
                            Assembly = model.Compilation.Assembly.ToDisplayString(),
                            Module = model.Compilation.Assembly.Modules.FirstOrDefault()?.ToDisplayString(),
                            Namespace = containingTypeSymbol?.ContainingNamespace?.ToDisplayString() ?? model.Compilation.Assembly.Name,
                            ClassName = GetContainingTypeName(fieldDeclaration),
                            Attributes = TitleCase(string.Join(", ", modifiers)),
                            Name = variable.Identifier.Text,
                            Type = typeSymbol?.Name ?? type.ToString(),
                            TypeFullName = typeSymbol?.ToDisplayString() ?? type.ToString(),
                            LineNumber = lineNumber,
                            ColumnNumber = columnNumber,
                            CustomAttributes = fieldDeclaration.AttributeLists.SelectMany(al => 
                                al.Attributes.Select(attr => {
                                    var attrSymbol = model.GetSymbolInfo(attr).Symbol;
                                    return new CustomAttributeInfo {
                                        Name = attrSymbol?.ContainingType.Name,
                                        FullName = attrSymbol?.ContainingType.ToDisplayString(),
                                        ConstructorArguments = attr.ArgumentList?.Arguments.Select(arg => new CustomAttributeArgumentInfo { Value = arg.Expression.ToString() }).ToList() ??
                                                               [],
                                        NamedArguments = []
                                    };
                                })).ToList(),
                            BaseType = baseType,
                            ImplementedInterfaces = implementedInterfaces,
                            MetadataToken = metadataToken
                        });
                    }
                }
            }

            // VB field declarations
            if (vbFieldDeclarations is not null)
            {
                foreach(var fieldDeclaration in vbFieldDeclarations)
                {
                    var modifiers = fieldDeclaration.Modifiers;
                    var variables = fieldDeclaration.Declarators;
                    var firstVariable = variables.FirstOrDefault();
                    var asClause = firstVariable?.AsClause as SimpleAsClauseSyntax;
                    var type = asClause?.Type;
                    var typeSymbol = type is not null ? model.GetSymbolInfo(type).Symbol as ITypeSymbol : null;
                    // Get inheritance information for the containing type
                    INamedTypeSymbol? containingTypeSymbol = null;
                    if (fieldDeclaration.Parent is not null)
                    {
                        if (model is not null)
                            containingTypeSymbol = model.GetDeclaredSymbol(fieldDeclaration.Parent) as INamedTypeSymbol;
                    }
                    var baseType = containingTypeSymbol?.BaseType?.Name;
                    var implementedInterfaces = containingTypeSymbol?.AllInterfaces.Select(i => i.Name).ToList();
                    if (model is not null)
                    {
                        foreach(var variable in variables)
                        {
                            var codeSpan = variable.SyntaxTree.GetLineSpan(variable.Span);
                            var lineNumber = codeSpan.StartLinePosition.Line + 1;
                            var columnNumber = codeSpan.Span.Start.Character + 1;
                            var metadataToken = 0;
                            fields.Add(new FieldInfo
                            {
                                Path = relativePath,
                                FileName = fileName,
                                Assembly = model?.Compilation.Assembly.ToDisplayString(),
                                Module = model?.Compilation.Assembly.Modules.FirstOrDefault()?.ToDisplayString(),
                                Namespace = containingTypeSymbol?.ContainingNamespace?.ToDisplayString() ?? model?.Compilation.Assembly.Name,
                                ClassName = GetContainingTypeNameVb(fieldDeclaration), // Use VB-specific method
                                Attributes = TitleCase(string.Join(", ", modifiers)),
                                Name = variable.Names.FirstOrDefault()?.Identifier.Text ?? "",
                                Type = typeSymbol?.Name ?? type?.ToString() ?? "Unknown",
                                TypeFullName = typeSymbol?.ToDisplayString() ?? type?.ToString(),
                                LineNumber = lineNumber,
                                ColumnNumber = columnNumber,
                                CustomAttributes = fieldDeclaration.AttributeLists.SelectMany(al => 
                                    al.Attributes.Select(attr => {
                                        var attrSymbol = model.GetSymbolInfo(attr).Symbol;
                                        return new CustomAttributeInfo {
                                            Name = attrSymbol?.ContainingType.Name,
                                            FullName = attrSymbol?.ContainingType.ToDisplayString(),
                                            ConstructorArguments = attr.ArgumentList?.Arguments.Select(arg => new CustomAttributeArgumentInfo { Value = arg.GetExpression().ToString() }).ToList() ??
                                                                   [],
                                            NamedArguments = []
                                        };
                                    })).ToList(),
                                BaseType = baseType,
                                ImplementedInterfaces = implementedInterfaces,
                                MetadataToken = metadataToken
                            });
                        }
                    }
                }
            }

            // C# event declarations
            if (csEventDeclarations is not null)
            {
                foreach(var eventDeclaration in csEventDeclarations)
                {
                    var modifiers = eventDeclaration.Modifiers;
                    var eventSymbol = model.GetDeclaredSymbol(eventDeclaration);
                    var codeSpan = eventDeclaration.SyntaxTree.GetLineSpan(eventDeclaration.Span);
                    var lineNumber = codeSpan.StartLinePosition.Line + 1;
                    var columnNumber = codeSpan.Span.Start.Character + 1;

                    if (eventSymbol is not null)
                    {
                        // Get inheritance information
                        var containingType = eventSymbol.ContainingType;
                        var baseType = containingType?.BaseType?.Name;
                        var implementedInterfaces = renderCache.InterfaceNames(containingType);
                        var metadataToken = 0;
                        if (SymbolEqualityComparer.Default.Equals(eventSymbol.ContainingAssembly, model?.Compilation.Assembly))
                        {
                            metadataToken = eventSymbol.MetadataToken;
                        }
                        events.Add(new EventInfo
                        {
                            Path = relativePath,
                            FileName = fileName,
                            Assembly = renderCache.Display(eventSymbol.ContainingAssembly),
                            Module = renderCache.Display(eventSymbol.ContainingModule),
                            Namespace = renderCache.Display(eventSymbol.ContainingNamespace),
                            ClassName = GetNamedContainingTypeName(eventSymbol),
                            Attributes = TitleCase(string.Join(", ", modifiers)),
                            Name = eventSymbol.Name,
                            Type = eventSymbol.Type.Name,
                            TypeFullName = eventSymbol.Type.ToDisplayString(),
                            LineNumber = lineNumber,
                            ColumnNumber = columnNumber,
                            CustomAttributes = ExtractCustomAttributes(eventSymbol, sourceDiagnostics),
                            BaseType = baseType,
                            ImplementedInterfaces = implementedInterfaces,
                            MetadataToken = metadataToken
                        });
                    }
                }
            }

            // C# event field declarations
            if (csEventFieldDeclarations is not null)
            {
                foreach(var eventFieldDeclaration in csEventFieldDeclarations)
                {
                    var modifiers = eventFieldDeclaration.Modifiers;
                    var variables = eventFieldDeclaration.Declaration.Variables;
                    var type = eventFieldDeclaration.Declaration.Type;
                    var typeSymbol = model.GetSymbolInfo(type).Symbol as ITypeSymbol;
                    
                    foreach(var variable in variables)
                    {
                        // A field-like event's declarator declares the event itself: Roslyn hands
                        // back an IEventSymbol there, never an IFieldSymbol. Casting to the field
                        // symbol left it null for every such event, so each one reported the
                        // compilation's name as its namespace, no interfaces and no token.
                        var eventSymbol = model.GetDeclaredSymbol(variable) as IEventSymbol;
                        var codeSpan = variable.SyntaxTree.GetLineSpan(variable.Span);
                        var lineNumber = codeSpan.StartLinePosition.Line + 1;
                        var columnNumber = codeSpan.Span.Start.Character + 1;
                        // Get inheritance information
                        var containingType = eventSymbol?.ContainingType;
                        var baseType = containingType?.BaseType?.Name;
                        var implementedInterfaces = renderCache.InterfaceNames(containingType);
                        var metadataToken = 0;
                        if (eventSymbol is not null && SymbolEqualityComparer.Default.Equals(eventSymbol.ContainingAssembly, model?.Compilation.Assembly))
                        {
                            metadataToken = eventSymbol.MetadataToken;
                        }
                        var eventType = eventSymbol?.Type ?? typeSymbol;
                        events.Add(new EventInfo
                        {
                            Path = relativePath,
                            FileName = fileName,
                            Assembly = eventSymbol is not null ? renderCache.Display(eventSymbol.ContainingAssembly) : model?.Compilation.Assembly.ToDisplayString(),
                            Module = eventSymbol is not null ? renderCache.Display(eventSymbol.ContainingModule) : model?.Compilation.Assembly.Modules.FirstOrDefault()?.ToDisplayString(),
                            Namespace = eventSymbol is not null ? renderCache.Display(eventSymbol.ContainingNamespace) : model?.Compilation.Assembly.Name,
                            ClassName = eventSymbol is null ? GetContainingTypeName(eventFieldDeclaration) : GetNamedContainingTypeName(eventSymbol),
                            Attributes = TitleCase(string.Join(", ", modifiers)),
                            Name = variable.Identifier.Text,
                            Type = eventType?.Name ?? type.ToString(),
                            TypeFullName = eventType?.ToDisplayString() ?? type.ToString(),
                            LineNumber = lineNumber,
                            ColumnNumber = columnNumber,
                            CustomAttributes = eventFieldDeclaration.AttributeLists.SelectMany(al => 
                                al.Attributes.Select(attr => {
                                    var attrSymbol = model.GetSymbolInfo(attr).Symbol;
                                    return new CustomAttributeInfo {
                                        Name = attrSymbol?.ContainingType.Name,
                                        FullName = attrSymbol?.ContainingType.ToDisplayString(),
                                        ConstructorArguments = attr.ArgumentList?.Arguments.Select(arg => new CustomAttributeArgumentInfo { Value = arg.Expression.ToString() }).ToList() ??
                                                               [],
                                        NamedArguments = []
                                    };
                                })).ToList(),
                            BaseType = baseType,
                            ImplementedInterfaces = implementedInterfaces,
                            MetadataToken = metadataToken
                        });
                    }
                }
            }

            // VB event declarations
            if (vbEventDeclarations is not null)
            {
                foreach(var eventDeclaration in vbEventDeclarations)
                {
                    var modifiers = eventDeclaration.Modifiers;
                    var eventSymbol = model.GetDeclaredSymbol(eventDeclaration);
                    var codeSpan = eventDeclaration.SyntaxTree.GetLineSpan(eventDeclaration.Span);
                    var lineNumber = codeSpan.StartLinePosition.Line + 1;
                    var columnNumber = codeSpan.Span.Start.Character + 1;

                    if (eventSymbol is not null)
                    {
                        // Get inheritance information
                        var containingType = eventSymbol.ContainingType;
                        var baseType = containingType?.BaseType?.Name;
                        var implementedInterfaces = renderCache.InterfaceNames(containingType);
                        var metadataToken = 0;
                        if (SymbolEqualityComparer.Default.Equals(eventSymbol.ContainingAssembly, model?.Compilation.Assembly))
                        {
                            metadataToken = eventSymbol.MetadataToken;
                        }
                        events.Add(new EventInfo
                        {
                            Path = relativePath,
                            FileName = fileName,
                            Assembly = renderCache.Display(eventSymbol.ContainingAssembly),
                            Module = renderCache.Display(eventSymbol.ContainingModule),
                            Namespace = renderCache.Display(eventSymbol.ContainingNamespace),
                            ClassName = GetNamedContainingTypeName(eventSymbol),
                            Attributes = TitleCase(string.Join(", ", modifiers)),
                            Name = eventSymbol.Name,
                            Type = eventSymbol.Type.Name,
                            TypeFullName = eventSymbol.Type.ToDisplayString(),
                            LineNumber = lineNumber,
                            ColumnNumber = columnNumber,
                            CustomAttributes = ExtractCustomAttributes(eventSymbol, sourceDiagnostics),
                            BaseType = baseType,
                            ImplementedInterfaces = implementedInterfaces,
                            MetadataToken = metadataToken
                        });
                    }
                }
            }

            // C# constructor declarations
            if (csConstructorDeclarations is not null)
            {
                foreach(var constructorDeclaration in csConstructorDeclarations)
                {
                    var modifiers = constructorDeclaration.Modifiers;
                    var constructorSymbol = model.GetDeclaredSymbol(constructorDeclaration);
                    var codeSpan = constructorDeclaration.SyntaxTree.GetLineSpan(constructorDeclaration.Span);
                    var lineNumber = codeSpan.StartLinePosition.Line + 1;
                    var columnNumber = codeSpan.Span.Start.Character + 1;
                    var metadataToken = 0;
                    if (constructorSymbol is not null)
                    {
                        // Get inheritance information
                        var containingType = constructorSymbol.ContainingType;
                        var baseType = containingType?.BaseType?.Name;
                        var implementedInterfaces = renderCache.InterfaceNames(containingType);
                        if (SymbolEqualityComparer.Default.Equals(constructorSymbol.ContainingAssembly, model?.Compilation.Assembly))
                        {
                            metadataToken = constructorSymbol.MetadataToken;
                        }
                        var isGenericMethod = constructorSymbol.IsGenericMethod;
                        List<string> genericParameters = [];
                        if (constructorSymbol.ContainingType?.IsGenericType ?? false)
                        {
                            genericParameters = constructorSymbol.ContainingType.TypeParameters.Select(tp => tp.Name).ToList();
                        }
                        constructors.Add(new ConstructorInfo
                        {
                            Path = relativePath,
                            FileName = fileName,
                            Assembly = renderCache.Display(constructorSymbol.ContainingAssembly),
                            Module = renderCache.Display(constructorSymbol.ContainingModule),
                            Namespace = renderCache.Display(constructorSymbol.ContainingNamespace),
                            ClassName = containingType?.Name,
                            Attributes = TitleCase(string.Join(", ", modifiers)),
                            Name = containingType?.Name,
                            ReturnType = "Void",
                            LineNumber = lineNumber,
                            ColumnNumber = columnNumber,
                            IsGenericMethod = isGenericMethod,
                            GenericParameters = genericParameters,
                            Parameters = constructorSymbol.Parameters.Select(p => new Parameter
                            {
                                Name = p.Name,
                                Type = TitleCase(p.Type.ToString()!),
                                TypeFullName = p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                                IsGenericParameter = p.Type is ITypeParameterSymbol
                            }).ToList(),
                            CustomAttributes = ExtractCustomAttributes(constructorSymbol, sourceDiagnostics),
                            IsStatic = constructorSymbol.IsStatic,
                            BaseType = baseType,
                            ImplementedInterfaces = implementedInterfaces,
                            MetadataToken = metadataToken
                        });
                    }
                }
            }

            // VB constructor declarations
            if (vbConstructorDeclarations is not null)
            {
                foreach(var constructorDeclaration in vbConstructorDeclarations)
                {
                    var ctorStmt = constructorDeclaration.SubNewStatement;
                    if (ctorStmt is not null)
                    {
                        var modifiers = ctorStmt.Modifiers;
                        var constructorSymbol = model.GetDeclaredSymbol(ctorStmt);
                        var codeSpan = ctorStmt.SyntaxTree.GetLineSpan(ctorStmt.Span);
                        var lineNumber = codeSpan.StartLinePosition.Line + 1;
                        var columnNumber = codeSpan.Span.Start.Character + 1;

                        if (constructorSymbol is not null)
                        {
                            // Get inheritance information
                            var containingType = constructorSymbol.ContainingType;
                            var baseType = containingType?.BaseType?.Name;
                            var implementedInterfaces = renderCache.InterfaceNames(containingType);
                            var metadataToken = 0;
                            if (SymbolEqualityComparer.Default.Equals(constructorSymbol.ContainingAssembly, model?.Compilation.Assembly))
                            {
                                metadataToken = constructorSymbol.MetadataToken;
                            }
                            constructors.Add(new ConstructorInfo
                            {
                                Path = relativePath,
                                FileName = fileName,
                                Assembly = renderCache.Display(constructorSymbol.ContainingAssembly),
                                Module = renderCache.Display(constructorSymbol.ContainingModule),
                                Namespace = renderCache.Display(constructorSymbol.ContainingNamespace),
                                ClassName = GetNamedContainingTypeName(constructorSymbol),
                                Attributes = TitleCase(string.Join(", ", modifiers)),
                                Name = constructorSymbol.ContainingType.Name,
                                ReturnType = "Void",
                                LineNumber = lineNumber,
                                ColumnNumber = columnNumber,
                                Parameters = constructorSymbol.Parameters.Select(p => new Parameter {
                                    Name = p.Name,
                                    Type = TitleCase(p.Type.ToString()!)
                                }).ToList(),
                                CustomAttributes = ExtractCustomAttributes(constructorSymbol, sourceDiagnostics),
                                IsStatic = constructorSymbol.IsStatic,
                                BaseType = baseType,
                                ImplementedInterfaces = implementedInterfaces,
                                MetadataToken = metadataToken
                            });
                        }
                    }
                }
            }

            // using declarations
            if (csUsingDirectives is not null)
            {
                foreach(var usingDirective in csUsingDirectives)
                {
                    var name = usingDirective.Name?.ToFullString();
                    var namespaceType = usingDirective.NamespaceOrType.ToFullString();
                    var location = usingDirective.GetLocation().GetLineSpan().StartLinePosition;
                    var lineNumber = location.Line + 1;
                    var columnNumber = location.Character + 1;
                    var namespaceMembers = new List<string>();
                    var assembly = string.Empty;
                    var module = string.Empty;

                    if (usingDirective.Name is not null)
                    {
                        var nsSymbol = ResolveNamespaceSymbol(model.GetSymbolInfo(usingDirective.Name));

                        if (nsSymbol is not null)
                        {
                            var nsMembers = nsSymbol.GetNamespaceMembers();
                            namespaceMembers.AddRange(nsMembers.Select(m => m.Name));
                            assembly = nsSymbol.ContainingAssembly?.ToDisplayString();
                            module = nsSymbol.ContainingModule?.ToDisplayString();
                            namespaceType = nsSymbol.ContainingNamespace?.ToDisplayString();
                        }
                    }

                    allUsingDirectives.Add(new Dependency
                    {
                        Path = relativePath,
                        FileName = fileName,
                        Assembly = assembly,
                        Module = module,
                        Namespace = namespaceType,
                        Name = name,
                        LineNumber = lineNumber,
                        ColumnNumber = columnNumber,
                        NamespaceMembers = namespaceMembers
                    });
                }
            }

            // File-based app packages: `#:package Id@Version` (or `#:sdk`-scoped variants) are the
            // only place a `dotnet run app.cs` app declares its NuGet references, so they surface
            // as dependencies the way `#r "nuget: ..."` does for F# scripts. The directives parse
            // as IgnoredDirectiveTrivia under the FileBasedProgram feature; the other `#:`
            // kinds (property, sdk, include, project) carry no package identity.
            if (csRoot is not null)
            {
                foreach (var directive in csRoot.DescendantTrivia(descendIntoTrivia: true))
                {
                    if (!directive.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.IgnoredDirectiveTrivia))
                    {
                        continue;
                    }

                    var directiveText = directive.ToString().Trim();
                    if (!directiveText.StartsWith("#:", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var packageMatch = Regex.Match(directiveText, @"^#:\s*package\s+([A-Za-z0-9_.\-]+)");
                    if (!packageMatch.Success)
                    {
                        continue;
                    }

                    var directiveSpan = directive.GetLocation().GetLineSpan().StartLinePosition;
                    allUsingDirectives.Add(new Dependency
                    {
                        Path = relativePath,
                        FileName = fileName,
                        Assembly = string.Empty,
                        Module = "FileBasedApp",
                        Namespace = "nuget",
                        Name = packageMatch.Groups[1].Value,
                        LineNumber = directiveSpan.Line + 1,
                        ColumnNumber = directiveSpan.Character + 1,
                        NamespaceMembers = []
                    });
                }
            }

            // import declarations
            if (vbImportsDirectives is not null)
            {
                foreach(var importDirective in vbImportsDirectives)
                {
                    var name = importDirective.Name?.ToFullString().Trim();
                    var namespaceType = importDirective.Alias?.ToFullString();
                    var location = importDirective.GetLocation().GetLineSpan().StartLinePosition;
                    var lineNumber = location.Line + 1;
                    var columnNumber = location.Character + 1;
                    var namespaceMembers = new List<string>();
                    var assembly = "";
                    var module = "";

                    if (importDirective.Name is not null)
                    {
                        var nsSymbol = ResolveNamespaceSymbol(model.GetSymbolInfo(importDirective.Name));
                        if (nsSymbol is not null)
                        {
                            var nsMembers = nsSymbol.GetNamespaceMembers();
                            namespaceMembers.AddRange(nsMembers.Select(m => m.Name));
                            assembly = nsSymbol.ContainingAssembly?.ToDisplayString();
                            module = nsSymbol.ContainingModule?.ToDisplayString();
                            namespaceType = nsSymbol.ContainingNamespace?.ToDisplayString();
                        }
                    }

                    allUsingDirectives.Add(new Dependency
                    {
                        Path = relativePath,
                        FileName = fileName,
                        Assembly = assembly,
                        Module = module,
                        Namespace = namespaceType,
                        Name = name,
                        LineNumber = lineNumber,
                        ColumnNumber = columnNumber,
                        NamespaceMembers = namespaceMembers
                    });
                }
            }

            // method calls / object creation / property access / event assignment
            if (model is not null)
            {
                var walker = new MethodCallOperationWalker(model, dispatchIndexes[model.Compilation], allMethodCalls, sourceFilePath, fileName, relativePath, renderCache);
                var operationNodes = csRoot is not null
                    // A primary constructor's base-type arguments (`class D(int x) : B(x)`) are its
                    // constructor initializer.
                    ? csRoot.DescendantNodes().Where(node => node is Microsoft.CodeAnalysis.CSharp.Syntax.BlockSyntax or ArrowExpressionClauseSyntax or EqualsValueClauseSyntax or ConstructorInitializerSyntax or PrimaryConstructorBaseTypeSyntax or GlobalStatementSyntax)
                    : vbRoot?.DescendantNodes().Where(node => node is Microsoft.CodeAnalysis.VisualBasic.Syntax.StatementSyntax or Microsoft.CodeAnalysis.VisualBasic.Syntax.EqualsValueSyntax) ?? [];
                foreach (var operationNode in operationNodes)
                {
                    var operation = operationNode is GlobalStatementSyntax globalStatement
                        ? OperationDepthGuard.GetOperation(model, globalStatement.Statement)
                        : OperationDepthGuard.GetOperation(model, operationNode);
                    if (operation is not null)
                    {
                        walker.Visit(operation);
                    }
                }

                if (walker.DepthBudgetExceeded)
                {
                    sourceDiagnostics.Add($"{sourceFilePath}: operations nested past the call-graph walker's depth budget were skipped; calls inside them are missing from MethodCalls and the call graph.");
                }

                symbols.AbandonedDispatchSites = walker.AbandonedDispatchSites;
                symbols.FirstAbandonedDispatch = walker.FirstAbandonedDispatch;
            }

            return symbols;
        }

        // Workers claim files as they free up and each file fills its own collector; collectors
        // merge in file order afterwards, so the output is byte-identical to a sequential run
        // regardless of the worker count or scheduling (issue #65).
        var fileSymbols = new SourceFileSymbols?[sourcesToInspect.Count];
        DedicatedStack.ForEach("Dosai symbol analysis", workerCount, fileSymbols.Length,
            index => fileSymbols[index] = AnalyzeSourceFile(sourcesToInspect[index]));

        var abandonedDispatchSites = 0;
        string? firstAbandonedDispatch = null;
        foreach (var fileResult in fileSymbols)
        {
            if (fileResult is null)
            {
                continue;
            }

            mergedMethods.AddRange(fileResult.Methods);
            mergedUsings.AddRange(fileResult.UsingDirectives);
            mergedMethodCalls.AddRange(fileResult.MethodCalls);
            mergedProperties.AddRange(fileResult.Properties);
            mergedFields.AddRange(fileResult.Fields);
            mergedEvents.AddRange(fileResult.Events);
            mergedConstructors.AddRange(fileResult.Constructors);
            mergedDiagnostics.AddRange(fileResult.Diagnostics);
            abandonedDispatchSites += fileResult.AbandonedDispatchSites;
            firstAbandonedDispatch ??= fileResult.FirstAbandonedDispatch;
        }

        // The collectors and the dispatch indexes have served their purpose; dropping the
        // references here lets the per-file list backing arrays (a near-copy of every record
        // collected) and the index's type buckets be collected while the graph is assembled
        // instead of surviving next to it until the method returns.
        fileSymbols = null;
        dispatchIndexes.Clear();

        // Issue #64 containment: each counted call site keeps its direct edge but lost at least
        // one synthesized dispatch-candidate edge to a Roslyn failure. Counted per call site in
        // file order, so the number is the same for every worker count, and reported without
        // --debug, because a quietly thinner call graph is exactly what consumers cannot diagnose.
        DebugLog.Count("call sites with a failed dispatch resolution", abandonedDispatchSites);
        if (abandonedDispatchSites > 0)
        {
            mergedDiagnostics.Add($"Dispatch resolution failed at {abandonedDispatchSites} call site(s), first {firstAbandonedDispatch}; Roslyn threw while resolving an implementing member, so those virtual dispatch candidate edges are missing from the call graph (direct call edges are kept).");
        }

        if (DebugLog.Enabled)
        {
            LogDeclarationErrors(csharpCompilation, csharpTrees.Count > 0);
            LogDeclarationErrors(vbCompilation, vbTrees.Count > 0);
        }

        // The per-file symbol-analysis phase ends with the merge; the frontends run on their own
        // clock because they cover F#, R, and C/C++ files the Roslyn loop never visits.
        symbolAnalysisPhase.Dispose();


        // Process non-Roslyn language frontends.
        List<Method> frontendMethods;
        List<Dependency> frontendDependencies;
        List<MethodCalls> frontendMethodCalls;
        using (DebugLog.Phase("methods.language-frontends"))
        {
            (frontendMethods, frontendDependencies, frontendMethodCalls) = LanguageFrontendAnalyzer.GetMethods(path, includeFSharp: true);
        }
        DebugLog.Count("language-frontend methods (F#, R, C/C++)", frontendMethods.Count);
        mergedMethods.AddRange(frontendMethods);
        mergedUsings.AddRange(frontendDependencies);
        mergedMethodCalls.AddRange(frontendMethodCalls);
        var methodNodeLookup = new Dictionary<string, MethodNode>(StringComparer.Ordinal);
        foreach (var method in mergedMethods.Where(m => !string.IsNullOrWhiteSpace(m.Name)))
        {
            var id = !string.IsNullOrWhiteSpace(method.SourceSignature)
                ? method.SourceSignature!
                : CreateMemberId(method.Namespace, method.ClassName, method.Name, method.Parameters, method.ReturnType);
            AddNode(id, method.Name!, method.ClassName, method.Namespace, method.FileName, method.Assembly, method.Module, "Method", method.LineNumber, method.ColumnNumber, false);
        }

        foreach (var constructor in mergedConstructors.Where(c => !string.IsNullOrWhiteSpace(c.ClassName)))
        {
            var constructorClassName = constructor.GenericParameters is { Count: > 0 } && constructor.ClassName?.Contains('<', StringComparison.Ordinal) != true
                ? $"{constructor.ClassName}<{string.Join(',', constructor.GenericParameters)}>"
                : constructor.ClassName;
            // The inventory namespace display for global-namespace types is the literal
            // "<global namespace>", which never matches the edge id format
            // (GenerateMethodSignature omits it). Keeping the node id in edge format prevents a
            // duplicate unreachable node per global-namespace constructor, the exact false
            // positive the dead-code report surfaces.
            var constructorNamespace = constructor.Namespace is "<global namespace>" ? null : constructor.Namespace;
            var id = CreateMemberId(constructorNamespace, constructorClassName, ".ctor", constructor.Parameters);
            AddNode(id, ".ctor", constructorClassName, constructorNamespace, constructor.FileName, constructor.Assembly, constructor.Module, "Constructor", constructor.LineNumber, constructor.ColumnNumber, false);
        }

        foreach (var call in mergedMethodCalls.Where(c => !string.IsNullOrWhiteSpace(c.SourceId) && !string.IsNullOrWhiteSpace(c.TargetId)))
        {
            if (!methodNodeLookup.ContainsKey(call.SourceId!))
            {
                AddNode(call.SourceId!, call.CallerMethod ?? call.SourceId!, call.CallerClass, call.CallerNamespace, call.FileName, null, null, "Method", call.LineNumber, call.ColumnNumber, false);
            }

            if (!methodNodeLookup.ContainsKey(call.TargetId!))
            {
                AddNode(call.TargetId!, call.CalledMethod ?? call.TargetId!, call.ClassName, call.Namespace, string.Empty, call.Assembly, call.Module, call.CallType == CallType.ConstructorCall ? "Constructor" : "Method", 0, 0, !call.IsInternal);
            }
        }

        var methodNodes = methodNodeLookup.Values.ToList();
        GraphAssembly.SortNodesInPlace(methodNodes);
        // Same-site collapse with the same first-wins semantics the GroupBy had (the first
        // call in merged order defines the edge), but deduping the call BEFORE the edge is
        // built: duplicate sites never allocate their edge object, and the key is a struct
        // instead of a ~200-byte concatenated string per call site - at millions of sites
        // that string churn was gigabytes of garbage allocated in exactly this tail (issue
        // #65's heap climb after symbol analysis finished).
        var seenCallSites = new HashSet<GraphAssembly.EdgeSiteKey>(mergedMethodCalls.Count, GraphAssembly.EdgeSiteKeyComparer.Instance);
        var callEdges = new List<MethodCallEdge>(mergedMethodCalls.Count);
        foreach (var call in mergedMethodCalls)
        {
            if (GraphAssembly.IsBlank(call.SourceId) || GraphAssembly.IsBlank(call.TargetId))
            {
                continue;
            }

            var evidenceKind = ResolveCallEvidenceKind(call);
            if (!seenCallSites.Add(GraphAssembly.EdgeSiteKey.FromCall(call, evidenceKind)))
            {
                continue;
            }

            callEdges.Add(new MethodCallEdge
            {
                SourceId = call.SourceId!,
                TargetId = call.TargetId!,
                CallLocation = new CallLocation { FileName = call.FileName, LineNumber = call.LineNumber, ColumnNumber = call.ColumnNumber },
                Path = call.Path,
                FileName = call.FileName,
                IsInternal = call.IsInternal,
                CalledMethodName = call.CalledMethod,
                SourceName = call.CallerMethod,
                TargetName = call.CalledMethod,
                Arguments = call.Arguments ?? [],
                ArgumentExpressions = call.ArgumentExpressions ?? [],
                CallType = call.CallType,
                EvidenceKind = evidenceKind,
                DispatchConfidence = call.DispatchConfidence,
                Evidence = call.Evidence.Count > 0 ? call.Evidence : [CreateDefaultCallEvidence(call, evidenceKind)]
            });
        }

        GraphAssembly.SortEdgesInPlace(callEdges);
        GraphAssembly.AssignEdgeIds(callEdges, "e");
        var callGraph = new CallGraph
        {
            Edges = callEdges,
            Nodes = methodNodes
        };
        // Source-to-Assembly Mapping Logic
        var assemblyMemberLookup = new Dictionary<string, object>();
        var assemblyNameLookup = new Dictionary<string, List<Method>>(StringComparer.Ordinal);
        foreach (var asmMethod in assemblyMethods)
        {
            var asmSignature = asmMethod.AssemblySignature;
            if (asmSignature is not null)
            {
                assemblyMemberLookup.TryAdd(asmSignature, asmMethod);
            }
            var nameKey = MemberNameLookupKey(asmMethod.Namespace, asmMethod.ClassName, asmMethod.Name);
            if (!assemblyNameLookup.TryGetValue(nameKey, out var candidates))
            {
                candidates = [];
                assemblyNameLookup[nameKey] = candidates;
            }
            candidates.Add(asmMethod);
        }

        foreach (var method in mergedMethods)
        {
            AddMapping(method, "Method");
        }
        return (mergedMethods, mergedUsings, mergedMethodCalls, mergedProperties, mergedFields, mergedEvents, mergedConstructors, callGraph, sourceAssemblyMappings, sourceMode, new Frameworks.SourceCompilations { CSharp = csharpCompilation, VisualBasic = vbCompilation }, mergedDiagnostics);

        void AddNode(string id, string name, string? className, string? namespaceName, string? file, string? assembly, string? module, string kind, int lineNumber, int columnNumber, bool isExternal)
        {
            methodNodeLookup.TryAdd(id, new MethodNode
            {
                Id = id,
                Name = name,
                Label = string.IsNullOrWhiteSpace(className) ? name : $"{className}.{name}",
                ClassName = className ?? string.Empty,
                Namespace = namespaceName ?? string.Empty,
                Assembly = assembly,
                Module = module,
                FileName = file ?? string.Empty,
                Kind = kind,
                LineNumber = lineNumber,
                ColumnNumber = columnNumber,
                IsExternal = isExternal
            });
        }

        void AddMapping<T>(T sourceMember, string memberType) where T : class
        {
            switch (sourceMember)
            {
                case Method sourceMethod:
                {
                    var sourceId = sourceMethod.SourceSignature ?? CreateMemberId(sourceMethod.Namespace, sourceMethod.ClassName, sourceMethod.Name, sourceMethod.Parameters, sourceMethod.ReturnType);
                    object? asmMemberObj = null;
                    var isMapped = !string.IsNullOrWhiteSpace(sourceMethod.SourceSignature) && assemblyMemberLookup.TryGetValue(sourceMethod.SourceSignature, out asmMemberObj);
                    if (!isMapped)
                    {
                        var nameKey = MemberNameLookupKey(sourceMethod.Namespace, sourceMethod.ClassName, sourceMethod.Name);
                        if (assemblyNameLookup.TryGetValue(nameKey, out var candidates) && TryFindAssemblyMethodMatch(sourceMethod, candidates, out var asmMember))
                        {
                            asmMemberObj = asmMember;
                            isMapped = true;
                        }
                    }
                    if (!isMapped)
                    {
                        break;
                    }
                    string? asmId = null;
                    var asmMethod = asmMemberObj as Method;
                    if (isMapped && asmMethod is not null)
                    {
                        asmId = asmMethod.AssemblySignature;
                    }
                    sourceAssemblyMappings.Add(new SourceAssemblyMapping
                    {
                        SourceId = sourceId,
                        SourcePath = sourceMethod.Path,
                        SourceLineNumber = sourceMethod.LineNumber,
                        SourceColumnNumber = sourceMethod.ColumnNumber,
                        SourceSignature = sourceMethod.SourceSignature,
                        SourceMetadataToken = sourceMethod.MetadataToken,
                        AssemblyMetadataToken = isMapped ? (asmMemberObj is Method m ? m.MetadataToken : 0) : 0,
                        AssemblyName = GetMappedAssemblyName(asmMethod, sourceMethod.Assembly),
                        ModuleName = GetMappedModuleName(asmMethod, sourceMethod.Module),
                        AssemblyId = asmId,
                        AssemblySignature = asmId,
                        MemberType = memberType,
                        MemberName = sourceMethod.Name,
                        ClassName = sourceMethod.ClassName,
                        Namespace = sourceMethod.Namespace,
                        IsMapped = isMapped
                    });
                    break;
                }
            }
        }

        static string MemberNameLookupKey(string? namespaceName, string? className, string? memberName) => string.Join('.', new[] { namespaceName is "<global namespace>" ? null : namespaceName, className, memberName }.Where(part => !string.IsNullOrWhiteSpace(part)));

        static string? GetMappedAssemblyName(Method? assemblyMethod, string? fallback) =>
            !string.IsNullOrWhiteSpace(assemblyMethod?.Assembly)
                ? assemblyMethod.Assembly
                : !string.IsNullOrWhiteSpace(assemblyMethod?.Path)
                    ? Path.GetFileNameWithoutExtension(assemblyMethod.Path)
                    : !string.IsNullOrWhiteSpace(assemblyMethod?.FileName)
                        ? Path.GetFileNameWithoutExtension(assemblyMethod.FileName)
                        : fallback;

        static string? GetMappedModuleName(Method? assemblyMethod, string? fallback) =>
            !string.IsNullOrWhiteSpace(assemblyMethod?.Module)
                ? assemblyMethod.Module
                : !string.IsNullOrWhiteSpace(assemblyMethod?.FileName)
                    ? assemblyMethod.FileName
                    : !string.IsNullOrWhiteSpace(assemblyMethod?.Path)
                        ? Path.GetFileName(assemblyMethod.Path)
                        : fallback;

        static bool TryFindAssemblyMethodMatch(Method sourceMethod, IReadOnlyList<Method> candidates, out Method? match)
        {
            match = null;
            if (candidates.Count == 0)
            {
                return false;
            }

            var sourceParameters = sourceMethod.Parameters ?? [];
            var parameterCountMatches = candidates
                .Where(candidate => candidate.Parameters is null || candidate.Parameters.Count == sourceParameters.Count)
                .ToList();
            var typedMatches = parameterCountMatches
                .Where(candidate => ParametersMatch(sourceMethod.Parameters, candidate.Parameters))
                .ToList();
            var returnMatches = typedMatches
                .Where(candidate => ReturnTypesMatch(sourceMethod.ReturnType, candidate.ReturnType, sourceMethod.Name))
                .ToList();

            var candidateSets = new List<List<Method>> { returnMatches };
            if (returnMatches.Count == typedMatches.Count)
            {
                candidateSets.Add(typedMatches);
            }
            if (typedMatches.Count == parameterCountMatches.Count)
            {
                candidateSets.Add(parameterCountMatches);
            }

            foreach (var candidateSet in candidateSets)
            {
                if (candidateSet.Count == 1)
                {
                    match = candidateSet[0];
                    return true;
                }
            }

            return false;
        }

        static bool ParametersMatch(IReadOnlyList<Parameter>? sourceParameters, IReadOnlyList<Parameter>? assemblyParameters)
        {
            if (sourceParameters is null || assemblyParameters is null)
            {
                return true;
            }

            if (sourceParameters.Count != assemblyParameters.Count)
            {
                return false;
            }

            for (var index = 0; index < sourceParameters.Count; index++)
            {
                var sourceType = sourceParameters[index].TypeFullName ?? sourceParameters[index].Type;
                var assemblyType = assemblyParameters[index].TypeFullName ?? assemblyParameters[index].Type;
                if (!string.IsNullOrWhiteSpace(sourceType) && !string.IsNullOrWhiteSpace(assemblyType) && !MemberTypeNamesMatch(sourceType, assemblyType))
                {
                    return false;
                }
            }
            return true;
        }

        static bool ReturnTypesMatch(string? sourceReturnType, string? assemblyReturnType, string? memberName) =>
            memberName == ".ctor" || string.IsNullOrWhiteSpace(sourceReturnType) || string.IsNullOrWhiteSpace(assemblyReturnType) || MemberTypeNamesMatch(sourceReturnType, assemblyReturnType);

        static bool MemberTypeNamesMatch(string sourceType, string assemblyType)
        {
            var sourceAliases = MemberTypeAliases(sourceType).ToHashSet(StringComparer.Ordinal);
            if (MemberTypeAliases(assemblyType).Any(sourceAliases.Contains))
            {
                return true;
            }

            // IL ids embed generic instantiations (`Method<args>` decoding) while source ids
            // use the original definition. Exact aliases miss, so compare arity-stripped forms
            // before giving up; the instantiation string itself is preserved as node metadata.
            var strippedSourceAliases = MemberTypeAliases(GraphIdNormalizer.StripGenericInstantiation(sourceType)).ToHashSet(StringComparer.Ordinal);
            return MemberTypeAliases(GraphIdNormalizer.StripGenericInstantiation(assemblyType)).Any(strippedSourceAliases.Contains);
        }

        static IEnumerable<string> MemberTypeAliases(string typeName)
        {
            var normalized = NormalizeSymbolName(typeName)
                .Replace('+', '.')
                .Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                yield break;
            }

            yield return normalized;
            if (normalized.EndsWith("[]", StringComparison.Ordinal))
            {
                foreach (var elementAlias in MemberTypeAliases(normalized[..^2]))
                {
                    yield return elementAlias + "[]";
                }
            }
            var dotIndex = normalized.LastIndexOf('.');
            if (dotIndex >= 0 && dotIndex < normalized.Length - 1)
            {
                yield return normalized[(dotIndex + 1)..];
            }

            foreach (var alias in PrimitiveTypeAliases(normalized))
            {
                yield return alias;
            }
        }

        static IEnumerable<string> PrimitiveTypeAliases(string typeName)
        {
            return typeName switch
            {
                "bool" or "Boolean" or "System.Boolean" => ["bool", "Boolean", "System.Boolean"],
                "byte" or "Byte" or "System.Byte" => ["byte", "Byte", "System.Byte"],
                "char" or "Char" or "System.Char" => ["char", "Char", "System.Char"],
                "decimal" or "Decimal" or "System.Decimal" => ["decimal", "Decimal", "System.Decimal"],
                "double" or "Double" or "System.Double" => ["double", "Double", "System.Double"],
                "float" or "Single" or "System.Single" => ["float", "Single", "System.Single"],
                "int" or "Integer" or "Int32" or "System.Int32" => ["int", "Integer", "Int32", "System.Int32"],
                "long" or "Long" or "Int64" or "System.Int64" => ["long", "Long", "Int64", "System.Int64"],
                "object" or "Object" or "System.Object" => ["object", "Object", "System.Object"],
                "short" or "Short" or "Int16" or "System.Int16" => ["short", "Short", "Int16", "System.Int16"],
                "string" or "String" or "System.String" => ["string", "String", "System.String"],
                "uint" or "UInt32" or "System.UInt32" => ["uint", "UInt32", "System.UInt32"],
                "ulong" or "UInt64" or "System.UInt64" => ["ulong", "UInt64", "System.UInt64"],
                "ushort" or "UInt16" or "System.UInt16" => ["ushort", "UInt16", "System.UInt16"],
                "void" or "Void" or "System.Void" => ["void", "Void", "System.Void"],
                _ => []
            };
        }
    }

    // Using/imports alias targets may resolve to a type, or ambiguously to several
    // candidate types (e.g. the same qualified name shipped by multiple assemblies in
    // the scan directory); only namespace symbols qualify for member enrichment.
    private static INamespaceSymbol? ResolveNamespaceSymbol(SymbolInfo symbolInfo)
    {
        if (symbolInfo.Symbol is INamespaceSymbol namespaceSymbol)
        {
            return namespaceSymbol;
        }

        return symbolInfo.CandidateSymbols.OfType<INamespaceSymbol>().FirstOrDefault();
    }

    private static string GetContainingTypeName(MemberDeclarationSyntax member)
    {
        var parent = member.Parent;
        while (parent is not null)
        {
            if (parent is ClassDeclarationSyntax classDecl)
                return classDecl.Identifier.Text;
            if (parent is StructDeclarationSyntax structDecl)
                return structDecl.Identifier.Text;
            parent = parent.Parent;
        }
        return "";
    }

    /// <summary>
    ///     Name of the nearest named declaring type of a symbol. Members declared in a C# 14+
    ///     <c>extension</c> block (methods, properties, C# 15 extension indexers) are contained in
    ///     a compiler-synthesized nested type whose metadata <see cref="INamedTypeSymbol.Name" />
    ///     is empty - it only renders as <c>extension(...)</c> in display strings. Walking outward
    ///     attributes those members to the enclosing static class, so the inventory keeps the
    ///     grouping callers expect instead of an empty class name.
    /// </summary>
    private static string GetNamedContainingTypeName(ISymbol symbol)
    {
        for (var containingType = symbol.ContainingType; containingType is not null; containingType = containingType.ContainingType)
        {
            if (!string.IsNullOrEmpty(containingType.Name))
            {
                return containingType.Name;
            }
        }
        return string.Empty;
    }

    private sealed class MethodCallOperationWalker(SemanticModel model, DispatchResolver.SourceIndex dispatchIndex, List<MethodCalls> methodCalls, string sourceFilePath, string fileName, string relativePath, SourceRenderCache renderCache) : DataFlowAnalyzer.DepthBoundedOperationWalker
    {
        // Bound for hostile trees: a generated file full of broken call sites must not turn
        // into an unbounded unresolved-edge list; real unbuilt projects stay far below this.
        private const int MaxUnresolvedCallsPerFile = 512;
        private readonly HashSet<string> unresolvedCallKeys = new(StringComparer.Ordinal);

        // Roots overlap: every block, initializer and (VB) statement is handed to this walker,
        // and each nested one is already inside its parent's operation tree, which recorded a
        // call once per enclosing block and walked each member once per nesting level.
        private readonly HashSet<IOperation> visitedOperations = new(ReferenceEqualityComparer.Instance);

        // Every call of a fluent chain starts at the chain's first token, and resolving the
        // enclosing symbol descends the tree to that position, so one lookup per call made a
        // chain quadratic.
        private readonly Dictionary<int, ISymbol?> enclosingSymbols = [];

        // Calls carry no source text here, so the budget can follow the stack (and the
        // OperationDepthGuard limit) and keep every call of a long chain in the graph.
        protected override int MaxAnalysisDepth => 1 << 17;

        public override void Visit(IOperation? operation)
        {
            if (operation is null)
            {
                return;
            }

            if (MaxDepthReached)
            {
                // Left unmarked so a nested root handed over later still covers it; the base
                // records the truncation.
                base.Visit(operation);
                return;
            }

            if (visitedOperations.Add(operation))
            {
                base.Visit(operation);
            }
        }

        private ISymbol? EnclosingSymbol(int position)
        {
            if (!enclosingSymbols.TryGetValue(position, out var symbol))
            {
                symbol = model.GetEnclosingSymbol(position);
                enclosingSymbols[position] = symbol;
            }

            return symbol;
        }

        public override void VisitInvocation(IInvocationOperation operation)
        {
            AddMethodCall(operation, operation.TargetMethod, CallType.MethodCall, operation.Arguments);
            AddSourceDispatchCandidates(operation);
            AddFrameworkAndReflectionCandidates(operation);
            base.VisitInvocation(operation);
        }

        public override void VisitInvalid(IInvalidOperation operation)
        {
            AddUnresolvedCall(operation);
            base.VisitInvalid(operation);
        }

        /// <summary>
        ///     A missing package assembly turns its call sites into <c>IInvalidOperation</c>, so
        ///     <see cref="VisitInvocation" /> never fires and the call would silently vanish
        ///     from the graph - the exact silent degradation that collapses package reachability
        ///     to the dependency-only fallback on unbuilt trees. Record the site from syntax
        ///     with the weakest evidence kind, so consumers can tell "called but unresolvable"
        ///     from "not called at all". C# syntax only: the other frontends own their
        ///     inventories, and <c>IInvalidOperation</c> also covers non-call syntax.
        /// </summary>
        private void AddUnresolvedCall(IInvalidOperation operation)
        {
            if (unresolvedCallKeys.Count >= MaxUnresolvedCallsPerFile)
            {
                return;
            }
            if (operation.Syntax is not (InvocationExpressionSyntax or ObjectCreationExpressionSyntax))
            {
                return;
            }
            if (EnclosingSymbol(operation.Syntax.SpanStart) is not IMethodSymbol callerSymbol)
            {
                return;
            }
            // Only sites whose target itself is missing: an invocation into an available method
            // can still bind to IInvalidOperation when an argument carries a poisoned (error)
            // type and overload resolution fails - GetSymbolInfo then returns real candidates,
            // and emitting an unresolved edge for it would blame a reference that exists.
            if (!IsMissingTarget(operation.Syntax))
            {
                return;
            }
            var cleanName = CleanUnresolvedName(operation.Syntax is InvocationExpressionSyntax invocation
                ? SafeSyntaxText.Text(invocation.Expression)
                : SafeSyntaxText.Text(((ObjectCreationExpressionSyntax)operation.Syntax).Type));
            if (string.IsNullOrWhiteSpace(cleanName))
            {
                return;
            }
            var location = operation.Syntax.GetLocation().GetLineSpan().StartLinePosition;
            var key = $"{location.Line}:{location.Character}:{cleanName}";
            if (!unresolvedCallKeys.Add(key))
            {
                return;
            }

            var isCreation = operation.Syntax is ObjectCreationExpressionSyntax;
            var segments = cleanName.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            // The type part of the name: everything for a constructor call, everything but
            // the method name for an invocation ("JsonConvert.SerializeObject" -> "JsonConvert").
            var typeSegments = isCreation ? segments : segments.Length > 1 ? segments[..^1] : segments;
            // A namespace is recorded ONLY when the receiver's own qualification states it
            // ("Newtonsoft.Json.JsonConvert.SerializeObject"). Guessing from the file's using
            // directives fabricates reachability: an unresolvable type in a file that imports
            // exactly one package would promote that innocent package from dependency-only to
            // reachable, and ReachabilityKind is what downstream consumers trust. Unqualified
            // receivers keep a null namespace, a null purl, and the dependency-only fallback.
            string? namespaceGuess = null;
            var className = typeSegments.Length > 0 ? typeSegments[^1] : null;
            if (typeSegments.Length >= 2 && IsNamespaceQualification(operation.Syntax, typeSegments[..^1]))
            {
                namespaceGuess = string.Join(".", typeSegments[..^1]);
            }

            var argumentTexts = operation.Syntax switch
            {
                InvocationExpressionSyntax invoked => invoked.ArgumentList.Arguments.Select(argument => SafeSyntaxText.Text(argument.Expression)).ToList(),
                ObjectCreationExpressionSyntax created => created.ArgumentList?.Arguments.Select(argument => SafeSyntaxText.Text(argument.Expression)).ToList() ?? [],
                _ => []
            };
            methodCalls.Add(new MethodCalls
            {
                Path = relativePath,
                FileName = fileName,
                Namespace = namespaceGuess,
                ClassName = className,
                CalledMethod = isCreation ? className ?? cleanName : segments[^1],
                LineNumber = location.Line + 1,
                ColumnNumber = location.Character + 1,
                Arguments = argumentTexts,
                ArgumentExpressions = argumentTexts,
                CallType = isCreation ? CallType.ConstructorCall : CallType.MethodCall,
                SourceId = renderCache.Signature(callerSymbol),
                TargetId = $"Unresolved:{cleanName}",
                CallerMethod = renderCache.MemberName(callerSymbol),
                CallerNamespace = renderCache.Display(callerSymbol.ContainingNamespace),
                CallerClass = GetNamedContainingTypeName(callerSymbol),
                IsInternal = false,
                EvidenceKind = AnalysisEvidenceKind.SourceUnresolved,
                Evidence =
                [
                    new AnalysisEvidence
                    {
                        Kind = AnalysisEvidenceKind.SourceUnresolved,
                        Source = "roslyn-source-unresolved",
                        Confidence = "Low",
                        Description = "Call site did not bind; the target assembly was not available to the compilation.",
                        FileName = fileName,
                        LineNumber = location.Line + 1,
                        ColumnNumber = location.Character + 1
                    }
                ]
            });
        }

        private static string CleanUnresolvedName(string name)
        {
            var cleaned = name.Replace("global::", string.Empty, StringComparison.Ordinal).Trim();
            var genericIndex = cleaned.IndexOf('<');
            if (genericIndex >= 0)
            {
                cleaned = cleaned[..genericIndex].Trim();
            }
            return cleaned;
        }

        /// <summary>
        ///     True when the leading segments of an unresolved receiver really are a namespace
        ///     qualification (<c>Newtonsoft.Json</c> in <c>Newtonsoft.Json.JsonConvert.Serialize</c>)
        ///     rather than a value chain (<c>client</c> in <c>client.Inner.Send</c>). Recording
        ///     the head of a value chain as a namespace puts noise in the Namespace field of
        ///     every unresolved edge, so the head has to look like, and resolve like, a
        ///     namespace: a symbol that resolves to a local, parameter, field or property is
        ///     decisive, and for the common case where nothing resolves at all the .NET naming
        ///     convention (namespace segments are capitalised) is the tiebreak.
        /// </summary>
        private bool IsNamespaceQualification(Microsoft.CodeAnalysis.SyntaxNode syntax, string[] namespaceSegments)
        {
            foreach (var segment in namespaceSegments)
            {
                if (segment.Length == 0 || !(char.IsUpper(segment[0]) || segment[0] == '_'))
                {
                    return false;
                }
            }
            var leftmost = syntax switch
            {
                InvocationExpressionSyntax invocation => LeftmostIdentifier(invocation.Expression),
                ObjectCreationExpressionSyntax created => LeftmostIdentifier(created.Type),
                _ => null
            };
            if (leftmost is null)
            {
                return true;
            }
            // A resolved value receiver is never a namespace, whatever it is named.
            return model.GetSymbolInfo(leftmost).Symbol is not (ILocalSymbol or IParameterSymbol or IFieldSymbol or IPropertySymbol);
        }

        private static IdentifierNameSyntax? LeftmostIdentifier(Microsoft.CodeAnalysis.SyntaxNode? node) => node switch
        {
            IdentifierNameSyntax identifier => identifier,
            MemberAccessExpressionSyntax memberAccess => LeftmostIdentifier(memberAccess.Expression),
            Microsoft.CodeAnalysis.CSharp.Syntax.QualifiedNameSyntax qualified => LeftmostIdentifier(qualified.Left),
            Microsoft.CodeAnalysis.CSharp.Syntax.AliasQualifiedNameSyntax aliased => LeftmostIdentifier(aliased.Name),
            _ => null
        };

        /// <summary>
        ///     True when the call's TARGET (the receiver it is made on, or the type being
        ///     created) resolved to nothing. An <c>IInvalidOperation</c> at a call site has two
        ///     very different causes: a missing reference makes the receiver/type an error
        ///     symbol (worth reporting), while a resolved receiver reached with a poisoned
        ///     argument or an inexpressible overload fails resolution downstream - a symptom of
        ///     some OTHER missing reference, and blaming it here would flag APIs that exist.
        ///     The receiver is therefore the decisive signal: a valid receiver type, or a
        ///     namespace receiver that resolves (the <c>Path</c> in <c>Path.GetFileName</c>),
        ///     means the call is not an unresolved-target site.
        /// </summary>
        private bool IsMissingTarget(Microsoft.CodeAnalysis.SyntaxNode syntax)
        {
            if (syntax is ObjectCreationExpressionSyntax created)
            {
                return IsErrorSymbol(model.GetSymbolInfo(created.Type).Symbol);
            }
            if (syntax is not InvocationExpressionSyntax invocation)
            {
                return false;
            }
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
            {
                // A bare identifier is a method group with no receiver to vouch for it: with
                // no symbol and no candidates, no method of that name was found anywhere.
                var identifierInfo = model.GetSymbolInfo(invocation.Expression);
                return identifierInfo.Symbol is null && identifierInfo.CandidateSymbols.Length == 0;
            }
            var receiver = memberAccess.Expression;
            var receiverType = model.GetTypeInfo(receiver).Type;
            if (receiverType is not null)
            {
                return receiverType.TypeKind == TypeKind.Error || receiverType.Kind == SymbolKind.ErrorType;
            }
            // Namespace receivers have no type; resolve the symbol instead (Path, System.Console).
            return IsErrorSymbol(model.GetSymbolInfo(receiver).Symbol);
        }

        private static bool IsErrorSymbol(ISymbol? symbol) => symbol is null || symbol.Kind == SymbolKind.ErrorType;

        public override void VisitObjectCreation(IObjectCreationOperation operation)
        {
            AddMethodCall(operation, operation.Constructor, CallType.ConstructorCall, operation.Arguments);
            AddReflectionConstructorCandidate(operation);
            base.VisitObjectCreation(operation);
        }

        public override void VisitPropertyReference(IPropertyReferenceOperation operation)
        {
            var callType = IsPropertyWrite(operation) ? CallType.PropertySet : CallType.PropertyGet;
            var accessor = callType == CallType.PropertySet ? operation.Property.SetMethod : operation.Property.GetMethod;
            AddMethodCall(operation, accessor, callType, []);
            base.VisitPropertyReference(operation);
        }

        public override void VisitDelegateCreation(IDelegateCreationOperation operation)
        {
            AddCallbackTarget(operation, operation.Target, CallType.DelegateInvoke, AnalysisEvidenceKind.SourceRoslynDelegateTarget, "Delegate target inferred from Roslyn delegate creation operation.");
            base.VisitDelegateCreation(operation);
        }

        public override void VisitEventAssignment(IEventAssignmentOperation operation)
        {
            AddCallbackTarget(operation, operation.HandlerValue, operation.Adds ? CallType.EventSubscribe : CallType.EventUnsubscribe, AnalysisEvidenceKind.SourceRoslynDelegateTarget, "Event handler target inferred from Roslyn event assignment operation.");
            base.VisitEventAssignment(operation);
        }

        public override void VisitAnonymousFunction(IAnonymousFunctionOperation operation)
        {
            AddInferredMethodCall(operation, operation.Symbol, CallType.DelegateInvoke, [], ["source-lambda-target"], AnalysisEvidenceKind.SourceRoslynDelegateTarget, "Lambda callback target inferred from Roslyn anonymous-function operation.", "Medium", operation.Symbol.ContainingSymbol as IMethodSymbol);
            base.VisitAnonymousFunction(operation);
        }

        private void AddMethodCall(IOperation operation, IMethodSymbol? targetMethod, CallType callType, IEnumerable<IArgumentOperation> arguments)
        {
            // A primary constructor's base call binds at the base type's name, where the
            // enclosing symbol is the type; inside its argument list it is the constructor.
            var callerPosition = operation.Syntax is PrimaryConstructorBaseTypeSyntax baseType
                ? baseType.ArgumentList.OpenParenToken.Span.End
                : operation.Syntax.SpanStart;
            if (targetMethod is null || EnclosingSymbol(callerPosition) is not IMethodSymbol callerSymbol)
            {
                return;
            }

            var location = operation.Syntax.GetLocation().GetLineSpan().StartLinePosition;
            var sourceId = renderCache.Signature(callerSymbol);
            var targetId = renderCache.Signature(targetMethod);
            if (string.IsNullOrWhiteSpace(sourceId) || string.IsNullOrWhiteSpace(targetId))
            {
                return;
            }

            var argumentList = arguments.Select(argument => renderCache.NormalizedFullyQualified(argument.Parameter?.Type ?? argument.Value.Type)).ToList();
            var argumentExpressions = arguments.Select(argument => SafeSyntaxText.Text(argument.Value.Syntax)).ToList();
            var targetLocations = targetMethod.Locations;
            var isInMetadata = targetLocations.Any(locationInfo => locationInfo.IsInMetadata);
            var isInSource = targetLocations.Any(locationInfo => locationInfo.IsInSource);
            var isInternal = isInSource || SymbolEqualityComparer.Default.Equals(targetMethod.ContainingAssembly, model.Compilation.Assembly);
            var calledMethod = targetMethod.MethodKind == MethodKind.Constructor
                ? targetMethod.ContainingType?.Name ?? targetMethod.Name
                : IsNestedFunction(targetMethod) ? renderCache.MemberName(targetMethod) : renderCache.NormalizedErrorMessage(targetMethod);

            methodCalls.Add(new MethodCalls
            {
                Path = relativePath,
                FileName = fileName,
                Assembly = renderCache.Display(targetMethod.ContainingAssembly),
                Module = renderCache.Display(targetMethod.ContainingModule),
                Namespace = renderCache.Display(targetMethod.ContainingNamespace),
                ClassName = GetNamedContainingTypeName(targetMethod),
                CalledMethod = calledMethod,
                LineNumber = location.Line + 1,
                ColumnNumber = location.Character + 1,
                Arguments = argumentList,
                ArgumentExpressions = argumentExpressions,
                CallType = callType,
                SourceId = sourceId,
                TargetId = targetId,
                CallerMethod = renderCache.MemberName(callerSymbol),
                CallerNamespace = renderCache.Display(callerSymbol.ContainingNamespace),
                CallerClass = GetNamedContainingTypeName(callerSymbol),
                IsInternal = isInternal && !isInMetadata,
                EvidenceKind = AnalysisEvidenceKind.SourceRoslynDirect,
                Evidence =
                [
                    new AnalysisEvidence
                    {
                        Kind = AnalysisEvidenceKind.SourceRoslynDirect,
                        Source = "roslyn-source",
                        Description = "Call discovered from Roslyn semantic operation.",
                        FileName = fileName,
                        LineNumber = location.Line + 1,
                        ColumnNumber = location.Character + 1
                    }
                ]
            });
        }

        /// <summary>Call sites of this file whose dispatch lookup lost a candidate to a Roslyn failure (issue #64).</summary>
        public int AbandonedDispatchSites { get; private set; }

        /// <summary>Location and member of the first such call site, for the slice diagnostic.</summary>
        public string? FirstAbandonedDispatch { get; private set; }

        private void AddSourceDispatchCandidates(IInvocationOperation operation)
        {
            // A non-virtual invocation of a virtual member - `base.M()`, VB `MyBase.M()` /
            // `MyClass.M()` - runs exactly the bound method, so no override is a candidate.
            if (!operation.IsVirtual || !ShouldInferDispatchCandidates(operation.TargetMethod))
            {
                return;
            }

            var lookup = dispatchIndex.Lookup(operation.TargetMethod, operation.Instance?.Type);
            if (lookup.Failure is { } failure)
            {
                AbandonedDispatchSites++;
                if (FirstAbandonedDispatch is null)
                {
                    var line = operation.Syntax.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    FirstAbandonedDispatch = string.Create(CultureInfo.InvariantCulture, $"{sourceFilePath}:{line} ('{failure.MemberName}' on '{failure.TypeName}', {failure.ExceptionType})");
                }
            }

            // Sealed/struct receivers resolve to the exact implementation; remaining candidates
            // are ranked instantiated-first with per-edge confidence. Even an exact resolution is a
            // *synthesized* edge, not a witnessed call site, so it keeps the VirtualCandidate
            // evidence kind, consumers that trust SourceRoslynDirect must only see real call
            // sites. DispatchConfidence = "exact" carries the resolution fact.
            foreach (var (candidate, dispatchConfidence) in lookup.Candidates.Take(16))
            {
                var isExact = dispatchConfidence == "exact";
                AddInferredMethodCall(
                    operation,
                    candidate,
                    CallType.MethodCall,
                    operation.Arguments,
                    [isExact ? "source-dispatch-exact" : "source-dispatch-candidate"],
                    AnalysisEvidenceKind.SourceRoslynVirtualCandidate,
                    isExact
                        ? "Dispatch resolved exactly (sealed receiver); edge synthesized from type hierarchy, not a witnessed call site."
                        : "Virtual/interface dispatch candidate inferred from source type hierarchy.",
                    isExact ? "High" : dispatchConfidence == "rta-candidate" ? "Medium" : "Low",
                    dispatchConfidence: dispatchConfidence);
            }
        }

        private void AddFrameworkAndReflectionCandidates(IInvocationOperation operation)
        {
            AddDelegateArgumentCallbackCandidates(operation);
            AddDiFrameworkCandidates(operation);
            AddReflectionCandidates(operation);
        }

        private void AddDelegateArgumentCallbackCandidates(IInvocationOperation operation)
        {
            if (!IsFrameworkCallbackRegistration(operation.TargetMethod))
            {
                return;
            }

            foreach (var argument in operation.Arguments)
            {
                if (argument.Value.Type?.TypeKind == TypeKind.Delegate)
                {
                    AddCallbackTarget(operation, argument.Value, CallType.DelegateInvoke, AnalysisEvidenceKind.FrameworkModel, "Framework callback target inferred from delegate argument registration.");
                }
            }
        }

        private void AddDiFrameworkCandidates(IInvocationOperation operation)
        {
            var methodName = operation.TargetMethod.Name;
            if (methodName is "AddSingleton" or "AddScoped" or "AddTransient" or "AddHostedService")
            {
                foreach (var implementationType in ResolveRegistrationImplementationTypes(operation))
                {
                    AddTypeConstructorCandidate(operation, implementationType, AnalysisEvidenceKind.FrameworkModel, $"DI registration {methodName} inferred service implementation constructor.");
                }
            }

            if (methodName is "GetService" or "GetRequiredService")
            {
                foreach (var serviceType in ResolveServiceProviderTypes(operation))
                {
                    AddTypeConstructorCandidate(operation, serviceType, AnalysisEvidenceKind.FrameworkModel, $"DI service resolution {methodName} inferred service constructor.");
                }
            }
        }

        private void AddReflectionCandidates(IInvocationOperation operation)
        {
            var method = operation.TargetMethod;
            var containingType = NormalizeSymbolName(method.ContainingType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? string.Empty);
            if (containingType == "System.Activator" && method.Name == "CreateInstance")
            {
                foreach (var createdType in ResolveActivatorTypes(operation))
                {
                    AddTypeConstructorCandidate(operation, createdType, AnalysisEvidenceKind.ReflectionHeuristic, "Activator.CreateInstance target inferred from generic type or typeof argument.");
                }
            }

            if (method.Name == "GetMethod" && operation.Instance is ITypeOfOperation typeOf && operation.Arguments.FirstOrDefault()?.Value is { } methodNameOperation && TryGetStringConstant(methodNameOperation, out var reflectedMethodName))
            {
                foreach (var reflectedMethod in ResolveMethodsByName(typeOf.TypeOperand, reflectedMethodName))
                {
                    AddInferredMethodCall(operation, reflectedMethod, CallType.MethodCall, [], ["reflection-getmethod"], AnalysisEvidenceKind.ReflectionHeuristic, "Reflection GetMethod target inferred from typeof(...) and literal method name.", "Low");
                }
            }
        }

        private void AddReflectionConstructorCandidate(IObjectCreationOperation operation)
        {
            if (operation.Type is INamedTypeSymbol createdType && IsFrameworkType(operation.Constructor?.ContainingType))
            {
                AddTypeConstructorCandidate(operation, createdType, AnalysisEvidenceKind.FrameworkModel, "Framework-created type constructor inferred from object creation.");
            }
        }

        private IEnumerable<INamedTypeSymbol> ResolveRegistrationImplementationTypes(IInvocationOperation operation)
        {
            foreach (var typeArgument in operation.TargetMethod.TypeArguments.OfType<INamedTypeSymbol>().Reverse().Take(1))
            {
                yield return typeArgument;
            }

            foreach (var argument in operation.Arguments)
            {
                if (argument.Value is ITypeOfOperation { TypeOperand: INamedTypeSymbol namedType })
                {
                    yield return namedType;
                }
            }
        }

        private IEnumerable<INamedTypeSymbol> ResolveServiceProviderTypes(IInvocationOperation operation)
        {
            foreach (var typeArgument in operation.TargetMethod.TypeArguments.OfType<INamedTypeSymbol>())
            {
                yield return typeArgument;
            }
            foreach (var argument in operation.Arguments)
            {
                if (argument.Value is ITypeOfOperation { TypeOperand: INamedTypeSymbol namedType })
                {
                    yield return namedType;
                }
            }
        }

        private IEnumerable<INamedTypeSymbol> ResolveActivatorTypes(IInvocationOperation operation)
        {
            foreach (var typeArgument in operation.TargetMethod.TypeArguments.OfType<INamedTypeSymbol>())
            {
                yield return typeArgument;
            }
            foreach (var argument in operation.Arguments)
            {
                if (argument.Value is ITypeOfOperation { TypeOperand: INamedTypeSymbol namedType })
                {
                    yield return namedType;
                }
            }
        }

        private IEnumerable<IMethodSymbol> ResolveMethodsByName(ITypeSymbol type, string methodName)
        {
            if (type is not INamedTypeSymbol namedType)
            {
                yield break;
            }

            foreach (var method in namedType.GetMembers(methodName).OfType<IMethodSymbol>().Where(method => method.MethodKind == MethodKind.Ordinary))
            {
                yield return method;
            }
        }

        private void AddTypeConstructorCandidate(IOperation operation, INamedTypeSymbol type, AnalysisEvidenceKind evidenceKind, string description)
        {
            var constructor = type.InstanceConstructors
                .Where(ctor => !ctor.IsStatic)
                .OrderBy(ctor => ctor.Parameters.Length)
                .FirstOrDefault();
            if (constructor is null || constructor.IsImplicitlyDeclared)
            {
                return;
            }

            AddInferredMethodCall(operation, constructor, CallType.ConstructorCall, [], [evidenceKind == AnalysisEvidenceKind.ReflectionHeuristic ? "reflection-constructor" : "framework-constructor"], evidenceKind, description, evidenceKind == AnalysisEvidenceKind.ReflectionHeuristic ? "Low" : "Medium");
        }

        private bool TryGetStringConstant(IOperation operation, out string value)
        {
            var constant = model.GetConstantValue(operation.Syntax);
            value = constant.HasValue ? constant.Value as string ?? string.Empty : string.Empty;
            return !string.IsNullOrWhiteSpace(value);
        }

        private static bool IsFrameworkCallbackRegistration(IMethodSymbol method)
        {
            var name = method.Name;
            var ns = method.ContainingNamespace?.ToDisplayString() ?? string.Empty;
            return ns.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) || name.StartsWith("Map", StringComparison.Ordinal) || name.StartsWith("Use", StringComparison.Ordinal) || name is "Run" or "On" or "Subscribe";
        }

        private static bool IsFrameworkType(INamedTypeSymbol? type)
        {
            var ns = type?.ContainingNamespace?.ToDisplayString() ?? string.Empty;
            return ns.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) || ns.StartsWith("Microsoft.Extensions", StringComparison.Ordinal);
        }

        private void AddCallbackTarget(IOperation operation, IOperation? callbackOperation, CallType callType, AnalysisEvidenceKind evidenceKind, string description)
        {
            var callback = ResolveCallbackMethod(callbackOperation);
            if (callback is null)
            {
                return;
            }

            AddInferredMethodCall(operation, callback, callType, [], ["source-callback-target"], evidenceKind, description, "Medium");
        }

        private void AddInferredMethodCall(IOperation operation, IMethodSymbol targetMethod, CallType callType, IEnumerable<IArgumentOperation> arguments, List<string> argumentExpressions, AnalysisEvidenceKind evidenceKind, string description, string confidence, IMethodSymbol? callerOverride = null, string? dispatchConfidence = null)
        {
            if ((callerOverride ?? EnclosingSymbol(operation.Syntax.SpanStart)) is not IMethodSymbol callerSymbol)
            {
                return;
            }

            var sourceId = renderCache.Signature(callerSymbol);
            var targetId = renderCache.Signature(targetMethod);
            if (string.IsNullOrWhiteSpace(sourceId) || string.IsNullOrWhiteSpace(targetId) || sourceId == targetId)
            {
                return;
            }

            var location = operation.Syntax.GetLocation().GetLineSpan().StartLinePosition;
            var argumentList = arguments.Select(argument => renderCache.NormalizedFullyQualified(argument.Parameter?.Type ?? argument.Value.Type)).ToList();
            var targetLocations = targetMethod.Locations;
            var isInMetadata = targetLocations.Any(locationInfo => locationInfo.IsInMetadata);
            var isInSource = targetLocations.Any(locationInfo => locationInfo.IsInSource);

            methodCalls.Add(new MethodCalls
            {
                Path = relativePath,
                FileName = fileName,
                Assembly = renderCache.Display(targetMethod.ContainingAssembly),
                Module = renderCache.Display(targetMethod.ContainingModule),
                Namespace = renderCache.Display(targetMethod.ContainingNamespace),
                ClassName = GetNamedContainingTypeName(targetMethod),
                CalledMethod = targetMethod.MethodKind == MethodKind.Constructor ? targetMethod.ContainingType?.Name ?? targetMethod.Name
                    : IsNestedFunction(targetMethod) ? renderCache.MemberName(targetMethod) : renderCache.NormalizedErrorMessage(targetMethod),
                LineNumber = location.Line + 1,
                ColumnNumber = location.Character + 1,
                Arguments = argumentList,
                ArgumentExpressions = argumentExpressions,
                CallType = callType,
                SourceId = sourceId,
                TargetId = targetId,
                CallerMethod = renderCache.MemberName(callerSymbol),
                CallerNamespace = callerSymbol.ContainingNamespace?.ToDisplayString() ?? string.Empty,
                CallerClass = GetNamedContainingTypeName(callerSymbol),
                IsInternal = (isInSource || SymbolEqualityComparer.Default.Equals(targetMethod.ContainingAssembly, model.Compilation.Assembly)) && !isInMetadata,
                EvidenceKind = evidenceKind,
                DispatchConfidence = dispatchConfidence,
                Evidence =
                [
                    new AnalysisEvidence
                    {
                        Kind = evidenceKind,
                        Source = "roslyn-source-inferred",
                        Description = description,
                        Confidence = confidence,
                        FileName = fileName,
                        LineNumber = location.Line + 1,
                        ColumnNumber = location.Character + 1
                    }
                ]
            });
        }

        private bool ShouldInferDispatchCandidates(IMethodSymbol targetMethod)
        {
            if (targetMethod.IsStatic || targetMethod.MethodKind != MethodKind.Ordinary || targetMethod.ContainingType is null)
            {
                return false;
            }

            var containingType = targetMethod.ContainingType;
            var sourceOrApplicationType = containingType.Locations.Any(location => location.IsInSource) || SymbolEqualityComparer.Default.Equals(containingType.ContainingAssembly, model.Compilation.Assembly);
            return sourceOrApplicationType && (containingType.TypeKind == TypeKind.Interface || targetMethod.IsVirtual || targetMethod.IsAbstract || targetMethod.IsOverride);
        }

        private static IMethodSymbol? ResolveCallbackMethod(IOperation? operation)
        {
            while (operation is IConversionOperation conversion)
            {
                operation = conversion.Operand;
            }

            return operation switch
            {
                IDelegateCreationOperation delegateCreation => ResolveCallbackMethod(delegateCreation.Target),
                IMethodReferenceOperation methodReference => methodReference.Method,
                IAnonymousFunctionOperation anonymousFunction => anonymousFunction.Symbol,
                _ => null
            };
        }

        private static bool IsPropertyWrite(IPropertyReferenceOperation operation)
        {
            var current = (IOperation)operation;
            var parent = operation.Parent;
            while (parent is IConversionOperation or IParenthesizedOperation)
            {
                current = parent;
                parent = parent.Parent;
            }

            return parent switch
            {
                IAssignmentOperation assignment when ReferenceEquals(assignment.Target, current) => true,
                ICompoundAssignmentOperation compound when ReferenceEquals(compound.Target, current) => true,
                IIncrementOrDecrementOperation => true,
                _ => false
            };
        }

    }

    /// <summary>
    /// Get list of files to inspect for the given path and file extensions
    /// </summary>
    /// <param name="path">Filesystem path to assembly/source file or directory containing assembly/source files</param>
    /// <param name="fileExtensions">File extensions to search for</param>
    /// <returns>List of files</returns>
    private static List<string> GetFilesToInspect(string path, params string[]? fileExtensions)
    {
        var filesToInspect = new List<string>();
        var fileAttributes = File.GetAttributes(path);
        if (fileExtensions is null || fileExtensions.Length == 0)
        {
            return filesToInspect; // Return empty list if no extensions provided
        }
        if (fileAttributes.HasFlag(FileAttributes.Directory))
        {
            var sourceExtensions = new HashSet<string>([Constants.CSharpSourceExtension, Constants.VBSourceExtension, Constants.FSharpSourceExtension], StringComparer.OrdinalIgnoreCase);
            // One walk for every requested extension: repeating the recursive enumeration per
            // extension multiplied both the I/O and the warnings a hostile tree produces. Results
            // are bucketed so the caller still sees them grouped by extension, in the requested
            // order, and only matching paths are held.
            var buckets = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var extension in fileExtensions)
            {
                buckets.TryAdd(extension, []);
            }
            // Best-effort discovery: unreadable or over-long subtrees are skipped with a
            // console warning; the inspection continues with the readable remainder.
            using var discoveryPhase = DebugLog.Enabled ? DebugLog.Phase($"discovery {string.Join("/", fileExtensions)}") : null;
            var skippedBuildOutput = 0;
            var excludedBefore = DebugLog.ExcludedTotals;
            foreach (var inputFile in SafeFileRead.EnumerateAllFilesSafe(path))
            {
                var extension = Path.GetExtension(inputFile);
                if (!buckets.TryGetValue(extension, out var bucket))
                {
                    continue;
                }
                var relativePath = Path.GetRelativePath(path, inputFile);
                if (HasDirectorySegment(relativePath, "obj") ||
                    (sourceExtensions.Contains(extension) && HasDirectorySegment(relativePath, "bin")) ||
                    inputFile.EndsWith($".g{extension}", StringComparison.OrdinalIgnoreCase))
                {
                    skippedBuildOutput++;
                    continue;
                }
                bucket.Add(inputFile);
            }
            if (DebugLog.Enabled)
            {
                var found = string.Join(", ", fileExtensions.Distinct(StringComparer.OrdinalIgnoreCase).Select(extension => $"{buckets[extension].Count} {extension}"));
                var excludedAfter = DebugLog.ExcludedTotals;
                DebugLog.Log($"discovered under '{path}': {found}; {skippedBuildOutput} skipped in obj/bin or generated; --exclude pruned {excludedAfter.Directories - excludedBefore.Directories} director(ies) and skipped {excludedAfter.Files - excludedBefore.Files} file(s)");
            }
            foreach (var extension in fileExtensions)
            {
                if (buckets.Remove(extension, out var bucket))
                {
                    filesToInspect.AddRange(bucket);
                }
            }
        }
        else
        {
            var extension = Path.GetExtension(path);
            // Check if the file extension matches any of the provided extensions
            if (fileExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                filesToInspect.Add(path);    
            }
        }
        return filesToInspect;
    }

    private static bool HasDirectorySegment(string relativePath, string segment)
    {
        var parts = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Take(Math.Max(0, parts.Length - 1)).Any(part => part.Equals(segment, StringComparison.OrdinalIgnoreCase));
    }
}