using System.Diagnostics;

namespace Depscan;

public enum BuildPreparationMode
{
    None,
    Restore,
    Build
}

/// <summary>
///     Opt-in <c>dotnet restore</c>/<c>dotnet build</c> before analysis, for trees scanned
///     without build output. Running either is executing MSBuild from the target repository
///     (props/targets imports, source generators), so both modes stay behind explicit CLI
///     flags and default off; see docs/THREAT_MODEL.md. Failure of any target is reported to
///     stderr and never fails the analysis, and each (path, mode) runs at most once per
///     process because several pipeline entry points share one scan.
/// </summary>
public static class BuildPreparation
{
    private const int RestoreTimeoutMs = 300_000;
    private const int BuildTimeoutMs = 600_000;
    // Aggregate budgets across every target in one scan: without them, a 50-project tree at
    // the per-target cap means hours of dotnet before any analysis starts.
    private const int RestoreBudgetMs = 600_000;
    private const int BuildBudgetMs = 1_200_000;
    private const int MaxProjectFiles = 50;

    private static readonly HashSet<string> Prepared = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object PreparedLock = new();

    public static void Prepare(string? path, BuildPreparationMode mode)
    {
        if (mode == BuildPreparationMode.None || string.IsNullOrWhiteSpace(path))
        {
            return;
        }
        string root;
        try
        {
            root = Path.GetFullPath(path);
        }
        catch (ArgumentException)
        {
            return;
        }
        if (!Directory.Exists(root))
        {
            return;
        }

        lock (PreparedLock)
        {
            if (!Prepared.Add($"{(int)mode}|{root}"))
            {
                return;
            }
        }

        var budgetMs = mode == BuildPreparationMode.Build ? BuildBudgetMs : RestoreBudgetMs;
        var budget = Stopwatch.StartNew();
        var targets = FindTargets(root);
        for (var index = 0; index < targets.Count; index++)
        {
            var remainingMs = budgetMs - budget.ElapsedMilliseconds;
            if (remainingMs <= 0)
            {
                Console.Error.WriteLine($"dosai: {(mode == BuildPreparationMode.Build ? "build" : "restore")} budget of {budgetMs / 1000}s exhausted; skipping {targets.Count - index} remaining target(s); analyzing the tree as-is.");
                return;
            }
            // The remaining budget caps this target's own timeout, so the aggregate budget is
            // a real wall-clock ceiling rather than a ceiling plus one full per-target timeout.
            RunDotNet(mode, root, targets[index], (int)Math.Min(mode == BuildPreparationMode.Build ? BuildTimeoutMs : RestoreTimeoutMs, remainingMs));
        }
    }

    /// <summary>
    ///     A root-level solution restores the whole graph in one invocation; otherwise each
    ///     project file outside build/output directories is restored individually, bounded so
    ///     a generated tree cannot turn one scan into hundreds of restore runs.
    /// </summary>
    private static List<string> FindTargets(string root)
    {
        try
        {
            var solutions = Directory.EnumerateFiles(root, "*.sln", SearchOption.TopDirectoryOnly)
                .Concat(Directory.EnumerateFiles(root, "*.slnx", SearchOption.TopDirectoryOnly))
                .ToList();
            if (solutions.Count > 0)
            {
                return solutions;
            }
            return SafeFileRead.EnumerateAllFilesSafe(root, "*.csproj")
                .Concat(SafeFileRead.EnumerateAllFilesSafe(root, "*.fsproj"))
                .Concat(SafeFileRead.EnumerateAllFilesSafe(root, "*.vbproj"))
                .Where(project => !IsUnderBuildDirectory(root, project))
                .Take(MaxProjectFiles)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    private static bool IsUnderBuildDirectory(string root, string filePath)
    {
        var relative = Path.GetRelativePath(root, filePath);
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment is "bin" or "obj" or "node_modules" or "artifacts" or "packages")
            {
                return true;
            }
        }
        return false;
    }

    private static void RunDotNet(BuildPreparationMode mode, string root, string target, int timeoutMs)
    {
        var verb = mode == BuildPreparationMode.Build ? "build" : "restore";
        var arguments = $"{verb} \"{target}\" -v:quiet --nologo";
        Console.Error.WriteLine($"dosai: dotnet {verb} {Path.GetFileName(target)} (--{verb} requested)");
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = arguments,
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null)
            {
                return;
            }
            // Drain both pipes while running: a verbose restore that fills either buffer
            // would otherwise block until the timeout instead of finishing.
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeoutMs))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                Console.Error.WriteLine($"dosai: dotnet {verb} {Path.GetFileName(target)} timed out after {timeoutMs / 1000}s and was stopped; analyzing the tree as-is.");
                return;
            }
            var exitCode = process.ExitCode;
            _ = stdoutTask.GetAwaiter().GetResult();
            _ = stderrTask.GetAwaiter().GetResult();
            if (exitCode != 0)
            {
                Console.Error.WriteLine($"dosai: dotnet {verb} {Path.GetFileName(target)} failed with exit code {exitCode}; analyzing the tree as-is.");
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            // A missing dotnet on PATH surfaces as Win32Exception here; either way analysis
            // continues without restore output.
            Console.Error.WriteLine($"dosai: could not run dotnet {verb} on {Path.GetFileName(target)}: {ex.Message}");
        }
    }
}
