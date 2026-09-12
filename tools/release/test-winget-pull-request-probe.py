#!/usr/bin/env python3
"""Focused integration tests for probe-winget-pull-request.py."""

from __future__ import annotations

import base64
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path, PurePosixPath
import subprocess
import tempfile
import threading
import unittest
from urllib.parse import parse_qs, urlsplit


SCRIPT = Path(__file__).with_name("probe-winget-pull-request.py")
TOKEN = "test-winget-token"
SUBMITTER = "winget-bot"
PACKAGE_ID = "GtGOpen.DotNetJq"
VERSION = "1.0.0"
TITLE = f"DotNetJq {VERSION}"
PR_NUMBER = 42


class GitHubApi:
    def __init__(self) -> None:
        self.search_items: list[object] = []
        self.pull_requests: dict[int, object] = {}
        self.files: dict[int, list[object]] = {}
        self.blobs: dict[str, bytes] = {}
        self.seen_author_query = False
        self.published_contents: object | None = None
        self.request_order: list[str] = []

        api = self

        class Handler(BaseHTTPRequestHandler):
            def do_GET(self) -> None:  # noqa: N802 - BaseHTTPRequestHandler API
                if self.headers.get("Authorization") != f"Bearer {TOKEN}":
                    self.send_error(401)
                    return
                parsed = urlsplit(self.path)
                if parsed.path == "/user":
                    self._write_json({"login": SUBMITTER})
                    return
                if parsed.path == "/repos/microsoft/winget-pkgs/issues":
                    api.request_order.append("issues")
                    query = parse_qs(parsed.query)
                    if (
                        query.get("state") != ["open"]
                        or query.get("creator") != [SUBMITTER]
                        or query.get("sort") != ["created"]
                        or query.get("direction") != ["desc"]
                        or query.get("per_page") != ["100"]
                        or query.get("page") != ["1"]
                    ):
                        self.send_error(400)
                        return
                    api.seen_author_query = True
                    self._write_json(api.search_items)
                    return
                if parsed.path == "/repos/microsoft/winget-pkgs":
                    self._write_json({"default_branch": "master"})
                    return
                contents_prefix = "/repos/microsoft/winget-pkgs/contents/"
                if parsed.path.startswith(contents_prefix):
                    api.request_order.append("published")
                    expected_path = str(
                        PurePosixPath(
                            "manifests", "g", "GtGOpen", "DotNetJq", VERSION
                        )
                    )
                    query = parse_qs(parsed.query)
                    if (
                        parsed.path.removeprefix(contents_prefix) != expected_path
                        or query != {"ref": ["master"]}
                    ):
                        self.send_error(400)
                        return
                    if api.published_contents is None:
                        self.send_error(404)
                    else:
                        self._write_json(api.published_contents)
                    return
                pull_prefix = "/repos/microsoft/winget-pkgs/pulls/"
                if parsed.path.startswith(pull_prefix):
                    suffix = parsed.path.removeprefix(pull_prefix)
                    if suffix.endswith("/files"):
                        number_text = suffix.removesuffix("/files")
                        if not number_text.isdigit():
                            self.send_error(404)
                            return
                        query = parse_qs(parsed.query)
                        if query != {"per_page": ["100"], "page": ["1"]}:
                            self.send_error(400)
                            return
                        self._write_json(api.files.get(int(number_text), []))
                        return
                    if suffix.isdigit() and not parsed.query:
                        pull_request = api.pull_requests.get(int(suffix))
                        if pull_request is not None:
                            self._write_json(pull_request)
                            return
                blob_prefixes = (
                    f"/repos/{SUBMITTER}/winget-pkgs/git/blobs/",
                    "/repos/microsoft/winget-pkgs/git/blobs/",
                )
                blob_prefix = next(
                    (
                        prefix
                        for prefix in blob_prefixes
                        if parsed.path.startswith(prefix)
                    ),
                    None,
                )
                if blob_prefix is not None and not parsed.query:
                    sha = parsed.path.removeprefix(blob_prefix)
                    if sha in api.blobs:
                        content = api.blobs[sha]
                        self._write_json(
                            {
                                "sha": sha,
                                "encoding": "base64",
                                "size": len(content),
                                "content": base64.b64encode(content).decode("ascii"),
                            }
                        )
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
        self.manifests = self.root / "manifests"
        self.manifests.mkdir()
        self.contents = {
            f"{PACKAGE_ID}.yaml": b"PackageIdentifier: GtGOpen.DotNetJq\n",
            f"{PACKAGE_ID}.installer.yaml": b"ManifestType: installer\n",
            f"{PACKAGE_ID}.locale.en-US.yaml": b"ManifestType: defaultLocale\n",
        }
        for name, content in self.contents.items():
            (self.manifests / name).write_bytes(content)

    def tearDown(self) -> None:
        self.api.close()
        self.temporary.cleanup()

    @property
    def remote_root(self) -> PurePosixPath:
        return PurePosixPath(
            "manifests", "g", "GtGOpen", "DotNetJq", VERSION
        )

    def add_pull_request(
        self,
        number: int = PR_NUMBER,
        *,
        title: str = TITLE,
        remote_version: str = VERSION,
        extra_path: str | None = None,
    ) -> None:
        self.api.search_items.append(
            {
                "number": number,
                "user": {"login": SUBMITTER},
                "pull_request": {
                    "url": (
                        f"{self.api.url}/repos/microsoft/winget-pkgs/pulls/{number}"
                    )
                },
            }
        )
        root = PurePosixPath(
            "manifests", "g", "GtGOpen", "DotNetJq", remote_version
        )
        file_records: list[object] = []
        for index, (name, content) in enumerate(self.contents.items(), start=1):
            sha = f"{number:02x}{index:038x}"
            self.api.blobs[sha] = content
            file_records.append(
                {
                    "filename": str(root / name),
                    "status": "added",
                    "sha": sha,
                }
            )
        if extra_path:
            sha = f"{number:02x}{99:038x}"
            self.api.blobs[sha] = b"extra\n"
            file_records.append(
                {"filename": extra_path, "status": "added", "sha": sha}
            )
        self.api.files[number] = file_records
        self.api.pull_requests[number] = {
            "number": number,
            "state": "open",
            "title": title,
            "html_url": (
                f"https://github.com/microsoft/winget-pkgs/pull/{number}"
            ),
            "user": {"login": SUBMITTER},
            "changed_files": len(file_records),
            "base": {
                "ref": "master",
                "repo": {"full_name": "microsoft/winget-pkgs"},
            },
            "head": {
                "ref": (
                    f"{PACKAGE_ID}-{VERSION}-"
                    "11111111-1111-1111-1111-111111111111"
                ),
                "user": {"login": SUBMITTER},
                "repo": {"full_name": f"{SUBMITTER}/winget-pkgs"},
            },
        }

    def add_published_manifests(self, *, extra_name: str | None = None) -> None:
        records: list[object] = []
        for index, (name, content) in enumerate(self.contents.items(), start=1):
            sha = f"{index:040x}"
            self.api.blobs[sha] = content
            records.append(
                {
                    "type": "file",
                    "name": name,
                    "path": str(self.remote_root / name),
                    "sha": sha,
                    "size": len(content),
                }
            )
        if extra_name is not None:
            content = b"extra\n"
            sha = f"{99:040x}"
            self.api.blobs[sha] = content
            records.append(
                {
                    "type": "file",
                    "name": extra_name,
                    "path": str(self.remote_root / extra_name),
                    "sha": sha,
                    "size": len(content),
                }
            )
        self.api.published_contents = records

    def run_probe(
        self, *, include_token: bool = True, pull_request_number: int | None = None
    ) -> subprocess.CompletedProcess[str]:
        output = self.root / "github-output"
        environment = os.environ.copy()
        environment["DOTNETJQ_RELEASE_TEST_MODE"] = "1"
        if include_token:
            environment["WINGET_CREATE_GITHUB_TOKEN"] = TOKEN
        else:
            environment.pop("WINGET_CREATE_GITHUB_TOKEN", None)
        arguments = [
                "python3",
                str(SCRIPT),
                "--package-id",
                PACKAGE_ID,
                "--version",
                VERSION,
                "--manifests",
                str(self.manifests),
                "--pr-title",
                TITLE,
                "--github-output",
                str(output),
                "--api-url",
                self.api.url,
            ]
        if pull_request_number is not None:
            arguments.extend(["--pull-request-number", str(pull_request_number)])
        result = subprocess.run(
            arguments,
            check=False,
            capture_output=True,
            text=True,
            env=environment,
        )
        result.github_output = (  # type: ignore[attr-defined]
            output.read_text(encoding="utf-8") if output.exists() else ""
        )
        return result

    def test_direct_post_submit_verification_avoids_search_index(self) -> None:
        self.add_pull_request(42)
        result = self.run_probe(pull_request_number=42)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(
            result.github_output,  # type: ignore[attr-defined]
            "state=exact-existing\n"
            f"submitter={SUBMITTER}\n"
            "pull_request_number=42\n"
            "pull_request_url=https://github.com/microsoft/winget-pkgs/pull/42\n",
        )
        self.assertFalse(self.api.seen_author_query)

    def test_unofficial_api_origin_is_rejected_before_token_transport(self) -> None:
        output = self.root / "unofficial-output"
        environment = os.environ.copy()
        environment["WINGET_CREATE_GITHUB_TOKEN"] = TOKEN
        result = subprocess.run(
            [
                "python3",
                str(SCRIPT),
                "--package-id",
                PACKAGE_ID,
                "--version",
                VERSION,
                "--manifests",
                str(self.manifests),
                "--pr-title",
                TITLE,
                "--github-output",
                str(output),
                "--api-url",
                "https://example.com",
            ],
            check=False,
            capture_output=True,
            text=True,
            env=environment,
        )
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("GitHub API URL is invalid", result.stderr)

    def test_missing_is_safe_to_submit(self) -> None:
        result = self.run_probe()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(
            result.github_output,  # type: ignore[attr-defined]
            f"state=missing\nsubmitter={SUBMITTER}\n",
        )
        self.assertTrue(self.api.seen_author_query)
        self.assertEqual(self.api.request_order, ["published", "issues"])

    def test_exact_published_verifies_inventory_and_bytes(self) -> None:
        self.add_published_manifests()
        result = self.run_probe()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(
            result.github_output,  # type: ignore[attr-defined]
            f"state=exact-published\nsubmitter={SUBMITTER}\n",
        )
        self.assertFalse(self.api.seen_author_query)
        self.assertEqual(self.api.request_order, ["published"])

    def test_published_manifest_byte_mismatch_fails_closed(self) -> None:
        self.add_published_manifests()
        first_sha = self.api.published_contents[0]["sha"]  # type: ignore[index]
        original = self.api.blobs[first_sha]
        self.api.blobs[first_sha] = bytes([original[0] ^ 1]) + original[1:]
        result = self.run_probe()
        self.assertEqual(result.returncode, 1)
        self.assertIn("published manifest bytes differ", result.stderr)
        self.assertFalse(self.api.seen_author_query)
        self.assertEqual(result.github_output, "")  # type: ignore[attr-defined]

    def test_published_manifest_extra_path_fails_closed(self) -> None:
        self.add_published_manifests(extra_name="unexpected.yaml")
        result = self.run_probe()
        self.assertEqual(result.returncode, 1)
        self.assertIn("published manifest path inventory differs", result.stderr)
        self.assertFalse(self.api.seen_author_query)

    def test_published_manifest_malformed_metadata_fails_closed(self) -> None:
        self.add_published_manifests()
        self.api.published_contents[0]["type"] = "dir"  # type: ignore[index]
        result = self.run_probe()
        self.assertEqual(result.returncode, 1)
        self.assertIn("contains malformed metadata", result.stderr)
        self.assertFalse(self.api.seen_author_query)

    def test_exact_existing_verifies_paths_and_bytes(self) -> None:
        self.add_pull_request()
        result = self.run_probe()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(
            result.github_output,  # type: ignore[attr-defined]
            (
                f"state=exact-existing\nsubmitter={SUBMITTER}\n"
                f"pull_request_number={PR_NUMBER}\n"
                "pull_request_url=https://github.com/microsoft/winget-pkgs/"
                f"pull/{PR_NUMBER}\n"
            ),
        )

    def test_existing_manifest_byte_mismatch_fails_closed(self) -> None:
        self.add_pull_request()
        first_sha = self.api.files[PR_NUMBER][0]["sha"]  # type: ignore[index]
        self.api.blobs[first_sha] = b"different\n"
        result = self.run_probe()
        self.assertEqual(result.returncode, 1)
        self.assertIn("manifest bytes differ", result.stderr)
        self.assertEqual(result.github_output, "")  # type: ignore[attr-defined]

    def test_existing_extra_path_fails_closed(self) -> None:
        self.add_pull_request(extra_path="README.md")
        result = self.run_probe()
        self.assertEqual(result.returncode, 1)
        self.assertIn("path inventory differs", result.stderr)

    def test_same_package_different_version_fails_closed(self) -> None:
        self.add_pull_request(
            title="DotNetJq 0.9.0", remote_version="0.9.0"
        )
        result = self.run_probe()
        self.assertEqual(result.returncode, 1)
        self.assertIn("candidate pull request #42 title differs", result.stderr)

    def test_expected_title_with_unrelated_paths_fails_closed(self) -> None:
        self.add_pull_request()
        files = self.api.files[PR_NUMBER]
        for index, item in enumerate(files):
            item["filename"] = f"manifests/o/Other/Package/1.0.0/file-{index}.yaml"  # type: ignore[index]
        result = self.run_probe()
        self.assertEqual(result.returncode, 1)
        self.assertIn("path inventory differs", result.stderr)

    def test_multiple_candidate_pull_requests_fail_closed(self) -> None:
        self.add_pull_request(PR_NUMBER)
        self.add_pull_request(PR_NUMBER + 1)
        result = self.run_probe()
        self.assertEqual(result.returncode, 1)
        self.assertIn("state is ambiguous", result.stderr)

    def test_unrelated_pull_request_is_ignored(self) -> None:
        self.add_pull_request(title="Other.Package 2.0.0")
        files = self.api.files[PR_NUMBER]
        for index, item in enumerate(files):
            item["filename"] = f"manifests/o/Other/Package/2.0.0/file-{index}.yaml"  # type: ignore[index]
        result = self.run_probe()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("state=missing\n", result.github_output)  # type: ignore[attr-defined]

    def test_ordinary_issue_by_submitter_is_ignored(self) -> None:
        self.api.search_items.append(
            {"number": 7, "user": {"login": SUBMITTER}, "title": "question"}
        )
        result = self.run_probe()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("state=missing\n", result.github_output)  # type: ignore[attr-defined]

    def test_local_manifest_inventory_mismatch_fails_before_api(self) -> None:
        (self.manifests / "unexpected.yaml").write_text("unexpected\n")
        result = self.run_probe()
        self.assertEqual(result.returncode, 1)
        self.assertIn("local manifest inventory differs", result.stderr)
        self.assertFalse(self.api.seen_author_query)

    def test_missing_token_fails_closed(self) -> None:
        result = self.run_probe(include_token=False)
        self.assertEqual(result.returncode, 1)
        self.assertIn("WINGET_CREATE_GITHUB_TOKEN is required", result.stderr)
        self.assertEqual(result.github_output, "")  # type: ignore[attr-defined]


if __name__ == "__main__":
    unittest.main(verbosity=2)
