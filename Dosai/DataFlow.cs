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
    Passthrough
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
    public string? Description { get; set; }
}

public sealed class DataFlowPatternSet
{
    public List<DataFlowPattern> Sources { get; set; } = [];
    public List<DataFlowPattern> Sinks { get; set; } = [];
    public List<DataFlowPattern> Passthroughs { get; set; } = [];
}

public sealed class DataFlowNode
{
    public required string Id { get; set; }
    public required string Kind { get; set; }
    public required string Name { get; set; }
    public string? Symbol { get; set; }
    public string? Type { get; set; }
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
    public Dictionary<string, string> Properties { get; set; } = [];
}

public sealed class DataFlowEdge
{
    public required string Id { get; set; }
    public required string SourceId { get; set; }
    public required string TargetId { get; set; }
    public required string Kind { get; set; }
    public string? Label { get; set; }
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
    public string? SinkArgument { get; set; }
    public int? SinkArgumentIndex { get; set; }
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
    public List<DataFlowNode> Nodes { get; set; } = [];
    public List<DataFlowEdge> Edges { get; set; } = [];
    public List<DataFlowSlice> Slices { get; set; } = [];
    public DataFlowPatternSet Patterns { get; set; } = new();
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

    public static string GetDataFlows(string path, string? patternsPath = null)
    {
        var result = Analyze(path, patternsPath);
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    public static DataFlowResult Analyze(string path, string? patternsPath = null)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            throw new FileNotFoundException($"Path does not exist: {path}", path);
        }

        var patterns = LoadPatterns(patternsPath);
        var result = new DataFlowResult { Patterns = patterns };
        var sourcesToInspect = GetSourceFiles(path);
        result.Statistics.FilesAnalyzed = sourcesToInspect.Count;

        var references = GetMetadataReferences(path, result.Diagnostics);
        var csharpTrees = sourcesToInspect
            .Where(source => Path.GetExtension(source).Equals(Constants.CSharpSourceExtension, StringComparison.OrdinalIgnoreCase))
            .Select(source => (CSharpSyntaxTree)CSharpSyntaxTree.ParseText(File.ReadAllText(source), path: source))
            .ToList();
        var vbTrees = sourcesToInspect
            .Where(source => Path.GetExtension(source).Equals(Constants.VBSourceExtension, StringComparison.OrdinalIgnoreCase))
            .Select(source => (VisualBasicSyntaxTree)VisualBasicSyntaxTree.ParseText(File.ReadAllText(source), path: source))
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
            options: new VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var graph = new DataFlowGraphBuilder(result);

        foreach (var tree in csharpTrees)
        {
            var model = csharpCompilation.GetSemanticModel(tree);
            var root = tree.GetCompilationUnitRoot();
            AnalyzeCompilationUnit(model, root, graph, patterns, path, tree.FilePath);
        }

        foreach (var tree in vbTrees)
        {
            var model = vbCompilation.GetSemanticModel(tree);
            var root = tree.GetCompilationUnitRoot();
            AnalyzeCompilationUnit(model, root, graph, patterns, path, tree.FilePath);
        }

