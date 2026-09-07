# Built-in data-flow pattern pack catalog

Dosai data-flow analysis always starts with an always-on baseline pattern set, then applies optional built-in packs selected by `--pattern-packs`, and finally merges user patterns from `--patterns`.

```bash
dotnet run --project ./Dosai/Dosai.csproj -- dataflows \
  --path ./src \
  --pattern-packs aspnet,data,filesystem \
  --o /tmp/dosai-dataflows.json
```

`--pattern-packs` defaults to `all`. `all` expands to:

```text
aspnet,data,filesystem,serialization,cloud,rpc,auth,crypto,grpc,messaging,ai,mcp,xss,xxe,injection,log,redos,template
```

User patterns passed with `--patterns` are merged after the selected built-in packs. See [Data-flow custom patterns](./dataflow-patterns.md) for the custom pattern file format.

## Selection model

| Input                         | Effective optional packs                                                                                                                                                   | Notes                                          |
| ----------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------- |
| omitted                       | `aspnet`, `data`, `filesystem`, `serialization`, `cloud`, `rpc`, `auth`, `crypto`, `grpc`, `messaging`, `ai`, `mcp`, `xss`, `xxe`, `injection`, `log`, `redos`, `template` | Default behavior.                              |
| `--pattern-packs all`         | all optional packs                                                                                                                                                         | Same as omitted.                               |
| `--pattern-packs aspnet,data` | `aspnet`, `data`                                                                                                                                                           | Always-on defaults still apply.                |
| `--pattern-packs crypto`      | `crypto`                                                                                                                                                                   | Adds crypto taint patterns on top of defaults. |

## Always-on baseline patterns

These patterns are always loaded before optional packs.

### Always-on sources

| Kind        | Match      | Pattern                                                                                    | Category     | Purpose                                     |
| ----------- | ---------- | ------------------------------------------------------------------------------------------ | ------------ | ------------------------------------------- |
| `Parameter` | `Exact`    | `Main`                                                                                     | `cli`        | Command-line `Main` arguments.              |
| `Parameter` | `Exact`    | `request`                                                                                  | `message`    | Request/message handler input.              |
| `Parameter` | `Exact`    | `command`                                                                                  | `message`    | Command handler input.                      |
| `Parameter` | `Exact`    | `query`                                                                                    | `message`    | Query handler input.                        |
| `Parameter` | `Exact`    | `model`                                                                                    | `http`       | MVC model-bound input.                      |
| `Parameter` | `Exact`    | `input`                                                                                    | `input`      | Generic input parameter.                    |
| `Attribute` | `Prefix`   | `HttpGet`, `HttpPost`, `HttpPut`, `HttpDelete`, `HttpPatch`                                | `http`       | ASP.NET endpoint parameters.                |
| `Attribute` | `Prefix`   | `Route`                                                                                    | `http`       | ASP.NET route endpoint parameter.           |
| `Attribute` | `Contains` | `FunctionName`, `HttpTrigger`                                                              | `serverless` | Azure Function entry points.                |
| `Type`      | `Contains` | `Microsoft.AspNetCore.Http.HttpRequest`, `Microsoft.AspNetCore.Http.HttpContext`           | `http`       | ASP.NET request/context objects.            |
| `Type`      | `Contains` | `Microsoft.AspNetCore.Http.IFormFile`                                                      | `http`       | Uploaded files.                             |
| `Method`    | `Contains` | `System.Console.ReadLine`                                                                  | `cli`        | Console input.                              |
| `Symbol`    | `Contains` | `.Request.Query`, `.Request.Form`, `.Request.Body`, `.Request.Headers`, `.Request.Cookies` | `http`       | ASP.NET request collections.                |
| `Code`      | `Contains` | `Request[`, `Request.QueryString`                                                          | `http`       | Legacy ASP.NET request collection fallback. |
| `Code`      | `Contains` | `.Text`, `.SelectedItem.Value`                                                             | `webforms`   | ASP.NET WebForms input controls.            |
| `Type`      | `Contains` | `Grpc.Core.ServerCallContext`                                                              | `rpc`        | gRPC server call context.                   |

### Always-on sinks

