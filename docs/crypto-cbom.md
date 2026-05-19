# Cryptography and CBOM analysis

Dosai's `crypto` command produces code-level cryptographic evidence for security review and combined CycloneDX-style CBOM generation.

## Commands

Native Dosai JSON:

```bash
dotnet run --project ./Dosai/Dosai.csproj -- crypto \
  --path ./Dosai \
  --o /tmp/dosai-crypto.json \
  --format dosai
```

Combined CycloneDX-style CBOM JSON:

```bash
dotnet run --project ./Dosai/Dosai.csproj -- crypto \
  --path ./Dosai \
  --o /tmp/dosai-cbom.json \
  --format cyclonedx
```

## Evidence model

The native result has five main evidence collections. `assets` describe algorithms, crypto libraries, protocols, certificates, and key-related assets. `operations` capture source operations and API calls that use those assets. `materials` record source-visible key, certificate, IV, nonce, and secret-like values with redaction and fingerprints. `protocols` capture protocol observations such as TLS usage. `findings` record weak crypto, hardcoded material, TLS validation bypass, static IV or nonce use, insecure RNG, low PBKDF2 iteration counts, and legacy TLS references. `statistics` provides aggregate counts, including the number of reachable findings.

## Reachability

Dosai performs best-effort reachability by reusing method extraction, entry point discovery, and callgraph data. When a crypto operation or finding can be associated with a reachable method, the result includes `reachableFromEntryPoint`, `entryPointIds`, and `methodId`. Reachability must never fail crypto analysis. If symbol resolution or callgraph construction is incomplete, Dosai records diagnostics and continues with file and method-name correlation.

## CycloneDX mapping

CycloneDX output includes crypto assets, operations, materials, and protocols as components with Dosai properties such as `dosai:crypto:evidenceType`, `dosai:crypto:assetType`, `dosai:crypto:family`, `dosai:crypto:strength`, `dosai:crypto:reachableFromEntryPoint`, and `dosai:location`. Findings are emitted as vulnerability-like entries with rule IDs, severity, recommendation, CWE, affected crypto assets, operation/material references, reachability, and location properties. This keeps the CBOM and code-level evidence in one file.

## Language coverage

The crypto analyzer uses Roslyn `IOperation` for C# and VB where possible. Additional frontends cover F# files such as `.fs`, `.fsi`, and `.fsx`; R files such as `.R`, `.Rmd`, and `.qmd`; and VC++/C/C++ files such as `.c`, `.cpp`, `.cc`, `.cxx`, `.h`, `.hpp`, and `.hh`.

The F# frontend loads `FSharp.Compiler.Service` and labels F# method evidence with that module when available. The R frontend uses R's native parser through `Rscript` and `getParseData` when R is installed, with a managed fallback for environments without R. The VC++ frontend extracts conservative function, include, call, crypto, and sink evidence without requiring native build metadata.
