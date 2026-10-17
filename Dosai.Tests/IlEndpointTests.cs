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

    private const string ConventionalAndRazorSource = """
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Razor.Hosting;

[assembly: RazorCompiledItem(typeof(AspNetCoreGeneratedDocument.Pages_Items), "mvc.1.0.razor-page", "/Pages/Items.cshtml")]
[assembly: RazorCompiledItem(typeof(AspNetCoreGeneratedDocument.Pages_About), "mvc.1.0.razor-page", "/Pages/About.cshtml")]
[assembly: RazorCompiledItem(typeof(AspNetCoreGeneratedDocument.Views_Home_Index), "mvc.1.0.view", "/Views/Home/Index.cshtml")]
[assembly: Microsoft.AspNetCore.Mvc.Razor.Compilation.RazorPage("/Pages/Legacy.cshtml", typeof(AspNetCoreGeneratedDocument.Pages_Legacy), "/old/{slug}")]

var app = new WebApplication();
app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");
app.MapRoute("legacy", "legacy/{controller}/{action}");

public class HomeController : Controller
{
    public string Index() => "home";
    [HttpPost] public void Save(string text) => Process.Start(text);
}

public class ItemsModel : PageModel
{
    public void OnGet(int id) { }
    public async Task OnPostDeleteAsync(string reason) { await Task.Yield(); Process.Start(reason); }
}

namespace AspNetCoreGeneratedDocument
{
    [RazorCompiledItemMetadata("RouteTemplate", "{id:int}"), Authorize(Roles = "Admin")]
    public sealed class Pages_Items : Page { public ItemsModel Model => null!; }
    public sealed class Pages_About : Page { }
    public sealed class Pages_Legacy : Page { }
    public sealed class Views_Home_Index { }
}

namespace Microsoft.AspNetCore.Razor.Hosting
{
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)] public sealed class RazorCompiledItemAttribute : Attribute { public RazorCompiledItemAttribute(Type type, string kind, string identifier) { } }
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)] public sealed class RazorCompiledItemMetadataAttribute : Attribute { public RazorCompiledItemMetadataAttribute(string key, string value) { } }
}
namespace Microsoft.AspNetCore.Mvc.Razor.Compilation
{
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)] public sealed class RazorPageAttribute : Attribute { public RazorPageAttribute(string path, Type viewType, string routeTemplate) { } }
}
namespace Microsoft.AspNetCore.Mvc.RazorPages
{
    public abstract class Page { }
    public abstract class PageModel { }
}
namespace Microsoft.AspNetCore.Mvc
{
    public abstract class ControllerBase { }
    public abstract class Controller : ControllerBase { }
    public sealed class HttpPostAttribute : Attribute { }
}
namespace Microsoft.AspNetCore.Authorization
{
    public sealed class AuthorizeAttribute : Attribute { public string? Roles { get; set; } }
}
namespace Microsoft.AspNetCore.Builder
{
    public interface IEndpointRouteBuilder { }
    public sealed class WebApplication : IEndpointRouteBuilder { }
    public static class ControllerEndpointRouteBuilderExtensions
    {
        public static object MapControllerRoute(this IEndpointRouteBuilder endpoints, string name, string pattern, object? defaults = null) => new();
        public static object MapRoute(this IEndpointRouteBuilder routes, string name, string template) => new();
    }
}
""";

    [Fact]
    public void AssemblyOnlyInput_ConventionalRoutesAndCompiledRazorPages()
    {
        using var fixture = new IlDataFlowParityTests.TempDir();
        IlDataFlowParityTests.EmitAssembly(fixture.Path, ConventionalAndRazorSource, outputKind: OutputKind.ConsoleApplication);

        var slice = JsonSerializer.Deserialize<MethodsSlice>(Depscan.Dosai.GetMethods(fixture.Path), RoadmapTemporaryDirectory.JsonOptions)!;
        var endpoints = slice.ApiEndpoints!.Where(endpoint => endpoint.Framework == "assembly-il").ToList();
        ApiEndpoint Endpoint(string path, string verb) => Assert.Single(endpoints, endpoint => endpoint.Path == path && endpoint.HttpMethod == verb);

        // The registered conventional template, expanded per controller and action.
        Assert.Equal("Index", Endpoint("/Home/Index/{id}", "ANY").MethodName);
        Assert.Equal("Conventional", Endpoint("/Home/Save/{id}", "POST").RoutingKind);
        Assert.Equal("Save", Endpoint("/legacy/Home/Save", "POST").MethodName);
        Assert.DoesNotContain(endpoints, endpoint => endpoint.Path == "/");

        // Compiled Razor Pages: the route from the identifier plus RouteTemplate, the PageModel's handlers.
        var get = Endpoint("/Items/{id}", "GET");
        Assert.Equal("OnGet", get.MethodName);
        Assert.Equal(["Admin"], get.Roles);
        Assert.Equal("OnPostDeleteAsync", Endpoint("/Items/{id}?handler=Delete", "POST").MethodName);
        Assert.Equal("RazorPage", Endpoint("/About", "GET").EndpointKind);
        Assert.Single(endpoints, endpoint => endpoint.Path == "/old/{slug}");
        Assert.DoesNotContain(endpoints, endpoint => endpoint.Path?.Contains("Views", StringComparison.Ordinal) == true || endpoint.Path == "/Home/Index" && endpoint.EndpointKind == "RazorPage");

        var result = DataFlowAnalyzer.Analyze(fixture.Path);
        var nodes = result.Nodes.ToDictionary(node => node.Id);
        var flow = Assert.Single(result.Slices, candidate => candidate.SinkCategory == "command" && nodes[candidate.SourceId].Name == "reason");
        var chain = Assert.Single(result.ExploitChains, candidate => candidate.SliceId == flow.Id);
        Assert.Equal("/Items/{id}?handler=Delete", Assert.Single(result.EntryPoints, entry => entry.Id == chain.EntryPointId).Route);
    }

    [Fact]
    public void AssemblyOnlyEndpoints_GetEndpointSecurityFindings()
    {
        using var fixture = new IlDataFlowParityTests.TempDir();
        IlDataFlowParityTests.EmitAssembly(fixture.Path, """
using System;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

public class LoginRequest { public string Email { get; set; } = ""; public string Password { get; set; } = ""; }
public class RegisterRequest { public string Name { get; set; } = ""; public bool IsAdmin { get; set; } }

[ApiController, Route("api/account"), Authorize(Roles = "Admin")]
public class AccountController : ControllerBase
{
    [HttpPost("login"), AllowAnonymous] public void Login(LoginRequest request) { }
    [HttpPost("register"), AllowAnonymous] public void Register(RegisterRequest request) { }
    [HttpPost("close")] public void Close(int id) { }
    [HttpPost("purge"), IgnoreAntiforgeryToken] public void Purge(int id) { }
    [HttpPost("safe"), ValidateAntiForgeryToken] public void Safe(int id) { }
    [HttpGet("status")] public string Status() => "ok";
}

[ApiController, Route("api/account")]
public class PublicAccountController : ControllerBase
{
    [HttpGet("status"), AllowAnonymous] public string Status() => "ok";
}

namespace Microsoft.AspNetCore.Mvc
{
    public abstract class ControllerBase { }
    public sealed class ApiControllerAttribute : Attribute { }
    public sealed class RouteAttribute : Attribute { public RouteAttribute(string template) { } }
    public sealed class HttpGetAttribute : Attribute { public HttpGetAttribute(string template) { } }
    public sealed class HttpPostAttribute : Attribute { public HttpPostAttribute(string template) { } }
    public sealed class IgnoreAntiforgeryTokenAttribute : Attribute { }
    public sealed class ValidateAntiForgeryTokenAttribute : Attribute { }
}
namespace Microsoft.AspNetCore.Authorization
{
    public sealed class AuthorizeAttribute : Attribute { public string? Roles { get; set; } }
    public sealed class AllowAnonymousAttribute : Attribute { }
}
""");

        var findings = JsonSerializer.Deserialize<MethodsSlice>(Depscan.Dosai.GetMethods(fixture.Path), RoadmapTemporaryDirectory.JsonOptions)!.SecurityFindings!;
        Depscan.Frameworks.SecurityFinding Single(string kind, string route) => Assert.Single(findings, finding => finding.Kind == kind && finding.Route == route);

        Assert.Contains("credential", Single("SensitiveUnauthenticatedEndpoint", "/api/account/login").Properties["classifications"], StringComparison.Ordinal);
        Single("MassAssignmentHint", "/api/account/register");
        Single("StateChangingEndpointWithoutAntiforgery", "/api/account/close");
        Single("StateChangingEndpointAntiforgeryDisabled", "/api/account/purge");
        Assert.DoesNotContain(findings, finding => finding.Route == "/api/account/safe");
        Single("AllowAnonymousOverridesAuthorization", "/api/account/login");
        Assert.Single(findings, finding => finding.Kind == "DuplicateRouteAuthMismatch" && finding.Route == "/api/account/status");
    }
}
