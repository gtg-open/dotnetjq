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
INSTALLER_ENTRY_OLD = re.compile(
    r"- Architecture: (?P<architecture>[^\n]+)\n"
    r"  InstallerUrl: (?P<url>[^\n]+)\n"
    r"  InstallerSha256: (?P<sha256>[0-9A-Fa-f]{64})\n"
    r"  NestedInstallerType: (?P<nested_type>[^\n]+)\n"
    r"  NestedInstallerFiles:\n"
    r"  - RelativeFilePath: (?P<relative_path>[^\n]+)\n"
    r"    PortableCommandAlias: (?P<alias>[^\n]+)\n"
)
INSTALLER_ENTRY_SERIALIZED = re.compile(
    r"- Architecture: (?P<architecture>[^\n]+)\n"
    r"  NestedInstallerType: (?P<nested_type>[^\n]+)\n"
    r"  NestedInstallerFiles:\n"
    r"  - RelativeFilePath: (?P<relative_path>[^\n]+)\n"
    r"    PortableCommandAlias: (?P<alias>[^\n]+)\n"
    r"  InstallerUrl: (?P<url>[^\n]+)\n"
    r"  InstallerSha256: (?P<sha256>[0-9A-Fa-f]{64})\n"
)


class CanonicalizationError(RuntimeError):
    pass


def normalize_line_endings(source: bytes) -> bytes:
    if b"\r\n" in source:
        remainder = source.replace(b"\r\n", b"")
        if b"\r" in remainder or b"\n" in remainder:
            raise CanonicalizationError("manifest input has mixed line endings")
        return source.replace(b"\r\n", b"\n")
    if b"\r" in source:
        raise CanonicalizationError("manifest input contains a bare carriage return")
    return source


def serialize_installer_entry(match: re.Match[str]) -> str:
    return (
        f"- Architecture: {match['architecture']}\n"
        f"  NestedInstallerType: {match['nested_type']}\n"
        "  NestedInstallerFiles:\n"
        f"  - RelativeFilePath: {match['relative_path']}\n"
        f"    PortableCommandAlias: {match['alias']}\n"
        f"  InstallerUrl: {match['url']}\n"
        f"  InstallerSha256: {match['sha256']}\n"
    )


def canonicalize_installer_order(text: str) -> str:
    marker = "Installers:\n"
    start = text.find(marker)
    end = text.find("ManifestType: installer\n", start + len(marker))
    if start < 0 or end < 0:
        raise CanonicalizationError("installer manifest framing is invalid")
    start += len(marker)
    section = text[start:end]
    position = 0
    entries: list[str] = []
    while position < len(section):
        match = INSTALLER_ENTRY_SERIALIZED.match(section, position)
        if match is None:
            match = INSTALLER_ENTRY_OLD.match(section, position)
        if match is None:
            raise CanonicalizationError("installer entry has an unexpected shape")
        entries.append(serialize_installer_entry(match))
        position = match.end()
    if not entries:
        raise CanonicalizationError("installer manifest contains no installers")
    return text[:start] + "".join(entries) + text[end:]


def canonical_bytes(source: bytes) -> bytes:
    if source.startswith(b"\xef\xbb\xbf"):
        raise CanonicalizationError("manifest template must not contain a UTF-8 BOM")
    source = normalize_line_endings(source)
    try:
        text = source.decode("utf-8")
    except UnicodeDecodeError as error:
        raise CanonicalizationError("manifest template is not UTF-8") from error
    if not text.endswith("\n"):
        raise CanonicalizationError("manifest template must end with LF")
    if text.startswith(CREATED_BY):
        text = text[len(CREATED_BY):]
    first, separator, remainder = text.partition("\n")
    schema_line = first + separator
    schema = SCHEMA.fullmatch(schema_line)
    if schema is None:
        raise CanonicalizationError("manifest template has an unexpected schema header")
    if not remainder.startswith("\n"):
        raise CanonicalizationError("manifest template must contain one blank line after its schema")
    if remainder.startswith("\n\n"):
        raise CanonicalizationError("manifest template has multiple blank lines after its schema")
    if schema.group(1) == "installer":
        text = canonicalize_installer_order(text)

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
