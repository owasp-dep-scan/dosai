using System.Text;
using Depscan;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Dosai.Tests;

// Issue #98: a call in a .razor or .cshtml file exists only in the build. The Razor compiler turns
// a template into C# whose #line directives point back at it, and no source file holds that C#, so
// the source analysis never sees the call. The call graph leaves a source tree's bin/ to the source
// analysis (reading it whole lists nearly every .cs call site twice), except for the tree's own
// builds whose PDB names a template: those give the call sites placed in a template.
public sealed class Issue98RazorCallSiteTests
{
    private const string ProgramSource = """
        using System;

        namespace App;

        public static class Program
        {
            public static void Main(string[] args)
            {
                Console.WriteLine(Vendor.Json.Serialize(args.Length));
            }
        }
        """;

    private const int ProgramSerializeLine = 9;

    // The shape of Razor's output: statements mapped to the template by #line, the rest of the
    // class (here KeepAlive) not mapped, and a hidden region whose calls the PDB places nowhere.
    private const string TemplateGeneratedSource = """
        #pragma warning disable 1591
        namespace App.Components
        {
            #line hidden
            public partial class Template
            {
                private int currentCount;

                public string Render()
                {
                    System.GC.KeepAlive(this);
        #nullable restore
        #line {line} "{document}"
                    var json = Vendor.Json.Serialize(currentCount);
        #line default
        #line hidden
        #nullable disable
                    return json;
                }
            }
        }
        """;

    private const int TemplateSerializeLine = 10;

    private const string GeneratedDocument = "Microsoft.CodeAnalysis.Razor.Compiler/Microsoft.NET.Sdk.Razor.SourceGenerators.RazorSourceGenerator/Template.g.cs";

    private const string VendorSource = """
        namespace Vendor;

        public static class Json
        {
            public static string Serialize(object value) => value.ToString() ?? string.Empty;
        }
        """;

    private const string VendorDocument = "/_/src/Vendor/Shared/Widget.razor";

    private static readonly string CounterTemplate = Path.Combine("Components", "Pages", "Counter.razor");

    public static TheoryData<string, string> TemplateBuilds() => new()
    {
        { "{root}/", CounterTemplate },
        { "/_/", CounterTemplate },
        { "{root}/", Path.Combine("Pages", "Privacy.cshtml") },
        { "{root}/", Path.Combine("Components", "Layout", "NavMenu.Razor") }
    };

    [Theory]
    [MemberData(nameof(TemplateBuilds))]
    public void Methods_ACallInATemplateOfTheTreesBuild_IsListedAtItsTemplateLine(string documentRoot, string template)
    {
        using var fixture = BuildFixture(documentRoot, template);
        var slice = Depscan.Dosai.GetMethodsSlice(fixture.Path);

        var call = Assert.Single(slice.MethodCalls ?? [], call => call.CalledMethod == "Serialize" && call.FileName == Path.GetFileName(template));
        Assert.Equal((AnalysisEvidenceKind.AssemblyIlDirect, Path.Combine("src", "App", template), TemplateSerializeLine, 13), (call.EvidenceKind, call.Path, call.LineNumber, call.ColumnNumber));
        Assert.Contains(slice.CallGraph!.Edges, edge => edge.CalledMethodName == "Serialize" && edge.Path == call.Path && edge.CallLocation.FileName == Path.GetFileName(template) && edge.CallLocation.LineNumber == TemplateSerializeLine);
    }

    [Fact]
    public void Methods_TheTreesBuildUnderBin_AddsNothingOutsideItsTemplates()
    {
        using var fixture = BuildFixture("{root}/", CounterTemplate);
        var slice = Depscan.Dosai.GetMethodsSlice(fixture.Path);
        var calls = slice.MethodCalls ?? [];

        // Every IL row sits in the template; the unmapped KeepAlive call and the build's other
        // call sites (Program.cs, already read as source) are not repeated from IL.
        Assert.All(calls.Where(IsIlCall), call => Assert.Equal("Counter.razor", call.FileName));
        Assert.Single(calls, call => call.CalledMethod is { } called && called.Contains("Serialize", StringComparison.Ordinal) && call.Path == Path.Combine("src", "App", "Program.cs") && call.LineNumber == ProgramSerializeLine);
        Assert.DoesNotContain(calls, call => call.CallerNamespace == "Vendor");
        Assert.DoesNotContain(slice.CallGraph!.Nodes, node => node.Name == "KeepAlive");
        Assert.DoesNotContain(calls, call => call.FileName?.EndsWith(".g.cs", StringComparison.Ordinal) == true);
        AssertNoDanglingEdges(slice);
    }