        result.Nodes = result.Nodes.OrderBy(n => n.FileName, StringComparer.Ordinal).ThenBy(n => n.LineNumber).ThenBy(n => n.ColumnNumber).ThenBy(n => n.Id, StringComparer.Ordinal).ToList();
        result.Edges = result.Edges.OrderBy(e => e.FileName, StringComparer.Ordinal).ThenBy(e => e.LineNumber).ThenBy(e => e.ColumnNumber).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();
        result.Slices = result.Slices.OrderBy(s => s.Id, StringComparer.Ordinal).ToList();
        result.Statistics.NodeCount = result.Nodes.Count;
        result.Statistics.EdgeCount = result.Edges.Count;
        result.Statistics.SourceCount = result.Nodes.Count(n => n.IsSource);
        result.Statistics.SinkCount = result.Nodes.Count(n => n.IsSink);
        result.Statistics.SliceCount = result.Slices.Count;
        return result;
    }

    private static void AnalyzeCompilationUnit(SemanticModel model, CSharpCompilationUnitSyntax root, DataFlowGraphBuilder graph, DataFlowPatternSet patterns, string basePath, string sourceFilePath)
    {
        var operationNodes = root.DescendantNodes()
            .Where(node => node is Microsoft.CodeAnalysis.CSharp.Syntax.BaseMethodDeclarationSyntax or Microsoft.CodeAnalysis.CSharp.Syntax.AccessorDeclarationSyntax or Microsoft.CodeAnalysis.CSharp.Syntax.LocalFunctionStatementSyntax);
        foreach (var node in operationNodes)
        {
            var operation = model.GetOperation(node);
            if (operation is not null)
            {
                new DataFlowOperationWalker(model, graph, patterns, basePath, sourceFilePath).Visit(operation);
            }
        }
    }

    private static void AnalyzeCompilationUnit(SemanticModel model, VisualBasicCompilationUnitSyntax root, DataFlowGraphBuilder graph, DataFlowPatternSet patterns, string basePath, string sourceFilePath)
    {
        var operationNodes = root.DescendantNodes()
            .Where(node => node is Microsoft.CodeAnalysis.VisualBasic.Syntax.MethodBlockSyntax or Microsoft.CodeAnalysis.VisualBasic.Syntax.AccessorBlockSyntax);
        foreach (var node in operationNodes)
        {
            var operation = model.GetOperation(node);
            if (operation is not null)
            {
                new DataFlowOperationWalker(model, graph, patterns, basePath, sourceFilePath).Visit(operation);
            }
        }
    }

    private static DataFlowPatternSet LoadPatterns(string? patternsPath)
    {
        var defaults = CreateDefaultPatterns();
        if (string.IsNullOrWhiteSpace(patternsPath))
        {
            return defaults;
        }

        var json = File.ReadAllText(patternsPath);
        var userPatterns = JsonSerializer.Deserialize<DataFlowPatternSet>(json, JsonOptions) ?? new DataFlowPatternSet();
        defaults.Sources.AddRange(NormalizeTargets(userPatterns.Sources, DataFlowPatternTarget.Source));
        defaults.Sinks.AddRange(NormalizeTargets(userPatterns.Sinks, DataFlowPatternTarget.Sink));
        defaults.Passthroughs.AddRange(NormalizeTargets(userPatterns.Passthroughs, DataFlowPatternTarget.Passthrough));
        return defaults;
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
            new() { Target = DataFlowPatternTarget.Source, Kind = DataFlowPatternKind.Type, Pattern = "Grpc.Core.ServerCallContext", Match = DataFlowMatchKind.Contains, Category = "rpc", Description = "gRPC server call context" }
        ],
        Sinks =
        [
            new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "System.Diagnostics.Process.Start", Match = DataFlowMatchKind.Contains, Category = "command", Description = "Process execution" },
            new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Type, Pattern = "System.Diagnostics.ProcessStartInfo", Match = DataFlowMatchKind.Contains, Category = "command", Description = "Process execution configuration" },
            new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "System.IO.File.", Match = DataFlowMatchKind.Contains, Category = "file", Description = "File system operation" },
            new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "System.IO.Directory.", Match = DataFlowMatchKind.Contains, Category = "file", Description = "Directory operation" },
            new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "System.IO.Path.Combine", Match = DataFlowMatchKind.Contains, Category = "file", Description = "Path construction" },
            new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "System.Net.Http.HttpClient.", Match = DataFlowMatchKind.Contains, Category = "network", Description = "Outbound HTTP request" },
            new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "GetGrain", Match = DataFlowMatchKind.Contains, Category = "rpc", Description = "Orleans grain dispatch" },
            new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Name, Pattern = "GetGrain", Match = DataFlowMatchKind.Exact, Category = "rpc", Description = "Orleans grain dispatch" },
            new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "System.Data.SqlClient.SqlCommand", Match = DataFlowMatchKind.Contains, Category = "sql", Description = "SQL command" },
            new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "Microsoft.Data.SqlClient.SqlCommand", Match = DataFlowMatchKind.Contains, Category = "sql", Description = "SQL command" },
            new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "ExecuteSqlRaw", Match = DataFlowMatchKind.Contains, Category = "sql", Description = "Entity Framework raw SQL execution" },
            new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "FromSqlRaw", Match = DataFlowMatchKind.Contains, Category = "sql", Description = "Entity Framework raw SQL query" },
            new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "System.Reflection.Assembly.Load", Match = DataFlowMatchKind.Contains, Category = "reflection", Description = "Dynamic assembly loading" },
            new() { Target = DataFlowPatternTarget.Sink, Kind = DataFlowPatternKind.Method, Pattern = "System.Type.GetType", Match = DataFlowMatchKind.Contains, Category = "reflection", Description = "Dynamic type lookup" }
        ],
        Passthroughs =
        [
            new() { Target = DataFlowPatternTarget.Passthrough, Kind = DataFlowPatternKind.Method, Pattern = "System.String.Concat", Match = DataFlowMatchKind.Contains, Category = "string" },
            new() { Target = DataFlowPatternTarget.Passthrough, Kind = DataFlowPatternKind.Method, Pattern = "System.String.Format", Match = DataFlowMatchKind.Contains, Category = "string" },
            new() { Target = DataFlowPatternTarget.Passthrough, Kind = DataFlowPatternKind.Method, Pattern = "ToString", Match = DataFlowMatchKind.Contains, Category = "string" },
            new() { Target = DataFlowPatternTarget.Passthrough, Kind = DataFlowPatternKind.Method, Pattern = "Trim", Match = DataFlowMatchKind.Contains, Category = "string" },
            new() { Target = DataFlowPatternTarget.Passthrough, Kind = DataFlowPatternKind.Method, Pattern = "Replace", Match = DataFlowMatchKind.Contains, Category = "string" }
        ]
    };

    private static List<string> GetSourceFiles(string path)
    {
        var attributes = File.GetAttributes(path);
        if (!attributes.HasFlag(FileAttributes.Directory))
        {
            var extension = Path.GetExtension(path);
            return extension.Equals(Constants.CSharpSourceExtension, StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(Constants.VBSourceExtension, StringComparison.OrdinalIgnoreCase)
                ? [path]
                : [];
        }

        return new DirectoryInfo(path)
            .EnumerateFiles("*.*", SearchOption.AllDirectories)
            .Where(file => file.Extension.Equals(Constants.CSharpSourceExtension, StringComparison.OrdinalIgnoreCase) || file.Extension.Equals(Constants.VBSourceExtension, StringComparison.OrdinalIgnoreCase))
            .Where(file => !file.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(file => !file.FullName.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(file => !file.Name.EndsWith($".g{file.Extension}", StringComparison.OrdinalIgnoreCase))
            .Select(file => file.FullName)
            .ToList();
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
            return peReader.HasMetadata && peReader.PEHeaders.CorHeader is not null;
        }
        catch
        {
            return false;
        }
    }

    private sealed class DataFlowOperationWalker(SemanticModel model, DataFlowGraphBuilder graph, DataFlowPatternSet patterns, string basePath, string sourceFilePath) : OperationWalker
    {
        private readonly Dictionary<string, TaintTrace> _taintedSymbols = new(StringComparer.Ordinal);
        private IMethodSymbol? _currentMethod;

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
            }
            base.VisitVariableDeclarator(operation);
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

        public override void VisitInvocation(IInvocationOperation operation)
        {
            ProcessSink(operation, operation.TargetMethod, operation.Arguments);
            ProcessCodeSink(operation, operation.Arguments);
            base.VisitInvocation(operation);
        }

        public override void VisitObjectCreation(IObjectCreationOperation operation)
        {
            ProcessSink(operation, operation.Constructor, operation.Arguments);
            ProcessCodeSink(operation, operation.Arguments);
            base.VisitObjectCreation(operation);
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
            foreach (var parameter in methodSymbol.Parameters)
            {
                var matched = MatchParameterSource(parameter, methodSymbol).ToList();
                if (matched.Count == 0)
                {
                    continue;
                }

                var node = graph.AddNode("Source", parameter.Name, syntax, model, basePath, sourceFilePath, methodSymbol,
                    isSource: true,
                    isSink: false,
                    matchedPatterns: matched,
                    category: matched.FirstOrDefault()?.Category,
                    symbol: parameter.ToDisplayString(),
                    typeName: Normalize(parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)),
                    code: parameter.Name);
                _taintedSymbols[SymbolKey(parameter)] = new TaintTrace([node.Id]);
            }
        }

        private IEnumerable<DataFlowPattern> MatchParameterSource(IParameterSymbol parameter, IMethodSymbol methodSymbol)
        {
            var parameterText = $"{parameter.Name} {Normalize(parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))}";
            foreach (var pattern in patterns.Sources.Where(p => p.Kind == DataFlowPatternKind.Parameter && (PatternMatches(parameter.Name, p) || PatternMatches(parameterText, p))))
            {
                yield return pattern;
            }

            if (methodSymbol.Name == "Main" && parameter.Name.Equals("args", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var pattern in patterns.Sources.Where(p => p.Kind == DataFlowPatternKind.Parameter && PatternMatches("Main", p)))
                {
                    yield return pattern;
                }
            }

            var methodAttributes = methodSymbol.GetAttributes().Concat(parameter.GetAttributes()).Select(a => a.AttributeClass?.Name ?? string.Empty).ToList();
            foreach (var pattern in patterns.Sources.Where(p => p.Kind == DataFlowPatternKind.Attribute && methodAttributes.Any(attribute => PatternMatches(attribute, p))))
            {
                yield return pattern;
            }

            foreach (var pattern in patterns.Sources.Where(p => p.Kind == DataFlowPatternKind.Type && PatternMatches(Normalize(parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)), p)))
            {
                yield return pattern;
            }
        }

        private void AssignSymbol(ISymbol symbol, IOperation value, SyntaxNode syntax, string edgeKind)
        {
            if (GetTaint(value) is not { } taint)
            {
                return;
            }

            var matchedSinkPatterns = MatchCode(value.Syntax.ToString(), patterns.Sinks).ToList();
            if (matchedSinkPatterns.Count > 0)
            {
                var sinkNode = graph.AddNode("Sink", GetOperationName(value), value, model, basePath, sourceFilePath, _currentMethod,
                    isSource: false,
                    isSink: true,
                    matchedPatterns: matchedSinkPatterns,
                    category: matchedSinkPatterns.FirstOrDefault()?.Category,
                    symbol: GetOperationSymbol(value),
                    typeName: Normalize(value.Type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? string.Empty),
                    code: value.Syntax.ToString());
                graph.AddEdges(taint.NodeIds, sinkNode.Id, "SinkExpression", value.Syntax, sourceFilePath, symbol.Name);
                graph.AddSlice(taint, sinkNode, matchedSinkPatterns.FirstOrDefault(), value.Syntax.ToString(), 0);
            }

            var assignmentNode = graph.AddNode("Assignment", symbol.Name, syntax, model, basePath, sourceFilePath, _currentMethod, isSource: false, isSink: false, matchedPatterns: [], category: null, symbol: symbol.ToDisplayString(), typeName: GetSymbolType(symbol), code: syntax.ToString());
            graph.AddEdges(taint.NodeIds, assignmentNode.Id, edgeKind, syntax, sourceFilePath, symbol.Name);
            _taintedSymbols[SymbolKey(symbol)] = taint.Append(assignmentNode.Id);
        }

        private void AssignTarget(IOperation target, IOperation value, SyntaxNode syntax, string edgeKind)
        {
            var symbol = GetReferencedSymbol(target);
            if (symbol is not null)
            {
                AssignSymbol(symbol, value, syntax, edgeKind);
            }
        }

        private void ProcessSink(IOperation operation, IMethodSymbol? targetMethod, IEnumerable<IArgumentOperation> arguments)
        {
            if (targetMethod is null)
            {
                return;
            }

            var matchedSinkPatterns = MatchSymbol(targetMethod, operation.Syntax, patterns.Sinks).ToList();
            if (matchedSinkPatterns.Count == 0)
            {
                return;
            }

            var argumentList = arguments.ToList();
            for (var index = 0; index < argumentList.Count; index++)
            {
                var argument = argumentList[index];
                if (GetTaint(argument.Value) is not { } taint)
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
                    code: operation.Syntax.ToString());
                graph.AddEdges(taint.NodeIds, sinkNode.Id, "SinkArgument", argument.Syntax, sourceFilePath, argument.Parameter?.Name ?? $"arg{index}");
                graph.AddSlice(taint, sinkNode, matchedSinkPatterns.FirstOrDefault(), argument.Syntax.ToString(), index);
            }
        }

        private void ProcessCodeSink(IOperation operation, IEnumerable<IArgumentOperation> arguments)
        {
            var matchedSinkPatterns = MatchCode(operation.Syntax.ToString(), patterns.Sinks).ToList();
            if (matchedSinkPatterns.Count == 0)
            {
                return;
            }

            var argumentList = arguments.ToList();
            for (var index = 0; index < argumentList.Count; index++)
            {
                var argument = argumentList[index];
                if (GetTaint(argument.Value) is not { } taint)
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
                    code: operation.Syntax.ToString());
                graph.AddEdges(taint.NodeIds, sinkNode.Id, "SinkArgument", argument.Syntax, sourceFilePath, argument.Parameter?.Name ?? $"arg{index}");
                graph.AddSlice(taint, sinkNode, matchedSinkPatterns.FirstOrDefault(), argument.Syntax.ToString(), index);
            }
        }

        private TaintTrace? GetTaint(IOperation? operation)
        {
            if (operation is null)
            {
                return null;
            }

            operation = Strip(operation);
            if (GetReferencedSymbol(operation) is { } existingSymbol && _taintedSymbols.TryGetValue(SymbolKey(existingSymbol), out var existing))
            {
                return existing;
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
                    code: operation.Syntax.ToString());
                return new TaintTrace([sourceNode.Id]);
            }

            if (operation is IInvocationOperation invocation)
            {
                var argTaint = Combine(invocation.Arguments.Select(a => GetTaint(a.Value)));
                if (argTaint is not null && ShouldPropagateThrough(invocation.TargetMethod))
                {
                    var node = graph.AddNode("Call", invocation.TargetMethod.Name, operation, model, basePath, sourceFilePath, _currentMethod, isSource: false, isSink: false, matchedPatterns: [], category: null, symbol: DescribeSymbol(invocation.TargetMethod), typeName: Normalize(invocation.Type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? string.Empty), code: operation.Syntax.ToString());
                    graph.AddEdges(argTaint.NodeIds, node.Id, "CallReturn", operation.Syntax, sourceFilePath, invocation.TargetMethod.Name);
                    return argTaint.Append(node.Id);
                }
                return argTaint;
            }

            if (operation is IObjectCreationOperation objectCreation)
            {
                return Combine(objectCreation.Arguments.Select(a => GetTaint(a.Value)));
            }

            var childTaint = Combine(operation.ChildOperations.Select(GetTaint));
            if (childTaint is not null && CreatesExpressionNode(operation))
            {
                var expressionNode = graph.AddNode("Expression", GetOperationName(operation), operation, model, basePath, sourceFilePath, _currentMethod, isSource: false, isSink: false, matchedPatterns: [], category: null, symbol: GetOperationSymbol(operation), typeName: Normalize(operation.Type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? string.Empty), code: operation.Syntax.ToString());
                graph.AddEdges(childTaint.NodeIds, expressionNode.Id, "Expression", operation.Syntax, sourceFilePath, operation.Kind.ToString());
                return childTaint.Append(expressionNode.Id);
            }

            return childTaint;
        }

        private IEnumerable<DataFlowPattern> MatchOperationSource(IOperation operation)
        {
            if (operation is IParameterReferenceOperation parameterReference)
            {
                foreach (var pattern in MatchParameterSource(parameterReference.Parameter, _currentMethod ?? parameterReference.Parameter.ContainingSymbol as IMethodSymbol ?? parameterReference.Parameter.ContainingSymbol as IMethodSymbol ?? throw new InvalidOperationException("Parameter without containing method")))
                {
                    yield return pattern;
                }
            }

            if (GetReferencedSymbol(operation) is { } symbol)
            {
                foreach (var pattern in MatchSymbol(symbol, operation.Syntax, patterns.Sources))
                {
                    yield return pattern;
                }
            }

            if (operation.Type is not null)
            {
                var typeName = Normalize(operation.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                foreach (var pattern in patterns.Sources.Where(p => p.Kind == DataFlowPatternKind.Type && PatternMatches(typeName, p)))
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
            var matchedPassthrough = patterns.Passthroughs.Any(pattern => PatternMatches(pattern.Kind switch
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

        private IEnumerable<DataFlowPattern> MatchSymbol(ISymbol symbol, SyntaxNode syntax, IEnumerable<DataFlowPattern> candidatePatterns)
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
                DataFlowMatchKind.Regex => Regex.IsMatch(value, pattern.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
                _ => value.Contains(pattern.Pattern, StringComparison.OrdinalIgnoreCase)
            };
        }

        private static TaintTrace? Combine(IEnumerable<TaintTrace?> traces)
        {
            var nodeIds = traces.Where(t => t is not null).SelectMany(t => t!.NodeIds).Distinct(StringComparer.Ordinal).ToList();
            return nodeIds.Count == 0 ? null : new TaintTrace(nodeIds);
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

    private sealed record TaintTrace(List<string> NodeIds)
    {
        public TaintTrace Append(string nodeId) => new(NodeIds.Concat([nodeId]).Distinct(StringComparer.Ordinal).ToList());
    }

    private sealed class DataFlowGraphBuilder(DataFlowResult result)
    {
        private int _nodeCounter;
        private int _edgeCounter;
        private int _sliceCounter;
        private readonly HashSet<string> _edgeKeys = new(StringComparer.Ordinal);

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
                Category = category
            };
            if (method is not null)
            {
                node.Properties["method"] = Normalize(method.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
            }
            result.Nodes.Add(node);
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
                result.Edges.Add(new DataFlowEdge
                {
                    Id = $"dfe{++_edgeCounter}",
                    SourceId = sourceId,
                    TargetId = targetId,
                    Kind = kind,
                    Label = label,
                    FileName = Path.GetFileName(sourceFilePath),
                    LineNumber = lineSpan.StartLinePosition.Line + 1,
                    ColumnNumber = lineSpan.StartLinePosition.Character + 1
                });
            }
        }

        public void AddSlice(TaintTrace trace, DataFlowNode sinkNode, DataFlowPattern? sinkPattern, string? sinkArgument, int sinkArgumentIndex)
        {
            var nodeIds = trace.NodeIds.Concat([sinkNode.Id]).Distinct(StringComparer.Ordinal).ToList();
            var edgeIds = result.Edges
                .Where(edge => nodeIds.Contains(edge.SourceId, StringComparer.Ordinal) && nodeIds.Contains(edge.TargetId, StringComparer.Ordinal))
                .Select(edge => edge.Id)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var firstSource = trace.NodeIds.FirstOrDefault(id => result.Nodes.FirstOrDefault(node => node.Id == id)?.IsSource == true) ?? trace.NodeIds.First();
            result.Slices.Add(new DataFlowSlice
            {
                Id = $"dfs{++_sliceCounter}",
                SourceId = firstSource,
                SinkId = sinkNode.Id,
                NodeIds = nodeIds,
                EdgeIds = edgeIds,
                SourceCategory = result.Nodes.FirstOrDefault(node => node.Id == firstSource)?.Category,
                SinkCategory = sinkPattern?.Category ?? sinkNode.Category,
                SinkArgument = sinkArgument,
                SinkArgumentIndex = sinkArgumentIndex,
                Summary = $"Data flows from {firstSource} to {sinkNode.Name} argument {sinkArgumentIndex}."
            });
        }

        private static string TrimCode(string code)
        {
            code = code.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
            return code.Length <= 240 ? code : code[..240] + "…";
        }
    }

    private static string Normalize(string symbolName) => symbolName
        .Replace("global::", string.Empty, StringComparison.Ordinal)
        .Replace("Global.", string.Empty, StringComparison.Ordinal);
}
