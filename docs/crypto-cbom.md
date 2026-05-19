# Cryptography and CBOM analysis

Dosai's `crypto` command produces code-level cryptographic evidence for security review and CBOM workflows.

## Commands

Native Dosai JSON:

```bash
dotnet run --project /Users/prabhu/work/owasp/dosai/Dosai/Dosai.csproj -- crypto \
  --path /Users/prabhu/work/owasp/dosai/Dosai \
  --o /tmp/dosai-crypto.json \
  --format dosai
```

CycloneDX-style CBOM JSON:

```bash
dotnet run --project /Users/prabhu/work/owasp/dosai/Dosai/Dosai.csproj -- crypto \
  --path /Users/prabhu/work/owasp/dosai/Dosai \
  --o /tmp/dosai-cbom.json \
  --format cyclonedx
```

cdxgen evidence sidecar:

```bash
dotnet run --project /Users/prabhu/work/owasp/dosai/Dosai/Dosai.csproj -- crypto \
  --path /Users/prabhu/work/owasp/dosai/Dosai \
  --o /tmp/dosai-crypto-evidence.json \
  --format cdxgen-evidence
```

## Evidence model

The native result includes:

- `assets`: algorithms, crypto libraries, protocols, certificates, and key-related assets.
- `operations`: API calls and source-code operations using crypto assets.
- `materials`: hardcoded or source-visible key/certificate/IV/secret-like material. Values are redacted and fingerprinted.
- `protocols`: protocol-level observations such as TLS usage.
- `findings`: weak crypto, hardcoded material, TLS validation bypass, static IV/nonce, insecure RNG, low PBKDF2 iterations, and legacy TLS findings.
- `statistics`: aggregate counts including reachable finding count.

## Reachability

Dosai performs best-effort reachability by reusing its method extraction, entry point discovery, and callgraph. When a crypto operation/finding can be associated with a reachable method, the result sets:

- `reachableFromEntryPoint: true`
- `entryPointIds: [...]`
- `methodId: ...`

Reachability is best-effort and must never fail crypto analysis; diagnostics explain fallback behavior.

## CycloneDX/cdxgen mapping

CycloneDX output includes crypto assets as components with Dosai properties such as:

- `dosai:crypto:assetType`
- `dosai:crypto:family`
- `dosai:crypto:strength`
- `dosai:crypto:reachableFromEntryPoint`
- `dosai:location`

Findings are emitted as vulnerability-like entries with rule IDs, severity, recommendation, CWE, affected crypto assets, reachability, and location properties.

The `cdxgen-evidence` format is a sidecar containing normalized Dosai crypto evidence that downstream tooling can correlate with package BOMs.

## Language coverage

The crypto analyzer uses Roslyn `IOperation` for C# and VB where possible. Additional frontends cover:

- F#: `.fs`, `.fsi`, `.fsx`
- R: `.R`, `.Rmd`, `.qmd`
- VC++/C/C++: `.c`, `.cpp`, `.cc`, `.cxx`, `.h`, `.hpp`, `.hh`

The F# frontend loads `FSharp.Compiler.Service` and labels F# method evidence with that module when available. The R frontend uses R's native parser through `Rscript` and `getParseData` when R is installed, with a managed fallback for environments without R. The VC++ frontend extracts conservative function, include, call, crypto, and sink evidence without requiring `compile_commands.json`.
