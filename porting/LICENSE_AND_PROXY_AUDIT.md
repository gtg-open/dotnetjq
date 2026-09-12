# License and proxy audit

This is an engineering compliance contract, not legal advice. It intentionally
avoids a dated working-tree verdict: release status is derived from the exact
candidate by the validators below.

## Verdict

A candidate passes this audit only when the semantic release gate and complete
cross-platform bundle gate both succeed. Those gates require license notices,
package metadata, corresponding source, managed replacement instructions,
NativeAOT relink instructions, and machine-validated traceability for the
managed jq 1.8.2 library and CLI port. No open non-`OMITTED` manifest record is
permitted. Intentionally omitted records describe native build/developer or
unused decNumber surfaces outside the managed deliverables; they are not
counted as semantic-parity claims.

The audited upstream pins are:

- jq `jq-1.8.2`, commit
  `34f7186b86743a083a589741b6cea95293524108`;
- Oniguruma commit `4ef89209a239c1aea328cf13c05a2807e5c146d1`,
  matching jq's `vendor/oniguruma` gitlink;
- GNU C Library tag `glibc-2.39`, annotated tag object
  `9609a435f3f9a07c1cf607ad5821b12f735abd69`, peeled release commit
  `ef321e23c20eebc6d6fb4044425c00e6df27b05f`.

## Manifest and substitution closure

`porting/PORTING_MANIFEST.json` is a schema-v2, generator-owned inventory. Its
82 file records have these exact strategy totals:

| Strategy | Records | Release state |
|---|---:|---|
| `PORT` | 25 | All `done`, scoped parity true |
| `PROXY` | 17 | All `done`, scoped parity true |
| `GENERATED` | 6 | All `done`, scoped parity true |
| `REUSE` | 8 | All `done`, byte/provenance checked |
| `OMITTED` | 26 | All `done`, parity false by explicit exclusion |

Every record carries a non-empty semantic scope, exclusions, and evidence. Each
non-omitted target exists and has pinned jq provenance and a strategy marker.
Every proxy target also carries the six required substitution fields:
`UPSTREAM COMPONENT`, `REPLACEMENT`, `WHY`, `BEHAVIORAL CONTRACT`,
`KNOWN DIFFERENCES`, and `TESTS COVERING THE SUBSTITUTION`.

The manifest separately records two dependencies and 12 auxiliary
implementations. The auxiliary strategy totals are one `PORT`, six `PROXY`,
and five `GENERATED`; all are `done` with scoped parity true. Generated records
pin their upstream input hashes, generators/checkers, output files, and positive
range or record counts.

The `vendor/oniguruma` dependency contains an exact `implementationTargets`
inventory for every production `src/DotNetJq/Compatibility/Regex/*.cs` file:
15 unique targets split evenly between five `PORT`, five `PROXY`, and five
`GENERATED` records. The validator discovers that directory independently and
rejects a missing, extra, duplicate, nonexistent, unpinned, or incorrectly
marked target. No Oniguruma native code or binary is compiled, copied, or
linked. Managed `PORT` and `GENERATED` material derived from the pinned
Oniguruma sources is shipped, so every distribution carries the exact pinned
Oniguruma license as `THIRD_PARTY_LICENSES/Oniguruma-License.txt`.

## Test provenance and CLI boundary

The manifest machine-maps 11 upstream test categories:

- `mantest`, `jqtest`, `base64test`, `uritest`, `optionaltest`, `onigtest`, and
  `manonigtest`, with exact driver and unchanged fixture SHA-256 values;
- `shtest` and `utf8test`, with every library-relevant block mapped to managed
  tests through `tests/EXCLUSIONS.md`;
- `tests/modules/**`, with the pinned commit, exact 19-file Git tree and
  per-file hashes;
- `tests/torture/**`, with the pinned one-file Git tree and the managed streaming
  parser robustness replacement for the native process/Valgrind loop.

`src/main.c` and `src/jq_test.c` are now completed `PORT` mappings to
`src/DotNetJq.Cli/`, covering the public jq 1.8.2 command-line and
developer-mode test-runner contracts while explicitly documenting managed
branding and implementation differences. One machine-readable exclusion,
`native-shell-and-memory-tooling`, remains for native FILE*/LD_PRELOAD fault
injection, Valgrind accounting, allocator failures, signals, and C
bytecode/opcode tracing. The unchanged shell drivers still execute their
portable jq CLI assertions against `dotnetjq`; excluded native-only mechanics
are never reported as a managed CLI pass.

## License matrix