    [Fact]
    public void Methods_ACallInAGeneratedDocument_IsNeverListedAsATemplateRow()
    {
        // No #line: the call sits in the generator's own document, which no file in the tree holds.
        using var fixture = BuildFixture("{root}/", CounterTemplate, mapTemplate: false);
        var slice = Depscan.Dosai.GetMethodsSlice(fixture.Path);

        Assert.DoesNotContain(slice.MethodCalls ?? [], IsIlCall);
        Assert.DoesNotContain(slice.CallGraph!.Edges, edge => edge.EvidenceKind == AnalysisEvidenceKind.AssemblyIlDirect);
        AssertNoDanglingEdges(slice);
    }

    [Fact]
    public void Methods_ACopyOfTheBuildOutsideBin_KeepsEveryCallOfTheBuild()
    {
        using var fixture = BuildFixture("{root}/", CounterTemplate);
        var output = Path.Combine(fixture.Path, "out");
        Directory.CreateDirectory(output);
        foreach (var file in Directory.EnumerateFiles(BinDirectory(fixture.Path)))
        {
            File.Copy(file, Path.Combine(output, Path.GetFileName(file)));
        }

        var slice = Depscan.Dosai.GetMethodsSlice(fixture.Path);
        var ilCalls = (slice.MethodCalls ?? []).Where(IsIlCall).ToList();

        // Issue #79's rows stay: the group is read whole, and the template row is still one row.
        Assert.Contains(ilCalls, call => call.CalledMethod == "Serialize" && call.FileName == "Program.cs" && call.LineNumber == ProgramSerializeLine);
        Assert.Single(ilCalls, call => call.CalledMethod == "Serialize" && call.FileName == "Counter.razor");
    }

    [Fact]
    public void Methods_ABuildUnderObj_IsNeverRead()
    {
        using var fixture = BuildFixture("{root}/", CounterTemplate, outputUnder: Path.Combine("obj", "Debug", "net10.0"));
        var slice = Depscan.Dosai.GetMethodsSlice(fixture.Path);

        Assert.DoesNotContain(slice.MethodCalls ?? [], IsIlCall);
    }

    [Fact]
    public void Methods_ABuildWhosePdbNamesADeletedTemplate_IsReportedAsStaleAndAddsNothing()
    {
        // Two stale documents: Counter.razor, deleted here, and the vendor assembly's Widget.razor.
        using var fixture = BuildFixture("{root}/", CounterTemplate);
        File.Delete(Path.Combine(fixture.Path, "src", "App", CounterTemplate));

        var slice = Depscan.Dosai.GetMethodsSlice(fixture.Path);

        Assert.DoesNotContain(slice.MethodCalls ?? [], IsIlCall);
        Assert.Contains(slice.Diagnostics ?? [], diagnostic => diagnostic.Contains("not in the tree (a stale build", StringComparison.Ordinal) && diagnostic.StartsWith("2 .razor/.cshtml", StringComparison.Ordinal));
    }

    [Fact]
    public void Methods_DebugAndReleaseBuildsSideBySide_AreBothRead()
    {
        // Distinct builds are distinct files (issue #83): each is read for its template calls.
        // The two put the call on different lines, as two builds of an edited template do.
        using var fixture = BuildFixture("{root}/", CounterTemplate);
        var release = Path.Combine(fixture.Path, "src", "App", "bin", "Release", "net10.0");
        Directory.CreateDirectory(release);
        var document = fixture.Path.Replace('\\', '/') + "/src/App/" + CounterTemplate.Replace('\\', '/');
        EmitAssembly(release, "App", [(TemplateGeneratedSource.Replace("{line}", "12", StringComparison.Ordinal).Replace("{document}", document, StringComparison.Ordinal), GeneratedDocument)], Path.Combine(BinDirectory(fixture.Path), "Vendor.dll"));

        var slice = Depscan.Dosai.GetMethodsSlice(fixture.Path);

        var rows = (slice.MethodCalls ?? []).Where(call => call.CalledMethod == "Serialize" && call.FileName == "Counter.razor").Select(call => call.LineNumber).Order().ToList();
        Assert.Equal([TemplateSerializeLine, 12], rows);
    }

