using Depscan;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Dosai.Tests;

// Issue #81: a built tree whose bin/ holds a package's newer copy of a framework assembly
// referenced both copies, and calls into it did not bind. Issue #82: a project with its own
// restore output borrowed another project's package of a framework assembly's name through the
// tree-wide purl tables.
public partial class DosaiTests
{
    private const string Issue81LibrarySource = """
        [assembly: System.Reflection.AssemblyVersion("{0}")]

        namespace Issue81.Logging;

        public interface ILogger {{ void Write(string message); }}

        public static class LoggerExtensions
        {{
            public static void LogInformation(this ILogger logger, string message) => logger.Write(message);
        }}
        """;

    private const string Issue81ProgramSource = """
        using Issue81.Logging;

        public static class Program
        {
            public static void Run(ILogger logger)
            {
                logger.LogInformation("Hello");
            }
        }
        """;

    /// <summary>
    ///     A framework set of the process-wide references plus a "framework" copy of
    ///     Issue81.Logging at <paramref name="frameworkVersion" />, kept outside the scanned tree
    ///     the way a reference pack is.
    /// </summary>
    private static (IDisposable Override, string PackDirectory) OverrideFrameworkWithIssue81Library(string frameworkVersion)
    {
        var pack = Path.Combine(Path.GetTempPath(), "dosai-issue81-pack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(pack);
        var frameworkCopy = Issue76AssemblyLoadingTests.EmitAssembly(pack, "Issue81.Logging", string.Format(System.Globalization.CultureInfo.InvariantCulture, Issue81LibrarySource, frameworkVersion));
        var references = FrameworkReferences.Current.References.Append((frameworkCopy, MetadataReference.CreateFromFile(frameworkCopy))).ToList();
        return (FrameworkReferences.OverrideForTesting(new FrameworkReferenceSet(references, "issue81-test", null, [])), pack);
    }

    private static string WriteIssue81App(string root, string? binVersion)
    {
        var app = Path.Combine(root, "WebApp");
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, "WebApp.csproj"), """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
        File.WriteAllText(Path.Combine(app, "Program.cs"), Issue81ProgramSource);
        if (binVersion is not null)
        {
            var bin = Path.Combine(app, "bin", "Debug", "net10.0");
            Directory.CreateDirectory(bin);
            Issue76AssemblyLoadingTests.EmitAssembly(bin, "Issue81.Logging", string.Format(System.Globalization.CultureInfo.InvariantCulture, Issue81LibrarySource, binVersion));
        }

        return app;
    }

    [Theory]
    [InlineData(null, "8.0.0.0")]
    [InlineData("9.0.0.0", "9.0.0.0")]
    [InlineData("7.0.0.0", "8.0.0.0")]
    public void GetMethods_PackageCopyOfAFrameworkAssemblyInBin_BindsToOneCopy(string? binVersion, string expectedVersion)
    {
        using var tempDirectory = new TemporaryDirectory();
        var (frameworkOverride, pack) = OverrideFrameworkWithIssue81Library("8.0.0.0");
        try
        {
            using (frameworkOverride)
            {
                WriteIssue81App(tempDirectory.Path, binVersion);

                var slice = Depscan.Dosai.GetMethodsSlice(tempDirectory.Path);

                // The highest version is the copy the SDK compiles against: the package's newer
                // copy over the framework's, the framework's over an older package copy.
                var call = Assert.Single(slice.MethodCalls ?? [], call => call.CalledMethod?.Contains("LogInformation", StringComparison.Ordinal) == true);
                Assert.Equal(AnalysisEvidenceKind.SourceRoslynDirect, call.EvidenceKind);
                Assert.Contains($"Version={expectedVersion}", call.Assembly, StringComparison.Ordinal);
                var diagnostics = slice.Diagnostics ?? [];
                Assert.DoesNotContain(diagnostics, line => line.StartsWith("Semantic binding failed", StringComparison.Ordinal));
                Assert.Equal(binVersion == "9.0.0.0", diagnostics.Any(line => line.Contains("framework reference(s) were replaced by a higher-version copy", StringComparison.Ordinal) && line.Contains("Issue81.Logging 9.0.0.0 over 8.0.0.0", StringComparison.Ordinal)));
                Assert.Equal(binVersion == "7.0.0.0", diagnostics.Any(line => line.Contains("build-output assembly reference(s) were left out", StringComparison.Ordinal) && line.Contains("Issue81.Logging 7.0.0.0 under 8.0.0.0", StringComparison.Ordinal)));
            }
        }
        finally
        {
            Directory.Delete(pack, recursive: true);
        }
    }

    [Fact]
    public void GetDataFlowsAndCrypto_PackageCopyOfAFrameworkAssemblyInBin_StillCompile()
    {
        // The data-flow and crypto compilations build their references the same way; a
        // duplicate there costs the same bindings, and the diagnostic says what was replaced.
        using var tempDirectory = new TemporaryDirectory();
        var (frameworkOverride, pack) = OverrideFrameworkWithIssue81Library("8.0.0.0");
        try
        {
            using (frameworkOverride)
            {
                WriteIssue81App(tempDirectory.Path, "9.0.0.0");

                var dataFlows = DataFlowAnalyzer.Analyze(tempDirectory.Path);
                var crypto = CryptoAnalyzer.Analyze(tempDirectory.Path);

                Assert.Contains(dataFlows.Diagnostics, line => line.Contains("Issue81.Logging 9.0.0.0 over 8.0.0.0", StringComparison.Ordinal));
                Assert.Contains(crypto.Diagnostics, line => line.Contains("Issue81.Logging 9.0.0.0 over 8.0.0.0", StringComparison.Ordinal));
            }
        }
        finally
        {
            Directory.Delete(pack, recursive: true);
        }
    }

    [Fact]
    public void CompilationReferenceSet_KeepsOneReferencePerNameIndependentOfOfferOrder()
    {
        using var tempDirectory = new TemporaryDirectory();
        string Emit(string directory, string version)
        {
            var output = Path.Combine(tempDirectory.Path, directory);
            Directory.CreateDirectory(output);
            return Issue76AssemblyLoadingTests.EmitAssembly(output, "Issue81.Logging", string.Format(System.Globalization.CultureInfo.InvariantCulture, Issue81LibrarySource, version));
        }

        var framework = Emit("pack", "8.0.0.0");
        var sameAsFramework = Emit("a/bin", "8.0.0.0");
        var older = Emit("b/bin", "7.0.0.0");
        var newerFirst = Emit("c/bin", "9.0.0.0");
        var newerSecond = Emit("d/bin", "9.0.0.0");
        var created = new List<string>();
        PortableExecutableReference Create(string path)
        {
            created.Add(path);
            return MetadataReference.CreateFromFile(path);
        }

        string? Winner(params string[] offers)
        {
            var set = new CompilationReferenceSet([(framework, MetadataReference.CreateFromFile(framework))]);
            foreach (var offer in offers)
            {
                set.AddBuildOutput(offer, Create);
            }

            return Assert.Single(set.References()).FilePath;
        }

        // Highest version, then the ordinally smaller path among equal versions, whatever the
        // file system's enumeration order.
        Assert.Equal(newerFirst, Winner(sameAsFramework, older, newerFirst, newerSecond));
        Assert.Equal(newerFirst, Winner(newerSecond, newerFirst, older, sameAsFramework));
        // A framework copy keeps the name against an equal or older copy.
        Assert.Equal(framework, Winner(sameAsFramework, older));
        // A losing candidate is never turned into a reference.
        Assert.DoesNotContain(older, created);
        Assert.DoesNotContain(sameAsFramework, created);
    }

    [Fact]
    public void CompilationReferenceSet_EqualVersionsOfOtherContent_AreReportedAndCopiesStayQuiet()
    {
        // Review of #81: two build-output files of one name and assembly version with other
        // content are two assemblies, and the ordinal tie-break chose one silently.
        using var tempDirectory = new TemporaryDirectory();
        string Emit(string directory, string extra)
        {
            var output = Path.Combine(tempDirectory.Path, directory);
            Directory.CreateDirectory(output);
            return Issue76AssemblyLoadingTests.EmitAssembly(output, "Issue81.Logging", string.Format(System.Globalization.CultureInfo.InvariantCulture, Issue81LibrarySource, "9.0.0.0") + extra);
        }

        var vendorA = Emit("a/bin", string.Empty);
        var copyOfA = Path.Combine(tempDirectory.Path, "b", "bin", "Issue81.Logging.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(copyOfA)!);
        File.Copy(vendorA, copyOfA);
        var vendorB = Emit("c/bin", "public static class VendorB { }");

        List<string> Diagnostics(params string[] offers)
        {
            var set = new CompilationReferenceSet([]);
            foreach (var offer in offers)
            {
                set.AddBuildOutput(offer, path => MetadataReference.CreateFromFile(path));
            }

            Assert.Equal(vendorA, Assert.Single(set.References()).FilePath);
            return set.Diagnostics();
        }

        Assert.Empty(Diagnostics(vendorA, copyOfA));
        Assert.Empty(Diagnostics(copyOfA, vendorA));
        var expected = $"1 build-output assembly reference(s) of the same name and assembly version as a kept one, but other content, were left out of the source compilation (Issue81.Logging 9.0.0.0: kept {vendorA}, left out {vendorB}): calls into that assembly bind against the kept file.";
        // Whichever is met first: the winner and the note do not depend on the order.
        Assert.Equal([expected], Diagnostics(vendorA, vendorB));
        Assert.Equal([expected], Diagnostics(vendorB, vendorA));
    }

    [Fact]
    public void PackageUrlResolver_ProjectWithItsOwnRestoreOutputNeverBorrowsAnotherProjectsPackage()
    {
        using var tempDirectory = new TemporaryDirectory();
        var root = tempDirectory.Path;
        WriteRestoredProject(root, "OldLib", packages: [("Microsoft.AspNetCore.Http.Abstractions", "2.1.1"), ("System.Runtime", "4.3.1")]);
        // Restored, with nothing of that name in its closure: the shared framework provides it.
        WriteRestoredProject(root, "WebApp", packages: [("Serilog", "3.1.1")]);
        // Each closure kind: a lock file, a packages.config, and a deps.json beside build output.
        Directory.CreateDirectory(Path.Combine(root, "Locked"));
        File.WriteAllText(Path.Combine(root, "Locked", "Locked.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(root, "Locked", "packages.lock.json"), """{"version":1,"dependencies":{"net10.0":{"Serilog":{"type":"Direct","resolved":"3.1.1"}}}}""");
        Directory.CreateDirectory(Path.Combine(root, "Legacy"));
        File.WriteAllText(Path.Combine(root, "Legacy", "Legacy.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(root, "Legacy", "packages.config"), """<?xml version="1.0" encoding="utf-8"?><packages><package id="Serilog" version="3.1.1" /></packages>""");
        Directory.CreateDirectory(Path.Combine(root, "Built", "bin"));
        File.WriteAllText(Path.Combine(root, "Built", "Built.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(root, "Built", "bin", "Built.deps.json"), """{"targets":{},"libraries":{"Serilog/3.1.1":{"type":"package"}}}""");
        // Only direct references, no restore output: the closure is unknown and the tree answers.
        Directory.CreateDirectory(Path.Combine(root, "Unrestored"));
        File.WriteAllText(Path.Combine(root, "Unrestored", "Unrestored.csproj"), """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="Serilog" Version="3.1.1" /></ItemGroup></Project>""");

        var resolver = PackageUrlResolver.Create(root);

        foreach (var project in new[] { "WebApp", "Locked", "Legacy", "Built" })
        {
            var location = $"{project}/Program.cs";
            Assert.True(resolver.IsClosedProjectScope(location), project);
            Assert.Null(resolver.Resolve(assembly: "Microsoft.AspNetCore.Http.Abstractions, Version=10.0.0.0, Culture=neutral, PublicKeyToken=adb9793829ddae60", symbol: "Microsoft.AspNetCore.Http.RequestDelegate.Invoke(Microsoft.AspNetCore.Http.HttpContext)", location: location));
            Assert.Null(resolver.Resolve(assembly: "System.Runtime, Version=10.0.0.0", symbol: "string.Join(string?, System.Collections.Generic.IEnumerable<string?>)", location: location));
            Assert.Null(resolver.Resolve(namespaceName: "Microsoft.AspNetCore.Http", location: location));
            Assert.Equal("pkg:nuget/Serilog@3.1.1", resolver.Resolve(assembly: "Serilog", location: location));
        }

        // The project that restores the old package keeps it, and so does every record the
        // tree-wide tables answer for.
        Assert.Equal("pkg:nuget/Microsoft.AspNetCore.Http.Abstractions@2.1.1", resolver.Resolve(assembly: "Microsoft.AspNetCore.Http.Abstractions", location: "OldLib/Class1.cs"));
        Assert.Equal("pkg:nuget/Microsoft.AspNetCore.Http.Abstractions@2.1.1", resolver.Resolve(assembly: "Microsoft.AspNetCore.Http.Abstractions"));
        Assert.Equal("pkg:nuget/Microsoft.AspNetCore.Http.Abstractions@2.1.1", resolver.Resolve(assembly: "Microsoft.AspNetCore.Http.Abstractions", location: "tools/script.cs"));
        Assert.False(resolver.IsClosedProjectScope("Unrestored/Program.cs"));
        Assert.True(resolver.IsProjectScoped("Unrestored/Program.cs"));
        Assert.Equal("pkg:nuget/Microsoft.AspNetCore.Http.Abstractions@2.1.1", resolver.Resolve(assembly: "Microsoft.AspNetCore.Http.Abstractions", location: "Unrestored/Program.cs"));
        Assert.False(resolver.IsClosedProjectScope("tools/script.cs"));
        Assert.False(resolver.IsClosedProjectScope(null));
    }

    [Fact]
    public void PackageUrlResolver_FileReferenceInARestoredProject_KeepsItsPurlAndFrameworkNamesStayClosed()
    {
        using var tempDirectory = new TemporaryDirectory();
        var root = tempDirectory.Path;
        WriteRestoredProject(root, "OldLib", packages: [("Serilog", "3.1.1"), ("System.Net.Http", "4.3.4"), ("System.Runtime", "4.3.1")]);
        // Restored with nothing in its closure, and a vendored DLL by hint path (review of #82:
        // the call bound to libs/Serilog.dll and lost the purl main gave it).
        WriteRestoredProject(root, "LegacyApp", packages: []);
        File.WriteAllText(Path.Combine(root, "LegacyApp", "LegacyApp.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <Reference Include="Serilog"><HintPath>libs\Serilog.dll</HintPath></Reference>
                <Reference Include="System.Net.Http" />
              </ItemGroup>
            </Project>
            """);
        // A classic project: the MSBuild namespace, a packages.config closure, a hint path into
        // its package folder (an assembly named unlike its package), a framework reference
        // with no file and one into the framework's reference assemblies.
        Directory.CreateDirectory(Path.Combine(root, "Classic"));
        File.WriteAllText(Path.Combine(root, "Classic", "packages.config"), """<?xml version="1.0" encoding="utf-8"?><packages><package id="Microsoft.AspNet.Mvc" version="5.2.3" targetFramework="net472" /></packages>""");
        File.WriteAllText(Path.Combine(root, "Classic", "Classic.csproj"), """
            <?xml version="1.0" encoding="utf-8"?>
            <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <ItemGroup>
                <Reference Include="System.Net.Http" />
                <Reference Include="System.Runtime"><HintPath>$(MSBuildProgramFiles32)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.2\Facades\System.Runtime.dll</HintPath></Reference>
                <Reference Include="System.Web.Mvc, Version=5.2.3.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35, processorArchitecture=MSIL">
                  <HintPath>..\packages\Microsoft.AspNet.Mvc.5.2.3\lib\net45\System.Web.Mvc.dll</HintPath>
                </Reference>
              </ItemGroup>
            </Project>
            """);

