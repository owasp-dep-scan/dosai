using System.Text.Json;
using Depscan;
using Depscan.Frameworks;
using Xunit;

namespace Dosai.Tests.Frameworks;

/// <summary>
///     Regression tests for the adversarial branch review findings
///     (~/dosai-branch-adversarial-review.md): S1 DI-parameter filtering, S2 secret markers,
///     S3 MCP confinement, C1 conventional service linkage, C2 .prompty, C3 loopback parsing,
///     P1 artifact hash cap, and the ChatMessage sink narrowing.
/// </summary>
public class AdversarialReviewRegressionTests : IDisposable
{
    private readonly TemporaryDirectory _tempDirectory = new();

    public void Dispose() => _tempDirectory.Dispose();

    // ---- S1: DI-injected handler parameters are not attacker-controlled -----------------

    [Fact]
    public void MinimalApiHandler_InterfaceAndServiceParametersAreNotSeeded()
    {
        File.WriteAllText(Path.Combine(_tempDirectory.Path, "Program.cs"), """
public static class WebApplication
{
    public static WebApplication Create() => new();
    public void MapGet(string template, System.Func<CatalogService, ICatalogApi, string, string, System.Threading.CancellationToken, string> handler) { }
}

public class CatalogService { }
public interface ICatalogApi { }

public class Program
{
    public static void Main()
    {
        var app = WebApplication.Create();
        app.MapGet("/items/{name}", Search);
    }

    public static string Search(CatalogService catalogService, ICatalogApi api, string name, System.Threading.CancellationToken ct)
        => name + catalogService + api + (ct.CanBeCanceled ? "x" : "y");
}
""");

        var result = DataFlowAnalyzer.Analyze(_tempDirectory.Path);

        var httpSources = result.Nodes.Where(node => node.IsSource && node.Category == "http").Select(node => node.Name).ToList();
        Assert.Contains("name", httpSources);
        Assert.DoesNotContain("catalogService", httpSources);
        Assert.DoesNotContain("api", httpSources);
        Assert.DoesNotContain("ct", httpSources);
    }

    [Fact]
    public void MinimalApiHandler_FromServicesParameterIsNotSeeded()
    {
        File.WriteAllText(Path.Combine(_tempDirectory.Path, "Program.cs"), """
public static class WebApplication
{
    public static WebApplication Create() => new();
    public void MapGet(string template, System.Func<Billing, string, string> handler) { }
}

public class Billing { }
public class FromServicesAttribute : System.Attribute { }

public class Program
{
    public static void Main()
    {
        var app = WebApplication.Create();
        app.MapGet("/items/{name}", Search);
    }

    public static string Search([FromServices] Billing billing, string name) => name + billing;
}
""");

        var result = DataFlowAnalyzer.Analyze(_tempDirectory.Path);

        var httpSources = result.Nodes.Where(node => node.IsSource && node.Category == "http").Select(node => node.Name).ToList();
        Assert.Contains("name", httpSources);
        Assert.DoesNotContain("billing", httpSources);
    }

    // ---- S1 (follow-on): lambda parameters never inherit the enclosing action's attributes -

    [Fact]
    public void LambdaParameter_DoesNotBecomeHttpSourceViaEnclosingHttpAttribute()
    {
        File.WriteAllText(Path.Combine(_tempDirectory.Path, "Controller.cs"), """
public class RouteAttribute : System.Attribute { public RouteAttribute(string t) { } }
public class HttpGetAttribute : System.Attribute { }

[Route("api/external")]
public class ExternalController
{
    [HttpGet]
    public string Callback()
    {
        var claims = new[] { "a", "b" };
        return string.Join(",", claims.Select(c => c.ToUpperInvariant()));
    }
}

public static class Enumerable
{
    public static System.Collections.Generic.IEnumerable<R> Select<T, R>(this System.Collections.Generic.IEnumerable<T> source, System.Func<T, R> selector) => null;
}
""");

        var result = DataFlowAnalyzer.Analyze(_tempDirectory.Path);

        Assert.DoesNotContain(result.Nodes, node => node.IsSource && node.Category == "http" && node.Name == "c");
    }

    // ---- S6: the ChatMessage sink matches the exact type, not DTO variants ----------------

    [Fact]
    public void ChatMessageDto_ConstructionIsNotAPromptSink()
    {
        File.WriteAllText(Path.Combine(_tempDirectory.Path, "Dto.cs"), """
public class ChatMessageDto { public ChatMessageDto(string content) { } }
public class ChatMessage { public ChatMessage(string content) { } }

public class Handlers
{
    public static string Relay(string userText) => new ChatMessageDto(userText).ToString();
}
""");

        var result = DataFlowAnalyzer.Analyze(_tempDirectory.Path);

        Assert.DoesNotContain(result.Nodes, node => node.IsSink && node.Category == "prompt" && node.Symbol?.Contains("ChatMessageDto") == true);
    }

    // ---- C1: conventional routes link operations and entry-point ids to their service ----

    [Fact]
    public void ConventionalRoutes_AreLinkedToTheirControllerService()
    {
        File.WriteAllText(Path.Combine(_tempDirectory.Path, "Conventional.cs"), """
public class Program
{
    public static void Main()
    {
        var builder = Microsoft.AspNetCore.Builder.WebApplication.Create();
        builder.MapDefaultControllerRoute();
    }
}

public class OrdersController
{
    public object Index() => null;
}
""");

        var slice = global::Depscan.Dosai.GetMethodsSlice(_tempDirectory.Path);

        var service = Assert.Single(slice.Services ?? [], candidate => candidate.Framework == "aspnetcore-mvc" && candidate.Name == "Orders");
        var operation = Assert.Single(service.Operations);
        Assert.Equal("/Orders/Index", operation.Path);
        Assert.Contains($"ep:{operation.Id}", service.EntryPointIds);
        Assert.Contains(slice.EntryPoints ?? [], entryPoint => entryPoint.Id == $"ep:{operation.Id}");
    }

