using Depscan;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace Dosai.Tests;

// Issue #78: in a built source tree the data-flow IL pass also reads the package assemblies a
// build copies into bin/. Those flows stay in the graph, but a flow whose every node lies in a
// dependency assembly is scoped "dependency" and capped at low severity, and the derived facts
// (weaknesses, dangerous-API and package reachability) stop presenting package internals as
// application findings. The fixture is a source tree with an emitted build output: the app and
// a project reference (deps.json "project"), two packages (deps.json "package"), and a tool a
// package drops in a subdirectory that no deps.json names. The Summaries_ tests pin the IL
// interpreters' stack model, whose drift sent the package loops of the issue into the state
// budget.
public sealed class Issue78DependencyFlowTests
{
    private const string AppSource = """
        using System.Diagnostics;

        namespace App;

        public static class Program
        {
            public static void Main(string[] args)
            {
                Process.Start(args[0]);
                Vendor.Runner.Execute(args[1]);
            }
        }
        """;

    private const string VendorSource = """
        using System;
        using System.Diagnostics;

        namespace Vendor;

        public static class Runner
        {
            public static void Execute(string value) => Process.Start(value);

            public static void Interactive()
            {
                var line = Console.ReadLine();
                Process.Start(line!);
            }
        }
        """;

    private const string CodecSource = """
        using System;
        using System.IO;

        namespace Codec;

        public static class Store
        {
            public static void Save()
            {
                var path = Console.ReadLine();
                File.WriteAllText(path!, "payload");
            }
        }
        """;

    private const string HelperSource = """
        using System;
        using System.Diagnostics;

        namespace Helper;

        public static class Shell
        {
            public static void Run()
            {
                var line = Console.ReadLine();
                Process.Start(line!);
            }
        }
        """;

    private const string ToolSource = """
        using System;
        using System.Diagnostics;

        namespace Tool;

        public static class Cli
        {
            public static void Run()
            {
                var line = Console.ReadLine();
                Process.Start(line!);
            }
        }
        """;

    private const string DepsJson = """
        {
          "runtimeTarget": { "name": ".NETCoreApp,Version=v10.0", "signature": "" },
          "targets": {
            ".NETCoreApp,Version=v10.0": {
              "App/1.0.0": { "dependencies": { "Vendor": "2.1.0", "Codec": "3.0.0", "Helper": "1.0.0" }, "runtime": { "App.dll": {} } },
              "Vendor/2.1.0": { "runtime": { "lib/net8.0/Vendor.dll": { "assemblyVersion": "2.1.0.0", "fileVersion": "2.1.0.0" } } },
              "Codec/3.0.0": { "runtime": { "lib/net8.0/Codec.dll": { "assemblyVersion": "3.0.0.0", "fileVersion": "3.0.0.0" } } },
              "Helper/1.0.0": { "runtime": { "Helper.dll": {} } }
            }
          },
          "libraries": {
            "App/1.0.0": { "type": "project", "serviceable": false, "sha512": "" },
            "Vendor/2.1.0": { "type": "package", "serviceable": true, "sha512": "sha512-x", "path": "vendor/2.1.0", "hashPath": "vendor.2.1.0.nupkg.sha512" },
            "Codec/3.0.0": { "type": "package", "serviceable": true, "sha512": "sha512-y", "path": "codec/3.0.0", "hashPath": "codec.3.0.0.nupkg.sha512" },
            "Helper/1.0.0": { "type": "project", "serviceable": false, "sha512": "" }
          }
        }
        """;