| Kind     | Match      | Pattern                                                                                                    | Category          | Purpose                                |
| -------- | ---------- | ---------------------------------------------------------------------------------------------------------- | ----------------- | -------------------------------------- |
| `Method` | `Contains` | `System.Diagnostics.Process.Start`                                                                         | `command`         | Process execution.                     |
| `Type`   | `Contains` | `System.Diagnostics.ProcessStartInfo`                                                                      | `command`         | Process execution configuration.       |
| `Method` | `Contains` | `System.IO.File.`, `System.IO.Directory.`, `System.IO.FileStream`, `System.IO.Path.Combine`                | `file`            | File and path operations.              |
| `Name`   | `Exact`    | `SaveAs`, `CopyTo`                                                                                         | `file`            | Upload/file copy helpers.              |
| `Code`   | `Contains` | `SaveAs(`, `CopyTo(`, `Server.MapPath`                                                                     | `file`            | Legacy file operation fallback.        |
| `Method` | `Contains` | `System.Net.Http.HttpClient.`                                                                              | `network`         | Outbound HTTP request.                 |
| `Method` | `Contains` | `Response.Redirect`                                                                                        | `redirect`        | HTTP redirect.                         |
| `Name`   | `Exact`    | `Redirect`                                                                                                 | `redirect`        | HTTP redirect helper.                  |
| `Method` | `Contains` | `GetGrain`                                                                                                 | `rpc`             | Orleans grain dispatch.                |
| `Name`   | `Exact`    | `GetGrain`                                                                                                 | `rpc`             | Orleans grain dispatch by simple name. |
| `Method` | `Contains` | `System.Data.SqlClient.SqlCommand`, `Microsoft.Data.SqlClient.SqlCommand`, `MySqlCommand`, `SqliteCommand` | `sql`             | SQL command construction/execution.    |
| `Name`   | `Exact`    | `ExecuteNonQuery`, `ExecuteReader`                                                                         | `sql`             | SQL command execution.                 |
| `Method` | `Contains` | `ExecuteSqlRaw`, `FromSqlRaw`                                                                              | `sql`             | Entity Framework raw SQL.              |
| `Method` | `Contains` | `System.Reflection.Assembly.Load`, `System.Type.GetType`                                                   | `reflection`      | Dynamic loading/type lookup.           |
| `Method` | `Contains` | `BinaryFormatter.Deserialize`                                                                              | `deserialization` | BinaryFormatter deserialization.       |
| `Name`   | `Exact`    | `Deserialize`                                                                                              | `deserialization` | Generic object deserialization.        |

### Always-on passthroughs

These calls preserve taint through common string transformations:

| Kind     | Match      | Pattern                | Category |
| -------- | ---------- | ---------------------- | -------- |
| `Method` | `Contains` | `System.String.Concat` | `string` |
| `Method` | `Contains` | `System.String.Format` | `string` |
| `Method` | `Contains` | `ToString`             | `string` |
| `Method` | `Contains` | `Trim`                 | `string` |
| `Method` | `Contains` | `Replace`              | `string` |

### Always-on sanitizers and validators

| Kind     | Match      | Pattern                                        | Category        | Purpose                          |
| -------- | ---------- | ---------------------------------------------- | --------------- | -------------------------------- |
| `Method` | `Contains` | `System.Text.Encodings.Web.HtmlEncoder.Encode` | `html-encoding` | HTML encoding.                   |
| `Method` | `Contains` | `System.Net.WebUtility.HtmlEncode`             | `html-encoding` | HTML encoding.                   |
| `Method` | `Contains` | `System.Uri.EscapeDataString`                  | `url-encoding`  | URL component encoding.          |
| `Method` | `Contains` | `System.Text.RegularExpressions.Regex.IsMatch` | `validation`    | Regex validator used as a guard. |
| `Name`   | `Exact`    | `IsMatch`                                      | `validation`    | Validator method by simple name. |
| `Name`   | `Exact`    | `TryParse`                                     | `validation`    | Parse validator.                 |

Validator/sanitizer matches can stop taint for the matched expression. Validator guards can suppress taint on validated branches and keep taint on unvalidated branches for common positive, negated, `&&`, and `||` conditions.

## Optional packs

### `aspnet`

Adds ASP.NET MVC binding and redirect patterns.

