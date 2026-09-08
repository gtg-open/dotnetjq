# NativeAOT corresponding source and relinking

This document defines the release-engineering procedure for DotNetJq
NativeAOT CLI artifacts. It is not legal advice.

> **Evidence scope.** Only `linux-x64` has been built, executed, and subjected
> to the modified-component rebuild/relink proof on the current local host.
> The other seven RIDs below are requirements implemented by the CI release
> workflow; they are not presented as locally observed results.

## Why the NativeAOT package has a separate source deliverable

In a framework-dependent managed installation,
`DotNetJq.GlibcCompat.dll` remains a separately replaceable assembly. NativeAOT
compiles that LGPL-derived component and the rest of the managed dependency
graph into one platform executable. Therefore every NativeAOT release is
accompanied by a version-matched corresponding-source archive and reproducible
instructions that let a recipient modify the component and rebuild the CLI.

The source archive contains:

- the complete `DotNetJq.GlibcCompat`, `DotNetJq`, and `DotNetJq.Cli` source
  trees, including checked-in generated parser, lexer, Unicode, and built-in
  resource inputs;
- project files, .NET 10 SDK selection, and shared build properties;
- all applicable license and third-party notice files;
- the porting manifest, this procedure, and the archive/rebuild/proof tools.

The source bundle does not freeze a guessed copy of a platform runtime license.
During each NativeAOT publish, the CLI project resolves the target RID's exact
`Microsoft.NETCore.App.Runtime.NativeAOT.<rid>` pack and copies that pack's
`LICENSE.TXT` and `THIRD-PARTY-NOTICES.TXT` beside the rebuilt executable. The
release archive and RID-package verifiers independently resolve the same pack
and compare both files byte-for-byte.

It deliberately contains no upstream jq checkout and needs none to build. The
checked-in generated sources and embedded jq library resources are build
inputs in their own right. NuGet/.NET SDK packages and the platform native
toolchain remain normal build-system prerequisites.

## Deterministic archive and checksums

Creation requires Bash, gzip, either `sha256sum` or `shasum`, and GNU tar. The
script accepts GNU tar as `tar` or `gtar` (the normal Homebrew name on macOS).
From a clean release source tree, run:

```bash
tools/aot-compliance/create-source-archive.sh \
  --version VERSION \
  --output-dir artifacts/source
```

GNU tar metadata is normalized to epoch zero, numeric owner/group zero, fixed
file modes, depth-first ordinal path order, and a gzip stream without a
timestamp or input filename. Running the command against identical source
content and the same version therefore produces a byte-identical archive.

The release verifier also parses the archive through Python 3's independent
`tarfile` reader and rejects a wrong traversal order, timestamp, owner/group,
mode, gzip header, PAX field, member type, or unexpected empty directory. This
makes the normalized representation a checked release invariant rather than
only a builder convention.

Two checksum layers are produced:

1. `dotnetjq-aot-source-VERSION.tar.gz.sha256` authenticates the distributed
   archive.
2. `SOURCE_SHA256SUMS` inside the archive inventories every bundled file other
   than the inventory itself.

On GNU/Linux, validate them with:

```bash
sha256sum --check dotnetjq-aot-source-VERSION.tar.gz.sha256
tar -xzf dotnetjq-aot-source-VERSION.tar.gz
cd dotnetjq-aot-source-VERSION
sha256sum --check SOURCE_SHA256SUMS
```

On macOS, the corresponding checksum commands are
`shasum -a 256 --check`; archive extraction may use the platform `tar` after
the deterministic archive has been created by GNU `gtar`. Windows recreation
requires a Unix-like Bash environment providing the same tools.

## Rebuild from a NativeAOT NuGet package

The .NET 10 pointer package and every RID-specific NativeAOT package contain a
complete expanded copy of the corresponding source below `aot-source/`. A
downloaded `.nupkg` is a ZIP archive. After validating the package checksum,
extract it into a new empty directory and rebuild from that subtree:

```bash
mkdir dotnetjq-nupkg
unzip dotnetjq.linux-x64.VERSION.nupkg -d dotnetjq-nupkg
cd dotnetjq-nupkg/aot-source
bash tools/aot-compliance/rebuild-aot.sh \
  --version VERSION \
  "$PWD" \
  linux-x64 \
  "$PWD/../../rebuilt-linux-x64"
```

Replace the package name and RID with any of the eight entries below, and use
its matching build host. Invoking the wrapper through `bash` is intentional:
ZIP/NuGet packages do not preserve Unix executable mode bits.

