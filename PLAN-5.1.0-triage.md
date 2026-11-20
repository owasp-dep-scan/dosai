# Issue triage plan for Dosai 5.1.0

This document assembles, onto branch `fix/5.1.0-analysis-triage`, the triage of the seven issues
reported by August Johansson against Dosai 5.0.0 at main `4f5f5b0`: #120, #127, #128,
#129, #130, #131 and #134. Every issue was reproduced on a real fixture under
`~/sandbox/dosai-corpus` and independently confirmed at `4f5f5b0` by a second pass that did
not share the reproducer's assumptions. The fixtures are `issue120-serilog`, `issue127-tfm`,
`issue128-libpacks`, `issue129-edgeids`, `issue130-initcalls`, `issue131-initflows` and
`issue134-weakhash`; the generator script of each is folded into
`Dosai.Tests/Corpus/setup.sh`, and the README table there records what every fixture
contains.

Each issue was worked up with a reproducer and an independent checker before any plan was
drafted, so every measured number quoted below was seen twice. Five implementation plans
were then written, one per issue cluster, adversarially reviewed and revised; the review
findings are already folded into the plans as they are embedded here, not listed separately.
A cross-cutting review of all five set the landing order recorded under "Execution order".
The plans follow as Parts 1 to 5, ordered by the landing position of their first issue, with
their internal headings demoted one level and their content otherwise verbatim.

## Triage

| Issue | Title | Milestone | Fixture | Size |
| --- | --- | --- | --- | --- |
| #120 | `--dependency-flows reachable` drops application flows whose sink only interface dispatch reaches | v5.1.0 Lodestar | `issue120-serilog` | L |
| #127 | `methods` reports .NET 11 assemblies for a net10.0 project | v5.1.0 Lodestar | `issue127-tfm` | M |
| #128 | `reachable` analyzes unreachable package bodies because every assembly's `<>c` is one key | v5.1.0 Lodestar | `issue128-libpacks` | M |
| #129 | `methods` lists PackageReachability EdgeIds that are not in CallGraph.Edges | Backlog | `issue129-edgeids` | S |
| #130 | `methods` records no calls made directly in field and auto-property initializers | v5.1.0 Lodestar | `issue130-initcalls` | M |
| #131 | `dataflows` finds no flow from initializers or expression bodies of an unbuilt tree | Backlog | `issue131-initflows` | L |
| #134 | `crypto` misses weak hashes outside block bodies of the one-shot hash APIs | v5.1.0 Lodestar | `issue134-weakhash` | S |

## Execution order

The cross-cutting review set one landing order for all seven issues, and the parts of this
document follow it.

1. Issue #127 lands first, because it changes framework binding for every tree with a
   detectable target and adds a base-naming diagnostic to every command's reports. That
   re-baselines the measured numbers every later plan recorded at `4f5f5b0`, so the
   re-measurement happens once, here, instead of once per plan.
2. Issue #134 lands second, with its corpus diagnostics pin written against #127 having
   landed: it asserts that no diagnostic originates from the crypto pass rather than that
   `Diagnostics` is empty, because #127 adds a note even to project-less trees.
3. Issue #130 lands before issue #131, because #131 reuses #130's constructor-attribution
   helper and its `.cctor()` id parity.
4. Issue #128 lands before issue #120, because #120's dispatch following fires keys built
   from #128's qualified type names, and the reporter's expected post-fix numbers for #120
   assume those keys are already in.
5. Issue #120 lands fifth, and it carries a corpus measurement obligation: the dependency
   bodies analyzed under `--dependency-flows reachable` are counted before and after the
   change, measured across the corpus apps (`eShopOnWeb/src/Web`,
   `modular-monolith-with-ddd/src`, `issue78-mailkit`, `issue107-mailkit`,
   `issue120-serilog/App`, `issue128-libpacks/main`), and the after values are pinned in its
   tests.
6. Issue #131 lands sixth: its Backlog milestone trails the v5.1.0 Lodestar items, and it
   depends only on #130.
7. Issue #129 lands last, per its Backlog milestone and because nothing else depends on it.

Corpus test numbers are re-baselined after #127 lands: each later part's pinned assertions
are updated to the post-#127 measurements before that change merges, and each plan states
this again where it applies.

## Part 1: Framework reference binding follows the tree's target framework (issue #127)

Branch `fix/5.1.0-analysis-triage` off main 4f5f5b0 (Dosai 5.0.0), milestone v5.1.0 Lodestar. The 5.1.0 theme is already TFM-aware analysis (docs/migration-5.1.0.md), and this defect is the reference-binding half of that theme: the framework metadata a compilation starts from ignores the tree's target unless the tree also declares a second shared framework.

### Reproduction (verified)

The fixture at `/Users/appthreat/sandbox/dosai-corpus/issue127-tfm` (subdirs `plain`, `detected`, `legacy`, `net11`, `ns2`) was scanned with `methods` at 4f5f5b0 on this machine and the recorded JSON under its `out/` directory re-read while writing this plan. Every project holds the same `Probe.cs` (`StringBuilder.Append` plus `Console.WriteLine`).

| Fixture | Target | `StringBuilder.Append(string?)` binds to | `Console.WriteLine(string?)` binds to | Base actually used |
| --- | --- | --- | --- | --- |
| plain | net10.0, no FrameworkReference | System.Private.CoreLib, Version=11.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e | System.Console, Version=11.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a | Dosai's own runtime, silently |
| detected | net10.0 + FrameworkReference Microsoft.AspNetCore.App | System.Runtime, Version=10.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a | System.Console, Version=10.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a | base pack Microsoft.NETCore.App 10.0.15 from the NuGet cache (debug line: "base framework Microsoft.NETCore.App 10.0.15 for net10.0 (exact major)", 167 references) |
| legacy | net48, restore-only | System.Private.CoreLib, Version=11.0.0.0 | System.Console, Version=11.0.0.0 | Dosai's own runtime, silently |
| net11 | net11.0 | System.Private.CoreLib, Version=11.0.0.0 | System.Console, Version=11.0.0.0 | Dosai's own runtime, correct (same major) |
| ns2 | netstandard2.0 | System.Private.CoreLib, Version=11.0.0.0 | System.Console, Version=11.0.0.0 | Dosai's own runtime, silently |

The `plain` and `legacy` runs carry no framework-reference diagnostic at all: `--debug` logs no pack line, and the JSON `Diagnostics` hold only the unrelated built-from-source or restore entries. The surprise the reproduction established: `dotnet restore` of the net48 project auto-pulled `Microsoft.NETFramework.ReferenceAssemblies.net48` 1.0.3 into `~/.nuget/packages` (restore marks the `Microsoft.NETFramework.ReferenceAssemblies` dependency `autoReferenced: true`), and the package sits there unused, with 133 reference DLLs under `build/.NETFramework/v4.8/` plus 104 more under `build/.NETFramework/v4.8/Facades/` (`mscorlib.dll` at the top level, `System.Runtime.dll` among the facades, no `System.Private.*` name anywhere). The reporter's Windows observation completes the picture: a tree holding net48-with-UseWindowsForms beside net10.0 resolved one base for both (10.0.14, exact major for the root representative) and bound the net48 project's `TimeSpan` against it, while `Metadata.ProjectGuardTargetFrameworks` already reports both projects' own targets.

### Root cause

All paths are in the worktree at 4f5f5b0.

