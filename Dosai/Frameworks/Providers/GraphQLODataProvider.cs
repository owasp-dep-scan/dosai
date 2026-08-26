using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Depscan.Frameworks.Providers;

/// <summary>
///     GraphQL (HotChocolate / GraphQL.NET) and OData: attributed resolvers, ObjectGraphType
///     derivatives, registration chain analysis (introspection, execution depth), and OData
///     [EnableQuery] endpoints with EntitySet/RouteComponent registration.
/// </summary>
public sealed class GraphQLODataProvider : IFrameworkProvider
{
    private static readonly string[] GraphQlTypeAttributes = ["QueryType", "MutationType", "SubscriptionType", "ObjectType", "ExtendObjectType"];
    private static readonly string[] ODataRegistrationMethods = ["AddRouteComponents", "MapODataRoute", "EnableODataRouteDebug", "AddODataQueryFilter"];

    public string Id => "graphql";

    public string DisplayName => "GraphQL & OData";

    public bool AppliesTo(FrameworkContext ctx) => ctx.CSharp is not null;

    public void Analyze(FrameworkContext ctx, FrameworkResults results)
    {
        try
        {
            AnalyzeGraphQL(ctx, results);
            AnalyzeOData(ctx, results);
        }
        catch (Exception ex)
        {
            ctx.Diagnostics.Add(new FrameworkDiagnostic("graphql", $"GraphQL/OData analysis failed: {ex.Message}"));
        }
    }

    // ---- GraphQL ----
    private static void AnalyzeGraphQL(FrameworkContext ctx, FrameworkResults results)
    {
        // Determine mount path from MapGraphQL mount points or default
        var graphqlMounts = ctx.MountPoints
            .Where(mp => mp.Kind == "graphql")
            .Select(mp => mp.Path)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var graphqlPath = graphqlMounts.Count > 0 ? graphqlMounts[0] : "/graphql";
        var pathIsDefaulted = graphqlMounts.Count == 0;

        // Check registration chain for security settings
        var introspection = "enabled";
        string? maxExecutionDepth = null;

foreach (var tree in ctx.CSharpTrees)
        {
            if (!ctx.TextContainsAny(tree, "QueryType", "MutationType", "SubscriptionType", "ObjectGraphType", "EnableQuery", "MapGraphQL", "AddRouteComponents", "AddGraphQLServer", "graphql"))
            {
                continue;
            }

            var root = tree.GetCompilationUnitRoot();
            var fileText = ctx.TextFor(tree);

            // Textual analysis of registration chains for security properties
            if (fileText.Contains("DisableIntrospection", StringComparison.Ordinal))
            {
                introspection = "disabled";
            }

            // AddMaxExecutionDepthRule(n) or .WithMaxExecutionDepth(n)
            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var name = ProviderHelpers.InvocationName(invocation);
                if (name.Equals("AddMaxExecutionDepthRule", StringComparison.Ordinal) ||
                    name.Equals("WithMaxExecutionDepth", StringComparison.Ordinal))
                {
                    var depthArg = invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression;
                    if (depthArg is LiteralExpressionSyntax literal && literal.Token.Value is int depth)
                    {
                        maxExecutionDepth = depth.ToString();
                    }
                    else if (depthArg is LiteralExpressionSyntax stringLiteral && int.TryParse(stringLiteral.Token.Text, out var parsedDepth))
                    {
                        maxExecutionDepth = parsedDepth.ToString();
                    }
                }
            }
        }

