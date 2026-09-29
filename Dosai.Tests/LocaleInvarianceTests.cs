using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Depscan;
using Xunit;

namespace Dosai.Tests;

// Issue #63: output must not depend on the locale of the machine running Dosai. Each test runs
// the real pipeline under a culture with distinct formatting, parsing, casing or collation rules.
// Partial class, same xunit collection as the rest of DosaiTests: these tests flip the thread
// culture and redirect the console, which is only safe serialized against the other
// console-redirecting tests.
public partial class DosaiTests
{
    /// <summary>Runs the enclosed test code under a named culture, restoring both cultures after.</summary>
    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _culture;
        private readonly CultureInfo _uiCulture;

        public CultureScope(string name)
        {
            _culture = CultureInfo.CurrentCulture;
            _uiCulture = CultureInfo.CurrentUICulture;
            var replacement = name.Length == 0 ? CultureInfo.InvariantCulture : new CultureInfo(name);
            CultureInfo.CurrentCulture = replacement;
            CultureInfo.CurrentUICulture = replacement;
        }

        public void Dispose()
        {
            CultureInfo.CurrentCulture = _culture;
            CultureInfo.CurrentUICulture = _uiCulture;
        }
    }

    #region Issue #63 - locale-independent output

    [Fact]
    public void GetMethods_TurkishLocale_KeepsModifierAndParameterTypeCasingInvariant()
    {
        // The repro from the issue: under tr-TR, title-casing "internal" produced "İnternal"
        // (U+0130) and "int" produced "İnt" in the JSON.
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "Sample.cs"), """
internal class Sample
{
    internal int Count(int items, string name) => items;
}
""");

        string json;
        using (new CultureScope("tr-TR"))
        {
            json = Depscan.Dosai.GetMethods(tempDirectory.Path);
        }

        var slice = JsonSerializer.Deserialize<MethodsSlice>(json, JsonStringEnums);
        var count = Assert.Single(slice!.Methods!, method => method.Name == "Count");
        Assert.Equal("Internal", count.Attributes);
        Assert.Equal("Int", Assert.Single(count.Parameters!, parameter => parameter.Name == "items").Type);
        Assert.Equal("String", Assert.Single(count.Parameters!, parameter => parameter.Name == "name").Type);
    }

    [Fact]
    public void GetDataFlows_SwedishLocale_FormatsNegativeSinkArgumentInvariantly()
    {
        // A sink invoked on a tainted receiver records SinkArgumentIndex -1; under sv-SE that
        // interpolated as U+2212 MINUS SIGN in the slice summary. FakeCommand keeps the fixture
        // self-contained: "ExecuteNonQuery" is an exact-name sink pattern, and a constructor
        // argument's taint taints the created instance.
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "ReceiverSink.cs"), ReceiverSinkFixture);

        string json;
        using (new CultureScope("sv-SE"))
        {
            json = DataFlowAnalyzer.GetDataFlows(tempDirectory.Path);
        }

        var result = JsonSerializer.Deserialize<DataFlowResult>(json, JsonStringEnums);
        Assert.NotNull(result);
        var receiverSlice = Assert.Single(result.Slices!, slice => slice.SinkArgumentIndex == -1);
        Assert.EndsWith("argument -1.", receiverSlice.Summary);
        Assert.DoesNotContain(result.Slices!, slice => slice.Summary.Contains('−'));
    }

    [Fact]
    public void DebugLog_SwedishLocale_UsesInvariantTimestampsAndByteFormats()
    {
        using var cultureScope = new CultureScope("sv-SE");

        // Byte formatting rounds through the invariant culture: "1.3 GB", never "1,3 GB".
        Assert.Equal("1.3 GB", DebugLog.FormatBytes(1395864371));
        Assert.Equal("15 KB", DebugLog.FormatBytes(15 * 1024 + 300));

        var recorder = new LineRecorder();
        lock (ConsoleOutputLock)
        {
            var originalError = Console.Error;
            Console.SetError(recorder);
            try
            {
                DebugLog.Configure(true);
                using (DebugLog.Phase("locale-invariance-test"))
                {
                }
            }
            finally
            {
                DebugLog.Configure(false);
                Console.SetError(originalError);
            }
        }

        var lines = recorder.Snapshot();
        Assert.Contains(lines, line => Regex.IsMatch(line, @"^\[dosai \+\d+\.\d{3}s\] start locale-invariance-test$"));
        Assert.Contains(lines, line => Regex.IsMatch(line, @"^\[dosai \+\d+\.\d{3}s\] end locale-invariance-test in \d+\.\d{3}s, managed heap \d+ [KMG]B, working set \d+(\.\d+)? [KMG]B$"));
    }

    [Fact]
    public void QueryJson_GermanAndSwedishLocales_CompareNumbersInvariantly()
    {
        // JSON numbers are invariant text. Parsed through the current culture, de-DE read the
        // '.' in "7.5" as a group separator (75 > 10) and sv-SE failed to parse it at all, so
        // the same query selected different elements depending on the machine.
        const string json = """
{
  "items": [
    { "name": "low", "score": 7.5 },
    { "name": "high", "score": 10.25 },
    { "name": "exact", "score": 8 }
  ]
}
""";

        foreach (var culture in new[] { "", "de-DE", "sv-SE" })
        {
            using var scope = new CultureScope(culture);
            var above = JsonSerializer.Deserialize<List<JsonElement>>(DosaiQueryEngine.QueryJson(json, "items[score>8]"));
            Assert.Equal(new[] { "high" }, above!.Select(item => item.GetProperty("name").GetString()));
            var fractional = JsonSerializer.Deserialize<List<JsonElement>>(DosaiQueryEngine.QueryJson(json, "items[score<=7.5]"));
            Assert.Equal(new[] { "low" }, fractional!.Select(item => item.GetProperty("name").GetString()));
        }
    }

    [Fact]
    public void GraphExports_SwedishLocale_FormatNegativeNumbersInvariantly()
    {
        // Graph sidecars write numbers with ToString(); a negative value (an unknown line) took
        // the locale minus sign U+2212 under sv-SE.
        var result = new DataFlowResult
        {
            Nodes = [new DataFlowNode { Id = "n1", Name = "source", Kind = "Source", LineNumber = -1 }],
            Edges = [new DataFlowEdge { Id = "e1", SourceId = "n1", TargetId = "n1", Kind = "Assignment", LineNumber = -1 }]
        };

        using var scope = new CultureScope("sv-SE");
        foreach (var format in new[] { DataFlowExportFormat.GraphMl, DataFlowExportFormat.Gexf })
        {
            var exported = DataFlowExporter.Export(result, format);
            Assert.Contains("-1", exported, StringComparison.Ordinal);
            Assert.DoesNotContain('−', exported);
        }
    }

    public static TheoryData<string> LocalesWithDistinctRules => new()
    {
        "tr-TR", // dotted and dotless I casing
        "sv-SE", // U+2212 minus sign, decimal comma, non-breaking-space grouping
        "de-DE", // decimal comma, '.' grouping
        "hu-HU", // collation contractions ("cs", "dz", "gy", ...)
        "da-DK"  // "aa" sorts as "å"
    };

    [Theory]
    [MemberData(nameof(LocalesWithDistinctRules))]
    public void Cli_Outputs_AreByteIdenticalAcrossLocales(string cultureName)
    {
        // Issue #63 asks for the same bytes on every machine, not just for the two fields it
        // named: every command's JSON and graph sidecars must match an invariant-culture run
        // except for the per-run timestamps and serial number.
        using var fixture = new TemporaryDirectory();
        WriteLocaleSensitiveFixture(fixture.Path);
        using var invariantOutput = new TemporaryDirectory();
        using var localizedOutput = new TemporaryDirectory();

        RunEveryCommand(fixture.Path, invariantOutput.Path, "");
        RunEveryCommand(fixture.Path, localizedOutput.Path, cultureName);

        var invariantFiles = Directory.GetFiles(invariantOutput.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(invariantFiles, Directory.GetFiles(localizedOutput.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.True(invariantFiles.Count >= 8, $"expected every command's output, got {string.Join(", ", invariantFiles)}");
        foreach (var file in invariantFiles)
        {
            var expected = MaskRunIdentity(File.ReadAllText(Path.Combine(invariantOutput.Path, file!)));
            var actual = MaskRunIdentity(File.ReadAllText(Path.Combine(localizedOutput.Path, file!)));
            Assert.True(expected == actual, $"{file} differs under {cultureName}");
        }

        // The fixture must actually reach the culture-sensitive paths, so the equality above
        // is not vacuous.
        var methods = File.ReadAllText(Path.Combine(invariantOutput.Path, "methods.json"));
        Assert.Contains("\"Attributes\":\"Internal\"", methods, StringComparison.Ordinal);
        Assert.Contains("\"Type\":\"Int\"", methods, StringComparison.Ordinal);
        Assert.Contains("argument -1.", File.ReadAllText(Path.Combine(invariantOutput.Path, "dataflows.json")), StringComparison.Ordinal);
        Assert.Contains("argument -1.", File.ReadAllText(Path.Combine(invariantOutput.Path, "query.json")), StringComparison.Ordinal);
    }

    private const string ReceiverSinkFixture = """
class FakeCommand
{
    public FakeCommand(string sql)
    {
        Sql = sql;
    }

    public string Sql { get; }
    public int ExecuteNonQuery() => 0;
}

class ReceiverSinkSample
{
    static void Main(string[] args)
    {
        var command = new FakeCommand(args[0]);
        command.ExecuteNonQuery();
    }
}
""";

    private static void WriteLocaleSensitiveFixture(string directory)
    {
        File.WriteAllText(Path.Combine(directory, "ReceiverSink.cs"), ReceiverSinkFixture);
        File.WriteAllText(Path.Combine(directory, "Inventory.cs"), """
using System.Security.Cryptography;

namespace Csárdás.Aarhus;

internal interface IInventory
{
    int Count(int items, string name);
}

internal sealed class Inventory : IInventory
{
    internal int Count(int items, string name) => items;
    int IInventory.Count(int items, string name) => items - 1;
}

internal class Czech
{
    internal static byte[] Digest(byte[] data) => MD5.HashData(data);

    internal static byte[] Derive(string password, byte[] salt)
    {
        using var derive = new Rfc2898DeriveBytes(password, salt, 1000, HashAlgorithmName.SHA1);
        return derive.GetBytes(16);
    }

    internal int Use(IInventory inventory) => inventory.Count(-1, "Ínput");
}
""");
    }

    private static void RunEveryCommand(string fixture, string output, string cultureName)
    {
        using var scope = new CultureScope(cultureName);
        lock (ConsoleOutputLock)
        {
            var originalOut = Console.Out;
            var originalError = Console.Error;
            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);
            try
            {
                string Out(string name) => Path.Combine(output, name);
                Assert.Equal(0, CommandLine.Main(["methods", "--path", fixture, "--o", Out("methods.json"), "--callgraph-format", "graphml", "--callgraph-out", Out("callgraph.graphml")]));
                Assert.Equal(0, CommandLine.Main(["methods", "--path", fixture, "--o", Out("methods-gexf.json"), "--callgraph-format", "gexf", "--callgraph-out", Out("callgraph.gexf")]));
                Assert.Equal(0, CommandLine.Main(["dataflows", "--path", fixture, "--o", Out("dataflows.json"), "--graph-format", "graphml", "--graph-out", Out("dataflows.graphml")]));
                Assert.Equal(0, CommandLine.Main(["dataflows", "--path", fixture, "--o", Out("dataflows-gexf.json"), "--graph-format", "gexf", "--graph-out", Out("dataflows.gexf")]));
                Assert.Equal(0, CommandLine.Main(["crypto", "--path", fixture, "--o", Out("crypto.json")]));
                Assert.Equal(0, CommandLine.Main(["crypto", "--path", fixture, "--o", Out("cbom.json"), "--format", "cyclonedx"]));
                Assert.Equal(0, CommandLine.Main(["agent-context", "--path", fixture, "--o", Out("agent-context.json")]));
                Assert.Equal(0, CommandLine.Main(["query", "--input", Out("dataflows.json"), "--o", Out("query.json"), "--query", "slices[sinkArgumentIndex<0]"]));
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalError);
                DebugLog.Configure(false);
            }
        }
    }

    private static string MaskRunIdentity(string text)
    {
        text = Regex.Replace(text, "\"(GeneratedAt|generatedAt|timestamp)\"\\s*:\\s*\"[^\"]*\"", "\"$1\":\"<masked>\"");
        return Regex.Replace(text, "urn:uuid:[0-9a-fA-F-]{36}", "urn:uuid:<masked>");
    }

    #endregion
}
