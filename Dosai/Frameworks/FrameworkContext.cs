using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.VisualBasic;

namespace Depscan.Frameworks;

/// <summary>The compilations built during source analysis, handed to framework providers for reuse.</summary>
public sealed class SourceCompilations
{
    public CSharpCompilation? CSharp { get; init; }

    public VisualBasicCompilation? VisualBasic { get; init; }

    public static readonly SourceCompilations Empty = new();
}

/// <summary>
///     Shared context handed to every framework provider: the compilations Dosai already built
///     (or freshly built ones for standalone callers), the detected file set, package URLs, and a
///     diagnostics sink. Providers must read state from here instead of re-scanning the tree.
/// </summary>
public sealed class FrameworkContext
{
    private FrameworkContext(string basePath)
    {
        BasePath = basePath;
    }

    public string BasePath { get; }

    public CSharpCompilation? CSharp { get; private set; }

    public VisualBasicCompilation? VisualBasic { get; private set; }

    public PackageUrlResolver PurlResolver { get; private set; } = null!;

    /// <summary>Set once by <see cref="InitializeDetection" />; providers read it from <see cref="Detection" />.</summary>
    private FrameworkDetection? _detection;

    public FrameworkDetection Detection => _detection ??= FrameworkDetection.Detect(this);

    /// <summary>.cs and .vb files below the analysis root (bin/obj excluded).</summary>
    public List<string> SourceFiles { get; } = [];

    /// <summary>.cshtml and .razor template files below the analysis root.</summary>
    public List<string> TemplateFiles { get; } = [];

    /// <summary>.proto IDL files below the analysis root.</summary>
    public List<string> ProtoFiles { get; } = [];

    /// <summary>On-disk model artifacts (.onnx/.gguf/.safetensors/.pt/.pth) below the root.</summary>
    public List<string> ModelArtifacts { get; } = [];

    /// <summary>Config/manifest files providers may consult: host.json, function.json, web.config, app.config, *.csproj, serverless templates.</summary>
    public List<string> ConfigFiles { get; } = [];

    public List<FrameworkDiagnostic> Diagnostics { get; } = [];

    /// <summary>
    ///     Mount points discovered by other providers (MapHub, MapGrpcService, MapGraphQL, MapMcp,
    ///     ...): the route lives at the mount, not the class, so the owning provider reads it from here.
    /// </summary>
    public List<MountPoint> MountPoints { get; } = [];

    /// <summary>
    ///     Type declarations already handled by an earlier provider ("{filePath}:{typeName}").
    ///     Later providers skip these to avoid duplicate endpoints for the same controller.
    /// </summary>
    public HashSet<string> HandledTypeIds { get; } = new(StringComparer.Ordinal);

    /// <summary>
    ///     Proto service contracts parsed from .proto files by the protobuf provider. The gRPC
    ///     provider joins implementation classes against these to build /package.Service/Method paths.
    /// </summary>
    public List<ProtoServiceContract> ProtoServices { get; } = [];

    /// <summary>Server-wide gRPC configuration facts (reflection exposed, JSON transcoding, gRPC-Web).</summary>
    public Dictionary<string, string> GrpcServerProperties { get; } = new(StringComparer.Ordinal);

    public bool ClassifyData { get; internal set; } = true;

    private readonly Dictionary<string, string> _textCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _rawUrlsCache = new(StringComparer.Ordinal);

    /// <summary>
    ///     File text for a tree, cached: providers gate on keyword containment and must not
    ///     re-read or re-string a tree each.
    /// </summary>
    public string TextFor(SyntaxTree tree)
    {
        if (_textCache.TryGetValue(tree.FilePath, out var text))
        {
            return text;
        }

        text = tree.ToString();
        _textCache[tree.FilePath] = text;
        return text;
    }

    /// <summary>File-scoped heuristic URLs for a tree (RawUrls), computed once per file.</summary>
    public List<string> RawUrlsFor(SyntaxTree tree)
    {
        if (_rawUrlsCache.TryGetValue(tree.FilePath, out var urls))
        {
            return urls;
        }

        urls = ProviderHelpers.ExtractRawUrls(TextFor(tree));
        _rawUrlsCache[tree.FilePath] = urls;
        return urls;
    }

    /// <summary>True when any keyword appears in the tree's text — the cheap per-file provider gate.</summary>
    public bool TextContainsAny(SyntaxTree tree, params string[] keywords)
    {
        var text = TextFor(tree);
        return keywords.Any(keyword => text.Contains(keyword, StringComparison.Ordinal));
    }

    public int MaxConventionalRoutes { get; internal set; } = 500;

    /// <summary>Full prompt text is redacted by default; opted in via --include-prompt-text.</summary>
    public bool IncludePromptText { get; internal set; }