| Target | Kind        | Match      | Pattern                                                 | Category   | Purpose                    |
| ------ | ----------- | ---------- | ------------------------------------------------------- | ---------- | -------------------------- |
| Source | `Type`      | `Contains` | `Microsoft.AspNetCore.Mvc.IActionResult`                | `http`     | MVC action result context. |
| Source | `Attribute` | `Contains` | `FromBody`                                              | `http`     | Model-bound body input.    |
| Source | `Attribute` | `Contains` | `FromQuery`                                             | `http`     | Query input.               |
| Source | `Attribute` | `Contains` | `FromForm`                                              | `http`     | Form input.                |
| Source | `Attribute` | `Contains` | `FromRoute`                                             | `http`     | Route input.               |
| Sink   | `Method`    | `Contains` | `Microsoft.AspNetCore.Mvc.ControllerBase.Redirect`      | `redirect` | ASP.NET redirect.          |
| Sink   | `Method`    | `Contains` | `Microsoft.AspNetCore.Mvc.ControllerBase.LocalRedirect` | `redirect` | ASP.NET local redirect.    |

### `data`

Adds data-access sinks and SQL parameterization sanitizers.

| Target    | Kind     | Match      | Pattern                    | Category               | Purpose                    |
| --------- | -------- | ---------- | -------------------------- | ---------------------- | -------------------------- |
| Sink      | `Method` | `Contains` | `Dapper.SqlMapper.Query`   | `sql`                  | Dapper raw SQL query.      |
| Sink      | `Method` | `Contains` | `Dapper.SqlMapper.Execute` | `sql`                  | Dapper raw SQL execution.  |
| Sink      | `Method` | `Contains` | `Npgsql.NpgsqlCommand`     | `sql`                  | PostgreSQL command.        |
| Sanitizer | `Name`   | `Exact`    | `AddWithValue`             | `sql-parameterization` | Parameterized SQL binding. |
| Sanitizer | `Name`   | `Exact`    | `Add`                      | `sql-parameterization` | Parameterized SQL binding. |

### `filesystem`

Adds filesystem/path sanitizers.

| Target    | Kind     | Match      | Pattern                      | Category             | Purpose                                       |
| --------- | -------- | ---------- | ---------------------------- | -------------------- | --------------------------------------------- |
| Sanitizer | `Method` | `Contains` | `System.IO.Path.GetFileName` | `path-validation`    | Limits path traversal to filename extraction. |
| Sanitizer | `Method` | `Contains` | `System.IO.Path.GetFullPath` | `path-normalization` | Path normalization.                           |

### `serialization`

Adds deserialization sinks.

| Target | Kind     | Match      | Pattern                                              | Category          | Purpose               |
| ------ | -------- | ---------- | ---------------------------------------------------- | ----------------- | --------------------- |
| Sink   | `Method` | `Contains` | `Newtonsoft.Json.JsonConvert.DeserializeObject`      | `deserialization` | JSON deserialization. |
| Sink   | `Method` | `Contains` | `System.Text.Json.JsonSerializer.Deserialize`        | `deserialization` | JSON deserialization. |
| Sink   | `Method` | `Contains` | `YamlDotNet.Serialization.IDeserializer.Deserialize` | `deserialization` | YAML deserialization. |

### `cloud`

Adds serverless trigger sources.

| Target | Kind        | Match      | Pattern             | Category     | Purpose                    |
| ------ | ----------- | ---------- | ------------------- | ------------ | -------------------------- |
| Source | `Attribute` | `Contains` | `QueueTrigger`      | `serverless` | Azure Queue trigger.       |
| Source | `Attribute` | `Contains` | `ServiceBusTrigger` | `serverless` | Azure Service Bus trigger. |
| Source | `Attribute` | `Contains` | `KafkaTrigger`      | `serverless` | Kafka trigger.             |

### `rpc`

Adds namespace-based RPC request sources.

| Target | Kind        | Match    | Pattern   | Category | Purpose                |
| ------ | ----------- | -------- | --------- | -------- | ---------------------- |
| Source | `Namespace` | `Prefix` | `Orleans` | `rpc`    | Orleans grain request. |
| Source | `Namespace` | `Prefix` | `Grpc`    | `rpc`    | gRPC request.          |

### `auth`

Adds authentication and token-generation sinks.

| Target | Kind   | Match   | Pattern                           | Category | Purpose                          |
| ------ | ------ | ------- | --------------------------------- | -------- | -------------------------------- |
| Sink   | `Name` | `Exact` | `CreateToken`                     | `auth`   | Token creation.                  |
| Sink   | `Name` | `Exact` | `SignInAsync`                     | `auth`   | Authentication sign-in.          |
| Sink   | `Name` | `Exact` | `GeneratePasswordResetTokenAsync` | `auth`   | Password reset token generation. |

### `crypto`

Adds source-to-crypto data-flow patterns. For a deeper crypto inventory, use the separate `crypto` command; this pack is specifically for data-flow slicing.

