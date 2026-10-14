using Depscan;
using Xunit;

namespace Depscan.Tests;

/// <summary>
///     File-based apps (<c>dotnet run app.cs</c>) have no project file: the SDK generates a
///     virtual project with <c>ImplicitUsings</c> enabled, its own target framework, and the
///     file's <c>#:sdk</c> and <c>#:property</c> directives applied, and restores the
///     <c>#:package</c> references in a per-user cache outside the tree. Without that model,
///     every call through an implicit using (<c>Console</c>, <c>List</c>, <c>File</c>, LINQ)
///     stayed unresolved, and a pinned package version never reached the purl.
/// </summary>
public class FileBasedAppTests
{
    private sealed class TempDir : IDisposable
    {
        public TempDir() => Directory.CreateDirectory(Path);

        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dosai-file-based-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static string Write(string directory, string name, string content)
    {
        Directory.CreateDirectory(directory);
        var path = System.IO.Path.Combine(directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    private const string App = """
#!/usr/bin/env -S dotnet --
#:package Humanizer@2.14.1
#:package System.CommandLine@2.0.1 Aliases=CommandLine
#:package Serilog@4.*
#:property Company="RC2 Validation Team"

var items = new List<string> { "a", "b" };
Console.WriteLine(items.Count);
await File.WriteAllTextAsync("out.txt", string.Join(",", items.Select(item => item.ToUpperInvariant())));
""";

    [Fact]
    public void GlobalUsings_FileBasedAppWithoutAProject_GetsItsVirtualProjectsUsings()
    {
        using var fixture = new TempDir();
        Write(fixture.Path, "app.cs", App);

        var decision = GlobalUsings.Resolve(fixture.Path);

        Assert.True(decision.Enabled);
        var text = decision.Tree!.GetText().ToString();
        Assert.Contains("global using System;", text, StringComparison.Ordinal);
        Assert.Contains("global using System.Linq;", text, StringComparison.Ordinal);
        // The SDK's own target is net11.0, whose base list adds System.Net.Http.Json.
        Assert.Contains("global using System.Net.Http.Json;", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Microsoft.AspNetCore", text, StringComparison.Ordinal);
    }

    [Fact]
    public void GlobalUsings_FileBasedAppDirectives_SelectTheSdkTargetAndImplicitUsings()
    {
        using var web = new TempDir();
        Write(web.Path, "api.cs", "#:sdk Microsoft.NET.Sdk.Web\nvar app = WebApplication.Create(args);\n");
        Assert.Contains("global using Microsoft.AspNetCore.Builder;", GlobalUsings.Resolve(web.Path).Tree!.GetText().ToString(), StringComparison.Ordinal);

        using var net10 = new TempDir();
        Write(net10.Path, "tool.cs", "#:property TargetFramework=net10.0\nConsole.WriteLine(1);\n");
        var net10Text = GlobalUsings.Resolve(net10.Path).Tree!.GetText().ToString();
        Assert.Contains("global using System;", net10Text, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Net.Http.Json", net10Text, StringComparison.Ordinal);

        using var disabled = new TempDir();
        Write(disabled.Path, "plain.cs", "#:property ImplicitUsings=disable\nSystem.Console.WriteLine(1);\n");
        Assert.False(GlobalUsings.Resolve(disabled.Path).Enabled);
    }

    [Fact]
    public void GlobalUsings_FileBasedAppDetection_NeedsAnEntryPointOutsideEveryProject()
    {
        // Top-level statements alone make an entry point; `dotnet run hello.cs` needs no directive.
        using var hello = new TempDir();
        Write(hello.Path, "hello.cs", "Console.WriteLine(\"hello\");\n");
        Assert.True(GlobalUsings.Resolve(hello.Path).Enabled);

        // A loose library file is no app: nothing to give usings to.
        using var library = new TempDir();
        Write(library.Path, "Helpers.cs", "namespace Lib; public static class Helpers { public static int Two() => 2; }\n");
        Assert.False(GlobalUsings.Resolve(library.Path).Enabled);

        // Inside a project's directory a file is that project's source, directives or not: the
        // project disables implicit usings, so the compilation must carry none.
        using var project = new TempDir();
        Write(project.Path, "app.csproj", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net11.0</TargetFramework>
  </PropertyGroup>
</Project>
""");
        Write(Path.Combine(project.Path, "scripts"), "run.cs", "#:package Humanizer@2.14.1\nSystem.Console.WriteLine(1);\n");
        Assert.False(GlobalUsings.Resolve(project.Path).Enabled);
    }

    [Fact]
    public void Methods_FileBasedApp_BindsThroughImplicitUsingsAndPinsDeclaredPackageVersions()
    {
        using var fixture = new TempDir();
        Write(fixture.Path, "app.cs", App);

        var slice = Depscan.Dosai.GetMethodsSlice(fixture.Path);

        Assert.DoesNotContain(slice.MethodCalls ?? [], call => call.EvidenceKind == AnalysisEvidenceKind.SourceUnresolved);
        Assert.Contains(slice.MethodCalls ?? [], call => call.CalledMethod?.Contains("Console.WriteLine", StringComparison.Ordinal) == true);
        var dependencies = (slice.Dependencies ?? []).Where(dependency => dependency.Module == "FileBasedApp").ToDictionary(dependency => dependency.Name!);
        Assert.Equal("pkg:nuget/Humanizer@2.14.1", dependencies["Humanizer"].Purl);
        // RC2's trailing item metadata does not end up in the version.
        Assert.Equal("pkg:nuget/System.CommandLine@2.0.1", dependencies["System.CommandLine"].Purl);
        // A floating version is only known after a restore, which happens outside the tree.
        Assert.Null(dependencies["Serilog"].Purl);
    }

    [Fact]
    public void Methods_FSharpScriptNuGetReference_PinsAnExactVersion()
    {
        using var fixture = new TempDir();
        Write(fixture.Path, "build.fsx", """
#r "nuget: FSharp.Data, 6.4.0"
#r "nuget: Argu"

let run () = printfn "%s" "done"
""");

        var slice = Depscan.Dosai.GetMethodsSlice(fixture.Path);

        var dependencies = slice.Dependencies ?? [];
        Assert.Contains(dependencies, dependency => dependency is { Name: "FSharp.Data", Purl: "pkg:nuget/FSharp.Data@6.4.0" });
        Assert.Contains(dependencies, dependency => dependency is { Name: "Argu", Purl: null });
    }
}
