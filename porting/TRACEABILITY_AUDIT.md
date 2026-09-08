# Definition-of-Done traceability audit

Audit date: 2026-09-06
Manifest: `porting/PORTING_MANIFEST.json`
Semantic source: jq 1.8.2, commit
`34f7186b86743a083a589741b6cea95293524108`

## Verdict

The requested structural traceability remediation is complete. The manifest
has an exact 82-entry match to its declared checked-in upstream inventory,
all declared targets and supporting artifacts resolve, all mapped C# targets
carry consistent provenance headers, and the generated parser/lexer outputs
have a deterministic regeneration and non-writing verification path.

This is a scoped traceability and compatibility verdict, not a claim of
unscoped C ABI, VM architecture, byte-identical Flex/Bison C output, or Bison's
private table numbering. A manifest entry
marked `done`/`semanticParity: true` claims only its declared
`semanticParityScope` and preserves explicit exclusions. Intentional omissions
are `done`/`semanticParity: false`. Manifest schema version 2 is release-closed:
all 82 file entries and every dependency, auxiliary implementation, and
machine-mapped test category have a terminal status. The embedded `builtin.jq`
resource is byte-identical to upstream, assembly-loaded as strict UTF-8, parsed
as a library, and contributes all 106 ordered source definitions to every
program's builtin scope.

## Verification results

| Check | Result | Evidence |
| --- | --- | --- |
| Exact upstream pin | PASS | The reference checkout, represented here as repository-relative `upstream/jq`, was clean at commit `34f7186b86743a083a589741b6cea95293524108`; the exact tag is `jq-1.8.2`. |
| Canonical checked-in inventory | PASS | 82/82 exact key-set match: 45 regular files directly under upstream `src/`, 30 `.c`/`.h` files directly under `vendor/decNumber/`, and seven named upstream `.test` fixtures. There are no missing or extra keys. |
| Strategy vocabulary | PASS | 25 `PORT`, 17 `PROXY`, 6 `GENERATED`, 8 `REUSE`, and 26 `OMITTED`; no strategy falls outside `PORT`, `PROXY`, `GENERATED`, `REUSE`, and `OMITTED`. |
| Strategy metadata | PASS | All 17 proxies have an explicit replacement, all six generated entries have source-of-truth and generator metadata, and all 34 reused/omitted entries have a reason. |
| Target existence | PASS | All 56 non-null file mappings resolve, representing 55 unique targets. The `src/main.c` target is the dedicated `src/DotNetJq.Cli/` project; the Oniguruma dependency target, all 12 auxiliary implementation targets, managed grammar inputs, generators/checkers, adapters, declarations, and generated outputs also resolve. |
| Mapped C# headers | PASS | 45/45 manifest entries with C# targets contain the exact repository, full revision/tag, mapped upstream file (or explicit shared upstream file), exact upstream URL, manifest strategy, and target path. |
| Generated pipeline | PASS | GPLEX byte-checks the production scanner; `DotNetJq.ParserGen` validates the managed grammars and byte-checks the production GPPG parser. The parser guard requires 167 jq alternatives, 169 generated rules, 312 states, zero conflicts, and byte-identical `%left`/`%right` tables for every marked `%precedence` stand-in. `generate.py --check` independently verifies jq-shaped declaration outputs and the structural coverage report. |
| Manifest pipeline | PASS | `python3 tools/generate_manifest.py --check` verifies the exact 82-entry inventory, completion schema, resolved targets, fixture/resource hashes, generated metadata, and required proxy headers without writing. The schema-v2 `--release` mode additionally rejects every open non-`OMITTED` file, dependency, auxiliary, or test-category mapping. |
| `builtin.jq` content/status | PASS, scoped | The managed resource is byte-identical to pinned upstream, assembly-loaded as strict UTF-8, parsed once as a library, and all 106 ordered definitions are bound into the source-builtin scope. Private primitives remain managed implementations. |
| Test-fixture reuse | PASS | All seven entries resolve directly into the pinned upstream checkout, each records the unchanged fixture SHA-256 and exact pass count, and the aggregate official result is 879/879. |
| Managed CLI process coverage | PASS, scoped | `DotNetJq.Cli.Tests` passes 84/84 without skips. Its input boundary coverage includes the exact 4,091-byte `fgets` payload, UTF-8 tail reads, raw cross-file continuation, EOF/blank/final-empty positions, and parsed/raw/slurp oracle comparisons. Its 22/22 developer-mode cases across 18 methods exercise managed `--run-tests`, production-bytecode disassembly, and tracing, including all seven official fixtures for 879/879 records through the real executable. Exact native VM trace identity remains excluded. |
| Upstream test-category inventory | PASS | `testCategories` machine-maps all nine pinned shell drivers, the exact 19-file `tests/modules/**` tree, and the exact one-file `tests/torture/**` tree. `shtest` and `utf8test` now target the unchanged-driver CLI verifier as well as focused process/library suites. Driver/file SHA-256 values, Git trees, managed targets, scoped parity, and the remaining native-only exclusion are validator-checked. |
| Native dependency boundary | PASS | The managed library and compatibility assembly contain no process launch or jq/libjq/native-engine interop declaration. The CLI has only OS console/TTY declarations (`kernel32` console mode and libc `isatty`); it never loads or invokes native jq/libjq. The main project has one intentional managed project/assembly reference to the separately replaceable `DotNetJq.GlibcCompat`; neither project has a production NuGet or bundled native dependency. The upstream path appears only in generated-file regeneration comments. |
| Managed library build | PASS | `dotnet build src/DotNetJq/DotNetJq.csproj --no-restore` built both managed assemblies with 0 warnings and 0 errors for `net10.0`, and a downstream tests-only project reference copied both DLLs. The NuGet package ID is `DotNetJq.Library`; assembly and namespace identity remain `DotNetJq`, separate from the `dotnetjq` tool package/command. |

