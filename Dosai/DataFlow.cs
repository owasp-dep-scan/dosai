using System.Reflection.PortableExecutable;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.VisualBasic;
using CSharpCompilationUnitSyntax = Microsoft.CodeAnalysis.CSharp.Syntax.CompilationUnitSyntax;
using VisualBasicCompilationUnitSyntax = Microsoft.CodeAnalysis.VisualBasic.Syntax.CompilationUnitSyntax;

namespace Depscan;

public enum DataFlowPatternTarget
{
    Source,
    Sink,
    Passthrough,
    Sanitizer
}

public enum DataFlowPatternKind
{
    Symbol,
    Method,
    Type,
    Namespace,
    Name,
    Parameter,
    Attribute,
    Code
}

public enum DataFlowMatchKind
{
    Contains,
    Exact,
    Prefix,
    Suffix,
    Regex
}

public sealed class DataFlowPattern
{
    public DataFlowPatternTarget Target { get; set; }
    public DataFlowPatternKind Kind { get; set; } = DataFlowPatternKind.Symbol;
    public DataFlowMatchKind Match { get; set; } = DataFlowMatchKind.Contains;
    public required string Pattern { get; set; }
    public string? Category { get; set; }
    public string? Purl { get; set; }
    public string? Description { get; set; }
    public List<string> TaintKinds { get; set; } = [];
    public List<string> RemovesTaintKinds { get; set; } = [];
    public string Confidence { get; set; } = "Medium";
    /// <summary>Optional severity override (info/low/medium/high/critical). Empty falls back to the per-category default.</summary>
    public string? Severity { get; set; }
}

public sealed class DataFlowPatternSet
{
    public List<DataFlowPattern> Sources { get; set; } = [];
    public List<DataFlowPattern> Sinks { get; set; } = [];
    public List<DataFlowPattern> Passthroughs { get; set; } = [];
    public List<DataFlowPattern> Sanitizers { get; set; } = [];
    public List<string> PatternPacks { get; set; } = [];
}

public sealed class DataFlowMethodSummary
{
    public required string Method { get; set; }
    public List<int> ReturnParameterIndexes { get; set; } = [];
    public List<int> SinkParameterIndexes { get; set; } = [];
    public List<string> SinkCategories { get; set; } = [];
    public List<string> TaintKinds { get; set; } = [];
    public List<string> FieldPaths { get; set; } = [];
    public string SummaryKind { get; set; } = "InferredLocal";
    public string Confidence { get; set; } = "Medium";
    public MethodIdentity? Identity { get; set; }
    public AnalysisEvidenceKind EvidenceKind { get; set; } = AnalysisEvidenceKind.Unknown;
    public List<AnalysisEvidence> Evidence { get; set; } = [];
}

public sealed class DataFlowNode
{
    public required string Id { get; set; }
    public required string Kind { get; set; }
    public required string Name { get; set; }
    public string? Symbol { get; set; }
    public string? Type { get; set; }
    public string? Purl { get; set; }
    public string? Code { get; set; }
    public string? Path { get; set; }
    public string? FileName { get; set; }
    public string? Namespace { get; set; }
    public string? ClassName { get; set; }
    public string? MethodName { get; set; }
    public int LineNumber { get; set; }
    public int ColumnNumber { get; set; }
    public bool IsSource { get; set; }
    public bool IsSink { get; set; }
    public List<string> MatchedPatterns { get; set; } = [];
    public string? Category { get; set; }
    public MethodIdentity? MethodIdentity { get; set; }
    public List<AnalysisEvidence> Evidence { get; set; } = [];
    public Dictionary<string, string> Properties { get; set; } = [];
}

public sealed class DataFlowEdge
{
    public required string Id { get; set; }
    public required string SourceId { get; set; }
    public required string TargetId { get; set; }
    public required string Kind { get; set; }
    public string? Label { get; set; }
    public string? SourcePurl { get; set; }
    public string? TargetPurl { get; set; }
    public string? Path { get; set; }
    public string? FileName { get; set; }
    public int LineNumber { get; set; }
    public int ColumnNumber { get; set; }
}

public sealed class DataFlowSlice
{
    public required string Id { get; set; }
    public required string SourceId { get; set; }
    public required string SinkId { get; set; }
    public List<string> NodeIds { get; set; } = [];
    public List<string> EdgeIds { get; set; } = [];
    public string? SourceCategory { get; set; }
    public string? SinkCategory { get; set; }
    public string? SourcePurl { get; set; }
    public string? SinkPurl { get; set; }
    public List<string> Purls { get; set; } = [];
    public string? SinkArgument { get; set; }
    public int? SinkArgumentIndex { get; set; }
    public string? Summary { get; set; }
    public List<string> TaintKinds { get; set; } = [];
    public List<string> FieldPaths { get; set; } = [];
    public string Confidence { get; set; } = "Medium";
    /// <summary>Derived severity (info/low/medium/high/critical) from the sink pattern or the per-category default.</summary>
    public string Severity { get; set; } = "medium";
}

/// <summary>
///     Negative evidence: a flow that would have reached a sink but was suppressed by a sanitizer
///     expression or a validation guard. Answers "why did my flow disappear?" and feeds audit trails.
/// </summary>
public sealed class SanitizedFlow
{
    public required string Id { get; set; }
    /// <summary>Either <c>SanitizerMatch</c> (expression sanitizer) or <c>SanitizerGuard</c> (branch guard).</summary>
    public required string Kind { get; set; }
    public List<string> SourceIds { get; set; } = [];
    public string? SourceCategory { get; set; }
    /// <summary>The sanitized expression text (for guards: the guard condition text).</summary>
    public string? Expression { get; set; }
    public string? SanitizerSymbol { get; set; }
    public string? SanitizerCategory { get; set; }
    public List<string> RemovesTaintKinds { get; set; } = [];
    public string? FileName { get; set; }
    public int LineNumber { get; set; }
    public int ColumnNumber { get; set; }
}

/// <summary>
///     A resolved route → call path → taint slice → sink chain: the concrete graph-derived linkage
///     between an attacker-reachable entry point and a dangerous sink (R2).
/// </summary>
public sealed class ExploitChain
{
    public required string Id { get; set; }
    public required string EntryPointId { get; set; }
    /// <summary>Exposure classification, e.g. anonymous-http, authenticated-http, rpc, cli, queue, mcp.</summary>
    public required string Exposure { get; set; }
    public string? WeaknessId { get; set; }
    public string? SliceId { get; set; }
    public string? SourceNodeId { get; set; }
    public string? SinkNodeId { get; set; }
    /// <summary>Method ids along the call path from the entry point to the taint source's method (bounded, ≤8 hops).</summary>
    public List<string> CallPath { get; set; } = [];
    public int HopCount { get; set; }
    public string Confidence { get; set; } = "Medium";
    public string? Summary { get; set; }
}

public sealed class DataFlowStatistics
{
    public int SourceCount { get; set; }
    public int SinkCount { get; set; }
    public int SliceCount { get; set; }
    public int NodeCount { get; set; }
    public int EdgeCount { get; set; }
    public int FilesAnalyzed { get; set; }
}

public sealed class DataFlowResult
{
    public AnalysisMetadata Metadata { get; set; } = new();
    public List<EntryPoint> EntryPoints { get; set; } = [];
    public List<DataFlowNode> Nodes { get; set; } = [];
    public List<DataFlowEdge> Edges { get; set; } = [];
    public List<DataFlowSlice> Slices { get; set; } = [];
    public List<SanitizedFlow> SanitizedFlows { get; set; } = [];
    public List<ExploitChain> ExploitChains { get; set; } = [];
    public List<PackageReachability> PackageReachability { get; set; } = [];
    public List<DangerousApiReachability> DangerousApiReachability { get; set; } = [];
    public List<WeaknessCandidate> WeaknessCandidates { get; set; } = [];
    public DataFlowPatternSet Patterns { get; set; } = new();
    public List<DataFlowMethodSummary> MethodSummaries { get; set; } = [];
    public DataFlowStatistics Statistics { get; set; } = new();
    public List<string> Diagnostics { get; set; } = [];
}

