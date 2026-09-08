#!/usr/bin/env python3
"""Verify that one GitHub release exactly matches a local release bundle."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import urllib.error
import urllib.parse
import urllib.request


class VerificationError(RuntimeError):
    """The remote release cannot be proven equal to the local bundle."""


class SafeRedirectHandler(urllib.request.HTTPRedirectHandler):
    """Never forward the workflow token to a cross-host asset redirect."""

    def redirect_request(self, request, file_pointer, code, message, headers, new_url):
        redirected = super().redirect_request(
            request, file_pointer, code, message, headers, new_url
        )
        if redirected is None:
            return None
        destination = urllib.parse.urlsplit(new_url)
        if destination.scheme != "https":
            raise VerificationError("release asset redirect must use HTTPS")
        source = urllib.parse.urlsplit(request.full_url)
        if (destination.scheme, destination.netloc) != (source.scheme, source.netloc):
            redirected.remove_header("Authorization")
            redirected.remove_header("X-GitHub-Api-Version")
        return redirected


class SameOriginRedirectHandler(urllib.request.HTTPRedirectHandler):
    """Reject API redirects before an authorization header can cross origin."""

    def redirect_request(self, request, file_pointer, code, message, headers, new_url):
        source = urllib.parse.urlsplit(request.full_url)
        destination = urllib.parse.urlsplit(new_url)
        if (destination.scheme, destination.netloc) != (source.scheme, source.netloc):
            raise VerificationError("GitHub API redirect crossed origin")
        return super().redirect_request(
            request, file_pointer, code, message, headers, new_url
        )


def request_json(url: str, token: str) -> object:
    request = urllib.request.Request(
        url,
        headers={
            "Accept": "application/vnd.github+json",
            "Authorization": f"Bearer {token}",
            "X-GitHub-Api-Version": "2022-11-28",
            "User-Agent": "dotnetjq-release-verifier",
        },
    )
    try:
        opener = urllib.request.build_opener(SameOriginRedirectHandler())
        with opener.open(request, timeout=60) as response:
            return json.load(response)
    except (OSError, urllib.error.HTTPError, json.JSONDecodeError) as error:
        raise VerificationError(f"GitHub API request failed for {url}: {error}") from error


def download_sha256(url: str, token: str) -> str:
    request = urllib.request.Request(
        url,
        headers={
            "Accept": "application/octet-stream",
            "Authorization": f"Bearer {token}",
            "X-GitHub-Api-Version": "2022-11-28",
            "User-Agent": "dotnetjq-release-verifier",
        },
    )
    digest = hashlib.sha256()
    opener = urllib.request.build_opener(SafeRedirectHandler())
    try:
        with opener.open(request, timeout=120) as response:
            while chunk := response.read(1024 * 1024):
                digest.update(chunk)
    except (OSError, urllib.error.HTTPError) as error:
        raise VerificationError(f"release asset download failed: {error}") from error
    return digest.hexdigest()


def local_assets(directory: Path) -> dict[str, Path]:
    if not directory.is_dir():
        raise VerificationError(f"artifact directory does not exist: {directory}")
    entries = list(directory.iterdir())
    unexpected = [entry for entry in entries if not entry.is_file() or entry.is_symlink()]
    if unexpected:
        raise VerificationError(
            "artifact directory contains a non-regular entry: "
            + ", ".join(str(entry) for entry in unexpected)
        )
    result = {entry.name: entry for entry in entries}
    if len(result) != 27:
        raise VerificationError(f"expected exactly 27 local release assets; found {len(result)}")
    return result


def local_release_body(path: Path) -> str:
    if not path.is_file() or path.is_symlink():
        raise VerificationError(f"release body is not a regular file: {path}")
    payload = path.read_bytes()
    if not payload or b"\r" in payload or not payload.endswith(b"\n"):
        raise VerificationError(
            "release body must be nonempty, LF-only, and newline-terminated"
        )
    try:
        return payload.decode("utf-8")
    except UnicodeDecodeError as error:
        raise VerificationError("release body is not valid UTF-8") from error


def find_release(api_url: str, repository: str, tag: str, token: str) -> dict[str, object]:
    encoded_repository = "/".join(urllib.parse.quote(part, safe="") for part in repository.split("/"))
    matches: list[dict[str, object]] = []
    for page in range(1, 101):
        url = f"{api_url}/repos/{encoded_repository}/releases?per_page=100&page={page}"
        payload = request_json(url, token)
        if not isinstance(payload, list):
            raise VerificationError("GitHub releases response is not a JSON array")
        for candidate in payload:
            if isinstance(candidate, dict) and candidate.get("tag_name") == tag:
                matches.append(candidate)
        if len(payload) < 100:
            break
    if len(matches) != 1:
        raise VerificationError(f"expected exactly one release for {tag}; found {len(matches)}")
    return matches[0]


def validate_api_url(value: str) -> str:
    api_url = value.rstrip("/")
    parsed = urllib.parse.urlsplit(api_url)
    loopback = parsed.hostname in {"127.0.0.1", "::1", "localhost"}
    structurally_valid = (
        bool(parsed.netloc)
        and not parsed.path
        and not parsed.query
        and not parsed.fragment
        and not parsed.username
        and not parsed.password
    )
    official = api_url == "https://api.github.com"
    test_fixture = (
        loopback
        and parsed.scheme == "http"
        and os.environ.get("DOTNETJQ_RELEASE_TEST_MODE") == "1"
    )
    if not structurally_valid or not (official or test_fixture):
        raise VerificationError("GitHub API URL is invalid")
    return api_url


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repository", required=True)
    parser.add_argument("--tag", required=True)
    parser.add_argument("--name", required=True)
    parser.add_argument("--body-file", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--state", choices=("draft", "published"), required=True)
    parser.add_argument("--prerelease", choices=("true", "false"), required=True)
    parser.add_argument("--api-url", default="https://api.github.com")
    args = parser.parse_args()

    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", args.repository):
        raise VerificationError("repository must be OWNER/NAME")
    if not re.fullmatch(r"v[0-9A-Za-z.-]+", args.tag):
        raise VerificationError("release tag has an invalid shape")
    token = os.environ.get("GITHUB_TOKEN", "")
    if not token:
        raise VerificationError("GITHUB_TOKEN is required")

    api_url = validate_api_url(args.api_url)
    local = local_assets(args.artifacts)
    body = local_release_body(args.body_file)
    release = find_release(api_url, args.repository, args.tag, token)
    expected_draft = args.state == "draft"
    expected_prerelease = args.prerelease == "true"
    if release.get("name") != args.name:
        raise VerificationError(f"release {args.tag} has an unexpected name")
    if release.get("body") != body:
        raise VerificationError(f"release {args.tag} has an unexpected body")
    if release.get("draft") is not expected_draft:
        raise VerificationError(f"release {args.tag} is not in expected {args.state} state")
    if release.get("prerelease") is not expected_prerelease:
        raise VerificationError(f"release {args.tag} has an unexpected prerelease flag")
    if not isinstance(release.get("immutable"), bool):
        raise VerificationError(f"release {args.tag} has a malformed immutable flag")
    if expected_draft and release["immutable"] is not False:
        raise VerificationError(f"draft release {args.tag} unexpectedly reports immutable")
    if not expected_draft and release["immutable"] is not True:
        raise VerificationError(f"published release {args.tag} is not immutable")

    remote_assets = release.get("assets")
    if not isinstance(remote_assets, list):
        raise VerificationError("release asset inventory is missing")
    remote_by_name: dict[str, dict[str, object]] = {}
    for asset in remote_assets:
        if not isinstance(asset, dict) or not isinstance(asset.get("name"), str):
            raise VerificationError("release contains malformed asset metadata")
        name = asset["name"]
        if name in remote_by_name:
            raise VerificationError(f"release contains duplicate asset name: {name}")
        remote_by_name[name] = asset
    if set(remote_by_name) != set(local):
        missing = sorted(set(local) - set(remote_by_name))
        extra = sorted(set(remote_by_name) - set(local))
        raise VerificationError(f"release asset inventory differs; missing={missing}, extra={extra}")

    api_origin = urllib.parse.urlsplit(api_url)
    encoded_repository = "/".join(
        urllib.parse.quote(part, safe="") for part in args.repository.split("/")
    )
    expected_path_prefix = (
        f"{api_origin.path.rstrip('/')}/repos/{encoded_repository}/releases/assets/"
    )
    for name in sorted(local):
        local_path = local[name]
        asset = remote_by_name[name]
        if asset.get("size") != local_path.stat().st_size:
            raise VerificationError(f"release asset size differs: {name}")
        asset_url = asset.get("url")
        if not isinstance(asset_url, str):
            raise VerificationError(f"release asset API URL is invalid: {name}")
        parsed_asset_url = urllib.parse.urlsplit(asset_url)
        if (
            (parsed_asset_url.scheme, parsed_asset_url.netloc)
            != (api_origin.scheme, api_origin.netloc)
            or not parsed_asset_url.path.startswith(expected_path_prefix)
            or parsed_asset_url.query
            or parsed_asset_url.fragment
            or parsed_asset_url.username
            or parsed_asset_url.password
        ):
            raise VerificationError(f"release asset API URL is invalid: {name}")
        local_digest = hashlib.sha256(local_path.read_bytes()).hexdigest()
        remote_digest = download_sha256(asset_url, token)
        if remote_digest != local_digest:
            raise VerificationError(f"release asset SHA-256 differs: {name}")

    print(f"verified {args.state} GitHub release {args.tag}: 27/27 exact assets")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except VerificationError as error:
        print(f"GitHub release verification failed: {error}", file=__import__("sys").stderr)
        raise SystemExit(1)
