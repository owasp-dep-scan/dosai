using System.Text.Json;
using System.Text.Json.Serialization;
using Depscan;
using Depscan.Frameworks;
using Xunit;

namespace Dosai.Tests.Frameworks;

public class AspNetCoreMvcProviderTests : IDisposable
{
    private readonly TemporaryDirectory _tempDirectory = new();

    private MethodsSlice Run(params (string Name, string Content)[] files)
    {
        foreach (var (name, content) in files)
        {
            File.WriteAllText(Path.Combine(_tempDirectory.Path, name), content);
        }

        var slice = JsonSerializer.Deserialize<MethodsSlice>(Depscan.Dosai.GetMethods(_tempDirectory.Path), new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() }
        });
        Assert.NotNull(slice);
        return slice;
    }

    public void Dispose() => _tempDirectory.Dispose();

    private const string MvcStubs = """
namespace Microsoft.AspNetCore.Mvc
{
    public class RouteAttribute : System.Attribute { public RouteAttribute(string template) { } }
    public class HttpGetAttribute : System.Attribute { public HttpGetAttribute(string template) { } }
    public class HttpPostAttribute : System.Attribute { public HttpPostAttribute() { } public HttpPostAttribute(string template) { } }
    public class AcceptVerbsAttribute : System.Attribute { public AcceptVerbsAttribute(params string[] verbs) { } }
    public class FromBodyAttribute : System.Attribute { }
    public class FromQueryAttribute : System.Attribute { }
    public class ControllerBase { }
}
""";

    [Fact]
    public void ControllerWithSemanticBaseType_HighConfidenceService()
    {
        var slice = Run(("Semantic.cs", MvcStubs + """

[Route("api/orders")]
public class OrdersController : Microsoft.AspNetCore.Mvc.ControllerBase
{
    [HttpGet("{id:int}")]
    public object Get(int id) => null;
}
"""));

        var service = Assert.Single(slice.Services ?? [], s => s.Name == "Orders");
        Assert.Equal(ServiceKinds.Http, service.ServiceKind);
        Assert.Equal(ServiceDirections.Inbound, service.Direction);
        Assert.Equal("high", service.Confidence);
        Assert.StartsWith("svc:aspnetcore-mvc:", service.Id);
        var operation = Assert.Single(service.Operations);
        Assert.Equal("/api/orders/{id}", operation.Path);
        Assert.Equal("GET", operation.HttpMethod);
        Assert.Contains("/api/orders/{id}", service.Endpoints);
    }

    [Fact]
    public void NameSuffixControllerWithoutReferences_DegradesToMediumConfidence()
    {
        var slice = Run(("Stub.cs", """
public class RouteAttribute : System.Attribute { public RouteAttribute(string template) { } }
public class HttpGetAttribute : System.Attribute { public HttpGetAttribute(string template) { } }

[Route("orders")]
public class OrdersController
{
    [HttpGet]
    public object List() => null;
}
"""));

        var endpoint = Assert.Single(slice.ApiEndpoints ?? [], e => e.MethodName == "List");
        // No references, no base type: the ASP.NET name-suffix rule still finds the controller,
        // but it must be reported at medium, never silently promoted to high.
        Assert.Equal("medium", endpoint.Confidence);
        Assert.Equal("/orders", endpoint.Path);
        var service = Assert.Single(slice.Services ?? [], s => s.Framework == "aspnetcore-mvc");
        Assert.Equal("medium", service.Confidence);
    }

    [Fact]
    public void NonControllerClassWithHttpAttributes_IsNotAnEndpoint()
    {
        // False-positive guard: HttpGet on a non-controller helper must not become an endpoint.
        var slice = Run(("Guard.cs", MvcStubs + """

public static class UrlBuilder
{
    [HttpGet("/not-a-route")]
    public static string Build() => "/x";
}
"""));

        Assert.DoesNotContain(slice.ApiEndpoints ?? [], e => e.MethodName == "Build");
        Assert.DoesNotContain(slice.Services ?? [], s => s.Name == "UrlBuilder");
    }

    [Fact]
    public void ControllerTokenRoute_ResolvesToConcreteControllerPath()
    {
        // Regression for the cdxgen deep-analysis report where the raw template token shipped
        // as the endpoint ("%5Bcontroller%5D"): [controller] must resolve to the controller name.
        var slice = Run(("Weather.cs", MvcStubs + """

[Route("[controller]")]
public class WeatherForecastController : Microsoft.AspNetCore.Mvc.ControllerBase
{
    [HttpGet]
    public object Get() => null;
}
"""));

        var service = Assert.Single(slice.Services ?? [], s => s.Name == "WeatherForecast");
        Assert.Equal(["/WeatherForecast"], service.Endpoints);
        var endpoint = Assert.Single(slice.ApiEndpoints ?? []);
        Assert.Equal("/WeatherForecast", endpoint.Path);
        Assert.Equal("[controller]", endpoint.Route);
    }

    [Fact]
    public void SegmentVersionedRoute_ExpandsToOneConcreteEndpointPerDeclaredApiVersion()
    {
        // Regression for URL-path API versioning ("v{version:apiVersion}/[controller]"): the
        // constraint's value domain is known from [ApiVersion], so the endpoint paths must be
        // concrete versions, never a path still containing "apiVersion".
        var slice = Run(("Versioned.cs", MvcStubs + """

namespace Asp.Versioning
{
    public class ApiVersionAttribute : System.Attribute { public ApiVersionAttribute(string version) { } }
    public class MapToApiVersionAttribute : System.Attribute { public MapToApiVersionAttribute(string version) { } }
}

[Route("v{version:apiVersion}/[controller]")]
[Asp.Versioning.ApiVersion("1.0")]
[Asp.Versioning.ApiVersion("2.0")]
public class OrdersController : Microsoft.AspNetCore.Mvc.ControllerBase
{
    [HttpGet]
    public object List() => null;

    [HttpGet("{id:int}")]
    public object Get(int id) => null;

    [HttpPost]
    [Asp.Versioning.MapToApiVersion("2.0")]
    public object Create(object order) => null;
}
"""));

        var service = Assert.Single(slice.Services ?? [], s => s.Name == "Orders");
        // List/Get serve both declared versions; Create is pinned to 2.0 by [MapToApiVersion].
        // Each version contributes its declared spelling and the equivalent compact one
        // (ASP.NET parses a missing minor as 0, so "v1" also matches [ApiVersion("1.0")]).
        Assert.Equal(
        [
            "/v1.0/Orders", "/v1/Orders",
            "/v2.0/Orders", "/v2/Orders",
            "/v1.0/Orders/{id}", "/v1/Orders/{id}",
            "/v2.0/Orders/{id}", "/v2/Orders/{id}"
        ], service.Endpoints);
        // One operation per version keeps ids stable; the compact spelling lives only in Endpoints.
        Assert.Equal(5, service.Operations.Count);
        Assert.Contains(service.Operations, op => op is { HttpMethod: "POST", Path: "/v2.0/Orders" } && op.Properties.TryGetValue("apiVersion", out var v) && v == "2.0");
        Assert.DoesNotContain(service.Operations, op => op is { HttpMethod: "POST", Path: "/v1.0/Orders" });
        var endpoint = Assert.Single(slice.ApiEndpoints ?? [], e => e is { MethodName: "Get", Path: "/v2.0/Orders/{id}" });
        // The verbatim template stays on Route for humans and diffing.
        Assert.Equal("v{version:apiVersion}/[controller]/{id:int}", endpoint.Route);
        Assert.All(slice.ApiEndpoints ?? [], e => Assert.DoesNotContain("apiVersion", e.Path));
        Assert.All(slice.ApiEndpoints ?? [], e => Assert.DoesNotContain("[controller]", e.Path));
    }

    [Fact]
    public void SegmentVersionedRoute_VersionWithMinorHasNoCompactAlias()
    {
        var slice = Run(("Minor.cs", MvcStubs + """

namespace Asp.Versioning
{
    public class ApiVersionAttribute : System.Attribute { public ApiVersionAttribute(string version) { } }
}

[Route("v{version:apiVersion}/[controller]")]
[Asp.Versioning.ApiVersion("1.1")]
public class ThingsController : Microsoft.AspNetCore.Mvc.ControllerBase
{
    [HttpGet]
    public object List() => null;
}
"""));

        // "1.1" has no equivalent shorter segment; only the declared spelling is emitted.
        var service = Assert.Single(slice.Services ?? [], s => s.Name == "Things");
        Assert.Equal(["/v1.1/Things"], service.Endpoints);
    }

    [Fact]
    public void MultipleClassLevelRoutes_EmitOneEndpointPerTemplate()
    {
        var slice = Run(("MultiRoute.cs", MvcStubs + """

[Route("api/v1/orders")]
[Route("api/v2/orders")]
public class OrdersController
{
    [HttpGet("{id}")]
    public object Get(int id) => null;
}
"""));

        Assert.Contains(slice.ApiEndpoints ?? [], e => e is { Path: "/api/v1/orders/{id}" });
        Assert.Contains(slice.ApiEndpoints ?? [], e => e is { Path: "/api/v2/orders/{id}" });
    }

    [Fact]
    public void AcceptVerbs_MapsEveryDeclaredVerb()
    {
        var slice = Run(("Accept.cs", MvcStubs + """

[Route("api/bulk")]
public class BulkController
{
    [AcceptVerbs("GET", "POST")]
    public object Handle() => null;
}
"""));

        Assert.Contains(slice.ApiEndpoints ?? [], e => e is { HttpMethod: "GET", Path: "/api/bulk" });
        Assert.Contains(slice.ApiEndpoints ?? [], e => e is { HttpMethod: "POST", Path: "/api/bulk" });
    }

    [Fact]
    public void AreaAttribute_PopulatesAreaToken()
    {
        var slice = Run(("Area.cs", MvcStubs + """

public class AreaAttribute : System.Attribute { public AreaAttribute(string name) { } }

[Area("admin")]
[Route("[area]/[controller]")]
public class UsersController
{
    [HttpGet]
    public object List() => null;
}
"""));

        Assert.Contains(slice.ApiEndpoints ?? [], e => e.Path == "/admin/Users");
    }

    [Fact]
    public void BindingAttributes_PopulateRouteParameterBindingSource()
    {
        var slice = Run(("Binding.cs", MvcStubs + """

public class OrderDto { public string Name { get; set; } }

[Route("api/orders")]
public class OrdersController
{
    [HttpPost("search")]
    public object Search([FromQuery] int page, [FromBody] OrderDto body) => null;
}
"""));

        var endpoint = Assert.Single(slice.ApiEndpoints ?? [], e => e.MethodName == "Search");
        Assert.Equal("/api/orders/search", endpoint.Path);
        var operation = Assert.Single(Assert.Single(slice.Services ?? [], s2 => s2.Name == "Orders").Operations);
        Assert.Contains("OrderDto", operation.RequestType);
    }

    [Fact]
    public void ConventionalRouting_ExpandsControllerActionsWithMediumConfidence()
    {
        var slice = Run(("Conventional.cs", MvcStubs + """

public class OrdersController
{
    public object Index() => null;
}

public class Program
{
    public static void Main()
    {
        var builder = Microsoft.AspNetCore.Builder.WebApplication.Create();
        builder.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");
    }
}
"""));

        Assert.Contains(slice.ApiEndpoints ?? [], e => e is { RoutingKind: "Conventional", Confidence: "medium", Path: "/Orders/Index" });
    }

    [Fact]
    public void NonControllerMarkedType_IsSkipped()
    {
        var slice = Run(("NonController.cs", MvcStubs + """

public class NonControllerAttribute : System.Attribute { }

[NonController]
[Route("api/hidden")]
public class HiddenController
{
    [HttpGet]
    public object List() => null;
}
"""));

        Assert.DoesNotContain(slice.ApiEndpoints ?? [], e => e.ClassName == "HiddenController");
    }

    [Fact]
    public void RouteTemplateWithUnresolvableConst_LeavesPathNullWithLowConfidence()
    {
        var slice = Run(("Const.cs", MvcStubs + """

public class OrdersController
{
    [HttpGet(RouteTemplates.List)]
    public object List() => null;
}

public static class RouteTemplates { public static readonly string List = "api/orders"; }
"""));

        var endpoint = Assert.Single(slice.ApiEndpoints ?? [], e => e.MethodName == "List");
        Assert.Null(endpoint.Path);
        Assert.Equal("low", endpoint.Confidence);
        // Route keeps the verbatim (unresolvable) template text for humans.
        Assert.NotNull(endpoint.Route);
    }

    [Fact]
    public void ServiceIds_AreReproducibleAcrossDifferentAbsolutePaths()
    {
        var source = MvcStubs + """

[Route("api/orders")]
public class OrdersController
{
    [HttpGet("{id}")]
    public object Get(int id) => null;
}
""";
        using var otherDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(_tempDirectory.Path, "Orders.cs"), source);
        File.WriteAllText(Path.Combine(otherDirectory.Path, "Orders.cs"), source);

        var first = JsonSerializer.Deserialize<MethodsSlice>(Depscan.Dosai.GetMethods(_tempDirectory.Path), new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } });
        var second = JsonSerializer.Deserialize<MethodsSlice>(Depscan.Dosai.GetMethods(otherDirectory.Path), new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } });

        Assert.Equal(first!.Services!.Select(s => s.Id).ToList(), second!.Services!.Select(s => s.Id).ToList());
        Assert.Equal(first.Services.SelectMany(s => s.Operations.Select(o => o.Id)).ToList(), second.Services.SelectMany(s => s.Operations.Select(o => o.Id)).ToList());
    }
}

