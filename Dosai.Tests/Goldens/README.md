# Golden graph-export bytes (issue #75)

`graph-labels-fixture/` is a bare source fixture (no project file: the fallback reference set is
what committed fixtures use, and a `net8.0` csproj would not resolve on machines without an 8.0
reference pack). Its labels exercise every exporter escape path: quotes, apostrophe, `<>&`,
`|`, `[]()`, `{}`, `#`, `;`, real newlines and tabs (the invocation spans lines), non-ASCII,
emoji, and an empty string argument. The entry point is `Main(string[] args)` because the cli
source pattern binds an entry point's `args`.

`graph-exports/` holds the bytes the **main build at 7292446** (the last commit before the
issue-#75 TextWriter refactor) produced for that fixture:

```bash
# from a checkout of 7292446, built and published as <dosai>
for fmt in mermaid graphml gexf; do
  dosai dataflows --path Dosai.Tests/Goldens/graph-labels-fixture \
    --o /tmp/df.json --graph-format $fmt --graph-out graph-exports/dataflows.$fmt
  dosai methods --path Dosai.Tests/Goldens/graph-labels-fixture \
    --o /tmp/m.json --callgraph-format $fmt --callgraph-out graph-exports/callgraph.$fmt
done
dosai crypto --path Dosai.Tests/Goldens/graph-labels-fixture \
  --o /tmp/c.json --format cyclonedx && cp /tmp/c.json graph-exports/crypto-cyclonedx.json
```

The graph files contain no timestamps or uuids and are byte-stable run to run; the CycloneDX
golden's `serialNumber` and `metadata.timestamp` are per-run values and are masked in the test.
`GraphExporters_ProduceTheFrozenMainBytes` additionally carries a verbatim copy of the 7292446
exporter implementations (`../GraphExportReference.cs`) for shapes real analysis never emits
(a dangling edge endpoint, an empty node label).

If an exporter change is intentional, regenerate these files with a build of the new main (not
a branch) and say so in the PR.
