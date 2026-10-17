using System.Text.Json;
using Depscan;
using Dosai.Tests.Roadmap;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Depscan.Tests;

/// <summary>
///     Endpoints of assembly-only inputs: controller attributes and minimal-API Map*
///     registrations read from metadata and IL, with entry points named by the call graph's
///     method ids, and their handlers' parameters seeded as HTTP sources so flows attach to the
///     route. The fixture is compiled by Roslyn with a PDB and no source beside it.
/// </summary>
public class IlEndpointTests
{
    private const string Source = """
using System;
using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;

var app = new WebApplication();
app.MapGet("/ping", () => "pong");
app.MapPost("/run/{name}", (string name) => Process.Start(name)).RequireAuthorization();
app.MapGet("/handler", Handlers.Get).AllowAnonymous();

public static class Handlers
{
    public static string Get(string id) => id;
}

[ApiController, Route("api/[controller]"), Authorize(Roles = "Ops")]
public class ItemsController : ControllerBase
{
    [HttpGet("{id}")]
    public string Get(string id) => id;

    [HttpPost, AllowAnonymous]
    public void Create(string command) => Process.Start(command);

    [NonAction]
    public void Helper() { }

    public void NotRouted() { }
}

public class HomeController : ControllerBase
{
    public string Index() => "home";
}

namespace Microsoft.AspNetCore.Mvc
{
    public abstract class ControllerBase { }
    public sealed class ApiControllerAttribute : Attribute { }
    public sealed class RouteAttribute : Attribute { public RouteAttribute(string template) { } }
    public sealed class HttpGetAttribute : Attribute { public HttpGetAttribute() { } public HttpGetAttribute(string template) { } }
    public sealed class HttpPostAttribute : Attribute { public HttpPostAttribute() { } public HttpPostAttribute(string template) { } }
    public sealed class NonActionAttribute : Attribute { }
}

namespace Microsoft.AspNetCore.Authorization
{
    public sealed class AuthorizeAttribute : Attribute { public string? Roles { get; set; } public string? Policy { get; set; } }
    public sealed class AllowAnonymousAttribute : Attribute { }
}

namespace Microsoft.AspNetCore.Builder
{
    public interface IEndpointRouteBuilder { }
    public sealed class WebApplication : IEndpointRouteBuilder { }
    public sealed class RouteHandlerBuilder
    {
        public RouteHandlerBuilder RequireAuthorization(params string[] policies) => this;
        public RouteHandlerBuilder AllowAnonymous() => this;
    }
    public static class EndpointRouteBuilderExtensions
    {
        public static RouteHandlerBuilder MapGet(this IEndpointRouteBuilder endpoints, string pattern, Delegate handler) => new();
        public static RouteHandlerBuilder MapPost(this IEndpointRouteBuilder endpoints, string pattern, Delegate handler) => new();
    }
}
""";

    [Fact]
    public void AssemblyOnlyInput_ControllerAndMinimalApiEndpointsAreRead()
    {
        using var fixture = new IlDataFlowParityTests.TempDir();
        IlDataFlowParityTests.EmitAssembly(fixture.Path, Source, outputKind: OutputKind.ConsoleApplication);

        var slice = JsonSerializer.Deserialize<MethodsSlice>(Depscan.Dosai.GetMethods(fixture.Path), RoadmapTemporaryDirectory.JsonOptions)!;
        var endpoints = slice.ApiEndpoints!.Where(endpoint => endpoint.Framework == "assembly-il").ToList();
        ApiEndpoint Endpoint(string path, string verb) => Assert.Single(endpoints, endpoint => endpoint.Path == path && endpoint.HttpMethod == verb);

        var get = Endpoint("/api/Items/{id}", "GET");
        Assert.True(get.AuthorizationRequired);
        Assert.Equal(["Ops"], get.Roles);
        Assert.True(Endpoint("/api/Items", "POST").AllowAnonymous);
        Assert.DoesNotContain(endpoints, endpoint => endpoint.MethodName is "Helper" or "NotRouted");
        Assert.Equal("Conventional", Endpoint("/Home/Index", "ANY").RoutingKind);
        Assert.Null(Endpoint("/ping", "GET").AuthorizationRequired);
        Assert.True(Endpoint("/run/{name}", "POST").AuthorizationRequired);
        Assert.True(Endpoint("/handler", "GET").AllowAnonymous);

        // Entry points name the handler by the IL call graph's own node ids.
        var nodeIds = slice.CallGraph!.Nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        var entries = slice.EntryPoints!.Where(entry => entry.Route is "/api/Items/{id}" or "/run/{name}" or "/handler").ToList();
        Assert.Equal(3, entries.Count);
        Assert.All(entries, entry => Assert.Contains(entry.MethodId!, nodeIds));
        Assert.Contains("Handlers.Get(", Assert.Single(entries, entry => entry.Route == "/handler").MethodId, StringComparison.Ordinal);
    }

    [Fact]
    public void AssemblyOnlyInput_HandlerParametersAreRouteAttributedSources()
    {
        using var fixture = new IlDataFlowParityTests.TempDir();
        IlDataFlowParityTests.EmitAssembly(fixture.Path, Source, outputKind: OutputKind.ConsoleApplication);

        var result = DataFlowAnalyzer.Analyze(fixture.Path);
        var nodes = result.Nodes.ToDictionary(node => node.Id);
        string? RouteOf(string parameter)
        {
            var slice = Assert.Single(result.Slices, candidate => candidate.SinkCategory == "command" && nodes[candidate.SourceId].Name == parameter);
            Assert.Equal("http", slice.SourceCategory);
            var chain = Assert.Single(result.ExploitChains, candidate => candidate.SliceId == slice.Id);
            return Assert.Single(result.EntryPoints, entry => entry.Id == chain.EntryPointId).Route;
        }

        Assert.Equal("/run/{name}", RouteOf("name"));
        Assert.Equal("/api/Items", RouteOf("command"));
    }
}
