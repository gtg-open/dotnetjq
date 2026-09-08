#!/usr/bin/env python3
"""Golden-byte tests for the pinned WingetCreate submission representation."""

from __future__ import annotations

import hashlib
import importlib.util
from pathlib import Path
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
MODULE_PATH = Path(__file__).with_name("canonicalize-winget-manifest.py")
SPEC = importlib.util.spec_from_file_location("winget_canonicalizer", MODULE_PATH)
assert SPEC is not None and SPEC.loader is not None
CANONICALIZER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(CANONICALIZER)


def render(name: str, replacements: dict[str, str]) -> bytes:
    text = (ROOT / "packaging/winget" / name).read_text(encoding="utf-8")
    for token, value in replacements.items():
        text = text.replace(token, value)
    if "__" in text:
        raise AssertionError(f"unresolved token in {name}")
    return CANONICALIZER.canonical_bytes(text.encode("utf-8"))


class CanonicalizationTests(unittest.TestCase):
    maxDiff = None

    def test_all_three_manifests_match_pinned_wingetcreate_bytes(self) -> None:
        common = {"__PACKAGE_ID__": "GtGOpen.DotNetJq", "__VERSION__": "1.0.0"}
        sha_x64 = "A" * 64
        sha_arm64 = "B" * 64
        cases = {
            "version.yaml.template": (
                common,
                """# Created using wingetcreate 1.12.13.0
# yaml-language-server: $schema=https://aka.ms/winget-manifest.version.1.10.0.schema.json

PackageIdentifier: GtGOpen.DotNetJq
PackageVersion: 1.0.0
DefaultLocale: en-US
ManifestType: version
ManifestVersion: 1.10.0
""",
            ),
            "installer.yaml.template": (
                {
                    **common,
                    "__WIN_X64_URL__": "https://example.invalid/dotnetjq-win-x64.zip",
                    "__WIN_X64_SHA256__": sha_x64,
                    "__WIN_ARM64_URL__": "https://example.invalid/dotnetjq-win-arm64.zip",
                    "__WIN_ARM64_SHA256__": sha_arm64,
                },
                f"""# Created using wingetcreate 1.12.13.0
# yaml-language-server: $schema=https://aka.ms/winget-manifest.installer.1.10.0.schema.json

PackageIdentifier: GtGOpen.DotNetJq
PackageVersion: 1.0.0
InstallerType: zip
Installers:
- Architecture: x64
  InstallerUrl: https://example.invalid/dotnetjq-win-x64.zip
  InstallerSha256: {sha_x64}
  NestedInstallerType: portable
  NestedInstallerFiles:
  - RelativeFilePath: dotnetjq.exe
    PortableCommandAlias: dotnetjq
- Architecture: arm64
  InstallerUrl: https://example.invalid/dotnetjq-win-arm64.zip
  InstallerSha256: {sha_arm64}
  NestedInstallerType: portable
  NestedInstallerFiles:
  - RelativeFilePath: dotnetjq.exe
    PortableCommandAlias: dotnetjq
ManifestType: installer
ManifestVersion: 1.10.0
""",
            ),
            "locale.en-US.yaml.template": (
                {
                    **common,
                    "__PUBLISHER__": "GtG Open",
                    "__REPOSITORY_URL__": "https://github.com/gtg-open/dotnetjq",
                    "__LICENSE_URL__": "https://github.com/gtg-open/dotnetjq/blob/v1.0.0/LICENSES.md",
                },
                """# Created using wingetcreate 1.12.13.0
# yaml-language-server: $schema=https://aka.ms/winget-manifest.defaultLocale.1.10.0.schema.json

PackageIdentifier: GtGOpen.DotNetJq
PackageVersion: 1.0.0
PackageLocale: en-US
Publisher: GtG Open
PackageName: DotNetJq
PackageUrl: https://github.com/gtg-open/dotnetjq
License: Multiple licenses; see LICENSES.md
LicenseUrl: https://github.com/gtg-open/dotnetjq/blob/v1.0.0/LICENSES.md
ShortDescription: jq 1.8.2-compatible command-line JSON processor for .NET and NativeAOT.
ManifestType: defaultLocale
ManifestVersion: 1.10.0
""",
            ),
        }
        for template, (replacements, expected) in cases.items():
            with self.subTest(template=template):
                self.assertEqual(
                    expected.replace("\n", "\r\n").encode("utf-8"),
                    render(template, replacements),
                )

    def test_noncanonical_template_framing_is_rejected(self) -> None:
        with self.assertRaises(CANONICALIZER.CanonicalizationError):
            CANONICALIZER.canonical_bytes(b"PackageIdentifier: Example.Bad\n")
        with self.assertRaises(CANONICALIZER.CanonicalizationError):
            CANONICALIZER.canonical_bytes(
                b"# yaml-language-server: $schema=https://aka.ms/winget-manifest.version.1.10.0.schema.json\r\n"
            )