## Generated-source traceability

The six generated mappings now distinguish grammar inputs from concrete
outputs:

| Upstream entry | Role or concrete target | Source of truth |
| --- | --- | --- |
| `src/lexer.l` | Maintained managed grammar input | `src/DotNetJq/Grammar/lexer.l` |
| `src/lexer.c` | GPLEX output `src/DotNetJq/Generated/Lexer/lexer.c.cs` | `src/DotNetJq/Grammar/lexer.l` |
| `src/lexer.h` | jq-shaped managed token/location declarations in `src/DotNetJq/Generated/Lexer/lexer.h.cs` | `src/DotNetJq/Grammar/lexer.l` |
| `src/parser.y` | Maintained managed grammar input | `src/DotNetJq/Grammar/parser.y` |
| `src/parser.c` | GPPG output `src/DotNetJq/Generated/Parser/JqGeneratedParser.g.cs` plus maintained scanner/support adapters | `src/DotNetJq/Grammar/parser.y` |
| `src/parser.h` | jq-shaped parse-result/entry-point declarations in `src/DotNetJq/Generated/Parser/parser.h.cs` | `src/DotNetJq/Grammar/parser.y` |

Each managed grammar records its exact upstream repository, revision, path,
URL, and source hash. The C action bodies are manually ported to C# in the same
inline positions; jq's original C-action `.l` and `.y` files are not committed.
GPLEX/GPPG outputs record their generator identities and are byte-checked.
`tools/parser-gen/README.md` documents the workflow, and
`.github/workflows/parser-generation-check.yml` checks drift in CI.

The generated GPPG parser is the sole production parser. Its stack guard accepts
9,994 balanced-parenthesis levels and rejects 9,995 with the bounded
`jq: error: memory exhausted` diagnostic. The six mappings claim
`done`/`semanticParity: true` only for deterministic output plus the observable
jq grammar, token, location, recovery, and resource-boundary contract proved by
the compatibility suites. Exact Flex/Bison C-output identity, automatic C-action
translation, and Bison's private table/recovery internals remain explicit
exclusions.

`src/jv_utf8_tables.h` is no longer falsely grouped with lexer generation. It
is a `PROXY` mapping into `src/DotNetJq/Port/src/jv_unicode.c.cs`, whose header
names it as a shared upstream source and explains that .NET `Rune` and managed
UTF-8 traversal replace the native lookup tables.

## Proxy and auxiliary traceability

The manifest records `src/compile.c`, `src/compile.h`, `src/execute.c`, and
`src/jq.h` as production `PORT` mappings: parser IR, compiler lowering, direct
bytecode VM, and state lifecycle respectively. Their headers state the source
surface, ownership contract, managed representation differences, and tests.
`src/libm.h` is consistently classified as a
proxy combining `System.Math`, permissively licensed managed Sun fdlibm
kernels, and a thin ABI call into the separately replaceable gamma-only
`DotNetJq.GlibcCompat` assembly. The bridge is also recorded under
`auxiliaryImplementations`, while the pinned GNU C Library source and license
boundary are recorded under `dependencies`.

