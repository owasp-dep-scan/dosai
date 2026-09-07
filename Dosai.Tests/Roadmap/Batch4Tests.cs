using System.Text.Json;
using System.Text.Json.Serialization;
using Depscan;
using Xunit;
using static Dosai.Tests.Roadmap.RoadmapStubs;

namespace Dosai.Tests.Roadmap;

/// <summary>
///     Entry points and reporting: top-level-statement entry roots for modern CLIs, the
///     dead-code report, the MCP tool surface, the attack-surface view, the query engine
///     (nested paths, OR, sort, count), ref/out taint propagation, and collection taint.
/// </summary>
public class Batch4Tests
{
    // ----- Top-level statements and Main variants -----

    [Fact]
    public void Methods_TopLevelStatements_ProduceMethodCliEntryPointAndReachability()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("Program.cs", """
using System.Diagnostics;

var t = args[0];
Process.Start("ping", t);
""");
        var slice = directory.Methods();

        // The compiler-synthesized `<Main>$` reaches the method inventory, with a
        // SourceSignature that matches the call-graph node id.
        var main = Assert.Single(slice.Methods!, method => method.Name == "<Main>$");
        Assert.Equal("Program.<Main>$(string[]):void", main.SourceSignature);
        Assert.NotNull(main.Parameters);
        Assert.Contains(main.Parameters, parameter => parameter.Name == "args");

        // The Cli entry point resolves to the concrete graph node, so R1 reachability keys off it.
        var entryPoint = Assert.Single(slice.EntryPoints!, candidate => candidate.Kind == "Cli");
        Assert.Equal(main.SourceSignature, entryPoint.MethodId);
        Assert.Contains(slice.CallGraph!.Nodes, node => node.Id == entryPoint.MethodId);

