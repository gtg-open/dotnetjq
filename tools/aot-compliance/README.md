# NativeAOT corresponding-source tools

These tools create and test the source package shipped beside a DotNetJq
NativeAOT CLI release. They are release-engineering controls for the
LGPL-derived `DotNetJq.GlibcCompat` code that becomes part of the monolithic
native executable. They are not legal advice.

Create a deterministic source archive (Bash, gzip, `sha256sum` or `shasum`,
and GNU `tar` or `gtar` are required):

```bash
tools/aot-compliance/create-source-archive.sh \
  --version 1.0.0 \
  --output-dir artifacts/source
```

The command creates both
`dotnetjq-aot-source-1.0.0.tar.gz` and its external `.sha256` file. The archive
also contains `SOURCE_SHA256SUMS`, a sorted SHA-256 inventory of every bundled
file other than the inventory itself.

Rebuild one artifact from an extracted archive on a matching native host:

```bash
tar -xzf dotnetjq-aot-source-1.0.0.tar.gz
dotnetjq-aot-source-1.0.0/tools/aot-compliance/rebuild-aot.sh \
  --version 1.0.0 \
  dotnetjq-aot-source-1.0.0 \
  linux-x64 \
  artifacts/linux-x64
```

Run the strong relinking proof on Linux. It requires Bash, GNU tar/coreutils,
Python 3, bubblewrap, the .NET 10 SDK, and the matching NativeAOT toolchain:

```bash
tools/aot-compliance/verify-relink.sh --version 1.0.0
```

On Ubuntu hosts that enable AppArmor's unprivileged-user-namespace restriction,
an administrator must allow unprivileged user namespaces before running the
proof. The GitHub Actions jobs do this only on their disposable Ubuntu runner,
following bubblewrap's own CI setup, so `--unshare-net` continues to prove that
the rebuild has no network access.

Run one ordinary NativeAOT restore/publish first if the required .NET runtime,
ILCompiler, and linker packages are not already present in the user's NuGet
cache. The sandbox mounts that cache read-only through the root filesystem and
writes build outputs only below its guarded temporary directory.

The proof creates the archive twice and compares it byte-for-byte, validates
both checksum layers, masks the development source tree with bubblewrap,
disables network access, rebuilds the unmodified source, changes only the
GlibcCompat `Gamma` entry point in a second temporary copy, and demonstrates a
different jq-visible result from a separately rebuilt NativeAOT executable.
Temporary cleanup is guarded by an exact `dotnetjq-aot-relink.*` path check.
Use `--keep-temp` when the proof evidence needs manual inspection.

Proof work defaults to
`${XDG_CACHE_HOME:-$HOME/.cache}/dotnetjq-aot-compliance`; override it with an
absolute `DOTNETJQ_AOT_PROOF_TEMP` path. The isolated build receives a private
in-memory `/tmp`, which NativeAOT requires for its linker intermediates.

See `porting/AOT_RELINKING.md` for the complete eight-RID rebuild matrix and
release requirements.
