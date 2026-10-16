using System.Text.Json;
using Depscan;
using Xunit;

namespace Depscan.Tests;

/// <summary>
///     Crypto classification and misuse gaps: identifier-boundary algorithm tokens, MACs and
///     KDFs ahead of the hashes they name, the algorithms the catalog lacked, rule-specific
///     CWEs, constant-folded and defaulted PBKDF2 parameters, static and zero IVs (and IV prose
///     that is not one), commented-out code, TLS versions, certificate callbacks, JWT
///     validation, key sizes, RSA padding, timing-unsafe comparisons, obsolete APIs and the
///     CycloneDX primitive of each algorithm.
/// </summary>
public class CryptoGapTests
{
    private sealed class TempDir : IDisposable
    {
        public TempDir() => Directory.CreateDirectory(Path);

        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dosai-crypto-gaps-" + Guid.NewGuid().ToString("N"));

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

    /// <summary>The findings of a rule, by the source line they sit on.</summary>
    private static HashSet<int> Lines(CryptoAnalysisResult result, string rule) =>
        result.Findings.Where(finding => finding.RuleId == rule).Select(finding => finding.Location.LineNumber).ToHashSet();

    private static int LineOf(string source, string marker)
    {
        var lines = source.Split('\n');
        var index = Array.FindIndex(lines, line => System.Text.RegularExpressions.Regex.IsMatch(line, System.Text.RegularExpressions.Regex.Escape(marker) + @"(?![\w:])"));
        Assert.True(index >= 0, $"marker {marker} not found");
        return index + 1;
    }

    private const string Usings = """
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

""";

    [Fact]
    public void AlgorithmTokens_MatchWholeIdentifierWords()
    {
        var source = Usings + """
public static class Tokens
{
    public static string Describe(Oid oid) => oid.FriendlyName ?? ""; // m:describe
    public static object Universal(AsnEncodedData data) => data.Format(true); // m:format
    public static object PublicRsa(X509Certificate2 certificate) => certificate.GetRSAPublicKey()!; // m:getrsa
    public static object Des() => DES.Create(); // m:des
    public static object Triple() => TripleDES.Create(); // m:triple
}
""";
        var result = Analyze(source);
        var algorithmsByLine = result.Assets.GroupBy(asset => asset.Location.LineNumber).ToDictionary(group => group.Key, group => group.Select(asset => asset.Name).ToHashSet());

        Assert.False(algorithmsByLine.TryGetValue(LineOf(source, "m:describe"), out var describe) && describe.Contains("DES/RC2/RC4"));
        Assert.False(algorithmsByLine.TryGetValue(LineOf(source, "m:format"), out var format) && format.Contains("RSA"));
        Assert.Contains("RSA", algorithmsByLine[LineOf(source, "m:getrsa")]);
        Assert.Contains("DES/RC2/RC4", algorithmsByLine[LineOf(source, "m:des")]);
        Assert.Contains("3DES", algorithmsByLine[LineOf(source, "m:triple")]);
    }

