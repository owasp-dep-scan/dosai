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
- Extension indexers (`extension(IEnumerable<int> s) { public int this[int i] => ...; }`) parse, and
  indexer use sites resolve in the call graph to the lowered
  `extension(...).this[int]` member. In source mode the accessor is not an inventory entry, since
  indexers do not appear in `Methods[]` or `Properties[]`; assembly mode lists the lowered
  `get_Item`. Either way the call graph node reports the enclosing static class rather than the
  synthesized container's empty name.
- Collection expression arguments (`[with(capacity: n), .. values]`) propagate element taint to
  the collection and through indexer reads (`names[0]`).
- Labeled `break`/`continue` (`outer: for (...) { continue outer; }`) do not disturb slice
  construction.
- The memory-safety preview shapes — `unsafe(...)` expressions in field initializers, and the
  pointer relaxations (`&x`, `fixed`, `stackalloc`, `sizeof` outside an `unsafe` context) — parse
  and inventory without requiring the updated safety rules. The `safe` contextual keyword on an
  `extern` member (`safe static extern int Read();`) and on fields of an explicit-layout struct
  parses too, and the members are inventoried like ordinary fields and methods.

### Conditional compilation (`#if`) resolves against a modern .NET target

Both the C# parse options and the F# line frontend define the modern-net symbol family - `NET`,
`NET11_0`, and the `NETx_0_OR_GREATER` chain down to .NET 5 - while `DEBUG`/`TRACE` and the
legacy `NETFRAMEWORK`/`NETSTANDARD` families stay undefined: a Release-shaped build against the
latest .NET target. `#if NET8_0_OR_GREATER` guards are near-universal in real libraries, and
parsing with an empty define set turned their bodies into disabled text invisible to
`Methods[]`, `MethodCalls[]`, and the data-flow walker. A missed sink in a guarded branch is
worse for a scanner than a declaration the analyzed project's own target would not compile.

### File-based apps (`dotnet run app.cs`)

- The `#:` directives (`#:property`, `#:package`, `#:include`, `#:sdk`, `#:project`) parse as
  trivia under the `FileBasedProgram` parser feature instead of reporting CS9298; without it the
  whole file risked disappearing from every result. The top-level statements report the
  compiler-synthesized `<Main>$`, and trailing type declarations (including unions) are
  inventoried normally.
- A `#:package Id@Version` directive surfaces in `Dependencies[]` with namespace `nuget` and
  module `FileBasedApp`, because a file-based app declares its NuGet references nowhere else.

### .NET 11 process-launch sinks

The new `System.Diagnostics.Process` launch APIs are `command` sinks in both source and
assembly mode, so a tainted argument cannot bypass analysis by switching API:

- `Process.Run` (a `Contains` match that also covers `RunAsync`, `RunAndCaptureText`, and
  `RunAndCaptureTextAsync`),
- `Process.StartAndForget`, and
- `SafeProcessHandle.Start`.

Members declared in an `extension` block (C# 14 methods/properties, C# 15 indexers) are now
attributed to the enclosing static class in `Methods[].ClassName`, in `MethodCalls[].ClassName` and
`CallerClass`, and in `CallGraph.Nodes[].ClassName` with its `Identity.ClassName`. They are
contained in a compiler-synthesized nested type with no metadata name, so 4.1.0 reported an empty
class name for them.

### Crypto and CBOM

- The `Aes` key-wrap methods (`EncryptKeyWrap`, `DecryptKeyWrap`, `TryDecryptKeyWrap`,
  `GetKeyWrapLength`) classify as an `AES Key Wrap` asset: family `key-wrap`, strength `strong`,
  operation `key-wrap/unwrap`, standard `RFC 3394`. It is matched ahead of the generic `AES`
  branch so the key-wrap purpose is preserved.
- The padded key-wrap variants (`EncryptKeyWrapPadded`, `DecryptKeyWrapPadded`,
  `TryDecryptKeyWrapPadded`, `GetKeyWrapPaddedLength`, RFC 5649) classify the same way with
  standard `RFC 5649`; the word-boundary token match stopped at the unpadded prefix before, so
  these calls fell through to the generic `AES` classification.
- `X25519DiffieHellman` (.NET 11) classifies as an `X25519` asset: family `key-agreement`,
  strength `strong`, operation `key-agreement`, standard `RFC 7748`, and joins the CBOM like
  ECDH.
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

The F# line frontend additionally follows the F# 11 compiler's preprocessor semantics:

- `#elif` (new in F# 11) is recognized, and conditional regions (`#if`/`#elif`/`#else`/
  `#endif`, with `!`, `&&`, `||`, and parentheses in conditions) contribute declarations and
  calls only from the branches the analysis define set selects - the same modern-net,
  Release-shaped set the C# parse options define (see the conditional-compilation section
  above). Inactive text previously leaked phantom functions and calls into `Methods[]` and
  `MethodCalls[]`.
