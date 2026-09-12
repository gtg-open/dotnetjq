#!/usr/bin/env python3
"""Verify the reproducible and safe ZIP representation of a NuGet package."""

from __future__ import annotations

import argparse
import struct
import sys
import zipfile
from pathlib import Path


CANONICAL_SOURCE_DATE_EPOCH = 315_532_800
CANONICAL_ZIP_TIMESTAMP = (1980, 1, 1, 0, 0, 0)
LOCAL_FILE_HEADER_SIGNATURE = 0x04034B50
LOCAL_FILE_HEADER_LENGTH = 30


class VerificationError(RuntimeError):
    """The nupkg is not a safe, reproducible ZIP archive."""


def require_package(path: Path) -> Path:
    if path.is_symlink():
        raise VerificationError(f"package must not be a symbolic link: {path}")
    try:
        resolved = path.resolve(strict=True)
    except (OSError, RuntimeError) as exception:
        raise VerificationError(f"cannot resolve package: {exception}") from exception
    if not resolved.is_file():
        raise VerificationError(f"package is not a regular file: {resolved}")
    if resolved.suffix.lower() != ".nupkg":
        raise VerificationError(f"package name must end in .nupkg: {resolved.name}")
    return resolved


def verify_safe_name(name: str) -> None:
    if not name or "\0" in name or "\\" in name or name.startswith("/"):
        raise VerificationError(f"unsafe ZIP entry name: {name!r}")
    parts = name.split("/")
    if any(part in ("", ".", "..") for part in parts):
        raise VerificationError(f"non-canonical ZIP entry name: {name!r}")
    if len(parts[0]) >= 2 and parts[0][0].isalpha() and parts[0][1] == ":":
        raise VerificationError(f"drive-qualified ZIP entry name: {name!r}")


def verify_local_timestamp(stream, entry: zipfile.ZipInfo) -> None:
    stream.seek(entry.header_offset)
    header = stream.read(LOCAL_FILE_HEADER_LENGTH)
    if len(header) != LOCAL_FILE_HEADER_LENGTH:
        raise VerificationError(f"truncated local ZIP header: {entry.filename}")
    if struct.unpack_from("<I", header)[0] != LOCAL_FILE_HEADER_SIGNATURE:
        raise VerificationError(f"invalid local ZIP header: {entry.filename}")

    dos_time, dos_date = struct.unpack_from("<HH", header, 10)
    # 1980-01-01 00:00:00 in the two DOS date/time words.
    if dos_time != 0 or dos_date != 0x21:
        raise VerificationError(
            f"local ZIP timestamp is not SOURCE_DATE_EPOCH="
            f"{CANONICAL_SOURCE_DATE_EPOCH}: {entry.filename}"
        )


def verify_package(path: Path) -> int:
    package = require_package(path)
    try:
        with package.open("rb") as stream, zipfile.ZipFile(stream, mode="r") as archive:
            entries = archive.infolist()
            if not entries:
                raise VerificationError("nupkg contains no entries")

            names: set[str] = set()
            for entry in entries:
                verify_safe_name(entry.filename)
                if entry.filename in names:
                    raise VerificationError(
                        f"nupkg contains a duplicate ZIP entry: {entry.filename}"
                    )
                names.add(entry.filename)
                if entry.date_time != CANONICAL_ZIP_TIMESTAMP:
                    raise VerificationError(
                        f"central ZIP timestamp is not SOURCE_DATE_EPOCH="
                        f"{CANONICAL_SOURCE_DATE_EPOCH}: {entry.filename}"
                    )
                verify_local_timestamp(stream, entry)

            bad_member = archive.testzip()
            if bad_member is not None:
                raise VerificationError(f"nupkg CRC validation failed: {bad_member}")
    except (OSError, RuntimeError, struct.error, zipfile.BadZipFile) as exception:
        if isinstance(exception, VerificationError):
            raise
        raise VerificationError(f"cannot parse nupkg ZIP archive: {exception}") from exception

    return len(entries)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("packages", type=Path, nargs="+")
    arguments = parser.parse_args()

    try:
        for package in arguments.packages:
            count = verify_package(package)
            print(f"nupkg ZIP metadata verified: {package}: entries={count}")
    except VerificationError as exception:
        print(f"nupkg ZIP metadata verification failed: {exception}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
