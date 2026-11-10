# Framework semantics

Dosai detects .NET frameworks through a provider model (`Dosai/Frameworks/`) instead of name
matching. Every detection carries a confidence tier, every inbound surface is emitted as a
`ServiceComponent` with resolved paths, and entry-point parameters are wired into taint analysis
as sources. Introduced in schema 4.0.0 and extended in schema 4.1.0, where the same metadata also
feeds severity-tagged security findings.

A framework that is not in this table does not count as shipped.

## Confidence tiers

| Tier     | Meaning                                              | Typical evidence                            |
| -------- | ---------------------------------------------------- | ------------------------------------------- |
| `high`   | Symbol resolved against a real base type / interface | `ControllerBase` derivation, `IConsumer<T>` |
| `medium` | Attribute or name match without symbol resolution    | `*Controller` suffix rule, `[Route]`        |
| `low`    | Textual or config-file inference                     | `@page` directives, host.json, `.proto`     |

Providers never silently promote a heuristic match to high confidence. Consumers can filter on
`Confidence` at every level (service, operation, endpoint).

## Output surface

| Field                              | Content                                                                                                                                                                       |
| ---------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `MethodsSlice.Services[]`          | First-class service inventory (inbound surfaces and outbound dependencies)                                                                                                    |
| `MethodsSlice.AiComponents[]`      | Models, MCP tools with JSON Schemas, prompts, agents, embeddings                                                                                                              |
| `MethodsSlice.Frameworks[]`        | Detected frameworks with version/purl/confidence                                                                                                                              |
| `MethodsSlice.SecurityFindings[]`  | Security findings derived from provider metadata (schema 4.1.0): endpoint security, MCP transport integrity, configuration security, each with severity, CWE, and remediation |
| `MethodsSlice.Reachability[]`      | Per-node reachability facts over the merged call graph (schema 4.1.0)                                                                                                         |
| `MethodsSlice.RecursionClusters[]` | Strongly-connected recursion clusters (schema 4.1.0)                                                                                                                          |
| `MethodsSlice.DeadCode[]`          | Source-declared methods no entry point reaches and no reflection/DI evidence keeps alive (schema 4.1.0)                                                                       |
| `ApiEndpoint.Path`                 | Resolved route path (leading `/`, tokens substituted), what CycloneDX consumes                                                                                                |
| `ApiEndpoint.Route`                | Verbatim route template, preserved for humans and diffing                                                                                                                     |
| `ApiEndpoint.RouteParameters[]`    | Parameters with constraints, defaults, optionality, binding source                                                                                                            |
| `ApiEndpoint.RawUrls[]`            | File-scope absolute URLs (heuristic evidence; renamed from `Urls`)                                                                                                            |

Stable bom-refs: `svc:<framework>:<group>/<name>`, `op:<serviceId>#<verb>:<path>`,
`ai:<kind>:<provider>/<name>`. Ids never contain absolute paths, lines, or timestamps, so output
is byte-reproducible across runs and machines; when the same namespace + class name appears in
several projects, the service group carries the relative source directory to keep ids unique.
See the [migration to 4.0.0](./migration-4.0.md) and [migration to 4.1.0](./migration-4.1.0.md)
guides for every output-visible change. Rows marked "(finding)" in the catalog below also produce
entries in `SecurityFindings[]` with severity, CWE, and remediation text.

Segment-versioned attribute routes (`v{version:apiVersion}/[controller]`) have a statically known
value domain: one concrete endpoint is emitted per declared `[ApiVersion]` (with `[MapToApiVersion]`
pinning an action to specific versions), e.g. `/v1.0/Orders` and `/v2.0/Orders`. ASP.NET parses a
missing minor as 0, so the equivalent compact spelling (`/v1/Orders`) is captured in
`ServiceComponent.Endpoints` alongside the declared one. The verbatim template always stays on
`ApiEndpoint.Route`.

## Performance expectations

Framework analysis adds work proportional to the number of routed surfaces: keyword-gated
provider passes, taint seeding, and four extra default pattern packs. Measured on real repos
(eShop, grpc-dotnet): **0-8% on `methods`, 5-12% on `dataflows`** for framework-heavy code
(the worst case is a gRPC/minimal-API-dense tree), and effectively zero on code without routed
surfaces. Mount registrations are resolved from the compiled syntax trees, never by re-reading
files from disk, and model artifacts over 256 MB skip hashing (see THREAT_MODEL.md).

## Supported frameworks

### Tier 1, Web/HTTP

