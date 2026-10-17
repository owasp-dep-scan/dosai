using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Depscan;

/// <summary>
///     HTTP endpoints of assembly-only inputs, read from metadata and IL: MVC controllers
///     (<c>[HttpGet]</c> and friends, <c>[Route]</c> with <c>[controller]</c>/<c>[action]</c>
///     tokens, <c>[Authorize]</c>/<c>[AllowAnonymous]</c>, <c>[ApiController]</c>, a
///     <c>ControllerBase</c>/<c>Controller</c> base type) and minimal-API registrations (a route
///     <c>ldstr</c> and a handler <c>ldftn</c> feeding a <c>Map*</c> call, the delegate shape the IL
///     call graph resolves). An assembly the tree builds from source is skipped: the source
///     providers already report its endpoints with full fidelity.
/// </summary>
internal static class AssemblyEndpointAnalyzer
{
    internal const string Framework = "assembly-il";

    private static readonly Dictionary<string, string> HttpAttributes = new(StringComparer.Ordinal)
    {
        ["HttpGetAttribute"] = "GET", ["HttpPostAttribute"] = "POST", ["HttpPutAttribute"] = "PUT", ["HttpDeleteAttribute"] = "DELETE",
        ["HttpPatchAttribute"] = "PATCH", ["HttpHeadAttribute"] = "HEAD", ["HttpOptionsAttribute"] = "OPTIONS"
    };

    private static readonly Dictionary<string, string> MapVerbs = new(StringComparer.Ordinal)
    {
        ["MapGet"] = "GET", ["MapPost"] = "POST", ["MapPut"] = "PUT", ["MapDelete"] = "DELETE", ["MapPatch"] = "PATCH", ["Map"] = "ANY", ["MapMethods"] = "ANY", ["MapFallback"] = "ANY"
    };

    /// <summary>One endpoint's handler: its assembly, metadata token, and the call-graph / data-flow id.</summary>
    internal sealed record Handler(string AssemblyPath, int MetadataToken, string MethodId, string Route);

    internal sealed record Result(List<ApiEndpoint> Endpoints, List<EntryPoint> EntryPoints, List<Handler> Handlers)
    {
        /// <summary>Per endpoint, the data classifications of its handler's inputs and a mass-assignment-shaped request type.</summary>
        public Dictionary<ApiEndpoint, (List<string> Sensitive, string? MassAssignmentDto)> Inputs { get; } = new(ReferenceEqualityComparer.Instance);

        /// <summary>Whether any analyzed assembly registers <c>AutoValidateAntiforgeryTokenAttribute</c> as a global filter.</summary>
        public bool GlobalAntiforgery { get; set; }

        /// <summary>The endpoints with their inputs, for the metadata-only security rules.</summary>
        public List<(ApiEndpoint Endpoint, IReadOnlyList<string> Sensitive, string? MassAssignmentDto)> ForSecurityRules() =>
            [.. Endpoints.Select(endpoint => Inputs.TryGetValue(endpoint, out var inputs) ? (endpoint, (IReadOnlyList<string>)inputs.Sensitive, inputs.MassAssignmentDto) : (endpoint, [], null))];
    }

