# DotNetJq release tooling

Most scripts in this directory assemble or verify candidates without external
mutation. The narrowly scoped `assert-release-tag.sh` checks the configured Git
remote, and `publish-nuget-plan.sh` publishes an already verified ten-package
plan. The tag-only GitHub Actions workflow is the sole orchestrator that calls
publication operations. Repository ownership and publisher identity remain
explicit inputs.

The supported matrix is:

```text
win-x64             win-arm64
linux-x64           linux-arm64
linux-musl-x64      linux-musl-arm64
osx-x64             osx-arm64
```

## Standalone NativeAOT archives

Run a target on a matching operating-system runner:

```sh
tools/release/build-native-archive.sh \
  --rid linux-x64 \
  --version 1.0.0 \
  --output-directory artifacts/release \
  --execute-validation
```

The build forces `PublishAot=true` and self-contained publication, rejects
managed runtime payload files, stages the exact repository license inventory
from `packaging/release-license-paths.txt`, and copies `LICENSE.TXT` and
`THIRD-PARTY-NOTICES.TXT` byte-for-byte from the exact NativeAOT runtime pack
resolved by the exact SDK for that RID. The project pins runtime pack version
`10.0.11`; `global.json` pins SDK `10.0.400` with roll-forward disabled. It
creates a versioned ZIP or tarball and
immediately validates it. An existing artifact is accepted only when it is
byte-identical; a different file is never overwritten. Unix archives require
GNU tar (`tar` or `gtar`) plus `gzip -n` so sorting, ownership, tar timestamps,
and the gzip header do not silently vary with the host implementation. Windows
ZIPs use the checked-in .NET 10 deterministic ZIP writer: entries are ordinally
sorted, stored without zlib compression, and carry fixed timestamps,
Unix-origin metadata, and file attributes on every build host. There is no
host-dependent `Compress-Archive` fallback. Verification uses Python 3's
independent ZIP/tar readers to require the exact member order, fixed timestamps,
numeric ownership, file modes, compression representation, and empty metadata
fields produced by those builders.

`verify-native-archive.sh` rejects path traversal, duplicate members, symbolic
links, unexpected files or even empty directories, missing or changed license
files, missing executables, and managed DLL or runtime-config payloads.
`--execute` additionally checks the extracted command.
The binaries do not require a .NET runtime, but they retain normal operating-
system dependencies. In particular, Alpine/musl installations must provide
`icu-libs`; the musl CI jobs install it before executing their smoke suites.

## .NET 10 RID-specific tool packages

The CLI's NuGet package ID and installed command are both `dotnetjq`. The
managed API package is separately identified as `DotNetJq.Library`; its
assembly and namespace remain `DotNetJq`.

The CLI project declares the eight `ToolPackageRuntimeIdentifiers` plus
`PublishAot=true`. This opts only tool packaging into the RID matrix; the
general project `RuntimeIdentifiers` property is intentionally not duplicated.
Build the pointer once:

```sh
dotnet pack src/DotNetJq.Cli/DotNetJq.Cli.csproj \
  -c Release -p:Version=1.0.0 -p:PackageVersion=1.0.0 \
  -p:Authors='PUBLISHER DISPLAY NAME' \
  -p:RepositoryUrl='https://github.com/OWNER/REPOSITORY' \
  -o artifacts/release
```

Build each RID on a matching OS:

```sh
dotnet pack src/DotNetJq.Cli/DotNetJq.Cli.csproj \
  --disable-build-servers --maxcpucount:1 -c Release -r linux-x64 \
  -p:ContinuousIntegrationBuild=true -p:DebugSymbols=false -p:DebugType=none \
  -p:NuGetAudit=false -p:PublishAot=true -p:SelfContained=true \
  -p:UseSharedCompilation=false \
  -p:Authors='PUBLISHER DISPLAY NAME' \
  -p:RepositoryUrl='https://github.com/OWNER/REPOSITORY' \
  -p:Version=1.0.0 -p:PackageVersion=1.0.0 \
  -o artifacts/release
```

Then validate the pointer metadata, every RID mapping/package, native entry
points, absence of an untested `any` fallback, exact license payloads, and the
complete byte-matched `aot-source/` corresponding-source tree. A complete set
also includes the separately packed managed library:

