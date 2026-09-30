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

## Guards resolve per project; `Metadata.ProjectGuardTargetFrameworks` (behavioral, additive)

Each C# and F# file's `#if` arms now resolve against its **nearest project** at or above it within
the scan root (the project's own target framework, else the nearest `Directory.Build.props`, else
`Directory.Build.targets`), instead of one target for the whole tree. In a tree holding a net48
project and a net8.0 project, the net48 project's `#if NETFRAMEWORK` members appear again and the
net8.0 project keeps its `#if NET8_0` members. Files outside every project, and files of a project
whose target is an MSBuild property reference (`$(NetCoreAppCurrent)`), resolve against
`GuardTargetFramework` as before. A single-file scan (`--path src/App/Program.cs`) reads its
project from the file's directory, so its guards, `TargetFrameworks` and `GuardTargetFramework`
follow that project instead of the latest-modern-net fallback.

On a tree with more than one project whose target is readable,
`Metadata.ProjectGuardTargetFrameworks` lists each project (path relative to the scan root,
`/`-separated), its detected targets and the one its files' guards used, sorted by path; the
multiple-targets `Diagnostics` note says the evaluation was per project. Single-project trees
leave it null.

```json
"ProjectGuardTargetFrameworks": [
  { "Project": "Legacy/Legacy.csproj", "TargetFrameworks": ["net48"], "GuardTargetFramework": "net48" },
  { "Project": "Modern/Modern.csproj", "TargetFrameworks": ["net8.0"], "GuardTargetFramework": "net8.0" }
]
```

## Field-like `Events[]` resolve their event symbol (behavioral)

A field-like event (`public event EventHandler Changed;`) is declared by its variable declarator,
which Roslyn resolves to an event symbol; the previous field-symbol cast was always null, so every
such record reported the compilation's name (`Dosai.SourceAnalysis.CSharp`) as `Namespace`, no
`ImplementedInterfaces` and the compilation's assembly and module. They now carry the event's
real namespace, containing type, interfaces, assembly and module, like `add`/`remove` events.

## Reference-assembly sources and duplicate declarations (behavioral, `Diagnostics`)

Reference-assembly source - GenAPI API-surface stubs whose bodies only `throw null` (or return
`null`/`default`), or that carry the `aka.ms/api-review` header, such as dotnet/runtime's `ref/`
folders - no longer compiles its redeclarations of types a non-stub file of the tree also
declares. Compiling both declared every member twice, and calls on those members bound
ambiguously and differently between runs. A stub that declares nothing else is left out whole; a
stub that also declares types no other file does is compiled with only those, on their original
lines, so calls into API surface that has no implementation in the tree still bind. The
redeclarations no longer contribute `Methods[]`, `Properties[]` or other inventory records (they
duplicated the implementation's), and `Diagnostics` entries name how many stubs were skipped and
how many trimmed, with examples. A stub folder scanned on its own is analyzed as before. Types still declared by more than one file without all declarations being partial
(per-platform or per-target variants compiled together) get a `Diagnostics` entry of their own.
The same partition applies to `dataflows` and `crypto`.

## Framework references in self-contained builds (behavioral, `Diagnostics`)

The self-contained single-file `-full` binaries analyzed with no framework metadata references
at all on any machine without a shared framework at least as new as their own, so every BCL call
was `Unresolved:` and framework purls were wrong or Low. They now reference their bundled
runtime; any build falls back to the newest installed shared framework of any version, with a
`Diagnostics` note when that is older than Dosai's own. When no framework reference can be
resolved at all, `Diagnostics` says so and the unresolved-call note no longer recommends
restoring the tree. The restore hint "Found project.assets.json but no package assemblies were
resolved" now appears only when the assets file declares packages.

Every build now references the same framework: the shared framework's assemblies, including the
`System.Private.*` implementations its facades forward to (without them a bundled build left
`Uri`, `XmlDocument`, `XDocument` and `DataContractSerializer` unresolved). A non-bundled run
(`dotnet run`, the test host) no longer references Dosai's own dependencies as well - Roslyn,
System.CommandLine, FSharp.Compiler.Service - which bound an unrestored tree's calls into those
packages to Dosai's copies; released builds never did.

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
