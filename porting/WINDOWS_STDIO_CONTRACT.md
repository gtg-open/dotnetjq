# Windows standard-stream and CRLF contract

## Decision and scope

`dotnetjq` uses byte-oriented standard streams and files on every operating
system. On Windows, `-b`/`--binary` is accepted for jq command-line
compatibility but is intentionally idempotent: the streams are already
untranslated. Output is UTF-8 without a BOM and jq-generated record separators
are LF (`0A`), not CRLF (`0D 0A`). Input is passed to jq's byte parser without
CRT newline or `CTRL+Z` translation.

Two facts must not be conflated:

- **Implementation policy:** always-binary I/O is cross-platform and applies
  on Windows, Linux, and macOS, with or without `-b`.
- **Compatibility discrepancy:** the resulting byte behavior differs from jq
  only on native Windows builds in their default (no-`-b`) CRT text mode.
  jq's non-Windows `-b` branch is a no-op, and POSIX text and binary stream
  modes do not perform the Windows CRLF/`CTRL+Z` translations. Therefore this
  policy does not create an equivalent Linux or macOS discrepancy.

This matches jq's Unix behavior and the standard-stream behavior of native
Windows jq when it is invoked with `-b`. It does **not** reproduce native
Windows jq's default Microsoft C runtime text translations. It also does not
reproduce those translations for named files, which native jq opens in text
mode even when `-b` was supplied.

The difference is a Windows process-I/O presentation detail. It does not change
the jq language or the managed library API. It is observable in the CLI when
literal CRLF or `CTRL+Z` bytes enter through stdin/files, or when exact output
and diagnostic line-ending bytes are inspected.

## Pinned jq 1.8.2 source mapping

The semantic reference is jq 1.8.2 commit
`34f7186b86743a083a589741b6cea95293524108`.

