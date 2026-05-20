# Data-flow custom patterns

Use `--patterns <file.json>` with the `dataflows` command to add project-specific sources, sinks, passthrough calls, and sanitizers.

```bash
dotnet run --project ./Dosai/Dosai.csproj -- dataflows \
  --path ./src \
  --patterns ./dataflow-patterns.json \
  --o /tmp/dosai-dataflows.json \
  --graph-format graphml \
  --graph-out /tmp/dosai-dataflows.graphml
```

User patterns are merged with Dosai's built-in patterns; they do not replace the defaults. `--pattern-packs` controls optional built-in packs and defaults to `all`.

```bash
dotnet run --project ./Dosai/Dosai.csproj -- dataflows \
  --path ./src \
  --patterns ./dataflow-patterns.json \
  --pattern-packs aspnet,data,filesystem \
  --o /tmp/dosai-dataflows.json
```

Supported pattern packs are `all`, `aspnet`, `data`, `filesystem`, `serialization`, `cloud`, `rpc`, `auth`, and `crypto`. See [Built-in data-flow pattern pack catalog](./pattern-packs.md) for the always-on defaults and the exact patterns added by each pack.

## Pattern file shape

The file is a JSON object with any of these arrays:

```json
{
  "sources": [],
  "sinks": [],
  "passthroughs": [],
  "sanitizers": []
}
```

Property names are case-insensitive. The examples use lowercase collection names for readability. Each pattern is normalized to the collection that contains it, so you normally do not need to set `target` inside each item.

Each pattern supports these fields:

| Field               | Required | Values                                                                            | Purpose                                                                                                                    |
| ------------------- | -------- | --------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------- |
| `pattern`           | yes      | string                                                                            | Text or regex to match.                                                                                                    |
| `kind`              | no       | `Symbol`, `Method`, `Type`, `Namespace`, `Name`, `Parameter`, `Attribute`, `Code` | Which semantic/code value to compare. Defaults to `Symbol`.                                                                |
| `match`             | no       | `Contains`, `Exact`, `Prefix`, `Suffix`, `Regex`                                  | Match mode. Defaults to `Contains`.                                                                                        |
| `category`          | no       | string                                                                            | Source/sink category emitted in nodes, slices, and weakness candidates.                                                    |
| `purl`              | no       | package URL string                                                                | Optional package/package-like identity to attach to matching nodes.                                                        |
| `description`       | no       | string                                                                            | Human-readable reason for the pattern.                                                                                     |
| `taintKinds`        | no       | string array                                                                      | Labels carried with the taint trace, such as `user-input`, `secret`, or `path`.                                            |
| `removesTaintKinds` | no       | string array                                                                      | Reserved for sanitizer metadata. Current sanitizer matching stops taint for the matched expression or guarded true branch. |
| `confidence`        | no       | string                                                                            | Confidence label. Defaults to `Medium`; common values are `Low`, `Medium`, and `High`.                                     |

## Pattern kinds

Prefer semantic kinds over `Code` when possible. Semantic matching is more accurate and avoids extra syntax-text work in hot loops.

| Kind        | What it matches                                                  | Common use                                                                |
| ----------- | ---------------------------------------------------------------- | ------------------------------------------------------------------------- |
| `Method`    | Resolved method/constructor symbol display text.                 | API sources, sinks, sanitizers, and passthrough helpers.                  |
| `Symbol`    | Resolved symbol display text for referenced members.             | Properties, fields, or broad member matches.                              |
| `Type`      | Operation type or containing type.                               | Request/context objects, command DTOs, dangerous object types.            |
| `Namespace` | Containing namespace.                                            | Framework or RPC namespace families.                                      |
| `Name`      | Simple symbol or method name.                                    | Legacy wrappers when namespaces are unstable.                             |
| `Parameter` | Method parameter name or `name type` text. Source patterns only. | Handler parameters such as `request`, `input`, or command DTO parameters. |
| `Attribute` | Method, parameter, or symbol attribute names.                    | ASP.NET, Azure Functions, queue triggers, custom binding attributes.      |
| `Code`      | Syntax text.                                                     | Last-resort fallback for unresolved references or legacy source shapes.   |

`Parameter` patterns intentionally seed method parameters only; they do not match arbitrary local variables with the same name.

## Source and assembly matching behavior

