# Migrating to schema 4.0.0

Schema 4.0.0 (branch `framework-semantics-4.0.0`) replaces name-matching endpoint extraction
with framework providers. The changes below are output-visible. Anything not listed here is
unchanged from 3.3.0.

## Metadata and entry points

| Change | 3.3.0 | 4.0.0 |
| ------ | ----- | ----- |
| `Metadata.SchemaVersion` | `3.3.0` | `4.0.0` |
| `EntryPoint.Urls` | file-scope absolute URLs | renamed to **`EntryPoint.RawUrls`** |
| `EntryPoint.Id` | sequential `ep1`, `ep2`, … | framework entry points use stable ids `ep:op:svc:<framework>:<group>/<name>#<verb>:<path>`; analyzer/Cli entry points keep sequential ids but are numbered **after** the framework ones, so every `epN` index shifts |
| `EntryPoint.Kind` | `HttpEndpoint` / `HttpMinimalApi` / `Cli` | `HttpController`, `HttpMinimalApi`, `HttpRazorPage`, `Grpc`, `SignalRHub`, `Soap`, `GraphQL`, `OData`, `AzureFunction`, `LambdaFunction`, `MessageConsumer`, `HostedService`, `McpTool`, `McpPrompt`, `McpResource`, `Cli`. **`HttpEndpoint` no longer exists.** |

Consumers that filter `Kind == "HttpEndpoint"` must switch to the new vocabulary; consumers that
key on `epN` ids must treat ids as opaque strings, not indexes.

## Route resolution

- `ApiEndpoint.Path` is now the **resolved route** (`/api/WeatherForecast/{id}`) with
  `[controller]`/`[action]`/`[area]` tokens substituted; `ApiEndpoint.Route` keeps the verbatim
  template. In 3.3.0 `Path` held the source file path — consumers must not read it as a location.
- Conventional routing (`MapControllerRoute`, `MapDefaultControllerRoute`) expands per action:
  `{controller=Home}/{action=Index}/{id?}` for `GrantsController.Index` yields `/Grants/Index`.
  Optional segments without defaults (`{id?}`) are omitted from the path and recorded in
  `RouteParameters` with `Optional = true` instead of appearing as a mandatory `{id}` segment.
- Attribute-routed controllers are excluded from conventional expansion (matching MVC), and
  Web API 2 convention paths use the real default route `api/{controller}/{id?}` instead of a
  fabricated `/api/{controller}/{action}` path.
- Minimal APIs compose `MapGroup` prefixes (including fluent chains such as
  `var v1 = app.MapGroup("api/catalog").HasApiVersion(1,0)`), so paths that previously lost
  their group prefix are now complete.

## Data flows

- `--pattern-packs` default (`all`) now includes the `grpc`, `messaging`, `ai`, and `mcp` packs.
  Default `dataflows` output can contain new source/sink categories (`rpc`, `messaging`,
  `llm-output`, `prompt`, `mcp`) and new weakness kinds (`PromptInjectionCandidate` CWE-1427,
  `McpToolInjectionCandidate`) without any flag changes. Pass
  `--pattern-packs aspnet,data,filesystem,serialization,cloud,rpc,auth,crypto` to restore the
  3.3.0 pattern set exactly.
- Framework entry-point parameters are seeded as taint sources (http/rpc/queue/websocket/mcp),
  so `dataflows` on the same code now reports more sources. DI-injected parameters
  (`[FromServices]`, interfaces, `DbContext`-style services, `HttpContext`,
  `CancellationToken`) are excluded.

## `methods` output shape

- New arrays: `Services[]` (service inventory), `Frameworks[]` (detections), `AiComponents[]`
  (models/tools/prompts/agents). Existing arrays are unchanged.
- `ApiEndpoints`/`EntryPoints` counts roughly double on controller apps: every routed action is
  now reported, not just attribute-routed ones.

## Stable ids

- `svc:<framework>:<group>/<name>` for services; `op:<serviceId>#<verb>:<path>` for operations;
  `ai:<kind>:<provider>/<name>` for AI components. Ids never contain absolute paths, lines, or
  timestamps, so output is byte-reproducible across machines. When the same namespace + class
  name appears in several projects (common in sample suites), the service group additionally
  carries the source directory (e.g. `svc:grpc:Server.examples.Counter/CounterService`) to keep
  ids unique.

## cdxgen consumers

- Prefer `ApiEndpoint.Path` (resolved route) over `Route` **only when `SchemaVersion >= 4.0.0`**
  — in 3.3.0 `Path` is a file path.
- Services map to CycloneDX 1.7 `services[]` with `bom-ref` = `svc:` id, `trustZone`, `data[]`
  classifications, and endpoint evidence.

## CLI additions

| Flag | Effect |
| ---- | ------ |
| `--classify-data` / `--no-classify-data` | Toggle DTO data classification (default on) |
| `--max-conventional-routes N` | Cap conventional route expansion (default 500) |
| `--include-prompt-text` | Emit full prompt text (default: SHA-256 prefix + first 200 chars; secret-shaped text is always withheld) |
| `mcp --mcp-root DIR` | Confine the MCP server to paths under `DIR` |
