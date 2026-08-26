using System.Text.Json;
using System.Text.Json.Serialization;
using Depscan;
using Depscan.Frameworks;
using Xunit;

namespace Dosai.Tests.Frameworks;

public class FrameworkTaintSeedingTests : IDisposable
{
    private readonly TemporaryDirectory _tempDirectory = new();

    public void Dispose() => _tempDirectory.Dispose();

    /// <summary>
    ///     The M4 exit criterion: a controller action whose plain string parameter carries no
    ///     binding attributes still produces a source→sink slice, because the framework seed
    ///     taints it as route-bound input. This was invisible before M4.
    /// </summary>
    [Fact]
    public void ControllerActionPlainRouteParameter_ProducesSourceToSinkSlice()
    {
        File.WriteAllText(Path.Combine(_tempDirectory.Path, "Lookup.cs"), """
using System.Diagnostics;

public class RouteAttribute : System.Attribute { public RouteAttribute(string template) { } }
public class HttpGetAttribute : System.Attribute { public HttpGetAttribute(string template) { } }

[Route("api/orders")]
public class OrdersController
{
    [HttpGet("{id}")]
    public string Lookup(string id) => System.Diagnostics.Process.Start("sh", id)?.ToString() ?? "";
}
""");

        var result = DataFlowAnalyzer.Analyze(_tempDirectory.Path);

        Assert.Contains(result.Nodes, node => node is { IsSource: true, Category: "http", MethodName: "Lookup", Name: "id" });
        Assert.Contains(result.Slices, slice => slice is { SourceCategory: "http", SinkCategory: "command" });
    }

    [Fact]
    public void ConsumerMessagePayload_IsTaintedAsRpcInput()
    {
        File.WriteAllText(Path.Combine(_tempDirectory.Path, "Consumer.cs"), """
using System.Diagnostics;

namespace MassTransit
{
    public interface IConsumer<T> where T : class { }
}

public class SubmitOrderConsumer : MassTransit.IConsumer<SubmitOrder>
{
    public void Consume(SubmitOrder message)
    {
        var path = message.SourcePath;
    }
}

public class SubmitOrder { public string SourcePath { get; set; } }
""");

        var result = DataFlowAnalyzer.Analyze(_tempDirectory.Path);

        Assert.Contains(result.Nodes, node => node is { IsSource: true, Category: "rpc", MethodName: "Consume", Name: "message" });
    }

    [Fact]
    public void NonEntryPointMethods_AreNotFrameworkTainted()
    {
        // False-positive guard: plain helper parameters must not become http sources.
        File.WriteAllText(Path.Combine(_tempDirectory.Path, "Helper.cs"), """
public static class Helper
{
    public static string Transform(string id) => id;
}
""");

        var result = DataFlowAnalyzer.Analyze(_tempDirectory.Path);

        Assert.DoesNotContain(result.Nodes, node => node is { IsSource: true, Category: "http" });
    }

    [Fact]
    public void NewPatternPacks_ArePartOfTheDefaultAllSet()
    {
        File.WriteAllText(Path.Combine(_tempDirectory.Path, "Program.cs"), "class Program { static void Main() {} }");

        var result = JsonSerializer.Deserialize<DataFlowResult>(DataFlowAnalyzer.GetDataFlows(_tempDirectory.Path), new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() }
        });

        Assert.NotNull(result);
        Assert.Contains(result.Patterns.PatternPacks, pack => pack == "grpc");
        Assert.Contains(result.Patterns.PatternPacks, pack => pack == "messaging");
        Assert.Contains(result.Patterns.PatternPacks, pack => pack == "ai");
        Assert.Contains(result.Patterns.PatternPacks, pack => pack == "mcp");
    }
}

public class DataClassificationTests : IDisposable
{
    private readonly TemporaryDirectory _tempDirectory = new();

    public void Dispose() => _tempDirectory.Dispose();

    [Fact]
    public void RequestDtoMembers_ClassifiedWithAuditableDescriptions()
    {
        File.WriteAllText(Path.Combine(_tempDirectory.Path, "Register.cs"), """
public class RouteAttribute : System.Attribute { public RouteAttribute(string template) { } }
public class HttpPostAttribute : System.Attribute { public HttpPostAttribute() { } }

public class RegisterRequest
{
    public string Email { get; set; }
    public string Password { get; set; }
    public string CardNumber { get; set; }
    public string Nickname { get; set; }
}

[Route("api/account")]
public class AccountController
{
    [HttpPost]
    public object Register(RegisterRequest request) => null;
}
""");

        var slice = JsonSerializer.Deserialize<MethodsSlice>(Depscan.Dosai.GetMethods(_tempDirectory.Path), new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() }
        });

        Assert.NotNull(slice);
        var service = Assert.Single(slice.Services ?? [], s => s.Name == "Account");
        Assert.Contains(service.Data, data => data is { Classification: "pii", Name: "RegisterRequest" } && data.Description.Contains("Email"));
        Assert.Contains(service.Data, data => data.Classification == "credential" && data.Description.Contains("Password"));
        // financial beats pii when both exist only via member precedence: CardNumber matches financial.
        Assert.Contains(service.Data, data => data.Classification == "financial" && data.Description.Contains("CardNumber"));
        Assert.DoesNotContain(service.Data, data => data.Classification == "public");
    }

    [Fact]
    public void UnclassifiedData_DefaultsToUnknownNeverPublic()
    {
        File.WriteAllText(Path.Combine(_tempDirectory.Path, "Ping.cs"), """
public class RouteAttribute : System.Attribute { public RouteAttribute(string template) { } }
public class HttpGetAttribute : System.Attribute { public HttpGetAttribute() { } }

public class PingRequest { public string Timestamp { get; set; } }

[Route("api/ping")]
public class PingController
{
    [HttpGet]
    public object Ping(PingRequest request) => null;
}
""");

        var slice = JsonSerializer.Deserialize<MethodsSlice>(Depscan.Dosai.GetMethods(_tempDirectory.Path), new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() }
        });

        var service = Assert.Single(slice!.Services!, s => s.Name == "Ping");
        var data = Assert.Single(service.Data);
        Assert.Equal("unknown", data.Classification);
    }
}

