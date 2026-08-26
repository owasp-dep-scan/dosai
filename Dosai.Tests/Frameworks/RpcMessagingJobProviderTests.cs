using System.Text.Json;
using System.Text.Json.Serialization;
using Depscan;
using Depscan.Frameworks;
using Xunit;

namespace Dosai.Tests.Frameworks;

public class SignalRProviderTests : IDisposable
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
    public void HubWithMapHubMount_EmitsServiceAndMethodOperations()
    {
        var slice = Run(("Program.cs", """
var app = Microsoft.AspNetCore.Builder.WebApplication.Create();
app.MapHub<ChatHub>("/chat");
"""), ("Chat.cs", """
public class Hub { }
public class HubMethodNameAttribute : System.Attribute { public HubMethodNameAttribute(string name) { } }
public class AuthorizeAttribute : System.Attribute { }

[Authorize]
public class ChatHub : Hub
{
    public void Broadcast(string message) { }

    [HubMethodName("whisper")]
    public void SendPrivate(string target, string text) { }
}
"""));

        var service = Assert.Single(slice.Services ?? [], s => s.Framework == "signalr" && s.Name == "ChatHub");
        Assert.Equal("websocket", service.ServiceKind);
        Assert.True(service.Authenticated);
        Assert.Contains("/chat", service.Endpoints);
        Assert.Equal(2, service.Operations.Count);
        Assert.Contains(service.Operations, operation => operation.Name == "whisper");

        var endpoint = Assert.Single(slice.ApiEndpoints ?? [], e => e.EndpointKind == "SignalRHub" && e.MethodName == "Broadcast");
        Assert.Equal("/chat", endpoint.Path);
        Assert.Equal("signalr", endpoint.Framework);
    }

    [Fact]
    public void HubWithoutMount_LeavesEndpointsEmptyWithoutCrashing()
    {
        var slice = Run(("Lonely.cs", """
public class Hub { }

public class LonelyHub : Hub
{
    public void Ping() { }
}
"""));

        var service = Assert.Single(slice.Services ?? [], s => s.Framework == "signalr");
        Assert.Empty(service.Endpoints);
    }

    [Fact]
    public void HubConnectionBuilderWithUrl_EmitsOutboundService()
    {
        var slice = Run(("Client.cs", """
public class HubConnectionBuilder { public HubConnectionBuilder WithUrl(string url) => this; }

public class ChatClient
{
    public void Connect()
    {
        var connection = new HubConnectionBuilder().WithUrl("https://chat.example.com/hubs/chat");
    }
}
"""));

        var outbound = Assert.Single(slice.Services ?? [], s => s.Framework == "signalr" && s.Direction == "outbound");
        Assert.Contains("https://chat.example.com/hubs/chat", outbound.Endpoints);
    }

    [Fact]
    public void PlainClassDerivingNothing_IsNotAHub()
    {
        var slice = Run(("Plain.cs", """
public class ChatHelper
{
    public void Broadcast(string message) { }
}
"""));

        Assert.DoesNotContain(slice.Services ?? [], s => s.Framework == "signalr");
    }
}

public class GraphQLODataProviderTests : IDisposable
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
    public void QueryType_EmitsResolversPostedToGraphQLMount()
    {
        var slice = Run(("Program.cs", """
var app = Microsoft.AspNetCore.Builder.WebApplication.Create();
app.MapGraphQL("/graphql-api");
"""), ("Query.cs", """
public class QueryTypeAttribute : System.Attribute { }

[QueryType]
public class OrderQuery
{
    public string Orders() => "[]";
}
"""));

        var service = Assert.Single(slice.Services ?? [], s => s.Framework == "graphql");
        Assert.Equal("graphql", service.ServiceKind);
        Assert.Contains("/graphql-api", service.Endpoints);
        var operation = Assert.Single(service.Operations);
        Assert.Equal("Orders", operation.Name);
        Assert.Equal("POST", operation.HttpMethod);

        var endpoint = Assert.Single(slice.ApiEndpoints ?? [], e => e.EndpointKind == "GraphQL");
        Assert.Equal("/graphql-api", endpoint.Path);
    }

    [Fact]
    public void IntrospectionSetting_IsSurfacedAsProperty()
    {
        var slice = Run(("Gql.cs", """
var builder = Microsoft.AspNetCore.Builder.WebApplication.Create();
builder.Services.AddGraphQLServer().DisableIntrospection();

public class QueryTypeAttribute : System.Attribute { }

[QueryType]
public class CatalogQuery
{
    public string Items() => "[]";
}
"""));

        var service = Assert.Single(slice.Services ?? [], s => s.Framework == "graphql");
        Assert.Equal("disabled", service.Properties["introspection"]);
    }

    [Fact]
    public void EnableQuery_EmitsODataEndpoint()
    {
        var slice = Run(("OData.cs", """
public class EnableQueryAttribute : System.Attribute { public int MaxExpansionDepth { get; set; } }

public class OrdersController
{
    [EnableQuery(MaxExpansionDepth = 2)]
    public object Get() => null;
}
"""));

        var service = Assert.Single(slice.Services ?? [], s => s.ServiceKind == "odata");
        var operation = Assert.Single(service.Operations);
        Assert.Equal("GET", operation.HttpMethod);
        Assert.Equal("2", operation.Properties["maxExpansionDepth"]);
        var endpoint = Assert.Single(slice.ApiEndpoints ?? [], e => e.EndpointKind == "OData");
        Assert.Equal("GET", endpoint.HttpMethod);
    }
}

