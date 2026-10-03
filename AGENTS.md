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
  project's detected target framework (`TargetFrameworkDetection.ForFile` feeding the per-file
  `CSharpSourceParser` parse options and the F# line frontend's `#if`/`#elif` tracking). A file
  resolves against its nearest project (`.csproj` for C#, `.fsproj` for F#) at or above it
  within the scan root - that project's own target, else the nearest `Directory.Build.props`,
  else `Directory.Build.targets` - and against the scan root's detection when it sits outside
  every project or its project's target is an MSBuild property reference. Each
  detected TFM contributes what its own build defines (`net8.0` -> `NET`, `NET8_0`, the
  `NET5_0_OR_GREATER` chain, the `NETCOREAPP` family; `netstandard2.0` and `net472` -> their
  families), a multi-target project resolves to one representative target (the most modern it
  declares - never the union of all of them, which would hide every `#if !SYMBOL` arm), and with
  no detectable TFM the fallback is the historical `FrameworkPreprocessorDefines.ModernNet` set
  (`NET`, `NET11_0`, the modern chain) so bare-directory scans behave as before. Detected TFMs
  surface in `Metadata.TargetFrameworks`, the root-wide representative as
  `Metadata.GuardTargetFramework`, each project's decision on a multi-project tree in
  `Metadata.ProjectGuardTargetFrameworks`, and the fallback appends a `Diagnostics` note. `DEBUG`/`TRACE` stay undefined - a
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
- The per-file symbol-analysis loop in `GetSourceMethods`, the dispatch index's
  object-creation scan and the parse itself run on a worker team of dedicated large-stack
  threads (`DedicatedStack.ForEach`; one per processor, capped by
  `Dosai.MaxSymbolAnalysisWorkers` / `DOSAI_SYMBOL_ANALYSIS_WORKERS`). Workers claim the next
  file as they free up. The per-file body must stay file-pure: everything it produces goes into
  that file's `SourceFileSymbols` collector (counts included), and everything it reads must be
  immutable, or memoized thread-safely with a result that does not depend on which thread
  computed it. Collectors merge in file order afterwards, which is what keeps the output
  byte-identical for every worker count - do not add shared mutable state, counters or merges
  inside the loop. Parse fills a tree array by index, so tree order stays the file order of a
  sequential parse.
- Every string rendered from a Roslyn symbol during source analysis goes through the per-run
  `SourceRenderCache` (signatures, display strings, interface-name lists). It is keyed by
  symbol reference through a `ConditionalWeakTable` on purpose: Roslyn symbol hash codes are
  not cached on the symbol (each hash walks the containing-symbol chain), so a hash-keyed
  table slows the worker loop, and a strongly-keyed table pins every symbol - including
  per-callsite constructed generics - for the whole run. Render through the cache and never
  re-render per call site. Symbol-keyed tables that must compare by symbol equality (the
  dispatch index's buckets and lookup memo) are per compilation and are dropped as soon as
  the symbol loop has merged.
- Method ids come from `Dosai.FormatMethodSignature` (source rendering through
  `SourceRenderCache.Signature`). Anonymous and local functions are named after their declaring
  member by `Dosai.NestedFunctionName` (`<Run>lambda2`, `<Run>Helper`); a lambda's symbol name
  is empty and a local function's only unique in its scope, so never build a call-graph id or
  node name from `IMethodSymbol.Name` directly - use `SourceRenderCache.MemberName`.
- Graph de-duplication and ordering go through `GraphAssembly`: an `EdgeSiteKey` struct
  instead of a concatenated key string per edge, stable in-place sorts instead of `OrderBy`
  chains. Dedupe the call record before building the edge. String legs of the key compare by
  reference first, so route edge endpoint ids through the render cache (single instance per
  distinct member) rather than fresh strings. Same-pair collapsing
  (`ReachabilityAnalyzer.CollapseDuplicateCallSites`) groups through an open-addressing table
  of group ids keyed by `CollapseKey` (no key stored per edge) and sorts only the distinct
  keys; the collapsed list comes out sorted by (source, target, call type, evidence kind) in
  exactly the old concatenated-key order (`GraphAssembly.CompareCollapseKeys` - the separator
  emulation matters: a strict-prefix continuation below U+001F sorts before the whole shorter
  field, and rendered ids do contain CR/LF/tab). Each grouping stage runs in its own
  `NoInlining` frame so only the placement arrays survive into the merge loop. Keep `ValueTuple`
  scratch keys free of nullable items: tuple hashing boxes them. `ReachabilityAnalyzer.Compute`
  builds its forward adjacency and FanIn counts by run-length over that order; an edge list in
  any other order is sorted as a copy (`SortedByCollapseKey`), never in place, so the facts do
  not depend on the caller's edge order.
- Never reuse a per-group scratch `HashSet`/`List` across millions of groups without a cap:
  `Clear()` is O(capacity), so one group with a thousand call sites taxes every later group
  with that capacity (issue #70's second hotspot). Drop and re-grow scratch above ~64 entries.
- The assembly IL call graph (`AssemblyCallGraphAnalyzer.Analyze`) decodes each assembly on the
  worker team (`DedicatedStack.ForEachInOrder`, same worker knob as symbol analysis) into an
  `AssemblyFragment`, and fragments merge in assembly order. The per-assembly body may touch only
  its fragment and its `AssemblyScan` caches; everything shared (`AnalysisContext`: known methods
  by assembly path, the token lookup) is read-only. A fragment node is the in-order replay of
  that assembly's `AddNode` calls, and `MergeFragmentNode` folds it in with the same
  first-value-wins/AND/append rules, so any new node field needs a merge rule that keeps that
  equivalence. Call-site keys dedupe inside the assembly while it is analyzed and across
  assemblies at the merge (`GraphAssembly.EdgeSiteIndex`, chunked keys, no giant reference
  arrays). Resolve members through `AssemblyScan.Resolve` (memoized per token, so call sites
  share one id string) and source locations through the per-assembly source map (binary search,
  one path and file-name string per document) - never per instruction.
- Phase order in `BuildMethodsSlice` is a memory contract: framework analysis and the security
  analyzer run immediately after source analysis (they are the only compilation consumers),
  then the compilations are dropped before the assembly IL call graph, enrichment,
  reachability and serialization - the syntax trees are the largest object the pipeline holds,
  and pinning them through those phases is the memory wall on very large trees (issue #65).
  Anything new that needs a `SemanticModel` must run before that release, inside
  `AnalyzeSourcesAndFrameworks`: that helper frame is what makes the release real. Never hold
  a compilation, the framework context, or a lambda capturing either in `BuildMethodsSlice` -
  it runs once at Tier-0, where every IL local stays live until it returns
  (`GetMethodsSlice_ReleasesSourceCompilationsBeforeTheIlPhase` checks this).
- The process runs with server GC (`Dosai.csproj`): parse, dispatch index and symbol analysis
  are wide parallel allocators, and workstation GC's single collector thread fell behind them
  on large trees (`DOTNET_gcServer=0` opts a host back into workstation GC).
  `DOSAI_DEBUG_GC=1` forces a full collection before each `--debug` phase-end heap read so
  figures compare without GC-timing noise.
- Virtual/interface dispatch resolution goes through `DispatchResolver.SourceIndex`: concrete
  types bucketed once per interface/base-type original definition, lookups memoized per
  (target, receiver) symbol pair. Route any new dispatch inference through the index rather
  than scanning types. Instantiation evidence is symbol-exact (the created type's symbol and,
  for constructed generics, its original definition); the string aliases this replaced also
  matched same-named types in other namespaces and promoted them to RTA rank. The scan reads
  the created type with `GetTypeInfo`, which binds the creation's whole enclosing statement,
  so it skips members `OperationDepthGuard.IsSafe` rejects exactly like the operation walkers;
  a creation whose constructor fails to bind still counts.
  `FindImplementationForInterfaceMember` takes the member as its
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
  applies per command) hold for every analyzer. Always parse with the scan root
  (`CSharpSourceParser.Parse(text, file, rootPath)`): the nearest-project walk is bounded by
  it and memoized per directory. Never go back to recursive TFM detection per source
  directory - one recursive enumeration per distinct directory, and a project's
  subdirectories fell back to the latest-modern-net set. A single-file root reads its project
  context from the file's directory (`TargetFrameworkDetection.ProjectContextRoot`), for the
  parse options, the implicit-usings decision and the reported target frameworks alike.
- Seed every Roslyn compilation of analyzed source from `FrameworkReferences.Current`, never
  from `typeof(object).Assembly.Location` or `TRUSTED_PLATFORM_ASSEMBLIES` directly: a
  self-contained single-file Dosai has neither (issue #67), and the provider falls back to the
  bundled runtime's in-memory metadata (names embedded at build time by the
  `EmbedFrameworkAssemblyNames` target) and then to the newest installed shared framework.
  Every build references the same set: trusted platform assemblies are filtered to the core
  library's directory (a non-bundled host also lists Dosai's own dependencies), and the bundled
  names are followed into the `System.Private.*` implementations their facades forward to.
  Surface `FrameworkReferences.Diagnostic` in the command's diagnostics. The
  `smoke-self-contained` CI job runs a published `-full` build with no `dotnet` reachable.
- Partition parsed C# trees through `ReferenceSources.Partition` before creating a
  compilation (methods, data-flow and crypto all do): reference-assembly source (GenAPI
  API-surface stubs, `throw null` bodies or the `aka.ms/api-review` header) loses its
  redeclarations of types implemented elsewhere in the tree - dropped whole when it declares
  nothing else, otherwise trimmed by blanking those declarations' tokens so the types left keep
  their lines - and `ReferenceSources.Diagnostics` reports skipped and trimmed stubs and any
  type still declared non-partially by more than one file (issue #69: the duplicate members
  bound ambiguously and differently between runs). Read a compiled tree's text from the tree,
  not from `tree.FilePath`: a trimmed stub's file still holds what the compilation left out. The compiler's full
  declaration-error histogram is `--debug`-only: that pass costs more than half again of the
  symbol loop, and `--debug` must not change the JSON.
- Synthetic syntax trees (the implicit-usings tree) have no file behind them: never read a
  tree back from disk by `tree.FilePath` without excluding them.
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
- Tutorials: `docs/LESSON1.md` through `docs/LESSON12.md`
- Threat model: `docs/THREAT_MODEL.md`
- blint integration: `docs/BLINT-INTEGRATION.md`
- YARA usage: `docs/YARA-USAGE.md`
- Security reporting: `SECURITY.md`
