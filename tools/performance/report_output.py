#!/usr/bin/env python3
"""Safely prepare and complete performance-report directories."""

from __future__ import annotations

import argparse
import contextlib
import csv
import dataclasses
import fcntl
import hashlib
import json
import os
import pathlib
import shutil
import stat
import subprocess
import sys
import tempfile
import uuid
from collections.abc import Iterator, Mapping, Sequence


STATUS_FILENAME = ".dotnetjq-report-status.json"
STATUS_SCHEMA_VERSION = 2
LEASE_SCHEMA_VERSION = 1
LEASE_FILENAME = ".dotnetjq-report-run.lock"
LEASE_ID_ENVIRONMENT = "DOTNETJQ_REPORT_LEASE_ID"
LEASE_FD_ENVIRONMENT = "DOTNETJQ_REPORT_LEASE_FD"

IMPLEMENTATION_NAMES = (
    "framework-dotnetjq",
    "aot-dotnetjq",
    "native-jq",
)
FIXTURE_COUNTS = {
    "jq.test": 550,
    "man.test": 231,
    "onig.test": 47,
    "manonig.test": 19,
    "base64.test": 10,
    "uri.test": 20,
    "optional.test": 2,
}
MACRO_SCENARIO_IDS = tuple(f"{index:02d}" for index in range(21))


@dataclasses.dataclass(frozen=True)
class ReportSpec:
    default_relative_path: pathlib.Path
    files: tuple[str, ...]


REPORT_SPECS = {
    "fixture": ReportSpec(
        pathlib.Path("artifacts/performance/results"),
        (
            "measurements.csv",
            "results.json",
            "scenario-summary.csv",
            "summary.csv",
            "summary.md",
        ),
    ),
    "macro": ReportSpec(
        pathlib.Path("artifacts/performance/macro-results"),
        (
            "raw.csv",
            "results.json",
            "summary.csv",
            "summary.md",
        ),
    ),
}


class ReportOutputError(RuntimeError):
    """A report directory cannot be prepared or certified safely."""


