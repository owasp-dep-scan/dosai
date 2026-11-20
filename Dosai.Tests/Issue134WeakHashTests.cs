using Depscan;
using Xunit;

namespace Depscan.Tests;

/// <summary>
///     Issue #134: the weak-hash rules read the algorithm argument of the one-shot hash APIs.
///     The semantic walker visits expression bodies, field-initializer lambdas and VB
///     statements the line fallback skips, a decoded argument classifies through the existing
///     rules (MD5 keeps its finding; an HMAC argument names the MAC and replaces the generic
///     HMAC asset the method name used to give), and signature tokens never enter because the
///     decode only sees compile-time constants on a bound CryptographicOperations call.
/// </summary>
public class Issue134WeakHashTests
{
    private sealed class TempDir : IDisposable
    {
        public TempDir() => Directory.CreateDirectory(Path);

        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dosai-issue134-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static CryptoAnalysisResult Analyze(string source, string fileName = "Fixture.cs")
    {
        using var fixture = new TempDir();
        File.WriteAllText(System.IO.Path.Combine(fixture.Path, fileName), source);
        return CryptoAnalyzer.Analyze(fixture.Path);
    }

    private static int LineOf(string source, string marker)
    {
        var lines = source.Split('\n');
        var index = Array.FindIndex(lines, line => line.Contains(marker, StringComparison.Ordinal));
        Assert.True(index >= 0, $"marker {marker} not found");
        return index + 1;
    }

    private record AssetRow(string Path, int Line, int Column, string Name, string? Family, string? Strength, string? Source);

    private static List<AssetRow> AssetsOnLine(CryptoAnalysisResult result, string path, int line) => result.Assets
        .Where(asset => asset.Location.Path == path && asset.Location.LineNumber == line)
        .Select(asset => new AssetRow(asset.Location.Path!, asset.Location.LineNumber, asset.Location.ColumnNumber, asset.Name, asset.Family, asset.Strength, asset.Properties.TryGetValue("source", out var source) ? source : null))
        .ToList();

    private const string Usings = """
using System;
using System.Security.Cryptography;

""";