The `dependencies` section maps jq's pinned Oniguruma submodule commit
`4ef89209a239c1aea328cf13c05a2807e5c146d1` to all 15 production files in
`Compatibility/Regex/*.cs` with explicit `PORT`, `PROXY`, or `GENERATED`
roles. The directory is exact-discovered and validator-checked. The
`auxiliaryImplementations` section links the filesystem, decimal, regex, time,
and CLI helpers back to their upstream source components so their role is
discoverable even when they are not the primary one-to-one target.

Upstream `src/main.c` is now a scoped `PORT` into `src/DotNetJq.Cli/`, not an
intentional omission. The mapping covers jq's public option, input, output,
module, diagnostic, halt, and process-status behavior through the `dotnetjq`
application. Its exclusions are implementation-specific: product branding,
native allocator/`FILE*` and signal mechanics, and exact native bytecode,
program-counter, stack/refcount, and trace-text identity. The managed CLI ports
`--run-tests` and provides production-bytecode `--debug-dump-disasm`,
`--debug-trace`, and `--debug-trace=all` output through jq-shaped interfaces.
Process-level tests invoke the actual executable and preserve stdout/stderr
bytes and statuses.
`ManagedTestRunner.cs` is additionally recorded as a `PORT` auxiliary of
`src/jq_test.c`; `DeveloperModeCompatibilityTests` passes 22/22 cases across 18 methods covering
its streaming fixture grammar, selection/status/file/option behavior, lifecycle
output, all seven official fixtures (879/879), exact production-bytecode
disassembly comparisons, and managed verbose trace integration. The complete
executable process suite passes 84/84 without skips.

The generated regex auxiliary
`Compatibility/Regex/OnigurumaExtendedGraphemeData.cs` is traced to pinned
Oniguruma `src/unicode_egcb_data.c`. Its independent checker reproduces the
Unicode 16.0 payload from source and verifies SHA-256
`21663445ace4f64775506f3fc53332a96e1b2b9f509b63eeb5462913daeb6d73`,
1,376 ranges, 1,792 encoded bytes, and 399 each of the regular Hangul LV/LVT
ranges. `tools/verify-release.sh` runs that checker. Its scoped runtime closure
passes within the 1,029/1,029 final Regex/Oniguruma slice, including a
100,002-character performance boundary, and the pinned property partitions.

The generated `OnigurumaSimpleCaseFoldData.cs` auxiliary is independently
reproduced from pinned Oniguruma `unicode_fold_data.c` at SHA-256
`690b14e8f84ff1345ec38657ab41a0c630dea37b99fb39519d9789ddf2558a55`.
Its release-gated checker verifies Unicode casefold version 160000, 1,423
groups, and 1,453 non-canonical members; focused helper tests pass 25/25.

Three generated Unicode-property auxiliaries are traced to pinned Oniguruma
`unicode_property_data.c` and `unicode_property_data_posix.c`. The release
checker endpoint-compares 612 Unicode tables, 15 POSIX tables, 629 CodeRanges
routes, 886 aliases, 627 decoded range tables, and the six focused emoji/
extended-pictographic tables containing 359 ranges.

`Compatibility/Time/JqStrptimeRegex.cs` isolates the managed regex field
scanner used by `strptime` behind a mapped jq-shaped proxy. The direct
`builtin.c.cs` port no longer imports .NET regex for time parsing or JSON-error
classification. Its proxy header and 17 focused cases freeze the supported
directive/tail contract and the explicit POSIX/locale exclusions.

The completed regex dependency mapping is `done`/`semanticParity: true` within
the jq-visible Oniguruma-backed filter contract. Evidence is the 1,029/1,029
focused Regex/Oniguruma slice, 2,678/2,678 complete managed suite, unchanged
`onig.test` 47/47 and `manonig.test` 19/19, and all 4,980 declared differential
cases across 33 categories (a request for 4,981 is rejected). An independent
frozen-artifact audit additionally matches 92,589/92,589 primary rows and 62/62
isolated processes, with zero process mismatch, crash, abort, unhandled
exception, or timeout. Native Oniguruma API, bytecode, allocator, engine
architecture, and C ABI identity are explicitly outside that scoped parity claim.