public class MinimalApiProviderTests : IDisposable
{
    private readonly TemporaryDirectory _tempDirectory = new();

    public void Dispose() => _tempDirectory.Dispose();

    private MethodsSlice Run(string content)
    {
        File.WriteAllText(Path.Combine(_tempDirectory.Path, "Program.cs"), content);
        var slice = JsonSerializer.Deserialize<MethodsSlice>(Depscan.Dosai.GetMethods(_tempDirectory.Path), new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() }
        });
        Assert.NotNull(slice);
        return slice;
    }

    [Fact]
    public void MapMethods_ReadsTheVerbArrayFromArgumentTwo()
    {
        var slice = Run("""
var app = Microsoft.AspNetCore.Builder.WebApplication.Create();
app.MapMethods("/votes", new[] { "GET", "PUT" }, () => "ok");
""");

        Assert.Contains(slice.ApiEndpoints ?? [], e => e is { HttpMethod: "GET", Path: "/votes" });
        Assert.Contains(slice.ApiEndpoints ?? [], e => e is { HttpMethod: "PUT", Path: "/votes" });
    }

    [Fact]
    public void MapGroupVariablePrefix_ComposesIntoEndpointPath()
    {
        var slice = Run("""
var app = Microsoft.AspNetCore.Builder.WebApplication.Create();
var api = app.MapGroup("/api/v1");
api.MapGet("/orders", () => "ok");
""");

        Assert.Contains(slice.ApiEndpoints ?? [], e => e is { Path: "/api/v1/orders", HttpMethod: "GET" });
    }

    [Fact]
    public void NestedMapGroupPrefixes_ComposeTransitively()
    {
        var slice = Run("""
var app = Microsoft.AspNetCore.Builder.WebApplication.Create();
var api = app.MapGroup("/api");
var v1 = api.MapGroup("/v1");
v1.MapPost("/orders", () => "ok");
""");

        Assert.Contains(slice.ApiEndpoints ?? [], e => e is { Path: "/api/v1/orders", HttpMethod: "POST" });
    }

    [Fact]
    public void FluentMapGroupChain_ComposesPrefix()
    {
        var slice = Run("""
var app = Microsoft.AspNetCore.Builder.WebApplication.Create();
app.MapGroup("/api").MapGet("/health", () => "ok");
""");

        Assert.Contains(slice.ApiEndpoints ?? [], e => e is { Path: "/api/health" });
    }

    [Fact]
    public void RequireAuthorizationInFluentChain_RecordsPolicyWithoutSubstringMisfires()
    {
        var slice = Run("""
var app = Microsoft.AspNetCore.Builder.WebApplication.Create();
app.MapGet("/open", () => "ok").WithTags("RequireAuthorization");
app.MapGet("/secure", () => "ok").RequireAuthorization("admin");
""");

        var open = Assert.Single(slice.ApiEndpoints ?? [], e => e.Path == "/open");
        var secure = Assert.Single(slice.ApiEndpoints ?? [], e => e.Path == "/secure");
        Assert.False(open.AuthorizationRequired == true);
        Assert.True(secure.AuthorizationRequired);
        Assert.Contains("admin", secure.AuthorizationPolicies);
        // A tag merely containing the word must not be mistaken for the chain call.
        Assert.Empty(open.AuthorizationPolicies);
    }

    [Fact]
    public void MapHub_IsRecordedAsMountPointNotEndpoint()
    {
        var slice = Run("""
var app = Microsoft.AspNetCore.Builder.WebApplication.Create();
app.MapHub<ChatHub>("/chat");
""");

        Assert.DoesNotContain(slice.ApiEndpoints ?? [], e => e.Path == "/chat");
        // The SignalR provider (M3) reads the mount point; for now it must not double as an HTTP endpoint.
    }

    [Fact]
    public void MapFallback_UsesFallbackTemplate()
    {
        var slice = Run("""
var app = Microsoft.AspNetCore.Builder.WebApplication.Create();
app.MapFallback("/pages/{*path}", () => "ok");
""");

        Assert.Contains(slice.ApiEndpoints ?? [], e => e is { HttpMethod: "ANY", Path: "/pages/{path}" });
    }
}