        // Analyze GraphQL type declarations
foreach (var tree in ctx.CSharpTrees)
        {
            if (!ctx.TextContainsAny(tree, "QueryType", "MutationType", "SubscriptionType", "ObjectGraphType", "EnableQuery", "MapGraphQL", "AddRouteComponents", "AddGraphQLServer", "graphql"))
            {
                continue;
            }

            var model = ctx.CSharp!.GetSemanticModel(tree);
            var root = tree.GetCompilationUnitRoot();
            var rawUrls = ctx.RawUrlsFor(tree);

            foreach (var typeDeclaration in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                var symbol = model.GetDeclaredSymbol(typeDeclaration);
                var attributes = ProviderHelpers.AttributesOf(typeDeclaration.AttributeLists).ToList();
                var typeName = typeDeclaration.Identifier.Text;
                var namespaceName = typeDeclaration.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();

                // Check for GraphQL type attributes (HotChocolate style)
                var graphQlAttr = attributes.FirstOrDefault(a =>
                    GraphQlTypeAttributes.Any(ga => ProviderHelpers.IsNamed(a, ga)));

                // Check for ObjectGraphType base (GraphQL.NET style)
                var derivesFromObjectGraphType = symbol is not null && ProviderHelpers.DerivesFromAny(symbol, "ObjectGraphType");
                var syntacticObjectGraphType = false;
                if (!derivesFromObjectGraphType && symbol is null)
                {
                    var baseList = (typeDeclaration as ClassDeclarationSyntax)?.BaseList;
                    if (baseList is not null)
                    {
                        syntacticObjectGraphType = baseList.Types.Any(bt =>
                            bt.Type.ToString().Contains("ObjectGraphType", StringComparison.Ordinal));
                    }
                }

                if (graphQlAttr is null && !derivesFromObjectGraphType && !syntacticObjectGraphType)
                {
                    continue;
                }

                var confidence = (symbol is not null || derivesFromObjectGraphType) ? ConfidenceTiers.Semantic : ConfidenceTiers.Syntactic;
                var attrName = graphQlAttr is not null ? ProviderHelpers.NormalizeAttributeName(graphQlAttr) : "ObjectType";
                var isQuery = attrName.Equals("QueryType", StringComparison.OrdinalIgnoreCase) || typeName.EndsWith("Query", StringComparison.OrdinalIgnoreCase);
                var isMutation = attrName.Equals("MutationType", StringComparison.OrdinalIgnoreCase) || typeName.EndsWith("Mutation", StringComparison.OrdinalIgnoreCase);
                var isSubscription = attrName.Equals("SubscriptionType", StringComparison.OrdinalIgnoreCase) || typeName.EndsWith("Subscription", StringComparison.OrdinalIgnoreCase);

                var serviceId = FrameworkIds.Service("graphql", namespaceName, typeName);
                var lineSpan = typeDeclaration.GetLocation().GetLineSpan().StartLinePosition;

                var service = new ServiceComponent
                {
                    Id = serviceId,
                    Name = typeName,
                    Group = namespaceName,
                    ServiceKind = ServiceKinds.GraphQl,
                    Direction = ServiceDirections.Inbound,
                    Framework = "graphql",
                    FrameworkVersion = ctx.Detection["graphql"]?.Version,
                    Purl = ctx.Detection["graphql"]?.Purl,
                    Confidence = confidence,
                    Location = CodeLocation.From(ctx.BasePath, tree.FilePath, lineSpan.Line + 1, lineSpan.Character + 1),
                    Evidence = new AnalysisEvidence
                    {
                        Kind = AnalysisEvidenceKind.FrameworkModel,
                        Source = "graphql",
                        Description = graphQlAttr is not null
                            ? $"GraphQL type [{attrName}]."
                            : "Type derives from ObjectGraphType.",
                        Confidence = confidence,
                        FileName = Path.GetFileName(tree.FilePath),
                        LineNumber = lineSpan.Line + 1
                    }
                };

                service.Properties["introspection"] = introspection;
                if (maxExecutionDepth is not null)
                {
                    service.Properties["maxExecutionDepth"] = maxExecutionDepth;
                }

                if (!service.Endpoints.Contains(graphqlPath, StringComparer.Ordinal))
                {
                    service.Endpoints.Add(graphqlPath);
                }

                // Public methods become operations (resolvers)
                foreach (var method in typeDeclaration.Members.OfType<MethodDeclarationSyntax>())
                {
                    if (!method.Modifiers.Any(m => m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PublicKeyword)))
                    {
                        continue;
                    }

                    var methodAttributes = ProviderHelpers.AttributesOf(method.AttributeLists).ToList();
                    var methodName = method.Identifier.Text;

                    // [Name("alias")] attribute (HotChocolate) renames the resolver
                    var nameAttr = methodAttributes.FirstOrDefault(a => ProviderHelpers.IsNamed(a, "Name"));
                    var operationName = nameAttr is not null
                        ? ProviderHelpers.AttributeArgumentText(nameAttr, model) ?? methodName
                        : methodName;

                    var methodLineSpan = method.GetLocation().GetLineSpan().StartLinePosition;
                    var methodSymbol = model.GetDeclaredSymbol(method);
                    var methodId = methodSymbol is null ? null : Depscan.Dosai.FormatMethodSignature(methodSymbol);
                    var operationId = FrameworkIds.Operation(serviceId, "POST", graphqlPath, operationName);

                    var operation = new ServiceOperation
                    {
                        Id = operationId,
                        Name = operationName,
                        HttpMethod = "POST",
                        Path = graphqlPath,
                        MethodId = methodId,
                        RequestType = methodSymbol?.Parameters.FirstOrDefault()?.Type.ToDisplayString(),
                        ResponseType = methodSymbol?.ReturnType.ToDisplayString(),
                        Confidence = confidence,
                        Location = CodeLocation.From(ctx.BasePath, tree.FilePath, methodLineSpan.Line + 1, methodLineSpan.Character + 1)
                    };
                    service.Operations.Add(operation);

                    if (methodId is not null && !service.MethodIds.Contains(methodId, StringComparer.Ordinal))
                    {
                        service.MethodIds.Add(methodId);
                    }

                    // ApiEndpoint per resolver
                    var endpointConfidence = pathIsDefaulted ? ConfidenceTiers.Heuristic : ConfidenceTiers.Syntactic;
                    var endpoint = new ApiEndpoint
                    {
                        FilePath = CodeLocation.From(ctx.BasePath, tree.FilePath).Path,
                        FileName = Path.GetFileName(tree.FilePath),
                        Namespace = namespaceName,
                        ClassName = typeName,
                        MethodName = methodName,
                        HttpMethod = "POST",
                        EndpointKind = "GraphQL",
                        RoutingKind = "Mount",
                        Path = graphqlPath,
                        Framework = "graphql",
                        ServiceId = serviceId,
                        OperationId = operationId,
                        Confidence = endpointConfidence,
                        LineNumber = methodLineSpan.Line + 1,
                        ColumnNumber = methodLineSpan.Character + 1,
                        RawUrls = rawUrls,
                        Evidence = new AnalysisEvidence
                        {
                            Kind = AnalysisEvidenceKind.FrameworkModel,
                            Source = "graphql",
                            Description = isQuery ? "GraphQL query resolver." :
                                isMutation ? "GraphQL mutation resolver." :
                                isSubscription ? "GraphQL subscription resolver." :
                                "GraphQL resolver.",
                            Confidence = endpointConfidence,
                            FileName = Path.GetFileName(tree.FilePath),
                            LineNumber = methodLineSpan.Line + 1
                        }
                    };
                    results.ApiEndpoints.Add(endpoint);

                    // EntryPoint
                    results.EntryPoints.Add(new EntryPoint
                    {
                        Id = $"ep:{operationId}",
                        Kind = "GraphQL",
                        MethodId = methodId,
                        MethodName = methodName,
                        ClassName = typeName,
                        Namespace = namespaceName,
                        FileName = Path.GetFileName(tree.FilePath),
                        Path = endpoint.FilePath,
                        LineNumber = methodLineSpan.Line + 1,
                        ColumnNumber = methodLineSpan.Character + 1,
                        HttpMethod = "POST",
                        Route = graphqlPath,
                        RawUrls = rawUrls
                    });

                    service.EntryPointIds.Add($"ep:{operationId}");
                }

                results.Services.Add(service);
            }
        }
    }

    // ---- OData ----
    private static void AnalyzeOData(FrameworkContext ctx, FrameworkResults results)
    {
        // Determine OData route prefix
        var odataMounts = ctx.MountPoints
            .Where(mp => mp.Kind == "odata")
            .Select(mp => mp.Path)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var odataPrefix = odataMounts.Count > 0 ? odataMounts[0] : "/odata";

        // Also check for AddRouteComponents("prefix", ...) in source
foreach (var tree in ctx.CSharpTrees)
        {
            if (!ctx.TextContainsAny(tree, "QueryType", "MutationType", "SubscriptionType", "ObjectGraphType", "EnableQuery", "MapGraphQL", "AddRouteComponents", "AddGraphQLServer", "graphql"))
            {
                continue;
            }

            var root = tree.GetCompilationUnitRoot();
            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var name = ProviderHelpers.InvocationName(invocation);
                if (name.Equals("AddRouteComponents", StringComparison.Ordinal))
                {
                    var prefixArg = ProviderHelpers.StringArguments(invocation).FirstOrDefault();
                    if (prefixArg is not null && odataMounts.Count == 0)
                    {
                        odataPrefix = RouteTemplateResolver.NormalizePrefix(prefixArg);
                    }
                }
            }
        }

