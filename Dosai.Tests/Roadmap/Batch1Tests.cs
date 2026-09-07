using System.Reflection;
using System.Text.Json;
using Depscan;
using Xunit;
using static Dosai.Tests.Roadmap.RoadmapStubs;

namespace Dosai.Tests.Roadmap;

/// <summary>Batch 1 quick wins: T10 slice dedup, T11 budget diagnostics, T12 validator fix, W6/W7 pattern hygiene, R4/R7/R9 graph facts, O3 drift guard.</summary>
public class Batch1Tests
{
    // ----- T10: source-mode slice deduplication -----

    [Fact]
    public void DataFlows_RepeatedSinkMatch_ProducesSingleSlice()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("Dedup.cs", """
using System.IO;

public static class Dedup
{
    public static void Run(string input)
    {
        File.WriteAllText(input + ".txt", input);
        File.WriteAllText(input + ".txt", input);
    }
}
""");
        var result = directory.DataFlows();

        var fileSlices = result.Slices.Where(slice => slice.SinkCategory == "file").ToList();
        // T10: the same (source, sink, category, argument index) tuple appended once, not once per
        // duplicated statement evaluation.
        Assert.Single(fileSlices, slice => slice.SinkArgumentIndex == 1 && (slice.SinkArgument ?? string.Empty).Contains("input"));
    }

    // ----- T11: budget-exhaustion diagnostics -----

    [Fact]
    public void DataFlows_DeepNesting_EmitsWalkerBudgetDiagnostic()
    {
        using var directory = new RoadmapTemporaryDirectory();
        // 260 nested binary operations: past the 200-level walker budget.
        var expression = "input";
        for (var depth = 0; depth < 260; depth++)
        {
            expression = $"({expression} + \"x\")";
        }

        directory.WriteSource("Deep.cs", $$"""
using System.IO;

public static class Deep
{
    public static void Run(string input) => File.WriteAllText("out.txt", {{expression}});
}
""");
        var result = directory.DataFlows();

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("nesting budget", StringComparison.Ordinal));
    }

    // ----- T12: validator over-approximation fix -----

    [Fact]
    public void DataFlows_ValidatorBooleanReturn_NoLongerPropagatesTaint()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("Validator.cs", """
using System.IO;

public static class Validator
{
    public static void Guarded(string input)
    {
        if (IsValid(input))
        {
            File.WriteAllText("ok.txt", input);
        }
    }

    public static void DecisionOnly(string input)
    {
        var ok = IsValid(input);
        File.WriteAllText("ok.txt", ok.ToString());
    }

    private static bool IsValid(string value) => value.Length > 0;
}
""");
        var result = directory.DataFlows();

        // The guard path is unaffected: taint still flows to the sink inside the checked branch.
        Assert.Contains(result.Slices, slice => slice is { SinkCategory: "file", SinkArgument: not null } && slice.SinkArgument.Contains("input"));
        // T12: consuming a bool validator's RESULT no longer taints the sink argument — the
        // "tainted true" over-approximation minted phantom flows into every checked-value use.
        Assert.DoesNotContain(result.Slices, slice => slice.SinkArgument is not null && slice.SinkArgument.Contains("ok.ToString()"));
        Assert.DoesNotContain(result.Slices, slice => slice.SinkArgument is not null && slice.SinkArgument.Contains("ok"));
    }

    // ----- W7: pattern hygiene -----

    [Fact]
    public void DataFlows_ListAddNoLongerSanitizes_SqlFlowSurvives()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("ListAdd.cs", """
using System.Collections.Generic;

public class DbCommand { public int ExecuteNonQuery(string sql) => 0; }

public static class ListAddFlow
{
    public static void Run(string input)
    {
        var values = new List<string>();
        values.Add(input);
        new DbCommand().ExecuteNonQuery("SELECT * FROM t WHERE n = '" + values[0] + "'");
    }
}
""");
        var result = directory.DataFlows();

        // W7 regression: a bare Name/Exact "Add" sanitizer matched List<T>.Add and masked the SQL
        // flow; only provider parameter collections parameterize now, and storing a tainted value
        // taints the collection so the read-back still reaches the sink.
        Assert.Contains(result.Slices, slice => slice.SinkCategory == "sql");
    }

    [Fact]
    public void DataFlows_KeySecretWordBoundaries_MonkeyNotCryptoSource()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("Noise.cs", """
using System.IO;

public static class Noise
{
    public static void Run(string monkey, string secretaryNote, string apiKey, string clientSecret, string session_key, string hmacKey)
    {
        File.WriteAllText("a.txt", monkey);
        File.WriteAllText("b.txt", secretaryNote);
        File.WriteAllText("c.txt", apiKey);
        File.WriteAllText("d.txt", clientSecret);
        File.WriteAllText("e.txt", session_key);
        File.WriteAllText("f.txt", hmacKey);
    }
}
""");
        var result = directory.DataFlows();

        Assert.DoesNotContain(result.Nodes, node => node.IsSource && node.Category == "crypto-material" && node.Name == "monkey");
        Assert.DoesNotContain(result.Nodes, node => node.IsSource && node.Name == "secretaryNote");
        // W7: bare "key"/"keys" names (every KeyValuePair iteration, every cache dictionary)
        // no longer mint crypto-material sources — only compound identifiers do.
        Assert.DoesNotContain(result.Nodes, node => node.IsSource && node.Name is "key" or "keys");
        // Compound key/secret identifiers are still recognized, across camelCase, PascalCase,
        // and separator forms.
        Assert.Contains(result.Nodes, node => node.IsSource && node.Category == "crypto-material" && node.Name == "apiKey");
        Assert.Contains(result.Nodes, node => node.IsSource && node.Category == "crypto-material" && node.Name == "hmacKey");
        Assert.Contains(result.Nodes, node => node.IsSource && node.Category == "crypto-material" && node.Name == "session_key");
        Assert.Contains(result.Nodes, node => node.IsSource && node.Category == "secret" && node.Name == "clientSecret");
    }

    // ----- W6: CWE mappings for previously unmapped categories -----

    [Fact]
    public void WeaknessMappings_CoverCryptoFamilyAndNewCategories()
    {
        var weaknessKind = typeof(TransparencyBuilder).GetMethod("WeaknessKind", BindingFlags.NonPublic | BindingFlags.Static);
        var weaknessCwe = typeof(TransparencyBuilder).GetMethod("WeaknessCwe", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(weaknessKind);
        Assert.NotNull(weaknessCwe);

        // W6: existing sink categories stop collapsing into DangerousDataFlowCandidate with no CWE.
        Assert.Equal("InsecureCryptoUsageCandidate", weaknessKind!.Invoke(null, ["crypto"]));
        Assert.Equal("CWE-327", weaknessCwe!.Invoke(null, ["InsecureCryptoUsageCandidate"]));
        Assert.Equal("JwtValidationCandidate", weaknessKind!.Invoke(null, ["jwt"]));
        Assert.Equal("CWE-345", weaknessCwe!.Invoke(null, ["JwtValidationCandidate"]));
        Assert.Equal("CertificateValidationCandidate", weaknessKind!.Invoke(null, ["certificate"]));
        Assert.Equal("CWE-295", weaknessCwe!.Invoke(null, ["CertificateValidationCandidate"]));
        Assert.Equal("TlsValidationCandidate", weaknessKind!.Invoke(null, ["tls"]));
        Assert.Equal("CWE-295", weaknessCwe!.Invoke(null, ["TlsValidationCandidate"]));
        Assert.Equal("McpEgressCandidate", weaknessKind!.Invoke(null, ["mcp-egress"]));
        Assert.Equal("CWE-1427", weaknessCwe!.Invoke(null, ["McpEgressCandidate"]));
        // W1–W5/W8 classes.
        Assert.Equal("XssCandidate", weaknessKind!.Invoke(null, ["xss"]));
        Assert.Equal("CWE-79", weaknessCwe!.Invoke(null, ["XssCandidate"]));
        Assert.Equal("XxeCandidate", weaknessKind!.Invoke(null, ["xxe"]));
        Assert.Equal("CWE-611", weaknessCwe!.Invoke(null, ["XxeCandidate"]));
        Assert.Equal("LdapInjectionCandidate", weaknessKind!.Invoke(null, ["ldap"]));
        Assert.Equal("CWE-90", weaknessCwe!.Invoke(null, ["LdapInjectionCandidate"]));
        Assert.Equal("XPathInjectionCandidate", weaknessKind!.Invoke(null, ["xpath"]));
        Assert.Equal("CWE-643", weaknessCwe!.Invoke(null, ["XPathInjectionCandidate"]));
        Assert.Equal("NoSqlInjectionCandidate", weaknessKind!.Invoke(null, ["nosql"]));
        Assert.Equal("CWE-943", weaknessCwe!.Invoke(null, ["NoSqlInjectionCandidate"]));
        Assert.Equal("LogInjectionCandidate", weaknessKind!.Invoke(null, ["log"]));
        Assert.Equal("CWE-117", weaknessCwe!.Invoke(null, ["LogInjectionCandidate"]));
        Assert.Equal("HeaderInjectionCandidate", weaknessKind!.Invoke(null, ["header"]));
        Assert.Equal("CWE-113", weaknessCwe!.Invoke(null, ["HeaderInjectionCandidate"]));
        Assert.Equal("ReDoSCandidate", weaknessKind!.Invoke(null, ["redos"]));
        Assert.Equal("CWE-1333", weaknessCwe!.Invoke(null, ["ReDoSCandidate"]));
        Assert.Equal("TemplateInjectionCandidate", weaknessKind!.Invoke(null, ["template"]));
        Assert.Equal("CWE-1336", weaknessCwe!.Invoke(null, ["TemplateInjectionCandidate"]));
    }

    [Fact]
    public void DataFlows_CryptoSlice_GetsCweAndSeverity()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("CryptoSink.cs", """
using System.Security.Cryptography;

public static class CryptoSinkFlow
{
    public static byte[] Run(string input)
    {
        using var sha = SHA256.Create();
        return sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));
    }
}
""");
        var result = directory.DataFlows();

        // W6: the crypto pack's sink categories used to collapse to DangerousDataFlowCandidate
        // with no CWE; agent-context now shows the CWE for a crypto slice.
        Assert.Contains(result.WeaknessCandidates, weakness => weakness is { Kind: "InsecureCryptoUsageCandidate", Cwe: "CWE-327", Severity: "high" });
    }

    // ----- R4: sealed-type devirtualization + dispatch confidence -----

    [Fact]
    public void CallGraph_SealedReceiver_DevirtualizesExactly()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("SealedDispatch.cs", """
public abstract class HandlerBase
{
    public abstract string Execute(string command);
}

public sealed class SafeHandler : HandlerBase
{
    public override string Execute(string command) => command.Trim();
}

public interface IRunner
{
    void Run(string value);
}

public struct FastRunner : IRunner
{
    public void Run(string value) { }
}

public static class SealedDispatch
{
    // Struct receiver typed by its interface: the static type admits exactly one implementation.
    public static void RunViaSealed(FastRunner runner, string input) => runner.Run(input);

    // Abstract-base receiver: an open candidate set is the honest answer.
    public static string RunViaBase(HandlerBase handler, string input) => handler.Execute(input);

    public static void Instantiate() => _ = new SafeHandler();
}
""");
        var slice = directory.Methods();

        // R4: a struct/sealed-typed receiver binds to the concrete implementation directly (the
        // sealed-receiver exact branch in DispatchResolver guarantees no candidate set even when
        // inference runs); the call site shows a direct edge and no dispatch candidates.
        Assert.Contains(slice.CallGraph!.Edges, edge =>
            edge.SourceId.Contains("RunViaSealed", StringComparison.Ordinal) &&
            edge.TargetId.Contains("FastRunner.Run", StringComparison.Ordinal));
        Assert.DoesNotContain(slice.CallGraph!.Edges, edge => edge.SourceId.Contains("RunViaSealed", StringComparison.Ordinal) && edge.DispatchConfidence is not null);
        // The abstract-base call site stays a ranked candidate (never a bogus exact edge); the
        // instantiated implementation carries rta-candidate ahead of any cha-candidate.
        Assert.DoesNotContain(slice.CallGraph!.Edges, edge => edge.DispatchConfidence == "exact" && edge.SourceId.Contains("RunViaBase", StringComparison.Ordinal));
        Assert.Contains(slice.CallGraph!.Edges, edge =>
            edge.SourceId.Contains("RunViaBase", StringComparison.Ordinal) &&
            edge.TargetId.Contains("SafeHandler.Execute", StringComparison.Ordinal) &&
            edge.DispatchConfidence == "rta-candidate");
    }

    [Fact]
    public void CallGraph_OpenHierarchy_CandidatesRankedWithConfidence()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("Hierarchy.cs", """
public interface IGreeter { string Greet(string name); }
public class EnglishGreeter : IGreeter { public string Greet(string name) => "hi " + name; }
public class FrenchGreeter : IGreeter { public string Greet(string name) => "bonjour " + name; }

public static class Dispatch
{
    public static string Run(IGreeter greeter, string input)
    {
        var english = new EnglishGreeter();
        _ = english.Greet("x");
        return greeter.Greet(input);
    }
}
""");
        var slice = directory.Methods();

        var candidates = slice.CallGraph!.Edges
            .Where(edge => edge.TargetId.Contains("Greeter.Greet", StringComparison.Ordinal) && edge.DispatchConfidence is not null)
            .ToList();
        Assert.NotEmpty(candidates);
        // The instantiated implementation carries rta-candidate; the uninstantiated one stays cha-candidate.
        Assert.Contains(candidates, edge => edge.DispatchConfidence == "rta-candidate" && edge.TargetId.Contains("EnglishGreeter", StringComparison.Ordinal));
        Assert.Contains(candidates, edge => edge.DispatchConfidence == "cha-candidate" && edge.TargetId.Contains("FrenchGreeter", StringComparison.Ordinal));
    }

    // ----- R7: call-site counts and fan-in/fan-out -----

    [Fact]
    public void CallGraph_TwoCallSites_CollapseToOneEdgeWithCount()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("Sites.cs", """
public static class Sites
{
    public static void Main()
    {
        Helper("a");
        Helper("b");
    }

    private static void Helper(string value) { }
}
""");
        var slice = directory.Methods();

        var edge = Assert.Single(slice.CallGraph!.Edges, candidate =>
            candidate.SourceId.Contains("Sites.Main", StringComparison.Ordinal) &&
            candidate.TargetId.Contains("Sites.Helper", StringComparison.Ordinal));
        Assert.Equal(2, edge.CallSiteCount);
    }

    [Fact]
    public void Reachability_FanInFanOut_ComputedPerNode()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("Fan.cs", """
public static class Fan
{
    public static void Main()
    {
        A();
        B();
        C();
    }
    public static void A() => Sink();
    public static void B() => Sink();
    public static void C() => Sink();
    public static void Sink() { }
}
""");
        var slice = directory.Methods();
        var reachability = slice.Reachability!.ToDictionary(facts => facts.NodeId, StringComparer.Ordinal);

        var main = reachability.First(pair => pair.Key.Contains("Fan.Main")).Value;
        var sink = reachability.First(pair => pair.Key.Contains("Fan.Sink")).Value;
        Assert.Equal(3, main.FanOut);
        Assert.Equal(3, sink.FanIn);
    }

    // ----- R9: crypto reachability file fallback is gated (covered end-to-end by the crypto
    //           analysis test in DosaiTests; here we pin the budgeted reverse walk does not throw).

    [Fact]
    public void CryptoAnalysis_UnrelatedCryptoInEndpointFile_NotClaimedReachable()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("Mixed.cs", """
using System.Security.Cryptography;

public class MixedController
{
    [Microsoft.AspNetCore.Mvc.HttpGet("/health")]
    public string Health() => "ok";

    public static string Fingerprint(string value)
    {
        using var md5 = MD5.Create();
        return System.Convert.ToHexString(md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(value)));
    }
}
""" + MvcStubs);
        var result = CryptoAnalyzer.Analyze(directory.Path);

        // R9: Fingerprint sits in the same FILE as an endpoint but no graph path reaches it; the
        // old whole-file fallback claimed High-confidence reachability for it.
        var md5 = Assert.Single(result.Findings, finding => finding.RuleId == "DOSAI-CRYPTO-WEAK-HASH-MD5");
        Assert.False(md5.ReachableFromEntryPoint);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("matched only at file level", StringComparison.Ordinal));
    }

    // ----- O3: CLI/docs drift guard -----

    [Fact]
    public async Task CommandLine_PatternPackHelp_ListsEveryShippedPack()
    {
        var helpText = await CaptureHelpAsync("dataflows");
        foreach (var pack in DataFlowAnalyzer.DefaultPatternPackNames)
        {
            Assert.Contains(pack, helpText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void DataFlows_DefaultPatterns_PatternPacksMatchShippedList()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("Packs.cs", "public static class Packs { public static void Main() { } }");
        var result = directory.DataFlows();

        Assert.Equal(
            DataFlowAnalyzer.DefaultPatternPackNames.Order(StringComparer.OrdinalIgnoreCase),
            result.Patterns.PatternPacks.Order(StringComparer.OrdinalIgnoreCase));
    }

    private static async Task<string> CaptureHelpAsync(string command)
    {
        var original = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            _ = await Task.Run(() => CommandLine.Main([$"{command}", "--help"]));
        }
        finally
        {
            Console.SetOut(original);
        }

        return writer.ToString();
    }
}
