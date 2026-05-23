using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.VisualBasic;
using CSharpSyntax = Microsoft.CodeAnalysis.CSharp.Syntax;
using VisualBasicSyntax = Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace Depscan;

public enum CryptoOutputFormat
{
    Dosai,
    CycloneDx
}

public sealed class CryptoAnalysisResult
{
    public AnalysisMetadata Metadata { get; set; } = new();
    public DataFlowResult? CryptoDataFlows { get; set; }
    public List<CryptoAsset> Assets { get; set; } = [];
    public List<CryptoOperation> Operations { get; set; } = [];
    public List<CryptoMaterial> Materials { get; set; } = [];
    public List<CryptoProtocol> Protocols { get; set; } = [];
    public List<CryptoFinding> Findings { get; set; } = [];
    public CryptoStatistics Statistics { get; set; } = new();
    public List<string> Diagnostics { get; set; } = [];
}

public sealed class CryptoAsset
{
    public required string Id { get; set; }
    public required string AssetType { get; set; }
    public required string Name { get; set; }
    public string? Family { get; set; }
    public string? Strength { get; set; }
    public string? Standard { get; set; }
    public string? Purl { get; set; }
    public CodeLocation Location { get; set; } = new();
    public bool ReachableFromEntryPoint { get; set; }
    public List<string> EntryPointIds { get; set; } = [];
    public List<string> DataFlowSliceIds { get; set; } = [];
    public Dictionary<string, string> Properties { get; set; } = [];
}

public sealed class CryptoOperation
{
    public required string Id { get; set; }
    public required string OperationType { get; set; }
    public required string Algorithm { get; set; }
    public string? Symbol { get; set; }
    public string? MethodId { get; set; }
    public string? MethodName { get; set; }
    public string? ClassName { get; set; }
    public string? Namespace { get; set; }
    public string? Code { get; set; }
    public CodeLocation Location { get; set; } = new();
    public bool ReachableFromEntryPoint { get; set; }
    public List<string> EntryPointIds { get; set; } = [];
    public List<string> DataFlowSliceIds { get; set; } = [];
    public Dictionary<string, string> Properties { get; set; } = [];
}

public sealed class CryptoMaterial
{
    public required string Id { get; set; }
    public string? Name { get; set; }
    public required string MaterialType { get; set; }
    public string Storage { get; set; } = "unknown";
    public string? Algorithm { get; set; }
    public string? RedactedValue { get; set; }
    public string? Fingerprint { get; set; }
    public string Confidence { get; set; } = "Medium";
    public string? MethodId { get; set; }
    public CodeLocation Location { get; set; } = new();
    public bool ReachableFromEntryPoint { get; set; }
    public List<string> EntryPointIds { get; set; } = [];
    public List<string> DataFlowSliceIds { get; set; } = [];
    public Dictionary<string, string> Properties { get; set; } = [];
}

public sealed class CryptoProtocol
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public string? Version { get; set; }
    public string? Strength { get; set; }
    public string? Symbol { get; set; }
    public string? MethodId { get; set; }
    public CodeLocation Location { get; set; } = new();
    public bool ReachableFromEntryPoint { get; set; }
    public List<string> EntryPointIds { get; set; } = [];
    public Dictionary<string, string> Properties { get; set; } = [];
}

public sealed class CryptoFinding
{
    public required string Id { get; set; }
    public required string RuleId { get; set; }
    public required string Severity { get; set; }
    public required string Confidence { get; set; }
    public required string Summary { get; set; }
    public string? Recommendation { get; set; }
    public string? Cwe { get; set; }
    public string? MethodId { get; set; }
    public List<string> AssetIds { get; set; } = [];
    public List<string> OperationIds { get; set; } = [];
    public List<string> MaterialIds { get; set; } = [];
    public CodeLocation Location { get; set; } = new();
    public bool ReachableFromEntryPoint { get; set; }
    public List<string> EntryPointIds { get; set; } = [];
    public List<string> DataFlowSliceIds { get; set; } = [];
    public List<string> SourceMaterialIds { get; set; } = [];
    public List<string> SinkOperationIds { get; set; } = [];
    public Dictionary<string, string> Properties { get; set; } = [];
}

public sealed class CodeLocation
{
    public string? Path { get; set; }
    public string? FileName { get; set; }
    public int LineNumber { get; set; }
    public int ColumnNumber { get; set; }
}

public sealed class CryptoStatistics
{
    public int FilesAnalyzed { get; set; }
    public int AssetCount { get; set; }
    public int OperationCount { get; set; }
    public int MaterialCount { get; set; }
    public int ProtocolCount { get; set; }
    public int FindingCount { get; set; }
    public int ReachableFindingCount { get; set; }
    public int CryptoDataFlowSliceCount { get; set; }
}

