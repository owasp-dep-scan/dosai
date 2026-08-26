using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Depscan.Frameworks.Providers;

/// <summary>
///     gRPC services and clients. Server implementations are found through MapGrpcService&lt;T&gt;
///     registrations and classes deriving a protoc-generated *Base type; operation paths are built
///     as /package.Service/Method by joining the implementation class against the .proto contracts
///     parsed by the protobuf provider. Clients (AddGrpcClient, GrpcChannel.ForAddress) become
///     outbound services with real addresses.
/// </summary>
public sealed class GrpcProvider : IFrameworkProvider
{
    public string Id => "grpc";

    public string DisplayName => "gRPC (Grpc.AspNetCore / Grpc.Net.Client)";

    public bool AppliesTo(FrameworkContext ctx) => ctx.CSharp is not null;

    public void Analyze(FrameworkContext ctx, FrameworkResults results)
    {
        var registeredServiceNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mount in ctx.MountPoints.Where(mount => mount.Kind == "grpc-service"))
        {
            // The type argument was captured from syntax when the mount was recorded; reading the
            // file back from disk here would re-read Program.cs once per registration and could
            // diverge from the compiled tree on unsaved edits.
            if (!string.IsNullOrWhiteSpace(mount.TypeName))
            {
                registeredServiceNames.UnionWith(mount.TypeName.Split(',', ';').Select(part => part.Trim().Split('<', '>')[0].Trim()));
            }
        }

foreach (var tree in ctx.CSharpTrees)
        {
            if (!ctx.TextContainsAny(tree, "GrpcChannel", "MapGrpcService", "AddGrpcClient", "ServerCallContext"))
            {
                continue;
            }

            var model = ctx.CSharp.GetSemanticModel(tree);
            var root = tree.GetCompilationUnitRoot();
            var rawUrls = ctx.RawUrlsFor(tree);

            // Registration + reflection + transcoding + client configuration invocations.
            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var name = ProviderHelpers.InvocationName(invocation);
                switch (name)
                {
                    case "AddGrpcReflection":
                    case "MapGrpcReflectionService":
                        ctx.GrpcServerProperties.TryAdd("reflection", "exposed");
                        break;
                    case "AddJsonTranscoding":
                        ctx.GrpcServerProperties.TryAdd("jsonTranscoding", "enabled");
                        break;
                    case "UseGrpcWeb":
                    case "EnableGrpcWeb":
                        ctx.GrpcServerProperties.TryAdd("grpcWeb", "enabled");
                        break;
                    case "AddGrpcClient":
                        AddClientFromRegistration(ctx, results, invocation, tree.FilePath);
                        break;
                }
            }

            foreach (var typeDeclaration in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                AnalyzeServiceClass(ctx, results, typeDeclaration, model, tree.FilePath, rawUrls, registeredServiceNames);
            }

