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

For managed assembly-only inputs, call graph edges are extracted from IL method bodies. Direct calls, constructor calls, delegate/event callback targets, generated async/iterator state-machine calls, and shared CHA/RTA-style virtual candidates are emitted with portable PDB source locations when available, so `methods` remains useful when source is unavailable.

`PackageReachability` facts include `SourceLocations` when a reachable package can be tied to a source-backed call graph node or edge. If only dependency/import evidence is available, for example in VB `Imports`, F# `open`, or R `library`/`require` statements, Dosai emits a low-confidence `Dependency` reachability fact from `Dependencies[].Purl`. These locations carry `Path`, `FileName`, `LineNumber`, `ColumnNumber`, and evidence `Kind`, and intentionally exclude DLL-only fallback locations so SBOM occurrence evidence points at user-reviewable source.

For source directories, `methods` ignores source files under `bin` and `obj` relative to the inspected root. Assembly analysis still accepts app output directories because binary-only reviews often start from build or publish output.

### Data-flow slices

```bash
dotnet run --project ./Dosai -- dataflows \
  --path /path/to/repo \
  --o dataflows.json \
  --graph-format graphml \
  --graph-out dataflows.graphml
```

The `dataflows.json` output includes `Metadata`, `EntryPoints`, `PackageReachability`, `DangerousApiReachability`, and `WeaknessCandidates` in addition to nodes, edges, and slices.

For data-flow outputs, `PackageReachability[].SourceLocations` is scoped to nodes or edges that carry the same PURL. When a PURL comes from a source/sink pattern rather than a concrete node or edge, Dosai falls back to the matching sink or source location. This avoids reporting unrelated source nodes in the same slice as package occurrences.

`--path` may point at a source tree, a single managed `.dll`/`.exe`, or a directory containing managed assemblies. Source analysis uses Roslyn `IOperation`; assembly-only analysis reconstructs local and interprocedural flows from IL method bodies, branch targets, exception regions, metadata symbols, portable PDB local scopes/sequence points, emitted framework attributes, and compiler-generated async/iterator/display-class fields. For assembly directories, Dosai reads adjacent `.deps.json` files to prefer project/application assemblies and avoid flooding results with framework and package dependency internals.

Assembly-derived nodes include `Properties.analysis = "assembly-il"`, `Properties.assembly`, `Properties.ilOffset`, and a metadata token. When a portable PDB is available, node and edge `FileName`, `Path`, `LineNumber`, and `ColumnNumber` use source locations; otherwise they fall back to the assembly file and IL offset.

For local triage, add `--print` to render each slice as a stack-trace-style path with code, file, line, column, symbols, PURLs, and edge transitions:

```bash
dotnet run --project ./Dosai -- dataflows \
  --path /path/to/repo \
  --o dataflows.json \
  --print
```

Example printed flow:

```text
└─ DataFlow dfs1: cli → command (Medium)
   Summary: cli data reaches command sink Start.
   Stack (3 frames, 3 transitions):
     at Source/cli args [dfn1] in Program.cs:5:5
        code: args
        symbol: string[] args
     via VariableAssignment [dfe1] from dfn1 to dfn2 in Program.cs:6:13 label=command
     at Assignment command [dfn2] in Program.cs:6:13
        code: command = args[0]
     via SinkArgument [dfe3] from dfn2 to dfn3 in Program.cs:7:23 label=fileName targetPurl=pkg:nuget/System.Diagnostics.Process
     at Sink/command Start [dfn3] in Program.cs:7:9 [pkg:nuget/System.Diagnostics.Process]
        code: Process.Start(command)
```

Without `--print`, `dataflows` is quiet by default and writes only the requested output files.

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

Validator-style sanitizers such as `Regex.IsMatch(input, pattern)` are branch aware for common `if` conditions. Dosai suppresses taint on the validated path and preserves taint on the unvalidated path. This reduces obvious false positives, but it is still pattern-based static analysis. Project-specific validation helpers should be added as custom sanitizer patterns.

Call graph evidence distinguishes direct observations from inferred framework and reflection edges. DI registrations, service-provider resolution, framework callback registrations, and simple reflection patterns are useful triage hints, but they should be reviewed as inferred `FrameworkModel` or `ReflectionHeuristic` evidence rather than proof that a runtime call always occurs.

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

`--print-sources-sinks` is a pattern diagnostics mode. It prints the matched sources and sinks, not the full source-to-sink path. Use `--print` when you want stack-trace-style path visualization.

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
