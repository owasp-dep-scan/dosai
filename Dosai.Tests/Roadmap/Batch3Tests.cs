using System.Text.Json;
using System.Text.Json.Serialization;
using Depscan;
using Xunit;
using static Dosai.Tests.Roadmap.RoadmapStubs;

namespace Dosai.Tests.Roadmap;

/// <summary>
///     Depth and breadth: lambda and async taint modeling, sanitizer evidence, severity and
///     suppressions, IL sanitizer guards, recursion clusters, generic-id normalization, the
///     Orleans provider, configuration analysis, PURL source expansion, and the richer diff.
/// </summary>
public class Batch3Tests
{
    // ----- Lambda / anonymous function taint -----

    [Fact]
    public void DataFlows_TaintedForEachLambda_SeedParameterReachesSink()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("Lambda.cs", """
using System.Collections.Generic;
using System.Diagnostics;

public static class LambdaFlow
{
    public static void Run(List<string> input)
    {
        input.ForEach(item => Process.Start("sh", item));
    }
}
""");
        var result = directory.DataFlows();

        Assert.Contains(result.Slices, slice => slice.SinkCategory == "command");
    }

    [Fact]
    public void DataFlows_DelegateVariableInvocation_PropagatesArgumentTaint()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("Delegate.cs", """
using System;
using System.IO;

public static class DelegateFlow
{
    public static void Run(string input)
    {
        Func<string, string> transform = value => value.Trim();
        File.WriteAllText("out.txt", transform(input));
    }
}
""");
        var result = directory.DataFlows();

        // Invoking a lambda stored in a variable propagates argument taint to the result.
        Assert.Contains(result.Slices, slice => slice is { SinkCategory: "file", SinkArgument: not null } && slice.SinkArgument.Contains("transform"));
    }

    [Fact]
    public void DataFlows_CapturedLocalInsideTaskRun_ReachesSink()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("Captured.cs", """
using System.IO;
using System.Threading.Tasks;

public static class CapturedFlow
{
    public static void Run(string input)
    {
        Task.Run(() => File.WriteAllText("out.txt", input));
    }
}
""");
        var result = directory.DataFlows();

        Assert.Contains(result.Slices, slice => slice.SinkCategory == "file");
    }

    // ----- Async/await modeling -----

    [Fact]
    public void DataFlows_AwaitOfAsyncHelper_CarriesTaintAndAwaitNode()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("Async.cs", """
using System.IO;
using System.Threading.Tasks;

public static class AsyncFlow
{
    public static async Task Use(string input)
    {
        var data = await FetchAsync(input);
        File.WriteAllText("out.txt", data);
    }

    public static async Task<string> FetchAsync(string input) => await Task.FromResult(input);
}
""");
        var result = directory.DataFlows();

        Assert.Contains(result.Slices, slice => slice.SinkCategory == "file");
        // The asynchronous boundary is visible in the trace.
        Assert.Contains(result.Nodes, node => node.Kind == "Await");
    }

    // ----- Sanitizer evidence and RemovesTaintKinds -----

    [Fact]
    public void DataFlows_KindScopedSanitizer_RemovesOnlyNamedKinds()
    {
        using var directory = new RoadmapTemporaryDirectory();
        var patternsPath = Path.Combine(directory.Path, "patterns.json");
        File.WriteAllText(patternsPath, """
{
  "sources": [
    { "target": "source", "kind": "parameter", "pattern": "input", "match": "exact", "category": "mixed", "taintKinds": ["insecure-random", "sql"] }
  ]
}
""");
        directory.WriteSource("Scoped.cs", """
using System.Security.Cryptography;

public class DbCommand { public int ExecuteNonQuery(string sql) => 0; }

public static class ScopedFlow
{
    public static void Run(string input)
    {
        var salted = RandomNumberGenerator.GetString(input.ToCharArray(), input.Length);
        new DbCommand().ExecuteNonQuery("SELECT * FROM t WHERE s = '" + salted + "'");
    }
}
""");
        var result = DataFlowAnalyzer.Analyze(directory.Path, patternsPath, null);

        // The sanitizer names RemovesTaintKinds=["insecure-random"]; the sql taint keeps
        // flowing while the insecure-random kind is stripped (partial negative evidence recorded).
        var slice = Assert.Single(result.Slices, candidate => candidate.SinkCategory == "sql");
        Assert.Contains("sql", slice.TaintKinds);
        Assert.DoesNotContain(slice.TaintKinds, kind => string.Equals(kind, "insecure-random", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.SanitizedFlows, sanitized => sanitized.Kind == "SanitizerMatch" && sanitized.RemovesTaintKinds.Contains("insecure-random"));
    }

    [Fact]
    public void DataFlows_RegexGuard_ProducesNegativeEvidence()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("Guarded.cs", """
using System.IO;
using System.Text.RegularExpressions;

public static class GuardedFlow
{
    public static void Run(string input)
    {
        if (Regex.IsMatch(input, "^safe$"))
        {
            File.WriteAllText("out.txt", input);
        }
    }
}
""");
        var result = directory.DataFlows();

        Assert.DoesNotContain(result.Slices, slice => slice.SinkCategory == "file");
        var guard = Assert.Single(result.SanitizedFlows, sanitized => sanitized.Kind == "SanitizerGuard");
        Assert.Equal("validation-guard", guard.SanitizerCategory);
        Assert.True(guard.LineNumber > 0);
    }

    // ----- Severity defaults + suppressions -----

    [Fact]
    public void Severity_DefaultsByCategory_AppliedToSlicesAndWeaknesses()
    {
        Assert.Equal("high", TransparencyBuilder.SeverityForCategory("sql"));
        Assert.Equal("high", TransparencyBuilder.SeverityForCategory("command"));
        Assert.Equal("medium", TransparencyBuilder.SeverityForCategory("redirect"));
        Assert.Equal("low", TransparencyBuilder.SeverityForCategory("log"));
        Assert.Equal("medium", TransparencyBuilder.SeverityForCategory("unknown-category"));
    }

    [Fact]
    public void Suppressions_CategoryFilter_RemovesSlicesAndWeaknesses()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("Suppress.cs", """
using System.IO;

public class DbCommand { public int ExecuteNonQuery(string sql) => 0; }

public static class SuppressFlow
{
    public static void Run(string input)
    {
        new DbCommand().ExecuteNonQuery("SELECT * FROM t WHERE n = '" + input + "'");
        File.WriteAllText("out.txt", input);
    }
}
""");
        var suppressionsPath = Path.Combine(directory.Path, "suppressions.json");
        File.WriteAllText(suppressionsPath, """
[ { "category": "sql", "expires": "2099-01-01T00:00:00Z", "reason": "parameterized elsewhere" } ]
""");
        var result = DataFlowAnalyzer.Analyze(directory.Path, null, null, suppressionsPath);

        Assert.DoesNotContain(result.Slices, slice => slice.SinkCategory == "sql");
        Assert.DoesNotContain(result.WeaknessCandidates, weakness => weakness.SinkCategory == "sql");
        Assert.Contains(result.Slices, slice => slice.SinkCategory == "file");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("Suppressed 1 data-flow slice", StringComparison.Ordinal));
    }

    [Fact]
    public void Suppressions_ExpiredEntry_LetsSliceResurface()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("Expired.cs", """
using System.IO;

public class DbCommand { public int ExecuteNonQuery(string sql) => 0; }

public static class ExpiredFlow
{
    public static void Run(string input) => new DbCommand().ExecuteNonQuery("SELECT * FROM t WHERE n = '" + input + "'");
}
""");
        var suppressionsPath = Path.Combine(directory.Path, "suppressions.json");
        File.WriteAllText(suppressionsPath, """
[ { "category": "sql", "expires": "2020-01-01T00:00:00Z", "reason": "expired suppression" } ]
""");
        var result = DataFlowAnalyzer.Analyze(directory.Path, null, null, suppressionsPath);

        Assert.Contains(result.Slices, slice => slice.SinkCategory == "sql");
    }

    [Fact]
    public void Suppressions_FileAndLine_MustMatchTheSameLocation()
    {
        using var directory = new RoadmapTemporaryDirectory();
        // A flow whose source and sink sit in different files: file+line matchers must be satisfied
        // by one location, never by pairing a file from the source with a line from the sink.
        var weakness = new WeaknessCandidate
        {
            Id = "wc1",
            Kind = "SqlInjectionCandidate",
            SinkCategory = "sql",
            SourceLocation = "Source.cs:5:9",
            SinkLocation = "Sink.cs:12:9"
        };

        Assert.False(Load("[ { \"file\": \"Source.cs\", \"line\": 12 } ]").MatchesWeakness(weakness), "file from the source location must not pair with a line from the sink location");
        Assert.False(Load("[ { \"file\": \"Sink.cs\", \"line\": 5 } ]").MatchesWeakness(weakness), "file from the sink location must not pair with a line from the source location");
        Assert.True(Load("[ { \"file\": \"Sink.cs\", \"line\": 12 } ]").MatchesWeakness(weakness), "file and line both hold for the sink location");
        Assert.True(Load("[ { \"file\": \"Source.cs\", \"line\": 5 } ]").MatchesWeakness(weakness), "file and line both hold for the source location");

        // Each matcher on its own keeps working: file-only and line-only entries match either location.
        Assert.True(Load("[ { \"file\": \"Sink.cs\" } ]").MatchesWeakness(weakness));
        Assert.True(Load("[ { \"line\": 5 } ]").MatchesWeakness(weakness));
        Assert.False(Load("[ { \"line\": 7 } ]").MatchesWeakness(weakness));

        SuppressionSet Load(string json)
        {
            var path = Path.Combine(directory.Path, $"suppressions-{Guid.NewGuid():N}.json");
            File.WriteAllText(path, json);
            return SuppressionSet.Load(path);
        }
    }

    // ----- Sanitizer-guard reasoning in IL mode -----

    [Fact]
    public void DataFlows_IlMode_RegexGuardSuppressesValidatedBranchOnly()
    {
        using var directory = new RoadmapTemporaryDirectory();
        var outputDirectory = directory.BuildTemporaryProject("IlGuard", """
using System.IO;
using System.Text.RegularExpressions;

public static class Program
{
    public static void Main(string[] args)
    {
        if (Regex.IsMatch(args[0], "^safe$"))
        {
            File.WriteAllText(args[0], "guarded");
        }
        else
        {
            File.WriteAllText(args[0], "unguarded");
        }
    }
}
""");
        var result = DataFlowAnalyzer.Analyze(outputDirectory);

        // The validated (true) branch of the guard loses the argument taint; the else branch
        // keeps it. Assembly mode used to be guard-blind: both branches sliced.
        var slices = result.Slices.Where(slice => slice.SinkCategory == "file").ToList();
        var slice = Assert.Single(slices);
        var sinkNode = result.Nodes.First(node => node.Id == slice.SinkId);
        // Line 14 is the unguarded (else) WriteAllText; the guarded one sits on line 10.
        Assert.Equal(14, sinkNode.LineNumber);
    }

    // ----- SCC / recursion clusters -----

    [Fact]
    public void Methods_MutuallyRecursivePair_SharesSccAndCluster()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("Recursive.cs", """
public static class Recursive
{
    public static int Even(int n) => n == 0 ? 1 : Odd(n - 1);
    public static int Odd(int n) => n == 0 ? 0 : Even(n - 1);
    public static int Leaf(int n) => n + 1;
}
""");
        var slice = directory.Methods();
        var reachability = slice.Reachability!.ToDictionary(facts => facts.NodeId, StringComparer.Ordinal);

        var even = reachability.First(pair => pair.Key.Contains("Recursive.Even")).Value;
        var odd = reachability.First(pair => pair.Key.Contains("Recursive.Odd")).Value;
        Assert.True(even.InRecursiveCycle);
        Assert.True(odd.InRecursiveCycle);
        Assert.Equal(even.SccId, odd.SccId);
        Assert.Contains(slice.RecursionClusters!, cluster => cluster.Size == 2 && cluster.MemberIds.Contains(even.NodeId) && cluster.MemberIds.Contains(odd.NodeId));

        // DAG nodes get singleton SCCs without noise.
        var leaf = reachability.First(pair => pair.Key.Contains("Recursive.Leaf")).Value;
        Assert.False(leaf.InRecursiveCycle);
    }

    // ----- Generic-instantiation ID normalization -----

    [Fact]
    public void NormalizeAssemblyGraph_InstantiatedGenericId_MergesOntoOriginalDefinition()
    {
        // A source↔assembly mapping keyed by the original definition, plus an assembly node whose
        // id embeds the generic instantiation from a call site (MethodSpec decoding).
        var mappings = new List<SourceAssemblyMapping>
        {
            new()
            {
                IsMapped = true,
                SourceId = "Ns.G.Echo(T):T",
                SourceSignature = "Ns.G.Echo(T):T",
                AssemblyId = "Ns.G.Echo(T):T",
                AssemblySignature = "Ns.G.Echo(T):T",
                MemberName = "Echo"
            }
        };
        var callGraph = new CallGraph
        {
            Nodes = [new MethodNode { Id = "Ns.G`1<System.String>.Echo(System.String):System.String", Name = "Echo", ClassName = "G", Namespace = "Ns", FileName = "" }],
            Edges =
            [
                new MethodCallEdge
                {
                    SourceId = "Ns.G.Run():void",
                    TargetId = "Ns.G`1<System.String>.Echo(System.String):System.String",
                    CallLocation = new CallLocation { FileName = "G.cs", LineNumber = 3, ColumnNumber = 5 }
                }
            ]
        };
        var calls = new List<MethodCalls>
        {
            new() { SourceId = "Ns.G.Run():void", TargetId = "Ns.G`1<System.String>.Echo(System.String):System.String", FileName = "G.cs", LineNumber = 3 }
        };

        Depscan.Dosai.NormalizeAssemblyGraphToSourceIds(calls, callGraph, mappings);

        // The instantiated IL node merges onto the source original-definition id (one node
        // per definition), and the instantiation string survives as node metadata.
        var node = Assert.Single(callGraph.Nodes);
        Assert.Equal("Ns.G.Echo(T):T", node.Id);
        Assert.Equal("Ns.G`1<System.String>.Echo(System.String):System.String", node.GenericInstantiation);
        Assert.All(callGraph.Edges, edge => Assert.Equal("Ns.G.Echo(T):T", edge.TargetId));
        Assert.All(calls, call => Assert.Equal("Ns.G.Echo(T):T", call.TargetId));
    }

    // ----- Orleans provider -----

    [Fact]
    public void Methods_OrleansGrain_GrainMethodEntryPointAndTaintSeeds()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("Grain.cs", """
namespace Orleans
{
    public abstract class Grain { }
    public interface IGrainWithIntegerKey { }
}
namespace Orleans.Core
{
    public interface IGrainFactory { T GetGrain<T>(string key) where T : class; }
}

public interface IChatGrain : Orleans.IGrainWithIntegerKey
{
    string Say(string message);
}

public class ChatGrain : Orleans.Grain, IChatGrain
{
    public string Say(string message) => System.Diagnostics.Process.Start("sh", message)?.ToString() ?? "";
}

public static class GrainClient
{
    public static void Call(Orleans.Core.IGrainFactory factory, string input)
    {
        var grain = factory.GetGrain<IChatGrain>(input);
        _ = grain.Say(input);
    }
}
""");
        var slice = directory.Methods();

        var endpoint = Assert.Single(slice.ApiEndpoints!, candidate => candidate.EndpointKind == "GrainMethod");
        Assert.Equal("orleans", endpoint.Framework);
        Assert.Equal(1, slice.EntryPoints!.Count(entryPoint => entryPoint.Kind == "GrainMethod"));
        Assert.Contains(slice.Services!, service => service.Framework == "orleans" && service.Direction == Depscan.Frameworks.ServiceDirections.Outbound);

        // Grain method parameters are rpc-message taint seeds, the flow to Process.Start is
        // found without the namespace-prefix heuristic.
        var result = directory.DataFlows();
        Assert.Contains(result.Slices, slice2 => slice2.SinkCategory == "command");
        Assert.Contains(result.Nodes, node => node is { IsSource: true, Category: "rpc" } && node.Name == "message");
    }

    // ----- Configuration security analysis -----

    [Fact]
    public void Methods_InsecureAppSettings_ProduceConfigFindingsWithLines()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("App.cs", MvcStubs + """

[Route("api/ping")]
public class PingController : Microsoft.AspNetCore.Mvc.ControllerBase
{
    [HttpGet]
    public string Ping() => "pong";
}
""");
        directory.WriteSource("appsettings.json", """
{
  "Authentication": {
    "JwtBearer": {
      "RequireHttpsMetadata": false
    }
  },
  "Cookies": {
    "Session": {
      "HttpOnly": false,
      "Secure": false,
      "SameSite": "None"
    }
  }
}
""");
        directory.WriteSource("web.config", """
<configuration>
  <system.web>
    <httpCookies httpOnlyCookies="false" requireSSL="false" />
    <customErrors mode="Off" />
  </system.web>
</configuration>
""");
        var slice = directory.Methods();

        Assert.Contains(slice.SecurityFindings!, finding => finding is { Kind: "ConfigSecurity", Severity: "medium", Cwe: "CWE-319" } && finding.Properties.GetValueOrDefault("property") == "RequireHttpsMetadata");
        Assert.Contains(slice.SecurityFindings!, finding => finding.Properties.GetValueOrDefault("property") == "HttpOnly" && finding.LineNumber > 0);
        Assert.Contains(slice.SecurityFindings!, finding => finding.Properties.GetValueOrDefault("configFile") == "web.config" && finding.Properties.GetValueOrDefault("property") == "httpOnlyCookies");
    }

    // ----- PURL source expansion -----

    [Fact]
    public void PackageUrlResolver_LockFileAndProjectReferences_ResolveWithEvidence()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("packages.lock.json", """
{
  "version": 1,
  "dependencies": {
    "net10.0": {
      "Newtonsoft.Json": { "type": "Direct", "requested": "[13.0.3, )", "resolved": "13.0.3" },
      "Serilog": { "type": "Direct", "requested": "[3.1.1, )", "resolved": "3.1.1" }
    }
  }
}
""");
        directory.WriteSource("App.csproj", """
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <PackageReference Include="Serilog" Version="4.0.0" />
    <PackageReference Include="DotNetEnv" />
  </ItemGroup>
</Project>
""");

        var resolver = PackageUrlResolver.Create(directory.Path);

        // Multi-dot package names resolve through the symbol/namespace probe (assembly-name
        // probes strip the last dot segment, matching the resolver's historic behavior).
        Assert.Equal("pkg:nuget/Newtonsoft.Json@13.0.3", resolver.Resolve(symbol: "Newtonsoft.Json"));
        // Lock file wins over the project reference (higher trust), and the conflict is recorded.
        Assert.Equal("pkg:nuget/Serilog@3.1.1", resolver.Resolve(assembly: "Serilog"));
        Assert.Contains(resolver.Diagnostics, diagnostic => diagnostic.Contains("Serilog", StringComparison.Ordinal) && diagnostic.Contains("ambiguity", StringComparison.OrdinalIgnoreCase));
        // Versionless project reference resolves without a version but is recorded as low confidence.
        Assert.Contains(resolver.ResolutionFacts, fact => fact.Name == "DotNetEnv" && fact.Confidence == "low");
        Assert.Contains(resolver.Diagnostics, diagnostic => diagnostic.Contains("packages.lock.json", StringComparison.Ordinal));
    }

    // ----- Structural diff beyond slices -----

    [Fact]
    public void Diff_TwoVersions_ReportsPerSectionDeltasAndRiskDelta()
    {
        static DataFlowResult Result(string sinkCategory, string severity, string route, string? purl, string weaknessKind)
        {
            var slice = new DataFlowSlice
            {
                Id = "dfs1",
                SourceId = "dfn1",
                SinkId = "dfn2",
                SourceCategory = "http",
                SinkCategory = sinkCategory,
                SinkArgument = "arg",
                Severity = severity
            };
            return new DataFlowResult
            {
                Slices = [slice],
                WeaknessCandidates =
                [
                    new WeaknessCandidate
                    {
                        Id = "wc1",
                        Kind = weaknessKind,
                        SourceLocation = "A.cs:1:1",
                        SinkLocation = "A.cs:2:2",
                        SinkCategory = sinkCategory,
                        Severity = severity
                    }
                ],
                EntryPoints =
                [
                    new EntryPoint { Id = "ep1", Kind = "HttpController", HttpMethod = "GET", Route = route, ClassName = "C", MethodName = "Get" }
                ],
                PackageReachability = purl is null ? [] : [new PackageReachability { Purl = purl, Reachable = true }]
            };
        }

        var oldResult = Result("redirect", "medium", "/old", null, "OpenRedirectCandidate");
        var newResult = Result("sql", "high", "/new", "pkg:nuget/New.Lib@1.0.0", "SqlInjectionCandidate");

        var diff = JsonDocument.Parse(TransparencyBuilder.DiffJson(oldResult, newResult)).RootElement;
        Assert.NotEmpty(diff.GetProperty("AddedSlices").EnumerateArray().ToList());
        Assert.NotEmpty(diff.GetProperty("RemovedSlices").EnumerateArray().ToList());
        Assert.Equal("HttpController:GET:/new:C.Get", Assert.Single(diff.GetProperty("AddedEntryPoints").EnumerateArray()).GetString());
        Assert.Equal("pkg:nuget/New.Lib@1.0.0", Assert.Single(diff.GetProperty("AddedPackages").EnumerateArray()).GetString());
        Assert.Equal(1, diff.GetProperty("RiskDelta").GetProperty("NewHighSeveritySlices").GetInt32());
        Assert.Equal(1, diff.GetProperty("RiskDelta").GetProperty("NewWeaknessKinds").GetInt32());
        Assert.Equal(1, diff.GetProperty("RiskDelta").GetProperty("NewlyReachablePackages").GetInt32());
    }

    [Fact]
    public void Diff_SeverityAware_LowSeverityAdditionsDoNotSignalHighRisk()
    {
        static DataFlowResult WithSlice(string severity) => new()
        {
            Slices = [new DataFlowSlice { Id = "dfs1", SourceId = "dfn1", SinkId = "dfn2", SourceCategory = "http", SinkCategory = "log", SinkArgument = "x", Severity = severity }]
        };

        var diff = JsonDocument.Parse(TransparencyBuilder.DiffJson(WithSlice("low"), WithSlice("low"))).RootElement;
        Assert.Equal(0, diff.GetProperty("RiskDelta").GetProperty("NewHighSeveritySlices").GetInt32());
    }
}
