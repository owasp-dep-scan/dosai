# AGENTS.md

Guidance for AI coding agents working on Dosai.

## Project overview

Dosai is a .NET source and assembly inspection tool. The main project is `Dosai/Dosai.csproj`; tests live in `Dosai.Tests` and test fixtures in `Dosai.TestData`.

Primary commands:

```bash
dotnet build ./Dosai.sln

dotnet test ./Dosai.sln
```

## Important source files

| File                                 | Purpose                                                          |
| ------------------------------------ | ---------------------------------------------------------------- |
| `Dosai/Dosai.cs`                     | Main methods/source/callgraph extraction pipeline                |
| `Dosai/DataFlow.cs`                  | Data-flow patterns, DTOs, Roslyn operation walker, slicing logic |
| `Dosai/DataFlowExporter.cs`          | Mermaid/GraphML/GEXF data-flow export                            |
| `Dosai/CallGraphExporter.cs`         | Mermaid/GraphML/GEXF call graph export                           |
| `Dosai/ApiEndpoint.cs`               | API route and URL extraction                                     |
| `Dosai/PackageUrlResolver.cs`        | NuGet PURL enrichment from assets/deps files                     |
| `Dosai/CryptoAnalysis.cs`            | Crypto assets, misuse, reachability, and CBOM evidence           |
| `Dosai/DataFlowAssembly.cs`          | IL-based data-flow reconstruction for assembly-only inputs       |
| `Dosai/AssemblyCallGraphAnalyzer.cs` | IL call graph, delegate targets, dispatch resolution             |
| `Dosai/LanguageFrontendAnalyzer.cs`  | F#, R, and VC++/C/C++ frontends                                  |
| `Dosai/Transparency.cs`              | Derived review facts, agent context, reports, diffs              |
| `Dosai/CommandLine.cs`               | CLI commands and options                                         |
| `Dosai.Tests/DosaiTests.cs`          | Unit/integration tests                                           |

## Coding expectations

- Prefer Roslyn `IOperation` over syntax-only analysis when semantic accuracy matters.
- Keep edge endpoints valid: every graph edge must reference existing nodes.
- Preserve JSON compatibility unless a task explicitly allows breaking changes.
- PURL enrichment must be best-effort and must never fail analysis.
- Add tests for every new analysis heuristic.
- For legacy projects with missing references, add safe fallback behavior instead of throwing.

## Data-flow performance notes

- The `dataflows` CI smoke test intentionally analyzes the full `Dosai` source tree (`--path ./Dosai`) rather than a tiny fixture.
- Preserve the current scaling optimizations in `Dosai/DataFlow.cs`: indexed pattern subsets (`DataFlowPatternIndex`), cached syntax text in the operation walker, edge de-duplication, and source-indexed outgoing edges for slice construction.
- When adding new source/sink/passthrough/sanitizer matching, route repeated lookups through the pattern index and avoid calling `SyntaxNode.ToString()` unless the selected pattern kind needs code text.
- When changing slice construction, keep it near-linear in trace size by using indexed edges; avoid scanning every graph edge for every slice.

## Validation checklist

Run before finishing changes:

```bash
dotnet test ./Dosai.sln
```

For CLI smoke tests:

```bash
dotnet run --project ./Dosai/Dosai.csproj -- methods \
  --path ./Dosai \
  --o /tmp/dosai-methods.json \
  --callgraph-format graphml \
  --callgraph-out /tmp/dosai-callgraph.graphml

dotnet run --project ./Dosai/Dosai.csproj -- dataflows \
  --path ./Dosai \
  --o /tmp/dosai-dataflows.json \
  --graph-format gexf \
  --graph-out /tmp/dosai-dataflows.gexf

dotnet run --project ./Dosai/Dosai.csproj -- agent-context \
  --path ./Dosai \
  --o /tmp/dosai-agent-context.json
```

## Data-flow heuristic policy

When adding a new source/sink:

1. Add a default pattern only if it is broadly useful for .NET security analysis.
2. Add a focused test showing source-to-sink reachability.
3. If the pattern compensates for unresolved references, document why.
4. Prefer `Name` or `Method` pattern kinds for symbol-resolved APIs; use `Code` only as a fallback.

## Documentation locations

The `docs` directory doubles as a Docsify site (see `docs/index.html`, `docs/_sidebar.md`, and `docs/_coverpage.md`), published to GitHub Pages by `.github/workflows/docs-pages.yml`.

- Command reference: `docs/commands.md`
- Compiler internals: `docs/compiler-engineering.md`
- Architecture overview: `docs/ARCHITECTURE.md`
- Security analyst guide: `docs/security-analysis.md`
- Cryptography and CBOM: `docs/crypto-cbom.md`
- AI-agent and automation workflows: `docs/agent-workflows.md`
- Query language: `docs/query-language.md`
- Framework semantics: `docs/frameworks.md`
- Migration to schema 4.0.0: `docs/migration-4.0.md`
- Data-flow custom patterns: `docs/dataflow-patterns.md`
- Built-in data-flow pattern packs: `docs/pattern-packs.md`
- PURL/supply-chain details: `docs/supply-chain-purl.md`
- Graph exports: `docs/graph-formats.md`
- Compliance and audit: `docs/compliance.md`
- Use case catalog: `docs/USE_CASES.md`
- Tutorials: `docs/LESSON1.md` through `docs/LESSON10.md`
- Threat model: `docs/THREAT_MODEL.md`
- blint integration: `docs/BLINT-INTEGRATION.md`
- YARA usage: `docs/YARA-USAGE.md`
- Security reporting: `SECURITY.md`