- `#:`-prefixed file-based app directive lines are ignored wherever they appear (FS-1337);
  before, `#:property ...` lines minted phantom `property` calls.
- Type-level record spreads (`type Labeled = { ...Config; Label: string }`), anonymous record
  spreads (`{| ...config; Label = "x" |}`), and nested dotted updates
  (`{ ...service; Opts.Host = host }`) extract their functions without phantom calls.

### ASP.NET Core 11 endpoints and taint

- `[ShortCircuit]` (on a minimal-API lambda handler or an MVC action) does not hide the
  endpoint from `ApiEndpoints[]`.
- Inline lambda handlers (`app.MapPost("/pets", (PetUnion pet) => ...)` — including .NET 11's
  union-typed JSON bodies) now seed their bound parameters as HTTP taint sources, like method
  groups always did. Infrastructure parameters (`CancellationToken`, `HttpContext`,
  `ClaimsPrincipal`, `IServiceProvider`, `ILogger*`, `[FromServices]`, and `I`-prefixed
  interfaces) stay excluded. Taint from a deserialized union payload reaching a sink is now a
  slice where it was previously invisible.

### R 4.4–4.6 syntax

The R fallback parser handles the null-coalescing `%||%` operator (R 4.4+), `%notin%` (R 4.6+),
and the `declare()` primitive (R 4.4+) without minting phantom function names from the infix
operator tokens.

### Unbuilt-tree reference resolution and honest degradation

Unbuilt checkouts no longer silently lose package calls. Two additive mechanisms close the gap
that left a restored-but-unbuilt tree indistinguishable from a package that is only imported:

- **Restore output as reference assemblies.** `project.assets.json` `packageFolders` plus each
  target's `compile` entries name the package DLLs in the NuGet packages cache; every
  metadata-reference builder now adds them (unpinned, from bytes), so semantic binding does not
  need `bin/` output. Call edges stay `SourceRoslynDirect` and package reachability stays
  `ExternalCallGraphNode`/High.
