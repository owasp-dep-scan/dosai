using Depscan;
using Xunit;

namespace Depscan.Tests;

/// <summary>
///     Source-mode data-flow shapes the walker and the summary collector lost: flows through
///     object and collection initializers, deconstruction, helpers that reach a sink through a
///     local or return a source, and object state carried between methods (a constructor or a
///     setter storing a value that another method reads into a sink). Each method of a fixture
///     reads its own source; the assertions name the methods whose sink must, or must not, be
///     reached.
/// </summary>
public class DataFlowEngineTests
{
    private sealed class TempDir : IDisposable
    {
        public TempDir() => Directory.CreateDirectory(Path);

        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dosai-dataflow-engine-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>The methods holding the sink of at least one slice.</summary>
    private static HashSet<string> MethodsWithSlices(string source)
    {
        using var fixture = new TempDir();
        File.WriteAllText(System.IO.Path.Combine(fixture.Path, "Fixture.cs"), source);
        var result = DataFlowAnalyzer.Analyze(fixture.Path);
        var nodes = result.Nodes.ToDictionary(node => node.Id);
        return result.Slices
            .Select(slice => nodes[slice.SinkId].MethodName)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
    }

    private const string Header = """
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Fixture;

""";

    [Fact]
    public void ObjectAndCollectionInitializers_CarryTaintFieldByField()
    {
        var methods = MethodsWithSlices(Header + """
public class Options
{
    public string Command { get; set; } = "";
    public string Mode { get; set; } = "";
    private string backing = "";
    public string Bodied { get => backing; set => backing = value; }
}

public static class Flows
{
    public static void AutoProperty() { var i = Console.ReadLine()!; var o = new Options { Command = i }; Process.Start(o.Command); }
    public static void BodiedProperty() { var i = Console.ReadLine()!; var o = new Options { Bodied = i }; Process.Start(o.Bodied); }
    public static void WholeObject() { var i = Console.ReadLine()!; Process.Start(new ProcessStartInfo { FileName = i }); }
    public static void WholeObjectVariable() { var i = Console.ReadLine()!; var psi = new ProcessStartInfo { FileName = i, UseShellExecute = false }; Process.Start(psi); }
    public static void IndexInitializer() { var i = Console.ReadLine()!; var m = new Dictionary<string, string> { ["k"] = i }; Process.Start(m["k"]); }
    public static void AddInitializer() { var i = Console.ReadLine()!; var m = new Dictionary<string, string> { { "k", i } }; Process.Start(m["k"]); }
    public static void ListInitializer() { var i = Console.ReadLine()!; var l = new List<string> { i }; Process.Start(l[0]); }
    public static void CleanSibling() { var i = Console.ReadLine()!; var o = new Options { Command = i, Mode = "fixed" }; Process.Start(o.Mode); }
    public static void ReassignedClean() { var i = Console.ReadLine()!; var o = new Options { Command = i }; o.Command = "fixed"; Process.Start(o.Command); }
}
""");

        Assert.Subset(new HashSet<string> { "AutoProperty", "BodiedProperty", "WholeObject", "WholeObjectVariable", "IndexInitializer", "AddInitializer", "ListInitializer" }, methods);
        // Field sensitivity: a clean sibling member and a member reassigned a clean value stay clean.
        Assert.DoesNotContain("CleanSibling", methods);
        Assert.DoesNotContain("ReassignedClean", methods);
    }

    [Fact]
    public void Deconstruction_WritesEachTargetFromItsElement()
    {
        var methods = MethodsWithSlices(Header + """
public record Pair(string Name, int Count);

public static class Flows
{
    public static void Declare() { var i = Console.ReadLine()!; var (a, b) = (i, 1); Process.Start(a); }
    public static void Assign() { var i = Console.ReadLine()!; string a; int b; (a, b) = (i, 1); Process.Start(a); }
    public static void Swap() { var i = Console.ReadLine()!; string a = "x", b = i; (a, b) = (b, a); Process.Start(a); }
    public static void Record() { var i = Console.ReadLine()!; var (name, _) = new Pair(i, 1); Process.Start(name); }
    public static void Loop() { var i = Console.ReadLine()!; foreach (var (k, v) in new Dictionary<string, string> { [i] = i }) Process.Start(v); }
    public static void CleanElement() { var i = Console.ReadLine()!; var (a, b) = (i, "fixed"); Process.Start(b); }
    public static void SwapClean() { var i = Console.ReadLine()!; string a = i, b = "x"; (a, b) = (b, a); Process.Start(a); }
}
""");

        Assert.Subset(new HashSet<string> { "Declare", "Assign", "Swap", "Record", "Loop" }, methods);
        Assert.DoesNotContain("CleanElement", methods);
        // The swap reads both old values before writing: `a` ends up with the clean "x".
        Assert.DoesNotContain("SwapClean", methods);
    }

