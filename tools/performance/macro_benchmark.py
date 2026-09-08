#!/usr/bin/env python3
"""End-to-end macrobenchmark for native jq and both DotNetJq CLI deployments."""

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
from decimal import Decimal, InvalidOperation, ROUND_FLOOR
from typing import BinaryIO, Sequence

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
PERFORMANCE_ENVIRONMENT_NAMES = {"GLIBC_TUNABLES"}
EXPECTED_SCENARIO_IDS = tuple(f"{index:02d}" for index in range(21))
IMPLEMENTATION_NAMES = (
    "framework-dotnetjq",
    "aot-dotnetjq",
    "native-jq",
)
UPSTREAM_FIXTURES = (
    "jq.test",
    "man.test",
    "onig.test",
    "manonig.test",
    "base64.test",
    "uri.test",
    "optional.test",
)


class BenchmarkError(RuntimeError):
    """An actionable benchmark configuration or correctness failure."""


@dataclasses.dataclass(frozen=True)
class Dataset:
    name: str
    path: pathlib.Path
    count: int
    size_bytes: int
    sha256: str


@dataclasses.dataclass(frozen=True)
class Scenario:
    id: str
    name: str
    dataset_name: str | None
    arguments: tuple[str, ...]
    jq_filter: str

    @property
    def label(self) -> str:
        return f"{self.id}:{self.name}"


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


@dataclasses.dataclass(frozen=True)
class Measurement:
    repetition: int
    scenario_position: int
    implementation_position: int
    elapsed_ns: int


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


def canonical_json_bytes(value: object) -> bytes:
    return json.dumps(
        value,
        ensure_ascii=False,
        sort_keys=True,
        separators=(",", ":"),
    ).encode("utf-8")


def document_sha256(value: object) -> str:
    return sha256_bytes(canonical_json_bytes(value))


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
        "compiled_input_manifest_sha256": document_sha256(entries),
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


def parse_scale(value: str) -> Decimal:
    try:
        scale = Decimal(value)
    except InvalidOperation as error:
        raise argparse.ArgumentTypeError(f"invalid decimal scale: {value!r}") from error
    if not scale.is_finite() or scale <= 0:
        raise argparse.ArgumentTypeError("scale must be a positive finite decimal")
    return scale


def canonical_decimal(value: Decimal) -> str:
    rendered = format(value.normalize(), "f")
    return "0" if rendered == "-0" else rendered


def scaled_count(base: int, scale: Decimal) -> int:
    return max(
        1,
        int(
            (Decimal(base) * scale + Decimal("0.5")).to_integral_value(
                rounding=ROUND_FLOOR
            )
        ),
    )


def resolve_executable(value: str, label: str) -> pathlib.Path:
    candidate = pathlib.Path(value).expanduser()
    if not candidate.is_absolute():
        candidate = pathlib.Path.cwd() / candidate
    candidate = candidate.resolve()
    if not candidate.is_file():
        raise BenchmarkError(f"{label} executable does not exist: {candidate}")
    if not os.access(candidate, os.X_OK):
        raise BenchmarkError(f"{label} executable is not executable: {candidate}")
    return candidate


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
        "sha256": document_sha256(entries),
        "entries": entries,
    }


def upstream_identity(upstream: pathlib.Path) -> dict[str, object]:
    fixtures_directory = upstream / "tests"
    modules = fixtures_directory / "modules"
    fixtures = [fixtures_directory / name for name in UPSTREAM_FIXTURES]
    missing = [path for path in fixtures if not path.is_file()]
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
            f"upstream jq checkout is dirty; expected the exact pinned tree: {details}"
        )
    return {
        "path": str(upstream),
        "git_head": head,
        "git_status_sha256": sha256_bytes(status.encode("utf-8")),
        "clean": True,
        "fixtures": manifest_identity(fixtures, upstream),
        "modules": manifest_identity(module_paths, upstream),
    }


