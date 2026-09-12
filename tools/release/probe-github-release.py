#!/usr/bin/env python3
"""Classify an existing GitHub release without mutating it.

The staging workflow uses this probe before creating a draft.  A draft is
reusable only when its metadata and every uploaded asset are byte-for-byte
equal to the locally verified release bundle.
"""

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


EXPECTED_ASSET_COUNT = 27

SEMVER_TAG = re.compile(
    r"^v"
    r"(?P<major>0|[1-9][0-9]*)\."
    r"(?P<minor>0|[1-9][0-9]*)\."
    r"(?P<patch>0|[1-9][0-9]*)"
    r"(?:-(?P<prerelease>"
    r"(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)"
    r"(?:\.(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*"
    r"))?"
    r"(?:\+(?P<build>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$"
)


class ProbeError(RuntimeError):
    """The remote release state cannot be classified safely."""


class ReleaseMismatch(ProbeError):
    """A release exists for the tag but does not equal the expected release."""


class SafeRedirectHandler(urllib.request.HTTPRedirectHandler):
    """Do not forward the workflow token to a cross-origin asset redirect."""

    def redirect_request(self, request, file_pointer, code, message, headers, new_url):
        redirected = super().redirect_request(
            request, file_pointer, code, message, headers, new_url
        )
        if redirected is None:
            return None
        destination = urllib.parse.urlsplit(new_url)
        if destination.scheme != "https":
            raise ProbeError("release asset redirect must use HTTPS")
        source = urllib.parse.urlsplit(request.full_url)
        if (destination.scheme, destination.netloc) != (source.scheme, source.netloc):
            redirected.remove_header("Authorization")
            redirected.remove_header("X-GitHub-Api-Version")
        return redirected


def authenticated_request(url: str, token: str, accept: str) -> urllib.request.Request:
    return urllib.request.Request(
        url,
        headers={
            "Accept": accept,
            "Authorization": f"Bearer {token}",
            "X-GitHub-Api-Version": "2022-11-28",
            "User-Agent": "dotnetjq-release-probe",
        },
    )


def request_json(url: str, token: str) -> object:
    request = authenticated_request(url, token, "application/vnd.github+json")
    opener = urllib.request.build_opener(SafeRedirectHandler())
    try:
        with opener.open(request, timeout=60) as response:
            return json.load(response)
    except (OSError, urllib.error.HTTPError, json.JSONDecodeError) as error:
        raise ProbeError(f"GitHub API request failed for {url}: {error}") from error


def file_sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        while chunk := stream.read(1024 * 1024):
            digest.update(chunk)
    return digest.hexdigest()


def download_sha256(url: str, token: str) -> str:
    request = authenticated_request(url, token, "application/octet-stream")
    digest = hashlib.sha256()
    opener = urllib.request.build_opener(SafeRedirectHandler())
    try:
        with opener.open(request, timeout=120) as response:
            while chunk := response.read(1024 * 1024):
                digest.update(chunk)
    except (OSError, urllib.error.HTTPError) as error:
        raise ProbeError(f"release asset download failed: {error}") from error
    return digest.hexdigest()


def local_assets(directory: Path) -> dict[str, Path]:
    if not directory.is_dir():
        raise ProbeError(f"artifact directory does not exist: {directory}")
    entries = list(directory.iterdir())
    unexpected = [entry for entry in entries if not entry.is_file() or entry.is_symlink()]
    if unexpected:
        raise ProbeError(
            "artifact directory contains a non-regular entry: "
            + ", ".join(str(entry) for entry in unexpected)
        )
    if len(entries) != EXPECTED_ASSET_COUNT:
        raise ProbeError(
            f"expected exactly {EXPECTED_ASSET_COUNT} local release assets; "
            f"found {len(entries)}"
        )
    result = {entry.name: entry for entry in entries}
    if len(result) != len(entries):
        raise ProbeError("artifact directory contains duplicate names")
    return result


def local_release_body(path: Path) -> str:
    if not path.is_file() or path.is_symlink():
        raise ProbeError(f"release body is not a regular file: {path}")
    payload = path.read_bytes()
    if not payload or b"\r" in payload or not payload.endswith(b"\n"):
        raise ProbeError("release body must be nonempty, LF-only, and newline-terminated")
    try:
        return payload.decode("utf-8")
    except UnicodeDecodeError as error:
        raise ProbeError("release body is not valid UTF-8") from error