    [Fact]
    public void Methods_ABuildWithAnEmbeddedPdb_IsReadForItsTemplateCalls()
    {
        using var fixture = BuildFixture("{root}/", CounterTemplate, pdb: DebugInformationFormat.Embedded);
        var slice = Depscan.Dosai.GetMethodsSlice(fixture.Path);

        var call = Assert.Single(slice.MethodCalls ?? [], call => call.CalledMethod == "Serialize" && call.FileName == "Counter.razor");
        Assert.Equal((Path.Combine("src", "App", CounterTemplate), TemplateSerializeLine), (call.Path, call.LineNumber));
    }

    [Fact]
    public void Methods_ABuildWithoutAPdb_AddsNothingAndDoesNotFail()
    {
        using var fixture = BuildFixture("{root}/", CounterTemplate);
        File.Delete(Path.Combine(BinDirectory(fixture.Path), "App.pdb"));

        var slice = Depscan.Dosai.GetMethodsSlice(fixture.Path);

        Assert.DoesNotContain(slice.MethodCalls ?? [], IsIlCall);
        Assert.Contains(slice.MethodCalls ?? [], call => call.CalledMethod is { } called && called.Contains("Serialize", StringComparison.Ordinal) && call.Path == Path.Combine("src", "App", "Program.cs"));
    }

    [Fact]
    public void Methods_ACodeBehindFile_IsListedOnceFromSource()
    {
        // Counter.razor.cs is a source file: its calls are source-analysed, and the build's copy
        // of them (a document that is not a template) is not added from IL.
        using var fixture = BuildFixture("{root}/", CounterTemplate, codeBehind: true);
        var slice = Depscan.Dosai.GetMethodsSlice(fixture.Path);

        var behind = Assert.Single(slice.MethodCalls ?? [], call => call.CalledMethod is { } called && called.Contains("Serialize", StringComparison.Ordinal) && call.FileName == "Counter.razor.cs");
        Assert.Equal(AnalysisEvidenceKind.SourceRoslynDirect, behind.EvidenceKind);
        Assert.Single(slice.MethodCalls!, call => call.CalledMethod == "Serialize" && call.FileName == "Counter.razor");
    }

    [Fact]
    public void Methods_StateMachinesOfTwoTemplates_NameTheirOwnCaller()
    {
        // Every Razor Pages view has a nested <ExecuteAsync>d__0 of its own; mapping a state
        // machine to its method by the simple type name sent the second view's calls to a
        // caller named after the nested type alone.
        using var fixture = new TempDir();
        var project = Path.Combine(fixture.Path, "src", "App");
        var bin = BinDirectory(fixture.Path);
        Directory.CreateDirectory(Path.Combine(project, "Pages"));
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(project, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(project, "Program.cs"), "namespace App; public static class Program { public static void Main() { } }");
        var root = fixture.Path.Replace('\\', '/');
        foreach (var view in new[] { "First", "Second" })
        {
            File.WriteAllText(Path.Combine(project, "Pages", $"{view}.cshtml"), "<p>@Vendor.Json.Serialize(1)</p>");
        }

        var generated = $$"""
            namespace App.Pages
            {
                public class First_cshtml
                {
                    public async System.Threading.Tasks.Task ExecuteAsync()
                    {
                        await System.Threading.Tasks.Task.Yield();
            #line 3 "{{root}}/src/App/Pages/First.cshtml"
                        Vendor.Json.Serialize(1);
            #line default
                    }
                }

                public class Second_cshtml
                {
                    public async System.Threading.Tasks.Task ExecuteAsync()
                    {
                        await System.Threading.Tasks.Task.Yield();
            #line 4 "{{root}}/src/App/Pages/Second.cshtml"
                        Vendor.Json.Serialize(2);
            #line default
                    }
                }
            }
            """;
        var vendor = EmitAssembly(bin, "Vendor", [(VendorSource, VendorDocument)], vendorReference: null);
        EmitAssembly(bin, "App", [(generated, GeneratedDocument)], vendor);

        var slice = Depscan.Dosai.GetMethodsSlice(fixture.Path);

        var rows = (slice.MethodCalls ?? []).Where(call => call.CalledMethod == "Serialize" && call.FileName is "First.cshtml" or "Second.cshtml").OrderBy(call => call.FileName, StringComparer.Ordinal).ToList();
        Assert.Equal(["First_cshtml", "Second_cshtml"], rows.Select(row => row.CallerClass));
        Assert.All(rows, row => Assert.Equal("ExecuteAsync", row.CallerMethod));
        Assert.All(rows, row => Assert.StartsWith("App.Pages.", row.SourceId, StringComparison.Ordinal));
        AssertNoDanglingEdges(slice);
    }

