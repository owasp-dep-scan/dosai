# Migrating to schema 5.0.0

Schema 5.0.0 is the .NET 11 / C# 15 release. The output changes are **additive only**: every
4.1.0 consumer keeps working, and no field was removed, renamed, or retyped. The major version
marks the platform move — Dosai now targets `net11.0` and requires the .NET 11 SDK to build —
rather than a break in the JSON contract.

The one field every consumer sees change value is `Metadata.SchemaVersion`, which now reads
`5.0.0`. Consumers that pin an exact schema version must accept `5.0.0`; consumers that compare
`>= 4.1.0` need no change.

## Platform requirements

| Requirement          | 4.1.0    | 5.0.0                                                        |
| -------------------- | -------- | ------------------------------------------------------------ |
| Target framework     | `net10.0`| `net11.0`                                                    |
| SDK to build         | 10.0.x   | 11.0.x (a 10.0.x SDK cannot build this target)               |
| Runtime to run       | 10.0.x   | Bundled — published binaries are self-contained              |
| Analyzable languages | C# 14    | C# 15 (including union declarations), F# 11, VB.NET, R, C/C++ |

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

### Crypto and CBOM

- The `Aes` key-wrap methods (`EncryptKeyWrap`, `DecryptKeyWrap`, `TryDecryptKeyWrap`,
  `GetKeyWrapLength`) classify as an `AES Key Wrap` asset: family `key-wrap`, strength `strong`,
  operation `key-wrap/unwrap`, standard `RFC 3394`. It is matched ahead of the generic `AES`
  branch so the key-wrap purpose is preserved.
- The experimental caller-driven TLS session types in `System.Net.Security` (`TlsContext`,
  `TlsSession`, `TlsBufferSession`, `TlsSocketSession`, `TlsOperationStatus`) classify as TLS
  `protocol` assets and raise one new finding per line:

| Rule id                            | Severity | Meaning                                                            |
| ---------------------------------- | -------- | ------------------------------------------------------------------ |
| `DOSAI-CRYPTO-EXPERIMENTAL-TLS-API`| `Low`    | Experimental TLS session API in use (diagnostic `SYSLIB5007`).     |

Consumers that enumerate rule ids, or that fail a build on any new finding, should expect this
id. It is informational: the API works, its surface is not yet stable.

### F# 11

`FSharp.Compiler.Service` moves to the .NET 11-aligned release, so F# 11 syntax — record
spreads (`{ ...record; Field = v }`) among them — parses instead of degrading to reduced
coverage.

## Correctness fixes with output impact

Two loader bugs are fixed. Both could **remove** results in 4.1.0, so 5.0.0 can report more from
an unchanged input:

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

## Nothing to change in queries or exports

Field names, identifier formats (`Metadata`, `SourceSignature`, method ids), severity and
confidence vocabularies, graph export attributes, the query language, and the CycloneDX property
names are unchanged from 4.1.0.
