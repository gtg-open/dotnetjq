#!/usr/bin/env python3
"""Validate and compare the unsigned payload of NuGet package archives."""

from __future__ import annotations

import argparse
import hashlib
import struct
import sys
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path


SIGNATURE_ENTRY = ".signature.p7s"
CHUNK_SIZE = 1024 * 1024


class VerificationError(Exception):
    pass


def local_name(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def archive_entries(
    archive: zipfile.ZipFile, path: Path, signature_policy: str
) -> dict[str, zipfile.ZipInfo]:
    entries: dict[str, zipfile.ZipInfo] = {}
    signature_count = 0
    for entry in archive.infolist():
        if entry.filename in entries:
            raise VerificationError(
                f"{path.name} contains duplicate ZIP entry {entry.filename!r}"
            )
        entries[entry.filename] = entry
        if entry.filename == SIGNATURE_ENTRY:
            signature_count += 1

    if signature_policy == "absent" and signature_count != 0:
        raise VerificationError(f"local package {path.name} is unexpectedly signed")
    if signature_policy == "present" and signature_count != 1:
        raise VerificationError(
            f"remote package {path.name} must contain exactly one {SIGNATURE_ENTRY}"
        )
    return entries


def nuspec_identity(
    archive: zipfile.ZipFile,
    entries: dict[str, zipfile.ZipInfo],
    path: Path,
) -> tuple[str, str]:
    nuspec_names = [
        name
        for name in entries
        if not name.endswith("/") and name.lower().endswith(".nuspec")
    ]
    if len(nuspec_names) != 1:
        raise VerificationError(
            f"{path.name} must contain exactly one nuspec; found {len(nuspec_names)}"
        )

    nuspec_name = nuspec_names[0]
    if "/" in nuspec_name or "\\" in nuspec_name:
        raise VerificationError(f"{path.name} nuspec must be a root ZIP entry")
    try:
        root = ET.fromstring(archive.read(nuspec_name))
    except (ET.ParseError, KeyError, RuntimeError, zipfile.BadZipFile) as error:
        raise VerificationError(f"could not parse {path.name} nuspec: {error}") from error

    metadata = [child for child in root if local_name(child.tag) == "metadata"]
    if len(metadata) != 1:
        raise VerificationError(f"{path.name} nuspec must contain one metadata element")

    def required_text(element_name: str) -> str:
        matches = [
            child for child in metadata[0] if local_name(child.tag) == element_name
        ]
        if len(matches) != 1 or matches[0].text is None:
            raise VerificationError(
                f"{path.name} nuspec must contain one {element_name} element"
            )
        return matches[0].text.strip()

    return required_text("id"), required_text("version")


def validate_identity(
    archive: zipfile.ZipFile,
    entries: dict[str, zipfile.ZipInfo],
    path: Path,
    expected_id: str,
    expected_version: str,
) -> None:
    actual_id, actual_version = nuspec_identity(archive, entries, path)
    if actual_id != expected_id:
        raise VerificationError(
            f"{path.name} nuspec ID {actual_id!r} does not equal {expected_id!r}"
        )
    if actual_version != expected_version:
        raise VerificationError(
            f"{path.name} nuspec version {actual_version!r} does not equal "
            f"{expected_version!r}"
        )


def payload_names(entries: dict[str, zipfile.ZipInfo]) -> list[str]:
    return sorted(name for name in entries if name != SIGNATURE_ENTRY)


def payload_fingerprint(
    archive: zipfile.ZipFile, entries: dict[str, zipfile.ZipInfo]
) -> str:
    digest = hashlib.sha256()
    for name in payload_names(entries):
        name_bytes = name.encode("utf-8")
        entry = entries[name]
        digest.update(struct.pack(">Q", len(name_bytes)))
        digest.update(name_bytes)
        digest.update(struct.pack(">Q", entry.file_size))
        with archive.open(entry, "r") as stream:
            while chunk := stream.read(CHUNK_SIZE):
                digest.update(chunk)
    return digest.hexdigest()


def compare_payloads(
    local_archive: zipfile.ZipFile,
    local_entries: dict[str, zipfile.ZipInfo],
    remote_archive: zipfile.ZipFile,
    remote_entries: dict[str, zipfile.ZipInfo],
    local_path: Path,
    remote_path: Path,
) -> None:
    local_names = payload_names(local_entries)
    remote_names = payload_names(remote_entries)
    if local_names != remote_names:
        missing = sorted(set(local_names) - set(remote_names))
        unexpected = sorted(set(remote_names) - set(local_names))
        detail = []
        if missing:
            detail.append(f"missing={missing!r}")
        if unexpected:
            detail.append(f"unexpected={unexpected!r}")
        raise VerificationError(
            "remote package payload entry names differ from the local package"
            + (f" ({', '.join(detail)})" if detail else "")
        )

    for name in local_names:
        local_entry = local_entries[name]
        remote_entry = remote_entries[name]
        if local_entry.file_size != remote_entry.file_size:
            raise VerificationError(
                f"remote entry {name!r} size differs from {local_path.name}"
            )
        with local_archive.open(local_entry, "r") as local_stream, remote_archive.open(
            remote_entry, "r"
        ) as remote_stream:
            while True:
                local_chunk = local_stream.read(CHUNK_SIZE)
                remote_chunk = remote_stream.read(CHUNK_SIZE)
                if local_chunk != remote_chunk:
                    raise VerificationError(
                        f"remote entry {name!r} bytes differ from {local_path.name}"
                    )
                if not local_chunk:
                    break


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--package", required=True, type=Path)
    parser.add_argument("--package-id", required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument(
        "--signature-policy", choices=("absent", "present"), default="absent"
    )
    parser.add_argument("--compare", type=Path)
    parser.add_argument("--expected-payload-sha256")
    return parser.parse_args()


def validate_hash(value: str | None) -> None:
    if value is None:
        return
    if len(value) != 64 or any(character not in "0123456789abcdef" for character in value):
        raise VerificationError("expected payload SHA-256 is malformed")


def main() -> int:
    args = parse_args()
    validate_hash(args.expected_payload_sha256)
    if not args.package.is_file():
        raise VerificationError(f"package does not exist: {args.package}")
    if args.compare is not None and not args.compare.is_file():
        raise VerificationError(f"comparison package does not exist: {args.compare}")

    try:
        with zipfile.ZipFile(args.package, "r") as local_archive:
            local_entries = archive_entries(
                local_archive, args.package, args.signature_policy
            )
            validate_identity(
                local_archive,
                local_entries,
                args.package,
                args.package_id,
                args.version,
            )
            fingerprint = payload_fingerprint(local_archive, local_entries)
            if (
                args.expected_payload_sha256 is not None
                and fingerprint != args.expected_payload_sha256
            ):
                raise VerificationError(
                    f"{args.package.name} payload SHA-256 differs from the plan"
                )

            if args.compare is not None:
                with zipfile.ZipFile(args.compare, "r") as remote_archive:
                    remote_entries = archive_entries(
                        remote_archive, args.compare, "present"
                    )
                    compare_payloads(
                        local_archive,
                        local_entries,
                        remote_archive,
                        remote_entries,
                        args.package,
                        args.compare,
                    )
                    validate_identity(
                        remote_archive,
                        remote_entries,
                        args.compare,
                        args.package_id,
                        args.version,
                    )
    except (OSError, RuntimeError, zipfile.BadZipFile) as error:
        raise VerificationError(f"could not inspect NuGet package: {error}") from error

    print(fingerprint)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except VerificationError as error:
        print(f"error: {error}", file=sys.stderr)
        raise SystemExit(1)
