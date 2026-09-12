#!/usr/bin/env python3
"""Reject CR bytes in text files whose bytes are embedded in release artifacts."""

from __future__ import annotations

from pathlib import Path
import sys


ROOT = Path(__file__).resolve().parents[2]
POLICY_FILES = (
    ROOT / "packaging" / "release-license-paths.txt",
    ROOT / "packaging" / "aot-source-paths.txt",
)
CONTROL_FILES = (
    ROOT / ".gitattributes",
    ROOT / ".github" / "workflows" / "cli-cross-platform.yml",
    ROOT / ".github" / "workflows" / "release-cli.yml",
    ROOT / ".github" / "workflows" / "semantic-compatibility.yml",
    ROOT / "tools" / "verify-release.sh",
)
CONTROL_TREES = (
    ROOT / "packaging",
    ROOT / "tools" / "release",
)
IGNORED_DIRECTORIES = {"bin", "obj", "__pycache__"}


def iter_policy_paths(policy: Path):
    for raw_line in policy.read_text(encoding="utf-8").splitlines():
        relative = raw_line.strip()
        if not relative or relative.startswith("#"):
            continue
        yield ROOT / relative


def iter_tree_files(tree: Path):
    for path in tree.rglob("*"):
        if any(part in IGNORED_DIRECTORIES for part in path.relative_to(tree).parts):
            continue
        if path.is_file():
            yield path


def is_git_binary(data: bytes) -> bool:
    # Match Git's text=auto binary heuristic: a NUL in the first 8 KiB keeps
    # the file byte-transparent instead of applying an end-of-line transform.
    return b"\0" in data[:8000]


def main() -> int:
    paths = set(CONTROL_FILES)
    for tree in CONTROL_TREES:
        paths.update(iter_tree_files(tree))
    for policy in POLICY_FILES:
        for path in iter_policy_paths(policy):
            if path.is_dir():
                paths.update(iter_tree_files(path))
            else:
                paths.add(path)

    missing = [path for path in paths if not path.is_file()]
    if missing:
        for path in sorted(missing):
            print(
                f"release tooling: packaging input is missing: {path.relative_to(ROOT)}",
                file=sys.stderr,
            )
        return 1

    invalid = []
    for path in sorted(paths):
        data = path.read_bytes()
        if not is_git_binary(data) and b"\r" in data:
            invalid.append(path)

    if invalid:
        for path in invalid:
            print(
                "release tooling: packaging text input contains a CR byte: "
                f"{path.relative_to(ROOT)}",
                file=sys.stderr,
            )
        return 1

    print(f"verified LF-stable packaging text inputs ({len(paths)} files)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
