# Dosai test corpus

`Dosai.Tests/CorpusTests.cs` runs integration analysis against **real sample applications** kept
outside the repository in `~/sandbox/dosai-corpus`. The tests self-skip when the corpus is absent,
so CI environments without the corpus stay green — but new analysis features should be validated
against these apps before release.

## Setup

Run the pinned setup script — it clones both apps at the exact commits the corpus tests' numeric
floors were calibrated against, then builds them:

```bash
./Dosai.Tests/Corpus/setup.sh
```

Pinned commits (bump deliberately, and re-calibrate the pinned floors in `CorpusTests.cs` when
you do):

| App | Repo | Commit |
| --- | --- | --- |
| eShopOnWeb | dotnet-architecture/eShopOnWeb | `4da8212117e87d808d4bbc7da6286fd2147ce606` |
| practical-aspnetcore (orleans-1) | dodyg/practical-aspnetcore | `91fb02ba0ea97266c58c04fd0cadfdaa1b899022` |
| modular-monolith-with-ddd | kgrzybek/modular-monolith-with-ddd | `91c8ef24b4cb6ef558c95d8267fa07d68c7059f8` |
| grpc-dotnet (Grpc.Net.Client) | grpc/grpc-dotnet | `74de8cad36e5d4a987b44f78eb4ef9f2d60592f1` |

**Always build the sample apps before running the corpus tests** — several assertions rely on
restore metadata (`project.assets.json`) and the built assemblies under `bin/`. The tests SKIP
(not pass) when the corpus is absent, so CI runs without the corpus are visibly skipped rather
than silently green.

## Apps and what they cover

| App | Path | Exercises |
| --- | --- | --- |
| eShopOnWeb | `eShopOnWeb/src/Web` | R1 reachability index at scale, R7 call-site counts, F1 endpoint security findings, F5 config analysis (`appsettings.json`), S1 purl resolution from restore metadata, W-pack slices on real code, R9 crypto reachability |
| practical-aspnetcore `orleans-1` | `practical-aspnetcore/projects/orleans/orleans-1` | F2 Orleans provider: grain methods as `GrainMethod` entry points, `GetGrain<T>` outbound services, `rpc-message` taint seeds |
| modular-monolith-with-ddd | `modular-monolith-with-ddd/src` | A real .NET 8 LTS app: TFM detection from `Directory.Build.props`, `Metadata.TargetFrameworks = [net8.0]`, endpoint security and reachability at scale |
| grpc-dotnet `Grpc.Net.Client` | `grpc-dotnet/src/Grpc.Net.Client` | A real six-target multi-targeting library (`net462;netstandard2.0;netstandard2.1;net8.0;net9.0;net10.0`): representative-target guard resolution (`net10.0`), full TFM set surfaced |