def dataset_identity(
    datasets: dict[str, Dataset], manifest_path: pathlib.Path
) -> dict[str, object]:
    if not manifest_path.is_file():
        raise BenchmarkError(f"dataset manifest is unavailable: {manifest_path}")
    entries: list[dict[str, object]] = []
    for name, dataset in sorted(datasets.items()):
        if not dataset.path.is_file():
            raise BenchmarkError(f"dataset file is unavailable: {dataset.path}")
        entries.append(
            {
                "name": name,
                "path": str(dataset.path),
                "bytes": dataset.path.stat().st_size,
                "sha256": sha256_file(dataset.path),
                "records": dataset.count,
            }
        )
    return {
        "manifest": {
            "path": str(manifest_path),
            "bytes": manifest_path.stat().st_size,
            "sha256": sha256_file(manifest_path),
        },
        "datasets": entries,
        "sha256": document_sha256(entries),
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
        raise BenchmarkError("publishable macro results require all 21 scenarios")
    if publishable and not arguments.regenerate:
        raise BenchmarkError(
            "publishable macro results require --regenerate so datasets come from the "
            "attested current generator"
        )
    if not publishable and output_directory == default_output:
        raise BenchmarkError(
            "a limited/smoke run is non-publishable and requires an explicit non-default "
            "--output-directory"
        )
    return publishable


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


def load_json_object(path: pathlib.Path, label: str) -> dict[str, object]:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except OSError as error:
        raise BenchmarkError(f"cannot read {label}: {path}: {error}") from error
    except json.JSONDecodeError as error:
        raise BenchmarkError(f"invalid JSON in {label}: {path}: {error}") from error
    if not isinstance(value, dict):
        raise BenchmarkError(f"{label} must contain a JSON object: {path}")
    return value


def load_workloads(
    path: pathlib.Path,
) -> tuple[dict[str, dict[str, object]], list[Scenario], dict[str, object]]:
    document = load_json_object(path, "macro workload manifest")
    if document.get("schema_version") != 1:
        raise BenchmarkError(
            f"unsupported macro workload schema {document.get('schema_version')!r}"
        )
    raw_datasets = document.get("datasets")
    raw_scenarios = document.get("scenarios")
    if not isinstance(raw_datasets, dict) or not isinstance(raw_scenarios, list):
        raise BenchmarkError("workload manifest requires datasets and scenarios")

    dataset_specs: dict[str, dict[str, object]] = {}
    for name, raw in raw_datasets.items():
        if not isinstance(name, str) or not isinstance(raw, dict):
            raise BenchmarkError("invalid dataset entry in workload manifest")
        dataset_path = raw.get("path")
        base_count = raw.get("base_count")
        if not isinstance(dataset_path, str) or not dataset_path:
            raise BenchmarkError(f"dataset {name!r} has no path")
        if not isinstance(base_count, int) or isinstance(base_count, bool) or base_count < 1:
            raise BenchmarkError(f"dataset {name!r} has invalid base_count")
        dataset_specs[name] = {"path": dataset_path, "base_count": base_count}

    scenarios: list[Scenario] = []
    for raw in raw_scenarios:
        if not isinstance(raw, dict):
            raise BenchmarkError("invalid scenario entry in workload manifest")
        scenario_id = raw.get("id")
        name = raw.get("name")
        dataset_name = raw.get("dataset")
        arguments = raw.get("args")
        jq_filter = raw.get("filter")
        if not isinstance(scenario_id, str) or not isinstance(name, str):
            raise BenchmarkError("each scenario requires string id and name")
        if dataset_name is not None and dataset_name not in dataset_specs:
            raise BenchmarkError(
                f"scenario {scenario_id!r} references unknown dataset {dataset_name!r}"
            )
        if not isinstance(arguments, list) or not all(
            isinstance(value, str) for value in arguments
        ):
            raise BenchmarkError(f"scenario {scenario_id!r} has invalid args")
        if not isinstance(jq_filter, str) or not jq_filter:
            raise BenchmarkError(f"scenario {scenario_id!r} has no filter")
        scenarios.append(
            Scenario(
                scenario_id,
                name,
                dataset_name,
                tuple(arguments),
                jq_filter,
            )
        )
    ids = tuple(scenario.id for scenario in scenarios)
    if ids != EXPECTED_SCENARIO_IDS:
        raise BenchmarkError(
            "macro workload manifest must contain scenarios 00 through 20 exactly once "
            f"in order; found {ids!r}"
        )
    return dataset_specs, scenarios, document


def inspect_dataset_cache(
    directory: pathlib.Path,
    dataset_specs: dict[str, dict[str, object]],
    scale: Decimal,
) -> tuple[dict[str, Dataset] | None, str | None]:
    manifest_path = directory / "dataset-sha256.json"
    if not manifest_path.is_file():
        return None, "digest manifest is absent"
    try:
        document = load_json_object(manifest_path, "dataset digest manifest")
    except BenchmarkError as error:
        return None, str(error)
    if document.get("schema_version") != 1:
        return None, "digest manifest schema is not 1"
    if document.get("scale") != canonical_decimal(scale):
        return None, (
            f"digest manifest scale is {document.get('scale')!r}, expected "
            f"{canonical_decimal(scale)!r}"
        )
    entries = document.get("datasets")
    if not isinstance(entries, dict):
        return None, "digest manifest datasets entry is invalid"

    datasets: dict[str, Dataset] = {}
    for name, spec in dataset_specs.items():
        entry = entries.get(name)
        if not isinstance(entry, dict):
            return None, f"digest manifest is missing dataset {name!r}"
        relative_path = entry.get("path")
        count = entry.get("count")
        size_bytes = entry.get("bytes")
        digest = entry.get("sha256")
        expected_count = scaled_count(int(spec["base_count"]), scale)
        if relative_path != spec["path"]:
            return None, f"dataset {name!r} path does not match workload manifest"
        if count != expected_count:
            return None, (
                f"dataset {name!r} count is {count!r}, expected {expected_count}"
            )
        if not isinstance(size_bytes, int) or size_bytes < 0:
            return None, f"dataset {name!r} byte count is invalid"
        if not isinstance(digest, str) or not re.fullmatch(r"[0-9a-f]{64}", digest):
            return None, f"dataset {name!r} SHA-256 is invalid"
        dataset_path = directory / str(relative_path)
        if not dataset_path.is_file():
            return None, f"dataset file is absent: {dataset_path}"
        actual_size = dataset_path.stat().st_size
        if actual_size != size_bytes:
            return None, (
                f"dataset {name!r} size is {actual_size}, expected {size_bytes}"
            )
        actual_digest = sha256_file(dataset_path)
        if actual_digest != digest:
            return None, (
                f"dataset {name!r} SHA-256 is {actual_digest}, expected {digest}"
            )
        datasets[name] = Dataset(name, dataset_path, count, size_bytes, digest)
    return datasets, None


def ensure_datasets(
    directory: pathlib.Path,
    generator: pathlib.Path,
    dataset_specs: dict[str, dict[str, object]],
    scale: Decimal,
    regenerate: bool,
) -> tuple[dict[str, Dataset], pathlib.Path]:
    if not regenerate:
        cached, reason = inspect_dataset_cache(directory, dataset_specs, scale)
        if cached is not None:
            print(f"Reusing verified macro datasets from {directory}", flush=True)
            return cached, directory / "dataset-sha256.json"
        print(f"Regenerating macro datasets: {reason}", flush=True)
    else:
        print("Regenerating macro datasets by request", flush=True)

    if not generator.is_file():
        raise BenchmarkError(f"macro dataset generator is unavailable: {generator}")
    directory.mkdir(parents=True, exist_ok=True)
    completed = subprocess.run(
        [
            sys.executable,
            str(generator),
            "--output",
            str(directory),
            "--scale",
            canonical_decimal(scale),
        ],
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        check=False,
    )
    if completed.stdout:
        print(completed.stdout, end="", flush=True)
    if completed.returncode != 0:
        detail = completed.stderr.strip()
        raise BenchmarkError(
            f"macro dataset generator exited {completed.returncode}: {detail}"
        )
    datasets, reason = inspect_dataset_cache(directory, dataset_specs, scale)
    if datasets is None:
        raise BenchmarkError(f"generated dataset validation failed: {reason}")
    return datasets, directory / "dataset-sha256.json"


def scenario_command_arguments(scenario: Scenario) -> tuple[str, ...]:
    return (*scenario.arguments, "--", scenario.jq_filter)


def execute(
    implementation: Implementation,
    scenario: Scenario,
    input_path: pathlib.Path | None,
    cwd: pathlib.Path,
    environment: dict[str, str],
    timeout_seconds: float | None,
    capture_output: bool,
) -> ProcessResult:
    source_handle: BinaryIO | None = None
    source: BinaryIO | int = subprocess.DEVNULL
    if input_path is not None:
        source_handle = input_path.open("rb")
        source = source_handle
    started = time.perf_counter_ns()
    try:
        completed = subprocess.run(
            [*implementation.command, *scenario_command_arguments(scenario)],
            stdin=source,
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
            f"{implementation.name} exceeded {timeout_seconds:g}s at {scenario.label}"
        ) from error
    finally:
        if source_handle is not None:
            source_handle.close()
    return ProcessResult(
        completed.returncode,
        completed.stdout if capture_output else b"",
        completed.stderr if capture_output else b"",
        time.perf_counter_ns() - started,
    )


def command_metadata_value(
    implementation: Implementation,
    option: str,
    cwd: pathlib.Path,
    environment: dict[str, str],
    timeout_seconds: float,
) -> str:
    try:
        completed = subprocess.run(
            [*implementation.command, option],
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            cwd=cwd,
            env=environment,
            timeout=timeout_seconds,
            check=False,
        )
    except subprocess.TimeoutExpired as error:
        raise BenchmarkError(
            f"{implementation.name} {option} exceeded {timeout_seconds:g}s"
        ) from error
    if completed.returncode != 0:
        raise BenchmarkError(
            f"{implementation.name} {option} exited {completed.returncode}: "
            f"{completed.stderr.decode('utf-8', errors='replace').strip()}"
        )
    return completed.stdout.decode("utf-8", errors="replace").strip()


def abbreviated_bytes(value: bytes, limit: int = 400) -> str:
    text = value.decode("utf-8", errors="backslashreplace")
    return repr(text if len(text) <= limit else text[:limit] + "...")


def assert_exact_match(
    implementation: Implementation,
    scenario: Scenario,
    reference: ProcessResult,
    actual: ProcessResult,
) -> None:
    if actual.signature == reference.signature:
        return
    differences: list[str] = []
    if actual.exit_code != reference.exit_code:
        differences.append(
            f"exit expected={reference.exit_code}, actual={actual.exit_code}"
        )
    if actual.stdout != reference.stdout:
        differences.append(
            "stdout "
            f"expected bytes={len(reference.stdout)} sha256={sha256_bytes(reference.stdout)}, "
            f"actual bytes={len(actual.stdout)} sha256={sha256_bytes(actual.stdout)}; "
            f"expected prefix={abbreviated_bytes(reference.stdout)}, "
            f"actual prefix={abbreviated_bytes(actual.stdout)}"
        )
    if actual.stderr != reference.stderr:
        differences.append(
            "stderr "
            f"expected bytes={len(reference.stderr)} sha256={sha256_bytes(reference.stderr)}, "
            f"actual bytes={len(actual.stderr)} sha256={sha256_bytes(actual.stderr)}; "
            f"expected prefix={abbreviated_bytes(reference.stderr)}, "
            f"actual prefix={abbreviated_bytes(actual.stderr)}"
        )
    raise BenchmarkError(
        f"captured correctness mismatch for {implementation.name} at {scenario.label}: "
        + "; ".join(differences)
    )


def output_fingerprint(result: ProcessResult) -> dict[str, object]:
    return {
        "exit_code": result.exit_code,
        "stdout_bytes": len(result.stdout),
        "stdout_sha256": sha256_bytes(result.stdout),
        "stderr_bytes": len(result.stderr),
        "stderr_sha256": sha256_bytes(result.stderr),
    }


def sample_stats(samples_ns: Sequence[int]) -> dict[str, float | int | None]:
    if not samples_ns:
        raise BenchmarkError("cannot summarize an empty measurement set")
    ordered = sorted(samples_ns)
    p95_ms = None
    if len(ordered) >= P95_MINIMUM_SAMPLES:
        p95_rank = math.ceil(0.95 * len(ordered)) - 1
        p95_ms = ordered[p95_rank] / 1_000_000
    return {
        "samples": len(samples_ns),
        "mean_ms": statistics.fmean(samples_ns) / 1_000_000,
        "median_ms": statistics.median(samples_ns) / 1_000_000,
        "p95_ms": p95_ms,
        "min_ms": ordered[0] / 1_000_000,
        "max_ms": ordered[-1] / 1_000_000,
        "stdev_ms": statistics.pstdev(samples_ns) / 1_000_000,
    }


def format_ratio(value: object) -> str:
    return "n/a" if value is None else f"{float(value):.2f}x"


def ratio(value: float, reference: float) -> float:
    if reference <= 0:
        raise BenchmarkError("native timing must be positive")
    return value / reference


def throughput_mib_s(size_bytes: int, median_ms: float) -> float | None:
    if size_bytes == 0 or median_ms <= 0:
        return None
    return (size_bytes / (1024 * 1024)) / (median_ms / 1000)


def summarize_measurements(
    scenarios: Sequence[Scenario],
    datasets: dict[str, Dataset],
    implementations: Sequence[Implementation],
    measurements: dict[tuple[str, str], list[Measurement]],
) -> tuple[list[dict[str, object]], list[dict[str, object]]]:
    summaries: list[dict[str, object]] = []
    by_key: dict[tuple[str, str], dict[str, object]] = {}
    for scenario in scenarios:
        dataset = datasets.get(scenario.dataset_name or "")
        size_bytes = dataset.size_bytes if dataset is not None else 0
        native_samples = [
            value.elapsed_ns
            for value in measurements[(scenario.id, "native-jq")]
        ]
        native_stats = sample_stats(native_samples)
        for implementation in implementations:
            samples = [
                value.elapsed_ns
                for value in measurements[(scenario.id, implementation.name)]
            ]
            stats = sample_stats(samples)
            stats_p95 = stats["p95_ms"]
            native_p95 = native_stats["p95_ms"]
            row: dict[str, object] = {
                "group": "scenario",
                "scenario_id": scenario.id,
                "scenario_name": scenario.name,
                "dataset": scenario.dataset_name,
                "dataset_count": dataset.count if dataset is not None else 0,
                "input_bytes": size_bytes,
                "input_mib": size_bytes / (1024 * 1024),
                "implementation": implementation.name,
                **stats,
                "median_vs_native": ratio(
                    float(stats["median_ms"]), float(native_stats["median_ms"])
                ),
                "p95_vs_native": None
                if stats_p95 is None or native_p95 is None
                else ratio(float(stats_p95), float(native_p95)),
                "throughput_mib_s": throughput_mib_s(
                    size_bytes, float(stats["median_ms"])
                ),
            }
            summaries.append(row)
            by_key[(scenario.id, implementation.name)] = row

    aggregates: list[dict[str, object]] = []
    processing_scenarios = [scenario for scenario in scenarios if scenario.id != "00"]
    aggregate_groups: list[tuple[str, str, list[Scenario]]] = []
    if processing_scenarios:
        aggregate_groups.append(
            ("PROCESSING_ONLY", "processing scenarios (startup excluded)", processing_scenarios)
        )
    if any(scenario.id == "00" for scenario in scenarios):
        aggregate_groups.append(
            ("WITH_STARTUP", "selected scenarios including startup", list(scenarios))
        )

    for group, description, group_scenarios in aggregate_groups:
        total_input_bytes = sum(
            datasets[scenario.dataset_name].size_bytes
            for scenario in group_scenarios
            if scenario.dataset_name is not None
        )
        for implementation in implementations:
            rows = [
                by_key[(scenario.id, implementation.name)]
                for scenario in group_scenarios
            ]
            median_ratios = [float(row["median_vs_native"]) for row in rows]
            p95_ratios = [
                float(row["p95_vs_native"])
                for row in rows
                if row["p95_vs_native"] is not None
            ]
            sum_median_ms = sum(float(row["median_ms"]) for row in rows)
            aggregate = {
                "group": group,
                "scenario_id": group,
                "scenario_name": description,
                "dataset": None,
                "dataset_count": 0,
                "input_bytes": total_input_bytes,
                "input_mib": total_input_bytes / (1024 * 1024),
                "implementation": implementation.name,
                "samples": sum(int(row["samples"]) for row in rows),
                "sum_scenario_medians_ms": sum_median_ms,
                "geomean_median_vs_native": math.exp(
                    statistics.fmean(math.log(value) for value in median_ratios)
                ),
                "geomean_p95_vs_native": None
                if len(p95_ratios) != len(rows)
                else math.exp(statistics.fmean(math.log(value) for value in p95_ratios)),
                "aggregate_throughput_mib_s": throughput_mib_s(
                    total_input_bytes, sum_median_ms
                ),
            }
            aggregates.append(aggregate)
    return summaries, aggregates


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
        "executable_bytes": executable.stat().st_size,
        "executable_sha256": sha256_file(executable),
        "executable_mtime_ns": executable.stat().st_mtime_ns,
        "artifacts": artifacts,
        "runtime_payload_bytes": sum(item["bytes"] for item in artifact_contract),
        "artifact_manifest_sha256": document_sha256(artifact_contract),
    }


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