| Framework (provider id)                                     | Detected                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                | Service kind / entry kind                   | Confidence      |
| ----------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------- | --------------- |
| ASP.NET Core MVC / Web API (`aspnetcore-mvc`)               | `ControllerBase`/`Controller` derivation (high) or the `*Controller` name-suffix rule (medium), `[Route]` on class (all templates), the 7 verb attributes, `[AcceptVerbs]`, `[ActionName]`, `[Area]`, `[NonController]`/`[NonAction]`, `[Consumes]`/`[Produces]`, `[ApiVersion]`/`[MapToApiVersion]`, `[FromBody \| Query \| Route \| Header \| Form]` binding sources, conventional routing (`MapControllerRoute`/`MapRoute`, expansion capped by `--max-conventional-routes`, default 500, medium)                                                                                                                                                                                                                                                    | `http` / `HttpController`                   | high/medium     |
| Minimal APIs (`minimal-api`)                                | `MapGet/Post/Put/Delete/Patch`, `Map` (any verb; middleware branches and mapper calls excluded), `MapMethods` (verb array read), `MapFallback`, `MapHealthChecks`, `MapIdentityApi` (its ten endpoints, `/manage/*` authenticated), `MapOpenApi`, `MapStaticAssets` (mount), nested `MapGroup` prefixes (variable, chained, fluent), fluent metadata (`RequireAuthorization`, `AllowAnonymous`, `RequireCors`, `DisableAntiforgery`) via an invocation-by-invocation chain walk, group conventions (in the group's chain or a later `group.RequireAuthorization();`) inherited by nested groups and endpoints, handler attributes (`[Authorize] () => …`, a method-group handler's attributes); method-group handlers name the entry point's `MethodId` | `http` / `HttpMinimalApi`                   | medium          |
| Web API 2 / MVC 5 / WCF / ASMX (`legacy-web`, `legacy-wcf`) | `ApiController` + `[RoutePrefix]`, action-name verb conventions, `[ServiceContract]`/`[OperationContract]` + `[WebGet]`/`[WebInvoke]`, `system.serviceModel` config endpoints with binding/security (basicHttpBinding without transport security is tagged), `[WebMethod]`                                                                                                                                                                                                                                                                                                                                                                                                                                                                              | `soap`/`http` / `Soap`, `HttpController`    | high/medium/low |
| Razor Pages / Blazor (`razor-blazor`)                       | `.cshtml`/`.razor` `@page` directives, `On{Verb}` handlers incl. named handlers, `@attribute [Authorize]`, Blazor `[Route]` components, interactive render modes (SignalR circuit dependency)                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                           | `http` / `HttpRazorPage`, `BlazorComponent` | low             |
| Community HTTP (`community-http`)                           | FastEndpoints (`Endpoint<TReq,TRes>` + `Configure()`), ServiceStack `[Route]` DTOs, Nancy module registrations, Carter modules                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                          | `http` / `HttpController`                   | medium          |

### Tier 2, RPC & serialization

| Framework (provider id)       | Detected                                                                                                                                                                                                                                                                                                                                                                     | Service kind / entry kind  | Confidence  |
| ----------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | -------------------------- | ----------- |
| gRPC (`grpc`)                 | Classes deriving protoc `*Base` types joined with `.proto` contracts for `/package.Service/Method` paths, `MapGrpcService` mounts, streaming modes, server reflection (finding), JSON transcoding, gRPC-Web, clients (`AddGrpcClient`, `GrpcChannel.ForAddress`)                                                                                                             | `grpc` / `Grpc`            | high/medium |
| Protocol Buffers (`protobuf`) | `.proto` files parsed directly (service/rpc/message, `google.api.http` annotations, streaming) with a brace-balancing scanner; csproj `<Protobuf GrpcServices=...>` metadata; protobuf-net/MessagePack via detection                                                                                                                                                         | `grpc` (from IDL)          | low         |
| SignalR (`signalr`)           | `Hub`/`Hub<T>` subclasses, `[HubMethodName]`, hub-method auth, `MapHub` mount association, `HubConnectionBuilder.WithUrl` clients (outbound), `IHubContext<T>` usage                                                                                                                                                                                                         | `websocket` / `SignalRHub` | high/medium |
| Orleans (`orleans`)           | Grain classes (`Grain` derivation or `IGrain`/`IAddressable` implementation), `[Reentrant]`/`[Alias]`, public grain methods as `GrainMethod` entry points with `rpc-message` taint seeds, `IGrainFactory.GetGrain<T>` call sites (outbound), `UseOrleans`/`AddApplicationParts` host registration; detected from `Microsoft.Orleans.*` package purls or `Orleans` namespaces | `rpc` / `GrainMethod`      | high/medium |

