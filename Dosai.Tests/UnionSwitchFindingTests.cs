using System.Text.Json;
using System.Text.RegularExpressions;
using Depscan;
using Xunit;

namespace Dosai.Tests;

/// <summary>
///     The union switch review findings (<c>UnionSwitchWithoutNullArm</c>,
///     <c>UnionSwitchMissingCase</c>): which switch shapes are reported, the evidence they carry,
///     their content-derived ids and paths, and how suppressions, diff and the text report treat
///     them. <c>UnionSwitchShapes.cs</c> names every method after the expected outcome.
/// </summary>
public class UnionSwitchFindingTests
{
    private const string Fixture = "UnionSwitchShapes.cs";

    private static readonly string[] ExpectedFlags =
    [
        "FlagGenericUnion", "FlagNegatedOrNull", "FlagNestedProperty", "FlagNullableWithoutNullArm", "FlagStatementGenericUnion",
        "FlagStatementMemberProviderUnion", "FlagStatementRelationalGap", "FlagTupleElement"
    ];

    [Fact]
    public void Analyze_UnionSwitchShapes_FlagsExactlyTheThrowingAndCaseSkippingSwitches()
    {
        var findings = UnionFindings(DataFlowAnalyzer.Analyze(FixturePath()));

        var flagged = findings.Select(weakness => EnclosingMethod(weakness)).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(ExpectedFlags, flagged);
    }

    [Theory]
    [InlineData("FlagNegatedOrNull", "UnionSwitchWithoutNullArm", "Empty union unmatched at: value")]
    [InlineData("FlagNullableWithoutNullArm", "UnionSwitchWithoutNullArm", "Empty union unmatched at: value")]
    [InlineData("FlagGenericUnion", "UnionSwitchWithoutNullArm", "Empty union unmatched at: value")]
    [InlineData("FlagTupleElement", "UnionSwitchWithoutNullArm", "Empty union unmatched at: #0")]
    [InlineData("FlagNestedProperty", "UnionSwitchWithoutNullArm", "Empty union unmatched at: Owner.Pet")]
    [InlineData("FlagStatementRelationalGap", "UnionSwitchMissingCase", "Unhandled case types: int")]
    [InlineData("FlagStatementMemberProviderUnion", "UnionSwitchMissingCase", "Unhandled case types: Dog")]
    [InlineData("FlagStatementGenericUnion", "UnionSwitchMissingCase", "Unhandled case types: Failed")]
    public void Analyze_UnionSwitchShapes_NamesTheEmptyPositionOrMissingCase(string method, string kind, string evidence)
    {
        var finding = Assert.Single(UnionFindings(DataFlowAnalyzer.Analyze(FixturePath())), weakness => EnclosingMethod(weakness) == method);

        Assert.Equal(kind, finding.Kind);
        Assert.Contains(evidence, finding.Evidence);
        Assert.Equal("low", finding.Severity);
    }

    [Fact]
    public void Analyze_UnionSwitchIds_AreContentDerivedAndUnique()
    {
        var findings = UnionFindings(DataFlowAnalyzer.Analyze(FixturePath()));

        Assert.All(findings, weakness =>
        {
            var line = LineOf(weakness);
            Assert.Equal($"union-switch-{weakness.Kind}-{Fixture}-{line}", weakness.Id);
        });
        Assert.Equal(findings.Count, findings.Select(weakness => weakness.Id).Distinct(StringComparer.Ordinal).Count());
    }

