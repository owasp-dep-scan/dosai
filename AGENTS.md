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
| `Dosai/CSharpSourceParser.cs`        | Single C# parse entry point and language-version policy          |

## Coding expectations

- Prefer Roslyn `IOperation` over syntax-only analysis when semantic accuracy matters.
- Parse C# only through `CSharpSourceParser.Parse`. Analyzed source may use a newer language
  version than the referenced compiler's default, and a parse error silently drops the file from
  every result, so the accepted language version stays a single decision. The parser also
  enables the `FileBasedProgram` feature so file-based app `#:` directives parse as trivia;
  `#:package` lines surface as dependencies in `GetSourceMethods`.
- Conditional compilation resolves against preprocessor symbols derived from the analyzed
  project's detected target framework (`TargetFrameworkDetection` feeding the per-root
  `CSharpSourceParser` parse options and the F# line frontend's `#if`/`#elif` tracking): each
  detected TFM contributes what its own build defines (`net8.0` -> `NET`, `NET8_0`, the
  `NET5_0_OR_GREATER` chain, the `NETCOREAPP` family; `netstandard2.0` and `net472` -> their
  families), a multi-target project resolves to one representative target (the most modern it
  declares - never the union of all of them, which would hide every `#if !SYMBOL` arm), and with
  no detectable TFM the fallback is the historical `FrameworkPreprocessorDefines.ModernNet` set
  (`NET`, `NET11_0`, the modern chain) so bare-directory scans behave as before. Detected TFMs
  surface in `Metadata.TargetFrameworks`, the representative as
  `Metadata.GuardTargetFramework`, and the fallback appends a `Diagnostics` note. `DEBUG`/`TRACE` stay undefined - a
  Release-shaped build. The F# frontend also ignores `#:`-prefixed lines (FS-1337). Bump the
  ceiling with `TargetFramework`.
- Never leave an inspected file locked. Metadata readers open with
  `FileShare.ReadWrite | FileShare.Delete`, and inspected assemblies are loaded by value
  (`InspectionAssemblyLoadContext`), because a mapped path stays locked on Windows for the
  process lifetime even after a collectible context is unloaded.
- Keep runtime-loader work (`Assembly.GetTypes()` and member reflection over inspected
  assemblies) inside `GetAssemblyMethods`, which runs it on a dedicated large-stack thread. The
  runtime type loader recurses per hierarchy level and, when a base type is missing, can
  overflow a default-sized stack (dotnet/runtime#131679) - an uncatchable process crash.
- Keep Roslyn `IOperation` work behind the guarded analysis entry points (`Dosai.GetMethodsSlice`,
  `DataFlowAnalyzer.Analyze`, `CryptoAnalyzer.Analyze`), which run on `DedicatedStack` threads
  (a nested entry point runs inline on the thread it is already on). The operation factory
  recurses roughly one frame set per call in a chain, so a deep fluent chain overflows a
  default-sized stack inside `SemanticModel.GetOperation` - the same uncatchable process crash,
  reached through source analysis. Request operations only through
  `OperationDepthGuard.GetOperation`, which skips members too deep for even the dedicated stack
  and reports them per file (`OperationDepthGuard.Describe`).
- The per-file symbol-analysis loop in `GetSourceMethods` and the dispatch index's
  object-creation scan run on a worker team of dedicated large-stack threads
  (`DedicatedStack.ForEach`; one per processor, capped by `Dosai.MaxSymbolAnalysisWorkers` /
  `DOSAI_SYMBOL_ANALYSIS_WORKERS`). Workers claim the next file as they free up. The per-file
  body must stay file-pure: everything it produces goes into that file's `SourceFileSymbols`
  collector (counts included), and everything it reads must be immutable, or memoized
  thread-safely with a result that does not depend on which thread computed it, before the
  first worker starts. Collectors merge in file order afterwards, which is what keeps the output
  byte-identical for every worker count - do not add shared mutable state, counters or merges
  inside the loop.
- Virtual/interface dispatch resolution goes through `DispatchResolver.SourceIndex`: concrete
  types bucketed once per interface/base-type original definition, lookups memoized per
  (target, receiver) symbol pair. Route any new dispatch inference through the index rather
  than scanning types. `FindImplementationForInterfaceMember` takes the member as its
  interface declares it: pass `ConstructedFrom`, never a method constructed with a call's type
  arguments, which makes Roslyn throw (issue #64). A resolution that still throws abandons only
  that candidate; the walker counts the call site and the count surfaces as a slice diagnostic.
  Only virtual invocations (`IInvocationOperation.IsVirtual`) get candidates: `base.M()` runs
  exactly the bound method.
- Analysis output is locale-independent (issue #63): a Turkish, Swedish, German, Hungarian or
  Danish locale must not change any output byte except the per-run timestamps. The Dosai
  project builds with `Dosai/Globalization.globalconfig`, which makes the culture-sensitive
  overload rules (CA1304, CA1305, CA1310, CA1311) errors: format and parse through
  `CultureInfo.InvariantCulture`, compare strings ordinally, and title-case only through the
  invariant-culture `Dosai.TitleCase`. The analyzers do not see interpolated strings, so format
  numbers inside JSON-bound strings with `string.Create(CultureInfo.InvariantCulture, ...)`;
  `Cli_Outputs_AreByteIdenticalAcrossLocales` compares every command's output across locales.
- Operation walkers derive from `DepthBoundedOperationWalker`. Its budget bounds cost, not stack
  (the stack is checked separately): walkers that render call text keep the 1,024-level default,
  and the call-graph walker follows the stack so long chains keep their head call.
- Enumerate the scanned tree through `SafeFileRead.EnumerateAllFilesSafe` (or filter through
  `PathExclusions.IsExcluded`), so `--exclude` globs (`PathExclusions`, an ambient scope the CLI
  applies per command) hold for every analyzer.
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
- Do not render an invocation's or receiver's text for every call without a cheap gate first
  (a file keyword check, the invoked name): the text of a call in a fluent chain spans the whole
  chain before it, so per-call rendering is quadratic in chain length.
- Preserve the set-backed membership guards in `TransparencyBuilder.BuildPackageReachability`
  (`PackageReachabilityAccumulator`): the `PackageReachability` lists keep insertion order for
  byte-stable output while hash sets beside them answer membership, so a purl holding tens of
  thousands of edge ids stays linear instead of quadratic.

## Validation checklist

Run before finishing changes:

```bash
dotnet test ./Dosai.sln
```

Building requires the .NET 11 SDK (11.0.x); a .NET 10 SDK cannot build the current target
frameworks. Run the suite on Windows as well when touching assembly loading or file I/O: file
locking and path semantics differ there and CI only covers Linux.

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
- Migration to schema 4.1.0 (additive; supersedes the unreleased 4.0.1): `docs/migration-4.1.0.md`
- Migration to schema 5.0.0 (.NET 11 / C# 15; additive output changes): `docs/migration-5.0.md`
- Migration to schema 5.1.0 (structured attribute arguments, TFM-aware analysis): `docs/migration-5.1.0.md`
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