```sh
dotnet pack src/DotNetJq/DotNetJq.csproj \
  -c Release -p:Version=1.0.0 -p:PackageVersion=1.0.0 \
  -p:Authors='PUBLISHER DISPLAY NAME' \
  -p:RepositoryUrl='https://github.com/OWNER/REPOSITORY' \
  -o artifacts/release

tools/release/verify-nuget-package-set.sh \
  --directory artifacts/release \
  --package-id dotnetjq \
  --library-package-id DotNetJq.Library \
  --version 1.0.0 \
  --authors 'PUBLISHER DISPLAY NAME' \
  --repository-url 'https://github.com/OWNER/REPOSITORY' \
  --native-archives artifacts/release
```

The complete release feed contains exactly ten nupkgs: `DotNetJq.Library`,
eight RID packages, and the `dotnetjq` selector. `create-nuget-publish-plan.sh`
writes a data-only TSV with the library first, all RID packages next, and the
pointer package last. Each row binds the filename to its SHA-256. It
deliberately emits no `dotnet nuget push` command.

Every pack requires explicit `Authors` and `RepositoryUrl` MSBuild properties;
the projects do not retain an assembly-name placeholder identity. Package-set
verification takes those same values as `--authors` and `--repository-url`,
requires `RepositoryType=git`, and compares the complete generated repository
element (including its source commit, when present) across the library,
selector, and all eight RID packages. The publication-plan command requires
the same two verifier arguments.

When `--native-archives` is supplied, verification also requires each RID
package's executable to be byte-identical to the already verified standalone
archive for that RID. This binds the package payload to the executable that ran
the strict cross-platform process suite instead of trusting a second build.

For a complete local package smoke test on the current host, run:

```sh
tools/release/verify-local-tool-package.sh \
  --version 1.0.0 \
  --authors 'PUBLISHER DISPLAY NAME' \
  --repository-url 'https://github.com/OWNER/REPOSITORY'
```

This detects the host RID, creates the pointer plus matching NativeAOT RID
package in a guarded temporary directory, validates their native/source/license
payloads, installs through a local-only NuGet configuration and isolated cache,
then runs the Unix shell contract or both the PowerShell 7 and Windows
PowerShell 5.1 contracts. A Windows package gate fails when either shell is
absent. Windows RID detection uses `PROCESSOR_ARCHITEW6432` first and
`PROCESSOR_ARCHITECTURE` second, because an x64-emulated Git Bash process on an
ARM64 machine can report `x86_64` from `uname -m`; unsupported or missing native
Windows architecture values fail closed.

An already assembled release feed can be tested without rebuilding it:

```sh
tools/release/verify-local-tool-package.sh \
  --version 1.0.0 \
  --authors 'PUBLISHER DISPLAY NAME' \
  --repository-url 'https://github.com/OWNER/REPOSITORY' \
  --rid win-arm64 \
  --package-directory artifacts/release \
  --native-archives artifacts/release
```

The explicit Windows RID supplies the matching `dotnet tool install --arch`
value, preventing an x64-emulated SDK process from selecting `win-x64` on the
ARM64 runner. Unix hosts retain the SDK's native RID selection because its
explicit-architecture validation in the pinned SDK does not accept all
supported Linux RIDs. The gate byte-compares the pointer and selected RID nupkgs in the
installed tool's private store with the local feed, rejects resolution of any
other `dotnetjq.*` RID package, validates the installed command shim's native
architecture, and (when `--native-archives` is present) byte-binds the selected
package payload to its verified standalone archive. Release CI performs this
exact installed-package test on native `win-x64` and `win-arm64` runners after
downloading the final complete bundle artifact.

The release workflow uses the same isolation rule for the complete feed: its
temporary `NuGet.Config` contains `<clear />` and only the local bundle source.
It does not use `--ignore-failed-sources`, so an undeclared remote-source
fallback cannot make a release pass.

## Checksums and package-manager metadata

```sh
tools/release/generate-checksums.sh --directory artifacts/release
tools/release/verify-checksums.sh --directory artifacts/release

tools/release/generate-winget-manifests.sh \
  --version 1.0.0 \
  --repository OWNER/REPOSITORY \
  --publisher 'PUBLISHER DISPLAY NAME' \
  --package-id Publisher.DotNetJq \
  --artifacts artifacts/release \
  --output-directory artifacts/winget

tools/release/generate-homebrew-formula.sh \
  --version 1.0.0 \
  --repository OWNER/REPOSITORY \
  --artifacts artifacts/release \
  --output artifacts/homebrew/dotnetjq.rb
```

