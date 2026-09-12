#!/usr/bin/env python3
"""Classify a published or open-PR WinGet submission without mutating GitHub.

A published package or existing pull request is reusable only when its path
inventory and every manifest byte exactly match the local generated
three-manifest bundle. Pull requests additionally bind the authenticated token
owner, title, and fork branch. Any state that cannot be proved exact fails
closed.
"""

from __future__ import annotations

import argparse
import base64
import binascii
import json
import os
from pathlib import Path, PurePosixPath
import re
import sys
import urllib.error
import urllib.parse
import urllib.request


TARGET_REPOSITORY = "microsoft/winget-pkgs"
USER_AGENT = "dotnetjq-winget-pr-probe"
PACKAGE_ID_PATTERN = re.compile(
    r"[A-Za-z0-9][A-Za-z0-9_-]*(?:\.[A-Za-z0-9][A-Za-z0-9_-]*)+"
)
VERSION_PATTERN = re.compile(
    r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)"
    r"(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?"
)
GIT_OBJECT_PATTERN = re.compile(r"(?:[0-9a-f]{40}|[0-9a-f]{64})")


class ProbeError(RuntimeError):
    """The remote WinGet pull-request state cannot be classified safely."""


class SafeRedirectHandler(urllib.request.HTTPRedirectHandler):
    """Never forward the WinGet publication token outside its API origin."""

    def redirect_request(self, request, file_pointer, code, message, headers, new_url):
        source = urllib.parse.urlsplit(request.full_url)
        destination = urllib.parse.urlsplit(new_url)
        if (source.scheme, source.netloc) != (destination.scheme, destination.netloc):
            raise ProbeError("GitHub API redirect changed origin")
        return super().redirect_request(
            request, file_pointer, code, message, headers, new_url
        )


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


def open_request(request: urllib.request.Request, timeout: int):
    return urllib.request.build_opener(SafeRedirectHandler()).open(
        request, timeout=timeout
    )


def request_json(url: str, token: str) -> object:
    request = urllib.request.Request(
        url,
        headers={
            "Accept": "application/vnd.github+json",
            "Authorization": f"Bearer {token}",
            "X-GitHub-Api-Version": "2022-11-28",
            "User-Agent": USER_AGENT,
        },
    )
    try:
        with open_request(request, timeout=60) as response:
            return json.load(response)
    except (OSError, urllib.error.HTTPError, json.JSONDecodeError) as error:
        raise ProbeError(f"GitHub API request failed for {url}: {error}") from error


def request_json_or_not_found(url: str, token: str) -> object | None:
    request = urllib.request.Request(
        url,
        headers={
            "Accept": "application/vnd.github+json",
            "Authorization": f"Bearer {token}",
            "X-GitHub-Api-Version": "2022-11-28",
            "User-Agent": USER_AGENT,
        },
    )
    try:
        with open_request(request, timeout=60) as response:
            return json.load(response)
    except urllib.error.HTTPError as error:
        if error.code == 404:
            error.close()
            return None
        raise ProbeError(f"GitHub API request failed for {url}: {error}") from error
    except (OSError, json.JSONDecodeError) as error:
        raise ProbeError(f"GitHub API request failed for {url}: {error}") from error


def authenticated_login(api_url: str, token: str) -> str:
    payload = request_json(f"{api_url}/user", token)
    if not isinstance(payload, dict):
        raise ProbeError("authenticated-user response is not a JSON object")
    login = payload.get("login")
    if not isinstance(login, str) or not re.fullmatch(r"[A-Za-z0-9-]+", login):
        raise ProbeError("authenticated-user response has no valid login")
    return login


def expected_local_manifests(
    directory: Path, package_id: str
) -> dict[str, Path]:
    if not directory.is_dir() or directory.is_symlink():
        raise ProbeError(f"manifest directory is not a regular directory: {directory}")
    expected_names = {
        f"{package_id}.yaml",
        f"{package_id}.installer.yaml",
        f"{package_id}.locale.en-US.yaml",
    }
    entries = list(directory.iterdir())
    actual_names = {entry.name for entry in entries}
    if len(actual_names) != len(entries):
        raise ProbeError("local manifest directory contains duplicate names")
    if actual_names != expected_names:
        missing = sorted(expected_names - actual_names)
        extra = sorted(actual_names - expected_names)
        raise ProbeError(
            f"local manifest inventory differs; missing={missing}, extra={extra}"
        )
    result: dict[str, Path] = {}
    for name in sorted(expected_names):
        path = directory / name
        if not path.is_file() or path.is_symlink():
            raise ProbeError(f"local manifest is not a regular file: {name}")
        if path.stat().st_size == 0:
            raise ProbeError(f"local manifest is empty: {name}")
        result[name] = path
    return result


