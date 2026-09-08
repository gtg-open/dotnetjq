#!/usr/bin/env python3
"""Focused integration tests for probe-github-release.py."""

from __future__ import annotations

from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import subprocess
import tempfile
import threading
import unittest
from urllib.parse import parse_qs, urlsplit


SCRIPT = Path(__file__).with_name("probe-github-release.py")
VERIFIER = Path(__file__).with_name("verify-github-release-assets.py")
REPOSITORY = "gtg-open/dotnetjq"
TAG = "v1.0.0"
NAME = "DotNetJq 1.0.0 — jq 1.8.2 compatible"
TOKEN = "test-token"
BODY = "# DotNetJq 1.0.0\n\nDeterministic release notes.\n"


class GitHubApi:
    def __init__(self) -> None:
        self.releases: list[object] = []
        self.assets: dict[str, bytes] = {}

        api = self

        class Handler(BaseHTTPRequestHandler):
            def do_GET(self) -> None:  # noqa: N802 - BaseHTTPRequestHandler API
                if self.headers.get("Authorization") != f"Bearer {TOKEN}":
                    self.send_error(401)
                    return
                parsed = urlsplit(self.path)
                if parsed.path == "/repos/gtg-open/dotnetjq/releases":
                    query = parse_qs(parsed.query)
                    if query != {"per_page": ["100"], "page": ["1"]}:
                        self.send_error(400)
                        return
                    self._write_json(api.releases)
                    return
                prefix = "/repos/gtg-open/dotnetjq/releases/assets/"
                if parsed.path.startswith(prefix) and not parsed.query:
                    identifier = parsed.path.removeprefix(prefix)
                    if identifier in api.assets:
                        payload = api.assets[identifier]
                        self.send_response(200)
                        self.send_header("Content-Type", "application/octet-stream")
                        self.send_header("Content-Length", str(len(payload)))
                        self.end_headers()
                        self.wfile.write(payload)
                        return
                self.send_error(404)

            def _write_json(self, value: object) -> None:
                payload = json.dumps(value).encode("utf-8")
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(payload)))
                self.end_headers()
                self.wfile.write(payload)

            def log_message(self, format: str, *args: object) -> None:
                pass

        self.server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()

    @property
    def url(self) -> str:
        host, port = self.server.server_address
        return f"http://{host}:{port}"

    def close(self) -> None:
        self.server.shutdown()
        self.server.server_close()
        self.thread.join()


