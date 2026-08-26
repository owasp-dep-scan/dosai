using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Depscan.Frameworks.Providers;

/// <summary>
///     Model Context Protocol servers and clients. Server side: [McpServerToolType]/[McpServerTool]
///     (including tools attributed on ordinary MVC controllers), prompts, resources, transport
///     registration (stdio vs http, stateless mode), and per-tool JSON Schemas derived from the
///     method signature plus [Description] attributes — exactly what the SDK sends to the model.
///     Client side: McpClientFactory transports become outbound services; StdioClientTransport's
///     command/arguments record which external MCP server binary this app launches.
/// </summary>
public sealed class McpProvider : IFrameworkProvider
{
    public string Id => "mcp";

    public string DisplayName => "Model Context Protocol";

    public bool AppliesTo(FrameworkContext ctx) => ctx.CSharp is not null;

    public void Analyze(FrameworkContext ctx, FrameworkResults results)
    {
        var mountPath = ctx.MountPoints.FirstOrDefault(mount => mount.Kind == "mcp")?.Path;
        var registrationText = string.Empty;
        var toolServiceEmitted = false;

        foreach (var tree in ctx.CSharpTrees)
        {
            var text = ctx.TextFor(tree);
            var model = ctx.CSharp!.GetSemanticModel(tree);
            var root = tree.GetCompilationUnitRoot();
            registrationText = text.Contains("AddMcpServer", StringComparison.Ordinal) ? text : registrationText;

            var hasMcpServer = text.Contains("McpServerTool", StringComparison.Ordinal) || text.Contains("McpServerPrompt", StringComparison.Ordinal) || text.Contains("McpServerResource", StringComparison.Ordinal) || text.Contains("AddMcpServer", StringComparison.Ordinal) || mountPath is not null;
            var hasMcpClient = text.Contains("McpClientFactory", StringComparison.Ordinal) || text.Contains("ClientTransport", StringComparison.Ordinal);
            if (!hasMcpServer && !hasMcpClient)
            {
                continue;
            }

            if (hasMcpServer && !toolServiceEmitted)
            {
                EmitServerService(ctx, results, mountPath, text);
                toolServiceEmitted = true;
            }

            if (hasMcpClient)
            {
                EmitClientService(ctx, results, tree, root);
            }

            foreach (var typeDeclaration in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                var typeAttributes = ProviderHelpers.AttributesOf(typeDeclaration.AttributeLists).ToList();
                var isToolType = typeAttributes.Any(attribute => ProviderHelpers.IsNamed(attribute, "McpServerToolType"));
                var isPromptType = typeAttributes.Any(attribute => ProviderHelpers.IsNamed(attribute, "McpServerPromptType"));
                var isResourceType = typeAttributes.Any(attribute => ProviderHelpers.IsNamed(attribute, "McpServerResourceType"));
                if (!isToolType && !isPromptType && !isResourceType)
                {
                    continue;
                }

                ctx.HandledTypeIds.Add($"{tree.FilePath}:{typeDeclaration.Identifier.Text}");
                foreach (var method in typeDeclaration.Members.OfType<MethodDeclarationSyntax>().Where(method => method.Modifiers.Any(modifier => modifier.IsKind(SyntaxKind.PublicKeyword))))
                {
                    var methodAttributes = ProviderHelpers.AttributesOf(method.AttributeLists).ToList();
                    var toolAttribute = methodAttributes.FirstOrDefault(attribute => ProviderHelpers.IsNamed(attribute, "McpServerTool"));
                    if (toolAttribute is not null)
                    {
                        EmitTool(ctx, results, typeDeclaration, method, methodAttributes, toolAttribute, model, tree, isToolType);
                    }

                    var promptAttribute = methodAttributes.FirstOrDefault(attribute => ProviderHelpers.IsNamed(attribute, "McpServerPrompt"));
                    if (promptAttribute is not null)
                    {
                        EmitPrompt(ctx, results, typeDeclaration, method, promptAttribute, model, tree);
                    }

                    var resourceAttribute = methodAttributes.FirstOrDefault(attribute => ProviderHelpers.IsNamed(attribute, "McpServerResource"));
                    if (resourceAttribute is not null)
                    {
                        EmitResource(ctx, results, typeDeclaration, method, model, tree);
                    }
                }
            }
        }

        // Attributes applied directly to ordinary classes (the SDK supports existing services):
        // find [McpServerTool] methods on types without the *Type attribute.
        foreach (var tree in ctx.CSharpTrees)
        {
            if (!ctx.TextContainsAny(tree, "McpServerTool", "McpServerPrompt"))
            {
                continue;
            }

            var model = ctx.CSharp!.GetSemanticModel(tree);
            var root = tree.GetCompilationUnitRoot();
            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                var methodAttributes = ProviderHelpers.AttributesOf(method.AttributeLists).ToList();
                var toolAttribute = methodAttributes.FirstOrDefault(attribute => ProviderHelpers.IsNamed(attribute, "McpServerTool"));
                if (toolAttribute is null)
                {
                    continue;
                }

                var typeDeclaration = method.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
                if (typeDeclaration is null || ProviderHelpers.AttributesOf(typeDeclaration.AttributeLists).Any(attribute => ProviderHelpers.IsNamed(attribute, "McpServerToolType")))
                {
                    continue; // already handled above
                }

                EmitTool(ctx, results, typeDeclaration, method, methodAttributes, toolAttribute, model, tree, isToolType: false);
            }
        }

