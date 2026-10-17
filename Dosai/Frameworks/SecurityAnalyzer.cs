using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Depscan.Frameworks;

/// <summary>
///     Post-provider security analysis: turns the endpoint/auth metadata the providers
///     already collect, the MCP stdio transports they inventory, and the configuration files they
///     discover into severity-tagged findings. Every sub-analyzer is exception-isolated per the
///     FrameworkRegistry contract, a failure degrades to a diagnostic, never aborts analysis.
/// </summary>
public static class SecurityAnalyzer
{
    private static readonly string[] SensitiveClassifications = ["pii", "credential", "financial", "health"];

    public static List<SecurityFinding> Run(FrameworkContext context, FrameworkAnalysisResult frameworkResult, IEnumerable<ApiEndpoint> endpoints)
    {
        var findings = new List<SecurityFinding>();
        var endpointList = endpoints.ToList();

        SafeRun(context, "endpoint-security", ids => AnalyzeEndpoints(context, frameworkResult, endpointList, findings, ids));
        SafeRun(context, "mcp-transport", ids => McpTransportAnalyzer.Assess(frameworkResult.Services, findings, ids, context.McpAllowlist));
        SafeRun(context, "config-security", ids => ConfigSecurityAnalyzer.Assess(context.ConfigFiles, findings, ids, context.Diagnostics));
        SafeRun(context, "middleware-security", ids => MiddlewareSecurityAnalyzer.Assess(context, endpointList, findings, ids));

        return findings
            .OrderBy(finding => SeverityRank(finding.Severity), Comparer<int>.Create((a, b) => b.CompareTo(a)))
            .ThenBy(finding => finding.Kind, StringComparer.Ordinal)
            .ThenBy(finding => finding.Id, StringComparer.Ordinal)
            .ToList();
    }

    private static void SafeRun(FrameworkContext context, string analyzerId, Action<FindingIds> body)
    {
        try
        {
            body(new FindingIds(analyzerId));
        }
        catch (Exception ex)
        {
            context.Diagnostics.Add(new FrameworkDiagnostic(analyzerId, $"Security analyzer failed and was skipped: {ex.Message}"));
        }
    }

    private static int SeverityRank(string severity) => severity.ToLowerInvariant() switch
    {
        "high" => 4,
        "medium" => 3,
        "low" => 2,
        "info" => 1,
        _ => 2
    };

    /// <summary>Content-derived finding ids: stable across runs and independent of finding order.</summary>
    public sealed class FindingIds(string analyzerId)
    {
        private readonly Dictionary<string, int> _issued = new(StringComparer.Ordinal);

        public string Next(string kind, string? fileName, int line)
        {
            var baseSlug = $"{analyzerId}-{kind}-{Sanitize(fileName)}-{line}";
            var occurrence = _issued.TryGetValue(baseSlug, out var count) ? count + 1 : 1;
            _issued[baseSlug] = occurrence;
            return occurrence == 1 ? baseSlug : $"{baseSlug}-{occurrence}";
        }

        private static string Sanitize(string? value) => string.IsNullOrWhiteSpace(value) ? "nofile" : value.Replace('/', '-').Replace('\\', '-').Replace(' ', '-');
    }

    // ----- Endpoint security findings -----

