using Depscan;
using Xunit;

namespace Dosai.Tests.Roadmap;

/// <summary>
///     Pipeline and cookie misconfiguration findings: the developer exception page outside a
///     development check, web applications with no HTTPS redirection or HSTS, insecure cookies,
///     forwarded headers trusted from any proxy, removed request size limits, and CORS findings
///     that name the policy that is actually open. ASP.NET Core types are stubbed in their real
///     namespaces.
/// </summary>
public class MiddlewareSecurityTests
{
    private const string Stubs = """
namespace Microsoft.AspNetCore.Builder
{
    public interface IApplicationBuilder { }
    public class WebApplicationBuilder { public WebApplication Build() => new(); public Microsoft.Extensions.DependencyInjection.IServiceCollection Services { get; } = null!; }
    public class WebApplication : IApplicationBuilder
    {
        public static WebApplicationBuilder CreateBuilder(string[] args) => new();
        public Microsoft.AspNetCore.Hosting.IWebHostEnvironment Environment { get; } = null!;
        public void Run() { }
    }
    public static class AppExtensions
    {
        public static IApplicationBuilder UseDeveloperExceptionPage(this IApplicationBuilder app) => app;
        public static IApplicationBuilder UseHttpsRedirection(this IApplicationBuilder app) => app;
        public static IApplicationBuilder UseHsts(this IApplicationBuilder app) => app;
        public static IApplicationBuilder UseExceptionHandler(this IApplicationBuilder app, string path) => app;
        public static IApplicationBuilder UseForwardedHeaders(this IApplicationBuilder app, ForwardedHeadersOptions options) => app;
    }
    public class ForwardedHeadersOptions
    {
        public System.Collections.Generic.IList<System.Net.IPAddress> KnownProxies { get; } = new System.Collections.Generic.List<System.Net.IPAddress>();
        public System.Collections.Generic.IList<object> KnownNetworks { get; } = new System.Collections.Generic.List<object>();
    }
    public class CookieBuilder { public Microsoft.AspNetCore.Http.CookieSecurePolicy SecurePolicy { get; set; } public bool HttpOnly { get; set; } public Microsoft.AspNetCore.Http.SameSiteMode SameSite { get; set; } }
}
namespace Microsoft.AspNetCore.Hosting
{
    public interface IWebHostEnvironment { }
    public static class HostingEnvironmentExtensions { public static bool IsDevelopment(this IWebHostEnvironment environment) => true; }
}
namespace Microsoft.AspNetCore.Http
{
    public enum CookieSecurePolicy { SameAsRequest, Always, None }
    public enum SameSiteMode { Unspecified = -1, None, Lax, Strict }
    public class CookieOptions { public bool Secure { get; set; } public bool HttpOnly { get; set; } public SameSiteMode SameSite { get; set; } }
}
namespace Microsoft.AspNetCore.Server.Kestrel.Core
{
    public class KestrelServerLimits { public long? MaxRequestBodySize { get; set; } }
}
namespace Microsoft.AspNetCore.Mvc
{
    public class DisableRequestSizeLimitAttribute : System.Attribute { }
    public class RequestSizeLimitAttribute : System.Attribute { public RequestSizeLimitAttribute(long bytes) { } }
    public class HttpPostAttribute : System.Attribute { public HttpPostAttribute(string template) { } }
    public class ControllerBase { }
}
namespace Microsoft.Extensions.DependencyInjection
{
    public interface IServiceCollection { }
    public class CorsPolicyBuilder
    {
        public CorsPolicyBuilder AllowAnyOrigin() => this;
        public CorsPolicyBuilder AllowCredentials() => this;
        public CorsPolicyBuilder WithOrigins(params string[] origins) => this;
    }
    public class CorsOptions
    {
        public void AddPolicy(string name, System.Action<CorsPolicyBuilder> configure) { }
        public void AddDefaultPolicy(System.Action<CorsPolicyBuilder> configure) { }
    }
    public static class CorsExtensions { public static IServiceCollection AddCors(this IServiceCollection services, System.Action<CorsOptions> setup) => services; }
}
""";

