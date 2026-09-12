#!/usr/bin/env python3
"""Emit the exact YAML byte representation submitted by pinned WingetCreate."""

from __future__ import annotations

import argparse
from pathlib import Path
import re
import sys


WINGETCREATE_VERSION = "1.12.13.0"
CREATED_BY = f"# Created using wingetcreate {WINGETCREATE_VERSION}\n"
SCHEMA = re.compile(
    r"# yaml-language-server: \$schema=https://aka\.ms/"
    r"winget-manifest\.(version|installer|defaultLocale)\.1\.10\.0\.schema\.json\n"
)


class CanonicalizationError(RuntimeError):
    pass


def canonical_bytes(source: bytes) -> bytes:
    if source.startswith(b"\xef\xbb\xbf"):
        raise CanonicalizationError("manifest template must not contain a UTF-8 BOM")
    if b"\r" in source:
        raise CanonicalizationError("manifest template must use LF before canonicalization")
    try:
        text = source.decode("utf-8")
    except UnicodeDecodeError as error:
        raise CanonicalizationError("manifest template is not UTF-8") from error
    if not text.endswith("\n"):
        raise CanonicalizationError("manifest template must end with LF")
    first, separator, remainder = text.partition("\n")
    schema_line = first + separator
    if SCHEMA.fullmatch(schema_line) is None:
        raise CanonicalizationError("manifest template has an unexpected schema header")
    if not remainder.startswith("\n"):
        raise CanonicalizationError("manifest template must contain one blank line after its schema")
    if remainder.startswith("\n\n"):
        raise CanonicalizationError("manifest template has multiple blank lines after its schema")

    # WingetCreate 1.12.13.0 sets Serialization.ProducedBy and calls
    # YamlSerializer.ToManifestString(). On Windows, StringBuilder.AppendLine
    # and YamlDotNet 16.3.0 therefore produce this CRLF representation.
    return (CREATED_BY + text).replace("\n", "\r\n").encode("utf-8")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    arguments = parser.parse_args()
    if arguments.input.is_symlink() or not arguments.input.is_file():
        raise CanonicalizationError("input must be a regular manifest file")
    if arguments.output.exists() or arguments.output.is_symlink():
        raise CanonicalizationError("output must not already exist")
    arguments.output.write_bytes(canonical_bytes(arguments.input.read_bytes()))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (CanonicalizationError, OSError) as error:
        print(f"WinGet manifest canonicalization failed: {error}", file=sys.stderr)
        raise SystemExit(1) from error
