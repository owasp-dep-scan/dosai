# Migrating to schema 5.0.0

Schema 5.0.0 is the .NET 11 / C# 15 release. The output changes are **additive only**: every
4.1.0 consumer keeps working, and no field was removed, renamed, or retyped. The major version
marks the platform move — Dosai now targets `net11.0` and requires the .NET 11 SDK to build —
rather than a break in the JSON contract.

The one field every consumer sees change value is `Metadata.SchemaVersion`, which now reads
`5.0.0`. Consumers that pin an exact schema version must accept `5.0.0`; consumers that compare
`>= 4.1.0` need no change.

## Platform requirements

| Requirement          | 4.1.0     | 5.0.0                                                         |
| -------------------- | --------- | ------------------------------------------------------------- |
| Target framework     | `net10.0` | `net11.0`                                                     |
| SDK to build         | 10.0.x    | 11.0.x (a 10.0.x SDK cannot build this target)                |
| Runtime to run       | 10.0.x    | Bundled — published binaries are self-contained               |
| Analyzable languages | C# 14     | C# 15 (including union declarations), F# 11, VB.NET, R, C/C++ |

While .NET 11 is prerelease, the `FSharp.Compiler.Service`, `FSharp.Core`, and
`System.Reflection.MetadataLoadContext` references are pinned to the RC SDK's builds and move to
the stable releases at GA.

## New analysis coverage

None of the following adds a field; each populates existing collections with cases that were
previously missing or silently dropped.

### C# 15 unions

- Union declarations are parsed, inventoried in `Methods[]`, and present in the call graph.
  Before, a `union` declaration was a parse error, and the whole file disappeared from every
  result without a diagnostic.
- The data classifier expands a union into its case payload records, so a `credential` or `pii`
  member declared on a single case surfaces in `Services[].Request`/`Response` classifications
  even though the union itself declares no members.
- Types marked `[JsonPolymorphic]` or `[JsonUnion]` are treated as (de)serialization boundaries,
  and their interface-typed members are followed into concrete payload types.

### Pattern-bound taint (`dataflows`)

Taint now flows from a matched value into the locals a pattern binds — `case var message:`,
`case string message:`, recursive patterns over union cases and positional records
(`case Success(var message):`), list patterns, and the same forms in switch expressions and `is`
patterns. Slices whose sink consumes a pattern-bound payload were previously invisible.

This surfaces a new `DataFlowNode.Kind` value, `PatternBinding`, and a matching edge `Kind`.
Consumers that switch exhaustively on node or edge kinds must add a default branch or handle the
new value; the graph exporters emit it like any other kind. This applies to **all** pattern
matching, not only unions, so existing C# projects can gain slices on upgrade.

### C# 15 beyond unions

The rest of the C# 15 feature set parses and analyzes like ordinary code:

- `closed` hierarchies (`public closed record class GateState;`) with exhaustive switch arms;
  payload bindings in those arms receive taint exactly like union case payloads.
- Extension indexers (`extension(IEnumerable<int> s) { public int this[int i] => ...; }`) are
  inventoried, and indexer use sites resolve in the call graph to the lowered
  `extension(...).this[int]` member.
- Collection expression arguments (`[with(capacity: n), .. values]`) propagate element taint to
  the collection and through indexer reads (`names[0]`).
- Labeled `break`/`continue` (`outer: for (...) { continue outer; }`) do not disturb slice
  construction.
- The memory-safety preview shapes — `unsafe(...)` expressions in field initializers, and the
  pointer relaxations (`&x`, `fixed`, `stackalloc`, `sizeof` outside an `unsafe` context) — parse
  and inventory without requiring the updated safety rules.

