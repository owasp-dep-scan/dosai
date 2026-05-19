#!/usr/bin/env python3
"""Data-flow performance and precision harness for Dosai.

The harness builds Dosai, generates small source-to-sink fixtures, runs the
`dataflows` command against each fixture, checks graph invariants and expected
slice categories, and can optionally run against Dosai's own source tree.
"""

from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import Iterable


@dataclass(frozen=True)
class Fixture:
    name: str
    files: dict[str, str]
    min_slices: int = 0
    required_sink_categories: tuple[str, ...] = ()
    forbidden_sink_categories: tuple[str, ...] = ()
    extra_args: tuple[str, ...] = ()


@dataclass
class RunResult:
    name: str
    elapsed: float
    stats: dict[str, int]
    output: Path
    issues: list[str] = field(default_factory=list)


FIXTURES: tuple[Fixture, ...] = (
    Fixture(
        name="cli_process",
        files={
            "Program.cs": """
using System.Diagnostics;

class Program
{
    static void Main(string[] args)
    {
        var command = args[0];
        Process.Start(command);
    }
}
"""
        },
        min_slices=1,
        required_sink_categories=("command",),
    ),
    Fixture(
        name="interprocedural_process",
        files={
            "Program.cs": """
using System.Diagnostics;

class Program
{
    static void Main(string[] args)
    {
        Run(args[0]);
    }

    static void Run(string command)
    {
        Process.Start(command);
    }
}
"""
        },
        min_slices=1,
        required_sink_categories=("command",),
    ),
    Fixture(
        name="sanitized_process",
        files={
            "Program.cs": """
using System.Diagnostics;
using System.Text.RegularExpressions;

class Program
{
    static void Main(string[] args)
    {
        var guarded = args[0];
        if (Regex.IsMatch(guarded, "^[a-z]+$"))
        {
            Process.Start(guarded);
        }
    }
}
"""
        },
        min_slices=0,
        forbidden_sink_categories=("command",),
    ),
    Fixture(
        name="file_read",
        files={
            "Program.cs": """
using System.IO;

class Program
{
    static void Main(string[] args)
    {
        var path = args[0];
        File.ReadAllText(path);
    }
}
"""
        },
        min_slices=1,
        required_sink_categories=("file",),
    ),
)


def run(command: list[str], cwd: Path, timeout: int) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        command,
        cwd=cwd,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        timeout=timeout,
        check=False,
    )


def build(root: Path, configuration: str, timeout: int) -> Path:
    build_cmd = ["dotnet", "build", "--configuration", configuration, "Dosai.sln"]
    result = run(build_cmd, root, timeout)
    if result.returncode != 0:
        print(result.stdout, file=sys.stderr)
        print(result.stderr, file=sys.stderr)
        raise SystemExit(f"Build failed with exit code {result.returncode}")

    return find_dosai_dll(root, configuration)


def find_dosai_dll(root: Path, configuration: str) -> Path:
    candidates = sorted((root / "Dosai" / "bin" / configuration / "net10.0").glob("*/Dosai.dll"))
    candidates += sorted((root / "Dosai" / "bin" / configuration / "net10.0").glob("Dosai.dll"))
    if not candidates:
        raise SystemExit("Could not locate built Dosai.dll under Dosai/bin; rerun without --skip-build")
    return candidates[0]


def write_fixture(base: Path, fixture: Fixture) -> Path:
    fixture_dir = base / fixture.name
    fixture_dir.mkdir(parents=True, exist_ok=True)
    for relative, content in fixture.files.items():
        path = fixture_dir / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content.strip() + "\n", encoding="utf-8")
    return fixture_dir


def validate_result(name: str, output: Path, fixture: Fixture | None = None) -> tuple[dict[str, int], list[str]]:
    data = json.loads(output.read_text(encoding="utf-8"))
    stats = data.get("Statistics", {})
    issues: list[str] = []

    node_ids = {node["Id"] for node in data.get("Nodes", [])}
    for edge in data.get("Edges", []):
        if edge.get("SourceId") not in node_ids or edge.get("TargetId") not in node_ids:
            issues.append(f"{name}: invalid edge endpoint {edge.get('Id')}")

    slices = data.get("Slices", [])
    if fixture is not None:
        if stats.get("SliceCount", 0) < fixture.min_slices:
            issues.append(f"{name}: expected at least {fixture.min_slices} slices, got {stats.get('SliceCount', 0)}")
        sink_categories = {slice_.get("SinkCategory") for slice_ in slices}
        for category in fixture.required_sink_categories:
            if category not in sink_categories:
                issues.append(f"{name}: missing required sink category {category!r}")
        for category in fixture.forbidden_sink_categories:
            if category in sink_categories:
                issues.append(f"{name}: forbidden sink category {category!r} was reported")

    return stats, issues


