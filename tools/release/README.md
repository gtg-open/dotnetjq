# DotNetJq release tooling

Most scripts in this directory assemble or verify candidates without external
mutation. The narrowly scoped `assert-release-tag.sh` checks the configured Git
remote, and `publish-nuget-plan.sh` publishes an already verified ten-package
plan. The tag-only GitHub Actions workflow is the sole orchestrator that calls
publication operations. Repository ownership and publisher identity remain
explicit inputs.

`generate-release-notes.sh` creates the exact, deterministic GitHub release body
from the validated version, tag, and repository. The release probe and the
pre/post-publication verifier compare that body as well as the title, release
state, prerelease flag, and all 27 asset payloads. GitHub-generated notes are not
used because their output can change independently of the tagged source.

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
resolved by the exact SDK for that RID. The build uses a fresh, private
`--artifacts-path`, so later verification restores cannot rewrite the
`project.assets.json` and NativeAOT package identity captured for performance
provenance. The project pins runtime pack version
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
All release helpers override and export `SOURCE_DATE_EPOCH=315532800`, the
ZIP-safe 1980-01-01 epoch. `Directory.Build.props` supplies the same value to
NuGet's pack targets for direct `dotnet pack` calls, including pack commands
that execute inside platform build containers. This prevents the wall clock
from changing the local and central ZIP timestamps, package hashes, publication
plan, and release checksum inventory on a retry of the same source build.
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
also records the exact package ID, normalized version, and a SHA-256 over the
ordered ZIP entry names and decompressed payload bytes. The payload hash omits
only NuGet's `.signature.p7s` entry. Plan creation deliberately emits no
`dotnet nuget push` command.

`publish-nuget-plan.sh` preflights all ten exact ID/version pairs through the
official NuGet.org flat-container API before the first push. An absent package
is eligible for publication; a present package is downloaded, checked with
`dotnet nuget verify --all` for a NuGet.org repository signature whose exact
owners list contains the explicitly expected package owner, validated for its
nuspec ID/version, and compared entry-for-entry with the local decompressed
payload. Mismatched or unproven state stops publication. A failed or timed-out
push is probed before any retry, and a retry occurs only while the exact version
is still proven absent. This makes reruns resumable without `--skip-duplicate`.
The pointer remains last, and a final pass remotely reverifies all ten payloads.
Run `python3 tools/release/test-nuget-publication.py` for the local fake-server
regressions covering mismatch rejection, ambiguous responses, bounded retry,
and an all-present zero-push rerun.

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

Every package-set check also runs `verify-nupkg-archive.py`. The independent ZIP
reader verifies CRCs, safe canonical entry names, unique entry names, and the
canonical epoch in both each local header and its central-directory record. It
deliberately does not require one compression method, creator platform, external
file attributes, or extra-field representation: those are valid
platform-specific ZIP metadata and are not needed for the timestamp invariant.
`test-nuget-reproducibility.sh` fault-tests that boundary, then packs the
library, selector, and current-host RID package twice and requires byte identity.
The two passes use independent artifact/intermediate trees, NuGet caches, and
.NET CLI homes, force independent restores, and canonicalize compiler path maps;
they therefore prove cold-build reproducibility rather than merely rewriting a
shared incremental output. Release CI runs this check on every native RID
runner, including inside each matching Alpine/musl build container.
The same gate runs in the eight-platform PR matrix before a release tag is
created. CI passes `--rid` explicitly so an emulated Windows shell cannot silently
select an x64 package in the ARM64 job. On a byte mismatch, the gate still fails;
it retains both packages and a per-entry hash/metadata comparison under
`artifacts/reproducibility/<rid>/`, uploaded by CI for diagnosis. The comparison
does not normalize packages or relax the byte-equality requirement.

### Cross-platform cold-build failure diagnosed before 1.0.0

A diagnostic run before version 1.0.0 isolated three build-representation
problems:

- Library DLLs on Windows/macOS embedded each temporary intermediate directory
  in their CodeView PDB path. The compiler path-map helper now normalizes native
  path spelling (including macOS's doubled separator and Windows's Git Bash and
  long-name aliases) before escaping the MSBuild property.
- The Windows NativeAOT executable differed at three timestamp fields. The CLI
  project requests `/Brepro` from the native linker as well as deterministic C#
  compilation.
- The macOS executable differed only in `LC_UUID` and the signature covering it;
  the RID package also accidentally included the SDK-generated `.dSYM` directory.
  The CLI requests reproducible native linking and omits the link-time debug map
  when `DebugType=none`, then excludes the `.dSYM` sidecar from the tool package.

These are packaging settings, not runtime compatibility changes. Native linker
options live in the CLI project so standalone publishing, RID packing, and
corresponding-source rebuilding receive the same options. The macOS fix retains
the content-derived UUID and normal signing; it does not use `-no_uuid` or patch
an executable after linking. See Apple's [build UUID guidance](https://developer.apple.com/documentation/technotes/tn3178-checking-for-and-resolving-build-uuid-problems).
The full cold-build comparisons and unchanged executable/process tests are the
acceptance gate for these settings; the diagnostic failure alone is not a claim
that the fix has passed.

Release verification jobs provision their own dependencies; a tool installed by
another job is not available in a fresh runner. Package isolation explicitly
installs `ripgrep` and `binutils` as well as its sandbox/archive tools. The later
bundle-verification jobs provision the pinned .NET SDK (runtime-license checks
invoke MSBuild), Python, and their `file`, Ruby, and ZIP readers. Benchmark
self-tests receive `DOTNETJQ_PERFORMANCE_UPSTREAM` bound to the same provisioned
checkout passed to the measured fixtures through `--upstream`; the argument to
the benchmark itself does not configure the separate self-test process.

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

