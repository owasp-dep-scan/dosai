using Depscan;
using Dosai.Tests.Roadmap;
using Xunit;

namespace Dosai.Tests;

/// <summary>
///     Forwarded-header tracking: request values the ASP.NET Core ForwardedHeaders middleware
///     rewrites (remote address and port, host, scheme) and direct reads of the
///     Forwarded/X-Forwarded-* headers by literal name are <c>proxy-derived</c> sources, and the
///     code and configuration that turn the middleware on raise a <c>ForwardedHeadersEnabled</c>
///     finding. Framework types are stubbed in their real namespaces. Every rule has a twin that
///     must stay quiet: the same member name on an application class, a non-literal header name,
///     Response.Headers, ForwardedHeaders.None, the setting set to false, and an application
///     method named UseForwardedHeaders.
/// </summary>
public class ForwardedHeaderTests
{
    private const string HttpStubs = """
using System.Net;

namespace Microsoft.Extensions.Primitives
{
    public struct StringValues { public override string ToString() => string.Empty; }
}
namespace Microsoft.AspNetCore.Http
{
    public struct HostString { public override string ToString() => string.Empty; }
    public abstract class ConnectionInfo
    {
        public abstract IPAddress? RemoteIpAddress { get; }
        public abstract int RemotePort { get; }
    }
    public class DefaultConnectionInfo : ConnectionInfo
    {
        public override IPAddress? RemoteIpAddress => null;
        public override int RemotePort => 0;
    }
    public interface IHeaderDictionary
    {
        Microsoft.Extensions.Primitives.StringValues this[string key] { get; }
        bool TryGetValue(string key, out Microsoft.Extensions.Primitives.StringValues value);
    }
    public class HeaderDictionary : IHeaderDictionary
    {
        public Microsoft.Extensions.Primitives.StringValues this[string key] => default;
        public bool TryGetValue(string key, out Microsoft.Extensions.Primitives.StringValues value) { value = default; return false; }
    }
    public class HttpContext
    {
        public ConnectionInfo Connection { get; } = new DefaultConnectionInfo();
        public HttpRequest Request { get; } = new();
        public HttpResponse Response { get; } = new();
    }
    public interface IQueryCollection { Microsoft.Extensions.Primitives.StringValues this[string key] { get; } }
    public class QueryCollection : IQueryCollection { public Microsoft.Extensions.Primitives.StringValues this[string key] => default; }
    public class HttpRequest
    {
        public IQueryCollection Query { get; } = new QueryCollection();
        public HostString Host { get; } = default;
        public string Scheme { get; } = string.Empty;
        public bool IsHttps { get; }
        public IHeaderDictionary Headers { get; } = new HeaderDictionary();
    }
    public class HttpResponse { public IHeaderDictionary Headers { get; } = new HeaderDictionary(); }
}
namespace Microsoft.AspNetCore.Http.Features
{
    public interface IHttpConnectionFeature { IPAddress? RemoteIpAddress { get; } }
}
namespace Microsoft.AspNetCore.Http.Extensions
{
    public static class UriHelper
    {
        public static string GetDisplayUrl(Microsoft.AspNetCore.Http.HttpRequest request) => string.Empty;
        public static string GetEncodedUrl(Microsoft.AspNetCore.Http.HttpRequest request) => string.Empty;
        public static string BuildAbsolute(string scheme, Microsoft.AspNetCore.Http.HostString host) => string.Empty;
    }
}
namespace System.Web
{
    public class HttpRequest
    {
        public System.Uri? Url { get; }
        public System.Collections.Specialized.NameValueCollection Headers { get; } = new();
        public System.Collections.Specialized.NameValueCollection ServerVariables { get; } = new();
    }
    public class HttpContext { public HttpRequest Request { get; } = new(); }
}
namespace Microsoft.AspNetCore.Mvc
{
    [System.AttributeUsage(System.AttributeTargets.Parameter)] public sealed class FromHeaderAttribute : System.Attribute
    {
        public FromHeaderAttribute() { }
        public FromHeaderAttribute(string name) { }
        public string? Name { get; set; }
    }
    public interface IActionResult { }
    public class RedirectResult : IActionResult { public RedirectResult(string url) { } }
    public abstract class ControllerBase { public RedirectResult Redirect(string url) => new(url); }
}
""";

