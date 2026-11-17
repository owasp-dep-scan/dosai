using Depscan;
using System.Runtime.Versioning;
using Xunit;

namespace Dosai.Tests;

// Child-process hygiene for the executables Dosai itself starts (dotnet for
// `dotnet --list-runtimes` and opt-in restore/build, Rscript for the R native parser). Each
// test plants a fake executable where a scanned tree could supply one and asserts it never
// runs, and the fakes delegate to the real executable for every other invocation. A partial
// of DosaiTests like DebugLoggingTests: these tests and the R-parser timeout test change the
// process-wide PATH and DOSAI_R_PARSE_TIMEOUT_MS, which is only safe when they never run in
// parallel.
public partial class DosaiTests
{
    [SupportedOSPlatform("Linux")]
    [SupportedOSPlatform("macOS")]
    [SkippableFact]
    public void BuildPreparation_NeverRunsDotnetFoundInsideTheScannedTree()
    {
        Skip.If(OperatingSystem.IsWindows(), "The planted executable needs the Unix exec bit.");

        using var tree = new TemporaryDirectory();
        using var markers = new TemporaryDirectory();
        // The stamp ties the marker to this test's own project file: a concurrently running
        // fixture build that hits the planted PATH entry delegates to the real dotnet without
        // leaving a marker, so only restore running the planted executable fails the test.
        var stamp = Guid.NewGuid().ToString("N");
        var marker = Path.Combine(markers.Path, "dotnet-was-run");
        WriteFakeExecutable(Path.Combine(tree.Path, "dotnet"), $"""
case "$*" in *{stamp}*) printf ran > '{marker}';; esac
exec '{RealDotnet()}' "$@"
""");
        // A project that fails to restore keeps the child quick; only the choice of the
        // executable is under test here, not a successful restore.
        File.WriteAllText(Path.Combine(tree.Path, $"broken-{stamp}.csproj"), "<Project");

        var originalPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", tree.Path + Path.PathSeparator + originalPath);
        try
        {
            BuildPreparation.Prepare(tree.Path, BuildPreparationMode.Restore);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }

        Assert.False(File.Exists(marker), "dotnet restore started the dotnet found inside the scanned tree.");
    }

    [SupportedOSPlatform("Linux")]
    [SupportedOSPlatform("macOS")]
    [SkippableFact]
    public void Rscript_PlantedInsideTheScanRootIsNeverRun()
    {
        Skip.If(OperatingSystem.IsWindows(), "The planted executable needs the Unix exec bit.");

        using var tree = new TemporaryDirectory();
        using var markers = new TemporaryDirectory();
        // The stamp links the marker to this test's own R file: the fake delegates to the real
        // Rscript for anything else, so a concurrently running R test that happens to hit the
        // planted PATH entry neither breaks nor trips this assertion.
        var stamp = Guid.NewGuid().ToString("N");
        var marker = Path.Combine(markers.Path, "rscript-was-run");
        var real = FindOnCurrentPath("Rscript");
        WriteFakeExecutable(Path.Combine(tree.Path, "Rscript"), $"""
case "$*" in *{stamp}*) printf ran > '{marker}';; esac
{(real is null ? "exit 0" : $"exec '{real}' \"$@\"")}
""");
        File.WriteAllText(Path.Combine(tree.Path, $"analysis-{stamp}.R"), "f <- function(x) { x + 1 }\n");

        var originalPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", tree.Path + Path.PathSeparator + originalPath);
        (List<Depscan.Method> Methods, List<Depscan.Dependency> Dependencies, List<Depscan.MethodCalls> Calls) result;
        try
        {
            result = LanguageFrontendAnalyzer.GetMethods(tree.Path);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }

        // Both the native parser and the fallback line parser contribute a "script" method, so
        // this holds whichever one ran; the assertion under test is the one below.
        Assert.Contains(result.Methods, method => method.Name == "script");
        Assert.False(File.Exists(marker), "The R native parser started the Rscript found inside the scanned tree.");
    }

    [SupportedOSPlatform("Linux")]
    [SupportedOSPlatform("macOS")]
    [SkippableFact]
    public void Rscript_IsLaunchedWithVanilla()
    {
        Skip.If(OperatingSystem.IsWindows(), "The planted executable needs the Unix exec bit.");

        // The fake sits on an absolute PATH entry outside the scanned tree, which is exactly
        // where Rscript is meant to come from; what it records is the argument list. Every
        // invocation overwrites the recording, and each of them passes through the same launch
        // site, so a concurrent R analysis during the PATH window cannot change the outcome.
        using var tree = new TemporaryDirectory();
        using var tools = new TemporaryDirectory();
        var recordedArguments = Path.Combine(tools.Path, "args");
        var real = FindOnCurrentPath("Rscript");
        WriteFakeExecutable(Path.Combine(tools.Path, "Rscript"), $"""
printf '%s\n' "$@" > '{recordedArguments}'
{(real is null ? "exit 0" : $"exec '{real}' \"$@\"")}
""");
        File.WriteAllText(Path.Combine(tree.Path, "analysis.R"), "f <- function(x) { x + 1 }\n");

        var originalPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", tools.Path + Path.PathSeparator + originalPath);
        try
        {
            LanguageFrontendAnalyzer.GetMethods(tree.Path);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }

        Assert.True(File.Exists(recordedArguments), "The R native parser did not start Rscript from the absolute PATH entry.");
        Assert.Contains("--vanilla", File.ReadAllText(recordedArguments), StringComparison.Ordinal);
    }

