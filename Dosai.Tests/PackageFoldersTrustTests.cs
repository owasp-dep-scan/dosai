using Depscan;
using Xunit;

namespace Depscan.Tests;

// packageFolders trust. project.assets.json is tree content, and the folders its
// packageFolders object names decide which assemblies on disk become metadata references, so
// a tree must not be able to point the resolver at arbitrary machine locations. An entry is
// accepted only when it is an absolute, non-UNC local path that sits under the scan root, or
// equals a packages folder the analyst's machine configures (NUGET_PACKAGES, the default
// global folder, the user-level NuGet.Config). Every other entry is skipped with a diagnostic that names
// it, and analysis proceeds without the folder. The same UNC refusal runs in the MSBuild
// import reader before any existence probe, because an Import path is tree content too.
public class PackageFoldersTrustTests
{
    // A UNC path shape is refused before any existence probe, so these tests need no network
    // and no Windows share: the string alone decides.
    [Fact]
    public void RestoreCache_PackageFolders_UncEntry_IsSkippedWithDiagnostic()
    {
        using var tempDirectory = new TemporaryDirectory();
        var root = Path.Combine(tempDirectory.Path, "src");
        WriteAssets(root, "//server/share/packages");

        var references = NuGetRestoreCache.GetReferencePaths(root);

        Assert.Empty(references);
        Assert.Contains(NuGetRestoreCache.GetDiagnostics(root), diagnostic =>
            diagnostic.Contains("//server/share/packages", StringComparison.Ordinal) &&
            diagnostic.Contains("UNC", StringComparison.Ordinal));
    }

    // The dll really exists in the outside folder: the refusal must be the trust rule, not a
    // missing file, so the old behavior that resolved it is what this test pins down.
    [Fact]
    public void RestoreCache_PackageFolders_AbsoluteFolderOutsideRoot_IsSkippedEvenWhenTheDllExists()
    {
        using var tempDirectory = new TemporaryDirectory();
        var root = Path.Combine(tempDirectory.Path, "src");
        var outsideCache = Path.Combine(tempDirectory.Path, "outside-cache");
        PlaceCachedDll(outsideCache);
        WriteAssets(root, outsideCache.Replace('\\', '/'));

        var references = NuGetRestoreCache.GetReferencePaths(root);

        Assert.DoesNotContain(references, path => path.Contains("outside-cache", StringComparison.Ordinal));
        Assert.Contains(NuGetRestoreCache.GetDiagnostics(root), diagnostic =>
            diagnostic.Contains("outside-cache", StringComparison.Ordinal) &&
            diagnostic.Contains("outside the scanned tree", StringComparison.Ordinal));
    }

    [Fact]
    public void RestoreCache_PackageFolders_RelativeEntry_IsSkippedWithDiagnostic()
    {
        using var tempDirectory = new TemporaryDirectory();
        var root = Path.Combine(tempDirectory.Path, "src");
        WriteAssets(root, "vendor/packages");

        var references = NuGetRestoreCache.GetReferencePaths(root);

        Assert.Empty(references);
        Assert.Contains(NuGetRestoreCache.GetDiagnostics(root), diagnostic =>
            diagnostic.Contains("vendor/packages", StringComparison.Ordinal) &&
            diagnostic.Contains("not absolute", StringComparison.Ordinal));
    }