def expected_remote_manifests(
    local: dict[str, Path], package_id: str, version: str
) -> dict[str, Path]:
    parts = package_id.split(".")
    root = PurePosixPath("manifests", package_id[0].lower(), *parts, version)
    return {str(root / name): path for name, path in local.items()}


def list_open_pull_requests(
    api_url: str, submitter: str, token: str
) -> list[dict[str, object]]:
    results: list[dict[str, object]] = []
    for page in range(1, 31):
        query = urllib.parse.urlencode(
            {
                "state": "open",
                "creator": submitter,
                "sort": "created",
                "direction": "desc",
                "per_page": 100,
                "page": page,
            }
        )
        payload = request_json(
            f"{api_url}/repos/{TARGET_REPOSITORY}/issues?{query}", token
        )
        if not isinstance(payload, list):
            raise ProbeError("open-issue listing response is not a JSON array")
        for item in payload:
            if not isinstance(item, dict):
                raise ProbeError("open-issue listing contains a malformed item")
            number = item.get("number")
            user = item.get("user")
            pull_request = item.get("pull_request")
            expected_url = (
                f"{api_url}/repos/{TARGET_REPOSITORY}/pulls/{number}"
                if isinstance(number, int)
                else None
            )
            if (
                not isinstance(number, int)
                or number <= 0
                or not isinstance(user, dict)
                or not isinstance(user.get("login"), str)
                or user["login"].casefold() != submitter.casefold()
            ):
                raise ProbeError("open-issue listing returned an inexact creator item")
            if pull_request is None:
                continue
            if (
                not isinstance(pull_request, dict)
                or pull_request.get("url") != expected_url
            ):
                raise ProbeError("open-issue listing returned an inexact pull request")
            results.append(item)
        if len(payload) < 100:
            break
    else:
        raise ProbeError("open pull-request inventory exceeds the 3000-item safety limit")
    numbers = [item["number"] for item in results]
    if len(numbers) != len(set(numbers)):
        raise ProbeError("open-issue listing contains duplicate pull requests")
    return results


def pull_request_details(
    api_url: str, number: int, submitter: str, token: str
) -> dict[str, object]:
    payload = request_json(
        f"{api_url}/repos/{TARGET_REPOSITORY}/pulls/{number}", token
    )
    if not isinstance(payload, dict):
        raise ProbeError(f"pull request #{number} response is not a JSON object")
    user = payload.get("user")
    if (
        payload.get("number") != number
        or payload.get("state") != "open"
        or not isinstance(user, dict)
        or not isinstance(user.get("login"), str)
        or user["login"].casefold() != submitter.casefold()
    ):
        raise ProbeError(f"pull request #{number} identity differs from its search result")
    return payload


def pull_request_files(
    api_url: str, pull_request: dict[str, object], token: str
) -> list[dict[str, object]]:
    number = pull_request["number"]
    changed_files = pull_request.get("changed_files")
    if not isinstance(changed_files, int) or changed_files < 0:
        raise ProbeError(f"pull request #{number} has no valid changed-file count")
    if changed_files > 3000:
        raise ProbeError(f"pull request #{number} exceeds GitHub's file-list limit")
    results: list[dict[str, object]] = []
    for page in range(1, 31):
        query = urllib.parse.urlencode({"per_page": 100, "page": page})
        payload = request_json(
            f"{api_url}/repos/{TARGET_REPOSITORY}/pulls/{number}/files?{query}",
            token,
        )
        if not isinstance(payload, list):
            raise ProbeError(f"pull request #{number} file response is not a JSON array")
        for item in payload:
            if (
                not isinstance(item, dict)
                or not isinstance(item.get("filename"), str)
                or not isinstance(item.get("status"), str)
                or not isinstance(item.get("sha"), str)
            ):
                raise ProbeError(f"pull request #{number} contains malformed file metadata")
            results.append(item)
        if len(results) >= changed_files:
            break
        if not payload:
            raise ProbeError(f"pull request #{number} file pagination ended early")
    if len(results) != changed_files:
        raise ProbeError(
            f"pull request #{number} file count differs; "
            f"metadata={changed_files}, listed={len(results)}"
        )
    filenames = [item["filename"] for item in results]
    if len(filenames) != len(set(filenames)):
        raise ProbeError(f"pull request #{number} contains duplicate changed paths")
    return results


