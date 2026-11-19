using Depscan;
using Xunit;
using Xunit.Sdk;

namespace Depscan.Tests;

/// <summary>
///     Issue #127: the base framework references follow the tree's target framework. A plain
///     modern tree binds its target-matched Microsoft.NETCore.App pack (not Dosai's own runtime),
///     a net4x tree binds the .NET Framework reference assemblies from the NuGet cache, and every
///     root reports the base it used. Fixtures are temporary directories; the net48 cases stay
///     restore-only (source and csproj, never a build), and pack-dependent cases skip where the
///     machine lacks the input.
/// </summary>
public class Issue127FrameworkBindingTests
{
    private sealed class TempDir : IDisposable
    {
        public TempDir() => Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dosai-issue127-" + Guid.NewGuid().ToString("N"));

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

    private const string ProbeSource = """
using System;
using System.Text;

namespace BindingProbe;

public static class Probe
{
    public static string Run()
    {
        var builder = new StringBuilder();
        builder.Append("answer");
        Console.WriteLine(builder.ToString());
        return builder.ToString();
    }
}
""";

    private static string Csproj(string targetFramework) => $"""
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>{targetFramework}</TargetFramework>
  </PropertyGroup>
</Project>
""";

    /// <summary>The runtime's install root, the way the reference packs are laid out under it.</summary>
    private static string DotnetRootOrSkip()
    {
        var coreLibrary = typeof(object).Assembly.Location;
        Skip.If(string.IsNullOrEmpty(coreLibrary), "The test host is single-file; the packs are located from the running runtime's directory.");
        return System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(coreLibrary)!, "..", "..", ".."));
    }

    /// <summary>The real NuGet cache root (NUGET_PACKAGES when set, else ~/.nuget/packages).</summary>
    private static string NuGetCache() =>
        Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } configured
            ? configured
            : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

    /// <summary>An exact-major Microsoft.NETCore.App pack for the target, or a skip.</summary>
    private static string CorePackOrSkip(int major)
    {
        var dotnetRoot = DotnetRootOrSkip();
        var pack = FrameworkReferences.ResolvePackDirectory(
            "Microsoft.NETCore.App", $"net{major}.0", major, [], [System.IO.Path.Combine(dotnetRoot, "packs")], [NuGetCache()], []);
        Skip.If(pack is null || !pack.Value.ExactMajor, $"no exact-major Microsoft.NETCore.App pack for net{major}.0 under {dotnetRoot} or {NuGetCache()}");
        return pack!.Value.Version;
    }

    [Fact]
    public void TargetClassification_SharesThePreprocessorVersionParse()
    {
        // The parse the preprocessor-define sets depend on, pinned once here rather than
        // re-implemented: net48 is 4.8 (major 4), never major 48.
        Assert.True(FrameworkPreprocessorDefines.TryParseVersion("48", out var net48));
        Assert.Equal((4, 8, 0), (net48.Major, net48.Minor, net48.Patch));
        Assert.True(FrameworkPreprocessorDefines.TryParseVersion("472", out var net472));
        Assert.Equal((4, 7, 2), (net472.Major, net472.Minor, net472.Patch));
        Assert.True(FrameworkPreprocessorDefines.TryParseVersion("451", out var net451));
        Assert.Equal((4, 5, 1), (net451.Major, net451.Minor, net451.Patch));
        Assert.True(FrameworkPreprocessorDefines.TryParseVersion("8.0", out var net8));
        Assert.Equal((8, 0, 0), (net8.Major, net8.Minor, net8.Patch));
        Assert.True(FrameworkPreprocessorDefines.TryParseVersion("10.0", out var net10));
        Assert.Equal((10, 0, 0), (net10.Major, net10.Minor, net10.Patch));
        Assert.False(FrameworkPreprocessorDefines.TryParseVersion("standard2.0", out _));

        Assert.Equal(FrameworkReferences.ReferencePackTargetFamily.NetFramework, FrameworkReferences.ClassifyTargetForReferencePacks("net48", out var v48));
        Assert.Equal((4, 8, 0), (v48.Major, v48.Minor, v48.Patch));
        Assert.Equal(FrameworkReferences.ReferencePackTargetFamily.NetFramework, FrameworkReferences.ClassifyTargetForReferencePacks("net472", out _));
        Assert.Equal(FrameworkReferences.ReferencePackTargetFamily.NetFramework, FrameworkReferences.ClassifyTargetForReferencePacks("net451", out _));
        Assert.Equal(FrameworkReferences.ReferencePackTargetFamily.ModernNet, FrameworkReferences.ClassifyTargetForReferencePacks("net8.0", out _));
        Assert.Equal(FrameworkReferences.ReferencePackTargetFamily.ModernNet, FrameworkReferences.ClassifyTargetForReferencePacks("net10.0", out _));
        Assert.Equal(FrameworkReferences.ReferencePackTargetFamily.ModernNet, FrameworkReferences.ClassifyTargetForReferencePacks("net10.0-windows", out _));
        Assert.Equal(FrameworkReferences.ReferencePackTargetFamily.None, FrameworkReferences.ClassifyTargetForReferencePacks("netstandard2.0", out _));
        Assert.Equal(FrameworkReferences.ReferencePackTargetFamily.None, FrameworkReferences.ClassifyTargetForReferencePacks("netcoreapp3.1", out _));
        Assert.Equal(FrameworkReferences.ReferencePackTargetFamily.None, FrameworkReferences.ClassifyTargetForReferencePacks(null, out _));
    }

    [Fact]
    public void NetFrameworkPackageSelection_ReadsTheHighestVersionLayoutFromTheNuGetCache()
    {
        using var cache = new TempDir();
        void WritePackage(string tfm, string version, params string[] directories)
        {
            foreach (var directory in directories)
            {
                Write(System.IO.Path.Combine(cache.Path, $"microsoft.netframework.referenceassemblies.{tfm}", version, "build", ".NETFramework", directory), "stub.dll", "directory shape only");
            }
        }

        WritePackage("net48", "1.0.2", "v4.8");
        WritePackage("net48", "1.0.3", "v4.8");
        WritePackage("net48", "1.0.4-rc.1", "v4.8");
        WritePackage("net472", "1.0.3", "v4.7.2");

        // net48 picks the v4.8 directory of the highest package version (a release beats the
        // prerelease of the same version; a higher version wins across), net472 its own v4.7.2.
        var net48 = FrameworkReferences.ResolveNetFrameworkReferenceAssemblies(
            "microsoft.netframework.referenceassemblies.net48", new FrameworkPreprocessorDefines.FrameworkVersion(4, 8, 0), [cache.Path]);
        Assert.NotNull(net48);
        Assert.Equal("1.0.4-rc.1", net48!.Value.Version);
        Assert.EndsWith(System.IO.Path.Combine("1.0.4-rc.1", "build", ".NETFramework", "v4.8"), net48.Value.Directory, StringComparison.Ordinal);

        var net472 = FrameworkReferences.ResolveNetFrameworkReferenceAssemblies(
            "microsoft.netframework.referenceassemblies.net472", new FrameworkPreprocessorDefines.FrameworkVersion(4, 7, 2), [cache.Path]);
        Assert.NotNull(net472);
        Assert.EndsWith(System.IO.Path.Combine("build", ".NETFramework", "v4.7.2"), net472!.Value.Directory, StringComparison.Ordinal);

        // Exact target package only: a cache holding net48 does not serve a net451 tree.
        Assert.Null(FrameworkReferences.ResolveNetFrameworkReferenceAssemblies(
            "microsoft.netframework.referenceassemblies.net451", new FrameworkPreprocessorDefines.FrameworkVersion(4, 5, 1), [cache.Path]));
        Assert.Null(FrameworkReferences.ResolveNetFrameworkReferenceAssemblies(
            "microsoft.netframework.referenceassemblies.net48", new FrameworkPreprocessorDefines.FrameworkVersion(4, 8, 0), []));

        // A version directory whose layout does not match the target version is not used.
        WritePackage("net46", "1.0.3", "v4.6.2");
        Assert.Null(FrameworkReferences.ResolveNetFrameworkReferenceAssemblies(
            "microsoft.netframework.referenceassemblies.net46", new FrameworkPreprocessorDefines.FrameworkVersion(4, 6, 1), [cache.Path]));
    }

    [Fact]
    public void Net48Tree_WithoutTheCachePackage_KeepsTheProcessWideSetWithTheAbsentNote()
    {
        using var fixture = new TempDir();
        Write(fixture.Path, "legacy.csproj", Csproj("net48"));
        Write(fixture.Path, "Probe.cs", ProbeSource);
        using (FrameworkReferences.OverrideNuGetRootForTesting(System.IO.Path.Combine(fixture.Path, "empty-cache")))
        {
            var tree = FrameworkReferences.ForTree(fixture.Path);
            Assert.Equal(FrameworkReferences.Current.References, tree.References);
            Assert.Equal(FrameworkReferences.Current.Source, tree.Source);
            Assert.NotNull(tree.Diagnostic);
            Assert.StartsWith(
                "No .NET Framework 4.8 reference assemblies (package Microsoft.NETFramework.ReferenceAssemblies.net48) were found in the NuGet cache; the base framework references come from Dosai's own runtime (",
                tree.Diagnostic, StringComparison.Ordinal);
            Assert.EndsWith(
                string.Create(System.Globalization.CultureInfo.InvariantCulture, $"), so APIs that differ between .NET Framework 4.8 and .NET {Environment.Version.Major} bind through a different base than the tree targets. Restoring the tree fetches the package."),
                tree.Diagnostic, StringComparison.Ordinal);
            Assert.Contains(FrameworkReferences.NetFrameworkPackAbsentMarker, tree.Diagnostic, StringComparison.Ordinal);
            // The smoke job's forbidden substring never appears, and the note must not read as
            // an installed-pack problem (the restore advice is legitimate only here).
            Assert.DoesNotContain("No .NET framework metadata references", tree.Diagnostic, StringComparison.Ordinal);
            Assert.DoesNotContain("No reference pack", tree.Diagnostic, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void UnresolvedCallDiagnostic_SelectsTheBranchThatMatchesTheTreeNote()
    {
        var set = (string? diagnostic) => new FrameworkReferenceSet(FrameworkReferences.Current.References, FrameworkReferences.Current.Source, diagnostic, []);

        // The net4x-absent note selects the restore advice; the netstandard and pack notes do not.
        var absent = set("No .NET Framework 4.8 reference assemblies (package Microsoft.NETFramework.ReferenceAssemblies.net48) were found in the NuGet cache; the base framework references come from Dosai's own runtime (trusted-platform-assemblies, .NET 11), so APIs that differ. Restoring the tree fetches the package.");
        var message = Depscan.Dosai.UnresolvedCallDiagnostic(3, absent);
        Assert.Contains("the .NET Framework reference assemblies are missing from the NuGet cache", message, StringComparison.Ordinal);
        Assert.Contains("restoring the tree fetches them", message, StringComparison.Ordinal);
        Assert.DoesNotContain("framework references come from installed packs", message, StringComparison.Ordinal);

        var netStandard = set("The tree's representative target 'netstandard2.0' has no reference pack; the base framework references come from Dosai's own runtime.");
        Assert.Contains("framework references come from installed packs", Depscan.Dosai.UnresolvedCallDiagnostic(3, netStandard), StringComparison.Ordinal);
        Assert.Contains("restoring or building the tree does not", Depscan.Dosai.UnresolvedCallDiagnostic(3, set("No reference pack for 'X' ...")), StringComparison.Ordinal);
        using (FrameworkReferences.OverrideForTesting(new FrameworkReferenceSet([], FrameworkReferences.NoneSource, null, [])))
        {
            Assert.Contains("Dosai resolved no framework metadata references", Depscan.Dosai.UnresolvedCallDiagnostic(3, set(null)), StringComparison.Ordinal);
        }
    }

    /// <summary>Every tree shape below carries exactly one base-naming note, of the expected shape.</summary>
    private static string SingleBaseNote(MethodsSlice result)
    {
        var notes = (result.Diagnostics ?? []).Where(diagnostic => diagnostic.Contains("Base framework references", StringComparison.Ordinal)
            || diagnostic.Contains("has no reference pack", StringComparison.Ordinal)
            || diagnostic.Contains("No TargetFramework detected", StringComparison.Ordinal)
            || diagnostic.Contains("No .NET Framework ", StringComparison.Ordinal)).ToList();
        var note = Assert.Single(notes);
        Assert.DoesNotContain("No .NET framework metadata references", note, StringComparison.Ordinal);
        return note;
    }

    private static IEnumerable<(string Called, string Assembly)> BindingAssemblies(MethodsSlice result) =>
        (result.MethodCalls ?? [])
            .Where(call => call.CalledMethod is { } called && (called.Contains("Append", StringComparison.Ordinal) || called.Contains("WriteLine", StringComparison.Ordinal)))
            .Select(call => (call.CalledMethod!, call.Assembly ?? string.Empty));

    [SkippableFact]
    public void Methods_PlainModernTree_BindsThroughTheTargetMatchedPack()
    {
        CorePackOrSkip(10);
        using var fixture = new TempDir();
        Write(fixture.Path, "plain.csproj", Csproj("net10.0"));
        Write(fixture.Path, "Probe.cs", ProbeSource);

        var result = Depscan.Dosai.GetMethodsSlice(fixture.Path);
        Assert.All(BindingAssemblies(result), binding => Assert.Contains("Version=10.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a", binding.Assembly, StringComparison.Ordinal));
        Assert.Contains(BindingAssemblies(result), binding => binding.Called.Contains("StringBuilder.Append", StringComparison.Ordinal) && binding.Assembly.StartsWith("System.Runtime, ", StringComparison.Ordinal));
        Assert.Contains(BindingAssemblies(result), binding => binding.Called.Contains("Console.WriteLine", StringComparison.Ordinal) && binding.Assembly.StartsWith("System.Console, ", StringComparison.Ordinal));

        var note = SingleBaseNote(result);
        Assert.StartsWith("Base framework references for target 'net10.0' come from the Microsoft.NETCore.App ", note, StringComparison.Ordinal);
        Assert.EndsWith(" reference pack (exact major).", note, StringComparison.Ordinal);
        Assert.DoesNotContain("No reference pack", note, StringComparison.Ordinal);
    }

    [Fact]
    public void Methods_SameMajorTree_KeepsTheRuntimeBindingAndNamesTheBase()
    {
        using var fixture = new TempDir();
        Write(fixture.Path, "app.csproj", Csproj("net11.0"));
        Write(fixture.Path, "Probe.cs", ProbeSource);

        var result = Depscan.Dosai.GetMethodsSlice(fixture.Path);
        Assert.All(BindingAssemblies(result), binding => Assert.Contains("Version=11.0.0.0", binding.Assembly, StringComparison.Ordinal));
        var note = SingleBaseNote(result);
        Assert.Equal(
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Base framework references for target 'net11.0' come from Dosai's own runtime ({FrameworkReferences.Current.Source}, .NET {Environment.Version.Major}), which matches the target's major."),
            note);
    }

    [Fact]
    public void Methods_NetStandardTree_KeepsTheRuntimeBindingAndNamesTheBase()
    {
        using var fixture = new TempDir();
        Write(fixture.Path, "lib.csproj", Csproj("netstandard2.0"));
        Write(fixture.Path, "Probe.cs", ProbeSource);

        var result = Depscan.Dosai.GetMethodsSlice(fixture.Path);
        Assert.All(BindingAssemblies(result), binding => Assert.Contains("Version=11.0.0.0", binding.Assembly, StringComparison.Ordinal));
        var note = SingleBaseNote(result);
        Assert.Equal(
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"The tree's representative target 'netstandard2.0' has no reference pack; the base framework references come from Dosai's own runtime ({FrameworkReferences.Current.Source}, .NET {Environment.Version.Major})."),
            note);
        // The base case must not read as an installed-pack problem.
        Assert.DoesNotContain("No reference pack", note, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void Methods_Net48Tree_BindsTheFrameworkReferenceAssemblies()
    {
        // Restore-only: source, csproj and a hand-written assets file; net48 is never built.
        var cache = FrameworkReferences.ResolveNetFrameworkReferenceAssemblies(
            "microsoft.netframework.referenceassemblies.net48", new FrameworkPreprocessorDefines.FrameworkVersion(4, 8, 0), [NuGetCache()]);
        Skip.If(cache is null, "microsoft.netframework.referenceassemblies.net48 is not in the NuGet cache (dotnet restore of a net48 project fetches it)");
        using var fixture = new TempDir();
        Write(fixture.Path, "legacy.csproj", Csproj("net48"));
        Write(fixture.Path, "Probe.cs", ProbeSource);
        Write(System.IO.Path.Combine(fixture.Path, "obj"), "project.assets.json", """
{"version": 3, "targets": {"net48": {}}, "libraries": {"Microsoft.NETFramework.ReferenceAssemblies.net48/1.0.3": {"type": "package"}}, "project": {"version": "1.0.0", "restore": {"projectUniqueName": "legacy", "projectName": "legacy", "projectPath": "legacy.csproj", "frameworks": {"net48": {"targetAlias": "net48"}}}}}
""");

        var result = Depscan.Dosai.GetMethodsSlice(fixture.Path);
        // mscorlib owns both symbols for net48 (the pack's System.Console.dll and the facades
        // forward there, and Roslyn reports the defining assembly).
        Assert.All(BindingAssemblies(result), binding => Assert.Equal("mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089", binding.Assembly));
        var note = SingleBaseNote(result);
        Assert.Equal(
            $"Base framework references for target 'net48' come from the .NET Framework 4.8 reference assemblies (Microsoft.NETFramework.ReferenceAssemblies.net48 {cache!.Value.Version}, NuGet cache); the process-wide runtime set fills nothing beside them.",
            note);
    }

    [Fact]
    public void Methods_MixedTree_NamesTheNet4xProjectsThatBindAgainstTheModernBase()
    {
        using var fixture = new TempDir();
        Directory.CreateDirectory(System.IO.Path.Combine(fixture.Path, "modern"));
        Directory.CreateDirectory(System.IO.Path.Combine(fixture.Path, "legacy"));
        Write(System.IO.Path.Combine(fixture.Path, "modern"), "modern.csproj", Csproj("net10.0"));
        Write(System.IO.Path.Combine(fixture.Path, "modern"), "Probe.cs", ProbeSource);
        Write(System.IO.Path.Combine(fixture.Path, "legacy"), "legacy.csproj", Csproj("net48"));
        Write(System.IO.Path.Combine(fixture.Path, "legacy"), "Probe.cs", ProbeSource);

        var result = Depscan.Dosai.GetMethodsSlice(fixture.Path);
        Assert.Contains(result.Diagnostics ?? [], diagnostic =>
            diagnostic.Contains("Base framework references follow the root's representative target 'net10.0'", StringComparison.Ordinal)
            && diagnostic.Contains("1 net4x project targeting net48 binds against that base too", StringComparison.Ordinal)
            && diagnostic.Contains("Metadata.ProjectGuardTargetFrameworks lists every project's target", StringComparison.Ordinal));
    }

    [Fact]
    public void Methods_Diagnostics_AreIdenticalAcrossRunsOfTheSameFixture()
    {
        using var fixture = new TempDir();
        Write(fixture.Path, "plain.csproj", Csproj("net10.0"));
        Write(fixture.Path, "Probe.cs", ProbeSource);

        var first = Depscan.Dosai.GetMethodsSlice(fixture.Path).Diagnostics ?? [];
        var second = Depscan.Dosai.GetMethodsSlice(fixture.Path).Diagnostics ?? [];
        Assert.Equal(first, second);
        Assert.Contains(first, diagnostic => diagnostic.Contains("Base framework references", StringComparison.Ordinal));
    }
}