    /// <summary>Endpoints of every analyzable assembly under <paramref name="path" /> that the tree does not build from source.</summary>
    internal static Result Analyze(string path, IReadOnlyList<Method>? knownMethods, ICollection<string> diagnostics)
    {
        var result = new Result([], [], []);
        List<string> files;
        try
        {
            files = AssemblyScope.GetAssemblyFiles(path, includeBuildArtifacts: false, excludeBinWhenSourceFilesPresent: false, diagnostics.Add);
            files = AssemblyCopies.Collapse(path, files).Distinct.ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return result;
        }

        var root = SourceDocumentPaths.Root(path);
        files = files.Where(file => root is null || !TreeFrameworks.IsBuiltFromSource(root, file)).ToList();

        // Conventional route templates first: the program that registers them is usually not the
        // assembly that declares the controllers.
        var templates = new List<string>();
        foreach (var file in files)
        {
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var peReader = new PEReader(stream);
                if (peReader.HasMetadata)
                {
                    result.GlobalAntiforgery |= RegistersGlobalAntiforgery(peReader);
                    templates.AddRange(ConventionalTemplates(peReader).Where(template => !templates.Contains(template, StringComparer.Ordinal)));
                }
            }
            catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // Reported by the endpoint pass below.
            }
        }

        foreach (var file in files)
        {
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var peReader = new PEReader(stream);
                if (!peReader.HasMetadata)
                {
                    continue;
                }

                AnalyzeAssembly(path, file, peReader, knownMethods, result, templates);
            }
            catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                diagnostics.Add($"Could not read endpoints from {file}: {ex.Message}");
            }
        }

        return result;
    }

    /// <summary>The handlers of one opened assembly (the IL data-flow pass seeds their parameters).</summary>
    internal static List<Handler> Handlers(string basePath, string assemblyPath, PEReader peReader)
    {
        var result = new Result([], [], []);
        var root = SourceDocumentPaths.Root(basePath);
        if (root is not null && TreeFrameworks.IsBuiltFromSource(root, assemblyPath))
        {
            return [];
        }

        try
        {
            AnalyzeAssembly(basePath, assemblyPath, peReader, null, result, ConventionalTemplates(peReader));
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException)
        {
            return [];
        }

        return result.Handlers;
    }

    private static void AnalyzeAssembly(string basePath, string assemblyPath, PEReader peReader, IReadOnlyList<Method>? knownMethods, Result result, IReadOnlyList<string> conventionalTemplates)
    {
        var reader = peReader.GetMetadataReader();
        var fileName = Path.GetFileName(assemblyPath);
        var relativePath = SourceDocumentPaths.Root(basePath) is { } root ? Path.GetRelativePath(root, assemblyPath).Replace('\\', '/') : fileName;
        var assemblyName = reader.IsAssembly ? reader.GetString(reader.GetAssemblyDefinition().Name) : Path.GetFileNameWithoutExtension(assemblyPath);
        if (assemblyName.StartsWith("System.", StringComparison.Ordinal) || assemblyName.StartsWith("Microsoft.", StringComparison.Ordinal) || assemblyName is "System" or "mscorlib" or "netstandard")
        {
            return;
        }

        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(typeHandle);
            if (IsController(reader, type))
            {
                AnalyzeController(reader, typeHandle, type, assemblyPath, fileName, relativePath, knownMethods, result, conventionalTemplates);
            }
        }

        AnalyzeCompiledRazorPages(reader, assemblyPath, fileName, relativePath, knownMethods, result);

        foreach (var methodHandle in reader.MethodDefinitions)
        {
            var method = reader.GetMethodDefinition(methodHandle);
            if (method.RelativeVirtualAddress != 0)
            {
                AnalyzeMapCalls(reader, peReader, methodHandle, method, assemblyPath, fileName, relativePath, knownMethods, result);
            }
        }
    }

    private static bool IsController(MetadataReader reader, TypeDefinition type)
    {
        var visibility = type.Attributes & TypeAttributes.VisibilityMask;
        if (visibility is not (TypeAttributes.Public or TypeAttributes.NestedPublic) || (type.Attributes & TypeAttributes.Abstract) != 0 || type.GetGenericParameters().Count > 0)
        {
            return false;
        }

        var attributes = Attributes(reader, type.GetCustomAttributes()).ToList();
        if (attributes.Any(attribute => attribute.Name == "NonControllerAttribute"))
        {
            return false;
        }

        if (attributes.Any(attribute => attribute.Name is "ApiControllerAttribute" or "ControllerAttribute"))
        {
            return true;
        }

        // The base type chain, as far as this assembly defines it, ends at ControllerBase/Controller.
        var baseType = type.BaseType;
        for (var depth = 0; depth < 16 && !baseType.IsNil; depth++)
        {
            switch (baseType.Kind)
            {
                case HandleKind.TypeReference:
                    var reference = reader.GetTypeReference((TypeReferenceHandle)baseType);
                    return reader.GetString(reference.Name) is "ControllerBase" or "Controller" && reader.GetString(reference.Namespace) == "Microsoft.AspNetCore.Mvc";
                case HandleKind.TypeDefinition:
                    var definition = reader.GetTypeDefinition((TypeDefinitionHandle)baseType);
                    if (reader.GetString(definition.Name) is "ControllerBase" or "Controller" && reader.GetString(definition.Namespace) == "Microsoft.AspNetCore.Mvc" ||
                        Attributes(reader, definition.GetCustomAttributes()).Any(attribute => attribute.Name is "ApiControllerAttribute" or "ControllerAttribute"))
                    {
                        return true;
                    }

                    baseType = definition.BaseType;
                    break;
                default:
                    return false;
            }
        }

        return false;
    }

    private static void AnalyzeController(MetadataReader reader, TypeDefinitionHandle typeHandle, TypeDefinition type, string assemblyPath, string fileName, string relativePath, IReadOnlyList<Method>? knownMethods, Result result, IReadOnlyList<string> conventionalTemplates)
    {
        var typeName = reader.GetString(type.Name);
        var controllerName = typeName.EndsWith("Controller", StringComparison.Ordinal) ? typeName[..^"Controller".Length] : typeName;
        var namespaceName = reader.GetString(type.Namespace);
        var classAttributes = InheritedAttributes(reader, type).ToList();
        var classRoutes = classAttributes.Where(attribute => attribute.Name == "RouteAttribute").Select(attribute => attribute.FirstString).OfType<string>().DefaultIfEmpty(null).ToList();
        var isApiController = classAttributes.Any(attribute => attribute.Name == "ApiControllerAttribute");

        foreach (var methodHandle in type.GetMethods())
        {
            var method = reader.GetMethodDefinition(methodHandle);
            var methodName = reader.GetString(method.Name);
            if ((method.Attributes & MethodAttributes.MemberAccessMask) != MethodAttributes.Public || (method.Attributes & MethodAttributes.Static) != 0 ||
                (method.Attributes & MethodAttributes.SpecialName) != 0 || methodName is ".ctor" or "Dispose")
            {
                continue;
            }

            var methodAttributes = Attributes(reader, method.GetCustomAttributes()).ToList();
            if (methodAttributes.Any(attribute => attribute.Name == "NonActionAttribute"))
            {
                continue;
            }

            var actionName = methodName.EndsWith("Async", StringComparison.Ordinal) && methodName.Length > 5 ? methodName[..^5] : methodName;
            var routes = new List<(string Verb, string? Template)>();
            foreach (var attribute in methodAttributes)
            {
                if (HttpAttributes.TryGetValue(attribute.Name, out var verb))
                {
                    routes.Add((verb, attribute.FirstString));
                }
            }

            var methodRoutes = methodAttributes.Where(attribute => attribute.Name == "RouteAttribute").Select(attribute => attribute.FirstString).ToList();
            if (routes.Count == 0 && methodRoutes.Count > 0)
            {
                routes.AddRange(methodRoutes.Select(template => ("ANY", template)));
            }

            // An action with no route template of its own on a controller without one is routed
            // conventionally (verb attributes still constrain it): every registered
            // MapControllerRoute/MapDefaultControllerRoute template, expanded with the controller
            // and action names, else `{controller}/{action}`. Attribute-routed API controllers
            // expose only attributed actions.
            var conventional = routes.All(route => route.Template is null) && !classRoutes.Any(route => route is not null);
            if (conventional)
            {
                if (isApiController)
                {
                    continue;
                }

                var verbs = routes.Select(route => route.Verb).Distinct(StringComparer.Ordinal).DefaultIfEmpty("ANY").ToList();
                routes = [.. (conventionalTemplates.Count > 0 ? conventionalTemplates : ["{controller}/{action}"])
                    .SelectMany(template => verbs.Select(verb => (verb, (string?)ExpandConventional(template, controllerName, actionName))))
                    .Distinct()];
            }

            var methodId = AssemblyCallGraphAnalyzer.MethodId(reader, methodHandle, assemblyPath, knownMethods);
            foreach (var classRoute in classRoutes)
            {
                foreach (var (verb, template) in routes)
                {
                    var combined = conventional ? "/" + template!.TrimStart('/') : Combine(classRoute, template);
                    combined = combined.Replace("[controller]", controllerName, StringComparison.OrdinalIgnoreCase).Replace("[action]", actionName, StringComparison.OrdinalIgnoreCase);
                    var resolved = Frameworks.RouteTemplateResolver.Resolve(combined);
                    var endpoint = new ApiEndpoint
                    {
                        Path = resolved.Path ?? combined,
                        Route = combined,
                        FilePath = relativePath,
                        FileName = fileName,
                        Namespace = namespaceName,
                        ClassName = typeName,
                        MethodName = methodName,
                        HttpMethod = verb,
                        EndpointKind = "Controller",
                        RoutingKind = conventional ? "Conventional" : "Attribute",
                        Framework = Framework,
                        Confidence = "medium",
                        RouteParameters = resolved.Parameters,
                        Evidence = new AnalysisEvidence { Kind = AnalysisEvidenceKind.AssemblyIlDirect, Source = "assembly-il", Description = $"Controller action attributes in {fileName}." }
                    };
                    ApplyAuthorization(endpoint, classAttributes.Concat(methodAttributes).ToList());
                    Add(result, endpoint, "HttpController", methodId, assemblyPath, MetadataTokens.GetToken(methodHandle), reader);
                }
            }
        }
    }

    /// <summary>
    ///     Minimal-API registrations in one body: the route string and the handler's <c>ldftn</c>
    ///     that precede a <c>Map*</c> call (the cached-delegate branch between them is skipped by
    ///     reading in instruction order), then the fluent <c>RequireAuthorization</c> /
    ///     <c>AllowAnonymous</c> calls that follow it.
    /// </summary>
    private static void AnalyzeMapCalls(MetadataReader reader, PEReader peReader, MethodDefinitionHandle owner, MethodDefinition method, string assemblyPath, string fileName, string relativePath, IReadOnlyList<Method>? knownMethods, Result result)
    {
        var instructions = AssemblyCallGraphAnalyzer.Decode(peReader.GetMethodBody(method.RelativeVirtualAddress).GetILReader()).ToList();
        string? route = null;
        MethodDefinitionHandle? handler = null;
        for (var index = 0; index < instructions.Count; index++)
        {
            var (_, opCode, operand) = instructions[index];
            if (opCode == OpCodes.Ldstr && operand is int stringToken)
            {
                route = reader.GetUserString(MetadataTokens.UserStringHandle(stringToken));
                handler = null;
                continue;
            }

            if ((opCode == OpCodes.Ldftn || opCode == OpCodes.Ldvirtftn) && operand is int functionToken && MetadataTokens.EntityHandle(functionToken) is { Kind: HandleKind.MethodDefinition } function)
            {
                handler = (MethodDefinitionHandle)function;
                continue;
            }

            if ((opCode != OpCodes.Call && opCode != OpCodes.Callvirt) || operand is not int callToken || CalleeName(reader, callToken) is not { } callee ||
                !MapVerbs.TryGetValue(callee.Name, out var verb) || !callee.Type.Contains("EndpointRouteBuilder", StringComparison.Ordinal) && !callee.Type.Contains("WebApplication", StringComparison.Ordinal) && !callee.Type.Contains("RouteGroupBuilder", StringComparison.Ordinal))
            {
                continue;
            }

            if (route is null || handler is not { } handlerHandle)
            {
                route = null;
                handler = null;
                continue;
            }

            var resolved = Frameworks.RouteTemplateResolver.Resolve(route);
            var handlerMethod = reader.GetMethodDefinition(handlerHandle);
            var declaringType = reader.GetTypeDefinition(handlerMethod.GetDeclaringType());
            var endpoint = new ApiEndpoint
            {
                Path = resolved.Path ?? route,
                Route = route,
                FilePath = relativePath,
                FileName = fileName,
                Namespace = reader.GetString(declaringType.Namespace),
                ClassName = reader.GetString(declaringType.Name),
                MethodName = reader.GetString(handlerMethod.Name),
                HttpMethod = verb,
                EndpointKind = "MinimalApi",
                RoutingKind = "MinimalApi",
                Framework = Framework,
                Confidence = "medium",
                RouteParameters = resolved.Parameters,
                Evidence = new AnalysisEvidence { Kind = AnalysisEvidenceKind.AssemblyIlDelegateTarget, Source = "assembly-il", Description = $"{callee.Name} registration with an ldftn handler in {fileName}." }
            };

            // The fluent conventions on the returned builder, until the value is dropped or stored.
            for (var next = index + 1; next < instructions.Count && next <= index + 8; next++)
            {
                var (_, nextOpCode, nextOperand) = instructions[next];
                if (nextOpCode == OpCodes.Pop || nextOpCode.Name?.StartsWith("stloc", StringComparison.Ordinal) == true)
                {
                    break;
                }

                if ((nextOpCode == OpCodes.Call || nextOpCode == OpCodes.Callvirt) && nextOperand is int conventionToken && CalleeName(reader, conventionToken) is { } convention)
                {
                    if (convention.Name == "RequireAuthorization" && !endpoint.AllowAnonymous)
                    {
                        endpoint.AuthorizationRequired = true;
                    }
                    else if (convention.Name == "AllowAnonymous")
                    {
                        endpoint.AllowAnonymous = true;
                        endpoint.AuthorizationRequired = false;
                    }
                    else if (convention.Name == "DisableAntiforgery")
                    {
                        endpoint.AntiForgeryRequired = false;
                    }
                }
            }

            ApplyAuthorization(endpoint, Attributes(reader, handlerMethod.GetCustomAttributes()).ToList(), keepExisting: true);
            Add(result, endpoint, "HttpMinimalApi", AssemblyCallGraphAnalyzer.MethodId(reader, handlerHandle, assemblyPath, knownMethods), assemblyPath, MetadataTokens.GetToken(handlerHandle), reader);
            route = null;
            handler = null;
        }
    }

    private static readonly System.Text.RegularExpressions.Regex ControllerToken = new(@"\{controller(=[^}]*)?\}", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly System.Text.RegularExpressions.Regex ActionToken = new(@"\{action(=[^}]*)?\}", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>A conventional template with its controller and action parameters filled in (<c>{controller=Home}/{action=Index}/{id?}</c> → <c>Home/Index/{id?}</c>).</summary>
    private static string ExpandConventional(string template, string controller, string action) =>
        ActionToken.Replace(ControllerToken.Replace(template, controller), action);

    /// <summary>
    ///     Route templates registered with <c>MapControllerRoute(name, pattern)</c>, <c>MapRoute(name, template)</c>,
    ///     <c>MapAreaControllerRoute(name, area, pattern)</c> and <c>MapDefaultControllerRoute()</c>
    ///     (<c>{controller=Home}/{action=Index}/{id?}</c>), read from the string arguments loaded
    ///     since the previous call.
    /// </summary>
    internal static List<string> ConventionalTemplates(PEReader peReader)
    {
        var reader = peReader.GetMetadataReader();
        var templates = new List<string>();
        foreach (var methodHandle in reader.MethodDefinitions)
        {
            var method = reader.GetMethodDefinition(methodHandle);
            if (method.RelativeVirtualAddress == 0)
            {
                continue;
            }

            var strings = new List<string>();
            foreach (var (_, opCode, operand) in AssemblyCallGraphAnalyzer.Decode(peReader.GetMethodBody(method.RelativeVirtualAddress).GetILReader()))
            {
                if (opCode == OpCodes.Ldstr && operand is int stringToken)
                {
                    strings.Add(reader.GetUserString(MetadataTokens.UserStringHandle(stringToken)));
                    continue;
                }

                if (opCode != OpCodes.Call && opCode != OpCodes.Callvirt)
                {
                    continue;
                }

                var name = operand is int token ? CalleeName(reader, token)?.Name : null;
                var template = name switch
                {
                    "MapControllerRoute" or "MapRoute" => strings.Count >= 2 ? strings[1] : strings.LastOrDefault(),
                    "MapAreaControllerRoute" => strings.Count >= 3 ? strings[2] : strings.LastOrDefault(),
                    "MapDefaultControllerRoute" => "{controller=Home}/{action=Index}/{id?}",
                    _ => null
                };
                if (template is not null && !templates.Contains(template, StringComparer.Ordinal))
                {
                    templates.Add(template);
                }

                strings.Clear();
            }
        }

        return templates;
    }

    /// <summary>
    ///     Razor Pages compiled into the assembly: <c>[assembly: RazorCompiledItem(type,
    ///     "mvc.1.0.razor-page", "/Pages/X.cshtml")]</c> (route from the identifier plus the page
    ///     type's <c>RouteTemplate</c> metadata) and ASP.NET Core 2.x
    ///     <c>[assembly: RazorPage(path, type, routeTemplate)]</c>. The page type's <c>Model</c>
    ///     property names the PageModel, whose <c>On{Verb}[Handler][Async]</c> methods are the
    ///     endpoints; authorization comes from the page and PageModel attributes. Views
    ///     (<c>mvc.1.0.view</c>) are not endpoints.
    /// </summary>
    private static void AnalyzeCompiledRazorPages(MetadataReader reader, string assemblyPath, string fileName, string relativePath, IReadOnlyList<Method>? knownMethods, Result result)
    {
        if (!reader.IsAssembly)
        {
            return;
        }

        foreach (var attribute in Attributes(reader, reader.GetAssemblyDefinition().GetCustomAttributes()))
        {
            string? typeName, identifier, routeTemplate = null;
            switch (attribute.Name)
            {
                case "RazorCompiledItemAttribute" when attribute.Fixed is [string type, "mvc.1.0.razor-page", string id]:
                    (typeName, identifier) = (type, id);
                    break;
                case "RazorPageAttribute" when attribute.Fixed is [string path, string type, var template]:
                    (typeName, identifier, routeTemplate) = (type, path, template as string);
                    break;
                default:
                    continue;
            }

            var pageType = FindType(reader, typeName.Split(',')[0].Trim());
            var pageAttributes = pageType is { } page ? Attributes(reader, reader.GetTypeDefinition(page).GetCustomAttributes()).ToList() : [];
            routeTemplate ??= pageAttributes.FirstOrDefault(candidate => candidate.Name == "RazorCompiledItemMetadataAttribute" && candidate.Fixed is ["RouteTemplate", string])?.Fixed![1] as string;
            var conventional = CompiledPageRoute(identifier);
            var route = string.IsNullOrEmpty(routeTemplate) ? conventional
                : routeTemplate.StartsWith('/') || routeTemplate.StartsWith("~/", StringComparison.Ordinal) ? "/" + routeTemplate.TrimStart('~', '/')
                : conventional.TrimEnd('/') + "/" + routeTemplate;
            var resolved = Frameworks.RouteTemplateResolver.Resolve(route);
            var pagePath = resolved.Path ?? route;

            var modelType = pageType is { } typed ? ModelType(reader, typed) : null;
            var modelAttributes = modelType is { } model ? Attributes(reader, reader.GetTypeDefinition(model).GetCustomAttributes()).ToList() : [];
            var handlers = new List<(string Verb, string? Handler, MethodDefinitionHandle? Method)>();
            if (modelType is { } modelHandle)
            {
                foreach (var methodHandle in reader.GetTypeDefinition(modelHandle).GetMethods())
                {
                    var method = reader.GetMethodDefinition(methodHandle);
                    var match = PageHandlerName.Match(reader.GetString(method.Name));
                    if (match.Success && (method.Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Public)
                    {
                        var handlerName = match.Groups[2].Length > 0 ? match.Groups[2].Value : null;
                        handlers.Add((match.Groups[1].Value.ToUpperInvariant(), handlerName, methodHandle));
                    }
                }
            }

            if (handlers.Count == 0)
            {
                handlers.Add(("GET", null, null));
            }

            foreach (var (verb, handlerName, methodHandle) in handlers)
            {
                var path = handlerName is null ? pagePath : $"{pagePath}?handler={handlerName}";
                var method = methodHandle is { } handle ? reader.GetMethodDefinition(handle) : (MethodDefinition?)null;
                var declaring = method is { } definition ? reader.GetTypeDefinition(definition.GetDeclaringType()) : (TypeDefinition?)null;
                var endpoint = new ApiEndpoint
                {
                    Path = path,
                    Route = route,
                    FilePath = relativePath,
                    FileName = fileName,
                    Namespace = declaring is { } owner ? reader.GetString(owner.Namespace) : null,
                    ClassName = declaring is { } named ? reader.GetString(named.Name) : null,
                    MethodName = method is { } handlerMethod ? reader.GetString(handlerMethod.Name) : null,
                    HttpMethod = verb,
                    EndpointKind = "RazorPage",
                    RoutingKind = "Template",
                    Framework = Framework,
                    Confidence = "medium",
                    RouteParameters = resolved.Parameters,
                    Evidence = new AnalysisEvidence { Kind = AnalysisEvidenceKind.AssemblyIlDirect, Source = "assembly-il", Description = $"Compiled Razor Page {identifier} in {fileName}." }
                };
                ApplyAuthorization(endpoint, pageAttributes.Concat(modelAttributes).ToList());
                Add(result, endpoint, "HttpRazorPage", methodHandle is { } id ? AssemblyCallGraphAnalyzer.MethodId(reader, id, assemblyPath, knownMethods) : null, assemblyPath, methodHandle is { } token ? MetadataTokens.GetToken(token) : 0, reader);
            }
        }
    }

    private static readonly System.Text.RegularExpressions.Regex PageHandlerName = new(@"^On(Get|Post|Put|Delete|Patch|Head|Options)([A-Z]\w*?)?(Async)?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>A compiled page's conventional route: its path under <c>Pages</c> (or an area's), without the extension, <c>Index</c> naming its folder.</summary>
    private static string CompiledPageRoute(string identifier)
    {
        var segments = identifier.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (segments.Count >= 3 && segments[0].Equals("Areas", StringComparison.OrdinalIgnoreCase) && segments[2].Equals("Pages", StringComparison.OrdinalIgnoreCase))
        {
            segments.RemoveAt(2);
            segments.RemoveAt(0);
        }
        else if (segments.Count > 0 && segments[0].Equals("Pages", StringComparison.OrdinalIgnoreCase))
        {
            segments.RemoveAt(0);
        }

        if (segments.Count > 0)
        {
            segments[^1] = Path.GetFileNameWithoutExtension(segments[^1]);
            if (segments[^1].Equals("Index", StringComparison.OrdinalIgnoreCase))
            {
                segments.RemoveAt(segments.Count - 1);
            }
        }

        return "/" + string.Join('/', segments);
    }

    private static TypeDefinitionHandle? FindType(MetadataReader reader, string fullName)
    {
        foreach (var handle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(handle);
            var name = reader.GetString(type.Name);
            var space = reader.GetString(type.Namespace);
            if ((space.Length == 0 ? name : $"{space}.{name}") == fullName)
            {
                return handle;
            }
        }

        return null;
    }

    /// <summary>The PageModel a compiled page exposes through its <c>Model</c> property, when this assembly declares it.</summary>
    private static TypeDefinitionHandle? ModelType(MetadataReader reader, TypeDefinitionHandle page)
    {
        foreach (var propertyHandle in reader.GetTypeDefinition(page).GetProperties())
        {
            var property = reader.GetPropertyDefinition(propertyHandle);
            if (reader.GetString(property.Name) != "Model")
            {
                continue;
            }

            var blob = reader.GetBlobReader(property.Signature);
            blob.ReadSignatureHeader();
            blob.ReadCompressedInteger(); // parameter count
            var code = blob.ReadSignatureTypeCode();
            if (code is SignatureTypeCode.TypeHandle && blob.ReadTypeHandle() is { Kind: HandleKind.TypeDefinition } model)
            {
                return (TypeDefinitionHandle)model;
            }
        }

        return null;
    }

    private static void Add(Result result, ApiEndpoint endpoint, string kind, string? methodId, string assemblyPath, int token, MetadataReader reader)
    {
        if (token != 0)
        {
            result.Inputs[endpoint] = ClassifyInputs(reader, MetadataTokens.MethodDefinitionHandle(token & 0xFFFFFF));
        }

        var operationId = $"op:{Framework}:{endpoint.FilePath}#{endpoint.HttpMethod}:{endpoint.Path}:{endpoint.MethodName}";
        endpoint.OperationId = operationId;
        result.Endpoints.Add(endpoint);
        result.EntryPoints.Add(new EntryPoint
        {
            Id = $"ep:{operationId}",
            Kind = kind,
            MethodId = methodId,
            MethodName = endpoint.MethodName,
            ClassName = endpoint.ClassName,
            Namespace = endpoint.Namespace,
            FileName = endpoint.FileName,
            Path = endpoint.FilePath,
            HttpMethod = endpoint.HttpMethod,
            Route = endpoint.Path,
            AuthorizationRequired = endpoint.AuthorizationRequired,
            AuthorizationPolicies = endpoint.AuthorizationPolicies,
            Roles = endpoint.Roles,
            AllowAnonymous = endpoint.AllowAnonymous
        });
        if (methodId is not null)
        {
            result.Handlers.Add(new Handler(assemblyPath, token, methodId, endpoint.Path ?? string.Empty));
        }
    }

    private static void ApplyAuthorization(ApiEndpoint endpoint, List<DecodedAttribute> attributes, bool keepExisting = false)
    {
        if (attributes.Any(attribute => attribute.Name is "ValidateAntiForgeryTokenAttribute" or "AutoValidateAntiforgeryTokenAttribute" or "RequireAntiforgeryTokenAttribute"))
        {
            endpoint.AntiForgeryRequired = true;
        }

        if (attributes.Any(attribute => attribute.Name is "IgnoreAntiforgeryTokenAttribute"))
        {
            endpoint.AntiForgeryRequired = false;
        }

        foreach (var authorize in attributes.Where(attribute => attribute.Name == "AuthorizeAttribute"))
        {
            endpoint.AuthorizationRequired = true;
            if (authorize.FirstString is { } policy) AddDistinct(endpoint.AuthorizationPolicies, policy);
            if (authorize.Named.GetValueOrDefault("Policy") is { } named) AddDistinct(endpoint.AuthorizationPolicies, named);
            foreach (var role in (authorize.Named.GetValueOrDefault("Roles") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) AddDistinct(endpoint.Roles, role);
            foreach (var scheme in (authorize.Named.GetValueOrDefault("AuthenticationSchemes") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) AddDistinct(endpoint.AuthenticationSchemes, scheme);
        }

        // [AllowAnonymous] wins over every authorization attribute; their roles and policies stay
        // listed (the AllowAnonymousOverridesAuthorization finding reads them).
        if (attributes.Any(attribute => attribute.Name == "AllowAnonymousAttribute"))
        {
            endpoint.AllowAnonymous = true;
            endpoint.AuthorizationRequired = false;
        }
    }

    /// <summary>
    ///     A handler's request data, classified by name as the source providers classify bound
    ///     types: its parameter names and, for a parameter of a type this assembly declares, its
    ///     public properties (one level), plus that type's name when a property is shaped like a
    ///     role or enablement flag.
    /// </summary>
    private static (List<string> Sensitive, string? MassAssignmentDto) ClassifyInputs(MetadataReader reader, MethodDefinitionHandle handle)
    {
        var sensitive = new List<string>();
        string? massAssignment = null;
        var method = reader.GetMethodDefinition(handle);
        var signature = method.DecodeSignature(TypeNameProvider.Instance, null);
        foreach (var parameterHandle in method.GetParameters())
        {
            var parameter = reader.GetParameter(parameterHandle);
            if (parameter.SequenceNumber == 0)
            {
                continue;
            }

            Classify(reader.GetString(parameter.Name));
            if (parameter.SequenceNumber - 1 < signature.ParameterTypes.Length && signature.ParameterTypes[parameter.SequenceNumber - 1] is { } type && type.StartsWith('#') &&
                int.TryParse(type.AsSpan(1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var row))
            {
                var definition = reader.GetTypeDefinition(MetadataTokens.TypeDefinitionHandle(row));
                foreach (var propertyHandle in definition.GetProperties())
                {
                    var name = reader.GetString(reader.GetPropertyDefinition(propertyHandle).Name);
                    Classify(name);
                    if (massAssignment is null && Frameworks.SecurityAnalyzer.IsMassAssignmentMemberName(name))
                    {
                        massAssignment = reader.GetString(definition.Name);
                    }
                }
            }
        }

        return (sensitive, massAssignment);

        void Classify(string name)
        {
            foreach (var classification in Frameworks.DataClassifier.ClassifyName(name).Where(classification => !sensitive.Contains(classification, StringComparer.Ordinal)))
            {
                sensitive.Add(classification);
            }
        }
    }

    /// <summary>
    ///     Whether the assembly registers <c>AutoValidateAntiforgeryTokenAttribute</c> as a global
    ///     MVC filter: <c>Filters.Add&lt;AutoValidateAntiforgeryTokenAttribute&gt;()</c> (a generic
    ///     instantiation naming it) or <c>Filters.Add(new AutoValidateAntiforgeryTokenAttribute())</c>
    ///     (a <c>newobj</c> of it in a method body, never an attribute application).
    /// </summary>
    internal static bool RegistersGlobalAntiforgery(PEReader peReader)
    {
        const string Filter = "Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryTokenAttribute";
        var reader = peReader.GetMetadataReader();
        for (var row = 1; row <= reader.GetTableRowCount(TableIndex.MethodSpec); row++)
        {
            try
            {
                if (reader.GetMethodSpecification(MetadataTokens.MethodSpecificationHandle(row)).DecodeSignature(TypeNameProvider.Instance, null).Contains(Filter))
                {
                    return true;
                }
            }
            catch (BadImageFormatException)
            {
                // Not decodable: not the filter registration.
            }
        }

        foreach (var methodHandle in reader.MethodDefinitions)
        {
            var method = reader.GetMethodDefinition(methodHandle);
            if (method.RelativeVirtualAddress == 0)
            {
                continue;
            }

            foreach (var (_, opCode, operand) in AssemblyCallGraphAnalyzer.Decode(peReader.GetMethodBody(method.RelativeVirtualAddress).GetILReader()))
            {
                if (opCode == OpCodes.Newobj && operand is int token && MetadataTokens.EntityHandle(token) is { Kind: HandleKind.MemberReference } reference &&
                    reader.GetMemberReference((MemberReferenceHandle)reference).Parent is { Kind: HandleKind.TypeReference } parent &&
                    TypeNameProvider.Instance.GetTypeFromReference(reader, (TypeReferenceHandle)parent, 0) == Filter)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Type names for signatures: full names for references, <c>#row</c> for this assembly's definitions.</summary>
    private sealed class TypeNameProvider : ISignatureTypeProvider<string, object?>
    {
        public static readonly TypeNameProvider Instance = new();
        public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[]";
        public string GetByReferenceType(string elementType) => elementType + "&";
        public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => genericType;
        public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
        public string GetPinnedType(string elementType) => elementType;
        public string GetPointerType(string elementType) => elementType + "*";
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => "#" + MetadataTokens.GetRowNumber(handle).ToString(System.Globalization.CultureInfo.InvariantCulture);
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            var reference = reader.GetTypeReference(handle);
            var space = reader.GetString(reference.Namespace);
            return space.Length == 0 ? reader.GetString(reference.Name) : $"{space}.{reader.GetString(reference.Name)}";
        }

        public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
    }

    private static void AddDistinct(List<string> list, string value)
    {
        if (!list.Contains(value, StringComparer.Ordinal)) list.Add(value);
    }

    private static string Combine(string? prefix, string? template)
    {
        if (template is not null && (template.StartsWith('/') || template.StartsWith("~/", StringComparison.Ordinal)))
        {
            return "/" + template.TrimStart('~', '/');
        }

        var parts = new[] { prefix, template }.Where(part => !string.IsNullOrEmpty(part)).Select(part => part!.Trim('/'));
        return "/" + string.Join('/', parts);
    }

    /// <summary>The type's attributes and those of the base types this assembly defines (a base controller's <c>[Authorize]</c>).</summary>
    private static IEnumerable<DecodedAttribute> InheritedAttributes(MetadataReader reader, TypeDefinition type)
    {
        foreach (var attribute in Attributes(reader, type.GetCustomAttributes()))
        {
            yield return attribute;
        }

        var baseType = type.BaseType;
        for (var depth = 0; depth < 16 && baseType.Kind == HandleKind.TypeDefinition; depth++)
        {
            var definition = reader.GetTypeDefinition((TypeDefinitionHandle)baseType);
            foreach (var attribute in Attributes(reader, definition.GetCustomAttributes()).Where(attribute => attribute.Name is "AuthorizeAttribute" or "AllowAnonymousAttribute"))
            {
                yield return attribute;
            }

            baseType = definition.BaseType;
        }
    }

    private sealed record DecodedAttribute(string Name, string? FirstString, Dictionary<string, string?> Named, List<object?>? Fixed = null);

    private static IEnumerable<DecodedAttribute> Attributes(MetadataReader reader, CustomAttributeHandleCollection handles)
    {
        foreach (var handle in handles)
        {
            var attribute = reader.GetCustomAttribute(handle);
            var parent = attribute.Constructor.Kind switch
            {
                HandleKind.MemberReference => reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent,
                HandleKind.MethodDefinition => reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType(),
                _ => default
            };
            var name = parent.Kind switch
            {
                HandleKind.TypeReference => reader.GetString(reader.GetTypeReference((TypeReferenceHandle)parent).Name),
                HandleKind.TypeDefinition => reader.GetString(reader.GetTypeDefinition((TypeDefinitionHandle)parent).Name),
                _ => null
            };
            if (name is null)
            {
                continue;
            }

            string? first = null;
            List<object?>? fixedValues = null;
            var named = new Dictionary<string, string?>(StringComparer.Ordinal);
            try
            {
                var value = attribute.DecodeValue(StringTypeProvider.Instance);
                first = value.FixedArguments.Select(argument => argument.Value).OfType<string>().FirstOrDefault();
                fixedValues = [.. value.FixedArguments.Select(argument => argument.Value)];
                foreach (var argument in value.NamedArguments)
                {
                    if (argument.Name is { } argumentName) named[argumentName] = argument.Value as string;
                }
            }
            catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException or ArgumentException or NotSupportedException)
            {
                // An enum or type argument from an assembly we cannot see: keep the name.
            }

            yield return new DecodedAttribute(name, first, named, fixedValues);
        }
    }

    private static (string Name, string Type)? CalleeName(MetadataReader reader, int token)
    {
        var handle = MetadataTokens.EntityHandle(token);
        if (handle.Kind == HandleKind.MethodSpecification)
        {
            handle = reader.GetMethodSpecification((MethodSpecificationHandle)handle).Method;
        }

        return handle.Kind switch
        {
            HandleKind.MemberReference => (reader.GetString(reader.GetMemberReference((MemberReferenceHandle)handle).Name), TypeName(reader, reader.GetMemberReference((MemberReferenceHandle)handle).Parent)),
            HandleKind.MethodDefinition => (reader.GetString(reader.GetMethodDefinition((MethodDefinitionHandle)handle).Name), TypeName(reader, reader.GetMethodDefinition((MethodDefinitionHandle)handle).GetDeclaringType())),
            _ => null
        };
    }

    private static string TypeName(MetadataReader reader, EntityHandle handle) => handle.Kind switch
    {
        HandleKind.TypeReference => reader.GetString(reader.GetTypeReference((TypeReferenceHandle)handle).Name),
        HandleKind.TypeDefinition => reader.GetString(reader.GetTypeDefinition((TypeDefinitionHandle)handle).Name),
        _ => string.Empty
    };

    /// <summary>Decodes attribute arguments far enough to read strings; every other type is opaque.</summary>
    private sealed class StringTypeProvider : ICustomAttributeTypeProvider<string>
    {
        public static readonly StringTypeProvider Instance = new();
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
        public string GetSystemType() => "System.Type";
        public string GetSZArrayType(string elementType) => elementType + "[]";
        // Full names: a `System.Type` argument is told apart from an enum by its name.
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => Qualified(reader.GetString(reader.GetTypeDefinition(handle).Namespace), reader.GetString(reader.GetTypeDefinition(handle).Name));
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => Qualified(reader.GetString(reader.GetTypeReference(handle).Namespace), reader.GetString(reader.GetTypeReference(handle).Name));
        private static string Qualified(string space, string name) => space.Length == 0 ? name : $"{space}.{name}";
        public string GetTypeFromSerializedName(string name) => name;
        public PrimitiveTypeCode GetUnderlyingEnumType(string type) => PrimitiveTypeCode.Int32;
        public bool IsSystemType(string type) => type == "System.Type";
    }
}