public static class CryptoAnalyzer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly Regex QuotedSecret = new("\"(?<value>(?:[A-Za-z0-9+/]{32,}={0,2}|[A-Fa-f0-9]{32,}|-----BEGIN [^-]+-----[\\s\\S]*?-----END [^-]+-----))\"", RegexOptions.Compiled);
    private static readonly Regex QuotedLiteral = new("\"(?<value>[^\"\\r\\n]{8,})\"", RegexOptions.Compiled);
    private static readonly Regex Identifier = new("\\b[A-Za-z_][A-Za-z0-9_]*\\b", RegexOptions.Compiled);
    private static readonly Regex RFunctionCall = new(@"(?<name>[A-Za-z_][\w\.:]*)\s*\(", RegexOptions.Compiled);
    private static readonly Regex CppFunctionCall = new(@"(?<name>[A-Za-z_][\w:]*)(?:\s*<[^>]+>)?\s*\(", RegexOptions.Compiled);

    public static string GetCryptoAnalysis(string path, string? format = null)
    {
        var result = Analyze(path);
        return Export(result, format);
    }

    public static string Export(CryptoAnalysisResult result, string? format = null) => CryptoBomExporter.Export(result, ParseFormat(format));

    public static CryptoAnalysisResult Analyze(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            throw new FileNotFoundException($"Path does not exist: {path}", path);
        }

        var files = GetSourceFiles(path);
        var result = new CryptoAnalysisResult { Metadata = TransparencyBuilder.CreateMetadata(path) };
        var reachability = BuildReachability(path, result.Diagnostics);
        result.Statistics.FilesAnalyzed = files.Count;

        AnalyzeDotNetSources(path, files, reachability, result);
        AnalyzeTextSources(path, files, reachability, result);
        AttachCryptoDataFlows(path, result);

        result.Assets = result.Assets.OrderBy(a => a.Location.FileName, StringComparer.Ordinal).ThenBy(a => a.Location.LineNumber).ThenBy(a => a.Id, StringComparer.Ordinal).ToList();
        result.Operations = result.Operations.OrderBy(o => o.Location.FileName, StringComparer.Ordinal).ThenBy(o => o.Location.LineNumber).ThenBy(o => o.Id, StringComparer.Ordinal).ToList();
        result.Materials = result.Materials.OrderBy(m => m.Location.FileName, StringComparer.Ordinal).ThenBy(m => m.Location.LineNumber).ThenBy(m => m.Id, StringComparer.Ordinal).ToList();
        result.Protocols = result.Protocols.OrderBy(p => p.Location.FileName, StringComparer.Ordinal).ThenBy(p => p.Location.LineNumber).ThenBy(p => p.Id, StringComparer.Ordinal).ToList();
        result.Findings = result.Findings.OrderBy(f => f.Location.FileName, StringComparer.Ordinal).ThenBy(f => f.Location.LineNumber).ThenBy(f => f.Id, StringComparer.Ordinal).ToList();
        result.Statistics.AssetCount = result.Assets.Count;
        result.Statistics.OperationCount = result.Operations.Count;
        result.Statistics.MaterialCount = result.Materials.Count;
        result.Statistics.ProtocolCount = result.Protocols.Count;
        result.Statistics.FindingCount = result.Findings.Count;
        result.Statistics.ReachableFindingCount = result.Findings.Count(f => f.ReachableFromEntryPoint);
        result.Statistics.CryptoDataFlowSliceCount = result.CryptoDataFlows?.Slices.Count ?? 0;
        return result;
    }

    private static void AttachCryptoDataFlows(string path, CryptoAnalysisResult result)
    {
        try
        {
            var dataFlows = DataFlowAnalyzer.Analyze(path, patternPacks: "crypto");
            result.CryptoDataFlows = dataFlows;
            CryptoDataFlowCorrelator.Attach(result, dataFlows);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or JsonException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            result.Diagnostics.Add($"Crypto data-flow fallback: {ex.Message}");
        }
    }

    private static CryptoOutputFormat ParseFormat(string? format)
    {
        var normalized = (format ?? "dosai").Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        return normalized switch
        {
            "" or "dosai" => CryptoOutputFormat.Dosai,
            "cyclonedx" or "cdx" => CryptoOutputFormat.CycloneDx,
            _ => throw new ArgumentException($"Unsupported crypto output format: {format}. Supported formats: dosai, cyclonedx.")
        };
    }

    private static void AnalyzeDotNetSources(string basePath, List<string> files, CryptoReachability reachability, CryptoAnalysisResult result)
    {
        var references = GetMetadataReferences(basePath, result.Diagnostics);
        var csharpTrees = files.Where(file => Path.GetExtension(file).Equals(Constants.CSharpSourceExtension, StringComparison.OrdinalIgnoreCase))
            .Select(file => (CSharpSyntaxTree)CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file)).ToList();
        var vbTrees = files.Where(file => Path.GetExtension(file).Equals(Constants.VBSourceExtension, StringComparison.OrdinalIgnoreCase))
            .Select(file => (VisualBasicSyntaxTree)VisualBasicSyntaxTree.ParseText(File.ReadAllText(file), path: file)).ToList();

        var csharpCompilation = CSharpCompilation.Create("Dosai.Crypto.CSharp", csharpTrees, references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var vbCompilation = VisualBasicCompilation.Create("Dosai.Crypto.VisualBasic", vbTrees, references, new VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        foreach (var tree in csharpTrees)
        {
            var model = csharpCompilation.GetSemanticModel(tree);
            foreach (var operation in GetCSharpOperationRoots(tree.GetRoot()).Select(node => model.GetOperation(node)).Where(operation => operation is not null))
            {
                new CryptoOperationWalker(model, basePath, tree.FilePath, reachability, result).Visit(operation);
            }
            AnalyzeLineFallback(basePath, tree.FilePath, File.ReadAllLines(tree.FilePath), reachability, result, language: "csharp");
        }

        foreach (var tree in vbTrees)
        {
            var model = vbCompilation.GetSemanticModel(tree);
            foreach (var operation in GetVisualBasicOperationRoots(tree.GetRoot()).Select(node => model.GetOperation(node)).Where(operation => operation is not null))
            {
                new CryptoOperationWalker(model, basePath, tree.FilePath, reachability, result).Visit(operation);
            }
            AnalyzeLineFallback(basePath, tree.FilePath, File.ReadAllLines(tree.FilePath), reachability, result, language: "vb");
        }
    }

    private static IEnumerable<SyntaxNode> GetCSharpOperationRoots(SyntaxNode root)
    {
        foreach (var method in root.DescendantNodes().OfType<CSharpSyntax.BaseMethodDeclarationSyntax>())
        {
            if (method.Body is not null) yield return method.Body;
            if (method.ExpressionBody is not null) yield return method.ExpressionBody;
            if (method is CSharpSyntax.ConstructorDeclarationSyntax { Initializer: not null } constructor) yield return constructor.Initializer;
        }

        foreach (var accessor in root.DescendantNodes().OfType<CSharpSyntax.AccessorDeclarationSyntax>())
        {
            if (accessor.Body is not null) yield return accessor.Body;
            if (accessor.ExpressionBody is not null) yield return accessor.ExpressionBody;
        }

        foreach (var localFunction in root.DescendantNodes().OfType<CSharpSyntax.LocalFunctionStatementSyntax>())
        {
            if (localFunction.Body is not null) yield return localFunction.Body;
            if (localFunction.ExpressionBody is not null) yield return localFunction.ExpressionBody;
        }
    }

    private static IEnumerable<SyntaxNode> GetVisualBasicOperationRoots(SyntaxNode root)
    {
        foreach (var methodBlock in root.DescendantNodes().OfType<VisualBasicSyntax.MethodBlockSyntax>())
        {
            foreach (var statement in methodBlock.Statements)
            {
                yield return statement;
            }
        }

        foreach (var accessorBlock in root.DescendantNodes().OfType<VisualBasicSyntax.AccessorBlockSyntax>())
        {
            foreach (var statement in accessorBlock.Statements)
            {
                yield return statement;
            }
        }
    }

    private static void AnalyzeTextSources(string basePath, List<string> files, CryptoReachability reachability, CryptoAnalysisResult result)
    {
        foreach (var file in files.Where(file => !Path.GetExtension(file).Equals(Constants.CSharpSourceExtension, StringComparison.OrdinalIgnoreCase) && !Path.GetExtension(file).Equals(Constants.VBSourceExtension, StringComparison.OrdinalIgnoreCase)))
        {
            AnalyzeLineFallback(basePath, file, File.ReadAllLines(file), reachability, result, DetectLanguage(file));
        }
    }

    private static void AnalyzeLineFallback(string basePath, string file, string[] lines, CryptoReachability reachability, CryptoAnalysisResult result, string language)
    {
        var currentMethod = string.Empty;
        var currentClass = string.Empty;
        var currentNamespace = string.Empty;
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            UpdateTextContext(line, language, ref currentNamespace, ref currentClass, ref currentMethod);
            var symbolCandidates = IsLikelyDeclarationLine(line, language) ? Enumerable.Empty<string>() : ExtractSymbolCandidates(line, language);
            foreach (var symbol in symbolCandidates)
            {
                RecordCryptoUse(symbol, line, basePath, file, index + 1, Math.Max(1, line.IndexOf(symbol, StringComparison.Ordinal) + 1), currentNamespace, currentClass, currentMethod, reachability, result, source: language);
            }

            DetectLiteralMaterial(line, basePath, file, index + 1, currentNamespace, currentClass, currentMethod, reachability, result);
            DetectLineMisuse(line, basePath, file, index + 1, currentNamespace, currentClass, currentMethod, reachability, result);
        }
    }

    private static void UpdateTextContext(string line, string language, ref string currentNamespace, ref string currentClass, ref string currentMethod)
    {
        var trimmed = line.Trim();
        if (language == "fsharp")
        {
            var moduleMatch = Regex.Match(trimmed, @"^(?:namespace|module)\s+([\w\.]+)");
            if (moduleMatch.Success) currentNamespace = moduleMatch.Groups[1].Value;
            var typeMatch = Regex.Match(trimmed, @"^type\s+(\w+)");
            if (typeMatch.Success) currentClass = typeMatch.Groups[1].Value;
            var fnMatch = Regex.Match(trimmed, @"^(?:let|member)\s+(?:rec\s+)?(?:\w+\.)?(\w+)");
            if (fnMatch.Success) currentMethod = fnMatch.Groups[1].Value;
        }
        else if (language == "r")
        {
            var fnMatch = Regex.Match(trimmed, @"^(\w+)\s*(?:<-|=)\s*function\s*\(");
            if (fnMatch.Success) currentMethod = fnMatch.Groups[1].Value;
        }
        else if (language == "cpp")
        {
            var fnMatch = Regex.Match(trimmed, @"(?:(?<class>\w+)::)?(?<name>\w+)\s*\([^;]*\)\s*(?:const\s*)?\{");
            if (fnMatch.Success)
            {
                currentClass = fnMatch.Groups["class"].Value.Length > 0 ? fnMatch.Groups["class"].Value : currentClass;
                currentMethod = fnMatch.Groups["name"].Value;
            }
        }
    }

    private static IEnumerable<string> ExtractSymbolCandidates(string line, string language)
    {
        var names = new List<string>();
        if (language == "r")
        {
            names.AddRange(RFunctionCall.Matches(line).Select(match => match.Groups["name"].Value));
        }
        else if (language == "cpp")
        {
            names.AddRange(CppFunctionCall.Matches(line).Select(match => match.Groups["name"].Value));
            names.AddRange(Regex.Matches(line, @"\b(EVP_[A-Za-z0-9_]+|AES_[A-Za-z0-9_]+|RSA_[A-Za-z0-9_]+|SSL_[A-Za-z0-9_]+|BCrypt[A-Za-z0-9_]+|NCrypt[A-Za-z0-9_]+|Crypt[A-Za-z0-9_]+)\b").Select(match => match.Value));
        }
        else
        {
            names.AddRange(Regex.Matches(line, @"\b(AesGcm|AesCcm|Aes|DES|TripleDES|RC2|MD5|SHA1|SHA256|SHA384|SHA512|RSA|DSA|ECDsa|ECDiffieHellman|HMACSHA256|HMACSHA384|HMACSHA512|Rfc2898DeriveBytes|RandomNumberGenerator|RNGCryptoServiceProvider|X509Certificate2|SslStream|SslProtocols|System\.Random|Random|CipherMode\.ECB|SecurityAlgorithms\.None)\b").Select(match => match.Value));
        }

        return names.Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsLikelyDeclarationLine(string line, string language)
    {
        var trimmed = line.Trim();
        if (language is "csharp" or "vb" or "text")
        {
            return Regex.IsMatch(trimmed, @"^(?:public|private|protected|internal|static|sealed|virtual|override|async|partial|extern|friend|shared|overrides|overridable|notinheritable|mustoverride|withevents|dim|sub|function)\b.*\([^;=]*\)\s*(?:=>|\{|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        if (language == "fsharp")
        {
            return Regex.IsMatch(trimmed, @"^(?:let|member)\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) && !trimmed.Contains('=');
        }

        if (language == "cpp")
        {
            return Regex.IsMatch(trimmed, @"^[\w:\<\>\*&\s]+\s+\w+\s*\([^;]*\)\s*(?:const\s*)?\{?$", RegexOptions.CultureInvariant) && !trimmed.Contains('=');
        }

        return false;
    }

    private static void RecordCryptoUse(string symbol, string code, string basePath, string file, int line, int column, string? namespaceName, string? className, string? methodName, CryptoReachability reachability, CryptoAnalysisResult result, string source)
    {
        var classification = ClassifyCryptoSymbol(symbol, code, source);
        if (classification is null)
        {
            return;
        }

        var location = CreateLocation(basePath, file, line, column);
        var methodId = ResolveMethodId(reachability, file, namespaceName, className, methodName);
        var entryPointIds = reachability.EntryPointsFor(methodId, file, methodName);
        var reachable = entryPointIds.Count > 0;
        var assetId = AddAsset(result, classification.Algorithm, classification.Family, classification.Strength, classification.Standard, location, reachable, entryPointIds, source);
        AddProtocolIfApplicable(result, classification, symbol, code, location, methodId, reachable, entryPointIds, source);
        var operationId = AddOperation(result, classification, symbol, code, location, methodId, methodName, className, namespaceName, reachable, entryPointIds, source);

        if (classification.FindingRule is not null)
        {
            AddFinding(result, classification.FindingRule, classification.Severity ?? "Medium", classification.Confidence ?? "High", classification.Summary ?? $"{classification.Algorithm} cryptographic usage requires review.", classification.Recommendation, location, methodId, reachable, entryPointIds, [assetId], [operationId], []);
        }
    }

    private static void DetectLineMisuse(string line, string basePath, string file, int lineNumber, string? namespaceName, string? className, string? methodName, CryptoReachability reachability, CryptoAnalysisResult result)
    {
        var checks = new[]
        {
            (Pattern: "ServerCertificateCustomValidationCallback", Rule: "DOSAI-CRYPTO-TLS-CERT-VALIDATION-DISABLED", Severity: "High", Summary: "TLS certificate validation callback is configured and may disable certificate validation.", Recommendation: "Use platform certificate validation or restrict callback logic to explicit certificate pinning."),
            (Pattern: "DangerousAcceptAnyServerCertificateValidator", Rule: "DOSAI-CRYPTO-TLS-CERT-VALIDATION-DISABLED", Severity: "High", Summary: "TLS certificate validation is explicitly disabled.", Recommendation: "Remove dangerous validation bypasses."),
            (Pattern: "CipherMode.ECB", Rule: "DOSAI-CRYPTO-ECB-MODE", Severity: "High", Summary: "ECB cipher mode is used.", Recommendation: "Use an authenticated mode such as AES-GCM, or CBC with random IV and authentication."),
            (Pattern: "SecurityAlgorithms.None", Rule: "DOSAI-CRYPTO-JWT-NONE", Severity: "High", Summary: "JWT 'none' signing algorithm is referenced.", Recommendation: "Require a strong signing algorithm and validate tokens."),
            (Pattern: "SslProtocols.Ssl3", Rule: "DOSAI-CRYPTO-LEGACY-TLS", Severity: "High", Summary: "Legacy SSL/TLS protocol is referenced.", Recommendation: "Require TLS 1.2 or newer."),
            (Pattern: "SslProtocols.Tls11", Rule: "DOSAI-CRYPTO-LEGACY-TLS", Severity: "Medium", Summary: "Legacy TLS 1.1 protocol is referenced.", Recommendation: "Require TLS 1.2 or newer."),
            (Pattern: "SSL_VERIFY_NONE", Rule: "DOSAI-CRYPTO-TLS-CERT-VALIDATION-DISABLED", Severity: "High", Summary: "OpenSSL peer verification is disabled.", Recommendation: "Use SSL_VERIFY_PEER and verify hostnames.")
        };

        foreach (var check in checks.Where(check => line.Contains(check.Pattern, StringComparison.OrdinalIgnoreCase)))
        {
            var location = CreateLocation(basePath, file, lineNumber, Math.Max(1, line.IndexOf(check.Pattern, StringComparison.OrdinalIgnoreCase) + 1));
            var methodId = ResolveMethodId(reachability, file, namespaceName, className, methodName);
            var entryPointIds = reachability.EntryPointsFor(methodId, file, methodName);
            if (check.Rule == "DOSAI-CRYPTO-LEGACY-TLS" || check.Pattern.Contains("SSL", StringComparison.OrdinalIgnoreCase))
            {
                AddProtocol(result, "TLS", InferProtocolVersion(check.Pattern), check.Severity == "High" ? "weak" : "legacy", check.Pattern, location, methodId, entryPointIds.Count > 0, entryPointIds, "line-fallback");
            }
            AddFinding(result, check.Rule, check.Severity, "High", check.Summary, check.Recommendation, location, methodId, entryPointIds.Count > 0, entryPointIds, [], [], []);
        }

        var pbkdf2 = Regex.Match(line, @"Rfc2898DeriveBytes\s*\([^\n;]*(?<iterations>\b\d{1,6}\b)");
        if (pbkdf2.Success && int.TryParse(pbkdf2.Groups["iterations"].Value, out var iterations) && iterations < 100_000)
        {
            var location = CreateLocation(basePath, file, lineNumber, Math.Max(1, pbkdf2.Index + 1));
            var methodId = ResolveMethodId(reachability, file, namespaceName, className, methodName);
            var entryPointIds = reachability.EntryPointsFor(methodId, file, methodName);
            AddFinding(result, "DOSAI-CRYPTO-LOW-PBKDF2-ITERATIONS", "Medium", "Medium", $"PBKDF2 iteration count {iterations} is below the recommended baseline.", "Use at least 100,000 iterations, and prefer current platform guidance.", location, methodId, entryPointIds.Count > 0, entryPointIds, [], [], []);
        }
    }

    private static void DetectLiteralMaterial(string line, string basePath, string file, int lineNumber, string? namespaceName, string? className, string? methodName, CryptoReachability reachability, CryptoAnalysisResult result)
    {
        var materialNameKind = ClassifySensitiveMaterialName(line);
        var hasPem = line.Contains("-----BEGIN", StringComparison.OrdinalIgnoreCase);
        if (materialNameKind is null && !hasPem)
        {
            return;
        }

        var literalMatches = materialNameKind == "iv-or-nonce"
            ? QuotedLiteral.Matches(line).Cast<Match>()
            : QuotedSecret.Matches(line).Cast<Match>();
        foreach (var match in literalMatches)
        {
            var value = match.Groups["value"].Value;
            if (materialNameKind == "iv-or-nonce" && value.Length < 8)
            {
                continue;
            }
            if (materialNameKind != "iv-or-nonce" && value.Length < 32 && !value.Contains("BEGIN", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var materialType = value.Contains("BEGIN", StringComparison.OrdinalIgnoreCase) ? "private-key-or-certificate" : materialNameKind ?? "key-or-secret";
            var materialName = FindSensitiveMaterialIdentifier(line, materialType);
            var location = CreateLocation(basePath, file, lineNumber, Math.Max(1, match.Index + 1));
            var methodId = ResolveMethodId(reachability, file, namespaceName, className, methodName);
            var entryPointIds = reachability.EntryPointsFor(methodId, file, methodName);
            var materialId = $"cma{result.Materials.Count + 1}";
            result.Materials.Add(new CryptoMaterial
            {
                Id = materialId,
                Name = materialName,
                MaterialType = materialType,
                Storage = "hardcoded",
                RedactedValue = Redact(value),
                Fingerprint = Fingerprint(value),
                Confidence = value.Contains("BEGIN", StringComparison.OrdinalIgnoreCase) ? "High" : "Medium",
                MethodId = methodId,
                Location = location,
                ReachableFromEntryPoint = entryPointIds.Count > 0,
                EntryPointIds = entryPointIds,
                Properties = { ["identifier"] = materialName ?? string.Empty }
            });

            var rule = materialType == "iv-or-nonce" ? "DOSAI-CRYPTO-STATIC-IV" : "DOSAI-CRYPTO-HARDCODED-MATERIAL";
            var severity = "High";
            var summary = materialType == "iv-or-nonce" ? "Hardcoded IV/nonce material was detected." : "Hardcoded cryptographic key, token, or certificate-like material was detected.";
            AddFinding(result, rule, severity, "Medium", summary, "Load cryptographic material from a managed secret store or KMS and generate IVs/nonces per operation where required.", location, methodId, entryPointIds.Count > 0, entryPointIds, [], [], [materialId]);
        }
    }

    private static string AddAsset(CryptoAnalysisResult result, string algorithm, string family, string strength, string? standard, CodeLocation location, bool reachable, List<string> entryPointIds, string source)
    {
        var key = $"{algorithm}\u001f{family}\u001f{location.Path}\u001f{location.LineNumber}";
        var existing = result.Assets.FirstOrDefault(asset => asset.Properties.TryGetValue("dedupeKey", out var value) && value == key);
        if (existing is not null)
        {
            return existing.Id;
        }

        var id = $"cas{result.Assets.Count + 1}";
        result.Assets.Add(new CryptoAsset
        {
            Id = id,
            AssetType = "algorithm",
            Name = algorithm,
            Family = family,
            Strength = strength,
            Standard = standard,
            Location = location,
            ReachableFromEntryPoint = reachable,
            EntryPointIds = entryPointIds,
            Properties = { ["source"] = source, ["dedupeKey"] = key }
        });
        return id;
    }

    private static string AddOperation(CryptoAnalysisResult result, CryptoClassification classification, string symbol, string code, CodeLocation location, string? methodId, string? methodName, string? className, string? namespaceName, bool reachable, List<string> entryPointIds, string source)
    {
        var dedupeKey = $"{classification.Algorithm}\u001f{classification.OperationType}\u001f{location.Path}\u001f{location.LineNumber}";
        var existing = result.Operations.FirstOrDefault(operation => operation.Properties.TryGetValue("dedupeKey", out var value) && value == dedupeKey);
        if (existing is not null)
        {
            MergeUnique(existing.EntryPointIds, entryPointIds);
            existing.ReachableFromEntryPoint = existing.ReachableFromEntryPoint || reachable;
            existing.MethodId ??= methodId;
            existing.MethodName ??= methodName;
            existing.ClassName ??= className;
            existing.Namespace ??= namespaceName;
            if (source == "roslyn" && existing.Properties.TryGetValue("source", out var existingSource) && existingSource != "roslyn")
            {
                existing.Symbol = symbol;
                existing.Code = TrimCode(code);
                existing.Properties["source"] = source;
            }
            return existing.Id;
        }

        var operationId = $"cop{result.Operations.Count + 1}";
        result.Operations.Add(new CryptoOperation
        {
            Id = operationId,
            OperationType = classification.OperationType,
            Algorithm = classification.Algorithm,
            Symbol = symbol,
            MethodId = methodId,
            MethodName = methodName,
            ClassName = className,
            Namespace = namespaceName,
            Code = TrimCode(code),
            Location = location,
            ReachableFromEntryPoint = reachable,
            EntryPointIds = entryPointIds,
            Properties = { ["source"] = source, ["dedupeKey"] = dedupeKey }
        });
        return operationId;
    }

    private static void AddFinding(CryptoAnalysisResult result, string ruleId, string severity, string confidence, string summary, string? recommendation, CodeLocation location, string? methodId, bool reachable, List<string> entryPointIds, List<string> assetIds, List<string> operationIds, List<string> materialIds)
    {
        var dedupeKey = $"{ruleId}\u001f{location.Path}\u001f{location.LineNumber}";
        var existing = result.Findings.FirstOrDefault(finding => finding.Properties.TryGetValue("dedupeKey", out var existingKey) && existingKey == dedupeKey);
        if (existing is not null)
        {
            MergeUnique(existing.AssetIds, assetIds);
            MergeUnique(existing.OperationIds, operationIds);
            MergeUnique(existing.MaterialIds, materialIds);
            MergeUnique(existing.EntryPointIds, entryPointIds);
            existing.ReachableFromEntryPoint = existing.ReachableFromEntryPoint || reachable;
            existing.MethodId ??= methodId;
            return;
        }

        result.Findings.Add(new CryptoFinding
        {
            Id = $"cf{result.Findings.Count + 1}",
            RuleId = ruleId,
            Severity = severity,
            Confidence = confidence,
            Summary = summary,
            Recommendation = recommendation,
            Cwe = ruleId.Contains("HARDCODED", StringComparison.OrdinalIgnoreCase) ? "CWE-321" : ruleId.Contains("CERT", StringComparison.OrdinalIgnoreCase) ? "CWE-295" : "CWE-327",
            MethodId = methodId,
            AssetIds = assetIds,
            OperationIds = operationIds,
            MaterialIds = materialIds,
            Location = location,
            ReachableFromEntryPoint = reachable,
            EntryPointIds = entryPointIds,
            Properties = { ["dedupeKey"] = dedupeKey }
        });
    }

    private static void AddProtocolIfApplicable(CryptoAnalysisResult result, CryptoClassification classification, string symbol, string code, CodeLocation location, string? methodId, bool reachable, List<string> entryPointIds, string source)
    {
        if (!classification.Family.Equals("protocol", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        AddProtocol(result, classification.Algorithm, InferProtocolVersion($"{symbol} {code}"), classification.Strength, symbol, location, methodId, reachable, entryPointIds, source);
    }

    private static void AddProtocol(CryptoAnalysisResult result, string name, string? version, string? strength, string symbol, CodeLocation location, string? methodId, bool reachable, List<string> entryPointIds, string source)
    {
        var dedupeKey = $"{name}\u001f{version}\u001f{location.Path}\u001f{location.LineNumber}";
        var existing = result.Protocols.FirstOrDefault(protocol => protocol.Properties.TryGetValue("dedupeKey", out var value) && value == dedupeKey);
        if (existing is not null)
        {
            MergeUnique(existing.EntryPointIds, entryPointIds);
            existing.ReachableFromEntryPoint = existing.ReachableFromEntryPoint || reachable;
            return;
        }

        result.Protocols.Add(new CryptoProtocol
        {
            Id = $"cpr{result.Protocols.Count + 1}",
            Name = name,
            Version = version,
            Strength = strength,
            Symbol = symbol,
            MethodId = methodId,
            Location = location,
            ReachableFromEntryPoint = reachable,
            EntryPointIds = entryPointIds,
            Properties = { ["source"] = source, ["dedupeKey"] = dedupeKey }
        });
    }

    private static string? InferProtocolVersion(string text)
    {
        if (text.Contains("Ssl3", StringComparison.OrdinalIgnoreCase) || text.Contains("SSLv3", StringComparison.OrdinalIgnoreCase)) return "SSL 3.0";
        if (text.Contains("Tls11", StringComparison.OrdinalIgnoreCase) || text.Contains("TLSv1_1", StringComparison.OrdinalIgnoreCase)) return "TLS 1.1";
        if (text.Contains("Tls12", StringComparison.OrdinalIgnoreCase) || text.Contains("TLSv1_2", StringComparison.OrdinalIgnoreCase)) return "TLS 1.2";
        if (text.Contains("Tls13", StringComparison.OrdinalIgnoreCase) || text.Contains("TLSv1_3", StringComparison.OrdinalIgnoreCase)) return "TLS 1.3";
        return null;
    }

    private static string? ClassifySensitiveMaterialName(string line)
    {
        var identifiers = Identifier.Matches(line)
            .Select(match => match.Value)
            .Where(identifier => !IsLanguageKeyword(identifier))
            .ToList();
        if (identifiers.Any(IsIvOrNonceName)) return "iv-or-nonce";
        if (identifiers.Any(IsKeyOrSecretName)) return "key-or-secret";
        return null;
    }

    private static string? FindSensitiveMaterialIdentifier(string line, string materialType)
    {
        var identifiers = Identifier.Matches(line)
            .Select(match => match.Value)
            .Where(identifier => !IsLanguageKeyword(identifier))
            .ToList();
        Func<string, bool> predicate = materialType == "iv-or-nonce" ? IsIvOrNonceName : IsKeyOrSecretName;
        return identifiers.FirstOrDefault(predicate);
    }

    private static bool IsLanguageKeyword(string identifier) => identifier is "private" or "public" or "protected" or "internal" or "static" or "readonly" or "const" or "string" or "byte" or "var" or "new" or "return";

    private static bool IsIvOrNonceName(string identifier)
    {
        var tokens = SplitIdentifier(identifier).ToList();
        return tokens.Contains("iv", StringComparer.OrdinalIgnoreCase) || tokens.Contains("nonce", StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsKeyOrSecretName(string identifier)
    {
        var tokens = SplitIdentifier(identifier).ToList();
        return tokens.Contains("key", StringComparer.OrdinalIgnoreCase) ||
               tokens.Contains("secret", StringComparer.OrdinalIgnoreCase) ||
               tokens.Contains("token", StringComparer.OrdinalIgnoreCase) ||
               tokens.Contains("password", StringComparer.OrdinalIgnoreCase) ||
               identifier.Contains("privatekey", StringComparison.OrdinalIgnoreCase) ||
               identifier.Contains("clientsecret", StringComparison.OrdinalIgnoreCase) ||
               identifier.Contains("jwtsecret", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> SplitIdentifier(string identifier)
    {
        foreach (Match match in Regex.Matches(identifier, @"[A-Z]?[a-z]+|[A-Z]+(?![a-z])|\d+"))
        {
            yield return match.Value.ToLowerInvariant();
        }
    }

    private static void MergeUnique(List<string> target, IEnumerable<string> values)
    {
        foreach (var value in values.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            if (!target.Contains(value, StringComparer.Ordinal))
            {
                target.Add(value);
            }
        }
    }

    private static CryptoClassification? ClassifyCryptoSymbol(string symbol, string code, string source)
    {
        var symbolText = symbol;
        var codeText = code;

        if (HasCryptoToken(symbolText, "MD5", "EVP_md5", "digest::digest") || codeText.Contains("algo = \"md5\"", StringComparison.OrdinalIgnoreCase))
            return Classify("MD5", "hash", "weak", rule: "DOSAI-CRYPTO-WEAK-HASH-MD5", severity: "High", summary: "MD5 hashing was detected.", recommendation: "Use SHA-256 or stronger for integrity, and password-specific hashing for passwords.");
        if (HasCryptoToken(symbolText, "SHA1", "SHA1CryptoServiceProvider", "EVP_sha1") || codeText.Contains("algo = \"sha1\"", StringComparison.OrdinalIgnoreCase))
            return Classify("SHA-1", "hash", "weak", rule: "DOSAI-CRYPTO-WEAK-HASH-SHA1", severity: "Medium", summary: "SHA-1 hashing was detected.", recommendation: "Use SHA-256 or stronger, unless required only for legacy non-security identifiers.");
        if (HasCryptoToken(symbolText, "DES", "TripleDES", "RC2", "EVP_des", "EVP_rc4"))
            return Classify(symbol.Contains("Triple", StringComparison.OrdinalIgnoreCase) ? "3DES" : "DES/RC2/RC4", "symmetric", "weak", rule: "DOSAI-CRYPTO-WEAK-CIPHER", severity: "High", summary: "Weak or legacy symmetric cipher was detected.", recommendation: "Use AES-GCM or another approved authenticated encryption mode.");
        if (HasCryptoToken(symbolText, "AesGcm", "AES_gcm", "EVP_aes_256_gcm")) return Classify("AES-GCM", "symmetric", "strong", "encrypt/decrypt", "NIST");
        if (HasCryptoToken(symbolText, "AesCcm", "AES_ccm")) return Classify("AES-CCM", "symmetric", "strong", "encrypt/decrypt", "NIST");
        if (HasCryptoToken(symbolText, "Aes", "AES_", "EVP_aes")) return Classify("AES", "symmetric", "acceptable", "encrypt/decrypt", "NIST");
        if (HasCryptoToken(symbolText, "RSA", "RSA_")) return Classify("RSA", "asymmetric", "acceptable", "sign/encrypt", "PKCS#1");
        if (HasCryptoToken(symbolText, "ECDsa", "ECDSA")) return Classify("ECDSA", "asymmetric", "strong", "sign", "FIPS 186");
        if (HasCryptoToken(symbolText, "ECDiffieHellman", "ECDH")) return Classify("ECDH", "key-agreement", "strong", "key-agreement");
        if (HasCryptoToken(symbolText, "SHA256", "SHA384", "SHA512", "EVP_sha256", "EVP_sha512")) return Classify(symbol.Contains("512", StringComparison.Ordinal) ? "SHA-512" : "SHA-2", "hash", "strong");
        if (HasCryptoToken(symbolText, "HMACSHA256", "HMACSHA384", "HMACSHA512", "HMAC(")) return Classify("HMAC", "mac", "strong", "mac");
        if (HasCryptoToken(symbolText, "Rfc2898DeriveBytes", "PKCS5_PBKDF2_HMAC")) return Classify("PBKDF2", "kdf", "acceptable", "key-derivation", "PKCS#5");
        if (HasCryptoToken(symbolText, "RandomNumberGenerator", "RNGCryptoServiceProvider", "RAND_bytes")) return Classify("CSPRNG", "random", "strong", "random");
        if (HasCryptoToken(symbolText, "System.Random", "Random", "rand", "srand")) return Classify("Non-cryptographic RNG", "random", "weak", "random", rule: "DOSAI-CRYPTO-INSECURE-RNG", severity: "Medium", summary: "Non-cryptographic random number generator was detected near security analysis context.", recommendation: "Use RandomNumberGenerator for security-sensitive randomness.");
        if (HasCryptoToken(symbolText, "X509Certificate2", "X509_STORE", "CertificateRequest")) return Classify("X.509", "certificate", "unknown", "certificate");
        if (HasCryptoToken(symbolText, "SslStream", "SSL_CTX", "SslProtocols", "TLS")) return Classify("TLS", "protocol", "acceptable", "transport-security", "TLS");
        if (HasCryptoToken(symbolText, "SecurityAlgorithms.None")) return Classify("JWT none", "signature", "weak", "sign", rule: "DOSAI-CRYPTO-JWT-NONE", severity: "High", summary: "JWT 'none' algorithm was detected.", recommendation: "Require strong token signing and validation.");
        if (HasCryptoToken(symbolText, "openssl::", "sodium::", "digest::")) return Classify(symbol, "library", "unknown", "library");
        return null;

        CryptoClassification Classify(string algorithm, string family, string strength, string operationType = "use", string? standard = null, string? rule = null, string? severity = null, string? summary = null, string? recommendation = null) =>
            new(algorithm, family, strength, operationType, standard, rule, severity, "High", summary, recommendation);
    }

    private static bool ContainsAny(string value, params string[] candidates) => candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));

    private static bool HasCryptoToken(string symbol, params string[] candidates)
    {
        var frameworkQualified = symbol.Contains("System.Security.Cryptography", StringComparison.OrdinalIgnoreCase) ||
                                 symbol.Contains("System.Security.Authentication", StringComparison.OrdinalIgnoreCase) ||
                                 symbol.Contains("System.Net.Security", StringComparison.OrdinalIgnoreCase) ||
                                 symbol.Contains("Microsoft.IdentityModel", StringComparison.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            if (symbol.Equals(candidate, StringComparison.OrdinalIgnoreCase) ||
                symbol.StartsWith($"{candidate}.", StringComparison.OrdinalIgnoreCase) ||
                symbol.StartsWith($"{candidate}::", StringComparison.OrdinalIgnoreCase) ||
                symbol.Equals($"System.{candidate}", StringComparison.OrdinalIgnoreCase) ||
                symbol.StartsWith($"System.{candidate}.", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if ((candidate.Contains('_', StringComparison.Ordinal) || candidate.Contains("::", StringComparison.Ordinal)) && symbol.Contains(candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (frameworkQualified && symbol.Contains(candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string? ResolveMethodId(CryptoReachability reachability, string file, string? namespaceName, string? className, string? methodName)
    {
        return reachability.ResolveMethodId(file, namespaceName, className, methodName);
    }

    private static CodeLocation CreateLocation(string basePath, string file, int line, int column) => new()
    {
        Path = SafeRelativePath(basePath, file),
        FileName = Path.GetFileName(file),
        LineNumber = line,
        ColumnNumber = column
    };

    private static string SafeRelativePath(string basePath, string file) => Directory.Exists(basePath) ? Path.GetRelativePath(basePath, file) : Path.GetFileName(file);

    private static string TrimCode(string code)
    {
        code = code.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
        return code.Length <= 240 ? code : code[..240] + "…";
    }

    private static string Redact(string value) => value.Length <= 8 ? "***" : $"{value[..Math.Min(4, value.Length)]}***{value[^Math.Min(4, value.Length)..]}";

    private static string Fingerprint(string value)
    {
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string DetectLanguage(string file)
    {
        var extension = Path.GetExtension(file).ToLowerInvariant();
        if (extension is ".fs" or ".fsi" or ".fsx") return "fsharp";
        if (extension is ".r" or ".rmd" or ".qmd") return "r";
        if (extension is ".cpp" or ".cc" or ".cxx" or ".c" or ".h" or ".hpp" or ".hh") return "cpp";
        return "text";
    }

    private static List<string> GetSourceFiles(string path)
    {
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Constants.CSharpSourceExtension, Constants.VBSourceExtension, Constants.FSharpSourceExtension, ".fsi", ".fsx", ".r", ".rmd", ".qmd", ".cpp", ".cc", ".cxx", ".c", ".h", ".hpp", ".hh"
        };
        if (File.Exists(path))
        {
            return extensions.Contains(Path.GetExtension(path)) ? [path] : [];
        }

        return Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories)
            .Where(file => extensions.Contains(Path.GetExtension(file)))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static List<PortableExecutableReference> GetMetadataReferences(string path, List<string> diagnostics)
    {
        var references = new Dictionary<string, PortableExecutableReference>(StringComparer.OrdinalIgnoreCase);
        void AddReference(string referencePath)
        {
            if (!File.Exists(referencePath) || references.ContainsKey(referencePath)) return;
            try { references.Add(referencePath, MetadataReference.CreateFromFile(referencePath)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException) { diagnostics.Add($"Could not add metadata reference {referencePath}: {ex.Message}"); }
        }

#pragma warning disable IL3000
        AddReference(typeof(object).Assembly.Location);
#pragma warning restore IL3000
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa)
        {
            foreach (var referencePath in tpa.Split(Path.PathSeparator)) AddReference(referencePath);
        }
        var root = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
        {
            foreach (var assembly in Directory.EnumerateFiles(root, "*.dll", SearchOption.AllDirectories)) AddReference(assembly);
        }
        return references.Values.ToList();
    }

    private static CryptoReachability BuildReachability(string path, List<string> diagnostics)
    {
        try
        {
            var methodsJson = Dosai.GetMethods(path);
            var methodsSlice = JsonSerializer.Deserialize<MethodsSlice>(methodsJson, JsonOptions);
            return CryptoReachability.From(methodsSlice);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or JsonException or InvalidOperationException)
        {
            diagnostics.Add($"Crypto reachability fallback: {ex.Message}");
            return CryptoReachability.Empty;
        }
    }

    private sealed class CryptoOperationWalker(SemanticModel model, string basePath, string sourceFilePath, CryptoReachability reachability, CryptoAnalysisResult result) : OperationWalker
    {
        public override void VisitInvocation(IInvocationOperation operation)
        {
            Record(operation, operation.TargetMethod, operation.Syntax.ToString());
            base.VisitInvocation(operation);
        }

        public override void VisitObjectCreation(IObjectCreationOperation operation)
        {
            Record(operation, operation.Constructor, operation.Syntax.ToString());
            base.VisitObjectCreation(operation);
        }

        public override void VisitSimpleAssignment(ISimpleAssignmentOperation operation)
        {
            var code = operation.Syntax.ToString();
            if (code.Contains("CipherMode.ECB", StringComparison.OrdinalIgnoreCase) || code.Contains("ServerCertificateCustomValidationCallback", StringComparison.OrdinalIgnoreCase))
            {
                var method = model.GetEnclosingSymbol(operation.Syntax.SpanStart) as IMethodSymbol;
                DetectLineMisuse(code, basePath, sourceFilePath, operation.Syntax.GetLocation().GetLineSpan().StartLinePosition.Line + 1, method?.ContainingNamespace?.ToDisplayString(), method?.ContainingType?.Name, method?.Name, reachability, result);
            }
            base.VisitSimpleAssignment(operation);
        }

        public override void VisitLiteral(ILiteralOperation operation)
        {
            if (operation.ConstantValue is { HasValue: true, Value: string })
            {
                var method = model.GetEnclosingSymbol(operation.Syntax.SpanStart) as IMethodSymbol;
                var line = operation.Syntax.Parent?.ToString() ?? operation.Syntax.ToString();
                DetectLiteralMaterial(line, basePath, sourceFilePath, operation.Syntax.GetLocation().GetLineSpan().StartLinePosition.Line + 1, method?.ContainingNamespace?.ToDisplayString(), method?.ContainingType?.Name, method?.Name, reachability, result);
            }
            base.VisitLiteral(operation);
        }

        private void Record(IOperation operation, IMethodSymbol? symbol, string code)
        {
            if (symbol is null) return;
            var method = model.GetEnclosingSymbol(operation.Syntax.SpanStart) as IMethodSymbol;
            var linePosition = operation.Syntax.GetLocation().GetLineSpan().StartLinePosition;
            var symbolText = DescribeSymbol(symbol);
            RecordCryptoUse(symbolText, code, basePath, sourceFilePath, linePosition.Line + 1, linePosition.Character + 1, method?.ContainingNamespace?.ToDisplayString(), method?.ContainingType?.Name, method?.Name, reachability, result, "roslyn");
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

    private static class CryptoDataFlowCorrelator
    {
        private static readonly HashSet<string> CryptoSinkCategories = new(StringComparer.OrdinalIgnoreCase) { "crypto", "certificate", "tls", "jwt" };
        private static readonly HashSet<string> CryptoSourceCategories = new(StringComparer.OrdinalIgnoreCase) { "crypto-material", "secret" };
        public static void Attach(CryptoAnalysisResult result, DataFlowResult dataFlows)
        {
            var index = SliceIndex.Create(dataFlows);
            foreach (var material in result.Materials)
            {
                MergeUnique(material.DataFlowSliceIds, index.FindMaterialSliceIds(material));
                if (material.DataFlowSliceIds.Count > 0) material.Properties["dataFlowSliceIds"] = string.Join(",", material.DataFlowSliceIds);
            }
            foreach (var operation in result.Operations)
            {
                MergeUnique(operation.DataFlowSliceIds, index.FindOperationSliceIds(operation));
                if (operation.DataFlowSliceIds.Count > 0) operation.Properties["dataFlowSliceIds"] = string.Join(",", operation.DataFlowSliceIds);
            }
            var materialsById = result.Materials.ToDictionary(material => material.Id, StringComparer.Ordinal);
            var operationsById = result.Operations.ToDictionary(operation => operation.Id, StringComparer.Ordinal);
            var materialIdsBySliceId = BuildEvidenceIdsBySlice(result.Materials, material => material.DataFlowSliceIds, material => material.Id);
            var operationIdsBySliceId = BuildEvidenceIdsBySlice(result.Operations, operation => operation.DataFlowSliceIds, operation => operation.Id);
            var materialIdsByLocation = BuildEvidenceIdsByLocation(result.Materials, material => material.Location, material => material.Id);
            var operationIdsByLocation = BuildEvidenceIdsByLocation(result.Operations, operation => operation.Location, operation => operation.Id);
            foreach (var finding in result.Findings)
            {
                foreach (var materialId in finding.MaterialIds)
                {
                    if (materialsById.TryGetValue(materialId, out var material)) MergeUnique(finding.DataFlowSliceIds, material.DataFlowSliceIds);
                }
                foreach (var operationId in finding.OperationIds)
                {
                    if (operationsById.TryGetValue(operationId, out var operation)) MergeUnique(finding.DataFlowSliceIds, operation.DataFlowSliceIds);
                }
                MergeUnique(finding.DataFlowSliceIds, index.FindLocationSliceIds(finding.Location));
                MergeUnique(finding.SourceMaterialIds, finding.MaterialIds);
                MergeUnique(finding.SinkOperationIds, finding.OperationIds);
                MergeUnique(finding.SourceMaterialIds, Lookup(materialIdsByLocation, finding.Location));
                MergeUnique(finding.SinkOperationIds, Lookup(operationIdsByLocation, finding.Location));
                foreach (var sliceId in finding.DataFlowSliceIds.ToList())
                {
                    MergeUnique(finding.SourceMaterialIds, Lookup(materialIdsBySliceId, sliceId));
                    MergeUnique(finding.SinkOperationIds, Lookup(operationIdsBySliceId, sliceId));
                }
                if (finding.DataFlowSliceIds.Count > 0) finding.Properties["dataFlowSliceIds"] = string.Join(",", finding.DataFlowSliceIds);
                if (finding.SourceMaterialIds.Count > 0) finding.Properties["sourceMaterialIds"] = string.Join(",", finding.SourceMaterialIds);
                if (finding.SinkOperationIds.Count > 0) finding.Properties["sinkOperationIds"] = string.Join(",", finding.SinkOperationIds);
            }
        }
        private static Dictionary<string, List<string>> BuildEvidenceIdsBySlice<T>(IEnumerable<T> evidence, Func<T, IEnumerable<string>> sliceIds, Func<T, string> idSelector)
        {
            var index = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var item in evidence)
            {
                foreach (var sliceId in sliceIds(item).Distinct(StringComparer.Ordinal))
                {
                    Add(index, sliceId, idSelector(item));
                }
            }
            return index;
        }
        private static Dictionary<string, List<string>> BuildEvidenceIdsByLocation<T>(IEnumerable<T> evidence, Func<T, CodeLocation> locationSelector, Func<T, string> idSelector)
        {
            var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in evidence)
            {
                Add(index, LocationKey(locationSelector(item)), idSelector(item));
            }
            return index;
        }
        private static IReadOnlyList<string> Lookup(Dictionary<string, List<string>> index, CodeLocation location) => index.TryGetValue(LocationKey(location), out var values) ? values : [];
        private static IReadOnlyList<string> Lookup(Dictionary<string, List<string>> index, string key) => index.TryGetValue(key, out var values) ? values : [];
        private static void Add(Dictionary<string, List<string>> index, string key, string value)
        {
            if (!index.TryGetValue(key, out var values))
            {
                values = [];
                index[key] = values;
            }
            if (!values.Contains(value, StringComparer.Ordinal)) values.Add(value);
        }
        private static string LocationKey(CodeLocation location) => $"{location.Path ?? location.FileName ?? string.Empty}\u001f{location.FileName ?? string.Empty}\u001f{location.LineNumber}";
        private static bool SameFile(string? fileName, string? path, CodeLocation location)
        {
            if (!string.IsNullOrWhiteSpace(fileName) && !string.IsNullOrWhiteSpace(location.FileName) && string.Equals(fileName, location.FileName, StringComparison.OrdinalIgnoreCase)) return true;
            return !string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(location.Path) && string.Equals(path, location.Path, StringComparison.OrdinalIgnoreCase);
        }
        private sealed class SliceIndex
        {
            private readonly Dictionary<string, DataFlowSlice> _slicesById;
            private readonly Dictionary<string, List<string>> _sliceIdsByLocation;
            private readonly Dictionary<string, List<string>> _sourceSliceIdsByLocation;
            private readonly Dictionary<string, List<string>> _sinkSliceIdsByLocation;
            private readonly Dictionary<string, List<(DataFlowNode Node, string SliceId)>> _cryptoSourceNodesByFile;
            private readonly Dictionary<string, List<(DataFlowNode Node, string SliceId)>> _cryptoSinkNodesByFile;
            private SliceIndex(Dictionary<string, DataFlowSlice> slicesById, Dictionary<string, List<string>> sliceIdsByLocation, Dictionary<string, List<string>> sourceSliceIdsByLocation, Dictionary<string, List<string>> sinkSliceIdsByLocation, Dictionary<string, List<(DataFlowNode Node, string SliceId)>> cryptoSourceNodesByFile, Dictionary<string, List<(DataFlowNode Node, string SliceId)>> cryptoSinkNodesByFile)
            {
                _slicesById = slicesById;
                _sliceIdsByLocation = sliceIdsByLocation;
                _sourceSliceIdsByLocation = sourceSliceIdsByLocation;
                _sinkSliceIdsByLocation = sinkSliceIdsByLocation;
                _cryptoSourceNodesByFile = cryptoSourceNodesByFile;
                _cryptoSinkNodesByFile = cryptoSinkNodesByFile;
            }
            public static SliceIndex Create(DataFlowResult dataFlows)
            {
                var nodesById = dataFlows.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
                var slicesById = dataFlows.Slices.ToDictionary(slice => slice.Id, StringComparer.Ordinal);
                var sliceIdsByLocation = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                var sourceSliceIdsByLocation = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                var sinkSliceIdsByLocation = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                var cryptoSourceNodesByFile = new Dictionary<string, List<(DataFlowNode Node, string SliceId)>>(StringComparer.OrdinalIgnoreCase);
                var cryptoSinkNodesByFile = new Dictionary<string, List<(DataFlowNode Node, string SliceId)>>(StringComparer.OrdinalIgnoreCase);
                foreach (var slice in dataFlows.Slices)
                {
                    var sliceNodes = slice.NodeIds.Select(nodeId => nodesById.TryGetValue(nodeId, out var node) ? node : null).Where(node => node is not null).Cast<DataFlowNode>().ToList();
                    foreach (var node in sliceNodes)
                    {
                        Add(sliceIdsByLocation, NodeLocationKey(node), slice.Id);
                        if (node.IsSource)
                        {
                            Add(sourceSliceIdsByLocation, NodeLocationKey(node), slice.Id);
                            if (CryptoSourceCategories.Contains(node.Category ?? string.Empty)) AddNodeByFile(cryptoSourceNodesByFile, node, slice.Id);
                        }
                        if (node.IsSink)
                        {
                            Add(sinkSliceIdsByLocation, NodeLocationKey(node), slice.Id);
                            if (CryptoSinkCategories.Contains(node.Category ?? string.Empty)) AddNodeByFile(cryptoSinkNodesByFile, node, slice.Id);
                        }
                    }
                }
                return new SliceIndex(slicesById, sliceIdsByLocation, sourceSliceIdsByLocation, sinkSliceIdsByLocation, cryptoSourceNodesByFile, cryptoSinkNodesByFile);
            }
            public IEnumerable<string> FindMaterialSliceIds(CryptoMaterial material)
            {
                foreach (var sliceId in Lookup(_sourceSliceIdsByLocation, LocationKey(material.Location))) yield return sliceId;
                if (string.IsNullOrWhiteSpace(material.Name)) yield break;
                foreach (var (node, sliceId) in LookupNodes(_cryptoSourceNodesByFile, material.Location))
                {
                    if (ContainsIdentifier(node.Name, material.Name) || ContainsIdentifier(node.Symbol, material.Name) || ContainsIdentifier(node.Code, material.Name)) yield return sliceId;
                }
            }
            public IEnumerable<string> FindOperationSliceIds(CryptoOperation operation)
            {
                foreach (var sliceId in Lookup(_sinkSliceIdsByLocation, LocationKey(operation.Location))) yield return sliceId;
                foreach (var (node, sliceId) in LookupNodes(_cryptoSinkNodesByFile, operation.Location))
                {
                    if (!_slicesById.TryGetValue(sliceId, out var slice) || slice.SinkId != node.Id) continue;
                    if (SliceSinkMatchesOperation(node, operation)) yield return sliceId;
                }
            }
            public IEnumerable<string> FindLocationSliceIds(CodeLocation location) => Lookup(_sliceIdsByLocation, LocationKey(location));
            private static bool SliceSinkMatchesOperation(DataFlowNode sink, CryptoOperation operation)
            {
                if (!SameFile(sink.FileName, sink.Path, operation.Location)) return false;
                if (!string.IsNullOrWhiteSpace(operation.MethodName) && ContainsIdentifier(sink.Symbol, operation.MethodName)) return true;
                return Math.Abs(sink.LineNumber - operation.Location.LineNumber) <= 1 &&
                       (string.IsNullOrWhiteSpace(operation.Symbol) ||
                        string.IsNullOrWhiteSpace(sink.Symbol) ||
                        sink.Symbol.Contains(operation.Algorithm, StringComparison.OrdinalIgnoreCase) ||
                        operation.Symbol.Contains(sink.Name, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(sink.MethodName, operation.MethodName, StringComparison.OrdinalIgnoreCase));
            }
            private static void AddNodeByFile(Dictionary<string, List<(DataFlowNode Node, string SliceId)>> index, DataFlowNode node, string sliceId)
            {
                var key = FileKey(node.FileName, node.Path);
                if (!index.TryGetValue(key, out var values))
                {
                    values = [];
                    index[key] = values;
                }
                values.Add((node, sliceId));
            }
            private static IEnumerable<(DataFlowNode Node, string SliceId)> LookupNodes(Dictionary<string, List<(DataFlowNode Node, string SliceId)>> index, CodeLocation location) => index.TryGetValue(FileKey(location.FileName, location.Path), out var values) ? values : [];
            private static string NodeLocationKey(DataFlowNode node) => $"{node.Path ?? node.FileName ?? string.Empty}\u001f{node.FileName ?? string.Empty}\u001f{node.LineNumber}";
            private static string FileKey(string? fileName, string? path) => $"{path ?? string.Empty}\u001f{fileName ?? string.Empty}";
        }
        private static bool ContainsIdentifier(string? value, string identifier) => !string.IsNullOrWhiteSpace(value) && value.Contains(identifier, StringComparison.OrdinalIgnoreCase);
    }
    private sealed record CryptoClassification(string Algorithm, string Family, string Strength, string OperationType, string? Standard, string? FindingRule, string? Severity, string? Confidence, string? Summary, string? Recommendation);

    private sealed class CryptoReachability
    {
        public static CryptoReachability Empty { get; } = new([], [], [], []);

        private readonly Dictionary<string, string> _methodIdsByLooseKey;
        private readonly Dictionary<string, List<string>> _entryPointsByMethodId;
        private readonly Dictionary<string, List<string>> _entryPointsByFile;
        private readonly List<EntryPoint> _entryPoints;

        private CryptoReachability(Dictionary<string, string> methodIdsByLooseKey, Dictionary<string, List<string>> entryPointsByMethodId, Dictionary<string, List<string>> entryPointsByFile, List<EntryPoint> entryPoints)
        {
            _methodIdsByLooseKey = methodIdsByLooseKey;
            _entryPointsByMethodId = entryPointsByMethodId;
            _entryPointsByFile = entryPointsByFile;
            _entryPoints = entryPoints;
        }

        public static CryptoReachability From(MethodsSlice? slice)
        {
            if (slice is null) return Empty;
            var methods = slice.Methods ?? [];
            var callGraph = slice.CallGraph ?? new CallGraph();
            var entryPoints = slice.EntryPoints ?? [];
            var methodIdsByLooseKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var method in methods)
            {
                var id = method.SourceSignature ?? method.AssemblySignature;
                if (string.IsNullOrWhiteSpace(id)) continue;
                AddKey(methodIdsByLooseKey, id, method.FileName, method.Namespace, method.ClassName, method.Name);
                AddKey(methodIdsByLooseKey, id, method.FileName, null, method.ClassName, method.Name);
                AddKey(methodIdsByLooseKey, id, null, method.Namespace, method.ClassName, method.Name);
                AddKey(methodIdsByLooseKey, id, null, null, method.ClassName, method.Name);
                AddKey(methodIdsByLooseKey, id, method.FileName, null, null, method.Name);
            }

            var adjacency = callGraph.Edges.GroupBy(edge => edge.SourceId, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Select(edge => edge.TargetId).Distinct(StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
            var reverseAdjacency = callGraph.Edges.GroupBy(edge => edge.TargetId, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Select(edge => edge.SourceId).Distinct(StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
            var byMethod = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var byFile = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entryPoints)
            {
                var startIds = new HashSet<string>(StringComparer.Ordinal);
                if (!string.IsNullOrWhiteSpace(entry.MethodId)) startIds.Add(entry.MethodId);
                if (methodIdsByLooseKey.TryGetValue(LooseKey(entry.FileName, entry.Namespace, entry.ClassName, entry.MethodName), out var looseId)) startIds.Add(looseId);
                if (methodIdsByLooseKey.TryGetValue(LooseKey(entry.FileName, null, entry.ClassName, entry.MethodName), out var fileClassId)) startIds.Add(fileClassId);
                if (methodIdsByLooseKey.TryGetValue(LooseKey(null, null, entry.ClassName, entry.MethodName), out var classId)) startIds.Add(classId);
                foreach (var method in methods.Where(method => string.Equals(method.FileName, entry.FileName, StringComparison.OrdinalIgnoreCase) && string.Equals(method.Name, entry.MethodName, StringComparison.OrdinalIgnoreCase)))
                {
                    if (!string.IsNullOrWhiteSpace(method.SourceSignature)) startIds.Add(method.SourceSignature!);
                }

                if (!string.IsNullOrWhiteSpace(entry.FileName))
                {
                    AddEntry(byFile, entry.FileName!, entry.Id);
                }

                foreach (var reachable in Walk(startIds, adjacency).Concat(Walk(startIds, reverseAdjacency).Take(64)).Distinct(StringComparer.Ordinal))
                {
                    AddEntry(byMethod, reachable, entry.Id);
                }
            }
            return new CryptoReachability(methodIdsByLooseKey, byMethod, byFile, entryPoints);
        }

        public string? ResolveMethodId(string file, string? namespaceName, string? className, string? methodName)
        {
            var fileName = Path.GetFileName(file);
            foreach (var key in new[]
            {
                LooseKey(fileName, namespaceName, className, methodName),
                LooseKey(fileName, null, className, methodName),
                LooseKey(null, namespaceName, className, methodName),
                LooseKey(null, null, className, methodName),
                LooseKey(fileName, null, null, methodName)
            })
            {
                if (_methodIdsByLooseKey.TryGetValue(key, out var id)) return id;
            }
            return null;
        }

        public List<string> EntryPointsFor(string? methodId, string file, string? methodName)
        {
            if (!string.IsNullOrWhiteSpace(methodId) && _entryPointsByMethodId.TryGetValue(methodId, out var entries)) return entries.ToList();
            var fileName = Path.GetFileName(file);
            var direct = _entryPoints
                .Where(entry => string.Equals(entry.FileName, Path.GetFileName(file), StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(methodName) && string.Equals(entry.MethodName, methodName, StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry.Id)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (direct.Count > 0) return direct;
            return _entryPointsByFile.TryGetValue(fileName, out var fileEntries) ? fileEntries.ToList() : [];
        }

        private static void AddKey(Dictionary<string, string> index, string id, string? fileName, string? namespaceName, string? className, string? methodName)
        {
            index.TryAdd(LooseKey(fileName, namespaceName, className, methodName), id);
        }

        private static void AddEntry(Dictionary<string, List<string>> index, string key, string entryId)
        {
            if (!index.TryGetValue(key, out var entries))
            {
                entries = [];
                index[key] = entries;
            }
            if (!entries.Contains(entryId, StringComparer.Ordinal)) entries.Add(entryId);
        }

        private static IEnumerable<string> Walk(IEnumerable<string> starts, Dictionary<string, List<string>> adjacency)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<string>(starts.Where(start => !string.IsNullOrWhiteSpace(start)).Distinct(StringComparer.Ordinal));
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (!seen.Add(current)) continue;
                yield return current;
                if (!adjacency.TryGetValue(current, out var targets)) continue;
                foreach (var target in targets)
                {
                    if (!seen.Contains(target)) queue.Enqueue(target);
                }
            }
        }

        private static string LooseKey(string? fileName, string? namespaceName, string? className, string? methodName) => $"{fileName ?? string.Empty}\u001f{namespaceName ?? string.Empty}\u001f{className ?? string.Empty}\u001f{methodName ?? string.Empty}";
    }

    private static string Normalize(string symbolName) => symbolName.Replace("global::", string.Empty, StringComparison.Ordinal).Replace("Global.", string.Empty, StringComparison.Ordinal);
}

public static class CryptoBomExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Export(CryptoAnalysisResult result, CryptoOutputFormat format) => format switch
    {
        CryptoOutputFormat.CycloneDx => ExportCycloneDx(result),
        _ => JsonSerializer.Serialize(result, JsonOptions)
    };

    private static string ExportCycloneDx(CryptoAnalysisResult result)
    {
        var components = new List<object>();
        components.AddRange(result.Assets.Select(asset => new
        {
            type = "cryptographic-asset",
            name = asset.Name,
            version = asset.Standard,
            bomRef = $"dosai:crypto:{asset.Id}",
            properties = ToProperties(new Dictionary<string, string?>
            {
                ["dosai:crypto:evidenceType"] = "asset",
                ["dosai:crypto:assetType"] = asset.AssetType,
                ["dosai:crypto:family"] = asset.Family,
                ["dosai:crypto:strength"] = asset.Strength,
                ["dosai:crypto:standard"] = asset.Standard,
                ["dosai:crypto:reachableFromEntryPoint"] = asset.ReachableFromEntryPoint.ToString().ToLowerInvariant(),
                ["dosai:crypto:entryPointIds"] = string.Join(",", asset.EntryPointIds),
                ["dosai:location"] = FormatLocation(asset.Location)
            })
        }));
        components.AddRange(result.Operations.Select(operation => new
        {
            type = "data",
            name = operation.Algorithm,
            bomRef = $"dosai:crypto:operation:{operation.Id}",
            properties = ToProperties(new Dictionary<string, string?>
            {
                ["dosai:crypto:evidenceType"] = "operation",
                ["dosai:crypto:operationType"] = operation.OperationType,
                ["dosai:crypto:algorithm"] = operation.Algorithm,
                ["dosai:crypto:symbol"] = operation.Symbol,
                ["dosai:method:id"] = operation.MethodId,
                ["dosai:method:name"] = operation.MethodName,
                ["dosai:class:name"] = operation.ClassName,
                ["dosai:namespace"] = operation.Namespace,
                ["dosai:crypto:reachableFromEntryPoint"] = operation.ReachableFromEntryPoint.ToString().ToLowerInvariant(),
                ["dosai:crypto:entryPointIds"] = string.Join(",", operation.EntryPointIds),
                ["dosai:crypto:dataFlowSliceIds"] = string.Join(",", operation.DataFlowSliceIds),
                ["dosai:location"] = FormatLocation(operation.Location)
            })
        }));
        components.AddRange(result.Materials.Select(material => new
        {
            type = "data",
            name = material.MaterialType,
            bomRef = $"dosai:crypto:material:{material.Id}",
            properties = ToProperties(new Dictionary<string, string?>
            {
                ["dosai:crypto:evidenceType"] = "material",
                ["dosai:crypto:materialName"] = material.Name,
                ["dosai:crypto:materialType"] = material.MaterialType,
                ["dosai:crypto:storage"] = material.Storage,
                ["dosai:crypto:algorithm"] = material.Algorithm,
                ["dosai:crypto:redactedValue"] = material.RedactedValue,
                ["dosai:crypto:fingerprint"] = material.Fingerprint,
                ["dosai:crypto:confidence"] = material.Confidence,
                ["dosai:method:id"] = material.MethodId,
                ["dosai:crypto:reachableFromEntryPoint"] = material.ReachableFromEntryPoint.ToString().ToLowerInvariant(),
                ["dosai:crypto:entryPointIds"] = string.Join(",", material.EntryPointIds),
                ["dosai:crypto:dataFlowSliceIds"] = string.Join(",", material.DataFlowSliceIds),
                ["dosai:location"] = FormatLocation(material.Location)
            })
        }));
        components.AddRange(result.Protocols.Select(protocol => new
        {
            type = "cryptographic-asset",
            name = protocol.Name,
            version = protocol.Version,
            bomRef = $"dosai:crypto:protocol:{protocol.Id}",
            properties = ToProperties(new Dictionary<string, string?>
            {
                ["dosai:crypto:evidenceType"] = "protocol",
                ["dosai:crypto:protocol"] = protocol.Name,
                ["dosai:crypto:version"] = protocol.Version,
                ["dosai:crypto:strength"] = protocol.Strength,
                ["dosai:crypto:symbol"] = protocol.Symbol,
                ["dosai:method:id"] = protocol.MethodId,
                ["dosai:crypto:reachableFromEntryPoint"] = protocol.ReachableFromEntryPoint.ToString().ToLowerInvariant(),
                ["dosai:crypto:entryPointIds"] = string.Join(",", protocol.EntryPointIds),
                ["dosai:location"] = FormatLocation(protocol.Location)
            })
        }));

        var vulnerabilities = result.Findings.Select(finding => new
        {
            id = finding.RuleId,
            bomRef = $"dosai:crypto:finding:{finding.Id}",
            source = new { name = "Dosai" },
            ratings = new[] { new { severity = finding.Severity.ToLowerInvariant(), method = "other", vector = finding.Confidence } },
            cwes = string.IsNullOrWhiteSpace(finding.Cwe) ? Array.Empty<int>() : ParseCwe(finding.Cwe),
            description = finding.Summary,
            recommendation = finding.Recommendation,
            affects = finding.AssetIds.Select(id => new { @ref = $"dosai:crypto:{id}" }).ToList(),
            properties = ToProperties(new Dictionary<string, string?>
            {
                ["dosai:crypto:reachableFromEntryPoint"] = finding.ReachableFromEntryPoint.ToString().ToLowerInvariant(),
                ["dosai:crypto:entryPointIds"] = string.Join(",", finding.EntryPointIds),
                ["dosai:crypto:assetIds"] = string.Join(",", finding.AssetIds),
                ["dosai:crypto:operationIds"] = string.Join(",", finding.OperationIds),
                ["dosai:crypto:materialIds"] = string.Join(",", finding.MaterialIds),
                ["dosai:crypto:dataFlowSliceIds"] = string.Join(",", finding.DataFlowSliceIds),
                ["dosai:crypto:sourceMaterialIds"] = string.Join(",", finding.SourceMaterialIds),
                ["dosai:crypto:sinkOperationIds"] = string.Join(",", finding.SinkOperationIds),
                ["dosai:method:id"] = finding.MethodId,
                ["dosai:location"] = FormatLocation(finding.Location)
            })
        }).ToList();

        var dependencies = BuildDependencies(result).ToList();

        var bom = new
        {
            bomFormat = "CycloneDX",
            specVersion = "1.6",
            serialNumber = $"urn:uuid:{Guid.NewGuid()}",
            version = 1,
            metadata = new
            {
                timestamp = result.Metadata.GeneratedAt,
                tools = new[] { new { vendor = "OWASP", name = "Dosai", version = result.Metadata.AnalyzerVersion } },
                properties = ToProperties(new Dictionary<string, string?>
                {
                    ["dosai:inputPath"] = result.Metadata.InputPath,
                    ["dosai:crypto:assetCount"] = result.Statistics.AssetCount.ToString(),
                    ["dosai:crypto:operationCount"] = result.Statistics.OperationCount.ToString(),
                    ["dosai:crypto:materialCount"] = result.Statistics.MaterialCount.ToString(),
                    ["dosai:crypto:protocolCount"] = result.Statistics.ProtocolCount.ToString(),
                    ["dosai:crypto:findingCount"] = result.Statistics.FindingCount.ToString(),
                    ["dosai:crypto:dataFlowSliceCount"] = result.Statistics.CryptoDataFlowSliceCount.ToString()
                })
            },
            components,
            dependencies,
            vulnerabilities
        };
        return JsonSerializer.Serialize(bom, JsonOptions);
    }

    private static IEnumerable<object> BuildDependencies(CryptoAnalysisResult result)
    {
        var materialReferencesBySliceId = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var material in result.Materials)
        {
            var materialReference = $"dosai:crypto:material:{material.Id}";
            foreach (var sliceId in material.DataFlowSliceIds.Distinct(StringComparer.Ordinal))
            {
                if (!materialReferencesBySliceId.TryGetValue(sliceId, out var materialReferences))
                {
                    materialReferences = new SortedSet<string>(StringComparer.Ordinal);
                    materialReferencesBySliceId[sliceId] = materialReferences;
                }
                materialReferences.Add(materialReference);
            }
        }

        foreach (var operation in result.Operations)
        {
            var sourceMaterialReferences = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var sliceId in operation.DataFlowSliceIds.Distinct(StringComparer.Ordinal))
            {
                if (!materialReferencesBySliceId.TryGetValue(sliceId, out var materialReferences)) continue;
                sourceMaterialReferences.UnionWith(materialReferences);
            }
            var sourceMaterials = sourceMaterialReferences.ToArray();
            if (sourceMaterials.Length > 0)
            {
                yield return new { @ref = $"dosai:crypto:operation:{operation.Id}", dependsOn = sourceMaterials };
            }
        }
    }

    private static object[] ToProperties(Dictionary<string, string?> values) => values
        .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Value))
        .Select(kvp => new { name = kvp.Key, value = kvp.Value })
        .ToArray();

    private static int[] ParseCwe(string cwe) => int.TryParse(cwe.Replace("CWE-", string.Empty, StringComparison.OrdinalIgnoreCase), out var number) ? [number] : [];

    private static string FormatLocation(CodeLocation location) => $"{location.Path ?? location.FileName}:{location.LineNumber}:{location.ColumnNumber}";
}
