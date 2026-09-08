# Release packaging inputs

This directory contains declarative inputs and templates used by
`tools/release/`. It does not contain credentials, repository ownership, or a
publication destination.

`release-targets.tsv` is the complete supported 64-bit NativeAOT matrix. The
host operating system column is enforced because NativeAOT cannot target a
different operating-system family. Architecture cross-compilation is not
forbidden by the scripts, but release CI should use a native runner for every
row.

`release-license-paths.txt` is the minimum license/notice payload required in
every standalone binary archive and every RID package. Directory entries are
copied and validated recursively.

`nativeaot-runtime-license-paths.txt` names the two additional files required
beside every NativeAOT executable. They are not repository snapshots: release
builds copy their bytes from the exact SDK-resolved runtime pack for the target
RID, and archive/package verification resolves that pack independently and
compares both files byte-for-byte. The RID-selector package has no executable,
so it does not substitute one platform's runtime notices for all platforms.

`aot-source-paths.txt` defines the expanded corresponding source required at
`aot-source/` in every NativeAOT NuGet package. The verifier compares every
listed file byte-for-byte with the release checkout and rejects missing or
additional files in that subtree. Build-output directories are excluded.

The WinGet and Homebrew files are inert templates. Their generators require an
explicit GitHub `owner/repository`, publisher, package identifier, and version;
no value is inferred from a Git remote. The generated prebuilt,
platform-specific Homebrew formula is for a project-owned custom tap, not
`homebrew/core`. Release verification performs deterministic regeneration,
Ruby syntax checks, and structural WinGet YAML validation; actual package-
manager mutation is deliberately separate from metadata generation and occurs
only in the protected tag-release jobs after the complete bundle gate.

NuGet identity follows the same rule. Every library, selector, and RID pack
requires explicit `Authors` and `RepositoryUrl` MSBuild inputs. The package-set
gate requires those expected values independently and rejects any difference in
the exact Authors/repository metadata across the ten packages.

## Runtime globalization dependency

The CLI is published with `InvariantGlobalization=false` and does not enable
app-local ICU. A NativeAOT archive is self-contained with respect to the .NET
runtime, but it still uses operating-system globalization support:

- Linux installations must provide ICU. Alpine/musl uses the `icu-libs`
  package, which the musl CI jobs install before running their smoke suites.
- The Homebrew formula declares a Linux-only dependency on `icu4c@78`; the
  formula installs the native executable under `libexec` and gives only its
  generated launcher the keg's library directory. The archive does not copy
  ICU into its application directory or alter the macOS command.
- The glibc Linux archives are built on Ubuntu 24.04 / glibc 2.39. They
  intentionally require glibc 2.39 or newer, not compatibility with older
  glibc systems. Alpine users must select the matching `linux-musl-*` archive
  instead.
- macOS uses the ICU implementation supplied by the operating system, so the
  Homebrew formula does not install a second ICU there.
- Windows uses ICU supplied by Windows when available and .NET's Windows NLS
  fallback otherwise; no separate ICU package is shipped with `dotnetjq`.

The framework-dependent `DotNetJq.Library` package still requires a compatible
.NET runtime; the runtime's platform package owns its own operating-system
prerequisites. The `dotnetjq` CLI tool's RID packages and standalone archives
are NativeAOT/self-contained, subject to the operating-system dependencies
listed above.

## NativeAOT publication status

NativeAOT folds the LGPL-derived GlibcCompat component into the executable.
Release candidates therefore carry expanded corresponding source and must pass
the repository's AOT relinking proof before publication readiness can be
asserted. The generators and validators in this directory remain inert inputs;
the protected tag workflow performs attestation and publication only after all
platform and installed-package gates succeed.
