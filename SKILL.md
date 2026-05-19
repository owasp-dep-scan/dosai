# SKILL.md

## Skill: Use and extend Dosai

### When to use this skill

Use this skill when you need to inspect .NET source/assemblies, produce call graphs, generate data-flow slices, correlate package URLs, or add new analysis rules to Dosai.

## Quick CLI recipes

### Extract methods, endpoints, dependencies, call graph

```bash
dotnet run --project ./Dosai -- methods \
  --path ./path/to/repo \
  --o dosai.json
```

### Export call graph

```bash
dotnet run --project ./Dosai -- methods \
  --path ./path/to/repo \
  --o dosai.json \
  --callgraph-format graphml \
  --callgraph-out callgraph.graphml
```

### Generate data-flow slices

```bash
dotnet run --project ./Dosai -- dataflows \
  --path ./path/to/repo \
  --o dataflows.json
```

### Diagnose source/sink detection

```bash
dotnet run --project ./Dosai -- dataflows \
  --path ./path/to/repo \
  --o dataflows.json \
  --print-sources-sinks
```

### Generate agent context and reports

```bash
dotnet run --project ./Dosai -- agent-context \
  --path ./path/to/repo \
  --o agent-context.json

dotnet run --project ./Dosai -- report \
  --input dataflows.json \
  --o dosai-report.md

dotnet run --project ./Dosai -- diff \
  --old old-dataflows.json \
  --new new-dataflows.json \
  --o dataflow-diff.json
```

### Custom patterns

```json
{
  "sources": [
    {
      "kind": "Method",
      "match": "Contains",
      "pattern": "Input.Get",
      "category": "custom-source"
    }
  ],
  "sinks": [
    {
      "kind": "Method",
      "match": "Contains",
      "pattern": "Dangerous.Exec",
      "category": "custom-sink"
    }
  ]
}
```

```bash
dotnet run --project ./Dosai -- dataflows \
  --path ./path/to/repo \
  --patterns patterns.json \
  --o dataflows.json
```

## Development workflow

1. Read relevant source:
   - `Dosai/DataFlow.cs`
   - `Dosai/Dosai.cs`
   - `Dosai/ApiEndpoint.cs`
   - `Dosai/PackageUrlResolver.cs`
2. Add tests in `Dosai.Tests/DosaiTests.cs`.
3. Run `dotnet test`.
4. Run at least one CLI smoke test.
5. Validate graph XML with Python or `xmllint`.

## Data-flow performance guardrails

- CI should keep a full-project data-flow smoke run against `./Dosai` so performance regressions are visible on normal pull requests.
- `Dosai/DataFlow.cs` uses `DataFlowPatternIndex` to pre-split source, sink, passthrough, and sanitizer patterns by hot lookup kind. Add new repeated pattern scans to that index instead of filtering the full pattern lists in tight loops.
- The operation walker caches syntax text and only materializes code strings for code-like pattern matching. Avoid unconditional `SyntaxNode.ToString()` calls when symbol, name, type, namespace, parameter, or attribute matching is enough.
- Slice construction depends on de-duplicated edges and an outgoing-edge index keyed by source node. Preserve this shape so creating each slice does not scan the whole data-flow edge list.

## Common tasks

### Add a source pattern

- Edit `CreateDefaultPatterns()` in `Dosai/DataFlow.cs`.
- Add a `DataFlowPatternTarget.Source` entry.
- Add a test with a source-to-sink flow.

### Add a sink pattern

- Edit `CreateDefaultPatterns()`.
- Prefer `Kind = Name` or `Kind = Method`.
- Use `Kind = Code` only when legacy/unresolved Roslyn operations require fallback.

### Add an endpoint shape

- Edit `Dosai/ApiEndpoint.cs`.
- Prefer syntax-based extraction to keep endpoint analysis robust when references are missing.

### Add PURL mapping behavior

- Edit `Dosai/PackageUrlResolver.cs`.
- PURL logic must be best-effort.
- Add tests using generated `project.assets.json` fixtures.

## Quality gates

```bash
dotnet test ./Dosai.sln
```

Optional external smoke tests:

```bash
git clone --depth 1 https://github.com/Soham7-dev/AspGoat.git /tmp/AspGoat

dotnet run --project ./Dosai -- dataflows \
  --path /tmp/AspGoat \
  --o /tmp/aspgoat-dataflows.json \
  --graph-format graphml \
  --graph-out /tmp/aspgoat.graphml
```
