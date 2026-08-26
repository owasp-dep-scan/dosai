# Framework semantics

Dosai detects .NET frameworks through a provider model (`Dosai/Frameworks/`) instead of name
matching. Every detection carries a confidence tier, every inbound surface is emitted as a
`ServiceComponent` with resolved paths, and entry-point parameters are wired into taint analysis
as sources. Schema 4.0.0.

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

| Field                           | Content                                                                         |
| ------------------------------- | ------------------------------------------------------------------------------- |
| `MethodsSlice.Services[]`       | First-class service inventory (inbound surfaces and outbound dependencies)      |
| `MethodsSlice.AiComponents[]`   | Models, MCP tools with JSON Schemas, prompts, agents, embeddings                |
| `MethodsSlice.Frameworks[]`     | Detected frameworks with version/purl/confidence                                |
| `ApiEndpoint.Path`              | Resolved route path (leading `/`, tokens substituted) — what CycloneDX consumes |
| `ApiEndpoint.Route`             | Verbatim route template, preserved for humans and diffing                       |
| `ApiEndpoint.RouteParameters[]` | Parameters with constraints, defaults, optionality, binding source              |
| `ApiEndpoint.RawUrls[]`         | File-scope absolute URLs (heuristic evidence; renamed from `Urls`)              |

Stable bom-refs: `svc:<framework>:<group>/<name>`, `op:<serviceId>#<verb>:<path>`,
`ai:<kind>:<provider>/<name>`. Ids never contain absolute paths, lines, or timestamps, so output
is byte-reproducible across runs and machines; when the same namespace + class name appears in
several projects, the service group carries the relative source directory to keep ids unique.
See [migration to 4.0.0](./migration-4.0.md) for every output-visible change.

## Performance expectations

Framework analysis adds work proportional to the number of routed surfaces: keyword-gated
provider passes, taint seeding, and four extra default pattern packs. Measured on real repos
(eShop, grpc-dotnet): **0–8% on `methods`, 5–12% on `dataflows`** for framework-heavy code
(the worst case is a gRPC/minimal-API-dense tree), and effectively zero on code without routed
surfaces. Mount registrations are resolved from the compiled syntax trees, never by re-reading
files from disk, and model artifacts over 256 MB skip hashing (see THREAT_MODEL.md).

## Supported frameworks

### Tier 1 — Web/HTTP

| Framework (provider id)                                     | Detected                                                                                                                                                                                                                                                                                                         | Service kind / entry kind                   | Confidence      |
| ----------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------- | --------------- | ------ | ---------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------- | ----------- |
| ASP.NET Core MVC / Web API (`aspnetcore-mvc`)               | `ControllerBase`/`Controller` derivation (high) or the `*Controller` name-suffix rule (medium), `[Route]` on class (all templates), the 7 verb attributes, `[AcceptVerbs]`, `[ActionName]`, `[Area]`, `[NonController]`/`[NonAction]`, `[Consumes]`/`[Produces]`, `[ApiVersion]`/`[MapToApiVersion]`, `[FromBody | Query                                       | Route           | Header | Form]` binding sources, conventional routing (`MapControllerRoute`/`MapRoute`, expansion capped by `--max-conventional-routes`, default 500, medium) | `http` / `HttpController` | high/medium |
| Minimal APIs (`minimal-api`)                                | `MapGet/Post/Put/Delete/Patch`, `MapMethods` (verb array read), `MapFallback`, `MapHealthChecks`, nested `MapGroup` prefixes (variable, chained, fluent), fluent metadata (`RequireAuthorization`, `AllowAnonymous`, `RequireCors`, `DisableAntiforgery`) via an invocation-by-invocation chain walk             | `http` / `HttpMinimalApi`                   | medium          |
| Web API 2 / MVC 5 / WCF / ASMX (`legacy-web`, `legacy-wcf`) | `ApiController` + `[RoutePrefix]`, action-name verb conventions, `[ServiceContract]`/`[OperationContract]` + `[WebGet]`/`[WebInvoke]`, `system.serviceModel` config endpoints with binding/security (basicHttpBinding without transport security is tagged), `[WebMethod]`                                       | `soap`/`http` / `Soap`, `HttpController`    | high/medium/low |
| Razor Pages / Blazor (`razor-blazor`)                       | `.cshtml`/`.razor` `@page` directives, `On{Verb}` handlers incl. named handlers, `@attribute [Authorize]`, Blazor `[Route]` components, interactive render modes (SignalR circuit dependency)                                                                                                                    | `http` / `HttpRazorPage`, `BlazorComponent` | low             |
| Community HTTP (`community-http`)                           | FastEndpoints (`Endpoint<TReq,TRes>` + `Configure()`), ServiceStack `[Route]` DTOs, Nancy module registrations, Carter modules                                                                                                                                                                                   | `http` / `HttpController`                   | medium          |

### Tier 2 — RPC & serialization

| Framework (provider id)       | Detected                                                                                                                                                                                                                                                         | Service kind / entry kind  | Confidence  |
| ----------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | -------------------------- | ----------- |
| gRPC (`grpc`)                 | Classes deriving protoc `*Base` types joined with `.proto` contracts for `/package.Service/Method` paths, `MapGrpcService` mounts, streaming modes, server reflection (finding), JSON transcoding, gRPC-Web, clients (`AddGrpcClient`, `GrpcChannel.ForAddress`) | `grpc` / `Grpc`            | high/medium |
| Protocol Buffers (`protobuf`) | `.proto` files parsed directly (service/rpc/message, `google.api.http` annotations, streaming) with a brace-balancing scanner; csproj `<Protobuf GrpcServices=...>` metadata; protobuf-net/MessagePack via detection                                             | `grpc` (from IDL)          | low         |
| SignalR (`signalr`)           | `Hub`/`Hub<T>` subclasses, `[HubMethodName]`, hub-method auth, `MapHub` mount association, `HubConnectionBuilder.WithUrl` clients (outbound), `IHubContext<T>` usage                                                                                             | `websocket` / `SignalRHub` | high/medium |

