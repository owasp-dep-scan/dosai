using System.Text.Json;
using Depscan;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Dosai.Tests;

// Issue #110: the whole tree compiled with one global-usings tree holding every project's implicit
// usings, so a Web SDK project's Microsoft.Extensions.Logging made `ILogger` ambiguous (CS0104)
// in a class library that logs through Serilog, and the library's calls stayed unbound. A
// file now compiles with the usings of its own project, written into its tree as file-local
// directives when the projects disagree.
public sealed class Issue110FileUsingsTests
{
    private const string WebProject = """<Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><TargetFramework>net8.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>""";
    private const string LibProject = """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>""";
    private const string ClassicProject = """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>""";

    // Stand-ins for the two packages, declared in the tree so the test needs no restore.
    private const string AspNetLogging = """
        namespace Microsoft.Extensions.Logging;

        public interface ILogger
        {
            void LogInformation(string message);
        }
        """;

    private const string SerilogSource = """
        namespace Serilog;

        public interface ILogger
        {
            void Information(string template);
        }
        """;

    private const string GreeterSource = """
        using Serilog;

        namespace Lib;

        public static class Greeter
        {
            public static void Greet(ILogger logger)
            {
                logger.Information("hello");
            }
        }
        """;

    private const string ServerSource = """
        namespace App;

        public class Server
        {
            // Bound through the Web SDK's Microsoft.Extensions.Logging using.
            public ILogger Log { get; set; } = null!;

            public void Run()
            {
                Log.LogInformation("started");
                Lib.Greeter.Greet(null!);
            }
        }
        """;

    [Fact]
    public void Methods_ALibraryThatLogsThroughSerilog_BindsItsCallsBesideAWebProject()
    {
        using var fixture = new TempDir();
        WriteTree(fixture.Path);

        var slice = Depscan.Dosai.GetMethodsSlice(fixture.Path);

        var information = Assert.Single(slice.MethodCalls!, call => call.Path.EndsWith("Greeter.cs", StringComparison.Ordinal) && call.CalledMethod.Contains("Information", StringComparison.Ordinal));
        Assert.NotEqual(AnalysisEvidenceKind.SourceUnresolved, information.EvidenceKind);
        Assert.StartsWith("Serilog.ILogger.Information", information.CalledMethod, StringComparison.Ordinal);
        // The Web project's own file still binds through its project's usings, and the call
        // into the library has the id of the library's own node.
        var logInformation = Assert.Single(slice.MethodCalls!, call => call.Path.EndsWith("Server.cs", StringComparison.Ordinal) && call.CalledMethod.Contains("LogInformation", StringComparison.Ordinal));
        Assert.NotEqual(AnalysisEvidenceKind.SourceUnresolved, logInformation.EvidenceKind);
        var greet = Assert.Single(slice.CallGraph!.Nodes, node => node.Name == "Greet");
        Assert.Contains(slice.CallGraph.Edges, edge => edge.TargetId == greet.Id && edge.CallLocation?.FileName == "Server.cs");
        Assert.Contains(slice.Diagnostics!, line => line.StartsWith("Global usings differ between projects", StringComparison.Ordinal));
    }

    [Fact]
    public void Methods_TheSameTreeTwice_GivesTheSameJson()
    {
        using var fixture = new TempDir();
        WriteTree(fixture.Path);

        string Run() => JsonSerializer.Serialize(Project(Depscan.Dosai.GetMethodsSlice(fixture.Path)), new JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });

