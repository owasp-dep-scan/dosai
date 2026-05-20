# Dotnet Source and Assembly Inspector (Dosai)

Dosai inspects source code, assemblies, and NuGet packages. It extracts methods, dependencies, API endpoints, call graphs, data-flow slices, crypto evidence, and package reachability facts for security review.

## Usage

`Dosai [command] [options]`

### Commands

Use `methods` for method inventory, endpoints, call graph, and dependency evidence. For managed assemblies, `methods` also extracts IL method-body call edges, portable PDB call locations, delegate targets, and lightweight virtual-call candidates. Use `dataflows` for source-to-sink slicing. Use `crypto` for cryptographic assets, materials, misuse findings, reachability, and CBOM evidence. `agent-context`, `query`, `mcp`, `report`, and `diff` support review automation and CI workflows.

For detailed command usage, implementation notes, algorithms, strengths, and limitations, see [the Dosai command reference](./docs/commands.md).

### Common options

`--path` is the file or directory to inspect. `--o` sets the output path and defaults to `dosai.json`. Use `--help` for command-specific options.

### Data-flow analysis

`dataflows` includes built-in .NET source and sink packs for ASP.NET, data access, filesystem, serialization, cloud/serverless, RPC, auth-sensitive APIs, and crypto-sensitive APIs. Custom pattern JSON can add `sources`, `sinks`, `passthroughs`, and `sanitizers`. Sanitizer matches stop taint propagation, and validators such as `Regex.IsMatch` suppress guarded true branches.

```bash
dotnet run --project ./Dosai/Dosai.csproj -- dataflows \
  --path ./Dosai \
  --o /tmp/dosai-dataflows.json \
  --pattern-packs all \
  --graph-format graphml \
  --graph-out /tmp/dosai-dataflows.graphml
```

The data-flow engine performs field-sensitive property/field taint where receiver identity is available and emits simple interprocedural summaries for parameter-to-return and parameter-to-sink callees. For C# and VB source it uses Roslyn `IOperation`; for assembly-only inputs it reconstructs method-body flow from IL metadata, control-flow branches, portable PDB sequence points and local scopes, async/iterator/display-class captured fields, external passthrough summaries, emitted framework attributes, and package dependency scope. Slices can carry taint kinds, field paths, confidence, source/assembly evidence, and F#/R/VC++ frontend evidence for common script and native input and sink patterns.

`dataflows` is quiet by default and writes the JSON/graph artifacts. Add `--print` during local triage to render each slice as a stack-trace-style path with frames such as `at Source/cli args [dfn1] in Program.cs:5:5`, code snippets, symbols, PURLs, and `via ...` edge transitions:

```bash
dotnet run --project ./Dosai/Dosai.csproj -- dataflows \
  --path ./Dosai \
  --o /tmp/dosai-dataflows.json \
  --print
```

Pass custom patterns with `--patterns ./dataflow-patterns.json`; the file is merged with built-in patterns. See [Data-flow custom patterns](./docs/dataflow-patterns.md) for the JSON schema, pattern kinds, and examples, and [Built-in data-flow pattern pack catalog](./docs/pattern-packs.md) for the contents of `--pattern-packs`.

The analyzer is optimized for full source-tree CI runs: pattern matching is indexed by hot lookup kind, syntax text is cached for code-like matches, assembly dependency directories are scoped with `.deps.json` when available, and slice construction uses indexed graph edges. Dosai's CI smoke test runs `dataflows --path ./Dosai` and assembly-only fixtures to guard both source and binary paths.

Source, binary, and combined analysis share a method identity and evidence model. Method inventory records, call graph nodes/edges, method calls, data-flow nodes, and method summaries can identify whether evidence came from Roslyn source, assembly metadata, IL call/data-flow reconstruction, delegate targets, virtual candidates, external summaries, framework models, or language frontends.

### Cryptography and CBOM evidence

`crypto` detects algorithms, operations, key and certificate material, TLS settings, weak algorithms, hardcoded material, static IVs and nonces, insecure RNG, disabled certificate validation, legacy TLS references, and low PBKDF2 iteration counts. Findings include source locations and best-effort reachability from CLI and API entry points when callgraph data is available.