| Target    | Kind     | Match      | Pattern                                              | Category          | Taint kinds                        | Purpose                                 |
| --------- | -------- | ---------- | ---------------------------------------------------- | ----------------- | ---------------------------------- | --------------------------------------- |
| Source    | `Code`   | `Contains` | `-----BEGIN`                                         | `crypto-material` | `secret`, `crypto-key`             | PEM-encoded crypto material.            |
| Source    | `Name`   | `Contains` | `key`                                                | `crypto-material` | `secret`, `crypto-key`             | Key-like values.                        |
| Source    | `Name`   | `Contains` | `secret`                                             | `secret`          | `secret`                           | Secret-like values.                     |
| Sink      | `Method` | `Contains` | `System.Security.Cryptography`                       | `crypto`          | `crypto-key`, `secret`             | Cryptographic API use.                  |
| Sink      | `Method` | `Contains` | `Microsoft.IdentityModel.Tokens`                     | `jwt`             | `jwt`, `secret`                    | JWT signing/validation API.             |
| Sink      | `Method` | `Contains` | `X509Certificate2`                                   | `certificate`     | `certificate`, `secret`            | Certificate loading.                    |
| Sink      | `Code`   | `Contains` | `ServerCertificateCustomValidationCallback`          | `tls`             | `certificate`                      | TLS certificate validation callback.    |
| Sanitizer | `Method` | `Contains` | `System.Security.Cryptography.RandomNumberGenerator` | `secure-random`   | removes `insecure-random` metadata | Cryptographically secure random source. |

## Choosing packs

Use `all` for broad security triage. Narrow packs when you want fewer categories or faster exploratory runs:

```bash
# Web/data review
dotnet run --project ./Dosai/Dosai.csproj -- dataflows \
  --path ./src \
  --pattern-packs aspnet,data,filesystem \
  --o /tmp/web-dataflows.json

# Message/RPC review
dotnet run --project ./Dosai/Dosai.csproj -- dataflows \
  --path ./src \
  --pattern-packs cloud,rpc,serialization \
  --o /tmp/message-dataflows.json

# Crypto-sensitive data-flow review
dotnet run --project ./Dosai/Dosai.csproj -- dataflows \
  --path ./src \
  --pattern-packs crypto \
  --o /tmp/crypto-dataflows.json
```

Run with `--print-sources-sinks` to see which built-in and custom patterns matched in a project:

```bash
dotnet run --project ./Dosai/Dosai.csproj -- dataflows \
  --path ./src \
  --pattern-packs all \
  --print-sources-sinks \
  --o /tmp/dosai-dataflows.json
```

## New packs (schema 4.0.0)

| Pack        | Sources                                                                                    | Sinks                                                                                                                                                                                                              |
| ----------- | ------------------------------------------------------------------------------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `grpc`      | `Grpc.Core.ServerCallContext` (rpc)                                                        | `GrpcChannel.ForAddress`, `Grpc.Net.Client.GrpcChannel` (network)                                                                                                                                                  |
| `messaging` | `ConsumeContext` (rpc)                                                                     | `IPublishEndpoint.Publish`, `ISendEndpoint.Send`, `DaprClient.InvokeMethodAsync` (messaging)                                                                                                                       |
| `ai`        | LLM model output (`ChatResponse`, `GetResponseAsync`, model output is attacker-influenced) | `IChatClient.GetResponseAsync`, `Kernel.InvokePromptAsync`, `ChatMessage` construction (exact type match, `ChatMessageDto` and other user DTOs are not prompts), `ChatClient.CompleteChatAsync` (prompt; CWE-1427) |
| `mcp`       | `[McpServerTool]`/`[McpServerPrompt]` parameters (mcp)                                     | `McpClientFactory.CreateAsync` (mcp-egress)                                                                                                                                                                        |

The `ai` pack is the prompt-injection rule set: untrusted input flowing into a chat message or
prompt invocation yields `PromptInjectionCandidate` weaknesses (CWE-1427); MCP tool arguments
yield `McpToolInjectionCandidate`. See [Framework semantics](./frameworks.md) for the provider
side (taint seeding of framework entry points).

## New packs (schema 4.0.1)

Six additional weakness-class packs ship enabled by default. Slices from these packs carry
severity (`Severity` on `DataFlowSlice` and `WeaknessCandidate`), and every mapped category
produces a CWE-stamped `WeaknessCandidate` kind (see [security analysis](./security-analysis.md)).