public class MessagingProviderTests : IDisposable
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
    public void MassTransitConsumer_EmitsInboundQueueService()
    {
        var slice = Run(("Consumer.cs", """
namespace MassTransit
{
    public interface IConsumer<T> where T : class { }
}

public class SubmitOrderConsumer : MassTransit.IConsumer<SubmitOrder>
{
    public void Consume(SubmitOrder message) { }
}

public class SubmitOrder { }
"""));

        var service = Assert.Single(slice.Services ?? [], s => s.Framework == "messaging" && s.Direction == "inbound");
        Assert.Equal("queue", service.ServiceKind);
        Assert.Equal("masstransit", service.Properties["framework"]);
        Assert.Contains("SubmitOrder", service.Properties["messageType"]);
        var operation = Assert.Single(service.Operations);
        Assert.Equal("Consume", operation.Name);
        var endpoint = Assert.Single(slice.ApiEndpoints ?? [], e => e.EndpointKind == "MessageConsumer");
        Assert.Equal("MessageConsumer", endpoint.EndpointKind);
    }

    [Fact]
    public void PublishInvocation_EmitsOutboundService()
    {
        var slice = Run(("Publisher.cs", """
public class PublishEndpoint
{
    public void Publish<T>(T message) { }
}

public class OrderEvents
{
    public void Announce()
    {
        var endpoint = new PublishEndpoint();
        endpoint.Publish<OrderPlaced>(new OrderPlaced());
    }
}

public class OrderPlaced { }
"""));

        var outbound = Assert.Single(slice.Services ?? [], s => s.Framework == "messaging" && s.Direction == "outbound");
        Assert.Equal("pubsub", outbound.ServiceKind);
        Assert.Equal("OrderEvents", outbound.Name);
    }

    [Fact]
    public void NonConsumerClassWithConsumeWord_IsNotAQueue()
    {
        var slice = Run(("Guard.cs", """
public class FoodProcessor
{
    public void Consume() { }
}
"""));

        Assert.DoesNotContain(slice.Services ?? [], s => s.Framework == "messaging");
    }
}

public class BackgroundJobProviderTests : IDisposable
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
    public void BackgroundService_EmitsScheduledServiceWithRegistration()
    {
        var slice = Run(("Worker.cs", """
public interface IHostedService { }
public abstract class BackgroundService : IHostedService { }

public static class ServiceCollectionExtensions
{
    public static void AddHostedService<T>() where T : IHostedService { }
}

public class Program
{
    public static void Main()
    {
        ServiceCollectionExtensions.AddHostedService<CleanupWorker>();
    }
}

public class CleanupWorker : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) { }
}
"""));

        var service = Assert.Single(slice.Services ?? [], s => s.Framework == "background-jobs" && s.Name == "CleanupWorker");
        Assert.Equal("scheduled", service.ServiceKind);
        Assert.Equal("true", service.Properties["registered"]);
        Assert.Contains(service.Operations, operation => operation.Name == "ExecuteAsync");
        Assert.Contains(slice.EntryPoints ?? [], entryPoint => entryPoint is { Kind: "HostedService", MethodName: "ExecuteAsync" });
    }

    [Fact]
    public void HangfireRecurringJob_RecordsCronAndHumanizedSchedule()
    {
        var slice = Run(("Jobs.cs", """
public static class RecurringJob
{
    public static void AddOrUpdate(string id, object job, string cron) { }
}

public class Program
{
    public static void Main()
    {
        RecurringJob.AddOrUpdate("cleanup", new object(), "0 0 * * *");
    }
}
"""));

        var service = Assert.Single(slice.Services ?? [], s => s.Properties.TryGetValue("framework", out var framework) && framework == "hangfire");
        Assert.Equal("0 0 * * *", service.Properties["cron"]);
        Assert.Equal("daily at 00:00", service.Properties["schedule"]);
    }

    [Fact]
    public void HangfireDashboardWithoutAuthorization_IsFlagged()
    {
        var slice = Run(("Dashboard.cs", """
var app = Microsoft.AspNetCore.Builder.WebApplication.Create();
app.MapHangfireDashboard("/hangfire");
"""));

        var dashboard = Assert.Single(slice.Services ?? [], s => s.Name == "Hangfire dashboard");
        Assert.Equal("missing", dashboard.Properties["dashboardAuthorization"]);
        Assert.Contains("finding:unauthenticated-hangfire-dashboard", dashboard.Tags);
    }

    [Fact]
    public void QuartzJob_IsDetected()
    {
        var slice = Run(("Job.cs", """
public interface IJob { }

public class ArchiveJob : IJob
{
    public void Execute() { }
}
"""));

        var service = Assert.Single(slice.Services ?? [], s => s.Framework == "background-jobs");
        Assert.Equal("ArchiveJob", service.Name);
        Assert.Contains(slice.EntryPoints ?? [], entryPoint => entryPoint is { Kind: "ScheduledJob", MethodName: "Execute" });
    }
}