public class LegacyWebProviderTests : IDisposable
{
    private readonly TemporaryDirectory _tempDirectory = new();

    public void Dispose() => _tempDirectory.Dispose();

    private MethodsSlice Run(params (string Name, string Content)[] files)
    {
        foreach (var (name, content) in files)
        {
            File.WriteAllText(Path.Combine(_tempDirectory.Path, name), content);
        }

        var slice = JsonSerializer.Deserialize<MethodsSlice>(Depscan.Dosai.GetMethods(_tempDirectory.Path), new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() }
        });
        Assert.NotNull(slice);
        return slice;
    }

    [Fact]
    public void WebApi2RoutePrefix_ComposesWithMethodRoute()
    {
        var slice = Run(("Legacy.cs", """
namespace System.Web.Http
{
    public class RoutePrefixAttribute : System.Attribute { public RoutePrefixAttribute(string prefix) { } }
    public class RouteAttribute : System.Attribute { public RouteAttribute(string template) { } }
    public class ApiController { }
}

[System.Web.Http.RoutePrefix("api/orders")]
public class OrdersController : System.Web.Http.ApiController
{
    [System.Web.Http.Route("{id}")]
    public object Get(int id) => null;
}
"""));

        var endpoint = Assert.Single(slice.ApiEndpoints ?? [], e => e.ClassName == "OrdersController" && e.MethodName == "Get");
        Assert.Equal("api/orders/{id}", endpoint.Route);
        Assert.Equal("/api/orders/{id}", endpoint.Path);
        Assert.Equal("legacy-web", endpoint.Framework);
        Assert.Equal("high", endpoint.Confidence);
    }

    [Fact]
    public void WebApi2Convention_MethodNamePrefixSelectsVerb()
    {
        var slice = Run(("Convention.cs", """
namespace System.Web.Http
{
    public class ApiController { }
}

public class ProductsController : System.Web.Http.ApiController
{
    public object GetAll() => null;
}
"""));

        var endpoint = Assert.Single(slice.ApiEndpoints ?? [], e => e.MethodName == "GetAll");
        Assert.Equal("GET", endpoint.HttpMethod);
    }

    [Fact]
    public void WcfServiceContract_EmitsSoapServiceAndRestEndpoints()
    {
        var slice = Run(("Wcf.cs", """
namespace System.ServiceModel
{
    public class ServiceContractAttribute : System.Attribute { }
    public class OperationContractAttribute : System.Attribute { }
    public class WebGetAttribute : System.Attribute { public string UriTemplate { get; set; } }
    public class WebInvokeAttribute : System.Attribute { public string Method { get; set; } public string UriTemplate { get; set; } }
}

[System.ServiceModel.ServiceContract]
public interface IOrderService
{
    [System.ServiceModel.OperationContract]
    void Submit(string order);

    [System.ServiceModel.OperationContract]
    [System.ServiceModel.WebGet(UriTemplate = "/orders/{id}")]
    string Find(string id);
}
"""));

        var wcfService = Assert.Single(slice.Services ?? [], s => s.Framework == "legacy-wcf");
        Assert.Equal(ServiceKinds.Http, wcfService.ServiceKind); // upgraded by the WebGet presence
        Assert.Contains(slice.ApiEndpoints ?? [], e => e is { HttpMethod: "GET", Path: "/orders/{id}", ClassName: "IOrderService" });
        Assert.Contains(wcfService.Operations, operation => operation.Name == "Submit" && operation.HttpMethod is null);
    }

    [Fact]
    public void WcfConfigFile_EmitsInboundServiceWithBindingProperties()
    {
        var slice = Run(("Web.config", """
<configuration>
  <system.serviceModel>
    <services>
      <service name="OrderService">
        <endpoint address="http://localhost:9000/orders" binding="basicHttpBinding" contract="IOrderService" />
      </service>
    </services>
  </system.serviceModel>
</configuration>
"""));

        var service = Assert.Single(slice.Services ?? [], s => s.Framework == "legacy-wcf" && s.Properties.ContainsKey("binding"));
        Assert.Equal("basicHttpBinding", service.Properties["binding"]);
        Assert.Equal("low", service.Confidence);
        Assert.Contains("finding:basicHttpBinding-without-transport-security", service.Tags);
        Assert.Contains("http://localhost:9000/orders", service.Endpoints);
    }

    [Fact]
    public void AsmxWebMethod_EmitsSoapPostEndpoint()
    {
        var slice = Run(("Asmx.cs", """
public class WebMethodAttribute : System.Attribute { }

public class LegacyService
{
    [WebMethod]
    public string Echo(string value) => value;
}
"""));

        var endpoint = Assert.Single(slice.ApiEndpoints ?? [], e => e.MethodName == "Echo");
        Assert.Equal("POST", endpoint.HttpMethod);
        Assert.Equal("/LegacyService/Echo", endpoint.Path);
        Assert.Equal("low", endpoint.Confidence);
    }
}

