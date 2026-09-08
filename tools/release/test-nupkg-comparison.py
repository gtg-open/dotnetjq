#!/usr/bin/env python3
"""Fault-test package diagnostics; byte differences must never be ignored."""

import importlib.util
import tempfile
import zipfile
from pathlib import Path

SPEC = importlib.util.spec_from_file_location(
    "compare_nupkg", Path(__file__).with_name("compare-nupkg.py")
)
assert SPEC is not None and SPEC.loader is not None
COMPARER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(COMPARER)


def write_package(path, entries, comment=b""):
    with zipfile.ZipFile(path, "w") as archive:
        archive.comment = comment
        for name, payload in entries:
            archive.writestr(zipfile.ZipInfo(name, (1980, 1, 1, 0, 0, 0)), payload)


def main():
    with tempfile.TemporaryDirectory(prefix="dotnetjq-package-comparison-") as temporary:
        first, second = Path(temporary) / "first.nupkg", Path(temporary) / "second.nupkg"
        entries = [("a", b"one"), ("b", b"two")]
        write_package(first, entries)
        write_package(second, entries)
        assert COMPARER.compare(first, second)["byte_identical"]
        write_package(second, [("a", b"changed"), entries[1]])
        result = COMPARER.compare(first, second)
        assert not result["byte_identical"]
        assert [entry["name"] for entry in result["different_entries"]] == ["a"]
        write_package(second, list(reversed(entries)))
        result = COMPARER.compare(first, second)
        assert not result["byte_identical"] and not result["entry_order_identical"]
        assert result["different_entries"] == []
        write_package(second, entries, b"different ZIP metadata")
        result = COMPARER.compare(first, second)
        assert not result["byte_identical"] and not result["archive_comment_identical"]
        assert result["different_entries"] == []
        write_package(second, entries[:1])
        result = COMPARER.compare(first, second)
        assert not result["byte_identical"]
        assert result["different_entries"][0]["second"] is None
    print("nupkg comparison fault tests passed (5/5)")


if __name__ == "__main__":
    main()
