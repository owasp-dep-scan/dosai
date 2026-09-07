# Migrating to schema 4.1.0

Schema 4.1.0 is additive only: every 4.0.0 consumer keeps working. New optional fields carry the
reachability, weakness, exploitability, supply-chain, and query analyses, and several heuristics
changed where they were provably wrong. Each of those changes is listed under
[behavioral changes](#behavioral-changes) below.

> Schema 4.0.1 was developed but never released. Everything once described in its migration note
> ships in 4.1.0, and that note has been folded into this document, so there is nothing to migrate
> through in between. Go straight from 4.0.0 to 4.1.0.

## New optional fields

### `methods` output (`MethodsSlice`)

| Field                               | Type                 | Meaning                                                                                                                                                                                                                                                                     |
| ----------------------------------- | -------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `Reachability`                      | `NodeReachability[]` | Per-node facts: `ReachableEntryPoints` (bounded id set), `ReachableNodeBucket` (1/10/100/1000/10000), `DepthFromEntryPoint` (min distance, null when unreachable), `FanIn`/`FanOut`, `SccId`/`InRecursiveCycle`.                                                            |
| `RecursionClusters`                 | `RecursionCluster[]` | Strongly-connected recursion clusters with bounded member lists.                                                                                                                                                                                                            |
| `SecurityFindings`                  | `SecurityFinding[]`  | Endpoint security findings (sensitive unauthenticated endpoints, CORS wildcard plus credentials, missing antiforgery, duplicate-route auth mismatch, mass-assignment hints), MCP transport integrity, and configuration security. Severity-tagged with CWE and remediation. |
| `Diagnostics`                       | `string[]`           | Framework diagnostics and reachability budget notices.                                                                                                                                                                                                                      |
| `MethodCallEdge.CallSiteCount`      | `int`                | Distinct call sites after same-pair edge collapsing; repeated `(source, target, callType, evidence)` edges collapse into one counted edge whose argument and evidence annotations are merged.                                                                               |
| `MethodCallEdge.DispatchConfidence` | `string?`            | `exact` (sealed receiver or sole instantiated implementation), `rta-candidate`, or `cha-candidate`.                                                                                                                                                                         |
| `MethodNode.GenericInstantiation`   | `string?`            | Original instantiated IL id when an instantiated-generic node merged onto its source original definition.                                                                                                                                                                   |
| `DeadCode`                          | `DeadCodeEntry[]`    | Source-declared methods and constructors that no entry point reaches and that no reflection or DI evidence keeps alive, with file and line. Empty for assembly-only inputs; suppressed, with a diagnostic, when the reachability budget was exhausted.                      |
| `NodeReachability.Reachable`        | `bool`               | Exact forward-reachable-from-any-entry-point flag. Unlike `ReachableEntryPoints`, it never saturates at 16 entries.                                                                                                                                                         |
| `NodeReachability.KeepAlive`        | `bool`               | Unreachable but retained: reflection, DI, or framework-model evidence (for example an `AddSingleton<Foo>()` registration or an `Activator.CreateInstance` call) proves runtime reachability, so the node stays out of `DeadCode`.                                           |
| `NodeReachability.KeepAliveReasons` | `string[]`           | Which evidence kinds kept an unreachable node alive.                                                                                                                                                                                                                        |

Graph exports (`--callgraph-format graphml|gexf`) gain matching node attributes
(`reachableEntryPoints`, `minDepthFromEntryPoint`, `fanIn`, `fanOut`, `inRecursiveCycle`,
`genericInstantiation`) and edge attributes (`callSiteCount`, `dispatchConfidence`).

### `dataflows` output (`DataFlowResult`)

| Field                                                   | Type                    | Meaning                                                                                                                                                                                                                                                                                                                |
| ------------------------------------------------------- | ----------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `SanitizedFlows`                                        | `SanitizedFlow[]`       | Negative evidence: would-be flows suppressed by a sanitizer expression (`SanitizerMatch`) or a validation guard (`SanitizerGuard`), with the sanitizer symbol, location, and `RemovesTaintKinds`.                                                                                                                      |
| `ExploitChains`                                         | `ExploitChain[]`        | Graph-derived route, call path, taint slice, and sink chains with a derived `Exposure` (`anonymous-http`, `authenticated-http`, `anonymous-rpc`, `queue`, `mcp`, `cli`). Also populates `DangerousApiReachability.EntryPointIds` and upgrades the linked `WeaknessCandidate.ConfidenceReasons` with the concrete path. |
| `DataFlowSlice.Severity` / `WeaknessCandidate.Severity` | `string`                | One of `info`, `low`, `medium`, `high`, defaulted per sink category; a pattern's `Severity` overrides it.                                                                                                                                                                                                              |
| `DataFlowPattern.Severity`                              | `string?`               | Optional per-pattern severity override.                                                                                                                                                                                                                                                                                |
| `AttackSurface`                                         | `AttackSurfaceGroup[]`  | Entry points grouped by exposure (`anonymous-http` first), each entry with linked weakness counts, CWEs, sink categories, and exploit-chain counts. Also rendered as a section in the `report` markdown and in `agent-context`.                                                                                        |
| `AgentContext.AttackSurface`                            | `AttackSurfaceGroup[]`  | Bounded copy (at most 10 groups) of the same view for agent consumption.                                                                                                                                                                                                                                               |
| `AttackSurfaceGroup.EntryPointsTruncated`               | `bool`                  | True when the group's `EntryPoints` rows are capped below `EntryPointCount`. The rollup counts always cover the whole group; only the row list is capped.                                                                                                                                                              |
| `DataFlowMethodSummary.OutParameterIndexes`             | `int[]`                 | Indexes of the `out`/`ref` parameters the callee writes. By-value parameter assignments are excluded because reassigning a by-value parameter never reaches the caller.                                                                                                                                                |
| `DataFlowMethodSummary.OutSourceCategories`             | `{ [index]: string[] }` | Source categories flowing into each out/ref parameter, keyed by parameter index, so a source reaching one out parameter cannot mint taint for a sibling that only receives a literal.                                                                                                                                  |

## Behavioral changes

### Entry points, reachability, and dead code

- **Entry points for modern CLIs**: top-level statements (the default `dotnet new console`
  template) now produce a method inventory entry for the compiler-synthesized `<Main>$`, a `Cli`
  entry point whose `MethodId` matches the call-graph node, and `args` taint seeding. Previously
  such programs yielded 0 methods, 0 entry points, 0 slices, and 0 exploit chains. Declared `Main`
  variants (`async Task`, `Task<int>`, `int`) already worked and are now covered by tests. In
  `dataflows` output, `Cli` entry points carry a `MethodId`.
- **Dataflow entry-point dedup**: `dataflows` no longer rebuilds entry points from provider-owned
  endpoints, which had produced a second, MethodId-less copy of every framework endpoint (methods
  mode already deduplicated). `result.EntryPoints.Count` halves on framework-heavy apps; exploit
  chains and weakness linkage are unaffected.
- **Constructor node ids in the global namespace**: inventory constructor nodes for
  global-namespace types now use the edge id format (`X..ctor()`) instead of
  `<global namespace>.X..ctor()`, removing duplicate unreachable twin nodes, which is the false
  positive the dead-code report surfaced.
- **Crypto reachability**: the whole-file fallback no longer silently marks crypto usage in
  endpoint-bearing files as reachable. Reachability claims require a graph path, and the gated
  fallback emits a diagnostic naming the file.

### Taint analysis

- **Source-mode summaries**: summaries iterate to a fixpoint, so chains of wrappers that end in a
  sink attribute to the outermost method, and `TaintKinds` are populated on summaries.
- **Lambdas and async**: lambda parameters passed to calls over tainted receivers or arguments
  are seeded; delegate invocations propagate argument taint; `await` keeps the async boundary
  visible in slices.
- **`ref`/`out` taint**: a call with any tainted argument or receiver taints its out/ref locals
  (`int.TryParse(tainted, out var v)`), and a callee summary that writes an out parameter from a
  pattern-matched source mints that source at the call site, for that parameter index only. A
  sibling out parameter that only ever receives a literal stays clean, and calls with clean
  arguments and no source-writing summary for that index mint nothing.
- **Collection and element taint**: `foreach` loop variables inherit the collection's taint;
  element and indexer stores (`arr[i] = tainted`, `dict[k] = tainted`) taint the container.
  Ordinary property stores keep field-sensitive member taint keys, so `obj.Tag = tainted` does
  not taint `obj.Other`.
- **Validator booleans**: boolean-returning calls (validators like `IsValid`/`TryParse`) no
  longer propagate argument taint to their result, because booleans are decisions, not payloads.
- **Budget diagnostics**: IL interpreter and summary budget exhaustion and deep-nesting walker
  caps emit diagnostics instead of truncating silently.

### Findings, severity, and suppression

- **Suppression**: `dataflows` and `agent-context` accept `--suppress <file>` with file plus line,
  sliceKey, weaknessId, or category entries and an optional `expires` date. An entry matches only
  when every field present in the entry matches (AND semantics), so `{"file": "A.cs", "line": 12}`
  cannot suppress line 12 of a different file. Expired entries resurface the finding.
- **Severity versus confidence**: a slice matched by a Low-confidence pattern is demoted one
  severity rank, so heuristic matches never trip a "new high-severity" CI gate on their own.
- **Static ReDoS findings**: catastrophic literal regex patterns (`new Regex("(a+)*$")`,
  `Regex.IsMatch(subject, "literal")`, `[GeneratedRegex("literal")]`) are reported as
  `ReDoSCandidate` weaknesses (CWE-1333, severity medium, with file and line).
  `RegexOptions.NonBacktracking` and an explicit match-timeout argument suppress the finding.
  Patterns longer than 200 characters get a review-needed diagnostic instead of a silent skip.
- **Pattern hygiene**: the bare `Add` sanitizer and the substring `key`/`secret` crypto sources
  are replaced by precise forms. Bare `key`/`keys` names mint nothing; only compound identifiers
  (`apiKey`, `client_secret`, `session_key`, `hmacKey`, and similar) do. See
  [pattern packs](./pattern-packs.md). Storing a tainted value into a collection now taints the
  collection. XXE hardening is recognized both inline (spacing-tolerant) and across statements
  (`var s = new XmlReaderSettings(); s.XmlResolver = null;`).
- **Endpoint finding precision**: the antiforgery finding skips Razor Pages handlers (they
  validate antiforgery automatically) and apps registering a global
  `AutoValidateAntiforgeryToken` filter. CORS wildcard plus credentials analysis resolves the
  policy registration through its symbol and reports the outermost registration only.

### Tooling surface

- **Diff**: `diff` gains severity-aware slice deltas plus entry-point, weakness, package, and
  `RiskDelta` sections (see [commands](./commands.md)). `SecurityFinding` ids are content-derived
  (kind, file, and line), so diffs and suppression keys stay stable as findings appear and
  disappear.
- **Query engine**: collection paths may be nested (`callGraph.nodes[isExternal=false]`),
  filter terms combine with `||` inside a `&&` conjunct, `sort by <prop> [desc]` orders results,
  and a trailing `count` returns `{"count": n}` instead of the array. New aliases:
  `exploitChains`, `sanitizedFlows`, `reachability`, `recursionClusters`, `deadCode`,
  `attackSurface`, `securityFindings`, `methods`, `callGraph`. See
  [query language](./query-language.md).
- **MCP tools**: `dosai.exploit_chains`, `dosai.attack_surface`, and `dosai.reachability`
  join the tool list; `dosai.reachability` accepts an optional `nodeId` argument. All remain thin
  wrappers over one analyzer entry point and respect `--mcp-root` confinement.
- **PURL resolution**: `packages.lock.json`, `paket.lock`, `packages.config`, and direct
  `.csproj` `<PackageReference>` entries are read in order of trust when restore output is
  absent. Version conflicts across sources are recorded as diagnostics, and `PackageUrlResolver`
  exposes `ResolutionFacts` (name, version, purl, source, confidence).