The expanded NuGet tree is a convenience copy, validated byte-for-byte against
the release source checkout. The deterministic source archive and its internal
`SOURCE_SHA256SUMS` remain the canonical content inventory. A recipient can
recreate and validate that canonical representation directly from the expanded
NuGet source:

```bash
bash tools/aot-compliance/create-source-archive.sh \
  --source-root "$PWD" \
  --version VERSION \
  --output-dir "$PWD/../../corresponding-source"
```

Neither the direct rebuild nor archive recreation reads an upstream jq
checkout.

## Exact .NET 10 NativeAOT rebuild matrix

NativeAOT links platform-native code. Run each command on the named operating
system and architecture (or an equivalent correctly configured native/cross
linking environment). Install the .NET 10 SDK selected by `global.json` first.
All commands start in the extracted archive root and produce a self-contained
executable; no .NET runtime installation is required to run that executable.
The publish directory also contains the two exact .NET runtime license/notice
files described above; keep them with any redistributed executable.
Self-contained publication does not statically incorporate every operating-
system library. The executable retains the managed non-invariant globalization
configuration. Linux needs system ICU (Alpine/musl deployments install
`icu-libs`); macOS uses the operating-system ICU implementation; Windows uses
OS ICU when available and .NET's Windows NLS fallback otherwise.

### Windows x64 (`win-x64`)

Use a Windows x64 host with the Visual Studio 2022 C++ x64 build tools and a
Windows SDK:

```powershell
dotnet publish src/DotNetJq.Cli/DotNetJq.Cli.csproj --configuration Release --runtime win-x64 --self-contained true -p:PublishAot=true -p:ContinuousIntegrationBuild=true -p:NuGetAudit=false -p:Version=VERSION -p:PackageVersion=VERSION --output artifacts/win-x64
```

### Windows ARM64 (`win-arm64`)

Use a Windows ARM64 host, or a supported Windows cross-build host, with the
Visual Studio 2022 C++ ARM64 build tools and a Windows SDK:

```powershell
dotnet publish src/DotNetJq.Cli/DotNetJq.Cli.csproj --configuration Release --runtime win-arm64 --self-contained true -p:PublishAot=true -p:ContinuousIntegrationBuild=true -p:NuGetAudit=false -p:Version=VERSION -p:PackageVersion=VERSION --output artifacts/win-arm64
```

### GNU/Linux x64 (`linux-x64`)

Use a glibc-based Linux x64 host with `clang` or `gcc`, the system linker, and
the distribution's zlib development package:

```bash
env -u LD_PRELOAD CC=gcc dotnet publish src/DotNetJq.Cli/DotNetJq.Cli.csproj --configuration Release --runtime linux-x64 --self-contained true -p:PublishAot=true -p:ContinuousIntegrationBuild=true -p:NuGetAudit=false -p:Version=VERSION -p:PackageVersion=VERSION --output artifacts/linux-x64
```

### GNU/Linux ARM64 (`linux-arm64`)

Use a glibc-based Linux ARM64 host with `clang` or `gcc`, the system linker,
and the distribution's zlib development package:

```bash
env -u LD_PRELOAD CC=gcc dotnet publish src/DotNetJq.Cli/DotNetJq.Cli.csproj --configuration Release --runtime linux-arm64 --self-contained true -p:PublishAot=true -p:ContinuousIntegrationBuild=true -p:NuGetAudit=false -p:Version=VERSION -p:PackageVersion=VERSION --output artifacts/linux-arm64
```

### musl Linux x64 (`linux-musl-x64`)

Use an Alpine/musl x64 .NET 10 SDK environment with `clang` or `gcc`,
`build-base`, and `zlib-dev`:

```bash
env -u LD_PRELOAD CC=gcc dotnet publish src/DotNetJq.Cli/DotNetJq.Cli.csproj --configuration Release --runtime linux-musl-x64 --self-contained true -p:PublishAot=true -p:ContinuousIntegrationBuild=true -p:NuGetAudit=false -p:Version=VERSION -p:PackageVersion=VERSION --output artifacts/linux-musl-x64
```

### musl Linux ARM64 (`linux-musl-arm64`)

Use an Alpine/musl ARM64 .NET 10 SDK environment with `clang` or `gcc`,
`build-base`, and `zlib-dev`:

```bash
env -u LD_PRELOAD CC=gcc dotnet publish src/DotNetJq.Cli/DotNetJq.Cli.csproj --configuration Release --runtime linux-musl-arm64 --self-contained true -p:PublishAot=true -p:ContinuousIntegrationBuild=true -p:NuGetAudit=false -p:Version=VERSION -p:PackageVersion=VERSION --output artifacts/linux-musl-arm64
```