    // An Import Project path is tree content as well: a UNC-shaped one is refused before any
    // existence probe, with a diagnostic that says so, instead of the generic outside-tree skip.
    [Fact]
    public void MsBuildImports_UncImportPath_IsSkippedWithUncDiagnostic()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "Lib.csproj"), """
<Project Sdk="Microsoft.NET.Sdk">
  <Import Project="\\server\share\props.props" />
</Project>
""");

        var diagnostics = MsBuildImports.DiagnosticsFor(tempDirectory.Path);

        Assert.Contains(diagnostics, diagnostic =>
            diagnostic.Contains("props.props", StringComparison.Ordinal) &&
            diagnostic.Contains("UNC", StringComparison.Ordinal));
    }

    // A cache the tree carries itself (a checked-in packages folder, a repositoryPath under
    // the root) stays trusted: the rule must not tighten past the scan root.
    [Fact]
    public void RestoreCache_PackageFolders_UnderScanRoot_IsAccepted()
    {
        using var tempDirectory = new TemporaryDirectory();
        var root = Path.Combine(tempDirectory.Path, "src");
        var cache = Path.Combine(root, "packages-cache");
        PlaceCachedDll(cache);
        WriteAssets(root, cache.Replace('\\', '/'));

        var references = NuGetRestoreCache.GetReferencePaths(root);

        Assert.Contains(references, path => path.EndsWith("Sample.Lib.dll", StringComparison.Ordinal));
        Assert.DoesNotContain(NuGetRestoreCache.GetDiagnostics(root), diagnostic => diagnostic.Contains("was skipped", StringComparison.Ordinal));
    }

    // A NuGet.config inside the scanned tree is tree content: the fallback folders it names
    // are not machine configuration, so one outside the tree is refused like any other.
    [Fact]
    public void RestoreCache_PackageFolders_FallbackFolderFromTheTreesNuGetConfig_IsRefused()
    {
        using var tempDirectory = new TemporaryDirectory();
        var root = Path.Combine(tempDirectory.Path, "src");
        var fallbackCache = Path.Combine(tempDirectory.Path, "fallback-cache");
        PlaceCachedDll(fallbackCache);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "NuGet.config"), $"""
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <fallbackPackageFolders>
    <add key="shared" value="{fallbackCache.Replace('\\', '/')}" />
  </fallbackPackageFolders>
</configuration>
""");
        WriteAssets(root, fallbackCache.Replace('\\', '/'));

        var references = NuGetRestoreCache.GetReferencePaths(root);

        Assert.Empty(references);
        Assert.Contains(NuGetRestoreCache.GetDiagnostics(root), diagnostic => diagnostic.Contains("was skipped", StringComparison.Ordinal));
    }

    // The user-level NuGet.Config is the analyst's configuration: its globalPackagesFolder and
    // fallback folders are read, relative values against the config's own directory.
    [Fact]
    public void ReadUserConfigPackageFolders_ReadsGlobalAndFallbackFolders()
    {
        using var tempDirectory = new TemporaryDirectory();
        var config = Path.Combine(tempDirectory.Path, "NuGet.Config");
        File.WriteAllText(config, """
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <config>
    <add key="globalPackagesFolder" value="relocated" />
    <add key="http_proxy" value="ignored" />
  </config>
  <fallbackPackageFolders>
    <add key="offline" value="/opt/offline-packages" />
  </fallbackPackageFolders>
</configuration>
""");

        var folders = NuGetRestoreCache.ReadUserConfigPackageFolders(config);

        Assert.Equal([Path.Combine(tempDirectory.Path, "relocated"), "/opt/offline-packages"], folders);
    }

    // NUGET_PACKAGES is the operator's own configuration, so the folder it names is trusted
    // from any scan root. The variable is restored in finally; trusting an extra folder can
    // change only trees whose assets name it, and no concurrent test's tree does.
    [Fact]
    public void RestoreCache_PackageFolders_NuGetPackagesEnvironmentFolder_IsAccepted()
    {
        using var tempDirectory = new TemporaryDirectory();
        var root = Path.Combine(tempDirectory.Path, "src");
        var environmentCache = Path.Combine(tempDirectory.Path, "environment-cache");
        PlaceCachedDll(environmentCache);
        WriteAssets(root, environmentCache.Replace('\\', '/'));
        var previous = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        Environment.SetEnvironmentVariable("NUGET_PACKAGES", environmentCache);
        try
        {
            var references = NuGetRestoreCache.GetReferencePaths(root);

            Assert.Contains(references, path => path.EndsWith("Sample.Lib.dll", StringComparison.Ordinal));
            Assert.DoesNotContain(NuGetRestoreCache.GetDiagnostics(root), diagnostic => diagnostic.Contains("was skipped", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable("NUGET_PACKAGES", previous);
        }
    }

    // The execution-context override fills the same role as NUGET_PACKAGES for tests that
    // model an outside-tree global packages folder (the restore tests of DosaiTests use it).
    [Fact]
    public void RestoreCache_PackageFolders_ConfiguredRootOverride_IsAccepted()
    {
        using var tempDirectory = new TemporaryDirectory();
        var root = Path.Combine(tempDirectory.Path, "src");
        var configuredCache = Path.Combine(tempDirectory.Path, "configured-cache");
        PlaceCachedDll(configuredCache);
        WriteAssets(root, configuredCache.Replace('\\', '/'));
        using (NuGetRestoreCache.OverrideTrustedPackageFoldersForTesting(configuredCache))
        {
            var references = NuGetRestoreCache.GetReferencePaths(root);

            Assert.Contains(references, path => path.EndsWith("Sample.Lib.dll", StringComparison.Ordinal));
            Assert.DoesNotContain(NuGetRestoreCache.GetDiagnostics(root), diagnostic => diagnostic.Contains("was skipped", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void PackageFolderClassification_BackslashUncForm_IsRefused()
    {
        using var tempDirectory = new TemporaryDirectory();
        Assert.Equal(NuGetRestoreCache.PackageFolderRefusal.UncPath, NuGetRestoreCache.ClassifyPackageFolder(@"\\server\share\packages", Path.Combine(tempDirectory.Path, "src")));
    }

    [Fact]
    public void PackageFolderClassification_SlashUncForm_IsRefused()
    {
        using var tempDirectory = new TemporaryDirectory();
        Assert.Equal(NuGetRestoreCache.PackageFolderRefusal.UncPath, NuGetRestoreCache.ClassifyPackageFolder("//server/share/packages", Path.Combine(tempDirectory.Path, "src")));
    }

    [Fact]
    public void PackageFolderClassification_RelativeEntry_IsRefused()
    {
        using var tempDirectory = new TemporaryDirectory();
        Assert.Equal(NuGetRestoreCache.PackageFolderRefusal.NotAbsolute, NuGetRestoreCache.ClassifyPackageFolder("vendor/packages", Path.Combine(tempDirectory.Path, "src")));
    }

    [Fact]
    public void PackageFolderClassification_AbsoluteEntryWithInvalidCharacters_IsRefused()
    {
        using var tempDirectory = new TemporaryDirectory();
        Assert.Equal(NuGetRestoreCache.PackageFolderRefusal.UnresolvablePath, NuGetRestoreCache.ClassifyPackageFolder("/packages\0cache", Path.Combine(tempDirectory.Path, "src")));
    }

    [Fact]
    public void PackageFolderClassification_UnderRoot_IsTrusted()
    {
        using var tempDirectory = new TemporaryDirectory();
        var root = Path.Combine(tempDirectory.Path, "src");
        Assert.Equal(NuGetRestoreCache.PackageFolderRefusal.None, NuGetRestoreCache.ClassifyPackageFolder(Path.Combine(root, "packages"), root));
    }

    [Fact]
    public void PackageFolderClassification_DefaultGlobalPackagesFolder_IsTrusted()
    {
        using var tempDirectory = new TemporaryDirectory();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.NotEqual(string.Empty, home);
        Assert.Equal(NuGetRestoreCache.PackageFolderRefusal.None, NuGetRestoreCache.ClassifyPackageFolder(Path.Combine(home, ".nuget", "packages"), Path.Combine(tempDirectory.Path, "src")));
    }

    [Fact]
    public void PackageFolderClassification_OutsideRoot_IsRefusedUnlessConfigured()
    {
        using var tempDirectory = new TemporaryDirectory();
        var root = Path.Combine(tempDirectory.Path, "src");
        var outside = Path.Combine(tempDirectory.Path, "machine-cache");
        Assert.Equal(NuGetRestoreCache.PackageFolderRefusal.OutsideTrustedRoots, NuGetRestoreCache.ClassifyPackageFolder(outside, root));
        Assert.Equal(NuGetRestoreCache.PackageFolderRefusal.None, NuGetRestoreCache.ClassifyPackageFolder(outside, root, [outside]));
    }

    // The directory-chaining functions walk up from a start directory the tree names: a UNC
    // start must resolve nothing rather than probe the share, and must not fall back to the
    // importing file's own directory either (props.props sits next to the project on purpose:
    // a fallback would follow it).
    [Fact]
    public void MsBuildImports_GetPathOfFileAbove_UncStartDirectory_ResolvesNothing()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(tempDirectory.Path, "props.props"), "<Project />");
        var projectFile = Path.Combine(tempDirectory.Path, "Lib.csproj");
        File.WriteAllText(projectFile, """
<Project Sdk="Microsoft.NET.Sdk">
  <Import Project="$([MSbuild]::GetPathOfFileAbove('props.props', '\\server\share\dir'))" />
</Project>
""");

        var segments = MsBuildImports.Expand(tempDirectory.Path, projectFile);

        // Only the project's own text: the import resolved nothing, so no segment of the
        // props file beside it was followed.
        Assert.All(segments, segment => Assert.Equal(projectFile, segment.File));
        Assert.DoesNotContain(segments, segment => segment.Content.Contains("props.props", StringComparison.Ordinal));
    }

    /// <summary>One assets file with the given packageFolders entries, declaring one package library.</summary>
    private static void WriteAssets(string root, params string[] packageFolderEntries)
    {
        Directory.CreateDirectory(Path.Combine(root, "obj"));
        var folders = string.Join(", ", packageFolderEntries.Select(entry => $"\"{JsonEscape(entry)}\": {{}}"));
        File.WriteAllText(Path.Combine(root, "obj", "project.assets.json"), $$"""
{
  "version": 3,
  "project": { "version": "1.0.0" },
  "packageFolders": { {{folders}} },
  "libraries": {
    "Sample.Lib/1.0.0": { "type": "package", "path": "sample.lib/1.0.0" }
  },
  "targets": {
    "net8.0": {
      "Sample.Lib/1.0.0": {
        "type": "package",
        "compile": { "lib/net8.0/Sample.Lib.dll": {} }
      }
    }
  }
}
""");
    }

    /// <summary>The package DLL in the layout the resolver combines library path and asset into.</summary>
    private static void PlaceCachedDll(string cacheRoot)
    {
        var directory = Path.Combine(cacheRoot, "sample.lib", "1.0.0", "lib", "net8.0");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "Sample.Lib.dll"), []);
    }

    private static string JsonEscape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dosai-packagefolders-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

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
}