    private static void AnalyzeEndpoints(FrameworkContext context, FrameworkAnalysisResult frameworkResult, List<ApiEndpoint> endpoints, List<SecurityFinding> findings, FindingIds ids)
    {
        var servicesById = frameworkResult.Services.ToDictionary(service => service.Id, StringComparer.Ordinal);
        // A globally-registered antiforgery filter (services.AddMvc(o => o.Filters.Add<AutoValidateAntiforgeryTokenAttribute>()))
        // validates every mutating handler, per-endpoint findings would be false positives.
        // Razor Pages validate antiforgery automatically (they are skipped by endpoint kind).
        var globalAntiforgery = HasGlobalAntiforgeryFilter(context);
        foreach (var endpoint in endpoints)
        {
            var sensitive = SensitiveInboundClassifications(endpoint, servicesById);
            var anonymous = endpoint.AllowAnonymous || endpoint.AuthorizationRequired == false;

            // 1. Sensitive unauthenticated endpoint.
            if (anonymous && sensitive.Count > 0)
            {
                findings.Add(new SecurityFinding
                {
                    Id = ids.Next("SensitiveUnauthenticatedEndpoint", endpoint.FileName, endpoint.LineNumber),
                    Kind = "SensitiveUnauthenticatedEndpoint",
                    Title = $"Anonymous endpoint {DescribeEndpoint(endpoint)} exchanges {string.Join(", ", sensitive)} data",
                    Severity = "high",
                    Cwe = "CWE-306",
                    EndpointId = endpoint.OperationId,
                    Route = endpoint.Path ?? endpoint.Route,
                    FileName = endpoint.FileName,
                    LineNumber = endpoint.LineNumber,
                    Evidence = $"Classifications: {string.Join(", ", sensitive)}. Auth metadata: {(endpoint.AllowAnonymous ? "[AllowAnonymous]" : endpoint.AuthorizationRequired == false ? "no authorization" : "unknown")}.",
                    Remediation = "Require authorization for endpoints that read or return sensitive data, or strip the sensitive fields for anonymous callers.",
                    Confidence = ConfidenceTiers.Semantic,
                    Properties = { ["classifications"] = string.Join(",", sensitive) }
                });
            }

            // 3. State-changing endpoint without antiforgery (CWE-352). Bearer-token APIs are
            // exempt (no cookie to forge); Razor Pages validate automatically; a global
            // AutoValidateAntiforgeryToken filter already covers MVC.
            var mutating = endpoint.HttpMethod is "POST" or "PUT" or "DELETE" or "PATCH";
            var bearerOnly = endpoint.AuthenticationSchemes.Count > 0 && endpoint.AuthenticationSchemes.All(scheme => scheme.Contains("Bearer", StringComparison.OrdinalIgnoreCase));
            if (mutating && !bearerOnly && !globalAntiforgery && endpoint.AntiForgeryRequired != false &&
                endpoint.EndpointKind is "Attribute" or "MinimalApi" or "HttpController" or "Conventional")
            {
                findings.Add(new SecurityFinding
                {
                    Id = ids.Next("StateChangingEndpointWithoutAntiforgery", endpoint.FileName, endpoint.LineNumber),
                    Kind = "StateChangingEndpointWithoutAntiforgery",
                    Title = $"{endpoint.HttpMethod} endpoint {DescribeEndpoint(endpoint)} does not opt into antiforgery",
                    Severity = "low",
                    Cwe = "CWE-352",
                    EndpointId = endpoint.OperationId,
                    Route = endpoint.Path ?? endpoint.Route,
                    FileName = endpoint.FileName,
                    LineNumber = endpoint.LineNumber,
                    Evidence = "Mutating handler with AntiForgeryRequired unset (no [ValidateAntiForgeryToken] or RequireAntiforgery detected).",
                    Remediation = "Add antiforgery validation ([ValidateAntiForgeryToken] / RequireAntiforgery()) or switch the endpoint to bearer-token authentication.",
                    Confidence = ConfidenceTiers.Heuristic
                });
            }

            // 4. Antiforgery switched off on a state-changing, cookie-capable endpoint
            // ([IgnoreAntiforgeryToken], DisableAntiforgery()). An explicit opt-out overrides a
            // global filter, so it is reported even when one exists.
            if (mutating && !bearerOnly && endpoint.AntiForgeryRequired == false && !endpoint.AllowAnonymous &&
                endpoint.EndpointKind is "Attribute" or "MinimalApi" or "HttpController" or "Conventional")
            {
                findings.Add(new SecurityFinding
                {
                    Id = ids.Next("StateChangingEndpointAntiforgeryDisabled", endpoint.FileName, endpoint.LineNumber),
                    Kind = "StateChangingEndpointAntiforgeryDisabled",
                    Title = $"{endpoint.HttpMethod} endpoint {DescribeEndpoint(endpoint)} disables antiforgery validation",
                    Severity = "medium",
                    Cwe = "CWE-352",
                    EndpointId = endpoint.OperationId,
                    Route = endpoint.Path ?? endpoint.Route,
                    FileName = endpoint.FileName,
                    LineNumber = endpoint.LineNumber,
                    Evidence = "Mutating handler opts out of antiforgery ([IgnoreAntiforgeryToken] or DisableAntiforgery()).",
                    Remediation = "Keep antiforgery validation for cookie-authenticated endpoints, or restrict the endpoint to bearer-token authentication.",
                    Confidence = ConfidenceTiers.Syntactic
                });
            }

            // 5. Mass-assignment hint (informational, name-heuristic only).
            var dtoName = servicesById.GetValueOrDefault(endpoint.ServiceId ?? string.Empty)?.Data?
                .FirstOrDefault(data => data.Flow is "inbound" or "bi-directional")?.Name;
            if (anonymous && dtoName is not null && MassAssignmentShapes(dtoName, context))
            {
                findings.Add(new SecurityFinding
                {
                    Id = ids.Next("MassAssignmentHint", endpoint.FileName, endpoint.LineNumber),
                    Kind = "MassAssignmentHint",
                    Title = $"Anonymous endpoint {DescribeEndpoint(endpoint)} binds a DTO with role/enablement-shaped members",
                    Severity = "info",
                    Cwe = "CWE-915",
                    EndpointId = endpoint.OperationId,
                    Route = endpoint.Path ?? endpoint.Route,
                    FileName = endpoint.FileName,
                    LineNumber = endpoint.LineNumber,
                    Evidence = $"Request model {dtoName} has bindable properties shaped like role/enablement flags.",
                    Remediation = "Use a dedicated request DTO without role/enablement members, or [Bind Never] them, so unprivileged callers cannot set privileged fields.",
                    Confidence = ConfidenceTiers.Heuristic
                });
            }
        }

        // 4. Duplicated routes with different auth levels.
        foreach (var group in endpoints
                     .Where(endpoint => !string.IsNullOrWhiteSpace(endpoint.Path) && !string.IsNullOrWhiteSpace(endpoint.HttpMethod))
                     .GroupBy(endpoint => $"{endpoint.HttpMethod} {endpoint.Path}", StringComparer.OrdinalIgnoreCase))
        {
            var members = group.ToList();
            if (members.Count < 2)
            {
                continue;
            }

            var anonymousMembers = members.Where(endpoint => endpoint.AllowAnonymous || endpoint.AuthorizationRequired == false).ToList();
            var protectedMembers = members.Where(endpoint => !endpoint.AllowAnonymous && endpoint.AuthorizationRequired == true).ToList();
            if (anonymousMembers.Count == 0 || protectedMembers.Count == 0)
            {
                continue;
            }

            findings.Add(new SecurityFinding
            {
                Id = ids.Next("DuplicateRouteAuthMismatch", anonymousMembers[0].FileName, anonymousMembers[0].LineNumber),
                Kind = "DuplicateRouteAuthMismatch",
                Title = $"Route {group.Key} is served with mixed authorization levels",
                Severity = "medium",
                Cwe = "CWE-306",
                Route = members[0].Path,
                FileName = anonymousMembers[0].FileName,
                LineNumber = anonymousMembers[0].LineNumber,
                Evidence = $"Anonymous handler in {anonymousMembers[0].FileName}:{anonymousMembers[0].LineNumber}; protected handler in {protectedMembers[0].FileName}:{protectedMembers[0].LineNumber}.",
                Remediation = "Serve one route with one authorization policy; route templates shared across handlers must agree on auth requirements.",
                Confidence = ConfidenceTiers.Semantic
            });
        }

        // 2. CORS wildcard on credentialed routes (CWE-942): policy definitions come from source.
        foreach (var (policyName, fileName, line) in FindWildcardCredentialCorsPolicies(context).DistinctBy(item => (item.PolicyName, item.FileName, item.Line)))
        {
            findings.Add(new SecurityFinding
            {
                Id = ids.Next("CorsWildcardWithCredentials", fileName, line),
                Kind = "CorsWildcardWithCredentials",
                Title = policyName is null
                    ? "Default CORS policy allows any origin together with credentials"
                    : $"CORS policy '{policyName}' allows any origin together with credentials",
                Severity = "medium",
                Cwe = "CWE-942",
                Route = policyName,
                FileName = fileName,
                LineNumber = line,
                Evidence = $"AllowAnyOrigin/SetIsOriginAllowed combined with AllowCredentials in {fileName}:{line}.",
                Remediation = "Restrict origins to an explicit allowlist; AllowCredentials requires explicit origins per the CORS specification.",
                Confidence = ConfidenceTiers.Heuristic
            });
        }
    }