def repository_default_branch(api_url: str, token: str) -> str:
    payload = request_json(f"{api_url}/repos/{TARGET_REPOSITORY}", token)
    if not isinstance(payload, dict) or not isinstance(payload.get("default_branch"), str):
        raise ProbeError("WinGet repository response has no default branch")
    branch = payload["default_branch"]
    if not branch or "\n" in branch or "\r" in branch:
        raise ProbeError("WinGet repository response has an invalid default branch")
    return branch


def published_manifests_are_exact(
    api_url: str,
    default_branch: str,
    expected: dict[str, Path],
    token: str,
) -> bool:
    roots = {str(PurePosixPath(path).parent) for path in expected}
    if len(roots) != 1:
        raise ProbeError("expected published manifest paths have different roots")
    root = roots.pop()
    encoded_root = "/".join(
        urllib.parse.quote(part, safe="") for part in PurePosixPath(root).parts
    )
    query = urllib.parse.urlencode({"ref": default_branch})
    payload = request_json_or_not_found(
        f"{api_url}/repos/{TARGET_REPOSITORY}/contents/{encoded_root}?{query}",
        token,
    )
    if payload is None:
        return False
    if not isinstance(payload, list):
        raise ProbeError("published manifest directory response is malformed")

    remote_by_path: dict[str, dict[str, object]] = {}
    for item in payload:
        if (
            not isinstance(item, dict)
            or item.get("type") != "file"
            or not isinstance(item.get("name"), str)
            or not isinstance(item.get("path"), str)
            or not isinstance(item.get("sha"), str)
            or not isinstance(item.get("size"), int)
            or isinstance(item["size"], bool)
            or item["size"] < 0
        ):
            raise ProbeError("published manifest directory contains malformed metadata")
        if item["path"] != str(PurePosixPath(root, item["name"])):
            raise ProbeError("published manifest directory contains an inexact path")
        if item["path"] in remote_by_path:
            raise ProbeError("published manifest directory contains duplicate paths")
        remote_by_path[item["path"]] = item

    if set(remote_by_path) != set(expected):
        missing = sorted(set(expected) - set(remote_by_path))
        extra = sorted(set(remote_by_path) - set(expected))
        raise ProbeError(
            "published manifest path inventory differs; "
            f"missing={missing}, extra={extra}"
        )
    for remote_path in sorted(expected):
        metadata = remote_by_path[remote_path]
        remote = decode_blob(
            api_url, TARGET_REPOSITORY, metadata["sha"], token
        )
        if len(remote) != metadata["size"]:
            raise ProbeError(f"published manifest size differs: {remote_path}")
        if remote != expected[remote_path].read_bytes():
            raise ProbeError(f"published manifest bytes differ: {remote_path}")
    return True


def decode_blob(
    api_url: str, repository: str, expected_sha: str, token: str
) -> bytes:
    if not GIT_OBJECT_PATTERN.fullmatch(expected_sha):
        raise ProbeError("pull request contains a malformed Git object ID")
    encoded_repository = "/".join(
        urllib.parse.quote(part, safe="") for part in repository.split("/")
    )
    payload = request_json(
        f"{api_url}/repos/{encoded_repository}/git/blobs/{expected_sha}", token
    )
    if (
        not isinstance(payload, dict)
        or payload.get("sha") != expected_sha
        or payload.get("encoding") != "base64"
        or not isinstance(payload.get("content"), str)
        or not isinstance(payload.get("size"), int)
    ):
        raise ProbeError(f"Git blob response is malformed for {expected_sha}")
    encoded = payload["content"].replace("\r", "").replace("\n", "")
    try:
        content = base64.b64decode(encoded, validate=True)
    except (ValueError, binascii.Error) as error:
        raise ProbeError(f"Git blob is not valid base64 for {expected_sha}") from error
    if len(content) != payload["size"]:
        raise ProbeError(f"Git blob size differs for {expected_sha}")
    return content


