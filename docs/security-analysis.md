# Security Analysis Guide

This guide explains how security analysts can use Dosai's new call graph, API endpoint, and data-flow slicing features.

## High-level workflow

```text
                 ┌────────────────────┐
                 │ Source / assemblies │
                 └─────────┬──────────┘
                           │
             ┌─────────────▼─────────────┐
             │ dosai methods              │
             │ methods + calls + routes   │
             │ callgraph + purls          │
             └─────────────┬─────────────┘
                           │
             ┌─────────────▼─────────────┐
             │ dosai dataflows            │
             │ sources -> sinks -> slices │
             └─────────────┬─────────────┘
                           │
       ┌───────────────────▼───────────────────┐
       │ Triage: endpoint + package + sink risk │
       └───────────────────────────────────────┘
```

## Commands

### Methods, endpoints, and call graph

```bash
dotnet run --project ./Dosai -- methods \
  --path /path/to/repo \
  --o dosai.json \
  --callgraph-format graphml \
  --callgraph-out callgraph.graphml
```

The `dosai.json` output includes:

- `Methods`
- `MethodCalls`
- `Properties`, `Fields`, `Events`, `Constructors`
- `CallGraph`
- `ApiEndpoints`
- `AssemblyInformation`
- `SourceAssemblyMapping`
- PURL fields where NuGet package identity is known

### Data-flow slices

```bash
dotnet run --project ./Dosai -- dataflows \
  --path /path/to/repo \
  --o dataflows.json \
  --graph-format graphml \
  --graph-out dataflows.graphml
```

The `dataflows.json` output includes `Metadata`, `EntryPoints`, `PackageReachability`, `DangerousApiReachability`, and `WeaknessCandidates` in addition to nodes, edges, and slices.

### Custom source, sink, passthrough, and sanitizer patterns

Use `--patterns` when application-specific wrappers or framework conventions are not covered by the built-in packs.

```bash
dotnet run --project ./Dosai -- dataflows \
  --path /path/to/repo \
  --patterns ./dataflow-patterns.json \
  --o dataflows.json \
  --print-sources-sinks
```

Pattern files contain `sources`, `sinks`, `passthroughs`, and `sanitizers` arrays. See [Data-flow custom patterns](./dataflow-patterns.md) for the full schema and examples. See [Built-in data-flow pattern pack catalog](./pattern-packs.md) for the exact defaults behind `--pattern-packs`.

Use [Dosai query language](./query-language.md) to filter large JSON outputs down to the slices, weakness candidates, packages, or crypto findings relevant to a review. Use [AI-agent and automation workflows](./agent-workflows.md) for recommended `agent-context`, MCP, query, report, and diff loops.

### Agent context, reports, and diff

```bash
dotnet run --project ./Dosai -- agent-context \
  --path /path/to/repo \
  --o agent-context.json

dotnet run --project ./Dosai -- report \
  --input dataflows.json \
  --o dosai-report.md

dotnet run --project ./Dosai -- diff \
  --old old-dataflows.json \
  --new new-dataflows.json \
  --o dataflow-diff.json
```

For CI gates, validate graph edge endpoint integrity and project-specific slice-count expectations directly against `dataflows.json` or with `query`.

### Print detected sources/sinks

```bash
dotnet run --project ./Dosai -- dataflows \
  --path /path/to/repo \
  --o dataflows.json \
  --print-sources-sinks
```

Example diagnostic output:

```text
Data-flow sources: 1
SOURCE  cli     Program.cs:4:19    args            args
Data-flow sinks: 1
SINK    command Program.cs:6:9     Start           Process.Start(args[0])
```

## Built-in source categories

| Category     | Examples                                                                 |
| ------------ | ------------------------------------------------------------------------ |
| `cli`        | `Main(string[] args)`, `Console.ReadLine()`                              |
| `http`       | ASP.NET route/action parameters, request headers/query/form/body/cookies |
| `webforms`   | `.Text`, `.SelectedItem.Value` controls                                  |
| `message`    | `request`, `command`, `query` handler parameters                         |
| `serverless` | Azure Function attributes/triggers                                       |
| `rpc`        | gRPC server call context                                                 |
| `input`      | generic `input` parameter                                                |

## Built-in sink categories