    /// <summary>
    ///     Detects a globally-registered antiforgery filter. Syntactic by design (filter
    ///     registration is config-like code): a tree containing both a Filters.Add registration and
    ///     AutoValidateAntiforgeryToken/ValidateAntiForgeryToken counts. Findings produced off
    ///     this path keep Heuristic confidence.
    /// </summary>
    private static bool HasGlobalAntiforgeryFilter(FrameworkContext context)
    {
        if (context.CSharp is null)
        {
            return false;
        }

        foreach (var tree in context.CSharpTrees)
        {
            var text = context.TextFor(tree);
            if (text.Contains("AutoValidateAntiforgeryToken", StringComparison.Ordinal) && text.Contains("Filters.Add", StringComparison.Ordinal) ||
                text.Contains("ValidateAntiForgeryToken", StringComparison.Ordinal) && text.Contains("Filters.Add<", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string DescribeEndpoint(ApiEndpoint endpoint) =>
        string.IsNullOrWhiteSpace(endpoint.Path) ? $"{endpoint.HttpMethod ?? "handler"} {endpoint.MethodName}" : $"{endpoint.HttpMethod ?? "GET"} {endpoint.Path}";

    private static List<string> SensitiveInboundClassifications(ApiEndpoint endpoint, IReadOnlyDictionary<string, ServiceComponent> servicesById)
    {
        if (string.IsNullOrWhiteSpace(endpoint.ServiceId) || !servicesById.TryGetValue(endpoint.ServiceId, out var service) || service.Data is null)
        {
            return [];
        }

        return service.Data
            .Where(data => data.Flow is "inbound" or "bi-directional")
            .Select(data => data.Classification)
            .Where(classification => SensitiveClassifications.Contains(classification, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList()!;
    }

    /// <summary>Name-heuristic mass-assignment check over the request DTO's bindable members.</summary>
    private static bool MassAssignmentShapes(string dtoTypeName, FrameworkContext context)
    {
        if (context.CSharp is null)
        {
            return false;
        }

        var simpleName = dtoTypeName.Split('.', '+').LastOrDefault()?.Split('<').FirstOrDefault() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(simpleName))
        {
            return false;
        }

        foreach (var tree in context.CSharpTrees)
        {
            foreach (var typeDeclaration in FrameworkContext.Declarations<TypeDeclarationSyntax>(tree))
            {
                if (!string.Equals(typeDeclaration.Identifier.Text, simpleName, StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var member in typeDeclaration.Members.OfType<PropertyDeclarationSyntax>())
                {
                    var name = member.Identifier.Text;
                    if (MassAssignmentMemberNames.Any(shape => name.Contains(shape, StringComparison.OrdinalIgnoreCase)))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static readonly string[] MassAssignmentMemberNames =
    [
        "role", "isadmin", "isactive", "isenabled", "islocked", "permissions", "claims", "securitystamp", "isverified", "emailconfirmed", "phonenumberconfirmed", "twofactorenabled"
    ];

    /// <summary>
    ///     Finds AddCors policy registrations whose builder chain enables any origin and credentials
    ///     together (CWE-942). Registration names resolve through the semantic operation when
    ///     references are available (the syntactic name is only a fallback, per the no-
    ///     SyntaxNode.ToString rule); only the outermost registration of a chain is reported so
    ///     AddCors + its inner AddPolicy lambda do not double-count, and the policy name is taken
    ///     from the FIRST string argument only (never a URL from a later argument).
    /// </summary>
    private static List<(string? PolicyName, string FileName, int Line)> FindWildcardCredentialCorsPolicies(FrameworkContext context)
    {
        var results = new List<(string?, string, int)>();
        if (context.CSharp is null)
        {
            return results;
        }

        foreach (var tree in context.CSharpTrees)
        {
            var model = context.CSharp.GetSemanticModel(tree);
            foreach (var invocation in context.InvocationsNamed(tree, IsCorsRegistrationName))
            {
                var syntacticName = ProviderHelpers.InvocationName(invocation);
                if (!IsCorsRegistrationName(syntacticName))
                {
                    continue;
                }

                // When the symbol resolves, confirm the semantic name agrees with the syntactic one.
                if (OperationDepthGuard.GetOperation(model, invocation) is IInvocationOperation { TargetMethod: { } targetMethod } &&
                    !IsCorsRegistrationName(targetMethod.Name))
                {
                    continue;
                }

                // Report each policy at its own registration: an AddCors(options => { AddPolicy("a",
                // ...); AddPolicy("b", ...); }) is judged per AddPolicy, so the finding names the
                // policy that is open (the outermost call used to be judged on the text of every
                // policy inside it, and named none). An AddCors with no policy registration inside
                // is the registration itself.
                if (invocation.ArgumentList.Arguments.Any(argument => argument.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(inner => IsCorsRegistrationName(ProviderHelpers.InvocationName(inner)))))
                {
                    continue;
                }

                string? policyName = null;
                var arguments = invocation.ArgumentList.Arguments;
                if (syntacticName != "AddDefaultPolicy" && arguments.Count > 1 && model.GetConstantValue(arguments[0].Expression) is { HasValue: true, Value: string name })
                {
                    policyName = name;
                }

                // The policy builder chain is the last argument (the config lambda). Reading the
                // chain needs code text, there is no symbol API for "allows any origin", so this
                // is the allowed SyntaxNode.ToString() fallback; everything above it is symbol/name
                // based.
                var chainText = arguments.Count > 0 ? arguments[^1].ToString() : invocation.ToString();
                var anyOrigin = chainText.Contains("AllowAnyOrigin", StringComparison.Ordinal) || chainText.Contains("SetIsOriginAllowed", StringComparison.Ordinal);
                var credentials = chainText.Contains("AllowCredentials", StringComparison.Ordinal);
                if (anyOrigin && credentials)
                {
                    var line = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    results.Add((policyName, Path.GetFileName(tree.FilePath), line));
                }
            }
        }

        return results;
    }

    private static bool IsCorsRegistrationName(string name) =>
        name is "AddCors" or "AddPolicy" or "AddDefaultPolicy";
}

/// <summary>MCP stdio transport integrity, the transports Dosai inventories become supply-chain assessments.</summary>
public static class McpTransportAnalyzer
{
    private static readonly HashSet<string> SafeCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "node", "npm", "npx", "python", "python3", "uv", "uvx", "dotnet", "deno", "bun", "docker", "java"
    };

    /// <summary>Exec patterns that commonly indicate remote-fetch-and-execute installs or shell piping.</summary>
    private static readonly string[] RiskyArgumentPatterns =
    [
        "curl ", "wget ", "| sh", "|sh", "| bash", "|bash", "chmod +x", "$(curl", "$(wget", "rm -rf"
    ];

    public static void Assess(IEnumerable<ServiceComponent> services, List<SecurityFinding> findings, SecurityAnalyzer.FindingIds ids, IReadOnlySet<string>? allowlist)
    {
        foreach (var service in services.Where(service => service.ServiceKind == ServiceKinds.Mcp &&
                                                           service.Direction == ServiceDirections.Outbound &&
                                                           service.Properties.GetValueOrDefault("transport") == "stdio"))
        {
            var command = service.Properties.GetValueOrDefault("command");
            if (string.IsNullOrWhiteSpace(command))
            {
                continue;
            }

            if (allowlist is not null && allowlist.Contains(command))
            {
                continue;
            }

            var arguments = service.Properties.GetValueOrDefault("arguments") ?? string.Empty;
            var location = service.Location;
            var fileName = location?.Path is null ? null : Path.GetFileName(location.Path);
            var line = location?.LineNumber ?? 0;

            if (!SafeCommands.Contains(command))
            {
                findings.Add(new SecurityFinding
                {
                    Id = ids.Next("McpTransportRisk", fileName, line),
                    Kind = "McpTransportRisk",
                    Title = $"MCP stdio transport launches '{command}', which is not on the common runtime safe-list",
                    Severity = "medium",
                    Cwe = "CWE-1104",
                    EndpointId = service.Id,
                    FileName = fileName,
                    LineNumber = line,
                    Evidence = $"StdioClientTransport command '{command}' {arguments}".Trim(),
                    Remediation = "Launch MCP servers through a vetted package manager/runtime and pin the package and version; add the command to --mcp-allowlist if this launch is policy-approved.",
                    Confidence = ConfidenceTiers.Heuristic,
                    Properties = { ["transport"] = "stdio", ["command"] = command }
                });
            }

            foreach (var pattern in RiskyArgumentPatterns.Where(pattern => arguments.Contains(pattern, StringComparison.Ordinal)))
            {
                findings.Add(new SecurityFinding
                {
                    Id = ids.Next("McpTransportRisk", fileName, line),
                    Kind = "McpTransportRisk",
                    Title = $"MCP stdio transport arguments include a fetch-and-execute pattern ('{pattern.Trim()}')",
                    Severity = "high",
                    Cwe = "CWE-829",
                    EndpointId = service.Id,
                    FileName = fileName,
                    LineNumber = line,
                    Evidence = $"StdioClientTransport command '{command} {arguments}'",
                    Remediation = "Install the MCP server through a package manager instead of piping remote content to a shell.",
                    Confidence = ConfidenceTiers.Heuristic,
                    Properties = { ["transport"] = "stdio", ["command"] = command, ["pattern"] = pattern.Trim() }
                });
            }

            // Unversioned npx package: `npx -y something` without @version, supply-chain risk on
            // every launch: whatever is latest on the registry is what runs.
            var tokens = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (command.Equals("npx", StringComparison.OrdinalIgnoreCase) || command.Equals("bunx", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var token in tokens.Where(token => !token.StartsWith('-') && token.Contains('.', StringComparison.Ordinal) && !token.Contains('@')))
                {
                    findings.Add(new SecurityFinding
                    {
                        Id = ids.Next("McpTransportRisk", fileName, line),
                        Kind = "McpTransportRisk",
                        Title = $"MCP stdio transport runs unversioned package '{token}' via {command}",
                        Severity = "medium",
                        Cwe = "CWE-1104",
                        EndpointId = service.Id,
                        FileName = fileName,
                        LineNumber = line,
                        Evidence = $"StdioClientTransport command '{command} {arguments}'",
                        Remediation = $"Pin the package version (e.g. {token}@1.2.3) so the launched server is reproducible.",
                        Confidence = ConfidenceTiers.Heuristic,
                        Properties = { ["transport"] = "stdio", ["package"] = token }
                    });
                }
            }

            if (arguments.Contains("..", StringComparison.Ordinal) || (arguments.StartsWith("/", StringComparison.Ordinal) && !arguments.StartsWith("/usr", StringComparison.Ordinal) && !arguments.StartsWith("/opt", StringComparison.Ordinal)))
            {
                findings.Add(new SecurityFinding
                {
                    Id = ids.Next("McpTransportRisk", fileName, line),
                    Kind = "McpTransportRisk",
                    Title = $"MCP stdio transport references a path outside the typical project root ('{command} {arguments}')",
                    Severity = "low",
                    Cwe = "CWE-1104",
                    EndpointId = service.Id,
                    FileName = fileName,
                    LineNumber = line,
                    Evidence = $"StdioClientTransport command '{command} {arguments}'",
                    Remediation = "Reference MCP servers within the repository or through a package manager; absolute or parent-traversing paths couple the agent to machine state.",
                    Confidence = ConfidenceTiers.Heuristic,
                    Properties = { ["transport"] = "stdio", ["command"] = command }
                });
            }
        }
    }
}

/// <summary>Configuration security over the appsettings*.json / web.config files already collected.</summary>
public static class ConfigSecurityAnalyzer
{
    public static void Assess(IEnumerable<string> configFiles, List<SecurityFinding> findings, SecurityAnalyzer.FindingIds ids, List<FrameworkDiagnostic> diagnostics)
    {
        foreach (var file in configFiles)
        {
            try
            {
                var fileName = Path.GetFileName(file);
                if (fileName.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase) && fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    AssessAppSettings(file, File.ReadAllText(file), findings, ids);
                }
                else if (fileName.Equals("web.config", StringComparison.OrdinalIgnoreCase))
                {
                    AssessWebConfig(file, File.ReadAllText(file), findings, ids);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or System.Xml.XmlException)
            {
                // Config findings are best-effort, but unreadable files are reported, not swallowed
                // silently, a silently-skipped config file looks exactly like a clean one.
                diagnostics.Add(new FrameworkDiagnostic("config-security", $"Could not read config file {Path.GetFileName(file)}: {ex.Message}"));
            }
        }
    }

    private static void AssessAppSettings(string file, string text, List<SecurityFinding> findings, SecurityAnalyzer.FindingIds ids)
    {
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;

        // JwtBearer/OIDC hardening flags, enumerated WITHIN the section so validation flags are
        // not reported once per section found (and not reported for unrelated objects).
        foreach (var jwtSection in DescendantPropertiesWithPaths(root).Where(property =>
                     property.Property.Name.Equals("JwtBearer", StringComparison.OrdinalIgnoreCase) ||
                     property.Property.Name.Equals("OpenIdConnect", StringComparison.OrdinalIgnoreCase) ||
                     property.Property.Name.Equals("OIDC", StringComparison.OrdinalIgnoreCase)))
        {
            if (jwtSection.Property.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (jwtSection.Property.Value.TryGetProperty("RequireHttpsMetadata", out var requireHttps) && requireHttps.ValueKind == JsonValueKind.False)
            {
                findings.Add(Finding(ids, file, "RequireHttpsMetadata", "Jwt metadata fetched over plain HTTP", "medium", "CWE-319",
                    "RequireHttpsMetadata=false lets an attacker-in-the-middle swap the signing keys. Set it to true (the default) or pin the metadata URL to https.", text));
            }

            foreach (var validation in DescendantProperties(jwtSection.Property.Value).Where(property =>
                         property.Name is "ValidateIssuer" or "ValidateAudience" or "ValidateIssuerSigningKey" &&
                         property.Value.ValueKind == JsonValueKind.False))
            {
                findings.Add(Finding(ids, file, validation.Name, $"Token validation '{validation.Name}' disabled in configuration", "medium", "CWE-345",
                    $"{validation.Name}=false accepts tokens outside their intended issuer/audience. Enable validation and configure ValidIssuer/ValidAudience.", text));
            }
        }

        // Cookie/session hardening: HttpOnly/Secure/SameSite are only meaningful (and only flaggable)
        // inside cookie sections; the path is tracked during traversal to avoid flagging unrelated
        // properties that happen to be named "Secure" or "HttpOnly".
        foreach (var (path, property) in DescendantPropertiesWithPaths(root))
        {
            string? title = null;
            var severity = "low";
            var remediation = "Review the cookie configuration.";
            if (property.Name.Equals("HttpOnly", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.False && path.Contains("Cookie", StringComparison.OrdinalIgnoreCase))
            {
                title = "Cookie emitted without HttpOnly";
                severity = "medium";
                remediation = "HttpOnly=false exposes the cookie to script; keep HttpOnly on session cookies.";
            }
            else if (property.Name.Equals("Secure", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.False && path.Contains("Cookie", StringComparison.OrdinalIgnoreCase))
            {
                title = "Cookie emitted without Secure";
                severity = "medium";
                remediation = "Secure=false lets the cookie travel over plain HTTP; require HTTPS.";
            }
            else if (property.Name.Equals("SameSite", StringComparison.OrdinalIgnoreCase) &&
                     property.Value.ValueKind == JsonValueKind.String &&
                     property.Value.GetString()?.Equals("None", StringComparison.OrdinalIgnoreCase) == true)
            {
                title = "Cookie SameSite set to None";
                severity = "low";
                remediation = "SameSite=None requires Secure and widens CSRF exposure; prefer Lax or Strict.";
            }

            if (title is not null)
            {
                findings.Add(Finding(ids, file, property.Name, $"{title} ({path})", severity, "CWE-1004", remediation, text));
            }
        }

        // Kestrel TLS floors.
        foreach (var ssl in DescendantProperties(root).Where(property => property.Name.Equals("SslProtocols", StringComparison.OrdinalIgnoreCase)))
        {
            var protocols = ssl.Value.ValueKind == JsonValueKind.String ? ssl.Value.GetString() ?? string.Empty : string.Empty;
            if (protocols.Contains("Tls", StringComparison.OrdinalIgnoreCase) && !protocols.Contains("Tls12", StringComparison.OrdinalIgnoreCase) && !protocols.Contains("Tls13", StringComparison.OrdinalIgnoreCase) ||
                protocols.Contains("Ssl3", StringComparison.OrdinalIgnoreCase) || protocols.Contains("Tls11", StringComparison.OrdinalIgnoreCase) || protocols.Contains("Tls10", StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(Finding(ids, file, "SslProtocols", $"Kestrel TLS floor allows deprecated protocols ({protocols})", "medium", "CWE-327",
                    "Pin Kestrel:SslProtocols to Tls12 and Tls13 only.", text));
            }
        }

        // CORS in configuration: any-origin plus credentials (cross-checks the endpoint CORS rule).
        foreach (var cors in DescendantProperties(root).Where(property => property.Name.Equals("Cors", StringComparison.OrdinalIgnoreCase)))
        {
            var corsText = cors.Value.GetRawText();
            if (corsText.Contains("AllowAnyOrigin", StringComparison.OrdinalIgnoreCase) && corsText.Contains("AllowCredentials", StringComparison.OrdinalIgnoreCase) ||
                corsText.Contains("IsOriginAllowed", StringComparison.OrdinalIgnoreCase) && corsText.Contains("AllowCredentials", StringComparison.OrdinalIgnoreCase) && corsText.Contains("true", StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(Finding(ids, file, "Cors", "CORS configuration allows any origin together with credentials", "medium", "CWE-942",
                    "Restrict origins to an explicit allowlist; AllowCredentials requires explicit origins per the CORS specification.", text));
            }
        }
    }

    private static void AssessWebConfig(string file, string text, List<SecurityFinding> findings, SecurityAnalyzer.FindingIds ids)
    {
        if (text.Contains("httpOnlyCookies=\"false\"", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(Finding(ids, file, "httpOnlyCookies", "web.config disables HttpOnly on cookies", "medium", "CWE-1004",
                "Remove httpOnlyCookies=\"false\"; session cookies must stay HttpOnly.", text));
        }

        if (text.Contains("requireSSL=\"false\"", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(Finding(ids, file, "requireSSL", "web.config disables requireSSL on cookies", "medium", "CWE-319",
                "Remove requireSSL=\"false\" so auth cookies only travel over HTTPS.", text));
        }

        if (text.Contains("customErrors", StringComparison.OrdinalIgnoreCase) && text.Contains("mode=\"Off\"", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(Finding(ids, file, "customErrors", "web.config exposes full error details (customErrors Off)", "low", "CWE-209",
                "Set customErrors mode to RemoteOnly or On to avoid leaking stack traces.", text));
        }

        if (text.Contains("<deployment", StringComparison.OrdinalIgnoreCase) && text.Contains("retail=\"false\"", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(Finding(ids, file, "deployment", "web.config deployment retail=false leaves debug behavior enabled", "low", "CWE-489",
                "Set <deployment retail=\"true\" /> in production.", text));
        }
    }

    private static SecurityFinding Finding(SecurityAnalyzer.FindingIds ids, string file, string property, string title, string severity, string cwe, string remediation, string text) => new()
    {
        Id = ids.Next("ConfigSecurity", Path.GetFileName(file), FindPropertyLine(text, property)),
        Kind = "ConfigSecurity",
        Title = title,
        Severity = severity,
        Cwe = cwe,
        FileName = Path.GetFileName(file),
        LineNumber = FindPropertyLine(text, property),
        Evidence = $"{Path.GetFileName(file)}: {property}",
        Remediation = remediation,
        Confidence = ConfidenceTiers.Syntactic,
        Properties = { ["configFile"] = Path.GetFileName(file), ["property"] = property }
    };

    /// <summary>
    ///     Line number of the quoted property name. No bare-substring fallback: a hit on
    ///     "RequireHttpsMetadata" inside a different identifier or a comment would point the
    ///     finding at the wrong line, and a wrong location is worse than no location.
    /// </summary>
    private static int FindPropertyLine(string text, string property)
    {
        var index = text.IndexOf($"\"{property}\"", StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return 0;
        }

        return 1 + text[..index].Count(character => character == '\n');
    }

    private static IEnumerable<JsonProperty> DescendantProperties(JsonElement element)
    {
        foreach (var (_, property) in DescendantPropertiesWithPaths(element))
        {
            yield return property;
        }
    }

    private static IEnumerable<(string Path, JsonProperty Property)> DescendantPropertiesWithPaths(JsonElement element, string? path = null)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var propertyPath = string.IsNullOrWhiteSpace(path) ? property.Name : $"{path}:{property.Name}";
                    yield return (propertyPath, property);
                    foreach (var descendant in DescendantPropertiesWithPaths(property.Value, propertyPath))
                    {
                        yield return descendant;
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var descendant in DescendantPropertiesWithPaths(item, path))
                    {
                        yield return descendant;
                    }
                }

                break;
        }
    }
}

/// <summary>
///     ASP.NET Core pipeline and cookie misconfiguration read from code: the developer exception
///     page outside an environment check, a web application with no HTTPS redirection or HSTS,
///     cookies set insecure, forwarded-header trust opened to every proxy, and request body size
///     limits removed. Each walk is gated on the file text and reads syntax; a member whose symbol
///     resolves must belong to a cookie or forwarded-headers type, so a same-named property of an
///     unrelated type is not reported.
/// </summary>
public static class MiddlewareSecurityAnalyzer
{
    public static void Assess(FrameworkContext context, IReadOnlyList<ApiEndpoint> endpoints, List<SecurityFinding> findings, SecurityAnalyzer.FindingIds ids)
    {
        if (context.CSharp is null)
        {
            return;
        }

        var pipelineFiles = new List<(string FileName, int Line)>();
        var hasHttpsRedirection = false;
        var hasHsts = false;
        foreach (var tree in context.CSharpTrees)
        {
            var fileName = Path.GetFileName(tree.FilePath);
            var model = context.CSharp.GetSemanticModel(tree);
            foreach (var invocation in context.InvocationsNamed(tree, name => name is "UseDeveloperExceptionPage" or "UseHttpsRedirection" or "UseHsts" or "CreateBuilder" or "CreateSlimBuilder" or "CreateEmptyBuilder" or "Clear"))
            {
                var name = ProviderHelpers.InvocationName(invocation);
                switch (name)
                {
                    case "UseHttpsRedirection":
                        hasHttpsRedirection = true;
                        break;
                    case "UseHsts":
                        hasHsts = true;
                        break;
                    case "CreateBuilder" or "CreateSlimBuilder" or "CreateEmptyBuilder" when invocation.Expression is MemberAccessExpressionSyntax { Expression: var receiver } && receiver.ToString().EndsWith("WebApplication", StringComparison.Ordinal):
                        pipelineFiles.Add((fileName, Line(invocation)));
                        break;
                    case "UseDeveloperExceptionPage" when !IsInsideDevelopmentCheck(invocation):
                        findings.Add(Finding(ids, "DeveloperExceptionPageUnguarded", "Developer exception page is enabled outside a development-environment check", "medium", "CWE-209", fileName, Line(invocation),
                            "UseDeveloperExceptionPage() runs in every environment and returns stack traces, source lines and request details to clients.",
                            "Call UseDeveloperExceptionPage() only inside `if (app.Environment.IsDevelopment())` and use UseExceptionHandler() elsewhere."));
                        break;
                    case "Clear" when invocation.Expression is MemberAccessExpressionSyntax { Expression: var cleared } && LastName(cleared) is "KnownProxies" or "KnownNetworks" or "KnownIPNetworks" &&
                                      IsMemberOf(model, cleared, "ForwardedHeadersOptions"):
                        findings.Add(Finding(ids, "ForwardedHeadersTrustAll", $"Forwarded headers are accepted from any proxy ({LastName(cleared)} cleared)", "medium", "CWE-348", fileName, Line(invocation),
                            $"{LastName(cleared)}.Clear() removes the loopback default, so X-Forwarded-For/Proto from any client rewrites the remote address and scheme.",
                            "List the real reverse proxies in KnownProxies/KnownNetworks instead of clearing them."));
                        break;
                }
            }

            // Pipeline shape of the Startup pattern: Configure(IApplicationBuilder app, ...).
            if (context.TextContainsAny(tree, "IApplicationBuilder"))
            {
                foreach (var method in FrameworkContext.Declarations<MethodDeclarationSyntax>(tree))
                {
                    if (method.Identifier.Text == "Configure" && method.ParameterList.Parameters.Any(parameter => parameter.Type?.ToString() is "IApplicationBuilder" or "Microsoft.AspNetCore.Builder.IApplicationBuilder"))
                    {
                        pipelineFiles.Add((fileName, Line(method)));
                    }
                }
            }

            if (context.TextContainsAny(tree, "Secure", "HttpOnly", "SameSite", "MaxRequestBodySize"))
            {
                AssessAssignments(tree, model, fileName, findings, ids);
            }

            if (context.TextContainsAny(tree, "DisableRequestSizeLimit", "RequestSizeLimit"))
            {
                AssessSizeLimitAttributes(tree, fileName, endpoints, findings, ids);
            }
        }

        // A web application with no HTTPS redirection or HSTS anywhere in its code.
        if (pipelineFiles.Count > 0)
        {
            var (file, line) = pipelineFiles.OrderBy(entry => entry.FileName, StringComparer.Ordinal).ThenBy(entry => entry.Line).First();
            if (!hasHttpsRedirection)
            {
                findings.Add(Finding(ids, "HttpsRedirectionMissing", "Web application does not redirect HTTP to HTTPS", "low", "CWE-319", file, line,
                    "No UseHttpsRedirection() call was found in the application's request pipeline.",
                    "Call app.UseHttpsRedirection() (or redirect at the reverse proxy and record that decision)."));
            }

            if (!hasHsts)
            {
                findings.Add(Finding(ids, "HstsMissing", "Web application does not send Strict-Transport-Security", "low", "CWE-319", file, line,
                    "No UseHsts() call was found in the application's request pipeline.",
                    "Call app.UseHsts() outside development (or send the header at the reverse proxy)."));
            }
        }
    }

    /// <summary>Insecure cookie settings and removed body size limits, from assignments and object initializers.</summary>
    private static void AssessAssignments(SyntaxTree tree, SemanticModel model, string fileName, List<SecurityFinding> findings, SecurityAnalyzer.FindingIds ids)
    {
        foreach (var assignment in tree.GetRoot().DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            var member = LastName(assignment.Left);
            var value = assignment.Right.ToString().Replace(" ", string.Empty, StringComparison.Ordinal);
            switch (member)
            {
                case "MaxRequestBodySize" when value == "null":
                    findings.Add(Finding(ids, "RequestSizeLimitDisabled", "Request body size limit is removed (MaxRequestBodySize = null)", "medium", "CWE-770", fileName, Line(assignment),
                        "A null MaxRequestBodySize lets a client stream an unbounded request body into the server.",
                        "Keep a finite MaxRequestBodySize, raising it only on the endpoints that need it ([RequestSizeLimit(bytes)])."));
                    continue;
                case "SecurePolicy" or "Secure" when (value.EndsWith("CookieSecurePolicy.None", StringComparison.Ordinal) || (member == "Secure" && value == "false")) && IsCookieMember(model, assignment):
                    findings.Add(Finding(ids, "InsecureCookie", "Cookie is not restricted to HTTPS", "medium", "CWE-614", fileName, Line(assignment),
                        $"{member} = {assignment.Right} lets the cookie travel over plain HTTP.",
                        "Use CookieSecurePolicy.Always (or Secure = true).", ("setting", member)));
                    continue;
                case "HttpOnly" when (value == "false" || value.EndsWith("HttpOnlyPolicy.None", StringComparison.Ordinal)) && IsCookieMember(model, assignment):
                    findings.Add(Finding(ids, "InsecureCookie", "Cookie is readable from script (HttpOnly disabled)", "medium", "CWE-1004", fileName, Line(assignment),
                        $"HttpOnly = {assignment.Right} exposes the cookie to any script running in the page.",
                        "Keep HttpOnly = true (HttpOnlyPolicy.Always) for every cookie that carries session or auth state.", ("setting", member)));
                    continue;
                case "SameSite" or "MinimumSameSitePolicy" when value.EndsWith("SameSiteMode.None", StringComparison.Ordinal) && IsCookieMember(model, assignment):
                    findings.Add(Finding(ids, "InsecureCookie", "Cookie is sent on cross-site requests (SameSite=None)", "low", "CWE-1275", fileName, Line(assignment),
                        $"{member} = {assignment.Right} sends the cookie with cross-site requests, which re-enables CSRF for cookie-authenticated endpoints.",
                        "Use SameSiteMode.Lax or Strict unless a cross-site flow requires None, and then require antiforgery.", ("setting", member)));
                    continue;
            }
        }
    }

    /// <summary><c>[DisableRequestSizeLimit]</c> and a <c>[RequestSizeLimit]</c> of the type's maximum.</summary>
    private static void AssessSizeLimitAttributes(SyntaxTree tree, string fileName, IReadOnlyList<ApiEndpoint> endpoints, List<SecurityFinding> findings, SecurityAnalyzer.FindingIds ids)
    {
        foreach (var attribute in tree.GetRoot().DescendantNodes().OfType<AttributeSyntax>())
        {
            var name = ProviderHelpers.NormalizeAttributeName(attribute);
            var unlimited = name == "DisableRequestSizeLimit" ||
                            name == "RequestSizeLimit" && attribute.ArgumentList?.Arguments.FirstOrDefault()?.Expression.ToString().Replace(" ", string.Empty, StringComparison.Ordinal) is "long.MaxValue" or "Int64.MaxValue" or "System.Int64.MaxValue" or "int.MaxValue" or "Int32.MaxValue";
            if (!unlimited)
            {
                continue;
            }

            var method = attribute.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
            var route = method is null
                ? null
                : endpoints.FirstOrDefault(endpoint => string.Equals(Path.GetFileName(endpoint.FilePath), fileName, StringComparison.OrdinalIgnoreCase) && endpoint.MethodName == method.Identifier.Text)?.Path;
            var finding = Finding(ids, "RequestSizeLimitDisabled", $"Request body size limit is removed ([{attribute.Name}])", "medium", "CWE-770", fileName, Line(attribute),
                $"[{attribute}] lets a client send an unbounded request body to this endpoint.",
                "Use [RequestSizeLimit(bytes)] with the largest body the endpoint actually accepts.");
            finding.Route = route;
            findings.Add(finding);
        }
    }

    /// <summary>
    ///     Whether the call sits in the development branch of an environment check
    ///     (<c>if (env.IsDevelopment())</c>, the <c>else</c> of <c>if (!env.IsDevelopment())</c>,
    ///     <c>IsEnvironment("Development")</c>), or in a method or local function whose name says it
    ///     configures development.
    /// </summary>
    private static bool IsInsideDevelopmentCheck(SyntaxNode node)
    {
        SyntaxNode child = node;
        foreach (var ancestor in node.Ancestors())
        {
            if (ancestor is IfStatementSyntax ifStatement)
            {
                var condition = ifStatement.Condition.ToString().Replace(" ", string.Empty, StringComparison.Ordinal);
                var checksDevelopment = condition.Contains("IsDevelopment(", StringComparison.Ordinal) || condition.Contains("IsEnvironment(", StringComparison.Ordinal) && condition.Contains("Development", StringComparison.Ordinal) ||
                                        condition.Contains("IsDevelopment", StringComparison.Ordinal) || condition.Contains("\"Development\"", StringComparison.Ordinal);
                if (checksDevelopment)
                {
                    var negated = condition.StartsWith('!') || condition.Contains("==false", StringComparison.Ordinal);
                    var inThen = ifStatement.Statement == child;
                    if (inThen != negated)
                    {
                        return true;
                    }
                }
            }

            if (ancestor is ConditionalExpressionSyntax conditional && conditional.Condition.ToString().Contains("IsDevelopment", StringComparison.Ordinal) && conditional.WhenTrue == child)
            {
                return true;
            }

            if (ancestor is MethodDeclarationSyntax method && method.Identifier.Text.Contains("Development", StringComparison.Ordinal) ||
                ancestor is LocalFunctionStatementSyntax local && local.Identifier.Text.Contains("Development", StringComparison.Ordinal))
            {
                return true;
            }

            child = ancestor;
        }

        return false;
    }

    /// <summary>The assigned member belongs to a cookie type (CookieOptions, CookieBuilder, CookiePolicyOptions, ...); syntax decides when the symbol does not resolve.</summary>
    private static bool IsCookieMember(SemanticModel model, AssignmentExpressionSyntax assignment)
    {
        if (model.GetSymbolInfo(assignment.Left).Symbol is { ContainingType: { } type })
        {
            return type.Name.Contains("Cookie", StringComparison.Ordinal);
        }

        // Unresolved: the receiver (`options.Cookie.HttpOnly`) or the initialized object (`new CookieOptions { ... }`) names a cookie.
        if (assignment.Left is MemberAccessExpressionSyntax access && access.Expression.ToString().Contains("Cookie", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return assignment.Parent is InitializerExpressionSyntax { Parent: ObjectCreationExpressionSyntax creation } && creation.Type.ToString().Contains("Cookie", StringComparison.Ordinal);
    }

    private static bool IsMemberOf(SemanticModel model, ExpressionSyntax expression, string typeName) =>
        model.GetSymbolInfo(expression).Symbol is { ContainingType: { } type } ? type.Name == typeName : true;

    private static string? LastName(ExpressionSyntax expression) => expression switch
    {
        MemberAccessExpressionSyntax access => access.Name.Identifier.Text,
        IdentifierNameSyntax identifier => identifier.Identifier.Text,
        _ => null
    };

    private static int Line(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private static SecurityFinding Finding(SecurityAnalyzer.FindingIds ids, string kind, string title, string severity, string cwe, string fileName, int line, string evidence, string remediation, (string Key, string Value)? property = null)
    {
        var finding = new SecurityFinding
        {
            Id = ids.Next(kind, fileName, line),
            Kind = kind,
            Title = title,
            Severity = severity,
            Cwe = cwe,
            FileName = fileName,
            LineNumber = line,
            Evidence = evidence,
            Remediation = remediation,
            Confidence = ConfidenceTiers.Syntactic
        };
        if (property is { } extra)
        {
            finding.Properties[extra.Key] = extra.Value;
        }

        return finding;
    }
}