foreach (var tree in ctx.CSharpTrees)
        {
            if (!ctx.TextContainsAny(tree, "QueryType", "MutationType", "SubscriptionType", "ObjectGraphType", "EnableQuery", "MapGraphQL", "AddRouteComponents", "AddGraphQLServer", "graphql"))
            {
                continue;
            }

            var model = ctx.CSharp!.GetSemanticModel(tree);
            var root = tree.GetCompilationUnitRoot();
            var rawUrls = ctx.RawUrlsFor(tree);

            foreach (var typeDeclaration in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                var symbol = model.GetDeclaredSymbol(typeDeclaration);
                var typeName = typeDeclaration.Identifier.Text;
                var namespaceName = typeDeclaration.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();

                // Check for [EnableQuery] on methods
                foreach (var method in typeDeclaration.Members.OfType<MethodDeclarationSyntax>())
                {
                    var methodAttributes = ProviderHelpers.AttributesOf(method.AttributeLists).ToList();
                    var enableQueryAttr = methodAttributes.FirstOrDefault(a => ProviderHelpers.IsNamed(a, "EnableQuery"));
                    if (enableQueryAttr is null)
                    {
                        continue;
                    }

                    if (!method.Modifiers.Any(m => m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PublicKeyword)))
                    {
                        continue;
                    }

                    var methodName = method.Identifier.Text;
                    var methodLineSpan = method.GetLocation().GetLineSpan().StartLinePosition;
                    var methodSymbol = model.GetDeclaredSymbol(method);
                    var methodId = methodSymbol is null ? null : Depscan.Dosai.FormatMethodSignature(methodSymbol);

                    // Parse EnableQuery named arguments for MaxExpansionDepth, AllowedQueryOptions
                    var maxExpansionDepth = NamedArgText(enableQueryAttr, "MaxExpansionDepth");
                    var allowedQueryOptions = NamedArgText(enableQueryAttr, "AllowedQueryOptions");

                    var serviceId = FrameworkIds.Service("graphql", namespaceName, typeName);
                    var operationId = FrameworkIds.Operation(serviceId, "GET", odataPrefix, methodName);

                    // Create service if not yet created for this controller
                    var service = results.Services.FirstOrDefault(s => s.Id == serviceId);
                    if (service is null)
                    {
                        service = new ServiceComponent
                        {
                            Id = serviceId,
                            Name = typeName,
                            Group = namespaceName,
                            ServiceKind = ServiceKinds.OData,
                            Direction = ServiceDirections.Inbound,
                            Framework = "graphql",
                            Confidence = ConfidenceTiers.Syntactic,
                            Location = CodeLocation.From(ctx.BasePath, tree.FilePath),
                            Evidence = new AnalysisEvidence
                            {
                                Kind = AnalysisEvidenceKind.FrameworkModel,
                                Source = "graphql",
                                Description = "OData controller with [EnableQuery].",
                                Confidence = ConfidenceTiers.Syntactic,
                                FileName = Path.GetFileName(tree.FilePath)
                            }
                        };
                        if (!service.Endpoints.Contains(odataPrefix, StringComparer.Ordinal))
                        {
                            service.Endpoints.Add(odataPrefix);
                        }

                        results.Services.Add(service);
                    }

                    var operation = new ServiceOperation
                    {
                        Id = operationId,
                        Name = methodName,
                        HttpMethod = "GET",
                        Path = odataPrefix,
                        MethodId = methodId,
                        RequestType = methodSymbol?.ReturnType?.ToDisplayString(),
                        Confidence = ConfidenceTiers.Syntactic,
                        Location = CodeLocation.From(ctx.BasePath, tree.FilePath, methodLineSpan.Line + 1, methodLineSpan.Character + 1)
                    };

                    if (maxExpansionDepth is not null)
                    {
                        operation.Properties["maxExpansionDepth"] = maxExpansionDepth;
                    }

                    if (allowedQueryOptions is not null)
                    {
                        operation.Properties["allowedQueryOptions"] = allowedQueryOptions;
                    }

                    service.Operations.Add(operation);

                    if (methodId is not null && !service.MethodIds.Contains(methodId, StringComparer.Ordinal))
                    {
                        service.MethodIds.Add(methodId);
                    }

                    // ApiEndpoint
                    var endpoint = new ApiEndpoint
                    {
                        FilePath = CodeLocation.From(ctx.BasePath, tree.FilePath).Path,
                        FileName = Path.GetFileName(tree.FilePath),
                        Namespace = namespaceName,
                        ClassName = typeName,
                        MethodName = methodName,
                        HttpMethod = "GET",
                        EndpointKind = "OData",
                        RoutingKind = "Mount",
                        Path = odataPrefix,
                        Framework = "graphql",
                        ServiceId = serviceId,
                        OperationId = operationId,
                        Confidence = odataMounts.Count > 0 ? ConfidenceTiers.Syntactic : ConfidenceTiers.Heuristic,
                        LineNumber = methodLineSpan.Line + 1,
                        ColumnNumber = methodLineSpan.Character + 1,
                        RawUrls = rawUrls,
                        Evidence = new AnalysisEvidence
                        {
                            Kind = AnalysisEvidenceKind.FrameworkModel,
                            Source = "graphql",
                            Description = "OData [EnableQuery] endpoint.",
                            Confidence = ConfidenceTiers.Syntactic,
                            FileName = Path.GetFileName(tree.FilePath),
                            LineNumber = methodLineSpan.Line + 1
                        }
                    };
                    results.ApiEndpoints.Add(endpoint);

                    // EntryPoint
                    results.EntryPoints.Add(new EntryPoint
                    {
                        Id = $"ep:{operationId}",
                        Kind = "OData",
                        MethodId = methodId,
                        MethodName = methodName,
                        ClassName = typeName,
                        Namespace = namespaceName,
                        FileName = Path.GetFileName(tree.FilePath),
                        Path = endpoint.FilePath,
                        LineNumber = methodLineSpan.Line + 1,
                        ColumnNumber = methodLineSpan.Character + 1,
                        HttpMethod = "GET",
                        Route = odataPrefix,
                        RawUrls = rawUrls
                    });

                    service.EntryPointIds.Add($"ep:{operationId}");
                }
            }
        }
    }

    private static string? NamedArgText(AttributeSyntax attribute, string name)
    {
        var argument = attribute.ArgumentList?.Arguments
            .FirstOrDefault(a => string.Equals(a.NameEquals?.Name.ToString(), name, StringComparison.OrdinalIgnoreCase));
        if (argument is null)
        {
            return null;
        }

        return argument.Expression switch
        {
            LiteralExpressionSyntax literal => literal.Token.ValueText,
            MemberAccessExpressionSyntax member => member.Name.Identifier.Text,
            _ => null
        };
    }
}
