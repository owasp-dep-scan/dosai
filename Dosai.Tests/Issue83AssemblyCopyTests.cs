using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using Depscan;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Dosai.Tests;

// Issue #83: a build copies each referenced project's assembly into the output of every project
// that references it, and the data-flow IL pass (also run by crypto) analyzed every copy, so a
// library referenced by an app and its test project reported each flow three times, with nodes
// that differed only in their id. Byte-identical copies are now analyzed once.
public sealed class Issue83AssemblyCopyTests
{
    private const string StoreSource = """
        using System.IO;

        namespace Lib;

        public static class Store
        {
            public static string Load(string request)
            {
                return File.ReadAllText(request);
            }

            public static string Banner() => "tamper-me";
        }
        """;

    private const string VendorSource = """
        namespace Vendor;

        public static class Shell
        {
            public static void Run(string request)
            {
                System.Diagnostics.Process.Start(request);
            }
        }
        """;

    private static readonly string[] Outputs = ["Lib/bin/Debug/net8.0", "App/bin/Debug/net8.0", "App.Tests/bin/Debug/net8.0"];

    // Store with static data: a ReadyToRun compiler moves the field data as it moves the IL.
    private const string StoreWithDataSource = """
        using System;
        using System.IO;

        namespace Lib;

        public static class Store
        {
            public static string Load(string request)
            {
                return File.ReadAllText(request);
            }

            public static int Checksum()
            {
                ReadOnlySpan<byte> key = new byte[] { 3, 1, 4, 1, 5, 9, 2, 6, 5, 3 };
                var sum = 0;
                foreach (var value in key)
                {
                    sum += value;
                }

                return sum;
            }
        }
        """;

    [Fact]
    public void DataFlows_ByteIdenticalCopiesOfAProjectAssembly_ReportEachFlowOnce()
    {
        using var fixture = new TempDir();
        WriteIssueTree(fixture.Path);

        var result = DataFlowAnalyzer.Analyze(fixture.Path);

        var slice = Assert.Single(IlSlices(result));
        Assert.Equal(("request", "ReadAllText"), (Node(result, slice.SourceId).Name, Node(result, slice.SinkId).Name));
        Assert.Single(result.Nodes, node => node is { IsSource: true, Name: "request" } && node.Properties.GetValueOrDefault("analysis") == "assembly-il");
        // One source file and one of the three assemblies.
        Assert.Equal(2, result.Statistics.FilesAnalyzed);
        Assert.Contains("2 assembly file(s) are byte-identical copies of another file in the tree and were not analyzed again, so each flow through them is reported once: Lib.dll analyzed in Lib/bin/Debug/net8.0 (copies in App/bin/Debug/net8.0, App.Tests/bin/Debug/net8.0).", result.Diagnostics);

        // crypto's data flows run the same pass.
        var crypto = CryptoAnalyzer.Analyze(fixture.Path).CryptoDataFlows;
        Assert.NotNull(crypto);
        Assert.Single(IlSlices(crypto));
    }

    [Fact]
    public void Methods_ByteIdenticalCopiesOfAProjectAssembly_ReportEachCallOnce()
    {
        // Binaries only (no source file), so the methods IL pass reads bin/: the review of #83
        // found three identical ReadAllText call records there, one per copy.
        using var fixture = new TempDir();
        WriteIssueTree(fixture.Path);
        File.Delete(Path.Combine(fixture.Path, "Lib", "Store.cs"));

        var slice = Depscan.Dosai.GetMethodsSlice(fixture.Path);

        var readAllText = Assert.Single(slice.MethodCalls ?? [], call => call.CalledMethod == "ReadAllText");
        Assert.Equal(AnalysisEvidenceKind.AssemblyIlDirect, readAllText.EvidenceKind);
        Assert.Equal("Load", readAllText.CallerMethod);
        // The inventory read one copy (the first it met), and the call graph analyzed the
        // ranked one: the caller still takes the inventory's identity, so there is one node.
        var load = Assert.Single(slice.Methods ?? [], method => method.Name == "Load");
        Assert.Contains(slice.CallGraph?.Edges ?? [], edge => edge.SourceId == load.AssemblySignature && edge.TargetId.Contains("ReadAllText", StringComparison.Ordinal));
        Assert.Single(slice.CallGraph?.Nodes ?? [], node => node.Name == "Load");
        Assert.Contains("2 assembly file(s) are byte-identical copies of another file in the tree and were not analyzed again, so each call in them is reported once: Lib.dll analyzed in Lib/bin/Debug/net8.0 (copies in App/bin/Debug/net8.0, App.Tests/bin/Debug/net8.0).", slice.Diagnostics ?? []);
    }