| Source | Pinned behavior |
|---|---|
| [`src/main.c:271-285`](https://github.com/jqlang/jq/blob/34f7186b86743a083a589741b6cea95293524108/src/main.c#L271-L285) | Native Windows jq receives the Unicode command line in `wmain` and converts every argument to UTF-8 with `WideCharToMultiByte(CP_UTF8)` before normal option processing. This path is separate from stdin and is unaffected by `-b`. |
| [`src/main.c:319-325`](https://github.com/jqlang/jq/blob/34f7186b86743a083a589741b6cea95293524108/src/main.c#L319-L325) | On Windows, jq initially sets `stdout` and `stderr` to `_O_TEXT | _O_U8TEXT`. `stdin` remains in the CRT's default text mode. |
| [`src/main.c:420-427`](https://github.com/jqlang/jq/blob/34f7186b86743a083a589741b6cea95293524108/src/main.c#L420-L427) | `-b` flushes output and changes only the three standard descriptors to `_O_BINARY`. The branch is a no-op outside Windows. |
| [`src/main.c:175-249`](https://github.com/jqlang/jq/blob/34f7186b86743a083a589741b6cea95293524108/src/main.c#L175-L249) | Results and errors are written through `stdout`/`stderr`; therefore redirected output is subject to the selected CRT translation mode. |
| [`src/util.c:260-345`](https://github.com/jqlang/jq/blob/34f7186b86743a083a589741b6cea95293524108/src/util.c#L260-L345) | Primary stdin and named input files are read through `FILE*`; named files are opened with mode `"r"`. |
| [`src/jv_file.c:12-83`](https://github.com/jqlang/jq/blob/34f7186b86743a083a589741b6cea95293524108/src/jv_file.c#L12-L83) | Program, module, `--rawfile`, and `--slurpfile` data pass through a descriptor/`FILE*` opened for text reading on Windows. |
| [`src/util.h:20-41`](https://github.com/jqlang/jq/blob/34f7186b86743a083a589741b6cea95293524108/src/util.h#L20-L41) | When jq recognizes an interactive Windows console, `priv_fwrite` bypasses stdio and calls `WriteFile`; redirected streams continue through `fwrite`. |
| [jq 1.8 manual, `--binary`](https://github.com/jqlang/jq/blob/34f7186b86743a083a589741b6cea95293524108/docs/content/manual/v1.8/manual.yml#L291-L295) | jq explicitly recommends `-b` to WSL, MSYS2, and Cygwin users invoking native `jq.exe`, to prevent LF-to-CRLF output conversion. |

Microsoft documents the relevant CRT rules: text input converts CRLF to LF and
treats `CTRL+Z` as EOF, text output converts LF to CRLF, and binary mode
suppresses those translations. `_O_U8TEXT` remains a translated text mode.
See [`_setmode`](https://learn.microsoft.com/en-us/cpp/c-runtime-library/reference/setmode?view=msvc-170)
and [translation-mode constants](https://learn.microsoft.com/en-us/cpp/c-runtime-library/translation-mode-constants?view=msvc-170).

## Observable behavior matrix

The table describes redirected handles, pipes, and regular files, where bytes
can be compared exactly. An interactive legacy Windows console has additional
console-host and code-page behavior discussed below.

| Surface on Windows | Native jq, no `-b` | Native jq with `-b` | `dotnetjq`, with or without `-b` |
|---|---|---|---|
| Command-line arguments | Windows Unicode arguments are converted to UTF-8 by jq's `wmain` | Same; `-b` does not affect arguments | Windows Unicode arguments become CLR strings, then jq values use UTF-8 storage |
| Standard input | CRT text: CRLF becomes LF; `CTRL+Z` can terminate input | Untranslated bytes | Untranslated bytes |
| Named primary input file | CRT text | CRT text; `-b` does not change later file opens | Untranslated bytes |
| `-f`, modules, `--rawfile`, `--slurpfile` | CRT text | CRT text | Untranslated bytes |
| Redirected stdout | LF becomes CRLF | LF remains LF | LF remains LF |
| Redirected stderr | LF becomes CRLF | LF remains LF | LF remains LF |
| NUL and JSON-sequence framing | Newline translation still applies; NUL/RS themselves are unchanged | Untranslated | Untranslated |

Concrete examples:

- Standard input `61 0D 0A` with `-R -c .` is parsed by default native
  Windows jq as the one-character line `a`, but by `jq -b` and `dotnetjq` as
  the two-character line `a\r`. Compact output consequently contains `"a"`
  versus `"a\\r"`, in addition to the output record's CRLF-versus-LF
  difference.
- `-n -r '"x"'` redirected to a byte consumer ends in CRLF with default
  native Windows jq, and LF with `jq -b` or `dotnetjq`.
- A raw string containing an LF is changed to CRLF by redirected native jq
  text output. An existing CRLF payload can therefore become CR-CRLF. Binary
  jq and `dotnetjq` preserve the payload's bytes.
- A named raw-input file containing CRLF is normalized by native Windows jq
  even with `-b`, because `-b` only changes the standard descriptors.
  `dotnetjq` preserves the CR before the LF.

For ordinary parsed JSON, CR and LF are both whitespace, so the input
translation is commonly invisible. It becomes significant for `-R`, `-Rs`,
`--rawfile`, literal byte payloads, exact diagnostics, and malformed or
`CTRL+Z`-containing data.

## Why `dotnetjq` behaves differently

On Windows, [`Program.cs`](../src/DotNetJq.Cli/Program.cs) obtains
`Console.OpenStandardInput/Output/Error()`; on Unix it wraps descriptors 0/1/2
directly so startup does not emit terminal-initialization escape sequences. The
CLI reads and writes those `Stream` instances as bytes. [`CliInputReader.cs`](../src/DotNetJq.Cli/CliInputReader.cs)
also opens named files with `FileStream`. [`CliOutput.cs`](../src/DotNetJq.Cli/CliOutput.cs)
writes explicit UTF-8 bytes plus explicit LF, record-separator, and NUL bytes.
No `TextReader`, `TextWriter`, or CRT `FILE*` layer performs newline
translation. .NET describes `Stream` as its byte input/output abstraction, and
`Console.OpenStandardOutput()` returns the standard output stream directly:
[.NET stream I/O](https://learn.microsoft.com/en-us/dotnet/standard/io/) and
[`Console.OpenStandardOutput`](https://learn.microsoft.com/en-us/dotnet/api/system.console.openstandardoutput?view=net-10.0).

The parsed `CliOptions.Binary` flag therefore has no downstream action. This is
not an accidental platform check omission: the present CLI contract is
cross-platform binary I/O, and `-b` expresses an already-satisfied request.

## Command-line arguments are not stdin

On Windows there is no byte encoding to choose for a direct command argument.
PowerShell first evaluates an argument as a Unicode string, Windows transports
the native command line as UTF-16, and .NET supplies `Program.Main` with CLR
strings. When an argument becomes a jq string, `dotnetjq` encodes that CLR
string into the port's UTF-8-backed `jv` storage. Native Windows jq has the same
observable text boundary: its pinned `wmain` receives `wchar_t` arguments and
converts them to UTF-8 before `umain` processes the program, `--arg` values, and
path strings. Subsequent filesystem path resolution is a separate OS/API
boundary; it is not controlled by stdin encoding or `-b` either.

That filesystem boundary is not uniformly Unicode-capable in native jq. The
pinned Windows [`util.c` `fopen` wrapper](https://github.com/jqlang/jq/blob/34f7186b86743a083a589741b6cea95293524108/src/util.c#L70-L81)
converts primary input filenames from UTF-8 to `_wfopen`, but module discovery
uses narrow `stat` and [`jv_load_file()` uses narrow `open`](https://github.com/jqlang/jq/blob/34f7186b86743a083a589741b6cea95293524108/src/jv_file.c#L12-L18).
The pinned Windows executable can therefore reject a module path outside its
active narrow code page even though `wmain` decoded the argument correctly.
The Windows differential module test uses an ASCII path for that reason;
dotnetjq's Unicode `System.IO` module-path behavior remains a direct process
test and a PowerShell test.

Consequently `$OutputEncoding`, the console code page, stdin redirection, and
`-b` do not control the character encoding of a jq program or `--arg` value
passed directly on the Windows command line. PowerShell 5.1 and PowerShell 7 do
have different quoting and argument-splitting rules for some difficult values.
In particular, Windows PowerShell 5.1's legacy native-argument marshalling can
consume double quotes embedded in a jq filter; escape those quotes for the
native command line or pass a quote-heavy filter with `-f`. This can change
*where* argument boundaries fall or which literal characters reach jq, but it
is not a UTF-8 versus UTF-16 conversion problem. See PowerShell's
[native-argument documentation](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_parsing#passing-arguments-to-native-commands).

On Linux and macOS, native process arguments are byte strings. The .NET hosting
boundary uses UTF-8 `char *` strings there, while native jq consumes the argument
bytes directly as UTF-8 text. Bash does not transcode every argument for jq: the
terminal, locale, producer, and shell must already agree on the bytes. This is
the same precondition native jq has and is unrelated to the Windows CRT text-mode
discrepancy. The .NET host's platform string encodings are recorded in the
[native-hosting contract](https://github.com/dotnet/runtime/blob/main/docs/design/features/native-hosting.md#scope).

## Home-directory lookup

Startup-library discovery follows jq 1.8.2's `src/util.c:get_home()` platform
branches over the CLI's immutable startup environment snapshot. POSIX hosts use
`HOME` only. Windows uses the first non-null value from `HOME`, then
`USERPROFILE`, then concatenates `HOMEDRIVE` (or an empty string when absent)
with `HOMEPATH`; it has no fallback when `HOMEPATH` is absent. Empty-but-present
values retain jq's `getenv()` semantics and stop the fallback chain. The
snapshot dictionary is case-insensitive on Windows and case-sensitive on POSIX,
matching the corresponding environment-name rules. Both the ordinary CLI path
and the managed `--run-tests` driver use this same resolver.

## PowerShell streams and UTF-8

Binary/text newline translation and character encoding are separate concerns.
A pipe carries bytes and has no encoding metadata. PowerShell must encode its
string pipeline objects before a native process can read them.

- PowerShell 7 defaults `$OutputEncoding` to BOM-less UTF-8. Windows PowerShell
  5.1 defaults this native-pipeline boundary to ASCII, so non-ASCII text needs
  explicit configuration. After UTF-8 is selected, Windows PowerShell 5.1
  prefixes a native stdin pipeline with a UTF-8 BOM; PowerShell 7 does not.
- `$OutputEncoding` controls how PowerShell encodes string objects piped into
  a native application's stdin. It does not affect command arguments.
- `[Console]::OutputEncoding` controls the encoding PowerShell assumes when it
  decodes a native application's stdout into PowerShell strings. Interactive
  input inherited directly from a Windows console instead depends on the
  console input encoding/code page; configure `[Console]::InputEncoding` as
  UTF-8 when byte-exact non-ASCII typing must be guaranteed.
- `dotnetjq` does not inspect any of those settings. It accepts stdin bytes and
  emits UTF-8 bytes; the shell is responsible for any conversion between bytes
  and PowerShell strings. See Microsoft's
  [character-encoding documentation](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_character_encoding)
  and [`$OutputEncoding`](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_preference_variables#outputencoding).

The stdin pipe contains no declaration saying "UTF-8" or "UTF-16". For parsed
JSON and non-ASCII raw text to have jq's intended character values, the producer
must supply UTF-8 bytes; neither native jq nor `dotnetjq` auto-detects UTF-16 or
a legacy code page. The `-b` option selects newline/`CTRL+Z` translation in
native Windows jq; it does not select a character encoding.

Thus ordinary Windows usage is straightforward: direct Unicode arguments work
without an encoding switch, and a PowerShell 7 string pipeline is UTF-8 by
default. Configure Windows PowerShell 5.1 explicitly before piping non-ASCII
strings. Its initial UTF-8 BOM is accepted by jq's parsed-JSON input mode but is
data, U+FEFF, in raw input mode. For exact bytes (especially `--raw-output0`,
arbitrary raw input, or a binary native-to-native pipeline), use `BaseStream` as
the checked-in test does, or a byte-preserving PowerShell 7.4+ native pipeline.
Capturing output as a PowerShell string necessarily asks PowerShell to decode
the UTF-8 bytes and is not a byte-for-byte observation.

The checked-in [`Verify-DotNetJq.ps1`](../tests/powershell/Verify-DotNetJq.ps1)
sets `$OutputEncoding` and `[Console]::OutputEncoding` with a BOM-less UTF-8
encoding object before testing PowerShell 7 and Windows PowerShell 5.1. Those
settings cover opposite directions of a text pipeline; neither setting
participates in direct argument transport. Its raw-slurp probe asserts the
shell-owned UTF-8 BOM from Windows PowerShell 5.1 and the absence of one from
PowerShell 7, together with both shells' CRLF object framing. The test separately
uses `StandardInput.BaseStream` and `StandardOutput.BaseStream` to verify exact
UTF-8, LF, and NUL bytes without a PowerShell text conversion.

## Test coverage and its boundary

The Windows CI jobs in [`.github/workflows/cli-cross-platform.yml`](../.github/workflows/cli-cross-platform.yml)
are configured to exercise the framework-dependent CLI under both PowerShell versions and the
NativeAOT CLI on Windows x64 and ARM64. Release CI repeats the PowerShell checks
for packaged executables. After the complete ten-package release bundle has
passed its structural and byte-identity gates, a downstream matrix downloads
that exact artifact on native Windows x64 and ARM64 runners. It installs the
`dotnetjq` selector through a local-only NuGet configuration and fresh cache,
explicitly selects the matrix architecture, requires the expected RID package
and installed native command shim, and runs `Verify-DotNetJq.ps1` under both
PowerShell 7 and Windows PowerShell 5.1. Either shell being absent is a release
failure rather than a skipped branch. The checked-in host-RID fixtures also
cover ARM64 Windows when x64-emulated Git Bash reports `x86_64` from `uname`.

Those checks intentionally assert the `dotnetjq` contract:

- PowerShell 7's default BOM-less UTF-8 native-input encoding, independently
  from the explicit encoding used for the shared PowerShell 5.1/7 corpus;
- UTF-8 pipeline input after explicit shell configuration;
- PowerShell-owned UTF-8 preamble policy and CRLF framing of multiple string
  pipeline objects;
- Unicode and space-bearing arguments, leading-dash positional arguments, and
  Unicode file paths;
- byte-exact LF from `-b` output;
- byte-exact UTF-8/LF diagnostics and arbitrary raw stderr payloads;
- JSON-sequence framing/recovery and NUL framing from `--raw-output0`;
- Windows case-insensitive environment-name lookup and jq's exact
  `HOME`/`USERPROFILE`/`HOMEDRIVE`+`HOMEPATH` startup-library precedence;
- jq-compatible process statuses.

The framework-dependent Linux, Windows, and macOS matrix and every supported
NativeAOT host are configured to run the CLI process suite with `DOTNETJQ_CLI` targeting the
executable under test. The PowerShell byte probes drain stdout and stderr
concurrently and use a bounded process wait so a failed implementation cannot
deadlock the Windows job.

The process corpus also deliberately expects CR preservation in raw input and
LF output on every OS. Linux and macOS use their pinned official jq executable
directly. Windows uses the pinned official `jq.exe` with `-b` automatically
prefixed, matching the product's binary standard-stream boundary. Named-file
CRLF/`CTRL+Z` behavior cannot be made binary by jq's `-b`; that intentional
extension is therefore covered by direct subject tests and is not mislabeled as
a default-`jq.exe` differential pass.

Interactive console rendering is less exact than redirected-stream behavior.
Before enabling automatic color, `Program.cs` now mirrors jq's Windows console
gate: it requires `GetConsoleMode` and either an `ANSICON` environment entry or
a successful `SetConsoleMode(...ENABLE_VIRTUAL_TERMINAL_PROCESSING)` call.
`-C` remains an explicit override and `-M` remains final. Native jq has the
separate `WriteFile` tty branch noted above, while `dotnetjq` writes to the .NET
standard stream, so final rendering still depends on the attached console host
and its active encoding. The current Windows checks cover native-process pipes
and redirected byte streams, not a legacy console screen-buffer byte oracle.

## Feasibility and recommendation

Faithful emulation is technically feasible without changing the managed jq
core. A Windows-only CLI adapter could:

1. translate CRLF to LF and honor `CTRL+Z` on default stdin;
2. expand every LF to CRLF on default stdout/stderr;
3. disable those standard-stream transforms after `-b`;
4. independently apply text input translation to every named file, even with
   `-b`;
5. preserve transform state across buffer boundaries and use translated byte
   positions for parser diagnostics.

Calling CRT `_setmode` from .NET is not a sufficient implementation: the CLI
performs I/O through .NET streams and OS handles, not through the CRT `FILE*`
objects whose descriptors `_setmode` changes. Explicit transforming stream
wrappers would be testable in framework-dependent and NativeAOT builds.

The retained product decision is to keep the current always-binary contract after
documenting its difference from native Windows jq's default. It is
deterministic across Windows and Unix, matches the mode upstream recommends to
Unix-oriented Windows shells, and is safer for NUL framing and byte-processing
pipelines. Emulating default `jq.exe` text mode would add a Windows-only
surprise, make named files behave differently from stdin, and break the current
cross-platform byte tests. If exact default-`jq.exe` emulation later becomes a
release requirement, it should be introduced as an explicit compatibility
policy with dedicated Windows byte-oracle tests, rather than by silently
changing `-b` or relying on the host shell.

Accordingly, Windows differential byte rows deliberately run the native oracle with
`-b`. They qualify this always-binary product policy only and must never be cited as
evidence of byte identity with native Windows jq's no-`-b` default.