### Tier 3, Serverless, messaging, jobs

| Framework (provider id)             | Detected                                                                                                                                                                                                                                                                          | Service kind / entry kind                     | Confidence  |
| ----------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | --------------------------------------------- | ----------- |
| Azure Functions (`azure-functions`) | Both models (`[FunctionName]`, `[Function]`), all triggers (Http with `AuthorizationLevel` as a security fact, Timer with cron, Queue, Blob, Service Bus, Event Hubs/Grid, Cosmos DB, Kafka, Durable), output bindings as egress tags, `host.json` `routePrefix`, `function.json` | `function`/`scheduled` / `AzureFunction`      | high        |
| AWS Lambda (`aws-lambda`)           | Annotations (`[LambdaFunction]`, `[RestApi]`, `[HttpApi]`), classic `(TIn, ILambdaContext)` handler signatures, event types, serverless templates                                                                                                                                 | `function` / `LambdaFunction`                 | high/medium |
| Messaging (`messaging`)             | MassTransit `IConsumer<T>`, NServiceBus `IHandleMessages<T>`, MediatR `IRequestHandler<,>`/`INotificationHandler<T>`, Rebus, Dapr, publishers (`Publish`/`Send`/`InvokeMethodAsync`), raw Kafka/RabbitMQ/Service Bus clients with entity names                                    | `queue`/`pubsub` / `MessageConsumer`          | high/medium |
| Background jobs (`background-jobs`) | `IHostedService`/`BackgroundService` with `AddHostedService<T>` registration detection, Hangfire `RecurringJob.AddOrUpdate` (cron + humanized schedule) and dashboard authorization (unauthenticated dashboard is a finding), Quartz `IJob`, Coravel `IInvocable`                 | `scheduled` / `ScheduledJob`, `HostedService` | high/medium |

### Tier 4, AI, agents, MCP

| Framework (provider id)        | Detected                                                                                                                                                                                                                                                                                                                                                                                                                                       | Service kind / entry kind                     | Confidence  |
| ------------------------------ | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | --------------------------------------------- | ----------- |
| Model Context Protocol (`mcp`) | Server: `[McpServerToolType]`/`[McpServerTool]` (including tools on ordinary classes), prompts, resources, per-tool JSON Schemas ([Description] carried, `CancellationToken`/`IProgress` excluded), transport (stdio/http), stateless mode, `WithToolsFromAssembly` breadth (finding), HTTP host-header restriction (finding). Client: `McpClientFactory` transports; `StdioClientTransport` command/arguments captured as a supply-chain fact | `mcp` / `McpTool`, `McpPrompt`, `McpResource` | high/medium |
| LLM SDKs (`llm`)               | Model identifiers in chat/embedding calls (provider inferred: openai/azure/anthropic/google/huggingface), inference endpoints as outbound services, tools exposed to models, agents, system prompts (redacted by default; `--include-prompt-text` for full text)                                                                                                                                                                               | `ai-inference` (outbound)                     | medium      |
| ML runtimes (`ml-runtime`)     | ML.NET trainers (task/architecture family), `InferenceSession`/`Model.Load` references, on-disk artifacts (`.onnx`, `.gguf`, `.safetensors`, `.pt`) hashed with SHA-256, HuggingFace repo ids                                                                                                                                                                                                                                                  | AI components only                            | medium/low  |
| Vector stores (`vector-store`) | Qdrant, Pinecone, Milvus, Weaviate, Chroma, pgvector, Azure AI Search, Elasticsearch, Redis vector clients with collection metadata                                                                                                                                                                                                                                                                                                            | `vector-store` (outbound)                     | medium      |

## Data classification and trust zones

`Services[].Data[]` classifies request/response types from their members (`pii`, `credential`,
`financial`, `health`; default `unknown`, never `public`). Every non-`unknown` classification
names the member that triggered it in `Description`. Disable with `--no-classify-data`.

C# union declarations (C# 15, .NET 11) classify through their case records: the classifier
expands a union into the payload records the compiler lowers per case, so a `credential` or
`pii` member on one case surfaces even though the union declares no members of its own. A
hand-written `[Union]` type expands the same way, with its cases read the way the compiler reads
them: the parameter types of its public single-parameter constructors, and the single-parameter
static `Create` methods of a nested `IUnionMembers` interface it implements (a union whose cases
are named only there has no case constructor at all). Types marked for JSON closed-type
polymorphism or union serialization (`JsonPolymorphicAttribute`, `JsonUnionAttribute`) are
treated as (de)serialization boundaries and their interface-typed members are followed.