For source inputs, `Code` patterns match syntax text and are useful as a fallback for unresolved references or legacy source shapes. For assembly-only inputs, Dosai has metadata symbols and IL literals rather than original syntax. To keep binary analysis precise, assembly matching applies semantic `Method`, `Symbol`, `Type`, `Namespace`, and `Name` patterns to resolved metadata members; `Code` source patterns are applied only to literal IL evidence such as `ldstr` strings, not to arbitrary metadata names. `Parameter` source patterns still seed method parameters, including `Main(string[] args)` as `cli`, and portable PDBs are used for source locations when available.

Short broad `Name`/`Contains` source patterns are intentionally filtered in assembly metadata mode to avoid noisy matches against compiler-generated members such as `get_Key`. Prefer `Method`, `Type`, or exact/regex `Parameter` patterns for binary-friendly custom rules.

## Example: custom legacy source and sink wrappers

This example marks `LegacyInput.ReadUntrusted()` as a source and `LegacyShell.Exec(...)` as a command sink. It also marks `NormalizeCommand(...)` as a passthrough so taint survives a local normalization wrapper, and `AllowListedCommand(...)` as a validator/sanitizer.

`dataflow-patterns.json`:

```json
{
  "sources": [
    {
      "kind": "Method",
      "match": "Contains",
      "pattern": "LegacyInput.ReadUntrusted",
      "category": "legacy-input",
      "description": "Legacy input helper",
      "taintKinds": ["user-input"],
      "confidence": "High"
    }
  ],
  "sinks": [
    {
      "kind": "Method",
      "match": "Contains",
      "pattern": "LegacyShell.Exec",
      "category": "command",
      "description": "Legacy shell wrapper",
      "confidence": "High"
    }
  ],
  "passthroughs": [
    {
      "kind": "Method",
      "match": "Contains",
      "pattern": "NormalizeCommand",
      "category": "string",
      "description": "Normalization preserves command taint"
    }
  ],
  "sanitizers": [
    {
      "kind": "Method",
      "match": "Contains",
      "pattern": "AllowListedCommand",
      "category": "validation",
      "description": "Allow-list validator suppresses taint in guarded true branches"
    }
  ]
}
```

Run it:

```bash
dotnet run --project ./Dosai/Dosai.csproj -- dataflows \
  --path ./path/to/repo \
  --patterns ./dataflow-patterns.json \
  --o /tmp/dosai-dataflows.json \
  --print-sources-sinks
```

The `--print-sources-sinks` output is useful while tuning patterns because it shows each matched source and sink with category, file location, symbol, PURL, and code.

## Example: parameter source and SQL wrapper sink

Use `Parameter` for framework or application handler parameters that should be treated as untrusted input.

```json
{
  "sources": [
    {
      "kind": "Parameter",
      "match": "Regex",
      "pattern": "^(request|command|query|input)$",
      "category": "message",
      "description": "Application message handler input",
      "taintKinds": ["user-input"]
    }
  ],
  "sinks": [
    {
      "kind": "Method",
      "match": "Contains",
      "pattern": "SqlRunner.ExecuteRaw",
      "category": "sql",
      "description": "Internal raw SQL execution wrapper"
    }
  ]
}
```

## Example: unresolved legacy API fallback

Use `Code` sparingly when references are missing and Roslyn cannot resolve a symbol. This is less precise, but useful for legacy projects.

```json
{
  "sinks": [
    {
      "kind": "Code",
      "match": "Contains",
      "pattern": "DangerousEval(",
      "category": "code-execution",
      "description": "Legacy dynamic execution helper seen in source text",
      "confidence": "Low"
    }
  ]
}
```

## Tuning workflow

1. Start with `Kind = "Method"`, `"Type"`, `"Name"`, `"Parameter"`, or `"Attribute"` before using `"Code"`.
2. Use `Match = "Contains"` while exploring, then tighten to `"Exact"`, `"Prefix"`, `"Suffix"`, or `"Regex"` to reduce false positives.
3. Run with `--print-sources-sinks` and inspect the emitted `Patterns`, `Nodes`, and `Slices` in the JSON output.
4. Add focused tests or harness cases when a custom pattern represents a reusable heuristic.
5. Keep regexes simple. Regex patterns run during analysis and can be expensive if they backtrack heavily.