        Assert.Equal(Run(), Run());
    }

    private static object Project(MethodsSlice slice) => new { slice.Methods, slice.MethodCalls, slice.CallGraph, slice.Diagnostics };

    [Fact]
    public void DataFlowsAndCrypto_ShareTheFix()
    {
        using var fixture = new TempDir();
        WriteTree(fixture.Path);
        File.WriteAllText(Path.Combine(fixture.Path, "Lib", "Reader.cs"), """
            using Serilog;

            namespace Lib;

            public static class Reader
            {
                public static string Read(ILogger logger, string path)
                {
                    var input = Console.ReadLine();
                    logger.Information(path);
                    return File.ReadAllText(input!);
                }
            }
            """);

        var flows = DataFlowAnalyzer.Analyze(fixture.Path);
        var crypto = CryptoAnalyzer.Analyze(fixture.Path);

        // Both parse with the scan root, so the file-local usings reach them: no unbound
        // ambiguity in the library, and the sink call is found.
        Assert.Contains(flows.Slices, slice => flows.Nodes.Single(node => node.Id == slice.SinkId).Name == "ReadAllText");
        Assert.DoesNotContain(flows.Diagnostics.Concat(crypto.Diagnostics), line => line.Contains("CS0104", StringComparison.Ordinal));
    }

    [Fact]
    public void GlobalUsings_ProjectsThatAgree_KeepTheSingleGlobalTree()
    {
        using var fixture = new TempDir();
        WriteProject(fixture.Path, "A", LibProject);
        WriteProject(fixture.Path, "B", LibProject);

        var decision = GlobalUsings.Resolve(fixture.Path);

        Assert.True(decision.Enabled);
        Assert.NotNull(decision.Tree);
        Assert.Null(decision.PerFile);
        var tree = CSharpSourceParser.Parse("namespace A; public class C {}", Path.Combine(fixture.Path, "A", "C.cs"), fixture.Path);
        Assert.DoesNotContain("using ", tree.GetText().ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void GlobalUsings_DisagreeingProjects_GiveEachFileItsOwnSet()
    {
        using var fixture = new TempDir();
        WriteProject(fixture.Path, "App", WebProject);
        WriteProject(fixture.Path, "Lib", LibProject);
        WriteProject(fixture.Path, "Classic", ClassicProject);

        var decision = GlobalUsings.Resolve(fixture.Path);

        Assert.True(decision.Enabled);
        Assert.Null(decision.Tree);
        var perFile = decision.PerFile!;
        Assert.Contains("Microsoft.Extensions.Logging", perFile.SetFor(Path.Combine(fixture.Path, "App", "Deep", "X.cs")));
        Assert.DoesNotContain("Microsoft.Extensions.Logging", perFile.SetFor(Path.Combine(fixture.Path, "Lib", "X.cs")));
        Assert.Contains("System.Linq", perFile.SetFor(Path.Combine(fixture.Path, "Lib", "X.cs")));
        Assert.Empty(perFile.SetFor(Path.Combine(fixture.Path, "Classic", "X.cs")));
        // Outside every project: the union.
        Assert.Contains("Microsoft.Extensions.Logging", perFile.SetFor(Path.Combine(fixture.Path, "Tools", "X.cs")));
    }

    [Theory]
    [InlineData("using System.Text;\nusing Foo;\n\nnamespace N;\npublic class C { }\n", 2, "using Foo;using ")]
    [InlineData("extern alias X;\n\nnamespace N;\npublic class C { }\n", 1, "extern alias X;using ")]
    [InlineData("// header\n#nullable enable\nnamespace N;\npublic class C { }\n", 3, "using ")]
    [InlineData("global using System.Text;\npublic class C { }\n", 1, "global using System.Text;using ")]
    [InlineData("[assembly: System.CLSCompliant(true)]\nnamespace N;\npublic class C { }\n", 1, "using ")]
    [InlineData("// attributes only\n[assembly: System.CLSCompliant(true)]\n", 2, "using ")]
    public void WithFileUsings_InsertsOnTheLineOfAnExistingDirective_AndNeverMovesALine(string source, int lineWithInsertion, string expectedStart)
    {
        using var fixture = new TempDir();
        WriteProject(fixture.Path, "App", WebProject);
        WriteProject(fixture.Path, "Lib", LibProject);
        var file = Path.Combine(fixture.Path, "App", "C.cs");

        var tree = CSharpSourceParser.Parse(source, file, fixture.Path);

        Assert.DoesNotContain(tree.GetDiagnostics(), diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        var lines = tree.GetText().Lines;
        Assert.Equal(source.Split('\n').Length, lines.Count);
        Assert.StartsWith(expectedStart, lines[lineWithInsertion - 1].ToString(), StringComparison.Ordinal);
        Assert.Contains("using Microsoft.AspNetCore.Builder;", lines[lineWithInsertion - 1].ToString(), StringComparison.Ordinal);
        // Other lines are the file's own.
        for (var line = 0; line < lines.Count; line++)
        {
            if (line != lineWithInsertion - 1)
            {
                Assert.Equal(source.Split('\n')[line], lines[line].ToString());
            }
        }
    }

    [Fact]
    public void WithFileUsings_DoesNotRepeatADirectiveTheFileDeclares()
    {
        using var fixture = new TempDir();
        WriteProject(fixture.Path, "App", WebProject);
        WriteProject(fixture.Path, "Lib", LibProject);

        var tree = CSharpSourceParser.Parse("using System;\nusing System.Linq;\nnamespace N;\npublic class C { }\n", Path.Combine(fixture.Path, "Lib", "C.cs"), fixture.Path);

        var text = tree.GetText().ToString();
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(text, @"using System;").Count);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(text, @"using System\.Linq;").Count);
        Assert.Contains("using System.IO;", text, StringComparison.Ordinal);
    }

    private static void WriteTree(string root)
    {
        WriteProject(root, "App", WebProject);
        WriteProject(root, "Lib", LibProject);
        File.WriteAllText(Path.Combine(root, "App", "Logging.cs"), AspNetLogging);
        File.WriteAllText(Path.Combine(root, "App", "Server.cs"), ServerSource);
        File.WriteAllText(Path.Combine(root, "Lib", "Serilog.cs"), SerilogSource);
        File.WriteAllText(Path.Combine(root, "Lib", "Greeter.cs"), GreeterSource);
    }

    private static void WriteProject(string root, string name, string project)
    {
        Directory.CreateDirectory(Path.Combine(root, name));
        File.WriteAllText(Path.Combine(root, name, $"{name}.csproj"), project);
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir() => Directory.CreateDirectory(Path);

        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dosai-issue110-" + Guid.NewGuid().ToString("N"));

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
}