    [Fact]
    public void MacsKdfsAndNewAlgorithms_AreClassifiedAheadOfTheirHashes()
    {
        var source = Usings + """
public static class Algorithms
{
    public static byte[] Mac(byte[] key, byte[] data) => HMACSHA256.HashData(key, data); // m:hmac256
    public static byte[] LegacyMac(byte[] key, byte[] data) => HMACMD5.HashData(key, data); // m:hmacmd5
    public static byte[] Sha3(byte[] data) => SHA3_256.HashData(data); // m:sha3
    public static byte[] Shake(byte[] data) => Shake256.HashData(data, 64); // m:shake
    public static byte[] Hkdf(byte[] ikm) => HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32); // m:hkdf
    public static object Kbkdf(byte[] key) => new SP800108HmacCounterKdf(key, HashAlgorithmName.SHA256); // m:kbkdf
    public static object Pbkdf1(string password, byte[] salt) => new PasswordDeriveBytes(password, salt); // m:pbkdf1
    public static object Dsa() => DSA.Create(); // m:dsa
    public static object Loader(byte[] raw) => X509CertificateLoader.LoadCertificate(raw); // m:loader
    public static void Protocols()
    {
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12; // m:spt
    }
}
""";
        var result = Analyze(source);
        bool Has(string marker, string algorithm) => result.Assets.Any(asset => asset.Location.LineNumber == LineOf(source, marker) && asset.Name == algorithm);

        Assert.True(Has("m:hmac256", "HMAC"));
        Assert.False(Has("m:hmac256", "SHA-2"));
        Assert.True(Has("m:hmacmd5", "HMAC-MD5"));
        Assert.DoesNotContain(LineOf(source, "m:hmacmd5"), Lines(result, "DOSAI-CRYPTO-WEAK-HASH-MD5"));
        Assert.True(Has("m:sha3", "SHA-3"));
        Assert.True(Has("m:shake", "SHAKE"));
        Assert.True(Has("m:hkdf", "HKDF"));
        Assert.True(Has("m:kbkdf", "KBKDF"));
        Assert.True(Has("m:pbkdf1", "PBKDF1"));
        Assert.Contains(LineOf(source, "m:pbkdf1"), Lines(result, "DOSAI-CRYPTO-WEAK-KDF"));
        Assert.True(Has("m:dsa", "DSA"));
        Assert.True(Has("m:loader", "X.509"));
        Assert.Contains(result.Protocols, protocol => protocol.Location.LineNumber == LineOf(source, "m:spt") && protocol.Version == "TLS 1.2");
    }

    [Fact]
    public void Pbkdf2_ReadsConstantsDefaultsAndPrf()
    {
        var source = Usings + """
public static class Kdf
{
    private const int Iterations = 1000;
    private const int Strong = 600_000;
    public static byte[] ConstLow(byte[] p, byte[] s) => Rfc2898DeriveBytes.Pbkdf2(p, s, Iterations, HashAlgorithmName.SHA256, 32); // m:constlow
    public static byte[] ConstStrong(byte[] p, byte[] s) => Rfc2898DeriveBytes.Pbkdf2(p, s, Strong, HashAlgorithmName.SHA256, 32); // m:strong
    public static byte[] WeakPrf(byte[] p, byte[] s) => Rfc2898DeriveBytes.Pbkdf2(p, s, Strong, HashAlgorithmName.SHA1, 32); // m:weakprf
#pragma warning disable SYSLIB0041, SYSLIB0060
    public static object Defaulted(string p, byte[] s) => new Rfc2898DeriveBytes(p, s); // m:default
    public static object SaltSizeFirst(string p) => new Rfc2898DeriveBytes(p, 16, 600_000, HashAlgorithmName.SHA256); // m:saltsize
}
""";
        var result = Analyze(source);
        var low = Lines(result, "DOSAI-CRYPTO-LOW-PBKDF2-ITERATIONS");
        var prf = Lines(result, "DOSAI-CRYPTO-WEAK-KDF-PRF");

        Assert.Contains(LineOf(source, "m:constlow"), low);
        Assert.DoesNotContain(LineOf(source, "m:strong"), low);
        Assert.Contains(LineOf(source, "m:default"), low);
        Assert.DoesNotContain(LineOf(source, "m:saltsize"), low);
        Assert.Contains(LineOf(source, "m:weakprf"), prf);
        Assert.Contains(LineOf(source, "m:default"), prf);
        Assert.DoesNotContain(LineOf(source, "m:strong"), prf);
        Assert.Contains(LineOf(source, "m:default"), Lines(result, "DOSAI-CRYPTO-OBSOLETE-API"));
        Assert.All(result.Findings.Where(finding => finding.RuleId is "DOSAI-CRYPTO-LOW-PBKDF2-ITERATIONS" or "DOSAI-CRYPTO-WEAK-KDF-PRF"), finding => Assert.Equal("CWE-916", finding.Cwe));
    }

