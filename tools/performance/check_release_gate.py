#!/usr/bin/env python3
"""Reject a release with incomplete or materially regressed CLI evidence."""

from __future__ import annotations

import argparse
import json
import math
from pathlib import Path
from typing import Any

PINNED_JQ_COMMIT = "34f7186b86743a083a589741b6cea95293524108"
PINNED_ORACLE_SHA256 = (
    "b1c22172dd303f3be49e935aa56aa48a8b7a46e0bc838b4997d3bb451495870f"
)


class GateError(RuntimeError):
    """A performance report cannot authorize release publication."""


def load_object(path: Path) -> dict[str, Any]:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise GateError(f"cannot read valid JSON from {path}: {error}") from error
    if not isinstance(value, dict):
        raise GateError(f"expected a JSON object in {path}")
    return value


def finite_positive(value: Any, label: str) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise GateError(f"{label} is not numeric")
    result = float(value)
    if not math.isfinite(result) or result <= 0:
        raise GateError(f"{label} must be finite and positive")
    return result


def require_metadata(report: dict[str, Any], scenarios: int, label: str) -> None:
    if report.get("schema_version") != 3:
        raise GateError(f"{label} report schema must be 3")
    metadata = report.get("metadata")
    if not isinstance(metadata, dict):
        raise GateError(f"{label} metadata is missing")
    if metadata.get("scenario_count") != scenarios:
        raise GateError(
            f"{label} report has {metadata.get('scenario_count')!r} scenarios; "
            f"expected {scenarios}"
        )
    scenario_rows = report.get("scenarios")
    if not isinstance(scenario_rows, list) or len(scenario_rows) != scenarios:
        raise GateError(f"{label} report must contain exactly {scenarios} scenario rows")
    repetitions = metadata.get("repetitions")
    if isinstance(repetitions, bool) or not isinstance(repetitions, int) or repetitions < 3:
        raise GateError(f"{label} report requires at least three repetitions")
    if metadata.get("pinned_jq_commit") != PINNED_JQ_COMMIT:
        raise GateError(f"{label} report is not bound to the pinned jq commit")
    if metadata.get("pinned_oracle_sha256") != PINNED_ORACLE_SHA256:
        raise GateError(f"{label} report is not bound to the pinned jq oracle")
    publication = metadata.get("publication")
    if not isinstance(publication, dict):
        raise GateError(f"{label} publication evidence is missing")
    required_publication = {
        "mode": "publishable",
        "publishable": True,
        "complete_run": True,
        "attestations_required": True,
    }
    for field, expected in required_publication.items():
        if publication.get(field) != expected:
            raise GateError(
                f"{label} publication field {field} must be {expected!r}"
            )
    attestations = publication.get("attestations")
    if not isinstance(attestations, list):
        raise GateError(f"{label} build attestations are missing")
    deployments = {
        item.get("deployment") for item in attestations if isinstance(item, dict)
    }
    if deployments != {"framework-dotnetjq", "aot-dotnetjq"}:
        raise GateError(f"{label} build attestations have an unexpected inventory")


def keyed_aggregate(
    report: dict[str, Any], group: str, implementation: str
) -> dict[str, Any]:
    aggregates = report.get("aggregates")
    if not isinstance(aggregates, list):
        raise GateError("report aggregates are missing")
    matches = [
        item
        for item in aggregates
        if isinstance(item, dict)
        and item.get("group") == group
        and item.get("implementation") == implementation
    ]
    if len(matches) != 1:
        raise GateError(
            f"expected one {group}/{implementation} aggregate; found {len(matches)}"
        )
    return matches[0]


def maximum(
    thresholds: dict[str, Any], metric: str, implementation: str
) -> float:
    try:
        value = thresholds["maximum_release_ratios"][metric][implementation]
    except (KeyError, TypeError) as error:
        raise GateError(f"threshold is missing for {metric}/{implementation}") from error
    return finite_positive(value, f"threshold {metric}/{implementation}")


def check_ratio(
    actual: float, limit: float, metric: str, implementation: str
) -> None:
    print(
        f"{metric} {implementation}: actual={actual:.6f}x "
        f"maximum={limit:.6f}x"
    )
    if actual > limit:
        raise GateError(
            f"material performance regression: {metric}/{implementation} "
            f"is {actual:.6f}x native, above {limit:.6f}x"
        )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--fixture-results", type=Path, required=True)
    parser.add_argument("--macro-results", type=Path, required=True)
    parser.add_argument("--thresholds", type=Path, required=True)
    args = parser.parse_args()

    fixture = load_object(args.fixture_results)
    macro = load_object(args.macro_results)
    thresholds = load_object(args.thresholds)
    if thresholds.get("schema_version") != 1:
        raise GateError("release threshold schema must be 1")
    require_metadata(fixture, 879, "fixture")
    require_metadata(macro, 21, "macro")

    implementations = ("aot-dotnetjq", "framework-dotnetjq")
    for implementation in implementations:
        aggregate = keyed_aggregate(fixture, "ALL", implementation)
        fixture_ratio = finite_positive(
            aggregate.get("mean_vs_native"),
            f"fixture mean ratio for {implementation}",
        )
        check_ratio(
            fixture_ratio,
            maximum(thresholds, "fixture_mean_vs_native", implementation),
            "fixture_mean_vs_native",
            implementation,
        )

    startup = fixture.get("startup")
    if not isinstance(startup, dict):
        raise GateError("fixture startup evidence is missing")
    try:
        native_startup = finite_positive(
            startup["native-jq"]["statistics"]["median_ms"],
            "native startup median",
        )
    except (KeyError, TypeError) as error:
        raise GateError("native startup median is missing") from error
    for implementation in implementations:
        try:
            implementation_startup = finite_positive(
                startup[implementation]["statistics"]["median_ms"],
                f"startup median for {implementation}",
            )
        except (KeyError, TypeError) as error:
            raise GateError(f"startup median is missing for {implementation}") from error
        check_ratio(
            implementation_startup / native_startup,
            maximum(thresholds, "startup_median_vs_native", implementation),
            "startup_median_vs_native",
            implementation,
        )

    for implementation in implementations:
        aggregate = keyed_aggregate(macro, "PROCESSING_ONLY", implementation)
        macro_ratio = finite_positive(
            aggregate.get("geomean_median_vs_native"),
            f"macro processing ratio for {implementation}",
        )
        check_ratio(
            macro_ratio,
            maximum(
                thresholds,
                "macro_processing_geomean_vs_native",
                implementation,
            ),
            "macro_processing_geomean_vs_native",
            implementation,
        )

    print("release performance gate passed (879 fixtures; 21 macro scenarios)")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except GateError as error:
        print(f"release performance gate failed: {error}", file=__import__("sys").stderr)
        raise SystemExit(1)
