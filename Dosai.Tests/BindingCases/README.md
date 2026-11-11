# Target framework binding cases

One case per target framework, each holding the same `Probe.cs` and a project file that pins the
framework. `TargetFrameworkBindingTests` analyzes every case and compares the call sites against
that case's `expected.json`. A change that binds one framework differently from the rest fails on
that case alone.

The cases reference no package and have no restore output, which is what lets them run anywhere
`dotnet test` runs, with no network and no build. Adding a `PackageReference` would make the
expectations depend on a restore the test does not perform.

## expected.json

`targetFramework` is the value `Metadata.TargetFrameworks` must hold. Each entry under `calls` is
one record of the slice's `MethodCalls`, with its `LineNumber`, `CallerMethod`, `Namespace`,
`ClassName`, `CalledMethod`, `EvidenceKind` and `Purl`. The comparison is over the whole list. An extra call
fails the case as surely as a missing one.

To see what a case produces now:

```bash
dotnet run --project ./Dosai/Dosai.csproj -- methods   --path ./Dosai.Tests/BindingCases/net10.0   --o /tmp/net10.json
```
