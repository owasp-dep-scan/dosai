# Lesson 3. Analyzing a compiled app without source

## Learning objective

In this lesson we publish a small application, discard the source, and recover inventory, call graph, and data-flow evidence from the binary alone. This is the workflow for vendor DLLs, legacy deployments, and review-from-artifacts situations.

## Prerequisites

```text
.NET SDK 8.0 or newer
The Dosai repository cloned locally
```

## Build the target and hide the source

Use the command-injection app from [lesson 1](LESSON1.md), publish it as a framework-dependent build, and keep the output directory as your analysis target:

```bash
dotnet publish /tmp/injector -c Release -o /tmp/injector-publish
```

The publish directory contains `injector.dll`, `injector.pdb`, and `injector.deps.json`. All three matter: the DLL carries the IL, the portable PDB carries sequence points and local names, and the deps file scopes which assemblies belong to the application. You can now analyze the binary without ever touching the source again.

## Inventory from metadata

```bash
dotnet run --project ./Dosai/Dosai.csproj -- methods \
  --path /tmp/injector-publish \
  --o /tmp/injector-methods.json
```

The `Methods[]` records now identify methods through assembly metadata, and the call graph edges carry IL evidence. Query the evidence kinds to see what the binary pass observed:

```bash
dotnet run --project ./Dosai/Dosai.csproj -- query \
  --input /tmp/injector-methods.json \
  --query 'packages[reachable=true]' \
  --o /tmp/reachable.json
```

For this app the reachable set includes `pkg:nuget/System.Diagnostics.Process`, attributed to the call graph edge into `Process.Start`.

## Data flow from IL

```bash
dotnet run --project ./Dosai/Dosai.csproj -- dataflows \
  --path /tmp/injector-publish \
  --o /tmp/injector-bin-dataflows.json \
  --print
```

The same cli-to-command slice appears, reconstructed from instruction operands rather than syntax:

```text
└─ DataFlow dfs1: cli → command (Medium)
   Summary: cli data reaches command sink Start.
   Stack (3 frames, 3 transitions):
     at Source/cli args [dfn1] in Program.cs:4:5
        symbol: Injector.Program.Main(System.String[])
     via VariableAssignment [dfe1] label=command
     at Sink/command Start [dfn3] in Program.cs:6:9
        symbol: System.Diagnostics.Process.Start(string)
```

With the PDB present, frames carry real file, line, and column from sequence points and local names come from PDB scopes. Without a PDB the frames fall back to assembly and IL offset locations, which is less friendly but still correct.

## What happens under the hood

```text
        injector.dll                    injector.deps.json
   ┌───────────────────┐               ┌─────────────────────┐
   │ IL method bodies  │               │ application scope   │
   │ opcodes + blobs   │               │ filters framework   │
   │ branch targets    │               │ internals out       │
   └─────────┬─────────┘               └──────────┬──────────┘
             │                                    │
             ▼                                    ▼
   ┌───────────────────────────────────────────────────────┐
   │ bounded worklist over instructions                    │
   │ follows branch, switch, fallthrough, exception region │
   │ successors; replays parameter-to-sink summaries       │
   └─────────┬─────────────────────────────────────────────┘
             │
             ▼
   ┌───────────────────┐     ┌───────────────────────────┐
   │ metadata symbols  │◀───▶│ portable PDB              │
   │ for pattern match │     │ sequence points, locals   │
   └───────────────────┘     └───────────────────────────┘
             │
             ▼
     slices with AssemblyIlDirect evidence
```

A few properties of the IL pass are worth knowing when you interpret results. Sink arguments get stable labels such as `arg0` or `receiver` when no source expression exists in IL. Compiler-generated async, iterator, and display-class fields are pre-seeded so taint crosses `await` boundaries. Catch and filter handlers receive exception-object state, so flows that reach a sink from an exception path are still followed. And `Code` source patterns from a custom rules file apply only to IL string literals, because metadata names are the only reliable comparison surface in a binary.

## Combined source and binary review

A common real-world shape is a repository with source for some projects and published output for others. Point `--path` at the parent directory and both frontends run; records merge through stable method identities, and each record's evidence kind tells you whether it was observed directly, summarized, inferred, or reconstructed. When the same method appears from both sides, the merged record keeps the strongest evidence rather than duplicating the finding.

## Honesty about the limits

Binary analysis cannot recover what was never emitted: source-level `Code` patterns, comments, and preprocessor shapes do not exist in IL. Dispatch through interfaces and virtual methods is approximated with shared candidate sets for instantiated application types, and those candidate edges are marked as inferred evidence, never as direct calls. Treat binary slices as strong triage input, and confirm high-impact findings against source when source exists.

## Try next

[Lesson 4](LESSON4.md) stays in binary-friendly territory and turns crypto usage into a CycloneDX-style CBOM.