- **`SourceUnresolved` evidence.** When the target assembly still is not available, the call
  site — previously dropped entirely — is recorded from syntax with the new
  `SourceUnresolved` evidence kind (score 1, below every direct kind) and a
  `Unresolved:<name>` target id. A package purl is attributed only when the receiver's own
  qualification states the namespace (`Newtonsoft.Json.JsonConvert.SerializeObject`); names
  recovered from `using` directives are deliberately not guessed, because attributing an
  unresolved call to whatever package the file imports would fabricate reachability for
  innocent packages. Affected packages gain a `ConfidenceReasons` entry ("Package assemblies
  were not available…") and `Diagnostics[]` counts the failed call sites and what the
  restore output resolved. Only sites whose receiver or created type genuinely failed to
  resolve are recorded: a resolved receiver that fails overload resolution (for example
  through a poisoned argument) keeps its candidates and is a downstream symptom of a
  different missing reference.
- **Implicit global usings are honored.** Compiling analyzed source without the project's
  MSBuild context used to drop every BCL name an `ImplicitUsings` project relies on —
  `Path`, `File`, `Console`, LINQ — because the SDK-injected `global using`s were absent.
  When any `csproj` or `Directory.Build.props`/`.targets` under the scanned path enables
  `ImplicitUsings`, a synthetic global-usings tree joins the compilation. On the Dosai
  self-scan this alone recovered thousands of previously invisible call edges. The decision
  is per scan root (`global using`s are compilation-wide and one compilation covers every
  scanned file): in a mixed monorepo, files from classic sibling projects also receive the
  synthetic usings, so a BCL name their own compiler would reject can bind there — accepted
  as the rarer failure mode versus silently dropping the enabling projects' calls.
- **`--restore` / `--build`** CLI flags (methods, dataflows, crypto, agent-context) run the
  corresponding `dotnet` command before analysis. Opt-in, because executing MSBuild from the
  target repository is a trust decision; failures and timeouts leave analysis running as-is.

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
  back through the addressed slots. Reads and writes through those markers (`ldind`/`ldobj`,
  `stind`/`stobj`) follow the pointee, and `ldelema` forwards the array's taint because an array
  element has no slot. The write-back is deliberately over-approximate: IL does not record which
  parameter a callee copied into which `out` slot, so each by-ref slot receives the union of the
  arguments and receiver.
- **R lambda-assigned functions were invisible.** `name <- \(args) { ... }` (R 4.1+ shorthand)
  was not recognized as a function declaration by the fallback parser; its calls were
  attributed to the previous function.
- **R Markdown and Quarto documents were scanned as flat R.** Markdown prose (full of
  `word (paren)` shapes) and other engines' chunks (python, sql) produced phantom functions,
  calls, and dependencies. Only ` ```{r} ` chunks are analyzed now.
- **F# and R comment prose produced phantom calls.** `// Record constructors (F# 11)` and
  `# Lambda syntax (R 4.1+)` matched the call regexes. Comments are stripped (quote-aware,
  nested `(* *)` for F#) before extraction. All three F# string forms are recognised - normal,
  verbatim (`@"...\"`), and triple-quoted - and the multi-line forms carry state across lines, so
  their body is no longer read as code.
- **F# module functions after a `type` were misattributed.** A `let` at or left of the enclosing
  `type` declaration's indentation is module-level - in flat scripts and in indented `module M =`
  bodies alike; the line frontend now resets the class context so `Methods[].ClassName` is the
  module, not the preceding type. F# script directives (`#r "nuget: ..."`, `#load "file.fsx"`)
  are collected as `Dependencies[]`.
- **Hostile input directories crashed scans.** Recursive enumeration for sources, assemblies,
  framework files, and metadata references threw on unreadable or over-long subtrees (a
  `--path` pointing into a shared temp directory hit `PathTooLongException`). All discovery is
  now best-effort: an unreadable subtree is skipped while enumeration continues with its
  siblings, so only the offending subtree is lost instead of everything after it. Linked
  directories are still followed - only a link that re-enters a directory already walked is
  skipped - so a symlinked source tree keeps contributing results. The metadata reference sweep
  and assembly discovery report the skipped directory in `Diagnostics[]`; the remaining discovery
  sites warn on stderr, leaving stdout to the MCP server's JSON-RPC stream.

## Nothing to change in queries or exports

Field names, identifier formats (`Metadata`, `SourceSignature`, method ids), severity and
confidence vocabularies, graph export attributes, the query language, and the CycloneDX property
names are unchanged from 4.1.0.

One corrective exception: `crypto --format cyclonedx` now emits schema-valid CycloneDX 1.6.
The previously invalid `bomRef` key became `bom-ref`, the document declares `$schema`, and
`cryptographic-asset` components carry `cryptoProperties.assetType` (plus
`algorithmProperties.primitive` / `protocolProperties.type` where the evidence maps). The
`dosai:crypto:*` property names are unchanged, and consumers of the native `--format dosai`
output are not affected.