Native Dosai JSON:

```bash
dotnet run --project ./Dosai/Dosai.csproj -- crypto \
  --path ./Dosai \
  --o /tmp/dosai-crypto.json \
  --format dosai
```

Combined CycloneDX-style CBOM output:

```bash
dotnet run --project ./Dosai/Dosai.csproj -- crypto \
  --path ./Dosai \
  --o /tmp/dosai-cbom.json \
  --format cyclonedx
```

The CycloneDX mode preserves Dosai properties such as `dosai:crypto:family`, `dosai:crypto:strength`, `dosai:crypto:reachableFromEntryPoint`, `dosai:crypto:evidenceType`, and `dosai:location` so downstream BOM tooling can correlate code-level crypto assets, operations, materials, protocols, and findings with package BOMs without a separate evidence sidecar.

### F#, R, and VC++ frontends

Dosai also analyzes F#, R, and VC++/C/C++ source. The F# frontend uses `FSharp.Compiler.Service` when available and records compiler-service evidence for `.fs`, `.fsi`, and `.fsx` files. The R frontend uses `Rscript` with R's native `getParseData` parser when R is installed, then falls back to managed lexical extraction if needed. The VC++ frontend extracts functions, includes, calls, native sinks, and crypto/TLS evidence from `.c`, `.cpp`, `.cc`, `.cxx`, `.h`, `.hpp`, and `.hh` files without requiring `compile_commands.json`.

Frontend evidence is conservative when project metadata is incomplete. It still provides inventory, callgraph, data-flow, and crypto coverage without failing analysis on missing references, missing R installations, or absent native build metadata.

### Querying JSON

```bash
dotnet run --project ./Dosai/Dosai.csproj -- query \
  --input /tmp/dosai-dataflows.json \
  --query 'slices[sinkCategory=sql]' \
  --o /tmp/sql-slices.json
```

Supported collection aliases include `nodes`, `edges`, `slices`, `weaknesses`, `entrypoints`, `packages`, `dangerous`, `summaries`, `assets`, `operations`, `materials`, `protocols`, and `findings`. Filters support `=`, `!=`, `~=`, `>`, `<`, `>=`, and `<=`.

For operators, aliases, nested-property filters, and MCP query examples, see [Dosai query language](./docs/query-language.md).

### MCP-style stdio server

```bash
printf '{"jsonrpc":"2.0","id":1,"method":"tools/list"}\n' | \
  dotnet run --project ./Dosai/Dosai.csproj -- mcp --path ./Dosai
```

For local-agent loops, MCP tool calls, prompt-size strategy, and CI automation recipes, see [AI-agent and automation workflows](./docs/agent-workflows.md).

The server exposes `dosai.methods`, `dosai.dataflows`, `dosai.crypto`, `dosai.agent_context`, and `dosai.query` tool calls as line-delimited JSON-RPC responses.

### API authorization metadata

Endpoint extraction records richer auth context from attributes and common minimal API chains, including authorization policies, roles, authentication schemes, required scopes/claims, CORS policies, anonymous access, and antiforgery hints.

---

## Developers

### Running code directly from the code repository

Build with `dotnet build ./Dosai`, then run a command such as:

```bash
dotnet run --project ./Dosai -- methods --path ./Dosai/Dosai.cs
dotnet run --project ./Dosai -- methods --path ./MyPackage.1.0.0.nupkg
dotnet run --project ./Dosai -- crypto --path ./Dosai --format cyclonedx --o /tmp/dosai-cbom.json
```

### Generating a self-contained executable for a system

For Windows, run `dotnet publish -r win-x64 --self-contained`. For Linux, run `dotnet publish -r linux-x64 --self-contained`.

### Invoking the self-contained executable

After publishing, invoke `Dosai.exe methods --path ./app.dll` on Windows or `Dosai methods --path ./src` on Linux.

### Run unit tests

`dotnet test`

---

## Technical Overview

Dosai uses the Microsoft.CodeAnalysis (Roslyn) API and .NET Reflection to extract metadata from source code and compiled assemblies. It provides a unified view of code structure and dependencies across different .NET compilation outputs.

