using System.Text.Json;
using System.Text.Json.Serialization;
using Depscan;
using Depscan.Frameworks;
using Xunit;

namespace Dosai.Tests.Frameworks;

public class ProtobufProviderTests : IDisposable
{
    private readonly TemporaryDirectory _tempDirectory = new();

    public void Dispose() => _tempDirectory.Dispose();

    private MethodsSlice Run(params (string Name, string Content)[] files)
    {
        foreach (var (name, content) in files)
        {
            var path = Path.Combine(_tempDirectory.Path, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        var slice = JsonSerializer.Deserialize<MethodsSlice>(Depscan.Dosai.GetMethods(_tempDirectory.Path), new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() }
        });
        Assert.NotNull(slice);
        return slice;
    }

    [Fact]
    public void ProtoFile_ServicesAndRpcsAreParsed()
    {
        var slice = Run(("greet.proto", """
syntax = "proto3";
package greet;

service Greeter {
  rpc SayHello (HelloRequest) returns (HelloReply);
  rpc Chat (stream ChatIn) returns (stream ChatOut);
  rpc Echo (EchoRequest) returns (EchoReply) {
    option (google.api.http) = {
      post: "/v1/echo"
      body: "*"
    };
  }
}

message HelloRequest { string name = 1; }
"""));

        var service = Assert.Single(slice.Services ?? [], s => s.Framework == "protobuf" && s.Name == "Greeter");
        Assert.Equal("grpc", service.ServiceKind);
        Assert.Equal("greet", service.Group);
        Assert.Equal(3, service.Operations.Count);
        var sayHello = Assert.Single(service.Operations, o => o.Name == "SayHello");
        Assert.Equal("/greet.Greeter/SayHello", sayHello.Path);
        Assert.Equal("unary", sayHello.StreamingMode);
        Assert.Equal("HelloRequest", sayHello.RequestType);
        var chat = Assert.Single(service.Operations, o => o.Name == "Chat");
        Assert.Equal("bidi", chat.StreamingMode);
        var echo = Assert.Single(service.Operations, o => o.Name == "Echo");
        Assert.Equal("POST", echo.Properties["httpVerb"]);
        Assert.Equal("/v1/echo", echo.Properties["httpPath"]);
        // Request/response messages are recorded as unknown-classification data flows.
        Assert.Contains(service.Data, data => data is { Name: "HelloRequest", Flow: "inbound", Classification: "unknown" });
    }

    [Fact]
    public void CsprojGrpcServicesMetadata_SetsDirection()
    {
        var slice = Run(
            ("App.csproj", """
<Project Sdk="Microsoft.NET.Sdk.Web">
  <ItemGroup>
    <Protobuf Include="Protos\greet.proto" GrpcServices="Server" />
  </ItemGroup>
</Project>
"""),
            ("Protos/greet.proto", """
syntax = "proto3";
package greet;
service Greeter { rpc SayHello (HelloRequest) returns (HelloReply); }
message HelloRequest { string name = 1; }
"""));

        var service = Assert.Single(slice.Services ?? [], s => s.Framework == "protobuf");
        Assert.Equal("Server", service.Properties["grpcServices"]);
    }
}

public class GrpcProviderTests : IDisposable
{
    private readonly TemporaryDirectory _tempDirectory = new();

    public void Dispose() => _tempDirectory.Dispose();

    private MethodsSlice Run(params (string Name, string Content)[] files)
    {
        foreach (var (name, content) in files)
        {
            var path = Path.Combine(_tempDirectory.Path, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        var slice = JsonSerializer.Deserialize<MethodsSlice>(Depscan.Dosai.GetMethods(_tempDirectory.Path), new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() }
        });
        Assert.NotNull(slice);
        return slice;
    }

