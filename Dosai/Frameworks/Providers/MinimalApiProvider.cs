using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Depscan.Frameworks.Providers;

/// <summary>
///     ASP.NET Core Minimal APIs: Map* endpoint registration invocations with MapGroup prefix
///     composition (fluent and via group variables), MapMethods verb arrays, and a node-by-node
///     fluent-chain walk for authorization/metadata. Map* calls that mount other frameworks
///     (MapHub, MapGrpcService, MapGraphQL, MapMcp,...) are recorded as mount points for the
///     owning provider instead of emitting duplicate endpoints.
/// </summary>
public sealed class MinimalApiProvider : IFrameworkProvider
{
    private static readonly Dictionary<string, string> MapVerbs = new(StringComparer.Ordinal)
    {
        ["MapGet"] = "GET",
        ["MapPost"] = "POST",
        ["MapPut"] = "PUT",
        ["MapDelete"] = "DELETE",
        ["MapPatch"] = "PATCH"
    };

    private static readonly Dictionary<string, string> MountKinds = new(StringComparer.Ordinal)
    {
        ["MapHub"] = "hub",
        ["MapGrpcService"] = "grpc-service",
        ["MapGraphQL"] = "graphql",
        ["MapMcp"] = "mcp",
        ["MapControllers"] = "controllers",
        ["MapRazorPages"] = "razor-pages",
        ["MapODataRoute"] = "odata",
        ["MapHangfireDashboard"] = "hangfire-dashboard",
        ["MapStaticAssets"] = "static-assets"
    };

    /// <summary>
    ///     The endpoints `MapIdentityApi&lt;TUser&gt;()` registers (.NET 8+), relative to its group;
    ///     the `/manage` endpoints require an authenticated user.
    /// </summary>
    private static readonly (string Verb, string Path, bool Authenticated)[] IdentityApiEndpoints =
    [
        ("POST", "/register", false), ("POST", "/login", false), ("POST", "/refresh", false),
        ("GET", "/confirmEmail", false), ("POST", "/resendConfirmationEmail", false),
        ("POST", "/forgotPassword", false), ("POST", "/resetPassword", false),
        ("POST", "/manage/2fa", true), ("GET", "/manage/info", true), ("POST", "/manage/info", true)
    ];

    public string Id => "minimal-api";

    public string DisplayName => "ASP.NET Core Minimal APIs";

    public bool AppliesTo(FrameworkContext ctx) => ctx.CSharp is not null;

