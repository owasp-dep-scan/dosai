namespace Depscan.Frameworks;

/// <summary>Result of one full framework analysis run: everything providers contributed plus detections.</summary>
public sealed class FrameworkAnalysisResult
{
    public List<ServiceComponent> Services { get; } = [];

    public List<ApiEndpoint> ApiEndpoints { get; } = [];

    public List<AiComponent> AiComponents { get; } = [];

    public List<EntryPoint> EntryPoints { get; } = [];

    public List<FrameworkTaintSeed> TaintSeeds { get; } = [];

    public List<DetectedFramework> Frameworks { get; } = [];

    public List<FrameworkDiagnostic> Diagnostics { get; } = [];
}

/// <summary>Options for framework analysis; defaults match the documented CLI defaults.</summary>
public sealed class FrameworkAnalysisOptions
{
    public bool ClassifyData { get; init; } = true;

    public int MaxConventionalRoutes { get; init; } = 500;

    /// <summary>IncludePromptText: emit full prompt text instead of the redacted default.</summary>
    public bool IncludePromptText { get; init; }
}

/// <summary>
///     Ordered provider list and dispatch. Providers are cheap-gated via AppliesTo and each Analyze
///     call is isolated: a throwing provider records a diagnostic and never takes down the run.
/// </summary>
public static class FrameworkRegistry
{
    /// <summary>Delivery order matters: web providers first, RPC, then messaging, then AI tiers.</summary>
    private static readonly IFrameworkProvider[] Providers = CreateProviders();

    private static IFrameworkProvider[] CreateProviders() =>
    [
        // Tier 1 — Web/HTTP. Minimal APIs first: later providers consume their mount points.
        new Providers.AspNetCoreMvcProvider(),
        new Providers.MinimalApiProvider(),
        new Providers.LegacyDotNetWebProvider(),
        new Providers.RazorBlazorProvider(),
        new Providers.CommunityHttpProvider(),
        // Tier 2 — RPC & serialization. Protobuf parses .proto contracts before gRPC joins them.
        new Providers.ProtobufProvider(),
        new Providers.GrpcProvider(),
        new Providers.SignalRProvider(),
        new Providers.GraphQLODataProvider(),
        // Tier 3 — Serverless, messaging, jobs
        new Providers.ServerlessProvider(),
        new Providers.MessagingProvider(),
        new Providers.BackgroundJobProvider(),
        // Tier 4 — AI, agents, MCP
        new Providers.McpProvider(),
        new Providers.LlmSdkProvider(),
        new Providers.MlRuntimeProvider(),
        new Providers.VectorStoreProvider()
    ];

    public static IReadOnlyList<IFrameworkProvider> AllProviders => Providers;

    public static FrameworkAnalysisResult Analyze(FrameworkContext ctx, FrameworkAnalysisOptions? options = null)
    {
        ctx.ClassifyData = options?.ClassifyData ?? true;
        ctx.MaxConventionalRoutes = options?.MaxConventionalRoutes ?? 500;
        ctx.IncludePromptText = options?.IncludePromptText ?? false;
        var result = new FrameworkAnalysisResult();
        var providerResults = new FrameworkResults();
        foreach (var framework in ctx.Detection.Frameworks)
        {
            result.Frameworks.Add(framework);
        }

        foreach (var provider in Providers)
        {
            try
            {
                if (!provider.AppliesTo(ctx))
                {
                    continue;
                }

                provider.Analyze(ctx, providerResults);
            }
            catch (Exception ex)
            {
                ctx.Diagnostics.Add(new FrameworkDiagnostic(provider.Id, $"Provider failed and was skipped: {ex.Message}"));
            }
        }

        foreach (var diagnostic in ctx.Diagnostics)
        {
            result.Diagnostics.Add(diagnostic);
        }

        result.Services.AddRange(providerResults.Services);
        result.ApiEndpoints.AddRange(providerResults.ApiEndpoints);
        result.AiComponents.AddRange(providerResults.AiComponents);
        result.EntryPoints.AddRange(providerResults.EntryPoints);
        result.TaintSeeds.AddRange(providerResults.TaintSeeds);
        LinkEntryPoints(result);
        ApplyDefaultTrustZones(result, ctx);
        Sort(result);
        return result;
    }

    /// <summary>
    ///     Derives <see cref="ServiceComponent.EntryPointIds" /> from the entry points that were actually
    ///     emitted, for every provider uniformly.
    /// </summary>
    /// <remarks>
    ///     Providers previously each remembered to append <c>ep:{operationId}</c> themselves, and only
    ///     three of the sixteen did — so a consumer could not distinguish "this service has no entry
    ///     points" from "this provider forgot to record them". Deriving the links here from the emitted
    ///     entry-point set also makes the referential-integrity guarantee structural: an id is only ever
    ///     written if the entry point it names exists.
    /// </remarks>
    private static void LinkEntryPoints(FrameworkAnalysisResult result)
    {
        var entryPointIds = result.EntryPoints.Select(entryPoint => entryPoint.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var service in result.Services)
        {
            var linked = service.Operations
                .Select(operation => $"ep:{operation.Id}")
                .Where(entryPointIds.Contains);
            foreach (var id in linked)
            {
                if (!service.EntryPointIds.Contains(id, StringComparer.Ordinal))
                {
                    service.EntryPointIds.Add(id);
                }
            }
        }
    }

