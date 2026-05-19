using System.Text.Json;
using System.Text.RegularExpressions;

namespace Depscan;

public sealed partial class PackageUrlResolver
{
    private static readonly (string Prefix, string PackageName)[] SystemPackagePrefixes =
    [
        ("System.Diagnostics.Process", "System.Diagnostics.Process"),
        ("System.IO.FileStream", "System.IO.FileSystem"),
        ("System.IO.File", "System.IO.FileSystem"),
        ("System.IO.Directory", "System.IO.FileSystem"),
        ("System.IO.Path", "System.IO.FileSystem"),
        ("System.Net.Http", "System.Net.Http"),
        ("System.Net.WebUtility", "System.Net.WebUtility"),
        ("System.Reflection.Assembly", "System.Reflection"),
        ("System.Security.Cryptography.X509Certificates", "System.Security.Cryptography.X509Certificates"),
        ("System.Security.Cryptography", "System.Security.Cryptography.Algorithms"),
        ("System.Text.Json", "System.Text.Json"),
        ("System.Text.RegularExpressions", "System.Text.RegularExpressions"),
        ("System.Console", "System.Console"),
        ("System.Uri", "System.Runtime"),
        ("System.Type", "System.Runtime"),
        ("System.String", "System.Runtime"),
        ("System.Collections", "System.Collections"),
        ("System.Linq", "System.Linq"),
        ("System.Threading.Tasks", "System.Threading.Tasks"),
        ("System.Threading", "System.Threading"),
        ("System", "System.Runtime")
    ];