- `Dosai/FrameworkReferences.cs:150-154`: `ResolveForTree` returns the process-wide set as soon as `TreeFrameworks.Detect` finds nothing, so a tree that declares no shared framework never reaches the base-pack branch at all. This is why `plain` binds .NET 11.
- `Dosai/FrameworkReferences.cs:216-233`: the base-pack branch sits after that return and is keyed on the target's major differing from Dosai's own runtime (`:218`, `:223-224`), not on detection having happened. The doc promise in `docs/compiler-engineering.md` ("a net9.0 web tree ... binds against the 10.x base and ASP.NET Core packs, a consistent pair") only holds when something else triggered detection.
- `Dosai/FrameworkReferences.cs:305-322`: `TryParseTargetMajor` reads the digit run after `net`, so `net48` arrives as major 48 and `net472` as 472, which passes the `>= 5` gate at `:161` despite the comment at `:159-160` saying that cannot happen. `ResolvePackDirectory`'s nearest-major fallback (`:393-401`, `SelectNearestMajor` at `:442-458`) then hands net48 an 11.x pack, which the major check at `:223-224` discards, and control lands in the `else` at `:234-239`: the base stays Dosai's runtime with a note that talks about a "48.x Microsoft.NETCore.App reference pack", a pack that cannot exist.
- `Dosai/TreeFrameworks.cs:313-326`: `Claim` drops `Microsoft.NETCore.App` before recording any framework. That split is deliberate (the base is `ForTree`'s job) and must survive this fix; the fix is to resolve a base from the detected target, not to count the base as a detected framework.
- `Dosai/CSharpSourceParser.cs:287-326`: `FrameworkPreprocessorDefines.TryParseVersion` already parses the digit run correctly elsewhere (`48` to 4.8, `472` to 4.7.2, first digit below 5 splitting into minor and patch digits).
- The wrong binding is silent: notes exist only on the detection-driven paths (`:164`, `:238`), and `Dosai/Dosai.cs:2909-2912` (with `Dosai/DataFlow.cs:1603-1608` and `Dosai/CryptoAnalysis.cs:1591-1596` doing the same) adds `ForTree`'s diagnostic only when one exists.
- The string `ReferenceAssemblies` appears nowhere under `Dosai/`; nothing reads the .NET Framework targeting pack the SDK restore already fetched.

### Design decisions

**Question 1: should a tree that declares no other shared framework reach the base-pack branch, and should the base follow each project's own target or stay one per scan root?**

Yes, it must reach the branch: the early return at `FrameworkReferences.cs:150-154` goes away, and the base is resolved from the tree's root-wide representative target whenever one is detectable, with or without a second shared framework. The base-pack branch was built for exactly this ("same-major trees already run on matching references", `:216-217`) and only the accidental ordering hides it from plain trees.

The base stays one per scan root. Reasons. AGENTS.md fixes one reference set per tree: `BuildMethodsSlice` creates a single CSharpCompilation for the whole tree (`Dosai.cs:2884-2888`) and `CompilationReferenceSet` keeps exactly one reference per assembly simple name, so two bases could never coexist in one set anyway: their assemblies share every simple name (`System.Runtime` 4.0 beside 10.0), and per-project bases would mean per-project compilations with per-file assignment, a structural change to the memory contract (compilations are dropped before the IL phase) and to every issue #65, #81 and #113 invariant, far beyond this defect. The codebase already has the right precedent for a tree-wide choice with per-project differences: conditional compilation resolves per file against each project's own target while the root-wide representative is reported (`Metadata.GuardTargetFramework`, `Metadata.ProjectGuardTargetFrameworks`, the diagnostic at `Dosai.cs:674`). Binding takes the same shape: one base, chosen by `FrameworkPreprocessorDefines.TrySelectRepresentative` over `TargetFrameworkDetection.Detect(root)` (the most modern target, so a mixed net48+net10 tree keeps a .NET 10 base exactly as today), plus a new diagnostic that names the net4x projects whose own target the base does not match and points at `Metadata.ProjectGuardTargetFrameworks`. `TargetFrameworkDetection.ForFile` per-file targets are a parse-options concern and stay out of binding.

**Question 2: should a net4x target read the .NET Framework reference assemblies where restore has put them in the NuGet cache, or keep binding against Dosai's own runtime with today's diagnostic?**

Read them from the NuGet cache. Restore fetches the package automatically and verifiably (verified on this machine, and the reporter saw the same in the SDK container), the layout is fixed (`microsoft.netframework.referenceassemblies.net48/<version>/build/.NETFramework/v4.8/` plus `Facades/`), and it is what the SDK itself compiles net4x against, so `net48` source binds `mscorlib` 4.0.0.0 instead of .NET 11 metadata that a net48 build would reject (the reporter's `File.CreateHardLink` CS0117 example binds today). When the package is absent, keep degrading to the process-wide set with a corrected diagnostic (naming the package and the runtime base actually used), because a missing cache package must stay a diagnostic, never a failure. A net4x pack owns the whole set the way a base reference pack owns the corlib, only stronger: `mscorlib` is the corlib, so the process-wide fill at `FrameworkReferences.cs:263-279` is skipped entirely (a .NET 11 assembly beside net48 `mscorlib` redeclares or forwards types into a corlib the compilation does not have, and the `System.Private.*` skip at `:271-273` alone does not cover the non-Private names), and the net4x pack has no `System.Private.*` file to exclude in the first place. On a net4x tree the detected modern shared frameworks (WindowsDesktop via `UseWindowsForms`) are not layered: a .NET 11 desktop pack beside net48 `mscorlib` duplicates everything, and the net4x pack already carries `System.Windows.Forms.dll` and the WPF assemblies; the skip is reported. Issue #113's corlib rules are honored by construction: nothing named `System.Private.*` enters from the pack, the fill that could bring Dosai's own copies does not run, and `CompilationReferenceSet`'s own guards stay as they are for the tree's build and restore output.

Two supporting decisions. A net4x target resolves no `Microsoft.NETCore.App` major at all: family "net" with version major 4 or lower is its own branch, so `ResolvePackDirectory` is never asked for a 4x base pack and the "No 48.x pack" wording disappears. A `netstandard` (or `netcoreapp`, or undetectable) representative keeps the process-wide set, but with a diagnostic naming that base, instead of today's silence.

### Change plan

1. `Dosai/FrameworkReferences.cs:305-322`: replace `TryParseTargetMajor` with a parse that yields a `FrameworkVersion`, delegating to `FrameworkPreprocessorDefines.TryParseVersion`; both the method and the `FrameworkVersion` it returns become `internal` (`Dosai/CSharpSourceParser.cs:287-326` and `:372`; the type is `private` today, so an internal method alone leaves `FrameworkReferences.cs` unable to name the return type). `TryParseTargetMajor` has the single call site at `FrameworkReferences.cs:161`. `net48` parses to 4.8, `net472` to 4.7.2, `net8.0` to 8.0; the representative moniker keeps its raw form and `MonikerOf` (`:297-303`) keeps stripping the platform suffix.
2. `Dosai/FrameworkReferences.cs:141-166`: restructure the `ResolveForTree` entry. Delete the early return at `:150-154` (`detected.Count == 0` no longer short-circuits); classify the representative instead: prefix `netstandard` or `netcoreapp`, or no parseable representative, means no reference pack (note naming the base actually used, generalizing the `:164` wording); family `net` with major 5 or above is the modern branch; family `net` with major 4 or below is the new net4x branch. The `>= 5` gate and its now-true comment move into this classification.
3. `Dosai/FrameworkReferences.cs:216-239`: the modern base branch runs for every modern target, detected or not, with its current rules unchanged (exact major first, nearest major with a note, discard when the resolved pack's major equals Dosai's own). Add the missing same-major note: when the target major equals `Environment.Version.Major`, name the base ("come from Dosai's own runtime ({current.Source}, .NET 11), which matches the target's major") so the net11 path is no longer silent either.
4. New net4x branch in `Dosai/FrameworkReferences.cs` (a `ResolveNetFrameworkReferenceAssemblies` core with an `internal` testable overload that takes the package roots, mirroring `ResolvePackDirectory` at `:339-346`): package id `microsoft.netframework.referenceassemblies.<tfm>` (the moniker without separators, lowercased) under `NuGetPackageRoots()` (`:531-546`, so `OverrideNuGetRootForTesting` at `:464-469` covers the tests), version directories highest release first, layout `build/.NETFramework/v<Major.Minor[.Patch]>/` derived from the parsed version (net48 to v4.8, net472 to v4.7.2). Exact target package only; no cross-TFM substitution in this fix. The references are the top-level `*.dll` plus `Facades/*.dll`, read through the existing `AddDirectory` local (`:177-214`) extended with an optional facades directory that is enumerated under the same origin label and recorded once. Calling the local twice instead would append the same origin to `packOrigins` once per call (`:209-213`), yielding a doubled `Source` ("tree-reference-packs[x,x]+...") and duplicate debug lines. The built-from-source drop (`:187-192`, without the base-pack exemption, since nothing fills those names here) and the one-per-name rule apply as they do for the modern packs. The net4x pack owns the whole set: skip the process-wide fill at `:263-279`, do not join `current.Diagnostic` at `:145-148` for this shape, and let the source be the tree-pack prefix alone when the pack was used. When the package is absent, return the process-wide set with the corrected diagnostic (see wording under Docs).
5. `Dosai/FrameworkReferences.cs:241-252`: when the representative is net4x, do not resolve the detected modern shared frameworks; add one note naming each skipped framework and its evidence, saying the net4x reference assemblies already contain its types.
6. Mixed-tree note, `Dosai/FrameworkReferences.cs` inside `ResolveForTree`: enumerate projects with `TargetFrameworkDetection.ProjectsUnder(root, ".csproj")` and `.fsproj` (the same inputs `Dosai.cs:770-788` reads, memoized per root by the existing caches), select each project's representative, and when any classifies as net-family major 4 or below (net4x) while the root's base is something else, add a note naming the count, up to five distinct targets, and `Metadata.ProjectGuardTargetFrameworks`. The note is scoped to net4x projects on purpose: `TryRank` (`CSharpSourceParser.cs:107-133`) orders netstandard (family 2) below modern (family 4), so a family-or-major comparison would fire for every ordinary solution holding a netstandard2.0 library beside a modern app, presenting benign binding as a defect and drowning the net48 case the note exists for. Netstandard and netcoreapp projects never trigger it, and a modern project of another major than the root's base also stays unreported in this fix.
7. `Dosai/Dosai.cs:647-652`: the unresolved-call classifier gains a branch for the net4x-absent case. Today the new absent-package note does not contain "No reference pack", so those trees fall through to the generic message at `:652`, which asserts "framework references come from installed packs, not from the tree", false for a net4x tree whose base is a NuGet cache package. When the tree's base note names the missing .NET Framework reference assemblies (a stable marker in the wording below), the message instead says the .NET Framework reference assemblies are missing from the NuGet cache and that restoring the tree fetches them; the existing branches (no references at all, missing packs, the generic message) keep their wording and their order.
8. `Dosai.Tests/Issue74BindingTests.cs:614-621`: `FrameworkReferences_TreeWithoutExtraFrameworks_KeepsTheProcessWideSet` pins the old silent record equality. The fixture moves to net11.0 (the `Net8Csproj` shape with the target replaced, as neighbouring tests in the file already build it) and the assertion compares `References` and `Source` against `Current`, allowing the new same-major note in `Diagnostic`. The net8.0 fixture cannot stay: GitHub ubuntu-latest runners preinstall the 8, 9 and 10 SDKs under `/usr/share/dotnet`, which `PackDirectories()` (`FrameworkReferences.cs:548-581`) probes, so CI resolves a real 8.x base pack for that fixture, `packOrigins` is non-empty and neither the old record equality nor a References/Source comparison holds there. Net11.0 is same-major on every machine (Dosai's runtime is .NET 11), so the set is the process-wide one everywhere and only the note differs. `OverrideForTesting` is not the tool for this test: the override wins the whole `ForTree` decision (`FrameworkReferences.cs:107-111`), so the test would no longer exercise `ForTree` at all.
9. No change to `Dosai/TreeFrameworks.cs:313-326` (`Claim` keeps dropping the base framework), to the per-root caching (`TreeResolvedByRoot`, reset by `ResetTreeCache` at `:61-68`, which `Dosai/McpServer.cs:150` already calls beside `TargetFrameworkDetection.ResetCaches` at `:147`), or to how commands surface the notes (`Dosai.cs:2909-2912`, `DataFlow.cs:1603-1608`, `CryptoAnalysis.cs:1591-1596` pick up `Diagnostic` as today). The new base-naming line therefore reaches every command's reports, crypto included; the queued #134 work plans to pin "no crypto-origin diagnostics" instead of an empty Diagnostics array, which absorbs this line, so this plan needs no crypto-side change beyond the shared wording.

Diagnostic wording to implement (invariant formatting for every number, per the Globalization rules):

- Exact-major base: "Base framework references for target 'net10.0' come from the Microsoft.NETCore.App 10.0.15 reference pack (exact major)."
- Same-major runtime: "Base framework references for target 'net11.0' come from Dosai's own runtime ({current.Source}, .NET 11), which matches the target's major."
- No modern pack: keep the existing sentence at `:238` unchanged.
- Netstandard or none: "The tree's representative target 'netstandard2.0' has no reference pack; the base framework references come from Dosai's own runtime ({current.Source}, .NET {major})." For an undetectable target: "No TargetFramework detected under the tree; the base framework references come from Dosai's own runtime ({current.Source}, .NET {major})." Neither contains the smoke job's forbidden substring "No .NET framework metadata references".
- Net4x pack used: "Base framework references for target 'net48' come from the .NET Framework 4.8 reference assemblies (Microsoft.NETFramework.ReferenceAssemblies.net48 1.0.3, NuGet cache); the process-wide runtime set fills nothing beside them."
- Net4x package absent: "No .NET Framework 4.8 reference assemblies (package Microsoft.NETFramework.ReferenceAssemblies.net48) were found in the NuGet cache; the base framework references come from Dosai's own runtime ({current.Source}, .NET {major}), so APIs that differ between .NET Framework 4.8 and .NET {major} bind through a different base than the tree targets. Restoring the tree fetches the package." The restore advice is legitimate here and only here: the missing thing is a NuGet package that restore provably auto-pulls, not an installed pack (the rule the unresolved-call classifier at `Dosai.cs:647-652` encodes by matching "No reference pack"). The note deliberately does not match that marker, and change 7 gives the classifier a branch keyed on this note so the unresolved-call message no longer asserts that framework references come from installed packs for a net4x tree.
- Skipped modern framework on net4x: "Microsoft.WindowsDesktop.App.WindowsForms via UseWindowsForms in Legacy/Legacy.csproj was not layered over the .NET Framework reference assemblies; the net4x reference assemblies already contain its types."
- Mixed tree: "Base framework references follow the root's representative target 'net10.0'; 1 net4x project targeting net48 binds against that base too (Metadata.ProjectGuardTargetFrameworks lists every project's target)."

### Test plan

New file `Dosai.Tests/Issue127FrameworkBindingTests.cs`, following the conventions of `Dosai.Tests/Issue74BindingTests.cs` (temp-dir fixtures, `Write`, the `ResolvePackDirectory`-style internal test hooks, `SkippableFact` where an installed pack or cache package is needed) and `Dosai.Tests/Issue113RuntimeCorlibTests.cs` (skip when the host lacks the fixture input).

1. Unit, the parse fix: `TryParseTargetVersion` (the internal parse from change 1) returns 4.8 for `net48`, 4.7.2 for `net472`, 4.5.1 for `net451`, 8.0 for `net8.0`, 10.0 for `net10.0`, and fails for `netstandard2.0`; the classification helper routes family-major-4 to the net4x branch and major 5+ to the modern branch, and `netcoreapp3.1` to no pack.
2. Unit, net4x package selection (macOS-safe, no build, no network): a temp NuGet root via `FrameworkReferences.OverrideNuGetRootForTesting` holding the directory shape only (`build/.NETFramework/v4.8` and `v4.7.2` with stub `.dll` names, the `Issue74BindingTests.WritePack` style): the net48 selection returns the v4.8 directory of the highest package version, net472 returns v4.7.2, a cache without the exact package returns null and produces the absent-package note text. No file is opened, matching how the existing pack tests assert selection only.
3. SkippableFact, end to end through `GetMethodsSlice`, exact expected strings. The fixture shapes are the corpus projects synthesized in temp dirs (a temp `plain` net10.0 without FrameworkReference, a `net11`, an `ns2`, a restore-only `legacy` net48 whose `obj/` holds a hand-written `project.assets.json` naming the auto-referenced package, matching the sandbox fixture; the test never builds net48). Skip conditions: the plain case skips when no exact-major 10.x `Microsoft.NETCore.App` pack exists on the machine (located with `FrameworkReferences.ResolvePackDirectory` over the real roots, the `Issue113.FindCoreReferencePackOrSkip` pattern), the legacy case skips when `microsoft.netframework.referenceassemblies.net48` is not in the NuGet cache. Expected: plain binds `Append` to "System.Runtime, Version=10.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a" and `WriteLine` to "System.Console, Version=10.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a" (the measured `detected` values, which is the whole point); net11 and ns2 keep their 11.0.0.0 bindings; legacy binds both calls to "mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089" (both symbols are defined in mscorlib for net48; the pack's `System.Console.dll` and the facades forward there, and Roslyn reports the defining assembly; confirm on the first run and record the printed pair if the facade chain surprises us).
4. Diagnostics: plain, net11, ns2 and legacy each carry exactly one base-naming note matching the wording table above for their case (legacy: pack note when the cache package exists, absent note when it does not, both asserted where the skip did not fire); the notes never contain "No .NET framework metadata references" (the smoke job asserts this), the modern no-pack note keeps the `Dosai.cs:650` classifier branch order intact (it must not contain "No reference pack" for the base case; the existing `:250` other-framework note still does), and the net4x-absent note selects the new unresolved-call branch of change 7, asserted as a plain string-selection check over a synthetic tree note, since the branches are string choices.
5. Determinism: two `GetMethodsSlice` runs over the same fixture produce identical `Diagnostics` lists (the note is computed once per root under `TreeResolveLock`, before any worker phase, so the existing byte-identity-per-worker-count and locale guarantees carry it; the existing `ParallelJsonWriter` and `Cli_Outputs_AreByteIdenticalAcrossLocales` tests need no change because both runs of a comparison take the same branch).
6. Update `Dosai.Tests/Issue74BindingTests.cs:614-621` per change 8, and run `dotnet test ./Dosai.sln` on macOS plus a Windows pass, since nothing here touches assembly loading, the file-IO risk is the NuGet cache read, which is read-only and `FileShare`-safe by `MetadataReference.CreateFromFile` as today.

macOS-safety summary: every net48 assertion is restore-only (source, csproj and a written assets file; no `dotnet build` of net48, no WindowsDesktop pack, no docker); pack-dependent cases skip rather than fail on machines without the input, so CI with only the 11 SDK still runs the unit and selection tests.

### Corpus fixture

`/Users/appthreat/sandbox/dosai-corpus/issue127-tfm` with `plain`, `detected`, `legacy` (net48, restore-only), `net11`, `ns2`; `out/` already holds the before evidence quoted in the Reproduction table, `verify/` a second before pass. After the change, rerun each and write to a new `after/` directory, keeping `out/` as the record of the defect:

```bash
cd /Users/appthreat/sandbox/dosai-corpus/issue127-tfm
dotnet restore legacy   # refreshes the net48 reference-assembly package if the cache was cleared
for d in plain detected legacy net11 ns2; do
  dotnet run --project /Users/appthreat/work/owasp/dosai/Dosai/Dosai.csproj -- methods \
    --path "$d" --o "after/$d.json" --debug > "after/$d-debug.stdout" 2> "after/$d-debug.stderr"
  python3 -c "import json,sys;[print(sys.argv[2], m['CalledMethod'], '|', m['Assembly']) for m in json.load(open(sys.argv[1],encoding='utf-8-sig'))['MethodCalls'] if 'Append' in (m['CalledMethod'] or '') or 'WriteLine' in (m['CalledMethod'] or '')]" "after/$d.json" "$d"
done
```

Expected after state: plain prints the 10.0.0.0 pair and its debug log gains the "base framework Microsoft.NETCore.App 10.0.15 for net10.0 (exact major)" line (parity with detected today); detected is unchanged; legacy prints the mscorlib 4.0.0.0 pair; net11 and ns2 print their unchanged 11.0.0.0 pairs, and all five JSONs carry the base-naming diagnostic. The Windows mixed-tree observation (net48 UseWindowsForms beside net10.0) stays a manual check on a Windows machine: the expected result there is the .NET 10 base as today plus the new mixed-tree note and the net48 project's own guards in `Metadata.ProjectGuardTargetFrameworks`.

### Risks

- The smoke-self-contained job (`.github/workflows/test.yml:298-365`) runs a bundled-runtime build with no dotnet reachable over a bare `Program.cs` tree with no project file: detection finds nothing, the classification lands on "no detectable target", and the set must keep `current.Source` (the grep at `:347` requires "framework metadata references (bundled-runtime)") and a note free of the substring "No .NET framework metadata references" (the assert at `:361`). The existing early return at `FrameworkReferences.cs:281-286` already keeps the source name when nothing came from a pack, and the wording table above keeps the substring out; the change plan deliberately does not touch that return. CI covers it on every push.
- Every command's `Diagnostics` grow at least one base-naming line, so the JSON changes for all consumers. This is additive under the schema 5.1.0 policy and documented in the migration guide, but in-repo assertions that pinned the silent behavior (change 7) or scrub-and-compare JSON (the `Issue113 Scrubbed` helper removes only its own marker) must be checked; the plan's test 5 keeps the new notes deterministic per root.
- The net4x branch skips the process-wide fill, so any framework name the net4x pack lacks binds nothing from the base (packages still join through `CompilationReferenceSet`). The pack is the complete SDK closure for that TFM, and the absent-package case keeps today's process-wide behavior, so the only new degradation is the one the diagnostic names. Roslyn gets a real corlib (`mscorlib`), which is the configuration every net4x compile has ever used, but the first run of the legacy fixture should be watched for surprise facade behavior (test 3 records it).
- Mixed net4x+modern trees keep one modern base for the net4x project's calls, by the representative rule. That is a reported compromise (the mixed-tree note plus `Metadata.ProjectGuardTargetFrameworks`), the same policy conditional compilation already lives with, and per-project bases are ruled out in Design decision 1.
- `TryParseVersion` becomes `internal` across `CSharpSourceParser.cs`; its behavior must not shift, since the preprocessor-define sets depend on it, so the unit test pins the shared parse rather than re-implementing it. `TreeFrameworks.Claim`'s drop of `Microsoft.NETCore.App` must survive untouched, or the base would be double-resolved.
- Locale: every number inside the new notes goes through `string.Create(CultureInfo.InvariantCulture, ...)`, or the Globalization analyzers fail the build.

### Docs

- `docs/compiler-engineering.md`, the issue #74 section around lines 55-78: rewrite the first paragraph to say the base pack is resolved for every tree with a detectable modern target, not only trees that declare a second shared framework; add a paragraph on the net4x reference assemblies (the NuGet cache package, the exact-package-only rule, mscorlib owning the corlib with no process-wide fill, detected modern frameworks not layered, the absent-package fallback); state the netstandard and undetectable-target behavior and that every root now reports the base it used.
- `docs/migration-5.1.0.md`: new additive section "Framework-reference diagnostics" recording that `Diagnostics` gain a base-naming line on every scan (the five wording shapes), that net10.0-and-earlier trees can now bind through the matching pack where they silently bound Dosai's runtime, and that net4x trees bind the .NET Framework reference assemblies when restore fetched them, with the exact assembly strings a consumer might have pinned changing accordingly (System.Private.CoreLib 11.0.0.0 to System.Runtime 10.0.0.0 for plain modern trees, to mscorlib 4.0.0.0 for net48).
- The diagnostics wording table above lands with the implementation so the plan and the code cannot drift; the restore-advice exception for the missing net4x cache package (a package restore auto-pulls, not an installed pack) is stated where the other pack diagnostics live.

### Size

M. One restructured resolution function, one new package-layout resolver beside `ResolvePackDirectory`, wording, one new test file, one updated test, two docs. No schema break, no pipeline restructure, no new per-root cache.

### Order

Landing position: this change lands first in the queued corpus work (issue #127, then #134, #130, #128 and #120, then the backlog items #131 and #129), because it changes framework binding for every fixture those issues measured. After it lands, a plain net8 or net10 tree binds through its target-matched pack and a net48 tree through mscorlib 4.0.0.0, so every before table the later issues recorded at 4f5f5b0 is stale: re-baseline each one at the commit that follows this merge, not at 4f5f5b0.

1. The parse fix (change 1) with its unit test, since everything else classifies through it.
2. The entry restructure and always-resolved modern base with notes (changes 2 and 3) plus the `Issue74` test update (change 8); this alone fixes the reported plain-net10.0 defect.
3. The net4x branch (changes 4 and 5) with the selection unit tests, then the end-to-end skippable tests.
4. The mixed-tree note and the unresolved-call classifier branch (changes 6 and 7), then the diagnostic-wording pass against the table.
5. Full `dotnet test ./Dosai.sln`, the corpus rerun into `after/`, and the docs (last, when the wording is final).

## Part 2: The weak-hash rules read the algorithm argument of the one-shot hash APIs (issue #134)

Branch `fix/5.1.0-analysis-triage` off main 4f5f5b0 (Dosai 5.0.0), milestone v5.1.0 Lodestar. The
issue: `CryptographicOperations.HashData(HashAlgorithmName.MD5, data)` reports the MD5 asset and
`DOSAI-CRYPTO-WEAK-HASH-MD5` in a block body and nothing at all in an expression body, in a VB
`Dim` statement, or in a field-initializer lambda. The reporter also tried anchoring the
declaration-line regex and rejected that build personally, because `public static bool Has(RSA? rsa)
=> rsa is not null;` then gained an RSA asset from its signature. The fix reads the
`HashAlgorithmName` argument through the operation walker, per the repo's data-flow heuristic
policy (symbol-resolved rules over line-text fallbacks), and leaves the regex alone. On the MAC
half of the API family the walker is not silent today: every `Hmac*` call already records a
generic strong `HMAC` asset from its method name, and the fix replaces that record with the MAC
the argument names instead of adding a contradictory second asset beside it.

### Reproduction (verified)

All evidence is from 4f5f5b0 on this machine over the fixture
`/Users/appthreat/sandbox/dosai-corpus/issue134-weakhash` (generated by
`corpus-pending/issue134.sh`), with the recorded reports under `verify/` and a python replay of the
declaration regex over the seven interesting lines. `crypto.json` fields below are
`Location.LineNumber:ColumnNumber` with the record's `source` property.

| Variant | Line | Result at 4f5f5b0 |
| --- | --- | --- |
| `Hashing.cs` expression body, `HashAlgorithmName.MD5` property form | 5 | nothing |
| `Hashing.cs` block body, same call | 9 | asset MD5 9:67 and finding `DOSAI-CRYPTO-WEAK-HASH-MD5` 9:67, `source: csharp` (the line fallback; the line starts with `return`, which is not a declaration keyword) |
| `vb/Hashing.vb` `Dim hash = CryptographicOperations.HashData(HashAlgorithmName.MD5, Data)` | 6 | nothing |
| `lambda/ParenLambda.cs` parenthesized-lambda field initializer | 6 | nothing |
| `hasprobe/HasProbe.cs` `public static bool Has(RSA? rsa) => rsa is not null;` beside a block-body MD5 use | 5 / 9 | nothing at 5 (must stay nothing); MD5 asset and finding at 9:67 |
| `boundary/Boundary.cs` `using static ...HashAlgorithmName`, arrow body, argument is the bare `MD5` property | 6 | nothing |
| `boundary/Boundary.cs` block body, `var algo = new HashAlgorithmName("MD5");` then `HashData(algo, data)` | 10 / 11 | asset MD5 10:43 and finding 10:43, `source: csharp`: the fallback's token regex matches `MD5` inside the string literal; nothing at 11 |

The whole-directory run reports exactly the three block-body or literal lines (Hashing.cs 9,
HasProbe.cs 9, Boundary.cs 10). The regex replay confirms `IsLikelyDeclarationLine` matches the
five keyword-led missed lines (Hashing.cs 5, vb 6, lambda 6, HasProbe 5, Boundary 6) and neither
reported `return` line nor Boundary 10 (it starts with `var`).

A second live probe (same build, temp fixture; the corpus fixture gains it as `hmac/Hmac.cs`)
shows the MAC half of the family is not silent today, which drives the design below:

| `CryptographicOperations.HmacData` shape | Line | Result at 4f5f5b0 |
| --- | --- | --- |
| arrow body, `HashAlgorithmName.MD5` argument | 6 | asset `HMAC` (family mac, strength strong, source roslyn) at 6:72, the invocation column; nothing about the algorithm |
| block body, same call | 10 | that generic `HMAC` at 10:16 plus the fallback's MD5 asset and `DOSAI-CRYPTO-WEAK-HASH-MD5` at 10:67 |
| arrow body, `HashAlgorithmName name` parameter (runtime algorithm) | 13 | generic `HMAC` at 13:98 only |

### Root cause

All paths are in the worktree at 4f5f5b0. The defect is two-sided: the line fallback skips lines
it thinks are declarations, and the semantic walker never reads the algorithm argument.

- `Dosai/CryptoAnalysis.cs:703`: `AnalyzeLineFallback` reads algorithm tokens only when
  `IsLikelyDeclarationLine` does not match. The C#/VB regex at `:908` ends with
  `\)\s*(?:=>|\{|$)`. The `$` alternative is anchored to the end of the line, but `=>` is not, so
  any keyword-led line whose parameter list is followed anywhere by an expression arrow matches:
  every expression-bodied member declaration hides its algorithm tokens from the fallback. That is
  also the load-bearing part for the pin: it is what keeps `Has(RSA? rsa)` from reporting RSA.
- `Dosai/CryptoAnalysis.cs:1931-1938`: the walker's `Record` classifies `DescribeSymbol` of the
  bound invocation (`:1940-1950`) and never inspects arguments. `ClassifyCryptoSymbol`
  (`:1362-1431`) has no rule for `CryptographicOperations.HashData`; its MD5 rule (`:1381`) and
  SHA-1 rule (`:1383`) match symbol tokens, and the fully qualified
  `System.Security.Cryptography.CryptographicOperations.HashData(...)` description contains none.
  So the walker, which does visit expression bodies (`:589`) and field initializers (`:605-611`),
  classifies the call to null and records nothing.
- `Dosai/CryptoAnalysis.cs:1639-1644` with `:1373` and `:1441-1488`: `Record` is not silent on the
  MAC half of the family. The description of an `HmacData` call is framework-qualified, so the
  bare `HMAC` candidate at `:1373` matches through `ContainsIdentifierToken` (`:1457`,
  `:1472-1488`): `.Hmac` after a dot starts an identifier word and the uppercase `D` of `Data`
  ends it. Every `Hmac*` call therefore already records a generic strong `HMAC` asset anchored at
  the invocation, which says nothing about the algorithm argument. A fix that only adds an
  argument-derived record would put a weak HMAC-MD5 beside a strong HMAC from the same call, so
  the argument record must replace the symbol record, not sit beside it.
- `Dosai/CryptoAnalysis.cs:1795-1800`: `HashAlgorithmNameOf` already decodes both forms the fixture
  uses, the `HashAlgorithmName.MD5` static-property reference (a `using static` import still binds
  the same property, so the using-static shape decodes too) and `new HashAlgorithmName("MD5")`
  with a constant string. Its only caller today is the PBKDF2-PRF rule at `:1697`. This fix gives
  it a second caller.
- The reporter's anchored variant, `\s*(?:=>|\{)?$` in place of `\s*(?:=>|\{|$)`, makes the
  expression-bodied line searchable and also makes `Has(RSA? rsa) => ...` searchable, and the token
  fallback at `:897` then reports RSA from the signature. Signatures can name RSA, DSA, ECDsa,
  DES, Aes and more, so any loosening of that regex re-opens a whole class of false positives; the
  anchor is not a one-character fix and the reporter was right to reject it.
- VB is not fallback-only: `AnalyzeDotNetSources` parses VB trees (`:543-547`), compiles them
  (`:550`) and runs the same `CryptoOperationWalker` over every method-block statement, accessor,
  field initializer and `As New` clause (`:569-581`, roots at `:635-675`) before the line fallback
  (`:577-580`). The VB `Dim` line is a statement root, the invocation binds against the same
  metadata references, so the VB miss has the same classification cause as the C# one and the same
  fix covers it. No VB-specific work is needed.

The overload family in the referenced metadata (verified in `Microsoft.NETCore.App.Ref` 11.0.0,
`ref/net11.0/System.Security.Cryptography.xml` in the NuGet cache copy): `HashData`
(five overloads), `HashDataAsync` (two), `HmacData` (six), `HmacDataAsync` (three), `TryHashData`,
`TryHmacData`, all with `HashAlgorithmName` as their first parameter named `hashAlgorithm`, plus
`FixedTimeEquals` and `ZeroMemory` which are irrelevant here. There is no `MacDataOneShot` in the
pack, so nothing to do for it. `MD5.Create()`-style calls are already covered: the bound symbol
names the algorithm, so `Record` classifies them today in every body shape.

### Change plan

1. `Dosai/CryptoAnalysis.cs:924-944` (`RecordCryptoUse`): add one optional parameter,
   `CryptoClassification? classification = null`, and use it instead of
   `ClassifyCryptoSymbol(symbol, code, source)` when it is provided. Everything downstream
   (asset, protocol check, operation, finding) is unchanged. This lets a caller classify from an
   argument while still passing a full method description as the operation's `Symbol` evidence.
2. `Dosai/CryptoAnalysis.cs:1795-1800`: promote `HashAlgorithmNameOf` out of the private nested
   walker into the outer `CryptoAnalyzer` class as `internal static`, behavior unchanged. The
   `InternalsVisibleTo` entry at `Dosai/Dosai.csproj:53` already covers the test project and
   `Dosai/FrameworkReferences.cs:478` is the precedent for internal helpers; inside the private
   walker the current `internal` marking is unreachable from tests, which is why the move is
   needed. Its single caller today (`:1697`) follows the move.
3. `Dosai/CryptoAnalysis.cs:1639-1644` (`CryptoOperationWalker.VisitInvocation`): read the
   argument before the symbol. A new private helper `RecordHashAlgorithmArgument(operation, code)`
   returns whether it recorded, and `Record(operation, operation.TargetMethod, code)` runs only
   when it did not. The order matters because of the generic-HMAC classification described in the
   root cause: without the skip, a decoded `HmacData(HashAlgorithmName.MD5, ...)` would record a
   weak HMAC-MD5 at the argument beside the strong generic HMAC the method name already produced
   at the invocation, two MAC assets disagreeing about strength on one call. When the decode is
   null (a local, a `HashAlgorithmName.Parse(...)` call, a computed value) the helper records
   nothing and `Record` runs exactly as today, so `HmacData(name, ...)` with a runtime-chosen
   algorithm keeps its generic `HMAC` asset (pinned by test 4). For the hash family the skip is
   naturally a no-op: their symbol descriptions classify to null today. If a future overload name
   ever misses the helper's family gate, `Record` still fires, which fails in the safe direction
   (today's record, never silence).
4. The helper, placed beside the promoted `HashAlgorithmNameOf`: return unless
   `operation.TargetMethod.ContainingType` has `Name == "CryptographicOperations"` and its
   namespace starts with `CryptographyNamespace` (`:1657`, the same gate `DetectSemanticMisuse`
   uses). Accept method names `HashData`, `HashDataAsync`, `TryHashData` (the hash family) and
   `HmacData`, `HmacDataAsync`, `TryHmacData` (the MAC family); there is no `MacDataOneShot` in
   the .NET 11 reference pack (verified, `Microsoft.NETCore.App.Ref` 11.0.0 lists exactly these
   six plus `FixedTimeEquals`/`ZeroMemory` on the type), and `using static
   CryptographicOperations` binds the same containing type. Find the argument whose parameter
   type's name is `HashAlgorithmName` (by type, not position; it is first in every overload).
   Decode it through `HashAlgorithmNameOf` and classify:
   `ClassifyCryptoSymbol(algorithmName, code, "roslyn")` for the hash family, so MD5 reuses the
   exact `DOSAI-CRYPTO-WEAK-HASH-MD5` asset, finding, severity and summary from `:1381-1382` and
   SHA-1 those from `:1383-1384`; for the MAC family, `ClassifyCryptoSymbol("HMAC" +
   algorithmName, ...)` so `HmacData(HashAlgorithmName.MD5, ...)` records the same HMAC-MD5 asset
   `HMACMD5.HashData` records today (`:1370`), and by the pinned contract
   (`Dosai.Tests/CryptoGapTests.cs:124`) a MAC never fires the weak-hash finding. Which strings
   decode: every static property .NET 11 defines on `HashAlgorithmName` (MD5, SHA1, SHA256,
   SHA384, SHA512, SHA3_256, SHA3_384, SHA3_512, verified in the ref pack XML) decodes through
   the property branch, and a constant decodes only when its spelling matches a classifier
   candidate: the canonical name strings "MD5", "SHA1", "SHA256", "SHA384" and "SHA512" do, while
   a hyphenated spelling such as "SHA-256" or "SHA3-256" matches no candidate (the SHA3
   candidates carry underscores, `:1380`) and records nothing. No hyphen or underscore
   normalization is added: the weak rules' targets decode in every form because their canonical
   strings equal their token spellings, and inventing spellings the classifier never defined is
   how token rules drift. Anchor the asset, operation and finding at the argument operation's
   syntax start (the evidence is the algorithm choice), resolve the enclosing method exactly like
   `Record` does (`:1934`), and pass `DescribeSymbol(operation.TargetMethod)` as the symbol so
   the CBOM operation names the API called, with source `"roslyn"`.
5. No regex change. The semantic path covers every verified missed shape: the C# expression body
   (roots at `:589`), the field-initializer lambda (`:605-611`), the VB `Dim` statement
   (`:637-643`), the using-static arrow (the argument still binds the `HashAlgorithmName.MD5`
   property, which `HashAlgorithmNameOf` decodes) and the inline
   `CryptographicOperations.HashData(new HashAlgorithmName("MD5"), data)` arrow (the
   `IObjectCreationOperation` branch at `:1798`). The block-body literal case
   (`var algo = new HashAlgorithmName("MD5")`) is already reported by the fallback and stays that
   way; the call over a local stays unreported at the call site, which is honest (the value is not
   a compile-time constant) and matches the walker's behavior everywhere else. The author's
   rejected build is the standing argument for leaving `:908` alone.
6. No change to the dedupe helpers: `AddAsset` (`:1110-1134`), `AddOperation` (`:1136-1175`) and
   `AddFinding` (`:1177-1210`) key on (algorithm or rule, path, line), so the walker and the
   fallback agree on one record per line and algorithm. The walker runs before the fallback per
   tree (`:552-567`), so its column and `source` win; `AddOperation`'s roslyn-upgrade branch
   (`:1148-1153`) exists for the opposite order and needs no edit. Together with change 3 this
   keeps exactly one MAC asset per `Hmac*` call site.
7. `docs/crypto-cbom.md`: add one row to the "C# and VB misuse is read from the bound call" table
   (around line 70) for `DOSAI-CRYPTO-WEAK-HASH-MD5` / `DOSAI-CRYPTO-WEAK-HASH-SHA1` stating that
   the algorithm is read from the `HashAlgorithmName` argument of the `CryptographicOperations`
   one-shot hash APIs in every body shape, that the argument's constant forms (property or
   `new HashAlgorithmName("...")`) both decode, and that the `Hmac*` one-shots classify as their
   MAC (an MD5 argument becomes HMAC-MD5 and replaces the generic HMAC asset the method name
   used to give) without the hash finding, like the named HMAC types.

Credits: before committing, run
`git credit for https://github.com/owasp-dep-scan/dosai/issues/134` to add the `Reported-by`
trailer for the reporter (their one-character experiment and its rejection are part of the root
cause record here), and repeat the trailers under Credits in the pull request description.

### Test plan

New file `Dosai.Tests/Issue134WeakHashTests.cs` in the style of `CryptoGapTests.cs` (temp-dir
fixture, `Analyze(source, fileName)`, `Lines(result, rule)`, `LineOf` markers,
`AssertOneFindingPerMarker`), all synthetic files, no corpus dependency:

1. `WeakHashArgument_IsReadInEveryBodyShape`: one file holding the arrow-body property form, the
   block body, the `using static` arrow with the bare `MD5` argument, the inline
   `new HashAlgorithmName("MD5")` arrow, an `await CryptographicOperations.HashDataAsync(...)`
   arrow, and the parenthesized-lambda field initializer. Expect exactly one
   `DOSAI-CRYPTO-WEAK-HASH-MD5` finding and one MD5 asset per marker line; the arrow line's asset
   column equals the `HashAlgorithmName` argument's column (assert from the source line text, not
   a hard-coded number); the operation's `Symbol` starts with
   `System.Security.Cryptography.CryptographicOperations.` and its `Properties["source"]` is
   `"roslyn"`.
2. `Vb_DimStatement_RecordsFromTheArgument`: the `Hashing.vb` body; expect the MD5 asset and the
   weak-hash finding on the `Dim` marker line.
3. `SignatureProbe_IsNotAnAsset` (the hasprobe pin): `public static bool Has(RSA? rsa) => rsa is
   not null;` beside a block-body MD5 call; expect no asset and no finding of any rule on the
   `Has` line, and the block line reporting exactly once.
4. `HmacData_ArgumentNamesTheMac_OneAssetPerLine`: two arrow-body calls. For
   `CryptographicOperations.HmacData(HashAlgorithmName.MD5, key, data)`, exactly one asset on the
   line, and it is HMAC-MD5 (family `mac`, strength `weak`); the generic `HMAC` asset the line
   carries at 4f5f5b0 is gone (the suppression of change 3), and there is no
   `DOSAI-CRYPTO-WEAK-HASH-MD5` finding, matching the `HMACMD5.HashData` pin at
   `CryptoGapTests.cs:124`. For `HmacData(name, key, data)` with a `HashAlgorithmName` parameter,
   exactly one asset, the generic `HMAC` (strength `strong`): the decode failed, so `Record` ran
   as today. The per-line asset count is asserted, not implied.
5. `HmacDataBlockBody_FallbackResidual_IsPinned`: the block-body form of the same MD5 call. The
   fallback still records the MD5 asset and `DOSAI-CRYPTO-WEAK-HASH-MD5` at the MD5 token, the
   walker records HMAC-MD5 at the argument, and no generic `HMAC` asset remains. Two assets and
   one finding on the line is the disclosed residual; the test pins it so a later cleanup of the
   fallback's token on MAC lines fails loudly instead of silently changing output.
6. `StrongAndSha1Arguments_ClassifyWithoutInventingRules`: arrow-body
   `HashData(HashAlgorithmName.SHA256, d)` gives a SHA-2 asset and no weak finding;
   `HashData(HashAlgorithmName.SHA1, d)` gives a SHA-1 asset and
   `DOSAI-CRYPTO-WEAK-HASH-SHA1` with CWE-328; `HashData(new HashAlgorithmName("SHA-256"), d)`
   records nothing at all, pinning the no-normalization decision for hyphenated spellings.
7. `HashAlgorithmNameOf_DecodesPropertyAndConstantForms`, the named unit test for the helper
   itself (change 2 makes it reachable): build one small `CSharpCompilation` in the style of
   `Dosai.Tests/Issue113RuntimeCorlibTests.cs:237` over expression-bodied properties holding
   `HashAlgorithmName.MD5`, `HashAlgorithmName.SHA3_256`, a conversion-wrapped property,
   `new HashAlgorithmName("MD5")`, `new HashAlgorithmName("SHA3-256")`, a parameter reference and
   `HashAlgorithmName.Parse(name)`; read each initializer's operation through the semantic model
   and call the internal helper directly, expecting `MD5`, `SHA3_256`, `MD5`, `MD5`, `null`,
   `null`, `null`.
8. `LocalAlgorithmName_StaysWithTheLineFallback`: block body with
   `var algo = new HashAlgorithmName("MD5"); return CryptographicOperations.HashData(algo, data);`;
   expect the finding on the declaration line only (unchanged behavior) and nothing on the call
   line.

Corpus integration, one `SkippableFact` in `Dosai.Tests/CorpusTests.cs` using
`CorpusPathOrSkip("issue134-weakhash")`, asserting the exact post-fix matrix of the whole-directory
run (table below), the per-line asset counts of the `hmac` cases, and the three negative pins
(HasProbe.cs line 5, Boundary.cs line 11, no generic HMAC beside a decoded MAC). Do not pin an
empty `Diagnostics` list: issue #127 lands first and adds a base-naming note even to project-less
trees, which the crypto command surfaces (`Dosai/CryptoAnalysis.cs:1591-1596`). Pin instead that no
diagnostic originates from the crypto pass itself (nothing containing "Crypto reachability
fallback" or "Crypto data-flow fallback"). Then `dotnet test ./Dosai.sln`. Nothing here touches
assembly loading or file I/O, so no Windows pass is required beyond CI. Locale independence is
unchanged by construction (no culture-sensitive calls, fixed summary strings) and stays guarded by
`Cli_Outputs_AreByteIdenticalAcrossLocales`.

### Corpus fixture

`/Users/appthreat/sandbox/dosai-corpus/issue134-weakhash` already holds `Hashing.cs` and the `vb`,
`lambda`, `hasprobe`, `boundary` subdirectories, the `check.py` printer and the `verify/` before
reports; `corpus-pending/issue134.sh` regenerates it. Extend that script (and the fixture) with an
`hmac` subdirectory holding the probe file of the reproduction table:

```csharp
using System;
using System.Security.Cryptography;

public static class Hmac
{
    public static byte[] Arrow(ReadOnlySpan<byte> key, byte[] data) => CryptographicOperations.HmacData(HashAlgorithmName.MD5, key, data);

    public static byte[] Block(ReadOnlySpan<byte> key, byte[] data)
    {
        return CryptographicOperations.HmacData(HashAlgorithmName.MD5, key, data);
    }

    public static byte[] Runtime(HashAlgorithmName name, ReadOnlySpan<byte> key, byte[] data) => CryptographicOperations.HmacData(name, key, data);
}
```

After the change, rerun the same commands per directory and keep `verify/` as the before record:

```bash
# check.py lives at the fixture root and opens crypto.json relative to the shell's cwd.
CHECK=/Users/appthreat/sandbox/dosai-corpus/issue134-weakhash/check.py
for d in . vb lambda hasprobe boundary hmac; do
  (cd "/Users/appthreat/sandbox/dosai-corpus/issue134-weakhash/$d" && \
   dotnet /Users/appthreat/work/owasp/dosai/Dosai/bin/Release/net11.0/osx-arm64/Dosai.dll \
     crypto --path . --o crypto.json && python3 "$CHECK")
done
```

Expected after state (line:column, asset name then finding rule on the same line):

| File | Line:Column | Records |
| --- | --- | --- |
| `Hashing.cs` | 5:81 | MD5 asset, `DOSAI-CRYPTO-WEAK-HASH-MD5` (new) |
| `Hashing.cs` | 9:49 | MD5 asset and finding (column moves from 67; one record, walker first) |
| `vb/Hashing.vb` | 6:53 | MD5 asset and finding (new) |
| `lambda/ParenLambda.cs` | 6:104 | MD5 asset and finding (new) |
| `hasprobe/HasProbe.cs` | 5 | nothing (pin) |
| `hasprobe/HasProbe.cs` | 9:49 | MD5 asset and finding (column moves from 67) |
| `boundary/Boundary.cs` | 6:90 | MD5 asset and finding (new; the argument is the bare `MD5` property) |
| `boundary/Boundary.cs` | 10:43 | unchanged, the fallback's record on the string literal |
| `boundary/Boundary.cs` | 11 | nothing (the algorithm is a local, not a constant) |
| `hmac/Hmac.cs` | 6:105 | exactly one asset, HMAC-MD5 (mac, weak); no generic HMAC (suppressed), no finding (new) |
| `hmac/Hmac.cs` | 10:49 | HMAC-MD5 asset from the walker plus the fallback's MD5 asset and `DOSAI-CRYPTO-WEAK-HASH-MD5` at 10:67 (the pinned residual); no generic HMAC |
| `hmac/Hmac.cs` | 13:98 | exactly one asset, generic `HMAC` (strong): the decode failed and `Record` ran as today |

The whole-directory run reports the weak-hash finding on exactly eight lines (the seven hash-family
lines above plus `hmac/Hmac.cs` 10) and the asset rows above and nothing else. Do not assert an
empty `Diagnostics` list: issue #127 lands first and adds a base-naming note for project-less
trees, surfaced by the crypto command (`Dosai/CryptoAnalysis.cs:1591-1596`). Assert instead that no
diagnostic originates from the crypto pass itself.

### Risks

- False positives stay bounded by the decode: classification runs on a value decoded from a
  `HashAlgorithmName` property reference or a constant string into its constructor, on a bound
  `CryptographicOperations` call, so signature tokens (`RSA? rsa`) cannot enter (the regex is
  untouched) and an unrecognized constant name classifies to null and records nothing. The one
  widening is the intended one: arrow bodies, VB statements and initializer lambdas that name a
  weak algorithm now report.
- The crypto evidence contract shifts on two already-reported lines: the block-body MD5 asset and
  finding on Hashing.cs 9 and HasProbe.cs 9 move from column 67 (the token) to column 49 (the
  argument), and their operations gain a fully qualified `Symbol`, the invocation text as `Code`
  and `source: roslyn` where they had `MD5`, the line text and `source: csharp`. The JSON fields
  themselves are unchanged, the lists only grow, and cdxgen reads the lists, but a consumer that
  pinned those locations or the `dosai:location` values sees the change; `BuildWeaknessCandidates`
  (`Dosai/CryptoAnalysis.cs:220-248`) grows accordingly. A sentence in the 5.1.0 release notes (and
   `docs/migration-5.1.0.md` if it grows a behavior section) should name it.
- The MAC family's existing evidence changes, not just grows: at 4f5f5b0 every `Hmac*` call
  already records a generic strong `HMAC` asset at the invocation column (verified live, the
  reproduction's second table). With the fix, a decoded call replaces that record with HMAC-MD5
  (or the argument's MAC) at the argument column, so the asset's name, strength and column all
  move on lines consumers may have pinned. That replacement is the point (the generic record
  asserts strength the caller never chose), but it belongs beside the Hashing.cs 9 column move in
  the release note, and test 4 plus the corpus `hmac` rows pin the outcome.
- A pre-existing approximation is inherited, not introduced: a block-body
  `HmacData(HashAlgorithmName.MD5, ...)` line keeps the fallback's MD5 token record (MD5 asset and
  weak-hash finding) beside the walker's HMAC-MD5 asset, because the fallback cannot tell a hash
  call from a MAC call. The suppression removes the line's third record, the generic `HMAC`, so
  the residual is two records where an arrow body has one; test 5 pins it. Making the fallback
  argument-aware is out of scope; if the maintainer wants shape parity for the MAC family, that is
  a separate, small follow-up.
- Hyphenated constant spellings ("SHA-256", "SHA3-256") decode to nothing and record nothing
  (change 4): the weak rules' targets are unaffected because their canonical strings match the
  classifier tokens, and adding normalization is deliberately declined to keep the decode
  faithful to what the classifier already defines. Test 6 pins the silence.
- The suppression fails safe: if a future overload name misses the helper's family gate, `Record`
  still records the generic `HMAC` as today, so an incomplete gate can never silence a MAC call,
  only leave it on the old coarse record.
- Locale and determinism: the new path adds no parsing or number formatting and walks no extra
  syntax (it reads one argument of calls it already visited), so the per-worker and locale
  guarantees are untouched. Issue #127 lands first and changes every command's `Diagnostics`; the
  corpus assertions here are written against that state (no crypto-origin diagnostics, not an
  empty list).

### Docs

`docs/crypto-cbom.md` change 7 above, one table row. No schema or migration-guide entry is
required because no field is added, renamed or restructured; the release notes mention the newly
reported body shapes, the two moved hash-family columns (67 to 49) and the MAC-family asset
replacement (generic `HMAC` at the invocation becomes the argument's MAC at the argument column
whenever the algorithm decodes).

### Size

S. One optional parameter, one helper of roughly twenty lines with a bool contract, one reordered
call site and one helper promotion to `internal` in `Dosai/CryptoAnalysis.cs`, one test file
(eight facts), one corpus test, one fixture subdirectory with its generator-script extension, one
docs row.

### Order

The code is independent of the other 5.1.0 items; nothing else touches `RecordCryptoUse` or the
walker this milestone. Issue #127 lands first, and its base-naming diagnostic is why the corpus
assertions here pin "no crypto-origin diagnostics" rather than an empty `Diagnostics` list; if
this change somehow lands first, that pin still holds (the list is then empty and contains no
crypto entry either). Land it any time before the release notes, ideally early so the corpus rerun
and the pinned columns settle while there is still room to adjust the anchor choice if review
prefers the invocation over the argument.

## Part 3: Initializer and expression-body calls and flows (issues #130 and #131)

Both defects live in the same subsystem: member initializers and expression bodies are walked
but their calls and flows are dropped or misattributed because the enclosing symbol at those
positions is a field or property, not a method. Branch `fix/5.1.0-analysis-triage`, base commit
`4f5f5b0` (dosai 5.0.0). Milestone split: issue #130 ships in `v5.1.0 Lodestar`; issue #131 sits
in `Backlog` because it needs cross-member static field taint in the source data-flow pass and
reuses the constructor-attribution helper that #130 introduces, so it rides behind #130.

Landing order note: issue #127 lands before #130 and changes framework binding for plain
`net10.0` fixtures like these, so every corpus number this plan pins is re-baselined after #127
lands and the assertions are updated to the post-#127 measurements before merge. When the
fixtures are registered in `Dosai.Tests/Corpus/setup.sh` and its README table, their sections
and rows are appended in landing order (#130's fixtures first, then #131's).

Two Roslyn facts were verified against Microsoft.CodeAnalysis.CSharp 5.9.0 (the version Dosai
builds against) and anchor the design:

- `INamedTypeSymbol.StaticConstructors` returns a real, implicitly declared static constructor
  symbol for a type with static field initializers and no declared `.cctor`, and
  `InstanceConstructors` returns an implicitly declared parameterless `.ctor()` when no instance
  constructor is declared. A record's copy constructor (`Rec(Rec original)`) is also exposed with
  `IsImplicitlyDeclared = true`, while its primary constructor is exposed as declared. So the
  owners can always be real `IMethodSymbol`s and render through the existing
  `SourceRenderCache`/`FormatMethodSignature` paths.
- `GetEnclosingSymbol` at a field or auto-property initializer returns the field (the backing
  field `C.AutoProperty.field` for an auto-property); inside a lambda in such an initializer it
  returns the lambda method; inside an expression-bodied property or indexer arrow it returns the
  getter accessor method. The getter case already records today; only the initializer case drops.

### Issue #130: methods records no calls made in field and auto-property initializers

Milestone: v5.1.0 Lodestar.

#### Reproduction (verified numbers)

On the restored base fixture (`Settings.cs` with a static field initializer on line 7, an
instance field creation on line 8, an auto-property initializer on line 9 and a lambda field
initializer on line 10), the measured `methods` output at `4f5f5b0` records only the line 10 row
(`10 <LambdaField>lambda1 -> Newtonsoft.Json.JsonConvert.SerializeObject(object?)`, evidence
`SourceRoslynDirect`) and one call-graph edge, and lists `pkg:nuget/Newtonsoft.Json@13.0.3` as
`ExternalCallGraphNode High` purely through the lambda. Without the lambda (the `nolambda`
variant) the package falls to `Dependency Low`. The `norestore` variant keeps only the line 10
row with evidence `SourceUnresolved`. Declared constructors do record their own bodies: the
`staticctor` variant has `11 .cctor Repro.Settings..cctor():void -> ...` and `instancector` has
`9 .ctor Repro.Settings..ctor() -> ...`. Struct field initializers and attribute arguments on
fields record nothing. Building the tree does not close the gap (the methods IL pass leaves a
non-Razor `bin/` to source analysis). One partial refutation of the report: with fully qualified
names and the lambda kept, the package is still listed; only qualified-without-lambda drops it.
Saved evidence: `/Users/appthreat/sandbox/dosai-corpus/issue130-initcalls/verify/*.json` and
the IL dump in `verify/ildump.cs` confirming the compiler places static field initializers in
`.cctor` and instance ones in `.ctor`, both generated when undeclared.

#### Root cause (path:line)

- `Dosai/Dosai.cs:3810`: the call walker's roots already include `EqualsValueClauseSyntax`, so
  initializer operations are visited.
- `Dosai/Dosai.cs:4587-4597` (`AddMethodCall`, guard at 4594-4597), `Dosai/Dosai.cs:4366-4379`
  (`AddUnresolvedCall`, guard at 4376-4379) and `Dosai/Dosai.cs:4878-4883`
  (`AddInferredMethodCall`, guard at 4880-4883) each return without recording when
  `EnclosingSymbol(position)` is not an `IMethodSymbol`; at an initializer it is the field (the
  backing field for an auto-property), so every direct call, creation and inferred candidate in
  an initializer is dropped.
- `Dosai/Dosai.cs:4332-4341`: `EnclosingSymbol` is a per-position memo over
  `model.GetEnclosingSymbol`, which has no notion of the constructor the initializer compiles
  into.

#### Change plan

1. **Id parity for static constructors first** (`Dosai/Dosai.cs:119-142`,
   `GenerateMethodSignature`). Extend the constructor special-casing to
   `MethodKind.StaticConstructor`: the name already falls through to `MetadataName`
   (`.cctor`); make the return type empty for `StaticConstructor` as it already is for
   `Constructor`. Today a declared static constructor renders `Repro.Settings..cctor():void`
   (measured on the `staticctor` variant) while both IL passes render `Ns.T..cctor()` with no
   return type (`Dosai/AssemblyCallGraphAnalyzer.cs:902`, `Dosai/DataFlowAssembly.cs:2344`).
   After the change both source and IL spell the id `Repro.Settings..cctor()`, so the
   synthesized rows below and a declared `.cctor`'s own rows are the same node. This is a
   JSON-visible id change; document it in `docs/migration-5.1.0.md`.

   The parity change has a second half, without which a declared static constructor ends up
   with two nodes: the inventory's constructor minting loop (`Dosai/Dosai.cs:3912-3925`)
   hardcodes `".ctor"` in `CreateMemberId`, so `verify/staticctor-methods.json` holds a phantom
   `Repro.Settings..ctor()` node beside `Repro.Settings..cctor():void` (the `ConstructorInfo`
   inventory knows the difference; `IsStatic` is recorded at `Dosai/Dosai.cs:3612`). Mint the
   declared static constructor's inventory node with `".cctor"` and the node name `.cctor`,
   and extend `CreateMemberId`'s return-type exemption (`Dosai/Dosai.cs:295`, currently
   `memberName != ".ctor"`) to `".cctor"` as well. After both halves, a declared static
   constructor has exactly one node, `Repro.Settings..cctor()`, whether it was minted from the
   inventory or from a row.

2. **A shared owner-resolution helper** (`Dosai/Dosai.cs`, new internal static method near the
   walker, for example `InitializerOwnerConstructors`). Input: the `ISymbol` that
   `GetEnclosingSymbol` returned at the initializer position (an `IFieldSymbol`, including
   auto-property backing fields whose `AssociatedSymbol` is the property, an `IPropertySymbol`
   or an `IEventSymbol`). Output: the constructors the compiler runs the initializer in, as real
   symbols:
   - A static member resolves to `ContainingType.StaticConstructors` single element: the
     declared `.cctor` when there is one, otherwise the implicitly declared symbol Roslyn
     exposes. Its rendered id is `Ns.T..cctor()`.
   - An instance member resolves to the declared instance constructors
     (`InstanceConstructors` where `!IsImplicitlyDeclared`), in declaration order; when the type
     declares none, the single implicitly declared parameterless `.ctor()`, and that fallback
     applies only to reference types. A struct's implicit parameterless constructor only zeroes
     fields and never runs initializers (C# itself requires a declared constructor before a
     struct may have field initializers), so for structs the owner list is the declared
     constructors and nothing else: a struct field initializer with no declared constructor
     stays unattributed, which is the semantically correct answer for the `structinit` fixture
     even though that tree does not compile. The rule keeps a class with a user-declared
     constructor attributed there (each declared constructor runs the initializer, which is
     exactly what IL contains), keeps a record's copy constructor out (it copies fields and
     does not run initializers) and keeps the record primary constructor in (it is exposed as
     declared, and instance field initializers including ones that read primary-constructor
     parameters run in it).
   Memoize per walker in a `Dictionary<ISymbol, IReadOnlyList<IMethodSymbol>>` keyed by symbol
   reference; the symbols are immutable per compilation, so the result never depends on the
   worker.

3. **Route the three guards through one caller resolution** (`Dosai/Dosai.cs:4594-4597`,
   `4376-4379`, `4880-4883`). Replace each `EnclosingSymbol(...) is not IMethodSymbol` early
   return with a call to a `ResolveCallers(operation, position)` that returns today's method
   symbol when the enclosing symbol is one, and otherwise, only when the operation sits inside a
   member initializer root (see step 4), the owner list from step 2. When the list has several
   constructors, emit one `MethodCalls` row per owner, in the owner order from step 2; the row
   keeps the initializer's own line and column, `CallerMethod` renders `.ctor` or `.cctor`
   (`SourceRenderCache.MemberName` returns the symbol name), `CallerClass` and
   `CallerNamespace` come from the owner's containing type and namespace, and `SourceId` is
   `renderCache.Signature(owner)`, that is `Repro.Settings..cctor()` or
   `Repro.Settings..ctor()`. There is no `EnclosingMethod` field on the `MethodCalls` DTO; the
   caller surface is `CallerMethod`/`CallerClass`/`CallerNamespace`/`SourceId`. `EvidenceKind`
   stays `SourceRoslynDirect` (or `SourceUnresolved` on unrestored trees) because the call site
   is witnessed syntax; the `Evidence` description gains a sentence that the caller is the
   constructor the compiler generates from the initializer. No new evidence kind, so downstream
   consumers that trust `SourceRoslynDirect` see a real edge, which is what promotes the package
   to `ExternalCallGraphNode High`. `AddUnresolvedCall` also keys its per-file dedup by
   `line:column:name` alone (`Dosai/Dosai.cs:4396`); with several owners that key suppresses the
   second and later rows on unrestored multi-constructor trees, so the owner's `SourceId` joins
   that key.

4. **Scope the mapping to initializer positions only.** The walker loop at
   `Dosai/Dosai.cs:3812-3821` knows the current root. Map to owners only when the root is an
   `EqualsValueClauseSyntax` whose parent is a `VariableDeclaratorSyntax` under a
   `FieldDeclarationSyntax`/`EventFieldDeclarationSyntax`, or whose parent is a
   `PropertyDeclarationSyntax`. Attribute arguments stay out of scope: positional attribute
   arguments are never under any root the walker visits, and named attribute arguments
   (`NameEquals`) are `EqualsValueClauseSyntax` nodes whose parent is an
   `AttributeArgumentSyntax`, excluded by the parent check, so they keep today's drop. Say so in
   the docs. Lambda bodies inside initializers keep the lambda caller
   (`Repro.Settings.<LambdaField>lambda1():string`); the backing-field to property mapping in
   `NestedFunctionName` (`Dosai/Dosai.cs:178-182`) already handles auto-property names.

5. **Call-graph validity and dedup.** The synthesized owner ids have no inventory entry (the
   `Constructors` inventory is syntax-driven, `Dosai/Dosai.cs:3589`, `3646`), so their nodes are
   minted from the rows at `Dosai/Dosai.cs:3927-3938`; that keeps every edge endpoint valid.
   In that minting loop, set the node `Kind` to `"Constructor"` when `CallerMethod` is `.ctor`
   or `.cctor`, matching the inventory's constructor nodes. `GraphAssembly.EdgeSiteKey`
   (`Dosai/GraphAssembly.cs:51-88`) keys on `SourceId`, so the multiple rows of one initializer
   call under several constructors stay distinct edges. Worker-count identity: rows are appended
   inside the file-pure per-file body into `SourceFileSymbols.MethodCalls`, in document order of
   roots and declaration order of owners, and the collectors merge in file order
   (`Dosai/Dosai.cs:3838-3861`), so the output is byte-identical for every worker count.

6. **Visual Basic is scoped out, with a diagnostic.** The VB root list
   (`Dosai/Dosai.cs:3811`) includes `EqualsValueSyntax` and `GetEnclosingSymbol` at a VB field
   initializer also returns the field, but the scope predicate from step 4 is written against
   C# syntax nodes and does not recognize a VB `EqualsValueSyntax` under a `FieldDeclaration`,
   so VB initializer calls keep today's drop rather than being attributed through untested
   VB-shaped assumptions. Make the drop visible instead of silent: the per-file collector
   counts the VB initializer call sites the guard dropped, and the file-order merge emits one
   diagnostic line when the count is nonzero (deterministic, first-wins wording). Extending the
   attribution to VB (shared constructor for `Shared` members, inline per-constructor emission
   for instance members) is a follow-up with its own VB fixture; note it in
   `docs/commands.md`.

#### Test plan

Unit tests in `Dosai.Tests/DosaiTests.cs` (synthetic trees in a `TemporaryDirectory`, read
through the existing `ReadMethods` helper in `Dosai.Tests/CompilationInputTests.cs:15`;
`Dosai.Tests/DosaiTests.cs:5710` holds its own copy of the same helper):

- `GetMethods_FieldInitializerCalls_AreAttributedToSynthesizedConstructors`: a class with a
  static field initializer, an instance creation initializer and an auto-property initializer
  calling `System.Console.WriteLine` (framework-resolvable). Assert `MethodCalls` rows whose
  `SourceId` is `Ns.Type..cctor()` (static line) and `Ns.Type..ctor()` (instance lines), with
  `CallerMethod` `.cctor`/`.ctor` and the initializer's own line numbers; assert the
  corresponding `CallGraph.Edges`; assert every edge references an existing node.
- `GetMethods_InstanceInitializerCalls_EmitOneRowPerDeclaredConstructor`: two declared
  constructors plus one instance initializer call produce exactly two rows and two edges with
  distinct `SourceId`s at the same line and column.
- `GetMethods_StructInitializerCalls_StaticAttributed_InstanceOnlyWithDeclaredCtor`: the
  `structinit` shape (no declared constructor) asserts the line 7 row under `Ns.S..cctor()` and
  no row for the line 8 instance initializer, because a struct's implicit parameterless
  constructor never runs initializers; a second synthetic struct that declares a constructor
  asserts its instance initializer call is attributed to that declared constructor.
- `GetMethods_PrimaryConstructorFieldInitializers_AttributeToPrimaryCtor`: a record or class
  with a primary constructor and an instance field initializer that reads the primary parameter
  records the row under the primary constructor's id (for example
  `Ns.Rec..ctor(System.String)`), which Roslyn exposes as a declared constructor.
- `GetMethods_RecordCopyConstructor_ExcludedFromInitializerOwners`: the same record asserts no
  row carries the copy constructor's id (`Ns.Rec..ctor(Rec)`), because the copy constructor
  copies fields and does not run initializers.
- `GetMethods_DeclaredStaticConstructorId_DropsVoidSuffix`: a declared static constructor's own
  body call renders `Ns.T..cctor()` with no `:void`, and the call graph holds exactly one node
  for the type's constructors, `Ns.T..cctor()`, with no phantom `Ns.T..ctor()` (regression for
  step 1, both halves).
- `GetMethods_FieldAttributeArguments_AreStillNotRecorded`: the `attrarg` shape asserts zero
  rows for both positional and named attribute arguments (scope guard).
- `GetMethods_UnrestoredTree_InitializerCallsRecordAsSourceUnresolved`: initializers calling an
  unresolvable package type record rows attributed to the synthesized constructors with
  `EvidenceKind` `SourceUnresolved`, one row per owner even when the class declares several
  constructors (regression for the unresolved-key fix in step 3).
- `GetMethods_VbInitializerCalls_StillDroppedButDiagnosed`: a VB file with a field initializer
  call records no row and the merged diagnostics carry the scoped-out note (step 6).

Corpus tests in `Dosai.Tests/CorpusTests.cs` (`SkippableFact`, `CorpusPathOrSkip`), with exact
assertions from the measured values:

- `base`: rows `(7, Repro.Settings..cctor(), Newtonsoft.Json.JsonConvert.SerializeObject(object?),
  MethodCall)`, `(8, Repro.Settings..ctor(), Newtonsoft.Json.JsonSerializerSettings,
  ConstructorCall)`, `(9, Repro.Settings..ctor(), SerializeObject, MethodCall)`, and the
  unchanged `(10, <LambdaField>lambda1, SerializeObject, MethodCall)`; plus
  `pkg:nuget/Newtonsoft.Json@13.0.3 ExternalCallGraphNode High` and the edge-endpoint validity
  assertion used by the other corpus tests.
- `nolambda`: rows 7, 8, 9 as above and the package still `ExternalCallGraphNode High` (the
  headline regression: it is `Dependency Low` today).
- `norestore`: rows at 7, 8, 9 with `SourceUnresolved` beside the line 10 one.
- `structinit`: measured today (verify/`structinit-methods.json`) at zero rows and
  `Dependency Low`; after the fix exactly one row, line 7 under `Repro.Settings..cctor()`, and
  no row for the line 8 instance initializer (a struct without a declared constructor never
  runs it), with the package promoted to `ExternalCallGraphNode High` (derived expectation;
  re-measured after #127 and after the change lands).
- `attrarg`: measured today (verify/`attrarg-methods.json`) at zero rows and
  `Dependency Low`; after the fix unchanged, zero rows and `Dependency Low`, because attribute
  arguments stay out of scope by design.
- `staticctor`: the line 11 row keeps its target but its `SourceId` becomes
  `Repro.Settings..cctor()`, and the phantom `Repro.Settings..ctor()` node that
  verify/`staticctor-methods.json` shows today is gone.
- `built`: the same rows as `base` (the IL pass leaves `bin/` to source analysis, no doubling).
- `qualified-nolambda`: measured today (verify/`qualified-nolambda-methods.json`) at zero rows
  with the package not listed at all; after the fix rows 7, 8 and 9 bind through the qualified
  names and the package is listed as `ExternalCallGraphNode High` (derived expectation, the
  report's partial refutation case).

#### Corpus fixture

`/Users/appthreat/sandbox/dosai-corpus/issue130-initcalls` already contains `base`, `nolambda`,
`norestore`, `staticctor`, `instancector`, `structinit`, `attrarg`, `qualified`,
`qualified-nolambda`, `qualified-norestore`, `qualified-nolambda-norestore` and `built`, each
with `Repro.csproj` (net10.0, Newtonsoft.Json 13.0.3) and `Settings.cs`, plus `check.py`,
`checkex.py` and the measured outputs under `verify/`. The verify directory now also holds the
baselines measured for this plan with the current binary: `structinit-methods.json`,
`attrarg-methods.json` and `qualified-nolambda-methods.json` (zero rows each; `structinit` and
`attrarg` at `Dependency Low`, `qualified-nolambda` unlisted). No new fixture work is needed;
the plan pins the assertions to these files. Because #127 lands first and changes framework
binding for these plain net10.0 trees, every number above is re-measured after #127 and the
assertions re-baselined before merge; the fixtures' `setup.sh` section and README row are
appended in landing order, after #127's entries.

#### Risks

- JSON compatibility: new `MethodCalls` rows appear (every initializer call site, multiplied by
  the owner-constructor count), the static-constructor id loses its `:void` suffix, and the
  phantom `..ctor()` node beside a declared static constructor disappears. All are
  additive-or-renaming changes to caller ids and node lists, documented in
  `docs/migration-5.1.0.md`; consumers that join `SourceId` against the IL pass's ids get better
  agreement, not worse.
- Row-count growth on multi-constructor classes mirrors the IL the compiler emits, but
  downstream counts of `MethodCalls` rise; the corpus tests pin the new counts so the growth is
  a decided, tested change.
- Worker-count identity depends on the owner list being derived from immutable symbols in
  declaration order and on rows staying inside the file-pure collector; both are covered by the
  design and the existing byte-identity guarantees of the merge loop.
- Over-attribution is the failure mode to guard: attribute arguments, enum-member or
  parameter-default `EqualsValueClause`s, and struct instance initializers without a declared
  constructor must not map to constructors. The parent-chain scope check in step 4, the
  struct rule in step 2 and their unit tests keep that boundary.

#### Docs

- `docs/commands.md`: the `methods` section notes that calls in field, auto-property and event
  initializers are recorded under the constructor the compiler runs them in (`.cctor` for
  static, every declared instance constructor, the generated parameterless one when a reference
  type declares none, declared constructors only for structs), that attribute arguments and VB
  initializer calls are out of scope (VB with a diagnostic), and that a declared static
  constructor's node id is `Ns.T..cctor()`.
- `docs/ARCHITECTURE.md`: a short paragraph in the source-analysis section on initializer
  attribution and the `.cctor()` id parity with the IL passes.
- `docs/migration-5.1.0.md`: the id change (`Repro.Settings..cctor():void` becomes
  `Repro.Settings..cctor()`) and the new rows.

#### Size

M.

#### Order

First of the two; nothing in it depends on #131.

### Issue #131: dataflows finds no flow from initializers, expression bodies or primary-constructor base arguments of an unbuilt tree

Milestone: Backlog; depends on #130's owner helper and id parity, so it is planned and landed
after #130.

#### Reproduction (verified numbers)

On the unbuilt fixture, `check.py` prints only `line 13 -> line 13 source`. On the built copy it
prints the six `assembly-il` flows `7 -> 8`, `9 -> 9`, `11 -> 11`, `12 -> 12`, `13 -> 13`,
`21 -> 21` plus the source `13 -> 13` (so the built tree reports the `Direct()` flow twice
already, once per pass). The `7 -> 8` flow is the `.cctor` `ReadLine` taint reaching
`UseField`'s `Process.Start`. The probe fixture (`issue131-initflows-probe`) isolates the
cross-member case with a block-bodied reader. Saved evidence:
`/Users/appthreat/sandbox/dosai-corpus/issue131-initflows{,-built,-probe,-probe-built}` with
measured `dataflows.json` under `verify/`.

#### Root cause (path:line)

- `Dosai/DataFlow.cs:608-609` (`CollectCompilationUnitSummaries`) and `Dosai/DataFlow.cs:640-641`
  (`AnalyzeCompilationUnit`) pick roots with a filter that accepts only
  `BaseMethodDeclarationSyntax`, `AccessorDeclarationSyntax` and `LocalFunctionStatementSyntax`;
  `VisitGlobalStatements` (`Dosai/DataFlow.cs:620-621`, `651-655`) exists because top-level
  statements sit outside that filter, and so do field and property initializers, the `=>`
  bodies of properties and indexers, and primary-constructor base arguments.
- The author's measured naive fix (adding `EqualsValueClauseSyntax`,
  `ArrowExpressionClauseSyntax` and `PrimaryConstructorBaseTypeSyntax` to both filters)
  duplicated the slices for lines 11 and 13 and still missed `7 -> 8`. The duplicates are
  structural: `AnalyzeCompilationUnit` creates a fresh `DataFlowOperationWalker` per matching
  descendant node (`Dosai/DataFlow.cs:642-648`), an expression-bodied method's arrow clause is
  itself a matching node nested inside its method declaration, `AddNode` mints fresh ids
  (`dfn{++counter}`, `Dosai/DataFlow.cs:5280`) and `AddSlice`'s dedup key starts with the
  per-walk first-source id (`Dosai/DataFlow.cs:5374-5378`), so two walks of one flow never
  collide.
- Cross-member static field taint has no producer for initializers: `DataFlowMemberFacts`
  (`Dosai/DataFlow.cs:139-142`) is filled only by `RecordWrite` inside a walked method body
  (`Dosai/DataFlow.cs:2039-2087`, member-facts fill at 2081-2085), and `Derive` returns empty
  without a current method (`Dosai/DataFlow.cs:1965`). The consumer side exists:
  `MemberReadTaint` (`Dosai/DataFlow.cs:3713-3744`) mints a source for a member read when the
  facts hold categories. The IL pass has the full mechanism through `FieldTaintKey`
  (`Dosai/DataFlowAssembly.cs:792`, `935`), which is why the built tree finds `7 -> 8`.

#### Change plan

1. **One root-selection helper, outermost roots only** (`Dosai/DataFlow.cs`, new private static
   `SelectDataFlowRoots(CSharpCompilationUnitSyntax root)` used by both :608-609 and :640-641).
   A single `DescendantNodes()` pass in document order keeps a node when it matches and is not
   contained in the span of the last kept node (document order makes ancestor spans precede
   descendant ones, so comparing against the last kept root is sufficient). Matching nodes:
   `BaseMethodDeclarationSyntax`, `AccessorDeclarationSyntax`,
   `LocalFunctionStatementSyntax` (as today, now automatically shadowing the arrow clauses and
   local initializers inside them), plus the new roots: an `EqualsValueClauseSyntax` whose
   parent is a `VariableDeclaratorSyntax` under `FieldDeclarationSyntax` or
   `EventFieldDeclarationSyntax`, or whose parent is a `PropertyDeclarationSyntax`; an
   `ArrowExpressionClauseSyntax` whose parent is a `PropertyDeclarationSyntax` or
   `IndexerDeclarationSyntax`; and a `PrimaryConstructorBaseTypeSyntax`. Locals' initializer
   clauses, parameter defaults and enum members are excluded by the parent chain and by the
   outermost rule. This is what removes the measured duplicates: an expression-bodied method or
   accessor is walked exactly once, as its declaration root.
2. **Pin the owner constructor for the new roots** (`Dosai/DataFlow.cs:2470-2524` walker and
   `:1736-1778` summary collector). Add a `VisitMemberInitializer` entry to both, mirroring
   `VisitGlobalStatements` (`:2641-2660`, `:1796-1830`): it resolves the owner constructors with
   the #130 helper (including its struct rule: a struct initializer without a declared
   constructor has no owner and is walked unpinned, so its intra-initializer flows are still
   found but no member facts are recorded for it) and pins `_currentMethod` to the first owner,
   walking the root operation and restoring. With the pin, `AddNode` stamps initializer nodes with
   `Properties["methodId"]` of `Ns.T..cctor()`/`Ns.T..ctor()`, matching the IL pass's method ids,
   and the walker's seeding and current-method resolution work unchanged. The graph pass walks
   each initializer once (not once per owner), which is what keeps node and slice counts stable.
3. **Record member facts with an origin** (`Dosai/DataFlow.cs:139-142`, `:2039-2087`). Extend
   `DataFlowMemberFacts` values from `List<string>` to a small structure holding the categories
   plus, when known, the initializer's origin (file name and line). The summary collector's
   `VisitMemberInitializer` records the write directly: member key `GlobalMemberPrefix +
   MemberName(member)` for static members and `MemberName(member)` for instance ones (the exact
   keys `MemberReadTaint` reads at `:3732`), categories from `Derive(initializerValue)` under
   the pinned owner, filling both the owner summary's `MemberSources` and the shared facts.
   Refactor the tail of `RecordWrite` (:2069-2086) into a `RecordMemberWrite(string memberKey,
   IOperation value)` both paths call. Constructor-stored members keep today's behavior
   (no origin recorded); only the initializer path records one.
4. **Mint member-fact sources at the initializer when the origin is known**
   (`Dosai/DataFlow.cs:3713-3744`). `MemberReadTaint` passes the recorded origin to
   `MintSource` when present, so the source node of the `7 -> 8` slice sits on line 7 at the
   `ReadLine` call, exactly where the IL pass places it, while reads without an origin keep
   minting at the read site. This is what makes the source `7 -> 8` slice and the IL `7 -> 8`
   slice the same flow for the dedup in step 6.
5. **Expression-bodied getters feed the summary fixpoint, and the fixpoint must be able to
   re-walk the new roots.** With the property arrow as a root, the summary collector records
   `ReturnSourceCategories` for `get_Input` (line 10), and `UseInput`'s call site mints through
   `SummarizedReturnTaint` (`Dosai/DataFlow.cs:3653-3690`) at line 11, producing the `11 -> 11`
   flow the IL pass reports. But `summaryMethodRoots` is registered only inside
   `VisitMethodBodyOperation` (`Dosai/DataFlow.cs:1771-1774`): an arrow root resolves to an
   `IBlockOperation`, not an `IMethodBodyOperation`, and the initializer roots pin their owner
   without any method-body operation, so neither is re-walked when a callee summary grows in a
   later round and facts derived from a not-yet-complete callee summary would go stale. Two
   registrations close that: the summary collector's `VisitBlock` fallback
   (`Dosai/DataFlow.cs:1780-1789`) does the same `methodRoots.TryAdd` for the method it just
   resolved (which covers arrow-bodied methods, accessors and getters), and the initializer
   walk collects its roots in a per-run list that is walked once more after the fixpoint
   converges, so member facts are derived against the converged summaries. Facts and summaries
   only ever grow, so the second initializer pass strictly adds; it is not itself part of the
   worklist, because constructors are nobody's callee in the caller index.
6. **Cross-pass slice dedup on built trees** (`Dosai/DataFlow.cs:277-295`, `:5359-5405`;
   `Dosai/DataFlowAssembly.cs:3997-4010`). Add an internal, non-serialized
   `HashSet<string>` of slice fingerprints to `DataFlowResult`, created in `AnalyzeCore` before
   the graph-walk phase (`Dosai/DataFlow.cs:498-517`). Both `AddSlice` implementations compute
   the fingerprint from slice content, not ids, and skip the slice when the fingerprint is
   already claimed:
   `{source node FileName}\u001f{sink node FileName}\u001f{source node line}\u001f{source node column}\u001f{sink node line}\u001f{sink node column}\u001f{sink category}\u001f{sinkArgumentIndex}`.
   The source file name and both columns are load-bearing: without them two genuinely distinct
   flows that share a sink file, lines, category and argument index would merge, so the
   fingerprint carries the source node's file and both nodes' line and column. The column
   parity this relies on, Roslyn syntax starts against PDB sequence-point starts at the same
   invocation, is pinned by the built-tree corpus test, which fails loudly if the passes drift.
   The source pass fills first, so on a built tree the six flows the source pass now finds keep
   their `source` evidence and the IL pass drops its duplicates; flows only the IL pass finds,
   package-internal ones included (issue #78's reason for reading `bin/` whole), keep
   `assembly-il`. Each per-pass `_sliceKeys` stays as the intra-pass guard. The unbuilt and
   built trees then print the same six flows, which is the report's "each flow found once".
   Worker-count and ordering identity: the source pass walks trees sequentially in path order
   and the IL pass merges fragments in assembly order, so the fingerprint set is consumed in a
   deterministic sequence regardless of the worker team.

#### Test plan

Unit tests in `Dosai.Tests/DosaiTests.cs` (temp directories, `ReadDataFlows`):

- `DataFlows_InitializerAndExpressionBodiedFlowsFoundOnUnbuiltTrees`: the issue's `Flows.cs`
  in a temp directory; assert the set of (source line, sink line) pairs over `Slices` is
  exactly `{(7,8),(9,9),(11,11),(12,12),(13,13),(21,21)}`, each appearing exactly once, and
  that none of their nodes carries `Properties["analysis"] = "assembly-il"`.
- `DataFlows_ExpressionBodiedMethodWalkedOnce`: a file mixing an expression-bodied method, an
  expression-bodied accessor and a block-bodied method with a local initializer; the local flow
  yields exactly one slice (the duplicate regression the naive fix introduced).
- `DataFlows_StaticFieldInitializerTaintReachesReaders`: the probe shape (block-bodied reader);
  exactly one slice with source line 7 and sink line at the reader, and the source node's
  `Properties["methodId"]` is `Ns.P..cctor()`.
- `DataFlows_SliceFingerprint_DedupesAcrossPasses`: unit-level check of the fingerprint helper
  with two synthetic slices sharing both files, lines, columns, category and argument index but
  different node ids (deduped), and negative cases that differ only in the source node's file
  name or in either node's column (both kept).
- `DataFlows_InstanceMemberFactsFromInitializers`: an instance-field initializer from
  `Console.ReadLine` read in another method mints the member-fact source (the instance twin of
  the static case, keyed without receiver).

Corpus tests in `Dosai.Tests/CorpusTests.cs` (`SkippableFact`):

- `Corpus_Issue131_InitFlows_Unbuilt` on `issue131-initflows`: exactly the six flows above,
  none duplicated, including `7 -> 8`.
- `Corpus_Issue131_InitFlows_Built` on `issue131-initflows-built`: the same six flows, each
  exactly once (today: six `assembly-il` plus one duplicate `source`).
- `Corpus_Issue131_InitFlows_Probe` and `..._ProbeBuilt` on `issue131-initflows-probe` and
  `-probe-built`: the cross-member flow appears exactly once in each, with identical lines
  between the built and unbuilt copies.

#### Corpus fixture

`/Users/appthreat/sandbox/dosai-corpus/issue131-initflows` (unbuilt, restored),
`issue131-initflows-built`, `issue131-initflows-probe` (unbuilt) and
`issue131-initflows-probe-built` already exist with `Repro.csproj` (net10.0, no packages),
`Flows.cs`/`Probe.cs` at the issue's line numbers, `check.py` and measured outputs under
`verify/`. No new fixture work is needed. These are plain net10.0 trees too, so their numbers
are re-baselined after #127 lands; the fixtures' `setup.sh` section and README row are appended
after #130's, in landing order.

#### Risks

- Root widening can silently reintroduce duplicates if a new root kind escapes the outermost
  rule; the mixed-shape unit test and the built-corpus assertion (each flow once) are the
  guards.
- The member-facts store is shared across the worker-pure source loop's sequential tree walk;
  facts only grow (union by ordinal key), and the origin is recorded by the first tree in path
  order that declares the member, which the path-ordered parse makes deterministic. A member
  declared in two partial files records the origin of the first file in path order; acceptable
  and deterministic.
- The cross-pass fingerprint can over-drop only when two genuinely distinct flows agree on the
  source node's file, the sink file, both lines, both columns, the category and the argument
  index, which is the same syntactic source position flowing into the same syntactic sink; any
  disagreement, a different source file, a shifted column, a different category, keeps both
  slices, which errs toward keeping evidence.
- Dropping the IL duplicates changes the `EvidenceKind` mix on built trees (flows previously
  reported as `assembly-il` are reported as `source`); document it in the migration notes so
  consumers keying on evidence kinds are not surprised.
- Node ids (`dfn{...}`) shift because the source pass now walks more roots before the IL pass
  numbers its nodes; ids are already order-dependent and the ordering is deterministic, but any
  consumer pinning raw node ids sees churn (migration note).

#### Docs

- `docs/commands.md`: the `dataflows` section states that field and property initializers,
  expression-bodied properties and indexers, and primary-constructor base arguments are
  analyzed on unbuilt trees, that a static field initializer's taint reaches the methods that
  read the field, and that on built trees each flow is reported once.
- `docs/ARCHITECTURE.md`: the source data-flow phase description gains the outermost-root
  selection, the origin-carrying member facts and the cross-pass slice fingerprint.
- `docs/migration-5.1.0.md`: new source slices on unbuilt trees, one copy per flow on built
  trees, and the node-id churn.

#### Size

L.

#### Order

After #130: it reuses `InitializerOwnerConstructors` and the `.cctor()` id parity, and its
milestone (Backlog) trails `v5.1.0 Lodestar`.

## Part 4: The reachable-mode dependency walk (issues #128 and #120)

Both defects sit in `PruneUnreachableDependencyMethods` and its `DependencyReachIndex`
(`Dosai/DataFlowAssembly.cs`). Issue #128 lands first: its qualified keys change what the
issue #120 walk reaches, and the reporter's expected post-fix numbers for #120 assume the
qualified keys are already in. All line numbers below are current main `4f5f5b0`
(dosai 5.0.0); the walk gained field and static-constructor reach in commit `a6bd11f`, but
the keys stayed unqualified.

### Issue #128: qualified keys for the reach walk

#### Reproduction (verified)

On the fixture `~/sandbox/dosai-corpus/issue128-libpacks`, scanning
`main/App/bin/Release/net10.0` with `dataflows --dependency-flows reachable` (App calls only
LibA; LibB is a packed package nothing calls) prints "5 of 13 method bodies ... not
analyzed", and `LibB.B.NeverCalled` appears in `MethodSummaries` with sink categories
`["command"]` although nothing calls LibB. The control without the lambda in LibB prints
"6 of 10" with every LibB body pruned. The probe variant shows the collision is not
compiler-generated-specific: LibP and LibQ each declare a user-written nested private static
class `Cache`, and `Cache.Local(int)` shares one key with `Cache.Local(string)`, so the
never-called `LibQ.Q.Cache.Local(string)` (a `Process.Start` sink) is analyzed.

#### Root cause

`Dosai/DataFlowAssembly.cs:512-513` (`MethodKey`) and `:515` (`FieldKey`) key a member by
`OpenName(member.ContainingType)` plus name (plus parameter count). `ContainingType` comes
from `GetFullTypeName` at `:2419-2423` (and `:2412-2417` for references), which returns the
bare `Name` when the namespace is blank. A nested type has no namespace of its own, so every
namespace-less nested type name is one string across all assemblies. The field-read rule
(`:420-427` maps `ldfld`/`ldsfld`/`ldflda`/`ldsflda` to `FieldRead`, `:471-473` returns every
method indexed by `IndexFieldStores` at `:484-494`) joins `<>c.<>9__0_0` stores and reads
across assemblies through that string, and the method index joins `<>c` members the same way.
The precedent is `FieldTaintKey` at `:2282-2292`, which qualifies by declaring assembly plus
symbol for exactly this reason, and `AssemblyCallGraphAnalyzer.cs:573-588`
(`QualifiedTypeNameParts`, iterative, cycle set, `MaxNestingDepth` 64 at `:600`) with the
comment at `:941-943` describing the same collision for call-graph ids.

#### Change plan

1. `Dosai/DataFlowAssembly.cs`, inside `DependencyReachIndex` (`:407-558`): add a qualified
   containing-type builder used only by the walk's keys. For a definition token it mirrors
   `QualifiedTypeNameParts`: walk the declaring-type chain iteratively with a seen-set and a
   64-level cap, join the parts with `.` and prefix the outermost type's namespace. For a
   reference it walks the `ResolutionScope` chain while it is a nested `TypeReference`, then
   takes the outermost reference's namespace, under the same seen-set and 64-level cap: a
   cycled resolution scope is untrusted metadata of exactly the shape the definition-side
   bound and `DeclaringAssemblyName`'s own `depth < 64` loop (`:2300`) exist for, and without
   it `PruneUnreachableDependencyMethods` would not terminate. For a `TypeSpecification`
   parent it decodes the generic head the way `DeclaringAssemblyName` (`:2298-2339`) does
   and recurses; on a decode failure it falls back through the legacy-key rule in item 3.
   Apply `OpenName` afterwards exactly as now, so a constructed or arity-carrying name still
   meets its open definition.
2. `Dosai/DataFlowAssembly.cs:451` and `:492` (index side) and `:473`, `:476` (lookup side):
   thread the qualified type into `MethodKey` and `FieldKey`. The index side already holds
   `definition.GetDeclaringType()` (`:438`); the lookup side has the reader and token at the
   call site of `Targets` (`:354-362`), so resolve the member's parent handle once and memoize
   the qualified name beside the existing `targets` memo at `:334`, `:354-358`.
3. Alongside the qualified index, record a secondary entry under the legacy unqualified key
   for every definition whose legacy key is unique across the whole definition set, and let a
   lookup that failed to build a qualified chain (a `TypeSpecification` whose head does not
   decode, a reference whose scope chain stops at depth or at a cycle) fall back to that
   secondary index. Uniqueness is decided over the complete set of indexed definitions before
   the walk starts, so it is a property of the metadata alone and cannot reintroduce the
   collisions this issue fixes: LibP's and LibQ's `Cache.Local` share a legacy key, so
   neither gets a secondary entry. The fallback therefore degrades a single member to
   today's reach instead of pruning it silently.
4. Qualify by the declaring chain only, not by declaring assembly plus chain. The index's
   stated property at `:404-405` is that keys over-approximate and "can only keep more
   methods in"; assembly qualification would break that in the wrong direction, because a
   type forward or a resolution scope that names a different simple assembly name than the
   assembly holding the definition would turn a join into a miss and prune a body application
   code does reach. Two assemblies declaring the same full namespace and chain can still
   join, which only keeps extra bodies in. `FieldTaintKey` needs the assembly because a wrong
   taint join creates false output; the reach walk is only a gate, so the cheaper guarantee
   is the right one here.
5. No other consumer changes. `MethodKey` and `FieldKey` are consumed only inside
   `DependencyReachIndex` (`AddDefinitions`, `IndexFieldStores`, `Targets`). The summaries
   dictionary (`CollectMethodSummaries`, `:568`) is keyed by the rendered symbol,
   `FieldTaintKey` is already qualified, and every rendered field (`Type`, `Symbol`, `Code`,
   `Namespace`, `Purl`) is built from the untouched `ContainingType` strings, so the output
   bytes change only where the diagnostic count changes.
6. `Dosai.Tests/Corpus/setup.sh` and `Dosai.Tests/Corpus/README.md`: add build recipes for
   `issue128-libpacks` (main, control, probe: pack LibA/LibB or LibP/LibQ into a local feed,
   build App against both packages) following the `issue78-mailkit` heredoc pattern, so the
   corpus tests below run beyond this machine. Three other plans append sections to the same
   two files and append order follows landing order, so this issue's `setup.sh` section and
   `README` row go last among the sections already present when it lands.

#### Test plan

Add `Dosai.Tests/Issue128QualifiedReachKeysTests.cs` with `Emit`-based fixtures in the
`Issue78DependencyFlowTests` style (deps.json marking two libraries as packages), plus the
SkippableFact corpus tests in `Dosai.Tests/CorpusTests.cs`:

1. `main` (path `issue128-libpacks/main/App/bin/Release/net10.0`, under
   `DependencyFlowScope.Apply(DependencyFlowMode.Reachable)`): the diagnostics contain a note
   matching `^9 of 13 method bodies in dependency assemblies were not analyzed`; no node has
   `Properties["assembly"] == "LibB.dll"`; no `MethodSummaries` entry names
   `LibB.B.NeverCalled` and none carries a `command` sink category from LibB; nodes with
   `Properties["assembly"] == "LibA.dll"` exist (all four LibA bodies, `Run` and its `<>c`
   members, stay kept). Before the fix the same run gives "5 of 13" and the NeverCalled
   summary.
2. `control` (path `issue128-libpacks/control/App/bin/Release/net10.0`): the note matches
   `^6 of 10`, no LibB node, LibA nodes present. This pins that qualification does not
   over-prune when no collision exists.
3. `probe` (path `issue128-libpacks/probe/App/bin/Release/net10.0`): no node or summary
   symbol contains `LibQ`; the `Process.Start` sink of `LibQ.Q.Cache.Local(string)` is gone;
   nodes with `Properties["assembly"] == "LibP.dll"` exist. The expected note is `4 of 6`
   (LibQ's four bodies pruned, LibP's two kept); pin the exact count from the first post-fix
   run, since the probe's measured evidence covers the collision, not the totals.
4. Local test: two package assemblies each declaring a nested `Cache` with same-named
   `Local(int)`/`Local(string)` members, Emit-compiled with a deps.json that marks both
   packages, asserting the same separation without the corpus.
5. Local test, TypeSpec parent: the app calls a member of a constructed nested generic type
   (a package declaring `Outer.Cache<T>` with a method, the app instantiating
   `Outer.Cache<Gadget>` and calling through it, so the member reference's parent is a
   `TypeSpecification`); under `reachable` the body stays kept. This pins the
   generic-head decode on the reference side.
6. Local test, fallback rule: the qualified-key builder and the secondary legacy index are
   made `internal` for the test project (the way other internals are tested), and the test
   asserts both halves of item 3: a definition set holding two same-legacy-key types records
   no secondary entries (the collision stays split), and a definition set whose legacy key is
   unique records the secondary entry, so a lookup that fell back to the legacy string still
   matches. This pins the path the risk section calls out, where a failed qualification
   could otherwise prune silently.

#### Corpus fixture

`/Users/appthreat/sandbox/dosai-corpus/issue128-libpacks` with `main` (LibA with a lambda,
LibB with `NeverCalled` plus a lambda and five unused methods, App calling only LibA),
`control` (LibB without the lambda), `probe` (user-written nested `Cache` classes in LibP and
LibQ), each with a local NuGet feed under `feed/`, and `verify/` holding the before-fix
outputs (`control-out.json`, `main-out.json`, `probe-out.json`).

#### Risks

The definition side and the reference side must render the identical qualified string
(nesting order, separator, generic opening); a mismatch silently prunes instead of joining,
which is why both fixture families are pinned by count and by member name, and why the
fallback of change-plan item 3 plus its test can only degrade a member to today's reach,
never below it. Both chain walks are bounded and cycle-guarded, so malformed metadata cannot
loop the walk. Chain-only keys
keep the index over-approximating, so order independence and the "property of the metadata
alone" claim in the doc comment at `:274-275` survive unchanged. The walk runs single-threaded
before any body, the keys never reach serialized output, and no schema field changes, so
worker-count byte identity and JSON compatibility hold; `Cli_Outputs_AreByteIdenticalAcrossLocales`
keeps covering the one string that does change (the diagnostic count) because keys are built
with ordinal comparisons and `CultureInfo.InvariantCulture`.

#### Docs

One sentence in the `--dependency-flows` bullet of `docs/migration-5.1.0.md`: the walk's
member and field keys carry the declaring type's full chain, so a nested type of one
assembly never joins another's. Issue #120, which lands after this one, rewrites that whole
bullet; the ordering is deliberate and stated in both plans, so #128 writes its sentence
knowing #120's rewrite will subsume it.

#### Size

M.

#### Order

1 (lands before issue #120; the RTA design fires keys built from qualified type names, and
the reporter's post-fix expectations for #120 assume it).

### Issue #120: dispatch following in the reachable walk

#### Reproduction (verified)

On `~/sandbox/dosai-corpus/issue120-serilog` (net8.0 app, Serilog 4.4.0,
Serilog.Sinks.Console 6.1.1): `all` gives 5 slices, all 5 application-anchored, and
`PackageReachability` lists both packages; `reachable` gives 2 slices, both anchored,
`pkg:nuget/Serilog.Sinks.Console@6.1.1` is gone, and the diagnostic reads "922 of 1190
method bodies ... not analyzed". The `all` node census holds Serilog.dll nodes in 6 methods
plus 8 nodes in `ExceptionTokenRenderer.Render`; `reachable` has zero in both. The walk stops
at `Serilog.ILogger.Write(LogEvent)`: the no-body skip at `Dosai/DataFlowAssembly.cs:440-443`
(RVA 0) keeps an abstract member out of the index, so the (declaring type, name, parameter
count) key matches nothing. Default interface members with bodies are followed. The boundary
probe (an app that directly instantiates `MessageTemplateTextFormatter` and calls `Format`
through `ITextFormatter`) still loses the flow under `reachable`, so instantiation evidence
in application code alone does not survive the interface call today. The doc promises that
contradict this are `Dosai/DependencyFlowScope.cs:12-13`, `Dosai/CommandLine.cs:67`, the doc
comment at `Dosai/DataFlowAssembly.cs:269-273`, the warning at `:384`,
`docs/ARCHITECTURE.md:111`, `docs/commands.md:192`, `docs/migration-5.1.0.md:532-536`, and
the assertions in `Dosai.Tests/Issue107AssemblyScopeTests.cs:116-118` and
`Dosai.Tests/CorpusTests.cs:390-393`.

#### Root cause

`PruneUnreachableDependencyMethods` (`Dosai/DataFlowAssembly.cs:278-386`) follows only the
member each call names. Interface and virtual dispatch is never resolved, so a call through
`ITextFormatter.Format` reaches no implementation body, and the nodes, edges and stored field
taints those bodies would produce are absent. `Transparency.cs:381-410`
(`BuildPackageReachability`) builds its entries from nodes, edges and slices alone, so
`Serilog.Sinks.Console` disappears with the pruned body. `IsDependencySlice` at `:439` reads
the slice's own unset `Scope`, which is why the lost slices are application-anchored, not
dependency-scoped.

#### Direction decision (the reporter's question)

Yes, the proposed direction is right, and this plan recommends rapid type analysis as
reported: follow a virtual or interface call only to types that reached code instantiates,
counting `newobj`, `ldtoken`, `box` and the generic arguments of called members as evidence,
taken from reached bodies only, with no class-hierarchy fallback. The reasons: a hierarchy
fallback is class-hierarchy analysis, which the doc comment already measured at three
quarters of MailKit's bodies kept and which admits exactly the dead formatters
(`MessageTemplateTextFormatter`, `JsonFormatter` via `SecondaryLoggerSink`) that made the
original report look like a regression; evidence from unreached bodies would admit
`SecondaryLoggerSink` and drag `LogEvent.Copy` back in; and among cheap over-approximations
RTA is the only one whose added bodies are bodies a run can actually execute, which is what
the reachable mode is for. Two refinements are needed on top of the reporter's sketch.
First, record dispatch keys only for `callvirt` and `ldvirtftn` sites: `call` on a virtual
member runs the bound method (already reached by name) and default interface bodies are
already followed, so restricting the new rule to real dispatch sites changes nothing else.
Second, the closure must fire in both directions: the walk decodes each body once
(`scanned`, `:333`, `:340`), so a dispatch key recorded before its type is instantiated and
a type instantiated before its key is recorded must each fire against the set accumulated so
far, or the result depends on dequeue order and misses pairs inside one body where the
`callvirt` precedes the `newobj`.

#### Change plan

1. `Dosai/DataFlowAssembly.cs`, `DependencyReachIndex`: extend `KindOf` (`:420-428`) with an
   `Instantiate` kind for `newobj` (which also keeps its `Call` behavior of reaching the
   constructor), `box` and `ldtoken`. A `newobj` operand is the constructor member token, not
   a type token: the instantiated type is that member's parent handle (a `MemberReference`
   parent or `MethodDefinition` declaring type), which may be a `TypeSpecification` for a
   constructed generic; `box` and `ldtoken` operands are type tokens directly. Render the
   parent or token with issue #128's qualified renderer and admit every dotted name it
   holds, generic arguments included (a `newobj` on `Cache<Gadget>` admits both), into an
   `_instantiated` set of open qualified type names; for called members, decode the same
   names from the signature types (extend `AssemblyMemberInfo` at `:4070` with the parameter
   type strings `ResolveMemberReference` at `:2264-2272` already computes). A name is
   admitted only when the index knows a dependency type of that name, which keeps the set
   small and the evidence metadata-only.
2. Same index: record a dispatch key (qualified declaring type, open member name, parameter
   count, taken from the same qualified-key builder as issue #128) for every `callvirt` and
   `ldvirtftn` whose resolved parent is not a sealed concrete type. Build the
   inheritance-and-interface table from the `TypeDefinition`s of every analyzed assembly,
   application assemblies included (`AddDefinitions` runs per dependency assembly only,
   `:296-302`, so application types are otherwise absent) and bodyless types included (an
   interface whose members all have RVA 0, such as `ITextFormatter`, never enters the method
   index, but its `TypeDefinition` still names what other types implement). The memoized
   closure of a type is its base-type names and `GetInterfaceImplementations` names, of the
   type and recursively of the bases and interfaces the table holds, bounded by the same
   64-level cap. When a type is admitted, fire every recorded dispatch key whose declaring
   type is in its closure; when a key is recorded, fire it against every admitted type whose
   closure contains its declaring type. Firing walks the instantiated type's base chain
   within the table and reaches the first declaration matching the key, through the existing
   `Reach` local (`:306-317`), which also pulls the type's static constructor as today. The
   match must also cover explicit interface implementations: C# emits them with the
   interface-qualified metadata name, read verbatim as the member's own name at
   `:2353-2356`, so a key of declaring chain, `Format` and parameter count would never reach
   a method named `Serilog.Formatting.ITextFormatter.Format`. A fired key `D, M, P` therefore
   additionally matches methods whose metadata name is the dotted form `OpenName(D).M`. Both
   sets only grow, so the closure is unique and
   order-independent.
3. Keep the abstract-member skip at `:440-443` as is: abstract declarations have no body to
   interpret, and dispatch firing does not need them indexed.
4. `Dosai/DataFlowAssembly.cs:384`, the diagnostic: the tail becomes "flows that start or
   pass inside them are not reported", because an application-anchored slice whose middle
   section lies in a pruned body is lost too, which is the corrected understanding of the
   original report.
5. `Dosai/DependencyFlowScope.cs:12-13`: replace "(the application slices are identical in
   both modes)" with wording that says the flows through the package code a run can execute
   are kept, while flows that only analyzing every package body finds are left out.
6. `Dosai/CommandLine.cs:67`: replace "with the same application slices" with "following
   interface and virtual dispatch to the types reached code instantiates; flows through
   package bodies no reachable path executes are left out".
7. `Dosai/DataFlowAssembly.cs:269-273`, the doc comment on the pruning: replace the three
   grounds with the new rule. It should say that keys carry the declaring chain (issue
   #128); that `callvirt` and `ldvirtftn` reach the implementations held by types reached
   code instantiates (`newobj`, `ldtoken`, `box`, generic arguments of called members,
   counted in reached bodies only); that there is no class-hierarchy fallback because it
   kept three quarters of MailKit's bodies and admitted implementations nothing constructs;
   that the interpreter still takes a call's summary from the member the call names, so
   dispatch following decides which bodies are interpreted, not which summary a call site
   uses; and that reachability stays a property of the metadata alone.
8. `docs/ARCHITECTURE.md:111`, `docs/commands.md:192`, and the `--dependency-flows` bullet
   at `docs/migration-5.1.0.md:527-544`: apply the same correction, and drop the "same as
   with `all`, node for node" sentence in favor of the narrowed promise with the Serilog
   fixture as the example (dead formatters stay out, `OutputTemplateRenderer` and
   `ExceptionTokenRenderer` come back). This rewrite of the bullet replaces the sentence
   issue #128 added to it; #128 lands first and states the same ordering in its own docs
   section, so the final bullet is written once, here.
9. `Dosai.Tests/Issue107AssemblyScopeTests.cs:116-118`: keep the equality (the fixture is
   all static calls, so it still holds) and note in the comment why it holds there. 
   `Dosai.Tests/CorpusTests.cs:390-393`: replace `Assert.Equal(ApplicationSlices(all),
   ApplicationSlices(without))` with a subset assertion (every reachable application slice
   appears in `all`), keep the exact equality between the two `reachable` runs, and reword
   the comment; the test's subject, that a root deps.json changes nothing, is unchanged.
   This is the one place either plan rewrites lines the other plan sits beside (#128's
   corpus tests are additive and append near it), so it is a merge-sequencing point: #120's
   edit lands after #128's additions, and a rebase keeps both.
10. `Dosai.Tests/Corpus/setup.sh` and `README.md`: add the `issue120-serilog` build recipe
   (App and probe, Serilog 4.4.0 and Serilog.Sinks.Console 6.1.1, Release build). As with
   issue #128, other plans append to the same two files in landing order, so this section
   and row go last among the sections already present when this lands, after #128's.

#### Test plan

Add `Dosai.Tests/Issue120RtaDispatchTests.cs` (Emit fixtures in the
`Issue78DependencyFlowTests` style, deps.json marking the implementation assembly a package)
and SkippableFact corpus tests in `Dosai.Tests/CorpusTests.cs`:

1. Local: an interface with one instantiated implementation carrying a sink is followed
   under `reachable` (the implementation's node exists and the flow from the app argument
   survives); a second, never-instantiated implementation stays pruned (no hierarchy
   fallback); an implementation constructed only inside an unreached package method stays
   pruned (reached-bodies-only evidence); an explicitly implemented member (C#
   `void ISink.Write(...)`, whose metadata name is the dotted interface-qualified form) is
   reached when its type is instantiated; and the same fixture built with the interface call
   textually before and after the construction gives identical slices (the two-directional
   firing rule).
2. `issue120-serilog/App` under `reachable`: `PackageReachability` regains
   `pkg:nuget/Serilog.Sinks.Console@6.1.1`; nodes exist whose symbol starts with
   `Serilog.Logger.Write(` (the walk passes `ILogger.Write`); nodes exist in
   `ExceptionTokenRenderer.Render` (8 nodes in `all`; pin the reachable count from the
   post-fix run) and in further Serilog.dll methods; the diagnostic still reads "N of 1190"
   with N below 922 (pin N from the measurement below); no application-anchored slice ends in
   `MessageTemplateTextFormatter.Format`, `JsonFormatter.Format` or `LogEvent.Copy`; the
   anchored-slice set is a subset of `all`'s and has at least 2 entries.
3. `issue120-serilog/probe` under `reachable`: an application-anchored slice (source node
   path ending in `.cs`) ends in
   `Serilog.Formatting.Display.MessageTemplateTextFormatter.Format(Serilog.Events.LogEvent,System.IO.TextWriter)`.
   This is the boundary probe, lost before the fix, kept by RTA through the app's own
   `newobj`.
4. Corpus measurement the reporter asked for, run before and after each change: for each of
   `eShopOnWeb/src/Web`, `modular-monolith-with-ddd/src`, `issue78-mailkit`,
   `issue107-mailkit`, `issue120-serilog/App`, `issue128-libpacks/main`, run
   `DOSAI_DEPENDENCY_FLOWS=reachable dotnet run --project ./Dosai/Dosai.csproj -- dataflows
   --path <corpus>/<app> --o /tmp/dep-walk.json` and print the "N of M method bodies ... not
   analyzed" note from `Diagnostics`; kept is M minus N. Record kept per app before the
   change (268 of 1190 on the Serilog app) and after, pin the after values in the tests of
   item 2, and time both runs so the RTA closure's cost is visible next to the 70 s to 3.5 s
   MailKit figures already documented.

#### Corpus fixture

`/Users/appthreat/sandbox/dosai-corpus/issue120-serilog` with `App` (the reporter's console
app), `probe` (direct `MessageTemplateTextFormatter` instantiation and an `ITextFormatter`
call), `nuget.config`, and `verify/` holding `all.json`, `reachable.json` and `census.py`
(the node census) from the before-fix runs.

#### Risks

The closure must stay order-independent, which the two-directional firing rule and the
monotone sets provide, with the local order test as the guard; the AGENTS.md invariant that
the IL pass gives the same flows in every file and method order depends on it. The RTA
type-set on real packages is the open cost: DI-heavy apps instantiate many types, every
admitted type fires pending keys, and the closure walk is memoized to keep it near-linear;
the measurement in the test plan records the kept-body growth and wall time across the
corpus, and the reporter's standalone walk (five methods reached on the Serilog fixture)
suggests a small set. The interpreter still takes no summary from the dispatch target, so a
flow needing value propagation through an interface call's return may still differ from
`all`; the narrowed doc wording must not promise node-for-node identity. The pinned MailKit
corpus equality may break and is re-pinned as a subset. Worker-count byte identity and JSON
compatibility are unaffected: no schema field changes and the only output difference is
which bodies are analyzed plus the diagnostic sentence.

#### Docs

Covered by change-plan items 4 through 9: `Dosai/DependencyFlowScope.cs`,
`Dosai/DataFlowAssembly.cs` (doc comment and warning), `Dosai/CommandLine.cs`,
`docs/ARCHITECTURE.md`, `docs/commands.md`, `docs/migration-5.1.0.md`, and the comments and
assertions in `Dosai.Tests/Issue107AssemblyScopeTests.cs` and `Dosai.Tests/CorpusTests.cs`.

#### Size

L.

#### Order

2 (after issue #128, whose qualified keys it consumes; note that after both land,
`JsonFormatter.Format` is reached by neither the field-read join (keys qualified) nor RTA
(never instantiated), which is the correct post-fix state and is pinned by test item 2).

## Part 5: PackageReachability EdgeIds after same-site collapsing (issue #129)

`methods` builds `PackageReachability` before it collapses repeated call sites, so entries
list edge ids that the collapse then removes from `CallGraph.Edges`. The reporter verified
the defect on Windows with the 5.0.0 release binary, and it reproduces at current main
`4f5f5b0` on the corpus fixture below. All anchors are `4f5f5b0`.

### Reproduction (verified values)

The fixture is `~/sandbox/dosai-corpus/issue129-edgeids` (provisioned by
`corpus-pending/issue129.sh`): a root `Program.cs` with two
`JsonConvert.SerializeObject` calls, plus `threecalls/Program.cs` with three
`SerializeObject` calls and one `DeserializeObject`, each with its own `Repro.csproj`
(net10.0, Newtonsoft.Json 13.0.3, restored).

On the reporter's original two-call tree, `CallGraph.Edges` holds `e1`
(SerializeObject, `CallSiteCount` 2, Evidence at 3:9 and 4:9), `e3` and `e4`, and the
Newtonsoft.Json entry lists `EdgeIds ['e1', 'e2']`, so `e2` dangles.

On the root scan at `4f5f5b0` (both Program.cs files, `verify/methods-root.json`), the
final edges are `e1` (DeserializeObject, threecalls 6:9), `e2` (SerializeObject,
`CallSiteCount` 3), `e5`, `e6` (WriteLine, `CallSiteCount` 2) and `e8`. The Newtonsoft.Json
entry lists `EdgeIds ['e1', 'e2', 'e3', 'e4']` with `e3` and `e4` missing, and
`SourceLocations` [(Program.cs, 1:1, Dependency), (threecalls/Program.cs, 1:1, Dependency),
(Program.cs, 3:9), (Program.cs, 4:9), (threecalls/Program.cs, 5:9), (threecalls/Program.cs,
6:9), all CallGraphEdge]. The System.Console entry lists `['e6', 'e7']` with `e7` missing.

On the probe scan at `4f5f5b0` (`threecalls/` only, `verify/methods-threecalls.json`), the
Newtonsoft.Json entry lists `EdgeIds ['e1', 'e2', 'e3', 'e4']` with `e3` and `e4` missing,
and `SourceLocations` [(Program.cs, 1:1, Dependency), (Program.cs, 3:9), (4:9), (5:9),
(6:9), all CallGraphEdge]. So every call site is present in `SourceLocations` today; only
the ids dangle.

### Root cause

`Dosai/Dosai.cs:610` builds the section, `Dosai/Dosai.cs:623` collapses afterwards.
`Dosai/Transparency.cs:342-348` iterates `callGraph.Edges` before the collapse and adds
each edge's id to the entries of both endpoint purls; the accumulator only ever appends
(`Dosai/Transparency.cs:597`). `Dosai/Reachability.cs:898` then replaces the edge list
with one keeper per (source, target, call type, evidence kind) group. The keeper is chosen
minimal by file, line and column over the group (`Dosai/Reachability.cs:816-833`), not as
the group's first id, and the dropped members' annotations merge into the keeper
(`Dosai/Reachability.cs:875-880`), while their ids stay recorded in the entries.

The naive fix, moving the build below the merge, was verified insufficient:
`Dosai/Transparency.cs:502-514` (lines 510-511) reads line and column only from
`edge.CallLocation`, which after a merge holds only the earliest site, so a post-merge
rebuild drops 4:9 (and 5:9 on the probe) even though the surviving edge's `Evidence`
carries every site.

### Fix design

Two designs were weighed.

(a) Build after the collapse and re-derive locations from the merged edge. Rejected: edge
`Evidence` entries carry the bare file name, not the path (verified: the root scan's merged
`e2` lists FileName "Program.cs" for a site that lives in `threecalls/Program.cs`), while
`SourceLocationFromEdge` resolves `edge.Path` first, so evidence-derived locations change a
`Path` whenever a group spans files and violate "nothing else may change". Doing it exactly
would need the collapse to carry each member's resolved path on the keeper, a new
edge-carried field plus a second copy of the path-resolution rule, and the build phase
would move.

(b) Keep the pre-collapse build and reconcile ids afterwards. A full dropped-id to
keeper-id map is unnecessary: every group's keeper is itself a pre-collapse edge, and the
edge loop at `Dosai/Transparency.cs:342-348` recorded it for both endpoint purls like every
other member. Removing ids that no longer exist in `CallGraph.Edges` therefore leaves a
live reference under each purl the keeper itself resolved to (the common case: group
members share their endpoints' purls), needs no new state on the edge, and touches no
other field. The exception is the split-purl group below in Risks, where the purl only a
dropped member carried loses the reference; a post-collapse rebuild loses it the same
way. Recommended.

After the fix, on the reporter's two-call tree the Newtonsoft.Json entry reads
`EdgeIds ["e1"]` with `SourceLocations` [(Program.cs, 1:1, Dependency), (Program.cs, 3:9),
(Program.cs, 4:9)] unchanged, and System.Console keeps `["e3"]` (already valid). On the
root scan the entry reads `EdgeIds ["e1", "e2"]` with the six `SourceLocations` above
unchanged, and System.Console reads `["e6"]`. On the probe scan it reads
`EdgeIds ["e1", "e2"]` with the five locations above unchanged; System.Collections keeps
`["e5"]` and System.Console keeps `["e6"]` on both scans.

### Compatibility

Inside the repo nothing joins `PackageReachability[].EdgeIds` to `CallGraph.Edges` in the
methods path: `Dosai/CryptoAnalysis.cs:448-474` joins slice `EdgeIds` against the
data-flow graph and prunes the data-flow overload's entries (no collapsing there, the
`DataFlowResult` builder at `Dosai/Transparency.cs:381` is a different code path);
`Dosai/CommandLine.cs:881,901` render `Slices[].EdgeIds` of the dataflows command. The
tests at `Dosai.Tests/DosaiTests.cs:1694-1748` call the builder directly on a hand-made
graph and never the filter. `Dosai.Tests/Goldens/` holds graph-export fixtures only, so no
golden file captures the methods section. The JSON change is confined to `EdgeIds` values
in the methods report (and the MCP `dosai.methods` tool, which serializes the same slice);
no schema field changes, so no migration note is required.

### Change plan

1. `Dosai/Transparency.cs`, beside the CallGraph overload of `BuildPackageReachability`
   (after line 379): add `public static void DropDanglingEdgeIds(List<PackageReachability>
   reachability, CallGraph callGraph)`. It builds one `HashSet<string>` (ordinal) of the
   final edge ids, then calls `entry.EdgeIds.RemoveAll(id => !ids.Contains(id))` per
   entry, skipping entries with no ids. Membership must stay set-backed per the AGENTS.md
   accumulator rule: never scan `Edges` per id. An entry whose every id was dropped stays
   in the list with an empty `EdgeIds`: the filter never removes entries, because their
   nodes, locations and dependency evidence can still be the reason the purl is reachable
   at all (`SliceIds`-only entries already serialize with an empty `EdgeIds`). The doc
   comment states the invariant the fix relies on: the section is built pre-collapse,
   every collapse group's keeper was itself recorded, so dropping absent ids removes no
   live reference, and `RemoveAll` preserves the surviving ids' insertion order.
2. `Dosai/Dosai.cs`, `BuildMethodsSlice` (lines 610-627): capture
   `var edgesBeforeCollapse = callGraph.Edges.Count;` before line 623, and immediately
   after the collapse and its `DebugLog.Count` (line 624) call
   `TransparencyBuilder.DropDanglingEdgeIds(packageReachability, callGraph)` when the
   count dropped, so an uncollapsed graph allocates nothing. The
   `methods.package-reachability` measure block stays where it is; nothing between lines
   611 and 623 reads `packageReachability` (its first use is the report assembly at line
   708), and the collapse leaves nodes, purls, categories and evidence kinds untouched,
   so no other entry field can shift.
3. `corpus-pending/issue129.sh`: extend it to also write `threecalls/Repro.csproj` and
   the three-call `Program.cs` and restore them, matching the sandbox fixture, so the
   probe variant is reproducible. Mirror the recipe in `Dosai.Tests/Corpus/setup.sh` and
   `Dosai.Tests/Corpus/README.md` following the `issue78-mailkit` heredoc pattern,
   appending this issue's section last among the new sections the other planned issues
   (#127, #134, #130, #128, #120, #131) add, matching the landing order below.
4. `docs/commands.md:158`: extend the cross-section reference bullet to say that
   `PackageReachability[].EdgeIds` name edges of the final `CallGraph`, where same-site
   collapsing has merged duplicate call sites into one counted edge
   (`CallSiteCount`), each merged group appearing as its surviving edge's id.

### Test plan

1. `Dosai.Tests/IssueRegressionTests.cs`, new region for issue #129 (in-memory, always
   runs): build a `CallGraph` with one source node and one target node carrying a purl,
   three edges sharing `SourceId`, `TargetId`, `CallType` and `EvidenceKind` at sites 3:9,
   4:9 and 5:9 whose ids are assigned so the minimal-site edge is not first, plus one
   edge to a different target. Call `BuildPackageReachability(callGraph, dependencies:
   [new Dependency { Purl = <the target's purl>, Path = "Program.cs", LineNumber = 1,
   ColumnNumber = 1 }])`, since Dependency-kind locations come only through that
   parameter (`Dosai/Transparency.cs:359-361`), then `CollapseDuplicateCallSites`, then
   `DropDanglingEdgeIds`. Assert the entry's `EdgeIds` equal exactly the keeper's id
   (proving the fix assumes nothing about first ids), every listed id exists in the final
   `Edges`, and `SourceLocations` still lists (Program.cs, 1:1, Dependency) plus 3:9,
   4:9, 5:9. Also assert the keeper's `CallSiteCount` is 3 and its `Evidence` holds all
   three sites, pinning that the fix must not need to rebuild locations. A second case
   pins a package losing its last edge id: a target node whose only edges are a group
   that fully collapses into a keeper recorded under a different purl (give the members
   the purl and the keeper a null one) keeps its entry, with `EdgeIds` empty and its
   `SourceLocations` intact.
2. `Dosai.Tests/CorpusTests.cs`, SkippableFact on `issue129-edgeids/threecalls`
   (`Depscan.Dosai.GetMethods`, the `CorpusPathOrSkip` convention): the Newtonsoft.Json
   entry's `EdgeIds` is exactly `["e1", "e2"]`; its `SourceLocations` are exactly
   [(Program.cs, 1:1, Dependency), (Program.cs, 3:9), (Program.cs, 4:9), (Program.cs,
   5:9), (Program.cs, 6:9), the last four Kind CallGraphEdge]; edge `e2` still has
   `CallSiteCount` 3. For every entry in the report, no `EdgeIds` member falls outside
   `CallGraph.Edges[].Id` (the `check.py` rule; the missing list is empty).
3. Same assertions on `issue129-edgeids` (root, both files): Newtonsoft.Json `EdgeIds`
   exactly `["e1", "e2"]`; System.Console exactly `["e6"]`; Newtonsoft.Json
   `SourceLocations` exactly [(Program.cs, 1:1, Dependency), (threecalls/Program.cs, 1:1,
   Dependency), (Program.cs, 3:9), (Program.cs, 4:9), (threecalls/Program.cs, 5:9),
   (threecalls/Program.cs, 6:9)], normalizing directory separators before comparing so
   the test holds on Windows too; the same whole-report no-missing-ids assertion.
4. `dotnet test ./Dosai.sln`; the fixture needs one `dotnet restore` per project, which
   the setup script performs, and the corpus tests skip when the corpus is absent.

### Corpus fixture

`/Users/appthreat/sandbox/dosai-corpus/issue129-edgeids` already holds the root project,
`threecalls/`, the reporter's `check.py`, and `verify/` with `methods-root.json`,
`methods-threecalls.json` and `analyze.py` from the before-fix runs at `4f5f5b0`. Change
plan item 3 makes `issue129.sh` reproduce all of it.

### Risks

Byte stability: `RemoveAll` keeps order, so surviving ids stay in today's relative order
and every other field is untouched; `FinalizeConfidence` already sorts `SourceLocations`
(`Dosai/Transparency.cs:664-669`), so that list's bytes cannot move. Worker-count
identity holds because the filter is a pure function of the collapsed graph and the
entries, both already deterministic per AGENTS.md. The one transient cost is the id set,
sized by the collapsed edge count, which reaches millions on the largest trees; the
before/after count guard skips it entirely when nothing collapsed, and the set dies right
after the pass. The corner case: a merged group whose members resolved different endpoint
purls (the same target id called from two projects on two versions) leaves the split
purl's entry without that edge's id, its locations intact; a post-collapse rebuild would
lose the id the same way, so the filter is not worse, and the docs sentence points
call-site counters at `CallSiteCount`. Consumers that counted `EdgeIds` as call sites
(cdxgen joins ids to edges) now see every join resolve, which is the fix's purpose.

### Docs

`docs/commands.md:158` per change plan item 4. No schema field changes, so
`docs/migration-5.1.0.md` needs no entry; its `EdgeIds` mention at line 228 describes the
crypto trimming of the data-flow report, which this change does not touch.

### Credits

The implementation commit carries `Reported-by:` for August Johansson with
`Link: https://github.com/owasp-dep-scan/dosai/issues/129` (name and noreply address from
the GitHub profile at commit time), repeated under Credits in the pull request
description.

### Size

S.

### Order

Last: after #127, #134, #130, #128, #120, then Backlog #131, this lands as #129. The
milestone is Backlog, the reporter called it lower priority, and nothing else depends on
it; the setup.sh and README sections it adds are appended after those of every other
planned issue, so no later-landing section has to insert around them.