    // An id names its kind, file and line, so a switch added above an existing one leaves the
    // existing id (and any suppression naming it) alone except for the line it moved to.
    [Fact]
    public void Analyze_UnionSwitchIds_DoNotDependOnOtherFindings()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "Later.cs"), UnionSource("Later"));
        var before = UnionFindings(DataFlowAnalyzer.Analyze(tempDirectory.Path)).Select(weakness => weakness.Id).ToList();

        File.WriteAllText(Path.Combine(tempDirectory.Path, "Earlier.cs"), UnionSource("Earlier").Replace("public union Pet(Cat, Dog);", string.Empty, StringComparison.Ordinal)
            .Replace("public sealed record Cat(string Name);", string.Empty, StringComparison.Ordinal)
            .Replace("public sealed record Dog(string Name);", string.Empty, StringComparison.Ordinal));
        var after = UnionFindings(DataFlowAnalyzer.Analyze(tempDirectory.Path)).Select(weakness => weakness.Id).ToList();

        Assert.Single(before);
        Assert.Equal(2, after.Count);
        Assert.Contains(before[0], after);
    }

    // Two files of one name in different directories keep apart: locations and ids carry the
    // scan-relative path, so diff sees two findings and a file-name suppression still matches.
    [Fact]
    public void Analyze_SameFileNameInTwoDirectories_KeepsBothFindingsApart()
    {
        using var tempDirectory = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(tempDirectory.Path, "a"));
        File.WriteAllText(Path.Combine(tempDirectory.Path, "a", "Program.cs"), UnionSource("A"));
        var before = DataFlowAnalyzer.Analyze(tempDirectory.Path);
        Directory.CreateDirectory(Path.Combine(tempDirectory.Path, "b"));
        File.WriteAllText(Path.Combine(tempDirectory.Path, "b", "Program.cs"), UnionSource("B").Replace("public union Pet(Cat, Dog);", "public union PetB(CatB, DogB);", StringComparison.Ordinal)
            .Replace("record Cat(", "record CatB(", StringComparison.Ordinal).Replace("record Dog(", "record DogB(", StringComparison.Ordinal)
            .Replace("(Pet pet)", "(PetB pet)", StringComparison.Ordinal).Replace("Cat cat", "CatB cat", StringComparison.Ordinal).Replace("Dog dog", "DogB dog", StringComparison.Ordinal));
        var after = DataFlowAnalyzer.Analyze(tempDirectory.Path);

        var findings = UnionFindings(after);
        Assert.Equal(["a/Program.cs", "b/Program.cs"], findings.Select(weakness => weakness.SourceLocation![..weakness.SourceLocation!.IndexOf(':', StringComparison.Ordinal)]));
        Assert.Equal(["union-switch-UnionSwitchWithoutNullArm-a-Program.cs-7", "union-switch-UnionSwitchWithoutNullArm-b-Program.cs-7"], findings.Select(weakness => weakness.Id));

        using var diff = JsonDocument.Parse(TransparencyBuilder.DiffJson(before, after));
        var added = diff.RootElement.GetProperty("AddedWeaknesses").EnumerateArray().Select(element => element.GetString()).ToList();
        Assert.Single(added);
        Assert.Contains("b/Program.cs:7:", added[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ApplySuppressions_MatchUnionSwitchFindingsByCategoryFileNameAndId()
    {
        using var tempDirectory = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(tempDirectory.Path, "src"));
        File.WriteAllText(Path.Combine(tempDirectory.Path, "src", "Program.cs"), UnionSource("Src"));
        var suppressions = Path.Combine(tempDirectory.Path, "suppressions.json");

        File.WriteAllText(suppressions, """[ { "category": "union-switch", "reason": "reviewed" } ]""");
        Assert.Empty(UnionFindings(DataFlowAnalyzer.Analyze(Path.Combine(tempDirectory.Path, "src"), suppressionsPath: suppressions)));

        // A bare file name still matches a location that carries the file's directory.
        var unsuppressed = Assert.Single(UnionFindings(DataFlowAnalyzer.Analyze(tempDirectory.Path)));
        Assert.StartsWith("src/Program.cs:7:", unsuppressed.SourceLocation, StringComparison.Ordinal);
        File.WriteAllText(suppressions, """[ { "file": "Program.cs", "line": 7, "reason": "reviewed" } ]""");
        Assert.Empty(UnionFindings(DataFlowAnalyzer.Analyze(tempDirectory.Path, suppressionsPath: suppressions)));

        File.WriteAllText(suppressions, $$"""[ { "weaknessId": "{{unsuppressed.Id}}", "reason": "reviewed" } ]""");
        Assert.Empty(UnionFindings(DataFlowAnalyzer.Analyze(tempDirectory.Path, suppressionsPath: suppressions)));
    }

    // The text report counts every weakness candidate in its summary line, so the ones that
    // belong to no flow are listed as well.
    [Fact]
    public void DataFlowTreeReport_ListsWeaknessesThatBelongToNoFlow()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "Program.cs"), UnionSource("Report"));
        var result = DataFlowAnalyzer.Analyze(tempDirectory.Path);

        var report = CommandLine.BuildDataFlowTreeReport(result, Path.Combine(tempDirectory.Path, "dataflows.json"));

        Assert.Contains("No data-flow slices found.", report);
        Assert.Contains("Other weakness candidates:", report);
        Assert.Contains("UnionSwitchWithoutNullArm union-switch-UnionSwitchWithoutNullArm-Program.cs-7: low, CWE-248 at Program.cs:7:", report);
        Assert.Contains("SwitchExpressionException", report);
    }

    private static string UnionSource(string className) => $$"""
public sealed record Cat(string Name);
public sealed record Dog(string Name);
public union Pet(Cat, Dog);
public static class {{className}}
{
    public static string Describe(Pet pet) =>
        pet switch
        {
            Cat cat => cat.Name,
            Dog dog => dog.Name,
        };
}
""";

    private static List<WeaknessCandidate> UnionFindings(DataFlowResult result) =>
        result.WeaknessCandidates.Where(weakness => weakness.SinkCategory == "union-switch").ToList();

    private static string FixturePath() => Path.Join(Directory.GetCurrentDirectory(), Fixture);

    private static int LineOf(WeaknessCandidate weakness)
    {
        var match = Regex.Match(weakness.SourceLocation!, @":(\d+):\d+$");
        return int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The fixture method whose declaration is the nearest one above the finding.</summary>
    private static string EnclosingMethod(WeaknessCandidate weakness)
    {
        var lines = File.ReadAllLines(FixturePath());
        for (var index = LineOf(weakness) - 1; index >= 0; index--)
        {
            var match = Regex.Match(lines[index], @"public static \S+ (\w+)\(");
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }

        return string.Empty;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dosai-union-switch-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