Members declared in an `extension` block (C# 14 methods/properties, C# 15 indexers) are now
attributed to the enclosing static class in `Methods[].ClassName`. They are contained in a
compiler-synthesized nested type with no metadata name, so 4.1.0 reported an empty class name
for them.

### Crypto and CBOM

- The `Aes` key-wrap methods (`EncryptKeyWrap`, `DecryptKeyWrap`, `TryDecryptKeyWrap`,
  `GetKeyWrapLength`) classify as an `AES Key Wrap` asset: family `key-wrap`, strength `strong`,
  operation `key-wrap/unwrap`, standard `RFC 3394`. It is matched ahead of the generic `AES`
  branch so the key-wrap purpose is preserved.
- .NET's post-quantum algorithms classify as strong assets: `MLKem` → ML-KEM
  (family `key-agreement`, operation `key-agreement/encapsulate`, standard FIPS 203), `MLDsa` →
  ML-DSA (family `signature`, operation `sign`, standard FIPS 204), and `SlhDsa` → SLH-DSA
  (family `signature`, operation `sign`, standard FIPS 205). They appear in `Assets[]`,
  `Operations[]`, and the CBOM like any other primitive.
- The experimental caller-driven TLS session types in `System.Net.Security` (`TlsContext`,
  `TlsSession`, `TlsBufferSession`, `TlsSocketSession`, `TlsOperationStatus`) classify as TLS
  `protocol` assets and raise one new finding per line:

| Rule id                             | Severity | Meaning                                                        |
| ----------------------------------- | -------- | -------------------------------------------------------------- |
| `DOSAI-CRYPTO-EXPERIMENTAL-TLS-API` | `Low`    | Experimental TLS session API in use (diagnostic `SYSLIB5007`). |

Consumers that enumerate rule ids, or that fail a build on any new finding, should expect this
id. It is informational: the API works, its surface is not yet stable.

### F# 11

`FSharp.Compiler.Service` moves to the .NET 11-aligned release, so F# 11 syntax — record
spreads (`{ ...record; Field = v }`), record constructors (`Point(0, 0)` and
`Point(Y = 20, X = 10)`), direct delegate construction (`Func<...>(Calculator.Add)`), and the
efficient interpolated strings — parses instead of degrading to reduced coverage.

## Correctness fixes with output impact

The following bugs are fixed. All of them could **remove** or corrupt results in 4.1.0, so
5.0.0 can report more from an unchanged input:

- **Shared-framework probing order.** Framework references were probed in directory-enumeration
  order, so on a machine with several .NET runtimes installed the oldest usually won. Types
  referencing anything newer failed to load and were dropped from `Methods[]` with only a
  console warning — a machine with .NET 10 and 11 side by side lost every C# 15 union type,
  because unions implement `System.Runtime.CompilerServices.IUnion`, which exists only in .NET
  11's `System.Runtime`. Probing is now running-runtime-first, then newest-first.
- **Inspected assemblies are no longer locked.** Assemblies were memory-mapped from their path
  and stayed locked for the process lifetime, because unloading a collectible load context is
  asynchronous. On Windows this made an analyzed build output undeletable, so a caller could not
  scan its own output directory and then clean or replace it. Inspected assemblies are now
  loaded by value; shared-framework assemblies keep the mapped path, being immutable.
- **Assembly-mode taint survived no type conversion.** The IL interpreter dropped taint on
  `castclass`, `isinst`, `box`, `unbox`, `ldlen`, and `conv.*`, so every object-typed dispatch
  in compiled code was invisible: closed-hierarchy and union switch arms, `is`-pattern
  bindings, downcasts (`object o = args[0]; Process.Start((string)o);`), and `args.Length`
  arithmetic all produced no slice. Conversions now preserve the operand's taint,
  `add`-family arithmetic combines its operands' taint (matching source mode), and by-ref
  arguments receive their pointee's taint.
- **`out`/by-ref parameters did not write back in IL.** Positional patterns lower to a
  `Deconstruct` call with `ldloca` of the bound local; the local never received the payload
  taint. `ldloca`/`ldarga` now push an address marker and calls write the callee-side taint
  back through the addressed slots.
- **R lambda-assigned functions were invisible.** `name <- \(args) { ... }` (R 4.1+ shorthand)
  was not recognized as a function declaration by the fallback parser; its calls were
  attributed to the previous function.
- **R Markdown and Quarto documents were scanned as flat R.** Markdown prose (full of
  `word (paren)` shapes) and other engines' chunks (python, sql) produced phantom functions,
  calls, and dependencies. Only `` ```{r} `` chunks are analyzed now.
- **F# and R comment prose produced phantom calls.** `// Record constructors (F# 11)` and
  `# Lambda syntax (R 4.1+)` matched the call regexes. Comments are stripped (quote-aware,
  nested `(* *)` for F#) before extraction.
- **F# module functions after a `type` were misattributed.** A column-0 `let` following a type
  declaration is module-level; the line frontend now resets the class context so
  `Methods[].ClassName` is the module, not the preceding type. F# script directives (`#r
  "nuget: ..."`, `#load "file.fsx"`) are collected as `Dependencies[]`.
- **Hostile input directories crashed scans.** Recursive enumeration for sources, assemblies,
  framework files, and metadata references threw on unreadable or over-long subtrees (a
  `--path` pointing into a shared temp directory hit `PathTooLongException`). All discovery is
  now best-effort: it keeps what it gathered and reports a diagnostic.

## Nothing to change in queries or exports

Field names, identifier formats (`Metadata`, `SourceSignature`, method ids), severity and
confidence vocabularies, graph export attributes, the query language, and the CycloneDX property
names are unchanged from 4.1.0.
