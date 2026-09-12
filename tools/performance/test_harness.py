#!/usr/bin/env python3
"""Fast deterministic tests for the performance harness; launches no benchmark CLIs."""

from __future__ import annotations

import argparse
import contextlib
import io
import json
import os
import pathlib
import subprocess
import sys
import tarfile
import tempfile
import unittest
from collections import defaultdict
from types import SimpleNamespace
from unittest import mock

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import build_attestation
import benchmark as fixture_benchmark
import macro_benchmark
import report_output


ROOT = pathlib.Path(__file__).resolve().parents[2]
PINNED_UPSTREAM = pathlib.Path(
    os.environ.get("DOTNETJQ_PERFORMANCE_UPSTREAM") or
    os.environ.get("DOTNETJQ_UPSTREAM") or ROOT / "upstream/jq"
)
REQUIRE_PINNED_UPSTREAM = False


def synthetic_implementation_metadata(names: list[str]) -> list[dict[str, object]]:
    return [
        {
            "name": name,
            "executable_sha256": f"{index + 1:064x}",
        }
        for index, name in enumerate(names)
    ]


def non_publishable_metadata(
    names: list[str], scenario_count: int, repetitions: int
) -> dict[str, object]:
    return {
        "finished_utc": "2026-01-01T00:00:00+00:00",
        "scenario_count": scenario_count,
        "repetitions": repetitions,
        "implementations": synthetic_implementation_metadata(names),
        "publication": {
            "publishable": False,
            "complete_run": False,
            "attestations_required": False,
            "attestations": [],
        },
    }


def write_test_release_archive(path: pathlib.Path, executable: bytes) -> None:
    with tarfile.open(path, "w:gz") as archive:
        member = tarfile.TarInfo("./dotnetjq")
        member.size = len(executable)
        member.mode = 0o755
        archive.addfile(member, io.BytesIO(executable))


def pinned_upstream_or_skip(test_case: unittest.TestCase) -> pathlib.Path:
    if PINNED_UPSTREAM.is_dir():
        return PINNED_UPSTREAM
    message = f"pinned upstream jq checkout is unavailable: {PINNED_UPSTREAM}"
    if REQUIRE_PINNED_UPSTREAM:
        test_case.fail(message)
    test_case.skipTest(message)


