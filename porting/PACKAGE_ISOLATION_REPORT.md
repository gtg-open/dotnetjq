# Package isolation validation

SDK: .NET SDK 10.0.400
Target framework: `net10.0`

## Result

This document defines a repeatable gate rather than preserving a stale
working-tree PASS. A `DotNetJq.Library` candidate passes when a fresh consumer
restores its exact package outside the repository and pinned jq checkout, then
runs both the packaged gamma component and a replacement rebuilt from the
package's corresponding source under network and filesystem isolation.

The repeatable entry point is:

```sh
tools/verify-isolated-package.sh \
  --authors 'PUBLISHER DISPLAY NAME' \
  --repository-url 'https://github.com/OWNER/REPOSITORY'
```

To prove the exact release artifact rather than repack the project, pass its
normalized version and path:

```sh
tools/verify-isolated-package.sh \
  --package artifacts/nuget/DotNetJq.Library.VERSION.nupkg \
  --version VERSION \
  --authors 'PUBLISHER DISPLAY NAME' \
  --repository-url 'https://github.com/OWNER/REPOSITORY'
```

Each successful run emits fresh hashes for the exact package, its two managed
assemblies, the rebuilt replacement, and the unchanged consumer inventory. The
temporary feed is intentionally deleted, so those candidate-specific values
belong in CI evidence rather than this source-controlled document. Its stable
success markers include:

```text
Build succeeded.
    0 Warning(s)
    0 Error(s)
PACKAGE_ISOLATION_OK
DotNetJq.GlibcCompat -> .../lgpl-source/.../DotNetJq.GlibcCompat.dll
Build succeeded.
    0 Warning(s)
    0 Error(s)
PACKAGE_ISOLATION_OK
isolated package verification passed: .../DotNetJq.Library.VERSION.nupkg
```

## Package and license checks

The script either packs `src/DotNetJq/DotNetJq.csproj` for a local check or
accepts an exact prebuilt package with `--package` and `--version`. It requires:

```text
COPYING
COPYING.LIB
LICENSES.md
README.md
THIRD_PARTY_NOTICES.md
THIRD_PARTY_LICENSES/GPPG-License.md
THIRD_PARTY_LICENSES/Oniguruma-License.txt
lib/net10.0/DotNetJq.dll
lib/net10.0/DotNetJq.GlibcCompat.dll
lib/net10.0/DotNetJq.GlibcCompat.xml
lib/net10.0/DotNetJq.xml
lgpl-source/Directory.Build.props
lgpl-source/global.json
lgpl-source/src/DotNetJq.GlibcCompat/COPYING.LIB
lgpl-source/src/DotNetJq.GlibcCompat/DotNetJq.GlibcCompat.csproj
lgpl-source/src/DotNetJq.GlibcCompat/GlibcCompatMath.cs
lgpl-source/src/DotNetJq.GlibcCompat/GlibcCompatMath.Pow.cs
lgpl-source/src/DotNetJq.GlibcCompat/GlibcCompatMath.X87.cs
lgpl-source/src/DotNetJq.GlibcCompat/NuGet.Offline.Config
lgpl-source/src/DotNetJq.GlibcCompat/README.md
```

The source check is inventory-based rather than limited to that displayed
minimum: the sorted non-`bin`/`obj` repository and archive inventories must be
exactly equal in both directions, and every corresponding file must be
byte-identical. Checked pipelines ensure a failed inventory command cannot be
mistaken for a partial successful result. This automatically covers additional
partial-class source files such as private gamma support kernels.

The package declares `LICENSES.md` as its NuGet license file. That file says
explicitly that no one license applies to the whole package and identifies
`DotNetJq.GlibcCompat.dll` and its source as
`LGPL-2.1-or-later`. `COPYING.LIB` at both package locations must be
byte-identical to GNU C Library 2.39's official file (SHA-256
`dc626520dcd53a22f727af3ee42c770e56c97a64fe3adb063799d8ab032fe551`).
The packaged jq `COPYING` must remain byte-identical to pinned
`COPYING.jq`. The packaged Oniguruma license must remain byte-identical to
`vendor/oniguruma/COPYING` at the pinned jq/Oniguruma revisions; the GPPG
license is likewise pinned by its declared SHA-256.

The `DotNetJq.Library` package intentionally carries both managed assemblies
and declares no external `DotNetJq.GlibcCompat` package dependency. The package
ID is distinct from the `dotnetjq` CLI tool ID; the library assembly and public
namespace remain `DotNetJq`.

Both assemblies declare the SDK `IsAotCompatible` and `IsTrimmable` contracts,
so their AOT, trim, and single-file analyzers run during ordinary builds. The
exact `lib/net10.0` inventory also contains both generated XML documentation
files. The component document records the four-method ABI and explicitly
distinguishes historical `gamma`/`lgamma` logarithmic semantics from `tgamma`.
The package also has no implicit publishing identity: the isolation gate
requires the expected `Authors` and GitHub `RepositoryUrl`, then checks their
exact generated NuGet metadata.

## Rebuild and replacement proof

After validating the original packaged binaries, the script:

1. Extracts only the packaged `lgpl-source` tree.
2. Enters Bubblewrap with networking unshared and the repository hidden.
3. Restores the component using its opt-in `NuGet.Offline.Config`, which clears
   all package sources, then builds it with the packaged SDK/build inputs. This
   source-only reconstruction disables the SDK's build-time AOT/trim analyzers
   because their ILLink pack is not part of the component source; normal builds
   retain and gate those analyzers.
4. Sets a distinctive `AssemblyInformationalVersion` on that rebuild.
5. Replaces only `DotNetJq.GlibcCompat.dll` beside the already-built
   consumer. A recursive SHA-256 inventory proves that every consumer-output
   file except the replaced component—including `DotNetJq.dll`, the consumer,
   `.deps.json`, and `.runtimeconfig.json`—is unchanged.
6. Reruns the isolated consumer and verifies the replacement marker, public
   gamma ABI, exact representative gamma-family results, and jq-facing calls.

This proves the component is a real separately linked, ABI-replaceable managed
assembly rather than gamma implementation code embedded in `DotNetJq.dll`.

## Isolation and dependency checks

Both runtime executions use Bubblewrap with:

- an empty filesystem over the resolved parent of the repository;
- an unusable `/usr/bin/jq`;
- networking unshared;
- inherited environment cleared;
- a deliberately missing `DOTNETJQ_UPSTREAM`;
- writable state limited to the validated temporary tree and private tempfs.

The package is rejected if it contains a named external jq executable, a `.jq`
runtime file, known native/runtime-specific extensions or directories, or any
GPPG/GPLEX generator assembly. The complete release package-set verifier adds
an exact entry inventory, so any extra payload is rejected regardless of its
filename. Runtime
reflection verifies that `DotNetJq.dll` references the separate
`DotNetJq.GlibcCompat` assembly, the main assembly does not define the
component type, neither managed assembly has a P/Invoke method or
`System.Diagnostics.Process` dependency, and the component exposes exactly
the supported `Gamma`, `Lgamma`, `LgammaR`, and `Tgamma` signatures.

## Consumer coverage

The isolated consumer verifies:

- package restore/build from the temporary local feed;
- jq compile/execute over selection, streams, `map`, arithmetic, `add`, and
  `range`;
- exact literal-number serialization beyond binary64;
- exact jq-visible gamma-family results through the separate component;
- explicit and default environment behavior;
- UTF-8 input/output and output-count limits;
- pre-execution cancellation;
- component ABI, separation, managed-only operation, and replacement identity.

No jq source checkout, jq oracle, usable jq process, native library, or network
is needed to build or run the shipped package.