    // ---- C2: .prompty files are classified as config and ingested as prompts -------------

    [Fact]
    public void PromptyFiles_AreClassifiedAsConfigFiles()
    {
        var sources = new List<string>();
        var templates = new List<string>();
        var protos = new List<string>();
        var configs = new List<string>();
        FrameworkContext.ClassifyFile("/app/prompts/assistant.prompty", sources, templates, protos, configs);
        Assert.Contains("/app/prompts/assistant.prompty", configs);
    }

    [Fact]
    public void PromptyFile_ProducesRedactedPromptComponent()
    {
        Directory.CreateDirectory(Path.Combine(_tempDirectory.Path, "prompts"));
        File.WriteAllText(Path.Combine(_tempDirectory.Path, "prompts", "assistant.prompty"), $$"""
---
name: assistant
---
You are a helpful assistant that answers questions about the catalog. Be terse and cite items.
""");

        var slice = global::Depscan.Dosai.GetMethodsSlice(_tempDirectory.Path);

        var prompt = (slice.AiComponents ?? []).FirstOrDefault(component => component.Kind == "prompt");
        Assert.NotNull(prompt);
        Assert.StartsWith("prompt-", prompt.Name);
        Assert.NotNull(prompt.PromptText); // preview emitted for benign prose
    }

    // ---- S2: secret-shaped candidate prompts are withheld entirely ------------------------

    [Theory]
    [InlineData("You are an assistant. eyJhbGciOiJIUzI1NiJ9.e30.signature-token-value-here")]
    [InlineData("Help the user. SharedAccessSignature sr=hub&sv=2020-01-01&sig=abc123")]
    [InlineData("Be brief. github_pat_11AAAAAAA0abcdefghijklmnop")]
    public void SecretShapedCandidatePrompts_AreWithheld(string promptText)
    {
        Directory.CreateDirectory(Path.Combine(_tempDirectory.Path, "prompts"));
        File.WriteAllText(Path.Combine(_tempDirectory.Path, "prompts", "leaky.prompty"), $$"""
---
name: leaky
---
{{promptText}}
""");

        var slice = global::Depscan.Dosai.GetMethodsSlice(_tempDirectory.Path);

        var prompt = (slice.AiComponents ?? []).FirstOrDefault(component => component.Kind == "prompt");
        Assert.NotNull(prompt);
        Assert.Null(prompt.PromptText); // withheld even though no --include-prompt-text
    }

    // ---- C3: loopback detection compares the parsed host exactly --------------------------

    [Theory]
    [InlineData("https://localhost:5001", true)]
    [InlineData("http://127.0.0.1/config", true)]
    [InlineData("amqps://rabbitmq.internal:5671", false)]
    [InlineData("https://api.localhost.evil.com", false)]
    [InlineData("https://evil.com/?next=http://localhost", false)]
    [InlineData("/queue/orders", true)]
    public void IsLoopback_ParsesHostExactly(string endpoint, bool expected)
    {
        Assert.Equal(expected, FrameworkRegistry.IsLoopback(endpoint));
    }

    // ---- P1: oversized model artifacts are inventoried without hashing -------------------

    [Fact]
    public void OversizedModelArtifact_IsInventoriedWithoutHash()
    {
        var artifactPath = Path.Combine(_tempDirectory.Path, "huge.onnx");
        using (var stream = File.Create(artifactPath))
        {
            stream.SetLength(Depscan.Frameworks.Providers.MlRuntimeProvider.MaxHashableArtifactBytes + 1024 * 1024);
        }

        Assert.Null(Depscan.Frameworks.Providers.MlRuntimeProvider.HashFile(artifactPath));

        var smallPath = Path.Combine(_tempDirectory.Path, "small.onnx");
        File.WriteAllText(smallPath, "tiny");
        Assert.NotNull(Depscan.Frameworks.Providers.MlRuntimeProvider.HashFile(smallPath));
    }

    // ---- S3: MCP path confinement ---------------------------------------------------------

    [Fact]
    public void McpRoot_ConfinesToolPaths()
    {
        var corpusRoot = Path.Combine(_tempDirectory.Path, "root");
        Directory.CreateDirectory(corpusRoot);
        File.WriteAllText(Path.Combine(corpusRoot, "Program.cs"), "public class Program { }");

        var request = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"dosai.methods\",\"arguments\":{\"path\":\"/etc\"}}}" + "\n";
        using var input = new StringReader(request);
        using var output = new StringWriter();
        McpServer.Run(corpusRoot, null, null, corpusRoot, input, output);

        Assert.Contains("outside the --mcp-root confinement", output.ToString());
    }

    [Fact]
    public void McpRoot_AllowsPathsUnderRoot()
    {
        var corpusRoot = Path.Combine(_tempDirectory.Path, "root");
        Directory.CreateDirectory(corpusRoot);
        File.WriteAllText(Path.Combine(corpusRoot, "Program.cs"), "public class Program { }");

        var request = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}" + "\n";
        using var input = new StringReader(request);
        using var output = new StringWriter();
        var exit = McpServer.Run(corpusRoot, null, null, corpusRoot, input, output);

        Assert.Equal(0, exit);
        Assert.Contains("dosai.services", output.ToString());
    }
}
