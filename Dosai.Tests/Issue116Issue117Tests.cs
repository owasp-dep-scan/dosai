using System.Text.Json;
using System.Text.Json.Nodes;
using Depscan;
using Xunit;

namespace Depscan.Tests;

/// <summary>
///     Issues #116 and #117. #116: restore writes the resolved frameworks of
///     project.assets.json under <c>project</c>, so a framework reference that only the assets
///     file names was never read. #117: properties and items set only in a file reached through
///     an <c>&lt;Import&gt;</c> (a chained Directory.Build.props, a build/ props file) were
///     ignored, which since #112 cost a test project its implicit usings. Fixtures are written
///     to temporary directories; nothing here restores packages or depends on which SDKs are
///     installed beyond the base framework references of the running runtime.
/// </summary>
public class Issue116Issue117Tests
{
    private sealed class TempDir : IDisposable
    {
        public TempDir() => Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dosai-issue116-117-" + Guid.NewGuid().ToString("N"));

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

    private static string WriteFile(string path, string content)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private const string PlainProject = """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
</Project>
""";

    // Trimmed from a real `dotnet restore` output (SDK 11, shape unchanged since SDK 8): the
    // frameworks restore resolved sit under `project`, with the frameworkReferences object
    // exactly as the issue's repro prints it. Machine-specific paths are kept out.
    private const string RestoredAssetsJson = """
{
  "version": 4,
  "targets": {
    "net8.0": {}
  },
  "libraries": {},
  "projectFileDependencyGroups": {
    "net8.0": []
  },
  "packageFolders": {
    "/home/analyst/.nuget/packages/": {}
  },
  "project": {
    "version": "1.0.0",
    "restore": {
      "projectUniqueName": "Lib/Lib.csproj",
      "projectName": "Lib",
      "projectPath": "Lib/Lib.csproj",
      "originalTargetFrameworks": [
        "net8.0"
      ],
      "frameworks": {
        "net8.0": {
          "framework": "net8.0",
          "targetAlias": "net8.0",
          "projectReferences": {}
        }
      },
      "SdkAnalysisLevel": "11.0.100"
    },
    "frameworks": {
      "net8.0": {
        "framework": "net8.0",
        "targetAlias": "net8.0",
        "imports": [
          "net461",
          "net462",
          "net47",
          "net471",
          "net472",
          "net48",
          "net481"
        ],
        "assetTargetFallback": true,
        "warn": true,
        "downloadDependencies": [
          {
            "name": "Microsoft.AspNetCore.App.Ref",
            "version": "[8.0.32, 8.0.32]"
          },
          {
            "name": "Microsoft.NETCore.App.Ref",
            "version": "[8.0.32, 8.0.32]"
          }
        ],
        "frameworkReferences": {
          "Microsoft.AspNetCore.App": {
            "privateAssets": "none"
          },
          "Microsoft.NETCore.App": {
            "privateAssets": "all"
          }
        }
      }
    }
  }
}
""";

