# Migration to schema 5.1.0

Schema 5.1.0 ships with the target-framework-aware analysis (issue #56 follow-up). The changes
are additive except for one restructured property; v5 is a new major, so the restructure was made
under the breaking-changes-allowed policy instead of keeping a lossy encoding.

## `CustomAttributes[].ConstructorArguments` is restructured (breaking)

`ConstructorArguments` was `List<string>`: one comma-joined string per constructor argument, with
array arguments flattened. It is now `List<CustomAttributeArgumentInfo>`, a lossless structured
form:

```json
{
  "Name": "InlineDataAttribute",
  "ConstructorArguments": [
    {
      "Type": "object?[]",
      "IsArray": true,
      "IsNull": true
    }
  ]
}
```

- A null constant — `[Attr(null)]`, including a null array reference — sets `IsNull`; `Value` and
  `Elements` are null. Reading these was the issue-#56 crash: `FormatTypedConstant` threw
  `NullReferenceException` on them and one attribute aborted the whole scan.
- An array constant sets `IsArray` and lists its elements in `Elements`, in source order. An
  empty array is an empty list, distinct from `IsNull`. Elements recurse, so jagged arrays nest.
- A scalar constant carries its text in `Value` (formatted invariantly).
- `Type` carries the argument type's display string (`"string[]"` from the Roslyn source path,
  `"System.String[]"` from the assembly/reflection path — the two paths agree on everything
  else by test).

`IsArray` and `IsNull` are nullable and omitted when false, so scalar output stays compact.

This restructure is what makes `[Attr(null)]`, `[Attr]`, `[Attr("")]`, and
`[Attr(new object?[] { null })]` distinguishable — impossible in the old comma-joined string,
where a null array, an empty array, and a wrapped empty string all collapsed to `""`.

## `NamedArguments[].Value` can be null (additive)

`[Attr(Name = null)]` now serializes as `"Value": null` instead of the empty string. The property
is annotated `JsonIgnore(Condition = Never)` so it is still emitted when null (the serializer's
`WhenWritingNull` default would silently drop it). Verified against the emitted JSON.

## The assembly/reflection path now matches the source path (fix)

Assembly-only analysis used to stringify array-valued attribute arguments as
`System.Collections.ObjectModel.ReadOnlyCollection\`1[...]`. Constructor arguments now produce the
same structured encoding as the source path, verified agreeing on a built .NET 8 assembly.

Named arguments keep their scalar string shape on both paths: a null named argument is now a real
null, but an array-valued named argument still flattens comma-joined, so a null element there is
not distinguishable from an empty string. Array-valued named arguments are rare - the issue-#56
shapes (`[InlineData(null)]`, `[DataRow(null)]`, `[Routes(null)]`) are all constructor arguments.

## `Metadata.TargetFrameworks`, `Metadata.GuardTargetFramework` and `Metadata.SchemaVersion` (additive)

`Metadata.TargetFrameworks` lists every target framework detected for the scan root (null when
nothing was detected; a `Diagnostics` note marks that fallback).
`Metadata.GuardTargetFramework` names the single one of them that conditional-compilation guards
were evaluated against - the most modern target a multi-target project declares. It is reported
separately because it, not the list, decides which `#if` arms appear in the results; when a
project declares several targets, a `Diagnostics` note repeats the choice.
`Metadata.SchemaVersion` is `"5.1.0"`.

Consumers that group or diff results by target should read `GuardTargetFramework`, not the first
entry of `TargetFrameworks` - the list is in declaration order, which is frequently oldest-first.

## Containment (behavioral)

A malformed attribute no longer aborts the scan, on either pipeline: the affected symbol's
(source) or member's (assembly) attribute list degrades to empty and a message is appended to
`Diagnostics`. On the assembly path this previously cost every remaining type in that file,
because the only handler was per-assembly. An unhandled exception in any CLI
command writes the exception to stderr, returns a non-zero exit code, and notes when the output
file already holds a partial result.

## Locale-independent output (fix, issue #63)

Output no longer depends on the locale of the machine running Dosai. Under a Turkish locale,
`Attributes` and parameter `Type` title-cased `internal` and `int` as `İnternal` and `İnt`; under
a Swedish locale, data-flow `Summary` text rendered a negative argument index with U+2212. Graph
sidecars and CBOM properties, `query` number comparisons (a German locale read `7.5` as 75) and
the `--debug` timestamps were locale-dependent in the same way. Consumers that normalized these
values can drop the normalization; everything except the per-run timestamps and CBOM serial
number is byte-identical across locales.

## Call-graph dispatch (behavioral, issues #64 and #65)

- A call to a generic method declared on a generic interface (`IBuilder<T>.Join<TEntity>()`) no
  longer aborts `methods` with `InvalidOperationException`; its implementations get their
  `SourceRoslynVirtualCandidate` edges like any other interface call.
- `base.M()` (VB `MyBase.M()` / `MyClass.M()`) no longer gets a dispatch-candidate edge to
  overrides: it is a non-virtual call and runs exactly the bound method.
- A call site with 32 or more candidate implementations kept only the first candidate; it now
  keeps 16, instantiated types first, like a call site with fewer candidates.
- A type implementing two constructions of one generic interface (`IHandler<int>` and
  `IHandler<string>`) yields its candidate edge once per call site instead of once per
  construction.
- Should Roslyn throw while resolving a candidate, the scan continues without that candidate and
  `Diagnostics` gains `Dispatch resolution failed at N call site(s), first <file>:<line> (...)`.
- A source file with an upper-case extension (`Program.CS`) is analyzed; it was parsed but its
  members never reached the inventory.

Symbol analysis also runs on one worker per processor (`DOSAI_SYMBOL_ANALYSIS_WORKERS` caps it);
the output is identical for every worker count.
