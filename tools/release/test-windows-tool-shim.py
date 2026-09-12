#!/usr/bin/env python3
"""Fault tests for the SDK Windows NativeAOT launcher/payload binding."""

from __future__ import annotations

import importlib.util
import tempfile
import zipfile
from pathlib import Path


SPEC = importlib.util.spec_from_file_location(
    "windows_tool_shim", Path(__file__).with_name("verify-windows-tool-shim.py")
)
assert SPEC is not None and SPEC.loader is not None
VERIFIER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(VERIFIER)


def fixture(root: Path, rid: str) -> tuple[Path, Path, Path, bytes]:
    tool = root / "tool path \u00e9"
    member = f"tools/any/{rid}/DotNetJq.Cli.exe"
    executable = tool / ".store" / f"dotnetjq.{rid}" / "1.0.0" / member
    executable.parent.mkdir(parents=True)
    executable.write_bytes(b"MZ fixture native payload")
    relative = executable.relative_to(tool).as_posix().replace("/", "\\")
    content = f'@echo off\r\n"%~dp0{relative}" %*\r\n'.encode("utf-8")
    (tool / "dotnetjq.cmd").write_bytes(content)
    package = root / "rid.nupkg"
    with zipfile.ZipFile(package, "w") as archive:
        archive.writestr(member, executable.read_bytes())
        archive.writestr(f"tools/any/{rid}/DotnetToolSettings.xml", (
            '<DotNetCliTool Version="2"><Commands><Command Name="dotnetjq" '
            'EntryPoint="DotNetJq.Cli.exe" Runner="executable" /></Commands></DotNetCliTool>'
        ))
    return tool, package, executable, content


def main() -> int:
    checks = 0
    for rid in ("win-x64", "win-arm64"):
        for fault in ("none", "missing-shim", "LF-shim", "wrong-target", "changed-payload", "missing-payload", "duplicate-payload"):
            with tempfile.TemporaryDirectory(prefix="dotnetjq-sdk-shim-") as temporary:
                tool, package, executable, content = fixture(Path(temporary), rid)
                shim = tool / "dotnetjq.cmd"
                if fault == "missing-shim":
                    shim.unlink()
                elif fault == "LF-shim":
                    shim.write_bytes(content.replace(b"\r\n", b"\n"))
                elif fault == "wrong-target":
                    shim.write_bytes(content.replace(b"DotNetJq.Cli.exe", b"other.exe"))
                elif fault == "changed-payload":
                    executable.write_bytes(b"MZ changed payload")
                elif fault == "missing-payload":
                    executable.unlink()
                elif fault == "duplicate-payload":
                    duplicate = tool / ".store" / "unexpected" / "tools" / "any" / rid / executable.name
                    duplicate.parent.mkdir(parents=True)
                    duplicate.write_bytes(executable.read_bytes())
                try:
                    actual = VERIFIER.verify(tool, package, rid)
                except VERIFIER.VerificationError:
                    if fault == "none":
                        raise
                else:
                    if fault != "none" or actual != executable.resolve():
                        raise AssertionError(f"incorrect verification result for {rid}/{fault}")
                checks += 1
    print(f"Windows SDK tool shim fault tests passed ({checks}/{checks})")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