    public void Analyze(FrameworkContext ctx, FrameworkResults results)
    {
        // Prefixes are scoped per file: two Program.cs files both declaring `var g =...` in the
        // same solution must not see each other's prefixes.
        var groupPrefixesByFile = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        Dictionary<string, string> PrefixesFor(string filePath)
        {
            if (!groupPrefixesByFile.TryGetValue(filePath, out var prefixes))
            {
                prefixes = new Dictionary<string, string>(StringComparer.Ordinal);
                groupPrefixesByFile[filePath] = prefixes;
            }

            return prefixes;
        }

        var emittedEndpointIds = new HashSet<string>(StringComparer.Ordinal);
        // Authorization and antiforgery conventions applied to a group variable, in its fluent
        // initializer (`app.MapGroup("/api").RequireAuthorization()`) or a later statement
        // (`api.RequireAuthorization();`), keyed like the prefixes.
        var groupMetadataByFile = new Dictionary<string, Dictionary<string, ApiEndpoint>>(StringComparer.Ordinal);
        Dictionary<string, ApiEndpoint> MetadataFor(string filePath)
        {
            if (!groupMetadataByFile.TryGetValue(filePath, out var metadata))
            {
                metadata = new Dictionary<string, ApiEndpoint>(StringComparer.Ordinal);
                groupMetadataByFile[filePath] = metadata;
            }

            return metadata;
        }

        // Pass 1: collect MapGroup prefixes keyed by the variable they are assigned to, so
        // `var api = app.MapGroup("/api"); api.MapGet(...)` composes transitively. The initializer
        // is often a fluent chain (var v1 = app.MapGroup("/x").HasApiVersion(1,0)), so the prefix
        // is recovered by walking the chain rather than requiring the invocation itself to be
        // MapGroup.
        foreach (var tree in ctx.CSharpTrees)
        {
            var prefixes = PrefixesFor(tree.FilePath);
            var root = tree.GetCompilationUnitRoot();
            // A prefix only ever comes from a MapGroup call in this file, directly or through a
            // variable this loop recorded from one: a file without one has nothing to record.
            foreach (var declaration in ctx.Invokes(tree, static name => name == "MapGroup") ? root.DescendantNodes().OfType<VariableDeclaratorSyntax>() : [])
            {
                if (declaration.Initializer?.Value is not InvocationExpressionSyntax invocation)
                {
                    continue;
                }

                var prefix = GroupPrefixOf(invocation, prefixes);
                if (ProviderHelpers.InvocationName(invocation) == "MapGroup")
                {
                    prefix = RouteTemplateResolver.CombinePrefix(prefix, FirstStringArgument(invocation));
                }

                if (prefix.Length > 0 && declaration.Identifier.Text.Length > 0)
                {
                    prefixes[declaration.Identifier.Text] = prefix;
                }

                if (IsGroupChain(invocation, prefixes) && declaration.Identifier.Text.Length > 0)
                {
                    MetadataFor(tree.FilePath)[declaration.Identifier.Text] = GroupMetadataOf(invocation, MetadataFor(tree.FilePath));
                }
            }

            // `api.RequireAuthorization();` after the declaration: conventions apply to the whole
            // group whatever the statement order.
            var metadata = MetadataFor(tree.FilePath);
            if (metadata.Count > 0)
            {
                foreach (var statement in root.DescendantNodes().OfType<ExpressionStatementSyntax>())
                {
                    if (statement.Expression is InvocationExpressionSyntax chain && RootIdentifier(chain) is { } groupName && metadata.TryGetValue(groupName, out var groupMetadata) &&
                        !ReceiverChain(chain).Any(link => ProviderHelpers.InvocationName(link) is "MapGroup" || MapVerbs.ContainsKey(ProviderHelpers.InvocationName(link))))
                    {
                        foreach (var link in ReceiverChain(chain))
                        {
                            ApplyConvention(groupMetadata, link);
                        }
                    }
                }
            }
        }

        // Pass 2: Map* endpoint registrations.
        foreach (var tree in ctx.CSharpTrees)
        {
            var rawUrls = ctx.RawUrlsFor(tree);
            var groupPrefixes = PrefixesFor(tree.FilePath);
            var groupMetadata = MetadataFor(tree.FilePath);
            foreach (var invocation in ctx.InvocationsNamed(tree, static name => MountKinds.ContainsKey(name) || MapVerbs.ContainsKey(name) || name is "MapMethods" or "MapFallback" or "MapHealthChecks" or "Map" or "MapIdentityApi" or "MapOpenApi"))
            {
                var name = ProviderHelpers.InvocationName(invocation);
                if (name == "Map")
                {
                    // IEndpointRouteBuilder.Map(pattern, handler) is an any-verb endpoint;
                    // IApplicationBuilder.Map(path, branch) is middleware branching, and an
                    // AutoMapper `mapper.Map<T>(source)` is no route at all.
                    if (IsEndpointMap(ctx, invocation))
                    {
                        AddEndpoint(ctx, results, invocation, "ANY", groupPrefixes, rawUrls, emittedEndpointIds, groupMetadata: groupMetadata);
                    }

                    continue;
                }

                if (name == "MapIdentityApi")
                {
                    foreach (var (identityVerb, identityPath, authenticated) in IdentityApiEndpoints)
                    {
                        AddEndpoint(ctx, results, invocation, identityVerb, groupPrefixes, rawUrls, emittedEndpointIds, identityPath, groupMetadata, requireAuthorization: authenticated, seedHandler: false);
                    }

                    continue;
                }

                if (name == "MapOpenApi")
                {
                    AddEndpoint(ctx, results, invocation, "GET", groupPrefixes, rawUrls, emittedEndpointIds, FirstStringArgument(invocation) ?? "/openapi/{documentName}.json", groupMetadata, seedHandler: false);
                    continue;
                }

                if (MountKinds.TryGetValue(name, out var mountKind))
                {
                    var mountPath = FirstStringArgument(invocation);
                    var typeName = (invocation.Expression as Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax)?.Name as GenericNameSyntax;
                    ctx.MountPoints.Add(new MountPoint(mountKind, RouteTemplateResolver.NormalizePrefix(string.IsNullOrWhiteSpace(mountPath) ? "/" : mountPath), tree.FilePath, invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1, typeName?.TypeArgumentList.Arguments.FirstOrDefault()?.ToString()));
                    continue;
                }

                if (!MapVerbs.TryGetValue(name, out var verb) && name != "MapMethods" && name != "MapFallback" && name != "MapHealthChecks")
                {
                    continue;
                }

                var httpMethod = verb ?? "ANY";
                if (name == "MapMethods")
                {
                    // MapMethods("/x", new[] { "GET", "POST" }, handler): the verb array was never
                    // read by the old analyzer; emit one endpoint per declared verb.
                    var verbs = MethodVerbs(invocation);
                    if (verbs.Count == 0)
                    {
                        verbs.Add("ANY");
                    }

                    foreach (var declaredVerb in verbs)
                    {
                        AddEndpoint(ctx, results, invocation, declaredVerb, groupPrefixes, rawUrls, emittedEndpointIds, groupMetadata: groupMetadata);
                    }

                    continue;
                }

                if (name == "MapFallback")
                {
                    var template = FirstStringArgument(invocation) ?? "/{fallback:nonfile}";
                    AddEndpoint(ctx, results, invocation, "ANY", groupPrefixes, rawUrls, emittedEndpointIds, template, groupMetadata);
                    continue;
                }

                if (name == "MapHealthChecks" && !MountKinds.ContainsKey(name))
                {
                    var path = FirstStringArgument(invocation) ?? "/health";
                    AddEndpoint(ctx, results, invocation, "GET", groupPrefixes, rawUrls, emittedEndpointIds, path, groupMetadata);
                    continue;
                }

                AddEndpoint(ctx, results, invocation, httpMethod, groupPrefixes, rawUrls, emittedEndpointIds, groupMetadata: groupMetadata);
            }
        }
    }