def release_inventory(
    api_url: str, repository: str, token: str
) -> list[dict[str, object]]:
    encoded_repository = "/".join(
        urllib.parse.quote(part, safe="") for part in repository.split("/")
    )
    releases: list[dict[str, object]] = []
    for page in range(1, 101):
        query = urllib.parse.urlencode({"per_page": 100, "page": page})
        url = f"{api_url}/repos/{encoded_repository}/releases?{query}"
        payload = request_json(url, token)
        if not isinstance(payload, list):
            raise ProbeError("GitHub releases response is not a JSON array")
        for candidate in payload:
            if not isinstance(candidate, dict):
                raise ProbeError("GitHub releases response contains a malformed entry")
            releases.append(candidate)
        if len(payload) < 100:
            return releases
    raise ProbeError("GitHub release inventory exceeds the safe pagination limit")


def parse_semver_tag(tag: str) -> tuple[tuple[str, str, str], bool]:
    match = SEMVER_TAG.fullmatch(tag)
    if match is None:
        raise ProbeError(f"release tag is not strict SemVer: {tag}")
    return (
        (match.group("major"), match.group("minor"), match.group("patch")),
        match.group("prerelease") is not None,
    )


def core_version_is_newer(
    candidate: tuple[str, str, str], target: tuple[str, str, str]
) -> bool:
    """Compare unbounded SemVer numeric identifiers without integer conversion."""

    for candidate_part, target_part in zip(candidate, target, strict=True):
        if len(candidate_part) != len(target_part):
            return len(candidate_part) > len(target_part)
        if candidate_part != target_part:
            return candidate_part > target_part
    return False


def stable_target_is_superseded(
    releases: list[dict[str, object]], *, tag: str, prerelease: bool
) -> bool:
    if prerelease:
        return False

    target_version, target_has_prerelease = parse_semver_tag(tag)
    if target_has_prerelease:
        raise ProbeError(
            "a release marked stable cannot use a prerelease SemVer target tag"
        )

    superseded = False
    for release in releases:
        candidate_tag = release.get("tag_name")
        if not isinstance(candidate_tag, str) or not candidate_tag.startswith("v"):
            continue

        draft = release.get("draft")
        candidate_prerelease = release.get("prerelease")
        if not isinstance(draft, bool) or not isinstance(candidate_prerelease, bool):
            raise ProbeError(
                f"release {candidate_tag} has malformed draft/prerelease metadata"
            )
        if draft or candidate_prerelease:
            continue

        candidate_version, candidate_has_prerelease = parse_semver_tag(candidate_tag)
        if candidate_has_prerelease:
            raise ProbeError(
                f"published stable release uses a prerelease SemVer tag: {candidate_tag}"
            )
        if core_version_is_newer(candidate_version, target_version):
            superseded = True

    return superseded


def require_verified_assets(
    release: dict[str, object],
    local: dict[str, Path],
    api_url: str,
    repository: str,
    token: str,
    *,
    allow_subset: bool,
) -> bool:
    remote_assets = release.get("assets")
    if not isinstance(remote_assets, list):
        raise ReleaseMismatch("existing release has no valid asset inventory")
    remote_by_name: dict[str, dict[str, object]] = {}
    for asset in remote_assets:
        if not isinstance(asset, dict) or not isinstance(asset.get("name"), str):
            raise ReleaseMismatch("existing release contains malformed asset metadata")
        name = asset["name"]
        if name in remote_by_name:
            raise ReleaseMismatch(f"existing release contains duplicate asset name: {name}")
        remote_by_name[name] = asset

    missing = sorted(set(local) - set(remote_by_name))
    extra = sorted(set(remote_by_name) - set(local))
    if extra or (missing and not allow_subset):
        raise ReleaseMismatch(
            f"existing release asset inventory differs; missing={missing}, extra={extra}"
        )

    api_origin = urllib.parse.urlsplit(api_url)
    encoded_repository = "/".join(
        urllib.parse.quote(part, safe="") for part in repository.split("/")
    )
    expected_path_prefix = (
        f"{api_origin.path.rstrip('/')}/repos/{encoded_repository}/releases/assets/"
    )
    for name in sorted(remote_by_name):
        local_path = local[name]
        asset = remote_by_name[name]
        if asset.get("state") != "uploaded":
            raise ReleaseMismatch(f"existing release asset is not fully uploaded: {name}")
        if asset.get("size") != local_path.stat().st_size:
            raise ReleaseMismatch(f"existing release asset size differs: {name}")
        asset_url = asset.get("url")
        if not isinstance(asset_url, str):
            raise ReleaseMismatch(f"existing release asset API URL is missing: {name}")
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
            raise ReleaseMismatch(f"existing release asset API URL is invalid: {name}")
        if download_sha256(asset_url, token) != file_sha256(local_path):
            raise ReleaseMismatch(f"existing release asset SHA-256 differs: {name}")
    return not missing


