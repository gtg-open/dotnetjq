# DotNetJq CLI compatibility tests

This project primarily launches the real CLI as a child process and compares exit codes,
stdout bytes, and stderr bytes, so the same process tests can exercise a
framework-dependent build or a NativeAOT binary. A small focused ownership slice calls
the internal jq-shaped input callback directly to verify that parser `jv` owners transfer
to the VM and are released on early reset; public behavior remains process-tested.

By default the project executes the `DotNetJq.Cli` build output. Set
`DOTNETJQ_CLI` to test a published `dotnetjq` executable instead.
Missing, empty, or non-executable configured subjects are unavailable test
inputs rather than deferred process-launch errors.

The differential tests require the jq 1.8.2 oracle built from commit
`34f7186b86743a083a589741b6cea95293524108`. They use
the executable named by `DOTNETJQ_JQ182`, or the provisioned binary at
`artifacts/test-assets/jq-1.8.2/oracle/jq` when present. The harness verifies
`jq --version` before using an oracle.
On Windows, the harness prefixes the oracle invocation with jq's `-b` option so
redirected stdin/stdout remain a byte boundary. jq's Windows CRT named-file text
mode is not used as an oracle for the managed always-binary named-file extension;
that behavior has a separate direct subject test.
Likewise, the pinned native Windows `jv_load_file()` reaches narrow `open()` for
module files after `wmain` has converted arguments to UTF-8. The Windows
differential module-path case therefore uses an ASCII path, while
`CliFileAndModuleTests.LibraryPathWithSpacesAndUnicodeLoadsModules` retains the
managed Unicode-path assertion on every platform.

The official `--run-tests` fixture test reads its checkout from
`DOTNETJQ_UPSTREAM`. Outside strict mode it may fall back to the `upstream/jq`
checkout. The harness SHA-256 verifies all seven consumed `.test` files and the
complete 19-file module fixture set against pinned jq 1.8.2 before executing
them; merely having matching filenames is not sufficient.

Local development permits dependency-based skips when the subject, oracle, or
upstream fixtures are unavailable. Release and CI verification must set
`DOTNETJQ_REQUIRE_FULL_COMPATIBILITY=1`; in that mode every unavailable or
invalid input is a test failure. Strict mode can still discover a valid local
subject or oracle; release jobs set all three paths explicitly so the tested
artifacts are unambiguous. A strict invocation that also proves the TRX contains
no skipped tests is:

```sh
DOTNETJQ_REQUIRE_FULL_COMPATIBILITY=1 \
DOTNETJQ_CLI=/absolute/path/to/dotnetjq \
DOTNETJQ_JQ182=/absolute/path/to/jq-1.8.2 \
DOTNETJQ_UPSTREAM=/absolute/path/to/jq-1.8.2-source \
dotnet test tests/DotNetJq.Cli.Tests/DotNetJq.Cli.Tests.csproj \
  --configuration Release \
  --logger 'trx;LogFileName=cli.trx' \
  --results-directory artifacts/test-results/cli
python3 tools/release/verify-trx-no-skips.py \
  artifacts/test-results/cli/cli.trx
```

Use `python` instead of `python3` on a Windows host where that is the Python
launcher name.

Output comparisons are byte-exact, including LF, NUL, JSON-sequence record
separators, ANSI escapes, partial output before failure, diagnostics, and exit
codes. The only deliberate product-brand difference is asserted without
normalization: native jq says `Use jq --help`, while this executable says
`Use dotnetjq --help`. Other diagnostics retain jq's `jq:` prefix for script
compatibility.

`DeveloperModeCompatibilityTests` also exercises the managed ports of
`--run-tests`, `--debug-dump-disasm`, `--debug-trace`, and
`--debug-trace=all`. It covers fixture grammar, selection/status/file/option
behavior, lifecycle output, and every record in all seven pinned official
fixtures through the real executable. Disassembly cases byte-compare production
bytecode listings with pinned jq 1.8.2; trace tests assert jq-observable opcode,
program-counter, value, and stack ordering without claiming exact native
stack/refcount trace-text identity. The strict TRX check is the release evidence
that the complete CLI process suite ran without skips.