    private const string SourceFixture = """
using System;
using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Http.Features;

public static class Handlers
{
    public static void ClientIp(HttpContext ctx) => Process.Start("tool", ctx.Connection.RemoteIpAddress?.ToString() ?? "");
    public static void AppClientIp(AppConnection conn) => Process.Start("tool", conn.RemoteIpAddress?.ToString() ?? "");
    public static void ClientPort(HttpContext ctx) => Process.Start("tool", ctx.Connection.RemotePort.ToString());
    public static void FeatureClientIp(IHttpConnectionFeature feature) => Process.Start("tool", feature.RemoteIpAddress?.ToString() ?? "");
    public static void Host(HttpContext ctx) => Process.Start("tool", ctx.Request.Host.ToString());
    public static void AppHost(AppRequest request) => Process.Start("tool", request.Host);
    public static void DisplayUrl(HttpContext ctx) => Process.Start("tool", UriHelper.GetDisplayUrl(ctx.Request));
    public static void EncodedUrl(HttpContext ctx) => Process.Start("tool", UriHelper.GetEncodedUrl(ctx.Request));
    public static void LegacyUrl(System.Web.HttpContext legacy) => Process.Start("tool", legacy.Request.Url?.ToString() ?? "");
    public static void Scheme(HttpContext ctx) => Process.Start("tool", ctx.Request.Scheme);
    public static void IsHttps(HttpContext ctx) => Process.Start("tool", ctx.Request.IsHttps.ToString());
    public static void HeaderIndexer(HttpContext ctx) => Process.Start("tool", ctx.Request.Headers["X-Forwarded-For"].ToString());
    public static void HeaderIndexerLowercase(HttpContext ctx) => Process.Start("tool", ctx.Request.Headers["x-real-ip"].ToString());
    public static void HeaderTryGetValue(HttpContext ctx) { ctx.Request.Headers.TryGetValue("X-Forwarded-Proto", out var proto); Process.Start("tool", proto.ToString()); }
    public static void HeaderNonLiteral(HttpContext ctx) { var name = Console.ReadLine() ?? ""; Process.Start("tool", ctx.Request.Headers[name].ToString()); }
    public static void ResponseHeader(HttpContext ctx) => Process.Start("tool", ctx.Response.Headers["X-Forwarded-For"].ToString());
    public static void QueryIndexer(HttpContext ctx) => Process.Start("tool", ctx.Request.Query["X-Forwarded-For"].ToString());
    public static void ServerVariablesCgi(System.Web.HttpContext legacy) => Process.Start("tool", legacy.Request.ServerVariables["HTTP_X_FORWARDED_FOR"] ?? "");
    public static void LegacyHeaderGet(System.Web.HttpContext legacy) => Process.Start("tool", legacy.Request.Headers.Get("X-Forwarded-Host") ?? "");
    public static void FromHeaderForwarded([Microsoft.AspNetCore.Mvc.FromHeader(Name = "X-Forwarded-For")] string ip) => Process.Start("tool", ip);
    public static void FromHeaderOther([Microsoft.AspNetCore.Mvc.FromHeader(Name = "X-Correlation-Id")] string id) => Process.Start("tool", id);
}

public class RedirectController : Microsoft.AspNetCore.Mvc.ControllerBase
{
    public void HostRedirect(HttpContext ctx) => Redirect(ctx.Request.Host.ToString());
}

public class AppConnection { public IPAddress? RemoteIpAddress { get; } }
public class AppRequest { public string Host { get; } = string.Empty; }
""";

