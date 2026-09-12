#!/usr/bin/env python3
"""Regression tests for fail-closed, resumable NuGet publication."""

from __future__ import annotations

import hashlib
import http.server
import os
import shutil
import subprocess
import tempfile
import threading
import urllib.parse
import zipfile
from pathlib import Path


SCRIPT_DIR = Path(__file__).resolve().parent
REPOSITORY_ROOT = SCRIPT_DIR.parent.parent
PUBLISHER = SCRIPT_DIR / "publish-nuget-plan.sh"
PAYLOAD_VERIFIER = SCRIPT_DIR / "verify-nuget-package-payload.py"
FAKE_DOTNET = SCRIPT_DIR / "test-fixtures" / "fake-nuget-dotnet.py"
VERSION = "1.2.3"
EXPECTED_OWNER = "gtg-open"


class FlatContainerHandler(http.server.BaseHTTPRequestHandler):
    storage: Path
    requests: list[str]

    def do_GET(self) -> None:  # noqa: N802 - BaseHTTPRequestHandler API
        path = urllib.parse.urlsplit(self.path).path
        type(self).requests.append(path)
        prefix = "/v3-flatcontainer/"
        if not path.startswith(prefix):
            self.send_error(404)
            return
        relative = Path(path.removeprefix(prefix))
        if relative.is_absolute() or ".." in relative.parts:
            self.send_error(400)
            return
        package = type(self).storage / relative
        if not package.is_file():
            self.send_error(404)
            return
        content = package.read_bytes()
        self.send_response(200)
        self.send_header("Content-Type", "application/octet-stream")
        self.send_header("Content-Length", str(len(content)))
        self.end_headers()
        self.wfile.write(content)

    def log_message(self, format: str, *args: object) -> None:
        pass


def make_package(path: Path, package_id: str, version: str, payload: bytes) -> None:
    nuspec = (
        '<?xml version="1.0" encoding="utf-8"?>'
        '<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">'
        "<metadata>"
        f"<id>{package_id}</id><version>{version}</version>"
        '<authors>fixture</authors><description>fixture</description>'
        "</metadata></package>"
    ).encode("utf-8")
    with zipfile.ZipFile(path, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        archive.writestr(f"{package_id}.nuspec", nuspec)
        archive.writestr("payload.txt", payload)


def signed_copy(source: Path, destination: Path) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(source, "r") as source_archive, zipfile.ZipFile(
        destination, "w"
    ) as destination_archive:
        for entry in source_archive.infolist():
            destination_archive.writestr(entry, source_archive.read(entry))
        destination_archive.writestr(".signature.p7s", b"fixture repository signature")


def remote_path(storage: Path, package_id: str, version: str) -> Path:
    lower_id = package_id.lower()
    lower_version = version.lower()
    return storage / lower_id / lower_version / f"{lower_id}.{lower_version}.nupkg"


def payload_fingerprint(package: Path, package_id: str) -> str:
    return subprocess.check_output(
        [
            "python3",
            str(PAYLOAD_VERIFIER),
            "--package",
            str(package),
            "--package-id",
            package_id,
            "--version",
            VERSION,
            "--signature-policy",
            "absent",
        ],
        text=True,
    ).strip()


def run_publisher(
    directory: Path,
    plan: Path,
    base_url: str,
    environment: dict[str, str],
) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [
            "bash",
            str(PUBLISHER),
            "--directory",
            str(directory),
            "--plan",
            str(plan),
            "--source",
            f"{base_url}/v3/index.json",
            "--flat-container-base",
            f"{base_url}/v3-flatcontainer",
            "--expected-owner",
            EXPECTED_OWNER,
            "--push-attempts",
            "2",
            "--probe-attempts",
            "2",
            "--probe-delay",
            "0",
            "--push-timeout",
            "10",
        ],
        cwd=REPOSITORY_ROOT,
        env=environment,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        check=False,
    )


def require(condition: bool, message: str, result: subprocess.CompletedProcess[str] | None = None) -> None:
    if condition:
        return
    if result is not None:
        message += f"\n--- publisher output ---\n{result.stdout}"
    raise AssertionError(message)


def expected_url(package_id: str) -> str:
    lower_id = package_id.lower()
    return f"/v3-flatcontainer/{lower_id}/{VERSION}/{lower_id}.{VERSION}.nupkg"


def require_all_preflighted(package_ids: list[str]) -> None:
    requested = set(FlatContainerHandler.requests)
    missing = [package_id for package_id in package_ids if expected_url(package_id) not in requested]
    require(not missing, f"preflight did not probe all exact package IDs: {missing!r}")