    [Fact]
    public void StaticIvs_ZeroAndConstantAreFound_ProseIsNot()
    {
        var source = Usings + """
public static class Ivs
{
    public static byte[] Zero(Aes aes, byte[] data) => aes.EncryptCbc(data, new byte[16]); // m:zeroarg
    public static void ZeroProperty(Aes aes) { aes.IV = new byte[16]; } // m:zeroprop
    public static void Constant(Aes aes) { aes.IV = Encoding.UTF8.GetBytes("abcdefgh12345678"); } // m:constprop
    public static void Local() { byte[] iv = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 }; } // m:local
    public static void Random(Aes aes) { aes.GenerateIV(); var iv = RandomNumberGenerator.GetBytes(16); aes.IV = iv; } // m:random
    public static void Log(byte[] iv) { Console.WriteLine("Generated IV for the request: " + Convert.ToHexString(iv)); } // m:log
    public static void Throw() { throw new ArgumentException("The IV must be exactly sixteen bytes"); } // m:throw
}
""";
        var result = Analyze(source);
        var staticIv = Lines(result, "DOSAI-CRYPTO-STATIC-IV");

        Assert.Contains(LineOf(source, "m:zeroarg"), staticIv);
        Assert.Contains(LineOf(source, "m:zeroprop"), staticIv);
        Assert.Contains(LineOf(source, "m:constprop"), staticIv);
        Assert.Contains(LineOf(source, "m:local"), staticIv);
        Assert.DoesNotContain(LineOf(source, "m:random"), staticIv);
        Assert.DoesNotContain(LineOf(source, "m:log"), staticIv);
        Assert.DoesNotContain(LineOf(source, "m:throw"), staticIv);
        Assert.All(result.Findings.Where(finding => finding.RuleId == "DOSAI-CRYPTO-STATIC-IV"), finding => Assert.Equal("CWE-1204", finding.Cwe));
    }

    [Fact]
    public void CommentedOutCode_IsNotAUse()
    {
        var source = Usings + """
public static class Comments
{
    public static void Run()
    {
        // var md5 = MD5.Create(); m:line
        /* var sha1 = SHA1.Create();
           var des = DES.Create(); m:block */
        var url = "http://example.com/MD5.Create()"; // m:string
    }
}
""";
        var result = Analyze(source);

        Assert.DoesNotContain(result.Assets, asset => asset.Name is "MD5" or "SHA-1" or "DES/RC2/RC4" && asset.Location.LineNumber < LineOf(source, "m:string"));

        var vb = """
Imports System.Security.Cryptography
Public Module Legacy
    Public Sub Run()
        ' Dim md5 = MD5.Create()
        REM Dim sha1 = SHA1.Create()
        Dim text = "MD5.Create() isn't used here"
    End Sub
End Module
""";
        var vbResult = Analyze(vb, "Legacy.vb");
        Assert.DoesNotContain(vbResult.Findings, finding => finding.RuleId is "DOSAI-CRYPTO-WEAK-HASH-MD5" or "DOSAI-CRYPTO-WEAK-HASH-SHA1" && finding.Location.LineNumber is 4 or 5);
    }

