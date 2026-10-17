using Depscan;
using Xunit;

namespace Dosai.Tests.Roadmap;

/// <summary>
///     Endpoint inventory and authorization gaps: [AllowAnonymous] in the exposure, roles kept
///     out of the policy list, inherited controller attributes, group and handler conventions on
///     minimal APIs, global authorization, explicitly disabled antiforgery, the Map*
///     registrations the provider skipped, per-hub SignalR mounts, and minimal-API entry points
///     that name their handler.
/// </summary>
public class EndpointGapTests
{
    private const string AuthStubs = """
namespace Microsoft.AspNetCore.Authorization
{
    public class AuthorizeAttribute : System.Attribute { public AuthorizeAttribute() { } public AuthorizeAttribute(string policy) { } public string? Roles { get; set; } public string? Policy { get; set; } }
    public class AllowAnonymousAttribute : System.Attribute { }
}
namespace Microsoft.AspNetCore.Mvc
{
    public class RouteAttribute : System.Attribute { public RouteAttribute(string template) { } }
    public class HttpGetAttribute : System.Attribute { public HttpGetAttribute() { } public HttpGetAttribute(string template) { } }
    public class HttpPostAttribute : System.Attribute { public HttpPostAttribute() { } public HttpPostAttribute(string template) { } }
    public class IgnoreAntiforgeryTokenAttribute : System.Attribute { }
    public class ControllerBase { }
}
""";

    private const string MinimalStubs = """
namespace Microsoft.AspNetCore.Builder
{
    public class WebApplication
    {
        public static WebApplication Create() => new();
        public RouteGroupBuilder MapGroup(string prefix) => new();
        public RouteHandlerBuilder MapGet(string pattern, System.Delegate handler) => new();
        public RouteHandlerBuilder MapPost(string pattern, System.Delegate handler) => new();
        public RouteHandlerBuilder Map(string pattern, System.Delegate handler) => new();
        public RouteGroupBuilder MapIdentityApi<TUser>() => new();
        public RouteHandlerBuilder MapOpenApi() => new();
        public void MapHub<THub>(string pattern) { }
        public void Run() { }
    }
    public class RouteGroupBuilder
    {
        public RouteGroupBuilder MapGroup(string prefix) => new();
        public RouteHandlerBuilder MapGet(string pattern, System.Delegate handler) => new();
        public RouteHandlerBuilder MapPost(string pattern, System.Delegate handler) => new();
        public RouteGroupBuilder RequireAuthorization(params string[] policies) => this;
        public RouteGroupBuilder AllowAnonymous() => this;
    }
    public class RouteHandlerBuilder
    {
        public RouteHandlerBuilder RequireAuthorization(params string[] policies) => this;
        public RouteHandlerBuilder AllowAnonymous() => this;
        public RouteHandlerBuilder DisableAntiforgery() => this;
    }
}
""";

    [Fact]
    public void ControllerAuthorization_InheritsBaseAttributesAndHonoursAllowAnonymous()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("Controllers.cs", AuthStubs + """

namespace App
{
    using Microsoft.AspNetCore.Authorization;
    using Microsoft.AspNetCore.Mvc;

    [Authorize(Roles = "Admin")]
    public abstract class SecureControllerBase : ControllerBase { }

    [Route("api/orders")]
    public class OrdersController : SecureControllerBase
    {
        [HttpGet("list")]
        public string List() => "";

        [HttpGet("public")]
        [AllowAnonymous]
        public string Public() => "";

        [HttpPost("import")]
        [IgnoreAntiforgeryToken]
        public string Import(string body) => body;
    }

    [Route("api/reports")]
    [Authorize("ReportsPolicy", Roles = "Auditor")]
    public class ReportsController : ControllerBase
    {
        [HttpGet("daily")]
        public string Daily() => "";
    }
}
""");
        var slice = directory.Methods();
        var endpoints = slice.ApiEndpoints!;

        var list = Assert.Single(endpoints, endpoint => endpoint.Path == "/api/orders/list");
        Assert.True(list.AuthorizationRequired);
        Assert.Contains("Admin", list.Roles);
        Assert.Empty(list.AuthorizationPolicies);

        var publicEndpoint = Assert.Single(endpoints, endpoint => endpoint.Path == "/api/orders/public");
        Assert.True(publicEndpoint.AllowAnonymous);
        var publicEntry = Assert.Single(slice.EntryPoints!, entryPoint => entryPoint.Route == "/api/orders/public");
        Assert.Equal("anonymous-http", TransparencyBuilder.ExposureFor(publicEntry));
        var listEntry = Assert.Single(slice.EntryPoints!, entryPoint => entryPoint.Route == "/api/orders/list");
        Assert.Equal("authenticated-http", TransparencyBuilder.ExposureFor(listEntry));