### macOS x64 (`osx-x64`)

Use an Intel macOS host with the Xcode command-line tools:

```bash
dotnet publish src/DotNetJq.Cli/DotNetJq.Cli.csproj --configuration Release --runtime osx-x64 --self-contained true -p:PublishAot=true -p:ContinuousIntegrationBuild=true -p:NuGetAudit=false -p:Version=VERSION -p:PackageVersion=VERSION --output artifacts/osx-x64
```

### macOS ARM64 (`osx-arm64`)

Use an Apple Silicon macOS host with the Xcode command-line tools:

```bash
dotnet publish src/DotNetJq.Cli/DotNetJq.Cli.csproj --configuration Release --runtime osx-arm64 --self-contained true -p:PublishAot=true -p:ContinuousIntegrationBuild=true -p:NuGetAudit=false -p:Version=VERSION -p:PackageVersion=VERSION --output artifacts/osx-arm64
```

The archive also provides a checked wrapper for the same operation:

```bash
tools/aot-compliance/rebuild-aot.sh --version VERSION "$PWD" RID "artifacts/RID"
```

The wrapper accepts exactly the eight RIDs above, rejects an archive containing
`upstream/jq`, requires a matching host operating system, refuses a non-empty
output directory, removes a stale inherited `LD_PRELOAD`, and reports the
actual executable path even if its assembly name changes.

## Modify and relink the LGPL-derived component

After validating `SOURCE_SHA256SUMS`, edit any implementation under
`src/DotNetJq.GlibcCompat`, then publish again into a new empty output
directory. For example, changing the body of
`GlibcCompatMath.Gamma(double)` changes the jq-visible `gamma` filter. Compare:

```bash
artifacts/original/DotNetJq.Cli -n '5 | gamma'
artifacts/modified/DotNetJq.Cli -n '5 | gamma'
```

The output filename can become `dotnetjq` without changing the rebuild
contract. Use the `AOT_EXECUTABLE=...` line emitted by `rebuild-aot.sh` instead
of assuming a fixed filename.

On a GNU/Linux x64 host, the repository's automated proof performs this entire
operation for `linux-x64` in guarded temporary directories:

```bash
tools/aot-compliance/verify-relink.sh --version VERSION --rid linux-x64
```

That is the only RID with locally observed build, execution, and
modified-component rebuild/relink evidence. For the other seven RIDs, building,
structural verification, archive-to-NuGet byte binding, and process testing on
matching runners are CI release-workflow requirements, not observations made on
this local host. The workflow does not claim a modified-component relink proof
for those seven RIDs.

The matching NativeAOT packages must already be in the user's NuGet cache;
running the applicable publish command once populates them. The sandbox reads
that cache, disables network access, and confines source and build-output
writes to its guarded temporary directory plus a private in-memory `/tmp` used
for NativeAOT linker intermediates.

Bubblewrap mounts the development source tree (including the external
checkout exposed as `upstream/jq`) as an empty filesystem and unshares the
network while it builds both copies from the extracted archive. The proof
asserts all of the following:

- two independent archive creations are byte-identical;
- the external archive hash and internal file inventory validate;
- the archive contains no upstream jq checkout;
- unmodified corresponding source produces a working NativeAOT CLI;
- both rebuilt executables report the requested release version exactly;
- changing only the GlibcCompat `Gamma` source changes the jq-visible result
  to the controlled sentinel `424242.5`;
- the original and modified native executable hashes differ.

## Release gate

For every NativeAOT release:

1. Build each of the eight binaries from the exact source revision being
   packaged.
2. Create the corresponding-source archive twice and require byte identity.
3. Validate the external checksum and internal `SOURCE_SHA256SUMS` inventory.
4. Run the sandboxed relinking proof for the release's `linux-x64` artifact and
   retain its hashes and output in release evidence. This is the representative
   proof that the distributed source permits a controlled LGPL-component
   modification and relink. Publication verification requires the proof's
   source and original-component hashes to match the distributed source archive,
   and its original-binary hash to match the executable shipped in the
   `linux-x64` archive. It is not described as execution coverage for the other
   architectures. Every RID is separately built, structurally verified,
   byte-bound between archive and NuGet package, and process-tested on its
   matching runner. A publisher may repeat the relink proof on additional Linux
   toolchains for extra assurance.
5. Publish the source archive, its `.sha256`, license notices, and rebuild
   instructions beside the version-matched binary assets.
6. Obtain project-specific legal review before treating this engineering
   procedure as a compliance conclusion.
