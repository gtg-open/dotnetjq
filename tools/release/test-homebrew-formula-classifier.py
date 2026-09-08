#!/usr/bin/env python3
"""Focused fail-closed tests for classify-homebrew-formula.py."""

from __future__ import annotations

import os
import subprocess
import sys
import tempfile
from pathlib import Path


SCRIPT = Path(__file__).with_name("classify-homebrew-formula.py")


def formula(version: str, extra: str = "") -> bytes:
    return (
        "# Generated test formula\n"
        "class Dotnetjq < Formula\n"
        f'  version "{version}"\n'
        f"{extra}"
        "end\n"
    ).encode()


def invoke(
    current: Path, candidate: Path, version: str
) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [
            sys.executable,
            os.fspath(SCRIPT),
            "--current",
            os.fspath(current),
            "--candidate",
            os.fspath(candidate),
            "--version",
            version,
        ],
        check=False,
        capture_output=True,
        text=True,
    )


def expect_state(
    name: str,
    current_contents: bytes | None,
    candidate_contents: bytes,
    version: str,
    expected: str,
) -> None:
    with tempfile.TemporaryDirectory(prefix="dotnetjq-homebrew-classifier-") as root:
        directory = Path(root)
        current = directory / "tap" / "Formula" / "dotnetjq.rb"
        candidate = directory / "generated" / "dotnetjq.rb"
        candidate.parent.mkdir(parents=True)
        candidate.write_bytes(candidate_contents)
        if current_contents is not None:
            current.parent.mkdir(parents=True)
            current.write_bytes(current_contents)

        result = invoke(current, candidate, version)
        if result.returncode != 0 or result.stdout != f"{expected}\n" or result.stderr:
            raise AssertionError(
                f"{name}: expected successful {expected!r}, got "
                f"exit={result.returncode}, stdout={result.stdout!r}, "
                f"stderr={result.stderr!r}"
            )


def expect_failure(
    name: str,
    current_contents: bytes | None,
    candidate_contents: bytes,
    version: str,
) -> None:
    with tempfile.TemporaryDirectory(prefix="dotnetjq-homebrew-classifier-") as root:
        directory = Path(root)
        current = directory / "tap" / "Formula" / "dotnetjq.rb"
        candidate = directory / "generated" / "dotnetjq.rb"
        candidate.parent.mkdir(parents=True)
        candidate.write_bytes(candidate_contents)
        if current_contents is not None:
            current.parent.mkdir(parents=True)
            current.write_bytes(current_contents)

        result = invoke(current, candidate, version)
        if (
            result.returncode == 0
            or result.stdout
            or not result.stderr.startswith("Homebrew formula classifier:")
        ):
            raise AssertionError(
                f"{name}: expected fail-closed result, got "
                f"exit={result.returncode}, stdout={result.stdout!r}, "
                f"stderr={result.stderr!r}"
            )


def main() -> int:
    checks = 0

    candidate = formula("2.0.0")
    expect_state(
        "missing current formula", None, candidate, "2.0.0", "publish:missing"
    )
    checks += 1
    expect_state(
        "older current formula", formula("1.99.999"), candidate, "2.0.0", "publish:upgrade"
    )
    checks += 1
    expect_state(
        "large numeric components compare without integer conversion",
        formula("999999999999999999999999.0.0"),
        formula("1000000000000000000000000.0.0"),
        "1000000000000000000000000.0.0",
        "publish:upgrade",
    )
    checks += 1
    expect_state("identical retry", candidate, candidate, "2.0.0", "skip:identical")
    checks += 1
    expect_state(
        "newer current formula", formula("2.0.1"), candidate, "2.0.0", "skip:superseded"
    )
    checks += 1

    expect_failure(
        "same version with different bytes",
        formula("2.0.0", "  # remote edit\n"),
        candidate,
        "2.0.0",
    )
    checks += 1
    expect_failure("candidate version mismatch", None, formula("2.0.1"), "2.0.0")
    checks += 1
    expect_failure("prerelease target", None, formula("2.0.0"), "2.0.0-rc.1")
    checks += 1
    expect_failure("leading-zero target", None, formula("2.0.0"), "02.0.0")
    checks += 1
    expect_failure(
        "unprovable current version",
        b"class Dotnetjq < Formula\nend\n",
        candidate,
        "2.0.0",
    )
    checks += 1
    expect_failure(
        "dynamic current version",
        b'class Dotnetjq < Formula\n  version ENV["VERSION"]\nend\n',
        candidate,
        "2.0.0",
    )
    checks += 1
    expect_failure(
        "ambiguous current version",
        b'class Dotnetjq < Formula\n  version "1.0.0"\n  version "3.0.0"\nend\n',
        candidate,
        "2.0.0",
    )
    checks += 1
    expect_failure(
        "prerelease current version", formula("2.0.0-rc.1"), candidate, "2.0.0"
    )
    checks += 1
    expect_failure(
        "leading-zero current version", formula("01.0.0"), candidate, "2.0.0"
    )
    checks += 1
    expect_failure(
        "wrong formula class",
        b'class Other < Formula\n  version "1.0.0"\nend\n',
        candidate,
        "2.0.0",
    )
    checks += 1
    expect_failure("invalid UTF-8 candidate", None, b"\xff\n", "2.0.0")
    checks += 1
    expect_failure("CRLF candidate", None, candidate.replace(b"\n", b"\r\n"), "2.0.0")
    checks += 1
    expect_failure("truncated candidate", None, candidate.rstrip(b"\n"), "2.0.0")
    checks += 1

    with tempfile.TemporaryDirectory(prefix="dotnetjq-homebrew-classifier-") as root:
        directory = Path(root)
        current = directory / "current.rb"
        candidate_path = directory / "candidate.rb"
        real_current = directory / "real-current.rb"
        real_current.write_bytes(formula("1.0.0"))
        current.symlink_to(real_current)
        candidate_path.write_bytes(candidate)
        result = invoke(current, candidate_path, "2.0.0")
        if result.returncode == 0 or result.stdout:
            raise AssertionError("symlink current formula must fail closed")
    checks += 1

    print(f"Homebrew formula classifier tests passed: {checks}/{checks}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