    /// <summary>Whether a `Map(...)` call registers an endpoint (pattern plus handler) rather than a middleware branch or a mapper call.</summary>
    private static bool IsEndpointMap(FrameworkContext ctx, InvocationExpressionSyntax invocation)
    {
        if (invocation.ArgumentList.Arguments.Count < 2 || invocation.ArgumentList.Arguments[0].Expression is not LiteralExpressionSyntax { Token.Value: string })
        {
            return false;
        }

        if (ctx.CSharp?.GetSemanticModel(invocation.SyntaxTree).GetSymbolInfo(invocation).Symbol is IMethodSymbol bound)
        {
            return bound.ContainingType?.Name is not ("MapExtensions" or "IMapper" or "Mapper") &&
                   !bound.Parameters.Any(parameter => parameter.Type.ToDisplayString().Contains("IApplicationBuilder", StringComparison.Ordinal));
        }

        // Unbound: a branch configures an application builder (`branch => branch.Run(...)`).
        var handler = invocation.ArgumentList.Arguments[1].Expression;
        return handler is not LambdaExpressionSyntax lambda ||
               !lambda.Body.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Any(call => ProviderHelpers.InvocationName(call) is "Run" or "Use" or "UseRouting" or "UseEndpoints" or "UseMiddleware");
    }

    /// <summary>The invocation and the invocations below it on its receiver chain, outermost first.</summary>
    private static IEnumerable<InvocationExpressionSyntax> ReceiverChain(InvocationExpressionSyntax invocation)
    {
        for (ExpressionSyntax? current = invocation; current is InvocationExpressionSyntax link; current = (link.Expression as MemberAccessExpressionSyntax)?.Expression)
        {
            yield return link;
        }
    }

    private static string? RootIdentifier(InvocationExpressionSyntax invocation) =>
        (ReceiverChain(invocation).Last().Expression as MemberAccessExpressionSyntax)?.Expression is IdentifierNameSyntax identifier ? identifier.Identifier.Text : null;

    private static bool IsGroupChain(InvocationExpressionSyntax invocation, Dictionary<string, string> prefixes) =>
        ReceiverChain(invocation).Any(link => ProviderHelpers.InvocationName(link) == "MapGroup") ||
        RootIdentifier(invocation) is { } root && prefixes.ContainsKey(root);

    /// <summary>The conventions a group's fluent chain applies, on top of those of the group it was made from.</summary>
    private static ApiEndpoint GroupMetadataOf(InvocationExpressionSyntax invocation, Dictionary<string, ApiEndpoint> groupMetadata)
    {
        var metadata = new ApiEndpoint();
        if (RootIdentifier(invocation) is { } parent && groupMetadata.TryGetValue(parent, out var parentMetadata))
        {
            MergeConventions(metadata, parentMetadata);
        }

        foreach (var link in ReceiverChain(invocation))
        {
            ApplyConvention(metadata, link);
        }

        return metadata;
    }

    private static void ApplyConvention(ApiEndpoint metadata, InvocationExpressionSyntax link)
    {
        switch (ProviderHelpers.InvocationName(link))
        {
            case "AllowAnonymous":
                metadata.AllowAnonymous = true;
                break;
            case "RequireAuthorization":
                metadata.AuthorizationRequired = true;
                foreach (var policy in ProviderHelpers.StringArguments(link)) AddDistinct(metadata.AuthorizationPolicies, policy);
                break;
            case "RequireCors":
                foreach (var policy in ProviderHelpers.StringArguments(link)) AddDistinct(metadata.CorsPolicies, policy);
                break;
            case "DisableAntiforgery":
                metadata.AntiForgeryRequired = false;
                break;
            case "RequireAntiforgery":
                metadata.AntiForgeryRequired = true;
                break;
        }
    }

