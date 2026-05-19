# AGENTS.md

Guidance for AI coding agents working on Dosai.

## Project overview

Dosai is a .NET source and assembly inspection tool. The main project is `Dosai/Dosai.csproj`; tests live in `Dosai.Tests` and test fixtures in `Dosai.TestData`.

Primary commands:

```bash
dotnet build /Users/prabhu/work/owasp/dosai/Dosai.sln

dotnet test /Users/prabhu/work/owasp/dosai/Dosai.sln
```

## Important source files

| File                          | Purpose                                                          |
| ----------------------------- | ---------------------------------------------------------------- |
| `Dosai/Dosai.cs`              | Main methods/source/callgraph extraction pipeline                |
| `Dosai/DataFlow.cs`           | Data-flow patterns, DTOs, Roslyn operation walker, slicing logic |
| `Dosai/DataFlowExporter.cs`   | Mermaid/GraphML/GEXF data-flow export                            |
| `Dosai/CallGraphExporter.cs`  | Mermaid/GraphML/GEXF call graph export                           |
| `Dosai/ApiEndpoint.cs`        | API route and URL extraction                                     |
| `Dosai/PackageUrlResolver.cs` | NuGet PURL enrichment from assets/deps files                     |
| `Dosai/CommandLine.cs`        | CLI commands and options                                         |
| `Dosai.Tests/DosaiTests.cs`   | Unit/integration tests                                           |

## Coding expectations

- Prefer Roslyn `IOperation` over syntax-only analysis when semantic accuracy matters.
- Keep edge endpoints valid: every graph edge must reference existing nodes.
- Preserve JSON compatibility unless a task explicitly allows breaking changes.
- PURL enrichment must be best-effort and must never fail analysis.
- Add tests for every new analysis heuristic.
- For legacy projects with missing references, add safe fallback behavior instead of throwing.

## Validation checklist

Run before finishing changes:

```bash
dotnet test /Users/prabhu/work/owasp/dosai/Dosai.sln
```

For CLI smoke tests:

```bash
dotnet run --project /Users/prabhu/work/owasp/dosai/Dosai/Dosai.csproj -- methods \
  --path /Users/prabhu/work/owasp/dosai/Dosai \
  --o /tmp/dosai-methods.json \
  --callgraph-format graphml \
  --callgraph-out /tmp/dosai-callgraph.graphml

dotnet run --project /Users/prabhu/work/owasp/dosai/Dosai/Dosai.csproj -- dataflows \
  --path /Users/prabhu/work/owasp/dosai/Dosai \
  --o /tmp/dosai-dataflows.json \
  --graph-format gexf \
  --graph-out /tmp/dosai-dataflows.gexf

dotnet run --project /Users/prabhu/work/owasp/dosai/Dosai/Dosai.csproj -- agent-context \
  --path /Users/prabhu/work/owasp/dosai/Dosai \
  --o /tmp/dosai-agent-context.json

dotnet run --project /Users/prabhu/work/owasp/dosai/Dosai/Dosai.csproj -- policy \
  --input /tmp/dosai-dataflows.json \
  --min-slices 0
```

## Data-flow heuristic policy

When adding a new source/sink:

1. Add a default pattern only if it is broadly useful for .NET security analysis.
2. Add a focused test showing source-to-sink reachability.
3. If the pattern compensates for unresolved references, document why.
4. Prefer `Name` or `Method` pattern kinds for symbol-resolved APIs; use `Code` only as a fallback.

## Documentation locations

- Compiler internals: `docs/compiler-engineering.md`
- Security analyst guide: `docs/security-analysis.md`
- PURL/supply-chain details: `docs/supply-chain-purl.md`
- Graph exports: `docs/graph-formats.md`
- Threat model: `THREAT_MODEL.md`
- Security reporting: `SECURITY.md`
