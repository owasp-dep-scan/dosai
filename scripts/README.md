# Dosai scripts

## `dataflow_perf_harness.py`

Runs a focused performance and precision harness for `dataflows`.

The harness:

- builds Dosai, unless `--skip-build` is supplied;
- generates small C# fixtures for CLI-to-command, interprocedural command, sanitized command, and file-read flows;
- checks every graph edge references existing nodes;
- checks expected sink categories are present or absent;
- analyzes `Dosai/DataFlow.cs` as a self-analysis regression case;
- optionally analyzes the full `Dosai` source tree with `--include-dosai`.

Examples:

```bash
python3 scripts/dataflow_perf_harness.py --skip-build
```

```bash
python3 scripts/dataflow_perf_harness.py --skip-build --include-dosai --timeout 120
```

Use `--keep` to preserve generated fixtures and JSON outputs for diffing.