| Category          | Examples                                                                                                         |
| ----------------- | ---------------------------------------------------------------------------------------------------------------- |
| `command`         | `Process.Start`, `ProcessStartInfo`                                                                              |
| `file`            | `File.*`, `Directory.*`, `FileStream`, `SaveAs`, `CopyTo`, `Server.MapPath`                                      |
| `network`         | `HttpClient.*`                                                                                                   |
| `redirect`        | `Response.Redirect`, `Redirect`                                                                                  |
| `sql`             | `SqlCommand`, `MySqlCommand`, `SqliteCommand`, `ExecuteNonQuery`, `ExecuteReader`, `ExecuteSqlRaw`, `FromSqlRaw` |
| `reflection`      | `Assembly.Load`, `Type.GetType`                                                                                  |
| `deserialization` | `BinaryFormatter.Deserialize`, `Deserialize`                                                                     |
| `rpc`             | Orleans `GetGrain` dispatch                                                                                      |

## Reading a slice

A data-flow slice contains:

```json
{
  "Id": "dfs1",
  "SourceId": "dfn12",
  "SinkId": "dfn13",
  "NodeIds": ["dfn12", "dfn13"],
  "EdgeIds": ["dfe15"],
  "SourceCategory": "http",
  "SinkCategory": "file",
  "SourcePurl": null,
  "SinkPurl": "pkg:nuget/Example@1.2.3",
  "Purls": ["pkg:nuget/Example@1.2.3"],
  "SinkArgument": "model.File.CopyTo(stream)",
  "SinkArgumentIndex": -1,
  "Summary": "Data flows from dfn12 to CopyTo argument -1."
}
```

`SinkArgumentIndex == -1` means the tainted value was the receiver object, e.g. `model.File.CopyTo(...)`.

## Weakness candidates

Dosai converts source-to-sink slices into deterministic weakness candidates. These are semantic review artifacts, not vulnerability-management records.

| Sink category     | Candidate kind                       | CWE     |
| ----------------- | ------------------------------------ | ------- |
| `command`         | `CommandInjectionCandidate`          | CWE-78  |
| `file`            | `PathTraversalOrFileAccessCandidate` | CWE-22  |
| `sql`             | `SqlInjectionCandidate`              | CWE-89  |
| `network`         | `SsrfCandidate`                      | CWE-918 |
| `redirect`        | `OpenRedirectCandidate`              | CWE-601 |
| `deserialization` | `UnsafeDeserializationCandidate`     | CWE-502 |
| `reflection`      | `UnsafeReflectionCandidate`          | CWE-470 |

Each weakness candidate includes confidence, reasons, slice ID, source/sink locations, route where known, PURLs, and evidence strings.

## API endpoints

Endpoints are emitted in `ApiEndpoints` from the `methods` command.

Supported patterns:

- Controller attributes: `[Route]`, `[HttpGet]`, `[HttpPost]`, etc.
- Minimal APIs: `MapGet`, `MapPost`, `MapPut`, `MapDelete`, `MapPatch`, `MapMethods`
- VB.NET HTTP/route attributes
- absolute URLs in source

Example:

```json
{
  "HttpMethod": "GET",
  "Route": "api/[controller]/{id}",
  "EndpointKind": "Attribute",
  "FileName": "OrdersController.cs",
  "LineNumber": 12,
  "Urls": ["https://api.example.test/orders/"]
}
```

## Custom patterns

```json
{
  "sources": [
    {
      "kind": "Method",
      "match": "Contains",
      "pattern": "Input.Get",
      "category": "custom-source",
      "purl": "pkg:nuget/Input.Package@1.0.0"
    }
  ],
  "sinks": [
    {
      "kind": "Method",
      "match": "Contains",
      "pattern": "Dangerous.Exec",
      "category": "custom-sink",
      "purl": "pkg:nuget/Dangerous.Package@2.0.0"
    }
  ]
}
```

Pattern fields:

- `kind`: `Symbol`, `Method`, `Type`, `Namespace`, `Name`, `Parameter`, `Attribute`, `Code`
- `match`: `Contains`, `Exact`, `Prefix`, `Suffix`, `Regex`
- `pattern`: string or regex
- `category`: analyst-facing category
- `purl`: optional package URL to attach to matching source/sink nodes

## Vulnerable-repo smoke baselines

This branch was tested against:

| Repo                                      | Slices found |
| ----------------------------------------- | -----------: |
| `Soham7-dev/AspGoat`                      |            5 |
| `jerryhoff/WebGoat.NET`                   |           10 |
| `kickmeforpresident/VulnerableMVCAppDemo` |            1 |

The scheduled/manual workflow `.github/workflows/vulnerable-repo-smoke.yml` keeps these broad heuristics from regressing.
