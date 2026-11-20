using Depscan;
using Xunit;

namespace Depscan.Tests;

/// <summary>
///     Issue #131 follow-ups: local functions keep their own summaries, initializer origins
///     stay in their own file, constants are not sources, and the cross-pass fingerprint set
///     only ever merges a real duplicate of a source-pass flow.
/// </summary>
public class Issue131FollowUpTests
{
    private sealed class TempDir : IDisposable
    {
        public TempDir() => Directory.CreateDirectory(Path);

        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dosai-issue131b-" + Guid.NewGuid().ToString("N"));

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

    internal static DataFlowResult AnalyzeSources(params (string Name, string Source)[] files)
    {
        using var fixture = new TempDir();
        foreach (var (name, source) in files)
        {
            var target = System.IO.Path.Combine(fixture.Path, name);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            File.WriteAllText(target, source);
        }

        return DataFlowAnalyzer.Analyze(fixture.Path);
    }

    private const string Header = """
using System;
using System.Diagnostics;

namespace Fixture;

""";

    [Fact]
    public void LocalFunction_KeepsItsOwnSummary()
    {
        var result = AnalyzeSources(("Fixture.cs", Header + """
public static class Program
{
    public static void Run()
    {
        var x = Console.ReadLine()!;
        Helper(x);
        static void Helper(string y) { Process.Start(y); }
    }

    public static void RunLambda()
    {
        var x = Console.ReadLine()!;
        Action<string> act = y => { Process.Start(y); };
        act(x);
    }
}
"""));
        var nodes = result.Nodes.ToDictionary(node => node.Id);
        Assert.Contains(result.MethodSummaries, summary => summary.Method.Contains("Program.Helper", StringComparison.Ordinal));
        var slice = Assert.Single(result.Slices);
        Assert.Equal("Run", nodes[slice.SinkId].MethodName);
    }

    [Fact]
    public void InitializerOrigin_StaysInTheFileThatHoldsTheInitializer()
    {
        var cfg = Header + """
public static class Cfg
{
    public static string Alpha = Console.ReadLine()!;

    static Cfg() { }
}
""";
        var result = AnalyzeSources(
            ("sub/Cfg.cs", cfg),
            ("Program.cs", Header + """
public static class Program
{
    public static void Run() { Process.Start(Cfg.Alpha); }
}
"""));
        var nodes = result.Nodes.ToDictionary(node => node.Id);
        var slice = Assert.Single(result.Slices);
        var source = nodes[slice.SourceId];
        Assert.Equal("Cfg.cs", source.FileName);
        Assert.Equal("sub/Cfg.cs", source.Path);
        Assert.Equal(Array.FindIndex(cfg.Split('\n'), line => line.Contains("Alpha =", StringComparison.Ordinal)) + 1, source.LineNumber);
    }

    [Fact]
    public void ConstantInitializer_IsNotTheOriginOfAMemberAssignedFromASourceElsewhere()
    {
        var text = Header + """
public static class Holder
{
    static string s = "safe-constant";

    static Holder() { }

    public static void Load() { s = Console.ReadLine()!; }
    public static void Use() { Process.Start(s); }
}
""";
        var result = AnalyzeSources(("Holder.cs", text));
        var nodes = result.Nodes.ToDictionary(node => node.Id);
        var slice = Assert.Single(result.Slices);
        // A member assigned from a source by a method keeps no origin: the source is minted at
        // the read, not at the constant initializer.
        var readLine = Array.FindIndex(text.Split('\n'), line => line.Contains("Process.Start(s)", StringComparison.Ordinal)) + 1;
        Assert.Equal(readLine, nodes[slice.SourceId].LineNumber);
    }

    [Fact]
    public void BlockLambdaInAFieldInitializer_DoesNotReplaceTheConstructorsFixpointRoot()
    {
        var result = AnalyzeSources(("Fixture.cs", Header + """
public class Holder
{
    readonly Func<string, int> _length = x => { return x.Length; };

    public Holder(string value) { Forward(value); }

    static void Forward(string v) { Last(v); }

    static void Last(string v) { Process.Start(v); }
}

public static class Program
{
    public static void Run() { var h = new Holder(Console.ReadLine()!); }
}
"""));
        Assert.NotEmpty(result.Slices);
    }

    [Fact]
    public void ConstantInitializers_LeaveNoEmptyConstructorSummaries()
    {
        var result = AnalyzeSources(("Fixture.cs", Header + """
public class Plain
{
    static string s = "x";
    int n = 5;
    public void Touch() { Console.WriteLine(n); }
}
"""));
        Assert.DoesNotContain(result.MethodSummaries, summary => summary.Method.Contains(".ctor", StringComparison.Ordinal) || summary.Method.Contains(".cctor", StringComparison.Ordinal));
    }

    private static void AssertNoDuplicateNodes(DataFlowResult result) =>
        Assert.Empty(result.Nodes.GroupBy(node => (node.Kind, node.Name, node.Path, node.LineNumber, node.ColumnNumber)).Where(group => group.Count() > 1));

    [Fact]
    public void LocalFunctionWithItsOwnSource_IsReportedOnce()
    {
        var result = AnalyzeSources(("Fixture.cs", Header.Replace("using System.Diagnostics;", "using System.IO;") + """
public static class Program
{
    public static void Main()
    {
        Local2();
        static void Local2() { var x = Console.ReadLine()!; File.ReadAllText(x); }
    }
}
"""));
        var slice = Assert.Single(result.Slices);
        Assert.Equal("Local2", result.Nodes.Single(node => node.Id == slice.SinkId).MethodName);
        AssertNoDuplicateNodes(result);
    }

    [Fact]
    public void TopLevelLocalFunction_IsReportedOnce()
    {
        var result = AnalyzeSources(("Program.cs", """
using System;
using System.IO;

Local2();

static void Local2() { var x = Console.ReadLine()!; File.ReadAllText(x); }
"""));
        Assert.Single(result.Slices);
        AssertNoDuplicateNodes(result);
    }
}
