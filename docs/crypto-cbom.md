# Cryptography and CBOM analysis

Dosai's `crypto` command produces code-level cryptographic evidence for security review and combined CycloneDX-style CBOM generation. For audit-oriented workflows built on this evidence, see the [compliance and audit guide](./compliance.md).

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

Combined CBOM plus crypto data-flow graph sidecars:

```bash
dotnet run --project ./Dosai/Dosai.csproj -- crypto \
  --path ./Dosai \
  --o /tmp/dosai-cbom.json \
  --format cyclonedx \
  --graph-format graphml,gexf
```

This writes `/tmp/dosai-cbom.json`, `/tmp/dosai-cbom-dataflows.graphml`, and `/tmp/dosai-cbom-dataflows.gexf`. Use `--graph-out` when exporting a single format and you need an explicit sidecar path.

## Output size: `--crypto-dataflows`

`CryptoDataFlows` is the bulk of the native JSON on large trees (roughly 90% on the issue-#75 corpus), and both output formats are streamed to the file rather than built as one string. Consumers that only read `Assets`, `Operations`, and `Materials` can shrink the `dosai` output further with `--crypto-dataflows`:

```bash
dotnet run --project ./Dosai/Dosai.csproj -- crypto \
  --path ./big-tree \
  --o /tmp/dosai-crypto.json \
  --format dosai \
  --crypto-dataflows none
```

- `full` (default) keeps the whole `CryptoDataFlows` graph; output is unchanged from a run without the flag.
- `slices` keeps the slices and only the nodes and edges they reference. Every `DataFlowSliceIds` value on `Materials`, `Operations`, and `Findings` (and every `dosai:crypto:dataFlowSliceIds` property in the CycloneDX export, which derives from them) still resolves, as does every id inside `CryptoDataFlows` itself: the derived collections' id lists are pruned to the retained graph.
- `none` omits `CryptoDataFlows` while keeping the slice-id properties and `Statistics` (including `CryptoDataFlowSliceCount`), so downstream counts stay meaningful.

The option shapes the JSON only; crypto graph sidecars (`--graph-format`) always carry the full graph. The MCP `dosai.crypto` tool accepts the same value as `crypto_dataflows`.

cdxgen should pass `--crypto-dataflows none`: `analyzeDosaiCrypto` reads only `Assets`, `Operations`, and `Materials`, and Node's `readFileSync`/`JSON.parse` path silently returns nothing for files past the ~512 MB string limit, which previously dropped all dosai crypto components from SBOM runs on large trees.

## Reusing the methods and dataflows reports

`crypto` accepts `--methods-report <file>` and `--dataflows-report <file>` (a `methods` and a `dataflows` report written for the same `--path`) and uses them instead of building the methods slice and walking the data flows with the crypto pack again, which is most of its time when cdxgen has just run both commands. Reports are checked (path, schema major, required sections, `crypto` among the recorded pattern packs) and a refused one is named in `Diagnostics` and replaced by a fresh analysis. A reused data-flow report contributes the slices whose recorded `Packs` lie within `crypto`; their ids are the report's own. The full description, what is kept and the timings are in [the command reference](./commands.md#reusing-the-methods-and-dataflows-reports---methods-report---dataflows-report).

## Evidence model

The native result has five main evidence collections. `assets` describe algorithms, crypto libraries, protocols, certificates, and key-related assets. `operations` capture source operations and API calls that use those assets. `materials` record source-visible key, certificate, IV, nonce, and secret-like values with redaction and fingerprints. `protocols` capture protocol observations such as TLS usage. `findings` record weak crypto, hardcoded material, TLS validation bypass, static IV or nonce use, insecure RNG, low PBKDF2 iteration counts, and legacy TLS references. `statistics` provides aggregate counts, including the number of reachable findings and crypto data-flow slices.

.NET 11 crypto additions are part of the evidence model. The `Aes` key-wrap methods (`EncryptKeyWrap`, `DecryptKeyWrap`, `TryDecryptKeyWrap`, `GetKeyWrapLength`) classify as an `AES Key Wrap` asset with `key-wrap`/`key-unwrap` operations under standard RFC 3394. The post-quantum algorithms classify as strong assets: `MLKem` is ML-KEM (family `key-agreement`, operation `key-agreement/encapsulate`, standard FIPS 203), `MLDsa` is ML-DSA (family `signature`, operation `sign`, standard FIPS 204), and `SlhDsa` is SLH-DSA (family `signature`, operation `sign`, standard FIPS 205). The composite algorithms are assets of their own: `CompositeMLKem` is Composite ML-KEM (standard draft-ietf-lamps-pq-composite-kem) and `CompositeMLDsa` is Composite ML-DSA (standard draft-ietf-lamps-pq-composite-sigs), never the pure algorithm their names contain. `Hpke` and its sender, recipient and suite types (.NET 11 RC 2) are HPKE (family `pke`, operation `encrypt/decrypt`, standard RFC 9180), `ChaCha20Poly1305` is ChaCha20-Poly1305 (family `symmetric`, standard RFC 8439), and `CmsRecipient.CreateForKeyEncapsulation`, the `EnvelopedCms` KEM recipient, is ML-KEM under standard RFC 9629. The experimental caller-driven TLS session types in `System.Net.Security` (`TlsContext`, `TlsSession`, `TlsBufferSession`, `TlsSocketSession`, `TlsOperationStatus`) classify as TLS protocol assets. Use of an API the .NET 11 reference pack still marks experimental raises a low-severity finding named for its diagnostic: `DOSAI-CRYPTO-EXPERIMENTAL-TLS-API` (`SYSLIB5007`), `DOSAI-CRYPTO-EXPERIMENTAL-HPKE` (`SYSLIB5009`), and `DOSAI-CRYPTO-EXPERIMENTAL-PQC-API` (`SYSLIB5006`: composite ML-KEM and ML-DSA, SLH-DSA, and ML-DSA's mu and pre-hash members).

Algorithm names match as identifier words, at the start of a name segment or a camel-case hump and not running on into lowercase (`GetRSAPublicKey` is RSA, `TripleDES` is 3DES, while `Describe` is not DES and `UniversalTagNumber` is not RSA). MACs and KDFs are classified before the hashes their names contain: `HMACSHA256` is HMAC (not SHA-2), `HMACMD5` is HMAC-MD5 (weak, but not the MD5 hash rule), `HMACSHA1` is HMAC-SHA1, and `SP800108HmacCounterKdf` is KBKDF. The catalog also covers `SHA3_256`/`SHA3_384`/`SHA3_512` (SHA-3), `Shake128`/`Shake256` (SHAKE, an XOF), `Kmac128`/`Kmac256`/`KmacXof*` (KMAC), `HKDF`, `PasswordDeriveBytes` (PBKDF1, `DOSAI-CRYPTO-WEAK-KDF`), `DSA` (`DOSAI-CRYPTO-WEAK-SIGNATURE`: FIPS 186-5 no longer approves DSA signing), `Rijndael` (AES), `X509CertificateLoader` (X.509) and `SecurityProtocolType` (TLS). C# and VB comments are blanked before the line pass, so commented-out code is not a use.

C# and VB misuse is read from the bound call where the line cannot show it:

| Rule                                 | CWE      | What it reports                                                                                                                                                                                                                                                                                                                                                                                                                                             |
| ------------------------------------ | -------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `DOSAI-CRYPTO-LOW-PBKDF2-ITERATIONS` | CWE-916  | PBKDF2 below 100,000 iterations, with constants folded (`const int Iterations = 1000`) and the constructors' default of 1,000 when the count is omitted. A salt size is never read as an iteration count.                                                                                                                                                                                                                                                   |
| `DOSAI-CRYPTO-WEAK-KDF-PRF`          | CWE-916  | PBKDF2 over HMAC-SHA1 or HMAC-MD5, including the constructors' SHA-1 default.                                                                                                                                                                                                                                                                                                                                                                               |
| `DOSAI-CRYPTO-STATIC-IV`             | CWE-1204 | A zero (`new byte[16]`) or constant (`new byte[] { … }`, `Encoding.UTF8.GetBytes("…")`, `Convert.FromBase64String("…")`) IV or nonce, passed as an `iv`/`nonce` argument or assigned to `IV` or an IV-named variable. A quoted literal on an IV line counts only when it is assigned to an IV-named target and holds no whitespace, so log and exception messages that mention an IV are not findings.                                                      |
| `DOSAI-CRYPTO-WEAK-KEY-SIZE`         | CWE-326  | `RSA.Create(1024)`, `new RSACryptoServiceProvider(1024)`, `KeySize = 1024` on an asymmetric algorithm (anything below 2048 bits), and elliptic curves below 224 bits.                                                                                                                                                                                                                                                                                       |
| `DOSAI-CRYPTO-RSA-PKCS1-ENCRYPTION`  | CWE-780  | RSA encryption with `RSAEncryptionPadding.Pkcs1` or `fOAEP: false`.                                                                                                                                                                                                                                                                                                                                                                                         |
| `DOSAI-CRYPTO-ECB-MODE`              | CWE-327  | `CipherMode.ECB` and the `EncryptEcb`/`DecryptEcb` one-shots.                                                                                                                                                                                                                                                                                                                                                                                               |
| `DOSAI-CRYPTO-OBSOLETE-API`          | CWE-477  | APIs .NET marks obsolete, with the diagnostic in the finding's `diagnostic` property: `SYSLIB0021` (`*CryptoServiceProvider`/`*Managed` algorithm types), `SYSLIB0022` (Rijndael), `SYSLIB0023` (`RNGCryptoServiceProvider`), `SYSLIB0045` (`Create(string)` factories), `SYSLIB0053` (`AesGcm` without a tag size), `SYSLIB0057` (certificate-loading constructors; use `X509CertificateLoader`) and `SYSLIB0060` (the `Rfc2898DeriveBytes` constructors). |

Line checks apply to every language:

| Rule                                        | CWE     | What it reports                                                                                                                                                                                      |
| ------------------------------------------- | ------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `DOSAI-CRYPTO-TLS-CERT-VALIDATION-DISABLED` | CWE-295 | A certificate-validation callback that always returns `true` (lambda or `delegate { return true; }`), `DangerousAcceptAnyServerCertificateValidator`, `SSL_VERIFY_NONE`.                             |
| `DOSAI-CRYPTO-TLS-CERT-VALIDATION-CUSTOM`   | CWE-295 | Any other custom certificate-validation callback (Medium): it replaces platform validation and needs review.                                                                                         |
| `DOSAI-CRYPTO-LEGACY-TLS`                   | CWE-327 | `SslProtocols`/`SecurityProtocolType` SSL 2/3, TLS 1.0 (`.Tls`) and TLS 1.1. The protocol is recorded once per line, with its version and the weaker strength.                                       |
| `DOSAI-CRYPTO-JWT-VALIDATION-DISABLED`      | CWE-347 | `RequireSignedTokens`/`ValidateIssuerSigningKey = false` or a replaced `SignatureValidator` (High); `ValidateLifetime`/`RequireExpirationTime`/`ValidateAudience`/`ValidateIssuer = false` (Medium). |
| `DOSAI-CRYPTO-NON-CONSTANT-TIME-COMPARE`    | CWE-208 | `SequenceEqual` on a hash, MAC, signature, digest or tag; use `CryptographicOperations.FixedTimeEquals`.                                                                                             |

Every finding carries the CWE of its rule: hardcoded material CWE-321, weak hashes CWE-328, insecure RNG CWE-338, JWT `none` CWE-347, the experimental-API rules CWE-1357, and CWE-327 for the remaining weak-algorithm rules.

Dosai also runs crypto-specific data-flow slicing as part of the `crypto` command. The native Dosai JSON output uses PascalCase property names and includes `CryptoDataFlows`; matching data-flow slice IDs are attached to `Materials`, `Operations`, and `Findings` as `DataFlowSliceIds`. Findings also include `SourceMaterialIds` and `SinkOperationIds` when Dosai can connect hardcoded material or input sources to crypto API use. CycloneDX output uses Dosai property names such as `dosai:crypto:dataFlowSliceIds`, `dosai:crypto:sourceMaterialIds`, and `dosai:crypto:sinkOperationIds`.

## Reachability

Dosai performs best-effort reachability by reusing method extraction, entry point discovery, and callgraph data. When a crypto operation or finding can be associated with a reachable method, the result includes `reachableFromEntryPoint`, `entryPointIds`, and `methodId`. Reachability must never fail crypto analysis, but it is also never asserted without evidence: a reachability claim requires a graph path from an entry point, and when symbol resolution or callgraph construction is incomplete the older file-level fallback is gated off and a diagnostic names the file instead of silently claiming reachability.

## CycloneDX mapping

CycloneDX output includes crypto assets, operations, materials, and protocols as components with Dosai properties such as `dosai:crypto:evidenceType`, `dosai:crypto:assetType`, `dosai:crypto:family`, `dosai:crypto:strength`, `dosai:crypto:reachableFromEntryPoint`, `dosai:crypto:entryPointIds`, `dosai:crypto:dataFlowSliceIds`, and `dosai:location`. Findings are emitted as vulnerability-like entries with rule IDs, severity, recommendation, CWE, affected crypto assets, operation/material references, reachability, source material IDs, sink operation IDs, data-flow slice IDs, and location properties. Component-level `dependencies` connect crypto operations to source materials when the data-flow slices establish that relationship. This keeps the CBOM and code-level evidence in one file while graph sidecars preserve full path details.

The document is schema-valid CycloneDX 1.6 JSON: it declares `$schema`, components and vulnerabilities reference themselves with the hyphenated `bom-ref` key, and every `cryptographic-asset` component carries `cryptoProperties.assetType` from the CycloneDX enum (`algorithm`, `certificate`, `protocol`, `related-crypto-material`). Where dosai evidence maps onto the spec, `algorithmProperties.primitive` is set from the algorithm first and the asset family second (ML-KEM is `kem`, AES-GCM, AES-CCM and ChaCha20-Poly1305 are `ae`, AES and the DES family are `block-cipher`, HPKE is `pke`, SHAKE is `xof`, the CSPRNG is `drbg`, ECDSA, DSA, ML-DSA and SLH-DSA are `signature`; otherwise the family gives `hash`, `mac`, `kdf`, `key-agree`) and protocol assets get `protocolProperties.type` and `protocolProperties.version`; values with no clean CycloneDX equivalent stay in the `dosai:crypto:*` properties. Every reference emitted in `affects` and `dependencies` resolves to a component `bom-ref`, so strict validators accept the file.

## Language coverage

The crypto analyzer uses Roslyn `IOperation` for C# and VB where possible. Additional frontends cover F# files such as `.fs`, `.fsi`, and `.fsx`; R files such as `.R`, `.Rmd`, and `.qmd`; and VC++/C/C++ files such as `.c`, `.cpp`, `.cc`, `.cxx`, `.h`, `.hpp`, and `.hh`.

The F# frontend loads `FSharp.Compiler.Service` and labels F# method evidence with that module when available. The R frontend uses R's native parser through `Rscript` and `getParseData` when R is installed, with a managed fallback for environments without R. The VC++ frontend extracts conservative function, include, call, crypto, and sink evidence without requiring native build metadata.
