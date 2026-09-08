#!/usr/bin/env python3
"""Focused fault tests for verify-nupkg-archive.py."""

from __future__ import annotations

import importlib.util
import struct
import tempfile
import warnings
import zipfile
from pathlib import Path


SCRIPT_DIR = Path(__file__).resolve().parent
VERIFIER_PATH = SCRIPT_DIR / "verify-nupkg-archive.py"
SPEC = importlib.util.spec_from_file_location("verify_nupkg_archive", VERIFIER_PATH)
if SPEC is None or SPEC.loader is None:
    raise RuntimeError(f"could not load {VERIFIER_PATH}")
VERIFIER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(VERIFIER)


def write_entry(
    archive: zipfile.ZipFile,
    name: str,
    content: bytes,
    *,
    timestamp: tuple[int, int, int, int, int, int],
    compression: int,
    create_system: int,
    external_attributes: int,
    extra: bytes = b"",
) -> None:
    entry = zipfile.ZipInfo(name, timestamp)
    entry.compress_type = compression
    entry.create_system = create_system
    entry.external_attr = external_attributes
    entry.extra = extra
    archive.writestr(entry, content)


def expect_rejected(path: Path, fragment: str) -> None:
    try:
        VERIFIER.verify_package(path)
    except VERIFIER.VerificationError as exception:
        if fragment not in str(exception):
            raise AssertionError(
                f"wrong rejection for {path.name}: {exception!s}"
            ) from exception
    else:
        raise AssertionError(f"invalid fixture was accepted: {path.name}")


def main() -> int:
    with tempfile.TemporaryDirectory(prefix="dotnetjq-nupkg-metadata-") as temporary:
        root = Path(temporary)
        valid = root / "valid.nupkg"
        with zipfile.ZipFile(valid, mode="w") as archive:
            # NuGet packages may legitimately be created on Windows or Unix and
            # may mix stored/deflated entries or carry unrelated extra fields.
            # Those details are deliberately outside the timestamp contract.
            write_entry(
                archive,
                "sample.nuspec",
                b"<package />",
                timestamp=VERIFIER.CANONICAL_ZIP_TIMESTAMP,
                compression=zipfile.ZIP_STORED,
                create_system=0,
                external_attributes=0,
            )
            write_entry(
                archive,
                "tools/any/sample.txt",
                b"sample",
                timestamp=VERIFIER.CANONICAL_ZIP_TIMESTAMP,
                compression=zipfile.ZIP_DEFLATED,
                create_system=3,
                external_attributes=0o100644 << 16,
                extra=struct.pack("<HHI", 0xCAFE, 4, 0x12345678),
            )
        if VERIFIER.verify_package(valid) != 2:
            raise AssertionError("valid fixture returned the wrong entry count")

        wrong_local_timestamp = root / "wrong-local-timestamp.nupkg"
        wrong_local_bytes = bytearray(valid.read_bytes())
        with zipfile.ZipFile(valid, mode="r") as archive:
            local_header_offset = archive.infolist()[0].header_offset
        # Change only the local DOS date to 1980-01-02; leave the central
        # directory at the canonical epoch so the local check is isolated.
        struct.pack_into("<HH", wrong_local_bytes, local_header_offset + 10, 0, 0x22)
        wrong_local_timestamp.write_bytes(wrong_local_bytes)
        expect_rejected(wrong_local_timestamp, "local ZIP timestamp")

        wrong_timestamp = root / "wrong-timestamp.nupkg"
        with zipfile.ZipFile(wrong_timestamp, mode="w") as archive:
            write_entry(
                archive,
                "sample.nuspec",
                b"<package />",
                timestamp=(2026, 1, 2, 3, 4, 6),
                compression=zipfile.ZIP_DEFLATED,
                create_system=3,
                external_attributes=0o100644 << 16,
            )
        expect_rejected(wrong_timestamp, "central ZIP timestamp")

        duplicate = root / "duplicate.nupkg"
        with warnings.catch_warnings():
            warnings.simplefilter("ignore", UserWarning)
            with zipfile.ZipFile(duplicate, mode="w") as archive:
                for content in (b"first", b"second"):
                    write_entry(
                        archive,
                        "sample.nuspec",
                        content,
                        timestamp=VERIFIER.CANONICAL_ZIP_TIMESTAMP,
                        compression=zipfile.ZIP_STORED,
                        create_system=0,
                        external_attributes=0,
                    )
        expect_rejected(duplicate, "duplicate ZIP entry")

        unsafe = root / "unsafe.nupkg"
        with zipfile.ZipFile(unsafe, mode="w") as archive:
            write_entry(
                archive,
                "../sample.nuspec",
                b"<package />",
                timestamp=VERIFIER.CANONICAL_ZIP_TIMESTAMP,
                compression=zipfile.ZIP_STORED,
                create_system=0,
                external_attributes=0,
            )
        expect_rejected(unsafe, "non-canonical ZIP entry")

    print("nupkg ZIP metadata fault tests passed (5/5)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