| Pack        | Sinks (category → weakness, CWE)                                                                                                                                                                                                                                                                                           | Paired mitigations                                                                                                                                                                                                                                                                                                                    |
| ----------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `xss`       | `IHtmlHelper.Raw`/`HtmlHelper.Raw`, legacy `HttpResponse.Write`/`WriteLiteral`, Blazor `MarkupString` construction, `.cshtml` `Html.Raw(`/`.InnerHtml =` code fallbacks (xss → XssCandidate, CWE-79)                                                                                                                       | `HtmlEncoder.Encode` / `WebUtility.HtmlEncode` (always-on sanitizers); suppressed flows appear in `SanitizedFlows`                                                                                                                                                                                                                    |
| `xxe`       | `XmlDocument.Load*`, `XmlTextReader`, `XmlReader.Create`, `XDocument.Load`, `XPathDocument` (xxe → XxeCandidate, CWE-611)                                                                                                                                                                                                  | Hardening markers, inline or prepared in an earlier statement: `XmlResolver = null`, `DtdProcessing.Prohibit`/`Ignore`, `XmlSecureResolver`. Source mode only — IL analysis sees the parse call but not the hardening, so IL-mode `xxe` slices are reported at `Low` confidence (severity demoted one rank) and say so in the summary |
| `injection` | LDAP: `DirectoryEntry`, `DirectorySearcher`, `SearchRequest`, `Novell.Directory.Ldap` (CWE-90); XPath: `SelectNodes`/`SelectSingleNode`, `XPathNavigator.*`, `XPathExpression.Compile` (CWE-643); NoSQL: `BsonDocument.Parse`, `FilterDefinitionBuilder.Where`, `RunCommand`, Redis `ScriptEvaluate`/`LuaScript` (CWE-943) | —                                                                                                                                                                                                                                                                                                                                     |
| `log`       | `ILogger`/`Serilog`/`NLog`/`log4net`/`Console` message bodies (log → CWE-117, Low severity); `IHeaderDictionary.Append/Add`, `AppendHeader` response headers (header → CWE-113)                                                                                                                                            | `UrlEncoder.Encode`/`WebUtility.UrlEncode`, newline-strip `Replace` calls (crlf-strip)                                                                                                                                                                                                                                                |
| `redos`     | `new Regex(tainted)` (Medium), `Regex.IsMatch`/`Regex.Match` with a tainted pattern (Low) (CWE-1333); catastrophically-backtracking `new Regex("literal")` patterns are additionally flagged statically in `Diagnostics`                                                                                                   | —                                                                                                                                                                                                                                                                                                                                     |
| `template`  | Fluid, Scriban, Handlebars.NET, RazorEngine, DotLiquid parse/render of tainted template source (CWE-1336)                                                                                                                                                                                                                  | —                                                                                                                                                                                                                                                                                                                                     |

### Hygiene fixes in 4.0.1

- The `data` pack's bare `Add` sanitizer is gone: only provider parameter collections
  (`SqlParameterCollection.Add`, `NpgsqlParameterCollection.Add`, `NpgsqlParameter`/`SqlParameter`
  construction) sanitize. `List<T>.Add` no longer masks SQL flows, and storing a tainted value
  into a collection now taints the collection so read-backs still reach sinks.
- The crypto `key`/`secret` sources require compound identifiers: bare `key`/`keys` names (every
  `KeyValuePair` iteration, every cache dictionary) mint nothing, and `Monkey`/`secretaryNote`
  never matched. `apiKey`, `client_secret`, `session_key`, and `hmacKey` still do.
- The IL-mode short-pattern noise filter (bare `Name`/`Contains` patterns under four characters)
  now applies in source mode too, so both modes agree.

## Severity (schema 4.0.1)

Every slice and weakness candidate carries `Severity` (`info|low|medium|high`), defaulting by sink
category — injection primitives (command/sql/file/xss/xxe/ldap/xpath/nosql/template/crypto family)
are `high`; exposure classes (redirect/header/prompt/rpc) are `medium`; log/redos are `low`.
Pattern authors can override with the pattern's optional `Severity` field. A match from a
Low-confidence pattern is demoted one severity rank, so heuristic fallbacks (Razor `Html.Raw(`
code text, logger extension heuristics) never trip a `NewHighSeveritySlices` CI gate on their own.
The `diff` command is severity-aware and reports a `RiskDelta` including `NewHighSeveritySlices`.