public static partial class DataFlowAnalyzer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string GetDataFlows(string path, string? patternsPath = null, string? patternPacks = null)
        => JsonSerializer.Serialize(Analyze(path, patternsPath, patternPacks), JsonOptions);

    /// <summary>
    /// Analyze <paramref name="path"/> and stream the result as JSON directly to <paramref name="outputFile"/>,
    /// returning the result so callers can reuse it (for graph export or printing) instead of round-tripping
    /// through the serialized string. Streaming keeps the JSON out of a single contiguous string, which bounds
    /// peak memory and avoids overflowing the string allocator on large trees.
    /// </summary>
    public static DataFlowResult WriteDataFlows(string path, string outputFile, string? patternsPath = null, string? patternPacks = null, string? suppressionsPath = null)
    {
        var result = Analyze(path, patternsPath, patternPacks, suppressionsPath);
        using var stream = new FileStream(outputFile, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 65536);
        JsonSerializer.Serialize(stream, result, JsonOptions);
        return result;
    }

    public static DataFlowResult Analyze(string path, string? patternsPath = null, string? patternPacks = null, string? suppressionsPath = null)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            throw new FileNotFoundException($"Path does not exist: {path}", path);
        }

        var patterns = LoadPatterns(patternsPath, patternPacks);
        var result = new DataFlowResult { Patterns = patterns, Metadata = TransparencyBuilder.CreateMetadata(path) };
        var purlResolver = PackageUrlResolver.Create(path);
        // S1: purl-resolution evidence (which lock/config file produced each purl, version
        // conflicts across sources) rides along with the analysis diagnostics.
        foreach (var diagnostic in purlResolver.Diagnostics.Where(diagnostic => !result.Diagnostics.Contains(diagnostic, StringComparer.Ordinal)))
        {
            result.Diagnostics.Add(diagnostic);
        }
        var sourcesToInspect = GetSourceFiles(path);
        result.Statistics.FilesAnalyzed = sourcesToInspect.Count;

        var references = GetMetadataReferences(path, result.Diagnostics);
        var csharpTrees = sourcesToInspect
            .Where(source => Path.GetExtension(source).Equals(Constants.CSharpSourceExtension, StringComparison.OrdinalIgnoreCase))
            .Select(source => SafeFileRead.TryReadAllText(source, out var content)
                ? (CSharpSyntaxTree)CSharpSyntaxTree.ParseText(content, path: source)
                : null)
            .OfType<CSharpSyntaxTree>()
            .ToList();
        var vbTrees = sourcesToInspect
            .Where(source => Path.GetExtension(source).Equals(Constants.VBSourceExtension, StringComparison.OrdinalIgnoreCase))
            .Select(source => SafeFileRead.TryReadAllText(source, out var content)
                ? (VisualBasicSyntaxTree)VisualBasicSyntaxTree.ParseText(content, path: source)
                : null)
            .OfType<VisualBasicSyntaxTree>()
            .ToList();

        var csharpCompilation = CSharpCompilation.Create(
            "Dosai.DataFlow.CSharp",
            syntaxTrees: csharpTrees,
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var vbCompilation = VisualBasicCompilation.Create(
            "Dosai.DataFlow.VisualBasic",
            syntaxTrees: vbTrees,
            references: references,
            options: new VisualBasicCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));

        // Framework providers reuse these compilations; building them again would double the parse cost.
        var frameworkContext = Frameworks.FrameworkContext.FromCompilations(path, csharpCompilation, vbCompilation, purlResolver);
        var frameworkResult = Frameworks.FrameworkRegistry.Analyze(frameworkContext);

        var summaries = new Dictionary<string, DataFlowMethodSummary>(StringComparer.Ordinal);
        var summaryCallerIndex = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var summaryMethodRoots = new Dictionary<string, (IOperation Root, SemanticModel Model)>(StringComparer.Ordinal);

        // T1: iterate summary collection to a fixpoint (capped at 3 rounds, mirroring the IL-mode
        // fixpoint in DataFlowAssembly). Round one is a full pass that also records the caller
        // index (callee summary key → enclosing methods) and each method's root operation; later
        // rounds are a worklist over callers of summarized methods only, so a wrapper chain
        // converges without re-walking every tree (and without re-fetching semantic models).
        // Lists only ever grow, so recursion terminates via the cap.
        foreach (var tree in csharpTrees)
        {
            var model = csharpCompilation.GetSemanticModel(tree);
            var root = tree.GetCompilationUnitRoot();
            CollectCompilationUnitSummaries(model, root, summaries, patterns, summaryCallerIndex, summaryMethodRoots);
        }

        foreach (var tree in vbTrees)
        {
            var model = vbCompilation.GetSemanticModel(tree);
            var root = tree.GetCompilationUnitRoot();
            CollectCompilationUnitSummaries(model, root, summaries, patterns, summaryCallerIndex, summaryMethodRoots);
        }

        // The worklist is seeded with every key round one produced, then narrowed each round to the
        // callers of the summaries that actually grew — so a converged wrapper chain is not
        // re-walked just because some unrelated summary exists.
        var changedSummaryKeys = new HashSet<string>(summaries.Keys, StringComparer.Ordinal);
        var cellCountsByKey = SummaryCellCounts(summaries);
        for (var round = 1; round < 3 && changedSummaryKeys.Count > 0; round++)
        {
            var callers = new HashSet<string>(StringComparer.Ordinal);
            foreach (var summaryKey in changedSummaryKeys)
            {
                if (summaryCallerIndex.TryGetValue(summaryKey, out var indexedCallers))
                {
                    callers.UnionWith(indexedCallers);
                }
            }

            callers.RemoveWhere(callerKey => !summaryMethodRoots.ContainsKey(callerKey));
            if (callers.Count == 0)
            {
                break;
            }

            foreach (var caller in callers)
            {
                var (root, model) = summaryMethodRoots[caller];
                new DataFlowSummaryCollector(model, summaries, patterns, summaryCallerIndex, summaryMethodRoots).Visit(root);
            }

            var cellCountsAfter = SummaryCellCounts(summaries);
            changedSummaryKeys = cellCountsAfter
                .Where(entry => entry.Value != cellCountsByKey.GetValueOrDefault(entry.Key))
                .Select(entry => entry.Key)
                .ToHashSet(StringComparer.Ordinal);
            cellCountsByKey = cellCountsAfter;
        }

        var graph = new DataFlowGraphBuilder(result, purlResolver, path);

        var frameworkSeeds = new FrameworkTaintSeedIndex(frameworkResult.TaintSeeds);
        foreach (var tree in csharpTrees)
        {
            var model = csharpCompilation.GetSemanticModel(tree);
            var root = tree.GetCompilationUnitRoot();
            AnalyzeCompilationUnit(model, root, graph, patterns, summaries, path, tree.FilePath, frameworkSeeds);
        }

        foreach (var tree in vbTrees)
        {
            var model = vbCompilation.GetSemanticModel(tree);
            var root = tree.GetCompilationUnitRoot();
            AnalyzeCompilationUnit(model, root, graph, patterns, summaries, path, tree.FilePath, frameworkSeeds);
        }

        AnalyzeLanguageFrontendDataFlows(path, sourcesToInspect, patterns, result);
        result.Statistics.FilesAnalyzed += AnalyzeAssemblyDataFlows(path, patterns, result, includeBuildArtifacts: sourcesToInspect.Count == 0);

        result.Nodes = result.Nodes.OrderBy(n => n.FileName, StringComparer.Ordinal).ThenBy(n => n.LineNumber).ThenBy(n => n.ColumnNumber).ThenBy(n => n.Id, StringComparer.Ordinal).ToList();
        result.Edges = result.Edges.OrderBy(e => e.FileName, StringComparer.Ordinal).ThenBy(e => e.LineNumber).ThenBy(e => e.ColumnNumber).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();
        result.Slices = result.Slices.OrderBy(s => s.Id, StringComparer.Ordinal).ToList();
        result.MethodSummaries = summaries.Values
            .Concat(result.MethodSummaries)
            .GroupBy(summary => $"{summary.Method}\u001f{summary.SummaryKind}\u001f{summary.EvidenceKind}", StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(summary => summary.Method, StringComparer.Ordinal)
            .ToList();
        result.Statistics.NodeCount = result.Nodes.Count;
        result.Statistics.EdgeCount = result.Edges.Count;
        result.Statistics.SourceCount = result.Nodes.Count(n => n.IsSource);
        result.Statistics.SinkCount = result.Nodes.Count(n => n.IsSink);
        result.Statistics.SliceCount = result.Slices.Count;
        // Framework entry points carry stable ids (ep:op:...); analyzer/Cli entry points get
        // sequential ids appended after them.
        var frameworkEntryPoints = frameworkResult.EntryPoints
            .Concat(TransparencyBuilder.BuildEntryPoints(frameworkResult.ApiEndpoints.Concat(ApiEndpointAnalyzer.GetApiEndpoints(path))));
        AddDataFlowEntryPoints(result);
        var next = frameworkEntryPoints.Count();
        foreach (var entryPoint in result.EntryPoints)
        {
            entryPoint.Id = $"ep{++next}";
        }

        result.EntryPoints.InsertRange(0, frameworkEntryPoints);
        result.PackageReachability = TransparencyBuilder.BuildPackageReachability(result);
        result.DangerousApiReachability = TransparencyBuilder.BuildDangerousApiReachability(result);
        result.WeaknessCandidates = TransparencyBuilder.BuildWeaknessCandidates(result, result.EntryPoints);
        // W5b: statically-detected catastrophic regex literals join the weakness queue as
        // CWE-1333 candidates so suppressions, diff, and downstream consumers see them.
        result.WeaknessCandidates.AddRange(ReDoSAnalyzer.Detect(csharpCompilation, result.Diagnostics));
        TransparencyBuilder.ApplySeverity(result);
        TransparencyBuilder.AttachExploitChains(result, graph.MethodEdges, graph.MethodIdsByFileMethod);
        TransparencyBuilder.ApplySuppressions(result, suppressionsPath);
        return result;
    }

    private static void AddDataFlowEntryPoints(DataFlowResult result)
    {
        var next = result.EntryPoints.Count;
        foreach (var source in result.Nodes.Where(node => node is { IsSource: true, Category: "cli", MethodName: "Main" }))
        {
            if (result.EntryPoints.Any(entryPoint => entryPoint.Kind == "Cli" && entryPoint.FileName == source.FileName && entryPoint.LineNumber == source.LineNumber))
            {
                continue;
            }

            result.EntryPoints.Add(new EntryPoint
            {
                Id = $"ep{++next}",
                Kind = "Cli",
                MethodName = source.MethodName,
                ClassName = source.ClassName,
                Namespace = source.Namespace,
                FileName = source.FileName,
                Path = source.Path,
                LineNumber = source.LineNumber,
                ColumnNumber = source.ColumnNumber,
                InputNames = [source.Name]
            });
        }
    }

    private static void CollectCompilationUnitSummaries(SemanticModel model, CSharpCompilationUnitSyntax root, Dictionary<string, DataFlowMethodSummary> summaries, DataFlowPatternSet patterns, Dictionary<string, HashSet<string>>? callerIndex = null, Dictionary<string, (IOperation Root, SemanticModel Model)>? methodRoots = null)
    {
        var operationNodes = root.DescendantNodes()
            .Where(node => node is Microsoft.CodeAnalysis.CSharp.Syntax.BaseMethodDeclarationSyntax or Microsoft.CodeAnalysis.CSharp.Syntax.AccessorDeclarationSyntax or Microsoft.CodeAnalysis.CSharp.Syntax.LocalFunctionStatementSyntax);
        foreach (var node in operationNodes)
        {
            var operation = model.GetOperation(node);
            if (operation is not null)
            {
                new DataFlowSummaryCollector(model, summaries, patterns, callerIndex, methodRoots).Visit(operation);
            }
        }
    }

    private static void CollectCompilationUnitSummaries(SemanticModel model, VisualBasicCompilationUnitSyntax root, Dictionary<string, DataFlowMethodSummary> summaries, DataFlowPatternSet patterns, Dictionary<string, HashSet<string>>? callerIndex = null, Dictionary<string, (IOperation Root, SemanticModel Model)>? methodRoots = null)
    {
        var operationNodes = root.DescendantNodes()
            .Where(node => node is Microsoft.CodeAnalysis.VisualBasic.Syntax.MethodBlockSyntax or Microsoft.CodeAnalysis.VisualBasic.Syntax.AccessorBlockSyntax);
        foreach (var node in operationNodes)
        {
            var operation = model.GetOperation(node);
            if (operation is not null)
            {
                new DataFlowSummaryCollector(model, summaries, patterns, callerIndex, methodRoots).Visit(operation);
            }
        }
    }

    private static void AnalyzeCompilationUnit(SemanticModel model, CSharpCompilationUnitSyntax root, DataFlowGraphBuilder graph, DataFlowPatternSet patterns, Dictionary<string, DataFlowMethodSummary> summaries, string basePath, string sourceFilePath, FrameworkTaintSeedIndex? frameworkSeeds = null)
    {
        var operationNodes = root.DescendantNodes()
            .Where(node => node is Microsoft.CodeAnalysis.CSharp.Syntax.BaseMethodDeclarationSyntax or Microsoft.CodeAnalysis.CSharp.Syntax.AccessorDeclarationSyntax or Microsoft.CodeAnalysis.CSharp.Syntax.LocalFunctionStatementSyntax);
        foreach (var node in operationNodes)
        {
            var operation = model.GetOperation(node);
            if (operation is not null)
            {
                new DataFlowOperationWalker(model, graph, patterns, summaries, basePath, sourceFilePath, frameworkSeeds).Visit(operation);
            }
        }
    }

    private static void AnalyzeCompilationUnit(SemanticModel model, VisualBasicCompilationUnitSyntax root, DataFlowGraphBuilder graph, DataFlowPatternSet patterns, Dictionary<string, DataFlowMethodSummary> summaries, string basePath, string sourceFilePath, FrameworkTaintSeedIndex? frameworkSeeds = null)
    {
        var operationNodes = root.DescendantNodes()
            .Where(node => node is Microsoft.CodeAnalysis.VisualBasic.Syntax.MethodBlockSyntax or Microsoft.CodeAnalysis.VisualBasic.Syntax.AccessorBlockSyntax);
        foreach (var node in operationNodes)
        {
            var operation = model.GetOperation(node);
            if (operation is not null)
            {
                new DataFlowOperationWalker(model, graph, patterns, summaries, basePath, sourceFilePath, frameworkSeeds).Visit(operation);
            }
        }
    }

    private static DataFlowPatternSet LoadPatterns(string? patternsPath, string? patternPacks)
    {
        var defaults = CreateDefaultPatterns();
        ApplyPatternPacks(defaults, patternPacks);
        if (string.IsNullOrWhiteSpace(patternsPath))
        {
            return defaults;
        }

        var json = File.ReadAllText(patternsPath);
        var userPatterns = JsonSerializer.Deserialize<DataFlowPatternSet>(json, JsonOptions) ?? new DataFlowPatternSet();
        defaults.Sources.AddRange(NormalizeTargets(userPatterns.Sources, DataFlowPatternTarget.Source));
        defaults.Sinks.AddRange(NormalizeTargets(userPatterns.Sinks, DataFlowPatternTarget.Sink));
        defaults.Passthroughs.AddRange(NormalizeTargets(userPatterns.Passthroughs, DataFlowPatternTarget.Passthrough));
        defaults.Sanitizers.AddRange(NormalizeTargets(userPatterns.Sanitizers, DataFlowPatternTarget.Sanitizer));
        return defaults;
    }

    /// <summary>
    ///     Single source of truth for the built-in pattern pack names. The CLI help text and the
    ///     docs drift test read this list so `--pattern-packs` help can never lag behind the
    ///     packs actually shipped by <see cref="ApplyPatternPacks"/>.
    /// </summary>
    public static readonly string[] DefaultPatternPackNames =
    [
        "aspnet", "data", "filesystem", "serialization", "cloud", "rpc", "auth", "crypto", "grpc", "messaging", "ai", "mcp",
        "xss", "xxe", "injection", "log", "redos", "template"
    ];

    private static void ApplyPatternPacks(DataFlowPatternSet patterns, string? patternPacks)
    {
        var requested = (patternPacks ?? "all")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(pack => pack.ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (requested.Count == 0 || requested.Contains("all"))
        {
            requested = DefaultPatternPackNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        patterns.PatternPacks = requested.OrderBy(pack => pack, StringComparer.OrdinalIgnoreCase).ToList();

        if (requested.Contains("aspnet"))
        {
            patterns.Sources.AddRange([
                new() { Target = DataFlowPatternTarget.Source, Kind = DataFlowPatternKind.Type, Pattern = "Microsoft.AspNetCore.Mvc.IActionResult", Match = DataFlowMatchKind.Contains, Category = "http", Description = "ASP.NET MVC action result context" },
                new() { Target = DataFlowPatternTarget.Source, Kind = DataFlowPatternKind.Attribute, Pattern = "FromBody", Match = DataFlowMatchKind.Contains, Category = "http", Description = "ASP.NET model-bound body input" },
                new() { Target = DataFlowPatternTarget.Source, Kind = DataFlowPatternKind.Attribute, Pattern = "FromQuery", Match = DataFlowMatchKind.Contains, Category = "http", Description = "ASP.NET query input" },
                new() { Target = DataFlowPatternTarget.Source, Kind = DataFlowPatternKind.Attribute, Pattern = "FromForm", Match = DataFlowMatchKind.Contains, Category = "http", Description = "ASP.NET form input" },
                new() { Target = DataFlowPatternTarget.Source, Kind = DataFlowPatternKind.Attribute, Pattern = "FromRoute", Match = DataFlowMatchKind.Contains, Category = "http", Description = "ASP.NET route input" }
            ]);
            patterns.Sinks.AddRange([
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "Microsoft.AspNetCore.Mvc.ControllerBase.Redirect", Match = DataFlowMatchKind.Contains, Category = "redirect", Description = "ASP.NET redirect" },
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "Microsoft.AspNetCore.Mvc.ControllerBase.LocalRedirect", Match = DataFlowMatchKind.Contains, Category = "redirect", Description = "ASP.NET local redirect" }
            ]);
        }

        if (requested.Contains("data"))
        {
            patterns.Sinks.AddRange([
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "Dapper.SqlMapper.Query", Match = DataFlowMatchKind.Contains, Category = "sql", Description = "Dapper raw SQL query" },
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "Dapper.SqlMapper.Execute", Match = DataFlowMatchKind.Contains, Category = "sql", Description = "Dapper raw SQL execution" },
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "Npgsql.NpgsqlCommand", Match = DataFlowMatchKind.Contains, Category = "sql", Description = "PostgreSQL command" }
            ]);
            patterns.Sanitizers.AddRange([
                new() { Target = DataFlowPatternTarget.Sanitizer, Kind = DataFlowPatternKind.Name, Pattern = "AddWithValue", Match = DataFlowMatchKind.Exact, Category = "sql-parameterization", Description = "Parameterized SQL binding" },
                // Method-anchored on purpose: a bare Name/Exact "Add" sanitizer matched List<T>.Add and
                // every other Add in the BCL, masking real SQL flows (W7). Only provider parameter
                // collections parameterize; anything else named Add must stop sanitizing.
                new() { Target = DataFlowPatternTarget.Sanitizer, Kind = DataFlowPatternKind.Method, Pattern = "SqlParameterCollection.Add", Match = DataFlowMatchKind.Contains, Category = "sql-parameterization", Description = "Parameterized SQL binding (System.Data/Microsoft.Data.SqlClient)" },
                new() { Target = DataFlowPatternTarget.Sanitizer, Kind = DataFlowPatternKind.Method, Pattern = "NpgsqlParameterCollection.Add", Match = DataFlowMatchKind.Contains, Category = "sql-parameterization", Description = "Parameterized SQL binding (Npgsql)" },
                new() { Target = DataFlowPatternTarget.Sanitizer, Kind = DataFlowPatternKind.Method, Pattern = "MySqlParameterCollection.Add", Match = DataFlowMatchKind.Contains, Category = "sql-parameterization", Description = "Parameterized SQL binding (MySQL)" },
                new() { Target = DataFlowPatternTarget.Sanitizer, Kind = DataFlowPatternKind.Method, Pattern = "SqliteParameterCollection.Add", Match = DataFlowMatchKind.Contains, Category = "sql-parameterization", Description = "Parameterized SQL binding (SQLite)" },
                new() { Target = DataFlowPatternTarget.Sanitizer, Kind = DataFlowPatternKind.Method, Pattern = "OracleParameterCollection.Add", Match = DataFlowMatchKind.Contains, Category = "sql-parameterization", Description = "Parameterized SQL binding (Oracle)" },
                new() { Target = DataFlowPatternTarget.Sanitizer, Kind = DataFlowPatternKind.Method, Pattern = "NpgsqlParameter.", Match = DataFlowMatchKind.Contains, Category = "sql-parameterization", Description = "Npgsql parameter construction" },
                new() { Target = DataFlowPatternTarget.Sanitizer, Kind = DataFlowPatternKind.Method, Pattern = "SqlParameter.", Match = DataFlowMatchKind.Contains, Category = "sql-parameterization", Description = "SqlParameter construction" }
            ]);
        }

        if (requested.Contains("filesystem"))
        {
            patterns.Sanitizers.AddRange([
                new() { Target = DataFlowPatternTarget.Sanitizer, Kind = DataFlowPatternKind.Method, Pattern = "System.IO.Path.GetFileName", Match = DataFlowMatchKind.Contains, Category = "path-validation", Description = "Path traversal limiting filename extraction" },
                new() { Target = DataFlowPatternTarget.Sanitizer, Kind = DataFlowPatternKind.Method, Pattern = "System.IO.Path.GetFullPath", Match = DataFlowMatchKind.Contains, Category = "path-normalization", Description = "Path normalization" }
            ]);
        }

        if (requested.Contains("serialization"))
        {
            patterns.Sinks.AddRange([
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "Newtonsoft.Json.JsonConvert.DeserializeObject", Match = DataFlowMatchKind.Contains, Category = "deserialization", Description = "JSON deserialization" },
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "System.Text.Json.JsonSerializer.Deserialize", Match = DataFlowMatchKind.Contains, Category = "deserialization", Description = "JSON deserialization" },
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "YamlDotNet.Serialization.IDeserializer.Deserialize", Match = DataFlowMatchKind.Contains, Category = "deserialization", Description = "YAML deserialization" }
            ]);
        }

        if (requested.Contains("cloud"))
        {
            patterns.Sources.AddRange([
                new() { Target = DataFlowPatternTarget.Source, Kind = DataFlowPatternKind.Attribute, Pattern = "QueueTrigger", Match = DataFlowMatchKind.Contains, Category = "serverless", Description = "Azure Queue trigger" },
                new() { Target = DataFlowPatternTarget.Source, Kind = DataFlowPatternKind.Attribute, Pattern = "ServiceBusTrigger", Match = DataFlowMatchKind.Contains, Category = "serverless", Description = "Azure Service Bus trigger" },
                new() { Target = DataFlowPatternTarget.Source, Kind = DataFlowPatternKind.Attribute, Pattern = "KafkaTrigger", Match = DataFlowMatchKind.Contains, Category = "serverless", Description = "Kafka trigger" }
            ]);
        }

        if (requested.Contains("rpc"))
        {
            patterns.Sources.AddRange([
                new() { Target = DataFlowPatternTarget.Source, Kind = DataFlowPatternKind.Namespace, Pattern = "Orleans", Match = DataFlowMatchKind.Prefix, Category = "rpc", Description = "Orleans grain request" },
                new() { Target = DataFlowPatternTarget.Source, Kind = DataFlowPatternKind.Namespace, Pattern = "Grpc", Match = DataFlowMatchKind.Prefix, Category = "rpc", Description = "gRPC request" }
            ]);
        }

        if (requested.Contains("auth"))
        {
            patterns.Sinks.AddRange([
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Name, Pattern = "CreateToken", Match = DataFlowMatchKind.Exact, Category = "auth", Description = "Token creation" },
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Name, Pattern = "SignInAsync", Match = DataFlowMatchKind.Exact, Category = "auth", Description = "Authentication sign-in" },
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Name, Pattern = "GeneratePasswordResetTokenAsync", Match = DataFlowMatchKind.Exact, Category = "auth", Description = "Password reset token generation" }
            ]);
        }

        if (requested.Contains("crypto"))
        {
            patterns.Sources.AddRange([
                new() { Target = DataFlowPatternTarget.Source, Kind = DataFlowPatternKind.Code, Pattern = "-----BEGIN", Match = DataFlowMatchKind.Contains, Category = "crypto-material", Description = "PEM encoded crypto material", TaintKinds = ["secret", "crypto-key"] },
                // W7, revisited: bare "key"/"keys" matched every KeyValuePair iteration and cache
                // dictionary in a codebase, minting most of the crypto slices on real apps — so the
                // bare words are gone. Only compound key identifiers (apiKey, client_secret,
                // session_key, hmacKey, …) mint crypto-material sources; [_.\-]? bridges the
                // separator inside compounds.
                new() { Target = DataFlowPatternTarget.Source, Kind = DataFlowPatternKind.Name, Pattern = @"(?i)(?:^|[_.\-\d])(?:api|public|private|session|auth|access|signing|sign|master|shared|account|license|encryption|crypto|cert|certificate|token|storage|product|hmac|client|server|symmetric|asymmetric|primary|secondary|rotation)[_.\-]?keys?(?:name|size|length|value|material|path|id|store|vault|ring|pair|stream|data|bytes)?(?:s)?$", Match = DataFlowMatchKind.Regex, Category = "crypto-material", Description = "Compound key identifier (apiKey, session_key, hmacKey, …)", TaintKinds = ["secret", "crypto-key"] },
                new() { Target = DataFlowPatternTarget.Source, Kind = DataFlowPatternKind.Name, Pattern = @"(?i)(?:^|[_.\-\d])secrets?(?:$|[_.\-\d])", Match = DataFlowMatchKind.Regex, Category = "secret", Description = "Secret-like value (word-boundary)", TaintKinds = ["secret"] },
                new() { Target = DataFlowPatternTarget.Source, Kind = DataFlowPatternKind.Name, Pattern = @"(?i)(?:^|[_.\-\d])(?:client|server|api|app|shared|access|auth|token|signing|encryption)[_.\-]?secrets?(?:name|path|id)?$", Match = DataFlowMatchKind.Regex, Category = "secret", Description = "Compound secret identifier (client_secret, …)", TaintKinds = ["secret"] },
                new() { Target = DataFlowPatternTarget.Source, Kind = DataFlowPatternKind.Name, Pattern = @"^(iv|IV|.*[a-z0-9]Iv|.*[_\.-](iv|IV))$", Match = DataFlowMatchKind.Regex, Category = "crypto-material", Description = "IV-like value", TaintKinds = ["crypto-iv", "crypto-nonce"] },
                new() { Target = DataFlowPatternTarget.Source, Kind = DataFlowPatternKind.Name, Pattern = @"^(nonce|Nonce|NONCE|.*[a-z0-9]Nonce|.*[_\.-](nonce|Nonce|NONCE))$", Match = DataFlowMatchKind.Regex, Category = "crypto-material", Description = "Nonce-like value", TaintKinds = ["crypto-nonce"] }
            ]);
            patterns.Sinks.AddRange([
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "System.Security.Cryptography", Match = DataFlowMatchKind.Contains, Category = "crypto", Description = "Cryptographic API", TaintKinds = ["crypto-key", "secret"] },
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "Microsoft.IdentityModel.Tokens", Match = DataFlowMatchKind.Contains, Category = "jwt", Description = "JWT signing/validation API", TaintKinds = ["jwt", "secret"] },
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "X509Certificate2", Match = DataFlowMatchKind.Contains, Category = "certificate", Description = "Certificate loading", TaintKinds = ["certificate", "secret"] },
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Code, Pattern = "ServerCertificateCustomValidationCallback", Match = DataFlowMatchKind.Contains, Category = "tls", Description = "TLS certificate validation callback", TaintKinds = ["certificate"] }
            ]);
            patterns.Sanitizers.AddRange([
                new() { Target = DataFlowPatternTarget.Sanitizer, Kind = DataFlowPatternKind.Method, Pattern = "System.Security.Cryptography.RandomNumberGenerator", Match = DataFlowMatchKind.Contains, Category = "secure-random", Description = "Cryptographically secure random source", RemovesTaintKinds = ["insecure-random"] }
            ]);
        }

        if (requested.Contains("grpc"))
        {
            patterns.Sources.AddRange([
                new() { Target = DataFlowPatternTarget.Source, Kind = DataFlowPatternKind.Type, Pattern = "Grpc.Core.ServerCallContext", Match = DataFlowMatchKind.Contains, Category = "rpc", Description = "gRPC server call context" }
            ]);
            patterns.Sinks.AddRange([
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "GrpcChannel.ForAddress", Match = DataFlowMatchKind.Contains, Category = "network", Description = "gRPC channel creation", TaintKinds = ["rpc"] },
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "Grpc.Net.Client.GrpcChannel", Match = DataFlowMatchKind.Contains, Category = "network", Description = "gRPC client channel", TaintKinds = ["rpc"] }
            ]);
        }

        if (requested.Contains("messaging"))
        {
            patterns.Sources.AddRange([
                new() { Target = DataFlowPatternTarget.Source, Kind = DataFlowPatternKind.Type, Pattern = "ConsumeContext", Match = DataFlowMatchKind.Contains, Category = "rpc", Description = "MassTransit consume context" }
            ]);
            patterns.Sinks.AddRange([
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "IPublishEndpoint.Publish", Match = DataFlowMatchKind.Contains, Category = "messaging", Description = "MassTransit publish", TaintKinds = ["rpc"] },
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "ISendEndpoint.Send", Match = DataFlowMatchKind.Contains, Category = "messaging", Description = "MassTransit send", TaintKinds = ["rpc"] },
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "DaprClient.InvokeMethodAsync", Match = DataFlowMatchKind.Contains, Category = "rpc", Description = "Dapr service invocation", TaintKinds = ["rpc"] }
            ]);
        }

        if (requested.Contains("ai"))
        {
            patterns.Sources.AddRange([
                // Qualified deliberately. A bare Contains on "ChatResponse"/"GetResponseAsync" matched
                // System.Net.WebRequest.GetResponseAsync() and any ChatResponseDto, reporting every
                // legacy WebRequest call site in a codebase as attacker-influenced LLM output — and the
                // symbol IS resolved here, so per the AGENTS.md heuristic policy the qualified form is
                // the correct one, matching the paired sink below.
                new() { Target = DataFlowPatternTarget.Source, Kind = DataFlowPatternKind.Type, Pattern = "Microsoft.Extensions.AI.ChatResponse", Match = DataFlowMatchKind.Contains, Category = "llm-output", Description = "LLM model output (treated as attacker-influenced)", TaintKinds = ["llm"] },
                new() { Target = DataFlowPatternTarget.Source, Kind = DataFlowPatternKind.Method, Pattern = "Microsoft.Extensions.AI.IChatClient.GetResponseAsync", Match = DataFlowMatchKind.Contains, Category = "llm-output", Description = "LLM response value", TaintKinds = ["llm"] },
                new() { Target = DataFlowPatternTarget.Source, Kind = DataFlowPatternKind.Method, Pattern = "Microsoft.SemanticKernel.Kernel.InvokePromptAsync", Match = DataFlowMatchKind.Contains, Category = "llm-output", Description = "LLM response value", TaintKinds = ["llm"] }
            ]);
            patterns.Sinks.AddRange([
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "Microsoft.Extensions.AI.IChatClient.GetResponseAsync", Match = DataFlowMatchKind.Contains, Category = "prompt", Description = "Prompt flowing into a model call (prompt injection)", TaintKinds = ["prompt-injection"] },
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "Microsoft.SemanticKernel.Kernel.InvokePromptAsync", Match = DataFlowMatchKind.Contains, Category = "prompt", Description = "Semantic Kernel prompt invocation", TaintKinds = ["prompt-injection"] },
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Type, Pattern = @"^(?:[\w.]+\.)?ChatMessage$", Match = DataFlowMatchKind.Regex, Category = "prompt", Description = "Chat message construction (exact type; DTO/ViewModel variants excluded)", TaintKinds = ["prompt-injection"] },
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "OpenAI.Chat.ChatClient.CompleteChatAsync", Match = DataFlowMatchKind.Contains, Category = "prompt", Description = "OpenAI chat completion", TaintKinds = ["prompt-injection"] }
            ]);
        }

        if (requested.Contains("mcp"))
        {
            patterns.Sources.AddRange([
                new() { Target = DataFlowPatternTarget.Source, Kind = DataFlowPatternKind.Attribute, Pattern = "McpServerTool", Match = DataFlowMatchKind.Contains, Category = "mcp", Description = "MCP server tool parameter (attacker-controlled)", TaintKinds = ["mcp"] },
                new() { Target = DataFlowPatternTarget.Source, Kind = DataFlowPatternKind.Attribute, Pattern = "McpServerPrompt", Match = DataFlowMatchKind.Contains, Category = "mcp", Description = "MCP server prompt argument", TaintKinds = ["mcp"] }
            ]);
            patterns.Sinks.AddRange([
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "McpClientFactory.CreateAsync", Match = DataFlowMatchKind.Contains, Category = "mcp-egress", Description = "Outbound MCP client connection", TaintKinds = ["rpc"] }
            ]);
        }

        if (requested.Contains("xss"))
        {
            patterns.Sinks.AddRange([
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "HtmlHelper.Raw", Match = DataFlowMatchKind.Contains, Category = "xss", Description = "Unencoded HTML output (IHtmlHelper.Raw/HtmlHelper.Raw)" },
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "System.Web.HttpResponse.Write", Match = DataFlowMatchKind.Contains, Category = "xss", Description = "Legacy HttpResponse.Write of unencoded markup" },
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "System.Web.HttpResponse.WriteLiteral", Match = DataFlowMatchKind.Contains, Category = "xss", Description = "Legacy HttpResponse.WriteLiteral" },
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "Microsoft.AspNetCore.Components.MarkupString", Match = DataFlowMatchKind.Contains, Category = "xss", Description = "Blazor MarkupString construction bypasses encoding" },
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Code, Pattern = "Html.Raw(", Match = DataFlowMatchKind.Contains, Category = "xss", Description = "Razor @Html.Raw fallback for syntax-only views", Confidence = "Low" },
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Code, Pattern = ".InnerHtml =", Match = DataFlowMatchKind.Contains, Category = "xss", Description = "WebForms/DOM InnerHtml assignment", Confidence = "Low" }
            ]);
            // HtmlEncoder.Encode / WebUtility.HtmlEncode sanitizers ship in the default set already.
        }

        if (requested.Contains("xxe"))
        {
            // Descriptions state the observed fact (a tainted document reaching an XML parser) and
            // never assert that the parser is unhardened: hardening is only *verifiable* in source
            // mode, where the guard markers below and the walker's hardened-symbol map suppress the
            // sink. In IL mode those guards are invisible, so the finding is confidence-downgraded
            // at slice construction (see DataFlowAssembly.GuardDependentCategories) rather than
            // claiming knowledge the analysis does not have.
            patterns.Sinks.AddRange([
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "System.Xml.XmlDocument.Load", Match = DataFlowMatchKind.Contains, Category = "xxe", Description = "Tainted document parsed by XmlDocument.Load/LoadXml (resolver hardening not verified)" },
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "System.Xml.XmlTextReader", Match = DataFlowMatchKind.Contains, Category = "xxe", Description = "Tainted document read by XmlTextReader (DTD handling not verified)" },
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "System.Xml.XmlReader.Create", Match = DataFlowMatchKind.Contains, Category = "xxe", Description = "Tainted document parsed by XmlReader.Create (settings hardening not verified)" },
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "System.Xml.Linq.XDocument.Load", Match = DataFlowMatchKind.Contains, Category = "xxe", Description = "Tainted document parsed by XDocument.Load (resolver hardening not verified)" },
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "System.Xml.XPath.XPathDocument", Match = DataFlowMatchKind.Contains, Category = "xxe", Description = "Tainted document parsed by XPathDocument (resolver hardening not verified)" }
            ]);
            // Hardening markers (spacing-tolerant regexes, not exact strings): a sink invocation
            // whose text or *arguments* carry one of these is treated as hardened and suppressed.
            // Cross-statement hardening — `var s = new XmlReaderSettings(); s.XmlResolver = null;`
            // — is tracked separately by the walker (hardened-symbol map), which covers the
            // idiomatic form the invocation text cannot show.
            patterns.Sanitizers.AddRange([
                new() { Target = DataFlowPatternTarget.Sanitizer, Kind = DataFlowPatternKind.Code, Pattern = @"XmlResolver\s*=\s*null", Match = DataFlowMatchKind.Regex, Category = "xxe-hardening", Description = "Null XML resolver disables external entity resolution" },
                new() { Target = DataFlowPatternTarget.Sanitizer, Kind = DataFlowPatternKind.Code, Pattern = @"DtdProcessing\s*=\s*(DtdProcessing\s*\.\s*)?(Prohibit|Ignore)", Match = DataFlowMatchKind.Regex, Category = "xxe-hardening", Description = "DtdProcessing.Prohibit/Ignore disallow or skip DTDs" },
                new() { Target = DataFlowPatternTarget.Sanitizer, Kind = DataFlowPatternKind.Code, Pattern = "XmlSecureResolver", Match = DataFlowMatchKind.Contains, Category = "xxe-hardening", Description = "XmlSecureResolver constrains entity resolution" }
            ]);
        }

        if (requested.Contains("injection"))
        {
            patterns.Sinks.AddRange([
                // LDAP (CWE-90)
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "System.DirectoryServices.DirectoryEntry", Match = DataFlowMatchKind.Contains, Category = "ldap", Description = "DirectoryEntry construction with attacker-influenced path/filter" },
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "System.DirectoryServices.DirectorySearcher", Match = DataFlowMatchKind.Contains, Category = "ldap", Description = "DirectorySearcher with tainted filter" },
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "System.DirectoryServices.Protocols.SearchRequest", Match = DataFlowMatchKind.Contains, Category = "ldap", Description = "LDAP SearchRequest with tainted filter" },
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "Novell.Directory.Ldap", Match = DataFlowMatchKind.Contains, Category = "ldap", Description = "Novell LDAP query with tainted filter" },
                // XPath (CWE-643)
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "XPathNavigator.Select", Match = DataFlowMatchKind.Contains, Category = "xpath", Description = "XPathNavigator.Select with tainted expression" },
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "XPathNavigator.Compile", Match = DataFlowMatchKind.Contains, Category = "xpath", Description = "XPathNavigator.Compile with tainted expression" },
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "XPathNavigator.Evaluate", Match = DataFlowMatchKind.Contains, Category = "xpath", Description = "XPathNavigator.Evaluate with tainted expression" },
                // XPath (CWE-643). XmlDocument.SelectNodes/SelectSingleNode are inherited from
                // XmlNode, so a type-qualified Method pattern never sees them — Name/Exact anchors
                // the resolved symbol instead.
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Name, Pattern = "SelectNodes", Match = DataFlowMatchKind.Exact, Category = "xpath", Description = "SelectNodes with tainted XPath" },
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Name, Pattern = "SelectSingleNode", Match = DataFlowMatchKind.Exact, Category = "xpath", Description = "SelectSingleNode with tainted XPath" },
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "XPathExpression.Compile", Match = DataFlowMatchKind.Contains, Category = "xpath", Description = "XPathExpression.Compile with tainted expression" },
                // NoSQL (CWE-943)
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "MongoDB.Bson.BsonDocument.Parse", Match = DataFlowMatchKind.Contains, Category = "nosql", Description = "BsonDocument.Parse with tainted JSON query" },
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "MongoDB.Driver.FilterDefinitionBuilder.Where", Match = DataFlowMatchKind.Contains, Category = "nosql", Description = "String-filter Where clause (expression form is parameterized)" },
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "MongoDB.Driver.IMongoDatabase.RunCommand", Match = DataFlowMatchKind.Contains, Category = "nosql", Description = "RunCommand with tainted command document" },
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Name, Pattern = "ScriptEvaluate", Match = DataFlowMatchKind.Exact, Category = "nosql", Description = "Redis Lua script evaluation (code injection adjacent)" },
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Name, Pattern = "LuaScript", Match = DataFlowMatchKind.Exact, Category = "nosql", Description = "Redis LuaScript preparation from tainted source" }
            ]);
        }

        if (requested.Contains("log"))
        {
            patterns.Sinks.AddRange([
                // Message bodies (CWE-117). Logging tainted values is usually intentional, so the
                // category defaults to Low severity; header values (CWE-113) stay Medium.
                // Real call sites resolve to the extension classes (LoggerExtensions.LogError,
                // DebugLoggerExtension, …), not the interface — anchor both shapes.
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "Microsoft.Extensions.Logging.LoggerExtensions", Match = DataFlowMatchKind.Contains, Category = "log", Description = "Logger extension output with tainted message/template", Severity = "Low", Confidence = "Low" },
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "Microsoft.Extensions.Logging.ILogger", Match = DataFlowMatchKind.Contains, Category = "log", Description = "Logger output with tainted message/template", Severity = "Low", Confidence = "Low" },
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "System.Console.WriteLine", Match = DataFlowMatchKind.Contains, Category = "log", Description = "Console output with tainted text", Severity = "Low", Confidence = "Low" },
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "System.Console.Write", Match = DataFlowMatchKind.Contains, Category = "log", Description = "Console output with tainted text", Severity = "Low", Confidence = "Low" },
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "Serilog", Match = DataFlowMatchKind.Contains, Category = "log", Description = "Serilog output with tainted message", Severity = "Low", Confidence = "Low" },
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "NLog.Logger", Match = DataFlowMatchKind.Contains, Category = "log", Description = "NLog output with tainted message", Severity = "Low", Confidence = "Low" },
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "NLog.LogManager", Match = DataFlowMatchKind.Contains, Category = "log", Description = "NLog static logger output", Severity = "Low", Confidence = "Low" },
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "log4net.ILog", Match = DataFlowMatchKind.Contains, Category = "log", Description = "log4net output with tainted message", Severity = "Low", Confidence = "Low" },
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "log4net.LogManager", Match = DataFlowMatchKind.Contains, Category = "log", Description = "log4net static logger output", Severity = "Low", Confidence = "Low" },
                // Response headers (CWE-113): real Append/Add calls are
                // Microsoft.AspNetCore.Http.HeaderDictionaryExtensions methods.
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "Microsoft.AspNetCore.Http.HeaderDictionaryExtensions", Match = DataFlowMatchKind.Contains, Category = "header", Description = "Response header extension with tainted value" },
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "IHeaderDictionary.Append", Match = DataFlowMatchKind.Contains, Category = "header", Description = "Response header append with tainted value" },
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "IHeaderDictionary.Add", Match = DataFlowMatchKind.Contains, Category = "header", Description = "Response header add with tainted value" },
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "System.Web.HttpResponse.AppendHeader", Match = DataFlowMatchKind.Contains, Category = "header", Description = "Legacy AppendHeader with tainted value" },
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Name, Pattern = "AppendHeader", Match = DataFlowMatchKind.Exact, Category = "header", Description = "Response header append with tainted value" }
            ]);
            patterns.Sanitizers.AddRange([
                new() { Target = DataFlowPatternTarget.Sanitizer, Kind = DataFlowPatternKind.Method, Pattern = "System.Text.Encodings.Web.UrlEncoder.Encode", Match = DataFlowMatchKind.Contains, Category = "header-encoding", Description = "URL encoding for header values" },
                new() { Target = DataFlowPatternTarget.Sanitizer, Kind = DataFlowPatternKind.Method, Pattern = "System.Net.WebUtility.UrlEncode", Match = DataFlowMatchKind.Contains, Category = "header-encoding", Description = "URL encoding for header values" },
                new() { Target = DataFlowPatternTarget.Sanitizer, Kind = DataFlowPatternKind.Code, Pattern = ".Replace(\"\\n\",", Match = DataFlowMatchKind.Contains, Category = "crlf-strip", Description = "Newline stripping for log/header injection" },
                new() { Target = DataFlowPatternTarget.Sanitizer, Kind = DataFlowPatternKind.Code, Pattern = ".Replace(\"\\r\",", Match = DataFlowMatchKind.Contains, Category = "crlf-strip", Description = "Carriage-return stripping for log/header injection" },
                new() { Target = DataFlowPatternTarget.Sanitizer, Kind = DataFlowPatternKind.Code, Pattern = ".Replace(Environment.NewLine,", Match = DataFlowMatchKind.Contains, Category = "crlf-strip", Description = "Newline stripping for log/header injection" }
            ]);
        }

        if (requested.Contains("redos"))
        {
            patterns.Sinks.AddRange([
                // Tainted *pattern* arguments. Tainted subjects validated against a fixed regex are the
                // correct pattern, so these default to Low severity; catastrophic *literal* patterns are
                // additionally flagged statically by the ReDoS literal heuristic.
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "System.Text.RegularExpressions.Regex..ctor", Match = DataFlowMatchKind.Contains, Category = "redos", Description = "Regex constructed from tainted pattern", Severity = "Medium" },
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "System.Text.RegularExpressions.Regex.IsMatch", Match = DataFlowMatchKind.Contains, Category = "redos", Description = "Regex.IsMatch with tainted pattern argument", Severity = "Low", Confidence = "Low" },
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "System.Text.RegularExpressions.Regex.Match", Match = DataFlowMatchKind.Contains, Category = "redos", Description = "Regex.Match with tainted pattern argument", Severity = "Low", Confidence = "Low" }
            ]);
        }

        if (requested.Contains("template"))
        {
            patterns.Sinks.AddRange([
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "Fluid.FluidTemplate", Match = DataFlowMatchKind.Contains, Category = "template", Description = "Fluid template parse/render of tainted source" },
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "Scriban.Template", Match = DataFlowMatchKind.Contains, Category = "template", Description = "Scriban template parse/render of tainted source" },
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "HandlebarsDotNet.Handlebars", Match = DataFlowMatchKind.Contains, Category = "template", Description = "Handlebars.NET compile of tainted template" },
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "RazorEngine", Match = DataFlowMatchKind.Contains, Category = "template", Description = "RazorEngine RunCompile with tainted template" },
                new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "DotLiquid.Template", Match = DataFlowMatchKind.Contains, Category = "template", Description = "DotLiquid template parse/render of tainted source" }
            ]);
        }
    }

    private static IEnumerable<DataFlowPattern> NormalizeTargets(IEnumerable<DataFlowPattern> patterns, DataFlowPatternTarget target)
    {
        foreach (var pattern in patterns)
        {
            pattern.Target = target;
            yield return pattern;
        }
    }

    private static DataFlowPatternSet CreateDefaultPatterns() => new()
    {
        Sources =
        [
            new() { Target = DataFlowPatternTarget.Source, Kind = DataFlowPatternKind.Parameter, Pattern = "Main", Match = DataFlowMatchKind.Exact, Category = "cli", Description = "Command-line Main arguments" },
            new() { Target = DataFlowPatternTarget.Source, Kind = DataFlowPatternKind.Parameter, Pattern = "request", Match = DataFlowMatchKind.Exact, Category = "message", Description = "Request/message handler input" },
            new() { Target = DataFlowPatternTarget.Source, Kind = DataFlowPatternKind.Parameter, Pattern = "command", Match = DataFlowMatchKind.Exact, Category = "message", Description = "Command handler input" },
            new() { Target = DataFlowPatternTarget.Source, Kind = DataFlowPatternKind.Parameter, Pattern = "query", Match = DataFlowMatchKind.Exact, Category = "message", Description = "Query handler input" },
            new() { Target = DataFlowPatternTarget.Source, Kind = DataFlowPatternKind.Parameter, Pattern = "model", Match = DataFlowMatchKind.Exact, Category = "http", Description = "MVC model-bound input" },
            new() { Target = DataFlowPatternTarget.Source, Kind = DataFlowPatternKind.Parameter, Pattern = "input", Match = DataFlowMatchKind.Exact, Category = "input", Description = "Generic input parameter" },
            new() { Target = DataFlowPatternTarget.Source, Kind = DataFlowPatternKind.Attribute, Pattern = "HttpGet", Match = DataFlowMatchKind.Prefix, Category = "http", Description = "ASP.NET HTTP endpoint parameter" },
            new() { Target = DataFlowPatternTarget.Source, Kind = DataFlowPatternKind.Attribute, Pattern = "HttpPost", Match = DataFlowMatchKind.Prefix, Category = "http", Description = "ASP.NET HTTP endpoint parameter" },
            new() { Target = DataFlowPatternTarget.Source, Kind = DataFlowPatternKind.Attribute, Pattern = "HttpPut", Match = DataFlowMatchKind.Prefix, Category = "http", Description = "ASP.NET HTTP endpoint parameter" },
            new() { Target = DataFlowPatternTarget.Source, Kind = DataFlowPatternKind.Attribute, Pattern = "HttpDelete", Match = DataFlowMatchKind.Prefix, Category = "http", Description = "ASP.NET HTTP endpoint parameter" },
            new() { Target = DataFlowPatternTarget.Source, Kind = DataFlowPatternKind.Attribute, Pattern = "HttpPatch", Match = DataFlowMatchKind.Prefix, Category = "http", Description = "ASP.NET HTTP endpoint parameter" },
            new() { Target = DataFlowPatternTarget.Source, Kind = DataFlowPatternKind.Attribute, Pattern = "Route", Match = DataFlowMatchKind.Prefix, Category = "http", Description = "ASP.NET route endpoint parameter" },
            new() { Target = DataFlowPatternTarget.Source, Kind = DataFlowPatternKind.Attribute, Pattern = "FunctionName", Match = DataFlowMatchKind.Contains, Category = "serverless", Description = "Azure Function entry point parameter" },
            new() { Target = DataFlowPatternTarget.Source, Kind = DataFlowPatternKind.Attribute, Pattern = "HttpTrigger", Match = DataFlowMatchKind.Contains, Category = "serverless", Description = "Azure Function HTTP trigger parameter" },
            new() { Target = DataFlowPatternTarget.Source, Kind = DataFlowPatternKind.Type, Pattern = "Microsoft.AspNetCore.Http.HttpRequest", Match = DataFlowMatchKind.Contains, Category = "http", Description = "ASP.NET HttpRequest" },
            new() { Target = DataFlowPatternTarget.Source, Kind = DataFlowPatternKind.Type, Pattern = "Microsoft.AspNetCore.Http.HttpContext", Match = DataFlowMatchKind.Contains, Category = "http", Description = "ASP.NET HttpContext" },
            new() { Target = DataFlowPatternTarget.Source, Kind = DataFlowPatternKind.Type, Pattern = "Microsoft.AspNetCore.Http.IFormFile", Match = DataFlowMatchKind.Contains, Category = "http", Description = "Uploaded file" },
            new() { Target = DataFlowPatternTarget.Source, Kind = DataFlowPatternKind.Method, Pattern = "System.Console.ReadLine", Match = DataFlowMatchKind.Contains, Category = "cli", Description = "Console input" },
            new() { Target = DataFlowPatternTarget.Source, Kind = DataFlowPatternKind.Symbol, Pattern = ".Request.Query", Match = DataFlowMatchKind.Contains, Category = "http", Description = "HTTP query string" },
            new() { Target = DataFlowPatternTarget.Source, Kind = DataFlowPatternKind.Symbol, Pattern = ".Request.Form", Match = DataFlowMatchKind.Contains, Category = "http", Description = "HTTP form data" },
            new() { Target = DataFlowPatternTarget.Source, Kind = DataFlowPatternKind.Symbol, Pattern = ".Request.Body", Match = DataFlowMatchKind.Contains, Category = "http", Description = "HTTP request body" },
            new() { Target = DataFlowPatternTarget.Source, Kind = DataFlowPatternKind.Symbol, Pattern = ".Request.Headers", Match = DataFlowMatchKind.Contains, Category = "http", Description = "HTTP headers" },
            new() { Target = DataFlowPatternTarget.Source, Kind = DataFlowPatternKind.Symbol, Pattern = ".Request.Cookies", Match = DataFlowMatchKind.Contains, Category = "http", Description = "HTTP cookies" },
            new() { Target = DataFlowPatternTarget.Source, Kind = DataFlowPatternKind.Code, Pattern = "Request[", Match = DataFlowMatchKind.Contains, Category = "http", Description = "ASP.NET request collection" },
            new() { Target = DataFlowPatternTarget.Source, Kind = DataFlowPatternKind.Code, Pattern = "Request.QueryString", Match = DataFlowMatchKind.Contains, Category = "http", Description = "ASP.NET query string" },
            new() { Target = DataFlowPatternTarget.Source, Kind = DataFlowPatternKind.Code, Pattern = ".Text", Match = DataFlowMatchKind.Contains, Category = "webforms", Description = "ASP.NET WebForms text control value" },
            new() { Target = DataFlowPatternTarget.Source, Kind = DataFlowPatternKind.Code, Pattern = ".SelectedItem.Value", Match = DataFlowMatchKind.Contains, Category = "webforms", Description = "ASP.NET WebForms selected value" },
            new() { Target = DataFlowPatternTarget.Source, Kind = DataFlowPatternKind.Type, Pattern = "Grpc.Core.ServerCallContext", Match = DataFlowMatchKind.Contains, Category = "rpc", Description = "gRPC server call context" }
        ],
        Sinks =
        [
            new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "System.Diagnostics.Process.Start", Match = DataFlowMatchKind.Contains, Category = "command", Description = "Process execution" },
            new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Type, Pattern = "System.Diagnostics.ProcessStartInfo", Match = DataFlowMatchKind.Contains, Category = "command", Description = "Process execution configuration" },
            new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "System.IO.File.", Match = DataFlowMatchKind.Contains, Category = "file", Description = "File system operation" },
            new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "System.IO.Directory.", Match = DataFlowMatchKind.Contains, Category = "file", Description = "Directory operation" },
            new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "System.IO.FileStream", Match = DataFlowMatchKind.Contains, Category = "file", Description = "File stream operation" },
            new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "System.IO.Path.Combine", Match = DataFlowMatchKind.Contains, Category = "file", Description = "Path construction" },
            new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Name, Pattern = "SaveAs", Match = DataFlowMatchKind.Exact, Category = "file", Description = "File upload save" },
            new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Name, Pattern = "CopyTo", Match = DataFlowMatchKind.Exact, Category = "file", Description = "Stream/file copy" },
            new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Code, Pattern = "SaveAs(", Match = DataFlowMatchKind.Contains, Category = "file", Description = "File upload save" },
            new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Code, Pattern = "CopyTo(", Match = DataFlowMatchKind.Contains, Category = "file", Description = "Stream/file copy" },
            new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Code, Pattern = "Server.MapPath", Match = DataFlowMatchKind.Contains, Category = "file", Description = "Server path mapping" },
            new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "System.Net.Http.HttpClient.", Match = DataFlowMatchKind.Contains, Category = "network", Description = "Outbound HTTP request" },
            new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "Response.Redirect", Match = DataFlowMatchKind.Contains, Category = "redirect", Description = "HTTP redirect" },
            new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Name, Pattern = "Redirect", Match = DataFlowMatchKind.Exact, Category = "redirect", Description = "HTTP redirect" },
            new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "GetGrain", Match = DataFlowMatchKind.Contains, Category = "rpc", Description = "Orleans grain dispatch" },
            new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Name, Pattern = "GetGrain", Match = DataFlowMatchKind.Exact, Category = "rpc", Description = "Orleans grain dispatch" },
            new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "System.Data.SqlClient.SqlCommand", Match = DataFlowMatchKind.Contains, Category = "sql", Description = "SQL command" },
            new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "Microsoft.Data.SqlClient.SqlCommand", Match = DataFlowMatchKind.Contains, Category = "sql", Description = "SQL command" },
            new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "MySqlCommand", Match = DataFlowMatchKind.Contains, Category = "sql", Description = "MySQL command" },
            new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "SqliteCommand", Match = DataFlowMatchKind.Contains, Category = "sql", Description = "SQLite command" },
            new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Name, Pattern = "ExecuteNonQuery", Match = DataFlowMatchKind.Exact, Category = "sql", Description = "SQL command execution" },
            new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Name, Pattern = "ExecuteReader", Match = DataFlowMatchKind.Exact, Category = "sql", Description = "SQL query execution" },
            new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "ExecuteSqlRaw", Match = DataFlowMatchKind.Contains, Category = "sql", Description = "Entity Framework raw SQL execution" },
            new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "FromSqlRaw", Match = DataFlowMatchKind.Contains, Category = "sql", Description = "Entity Framework raw SQL query" },
            new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "System.Reflection.Assembly.Load", Match = DataFlowMatchKind.Contains, Category = "reflection", Description = "Dynamic assembly loading" },
            new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "System.Type.GetType", Match = DataFlowMatchKind.Contains, Category = "reflection", Description = "Dynamic type lookup" },
            new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "BinaryFormatter.Deserialize", Match = DataFlowMatchKind.Contains, Category = "deserialization", Description = "BinaryFormatter deserialization" },
            new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Name, Pattern = "Deserialize", Match = DataFlowMatchKind.Exact, Category = "deserialization", Description = "Object deserialization" }
        ],
        Passthroughs =
        [
            new() { Target = DataFlowPatternTarget.Passthrough, Kind = DataFlowPatternKind.Method, Pattern = "System.String.Concat", Match = DataFlowMatchKind.Contains, Category = "string" },
            new() { Target = DataFlowPatternTarget.Passthrough, Kind = DataFlowPatternKind.Method, Pattern = "System.String.Format", Match = DataFlowMatchKind.Contains, Category = "string" },
            new() { Target = DataFlowPatternTarget.Passthrough, Kind = DataFlowPatternKind.Method, Pattern = "ToString", Match = DataFlowMatchKind.Contains, Category = "string" },
            new() { Target = DataFlowPatternTarget.Passthrough, Kind = DataFlowPatternKind.Method, Pattern = "Trim", Match = DataFlowMatchKind.Contains, Category = "string" },
            new() { Target = DataFlowPatternTarget.Passthrough, Kind = DataFlowPatternKind.Method, Pattern = "Replace", Match = DataFlowMatchKind.Contains, Category = "string" }
        ],
        Sanitizers =
        [
            new() { Target = DataFlowPatternTarget.Sanitizer, Kind = DataFlowPatternKind.Method, Pattern = "System.Text.Encodings.Web.HtmlEncoder.Encode", Match = DataFlowMatchKind.Contains, Category = "html-encoding", Description = "HTML encoding" },
            new() { Target = DataFlowPatternTarget.Sanitizer, Kind = DataFlowPatternKind.Method, Pattern = "System.Net.WebUtility.HtmlEncode", Match = DataFlowMatchKind.Contains, Category = "html-encoding", Description = "HTML encoding" },
            new() { Target = DataFlowPatternTarget.Sanitizer, Kind = DataFlowPatternKind.Method, Pattern = "System.Uri.EscapeDataString", Match = DataFlowMatchKind.Contains, Category = "url-encoding", Description = "URL component encoding" },
            new() { Target = DataFlowPatternTarget.Sanitizer, Kind = DataFlowPatternKind.Method, Pattern = "System.Text.RegularExpressions.Regex.IsMatch", Match = DataFlowMatchKind.Contains, Category = "validation", Description = "Regex validator used as a guard" },
            new() { Target = DataFlowPatternTarget.Sanitizer, Kind = DataFlowPatternKind.Name, Pattern = "IsMatch", Match = DataFlowMatchKind.Exact, Category = "validation", Description = "Validator method used as a guard or sanitizer" },
            new() { Target = DataFlowPatternTarget.Sanitizer, Kind = DataFlowPatternKind.Name, Pattern = "TryParse", Match = DataFlowMatchKind.Exact, Category = "validation", Description = "Parse validator" }
        ]
    };

    private static List<string> GetSourceFiles(string path)
    {
        var attributes = File.GetAttributes(path);
        if (!attributes.HasFlag(FileAttributes.Directory))
        {
            var extension = Path.GetExtension(path);
            return extension.Equals(Constants.CSharpSourceExtension, StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(Constants.VBSourceExtension, StringComparison.OrdinalIgnoreCase) ||
                   IsLanguageFrontendExtension(extension)
                ? [path]
                : [];
        }

        return new DirectoryInfo(path)
            .EnumerateFiles("*.*", SearchOption.AllDirectories)
            .Where(file => file.Extension.Equals(Constants.CSharpSourceExtension, StringComparison.OrdinalIgnoreCase) || file.Extension.Equals(Constants.VBSourceExtension, StringComparison.OrdinalIgnoreCase) || IsLanguageFrontendExtension(file.Extension))
            .Where(file => !file.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(file => !file.FullName.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(file => !file.Name.EndsWith($".g{file.Extension}", StringComparison.OrdinalIgnoreCase))
            .Select(file => file.FullName)
            .ToList();
    }

    private static bool IsLanguageFrontendExtension(string extension) => extension.Equals(Constants.FSharpSourceExtension, StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(Constants.FSharpSignatureExtension, StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(Constants.FSharpScriptExtension, StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(Constants.RSourceExtension, StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".rmd", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".qmd", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(Constants.CSourceExtension, StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(Constants.CppSourceExtension, StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".cc", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".cxx", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(Constants.CppHeaderExtension, StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".hpp", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".hh", StringComparison.OrdinalIgnoreCase);

    private static void AnalyzeLanguageFrontendDataFlows(string basePath, IEnumerable<string> files, DataFlowPatternSet patterns, DataFlowResult result)
    {
        var nodeCounter = result.Nodes.Count;
        var edgeCounter = result.Edges.Count;
        var sliceCounter = result.Slices.Count;
        foreach (var file in files.Where(file => IsLanguageFrontendExtension(Path.GetExtension(file))))
        {
            var language = DetectLanguageFrontend(file);
            var tainted = new Dictionary<string, DataFlowNode>(StringComparer.OrdinalIgnoreCase);
            var lines = SafeFileRead.TryReadAllLines(file);
            if (lines is null)
            {
                continue;
            }
            string? currentMethod = null;
            string? currentClass = Path.GetFileNameWithoutExtension(file);
            string? currentNamespace = language;
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index];
                UpdateLanguageContext(line, language, ref currentNamespace, ref currentClass, ref currentMethod);
                var sourceMatch = MatchLanguageSource(line, language, patterns);
                var assignedName = ExtractAssignedName(line, language);
                if (sourceMatch is not null)
                {
                    var sourceNode = CreateLanguageNode(result, ref nodeCounter, "Source", assignedName ?? sourceMatch.Pattern, true, false, sourceMatch, basePath, file, index + 1, Math.Max(1, line.IndexOf(sourceMatch.Pattern, StringComparison.OrdinalIgnoreCase) + 1), currentNamespace, currentClass, currentMethod, line);
                    sourceNode.Properties["language"] = language;
                    sourceNode.Properties["analysis"] = "language-frontend";
                    if (!string.IsNullOrWhiteSpace(assignedName)) tainted[assignedName] = sourceNode;
                }

                var sinkMatch = MatchLanguageSink(line, language, patterns);
                if (sinkMatch is null) continue;
                var taintedInputs = tainted.Where(kvp => line.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase)).Select(kvp => kvp.Value).ToList();
                if (taintedInputs.Count == 0 && sourceMatch is not null)
                {
                    taintedInputs.Add(result.Nodes.Last());
                }
                if (taintedInputs.Count == 0) continue;

                var sinkNode = CreateLanguageNode(result, ref nodeCounter, "Sink", sinkMatch.Pattern, false, true, sinkMatch, basePath, file, index + 1, Math.Max(1, line.IndexOf(sinkMatch.Pattern, StringComparison.OrdinalIgnoreCase) + 1), currentNamespace, currentClass, currentMethod, line);
                sinkNode.Properties["language"] = language;
                sinkNode.Properties["analysis"] = "language-frontend";
                foreach (var source in taintedInputs.DistinctBy(node => node.Id))
                {
                    var edge = new DataFlowEdge
                    {
                        Id = $"dfl{++edgeCounter}",
                        SourceId = source.Id,
                        TargetId = sinkNode.Id,
                        Kind = "LanguageFrontendFlow",
                        Label = assignedName,
                        SourcePurl = source.Purl,
                        TargetPurl = sinkNode.Purl,
                        Path = Directory.Exists(basePath) ? Path.GetRelativePath(basePath, file) : Path.GetFileName(file),
                        FileName = Path.GetFileName(file),
                        LineNumber = index + 1,
                        ColumnNumber = 1
                    };
                    result.Edges.Add(edge);
                    result.Slices.Add(new DataFlowSlice
                    {
                        Id = $"dfsl{++sliceCounter}",
                        SourceId = source.Id,
                        SinkId = sinkNode.Id,
                        NodeIds = [source.Id, sinkNode.Id],
                        EdgeIds = [edge.Id],
                        SourceCategory = source.Category,
                        SinkCategory = sinkNode.Category,
                        SinkArgument = line.Trim(),
                        SinkArgumentIndex = 0,
                        TaintKinds = sourceMatch?.TaintKinds.Concat(sinkMatch.TaintKinds).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? sinkMatch.TaintKinds,
                        Confidence = "Low",
                        Summary = $"{language} frontend data flow from {source.Name} to {sinkNode.Name}."
                    });
                }
            }
        }
    }

    private static DataFlowNode CreateLanguageNode(DataFlowResult result, ref int nodeCounter, string kind, string name, bool isSource, bool isSink, DataFlowPattern pattern, string basePath, string file, int line, int column, string? namespaceName, string? className, string? methodName, string code)
    {
        var node = new DataFlowNode
        {
            Id = $"dfln{++nodeCounter}",
            Kind = kind,
            Name = name,
            Path = Directory.Exists(basePath) ? Path.GetRelativePath(basePath, file) : Path.GetFileName(file),
            FileName = Path.GetFileName(file),
            Namespace = namespaceName,
            ClassName = className,
            MethodName = methodName,
            LineNumber = line,
            ColumnNumber = column,
            IsSource = isSource,
            IsSink = isSink,
            MatchedPatterns = [pattern.Pattern],
            Category = pattern.Category,
            Code = code.Trim().Length <= 240 ? code.Trim() : code.Trim()[..240] + "…",
            Evidence =
            [
                new AnalysisEvidence
                {
                    Kind = AnalysisEvidenceKind.LanguageFrontend,
                    Source = "language-frontend",
                    Description = "Data-flow node discovered by non-Roslyn language frontend.",
                    FileName = Path.GetFileName(file),
                    LineNumber = line,
                    ColumnNumber = column
                }
            ]
        };
        node.Properties["confidence"] = pattern.Confidence;
        if (pattern.TaintKinds.Count > 0) node.Properties["taintKinds"] = string.Join(",", pattern.TaintKinds);
        result.Nodes.Add(node);
        return node;
    }

    private static string DetectLanguageFrontend(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".fs" or ".fsi" or ".fsx" => "fsharp",
        ".r" or ".rmd" or ".qmd" => "r",
        _ => "vcpp"
    };

    private static void UpdateLanguageContext(string line, string language, ref string? namespaceName, ref string? className, ref string? methodName)
    {
        var trimmed = line.Trim();
        if (language == "fsharp")
        {
            var module = Regex.Match(trimmed, @"^(?:namespace|module)\s+([\w\.]+)");
            if (module.Success) namespaceName = module.Groups[1].Value;
            var type = Regex.Match(trimmed, @"^type\s+(\w+)");
            if (type.Success) className = type.Groups[1].Value;
            var fn = Regex.Match(trimmed, @"^(?:let|member)\s+(?:rec\s+)?(?:\w+\.)?(\w+)");
            if (fn.Success) methodName = fn.Groups[1].Value;
        }
        else if (language == "r")
        {
            var fn = Regex.Match(trimmed, @"^(\w+)\s*(?:<-|=)\s*function\s*\(");
            if (fn.Success) methodName = fn.Groups[1].Value;
        }
        else
        {
            var fn = Regex.Match(trimmed, @"(?:(\w+)::)?(\w+)\s*\([^;]*\)\s*(?:const\s*)?\{");
            if (fn.Success)
            {
                if (fn.Groups[1].Success) className = fn.Groups[1].Value;
                methodName = fn.Groups[2].Value;
            }
        }
    }

    private static string? ExtractAssignedName(string line, string language)
    {
        var match = language switch
        {
            "r" => Regex.Match(line, @"\b([A-Za-z_][\w.]*)\s*(?:<-|=)"),
            "fsharp" => Regex.Match(line, @"\blet\s+(?:mutable\s+)?([A-Za-z_][\w']*)\s*="),
            _ => Regex.Match(line, @"\b(?:auto|char\*|std::string|string|const\s+char\*)?\s*([A-Za-z_][\w]*)\s*=")
        };
        return match.Success ? match.Groups[1].Value : null;
    }

    private static DataFlowPattern? MatchLanguageSource(string line, string language, DataFlowPatternSet patterns)
    {
        var defaults = language switch
        {
            "r" => new[] { "input$", "req$", "commandArgs", "Sys.getenv", "fileInput" },
            "fsharp" => new[] { "Request.Query", "Request.Form", "Request.Body", "HttpContext", "argv", "Console.ReadLine" },
            _ => new[] { "argv", "getenv", "std::cin", "recv(", "ReadFile", "InternetReadFile" }
        };
        var patternMatch = patterns.Sources.FirstOrDefault(pattern => PatternMatches(line, pattern));
        if (patternMatch is not null) return patternMatch;
        var token = defaults.FirstOrDefault(candidate => line.Contains(candidate, StringComparison.OrdinalIgnoreCase));
        return token is null
            ? null
            : new DataFlowPattern { Target = DataFlowPatternTarget.Source, Kind = DataFlowPatternKind.Code, Pattern = token, Category = language == "r" ? "r-input" : language == "fsharp" ? "fsharp-input" : "native-input", Description = "Language frontend input source", TaintKinds = ["user-input"], Confidence = "Low" };
    }

    private static DataFlowPattern? MatchLanguageSink(string line, string language, DataFlowPatternSet patterns)
    {
        var defaults = language switch
        {
            "r" => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["system("] = "command", ["system2("] = "command", ["shell("] = "command", ["eval("] = "eval", ["parse("] = "eval", ["dbGetQuery"] = "sql", ["dbExecute"] = "sql", ["httr::GET"] = "network", ["download.file"] = "network" },
            "fsharp" => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Process.Start"] = "command", ["SqlCommand"] = "sql", ["File."] = "file", ["HttpClient"] = "network", ["Deserialize"] = "deserialization" },
            _ => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["system("] = "command", ["popen("] = "command", ["CreateProcess"] = "command", ["SQLExecDirect"] = "sql", ["strcpy"] = "memory", ["sprintf"] = "memory", ["EVP_DecryptInit"] = "crypto", ["SSL_CTX_set_verify"] = "tls" }
        };
        var patternMatch = patterns.Sinks.FirstOrDefault(pattern => PatternMatches(line, pattern));
        if (patternMatch is not null) return patternMatch;
        foreach (var (token, category) in defaults)
        {
            if (line.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return new DataFlowPattern { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Code, Pattern = token, Category = category, Description = "Language frontend sink", TaintKinds = [category], Confidence = "Low" };
            }
        }
        return null;
    }

    private static bool PatternMatches(string value, DataFlowPattern pattern)
    {
        return pattern.Match switch
        {
            DataFlowMatchKind.Exact => value.Equals(pattern.Pattern, StringComparison.OrdinalIgnoreCase),
            DataFlowMatchKind.Prefix => value.StartsWith(pattern.Pattern, StringComparison.OrdinalIgnoreCase),
            DataFlowMatchKind.Suffix => value.EndsWith(pattern.Pattern, StringComparison.OrdinalIgnoreCase),
            DataFlowMatchKind.Regex => Regex.IsMatch(value, pattern.Pattern, RegexOptions.CultureInvariant),
            _ => value.Contains(pattern.Pattern, StringComparison.OrdinalIgnoreCase)
        };
    }

    private static List<PortableExecutableReference> GetMetadataReferences(string path, List<string> diagnostics)
    {
        var references = new Dictionary<string, PortableExecutableReference>(StringComparer.OrdinalIgnoreCase);
        void AddReference(string referencePath)
        {
            if (!File.Exists(referencePath) || references.ContainsKey(referencePath))
            {
                return;
            }
            try
            {
                references.Add(referencePath, MetadataReference.CreateFromFile(referencePath));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException)
            {
                diagnostics.Add($"Could not add metadata reference {referencePath}: {ex.Message}");
            }
        }

#pragma warning disable IL3000
        AddReference(typeof(object).Assembly.Location);
#pragma warning restore IL3000
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string trustedPlatformAssemblies)
        {
            foreach (var referencePath in trustedPlatformAssemblies.Split(Path.PathSeparator))
            {
                AddReference(referencePath);
            }
        }

        var rootDirectory = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(rootDirectory) && Directory.Exists(rootDirectory))
        {
            foreach (var assemblyPath in Directory.EnumerateFiles(rootDirectory, "*.dll", SearchOption.AllDirectories).Where(IsManagedAssembly))
            {
                AddReference(assemblyPath);
            }
        }

        return references.Values.ToList();
    }

    private static bool IsManagedAssembly(string filePath)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var peReader = new PEReader(stream);
            return peReader is { HasMetadata: true, PEHeaders.CorHeader: not null };
        }
        catch
        {
            return false;
        }
    }

    // Machine-generated code can nest expressions thousands of levels deep; a recursive
    // Roslyn operation walk past that depth terminates the process with an unrecoverable
    // stack overflow. Descendants stop descending past MaxAnalysisDepth so analysis of
    // pathological members degrades gracefully instead of crashing the whole scan.
    internal abstract class DepthBoundedOperationWalker : OperationWalker
    {
        protected const int MaxAnalysisDepth = 200;

        private int _depth;

        protected bool MaxDepthReached => _depth >= MaxAnalysisDepth;

        protected void EnterDepth() => _depth++;

        protected void ExitDepth() => _depth--;

        public override void Visit(IOperation? operation)
        {
            if (MaxDepthReached)
            {
                return;
            }

            EnterDepth();
            try
            {
                base.Visit(operation);
            }
            finally
            {
                ExitDepth();
            }
        }
    }

    /// <summary>
    ///     Collects per-method data-flow summaries. Optionally records, for the T1 fixpoint, an
    ///     index of callee summary key → enclosing caller methods and each visited method's root
    ///     operation, so subsequent rounds only re-walk callers of summaries that changed.
    /// </summary>
    private sealed class DataFlowSummaryCollector(SemanticModel model, Dictionary<string, DataFlowMethodSummary> summaries, DataFlowPatternSet patterns, Dictionary<string, HashSet<string>>? callerIndex = null, Dictionary<string, (IOperation Root, SemanticModel Model)>? methodRoots = null) : DepthBoundedOperationWalker
    {
        private IMethodSymbol? _currentMethod;

        public override void VisitMethodBodyOperation(IMethodBodyOperation operation)
        {
            var previousMethod = _currentMethod;
            // The method-body operation's syntax is the whole declaration, whose span start sits
            // before the method name — GetEnclosingSymbol there returns the CONTAINING TYPE. Start
            // from the body/expression span instead, which is unambiguously inside the method.
            var innerSpanStart = operation.BlockBody?.Syntax.SpanStart ?? operation.ExpressionBody?.Syntax.SpanStart ?? operation.Syntax.SpanStart;
            _currentMethod = model.GetEnclosingSymbol(innerSpanStart) as IMethodSymbol
                             ?? model.GetEnclosingSymbol(operation.Syntax.FullSpan.End - 1) as IMethodSymbol;
            if (_currentMethod is not null && methodRoots is not null)
            {
                methodRoots.TryAdd(DescribeSymbol(_currentMethod), (operation, model));
            }
            base.VisitMethodBodyOperation(operation);
            _currentMethod = previousMethod;
        }

        public override void VisitBlock(IBlockOperation operation)
        {
            var previousMethod = _currentMethod;
            if (_currentMethod is null)
            {
                _currentMethod = model.GetEnclosingSymbol(operation.Syntax.SpanStart) as IMethodSymbol;
            }
            base.VisitBlock(operation);
            _currentMethod = previousMethod;
        }

        public override void VisitReturn(IReturnOperation operation)
        {
            if (_currentMethod is not null && operation.ReturnedValue is not null)
            {
                foreach (var parameterIndex in FindParameterIndexes(operation.ReturnedValue, _currentMethod))
                {
                    AddUnique(GetSummary(_currentMethod).ReturnParameterIndexes, parameterIndex);
                }
            }
            base.VisitReturn(operation);
        }

        public override void VisitInvocation(IInvocationOperation operation)
        {
            RecordSinkSummary(operation, operation.TargetMethod, operation.Arguments);
            PropagateCalleeSummary(operation, operation.TargetMethod, operation.Arguments);
            IndexCaller(operation.TargetMethod);
            base.VisitInvocation(operation);
        }

        /// <summary>T1: records that the enclosing method invokes <paramref name="targetMethod"/>, keyed by the summary keys PropagateCalleeSummary consults.</summary>
        private void IndexCaller(IMethodSymbol? targetMethod)
        {
            if (_currentMethod is null || targetMethod is null || callerIndex is null)
            {
                return;
            }

            var callerKey = DescribeSymbol(_currentMethod);
            if (callerIndex.TryGetValue(DescribeSymbol(targetMethod), out var callers))
            {
                callers.Add(callerKey);
            }
            else
            {
                callerIndex[DescribeSymbol(targetMethod)] = [callerKey];
            }

            var originalKey = DescribeSymbol(targetMethod.OriginalDefinition);
            if (originalKey != DescribeSymbol(targetMethod))
            {
                if (callerIndex.TryGetValue(originalKey, out var originalCallers))
                {
                    originalCallers.Add(callerKey);
                }
                else
                {
                    callerIndex[originalKey] = [callerKey];
                }
            }
        }

        public override void VisitObjectCreation(IObjectCreationOperation operation)
        {
            RecordSinkSummary(operation, operation.Constructor, operation.Arguments);
            base.VisitObjectCreation(operation);
        }

        /// <summary>
        ///     T1: absorb the callee's summary into the caller's summary. A wrapper calling another
        ///     wrapper that reaches a sink inherits the sink parameter indexes; a wrapper returning the
        ///     callee's tainted return inherits the return parameter indexes. Combined with the fixpoint
        ///     loop this attributes multi-hop chains to the outermost method. Callees without summaries
        ///     (recursion, metadata-only) simply contribute nothing, so cycles converge via the cap.
        /// </summary>
        private void PropagateCalleeSummary(IInvocationOperation operation, IMethodSymbol? targetMethod, IEnumerable<IArgumentOperation> arguments)
        {
            if (_currentMethod is null || targetMethod is null)
            {
                return;
            }

            if (!summaries.TryGetValue(DescribeSymbol(targetMethod), out var calleeSummary) &&
                !summaries.TryGetValue(DescribeSymbol(targetMethod.OriginalDefinition), out calleeSummary))
            {
                return;
            }

            var argumentList = arguments.ToList();
            var callerSummary = GetSummary(_currentMethod);
            foreach (var (parameterIndex, argument) in argumentList.Select((argument, index) => (index, argument)))
            {
                var indexes = FindParameterIndexes(argument.Value, _currentMethod).ToList();
                if (indexes.Count == 0)
                {
                    continue;
                }

                if (calleeSummary.SinkParameterIndexes.Contains(parameterIndex))
                {
                    foreach (var callerIndex in indexes)
                    {
                        AddUnique(callerSummary.SinkParameterIndexes, callerIndex);
                    }

                    foreach (var category in calleeSummary.SinkCategories.Where(category => !string.IsNullOrWhiteSpace(category)))
                    {
                        if (!callerSummary.SinkCategories.Contains(category!, StringComparer.Ordinal))
                        {
                            callerSummary.SinkCategories.Add(category!);
                        }
                    }
                }

                if (calleeSummary.ReturnParameterIndexes.Contains(parameterIndex))
                {
                    foreach (var callerIndex in indexes)
                    {
                        AddUnique(callerSummary.ReturnParameterIndexes, callerIndex);
                    }
                }
            }

            foreach (var taintKind in calleeSummary.TaintKinds.Where(taintKind => !callerSummary.TaintKinds.Contains(taintKind, StringComparer.Ordinal)))
            {
                callerSummary.TaintKinds.Add(taintKind);
            }
        }

        private void RecordSinkSummary(IOperation operation, IMethodSymbol? targetMethod, IEnumerable<IArgumentOperation> arguments)
        {
            if (_currentMethod is null || targetMethod is null)
            {
                return;
            }

            var sinkPatterns = MatchSymbol(targetMethod, operation.Syntax, patterns.Sinks).Concat(MatchCode(SafeSyntaxText.Text(operation.Syntax), patterns.Sinks)).ToList();
            if (sinkPatterns.Count == 0)
            {
                return;
            }

            var summary = GetSummary(_currentMethod);
            foreach (var argument in arguments)
            {
                foreach (var parameterIndex in FindParameterIndexes(argument.Value, _currentMethod))
                {
                    AddUnique(summary.SinkParameterIndexes, parameterIndex);
                    foreach (var category in sinkPatterns.Select(pattern => pattern.Category).Where(category => !string.IsNullOrWhiteSpace(category)).Distinct(StringComparer.Ordinal))
                    {
                        if (!summary.SinkCategories.Contains(category!, StringComparer.Ordinal))
                        {
                            summary.SinkCategories.Add(category!);
                        }
                    }

                    // T13: surface the taint kinds the sink patterns stamp so multi-hop flows keep
                    // their category payload (e.g. secret/crypto-key surviving wrapper chains);
                    // kinds default to the sink category like the walker's seed logic.
                    foreach (var taintKind in sinkPatterns
                                 .SelectMany(pattern => pattern.TaintKinds.Count > 0 ? pattern.TaintKinds : [pattern.Category ?? "user-input"])
                                 .Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        if (!summary.TaintKinds.Contains(taintKind, StringComparer.Ordinal))
                        {
                            summary.TaintKinds.Add(taintKind);
                        }
                    }
                }
            }
        }

        private DataFlowMethodSummary GetSummary(IMethodSymbol method)
        {
            var key = DescribeSymbol(method);
            if (!summaries.TryGetValue(key, out var summary))
            {
                summary = new DataFlowMethodSummary
                {
                    Method = key,
                    EvidenceKind = AnalysisEvidenceKind.SourceRoslynSummary,
                    Identity = MethodIdentityFactory.FromParts(key, key, null, key, method.ContainingAssembly?.ToDisplayString(), method.ContainingModule?.ToDisplayString(), method.ContainingNamespace?.ToDisplayString(), method.ContainingType?.Name, method.Name, 0, null, AnalysisEvidenceKind.SourceRoslynSummary),
                    Evidence =
                    [
                        new AnalysisEvidence
                        {
                            Kind = AnalysisEvidenceKind.SourceRoslynSummary,
                            Source = "roslyn-source",
                            Description = "Method summary inferred from Roslyn operation analysis."
                        }
                    ]
                };
                summaries[key] = summary;
            }
            return summary;
        }

        private static IEnumerable<int> FindParameterIndexes(IOperation operation, IMethodSymbol method)
        {
            var indexes = new List<int>();
            var pending = new Stack<IOperation>();
            pending.Push(operation);
            while (pending.Count > 0)
            {
                var current = Strip(pending.Pop());
                if (current is IParameterReferenceOperation parameterReference)
                {
                    var index = method.Parameters.IndexOf(parameterReference.Parameter);
                    if (index >= 0)
                    {
                        indexes.Add(index);
                    }
                }

                var children = current.ChildOperations.ToList();
                for (var i = children.Count - 1; i >= 0; i--)
                {
                    pending.Push(children[i]);
                }
            }

            return indexes;
        }

        private static void AddUnique(List<int> values, int value)
        {
            if (!values.Contains(value))
            {
                values.Add(value);
                values.Sort();
            }
        }

        private static IEnumerable<DataFlowPattern> MatchSymbol(ISymbol symbol, SyntaxNode syntax, IEnumerable<DataFlowPattern> candidatePatterns)
        {
            var normalizedSymbol = DescribeSymbol(symbol);
            var name = symbol.Name;
            var containingType = Normalize((symbol.ContainingType ?? symbol as INamedTypeSymbol)?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? string.Empty);
            var namespaceName = symbol.ContainingNamespace?.ToDisplayString() ?? string.Empty;
            var code = syntax.ToString();

            foreach (var pattern in candidatePatterns)
            {
                var value = pattern.Kind switch
                {
                    DataFlowPatternKind.Method => normalizedSymbol,
                    DataFlowPatternKind.Symbol => normalizedSymbol,
                    DataFlowPatternKind.Type => containingType,
                    DataFlowPatternKind.Namespace => namespaceName,
                    DataFlowPatternKind.Name => name,
                    DataFlowPatternKind.Code => code,
                    DataFlowPatternKind.Attribute => string.Join(' ', symbol.GetAttributes().Select(a => a.AttributeClass?.Name ?? string.Empty)),
                    _ => normalizedSymbol
                };

                if (PatternMatches(value, pattern))
                {
                    yield return pattern;
                }
            }
        }

        private static IEnumerable<DataFlowPattern> MatchCode(string code, IEnumerable<DataFlowPattern> candidatePatterns)
        {
            foreach (var pattern in candidatePatterns)
            {
                var canMatchCode = pattern.Kind is DataFlowPatternKind.Code or DataFlowPatternKind.Method or DataFlowPatternKind.Symbol or DataFlowPatternKind.Name;
                if (canMatchCode && PatternMatches(code, pattern))
                {
                    yield return pattern;
                }
            }
        }

        private static bool PatternMatches(string value, DataFlowPattern pattern)
        {
            return pattern.Match switch
            {
                DataFlowMatchKind.Exact => value.Equals(pattern.Pattern, StringComparison.OrdinalIgnoreCase),
                DataFlowMatchKind.Prefix => value.StartsWith(pattern.Pattern, StringComparison.OrdinalIgnoreCase),
                DataFlowMatchKind.Suffix => value.EndsWith(pattern.Pattern, StringComparison.OrdinalIgnoreCase),
                DataFlowMatchKind.Regex => Regex.IsMatch(value, pattern.Pattern, RegexOptions.CultureInvariant),
                _ => value.Contains(pattern.Pattern, StringComparison.OrdinalIgnoreCase)
            };
        }

        private static IOperation Strip(IOperation operation)
        {
            while (operation is IConversionOperation conversion)
            {
                operation = conversion.Operand;
            }
            return operation;
        }

        private static string DescribeSymbol(ISymbol symbol)
        {
            if (symbol is IMethodSymbol methodSymbol)
            {
                var containingType = Normalize(methodSymbol.ContainingType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? string.Empty);
                var methodName = methodSymbol.MethodKind == MethodKind.Constructor ? ".ctor" : methodSymbol.Name;
                var parameters = string.Join(",", methodSymbol.Parameters.Select(p => Normalize(p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))));
                return $"{containingType}.{methodName}({parameters})";
            }

            return Normalize(symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
        }
    }

    private sealed class DataFlowOperationWalker(SemanticModel model, DataFlowGraphBuilder graph, DataFlowPatternSet patterns, Dictionary<string, DataFlowMethodSummary> summaries, string basePath, string sourceFilePath, FrameworkTaintSeedIndex? frameworkSeeds = null) : DepthBoundedOperationWalker
    {
        private readonly Dictionary<string, TaintTrace> _taintedSymbols = new(StringComparer.Ordinal);
        private readonly DataFlowPatternIndex _patternIndex = new(patterns);
        private readonly FrameworkTaintSeedIndex? _frameworkSeeds = frameworkSeeds;
        private readonly Dictionary<SyntaxNode, string> _syntaxTextCache = new();
        private readonly Dictionary<SyntaxNode, TaintTrace> _awaitTaintBySyntax = new();
        private readonly HashSet<string> _suppressedGuardKeys = new(StringComparer.Ordinal);
        // W7: symbol keys of XmlReaderSettings/XmlDocument instances proven hardened in an earlier
        // statement (XmlResolver = null / DtdProcessing.Prohibit|Ignore) — the idiomatic form the
        // sink-invocation text cannot show.
        private readonly HashSet<string> _hardenedSymbols = new(StringComparer.Ordinal);
        private bool _depthBudgetReported;
        private IMethodSymbol? _currentMethod;

        /// <summary>Spacing-tolerant hardening marker, shared by the declarator and assignment hooks.</summary>
        private static readonly Regex HardeningMarkerRegex = new(@"XmlResolver\s*=\s*null|DtdProcessing\s*=\s*(DtdProcessing\s*\.\s*)?(Prohibit|Ignore)", RegexOptions.Compiled);

        public override void Visit(IOperation? operation)
        {
            // T11: silent truncation understates results; report the budget once per walker with the
            // enclosing method so users can tell analysis depth from a finding gap.
            if (MaxDepthReached && !_depthBudgetReported)
            {
                _depthBudgetReported = true;
                graph.RecordDiagnostic($"Data-flow walker hit the {MaxAnalysisDepth}-level nesting budget near {DescribeMethodContext(_currentMethod)}; deeper operations in that member were skipped.");
            }

            base.Visit(operation);
        }

        private static string DescribeMethodContext(IMethodSymbol? method) => method is null
            ? "an unattributed member"
            : $"{method.ContainingType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? "<unknown>"}.{method.Name}";

        public override void VisitMethodBodyOperation(IMethodBodyOperation operation)
        {
            var previousMethod = _currentMethod;
            _currentMethod = model.GetEnclosingSymbol(operation.Syntax.SpanStart) as IMethodSymbol;
            if (_currentMethod is not null)
            {
                SeedMethodParameters(_currentMethod, operation.Syntax);
            }
            base.VisitMethodBodyOperation(operation);
            _currentMethod = previousMethod;
        }

        public override void VisitBlock(IBlockOperation operation)
        {
            if (_currentMethod is null && model.GetEnclosingSymbol(operation.Syntax.SpanStart) is IMethodSymbol methodSymbol)
            {
                _currentMethod = methodSymbol;
                SeedMethodParameters(methodSymbol, operation.Syntax);
            }
            base.VisitBlock(operation);
        }

        public override void VisitVariableDeclarator(IVariableDeclaratorOperation operation)
        {
            if (operation.GetVariableInitializer()?.Value is { } initializer)
            {
                AssignSymbol(operation.Symbol, initializer, operation.Syntax, "VariableAssignment");
                MarkHardenedDeclarator(operation.Symbol, initializer);
            }
            base.VisitVariableDeclarator(operation);
        }

        /// <summary>
        ///     W2: `var settings = new XmlReaderSettings { XmlResolver = null };` — the hardening
        ///     marker sits inside the initializer of an Xml-typed settings object, so later sinks
        ///     that take the variable are suppressed.
        /// </summary>
        private void MarkHardenedDeclarator(ISymbol symbol, IOperation initializer)
        {
            var typeName = GetSymbolType(symbol);
            if (!typeName.Contains("XmlReaderSettings", StringComparison.Ordinal) &&
                !typeName.Contains("XmlDocument", StringComparison.Ordinal) &&
                !typeName.Contains("XmlTextReader", StringComparison.Ordinal) &&
                !typeName.Contains("XmlUrlResolver", StringComparison.Ordinal))
            {
                return;
            }

            if (HardeningMarkerRegex.IsMatch(SyntaxText(initializer.Syntax)))
            {
                _hardenedSymbols.Add(SymbolKey(symbol));
            }
        }

        public override void VisitSimpleAssignment(ISimpleAssignmentOperation operation)
        {
            AssignTarget(operation.Target, operation.Value, operation.Syntax, "Assignment");
            base.VisitSimpleAssignment(operation);
        }

        public override void VisitCompoundAssignment(ICompoundAssignmentOperation operation)
        {
            AssignTarget(operation.Target, operation.Value, operation.Syntax, "CompoundAssignment");
            base.VisitCompoundAssignment(operation);
        }

        public override void VisitConditional(IConditionalOperation operation)
        {
            var trueGuardedKeys = GetSanitizedGuardKeys(operation.Condition, whenConditionIsTrue: true).ToList();
            var falseGuardedKeys = GetSanitizedGuardKeys(operation.Condition, whenConditionIsTrue: false).ToList();
            Visit(operation.Condition);
            if (trueGuardedKeys.Count == 0)
            {
                Visit(operation.WhenTrue);
            }
            else
            {
                VisitWithSuppressedTaint(trueGuardedKeys, operation.Condition, operation.WhenTrue);
            }

            if (falseGuardedKeys.Count == 0 || operation.WhenFalse is null)
            {
                Visit(operation.WhenFalse);
            }
            else
            {
                VisitWithSuppressedTaint(falseGuardedKeys, operation.Condition, operation.WhenFalse);
            }
        }

        private void VisitWithSuppressedTaint(IEnumerable<string> keys, IOperation guardCondition, IOperation operation)
        {
            var saved = new Dictionary<string, TaintTrace?>(StringComparer.Ordinal);
            foreach (var key in keys.Distinct(StringComparer.Ordinal))
            {
                saved[key] = _taintedSymbols.TryGetValue(key, out var trace) ? trace : null;
                _taintedSymbols.Remove(key);
                // T4/a: the guard also suppresses pattern-minted sources for these keys — otherwise
                // a parameter like `input` re-materializes as a fresh source inside the guarded
                // branch and the validation guard silently stops guarding.
                _suppressedGuardKeys.Add(key);
            }

            try
            {
                // T4: a guard that suppresses a live taint is negative evidence — record which flow the
                // validator killed, where, and why the branch below reports nothing.
                foreach (var trace in saved.Values.Where(trace => trace is not null).Cast<TaintTrace>().Take(8))
                {
                    graph.RecordSanitizedGuard(trace, guardCondition.Syntax, sourceFilePath);
                }

                Visit(operation);
            }
            finally
            {
                foreach (var (key, trace) in saved)
                {
                    _suppressedGuardKeys.Remove(key);
                    if (trace is null)
                    {
                        _taintedSymbols.Remove(key);
                    }
                    else
                    {
                        _taintedSymbols[key] = trace;
                    }
                }
            }
        }

        public override void VisitInvocation(IInvocationOperation operation)
        {
            RecordMethodEdge(operation.TargetMethod);
            SeedLambdaArguments(operation);
            PropagateCollectionMutation(operation);
            ProcessSink(operation, operation.TargetMethod, operation.Arguments);
            ProcessCodeSink(operation, operation.Arguments);
            base.VisitInvocation(operation);
        }

        public override void VisitObjectCreation(IObjectCreationOperation operation)
        {
            RecordMethodEdge(operation.Constructor);
            ProcessSink(operation, operation.Constructor, operation.Arguments);
            ProcessCodeSink(operation, operation.Arguments);
            base.VisitObjectCreation(operation);
        }

        /// <summary>
        ///     T3: awaiting a tainted <c>Task&lt;T&gt;</c>/<c>ValueTask&lt;T&gt;</c> yields the taint of T. Child
        ///     propagation already carries the operand's taint; the explicit Await node keeps the
        ///     asynchronous boundary visible in slices (aligned with the IL mode's state-machine
        ///     reconstruction, which preseeds MoveNext fields).
        /// </summary>
        public override void VisitAwait(IAwaitOperation operation)
        {
            if (GetTaint(operation.Operation) is { } taint)
            {
                var awaitNode = graph.AddNode("Await", "await", operation, model, basePath, sourceFilePath, _currentMethod, isSource: false, isSink: false, matchedPatterns: [], category: null);
                graph.AddEdges(taint.NodeIds, awaitNode.Id, "Await", operation.Syntax, sourceFilePath, "await");
                _awaitTaintBySyntax[operation.Syntax] = taint.Append(awaitNode.Id);
            }

            base.VisitAwait(operation);
        }

        /// <summary>
        ///     Records a caller→callee edge between method signatures (R2). Keyed with the same
        ///     GenerateMethodSignature format as entry-point MethodIds, so exploit-chain resolution
        ///     can walk concrete graph paths instead of file/line heuristics.
        /// </summary>
        private void RecordMethodEdge(IMethodSymbol? targetMethod)
        {
            if (_currentMethod is null || targetMethod is null)
            {
                return;
            }

            var caller = Dosai.FormatMethodSignature(_currentMethod);
            var callee = Dosai.FormatMethodSignature(targetMethod);
            if (string.IsNullOrWhiteSpace(caller) || string.IsNullOrWhiteSpace(callee) || string.Equals(caller, callee, StringComparison.Ordinal))
            {
                return;
            }

            graph.AddMethodEdge(caller, callee);
            graph.RecordMethodLocation(Path.GetFileName(sourceFilePath), targetMethod.Name, callee);
        }

        /// <summary>
        ///     T2: seed lambda parameters when the lambda is passed to a call whose receiver or other
        ///     arguments are tainted — `tainted.ForEach(x => Sink(x))`, `tainted.Select(Transform)`.
        ///     Parameters are seeded directly into the taint map (not via pattern matching), so the
        ///     phantom-source guard for lambda parameters in MatchOperationSource still holds.
        /// </summary>
        private void SeedLambdaArguments(IInvocationOperation operation)
        {
            var lambdas = operation.Arguments
                .Select(argument => UnwrapLambda(argument.Value))
                .OfType<IAnonymousFunctionOperation>()
                .ToList();
            if (lambdas.Count == 0)
            {
                return;
            }

            var combined = Combine(new[] { GetTaint(operation.Instance) }
                .Concat(operation.Arguments.Where(argument => UnwrapLambda(argument.Value) is not IAnonymousFunctionOperation).Select(argument => GetTaint(argument.Value))));
            if (combined is null)
            {
                return;
            }

            foreach (var lambda in lambdas)
            {
                foreach (var parameter in lambda.Symbol.Parameters)
                {
                    _taintedSymbols[SymbolKey(parameter)] = combined;
                }
            }
        }

        /// <summary>
        ///     Lambdas reach call arguments wrapped in conversions and delegate creations; unwrap all
        ///     of them to find the anonymous function (Strip only handles conversions).
        /// </summary>
        private static IOperation UnwrapLambda(IOperation operation)
        {
            while (true)
            {
                switch (operation)
                {
                    case IConversionOperation conversion:
                        operation = conversion.Operand;
                        continue;
                    case IDelegateCreationOperation { Target: var target }:
                        operation = target;
                        continue;
                    case IParenthesizedOperation parenthesized:
                        operation = parenthesized.Operand;
                        continue;
                    default:
                        return operation;
                }
            }
        }

        public override void VisitInvalid(IInvalidOperation operation)
        {
            ProcessInvalidCodeSink(operation);
            base.VisitInvalid(operation);
        }

        public override void VisitReturn(IReturnOperation operation)
        {
            if (operation.ReturnedValue is not null && GetTaint(operation.ReturnedValue) is { } taint)
            {
                var returnNode = graph.AddNode("Return", "return", operation, model, basePath, sourceFilePath, _currentMethod, isSource: false, isSink: false, matchedPatterns: [], category: null);
                graph.AddEdges(taint.NodeIds, returnNode.Id, "Return", operation.Syntax, sourceFilePath, "returned value");
            }
            base.VisitReturn(operation);
        }

        private void SeedMethodParameters(IMethodSymbol methodSymbol, SyntaxNode syntax)
        {
            // Lambda parameters are not framework entry-point parameters: binding them here made
            // query lambdas (claims => ...) inherit the enclosing action's [HttpGet] attribute
            // pattern and mint phantom sources. Their values arrive through the enclosing flow.
            if (methodSymbol.MethodKind is MethodKind.LambdaMethod or MethodKind.AnonymousFunction)
            {
                return;
            }

            // R2: index the enclosing method so entry points without a MethodId can be resolved to
            // a concrete graph node by (file, method name).
            graph.RecordMethodLocation(Path.GetFileName(sourceFilePath), methodSymbol.Name, Dosai.FormatMethodSignature(methodSymbol));

            foreach (var parameter in methodSymbol.Parameters)
            {
                var matched = MatchParameterSource(parameter, methodSymbol).ToList();
                if (matched.Count > 0)
                {
                    var node = graph.AddNode("Source", parameter.Name, syntax, model, basePath, sourceFilePath, methodSymbol,
                        isSource: true,
                        isSink: false,
                        matchedPatterns: matched,
                        category: matched.FirstOrDefault()?.Category,
                        symbol: parameter.ToDisplayString(),
                        typeName: Normalize(parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)),
                        code: parameter.Name);
                    _taintedSymbols[SymbolKey(parameter)] = new TaintTrace([node.Id], matched.SelectMany(pattern => pattern.TaintKinds.Count > 0 ? pattern.TaintKinds : [pattern.Category ?? "user-input"]).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), []);
                    continue;
                }

                // Framework entry points taint their bound parameters: a controller action's plain
                // string id, an rpc request message, a hub method argument. These are symbol-anchored
                // seeds from the framework providers, not name matches.
                if (_frameworkSeeds?.Find(Path.GetFileName(sourceFilePath), methodSymbol, parameter.Name) is not { } seed)
                {
                    continue;
                }

                var seedNode = graph.AddNode("Source", parameter.Name, syntax, model, basePath, sourceFilePath, methodSymbol,
                    isSource: true,
                    isSink: false,
                    matchedPatterns: [],
                    category: seed.TaintKind,
                    symbol: parameter.ToDisplayString(),
                    typeName: Normalize(parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)),
                    code: parameter.Name);
                _taintedSymbols[SymbolKey(parameter)] = new TaintTrace([seedNode.Id], [seed.TaintKind, seed.BindingSource], []);
            }
        }

        private IEnumerable<DataFlowPattern> MatchParameterSource(IParameterSymbol parameter, IMethodSymbol methodSymbol)
        {
            var parameterText = $"{parameter.Name} {Normalize(parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))}";
            foreach (var pattern in _patternIndex.SourceParameters.Where(p => PatternMatches(parameter.Name, p) || PatternMatches(parameterText, p)))
            {
                yield return pattern;
            }

            if (methodSymbol.Name == "Main" && parameter.Name.Equals("args", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var pattern in _patternIndex.SourceParameters.Where(p => PatternMatches("Main", p)))
                {
                    yield return pattern;
                }
            }

            var methodAttributes = methodSymbol.GetAttributes().Concat(parameter.GetAttributes()).Select(a => a.AttributeClass?.Name ?? string.Empty).ToList();
            foreach (var pattern in _patternIndex.SourceAttributes.Where(p => methodAttributes.Any(attribute => PatternMatches(attribute, p))))
            {
                yield return pattern;
            }

            var parameterType = Normalize(parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
            foreach (var pattern in _patternIndex.SourceTypes.Where(p => PatternMatches(parameterType, p)))
            {
                yield return pattern;
            }
        }

        private void AssignSymbol(ISymbol symbol, IOperation value, SyntaxNode syntax, string edgeKind)
        {
            if (GetTaint(value) is not { } taint)
            {
                _taintedSymbols.Remove(SymbolKey(symbol));
                return;
            }

            var valueText = _patternIndex.SinkCodeLike.Count == 0 ? null : SyntaxText(value.Syntax);
            var matchedSinkPatterns = valueText is null ? [] : MatchCode(valueText, _patternIndex.SinkCodeLike).ToList();
            if (matchedSinkPatterns.Count > 0)
            {
                var sinkNode = graph.AddNode("Sink", GetOperationName(value), value, model, basePath, sourceFilePath, _currentMethod,
                    isSource: false,
                    isSink: true,
                    matchedPatterns: matchedSinkPatterns,
                    category: matchedSinkPatterns.FirstOrDefault()?.Category,
                    symbol: GetOperationSymbol(value),
                    typeName: Normalize(value.Type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? string.Empty),
                    code: valueText);
                graph.AddEdges(taint.NodeIds, sinkNode.Id, "SinkExpression", value.Syntax, sourceFilePath, symbol.Name);
                graph.AddSlice(taint, sinkNode, matchedSinkPatterns.FirstOrDefault(), valueText, 0);
            }

            var assignmentNode = graph.AddNode("Assignment", symbol.Name, syntax, model, basePath, sourceFilePath, _currentMethod, isSource: false, isSink: false, matchedPatterns: [], category: null, symbol: symbol.ToDisplayString(), typeName: GetSymbolType(symbol), code: SyntaxText(syntax));
            graph.AddEdges(taint.NodeIds, assignmentNode.Id, edgeKind, syntax, sourceFilePath, symbol.Name);
            _taintedSymbols[SymbolKey(symbol)] = taint.Append(assignmentNode.Id);
        }

        private void AssignTarget(IOperation target, IOperation value, SyntaxNode syntax, string edgeKind)
        {
            MarkHardenedAssignment(target, value);
            var symbol = GetReferencedSymbol(target);
            if (symbol is not null)
            {
                var taintKey = TaintKey(target) ?? SymbolKey(symbol);
                if (GetTaint(value) is not { } taint)
                {
                    _taintedSymbols.Remove(taintKey);
                    if (taintKey != SymbolKey(symbol)) _taintedSymbols.Remove(SymbolKey(symbol));
                    return;
                }

                var assignmentNode = graph.AddNode("Assignment", symbol.Name, syntax, model, basePath, sourceFilePath, _currentMethod, isSource: false, isSink: false, matchedPatterns: [], category: null, symbol: symbol.ToDisplayString(), typeName: GetSymbolType(symbol), code: SyntaxText(syntax));
                if (taint.TaintKinds.Count > 0) assignmentNode.Properties["taintKinds"] = string.Join(',', taint.TaintKinds);
                if (taint.FieldPaths.Count > 0) assignmentNode.Properties["fieldPaths"] = string.Join(',', taint.FieldPaths);
                graph.AddEdges(taint.NodeIds, assignmentNode.Id, edgeKind, syntax, sourceFilePath, symbol.Name);
                _taintedSymbols[taintKey] = taint.Append(assignmentNode.Id).WithFieldPath(TaintKey(target));
            }
        }

        /// <summary>
        ///     W7 companion: storing a tainted value into a collection taints the collection, so the
        ///     flow survives being read back (values.Add(input); Sink(values[0])). The old bare-Name
        ///     "Add" sanitizer masked exactly this shape; without it the receiver needs the taint.
        /// </summary>
        private void PropagateCollectionMutation(IInvocationOperation operation)
        {
            if (operation.Instance is null)
            {
                return;
            }

            switch (operation.TargetMethod.Name)
            {
                case "Add" or "AddRange" or "Insert" or "Push" or "Enqueue" or "Append" or "Prepend" or "Concat":
                    break;
                default:
                    return;
            }

            var receiverType = Normalize(operation.Instance.Type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? string.Empty);
            if (!receiverType.StartsWith("System.Collections", StringComparison.Ordinal) &&
                !receiverType.StartsWith("System.Text", StringComparison.Ordinal) &&
                receiverType != "string" && receiverType != "System.String")
            {
                return;
            }

            var argumentTaint = Combine(operation.Arguments.Select(argument => GetTaint(argument.Value)));
            if (argumentTaint is not null && TaintKey(operation.Instance) is { } receiverKey)
            {
                _taintedSymbols[receiverKey] = argumentTaint;
            }
        }

        /// <summary>
        ///     W2: `doc.XmlResolver = null;` / `settings.DtdProcessing = DtdProcessing.Prohibit;` —
        ///     the hardened instance is remembered so sinks receiving it later are suppressed.
        /// </summary>
        private void MarkHardenedAssignment(IOperation target, IOperation value)
        {
            if (Strip(target) is not IPropertyReferenceOperation { Property.Name: "XmlResolver" or "DtdProcessing" } propertyReference)
            {
                return;
            }

            var valueText = SyntaxText(value.Syntax);
            var hardened = propertyReference.Property.Name == "XmlResolver"
                ? HardeningMarkerRegex.IsMatch($"XmlResolver = {valueText}")
                : valueText.Contains("Prohibit", StringComparison.Ordinal) || valueText.Contains("Ignore", StringComparison.Ordinal);
            if (!hardened)
            {
                return;
            }

            if (propertyReference.Instance is not null && GetReferencedSymbol(propertyReference.Instance) is { } instanceSymbol)
            {
                _hardenedSymbols.Add(SymbolKey(instanceSymbol));
            }
        }

        /// <summary>True when the operation references a symbol known to be hardened (cross-statement XXE guard).</summary>
        private bool ReferencesHardenedSymbol(IOperation? operation)
        {
            while (operation is not null)
            {
                switch (operation)
                {
                    case ILocalReferenceOperation local:
                        return _hardenedSymbols.Contains(SymbolKey(local.Local));
                    case IParameterReferenceOperation parameter:
                        return _hardenedSymbols.Contains(SymbolKey(parameter.Parameter));
                    case IPropertyReferenceOperation { Instance: not null } property:
                        operation = property.Instance;
                        break;
                    case IFieldReferenceOperation { Instance: not null } field:
                        operation = field.Instance;
                        break;
                    default:
                        return false;
                }
            }

            return false;
        }

        private void ProcessSink(IOperation operation, IMethodSymbol? targetMethod, IEnumerable<IArgumentOperation> arguments)
        {
            if (targetMethod is null)
            {
                return;
            }

            var argumentList = arguments.ToList();
            var argumentTaints = argumentList.Select(argument => GetTaint(argument.Value)).ToList();
            ProcessInterproceduralSink(operation, targetMethod, argumentList, argumentTaints);

            var matchedSinkPatterns = MatchSymbol(targetMethod, operation.Syntax, _patternIndex.Sinks).ToList();
            if (matchedSinkPatterns.Count == 0)
            {
                return;
            }

            var operationText = SyntaxText(operation.Syntax);
            if (IsHardenedSink(operationText, matchedSinkPatterns, argumentList.Select(argument => argument.Value), out var hardening))
            {
                // W2-style guards: the sink call carries hardening markers in its own text (e.g.
                // XmlReaderSettings created inline with a nulled resolver) or receives an instance
                // hardened in an earlier statement. The would-be flows are negative evidence.
                foreach (var taint in argumentTaints.Where(taint => taint is not null).Cast<TaintTrace>())
                {
                    graph.RecordSanitizedFlow("SanitizerMatch", taint, [hardening!], operation.Syntax, sourceFilePath);
                }

                return;
            }

            if (operation is IInvocationOperation { Instance: not null } invocation && GetTaint(invocation.Instance) is { } receiverTaint)
            {
                var sinkNode = graph.AddNode("Sink", targetMethod.Name, operation, model, basePath, sourceFilePath, _currentMethod,
                    isSource: false,
                    isSink: true,
                    matchedPatterns: matchedSinkPatterns,
                    category: matchedSinkPatterns.FirstOrDefault()?.Category,
                    symbol: DescribeSymbol(targetMethod),
                    typeName: Normalize(targetMethod.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)),
                    code: operationText);
                graph.AddEdges(receiverTaint.NodeIds, sinkNode.Id, "SinkReceiver", invocation.Instance.Syntax, sourceFilePath, "receiver");
                graph.AddSlice(receiverTaint, sinkNode, matchedSinkPatterns.FirstOrDefault(), SyntaxText(invocation.Instance.Syntax), -1);
            }

            for (var index = 0; index < argumentList.Count; index++)
            {
                var argument = argumentList[index];
                if (argumentTaints[index] is not { } taint)
                {
                    continue;
                }

                var sinkNode = graph.AddNode("Sink", targetMethod.Name, operation, model, basePath, sourceFilePath, _currentMethod,
                    isSource: false,
                    isSink: true,
                    matchedPatterns: matchedSinkPatterns,
                    category: matchedSinkPatterns.FirstOrDefault()?.Category,
                    symbol: DescribeSymbol(targetMethod),
                    typeName: Normalize(targetMethod.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)),
                    code: operationText);
                graph.AddEdges(taint.NodeIds, sinkNode.Id, "SinkArgument", argument.Syntax, sourceFilePath, argument.Parameter?.Name ?? $"arg{index}");
                graph.AddSlice(taint, sinkNode, matchedSinkPatterns.FirstOrDefault(), SyntaxText(argument.Syntax), index);
            }
        }

        private void ProcessInterproceduralSink(IOperation operation, IMethodSymbol targetMethod, IReadOnlyList<IArgumentOperation> argumentList, IReadOnlyList<TaintTrace?> argumentTaints)
        {
            if (!TryGetSummary(targetMethod, out var summary) || summary.SinkParameterIndexes.Count == 0)
            {
                return;
            }

            foreach (var parameterIndex in summary.SinkParameterIndexes.Where(index => index >= 0 && index < argumentList.Count))
            {
                var argument = argumentList[parameterIndex];
                if (argumentTaints[parameterIndex] is not { } taint)
                {
                    continue;
                }

                var category = summary.SinkCategories.FirstOrDefault() ?? "interprocedural";
                var summaryPattern = new DataFlowPattern
                {
                    Target = DataFlowPatternTarget.Sink,
                    Kind = DataFlowPatternKind.Method,
                    Pattern = DescribeSymbol(targetMethod),
                    Category = category,
                    Description = "Sink reached through a summarized callee"
                };
                var sinkNode = graph.AddNode("Sink", targetMethod.Name, operation, model, basePath, sourceFilePath, _currentMethod,
                    isSource: false,
                    isSink: true,
                    matchedPatterns: [summaryPattern],
                    category: category,
                    symbol: DescribeSymbol(targetMethod),
                    typeName: Normalize(targetMethod.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)),
                    code: SyntaxText(operation.Syntax));
                sinkNode.Properties["summaryMethod"] = summary.Method;
                // R2: the concrete sink frame (the summarized callee that reaches the real sink) in
                // GenerateMethodSignature form, so exploit chains can path all the way to it.
                sinkNode.Properties["sinkMethodId"] = Dosai.FormatMethodSignature(targetMethod);
                graph.AddEdges(taint.NodeIds, sinkNode.Id, "InterproceduralSink", argument.Syntax, sourceFilePath, argument.Parameter?.Name ?? $"arg{parameterIndex}");
                graph.AddSlice(taint, sinkNode, summaryPattern, SyntaxText(argument.Syntax), parameterIndex);
            }
        }

        private bool TryGetSummary(IMethodSymbol method, out DataFlowMethodSummary summary)
        {
            if (summaries.TryGetValue(DescribeSymbol(method), out summary!))
            {
                return true;
            }

            if (summaries.TryGetValue(DescribeSymbol(method.OriginalDefinition), out summary!))
            {
                return true;
            }

            summary = null!;
            return false;
        }

        private List<DataFlowPattern> MatchSanitizers(IOperation operation)
        {
            var matches = new List<DataFlowPattern>();
            if (operation is IInvocationOperation invocation)
            {
                matches.AddRange(MatchSymbol(invocation.TargetMethod, operation.Syntax, _patternIndex.Sanitizers));
            }

            if (operation is IObjectCreationOperation { Constructor: not null } objectCreation)
            {
                matches.AddRange(MatchSymbol(objectCreation.Constructor, operation.Syntax, _patternIndex.Sanitizers));
            }

            if (_patternIndex.SanitizerCodeLike.Count > 0)
            {
                matches.AddRange(MatchCode(SyntaxText(operation.Syntax), _patternIndex.SanitizerCodeLike));
            }

            return matches;
        }

        /// <summary>
        ///     Applies matched sanitizer patterns to a trace (T4). A sanitizer with an empty
        ///     <see cref="DataFlowPattern.RemovesTaintKinds"/> keeps the historic remove-all
        ///     semantics; a sanitizer that names kinds (e.g. RandomNumberGenerator removes only
        ///     "insecure-random") strips exactly those kinds and lets the rest keep flowing.
        ///     Returns the surviving trace, or null when the flow is fully sanitized.
        /// </summary>
        private static TaintTrace? ApplySanitizers(List<DataFlowPattern> sanitizerPatterns, TaintTrace trace)
        {
            if (sanitizerPatterns.Any(pattern => pattern.RemovesTaintKinds.Count == 0))
            {
                return null;
            }

            var removed = sanitizerPatterns.SelectMany(pattern => pattern.RemovesTaintKinds).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (trace.TaintKinds.Count == 0 || trace.TaintKinds.All(kind => removed.Contains(kind)))
            {
                return null;
            }

            var kept = trace.TaintKinds.Where(kind => !removed.Contains(kind)).ToList();
            return kept.Count == trace.TaintKinds.Count ? trace : new TaintTrace(trace.NodeIds, kept, trace.FieldPaths);
        }

        private TaintTrace? FindExistingTaint(IOperation operation)
        {
            if (TaintKey(operation) is { } operationKey && _taintedSymbols.TryGetValue(operationKey, out var operationTaint))
            {
                return operationTaint;
            }

            if (GetReferencedSymbol(operation) is { } existingSymbol && _taintedSymbols.TryGetValue(SymbolKey(existingSymbol), out var existing))
            {
                return existing;
            }

            return null;
        }

        /// <summary>
        ///     The would-be flow through a sanitized expression: the taint of the sanitizer call's own
        ///     arguments/receiver. Only used for SanitizerMatch negative evidence, never returned as a
        ///     live flow.
        /// </summary>
        private TaintTrace? GetTaintThroughChildrenForSanitizerEvidence(IOperation operation) => operation switch
        {
            IInvocationOperation invocation => Combine(invocation.Arguments.Select(argument => GetTaint(argument.Value)).Append(GetTaint(invocation.Instance))),
            IObjectCreationOperation creation => Combine(creation.Arguments.Select(argument => GetTaint(argument.Value))),
            _ => Combine(operation.ChildOperations.Select(GetTaint))
        };

        private IEnumerable<string> GetSanitizedGuardKeys(IOperation condition, bool whenConditionIsTrue)
        {
            condition = Strip(condition);
            if (condition is IUnaryOperation { OperatorKind: UnaryOperatorKind.Not } negated)
            {
                foreach (var key in GetSanitizedGuardKeys(negated.Operand, !whenConditionIsTrue))
                {
                    yield return key;
                }
                yield break;
            }

            if (condition is IBinaryOperation { OperatorKind: BinaryOperatorKind.ConditionalAnd } andOperation)
            {
                if (whenConditionIsTrue)
                {
                    foreach (var key in GetSanitizedGuardKeys(andOperation.LeftOperand, true).Concat(GetSanitizedGuardKeys(andOperation.RightOperand, true))) yield return key;
                }
                yield break;
            }

            if (condition is IBinaryOperation { OperatorKind: BinaryOperatorKind.ConditionalOr } orOperation)
            {
                if (!whenConditionIsTrue)
                {
                    foreach (var key in GetSanitizedGuardKeys(orOperation.LeftOperand, false).Concat(GetSanitizedGuardKeys(orOperation.RightOperand, false))) yield return key;
                }
                else
                {
                    var left = GetSanitizedGuardKeys(orOperation.LeftOperand, true).ToHashSet(StringComparer.Ordinal);
                    foreach (var key in GetSanitizedGuardKeys(orOperation.RightOperand, true).Where(left.Contains)) yield return key;
                }
                yield break;
            }

            if (whenConditionIsTrue && condition is IInvocationOperation invocation && MatchSymbol(invocation.TargetMethod, invocation.Syntax, _patternIndex.Sanitizers).Any())
            {
                foreach (var argument in invocation.Arguments)
                {
                    if (TaintKey(argument.Value) is { } key)
                    {
                        yield return key;
                    }
                    if (GetReferencedSymbol(argument.Value) is { } symbol)
                    {
                        yield return SymbolKey(symbol);
                    }
                }
            }

            foreach (var child in condition.ChildOperations)
            {
                foreach (var key in GetSanitizedGuardKeys(child, whenConditionIsTrue))
                {
                    yield return key;
                }
            }
        }

        /// <summary>
        ///     W2-style paired hardening: a sink invocation is suppressed when its text matches a
        ///     "<c>&lt;category&gt;-hardening</c>" sanitizer marker (spacing-tolerant) or when any of
        ///     its arguments (or the receiver) references an instance hardened in an earlier
        ///     statement — the idiomatic `settings.XmlResolver = null;` form.
        /// </summary>
        private bool IsHardenedSink(string operationText, List<DataFlowPattern> matchedSinkPatterns, IEnumerable<IOperation> argumentValues, out DataFlowPattern? hardening)
        {
            hardening = null;
            foreach (var sinkCategory in matchedSinkPatterns.Select(pattern => pattern.Category).Where(category => !string.IsNullOrWhiteSpace(category)).Distinct(StringComparer.Ordinal))
            {
                if (!_patternIndex.HardeningSanitizersByCategory.TryGetValue(sinkCategory!, out var candidates))
                {
                    continue;
                }

                foreach (var sanitizer in candidates)
                {
                    if (PatternMatches(operationText, sanitizer))
                    {
                        hardening = sanitizer;
                        return true;
                    }
                }

                if (argumentValues.Any(ReferencesHardenedSymbol))
                {
                    hardening = new DataFlowPattern
                    {
                        Target = DataFlowPatternTarget.Sanitizer,
                        Kind = DataFlowPatternKind.Code,
                        Pattern = "hardened Xml settings instance (cross-statement)",
                        Category = $"{sinkCategory}-hardening",
                        Description = "XmlReaderSettings/XmlDocument hardened in an earlier statement"
                    };
                    return true;
                }
            }

            return false;
        }

        private void ProcessCodeSink(IOperation operation, IEnumerable<IArgumentOperation> arguments)
        {
            if (_patternIndex.SinkCodeLike.Count == 0)
            {
                return;
            }

            var operationText = SyntaxText(operation.Syntax);
            var matchedSinkPatterns = MatchCode(operationText, _patternIndex.SinkCodeLike).ToList();
            if (matchedSinkPatterns.Count == 0)
            {
                return;
            }

            var codeSinkArguments = arguments.ToList();
            if (IsHardenedSink(operationText, matchedSinkPatterns, codeSinkArguments.Select(argument => argument.Value), out var hardening))
            {
                var hardenedTaints = codeSinkArguments.Select(argument => GetTaint(argument.Value)).Where(taint => taint is not null).Cast<TaintTrace>().ToList();
                foreach (var taint in hardenedTaints)
                {
                    graph.RecordSanitizedFlow("SanitizerMatch", taint, [hardening!], operation.Syntax, sourceFilePath);
                }

                if (hardenedTaints.Count > 0)
                {
                    return;
                }
            }

            var argumentList = codeSinkArguments;
            var argumentTaints = argumentList.Select(argument => GetTaint(argument.Value)).ToList();
            if (operation is IInvocationOperation { Instance: not null } invocation && GetTaint(invocation.Instance) is { } receiverTaint)
            {
                var sinkNode = graph.AddNode("Sink", GetOperationName(operation), operation, model, basePath, sourceFilePath, _currentMethod,
                    isSource: false,
                    isSink: true,
                    matchedPatterns: matchedSinkPatterns,
                    category: matchedSinkPatterns.FirstOrDefault()?.Category,
                    symbol: GetOperationSymbol(operation),
                    typeName: Normalize(operation.Type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? string.Empty),
                    code: operationText);
                graph.AddEdges(receiverTaint.NodeIds, sinkNode.Id, "SinkReceiver", invocation.Instance.Syntax, sourceFilePath, "receiver");
                graph.AddSlice(receiverTaint, sinkNode, matchedSinkPatterns.FirstOrDefault(), SyntaxText(invocation.Instance.Syntax), -1);
            }

            for (var index = 0; index < argumentList.Count; index++)
            {
                var argument = argumentList[index];
                if (argumentTaints[index] is not { } taint)
                {
                    continue;
                }

                var sinkNode = graph.AddNode("Sink", GetOperationName(operation), operation, model, basePath, sourceFilePath, _currentMethod,
                    isSource: false,
                    isSink: true,
                    matchedPatterns: matchedSinkPatterns,
                    category: matchedSinkPatterns.FirstOrDefault()?.Category,
                    symbol: GetOperationSymbol(operation),
                    typeName: Normalize(operation.Type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? string.Empty),
                    code: operationText);
                graph.AddEdges(taint.NodeIds, sinkNode.Id, "SinkArgument", argument.Syntax, sourceFilePath, argument.Parameter?.Name ?? $"arg{index}");
                graph.AddSlice(taint, sinkNode, matchedSinkPatterns.FirstOrDefault(), SyntaxText(argument.Syntax), index);
            }
        }

        private void ProcessInvalidCodeSink(IInvalidOperation operation)
        {
            if (_patternIndex.SinkCodeLike.Count == 0)
            {
                return;
            }

            var operationText = SyntaxText(operation.Syntax);
            var matchedSinkPatterns = MatchCode(operationText, _patternIndex.SinkCodeLike).ToList();
            if (matchedSinkPatterns.Count == 0)
            {
                return;
            }

            var taint = Combine(operation.ChildOperations.Select(GetTaint));
            if (taint is null)
            {
                return;
            }

            var sinkNode = graph.AddNode("Sink", GetOperationName(operation), operation, model, basePath, sourceFilePath, _currentMethod,
                isSource: false,
                isSink: true,
                matchedPatterns: matchedSinkPatterns,
                category: matchedSinkPatterns.FirstOrDefault()?.Category,
                symbol: GetOperationSymbol(operation),
                typeName: Normalize(operation.Type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? string.Empty),
                code: operationText);
            graph.AddEdges(taint.NodeIds, sinkNode.Id, "SinkExpression", operation.Syntax, sourceFilePath, operation.Kind.ToString());
            graph.AddSlice(taint, sinkNode, matchedSinkPatterns.FirstOrDefault(), operationText, 0);
        }

        private TaintTrace? GetTaint(IOperation? operation)
        {
            if (operation is null || MaxDepthReached)
            {
                return null;
            }

            EnterDepth();
            try
            {
                return GetTaintCore(operation);
            }
            finally
            {
                ExitDepth();
            }
        }

        private TaintTrace? GetTaintCore(IOperation operation)
        {
            operation = Strip(operation);
            if (_awaitTaintBySyntax.TryGetValue(operation.Syntax, out var awaitTaint))
            {
                return awaitTaint;
            }

            var sanitizerPatterns = MatchSanitizers(operation);
            if (sanitizerPatterns.Count > 0)
            {
                // T4: when a sanitizer suppresses (or narrows) a live flow, record the negative
                // evidence instead of letting the flow silently disappear.
                var existingTaint = FindExistingTaint(operation) ?? GetTaintThroughChildrenForSanitizerEvidence(operation);
                var sanitized = existingTaint is null ? null : ApplySanitizers(sanitizerPatterns, existingTaint);
                if (existingTaint is not null && !ReferenceEquals(sanitized, existingTaint))
                {
                    graph.RecordSanitizedFlow("SanitizerMatch", existingTaint, sanitizerPatterns, operation.Syntax, sourceFilePath);
                }

                if (sanitized is null)
                {
                    return null;
                }

                if (!ReferenceEquals(sanitized, existingTaint))
                {
                    // Partially sanitized: only the surviving taint kinds keep flowing.
                    return sanitized;
                }

                // The sanitizer names kinds this flow does not carry; fall through so the canonical
                // trace (with its full node chain) is returned.
            }

            if (TaintKey(operation) is { } operationKey && _taintedSymbols.TryGetValue(operationKey, out var operationTaint))
            {
                return operationTaint;
            }

            if (GetReferencedSymbol(operation) is { } existingSymbol && _taintedSymbols.TryGetValue(SymbolKey(existingSymbol), out var existing))
            {
                return existing;
            }

            // A validation guard suppressed this symbol for the guarded branch: neither the seeded
            // map nor pattern minting may re-introduce the taint here.
            if (GetReferencedSymbol(operation) is { } guardedSymbol && _suppressedGuardKeys.Contains(SymbolKey(guardedSymbol)))
            {
                return null;
            }

            var matchedSourcePatterns = MatchOperationSource(operation).ToList();
            if (matchedSourcePatterns.Count > 0)
            {
                var sourceNode = graph.AddNode("Source", GetOperationName(operation), operation, model, basePath, sourceFilePath, _currentMethod,
                    isSource: true,
                    isSink: false,
                    matchedPatterns: matchedSourcePatterns,
                    category: matchedSourcePatterns.FirstOrDefault()?.Category,
                    symbol: GetOperationSymbol(operation),
                    typeName: Normalize(operation.Type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? string.Empty),
                    code: SyntaxText(operation.Syntax));
                return new TaintTrace([sourceNode.Id], matchedSourcePatterns.SelectMany(pattern => pattern.TaintKinds.Count > 0 ? pattern.TaintKinds : [pattern.Category ?? "user-input"]).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), []);
            }

            if (operation is IInvocationOperation invocation)
            {
                var argumentList = invocation.Arguments.ToList();
                var argumentTaints = argumentList.Select(argument => GetTaint(argument.Value)).ToList();
                var argTaint = Combine(argumentTaints);
                var receiverTaint = GetTaint(invocation.Instance);
                if (receiverTaint is not null && ShouldPropagateReceiverThrough(invocation.TargetMethod))
                {
                    var node = graph.AddNode("Call", invocation.TargetMethod.Name, operation, model, basePath, sourceFilePath, _currentMethod, isSource: false, isSink: false, matchedPatterns: [], category: null, symbol: DescribeSymbol(invocation.TargetMethod), typeName: Normalize(invocation.Type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? string.Empty), code: SyntaxText(operation.Syntax));
                    graph.AddEdges(receiverTaint.NodeIds, node.Id, "ReceiverReturn", operation.Syntax, sourceFilePath, invocation.TargetMethod.Name);
                    return receiverTaint.Append(node.Id);
                }

                // T2: invoking a delegate variable (a lambda stored earlier) propagates argument and
                // captured taint to the invocation result, mirroring how the lambda's body was walked.
                if (invocation.TargetMethod.MethodKind == MethodKind.DelegateInvoke && (argTaint is not null || receiverTaint is not null))
                {
                    var delegateTaint = Combine([argTaint, receiverTaint]);
                    if (delegateTaint is not null)
                    {
                        var node = graph.AddNode("Call", "Invoke", operation, model, basePath, sourceFilePath, _currentMethod, isSource: false, isSink: false, matchedPatterns: [], category: null, symbol: DescribeSymbol(invocation.TargetMethod), typeName: Normalize(invocation.Type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? string.Empty), code: SyntaxText(operation.Syntax));
                        graph.AddEdges(delegateTaint.NodeIds, node.Id, "DelegateInvoke", operation.Syntax, sourceFilePath, "invoke");
                        return delegateTaint.Append(node.Id);
                    }
                }

                // T12: booleans carry a decision, not the payload — a `bool IsValid(string)` returning
                // "tainted true" minted phantom flows into every sink that consumed the check result.
                var propagatesPayload = !ReturnsDecisionOnly(invocation.TargetMethod);
                if (argTaint is not null && propagatesPayload && TryGetSummary(invocation.TargetMethod, out var invocationSummary))
                {
                    var matchingIndexes = invocationSummary.ReturnParameterIndexes.Where(index => index >= 0 && index < argumentTaints.Count && argumentTaints[index] is not null).ToList();
                    if (matchingIndexes.Count > 0)
                    {
                        var node = graph.AddNode("CallSummary", invocation.TargetMethod.Name, operation, model, basePath, sourceFilePath, _currentMethod, isSource: false, isSink: false, matchedPatterns: [], category: null, symbol: DescribeSymbol(invocation.TargetMethod), typeName: Normalize(invocation.Type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? string.Empty), code: SyntaxText(operation.Syntax));
                        graph.AddEdges(argTaint.NodeIds, node.Id, "InterproceduralReturn", operation.Syntax, sourceFilePath, string.Join(",", matchingIndexes));
                        return argTaint.Append(node.Id);
                    }
                }
                if (argTaint is not null && propagatesPayload && ShouldPropagateThrough(invocation.TargetMethod))
                {
                    var node = graph.AddNode("Call", invocation.TargetMethod.Name, operation, model, basePath, sourceFilePath, _currentMethod, isSource: false, isSink: false, matchedPatterns: [], category: null, symbol: DescribeSymbol(invocation.TargetMethod), typeName: Normalize(invocation.Type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? string.Empty), code: SyntaxText(operation.Syntax));
                    graph.AddEdges(argTaint.NodeIds, node.Id, "CallReturn", operation.Syntax, sourceFilePath, invocation.TargetMethod.Name);
                    return argTaint.Append(node.Id);
                }
                // T12: a boolean result is a decision about the input, not the payload itself.
                return propagatesPayload ? argTaint : null;
            }

            if (operation is IObjectCreationOperation objectCreation)
            {
                return Combine(objectCreation.Arguments.Select(argument => GetTaint(argument.Value)));
            }

            var childTaint = Combine(operation.ChildOperations.Select(GetTaint));
            if (childTaint is not null && CreatesExpressionNode(operation))
            {
                var expressionNode = graph.AddNode("Expression", GetOperationName(operation), operation, model, basePath, sourceFilePath, _currentMethod, isSource: false, isSink: false, matchedPatterns: [], category: null, symbol: GetOperationSymbol(operation), typeName: Normalize(operation.Type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? string.Empty), code: SyntaxText(operation.Syntax));
                graph.AddEdges(childTaint.NodeIds, expressionNode.Id, "Expression", operation.Syntax, sourceFilePath, operation.Kind.ToString());
                return childTaint.Append(expressionNode.Id);
            }

            return childTaint;
        }

        private IEnumerable<DataFlowPattern> MatchOperationSource(IOperation operation)
        {
            if (_patternIndex.SourceCode.Count > 0)
            {
                var operationText = SyntaxText(operation.Syntax);
                foreach (var pattern in _patternIndex.SourceCode.Where(p => PatternMatches(operationText, p)))
                {
                    yield return pattern;
                }
            }

            if (operation is IParameterReferenceOperation parameterReference)
            {
                var ownerMethod = _currentMethod ?? parameterReference.Parameter.ContainingSymbol as IMethodSymbol ?? throw new InvalidOperationException("Parameter without containing method");
                // A lambda parameter must not be matched against the enclosing method's routing
                // attributes: `claims.Select(c => ...)` inside an [HttpGet] action made `c` a
                // phantom http source through exactly this path.
                if (SymbolEqualityComparer.Default.Equals(parameterReference.Parameter.ContainingSymbol, ownerMethod))
                {
                    foreach (var pattern in MatchParameterSource(parameterReference.Parameter, ownerMethod))
                    {
                        yield return pattern;
                    }
                }
            }

            if (GetReferencedSymbol(operation) is { } symbol)
            {
                foreach (var pattern in MatchSymbol(symbol, operation.Syntax, _patternIndex.Sources))
                {
                    yield return pattern;
                }
            }

            if (operation.Type is not null)
            {
                var typeName = Normalize(operation.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                foreach (var pattern in _patternIndex.SourceTypes.Where(p => PatternMatches(typeName, p)))
                {
                    yield return pattern;
                }
            }
        }

        private bool ShouldPropagateThrough(IMethodSymbol methodSymbol)
        {
            var symbolName = DescribeSymbol(methodSymbol);
            var containingType = Normalize(methodSymbol.ContainingType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? string.Empty);
            var namespaceName = methodSymbol.ContainingNamespace?.ToDisplayString() ?? string.Empty;
            var matchedPassthrough = _patternIndex.Passthroughs.Any(pattern => PatternMatches(pattern.Kind switch
            {
                DataFlowPatternKind.Method => symbolName,
                DataFlowPatternKind.Symbol => symbolName,
                DataFlowPatternKind.Type => containingType,
                DataFlowPatternKind.Namespace => namespaceName,
                DataFlowPatternKind.Name => methodSymbol.Name,
                _ => symbolName
            }, pattern));
            return matchedPassthrough || methodSymbol.ContainingNamespace?.ToDisplayString().StartsWith("System", StringComparison.Ordinal) == true || !methodSymbol.Locations.Any(location => location.IsInMetadata);
        }

        /// <summary>
        ///     T12: a boolean return carries a decision about the input, not the input itself.
        ///     Validator-shaped methods (IsMatch/TryParse/IsValid/…) and every other bool-returning
        ///     method stop propagating argument taint to their result.
        /// </summary>
        private static bool ReturnsDecisionOnly(IMethodSymbol methodSymbol) =>
            methodSymbol.ReturnType?.SpecialType == SpecialType.System_Boolean;

        private static bool ShouldPropagateReceiverThrough(IMethodSymbol methodSymbol)
        {
            var type = Normalize(methodSymbol.ContainingType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? string.Empty);
            var name = methodSymbol.Name;
            return type.StartsWith("System.Collections", StringComparison.Ordinal) ||
                   type.StartsWith("System.Linq", StringComparison.Ordinal) ||
                   type == "string" ||
                   type == "System.String" ||
                   name is "First" or "FirstOrDefault" or "Single" or "SingleOrDefault" or "Last" or "LastOrDefault" or "ElementAt" or "ToList" or "ToArray" or "Select" or "Where" or "Trim" or "Replace" or "Substring" or "ToString";
        }

        private IEnumerable<DataFlowPattern> MatchSymbol(ISymbol symbol, SyntaxNode syntax, IReadOnlyList<DataFlowPattern> candidatePatterns)
        {
            var normalizedSymbol = DescribeSymbol(symbol);
            var name = symbol.Name;
            var containingType = Normalize((symbol.ContainingType ?? symbol as INamedTypeSymbol)?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? string.Empty);
            var namespaceName = symbol.ContainingNamespace?.ToDisplayString() ?? string.Empty;
            string? code = null;
            string? attributes = null;

            foreach (var pattern in candidatePatterns)
            {
                var value = pattern.Kind switch
                {
                    DataFlowPatternKind.Method => normalizedSymbol,
                    DataFlowPatternKind.Symbol => normalizedSymbol,
                    DataFlowPatternKind.Type => containingType,
                    DataFlowPatternKind.Namespace => namespaceName,
                    DataFlowPatternKind.Name => name,
                    DataFlowPatternKind.Code => code ??= SyntaxText(syntax),
                    DataFlowPatternKind.Attribute => attributes ??= string.Join(' ', symbol.GetAttributes().Select(a => a.AttributeClass?.Name ?? string.Empty)),
                    DataFlowPatternKind.Parameter => null,
                    _ => normalizedSymbol
                };

                if (value is not null && PatternMatches(value, pattern))
                {
                    yield return pattern;
                }
            }
        }

        private static IEnumerable<DataFlowPattern> MatchCode(string code, IEnumerable<DataFlowPattern> candidatePatterns)
        {
            foreach (var pattern in candidatePatterns)
            {
                var canMatchCode = pattern.Kind is DataFlowPatternKind.Code or DataFlowPatternKind.Method or DataFlowPatternKind.Symbol or DataFlowPatternKind.Name;
                if (canMatchCode && PatternMatches(code, pattern))
                {
                    yield return pattern;
                }
            }
        }

        private static bool PatternMatches(string value, DataFlowPattern pattern)
        {
            return pattern.Match switch
            {
                DataFlowMatchKind.Exact => value.Equals(pattern.Pattern, StringComparison.OrdinalIgnoreCase),
                DataFlowMatchKind.Prefix => value.StartsWith(pattern.Pattern, StringComparison.OrdinalIgnoreCase),
                DataFlowMatchKind.Suffix => value.EndsWith(pattern.Pattern, StringComparison.OrdinalIgnoreCase),
                DataFlowMatchKind.Regex => Regex.IsMatch(value, pattern.Pattern, RegexOptions.CultureInvariant),
                _ => value.Contains(pattern.Pattern, StringComparison.OrdinalIgnoreCase)
            };
        }

        private static TaintTrace? Combine(IEnumerable<TaintTrace?> traces)
        {
            var nodeIds = new List<string>();
            var taintKinds = new List<string>();
            var fieldPaths = new List<string>();
            var seenNodeIds = new HashSet<string>(StringComparer.Ordinal);
            var seenTaintKinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenFieldPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var trace in traces)
            {
                if (trace is null)
                {
                    continue;
                }

                foreach (var nodeId in trace.NodeIds)
                {
                    if (seenNodeIds.Add(nodeId)) nodeIds.Add(nodeId);
                }

                foreach (var taintKind in trace.TaintKinds)
                {
                    if (seenTaintKinds.Add(taintKind)) taintKinds.Add(taintKind);
                }

                foreach (var fieldPath in trace.FieldPaths)
                {
                    if (seenFieldPaths.Add(fieldPath)) fieldPaths.Add(fieldPath);
                }
            }

            return nodeIds.Count == 0 ? null : new TaintTrace(nodeIds, taintKinds, fieldPaths);
        }

        private string SyntaxText(SyntaxNode syntax)
        {
            if (_syntaxTextCache.TryGetValue(syntax, out var text))
            {
                return text;
            }

            text = SafeSyntaxText.Text(syntax);
            _syntaxTextCache[syntax] = text;
            return text;
        }

        private static IOperation Strip(IOperation operation)
        {
            while (operation is IConversionOperation conversion)
            {
                operation = conversion.Operand;
            }
            return operation;
        }

        private static bool CreatesExpressionNode(IOperation operation) => operation is IBinaryOperation or IInterpolatedStringOperation or IArrayCreationOperation or ICoalesceOperation;

        private static ISymbol? GetReferencedSymbol(IOperation operation) => operation switch
        {
            ILocalReferenceOperation local => local.Local,
            IParameterReferenceOperation parameter => parameter.Parameter,
            IFieldReferenceOperation field => field.Field,
            IPropertyReferenceOperation property => property.Property,
            IInvocationOperation invocation => invocation.TargetMethod,
            IObjectCreationOperation creation => creation.Constructor,
            _ => null
        };

        private static string GetSymbolType(ISymbol symbol) => symbol switch
        {
            ILocalSymbol local => Normalize(local.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)),
            IParameterSymbol parameter => Normalize(parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)),
            IFieldSymbol field => Normalize(field.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)),
            IPropertySymbol property => Normalize(property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)),
            _ => string.Empty
        };

        private static string SymbolKey(ISymbol symbol) => DescribeSymbol(symbol);

        private static string? TaintKey(IOperation operation)
        {
            operation = Strip(operation);
            return operation switch
            {
                ILocalReferenceOperation local => SymbolKey(local.Local),
                IParameterReferenceOperation parameter => SymbolKey(parameter.Parameter),
                IFieldReferenceOperation field => MemberTaintKey(field.Field, field.Instance),
                IPropertyReferenceOperation property => MemberTaintKey(property.Property, property.Instance),
                _ => null
            };
        }

        private static string MemberTaintKey(ISymbol member, IOperation? instance)
        {
            if (instance is null)
            {
                return SymbolKey(member);
            }

            instance = Strip(instance);
            if (GetReferencedSymbol(instance) is { } instanceSymbol)
            {
                return $"{SymbolKey(member)}@{SymbolKey(instanceSymbol)}";
            }

            return $"{SymbolKey(member)}@{instance.Syntax}";
        }

        private static string GetOperationName(IOperation operation) => GetReferencedSymbol(operation)?.Name ?? operation.Kind.ToString();

        private static string? GetOperationSymbol(IOperation operation) => GetReferencedSymbol(operation) is { } symbol ? DescribeSymbol(symbol) : null;

        private static string DescribeSymbol(ISymbol symbol)
        {
            if (symbol is IMethodSymbol methodSymbol)
            {
                var containingType = Normalize(methodSymbol.ContainingType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? string.Empty);
                var methodName = methodSymbol.MethodKind == MethodKind.Constructor ? ".ctor" : methodSymbol.Name;
                var parameters = string.Join(",", methodSymbol.Parameters.Select(p => Normalize(p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))));
                return $"{containingType}.{methodName}({parameters})";
            }

            return Normalize(symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
        }
    }

    private sealed record TaintTrace(List<string> NodeIds, List<string> TaintKinds, List<string> FieldPaths)
    {
        public TaintTrace Append(string nodeId)
        {
            if (NodeIds.Contains(nodeId, StringComparer.Ordinal))
            {
                return this;
            }

            var nodeIds = new List<string>(NodeIds.Count + 1);
            nodeIds.AddRange(NodeIds);
            nodeIds.Add(nodeId);
            return new TaintTrace(nodeIds, TaintKinds, FieldPaths);
        }

        public TaintTrace WithFieldPath(string? fieldPath) => string.IsNullOrWhiteSpace(fieldPath) || FieldPaths.Contains(fieldPath, StringComparer.Ordinal)
            ? this
            : new TaintTrace(NodeIds, TaintKinds, FieldPaths.Concat([fieldPath]).ToList());
    }

    private sealed class DataFlowPatternIndex
    {
        public DataFlowPatternIndex(DataFlowPatternSet patterns)
        {
            Sources = patterns.Sources.Where(HasSignal).ToList();
            Sinks = patterns.Sinks.Where(HasSignal).ToList();
            Passthroughs = patterns.Passthroughs.Where(HasSignal).ToList();
            Sanitizers = patterns.Sanitizers.Where(HasSignal).ToList();
            SourceParameters = Sources.Where(pattern => pattern.Kind == DataFlowPatternKind.Parameter).ToArray();
            SourceAttributes = Sources.Where(pattern => pattern.Kind == DataFlowPatternKind.Attribute).ToArray();
            SourceTypes = Sources.Where(pattern => pattern.Kind == DataFlowPatternKind.Type).ToArray();
            SourceCode = Sources.Where(pattern => pattern.Kind == DataFlowPatternKind.Code).ToArray();
            SinkCodeLike = Sinks.Where(IsCodeLike).ToArray();
            SanitizerCodeLike = Sanitizers.Where(IsCodeLike).ToArray();
            HardeningSanitizersByCategory = Sanitizers
                .Where(pattern => pattern.Category is not null && pattern.Category.EndsWith("-hardening", StringComparison.Ordinal))
                .GroupBy(pattern => pattern.Category![..^"-hardening".Length], StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        }

        public IReadOnlyList<DataFlowPattern> Sources { get; }
        public IReadOnlyList<DataFlowPattern> Sinks { get; }
        public IReadOnlyList<DataFlowPattern> Passthroughs { get; }
        public IReadOnlyList<DataFlowPattern> Sanitizers { get; }
        public IReadOnlyList<DataFlowPattern> SourceParameters { get; }
        public IReadOnlyList<DataFlowPattern> SourceAttributes { get; }
        public IReadOnlyList<DataFlowPattern> SourceTypes { get; }
        public IReadOnlyList<DataFlowPattern> SourceCode { get; }
        public IReadOnlyList<DataFlowPattern> SinkCodeLike { get; }
        public IReadOnlyList<DataFlowPattern> SanitizerCodeLike { get; }
        public IReadOnlyDictionary<string, List<DataFlowPattern>> HardeningSanitizersByCategory { get; }

        private static bool IsCodeLike(DataFlowPattern pattern) => pattern.Kind is DataFlowPatternKind.Code or DataFlowPatternKind.Method or DataFlowPatternKind.Symbol or DataFlowPatternKind.Name;

        /// <summary>
        ///     W7: port of the IL-mode noise filter (DataFlowAssembly's IsAssemblySourceMemberPattern)
        ///     so both modes agree — a bare Name/Contains pattern shorter than four characters matches
        ///     too much of the dictionary to be a signal (key, url, id, ...).
        /// </summary>
        private static bool HasSignal(DataFlowPattern pattern) => pattern.Kind switch
        {
            DataFlowPatternKind.Name when pattern is { Match: DataFlowMatchKind.Contains, Pattern.Length: < 4 } => false,
            _ => true
        };
    }

    private sealed class DataFlowGraphBuilder(DataFlowResult result, PackageUrlResolver purlResolver, string basePath)
    {
        private int _nodeCounter;
        private int _edgeCounter;
        private int _sliceCounter;
        private int _sanitizedFlowCounter;
        private readonly HashSet<string> _edgeKeys = new(StringComparer.Ordinal);
        private readonly HashSet<string> _sliceKeys = new(StringComparer.Ordinal);
        private readonly HashSet<string> _sanitizedFlowKeys = new(StringComparer.Ordinal);
        private readonly Dictionary<string, DataFlowNode> _nodesById = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<DataFlowEdge>> _outgoingEdgesBySource = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _methodEdges = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _methodIdsByFileMethod = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Caller method id → callee method ids, recorded during the operation walk (R2).</summary>
        public IReadOnlyDictionary<string, HashSet<string>> MethodEdges => _methodEdges;

        /// <summary>"file|methodName" → method id, for resolving entry points without a MethodId (R2).</summary>
        public IReadOnlyDictionary<string, string> MethodIdsByFileMethod => _methodIdsByFileMethod;

        public void RecordDiagnostic(string message)
        {
            if (!result.Diagnostics.Contains(message, StringComparer.Ordinal))
            {
                result.Diagnostics.Add(message);
            }
        }

        public void AddMethodEdge(string caller, string callee)
        {
            if (!_methodEdges.TryGetValue(caller, out var callees))
            {
                callees = [];
                _methodEdges[caller] = callees;
            }

            callees.Add(callee);
        }

        public void RecordMethodLocation(string? fileName, string? methodName, string methodId)
        {
            if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(methodName))
            {
                return;
            }

            _methodIdsByFileMethod.TryAdd($"{fileName}|{methodName}", methodId);
        }

        public void RecordSanitizedFlow(string kind, TaintTrace trace, List<DataFlowPattern> sanitizerPatterns, SyntaxNode syntax, string sourceFilePath)
        {
            var lineSpan = syntax.GetLocation().GetLineSpan();
            var sanitizer = sanitizerPatterns.First();
            var sourceIds = trace.NodeIds.Where(nodeId => _nodesById.TryGetValue(nodeId, out var node) && node.IsSource).Take(4).ToList();
            if (sourceIds.Count == 0)
            {
                sourceIds = trace.NodeIds.Take(2).ToList();
            }

            var key = $"{kind}\u001f{string.Join(',', sourceIds)}\u001f{sanitizer.Pattern}\u001f{Path.GetFileName(sourceFilePath)}\u001f{lineSpan.StartLinePosition.Line + 1}";
            if (!_sanitizedFlowKeys.Add(key))
            {
                return;
            }

            result.SanitizedFlows.Add(new SanitizedFlow
            {
                Id = $"sf{++_sanitizedFlowCounter}",
                Kind = kind,
                SourceIds = sourceIds,
                SourceCategory = sourceIds.Select(id => _nodesById.TryGetValue(id, out var node) ? node.Category : null).FirstOrDefault(category => category is not null),
                Expression = TrimCode(SafeSyntaxText.Text(syntax)),
                SanitizerSymbol = sanitizer.Pattern,
                SanitizerCategory = sanitizer.Category,
                RemovesTaintKinds = sanitizerPatterns.SelectMany(pattern => pattern.RemovesTaintKinds).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                FileName = Path.GetFileName(sourceFilePath),
                LineNumber = lineSpan.StartLinePosition.Line + 1,
                ColumnNumber = lineSpan.StartLinePosition.Character + 1
            });
        }

        public void RecordSanitizedGuard(TaintTrace trace, SyntaxNode guardSyntax, string sourceFilePath)
        {
            var lineSpan = guardSyntax.GetLocation().GetLineSpan();
            var sourceIds = trace.NodeIds.Where(nodeId => _nodesById.TryGetValue(nodeId, out var node) && node.IsSource).Take(4).ToList();
            if (sourceIds.Count == 0)
            {
                sourceIds = trace.NodeIds.Take(2).ToList();
            }

            var key = $"SanitizerGuard\u001f{string.Join(',', sourceIds)}\u001f{Path.GetFileName(sourceFilePath)}\u001f{lineSpan.StartLinePosition.Line + 1}";
            if (!_sanitizedFlowKeys.Add(key))
            {
                return;
            }

            result.SanitizedFlows.Add(new SanitizedFlow
            {
                Id = $"sf{++_sanitizedFlowCounter}",
                Kind = "SanitizerGuard",
                SourceIds = sourceIds,
                SourceCategory = sourceIds.Select(id => _nodesById.TryGetValue(id, out var node) ? node.Category : null).FirstOrDefault(category => category is not null),
                Expression = TrimCode(SafeSyntaxText.Text(guardSyntax)),
                SanitizerCategory = "validation-guard",
                FileName = Path.GetFileName(sourceFilePath),
                LineNumber = lineSpan.StartLinePosition.Line + 1,
                ColumnNumber = lineSpan.StartLinePosition.Character + 1
            });
        }

        public DataFlowNode AddNode(string kind, string name, IOperation operation, SemanticModel model, string basePath, string sourceFilePath, IMethodSymbol? method, bool isSource, bool isSink, IReadOnlyCollection<DataFlowPattern> matchedPatterns, string? category, string? symbol = null, string? typeName = null, string? code = null)
            => AddNode(kind, name, operation.Syntax, model, basePath, sourceFilePath, method, isSource, isSink, matchedPatterns, category, symbol, typeName, code);

        public DataFlowNode AddNode(string kind, string name, SyntaxNode syntax, SemanticModel model, string basePath, string sourceFilePath, IMethodSymbol? method, bool isSource, bool isSink, IReadOnlyCollection<DataFlowPattern> matchedPatterns, string? category, string? symbol = null, string? typeName = null, string? code = null)
        {
            var lineSpan = syntax.GetLocation().GetLineSpan();
            var node = new DataFlowNode
            {
                Id = $"dfn{++_nodeCounter}",
                Kind = kind,
                Name = name,
                Symbol = symbol,
                Type = typeName,
                Purl = matchedPatterns.Select(pattern => pattern.Purl).FirstOrDefault(purl => !string.IsNullOrWhiteSpace(purl)) ??
                       purlResolver.Resolve(method?.ContainingAssembly?.ToDisplayString(), method?.ContainingModule?.ToDisplayString(), symbol, method?.ContainingNamespace?.ToDisplayString(), typeName),
                Code = TrimCode(code ?? syntax.ToString()),
                Path = Path.GetRelativePath(basePath, sourceFilePath),
                FileName = Path.GetFileName(sourceFilePath),
                Namespace = method?.ContainingNamespace?.ToDisplayString() ?? string.Empty,
                ClassName = method?.ContainingType?.Name ?? string.Empty,
                MethodName = method?.Name ?? model.GetEnclosingSymbol(syntax.SpanStart)?.Name,
                LineNumber = lineSpan.StartLinePosition.Line + 1,
                ColumnNumber = lineSpan.StartLinePosition.Character + 1,
                IsSource = isSource,
                IsSink = isSink,
                MatchedPatterns = matchedPatterns.Select(p => p.Pattern).Distinct(StringComparer.Ordinal).ToList(),
                Category = category,
                MethodIdentity = method is null ? null : MethodIdentityFactory.FromParts(null, Normalize(method.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)), null, Normalize(method.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)), method.ContainingAssembly?.ToDisplayString(), method.ContainingModule?.ToDisplayString(), method.ContainingNamespace?.ToDisplayString(), method.ContainingType?.Name, method.Name, 0, null, AnalysisEvidenceKind.SourceRoslynDirect),
                Evidence =
                [
                    new AnalysisEvidence
                    {
                        Kind = AnalysisEvidenceKind.SourceRoslynDirect,
                        Source = "roslyn-source",
                        Description = "Data-flow node discovered from Roslyn operation analysis.",
                        FileName = Path.GetFileName(sourceFilePath),
                        LineNumber = lineSpan.StartLinePosition.Line + 1,
                        ColumnNumber = lineSpan.StartLinePosition.Character + 1
                    }
                ]
            };
            if (method is not null)
            {
                node.Properties["method"] = Normalize(method.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                // R2: the GenerateMethodSignature-form id matches entry-point MethodIds so exploit
                // chains can link taint nodes to concrete graph paths.
                node.Properties["methodId"] = Dosai.FormatMethodSignature(method);
            }
            result.Nodes.Add(node);
            _nodesById[node.Id] = node;
            return node;
        }

        public void AddEdges(IEnumerable<string> sourceIds, string targetId, string kind, SyntaxNode syntax, string sourceFilePath, string? label)
        {
            foreach (var sourceId in sourceIds.Distinct(StringComparer.Ordinal))
            {
                var key = $"{sourceId}\u001f{targetId}\u001f{kind}\u001f{label}";
                if (!_edgeKeys.Add(key))
                {
                    continue;
                }
                var lineSpan = syntax.GetLocation().GetLineSpan();
                var edge = new DataFlowEdge
                {
                    Id = $"dfe{++_edgeCounter}",
                    SourceId = sourceId,
                    TargetId = targetId,
                    Kind = kind,
                    Label = label,
                    SourcePurl = _nodesById.TryGetValue(sourceId, out var sourceNode) ? sourceNode.Purl : null,
                    TargetPurl = _nodesById.TryGetValue(targetId, out var targetNode) ? targetNode.Purl : null,
                    Path = SafeRelativeSourcePath(basePath, sourceFilePath),
                    FileName = Path.GetFileName(sourceFilePath),
                    LineNumber = lineSpan.StartLinePosition.Line + 1,
                    ColumnNumber = lineSpan.StartLinePosition.Character + 1
                };
                result.Edges.Add(edge);
                if (!_outgoingEdgesBySource.TryGetValue(edge.SourceId, out var outgoing))
                {
                    outgoing = [];
                    _outgoingEdgesBySource[edge.SourceId] = outgoing;
                }
                outgoing.Add(edge);
            }
        }

        public void AddSlice(TaintTrace trace, DataFlowNode sinkNode, DataFlowPattern? sinkPattern, string? sinkArgument, int sinkArgumentIndex)
        {
            var nodeIds = trace.NodeIds.Concat([sinkNode.Id]).Distinct(StringComparer.Ordinal).ToList();
            var nodeIdSet = nodeIds.ToHashSet(StringComparer.Ordinal);
            var edgeIds = nodeIds
                .Where(nodeId => _outgoingEdgesBySource.ContainsKey(nodeId))
                .SelectMany(nodeId => _outgoingEdgesBySource[nodeId])
                .Where(edge => nodeIdSet.Contains(edge.TargetId))
                .Select(edge => edge.Id)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var firstSource = trace.NodeIds.FirstOrDefault(id => _nodesById.TryGetValue(id, out var candidateSource) && candidateSource.IsSource) ?? trace.NodeIds.First();
            // T10: source-mode slices used to be appended per match, so the same flow repeated for
            // every re-evaluation in loops and guards. Keyed on the flow, not the per-site sink
            // node id (each evaluation mints a fresh node), mirroring the IL mode's _sliceKeys.
            var sliceKey = $"{firstSource}\u001f{sinkPattern?.Category ?? sinkNode.Category}\u001f{sinkArgumentIndex}\u001f{sinkArgument}";
            if (!_sliceKeys.Add(sliceKey))
            {
                return;
            }

            _nodesById.TryGetValue(firstSource, out var sourceNode);
            var sliceNodes = nodeIds.Select(nodeId => _nodesById.TryGetValue(nodeId, out var node) ? node : null).Where(node => node is not null).ToList();
            var patternPurls = new[] { sinkPattern?.Purl, sourceNode?.Purl, sinkNode.Purl }.Where(purl => !string.IsNullOrWhiteSpace(purl));
            result.Slices.Add(new DataFlowSlice
            {
                Id = $"dfs{++_sliceCounter}",
                SourceId = firstSource,
                SinkId = sinkNode.Id,
                NodeIds = nodeIds,
                EdgeIds = edgeIds,
                SourceCategory = sourceNode?.Category,
                SinkCategory = sinkPattern?.Category ?? sinkNode.Category,
                SourcePurl = sourceNode?.Purl,
                SinkPurl = sinkNode.Purl,
                Purls = sliceNodes.Select(node => node!.Purl).Concat(patternPurls).Where(purl => !string.IsNullOrWhiteSpace(purl)).Distinct(StringComparer.Ordinal).ToList()!,
                SinkArgument = sinkArgument,
                SinkArgumentIndex = sinkArgumentIndex,
                TaintKinds = trace.TaintKinds.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                FieldPaths = trace.FieldPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                Confidence = sinkPattern?.Confidence ?? "Medium",
                Severity = TransparencyBuilder.SeverityForPattern(sinkPattern?.Category ?? sinkNode.Category, sinkPattern?.Severity, sinkPattern?.Confidence),
                Summary = $"Data flows from {firstSource} to {sinkNode.Name} argument {sinkArgumentIndex}."
            });
        }

        private static string TrimCode(string code)
        {
            code = code.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
            return code.Length <= 240 ? code : code[..240] + "…";
        }

        private static string SafeRelativeSourcePath(string inspectedPath, string sourcePath)
        {
            var root = Directory.Exists(inspectedPath) ? inspectedPath : Path.GetDirectoryName(inspectedPath);
            if (string.IsNullOrWhiteSpace(root)) return Path.GetFileName(sourcePath);
            try
            {
                var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(sourcePath));
                if (string.IsNullOrWhiteSpace(relative) || Path.IsPathFullyQualified(relative) || relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) || relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal)) return Path.GetFileName(sourcePath);
                return relative;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return Path.GetFileName(sourcePath);
            }
        }
    }

    private static string Normalize(string symbolName) => symbolName
        .Replace("global::", string.Empty, StringComparison.Ordinal)
        .Replace("Global.", string.Empty, StringComparison.Ordinal);

    /// <summary>
    ///     Cheap change signal for the summary fixpoint: total number of tracked cells across all
    ///     summaries. Adding a sink/return/taint-kind entry grows the count; rewriting an identical
    ///     summary does not.
    /// </summary>
    private static int SummaryCellCount(DataFlowMethodSummary summary) =>
        2 + summary.ReturnParameterIndexes.Count + summary.SinkParameterIndexes.Count + summary.SinkCategories.Count + summary.TaintKinds.Count + summary.FieldPaths.Count;

    /// <summary>Per-summary cell counts, so a fixpoint round can tell which summaries grew and walk only their callers.</summary>
    private static Dictionary<string, int> SummaryCellCounts(Dictionary<string, DataFlowMethodSummary> summaries) =>
        summaries.ToDictionary(entry => entry.Key, entry => SummaryCellCount(entry.Value), StringComparer.Ordinal);

    /// <summary>
    ///     Static ReDoS detection (W5b): flags catastrophically-backtracking *literal* regex
    ///     patterns at their source locations — a quantified group whose body itself contains a
    ///     quantifier (<c>(a+)*</c>), or a quantified alternation with overlapping branches
    ///     (<c>(a|aa)*</c>). Covers <c>new Regex("literal")</c>, the static
    ///     <c>Regex.IsMatch/Match/Matches(subject, "literal")</c> forms, and
    ///     <c>[GeneratedRegex("literal")]</c>. Real mitigations are honored:
    ///     <c>RegexOptions.NonBacktracking</c> or a match-timeout argument suppress the finding,
    ///     and patterns too long to analyze statically are reported as skipped diagnostics instead
    ///     of being silently ignored. Results are CWE-1333 weakness candidates (severity medium,
    ///     confidence medium — static evidence without reachability), not taint slices.
    /// </summary>
    internal static partial class ReDoSAnalyzer
    {
        public const int MaxAnalyzedPatternLength = 200;

        /// <summary>Matches a group whose body contains an inner quantifier: e.g. (a+)*, ((a)*)+, (a{2,3})*.</summary>
        [GeneratedRegex(@"\([^()]*[+*{][^()]*\)[+*\{]")]
        private static partial Regex NestedQuantifierRegex();

        /// <summary>Matches a quantified alternation whose alternatives share a first character: (a|a), (ab|a).</summary>
        [GeneratedRegex(@"\(([^|()]*)\|([^|()]*)\)[+*\{]")]
        private static partial Regex OverlappingAlternationRegex();

        public static List<WeaknessCandidate> Detect(CSharpCompilation compilation, List<string> diagnostics)
        {
            var candidates = new List<WeaknessCandidate>();
            foreach (var tree in compilation.SyntaxTrees.OfType<CSharpSyntaxTree>())
            {
                var model = compilation.GetSemanticModel(tree);
                var root = tree.GetCompilationUnitRoot();
                var fileName = Path.GetFileName(tree.FilePath);

                foreach (var creation in root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ObjectCreationExpressionSyntax>())
                {
                    if (model.GetOperation(creation) is not IObjectCreationOperation operation ||
                        !IsRegexType(operation.Type))
                    {
                        continue;
                    }

                    // A timeout (TimeSpan) or NonBacktracking option bounds backtracking — honored.
                    if (HasBacktrackingMitigation(operation.Arguments))
                    {
                        continue;
                    }

                    TryAddCandidate(candidates, operation.Arguments.FirstOrDefault()?.Value, $"Regex constructor at {fileName}", SafeSyntaxText.Text(creation), fileName, creation.GetLocation(), diagnostics);
                }

                foreach (var invocation in root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>())
                {
                    if (model.GetOperation(invocation) is not IInvocationOperation { TargetMethod: { } targetMethod } invocationOperation ||
                        targetMethod.Name is not ("IsMatch" or "Match" or "Matches") ||
                        !IsRegexType(targetMethod.ContainingType))
                    {
                        continue;
                    }

                    // The pattern is the second argument of the static Regex.* methods.
                    var arguments = invocationOperation.Arguments.ToList();
                    if (arguments.Count < 2)
                    {
                        continue;
                    }

                    if (HasBacktrackingMitigation(arguments.Skip(2)))
                    {
                        continue;
                    }

                    TryAddCandidate(candidates, arguments[1].Value, $"Regex.{targetMethod.Name} call at {fileName}", SafeSyntaxText.Text(invocation), fileName, invocation.GetLocation(), diagnostics);
                }

                foreach (var attribute in root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.AttributeSyntax>())
                {
                    if (!string.Equals(attribute.Name.ToString(), "GeneratedRegex", StringComparison.Ordinal) &&
                        !attribute.Name.ToString().EndsWith(".GeneratedRegex", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (SafeSyntaxText.Text(attribute).Contains("NonBacktracking", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    // Attributes have no IOperation, so the pattern literal is read from the syntax.
                    if (attribute.ArgumentList?.Arguments.FirstOrDefault()?.Expression is not Microsoft.CodeAnalysis.CSharp.Syntax.LiteralExpressionSyntax { Token.Value: string pattern })
                    {
                        continue;
                    }

                    AddCandidate(candidates, pattern, $"[GeneratedRegex] at {fileName}", SafeSyntaxText.Text(attribute), fileName, attribute.GetLocation(), diagnostics);
                }
            }

            return candidates;
        }

        private static bool IsRegexType(ITypeSymbol? type) =>
            Normalize(type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? string.Empty) == "System.Text.RegularExpressions.Regex";

        /// <summary>TimeSpan arguments are match timeouts; RegexOptions.NonBacktracking disables backtracking.</summary>
        private static bool HasBacktrackingMitigation(IEnumerable<IArgumentOperation> arguments) => arguments.Any(argument =>
            (argument.Value.Type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? string.Empty).Contains("TimeSpan", StringComparison.Ordinal) ||
            SafeSyntaxText.Text(argument.Value.Syntax).Contains("NonBacktracking", StringComparison.Ordinal));

        private static void TryAddCandidate(List<WeaknessCandidate> candidates, IOperation? patternOperation, string usage, string code, string fileName, Location location, List<string> diagnostics)
        {
            if (patternOperation is ILiteralOperation { ConstantValue: { HasValue: true, Value: string pattern } })
            {
                AddCandidate(candidates, pattern, usage, code, fileName, location, diagnostics);
            }
        }

        private static void AddCandidate(List<WeaknessCandidate> candidates, string pattern, string usage, string code, string fileName, Location location, List<string> diagnostics)
        {
            var lineSpan = location.GetLineSpan().StartLinePosition;
            var locationText = $"{fileName}:{lineSpan.Line + 1}:{lineSpan.Character + 1}";
            if (pattern.Length > MaxAnalyzedPatternLength)
            {
                // Not silent: a too-long pattern still gets an actionable note, just no verdict.
                diagnostics.Add($"ReDoS: pattern at {locationText} is {pattern.Length} characters, above the {MaxAnalyzedPatternLength}-character static analysis limit — review it manually for catastrophic backtracking.");
                return;
            }

            if (!IsCatastrophic(pattern))
            {
                return;
            }

            candidates.Add(new WeaknessCandidate
            {
                Id = $"wcr{candidates.Count + 1}",
                Kind = "ReDoSCandidate",
                Cwe = "CWE-1333",
                Confidence = "Medium",
                Severity = "medium",
                ConfidenceReasons = ["Static analysis of a literal pattern; no reachability or input analysis performed."],
                SourceLocation = locationText,
                SinkLocation = locationText,
                SinkCategory = "redos",
                Evidence = [code, $"Catastrophic-backtracking shape in \"{Trim(pattern)}\""],
                Summary = $"Catastrophic backtracking pattern \"{Trim(pattern)}\" in {usage}; consider a linear-time pattern, RegexOptions.NonBacktracking, or an explicit match timeout."
            });
        }

        internal static bool IsCatastrophic(string pattern)
        {
            if (pattern.Length > MaxAnalyzedPatternLength)
            {
                return false;
            }

            foreach (Match match in NestedQuantifierRegex().Matches(pattern))
            {
                // The matched text must be a real group (starts with '(') and the quantifier must sit
                // outside it: "(a+)*" yes, "(a)*(b)" no (regex already requires ) followed by quantifier).
                if (match.Value.StartsWith('('))
                {
                    return true;
                }
            }

            foreach (Match match in OverlappingAlternationRegex().Matches(pattern))
            {
                var left = match.Groups[1].Value;
                var right = match.Groups[2].Value;
                if (left.Length > 0 && right.Length > 0 && left[0] == right[0])
                {
                    return true;
                }
            }

            return false;
        }

        private static string Trim(string value) => value.Length <= 80 ? value : value[..80] + "…";
    }
}

/// <summary>
///     Symbol-anchored lookup for framework taint seeds: entry-point parameters bound to untrusted
///     input (http-route/http-body/rpc-message/queue-message/mcp-tool-arg). Keyed once, O(1) per
///     method so seeding never scans every seed per node.
/// </summary>
public sealed class FrameworkTaintSeedIndex
{
    private readonly Dictionary<string, List<Frameworks.FrameworkTaintSeed>> _byMethod = new(StringComparer.OrdinalIgnoreCase);

    public FrameworkTaintSeedIndex(IEnumerable<Frameworks.FrameworkTaintSeed> seeds)
    {
        foreach (var seed in seeds)
        {
            var key = Key(seed.FileName, seed.MethodName);
            if (!_byMethod.TryGetValue(key, out var list))
            {
                _byMethod[key] = list = [];
            }

            list.Add(seed);
        }
    }

    /// <summary>
    ///     Resolves the seed for one parameter of one method.
    /// </summary>
    /// <remarks>
    ///     The method signature is the discriminator whenever the provider captured one. Matching on
    ///     name plus class alone made overloads indistinguishable, so a <c>[NonAction]</c> or private
    ///     <c>Get(string id, int page)</c> sitting beside a routed <c>Get(string id)</c> inherited the
    ///     routed method's seeds and became a phantom untrusted source that no route can reach. Where a
    ///     signature is unavailable, the previous name-and-class match is kept as a fallback rather
    ///     than dropping the seed.
    /// </remarks>
    public Frameworks.FrameworkTaintSeed? Find(string? fileName, Microsoft.CodeAnalysis.IMethodSymbol method, string parameterName)
    {
        if (fileName is null || !_byMethod.TryGetValue(Key(fileName, method.Name), out var seeds))
        {
            return null;
        }

        var className = method.ContainingType?.Name;
        var signature = Dosai.FormatMethodSignature(method);
        var candidates = seeds.Where(seed => seed.ParameterName.Equals(parameterName, StringComparison.Ordinal)).ToList();
        return candidates.FirstOrDefault(seed => seed.MethodSignature is not null && seed.MethodSignature == signature)
               ?? candidates.FirstOrDefault(seed => seed.MethodSignature is null && (seed.ClassName is null || seed.ClassName == className));
    }

    private static string Key(string? fileName, string methodName) => $"{fileName}|{methodName}";
}