        var daily = Assert.Single(endpoints, endpoint => endpoint.Path == "/api/reports/daily");
        Assert.Equal(["ReportsPolicy"], daily.AuthorizationPolicies);
        Assert.Equal(["Auditor"], daily.Roles);

        Assert.Contains(slice.SecurityFindings!, finding => finding.Kind == "StateChangingEndpointAntiforgeryDisabled" && finding.Route == "/api/orders/import" && finding.Cwe == "CWE-352");
    }

    [Fact]
    public void MinimalApi_GroupAndHandlerConventionsAndNewRegistrations()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("Program.cs", """
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;

var app = WebApplication.Create();
var api = app.MapGroup("/api").RequireAuthorization("ApiPolicy");
var admin = api.MapGroup("/admin");
var open = app.MapGroup("/open");
open.AllowAnonymous();

api.MapGet("/items", () => "items");
api.MapGet("/health", () => "ok").AllowAnonymous();
admin.MapPost("/purge", () => "purged");
open.MapGet("/status", () => "up");
app.MapGet("/annotated", [Authorize] () => "secret");
app.MapGet("/handler", Handlers.Get);
app.MapPost("/upload", (string body) => body).RequireAuthorization().DisableAntiforgery();
app.Map("/any", () => "any");
app.MapIdentityApi<User>();
app.MapOpenApi();
app.Run();

public class User { }

public static class Handlers
{
    [Authorize(Roles = "Ops")]
    public static string Get(string id) => id;
}
""" + MinimalStubs + AuthStubs.Replace("namespace Microsoft.AspNetCore.Mvc", "namespace Unused.Mvc", StringComparison.Ordinal));
        var slice = directory.Methods();
        var endpoints = slice.ApiEndpoints!.Where(endpoint => endpoint.Framework == "minimal-api").ToList();
        ApiEndpoint Endpoint(string path, string? verb = null) => Assert.Single(endpoints, endpoint => endpoint.Path == path && (verb is null || endpoint.HttpMethod == verb));

        Assert.True(Endpoint("/api/items").AuthorizationRequired);
        Assert.Contains("ApiPolicy", Endpoint("/api/items").AuthorizationPolicies);
        Assert.True(Endpoint("/api/health").AllowAnonymous);
        Assert.True(Endpoint("/api/admin/purge").AuthorizationRequired);
        Assert.True(Endpoint("/open/status").AllowAnonymous);
        Assert.True(Endpoint("/annotated").AuthorizationRequired);
        Assert.True(Endpoint("/handler").AuthorizationRequired);
        Assert.Contains("Ops", Endpoint("/handler").Roles);
        Assert.Equal(false, Endpoint("/upload").AntiForgeryRequired);
        Assert.Equal("ANY", Endpoint("/any").HttpMethod);
        Assert.Null(Endpoint("/login", "POST").AuthorizationRequired);
        Assert.True(Endpoint("/manage/info", "GET").AuthorizationRequired);
        Assert.Equal("GET", Endpoint("/openapi/{documentName}.json").HttpMethod);

        var handlerEntry = Assert.Single(slice.EntryPoints!, entryPoint => entryPoint.Route == "/handler");
        Assert.Equal("Get", handlerEntry.MethodName);
        Assert.Contains("Handlers.Get", handlerEntry.MethodId, StringComparison.Ordinal);
        Assert.Contains(slice.SecurityFindings!, finding => finding.Kind == "StateChangingEndpointAntiforgeryDisabled" && finding.Route == "/upload");
    }

    [Fact]
    public void MinimalApiFlows_AreAttributedToTheirOwnRoute()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("Program.cs", """
using Microsoft.AspNetCore.Builder;

var app = WebApplication.Create();
app.MapGet("/first", () => "static");
app.MapPost("/second", (string command) =>
{
    System.Diagnostics.Process.Start(command);
    return "ran";
});
app.Run();
""" + MinimalStubs);
        var result = directory.DataFlows();

        var weakness = Assert.Single(result.WeaknessCandidates, candidate => candidate.SinkCategory == "command");
        Assert.Equal("/second", weakness.Route);
    }

    [Fact]
    public void MinimalApiLambdaHandlers_NameTheirLambdaAndOwnTheirFlows()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("Program.cs", """
using Microsoft.AspNetCore.Builder;

var app = WebApplication.Create();
System.Diagnostics.Process.Start(args[0]);
app.MapGet("/health", () => "ok");
app.MapPost("/run", (string command) =>
{
    System.Diagnostics.Process.Start(command);
    return "ran";
});
app.Run();
""" + MinimalStubs);

        var slice = directory.Methods();
        var run = Assert.Single(slice.EntryPoints!, entryPoint => entryPoint.Route == "/run");
        Assert.Equal("Program.<<Main>$>lambda2(string):string", run.MethodId);
        Assert.Equal("Program.<<Main>$>lambda1():string", Assert.Single(slice.EntryPoints!, entryPoint => entryPoint.Route == "/health").MethodId);
        // The id is the call graph's own lambda node, so reachability walks start from the handler.
        Assert.Contains(slice.CallGraph!.Nodes, node => node.Id == run.MethodId);
        var processStart = slice.CallGraph.Edges.Where(edge => edge.SourceId == run.MethodId && edge.TargetId.StartsWith("System.Diagnostics.Process.Start(", StringComparison.Ordinal)).Select(edge => edge.TargetId).First();
        Assert.Contains(run.Id, Assert.Single(slice.Reachability!, fact => fact.NodeId == processStart).ReachableEntryPoints);

        var result = directory.DataFlows();
        var http = Assert.Single(result.EntryPoints, entryPoint => entryPoint.Route == "/run");
        Assert.Equal(run.MethodId, http.MethodId);
        var cli = Assert.Single(result.EntryPoints, entryPoint => entryPoint.Kind == "Cli");
        var nodes = result.Nodes.ToDictionary(node => node.Id);
        var handlerSlice = Assert.Single(result.Slices, candidate => candidate.SinkCategory == "command" && nodes[candidate.SourceId].Name == "command");
        var argsSlice = Assert.Single(result.Slices, candidate => candidate.SinkCategory == "command" && nodes[candidate.SourceId].Name == "args");
        Assert.Equal(http.Id, Assert.Single(result.ExploitChains, chain => chain.SliceId == handlerSlice.Id).EntryPointId);
        Assert.Equal(cli.Id, Assert.Single(result.ExploitChains, chain => chain.SliceId == argsSlice.Id).EntryPointId);
        Assert.Equal("/run", Assert.Single(result.WeaknessCandidates, weakness => weakness.SliceId == handlerSlice.Id).Route);
        var surface = result.AttackSurface.SelectMany(group => group.EntryPoints).ToList();
        Assert.Equal(1, Assert.Single(surface, entry => entry.EntryPointId == http.Id).ExploitChainCount);
        Assert.Equal(1, Assert.Single(surface, entry => entry.EntryPointId == cli.Id).ExploitChainCount);
    }

    [Fact]
    public void GlobalAuthorization_AppliesToEndpointsWithoutTheirOwnMetadata()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("Program.cs", """
using Microsoft.AspNetCore.Builder;

public static class Startup
{
    public static void Configure(AuthorizationOptions options) => options.FallbackPolicy = new object();

    public static void Map(WebApplication app)
    {
        app.MapGet("/secured", () => "fallback");
        app.MapGet("/anonymous", () => "open").AllowAnonymous();
    }
}

public class AuthorizationOptions { public object? FallbackPolicy { get; set; } }
""" + MinimalStubs);
        var slice = directory.Methods();

        Assert.True(Assert.Single(slice.ApiEndpoints!, endpoint => endpoint.Path == "/secured").AuthorizationRequired);
        Assert.Equal(false, Assert.Single(slice.ApiEndpoints!, endpoint => endpoint.Path == "/anonymous").AuthorizationRequired);
        Assert.True(Assert.Single(slice.EntryPoints!, entryPoint => entryPoint.Route == "/secured").AuthorizationRequired);
    }

    [Fact]
    public void SignalRHubs_EachTakeTheirOwnMount()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("Hubs.cs", """
using Microsoft.AspNetCore.Builder;

namespace Microsoft.AspNetCore.SignalR { public class Hub { } }

namespace App
{
    public class ChatHub : Microsoft.AspNetCore.SignalR.Hub { public void Send(string message) { } }
    public class NotificationsHub : Microsoft.AspNetCore.SignalR.Hub { public void Subscribe(string topic) { } }

    public static class Routes
    {
        public static void Map(WebApplication app)
        {
            app.MapHub<ChatHub>("/hubs/chat");
            app.MapHub<NotificationsHub>("/hubs/notifications");
        }
    }
}
""" + MinimalStubs);
        var slice = directory.Methods();
        var hubEndpoints = slice.ApiEndpoints!.Where(endpoint => endpoint.EndpointKind == "SignalRHub").ToList();

        Assert.Equal("/hubs/chat", Assert.Single(hubEndpoints, endpoint => endpoint.ClassName == "ChatHub").Path);
        Assert.Equal("/hubs/notifications", Assert.Single(hubEndpoints, endpoint => endpoint.ClassName == "NotificationsHub").Path);
    }
}
