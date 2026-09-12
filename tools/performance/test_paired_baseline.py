#!/usr/bin/env python3
"""Deterministic provenance, paired-inventory, and slowdown gate fault tests."""

from __future__ import annotations

import copy
import contextlib
import io
from pathlib import Path
import tempfile
import unittest
from unittest import mock

import benchmark
import macro_benchmark as macro_driver
import check_release_gate as gate
import paired_baseline as paired
import report_output

POLICY = paired.load_policy()
COMMIT = POLICY["baseline"]["commit"]
HASH = "a" * 64
HOST = {"boot_sha256": "b" * 64, "github_run_id": "123", "github_run_attempt": "1", "github_job": "performance", "affinity": [0, 1, 2, 3]}
NAMES = (*report_output.IMPLEMENTATION_NAMES, *paired.BASELINE_NAMES)


def attestation(name, release=False):
    if name == "framework-dotnetjq":
        runtime = {"kind": "CoreCLR", "version": "10.0.12", "manifest_sha256": HASH}
    else:
        packages = [
            {"id": package_id, "version": "10.0.11"}
            for package_id in paired.provenance.NATIVE_AOT_PACKAGE_IDS
        ]
        runtime = {
            "kind": "NativeAOT",
            "target": "net10.0/linux-x64",
            "packages_sha256": HASH,
            "packages": packages,
        }
        if release:
            runtime.update(
                target="net10.0/linux-x64 release archive",
                archive_sha256=HASH,
                member_sha256=HASH,
                project_assets_sha256=HASH,
            )
            for package in packages:
                package["payload_sha256"] = HASH
    return {"deployment": name, "runtime": runtime}


def metadata(count):
    baseline_proof = [attestation(name) for name in paired.DEPLOYMENTS]
    candidate_proof = [attestation(name, release=True) for name in paired.DEPLOYMENTS]
    return {
        "scenario_count": count, "repetitions": 3, "startup_repetitions": 300,
        "pinned_jq_commit": gate.PINNED_JQ_COMMIT,
        "pinned_oracle_sha256": gate.PINNED_ORACLE_SHA256,
        "publication": {"mode": "publishable", "publishable": True,
                        "complete_run": True, "attestations_required": True,
                        "attestations": candidate_proof},
        "paired_baseline": {"mode": paired.MODE, "commit": COMMIT,
                            "source": {"git_head": COMMIT, "git_dirty": False},
                            "policy_sha256": HASH, "attestations": baseline_proof,
                            "host_session": HOST},
        "environment": {"set": {"LANG": "C"}, "preserved_runtime_roots": {}, "inherited_path": "/bin"},
        "implementations": [{"name": name, "executable_path": f"/subject/{name}",
                             "executable_sha256": str(index + 1) * 64}
                            for index, name in enumerate(NAMES)],
    }


def reports(candidate_factor=1.0):
    values = {name: int(1000 * (candidate_factor if name in paired.DEPLOYMENTS else 1)) for name in NAMES}
    def samples(count):
        return {name: {"samples_ns": [value] * count,
                       "statistics": {"median_ms": value / 1e6}}
                for name, value in values.items()}
    fixture = {"schema_version": 3, "metadata": metadata(879), "startup": samples(300),
               "scenarios": [{"id": str(i), "implementations": samples(3)} for i in range(879)],
               "aggregates": [{"group": "ALL", "implementation": name,
                               "mean_vs_native": value / values["native-jq"]} for name, value in values.items()]}
    macro = {"schema_version": 3, "metadata": metadata(21),
             "scenarios": [{"id": f"{i:02d}", "implementations": samples(3)} for i in range(21)],
             "aggregates": [{"group": "PROCESSING_ONLY", "implementation": name,
                             "geomean_median_vs_native": value / values["native-jq"]} for name, value in values.items()]}
    return fixture, macro