class FixtureHarnessTests(unittest.TestCase):
    def implementations(self) -> list[fixture_benchmark.Implementation]:
        return [
            fixture_benchmark.Implementation(name, (name,), ())
            for name in ("framework-dotnetjq", "aot-dotnetjq", "native-jq")
        ]

    def test_official_fixture_capacity_is_879(self) -> None:
        tests = pinned_upstream_or_skip(self) / "tests"
        counts = {
            name: len(fixture_benchmark.parse_fixture(tests / name))
            for name in fixture_benchmark.FIXTURE_ORDER
        }
        self.assertEqual(fixture_benchmark.FIXTURE_COUNTS, counts)
        self.assertEqual(879, sum(counts.values()))

    def test_strict_upstream_mode_turns_missing_checkout_into_failure(self) -> None:
        missing = pathlib.Path("/definitely-unavailable/dotnetjq-pinned-upstream")
        with mock.patch.object(sys.modules[__name__], "PINNED_UPSTREAM", missing):
            with mock.patch.object(
                sys.modules[__name__], "REQUIRE_PINNED_UPSTREAM", True
            ):
                with self.assertRaises(AssertionError):
                    pinned_upstream_or_skip(self)
            with mock.patch.object(
                sys.modules[__name__], "REQUIRE_PINNED_UPSTREAM", False
            ):
                with self.assertRaises(unittest.SkipTest):
                    pinned_upstream_or_skip(self)

    def test_pinned_upstream_identity_covers_fixtures_and_modules(self) -> None:
        upstream = pinned_upstream_or_skip(self)
        fixture_identity = fixture_benchmark.upstream_identity(upstream)
        macro_identity = macro_benchmark.upstream_identity(upstream)
        self.assertEqual(fixture_benchmark.PINNED_COMMIT, fixture_identity["git_head"])
        self.assertTrue(fixture_identity["clean"])
        self.assertEqual(7, fixture_identity["fixtures"]["files"])
        self.assertGreater(fixture_identity["modules"]["files"], 0)
        self.assertEqual(
            fixture_identity["fixtures"]["sha256"],
            macro_identity["fixtures"]["sha256"],
        )

    def test_rotation_is_balanced_per_scenario(self) -> None:
        implementations = self.implementations()
        for stable_index in range(17):
            positions: dict[str, list[int]] = defaultdict(list)
            for repetition in range(3):
                order = fixture_benchmark.rotated_implementations(
                    implementations, stable_index, repetition
                )
                for position, implementation in enumerate(order):
                    positions[implementation.name].append(position)
            for values in positions.values():
                self.assertEqual([0, 1, 2], sorted(values))

    def test_small_sample_p95_is_null_but_range_is_retained(self) -> None:
        stats = fixture_benchmark.sample_stats([1_000_000, 2_000_000, 3_000_000])
        self.assertIsNone(stats["p95_ms"])
        self.assertEqual(1.0, stats["min_ms"])
        self.assertEqual(2.0, stats["median_ms"])
        self.assertEqual(3.0, stats["max_ms"])
        qualified = fixture_benchmark.sample_stats(
            [value * 1_000_000 for value in range(1, 21)]
        )
        self.assertEqual(19.0, qualified["p95_ms"])

    def test_fixture_correctness_is_byte_exact(self) -> None:
        implementation = self.implementations()[0]
        case = fixture_benchmark.FixtureCase(
            "jq.test", 1, 1, ".", "{}", ("{}",), False, False
        )
        reference = fixture_benchmark.ProcessResult(0, b'{"a":1}\n', b"", 1)
        fixture_benchmark.assert_matches_reference(
            implementation, case, reference, reference, "test"
        )
        with self.assertRaises(fixture_benchmark.BenchmarkError):
            fixture_benchmark.assert_matches_reference(
                implementation,
                case,
                reference,
                fixture_benchmark.ProcessResult(0, b'{"a": 1}\n', b"", 1),
                "test",
            )
        with self.assertRaises(fixture_benchmark.BenchmarkError):
            fixture_benchmark.assert_matches_reference(
                implementation,
                case,
                reference,
                fixture_benchmark.ProcessResult(0, reference.stdout, b"different", 1),
                "test",
            )
        with self.assertRaises(fixture_benchmark.BenchmarkError):
            fixture_benchmark.assert_matches_reference(
                implementation,
                case,
                reference,
                fixture_benchmark.ProcessResult(1, reference.stdout, reference.stderr, 1),
                "test",
            )

    def test_fixture_correctness_rejects_property_order_change(self) -> None:
        implementation = self.implementations()[0]
        case = fixture_benchmark.FixtureCase(
            "jq.test", 1, 1, ".", "{}", ("{}",), False, False
        )
        reference = fixture_benchmark.ProcessResult(
            0, b'{"first":1,"second":2}\n', b"", 1
        )
        reordered = fixture_benchmark.ProcessResult(
            0, b'{"second":2,"first":1}\n', b"", 1
        )
        with self.assertRaisesRegex(
            fixture_benchmark.BenchmarkError, "stdout expected="
        ):
            fixture_benchmark.assert_matches_reference(
                implementation, case, reference, reordered, "test"
            )

    def test_shared_fixture_preflight_invokes_each_implementation_once_per_case(
        self,
    ) -> None:
        implementations = self.implementations()
        managed = implementations[:2]
        native = implementations[2]
        cases = [
            fixture_benchmark.FixtureCase(
                "jq.test", 1, 1, ".", "{}", ("{}",), False, False
            ),
            fixture_benchmark.FixtureCase(
                "jq.test", 2, 4, ".a", '{"a":1}', ("1",), False, False
            ),
        ]
        fixture_result = fixture_benchmark.ProcessResult(
            0, b"550 of 550 tests passed (0 malformed, 0 skipped)\n", b"", 1
        )
        scenario_result = fixture_benchmark.ProcessResult(0, b"{}\n", b"", 1)
        with mock.patch.object(
            fixture_benchmark, "validate_native_fixture", return_value=fixture_result
        ) as fixture_validation, mock.patch.object(
            fixture_benchmark, "execute", return_value=scenario_result
        ) as execution, contextlib.redirect_stdout(io.StringIO()):
            references = fixture_benchmark.validate_fixture_preflight(
                native,
                managed,
                cases,
                [pathlib.Path("/fixtures/jq.test")],
                pathlib.Path("/fixtures/modules"),
                pathlib.Path("/upstream"),
                {},
                30.0,
            )

        self.assertEqual(3, fixture_validation.call_count)
        self.assertEqual(
            ["native-jq", "framework-dotnetjq", "aot-dotnetjq"],
            [call.args[0].name for call in fixture_validation.call_args_list],
        )
        self.assertEqual(6, execution.call_count)
        self.assertEqual(
            [
                "native-jq",
                "framework-dotnetjq",
                "aot-dotnetjq",
                "native-jq",
                "framework-dotnetjq",
                "aot-dotnetjq",
            ],
            [call.args[0].name for call in execution.call_args_list],
        )
        self.assertEqual({case.scenario_id for case in cases}, set(references))

    def test_correctness_only_rejects_selection_reporting_and_timing_options(
        self,
    ) -> None:
        rejected = (
            ["--fixture", "jq.test"],
            ["--limit", "1"],
            ["--output-directory", "/tmp/results"],
            ["--repetitions", "1"],
            ["--warmups", "0"],
            ["--startup-repetitions", "1"],
            ["--seed", "1"],
            ["--publishable"],
            ["--non-publishable"],
            ["--correctness-only"],
        )
        for conflicting in rejected:
            with self.subTest(option=conflicting[0]), contextlib.redirect_stderr(
                io.StringIO()
            ):
                with self.assertRaises(SystemExit) as error:
                    fixture_benchmark.parse_arguments(
                        ["--correctness-only", *conflicting]
                    )
                self.assertEqual(2, error.exception.code)

        accepted = fixture_benchmark.parse_arguments(
            ["--correctness-only", "--timeout-seconds", "1"]
        )
        self.assertTrue(accepted.correctness_only)
        self.assertEqual(1.0, accepted.timeout_seconds)

    def test_correctness_only_report_setup_preserves_existing_report_leaf(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory) / "repository"
            root.mkdir()
            output = root / report_output.report_spec("fixture").default_relative_path
            output.mkdir(parents=True)
            sentinel = output / "existing-report.txt"
            sentinel.write_bytes(b"must remain byte-identical\n")
            arguments = SimpleNamespace(correctness_only=True)

            with report_output.report_run_lease(output, root, "fixture") as lease:
                selected_output, publishable = fixture_benchmark.prepare_benchmark_report(
                    arguments, root, lease, True
                )

            self.assertIsNone(selected_output)
            self.assertFalse(publishable)
            self.assertEqual(b"must remain byte-identical\n", sentinel.read_bytes())
            self.assertEqual({"existing-report.txt"}, {item.name for item in output.iterdir()})

    def test_fixture_report_renders_unqualified_p95_as_na(self) -> None:
        implementations = self.implementations()
        case = fixture_benchmark.FixtureCase(
            "jq.test", 1, 1, ".", "{}", ("{}",), False, False
        )
        reference = fixture_benchmark.ProcessResult(0, b"{}\n", b"", 1)
        measurements = {
            (case.scenario_id, implementation.name): [1_000_000, 2_000_000, 3_000_000]
            for implementation in implementations
        }
        startup = {
            implementation.name: list(range(1_000_000, 21_000_000, 1_000_000))
            for implementation in implementations
        }
        aggregates = fixture_benchmark.aggregate_measurements(
            [case], measurements, implementations
        )
        native_startup_mean = sum(startup["native-jq"]) / len(startup["native-jq"])
        for implementation in implementations:
            stats = fixture_benchmark.sample_stats(startup[implementation.name])
            measured_total_ms = stats.pop("total_ms")
            stats.update(
                {
                    "group": "STARTUP",
                    "implementation": implementation.name,
                    "scenarios": 1,
                    "total_ms": stats["median_ms"],
                    "measured_total_ms": measured_total_ms,
                    "mean_vs_native": (
                        sum(startup[implementation.name])
                        / len(startup[implementation.name])
                        / native_startup_mean
                    ),
                }
            )
            aggregates.append(stats)
        with tempfile.TemporaryDirectory() as directory:
            base = pathlib.Path(directory)
            synthetic_root = base / "repository"
            synthetic_root.mkdir()
            output = base / "fixture-output"
            with report_output.report_run_lease(
                output, synthetic_root, "fixture"
            ) as lease:
                report_output.prepare_report_directory(
                    output, synthetic_root, "fixture", lease
                )
                metadata = non_publishable_metadata(
                    [implementation.name for implementation in implementations], 1, 3
                )
                metadata["startup_repetitions"] = 20
                fixture_benchmark.write_reports(
                    output,
                    lease,
                    metadata,
                    [case],
                    {case.scenario_id: reference},
                    measurements,
                    implementations,
                    aggregates,
                    startup,
                )
            summary = (output / "summary.md").read_text(encoding="utf-8")
            self.assertIn("| n/a |", summary)
            self.assertIn("non-publishable smoke/test result", summary)
            results = json.loads((output / "results.json").read_text(encoding="utf-8"))
            self.assertEqual(3, results["schema_version"])
            self.assertFalse(results["metadata"]["publication"]["publishable"])
            status = report_output.read_report_status(output, "fixture")
            self.assertEqual("complete", status["state"])
            self.assertEqual(69, status["validation"]["timed_measurements"])
            self.assertEqual(5, len(status["files"]))
            self.assertEqual(
                set(report_output.report_spec("fixture").files)
                | {report_output.STATUS_FILENAME},
                {entry.name for entry in output.iterdir()},
            )
            unexpected = output / "unexpected.txt"
            unexpected.write_text("unexpected\n", encoding="utf-8")
            with self.assertRaisesRegex(
                report_output.ReportOutputError, "inventory changed"
            ):
                report_output.read_report_status(output, "fixture")
            unexpected.unlink()
            (output / "summary.md").write_text("tampered\n", encoding="utf-8")
            with self.assertRaisesRegex(
                report_output.ReportOutputError, "changed after certification"
            ):
                report_output.read_report_status(output, "fixture")

    def test_duplicate_fixture_selection_is_rejected(self) -> None:
        with self.assertRaises(fixture_benchmark.BenchmarkError):
            fixture_benchmark.reject_duplicates(
                ["jq.test", "jq.test"], "fixture selection"
            )

    def test_wrong_oracle_hash_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            candidate = pathlib.Path(directory) / "jq"
            candidate.write_bytes(b"not the pinned jq oracle")
            with self.assertRaises(fixture_benchmark.BenchmarkError):
                fixture_benchmark.validate_pinned_oracle(candidate)

    def test_performance_environment_is_sanitized_and_recorded(self) -> None:
        parent = {
            "PATH": "/usr/bin:/bin",
            "DOTNET_ROOT": "/runtime",
            "DOTNET_GCHeapHardLimit": "1234",
            "COMPlus_TieredCompilation": "0",
            "COREHOST_TRACE": "1",
            "LD_LIBRARY_PATH": "/unexpected",
            "JQ_COLORS": "abc",
        }
        with mock.patch.dict(os.environ, parent, clear=True):
            environment = fixture_benchmark.stable_environment(pathlib.Path("/tmp/home"))
            self.assertEqual("/runtime", environment["DOTNET_ROOT"])
            self.assertNotIn("DOTNET_GCHeapHardLimit", environment)
            self.assertNotIn("COMPlus_TieredCompilation", environment)
            self.assertNotIn("COREHOST_TRACE", environment)
            self.assertNotIn("LD_LIBRARY_PATH", environment)
            self.assertNotIn("JQ_COLORS", environment)
            contract = fixture_benchmark.environment_contract()
            self.assertEqual(
                [
                    "COMPlus_TieredCompilation",
                    "COREHOST_TRACE",
                    "DOTNET_GCHeapHardLimit",
                    "JQ_COLORS",
                    "LD_LIBRARY_PATH",
                ],
                contract["removed_parent_performance_variables"],
            )
            self.assertTrue(
                macro_benchmark.performance_environment_variable("COREHOST_TRACE")
            )

    def test_duplicate_executable_and_artifact_identities_are_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            first = root / "first"
            second = root / "second"
            first.write_bytes(b"same executable")
            second.write_bytes(b"same executable")
            first.chmod(0o755)
            second.chmod(0o755)
            implementations = [
                fixture_benchmark.Implementation("one", (str(first),), (first,)),
                fixture_benchmark.Implementation("two", (str(second),), (second,)),
            ]
            with self.assertRaises(fixture_benchmark.BenchmarkError):
                fixture_benchmark.validate_implementation_identities(implementations)

    def test_build_configuration_contract_distinguishes_coreclr_and_aot(self) -> None:
        valid = {
            "framework-dotnetjq": "net10.0; CoreCLR; linux-x64; jq-1.8.2 compatible",
            "aot-dotnetjq": "net10.0; NativeAOT; linux-x64; jq-1.8.2 compatible",
            "native-jq": "--host=x86_64-linux-gnu",
        }
        fixture_benchmark.validate_build_configurations(valid)
        invalid = dict(valid)
        invalid["aot-dotnetjq"] = invalid["framework-dotnetjq"]
        with self.assertRaises(fixture_benchmark.BenchmarkError):
            fixture_benchmark.validate_build_configurations(invalid)

    def test_nonpublishable_run_cannot_use_default_report_path(self) -> None:
        arguments = SimpleNamespace(publishable=False, non_publishable=True)
        default = pathlib.Path("/tmp/default-results")
        with self.assertRaises(fixture_benchmark.BenchmarkError):
            fixture_benchmark.publication_decision(
                arguments, False, default, default
            )
        self.assertFalse(
            fixture_benchmark.publication_decision(
                arguments, False, pathlib.Path("/tmp/smoke-results"), default
            )
        )

    def test_report_prepare_removes_stale_files_and_marks_failed_run_incomplete(
        self,
    ) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory) / "repository"
            root.mkdir()
            output = root / "artifacts/performance/results"
            output.mkdir(parents=True)
            (output / "results.json").write_text("stale\n", encoding="utf-8")
            nested = output / "stale-directory"
            nested.mkdir()
            (nested / "old.csv").write_text("stale\n", encoding="utf-8")

            with report_output.report_run_lease(output, root, "fixture") as lease:
                prepared = report_output.prepare_report_directory(
                    output, root, "fixture", lease
                )
                run_id = lease.run_id

            self.assertEqual(output, prepared)
            self.assertEqual(
                {report_output.STATUS_FILENAME},
                {entry.name for entry in prepared.iterdir()},
            )
            self.assertEqual(
                "incomplete",
                report_output.read_report_status(prepared, "fixture")["state"],
            )
            self.assertEqual(
                run_id,
                report_output.read_report_status(prepared, "fixture")["run_id"],
            )
            self.assertFalse((prepared / "results.json").exists())
            self.assertFalse(nested.exists())

    def test_report_runner_parses_the_shell_command_separator(self) -> None:
        arguments = report_output.parse_arguments(
            [
                "--root",
                "/tmp/repository",
                "--kind",
                "fixture",
                "--output",
                "/tmp/results",
                "--run-command",
                "--",
                "/tmp/run.sh",
                "--output-directory",
                "/tmp/results",
            ]
        )
        self.assertEqual(
            [
                "--",
                "/tmp/run.sh",
                "--output-directory",
                "/tmp/results",
            ],
            arguments.run_command,
        )

    def test_report_lease_is_exclusive_inherited_and_released(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory) / "repository"
            root.mkdir()
            fixture_output = root / report_output.report_spec(
                "fixture"
            ).default_relative_path
            macro_output = root / report_output.report_spec("macro").default_relative_path

            with report_output.report_run_lease(
                fixture_output, root, "fixture"
            ) as first:
                first_id = first.run_id
                with self.assertRaisesRegex(
                    report_output.ReportOutputError, "holds the exclusive lease"
                ):
                    with report_output.report_run_lease(
                        macro_output, root, "macro"
                    ):
                        pass
                competitor = "\n".join(
                    (
                        "import pathlib, sys",
                        f"sys.path.insert(0, {str((ROOT / 'tools/performance').resolve())!r})",
                        "import report_output",
                        f"root = pathlib.Path({str(root)!r})",
                        f"output = pathlib.Path({str(macro_output)!r})",
                        "try:",
                        "    with report_output.report_run_lease(output, root, 'macro'):",
                        "        raise SystemExit(2)",
                        "except report_output.ReportOutputError as error:",
                        "    raise SystemExit(",
                        "        0 if 'holds the exclusive lease' in str(error) else 3",
                        "    )",
                    )
                )
                competitor_environment = os.environ.copy()
                competitor_environment.pop(report_output.LEASE_ID_ENVIRONMENT, None)
                competitor_environment.pop(report_output.LEASE_FD_ENVIRONMENT, None)
                completed = subprocess.run(
                    [sys.executable, "-c", competitor],
                    env=competitor_environment,
                    check=False,
                )
                self.assertEqual(0, completed.returncode)

            with report_output.report_run_lease(
                macro_output, root, "macro"
            ) as second:
                self.assertNotEqual(first_id, second.run_id)

            child = (
                "import pathlib,sys;"
                f"sys.path.insert(0,{str((ROOT / 'tools/performance').resolve())!r});"
                "import report_output;"
                f"root=pathlib.Path({str(root)!r});"
                f"output=pathlib.Path({str(fixture_output)!r});"
                "context=report_output.report_run_lease(output,root,'fixture');"
                "lease=context.__enter__();lease.validate();context.__exit__(None,None,None)"
            )
            self.assertEqual(
                0,
                report_output.run_with_report_lease(
                    fixture_output,
                    root,
                    "fixture",
                    [sys.executable, "-c", child],
                ),
            )

    def test_report_lease_refuses_symlink_but_reclaims_stale_regular_file(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory) / "repository"
            root.mkdir()
            output = root / report_output.report_spec("fixture").default_relative_path
            lock_parent = root / "artifacts/performance"
            lock_parent.mkdir(parents=True)
            lock_path = lock_parent / report_output.LEASE_FILENAME
            lock_path.write_text("stale lease payload\n", encoding="utf-8")
            with report_output.report_run_lease(output, root, "fixture") as lease:
                lease.validate()
            lock_path.unlink()
            victim = pathlib.Path(directory) / "victim"
            victim.write_text("keep\n", encoding="utf-8")
            lock_path.symlink_to(victim)
            with self.assertRaises(report_output.ReportOutputError):
                with report_output.report_run_lease(output, root, "fixture"):
                    pass
            self.assertEqual("keep\n", victim.read_text(encoding="utf-8"))

    def test_failed_fixture_and_macro_preflight_cannot_leave_old_reports(self) -> None:
        for module, kind in (
            (fixture_benchmark, "fixture"),
            (macro_benchmark, "macro"),
        ):
            with self.subTest(kind=kind), tempfile.TemporaryDirectory() as directory:
                root = pathlib.Path(directory) / "repository"
                root.mkdir()
                output = root / report_output.report_spec(kind).default_relative_path
                output.mkdir(parents=True)
                (output / "results.json").write_text("old result\n", encoding="utf-8")
                with mock.patch.object(module, "repository_root", return_value=root), mock.patch.object(
                    module,
                    "source_identity",
                    side_effect=module.BenchmarkError("forced failure before report write"),
                ):
                    with self.assertRaisesRegex(
                        module.BenchmarkError, "forced failure before report write"
                    ):
                        module.main(["--regenerate"] if kind == "macro" else [])
                self.assertFalse((output / "results.json").exists())
                self.assertEqual(
                    "incomplete",
                    report_output.read_report_status(output, kind)["state"],
                )

    def test_cleanup_failure_keeps_old_report_quarantined_not_current(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory) / "repository"
            root.mkdir()
            output = root / report_output.report_spec(
                "fixture"
            ).default_relative_path
            output.mkdir(parents=True)
            (output / "results.json").write_text("old result\n", encoding="utf-8")

            with mock.patch.object(
                report_output.shutil, "rmtree", side_effect=OSError("forced cleanup failure")
            ):
                with self.assertRaisesRegex(
                    report_output.ReportOutputError, "safely marked incomplete"
                ):
                    with report_output.report_run_lease(
                        output, root, "fixture"
                    ) as lease:
                        report_output.prepare_report_directory(
                            output, root, "fixture", lease
                        )

            self.assertFalse((output / "results.json").exists())
            self.assertEqual(
                "incomplete",
                report_output.read_report_status(output, "fixture")["state"],
            )
            quarantined = list(output.parent.glob(".results.previous-*"))
            self.assertEqual(1, len(quarantined))
            self.assertEqual(
                "old result\n",
                (quarantined[0] / "results.json").read_text(encoding="utf-8"),
            )

    def test_report_output_refuses_symlinks_broad_paths_and_repository_overlap(
        self,
    ) -> None:
        with tempfile.TemporaryDirectory() as directory:
            base = pathlib.Path(directory)
            root = base / "repository"
            root.mkdir()
            victim = base / "victim"
            victim.mkdir()
            sentinel = victim / "keep.txt"
            sentinel.write_text("keep\n", encoding="utf-8")
            default_output = root / report_output.report_spec(
                "fixture"
            ).default_relative_path
            default_output.parent.mkdir(parents=True)
            default_output.symlink_to(victim, target_is_directory=True)

            with self.assertRaises(report_output.ReportOutputError):
                with report_output.report_run_lease(default_output, root, "fixture"):
                    pass
            self.assertEqual("keep\n", sentinel.read_text(encoding="utf-8"))

            linked_parent = base / "linked-parent"
            linked_parent.symlink_to(victim, target_is_directory=True)
            with self.assertRaises(report_output.ReportOutputError):
                with report_output.report_run_lease(
                    linked_parent / "reports", root, "fixture"
                ):
                    pass
            self.assertEqual("keep\n", sentinel.read_text(encoding="utf-8"))
            with self.assertRaises(report_output.ReportOutputError):
                with report_output.report_run_lease(root, root, "fixture"):
                    pass
            with self.assertRaises(report_output.ReportOutputError):
                with report_output.report_run_lease(root / "src", root, "fixture"):
                    pass

            unowned = base / "unowned-custom-output"
            unowned.mkdir()
            (unowned / "unrelated.txt").write_text("do not delete\n", encoding="utf-8")
            with self.assertRaises(report_output.ReportOutputError):
                with report_output.report_run_lease(unowned, root, "fixture") as lease:
                    report_output.prepare_report_directory(
                        unowned, root, "fixture", lease
                    )
            self.assertTrue((unowned / "unrelated.txt").is_file())

    def test_report_completion_requires_each_exact_regular_file_inventory(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            base = pathlib.Path(directory)
            for kind in report_output.REPORT_SPECS:
                with self.subTest(kind=kind):
                    root = base / f"repository-{kind}"
                    root.mkdir()
                    requested = root / report_output.report_spec(kind).default_relative_path
                    with report_output.report_run_lease(requested, root, kind) as lease:
                        output = report_output.prepare_report_directory(
                            requested, root, kind, lease
                        )
                        for name in report_output.report_spec(kind).files:
                            (output / name).write_text(f"{name}\n", encoding="utf-8")
                        with self.assertRaises(report_output.ReportOutputError):
                            report_output.complete_report_directory(output, kind, lease)

                        output = report_output.prepare_report_directory(
                            requested, root, kind, lease
                        )
                        for name in report_output.report_spec(kind).files[:-1]:
                            (output / name).write_text(f"{name}\n", encoding="utf-8")
                        with self.assertRaises(report_output.ReportOutputError):
                            report_output.complete_report_directory(output, kind, lease)

                        (output / report_output.report_spec(kind).files[-1]).write_text(
                            "last\n", encoding="utf-8"
                        )
                        (output / "unexpected.txt").write_text(
                            "unexpected\n", encoding="utf-8"
                        )
                        with self.assertRaises(report_output.ReportOutputError):
                            report_output.complete_report_directory(output, kind, lease)

    def test_tampered_attestation_and_wrong_publish_command_are_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            path = root / "attestation.json"
            body = {"deployment": "framework-dotnetjq"}
            document = build_attestation.envelope(1, "attestation", body)
            document["attestation"]["deployment"] = "tampered"
            path.write_text(json.dumps(document), encoding="utf-8")
            with self.assertRaises(build_attestation.AttestationError):
                build_attestation.read_envelope(path, 1, "attestation")
            with self.assertRaises(build_attestation.AttestationError):
                build_attestation.create_attestation(
                    ROOT,
                    root / "missing-snapshot.json",
                    "framework-dotnetjq",
                    ["dotnet", "publish", "wrong"],
                )

    def test_artifact_manifest_covers_every_file_and_detects_change(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            (root / "program").write_bytes(b"program")
            (root / "dependency.dll").write_bytes(b"dependency")
            before = build_attestation.directory_manifest(root)
            self.assertEqual(2, before["files"])
            (root / "dependency.dll").write_bytes(b"changed")
            self.assertNotEqual(before, build_attestation.directory_manifest(root))

    def test_external_aot_attestation_requires_matching_producer_archive(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            artifact_directory = root / "extracted"
            artifact_directory.mkdir()
            executable = artifact_directory / "dotnetjq"
            executable.write_bytes(b"native executable")
            executable.chmod(0o755)
            archive = root / "dotnetjq-1.2.3-linux-x64.tar.gz"
            write_test_release_archive(archive, executable.read_bytes())
            snapshot = root / "snapshot.json"
            native_aot_snapshot = root / "native-aot.json"
            provenance = root / "release.provenance.json"
            source = {
                "git_head": "a" * 40,
                "git_dirty": False,
                "compiled_inputs": {"sha256": "b" * 64},
            }
            dotnet = {
                "sdk_version": "10.0.100",
                "native_tool_candidates": {},
            }
            packages = [
                {
                    "id": package_id,
                    "version": "10.0.11",
                    "payload": {"sha256": "c" * 64},
                }
                for package_id in build_attestation.NATIVE_AOT_PACKAGE_IDS
            ]
            native_aot = {
                "target": "net10.0/linux-x64",
                "project_assets_sha256": "d" * 64,
                "packages": packages,
                "packages_sha256": build_attestation.document_sha256(packages),
            }
            with mock.patch.object(
                build_attestation, "source_snapshot", return_value=source
            ), mock.patch.object(
                build_attestation, "dotnet_toolchain_identity", return_value=dotnet
            ), mock.patch.object(
                build_attestation, "native_aot_build_identity", return_value=native_aot
            ), mock.patch.object(
                build_attestation, "verify_native_aot_packages"
            ):
                build_attestation.create_snapshot(root, snapshot)
                build_attestation.create_native_aot_snapshot(
                    root, root / "project.assets.json", native_aot_snapshot
                )
                build_attestation.create_release_provenance(
                    root,
                    snapshot,
                    archive,
                    executable,
                    native_aot_snapshot,
                    "1.2.3",
                    provenance,
                )
                build_attestation.create_external_aot_attestation(
                    root, executable, archive, provenance
                )
                summary = build_attestation.validate_attestation(
                    root, "aot-dotnetjq", executable, source, dotnet
                )
                self.assertEqual(
                    build_attestation.sha256_file(archive),
                    summary["runtime"]["archive_sha256"],
                )
                self.assertEqual(
                    native_aot["packages_sha256"],
                    summary["runtime"]["packages_sha256"],
                )
                original_provenance = provenance.read_bytes()
                document = json.loads(original_provenance)
                del document["provenance"]["native_aot_build"]["packages"][0][
                    "payload"
                ]
                document["provenance"]["native_aot_build"]["packages_sha256"] = (
                    build_attestation.document_sha256(
                        document["provenance"]["native_aot_build"]["packages"]
                    )
                )
                document["provenance_sha256"] = build_attestation.document_sha256(
                    document["provenance"]
                )
                provenance.write_text(json.dumps(document), encoding="utf-8")
                with self.assertRaises(build_attestation.AttestationError):
                    build_attestation.validate_attestation(
                        root, "aot-dotnetjq", executable, source, dotnet
                    )
                provenance.write_bytes(original_provenance)
                archive.write_bytes(b"changed")
                with self.assertRaises(build_attestation.AttestationError):
                    build_attestation.validate_attestation(
                        root, "aot-dotnetjq", executable, source, dotnet
                    )

    def test_run_script_rejects_incomplete_or_conflicting_external_mode(self) -> None:
        run_script = ROOT / "tools/performance/run.sh"
        complete = [
            "--external-framework-executable", "/tmp/framework/DotNetJq.Cli",
            "--external-aot-executable", "/tmp/aot/dotnetjq",
            "--external-aot-archive", "/tmp/dotnetjq.tar.gz",
            "--external-aot-provenance", "/tmp/provenance.json",
        ]
        environment = dict(os.environ)
        environment.pop("LD_PRELOAD", None)
        for arguments in (
            ["--external-aot-archive", "/tmp/archive"],
            [*complete, "--skip-build"],
            [*complete, "--framework-executable", "/tmp/ordinary"],
        ):
            with self.subTest(arguments=arguments):
                completed = subprocess.run(
                    [str(run_script), *arguments],
                    cwd=ROOT,
                    env=environment,
                    stdout=subprocess.PIPE,
                    stderr=subprocess.PIPE,
                    text=True,
                    check=False,
                )
                self.assertNotEqual(0, completed.returncode)

    def test_native_aot_attestation_binds_all_selected_pack_metadata(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            assets_path = (
                root
                / "artifacts/performance/build/aot/obj/DotNetJq.Cli/project.assets.json"
            )
            assets_path.parent.mkdir(parents=True)
            package_root = root / "packages"
            libraries = {}
            downloads = []
            for package_id in build_attestation.NATIVE_AOT_PACKAGE_IDS:
                version = "10.0.11"
                relative = pathlib.Path(package_id.lower()) / version
                package = package_root / relative
                package.mkdir(parents=True)
                (package / f"{package_id.lower()}.{version}.nupkg.sha512").write_text(
                    "package-content-sha512\n", encoding="ascii"
                )
                (package / f"{package_id.lower()}.nuspec").write_text(
                    "<package />\n", encoding="utf-8"
                )
                payload = package / "payload" / "compiler-or-runtime.bin"
                payload.parent.mkdir()
                payload.write_bytes((package_id + " payload").encode("utf-8"))
                if package_id == "Microsoft.NETCore.App.Runtime.NativeAOT.linux-x64":
                    downloads.append(
                        {"name": package_id, "version": f"[{version}, {version}]"}
                    )
                else:
                    libraries[f"{package_id}/{version}"] = {
                        "path": relative.as_posix()
                    }
            assets_path.write_text(
                json.dumps(
                    {
                        "targets": {"net10.0/linux-x64": {}},
                        "libraries": libraries,
                        "packageFolders": {str(package_root) + os.sep: {}},
                        "project": {
                            "frameworks": {
                                "net10.0": {"downloadDependencies": downloads}
                            }
                        },
                    }
                ),
                encoding="utf-8",
            )
            identity = build_attestation.native_aot_build_identity(root)
            self.assertEqual("net10.0/linux-x64", identity["target"])
            self.assertEqual(4, len(identity["packages"]))
            build_attestation.verify_native_aot_packages(identity)
            package = pathlib.Path(identity["packages"][0]["path"])
            next(package.rglob("compiler-or-runtime.bin")).write_bytes(b"changed")
            with self.assertRaises(build_attestation.AttestationError):
                build_attestation.verify_native_aot_packages(identity)

    def test_publish_contract_uses_clean_isolated_intermediate_roots(self) -> None:
        framework_command = build_attestation.EXPECTED_PUBLISH_COMMANDS[
            "framework-dotnetjq"
        ]
        aot_command = build_attestation.EXPECTED_PUBLISH_COMMANDS["aot-dotnetjq"]
        framework_root = build_attestation.EXPECTED_BUILD_ROOTS["framework-dotnetjq"]
        aot_root = build_attestation.EXPECTED_BUILD_ROOTS["aot-dotnetjq"]
        self.assertNotEqual(framework_root, aot_root)
        self.assertEqual(
            framework_root,
            framework_command[framework_command.index("--artifacts-path") + 1],
        )
        self.assertEqual(
            aot_root,
            aot_command[aot_command.index("--artifacts-path") + 1],
        )
        self.assertIn("-p:DebugType=none", aot_command)
        self.assertNotIn("-p:DebugType=None", aot_command)
        run_script = (ROOT / "tools/performance/run.sh").read_text(encoding="utf-8")
        self.assertIn("-p:DebugType=none", run_script)
        self.assertNotIn("-p:DebugType=None", run_script)
        self.assertIn('"$script_directory/report_output.py"', run_script)
        self.assertIn("--require-pinned-upstream", run_script)
        self.assertNotIn("< <(", run_script)
        self.assertIn('if ! environment_names="$(compgen -e)"', run_script)
        self.assertIn("--run-command --", run_script)
        self.assertIn("correctness_only_count", run_script)
        self.assertIn("create-external-aot", run_script)
        release_builder = (ROOT / "tools/release/build-native-archive.sh").read_text(
            encoding="utf-8"
        )
        self.assertIn("--performance-provenance", release_builder)
        self.assertIn("snapshot-native-aot", release_builder)
        self.assertIn("create-release-provenance", release_builder)
        self.assertIn(
            'if [[ "$correctness_only_count" -eq 0 ]]; then\n'
            '  python3 -B "$script_directory/report_output.py"',
            run_script,
        )
        self.assertIn(
            'if [[ "$correctness_only_count" -eq 0 ]]; then\n'
            '  if [[ "$skip_build" -eq 1 || "$limited_run" -eq 1 ]]; then',
            run_script,
        )
        release_script = (ROOT / "tools/verify-release.sh").read_text(encoding="utf-8")
        self.assertEqual(1, release_script.count("tools/performance/run.sh"))
        self.assertEqual(1, release_script.count("--correctness-only"))
        self.assertIn('DOTNETJQ_PERFORMANCE_UPSTREAM="$upstream_root"', release_script)
        self.assertIn('--native-executable "$oracle_path"', release_script)
        self.assertIn('--upstream "$upstream_root"', release_script)
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            for deployment in build_attestation.DEPLOYMENTS:
                build_attestation.prepare_output(root, deployment)
                output = root / build_attestation.EXPECTED_OUTPUTS[deployment]
                intermediates = root / build_attestation.EXPECTED_BUILD_ROOTS[deployment]
                (output / "stale-output").write_bytes(b"stale")
                (intermediates / "stale-intermediate").write_bytes(b"stale")
                build_attestation.prepare_output(root, deployment)
                self.assertEqual([], list(output.iterdir()))
                self.assertEqual([], list(intermediates.iterdir()))

    def test_host_native_tool_candidates_are_content_identified(self) -> None:
        candidates = build_attestation.native_tool_candidates(ROOT)
        self.assertEqual({"clang", "cc", "ld"}, set(candidates))
        for identity in candidates.values():
            if identity is None:
                continue
            self.assertRegex(str(identity["sha256"]), r"^[0-9a-f]{64}$")
            self.assertTrue(identity["version"])


class MacroHarnessTests(unittest.TestCase):
    def test_macro_capacity_is_21(self) -> None:
        _, scenarios, _ = macro_benchmark.load_workloads(
            ROOT / "tools/performance/macro/workloads.json"
        )
        self.assertEqual(macro_benchmark.EXPECTED_SCENARIO_IDS, tuple(s.id for s in scenarios))
        self.assertEqual(21, len(scenarios))
        self.assertEqual(
            fixture_benchmark.PINNED_ORACLE_SHA256,
            macro_benchmark.PINNED_ORACLE_SHA256,
        )

    def test_macro_rotation_is_balanced_per_scenario(self) -> None:
        implementations = [
            macro_benchmark.Implementation(name, (name,), ())
            for name in macro_benchmark.IMPLEMENTATION_NAMES
        ]
        for stable_index in range(21):
            positions: dict[str, list[int]] = defaultdict(list)
            for repetition in range(3):
                order = macro_benchmark.rotated_implementations(
                    implementations, stable_index, repetition
                )
                for position, implementation in enumerate(order):
                    positions[implementation.name].append(position)
            for values in positions.values():
                self.assertEqual([0, 1, 2], sorted(values))

    def test_processing_and_startup_aggregates_are_separate(self) -> None:
        scenarios = [
            macro_benchmark.Scenario("00", "startup", None, (), "null"),
            macro_benchmark.Scenario("01", "work", "records", (), "."),
        ]
        datasets = {
            "records": macro_benchmark.Dataset(
                "records", pathlib.Path("records.json"), 1, 1_048_576, "0" * 64
            )
        }
        implementations = [
            macro_benchmark.Implementation(name, (name,), ())
            for name in macro_benchmark.IMPLEMENTATION_NAMES
        ]
        measurements: dict[
            tuple[str, str], list[macro_benchmark.Measurement]
        ] = defaultdict(list)
        for scenario_position, scenario in enumerate(scenarios, start=1):
            for implementation_position, implementation in enumerate(
                implementations, start=1
            ):
                for repetition in range(1, 4):
                    measurements[(scenario.id, implementation.name)].append(
                        macro_benchmark.Measurement(
                            repetition,
                            scenario_position,
                            implementation_position,
                            (1 if scenario.id == "00" else 10) * 1_000_000,
                        )
                    )
        summaries, aggregates = macro_benchmark.summarize_measurements(
            scenarios, datasets, implementations, measurements
        )
        self.assertTrue(all(row["p95_ms"] is None for row in summaries))
        self.assertEqual(
            {"PROCESSING_ONLY", "WITH_STARTUP"},
            {str(row["group"]) for row in aggregates},
        )
        processing = next(
            row
            for row in aggregates
            if row["group"] == "PROCESSING_ONLY"
            and row["implementation"] == "native-jq"
        )
        with_startup = next(
            row
            for row in aggregates
            if row["group"] == "WITH_STARTUP"
            and row["implementation"] == "native-jq"
        )
        self.assertEqual(10.0, processing["sum_scenario_medians_ms"])
        self.assertEqual(11.0, with_startup["sum_scenario_medians_ms"])
        self.assertIsNone(with_startup["geomean_p95_vs_native"])
        correctness = {
            (scenario.id, implementation.name): macro_benchmark.ProcessResult(
                0, b"null\n", b"", 1
            )
            for scenario in scenarios
            for implementation in implementations
        }
        with tempfile.TemporaryDirectory() as directory:
            base = pathlib.Path(directory)
            synthetic_root = base / "repository"
            synthetic_root.mkdir()
            output = base / "macro-output"
            with report_output.report_run_lease(
                output, synthetic_root, "macro"
            ) as lease:
                report_output.prepare_report_directory(
                    output, synthetic_root, "macro", lease
                )
                macro_benchmark.write_reports(
                    output,
                    lease,
                    non_publishable_metadata(
                        [implementation.name for implementation in implementations], 2, 3
                    ),
                    scenarios,
                    datasets,
                    implementations,
                    correctness,
                    measurements,
                    summaries,
                    aggregates,
                )
            summary = (output / "summary.md").read_text(encoding="utf-8")
            self.assertIn("PROCESSING_ONLY", summary)
            self.assertIn("WITH_STARTUP", summary)
            self.assertIn("median [min-max]", summary)
            self.assertIn("| n/a |", summary)
            self.assertIn("non-publishable smoke/test result", summary)
            results = json.loads((output / "results.json").read_text(encoding="utf-8"))
            self.assertEqual(3, results["schema_version"])
            self.assertFalse(results["metadata"]["publication"]["publishable"])
            status = report_output.read_report_status(output, "macro")
            self.assertEqual("complete", status["state"])
            self.assertEqual(18, status["validation"]["timed_measurements"])
            self.assertEqual(
                set(report_output.report_spec("macro").files)
                | {report_output.STATUS_FILENAME},
                {entry.name for entry in output.iterdir()},
            )

    def test_dataset_identity_covers_manifest_and_each_dataset(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            data = root / "records.json"
            manifest = root / "dataset-sha256.json"
            data.write_bytes(b"{}\n")
            manifest.write_text("{}\n", encoding="utf-8")
            datasets = {
                "records": macro_benchmark.Dataset(
                    "records", data, 1, data.stat().st_size,
                    macro_benchmark.sha256_file(data)
                )
            }
            before = macro_benchmark.dataset_identity(datasets, manifest)
            data.write_bytes(b"[]\n")
            self.assertNotEqual(
                before, macro_benchmark.dataset_identity(datasets, manifest)
            )

    def test_macro_default_upstream_and_publication_gate_are_explicit(self) -> None:
        with mock.patch.dict(os.environ, {"DOTNETJQ_UPSTREAM": ""}):
            arguments = macro_benchmark.parse_arguments([])
        self.assertEqual(str(ROOT / "upstream/jq"), arguments.upstream)
        default = pathlib.Path("/tmp/default-macro-results")
        arguments.publishable = False
        arguments.non_publishable = True
        with self.assertRaises(macro_benchmark.BenchmarkError):
            macro_benchmark.publication_decision(arguments, False, default, default)
        arguments.non_publishable = False
        arguments.publishable = True
        with self.assertRaisesRegex(macro_benchmark.BenchmarkError, "--regenerate"):
            macro_benchmark.publication_decision(arguments, True, default, default)
        arguments.regenerate = True
        self.assertTrue(
            macro_benchmark.publication_decision(arguments, True, default, default)
        )

    def test_benchmark_paths_use_repository_defaults_and_explicit_overrides(self) -> None:
        for driver in (fixture_benchmark, macro_benchmark):
            with self.subTest(driver=driver.__name__):
                with mock.patch.dict(os.environ, {"DOTNETJQ_UPSTREAM": "", "DOTNETJQ_ORACLE": ""}):
                    arguments = driver.parse_arguments([])
                self.assertEqual(str(ROOT / "upstream/jq"), arguments.upstream)
                self.assertEqual(str(ROOT / "artifacts/test-assets/jq-1.8.2/oracle/jq"),
                                 arguments.native_executable)
                with mock.patch.dict(os.environ, {"DOTNETJQ_UPSTREAM": "configured/source",
                                                  "DOTNETJQ_ORACLE": "configured/oracle"}):
                    arguments = driver.parse_arguments([])
                    self.assertEqual("configured/source", arguments.upstream)
                    self.assertEqual("configured/oracle", arguments.native_executable)
                    arguments = driver.parse_arguments(["--upstream", "explicit/source",
                                                        "--native-executable", "explicit/oracle"])
                    self.assertEqual("explicit/source", arguments.upstream)
                    self.assertEqual("explicit/oracle", arguments.native_executable)


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("--require-pinned-upstream", action="store_true")
    arguments, unittest_arguments = parser.parse_known_args(argv)
    global REQUIRE_PINNED_UPSTREAM
    REQUIRE_PINNED_UPSTREAM = arguments.require_pinned_upstream
    program = unittest.main(
        argv=[sys.argv[0], *unittest_arguments], verbosity=2, exit=False
    )
    return 0 if program.result.wasSuccessful() else 1


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
