# Migrating to schema 4.0.1

Schema 4.0.1 is **additive only**: every 4.0.0 consumer keeps working. New optional fields carry
the new analyses; two heuristic behaviors changed where they were provably wrong.

## New optional fields

### `methods` output (`MethodsSlice`)

| Field                               | Type                 | Meaning                                                                                                                                                                                                                                                                     |
| ----------------------------------- | -------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `Reachability`                      | `NodeReachability[]` | Per-node facts (R1): `ReachableEntryPoints` (bounded id set), `ReachableNodeBucket` (1/10/100/1000/10000), `DepthFromEntryPoint` (min distance, null when unreachable), `FanIn`/`FanOut`, `SccId`/`InRecursiveCycle` (R3).                                                  |
| `RecursionClusters`                 | `RecursionCluster[]` | Strongly-connected recursion clusters (R3) with bounded member lists.                                                                                                                                                                                                       |
| `SecurityFindings`                  | `SecurityFinding[]`  | F1 endpoint security findings (sensitive unauthenticated endpoints, CORS wildcard+credentials, missing antiforgery, duplicate-route auth mismatch, mass-assignment hints), S2 MCP transport integrity, F5 configuration security. Severity-tagged with CWE and remediation. |
| `Diagnostics`                       | `string[]`           | Framework diagnostics and reachability budget notices.                                                                                                                                                                                                                      |
| `MethodCallEdge.CallSiteCount`      | `int`                | Distinct call sites after same-pair edge collapsing (R7); repeated `(source, target, callType, evidence)` edges collapse into one counted edge whose argument/evidence annotations are merged.                                                                              |
| `MethodCallEdge.DispatchConfidence` | `string?`            | `exact` (sealed receiver or sole instantiated implementation), `rta-candidate`, or `cha-candidate` (R4).                                                                                                                                                                    |
| `MethodNode.GenericInstantiation`   | `string?`            | Original instantiated IL id when an instantiated-generic node merged onto its source original definition (R10).                                                                                                                                                             |

Graph exports (`--callgraph-format graphml|gexf`) gain matching node attributes
(`reachableEntryPoints`, `minDepthFromEntryPoint`, `fanIn`, `fanOut`, `inRecursiveCycle`,
`genericInstantiation`) and edge attributes (`callSiteCount`, `dispatchConfidence`).

### `dataflows` output (`DataFlowResult`)

| Field                                                   | Type              | Meaning                                                                                                                                                                                                                                                                                                                  |
| ------------------------------------------------------- | ----------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `SanitizedFlows`                                        | `SanitizedFlow[]` | Negative evidence (T4): would-be flows suppressed by a sanitizer expression (`SanitizerMatch`) or a validation guard (`SanitizerGuard`), with the sanitizer symbol, location, and `RemovesTaintKinds`.                                                                                                                   |
| `ExploitChains`                                         | `ExploitChain[]`  | Graph-derived route → call path → taint slice → sink chains (R2) with derived `Exposure` (`anonymous-http`, `authenticated-http`, `anonymous-rpc`, `queue`, `mcp`, `cli`). Also populates `DangerousApiReachability.EntryPointIds` and upgrades the linked `WeaknessCandidate.ConfidenceReasons` with the concrete path. |
| `DataFlowSlice.Severity` / `WeaknessCandidate.Severity` | `string`          | `info                                                                                                                                                                                                                                                                                                                    | low | medium | high`defaulted per sink category; pattern`Severity` overrides. |
| `DataFlowPattern.Severity`                              | `string?`         | Optional per-pattern severity override.                                                                                                                                                                                                                                                                                  |

## Behavioral changes

- **Suppression** (T5): `dataflows` and `agent-context` accept `--suppress <file>` (file+line,
  sliceKey, weaknessId, or category entries with optional `expires`). An entry matches only when
  **every** field present in the entry matches (AND semantics), so `{"file": "A.cs", "line": 12}`
  cannot suppress line 12 of a different file; expired entries resurface the finding.
- **Diff** (O1): `diff` gains severity-aware slice deltas plus entry-point, weakness, package, and
  `RiskDelta` sections (see [commands](./commands.md)). `SecurityFinding` ids are content-derived
  (kind+file+line), so diffs and suppression keys stay stable as findings appear and disappear.
- **PURL resolution** (S1): `packages.lock.json`, `paket.lock`, `packages.config`, and direct
  `.csproj` `<PackageReference>` entries are read in order of trust when restore output is absent;
  version conflicts across sources are recorded as diagnostics, and `PackageUrlResolver`
  exposes `ResolutionFacts` (name, version, purl, source, confidence).
- **Crypto reachability** (R9): the whole-file fallback no longer silently marks crypto usage in
  endpoint-bearing files as reachable — reachability claims require a graph path, and the gated
  fallback emits a diagnostic naming the file.
- **Validator booleans** (T12): boolean-returning calls (validators like `IsValid`/`TryParse`)
  no longer propagate argument taint to their result — booleans are decisions, not payloads.
- **Pattern hygiene** (W7): the bare `Add` sanitizer and the substring `key`/`secret` crypto
  sources are replaced by precise forms — bare `key`/`keys` names mint nothing; only compound
  identifiers (`apiKey`, `client_secret`, `session_key`, `hmacKey`, …) do (see
  [pattern packs](./pattern-packs.md)). Storing a tainted value into a collection now taints the
  collection. XXE hardening is recognized both inline (spacing-tolerant) and across statements
  (`var s = new XmlReaderSettings(); s.XmlResolver = null;`).
- **Source-mode summaries** (T1/T13): summaries iterate to a fixpoint, so wrapper→wrapper→sink
  chains attribute to the outermost method, and `TaintKinds` are populated on summaries.
- **Lambdas and async** (T2/T3): lambda parameters passed to calls over tainted receivers/arguments
  are seeded; delegate invocations propagate argument taint; `await` keeps the async boundary
  visible in slices.
- **Budget diagnostics** (T11): IL interpreter/summary budget exhaustion and deep-nesting walker
  caps emit diagnostics instead of truncating silently.
- **Static ReDoS findings** (W5b): catastrophic literal regex patterns (`new Regex("(a+)*$")`,
  `Regex.IsMatch(subject, "literal")`, `[GeneratedRegex("literal")]`) are reported as
  `ReDoSCandidate` weaknesses (CWE-1333, severity medium, with file/line). `RegexOptions.NonBacktracking`
  and an explicit match-timeout argument suppress the finding; patterns longer than 200 characters
  get a review-needed diagnostic instead of a silent skip.
- **Severity vs confidence** (T5): a slice matched by a Low-confidence pattern is demoted one
  severity rank, so heuristic matches never trip a "new high-severity" CI gate on their own.
- **F1 precision**: the antiforgery finding skips Razor Pages handlers (they validate
  antiforgery automatically) and apps registering a global
  `AutoValidateAntiforgeryToken` filter; CORS wildcard+credentials analysis resolves the policy
  registration through its symbol and reports the outermost registration only.
