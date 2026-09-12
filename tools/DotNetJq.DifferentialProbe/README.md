# DotNetJq differential probe

This .NET 10 tool builds a deterministic, seed-shuffled corpus balanced across
streams, generators, updates and paths, types and error boundaries, numbers and
numeric literals, strings, formats, objects/arrays, control/errors, parser and
compiler compositions, modules, dates, explicit stateful-capability boundaries,
regular expressions, and builtins. It compares outcome, output count, output
order, JSON value equality, and compile/runtime diagnostic payloads between
managed DotNetJq and the pinned official jq 1.8.2 binary. It has no exclusions,
tolerance, allowlist, or expected-failure list.

Failure diagnostics use a deliberately narrow stable comparison. The native
process executable/source-location envelope (`jq: error (at ...):`) and its
terminal `jq: N compile error(s)` summary (including its optional blank
separator) have no managed-library counterpart, so only those wrappers are
removed. The error payload, compile location, source excerpt, carets,
punctuation, rendered jq values, line structure, and ordering of multiple
compile diagnostics remain exact after CRLF/CR line endings are folded to LF.
An absent diagnostic never equals a non-empty one.

Run the default 5,040-case corpus from the repository root:

```bash
dotnet run --project tools/DotNetJq.DifferentialProbe
```

All 5,040 filter/input pairs are unique. The default covers 20 balanced
categories, all 437 declared category filters, and all 48 declared JSON inputs.
Corpus construction fails unless the full pinned `builtins/0` inventory
contains exactly 226 unique signatures and every signature has a meaningful
representative invocation in the generated cases. The inventory is re-read
from the pinned oracle at run time. Generation also fails if a category,
declared filter, or input is absent.

Every mismatch is printed and written to
`porting/DIFFERENTIAL_PROBE_REPORT.md` with its exact filter/input and a replay
command. Replay one case with the same corpus parameters:

```bash
dotnet run --project tools/DotNetJq.DifferentialProbe -- \
  --seed 18082 --count 5040 --case 42
```

Single-case replay does not write a report by default, so it cannot overwrite
the canonical full-corpus report. Pass `--report PATH` explicitly when a
single-case report is wanted.

Run the deterministic regex-focused corpus separately:

```bash
dotnet run --project tools/DotNetJq.DifferentialProbe -- --corpus regex
```

Its pinned seed `1808202` dynamically enumerates the exact distinct capacity of
the declared regex groups: 4,980 unique cases across captures, POSIX classes,
Unicode properties and case folding, zero-width advancement, lookarounds,
backreferences and capture levels, subcalls and conditionals, nested absent
ranges, `\G`, `\K`, class-context/control escapes, lookbehind-width reduction,
built-in and content callouts, options, empty alternatives, exact invalid-pattern
diagnostics, anchors, and replacement/split behavior. The generator rejects a
request for 4,981 cases, so a full-capacity 4,980-case run is exhaustive over the
declared corpus rather than a truncated sample. The no-allowlist results are written to
`porting/REGEX_DIFFERENTIAL_PROBE_REPORT.md` without replacing the general
corpus report.

That evidence qualifies the jq-visible regex filter surface. It does not claim
native Oniguruma API, bytecode, allocator, engine-architecture, or C ABI
identity.

The official oracle gets its own process and timeout per case. Managed
execution uses explicit time, recursion, execution-transition, input/output,
output-count, and regex limits. Exit code `0` means complete parity, `1` means
mismatches were reported, and `2` means the invocation is invalid.