def run_dataflows(dll: Path, target: Path, output: Path, root: Path, timeout: int, extra_args: Iterable[str] = ()) -> float:
    command = ["dotnet", str(dll), "dataflows", "--path", str(target), "--o", str(output), *extra_args]
    start = time.perf_counter()
    result = run(command, root, timeout)
    elapsed = time.perf_counter() - start
    if result.returncode != 0:
        print(result.stdout, file=sys.stderr)
        print(result.stderr, file=sys.stderr)
        raise SystemExit(f"dataflows failed for {target} with exit code {result.returncode}")
    return elapsed


def run_fixture(dll: Path, root: Path, workspace: Path, fixture: Fixture, timeout: int) -> RunResult:
    fixture_dir = write_fixture(workspace, fixture)
    output = workspace / f"{fixture.name}.json"
    elapsed = run_dataflows(dll, fixture_dir, output, root, timeout, fixture.extra_args)
    stats, issues = validate_result(fixture.name, output, fixture)
    return RunResult(fixture.name, elapsed, stats, output, issues)


def copy_dataflow_self(root: Path, workspace: Path) -> Path:
    target = workspace / "dosai_dataflow_cs"
    target.mkdir(parents=True, exist_ok=True)
    shutil.copy2(root / "Dosai" / "DataFlow.cs", target / "DataFlow.cs")
    return target


def print_result(result: RunResult) -> None:
    stats = result.stats
    status = "FAIL" if result.issues else "OK"
    print(
        f"{status:4} {result.name:24} {result.elapsed:8.2f}s "
        f"files={stats.get('FilesAnalyzed', 0):3} nodes={stats.get('NodeCount', 0):5} "
        f"edges={stats.get('EdgeCount', 0):5} slices={stats.get('SliceCount', 0):5}"
    )
    for issue in result.issues:
        print(f"     - {issue}")


def main() -> int:
    parser = argparse.ArgumentParser(description="Run Dosai dataflows performance and precision fixtures.")
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1], help="Repository root")
    parser.add_argument("--configuration", default="Debug", help="Build configuration")
    parser.add_argument("--timeout", type=int, default=120, help="Timeout per dataflows run in seconds")
    parser.add_argument("--build-timeout", type=int, default=180, help="Build timeout in seconds")
    parser.add_argument("--skip-build", action="store_true", help="Use an existing build")
    parser.add_argument("--include-dosai", action="store_true", help="Also analyze the full Dosai source tree")
    parser.add_argument("--keep", action="store_true", help="Keep generated fixture/output directory")
    args = parser.parse_args()

    root = args.root.resolve()
    if not (root / "Dosai.sln").exists():
        raise SystemExit(f"Repository root does not contain Dosai.sln: {root}")

    dll = find_dosai_dll(root, args.configuration) if args.skip_build else build(root, args.configuration, args.build_timeout)
    workspace = Path(tempfile.mkdtemp(prefix="dosai-dataflow-harness-"))
    print(f"Dosai DLL: {dll}")
    print(f"Workspace: {workspace}")

    all_results: list[RunResult] = []
    try:
        for fixture in FIXTURES:
            result = run_fixture(dll, root, workspace, fixture, args.timeout)
            all_results.append(result)
            print_result(result)

        dataflow_self = copy_dataflow_self(root, workspace)
        output = workspace / "dosai_dataflow_cs.json"
        elapsed = run_dataflows(dll, dataflow_self, output, root, args.timeout)
        stats, issues = validate_result("dosai_dataflow_cs", output)
        self_result = RunResult("dosai_dataflow_cs", elapsed, stats, output, issues)
        all_results.append(self_result)
        print_result(self_result)

        if args.include_dosai:
            output = workspace / "dosai_full.json"
            elapsed = run_dataflows(dll, root / "Dosai", output, root, args.timeout)
            stats, issues = validate_result("dosai_full", output)
            full_result = RunResult("dosai_full", elapsed, stats, output, issues)
            all_results.append(full_result)
            print_result(full_result)

        failed = [result for result in all_results if result.issues]
        return 1 if failed else 0
    finally:
        if args.keep:
            print(f"Kept workspace: {workspace}")
        else:
            shutil.rmtree(workspace, ignore_errors=True)


if __name__ == "__main__":
    raise SystemExit(main())