    [Fact]
    public void ServiceImplementation_JoinsProtoContractForPath()
    {
        var slice = Run(
            ("greet.proto", """
syntax = "proto3";
package greet;
service Greeter { rpc SayHello (HelloRequest) returns (HelloReply); }
message HelloRequest { string name = 1; }
"""),
            ("GreeterService.cs", """
namespace Services;

public class GreeterService : Greeter.GreeterBase
{
    public override Task<HelloReply> SayHello(HelloRequest request, ServerCallContext context) => null;
}
"""));

        var endpoint = Assert.Single(slice.ApiEndpoints ?? [], e => e.EndpointKind == "Grpc");
        Assert.Equal("/greet.Greeter/SayHello", endpoint.Path);
        Assert.Equal("grpc", endpoint.Framework);
        var service = Assert.Single(slice.Services ?? [], s => s.Framework == "grpc");
        Assert.Equal("grpc", service.ServiceKind);
        var operation = Assert.Single(service.Operations);
        Assert.Equal("unary", operation.StreamingMode);
    }

    [Fact]
    public void ServerWithoutProto_DegradesToHeuristicPath()
    {
        var slice = Run(("Service.cs", """
namespace Services;

public class OrderService : Order.OrderBase
{
    public override Task<Reply> Create(Request request, ServerCallContext context) => null;
}
"""));

        var endpoint = Assert.Single(slice.ApiEndpoints ?? [], e => e.EndpointKind == "Grpc");
        // No .proto file: the path falls back to namespace.Type/Method at low confidence.
        Assert.Equal("/Services.OrderService/Create", endpoint.Path);
        Assert.Equal("low", endpoint.Confidence);
    }

    [Fact]
    public void GrpcChannelForAddress_EmitsOutboundService()
    {
        var slice = Run(("Client.cs", """
namespace Clients;

public class GreeterClient
{
    public void Call()
    {
        var channel = GrpcChannel.ForAddress("https://orders.example.com:5001");
    }
}
"""));

        var outbound = Assert.Single(slice.Services ?? [], s => s.Direction == "outbound" && s.Framework == "grpc");
        Assert.Contains("https://orders.example.com:5001", outbound.Endpoints);
    }

    [Fact]
    public void PlainClassWithoutBase_IsNotAGrpcService()
    {
        var slice = Run(("Plain.cs", """
public class PlainService
{
    public void Handle(object request) { }
}
"""));

        Assert.DoesNotContain(slice.ApiEndpoints ?? [], e => e.EndpointKind == "Grpc");
    }
}

public class ServerlessProviderTests : IDisposable
{
    private readonly TemporaryDirectory _tempDirectory = new();

    public void Dispose() => _tempDirectory.Dispose();

    private MethodsSlice Run(params (string Name, string Content)[] files)
    {
        foreach (var (name, content) in files)
        {
            var path = Path.Combine(_tempDirectory.Path, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        var slice = JsonSerializer.Deserialize<MethodsSlice>(Depscan.Dosai.GetMethods(_tempDirectory.Path), new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() }
        });
        Assert.NotNull(slice);
        return slice;
    }

    [Fact]
    public void HttpTrigger_EmitsEndpointWithRoutePrefixAndAuthLevel()
    {
        var slice = Run(("Functions.cs", """
public class FunctionNameAttribute : System.Attribute { public FunctionNameAttribute(string name) { } }
public class HttpTriggerAttribute : System.Attribute { public HttpTriggerAttribute(params object[] args) { } public string Route { get; set; } }
public class QueueOutputAttribute : System.Attribute { public QueueOutputAttribute(string queue) { } }

public class OrderFunctions
{
    [FunctionName("CreateOrder")]
    [HttpTrigger("post", Route = "orders/{id}")]
    [QueueOutput("orders-created")]
    public static string Create(string id) => id;
}
"""));

        var endpoint = Assert.Single(slice.ApiEndpoints ?? [], e => e.MethodName == "Create");
        Assert.Equal("/api/orders/{id}", endpoint.Path);
        Assert.Equal("POST", endpoint.HttpMethod);
        Assert.Equal("AzureFunction", endpoint.EndpointKind);
        var service = Assert.Single(slice.Services ?? [], s => s.Framework == "azure-functions");
        Assert.Equal("function", service.ServiceKind);
        Assert.Contains("egress:orders-created", service.Tags);
        Assert.Contains("/api/orders/{id}", service.Endpoints);
    }