public class TrustBoundaryTests : IDisposable
{
    private readonly TemporaryDirectory _tempDirectory = new();

    public void Dispose() => _tempDirectory.Dispose();

    [Fact]
    public void PublicEndpointReachingPublisher_CrossesTrustBoundary()
    {
        File.WriteAllText(Path.Combine(_tempDirectory.Path, "Flow.cs"), """
public class RouteAttribute : System.Attribute { public RouteAttribute(string template) { } }
public class HttpGetAttribute : System.Attribute { public HttpGetAttribute() { } }
public class AllowAnonymousAttribute : System.Attribute { }

public class PublishEndpoint { public void Publish<T>(T message) { } }

public class EventPublisher
{
    private readonly PublishEndpoint _endpoint = new();

    public void Announce(string message)
    {
        _endpoint.Publish<OrderPlaced>(new OrderPlaced());
    }
}

public class OrderPlaced { }

[AllowAnonymous]
[Route("api/public")]
public class PublicController
{
    private readonly EventPublisher _publisher = new();

    [HttpGet]
    public object Post(string q)
    {
        _publisher.Announce(q);
        return null;
    }
}
""");

        var slice = JsonSerializer.Deserialize<MethodsSlice>(Depscan.Dosai.GetMethods(_tempDirectory.Path), new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() }
        });

        Assert.NotNull(slice);
        var controller = Assert.Single(slice.Services ?? [], s => s.Name == "Public");
        Assert.Equal(TrustZones.Public, controller.TrustZone);
        // Computed from the call graph: Post -> Announce, and Announce publishes outbound.
        Assert.True(controller.CrossesTrustBoundary);

        // The publisher's destination is genuinely unresolved — no broker address appears anywhere in
        // the source — so its trust zone is Unknown rather than External. Claiming External here would
        // assert the broker is off-premises, which nothing in the code establishes; an in-cluster
        // broker is at least as likely. That the call egresses at all is a separate, weaker claim, and
        // it is the one that drives CrossesTrustBoundary above.
        var publisher = Assert.Single(slice.Services ?? [], s => s.Direction == "outbound");
        Assert.Equal(TrustZones.Unknown, publisher.TrustZone);
    }

    [Fact]
    public void PublicEndpointWithoutOutboundReach_DoesNotCrossBoundary()
    {
        File.WriteAllText(Path.Combine(_tempDirectory.Path, "Safe.cs"), """
public class RouteAttribute : System.Attribute { public RouteAttribute(string template) { } }
public class HttpGetAttribute : System.Attribute { public HttpGetAttribute() { } }
public class AllowAnonymousAttribute : System.Attribute { }

[AllowAnonymous]
[Route("api/safe")]
public class SafeController
{
    [HttpGet]
    public object Get() => null;
}
""");

        var slice = JsonSerializer.Deserialize<MethodsSlice>(Depscan.Dosai.GetMethods(_tempDirectory.Path), new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() }
        });

        var controller = Assert.Single(slice!.Services!, s => s.Name == "Safe");
        Assert.Equal(TrustZones.Public, controller.TrustZone);
        Assert.NotEqual(true, controller.CrossesTrustBoundary);
    }

    /// <summary>
    ///     Minimal-API handler method groups bind route/query/body values, so their parameters
    ///     must be seeded exactly like controller actions; without this, minimal-API-only apps
    ///     (the common modern layout) produced zero attacker-controlled sources.
    /// </summary>
    [Fact]
    public void MinimalApiMethodGroupHandler_ParametersAreSeededAsHttp()
    {
        File.WriteAllText(Path.Combine(_tempDirectory.Path, "Program.cs"), """
using System.Diagnostics;

public static class WebApplication
{
    public void MapGet(string template, System.Func<string, string> handler) { }
    public static WebApplication Create() => new();
    public void Run() { }
}

public class Program
{
    public static void Main()
    {
        var app = WebApplication.Create();
        app.MapGet("/items/{id}", GetItem);
    }

    public static string GetItem(string id) => System.Diagnostics.Process.Start("sh", id)?.ToString() ?? "";
}
""");

        var result = DataFlowAnalyzer.Analyze(_tempDirectory.Path);

        Assert.Contains(result.Nodes, node => node is { IsSource: true, Category: "http", MethodName: "GetItem", Name: "id" });
        Assert.Contains(result.Slices, slice => slice is { SourceCategory: "http", SinkCategory: "command" });
    }
}