        var resolver = PackageUrlResolver.Create(root);

        const string legacy = "LegacyApp/Program.cs";
        Assert.True(resolver.IsClosedProjectScope(legacy));
        Assert.Equal("pkg:nuget/Serilog@3.1.1", resolver.Resolve(assembly: "Serilog, Version=2.0.0.0, Culture=neutral, PublicKeyToken=24c2f752a8e58a10", symbol: "Serilog.Log.Information(string)", location: legacy));
        Assert.Equal("pkg:nuget/Serilog@3.1.1", resolver.Resolve(symbol: "Serilog.Log.Information(string)", location: legacy));
        // The closure still decides every name the project does not reference by file: the
        // versionless framework fallback, not OldLib's 4.3.4.
        Assert.Null(resolver.Resolve(assembly: "System.Net.Http", location: legacy));
        Assert.Equal("pkg:nuget/System.Net.Http", resolver.Resolve(namespaceName: "System.Net.Http", location: legacy));

        const string classic = "Classic/HomeController.cs";
        Assert.True(resolver.IsClosedProjectScope(classic));
        Assert.Equal("pkg:nuget/Microsoft.AspNet.Mvc@5.2.3", resolver.Resolve(assembly: "System.Web.Mvc, Version=5.2.3.0", symbol: "System.Web.Mvc.Controller.View()", location: classic));
        Assert.Null(resolver.Resolve(assembly: "System.Net.Http", location: classic));
        Assert.Null(resolver.Resolve(assembly: "System.Runtime", location: classic));

