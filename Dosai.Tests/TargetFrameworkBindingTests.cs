using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Depscan;
using Xunit;

namespace Dosai.Tests;

/// <summary>
///     Pins the calls a minimal probe binds on every target framework the corpus covers. The cases
///     hold no package reference and no restore output, so they analyze the same way on every OS;
///     adding one would make these expectations depend on a restore the test never runs.
/// </summary>
public class TargetFrameworkBindingTests
{
    private static readonly JsonSerializerOptions SliceOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly JsonSerializerOptions CaseOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static TheoryData<string> TargetFrameworks => new()
    {
        "netstandard2.0",
        "net8.0",
        "net10.0",
        "net11.0",
        "net48"
    };

    private static string CaseDirectory(string targetFramework)
    {
        return Path.Combine(AppContext.BaseDirectory, "BindingCases", targetFramework);
    }

    private static ExpectedCase ReadExpected(string targetFramework)
    {
        var path = Path.Combine(CaseDirectory(targetFramework), "expected.json");
        return JsonSerializer.Deserialize<ExpectedCase>(File.ReadAllText(path), CaseOptions)!;
    }

    private static string Describe(
        int line
        , string? caller
        , string? containingNamespace
        , string? className
        , string? calledMethod
        , string evidenceKind
        , string? purl
    )
    {
        return string.Join(
            " | "
            , line.ToString(CultureInfo.InvariantCulture)
            , caller
            , containingNamespace
            , className
            , calledMethod
            , evidenceKind
            , purl ?? "no purl"
        );
    }

    private static string Describe(ExpectedCall call)
    {
        return Describe(
            call.Line
            , call.Caller
            , call.Namespace
            , call.ClassName
            , call.CalledMethod
            , call.EvidenceKind
            , call.Purl
        );
    }

    private static string Describe(MethodCalls call)
    {
        return Describe(
            call.LineNumber
            , call.CallerMethod
            , call.Namespace
            , call.ClassName
            , call.CalledMethod
            , call.EvidenceKind.ToString()
            , call.Purl
        );
    }

    [Theory]
    [MemberData(nameof(TargetFrameworks))]
    public void Methods_BindsTheSameCallsOnEveryTargetFramework(string targetFramework)
    {
        var expected = ReadExpected(targetFramework);

        var slice = JsonSerializer.Deserialize<MethodsSlice>(
            Depscan.Dosai.GetMethods(CaseDirectory(targetFramework))
            , SliceOptions
        )!;

        Assert.Equal(new[] { expected.TargetFramework }, slice.Metadata!.TargetFrameworks);
        Assert.Equal(
            expected.Calls.Select(Describe).Order().ToList()
            , slice.MethodCalls!.Select(Describe).Order().ToList()
        );
    }

    public sealed record ExpectedCase(string TargetFramework, IReadOnlyList<ExpectedCall> Calls);

    public sealed record ExpectedCall(
        int Line
        , string Caller
        , string Namespace
        , string ClassName
        , string CalledMethod
        , string EvidenceKind
        , string? Purl
    );
}