    // Issue #116: the only evidence for Microsoft.AspNetCore.App is the assets file, in the
    // shape restore actually writes (frameworks under `project`).
    [Fact]
    public void TreeFrameworks_AssetsFileUnderProject_FrameworkReferencesAreDetected()
    {
        using var fixture = new TempDir();
        Write(System.IO.Path.Combine(fixture.Path, "Lib"), "Lib.csproj", PlainProject);
        Write(System.IO.Path.Combine(fixture.Path, "Lib", "obj"), "project.assets.json", RestoredAssetsJson);

        var detected = TreeFrameworks.Detect(fixture.Path).ToDictionary(framework => framework.Name, framework => framework.Evidence, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Microsoft.AspNetCore.App", detected.Keys);
        Assert.Contains("project.assets.json", detected["Microsoft.AspNetCore.App"], StringComparison.Ordinal);
        // The base framework is never claimed, whatever the assets file lists.
        Assert.DoesNotContain("Microsoft.NETCore.App", detected.Keys);
    }

    // Older and hand-written files carry `frameworks` at the root; keep reading them.
    [Fact]
    public void TreeFrameworks_RootLevelFrameworksInAssetsFile_IsStillRead()
    {
        using var fixture = new TempDir();
        Write(System.IO.Path.Combine(fixture.Path, "Lib"), "Lib.csproj", PlainProject);
        Write(System.IO.Path.Combine(fixture.Path, "Lib", "obj"), "project.assets.json", """
{"version": 3, "frameworks": {"net8.0": {"frameworkReferences": {"Microsoft.AspNetCore.App": {"type": "platform"}}}}}
""");

        var detected = TreeFrameworks.Detect(fixture.Path).ToDictionary(framework => framework.Name, framework => framework.Evidence, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Microsoft.AspNetCore.App", detected.Keys);
        Assert.Contains("project.assets.json", detected["Microsoft.AspNetCore.App"], StringComparison.Ordinal);
    }

    // Issue #117's main repro: a test project whose Directory.Build.props only imports the
    // root one (with a backslash) gets the root's ImplicitUsings, so MemoryStream binds again.
    private static void WriteChainedImplicitUsingsFixture(TempDir fixture, string importProjectAttribute)
    {
        WriteFile(System.IO.Path.Combine(fixture.Path, "Directory.Build.props"), """
<Project>
  <PropertyGroup>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
""");
        WriteFile(System.IO.Path.Combine(fixture.Path, "test", "Directory.Build.props"), $"""
<Project>
  <Import Project="{importProjectAttribute}" />
</Project>
""");
        var app = System.IO.Path.Combine(fixture.Path, "App");
        Write(app, "App.csproj", """
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
</Project>
""");
        Write(app, "Program.cs", "var app = WebApplication.CreateBuilder(args).Build();\napp.Run();\n");
        var tests = System.IO.Path.Combine(fixture.Path, "test", "App.Tests");
        Write(tests, "App.Tests.csproj", PlainProject);
        Write(tests, "StreamTests.cs", """
namespace App.Tests;

public static class StreamTests
{
    public static long Length() => new MemoryStream(new byte[3]).Length;
}
""");
    }

    private static void AssertStreamTestsBindingWorks(TempDir fixture)
    {
        var decision = GlobalUsings.Resolve(fixture.Path);
        Assert.True(decision.Enabled);
        // The common set carries the base usings only when the test project resolved them too:
        // an empty test-project set would leave no common tree at all.
        Assert.NotNull(decision.Tree);
        Assert.Contains("global using System.IO;", decision.Tree!.GetText().ToString(), StringComparison.Ordinal);
        Assert.NotNull(decision.PerFile);

        var slice = Depscan.Dosai.GetMethodsSlice(fixture.Path);
        Assert.Contains(slice.MethodCalls ?? [], call => (call.Path ?? string.Empty).Replace('\\', '/').EndsWith("StreamTests.cs", StringComparison.Ordinal)
            && call.EvidenceKind == AnalysisEvidenceKind.SourceRoslynDirect
            && string.Concat(call.CalledMethod, call.TargetId).Contains("MemoryStream", StringComparison.Ordinal));
    }

    [Fact]
    public void GlobalUsings_ImplicitUsingsThroughBackslashImport_ReachesTheTestProject()
    {
        using var fixture = new TempDir();
        WriteChainedImplicitUsingsFixture(fixture, "..\\Directory.Build.props");
        AssertStreamTestsBindingWorks(fixture);
    }

    [Fact]
    public void GlobalUsings_ImplicitUsingsThroughPathFunctionImports_ReachesTheTestProject()
    {
        // The two chaining forms real repositories use to import the parent Directory.Build
        // file: GetPathOfFileAbove (start above this file, so it does not import itself) and
        // GetDirectoryNameOfFileAbove spliced into a larger path.
        using var getPath = new TempDir();
        WriteChainedImplicitUsingsFixture(getPath, "$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))");
        AssertStreamTestsBindingWorks(getPath);

        using var getDirectory = new TempDir();
        WriteChainedImplicitUsingsFixture(getDirectory, "$([MSBuild]::GetDirectoryNameOfFileAbove($(MSBuildThisFileDirectory).., Directory.Build.props))/Directory.Build.props");
        AssertStreamTestsBindingWorks(getDirectory);
    }

    // TargetFramework set only in the imported root props: the test project guards against
    // net8.0 while another project of the tree targets net10.0.
    [Fact]
    public void TargetFrameworkDetection_ThroughImport_IsPerProjectAndGuardsSurvive()
    {
        using var fixture = new TempDir();
        WriteFile(System.IO.Path.Combine(fixture.Path, "Directory.Build.props"), """
<Project>
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
</Project>
""");
        WriteFile(System.IO.Path.Combine(fixture.Path, "test", "Directory.Build.props"), """
<Project>
  <Import Project="..\Directory.Build.props" />
</Project>
""");
        Write(System.IO.Path.Combine(fixture.Path, "App"), "App.csproj", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>
""");
        var tests = System.IO.Path.Combine(fixture.Path, "test", "App.Tests");
        Write(tests, "App.Tests.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Write(tests, "Conditional.cs", """
namespace App.Tests;

public static class Conditional
{
#if NET8_0
    public static string OnlyNet8() => "net8";
#endif
}
""");

        var targets = TargetFrameworkDetection.ForFile(fixture.Path, System.IO.Path.Combine(tests, "App.Tests.csproj"), ".csproj").TargetFrameworks;
        Assert.Equal(["net8.0"], targets);

        var slice = Depscan.Dosai.GetMethodsSlice(fixture.Path);
        Assert.Contains(slice.Methods ?? [], method => (method.Path ?? string.Empty).Replace('\\', '/').EndsWith("Conditional.cs", StringComparison.Ordinal) && method.Name == "OnlyNet8");
    }

    // UseWindowsForms through an import, and UseWPF through a Directory.Build.targets that
    // imports a build/ props file (the layout of the issue's comment).
    [Fact]
    public void TreeFrameworks_UsePropertiesThroughImports_AreDetected()
    {
        using var forms = new TempDir();
        WriteFile(System.IO.Path.Combine(forms.Path, "Directory.Build.props"), """
<Project>
  <PropertyGroup>
    <UseWindowsForms>true</UseWindowsForms>
  </PropertyGroup>
</Project>
""");
        WriteFile(System.IO.Path.Combine(forms.Path, "test", "Directory.Build.props"), """
<Project>
  <Import Project="../Directory.Build.props" />
</Project>
""");
        Write(System.IO.Path.Combine(forms.Path, "test", "App"), "App.csproj", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
  </PropertyGroup>
</Project>
""");
        var detected = TreeFrameworks.Detect(forms.Path).Select(framework => framework.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("Microsoft.WindowsDesktop.App.WindowsForms", detected);

        using var wpf = new TempDir();
        WriteFile(System.IO.Path.Combine(wpf.Path, "build", "wpf.props"), """
<Project>
  <PropertyGroup>
    <UseWPF>true</UseWPF>
  </PropertyGroup>
</Project>
""");
        WriteFile(System.IO.Path.Combine(wpf.Path, "test", "Directory.Build.targets"), """
<Project>
  <Import Project="../build/wpf.props" />
</Project>
""");
        Write(System.IO.Path.Combine(wpf.Path, "test", "App"), "App.csproj", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
  </PropertyGroup>
</Project>
""");
        var wpfDetected = TreeFrameworks.Detect(wpf.Path).ToDictionary(framework => framework.Name, framework => framework.Evidence, StringComparer.Ordinal);
        Assert.Contains("Microsoft.WindowsDesktop.App.WPF", wpfDetected.Keys);
    }

    // Issue #116's repro: the FrameworkReference sits in a file Dosai does not read on its own
    // (build/aspnet.props), imported by the project; no assets file is present.
    [Fact]
    public void TreeFrameworks_FrameworkReferenceThroughProjectImport_IsDetectedWithoutAssets()
    {
        using var fixture = new TempDir();
        WriteFile(System.IO.Path.Combine(fixture.Path, "build", "aspnet.props"), """
<Project>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
</Project>
""");
        Write(System.IO.Path.Combine(fixture.Path, "Lib"), "Lib.csproj", """
<Project Sdk="Microsoft.NET.Sdk">
  <Import Project="..\build\aspnet.props" />
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
</Project>
""");

        var detected = TreeFrameworks.Detect(fixture.Path).ToDictionary(framework => framework.Name, framework => framework.Evidence, StringComparer.Ordinal);
        Assert.Contains("Microsoft.AspNetCore.App", detected.Keys);
        Assert.Contains("aspnet.props", detected["Microsoft.AspNetCore.App"], StringComparison.Ordinal);
    }

    // <Using> items from an imported file are applied in evaluation order, so a Remove in the
    // importing file after the import drops the imported Include.
    [Fact]
    public void GlobalUsings_UsingItemsThroughImports_FollowEvaluationOrder()
    {
        using var removed = new TempDir();
        WriteFile(System.IO.Path.Combine(removed.Path, "shared.props"), """
<Project>
  <ItemGroup>
    <Using Include="System.Text.Json" />
  </ItemGroup>
</Project>
""");
        WriteFile(System.IO.Path.Combine(removed.Path, "test", "Directory.Build.props"), """
<Project>
  <Import Project="..\shared.props" />
  <ItemGroup>
    <Using Remove="System.Text.Json" />
  </ItemGroup>
</Project>
""");
        Write(System.IO.Path.Combine(removed.Path, "test", "App"), "App.csproj", PlainProject.Replace("<TargetFramework>net8.0</TargetFramework>", "<TargetFramework>net8.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings>"));
        var removedText = GlobalUsings.Resolve(removed.Path).Tree!.GetText().ToString();
        Assert.DoesNotContain("global using System.Text.Json;", removedText, StringComparison.Ordinal);

        using var kept = new TempDir();
        WriteFile(System.IO.Path.Combine(kept.Path, "shared.props"), """
<Project>
  <ItemGroup>
    <Using Include="System.Text.Json" />
  </ItemGroup>
</Project>
""");
        WriteFile(System.IO.Path.Combine(kept.Path, "test", "Directory.Build.props"), """
<Project>
  <Import Project="..\shared.props" />
</Project>
""");
        Write(System.IO.Path.Combine(kept.Path, "test", "App"), "App.csproj", PlainProject.Replace("<TargetFramework>net8.0</TargetFramework>", "<TargetFramework>net8.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings>"));
        var keptText = GlobalUsings.Resolve(kept.Path).Tree!.GetText().ToString();
        Assert.Contains("global using System.Text.Json;", keptText, StringComparison.Ordinal);
    }

    // Last definition in evaluation order wins: the project's own definition after the import
    // beats the imported one, and one before it loses to the imported one.
    [Fact]
    public void GlobalUsings_LastDefinitionWins_WithinTheEvaluationOrder()
    {
        const string enableProps = """
<Project>
  <PropertyGroup>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
""";

        using var disabled = new TempDir();
        WriteFile(System.IO.Path.Combine(disabled.Path, "props.props"), enableProps);
        Write(disabled.Path, "App.csproj", """
<Project Sdk="Microsoft.NET.Sdk">
  <Import Project="props.props" />
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>disable</ImplicitUsings>
  </PropertyGroup>
</Project>
""");
        Assert.False(GlobalUsings.Resolve(disabled.Path).Enabled);

        using var enabled = new TempDir();
        WriteFile(System.IO.Path.Combine(enabled.Path, "props.props"), enableProps);
        Write(enabled.Path, "App.csproj", """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>disable</ImplicitUsings>
  </PropertyGroup>
  <Import Project="props.props" />
</Project>
""");
        Assert.True(GlobalUsings.Resolve(enabled.Path).Enabled);
    }

    // Exists(...) is evaluated (true follows the import as an unconditioned one); any other
    // condition is followed but marked, so a property only it defines is unknown and items
    // from it are applied with a diagnostic.
    [Fact]
    public void MsBuildImports_ExistsConditionFollowed_AndOtherConditionsMarked()
    {
        using var exists = new TempDir();
        WriteFile(System.IO.Path.Combine(exists.Path, "Directory.Build.props"), """
<Project>
  <PropertyGroup>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
""");
        WriteFile(System.IO.Path.Combine(exists.Path, "test", "Directory.Build.props"), """
<Project>
  <Import Project="..\Directory.Build.props" Condition="Exists('..\Directory.Build.props')" />
</Project>
""");
        Write(System.IO.Path.Combine(exists.Path, "test", "App"), "App.csproj", PlainProject);
        Assert.True(GlobalUsings.Resolve(exists.Path).Enabled);

        using var unknown = new TempDir();
        WriteFile(System.IO.Path.Combine(unknown.Path, "build", "props.props"), """
<Project>
  <PropertyGroup>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
""");
        Write(unknown.Path, "App.csproj", """
<Project Sdk="Microsoft.NET.Sdk">
  <Import Project="build\props.props" Condition="'$(Configuration)' == 'Debug'" />
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
</Project>
""");
        Assert.False(GlobalUsings.Resolve(unknown.Path).Enabled);

        using var marked = new TempDir();
        WriteFile(System.IO.Path.Combine(marked.Path, "build", "props.props"), """
<Project>
  <ItemGroup>
    <Using Include="System.Text.Json" />
  </ItemGroup>
</Project>
""");
        Write(marked.Path, "App.csproj", """
<Project Sdk="Microsoft.NET.Sdk">
  <Import Project="build\props.props" Condition="'$(Configuration)' == 'Debug'" />
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
""");
        var decision = GlobalUsings.Resolve(marked.Path);
        Assert.Contains("global using System.Text.Json;", decision.Tree!.GetText().ToString(), StringComparison.Ordinal);
        Assert.Contains(decision.Diagnostics, diagnostic => diagnostic.Contains("conditioned <Import>", StringComparison.Ordinal) && diagnostic.Contains("props.props", StringComparison.Ordinal));
    }

    // Broken imports never throw and never read what they must not: each is skipped, with a
    // diagnostic except where the import's own Exists condition already decided.
    [Fact]
    public void MsBuildImports_BrokenImports_AreSkippedWithDiagnostics()
    {
        using var fixture = new TempDir();
        const string importing = """
<Project>
  <Import Project="{0}" />
</Project>
""";

        // A imports B imports A.
        WriteFile(System.IO.Path.Combine(fixture.Path, "cycle", "a.props"), """
<Project>
  <Import Project="b.props" />
</Project>
""");
        WriteFile(System.IO.Path.Combine(fixture.Path, "cycle", "b.props"), """
<Project>
  <Import Project="a.props" />
</Project>
""");
        Write(System.IO.Path.Combine(fixture.Path, "cycle"), "App.csproj", """
<Project Sdk="Microsoft.NET.Sdk">
  <Import Project="a.props" />
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
</Project>
""");

        // A file importing itself.
        WriteFile(System.IO.Path.Combine(fixture.Path, "selfimport", "props.props"), string.Format(importing, "props.props"));
        Write(System.IO.Path.Combine(fixture.Path, "selfimport"), "App.csproj", """
<Project Sdk="Microsoft.NET.Sdk">
  <Import Project="props.props" />
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
</Project>
""");

        // A missing file, plainly and under Exists.
        WriteFile(System.IO.Path.Combine(fixture.Path, "missing", "props.props"), string.Format(importing, "..\\missing.props"));
        Write(System.IO.Path.Combine(fixture.Path, "missing"), "App.csproj", """
<Project Sdk="Microsoft.NET.Sdk">
  <Import Project="props.props" />
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
</Project>
""");
        WriteFile(System.IO.Path.Combine(fixture.Path, "missingexists", "props.props"), """
<Project>
  <Import Project="..\missing.props" Condition="Exists('..\missing.props')" />
</Project>
""");
        Write(System.IO.Path.Combine(fixture.Path, "missingexists"), "App.csproj", """
<Project Sdk="Microsoft.NET.Sdk">
  <Import Project="props.props" />
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
</Project>
""");

        // An unresolved property and a wildcard.
        WriteFile(System.IO.Path.Combine(fixture.Path, "unknownprop", "props.props"), string.Format(importing, "..\\$(DosaiUnknownProperty)\\shared.props"));
        Write(System.IO.Path.Combine(fixture.Path, "unknownprop"), "App.csproj", """
<Project Sdk="Microsoft.NET.Sdk">
  <Import Project="props.props" />
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
</Project>
""");
        WriteFile(System.IO.Path.Combine(fixture.Path, "wildcard", "props.props"), string.Format(importing, "..\\*.props"));
        Write(System.IO.Path.Combine(fixture.Path, "wildcard"), "App.csproj", """
<Project Sdk="Microsoft.NET.Sdk">
  <Import Project="props.props" />
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
</Project>
""");

        var decision = GlobalUsings.Resolve(fixture.Path);
        var diagnostics = string.Join("\n", decision.Diagnostics);
        Assert.Contains("closes an import cycle", diagnostics, StringComparison.Ordinal);
        Assert.Contains("'cycle/a.props'", diagnostics, StringComparison.Ordinal);
        Assert.Contains("does not exist", diagnostics, StringComparison.Ordinal);
        Assert.Contains("cannot expand", diagnostics, StringComparison.Ordinal);
        // The Exists-guarded import of the same missing file stays silent: its own message is
        // absent even though the plain one is reported.
        Assert.DoesNotContain("'missingexists/", diagnostics, StringComparison.Ordinal);

        // An import outside the scan root is skipped with a diagnostic, and its content is
        // never read.
        using var outside = new TempDir();
        WriteFile(System.IO.Path.Combine(outside.Path, "outside.props"), """
<Project>
  <PropertyGroup>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
""");
        var innerRoot = System.IO.Path.Combine(outside.Path, "inner");
        WriteFile(System.IO.Path.Combine(innerRoot, "App", "props.props"), string.Format(importing, "..\\..\\outside.props"));
        Write(System.IO.Path.Combine(innerRoot, "App"), "App.csproj", """
<Project Sdk="Microsoft.NET.Sdk">
  <Import Project="props.props" />
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
</Project>
""");
        var innerDecision = GlobalUsings.Resolve(innerRoot);
        Assert.False(innerDecision.Enabled);
        Assert.Contains(innerDecision.Diagnostics, diagnostic => diagnostic.Contains("outside the scanned tree", StringComparison.Ordinal));
    }

    // A legacy project imports the MSBuild toolset and its packages.config packages' build
    // files: neither is a file of the tree nor a broken import, so neither gives a diagnostic.
    // Single-quoted attributes are read like double-quoted ones.
    [Fact]
    public void MsBuildImports_ToolsetAndPackageImports_AreSilent_AndSingleQuotesAreRead()
    {
        using var fixture = new TempDir();
        var legacy = System.IO.Path.Combine(fixture.Path, "Legacy");
        Write(legacy, "Legacy.csproj", """
<?xml version="1.0" encoding="utf-8"?>
<Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <Import Project="$(MSBuildExtensionsPath)\$(MSBuildToolsVersion)\Microsoft.Common.props" Condition="Exists('$(MSBuildExtensionsPath)\$(MSBuildToolsVersion)\Microsoft.Common.props')" />
  <PropertyGroup>
    <TargetFrameworkVersion>v4.7.2</TargetFrameworkVersion>
  </PropertyGroup>
  <Import Project="$(MSBuildToolsPath)\Microsoft.CSharp.targets" />
  <Import Project="..\packages\Some.Package.1.0.0\build\Some.Package.targets" Condition="Exists('..\packages\Some.Package.1.0.0\build\Some.Package.targets')" />
  <Import Project="..\packages\Other.Package.2.0.0\build\Other.Package.targets" />
</Project>
""");
        WriteFile(System.IO.Path.Combine(fixture.Path, "packages", "Other.Package.2.0.0", "build", "Other.Package.targets"), """
<Project>
  <PropertyGroup>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
""");
        var modern = System.IO.Path.Combine(fixture.Path, "Modern");
        WriteFile(System.IO.Path.Combine(fixture.Path, "build", "shared.props"), """
<Project>
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
</Project>
""");
        Write(modern, "Modern.csproj", """
<Project Sdk='Microsoft.NET.Sdk'>
  <Import Project='..\build\shared.props' />
</Project>
""");

        Assert.Empty(MsBuildImports.DiagnosticsFor(fixture.Path));
        // A package's build file sits in build output by Dosai's rule and is not read.
        Assert.Null(MsBuildImports.PropertyValue(fixture.Path, legacy, System.IO.Path.Combine(legacy, "Legacy.csproj"), File.ReadAllText(System.IO.Path.Combine(legacy, "Legacy.csproj")), "ImplicitUsings"));
        Assert.Contains("net8.0", TargetFrameworkDetection.ForFile(fixture.Path, System.IO.Path.Combine(modern, "Code.cs"), ".csproj").TargetFrameworks);
    }

    // The import diagnostics are a function of the tree: the same list whichever reader
    // expanded which file first, with or without a warm cache.
    [Fact]
    public void MsBuildImports_Diagnostics_DoNotDependOnReadOrder()
    {
        using var fixture = new TempDir();
        WriteFile(System.IO.Path.Combine(fixture.Path, "a", "props.props"), """
<Project>
  <Import Project="$(DosaiUnknownProperty)\x.props" />
</Project>
""");
        Write(System.IO.Path.Combine(fixture.Path, "a"), "A.csproj", """
<Project Sdk="Microsoft.NET.Sdk">
  <Import Project="props.props" />
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
</Project>
""");
        WriteFile(System.IO.Path.Combine(fixture.Path, "b", "Directory.Build.props"), """
<Project>
  <Import Project="missing.props" />
</Project>
""");
        Write(System.IO.Path.Combine(fixture.Path, "b"), "B.csproj", PlainProject);

        MsBuildImports.ResetCache();
        var cold = MsBuildImports.DiagnosticsFor(fixture.Path);
        MsBuildImports.ResetCache();
        _ = MsBuildImports.Expand(fixture.Path, System.IO.Path.Combine(fixture.Path, "b", "Directory.Build.props"));
        var warm = MsBuildImports.DiagnosticsFor(fixture.Path);
        Assert.Equal(2, cold.Count);
        Assert.Equal(cold, warm);
        Assert.Contains(cold, message => message.Contains("'a/props.props'", StringComparison.Ordinal));
        Assert.Contains(cold, message => message.Contains("'b/missing.props'", StringComparison.Ordinal));
    }

    // Two runs over the same tree produce byte-identical JSON apart from the run's timestamp.
    [Fact]
    public void Methods_ImportAwareFixture_IsDeterministic()
    {
        using var fixture = new TempDir();
        WriteChainedImplicitUsingsFixture(fixture, "..\\Directory.Build.props");
        var first = Scrubbed(Depscan.Dosai.GetMethodsSlice(fixture.Path));
        var second = Scrubbed(Depscan.Dosai.GetMethodsSlice(fixture.Path));
        Assert.Equal(first, second);
    }

    private static string Scrubbed(MethodsSlice slice)
    {
        var node = JsonSerializer.SerializeToNode(slice, Depscan.Dosai.Options)!.AsObject();
        node["Metadata"]?["GeneratedAt"]?.ReplaceWith("scrubbed");
        return node.ToJsonString();
    }

    // The MCP server is one long-lived process: a build file edited between two tool calls must
    // be seen fresh, including everything its imports brought in.
    [Fact]
    public void McpCacheReset_SeesAnEditedImportedFile()
    {
        using var fixture = new TempDir();
        var rootProps = WriteFile(System.IO.Path.Combine(fixture.Path, "Directory.Build.props"), """
<Project>
  <PropertyGroup>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
""");
        WriteFile(System.IO.Path.Combine(fixture.Path, "test", "Directory.Build.props"), """
<Project>
  <Import Project="..\Directory.Build.props" />
</Project>
""");
        Write(System.IO.Path.Combine(fixture.Path, "test", "App"), "App.csproj", PlainProject);
        Assert.True(GlobalUsings.Resolve(fixture.Path).Enabled);

        File.WriteAllText(rootProps, """
<Project>
  <PropertyGroup>
    <ImplicitUsings>disable</ImplicitUsings>
  </PropertyGroup>
</Project>
""");
        var request = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new { name = "dosai.methods", arguments = new { path = fixture.Path } }
        });
        using var input = new StringReader(request + "\n");
        using var output = new StringWriter();
        McpServer.Run(defaultPath: fixture.Path, input: input, output: output);
        Assert.Contains("\"result\"", output.ToString(), StringComparison.Ordinal);
        Assert.False(GlobalUsings.Resolve(fixture.Path).Enabled);
    }
}
