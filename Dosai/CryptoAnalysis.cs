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
    public Dictionary<string, string> Properties { get; set; } = [];
}

public sealed class CryptoMaterial
{
    public required string Id { get; set; }
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
    private static readonly Regex AssignmentName = new("(?<name>key|secret|token|password|privateKey|clientSecret|jwtSecret|iv|nonce)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RFunctionCall = new(@"(?<name>[A-Za-z_][\w\.:]*)\s*\(", RegexOptions.Compiled);
    private static readonly Regex CppFunctionCall = new(@"(?<name>[A-Za-z_][\w:]*)(?:\s*<[^>]+>)?\s*\(", RegexOptions.Compiled);

    public static string GetCryptoAnalysis(string path, string? format = null)
    {
        var result = Analyze(path);
        return CryptoBomExporter.Export(result, ParseFormat(format));
    }

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
        return result;
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
            var symbolCandidates = ExtractSymbolCandidates(line, language);
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
            names.AddRange(Regex.Matches(line, @"\b([A-Za-z_][\w\.]*)(?:<[^>]+>)?\s*\(").Select(match => match.Groups[1].Value));
            names.AddRange(Regex.Matches(line, @"\b(AesGcm|AesCcm|Aes|DES|TripleDES|RC2|MD5|SHA1|SHA256|SHA384|SHA512|RSA|DSA|ECDsa|ECDiffieHellman|HMACSHA256|HMACSHA512|Rfc2898DeriveBytes|RandomNumberGenerator|RNGCryptoServiceProvider|X509Certificate2|SslStream|CipherMode\.ECB|SecurityAlgorithms\.None)\b").Select(match => match.Value));
        }

        return names.Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase);
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
            Properties = { ["source"] = source }
        });

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
        if (!AssignmentName.IsMatch(line) && !line.Contains("-----BEGIN", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        foreach (Match match in QuotedSecret.Matches(line))
        {
            var value = match.Groups["value"].Value;
            if (value.Length < 32 && !value.Contains("BEGIN", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var materialType = line.Contains("iv", StringComparison.OrdinalIgnoreCase) || line.Contains("nonce", StringComparison.OrdinalIgnoreCase) ? "iv-or-nonce" : value.Contains("BEGIN", StringComparison.OrdinalIgnoreCase) ? "private-key-or-certificate" : "key-or-secret";
            var location = CreateLocation(basePath, file, lineNumber, Math.Max(1, match.Index + 1));
            var methodId = ResolveMethodId(reachability, file, namespaceName, className, methodName);
            var entryPointIds = reachability.EntryPointsFor(methodId, file, methodName);
            var materialId = $"cma{result.Materials.Count + 1}";
            result.Materials.Add(new CryptoMaterial
            {
                Id = materialId,
                MaterialType = materialType,
                Storage = "hardcoded",
                RedactedValue = Redact(value),
                Fingerprint = Fingerprint(value),
                Confidence = value.Contains("BEGIN", StringComparison.OrdinalIgnoreCase) ? "High" : "Medium",
                MethodId = methodId,
                Location = location,
                ReachableFromEntryPoint = entryPointIds.Count > 0,
                EntryPointIds = entryPointIds
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

    private static void AddFinding(CryptoAnalysisResult result, string ruleId, string severity, string confidence, string summary, string? recommendation, CodeLocation location, string? methodId, bool reachable, List<string> entryPointIds, List<string> assetIds, List<string> operationIds, List<string> materialIds)
    {
        var dedupeKey = $"{ruleId}\u001f{location.Path}\u001f{location.LineNumber}\u001f{location.ColumnNumber}";
        if (result.Findings.Any(finding => finding.Properties.TryGetValue("dedupeKey", out var existing) && existing == dedupeKey))
        {
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

    private static CryptoClassification? ClassifyCryptoSymbol(string symbol, string code, string source)
    {
        var text = $"{symbol} {code}";

        if (ContainsAny(text, "MD5", "EVP_md5", "digest::digest") || text.Contains("algo = \"md5\"", StringComparison.OrdinalIgnoreCase))
            return Classify("MD5", "hash", "weak", rule: "DOSAI-CRYPTO-WEAK-HASH-MD5", severity: "High", summary: "MD5 hashing was detected.", recommendation: "Use SHA-256 or stronger for integrity, and password-specific hashing for passwords.");
        if (ContainsAny(text, "SHA1", "SHA1CryptoServiceProvider", "EVP_sha1") || text.Contains("algo = \"sha1\"", StringComparison.OrdinalIgnoreCase))
            return Classify("SHA-1", "hash", "weak", rule: "DOSAI-CRYPTO-WEAK-HASH-SHA1", severity: "Medium", summary: "SHA-1 hashing was detected.", recommendation: "Use SHA-256 or stronger, unless required only for legacy non-security identifiers.");
        if (ContainsAny(text, "DES", "TripleDES", "RC2", "EVP_des", "EVP_rc4"))
            return Classify(symbol.Contains("Triple", StringComparison.OrdinalIgnoreCase) ? "3DES" : "DES/RC2/RC4", "symmetric", "weak", rule: "DOSAI-CRYPTO-WEAK-CIPHER", severity: "High", summary: "Weak or legacy symmetric cipher was detected.", recommendation: "Use AES-GCM or another approved authenticated encryption mode.");
        if (ContainsAny(text, "AesGcm", "AES_gcm", "EVP_aes_256_gcm")) return Classify("AES-GCM", "symmetric", "strong", "encrypt/decrypt", "NIST");
        if (ContainsAny(text, "AesCcm", "AES_ccm")) return Classify("AES-CCM", "symmetric", "strong", "encrypt/decrypt", "NIST");
        if (ContainsAny(text, "Aes", "AES_", "EVP_aes")) return Classify("AES", "symmetric", "acceptable", "encrypt/decrypt", "NIST");
        if (ContainsAny(text, "RSA", "RSA_")) return Classify("RSA", "asymmetric", "acceptable", "sign/encrypt", "PKCS#1");
        if (ContainsAny(text, "ECDsa", "ECDSA")) return Classify("ECDSA", "asymmetric", "strong", "sign", "FIPS 186");
        if (ContainsAny(text, "ECDiffieHellman", "ECDH")) return Classify("ECDH", "key-agreement", "strong", "key-agreement");
        if (ContainsAny(text, "SHA256", "SHA384", "SHA512", "EVP_sha256", "EVP_sha512")) return Classify(symbol.Contains("512", StringComparison.Ordinal) ? "SHA-512" : "SHA-2", "hash", "strong");
        if (ContainsAny(text, "HMACSHA256", "HMACSHA384", "HMACSHA512", "HMAC(")) return Classify("HMAC", "mac", "strong", "mac");
        if (ContainsAny(text, "Rfc2898DeriveBytes", "PKCS5_PBKDF2_HMAC")) return Classify("PBKDF2", "kdf", "acceptable", "key-derivation", "PKCS#5");
        if (ContainsAny(text, "RandomNumberGenerator", "RNGCryptoServiceProvider", "RAND_bytes")) return Classify("CSPRNG", "random", "strong", "random");
        if (ContainsAny(text, "System.Random", "new Random", "rand()", "srand(")) return Classify("Non-cryptographic RNG", "random", "weak", "random", rule: "DOSAI-CRYPTO-INSECURE-RNG", severity: "Medium", summary: "Non-cryptographic random number generator was detected near security analysis context.", recommendation: "Use RandomNumberGenerator for security-sensitive randomness.");
        if (ContainsAny(text, "X509Certificate2", "X509_STORE", "CertificateRequest")) return Classify("X.509", "certificate", "unknown", "certificate");
        if (ContainsAny(text, "SslStream", "SSL_CTX", "SslProtocols", "TLS")) return Classify("TLS", "protocol", "acceptable", "transport-security", "TLS");
        if (ContainsAny(text, "SecurityAlgorithms.None")) return Classify("JWT none", "signature", "weak", "sign", rule: "DOSAI-CRYPTO-JWT-NONE", severity: "High", summary: "JWT 'none' algorithm was detected.", recommendation: "Require strong token signing and validation.");
        if (ContainsAny(text, "openssl::", "sodium::", "digest::")) return Classify(symbol, "library", "unknown", "library");
        return null;

        CryptoClassification Classify(string algorithm, string family, string strength, string operationType = "use", string? standard = null, string? rule = null, string? severity = null, string? summary = null, string? recommendation = null) =>
            new(algorithm, family, strength, operationType, standard, rule, severity, "High", summary, recommendation);
    }

    private static bool ContainsAny(string value, params string[] candidates) => candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));

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
                ["dosai:crypto:materialType"] = material.MaterialType,
                ["dosai:crypto:storage"] = material.Storage,
                ["dosai:crypto:algorithm"] = material.Algorithm,
                ["dosai:crypto:redactedValue"] = material.RedactedValue,
                ["dosai:crypto:fingerprint"] = material.Fingerprint,
                ["dosai:crypto:confidence"] = material.Confidence,
                ["dosai:method:id"] = material.MethodId,
                ["dosai:crypto:reachableFromEntryPoint"] = material.ReachableFromEntryPoint.ToString().ToLowerInvariant(),
                ["dosai:crypto:entryPointIds"] = string.Join(",", material.EntryPointIds),
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
                ["dosai:method:id"] = finding.MethodId,
                ["dosai:location"] = FormatLocation(finding.Location)
            })
        }).ToList();

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
                    ["dosai:crypto:findingCount"] = result.Statistics.FindingCount.ToString()
                })
            },
            components,
            vulnerabilities
        };
        return JsonSerializer.Serialize(bom, JsonOptions);
    }

    private static object[] ToProperties(Dictionary<string, string?> values) => values
        .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Value))
        .Select(kvp => new { name = kvp.Key, value = kvp.Value })
        .ToArray();

    private static int[] ParseCwe(string cwe) => int.TryParse(cwe.Replace("CWE-", string.Empty, StringComparison.OrdinalIgnoreCase), out var number) ? [number] : [];

    private static string FormatLocation(CodeLocation location) => $"{location.Path ?? location.FileName}:{location.LineNumber}:{location.ColumnNumber}";
}
