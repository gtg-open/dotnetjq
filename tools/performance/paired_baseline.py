#!/usr/bin/env python3
"""Source-bound baseline subjects measured inside the candidate benchmark process."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess

import build_attestation as provenance

BASELINE_NAMES = ("baseline-framework-dotnetjq", "baseline-aot-dotnetjq")
DEPLOYMENTS = ("framework-dotnetjq", "aot-dotnetjq")
MODE = "same-process-interleaved"


def policy_path() -> Path:
    return Path(__file__).resolve().with_name("release-thresholds.json")


def load_policy() -> dict:
    policy = json.loads(policy_path().read_text(encoding="utf-8"))
    if policy.get("schema_version") != 2:
        raise provenance.AttestationError("same-runner performance policy must have schema 2")
    return policy


def host_session() -> dict:
    # A CPU model/OS label does not identify a particular VM. The Linux boot ID
    # binds both reports to the same running host without exposing its raw ID.
    boot = Path("/proc/sys/kernel/random/boot_id").read_bytes().strip()
    if not boot:
        raise provenance.AttestationError("same-runner host identity is unavailable")
    return {"boot_sha256": hashlib.sha256(boot).hexdigest(),
            "github_run_id": os.environ.get("GITHUB_RUN_ID"),
            "github_run_attempt": os.environ.get("GITHUB_RUN_ATTEMPT"),
            "github_job": os.environ.get("GITHUB_JOB"),
            "affinity": sorted(os.sched_getaffinity(0))}


class PairedBaseline:
    def __init__(self, root: Path, candidate_root: Path):
        self.root = root.resolve(strict=True)
        if self.root == candidate_root.resolve():
            raise provenance.AttestationError("baseline must be a separate pinned checkout")
        self.policy = load_policy()
        self.policy_sha256 = provenance.sha256_file(policy_path())
        self.source = provenance.source_snapshot(self.root)
        expected = self.policy["baseline"]["commit"]
        if self.source["git_head"] != expected or self.source["git_dirty"]:
            raise provenance.AttestationError("baseline checkout is dirty or not the pinned commit")
        self.paths = {deployment: self.root / provenance.EXPECTED_OUTPUTS[deployment] / "DotNetJq.Cli"
                      for deployment in DEPLOYMENTS}

    def implementations(self, implementation_type, framework_artifact_paths):
        framework, aot = (self.paths[name] for name in DEPLOYMENTS)
        return [implementation_type(BASELINE_NAMES[0], (str(framework),),
                                    framework_artifact_paths(framework)),
                implementation_type(BASELINE_NAMES[1], (str(aot),), (aot,))]

    def verify(self, toolchain: dict) -> dict:
        if provenance.source_snapshot(self.root) != self.source:
            raise provenance.AttestationError("baseline source changed during comparison")
        if provenance.sha256_file(policy_path()) != self.policy_sha256:
            raise provenance.AttestationError("performance policy changed during comparison")
        attestations = [provenance.validate_attestation(
            self.root, name, self.paths[name], self.source, toolchain)
            for name in DEPLOYMENTS]
        return {"mode": MODE, "commit": self.source["git_head"],
                "source": self.source, "policy_sha256": self.policy_sha256,
                "attestations": attestations, "host_session": host_session()}


def optional_baseline(arguments, candidate_root: Path):
    value = getattr(arguments, "baseline_root", None)
    return PairedBaseline(Path(value), candidate_root) if value else None


def validate_paths(implementations) -> None:
    paths = [item.artifact_paths[0].resolve() for item in implementations]
    if len(set(paths)) != len(paths):
        raise provenance.AttestationError("baseline/candidate executable paths must be distinct")


def validate_configurations(configurations: dict[str, str]) -> None:
    for name, engine in zip(BASELINE_NAMES, ("CoreCLR", "NativeAOT"), strict=True):
        expected = f"net10.0; {engine}; linux-x64; jq-1.8.2 compatible"
        if configurations.get(name) != expected:
            raise provenance.AttestationError(f"{name}: unexpected build configuration")


def build(root: Path) -> None:
    baseline = PairedBaseline(root, Path(__file__).resolve().parents[2])
    build_environment = os.environ.copy()
    build_environment["DOTNETJQ_SOURCE_REVISION"] = baseline.source["git_head"]
    build_environment["DOTNETJQ_BUILD_NUMBER"] = "0"
    for deployment in DEPLOYMENTS:
        provenance.prepare_output(baseline.root, deployment)
        snapshot = baseline.root / "artifacts/performance" / f"{deployment}-prebuild.json"
        provenance.create_snapshot(baseline.root, snapshot)
        command = provenance.EXPECTED_PUBLISH_COMMANDS[deployment]
        subprocess.run(command, cwd=baseline.root, env=build_environment, check=True)
        provenance.create_attestation(baseline.root, snapshot, deployment, command)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--build", type=Path, required=True)
    build(parser.parse_args().build)
