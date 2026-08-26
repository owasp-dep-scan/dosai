using Depscan.Frameworks;
using Xunit;

namespace Dosai.Tests.Frameworks;

public class RouteTemplateResolverTests
{
    private static RouteTokenValues Tokens(string? controller = null, string? action = null, string? area = null) => new() { Controller = controller, Action = action, Area = area };

    public static TheoryData<string?, string?, string> CombineCases => new()
    {
        // Class route + method route
        { "api/orders", "{id}", "api/orders/{id}" },
        // Method only
        { null, "health", "health" },
        { "", "health", "health" },
        // Class only
        { "api/orders", null, "api/orders" },
        { "api/orders", "", "api/orders" },
        // Nothing at all
        { null, null, "" },
        // Absolute method route overrides class route (ASP.NET override semantics)
        { "api/orders", "/admin/list", "/admin/list" },
        { "api/orders", "~/admin/list", "/admin/list" },
        { "api/orders", "~/", "/" },
        // A leading "/" on the method route is an absolute override, even when the class route ends with "/"
        { "api/", "/orders", "/orders" },
        { "api//orders", "{id}", "api//orders/{id}" }
    };

    [Theory]
    [MemberData(nameof(CombineCases))]
    public void Combine_ImplementsAspNetOverrideSemantics(string? classRoute, string? methodRoute, string expected)
    {
        Assert.Equal(expected, RouteTemplateResolver.Combine(classRoute, methodRoute));
    }

    [Fact]
    public void Combine_AbsoluteMethodRouteDiscardsClassRoute()
    {
        // ASP.NET: a method template starting with '/' or '~/' replaces, not appends. The old
        // CombineRoutes blindly concatenated, producing "api/orders//admin/list".
        Assert.Equal("/admin/list", RouteTemplateResolver.Combine("api/orders", "/admin/list"));
        Assert.Equal("/admin/list", RouteTemplateResolver.Combine("api/orders", "~/admin/list"));
    }

    [Fact]
    public void Resolve_ControllerToken_SubstitutesClassName()
    {
        var resolved = RouteTemplateResolver.Resolve("api/[controller]", Tokens(controller: "WeatherForecast"));
        Assert.Equal("/api/WeatherForecast", resolved.Path);
        Assert.Equal(["controller"], resolved.Tokens);
        Assert.False(resolved.HasUnresolvedTokens);
    }

    [Fact]
    public void Resolve_TokensAreCaseInsensitiveButValuesPreserveCasing()
    {
        var resolved = RouteTemplateResolver.Resolve("api/[Controller]/[Action]", Tokens(controller: "WeatherForecast", action: "Get"));
        Assert.Equal("/api/WeatherForecast/Get", resolved.Path);
        Assert.Contains("controller", resolved.Tokens);
        Assert.Contains("action", resolved.Tokens);
    }

    [Fact]
    public void Resolve_ActionToken_SubstitutesActionName()
    {
        var resolved = RouteTemplateResolver.Resolve("api/[controller]/[action]", Tokens(controller: "Orders", action: "List"));
        Assert.Equal("/api/Orders/List", resolved.Path);
    }

    [Fact]
    public void Resolve_AreaToken_SubstitutesArea()
    {
        var resolved = RouteTemplateResolver.Resolve("[area]/[controller]", Tokens(area: "Admin", controller: "Users"));
        Assert.Equal("/Admin/Users", resolved.Path);
    }

    [Fact]
    public void Resolve_UnresolvedToken_LeavesPathNullAndFlagsLowConfidence()
    {
        var resolved = RouteTemplateResolver.Resolve("api/[controller]/list", Tokens(action: "list"));
        Assert.Null(resolved.Path);
        Assert.True(resolved.HasUnresolvedTokens);
        Assert.Equal("low", resolved.Confidence);
        // Template text stays verbatim for humans and diffing.
        Assert.Equal(["controller"], resolved.Tokens);
    }

    [Fact]
    public void Resolve_NullTemplate_ProducesNullPath()
    {
        var resolved = RouteTemplateResolver.Resolve(null);
        Assert.Null(resolved.Path);
        Assert.Empty(resolved.Parameters);
    }

    [Fact]
    public void Resolve_EmptyTemplate_ProducesRootPath()
    {
        // [Route("")] means the controller root.
        var resolved = RouteTemplateResolver.Resolve("");
        Assert.Equal("/", resolved.Path);
    }

    [Fact]
    public void Resolve_ConstraintIsStrippedFromPathButRecorded()
    {
        var resolved = RouteTemplateResolver.Resolve("api/orders/{id:int:min(1)}");
        Assert.Equal("/api/orders/{id}", resolved.Path);
        var parameter = Assert.Single(resolved.Parameters);
        Assert.Equal("id", parameter.Name);
        Assert.Equal(["int", "min(1)"], parameter.Constraints);
        Assert.False(parameter.Optional);
    }