    private readonly Dictionary<string, string> _assemblyToPurl = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _packageToPurl = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Prefix, string Purl)> _namespacePrefixes = [];

    private PackageUrlResolver()
    {
    }

    public static PackageUrlResolver Create(string path)
    {
        var resolver = new PackageUrlResolver();
        var root = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return resolver;
        }

        foreach (var assetsFile in SafeEnumerateFiles(root, "project.assets.json"))
        {
            resolver.ReadProjectAssets(assetsFile);
        }

        foreach (var depsFile in SafeEnumerateFiles(root, "*.deps.json"))
        {
            resolver.ReadDepsJson(depsFile);
        }

        resolver._namespacePrefixes.AddRange(resolver._packageToPurl.Keys
            .Where(name => name.Contains('.', StringComparison.Ordinal))
            .OrderByDescending(name => name.Length)
            .Select(name => (Prefix: name, Purl: resolver._packageToPurl[name])));
        return resolver;
    }

    private static IEnumerable<string> SafeEnumerateFiles(string root, string searchPattern)
    {
        try
        {
            return Directory.EnumerateFiles(root, searchPattern, new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            }).ToList();
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
        catch (ArgumentException)
        {
            return [];
        }
    }

    public string? Resolve(string? assembly = null, string? module = null, string? symbol = null, string? namespaceName = null, string? typeName = null)
    {
        foreach (var candidate in BuildAssemblyCandidates(assembly, module, symbol, typeName))
        {
            if (_assemblyToPurl.TryGetValue(candidate, out var purl) || _packageToPurl.TryGetValue(candidate, out purl))
            {
                return purl;
            }
        }

        var qualifiedName = FirstNonEmpty(symbol, typeName, namespaceName);
        if (!string.IsNullOrWhiteSpace(qualifiedName))
        {
            qualifiedName = NormalizeSymbol(qualifiedName);
            foreach (var (prefix, purl) in _namespacePrefixes)
            {
                if (qualifiedName.Equals(prefix, StringComparison.OrdinalIgnoreCase) || qualifiedName.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase))
                {
                    return purl;
                }
            }

            if (TryResolveSystemPurl(qualifiedName, out var systemPurl))
            {
                return systemPurl;
            }
        }

        return null;
    }

    private static bool TryResolveSystemPurl(string qualifiedName, out string purl)
    {
        foreach (var (prefix, packageName) in SystemPackagePrefixes)
        {
            if (qualifiedName.Equals(prefix, StringComparison.OrdinalIgnoreCase) || qualifiedName.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase))
            {
                purl = $"pkg:nuget/{EscapePurl(packageName)}";
                return true;
            }
        }

        purl = string.Empty;
        return false;
    }

    private void ReadProjectAssets(string filePath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(filePath));
            if (!document.RootElement.TryGetProperty("libraries", out var libraries) || libraries.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var packagePurls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var library in libraries.EnumerateObject())
            {
                var parts = library.Name.Split('/', 2);
                if (parts.Length != 2)
                {
                    continue;
                }

                var type = library.Value.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
                if (!string.Equals(type, "package", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var purl = BuildNuGetPurl(parts[0], parts[1]);
                packagePurls[library.Name] = purl;
                AddPackage(parts[0], purl);
            }

            if (!document.RootElement.TryGetProperty("targets", out var targets) || targets.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var target in targets.EnumerateObject())
            {
                foreach (var library in target.Value.EnumerateObject())
                {
                    if (!packagePurls.TryGetValue(library.Name, out var purl))
                    {
                        continue;
                    }

                    AddAssets(library.Value, "compile", purl);
                    AddAssets(library.Value, "runtime", purl);
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Resolver is best-effort and should never fail analysis.
        }
    }

    private void ReadDepsJson(string filePath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(filePath));
            if (!document.RootElement.TryGetProperty("libraries", out var libraries) || libraries.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var packagePurls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var library in libraries.EnumerateObject())
            {
                var parts = library.Name.Split('/', 2);
                if (parts.Length != 2)
                {
                    continue;
                }

                var type = library.Value.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
                if (!string.Equals(type, "package", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var purl = BuildNuGetPurl(parts[0], parts[1]);
                packagePurls[library.Name] = purl;
                AddPackage(parts[0], purl);
            }

            if (!document.RootElement.TryGetProperty("targets", out var targets) || targets.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var target in targets.EnumerateObject())
            {
                foreach (var library in target.Value.EnumerateObject())
                {
                    if (!packagePurls.TryGetValue(library.Name, out var purl))
                    {
                        continue;
                    }

                    AddAssets(library.Value, "runtime", purl);
                    AddAssets(library.Value, "compile", purl);
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Resolver is best-effort and should never fail analysis.
        }
    }

    private void AddAssets(JsonElement libraryElement, string propertyName, string purl)
    {
        if (!libraryElement.TryGetProperty(propertyName, out var assets) || assets.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var asset in assets.EnumerateObject())
        {
            var assemblyName = Path.GetFileNameWithoutExtension(asset.Name.Replace('/', Path.DirectorySeparatorChar));
            if (!string.IsNullOrWhiteSpace(assemblyName))
            {
                _assemblyToPurl.TryAdd(assemblyName, purl);
            }
        }
    }

    private void AddPackage(string packageName, string purl)
    {
        _packageToPurl.TryAdd(packageName, purl);
        var lastSegment = packageName.Split('.').LastOrDefault();
        if (!string.IsNullOrWhiteSpace(lastSegment))
        {
            _packageToPurl.TryAdd(lastSegment, purl);
        }
    }

    private static IEnumerable<string> BuildAssemblyCandidates(string? assembly, string? module, string? symbol, string? typeName)
    {
        foreach (var candidate in new[] { assembly, module })
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var cleaned = candidate.Split(',')[0].Trim();
            cleaned = Path.GetFileNameWithoutExtension(cleaned);
            if (!string.IsNullOrWhiteSpace(cleaned))
            {
                yield return cleaned;
            }
        }

        foreach (var candidate in new[] { symbol, typeName })
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var cleaned = NormalizeSymbol(candidate);
            var first = cleaned.Split('.', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(first))
            {
                yield return first;
            }
        }
    }

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string NormalizeSymbol(string value) => GenericArityRegex().Replace(value.Replace("global::", string.Empty, StringComparison.Ordinal).Replace("Global.", string.Empty, StringComparison.Ordinal), string.Empty);

    private static string BuildNuGetPurl(string name, string version) => $"pkg:nuget/{EscapePurl(name)}@{EscapePurl(version)}";

    private static string EscapePurl(string value) => Uri.EscapeDataString(value).Replace("%2E", ".", StringComparison.Ordinal).Replace("%2D", "-", StringComparison.Ordinal).Replace("%5F", "_", StringComparison.Ordinal);

    [GeneratedRegex("`[0-9]+")]
    private static partial Regex GenericArityRegex();
}