@dataclasses.dataclass
class ReportLease:
    """An exclusive lease spanning report preparation through certification."""

    root: pathlib.Path
    target: pathlib.Path
    kind: str
    run_id: str
    lock_path: pathlib.Path
    fd: int
    owns_fd: bool
    released: bool = False

    def validate(self) -> None:
        if self.released:
            raise ReportOutputError("report lease has already been released")
        _validate_run_id(self.run_id)
        try:
            descriptor = os.fstat(self.fd)
            path_stat = os.lstat(self.lock_path)
        except OSError as error:
            raise ReportOutputError(
                f"report lease file is unavailable: {self.lock_path}"
            ) from error
        if not stat.S_ISREG(descriptor.st_mode) or not stat.S_ISREG(path_stat.st_mode):
            raise ReportOutputError(f"report lease is not a regular file: {self.lock_path}")
        if descriptor.st_nlink != 1 or path_stat.st_nlink != 1:
            raise ReportOutputError(f"report lease file has unsafe hard links: {self.lock_path}")
        if (descriptor.st_dev, descriptor.st_ino) != (path_stat.st_dev, path_stat.st_ino):
            raise ReportOutputError(f"report lease path changed: {self.lock_path}")
        try:
            fcntl.flock(self.fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except OSError as error:
            raise ReportOutputError(
                f"exclusive report lease is no longer held: {self.lock_path}"
            ) from error
        if _read_lease_record(self.fd, self.lock_path) != _lease_record(self):
            raise ReportOutputError(f"report lease identity changed: {self.lock_path}")

    def release(self) -> None:
        if self.released:
            return
        self.released = True
        if not self.owns_fd:
            return
        try:
            fcntl.flock(self.fd, fcntl.LOCK_UN)
        finally:
            os.close(self.fd)


def report_spec(kind: str) -> ReportSpec:
    try:
        return REPORT_SPECS[kind]
    except KeyError as error:
        raise ReportOutputError(f"unknown performance report kind: {kind!r}") from error


def _validate_run_id(run_id: str) -> None:
    try:
        parsed = uuid.UUID(run_id)
    except (ValueError, AttributeError) as error:
        raise ReportOutputError(f"invalid report run ID: {run_id!r}") from error
    if str(parsed) != run_id:
        raise ReportOutputError(f"report run ID is not canonical: {run_id!r}")


def _lease_path(root: pathlib.Path) -> pathlib.Path:
    repository_root = root.expanduser().resolve(strict=True)
    relative_parent = pathlib.Path("artifacts/performance")
    _reject_symbolic_default_components(repository_root, relative_parent)
    parent = repository_root / relative_parent
    try:
        parent.mkdir(parents=True, exist_ok=True)
    except OSError as error:
        raise ReportOutputError(f"cannot create report lease directory: {parent}") from error
    _reject_symbolic_default_components(repository_root, relative_parent)
    if parent.resolve(strict=True) != parent:
        raise ReportOutputError(f"report lease directory changed while resolving: {parent}")
    return parent / LEASE_FILENAME


def _lease_record(lease: ReportLease) -> dict[str, object]:
    return {
        "schema_version": LEASE_SCHEMA_VERSION,
        "run_id": lease.run_id,
        "kind": lease.kind,
        "target": str(lease.target),
    }


def _write_lease_record(fd: int, path: pathlib.Path, document: Mapping[str, object]) -> None:
    payload = json.dumps(document, sort_keys=True, separators=(",", ":")).encode("utf-8") + b"\n"
    try:
        os.ftruncate(fd, 0)
        os.lseek(fd, 0, os.SEEK_SET)
        view = memoryview(payload)
        while view:
            written = os.write(fd, view)
            if written <= 0:
                raise OSError("short write")
            view = view[written:]
        os.fsync(fd)
    except OSError as error:
        raise ReportOutputError(f"cannot write report lease identity: {path}") from error


def _read_lease_record(fd: int, path: pathlib.Path) -> dict[str, object]:
    try:
        size = os.fstat(fd).st_size
        payload = os.pread(fd, size, 0)
        document = json.loads(payload.decode("utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        raise ReportOutputError(f"invalid report lease identity: {path}") from error
    expected_keys = {"schema_version", "run_id", "kind", "target"}
    if not isinstance(document, dict) or set(document) != expected_keys:
        raise ReportOutputError(f"invalid report lease identity schema: {path}")
    return document


def _open_report_lease(
    requested: pathlib.Path, root: pathlib.Path, kind: str
) -> ReportLease:
    target = resolve_report_directory(requested, root, kind)
    repository_root = root.expanduser().resolve(strict=True)
    lock_path = _lease_path(repository_root)
    if lock_path.is_symlink():
        raise ReportOutputError(f"refusing symbolic report lease file: {lock_path}")
    flags = os.O_RDWR | os.O_CREAT
    if hasattr(os, "O_CLOEXEC"):
        flags |= os.O_CLOEXEC
    if hasattr(os, "O_NOFOLLOW"):
        flags |= os.O_NOFOLLOW
    try:
        fd = os.open(lock_path, flags, 0o600)
    except OSError as error:
        raise ReportOutputError(f"cannot open report lease file: {lock_path}") from error
    try:
        descriptor = os.fstat(fd)
        path_stat = os.lstat(lock_path)
        if not stat.S_ISREG(descriptor.st_mode) or not stat.S_ISREG(path_stat.st_mode):
            raise ReportOutputError(f"report lease is not a regular file: {lock_path}")
        if descriptor.st_nlink != 1 or path_stat.st_nlink != 1:
            raise ReportOutputError(f"report lease file has unsafe hard links: {lock_path}")
        if (descriptor.st_dev, descriptor.st_ino) != (path_stat.st_dev, path_stat.st_ino):
            raise ReportOutputError(f"report lease path changed while opening: {lock_path}")
        try:
            fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError as error:
            raise ReportOutputError(
                f"another performance report run holds the exclusive lease: {lock_path}"
            ) from error
        run_id = str(uuid.uuid4())
        lease = ReportLease(
            repository_root, target, kind, run_id, lock_path, fd, True
        )
        _write_lease_record(fd, lock_path, _lease_record(lease))
        lease.validate()
        return lease
    except BaseException:
        os.close(fd)
        raise


def _adopt_report_lease(
    requested: pathlib.Path,
    root: pathlib.Path,
    kind: str,
    run_id: str,
    fd_text: str,
) -> ReportLease:
    target = resolve_report_directory(requested, root, kind)
    repository_root = root.expanduser().resolve(strict=True)
    lock_path = _lease_path(repository_root)
    _validate_run_id(run_id)
    try:
        fd = int(fd_text, 10)
    except ValueError as error:
        raise ReportOutputError(f"invalid inherited report lease descriptor: {fd_text!r}") from error
    if fd < 0:
        raise ReportOutputError(f"invalid inherited report lease descriptor: {fd}")
    lease = ReportLease(repository_root, target, kind, run_id, lock_path, fd, False)
    lease.validate()
    return lease


@contextlib.contextmanager
def report_run_lease(
    requested: pathlib.Path, root: pathlib.Path, kind: str
) -> Iterator[ReportLease]:
    """Acquire or adopt the one process-wide performance-report lease."""
    run_id = os.environ.get(LEASE_ID_ENVIRONMENT)
    fd_text = os.environ.get(LEASE_FD_ENVIRONMENT)
    if (run_id is None) != (fd_text is None):
        raise ReportOutputError(
            "inherited report lease requires both run-ID and descriptor variables"
        )
    lease = (
        _open_report_lease(requested, root, kind)
        if run_id is None
        else _adopt_report_lease(requested, root, kind, run_id, fd_text)
    )
    try:
        yield lease
    finally:
        lease.release()


def run_with_report_lease(
    requested: pathlib.Path,
    root: pathlib.Path,
    kind: str,
    command: Sequence[str],
) -> int:
    if not command:
        raise ReportOutputError("report lease runner requires a command")
    # The broker owns the descriptor and stays alive until the entire child
    # pipeline exits. The child adopts the same open-file description and run ID.
    with report_run_lease(requested, root, kind) as lease:
        environment = os.environ.copy()
        environment[LEASE_ID_ENVIRONMENT] = lease.run_id
        environment[LEASE_FD_ENVIRONMENT] = str(lease.fd)
        try:
            completed = subprocess.run(
                list(command),
                env=environment,
                pass_fds=(lease.fd,),
                check=False,
            )
        except OSError as error:
            raise ReportOutputError(
                f"cannot launch report pipeline command: {command[0]}"
            ) from error
        return completed.returncode


def _absolute_unresolved(path: pathlib.Path) -> pathlib.Path:
    expanded = path.expanduser()
    return expanded if expanded.is_absolute() else pathlib.Path.cwd() / expanded


def _is_same_or_ancestor(candidate: pathlib.Path, protected: pathlib.Path) -> bool:
    return candidate == protected or candidate in protected.parents


def _reject_symbolic_default_components(
    root: pathlib.Path, relative_path: pathlib.Path
) -> None:
    current = root
    for component in relative_path.parts:
        current /= component
        if current.is_symlink():
            raise ReportOutputError(
                f"refusing symbolic default report path component: {current}"
            )


def _reject_symbolic_path_components(path: pathlib.Path) -> None:
    for component in (path, *path.parents):
        if component == pathlib.Path(component.anchor):
            continue
        if component.is_symlink():
            raise ReportOutputError(
                f"refusing report output path with symbolic component: {component}"
            )


def resolve_report_directory(
    requested: pathlib.Path, root: pathlib.Path, kind: str
) -> pathlib.Path:
    """Return a canonical safe leaf, creating only its reserved default parent."""
    spec = report_spec(kind)
    try:
        repository_root = root.expanduser().resolve(strict=True)
    except OSError as error:
        raise ReportOutputError(f"repository root is unavailable: {root}") from error
    if not repository_root.is_dir():
        raise ReportOutputError(f"repository root is not a directory: {repository_root}")

    unresolved = _absolute_unresolved(requested)
    if not unresolved.name or unresolved.name in {".", ".."}:
        raise ReportOutputError(f"refusing broad report output path: {requested}")
    _reject_symbolic_path_components(unresolved)
    try:
        target = unresolved.resolve(strict=False)
    except OSError as error:
        raise ReportOutputError(f"cannot resolve report output directory: {unresolved}") from error

    default_unresolved = repository_root / spec.default_relative_path
    default_target = default_unresolved.resolve(strict=False)
    is_default = target == default_target
    if is_default:
        _reject_symbolic_default_components(repository_root, spec.default_relative_path)
    else:
        try:
            target.relative_to(repository_root)
        except ValueError:
            pass
        else:
            raise ReportOutputError(
                "custom report output directories must be outside the repository; "
                f"refusing {target}"
            )

    protected_paths = {
        repository_root,
        pathlib.Path.cwd().resolve(),
        pathlib.Path.home().resolve(),
        pathlib.Path(tempfile.gettempdir()).resolve(),
    }
    if target.parent == target or target.parent == pathlib.Path(target.anchor):
        raise ReportOutputError(f"refusing broad report output path: {target}")
    for protected in protected_paths:
        if _is_same_or_ancestor(target, protected):
            raise ReportOutputError(
                f"report output path overlaps protected directory {protected}: {target}"
            )

    if target.is_symlink():
        raise ReportOutputError(f"refusing symbolic report output directory: {target}")
    if target.exists() and not target.is_dir():
        raise ReportOutputError(f"report output path is not a directory: {target}")
    if target.exists() and target.is_mount():
        raise ReportOutputError(f"refusing mounted report output directory: {target}")

    parent = target.parent
    if is_default:
        try:
            parent.mkdir(parents=True, exist_ok=True)
        except OSError as error:
            raise ReportOutputError(
                f"cannot create default report parent directory: {parent}"
            ) from error
    elif not parent.is_dir():
        raise ReportOutputError(
            f"custom report parent directory must already exist: {parent}"
        )
    return target


def _status_document(
    kind: str,
    state: str,
    run_id: str,
    files: Sequence[Mapping[str, object]] = (),
    validation: Mapping[str, object] | None = None,
) -> dict[str, object]:
    return {
        "schema_version": STATUS_SCHEMA_VERSION,
        "kind": kind,
        "state": state,
        "run_id": run_id,
        "expected_files": list(report_spec(kind).files),
        "files": list(files),
        "validation": None if validation is None else dict(validation),
    }


def _write_status(
    directory: pathlib.Path,
    kind: str,
    state: str,
    run_id: str,
    files: Sequence[Mapping[str, object]] = (),
    validation: Mapping[str, object] | None = None,
) -> None:
    status_path = directory / STATUS_FILENAME
    temporary = directory / f"{STATUS_FILENAME}.tmp-{run_id}"
    try:
        temporary.write_text(
            json.dumps(
                _status_document(kind, state, run_id, files, validation),
                sort_keys=True,
                indent=2,
            )
            + "\n",
            encoding="utf-8",
        )
        os.replace(temporary, status_path)
    except OSError as error:
        raise ReportOutputError(f"cannot write report status marker: {status_path}") from error


def read_report_status(
    directory: pathlib.Path, kind: str, verify_complete: bool = True
) -> dict[str, object]:
    status_path = directory / STATUS_FILENAME
    if status_path.is_symlink() or not status_path.is_file():
        raise ReportOutputError(
            f"report directory has no regular ownership/status marker: {status_path}"
        )
    try:
        document = json.loads(status_path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        raise ReportOutputError(f"invalid report status marker: {status_path}") from error
    expected_keys = {
        "schema_version",
        "kind",
        "state",
        "run_id",
        "expected_files",
        "files",
        "validation",
    }
    if not isinstance(document, dict) or set(document) != expected_keys:
        raise ReportOutputError(f"invalid report status marker schema: {status_path}")
    if document.get("schema_version") != STATUS_SCHEMA_VERSION:
        raise ReportOutputError(f"unsupported report status marker version: {status_path}")
    if document.get("kind") != kind:
        raise ReportOutputError(
            f"report status marker kind does not match {kind!r}: {status_path}"
        )
    if document.get("state") not in {"incomplete", "complete"}:
        raise ReportOutputError(f"invalid report status marker state: {status_path}")
    run_id = document.get("run_id")
    if not isinstance(run_id, str):
        raise ReportOutputError(f"invalid report status marker run ID: {status_path}")
    _validate_run_id(run_id)
    if document.get("expected_files") != list(report_spec(kind).files):
        raise ReportOutputError(f"report status marker inventory mismatch: {status_path}")
    files = document.get("files")
    validation = document.get("validation")
    if document["state"] == "incomplete":
        if files != [] or validation is not None:
            raise ReportOutputError(
                f"incomplete report marker contains completion data: {status_path}"
            )
    else:
        if not isinstance(files, list) or not isinstance(validation, dict):
            raise ReportOutputError(f"complete report marker is incomplete: {status_path}")
        if verify_complete:
            _verify_complete_report(directory, kind, files, validation)
    return document


def _reject_nested_mounts(directory: pathlib.Path) -> None:
    try:
        mounted = next((path for path in directory.rglob("*") if path.is_mount()), None)
    except OSError as error:
        raise ReportOutputError(f"cannot inspect report directory: {directory}") from error
    if mounted is not None:
        raise ReportOutputError(
            f"refusing to clean report directory containing a mount: {mounted}"
        )


def _sha256_file(path: pathlib.Path) -> str:
    digest = hashlib.sha256()
    try:
        with path.open("rb") as source:
            for block in iter(lambda: source.read(1024 * 1024), b""):
                digest.update(block)
    except OSError as error:
        raise ReportOutputError(f"cannot hash report file: {path}") from error
    return digest.hexdigest()


def _report_file_record(path: pathlib.Path) -> dict[str, object]:
    if path.is_symlink() or not path.is_file():
        raise ReportOutputError(f"report entry is not a regular file: {path}")
    return {
        "name": path.name,
        "bytes": path.stat().st_size,
        "sha256": _sha256_file(path),
    }


def _load_json_object(path: pathlib.Path, label: str) -> dict[str, object]:
    try:
        document = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        raise ReportOutputError(f"invalid {label}: {path}") from error
    if not isinstance(document, dict):
        raise ReportOutputError(f"{label} must be a JSON object: {path}")
    return document


def _read_csv_rows(
    path: pathlib.Path, expected_fields: Sequence[str]
) -> list[dict[str, str]]:
    try:
        with path.open("r", encoding="utf-8", newline="") as source:
            reader = csv.DictReader(source)
            if reader.fieldnames != list(expected_fields):
                raise ReportOutputError(
                    f"report CSV header mismatch in {path}: {reader.fieldnames!r}"
                )
            rows = list(reader)
    except (OSError, UnicodeError, csv.Error) as error:
        raise ReportOutputError(f"invalid report CSV: {path}") from error
    if any(None in row for row in rows):
        raise ReportOutputError(f"report CSV has excess columns: {path}")
    return rows


def _positive_int(value: object, label: str) -> int:
    if not isinstance(value, int) or isinstance(value, bool) or value < 1:
        raise ReportOutputError(f"{label} must be a positive integer")
    return value


def _csv_int(value: str | None, label: str) -> int:
    try:
        parsed = int(value or "", 10)
    except ValueError as error:
        raise ReportOutputError(f"{label} is not an integer: {value!r}") from error
    return parsed


def _validate_publication(
    metadata: Mapping[str, object], kind: str, scenario_count: int
) -> bool:
    publication = metadata.get("publication")
    if not isinstance(publication, dict):
        raise ReportOutputError(f"{kind} results lack publication metadata")
    publishable = publication.get("publishable")
    complete_run = publication.get("complete_run")
    if not isinstance(publishable, bool) or not isinstance(complete_run, bool):
        raise ReportOutputError(f"{kind} publication flags are invalid")
    if publishable:
        expected_count = 879 if kind == "fixture" else 21
        if not complete_run or scenario_count != expected_count:
            raise ReportOutputError(
                f"publishable {kind} report has incomplete scenario capacity"
            )
        if publication.get("attestations_required") is not True:
            raise ReportOutputError(f"publishable {kind} report lacks attestation requirement")
        attestations = publication.get("attestations")
        if not isinstance(attestations, list) or {
            item.get("deployment") for item in attestations if isinstance(item, dict)
        } != {"framework-dotnetjq", "aot-dotnetjq"}:
            raise ReportOutputError(f"publishable {kind} report lacks both build attestations")
        if len(attestations) != 2 or not all(
            isinstance(item, dict) for item in attestations
        ):
            raise ReportOutputError(f"publishable {kind} report attestation inventory is invalid")
    return publishable


def _validate_implementation_metadata(metadata: Mapping[str, object], kind: str) -> None:
    implementations = metadata.get("implementations")
    if (
        not isinstance(implementations, list)
        or not all(isinstance(item, dict) for item in implementations)
        or [item.get("name") for item in implementations]
        != list(IMPLEMENTATION_NAMES)
    ):
        raise ReportOutputError(f"{kind} result implementation inventory is invalid")
    hashes = [item.get("executable_sha256") for item in implementations]
    if any(
        not isinstance(value, str)
        or len(value) != 64
        or any(character not in "0123456789abcdef" for character in value)
        for value in hashes
    ):
        raise ReportOutputError(f"{kind} result executable identity is invalid")
    if len(set(hashes)) != len(hashes):
        raise ReportOutputError(f"{kind} result executable identities are not distinct")


def _validate_fixture_report(directory: pathlib.Path) -> dict[str, object]:
    document = _load_json_object(directory / "results.json", "fixture results JSON")
    if document.get("schema_version") != 3:
        raise ReportOutputError("fixture results schema must be 3")
    metadata = document.get("metadata")
    scenarios = document.get("scenarios")
    startup = document.get("startup")
    aggregates = document.get("aggregates")
    if not isinstance(metadata, dict) or not isinstance(scenarios, list):
        raise ReportOutputError("fixture results metadata/scenarios are invalid")
    if not isinstance(startup, dict) or not isinstance(aggregates, list):
        raise ReportOutputError("fixture results startup/aggregates are invalid")
    scenario_count = _positive_int(metadata.get("scenario_count"), "fixture scenario count")
    repetitions = _positive_int(metadata.get("repetitions"), "fixture repetitions")
    startup_repetitions = _positive_int(
        metadata.get("startup_repetitions"), "fixture startup repetitions"
    )
    if len(scenarios) != scenario_count:
        raise ReportOutputError("fixture scenario count does not match results JSON")
    publishable = _validate_publication(metadata, "fixture", scenario_count)
    _validate_implementation_metadata(metadata, "fixture")

    scenario_by_id: dict[str, dict[str, object]] = {}
    expected_measurements: dict[tuple[str, str, int], int] = {}
    fixture_names: list[str] = []
    for raw in scenarios:
        if not isinstance(raw, dict):
            raise ReportOutputError("fixture scenario entry is invalid")
        scenario_id = raw.get("id")
        fixture = raw.get("fixture")
        if not isinstance(scenario_id, str) or not isinstance(fixture, str):
            raise ReportOutputError("fixture scenario identity is invalid")
        if scenario_id in scenario_by_id:
            raise ReportOutputError(f"duplicate fixture scenario ID: {scenario_id}")
        scenario_by_id[scenario_id] = raw
        if fixture not in fixture_names:
            fixture_names.append(fixture)
        implementation_results = raw.get("implementations")
        if not isinstance(implementation_results, dict) or list(
            implementation_results
        ) != list(IMPLEMENTATION_NAMES):
            raise ReportOutputError(
                f"fixture scenario implementation inventory is invalid: {scenario_id}"
            )
        for name in IMPLEMENTATION_NAMES:
            result = implementation_results[name]
            samples = result.get("samples_ns") if isinstance(result, dict) else None
            if not isinstance(samples, list) or len(samples) != repetitions or any(
                not isinstance(value, int) or isinstance(value, bool) or value <= 0
                for value in samples
            ):
                raise ReportOutputError(
                    f"fixture sample capacity is invalid: {scenario_id}/{name}"
                )
            for repetition, elapsed in enumerate(samples, start=1):
                expected_measurements[(scenario_id, name, repetition)] = elapsed

    if publishable:
        if metadata.get("full_fixture_scenario_count") != 879:
            raise ReportOutputError("publishable fixture full capacity is not 879")
        expected_ids = [
            f"{fixture}:{index:03d}"
            for fixture, count in FIXTURE_COUNTS.items()
            for index in range(1, count + 1)
        ]
        if list(scenario_by_id) != expected_ids:
            raise ReportOutputError("publishable fixture scenario IDs are incomplete or reordered")
        fixtures = metadata.get("fixtures")
        if not isinstance(fixtures, list) or [
            (item.get("name"), item.get("cases"))
            for item in fixtures
            if isinstance(item, dict)
        ] != list(FIXTURE_COUNTS.items()):
            raise ReportOutputError("publishable fixture metadata has the wrong capacities")

    for name in IMPLEMENTATION_NAMES:
        result = startup.get(name)
        samples = result.get("samples_ns") if isinstance(result, dict) else None
        if not isinstance(samples, list) or len(samples) != startup_repetitions or any(
            not isinstance(value, int) or isinstance(value, bool) or value <= 0
            for value in samples
        ):
            raise ReportOutputError(f"fixture startup sample capacity is invalid: {name}")
        for repetition, elapsed in enumerate(samples, start=1):
            expected_measurements[("startup:null", name, repetition)] = elapsed

    measurement_fields = (
        "scenario_id",
        "fixture",
        "fixture_index",
        "source_line",
        "implementation",
        "repetition",
        "elapsed_ns",
    )
    measurement_rows = _read_csv_rows(directory / "measurements.csv", measurement_fields)
    seen_measurements: set[tuple[str, str, int]] = set()
    for row in measurement_rows:
        key = (
            row["scenario_id"],
            row["implementation"],
            _csv_int(row["repetition"], "fixture repetition"),
        )
        if key in seen_measurements or key not in expected_measurements:
            raise ReportOutputError(f"unexpected/duplicate fixture measurement: {key}")
        if _csv_int(row["elapsed_ns"], "fixture elapsed_ns") != expected_measurements[key]:
            raise ReportOutputError(f"fixture CSV/JSON sample mismatch: {key}")
        seen_measurements.add(key)
    if seen_measurements != set(expected_measurements):
        raise ReportOutputError("fixture measurement CSV is incomplete")

    scenario_summary_fields = (
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
    )
    scenario_rows = _read_csv_rows(
        directory / "scenario-summary.csv", scenario_summary_fields
    )
    expected_summaries = {
        (scenario_id, name): repetitions
        for scenario_id in scenario_by_id
        for name in IMPLEMENTATION_NAMES
    } | {
        ("startup:null", name): startup_repetitions for name in IMPLEMENTATION_NAMES
    }
    seen_summaries: set[tuple[str, str]] = set()
    for row in scenario_rows:
        key = (row["scenario_id"], row["implementation"])
        if key in seen_summaries or key not in expected_summaries:
            raise ReportOutputError(f"unexpected/duplicate fixture summary: {key}")
        if _csv_int(row["samples"], "fixture summary samples") != expected_summaries[key]:
            raise ReportOutputError(f"fixture summary sample count mismatch: {key}")
        seen_summaries.add(key)
    if seen_summaries != set(expected_summaries):
        raise ReportOutputError("fixture scenario summary CSV is incomplete")

    aggregate_fields = (
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
    )
    aggregate_rows = _read_csv_rows(directory / "summary.csv", aggregate_fields)
    aggregate_keys = [
        (str(item.get("group")), str(item.get("implementation")))
        for item in aggregates
        if isinstance(item, dict)
    ]
    csv_aggregate_keys = [(row["group"], row["implementation"]) for row in aggregate_rows]
    if (
        len(aggregate_keys) != len(aggregates)
        or len(set(aggregate_keys)) != len(aggregate_keys)
        or csv_aggregate_keys != aggregate_keys
    ):
        raise ReportOutputError("fixture aggregate JSON/CSV inventories differ")
    expected_groups = ["ALL", *fixture_names, "STARTUP"]
    if aggregate_keys != [
        (group, name) for group in expected_groups for name in IMPLEMENTATION_NAMES
    ]:
        raise ReportOutputError("fixture aggregate capacity is invalid")

    try:
        summary_text = (directory / "summary.md").read_text(encoding="utf-8")
    except (OSError, UnicodeError) as error:
        raise ReportOutputError("fixture Markdown summary is unreadable") from error
    if "# DotNetJq CLI performance comparison" not in summary_text:
        raise ReportOutputError("fixture Markdown summary is invalid")
    timed_measurements = scenario_count * len(IMPLEMENTATION_NAMES) * repetitions + (
        len(IMPLEMENTATION_NAMES) * startup_repetitions
    )
    return {
        "schema_version": 1,
        "kind": "fixture",
        "publishable": publishable,
        "scenario_count": scenario_count,
        "repetitions": repetitions,
        "startup_repetitions": startup_repetitions,
        "timed_measurements": timed_measurements,
        "scenario_summary_rows": len(scenario_rows),
        "aggregate_rows": len(aggregate_rows),
    }


def _validate_macro_report(directory: pathlib.Path) -> dict[str, object]:
    document = _load_json_object(directory / "results.json", "macro results JSON")
    if document.get("schema_version") != 3:
        raise ReportOutputError("macro results schema must be 3")
    metadata = document.get("metadata")
    scenarios = document.get("scenarios")
    aggregates = document.get("aggregates")
    if not isinstance(metadata, dict) or not isinstance(scenarios, list):
        raise ReportOutputError("macro results metadata/scenarios are invalid")
    if not isinstance(aggregates, list):
        raise ReportOutputError("macro results aggregates are invalid")
    scenario_count = _positive_int(metadata.get("scenario_count"), "macro scenario count")
    repetitions = _positive_int(metadata.get("repetitions"), "macro repetitions")
    if len(scenarios) != scenario_count:
        raise ReportOutputError("macro scenario count does not match results JSON")
    publishable = _validate_publication(metadata, "macro", scenario_count)
    _validate_implementation_metadata(metadata, "macro")

    scenario_by_id: dict[str, dict[str, object]] = {}
    expected_measurements: dict[tuple[str, str, int], int] = {}
    for raw in scenarios:
        if not isinstance(raw, dict) or not isinstance(raw.get("id"), str):
            raise ReportOutputError("macro scenario entry is invalid")
        scenario_id = raw["id"]
        if scenario_id in scenario_by_id:
            raise ReportOutputError(f"duplicate macro scenario ID: {scenario_id}")
        scenario_by_id[scenario_id] = raw
        correctness = raw.get("correctness")
        implementation_results = raw.get("implementations")
        if not isinstance(correctness, dict) or list(correctness) != list(
            IMPLEMENTATION_NAMES
        ):
            raise ReportOutputError(f"macro correctness inventory is invalid: {scenario_id}")
        fingerprints = [
            json.dumps(correctness[name], sort_keys=True, separators=(",", ":"))
            for name in IMPLEMENTATION_NAMES
        ]
        if len(set(fingerprints)) != 1:
            raise ReportOutputError(f"macro correctness fingerprints differ: {scenario_id}")
        if not isinstance(implementation_results, dict) or list(
            implementation_results
        ) != list(IMPLEMENTATION_NAMES):
            raise ReportOutputError(
                f"macro scenario implementation inventory is invalid: {scenario_id}"
            )
        for name in IMPLEMENTATION_NAMES:
            result = implementation_results[name]
            samples = result.get("samples_ns") if isinstance(result, dict) else None
            if not isinstance(samples, list) or len(samples) != repetitions or any(
                not isinstance(value, int) or isinstance(value, bool) or value <= 0
                for value in samples
            ):
                raise ReportOutputError(
                    f"macro sample capacity is invalid: {scenario_id}/{name}"
                )
            for repetition, elapsed in enumerate(samples, start=1):
                expected_measurements[(scenario_id, name, repetition)] = elapsed
    if publishable:
        if metadata.get("full_scenario_count") != 21:
            raise ReportOutputError("publishable macro full capacity is not 21")
        if metadata.get("datasets_regenerated") is not True:
            raise ReportOutputError("publishable macro datasets were not freshly regenerated")
        if tuple(scenario_by_id) != MACRO_SCENARIO_IDS:
            raise ReportOutputError("publishable macro scenario IDs are incomplete or reordered")

    raw_fields = (
        "scenario_id",
        "scenario_name",
        "dataset",
        "implementation",
        "repetition",
        "scenario_position",
        "implementation_position",
        "elapsed_ns",
    )
    raw_rows = _read_csv_rows(directory / "raw.csv", raw_fields)
    seen_measurements: set[tuple[str, str, int]] = set()
    scenario_positions: dict[tuple[int, int], str] = {}
    implementation_positions: dict[tuple[str, int], set[int]] = {}
    for row in raw_rows:
        repetition = _csv_int(row["repetition"], "macro repetition")
        key = (row["scenario_id"], row["implementation"], repetition)
        if key in seen_measurements or key not in expected_measurements:
            raise ReportOutputError(f"unexpected/duplicate macro measurement: {key}")
        if _csv_int(row["elapsed_ns"], "macro elapsed_ns") != expected_measurements[key]:
            raise ReportOutputError(f"macro CSV/JSON sample mismatch: {key}")
        scenario_position = _csv_int(row["scenario_position"], "macro scenario position")
        implementation_position = _csv_int(
            row["implementation_position"], "macro implementation position"
        )
        position_key = (repetition, scenario_position)
        previous = scenario_positions.setdefault(position_key, row["scenario_id"])
        if previous != row["scenario_id"]:
            raise ReportOutputError("macro scenario position maps to multiple scenarios")
        implementation_positions.setdefault((row["scenario_id"], repetition), set()).add(
            implementation_position
        )
        seen_measurements.add(key)
    if seen_measurements != set(expected_measurements):
        raise ReportOutputError("macro raw measurement CSV is incomplete")
    for repetition in range(1, repetitions + 1):
        if {
            position for rep, position in scenario_positions if rep == repetition
        } != set(range(1, scenario_count + 1)):
            raise ReportOutputError("macro scenario positions are incomplete")
    if any(
        positions != set(range(1, len(IMPLEMENTATION_NAMES) + 1))
        for positions in implementation_positions.values()
    ):
        raise ReportOutputError("macro implementation positions are incomplete")

    summary_fields = (
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
    )
    summary_rows = _read_csv_rows(directory / "summary.csv", summary_fields)
    scenario_keys = [
        ("scenario", scenario_id, name)
        for scenario_id in scenario_by_id
        for name in IMPLEMENTATION_NAMES
    ]
    aggregate_keys = [
        (str(item.get("group")), str(item.get("scenario_id")), str(item.get("implementation")))
        for item in aggregates
        if isinstance(item, dict)
    ]
    csv_keys = [
        (row["group"], row["scenario_id"], row["implementation"])
        for row in summary_rows
    ]
    if len(aggregate_keys) != len(aggregates) or csv_keys != [
        *scenario_keys,
        *aggregate_keys,
    ]:
        raise ReportOutputError("macro summary JSON/CSV inventories differ")
    expected_groups = []
    if any(scenario_id != "00" for scenario_id in scenario_by_id):
        expected_groups.append("PROCESSING_ONLY")
    if "00" in scenario_by_id:
        expected_groups.append("WITH_STARTUP")
    if aggregate_keys != [
        (group, group, name) for group in expected_groups for name in IMPLEMENTATION_NAMES
    ]:
        raise ReportOutputError("macro aggregate capacity is invalid")
    for row in summary_rows[: len(scenario_keys)]:
        if _csv_int(row["samples"], "macro summary samples") != repetitions:
            raise ReportOutputError("macro scenario summary sample count mismatch")

    try:
        summary_text = (directory / "summary.md").read_text(encoding="utf-8")
    except (OSError, UnicodeError) as error:
        raise ReportOutputError("macro Markdown summary is unreadable") from error
    if "# DotNetJq macro performance comparison" not in summary_text:
        raise ReportOutputError("macro Markdown summary is invalid")
    return {
        "schema_version": 1,
        "kind": "macro",
        "publishable": publishable,
        "scenario_count": scenario_count,
        "repetitions": repetitions,
        "timed_measurements": len(expected_measurements),
        "summary_rows": len(summary_rows),
        "aggregate_rows": len(aggregate_keys),
    }


def validate_report_content(directory: pathlib.Path, kind: str) -> dict[str, object]:
    if kind == "fixture":
        return _validate_fixture_report(directory)
    if kind == "macro":
        return _validate_macro_report(directory)
    raise ReportOutputError(f"unknown report kind: {kind!r}")


def _verify_complete_report(
    directory: pathlib.Path,
    kind: str,
    recorded_files: Sequence[object],
    recorded_validation: Mapping[str, object],
) -> None:
    expected_names = list(report_spec(kind).files)
    expected_entries = {STATUS_FILENAME, *expected_names}
    try:
        actual_entries = {entry.name for entry in directory.iterdir()}
    except OSError as error:
        raise ReportOutputError(f"cannot inspect complete report: {directory}") from error
    if actual_entries != expected_entries:
        raise ReportOutputError(f"complete report inventory changed: {directory}")
    expected_keys = {"name", "bytes", "sha256"}
    if len(recorded_files) != len(expected_names):
        raise ReportOutputError(f"complete report file inventory is incomplete: {directory}")
    current_records: list[dict[str, object]] = []
    for expected_name, raw in zip(expected_names, recorded_files, strict=True):
        if not isinstance(raw, dict) or set(raw) != expected_keys:
            raise ReportOutputError(f"invalid complete report file record: {directory}")
        if raw.get("name") != expected_name:
            raise ReportOutputError(f"complete report file order/name mismatch: {directory}")
        current_records.append(_report_file_record(directory / expected_name))
    if current_records != list(recorded_files):
        raise ReportOutputError(f"complete report files changed after certification: {directory}")
    current_validation = validate_report_content(directory, kind)
    if current_validation != dict(recorded_validation):
        raise ReportOutputError(f"complete report validation changed: {directory}")


def prepare_report_directory(
    requested: pathlib.Path,
    root: pathlib.Path,
    kind: str,
    lease: ReportLease,
) -> pathlib.Path:
    """Clean one owned/specified report leaf and mark the new run incomplete."""
    target = resolve_report_directory(requested, root, kind)
    lease.validate()
    if lease.root != root.expanduser().resolve(strict=True):
        raise ReportOutputError("report lease belongs to a different repository")
    if lease.target != target or lease.kind != kind:
        raise ReportOutputError("report lease does not match the requested output")
    spec = report_spec(kind)
    default_target = (root.expanduser().resolve() / spec.default_relative_path).resolve(
        strict=False
    )
    is_default = target == default_target
    quarantined: pathlib.Path | None = None

    if target.exists():
        entries = list(target.iterdir())
        if entries == [target / STATUS_FILENAME] or {
            entry.name for entry in entries
        } == {STATUS_FILENAME}:
            try:
                status_document = read_report_status(target, kind)
            except ReportOutputError:
                status_document = None
            if (
                status_document is not None
                and status_document["state"] == "incomplete"
                and status_document["run_id"] == lease.run_id
            ):
                return target
        if entries and not is_default:
            read_report_status(target, kind)
        _reject_nested_mounts(target)
        try:
            quarantined = target.with_name(
                f".{target.name}.previous-{uuid.uuid4().hex}"
            )
            target.rename(quarantined)
        except OSError as error:
            raise ReportOutputError(
                f"cannot atomically quarantine old report directory: {target}"
            ) from error
    try:
        target.mkdir()
    except OSError as error:
        recovery = "" if quarantined is None else f"; old report retained at {quarantined}"
        raise ReportOutputError(
            f"cannot create report output directory: {target}{recovery}"
        ) from error
    try:
        _write_status(target, kind, "incomplete", lease.run_id)
    except ReportOutputError as error:
        recovery = "" if quarantined is None else f"; old report retained at {quarantined}"
        raise ReportOutputError(f"{error}{recovery}") from error
    if quarantined is not None:
        try:
            shutil.rmtree(quarantined)
        except OSError as error:
            raise ReportOutputError(
                "new report directory is safely marked incomplete, but the quarantined "
                f"old report could not be removed: {quarantined}"
            ) from error
    return target


def require_prepared_report_directory(
    directory: pathlib.Path, kind: str, lease: ReportLease
) -> None:
    lease.validate()
    if lease.target != directory.resolve(strict=False) or lease.kind != kind:
        raise ReportOutputError("report lease does not match the prepared output")
    if directory.is_symlink() or not directory.is_dir():
        raise ReportOutputError(f"report output directory is not prepared: {directory}")
    status = read_report_status(directory, kind)
    if status["state"] != "incomplete":
        raise ReportOutputError(f"report output directory is not incomplete: {directory}")
    if status["run_id"] != lease.run_id:
        raise ReportOutputError(f"report output belongs to another run: {directory}")
    actual = {entry.name for entry in directory.iterdir()}
    if actual != {STATUS_FILENAME}:
        raise ReportOutputError(
            f"prepared report directory already contains unexpected entries: {directory}"
        )


def complete_report_directory(
    directory: pathlib.Path, kind: str, lease: ReportLease
) -> None:
    """Certify the exact report inventory, then atomically mark it complete."""
    lease.validate()
    if lease.target != directory.resolve(strict=False) or lease.kind != kind:
        raise ReportOutputError("report lease does not match the completed output")
    status = read_report_status(directory, kind)
    if status["state"] != "incomplete":
        raise ReportOutputError(f"report output directory is not incomplete: {directory}")
    if status["run_id"] != lease.run_id:
        raise ReportOutputError(f"report output belongs to another run: {directory}")
    expected = set(report_spec(kind).files)
    actual_entries = {entry.name: entry for entry in directory.iterdir()}
    actual_reports = set(actual_entries) - {STATUS_FILENAME}
    if actual_reports != expected:
        missing = sorted(expected - actual_reports)
        unexpected = sorted(actual_reports - expected)
        raise ReportOutputError(
            f"report inventory mismatch in {directory}; missing={missing}, "
            f"unexpected={unexpected}"
        )
    for name in sorted(expected):
        path = actual_entries[name]
        if path.is_symlink() or not path.is_file():
            raise ReportOutputError(f"report entry is not a regular file: {path}")
    validation = validate_report_content(directory, kind)
    records = [_report_file_record(directory / name) for name in report_spec(kind).files]
    _write_status(
        directory,
        kind,
        "complete",
        lease.run_id,
        records,
        validation,
    )
    read_report_status(directory, kind, verify_complete=True)


def parse_arguments(argv: Sequence[str]) -> argparse.Namespace:
    # argparse treats a bare ``--`` immediately after an optional argument with
    # nargs=REMAINDER as its own end-of-options marker, leaving the command
    # unparsed.  Split the runner command first so the shell-facing
    # ``--run-command -- command ...`` contract remains unambiguous even when
    # the child command has options of its own.
    option_arguments = list(argv)
    run_command: list[str] | None = None
    try:
        command_index = option_arguments.index("--run-command")
    except ValueError:
        pass
    else:
        run_command = option_arguments[command_index + 1 :]
        option_arguments = option_arguments[:command_index]

    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", required=True)
    parser.add_argument("--kind", required=True, choices=tuple(REPORT_SPECS))
    parser.add_argument("--output", required=True)
    arguments = parser.parse_args(option_arguments)
    arguments.run_command = run_command
    return arguments


def main(argv: Sequence[str]) -> int:
    arguments = parse_arguments(argv)
    requested = pathlib.Path(arguments.output)
    root = pathlib.Path(arguments.root)
    if arguments.run_command is not None:
        command = list(arguments.run_command)
        if command[:1] == ["--"]:
            command = command[1:]
        return run_with_report_lease(requested, root, arguments.kind, command)
    with report_run_lease(requested, root, arguments.kind) as lease:
        target = prepare_report_directory(requested, root, arguments.kind, lease)
        print(
            f"Prepared incomplete {arguments.kind} report directory: {target} "
            f"(run {lease.run_id})"
        )
        return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main(sys.argv[1:]))
    except ReportOutputError as error:
        print(f"performance report preparation failed: {error}", file=sys.stderr)
        raise SystemExit(1) from error
