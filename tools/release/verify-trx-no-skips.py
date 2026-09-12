#!/usr/bin/env python3
"""Reject a TRX test result unless every discovered test passed and none skipped."""

from __future__ import annotations

import argparse
import sys
import xml.etree.ElementTree as ET
from pathlib import Path


ZERO_COUNTERS = (
    "failed",
    "error",
    "timeout",
    "aborted",
    "inconclusive",
    "passedButRunAborted",
    "notRunnable",
    "notExecuted",
    "disconnected",
    "warning",
    "inProgress",
    "pending",
)


class VerificationError(RuntimeError):
    """The TRX is missing evidence or records a non-passing outcome."""


def local_name(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def counter(counters: ET.Element, name: str, *, required: bool = False) -> int:
    raw = counters.get(name)
    if raw is None:
        if required:
            raise VerificationError(f"Counters is missing required '{name}' attribute")
        return 0

    try:
        value = int(raw)
    except ValueError as exception:
        raise VerificationError(f"Counters.{name} is not an integer: {raw!r}") from exception
    if value < 0:
        raise VerificationError(f"Counters.{name} is negative: {value}")
    return value


def verify(path: Path) -> int:
    try:
        resolved = path.resolve(strict=True)
    except (OSError, RuntimeError) as exception:
        raise VerificationError(f"cannot resolve result file: {exception}") from exception
    if not resolved.is_file():
        raise VerificationError("result path is not a regular file")

    try:
        root = ET.parse(resolved).getroot()
    except (ET.ParseError, OSError) as exception:
        raise VerificationError(f"cannot parse TRX XML: {exception}") from exception
    if local_name(root.tag) != "TestRun":
        raise VerificationError(f"root element is {local_name(root.tag)!r}, not 'TestRun'")

    counters_elements = [
        element for element in root.iter() if local_name(element.tag) == "Counters"
    ]
    if len(counters_elements) != 1:
        raise VerificationError(
            f"expected exactly one ResultSummary/Counters element, found {len(counters_elements)}"
        )
    counters = counters_elements[0]

    total = counter(counters, "total", required=True)
    executed = counter(counters, "executed", required=True)
    passed = counter(counters, "passed", required=True)
    if total == 0:
        raise VerificationError("TRX contains zero tests")

    nonzero = {name: counter(counters, name) for name in ZERO_COUNTERS}
    nonzero = {name: value for name, value in nonzero.items() if value != 0}
    if nonzero:
        detail = ", ".join(f"{name}={value}" for name, value in nonzero.items())
        raise VerificationError(f"non-passing counters are present: {detail}")
    if executed != total:
        raise VerificationError(f"executed={executed}, but total={total}")
    if passed != total:
        raise VerificationError(f"passed={passed}, but total={total}")

    results = [
        element for element in root.iter() if local_name(element.tag) == "UnitTestResult"
    ]
    if len(results) != total:
        raise VerificationError(
            f"found {len(results)} UnitTestResult records, but Counters.total={total}"
        )
    outcomes: dict[str, int] = {}
    for result in results:
        outcome = result.get("outcome", "<missing>")
        outcomes[outcome] = outcomes.get(outcome, 0) + 1
    if outcomes != {"Passed": total}:
        detail = ", ".join(f"{name}={value}" for name, value in sorted(outcomes.items()))
        raise VerificationError(f"UnitTestResult outcomes are not all Passed: {detail}")

    print(f"TRX verified: {resolved}: total={total}, passed={passed}, skipped=0")
    return total


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Require every test in one or more TRX files to pass with zero skips."
    )
    parser.add_argument("trx", nargs="+", type=Path, help="TRX result file to verify")
    arguments = parser.parse_args()

    failures: list[str] = []
    for path in arguments.trx:
        try:
            verify(path)
        except VerificationError as exception:
            failures.append(f"{path}: {exception}")

    if failures:
        for failure in failures:
            print(f"TRX verification failed: {failure}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