    /// <summary>
    ///     Builds a context that reuses compilations Dosai already constructed for methods/call-graph
    ///     extraction, avoiding a second parse of every source file.
    /// </summary>
    public static FrameworkContext FromCompilations(string basePath, CSharpCompilation? csharp, VisualBasicCompilation? visualBasic, PackageUrlResolver purlResolver)
    {
        var context = new FrameworkContext(basePath)
        {
            CSharp = csharp,
            VisualBasic = visualBasic,
            PurlResolver = purlResolver
        };
        context.DiscoverFiles();
        return context;
    }

    /// <summary>
    ///     Builds a context with fresh compilations. Used when framework analysis runs standalone
    ///     (e.g. from the dataflows command). Reference resolution mirrors Dosai's source pipeline.
    /// </summary>
    public static FrameworkContext Create(string basePath)
    {
        var context = new FrameworkContext(basePath) { PurlResolver = PackageUrlResolver.Create(basePath) };
        context.DiscoverFiles();
        var references = BuildMetadataReferences(basePath);

        var csharpTrees = context.SourceFiles
            .Where(file => file.EndsWith(Constants.CSharpSourceExtension, StringComparison.OrdinalIgnoreCase))
            .Select(context.TryReadFile)
            .Where(read => read.Text is not null)
            .Select(read => (CSharpSyntaxTree)CSharpSyntaxTree.ParseText(read.Text!, path: read.Path))
            .ToList();
        if (csharpTrees.Count > 0)
        {
            context.CSharp = CSharpCompilation.Create(
                "Dosai.FrameworkAnalysis.CSharp",
                syntaxTrees: csharpTrees,
                references: references,
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        }

        var vbTrees = context.SourceFiles
            .Where(file => file.EndsWith(Constants.VBSourceExtension, StringComparison.OrdinalIgnoreCase))
            .Select(context.TryReadFile)
            .Where(read => read.Text is not null)
            .Select(read => (VisualBasicSyntaxTree)VisualBasicSyntaxTree.ParseText(read.Text!, path: read.Path))
            .ToList();
        if (vbTrees.Count > 0)
        {
            context.VisualBasic = VisualBasicCompilation.Create(
                "Dosai.FrameworkAnalysis.VisualBasic",
                syntaxTrees: vbTrees,
                references: references,
                options: new VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        }

        return context;
    }

    /// <summary>Reads a source file for the standalone compilation, skipping unreadable files with a diagnostic instead of failing the run.</summary>
    private (string Path, string? Text) TryReadFile(string file)
    {
        try
        {
            return (file, File.ReadAllText(file));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Add(new FrameworkDiagnostic("context", $"Could not read source file, skipped: {Path.GetFileName(file)}"));
            return (file, null);
        }
    }

    public SemanticModel? GetSemanticModel(SyntaxTree tree) => CSharp?.GetSemanticModel(tree) ?? VisualBasic?.GetSemanticModel(tree);

    /// <summary>Every C# syntax tree in the compilation.</summary>
    public IEnumerable<CSharpSyntaxTree> CSharpTrees => CSharp?.SyntaxTrees.OfType<CSharpSyntaxTree>() ?? [];

    /// <summary>All namespaces imported via using/imports directives across the compilation (lower-cased).</summary>
    public IReadOnlySet<string> ImportedNamespaces
    {
        get
        {
            if (_importedNamespaces is null)
            {
                var set = new HashSet<string>(StringComparer.Ordinal);
                foreach (var tree in CSharp?.SyntaxTrees ?? [])
                {
                    foreach (var usingDirective in ((CSharpSyntaxTree)tree).GetCompilationUnitRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.UsingDirectiveSyntax>())
                    {
                        var name = usingDirective.Name?.ToString();
                        if (!string.IsNullOrWhiteSpace(name)) set.Add(name);
                    }
                }

                foreach (var tree in VisualBasic?.SyntaxTrees ?? [])
                {
                    foreach (var imports in ((VisualBasicSyntaxTree)tree).GetCompilationUnitRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.VisualBasic.Syntax.SimpleImportsClauseSyntax>())
                    {
                        var name = imports.Name?.ToString();
                        if (!string.IsNullOrWhiteSpace(name)) set.Add(name);
                    }
                }

                _importedNamespaces = set;
            }

            return _importedNamespaces;
        }
    }

    private HashSet<string>? _importedNamespaces;

    /// <summary>
    ///     True when the application applies authorization globally rather than per-endpoint, via an
    ///     <c>AuthorizationOptions.FallbackPolicy</c>, a global <c>AuthorizeFilter</c>, or
    ///     <c>RequireAuthorization()</c> on a controller/page mount.
    /// </summary>
    /// <remarks>
    ///     This is the difference between "this endpoint has no <c>[Authorize]</c>, so it is anonymous"
    ///     and "this endpoint has no <c>[Authorize]</c>, and it does not need one". Without the signal,
    ///     an inbound service carrying no authorization metadata cannot honestly be called public — and
    ///     a service that is never called public never gets its trust boundary evaluated. Syntactic
    ///     evidence only, so callers must not raise confidence above <see cref="ConfidenceTiers.Syntactic" />
    ///     on the strength of it.
    /// </remarks>
    public bool HasGlobalAuthorizationFallback
    {
        get
        {
            if (_hasGlobalAuthorizationFallback is null)
            {
                _hasGlobalAuthorizationFallback = false;
                foreach (var tree in CSharpTrees)
                {
                    if (ContainsActiveGlobalAuthorizationMarker(TextFor(tree)))
                    {
                        _hasGlobalAuthorizationFallback = true;
                        break;
                    }
                }
            }

            return _hasGlobalAuthorizationFallback.Value;
        }
    }

    private bool? _hasGlobalAuthorizationFallback;

    /// <summary>
    ///     The global-authorization markers must appear in live code: a commented-out
    ///     <c>FallbackPolicy</c> line still contains the substring, and treating it as active
    ///     silently suppresses the Public trust zone (and with it the boundary-crossing sweep)
    ///     for the whole application.
    /// </summary>
    private static bool ContainsActiveGlobalAuthorizationMarker(string text)
    {
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimStart();
            if (line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith("*", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.Contains("FallbackPolicy", StringComparison.Ordinal) ||
                line.Contains("AuthorizeFilter", StringComparison.Ordinal) ||
                line.Contains("MapControllers().RequireAuthorization", StringComparison.Ordinal) ||
                line.Contains("MapRazorPages().RequireAuthorization", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static List<MetadataReference> BuildMetadataReferences(string basePath)
    {
        var references = new List<MetadataReference>();
#pragma warning disable IL3000
        var coreLib = typeof(object).Assembly.Location;
#pragma warning restore IL3000
        if (!string.IsNullOrWhiteSpace(coreLib))
        {
            references.Add(MetadataReference.CreateFromFile(coreLib));
        }

        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string trustedPlatformAssemblies)
        {
            foreach (var referencePath in trustedPlatformAssemblies.Split(Path.PathSeparator).Where(File.Exists))
            {
                references.Add(MetadataReference.CreateFromFile(referencePath));
            }
        }

        foreach (var assemblyPath in EnumerateFilesSafe(basePath, Constants.AssemblyExtension))
        {
            references.Add(MetadataReference.CreateFromFile(assemblyPath));
        }

        return references;
    }

    private void DiscoverFiles()
    {
        if (File.Exists(BasePath))
        {
            ClassifyFile(BasePath, SourceFiles, TemplateFiles, ProtoFiles, ConfigFiles, ModelArtifacts);
            return;
        }

        if (!Directory.Exists(BasePath))
        {
            return;
        }

        foreach (var file in EnumerateFilesSafe(BasePath, "*.*"))
        {
            ClassifyFile(file, SourceFiles, TemplateFiles, ProtoFiles, ConfigFiles, ModelArtifacts);
        }
    }

    internal static void ClassifyFile(string file, List<string> sources, List<string> templates, List<string> protos, List<string> configs, List<string>? artifacts = null)
    {
        var name = Path.GetFileName(file);
        var extension = Path.GetExtension(file);
        if (name.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".g.vb", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        switch (extension.ToLowerInvariant())
        {
            case ".cs":
            case ".vb":
                sources.Add(file);
                break;
            case ".cshtml":
            case ".razor":
                templates.Add(file);
                break;
            case ".proto":
                protos.Add(file);
                break;
            case ".onnx":
            case ".gguf":
            case ".safetensors":
            case ".pt":
            case ".pth":
                artifacts?.Add(file);
                break;

            case ".prompty":
                configs.Add(file);
                break;

            case ".json":
                if (name.Equals("host.json", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("function.json", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("local.settings.json", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("appsettings.json", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("appsettings.", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("aws-lambda-tools-defaults.json", StringComparison.OrdinalIgnoreCase))
                {
                    configs.Add(file);
                }

                break;
            case ".csproj":
            case ".fsproj":
            case ".vbproj":
                configs.Add(file);
                break;
            case ".config":
                if (name.Equals("web.config", StringComparison.OrdinalIgnoreCase) || name.Equals("app.config", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".svc", StringComparison.OrdinalIgnoreCase))
                {
                    configs.Add(file);
                }

                break;
            case ".xml":
                if (name.Equals("serverless.template", StringComparison.OrdinalIgnoreCase) || name.Equals("serverless.xml", StringComparison.OrdinalIgnoreCase))
                {
                    configs.Add(file);
                }

                break;
        }
    }

    internal static IEnumerable<string> EnumerateFilesSafe(string path, string extension)
    {
        if (!Directory.Exists(path))
        {
            return [];
        }

        return new DirectoryInfo(path).EnumerateFiles(extension, SearchOption.AllDirectories)
            .Where(file => !IsIgnoredDirectory(file.FullName))
            .Select(file => file.FullName)
            .ToList();
    }

    internal static bool IsIgnoredDirectory(string fullPath) =>
        fullPath.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
        fullPath.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
        fullPath.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
}
