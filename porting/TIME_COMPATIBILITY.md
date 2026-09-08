# Time compatibility boundary

## Source and oracle

jq 1.8.2 delegates its time builtins to the host C library. The managed port
therefore treats both jq and the pinned Linux C-library behavior as source:

- jq commit `34f7186b86743a083a589741b6cea95293524108`,
  `src/builtin.c:1490-1927` (`tm2jv`, `jv2tm`, `strptime`, `strftime`,
  `mktime`, `gmtime`, `localtime`, and `now`);
- glibc 2.39 commit `ef321e23c20eebc6d6fb4044425c00e6df27b05f`,
  especially `time/strptime_l.c`, `time/strftime_l.c`, `time/tzfile.c`,
  `time/tzset.c`, `time/mktime.c`, `time/mktime-internal.h`, and
  `time/timegm.c`;
- the statically linked jq-1.8.2 Linux oracle at verification time, SHA-256
  `b1c22172dd303f3be49e935aa56aa48a8b7a46e0bc838b4997d3bb451495870f`.

The oracle is test input only. Neither the library nor CLI launches jq, loads a
native time library, mutates process-global `TZ`, or depends on either source
checkout at build or runtime.

## Managed implementation

`Port/src/builtin.c.cs` retains jq's call shape, operand checks, errors,
eight-field array conversion, signed integer behavior, and the special `-1`
and `-2` `mktime` results. It implements civil-date conversion outside
`DateTime`'s year range and preserves jq's fractional-second rule for negative
epochs. `now` follows jq's `gettimeofday` path by truncating to microseconds.

`Compatibility/Time/JqStrptimeRegex.cs` retains its historical filename for
traceability but contains no regular expression. It is an AOT-safe scanner for
glibc's C-locale grammar and raw `struct tm` state. That distinction matters:
`strptime` does not validate a complete calendar date, does not normalize the
result, leaves unassigned fields at jq's observable sentinels, accepts a
remaining suffix only when its first byte is C whitespace, and treats bytes
after the first NUL as invisible.

`Compatibility/Time/JqTimeZone.cs` reads public TZif v1-v4 files and evaluates
their POSIX footer rules. It also parses POSIX `TZ` strings directly and, when
such a string supplies DST names but omits transition rules, applies the
installed `posixrules` TZif transition history as glibc does. This keeps IANA
offsets, abbreviations, invalid-zone behavior, DST gaps/folds, and far epochs
available under NativeAOT without reflecting over private `TimeZoneInfo`
fields. Its `mktime` inversion is the glibc six-probe `t`/`t1`/`t2` algorithm,
including oscillation selection, explicit `tm_isdst` correction/failure,
the cached offset guess, and the rule that clamps a raw `tm_sec` for probing
and applies it only after the guess is updated. A gap is not inherently moved
forward: Dublin/Casablanca negative-DST gaps and fresh Apia/Kwajalein date-line
gaps move backward, while prior calls can select the other side. Unspecified
DST folds are likewise call-order dependent; a fresh explicit-environment context starts
with the same zero-second guess as a fresh native process, while ordinary default execution
retains the process-static guess across states and executions. POSIX offsets/rules retain
glibc's `%hu` token consumption/narrowing, partially mutated malformed-rule
state where subsequent native evaluation remains defined, UTC-year selection,
Zeller/C integer arithmetic, and far-year wrapping.

The formatter preserves glibc flags, widths, `E`/`O` acceptance, composite
directives, unknown specifiers, the `strlen(format)+100` output buffer, and the
otherwise surprising `%s` rule: `strftime` formats a UTC `struct tm`, but glibc
computes `%s` by interpreting that wall clock through the ambient local zone.
For `strflocaltime`, `%s` performs a second canonical `mktime`; this state
change is preserved because it can alter the next fold or date-line gap.

With no explicit `JqExecutionOptions.Environment`, each time builtin reads ambient
`TZ`, `LC_ALL`, `LC_TIME`, and `LANG` at its call boundary. All default jq states share
one guarded time-zone object and `mktime` offset guess, matching libc's process-static
call-order behavior across executions. The guard corresponds to libc's internal
serialization of mutable time-zone state.

