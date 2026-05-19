# Dosai Advanced Transparency Implementation Plan

This plan captures the autonomous implementation roadmap for making Dosai a best-in-class .NET transparency, supply-chain reachability, vulnerability-analysis, and AI-agent context engine.

## Guiding principles

- Prefer semantic Roslyn `IOperation` analysis when symbols are available.
- Preserve safe fallback behavior for missing references and legacy projects.
- Keep graph edge endpoints valid at all times.
- PURL enrichment is best-effort and must never fail analysis.
- Emit deterministic JSON suitable for humans, CI, and AI agents.
- Avoid dependency vulnerability management and license detection; those belong to depscan.

## Phase 1: Core transparency schema

1. Add `SchemaVersion`, `AnalyzerVersion`, `GeneratedAt`, and `InputPath` metadata to all major outputs.
2. Add unified `EntryPoints` covering:
   - HTTP MVC/Web API attributes
   - Minimal APIs
   - CLI `Main`
   - VB.NET `Main`
   - message/request/command/query handlers
   - future serverless/gRPC/Orleans hooks
3. Link entrypoints to methods where stable IDs are available.
4. Add API authorization metadata where visible from attributes/fluent calls.

## Phase 2: Package and dangerous API reachability

1. Build `PackageReachability` facts from call graph and data-flow PURLs.
2. Build `DangerousApiReachability` facts from sink categories and call graph target categories.
3. Link packages to:
   - reachable call graph nodes
   - data-flow slices
   - entrypoints
   - source/sink categories
4. Add CLI validation/reporting for reachability summaries.

## Phase 3: Weakness candidates

1. Convert source-to-sink slices into deterministic weakness candidates:
   - command injection
   - path traversal / file read / file write
   - SQL injection
   - SSRF / outbound network
   - open redirect
   - unsafe deserialization
   - reflection/dynamic loading
   - RPC dispatch exposure
2. Add evidence bundles:
   - source node
   - sink node
   - route/entrypoint if known
   - PURLs
   - code snippets
   - graph path
3. Add confidence scoring with explainable reasons.

## Phase 4: Better data-flow precision

1. Add typed taint kinds.
2. Add sanitizer/validator pattern target and annotations.
3. Add field/property-sensitive propagation for common MVC/service patterns.
4. Add simple method summaries:
   - return tainted if argument tainted
   - parameter-to-sink summaries
   - field assignment summaries
5. Improve async/await and task propagation.
6. Improve collection/indexer propagation.
7. Improve VB.NET parity:
   - WebForms event handlers
   - `Handles` clauses
   - `Request.QueryString`
   - `Server.MapPath`
   - `Response.Redirect`
   - `My.Computer.FileSystem`

## Phase 5: AI-agent outputs

1. Add `agent-context` command producing compact JSON/Markdown context.
2. Add `explain`/`report` command for slices and weakness candidates.
3. Add `neighborhood` extraction around nodes and slices.
4. Add query/diff/policy helpers:
   - new endpoints
   - new slices
   - new package reachability
   - invalid graph edges
   - minimum slice counts

## Phase 6: Workflows and regression corpus

1. Keep fast unit/integration tests self-contained.
2. Add scheduled/manual vulnerable repo smoke workflow.
3. Expand public vulnerable .NET repo corpus incrementally.
4. Validate:
   - minimum slices
   - no invalid graph edges
   - graph XML parses
   - endpoint extraction does not regress
   - PURL enrichment does not regress where assets exist

## Phase 7: Documentation

Update docs for:

- compiler engineering internals
- security analyst workflows
- supply-chain/PURL analysis
- graph formats
- agent usage
- threat model
- security policy
- advanced reachability and weakness candidate model

## Current high-impact implementation target

Implement in this coding session:

- Core output metadata.
- Unified entrypoint DTOs and extraction.
- Package reachability and dangerous API reachability facts.
- Weakness candidates with confidence and evidence.
- Data-flow sanitizer/taint metadata basics.
- Agent context/report/diff/policy commands if time permits.
- Tests and workflow updates for the above.
