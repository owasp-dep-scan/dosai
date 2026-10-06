using System.Text.Json;
using System.Text.RegularExpressions;

namespace Depscan;

/// <summary>
///     The shared frameworks (beyond Microsoft.NETCore.App) a scan root references, detected
///     from the same inputs MSBuild uses: the SDK attribute, explicit
///     <c>&lt;FrameworkReference&gt;</c> items, <c>UseWindowsForms</c>/<c>UseWPF</c>,
///     project.assets.json's per-target frameworkReferences, and
///     <c>*.runtimeconfig.json</c> (issue #74).
/// </summary>
/// <remarks>
///     The SDK-implied references mirror what the SDKs declare on disk:
///     <c>Microsoft.NET.Sdk.Web/Targets/Sdk.Server.props</c> adds
///     <c>Microsoft.AspNetCore.App</c> for .NETCoreApp 3.0+, and
///     <c>Microsoft.NET.Sdk.WindowsDesktop/targets/Microsoft.NET.Sdk.WindowsDesktop.props</c>
///     adds <c>Microsoft.WindowsDesktop.App</c> (both), <c>.WPF</c> or <c>.WindowsForms</c>
///     depending on the two Use* properties. The Worker, Razor and Blazor Web Assembly SDKs
///     add none of their own.
/// </remarks>
internal static partial class TreeFrameworks
{
    public static List<(string Name, string Evidence)> Detect(string root)
    {
        var frameworks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var projects = SafeFileRead.EnumerateAllFilesSafe(root, "*.csproj")
                .Concat(SafeFileRead.EnumerateAllFilesSafe(root, "*.fsproj"))
                .Concat(SafeFileRead.EnumerateAllFilesSafe(root, "*.vbproj"))
                .Where(file => !IsUnderBuildDirectory(root, file))
                .Order(SafeFileRead.PathComparer)
                .ToList();
            foreach (var project in projects)
            {
                if (!SafeFileRead.TryReadAllText(project, out var rawContent))
                {
                    continue;
                }

                // A commented-out FrameworkReference or Use* property is not part of the build.
                var content = GlobalUsings.WithoutXmlComments(rawContent);

                var projectDirectory = Path.GetDirectoryName(project)!;
                var reference = Path.GetRelativePath(root, project);
                var sdks = ProjectSdks(content);
                if (sdks.Contains("Microsoft.NET.Sdk.Web"))
                {
                    // Sdk.Server.props: Microsoft.AspNetCore.App for .NETCoreApp >= 3.0.
                    Claim(frameworks, "Microsoft.AspNetCore.App", $"Sdk=Microsoft.NET.Sdk.Web in {reference}");
                }

                if (sdks.Contains("Microsoft.NET.Sdk.WindowsDesktop") || PropertyIsTrue(root, projectDirectory, content, "UseWindowsForms") || PropertyIsTrue(root, projectDirectory, content, "UseWPF"))
                {
                    var useForms = PropertyIsTrue(root, projectDirectory, content, "UseWindowsForms");
                    var useWpf = PropertyIsTrue(root, projectDirectory, content, "UseWPF");
                    // Microsoft.NET.Sdk.WindowsDesktop.props: both -> Microsoft.WindowsDesktop.App,
                    // else the one sub-framework that is enabled.
                    var name = useForms && useWpf ? "Microsoft.WindowsDesktop.App" : useForms ? "Microsoft.WindowsDesktop.App.WindowsForms" : "Microsoft.WindowsDesktop.App.WPF";
                    Claim(frameworks, name, $"{(useForms && useWpf ? "UseWindowsForms+UseWPF" : useForms ? "UseWindowsForms" : "UseWPF")} in {reference}");
                }

                foreach (var match in FrameworkReferenceItemRegex().Matches(content).Cast<Match>())
                {
                    var include = Attributes(match).GetValueOrDefault("Include");
                    if (!string.IsNullOrWhiteSpace(include))
                    {
                        Claim(frameworks, include, $"FrameworkReference in {reference}");
                    }
                }
            }

            foreach (var buildFile in SafeFileRead.EnumerateAllFilesSafe(root, "Directory.Build.props")
                         .Concat(SafeFileRead.EnumerateAllFilesSafe(root, "Directory.Build.targets"))
                         .Where(file => !IsUnderBuildDirectory(root, file)).Order(SafeFileRead.PathComparer))
            {
                if (!SafeFileRead.TryReadAllText(buildFile, out var rawContent))
                {
                    continue;
                }

                var content = GlobalUsings.WithoutXmlComments(rawContent);

                foreach (var match in FrameworkReferenceItemRegex().Matches(content).Cast<Match>())
                {
                    var include = Attributes(match).GetValueOrDefault("Include");
                    if (!string.IsNullOrWhiteSpace(include))
                    {
                        Claim(frameworks, include, $"FrameworkReference in {Path.GetRelativePath(root, buildFile)}");
                    }
                }
            }

            CollectFromAssetsFiles(root, frameworks);
            CollectFromRuntimeConfigs(root, frameworks);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
        {
            // Best effort: framework detection only widens references.
        }

        return frameworks.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => (pair.Key, pair.Value)).ToList();
    }

    /// <summary>project.assets.json records the frameworks restore resolved per target, including framework references NuGet downloaded.</summary>
    private static void CollectFromAssetsFiles(string root, Dictionary<string, string> frameworks)
    {
        // obj/ is excluded from discovery by design, so these files are enumerated explicitly.
        foreach (var assetsFile in SafeFileRead.EnumerateAllFilesSafe(root, "project.assets.json"))
        {
            if (!SafeFileRead.TryReadAllText(assetsFile, out var content))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(content);
                if (!document.RootElement.TryGetProperty("frameworks", out var frameworkTargets) || frameworkTargets.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var target in frameworkTargets.EnumerateObject())
                {
                    if (!target.Value.TryGetProperty("frameworkReferences", out var references))
                    {
                        continue;
                    }

                    // Object form: { "Microsoft.AspNetCore.App": { "type": "platform" } }; array
                    // form also occurs in older formats.
                    if (references.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var reference in references.EnumerateObject())
                        {
                            Claim(frameworks, reference.Name, $"project.assets.json ({Path.GetRelativePath(root, assetsFile)})");
                        }
                    }
                    else if (references.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var reference in references.EnumerateArray())
                        {
                            if (reference.ValueKind == JsonValueKind.String)
                            {
                                Claim(frameworks, reference.GetString() ?? string.Empty, $"project.assets.json ({Path.GetRelativePath(root, assetsFile)})");
                            }
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // A malformed assets file contributes nothing.
            }
        }
    }

    /// <summary>A built tree's runtimeconfig names the exact frameworks the app runs on.</summary>
    private static void CollectFromRuntimeConfigs(string root, Dictionary<string, string> frameworks)
    {
        foreach (var runtimeConfig in SafeFileRead.EnumerateAllFilesSafe(root, "*.runtimeconfig.json"))
        {
            if (!SafeFileRead.TryReadAllText(runtimeConfig, out var content))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(content);
                if (!document.RootElement.TryGetProperty("runtimeOptions", out var runtimeOptions) || runtimeOptions.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (runtimeOptions.TryGetProperty("frameworks", out var runtimeFrameworks) && runtimeFrameworks.ValueKind == JsonValueKind.Array)
                {
                    foreach (var framework in runtimeFrameworks.EnumerateArray())
                    {
                        if (framework.ValueKind == JsonValueKind.Object
                            && framework.TryGetProperty("name", out var name)
                            && name.ValueKind == JsonValueKind.String)
                        {
                            Claim(frameworks, name.GetString() ?? string.Empty, $"runtimeconfig ({Path.GetRelativePath(root, runtimeConfig)})");
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // A malformed runtimeconfig contributes nothing.
            }
        }
    }

    private static void Claim(Dictionary<string, string> frameworks, string name, string evidence)
    {
        if (name.Length == 0)
        {
            return;
        }

        // Keep the first evidence (projects are visited in path order); Microsoft.NETCore.App
        // itself is not tracked - the base set is the framework references' job.
        if (!string.Equals(name, "Microsoft.NETCore.App", StringComparison.OrdinalIgnoreCase))
        {
            frameworks.TryAdd(name, evidence);
        }
    }

    private static HashSet<string> ProjectSdks(string project)
    {
        var sdks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match declaration in ProjectSdkAttributeRegex().Matches(project))
        {
            foreach (var sdk in declaration.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                sdks.Add(sdk);
            }
        }

        return sdks;
    }

    private static bool PropertyIsTrue(string root, string projectDirectory, string project, string propertyName)
    {
        foreach (Match match in Regex.Matches(project, $@"<(?:{propertyName})\s*>([^<]*)</(?:{propertyName})\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return match.Groups[1].Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        foreach (var buildFile in new[] { "Directory.Build.props", "Directory.Build.targets" })
        {
            for (var directory = projectDirectory; directory is not null; directory = Path.GetDirectoryName(directory))
            {
                var candidate = Path.Combine(directory, buildFile);
                if (File.Exists(candidate) && !IsUnderBuildDirectory(root, candidate))
                {
                    return SafeFileRead.TryReadAllText(candidate, out var content)
                        && Regex.IsMatch(GlobalUsings.WithoutXmlComments(content), $@"<(?:{propertyName})\s*>\s*true\s*</(?:{propertyName})\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                }

                if (string.Equals(directory, root, SafeFileRead.PathComparison))
                {
                    break;
                }
            }
        }

        return false;
    }

    private static Dictionary<string, string> Attributes(Match item)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match attribute in AttributeRegex().Matches(item.Groups[1].Value))
        {
            attributes[attribute.Groups[1].Value] = attribute.Groups[2].Value;
        }

        return attributes;
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

    [GeneratedRegex(@"<FrameworkReference\s+([^>]*?)(?:/>|>.*?</FrameworkReference\s*>)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex FrameworkReferenceItemRegex();

    [GeneratedRegex(@"<Project\b[^>]*?\bSdk\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProjectSdkAttributeRegex();

    [GeneratedRegex(@"([\w]+)\s*=\s*""([^""]*)""", RegexOptions.CultureInvariant)]
    private static partial Regex AttributeRegex();
}