    [Fact]
    public void WeakHashArgument_IsReadInEveryBodyShape()
    {
        var source = Usings + """
public static class Hashing
{
    public static byte[] Arrow(byte[] data) => CryptographicOperations.HashData(HashAlgorithmName.MD5, data); // m:arrow
    public static async System.Threading.Tasks.Task<byte[]> ArrowAsync(System.IO.Stream data) => await CryptographicOperations.HashDataAsync(HashAlgorithmName.MD5, data); // m:async
    public static byte[] InlineConstant(byte[] data) => CryptographicOperations.HashData(new HashAlgorithmName("MD5"), data); // m:inline

    public static byte[] Block(byte[] data)
    {
        return CryptographicOperations.HashData(HashAlgorithmName.MD5, data); // m:block
    }

    private static readonly Func<byte[], byte[]> ParenLambda = (d) => CryptographicOperations.HashData(HashAlgorithmName.MD5, d); // m:lambda
}
""";
        // The using-static shape is its own file: beside `using System.Security.Cryptography`
        // the bare MD5 is CS0229-ambiguous between the type and the property, so the call must
        // be fully qualified for the argument to bind the property at all.
        var usingStatic = """
using static System.Security.Cryptography.HashAlgorithmName;

public static class UsingStatic
{
    public static byte[] ViaUsingStatic(byte[] data) => System.Security.Cryptography.CryptographicOperations.HashData(MD5, data); // m:usingstatic
}
""";
        CryptoAnalysisResult result;
        using (var fixture = new TempDir())
        {
            File.WriteAllText(System.IO.Path.Combine(fixture.Path, "Hashing.cs"), source);
            File.WriteAllText(System.IO.Path.Combine(fixture.Path, "UsingStatic.cs"), usingStatic);
            result = CryptoAnalyzer.Analyze(fixture.Path);
        }

        foreach (var (marker, argumentText) in new[]
                 {
                     ("m:arrow", "HashAlgorithmName.MD5"),
                     ("m:async", "HashAlgorithmName.MD5"),
                     ("m:inline", "new HashAlgorithmName(\"MD5\")"),
                     ("m:block", "HashAlgorithmName.MD5"),
                     ("m:lambda", "HashAlgorithmName.MD5"),
                 })
        {
            var line = LineOf(source, marker);
            var asset = Assert.Single(AssetsOnLine(result, "Hashing.cs", line), row => row.Name == "MD5");
            // The asset is anchored at the algorithm argument, not the invocation or a token.
            var argumentColumn = source.Split('\n')[line - 1].IndexOf(argumentText, StringComparison.Ordinal) + 1;
            Assert.Equal(argumentColumn, asset.Column);
        }

        var usingStaticLine = LineOf(usingStatic, "m:usingstatic");
        Assert.Contains(AssetsOnLine(result, "UsingStatic.cs", usingStaticLine), row => row.Name == "MD5" && row.Source == "roslyn");

        // The finding fires exactly once per line, and the operation names the API called.
        Assert.All(new[] { "m:arrow", "m:async", "m:inline", "m:block", "m:lambda" }, marker =>
            Assert.Contains(LineOf(source, marker), Lines(result, "DOSAI-CRYPTO-WEAK-HASH-MD5")));
        Assert.Equal(5, result.Findings.Count(finding => finding.RuleId == "DOSAI-CRYPTO-WEAK-HASH-MD5" && finding.Location.Path == "Hashing.cs"));
        var operation = Assert.Single(result.Operations, op =>
            op.Symbol?.StartsWith("System.Security.Cryptography.CryptographicOperations.", StringComparison.Ordinal) == true
            && op.Location.Path == "Hashing.cs"
            && op.Location.LineNumber == LineOf(source, "m:arrow"));
        Assert.Equal("roslyn", operation.Properties["source"]);
        Assert.Equal("MD5", operation.Algorithm);
    }

    [Fact]
    public void Vb_DimStatement_RecordsFromTheArgument()
    {
        var source = """
Imports System.Security.Cryptography

Public Module HashingVb

    Public Function Hash(data As Byte()) As Byte()
        Dim hash = CryptographicOperations.HashData(HashAlgorithmName.MD5, Data) ' m:dim
        Return hash
    End Function

End Module
""";
        var result = Analyze(source, "Hashing.vb");
        var line = LineOf(source, "m:dim");
        Assert.Contains(AssetsOnLine(result, "Hashing.vb", line), row => row.Name == "MD5" && row.Source == "roslyn");
        Assert.Contains(line, Lines(result, "DOSAI-CRYPTO-WEAK-HASH-MD5"));
    }

    /// <summary>The reporter's anchored-regex build would have reported the RSA of a signature; it must stay silent.</summary>
    [Fact]
    public void SignatureProbe_IsNotAnAsset()
    {
        var source = Usings + """
public static class HasProbe
{
    public static bool Has(RSA? rsa) => rsa is not null; // m:has

    public static byte[] Block(byte[] data)
    {
        return CryptographicOperations.HashData(HashAlgorithmName.MD5, data); // m:block
    }
}
""";
        var result = Analyze(source);
        Assert.DoesNotContain(result.Assets, asset => asset.Location.LineNumber == LineOf(source, "m:has"));
        Assert.Empty(result.Findings.Where(finding => finding.Location.LineNumber == LineOf(source, "m:has")));
        var line = LineOf(source, "m:block");
        Assert.Contains(AssetsOnLine(result, "Fixture.cs", line), row => row.Name == "MD5");
        Assert.Contains(line, Lines(result, "DOSAI-CRYPTO-WEAK-HASH-MD5"));
    }

