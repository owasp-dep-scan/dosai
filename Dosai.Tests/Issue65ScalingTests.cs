using Depscan;
using Depscan.Frameworks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Dosai.Tests;

/// <summary>
///     Issue #65 follow-ups on very large trees: the framework phase's keyword gates, and the
///     duplicate metadata copies of source-built assemblies that made binding depend on timing.
/// </summary>
public partial class DosaiTests
{
    [Fact]
    public void FrameworkContext_TextContainsAny_MatchesTheOrdinalContainsLoop()
    {
        // The vectorized search must answer exactly what the per-keyword string.Contains loop
        // answered, for every keyword list and text: ordinal, case-sensitive, any position,
        // keywords that overlap or prefix each other, and an empty keyword matching everything.
        var random = new Random(65);
        string[] alphabet = ["a", "b", "A", "(", ".", "<", "ab", "ba", "Ab", " ", "\n", "é", "İ", "ı"];
        string RandomText(int maxParts) => string.Concat(Enumerable.Range(0, random.Next(maxParts)).Select(_ => alphabet[random.Next(alphabet.Length)]));
        var texts = Enumerable.Range(0, 60).Select(_ => RandomText(40)).ToList();
        var trees = texts.Select((text, index) => CSharpSyntaxTree.ParseText($"/*{text}*/", path: $"f{index}.cs")).ToList();
        var context = FrameworkContext.FromCompilations(Path.GetTempPath(), CSharpCompilation.Create("Gates", trees), null, PackageUrlResolver.Create(Path.GetTempPath()));
        var lists = Enumerable.Range(0, 200).Select(_ => Enumerable.Range(0, 1 + random.Next(6)).Select(_ => RandomText(4)).ToArray()).ToList();
        lists.Add(["Function(", "Function"]);
        lists.Add([string.Empty]);
        lists.Add(["zzz", string.Empty]);
        foreach (var tree in trees)
        {
            var text = tree.ToString();
            foreach (var keywords in lists)
            {
                Assert.Equal(keywords.Any(keyword => text.Contains(keyword, StringComparison.Ordinal)), context.TextContainsAny(tree, keywords));
            }
        }
    }

    [Fact]
    public void FrameworkContext_TextFor_KeepsAnsweringByPath()
    {
        // Two trees under one path: the text is the first one asked for, as before the texts
        // were rendered up front.
        var first = CSharpSyntaxTree.ParseText("class First { }", path: "Same.cs");
        var second = CSharpSyntaxTree.ParseText("class Second { }", path: "Same.cs");
        var context = FrameworkContext.FromCompilations(Path.GetTempPath(), CSharpCompilation.Create("SamePath", [first, second]), null, PackageUrlResolver.Create(Path.GetTempPath()));
        Assert.Equal("class Second { }", context.TextFor(second));
        Assert.Equal("class Second { }", context.TextFor(first));
    }