            // Client constructions: GrpcChannel.ForAddress("https://...") or new X.XClient(channel).
            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(invocation => ProviderHelpers.InvocationName(invocation) == "ForAddress"))
            {
                var address = ProviderHelpers.StringArguments(invocation).FirstOrDefault();
                if (string.IsNullOrWhiteSpace(address))
                {
                    continue;
                }

                var lineSpan = invocation.GetLocation().GetLineSpan().StartLinePosition;
                results.Services.Add(new ServiceComponent
                {
                    Id = FrameworkIds.Service("grpc", Path.GetDirectoryName(CodeLocation.From(ctx.BasePath, tree.FilePath).Path), $"channel-{UriHostOf(address)}"),
                    Name = $"gRPC channel ({UriHostOf(address)})",
                    ServiceKind = ServiceKinds.Grpc,
                    Direction = ServiceDirections.Outbound,
                    Framework = "grpc",
                    Endpoints = [address],
                    Confidence = ConfidenceTiers.Syntactic,
                    Location = CodeLocation.From(ctx.BasePath, tree.FilePath, lineSpan.Line + 1),
                    Evidence = new AnalysisEvidence { Kind = AnalysisEvidenceKind.FrameworkModel, Source = "grpc", Description = $"GrpcChannel.ForAddress({address}).", Confidence = ConfidenceTiers.Syntactic }
                });
            }
        }

        PromoteServerProperties(ctx, results);
    }

    private static void AnalyzeServiceClass(FrameworkContext ctx, FrameworkResults results, ClassDeclarationSyntax typeDeclaration, SemanticModel model, string filePath, List<string> rawUrls, HashSet<string> registeredServiceNames)
    {
        var symbol = model.GetDeclaredSymbol(typeDeclaration);
        var typeName = typeDeclaration.Identifier.Text;
        var namespaceName = typeDeclaration.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();

        // A gRPC service implementation derives the protoc-generated {Service}Base. The base symbol
        // usually does not resolve (generated code is not compiled), so fall back to the base-list
        // name syntactically.
        string? protoServiceName = null;
        string confidence;
        var syntacticBaseOnly = false;
        if (symbol?.BaseType is { Name: { } baseTypeName } && baseTypeName.EndsWith("Base", StringComparison.Ordinal) && baseTypeName != "ControllerBase" && baseTypeName.Length > 4)
        {
            protoServiceName = baseTypeName[..^"Base".Length];
            confidence = symbol.BaseType.DeclaredAccessibility == Accessibility.NotApplicable && symbol.BaseType.Locations.Any(location => location.IsInSource)
                ? ConfidenceTiers.Semantic
                : ConfidenceTiers.Syntactic;
        }
        else
        {
            var syntacticBase = typeDeclaration.BaseList?.Types
                .Select(baseType => baseType.Type.ToString().Split('.').Last())
                .FirstOrDefault(baseName => baseName.EndsWith("Base", StringComparison.Ordinal) && baseName != "ControllerBase" && baseName.Length > 4);
            var fullNameEarly = namespaceName is null ? typeName : $"{namespaceName}.{typeName}";
            if (syntacticBase is null && !registeredServiceNames.Contains(typeName) && !registeredServiceNames.Contains(fullNameEarly))
            {
                return;
            }

            syntacticBaseOnly = syntacticBase is null;
            protoServiceName = syntacticBase?[^"Base".Length..] ?? typeName.Replace("Service", string.Empty);
            confidence = ConfidenceTiers.Syntactic;
        }

        if (protoServiceName is null)
        {
            return;
        }

        // A "*Base" base type alone is not proof of gRPC: user base classes (EntityBase,
        // TestCaseBase, RepositoryBase, ...) are common and their references usually do not
        // resolve. Require corroboration: a MapGrpcService registration for this type, a .proto
        // service whose name matches, or a ServerCallContext parameter on one of its methods
        // (protoc-generated overrides always have one).
        var fullName = namespaceName is null ? typeName : $"{namespaceName}.{typeName}";
        var hasServerCallContext = typeDeclaration.Members.OfType<MethodDeclarationSyntax>().Any(method => method.ParameterList.Parameters.Any(parameter => parameter.Type?.ToString().Contains("ServerCallContext", StringComparison.Ordinal) == true));
        var registeredUnqualified = registeredServiceNames.Contains(typeName);
        if (syntacticBaseOnly && !hasServerCallContext && registeredUnqualified && !registeredServiceNames.Contains(fullName))
        {
            // A bare MapGrpcService<Name> (no namespace) matches every class with that simple
            // name — e.g. a typed client named like the registered server. Without a *Base base
            // or a ServerCallContext parameter, this class is not the registered service.
            return;
        }

        // A "*Base" base type alone is not proof of gRPC: user base classes (EntityBase,
        // TestCaseBase, RepositoryBase, ...) are common and their references usually do not
        // resolve. Require corroboration: a MapGrpcService registration for this type, a .proto
        // service whose name matches, or a ServerCallContext parameter on one of its methods
        // (protoc-generated overrides always have one).
        var registered = registeredServiceNames.Contains(typeName) || registeredServiceNames.Contains(fullName);
        var corroborated = registered
                           || ctx.ProtoServices.Any(service => service.Name.Equals(protoServiceName, StringComparison.Ordinal) || typeName.StartsWith(service.Name, StringComparison.Ordinal))
                           || hasServerCallContext;
        if (!corroborated)
        {
            return;
        }

        ctx.HandledTypeIds.Add($"{filePath}:{typeName}");
        var contract = ctx.ProtoServices.FirstOrDefault(service => service.Name.Equals(protoServiceName, StringComparison.Ordinal))
                       ?? ctx.ProtoServices.FirstOrDefault(service => typeName.StartsWith(service.Name, StringComparison.Ordinal));
        var mountPath = ctx.MountPoints.FirstOrDefault(mount => mount.Kind == "grpc-service")?.Path ?? "/";
        // The group includes the source directory: example suites routinely reuse the same
        // namespace and class name in several projects, and identical ids would collide.
        var directoryGroup = Path.GetDirectoryName(CodeLocation.From(ctx.BasePath, filePath).Path)?.Replace(Path.DirectorySeparatorChar, '.');
        var serviceId = FrameworkIds.Service("grpc", string.IsNullOrWhiteSpace(directoryGroup) ? namespaceName : $"{namespaceName}.{directoryGroup}", typeName);
        var service = new ServiceComponent
        {
            Id = serviceId,
            Name = typeName,
            Group = namespaceName,
            ServiceKind = ServiceKinds.Grpc,
            Direction = ServiceDirections.Inbound,
            Framework = "grpc",
            FrameworkVersion = ctx.Detection["grpc"]?.Version,
            Purl = ctx.Detection["grpc"]?.Purl,
            Confidence = confidence,
            Location = CodeLocation.From(ctx.BasePath, filePath),
            Evidence = new AnalysisEvidence
            {
                Kind = AnalysisEvidenceKind.FrameworkModel,
                Source = "grpc",
                Description = contract is null
                    ? $"Class derives generated base {protoServiceName}Base (proto contract not found)."
                    : $"Class derives generated base for proto service {(contract.Package is null ? contract.Name : contract.Package + "." + contract.Name)}.",
                Confidence = confidence
            }
        };
        results.Services.Add(service);

        var duplicateMethodNames = new HashSet<string>(StringComparer.Ordinal);
        var methodOccurrence = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var group in typeDeclaration.Members.OfType<MethodDeclarationSyntax>().GroupBy(method => method.Identifier.Text))
        {
            if (group.Count() > 1)
            {
                duplicateMethodNames.Add(group.Key);
            }
        }

        foreach (var method in typeDeclaration.Members.OfType<MethodDeclarationSyntax>().Where(method => (method.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.PublicKeyword)) || method.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.OverrideKeyword))) && !IsObjectMethod(method.Identifier.Text)))
        {
            var methodName = method.Identifier.Text;
            var rpc = contract?.Methods.FirstOrDefault(candidate => candidate.Name.Equals(methodName, StringComparison.Ordinal) || methodName.EndsWith(candidate.Name, StringComparison.Ordinal) || methodName.Equals($"{candidate.Name}Async", StringComparison.Ordinal));
            var rpcPath = rpc is not null && contract is not null
                ? $"/{(contract.Package is null ? contract.Name : $"{contract.Package}.{contract.Name}")}/{rpc.Name}"
                : $"/{(namespaceName is null ? typeName : namespaceName + "." + typeName)}/{methodName}";
            var lineSpan = method.GetLocation().GetLineSpan().StartLinePosition;
            var methodSymbol = model.GetDeclaredSymbol(method);
            var methodId = methodSymbol is null ? null : Depscan.Dosai.FormatMethodSignature(methodSymbol);
            // Overloads sharing a method name would collide on one operation id; a declaration-
            // order suffix keeps them distinct and stays stable across runs.
            string operationName;
            if (duplicateMethodNames.Contains(methodName))
            {
                methodOccurrence[methodName] = methodOccurrence.TryGetValue(methodName, out var seen) ? seen + 1 : 1;
                operationName = methodOccurrence[methodName] == 1 ? methodName : $"{methodName}~{methodOccurrence[methodName]}";
            }
            else
            {
                operationName = methodName;
            }

            var operationId = FrameworkIds.Operation(serviceId, null, rpcPath, operationName);
            var operationConfidence = rpc is null ? ConfidenceTiers.Heuristic : confidence;

            var operation = new ServiceOperation
            {
                Id = operationId,
                Name = methodName,
                Path = rpcPath,
                StreamingMode = rpc is null ? null : ProtobufProvider.StreamingModeOf(rpc),
                RequestType = rpc?.InputType ?? methodSymbol?.Parameters.FirstOrDefault()?.Type.ToDisplayString(),
                ResponseType = rpc?.OutputType,
                MethodId = methodId,
                Confidence = operationConfidence,
                Location = CodeLocation.From(ctx.BasePath, filePath, lineSpan.Line + 1)
            };
            if (rpc?.HttpVerb is { } verb && rpc.HttpPath is { } httpPath)
            {
                // JSON transcoding exposes this rpc over plain HTTP as well.
                operation.Properties["httpVerb"] = verb;
                operation.Properties["httpPath"] = httpPath;
            }

            service.Operations.Add(operation);

            var endpoint = new ApiEndpoint
            {
                Path = rpcPath,
                FilePath = CodeLocation.From(ctx.BasePath, filePath).Path,
                FileName = Path.GetFileName(filePath),
                Namespace = namespaceName,
                ClassName = typeName,
                MethodName = methodName,
                Route = rpcPath,
                EndpointKind = "Grpc",
                RoutingKind = "Mount",
                Framework = "grpc",
                ServiceId = serviceId,
                OperationId = operationId,
                Confidence = operationConfidence,
                LineNumber = lineSpan.Line + 1,
                ColumnNumber = lineSpan.Character + 1,
                RawUrls = rawUrls,
                Evidence = new AnalysisEvidence
                {
                    Kind = AnalysisEvidenceKind.FrameworkModel,
                    Source = "grpc",
                    Description = $"gRPC operation mounted at {mountPath} (service path {rpcPath}).",
                    Confidence = operationConfidence,
                    FileName = Path.GetFileName(filePath),
                    LineNumber = lineSpan.Line + 1
                }
            };
            var authorizeAttributes = ProviderHelpers.AttributesOf(typeDeclaration.AttributeLists).Concat(ProviderHelpers.AttributesOf(method.AttributeLists)).ToList();
            ProviderHelpers.ApplyAuthorizationMetadata(endpoint, authorizeAttributes);
            results.ApiEndpoints.Add(endpoint);
            service.Authenticated = endpoint.AllowAnonymous ? false : endpoint.AuthorizationRequired;

            results.EntryPoints.Add(new EntryPoint
            {
                Id = $"ep:{operationId}",
                Kind = "Grpc",
                MethodId = methodId,
                MethodName = methodName,
                ClassName = typeName,
                Namespace = namespaceName,
                FileName = endpoint.FileName,
                Path = endpoint.FilePath,
                LineNumber = endpoint.LineNumber,
                ColumnNumber = endpoint.ColumnNumber,
                Route = rpcPath,
                AuthorizationRequired = endpoint.AuthorizationRequired,
                AllowAnonymous = endpoint.AllowAnonymous
            });

            // rpc request messages are attacker-controlled exactly like HTTP bodies.
            foreach (var parameter in method.ParameterList.Parameters)
            {
                var parameterName = parameter.Identifier.Text;
                if (parameterName.Equals("context", StringComparison.OrdinalIgnoreCase) || parameter.Type?.ToString().Contains("ServerCallContext") == true)
                {
                    continue;
                }

                results.TaintSeeds.Add(new FrameworkTaintSeed
                {
                    MethodName = methodName,
                    ParameterName = parameterName,
                    ClassName = typeName,
                    Namespace = namespaceName,
                    FileName = Path.GetFileName(filePath),
                    MethodSignature = methodId,
                    LineNumber = lineSpan.Line + 1,
                    BindingSource = "rpc-message",
                    TaintKind = "rpc",
                    FrameworkId = "grpc",
                    EndpointPath = rpcPath,
                    Confidence = methodSymbol is null ? ConfidenceTiers.Syntactic : ConfidenceTiers.Semantic
                });
            }
        }
    }

    /// <summary>Dispose/Equals/GetHashCode/ToString are not rpc operations even when public.</summary>
    private static bool IsObjectMethod(string methodName) => methodName is "Dispose" or "DisposeAsync" or "Equals" or "GetHashCode" or "ToString" or "GetType";

    private static void AddClientFromRegistration(FrameworkContext ctx, FrameworkResults results, InvocationExpressionSyntax invocation, string filePath)
    {
        var clientType = (invocation.Expression as MemberAccessExpressionSyntax)?.Name as GenericNameSyntax;
        var clientName = clientType?.TypeArgumentList.Arguments.FirstOrDefault()?.ToString();
        if (string.IsNullOrWhiteSpace(clientName))
        {
            return;
        }

        var address = invocation.DescendantNodes().OfType<LiteralExpressionSyntax>()
            .Where(literal => literal.Token.Value is string value && value.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            .Select(literal => (string)literal.Token.Value!)
            .FirstOrDefault();
        var lineSpan = invocation.GetLocation().GetLineSpan().StartLinePosition;
        var service = new ServiceComponent
        {
            Id = FrameworkIds.Service("grpc", null, clientName),
            Name = clientName,
            ServiceKind = ServiceKinds.Grpc,
            Direction = ServiceDirections.Outbound,
            Framework = "grpc",
            Confidence = ConfidenceTiers.Syntactic,
            Location = CodeLocation.From(ctx.BasePath, filePath, lineSpan.Line + 1),
            Evidence = new AnalysisEvidence { Kind = AnalysisEvidenceKind.FrameworkModel, Source = "grpc", Description = $"AddGrpcClient<{clientName}>() registration.", Confidence = ConfidenceTiers.Syntactic }
        };
        if (address is not null)
        {
            service.Endpoints.Add(address);
        }
        else
        {
            service.Properties["address"] = "unknown";
        }

        results.Services.Add(service);
    }

    private static void PromoteServerProperties(FrameworkContext ctx, FrameworkResults results)
    {
        if (ctx.GrpcServerProperties.Count == 0)
        {
            return;
        }

        foreach (var service in results.Services.Where(service => service.Framework == "grpc" && service.Direction == ServiceDirections.Inbound))
        {
            foreach (var (key, value) in ctx.GrpcServerProperties)
            {
                service.Properties[key] = value;
                if (key == "reflection")
                {
                    service.Tags.Add("finding:grpc-reflection-exposed");
                }
            }
        }
    }

    private static string UriHostOf(string address)
    {
        try
        {
            return new Uri(address).Host;
        }
        catch (UriFormatException)
        {
            return address;
        }
    }
}
