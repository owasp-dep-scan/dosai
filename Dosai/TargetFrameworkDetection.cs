using System.Text.Json;

namespace Depscan;

/// <summary>
///     Resolves the effective target framework(s) of a scan root, best-effort, by reading
///     project files - <c>*.csproj</c>/<c>*.fsproj</c>/<c>*.vbproj</c> and
///     <c>Directory.Build.props</c>/<c>Directory.Build.targets</c> - via regex over file text,
///     mirroring <see cref="CSharpSourceParser.DetectImplicitUsings" /> (no MSBuild evaluation,
///     no added dependency). For assembly-only trees without a project file it falls back to
///     <c>*.runtimeconfig.json</c> (<c>runtimeOptions.tfm</c> or the framework version).
///     Detection is cached per resolved root under a lock; unreadable or absent files simply
///     contribute nothing, and an empty result means the caller falls back to the default
///     latest-modern-net define set. OS/platform-suffixed TFMs (<c>net8.0-windows</c>) are
///     recorded whole; <see cref="FrameworkPreprocessorDefines" /> strips the suffix for
///     symbol purposes.
/// </summary>
internal static class TargetFrameworkDetection
{
    private static readonly Lock DetectionLock = new();
    private static readonly Dictionary<string, IReadOnlyList<string>> DetectedByRoot = new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> Detect(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return [];
        }

        string root;
        try
        {
            root = Path.GetFullPath(path);
        }
        catch (ArgumentException)
        {
            return [];
        }

        lock (DetectionLock)
        {
            if (DetectedByRoot.TryGetValue(root, out var cached))
            {
                return cached;
            }

            var detected = DetectNoCache(root);
            DetectedByRoot[root] = detected;
            return detected;
        }
    }

    private static IReadOnlyList<string> DetectNoCache(string root)
    {
        var detected = new List<string>();
        try
        {
            var projectFiles = SafeFileRead.EnumerateAllFilesSafe(root, "*.csproj")
                .Concat(SafeFileRead.EnumerateAllFilesSafe(root, "*.fsproj"))
                .Concat(SafeFileRead.EnumerateAllFilesSafe(root, "*.vbproj"))
                .Concat(SafeFileRead.EnumerateAllFilesSafe(root, "Directory.Build.props"))
                .Concat(SafeFileRead.EnumerateAllFilesSafe(root, "Directory.Build.targets"));
            foreach (var projectFile in projectFiles.Where(file => !IsUnderBuildDirectory(root, file)))
            {
                CollectTargetFrameworks(projectFile, detected);
            }

            // Assembly-only trees: a built output directory carries no project file, but a
            // runtimeconfig names the framework the app runs on.
            if (detected.Count == 0)
            {
                // A runtimeconfig only exists in build output, so unlike project files it is read
                // from bin/obj rather than skipped there.
                foreach (var runtimeConfig in SafeFileRead.EnumerateAllFilesSafe(root, "*.runtimeconfig.json"))
                {
                    if (TryDetectFromRuntimeConfig(runtimeConfig, out var targetFramework))
                    {
                        detected.Add(targetFramework);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Best-effort detection; analysis continues with the default modern-net defines.
        }

        return detected.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    // `TargetFrameworks?` matches both TargetFramework and TargetFrameworks while rejecting
    // TargetFrameworkVersion (the next character after the name is neither `s` nor `>`).
    private static readonly System.Text.RegularExpressions.Regex TargetFrameworkRegex =
        new(@"<TargetFrameworks?\s*>([^<]+)</TargetFrameworks?\s*>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    // Classic, non-SDK projects carry `<TargetFrameworkVersion>v4.7.2</TargetFrameworkVersion>`
    // instead of a TFM. Without this they detect as nothing and fall back to the modern-net
    // defines, which is the one case where the fallback is knowably wrong.
    private static readonly System.Text.RegularExpressions.Regex TargetFrameworkVersionRegex =
        new(@"<TargetFrameworkVersion\s*>\s*v?([0-9]+(?:\.[0-9]+)*)\s*</TargetFrameworkVersion\s*>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static void CollectTargetFrameworks(string projectFile, List<string> detected)
    {
        if (!SafeFileRead.TryReadAllText(projectFile, out var content))
        {
            return;
        }

        foreach (var match in TargetFrameworkRegex.Matches(content).Cast<System.Text.RegularExpressions.Match>())
        {
            foreach (var value in match.Groups[1].Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (IsPlausibleTargetFramework(value))
                {
                    detected.Add(value);
                }
            }
        }

        foreach (var match in TargetFrameworkVersionRegex.Matches(content).Cast<System.Text.RegularExpressions.Match>())
        {
            // `v4.7.2` is the `net472` moniker: digits joined, separators dropped.
            var moniker = "net" + match.Groups[1].Value.Replace(".", string.Empty, StringComparison.Ordinal);
            if (IsPlausibleTargetFramework(moniker))
            {
                detected.Add(moniker);
            }
        }
    }

    private static bool IsUnderBuildDirectory(string root, string filePath)
    {
        foreach (var segment in Path.GetRelativePath(root, filePath).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment is "bin" or "obj" or "node_modules" or "artifacts" or "packages")
            {
                return true;
            }
        }

        return false;
    }

    // Accepts `net8.0`, `net8.0-windows`, `net472`, `netstandard2.0`, `netcoreapp3.1`; rejects
    // MSBuild property references and junk that happens to sit inside the element.
    private static bool IsPlausibleTargetFramework(string value)
    {
        if (value.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase))
        {
            return value.Length > 11 && char.IsAsciiDigit(value[11]);
        }

        if (value.StartsWith("netcoreapp", StringComparison.OrdinalIgnoreCase))
        {
            return value.Length > 10 && char.IsAsciiDigit(value[10]);
        }

        return value.Length > 3 && value.StartsWith("net", StringComparison.OrdinalIgnoreCase) && char.IsAsciiDigit(value[3]);
    }

    private static bool TryDetectFromRuntimeConfig(string runtimeConfigFile, out string targetFramework)
    {
        targetFramework = string.Empty;
        if (!SafeFileRead.TryReadAllText(runtimeConfigFile, out var content))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            var runtimeOptions = document.RootElement.TryGetProperty("runtimeOptions", out var options) ? options : default;
            if (runtimeOptions.ValueKind == JsonValueKind.Object)
            {
                if (runtimeOptions.TryGetProperty("tfm", out var tfm) && tfm.ValueKind == JsonValueKind.String && IsPlausibleTargetFramework(tfm.GetString() ?? string.Empty))
                {
                    targetFramework = tfm.GetString()!;
                    return true;
                }

                if (runtimeOptions.TryGetProperty("framework", out var framework)
                    && framework.ValueKind == JsonValueKind.Object
                    && framework.TryGetProperty("version", out var version)
                    && version.ValueKind == JsonValueKind.String
                    && Version.TryParse(version.GetString(), out var parsed))
                {
                    targetFramework = $"net{parsed.Major}.{parsed.Minor}";
                    return IsPlausibleTargetFramework(targetFramework);
                }
            }
        }
        catch (JsonException)
        {
            // A malformed runtimeconfig contributes nothing.
        }

        return false;
    }
}