    [Fact]
    public void HmacData_ArgumentNamesTheMac_OneAssetPerLine()
    {
        var source = Usings + """
public static class Hmac
{
    public static byte[] Decoded(byte[] key, byte[] data) => CryptographicOperations.HmacData(HashAlgorithmName.MD5, key, data); // m:decoded
    public static byte[] Runtime(HashAlgorithmName name, byte[] key, byte[] data) => CryptographicOperations.HmacData(name, key, data); // m:runtime
}
""";
        var result = Analyze(source);

        // A decoded argument replaces the generic strong HMAC the method name used to give.
        var decoded = Assert.Single(AssetsOnLine(result, "Fixture.cs", LineOf(source, "m:decoded")));
        Assert.Equal(("HMAC-MD5", "mac", "weak", "roslyn"), (decoded.Name, decoded.Family, decoded.Strength, decoded.Source));
        Assert.DoesNotContain(LineOf(source, "m:decoded"), Lines(result, "DOSAI-CRYPTO-WEAK-HASH-MD5"));

        // A runtime-chosen algorithm is a MAC of unknown strength, never the strong generic HMAC.
        var runtime = Assert.Single(AssetsOnLine(result, "Fixture.cs", LineOf(source, "m:runtime")));
        Assert.Equal(("HMAC", "mac", "unknown", "roslyn"), (runtime.Name, runtime.Family, runtime.Strength, runtime.Source));
    }

    [Fact]
    public void HmacDataBlockBody_GivesHmacMd5Only_LikeTheExpressionBody()
    {
        var source = Usings + """
public static class Hmac
{
    public static byte[] Block(byte[] key, byte[] data)
    {
        return CryptographicOperations.HmacData(HashAlgorithmName.MD5, key, data); // m:block
    }
}
""";
        var result = Analyze(source);
        var line = LineOf(source, "m:block");
        // The line fallback no longer reads the MD5 token the walker already classified as the
        // MAC's algorithm: one HMAC-MD5 asset and no plain-MD5 hashing finding, as in the
        // expression-bodied form.
        var row = Assert.Single(AssetsOnLine(result, "Fixture.cs", line));
        Assert.Equal(("HMAC-MD5", "mac", "weak", "roslyn"), (row.Name, row.Family, row.Strength, row.Source));
        Assert.DoesNotContain(line, Lines(result, "DOSAI-CRYPTO-WEAK-HASH-MD5"));
    }

    [Fact]
    public void ReadonlyFieldAlgorithm_IsResolved_AndAnUnresolvableMacIsNotStrong()
    {
        var source = Usings + """
public class Fields
{
    private static readonly HashAlgorithmName Weak = HashAlgorithmName.MD5;
    public static HashAlgorithmName Mutable = HashAlgorithmName.SHA256;
    private static readonly HashAlgorithmName Reassigned = HashAlgorithmName.SHA1;
    private readonly HashAlgorithmName _instance = HashAlgorithmName.SHA1;

    static Fields() { Reassigned = HashAlgorithmName.SHA512; }

    public byte[] ViaInstance(byte[] key, byte[] data) => CryptographicOperations.HmacData(_instance, key, data); // m:instance
    public static byte[] ViaReassigned(byte[] key, byte[] data) => CryptographicOperations.HmacData(Reassigned, key, data); // m:reassigned

    public static byte[] ViaField(byte[] key, byte[] data) => CryptographicOperations.HmacData(Weak, key, data); // m:field
    public static byte[] ViaMutable(byte[] key, byte[] data) => CryptographicOperations.HmacData(Mutable, key, data); // m:mutable
}
""";
        var result = Analyze(source);
        var field = Assert.Single(AssetsOnLine(result, "Fixture.cs", LineOf(source, "m:field")));
        Assert.Equal(("HMAC-MD5", "weak"), (field.Name, field.Strength));
        var mutable = Assert.Single(AssetsOnLine(result, "Fixture.cs", LineOf(source, "m:mutable")));
        Assert.Equal(("HMAC", "unknown"), (mutable.Name, mutable.Strength));
        // A constructor may store another value in an instance field, and a static constructor
        // in a static one: neither initializer is trusted, so no weak finding appears.
        foreach (var marker in new[] { "m:instance", "m:reassigned" })
        {
            var row = Assert.Single(AssetsOnLine(result, "Fixture.cs", LineOf(source, marker)));
            Assert.Equal(("HMAC", "unknown"), (row.Name, row.Strength));
        }
    }