    private static DataFlowResult AnalyzeSource(string source)
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("Program.cs", source + HttpStubs);
        return DataFlowAnalyzer.Analyze(directory.Path);
    }

    /// <summary>The source categories (and forwarded-header properties) of a method's source nodes.</summary>
    private static Dictionary<string, List<(string? Category, string? Kind, string? Header)>> SourcesByMethod(DataFlowResult result) =>
        result.Nodes
            .Where(node => node.IsSource)
            .GroupBy(node => node.MethodName ?? string.Empty)
            .ToDictionary(group => group.Key, group => group.Select(node => (node.Category, node.Kind, node.Properties.GetValueOrDefault("forwardedHeader"))).ToList(), StringComparer.Ordinal);

    private static bool IsProxyDerived(Dictionary<string, List<(string? Category, string? Kind, string? Header)>> sources, string method) =>
        sources.TryGetValue(method, out var entries) && entries.Any(entry => entry.Category == "proxy-derived");

    private static void AssertSlice(DataFlowResult result, string method, string sinkCategory, string kind)
    {
        var nodes = result.Nodes.ToDictionary(node => node.Id);
        Assert.Contains(result.Slices, slice =>
            nodes.TryGetValue(slice.SourceId, out var source) && source.MethodName == method &&
            slice.SourceCategory == "proxy-derived" && slice.SinkCategory == sinkCategory &&
            slice.TaintKinds.Contains(kind, StringComparer.Ordinal));
    }

    [Fact]
    public void RewrittenRequestValues_AreProxyDerivedSources()
    {
        var result = AnalyzeSource(SourceFixture);
        var sources = SourcesByMethod(result);

        // client-ip: the middleware rewrites RemoteIpAddress and RemotePort from X-Forwarded-For.
        Assert.True(IsProxyDerived(sources, "ClientIp"));
        Assert.True(IsProxyDerived(sources, "ClientPort"));
        Assert.True(IsProxyDerived(sources, "FeatureClientIp"));
        // host: Host is rewritten from X-Forwarded-Host, the display/encoded URL builds on it.
        Assert.True(IsProxyDerived(sources, "Host"));
        Assert.True(IsProxyDerived(sources, "DisplayUrl"));
        Assert.True(IsProxyDerived(sources, "EncodedUrl"));
        // scheme: Scheme is rewritten from X-Forwarded-Proto and IsHttps derives from it.
        Assert.True(IsProxyDerived(sources, "Scheme"));
        Assert.True(IsProxyDerived(sources, "IsHttps"));
        // The legacy System.Web Url is rebuilt from the client-chosen Host header.
        Assert.True(IsProxyDerived(sources, "LegacyUrl"));

        AssertSlice(result, "ClientIp", "command", "client-ip");
        AssertSlice(result, "ClientPort", "command", "client-ip");
        AssertSlice(result, "FeatureClientIp", "command", "client-ip");
        AssertSlice(result, "Host", "command", "host");
        AssertSlice(result, "DisplayUrl", "command", "host");
        AssertSlice(result, "Scheme", "command", "scheme");
        AssertSlice(result, "IsHttps", "command", "scheme");
        AssertSlice(result, "LegacyUrl", "command", "host");

        // End-to-end: a rewritten host reaching a redirect sink.
        var nodes = result.Nodes.ToDictionary(node => node.Id);
        Assert.Contains(result.Slices, slice =>
            nodes.TryGetValue(slice.SourceId, out var source) && source.MethodName == "HostRedirect" &&
            slice.SourceCategory == "proxy-derived" && slice.SinkCategory == "redirect");
    }

    [Fact]
    public void RewrittenRequestValues_TwinsOnApplicationTypes_StayUntagged()
    {
        var result = AnalyzeSource(SourceFixture);
        var sources = SourcesByMethod(result);

        // The same member names on application classes are not the framework's accessors.
        Assert.False(IsProxyDerived(sources, "AppClientIp"));
        Assert.False(IsProxyDerived(sources, "AppHost"));
    }

    [Fact]
    public void DirectHeaderReads_AreForwardedHeaderSources()
    {
        var result = AnalyzeSource(SourceFixture);
        var sources = SourcesByMethod(result);

        Assert.True(IsProxyDerived(sources, "HeaderIndexer"));
        Assert.True(IsProxyDerived(sources, "HeaderIndexerLowercase"));
        Assert.True(IsProxyDerived(sources, "HeaderTryGetValue"));
        Assert.True(IsProxyDerived(sources, "ServerVariablesCgi"));
        Assert.True(IsProxyDerived(sources, "LegacyHeaderGet"));
        Assert.True(IsProxyDerived(sources, "FromHeaderForwarded"));

        var forwardedHeader = ForwardedHeaderOf(result, "HeaderIndexer");
        Assert.Equal("x-forwarded-for", forwardedHeader);
        Assert.Equal("x-real-ip", ForwardedHeaderOf(result, "HeaderIndexerLowercase"));
        Assert.Equal("x-forwarded-proto", ForwardedHeaderOf(result, "HeaderTryGetValue"));
        // The CGI server-variable spelling decodes to the same header name.
        Assert.Equal("x-forwarded-for", ForwardedHeaderOf(result, "ServerVariablesCgi"));
        Assert.Equal("x-forwarded-host", ForwardedHeaderOf(result, "LegacyHeaderGet"));
        Assert.Equal("x-forwarded-for", ForwardedHeaderOf(result, "FromHeaderForwarded"));

        AssertSlice(result, "HeaderIndexer", "command", "forwarded-header");
        AssertSlice(result, "HeaderTryGetValue", "command", "forwarded-header");
        AssertSlice(result, "LegacyHeaderGet", "command", "forwarded-header");
    }

    [Fact]
    public void DirectHeaderReads_Twins_StayUntagged()
    {
        var result = AnalyzeSource(SourceFixture);
        var sources = SourcesByMethod(result);

        // A non-literal header name, the same literal through Response.Headers, and a
        // FromHeader naming a header outside the forwarded set are not forwarded-header reads.
        Assert.False(IsProxyDerived(sources, "HeaderNonLiteral"));
        Assert.False(IsProxyDerived(sources, "ResponseHeader"));
        // The query string is not a forwarded header, whatever the literal names.
        Assert.False(IsProxyDerived(sources, "QueryIndexer"));
        Assert.False(IsProxyDerived(sources, "FromHeaderOther"));

        // The generic header source still applies to the non-literal and non-forwarded reads.
        Assert.Contains(sources.GetValueOrDefault("HeaderNonLiteral") ?? [], entry => entry.Category == "http");
        Assert.Contains(sources.GetValueOrDefault("FromHeaderOther") ?? [], entry => entry.Category == "http");
    }

    private static string? ForwardedHeaderOf(DataFlowResult result, string method) =>
        result.Nodes.FirstOrDefault(node => node.IsSource && node.MethodName == method && node.Properties.ContainsKey("forwardedHeader"))?.Properties.GetValueOrDefault("forwardedHeader");

    private const string FindingStubs = """
namespace Microsoft.AspNetCore.HttpOverrides
{
    [System.Flags]
    public enum ForwardedHeaders { None = 0, XForwardedFor = 1, XForwardedHost = 2, XForwardedProto = 4, XForwardedPrefix = 8, All = 15 }
}
namespace Microsoft.AspNetCore.Builder
{
    public interface IApplicationBuilder { }
    public class ForwardedHeadersOptions
    {
        public Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders ForwardedHeaders { get; set; }
        public int? ForwardLimit { get; set; }
        public System.Collections.Generic.IList<object> KnownProxies { get; } = new System.Collections.Generic.List<object>();
        public System.Collections.Generic.IList<object> KnownNetworks { get; } = new System.Collections.Generic.List<object>();
    }
    public static class ForwardedHeadersExtensions
    {
        public static IApplicationBuilder UseForwardedHeaders(this IApplicationBuilder app) => app;
        public static IApplicationBuilder UseForwardedHeaders(this IApplicationBuilder app, ForwardedHeadersOptions options) => app;
    }
}
namespace Microsoft.Extensions.DependencyInjection
{
    public interface IServiceCollection { }
    public static class OptionsServiceCollectionExtensions
    {
        public static IServiceCollection Configure<TOptions>(this IServiceCollection services, System.Action<TOptions> configure) where TOptions : class => services;
    }
}
""";

    private const string FindingFixture = """
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;

public static class Setup
{
    public static void Inline(IApplicationBuilder app) =>
        app.UseForwardedHeaders(new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto });
    public static void NoHeaders(IApplicationBuilder app) => app.UseForwardedHeaders();
    public static void AllFlags(IServiceCollection services) =>
        services.Configure<ForwardedHeadersOptions>(options => options.ForwardedHeaders = ForwardedHeaders.All);
    public static void UnboundedHops(IServiceCollection services) =>
        services.Configure<ForwardedHeadersOptions>(options => { options.ForwardedHeaders = ForwardedHeaders.XForwardedFor; options.ForwardLimit = null; });
    public static void Off(IApplicationBuilder app) =>
        app.UseForwardedHeaders(new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.None });
    public static void OtherOptions(IServiceCollection services) =>
        services.Configure<OtherOptions>(options => options.Value = 1);
    public static void AppNamed(IApplicationBuilder app) => AppPipeline.UseForwardedHeaders(app);
    public static void TrustAll(IApplicationBuilder app)
    {
        var forwarded = new ForwardedHeadersOptions();
        forwarded.KnownProxies.Clear();
        app.UseForwardedHeaders(forwarded);
    }
}

public static class AppPipeline { public static IApplicationBuilder UseForwardedHeaders(IApplicationBuilder app) => app; }
public class OtherOptions { public int Value { get; set; } }
""";

    private static List<Depscan.Frameworks.SecurityFinding> AnalyzeFindings(string source)
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("Program.cs", source + FindingStubs);
        return directory.Methods().SecurityFindings!;
    }

    [Fact]
    public void ForwardedHeadersEnabled_IsReportedWhereTheMiddlewareIsTurnedOn()
    {
        var findings = AnalyzeFindings(FindingFixture);
        List<Depscan.Frameworks.SecurityFinding> Of(string kind) => findings.Where(finding => finding.Kind == kind).ToList();

        var enabled = Of("ForwardedHeadersEnabled");
        // Inline options, the DI registration with flags, the flagless call, the unbounded
        // registration and the trust-all site's own call are each their own site; None, other
        // options and an application method of the same name give nothing.
        Assert.Equal(5, enabled.Count);
        Assert.All(enabled, finding => Assert.Equal("info", finding.Severity));
        Assert.All(enabled, finding => Assert.Equal("CWE-348", finding.Cwe));
        Assert.All(enabled, finding => Assert.Equal("aspnet", finding.Properties["framework"]));

        var inline = Assert.Single(enabled, finding => finding.FileName == "Program.cs" && finding.LineNumber == 8);
        Assert.Equal("x-forwarded-for,x-forwarded-proto", inline.Properties.GetValueOrDefault("headers"));

        var allFlags = Assert.Single(enabled, finding => finding.LineNumber == 11);
        Assert.Equal("all", allFlags.Properties.GetValueOrDefault("headers"));

        Assert.Single(enabled, finding => finding.LineNumber == 9 && !finding.Properties.ContainsKey("headers"));

        var unbounded = Assert.Single(enabled, finding => finding.LineNumber == 13);
        Assert.Equal("x-forwarded-for", unbounded.Properties.GetValueOrDefault("headers"));
        Assert.Equal("unbounded", unbounded.Properties.GetValueOrDefault("forwardLimit"));

        // None is off (line 15), Configure<OtherOptions> is not this middleware (line 17), and an
        // application UseForwardedHeaders is not the framework's (line 18).
        Assert.DoesNotContain(enabled, finding => finding.LineNumber is 15 or 17 or 18);

        // The trust-all rule keeps firing beside the new informational finding.
        var trustAll = Assert.Single(Of("ForwardedHeadersTrustAll"));
        Assert.Equal(22, trustAll.LineNumber);
    }

    [Fact]
    public void ForwardedHeadersEnabled_FromConfiguration()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("App.cs", "public class App { }\n");
        directory.WriteSource("appsettings.json", """
{
  "ForwardedHeaders_Enabled": "true"
}
""");
        var findings = directory.Methods().SecurityFindings!;
        var enabled = Assert.Single(findings, finding => finding.Kind == "ForwardedHeadersEnabled");
        Assert.Equal("aspnet", enabled.Properties["framework"]);
        Assert.Equal("x-forwarded-for,x-forwarded-proto", enabled.Properties.GetValueOrDefault("headers"));
        Assert.Equal("appsettings.json", enabled.Properties.GetValueOrDefault("configFile"));
    }

    [Fact]
    public void ForwardedHeadersEnabled_ConfigurationTwins_StayQuiet()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("App.cs", "public class App { }\n");
        directory.WriteSource("appsettings.json", """
{
  "ForwardedHeaders_Enabled": "false"
}
""");
        directory.WriteSource("launchSettings.json", """
{
  "profiles": {
    "Dev": {
      "commandName": "Project",
      "environmentVariables": {
        "ASPNETCORE_FORWARDEDHEADERS_ENABLED": "false"
      }
    }
  }
}
""");
        var findings = directory.Methods().SecurityFindings!;
        Assert.DoesNotContain(findings, finding => finding.Kind == "ForwardedHeadersEnabled");
    }

    [Fact]
    public void ForwardedHeadersEnabled_FromLaunchSettingsEnvironment()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("App.cs", "public class App { }\n");
        directory.WriteSource("launchSettings.json", """
{
  "profiles": {
    "Dev": {
      "commandName": "Project",
      "environmentVariables": {
        "ASPNETCORE_FORWARDEDHEADERS_ENABLED": "true"
      }
    }
  }
}
""");
        var findings = directory.Methods().SecurityFindings!;
        var enabled = Assert.Single(findings, finding => finding.Kind == "ForwardedHeadersEnabled");
        Assert.Equal("aspnet", enabled.Properties["framework"]);
        Assert.Equal("x-forwarded-for,x-forwarded-proto", enabled.Properties.GetValueOrDefault("headers"));
        Assert.Equal("launchSettings.json", enabled.Properties.GetValueOrDefault("configFile"));
    }
}