    [Fact]
    public void AnonymousHttpTrigger_IsFlaggedPublic()
    {
        var slice = Run(("Anon.cs", """
public class FunctionNameAttribute : System.Attribute { public FunctionNameAttribute(string name) { } }
public class HttpTriggerAttribute : System.Attribute { public HttpTriggerAttribute(params object[] args) { } }

public class PublicFunctions
{
    [FunctionName("Ping")]
    [HttpTrigger("get", "Anonymous")]
    public static string Ping() => "pong";
}
"""));

        var service = Assert.Single(slice.Services ?? [], s => s.Framework == "azure-functions");
        Assert.True(service.AllowAnonymous);
        Assert.False(service.Authenticated);
        Assert.Equal("public", service.TrustZone);
        Assert.Contains("finding:anonymous-http-trigger", service.Tags);
    }

    [Fact]
    public void TimerTrigger_RecordsCron()
    {
        var slice = Run(("Timer.cs", """
public class FunctionNameAttribute : System.Attribute { public FunctionNameAttribute(string name) { } }
public class TimerTriggerAttribute : System.Attribute { public TimerTriggerAttribute(string cron) { } }

public class ScheduledFunctions
{
    [FunctionName("Cleanup")]
    [TimerTrigger("0 */5 * * * *")]
    public static void Cleanup() { }
}
"""));

        var service = Assert.Single(slice.Services ?? [], s => s.Framework == "azure-functions");
        Assert.Equal("scheduled", service.ServiceKind);
        Assert.Equal("0 */5 * * * *", service.Properties["cron"]);
    }

    [Fact]
    public void HostJsonRoutePrefix_IsApplied()
    {
        var slice = Run(
            ("host.json", """{ "extensions": { "http": { "routePrefix": "" } } }"""),
            ("Fn.cs", """
public class FunctionNameAttribute : System.Attribute { public FunctionNameAttribute(string name) { } }
public class HttpTriggerAttribute : System.Attribute { public HttpTriggerAttribute(params object[] args) { } }

public class Functions
{
    [FunctionName("Ping")]
    [HttpTrigger("get", Route = "ping")]
    public static string Ping() => "pong";
}
"""));

        var endpoint = Assert.Single(slice.ApiEndpoints ?? [], e => e.MethodName == "Ping");
        // Empty routePrefix in host.json removes the /api prefix entirely.
        Assert.Equal("/ping", endpoint.Path);
    }

    [Fact]
    public void LambdaAnnotations_EmitsRestApiEndpoint()
    {
        var slice = Run(("Lambda.cs", """
public class LambdaFunctionAttribute : System.Attribute { public LambdaFunctionAttribute() { } }
public class RestApiAttribute : System.Attribute { public RestApiAttribute(string route) { } }

public class Functions
{
    [LambdaFunction]
    [RestApi("/orders/{id}")]
    public string GetOrder(string id) => id;
}
"""));

        var endpoint = Assert.Single(slice.ApiEndpoints ?? [], e => e.EndpointKind == "LambdaFunction");
        Assert.Equal("/orders/{id}", endpoint.Path);
        Assert.Equal("aws-lambda", endpoint.Framework);
    }

    [Fact]
    public void ClassicLambdaHandlerSignature_IsDetected()
    {
        var slice = Run(("Handler.cs", """
public interface ILambdaContext { }

public class Handler
{
    public string HandleUpper(string input, ILambdaContext context) => input.ToUpperInvariant();
}
"""));

        var service = Assert.Single(slice.Services ?? [], s => s.Framework == "aws-lambda");
        Assert.Equal("function", service.ServiceKind);
    }
}
