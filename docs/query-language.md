# Dosai query language

Use `query` to filter Dosai JSON outputs in shell scripts, CI jobs, and agent workflows without writing custom JSON traversal code.

```bash
dotnet run --project ./Dosai/Dosai.csproj -- query \
  --input /tmp/dosai-dataflows.json \
  --query 'slices[sinkCategory=command]' \
  --o /tmp/command-slices.json
```

The command always writes a JSON array containing matching elements.

## Grammar

```text
collection
collection[property=value]
collection[property!=value]
collection[property~=substring]
collection[number>10]
collection[number>=10]
collection[number<10]
collection[number<=10]
collection[a=b && c~=d]
collection[nested.property=value]
```

Filters are combined with logical AND using `&&`. Property names are case-insensitive. String comparisons are case-insensitive. Numeric comparisons are used when both sides parse as numbers.

Quote query expressions in the shell because brackets, `>`, `<`, and `&&` have shell meaning:

```bash
--query 'weaknesses[confidence=High && sinkCategory=sql]'
```

## Operators

| Operator | Meaning                       | Example                    |
| -------- | ----------------------------- | -------------------------- |
| `=`      | equals                        | `slices[sinkCategory=sql]` |
| `!=`     | not equals                    | `nodes[isSink!=true]`      |
| `~=`     | contains substring            | `findings[ruleId~=MD5]`    |
| `>`      | numeric greater than          | `nodes[lineNumber>100]`    |
| `>=`     | numeric greater than or equal | `nodes[lineNumber>=10]`    |
| `<`      | numeric less than             | `nodes[columnNumber<20]`   |
| `<=`     | numeric less than or equal    | `nodes[columnNumber<=80]`  |

If a property value is an array, Dosai checks whether any array element matches the filter.

## Collection aliases

| Alias                                                    | Normalized collection      | Common source           |
| -------------------------------------------------------- | -------------------------- | ----------------------- |
| `node`, `nodes`                                          | `Nodes`                    | `dataflows`             |
| `edge`, `edges`                                          | `Edges`                    | `dataflows`             |
| `slice`, `slices`                                        | `Slices`                   | `dataflows`             |
| `weakness`, `weaknesses`, `weaknessCandidates`           | `WeaknessCandidates`       | `dataflows`             |
| `entrypoint`, `entrypoints`                              | `EntryPoints`              | `methods`, `dataflows`  |
| `package`, `packages`, `packageReachability`             | `PackageReachability`      | `methods`, `dataflows`  |
| `dangerous`, `dangerousApis`, `dangerousApiReachability` | `DangerousApiReachability` | `dataflows`             |
| `summary`, `summaries`, `methodSummaries`                | `MethodSummaries`          | `dataflows`             |
| `assets`, `cryptoAssets`                                 | `Assets`                   | `crypto --format dosai` |
| `operations`, `cryptoOperations`                         | `Operations`               | `crypto --format dosai` |
| `materials`, `cryptoMaterials`                           | `Materials`                | `crypto --format dosai` |
| `protocols`, `cryptoProtocols`                           | `Protocols`                | `crypto --format dosai` |
| `findings`, `cryptoFindings`                             | `Findings`                 | `crypto --format dosai` |

Unknown collection names are treated literally, so `query` can still filter future top-level arrays if the JSON contains a matching property.

## Data-flow examples

Find command-injection candidate slices:

```bash
dotnet run --project ./Dosai/Dosai.csproj -- query \
  --input /tmp/dosai-dataflows.json \
  --query 'slices[sinkCategory=command]' \
  --o /tmp/command-slices.json
```

Find high-confidence weakness candidates:

```bash
dotnet run --project ./Dosai/Dosai.csproj -- query \
  --input /tmp/dosai-dataflows.json \
  --query 'weaknesses[confidence=High]' \
  --o /tmp/high-confidence-weaknesses.json
```

Find source nodes from a specific file:

```bash
dotnet run --project ./Dosai/Dosai.csproj -- query \
  --input /tmp/dosai-dataflows.json \
  --query 'nodes[isSource=true && fileName=Program.cs]' \
  --o /tmp/program-sources.json
```

Find method summaries that mark a parameter as reaching a sink. `SinkParameterIndexes` is an array, so this matches if any element equals `0`:

```bash
dotnet run --project ./Dosai/Dosai.csproj -- query \
  --input /tmp/dosai-dataflows.json \
  --query 'summaries[sinkParameterIndexes=0]' \
  --o /tmp/parameter-zero-sink-summaries.json
```

## Crypto examples

Find weak MD5 findings in native Dosai crypto JSON:

```bash
dotnet run --project ./Dosai/Dosai.csproj -- crypto \
  --path ./src \
  --format dosai \
  --o /tmp/dosai-crypto.json

dotnet run --project ./Dosai/Dosai.csproj -- query \
  --input /tmp/dosai-crypto.json \
  --query 'findings[ruleId~=MD5]' \
  --o /tmp/md5-findings.json
```

Find crypto assets classified as weak:

```bash
dotnet run --project ./Dosai/Dosai.csproj -- query \
  --input /tmp/dosai-crypto.json \
  --query 'assets[strength=weak]' \
  --o /tmp/weak-crypto-assets.json
```

Find hardcoded material findings:

```bash
dotnet run --project ./Dosai/Dosai.csproj -- query \
  --input /tmp/dosai-crypto.json \
  --query 'findings[ruleId~=HARDCODED]' \
  --o /tmp/hardcoded-material-findings.json
```

## MCP/agent use

The MCP server exposes the same query engine as `dosai.query`. It can query an existing file:

```bash
printf '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"dosai.query","arguments":{"input":"/tmp/dosai-dataflows.json","query":"slices[sinkCategory=sql]"}}}\n' | \
  dotnet run --project ./Dosai/Dosai.csproj -- mcp --path ./src
```

If `input` is omitted, `dosai.query` first runs data-flow analysis for the configured path and then filters the generated result:

```bash
printf '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"dosai.query","arguments":{"query":"weaknesses[confidence=High]"}}}\n' | \
  dotnet run --project ./Dosai/Dosai.csproj -- mcp --path ./src
```

## Limitations

The language is intentionally compact. It does not implement full JSONPath or JMESPath. It does not support OR expressions, grouping, sorting, projections, joins, wildcards, array indexes, computed expressions, or recursive descent. For those operations, use `jq`, Python, or a general JSON query tool after using Dosai to produce the domain-specific JSON.