    [Theory]
    [InlineData("Lib", "App")]
    [InlineData("Zz", "Aa")]
    public void Methods_ATemplateLibraryCopiedIntoAnApp_GivesItsTemplateCallsOnce(string library, string app)
    {
        // A component library's assembly is copied into the app's bin/ (issue #83): the copies
        // collapse to one analyzed file whichever directory sorts first (issue #111).
        using var fixture = new TempDir();
        var root = fixture.Path.Replace('\\', '/');
        foreach (var project in new[] { library, app })
        {
            Directory.CreateDirectory(Path.Combine(fixture.Path, project));
            File.WriteAllText(Path.Combine(fixture.Path, project, $"{project}.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk.Razor\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        }

        Directory.CreateDirectory(Path.Combine(fixture.Path, library, "Shared"));
        File.WriteAllText(Path.Combine(fixture.Path, library, "Shared", "Widget.razor"), "<p>@Vendor.Json.Serialize(1)</p>");
        File.WriteAllText(Path.Combine(fixture.Path, app, "Program.cs"), "public static class Program { public static void Main() { } }");
        var libBin = Path.Combine(fixture.Path, library, "bin", "Debug", "net10.0");
        var appBin = Path.Combine(fixture.Path, app, "bin", "Debug", "net10.0");
        Directory.CreateDirectory(libBin);
        Directory.CreateDirectory(appBin);
        var vendor = EmitAssembly(libBin, "Vendor", [(VendorSource, VendorDocument)], vendorReference: null);
        var widget = $$"""
            namespace {{library}}
            {
                public class Widget
                {
                    public string Render()
                    {
            #line 1 "{{root}}/{{library}}/Shared/Widget.razor"
                        return Vendor.Json.Serialize(1);
            #line default
                    }
                }
            }
            """;
        var dll = EmitAssembly(libBin, library, [(widget, GeneratedDocument)], vendor);
        foreach (var file in new[] { dll, Path.ChangeExtension(dll, ".pdb"), vendor, Path.ChangeExtension(vendor, ".pdb") })
        {
            File.Copy(file, Path.Combine(appBin, Path.GetFileName(file)));
        }

        var slice = Depscan.Dosai.GetMethodsSlice(fixture.Path);

        var row = Assert.Single(slice.MethodCalls ?? [], call => call.CalledMethod == "Serialize" && call.FileName == "Widget.razor");
        Assert.Equal(Path.Combine(library, "Shared", "Widget.razor"), row.Path);
        Assert.Equal(1, row.LineNumber);
        AssertNoDanglingEdges(slice);
    }

    [Fact]
    public void Methods_TemplateCallSites_AreIdenticalForEveryWorkerCount()
    {
        using var fixture = BuildFixture("{root}/", CounterTemplate, codeBehind: true);
        var previous = Depscan.Dosai.MaxSymbolAnalysisWorkers;
        try
        {
            Depscan.Dosai.MaxSymbolAnalysisWorkers = 1;
            var single = Strip(Depscan.Dosai.GetMethods(fixture.Path));
            Depscan.Dosai.MaxSymbolAnalysisWorkers = 4;
            Assert.Equal(single, Strip(Depscan.Dosai.GetMethods(fixture.Path)));
        }
        finally
        {
            Depscan.Dosai.MaxSymbolAnalysisWorkers = previous;
        }

        static string Strip(string json) => System.Text.RegularExpressions.Regex.Replace(json, "\"GeneratedAt\":\"[^\"]*\"", "\"GeneratedAt\":\"\"");
    }

    [Theory]
    [InlineData("Components/Pages/Counter.razor", true)]
    [InlineData("Pages/Privacy.cshtml", true)]
    [InlineData("Components/Layout/NavMenu.Razor", true)]
    [InlineData("Components/Pages/Counter.razor.cs", false)]
    [InlineData("Components/Pages/Counter.razor.css", false)]
    [InlineData("obj/Debug/net10.0/Counter_razor.g.cs", false)]
    [InlineData("Program.cs", false)]
    [InlineData(null, false)]
    public void IsTemplate_MatchesRazorAndCshtmlFilesOnly(string? path, bool expected) =>
        Assert.Equal(expected, RazorBuilds.IsTemplate(path));

    private static void AssertNoDanglingEdges(MethodsSlice slice)
    {
        var nodeIds = slice.CallGraph!.Nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        Assert.All(slice.CallGraph.Edges, edge => Assert.True(nodeIds.Contains(edge.SourceId) && nodeIds.Contains(edge.TargetId), $"{edge.SourceId} -> {edge.TargetId}"));
    }

    private static bool IsIlCall(MethodCalls call) => call.EvidenceKind is AnalysisEvidenceKind.AssemblyIlDirect or AnalysisEvidenceKind.AssemblyIlDelegateTarget or AnalysisEvidenceKind.AssemblyIlVirtualCandidate or AnalysisEvidenceKind.AssemblyIlGeneratedState;

    private static string BinDirectory(string root) => Path.Combine(root, "src", "App", "bin", "Debug", "net10.0");

    // documentRoot "{root}/" names the PDB's documents by their path in the tree, "/_/" as a deterministic build does.
    private static TempDir BuildFixture(string documentRoot, string template, bool mapTemplate = true, string? outputUnder = null, DebugInformationFormat pdb = DebugInformationFormat.PortablePdb, bool codeBehind = false)
    {
        var fixture = new TempDir();
        var project = Path.Combine(fixture.Path, "src", "App");
        var templateFile = Path.Combine(project, template);
        var bin = outputUnder is null ? BinDirectory(fixture.Path) : Path.Combine(project, outputUnder);
        Directory.CreateDirectory(Path.GetDirectoryName(templateFile)!);
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(project, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(project, "Program.cs"), ProgramSource);
        File.WriteAllText(templateFile, "<p>@Vendor.Json.Serialize(currentCount)</p>");

        var documents = documentRoot.Replace("{root}", fixture.Path.Replace('\\', '/'), StringComparison.Ordinal) + "src/App/";
        var generated = TemplateGeneratedSource
            .Replace("{line}", TemplateSerializeLine.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{document}", documents + template.Replace('\\', '/'), StringComparison.Ordinal);
        if (!mapTemplate)
        {
            generated = generated.Replace("#line " + TemplateSerializeLine.ToString(System.Globalization.CultureInfo.InvariantCulture) + " \"" + documents + template.Replace('\\', '/') + "\"", string.Empty, StringComparison.Ordinal);
        }

        var sources = new List<(string Source, string Document)>
        {
            (ProgramSource, documents + "Program.cs"),
            (generated, GeneratedDocument)
        };
        if (codeBehind)
        {
            // The same call, in the template's code-behind file, which the tree holds as source.
            const string behindSource = """
                namespace App.Components
                {
                    public partial class Template
                    {
                        public string Behind() => Vendor.Json.Serialize(1);
                    }
                }
                """;
            File.WriteAllText(templateFile + ".cs", behindSource);
            sources.Add((behindSource, documents + template.Replace('\\', '/') + ".cs"));
        }

        var vendor = EmitAssembly(bin, "Vendor", [(VendorSource, VendorDocument)], vendorReference: null);
        EmitAssembly(bin, "App", sources, vendor, pdb);
        return fixture;
    }

    private static string EmitAssembly(string outputDirectory, string assemblyName, IEnumerable<(string Source, string Document)> sources, string? vendorReference, DebugInformationFormat pdbFormat = DebugInformationFormat.PortablePdb)
    {
        var metadata = FrameworkReferences.Current.References.Select(entry => (MetadataReference)entry.Reference).ToList();
        if (vendorReference is not null) metadata.Add(MetadataReference.CreateFromFile(vendorReference));
        var compilation = CSharpCompilation.Create(assemblyName,
            sources.Select(source => CSharpSyntaxTree.ParseText(SourceText.From(source.Source, Encoding.UTF8), path: source.Document)),
            metadata,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        var dllPath = Path.Combine(outputDirectory, $"{assemblyName}.dll");
        using (var dll = File.Create(dllPath))
        {
            var options = new EmitOptions(debugInformationFormat: pdbFormat);
            Microsoft.CodeAnalysis.Emit.EmitResult emit;
            if (pdbFormat == DebugInformationFormat.Embedded)
            {
                emit = compilation.Emit(dll, options: options);
            }
            else
            {
                using var pdb = File.Create(Path.ChangeExtension(dllPath, ".pdb"));
                emit = compilation.Emit(dll, pdb, options: options);
            }

            Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));
        }

        return dllPath;
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir() => Directory.CreateDirectory(Path);

        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dosai-issue98-" + Guid.NewGuid().ToString("N"));

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