    [Fact]
    public void DataFlows_BuiltSourceTree_ScopesDependencyFlowsAndCapsTheirSeverity()
    {
        using var fixture = BuildFixture();
        var result = DataFlowAnalyzer.Analyze(fixture.Path);

        // The app's own flows (direct, and through the package's summary) and the project
        // reference's flow keep their severity and carry no scope.
        var appSlices = SlicesIn(result, "App.dll");
        Assert.Equal(2, appSlices.Count);
        Assert.All(appSlices, slice => Assert.Equal(("high", (string?)null), (slice.Severity, slice.Scope)));
        var helper = Assert.Single(SlicesIn(result, "Helper.dll"));
        Assert.Equal(("high", (string?)null), (helper.Severity, helper.Scope));

        // Package flows and the tool's flow stay in the graph, scoped and capped at low.
        var dependencySlices = SlicesIn(result, "Vendor.dll").Concat(SlicesIn(result, "Codec.dll")).Concat(SlicesIn(result, "Tool.dll")).ToList();
        Assert.Equal(3, dependencySlices.Count);
        Assert.All(dependencySlices, slice =>
        {
            Assert.Equal(TransparencyBuilder.DependencyScope, slice.Scope);
            Assert.Equal("low", slice.Severity);
            Assert.Contains("inside dependency code", slice.Summary, StringComparison.Ordinal);
        });
        Assert.Equal(3, result.Statistics.DependencySliceCount);
        Assert.Equal(6, result.Statistics.SliceCount);

        // Only nodes from dependency assemblies carry the scope property.
        Assert.All(result.Nodes.Where(node => node.Properties.TryGetValue("assembly", out var assembly) && assembly is "App.dll" or "Helper.dll"),
            node => Assert.False(TransparencyBuilder.IsDependencyNode(node)));
        Assert.All(result.Nodes.Where(node => node.Properties.TryGetValue("assembly", out var assembly) && assembly is "Vendor.dll" or "Codec.dll" or "Tool.dll"),
            node => Assert.True(TransparencyBuilder.IsDependencyNode(node)));
    }

    [Fact]
    public void DataFlows_BuiltSourceTree_DerivedFactsFollowTheScope()
    {
        using var fixture = BuildFixture();
        var result = DataFlowAnalyzer.Analyze(fixture.Path);
        var slicesById = result.Slices.ToDictionary(slice => slice.Id, StringComparer.Ordinal);

        // Weaknesses copy the slice's scope and severity and say why.
        Assert.All(result.WeaknessCandidates.Where(weakness => weakness.SliceId is not null), weakness =>
        {
            var slice = slicesById[weakness.SliceId!];
            Assert.Equal(slice.Scope, weakness.Scope);
            Assert.Equal(slice.Severity, weakness.Severity);
            Assert.Equal(slice.Scope is not null, weakness.ConfidenceReasons.Any(reason => reason.Contains("inside dependency code", StringComparison.Ordinal)));
        });

        // A sink inside dependency IL is a Low-confidence dangerous-API fact; the app's are not.
        var nodesById = result.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        Assert.All(result.DangerousApiReachability, api =>
        {
            var node = nodesById[api.NodeIds.Single()];
            Assert.Equal(TransparencyBuilder.IsDependencyNode(node) ? "Low" : "High", api.Confidence);
        });
        Assert.Contains(result.DangerousApiReachability, api => api.Confidence == "High");

        // Codec is only ever seen from inside its own IL: Low, with the reason. Vendor is also
        // reached from the app's call site, so its evidence is the app's and stays High.
        var codec = Assert.Single(result.PackageReachability, package => package.Purl == "pkg:nuget/Codec@3.0.0");
        Assert.Equal("Low", codec.Confidence);
        Assert.Contains(codec.ConfidenceReasons, reason => reason.StartsWith("All evidence lies inside dependency code", StringComparison.Ordinal));
        Assert.NotEmpty(codec.SliceIds);
        var vendor = Assert.Single(result.PackageReachability, package => package.Purl == "pkg:nuget/Vendor@2.1.0");
        Assert.Equal("High", vendor.Confidence);
        Assert.DoesNotContain(vendor.ConfidenceReasons, reason => reason.StartsWith("All evidence lies inside dependency code", StringComparison.Ordinal));

        // The agent context's high-risk lists leave dependency flows out.
        var context = TransparencyBuilder.BuildAgentContext(result, fixture.Path);
        Assert.NotEmpty(context.HighRiskWeaknesses);
        Assert.All(context.HighRiskWeaknesses, weakness => Assert.Null(weakness.Scope));
        Assert.All(context.HighRiskSlices, slice => Assert.Null(slice.Scope));

        // The markdown report counts them and lists application findings first.
        var report = TransparencyBuilder.ToMarkdownReport(result);
        Assert.Contains("- Slices inside dependency code: 3", report, StringComparison.Ordinal);
        Assert.Contains("- Scope: dependency code", report, StringComparison.Ordinal);
        Assert.True(report.IndexOf("- Severity: high", StringComparison.Ordinal) < report.IndexOf("- Scope: dependency code", StringComparison.Ordinal));
    }