        var reachabilityByNode = slice.Reachability!.ToDictionary(facts => facts.NodeId, StringComparer.Ordinal);
        var mainFacts = reachabilityByNode[entryPoint.MethodId!];
        Assert.True(mainFacts.Reachable);
        Assert.Equal(0, mainFacts.DepthFromEntryPoint);
        var sinkFacts = reachabilityByNode.Values.Single(facts => facts.NodeId.Contains("Process.Start", StringComparison.Ordinal));
        Assert.True(sinkFacts.Reachable);
        Assert.Contains(entryPoint.Id, sinkFacts.ReachableEntryPoints);
        Assert.Equal(1, sinkFacts.DepthFromEntryPoint);
    }

    [Fact]
    public void DataFlows_TopLevelStatements_ProduceSliceExploitChainAndWeakness()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("Program.cs", """
using System.Diagnostics;

var t = args[0];
Process.Start("ping", t);
""");
        var result = directory.DataFlows();

        // The acceptance fixture: args seeds a cli source, the statement chain carries it to
        // Process.Start, and the chain links to the synthesized `<Main>$` entry point.
        var slice = Assert.Single(result.Slices, candidate => candidate.SinkCategory == "command");
        Assert.Equal("cli", slice.SourceCategory);
        var chain = Assert.Single(result.ExploitChains);
        Assert.Equal("cli", chain.Exposure);
        Assert.Equal(slice.Id, chain.SliceId);
        var weakness = Assert.Single(result.WeaknessCandidates, candidate => candidate.SliceId == slice.Id);
        Assert.Equal("CWE-78", weakness.Cwe);
        Assert.Contains(result.EntryPoints, entryPoint => entryPoint is { Kind: "Cli" } && entryPoint.MethodId == chain.CallPath.FirstOrDefault());
    }

    [Fact]
    public void DataFlows_MainVariants_SeedArgsWithEntryPoints()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("AsyncProgram.cs", """
using System.Diagnostics;

public class AsyncProgram
{
    public static async Task Main(string[] args)
    {
        await Task.Delay(1);
        Process.Start("ping", args[0]);
    }
}
""");
        directory.WriteSource("IntProgram.cs", """
using System.Diagnostics;

public class IntProgram
{
    public static int Main(string[] args)
    {
        Process.Start("ping", args[0]);
        return 0;
    }
}
""");
        var result = directory.DataFlows();

        // Async Task Main and int Main are all literally named Main, seeding parity means
        // each produces a cli -> command slice and its own Cli entry point.
        Assert.Equal(2, result.Slices.Count(slice => slice is { SourceCategory: "cli", SinkCategory: "command" }));
        Assert.Contains(result.EntryPoints, entryPoint => entryPoint is { Kind: "Cli", MethodName: "Main" } && entryPoint.MethodId!.Contains("AsyncProgram.Main"));
        Assert.Contains(result.EntryPoints, entryPoint => entryPoint is { Kind: "Cli", MethodName: "Main" } && entryPoint.MethodId!.Contains("IntProgram.Main"));
    }

    // ----- Dead-code report -----

    [Fact]
    public void Methods_DeadCode_FlagsUnreachableHelper()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("Dead.cs", """
public static class Dead
{
    public static void Main() { A(); }
    public static void A() { }
    private static void NeverCalled(string input) => System.Diagnostics.Process.Start("sh", input);
}
""");
        var slice = directory.Methods();

        var neverCalled = Assert.Single(slice.DeadCode!, entry => entry.NodeId.Contains("Dead.NeverCalled"));
        Assert.Equal("Dead.cs", neverCalled.FileName);
        Assert.True(neverCalled.LineNumber > 0);
        var facts = slice.Reachability!.Single(candidate => candidate.NodeId.Contains("Dead.NeverCalled"));
        Assert.False(facts.Reachable);
        // Reachable methods never appear in the dead-code report.
        Assert.DoesNotContain(slice.DeadCode!, entry => entry.NodeId.Contains("Dead.Main") || entry.NodeId.Contains("Dead.A"));
    }

    [Fact]
    public void Methods_DeadCode_ReportsMembersOfGenericTypesButNotSynthesizedMembers()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("Generic.cs", """
using System;
using System.Linq;

public sealed class Box<T>
{
    public Box(T value) { Value = value; }
    public T Value { get; }
    public void DeadInGeneric() { Console.WriteLine(Value); }
}

public static class GenericProgram
{
    public static void Main()
    {
        // A lambda here forces Roslyn to synthesize a display-class member, which must stay out
        // of the report even though the surrounding method is reachable.
        Console.WriteLine(new[] { 1, 2 }.Where(value => value > 1).Count());
    }
}
""");
        var slice = directory.Methods();

        // Members of a generic type are reviewable dead code: the node id embeds the type
        // arguments (`Box<T>..ctor(T)`), which must not be mistaken for compiler-generated syntax.
        Assert.Contains(slice.DeadCode!, entry => entry.NodeId.Contains("Box<T>.DeadInGeneric"));
        Assert.Contains(slice.DeadCode!, entry => entry.NodeId.Contains("Box<T>..ctor"));

        // Compiler-synthesized members are still excluded: their *names* start with '<' and their
        // containers are `<>`-prefixed.
        Assert.DoesNotContain(slice.DeadCode!, entry =>
            entry.Name!.StartsWith('<') || entry.ClassName?.Contains("<>", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Methods_DeadCode_KeepsDiRegisteredAndReflectionTargetsAlive()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("Boot.cs", """
using System;

public static class Di
{
    public static void AddSingleton<T>() { }
}

public class RegisteredService
{
    public RegisteredService() { }
    private void Helper() { }
}

public class ReflectedService
{
    public ReflectedService() { }
}

public static class Bootstrapper
{
    // A composition root nothing invokes: the DI/reflection evidence below is what keeps the
    // registered constructors out of the dead-code report even though no entry point reaches them.
    public static void Compose()
    {
        Di.AddSingleton<RegisteredService>();
        Activator.CreateInstance(typeof(ReflectedService));
    }
}

public static class Boot
{
    public static void Main() { }
}
""");
        var slice = directory.Methods();

        var dead = slice.DeadCode!;
        // The unreachable composition root itself is dead, and so is the unregistered helper...
        Assert.Contains(dead, entry => entry.NodeId.Contains("Bootstrapper.Compose"));
        Assert.Contains(dead, entry => entry.NodeId.Contains("RegisteredService.Helper"));
        //...but the DI-registered and reflection-created constructors are kept alive.
        Assert.DoesNotContain(dead, entry => entry.NodeId.Contains(".ctor"));
        var registeredCtor = slice.Reachability!.Single(facts => facts.NodeId.Contains("RegisteredService") && facts.NodeId.Contains(".ctor"));
        Assert.False(registeredCtor.Reachable);
        Assert.True(registeredCtor.KeepAlive);
        Assert.Contains(registeredCtor.KeepAliveReasons, reason => reason.Contains("FrameworkModel", StringComparison.Ordinal));
        var reflectedCtor = slice.Reachability!.Single(facts => facts.NodeId.Contains("ReflectedService") && facts.NodeId.Contains(".ctor"));
        Assert.True(reflectedCtor.KeepAlive);
        // Main itself is an entry point and never dead.
        Assert.DoesNotContain(dead, entry => entry.NodeId.Contains("Boot.Main"));
    }

    // ----- Attack-surface view -----

    [Fact]
    public void DataFlows_AttackSurface_GroupsByExposureAndCounts()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("Mixed.cs", MvcStubs + """

public static class Executor
{
    public static string Spawn(string arg) => System.Diagnostics.Process.Start("sh", arg)?.ToString() ?? "";
}

[Route("api/open")]
public class OpenController : Microsoft.AspNetCore.Mvc.ControllerBase
{
    [HttpGet("run")]
    [Microsoft.AspNetCore.Mvc.AllowAnonymous]
    public string Run(string input) => Executor.Spawn(input);
}

[Route("api/locked")]
public class LockedController : Microsoft.AspNetCore.Mvc.ControllerBase
{
    [HttpGet("run")]
    [Microsoft.AspNetCore.Mvc.Authorize]
    public string Run(string input) => Executor.Spawn(input);
}
""");
        var result = directory.DataFlows();

        // One group per exposure, most-exposed first; the anonymous group carries the linked
        // weakness and chain counts, the authenticated group has the same flow but its own group.
        Assert.NotEmpty(result.AttackSurface);
        Assert.Equal("anonymous-http", result.AttackSurface[0].Exposure);
        var anonymous = result.AttackSurface.Single(group => group.Exposure == "anonymous-http");
        Assert.Equal(1, anonymous.EntryPointCount);
        Assert.True(anonymous.WeaknessCount >= 1);
        Assert.True(anonymous.ExploitChainCount >= 1);
        var anonymousEntry = anonymous.EntryPoints.Single(entry => entry.Route is not null && entry.Route.Contains("api/open", StringComparison.Ordinal));
        Assert.Equal("command", Assert.Single(anonymousEntry.SinkCategories));
        Assert.Contains("CWE-78", anonymousEntry.Cwes);
        var authenticated = result.AttackSurface.Single(group => group.Exposure == "authenticated-http");
        Assert.Equal(1, authenticated.EntryPointCount);
        // Grouping is total: every entry point lands in exactly one group.
        Assert.Equal(result.EntryPoints.Count, result.AttackSurface.Sum(group => group.EntryPointCount));

        var report = TransparencyBuilder.ToMarkdownReport(result);
        Assert.Contains("## Attack surface (grouped by exposure)", report);
        Assert.Contains("### anonymous-http", report);

        var agentContext = TransparencyBuilder.BuildAgentContext(result, directory.Path);
        Assert.NotEmpty(agentContext.AttackSurface);
    }

    // ----- Query engine, nested paths, OR, sort, count -----

    [Fact]
    public void QueryEngine_NestedPathsOperatorsSortAndCount()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("Query.cs", """
using System.Diagnostics;

public static class Query
{
    public static void Main(string[] args)
    {
        A(args[0]);
        B("safe");
    }
    public static void A(string input) => Process.Start("sh", input);
    public static void B(string input) => Process.Start("sh", input);
}
""");
        var methodsJson = Depscan.Dosai.GetMethods(directory.Path);
        var flowsJson = JsonSerializer.Serialize(directory.DataFlows(), new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } });

        // (a) nested collection path: the call graph lives under callGraph.nodes/edges in
        // methods.json (R1's per-node fan-in/fan-out facts live in reachability).
        var callGraphNodes = Deserialize(DosaiQueryEngine.QueryJson(methodsJson, "callGraph.nodes[isExternal=false]"));
        Assert.NotEmpty(callGraphNodes);
        Assert.All(callGraphNodes, node => Assert.Equal("Query.cs", GetString(node, "FileName")));

        // (b) sort by prop desc over the reachability facts, Main has the largest fan-out.
        var sorted = Deserialize(DosaiQueryEngine.QueryJson(methodsJson, "reachability[fanOut>=0] sort by fanOut desc"));
        Assert.NotEmpty(sorted);
        Assert.True(GetNumber(sorted[0], "FanOut") >= GetNumber(sorted[^1], "FanOut"));

        // (c) OR between terms with flat semantics.
        var orSlices = Deserialize(DosaiQueryEngine.QueryJson(flowsJson, "slices[severity=high||severity=critical]"));
        Assert.Equal(directory.DataFlows().Slices.Count(slice => slice.Severity is "high" or "critical"), orSlices.Count);
        // AND of conjuncts still narrows.
        var andSlices = Deserialize(DosaiQueryEngine.QueryJson(flowsJson, "slices[sinkCategory=command&&severity=high]"));
        Assert.All(andSlices, slice => Assert.Equal("command", GetString(slice, "SinkCategory")));

        // (d) count aggregate mode.
        var counted = JsonDocument.Parse(DosaiQueryEngine.QueryJson(flowsJson, "weaknesses count")).RootElement;
        Assert.Equal(directory.DataFlows().WeaknessCandidates.Count, counted.GetProperty("count").GetInt32());
        var emptyCount = JsonDocument.Parse(DosaiQueryEngine.QueryJson(flowsJson, "weaknesses[sinkCategory=nosuch] count")).RootElement;
        Assert.Equal(0, emptyCount.GetProperty("count").GetInt32());

        // Unknown collections still pass through literally (no match, no throw).
        Assert.Empty(Deserialize(DosaiQueryEngine.QueryJson(flowsJson, "nosuchcollection[x=1]")));

        static List<JsonElement> Deserialize(string json) => JsonSerializer.Deserialize<List<JsonElement>>(json)!;
        static double GetNumber(JsonElement element, string property) => element.GetProperty(property).GetDouble();
        static string GetString(JsonElement element, string property) => element.GetProperty(property).GetString()!;
    }

    [Fact]
    public void QueryEngine_DeadCodeAndReachabilityAliases()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("Dead.cs", """
public static class Dead
{
    public static void Main() { }
    private static void NeverCalled() { }
}
""");
        var methodsJson = Depscan.Dosai.GetMethods(directory.Path);

        var unreachable = Deserialize(DosaiQueryEngine.QueryJson(methodsJson, "reachability[reachable=false]"));
        Assert.NotEmpty(unreachable);
        var dead = Deserialize(DosaiQueryEngine.QueryJson(methodsJson, "deadcode"));
        Assert.NotEmpty(dead);
        Assert.Contains(dead, entry => GetString(entry, "NodeId").Contains("NeverCalled", StringComparison.Ordinal));

        static List<JsonElement> Deserialize(string json) => JsonSerializer.Deserialize<List<JsonElement>>(json)!;
        static string GetString(JsonElement element, string property) => element.GetProperty(property).GetString()!;
    }

    // ----- MCP tool surface -----

    [Fact]
    public void Mcp_NewTools_ListAndCallExploitChainsAttackSurfaceReachability()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("Cmd.cs", MvcStubs + """

[Route("api/cmd")]
public class CmdController : Microsoft.AspNetCore.Mvc.ControllerBase
{
    [HttpGet("run")]
    public string Run(string input) => System.Diagnostics.Process.Start("sh", input)?.ToString() ?? "";
}
""");

        using var listInput = new StringReader("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}\n");
        using var listOutput = new StringWriter();
        McpServer.Run(directory.Path, null, null, null, listInput, listOutput);
        var tools = listOutput.ToString();
        Assert.Contains("dosai.exploit_chains", tools);
        Assert.Contains("dosai.attack_surface", tools);
        Assert.Contains("dosai.reachability", tools);

        using var callInput = new StringReader(string.Join("\n",
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"dosai.exploit_chains\",\"arguments\":{}}}",
            "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"dosai.attack_surface\",\"arguments\":{}}}",
            "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{\"name\":\"dosai.reachability\",\"arguments\":{}}}",
            ""));
        using var callOutput = new StringWriter();
        McpServer.Run(directory.Path, null, null, null, callInput, callOutput);
        var payload = callOutput.ToString();
        // Each tool returns content[0].text JSON from its analyzer entry point.
        Assert.Contains("EntryPointId", payload);
        Assert.Contains("anonymous-http", payload);
        Assert.Contains("reachability", payload);
    }

    [Fact]
    public void Mcp_ReachabilityTool_RespectsRootConfinement()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("Program.cs", "using System.Diagnostics;\nvar t = args[0];\nProcess.Start(\"ping\", t);\n");
        using var root = new RoadmapTemporaryDirectory();

        using var input = new StringReader("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"dosai.reachability\",\"arguments\":{\"path\":\"" + directory.Path.Replace("\\", "\\\\", StringComparison.Ordinal) + "\"}}}\n");
        using var output = new StringWriter();
        var exitCode = McpServer.Run(root.Path, null, null, root.Path, input, output);

        Assert.Equal(0, exitCode);
        Assert.Contains("outside the --mcp-root confinement", output.ToString());
    }

    // ----- Ref/out propagation -----

    [Fact]
    public void DataFlows_TryParseOutVar_CarriesTaintToSink()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("TryParse.cs", """
using System.Diagnostics;

public static class TryParseFlow
{
    public static void Main(string[] args)
    {
        if (int.TryParse(args[0], out var number))
        {
            Process.Start("ping", number.ToString());
        }
    }
}
""");
        var result = directory.DataFlows();

        Assert.Contains(result.Slices, slice => slice is { SourceCategory: "cli", SinkCategory: "command" } && slice.SinkArgument!.Contains("number", StringComparison.Ordinal));
        Assert.Contains(result.Nodes, node => node.Kind == "OutArgument");
    }

    [Fact]
    public void DataFlows_TryParseCleanInput_KeepsOutVarUntainted()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("Clean.cs", """
using System.Diagnostics;

public static class CleanFlow
{
    public static void Main()
    {
        if (int.TryParse("42", out var constant))
        {
            Process.Start("ping", constant.ToString());
        }
    }
}
""");
        var result = directory.DataFlows();

        // Negative case: a call with clean arguments and no source-writing summary must not mint
        // taint out of thin air.
        Assert.DoesNotContain(result.Slices, slice => slice.SinkCategory == "command");
        Assert.DoesNotContain(result.Nodes, node => node.Kind == "OutArgument");
    }

    [Fact]
    public void DataFlows_HelperOutParamFromSource_SeedsOutLocalViaSummary()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("Config.cs", """
using System.Diagnostics;

public static class Secrets
{
    public static string ApiKey = "stored";
}

public static class ConfigFlow
{
    public static bool TryGetKey(out string key)
    {
        key = Secrets.ApiKey;
        return true;
    }

    public static void Main()
    {
        TryGetKey(out var resolved);
        Process.Start("sh", resolved);
    }
}
""");
        directory.WriteSource("patterns.json", """
{
  "sources": [
    { "target": "source", "kind": "Name", "pattern": "ApiKey", "match": "Exact", "category": "config", "description": "Test source: API key field" }
  ]
}
""");
        var result = DataFlowAnalyzer.Analyze(directory.Path, System.IO.Path.Combine(directory.Path, "patterns.json"), null);

        // Summary side: the helper's out parameter is recorded as written and source-fed.
        var summary = Assert.Single(result.MethodSummaries, candidate => candidate.Method.Contains("ConfigFlow.TryGetKey"));
        Assert.Contains(0, summary.OutParameterIndexes);
        Assert.Contains("config", summary.OutSourceCategories[0]);
        // Call side: the out local is seeded from the callee's source-backed write. The default
        // crypto pack also matches the ApiKey field name, so either category is a valid seed.
        Assert.Contains(result.Slices, slice => slice is { SinkCategory: "command" } && slice.SinkArgument!.Contains("resolved", StringComparison.Ordinal) && slice.SourceCategory is "config" or "crypto-material");
    }

    [Fact]
    public void DataFlows_ByValueParameterAssignedFromSource_DoesNotSeedSiblingOutParameter()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("ByValue.cs", """
using System.Diagnostics;

public static class Secrets
{
    public static string ApiKey = "stored";
}

public static class ByValueFlow
{
    // `scratch` is by-value: reassigning it is local to the callee and never reaches the caller.
    // `safe` is the only real write-back and only ever receives a literal.
    public static bool TryGet(string scratch, out string safe)
    {
        scratch = Secrets.ApiKey;
        safe = "constant-literal";
        return scratch.Length > 0;
    }

    public static void Main()
    {
        TryGet("clean", out var value);
        Process.Start("sh", value);
    }
}
""");
        directory.WriteSource("patterns.json", """
{
  "sources": [
    { "target": "source", "kind": "Name", "pattern": "ApiKey", "match": "Exact", "category": "config", "description": "Test source: API key field" }
  ]
}
""");
        var result = DataFlowAnalyzer.Analyze(directory.Path, System.IO.Path.Combine(directory.Path, "patterns.json"), null);

        // Only the out parameter is recorded; the by-value parameter's assignment is not a
        // write-back contract and must not appear.
        var summary = Assert.Single(result.MethodSummaries, candidate => candidate.Method.Contains("ByValueFlow.TryGet"));
        Assert.Equal([1], summary.OutParameterIndexes);
        Assert.DoesNotContain(0, summary.OutSourceCategories.Keys);

        // Categories are keyed per parameter index, so the source reaching `scratch` cannot mint a
        // finding for `safe`, which can only ever hold a literal.
        Assert.Empty(summary.OutSourceCategories);
        Assert.DoesNotContain(result.Slices, slice => slice.SinkCategory == "command");
    }

    [Fact]
    public void AttackSurface_OrdersChainsDescendingAndRanksBareAnonymousAboveAuthenticated()
    {
        // Two entry points with no linked weaknesses (the common case on a framework app) tie on
        // both weakness counts, so the chain count decides. Chains must sort descending like the
        // counts above them: the group's row list is capped, and sorting ascending pushed the
        // entry points that actually have chains out of the report first.
        var result = new DataFlowResult
        {
            EntryPoints =
            [
                new EntryPoint { Id = "ep1", Kind = "HttpController", Route = "/no-chains", AllowAnonymous = true },
                new EntryPoint { Id = "ep2", Kind = "HttpController", Route = "/two-chains", AllowAnonymous = true },
                // An unauthenticated entry point of an unclassified kind lands in the bare
                // "anonymous" bucket, which must still outrank the authenticated groups.
                new EntryPoint { Id = "ep3", Kind = "SomethingUnmodelled" },
                new EntryPoint { Id = "ep4", Kind = "HttpController", Route = "/secure", AuthorizationRequired = true }
            ],
            ExploitChains =
            [
                new ExploitChain { Id = "ec1", EntryPointId = "ep2", Exposure = "anonymous-http" },
                new ExploitChain { Id = "ec2", EntryPointId = "ep2", Exposure = "anonymous-http" }
            ]
        };

        var surface = TransparencyBuilder.BuildAttackSurface(result);

        var anonymousHttp = surface.Single(group => group.Exposure == "anonymous-http");
        Assert.Equal(["ep2", "ep1"], anonymousHttp.EntryPoints.Select(entry => entry.EntryPointId));
        Assert.Equal(2, anonymousHttp.ExploitChainCount);
        Assert.False(anonymousHttp.EntryPointsTruncated);

        // Triage order: every anonymous bucket precedes every authenticated one.
        var order = surface.Select(group => group.Exposure).ToList();
        Assert.True(order.IndexOf("anonymous") < order.IndexOf("authenticated-http"));
    }

    // ----- Collection/element taint -----

    [Fact]
    public void DataFlows_ForEachOverTaintedCollection_TaintsLoopVariable()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("Loop.cs", """
using System.Diagnostics;

public static class LoopFlow
{
    public static void Main(string[] args)
    {
        foreach (var part in args)
        {
            Process.Start("ping", part);
        }
    }
}
""");
        var result = directory.DataFlows();

        Assert.Contains(result.Slices, slice => slice is { SourceCategory: "cli", SinkCategory: "command" } && slice.SinkArgument == "part");
        Assert.Contains(result.Nodes, node => node is { Kind: "Element", Name: "part" });
    }

    [Fact]
    public void DataFlows_IndexerStoreTaintsContainer_ReadBackStaysTainted()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("Dict.cs", """
using System.Collections.Generic;
using System.Diagnostics;

public static class DictFlow
{
    public static void Main(string[] args)
    {
        var map = new Dictionary<string, string>();
        map["key"] = args[0];
        Process.Start("sh", map["unrelated"]);
    }
}
""");
        var result = directory.DataFlows();

        Assert.Contains(result.Slices, slice => slice is { SourceCategory: "cli", SinkCategory: "command" });
    }

    [Fact]
    public void DataFlows_PropertyStore_DoesNotTaintSiblingMembers()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("Holder.cs", """
using System.Diagnostics;

public class Holder
{
    public string Tag { get; set; } = "";
    public string Other { get; set; } = "constant";
}

public static class MemberFlow
{
    public static void Main(string[] args)
    {
        var holder = new Holder();
        holder.Tag = args[0];
        Process.Start("sh", holder.Other);
    }
}
""");
        var result = directory.DataFlows();

        // Negative case: tainting holder.Tag must not taint holder.Other, member taint keys
        // keep field sensitivity for ordinary property stores.
        Assert.DoesNotContain(result.Slices, slice => slice.SinkCategory == "command");
    }
}
