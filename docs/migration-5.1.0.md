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
