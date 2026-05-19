# Dotnet Source and Assembly Inspector (Dosai)

List details about the namespaces, methods, dependencies, properties, fields, events, constructors, and call graphs from a C# .NET source file, assembly, or NuGet package (.nupkg).

## Usage

`Dosai [command] [options]`

### Commands:

- `methods` Retrieve details about the methods
- `dataflows` Build source-to-sink data-flow slices with framework pattern packs
- `agent-context` Generate compact agent context for AI/security review
- `query` Filter Dosai JSON with expressions such as `slices[sinkCategory=sql]`
- `mcp` Run an MCP-style JSON-RPC server over stdin/stdout
- `report`, `diff`, `policy` Generate reports, compare data-flow runs, and apply CI checks

### Options:

- `--path [path]` (REQUIRED) The file or directory to inspect (supports .dll, .exe, .cs, .vb, .fs, .nupkg)
- `--o [file]` (OPTIONAL) The output file location and name, default value when option not provided is 'dosai.json'
- `--version` Show version information
- `-?`, `-h`, `--help` Show help and usage information

### Data-flow analysis

`dataflows` includes built-in .NET source/sink packs for ASP.NET, data access, filesystem, serialization, cloud/serverless, RPC, and auth-sensitive APIs. Custom pattern JSON can add `sources`, `sinks`, `passthroughs`, and `sanitizers`; sanitizer matches stop taint propagation and validators such as `Regex.IsMatch` suppress guarded true branches.

```bash
dotnet run --project ./Dosai/Dosai.csproj -- dataflows \
  --path ./Dosai \
  --o /tmp/dosai-dataflows.json \
  --pattern-packs all \
  --graph-format graphml \
  --graph-out /tmp/dosai-dataflows.graphml
```

The data-flow engine performs field-sensitive property/field taint where receiver identity is available and emits simple interprocedural summaries for parameter-to-return and parameter-to-sink callees.

### Querying JSON

```bash
dotnet run --project ./Dosai/Dosai.csproj -- query \
  --input /tmp/dosai-dataflows.json \
  --query 'slices[sinkCategory=sql]' \
  --o /tmp/sql-slices.json
```

Supported collection aliases include `nodes`, `edges`, `slices`, `weaknesses`, `entrypoints`, `packages`, `dangerous`, and `summaries`. Filters support `=`, `!=`, `~=`, `>`, `<`, `>=`, and `<=`.

### MCP-style stdio server

```bash
printf '{"jsonrpc":"2.0","id":1,"method":"tools/list"}\n' | \
  dotnet run --project ./Dosai/Dosai.csproj -- mcp --path ./Dosai
```

The server exposes `dosai.methods`, `dosai.dataflows`, `dosai.agent_context`, and `dosai.query` tool calls as line-delimited JSON-RPC responses.

### API authorization metadata

Endpoint extraction records richer auth context from attributes and common minimal API chains, including authorization policies, roles, authentication schemes, required scopes/claims, CORS policies, anonymous access, and antiforgery hints.

---

## Developers

### Running code directly from the code repository

1. `dotnet build ./Dosai`
2. Run a command such as:
   - `dotnet run --project ./Dosai/ methods --path ./Dosai/bin/x64/Debug/net10.0/Dosai.dll`
   - `dotnet run --project ./Dosai/ methods --path ./Dosai/Dosai.cs`
   - `dotnet run --project ./Dosai/ methods --path ./MyPackage.1.0.0.nupkg`

### Generating a self-contained executable for a system

- Windows: `dotnet publish -r win-x64 --self-contained`
- Linux: `dotnet publish -r linux-x64 --self-contained`

### Invoking the self-contained executable

- Windows: `Dosai.exe methods --path ./Dosai/bin/x64/Debug/net8.0/Dosai.dll`
- Linux: `Dosai methods --path ./Dosai/Dosai.cs`

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

- **`GetSourceMethods`**: Uses Roslyn's `SyntaxTree`, `SemanticModel`, and symbol analysis (`IMethodSymbol`, `INamespaceSymbol`, etc.) to parse source files (.cs, .vb, .fs) and extract method signatures, dependencies (`using` directives), property/field/event declarations, and call graph information.
- **`GetAssemblyMethods`**: Uses .NET Reflection (`Assembly.LoadFrom`, `Type.GetMethods`, etc.) to load compiled assemblies (.dll, .exe) and extract method metadata, including signatures, attributes, and inheritance details.
- **`GetAssemblyInformation`**: Uses Reflection and `FileVersionInfo` to gather metadata about assemblies such as version, location, dependencies, and target framework.
- **`GetMethodsFromNupkg`**: Extracts the .nupkg archive (ZIP format) to a temporary directory, filtering for relevant .NET assemblies and source files, then delegates analysis to the existing `GetMethods` logic.

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