    /// <summary>
    ///     Folds inherited conventions (a group's, or a handler's attributes) into an endpoint:
    ///     AllowAnonymous anywhere wins, as at runtime; a requirement anywhere else applies; an
    ///     endpoint's own antiforgery choice beats the inherited one.
    /// </summary>
    private static void MergeConventions(ApiEndpoint endpoint, ApiEndpoint inherited)
    {
        endpoint.AllowAnonymous |= inherited.AllowAnonymous;
        if (inherited.AuthorizationRequired == true)
        {
            endpoint.AuthorizationRequired = true;
        }

        foreach (var policy in inherited.AuthorizationPolicies) AddDistinct(endpoint.AuthorizationPolicies, policy);
        foreach (var role in inherited.Roles) AddDistinct(endpoint.Roles, role);
        foreach (var scheme in inherited.AuthenticationSchemes) AddDistinct(endpoint.AuthenticationSchemes, scheme);
        foreach (var scope in inherited.RequiredScopes) AddDistinct(endpoint.RequiredScopes, scope);
        foreach (var claim in inherited.RequiredClaims) AddDistinct(endpoint.RequiredClaims, claim);
        foreach (var policy in inherited.CorsPolicies) AddDistinct(endpoint.CorsPolicies, policy);
        endpoint.AntiForgeryRequired ??= inherited.AntiForgeryRequired;
        if (endpoint.AllowAnonymous)
        {
            endpoint.AuthorizationRequired = false;
        }
    }

    private static void AddDistinct(List<string> values, string value)
    {
        if (!values.Contains(value, StringComparer.Ordinal))
        {
            values.Add(value);
        }
    }

    /// <summary>The group conventions above an endpoint: fluent links between it and its MapGroup, and the group variable it hangs off.</summary>
    private static ApiEndpoint? InheritedGroupMetadata(InvocationExpressionSyntax invocation, Dictionary<string, ApiEndpoint>? groupMetadata)
    {
        var receiver = (invocation.Expression as MemberAccessExpressionSyntax)?.Expression;
        if (receiver is IdentifierNameSyntax identifier)
        {
            return groupMetadata is not null && groupMetadata.TryGetValue(identifier.Identifier.Text, out var variableMetadata) ? variableMetadata : null;
        }

        if (receiver is not InvocationExpressionSyntax receiverInvocation || !ReceiverChain(receiverInvocation).Any(link => ProviderHelpers.InvocationName(link) == "MapGroup"))
        {
            return null;
        }

        return GroupMetadataOf(receiverInvocation, groupMetadata ?? []);
    }

    /// <summary>
    ///     Computes the MapGroup prefix for an endpoint or group registration by walking the
    ///     receiver chain DOWNWARD (group.MapGet(...), app.MapGroup("/a").MapGet(...)): in a fluent
    ///     chain the MapGroup invocation is a child of the endpoint invocation, not an ancestor.
    /// </summary>
    private static string GroupPrefixOf(InvocationExpressionSyntax invocation, Dictionary<string, string> groupPrefixes)
    {
        var prefix = string.Empty;
        var receiver = (invocation.Expression as MemberAccessExpressionSyntax)?.Expression;
        var guard = 0;
        while (receiver is not null && guard++ < 16)
        {
            switch (receiver)
            {
                case InvocationExpressionSyntax receiverInvocation when ProviderHelpers.InvocationName(receiverInvocation) == "MapGroup":
                {
                    var literal = FirstStringArgument(receiverInvocation);
                    if (literal is not null)
                    {
                        prefix = RouteTemplateResolver.CombinePrefix(literal, prefix);
                    }

                    receiver = (receiverInvocation.Expression as MemberAccessExpressionSyntax)?.Expression;
                    break;
                }
                case IdentifierNameSyntax identifier when groupPrefixes.TryGetValue(identifier.Identifier.Text, out var variablePrefix):
                    return RouteTemplateResolver.CombinePrefix(variablePrefix, prefix);
                case InvocationExpressionSyntax intermediate:
                    // A non-MapGroup fluent link (HasApiVersion, RequireAuthorization,...): keep
                    // walking outward, the MapGroup may sit further up the chain.
                    receiver = (intermediate.Expression as MemberAccessExpressionSyntax)?.Expression;
                    break;
                default:
                    return prefix;
            }
        }

        return prefix;
    }