        ApplyServerSecurityProperties(ctx, results, registrationText);
    }

    private static void EmitServerService(FrameworkContext ctx, FrameworkResults results, string? mountPath, string registrationText)
    {
        var transport = registrationText.Contains("WithHttpTransport", StringComparison.Ordinal) ? "http" : registrationText.Contains("WithStdioServerTransport", StringComparison.Ordinal) ? "stdio" : "unknown";
        var service = new ServiceComponent
        {
            Id = FrameworkIds.Service("mcp", null, "mcp-server"),
            Name = "MCP server",
            ServiceKind = ServiceKinds.Mcp,
            Direction = ServiceDirections.Inbound,
            Framework = "mcp",
            FrameworkVersion = ctx.Detection["mcp"]?.Version,
            Purl = ctx.Detection["mcp"]?.Purl,
            Confidence = ConfidenceTiers.Syntactic,
            Location = CodeLocation.From(ctx.BasePath, mountPath is null ? null : ctx.SourceFiles.FirstOrDefault()),
            Evidence = new AnalysisEvidence { Kind = AnalysisEvidenceKind.FrameworkModel, Source = "mcp", Description = $"MCP server registration ({transport} transport).", Confidence = ConfidenceTiers.Syntactic }
        };
        service.Properties["transport"] = transport;
        if (registrationText.Contains("Stateless", StringComparison.Ordinal))
        {
            service.Properties["stateless"] = "true";
        }

        if (mountPath is not null)
        {
            service.Endpoints.Add(mountPath);
        }

        results.Services.Add(service);
    }

    private static void ApplyServerSecurityProperties(FrameworkContext ctx, FrameworkResults results, string registrationText)
    {
        var server = results.Services.FirstOrDefault(service => service.Framework == "mcp" && service.Direction == ServiceDirections.Inbound);
        if (server is null)
        {
            return;
        }

        // WithToolsFromAssembly() exposes every attributed type in the assembly — a broader surface
        // than an explicit WithTools<T>() list.
        if (registrationText.Contains("WithToolsFromAssembly", StringComparison.Ordinal))
        {
            server.Properties["toolDiscovery"] = "assembly-scan";
            server.Tags.Add("finding:mcp-assembly-wide-tool-exposure");
        }
        else if (registrationText.Contains("WithTools<", StringComparison.Ordinal))
        {
            server.Properties["toolDiscovery"] = "explicit";
        }

        // The SDK docs call for restricting Host headers on HTTP transport (DNS-rebinding guard).
        if (server.Properties.GetValueOrDefault("transport") == "http" && !registrationText.Contains("AllowedHosts", StringComparison.Ordinal) && !registrationText.Contains("AllowedOrigins", StringComparison.Ordinal))
        {
            server.Tags.Add("finding:mcp-http-host-header-unrestricted");
        }
    }

    private static void EmitTool(FrameworkContext ctx, FrameworkResults results, TypeDeclarationSyntax typeDeclaration, MethodDeclarationSyntax method, List<AttributeSyntax> methodAttributes, AttributeSyntax toolAttribute, SemanticModel model, SyntaxTree tree, bool isToolType)
    {
        // McpServerToolAttribute carries the tool name as the `Name` property, not a positional
        // argument; its other properties (Title, Destructive, Idempotent, ReadOnly, OpenWorld,
        // UseStructuredContent) must never be mistaken for it. Absent an explicit Name the SDK
        // defaults the tool to the method name.
        var explicitName = ProviderHelpers.AttributeStringArguments(toolAttribute, "Name").FirstOrDefault()
                           ?? ProviderHelpers.AttributeArgumentText(toolAttribute, model);
        var toolName = string.IsNullOrWhiteSpace(explicitName) ? method.Identifier.Text : explicitName;
        var lineSpan = method.GetLocation().GetLineSpan().StartLinePosition;
        var methodSymbol = model.GetDeclaredSymbol(method);
        var methodId = methodSymbol is null ? null : Depscan.Dosai.FormatMethodSignature(methodSymbol);
        var serviceId = FrameworkIds.Service("mcp", null, "mcp-server");
        var operationId = FrameworkIds.Operation(serviceId, null, $"tool:{toolName}", toolName);

        var operation = new ServiceOperation
        {
            Id = operationId,
            Name = toolName,
            MethodId = methodId,
            RequestType = methodSymbol?.Parameters.FirstOrDefault()?.Type.ToDisplayString(),
            ResponseType = methodSymbol?.ReturnType.ToDisplayString(),
            Confidence = isToolType ? ConfidenceTiers.Syntactic : ConfidenceTiers.Syntactic,
            Location = CodeLocation.From(ctx.BasePath, tree.FilePath, lineSpan.Line + 1, lineSpan.Character + 1)
        };
        operation.Properties["kind"] = "tool";

        var server = results.Services.FirstOrDefault(service => service.Framework == "mcp" && service.Direction == ServiceDirections.Inbound);
        server?.Operations.Add(operation);

        var authorized = ProviderHelpers.AttributesOf(typeDeclaration.AttributeLists).Concat(methodAttributes).Any(attribute => ProviderHelpers.IsNamed(attribute, "Authorize"));
        if (authorized)
        {
            operation.Authenticated = true;
        }

        var endpoint = new ApiEndpoint
        {
            Path = server?.Endpoints.FirstOrDefault() ?? mountPathOf(ctx),
            FilePath = CodeLocation.From(ctx.BasePath, tree.FilePath).Path,
            FileName = Path.GetFileName(tree.FilePath),
            Namespace = typeDeclaration.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString(),
            ClassName = typeDeclaration.Identifier.Text,
            MethodName = method.Identifier.Text,
            EndpointKind = "McpTool",
            RoutingKind = "Mount",
            Framework = "mcp",
            ServiceId = serviceId,
            OperationId = operationId,
            Confidence = ConfidenceTiers.Syntactic,
            LineNumber = lineSpan.Line + 1,
            ColumnNumber = lineSpan.Character + 1,
            RawUrls = ctx.RawUrlsFor(tree),
            AuthorizationRequired = authorized ? true : null,
            Route = $"tool:{toolName}",
            Evidence = new AnalysisEvidence { Kind = AnalysisEvidenceKind.FrameworkModel, Source = "mcp", Description = $"MCP tool '{toolName}'.", Confidence = ConfidenceTiers.Syntactic, FileName = Path.GetFileName(tree.FilePath), LineNumber = lineSpan.Line + 1 }
        };
        results.ApiEndpoints.Add(endpoint);
        results.EntryPoints.Add(new EntryPoint
        {
            Id = $"ep:{operationId}",
            Kind = "McpTool",
            MethodId = methodId,
            MethodName = method.Identifier.Text,
            ClassName = typeDeclaration.Identifier.Text,
            Namespace = endpoint.Namespace,
            FileName = endpoint.FileName,
            Path = endpoint.FilePath,
            LineNumber = endpoint.LineNumber,
            ColumnNumber = endpoint.ColumnNumber,
            Route = $"tool:{toolName}"
        });

        // Tool arguments are attacker-controlled in exactly the way HTTP parameters are.
        foreach (var parameter in method.ParameterList.Parameters)
        {
            var typeName = parameter.Type?.ToString() ?? string.Empty;
            if (ToolSchemaBuilder.IsInfrastructureParameter(parameter, typeName))
            {
                continue;
            }

            results.TaintSeeds.Add(new FrameworkTaintSeed
            {
                MethodName = method.Identifier.Text,
                ParameterName = parameter.Identifier.Text,
                ClassName = typeDeclaration.Identifier.Text,
                Namespace = endpoint.Namespace,
                FileName = endpoint.FileName,
                MethodSignature = methodId,
                LineNumber = lineSpan.Line + 1,
                BindingSource = "mcp-tool-arg",
                TaintKind = "mcp",
                FrameworkId = "mcp",
                EndpointPath = $"tool:{toolName}",
                Confidence = methodSymbol is null ? ConfidenceTiers.Syntactic : ConfidenceTiers.Semantic
            });
        }

        // The JSON Schema the SDK derives from the signature: [Description] on the method and
        // every parameter, excluding progress/cancellation/DI parameters.
        var schema = ToolSchemaBuilder.BuildSchema(methodSymbol, method, methodAttributes);
        results.AiComponents.Add(new AiComponent
        {
            Id = FrameworkIds.Ai("tool", "mcp", toolName),
            Kind = "tool",
            Name = toolName,
            Provider = "mcp",
            ToolSchema = schema,
            ServiceIds = [serviceId],
            Confidence = ConfidenceTiers.Syntactic,
            Location = CodeLocation.From(ctx.BasePath, tree.FilePath, lineSpan.Line + 1, lineSpan.Character + 1),
            Evidence = new AnalysisEvidence { Kind = AnalysisEvidenceKind.FrameworkModel, Source = "mcp", Description = "MCP tool with derived JSON Schema.", Confidence = ConfidenceTiers.Syntactic, FileName = Path.GetFileName(tree.FilePath), LineNumber = lineSpan.Line + 1 }
        });
    }

    private static string? mountPathOf(FrameworkContext ctx) => ctx.MountPoints.FirstOrDefault(mount => mount.Kind == "mcp")?.Path;

    private static void EmitPrompt(FrameworkContext ctx, FrameworkResults results, TypeDeclarationSyntax typeDeclaration, MethodDeclarationSyntax method, AttributeSyntax promptAttribute, SemanticModel model, SyntaxTree tree)
    {
        var explicitName = ProviderHelpers.AttributeArgumentText(promptAttribute, model);
        var promptName = string.IsNullOrWhiteSpace(explicitName) ? method.Identifier.Text : explicitName;
        var lineSpan = method.GetLocation().GetLineSpan().StartLinePosition;
        var operationId = FrameworkIds.Operation(FrameworkIds.Service("mcp", null, "mcp-server"), null, $"prompt:{promptName}", promptName);
        var server = results.Services.FirstOrDefault(service => service.Framework == "mcp" && service.Direction == ServiceDirections.Inbound);
        server?.Operations.Add(new ServiceOperation
        {
            Id = operationId,
            Name = promptName,
            Confidence = ConfidenceTiers.Syntactic,
            Location = CodeLocation.From(ctx.BasePath, tree.FilePath, lineSpan.Line + 1)
        });
        results.EntryPoints.Add(new EntryPoint
        {
            Id = $"ep:{operationId}",
            Kind = "McpPrompt",
            MethodName = method.Identifier.Text,
            ClassName = typeDeclaration.Identifier.Text,
            FileName = Path.GetFileName(tree.FilePath),
            LineNumber = lineSpan.Line + 1,
            Route = $"prompt:{promptName}"
        });
    }

    private static void EmitResource(FrameworkContext ctx, FrameworkResults results, TypeDeclarationSyntax typeDeclaration, MethodDeclarationSyntax method, SemanticModel model, SyntaxTree tree)
    {
        var resourceName = method.Identifier.Text;
        var lineSpan = method.GetLocation().GetLineSpan().StartLinePosition;
        var operationId = FrameworkIds.Operation(FrameworkIds.Service("mcp", null, "mcp-server"), null, $"resource:{resourceName}", resourceName);
        results.EntryPoints.Add(new EntryPoint
        {
            Id = $"ep:{operationId}",
            Kind = "McpResource",
            MethodName = method.Identifier.Text,
            ClassName = typeDeclaration.Identifier.Text,
            FileName = Path.GetFileName(tree.FilePath),
            LineNumber = lineSpan.Line + 1,
            Route = $"resource:{resourceName}"
        });
    }

    /// <summary>Outbound MCP clients: factory transports. Stdio captures the launched command (supply-chain fact).</summary>
    private static void EmitClientService(FrameworkContext ctx, FrameworkResults results, SyntaxTree tree, Microsoft.CodeAnalysis.CSharp.Syntax.CompilationUnitSyntax root)
    {
        string? url = null;
        string? command = null;
        var arguments = new List<string>();
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var name = ProviderHelpers.InvocationName(invocation);
            if (name is not ("WithUrl" or "CreateAsync") && !name.EndsWith("ClientTransport", StringComparison.Ordinal))
            {
                continue;
            }

            url ??= ProviderHelpers.StringArguments(invocation).FirstOrDefault(candidate => candidate.StartsWith("http", StringComparison.OrdinalIgnoreCase));
        }

        // StdioClientTransport(new StdioClientTransportOptions { Command = "npx", Arguments = [...] })
        foreach (var assignment in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            var left = assignment.Left.ToString();
            if (left.EndsWith("Command", StringComparison.Ordinal) && assignment.Right is LiteralExpressionSyntax commandLiteral && commandLiteral.Token.Value is string commandValue)
            {
                command = commandValue;
            }
            else if (left.EndsWith("Arguments", StringComparison.Ordinal) && assignment.Right is ImplicitArrayCreationExpressionSyntax array)
            {
                arguments.AddRange(array.Initializer?.Expressions.OfType<LiteralExpressionSyntax>().Where(literal => literal.Token.Value is string).Select(literal => (string)literal.Token.Value!) ?? []);
            }
        }

        var clientService = new ServiceComponent
        {
            Id = FrameworkIds.Service("mcp", null, command is null ? "mcp-client" : $"mcp-client-{command}"),
            Name = command is null ? "MCP client" : $"MCP client ({command})",
            ServiceKind = ServiceKinds.Mcp,
            Direction = ServiceDirections.Outbound,
            Framework = "mcp",
            Confidence = ConfidenceTiers.Syntactic,
            Location = CodeLocation.From(ctx.BasePath, tree.FilePath),
            Evidence = new AnalysisEvidence { Kind = AnalysisEvidenceKind.FrameworkModel, Source = "mcp", Description = "MCP client transport.", Confidence = ConfidenceTiers.Syntactic }
        };
        clientService.Properties["transport"] = command is not null ? "stdio" : url is not null ? "http" : "unknown";
        if (command is not null)
        {
            clientService.Properties["command"] = command;
            if (arguments.Count > 0)
            {
                clientService.Properties["arguments"] = string.Join(" ", arguments);
            }

            clientService.Tags.Add("supply-chain:launches-external-process");
        }

        if (url is not null)
        {
            clientService.Endpoints.Add(url);
        }

        results.Services.Add(clientService);
    }
}

