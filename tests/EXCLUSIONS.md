# Upstream shell-driver execution and native-only exclusions

Target: jq 1.8.2, commit
`34f7186b86743a083a589741b6cea95293524108`.

`shtest` and `utf8test` are no longer translated into a library-shaped test
driver. `tools/verify-cli-compatibility.sh` verifies their pinned SHA-256 values,
archives the exact pinned commit, builds `DotNetJq.Cli`, sets `JQ` to that real
executable, and invokes `tests/shtest` and `tests/utf8test` unchanged. The only
temporary `jq` command alias is confined to the archived fixture because
`tests/jq-f-test.sh` intentionally finds a command with that exact name through
`PATH`; the product itself remains named `dotnetjq`.

This distinction matters: shell is the upstream test orchestration language,
not a substitute implementation. Options, files, streams, bytes, diagnostics,
and exit codes are produced by `DotNetJq.Cli` in a child process. The managed
library-focused tests remain independent, more local evidence for the same
semantic paths.

## Driver coverage

| Upstream driver | Managed execution boundary |
| --- | --- |
| `tests/shtest` | The unchanged driver exercises filter-file execution; raw/slurp/sequence/stream input; parse recovery; bundled and interspersed options; `-e`; compact, indented, raw, NUL-delimited, and ANSI-colored output; `$ARGS`; named and file arguments; `$HOME/.jq`; `-L`, import order, and cycles; halt/error bytes and statuses; diagnostics; locale/time-zone behavior where enabled by the host; every available prefix of `tests/torture/input0.json`; and the pinned security regressions. |
| `tests/utf8test` | All eight unchanged supplementary-plane loops execute through `-f` and `-R -s`, covering program-file and raw-file byte decoding through the CLI. |
| `tests/modules/**` | The exact 19-file tree is consumed by the unchanged `shtest` module blocks and by focused CLI/library tests; paths and SHA-256 values are locked in `PORTING_MANIFEST.json`. |
| `tests/torture/**` | With ordinary (non-Valgrind) setup, the unchanged `shtest` loop invokes `dotnetjq` for every byte prefix in normal and streaming reconstruction modes and rejects assert/abort/core diagnostics. Focused streaming-parser tests additionally exercise split-buffer state directly. |

`tests/DotNetJq.Cli.Tests` complements those drivers with byte-exact process
tests and pinned-oracle comparisons. It covers cross-platform input-file state,
Unicode and space-bearing paths, missing-file partial output, option diagnostics,
compile/runtime errors, output modes, color policy, startup modules, arguments,
halts, and statuses. Its developer-mode coverage runs all seven official
fixtures through `--run-tests` for every fixture record. Release and
cross-platform CI run the entire process suite in strict mode and reject a TRX
containing any skipped or non-passing test, so these claims do not depend on a
mutable method count. `tests/cli-shell/verify.sh` and
`tests/powershell/Verify-DotNetJq.ps1` run the installed/published command shape;
the latter covers both PowerShell 7 and Windows PowerShell 5.1, including
`$LASTEXITCODE` and byte-preserving LF/NUL output. The release matrix also
installs the final selector plus exact `win-x64` or `win-arm64` RID package from
the verified local-only bundle, rejects a different resolved RID or shim
architecture, and treats either missing PowerShell implementation as a failed
gate rather than an exclusion.

## Remaining native-only boundary

The manifest has one structured CLI exclusion,
`native-shell-and-memory-tooling`. It is deliberately narrow:

- `shtest` lines 30-43 load the native `libinject_errors.so` shim with
  `LD_PRELOAD` to force C `FILE*` read/close failures. The clean archived test
  tree contains no native build product, so the unchanged driver's own guard
  skips this block. Managed file/open/read/write failures have direct process
  and library tests, but a C stdio interposer cannot instrument .NET streams.
- When upstream `VALGRIND` integration is enabled, it observes native heap and
  leak behavior. The same jq commands and comparisons run without Valgrind;
  Valgrind's accounting is not a managed-runtime semantic result.
- Native allocator failures, signal identity, and exact native bytecode,
  program-counter, stack/refcount, and trace-text identity are implementation-
  internal surfaces. The CLI implements `--run-tests` and managed structural
  `--debug-dump-disasm`, `--debug-trace`, and `--debug-trace=all`; those views
  support portable fixture, size/binding, and event-order checks without claiming
  byte-for-byte native VM identity.

Platform conditionals in the unchanged scripts are not silently counted as a
universal single-host result. Unix TTY and installed-locale branches run only
when the driver enables them. Windows `HOME`/`USERPROFILE`/
`HOMEDRIVE`+`HOMEPATH` precedence, argument encoding, binary standard streams,
and PowerShell behavior are covered by the dedicated Windows CI matrix. These
checks assert dotnetjq's cross-platform always-binary
policy; they do not claim that default native `jq.exe` omits CRT text
translation. The intentional Windows-only CRLF/`CTRL+Z` discrepancy, including
stdin, named-file, pipe, redirected-output, and `-b` boundaries, is recorded in
[`porting/WINDOWS_STDIO_CONTRACT.md`](../porting/WINDOWS_STDIO_CONTRACT.md).
NativeAOT commands are exercised separately for all declared 64-bit RIDs.

## Other upstream groups

The seven semantic fixtures (`jq.test`, `man.test`, `onig.test`,
`manonig.test`, `base64.test`, `uri.test`, and `optional.test`) remain byte-for-
byte reused through the managed compatibility runner. No failing case is
deleted, weakened, or placed on an expected-failure allowlist merely because an
upstream runner is a shell program or native executable.