def require_exact_candidate(
    *,
    api_url: str,
    pull_request: dict[str, object],
    files: list[dict[str, object]],
    submitter: str,
    package_id: str,
    version: str,
    title: str,
    expected: dict[str, Path],
    default_branch: str,
    token: str,
) -> tuple[int, str]:
    number = pull_request["number"]
    if pull_request.get("title") != title:
        raise ProbeError(f"candidate pull request #{number} title differs")
    if pull_request.get("html_url") != (
        f"https://github.com/{TARGET_REPOSITORY}/pull/{number}"
    ):
        raise ProbeError(f"candidate pull request #{number} URL differs")

    base = pull_request.get("base")
    head = pull_request.get("head")
    if not isinstance(base, dict) or not isinstance(head, dict):
        raise ProbeError(f"candidate pull request #{number} has malformed refs")
    base_repo = base.get("repo")
    head_repo = head.get("repo")
    head_user = head.get("user")
    if (
        not isinstance(base_repo, dict)
        or base_repo.get("full_name") != TARGET_REPOSITORY
        or base.get("ref") != default_branch
    ):
        raise ProbeError(f"candidate pull request #{number} base differs")
    expected_head_repository = f"{submitter}/winget-pkgs"
    if (
        not isinstance(head_repo, dict)
        or not isinstance(head_repo.get("full_name"), str)
        or head_repo["full_name"].casefold() != expected_head_repository.casefold()
        or not isinstance(head_user, dict)
        or not isinstance(head_user.get("login"), str)
        or head_user["login"].casefold() != submitter.casefold()
    ):
        raise ProbeError(f"candidate pull request #{number} head repository differs")
    branch = head.get("ref")
    branch_pattern = re.compile(
        re.escape(f"{package_id}-{version}-")
        + r"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-"
        + r"[0-9a-fA-F]{4}-[0-9a-fA-F]{12}"
    )
    if not isinstance(branch, str) or not branch_pattern.fullmatch(branch):
        raise ProbeError(f"candidate pull request #{number} branch differs")

    remote_by_path = {item["filename"]: item for item in files}
    if set(remote_by_path) != set(expected):
        missing = sorted(set(expected) - set(remote_by_path))
        extra = sorted(set(remote_by_path) - set(expected))
        raise ProbeError(
            f"candidate pull request #{number} path inventory differs; "
            f"missing={missing}, extra={extra}"
        )
    for remote_path in sorted(expected):
        metadata = remote_by_path[remote_path]
        if metadata.get("status") != "added" or metadata.get("previous_filename"):
            raise ProbeError(
                f"candidate pull request #{number} does not add a new manifest: "
                f"{remote_path}"
            )
        remote = decode_blob(
            api_url, expected_head_repository, metadata["sha"], token
        )
        if remote != expected[remote_path].read_bytes():
            raise ProbeError(
                f"candidate pull request #{number} manifest bytes differ: {remote_path}"
            )
    return number, pull_request["html_url"]


def classify(
    *,
    api_url: str,
    submitter: str,
    package_id: str,
    version: str,
    title: str,
    expected: dict[str, Path],
    token: str,
) -> tuple[str, int | None, str | None]:
    default_branch = repository_default_branch(api_url, token)
    if published_manifests_are_exact(
        api_url, default_branch, expected, token
    ):
        return "exact-published", None, None

    package_parts = package_id.split(".")
    package_prefix = str(
        PurePosixPath(
            "manifests", package_id[0].lower(), *package_parts
        )
    ) + "/"
    candidates: list[tuple[dict[str, object], list[dict[str, object]]]] = []
    for listed_item in list_open_pull_requests(api_url, submitter, token):
        number = listed_item["number"]
        pull_request = pull_request_details(api_url, number, submitter, token)
        files = pull_request_files(api_url, pull_request, token)
        touches_package = any(
            item["filename"].startswith(package_prefix) for item in files
        )
        if pull_request.get("title") == title or touches_package:
            candidates.append((pull_request, files))

    if not candidates:
        return "missing", None, None
    if len(candidates) != 1:
        numbers = sorted(candidate[0]["number"] for candidate in candidates)
        raise ProbeError(
            f"WinGet pull-request state is ambiguous; candidate PRs={numbers}"
        )
    number, url = require_exact_candidate(
        api_url=api_url,
        pull_request=candidates[0][0],
        files=candidates[0][1],
        submitter=submitter,
        package_id=package_id,
        version=version,
        title=title,
        expected=expected,
        default_branch=default_branch,
        token=token,
    )
    return "exact-existing", number, url


