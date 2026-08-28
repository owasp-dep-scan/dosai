![logo](_media/dosai.svg)

# Dosai

> Source and assembly inspection for .NET security review.

[Quickstart](QUICKSTART.md) · [Security analyst guide](security-analysis.md) · [Command reference](commands.md) · [Lessons](LESSON1.md)

Dosai reads .NET source trees, compiled assemblies, and NuGet packages, and turns them into facts a reviewer can act on: method inventories, call graphs, API endpoints, service inventories, source-to-sink data-flow slices, cryptographic evidence, and package reachability.

## What Dosai helps you do

- Triage injection and dangerous-API risk with source-to-sink slices and weakness candidates mapped to CWEs
- Build a CycloneDX-style CBOM with code-level crypto evidence, redacted material fingerprints, and reachability
- Map HTTP, RPC, messaging, serverless, MCP, and AI surfaces with trust zones and data classification
- Correlate findings to NuGet packages with PURL enrichment so SBOM reviews get reachability evidence

## Choose your path

### Security analysts

Start with the [security analyst guide](security-analysis.md), then learn the [pattern packs](pattern-packs.md) and the [query language](query-language.md) for filtering large outputs.

### Compliance and audit teams

The [compliance and audit guide](compliance.md) explains which artifacts to generate for crypto review, service inventory, and AI governance, and states plainly what the evidence does and does not prove.

### Analyzer engineers

The [architecture overview](ARCHITECTURE.md) and the [compiler engineering notes](compiler-engineering.md) describe the Roslyn and IL pipelines behind the outputs.

### Agent builders

The [agent workflows guide](agent-workflows.md) covers the MCP server, compact agent context, and prompt-size strategy for local review automation.