    [Fact]
    public void DataFlows_BuiltSourceTree_SerializesTheScopeAndTheTreeReportLabelsIt()
    {
        using var fixture = BuildFixture();
        var json = DataFlowAnalyzer.GetDataFlows(fixture.Path);
        Assert.Contains("\"Scope\":\"dependency\"", json, StringComparison.Ordinal);
        Assert.Contains("\"scope\":\"dependency\"", json, StringComparison.Ordinal);
        Assert.Contains("\"DependencySliceCount\":3", json, StringComparison.Ordinal);

        var result = JsonSerializer.Deserialize<DataFlowResult>(json, new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } })!;
        using var writer = new StringWriter();
        CommandLine.WriteDataFlowTreeReport(writer, result, "flows.json");
        var tree = writer.ToString();
        Assert.Contains("6 flows (3 inside dependency code)", tree, StringComparison.Ordinal);
        Assert.Contains("(Medium, dependency code)", tree, StringComparison.Ordinal);
    }

    [Fact]
    public void DataFlows_SingleAssemblyTarget_IsTheApplication()
    {
        // Scanning a package assembly itself asks about that code: nothing is demoted.
        using var fixture = BuildFixture();
        var vendor = Directory.GetFiles(fixture.Path, "Vendor.dll", SearchOption.AllDirectories).Single();
        var result = DataFlowAnalyzer.Analyze(vendor);

        var slice = Assert.Single(result.Slices);
        Assert.Equal(("high", (string?)null), (slice.Severity, slice.Scope));
        Assert.Equal(0, result.Statistics.DependencySliceCount);
        Assert.DoesNotContain(result.Nodes, TransparencyBuilder.IsDependencyNode);
    }

    [Fact]
    public void DataFlows_NoDepsJson_FallsBackToRestoreMetadataByWholeName()
    {
        // .NET Framework-style output: no deps.json, packages.config names Vendor. App and an
        // assembly whose name only starts with a package name stay application code.
        using var fixture = new TempDir();
        var project = Path.Combine(fixture.Path, "App");
        var bin = Path.Combine(project, "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(project, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net48</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(project, "Program.cs"), "namespace App; public static class Entry { public static void Run() { } }");
        File.WriteAllText(Path.Combine(project, "packages.config"), """
            <?xml version="1.0" encoding="utf-8"?>
            <packages>
              <package id="Vendor" version="2.1.0" targetFramework="net48" />
            </packages>
            """);
        var vendor = Emit(bin, "Vendor", VendorSource);
        Emit(bin, "App", AppSource, references: vendor);
        Emit(bin, "Vendor.Extras", HelperSource.Replace("namespace Helper;", "namespace Vendor.Extras;", StringComparison.Ordinal));

        var result = DataFlowAnalyzer.Analyze(fixture.Path);

        Assert.All(SlicesIn(result, "Vendor.dll"), slice => Assert.Equal((TransparencyBuilder.DependencyScope, "low"), (slice.Scope, slice.Severity)));
        Assert.NotEmpty(SlicesIn(result, "Vendor.dll"));
        Assert.All(SlicesIn(result, "App.dll").Concat(SlicesIn(result, "Vendor.Extras.dll")), slice => Assert.Equal(("high", (string?)null), (slice.Severity, slice.Scope)));
        Assert.NotEmpty(SlicesIn(result, "Vendor.Extras.dll"));
    }

    [Fact]
    public void DataFlows_PackageNameBuiltFromSource_IsTheApplication()
    {
        // The tree builds Codec from source: a deps.json that calls it a package (a stale
        // output, a local feed of the same project) does not make the tree's own code a dependency.
        using var fixture = BuildFixture();
        var codecProject = Path.Combine(fixture.Path, "Codec");
        Directory.CreateDirectory(codecProject);
        File.WriteAllText(Path.Combine(codecProject, "Codec.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(codecProject, "Store.cs"), "namespace Codec; public static class Placeholder { }");

        var result = DataFlowAnalyzer.Analyze(fixture.Path);

        var codec = Assert.Single(SlicesIn(result, "Codec.dll"));
        Assert.Equal(("high", (string?)null), (codec.Severity, codec.Scope));
        Assert.All(SlicesIn(result, "Vendor.dll"), slice => Assert.Equal(TransparencyBuilder.DependencyScope, slice.Scope));
    }

    [Fact]
    public void Crypto_BuiltSourceTree_CountsDependencyDataFlowSlices()
    {
        using var fixture = BuildFixture();
        var result = CryptoAnalyzer.Analyze(fixture.Path);

        var dataFlows = Assert.IsType<DataFlowResult>(result.CryptoDataFlows);
        Assert.True(result.Statistics.CryptoDependencyDataFlowSliceCount > 0);
        Assert.Equal(dataFlows.Slices.Count(TransparencyBuilder.IsDependencySlice), result.Statistics.CryptoDependencyDataFlowSliceCount);
        Assert.Equal(dataFlows.Slices.Count, result.Statistics.CryptoDataFlowSliceCount);
        Assert.All(dataFlows.Slices.Where(TransparencyBuilder.IsDependencySlice), slice => Assert.Equal("low", slice.Severity));
    }

    [Fact]
    public void Diff_KeyedSliceSeverity_IsTheGroupsHighest()
    {
        // Two slices of one shape: a dependency flow capped at low listed first and an
        // application flow at high. The added key is a new high-severity slice either way.
        var empty = new DataFlowResult();
        var added = new DataFlowResult
        {
            Slices =
            [
                new DataFlowSlice { Id = "dfs1", SourceId = "a", SinkId = "b", SourceCategory = "cli", SinkCategory = "command", SinkArgument = "0", Severity = "low", Scope = TransparencyBuilder.DependencyScope },
                new DataFlowSlice { Id = "dfs2", SourceId = "c", SinkId = "d", SourceCategory = "cli", SinkCategory = "command", SinkArgument = "0", Severity = "high" }
            ]
        };

        using var diff = JsonDocument.Parse(TransparencyBuilder.DiffJson(empty, added));
        var riskDelta = diff.RootElement.GetProperty("RiskDelta");
        Assert.Equal(1, riskDelta.GetProperty("NewHighSeveritySlices").GetInt32());
        Assert.Equal(0, riskDelta.GetProperty("NewLowSeveritySlices").GetInt32());
    }

    [Fact]
    public void Summaries_GenericArrayStores_DoNotDriftTheStackOrHitTheBudget()
    {
        // A generic `stelem` pops three values. Modelled as popping none, every pass round the
        // copy loop grew the abstract stack, each pass looked like a new state, and the method
        // ran into the 10000-state budget with a summary that only came out right by accident.
        using var fixture = new TempDir();
        var dll = Emit(fixture.Path, "Arrays", """
            namespace Fixture;

            public static class Arrays
            {
                public static T[] Copy<T>(T[] source)
                {
                    var result = new T[source.Length];
                    for (var index = 0; index < source.Length; index++)
                    {
                        result[index] = source[index];
                    }
                    return result;
                }

                public static T[] Wrap<T>(T value) => new[] { value };

                public static void Fill<T>(T[] target, T value)
                {
                    for (var index = 0; index < target.Length; index++)
                    {
                        target[index] = value;
                    }
                }

                public static string[] Pair(string first, string second)
                {
                    var pair = new string[2];
                    pair[0] = first;
                    pair[1] = second;
                    return pair;
                }
            }
            """);

        var result = DataFlowAnalyzer.Analyze(dll);

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Contains("state budget", StringComparison.Ordinal));
        Assert.Equal([0], ReturnIndexes(result, "Fixture.Arrays.Copy("));
        Assert.Equal([0], ReturnIndexes(result, "Fixture.Arrays.Wrap("));
        Assert.Equal([0, 1], ReturnIndexes(result, "Fixture.Arrays.Pair("));
        // A void method has no return: the old drift left a stored value for `ret` to "return".
        Assert.DoesNotContain(result.MethodSummaries, summary => summary.Method.StartsWith("Fixture.Arrays.Fill(", StringComparison.Ordinal) && summary.ReturnParameterIndexes.Count > 0);
    }

    [Fact]
    public void Summaries_FunctionPointerCalls_PopTheirSignature()
    {
        // `calli` pops the call-site signature's arguments and the function pointer; modelled as
        // popping nothing, a loop of calls drifted the stack into the state budget.
        using var fixture = new TempDir();
        var dll = Emit(fixture.Path, "Pointers", """
            namespace Fixture;

            public static unsafe class Pointers
            {
                private static void Consume(string value) { }

                private static string Echo(string value) => value;

                public static string Loop(string value, int count)
                {
                    delegate*<string, void> consume = &Consume;
                    delegate*<string, string> echo = &Echo;
                    var current = value;
                    for (var index = 0; index < count; index++)
                    {
                        consume(current);
                        current = echo(current);
                    }
                    return value;
                }
            }
            """, allowUnsafe: true);

        var result = DataFlowAnalyzer.Analyze(dll);

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Contains("state budget", StringComparison.Ordinal));
        Assert.Equal([0], ReturnIndexes(result, "Fixture.Pointers.Loop("));
    }

    private static List<int> ReturnIndexes(DataFlowResult result, string methodPrefix) =>
        Assert.Single(result.MethodSummaries, summary => summary.SummaryKind == "AssemblyIL" && summary.Method.StartsWith(methodPrefix, StringComparison.Ordinal))
            .ReturnParameterIndexes.Order().ToList();

    private static List<DataFlowSlice> SlicesIn(DataFlowResult result, string assemblyFileName)
    {
        var nodesById = result.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        return result.Slices
            .Where(slice => nodesById.TryGetValue(slice.SinkId, out var sink) && sink.Properties.TryGetValue("assembly", out var assembly) && assembly == assemblyFileName)
            .ToList();
    }

    /// <summary>
    ///     <c>App/</c> with its project file and source, and its build output under
    ///     <c>App/bin/Debug/net10.0</c>: App and Helper (projects), Vendor and Codec (packages),
    ///     and <c>tools/Tool.dll</c>, which no deps.json names.
    /// </summary>
    private static TempDir BuildFixture()
    {
        var fixture = new TempDir();
        var project = Path.Combine(fixture.Path, "App");
        var output = Path.Combine(project, "bin", "Debug", "net10.0");
        Directory.CreateDirectory(Path.Combine(output, "tools"));
        File.WriteAllText(Path.Combine(project, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(project, "Program.cs"), "namespace App; public static class Entry { public static void Run() { } }");
        File.WriteAllText(Path.Combine(output, "App.deps.json"), DepsJson);
        var vendor = Emit(output, "Vendor", VendorSource);
        Emit(output, "Codec", CodecSource);
        Emit(output, "Helper", HelperSource);
        Emit(output, "App", AppSource, references: vendor);
        Emit(Path.Combine(output, "tools"), "Tool", ToolSource);
        return fixture;
    }

    private static string Emit(string outputDirectory, string assemblyName, string source, string? references = null, bool allowUnsafe = false)
    {
        var metadata = FrameworkReferences.Current.References.Select(entry => (MetadataReference)entry.Reference).ToList();
        if (references is not null) metadata.Add(MetadataReference.CreateFromFile(references));
        var compilation = CSharpCompilation.Create(assemblyName,
            [CSharpSyntaxTree.ParseText(source, path: $"{assemblyName}.cs")],
            metadata,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: allowUnsafe, nullableContextOptions: NullableContextOptions.Enable));
        var dllPath = Path.Combine(outputDirectory, $"{assemblyName}.dll");
        var emit = compilation.Emit(dllPath);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));
        return dllPath;
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir() => Directory.CreateDirectory(Path);

        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dosai-issue78-" + Guid.NewGuid().ToString("N"));

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