class ProbeTests(unittest.TestCase):
    def setUp(self) -> None:
        self.api = GitHubApi()
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.artifacts = self.root / "artifacts"
        self.artifacts.mkdir()
        self.body_file = self.root / "release-notes.md"
        self.body_file.write_text(BODY, encoding="utf-8", newline="\n")
        self.contents = {
            "alpha.txt": b"alpha\n",
            "beta.bin": b"\x00\x01\xff",
            **{f"fixture-{index:02}.txt": f"fixture {index}\n".encode() for index in range(25)},
        }
        for name, content in self.contents.items():
            (self.artifacts / name).write_bytes(content)

    def tearDown(self) -> None:
        self.api.close()
        self.temporary.cleanup()

    def release(
        self,
        *,
        draft: bool,
        name: str = NAME,
        body: str = BODY,
        prerelease: bool = False,
        tag: str = TAG,
        immutable: bool | None = None,
    ) -> dict[str, object]:
        assets: list[dict[str, object]] = []
        for index, (asset_name, content) in enumerate(self.contents.items(), start=1):
            identifier = str(index)
            self.api.assets[identifier] = content
            assets.append(
                {
                    "name": asset_name,
                    "size": len(content),
                    "state": "uploaded",
                    "url": f"{self.api.url}/repos/{REPOSITORY}/releases/assets/{identifier}",
                }
            )
        return {
            "tag_name": tag,
            "name": name,
            "body": body,
            "draft": draft,
            "immutable": not draft if immutable is None else immutable,
            "prerelease": prerelease,
            "assets": assets,
        }

    def run_probe(
        self, prerelease: str = "false", api_url: str | None = None
    ) -> subprocess.CompletedProcess[str]:
        output = self.root / "github-output"
        environment = os.environ.copy()
        environment["GITHUB_TOKEN"] = TOKEN
        environment["DOTNETJQ_RELEASE_TEST_MODE"] = "1"
        result = subprocess.run(
            [
                "python3",
                str(SCRIPT),
                "--repository",
                REPOSITORY,
                "--tag",
                TAG,
                "--name",
                NAME,
                "--body-file",
                str(self.body_file),
                "--artifacts",
                str(self.artifacts),
                "--prerelease",
                prerelease,
                "--github-output",
                str(output),
                "--api-url",
                api_url or self.api.url,
            ],
            check=False,
            capture_output=True,
            text=True,
            env=environment,
        )
        result.github_output = output.read_text(encoding="utf-8") if output.exists() else ""  # type: ignore[attr-defined]
        return result

    def run_verifier(
        self, *, api_url: str | None = None, state: str = "draft"
    ) -> subprocess.CompletedProcess[str]:
        environment = os.environ.copy()
        environment["GITHUB_TOKEN"] = TOKEN
        environment["DOTNETJQ_RELEASE_TEST_MODE"] = "1"
        return subprocess.run(
            [
                "python3",
                str(VERIFIER),
                "--repository",
                REPOSITORY,
                "--tag",
                TAG,
                "--name",
                NAME,
                "--body-file",
                str(self.body_file),
                "--artifacts",
                str(self.artifacts),
                "--state",
                state,
                "--prerelease",
                "false",
                "--api-url",
                api_url or self.api.url,
            ],
            check=False,
            capture_output=True,
            text=True,
            env=environment,
        )

    def test_post_stage_verifier_accepts_exact_release(self) -> None:
        self.api.releases = [self.release(draft=True)]
        result = self.run_verifier()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("27/27 exact assets", result.stdout)

    def test_post_stage_verifier_rejects_body_mismatch(self) -> None:
        self.api.releases = [self.release(draft=True, body="wrong\n")]
        result = self.run_verifier()
        self.assertEqual(result.returncode, 1)
        self.assertIn("unexpected body", result.stderr)

    def test_post_stage_verifier_rejects_unofficial_api_origin(self) -> None:
        result = self.run_verifier(api_url="https://example.com")
        self.assertEqual(result.returncode, 1)
        self.assertIn("GitHub API URL is invalid", result.stderr)

    def test_post_publication_verifier_rejects_mutable_release(self) -> None:
        self.api.releases = [self.release(draft=False, immutable=False)]
        result = self.run_verifier(state="published")
        self.assertEqual(result.returncode, 1)
        self.assertIn("published release v1.0.0 is not immutable", result.stderr)

    def test_non_loopback_http_api_is_rejected_before_token_transport(self) -> None:
        result = self.run_probe(api_url="http://example.com")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("GitHub API URL is invalid", result.stderr)

    def test_unofficial_https_api_is_rejected_before_token_transport(self) -> None:
        result = self.run_probe(api_url="https://example.com")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("GitHub API URL is invalid", result.stderr)

    def test_missing_release_is_safe_to_create(self) -> None:
        result = self.run_probe()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(result.github_output, "state=missing\nsuperseded=false\n")  # type: ignore[attr-defined]

    def test_exact_draft_is_safe_to_reuse(self) -> None:
        self.api.releases = [self.release(draft=True)]
        result = self.run_probe()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(result.github_output, "state=exact-draft\nsuperseded=false\n")  # type: ignore[attr-defined]

    def test_exact_subset_draft_is_safe_to_resume(self) -> None:
        release = self.release(draft=True)
        release["assets"] = release["assets"][:7]  # type: ignore[index]
        self.api.releases = [release]
        result = self.run_probe()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(result.github_output, "state=partial-draft\nsuperseded=false\n")  # type: ignore[attr-defined]

    def test_subset_draft_byte_mismatch_fails_closed(self) -> None:
        release = self.release(draft=True)
        release["assets"] = release["assets"][:7]  # type: ignore[index]
        self.api.assets["1"] = b"wrong\n"
        self.api.releases = [release]
        result = self.run_probe()
        self.assertEqual(result.returncode, 1)
        self.assertIn("asset SHA-256 differs", result.stderr)
        self.assertEqual(result.github_output, "state=mismatched\nsuperseded=false\n")  # type: ignore[attr-defined]

    def test_exact_published_release_is_distinguished(self) -> None:
        self.api.releases = [self.release(draft=False)]
        result = self.run_probe()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(result.github_output, "state=published\nsuperseded=false\n")  # type: ignore[attr-defined]

    def test_mutable_published_release_fails_closed(self) -> None:
        self.api.releases = [self.release(draft=False, immutable=False)]
        result = self.run_probe()
        self.assertEqual(result.returncode, 1)
        self.assertIn("existing published release is not immutable", result.stderr)
        self.assertEqual(result.github_output, "state=mismatched\nsuperseded=false\n")  # type: ignore[attr-defined]

    def test_immutable_draft_release_fails_closed(self) -> None:
        self.api.releases = [self.release(draft=True, immutable=True)]
        result = self.run_probe()
        self.assertEqual(result.returncode, 1)
        self.assertIn("draft release unexpectedly reports immutable", result.stderr)
        self.assertEqual(result.github_output, "state=mismatched\nsuperseded=false\n")  # type: ignore[attr-defined]

    def test_partial_published_release_fails_closed(self) -> None:
        release = self.release(draft=False)
        release["assets"] = release["assets"][:7]  # type: ignore[index]
        self.api.releases = [release]
        result = self.run_probe()
        self.assertEqual(result.returncode, 1)
        self.assertIn("asset inventory differs", result.stderr)
        self.assertEqual(result.github_output, "state=mismatched\nsuperseded=false\n")  # type: ignore[attr-defined]

    def test_metadata_mismatch_fails_closed(self) -> None:
        self.api.releases = [self.release(draft=True, name="unexpected")]
        result = self.run_probe()
        self.assertEqual(result.returncode, 1)
        self.assertIn("release name differs", result.stderr)
        self.assertEqual(result.github_output, "state=mismatched\nsuperseded=false\n")  # type: ignore[attr-defined]

    def test_release_body_mismatch_fails_closed(self) -> None:
        self.api.releases = [self.release(draft=True, body="misleading notes\n")]
        result = self.run_probe()
        self.assertEqual(result.returncode, 1)
        self.assertIn("release body differs", result.stderr)
        self.assertEqual(result.github_output, "state=mismatched\nsuperseded=false\n")  # type: ignore[attr-defined]

    def test_extra_asset_inventory_fails_closed(self) -> None:
        release = self.release(draft=True)
        release["assets"].append(  # type: ignore[union-attr]
            {
                "name": "unexpected.txt",
                "size": 11,
                "state": "uploaded",
                "url": f"{self.api.url}/repos/{REPOSITORY}/releases/assets/extra",
            }
        )
        self.api.releases = [release]
        result = self.run_probe()
        self.assertEqual(result.returncode, 1)
        self.assertIn("asset inventory differs", result.stderr)
        self.assertEqual(result.github_output, "state=mismatched\nsuperseded=false\n")  # type: ignore[attr-defined]

    def test_asset_byte_mismatch_fails_closed(self) -> None:
        self.api.releases = [self.release(draft=True)]
        self.api.assets["1"] = b"wrong\n"
        result = self.run_probe()
        self.assertEqual(result.returncode, 1)
        self.assertIn("asset SHA-256 differs", result.stderr)
        self.assertEqual(result.github_output, "state=mismatched\nsuperseded=false\n")  # type: ignore[attr-defined]

    def test_duplicate_release_records_fail_closed(self) -> None:
        self.api.releases = [self.release(draft=True), self.release(draft=True)]
        result = self.run_probe()
        self.assertEqual(result.returncode, 1)
        self.assertIn("state is ambiguous", result.stderr)
        self.assertEqual(result.github_output, "state=mismatched\nsuperseded=false\n")  # type: ignore[attr-defined]

    def test_newer_published_stable_release_supersedes_target(self) -> None:
        self.api.releases = [
            self.release(draft=True),
            self.release(draft=False, tag="v1.0.1"),
        ]
        result = self.run_probe()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(
            result.github_output,
            "state=exact-draft\nsuperseded=true\n",
        )  # type: ignore[attr-defined]

    def test_older_published_stable_release_does_not_supersede_target(self) -> None:
        self.api.releases = [
            self.release(draft=True),
            self.release(draft=False, tag="v0.999.999"),
        ]
        result = self.run_probe()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(
            result.github_output,
            "state=exact-draft\nsuperseded=false\n",
        )  # type: ignore[attr-defined]

    def test_drafts_and_prereleases_do_not_supersede_target(self) -> None:
        self.api.releases = [
            self.release(draft=True),
            self.release(draft=True, tag="vmalformed-draft"),
            self.release(
                draft=False,
                prerelease=True,
                tag="vmalformed-prerelease",
            ),
        ]
        result = self.run_probe()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(
            result.github_output,
            "state=exact-draft\nsuperseded=false\n",
        )  # type: ignore[attr-defined]

    def test_malformed_published_stable_tag_fails_closed(self) -> None:
        self.api.releases = [
            self.release(draft=True),
            self.release(draft=False, tag="v01.0.1"),
        ]
        result = self.run_probe()
        self.assertEqual(result.returncode, 1)
        self.assertIn("not strict SemVer", result.stderr)
        self.assertEqual(result.github_output, "")  # type: ignore[attr-defined]


if __name__ == "__main__":
    unittest.main(verbosity=2)