class ManifestVerificationTests(unittest.TestCase):
    """Exercise the release Ruby validator with the actual canonicalizer output."""

    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory(prefix="dotnetjq-winget-verifier-")
        self.addCleanup(self.temporary.cleanup)
        self.directory = Path(self.temporary.name)
        self.package_id = "GtGOpen.DotNetJq"
        common = {"__PACKAGE_ID__": self.package_id, "__VERSION__": "1.0.0"}
        installer = dict(common)
        for architecture in ("x64", "arm64"):
            name = f"dotnetjq-1.0.0-win-{architecture}.zip"
            # This validator binds archive bytes by SHA-256; ZIP/executable
            # validity is checked separately by verify-native-archive.sh.
            archive_bytes = f"unit-test archive hash input: {architecture}".encode()
            (self.directory / name).write_bytes(archive_bytes)
            token = f"__WIN_{architecture.upper()}"
            installer[f"{token}_URL__"] = (
                f"https://github.com/gtg-open/dotnetjq/releases/download/v1.0.0/{name}"
            )
            installer[f"{token}_SHA256__"] = hashlib.sha256(archive_bytes).hexdigest().upper()
        replacements = {
            "version": common,
            "installer": installer,
            "locale.en-US": {
                **common,
                "__PUBLISHER__": "GtG Open",
                "__REPOSITORY_URL__": "https://github.com/gtg-open/dotnetjq",
                "__LICENSE_URL__": "https://github.com/gtg-open/dotnetjq/blob/v1.0.0/LICENSES.md",
            },
        }
        self.manifests = {}
        for kind, values in replacements.items():
            suffix = "" if kind == "version" else f".{kind}"
            path = self.directory / f"{self.package_id}{suffix}.yaml"
            contents = render(f"{kind}.yaml.template", values)
            path.write_bytes(contents)
            self.manifests[kind] = (path, contents)

    def verify(self) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            [
                "ruby", str(ROOT / "tools/release/verify-winget-manifests.rb"),
                str(self.directory), self.package_id, "1.0.0",
                "gtg-open/dotnetjq", "v1.0.0", "GtG Open",
            ],
            check=False, capture_output=True, text=True, timeout=30,
        )

    def test_canonical_output_passes_full_yaml_and_archive_hash_verification(self) -> None:
        result = self.verify()
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("WinGet manifests verified", result.stdout)

    def test_all_manifest_headers_remain_exact(self) -> None:
        created_by = CANONICALIZER.CREATED_BY.replace("\n", "\r\n").encode()
        mutations = {
            "missing producer": lambda value: value.removeprefix(created_by),
            "wrong producer": lambda value: value.replace(b"1.12.13.0", b"1.12.12.0", 1),
            "wrong schema": lambda value: value.replace(b".1.10.0.schema", b".1.9.0.schema", 1),
            "missing schema": lambda value: b"\r\n".join(value.split(b"\r\n")[:1] + value.split(b"\r\n")[2:]),
            "extra leading comment": lambda value: b"# extra\r\n" + value,
            "missing separator": lambda value: value.replace(b"\r\n\r\n", b"\r\n", 1),
            "UTF-8 BOM": lambda value: b"\xef\xbb\xbf" + value,
            "LF only": lambda value: value.replace(b"\r\n", b"\n"),
            "mixed line endings": lambda value: value.replace(b"PackageIdentifier: GtGOpen.DotNetJq\r\n", b"PackageIdentifier: GtGOpen.DotNetJq\n"),
        }
        for kind, (path, original) in self.manifests.items():
            for label, mutate in mutations.items():
                with self.subTest(manifest=kind, mutation=label):
                    path.write_bytes(mutate(original))
                    result = self.verify()
                    self.assertNotEqual(0, result.returncode, result.stdout + result.stderr)
                    self.assertIn(str(path), result.stderr)
                    path.write_bytes(original)

    def test_schema_structure_duplicate_keys_and_archive_hashes_still_fail_closed(self) -> None:
        mutations = [
            ("version", b"ManifestType: version", b"ManifestType: installer"),
            ("version", b"DefaultLocale: en-US", b"DefaultLocale: en-US\r\nDefaultLocale: en-US"),
            ("installer", b"Architecture: arm64", b"Architecture: x64"),
            ("installer", b"PortableCommandAlias: dotnetjq", b"PortableCommandAlias: different"),
            ("installer", b"https://github.com/gtg-open/dotnetjq/", b"https://example.invalid/"),
            ("locale.en-US", b"Publisher: GtG Open", b"Publisher: Someone Else"),
        ]
        for kind, old, new in mutations:
            with self.subTest(manifest=kind, mutation=old):
                path, original = self.manifests[kind]
                self.assertIn(old, original)
                path.write_bytes(original.replace(old, new, 1))
                result = self.verify()
                self.assertNotEqual(0, result.returncode, result.stdout + result.stderr)
                path.write_bytes(original)
        (self.directory / "dotnetjq-1.0.0-win-arm64.zip").write_bytes(b"changed archive")
        result = self.verify()
        self.assertNotEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("SHA-256", result.stderr)


if __name__ == "__main__":
    unittest.main(verbosity=2)