System.Text.Json writes a union as its case value alone, with no tag naming the case. Reading
one back works only while each case has its own JSON shape: when two cases serialize alike (two
records that are both JSON objects), deserialization throws `JsonException` and asks for a type
classifier. Review the classifier such a type is given: it decides which case, and so which
members, untrusted JSON binds into, and one that guesses from loosely checked properties can
bind a request into a case the client was not meant to reach. A request body of a union type is
also a place where a default, empty union can enter (see the union switch findings in
[compiler engineering](compiler-engineering.md#weakness-candidate-model)).

Authorization metadata follows ASP.NET Core's rules. `[AllowAnonymous]` anywhere (method, class,
base class, group, handler) makes an endpoint anonymous, whatever roles or policies it also
carries, and its exposure is `anonymous-http`. A controller inherits the authorization
attributes of its source-declared base controllers. `[Authorize("Policy")]`'s positional argument
is a policy; `Roles` and `AuthenticationSchemes` go to their own lists. Application-wide
authorization - an authorization `FallbackPolicy`, `MapControllers().RequireAuthorization()`, a
global `AuthorizeFilter`, `MapRazorPages().RequireAuthorization()` - marks every covered endpoint
without metadata of its own as requiring authorization, with a diagnostic naming the source. A
state-changing endpoint that opts out of antiforgery (`[IgnoreAntiforgeryToken]`,
`DisableAntiforgery()`) without bearer-only authentication raises
`StateChangingEndpointAntiforgeryDisabled` (medium, CWE-352). Each SignalR hub takes the
`MapHub<THub>` paths that name it.

Pipeline and cookie configuration in code also raises findings: `DeveloperExceptionPageUnguarded`
(medium, CWE-209: `UseDeveloperExceptionPage()` outside an `IsDevelopment()`/`IsEnvironment`
branch), `HttpsRedirectionMissing` and `HstsMissing` (low, CWE-319: a web application, from
`WebApplication.CreateBuilder` or a `Configure(IApplicationBuilder)` method, with no
`UseHttpsRedirection()` / `UseHsts()` anywhere), `InsecureCookie` (`Secure = false` or
`CookieSecurePolicy.None`: medium, CWE-614; `HttpOnly = false` or `HttpOnlyPolicy.None`: medium,
CWE-1004; `SameSiteMode.None`: low, CWE-1275; `Properties.setting` names the member) on cookie
types, `ForwardedHeadersTrustAll` (medium, CWE-348: `KnownProxies`/`KnownNetworks`/`KnownIPNetworks`
cleared on `ForwardedHeadersOptions`), `ForwardedHeadersEnabled` (info, CWE-348:
`app.UseForwardedHeaders(...)` and a `services.Configure<ForwardedHeadersOptions>(...)` that
names flags, each at its own site, with `Properties.framework` and, when a literal or flags
expression names them, `Properties.headers` (`x-forwarded`, the header family in the vocabulary
chen and the cdxgen plugins share; ASP.NET Core never reads RFC 7239 `Forwarded`) and
`Properties.forwardedHeaders` (the flags named: `x-forwarded-for`, ... or `all`);
`ForwardedHeaders.None` gives nothing, and a `ForwardLimit = null` in the same configuration is
recorded as `Properties.forwardLimit` = `unbounded`) and `RequestSizeLimitDisabled` (medium,
CWE-770: `MaxRequestBodySize = null`, `[DisableRequestSizeLimit]`, `[RequestSizeLimit(long.MaxValue)]`,
with the endpoint's `Route` when the attribute is on an action). `CorsWildcardWithCredentials`
judges each `AddPolicy`/`AddDefaultPolicy` on its own builder and names that policy.

`ForwardedHeadersEnabled` is information, not a defect: it says the application consumes
`Forwarded`/`X-Forwarded-*` header values, which behind a reverse proxy that forwards them
unchanged lets the client choose the remote IP, host and scheme the application sees. The same
finding is raised for the configuration switch: `"ForwardedHeaders_Enabled": "true"` in an
appsettings file and `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` in a launchSettings.json
profile (both configure X-Forwarded-For and X-Forwarded-Proto and clear the loopback proxy
restriction; a `false` value gives nothing). Sites are matched through the semantic model, so an
application method named `UseForwardedHeaders` or a `Configure` of another options type is not
reported. The rewritten values themselves are tracked as `proxy-derived` data-flow sources (see
[security analysis](./security-analysis.md)).

Razor Pages pair each `.cshtml` with its PageModel (`@model`, else the `.cshtml.cs`
code-behind): `On{Verb}[Handler][Async]` methods are the endpoints, every `@page` directive is a
route (a relative template is appended to the conventional route), and authorization comes from
the PageModel's attributes and `@attribute [Authorize(...)]`. Handler parameters are taint seeds
and `[BindProperty]`/`[BindProperties]` properties are sources.

Assembly-only inputs get endpoints from IL (`Framework: "assembly-il"`): controller attributes,
conventional routes (`MapControllerRoute`/`MapAreaControllerRoute`/`MapRoute`/`MapDefaultControllerRoute`
templates expanded for unattributed actions), minimal-API `Map*` registrations, and compiled
Razor Pages (`RazorCompiledItem` with `RouteTemplate` metadata, 2.x `RazorPage`), with the IL call
graph's method ids. The endpoint security rules run on them from metadata (handler parameter and
request-type member names, authorization and antiforgery attributes, a global antiforgery filter).
`AllowAnonymousOverridesAuthorization` (low, CWE-862) flags `[AllowAnonymous]` on an endpoint that
also names roles or policies.

`TrustZone`: `public` (anonymous inbound), `authenticated`, `internal` (loopback/queue),
`external` (outbound to non-loopback hosts), `unknown`. `CrossesTrustBoundary` is computed from
the call graph, it is only set when a positive path exists from a public inbound service to an
external outbound service's methods, never guessed.

## Taint seeding

Framework entry points taint their bound parameters in data-flow analysis (binding sources:
`http-route`, `http-query`, `http-body`, `rpc-message`, `queue-message`, `websocket-message`,
`mcp-tool-arg`, `function-payload`; Orleans grain methods seed `rpc-message`). CLI entry points
do the same for `args`, including the compiler-synthesized `<Main>$` of top-level-statement
programs, which is seeded as a `cli` source (schema 4.1.0). A controller action taking a plain
`string id` produces a real source→sink slice. Weakness kinds include `PromptInjectionCandidate`
and `McpToolInjectionCandidate` (CWE-1427).

## Best-effort areas

- VB.NET endpoint extraction remains syntax-only (`ApiEndpointAnalyzer`); C# is fully
  provider-driven.
- Route templates that cannot be resolved (non-constant expressions) keep the verbatim template
  in `Route` and leave `Path` null at low confidence, never a garbled path.
- Razor/Blazor parsing is textual by design (the templates are not C# compilations).
- Calls inside `.razor` and `.cshtml` files come from the build: `methods` reads them from the
  assemblies under `bin/` that the tree builds from source and whose PDB (a PDB file, or an
  embedded one) names a template the tree still holds, so a tree that has not been built lists
  none of them. Markup, `@code`, `@functions` and `@if`/`@foreach` bodies give rows, with
  `EvidenceKind` `AssemblyIlDirect` (or `AssemblyIlGeneratedState` in an async `ExecuteAsync`),
  `Path` relative to the scan root, and the caller named after the generated member
  (`BuildRenderTree`, `ExecuteAsync` or the `@code` method). A call into a `.cs` member lands on
  that member's source node. The Razor compiler maps some template code to no line at all (an
  event handler bound by `@onclick`, a plain HTML attribute value, a component parameter; see
  dotnet/razor#11432 and dotnet/razor#13056): such a call is listed at the line of the mapped code
  before it when it calls the application or a package, and not at all when it calls the
  framework or ASP.NET Core, because the compiler's own generated plumbing (render-tree builder
  calls, tag-helper scopes) looks the same. In builds from the .NET SDK 8.0.426 most markup
  expressions are unmapped. Calls in the compiler's generated documents are not listed, nor are a
  code-behind's IL copies (a `.razor.cs` call is one source row), and Debug and Release builds of
  one template list each call once. A call from a `.cs` file into a member a template declares
  stays `SourceUnresolved`: the generated partial class is not source. A template the tree no
  longer holds (a stale build) or one outside the scanned directory adds nothing, and
  `Diagnostics` counts each, along with templates changed after their build.

## CLI

```bash
dotnet run --project ./Dosai/Dosai.csproj -- methods --path ./src --o methods.json \
  --no-classify-data \             # disable service.data classification
  --max-conventional-routes 250 \  # cap conventional route expansion
  --include-prompt-text            # emit full system prompt text (redacted by default)
```
