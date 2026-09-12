#!/usr/bin/env python3
"""Bind the SDK's Windows NativeAOT .cmd launcher to its exact RID payload."""

from __future__ import annotations

import argparse
import sys
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path


class VerificationError(Exception):
    pass


def verify(tool_directory: Path, package: Path, rid: str) -> Path:
    if rid not in ("win-x64", "win-arm64"):
        raise VerificationError(f"not a Windows release RID: {rid}")
    tool_directory = tool_directory.resolve()
    with zipfile.ZipFile(package) as archive:
        settings_path = f"tools/any/{rid}/DotnetToolSettings.xml"
        if archive.namelist().count(settings_path) != 1:
            raise VerificationError("expected exactly one RID tool settings entry")
        root = ET.fromstring(archive.read(settings_path))
        commands = root.findall("./Commands/Command")
        if len(commands) != 1:
            raise VerificationError("expected exactly one native tool command")
        command = commands[0]
        entry = command.get("EntryPoint", "")
        if (command.get("Name") != "dotnetjq" or command.get("Runner") != "executable"
                or not entry.endswith(".exe") or "/" in entry or "\\" in entry):
            raise VerificationError("unexpected native tool command or entry point")
        payload_path = f"tools/any/{rid}/{entry}"
        if archive.namelist().count(payload_path) != 1:
            raise VerificationError("expected exactly one native package payload")
        payload = archive.read(payload_path)

    store = tool_directory / ".store"
    if not store.is_dir() or store.is_symlink():
        raise VerificationError("installed private store is missing or is a symbolic link")
    matches = [
        path for path in store.rglob(entry)
        if path.parts[-4:] == ("tools", "any", rid, entry)
    ]
    if len(matches) != 1:
        raise VerificationError("expected exactly one installed native RID executable")
    executable = matches[0]
    if (not executable.is_file() or executable.is_symlink()
            or not executable.resolve().is_relative_to(store.resolve())):
        raise VerificationError("installed native executable escapes the private store")
    if executable.read_bytes() != payload:
        raise VerificationError("installed native executable differs from its verified package")

    # SDK 10.0.400 ShellShimRepository.CreateShim/GetShimPath uses this exact
    # UTF-8/CRLF batch form for Runner=executable, not a managed .exe apphost.
    relative = executable.relative_to(tool_directory).as_posix().replace("/", "\\")
    expected = f'@echo off\r\n"%~dp0{relative}" %*\r\n'.encode("utf-8")
    shim = tool_directory / "dotnetjq.cmd"
    if not shim.is_file() or shim.is_symlink() or shim.read_bytes() != expected:
        raise VerificationError("Windows tool shim differs from the exact SDK launcher or target")
    return executable


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tool-directory", type=Path, required=True)
    parser.add_argument("--package", type=Path, required=True)
    parser.add_argument("--rid", required=True)
    args = parser.parse_args()
    try:
        executable = verify(args.tool_directory, args.package, args.rid)
    except (VerificationError, OSError, KeyError, ET.ParseError, zipfile.BadZipFile) as error:
        print(f"Windows tool shim verification failed: {error}", file=sys.stderr)
        return 1
    sys.stdout.reconfigure(encoding="utf-8")
    print(executable)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
