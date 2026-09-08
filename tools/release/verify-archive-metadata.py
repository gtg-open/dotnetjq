#!/usr/bin/env python3
"""Verify deterministic metadata emitted by DotNetJq release archive builders."""

from __future__ import annotations

import argparse
import posixpath
import stat
import struct
import sys
import tarfile
import zipfile
from pathlib import Path


class VerificationError(RuntimeError):
    """The archive does not have the deterministic release representation."""


def require_regular_file(path: Path) -> Path:
    try:
        resolved = path.resolve(strict=True)
    except (OSError, RuntimeError) as exception:
        raise VerificationError(f"cannot resolve archive: {exception}") from exception
    if not resolved.is_file():
        raise VerificationError(f"archive is not a regular file: {resolved}")
    return resolved


def verify_gzip_header(path: Path) -> None:
    try:
        with path.open("rb") as stream:
            header = stream.read(10)
    except OSError as exception:
        raise VerificationError(f"cannot read gzip header: {exception}") from exception
    if len(header) != 10 or header[:3] != b"\x1f\x8b\x08":
        raise VerificationError("tar.gz archive has an invalid gzip header")
    flags = header[3]
    if flags & 0x1E:
        raise VerificationError(
            "gzip header contains nondeterministic extra/name/comment/header-CRC fields"
        )
    if struct.unpack("<I", header[4:8])[0] != 0:
        raise VerificationError("gzip header modification time is not normalized to zero")


def verify_tar(path: Path, *, mode: str, version: str | None) -> int:
    verify_gzip_header(path)
    try:
        with tarfile.open(path, mode="r:gz") as archive:
            members = archive.getmembers()
            global_pax_headers = archive.pax_headers
    except (OSError, tarfile.TarError) as exception:
        raise VerificationError(f"cannot parse tar.gz archive: {exception}") from exception

    if not members:
        raise VerificationError("tar.gz archive contains no members")
    names = [member.name for member in members]
    if len(names) != len(set(names)):
        raise VerificationError("tar.gz archive contains duplicate member names")
    if global_pax_headers:
        raise VerificationError("tar.gz archive contains unexpected global PAX metadata")

    if mode == "native":
        expected_root = "."
        expected_mtime = 315_532_800  # 1980-01-01 00:00:00 UTC
    else:
        if version is None:
            raise VerificationError("AOT source verification requires a version")
        expected_root = f"dotnetjq-aot-source-{version}"
        expected_mtime = 0

    if names[0] != expected_root or not members[0].isdir():
        raise VerificationError(f"first tar.gz member is not root directory {expected_root!r}")

    members_by_name = {member.name: member for member in members}
    children: dict[str, list[str]] = {member.name: [] for member in members if member.isdir()}
    for member in members[1:]:
        parent = posixpath.dirname(member.name)
        if parent not in children:
            raise VerificationError(
                f"tar.gz member has no preceding directory member: {member.name}"
            )
        children[parent].append(member.name)

    expected_order: list[str] = []

    def append_tree(name: str) -> None:
        expected_order.append(name)
        if members_by_name[name].isdir():
            for child in sorted(children[name], key=posixpath.basename):
                append_tree(child)

    append_tree(expected_root)
    if names != expected_order:
        raise VerificationError(
            "tar.gz members are not in deterministic depth-first ordinal order"
        )

    for member in members:
        name = member.name
        if name != expected_root and not name.startswith(f"{expected_root}/"):
            raise VerificationError(f"tar.gz member escapes expected root: {name}")
        if not (member.isfile() or member.isdir()):
            raise VerificationError(f"tar.gz member is not a regular file/directory: {name}")
        if member.uid != 0 or member.gid != 0:
            raise VerificationError(f"tar.gz owner is not numeric zero: {name}")
        if member.uname or member.gname:
            raise VerificationError(f"tar.gz member records an owner/group name: {name}")
        if member.mtime != expected_mtime:
            raise VerificationError(f"tar.gz timestamp is not normalized: {name}")
        if member.pax_headers:
            raise VerificationError(f"tar.gz member contains unexpected PAX metadata: {name}")

        if member.isdir():
            expected_permissions = 0o755
        elif mode == "native":
            expected_permissions = 0o755 if name == "./dotnetjq" else 0o644
        else:
            expected_permissions = 0o755 if name.endswith(".sh") else 0o644
        if member.mode != expected_permissions:
            raise VerificationError(
                f"tar.gz mode for {name} is {member.mode:#o}, "
                f"expected {expected_permissions:#o}"
            )

    return len(members)


def verify_zip(path: Path) -> int:
    try:
        with zipfile.ZipFile(path, mode="r") as archive:
            entries = archive.infolist()
            archive_comment = archive.comment
            bad_member = archive.testzip()
    except (OSError, zipfile.BadZipFile, RuntimeError) as exception:
        raise VerificationError(f"cannot parse ZIP archive: {exception}") from exception

    if not entries:
        raise VerificationError("ZIP archive contains no entries")
    if bad_member is not None:
        raise VerificationError(f"ZIP CRC validation failed for {bad_member}")
    if archive_comment:
        raise VerificationError("ZIP archive contains an unexpected comment")
    names = [entry.filename for entry in entries]
    if len(names) != len(set(names)):
        raise VerificationError("ZIP archive contains duplicate entry names")
    if names != sorted(names):
        raise VerificationError("ZIP entries are not in deterministic ordinal order")

    for entry in entries:
        name = entry.filename
        if entry.is_dir():
            raise VerificationError(f"ZIP contains an explicit directory entry: {name}")
        if entry.date_time != (1980, 1, 1, 0, 0, 0):
            raise VerificationError(f"ZIP timestamp is not normalized: {name}")
        if entry.compress_type != zipfile.ZIP_STORED:
            raise VerificationError(f"ZIP entry is not stored without compression: {name}")
        if entry.create_system != 3:
            raise VerificationError(f"ZIP entry does not record Unix mode metadata: {name}")
        if entry.extra or entry.comment:
            raise VerificationError(f"ZIP entry contains extra metadata/comment: {name}")
        expected_mode = stat.S_IFREG | (0o755 if name == "dotnetjq.exe" else 0o644)
        actual_mode = (entry.external_attr >> 16) & 0xFFFF
        if actual_mode != expected_mode:
            raise VerificationError(
                f"ZIP mode for {name} is {actual_mode:#o}, expected {expected_mode:#o}"
            )

    return len(entries)


def main() -> int:
    parser = argparse.ArgumentParser()
    subparsers = parser.add_subparsers(dest="mode", required=True)

    native = subparsers.add_parser("native")
    native.add_argument("archive", type=Path)
    native.add_argument("--format", required=True, choices=("zip", "tar.gz"))

    source = subparsers.add_parser("aot-source")
    source.add_argument("archive", type=Path)
    source.add_argument("--version", required=True)

    arguments = parser.parse_args()
    try:
        archive = require_regular_file(arguments.archive)
        if arguments.mode == "native" and arguments.format == "zip":
            count = verify_zip(archive)
        elif arguments.mode == "native":
            count = verify_tar(archive, mode="native", version=None)
        else:
            count = verify_tar(
                archive,
                mode="aot-source",
                version=arguments.version,
            )
    except VerificationError as exception:
        print(f"archive metadata verification failed: {exception}", file=sys.stderr)
        return 1

    print(f"archive metadata verified: {archive}: entries={count}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
