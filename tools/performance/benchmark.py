#!/usr/bin/env python3
"""Process-per-scenario jq CLI benchmark for the pinned jq 1.8.2 fixtures."""

from __future__ import annotations

import argparse
import csv
import dataclasses
import datetime as dt
import hashlib
import json
import math
import os
import pathlib
import platform
import random
import re
import shutil
import statistics
import subprocess
import sys
import tempfile
import time
from collections import defaultdict
from typing import Sequence

import build_attestation as provenance
import report_output
import paired_baseline


PINNED_COMMIT = "34f7186b86743a083a589741b6cea95293524108"
PINNED_ORACLE_SHA256 = (
    "b1c22172dd303f3be49e935aa56aa48a8b7a46e0bc838b4997d3bb451495870f"
)
P95_MINIMUM_SAMPLES = 20
CONTROLLED_ENVIRONMENT = {
    "PAGER": "less",
    "TZ": "UTC",
    "LC_ALL": "C",
    "LANG": "C",
    "NO_COLOR": "1",
    "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
    "DOTNET_NOLOGO": "1",
    "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "1",
}
RUNTIME_ROOT_VARIABLES = {
    "DOTNET_ROOT",
    "DOTNET_ROOT_X64",
    "DOTNET_ROOT_X86",
    "DOTNET_ROOT_ARM64",
    "DOTNET_ROOT_ARM",
}
PERFORMANCE_ENVIRONMENT_PREFIXES = (
    "COMPlus_",
    "CORECLR_",
    "COREHOST_",
    "DOTNET_",
    "JQ_",
    "LD_",
    "MALLOC_",
)
PERFORMANCE_ENVIRONMENT_NAMES = {
    "GLIBC_TUNABLES",
    "JQ_COLORS",
    "JQ_LIBRARY_PATH",
    "LD_LIBRARY_PATH",
    "LD_PRELOAD",
    "MALLOC_ARENA_MAX",
    "MALLOC_MMAP_THRESHOLD_",
    "MALLOC_PERTURB_",
    "MALLOC_TOP_PAD_",
    "MALLOC_TRIM_THRESHOLD_",
}
FIXTURE_COUNTS = {
    "jq.test": 550,
    "man.test": 231,
    "onig.test": 47,
    "manonig.test": 19,
    "base64.test": 10,
    "uri.test": 20,
    "optional.test": 2,
}
FIXTURE_ORDER = tuple(FIXTURE_COUNTS)
CORRECTNESS_ONLY_FORBIDDEN_OPTIONS = (
    "--fixture",
    "--limit",
    "--output-directory",
    "--repetitions",
    "--warmups",
    "--startup-repetitions",
    "--seed",
)


class BenchmarkError(RuntimeError):
    """An actionable benchmark configuration or correctness failure."""


@dataclasses.dataclass(frozen=True)
class FixtureCase:
    fixture: str
    fixture_index: int
    source_line: int
    program: str
    input_text: str | None
    expected_lines: tuple[str, ...]
    must_fail: bool
    check_failure_message: bool

    @property
    def scenario_id(self) -> str:
        return f"{self.fixture}:{self.fixture_index:03d}"


@dataclasses.dataclass(frozen=True)
class Implementation:
    name: str
    command: tuple[str, ...]
    artifact_paths: tuple[pathlib.Path, ...]


@dataclasses.dataclass(frozen=True)
class ProcessResult:
    exit_code: int
    stdout: bytes
    stderr: bytes
    elapsed_ns: int

    @property
    def signature(self) -> tuple[int, bytes, bytes]:
        return self.exit_code, self.stdout, self.stderr


def repository_root() -> pathlib.Path:
    return pathlib.Path(__file__).resolve().parents[2]


def sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def sha256_file(path: pathlib.Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def read_optional_text(path: pathlib.Path) -> str | None:
    try:
        return path.read_text(encoding="utf-8").strip() or None
    except OSError:
        return None


def performance_environment_variable(name: str) -> bool:
    return name in PERFORMANCE_ENVIRONMENT_NAMES or name.startswith(
        PERFORMANCE_ENVIRONMENT_PREFIXES
    )


def source_identity(root: pathlib.Path) -> dict[str, object]:
    source_roots = (
        root / "src/DotNetJq",
        root / "src/DotNetJq.Cli",
        root / "src/DotNetJq.GlibcCompat",
    )
    paths = [
        path
        for path in (
            root / "Directory.Build.props",
            root / "global.json",
            *(
                candidate
                for source_root in source_roots
                if source_root.is_dir()
                for candidate in source_root.rglob("*")
                if candidate.is_file()
                and "bin" not in candidate.relative_to(source_root).parts
                and "obj" not in candidate.relative_to(source_root).parts
            ),
        )
        if path.is_file()
    ]
    entries = [
        {
            "path": path.relative_to(root).as_posix(),
            "bytes": path.stat().st_size,
            "sha256": sha256_file(path),
        }
        for path in sorted(set(paths))
    ]
    try:
        head = subprocess.run(
            ["git", "-C", str(root), "rev-parse", "HEAD"],
            check=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
        ).stdout.strip()
        status = subprocess.run(
            ["git", "-C", str(root), "status", "--porcelain=v1", "--untracked-files=all"],
            check=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
        ).stdout
    except (OSError, subprocess.CalledProcessError) as error:
        raise BenchmarkError(f"cannot inspect source repository: {root}") from error
    return {
        "git_head": head,
        "git_dirty": bool(status),
        "git_status_entries": len(status.splitlines()),
        "git_status_sha256": sha256_bytes(status.encode("utf-8")),
        "compiled_input_files": len(entries),
        "compiled_input_manifest_sha256": sha256_bytes(
            json.dumps(entries, sort_keys=True, separators=(",", ":")).encode("utf-8")
        ),
    }


def dotnet_metadata(
    root: pathlib.Path,
    environment: dict[str, str],
    timeout_seconds: float,
) -> dict[str, object]:
    dotnet = shutil.which("dotnet", path=environment.get("PATH"))
    if dotnet is None:
        raise BenchmarkError("dotnet host is unavailable on PATH")
    dotnet_path = pathlib.Path(dotnet).resolve()

    def capture(*arguments: str) -> str:
        try:
            completed = subprocess.run(
                [str(dotnet_path), *arguments],
                stdin=subprocess.DEVNULL,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                cwd=root,
                env=environment,
                timeout=timeout_seconds,
                check=False,
                text=True,
            )
        except subprocess.TimeoutExpired as error:
            raise BenchmarkError(
                f"dotnet {' '.join(arguments)} exceeded {timeout_seconds:g}s"
            ) from error
        if completed.returncode != 0:
            raise BenchmarkError(
                f"dotnet {' '.join(arguments)} exited {completed.returncode}: "
                f"{completed.stderr.strip()}"
            )
        return completed.stdout.strip()

    information = capture("--info")
    return {
        "host_path": str(dotnet_path),
        "host_sha256": sha256_file(dotnet_path),
        "sdk_version": capture("--version"),
        "info": information,
        "info_sha256": sha256_bytes(information.encode("utf-8")),
        "global_json": {
            "path": str(root / "global.json"),
            "sha256": sha256_file(root / "global.json"),
        },
    }


def system_metadata() -> dict[str, object]:
    cpu_model = None
    cpuinfo = read_optional_text(pathlib.Path("/proc/cpuinfo"))
    if cpuinfo:
        match = re.search(r"(?m)^model name\s*:\s*(.+)$", cpuinfo)
        cpu_model = match.group(1) if match else None
    governors = sorted(
        {
            value
            for path in pathlib.Path("/sys/devices/system/cpu").glob(
                "cpu[0-9]*/cpufreq/scaling_governor"
            )
            if (value := read_optional_text(path)) is not None
        }
    )
    try:
        affinity = sorted(os.sched_getaffinity(0))
    except (AttributeError, OSError):
        affinity = None
    try:
        load_average = list(os.getloadavg())
    except OSError:
        load_average = None
    return {
        "platform": platform.platform(),
        "kernel": platform.release(),
        "machine": platform.machine(),
        "cpu_model": cpu_model,
        "logical_cpu_count": os.cpu_count(),
        "cpu_affinity": affinity,
        "cpu_governors": governors or None,
        "load_average_1m_5m_15m": load_average,
    }


def trim_lf(line: str) -> str:
    return line[:-1] if line.endswith("\n") else line


def skipped_fixture_line(line: str) -> bool:
    offset = 0
    while offset < len(line) and line[offset] in (" ", "\t"):
        offset += 1
    return offset == len(line) or line[offset] in ("#", "\n", "\0")


def parse_fixture(path: pathlib.Path) -> list[FixtureCase]:
    # newline="" preserves the fixture grammar's physical lines and its BOM test.
    with path.open("r", encoding="utf-8", newline="") as source:
        lines = source.readlines()

    cases: list[FixtureCase] = []
    cursor = 0
    must_fail = False
    check_message = False
    while cursor < len(lines):
        line = lines[cursor]
        cursor += 1
        if skipped_fixture_line(line):
            continue
        marker = trim_lf(line)
        if marker in ("%%FAIL", "%%FAIL IGNORE MSG"):
            must_fail = True
            check_message = marker == "%%FAIL"
            continue

        program_line = cursor
        program = marker
        if must_fail:
            expected: list[str] = []
            while cursor < len(lines):
                expected_line = lines[cursor]
                cursor += 1
                if skipped_fixture_line(expected_line):
                    break
                expected.append(trim_lf(expected_line))
            cases.append(
                FixtureCase(
                    path.name,
                    len(cases) + 1,
                    program_line,
                    program,
                    None,
                    tuple(expected),
                    True,
                    check_message,
                )
            )
            must_fail = False
            check_message = False
            continue

        if cursor >= len(lines):
            raise BenchmarkError(f"{path}:{program_line}: missing test input")
        input_text = trim_lf(lines[cursor])
        cursor += 1
        expected = []
        while cursor < len(lines):
            expected_line = lines[cursor]
            cursor += 1
            if skipped_fixture_line(expected_line):
                break
            expected.append(trim_lf(expected_line))
        cases.append(
            FixtureCase(
                path.name,
                len(cases) + 1,
                program_line,
                program,
                input_text,
                tuple(expected),
                False,
                False,
            )
        )

    if must_fail:
        raise BenchmarkError(f"{path}: dangling %%FAIL marker")
    return cases


def reject_duplicates(values: Sequence[str], label: str) -> None:
    duplicates = sorted({value for value in values if values.count(value) > 1})
    if duplicates:
        raise BenchmarkError(f"duplicate {label}: {', '.join(duplicates)}")


def manifest_identity(paths: Sequence[pathlib.Path], root: pathlib.Path) -> dict[str, object]:
    def recorded_path(path: pathlib.Path) -> str:
        try:
            return path.relative_to(root).as_posix()
        except ValueError:
            return str(path.resolve())

    entries = [
        {
            "path": recorded_path(path),
            "bytes": path.stat().st_size,
            "sha256": sha256_file(path),
        }
        for path in sorted(set(paths))
    ]
    return {
        "files": len(entries),
        "sha256": sha256_bytes(
            json.dumps(entries, sort_keys=True, separators=(",", ":")).encode("utf-8")
        ),
        "entries": entries,
    }


def upstream_identity(upstream: pathlib.Path) -> dict[str, object]:
    fixtures_directory = upstream / "tests"
    modules = fixtures_directory / "modules"
    fixture_paths = [fixtures_directory / name for name in FIXTURE_ORDER]
    missing = [path for path in fixture_paths if not path.is_file()]
    if missing:
        raise BenchmarkError(f"upstream fixture is unavailable: {missing[0]}")
    if not modules.is_dir():
        raise BenchmarkError(f"upstream module directory is unavailable: {modules}")
    module_paths = [path for path in modules.rglob("*") if path.is_file()]
    try:
        head = subprocess.run(
            ["git", "-C", str(upstream), "rev-parse", "HEAD"],
            check=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
        ).stdout.strip()
        status = subprocess.run(
            [
                "git",
                "-C",
                str(upstream),
                "status",
                "--porcelain=v1",
                "--untracked-files=all",
            ],
            check=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
        ).stdout
    except (OSError, subprocess.CalledProcessError) as error:
        raise BenchmarkError(f"cannot inspect upstream checkout: {upstream}") from error
    if head != PINNED_COMMIT:
        raise BenchmarkError(
            f"upstream jq HEAD is {head}; expected pinned jq-1.8.2 commit {PINNED_COMMIT}"
        )
    if status:
        details = "; ".join(status.splitlines()[:5])
        raise BenchmarkError(
            "upstream jq checkout is dirty; benchmark fixtures/modules must come from the "
            f"exact pinned tree: {details}"
        )
    return {
        "path": str(upstream),
        "git_head": head,
        "git_status_sha256": sha256_bytes(status.encode("utf-8")),
        "clean": True,
        "fixtures": manifest_identity(fixture_paths, upstream),
        "modules": manifest_identity(module_paths, upstream),
    }


def validate_implementation_identities(
    implementations: Sequence[Implementation],
) -> None:
    executable_paths = [item.artifact_paths[0].resolve() for item in implementations]
    if len(set(executable_paths)) != len(executable_paths):
        raise BenchmarkError("benchmark implementations resolve to the same executable path")
    executable_hashes = [sha256_file(path) for path in executable_paths]
    if len(set(executable_hashes)) != len(executable_hashes):
        raise BenchmarkError("benchmark implementations have duplicate executable identities")
    artifact_hashes = [
        str(implementation_metadata(item, "<identity>")["artifact_manifest_sha256"])
        for item in implementations
    ]
    if len(set(artifact_hashes)) != len(artifact_hashes):
        raise BenchmarkError("benchmark implementations have duplicate artifact identities")


def validate_build_configurations(configurations: dict[str, str]) -> None:
    expected = {
        "framework-dotnetjq": "net10.0; CoreCLR; linux-x64; jq-1.8.2 compatible",
        "aot-dotnetjq": "net10.0; NativeAOT; linux-x64; jq-1.8.2 compatible",
    }
    for name, value in expected.items():
        if configurations.get(name) != value:
            raise BenchmarkError(
                f"{name} --build-configuration is {configurations.get(name)!r}; "
                f"expected {value!r}"
            )
    if configurations.get("native-jq") in expected.values():
        raise BenchmarkError("native jq reports a managed deployment build configuration")


def publication_decision(
    arguments: argparse.Namespace,
    complete_run: bool,
    output_directory: pathlib.Path,
    default_output: pathlib.Path,
) -> bool:
    if arguments.publishable:
        publishable = True
    elif arguments.non_publishable:
        publishable = False
    else:
        publishable = complete_run and output_directory == default_output
    if publishable and not complete_run:
        raise BenchmarkError(
            "publishable fixture results require the unselected, unlimited 879-scenario run"
        )
    if not publishable and output_directory == default_output:
        raise BenchmarkError(
            "a smoke/--skip-build run is non-publishable and requires an explicit "
            "non-default --output-directory"
        )
    return publishable


def prepare_benchmark_report(
    arguments: argparse.Namespace,
    root: pathlib.Path,
    lease: report_output.ReportLease,
    requested_complete_run: bool,
) -> tuple[pathlib.Path | None, bool]:
    """Prepare a timing report, or leave every report leaf untouched for correctness-only."""
    if arguments.correctness_only:
        return None, False

    requested_output = pathlib.Path(arguments.output_directory)
    try:
        output_directory = report_output.resolve_report_directory(
            requested_output, root, "fixture"
        )
    except report_output.ReportOutputError as error:
        raise BenchmarkError(str(error)) from error
    default_output = (
        root / report_output.report_spec("fixture").default_relative_path
    ).resolve(strict=False)
    try:
        prepared_output = report_output.prepare_report_directory(
            requested_output, root, "fixture", lease
        )
    except report_output.ReportOutputError as error:
        raise BenchmarkError(str(error)) from error
    if prepared_output != output_directory:
        raise BenchmarkError("report output path changed while it was being prepared")
    return output_directory, publication_decision(
        arguments, requested_complete_run, output_directory, default_output
    )


def resolve_executable(value: str, label: str) -> str:
    candidate = pathlib.Path(value).expanduser()
    if not candidate.is_absolute():
        candidate = pathlib.Path.cwd() / candidate
    candidate = candidate.resolve()
    if not candidate.is_file():
        raise BenchmarkError(f"{label} executable does not exist: {candidate}")
    if not os.access(candidate, os.X_OK):
        raise BenchmarkError(f"{label} executable is not executable: {candidate}")
    return str(candidate)


def validate_pinned_oracle(path: pathlib.Path) -> str:
    actual = sha256_file(path)
    if actual != PINNED_ORACLE_SHA256:
        raise BenchmarkError(
            f"native jq SHA-256 is {actual}; expected pinned jq-1.8.2 oracle "
            f"{PINNED_ORACLE_SHA256}: {path}"
        )
    return actual


def rotated_implementations(
    implementations: Sequence[Implementation],
    stable_scenario_index: int,
    repetition_index: int,
) -> list[Implementation]:
    if not implementations:
        raise BenchmarkError("cannot rotate an empty implementation list")
    offset = (stable_scenario_index + repetition_index) % len(implementations)
    return [*implementations[offset:], *implementations[:offset]]


def framework_artifact_paths(executable: pathlib.Path) -> tuple[pathlib.Path, ...]:
    """Return the apphost plus the managed files that determine its behavior."""
    siblings = sorted(
        (
            *executable.parent.glob("*.dll"),
            *executable.parent.glob("*.deps.json"),
            *executable.parent.glob("*.runtimeconfig.json"),
        ),
        key=lambda path: path.name,
    )
    return (executable, *siblings)


def implementation_metadata(
    implementation: Implementation,
    version: str,
) -> dict[str, object]:
    executable = implementation.artifact_paths[0]
    artifact_contract = [
        {
            "name": path.name,
            "bytes": path.stat().st_size,
            "sha256": sha256_file(path),
        }
        for path in implementation.artifact_paths
    ]
    artifacts = [
        {**contract, "path": str(path), "mtime_ns": path.stat().st_mtime_ns}
        for contract, path in zip(artifact_contract, implementation.artifact_paths, strict=True)
    ]
    return {
        "name": implementation.name,
        "command": list(implementation.command),
        "version": version,
        "executable_path": str(executable),
        "executable_size_bytes": executable.stat().st_size,
        "executable_sha256": sha256_file(executable),
        "executable_mtime_ns": executable.stat().st_mtime_ns,
        "artifacts": artifacts,
        "runtime_payload_size_bytes": sum(item["bytes"] for item in artifact_contract),
        "artifact_manifest_sha256": sha256_bytes(
            json.dumps(artifact_contract, sort_keys=True, separators=(",", ":")).encode("utf-8")
        ),
    }


def stable_environment(home: pathlib.Path) -> dict[str, str]:
    environment = os.environ.copy()
    environment.pop(report_output.LEASE_ID_ENVIRONMENT, None)
    environment.pop(report_output.LEASE_FD_ENVIRONMENT, None)
    for name in tuple(environment):
        if performance_environment_variable(name) and name not in RUNTIME_ROOT_VARIABLES:
            environment.pop(name, None)
    environment.update(CONTROLLED_ENVIRONMENT)
    environment["HOME"] = str(home)
    environment["DOTNET_CLI_HOME"] = str(home)
    return environment


def environment_contract() -> dict[str, object]:
    runtime_roots = {
        name: os.environ[name]
        for name in sorted(RUNTIME_ROOT_VARIABLES)
        if name in os.environ
    }
    removed = sorted(
        name
        for name in os.environ
        if performance_environment_variable(name) and name not in RUNTIME_ROOT_VARIABLES
    )
    return {
        "set": {
            **CONTROLLED_ENVIRONMENT,
            "HOME": "<temporary-home>",
            "DOTNET_CLI_HOME": "<temporary-home>",
        },
        "preserved_runtime_roots": runtime_roots,
        "removed_parent_performance_variables": removed,
        "inherited_path": os.environ.get("PATH"),
        "inherited_other_variables": (
            "parent variables outside the documented performance-affecting prefixes"
        ),
    }


def execute(
    implementation: Implementation,
    arguments: Sequence[str],
    stdin: bytes,
    cwd: pathlib.Path,
    environment: dict[str, str],
    timeout_seconds: float | None,
    capture_output: bool = True,
) -> ProcessResult:
    started = time.perf_counter_ns()
    try:
        completed = subprocess.run(
            [*implementation.command, *arguments],
            input=stdin,
            stdout=subprocess.PIPE if capture_output else subprocess.DEVNULL,
            stderr=subprocess.PIPE if capture_output else subprocess.DEVNULL,
            cwd=cwd,
            env=environment,
            timeout=timeout_seconds,
            check=False,
        )
    except subprocess.TimeoutExpired as error:
        assert timeout_seconds is not None
        raise BenchmarkError(
            f"{implementation.name} exceeded {timeout_seconds:g}s running {arguments[-1]!r}"
        ) from error
    elapsed = time.perf_counter_ns() - started
    return ProcessResult(
        completed.returncode,
        completed.stdout if capture_output else b"",
        completed.stderr if capture_output else b"",
        elapsed,
    )


def case_arguments(case: FixtureCase, modules: pathlib.Path) -> tuple[list[str], bytes]:
    arguments = ["-L", str(modules), "-c", "--", case.program]
    stdin = b"" if case.input_text is None else (case.input_text + "\n").encode("utf-8")
    return arguments, stdin


def text_diff(expected: bytes, actual: bytes) -> str:
    expected_text = expected.decode("utf-8", errors="backslashreplace")
    actual_text = actual.decode("utf-8", errors="backslashreplace")
    if len(expected_text) > 400:
        expected_text = expected_text[:400] + "..."
    if len(actual_text) > 400:
        actual_text = actual_text[:400] + "..."
    return f"expected={expected_text!r}; actual={actual_text!r}"


def assert_matches_reference(
    implementation: Implementation,
    case: FixtureCase,
    reference: ProcessResult,
    actual: ProcessResult,
    phase: str,
) -> None:
    if actual.signature == reference.signature:
        return
    details: list[str] = []
    if actual.exit_code != reference.exit_code:
        details.append(f"exit expected={reference.exit_code}, actual={actual.exit_code}")
    if actual.stdout != reference.stdout:
        details.append("stdout " + text_diff(reference.stdout, actual.stdout))
    if actual.stderr != reference.stderr:
        details.append("stderr " + text_diff(reference.stderr, actual.stderr))
    raise BenchmarkError(
        f"{phase} correctness mismatch for {implementation.name} at "
        f"{case.scenario_id} (source line {case.source_line}): " + "; ".join(details)
    )


def validate_native_fixture(
    native: Implementation,
    fixture_path: pathlib.Path,
    expected_count: int,
    modules: pathlib.Path,
    upstream: pathlib.Path,
    environment: dict[str, str],
    timeout_seconds: float,
) -> ProcessResult:
    result = execute(
        native,
        ["-L", str(modules), "--run-tests", str(fixture_path)],
        b"",
        upstream,
        environment,
        timeout_seconds,
    )
    output = (result.stdout + b"\n" + result.stderr).decode("utf-8", errors="replace")
    match = re.search(r"(?m)^(\d+) of (\d+) tests passed \(0 malformed, 0 skipped\)$", output)
    if result.exit_code != 0 or match is None:
        raise BenchmarkError(
            f"pinned native jq did not pass {fixture_path.name} through --run-tests "
            f"(exit {result.exit_code})"
        )
    passed, total = (int(value) for value in match.groups())
    if passed != expected_count or total != expected_count:
        raise BenchmarkError(
            f"pinned native jq reported {passed}/{total} for {fixture_path.name}; "
            f"expected {expected_count}/{expected_count}"
        )
    return result


def validate_fixture_preflight(
    native: Implementation,
    managed_implementations: Sequence[Implementation],
    cases: Sequence[FixtureCase],
    fixture_paths: Sequence[pathlib.Path],
    modules: pathlib.Path,
    upstream: pathlib.Path,
    environment: dict[str, str],
    timeout_seconds: float,
) -> dict[str, ProcessResult]:
    """Run the one correctness gate shared by timing and correctness-only modes."""
    print("Validating selected official fixtures with all implementations...", flush=True)
    selected_fixture_names = {case.fixture for case in cases}
    for fixture_path in fixture_paths:
        if fixture_path.name not in selected_fixture_names:
            continue
        native_fixture_result = validate_native_fixture(
            native,
            fixture_path,
            FIXTURE_COUNTS[fixture_path.name],
            modules,
            upstream,
            environment,
            timeout_seconds,
        )
        for implementation in managed_implementations:
            actual_fixture_result = validate_native_fixture(
                implementation,
                fixture_path,
                FIXTURE_COUNTS[fixture_path.name],
                modules,
                upstream,
                environment,
                timeout_seconds,
            )
            if actual_fixture_result.signature != native_fixture_result.signature:
                raise BenchmarkError(
                    f"official --run-tests output differs for {implementation.name} "
                    f"on {fixture_path.name}"
                )

    print(
        f"Validating stdout, stderr, and exit status for {len(cases)} scenarios...",
        flush=True,
    )
    references: dict[str, ProcessResult] = {}
    for case_number, case in enumerate(cases, start=1):
        cli_arguments, stdin = case_arguments(case, modules)
        reference = execute(
            native,
            cli_arguments,
            stdin,
            upstream,
            environment,
            timeout_seconds,
        )
        if case.must_fail and reference.exit_code == 0:
            raise BenchmarkError(
                f"pinned native jq unexpectedly compiled failure scenario {case.scenario_id}"
            )
        references[case.scenario_id] = reference
        for implementation in managed_implementations:
            actual = execute(
                implementation,
                cli_arguments,
                stdin,
                upstream,
                environment,
                timeout_seconds,
            )
            assert_matches_reference(implementation, case, reference, actual, "pre-timing")
        if case_number % 100 == 0 or case_number == len(cases):
            print(f"  validated {case_number}/{len(cases)}", flush=True)
    return references


def command_metadata_value(
    implementation: Implementation,
    option: str,
    cwd: pathlib.Path,
    environment: dict[str, str],
    timeout_seconds: float,
) -> str:
    result = execute(implementation, [option], b"", cwd, environment, timeout_seconds)
    if result.exit_code != 0:
        raise BenchmarkError(f"{implementation.name} {option} exited {result.exit_code}")
    return result.stdout.decode("utf-8", errors="replace").strip()


def sample_stats(samples: Sequence[int]) -> dict[str, float | int | None]:
    ordered = sorted(samples)
    if not ordered:
        raise BenchmarkError("cannot summarize an empty measurement set")
    p95_ms = None
    if len(ordered) >= P95_MINIMUM_SAMPLES:
        rank = math.ceil(0.95 * len(ordered)) - 1
        p95_ms = ordered[rank] / 1_000_000
    return {
        "samples": len(samples),
        "total_ms": sum(samples) / 1_000_000,
        "mean_ms": statistics.fmean(samples) / 1_000_000,
        "median_ms": statistics.median(samples) / 1_000_000,
        "p95_ms": p95_ms,
        "min_ms": ordered[0] / 1_000_000,
        "max_ms": ordered[-1] / 1_000_000,
        "stdev_ms": statistics.pstdev(samples) / 1_000_000,
    }


def format_milliseconds(value: object) -> str:
    return "n/a" if value is None else f"{float(value):.3f}"


def aggregate_measurements(
    cases: Sequence[FixtureCase],
    measurements: dict[tuple[str, str], list[int]],
    implementations: Sequence[Implementation],
) -> list[dict[str, object]]:
    groups = ["ALL", *dict.fromkeys(case.fixture for case in cases)]
    aggregates: list[dict[str, object]] = []
    for group in groups:
        case_ids = [
            case.scenario_id for case in cases if group == "ALL" or case.fixture == group
        ]
        native_values = [
            value
            for case_id in case_ids
            for value in measurements[(case_id, "native-jq")]
        ]
        native_mean = statistics.fmean(native_values)
        for implementation in implementations:
            values = [
                value
                for case_id in case_ids
                for value in measurements[(case_id, implementation.name)]
            ]
            stats = sample_stats(values)
            suite_total_ms = sum(
                statistics.median(measurements[(case_id, implementation.name)])
                for case_id in case_ids
            ) / 1_000_000
            measured_total_ms = stats.pop("total_ms")
            stats.update(
                {
                    "group": group,
                    "implementation": implementation.name,
                    "scenarios": len(case_ids),
                    "total_ms": suite_total_ms,
                    "measured_total_ms": measured_total_ms,
                    "mean_vs_native": statistics.fmean(values) / native_mean,
                }
            )
            aggregates.append(stats)
    return aggregates


def write_reports(
    output_directory: pathlib.Path,
    lease: report_output.ReportLease,
    metadata: dict[str, object],
    cases: Sequence[FixtureCase],
    references: dict[str, ProcessResult],
    measurements: dict[tuple[str, str], list[int]],
    implementations: Sequence[Implementation],
    aggregates: list[dict[str, object]],
    startup_measurements: dict[str, list[int]],
) -> None:
    try:
        report_output.require_prepared_report_directory(
            output_directory, "fixture", lease
        )
    except report_output.ReportOutputError as error:
        raise BenchmarkError(str(error)) from error
    scenario_documents: list[dict[str, object]] = []
    for case in cases:
        reference = references[case.scenario_id]
        implementation_results = {}
        for implementation in implementations:
            samples = measurements[(case.scenario_id, implementation.name)]
            implementation_results[implementation.name] = {
                "samples_ns": samples,
                "statistics": sample_stats(samples),
            }
        scenario_documents.append(
            {
                "id": case.scenario_id,
                "fixture": case.fixture,
                "fixture_index": case.fixture_index,
                "source_line": case.source_line,
                "program": case.program,
                "input": case.input_text,
                "must_fail": case.must_fail,
                "expected_output_count": len(case.expected_lines),
                "reference": {
                    "exit_code": reference.exit_code,
                    "stdout_sha256": sha256_bytes(reference.stdout),
                    "stderr_sha256": sha256_bytes(reference.stderr),
                },
                "implementations": implementation_results,
            }
        )

    json_document = {
        "schema_version": 3,
        "metadata": metadata,
        "aggregates": aggregates,
        "startup": {
            implementation.name: {
                "samples_ns": startup_measurements[implementation.name],
                "statistics": sample_stats(startup_measurements[implementation.name]),
            }
            for implementation in implementations
        },
        "scenarios": scenario_documents,
    }
    (output_directory / "results.json").write_text(
        json.dumps(json_document, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )

    fields = [
        "group",
        "implementation",
        "scenarios",
        "samples",
        "total_ms",
        "measured_total_ms",
        "mean_ms",
        "median_ms",
        "p95_ms",
        "min_ms",
        "max_ms",
        "stdev_ms",
        "mean_vs_native",
    ]
    with (output_directory / "summary.csv").open("w", encoding="utf-8", newline="") as target:
        writer = csv.DictWriter(target, fieldnames=fields)
        writer.writeheader()
        writer.writerows(aggregates)

    with (output_directory / "scenario-summary.csv").open(
        "w", encoding="utf-8", newline=""
    ) as target:
        fields = [
            "scenario_id",
            "fixture",
            "fixture_index",
            "source_line",
            "outcome",
            "implementation",
            "samples",
            "mean_ms",
            "median_ms",
            "p95_ms",
            "min_ms",
            "max_ms",
            "stdev_ms",
        ]
        writer = csv.DictWriter(target, fieldnames=fields)
        writer.writeheader()
        for case in cases:
            reference = references[case.scenario_id]
            outcome = (
                "compile_failure"
                if case.must_fail
                else "runtime_failure"
                if reference.exit_code != 0
                else "empty_success"
                if not reference.stdout
                else "success"
            )
            for implementation in implementations:
                stats = sample_stats(measurements[(case.scenario_id, implementation.name)])
                writer.writerow(
                    {
                        "scenario_id": case.scenario_id,
                        "fixture": case.fixture,
                        "fixture_index": case.fixture_index,
                        "source_line": case.source_line,
                        "outcome": outcome,
                        "implementation": implementation.name,
                        **{key: stats[key] for key in fields if key in stats},
                    }
                )
        for implementation in implementations:
            stats = sample_stats(startup_measurements[implementation.name])
            writer.writerow(
                {
                    "scenario_id": "startup:null",
                    "fixture": "STARTUP",
                    "fixture_index": 1,
                    "source_line": 0,
                    "outcome": "success",
                    "implementation": implementation.name,
                    **{key: stats[key] for key in fields if key in stats},
                }
            )

    with (output_directory / "measurements.csv").open(
        "w", encoding="utf-8", newline=""
    ) as target:
        writer = csv.writer(target)
        writer.writerow(
            [
                "scenario_id",
                "fixture",
                "fixture_index",
                "source_line",
                "implementation",
                "repetition",
                "elapsed_ns",
            ]
        )
        for case in cases:
            for implementation in implementations:
                for repetition, elapsed_ns in enumerate(
                    measurements[(case.scenario_id, implementation.name)], start=1
                ):
                    writer.writerow(
                        [
                            case.scenario_id,
                            case.fixture,
                            case.fixture_index,
                            case.source_line,
                            implementation.name,
                            repetition,
                            elapsed_ns,
                        ]
                    )
        for implementation in implementations:
            for repetition, elapsed_ns in enumerate(
                startup_measurements[implementation.name], start=1
            ):
                writer.writerow(
                    [
                        "startup:null",
                        "STARTUP",
                        1,
                        0,
                        implementation.name,
                        repetition,
                        elapsed_ns,
                    ]
                )

    publication = metadata.get("publication")
    publication_label = (
        "publishable (fresh build attestations verified)"
        if isinstance(publication, dict) and publication.get("publishable")
        else "non-publishable smoke/test result"
    )
    lines = [
        "# DotNetJq CLI performance comparison",
        "",
        f"Generated: {metadata['finished_utc']}",
        f"Publication status: {publication_label}",
        "",
        (
            f"Each of the {metadata['scenario_count']} selected fixture scenarios ran in a new "
            f"process for each implementation. Results include process startup, jq compilation, "
            f"evaluation, output serialization, and process shutdown. Each scenario has "
            f"{metadata['repetitions']} interleaved timed repetitions."
        ),
        "",
        "All implementations passed each selected fixture with the official `--run-tests` runner, "
        "then received an untimed per-scenario correctness/warmup invocation. Every captured "
        "scenario requires byte-for-byte stdout and stderr equality and the exact native exit "
        "status. "
        "Timed output is discarded to avoid pipe/capture distortion.",
        "",
        (
            f"p95 is reported only for groups with at least {P95_MINIMUM_SAMPLES} samples; "
            "smaller groups show `n/a`. Per-scenario CSV rows retain median, minimum, and maximum."
        ),
        "",
        "| Fixture | Implementation | Scenarios | Suite total ms | Mean ms | Median ms | "
        "p95 ms | vs native |",
        "|---|---:|---:|---:|---:|---:|---:|---:|",
    ]
    for row in aggregates:
        lines.append(
            f"| {row['group']} | {row['implementation']} | {row['scenarios']} | "
            f"{float(row['total_ms']):.3f} | {float(row['mean_ms']):.3f} | "
            f"{float(row['median_ms']):.3f} | {format_milliseconds(row['p95_ms'])} | "
            f"{float(row['mean_vs_native']):.2f}x |"
        )
    lines.extend(
        [
            "",
            "`vs native` is the ratio of mean process latency; values above 1 are slower than "
            "native jq. See `results.json` for every program and sample, `measurements.csv` for "
            "raw timings, `scenario-summary.csv` for per-case statistics, and `summary.csv` for "
            "aggregate statistics.",
            "",
        ]
    )
    (output_directory / "summary.md").write_text("\n".join(lines), encoding="utf-8")
    try:
        report_output.complete_report_directory(output_directory, "fixture", lease)
    except report_output.ReportOutputError as error:
        raise BenchmarkError(str(error)) from error


def parse_arguments(argv: Sequence[str]) -> argparse.Namespace:
    root = repository_root()
    parser = argparse.ArgumentParser(
        description=(
            "Compare framework-dependent DotNetJq, NativeAOT DotNetJq, and pinned native jq "
            "with one CLI process per jq-1.8.2 fixture scenario."
        )
    )
    parser.add_argument(
        "--framework-executable",
        default=str(root / "artifacts/performance/framework/DotNetJq.Cli"),
    )
    parser.add_argument(
        "--aot-executable",
        default=str(root / "artifacts/performance/native-aot/DotNetJq.Cli"),
    )
    parser.add_argument(
        "--native-executable",
        default=os.environ.get("DOTNETJQ_ORACLE") or
                str(root / "artifacts/test-assets/jq-1.8.2/oracle/jq"),
    )
    parser.add_argument("--upstream", default=os.environ.get("DOTNETJQ_UPSTREAM") or
                        str(root / "upstream/jq"))
    parser.add_argument("--baseline-root", help="clean pinned baseline checkout with attested builds")
    parser.add_argument(
        "--output-directory", default=str(root / "artifacts/performance/results")
    )
    parser.add_argument("--repetitions", type=int, default=3)
    parser.add_argument(
        "--warmups",
        type=int,
        default=1,
        help="additional untimed warmup passes after the captured correctness pass (default: 1)",
    )
    parser.add_argument("--startup-repetitions", type=int, default=30)
    parser.add_argument("--seed", type=int, default=182)
    parser.add_argument("--timeout-seconds", type=float, default=30.0)
    parser.add_argument(
        "--fixture",
        action="append",
        choices=FIXTURE_ORDER,
        help="benchmark only this fixture; repeat for more than one (default: all seven)",
    )
    parser.add_argument(
        "--limit",
        type=int,
        help="benchmark only the first N parsed scenarios (intended for harness smoke tests)",
    )
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument(
        "--correctness-only",
        action="store_true",
        help=(
            "run the complete official-fixture correctness preflight without warmups, "
            "timing, or report publication"
        ),
    )
    mode.add_argument(
        "--publishable",
        action="store_true",
        help="require fresh deterministic build attestations (normally supplied by run.sh)",
    )
    mode.add_argument(
        "--non-publishable",
        action="store_true",
        help="mark a smoke/--skip-build run and require a non-default output directory",
    )
    arguments = parser.parse_args(argv)
    if arguments.repetitions < 1:
        parser.error("--repetitions must be at least 1")
    if arguments.warmups < 0:
        parser.error("--warmups must not be negative")
    if arguments.startup_repetitions < 1:
        parser.error("--startup-repetitions must be at least 1")
    if arguments.timeout_seconds <= 0:
        parser.error("--timeout-seconds must be positive")
    if arguments.limit is not None and arguments.limit < 1:
        parser.error("--limit must be at least 1")
    if arguments.correctness_only:
        if sum(value == "--correctness-only" for value in argv) != 1:
            parser.error("--correctness-only may be supplied only once")
        for option in CORRECTNESS_ONLY_FORBIDDEN_OPTIONS:
            if any(value == option or value.startswith(option + "=") for value in argv):
                parser.error(f"{option} cannot be used with --correctness-only")
    return arguments


def run_benchmark(
    arguments: argparse.Namespace,
    root: pathlib.Path,
    lease: report_output.ReportLease,
) -> int:
    requested_complete_run = arguments.fixture is None and arguments.limit is None
    output_directory, publishable = prepare_benchmark_report(
        arguments, root, lease, requested_complete_run
    )
    attestations_required = publishable or arguments.correctness_only

    harness_path = pathlib.Path(__file__).resolve()
    harness_paths = (
        harness_path,
        pathlib.Path(provenance.__file__).resolve(),
        pathlib.Path(report_output.__file__).resolve(),
        pathlib.Path(paired_baseline.__file__).resolve(),
    )
    harness_before = manifest_identity(harness_paths, root)
    source_before = source_identity(root)
    try:
        attested_source_before = provenance.source_snapshot(root)
    except provenance.AttestationError as error:
        raise BenchmarkError(str(error)) from error
    started = dt.datetime.now(dt.timezone.utc)
    upstream = pathlib.Path(arguments.upstream).expanduser().resolve()
    fixtures_directory = upstream / "tests"
    modules = fixtures_directory / "modules"
    upstream_before = upstream_identity(upstream)

    selected_names = arguments.fixture or list(FIXTURE_ORDER)
    reject_duplicates(selected_names, "fixture selection")
    fixture_paths = [fixtures_directory / name for name in selected_names]
    all_cases: list[FixtureCase] = []
    fixture_metadata: list[dict[str, object]] = []
    for fixture_path in fixture_paths:
        if not fixture_path.is_file():
            raise BenchmarkError(f"fixture is unavailable: {fixture_path}")
        cases = parse_fixture(fixture_path)
        expected_count = FIXTURE_COUNTS[fixture_path.name]
        if len(cases) != expected_count:
            raise BenchmarkError(
                f"parsed {len(cases)} cases from {fixture_path.name}; expected {expected_count}"
            )
        all_cases.extend(cases)
        fixture_metadata.append(
            {
                "name": fixture_path.name,
                "cases": len(cases),
                "sha256": sha256_file(fixture_path),
            }
        )
    cases = all_cases[: arguments.limit] if arguments.limit else all_cases
    if not cases:
        raise BenchmarkError("no fixture scenarios were selected")
    stable_case_indices = {
        case.scenario_id: index for index, case in enumerate(cases)
    }

    complete_run = arguments.fixture is None and arguments.limit is None and len(cases) == 879
    if complete_run != requested_complete_run:
        raise BenchmarkError(
            "the complete fixture selection did not resolve to the required 879 scenarios"
        )

    framework_path = pathlib.Path(
        resolve_executable(arguments.framework_executable, "framework DotNetJq")
    )
    aot_path = pathlib.Path(resolve_executable(arguments.aot_executable, "NativeAOT DotNetJq"))
    native_path = pathlib.Path(resolve_executable(arguments.native_executable, "native jq"))
    validate_pinned_oracle(native_path)
    implementations = [
        Implementation(
            "framework-dotnetjq",
            (str(framework_path),),
            framework_artifact_paths(framework_path),
        ),
        Implementation("aot-dotnetjq", (str(aot_path),), (aot_path,)),
        Implementation("native-jq", (str(native_path),), (native_path,)),
    ]
    validate_implementation_identities(implementations)
    baseline = paired_baseline.optional_baseline(arguments, root)
    if baseline:
        baseline_subjects = baseline.implementations(Implementation, framework_artifact_paths)
        # Framework apphosts can legitimately be byte-identical across versions;
        # their DLL payloads and source-bound attestations identify the builds.
        validate_implementation_identities([*baseline_subjects, implementations[2]])
        implementations.extend(baseline_subjects)
        paired_baseline.validate_paths(implementations)
    implementation_by_name = {item.name: item for item in implementations}
    initial_artifact_manifests = {
        item.name: implementation_metadata(item, "<preflight>")[
            "artifact_manifest_sha256"
        ]
        for item in implementations
    }

    startup_measurements: dict[str, list[int]] = defaultdict(list)
    attestation_summaries: list[dict[str, object]] = []
    baseline_evidence = None
    with tempfile.TemporaryDirectory(prefix="dotnetjq-performance-home-") as home_text:
        environment = stable_environment(pathlib.Path(home_text))
        dotnet = dotnet_metadata(root, environment, arguments.timeout_seconds)
        try:
            toolchain_before = provenance.dotnet_toolchain_identity(
                root, environment, arguments.timeout_seconds
            )
            framework_runtime_before = provenance.framework_runtime_identity(
                framework_path.parent, toolchain_before
            )
            if baseline:
                baseline_evidence = baseline.verify(toolchain_before)
            if attestations_required:
                attestation_summaries = [
                    provenance.validate_attestation(
                        root,
                        "framework-dotnetjq",
                        framework_path,
                        attested_source_before,
                        toolchain_before,
                    ),
                    provenance.validate_attestation(
                        root,
                        "aot-dotnetjq",
                        aot_path,
                        attested_source_before,
                        toolchain_before,
                    ),
                ]
        except provenance.AttestationError as error:
            raise BenchmarkError(str(error)) from error
        versions = {
            item.name: command_metadata_value(
                item, "--version", upstream, environment, arguments.timeout_seconds
            )
            for item in implementations
        }
        build_configurations = {
            item.name: command_metadata_value(
                item,
                "--build-configuration",
                upstream,
                environment,
                arguments.timeout_seconds,
            )
            for item in implementations
        }
        validate_build_configurations(build_configurations)
        if baseline:
            paired_baseline.validate_configurations(build_configurations)
        if "jq-1.8.2" not in versions["native-jq"]:
            raise BenchmarkError(
                f"native oracle has unexpected version: {versions['native-jq']!r}"
            )
        for name in ("framework-dotnetjq", "aot-dotnetjq"):
            if "jq-1.8.2 compatible" not in versions[name]:
                raise BenchmarkError(f"{name} has unexpected version: {versions[name]!r}")

        native = implementation_by_name["native-jq"]
        startup_case = FixtureCase("STARTUP", 1, 0, "null", None, ("null",), False, False)
        startup_arguments = ["-M", "-n", "-c", "--", "null"]
        startup_reference = execute(
            native,
            startup_arguments,
            b"",
            upstream,
            environment,
            arguments.timeout_seconds,
        )
        for implementation in implementations[:2]:
            actual = execute(
                implementation,
                startup_arguments,
                b"",
                upstream,
                environment,
                arguments.timeout_seconds,
            )
            assert_matches_reference(
                implementation, startup_case, startup_reference, actual, "startup correctness"
            )
        if not arguments.correctness_only:
            for _ in range(arguments.warmups):
                for implementation in implementations:
                    warmup = execute(
                        implementation,
                        startup_arguments,
                        b"",
                        upstream,
                        environment,
                        arguments.timeout_seconds,
                        capture_output=False,
                    )
                    if warmup.exit_code != startup_reference.exit_code:
                        raise BenchmarkError(f"startup warmup failed for {implementation.name}")
            for repetition in range(arguments.startup_repetitions):
                offset = repetition % len(implementations)
                for implementation in implementations[offset:] + implementations[:offset]:
                    result = execute(
                        implementation,
                        startup_arguments,
                        b"",
                        upstream,
                        environment,
                        None,
                        capture_output=False,
                    )
                    if result.exit_code != startup_reference.exit_code:
                        raise BenchmarkError(f"startup timing failed for {implementation.name}")
                    startup_measurements[implementation.name].append(result.elapsed_ns)

        references = validate_fixture_preflight(
            native,
            [item for item in implementations if item.name != "native-jq"],
            cases,
            fixture_paths,
            modules,
            upstream,
            environment,
            arguments.timeout_seconds,
        )
        measurements: dict[tuple[str, str], list[int]] = defaultdict(list)
        if not arguments.correctness_only:
            for warmup_index in range(arguments.warmups):
                print(
                    f"Running untimed fixture warmup {warmup_index + 1}/{arguments.warmups}...",
                    flush=True,
                )
                for case in cases:
                    cli_arguments, stdin = case_arguments(case, modules)
                    for implementation in rotated_implementations(
                        implementations,
                        stable_case_indices[case.scenario_id],
                        warmup_index,
                    ):
                        warmup = execute(
                            implementation,
                            cli_arguments,
                            stdin,
                            upstream,
                            environment,
                            arguments.timeout_seconds,
                            capture_output=False,
                        )
                        if warmup.exit_code != references[case.scenario_id].exit_code:
                            raise BenchmarkError(
                                f"warmup exit mismatch for {implementation.name} at "
                                f"{case.scenario_id}"
                            )

            rng = random.Random(arguments.seed)
            print(
                f"Timing {len(cases)} scenarios x {len(implementations)} implementations x "
                f"{arguments.repetitions} repetitions...",
                flush=True,
            )
            for repetition in range(arguments.repetitions):
                ordered_cases = list(cases)
                rng.shuffle(ordered_cases)
                for case in ordered_cases:
                    cli_arguments, stdin = case_arguments(case, modules)
                    implementation_order = rotated_implementations(
                        implementations,
                        stable_case_indices[case.scenario_id],
                        repetition,
                    )
                    for implementation in implementation_order:
                        actual = execute(
                            implementation,
                            cli_arguments,
                            stdin,
                            upstream,
                            environment,
                            None,
                            capture_output=False,
                        )
                        if actual.exit_code != references[case.scenario_id].exit_code:
                            raise BenchmarkError(
                                f"timed repetition {repetition + 1} exit mismatch for "
                                f"{implementation.name} at {case.scenario_id}: expected "
                                f"{references[case.scenario_id].exit_code}, "
                                f"actual {actual.exit_code}"
                            )
                        measurements[(case.scenario_id, implementation.name)].append(
                            actual.elapsed_ns
                        )
                print(
                    f"  completed repetition {repetition + 1}/{arguments.repetitions}",
                    flush=True,
                )

        dotnet_after = dotnet_metadata(root, environment, arguments.timeout_seconds)
        if dotnet_after != dotnet:
            raise BenchmarkError(".NET host, SDK, or runtime identity changed during benchmark")
        try:
            toolchain_after = provenance.dotnet_toolchain_identity(
                root, environment, arguments.timeout_seconds
            )
            if toolchain_after != toolchain_before:
                raise BenchmarkError(
                    ".NET host, SDK, or runtime inventory changed during benchmark"
                )
            if baseline and baseline.verify(toolchain_after) != baseline_evidence:
                raise BenchmarkError("paired baseline changed during benchmark")
            framework_runtime_after = provenance.framework_runtime_identity(
                framework_path.parent, toolchain_after
            )
            if framework_runtime_after != framework_runtime_before:
                raise BenchmarkError("selected CoreCLR runtime changed during benchmark")
            attested_source_postflight = provenance.source_snapshot(root)
            if attested_source_postflight != attested_source_before:
                raise BenchmarkError(
                    "source repository identity changed while benchmark was running"
                )
            if attestations_required:
                postflight_attestations = [
                    provenance.validate_attestation(
                        root,
                        "framework-dotnetjq",
                        framework_path,
                        attested_source_postflight,
                        toolchain_after,
                    ),
                    provenance.validate_attestation(
                        root,
                        "aot-dotnetjq",
                        aot_path,
                        attested_source_postflight,
                        toolchain_after,
                    ),
                ]
                if postflight_attestations != attestation_summaries:
                    raise BenchmarkError("build attestations changed during benchmark")
        except provenance.AttestationError as error:
            raise BenchmarkError(str(error)) from error

    source_after = source_identity(root)
    if (
        source_after["compiled_input_manifest_sha256"]
        != source_before["compiled_input_manifest_sha256"]
    ):
        raise BenchmarkError("compiled source inputs changed while the benchmark was running")
    try:
        attested_source_after = provenance.source_snapshot(root)
    except provenance.AttestationError as error:
        raise BenchmarkError(str(error)) from error
    if attested_source_after != attested_source_before:
        raise BenchmarkError("source repository identity changed while benchmark was running")
    if upstream_identity(upstream) != upstream_before:
        raise BenchmarkError(
            "upstream HEAD/status, fixture, or module identity changed during benchmark"
        )
    if manifest_identity(harness_paths, root) != harness_before:
        raise BenchmarkError("performance harness changed while benchmark was running")
    implementation_documents = [
        {
            **implementation_metadata(item, versions[item.name]),
            "build_configuration": build_configurations[item.name],
        }
        for item in implementations
    ]
    for document in implementation_documents:
        if (
            document["artifact_manifest_sha256"]
            != initial_artifact_manifests[document["name"]]
        ):
            raise BenchmarkError(
                f"{document['name']} artifacts changed while the benchmark was running"
            )

    if arguments.correctness_only:
        print(
            f"Correctness-only fixture verification passed: {len(cases)}/{len(cases)} "
            f"scenarios across {len(fixture_paths)} fixtures for framework-dependent "
            "and NativeAOT DotNetJq."
        )
        return 0

    if output_directory is None:
        raise BenchmarkError("timing mode did not prepare a report output directory")
    aggregates = aggregate_measurements(cases, measurements, implementations)
    startup_native_mean = statistics.fmean(startup_measurements["native-jq"])
    for implementation in implementations:
        stats = sample_stats(startup_measurements[implementation.name])
        measured_total_ms = stats.pop("total_ms")
        stats.update(
            {
                "group": "STARTUP",
                "implementation": implementation.name,
                "scenarios": 1,
                "total_ms": stats["median_ms"],
                "measured_total_ms": measured_total_ms,
                "mean_vs_native": statistics.fmean(startup_measurements[implementation.name])
                / startup_native_mean,
            }
        )
        aggregates.append(stats)
    finished = dt.datetime.now(dt.timezone.utc)
    metadata: dict[str, object] = {
        "started_utc": started.isoformat(),
        "paired_baseline": baseline_evidence,
        "finished_utc": finished.isoformat(),
        "duration_seconds": (finished - started).total_seconds(),
        "pinned_jq_commit": PINNED_COMMIT,
        "pinned_oracle_sha256": PINNED_ORACLE_SHA256,
        "upstream": str(upstream),
        "upstream_clean": True,
        "upstream_identity": upstream_before,
        "fixtures": fixture_metadata,
        "scenario_count": len(cases),
        "full_fixture_scenario_count": len(all_cases),
        "repetitions": arguments.repetitions,
        "warmups": arguments.warmups,
        "startup_repetitions": arguments.startup_repetitions,
        "seed": arguments.seed,
        "implementation_order": (
            "scenario order is deterministically shuffled; implementation order uses a stable "
            "scenario-index/repetition Latin rotation, independent of shuffled position"
        ),
        "percentile_policy": (
            f"nearest-rank p95 is null below {P95_MINIMUM_SAMPLES} samples; default "
            "per-scenario comparisons use median, minimum, and maximum"
        ),
        "validation_timeout_seconds": arguments.timeout_seconds,
        "timed_wait": (
            "blocking waitpid without Python subprocess timeout polling; every timed command "
            "first passed the timeout-protected correctness gate and any configured warmup"
        ),
        "system": system_metadata(),
        "python": platform.python_version(),
        "implementations": implementation_documents,
        "dotnet": dotnet,
        "dotnet_postflight": dotnet_after,
        "dotnet_identity_stable": True,
        "toolchain_identity": toolchain_before,
        "framework_runtime": {
            "requested_version": framework_runtime_before["requested_version"],
            "selected_version": framework_runtime_before["selected_version"],
            "path": framework_runtime_before["path"],
            "manifest_sha256": framework_runtime_before["files"]["sha256"],
        },
        "source_identity": source_before,
        "publication": {
            "mode": "publishable" if publishable else "non-publishable",
            "publishable": publishable,
            "complete_run": complete_run,
            "attestation_schema_version": provenance.ATTESTATION_SCHEMA_VERSION,
            "attestations_required": publishable,
            "attestations": attestation_summaries,
            "non_publishable_reason": (
                None
                if publishable
                else "limited selection, --skip-build, custom smoke invocation, or explicit mode"
            ),
        },
        "harness": {
            "files": harness_before,
        },
        "environment": environment_contract(),
        "measurement_scope": (
            "one fresh process per fixture scenario; end-to-end wall-clock latency; stdout and "
            "stderr discarded during timing"
        ),
        "correctness": (
            "all implementations pass official --run-tests; untimed per-scenario native "
            "comparison; "
            "all captured scenario exit codes, stdout bytes, and stderr bytes compared exactly; "
            "timed executions recheck exit status"
        ),
    }
    write_reports(
        output_directory,
        lease,
        metadata,
        cases,
        references,
        measurements,
        implementations,
        aggregates,
        startup_measurements,
    )
    overall = [row for row in aggregates if row["group"] == "ALL"]
    print(f"Reports written to {output_directory}")
    for row in overall:
        print(
            f"  {row['implementation']}: mean {row['mean_ms']:.3f} ms, "
            f"median {row['median_ms']:.3f} ms, {row['mean_vs_native']:.2f}x native"
        )
    return 0


def main(argv: Sequence[str]) -> int:
    arguments = parse_arguments(argv)
    root = repository_root()
    requested_output = pathlib.Path(arguments.output_directory)
    try:
        with report_output.report_run_lease(
            requested_output, root, "fixture"
        ) as lease:
            return run_benchmark(arguments, root, lease)
    except report_output.ReportOutputError as error:
        raise BenchmarkError(str(error)) from error


if __name__ == "__main__":
    try:
        raise SystemExit(main(sys.argv[1:]))
    except BenchmarkError as error:
        print(f"performance benchmark failed: {error}", file=sys.stderr)
        raise SystemExit(1) from error
