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
collection[filter]
collection[a.b.c=value]                     # nested property path
parent.child[filter]                        # nested collection (callGraph.nodes)
collection[a=b || c=d]                      # OR inside a conjunct
collection[a=b && (c=d || c=e)]             # not supported: no grouping
collection[a=b && c~=d]                     # AND of conjuncts
collection[filter] sort by property         # ascending
collection[filter] sort by property desc    # descending
collection[filter] count                    # {"count": n}
```

Conjuncts separated by `&&` are AND-ed; terms separated by `||` inside a conjunct are OR-ed (`severity=high||severity=critical` picks either). Property names are case-insensitive. String comparisons are case-insensitive. Numeric comparisons are used when both sides parse as numbers. Elements missing the sort property sort last in both directions.

Quote query expressions in the shell because brackets, `>`, `<`, and `&&` have shell meaning:

```bash
--query 'weaknesses[confidence=High && sinkCategory=sql]'
```

## Operators

| Operator  | Meaning                       | Example                                      |
| --------- | ----------------------------- | -------------------------------------------- |
| `=`       | equals                        | `slices[sinkCategory=sql]`                   |
| `!=`      | not equals                    | `nodes[isSink!=true]`                        |
| `~=`      | contains substring            | `findings[ruleId~=MD5]`                      |
| `>`       | numeric greater than          | `nodes[lineNumber>100]`                      |
| `>=`      | numeric greater than or equal | `nodes[lineNumber>=10]`                      |
| `<`       | numeric less than             | `nodes[columnNumber<20]`                     |
| `<=`      | numeric less than or equal    | `nodes[columnNumber<=80]`                    |
| `\|\|`    | OR between filter terms       | `slices[severity=high\|\|severity=critical]` |
| `&&`      | AND between conjuncts         | `weaknesses[confidence=High && cwe=CWE-78]`  |
| `sort by` | order by a property           | `reachability[fanOut>0] sort by fanOut desc` |
| `count`   | aggregate to `{"count": n}`   | `weaknesses[severity=high] count`            |

If a property value is an array, Dosai checks whether any array element matches the filter.

## Collection aliases

| Alias                                                     | Normalized collection      | Common source           |
| --------------------------------------------------------- | -------------------------- | ----------------------- |
| `node`, `nodes`                                           | `Nodes`                    | `dataflows`             |
| `edge`, `edges`                                           | `Edges`                    | `dataflows`             |
| `slice`, `slices`                                         | `Slices`                   | `dataflows`             |
| `weakness`, `weaknesses`, `weaknessCandidates`            | `WeaknessCandidates`       | `dataflows`             |
| `entrypoint`, `entrypoints`                               | `EntryPoints`              | `methods`, `dataflows`  |
| `package`, `packages`, `packageReachability`              | `PackageReachability`      | `methods`, `dataflows`  |
| `dangerous`, `dangerousApis`, `dangerousApiReachability`  | `DangerousApiReachability` | `dataflows`             |
| `summary`, `summaries`, `methodSummaries`                 | `MethodSummaries`          | `dataflows`             |
| `exploitChain`, `exploitChains`, `chains`                 | `ExploitChains`            | `dataflows`             |
| `sanitizedFlow`, `sanitizedFlows`                         | `SanitizedFlows`           | `dataflows`             |
| `reachability`                                            | `Reachability`             | `methods`               |
| `recursionCluster`, `recursionClusters`, `clusters`       | `RecursionClusters`        | `methods`               |
| `deadCode`                                                | `DeadCode`                 | `methods`               |
| `attackSurface`, `surface`                                | `AttackSurface`            | `dataflows`             |
| `securityFinding`, `securityFindings`, `endpointFindings` | `SecurityFindings`         | `methods`               |
| `method`, `methods`                                       | `Methods`                  | `methods`               |
| `callGraph.nodes`, `callGraph.edges`                      | nested `CallGraph` arrays  | `methods`               |
| `assets`, `cryptoAssets`                                  | `Assets`                   | `crypto --format dosai` |
| `operations`, `cryptoOperations`                          | `Operations`               | `crypto --format dosai` |
| `materials`, `cryptoMaterials`                            | `Materials`                | `crypto --format dosai` |
| `protocols`, `cryptoProtocols`                            | `Protocols`                | `crypto --format dosai` |
| `findings`, `cryptoFindings`                              | `Findings`                 | `crypto --format dosai` |

Unknown collection names are treated literally (including dotted paths), so `query` can still filter future or custom top-level arrays if the JSON contains a matching property.

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

## Methods and graph examples

The call graph of a `methods` run is queryable through nested collection paths. Find internal,
non-external call-graph nodes:

```bash
dotnet run --project ./Dosai/Dosai.csproj -- query \
  --input /tmp/dosai-methods.json \
  --query 'callGraph.nodes[isExternal=false]' \
  --o /tmp/internal-nodes.json
```

Find the busiest graph nodes by fan-out (the facts live in `reachability`, not on the nodes):

```bash
dotnet run --project ./Dosai/Dosai.csproj -- query \
  --input /tmp/dosai-methods.json \
  --query 'reachability[fanOut>0] sort by fanOut desc' \
  --o /tmp/top-fanout.json
```

List dead code and count unreachable methods:

```bash
dotnet run --project ./Dosai/Dosai.csproj -- query \
  --input /tmp/dosai-methods.json \
  --query 'deadcode' \
  --o /tmp/dead-code.json

dotnet run --project ./Dosai/Dosai.csproj -- query \
  --input /tmp/dosai-methods.json \
  --query 'reachability[reachable=false] count'
```

Count high-severity weakness candidates in one line:

```bash
dotnet run --project ./Dosai/Dosai.csproj -- query \
  --input /tmp/dosai-dataflows.json \
  --query 'weaknesses[severity=high] count'
```

## Limitations

The language is intentionally compact. It does not implement full JSONPath or JMESPath. It does not support parenthesized grouping, projections, joins, wildcards, array indexes, computed expressions, or recursive descent; `||` only ORs filter terms inside a `&&` conjunct. For those operations, use `jq`, Python, or a general JSON query tool after using Dosai to produce the domain-specific JSON.
