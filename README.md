# Dotnet Source and Assembly Inspector (Dosai)

Dosai inspects source code, assemblies, and NuGet packages. It extracts methods, dependencies, API endpoints, call graphs, data-flow slices, crypto evidence, and package reachability facts for security review.

## Documentation

A rendered documentation site with guides, architecture notes, use cases, and step-by-step lessons is published from the `docs` directory at [owasp-dep-scan.github.io/dosai](https://owasp-dep-scan.github.io/dosai/). The same Markdown lives in `docs`, and the paragraphs below group it by the role you are most likely to have when you arrive here.

If you review code for security problems, start with the [security analyst guide](./docs/security-analysis.md). It walks through the `methods`, `dataflows`, and `crypto` outputs with triage workflows, the built-in source and sink categories, and weakness candidates with their CWE mappings. From there you can go deeper on [custom data-flow patterns](./docs/dataflow-patterns.md), the [built-in pattern pack catalog](./docs/pattern-packs.md), and the [query language](./docs/query-language.md) for filtering large JSON outputs. The [crypto and CBOM evidence](./docs/crypto-cbom.md) and [supply-chain PURL enrichment](./docs/supply-chain-purl.md) guides cover cryptographic findings and tracing results to NuGet packages, and [AI-agent and automation workflows](./docs/agent-workflows.md) describes the agent-context, MCP, report, and diff loops for review automation.

If you maintain or extend the analyzer itself, the [architecture overview](./docs/ARCHITECTURE.md) is the entry point, and the [compiler engineering notes](./docs/compiler-engineering.md) describe the Roslyn operation walkers, stable method identities, IL-based reconstruction, and the performance constraints of the pipeline. The [framework semantics](./docs/frameworks.md) guide documents the provider model that detects ASP.NET Core, WCF, gRPC, messaging, serverless, and AI frameworks, including confidence tiers, trust zones, and taint seeding. The [graph export formats](./docs/graph-formats.md) reference covers the Mermaid, GraphML, and GEXF outputs, and the [schema 4.0.0 migration guide](./docs/migration-4.0.md) lists every output-visible change for consumers of the JSON.

If your work is compliance, audit, or bills of materials, see the [compliance and audit guide](./docs/compliance.md). It explains how to produce a CycloneDX-style CBOM, NuGet PURL occurrence evidence, service trust zones, data classification labels, and an AI component inventory, and it states plainly what that evidence does and does not prove.

The [command reference](./docs/commands.md) documents every command with inputs, outputs, algorithms, strengths, and limitations, and it is useful regardless of role. The [threat model](./docs/THREAT_MODEL.md) explains how Dosai handles untrusted input, and [SECURITY.md](./SECURITY.md) covers reporting security issues. [SKILL.md](./SKILL.md) packages the common workflows as an AI agent skill, and the [blint integration](./docs/BLINT-INTEGRATION.md) and [YARA usage](./docs/YARA-USAGE.md) notes cover complementary binary and rule-based analysis. The [lessons](./docs/LESSON1.md) walk the common workflows end to end with runnable examples.

## Usage

`Dosai [command] [options]`

### Commands

Use `methods` for method inventory, endpoints, call graph, and dependency evidence. For managed assemblies, `methods` also extracts IL method-body call edges, portable PDB call locations, delegate targets, and lightweight virtual-call candidates. Use `dataflows` for source-to-sink slicing. Use `crypto` for cryptographic assets, materials, misuse findings, reachability, and CBOM evidence. `agent-context`, `query`, `mcp`, `report`, and `diff` support review automation and CI workflows.

For detailed command usage, implementation notes, algorithms, strengths, and limitations, see [the Dosai command reference](./docs/commands.md).

### Common options

`--path` is the file or directory to inspect. `--o` sets the output path and defaults to `dosai.json`. Use `--help` for command-specific options.

### Data-flow analysis

`dataflows` includes built-in .NET source and sink packs for ASP.NET, data access, filesystem, serialization, cloud/serverless, RPC, auth-sensitive APIs, and crypto-sensitive APIs. Custom pattern JSON can add `sources`, `sinks`, `passthroughs`, and `sanitizers`. Sanitizer matches stop taint propagation, and validators such as `Regex.IsMatch` suppress guarded true branches.

```bash
dotnet run --project ./Dosai/Dosai.csproj -- dataflows \
  --path ./Dosai \
  --o /tmp/dosai-dataflows.json \
  --pattern-packs all \
  --graph-format graphml \
  --graph-out /tmp/dosai-dataflows.graphml
```

The data-flow engine performs field-sensitive property/field taint where receiver identity is available and emits simple interprocedural summaries for parameter-to-return and parameter-to-sink callees. For C# and VB source it uses Roslyn `IOperation`; for assembly-only inputs it reconstructs method-body flow from IL metadata, control-flow branches, portable PDB sequence points and local scopes, async/iterator/display-class captured fields, external passthrough summaries, emitted framework attributes, and package dependency scope. Slices can carry taint kinds, field paths, confidence, source/assembly evidence, and F#/R/VC++ frontend evidence for common script and native input and sink patterns.

`dataflows` is quiet by default and writes the JSON/graph artifacts. Add `--print` during local triage to render each slice as a stack-trace-style path with frames such as `at Source/cli args [dfn1] in Program.cs:5:5`, code snippets, symbols, PURLs, and `via ...` edge transitions:

```bash
dotnet run --project ./Dosai/Dosai.csproj -- dataflows \
  --path ./Dosai \
  --o /tmp/dosai-dataflows.json \
  --print
```

Pass custom patterns with `--patterns ./dataflow-patterns.json`; the file is merged with built-in patterns. See [Data-flow custom patterns](./docs/dataflow-patterns.md) for the JSON schema, pattern kinds, and examples, [Built-in data-flow pattern pack catalog](./docs/pattern-packs.md) for the contents of `--pattern-packs`, and [Graph export formats](./docs/graph-formats.md) for Mermaid, GraphML, and GEXF details.

The analyzer is optimized for full source-tree CI runs: pattern matching is indexed by hot lookup kind, syntax text is cached for code-like matches, assembly dependency directories are scoped with `.deps.json` when available, and slice construction uses indexed graph edges. Dosai's CI smoke test runs `dataflows --path ./Dosai` and assembly-only fixtures to guard both source and binary paths.

Source, binary, and combined analysis share a method identity and evidence model. Method inventory records, call graph nodes/edges, method calls, data-flow nodes, and method summaries can identify whether evidence came from Roslyn source, assembly metadata, IL call/data-flow reconstruction, delegate targets, virtual candidates, external summaries, framework models, or language frontends.

### Cryptography and CBOM evidence

`crypto` detects algorithms, operations, key and certificate material, TLS settings, weak algorithms, hardcoded material, static IVs and nonces, insecure RNG, disabled certificate validation, legacy TLS references, and low PBKDF2 iteration counts. Findings include source locations, best-effort reachability from CLI and API entry points, and crypto-specific data-flow slice IDs when matching source-to-sink paths are available.

Native Dosai JSON:

```bash
dotnet run --project ./Dosai/Dosai.csproj -- crypto \
  --path ./Dosai \
  --o /tmp/dosai-crypto.json \
  --format dosai
```

Combined CycloneDX-style CBOM output:

```bash
dotnet run --project ./Dosai/Dosai.csproj -- crypto \
  --path ./Dosai \
  --o /tmp/dosai-cbom.json \
  --format cyclonedx
```

The CycloneDX mode preserves Dosai properties such as `dosai:crypto:family`, `dosai:crypto:strength`, `dosai:crypto:reachableFromEntryPoint`, `dosai:crypto:evidenceType`, and `dosai:location` so downstream BOM tooling can correlate code-level crypto assets, operations, materials, protocols, and findings with package BOMs without a separate evidence sidecar.

CBOM output can also include graph sidecars for full path inspection:

```bash
dotnet run --project ./Dosai/Dosai.csproj -- crypto \
  --path ./Dosai \
  --o /tmp/dosai-cbom.json \
  --format cyclonedx \
  --graph-format graphml,gexf
```

The CBOM includes `dosai:crypto:dataFlowSliceIds`, `dosai:crypto:sourceMaterialIds`, and `dosai:crypto:sinkOperationIds` properties where Dosai can correlate material sources to crypto operations. Graph sidecars preserve the detailed data-flow nodes and edges. See [Cryptography and CBOM analysis](./docs/crypto-cbom.md) for the full evidence model, formats, and limitations.

### F#, R, and VC++ frontends

Dosai also analyzes F#, R, and VC++/C/C++ source. The F# frontend uses `FSharp.Compiler.Service` when available and records compiler-service evidence for `.fs`, `.fsi`, and `.fsx` files. The R frontend uses `Rscript` with R's native `getParseData` parser when R is installed, then falls back to managed lexical extraction if needed. The VC++ frontend extracts functions, includes, calls, native sinks, and crypto/TLS evidence from `.c`, `.cpp`, `.cc`, `.cxx`, `.h`, `.hpp`, and `.hh` files without requiring `compile_commands.json`.

Frontend evidence is conservative when project metadata is incomplete. It still provides inventory, callgraph, data-flow, and crypto coverage without failing analysis on missing references, missing R installations, or absent native build metadata.

### Querying JSON

```bash
dotnet run --project ./Dosai/Dosai.csproj -- query \
  --input /tmp/dosai-dataflows.json \
  --query 'slices[sinkCategory=sql]' \
  --o /tmp/sql-slices.json
```

Supported collection aliases include `nodes`, `edges`, `slices`, `weaknesses`, `entrypoints`, `packages`, `dangerous`, `summaries`, `assets`, `operations`, `materials`, `protocols`, and `findings`. Filters support `=`, `!=`, `~=`, `>`, `<`, `>=`, and `<=`.

For operators, aliases, nested-property filters, and MCP query examples, see [Dosai query language](./docs/query-language.md).

### MCP-style stdio server

```bash
printf '{"jsonrpc":"2.0","id":1,"method":"tools/list"}\n' | \
  dotnet run --project ./Dosai/Dosai.csproj -- mcp --path ./Dosai
```

For local-agent loops, MCP tool calls, prompt-size strategy, and CI automation recipes, see [AI-agent and automation workflows](./docs/agent-workflows.md).

The server exposes `dosai.methods`, `dosai.dataflows`, `dosai.crypto`, `dosai.agent_context`, and `dosai.query` tool calls as line-delimited JSON-RPC responses.

### API authorization metadata

Endpoint extraction records richer auth context from attributes and common minimal API chains, including authorization policies, roles, authentication schemes, required scopes/claims, CORS policies, anonymous access, and antiforgery hints.

Since schema 4.0.0, a framework provider model also emits a first-class service inventory (`Services[]`), framework detections (`Frameworks[]`), and AI components (`AiComponents[]`), with resolved route paths, trust zones, and request/response data classification. See [Framework semantics](./docs/frameworks.md) for the provider catalog and [Migrating to schema 4.0.0](./docs/migration-4.0.md) for the output-visible changes.

---

## Developers

### Running code directly from the code repository

Build with `dotnet build ./Dosai`, then run a command such as:

```bash
dotnet run --project ./Dosai -- methods --path ./Dosai/Dosai.cs
dotnet run --project ./Dosai -- methods --path ./MyPackage.1.0.0.nupkg
dotnet run --project ./Dosai -- crypto --path ./Dosai --format cyclonedx --o /tmp/dosai-cbom.json
```

### Generating a self-contained executable for a system

For Windows, run `dotnet publish -r win-x64 --self-contained`. For Linux, run `dotnet publish -r linux-x64 --self-contained`.

### Invoking the self-contained executable

After publishing, invoke `Dosai.exe methods --path ./app.dll` on Windows or `Dosai methods --path ./src` on Linux.

### Run unit tests

`dotnet test`

The [scripts README](./scripts/README.md) documents a focused performance and precision harness for `dataflows` that complements the unit tests when changing the analysis pipeline.

---

## Technical Overview

Dosai uses the Microsoft.CodeAnalysis (Roslyn) API and .NET Reflection to extract metadata from source code and compiled assemblies. It provides a unified view of code structure and dependencies across different .NET compilation outputs.

For implementation notes, algorithms, strengths, and limitations, see [Dosai compiler engineering notes](./docs/compiler-engineering.md), and for a component-level tour of the pipeline, see the [architecture overview](./docs/ARCHITECTURE.md). For a review-oriented walkthrough of the findings Dosai produces, see the [security analyst guide](./docs/security-analysis.md).

### Core Components

```
┌─────────────────┐    ┌─────────────────┐    ┌─────────────────┐
│   Source Code   │    │  .NET Assembly  │    │   .nupkg File   │
│   (.cs, .vb)    │    │  (.dll, .exe)   │    │                 │
└─────────┬───────┘    └─────────┬───────┘    └─────────┬───────┘
          │                      │                      │
          │                      │                      │ (Extract)
          ▼                      ▼                      ▼
    ┌─────────────┐      ┌─────────────┐        ┌─────────────┐
    │  Roslyn     │      │  Reflection │        │  Extracted  │
    │  Analysis   │      │  Analysis   │───────▶│  Directory  │
    │             │      │             │        │             │
    └──────┬──────┘      └──────┬──────┘        └──────┬──────┘
           │                    │                      │
           │                    │                      │
           └────────────────────┼──────────────────────┘
                                │
                                ▼
                        ┌─────────────────┐
                        │  Unified JSON   │
                        │   Output Model  │
                        │ (MethodsSlice)  │
                        └─────────────────┘
```

`GetSourceMethods` uses Roslyn's `SyntaxTree`, `SemanticModel`, and symbol analysis for C# and VB source, with dedicated language frontends for F#, R, and VC++/C/C++. `GetAssemblyMethods` loads compiled assemblies with .NET Reflection and extracts method metadata including signatures, attributes, and inheritance details. `GetMethodsFromNupkg` extracts a `.nupkg` archive to a temporary directory, filters relevant assemblies and source files, and delegates to the standard analysis pipeline before cleaning up. On top of these, `DataFlowAnalyzer` builds source-to-sink slices with pattern packs, sanitizer handling, method summaries, field-sensitive taint keys, graph exports, package reachability, and weakness candidates, and `CryptoAnalyzer` detects cryptographic assets, operations, materials, weak crypto findings, and CBOM evidence with best-effort reachability.

The output is a JSON object conforming to the `MethodsSlice` structure, with collections for dependencies, methods, method calls, members, the call graph, API endpoints, assembly information, source-assembly mappings, services, frameworks, and AI components. Field meanings and identifiers are versioned through `Metadata.SchemaVersion`, and every output-visible change is documented in the [migration guide](./docs/migration-4.0.md).

## Complementary Analysis with OWASP blint

See [this document](./docs/BLINT-INTEGRATION.md) for integration ideas.

## Integration with YARA cli

See [Yara Usage docs](./docs/YARA-USAGE.md)

## License

MIT