def write_reports(
    output_directory: pathlib.Path,
    lease: report_output.ReportLease,
    metadata: dict[str, object],
    scenarios: Sequence[Scenario],
    datasets: dict[str, Dataset],
    implementations: Sequence[Implementation],
    correctness: dict[tuple[str, str], ProcessResult],
    measurements: dict[tuple[str, str], list[Measurement]],
    summaries: list[dict[str, object]],
    aggregates: list[dict[str, object]],
) -> None:
    try:
        report_output.require_prepared_report_directory(output_directory, "macro", lease)
    except report_output.ReportOutputError as error:
        raise BenchmarkError(str(error)) from error
    summary_by_key = {
        (str(row["scenario_id"]), str(row["implementation"])): row
        for row in summaries
    }
    scenario_documents: list[dict[str, object]] = []
    for scenario in scenarios:
        dataset = datasets.get(scenario.dataset_name or "")
        scenario_documents.append(
            {
                "id": scenario.id,
                "name": scenario.name,
                "dataset": scenario.dataset_name,
                "input": None
                if dataset is None
                else {
                    "path": str(dataset.path),
                    "count": dataset.count,
                    "bytes": dataset.size_bytes,
                    "sha256": dataset.sha256,
                },
                "arguments": list(scenario.arguments),
                "filter": scenario.jq_filter,
                "correctness": {
                    implementation.name: output_fingerprint(
                        correctness[(scenario.id, implementation.name)]
                    )
                    for implementation in implementations
                },
                "implementations": {
                    implementation.name: {
                        "samples_ns": [
                            sample.elapsed_ns
                            for sample in measurements[(scenario.id, implementation.name)]
                        ],
                        "statistics": {
                            key: value
                            for key, value in summary_by_key[
                                (scenario.id, implementation.name)
                            ].items()
                            if key
                            not in {
                                "group",
                                "scenario_id",
                                "scenario_name",
                                "dataset",
                                "dataset_count",
                                "input_bytes",
                                "input_mib",
                                "implementation",
                            }
                        },
                    }
                    for implementation in implementations
                },
            }
        )
    result_document = {
        "schema_version": 3,
        "metadata": metadata,
        "datasets": {
            name: {
                "path": str(dataset.path),
                "count": dataset.count,
                "bytes": dataset.size_bytes,
                "sha256": dataset.sha256,
            }
            for name, dataset in datasets.items()
        },
        "aggregates": aggregates,
        "scenarios": scenario_documents,
    }
    (output_directory / "results.json").write_text(
        json.dumps(result_document, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )

    with (output_directory / "raw.csv").open(
        "w", encoding="utf-8", newline=""
    ) as target:
        writer = csv.writer(target)
        writer.writerow(
            [
                "scenario_id",
                "scenario_name",
                "dataset",
                "implementation",
                "repetition",
                "scenario_position",
                "implementation_position",
                "elapsed_ns",
            ]
        )
        for scenario in scenarios:
            for implementation in implementations:
                for sample in measurements[(scenario.id, implementation.name)]:
                    writer.writerow(
                        [
                            scenario.id,
                            scenario.name,
                            scenario.dataset_name or "",
                            implementation.name,
                            sample.repetition,
                            sample.scenario_position,
                            sample.implementation_position,
                            sample.elapsed_ns,
                        ]
                    )

    summary_fields = [
        "group",
        "scenario_id",
        "scenario_name",
        "dataset",
        "dataset_count",
        "input_bytes",
        "input_mib",
        "implementation",
        "samples",
        "mean_ms",
        "median_ms",
        "p95_ms",
        "min_ms",
        "max_ms",
        "stdev_ms",
        "median_vs_native",
        "p95_vs_native",
        "throughput_mib_s",
        "sum_scenario_medians_ms",
        "geomean_median_vs_native",
        "geomean_p95_vs_native",
        "aggregate_throughput_mib_s",
    ]
    with (output_directory / "summary.csv").open(
        "w", encoding="utf-8", newline=""
    ) as target:
        writer = csv.DictWriter(target, fieldnames=summary_fields)
        writer.writeheader()
        for row in [*summaries, *aggregates]:
            writer.writerow({field: row.get(field) for field in summary_fields})

    native_rows = {
        scenario.id: summary_by_key[(scenario.id, "native-jq")]
        for scenario in scenarios
    }
    framework_rows = {
        scenario.id: summary_by_key[(scenario.id, "framework-dotnetjq")]
        for scenario in scenarios
    }
    aot_rows = {
        scenario.id: summary_by_key[(scenario.id, "aot-dotnetjq")]
        for scenario in scenarios
    }
    publication = metadata.get("publication")
    publication_label = (
        "publishable (fresh build attestations verified)"
        if isinstance(publication, dict) and publication.get("publishable")
        else "non-publishable smoke/test result"
    )
    lines = [
        "# DotNetJq macro performance comparison",
        "",
        f"Generated: {metadata['finished_utc']}",
        f"Publication status: {publication_label}",
        "",
        (
            f"All {len(scenarios)} workloads passed an exact captured stdout, stderr, and "
            "exit-status comparison against pinned native jq before timing. Each timing is a "
            "fresh CLI process; dataset input is supplied through standard input and timed output "
            "is discarded."
        ),
        "",
        "Ratios are latency divided by native jq latency; values above 1.00x are slower.",
        "Throughput is input MiB divided by median end-to-end process time.",
        (
            f"Nearest-rank p95 is reported only with at least {P95_MINIMUM_SAMPLES} samples; "
            "the default three-repetition table therefore shows median and min-max instead."
        ),
        "",
        "| ID | Workload | Input MiB | .NET median [min-max] ms | .NET/native | "
        "AOT median [min-max] ms | AOT/native | Native median [min-max] ms | "
        "Throughput MiB/s (.NET / AOT / native) |",
        "|---:|---|---:|---:|---:|---:|---:|---:|---:|",
    ]
    for scenario in scenarios:
        framework = framework_rows[scenario.id]
        aot = aot_rows[scenario.id]
        native = native_rows[scenario.id]

        def display_throughput(row: dict[str, object]) -> str:
            value = row["throughput_mib_s"]
            return "n/a" if value is None else f"{float(value):.2f}"

        lines.append(
            f"| {scenario.id} | {scenario.name} | {float(native['input_mib']):.3f} | "
            f"{float(framework['median_ms']):.3f} "
            f"[{float(framework['min_ms']):.3f}-{float(framework['max_ms']):.3f}] | "
            f"{float(framework['median_vs_native']):.2f}x | "
            f"{float(aot['median_ms']):.3f} "
            f"[{float(aot['min_ms']):.3f}-{float(aot['max_ms']):.3f}] | "
            f"{float(aot['median_vs_native']):.2f}x | "
            f"{float(native['median_ms']):.3f} "
            f"[{float(native['min_ms']):.3f}-{float(native['max_ms']):.3f}] | "
            f"{display_throughput(framework)} / {display_throughput(aot)} / "
            f"{display_throughput(native)} |"
        )
    aggregate_by_key = {
        (str(row["group"]), str(row["implementation"])): row for row in aggregates
    }
    aggregate_groups = list(dict.fromkeys(str(row["group"]) for row in aggregates))
    lines.extend(
        [
            "",
            "## Aggregate",
            "",
            "| Scope | Implementation | Sum of scenario medians ms | Geomean median ratio | "
            "Geomean p95 ratio | Aggregate MiB/s |",
            "|---|---|---:|---:|---:|---:|",
        ]
    )
    for group in aggregate_groups:
        for name in (item.name for item in implementations):
            row = aggregate_by_key[(group, name)]
            throughput = row["aggregate_throughput_mib_s"]
            lines.append(
                f"| {group} | {name} | "
                f"{float(row['sum_scenario_medians_ms']):.3f} | "
                f"{float(row['geomean_median_vs_native']):.2f}x | "
                f"{format_ratio(row['geomean_p95_vs_native'])} | "
                f"{'n/a' if throughput is None else f'{float(throughput):.2f}'} |"
            )
    lines.extend(
        [
            "",
            "See `results.json` for hashes, exact correctness fingerprints, metadata, and every "
            "sample; `raw.csv` for raw measurements; and `summary.csv` for machine-readable "
            "scenario and aggregate statistics.",
            "",
        ]
    )
    (output_directory / "summary.md").write_text(
        "\n".join(lines), encoding="utf-8"
    )
    try:
        report_output.complete_report_directory(output_directory, "macro", lease)
    except report_output.ReportOutputError as error:
        raise BenchmarkError(str(error)) from error


def parse_arguments(argv: Sequence[str]) -> argparse.Namespace:
    root = repository_root()
    parser = argparse.ArgumentParser(
        description=(
            "Compare framework-dependent DotNetJq, NativeAOT DotNetJq, and pinned native "
            "jq across deterministic large-data jq CLI workloads."
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
        "--workload-manifest",
        default=str(root / "tools/performance/macro/workloads.json"),
    )
    parser.add_argument(
        "--generator",
        default=str(root / "tools/performance/macro/generate.py"),
    )
    parser.add_argument(
        "--data-directory",
        help=(
            "generated dataset directory (default: artifacts/performance/macro-data/scale-<scale>)"
        ),
    )
    parser.add_argument(
        "--output-directory",
        default=str(root / "artifacts/performance/macro-results"),
    )
    parser.add_argument("--scale", type=parse_scale, default=Decimal("0.1"))
    parser.add_argument("--warmups", type=int, default=1)
    parser.add_argument("--repetitions", type=int, default=3)
    parser.add_argument("--timeout-seconds", type=float, default=300.0)
    parser.add_argument("--seed", type=int, default=18_202)
    parser.add_argument(
        "--scenario",
        action="append",
        help="run only this scenario id or name; repeat to select several (default: all 21)",
    )
    parser.add_argument(
        "--regenerate",
        action="store_true",
        help=(
            "regenerate datasets even when the digest-verified cache is reusable; "
            "required for publishable reports"
        ),
    )
    publication = parser.add_mutually_exclusive_group()
    publication.add_argument(
        "--publishable",
        action="store_true",
        help="require fresh datasets and deterministic build attestations",
    )
    publication.add_argument(
        "--non-publishable",
        action="store_true",
        help="mark a smoke run and require a non-default output directory",
    )
    arguments = parser.parse_args(argv)
    if arguments.warmups < 0:
        parser.error("--warmups must not be negative")
    if arguments.repetitions < 1:
        parser.error("--repetitions must be at least 1")
    if arguments.timeout_seconds <= 0:
        parser.error("--timeout-seconds must be positive")
    return arguments


def select_scenarios(
    scenarios: Sequence[Scenario], selectors: Sequence[str] | None
) -> list[Scenario]:
    if not selectors:
        return list(scenarios)
    selected: list[Scenario] = []
    for selector in selectors:
        matches = [
            scenario
            for scenario in scenarios
            if scenario.id == selector or scenario.name == selector
        ]
        if not matches:
            raise BenchmarkError(f"unknown macro scenario id or name: {selector!r}")
        if matches[0] not in selected:
            selected.append(matches[0])
    return selected


def run_benchmark(
    arguments: argparse.Namespace,
    root: pathlib.Path,
    lease: report_output.ReportLease,
) -> int:
    requested_complete_run = arguments.scenario is None
    requested_output = pathlib.Path(arguments.output_directory)
    try:
        output_directory = report_output.resolve_report_directory(
            requested_output, root, "macro"
        )
    except report_output.ReportOutputError as error:
        raise BenchmarkError(str(error)) from error
    default_output = (
        root / report_output.report_spec("macro").default_relative_path
    ).resolve(strict=False)
    try:
        prepared_output = report_output.prepare_report_directory(
            requested_output, root, "macro", lease
        )
    except report_output.ReportOutputError as error:
        raise BenchmarkError(str(error)) from error
    if prepared_output != output_directory:
        raise BenchmarkError("report output path changed while it was being prepared")
    publishable = publication_decision(
        arguments, requested_complete_run, output_directory, default_output
    )

    harness_path = pathlib.Path(__file__).resolve()
    workload_path = pathlib.Path(arguments.workload_manifest).expanduser().resolve()
    generator_path = pathlib.Path(arguments.generator).expanduser().resolve()
    harness_paths = (
        harness_path,
        pathlib.Path(provenance.__file__).resolve(),
        pathlib.Path(report_output.__file__).resolve(),
        pathlib.Path(paired_baseline.__file__).resolve(),
        workload_path,
        generator_path,
    )
    source_before = source_identity(root)
    try:
        attested_source_before = provenance.source_snapshot(root)
    except provenance.AttestationError as error:
        raise BenchmarkError(str(error)) from error
    upstream = pathlib.Path(arguments.upstream).expanduser().resolve()
    upstream_before = upstream_identity(upstream)
    started = dt.datetime.now(dt.timezone.utc)
    scale: Decimal = arguments.scale
    scale_text = canonical_decimal(scale)
    dataset_specs, all_scenarios, workload_document = load_workloads(workload_path)
    if not generator_path.is_file():
        raise BenchmarkError(f"macro dataset generator is unavailable: {generator_path}")
    workload_sha256 = sha256_file(workload_path)
    generator_sha256 = sha256_file(generator_path)
    harness_before = manifest_identity(harness_paths, root)
    scenarios = select_scenarios(all_scenarios, arguments.scenario)
    stable_scenario_indices = {
        scenario.id: index for index, scenario in enumerate(all_scenarios)
    }

    scale_path_component = re.sub(r"[^0-9A-Za-z]+", "p", scale_text).strip("p") or "0"
    data_directory = pathlib.Path(
        arguments.data_directory
        or root / "artifacts/performance/macro-data" / f"scale-{scale_path_component}"
    ).expanduser().resolve()
    datasets, dataset_manifest_path = ensure_datasets(
        data_directory,
        generator_path,
        dataset_specs,
        scale,
        arguments.regenerate,
    )
    datasets_before = dataset_identity(datasets, dataset_manifest_path)

    complete_run = arguments.scenario is None and len(scenarios) == 21
    if complete_run != requested_complete_run:
        raise BenchmarkError(
            "the complete macro selection did not resolve to the required 21 scenarios"
        )

    framework_path = resolve_executable(
        arguments.framework_executable, "framework DotNetJq"
    )
    aot_path = resolve_executable(arguments.aot_executable, "NativeAOT DotNetJq")
    native_path = resolve_executable(arguments.native_executable, "native jq")
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

    correctness: dict[tuple[str, str], ProcessResult] = {}
    measurements: dict[tuple[str, str], list[Measurement]] = defaultdict(list)
    attestation_summaries: list[dict[str, object]] = []
    baseline_evidence = None
    with tempfile.TemporaryDirectory(prefix="dotnetjq-macro-home-") as home_text:
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
            if publishable:
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
            implementation.name: command_metadata_value(
                implementation,
                "--version",
                root,
                environment,
                arguments.timeout_seconds,
            )
            for implementation in implementations
        }
        build_configurations = {
            implementation.name: command_metadata_value(
                implementation,
                "--build-configuration",
                root,
                environment,
                arguments.timeout_seconds,
            )
            for implementation in implementations
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

        print(
            f"Validating exact output for {len(scenarios)} macro scenarios at scale "
            f"{scale_text}...",
            flush=True,
        )
        native = implementation_by_name["native-jq"]
        for position, scenario in enumerate(scenarios, start=1):
            dataset = datasets.get(scenario.dataset_name or "")
            input_path = dataset.path if dataset is not None else None
            reference = execute(
                native,
                scenario,
                input_path,
                root,
                environment,
                arguments.timeout_seconds,
                capture_output=True,
            )
            if reference.exit_code != 0:
                raise BenchmarkError(
                    f"native jq exited {reference.exit_code} at {scenario.label}: "
                    f"{abbreviated_bytes(reference.stderr)}"
                )
            correctness[(scenario.id, native.name)] = reference
            for implementation in implementations:
                if implementation.name == "native-jq":
                    continue
                actual = execute(
                    implementation,
                    scenario,
                    input_path,
                    root,
                    environment,
                    arguments.timeout_seconds,
                    capture_output=True,
                )
                correctness[(scenario.id, implementation.name)] = actual
                assert_exact_match(implementation, scenario, reference, actual)
            print(f"  validated {position}/{len(scenarios)} {scenario.label}", flush=True)

        for warmup_index in range(arguments.warmups):
            print(
                f"Running interleaved warmup {warmup_index + 1}/{arguments.warmups}...",
                flush=True,
            )
            offset = warmup_index % len(scenarios)
            ordered = scenarios[offset:] + scenarios[:offset]
            for scenario in ordered:
                dataset = datasets.get(scenario.dataset_name or "")
                input_path = dataset.path if dataset is not None else None
                implementation_order = rotated_implementations(
                    implementations,
                    stable_scenario_indices[scenario.id],
                    warmup_index,
                )
                for implementation in implementation_order:
                    result = execute(
                        implementation,
                        scenario,
                        input_path,
                        root,
                        environment,
                        arguments.timeout_seconds,
                        capture_output=False,
                    )
                    expected_exit = correctness[(scenario.id, native.name)].exit_code
                    if result.exit_code != expected_exit:
                        raise BenchmarkError(
                            f"warmup exit mismatch for {implementation.name} at "
                            f"{scenario.label}: expected {expected_exit}, actual "
                            f"{result.exit_code}"
                        )

        rng = random.Random(arguments.seed)
        print(
            f"Timing {len(scenarios)} scenarios x {len(implementations)} implementations x "
            f"{arguments.repetitions} repetitions...",
            flush=True,
        )
        for repetition in range(1, arguments.repetitions + 1):
            ordered = list(scenarios)
            rng.shuffle(ordered)
            for scenario_position, scenario in enumerate(ordered, start=1):
                dataset = datasets.get(scenario.dataset_name or "")
                input_path = dataset.path if dataset is not None else None
                implementation_order = rotated_implementations(
                    implementations,
                    stable_scenario_indices[scenario.id],
                    repetition - 1,
                )
                for implementation_position, implementation in enumerate(
                    implementation_order, start=1
                ):
                    result = execute(
                        implementation,
                        scenario,
                        input_path,
                        root,
                        environment,
                        None,
                        capture_output=False,
                    )
                    expected_exit = correctness[(scenario.id, native.name)].exit_code
                    if result.exit_code != expected_exit:
                        raise BenchmarkError(
                            f"timed repetition {repetition} exit mismatch for "
                            f"{implementation.name} at {scenario.label}: expected "
                            f"{expected_exit}, actual {result.exit_code}"
                        )
                    measurements[(scenario.id, implementation.name)].append(
                        Measurement(
                            repetition,
                            scenario_position,
                            implementation_position,
                            result.elapsed_ns,
                        )
                    )
            print(
                f"  completed repetition {repetition}/{arguments.repetitions}",
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
            if publishable:
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
        raise BenchmarkError("macro harness, workload, or generator changed during benchmark")
    if dataset_identity(datasets, dataset_manifest_path) != datasets_before:
        raise BenchmarkError("macro dataset manifest or dataset files changed during benchmark")
    implementation_documents = [
        {
            **implementation_metadata(implementation, versions[implementation.name]),
            "build_configuration": build_configurations[implementation.name],
        }
        for implementation in implementations
    ]
    for document in implementation_documents:
        if (
            document["artifact_manifest_sha256"]
            != initial_artifact_manifests[document["name"]]
        ):
            raise BenchmarkError(
                f"{document['name']} artifacts changed while the benchmark was running"
            )

    summaries, aggregates = summarize_measurements(
        scenarios, datasets, implementations, measurements
    )
    finished = dt.datetime.now(dt.timezone.utc)
    system = system_metadata()
    env_contract = environment_contract()
    metadata: dict[str, object] = {
        "started_utc": started.isoformat(),
        "paired_baseline": baseline_evidence,
        "finished_utc": finished.isoformat(),
        "duration_seconds": (finished - started).total_seconds(),
        "pinned_jq_commit": PINNED_COMMIT,
        "pinned_oracle_sha256": PINNED_ORACLE_SHA256,
        "upstream": str(upstream),
        "upstream_identity": upstream_before,
        "scale": scale_text,
        "scenario_count": len(scenarios),
        "full_scenario_count": len(all_scenarios),
        "warmups": arguments.warmups,
        "repetitions": arguments.repetitions,
        "validation_timeout_seconds": arguments.timeout_seconds,
        "timed_wait": (
            "blocking waitpid without Python subprocess timeout polling; every timed command "
            "first passed the timeout-protected correctness gate and any configured warmup"
        ),
        "seed": arguments.seed,
        "implementation_order": (
            "scenario order is deterministically shuffled; implementation order uses a stable "
            "scenario-index/repetition Latin rotation, independent of shuffled position"
        ),
        "percentile_policy": (
            f"nearest-rank p95 is null below {P95_MINIMUM_SAMPLES} samples; default "
            "per-scenario comparisons use median, minimum, and maximum"
        ),
        "python": platform.python_version(),
        "workload_manifest": {
            "path": str(workload_path),
            "sha256": workload_sha256,
            "document_sha256": document_sha256(workload_document),
        },
        "dataset_manifest": {
            "path": str(dataset_manifest_path),
            "sha256": sha256_file(dataset_manifest_path),
            "identity": datasets_before,
        },
        "generator": {
            "path": str(generator_path),
            "sha256": generator_sha256,
        },
        "datasets_regenerated": arguments.regenerate,
        "harness": {
            "files": harness_before,
        },
        "system": system,
        "system_sha256": document_sha256(system),
        "environment": env_contract,
        "environment_sha256": document_sha256(env_contract),
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
                else "limited selection, smoke invocation, or explicit non-publishable mode"
            ),
        },
        "measurement_scope": (
            "one fresh process per scenario and implementation; end-to-end wall-clock; "
            "dataset file connected to stdin; timed stdout and stderr sent to the null device"
        ),
        "correctness": (
            "captured native jq reference followed by exact exit-code, stdout-byte, and "
            "stderr-byte equality for framework-dependent and NativeAOT DotNetJq; mismatch "
            "aborts before warmup or timing"
        ),
    }
    write_reports(
        output_directory,
        lease,
        metadata,
        scenarios,
        datasets,
        implementations,
        correctness,
        measurements,
        summaries,
        aggregates,
    )
    print(f"Reports written to {output_directory}")
    for row in aggregates:
        print(
            f"  {row['group']} {row['implementation']}: sum of medians "
            f"{float(row['sum_scenario_medians_ms']):.3f} ms, geomean "
            f"{float(row['geomean_median_vs_native']):.2f}x native"
        )
    return 0


def main(argv: Sequence[str]) -> int:
    arguments = parse_arguments(argv)
    root = repository_root()
    requested_output = pathlib.Path(arguments.output_directory)
    try:
        with report_output.report_run_lease(
            requested_output, root, "macro"
        ) as lease:
            return run_benchmark(arguments, root, lease)
    except report_output.ReportOutputError as error:
        raise BenchmarkError(str(error)) from error


if __name__ == "__main__":
    try:
        raise SystemExit(main(sys.argv[1:]))
    except BenchmarkError as error:
        print(f"macro performance benchmark failed: {error}", file=sys.stderr)
        raise SystemExit(1) from error
