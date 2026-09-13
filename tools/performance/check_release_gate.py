#!/usr/bin/env python3
"""Reject a release with incomplete or materially regressed CLI evidence."""

from __future__ import annotations

import argparse
import json
import math
import re
import statistics
from pathlib import Path
from typing import Any

import paired_baseline
import report_output

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
        value = thresholds["maximum_candidate_to_baseline_ratios"][metric][implementation]
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
            f"is {actual:.6f}x the same-runner baseline, above {limit:.6f}x"
        )


def runtime_toolchain_identity(
    deployment: str, runtime: Any, label: str
) -> dict[str, Any]:
    if not isinstance(runtime, dict):
        raise GateError(f"{label} {deployment} runtime identity is missing")
    if deployment == "framework-dotnetjq":
        if runtime.get("kind") != "CoreCLR":
            raise GateError(f"{label} framework runtime is not CoreCLR")
        version = runtime.get("version")
        manifest = runtime.get("manifest_sha256")
        if not isinstance(version, str) or not version:
            raise GateError(f"{label} CoreCLR version is missing")
        if re.fullmatch(r"[0-9a-f]{64}", str(manifest)) is None:
            raise GateError(f"{label} CoreCLR manifest identity is invalid")
        return {"kind": "CoreCLR", "version": version, "manifest_sha256": manifest}
    if deployment != "aot-dotnetjq":
        raise GateError(f"unexpected runtime deployment: {deployment}")
    if runtime.get("kind") != "NativeAOT":
        raise GateError(f"{label} AOT runtime is not NativeAOT")
    target = runtime.get("target")
    release_archive = target == "net10.0/linux-x64 release archive"
    if target not in ("net10.0/linux-x64", "net10.0/linux-x64 release archive"):
        raise GateError(f"{label} NativeAOT target identity is invalid")
    packages_sha256 = runtime.get("packages_sha256")
    if re.fullmatch(r"[0-9a-f]{64}", str(packages_sha256)) is None:
        raise GateError(f"{label} NativeAOT package-set identity is invalid")
    packages = runtime.get("packages")
    expected_ids = list(paired_baseline.provenance.NATIVE_AOT_PACKAGE_IDS)
    if (
        not isinstance(packages, list)
        or len(packages) != len(expected_ids)
        or any(not isinstance(item, dict) for item in packages)
        or [item.get("id") for item in packages] != expected_ids
        or any(
            not isinstance(item.get("version"), str) or not item["version"]
            for item in packages
        )
    ):
        raise GateError(f"{label} NativeAOT package inventory is invalid")
    if release_archive:
        for field in ("archive_sha256", "member_sha256", "project_assets_sha256"):
            if re.fullmatch(r"[0-9a-f]{64}", str(runtime.get(field))) is None:
                raise GateError(f"{label} NativeAOT release {field} is invalid")
        if any(
            re.fullmatch(r"[0-9a-f]{64}", str(item.get("payload_sha256"))) is None
            for item in packages
        ):
            raise GateError(f"{label} NativeAOT release package payload identity is invalid")
    return {
        "kind": "NativeAOT",
        "target": "net10.0/linux-x64",
        "packages_sha256": packages_sha256,
        "packages": [{"id": item["id"], "version": item["version"]} for item in packages],
    }


def require_pair(fixture: dict, macro: dict, thresholds: dict, policy_sha256: str) -> None:
    if thresholds.get("schema_version") != 2:
        raise GateError("same-runner release threshold schema must be 2")
    require_metadata(fixture, 879, "fixture")
    require_metadata(macro, 21, "macro")
    baseline = fixture["metadata"].get("paired_baseline")
    if not isinstance(baseline, dict) or baseline != macro["metadata"].get("paired_baseline"):
        raise GateError("fixture/macro reports must share the same pinned baseline evidence")
    for report in (fixture, macro):
        if len(report_output.implementation_names(report["metadata"])) != 5:
            raise GateError("same-process reports must contain all five subjects")
        if baseline.get("commit") != thresholds["baseline"]["commit"]:
            raise GateError("report uses the wrong baseline source commit")
        if baseline.get("policy_sha256") != policy_sha256:
            raise GateError("report uses a different performance policy")
        if baseline.get("host_session") != paired_baseline.host_session():
            raise GateError("reports were not measured in this host/job session")
        candidates = {
            item["deployment"]: item.get("runtime")
            for item in report["metadata"]["publication"]["attestations"]
        }
        expected_runtimes = {
            item["deployment"]: item.get("runtime")
            for item in baseline["attestations"]
        }
        if set(candidates) != set(paired_baseline.DEPLOYMENTS):
            raise GateError("candidate runtime inventory is incomplete")
        for deployment in paired_baseline.DEPLOYMENTS:
            expected = runtime_toolchain_identity(
                deployment, expected_runtimes.get(deployment), "baseline"
            )
            actual = runtime_toolchain_identity(
                deployment, candidates.get(deployment), "candidate"
            )
            if actual != expected:
                raise GateError("baseline/candidate runtime identities differ")
    for field in ("source_identity", "toolchain_identity", "framework_runtime"):
        if fixture["metadata"].get(field) != macro["metadata"].get(field):
            raise GateError(f"fixture/macro {field} identity differs")
    # The two report formats have differently named size fields. Compare the
    # exact shared executable/payload identities, not those display fields.
    identity_fields = ("name", "version", "command", "executable_path", "executable_sha256",
                       "artifact_manifest_sha256", "build_configuration")
    identities = [[{key: item.get(key) for key in identity_fields}
                   for item in report["metadata"]["implementations"]] for report in (fixture, macro)]
    if identities[0] != identities[1]:
        raise GateError("fixture/macro implementation identities differ")
    # Removed parent variables intentionally never reach the timed children;
    # run.sh removes some earlier than the direct macro driver does.
    for field in ("set", "preserved_runtime_roots", "inherited_path"):
        if fixture["metadata"]["environment"].get(field) != macro["metadata"]["environment"].get(field):
            raise GateError("fixture/macro effective environments differ")
    if fixture["metadata"].get("startup_repetitions", 0) < 300:
        raise GateError("release startup requires at least 300 samples per subject")


