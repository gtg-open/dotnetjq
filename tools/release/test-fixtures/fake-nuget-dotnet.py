#!/usr/bin/env python3
"""Minimal dotnet-nuget test double for publication state-machine tests."""

from __future__ import annotations

import os
import sys
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path


def die(message: str) -> int:
    print(f"fake dotnet: {message}", file=sys.stderr)
    return 1


def local_name(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def identity(package: Path) -> tuple[str, str]:
    with zipfile.ZipFile(package, "r") as archive:
        names = [name for name in archive.namelist() if name.lower().endswith(".nuspec")]
        if len(names) != 1:
            raise ValueError("package does not contain exactly one nuspec")
        root = ET.fromstring(archive.read(names[0]))
        metadata = next(child for child in root if local_name(child.tag) == "metadata")

        def value(name: str) -> str:
            return next(
                child.text.strip()
                for child in metadata
                if local_name(child.tag) == name and child.text is not None
            )

        return value("id"), value("version")


def signed_copy(source: Path, destination: Path) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)
    temporary = destination.with_suffix(".tmp")
    with zipfile.ZipFile(source, "r") as source_archive, zipfile.ZipFile(
        temporary, "w"
    ) as destination_archive:
        for entry in source_archive.infolist():
            if entry.filename != ".signature.p7s":
                destination_archive.writestr(entry, source_archive.read(entry))
        destination_archive.writestr(".signature.p7s", b"fake repository signature")
    temporary.replace(destination)


def verify(arguments: list[str]) -> int:
    package = Path(arguments[-1])
    try:
        package_id, _ = identity(package)
        with zipfile.ZipFile(package, "r") as archive:
            if archive.namelist().count(".signature.p7s") != 1:
                return die("signature verification failed")
    except (OSError, ValueError, zipfile.BadZipFile, ET.ParseError) as error:
        return die(str(error))

    if package_id == os.environ.get("FAKE_NUGET_AUTHOR_ONLY_ID"):
        print("Signature type: Author")
    else:
        print("Signature type: Repository")
        print("Service index: https://api.nuget.org/v3/index.json")
        if package_id == os.environ.get("FAKE_NUGET_OWNER_MISMATCH_ID"):
            print("Owners: not-gtg-open")
        else:
            print("Owners: fixture-owner, GTG-OPEN")
    print(f"Successfully verified package '{package.name}'.")
    return 0


def push(arguments: list[str]) -> int:
    package = Path(arguments[2])
    storage = Path(os.environ["FAKE_NUGET_STORAGE"])
    state = Path(os.environ["FAKE_NUGET_STATE"])
    push_log = Path(os.environ["FAKE_NUGET_PUSH_LOG"])
    state.mkdir(parents=True, exist_ok=True)
    try:
        package_id, version = identity(package)
    except (OSError, ValueError, zipfile.BadZipFile, ET.ParseError) as error:
        return die(str(error))

    with push_log.open("a", encoding="utf-8") as stream:
        stream.write(f"{package_id}\n")
    counter_path = state / f"{package_id.lower()}.attempts"
    attempt = int(counter_path.read_text(encoding="ascii")) + 1 if counter_path.exists() else 1
    counter_path.write_text(str(attempt), encoding="ascii")

    if package_id == os.environ.get("FAKE_NUGET_FAIL_BEFORE_ACCEPT_ID") and attempt == 1:
        return die("simulated ambiguous response before acceptance")

    lower_id = package_id.lower()
    lower_version = version.lower()
    destination = (
        storage
        / lower_id
        / lower_version
        / f"{lower_id}.{lower_version}.nupkg"
    )
    signed_copy(package, destination)
    if package_id == os.environ.get("FAKE_NUGET_ACCEPT_THEN_FAIL_ID") and attempt == 1:
        return die("simulated connection loss after acceptance")
    print(f"Pushed {package_id} {version}")
    return 0


def main() -> int:
    arguments = sys.argv[1:]
    if len(arguments) >= 2 and arguments[:2] == ["nuget", "verify"]:
        return verify(arguments)
    if len(arguments) >= 3 and arguments[:2] == ["nuget", "push"]:
        return push(arguments)
    return die(f"unsupported invocation: {arguments!r}")


raise SystemExit(main())