    [Fact]
    public void Resolve_OptionalMarkerIsStrippedFromPathButRecorded()
    {
        var resolved = RouteTemplateResolver.Resolve("api/orders/{id?}");
        Assert.Equal("/api/orders/{id}", resolved.Path);
        var parameter = Assert.Single(resolved.Parameters);
        Assert.True(parameter.Optional);
    }

    [Fact]
    public void Resolve_OptionalMarkerAfterConstraintsIsRecorded()
    {
        var resolved = RouteTemplateResolver.Resolve("api/orders/{id:int?}");
        Assert.Equal("/api/orders/{id}", resolved.Path);
        var parameter = Assert.Single(resolved.Parameters);
        Assert.Equal(["int"], parameter.Constraints);
        Assert.True(parameter.Optional);
    }

    [Fact]
    public void Resolve_DefaultValueIsStrippedFromPathButRecorded()
    {
        var resolved = RouteTemplateResolver.Resolve("pages/{page=1}");
        Assert.Equal("/pages/{page}", resolved.Path);
        var parameter = Assert.Single(resolved.Parameters);
        Assert.Equal("1", parameter.DefaultValue);
        Assert.Equal([], parameter.Constraints);
    }

    [Fact]
    public void Resolve_ConstraintsAndDefaultTogether()
    {
        var resolved = RouteTemplateResolver.Resolve("pages/{page:int=1}");
        Assert.Equal("/pages/{page}", resolved.Path);
        var parameter = Assert.Single(resolved.Parameters);
        Assert.Equal(["int"], parameter.Constraints);
        Assert.Equal("1", parameter.DefaultValue);
    }

    [Fact]
    public void Resolve_SingleStarCatchAllIsRecorded()
    {
        var resolved = RouteTemplateResolver.Resolve("files/{*path}");
        Assert.Equal("/files/{path}", resolved.Path);
        var parameter = Assert.Single(resolved.Parameters);
        Assert.True(parameter.CatchAll);
    }

    [Fact]
    public void Resolve_DoubleStarCatchAllIsRecorded()
    {
        var resolved = RouteTemplateResolver.Resolve("files/{**path}");
        Assert.Equal("/files/{path}", resolved.Path);
        Assert.True(Assert.Single(resolved.Parameters).CatchAll);
    }

    [Fact]
    public void Resolve_RegexConstraintWithoutBraces()
    {
        var resolved = RouteTemplateResolver.Resolve(@"api/{id:regex(^\d+$)}");
        Assert.Equal("/api/{id}", resolved.Path);
        Assert.Equal([@"regex(^\d+$)"], Assert.Single(resolved.Parameters).Constraints);
    }

    [Fact]
    public void Resolve_RegexConstraintWithNestedBracesAndBraceQuantifier()
    {
        // The brace inside parentheses must not terminate the parameter; this needs a real
        // tokenizer, not a naive regex.
        var resolved = RouteTemplateResolver.Resolve(@"api/{id:regex(^\d{2,3}$)}");
        Assert.Equal("/api/{id}", resolved.Path);
        Assert.Equal([@"regex(^\d{2,3}$)"], Assert.Single(resolved.Parameters).Constraints);
    }

    [Fact]
    public void Resolve_RegexConstraintWithBraceInsideCharacterClass()
    {
        var resolved = RouteTemplateResolver.Resolve(@"api/{code:regex(a{2})}");
        Assert.Equal("/api/{code}", resolved.Path);
        Assert.Equal(["regex(a{2})"], Assert.Single(resolved.Parameters).Constraints);
    }

    [Fact]
    public void Resolve_RegexConstraintWithColonInsideParensDoesNotSplit()
    {
        var resolved = RouteTemplateResolver.Resolve(@"api/{time:regex((\d+):(\d+))}");
        Assert.Equal("/api/{time}", resolved.Path);
        Assert.Equal([@"regex((\d+):(\d+))"], Assert.Single(resolved.Parameters).Constraints);
    }

    [Fact]
    public void Resolve_CatchAllMidTemplateDoesNotCrash()
    {
        // Invalid in ASP.NET (catch-all must be last) but analysis must never throw.
        var resolved = RouteTemplateResolver.Resolve("files/{*path}/detail");
        Assert.Equal("/files/{path}/detail", resolved.Path);
        Assert.True(Assert.Single(resolved.Parameters).CatchAll);
    }

    [Fact]
    public void Resolve_UnbalancedOpeningBraceDoesNotCrash()
    {
        var resolved = RouteTemplateResolver.Resolve("api/{id");
        Assert.NotNull(resolved.Path);
        Assert.True(resolved.HasMalformedSegment);
        Assert.Equal("medium", resolved.Confidence);
    }

    [Fact]
    public void Resolve_UnbalancedClosingBraceDoesNotCrash()
    {
        var resolved = RouteTemplateResolver.Resolve("api/id}");
        Assert.True(resolved.HasMalformedSegment);
    }

    [Fact]
    public void Resolve_EmptyParameterSegmentIsTreatedAsLiteral()
    {
        var resolved = RouteTemplateResolver.Resolve("api/{}");
        Assert.True(resolved.HasMalformedSegment);
    }