The verifier first checks that `dotnet --info` reports the exact requested RID;
an x64-emulated SDK on an ARM64 runner fails this prerequisite explicitly.
Installation then uses the native SDK's default architecture without `--arch`
and explicitly selects `--framework net10.0`.
In the pinned SDK 10.0.400, an explicit architecture makes
[`ShellShimTemplateFinder.ResolveAppHostSourceDirectoryAsync`](https://github.com/dotnet/sdk/blob/v10.0.400/src/Cli/dotnet/ShellShim/ShellShimTemplateFinder.cs)
download `microsoft.netcore.app.host.<rid>` even when the requested architecture
already matches. Omitting it uses the SDK's bundled apphost template and keeps
the feed strictly local; it also avoids that method's limited explicit-RID
allowlist on Linux. The explicit framework also avoids another branch of the
same SDK method: native packages advertise `tools/any` (framework version zero),
which otherwise triggers the legacy pre-.NET 5/6 x64 apphost download on ARM64
Windows/macOS. The native package's `any` target is compatible with the explicit
.NET 10 selection; the selected executable is still checked against the native
matrix RID. No extra NuGet source or apphost package is added to the
release feed. The gate byte-compares the pointer and selected RID nupkgs in the
installed tool's private store with the local feed, rejects resolution of any
other `dotnetjq.*` RID package, validates the installed command shim's native
architecture, and (when `--native-archives` is present) byte-binds the selected
package payload to its verified standalone archive. Release CI performs this
exact installed-package test for all eight supported RIDs after downloading the
final complete bundle artifact; the two musl packages run inside the pinned
Alpine environment. That environment explicitly installs full Info-ZIP `unzip`
because BusyBox's reduced command does not support the `-Z1` inventory operation.
Pointer inventory filtering uses portable awk field comparisons and is covered
by fixtures on the native runners, including macOS's BSD awk. These are
verification/setup adaptations, not changes to the library or CLI behavior.
On Windows the SDK's
[`ShellShimRepository`](https://github.com/dotnet/sdk/blob/v10.0.400/src/Cli/dotnet/ShellShim/ShellShimRepository.cs)
creates a `dotnetjq.cmd` launcher for a native tool. The verifier requires its
exact UTF-8/CRLF batch contents and target path, byte-compares that installed
executable with its verified RID package, and validates the executable's PE
architecture. All PowerShell and byte-stream assertions run through the actual
`.cmd` launcher (using `cmd.exe` for direct process-stream probes), not by
bypassing it and invoking the packaged executable. Fourteen cross-platform
fault fixtures reject missing/changed launchers and missing/changed/duplicate
payloads for both Windows RIDs. Standalone archives still contain `dotnetjq.exe`.

The cold-build test also installs, exercises, and uninstalls its verified
pointer/RID pair on every PR matrix target before removing the temporary feed.
The later release installation still checks the complete final bundle and
byte-binds its executable to the corresponding release archive.

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
The generated YAML is stored in the exact CRLF, header, and indentation form
that pinned WingetCreate 1.12.13.0 (YamlDotNet 16.3.0) commits after it
deserializes and reserializes the inputs. This makes open-PR and already-merged
rerun checks byte-exact rather than comparing against pre-serialization input.
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
release is public. Reruns are fail-closed and downgrade-safe: the Homebrew
classifier skips an identical or newer valid formula, while the WinGet probe
skips an exact version already merged into the canonical manifest branch or an
exact open pull request. Same-version differences, malformed metadata, extras,
and ambiguous remote state stop publication.

The WinGet validator checks the **submitted** byte format from pinned
WingetCreate 1.12.13.0: the producer comment comes first, the exact per-manifest
schema comment second, then a blank line; line endings are CRLF. The checked-in
templates themselves remain LF-only. A pre-release pipeline run exposed an old
validator assumption that the template's schema comment would still be the
first submitted line. `test-winget-manifest-canonicalization.py` now feeds the
actual canonicalizer output into the complete Ruby YAML/hash validator and
rejects altered producer/schema headers, line endings, duplicate keys,
identities, URLs, archive hashes, and installer structure. This test runs in the
full semantic gate on GitHub, with Ruby explicitly installed; no jq library or
CLI execution behavior is changed.

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
native builds and the complete eight-RID installed-package matrix, it redownloads and
reverifies the exact bundle, creates checksum-based build-provenance
attestations, stages all assets in a draft GitHub release, publishes the ten
NuGet packages using a short-lived OIDC key, and then publishes the GitHub
release. Stable releases additionally open a Homebrew tap pull request and
submit WinGet manifests; release candidates stop after NuGet and the GitHub
prerelease. The irreversible core and downstream package-manager publication
jobs use the protected `release` environment; engineering verification,
attestation, and private draft staging do not consume that approval boundary.
The performance gate measures the exact `linux-x64` archive produced by the
native release job, not a separately rebuilt NativeAOT executable. A producer
proof binds its pre-build source, selected SDK identity files/native tools,
resolved NativeAOT package
payloads, archive, and executable member; the performance consumer downloads
that exact named workflow artifact and revalidates the chain before timing.
The reviewed DotNetJq baseline is rebuilt and attested on this same benchmark
runner. Both baseline deployments are interleaved with the candidate and native
jq across all 879 fixtures and 21 workloads; the six regression metrics compare
candidate to baseline. Historical workstation timings are not release limits.
PR CI exercises the same paired harness and gate before changes reach `main`.

Reference: [.NET 10 RID-specific and AOT tools](https://learn.microsoft.com/en-us/dotnet/core/tools/rid-specific-tools).