public class RazorBlazorProviderTests : IDisposable
{
    private readonly TemporaryDirectory _tempDirectory = new();

    public void Dispose() => _tempDirectory.Dispose();

    [Fact]
    public void RazorPageDirective_EmitsHandlerEndpoints()
    {
        File.WriteAllText(Path.Combine(_tempDirectory.Path, "Contact.cshtml"), """
@page "/contact/{id:int?}"
@attribute [Authorize]

public void OnGet(int id) { }
public void OnPost() { }
""");
        File.WriteAllText(Path.Combine(_tempDirectory.Path, "Program.cs"), "class Program { static void Main() {} }");

        var slice = JsonSerializer.Deserialize<MethodsSlice>(Depscan.Dosai.GetMethods(_tempDirectory.Path), new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() }
        });

        Assert.NotNull(slice);
        Assert.Contains(slice.ApiEndpoints ?? [], e => e is { HttpMethod: "GET", Path: "/contact/{id}" });
        Assert.Contains(slice.ApiEndpoints ?? [], e => e is { HttpMethod: "POST", Path: "/contact/{id}" });
        var service = Assert.Single(slice.Services ?? [], s => s.Framework == "razor-blazor");
        Assert.True(service.Authenticated);
        Assert.Equal("low", service.Confidence);
    }

    [Fact]
    public void BlazorComponentRoute_EmitsComponentEndpoint()
    {
        File.WriteAllText(Path.Combine(_tempDirectory.Path, "Counter.razor"), """
@page "/counter"

<h1>Counter</h1>
""");
        File.WriteAllText(Path.Combine(_tempDirectory.Path, "Program.cs"), "class Program { static void Main() {} }");

        var slice = JsonSerializer.Deserialize<MethodsSlice>(Depscan.Dosai.GetMethods(_tempDirectory.Path), new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() }
        });

        Assert.NotNull(slice);
        Assert.Contains(slice.ApiEndpoints ?? [], e => e is { Path: "/counter", EndpointKind: "RazorPage" });
    }
}