    [Fact]
    public void PipelineAndCookieMisconfigurations_AreFindings()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("Program.cs", """
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCors(options =>
{
    options.AddPolicy("Strict", policy => policy.WithOrigins("https://app.example").AllowCredentials());
    options.AddPolicy("Open", policy => policy.AllowAnyOrigin().AllowCredentials());
    options.AddDefaultPolicy(policy => policy.AllowAnyOrigin().AllowCredentials());
});
var app = builder.Build();
app.UseDeveloperExceptionPage();
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error");
}
else
{
    app.UseDeveloperExceptionPage();
}
var forwarded = new ForwardedHeadersOptions();
forwarded.KnownProxies.Clear();
forwarded.KnownNetworks.Clear();
app.UseForwardedHeaders(forwarded);
app.Run();

public static class Cookies
{
    public static CookieOptions Session() => new CookieOptions { Secure = false, HttpOnly = false, SameSite = SameSiteMode.None };
    public static CookieOptions Hardened() => new CookieOptions { Secure = true, HttpOnly = true, SameSite = SameSiteMode.Strict };
    public static void Auth(CookieBuilder cookie) { cookie.SecurePolicy = CookieSecurePolicy.None; cookie.HttpOnly = true; }
    public static void Limits(KestrelServerLimits limits) => limits.MaxRequestBodySize = null;
    public static void Keep(KestrelServerLimits limits) => limits.MaxRequestBodySize = 10_000_000;
}

public class UploadController : ControllerBase
{
    [HttpPost("/upload"), DisableRequestSizeLimit]
    public void Upload() { }

    [HttpPost("/bulk"), RequestSizeLimit(long.MaxValue)]
    public void Bulk() { }

    [HttpPost("/small"), RequestSizeLimit(1_000_000)]
    public void Small() { }
}
""" + Stubs);

        var findings = directory.Methods().SecurityFindings!;
        List<Depscan.Frameworks.SecurityFinding> Of(string kind) => findings.Where(finding => finding.Kind == kind).ToList();

        var developer = Assert.Single(Of("DeveloperExceptionPageUnguarded"));
        Assert.Equal(16, developer.LineNumber);
        Assert.Equal("CWE-209", developer.Cwe);
        Assert.Equal("CWE-319", Assert.Single(Of("HttpsRedirectionMissing")).Cwe);
        Assert.Single(Of("HstsMissing"));
        Assert.Equal(2, Of("ForwardedHeadersTrustAll").Count);
        var cookies = Of("InsecureCookie");
        Assert.Equal(["HttpOnly", "SameSite", "Secure", "SecurePolicy"], cookies.Select(finding => finding.Properties["setting"]).Order(StringComparer.Ordinal).ToArray());
        Assert.Contains(cookies, finding => finding.Cwe == "CWE-1004");
        var limits = Of("RequestSizeLimitDisabled");
        Assert.Equal(3, limits.Count);
        Assert.Contains(limits, finding => finding.Route == "/upload");
        Assert.Contains(limits, finding => finding.Route == "/bulk");
        Assert.DoesNotContain(limits, finding => finding.Route == "/small");

        // Each CORS policy is judged on its own builder: the open one is named, the strict one is not reported.
        var cors = Of("CorsWildcardWithCredentials");
        Assert.Equal(2, cors.Count);
        Assert.Contains(cors, finding => finding.Route == "Open" && finding.Title.Contains("'Open'", StringComparison.Ordinal));
        Assert.Contains(cors, finding => finding.Route is null && finding.Title.StartsWith("Default CORS policy", StringComparison.Ordinal));
        Assert.DoesNotContain(cors, finding => finding.Route == "Strict");
    }

    [Fact]
    public void HardenedPipeline_HasNoMiddlewareFindings()
    {
        using var directory = new RoadmapTemporaryDirectory();
        directory.WriteSource("Program.cs", """
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

var app = WebApplication.CreateBuilder(args).Build();
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseHsts();
}
app.UseHttpsRedirection();
app.Run();
""" + Stubs);

        var findings = directory.Methods().SecurityFindings!;

        Assert.DoesNotContain(findings, finding => finding.Kind is "DeveloperExceptionPageUnguarded" or "HttpsRedirectionMissing" or "HstsMissing" or "InsecureCookie" or "ForwardedHeadersTrustAll" or "RequestSizeLimitDisabled");
    }
}