### Tier 3 — Serverless, messaging, jobs

| Framework (provider id)             | Detected                                                                                                                                                                                                                                                                          | Service kind / entry kind                     | Confidence  |
| ----------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | --------------------------------------------- | ----------- |
| Azure Functions (`azure-functions`) | Both models (`[FunctionName]`, `[Function]`), all triggers (Http with `AuthorizationLevel` as a security fact, Timer with cron, Queue, Blob, Service Bus, Event Hubs/Grid, Cosmos DB, Kafka, Durable), output bindings as egress tags, `host.json` `routePrefix`, `function.json` | `function`/`scheduled` / `AzureFunction`      | high        |
| AWS Lambda (`aws-lambda`)           | Annotations (`[LambdaFunction]`, `[RestApi]`, `[HttpApi]`), classic `(TIn, ILambdaContext)` handler signatures, event types, serverless templates                                                                                                                                 | `function` / `LambdaFunction`                 | high/medium |
| Messaging (`messaging`)             | MassTransit `IConsumer<T>`, NServiceBus `IHandleMessages<T>`, MediatR `IRequestHandler<,>`/`INotificationHandler<T>`, Rebus, Dapr, publishers (`Publish`/`Send`/`InvokeMethodAsync`), raw Kafka/RabbitMQ/Service Bus clients with entity names                                    | `queue`/`pubsub` / `MessageConsumer`          | high/medium |
| Background jobs (`background-jobs`) | `IHostedService`/`BackgroundService` with `AddHostedService<T>` registration detection, Hangfire `RecurringJob.AddOrUpdate` (cron + humanized schedule) and dashboard authorization (unauthenticated dashboard is a finding), Quartz `IJob`, Coravel `IInvocable`                 | `scheduled` / `ScheduledJob`, `HostedService` | high/medium |

### Tier 4 — AI, agents, MCP

| Framework (provider id)        | Detected                                                                                                                                                                                                                                                                                                                                                                                                                                       | Service kind / entry kind                     | Confidence  |
| ------------------------------ | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | --------------------------------------------- | ----------- |
| Model Context Protocol (`mcp`) | Server: `[McpServerToolType]`/`[McpServerTool]` (including tools on ordinary classes), prompts, resources, per-tool JSON Schemas ([Description] carried, `CancellationToken`/`IProgress` excluded), transport (stdio/http), stateless mode, `WithToolsFromAssembly` breadth (finding), HTTP host-header restriction (finding). Client: `McpClientFactory` transports; `StdioClientTransport` command/arguments captured as a supply-chain fact | `mcp` / `McpTool`, `McpPrompt`, `McpResource` | high/medium |
| LLM SDKs (`llm`)               | Model identifiers in chat/embedding calls (provider inferred: openai/azure/anthropic/google/huggingface), inference endpoints as outbound services, tools exposed to models, agents, system prompts (redacted by default; `--include-prompt-text` for full text)                                                                                                                                                                               | `ai-inference` / —                            | medium      |
| ML runtimes (`ml-runtime`)     | ML.NET trainers (task/architecture family), `InferenceSession`/`Model.Load` references, on-disk artifacts (`.onnx`, `.gguf`, `.safetensors`, `.pt`) hashed with SHA-256, HuggingFace repo ids                                                                                                                                                                                                                                                  | — (AI components)                             | medium/low  |
| Vector stores (`vector-store`) | Qdrant, Pinecone, Milvus, Weaviate, Chroma, pgvector, Azure AI Search, Elasticsearch, Redis vector clients with collection metadata                                                                                                                                                                                                                                                                                                            | `vector-store` (outbound)                     | medium      |

## Data classification and trust zones

`Services[].Data[]` classifies request/response types from their members (`pii`, `credential`,
`financial`, `health`; default `unknown`, never `public`). Every non-`unknown` classification
names the member that triggered it in `Description`. Disable with `--classify-data false`.

`TrustZone`: `public` (anonymous inbound), `authenticated`, `internal` (loopback/queue),
`external` (outbound to non-loopback hosts), `unknown`. `CrossesTrustBoundary` is computed from
the call graph — it is only set when a positive path exists from a public inbound service to an
external outbound service's methods, never guessed.

## Taint seeding

Framework entry points taint their bound parameters in data-flow analysis (binding sources:
`http-route`, `http-query`, `http-body`, `rpc-message`, `queue-message`, `websocket-message`,
`mcp-tool-arg`, `function-payload`). A controller action taking a plain `string id` produces a
real source→sink slice. Weakness kinds include `PromptInjectionCandidate` and
`McpToolInjectionCandidate` (CWE-1427).

## Best-effort areas

- VB.NET endpoint extraction remains syntax-only (`ApiEndpointAnalyzer`); C# is fully
  provider-driven.
- Route templates that cannot be resolved (non-constant expressions) keep the verbatim template
  in `Route` and leave `Path` null at low confidence — never a garbled path.
- Razor/Blazor parsing is textual by design (the templates are not C# compilations).

## CLI

```bash
dotnet run --project ./Dosai/Dosai.csproj -- methods --path ./src --o methods.json \
  --classify-data false \        # disable service.data classification
  --max-conventional-routes 250 \ # cap conventional route expansion
  --include-prompt-text           # emit full system prompt text (redacted by default)
```