    /// <summary>
    ///     Seeds the parameters of a Map* handler: a method group (`MapGet("/x", GetAllItems)`) or
    ///     an inline lambda (`MapPost("/x", (PetUnion pet) => ...)` - the dominant minimal-API form,
    ///     and the only one for .NET 11's union-typed JSON bodies). Minimal-API binding pulls
    ///     route/query/header/body values, so every handler parameter (except infrastructure types)
    ///     is attacker-influenced, exactly like a controller action. Lambda parameters are seeded
    ///     from syntax and anchored to their own line, because the Map* overloads are usually
    ///     unresolved and leave the lambda's converted symbol error-typed.
    /// </summary>
    private static void SeedHandlerParameters(FrameworkContext ctx, FrameworkResults results, InvocationExpressionSyntax invocation, string endpointPath)
    {
        if (ctx.CSharp is null)
        {
            return;
        }

        foreach (var argument in invocation.ArgumentList.Arguments)
        {
            if (argument.NameColon is not null)
            {
                continue;
            }

            IEnumerable<ParameterSyntax>? lambdaParameters = argument.Expression switch
            {
                SimpleLambdaExpressionSyntax simple => [simple.Parameter],
                ParenthesizedLambdaExpressionSyntax parenthesized => parenthesized.ParameterList.Parameters,
                _ => null
            };
            if (lambdaParameters is not null)
            {
                foreach (var parameter in lambdaParameters)
                {
                    if (IsInfrastructureParameterSyntax(parameter))
                    {
                        continue;
                    }

                    results.TaintSeeds.Add(new FrameworkTaintSeed
                    {
                        MethodName = FrameworkTaintSeedIndex.LambdaHandlerMethodName,
                        ParameterName = parameter.Identifier.Text,
                        FileName = Path.GetFileName(invocation.SyntaxTree.FilePath),
                        LineNumber = parameter.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                        BindingSource = "http-body",
                        TaintKind = "http",
                        FrameworkId = "minimal-api",
                        EndpointPath = endpointPath,
                        Confidence = ConfidenceTiers.Syntactic
                    });
                }

                break;
            }

            var handlerName = argument.Expression switch
            {
                IdentifierNameSyntax identifier => identifier.Identifier.Text,
                Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax _, Name: IdentifierNameSyntax member } => member.Identifier.Text,
                _ => null
            };
            if (handlerName is null || handlerName.Equals("Run", StringComparison.Ordinal) || handlerName.Equals("MapGroup", StringComparison.Ordinal))
            {
                continue;
            }

            var symbolInfo = ctx.CSharp.GetSemanticModel(invocation.SyntaxTree).GetSymbolInfo(argument.Expression);
            // When the Map* overloads come from an unresolved framework reference, the method-group
            // conversion cannot complete and Symbol comes back null; the candidate set still names
            // the group's methods, so fall back to the unique candidate with this handler's name.
            var methodSymbol = symbolInfo.Symbol as IMethodSymbol
                               ?? symbolInfo.CandidateSymbols.OfType<IMethodSymbol>().Where(candidate => candidate.Name == handlerName).GroupBy(candidate => candidate.ToDisplayString()).Select(group => group.First()).FirstOrDefault();
            if (methodSymbol is null)
            {
                continue;
            }

            foreach (var parameter in methodSymbol.Parameters)
            {
                if (IsInfrastructureParameter(parameter))
                {
                    continue;
                }

                results.TaintSeeds.Add(new FrameworkTaintSeed
                {
                    MethodName = methodSymbol.Name,
                    ParameterName = parameter.Name,
                    Namespace = methodSymbol.ContainingType?.ContainingNamespace?.ToDisplayString(),
                    ClassName = methodSymbol.ContainingType?.Name,
                    FileName = Path.GetFileName(invocation.SyntaxTree.FilePath),
                    MethodSignature = Depscan.Dosai.FormatMethodSignature(methodSymbol),
                    LineNumber = methodSymbol.Locations.FirstOrDefault()?.GetLineSpan().StartLinePosition.Line + 1 ?? 0,
                    BindingSource = "minimal-api",
                    TaintKind = "http",
                    FrameworkId = "minimal-api",
                    EndpointPath = endpointPath,
                    Confidence = ConfidenceTiers.Semantic
                });
            }

            break;
        }
    }

    /// <summary>
    ///     An inline handler's own method: the lambda the compiler emits, whose id
    ///     (<see cref="Depscan.Dosai.FormatMethodSignature" />, named by
    ///     <see cref="Depscan.Dosai.NestedFunctionName" /> as <c>&lt;&lt;Main&gt;$&gt;lambda2</c>) is
    ///     the call graph's and the data flow's id for the handler body. The lambda symbol binds
    ///     even when the Map* overload does not (unresolved framework references).
    /// </summary>
    private static IMethodSymbol? LambdaSymbol(FrameworkContext ctx, LambdaExpressionSyntax lambda) =>
        ctx.CSharp?.GetSemanticModel(lambda.SyntaxTree).GetSymbolInfo(lambda).Symbol as IMethodSymbol is { MethodKind: MethodKind.AnonymousFunction } symbol ? symbol : null;

    /// <summary>
    ///     The handler argument of a Map* call: a lambda's attributes, or a method group's symbol
    ///     and the attributes on its declaration.
    /// </summary>
    private static (IMethodSymbol? Method, List<AttributeSyntax> Attributes)? HandlerOf(FrameworkContext ctx, InvocationExpressionSyntax invocation)
    {
        foreach (var argument in invocation.ArgumentList.Arguments.Where(argument => argument.NameColon is null))
        {
            switch (argument.Expression)
            {
                case ParenthesizedLambdaExpressionSyntax lambda:
                    return (LambdaSymbol(ctx, lambda), ProviderHelpers.AttributesOf(lambda.AttributeLists).ToList());
                case SimpleLambdaExpressionSyntax simple:
                    return (LambdaSymbol(ctx, simple), ProviderHelpers.AttributesOf(simple.AttributeLists).ToList());
                case IdentifierNameSyntax or MemberAccessExpressionSyntax when ctx.CSharp is not null:
                {
                    var symbolInfo = ctx.CSharp.GetSemanticModel(invocation.SyntaxTree).GetSymbolInfo(argument.Expression);
                    var name = argument.Expression is IdentifierNameSyntax identifier ? identifier.Identifier.Text : ((MemberAccessExpressionSyntax)argument.Expression).Name.Identifier.Text;
                    var method = symbolInfo.Symbol as IMethodSymbol ??
                                 symbolInfo.CandidateSymbols.OfType<IMethodSymbol>().Where(candidate => candidate.Name == name).GroupBy(candidate => candidate.ToDisplayString()).Select(group => group.First()).FirstOrDefault();
                    if (method is null || method.MethodKind != MethodKind.Ordinary)
                    {
                        continue;
                    }

                    var attributes = method.DeclaringSyntaxReferences
                        .Select(reference => reference.GetSyntax())
                        .OfType<MethodDeclarationSyntax>()
                        .SelectMany(declaration => ProviderHelpers.AttributesOf(declaration.AttributeLists))
                        .ToList();
                    return (method, attributes);
                }
            }
        }

        return null;
    }

    /// <summary>
    ///     Syntax twin of <see cref="IsInfrastructureParameter" /> for inline lambda handlers,
    ///     whose parameter types often fail to resolve. Applies the same exclusions by type-name
    ///     text and written attributes, so a `CancellationToken ct`, an injected `ILogger<T>` or
    ///     `ICatalogService`, and a `[FromServices]`-annotated parameter never become untrusted
    ///     sources.
    /// </summary>
    private static bool IsInfrastructureParameterSyntax(ParameterSyntax parameter)
    {
        var typeName = parameter.Type?.ToString() ?? string.Empty;
        var typeNameLastSegment = typeName.Split('.').LastOrDefault() ?? string.Empty;
        if (typeNameLastSegment.Contains("CancellationToken", StringComparison.Ordinal) ||
            typeNameLastSegment.Contains("HttpContext", StringComparison.Ordinal) ||
            typeNameLastSegment.Contains("ClaimsPrincipal", StringComparison.Ordinal) ||
            typeNameLastSegment.Contains("IServiceProvider", StringComparison.Ordinal) ||
            typeNameLastSegment.StartsWith("ILogger", StringComparison.Ordinal))
        {
            return true;
        }

        if (parameter.AttributeLists.SelectMany(list => list.Attributes).Any(attribute => attribute.Name.ToString() is "FromServices" or "FromServicesAttribute" or "FromKeyedServices" or "FromKeyedServicesAttribute"))
        {
            return true;
        }

        // I + Upper-second-letter is the conventional interface spelling; classes like "Item"
        // fail the second-letter check. Mirrors the semantic-path heuristic.
        if (typeNameLastSegment.Length > 2 && typeNameLastSegment[0] == 'I' && char.IsUpper(typeNameLastSegment[1]))
        {
            return true;
        }

        if (parameter.Identifier.Text is "ct" or "cancellationToken" or "cancellationTokenSource")
        {
            return true;
        }

        return false;
    }

    /// <summary>
    ///     Minimal-API binding is ambient for infrastructure types and DI for registered services:
    ///     neither is attacker-controlled. Mirrors the filters the MVC (<c>BindingSourceOf</c>) and
    ///     MCP (<c>ToolSchemaBuilder.IsInfrastructureParameter</c>) providers already apply, so a
    ///     <c>CatalogService</c> or <c>HttpContext</c> parameter never becomes a phantom untrusted
    ///     source. Interface types are skipped even when unresolved: an unbound interface parameter
    ///     is DI-injected in every realistic minimal API signature.
    /// </summary>
    private static bool IsInfrastructureParameter(Microsoft.CodeAnalysis.IParameterSymbol parameter)
    {
        var typeName = parameter.Type.Name;
        if (typeName.Contains("CancellationToken", StringComparison.Ordinal) ||
            typeName.Contains("HttpContext", StringComparison.Ordinal) ||
            typeName.Contains("ClaimsPrincipal", StringComparison.Ordinal) ||
            typeName.Contains("IServiceProvider", StringComparison.Ordinal) ||
            typeName.StartsWith("ILogger", StringComparison.Ordinal))
        {
            return true;
        }

        if (parameter.GetAttributes().Any(attribute => attribute.AttributeClass?.Name is "FromServices" or "FromServicesAttribute" or "FromKeyedServices" or "FromKeyedServicesAttribute"))
        {
            return true;
        }

        // Unresolved references produce error-typed symbols whose Name is the written text, so
        // "ICatalogService service" still reads as an interface here. I + Upper-second-letter is
        // the conventional interface spelling; classes like "Item" fail the second-letter check.
        if (parameter.Type.TypeKind == Microsoft.CodeAnalysis.TypeKind.Interface ||
            (parameter.Type is Microsoft.CodeAnalysis.IErrorTypeSymbol && parameter.Type.Name.Length > 2 && parameter.Type.Name[0] == 'I' && char.IsUpper(parameter.Type.Name[1])))
        {
            return true;
        }

        // Common cancellation-token spellings whose type did not resolve.
        if (parameter.Name is "ct" or "cancellationToken" or "cancellationTokenSource" && parameter.Type is Microsoft.CodeAnalysis.IErrorTypeSymbol)
        {
            return true;
        }

        // A concrete class declared in this source tree with a service suffix is a DI-injected
        // application service far more often than a request DTO; skipping it errs toward silence
        // rather than a phantom untrusted source.
        if (IsSourceDeclaredService(parameter.Type))
        {
            return true;
        }

        return false;
    }

    private static bool IsSourceDeclaredService(Microsoft.CodeAnalysis.ITypeSymbol type)
    {
        return type.TypeKind == Microsoft.CodeAnalysis.TypeKind.Class &&
               type.Locations.Any(location => location.IsInSource) &&
               (type.Name.EndsWith("Services", StringComparison.Ordinal) ||
                type.Name.EndsWith("Service", StringComparison.Ordinal) ||
                type.Name.EndsWith("Repository", StringComparison.Ordinal) ||
                type.Name.EndsWith("Context", StringComparison.Ordinal) && type.Name.Contains("Db", StringComparison.Ordinal) ||
                type.Name.EndsWith("Client", StringComparison.Ordinal) ||
                type.Name.EndsWith("Store", StringComparison.Ordinal) ||
                type.Name.EndsWith("Provider", StringComparison.Ordinal) ||
                type.Name.EndsWith("Mapper", StringComparison.Ordinal) ||
                type.Name.EndsWith("Factory", StringComparison.Ordinal));
    }

    private static List<string> MethodVerbs(InvocationExpressionSyntax invocation)
    {
        var verbs = new List<string>();
        var arrayArgument = invocation.ArgumentList.Arguments.ElementAtOrDefault(1)?.Expression;
        switch (arrayArgument)
        {
            case ImplicitArrayCreationExpressionSyntax array:
                foreach (var expression in array.Initializer?.Expressions.OfType<LiteralExpressionSyntax>() ?? [])
                {
                    if (expression.Token.Value is string value)
                    {
                        verbs.Add(value.ToUpperInvariant());
                    }
                }

                break;
            case LiteralExpressionSyntax literal when literal.Token.Value is string single:
                verbs.Add(single.ToUpperInvariant());
                break;
        }

        return verbs;
    }

    private static string? FirstStringArgument(InvocationExpressionSyntax invocation) => ProviderHelpers.StringArguments(invocation).FirstOrDefault();

    private static void AddEndpoint(FrameworkContext ctx, FrameworkResults results, InvocationExpressionSyntax invocation, string httpMethod, Dictionary<string, string> groupPrefixes, List<string> rawUrls, HashSet<string> emittedEndpointIds, string? explicitTemplate = null, Dictionary<string, ApiEndpoint>? groupMetadata = null, bool requireAuthorization = false, bool seedHandler = true)
    {
        var template = explicitTemplate ?? FirstStringArgument(invocation);
        if (template is null)
        {
            // Non-literal route (const/interpolation we cannot resolve): keep the invocation text
            // verbatim with low confidence rather than emitting a garbled path.
            template = invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression.ToString();
        }

        var prefix = GroupPrefixOf(invocation, groupPrefixes);

        var combined = RouteTemplateResolver.CombinePrefix(prefix, template);
        var resolved = RouteTemplateResolver.Resolve(combined);
        var lineSpan = invocation.GetLocation().GetLineSpan().StartLinePosition;
        var fileName = Path.GetFileName(invocation.SyntaxTree.FilePath);
        var serviceId = FrameworkIds.Service("minimal-api", Path.GetDirectoryName(CodeLocation.From(ctx.BasePath, invocation.SyntaxTree.FilePath).Path), fileName);
        var operationId = FrameworkIds.Operation(serviceId, httpMethod, resolved.Path, template ?? httpMethod);

        // Repeated registrations of the same (file, verb, path), e.g. AddHealthChecks wired
        // twice, or versioned groups collapsing to one path, must not duplicate ids.
        if (!emittedEndpointIds.Add(operationId))
        {
            return;
        }

        var endpoint = new ApiEndpoint
        {
            Path = resolved.Path,
            FilePath = CodeLocation.From(ctx.BasePath, invocation.SyntaxTree.FilePath).Path,
            FileName = fileName,
            HttpMethod = httpMethod,
            Route = combined,
            EndpointKind = "MinimalApi",
            RoutingKind = "MinimalApi",
            Framework = "minimal-api",
            ServiceId = serviceId,
            OperationId = operationId,
            Confidence = resolved.Path is null || resolved.HasMalformedSegment ? "low" : "medium",
            LineNumber = lineSpan.Line + 1,
            ColumnNumber = lineSpan.Character + 1,
            RawUrls = rawUrls,
            RouteParameters = resolved.Parameters,
            Evidence = new AnalysisEvidence
            {
                Kind = AnalysisEvidenceKind.FrameworkModel,
                Source = "minimal-api",
                Description = "Minimal API endpoint registration.",
                Confidence = "medium",
                FileName = fileName,
                LineNumber = lineSpan.Line + 1,
                ColumnNumber = lineSpan.Character + 1
            }
        };
        ProviderHelpers.ApplyMinimalApiMetadata(endpoint, invocation);
        if (requireAuthorization)
        {
            endpoint.AuthorizationRequired = true;
        }

        // The handler's own attributes (`[Authorize] (int id) => …`, or a method group's
        // `[AllowAnonymous]`) and the group's conventions are endpoint metadata too.
        var handler = seedHandler ? HandlerOf(ctx, invocation) : null;
        if (handler?.Attributes is { Count: > 0 } handlerAttributes)
        {
            var fromHandler = new ApiEndpoint();
            ProviderHelpers.ApplyAuthorizationMetadata(fromHandler, handlerAttributes);
            MergeConventions(endpoint, fromHandler);
        }

        if (InheritedGroupMetadata(invocation, groupMetadata) is { } inherited)
        {
            MergeConventions(endpoint, inherited);
        }

        results.ApiEndpoints.Add(endpoint);

        if (seedHandler)
        {
            SeedHandlerParameters(ctx, results, invocation, resolved.Path ?? combined);
        }

        var service = results.Services.FirstOrDefault(existing => existing.Id == serviceId);
        if (service is null)
        {
            service = new ServiceComponent
            {
                Id = serviceId,
                Name = fileName,
                ServiceKind = ServiceKinds.Http,
                Direction = ServiceDirections.Inbound,
                Framework = "minimal-api",
                Confidence = ConfidenceTiers.Syntactic,
                Location = CodeLocation.From(ctx.BasePath, invocation.SyntaxTree.FilePath)
            };
            results.Services.Add(service);
        }

        service.Operations.Add(new ServiceOperation
        {
            Id = operationId,
            Name = endpoint.Path ?? combined,
            HttpMethod = httpMethod,
            Path = resolved.Path,
            RouteTemplate = combined,
            RouteParameters = resolved.Parameters,
            Authenticated = endpoint.AuthorizationRequired,
            Confidence = endpoint.Confidence,
            Location = CodeLocation.From(ctx.BasePath, invocation.SyntaxTree.FilePath, lineSpan.Line + 1, lineSpan.Character + 1)
        });
        if (resolved.Path is not null && !service.Endpoints.Contains(resolved.Path, StringComparer.Ordinal))
        {
            service.Endpoints.Add(resolved.Path);
        }

        results.EntryPoints.Add(new EntryPoint
        {
            Id = $"ep:{operationId}",
            Kind = "HttpMinimalApi",
            // A method-group handler is a method of its own: name it so flows and call paths
            // land on this route instead of the file's first entry point.
            // A lambda handler is named the way the call graph and data flow name it.
            MethodId = handler?.Method is { } handlerMethod ? Depscan.Dosai.FormatMethodSignature(handlerMethod) : null,
            MethodName = handler?.Method is { } named ? Depscan.Dosai.IsNestedFunction(named) ? Depscan.Dosai.NestedFunctionName(named) : named.Name : null,
            ClassName = handler?.Method?.ContainingType?.Name,
            Namespace = handler?.Method?.ContainingType?.ContainingNamespace?.ToDisplayString(),
            FileName = fileName,
            Path = endpoint.FilePath,
            LineNumber = endpoint.LineNumber,
            ColumnNumber = endpoint.ColumnNumber,
            HttpMethod = httpMethod,
            Route = resolved.Path ?? combined,
            AuthorizationRequired = endpoint.AuthorizationRequired,
            AuthorizationPolicies = endpoint.AuthorizationPolicies,
            Roles = endpoint.Roles,
            AllowAnonymous = endpoint.AllowAnonymous,
            AuthenticationSchemes = endpoint.AuthenticationSchemes,
            RequiredClaims = endpoint.RequiredClaims,
            RequiredScopes = endpoint.RequiredScopes,
            CorsPolicies = endpoint.CorsPolicies,
            AntiForgeryRequired = endpoint.AntiForgeryRequired,
            RawUrls = endpoint.RawUrls
        });
    }
}