class GateTests(unittest.TestCase):
    def evaluate(self, fixture, macro):
        with mock.patch.object(paired, "host_session", return_value=HOST), contextlib.redirect_stdout(io.StringIO()):
            gate.evaluate(fixture, macro, POLICY, HASH)

    def test_equal_and_faster_candidates_pass(self):
        for ratio in (0.8, 1.0, 1.3):
            with self.subTest(ratio=ratio):
                self.evaluate(*reports(ratio))

    def test_real_slowdown_fails_all_six_metrics(self):
        with self.assertRaises(gate.GateError) as caught:
            self.evaluate(*reports(1.31))
        self.assertEqual(6, str(caught.exception).count("baseline"))

    def test_cross_machine_job_source_policy_and_runtime_rejected(self):
        for field, value in (("host_session", {}), ("commit", "0" * 40),
                             ("policy_sha256", "0" * 64), ("mode", "separate-runs")):
            with self.subTest(field=field):
                fixture, macro = reports()
                for report in (fixture, macro):
                    report["metadata"]["paired_baseline"][field] = value
                with self.assertRaises((gate.GateError, report_output.ReportOutputError)):
                    self.evaluate(fixture, macro)
        fixture, macro = reports()
        macro["metadata"]["paired_baseline"] = copy.deepcopy(macro["metadata"]["paired_baseline"])
        macro["metadata"]["paired_baseline"]["host_session"] = {**HOST, "github_job": "different-job"}
        with self.assertRaises(gate.GateError):
            self.evaluate(fixture, macro)
        fixture, macro = reports()
        fixture["metadata"]["publication"]["attestations"] = copy.deepcopy(fixture["metadata"]["publication"]["attestations"])
        fixture["metadata"]["publication"]["attestations"][0]["runtime"]["version"] = "99.0"
        with self.assertRaises(gate.GateError):
            self.evaluate(fixture, macro)

    def test_native_aot_toolchain_and_release_artifact_tampering_rejected(self):
        for fault in (
            "package-version", "packages-sha256", "archive-sha256",
            "payload-sha256", "malformed-package",
        ):
            fixture, macro = reports()
            for report in (fixture, macro):
                runtime = report["metadata"]["publication"]["attestations"][1]["runtime"]
                if fault == "package-version":
                    runtime["packages"][0]["version"] = "99.0.0"
                elif fault == "packages-sha256":
                    runtime["packages_sha256"] = "b" * 64
                elif fault == "archive-sha256":
                    runtime["archive_sha256"] = "invalid"
                elif fault == "malformed-package":
                    runtime["packages"][-1] = None
                else:
                    runtime["packages"][0]["payload_sha256"] = "invalid"
            with self.subTest(fault=fault), self.assertRaises(gate.GateError):
                self.evaluate(fixture, macro)

    def test_old_three_subject_reports_cannot_authorize_release(self):
        fixture, macro = reports()
        for report in (fixture, macro):
            report["metadata"].pop("paired_baseline")
        with self.assertRaises(gate.GateError):
            self.evaluate(fixture, macro)

    def test_startup_requires_300_samples_and_no_missing_measurements(self):
        for fault in ("repetitions", "sample", "negative", "summary"):
            fixture, macro = reports()
            if fault == "repetitions":
                fixture["metadata"]["startup_repetitions"] = 30
            elif fault == "sample":
                fixture["startup"]["baseline-aot-dotnetjq"]["samples_ns"].pop()
            elif fault == "negative":
                fixture["scenarios"][0]["implementations"]["aot-dotnetjq"]["samples_ns"][0] = -1
            else:
                fixture["aggregates"][0]["mean_vs_native"] = 0.1
            with self.subTest(fault=fault), self.assertRaises(gate.GateError):
                self.evaluate(fixture, macro)

    def test_candidate_payload_change_between_reports_rejected(self):
        fixture, macro = reports()
        macro["metadata"]["implementations"][0]["executable_sha256"] = "f" * 64
        with self.assertRaises(gate.GateError):
            self.evaluate(fixture, macro)

    def test_effective_environment_change_rejected_but_removed_variables_allowed(self):
        fixture, macro = reports()
        fixture["metadata"]["environment"]["removed_parent_performance_variables"] = ["LD_PRELOAD"]
        self.evaluate(fixture, macro)
        macro["metadata"]["environment"]["set"]["LANG"] = "different"
        with self.assertRaises(gate.GateError):
            self.evaluate(fixture, macro)

    def test_paired_inventory_keeps_apphost_sharing_but_rejects_path_alias(self):
        data = metadata(879)
        data["implementations"][3]["executable_sha256"] = data["implementations"][0]["executable_sha256"]
        report_output._validate_implementation_metadata(data, "fixture")
        data["implementations"][3]["executable_path"] = data["implementations"][0]["executable_path"]
        with self.assertRaises(report_output.ReportOutputError):
            report_output._validate_implementation_metadata(data, "fixture")

    def test_five_way_rotation_is_balanced(self):
        subjects = [benchmark.Implementation(name, (name,), ()) for name in NAMES]
        for position in range(5):
            self.assertEqual(set(NAMES), {benchmark.rotated_implementations(subjects, 0, rep)[position].name for rep in range(5)})