/// <summary>Builds the JSON Schema the MCP SDK derives from a tool method's signature.</summary>
internal static class ToolSchemaBuilder
{
    /// <summary>
    ///     Parameters <c>McpServerTool.Create</c> binds from the host rather than from the client's
    ///     JSON, and therefore keeps out of the tool's input schema.
    /// </summary>
    /// <remarks>
    ///     Getting this set wrong is not cosmetic in either direction: a host-bound parameter left in
    ///     the schema is advertised to the model as a required argument that the SDK will never accept,
    ///     and — because MCP tool arguments are seeded as attacker-controlled taint sources — it also
    ///     invents an untrusted input that no attacker can actually reach.
    /// </remarks>
    internal static bool IsInfrastructureParameter(ParameterSyntax parameter, string typeName) =>
        typeName.Contains("CancellationToken", StringComparison.Ordinal) ||
        typeName.Contains("IProgress", StringComparison.Ordinal) ||
        typeName.Contains("ILogger", StringComparison.Ordinal) ||
        typeName.Contains("IMcpServer", StringComparison.Ordinal) ||
        typeName.Contains("RequestContext", StringComparison.Ordinal) ||
        typeName.Contains("ServerCallContext", StringComparison.Ordinal) ||
        ProviderHelpers.AttributesOf(parameter.AttributeLists)
            .Any(attribute => ProviderHelpers.IsNamed(attribute, "FromServices") ||
                              ProviderHelpers.IsNamed(attribute, "FromKeyedServices"));

