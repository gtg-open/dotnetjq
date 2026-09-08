#!/usr/bin/env python3
"""Create and verify deterministic provenance for performance-test publications."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import pathlib
import re
import shutil
import subprocess
import sys
from typing import Mapping, Sequence


ATTESTATION_FILENAME = ".dotnetjq-build-attestation.json"
SNAPSHOT_SCHEMA_VERSION = 1
ATTESTATION_SCHEMA_VERSION = 1
DEPLOYMENTS = ("framework-dotnetjq", "aot-dotnetjq")
SOURCE_ROOTS = (
    "src/DotNetJq",
    "src/DotNetJq.Cli",
    "src/DotNetJq.GlibcCompat",
)
SOURCE_FILES = ("Directory.Build.props", "global.json")
EXPECTED_PUBLISH_COMMANDS = {
    "framework-dotnetjq": [
        "dotnet",
        "publish",
        "src/DotNetJq.Cli/DotNetJq.Cli.csproj",
        "--configuration",
        "Release",
        "--runtime",
        "linux-x64",
        "--self-contained",
        "false",
        "--output",
        "artifacts/performance/framework",
        "--artifacts-path",
        "artifacts/performance/build/framework",
        "-p:PublishAot=false",
        "-p:NuGetAudit=false",
        "--nologo",
    ],
    "aot-dotnetjq": [
        "dotnet",
        "publish",
        "src/DotNetJq.Cli/DotNetJq.Cli.csproj",
        "--configuration",
        "Release",
        "--runtime",
        "linux-x64",
        "--self-contained",
        "true",
        "--output",
        "artifacts/performance/native-aot",
        "--artifacts-path",
        "artifacts/performance/build/aot",
        "-p:PublishAot=true",
        "-p:DebugSymbols=false",
        "-p:DebugType=none",
        "-p:NuGetAudit=false",
        "--nologo",
    ],
}
EXPECTED_OUTPUTS = {
    "framework-dotnetjq": "artifacts/performance/framework",
    "aot-dotnetjq": "artifacts/performance/native-aot",
}
EXPECTED_BUILD_ROOTS = {
    "framework-dotnetjq": "artifacts/performance/build/framework",
    "aot-dotnetjq": "artifacts/performance/build/aot",
}
NATIVE_AOT_PACKAGE_IDS = (
    "Microsoft.DotNet.ILCompiler",
    "Microsoft.NET.ILLink.Tasks",
    "Microsoft.NETCore.App.Runtime.NativeAOT.linux-x64",
    "runtime.linux-x64.Microsoft.DotNet.ILCompiler",
)


class AttestationError(RuntimeError):
    """A build cannot be proven to correspond to the declared inputs."""


def canonical_json_bytes(value: object) -> bytes:
    return json.dumps(
        value, ensure_ascii=False, sort_keys=True, separators=(",", ":")
    ).encode("utf-8")


def document_sha256(value: object) -> str:
    return hashlib.sha256(canonical_json_bytes(value)).hexdigest()


def sha256_file(path: pathlib.Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def file_record(path: pathlib.Path, base: pathlib.Path) -> dict[str, object]:
    return {
        "path": path.relative_to(base).as_posix(),
        "bytes": path.stat().st_size,
        "sha256": sha256_file(path),
    }


def manifest_for_paths(paths: Sequence[pathlib.Path], base: pathlib.Path) -> dict[str, object]:
    entries = [file_record(path, base) for path in sorted(set(paths))]
    return {
        "files": len(entries),
        "bytes": sum(int(entry["bytes"]) for entry in entries),
        "sha256": document_sha256(entries),
        "entries": entries,
    }


def directory_manifest(
    directory: pathlib.Path, excluded_names: Sequence[str] = ()
) -> dict[str, object]:
    if not directory.is_dir():
        raise AttestationError(f"artifact directory is unavailable: {directory}")
    excluded = set(excluded_names)
    paths = [
        path
        for path in directory.rglob("*")
        if path.is_file() and path.name not in excluded
    ]
    if not paths:
        raise AttestationError(f"artifact directory is empty: {directory}")
    return manifest_for_paths(paths, directory)


def git_value(root: pathlib.Path, arguments: Sequence[str]) -> str:
    try:
        return subprocess.run(
            ["git", "-C", str(root), *arguments],
            check=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
        ).stdout
    except (OSError, subprocess.CalledProcessError) as error:
        raise AttestationError(f"cannot inspect source repository: {root}") from error


def source_snapshot(root: pathlib.Path) -> dict[str, object]:
    paths = [root / relative for relative in SOURCE_FILES]
    for relative in SOURCE_ROOTS:
        source_root = root / relative
        if not source_root.is_dir():
            raise AttestationError(f"compiled source directory is unavailable: {source_root}")
        paths.extend(
            candidate
            for candidate in source_root.rglob("*")
            if candidate.is_file()
            and "bin" not in candidate.relative_to(source_root).parts
            and "obj" not in candidate.relative_to(source_root).parts
        )
    missing = [path for path in paths if not path.is_file()]
    if missing:
        raise AttestationError(f"compiled source input is unavailable: {missing[0]}")
    status = git_value(root, ["status", "--porcelain=v1", "--untracked-files=all"])
    return {
        "git_head": git_value(root, ["rev-parse", "HEAD"]).strip(),
        "git_dirty": bool(status),
        "git_status_entries": len(status.splitlines()),
        "git_status_sha256": hashlib.sha256(status.encode("utf-8")).hexdigest(),
        "compiled_inputs": manifest_for_paths(paths, root),
    }


def capture_dotnet(
    dotnet_path: pathlib.Path,
    arguments: Sequence[str],
    root: pathlib.Path,
    environment: Mapping[str, str] | None,
    timeout_seconds: float,
) -> str:
    try:
        result = subprocess.run(
            [str(dotnet_path), *arguments],
            check=False,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            cwd=root,
            env=None if environment is None else dict(environment),
            timeout=timeout_seconds,
        )
    except (OSError, subprocess.TimeoutExpired) as error:
        raise AttestationError(f"cannot inspect dotnet {' '.join(arguments)}") from error
    if result.returncode != 0:
        raise AttestationError(
            f"dotnet {' '.join(arguments)} exited {result.returncode}: {result.stderr.strip()}"
        )
    return result.stdout.strip()


def native_tool_candidates(
    root: pathlib.Path,
    environment: Mapping[str, str] | None = None,
    timeout_seconds: float = 30.0,
) -> dict[str, dict[str, object] | None]:
    """Fingerprint host native tools that NativeAOT may select through PATH."""
    search_path = None if environment is None else environment.get("PATH")
    candidates: dict[str, dict[str, object] | None] = {}
    for name in ("clang", "cc", "ld"):
        command = shutil.which(name, path=search_path)
        if command is None:
            candidates[name] = None
            continue
        command_path = pathlib.Path(command)
        resolved_path = command_path.resolve()
        try:
            result = subprocess.run(
                [str(command_path), "--version"],
                check=False,
                stdin=subprocess.DEVNULL,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                text=True,
                cwd=root,
                env=None if environment is None else dict(environment),
                timeout=timeout_seconds,
            )
        except (OSError, subprocess.TimeoutExpired) as error:
            raise AttestationError(f"cannot inspect host tool {name}") from error
        if result.returncode != 0:
            raise AttestationError(
                f"{name} --version exited {result.returncode}: {result.stderr.strip()}"
            )
        candidates[name] = {
            "command_path": str(command_path),
            "resolved_path": str(resolved_path),
            "sha256": sha256_file(resolved_path),
            "version": (result.stdout + result.stderr).strip(),
        }
    return candidates


def dotnet_toolchain_identity(
    root: pathlib.Path,
    environment: Mapping[str, str] | None = None,
    timeout_seconds: float = 30.0,
) -> dict[str, object]:
    search_path = None if environment is None else environment.get("PATH")
    dotnet = shutil.which("dotnet", path=search_path)
    if dotnet is None:
        raise AttestationError("dotnet host is unavailable on PATH")
    dotnet_path = pathlib.Path(dotnet).resolve()
    version = capture_dotnet(dotnet_path, ["--version"], root, environment, timeout_seconds)
    sdk_inventory = capture_dotnet(
        dotnet_path, ["--list-sdks"], root, environment, timeout_seconds
    )
    runtime_inventory = capture_dotnet(
        dotnet_path, ["--list-runtimes"], root, environment, timeout_seconds
    )
    selected_sdk_path: pathlib.Path | None = None
    for line in sdk_inventory.splitlines():
        match = re.fullmatch(r"([^ ]+) \[(.+)\]", line.strip())
        if match and match.group(1) == version:
            selected_sdk_path = pathlib.Path(match.group(2)) / version
            break
    if selected_sdk_path is None or not selected_sdk_path.is_dir():
        raise AttestationError(f"selected .NET SDK {version} is absent from --list-sdks")
    identity_candidates = [
        selected_sdk_path / ".version",
        selected_sdk_path / "dotnet.dll",
        selected_sdk_path / "dotnet.runtimeconfig.json",
        selected_sdk_path / "MSBuild.dll",
        selected_sdk_path / "Microsoft.NETCoreSdk.BundledVersions.props",
        selected_sdk_path / "Microsoft.Common.CurrentVersion.targets",
        selected_sdk_path / "Sdks/Microsoft.NET.Sdk/Sdk/Sdk.props",
        selected_sdk_path / "Sdks/Microsoft.NET.Sdk/Sdk/Sdk.targets",
    ]
    identity_files = [path for path in identity_candidates if path.is_file()]
    if len(identity_files) != len(identity_candidates):
        raise AttestationError(
            f"selected .NET SDK identity files are incomplete: {selected_sdk_path}"
        )
    return {
        "host_path": str(dotnet_path),
        "host_sha256": sha256_file(dotnet_path),
        "sdk_version": version,
        "sdk_inventory": sdk_inventory.splitlines(),
        "runtime_inventory": runtime_inventory.splitlines(),
        "selected_sdk_path": str(selected_sdk_path.resolve()),
        "selected_sdk_identity": manifest_for_paths(identity_files, selected_sdk_path),
        "global_json": file_record(root / "global.json", root),
        "native_tool_candidates": native_tool_candidates(
            root, environment, timeout_seconds
        ),
    }


def version_key(value: str) -> tuple[int, ...]:
    core = value.split("-", 1)[0]
    try:
        return tuple(int(part) for part in core.split("."))
    except ValueError as error:
        raise AttestationError(f"unsupported .NET runtime version: {value!r}") from error


def framework_runtime_identity(
    framework_directory: pathlib.Path,
    toolchain: Mapping[str, object],
) -> dict[str, object]:
    runtimeconfigs = sorted(framework_directory.glob("*.runtimeconfig.json"))
    if len(runtimeconfigs) != 1:
        raise AttestationError(
            f"framework publication must contain exactly one runtimeconfig: {framework_directory}"
        )
    runtimeconfig_path = runtimeconfigs[0]
    try:
        runtimeconfig = json.loads(runtimeconfig_path.read_text(encoding="utf-8"))
        framework = runtimeconfig["runtimeOptions"]["framework"]
        framework_name = framework["name"]
        requested_version = framework["version"]
    except (OSError, json.JSONDecodeError, KeyError, TypeError) as error:
        raise AttestationError(f"invalid runtimeconfig: {runtimeconfig_path}") from error
    if framework_name != "Microsoft.NETCore.App":
        raise AttestationError(f"unexpected framework dependency: {framework_name!r}")
    requested = version_key(str(requested_version))
    candidates: list[tuple[tuple[int, ...], str, pathlib.Path]] = []
    for raw_line in toolchain.get("runtime_inventory", []):
        match = re.fullmatch(r"Microsoft\.NETCore\.App ([^ ]+) \[(.+)\]", str(raw_line))
        if not match:
            continue
        version = match.group(1)
        key = version_key(version)
        if key[:2] == requested[:2] and key >= requested:
            candidates.append((key, version, pathlib.Path(match.group(2)) / version))
    if not candidates:
        raise AttestationError(
            f"no installed Microsoft.NETCore.App runtime satisfies {requested_version}"
        )
    _, selected_version, runtime_path = max(candidates)
    runtime_path = runtime_path.resolve()
    if not runtime_path.is_dir():
        raise AttestationError(f"selected framework runtime is unavailable: {runtime_path}")
    return {
        "framework": framework_name,
        "requested_version": str(requested_version),
        "selected_version": selected_version,
        "path": str(runtime_path),
        "runtimeconfig_sha256": sha256_file(runtimeconfig_path),
        "files": directory_manifest(runtime_path),
    }


def package_payload_manifest(package_path: pathlib.Path) -> dict[str, object]:
    paths = [
        path
        for path in package_path.rglob("*")
        if path.is_file()
        and path.name != ".nupkg.metadata"
        and not path.name.endswith(".nupkg.sha512")
        and not path.name.endswith(".nuspec")
        and not path.name.endswith(".nupkg")
    ]
    if not paths:
        raise AttestationError(f"NativeAOT package payload is empty: {package_path}")
    return manifest_for_paths(paths, package_path)


def native_aot_build_identity(
    root: pathlib.Path, build_root: pathlib.Path | None = None
) -> dict[str, object]:
    selected_build_root = (
        (root / EXPECTED_BUILD_ROOTS["aot-dotnetjq"]).resolve()
        if build_root is None
        else build_root.resolve()
    )
    assets_path = selected_build_root / "obj/DotNetJq.Cli/project.assets.json"
    try:
        assets = json.loads(assets_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise AttestationError(f"cannot read NativeAOT project assets: {assets_path}") from error
    targets = [str(value) for value in assets.get("targets", {})]
    target = next(
        (value for value in targets if value.lower() == "net10.0/linux-x64"), None
    )
    if target is None:
        raise AttestationError(
            "NativeAOT project assets do not contain the net10.0/linux-x64 target"
        )
    libraries = {str(key).lower(): value for key, value in assets.get("libraries", {}).items()}
    package_roots = [pathlib.Path(value) for value in assets.get("packageFolders", {})]
    download_versions: dict[str, set[str]] = {}
    frameworks = assets.get("project", {}).get("frameworks", {})
    if isinstance(frameworks, dict):
        for framework in frameworks.values():
            if not isinstance(framework, dict):
                continue
            for dependency in framework.get("downloadDependencies", []):
                if not isinstance(dependency, dict):
                    continue
                package_name = str(dependency.get("name", "")).lower()
                version_range = str(dependency.get("version", ""))
                match = re.fullmatch(r"\[([^,]+), \1\]", version_range)
                if package_name and match:
                    download_versions.setdefault(package_name, set()).add(match.group(1))
    packages: list[dict[str, object]] = []
    for package_id in NATIVE_AOT_PACKAGE_IDS:
        prefix = package_id.lower() + "/"
        matches = [(key, value) for key, value in libraries.items() if key.startswith(prefix)]
        if len(matches) == 1:
            key, library = matches[0]
            version = key.split("/", 1)[1]
            relative_path = pathlib.Path(str(library.get("path", key)))
        elif len(matches) == 0 and len(download_versions.get(package_id.lower(), ())) == 1:
            version = next(iter(download_versions[package_id.lower()]))
            relative_path = pathlib.Path(package_id.lower()) / version
        else:
            raise AttestationError(
                f"NativeAOT assets must identify exactly one {package_id} package"
            )
        package_path = next(
            (
                root_path / relative_path
                for root_path in package_roots
                if (root_path / relative_path).is_dir()
            ),
            None,
        )
        if package_path is None:
            raise AttestationError(f"NativeAOT package cache entry is unavailable: {key}")
        sha512_path = package_path / f"{package_id.lower()}.{version}.nupkg.sha512"
        nuspec_path = package_path / f"{package_id.lower()}.nuspec"
        if not sha512_path.is_file() or not nuspec_path.is_file():
            raise AttestationError(f"NativeAOT package metadata is incomplete: {package_path}")
        packages.append(
            {
                "id": package_id,
                "version": version,
                "path": str(package_path.resolve()),
                "nupkg_sha512": sha512_path.read_text(encoding="ascii").strip(),
                "identity_files": manifest_for_paths(
                    [sha512_path, nuspec_path], package_path
                ),
                "payload": package_payload_manifest(package_path),
            }
        )
    return {
        "target": target,
        "project_assets_path": str(assets_path),
        "project_assets_sha256": sha256_file(assets_path),
        "packages": packages,
        "packages_sha256": document_sha256(packages),
    }


def verify_native_aot_packages(identity: Mapping[str, object]) -> None:
    assets_path = pathlib.Path(str(identity.get("project_assets_path")))
    if (
        not assets_path.is_file()
        or sha256_file(assets_path) != identity.get("project_assets_sha256")
    ):
        raise AttestationError("NativeAOT project assets changed after publication")
    packages = identity.get("packages")
    if not isinstance(packages, list) or len(packages) != len(NATIVE_AOT_PACKAGE_IDS):
        raise AttestationError("NativeAOT package attestation is incomplete")
    if [item.get("id") for item in packages if isinstance(item, dict)] != list(
        NATIVE_AOT_PACKAGE_IDS
    ):
        raise AttestationError("NativeAOT package attestation has unexpected package IDs")
    if identity.get("packages_sha256") != document_sha256(packages):
        raise AttestationError("NativeAOT package attestation digest is invalid")
    for package in packages:
        if not isinstance(package, dict):
            raise AttestationError("NativeAOT package attestation is invalid")
        package_path = pathlib.Path(str(package.get("path")))
        recorded_manifest = package.get("identity_files")
        if not isinstance(recorded_manifest, dict):
            raise AttestationError("NativeAOT package identity manifest is invalid")
        entries = recorded_manifest.get("entries")
        if not isinstance(entries, list):
            raise AttestationError("NativeAOT package identity entries are invalid")
        current_paths = [package_path / str(entry["path"]) for entry in entries]
        if any(not path.is_file() for path in current_paths):
            raise AttestationError(f"NativeAOT package metadata changed: {package_path}")
        current = manifest_for_paths(current_paths, package_path)
        if current != recorded_manifest:
            raise AttestationError(f"NativeAOT package metadata changed: {package_path}")
        recorded_payload = package.get("payload")
        if not isinstance(recorded_payload, dict):
            raise AttestationError("NativeAOT package payload manifest is invalid")
        if package_payload_manifest(package_path) != recorded_payload:
            raise AttestationError(f"NativeAOT package payload changed: {package_path}")


def envelope(schema_version: int, body_name: str, body: dict[str, object]) -> dict[str, object]:
    return {
        "schema_version": schema_version,
        body_name: body,
        f"{body_name}_sha256": document_sha256(body),
    }


def write_json(path: pathlib.Path, document: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(
        json.dumps(document, ensure_ascii=False, sort_keys=True, indent=2) + "\n",
        encoding="utf-8",
    )


def read_envelope(
    path: pathlib.Path, schema_version: int, body_name: str
) -> dict[str, object]:
    try:
        document = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise AttestationError(f"cannot read build provenance: {path}") from error
    body = document.get(body_name)
    if document.get("schema_version") != schema_version or not isinstance(body, dict):
        raise AttestationError(f"unsupported or invalid build provenance: {path}")
    if document.get(f"{body_name}_sha256") != document_sha256(body):
        raise AttestationError(f"build provenance digest is invalid: {path}")
    return body


def create_snapshot(root: pathlib.Path, output: pathlib.Path) -> None:
    body = {
        "source": source_snapshot(root),
        "dotnet": dotnet_toolchain_identity(root),
    }
    write_json(output, envelope(SNAPSHOT_SCHEMA_VERSION, "snapshot", body))


def create_attestation(
    root: pathlib.Path,
    snapshot_path: pathlib.Path,
    deployment: str,
    publish_command: Sequence[str],
) -> pathlib.Path:
    if deployment not in DEPLOYMENTS:
        raise AttestationError(f"unknown deployment: {deployment}")
    expected_command = EXPECTED_PUBLISH_COMMANDS[deployment]
    if list(publish_command) != expected_command:
        raise AttestationError(
            f"publish command for {deployment} differs from the required deterministic command"
        )
    snapshot = read_envelope(snapshot_path, SNAPSHOT_SCHEMA_VERSION, "snapshot")
    current_source = source_snapshot(root)
    current_dotnet = dotnet_toolchain_identity(root)
    if snapshot.get("source") != current_source:
        raise AttestationError("compiled source inputs changed during publication")
    if snapshot.get("dotnet") != current_dotnet:
        raise AttestationError(".NET host, SDK, or runtime inventory changed during publication")
    output_directory = (root / EXPECTED_OUTPUTS[deployment]).resolve()
    build_root = (root / EXPECTED_BUILD_ROOTS[deployment]).resolve()
    executable = output_directory / "DotNetJq.Cli"
    if not executable.is_file() or not os.access(executable, os.X_OK):
        raise AttestationError(f"published executable is unavailable: {executable}")
    artifact_manifest = directory_manifest(
        output_directory, excluded_names=(ATTESTATION_FILENAME,)
    )
    body: dict[str, object] = {
        "deployment": deployment,
        "target_framework": "net10.0",
        "runtime_identifier": "linux-x64",
        "publish_command": list(publish_command),
        "source": current_source,
        "dotnet": current_dotnet,
        "artifact_directory": str(output_directory),
        "build_artifacts_directory": str(build_root),
        "executable": file_record(executable, output_directory),
        "artifacts": artifact_manifest,
        "build_artifacts": directory_manifest(build_root),
    }
    if deployment == "framework-dotnetjq":
        body["framework_runtime"] = framework_runtime_identity(
            output_directory, current_dotnet
        )
    else:
        body["native_aot_build"] = native_aot_build_identity(root, build_root)
    output = output_directory / ATTESTATION_FILENAME
    write_json(output, envelope(ATTESTATION_SCHEMA_VERSION, "attestation", body))
    return output


def validate_attestation(
    root: pathlib.Path,
    deployment: str,
    executable: pathlib.Path,
    source: Mapping[str, object],
    dotnet: Mapping[str, object],
) -> dict[str, object]:
    path = executable.parent / ATTESTATION_FILENAME
    body = read_envelope(path, ATTESTATION_SCHEMA_VERSION, "attestation")
    if body.get("deployment") != deployment:
        raise AttestationError(f"attestation deployment mismatch: {path}")
    if body.get("target_framework") != "net10.0" or body.get("runtime_identifier") != "linux-x64":
        raise AttestationError(f"attestation target mismatch: {path}")
    if body.get("publish_command") != EXPECTED_PUBLISH_COMMANDS[deployment]:
        raise AttestationError(f"attestation publish command mismatch: {path}")
    expected_directory = (root / EXPECTED_OUTPUTS[deployment]).resolve()
    expected_build_root = (root / EXPECTED_BUILD_ROOTS[deployment]).resolve()
    if executable.resolve() != expected_directory / "DotNetJq.Cli":
        raise AttestationError(
            f"publishable {deployment} executable must be {expected_directory / 'DotNetJq.Cli'}"
        )
    if body.get("artifact_directory") != str(expected_directory):
        raise AttestationError(f"attestation artifact directory mismatch: {path}")
    if body.get("build_artifacts_directory") != str(expected_build_root):
        raise AttestationError(f"attestation build artifacts directory mismatch: {path}")
    if body.get("source") != source:
        raise AttestationError(f"attested source does not match current source: {path}")
    if body.get("dotnet") != dotnet:
        raise AttestationError(f"attested .NET toolchain does not match current host: {path}")
    current_artifacts = directory_manifest(
        expected_directory, excluded_names=(ATTESTATION_FILENAME,)
    )
    if body.get("artifacts") != current_artifacts:
        raise AttestationError(f"published artifacts do not match their attestation: {path}")
    if body.get("build_artifacts") != directory_manifest(expected_build_root):
        raise AttestationError(f"build intermediates do not match their attestation: {path}")
    if body.get("executable") != file_record(executable, expected_directory):
        raise AttestationError(f"published executable does not match its attestation: {path}")
    if deployment == "framework-dotnetjq":
        current_runtime = framework_runtime_identity(expected_directory, dotnet)
        if body.get("framework_runtime") != current_runtime:
            raise AttestationError(f"selected framework runtime changed: {path}")
    else:
        native_aot = body.get("native_aot_build")
        if not isinstance(native_aot, dict) or native_aot.get("target") != "net10.0/linux-x64":
            raise AttestationError(f"NativeAOT build metadata is invalid: {path}")
        verify_native_aot_packages(native_aot)
    return {
        "path": str(path),
        "sha256": sha256_file(path),
        "schema_version": ATTESTATION_SCHEMA_VERSION,
        "deployment": deployment,
        "publish_command": body["publish_command"],
        "source_manifest_sha256": source["compiled_inputs"]["sha256"],
        "artifact_manifest_sha256": current_artifacts["sha256"],
        "build_artifact_manifest_sha256": body["build_artifacts"]["sha256"],
        "build_artifacts_directory": str(expected_build_root),
        "native_tool_candidates": dotnet["native_tool_candidates"],
        "runtime": (
            {
                "kind": "CoreCLR",
                "version": body["framework_runtime"]["selected_version"],
                "manifest_sha256": body["framework_runtime"]["files"]["sha256"],
            }
            if deployment == "framework-dotnetjq"
            else {
                "kind": "NativeAOT",
                "target": body["native_aot_build"]["target"],
                "packages_sha256": body["native_aot_build"]["packages_sha256"],
                "packages": [
                    {"id": item["id"], "version": item["version"]}
                    for item in body["native_aot_build"]["packages"]
                ],
            }
        ),
    }


def prepare_output(root: pathlib.Path, deployment: str) -> None:
    if deployment not in DEPLOYMENTS:
        raise AttestationError(f"unknown deployment: {deployment}")
    targets = (
        (root / EXPECTED_OUTPUTS[deployment], root / "artifacts/performance"),
        (root / EXPECTED_BUILD_ROOTS[deployment], root / "artifacts/performance/build"),
    )
    for target, allowed_parent in targets:
        if target.parent.resolve() != allowed_parent.resolve() or target.is_symlink():
            raise AttestationError(
                f"refusing to prepare unexpected or symbolic output directory: {target}"
            )
        if target.exists():
            if not target.is_dir():
                raise AttestationError(f"output path is not a directory: {target}")
            shutil.rmtree(target)
        target.mkdir(parents=True)


def parse_arguments(argv: Sequence[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", required=True)
    subparsers = parser.add_subparsers(dest="action", required=True)
    snapshot = subparsers.add_parser("snapshot")
    snapshot.add_argument("--output", required=True)
    prepare = subparsers.add_parser("prepare-output")
    prepare.add_argument("--deployment", required=True, choices=DEPLOYMENTS)
    create = subparsers.add_parser("create")
    create.add_argument("--snapshot", required=True)
    create.add_argument("--deployment", required=True, choices=DEPLOYMENTS)
    create.add_argument("publish_command", nargs=argparse.REMAINDER)
    return parser.parse_args(argv)


def main(argv: Sequence[str]) -> int:
    arguments = parse_arguments(argv)
    root = pathlib.Path(arguments.root).expanduser().resolve()
    if arguments.action == "snapshot":
        create_snapshot(root, pathlib.Path(arguments.output).expanduser().resolve())
    elif arguments.action == "prepare-output":
        prepare_output(root, arguments.deployment)
    else:
        command = arguments.publish_command
        if command[:1] == ["--"]:
            command = command[1:]
        create_attestation(
            root,
            pathlib.Path(arguments.snapshot).expanduser().resolve(),
            arguments.deployment,
            command,
        )
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main(sys.argv[1:]))
    except AttestationError as error:
        print(f"build attestation failed: {error}", file=sys.stderr)
        raise SystemExit(1) from error