def main() -> int:
    with tempfile.TemporaryDirectory(prefix="dotnetjq-nuget-publication-test.") as temporary:
        root = Path(temporary)
        packages = root / "packages"
        storage = root / "flat-container"
        state = root / "state"
        fake_bin = root / "bin"
        packages.mkdir()
        storage.mkdir()
        fake_bin.mkdir()
        push_log = root / "push.log"
        push_log.write_text("", encoding="utf-8")
        os.symlink(FAKE_DOTNET, fake_bin / "dotnet")

        rids = [
            line.split("\t", 1)[0]
            for line in (REPOSITORY_ROOT / "packaging" / "release-targets.tsv")
            .read_text(encoding="utf-8")
            .splitlines()
            if line and not line.startswith("#")
        ]
        require(len(rids) == 8, "publication fixture requires eight release RIDs")
        rows: list[tuple[str, str, str, Path]] = []
        package_ids = ["DotNetJq.Library"] + [f"dotnetjq.{rid}" for rid in rids] + ["dotnetjq"]
        kinds = ["LIBRARY"] + [f"RID:{rid}" for rid in rids] + ["POINTER_LAST"]
        for order, (kind, package_id) in enumerate(zip(kinds, package_ids), start=1):
            package = packages / f"{package_id}.{VERSION}.nupkg"
            make_package(package, package_id, VERSION, f"payload-{order}".encode("ascii"))
            rows.append((f"{order:02d}", kind, package_id, package))

        plan = packages / "nuget-publish-order.tsv"
        with plan.open("w", encoding="utf-8", newline="\n") as stream:
            stream.write(
                "# order\tkind\tpackage_id\tversion\tfile\tsha256\tpayload_sha256\n"
            )
            for order, kind, package_id, package in rows:
                sha256 = hashlib.sha256(package.read_bytes()).hexdigest()
                payload_sha256 = payload_fingerprint(package, package_id)
                stream.write(
                    f"{order}\t{kind}\t{package_id}\t{VERSION}\t{package.name}\t"
                    f"{sha256}\t{payload_sha256}\n"
                )

        FlatContainerHandler.storage = storage
        FlatContainerHandler.requests = []
        server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), FlatContainerHandler)
        server_thread = threading.Thread(target=server.serve_forever, daemon=True)
        server_thread.start()
        base_url = f"http://127.0.0.1:{server.server_port}"

        environment = os.environ.copy()
        for variable in (
            "FAKE_NUGET_AUTHOR_ONLY_ID",
            "FAKE_NUGET_OWNER_MISMATCH_ID",
            "FAKE_NUGET_FAIL_BEFORE_ACCEPT_ID",
            "FAKE_NUGET_ACCEPT_THEN_FAIL_ID",
        ):
            environment.pop(variable, None)
        environment.update(
            {
                "DOTNETJQ_RELEASE_TEST_MODE": "1",
                "NUGET_API_KEY": "fixture-key",
                "FAKE_NUGET_STORAGE": str(storage),
                "FAKE_NUGET_STATE": str(state),
                "FAKE_NUGET_PUSH_LOG": str(push_log),
                "PATH": f"{fake_bin}{os.pathsep}{environment['PATH']}",
            }
        )

        try:
            first_remote = remote_path(storage, package_ids[0], VERSION)
            signed_copy(rows[0][3], first_remote)

            FlatContainerHandler.requests.clear()
            author_only_environment = environment | {
                "FAKE_NUGET_AUTHOR_ONLY_ID": package_ids[0]
            }
            result = run_publisher(packages, plan, base_url, author_only_environment)
            require(result.returncode != 0, "author-only signature unexpectedly passed", result)
            require_all_preflighted(package_ids)
            require(push_log.read_text(encoding="utf-8") == "", "signature failure allowed a push", result)

            FlatContainerHandler.requests.clear()
            owner_mismatch_environment = environment | {
                "FAKE_NUGET_OWNER_MISMATCH_ID": package_ids[0]
            }
            result = run_publisher(packages, plan, base_url, owner_mismatch_environment)
            require(result.returncode != 0, "wrong repository owner unexpectedly passed", result)
            require_all_preflighted(package_ids)
            require(
                push_log.read_text(encoding="utf-8") == "",
                "repository-owner mismatch allowed a push",
                result,
            )

            mismatched = root / "mismatched.nupkg"
            make_package(mismatched, package_ids[0], VERSION, b"mismatched-payload")
            signed_copy(mismatched, first_remote)
            FlatContainerHandler.requests.clear()
            result = run_publisher(packages, plan, base_url, environment)
            require(result.returncode != 0, "mismatched remote package unexpectedly passed", result)
            require_all_preflighted(package_ids)
            require(push_log.read_text(encoding="utf-8") == "", "mismatch allowed a push", result)

            signed_copy(rows[0][3], first_remote)
            FlatContainerHandler.requests.clear()
            publication_environment = environment | {
                "FAKE_NUGET_FAIL_BEFORE_ACCEPT_ID": package_ids[1],
                "FAKE_NUGET_ACCEPT_THEN_FAIL_ID": package_ids[2],
            }
            result = run_publisher(packages, plan, base_url, publication_environment)
            require(result.returncode == 0, "resumable publication failed", result)
            pushes = push_log.read_text(encoding="utf-8").splitlines()
            expected_pushes = [package_ids[1], package_ids[1], *package_ids[2:]]
            require(pushes == expected_pushes, f"unexpected push order/retries: {pushes!r}", result)
            for _, _, package_id, package in rows:
                require(
                    remote_path(storage, package_id, VERSION).is_file(),
                    f"remote package missing after publication: {package_id}",
                    result,
                )

            push_count = len(pushes)
            FlatContainerHandler.requests.clear()
            verify_only_environment = environment.copy()
            verify_only_environment.pop("NUGET_API_KEY")
            result = run_publisher(packages, plan, base_url, verify_only_environment)
            require(result.returncode == 0, "all-present resumable rerun failed", result)
            require(
                len(push_log.read_text(encoding="utf-8").splitlines()) == push_count,
                "all-present rerun performed a duplicate push",
                result,
            )
            for package_id in package_ids:
                require(
                    FlatContainerHandler.requests.count(expected_url(package_id)) >= 2,
                    f"rerun did not preflight and finally reverify {package_id}",
                    result,
                )
        finally:
            server.shutdown()
            server.server_close()
            server_thread.join(timeout=5)

    print("NuGet publication state-machine tests passed (5/5)")
    return 0


raise SystemExit(main())
