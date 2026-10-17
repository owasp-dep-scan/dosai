using Depscan;
using Xunit;

namespace Dosai.Tests.Roadmap;

/// <summary>
///     Razor Pages paired with their PageModel: <c>On{Verb}[Handler][Async]</c> handlers as
///     endpoints routed by every <c>@page</c> directive (relative templates appended to the
///     conventional route), authorization from the PageModel and from <c>@attribute</c>, and
///     handler parameters and <c>[BindProperty]</c> properties as taint sources attributed to
///     their route.
/// </summary>
public class RazorPageTests
{
    private const string Stubs = """
namespace Microsoft.AspNetCore.Mvc
{
    public interface IActionResult { }
    public class BindPropertyAttribute : System.Attribute { }
    public class BindPropertiesAttribute : System.Attribute { }
}
namespace Microsoft.AspNetCore.Mvc.RazorPages
{
    public abstract class PageModel { public Microsoft.AspNetCore.Mvc.IActionResult Page() => null!; }
}
namespace Microsoft.AspNetCore.Authorization
{
    public class AuthorizeAttribute : System.Attribute { public string? Roles { get; set; } public string? Policy { get; set; } }
    public class AllowAnonymousAttribute : System.Attribute { }
}
""";

    private static RoadmapTemporaryDirectory Fixture()
    {
        var directory = new RoadmapTemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(directory.Path, "Pages"));
        directory.WriteSource("Pages/Items.cshtml", """
@page "{id:int}"
@model ItemsModel
@attribute [Authorize(Roles = "Admin,Ops")]
<h1>Items</h1>
""");
        directory.WriteSource("Pages/Items.cshtml.cs", """
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

public class ItemsModel : PageModel
{
    [BindProperty] public string? Name { get; set; }
    public void OnGet(int id) { }
    public async Task<IActionResult> OnPostDeleteAsync(string reason)
    {
        await Task.Yield();
        Process.Start(reason);
        return Page();
    }
    public void OnPostRename() => Process.Start(Name!);
    private void OnGetHidden() { }
}
""");
        directory.WriteSource("Pages/Open.cshtml", """
@page
@page "/legacy/open"
@model Web.Pages.OpenModel
""");
        directory.WriteSource("Pages/Open.cshtml.cs", """
using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Web.Pages;

[AllowAnonymous, BindProperties]
public class OpenModel : PageModel
{
    public string? Term { get; set; }
    public void OnGet(string q) => Process.Start(q);
    public void OnPost() => Process.Start(Term!);
}
""");
        directory.WriteSource("Stubs.cs", Stubs);
        return directory;
    }

    [Fact]
    public void RazorPages_HandlersRoutesAndAuthorizationComeFromThePageModel()
    {
        using var directory = Fixture();
        var slice = directory.Methods();
        var endpoints = slice.ApiEndpoints!.Where(endpoint => endpoint.EndpointKind == "RazorPage").ToList();
        ApiEndpoint Endpoint(string path, string verb) => Assert.Single(endpoints, endpoint => endpoint.Path == path && endpoint.HttpMethod == verb);

        var get = Endpoint("/Items/{id}", "GET");
        Assert.Equal("OnGet", get.MethodName);
        Assert.True(get.AuthorizationRequired);
        Assert.Equal(["Admin", "Ops"], get.Roles);
        Assert.Contains(get.RouteParameters, parameter => parameter.Name == "id");
        Assert.Equal("OnPostDeleteAsync", Endpoint("/Items/{id}?handler=Delete", "POST").MethodName);
        Assert.Equal("OnPostRename", Endpoint("/Items/{id}?handler=Rename", "POST").MethodName);
        Assert.DoesNotContain(endpoints, endpoint => endpoint.Path?.Contains("Hidden", StringComparison.Ordinal) == true);

        // Both @page directives route the page; the PageModel's [AllowAnonymous] applies.
        Assert.True(Endpoint("/Open", "GET").AllowAnonymous);
        Assert.True(Endpoint("/legacy/open", "GET").AllowAnonymous);
        Assert.Equal(false, Endpoint("/legacy/open", "POST").AuthorizationRequired);

        var delete = Assert.Single(slice.EntryPoints!, entryPoint => entryPoint.Route == "/Items/{id}?handler=Delete");
        Assert.Equal("ItemsModel.OnPostDeleteAsync(string):System.Threading.Tasks.Task<Microsoft.AspNetCore.Mvc.IActionResult>", delete.MethodId);
        Assert.Equal("Items.cshtml.cs", delete.FileName);
        Assert.Contains(slice.CallGraph!.Nodes, node => node.Id == delete.MethodId);
        Assert.Equal("Web.Pages.OpenModel.OnGet(string):void", Assert.Single(slice.EntryPoints!, entryPoint => entryPoint.Route == "/legacy/open" && entryPoint.HttpMethod == "GET").MethodId);
    }

    [Fact]
    public void RazorPages_HandlerParametersAndBoundPropertiesAreSourcesOfTheirRoute()
    {
        using var directory = Fixture();
        var result = directory.DataFlows();
        var nodes = result.Nodes.ToDictionary(node => node.Id);
        string? RouteOf(string sourceName)
        {
            var slice = Assert.Single(result.Slices, candidate => candidate.SinkCategory == "command" && nodes[candidate.SourceId].Name == sourceName);
            var chain = Assert.Single(result.ExploitChains, candidate => candidate.SliceId == slice.Id);
            return Assert.Single(result.EntryPoints, entryPoint => entryPoint.Id == chain.EntryPointId).Route;
        }

        Assert.Equal("/Items/{id}?handler=Delete", RouteOf("reason"));
        Assert.Equal("/Items/{id}?handler=Rename", RouteOf("Name"));
        Assert.Contains(RouteOf("q"), new[] { "/Open", "/legacy/open" });
        // [BindProperties] binds every public settable property of the PageModel.
        Assert.Contains(RouteOf("Term"), new[] { "/Open", "/legacy/open" });
    }
}