### Core Components

```
┌─────────────────┐    ┌─────────────────┐    ┌─────────────────┐
│   Source Code   │    │  .NET Assembly  │    │   .nupkg File   │
│   (.cs, .vb)    │    │  (.dll, .exe)   │    │                 │
└─────────┬───────┘    └─────────┬───────┘    └─────────┬───────┘
          │                      │                      │
          │                      │                      │ (Extract)
          ▼                      ▼                      ▼
    ┌─────────────┐      ┌─────────────┐        ┌─────────────┐
    │  Roslyn     │      │  Reflection │        │  Extracted  │
    │  Analysis   │      │  Analysis   │───────▶│  Directory  │
    │             │      │             │        │             │
    └──────┬──────┘      └──────┬──────┘        └──────┬──────┘
           │                    │                      │
           │                    │                      │
           └────────────────────┼──────────────────────┘
                                │
                                ▼
                        ┌─────────────────┐
                        │  Unified JSON   │
                        │   Output Model  │
                        │ (MethodsSlice)  │
                        └─────────────────┘
```

- **`GetSourceMethods`**: Uses Roslyn's `SyntaxTree`, `SemanticModel`, and symbol analysis (`IMethodSymbol`, `INamespaceSymbol`, etc.) for C# and VB source. F#, R, and VC++/C/C++ use dedicated language frontends. The method pipeline extracts signatures, dependencies, property/field/event declarations, endpoint metadata, and call graph information.
- **`GetAssemblyMethods`**: Uses .NET Reflection (`Assembly.LoadFrom`, `Type.GetMethods`, etc.) to load compiled assemblies (.dll, .exe) and extract method metadata, including signatures, attributes, and inheritance details.
- **`GetAssemblyInformation`**: Uses Reflection and `FileVersionInfo` to gather metadata about assemblies such as version, location, dependencies, and target framework.
- **`GetMethodsFromNupkg`**: Extracts the .nupkg archive (ZIP format) to a temporary directory, filters relevant assemblies and source files, then delegates analysis to the existing `GetMethods` logic.
- **`DataFlowAnalyzer`**: Builds source-to-sink slices with pattern packs, sanitizer handling, validator guard suppression, method summaries, field-sensitive taint keys, graph exports, package reachability, and weakness candidates.
- **`CryptoAnalyzer`**: Detects cryptographic assets, operations, materials, weak crypto, TLS validation bypasses, low PBKDF2 iterations, and CBOM evidence with best-effort reachability.

### Output Schema

The output is a JSON object conforming to the `MethodsSlice` class structure, containing:

- **`Dependencies`**: List of external namespaces/libraries used.
- **`Methods`**: List of `Method` objects detailing signatures, locations, parameters, return types, etc.
- **`MethodCalls`**: List of `MethodCalls` objects representing invocations found in source code.
- **`Properties`, `Fields`, `Events`, `Constructors`**: Lists of corresponding member types found in source.
- **`CallGraph`**: List of `MethodCallEdge` objects defining the call graph structure.
- **`AssemblyInformation`**: List of `AssemblyInfo` objects detailing the inspected assemblies.
- **`SourceAssemblyMapping`**: List of `SourceAssemblyMapping` objects linking source locations to assembly definitions.

### NuGet Package (.nupkg) Handling

`.nupkg` files are ZIP archives. `GetMethodsFromNupkg` performs the following steps:

1.  Creates a temporary directory.
2.  Uses `System.IO.Compression.ZipFile.OpenRead` to read the .nupkg.
3.  Enumerates entries, skipping metadata files (`.nuspec`, `package/`, etc.).
4.  Extracts files with relevant extensions (`.dll`, `.exe`, `.cs`, `.vb`, `.fs`).
5.  Calls the standard `GetMethods` on the temporary directory.
6.  Cleans up the temporary directory after analysis.

## Complementary Analysis with OWASP blint

See [this document](./BLINT-INTEGRATION.md) for integration ideas.

## Integration with YARA cli

See [Yara Usage docs](./YARA-USAGE.md)

## License

MIT