| Material | Distribution status | Applicable terms and evidence |
|---|---|---|
| jq-derived managed layout, APIs, and behavior | Shipped | jq MIT terms and all additional notices from pinned top-level `COPYING`; repository `COPYING.jq` and packaged `COPYING` are byte-identical to pinned jq |
| `Resources/builtin.jq` | Embedded and shipped byte-for-byte | Pinned jq notice; resource bytes exactly match `src/builtin.jq` at the jq pin |
| IBM decNumber compatibility mapping | Managed proxies shipped; native C not linked | ICU License; the five mapped targets preserve applicable IBM copyright and ICU identification, with full terms retained in `COPYING` |
| Oniguruma | Managed source-derived material shipped; native library not linked | K. Kosako BSD-style two-clause license at the pinned Oniguruma commit; the exact `vendor/oniguruma/COPYING` bytes ship as `THIRD_PARTY_LICENSES/Oniguruma-License.txt` |
| fdlibm-derived special functions | Managed translations shipped | Sun Microsystems permissive fdlibm notice preserved in source and `THIRD_PARTY_NOTICES.md` |
| GNU C Library gamma compatibility | Separate replaceable assembly in the managed package; statically incorporated into NativeAOT CLI artifacts | `LGPL-2.1-or-later`; exact GNU C Library 2.39 `COPYING.LIB`, source mapping, managed DLL replacement inputs, complete AOT source, eight-RID rebuild instructions, verified relink proof, and reverse-engineering permission are included |
| .NET NativeAOT runtime | Statically incorporated into RID-specific tool packages and standalone NativeAOT archives | Each binary distribution carries `LICENSE.TXT` and `THIRD-PARTY-NOTICES.TXT`, copied byte-for-byte from the exact SDK-resolved `Microsoft.NETCore.App.Runtime.NativeAOT.<rid>` pack and checked against that pack during release verification |
| Official jq test fixtures | Read unchanged from the external pinned jq checkout; not in product assembly/package | Notices in the pinned jq source distribution; manifest records exact hashes or Git trees and managed test usage |

`THIRD_PARTY_NOTICES.md` identifies the verbatim builtin reuse, managed
decNumber mapping, native-free Oniguruma boundary and managed source-derived
material, fdlibm-derived algorithms, the LGPL component and source mapping, the
.NET NativeAOT runtime notice boundary, and external-only fixtures.
`LICENSES.md` states that no single license covers the whole package and points
to the component-specific terms.

## NuGet metadata and contents

`src/DotNetJq/DotNetJq.csproj` targets `net10.0` and produces the
`DotNetJq.Library` package with:

- `PackageLicenseFile=LICENSES.md` and `PackageReadmeFile=README.md`;
- explicit publisher-supplied `Authors` and `RepositoryUrl` metadata, with
  exact identity equality enforced across the library, selector, and RID
  packages;
- `COPYING`, `COPYING.LIB`, `LICENSE.DotNetJq`, `LICENSES.md`, `README.md`, and
  `THIRD_PARTY_NOTICES.md` at the package root, plus the exact pinned GPPG and
  Oniguruma license files under `THIRD_PARTY_LICENSES/`;
- `DotNetJq.dll`, the separately built `DotNetJq.GlibcCompat.dll`, and XML
  documentation for both assemblies under `lib/net10.0/`;
- the complete non-`bin`/`obj` `DotNetJq.GlibcCompat` source tree plus
  `Directory.Build.props` and `global.json` under `lgpl-source/`;
- no external component-package dependency and no native/runtime-specific or
  process artifact.

The product and compatibility runner have no third-party NuGet package
dependencies. Test-framework packages are development-only and are not shipped
in the product package.

`src/DotNetJq.Cli/DotNetJq.Cli.csproj` produces the `dotnetjq` .NET 10
NativeAOT tool as one pointer package plus eight 64-bit RID packages. Every one
of those nine packages contains the complete, byte-validated corresponding
source under `aot-source/`, including `DotNetJq.GlibcCompat`, `DotNetJq`, the
CLI, checked-in generated parser/lexer and resources, build inputs, license
material, `porting/AOT_RELINKING.md`, and `tools/aot-compliance/`.
Each RID package additionally contains the exact `LICENSE.TXT` and
`THIRD-PARTY-NOTICES.TXT` selected from its resolved NativeAOT runtime pack;
the pointer package contains no runtime binary and therefore no invented
RID-independent runtime-license copy.

## LGPL corresponding-source, replacement, and NativeAOT relink proofs

The LGPL implementation is confined to
`DotNetJq.GlibcCompat.dll`. `DotNetJq.dll` contains only calls through the
public managed `GlibcCompatMath` ABI and contains none of the component's gamma
algorithms or coefficient tables.

`tools/verify-isolated-package.sh` proves the distribution boundary by:

1. requiring the license, notice, binaries, complete component source, project,
   offline NuGet configuration, and SDK/build inputs in the package;
2. byte-comparing every packaged corresponding-source file with the repository
   source and comparing all jq/glibc license copies with their pinned originals;
3. restoring and building the extracted component with network access disabled
   and all NuGet package sources cleared;
4. marking that rebuild, replacing only `DotNetJq.GlibcCompat.dll`, and verifying
   that the consumer and `DotNetJq.dll` hashes remain unchanged;
5. running the unchanged isolated consumer against both the packaged and rebuilt
   components, checking the public ABI, replacement identity, exact
   gamma-family results, and jq-visible calls;