    [Fact]
    public void Methods_MetadataCopyOfASourceBuiltAssembly_StaysOutOfTheCompilation()
    {
        // The tree holds Lib's project and source and, under bin/, Lib's own build output.
        // Referencing both made Lib's extension methods ambiguous between the source and the
        // metadata copy; Roslyn then settled each call by evaluation timing, so the call graph
        // changed with the worker count and between runs (OrchardCore's src: 3,829 such copies).
        using var fixture = new TemporaryDirectory();
        const string libSource = """
            namespace Lib65;

            public sealed class Builder
            {
                public Builder Add(string key) => this;
            }

            public static class BuilderExtensions
            {
                public static Builder AddDefaults(this Builder builder) => builder.Add("default");
                public static Builder AddNamed(this Builder builder, string name) => builder.Add(name);
            }
            """;
        var libDirectory = Path.Combine(fixture.Path, "Lib65");
        Directory.CreateDirectory(libDirectory);
        File.WriteAllText(Path.Combine(libDirectory, "Lib65.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(libDirectory, "Builder.cs"), libSource);
        var bin = Path.Combine(libDirectory, "bin", "Release", "net11.0");
        Directory.CreateDirectory(bin);
        Issue76AssemblyLoadingTests.EmitAssembly(bin, "Lib65", libSource);
        var appDirectory = Path.Combine(fixture.Path, "App65");
        Directory.CreateDirectory(appDirectory);
        File.WriteAllText(Path.Combine(appDirectory, "App65.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(appDirectory, "Program.cs"), """
            using Lib65;

            namespace App65;

            public static class Startup
            {
                public static void Configure()
                {
                    var builder = new Lib65.Builder();
                    builder.AddDefaults().AddNamed("one").AddDefaults();
                }
            }
            """);

        var slices = new[] { 1, 4 }.Select(workers => WithSymbolAnalysisWorkers(workers, () => Depscan.Dosai.GetMethodsSlice(fixture.Path))).ToList();
        foreach (var slice in slices)
        {
            var extensionCalls = slice.MethodCalls!.Where(call => call.FileName == "Program.cs" && call.CalledMethod?.Contains("BuilderExtensions.Add", StringComparison.Ordinal) == true).ToList();
            Assert.Equal(3, extensionCalls.Count);
            Assert.All(extensionCalls, call =>
            {
                Assert.Equal(AnalysisEvidenceKind.SourceRoslynDirect, call.EvidenceKind);
                Assert.StartsWith("Dosai.SourceAnalysis.CSharp", call.Assembly, StringComparison.Ordinal);
                Assert.True(call.IsInternal);
            });
            Assert.Contains(slice.Diagnostics, diagnostic => diagnostic.StartsWith("1 assembly reference(s) were left out of the source compilation", StringComparison.Ordinal));
        }

        Assert.Equal(
            System.Text.Json.JsonSerializer.Serialize(slices[0].MethodCalls),
            System.Text.Json.JsonSerializer.Serialize(slices[1].MethodCalls));
    }

    [Fact]
    public void TreeFrameworks_SourceAssemblyNames_UseAssemblyNameElseTheProjectFileName()
    {
        using var fixture = new TemporaryDirectory();
        void Project(string relative, string body)
        {
            var file = Path.Combine(fixture.Path, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, $"<Project Sdk=\"Microsoft.NET.Sdk\">{body}</Project>");
        }

        Project("a/Plain.csproj", "<PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup>");
        Project("b/Renamed.csproj", "<PropertyGroup><AssemblyName>Custom.Name</AssemblyName></PropertyGroup>");
        Project("c/Computed.csproj", "<PropertyGroup><AssemblyName>$(RootNamespace).Core</AssemblyName></PropertyGroup>");
        Project("d/Commented.csproj", "<!-- <PropertyGroup><AssemblyName>Ghost</AssemblyName></PropertyGroup> -->");
        Project("e/bin/Release/Built.csproj", string.Empty);
        Project("f/Vb.vbproj", string.Empty);

        var names = TreeFrameworks.SourceAssemblyNames(fixture.Path);
        Assert.Equal(["Commented", "Computed", "Custom.Name", "Plain", "Vb"], names.Order(StringComparer.Ordinal));
        Assert.True(TreeFrameworks.IsBuiltFromSource(fixture.Path, Path.Combine(fixture.Path, "x", "custom.name.dll")));
        Assert.False(TreeFrameworks.IsBuiltFromSource(fixture.Path, Path.Combine(fixture.Path, "x", "Renamed.dll")));
    }

    [SkippableFact]
    public void FrameworkReferences_PackAssemblyTheTreeBuildsFromSource_IsLeftOut()
    {
        // dotnet/runtime builds Microsoft.Extensions.Logging and friends from source and has a
        // web project, so the ASP.NET Core pack's copies used to sit beside the source.
        using var fixture = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(fixture.Path, "web"));
        File.WriteAllText(Path.Combine(fixture.Path, "web", "web.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>");
        Directory.CreateDirectory(Path.Combine(fixture.Path, "logging"));
        File.WriteAllText(Path.Combine(fixture.Path, "logging", "Microsoft.Extensions.Logging.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>");
        FrameworkReferences.ResetTreeCache();
        TreeFrameworks.ResetCache();
        var set = FrameworkReferences.ForTree(fixture.Path);
        Skip.IfNot(set.Source.Contains("Microsoft.AspNetCore.App", StringComparison.Ordinal), "no ASP.NET Core reference pack installed");
        Assert.DoesNotContain(set.References, reference => Path.GetFileName(reference.Key) == "Microsoft.Extensions.Logging.dll");
        Assert.Contains(set.References, reference => Path.GetFileName(reference.Key) == "Microsoft.Extensions.Logging.Abstractions.dll");
        Assert.Contains("left out because the tree builds it from source (Microsoft.Extensions.Logging)", set.Diagnostic, StringComparison.Ordinal);
    }
}