    [Fact]
    public void IncrementalHashAndSignatureCalls_DecodeTheirAlgorithmInExpressionBodies()
    {
        var source = Usings + """
public static class Others
{
    public static IncrementalHash Hash() => IncrementalHash.CreateHash(HashAlgorithmName.MD5); // m:hash
    public static IncrementalHash Mac(byte[] key) => IncrementalHash.CreateHMAC(HashAlgorithmName.MD5, key); // m:mac
    public static byte[] Sign(RSA rsa, byte[] data) => rsa.SignData(data, HashAlgorithmName.MD5, RSASignaturePadding.Pkcs1); // m:sign
    public static bool Verify(ECDsa key, byte[] data, byte[] signature) => key.VerifyData(data, signature, HashAlgorithmName.SHA1); // m:verify
}
""";
        var result = Analyze(source);
        Assert.Contains(AssetsOnLine(result, "Fixture.cs", LineOf(source, "m:hash")), row => row.Name == "MD5");
        Assert.Contains(LineOf(source, "m:hash"), Lines(result, "DOSAI-CRYPTO-WEAK-HASH-MD5"));
        Assert.Contains(AssetsOnLine(result, "Fixture.cs", LineOf(source, "m:mac")), row => row.Name == "HMAC-MD5");
        Assert.DoesNotContain(LineOf(source, "m:mac"), Lines(result, "DOSAI-CRYPTO-WEAK-HASH-MD5"));
        var sign = AssetsOnLine(result, "Fixture.cs", LineOf(source, "m:sign"));
        Assert.Contains(sign, row => row.Name == "MD5");
        Assert.Contains(LineOf(source, "m:sign"), Lines(result, "DOSAI-CRYPTO-WEAK-HASH-MD5"));
        Assert.Contains(AssetsOnLine(result, "Fixture.cs", LineOf(source, "m:verify")), row => row.Name == "SHA-1");
    }

    [Fact]
    public void StrongAndSha1Arguments_ClassifyWithoutInventingRules()
    {
        var source = Usings + """
public static class Kinds
{
    public static byte[] Sha256(byte[] data) => CryptographicOperations.HashData(HashAlgorithmName.SHA256, data); // m:sha256
    public static byte[] Sha1(byte[] data) => CryptographicOperations.HashData(HashAlgorithmName.SHA1, data); // m:sha1
    public static byte[] Hyphenated(byte[] data) => CryptographicOperations.HashData(new HashAlgorithmName("SHA-256"), data); // m:hyphen
}
""";
        var result = Analyze(source);

        var sha256 = Assert.Single(AssetsOnLine(result, "Fixture.cs", LineOf(source, "m:sha256")));
        Assert.Equal("SHA-2", sha256.Name);
        Assert.DoesNotContain(LineOf(source, "m:sha256"), Lines(result, "DOSAI-CRYPTO-WEAK-HASH-MD5"));
        Assert.DoesNotContain(LineOf(source, "m:sha256"), Lines(result, "DOSAI-CRYPTO-WEAK-HASH-SHA1"));

        var sha1 = Assert.Single(AssetsOnLine(result, "Fixture.cs", LineOf(source, "m:sha1")), row => row.Name == "SHA-1");
        var sha1Line = LineOf(source, "m:sha1");
        var sha1Finding = Assert.Single(result.Findings, finding => finding.RuleId == "DOSAI-CRYPTO-WEAK-HASH-SHA1");
        Assert.Equal(sha1Line, sha1Finding.Location.LineNumber);
        Assert.Equal("CWE-328", sha1Finding.Cwe);

        // A hyphenated spelling matches no classifier candidate: no normalization is invented.
        Assert.DoesNotContain(result.Assets, asset => asset.Location.LineNumber == LineOf(source, "m:hyphen"));
        Assert.DoesNotContain(LineOf(source, "m:hyphen"), Lines(result, "DOSAI-CRYPTO-WEAK-HASH-MD5"));
    }

