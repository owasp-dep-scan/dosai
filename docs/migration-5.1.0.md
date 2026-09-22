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
`System.Collections.ObjectModel.ReadOnlyCollection\`1[...]`. It now produces the same structured
encoding as the source path, verified agreeing on a built .NET 8 assembly.

## `Metadata.TargetFrameworks` and `Metadata.SchemaVersion` (additive)

`Metadata.TargetFrameworks` lists the target frameworks detected for the scan root (null when
nothing was detected; a `Diagnostics` note marks that fallback). `Metadata.SchemaVersion` is
`"5.1.0"`.

## Containment (behavioral)

A malformed attribute on one symbol no longer aborts the scan: that symbol's attribute list
degrades to empty and a message is appended to `Diagnostics`. An unhandled exception in any CLI
command writes the exception to stderr, returns a non-zero exit code, and notes when the output
file already holds a partial result.
