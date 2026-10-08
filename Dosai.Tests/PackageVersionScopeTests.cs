using System.Text.Json;
using Depscan;
using Xunit;

namespace Dosai.Tests;

// Issue #72: two projects restoring two versions of one package. Each project's records must
// carry the version that project restores, and the report must say where versions split.
public partial class DosaiTests
{
    /// <summary>A project file and its restore output, the way <c>dotnet restore</c> lays them out.</summary>
    private static void WriteRestoredProject(string root, string project, string? assetsDirectory = null, string? csprojVersion = null, params (string Name, string Version)[] packages)
    {
        var projectDirectory = Path.Combine(root, project);
        Directory.CreateDirectory(projectDirectory);
        var projectFile = Path.Combine(projectDirectory, $"{Path.GetFileName(project)}.csproj");
        var references = csprojVersion is null ? string.Empty : $"""<ItemGroup><PackageReference Include="{packages[0].Name}" Version="{csprojVersion}" /></ItemGroup>""";
        File.WriteAllText(projectFile, $"""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>{references}</Project>""");
        var obj = assetsDirectory is null ? Path.Combine(projectDirectory, "obj") : Path.Combine(root, assetsDirectory);
        Directory.CreateDirectory(obj);
        File.WriteAllText(Path.Combine(obj, "project.assets.json"), JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["version"] = 3,
            ["targets"] = new Dictionary<string, object>
            {
                ["net10.0"] = packages.ToDictionary(package => $"{package.Name}/{package.Version}", package => (object)new Dictionary<string, object>
                {
                    ["type"] = "package",
                    ["compile"] = new Dictionary<string, object> { [$"lib/net8.0/{package.Name}.dll"] = new Dictionary<string, object>() }
                })
            },
            ["libraries"] = packages.ToDictionary(package => $"{package.Name}/{package.Version}", package => (object)new Dictionary<string, object>
            {
                ["type"] = "package",
                ["path"] = $"{package.Name.ToLowerInvariant()}/{package.Version}"
            }),
            ["project"] = new Dictionary<string, object> { ["restore"] = new Dictionary<string, object> { ["projectPath"] = projectFile } }
        }));
    }

    [Fact]
    public void PackageUrlResolver_ResolvesEachProjectsOwnVersion()
    {
        using var tempDirectory = new TemporaryDirectory();
        var root = tempDirectory.Path;
        WriteRestoredProject(root, "ProjA", packages: [("Moq", "4.15.1"), ("Castle.Core", "4.4.0")]);
        WriteRestoredProject(root, "ProjB", csprojVersion: "4.17.6", packages: [("Moq", "4.18.0"), ("Castle.Core", "5.0.0")]);
        // An artifacts layout: the restore output lives outside the project and names it, and the
        // build output beside it belongs to the same project.
        WriteRestoredProject(root, "src/ProjC", assetsDirectory: "artifacts/obj/ProjC", packages: [("Moq", "4.20.72")]);
        Directory.CreateDirectory(Path.Combine(root, "artifacts", "bin", "ProjC", "debug"));
        File.WriteAllText(Path.Combine(root, "artifacts", "bin", "ProjC", "debug", "ProjC.deps.json"), """{"targets":{},"libraries":{"Moq/4.20.72":{"type":"package"},"Polly/8.4.2":{"type":"package"}}}""");
        // A project with no package sources of its own, nested in one that has them.
        Directory.CreateDirectory(Path.Combine(root, "ProjB", "Inner"));
        File.WriteAllText(Path.Combine(root, "ProjB", "Inner", "Inner.csproj"), "<Project />");

        var resolver = PackageUrlResolver.Create(root);

        Assert.Equal("pkg:nuget/Moq@4.15.1", resolver.Resolve(assembly: "Moq", location: "ProjA/Class1.cs"));
        Assert.Equal("pkg:nuget/Moq@4.18.0", resolver.Resolve(assembly: "Moq", location: "ProjB/Class1.cs"));
        Assert.Equal("pkg:nuget/Moq@4.18.0", resolver.Resolve(assembly: "Moq, Version=4.18.0.0", location: Path.Combine(root, "ProjB", "bin", "Debug", "net10.0", "ProjB.dll")));
        Assert.Equal("pkg:nuget/Moq@4.18.0", resolver.Resolve(symbol: "Moq.Mock`1..ctor()", location: "ProjB/Sub/Deep.cs"));
        Assert.Equal("pkg:nuget/Castle.Core@5.0.0", resolver.Resolve(namespaceName: "Castle.Core.Logging", location: "ProjB/Class1.cs"));
        Assert.Equal("pkg:nuget/Castle.Core@4.4.0", resolver.Resolve(namespaceName: "Castle.Core.Logging", location: "ProjA/Class1.cs"));
        Assert.Equal("pkg:nuget/Castle.Core@5.0.0", resolver.Resolve(symbol: "Castle.Core.Logging.ILogger.Info()", location: "ProjB/Class1.cs"));
        Assert.Equal("pkg:nuget/Moq@4.20.72", resolver.Resolve(assembly: "Moq", location: "src/ProjC/Class1.cs"));
        // No location, a file outside every project, or a project without package sources of
        // its own: the tree-wide answer, the first source in path order.
        Assert.Equal("pkg:nuget/Moq@4.15.1", resolver.Resolve(assembly: "Moq"));
        Assert.Equal("pkg:nuget/Moq@4.15.1", resolver.Resolve(assembly: "Moq", location: "tools/script.cs"));
        Assert.Equal("pkg:nuget/Moq@4.15.1", resolver.Resolve(assembly: "Moq", location: "ProjB/Inner/Class1.cs"));
        // A package only one project restores never resolves for another project with restore
        // output of its own: that project's closure does not have it (issue #82). A project
        // without one still takes the tree-wide answer.
        Assert.Null(resolver.Resolve(module: "Castle.Core.dll", location: "src/ProjC/Class1.cs"));
        Assert.Equal("pkg:nuget/Castle.Core@4.4.0", resolver.Resolve(module: "Castle.Core.dll", location: "ProjB/Inner/Class1.cs"));

        Assert.Contains("Package Moq resolves to 3 versions across projects: 4.15.1 (ProjA), 4.18.0 (ProjB), 4.20.72 (src/ProjC). Each project's records carry its own version; records outside those projects carry 4.15.1, and so does a call-graph node shared by them, unless every call site of the node sits in a project with a known package closure and those sites agree (the node then carries their answer).", resolver.VersionDiagnostics);
        Assert.Contains(resolver.VersionDiagnostics, line => line.StartsWith("Package Castle.Core resolves to 2 versions across projects: 4.4.0 (ProjA), 5.0.0 (ProjB).", StringComparison.Ordinal));
        Assert.Contains("PURL version ambiguity for Moq in ProjB: csproj says 4.17.6; keeping 4.18.0 from project.assets.json.", resolver.VersionDiagnostics);
        Assert.Equal(3, resolver.VersionDiagnostics.Count);
        Assert.DoesNotContain(resolver.VersionDiagnostics, line => line.Contains("outside any project", StringComparison.Ordinal));
        Assert.Equal("pkg:nuget/Polly@8.4.2", resolver.Resolve(assembly: "Polly", location: "src/ProjC/Class1.cs"));
        Assert.Contains(resolver.ResolutionFacts, fact => fact is { Name: "Polly", Source: "*.deps.json", Project: "src/ProjC" });
        Assert.All(resolver.VersionDiagnostics, line => Assert.Contains(line, resolver.Diagnostics));
        Assert.Contains(resolver.ResolutionFacts, fact => fact is { Name: "Moq", Version: "4.18.0", Project: "ProjB" });
        Assert.Contains(resolver.ResolutionFacts, fact => fact is { Name: "Moq", Version: "4.20.72", Project: "src/ProjC" });
    }

    [Fact]
    public void PackageUrlResolver_HasNoLastSegmentTruncationOrFirstSegmentAliases()
    {
        // Each of these named an unrelated package: a package's last segment taken as its name
        // (System.Console code as Serilog.Sinks.Console, Azure SDK code as a SqlClient
        // extension), an assembly display name cut at its last dot (Castle.Core as Castle), a
        // first namespace segment matched before the package the symbol really belongs to, and
        // the restore placeholder `_._` registered as an assembly.
        using var tempDirectory = new TemporaryDirectory();
        var root = tempDirectory.Path;
        WriteRestoredProject(root, "App", packages: [("Serilog", "3.1.1"), ("Serilog.Sinks.Console", "5.0.1"), ("Microsoft.Data.SqlClient.Extensions.Azure", "1.0.0"), ("Castle", "1.0.0"), ("Castle.Core", "5.1.1")]);
        var assets = Path.Combine(root, "App", "obj", "project.assets.json");
        File.WriteAllText(assets, File.ReadAllText(assets).Replace("lib/net8.0/Castle.dll", "lib/net8.0/_._", StringComparison.Ordinal));

        var resolver = PackageUrlResolver.Create(root);

        Assert.Equal("pkg:nuget/System.Console", resolver.Resolve(symbol: "System.Console.WriteLine(string):void", location: "App/Program.cs"));
        Assert.Null(resolver.Resolve(symbol: "Console.Out.Flush()", location: "App/Program.cs"));
        Assert.Null(resolver.Resolve(symbol: "Azure.Storage.Blobs.BlobClient.Upload()", namespaceName: "Azure.Storage.Blobs", location: "App/Program.cs"));
        Assert.Equal("pkg:nuget/Serilog.Sinks.Console@5.0.1", resolver.Resolve(symbol: "Serilog.Sinks.Console.ConsoleSink.Emit()", location: "App/Program.cs"));
        Assert.Equal("pkg:nuget/Serilog@3.1.1", resolver.Resolve(symbol: "Serilog.Log.Information(string):void", location: "App/Program.cs"));
        Assert.Equal("pkg:nuget/Castle.Core@5.1.1", resolver.Resolve(assembly: "Castle.Core, Version=5.1.1.0, Culture=neutral", location: "App/Program.cs"));
        Assert.Equal("pkg:nuget/Castle.Core@5.1.1", resolver.Resolve(module: "Castle.Core.dll", location: "App/Program.cs"));
        Assert.Null(resolver.Resolve(module: "_._", location: "App/Program.cs"));
        Assert.Null(resolver.Resolve(module: "_", location: "App/Program.cs"));
    }

    [Theory]
    [InlineData("$(MoqVersion)")]
    [InlineData("[4.0,5.0)")]
    [InlineData("4.*")]
    public void PackageUrlResolver_ProjectReferenceWithoutALiteralVersionIsVersionless(string version)
    {
        // Before #72 a project's own sources rarely decided its records' purls; now they do, and
        // a version expression must not become a purl version.
        using var tempDirectory = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(tempDirectory.Path, "App"));
        File.WriteAllText(Path.Combine(tempDirectory.Path, "App", "App.csproj"), $"""<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="Moq" Version="{version}" /></ItemGroup></Project>""");

        var resolver = PackageUrlResolver.Create(tempDirectory.Path);

        Assert.Equal("pkg:nuget/Moq", resolver.Resolve(assembly: "Moq", location: "App/Program.cs"));
        Assert.Contains(resolver.ResolutionFacts, fact => fact is { Name: "Moq", Version: "", Confidence: "low", Project: "App" });
    }

    [Fact]
    public void GetMethods_TwoProjectsOnTwoVersionsOfAPackage_EachKeepsItsVersionAndTheReportSaysSo()
    {
        using var tempDirectory = new TemporaryDirectory();
        var root = tempDirectory.Path;
        WriteRestoredProject(root, "ProjA", packages: [("Moq", "4.15.1")]);
        WriteRestoredProject(root, "ProjB", packages: [("Moq", "4.18.0")]);
        foreach (var (project, value) in new[] { ("A", 1), ("B", 2) })
        {
            File.WriteAllText(Path.Combine(root, $"Proj{project}", "Class1.cs"), $$"""
                using Moq;

                public interface IClock{{project}} { int Now(); }

                public static class Project{{project}}
                {
                    public static int Run()
                    {
                        var mock = new Mock<IClock{{project}}>();
                        mock.Setup(c => c.Now()).Returns({{value}});
                        return mock.Object.Now();
                    }
                }
                """);
        }

        var slice = Depscan.Dosai.GetMethodsSlice(root);

        // Paths are relative to the scan root with the platform's separator.
        static string Portable(string? path) => (path ?? string.Empty).Replace('\\', '/');
        Assert.Equal("pkg:nuget/Moq@4.15.1", Assert.Single(slice.Dependencies!, dependency => dependency.Name == "Moq" && Portable(dependency.Path) == "ProjA/Class1.cs").Purl);
        Assert.Equal("pkg:nuget/Moq@4.18.0", Assert.Single(slice.Dependencies!, dependency => dependency.Name == "Moq" && Portable(dependency.Path) == "ProjB/Class1.cs").Purl);
        var older = Assert.Single(slice.PackageReachability!, package => package.Purl == "pkg:nuget/Moq@4.15.1");
        var newer = Assert.Single(slice.PackageReachability!, package => package.Purl == "pkg:nuget/Moq@4.18.0");
        Assert.NotEmpty(older.SourceLocations);
        Assert.NotEmpty(newer.SourceLocations);
        Assert.All(older.SourceLocations, location => Assert.StartsWith("ProjA/", Portable(location.Path), StringComparison.Ordinal));
        Assert.All(newer.SourceLocations, location => Assert.StartsWith("ProjB/", Portable(location.Path), StringComparison.Ordinal));
        Assert.Contains(slice.Diagnostics!, line => line.StartsWith("Package Moq resolves to 2 versions across projects: 4.15.1 (ProjA), 4.18.0 (ProjB).", StringComparison.Ordinal));
    }
}