    [Fact]
    public void TransportAndTokenMisuse_IsReportedWithItsOwnRuleAndCwe()
    {
        var source = Usings + """
public static class Transport
{
    public static void Tls10(SslServerAuthenticationOptions options) { options.EnabledSslProtocols = SslProtocols.Tls; } // m:tls10
    public static void Tls11() { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls11; } // m:tls11
    public static void Tls12(SslServerAuthenticationOptions options) { options.EnabledSslProtocols = SslProtocols.Tls12; } // m:tls12
    public static void AcceptAll(HttpClientHandler handler) { handler.ServerCertificateCustomValidationCallback = (message, certificate, chain, errors) => true; } // m:always
    public static void Custom(HttpClientHandler handler) { handler.ServerCertificateCustomValidationCallback = (message, certificate, chain, errors) => errors == SslPolicyErrors.None; } // m:custom
    public static void AcceptAllDelegate() { ServicePointManager.ServerCertificateValidationCallback += delegate { return true; }; } // m:delegate
    public static byte[] Pkcs1(RSA rsa, byte[] data) => rsa.Encrypt(data, RSAEncryptionPadding.Pkcs1); // m:pkcs1
    public static byte[] Oaep(RSA rsa, byte[] data) => rsa.Encrypt(data, RSAEncryptionPadding.OaepSHA256); // m:oaep
    public static object SmallRsa() => RSA.Create(1024); // m:rsa1024
    public static object BigRsa() => RSA.Create(3072); // m:rsa3072
    public static void SmallKeySize(RSA rsa) { rsa.KeySize = 1024; } // m:keysize
    public static object SmallCurve() => ECDsa.Create(ECCurve.NamedCurves.brainpoolP160r1); // m:curve
    public static byte[] Ecb(Aes aes, byte[] data) => aes.EncryptEcb(data, PaddingMode.PKCS7); // m:ecb
    public static bool Compare(byte[] expectedHash, byte[] actual) => expectedHash.SequenceEqual(actual); // m:compare
    public static bool FixedTime(byte[] expectedHash, byte[] actual) => CryptographicOperations.FixedTimeEquals(expectedHash, actual); // m:fixed
    public static object Jwt() => new { RequireSignedTokens = false, ValidateLifetime = false }; // m:jwt
}
""";
        var result = Analyze(source);

        Assert.Contains(LineOf(source, "m:tls10"), Lines(result, "DOSAI-CRYPTO-LEGACY-TLS"));
        Assert.Contains(LineOf(source, "m:tls11"), Lines(result, "DOSAI-CRYPTO-LEGACY-TLS"));
        Assert.DoesNotContain(LineOf(source, "m:tls12"), Lines(result, "DOSAI-CRYPTO-LEGACY-TLS"));
        Assert.Single(result.Protocols, protocol => protocol.Location.LineNumber == LineOf(source, "m:tls10"));
        Assert.Contains(result.Protocols, protocol => protocol.Location.LineNumber == LineOf(source, "m:tls10") && protocol is { Version: "TLS 1.0", Strength: "weak" });
        Assert.Single(result.Protocols, protocol => protocol.Location.LineNumber == LineOf(source, "m:tls11"));
        Assert.Contains(result.Protocols, protocol => protocol.Location.LineNumber == LineOf(source, "m:tls11") && protocol is { Version: "TLS 1.1", Strength: "legacy" });

        var disabled = Lines(result, "DOSAI-CRYPTO-TLS-CERT-VALIDATION-DISABLED");
        var custom = Lines(result, "DOSAI-CRYPTO-TLS-CERT-VALIDATION-CUSTOM");
        Assert.Contains(LineOf(source, "m:always"), disabled);
        Assert.DoesNotContain(LineOf(source, "m:always"), custom);
        Assert.Contains(LineOf(source, "m:delegate"), disabled);
        Assert.Contains(LineOf(source, "m:custom"), custom);
        Assert.DoesNotContain(LineOf(source, "m:custom"), disabled);

        Assert.Contains(LineOf(source, "m:pkcs1"), Lines(result, "DOSAI-CRYPTO-RSA-PKCS1-ENCRYPTION"));
        Assert.DoesNotContain(LineOf(source, "m:oaep"), Lines(result, "DOSAI-CRYPTO-RSA-PKCS1-ENCRYPTION"));
        var weakKeys = Lines(result, "DOSAI-CRYPTO-WEAK-KEY-SIZE");
        Assert.Contains(LineOf(source, "m:rsa1024"), weakKeys);
        Assert.DoesNotContain(LineOf(source, "m:rsa3072"), weakKeys);
        Assert.Contains(LineOf(source, "m:keysize"), weakKeys);
        Assert.Contains(LineOf(source, "m:curve"), weakKeys);
        Assert.Contains(LineOf(source, "m:ecb"), Lines(result, "DOSAI-CRYPTO-ECB-MODE"));
        Assert.Contains(LineOf(source, "m:compare"), Lines(result, "DOSAI-CRYPTO-NON-CONSTANT-TIME-COMPARE"));
        Assert.DoesNotContain(LineOf(source, "m:fixed"), Lines(result, "DOSAI-CRYPTO-NON-CONSTANT-TIME-COMPARE"));
        Assert.Contains(LineOf(source, "m:jwt"), Lines(result, "DOSAI-CRYPTO-JWT-VALIDATION-DISABLED"));

        string CweOf(string rule) => result.Findings.First(finding => finding.RuleId == rule).Cwe!;
        Assert.Equal("CWE-295", CweOf("DOSAI-CRYPTO-TLS-CERT-VALIDATION-DISABLED"));
        Assert.Equal("CWE-780", CweOf("DOSAI-CRYPTO-RSA-PKCS1-ENCRYPTION"));
        Assert.Equal("CWE-326", CweOf("DOSAI-CRYPTO-WEAK-KEY-SIZE"));
        Assert.Equal("CWE-208", CweOf("DOSAI-CRYPTO-NON-CONSTANT-TIME-COMPARE"));
        Assert.Equal("CWE-347", CweOf("DOSAI-CRYPTO-JWT-VALIDATION-DISABLED"));
    }