    public static string BuildSchema(IMethodSymbol? methodSymbol, MethodDeclarationSyntax method, List<AttributeSyntax> methodAttributes)
    {
        var properties = new List<string>();
        var required = new List<string>();
        foreach (var parameter in method.ParameterList.Parameters)
        {
            var typeName = parameter.Type?.ToString() ?? "string";
            if (IsInfrastructureParameter(parameter, typeName))
            {
                continue;
            }

            var description = ProviderHelpers.AttributesOf(parameter.AttributeLists)
                .Where(attribute => ProviderHelpers.IsNamed(attribute, "Description"))
                .Select(attribute => ProviderHelpers.AttributeArgumentText(attribute, null))
                .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));
            var jsonType = JsonTypeOf(typeName);
            var property = $"\"{parameter.Identifier.Text}\":{{\"type\":\"{jsonType}\"";
            if (!string.IsNullOrWhiteSpace(description))
            {
                property += $",\"description\":\"{Escape(description!)}\"";
            }

            if (parameter.Default is not null)
            {
                // Emit the default with its JSON type: quoting every default turned `int limit = 10`
                // into "default":"10" and a null default into "default":"", neither of which is the
                // schema the SDK sends.
                var defaultLiteral = parameter.Default.Value as LiteralExpressionSyntax;
                var defaultText = defaultLiteral?.Token.ValueText;
                property += defaultLiteral?.Token.Value switch
                {
                    null => ",\"default\":null",
                    bool flag => $",\"default\":{(flag ? "true" : "false")}",
                    string text => $",\"default\":\"{Escape(text)}\"",
                    _ => $",\"default\":{defaultText}"
                };
            }
            else
            {
                required.Add(parameter.Identifier.Text);
            }

            properties.Add(property + "}");
        }

        var schema = "{\"type\":\"object\",\"properties\":{" + string.Join(",", properties) + "}";
        if (required.Count > 0)
        {
            schema += ",\"required\":[" + string.Join(",", required.Select(name => $"\"{name}\"")) + "]";
        }

        var methodDescription = methodAttributes
            .Where(attribute => ProviderHelpers.IsNamed(attribute, "Description"))
            .Select(attribute => ProviderHelpers.AttributeArgumentText(attribute, null))
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));
        if (!string.IsNullOrWhiteSpace(methodDescription))
        {
            schema += $",\"description\":\"{Escape(methodDescription!)}\"";
        }

        return schema + "}";
    }

    private static string JsonTypeOf(string clrType) => clrType switch
    {
        "int" or "long" or "short" or "byte" or "System.Int32" or "System.Int64" => "integer",
        "double" or "float" or "decimal" or "System.Double" or "System.Single" or "System.Decimal" => "number",
        "bool" or "System.Boolean" => "boolean",
        _ when clrType.EndsWith("[]") || clrType.StartsWith("List<") || clrType.StartsWith("IEnumerable<") || clrType.StartsWith("System.Collections") => "array",
        _ => "string"
    };

    private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