    [Fact]
    public void AssemblyCopies_AnalyzedCopyDependsOnThePathsAloneNeverOnTheListingOrder()
    {
        using var fixture = new TempDir();
        var copies = WriteIssueTree(fixture.Path);
        var reversed = copies.AsEnumerable().Reverse().ToList();

        // The project's own output, with its PDB, whatever order the files came in.
        foreach (var order in new[] { copies, reversed })
        {
            var collapsed = AssemblyCopies.Collapse(fixture.Path, order);
            var group = Assert.Single(collapsed.Groups);
            Assert.Equal(copies[0], group.Analyzed);
            Assert.Equal([copies[1], copies[2]], group.Copies);
            Assert.Equal([copies[0]], collapsed.Distinct);
        }

        // A copy without a PDB beside it is never the one analyzed (the pass reads its source
        // locations from there); among the others, tree order puts App/ before App.Tests/.
        File.Delete(Path.ChangeExtension(copies[0], ".pdb"));
        foreach (var order in new[] { copies, reversed })
        {
            var group = Assert.Single(AssemblyCopies.Collapse(fixture.Path, order).Groups);
            Assert.Equal(copies[1], group.Analyzed);
            Assert.Equal([copies[2], copies[0]], group.Copies);
        }

        Assert.True(AssemblyCopies.TreeOrder.Instance.Compare(Path.Combine("x", "App", "a.dll"), Path.Combine("x", "App.Tests", "a.dll")) < 0);
        Assert.True(AssemblyCopies.TreeOrder.Instance.Compare(Path.Combine("x", "App"), Path.Combine("x", "App", "a.dll")) < 0);
    }

    [Fact]
    public void DataFlows_ACopyWithTheSameModuleIdButOtherBytes_IsStillAnalyzed()
    {
        // A patched or tampered file keeps the module version id and, here, the length; only the
        // content decides that two files are one assembly.
        using var fixture = new TempDir();
        var copies = WriteIssueTree(fixture.Path);
        var bytes = File.ReadAllBytes(copies[2]);
        var original = Encoding.Unicode.GetBytes("tamper-me");
        var offset = bytes.AsSpan().IndexOf(original);
        Assert.True(offset > 0);
        Encoding.Unicode.GetBytes("tamper-it").CopyTo(bytes, offset);
        File.WriteAllBytes(copies[2], bytes);
        Assert.Equal(new FileInfo(copies[0]).Length, new FileInfo(copies[2]).Length);
        Assert.Equal(ModuleVersionId(copies[0]), ModuleVersionId(copies[2]));

        var result = DataFlowAnalyzer.Analyze(fixture.Path);

        Assert.Equal(2, IlSlices(result).Count);
        Assert.Contains("1 assembly file(s) are byte-identical copies of another file in the tree and were not analyzed again, so each flow through them is reported once: Lib.dll analyzed in Lib/bin/Debug/net8.0 (copies in App/bin/Debug/net8.0).", result.Diagnostics);
    }

