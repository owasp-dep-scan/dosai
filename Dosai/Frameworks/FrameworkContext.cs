using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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
    ///    ...): the route lives at the mount, not the class, so the owning provider reads it from here.
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

        _treeTexts ??= MaterializeTreeTexts();
        text = _treeTexts.TryGetValue(tree, out var materialized) ? materialized : tree.ToString();
        _textCache[tree.FilePath] = text;
        return text;
    }

    /// <summary>
    ///     Every compilation tree's text, rendered once on the worker team (issue #65): the
    ///     providers' keyword gates ask for every tree's text, and rendering a large tree's text
    ///     one tree at a time on the provider thread was a serial pass over the whole source.
    ///     Keyed by tree, not path, so <see cref="TextFor" /> keeps answering by path exactly as
    ///     before (the first tree asked for under a path names its text).
    /// </summary>
    private Dictionary<SyntaxTree, string> MaterializeTreeTexts()
    {
        var trees = (CSharp?.SyntaxTrees ?? []).Concat(VisualBasic?.SyntaxTrees ?? []).ToArray();
        var texts = new string[trees.Length];
        DedicatedStack.ForEach("Dosai framework tree text", Math.Max(1, Dosai.MaxSymbolAnalysisWorkers), trees.Length,
            index => texts[index] = trees[index].ToString());
        var byTree = new Dictionary<SyntaxTree, string>(trees.Length, ReferenceEqualityComparer.Instance);
        for (var index = 0; index < trees.Length; index++)
        {
            byTree.TryAdd(trees[index], texts[index]);
        }

        return byTree;
    }

    private Dictionary<SyntaxTree, string>? _treeTexts;

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

    /// <summary>True when any keyword appears in the tree's text, the cheap per-file provider gate.</summary>
    public bool TextContainsAny(SyntaxTree tree, params string[] keywords)
    {
        var text = TextFor(tree);
        // A null search is a list holding an empty keyword, which Contains("") matched in every text.
        return KeywordSearch(keywords) is not { } search || text.AsSpan().ContainsAny(search);
    }

    /// <summary>
    ///     One vectorized multi-string search per distinct keyword list (ordinal, like the
    ///     <c>string.Contains</c> loop it replaces): every provider gates every tree on a keyword
    ///     list, and one pass over the text per keyword was the framework phase's serial cost on
    ///     large trees (issue #65). A list holding an empty keyword matches every text, exactly
    ///     as <c>Contains("")</c> did.
    /// </summary>
    private System.Buffers.SearchValues<string>? KeywordSearch(string[] keywords)
    {
        var key = string.Join('\u0000', keywords);
        if (!_keywordSearches.TryGetValue(key, out var search))
        {
            search = keywords.Any(keyword => keyword.Length == 0)
                ? null
                : System.Buffers.SearchValues.Create(keywords, StringComparison.Ordinal);
            _keywordSearches[key] = search;
        }

        return search;
    }

    private readonly Dictionary<string, System.Buffers.SearchValues<string>?> _keywordSearches = new(StringComparer.Ordinal);

    public int MaxConventionalRoutes { get; internal set; } = 500;

    /// <summary>Full prompt text is redacted by default; opted in via --include-prompt-text.</summary>
    public bool IncludePromptText { get; internal set; }

    /// <summary>
    ///     CI-policy allowlist (--mcp-allowlist) of MCP stdio transport commands whose launches are
    ///     approved; null disables allowlist suppression.
    /// </summary>
    public IReadOnlySet<string>? McpAllowlist { get; internal set; }

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
            .Select(read => CSharpSourceParser.Parse(read.Text!, read.Path, basePath))
            .ToList();
        if (CSharpSourceParser.TryCreateImplicitUsingsTree(basePath) is { } implicitUsingsTree)
        {
            csharpTrees.Insert(0, implicitUsingsTree);
        }
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

    // Providers each walked every tree of the compilation for their node kinds - a dozen full
    // walks of the source, one thread, most of the framework phase on a large tree. The two
    // helpers below give the same nodes in the same order without those walks, and without
    // holding any node: a cache of nodes would pin the red trees the walks let the collector
    // reclaim (2.4 GB on dotnet/runtime).

    /// <summary>
    ///     Type, method or using declarations of a tree, in document order: exactly what
    ///     <c>tree.GetRoot().DescendantNodes().OfType&lt;T&gt;()</c> yields. None of them occurs
    ///     inside a statement or an expression (a local function is a statement, a lambda an
    ///     expression), so the walk skips those - method bodies, initializers and arguments, most
    ///     of a tree.
    /// </summary>
    public static IEnumerable<T> Declarations<T>(SyntaxTree tree) where T : CSharpSyntaxNode
    {
        System.Diagnostics.Debug.Assert(typeof(T).IsAssignableTo(typeof(BaseTypeDeclarationSyntax)) || typeof(T).IsAssignableTo(typeof(BaseMethodDeclarationSyntax)) || typeof(T) == typeof(UsingDirectiveSyntax));
        return tree.GetRoot().DescendantNodes(static node => node is not (StatementSyntax or ExpressionSyntax)).OfType<T>();
    }

    /// <summary>
    ///     The tree's invocations, exactly as <c>tree.GetRoot().DescendantNodes().OfType&lt;InvocationExpressionSyntax&gt;()</c>
    ///     yields them, when one of them has a <see cref="ProviderHelpers.InvocationName" /> that
    ///     passes <paramref name="mayMatch" />; none otherwise. For a loop that acts only on
    ///     invocations whose name passes <paramref name="mayMatch" /> (or a narrower test), skipping
    ///     a tree without such a name skips nothing but the walk.
    /// </summary>
    public IEnumerable<InvocationExpressionSyntax> InvocationsNamed(SyntaxTree tree, Func<string, bool> mayMatch)
        => Invokes(tree, mayMatch) ? tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>() : [];

    /// <summary>Whether any invocation in the tree has a <see cref="ProviderHelpers.InvocationName" /> passing <paramref name="mayMatch" />.</summary>
    public bool Invokes(SyntaxTree tree, Func<string, bool> mayMatch)
    {
        _invocationNames ??= IndexInvocationNames();
        if (!_invocationNames.TryGetValue(tree, out var names))
        {
            // Not a tree of this compilation: no index, so it may.
            return true;
        }

        foreach (var name in names)
        {
            if (mayMatch(name))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The distinct invocation names of every C# tree, gathered once, one tree per worker.</summary>
    private Dictionary<SyntaxTree, string[]> IndexInvocationNames()
    {
        var trees = CSharp?.SyntaxTrees.ToArray() ?? [];
        var names = new string[trees.Length][];
        DedicatedStack.ForEach("Dosai framework invocation names", Math.Max(1, Dosai.MaxSymbolAnalysisWorkers), trees.Length, index =>
        {
            var distinct = new HashSet<string>(StringComparer.Ordinal);
            foreach (var invocation in trees[index].GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                distinct.Add(ProviderHelpers.InvocationName(invocation));
            }

            names[index] = [.. distinct];
        });

        var byTree = new Dictionary<SyntaxTree, string[]>(trees.Length, ReferenceEqualityComparer.Instance);
        for (var index = 0; index < trees.Length; index++)
        {
            byTree[trees[index]] = names[index];
        }

        return byTree;
    }

    private Dictionary<SyntaxTree, string[]>? _invocationNames;

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
                    foreach (var usingDirective in Declarations<UsingDirectiveSyntax>(tree))
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
    ///     an inbound service carrying no authorization metadata cannot honestly be called public, and
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
        var frameworkReferences = FrameworkReferences.Current.References;
        var referencePaths = frameworkReferences.Select(framework => framework.Key).ToList();
        var references = frameworkReferences.Select(framework => (MetadataReference)framework.Reference).ToList();
        foreach (var assemblyPath in EnumerateFilesSafe(basePath, Constants.AssemblyExtension))
        {
            referencePaths.Add(assemblyPath);
            references.Add(MetadataReference.CreateFromFile(assemblyPath));
        }

        // Restored-but-unbuilt trees: package DLLs from the NuGet cache, unpinned from bytes so
        // the shared packages folder is never locked. Deduped against the FULL reference set -
        // runtime assemblies included - so cache copies of framework facades (System.Memory,
        // netstandard shims) never land beside the runtime's at a different version.
        foreach (var cacheAssembly in NuGetRestoreCache.GetReferencePaths(basePath, referencePaths))
        {
            if (NuGetRestoreCache.TryCreateUnpinnedReference(cacheAssembly) is { } cacheReference)
            {
                references.Add(cacheReference);
            }
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

        // "Safe" includes hostile trees: unreadable or over-long subtrees are skipped with a
        // console warning and the readable remainder is returned.
        return SafeFileRead.EnumerateAllFilesSafe(path, extension)
            .Where(file => !IsIgnoredDirectory(file));
    }

    internal static bool IsIgnoredDirectory(string fullPath) =>
        fullPath.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
        fullPath.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
        fullPath.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
}
