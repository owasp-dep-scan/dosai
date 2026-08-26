using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Depscan.Frameworks.Providers;

/// <summary>
///     ASP.NET Core Minimal APIs: Map* endpoint registration invocations with MapGroup prefix
///     composition (fluent and via group variables), MapMethods verb arrays, and a node-by-node
///     fluent-chain walk for authorization/metadata. Map* calls that mount other frameworks
///     (MapHub, MapGrpcService, MapGraphQL, MapMcp, ...) are recorded as mount points for the
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
        ["MapHangfireDashboard"] = "hangfire-dashboard"
    };

    public string Id => "minimal-api";

    public string DisplayName => "ASP.NET Core Minimal APIs";

    public bool AppliesTo(FrameworkContext ctx) => ctx.CSharp is not null;

    public void Analyze(FrameworkContext ctx, FrameworkResults results)
    {
        // Prefixes are scoped per file: two Program.cs files both declaring `var g = ...` in the
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

        // Pass 1: collect MapGroup prefixes keyed by the variable they are assigned to, so
        // `var api = app.MapGroup("/api"); api.MapGet(...)` composes transitively. The initializer
        // is often a fluent chain (var v1 = app.MapGroup("/x").HasApiVersion(1,0)), so the prefix
        // is recovered by walking the chain rather than requiring the invocation itself to be
        // MapGroup.
        foreach (var tree in ctx.CSharpTrees)
        {
            var prefixes = PrefixesFor(tree.FilePath);
            var root = tree.GetCompilationUnitRoot();
            foreach (var declaration in root.DescendantNodes().OfType<VariableDeclaratorSyntax>())
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
            }
        }

        // Pass 2: Map* endpoint registrations.
        foreach (var tree in ctx.CSharpTrees)
        {
            var root = tree.GetCompilationUnitRoot();
            var rawUrls = ctx.RawUrlsFor(tree);
            var groupPrefixes = PrefixesFor(tree.FilePath);
            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var name = ProviderHelpers.InvocationName(invocation);
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
                        AddEndpoint(ctx, results, invocation, declaredVerb, groupPrefixes, rawUrls, emittedEndpointIds);
                    }

                    continue;
                }

                if (name == "MapFallback")
                {
                    var template = FirstStringArgument(invocation) ?? "/{fallback:nonfile}";
                    AddEndpoint(ctx, results, invocation, "ANY", groupPrefixes, rawUrls, emittedEndpointIds, template);
                    continue;
                }

                if (name == "MapHealthChecks" && !MountKinds.ContainsKey(name))
                {
                    var path = FirstStringArgument(invocation) ?? "/health";
                    AddEndpoint(ctx, results, invocation, "GET", groupPrefixes, rawUrls, emittedEndpointIds, path);
                    continue;
                }

                AddEndpoint(ctx, results, invocation, httpMethod, groupPrefixes, rawUrls, emittedEndpointIds);
            }
        }
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
                    // A non-MapGroup fluent link (HasApiVersion, RequireAuthorization, ...): keep
                    // walking outward — the MapGroup may sit further up the chain.
                    receiver = (intermediate.Expression as MemberAccessExpressionSyntax)?.Expression;
                    break;
                default:
                    return prefix;
            }
        }

        return prefix;
    }

    /// <summary>
    ///     Seeds the parameters of a Map* handler method group (`MapGet("/x", GetAllItems)`):
    ///     minimal-API binding pulls route/query/header/body values, so every handler parameter
    ///     (except cancellation tokens) is attacker-influenced, exactly like a controller action.
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

    private static void AddEndpoint(FrameworkContext ctx, FrameworkResults results, InvocationExpressionSyntax invocation, string httpMethod, Dictionary<string, string> groupPrefixes, List<string> rawUrls, HashSet<string> emittedEndpointIds, string? explicitTemplate = null)
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

        // Repeated registrations of the same (file, verb, path) — e.g. AddHealthChecks wired
        // twice, or versioned groups collapsing to one path — must not duplicate ids.
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
        results.ApiEndpoints.Add(endpoint);

        SeedHandlerParameters(ctx, results, invocation, resolved.Path ?? combined);

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