    [Fact]
    public void DataFlows_AReadyToRunImageOfABuild_IsAnalyzedOnceWithItsIlBuild()
    {
        // Review of #83: a ReadyToRun publish holds the same metadata and IL as the build it was
        // compiled from, module version id included, in a file of other bytes and length, so
        // the publish folder reported every flow once more.
        using var fixture = new TempDir();
        var build = WriteReleaseBuild(fixture.Path);
        var publish = Path.Combine(fixture.Path, "App", "publish", "Lib.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(publish)!);
        ReadyToRunShapedImage.Write(build, publish);
        File.Copy(Path.ChangeExtension(build, ".pdb"), Path.ChangeExtension(publish, ".pdb"));
        Assert.NotEqual(new FileInfo(build).Length, new FileInfo(publish).Length);
        Assert.Equal(ModuleVersionId(build), ModuleVersionId(publish));

        var result = DataFlowAnalyzer.Analyze(fixture.Path);

        var slice = Assert.Single(IlSlices(result));
        Assert.Equal(("request", "ReadAllText"), (Node(result, slice.SourceId).Name, Node(result, slice.SinkId).Name));
        Assert.Contains("1 assembly file(s) are copies of another file in the tree, byte-identical or with the same metadata and IL (such as a ReadyToRun image of the same build), and were not analyzed again, so each flow through them is reported once: Lib.dll analyzed in Lib/bin/Release/net8.0 (same IL in App/publish).", result.Diagnostics);
    }

    [Fact]
    public void AssemblyCopies_ReadyToRunImages_CollapseOnlyOnTheSameIlAndYieldToTheIlBuild()
    {
        using var fixture = new TempDir();
        // No project builds it, so neither file is a project's own output.
        var build = WriteReleaseBuild(fixture.Path, withProject: false);
        File.Delete(Path.ChangeExtension(build, ".pdb"));
        // Tree order puts every image before the build; the IL-only file is still the one analyzed.
        var image = Path.Combine(fixture.Path, "A", "Lib.dll");
        var otherData = Path.Combine(fixture.Path, "B", "Lib.dll");
        var mixedMode = Path.Combine(fixture.Path, "C", "Lib.dll");
        foreach (var path in new[] { image, otherData, mixedMode })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        }

        ReadyToRunShapedImage.Write(build, image);
        // The same IL with other static data is another assembly.
        ReadyToRunShapedImage.Write(build, otherData, patchFieldData: data => data[0] ^= 0xFF);
        // Not IL-only and no ReadyToRun header: native code of its own, never compared by IL.
        ReadyToRunShapedImage.Write(build, mixedMode, readyToRunHeader: false);

        foreach (var order in new[] { new List<string> { build, image, otherData, mixedMode }, [mixedMode, otherData, image, build] })
        {
            var collapsed = AssemblyCopies.Collapse(fixture.Path, order);
            var group = Assert.Single(collapsed.Groups);
            Assert.Equal(build, group.Analyzed);
            Assert.Equal([image], group.Copies);
            Assert.Equal([image], group.SameIlCopies);
            Assert.Equal(3, collapsed.Distinct.Count);
        }
    }

    [Theory]
    [InlineData("reference", "dependency")]
    [InlineData("project", null)]
    public void DataFlows_CopiesOfOneAssembly_AreClassifiedTogether(string libraryType, string? expectedScope)
    {
        // The analyzed copy (it alone has a PDB) is a loose file nothing names, which on its own is
        // the application's; its copy in a build output is what that output's deps.json says.
        using var fixture = new TempDir();
        var libs = Path.Combine(fixture.Path, "libs");
        var bin = Path.Combine(fixture.Path, "App", "bin", "Debug", "net8.0");
        Directory.CreateDirectory(libs);
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(fixture.Path, "App", "App.csproj"), """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>""");
        var vendor = Emit(libs, "Vendor", VendorSource, "/_/vendor/Shell.cs");
        File.Copy(vendor, Path.Combine(bin, "Vendor.dll"));
        File.WriteAllText(Path.Combine(bin, "App.deps.json"), """
            {"targets":{".NETCoreApp,Version=v8.0":{"App/1.0.0":{"runtime":{"App.dll":{}}},"Vendor/1.0.0.0":{"runtime":{"Vendor.dll":{}}}}},
             "libraries":{"App/1.0.0":{"type":"project"},"Vendor/1.0.0.0":{"type":"LIBRARY_TYPE"}}}
            """.Replace("LIBRARY_TYPE", libraryType, StringComparison.Ordinal));

        var result = DataFlowAnalyzer.Analyze(fixture.Path);

