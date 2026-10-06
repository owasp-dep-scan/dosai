using System.Runtime.CompilerServices;
using Depscan;
using Xunit;

namespace Depscan.Tests;

/// <summary>
///     Issue #74: global usings beyond the base SDK list, tree framework-reference packs, and
///     their diagnostics. Fixtures are written to temporary directories; nothing here restores
///     packages or depends on which SDKs are installed, except the explicitly skipped
///     installed-pack tests.
/// </summary>
public class Issue74BindingTests
{
    private sealed class TempDir : IDisposable
    {
        public TempDir() => Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dosai-issue74-" + Guid.NewGuid().ToString("N"));

        public string Path { get; }

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

    private const string Net8Csproj = """
<Project Sdk="{0}">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
""";

    [Fact]
    public void GlobalUsings_WebSdk_AddsTheNineWebUsings()
    {
        using var fixture = new TempDir();
        Write(fixture.Path, "web.csproj", string.Format(Net8Csproj, "Microsoft.NET.Sdk.Web"));
        Write(fixture.Path, "Program.cs", "var builder = WebApplication.CreateBuilder(args);");

        var decision = GlobalUsings.Resolve(fixture.Path);
        Assert.True(decision.Enabled);
        var text = decision.Tree!.GetText().ToString();
        foreach (var expected in new[]
                 {
                     "global using Microsoft.AspNetCore.Builder;", "global using Microsoft.AspNetCore.Hosting;", "global using Microsoft.AspNetCore.Http;",
                     "global using Microsoft.AspNetCore.Routing;", "global using Microsoft.Extensions.Configuration;", "global using Microsoft.Extensions.DependencyInjection;",
                     "global using Microsoft.Extensions.Hosting;", "global using Microsoft.Extensions.Logging;", "global using System.Net.Http.Json;"
                 })
        {
            Assert.Contains(expected, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void GlobalUsings_WorkerAndWindowsFormsSdkLists_AndTheBaseSdkDifferences()
    {
        using var worker = new TempDir();
        Write(worker.Path, "worker.csproj", string.Format(Net8Csproj, "Microsoft.NET.Sdk.Worker"));
        var workerText = GlobalUsings.Resolve(worker.Path).Tree!.GetText().ToString();
        Assert.Contains("global using Microsoft.Extensions.Hosting;", workerText, StringComparison.Ordinal);
        Assert.DoesNotContain("Microsoft.AspNetCore", workerText, StringComparison.Ordinal);

        using var forms = new TempDir();
        Write(forms.Path, "forms.csproj", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <UseWindowsForms>true</UseWindowsForms>
  </PropertyGroup>
</Project>
""");
        var formsText = GlobalUsings.Resolve(forms.Path).Tree!.GetText().ToString();
        Assert.Contains("global using System.Windows.Forms;", formsText, StringComparison.Ordinal);
        Assert.Contains("global using System.Drawing;", formsText, StringComparison.Ordinal);

        // .NET 11's base list adds System.Net.Http.Json; .NET Framework's drops System.Net.Http.
        using var net11 = new TempDir();
        Write(net11.Path, "app.csproj", string.Format(Net8Csproj, "Microsoft.NET.Sdk").Replace("net8.0", "net11.0"));
        var net11Text = GlobalUsings.Resolve(net11.Path).Tree!.GetText().ToString();
        Assert.Equal(1, net11Text.Split("System.Net.Http.Json", StringSplitOptions.RemoveEmptyEntries).Length - 1);

        using var net472 = new TempDir();
        Write(net472.Path, "legacy.csproj", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net472</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
""");
        var net472Text = GlobalUsings.Resolve(net472.Path).Tree!.GetText().ToString();
        Assert.DoesNotContain("global using System.Net.Http;", net472Text, StringComparison.Ordinal);
    }

    [Fact]
    public void GlobalUsings_UsingItems_StaticAliasAndRemove_AreHonoredFromProjectAndDirectoryBuild()
    {
        using var fixture = new TempDir();
        var projectDir = System.IO.Path.Combine(fixture.Path, "src");
        Write(projectDir, "app.csproj", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <Using Include="System.Text" />
    <Using Include="System.Math" Static="true" />
    <Using Include="System.Text.StringBuilder" Alias="SB" />
  </ItemGroup>
</Project>
""");
        Write(projectDir, "Directory.Build.props", """
<Project>
  <ItemGroup>
    <Using Remove="System.Net.Http" />
  </ItemGroup>
</Project>
""");

        var decision = GlobalUsings.Resolve(projectDir);
        var text = decision.Tree!.GetText().ToString();
        Assert.Contains("global using System.Text;", text, StringComparison.Ordinal);
        Assert.Contains("global using static System.Math;", text, StringComparison.Ordinal);
        Assert.Contains("global using SB = System.Text.StringBuilder;", text, StringComparison.Ordinal);
        // The Remove item applies even when it also comes from Directory.Build.props, and a
        // conditioned item is applied with a diagnostic.
        Assert.DoesNotContain("global using System.Net.Http;", text, StringComparison.Ordinal);
    }

    [Fact]
    public void GlobalUsings_ConditionedUsingItem_IsAppliedWithADiagnostic()
    {
        using var fixture = new TempDir();
        Write(fixture.Path, "app.csproj", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <Using Include="System.Text" Condition="'$(Configuration)' == 'Debug'" />
  </ItemGroup>
</Project>
""");
        var decision = GlobalUsings.Resolve(fixture.Path);
        Assert.True(decision.Enabled);
        Assert.Contains(decision.Diagnostics, diagnostic => diagnostic.Contains("does not evaluate", StringComparison.Ordinal));
    }

    [Fact]
    public void GlobalUsings_GeneratedFileIsAuthoritative()
    {
        using var fixture = new TempDir();
        var projectPath = Write(fixture.Path, "app.csproj", string.Format(Net8Csproj, "Microsoft.NET.Sdk"));
        var outputDirectory = System.IO.Path.Combine(fixture.Path, "obj", "Debug", "net8.0");
        var generatedPath = Write(outputDirectory, "app.GlobalUsings.g.cs", "global using System;\nglobal using System.Text;\n");
        // Fresh g.cs: authoritative, even though the project would compute a different set.
        File.SetLastWriteTimeUtc(generatedPath, File.GetLastWriteTimeUtc(projectPath).AddMinutes(1));
        var decision = GlobalUsings.Resolve(fixture.Path);
        var text = decision.Tree!.GetText().ToString();
        Assert.Contains("global using System.Text;", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Microsoft.AspNetCore", text, StringComparison.Ordinal);
    }

    [Fact]
    public void GlobalUsings_StaleGeneratedFile_IsRecomputedFromTheProjectWithADiagnostic()
    {
        using var fixture = new TempDir();
        var projectPath = Write(fixture.Path, "app.csproj", string.Format(Net8Csproj, "Microsoft.NET.Sdk"));
        var generatedPath = Write(System.IO.Path.Combine(fixture.Path, "obj", "Debug", "net8.0"), "app.GlobalUsings.g.cs", "global using System;\nglobal using System.Text;\n");
        File.SetLastWriteTimeUtc(generatedPath, File.GetLastWriteTimeUtc(projectPath).AddMinutes(-5));

        var decision = GlobalUsings.Resolve(fixture.Path);
        Assert.Contains(decision.Diagnostics, diagnostic => diagnostic.Contains("older than the project file", StringComparison.Ordinal));
        Assert.DoesNotContain("global using System.Text;", decision.Tree!.GetText().ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void GlobalUsings_DisabledProjectAndDisagreeingProjects()
    {
        using var disabled = new TempDir();
        Write(disabled.Path, "classic.csproj", """
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>disable</ImplicitUsings>
  </PropertyGroup>
</Project>
""");
        Write(disabled.Path, "Program.cs", "var x = 1;");
        Assert.False(GlobalUsings.Resolve(disabled.Path).Enabled);

        using var mixed = new TempDir();
        var web = System.IO.Path.Combine(mixed.Path, "web");
        var console = System.IO.Path.Combine(mixed.Path, "console");
        Write(web, "web.csproj", string.Format(Net8Csproj, "Microsoft.NET.Sdk.Web"));
        Write(console, "console.csproj", string.Format(Net8Csproj, "Microsoft.NET.Sdk"));
        var decision = GlobalUsings.Resolve(mixed.Path);
        Assert.True(decision.Enabled);
        Assert.Contains(decision.Diagnostics, diagnostic => diagnostic.Contains("differ between projects", StringComparison.Ordinal));
    }

    [Fact]
    public void GlobalUsings_AUsingThatDoesNotResolveInOneProject_DoesNotStopBindingInTheOthers()
    {
        // The union merge must not break other projects' bindings when one project's using
        // names nothing (a missing package namespace): Roslyn errors that directive alone.
        using var fixture = new TempDir();
        var web = System.IO.Path.Combine(fixture.Path, "web");
        var console = System.IO.Path.Combine(fixture.Path, "console");
        Write(web, "web.csproj", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <Using Include="Missing.Package.Namespace" />
  </ItemGroup>
</Project>
""");
        Write(console, "console.csproj", string.Format(Net8Csproj, "Microsoft.NET.Sdk"));
        Write(console, "Program.cs", "class P { static void Main() { System.Console.WriteLine(System.Text.Encoding.UTF8); } }");

        var result = Depscan.Dosai.GetMethodsSlice(fixture.Path);
        Assert.Contains(result.MethodCalls, call => call.CalledMethod.Contains("Console.WriteLine", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("differ between projects", StringComparison.Ordinal));
    }

    [Fact]
    public void TreeFrameworks_DetectsSdksUsingItemsAssetsAndRuntimeConfigs()
    {
        using var fixture = new TempDir();
        var web = System.IO.Path.Combine(fixture.Path, "web");
        Write(web, "web.csproj", string.Format(Net8Csproj, "Microsoft.NET.Sdk.Web"));
        var desktop = System.IO.Path.Combine(fixture.Path, "desktop");
        Write(desktop, "desktop.csproj", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <UseWPF>true</UseWPF>
  </PropertyGroup>
</Project>
""");
        var explicitReference = System.IO.Path.Combine(fixture.Path, "lib");
        Write(explicitReference, "lib.csproj", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
</Project>
""");
        Write(System.IO.Path.Combine(web, "obj"), "project.assets.json", """
{"frameworks": {"net8.0": {"frameworkReferences": {"Microsoft.AspNetCore.App": {"type": "platform"}}}}}
""");
        Write(fixture.Path, "app.runtimeconfig.json", """
{"runtimeOptions": {"frameworks": [{"name": "Microsoft.AspNetCore.App", "version": "8.0.31"}]}}
""");

        var detected = Depscan.TreeFrameworks.Detect(fixture.Path).ToDictionary(framework => framework.Name, framework => framework.Evidence, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Microsoft.AspNetCore.App", detected.Keys);
        Assert.Contains("Microsoft.WindowsDesktop.App.WPF", detected.Keys);
        Assert.DoesNotContain("Microsoft.NETCore.App", detected.Keys);
    }

    [Fact]
    public void FrameworkReferences_PackResolution_PrefersExactMajorAndReportsFallbacks()
    {
        using var packs = new TempDir();
        // A synthetic pack layout with two versions: an exact-major 8.0.1 and a newer 9.0.0.
        foreach (var version in new[] { "8.0.1", "9.0.0" })
        {
            var referenceDirectory = System.IO.Path.Combine(packs.Path, "packs", "Test.Framework.Ref", version, "ref", "net8.0");
            Write(referenceDirectory, "Test.Framework.dll", "not a real assembly - only the directory shape matters for selection");
        }

        var notes = new List<string>();
        var exact = FrameworkReferences.ResolvePackDirectory("Test.Framework", "net8.0", 8, notes, [packs.Path + System.IO.Path.DirectorySeparatorChar + "packs"]);
        Assert.NotNull(exact);
        Assert.Equal("8.0.1", exact!.Value.Version);
        Assert.True(exact.Value.ExactMajor);

        var fallback = FrameworkReferences.ResolvePackDirectory("Test.Framework", "net8.0", 7, notes, [System.IO.Path.Combine(packs.Path, "packs")]);
        Assert.NotNull(fallback);
        Assert.Equal("9.0.0", fallback!.Value.Version);
        Assert.False(fallback.Value.ExactMajor);
        Assert.Contains(notes, note => note.Contains("No 7.x reference pack", StringComparison.Ordinal) && note.Contains("9.0.0", StringComparison.Ordinal));

        var missing = FrameworkReferences.ResolvePackDirectory("Missing.Framework", "net8.0", 8, notes, [System.IO.Path.Combine(packs.Path, "packs")]);
        Assert.Null(missing);
    }

    [Fact]
    public void FrameworkReferences_TreeWithoutExtraFrameworks_KeepsTheProcessWideSet()
    {
        using var fixture = new TempDir();
        Write(fixture.Path, "app.csproj", string.Format(Net8Csproj, "Microsoft.NET.Sdk"));
        Assert.Equal(FrameworkReferences.Current, FrameworkReferences.ForTree(fixture.Path));
    }

    /// <summary>
    ///     Binding through a real installed pack: webprobe-style source with ILogger binds when
    ///     an ASP.NET Core pack exists for the tree's target. Skipped where no pack is installed.
    /// </summary>
    [SkippableFact]
    public void Methods_WebTree_BindsThroughTheAspNetCorePack()
    {
        using var fixture = new TempDir();
        Write(fixture.Path, "web.csproj", string.Format(Net8Csproj, "Microsoft.NET.Sdk.Web"));
        Write(fixture.Path, "Program.cs", """
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.MapGet("/", (ILogger<Program> logger) =>
{
    logger.LogWarning("Serving the root");
    return TypedResults.Ok("hello");
});
app.Run();
""");

        var result = Depscan.Dosai.GetMethodsSlice(fixture.Path);
        Skip.If(result.Diagnostics.Any(diagnostic => diagnostic.Contains("No reference pack for 'Microsoft.AspNetCore.App'", StringComparison.Ordinal)), "no ASP.NET Core pack for the target");
        var targets = string.Join("\n", result.MethodCalls.Select(call => (call.TargetId ?? string.Empty) + "|" + call.CalledMethod));
        Assert.Contains("WebApplication.CreateBuilder(string[])", targets, StringComparison.Ordinal);
        Assert.Contains("LoggerExtensions.LogWarning", targets, StringComparison.Ordinal);
        Assert.Contains("TypedResults.Ok<string>", targets, StringComparison.Ordinal);
    }

    [Fact]
    public void Methods_DesktopTreeWithoutPacksOnThisOs_ProducesADiagnosticNotAFailure()
    {
        using var fixture = new TempDir();
        Write(fixture.Path, "forms.csproj", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <UseWindowsForms>true</UseWindowsForms>
  </PropertyGroup>
</Project>
""");
        Write(fixture.Path, "MainForm.cs", """
namespace Forms
{
    public class MainForm : System.Windows.Forms.Form { }
}
""");

        var result = Depscan.Dosai.GetMethodsSlice(fixture.Path);
        // Either the pack binds the calls, or the missing pack is named - never a crash, and
        // the diagnostic never advises restoring the tree for a pack problem.
        var packDiagnostic = result.Diagnostics.FirstOrDefault(diagnostic => diagnostic.Contains("Microsoft.WindowsDesktop.App", StringComparison.Ordinal));
        Assert.True(packDiagnostic is null || !packDiagnostic.Contains("Restore or build the tree", StringComparison.Ordinal),
            $"pack diagnostic must not advise restore/build: {packDiagnostic}");
        Skip.If(packDiagnostic is null && OperatingSystem.IsMacOS(), "this machine has a WindowsDesktop pack installed");
    }

    [Fact]
    public void AssemblyProbing_RuntimeConfigNamesFrameworksBeyondTheBase()
    {
        using var fixture = new TempDir();
        var sharedRoot = System.IO.Path.Combine(fixture.Path, "shared");
        var aspNetCore8 = Directory.CreateDirectory(System.IO.Path.Combine(sharedRoot, "Microsoft.AspNetCore.App", "8.0.31")).FullName;
        var aspNetCore9 = Directory.CreateDirectory(System.IO.Path.Combine(sharedRoot, "Microsoft.AspNetCore.App", "9.0.0")).FullName;
        var netCore11 = Directory.CreateDirectory(System.IO.Path.Combine(sharedRoot, "Microsoft.NETCore.App", "11.0.0")).FullName;
        var appDirectory = System.IO.Path.Combine(fixture.Path, "app");
        Directory.CreateDirectory(appDirectory);

        var frameworks = new List<(string Name, string Version)> { ("Microsoft.AspNetCore.App", "8.0.31") };
        var probing = Depscan.Dosai.GetSharedFrameworkProbingPaths(netCore11, [sharedRoot], appDirectory, _ => frameworks);

        // The running runtime's directory leads, then the exact named version ahead of the
        // newer installed one, and both are present: a non-base framework carries no
        // System.Runtime, so no version floor applies.
        Assert.True(probing.SequenceEqual(new[] { netCore11, aspNetCore8, aspNetCore9 }),
            $"probing order was [{string.Join(", ", probing.Select(directory => $"{Path.GetFileName(Path.GetDirectoryName(directory) ?? string.Empty)}/{Path.GetFileName(directory)}"))}]");
    }
}