def raw_samples(row: dict, count: int, label: str) -> list[int]:
    values = row.get("samples_ns")
    if (not isinstance(values, list) or len(values) != count
            or any(type(value) is not int or value <= 0 for value in values)):
        raise GateError(f"invalid raw sample inventory: {label}")
    return values


def verify_summary(actual: float, recorded: Any, label: str) -> None:
    value = finite_positive(recorded, label)
    if not math.isclose(actual, value, rel_tol=1e-12, abs_tol=1e-12):
        raise GateError(f"summary does not match raw measurements: {label}")


def measured_metrics(fixture: dict, macro: dict) -> dict[str, dict[str, float]]:
    metrics = {name: {} for name in ("fixture_mean", "startup_median", "macro_processing_geomean")}
    names = report_output.implementation_names(fixture["metadata"])
    for name in names:
        samples = [value for scenario in fixture["scenarios"] for value in raw_samples(
            scenario["implementations"][name], fixture["metadata"]["repetitions"], f"{scenario['id']}/{name}")]
        metrics["fixture_mean"][name] = statistics.fmean(samples)
        startup = raw_samples(fixture["startup"][name], fixture["metadata"]["startup_repetitions"], f"startup/{name}")
        metrics["startup_median"][name] = statistics.median(startup)
        verify_summary(metrics["startup_median"][name] / 1e6,
                       fixture["startup"][name]["statistics"]["median_ms"], f"startup/{name}")
        medians = [statistics.median(raw_samples(scenario["implementations"][name],
                    macro["metadata"]["repetitions"], f"macro/{scenario['id']}/{name}"))
                   for scenario in macro["scenarios"] if scenario["id"] != "00"]
        metrics["macro_processing_geomean"][name] = math.exp(statistics.fmean(math.log(x) for x in medians))
    for name in names:
        verify_summary(metrics["fixture_mean"][name] / metrics["fixture_mean"]["native-jq"],
                       keyed_aggregate(fixture, "ALL", name)["mean_vs_native"], f"fixture/{name}")
        verify_summary(metrics["macro_processing_geomean"][name] / metrics["macro_processing_geomean"]["native-jq"],
                       keyed_aggregate(macro, "PROCESSING_ONLY", name)["geomean_median_vs_native"], f"macro/{name}")
    return metrics


def evaluate(fixture: dict, macro: dict, thresholds: dict, policy_sha256: str) -> None:
    require_pair(fixture, macro, thresholds, policy_sha256)
    metrics = measured_metrics(fixture, macro)
    failures = []
    for metric, values in metrics.items():
        for implementation in ("aot-dotnetjq", "framework-dotnetjq"):
            actual = values[implementation] / values[f"baseline-{implementation}"]
            limit = maximum(thresholds, metric, implementation)
            print(f"{metric} {implementation}: candidate/baseline={actual:.6f}x "
                  f"maximum={limit:.6f}x; candidate/native={values[implementation]/values['native-jq']:.6f}x; "
                  f"baseline/native={values[f'baseline-{implementation}']/values['native-jq']:.6f}x")
            # Geometric means use log/exp; allow only floating-point roundoff
            # at the exact boundary, not a statistical or performance margin.
            if actual > limit and not math.isclose(actual, limit, rel_tol=1e-12, abs_tol=0):
                failures.append(f"{metric}/{implementation}: {actual:.6f}x > {limit:.6f}x baseline")
    if failures:
        raise GateError("material performance regression: " + "; ".join(failures))
    print("same-runner release performance gate passed (879 fixtures; 21 macro scenarios; five interleaved subjects)")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--fixture-results", type=Path, required=True)
    parser.add_argument("--macro-results", type=Path, required=True)
    parser.add_argument("--thresholds", type=Path, required=True)
    args = parser.parse_args()
    for path, kind in ((args.fixture_results, "fixture"), (args.macro_results, "macro")):
        if path.name != "results.json" or report_output.read_report_status(path.parent, kind)["state"] != "complete":
            raise GateError(f"{kind} report is not a certified complete report")
    evaluate(load_object(args.fixture_results), load_object(args.macro_results),
             load_object(args.thresholds), paired_baseline.provenance.sha256_file(args.thresholds))
    return 0



if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except GateError as error:
        print(f"release performance gate failed: {error}", file=__import__("sys").stderr)
        raise SystemExit(1)