def classify_release(
    release: dict[str, object],
    *,
    tag: str,
    name: str,
    body: str,
    prerelease: bool,
    local: dict[str, Path],
    api_url: str,
    repository: str,
    token: str,
) -> str:
    if release.get("tag_name") != tag:
        raise ReleaseMismatch("existing release tag differs")
    if release.get("name") != name:
        raise ReleaseMismatch("existing release name differs")
    if release.get("body") != body:
        raise ReleaseMismatch("existing release body differs")
    if release.get("prerelease") is not prerelease:
        raise ReleaseMismatch("existing release prerelease flag differs")
    if not isinstance(release.get("draft"), bool):
        raise ReleaseMismatch("existing release has a malformed draft flag")
    draft = release["draft"]
    if not isinstance(release.get("immutable"), bool):
        raise ReleaseMismatch("existing release has a malformed immutable flag")
    if draft and release["immutable"] is not False:
        raise ReleaseMismatch("existing draft release unexpectedly reports immutable")
    if not draft and release["immutable"] is not True:
        raise ReleaseMismatch("existing published release is not immutable")
    complete = require_verified_assets(
        release,
        local,
        api_url,
        repository,
        token,
        allow_subset=draft,
    )
    if draft:
        return "exact-draft" if complete else "partial-draft"
    return "published"


def write_github_output(path: str | None, state: str, superseded: bool) -> None:
    if path:
        with Path(path).open("a", encoding="utf-8", newline="\n") as stream:
            stream.write(f"state={state}\n")
            stream.write(f"superseded={'true' if superseded else 'false'}\n")


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
        raise ProbeError("GitHub API URL is invalid")
    return api_url


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repository", required=True)
    parser.add_argument("--tag", required=True)
    parser.add_argument("--name", required=True)
    parser.add_argument("--body-file", type=Path, required=True)
    parser.add_argument("--artifacts", type=Path, required=True)
    parser.add_argument("--prerelease", choices=("true", "false"), required=True)
    parser.add_argument("--github-output")
    parser.add_argument(
        "--api-url", default=os.environ.get("GITHUB_API_URL", "https://api.github.com")
    )
    args = parser.parse_args()

    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", args.repository):
        raise ProbeError("repository must be OWNER/NAME")
    if not re.fullmatch(r"v[0-9A-Za-z.+-]+", args.tag):
        raise ProbeError("release tag has an invalid shape")
    if not args.name or "\n" in args.name or "\r" in args.name:
        raise ProbeError("release name must be a nonempty single-line value")
    api_url = validate_api_url(args.api_url)
    token = os.environ.get("GITHUB_TOKEN", "")
    if not token:
        raise ProbeError("GITHUB_TOKEN is required")

    local = local_assets(args.artifacts)
    body = local_release_body(args.body_file)
    releases = release_inventory(api_url, args.repository, token)
    superseded = stable_target_is_superseded(
        releases,
        tag=args.tag,
        prerelease=args.prerelease == "true",
    )
    matches = [release for release in releases if release.get("tag_name") == args.tag]
    try:
        if not matches:
            state = "missing"
        elif len(matches) != 1:
            raise ReleaseMismatch(
                f"existing release state is ambiguous: "
                f"found {len(matches)} releases for {args.tag}"
            )
        else:
            state = classify_release(
                matches[0],
                tag=args.tag,
                name=args.name,
                body=body,
                prerelease=args.prerelease == "true",
                local=local,
                api_url=api_url,
                repository=args.repository,
                token=token,
            )
    except ReleaseMismatch:
        write_github_output(args.github_output, "mismatched", superseded)
        raise

    write_github_output(args.github_output, state, superseded)
    print(
        f"GitHub release {args.tag}: {state}; "
        f"superseded={'true' if superseded else 'false'}"
    )
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except ProbeError as error:
        print(f"GitHub release probe failed closed: {error}", file=__import__("sys").stderr)
        raise SystemExit(1)
