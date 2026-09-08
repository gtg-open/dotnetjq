#!/usr/bin/env python3
"""Diagnose unequal cold-build packages without normalizing either input."""

from __future__ import annotations

import argparse
import hashlib
import json
import zipfile
from pathlib import Path


def digest(content: bytes) -> str:
    return hashlib.sha256(content).hexdigest()


def inventory(path: Path) -> dict:
    with zipfile.ZipFile(path) as archive:
        entries = []
        for entry in archive.infolist():
            payload = archive.read(entry)
            item = {
                "name": entry.filename,
                "sha256": digest(payload),
                "size": len(payload),
                "timestamp": entry.date_time,
                "compression": entry.compress_type,
                "compressed_size": entry.compress_size,
                "creator": entry.create_system,
                "attributes": entry.external_attr,
                "extra": entry.extra.hex(),
                "comment": entry.comment.hex(),
            }
            # Diagnostic only: expose a managed PE's embedded CodeView PDB path
            # when present. Never edit it or use it to decide byte equality.
            marker = payload.find(b"RSDS") if entry.filename.endswith(".dll") else -1
            if marker >= 0:
                item["codeview_path"] = payload[marker + 24:].split(b"\0", 1)[0].decode(
                    "utf-8", errors="replace"
                )
            entries.append(item)
        return {"sha256": digest(path.read_bytes()), "entries": entries,
                "archive_comment": archive.comment.hex()}


def compare(first: Path, second: Path) -> dict:
    left, right = inventory(first), inventory(second)
    left_entries = {entry["name"]: entry for entry in left["entries"]}
    right_entries = {entry["name"]: entry for entry in right["entries"]}
    names = sorted(left_entries.keys() | right_entries.keys())
    return {
        "byte_identical": left["sha256"] == right["sha256"],
        "first_sha256": left["sha256"],
        "second_sha256": right["sha256"],
        "entry_order_identical": list(left_entries) == list(right_entries),
        "archive_comment_identical": left["archive_comment"] == right["archive_comment"],
        "different_entries": [
            {"name": name, "first": left_entries.get(name), "second": right_entries.get(name)}
            for name in names if left_entries.get(name) != right_entries.get(name)
        ],
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("first", type=Path)
    parser.add_argument("second", type=Path)
    arguments = parser.parse_args()
    result = compare(arguments.first, arguments.second)
    print(json.dumps(result, indent=2))
    return 0 if result["byte_identical"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