    [Fact]
    public void ObsoleteCryptoApis_NameTheirDiagnostic()
    {
        var source = Usings + """
#pragma warning disable SYSLIB0021, SYSLIB0022, SYSLIB0023, SYSLIB0045, SYSLIB0053, SYSLIB0057
public static class Obsolete
{
    public static object Managed() => new SHA256Managed(); // m:0021
    public static object Rijndael() => new RijndaelManaged(); // m:0022
    public static object Rng() => new RNGCryptoServiceProvider(); // m:0023
    public static object ByName() => HashAlgorithm.Create("SHA256")!; // m:0045
    public static object Gcm(byte[] key) => new AesGcm(key); // m:0053
    public static object GcmTag(byte[] key) => new AesGcm(key, 16); // m:gcmtag
    public static object Certificate(byte[] raw) => new X509Certificate2(raw); // m:0057
}
""";
        var result = Analyze(source);
        string? DiagnosticAt(string marker) => result.Findings
            .FirstOrDefault(finding => finding.RuleId == "DOSAI-CRYPTO-OBSOLETE-API" && finding.Location.LineNumber == LineOf(source, marker))
            ?.Properties.GetValueOrDefault("diagnostic");

        Assert.Equal("SYSLIB0021", DiagnosticAt("m:0021"));
        Assert.Equal("SYSLIB0022", DiagnosticAt("m:0022"));
        Assert.Equal("SYSLIB0023", DiagnosticAt("m:0023"));
        Assert.Equal("SYSLIB0045", DiagnosticAt("m:0045"));
        Assert.Equal("SYSLIB0053", DiagnosticAt("m:0053"));
        Assert.Null(DiagnosticAt("m:gcmtag"));
        Assert.Equal("SYSLIB0057", DiagnosticAt("m:0057"));
        Assert.All(result.Findings.Where(finding => finding.RuleId == "DOSAI-CRYPTO-OBSOLETE-API"), finding => Assert.Equal("CWE-477", finding.Cwe));
    }

    [Fact]
    public void CycloneDxPrimitive_FollowsTheAlgorithm()
    {
        using var fixture = new TempDir();
        File.WriteAllText(System.IO.Path.Combine(fixture.Path, "Primitives.cs"), Usings + """
#pragma warning disable SYSLIB5006
public static class Primitives
{
    public static object Kem() => MLKem.GenerateKey(MLKemAlgorithm.MLKem768);
    public static object Gcm(byte[] key) => new AesGcm(key, 16);
    public static object Block() => Aes.Create();
    public static byte[] Random() => RandomNumberGenerator.GetBytes(16);
    public static object Agreement() => ECDiffieHellman.Create();
}
""");
        var cdx = CryptoAnalyzer.GetCryptoAnalysis(fixture.Path, "cyclonedx");
        using var document = JsonDocument.Parse(cdx);
        var primitives = document.RootElement.GetProperty("components").EnumerateArray()
            .Where(component => component.GetProperty("type").GetString() == "cryptographic-asset" &&
                                component.GetProperty("cryptoProperties").TryGetProperty("algorithmProperties", out var properties) && properties.ValueKind == JsonValueKind.Object)
            .GroupBy(component => component.GetProperty("name").GetString()!)
            .ToDictionary(group => group.Key, group => group.First().GetProperty("cryptoProperties").GetProperty("algorithmProperties").GetProperty("primitive").GetString());

        Assert.Equal("kem", primitives["ML-KEM"]);
        Assert.Equal("ae", primitives["AES-GCM"]);
        Assert.Equal("block-cipher", primitives["AES"]);
        Assert.Equal("drbg", primitives["CSPRNG"]);
        Assert.Equal("key-agree", primitives["ECDH"]);
    }
}