    [Fact]
    public void Helpers_ReachSinksThroughLocalsAndReturnSources()
    {
        var methods = MethodsWithSlices(Header + """
public static class Flows
{
    static void SinkViaLocal(string c) { var cmd = "run " + c; Process.Start(cmd); }
    static void SinkViaTwoHops(string c) => SinkViaLocal(c);
    static string SourceViaLocal() { var line = Console.ReadLine(); return line!; }
    static async Task<string> SourceAsync() => (await Console.In.ReadLineAsync())!;
    static IEnumerable<string> SourceIterator() { yield return Console.ReadLine()!; }
    static bool IsSafe() => Console.ReadLine() == "ok";

    public static void ViaLocal() { var i = Console.ReadLine()!; SinkViaLocal(i); }
    public static void TwoHops() { var i = Console.ReadLine()!; SinkViaTwoHops(i); }
    public static void SourceHelper() { Process.Start(SourceViaLocal()); }
    public static async Task AsyncSourceHelper() { Process.Start(await SourceAsync()); }
    public static void IteratorSource() { foreach (var line in SourceIterator()) Process.Start(line); }
    public static void DecisionOnly() { Process.Start(IsSafe().ToString()); }
    public static void ReaderOverStream() { var body = new StreamReader(Console.OpenStandardInput()).ReadToEnd(); Process.Start(body); }
}
""");

        Assert.Subset(new HashSet<string> { "ViaLocal", "TwoHops", "SourceHelper", "AsyncSourceHelper", "IteratorSource", "ReaderOverStream" }, methods);
        // A bool is a decision about the source, not the source.
        Assert.DoesNotContain("DecisionOnly", methods);
    }

    [Fact]
    public void ObjectState_CarriesTaintBetweenMethodsOfTheSameObject()
    {
        var methods = MethodsWithSlices(Header + """
public class Runner
{
    private string command = "";
    private readonly string fixedCommand;
    private string other = "safe";
    public Runner(string c) { fixedCommand = c; }
    public void Set(string c) => command = c;
    public void Go() => Process.Start(command);
    public void GoFixed() => Process.Start(fixedCommand);
    public void GoOther() => Process.Start(other);
}

public class Service(string connection)
{
    public void Run() => Process.Start(connection);
}

public class Configured
{
    private readonly string fromEnvironment = Console.ReadLine()!;
    private readonly string fromConstructor;
    public Configured() { fromConstructor = Console.ReadLine()!; }
    public void Launch() => Process.Start(fromConstructor);
}

public static class Flows
{
    public static void SetterThenGo() { var i = Console.ReadLine()!; var r = new Runner("x"); r.Set(i); r.Go(); }
    public static void ConstructorThenGo() { var i = Console.ReadLine()!; var r = new Runner(i); r.GoFixed(); }
    public static void InlineReceiver() { var i = Console.ReadLine()!; new Runner(i).GoFixed(); }
    public static void PrimaryConstructor() { var i = Console.ReadLine()!; new Service(i).Run(); }
    public static void UntaintedMember() { var i = Console.ReadLine()!; var r = new Runner(i); r.GoOther(); }
    public static void ReassignedReceiver() { var i = Console.ReadLine()!; var r = new Runner("x"); r.Set(i); r = new Runner("y"); r.Go(); }
}
""");

        Assert.Subset(new HashSet<string> { "SetterThenGo", "ConstructorThenGo", "InlineReceiver", "PrimaryConstructor", "Launch" }, methods);
        Assert.DoesNotContain("UntaintedMember", methods);
        // A new object in the variable does not inherit the old one's members.
        Assert.DoesNotContain("ReassignedReceiver", methods);
    }

    [Fact]
    public void WebFormsSource_NeedsAControlNotAnyTextProperty()
    {
        var methods = MethodsWithSlices(Header + """
public class TextBox { public string Text { get; set; } = ""; }

public static class Flows
{
    public static void ControlByName(TextBox txtCommand) { Process.Start(txtCommand.Text); }
    public static void EncodingText() { var bytes = System.Text.Encoding.UTF8.GetBytes("x"); Process.Start(System.Text.Encoding.UTF8.GetString(bytes)); }
    public static void BuilderText() { var sb = new System.Text.StringBuilder("ls"); Process.Start(sb.ToString()); }
}
""");

        Assert.Contains("ControlByName", methods);
        Assert.DoesNotContain("EncodingText", methods);
        Assert.DoesNotContain("BuilderText", methods);
    }
}
