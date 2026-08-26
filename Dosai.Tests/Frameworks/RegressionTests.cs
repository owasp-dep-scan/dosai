using System.Text.Json;
using System.Text.Json.Serialization;
using Depscan;
using Depscan.Frameworks;
using Xunit;

namespace Dosai.Tests.Frameworks;

/// <summary>
///     Regressions found while reviewing the 4.0.0 framework-semantics work. Each test pins a
///     behaviour that was wrong in a way that would have shown up on ordinary .NET projects.
/// </summary>
public class FrameworkRegressionTests : IDisposable
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
        return slice!;
    }

    private const string MvcStubs = """
        using System;
        public class RouteAttribute : Attribute { public RouteAttribute(string template) { } public string Name { get; set; } }
        public class HttpGetAttribute : Attribute { public HttpGetAttribute() { } public HttpGetAttribute(string template) { } public string Name { get; set; } }
        public class ApiControllerAttribute : Attribute { }
        public class ControllerBase { }
        """;

    /// <summary>
    ///     `[HttpGet(Name = "...")]` is what `dotnet new webapi` scaffolds. Reading argument 0 without
    ///     skipping named arguments turned the route name into a path segment.
    /// </summary>
    [Fact]
    public void NamedAttributeArgument_IsNotTreatedAsRouteTemplate()
    {
        var slice = Run(("W.cs", MvcStubs + """

            [Route("api/weather")]
            public class WeatherForecastController : ControllerBase
            {
                [HttpGet(Name = "GetWeatherForecast")] public object Get() => null;
            }
            """));

        var endpoint = Assert.Single(slice.ApiEndpoints ?? []);
        Assert.Equal("/api/weather", endpoint.Path);
    }

    /// <summary>
    ///     Controller discovery must follow DefaultControllerTypeProvider: a public non-abstract,
    ///     non-generic class that either is named *Controller or derives from ControllerBase.
    /// </summary>
    [Fact]
    public void ControllerDiscovery_SkipsAbstractAndFindsUnsuffixedDerivedTypes()
    {
        var slice = Run(("C.cs", MvcStubs + """

            [Route("api/base")]
            public abstract class BaseApiController : ControllerBase
            {
                [HttpGet("shared")] public object Shared() => null;
            }

            [Route("api/things")]
            public class Things : ControllerBase
            {
                [HttpGet("x")] public object X() => null;
                public static string Helper(string s) => s;
            }
            """));

        var paths = (slice.ApiEndpoints ?? []).Select(endpoint => endpoint.Path).ToList();
        Assert.Contains("/api/things/x", paths);
        // Abstract shared bases are never routed by MVC.
        Assert.DoesNotContain("/api/base/shared", paths);
        // Static helpers are not actions.
        Assert.DoesNotContain(slice.ApiEndpoints ?? [], endpoint => endpoint.MethodName == "Helper");
    }

    /// <summary>
    ///     PackageUrlResolver's catch-all ("System" -> System.Runtime) fallback made every System.*
    ///     probe resolve, so WCF and Web API 2 were reported as present, at high confidence, in every
    ///     project analyzed.
    /// </summary>
    [Fact]
    public void FrameworkDetection_DoesNotReportUnreferencedSystemFrameworks()
    {
        var slice = Run(("D.cs", MvcStubs + """

            [Route("api/d")]
            public class DController : ControllerBase
            {
                [HttpGet("x")] public object X() => null;
            }
            """));

        Assert.DoesNotContain(slice.Frameworks ?? [], framework => framework.Id is "legacy-wcf" or "legacy-web");
    }

    /// <summary>
    ///     Entry points were built from the framework providers AND from the analyzer's view of the
    ///     same endpoints, producing a duplicate for each with a null MethodId.
    /// </summary>
    [Fact]
    public void EntryPoints_AreNotDuplicatedPerEndpoint()
    {
        var slice = Run(("E.cs", MvcStubs + """

            [Route("api/e")]
            public class EController : ControllerBase
            {
                [HttpGet("one")] public object One() => null;
                [HttpGet("two")] public object Two() => null;
            }
            """));

        var httpEntryPoints = (slice.EntryPoints ?? []).Where(entryPoint => entryPoint.Kind == "HttpController").ToList();
        Assert.Equal(2, httpEntryPoints.Count);
        Assert.All(httpEntryPoints, entryPoint => Assert.False(string.IsNullOrEmpty(entryPoint.MethodId)));
        Assert.Equal(httpEntryPoints.Count, httpEntryPoints.Select(entryPoint => entryPoint.Id).Distinct().Count());
    }

    /// <summary>
    ///     Services must reference the entry points they own, and every referenced id must resolve.
    /// </summary>
    [Fact]
    public void Services_LinkToEmittedEntryPoints()
    {
        var slice = Run(("L.cs", MvcStubs + """

            [Route("api/l")]
            public class LController : ControllerBase
            {
                [HttpGet("one")] public object One() => null;
            }
            """));

        var service = Assert.Single(slice.Services ?? [], candidate => candidate.Framework == "aspnetcore-mvc");
        var entryPointIds = (slice.EntryPoints ?? []).Select(entryPoint => entryPoint.Id).ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(service.EntryPointIds);
        Assert.All(service.EntryPointIds, id => Assert.Contains(id, entryPointIds));
    }

    /// <summary>
    ///     An inbound controller with no authorization metadata is anonymously reachable; leaving it
    ///     Unknown kept it out of the trust-boundary sweep entirely.
    /// </summary>
    [Fact]
    public void InboundServiceWithoutAuthorization_IsPublic()
    {
        var slice = Run(("T.cs", MvcStubs + """

            [Route("api/t")]
            public class TController : ControllerBase
            {
                [HttpGet("x")] public object X() => null;
            }
            """));

        var service = Assert.Single(slice.Services ?? [], candidate => candidate.Framework == "aspnetcore-mvc");
        Assert.Equal(TrustZones.Public, service.TrustZone);
        // "Unknown" is the honest answer for authentication, not "false".
        Assert.Null(service.Authenticated);
    }

    /// <summary>
    ///     Controller-level authorization was assigned from whichever action was visited last, so a
    ///     single [AllowAnonymous] action could report an [Authorize]d controller as public, or vice
    ///     versa depending on declaration order.
    /// </summary>
    [Fact]
    public void MixedAuthorizationActions_AggregateRatherThanOverwrite()
    {
        const string source = MvcStubs + """

            public class AuthorizeAttribute : Attribute { public AuthorizeAttribute() { } public string Policy { get; set; } }
            public class AllowAnonymousAttribute : Attribute { }

            [Authorize]
            [Route("api/h")]
            public class HController : ControllerBase
            {
                [HttpGet("secure")] public object Secure() => null;
                [AllowAnonymous][HttpGet("open")] public object Open() => null;
            }
            """;

        var service = Assert.Single(Run(("H.cs", source)).Services ?? [], candidate => candidate.Framework == "aspnetcore-mvc");
        // One anonymous action makes the controller anonymously reachable regardless of ordering.
        Assert.Equal(TrustZones.Public, service.TrustZone);
        Assert.True(service.AllowAnonymous);
        Assert.False(service.Authenticated);
    }

    /// <summary>
    ///     Classification keywords were raw substrings, so CompanyName matched "pan" (financial) and
    ///     TermsAndConditions matched "condition" (health).
    /// </summary>
    [Fact]
    public void DataClassification_DoesNotMatchKeywordsMidWord()
    {
        var slice = Run(("P.cs", MvcStubs + """

            public class HttpPostAttribute : Attribute { }

            public class PageRequest
            {
                public string ContinuationToken { get; set; }
                public string CompanyName { get; set; }
                public string TermsAndConditions { get; set; }
                public string ServerAddress { get; set; }
                public string EmailAddress { get; set; }
            }

            [Route("api/p")]
            public class PController : ControllerBase
            {
                [HttpPost] public object Post(PageRequest request) => null;
            }
            """));

        var classifications = (slice.Services ?? [])
            .SelectMany(service => service.Data)
            .Select(data => data.Classification)
            .ToList();
        Assert.Contains("pii", classifications);        // EmailAddress is genuine.
        Assert.DoesNotContain("financial", classifications);  // CompanyName is not a PAN.
        Assert.DoesNotContain("health", classifications);     // TermsAndConditions is not a diagnosis.
        Assert.DoesNotContain("credential", classifications); // ContinuationToken is not a secret.
    }

    public void Dispose() => _tempDirectory.Dispose();
}