        var borrowed = Assert.Single(resolver.VersionDiagnostics, line => line.StartsWith("Project ", StringComparison.Ordinal));
        Assert.Equal(@"Project LegacyApp references assemblies by file outside its package closure, and their records take the purl other projects restore for that name; the version is not read from the file: Serilog (libs\Serilog.dll) as pkg:nuget/Serilog@3.1.1.", borrowed);
        Assert.Contains(borrowed, resolver.Diagnostics);
    }

    [Fact]
    public void PackageUrlResolver_UnionBinding_NamesProjectsWhoseClosureHasAnotherVersion()
    {
        // Review of #81: the compilation binds every project's calls to the highest copy in the
        // tree, so P1 (9.0.10) binds against P2's 11.0.0.0 copy and still reports 9.0.10.
        using var tempDirectory = new TemporaryDirectory();
        var root = tempDirectory.Path;
        WriteRestoredProject(root, "P1", packages: [("Issue81.Logging", "9.0.10")]);
        WriteRestoredProject(root, "P2", packages: [("Issue81.Logging", "11.0.0-rc.1")]);
        WriteRestoredProject(root, "P3", packages: [("Issue81.Logging", "11.0.0-rc.1"), ("Serilog", "3.1.1")]);
        var bin = Path.Combine(root, "P2", "bin", "Debug", "net10.0");
        Directory.CreateDirectory(bin);
        var bound = Issue76AssemblyLoadingTests.EmitAssembly(bin, "Issue81.Logging", string.Format(System.Globalization.CultureInfo.InvariantCulture, Issue81LibrarySource, "11.0.0.0"));

        var resolver = PackageUrlResolver.Create(root);

        const string prefix = "The source compilation references one copy of each assembly for the whole tree, so calls in some projects bind against another project's version of a package while keeping their own project's purl: ";
        // A name no project's packages ship has nothing to disagree with.
        Assert.Equal(prefix + "Issue81.Logging 11.0.0.0 from P2/bin/Debug/net10.0 (pkg:nuget/Issue81.Logging@11.0.0-rc.1) for P1 (pkg:nuget/Issue81.Logging@9.0.10).",
            resolver.UnionBindingDiagnostic([("Issue81.Logging", bound), ("Unrelated", Path.Combine(bin, "Unrelated.dll"))]));
        // A restore-cache copy names its package by its folders, in the casing a project restores.
        var cache = Path.Combine(Path.GetTempPath(), "dosai-issue81-cache", "issue81.logging", "9.0.10", "lib", "net8.0", "Issue81.Logging.dll");
        Assert.Equal(prefix + "Issue81.Logging from the restore cache (pkg:nuget/Issue81.Logging@9.0.10) for P2 (pkg:nuget/Issue81.Logging@11.0.0-rc.1), P3 (pkg:nuget/Issue81.Logging@11.0.0-rc.1).",
            resolver.UnionBindingDiagnostic([("Issue81.Logging", cache)]));
        // Every project that restores the name agrees with the bound copy.
        var serilog = Path.Combine(Path.GetTempPath(), "dosai-issue81-cache", "serilog", "3.1.1", "lib", "net8.0", "Serilog.dll");
        Assert.Null(resolver.UnionBindingDiagnostic([("Serilog", serilog)]));
        // A file whose own project says nothing is not guessed from the tree.
        Assert.Null(resolver.UnionBindingDiagnostic([("Issue81.Logging", Path.Combine(root, "libs", "Issue81.Logging.dll"))]));
    }

    [Fact]
    public void GetMethodsAndDataFlows_UnionBindingAcrossProjects_IsReported()
    {
        using var tempDirectory = new TemporaryDirectory();
        var root = tempDirectory.Path;
        WriteRestoredProject(root, "P1", packages: [("Issue81.Logging", "9.0.10")]);
        WriteRestoredProject(root, "P2", packages: [("Issue81.Logging", "11.0.0-rc.1")]);
        File.WriteAllText(Path.Combine(root, "P1", "Program.cs"), Issue81ProgramSource);
        var bin = Path.Combine(root, "P2", "bin", "Debug", "net10.0");
        Directory.CreateDirectory(bin);
        Issue76AssemblyLoadingTests.EmitAssembly(bin, "Issue81.Logging", string.Format(System.Globalization.CultureInfo.InvariantCulture, Issue81LibrarySource, "11.0.0.0"));
        const string expected = "Issue81.Logging 11.0.0.0 from P2/bin/Debug/net10.0 (pkg:nuget/Issue81.Logging@11.0.0-rc.1) for P1 (pkg:nuget/Issue81.Logging@9.0.10).";

        var slice = Depscan.Dosai.GetMethodsSlice(root);
        var dataFlows = DataFlowAnalyzer.Analyze(root);

        var call = Assert.Single(slice.MethodCalls ?? [], call => call.CalledMethod?.Contains("LogInformation", StringComparison.Ordinal) == true);
        Assert.Equal(AnalysisEvidenceKind.SourceRoslynDirect, call.EvidenceKind);
        Assert.Equal("pkg:nuget/Issue81.Logging@9.0.10", call.Purl);
        Assert.Single(slice.Diagnostics ?? [], line => line.EndsWith(expected, StringComparison.Ordinal));
        Assert.Single(dataFlows.Diagnostics, line => line.EndsWith(expected, StringComparison.Ordinal));
    }

    // System.Text.Json is a framework assembly in every host this runs on (an implementation
    // assembly beside the reference packs' facade names) and an old package of the same name.
    private const string Issue82JoinSource = """
        public static class {0}
        {{
            public static string Run() => System.Text.Json.JsonSerializer.Serialize(new[] {{ "a", "b" }});
        }}
        """;

    [Fact]
    public void GetMethods_FrameworkCallInARestoredProject_DoesNotTakeAnotherProjectsPackagePurl()
    {
        using var tempDirectory = new TemporaryDirectory();
        var root = tempDirectory.Path;
        WriteRestoredProject(root, "OldLib", packages: [("System.Text.Json", "4.7.2")]);
        WriteRestoredProject(root, "WebApp", packages: [("Serilog", "3.1.1")]);
        File.WriteAllText(Path.Combine(root, "WebApp", "Program.cs"), string.Format(System.Globalization.CultureInfo.InvariantCulture, Issue82JoinSource, "WebEndpoint"));

        var slice = Depscan.Dosai.GetMethodsSlice(root);

        const string borrowed = "pkg:nuget/System.Text.Json@4.7.2";
        var serialize = Assert.Single(slice.MethodCalls ?? [], call => call.CalledMethod?.StartsWith("System.Text.Json.JsonSerializer.Serialize", StringComparison.Ordinal) == true);
        Assert.Equal(AnalysisEvidenceKind.SourceRoslynDirect, serialize.EvidenceKind);
        // What the same project gets alone: the versionless framework fallback.
        Assert.Equal("pkg:nuget/System.Text.Json", serialize.Purl);
        Assert.DoesNotContain(slice.CallGraph?.Nodes ?? [], node => node.Purl == borrowed);
        Assert.DoesNotContain(slice.CallGraph?.Edges ?? [], edge => edge.TargetPurl == borrowed || edge.SourcePurl == borrowed);
        Assert.DoesNotContain(slice.PackageReachability ?? [], package => package.Purl == borrowed);
    }

    [Fact]
    public void GetMethods_NodeCalledFromProjectsThatDisagree_KeepsTheTreeWideAnswerAndEachSiteItsOwn()
    {
        using var tempDirectory = new TemporaryDirectory();
        var root = tempDirectory.Path;
        WriteRestoredProject(root, "OldLib", packages: [("System.Text.Json", "4.7.2")]);
        WriteRestoredProject(root, "WebApp", packages: [("Serilog", "3.1.1")]);
        File.WriteAllText(Path.Combine(root, "OldLib", "Class1.cs"), string.Format(System.Globalization.CultureInfo.InvariantCulture, Issue82JoinSource, "OldJoin"));
        File.WriteAllText(Path.Combine(root, "WebApp", "Program.cs"), string.Format(System.Globalization.CultureInfo.InvariantCulture, Issue82JoinSource, "WebEndpoint"));

        var slice = Depscan.Dosai.GetMethodsSlice(root);

        static string Portable(string? path) => (path ?? string.Empty).Replace('\\', '/');
        const string old = "pkg:nuget/System.Text.Json@4.7.2";
        var serializeCalls = (slice.MethodCalls ?? []).Where(call => call.CalledMethod?.StartsWith("System.Text.Json.JsonSerializer.Serialize", StringComparison.Ordinal) == true).ToList();
        Assert.Equal(old, Assert.Single(serializeCalls, call => Portable(call.Path).StartsWith("OldLib/", StringComparison.Ordinal)).Purl);
        Assert.Equal("pkg:nuget/System.Text.Json", Assert.Single(serializeCalls, call => Portable(call.Path).StartsWith("WebApp/", StringComparison.Ordinal)).Purl);
        var edges = (slice.CallGraph?.Edges ?? []).Where(edge => edge.TargetId.StartsWith("System.Text.Json.JsonSerializer.Serialize", StringComparison.Ordinal)).ToList();
        Assert.Equal(old, Assert.Single(edges, edge => Portable(edge.Path).StartsWith("OldLib/", StringComparison.Ordinal)).TargetPurl);
        Assert.Equal("pkg:nuget/System.Text.Json", Assert.Single(edges, edge => Portable(edge.Path).StartsWith("WebApp/", StringComparison.Ordinal)).TargetPurl);
        // Shared by two projects that disagree: the node keeps the tree-wide answer, as before.
        Assert.Equal(old, Assert.Single(slice.CallGraph?.Nodes ?? [], node => node.Id == edges[0].TargetId).Purl);
    }
}