## Inventory and resource policy

The machine-readable `inventoryPolicy` makes the authoritative checked-in
scope explicit. It also documents these upstream `BUILT_SOURCES` rather than
silently omitting them:

- `src/builtin.inc` is an ephemeral C include generated from the separately
  mapped `src/builtin.jq`; the managed build embeds the source resource.
- `src/config_opts.inc` is native configure output; the managed CLI reports its
  .NET target, execution engine, RID, and jq compatibility level directly.
- `src/version.h` is a native generated header; managed product and jq
  compatibility versions are explicit CLI build inputs.

The `src/builtin.jq` mapping is intentionally scoped. It proves exact resource
bytes, assembly loading, strict UTF-8 parsing, and runtime binding of all 106
ordered definitions. It does not claim source-text evaluation in place of
managed private primitives, native bytecode/VM identity, or behavior outside
the separately tested public builtin contract.

## Official test-category and CLI-boundary traceability

Manifest schema version 2 records the upstream test launchers as semantic inputs
instead of treating only their `.test` payloads as evidence. `mantest`, `jqtest`,
`base64test`, `uritest`, `optionaltest`, `onigtest`, and `manonigtest` each record
the exact driver and fixture hashes, their managed reporter targets, their unchanged
pass count, and the shell/native-harness exclusions. `shtest` and `utf8test` point to
`tools/verify-cli-compatibility.sh`, which archives the pinned commit and executes
both drivers unchanged with `JQ` set to `dotnetjq`, plus byte-exact CLI process tests
and focused library-semantic suites. `tests/EXCLUSIONS.md` documents the boundary.

The module dependency provenance is closed independently of prose: Git tree
`457673594ed5c117efba91acbd87931fb9bf309e` contains exactly 19 recorded relative
paths, each with a pinned SHA-256. The torture tree
`b7ee9eb5804f1cade2d0321af1566d807c165791` contains the single hash-locked
`input0.json`. Validation discovers both directories and rejects any missing, added,
or changed file. The release gate also compares both Git tree objects directly with
the clean pinned jq checkout.

The top-level `cliExclusions` now contains one stable ID:
`native-shell-and-memory-tooling`. Public option parsing, terminal/color presentation,
process-status/stderr conversion, input discovery, and `$HOME` startup behavior are no
longer exclusions. The remaining record covers native `LD_PRELOAD`/`FILE*` injection,
Valgrind/allocator/signal identity, and exact C VM trace identity. The managed test,
disassembly, and trace switches remain implemented, and the unchanged driver's portable
assertions still execute through the managed CLI.

## Reproduction commands

```sh
python3 tools/generate_manifest.py --check --release
tools/parser-gen/generate-gplex-lexer.sh --check
dotnet run --project tools/parser-gen/DotNetJq.ParserGen/DotNetJq.ParserGen.csproj -- \
  validate --parser src/DotNetJq/Grammar/parser.y --lexer src/DotNetJq/Grammar/lexer.l
dotnet run --project tools/parser-gen/DotNetJq.ParserGen/DotNetJq.ParserGen.csproj -- \
  check-generated-parser --parser src/DotNetJq/Grammar/parser.y \
  --output src/DotNetJq/Generated/Parser/JqGeneratedParser.g.cs
python3 tools/parser-gen/generate.py --check --upstream upstream/jq
tools/verify-cli-compatibility.sh
perl tools/DotNetJq.DifferentialProbe/verify-oniguruma-egcb-data.pl \
  --check upstream/jq/vendor/oniguruma/src/unicode_egcb_data.c \
  src/DotNetJq/Compatibility/Regex/OnigurumaExtendedGraphemeData.cs
perl tools/DotNetJq.DifferentialProbe/generate-oniguruma-simple-case-fold-data.pl \
  --check upstream/jq/vendor/oniguruma/src/unicode_fold_data.c \
  src/DotNetJq/Compatibility/Regex/OnigurumaSimpleCaseFoldData.cs
dotnet build src/DotNetJq/DotNetJq.csproj --no-restore
```

The generated-parser Release promotion candidate passed 1,441/1,441; the
current complete managed suite passes 2,678/2,678 with zero skips. The compatibility checkpoint also passes all seven unchanged
official fixtures 879/879, the general differential corpus
5,040/5,040, and the complete declared regex differential inventory 4,980/4,980.
The schema-v2 release check rejects any reopened non-`OMITTED` mapping.