public class CommunityHttpProviderTests : IDisposable
{
    private readonly TemporaryDirectory _tempDirectory = new();

    public void Dispose() => _tempDirectory.Dispose();

    [Fact]
    public void FastEndpointConfigure_EmitsRouteAndVerbs()
    {
        File.WriteAllText(Path.Combine(_tempDirectory.Path, "Upload.cs"), """
public class Endpoint<TReq, TRes> { public void Get(string route) { } public void Post(string route) { } public void AllowAnonymous() { } public void Roles(string roles) { } }

public class UploadEndpoint : Endpoint<UploadRequest, UploadResponse>
{
    public override void Configure()
    {
        Post("/upload");
        AllowAnonymous();
    }
}

public class UploadRequest { }
public class UploadResponse { }
""");

        var slice = JsonSerializer.Deserialize<MethodsSlice>(Depscan.Dosai.GetMethods(_tempDirectory.Path), new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() }
        });

        Assert.NotNull(slice);
        var endpoint = Assert.Single(slice.ApiEndpoints ?? [], e => e.ClassName == "UploadEndpoint");
        Assert.Equal("POST", endpoint.HttpMethod);
        Assert.Equal("/upload", endpoint.Path);
        Assert.True(endpoint.AllowAnonymous);
    }

    [Fact]
    public void NancyModuleRouteRegistrations_AreDetected()
    {
        File.WriteAllText(Path.Combine(_tempDirectory.Path, "Nancy.cs", ""), "");
        File.Delete(Path.Combine(_tempDirectory.Path, "Nancy.cs"));
        File.WriteAllText(Path.Combine(_tempDirectory.Path, "Module.cs"), """
public class NancyModule { public DynamicDictionary Get; }

public class HomeModule : NancyModule
{
    public HomeModule()
    {
        Get["/"] = _ => "home";
    }
}
""");

        var slice = JsonSerializer.Deserialize<MethodsSlice>(Depscan.Dosai.GetMethods(_tempDirectory.Path), new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() }
        });

        Assert.NotNull(slice);
        Assert.Contains(slice.ApiEndpoints ?? [], e => e is { HttpMethod: "GET", Framework: "community-http" });
    }
}