        var slice = Assert.Single(IlSlices(result));
        Assert.Equal(expectedScope, slice.Scope);
        Assert.Contains(result.Diagnostics, line => line.Contains("Vendor.dll analyzed in libs (copies in App/bin/Debug/net8.0)", StringComparison.Ordinal));
    }

    // Review of #83: with copies collapsed, flows the old output found only because a later
    // copy's pass saw what an earlier one recorded must come from the pass itself, in every
    // analysis order. Binaries only, without PDBs: the files sort so the caller comes first.
    private const string CalleeSource = """
        namespace Zz.Callee;

        public static class Store
        {
            public static string Load(string path) => System.IO.File.ReadAllText(path);
        }
        """;

    private const string CallerSource = """
        namespace Aa.Caller;

        public static class Program
        {
            public static void Main(string[] args) => System.Console.WriteLine(Zz.Callee.Store.Load(args[0]));
        }
        """;

    // The reader is declared, so analyzed, before the method that stores the field.
    private const string FieldSource = """
        namespace Aa.Fields;

        public static class Report
        {
            private static string _path = "report.txt";

            public static string Read() => System.IO.File.ReadAllText(_path);

            public static void Configure() => _path = System.Console.ReadLine()!;
        }
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DataFlows_ACallIntoAnAssemblyAnalyzedLater_UsesItsSummary(bool withCopies)
    {
        using var fixture = new TempDir();
        var bin = Path.Combine(fixture.Path, "bin");
        Directory.CreateDirectory(bin);
        var callee = Issue76AssemblyLoadingTests.EmitAssembly(bin, "Zz.Callee", CalleeSource);
        Issue76AssemblyLoadingTests.EmitAssembly(bin, "Aa.Caller", CallerSource, callee);
        CopyInto(fixture.Path, bin, withCopies);

        var result = DataFlowAnalyzer.Analyze(fixture.Path);

        // Main's argument reaches File.ReadAllText through Load, once whatever the copies.
        var interprocedural = Assert.Single(IlSlices(result), slice => Node(result, slice.SinkId).Name == "Load");
        Assert.Equal("args", Node(result, interprocedural.SourceId).Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DataFlows_ACallThroughAChainOfAssemblies_ReachesTheSinkAtTheEnd(bool withCopies)
    {
        // A summary that depends on another assembly's summary: Middle.Forward's sink is the one
        // in Zz.Callee, so Middle settles after Zz.Callee and Aa.Caller after Middle, whatever the
        // order the files are listed in.
        using var fixture = new TempDir();
        var bin = Path.Combine(fixture.Path, "bin");
        Directory.CreateDirectory(bin);
        var callee = Issue76AssemblyLoadingTests.EmitAssembly(bin, "Zz.Callee", CalleeSource);
        var middle = Issue76AssemblyLoadingTests.EmitAssembly(bin, "Mm.Middle", """
            namespace Mm.Middle;
            public static class Relay
            {
                public static string Forward(string path) => Zz.Callee.Store.Load(path);
            }
            """, callee);
        Issue76AssemblyLoadingTests.EmitAssembly(bin, "Aa.Caller", """
            namespace Aa.Caller;
            public static class Program
            {
                public static void Main(string[] args) => System.Console.WriteLine(Mm.Middle.Relay.Forward(args[0]));
            }
            """, middle, callee);
        CopyInto(fixture.Path, bin, withCopies);

        var result = DataFlowAnalyzer.Analyze(fixture.Path);

        var interprocedural = Assert.Single(IlSlices(result), slice => Node(result, slice.SinkId).Name == "Forward");
        Assert.Equal("args", Node(result, interprocedural.SourceId).Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DataFlows_AFieldReadBeforeTheMethodThatStoresIt_StillCarriesTheTaint(bool withCopies)
    {
        using var fixture = new TempDir();
        var bin = Path.Combine(fixture.Path, "bin");
        Directory.CreateDirectory(bin);
        Issue76AssemblyLoadingTests.EmitAssembly(bin, "Aa.Fields", FieldSource);
        CopyInto(fixture.Path, bin, withCopies);

        var result = DataFlowAnalyzer.Analyze(fixture.Path);

        var slice = Assert.Single(IlSlices(result));
        Assert.Equal(("ReadLine", "ReadAllText"), (Node(result, slice.SourceId).Name, Node(result, slice.SinkId).Name));
    }

    [Fact]
    public void DataFlows_AFieldOfOneAssembly_NeverCarriesTheSameNamedFieldOfAnother()
    {
        // Every C# assembly declares <>c.<>9__0_0-style compiler fields, and field taints were
        // keyed by symbol alone, so a store in one package flowed into a reader in an unrelated
        // one. Two assemblies declaring Shared.Holder._value, one storing input, one reading it.
        using var fixture = new TempDir();
        var bin = Path.Combine(fixture.Path, "bin");
        Directory.CreateDirectory(bin);
        Issue76AssemblyLoadingTests.EmitAssembly(bin, "Writer", """
            namespace Shared;
            public static class Holder
            {
                private static string _value = "";
                public static void Store() => _value = System.Console.ReadLine()!;
            }
            """);
        Issue76AssemblyLoadingTests.EmitAssembly(bin, "Reader", """
            namespace Shared;
            public static class Holder
            {
                private static string _value = "";
                public static string Read() => System.IO.File.ReadAllText(_value);
            }
            """);

        var result = DataFlowAnalyzer.Analyze(fixture.Path);

        Assert.Empty(IlSlices(result));
    }

    /// <summary>With copies, the files in <paramref name="bin" /> also go to two other outputs that sort before it.</summary>
    private static void CopyInto(string root, string bin, bool withCopies)
    {
        if (!withCopies)
        {
            return;
        }

        foreach (var output in new[] { "A/bin", "B/bin" })
        {
            var directory = Path.Combine([root, .. output.Split('/')]);
            Directory.CreateDirectory(directory);
            foreach (var file in Directory.GetFiles(bin, "*.dll"))
            {
                File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
            }
        }
    }

    /// <summary>
    ///     The reporter's tree: the library's project and source, and its build copied into the
    ///     outputs of the app and the test project, each with its PDB. Returns the three copies.
    /// </summary>
    private static List<string> WriteIssueTree(string root)
    {
        foreach (var project in new[] { "Lib", "App", "App.Tests" })
        {
            Directory.CreateDirectory(Path.Combine(root, project));
            File.WriteAllText(Path.Combine(root, project, $"{project}.csproj"), """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>""");
        }

        File.WriteAllText(Path.Combine(root, "Lib", "Store.cs"), StoreSource);
        var copies = Outputs.Select(output => Path.Combine([root, .. output.Split('/'), "Lib.dll"])).ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(copies[0])!);
        Emit(Path.GetDirectoryName(copies[0])!, "Lib", StoreSource, "/_/Lib/Store.cs");
        foreach (var copy in copies.Skip(1))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(copies[0], copy);
            File.Copy(Path.ChangeExtension(copies[0], ".pdb"), Path.ChangeExtension(copy, ".pdb"));
        }

        return copies;
    }

    /// <summary>
    ///     The library built into <c>Lib/bin/Release/net8.0</c> with its PDB, from source with
    ///     static data, and the project that builds it unless <paramref name="withProject" /> is
    ///     false. Returns the assembly.
    /// </summary>
    private static string WriteReleaseBuild(string root, bool withProject = true)
    {
        var project = Path.Combine(root, "Lib");
        var output = Path.Combine(project, "bin", "Release", "net8.0");
        Directory.CreateDirectory(output);
        if (withProject)
        {
            File.WriteAllText(Path.Combine(project, "Lib.csproj"), """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>""");
            File.WriteAllText(Path.Combine(project, "Store.cs"), StoreWithDataSource);
        }

        var build = Emit(output, "Lib", StoreWithDataSource, "/_/Lib/Store.cs");
        using var stream = File.OpenRead(build);
        using var peReader = new PEReader(stream);
        Assert.True(peReader.GetMetadataReader().GetTableRowCount(TableIndex.FieldRva) > 0, "the fixture needs static data");
        return build;
    }

    private static List<DataFlowSlice> IlSlices(DataFlowResult result) =>
        result.Slices.Where(slice => slice.Summary?.StartsWith("Assembly IL", StringComparison.Ordinal) == true).ToList();

    private static DataFlowNode Node(DataFlowResult result, string? id) => result.Nodes.Single(node => node.Id == id);

    private static Guid ModuleVersionId(string path)
    {
        using var stream = File.OpenRead(path);
        using var peReader = new PEReader(stream);
        var reader = peReader.GetMetadataReader();
        return reader.GetGuid(reader.GetModuleDefinition().Mvid);
    }

    private static string Emit(string outputDirectory, string assemblyName, string source, string document)
    {
        var references = FrameworkReferences.Current.References.Select(entry => (MetadataReference)entry.Reference).ToList();
        var compilation = CSharpCompilation.Create(assemblyName,
            [CSharpSyntaxTree.ParseText(SourceText.From(source, Encoding.UTF8), path: document)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, deterministic: true));
        var dllPath = Path.Combine(outputDirectory, $"{assemblyName}.dll");
        using (var dll = File.Create(dllPath))
        using (var pdb = File.Create(Path.ChangeExtension(dllPath, ".pdb")))
        {
            var emit = compilation.Emit(dll, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));
            Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));
        }

        return dllPath;
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir() => Directory.CreateDirectory(Path);

        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dosai-issue83-" + Guid.NewGuid().ToString("N"));

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
