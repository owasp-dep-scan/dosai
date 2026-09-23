# Dotnet Source and Assembly Inspector (Dosai)

Dosai inspects source code, assemblies, and NuGet packages. It extracts methods, dependencies, API endpoints, call graphs, data-flow slices, crypto evidence, package reachability facts, per-node reachability with dead-code reporting, exploit chains from routes to sinks, an attack-surface view grouped by exposure, and severity-tagged security findings for security review.

## Documentation

A rendered documentation site with guides, architecture notes, use cases, and step-by-step lessons is published from the `docs` directory at [owasp-dep-scan.github.io/dosai](https://owasp-dep-scan.github.io/dosai/). The same Markdown lives in `docs`, and the paragraphs below group it by the role you are most likely to have when you arrive here.

If you review code for security problems, start with the [security analyst guide](./docs/security-analysis.md). It walks through the `methods`, `dataflows`, and `crypto` outputs with triage workflows, the built-in source and sink categories, and weakness candidates with their CWE mappings. From there you can go deeper on [custom data-flow patterns](./docs/dataflow-patterns.md), the [built-in pattern pack catalog](./docs/pattern-packs.md), and the [query language](./docs/query-language.md) for filtering large JSON outputs. The [crypto and CBOM evidence](./docs/crypto-cbom.md) and [supply-chain PURL enrichment](./docs/supply-chain-purl.md) guides cover cryptographic findings and tracing results to NuGet packages, and [AI-agent and automation workflows](./docs/agent-workflows.md) describes the agent-context, MCP, report, and diff loops for review automation.

If you maintain or extend the analyzer itself, the [architecture overview](./docs/ARCHITECTURE.md) is the entry point, and the [compiler engineering notes](./docs/compiler-engineering.md) describe the Roslyn operation walkers, stable method identities, IL-based reconstruction, and the performance constraints of the pipeline. The [framework semantics](./docs/frameworks.md) guide documents the provider model that detects ASP.NET Core, WCF, gRPC, messaging, serverless, and AI frameworks, including confidence tiers, trust zones, and taint seeding. The [graph export formats](./docs/graph-formats.md) reference covers the Mermaid, GraphML, and GEXF outputs. The [schema 4.0.0](./docs/migration-4.0.md), [schema 4.1.0](./docs/migration-4.1.0.md), and [schema 5.0.0](./docs/migration-5.0.md) migration guides list every output-visible change for consumers of the JSON.

If your work is compliance, audit, or bills of materials, see the [compliance and audit guide](./docs/compliance.md). It explains how to produce a CycloneDX-style CBOM, NuGet PURL occurrence evidence, service trust zones, data classification labels, and an AI component inventory, and it states plainly what that evidence does and does not prove.

The [command reference](./docs/commands.md) documents every command with inputs, outputs, algorithms, strengths, and limitations, and it is useful regardless of role. The [threat model](./docs/THREAT_MODEL.md) explains how Dosai handles untrusted input, and [SECURITY.md](./SECURITY.md) covers reporting security issues. [SKILL.md](./SKILL.md) packages the common workflows as an AI agent skill, and the [blint integration](./docs/BLINT-INTEGRATION.md) and [YARA usage](./docs/YARA-USAGE.md) notes cover complementary binary and rule-based analysis. The [lessons](./docs/LESSON1.md) walk the common workflows end to end with runnable examples.

## Usage

`Dosai [command] [options]`

### Commands

Use `methods` for method inventory, endpoints, call graph, dependency evidence, per-node reachability facts, recursion clusters, dead-code reporting, and endpoint security findings. For managed assemblies, `methods` also extracts IL method-body call edges, portable PDB call locations, delegate targets, and lightweight virtual-call candidates. Use `dataflows` for source-to-sink slicing with severity, exploit chains, an attack-surface view, sanitizer negative evidence, and optional suppressions. Use `crypto` for cryptographic assets, materials, misuse findings, reachability, and CBOM evidence. `agent-context`, `query`, `mcp`, `report`, and `diff` support review automation and CI workflows.

For detailed command usage, implementation notes, algorithms, strengths, and limitations, see [the Dosai command reference](./docs/commands.md).

### Common options

`--path` is the file or directory to inspect. `--o` sets the output path and defaults to `dosai.json`. Use `--help` for command-specific options.

### Debug progress logging

Add `--debug` to any command (or set `DOSAI_DEBUG=1` / `DOSAI_DEBUG=true`) to report what dosai is doing on stderr: which phase is running, how long it took, and how large the intermediate data is. Long analyses log a heartbeat naming the current phase every 30 seconds, so a run that seems stuck tells you where it is. This is the log to paste into an issue when a scan is slow - it contains paths, counts, assembly and phase names, and timings only, never source text or other file contents.

```bash
dotnet run --project ./Dosai/Dosai.csproj -- methods \
  --path ./YourRepo \
  --o /tmp/dosai-methods.json \
  --debug
```

Example output (captured from `methods --path ./Dosai --debug`, with one heartbeat line from a longer `dataflows` run on the same tree):

```
[dosai +0.006s] dosai 5.0.0.0, .NET 11.0.0-rc.1.26425.128, macOS 26.6.2 Arm64, 14 processor(s)
[dosai +0.009s] input path: /Users/you/dosai/Dosai
[dosai +0.009s] --exclude: <none>
[dosai +0.020s] file discovery under './Dosai': 90 .cs, 1420 .dll, 0 .exe, 0 .fs, 0 .vb, 0 excluded by --exclude, 1669 files total
[dosai +0.020s] start methods
[dosai +0.050s] start methods.assembly-inspection
[dosai +0.072s] assembly scoping: kept 20, dropped 1385 of 1405 candidates (heuristic name filter (System./Microsoft./Newtonsoft./FSharp./Humanizer prefixes; no deps.json project libraries))
[dosai +0.148s] end methods.assembly-inspection in 0.098s, managed heap 7 MB, working set 147 MB
[dosai +0.610s] start methods.symbol-analysis
[dosai +7.666s] end methods.symbol-analysis in 7.055s, managed heap 162 MB, working set 1.0 GB
[dosai +8.924s] reachability bucketing path: condensed bitsets (6006 components x 6022 nodes, 4 MB of bitsets)
[dosai +8.939s] reachability bucketing (condensed bitsets) completed in 0.015s
[dosai +9.273s] end methods in 9.254s, managed heap 247 MB, working set 1.1 GB
[dosai +30.023s] still in dataflows.graph-walk, managed heap 171 MB, working set 1.0 GB
```

All debug output goes to stderr: JSON files and the `mcp` JSON-RPC stream on stdout are never touched, and a run's output is byte-identical with and without `--debug` (apart from the pre-existing `GeneratedAt` timestamp, which changes between any two runs). When the flag is off, debug logging costs nothing - no messages are built at all.

### Data-flow analysis

`dataflows` includes built-in .NET source and sink packs for ASP.NET, data access, filesystem, serialization, cloud/serverless, RPC, auth-sensitive APIs, and crypto-sensitive APIs, plus weakness-class packs for XSS, XXE, LDAP/XPath/NoSQL injection, log and header injection, ReDoS, and template injection. Custom pattern JSON can add `sources`, `sinks`, `passthroughs`, and `sanitizers`, and a pattern can override the severity of the slices it produces. Sanitizer matches stop taint propagation, validators such as `Regex.IsMatch` suppress guarded true branches, and both record the suppressed flow in `SanitizedFlows` as negative evidence.

```bash
dotnet run --project ./Dosai/Dosai.csproj -- dataflows \
  --path ./Dosai \
  --o /tmp/dosai-dataflows.json \
  --pattern-packs all \
  --graph-format graphml \
  --graph-out /tmp/dosai-dataflows.graphml
```

The data-flow engine performs field-sensitive property/field taint where receiver identity is available and emits interprocedural summaries for parameter-to-return, parameter-to-sink, and `ref`/`out` parameter writes, iterating them to a fixpoint so wrapper chains attribute to the outermost method. Lambda parameters passed over tainted values are seeded, delegate invocations and `await` propagate taint, and `foreach` variables and indexer stores taint their collection. For C# and VB source it uses Roslyn `IOperation`; for assembly-only inputs it reconstructs method-body flow from IL metadata, control-flow branches, portable PDB sequence points and local scopes, async/iterator/display-class captured fields, external passthrough summaries, emitted framework attributes, and package dependency scope. Slices can carry taint kinds, field paths, confidence, severity, source/assembly evidence, and F#/R/VC++ frontend evidence for common script and native input and sink patterns.

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

`crypto` detects algorithms, operations, key and certificate material, TLS settings, weak algorithms, hardcoded material, static IVs and nonces, insecure RNG, disabled certificate validation, legacy TLS references, and low PBKDF2 iteration counts. Findings include source locations, reachability from CLI and API entry points (a graph path is required for the claim; when the call graph cannot support one, the fallback is gated off and a diagnostic names the file), and crypto-specific data-flow slice IDs when matching source-to-sink paths are available.

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

Dosai also analyzes F#, R, and VC++/C/C++ source. The F# frontend uses `FSharp.Compiler.Service` when available and records compiler-service evidence for `.fs`, `.fsi`, and `.fsx` files; F# script directives (`#r "nuget: ..."`, `#r "Assembly"`, `#load "file.fsx"`) are collected as dependencies. The R frontend uses `Rscript` with R's native `getParseData` parser when R is installed, then falls back to managed lexical extraction if needed; that fallback recognizes modern R syntax (native pipes, `_` placeholders, `\(x)` lambda assignments) and analyzes R Markdown/Quarto documents chunk by chunk, so markdown prose and non-R chunks contribute nothing. The VC++ frontend extracts functions, includes, calls, native sinks, and crypto/TLS evidence from `.c`, `.cpp`, `.cc`, `.cxx`, `.h`, `.hpp`, and `.hh` files without requiring `compile_commands.json`.

Frontend evidence is conservative when project metadata is incomplete. It still provides inventory, callgraph, data-flow, and crypto coverage without failing analysis on missing references, missing R installations, or absent native build metadata.

### .NET 11 and C# 15 support

Dosai targets .NET 11 and analyzes C# 15 source, the default language version for `net11.0` projects. C# 15 union declarations are parsed, inventoried, and tracked through pattern matching: taint flows from a `switch` operand into the payload locals bound by case patterns, including recursive patterns over union cases, and the data classifier expands a union into its case records so members hidden in a single case still surface in data classifications. The rest of C# 15 is covered too: `closed` hierarchies with exhaustive switches, extension indexers, collection expression arguments (`[with(...), .. values]`), labeled `break`/`continue`, and the memory-safety preview shapes (`unsafe(...)` expressions, pointer relaxations). Members declared in `extension` blocks are attributed to their enclosing static class.

.NET 11 library additions are recognized where they matter to analysis: the `Aes` key-wrap methods (RFC 3394) and the post-quantum algorithms (`MLKem`/FIPS 203, `MLDsa`/FIPS 204, `SlhDsa`/FIPS 205) classify as crypto operations in the CBOM, and the experimental caller-driven TLS session types in `System.Net.Security` classify as TLS protocol assets with a low-severity `DOSAI-CRYPTO-EXPERIMENTAL-TLS-API` finding (diagnostic `SYSLIB5007`). Assembly-only inputs built for .NET 11 extract fully, including C# 15 union and positional-pattern lowering: type tests, downcasts, `Deconstruct` out-parameters, and arithmetic keep taint in compiled code. F# 11 source parses with the current language constructs, including record spreads and record constructors.

Building Dosai requires the .NET 11 SDK (11.0.x); a .NET 10 SDK cannot build the current target frameworks. While .NET 11 is prerelease, the `FSharp.Compiler.Service`, `FSharp.Core`, and `System.Reflection.MetadataLoadContext` references are RC builds and are pinned to the RC SDK's versions; they move to the stable releases when .NET 11 reaches GA. The published binaries are self-contained, so analyzing .NET 11 code does not require a .NET 11 runtime on the machine running Dosai.

### .NET version support (target-framework aware analysis)

Dosai does not assume the analyzed code targets the same .NET it was built with. It detects the
analyzed project's target framework(s) — from `*.csproj`/`*.fsproj`/`*.vbproj`
(`<TargetFramework>`/`<TargetFrameworks>`), `Directory.Build.props`/`Directory.Build.targets`,
or, for built trees without a project file, `*.runtimeconfig.json` — and evaluates
conditional-compilation guards against what each detected target actually defines:

| Detected target | Preprocessor guards analyzed |
| --- | --- |
| `net8.0` | `NET`, `NET8_0`, `NET5_0_OR_GREATER`..`NET8_0_OR_GREATER`, the `NETCOREAPP` family |
| `netstandard2.0` | `NETSTANDARD`, `NETSTANDARD2_0` and the lower `NETSTANDARD*_OR_GREATER` chain |
| `net472` | `NETFRAMEWORK`, `NET472` and the `NET4x_OR_GREATER` chain |
| multi-target | the set of one **representative** target — the most modern one declared |
| `<TargetFrameworkVersion>v4.7.2</TargetFrameworkVersion>` (classic, non-SDK projects) | treated as `net472` |
| nothing detected | the historical fallback (`NET`, latest `NETn_0`, the modern chain), reported via a `Diagnostics` note |

OS/platform-suffixed targets (`net8.0-windows`) contribute their base target's symbols. Every
detected target is surfaced as `Metadata.TargetFrameworks`, and the one that guards were actually
evaluated against as `Metadata.GuardTargetFramework` — on a multi-target tree that choice decides
which `#if` arms are in the results at all, so it is reported explicitly and, when the targets
disagree, in a `Diagnostics` note too.

A multi-target project resolves to a single representative target rather than the union of all of
them, ranked by family (modern .NET, then .NET Core, then .NET Standard, then .NET Framework) and
then by version. A union looks like the cautious choice and is the opposite: defining a symbol
because *some* target defines it hides every `#if !SYMBOL` arm, and negated guards are the most
common shape in real multi-targeting libraries. Hangfire.Core
(`net451;net46;netstandard1.3;netstandard2.0`) is the worked example — its `#if !NETSTANDARD1_3`
members ship in three of its four assemblies, and unioning the four symbol sets erased them from
the inventory and the call graph entirely. One representative keeps every arm that target
compiles, which is a real, self-consistent compilation rather than a mix no build produces.

The known limitation is the mirror image: arms exclusive to a *lower* target (`#if NETFRAMEWORK`
in a `net462;net8.0` library) stay invisible, exactly as they were before target-framework
detection existed. Seeing those too would mean analyzing each target separately and merging the
results. Likewise, for trees whose targets all predate .NET 5, modern-only branches
(`#if NET5_0_OR_GREATER`) are no longer analyzed — they are phantom code for that target. The
fallback for scan roots with no detectable target framework is unchanged from earlier releases.

Building Dosai itself still requires the .NET 11 SDK (11.0.x); a .NET 10 SDK cannot build the
current target frameworks. Analyzed source may target anything from `netstandard1.x` and
`net4x` through `net11.0` — older targets are parsed and analyzed with the same fidelity; see
[Migration to schema 5.1.0](./docs/migration-5.1.0.md) for the output changes that shipped with
this behavior.

### Querying JSON

```bash
dotnet run --project ./Dosai/Dosai.csproj -- query \
  --input /tmp/dosai-dataflows.json \
  --query 'slices[sinkCategory=sql]' \
  --o /tmp/sql-slices.json
```

Supported collection aliases include `nodes`, `edges`, `slices`, `weaknesses`, `entrypoints`, `packages`, `dangerous`, `summaries`, `assets`, `operations`, `materials`, `protocols`, `findings`, `services`, `aiComponents`, `exploitChains`, `sanitizedFlows`, `reachability`, `recursionClusters`, `deadCode`, `attackSurface`, `securityFindings`, `methods`, and `callGraph`. Filters support `=`, `!=`, `~=`, `>`, `<`, `>=`, and `<=`, several terms can be OR-ed inside a conjunct with `||`, collection paths can be nested (`callGraph.nodes[...]`), results can be ordered with `sort by <property> [desc]`, and a trailing `count` returns the number of matches instead of the array.

For operators, aliases, nested-property filters, and MCP query examples, see [Dosai query language](./docs/query-language.md).

### MCP-style stdio server

```bash
printf '{"jsonrpc":"2.0","id":1,"method":"tools/list"}\n' | \
  dotnet run --project ./Dosai/Dosai.csproj -- mcp --path ./Dosai
```

For local-agent loops, MCP tool calls, prompt-size strategy, and CI automation recipes, see [AI-agent and automation workflows](./docs/agent-workflows.md).

The server exposes `dosai.methods`, `dosai.dataflows`, `dosai.crypto`, `dosai.agent_context`, `dosai.services`, `dosai.ai_components`, `dosai.query`, `dosai.exploit_chains`, `dosai.attack_surface`, and `dosai.reachability` tool calls as line-delimited JSON-RPC responses. `--mcp-root` confines file access to a chosen root and `--mcp-allowlist` restricts which stdio transport commands are treated as policy-approved.

### API authorization metadata

Endpoint extraction records richer auth context from attributes and common minimal API chains, including authorization policies, roles, authentication schemes, required scopes/claims, CORS policies, anonymous access, and antiforgery hints.

Since schema 4.0.0, a framework provider model also emits a first-class service inventory (`Services[]`), framework detections (`Frameworks[]`), and AI components (`AiComponents[]`), with resolved route paths, trust zones, and request/response data classification. Since schema 4.1.0, the same metadata feeds severity-tagged `SecurityFindings[]` (sensitive unauthenticated endpoints, CORS wildcard plus credentials, missing antiforgery, duplicate-route auth mismatch, mass-assignment hints, MCP transport integrity, and configuration security, each with a CWE and remediation). See [Framework semantics](./docs/frameworks.md) for the provider catalog and the [schema 4.0.0](./docs/migration-4.0.md) and [schema 4.1.0](./docs/migration-4.1.0.md) migration guides for the output-visible changes.

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

`GetSourceMethods` uses Roslyn's `SyntaxTree`, `SemanticModel`, and symbol analysis for C# and VB source, with dedicated language frontends for F#, R, and VC++/C/C++. `GetAssemblyMethods` loads compiled assemblies with .NET Reflection and extracts method metadata including signatures, attributes, and inheritance details. `GetMethodsFromNupkg` extracts a `.nupkg` archive to a temporary directory, filters relevant assemblies and source files, and delegates to the standard analysis pipeline before cleaning up. On top of these, `DataFlowAnalyzer` builds source-to-sink slices with pattern packs, sanitizer handling, method summaries, field-sensitive taint keys, graph exports, package reachability, and severity-tagged weakness candidates; the reachability analyzer adds per-node entry-point facts, recursion clusters, and the dead-code report; the framework security analyzer turns provider metadata into `SecurityFindings`; and `CryptoAnalyzer` detects cryptographic assets, operations, materials, weak crypto findings, and CBOM evidence with graph-path-backed reachability.

The output is a JSON object conforming to the `MethodsSlice` structure, with collections for dependencies, methods, method calls, members, the call graph, API endpoints, assembly information, source-assembly mappings, services, frameworks, AI components, reachability facts, recursion clusters, dead-code entries, and security findings. Field meanings and identifiers are versioned through `Metadata.SchemaVersion`, and every output-visible change is documented in the [migration guides](./docs/migration-5.0.md).

## Complementary Analysis with OWASP blint

See [this document](./docs/BLINT-INTEGRATION.md) for integration ideas.

## Integration with YARA cli

See [Yara Usage docs](./docs/YARA-USAGE.md)

## License

MIT