Setting `JqExecutionOptions.Environment` is an opt-in managed sandbox extension. The
supplied dictionary is copied, selects those variables for the execution, and uses the
`JqProgram`'s bounded per-state time context so independently configured states do not
cross-contaminate one another. On Windows, these getenv-style selections are
case-insensitive like the CRT; materialized `$ENV`/`env` objects still preserve actual
key casing and jq's ordinary case-sensitive object lookup.

On Linux, a requested non-C locale is used only when it is present in glibc's
installed locale directory/archive. If it is absent, the C locale remains in
effect just as it does after jq's failed `setlocale` request; ICU knowledge by
itself is not treated as evidence that the native locale exists.

## Proven parity scope

`TimeBuiltinOracleMatrixTests` contains 125 passing test cases (including theory
rows) from the pinned oracle. Results independent of installed platform data are
frozen constants. Four Dublin/Casablanca gap rows, whose side-selection can change
with the host TZif encoding, instead compare exactly with the pinned jq executable
using that same installed TZif data; when the optional development oracle is absent,
they retain the original frozen glibc-2.39 baseline. The matrix covers:

- every C-locale `strftime` directive, flags, padding, case modifiers,
  `E`/`O`, explicit widths, unknown directives, and the exact buffer boundary;
- every accepted C-locale `strptime` directive, raw field sentinels, accepted
  modifiers, ranges, C whitespace, partial tails, failures, and embedded NUL;
- `mktime` normalization/truncation and reserved results, failed-normalization
  raw `struct tm` output, positive and negative fractional epochs, and both
  glibc `tm_year` limits including signed wrap;
- `fromdate`, `todate`, `gmtime`, `localtime`, IANA TZif zones, 27 POSIX-TZ
  grammar/default-rule-history rows, invalid/partial identifiers, `%hu`
  narrowing, UTC-year and signed/far-year rule arithmetic, `%z`'s two-stage
  flags/width behavior, `%Z`, `%E%`/`%O%`, the `%s` ambient-zone rule, and 22
  original call-order permutations around DST gaps/folds in Paris and New York;
- negative-DST and non-hour/date-line gaps in Dublin, Casablanca, Kathmandu,
  Apia, and Kwajalein; explicit-`tm_isdst` six-probe failure; raw-second
  post-adjustment; and state changes both with and without a `%s` directive;
- missing native-locale fallback, default cross-`jq_state` process-static `mktime`
  behavior, and deliberate isolation only for explicitly supplied environments;
- `now` type, wall-clock range, and microsecond quantization.

`tools/native-aot-smoke/verify.sh` additionally publishes a local `linux-x64`
native executable and executes the scanner, formatter, conversions, and POSIX
DST rule path. The nine-case smoke passes without trim warnings, reflection,
generator DLLs, or a native jq/time dependency.

## Explicit exclusions

Parity is intentionally not claimed for these platform-data or capability
surfaces:

- exact non-C locale era/alternate-digit tables and byte collation; for an
  installed locale the managed extension formats with .NET globalization data;
- leap-second-aware `right/*` TZif timelines;
- malformed POSIX `M` rules whose narrowed month is outside 1..12: glibc leaves
  the rule type partially mutated and then reads outside `__mon_yday` in
  `compute_change` (for example `M13...` or `M-3...`), which is undefined C
  behavior; the managed evaluator stays bounds-safe and does not reproduce the
  pinned binary's adjacent-memory accident;
- absolute `TZ` file paths and arbitrary host file reads;
- Windows-only/system zones for which no TZif data exists: valid system IDs are
  resolved before permissive POSIX parsing and the fallback uses public
  `TimeZoneInfo` names and its supported year range;
- extensions or behavioral differences of a host libc other than the pinned
  Linux/glibc-2.39 oracle, and native `struct tm`/locale/C ABI identity;
- interaction between glibc's one process-static TZ/mktime context and the managed
  extension's deliberately isolated explicit-environment contexts;
- differences caused solely by the host's installed TZif/tzdata build or version.

These exclusions are also present in `PORTING_MANIFEST.json`; none is silently
converted to a full cross-libc parity claim.