def classify_direct_pull_request(
    *,
    api_url: str,
    number: int,
    submitter: str,
    package_id: str,
    version: str,
    title: str,
    expected: dict[str, Path],
    token: str,
) -> tuple[str, int, str]:
    default_branch = repository_default_branch(api_url, token)
    pull_request = pull_request_details(api_url, number, submitter, token)
    files = pull_request_files(api_url, pull_request, token)
    verified_number, url = require_exact_candidate(
        api_url=api_url,
        pull_request=pull_request,
        files=files,
        submitter=submitter,
        package_id=package_id,
        version=version,
        title=title,
        expected=expected,
        default_branch=default_branch,
        token=token,
    )
    return "exact-existing", verified_number, url


def write_github_output(
    path: Path, *, state: str, submitter: str, number: int | None, url: str | None
) -> None:
    with path.open("a", encoding="utf-8", newline="\n") as stream:
        stream.write(f"state={state}\n")
        stream.write(f"submitter={submitter}\n")
        if number is not None and url is not None:
            stream.write(f"pull_request_number={number}\n")
            stream.write(f"pull_request_url={url}\n")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--package-id", required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--manifests", type=Path, required=True)
    parser.add_argument("--pr-title")
    parser.add_argument("--pull-request-number", type=int)
    parser.add_argument("--github-output", type=Path)
    parser.add_argument(
        "--api-url", default=os.environ.get("GITHUB_API_URL", "https://api.github.com")
    )
    args = parser.parse_args()

    if not PACKAGE_ID_PATTERN.fullmatch(args.package_id):
        raise ProbeError("package ID is invalid")
    if not VERSION_PATTERN.fullmatch(args.version):
        raise ProbeError("package version is invalid")
    if args.pull_request_number is not None and args.pull_request_number <= 0:
        raise ProbeError("pull-request number must be positive")
    title = args.pr_title or f"{args.package_id.rsplit('.', 1)[-1]} {args.version}"
    if not title or "\n" in title or "\r" in title:
        raise ProbeError("pull-request title must be a nonempty single-line value")
    api_url = validate_api_url(args.api_url)
    token = os.environ.get("WINGET_CREATE_GITHUB_TOKEN", "")
    if not token:
        raise ProbeError("WINGET_CREATE_GITHUB_TOKEN is required")

    local = expected_local_manifests(args.manifests, args.package_id)
    expected = expected_remote_manifests(local, args.package_id, args.version)
    submitter = authenticated_login(api_url, token)
    if args.pull_request_number is None:
        state, number, url = classify(
            api_url=api_url,
            submitter=submitter,
            package_id=args.package_id,
            version=args.version,
            title=title,
            expected=expected,
            token=token,
        )
    else:
        state, number, url = classify_direct_pull_request(
            api_url=api_url,
            number=args.pull_request_number,
            submitter=submitter,
            package_id=args.package_id,
            version=args.version,
            title=title,
            expected=expected,
            token=token,
        )
    if args.github_output:
        write_github_output(
            args.github_output,
            state=state,
            submitter=submitter,
            number=number,
            url=url,
        )
    if state == "missing":
        print(
            f"WinGet pull request {args.package_id} {args.version} "
            f"for {submitter}: missing"
        )
    elif state == "exact-published":
        print(
            f"WinGet package {args.package_id} {args.version}: exact-published"
        )
    else:
        print(
            f"WinGet pull request {args.package_id} {args.version} "
            f"for {submitter}: exact-existing ({url})"
        )
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except ProbeError as error:
        print(f"WinGet pull-request probe failed closed: {error}", file=sys.stderr)
        raise SystemExit(1)