    [Fact]
    public void LocalAlgorithmName_StaysWithTheLineFallback()
    {
        var source = Usings + """
public static class Local
{
    public static byte[] ViaLocal(byte[] data)
    {
        var algo = new HashAlgorithmName("MD5"); // m:declare
        return CryptographicOperations.HashData(algo, data); // m:call
    }
}
""";
        var result = Analyze(source);
        Assert.Contains(LineOf(source, "m:declare"), Lines(result, "DOSAI-CRYPTO-WEAK-HASH-MD5"));
        Assert.DoesNotContain(LineOf(source, "m:call"), Lines(result, "DOSAI-CRYPTO-WEAK-HASH-MD5"));
        Assert.DoesNotContain(result.Assets, asset => asset.Location.LineNumber == LineOf(source, "m:call"));
    }

    /// <summary>The decode helper itself: property form (with and without a conversion), constant form, and the failures.</summary>
    [Fact]
    public void HashAlgorithmNameOf_DecodesPropertyAndConstantForms()
    {
        var snippet = """
using System.Security.Cryptography;

public static class Probe
{
    private static HashAlgorithmName Md5 => HashAlgorithmName.MD5;
    private static HashAlgorithmName Sha3 => HashAlgorithmName.SHA3_256;
    private static HashAlgorithmName Converted => (HashAlgorithmName)HashAlgorithmName.MD5;
    private static HashAlgorithmName FromConstant => new HashAlgorithmName("MD5");
    private static HashAlgorithmName FromHyphenated => new HashAlgorithmName("SHA3-256");
    private static HashAlgorithmName FromParameter(HashAlgorithmName name) => name;
    private static HashAlgorithmName FromComputedName(string name) => new HashAlgorithmName(name);
}
""";
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(snippet);
        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
            "issue134-decode",
            [tree],
            FrameworkReferences.Current.References.Select(entry => entry.Reference),
            new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));
        Assert.Empty(compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error));
        var model = compilation.GetSemanticModel(tree);

        string? Decode(string memberName)
        {
            var declaration = (Microsoft.CodeAnalysis.SyntaxNode?)tree.GetRoot().DescendantNodes()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.PropertyDeclarationSyntax>()
                .FirstOrDefault(property => property.Identifier.ValueText == memberName)
                ?? tree.GetRoot().DescendantNodes()
                    .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>()
                    .FirstOrDefault(method => method.Identifier.ValueText == memberName);
            Assert.NotNull(declaration);
            var expression = declaration switch
            {
                Microsoft.CodeAnalysis.CSharp.Syntax.PropertyDeclarationSyntax property => (Microsoft.CodeAnalysis.SyntaxNode)property.ExpressionBody!.Expression,
                _ => ((Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax)declaration).ExpressionBody!.Expression
            };
            return CryptoAnalyzer.HashAlgorithmNameOf(model.GetOperation(expression)!);
        }

        Assert.Equal("MD5", Decode("Md5"));
        Assert.Equal("SHA3_256", Decode("Sha3"));
        Assert.Equal("MD5", Decode("Converted"));
        Assert.Equal("MD5", Decode("FromConstant"));
        Assert.Equal("SHA3-256", Decode("FromHyphenated"));
        Assert.Null(Decode("FromParameter"));
        Assert.Null(Decode("FromComputedName"));
    }

    private static HashSet<int> Lines(CryptoAnalysisResult result, string rule) =>
        result.Findings.Where(finding => finding.RuleId == rule).Select(finding => finding.Location.LineNumber).ToHashSet();
}
