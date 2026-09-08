"""Fail-closed publication wiring, without adding a YAML runtime dependency."""
from pathlib import Path
import os
import re
import shutil
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
WORKFLOWS = (
    "release-cli.yml", "cli-cross-platform.yml", "semantic-compatibility.yml",
    "parser-generation-check.yml", "performance-pair-check.yml",
)


def workflow_jobs(filename):
    """Read the deliberately fixed, reviewed job/step indentation in these files."""
    text = (ROOT / ".github/workflows" / filename).read_text(encoding="utf-8")
    for job in re.split(r"(?=^  [a-z][a-z0-9-]*:\n)", text, flags=re.M):
        if "\n    steps:\n" not in job:
            continue
        header, body = job.split("\n    steps:\n", 1)
        blocks = re.split(r"(?=^      - )", body, flags=re.M)
        yield header.split(":", 1)[0].strip(), [block for block in blocks if block.strip()]


def field(step, name):
    match = re.search(r"^        " + re.escape(name) + r": (.*)$", step, re.M)
    return match.group(1) if match else None


class WorkflowPrivacyTests(unittest.TestCase):
    def test_removing_an_unpublished_tag_cannot_start_publication(self):
        text = (ROOT / ".github/workflows/release-cli.yml").read_text(encoding="utf-8")
        validation = text.split("  validate-tag:\n", 1)[1].split("  semantic-compatibility:\n", 1)[0]
        self.assertIn("    if: github.event.deleted == false\n", validation)

    def test_all_build_and_publication_jobs_scan_full_source_history_first(self):
        for filename in WORKFLOWS:
            for name, steps in workflow_jobs(filename):
                with self.subTest(workflow=filename, job=name):
                    self.assertIn("uses: actions/checkout@", steps[0])
                    self.assertIn("fetch-depth: 0", steps[0])
                    self.assertIn("persist-credentials: false", steps[0])
                    self.assertIn("uses: actions/setup-python@", steps[1])
                    self.assertEqual(field(steps[2], "id"), "privacy-source")
                    self.assertIn("tools/privacy/check.py", steps[2])
                    self.assertIn("--history", steps[2])
                    self.assertNotIn("--github-hosted", steps[2])
                    self.assertIsNone(field(steps[2], "if"))
                    if "path: source" in steps[0]:
                        self.assertIn("source/tools/privacy/check.py --repo source", steps[2])
                    else:
                        self.assertNotIn("--repo", steps[2])

    def test_every_artifact_upload_requires_its_own_successful_output_scan(self):
        uploads = 0
        for filename in WORKFLOWS:
            for name, steps in workflow_jobs(filename):
                for index, step in enumerate(steps):
                    if "uses: actions/upload-artifact@" not in step:
                        continue
                    uploads += 1
                    with self.subTest(workflow=filename, job=name, upload=index):
                        scan = steps[index - 1]
                        scan_id = field(scan, "id")
                        self.assertTrue(scan_id and scan_id.startswith("privacy-artifacts-"))
                        self.assertIn("tools/privacy/check.py --metadata", scan)
                        self.assertIn("--github-hosted", scan)
                        self.assertIn("--output", scan)
                        self.assertIn(f"steps.{scan_id}.outcome == 'success'", field(step, "if"))
                        for mode in ("always()", "failure()"):
                            if mode in (field(step, "if") or ""):
                                self.assertIn(mode, field(scan, "if"))
                                self.assertIn("steps.privacy-source.outcome == 'success'", field(scan, "if"))
        self.assertEqual(uploads, 11)

    def test_artifact_downloads_are_reinspected_before_consumption(self):
        for name, steps in workflow_jobs("release-cli.yml"):
            for index, step in enumerate(steps):
                if "uses: actions/download-artifact@" not in step:
                    continue
                with self.subTest(job=name, download=index):
                    path = re.search(r"^          path: (.+)$", step, re.M).group(1)
                    self.assertIn(f"--output {path}", steps[index + 1])
                    self.assertIn("tools/privacy/check.py", steps[index + 1])
                    self.assertIn("--github-hosted", steps[index + 1])

    def test_preflight_and_each_external_publication_boundary_rescan_the_bundle(self):
        jobs = dict(workflow_jobs("release-cli.yml"))
        for name in ("publication-preflight", "attest", "stage-github-release",
                     "publish-core", "homebrew", "winget"):
            with self.subTest(job=name):
                scans = [step for step in jobs[name] if "tools/privacy/check.py" in step]
                self.assertTrue(any("--output artifacts/bundle" in step for step in scans))
                for step in scans:
                    if "--output" in step:
                        self.assertIn("--github-hosted", step)
        for name in ("stage-github-release", "publish-core"):
            self.assertTrue(any('--output "$RUNNER_TEMP/dotnetjq-release-notes.md"' in step
                                for step in jobs[name]))
        self.assertTrue(any("--output tap/Formula/dotnetjq.rb" in step for step in jobs["homebrew"]))
        self.assertTrue(any("--output artifacts/winget" in step for step in jobs["winget"]))

    def test_privacy_scan_failures_are_never_ignored(self):
        for filename in WORKFLOWS:
            for name, steps in workflow_jobs(filename):
                for step in steps:
                    if "tools/privacy/check.py" not in step:
                        continue
                    with self.subTest(workflow=filename, job=name):
                        self.assertNotIn("continue-on-error", step)
                        self.assertNotIn("|| true", step)
                        self.assertNotIn("set +e", step)

    def test_optional_evidence_scan_rejects_disclosures_and_accepts_absent_outputs(self):
        steps = dict(workflow_jobs("performance-pair-check.yml"))["compare"]
        scan = next(step for step in steps if field(step, "id") == "privacy-artifacts-1")
        script = "\n".join(line[10:] for line in scan.split("        run: |\n", 1)[1].splitlines())
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "tools/privacy").mkdir(parents=True)
            shutil.copyfile(ROOT / "tools/privacy/check.py", root / "tools/privacy/check.py")
            (root / "README.md").write_text("Synthetic workflow test.\n", encoding="utf-8")
            environment = os.environ.copy()
            environment.update({
                "GITHUB_ACTIONS": "true", "RUNNER_ENVIRONMENT": "github-hosted",
                "GIT_CONFIG_NOSYSTEM": "1", "GIT_CONFIG_GLOBAL": os.devnull,
                "GIT_AUTHOR_NAME": "GtG Open Maintainer", "GIT_COMMITTER_NAME": "GtG Open Maintainer",
                "GIT_AUTHOR_EMAIL": "325667272+gtg-open-maintainer@users.noreply.github.com",
                "GIT_COMMITTER_EMAIL": "325667272+gtg-open-maintainer@users.noreply.github.com",
            })
            for arguments in (("init", "--quiet"), ("add", "README.md"), ("commit", "--quiet", "-m", "Safe fixture")):
                subprocess.run(["git", *arguments], cwd=root, env=environment, check=True, capture_output=True)

            def run():
                return subprocess.run(["bash", "--noprofile", "--norc", "-e", "-o", "pipefail", "-c", script],
                                      cwd=root, env=environment, text=True, capture_output=True)

            self.assertEqual(run().returncode, 0, "Missing optional failure reports should retain upload warning behavior")
            reports = root / "artifacts/performance/results"
            reports.mkdir(parents=True)
            report = reports / "results.json"
            report.write_text('{"result": "clean"}', encoding="utf-8")
            self.assertEqual(run().returncode, 0)
            report.write_text('/' + 'home' + '/invented-contributor/private-output', encoding="utf-8")
            result = run()
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("workstation-home-path", result.stderr)
            self.assertNotIn("invented-contributor", result.stderr)


if __name__ == "__main__":
    unittest.main()
