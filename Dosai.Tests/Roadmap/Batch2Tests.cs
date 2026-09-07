using System.Text.Json;
using Depscan;
using Depscan.Frameworks;
using Xunit;
using static Dosai.Tests.Roadmap.RoadmapStubs;

namespace Dosai.Tests.Roadmap;

/// <summary>
///     Core cross-cutting analyses: the multi-level summary fixpoint, the per-node
///     reachability index, exploit chains, weakness packs, endpoint security findings,
///     and MCP transport integrity.
/// </summary>
public class Batch2Tests
{
    // ----- Multi-level summary fixpoint -----

    [Fact]
    public void DataFlows_WrapperWrapperSinkChain_AttributesSinkToOutermostMethod()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("Chain.cs", """
using System.Diagnostics;

public static class Chain
{
    public static void Outer(string input) => Middle(input);
    private static void Middle(string arg) => Inner(arg);
    private static void Inner(string arg) => Process.Start("sh", arg);
}
""");
        var result = directory.DataFlows();

        // Before the fixpoint, summaries never absorbed callee summaries, so the sink stopped
        // at Inner's frame; the outermost wrapper (the one an entry point actually calls) saw nothing.
        Assert.Contains(result.MethodSummaries, summary => summary.Method.Contains("Chain.Outer") && summary.SinkParameterIndexes.Contains(0));
        Assert.Contains(result.MethodSummaries, summary => summary.Method.Contains("Chain.Middle") && summary.SinkParameterIndexes.Contains(0));
        // The slice's source lives in Outer's frame: the outermost method's parameter.
        var outerSource = result.Nodes.FirstOrDefault(node => node is { IsSource: true } && node.MethodName == "Outer");
        Assert.NotNull(outerSource);
        Assert.Contains(result.Slices, slice => slice.SourceId == outerSource!.Id && slice.SinkCategory == "command");
        // Multi-hop taint kinds survive summary propagation.
        Assert.Contains(result.MethodSummaries, summary => summary.Method.Contains("Chain.Outer") && summary.TaintKinds.Count > 0);
    }

    [Fact]
    public void DataFlows_MutuallyRecursivePair_FixpointTerminates()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("Recursion.cs", """
using System.Diagnostics;

public static class Recursion
{
    public static void Even(string input, int n) { if (n == 0) { Sink(input); } else { Odd(input, n - 1); } }
    private static void Odd(string input, int n) => Even(input, n - 1);
    private static void Sink(string arg) => Process.Start("sh", arg);
}
""");
        var result = directory.DataFlows();

        // The fixpoint must converge on a cycle: both mutally-recursive members eventually carry
        // the sink summary, and analysis completes rather than diverging.
        Assert.Contains(result.MethodSummaries, summary => summary.Method.Contains("Recursion.Even") && summary.SinkParameterIndexes.Contains(0));
        Assert.Contains(result.MethodSummaries, summary => summary.Method.Contains("Recursion.Odd") && summary.SinkParameterIndexes.Contains(0));
    }

    // ----- Core reachability index -----

    [Fact]
    public void Methods_ReachabilityIndex_ComputesEntryPointsAndDepths()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("Linear.cs", """
public static class Linear
{
    public static void Main()
    {
        A();
        Unrelated();
    }
    public static void A() => B();
    public static void B() => C();
    public static void C() { }
    public static void Unrelated() { }
}
""");
        var slice = directory.Methods();
        var reachability = slice.Reachability!.ToDictionary(facts => facts.NodeId, StringComparer.Ordinal);
        var mainEntry = Assert.Single(slice.EntryPoints!, entryPoint => entryPoint.Kind == "Cli");
        var mainFacts = reachability.First(pair => pair.Key.Contains("Linear.Main")).Value;
        var aFacts = reachability.First(pair => pair.Key.Contains("Linear.A")).Value;
        var bFacts = reachability.First(pair => pair.Key.Contains("Linear.B")).Value;
        var cFacts = reachability.First(pair => pair.Key.Contains("Linear.C")).Value;
        var siblingFacts = reachability.First(pair => pair.Key.Contains("Linear.Unrelated")).Value;

        Assert.Contains(mainEntry.Id, aFacts.ReachableEntryPoints);
        Assert.Equal(1, aFacts.DepthFromEntryPoint);
        Assert.Equal(2, bFacts.DepthFromEntryPoint);
        Assert.Equal(3, cFacts.DepthFromEntryPoint);
        // The sibling is reachable from Main directly (it IS called by Main), but nothing deeper.
        Assert.Contains(mainEntry.Id, siblingFacts.ReachableEntryPoints);
    }

    [Fact]
    public void Methods_ReachabilityIndex_UnreachedNodeHasNoEntryPoint()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("Orphan.cs", """
public static class Orphan
{
    public static void Main() { }
    private static void NeverCalled(string input) => System.Diagnostics.Process.Start("sh", input);
}
""");
        var slice = directory.Methods();
        var orphanFacts = slice.Reachability!.First(facts => facts.NodeId.Contains("Orphan.NeverCalled"));

        Assert.Empty(orphanFacts.ReachableEntryPoints);
        Assert.Null(orphanFacts.DepthFromEntryPoint);
    }

    // ----- Exploit chains -----

    [Fact]
    public void DataFlows_ControllerHelperSinkChain_ProducesExploitChain()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("Cmd.cs", MvcStubs + """

public static class Executor
{
    public static string Spawn(string arg) => System.Diagnostics.Process.Start("sh", arg)?.ToString() ?? "";
}

[Route("api/cmd")]
public class CmdController : Microsoft.AspNetCore.Mvc.ControllerBase
{
    [HttpGet("run")]
    public string Run(string input) => Executor.Spawn(input);
}
""");
        var result = directory.DataFlows();

        var chain = Assert.Single(result.ExploitChains);
        // Chain resolves the entry point BY GRAPH (route → action → helper → sink), not by file/line.
        Assert.Equal("anonymous-http", chain.Exposure);
        Assert.Contains(chain.CallPath, id => id.Contains("CmdController.Run", StringComparison.Ordinal));
        Assert.Contains(chain.CallPath, id => id.Contains("Executor.Spawn", StringComparison.Ordinal));
        Assert.Equal("command", result.Slices.First(slice => slice.Id == chain.SliceId).SinkCategory);
        Assert.Contains(result.WeaknessCandidates, weakness =>
            weakness.SinkCategory == "command" &&
            weakness.EntryPointId == chain.EntryPointId &&
            weakness.ConfidenceReasons.Any(reason => reason.Contains("reachable from anonymous-http", StringComparison.Ordinal)));
        // R2 also populates the previously dead EntryPointIds field.
        Assert.Contains(result.DangerousApiReachability, api => api.EntryPointIds.Contains(chain.EntryPointId));
    }

    [Fact]
    public void DataFlows_SanitizedFlow_YieldsNoExploitChain()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("Safe.cs", MvcStubs + """

[Route("api/safe")]
public class SafeController : Microsoft.AspNetCore.Mvc.ControllerBase
{
    [HttpGet("run")]
    public string Run(string input) => System.Diagnostics.Process.Start("sh", System.Net.WebUtility.HtmlEncode(input))?.ToString() ?? "";
}
""");
        var result = directory.DataFlows();

        Assert.Empty(result.ExploitChains);
        Assert.DoesNotContain(result.Slices, slice => slice.SinkCategory == "command");
        Assert.Contains(result.SanitizedFlows, sanitized => sanitized.Kind == "SanitizerMatch");
    }

    // ----- XSS -----

    [Fact]
    public void DataFlows_HtmlRawXss_ProducesSliceAndCandidate()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("Xss.cs", HtmlStubs + """

public static class XssPage
{
    public static string RenderUnsafe(Microsoft.AspNetCore.Html.IHtmlHelper html, string input) => html.Raw(input).ToString();

    public static string RenderSafe(Microsoft.AspNetCore.Html.IHtmlHelper html, string input) => html.Raw(System.Net.WebUtility.HtmlEncode(input)).ToString();
}
""");
        var result = directory.DataFlows();

        Assert.Contains(result.Slices, slice => slice.SinkCategory == "xss");
        Assert.Contains(result.WeaknessCandidates, weakness => weakness is { Kind: "XssCandidate", Cwe: "CWE-79", Severity: "high" });
        // The paired HtmlEncode mitigation suppresses the flow and leaves negative evidence.
        Assert.Contains(result.SanitizedFlows, sanitized => sanitized.Kind == "SanitizerMatch");
    }

    // ----- XXE -----

    [Fact]
    public void DataFlows_XmlDocumentTaintedInput_ProducesXxeSlice()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("Xxe.cs", """
using System.Xml;

public static class XxeFlow
{
    public static void Parse(string input)
    {
        var doc = new XmlDocument();
        doc.LoadXml(input);
    }
}
""");
        var result = directory.DataFlows();

        Assert.Contains(result.Slices, slice => slice.SinkCategory == "xxe");
        Assert.Contains(result.WeaknessCandidates, weakness => weakness is { Kind: "XxeCandidate", Cwe: "CWE-611" });
    }

    [Fact]
    public void DataFlows_HardenedXmlReaderSettings_SuppressesXxeFlow()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("XxeHardened.cs", """
using System.Xml;

public static class XxeHardened
{
    // Idiomatic cross-statement hardening: the settings object is hardened in an earlier
    // statement, which the reader-creation expression text cannot show.
    public static void ParseHardenedAhead(string input)
    {
        var settings = new XmlReaderSettings();
        settings.XmlResolver = null;
        using var reader = XmlReader.Create(input, settings);
    }

    // Inline hardening, spacing-tolerant (no spaces around =).
    public static void ParseHardenedInline(string input)
    {
        using var reader = XmlReader.Create(input, new XmlReaderSettings { XmlResolver=null });
    }
}
""");
        var result = directory.DataFlows();

        // Hardening markers suppress the XXE finding both inline (spacing-tolerant) and
        // when the hardened settings instance was prepared in an earlier statement.
        Assert.DoesNotContain(result.Slices, slice => slice.SinkCategory == "xxe");
        Assert.Contains(result.SanitizedFlows, sanitized => sanitized.SanitizerCategory == "xxe-hardening");
    }

    [Fact]
    public void DataFlows_IlMode_XxeSliceIsConfidenceDowngraded()
    {
        using var directory = new RoadmapTemporaryDirectory();
        var binaryDirectory = directory.BuildTemporaryProject("IlXxe", """
using System.Xml;

public static class Program
{
    public static void Main(string[] args)
    {
        var doc = new XmlDocument();
        doc.LoadXml(args[0]);
    }
}
""");
        var result = DataFlowAnalyzer.Analyze(binaryDirectory);

        // The XXE guard markers and the hardened-symbol map are source-mode only, so an IL-mode
        // XXE flow cannot know whether the parser was hardened: it reports Low confidence (which
        // demotes the category's high severity one rank) and says so in the summary, instead of
        // asserting an unhardened resolver it never observed.
        var xxeSlice = Assert.Single(result.Slices, slice => slice.SinkCategory == "xxe");
        Assert.Equal("Low", xxeSlice.Confidence);
        Assert.Equal("medium", xxeSlice.Severity);
        Assert.Contains("not observable in IL", xxeSlice.Summary, StringComparison.Ordinal);

        // Source-mode findings for the same shape keep their normal confidence (the guards apply).
        directory.WriteSource("SourceXxe.cs", """
using System.Xml;

public static class SourceXxeFlow
{
    public static void Parse(string input)
    {
        var doc = new XmlDocument();
        doc.LoadXml(input);
    }
}
""");
        var sourceResult = directory.DataFlows();
        Assert.Contains(sourceResult.Slices, slice => slice.SinkCategory == "xxe" && slice.Confidence != "Low");
    }

    // ----- LDAP / XPath / NoSQL -----

    [Fact]
    public void DataFlows_LdapXPathNoSql_EachProduceSlices()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("Injections.cs", DirectoryServicesStubs + MongoStubs + """

public static class InjectionTargets
{
    public static void Ldap(string input) { var searcher = new System.DirectoryServices.DirectorySearcher(input); }

    public static void XPath(string input)
    {
        var doc = new System.Xml.XmlDocument();
        doc.SelectNodes(input);
    }

    public static void NoSql(string input) { var query = MongoDB.Bson.BsonDocument.Parse(input); }
}
""");
        var result = directory.DataFlows();

        Assert.Contains(result.Slices, slice => slice.SinkCategory == "ldap");
        Assert.Contains(result.WeaknessCandidates, weakness => weakness is { Kind: "LdapInjectionCandidate", Cwe: "CWE-90" });
        Assert.Contains(result.Slices, slice => slice.SinkCategory == "xpath");
        Assert.Contains(result.WeaknessCandidates, weakness => weakness is { Kind: "XPathInjectionCandidate", Cwe: "CWE-643" });
        Assert.Contains(result.Slices, slice => slice.SinkCategory == "nosql");
        Assert.Contains(result.WeaknessCandidates, weakness => weakness is { Kind: "NoSqlInjectionCandidate", Cwe: "CWE-943" });
    }

    // ----- Log / header injection -----

    [Fact]
    public void DataFlows_LoggerAndHeaderSinks_ProduceSlicesWithSeverity()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("Logging.cs", LoggingStubs + HtmlStubs + """

public class ResponseHeaders { public Microsoft.AspNetCore.Http.IHeaderDictionary Headers { get; set; } }

public static class LogFlow
{
    public static void Log(Microsoft.Extensions.Logging.ILogger logger, ResponseHeaders response, string input)
    {
        logger.LogError("failed for " + input);
        response.Headers.Append("X-Trace", input);
    }
}
""");
        var result = directory.DataFlows();

        // T5/#10: the log category defaults to Low severity AND its patterns are Low-confidence,
        // so the confidence demotion drops it one more rank to info, heuristic log matches must
        // never trip a "new high-severity" CI gate.
        Assert.Contains(result.Slices, slice => slice is { SinkCategory: "log", Severity: "info" });
        Assert.Contains(result.WeaknessCandidates, weakness => weakness is { Kind: "LogInjectionCandidate", Cwe: "CWE-117", Severity: "info" });
        Assert.Contains(result.Slices, slice => slice is { SinkCategory: "header", Severity: "medium" });
        Assert.Contains(result.WeaknessCandidates, weakness => weakness is { Kind: "HeaderInjectionCandidate", Cwe: "CWE-113" });
    }

    // ----- ReDoS -----

    [Fact]
    public void DataFlows_TaintedRegexPattern_ProducesRedosSlice()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("Redos.cs", """
using System.Text.RegularExpressions;

public static class RedosFlow
{
    public static bool Check(string input) => Regex.IsMatch(input, input);
}
""");
        var result = directory.DataFlows();

        Assert.Contains(result.Slices, slice => slice.SinkCategory == "redos");
        Assert.Contains(result.WeaknessCandidates, weakness => weakness is { Kind: "ReDoSCandidate", Cwe: "CWE-1333" });
    }

    [Fact]
    public void DataFlows_CatastrophicLiteralPattern_ProducesCwe1333Candidates()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("RedosLiteral.cs", """
using System;
using System.Text.RegularExpressions;

public static class RedosLiteral
{
    public static Regex Slow() => new Regex("(a+)*$");
    public static bool SlowStaticMatch(string subject) => Regex.IsMatch(subject, "(a|aa)+$");
    public static Regex Fine() => new Regex("^[a-z]+$");
    public static Regex NonBacktracking() => new Regex("(a+)*$", RegexOptions.NonBacktracking);
    public static Regex Bounded() => new Regex("(a+)*$", TimeSpan.FromSeconds(1));
}
""");
        var result = directory.DataFlows();

        // Catastrophic literal patterns are CWE-1333 weakness candidates with locations:
        // not prose in Diagnostics, so suppressions, diff, and downstream tools see them.
        var redos = result.WeaknessCandidates.Where(weakness => weakness.Kind == "ReDoSCandidate").ToList();
        Assert.Equal(2, redos.Count);
        Assert.All(redos, weakness =>
        {
            Assert.Equal("CWE-1333", weakness.Cwe);
            Assert.Contains("RedosLiteral.cs", weakness.SinkLocation);
        });
        Assert.Contains(redos, weakness => weakness.Evidence.Any(evidence => evidence.Contains("(a+)*", StringComparison.Ordinal)));
        Assert.Contains(redos, weakness => weakness.Evidence.Any(evidence => evidence.Contains("(a|aa)", StringComparison.Ordinal)));
        // The benign pattern produces nothing, and the real mitigations (NonBacktracking,
        // match timeout) are honored.
        Assert.DoesNotContain(redos, weakness => weakness.SourceLocation?.Contains("Fine", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(redos, weakness => weakness.SourceLocation?.Contains("NonBacktracking", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(redos, weakness => weakness.SourceLocation?.Contains("Bounded", StringComparison.Ordinal) == true);
    }

    // ----- Endpoint security findings -----

    [Fact]
    public void Methods_SensitiveAnonymousEndpoint_FlaggedHighSeverity()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("Profile.cs", MvcStubs + """

public class ProfileDto
{
    public string Email { get; set; }
    public string FullName { get; set; }
}

[Route("api/profile")]
public class ProfileController : Microsoft.AspNetCore.Mvc.ControllerBase
{
    [HttpPost]
    [Microsoft.AspNetCore.Mvc.AllowAnonymous]
    public ProfileDto Save([Microsoft.AspNetCore.Mvc.FromBody] ProfileDto dto) => dto;
}
""");
        var slice = directory.Methods();

        var finding = Assert.Single(slice.SecurityFindings!, candidate => candidate.Kind == "SensitiveUnauthenticatedEndpoint");
        Assert.Equal("high", finding.Severity);
        Assert.Equal("CWE-306", finding.Cwe);
        Assert.Equal("pii", finding.Properties.GetValueOrDefault("classification") ?? "pii");
        Assert.NotNull(finding.Remediation);
    }

    [Fact]
    public void Methods_CorsWildcardWithCredentials_Flagged()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("Cors.cs", MvcStubs + """

public static class CorsSetup
{
    public static void AddPolicies(IServiceCollection services) => services.AddCors("wide", policy => policy.AllowAnyOrigin().AllowCredentials());
}
""");
        var slice = directory.Methods();

        var finding = Assert.Single(slice.SecurityFindings!, candidate => candidate.Kind == "CorsWildcardWithCredentials");
        Assert.Equal("CWE-942", finding.Cwe);
        Assert.Contains("wide", finding.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void Methods_MutatingEndpointWithoutAntiforgery_Flagged()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("Antiforgery.cs", MvcStubs + """

[Route("api/items")]
public class ItemsController : Microsoft.AspNetCore.Mvc.ControllerBase
{
    [HttpPost]
    public string Save([Microsoft.AspNetCore.Mvc.FromBody] string value) => value;
}
""");
        var slice = directory.Methods();

        Assert.Contains(slice.SecurityFindings!, finding => finding is { Kind: "StateChangingEndpointWithoutAntiforgery", Cwe: "CWE-352", Severity: "low" });
    }

    [Fact]
    public void Methods_DuplicateRouteWithMixedAuth_Flagged()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("Dup.cs", MvcStubs + """

[Route("api/dup")]
public class DupAnonymousController : Microsoft.AspNetCore.Mvc.ControllerBase
{
    [HttpGet]
    [Microsoft.AspNetCore.Mvc.AllowAnonymous]
    public string Get() => "anon";
}

[Route("api/dup")]
public class DupProtectedController : Microsoft.AspNetCore.Mvc.ControllerBase
{
    [HttpGet]
    [Microsoft.AspNetCore.Mvc.Authorize]
    public string Get() => "protected";
}
""");
        var slice = directory.Methods();

        var finding = Assert.Single(slice.SecurityFindings!, candidate => candidate.Kind == "DuplicateRouteAuthMismatch");
        Assert.Equal("CWE-306", finding.Cwe);
    }

    // ----- MCP transport integrity -----

    [Fact]
    public void Methods_McpStdioTransport_RiskyLaunchFlagged()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("McpClient.cs", """
public static class McpClientConfig
{
    public static void Setup()
    {
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = "npx",
            Arguments = new[] { "-y", "some.scraper.server" }
        });
        _ = McpClientFactory.CreateAsync(transport);
    }
}
""");
        var slice = directory.Methods();

        Assert.Contains(slice.SecurityFindings!, finding => finding is { Kind: "McpTransportRisk", Severity: "medium" } && finding.Title.Contains("unversioned", StringComparison.Ordinal));
    }

    [Fact]
    public void McpTransportAnalyzer_AllowlistSuppressesAndCleanLaunchStaysClean()
    {
        var services = new List<ServiceComponent>
        {
            new()
            {
                Id = "svc:mcp:mcp-client-custom-runner",
                ServiceKind = ServiceKinds.Mcp,
                Direction = ServiceDirections.Outbound,
                Properties = { ["transport"] = "stdio", ["command"] = "custom-runner", ["arguments"] = "start --config x.json" }
            },
            new()
            {
                Id = "svc:mcp:mcp-client-dotnet",
                ServiceKind = ServiceKinds.Mcp,
                Direction = ServiceDirections.Outbound,
                Properties = { ["transport"] = "stdio", ["command"] = "dotnet", ["arguments"] = "run --project ./tools/mcp" }
            }
        };

        var findings = new List<SecurityFinding>();
        McpTransportAnalyzer.Assess(services, findings, new SecurityAnalyzer.FindingIds("mcp-transport"), null);
        // Without an allowlist: the unknown runner is flagged, the dotnet launch is clean.
        Assert.Contains(findings, finding => finding.Title.Contains("custom-runner"));
        Assert.DoesNotContain(findings, finding => finding.Title.Contains("dotnet"));
        // Ids are content-derived (kind+file+line), stable across runs and finding order.
        Assert.All(findings, finding => Assert.StartsWith("mcp-transport-", finding.Id));

        findings.Clear();
        McpTransportAnalyzer.Assess(services, findings, new SecurityAnalyzer.FindingIds("mcp-transport"), new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "custom-runner" });
        // With the allowlist: the policy-approved command is not flagged.
        Assert.DoesNotContain(findings, finding => finding.Title.Contains("custom-runner"));
    }
}
