# Compliance and audit guide

Dosai turns source code and compiled assemblies into deterministic, code-level evidence that compliance, audit, and risk teams can attach to software assurance records. This guide explains which artifacts to generate for cryptography review, supply-chain inventory, data classification, and AI component inventory, and what each artifact does and does not prove.

Dosai never queries vulnerability databases and never makes exploitability claims. It converts semantic observations from the code into structured facts, so it fits best as the evidence-producing step of a review process where a human or a downstream tool makes the final judgment.

## Cryptography review and the CBOM

The `crypto` command produces a Cryptography Bill of Materials together with misuse findings. The CycloneDX-style output keeps code-level evidence and the BOM in one file, so BOM tooling does not need a separate evidence sidecar.

```bash
dotnet run --project ./Dosai/Dosai.csproj -- crypto \
  --path ./src \
  --o /tmp/dosai-cbom.json \
  --format cyclonedx
```

The CBOM lists crypto assets (algorithms, libraries, protocols, certificates, key-related assets), operations that use them, source-visible key and certificate material (redacted, with fingerprints), and protocol observations such as TLS usage. Findings cover weak algorithms, hardcoded material, TLS validation bypasses, static IVs and nonces, insecure random number generation, low PBKDF2 iteration counts, and legacy TLS references. Each finding carries a rule ID, severity, recommendation, and CWE mapping.

Dosai properties preserve the audit trail inside the CBOM. The important ones are `dosai:crypto:family`, `dosai:crypto:strength`, `dosai:crypto:evidenceType`, `dosai:crypto:reachableFromEntryPoint`, `dosai:crypto:entryPointIds`, `dosai:location`, and the correlation properties `dosai:crypto:dataFlowSliceIds`, `dosai:crypto:sourceMaterialIds`, and `dosai:crypto:sinkOperationIds`. When Dosai can trace a hardcoded key to the API call that uses it, those properties connect the material to the operation without manual cross-referencing.

Reachability is attached where the call graph supports it: a finding that sits on a path from a CLI or API entry point is flagged `reachableFromEntryPoint`. Reachability is best-effort and never blocks analysis; when symbol resolution is incomplete, Dosai records diagnostics and falls back to file and method-name correlation. Treat reachability as prioritization evidence, not as proof that a finding is exploitable.

For full path inspection, export graph sidecars next to the CBOM:

```bash
dotnet run --project ./Dosai/Dosai.csproj -- crypto \
  --path ./src \
  --o /tmp/dosai-cbom.json \
  --format cyclonedx \
  --graph-format graphml,gexf
```

See [Cryptography and CBOM analysis](./crypto-cbom.md) for the complete evidence model and [Graph export formats](./graph-formats.md) for the sidecar formats.

## Supply-chain inventory and SBOM correlation

Dosai enriches methods, call graph edges, and data-flow slices with NuGet Package URLs (PURLs) inferred from `project.assets.json` and `*.deps.json` restore metadata. The format is `pkg:nuget/<PackageName>@<Version>`.

For audits, the most useful output is `PackageReachability`: for each package Dosai observes, it records whether the package is reachable from an entry point and attaches source-file occurrence locations. Occurrence evidence is deliberately restricted to source files such as `.cs`, `.vb`, `.fs`, and `.R`, because assembly-only fallback paths are weak evidence for source-oriented SBOMs.

```json
{
  "Purl": "pkg:nuget/System.Diagnostics.Process",
  "Reachable": true,
  "ReachabilityKind": "CallGraphEdge",
  "SourceLocations": [
    {
      "Path": "src/Program.cs",
      "FileName": "Program.cs",
      "LineNumber": 10,
      "ColumnNumber": 9,
      "Kind": "CallGraphEdge"
    }
  ]
}
```

Typical audit questions this answers: which restored packages does the application actually call, and where in the source do those calls happen. When a vulnerability advisory names a package, the PURL fields on slices and call graph edges let you check whether untrusted input can reach the affected API, which is the input for a risk decision rather than the decision itself.

PURL resolution is best-effort. Source-only dependencies without restore metadata may not resolve, multiple packages can expose the same namespace prefix, and binding redirects are not modeled. PURLs are correlation metadata, not a vulnerability verdict. Details are in [Supply-chain PURL enrichment](./supply-chain-purl.md).

## Service inventory, trust zones, and data classification

The `methods` command emits a first-class service inventory in `Services[]`. Every detected inbound surface (HTTP controllers, minimal APIs, gRPC services, SignalR hubs, SOAP endpoints, queue consumers, Azure Functions, AWS Lambdas, MCP tools) and outbound dependency (databases, HTTP clients, vector stores, LLM endpoints) is recorded with resolved paths, confidence, and a trust zone.

