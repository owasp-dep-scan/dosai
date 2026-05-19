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
    {
        var result = Analyze(path, patternsPath, patternPacks);
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    public static DataFlowResult Analyze(string path, string? patternsPath = null, string? patternPacks = null)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            throw new FileNotFoundException($"Path does not exist: {path}", path);
        }

        var patterns = LoadPatterns(patternsPath, patternPacks);
        var result = new DataFlowResult { Patterns = patterns, Metadata = TransparencyBuilder.CreateMetadata(path) };
        var purlResolver = PackageUrlResolver.Create(path);
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

        var summaries = new Dictionary<string, DataFlowMethodSummary>(StringComparer.Ordinal);

        foreach (var tree in csharpTrees)
        {
            var model = csharpCompilation.GetSemanticModel(tree);
            var root = tree.GetCompilationUnitRoot();
            CollectCompilationUnitSummaries(model, root, summaries, patterns);
        }

        foreach (var tree in vbTrees)
        {
            var model = vbCompilation.GetSemanticModel(tree);
            var root = tree.GetCompilationUnitRoot();
            CollectCompilationUnitSummaries(model, root, summaries, patterns);
        }

        var graph = new DataFlowGraphBuilder(result, purlResolver);

        foreach (var tree in csharpTrees)
        {
            var model = csharpCompilation.GetSemanticModel(tree);
            var root = tree.GetCompilationUnitRoot();
            AnalyzeCompilationUnit(model, root, graph, patterns, summaries, path, tree.FilePath);
        }

        foreach (var tree in vbTrees)
        {
            var model = vbCompilation.GetSemanticModel(tree);
            var root = tree.GetCompilationUnitRoot();
            AnalyzeCompilationUnit(model, root, graph, patterns, summaries, path, tree.FilePath);
        }

        AnalyzeLanguageFrontendDataFlows(path, sourcesToInspect, patterns, result);

        result.Nodes = result.Nodes.OrderBy(n => n.FileName, StringComparer.Ordinal).ThenBy(n => n.LineNumber).ThenBy(n => n.ColumnNumber).ThenBy(n => n.Id, StringComparer.Ordinal).ToList();
        result.Edges = result.Edges.OrderBy(e => e.FileName, StringComparer.Ordinal).ThenBy(e => e.LineNumber).ThenBy(e => e.ColumnNumber).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();
        result.Slices = result.Slices.OrderBy(s => s.Id, StringComparer.Ordinal).ToList();
        result.MethodSummaries = summaries.Values.OrderBy(summary => summary.Method, StringComparer.Ordinal).ToList();
        result.Statistics.NodeCount = result.Nodes.Count;
        result.Statistics.EdgeCount = result.Edges.Count;
        result.Statistics.SourceCount = result.Nodes.Count(n => n.IsSource);
        result.Statistics.SinkCount = result.Nodes.Count(n => n.IsSink);
        result.Statistics.SliceCount = result.Slices.Count;
        result.EntryPoints = TransparencyBuilder.BuildEntryPoints(ApiEndpointAnalyzer.GetApiEndpoints(path));
        AddDataFlowEntryPoints(result);
        result.PackageReachability = TransparencyBuilder.BuildPackageReachability(result);
        result.DangerousApiReachability = TransparencyBuilder.BuildDangerousApiReachability(result);
        result.WeaknessCandidates = TransparencyBuilder.BuildWeaknessCandidates(result, result.EntryPoints);
        return result;
    }

    private static void AddDataFlowEntryPoints(DataFlowResult result)
    {
        var next = result.EntryPoints.Count;
        foreach (var source in result.Nodes.Where(node => node.IsSource && node.Category == "cli" && node.MethodName == "Main"))
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

    private static void CollectCompilationUnitSummaries(SemanticModel model, CSharpCompilationUnitSyntax root, Dictionary<string, DataFlowMethodSummary> summaries, DataFlowPatternSet patterns)
    {
        var operationNodes = root.DescendantNodes()
            .Where(node => node is Microsoft.CodeAnalysis.CSharp.Syntax.BaseMethodDeclarationSyntax or Microsoft.CodeAnalysis.CSharp.Syntax.AccessorDeclarationSyntax or Microsoft.CodeAnalysis.CSharp.Syntax.LocalFunctionStatementSyntax);
        foreach (var node in operationNodes)
        {
            var operation = model.GetOperation(node);
            if (operation is not null)
            {
                new DataFlowSummaryCollector(model, summaries, patterns).Visit(operation);
            }
        }
    }

    private static void CollectCompilationUnitSummaries(SemanticModel model, VisualBasicCompilationUnitSyntax root, Dictionary<string, DataFlowMethodSummary> summaries, DataFlowPatternSet patterns)
    {
        var operationNodes = root.DescendantNodes()
            .Where(node => node is Microsoft.CodeAnalysis.VisualBasic.Syntax.MethodBlockSyntax or Microsoft.CodeAnalysis.VisualBasic.Syntax.AccessorBlockSyntax);
        foreach (var node in operationNodes)
        {
            var operation = model.GetOperation(node);
            if (operation is not null)
            {
                new DataFlowSummaryCollector(model, summaries, patterns).Visit(operation);
            }
        }
    }

    private static void AnalyzeCompilationUnit(SemanticModel model, CSharpCompilationUnitSyntax root, DataFlowGraphBuilder graph, DataFlowPatternSet patterns, Dictionary<string, DataFlowMethodSummary> summaries, string basePath, string sourceFilePath)
    {
        var operationNodes = root.DescendantNodes()
            .Where(node => node is Microsoft.CodeAnalysis.CSharp.Syntax.BaseMethodDeclarationSyntax or Microsoft.CodeAnalysis.CSharp.Syntax.AccessorDeclarationSyntax or Microsoft.CodeAnalysis.CSharp.Syntax.LocalFunctionStatementSyntax);
        foreach (var node in operationNodes)
        {
            var operation = model.GetOperation(node);
            if (operation is not null)
            {
                new DataFlowOperationWalker(model, graph, patterns, summaries, basePath, sourceFilePath).Visit(operation);
            }
        }
    }

    private static void AnalyzeCompilationUnit(SemanticModel model, VisualBasicCompilationUnitSyntax root, DataFlowGraphBuilder graph, DataFlowPatternSet patterns, Dictionary<string, DataFlowMethodSummary> summaries, string basePath, string sourceFilePath)
    {
        var operationNodes = root.DescendantNodes()
            .Where(node => node is Microsoft.CodeAnalysis.VisualBasic.Syntax.MethodBlockSyntax or Microsoft.CodeAnalysis.VisualBasic.Syntax.AccessorBlockSyntax);
        foreach (var node in operationNodes)
        {
            var operation = model.GetOperation(node);
            if (operation is not null)
            {
                new DataFlowOperationWalker(model, graph, patterns, summaries, basePath, sourceFilePath).Visit(operation);
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

    private static void ApplyPatternPacks(DataFlowPatternSet patterns, string? patternPacks)
    {
        var requested = (patternPacks ?? "all")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(pack => pack.ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (requested.Count == 0 || requested.Contains("all"))
        {
            requested = ["aspnet", "data", "filesystem", "serialization", "cloud", "rpc", "auth", "crypto"];
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
                new() { Target = DataFlowPatternTarget.Sanitizer, Kind = DataFlowPatternKind.Name, Pattern = "Add", Match = DataFlowMatchKind.Exact, Category = "sql-parameterization", Description = "Parameterized SQL binding" }
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
                new() { Target = DataFlowPatternTarget.Source, Kind = DataFlowPatternKind.Name, Pattern = "key", Match = DataFlowMatchKind.Contains, Category = "crypto-material", Description = "Key-like value", TaintKinds = ["secret", "crypto-key"] },
                new() { Target = DataFlowPatternTarget.Source, Kind = DataFlowPatternKind.Name, Pattern = "secret", Match = DataFlowMatchKind.Contains, Category = "secret", Description = "Secret-like value", TaintKinds = ["secret"] }
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
            var lines = File.ReadAllLines(file);
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
            Code = code.Trim().Length <= 240 ? code.Trim() : code.Trim()[..240] + "…"
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
            DataFlowMatchKind.Regex => Regex.IsMatch(value, pattern.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
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
            return peReader.HasMetadata && peReader.PEHeaders.CorHeader is not null;
        }
        catch
        {
            return false;
        }
    }

    private sealed class DataFlowSummaryCollector(SemanticModel model, Dictionary<string, DataFlowMethodSummary> summaries, DataFlowPatternSet patterns) : OperationWalker
    {
        private IMethodSymbol? _currentMethod;

        public override void VisitMethodBodyOperation(IMethodBodyOperation operation)
        {
            var previousMethod = _currentMethod;
            _currentMethod = model.GetEnclosingSymbol(operation.Syntax.SpanStart) as IMethodSymbol;
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
            base.VisitInvocation(operation);
        }

        public override void VisitObjectCreation(IObjectCreationOperation operation)
        {
            RecordSinkSummary(operation, operation.Constructor, operation.Arguments);
            base.VisitObjectCreation(operation);
        }

        private void RecordSinkSummary(IOperation operation, IMethodSymbol? targetMethod, IEnumerable<IArgumentOperation> arguments)
        {
            if (_currentMethod is null || targetMethod is null)
            {
                return;
            }

            var sinkPatterns = MatchSymbol(targetMethod, operation.Syntax, patterns.Sinks).Concat(MatchCode(operation.Syntax.ToString(), patterns.Sinks)).ToList();
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
                }
            }
        }

        private DataFlowMethodSummary GetSummary(IMethodSymbol method)
        {
            var key = DescribeSymbol(method);
            if (!summaries.TryGetValue(key, out var summary))
            {
                summary = new DataFlowMethodSummary { Method = key };
                summaries[key] = summary;
            }
            return summary;
        }

        private static IEnumerable<int> FindParameterIndexes(IOperation operation, IMethodSymbol method)
        {
            operation = Strip(operation);
            if (operation is IParameterReferenceOperation parameterReference)
            {
                var index = method.Parameters.IndexOf(parameterReference.Parameter);
                if (index >= 0)
                {
                    yield return index;
                }
            }

            foreach (var child in operation.ChildOperations)
            {
                foreach (var index in FindParameterIndexes(child, method))
                {
                    yield return index;
                }
            }
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
                DataFlowMatchKind.Regex => Regex.IsMatch(value, pattern.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
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

    private sealed class DataFlowOperationWalker(SemanticModel model, DataFlowGraphBuilder graph, DataFlowPatternSet patterns, Dictionary<string, DataFlowMethodSummary> summaries, string basePath, string sourceFilePath) : OperationWalker
    {
        private readonly Dictionary<string, TaintTrace> _taintedSymbols = new(StringComparer.Ordinal);
        private readonly DataFlowPatternIndex _patternIndex = new(patterns);
        private readonly Dictionary<SyntaxNode, string> _syntaxTextCache = new();
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

        public override void VisitConditional(IConditionalOperation operation)
        {
            var guardedKeys = GetSanitizedGuardKeys(operation.Condition).ToList();
            Visit(operation.Condition);
            if (guardedKeys.Count == 0 || operation.WhenTrue is null)
            {
                Visit(operation.WhenTrue);
            }
            else
            {
                VisitWithSuppressedTaint(guardedKeys, operation.WhenTrue);
            }
            Visit(operation.WhenFalse);
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
                _taintedSymbols[SymbolKey(parameter)] = new TaintTrace([node.Id], matched.SelectMany(pattern => pattern.TaintKinds.Count > 0 ? pattern.TaintKinds : [pattern.Category ?? "user-input"]).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), []);
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

            if (method.OriginalDefinition is not null && summaries.TryGetValue(DescribeSymbol(method.OriginalDefinition), out summary!))
            {
                return true;
            }

            summary = null!;
            return false;
        }

        private bool IsSanitized(IOperation operation)
        {
            if (operation is IInvocationOperation invocation && MatchSymbol(invocation.TargetMethod, operation.Syntax, _patternIndex.Sanitizers).Any())
            {
                return true;
            }

            if (operation is IObjectCreationOperation objectCreation && objectCreation.Constructor is not null && MatchSymbol(objectCreation.Constructor, operation.Syntax, _patternIndex.Sanitizers).Any())
            {
                return true;
            }

            return _patternIndex.SanitizerCodeLike.Count > 0 && MatchCode(SyntaxText(operation.Syntax), _patternIndex.SanitizerCodeLike).Any();
        }

        private IEnumerable<string> GetSanitizedGuardKeys(IOperation condition)
        {
            condition = Strip(condition);
            if (condition is IUnaryOperation { OperatorKind: UnaryOperatorKind.Not } negated)
            {
                condition = Strip(negated.Operand);
            }

            if (condition is IInvocationOperation invocation && MatchSymbol(invocation.TargetMethod, invocation.Syntax, _patternIndex.Sanitizers).Any())
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
                foreach (var key in GetSanitizedGuardKeys(child))
                {
                    yield return key;
                }
            }
        }

        private void VisitWithSuppressedTaint(IEnumerable<string> keys, IOperation operation)
        {
            var saved = new Dictionary<string, TaintTrace?>(StringComparer.Ordinal);
            foreach (var key in keys.Distinct(StringComparer.Ordinal))
            {
                saved[key] = _taintedSymbols.TryGetValue(key, out var trace) ? trace : null;
                _taintedSymbols.Remove(key);
            }

            Visit(operation);

            foreach (var (key, trace) in saved)
            {
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

            var argumentList = arguments.ToList();
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
            if (operation is null)
            {
                return null;
            }

            operation = Strip(operation);
            if (IsSanitized(operation))
            {
                return null;
            }

            if (TaintKey(operation) is { } operationKey && _taintedSymbols.TryGetValue(operationKey, out var operationTaint))
            {
                return operationTaint;
            }

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
                if (argTaint is not null && TryGetSummary(invocation.TargetMethod, out var invocationSummary))
                {
                    var matchingIndexes = invocationSummary.ReturnParameterIndexes.Where(index => index >= 0 && index < argumentTaints.Count && argumentTaints[index] is not null).ToList();
                    if (matchingIndexes.Count > 0)
                    {
                        var node = graph.AddNode("CallSummary", invocation.TargetMethod.Name, operation, model, basePath, sourceFilePath, _currentMethod, isSource: false, isSink: false, matchedPatterns: [], category: null, symbol: DescribeSymbol(invocation.TargetMethod), typeName: Normalize(invocation.Type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? string.Empty), code: SyntaxText(operation.Syntax));
                        graph.AddEdges(argTaint.NodeIds, node.Id, "InterproceduralReturn", operation.Syntax, sourceFilePath, string.Join(",", matchingIndexes));
                        return argTaint.Append(node.Id);
                    }
                }
                if (argTaint is not null && ShouldPropagateThrough(invocation.TargetMethod))
                {
                    var node = graph.AddNode("Call", invocation.TargetMethod.Name, operation, model, basePath, sourceFilePath, _currentMethod, isSource: false, isSink: false, matchedPatterns: [], category: null, symbol: DescribeSymbol(invocation.TargetMethod), typeName: Normalize(invocation.Type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? string.Empty), code: SyntaxText(operation.Syntax));
                    graph.AddEdges(argTaint.NodeIds, node.Id, "CallReturn", operation.Syntax, sourceFilePath, invocation.TargetMethod.Name);
                    return argTaint.Append(node.Id);
                }
                return argTaint;
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
                foreach (var pattern in MatchParameterSource(parameterReference.Parameter, _currentMethod ?? parameterReference.Parameter.ContainingSymbol as IMethodSymbol ?? throw new InvalidOperationException("Parameter without containing method")))
                {
                    yield return pattern;
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
                DataFlowMatchKind.Regex => Regex.IsMatch(value, pattern.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
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

            text = syntax.ToString();
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
            Sources = patterns.Sources;
            Sinks = patterns.Sinks;
            Passthroughs = patterns.Passthroughs;
            Sanitizers = patterns.Sanitizers;
            SourceParameters = Sources.Where(pattern => pattern.Kind == DataFlowPatternKind.Parameter).ToArray();
            SourceAttributes = Sources.Where(pattern => pattern.Kind == DataFlowPatternKind.Attribute).ToArray();
            SourceTypes = Sources.Where(pattern => pattern.Kind == DataFlowPatternKind.Type).ToArray();
            SourceCode = Sources.Where(pattern => pattern.Kind == DataFlowPatternKind.Code).ToArray();
            SinkCodeLike = Sinks.Where(IsCodeLike).ToArray();
            SanitizerCodeLike = Sanitizers.Where(IsCodeLike).ToArray();
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

        private static bool IsCodeLike(DataFlowPattern pattern) => pattern.Kind is DataFlowPatternKind.Code or DataFlowPatternKind.Method or DataFlowPatternKind.Symbol or DataFlowPatternKind.Name;
    }

    private sealed class DataFlowGraphBuilder(DataFlowResult result, PackageUrlResolver purlResolver)
    {
        private int _nodeCounter;
        private int _edgeCounter;
        private int _sliceCounter;
        private readonly HashSet<string> _edgeKeys = new(StringComparer.Ordinal);
        private readonly Dictionary<string, DataFlowNode> _nodesById = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<DataFlowEdge>> _outgoingEdgesBySource = new(StringComparer.Ordinal);

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
                Category = category
            };
            if (method is not null)
            {
                node.Properties["method"] = Normalize(method.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
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
