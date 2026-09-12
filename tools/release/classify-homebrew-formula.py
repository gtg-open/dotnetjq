#!/usr/bin/env python3
"""Classify a generated Homebrew formula update without executing Ruby."""

from __future__ import annotations

import argparse
import re
import stat
import sys
from dataclasses import dataclass
from pathlib import Path


MAX_FORMULA_BYTES = 1024 * 1024
STABLE_VERSION_PATTERN = re.compile(
    r"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$"
)
CLASS_LINE_PATTERN = re.compile(r"^[ \t]*class(?:[ \t]|$)")
VERSION_LINE_PATTERN = re.compile(r"^[ \t]*version(?:[ \t(]|$)")
GENERATED_VERSION_LINE_PATTERN = re.compile(
    r'^  version "((?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*))"$'
)


class ClassifierError(Exception):
    """A formula state cannot be proven safe."""


@dataclass(frozen=True)
class Formula:
    contents: bytes
    version: str


def parse_stable_version(value: str, label: str) -> tuple[str, str, str]:
    match = STABLE_VERSION_PATTERN.fullmatch(value)
    if match is None:
        raise ClassifierError(
            f"{label} must be a stable MAJOR.MINOR.PATCH SemVer without leading zeros"
        )
    return match.group(1), match.group(2), match.group(3)


def read_regular_file(path: Path, label: str) -> bytes:
    try:
        metadata = path.lstat()
    except OSError as error:
        raise ClassifierError(f"cannot inspect {label} {path}: {error}") from error

    if not stat.S_ISREG(metadata.st_mode):
        raise ClassifierError(f"{label} must be a regular file: {path}")
    if metadata.st_size > MAX_FORMULA_BYTES:
        raise ClassifierError(
            f"{label} exceeds the {MAX_FORMULA_BYTES}-byte safety limit: {path}"
        )

    try:
        contents = path.read_bytes()
    except OSError as error:
        raise ClassifierError(f"cannot read {label} {path}: {error}") from error

    if len(contents) > MAX_FORMULA_BYTES:
        raise ClassifierError(
            f"{label} exceeds the {MAX_FORMULA_BYTES}-byte safety limit: {path}"
        )
    return contents


def parse_formula(path: Path, label: str) -> Formula:
    contents = read_regular_file(path, label)
    if not contents or not contents.endswith(b"\n"):
        raise ClassifierError(f"{label} must be nonempty and end with a newline")
    if b"\x00" in contents or b"\r" in contents:
        raise ClassifierError(f"{label} contains unsupported control or line-ending bytes")

    try:
        text = contents.decode("utf-8", errors="strict")
    except UnicodeDecodeError as error:
        raise ClassifierError(f"{label} is not valid UTF-8") from error

    lines = text[:-1].split("\n")
    class_lines = [line for line in lines if CLASS_LINE_PATTERN.match(line)]
    if class_lines != ["class Dotnetjq < Formula"]:
        raise ClassifierError(
            f"{label} does not contain exactly the generated Dotnetjq formula class"
        )

    version_lines = [line for line in lines if VERSION_LINE_PATTERN.match(line)]
    if len(version_lines) != 1:
        raise ClassifierError(
            f"{label} must contain exactly one explicit generated version statement"
        )
    version_match = GENERATED_VERSION_LINE_PATTERN.fullmatch(version_lines[0])
    if version_match is None:
        raise ClassifierError(
            f"{label} version is not an explicit stable generated SemVer"
        )

    version = version_match.group(1)
    parse_stable_version(version, f"{label} version")
    return Formula(contents=contents, version=version)


def compare_numeric_identifier(left: str, right: str) -> int:
    if len(left) != len(right):
        return -1 if len(left) < len(right) else 1
    if left == right:
        return 0
    return -1 if left < right else 1


def compare_versions(left: str, right: str) -> int:
    left_parts = parse_stable_version(left, "left version")
    right_parts = parse_stable_version(right, "right version")
    for left_part, right_part in zip(left_parts, right_parts, strict=True):
        comparison = compare_numeric_identifier(left_part, right_part)
        if comparison:
            return comparison
    return 0


def classify(current_path: Path, candidate_path: Path, target_version: str) -> str:
    parse_stable_version(target_version, "target version")
    candidate = parse_formula(candidate_path, "candidate formula")
    if candidate.version != target_version:
        raise ClassifierError(
            f"candidate formula version {candidate.version} does not equal target "
            f"version {target_version}"
        )

    try:
        current_path.lstat()
    except FileNotFoundError:
        return "publish:missing"
    except OSError as error:
        raise ClassifierError(
            f"cannot inspect current formula {current_path}: {error}"
        ) from error

    current = parse_formula(current_path, "current formula")
    comparison = compare_versions(current.version, target_version)
    if comparison < 0:
        return "publish:upgrade"
    if comparison > 0:
        return "skip:superseded"
    if current.contents == candidate.contents:
        return "skip:identical"
    raise ClassifierError(
        "current and candidate formulas claim the same version but are not byte-identical"
    )


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(
        description=(
            "Classify a local Homebrew tap formula update. On success, prints one "
            "of publish:missing, publish:upgrade, skip:identical, or "
            "skip:superseded. Any unprovable state fails closed."
        )
    )
    parser.add_argument("--current", type=Path, required=True)
    parser.add_argument("--candidate", type=Path, required=True)
    parser.add_argument("--version", required=True)
    args = parser.parse_args(argv)

    try:
        result = classify(args.current, args.candidate, args.version)
    except ClassifierError as error:
        print(f"Homebrew formula classifier: {error}", file=sys.stderr)
        return 2

    print(result)
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