Two fields matter most for compliance review:

- `TrustZone` classifies each service as `public`, `authenticated`, `internal`, or `external` (with `unknown` when evidence is missing). `CrossesTrustBoundary` is set only when a positive call graph path exists from a public inbound service to an external outbound service, never guessed.
- `Data[]` labels request and response types as `pii`, `credential`, `financial`, or `health` based on DTO member names. Every non-`unknown` classification names the member that triggered it, so a label can always be traced back to the code that caused it. The default is `unknown`; `public` is never emitted. Disable classification with `--no-classify-data`.

Services carry stable bom-refs (`svc:<framework>:<group>/<name>`, operations as `op:<serviceId>#<verb>:<path>`) and map to CycloneDX 1.7 `services[]` with trust zone and data classifications, which is how cdxgen consumes them. See [Framework semantics](./frameworks.md) for the full provider model and detected frameworks.

## AI component inventory

For AI governance reviews, `methods` also emits `AiComponents[]`: model identifiers, hashed on-disk model artifacts (ONNX, GGUF, Safetensors, PyTorch), MCP tools with their JSON Schemas, agents, and embeddings.

System prompts are redacted by default. Dosai emits a SHA-256 prefix plus the first 200 characters, and secret-shaped prompt text is always withheld. Use `--include-prompt-text` only when the audit specifically requires full prompt text and the output will be handled accordingly.

```bash
dotnet run --project ./Dosai/Dosai.csproj -- methods \
  --path ./src \
  --o /tmp/dosai-methods.json \
  --include-prompt-text
```

## Reproducibility and schema stability

Two properties make Dosai output usable as audit evidence.

First, output is deterministic. Identifiers never contain absolute paths, line numbers, or timestamps, so the same tree analyzed on two machines produces byte-identical ids. A finding re-analyzed after a review should produce the same id, which makes diffing between runs meaningful.

Second, the output schema is versioned in `Metadata.SchemaVersion`. Output-visible changes are documented per version in the [migration guide](./migration-4.0.md). Consumers that integrate Dosai JSON into pipelines should pin to a schema version and check `Metadata.SchemaVersion` before reading fields, because field meaning can change between versions: in 3.0.x `ApiEndpoint.Path` held a source file path, while from 4.0.0 it holds the resolved route and `ApiEndpoint.Route` keeps the verbatim template.

Use the `diff` command to compare a current run against a reviewed baseline, so an audit can be scoped to what changed:

```bash
dotnet run --project ./Dosai/Dosai.csproj -- diff \
  --old /tmp/baseline-dataflows.json \
  --new /tmp/dosai-dataflows.json \
  --o /tmp/dosai-diff.json
```

## Filtering evidence for reports

The `query` command narrows large outputs to the records an audit actually needs, without custom JSON tooling:

```bash
dotnet run --project ./Dosai/Dosai.csproj -- crypto \
  --path ./src --format dosai --o /tmp/dosai-crypto.json

dotnet run --project ./Dosai/Dosai.csproj -- query \
  --input /tmp/dosai-crypto.json \
  --query 'findings[ruleId~=MD5]' \
  --o /tmp/md5-findings.json

dotnet run --project ./Dosai/Dosai.csproj -- query \
  --input /tmp/dosai-dataflows.json \
  --query 'packages[reachable=true]' \
  --o /tmp/reachable-packages.json
```

The query grammar, collection aliases, and MCP usage are documented in [Dosai query language](./query-language.md).

## What this evidence does and does not prove

Dosai records what the code contains: which crypto algorithms are used, where key material appears, which packages are called, which services exist, and how data flows between them. It does not execute the target code, does not consult vulnerability databases, and does not claim that a finding is exploitable.

For a compliance workflow this is usually the right division of labor: Dosai produces reproducible, location-specific evidence, and the reviewer or risk owner decides what it means for policy obligations such as crypto inventory requirements, SBOM mandates, or data handling rules.

Limits worth stating in an audit report:

- Data-flow analysis is pattern-driven static analysis. Sanitizer modeling is conservative; project-specific validators may need custom patterns, and custom validation logic can produce false positives or negatives.
- PURL attribution is best-effort and can be ambiguous when packages share namespace prefixes.
- Reachability reflects the reconstructed call graph, including inferred dispatch edges. Findings reachable only through unmodeled runtime mechanisms may be reported as unreachable.
- Endpoint and framework detection is confidence-tiered. Filter on `Confidence` when the audit requires only symbol-resolved evidence.
