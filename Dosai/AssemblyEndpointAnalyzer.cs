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

    internal sealed record Result(List<ApiEndpoint> Endpoints, List<EntryPoint> EntryPoints, List<Handler> Handlers);

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
        foreach (var file in files)
        {
            if (root is not null && TreeFrameworks.IsBuiltFromSource(root, file))
            {
                continue;
            }

            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var peReader = new PEReader(stream);
                if (!peReader.HasMetadata)
                {
                    continue;
                }

                AnalyzeAssembly(path, file, peReader, knownMethods, result);
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
            AnalyzeAssembly(basePath, assemblyPath, peReader, null, result);
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException)
        {
            return [];
        }

        return result.Handlers;
    }

    private static void AnalyzeAssembly(string basePath, string assemblyPath, PEReader peReader, IReadOnlyList<Method>? knownMethods, Result result)
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
                AnalyzeController(reader, typeHandle, type, assemblyPath, fileName, relativePath, knownMethods, result);
            }
        }

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

    private static void AnalyzeController(MetadataReader reader, TypeDefinitionHandle typeHandle, TypeDefinition type, string assemblyPath, string fileName, string relativePath, IReadOnlyList<Method>? knownMethods, Result result)
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

            var conventional = false;
            if (routes.Count == 0)
            {
                // Attribute-routed API controllers expose only attributed actions; an MVC
                // controller's public actions are reachable through the conventional route.
                if (isApiController || classRoutes.Any(route => route is not null))
                {
                    continue;
                }

                routes.Add(("ANY", null));
                conventional = true;
            }

            var methodId = AssemblyCallGraphAnalyzer.MethodId(reader, methodHandle, assemblyPath, knownMethods);
            foreach (var classRoute in classRoutes)
            {
                foreach (var (verb, template) in routes)
                {
                    var combined = conventional ? $"/{controllerName}/{actionName}" : Combine(classRoute, template);
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
                    Add(result, endpoint, "HttpController", methodId, assemblyPath, MetadataTokens.GetToken(methodHandle));
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
                }
            }

            ApplyAuthorization(endpoint, Attributes(reader, handlerMethod.GetCustomAttributes()).ToList(), keepExisting: true);
            Add(result, endpoint, "HttpMinimalApi", AssemblyCallGraphAnalyzer.MethodId(reader, handlerHandle, assemblyPath, knownMethods), assemblyPath, MetadataTokens.GetToken(handlerHandle));
            route = null;
            handler = null;
        }
    }

    private static void Add(Result result, ApiEndpoint endpoint, string kind, string methodId, string assemblyPath, int token)
    {
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
        result.Handlers.Add(new Handler(assemblyPath, token, methodId, endpoint.Path ?? string.Empty));
    }

    private static void ApplyAuthorization(ApiEndpoint endpoint, List<DecodedAttribute> attributes, bool keepExisting = false)
    {
        if (attributes.Any(attribute => attribute.Name == "AllowAnonymousAttribute"))
        {
            endpoint.AllowAnonymous = true;
            endpoint.AuthorizationRequired = false;
            return;
        }

        foreach (var authorize in attributes.Where(attribute => attribute.Name == "AuthorizeAttribute"))
        {
            endpoint.AuthorizationRequired = true;
            if (authorize.FirstString is { } policy) AddDistinct(endpoint.AuthorizationPolicies, policy);
            if (authorize.Named.GetValueOrDefault("Policy") is { } named) AddDistinct(endpoint.AuthorizationPolicies, named);
            foreach (var role in (authorize.Named.GetValueOrDefault("Roles") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) AddDistinct(endpoint.Roles, role);
            foreach (var scheme in (authorize.Named.GetValueOrDefault("AuthenticationSchemes") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) AddDistinct(endpoint.AuthenticationSchemes, scheme);
        }

        if (!keepExisting && endpoint.AuthorizationRequired is null && attributes.Count > 0)
        {
            endpoint.AuthorizationRequired = null;
        }
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

    private sealed record DecodedAttribute(string Name, string? FirstString, Dictionary<string, string?> Named);

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
            var named = new Dictionary<string, string?>(StringComparer.Ordinal);
            try
            {
                var value = attribute.DecodeValue(StringTypeProvider.Instance);
                first = value.FixedArguments.Select(argument => argument.Value).OfType<string>().FirstOrDefault();
                foreach (var argument in value.NamedArguments)
                {
                    if (argument.Name is { } argumentName) named[argumentName] = argument.Value as string;
                }
            }
            catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException or ArgumentException or NotSupportedException)
            {
                // An enum or type argument from an assembly we cannot see: keep the name.
            }

            yield return new DecodedAttribute(name, first, named);
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
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => reader.GetString(reader.GetTypeDefinition(handle).Name);
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => reader.GetString(reader.GetTypeReference(handle).Name);
        public string GetTypeFromSerializedName(string name) => name;
        public PrimitiveTypeCode GetUnderlyingEnumType(string type) => PrimitiveTypeCode.Int32;
        public bool IsSystemType(string type) => type == "System.Type";
    }
}
