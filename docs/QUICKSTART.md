# Quickstart

This page takes you from an empty shell to your first reviewed data-flow slice in about ten minutes. Everything here works on Linux, macOS, and Windows with a single prerequisite.

## Prerequisites

```text
.NET SDK 8.0 or newer
```

That is all. Dosai runs from source with `dotnet run`, and Roslyn ships with the SDK. Optional extras widen coverage later: the `Rscript` binary improves R analysis, and `FSharp.Compiler.Service` improves F# analysis, but neither is required.

## Build and smoke test

Clone the repository, then build and test once so you know the toolchain is healthy before pointing it at anything else.

```bash
git clone https://github.com/owasp-dep-scan/dosai.git
cd dosai
dotnet build ./Dosai.sln
dotnet test ./Dosai.sln
```

Dosai analyzes its own source tree as a live smoke test, so a clean test run already proves the data-flow engine works end to end.

## Analyze a small vulnerable app

Create a tiny console app with one deliberate command-injection flaw. Keeping the code small makes it easy to check every fact Dosai reports.

```bash
dotnet new console -o /tmp/injector
cat > /tmp/injector/Program.cs << 'EOF'
using System.Diagnostics;

string command = args[0];
Process.Start(command);
EOF
```

Now run the two analysis commands that matter most.

```bash
dotnet run --project ./Dosai/Dosai.csproj -- dataflows \
  --path /tmp/injector \
  --o /tmp/injector-dataflows.json \
  --print
```

Because `Main(string[] args)` matches the built-in `cli` source pattern and `Process.Start` matches the built-in `command` sink pattern, the run produces one slice and one weakness candidate without any configuration.

```text
Dosai Data-flow Analysis
Summary: 1 flow, 1 source, 1 sink, 1 file analyzed, 1 weakness candidate
Data-flow stack traces:
└─ DataFlow dfs1: cli → command (Medium)
   Summary: cli data reaches command sink Start.
   Stack (3 frames, 3 transitions):
     at Source/cli args [dfn1] in Program.cs:4:5
        code: args
        symbol: string[] args
     via VariableAssignment [dfe1] from dfn1 to dfn2 in Program.cs:5:13 label=command
     at Assignment command [dfn2] in Program.cs:5:13
        code: command = args[0]
     via SinkArgument [dfe3] from dfn2 to dfn3 in Program.cs:6:9 label=fileName
     at Sink/command Start [dfn3] in Program.cs:6:9
        code: Process.Start(command)
        symbol: System.Diagnostics.Process.Start(string)
```

Read it like a stack trace: the tainted value starts at `args`, moves through the `command` variable, and lands in the sink argument. The JSON output keeps the same story as nodes and edges, plus a weakness candidate with CWE-78 attached.

```bash
dotnet run --project ./Dosai/Dosai.csproj -- query \
  --input /tmp/injector-dataflows.json \
  --query 'weaknesses[confidence=High]' \
  --o /tmp/high-risk.json
```

## What lands on disk

Each command writes one primary JSON file, with optional graph sidecars when you pass export flags.

```text
/tmp
├── injector-dataflows.json        DataFlowResult: nodes, edges, slices, weaknesses
├── dosai-methods.json             MethodsSlice: methods, endpoints, call graph
├── dosai-callgraph.graphml        Call graph sidecar for yEd or Gephi
└── dosai-cbom.json                CycloneDX-style CBOM from the crypto command
```

The JSON is the canonical record. Reports and printed paths are conveniences for humans and should not feed automation.

## Commands worth learning first

`methods` answers "what is here": methods, API endpoints, services, call graph, and package reachability. `dataflows` answers "where can untrusted input go": slices from sources to sinks with confidence and CWE-mapped weakness candidates. `query` filters either JSON in scripts. Those three cover most day-to-day review, and the remaining commands (`crypto`, `agent-context`, `report`, `diff`, `mcp`) layer on top of them.

The [command reference](commands.md) documents every flag, and [lesson 1](LESSON1.md) turns this quickstart into a full walkthrough with interpretation practice.