class ProvenanceTests(unittest.TestCase):
    def test_wrong_or_dirty_source_and_same_checkout_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            with self.assertRaises(paired.provenance.AttestationError):
                paired.PairedBaseline(root, root)
            for source in ({"git_head": "0" * 40, "git_dirty": False},
                           {"git_head": COMMIT, "git_dirty": True}):
                with mock.patch.object(paired.provenance, "source_snapshot", return_value=source):
                    with self.assertRaises(paired.provenance.AttestationError):
                        paired.PairedBaseline(root, root / "candidate")

    def test_source_and_policy_and_attestation_tampering_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = {"git_head": COMMIT, "git_dirty": False}
            with mock.patch.object(paired.provenance, "source_snapshot", return_value=source):
                baseline = paired.PairedBaseline(root, root / "candidate")
            with mock.patch.object(paired.provenance, "source_snapshot", return_value={}):
                with self.assertRaises(paired.provenance.AttestationError):
                    baseline.verify({})
            with mock.patch.object(paired.provenance, "source_snapshot", return_value=source):
                with mock.patch.object(paired.provenance, "sha256_file", return_value="wrong"):
                    with self.assertRaises(paired.provenance.AttestationError):
                        baseline.verify({})
                with mock.patch.object(paired.provenance, "validate_attestation", side_effect=paired.provenance.AttestationError("tampered")):
                    with self.assertRaises(paired.provenance.AttestationError):
                        baseline.verify({})


class ReportTests(unittest.TestCase):
    def test_five_subject_fixture_report_certifies_and_detects_tampering(self):
        subjects = [benchmark.Implementation(name, (name,), ()) for name in NAMES]
        case = benchmark.FixtureCase("jq.test", 1, 1, ".", "null", ("null",), False, False)
        measured = {(case.scenario_id, name): [1_000_000] * 3 for name in NAMES}
        startup = {name: [1_000_000] * 300 for name in NAMES}
        aggregates = benchmark.aggregate_measurements([case], measured, subjects)
        for name in NAMES:
            stats = benchmark.sample_stats(startup[name])
            total = stats.pop("total_ms")
            stats.update(group="STARTUP", implementation=name, scenarios=1,
                         total_ms=stats["median_ms"], measured_total_ms=total, mean_vs_native=1.0)
            aggregates.append(stats)
        data = metadata(1)
        data["publication"].update(publishable=False, complete_run=False)
        data["finished_utc"] = "2026-09-08T00:00:00+00:00"
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            output = root / "artifacts/performance/results"
            with report_output.report_run_lease(output, root, "fixture") as lease:
                report_output.prepare_report_directory(output, root, "fixture", lease)
                benchmark.write_reports(output, lease, data, [case],
                                        {case.scenario_id: benchmark.ProcessResult(0, b"null\n", b"", 1)},
                                        measured, subjects, aggregates, startup)
            status = report_output.read_report_status(output, "fixture")
            self.assertEqual(1515, status["validation"]["timed_measurements"])
            with (output / "measurements.csv").open("a") as stream:
                stream.write("tampered\n")
            with self.assertRaises(report_output.ReportOutputError):
                report_output.read_report_status(output, "fixture")

    def test_five_subject_macro_report_certifies(self):
        subjects = [macro_driver.Implementation(name, (name,), ()) for name in NAMES]
        scenarios = [macro_driver.Scenario("00", "startup", None, (), "null"),
                     macro_driver.Scenario("01", "work", None, (), "null")]
        measured = {(scenario.id, name): [macro_driver.Measurement(rep, position, index, 1_000_000)
                    for rep in range(1, 4)] for position, scenario in enumerate(scenarios, 1)
                    for index, name in enumerate(NAMES, 1)}
        correct = {(scenario.id, name): macro_driver.ProcessResult(0, b"null\n", b"", 1)
                   for scenario in scenarios for name in NAMES}
        summaries, aggregates = macro_driver.summarize_measurements(scenarios, {}, subjects, measured)
        data = metadata(2)
        data["publication"].update(publishable=False, complete_run=False)
        data["finished_utc"] = "2026-09-08T00:00:00+00:00"
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            output = root / "artifacts/performance/macro-results"
            with report_output.report_run_lease(output, root, "macro") as lease:
                report_output.prepare_report_directory(output, root, "macro", lease)
                macro_driver.write_reports(output, lease, data, scenarios, {}, subjects,
                                           correct, measured, summaries, aggregates)
            status = report_output.read_report_status(output, "macro")
            self.assertEqual(30, status["validation"]["timed_measurements"])


if __name__ == "__main__":
    unittest.main()
