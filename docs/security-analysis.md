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