    [Fact]
    public void Resolve_LiteralTemplatePassesThroughWithLeadingSlash()
    {
        var resolved = RouteTemplateResolver.Resolve("health/status");
        Assert.Equal("/health/status", resolved.Path);
        Assert.Empty(resolved.Parameters);
        Assert.Equal("high", resolved.Confidence);
    }

    [Fact]
    public void Resolve_DuplicateAndTrailingSlashesCollapse()
    {
        var resolved = RouteTemplateResolver.Resolve("api//orders/");
        Assert.Equal("/api/orders", resolved.Path);
    }

    [Fact]
    public void Resolve_UnicodeSegmentNamesSurvive()
    {
        var resolved = RouteTemplateResolver.Resolve("kunden/überblick/{id}");
        Assert.Equal("/kunden/überblick/{id}", resolved.Path);
    }

    [Fact]
    public void Resolve_MultipleParametersAllRecordedInOrder()
    {
        var resolved = RouteTemplateResolver.Resolve("shop/{controller}/{action}/{id:int?}");
        Assert.Equal("/shop/{controller}/{action}/{id}", resolved.Path);
        Assert.Equal(3, resolved.Parameters.Count);
        Assert.Equal("controller", resolved.Parameters[0].Name);
        Assert.Equal("action", resolved.Parameters[1].Name);
        Assert.Equal("id", resolved.Parameters[2].Name);
        Assert.True(resolved.Parameters[2].Optional);
    }

    [Fact]
    public void Resolve_ControllerAndActionTokensInsideLargerSegments()
    {
        var resolved = RouteTemplateResolver.Resolve("v1/[controller]-items/[action]-all", Tokens(controller: "Orders", action: "List"));
        Assert.Equal("/v1/Orders-items/List-all", resolved.Path);
    }

    [Fact]
    public void ActionName_StripsAsyncSuffix()
    {
        // ASP.NET Core removes the Async suffix from action names by default
        // (SuppressAsyncSuffixInActionNames defaults to false).
        Assert.Equal("List", RouteTemplateResolver.ActionName("ListAsync"));
        Assert.Equal("Get", RouteTemplateResolver.ActionName("Get"));
        Assert.Equal("Async", RouteTemplateResolver.ActionName("Async"));
    }

    [Fact]
    public void ControllerName_StripsControllerSuffix()
    {
        Assert.Equal("WeatherForecast", RouteTemplateResolver.ControllerName("WeatherForecastController"));
        Assert.Equal("HomeController2", RouteTemplateResolver.ControllerName("HomeController2"));
        Assert.Equal("Plain", RouteTemplateResolver.ControllerName("Plain"));
    }

    [Fact]
    public void ExpandConventional_ReplacesControllerAndAction()
    {
        var resolved = RouteTemplateResolver.ExpandConventional("{controller=Home}/{action=Index}/{id?}", "Orders", "List");
        Assert.Equal("/Orders/List", resolved.Path);
        var parameter = Assert.Single(resolved.Parameters);
        Assert.Equal("id", parameter.Name);
        Assert.True(parameter.Optional);
    }

    [Fact]
    public void ExpandConventional_FallsBackToDefaultsWhenActionMissing()
    {
        var resolved = RouteTemplateResolver.ExpandConventional("{controller=Home}/{action=Index}", "Orders", null);
        Assert.Equal("/Orders/Index", resolved.Path);
    }

    [Fact]
    public void ExpandConventional_ControllerDefaultUsedWhenValueMissing()
    {
        var resolved = RouteTemplateResolver.ExpandConventional("{controller=Home}/{action=Index}", null, "About");
        Assert.Equal("/Home/About", resolved.Path);
    }

    [Fact]
    public void ExpandConventional_NoValues_ProducesNothing()
    {
        var resolved = RouteTemplateResolver.ExpandConventional("{controller=Home}/{action=Index}", null, null);
        Assert.Null(resolved.Path);
    }

    [Fact]
    public void ExpandConventional_ConstraintsOnControllerSegmentAreIgnored()
    {
        var resolved = RouteTemplateResolver.ExpandConventional("{controller:alpha}/{id:int}", "Orders", "List");
        Assert.Equal("/Orders/{id}", resolved.Path);
    }

    [Fact]
    public void NormalizePrefix_RootsAndTrims()
    {
        Assert.Equal("/api", RouteTemplateResolver.NormalizePrefix("api"));
        Assert.Equal("/api", RouteTemplateResolver.NormalizePrefix("/api/"));
        Assert.Equal("/api", RouteTemplateResolver.NormalizePrefix("~/api"));
        Assert.Equal("", RouteTemplateResolver.NormalizePrefix(null));
    }

    [Fact]
    public void Resolve_IsReproducibleAcrossCalls()
    {
        var first = RouteTemplateResolver.Resolve("api/[controller]/{id:int}", Tokens(controller: "Orders"));
        var second = RouteTemplateResolver.Resolve("api/[controller]/{id:int}", Tokens(controller: "Orders"));
        Assert.Equal(first.Path, second.Path);
        Assert.Equal(first.Parameters.Count, second.Parameters.Count);
    }
}