    [SupportedOSPlatform("Linux")]
    [SupportedOSPlatform("macOS")]
    [SkippableFact]
    public void Rscript_SlowParserOutsideTheScanRoot_TimesOutAndFallsBackToTheRegexFrontend()
    {
        Skip.If(OperatingSystem.IsWindows(), "The planted executable needs the Unix exec bit.");

        using var tree = new TemporaryDirectory();
        using var tools = new TemporaryDirectory();
        // A hung Rscript on a legitimate absolute PATH entry outside the scanned tree: the
        // parser timeout must still cut it short and the regex frontend take over.
        WriteFakeExecutable(Path.Combine(tools.Path, "Rscript"), "sleep 5");
        File.WriteAllText(Path.Combine(tree.Path, "app.R"), """
run <- function(input) {
  print(input)
}
""");

        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var originalTimeout = Environment.GetEnvironmentVariable("DOSAI_R_PARSE_TIMEOUT_MS");
        (List<Depscan.Method> Methods, List<Depscan.Dependency> Dependencies, List<Depscan.MethodCalls> Calls) result;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            Environment.SetEnvironmentVariable("PATH", tools.Path + Path.PathSeparator + originalPath);
            Environment.SetEnvironmentVariable("DOSAI_R_PARSE_TIMEOUT_MS", "100");
            result = LanguageFrontendAnalyzer.GetMethods(tree.Path);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Environment.SetEnvironmentVariable("DOSAI_R_PARSE_TIMEOUT_MS", originalTimeout);
        }
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"R parser timeout should be enforced before fallback, but took {stopwatch.Elapsed}.");
        Assert.Contains(result.Methods, method => method is { Name: "run", Module: "LanguageFrontend" });
    }

    [Fact]
    public void ResolveFromPath_SkipsRelativeEmptyAndDotEntries()
    {
        using var tools = new TemporaryDirectory();
        var tool = Path.Combine(tools.Path, "dosai-probe-tool");
        File.WriteAllText(tool, string.Empty);

        // A relative entry that would resolve to an existing directory holding the tool, if
        // relative entries were searched at all: resolving them depends on the working
        // directory at exec time, which is exactly what the resolver must not rely on.
        var relative = "dosai-relative-path-entry";
        Directory.CreateDirectory(relative);
        File.WriteAllText(Path.Combine(relative, "dosai-probe-tool"), string.Empty);
        try
        {
            Assert.Null(SafeProcess.ResolveFromPath(
                relative + Path.PathSeparator + "." + Path.PathSeparator + string.Empty,
                scanRoot: null,
                ["dosai-probe-tool"]));
        }
        finally
        {
            Directory.Delete(relative, recursive: true);
        }

        // The same tool on an absolute entry outside every scan root is found, even with the
        // unusable entries ahead of it.
        var pathValue = "." + Path.PathSeparator + string.Empty + Path.PathSeparator + relative + Path.PathSeparator + tools.Path;
        Assert.Equal(tool, SafeProcess.ResolveFromPath(pathValue, scanRoot: null, ["dosai-probe-tool"]));
    }

    [Fact]
    public void ResolveFromPath_SkipsEntriesInsideTheScanRoot()
    {
        using var tree = new TemporaryDirectory();
        using var outside = new TemporaryDirectory();
        var planted = Path.Combine(tree.Path, "tools");
        Directory.CreateDirectory(planted);
        File.WriteAllText(Path.Combine(planted, "dosai-probe-tool"), string.Empty);
        var pathValue = planted + Path.PathSeparator + outside.Path;

        Assert.Null(SafeProcess.ResolveFromPath(pathValue, tree.Path, ["dosai-probe-tool"]));
        // The scan-root rule is about the root being scanned: with a different root the same
        // entry is an ordinary PATH directory again.
        Assert.Equal(Path.Combine(planted, "dosai-probe-tool"), SafeProcess.ResolveFromPath(pathValue, outside.Path, ["dosai-probe-tool"]));
    }

    [Fact]
    public void ResolveDotnet_PrefersDotnetHostPath()
    {
        using var directory = new TemporaryDirectory();
        var host = Path.Combine(directory.Path, "dotnet-host");
        File.WriteAllText(host, string.Empty);
        var runningHost = Path.Combine(directory.Path, "dotnet");
        File.WriteAllText(runningHost, string.Empty);

        // The explicit operator choice wins even when it points into the scan root: the
        // outside-root rule governs DOTNET_ROOT and PATH, not DOTNET_HOST_PATH.
        Assert.Equal(host, SafeProcess.ResolveDotnet(host, runningHost, directory.Path, directory.Path, scanRoot: directory.Path));
    }

    [Fact]
    public void ResolveDotnet_UsesTheRunningHostWhenItIsDotnet()
    {
        using var directory = new TemporaryDirectory();
        var runningHost = Path.Combine(directory.Path, "dotnet");
        File.WriteAllText(runningHost, string.Empty);
        var other = Path.Combine(directory.Path, "tools");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "dotnet"), string.Empty);

        Assert.Equal(runningHost, SafeProcess.ResolveDotnet(hostPath: null, runningHost, dotnetRoot: null, other, scanRoot: null));

        // A process that is not the dotnet host (the shipped dosai apphost, say) must not be
        // mistaken for one; resolution continues with DOTNET_ROOT and PATH.
        var appHost = Path.Combine(directory.Path, "dosai");
        File.WriteAllText(appHost, string.Empty);
        Assert.Equal(Path.Combine(other, "dotnet"), SafeProcess.ResolveDotnet(hostPath: null, appHost, dotnetRoot: null, other, scanRoot: null));
    }

    [Fact]
    public void ResolveDotnet_UsesDotnetRootOutsideTheScanRoot()
    {
        using var tree = new TemporaryDirectory();
        using var directory = new TemporaryDirectory();
        var inside = Path.Combine(tree.Path, "sdk");
        Directory.CreateDirectory(inside);
        File.WriteAllText(Path.Combine(inside, "dotnet"), string.Empty);
        var onPath = Path.Combine(directory.Path, "bin");
        Directory.CreateDirectory(onPath);
        File.WriteAllText(Path.Combine(onPath, "dotnet"), string.Empty);

        Assert.Equal(Path.Combine(onPath, "dotnet"), SafeProcess.ResolveDotnet(hostPath: null, processPath: null, dotnetRoot: inside, onPath, tree.Path));
        Assert.Equal(Path.Combine(inside, "dotnet"), SafeProcess.ResolveDotnet(hostPath: null, processPath: null, dotnetRoot: inside, onPath, scanRoot: null));
    }

    [Fact]
    public void ResolveDotnet_FindsDotnetOnPathOutsideTheScanRoot()
    {
        using var tree = new TemporaryDirectory();
        using var directory = new TemporaryDirectory();
        var planted = Path.Combine(tree.Path, "dotnet");
        File.WriteAllText(planted, string.Empty);
        var real = Path.Combine(directory.Path, "dotnet");
        File.WriteAllText(real, string.Empty);
        var pathValue = tree.Path + Path.PathSeparator + directory.Path;

        Assert.Equal(real, SafeProcess.ResolveDotnet(hostPath: null, processPath: null, dotnetRoot: null, pathValue, tree.Path));
        // Without a root to exclude, an absolute PATH entry is trusted like the operator's
        // own PATH; the relative-entry rules still apply.
        Assert.Equal(planted, SafeProcess.ResolveDotnet(hostPath: null, processPath: null, dotnetRoot: null, pathValue, scanRoot: null));
    }

    [Fact]
    public void NeutralWorkingDirectory_IsAnExistingDirectory()
    {
        var neutral = SafeProcess.NeutralWorkingDirectory();
        Assert.NotNull(neutral);
        Assert.True(Directory.Exists(neutral));
    }

    [SkippableFact]
    public void GetDotnetSharedRuntimeRoots_StillListsInstalledSharedRuntimes()
    {
        Skip.If(SafeProcess.ResolveDotnet(scanRoot: null) is null, "No absolute dotnet executable is resolvable on this machine.");

        Assert.Contains(Depscan.Dosai.GetDotnetSharedRuntimeRoots(), root => Directory.Exists(root));
    }

    /// <summary>The absolute path of a real dotnet executable, for the fakes to delegate to.</summary>
    private static string RealDotnet()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(processPath) && Path.GetFileName(processPath) is "dotnet" or "dotnet.exe" && File.Exists(processPath))
        {
            return processPath;
        }
        var found = FindOnCurrentPath("dotnet");
        Assert.NotNull(found);
        return found!;
    }

    private static string? FindOnCurrentPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator))
        {
            if (!Path.IsPathRooted(directory))
            {
                continue;
            }
            string full;
            try
            {
                full = Path.GetFullPath(directory);
            }
            catch (ArgumentException)
            {
                continue;
            }
            if (!Directory.Exists(full))
            {
                continue;
            }
            var candidate = Path.Combine(full, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    [SupportedOSPlatform("Linux")]
    [SupportedOSPlatform("macOS")]
    private static void WriteFakeExecutable(string path, string body)
    {
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }
}