6. rejecting a jq executable, native/P/Invoke/process dependency, ambient source
   checkout, or network dependency.

The package terms expressly permit reverse engineering of the combined work for
debugging modifications to the LGPL component. The packaged README explains how
to rebuild and replace the component while retaining the assembly name, version,
and four public gamma-family signatures.

For NativeAOT, `tools/aot-compliance/create-source-archive.sh` creates the
versioned deterministic `dotnetjq-aot-source-VERSION.tar.gz`, its external
SHA-256, and an internal complete `SOURCE_SHA256SUMS` inventory. The expanded
copy in each tool NuGet package is validated byte-for-byte against the same
declared source inputs. Standalone binary archives are released beside the
version-matched source archive.

`tools/aot-compliance/verify-relink.sh` proves the static relinking path by:

1. requiring two independently created source archives to be byte-identical
   and validating both checksum layers;
2. extracting into guarded temporary directories and confirming no upstream jq
   checkout is present;
3. masking the development and upstream source tree and disabling network
   access with bubblewrap;
4. building an original self-contained NativeAOT CLI from only the distributed
   source plus the preinstalled .NET/native toolchain;
5. changing only `GlibcCompatMath.Gamma`, then building a second NativeAOT CLI;
6. requiring the jq-visible result to change from the real gamma result to the
   controlled `424242.5` sentinel and requiring both source and executable
   hashes to differ.

## Release validators

The Linux semantic/integration entry point is `tools/verify-release.sh`. It
enforces the exact jq HEAD, clean jq and Oniguruma worktrees, the Oniguruma
gitlink/HEAD, and the exact module and torture fixture Git trees before running:

- parser/lexer structural checks, mutation self-tests, and deterministic
  output regeneration checks;
- `tools/generate_manifest.py --check --release`, which rejects any open
  non-`OMITTED` file, dependency, auxiliary, or test-category record;
- exact Oniguruma grapheme, case-fold, and Unicode-property data checks;
- the deterministic fdlibm corpus check;
- a production scan for process/oracle/source-checkout references and a
  managed-library scan for native interop;
- the complete managed compatibility suite and the runner's crash-isolation
  self-test;
- NativeAOT parser, regex, and gamma-boundary smoke verification;
- the host `linux-x64` NativeAOT CLI archive/package, native entry point, exact
  license and expanded `aot-source/` payload, with no `any` fallback;
- the deterministic AOT corresponding-source archive and proof-bound static
  GlibcCompat relink verification;
- general and regex differential corpora against the pinned jq oracle; and
- the isolated NuGet pack, consumer, offline rebuild, and DLL-replacement proof.

The cross-platform CLI and tag workflows are configured to require matching-runner
builds and strict, zero-skip process tests for all eight 64-bit RIDs. The complete release-bundle
gate then requires exactly ten NuGet packages, all eight archives, exact
archive-to-RID-package executable equality, runtime-pack license equality,
structurally verified package-manager metadata, checksums, and the hash-bound
publication plan. The protected tag workflow then redownloads and independently
reverifies that bundle after both Windows installed-package gates, attests its
checksum inventory, stages a draft GitHub release, publishes NuGet in the
verified order, and finally makes the release public. Stable versions alone
open the Homebrew tap PR and submit WinGet manifests.

The manifest release check verifies all 82 mappings: 25 `PORT`, 17 `PROXY`, 6
`GENERATED`, 8 `REUSE`, and 26 `OMITTED`, with one native-only CLI exclusion.

## Hash evidence

Mutable project and package hashes are deliberately not copied into this
document: doing so makes the audit stale after the next source edit. The
release tooling computes and checks them from the candidate, records the ten
NuGet hashes in `nuget-publish-order.tsv`, inventories the complete bundle in
`SHA256SUMS`, and emits the isolated replacement and relink hashes during the
gates. Independently pinned, stable license anchors remain enforced in code:

| Pinned input | SHA-256 |
|---|---|
| jq `COPYING` / repository `COPYING.jq` / packaged `COPYING` | `ad2b4a266b2268939c1446979759706077421cf906a203aa188c6f396e8cfd74` |
| glibc 2.39 `COPYING.LIB` and repository/package copies | `dc626520dcd53a22f727af3ee42c770e56c97a64fe3adb063799d8ab032fe551` |
| GPPG 1.2.5 license | `354bb658c3465bf907ad308f9aa51eea9511ffff7db723f78a519b17758aabd4` |
| Oniguruma `COPYING` | `70ba5469ea0bab6e18a32d7009068f996503168d27be57747e08da34337ff26f` |
| jq `src/builtin.jq` | `b8a5fd9579be9b51c9a04e6620f8c1655539aa57eea33a84e202a8dea401f2a4` |

## Remaining caveat

The release inputs currently have no package lock file or generated SBOM. This
does not change the package-content, source-provenance, or replaceability verdict,
but a lock file and SBOM should be emitted by a publication pipeline that
requires durable dependency attestation.