WinGet receives x64/arm64 portable ZIP entries and only the `dotnetjq` alias.
Homebrew selects macOS/Linux and arm64/x64 archives and tests a real filter.
Linux Homebrew intentionally uses glibc archives, not musl archives. The
formula installs the complete license/notice payload and stages the matching
corresponding-source archive under its package share directory. Because
`icu4c@78` is keg-only, the Linux formula keeps the binary under `libexec` and
uses a command-specific environment launcher to expose that keg; macOS installs
the binary directly and uses the operating-system ICU implementation. The
Linux archive's explicit baseline is Ubuntu 24.04 / glibc 2.39. This prebuilt,
platform-specific formula is intended for a project-owned custom tap, not
submission to `homebrew/core`.

`verify-package-manager-metadata.sh` requires exactly `dotnetjq.rb` and the
three expected multi-file WinGet manifests. It regenerates and byte-compares
all four files against the verified archives, rejects unresolved placeholders,
checks the formula's Ruby syntax and required ICU/source/test contracts, and
parses the WinGet YAML with duplicate-key, schema-header, structure, URL, and
archive-hash checks. An actual custom-tap `brew install`/`brew test` remains a
downstream check. For stable tags, the release workflow opens a pull request in
the configured tap and submits the verified WinGet manifests after the GitHub
release is public.

## Source and release validation

The distributable `dotnetjq-aot-source-VERSION.tar.gz` is built by
`tools/aot-compliance/create-source-archive.sh`. The release verifier requires
its external checksum, internal full-file checksum inventory, byte equality
with every declared source input, and the exact generated grammar, resource,
Gamma, rebuild-tool, and relinking-document inputs. `verify-release-bundle.sh`
also requires all eight standalone archives, exactly ten NuGet packages, the
hash-bound ten-row publication plan, the exact four-file package-manager
metadata set, a complete `SHA256SUMS` inventory, and a recorded relinking proof.
Its top-level allowlist contains exactly those 27 regular files; extra files,
directories, and symbolic links fail the bundle gate.

## Pinned cross-platform compatibility inputs

`provision-jq-1.8.2-test-assets.sh` checks out jq commit
`34f7186b86743a083a589741b6cea95293524108` with its pinned submodules and
downloads the official jq 1.8.2 executable selected by RID. Every supported RID
and exact release-asset SHA-256 is recorded in `jq-1.8.2-oracles.tsv`; the two
musl RIDs intentionally use jq's static Linux release executables. The helper
refuses an incomplete RID inventory, a source-commit mismatch, a modified
checkout, an oracle hash mismatch, or a version mismatch.

CI sets `DOTNETJQ_REQUIRE_FULL_COMPATIBILITY=1`, `DOTNETJQ_JQ182`,
`DOTNETJQ_UPSTREAM`, and an explicit `DOTNETJQ_CLI`. The complete CLI process
suite is run against the managed command and the executable extracted from
every one of the eight release-format archives, including both Alpine RIDs.
Each run emits TRX and `verify-trx-no-skips.py` requires a non-empty,
all-passed result set with zero dynamic or recorded skips. Workflow actions are
referenced by immutable commit SHA, and Alpine SDK/runtime images are referenced
by their multi-architecture manifest digest.

## Publication readiness

NativeAOT incorporates the LGPL GlibcCompat implementation into the executable.
`assert-publication-ready.sh` therefore validates the real AOT rebuild/relink
proof produced by `tools/aot-compliance/verify-relink.sh`, binds it to the
SHA-256 of the distributed corresponding-source archive and the exact original
component within it, and requires the proof's unmodified binary SHA-256 to
equal the executable inside the shipped proof-RID archive. Missing, stale,
unrelated, or malformed evidence fails closed. The check is an engineering
gate, not legal advice, and it contains no upload or publication command.

The tag-triggered workflow accepts only stable `vMAJOR.MINOR.PATCH` and
`vMAJOR.MINOR.PATCH-rc.N` tags whose base version matches
`Directory.Build.props` and whose commit is on `origin/main`. After all eight
native builds and both Windows installed-package gates, it redownloads and
reverifies the exact bundle, creates checksum-based build-provenance
attestations, stages all assets in a draft GitHub release, publishes the ten
NuGet packages using a short-lived OIDC key, and then publishes the GitHub
release. Stable releases additionally open a Homebrew tap pull request and
submit WinGet manifests; release candidates stop after NuGet and the GitHub
prerelease. Publication jobs use the protected `release` environment.

Reference: [.NET 10 RID-specific and AOT tools](https://learn.microsoft.com/en-us/dotnet/core/tools/rid-specific-tools).
