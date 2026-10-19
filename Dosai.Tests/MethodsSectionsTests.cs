using System.IO.Compression;
using System.Text.Json;
using Depscan;
using Xunit;

namespace Dosai.Tests;

// Issue #96: `methods --sections` names the top-level report sections to write, the way
// --crypto-dataflows bounds the crypto graph. The flag is write-only: every kept section must
// be byte-equal to the same section of an unfiltered run, Metadata is always written, the
// written sections keep contract order whatever order the list gives, and
// Metadata.OmittedSections names what was left out. These tests drive the CLI end to end
// through CommandLine.Main on the WebInventory fixture.
public partial class DosaiTests
{
    private const string WebInventorySource = "WebInventory.cs";

    /// <summary>
    ///     A private copy of the WebInventory fixture. One copy per test: every run inside a
    ///     test must scan the same absolute path, because Metadata.InputPath records it and is
    ///     compared across runs.
    /// </summary>
    private static TemporaryDirectory CopyWebInventoryFixture()
    {
        var fixture = new TemporaryDirectory();
        File.Copy(GetFilePath(WebInventorySource), Path.Combine(fixture.Path, WebInventorySource));
        return fixture;
    }

    private static string RunMethodsOnPath(string path, string outputDirectory, params string[] extraArgs)
    {
        var outputFile = Path.Combine(outputDirectory, $"methods-{Guid.NewGuid():N}.json");
        var args = new List<string> { "methods", "--path", path, "--o", outputFile };
        args.AddRange(extraArgs);
        lock (ConsoleOutputLock)
        {
            var originalOut = Console.Out;
            var originalError = Console.Error;
            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);
            try
            {
                Assert.Equal(0, CommandLine.Main([.. args]));
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalError);
                DebugLog.Configure(false);
            }
        }

        return File.ReadAllText(outputFile);
    }

    private static List<string> TopLevelKeys(JsonDocument document) => document.RootElement.EnumerateObject().Select(property => property.Name).ToList();

    /// <summary>
    ///     Every top-level MethodsSlice section name, in contract order, read from the
    ///     serializer's own contract with the methods options: the same source of truth the
    ///     filter uses, so a section added to MethodsSlice joins these tests unattended.
    /// </summary>
    private static List<string> ContractSectionNames()
    {
        var options = SliceJsonOptions();
        options.MakeReadOnly(populateMissingResolver: true);
        return options.GetTypeInfo(typeof(MethodsSlice)).Properties.Select(property => property.Name).ToList();
    }

    private static List<string> TopLevelKeys(string json)
    {
        using var document = JsonDocument.Parse(json);
        return TopLevelKeys(document);
    }

    /// <summary>The fixture's section key set with its non-null values, derived from an unfiltered run.</summary>
    private static HashSet<string> NonNullSections(string baselineJson)
    {
        using var document = JsonDocument.Parse(baselineJson);
        return document.RootElement.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
    }

    private static string MetadataWithoutRunIdentity(JsonElement metadata)
    {
        var rebuilt = new Dictionary<string, JsonElement>();
        foreach (var property in metadata.EnumerateObject())
        {
            if (property.Name is "GeneratedAt" or "OmittedSections")
            {
                continue;
            }

            rebuilt[property.Name] = property.Value.Clone();
        }

        return JsonSerializer.Serialize(rebuilt);
    }

    /// <summary>Asserts one filtered run against the baseline: keys, order, OmittedSections, and per-section byte equality.</summary>
    private static void AssertSelectionMatchesBaseline(string filteredJson, string baselineJson, IReadOnlyList<string> contractNames, string requestedList)
    {
        var requested = requestedList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(name => contractNames.First(contract => contract.Equals(name, StringComparison.OrdinalIgnoreCase)))
            .ToHashSet(StringComparer.Ordinal);
        requested.Add("Metadata");
        var expectedKeys = contractNames.Where(requested.Contains).ToList();
        var expectedOmitted = contractNames.Where(name => !requested.Contains(name)).ToList();
        var nonNull = NonNullSections(baselineJson);

        using var filtered = JsonDocument.Parse(filteredJson);
        var actualKeys = TopLevelKeys(filtered);
        Assert.Equal(expectedKeys.Where(nonNull.Contains).ToList(), actualKeys);

        var metadataProperty = filtered.RootElement.GetProperty("Metadata");
        if (expectedOmitted.Count == 0)
        {
            Assert.False(metadataProperty.TryGetProperty("OmittedSections", out _));
        }
        else
        {
            Assert.Equal(expectedOmitted, metadataProperty.GetProperty("OmittedSections").EnumerateArray().Select(value => value.GetString()!).ToList());
        }

        using var baseline = JsonDocument.Parse(baselineJson);
        foreach (var key in actualKeys)
        {
            var expected = key == "Metadata"
                ? MetadataWithoutRunIdentity(baseline.RootElement.GetProperty(key))
                : baseline.RootElement.GetProperty(key).GetRawText();
            var actual = key == "Metadata"
                ? MetadataWithoutRunIdentity(filtered.RootElement.GetProperty(key))
                : filtered.RootElement.GetProperty(key).GetRawText();
            Assert.True(actual == expected, $"section {key} differs from the unfiltered run");
        }
    }

    [Fact]
    public void MethodsSections_EachSectionAlone_WritesOnlyThatSectionPlusMetadata()
    {
        using var fixture = CopyWebInventoryFixture();
        using var output = new TemporaryDirectory();
        var baseline = RunMethodsOnPath(fixture.Path, output.Path);
        var contractNames = ContractSectionNames();

        foreach (var section in contractNames.Where(name => name != "Metadata"))
        {
            var filtered = RunMethodsOnPath(fixture.Path, output.Path, "--sections", section);
            AssertSelectionMatchesBaseline(filtered, baseline, contractNames, section);
        }
    }

    [Fact]
    public void MethodsSections_MixedCasePaddedListWithDuplicates_CollapsesAndKeepsContractOrder()
    {
        using var fixture = CopyWebInventoryFixture();
        using var output = new TemporaryDirectory();
        var baseline = RunMethodsOnPath(fixture.Path, output.Path);
        var contractNames = ContractSectionNames();
        var filtered = RunMethodsOnPath(fixture.Path, output.Path, "--sections", " callgraph , METADATA ,callgraph");
        AssertSelectionMatchesBaseline(filtered, baseline, contractNames, "callgraph , METADATA ,callgraph");
    }

    [Fact]
    public void MethodsSections_FullListExplicitly_WritesEverySectionAndNoOmittedSections()
    {
        using var fixture = CopyWebInventoryFixture();
        using var output = new TemporaryDirectory();
        var baseline = RunMethodsOnPath(fixture.Path, output.Path);
        var contractNames = ContractSectionNames();
        var requested = string.Join(",", contractNames);
        var filtered = RunMethodsOnPath(fixture.Path, output.Path, "--sections", requested);
        AssertSelectionMatchesBaseline(filtered, baseline, contractNames, requested);

        using var document = JsonDocument.Parse(filtered);
        Assert.False(document.RootElement.GetProperty("Metadata").TryGetProperty("OmittedSections", out _));
        // Naming every section is the unfiltered document: same bytes apart from the timestamp.
        Assert.Equal(MaskRunIdentity(baseline), MaskRunIdentity(filtered));
    }

    [Fact]
    public void MethodsSections_WithoutTheFlag_WritesTheContractUnchanged()
    {
        using var fixture = CopyWebInventoryFixture();
        using var output = new TemporaryDirectory();
        var first = RunMethodsOnPath(fixture.Path, output.Path);
        var second = RunMethodsOnPath(fixture.Path, output.Path);
        Assert.Equal(TopLevelKeys(first), TopLevelKeys(second));
        Assert.Equal(ContractSectionNames().Where(NonNullSections(first).Contains).ToList(), TopLevelKeys(first));
        using var document = JsonDocument.Parse(second);
        Assert.False(document.RootElement.GetProperty("Metadata").TryGetProperty("OmittedSections", out _));
    }

    [Fact]
    public void MethodsSections_InvalidSelections_FailBeforeAnalysisAndLeaveTheOutputFileUntouched()
    {
        using var fixture = CopyWebInventoryFixture();
        using var output = new TemporaryDirectory();
        var outputFile = Path.Combine(output.Path, "pre-existing.json");
        const string sentinel = "previous run output";
        File.WriteAllText(outputFile, sentinel);
        var untouchedTime = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(outputFile, untouchedTime);

        string errors;
        lock (ConsoleOutputLock)
        {
            var originalOut = Console.Out;
            var originalError = Console.Error;
            var recorder = new StringWriter();
            Console.SetOut(TextWriter.Null);
            Console.SetError(recorder);
            try
            {
                foreach (var badValue in new[] { "Methodz", ",", "" })
                {
                    Assert.Equal(1, CommandLine.Main(["methods", "--path", fixture.Path, "--o", outputFile, "--sections", badValue]));
                }
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalError);
                DebugLog.Configure(false);
            }

            errors = recorder.ToString();
        }

        Assert.Contains("Methodz", errors, StringComparison.Ordinal);
        Assert.Contains("Valid sections", errors, StringComparison.Ordinal);
        Assert.Equal(sentinel, File.ReadAllText(outputFile));
        Assert.Equal(untouchedTime, File.GetLastWriteTimeUtc(outputFile));
    }

    [Fact]
    public void MethodsSections_CallGraphExportIsByteIdenticalToAnUnfilteredRun()
    {
        using var fixture = CopyWebInventoryFixture();
        using var output = new TemporaryDirectory();
        var fullGraph = Path.Combine(output.Path, "full.graphml");
        var filteredGraph = Path.Combine(output.Path, "filtered.graphml");
        RunMethodsOnPath(fixture.Path, output.Path, "--callgraph-format", "graphml", "--callgraph-out", fullGraph);
        // Metadata alone omits both CallGraph and Reachability, the two sections the exporter reads.
        RunMethodsOnPath(fixture.Path, output.Path, "--sections", "Metadata", "--callgraph-format", "graphml", "--callgraph-out", filteredGraph);
        Assert.Equal(File.ReadAllBytes(fullGraph), File.ReadAllBytes(filteredGraph));
    }

    [Fact]
    public void MethodsSections_OtherCommandOutputsCarryNoOmittedSectionsKey()
    {
        using var fixture = CopyWebInventoryFixture();
        using var output = new TemporaryDirectory();
        lock (ConsoleOutputLock)
        {
            var originalOut = Console.Out;
            var originalError = Console.Error;
            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);
            try
            {
                Assert.Equal(0, CommandLine.Main(["agent-context", "--path", fixture.Path, "--o", Path.Combine(output.Path, "agent-context.json")]));
                Assert.Equal(0, CommandLine.Main(["dataflows", "--path", fixture.Path, "--o", Path.Combine(output.Path, "dataflows.json")]));
                Assert.Equal(0, CommandLine.Main(["crypto", "--path", fixture.Path, "--o", Path.Combine(output.Path, "crypto.json")]));
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalError);
                DebugLog.Configure(false);
            }
        }

        foreach (var file in new[] { "agent-context.json", "dataflows.json", "crypto.json" })
        {
            Assert.DoesNotContain("OmittedSections", File.ReadAllText(Path.Combine(output.Path, file)), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void McpMethodsTool_SectionsArgument_ShapesOutputAndRejectsUnknownValues()
    {
        using var tempDirectory = CopyWebInventoryFixture();

        var output = new StringWriter();
        McpServer.Run(tempDirectory.Path, null, null, null, new StringReader("""
        {"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"dosai.methods","arguments":{"sections":"metadata,diagnostics"}}}

        """), output);
        using var response = JsonDocument.Parse(output.ToString().TrimEnd());
        var payload = JsonDocument.Parse(response.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!);
        Assert.Equal(["Metadata", "Diagnostics"], TopLevelKeys(payload));
        Assert.NotEmpty(payload.RootElement.GetProperty("Metadata").GetProperty("OmittedSections").EnumerateArray());

        output = new StringWriter();
        McpServer.Run(tempDirectory.Path, null, null, null, new StringReader("""
        {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"dosai.methods","arguments":{"sections":"Methodz"}}}

        """), output);
        Assert.Contains("\"error\"", output.ToString(), StringComparison.Ordinal);

        output = new StringWriter();
        McpServer.Run(tempDirectory.Path, null, null, null, new StringReader("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/list\"}\n"), output);
        Assert.Contains("\"sections\"", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void MethodsSections_NupkgInput_WritesOnlyTheSelectedSections()
    {
        // A .nupkg goes through its own write path (extracted, then analyzed as assemblies).
        using var output = new TemporaryDirectory();
        var package = Path.Combine(output.Path, "Dosai.TestData.CSharp.1.0.0.nupkg");
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            archive.CreateEntryFromFile(GetFilePath("Dosai.TestData.CSharp.dll"), "lib/net8.0/Dosai.TestData.CSharp.dll");
        }

        var baseline = RunMethodsOnPath(package, output.Path);
        var filtered = RunMethodsOnPath(package, output.Path, "--sections", "methods,AssemblyInformation");
        Assert.Equal(["Metadata", "Methods", "AssemblyInformation"], TopLevelKeys(filtered));
        using var document = JsonDocument.Parse(filtered);
        Assert.NotEmpty(document.RootElement.GetProperty("Methods").EnumerateArray());
        Assert.Contains("CallGraph", document.RootElement.GetProperty("Metadata").GetProperty("OmittedSections").EnumerateArray().Select(value => value.GetString()));
        // Each extraction lands in a fresh temporary directory, so the paths differ per run;
        // the method inventory itself must not.
        using var full = JsonDocument.Parse(baseline);
        Assert.Equal(
            full.RootElement.GetProperty("Methods").EnumerateArray().Select(method => $"{method.GetProperty("ClassName")}.{method.GetProperty("Name")}").ToList(),
            document.RootElement.GetProperty("Methods").EnumerateArray().Select(method => $"{method.GetProperty("ClassName")}.{method.GetProperty("Name")}").ToList());
    }

    /// <summary>Fixture facts for the report: which sections the WebInventory tree fills at all.</summary>
    [Fact]
    public void MethodsSections_WebInventoryFixture_SectionFacts()
    {
        using var fixture = CopyWebInventoryFixture();
        using var output = new TemporaryDirectory();
        var baseline = RunMethodsOnPath(fixture.Path, output.Path);
        var nonNull = NonNullSections(baseline);
        // Facts the fixture promises, so the equality tests above are not vacuous: endpoints,
        // properties, fields, events, constructors, a call graph, reachability and dead code are
        // all present in an unfiltered run. Assembly-only sections (AssemblyInformation,
        // SourceAssemblyMapping) legitimately stay empty on a source-only tree.
        foreach (var required in new[] { "Metadata", "Dependencies", "Methods", "MethodCalls", "Properties", "Fields", "Events", "Constructors", "CallGraph", "ApiEndpoints", "EntryPoints", "Reachability", "DeadCode", "Diagnostics" })
        {
            Assert.True(nonNull.Contains(required), $"expected section {required} to be written, got only: {string.Join(", ", nonNull.Order())}");
        }
    }
}