    /// <summary>
    ///     Settles the trust zone of inbound services that no provider classified.
    /// </summary>
    /// <remarks>
    ///     An inbound HTTP service carrying no authorization metadata is anonymous — that is precisely
    ///     what "no <c>[Authorize]</c>" means — unless the application authorizes globally, which
    ///     <see cref="FrameworkContext.HasGlobalAuthorizationFallback" /> detects. Leaving these at
    ///     <see cref="TrustZones.Unknown" /> (the previous behaviour for the eleven providers that never
    ///     set a zone, and for every unauthenticated MVC controller) left them out of the
    ///     <see cref="ApplyTrustBoundaries" /> sweep, which only walks public inbound services — so the
    ///     boundary-crossing analysis silently had almost nothing to work on.
    /// </remarks>
    private static void ApplyDefaultTrustZones(FrameworkAnalysisResult result, FrameworkContext ctx)
    {
        foreach (var service in result.Services)
        {
            if (service.Direction != ServiceDirections.Inbound || service.TrustZone != TrustZones.Unknown)
            {
                continue;
            }

            var authenticated = service.Authenticated == true ||
                                service.AuthorizationPolicies.Count > 0 ||
                                service.Roles.Count > 0 ||
                                service.AuthenticationSchemes.Count > 0;
            service.TrustZone = authenticated
                ? TrustZones.Authenticated
                : ctx.HasGlobalAuthorizationFallback
                    ? TrustZones.Unknown
                    : TrustZones.Public;
        }
    }

    /// <summary>
    ///     Computes trust zones from the call graph: an inbound public service that can reach an
    ///     outbound external service's methods crosses a trust boundary. Never guessed — only set
    ///     when a positive call-graph path exists. Outbound services with non-loopback endpoints
    ///     are marked external.
    /// </summary>
    public static void ApplyTrustBoundaries(FrameworkAnalysisResult result, CallGraph callGraph)
    {
        var outgoingBySource = callGraph.Edges
            .GroupBy(edge => edge.SourceId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(edge => edge.TargetId).ToList(), StringComparer.Ordinal);

        foreach (var outbound in result.Services.Where(service => service.Direction == ServiceDirections.Outbound))
        {
            // An outbound service with no known endpoint is not evidence of an external call — it is
            // absence of evidence, and calling it External was exactly the guess this analysis is
            // meant to avoid, since the External set feeds the boundary-crossing sweep below.
            outbound.TrustZone = outbound.Endpoints.Count == 0
                ? TrustZones.Unknown
                : outbound.Endpoints.Any(endpoint => !IsLoopback(endpoint))
                    ? TrustZones.External
                    : TrustZones.Internal;
        }

        // "Where does it go" and "does it leave the process" are separate claims. An outbound service
        // with no resolved address keeps TrustZone Unknown — we genuinely do not know the destination —
        // but it still egresses, so it still counts for boundary crossing. Only a confirmed loopback
        // destination (Internal) is excluded.
        var outboundMethodIds = result.Services
            .Where(service => service.Direction == ServiceDirections.Outbound && service.TrustZone != TrustZones.Internal)
            .SelectMany(service => service.MethodIds)
            .ToHashSet(StringComparer.Ordinal);
        if (outboundMethodIds.Count == 0)
        {
            return;
        }

        foreach (var inbound in result.Services.Where(service => service.Direction == ServiceDirections.Inbound && service.TrustZone == TrustZones.Public))
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var frontier = new Queue<string>(inbound.MethodIds);
            while (frontier.Count > 0 && visited.Count < 10000)
            {
                var current = frontier.Dequeue();
                if (!visited.Add(current))
                {
                    continue;
                }

                if (outboundMethodIds.Contains(current))
                {
                    inbound.CrossesTrustBoundary = true;
                    break;
                }

                if (outgoingBySource.TryGetValue(current, out var targets))
                {
                    foreach (var target in targets)
                    {
                        frontier.Enqueue(target);
                    }
                }
            }
        }
    }

    /// <summary>
    ///     Loopback detection compares the parsed host exactly. A substring test would classify
    ///     <c>https://api.localhost.evil.com</c> or <c>https://evil.com/?next=localhost</c> as
    ///     internal, excluding a genuinely external egress from the trust-boundary sweep.
    /// </summary>
    internal static bool IsLoopback(string endpoint)
    {
        // Non-URL endpoints: internal queue/topic mounts (e.g. "/queue/orders") are in-process.
        if (endpoint.StartsWith("/queue/", StringComparison.Ordinal))
        {
            return true;
        }

        if (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            return IsLoopbackHost(uri.Host);
        }

        // Scheme-less "host:port" forms and bare host names.
        var candidate = endpoint;
        var schemeSeparator = candidate.IndexOf("://", StringComparison.Ordinal);
        if (schemeSeparator >= 0)
        {
            candidate = candidate[(schemeSeparator + 3)..];
        }

        var authority = candidate.Split('/')[0];
        var host = authority.Split(':')[0];
        return IsLoopbackHost(host);
    }

    private static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("127.0.0.1", StringComparison.Ordinal) ||
        host.Equals("::1", StringComparison.Ordinal) ||
        host.Equals("[::1]", StringComparison.Ordinal);

    private static void Sort(FrameworkAnalysisResult result)
    {
        result.Services.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        result.ApiEndpoints.Sort((a, b) => string.CompareOrdinal(a.FileName, b.FileName) is { } fileCompare && fileCompare != 0
            ? fileCompare
            : a.LineNumber != b.LineNumber
                ? a.LineNumber.CompareTo(b.LineNumber)
                : string.CompareOrdinal(a.Route, b.Route));
        result.AiComponents.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
    }
}
