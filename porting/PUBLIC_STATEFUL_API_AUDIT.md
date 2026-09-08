# Stateful public API implementation audit

Audit date: 2026-09-05. Semantic source: jq 1.8.2 commit
`34f7186b86743a083a589741b6cea95293524108`.

## Result

The formerly proposed stateful libjq boundary is implemented. DotNetJq now has an
explicit, per-execution input source, raw debug and standard-error value sinks, input
positions, immutable compilation-origin attributes, jq-shaped halt state, and a
pull-based public result stream. None of these features consults ambient standard
input, standard error, current directory, executable path, or home directory. Default
`$ENV` snapshots the process environment at compilation and `env` reads it at each
builtin invocation, matching jq. An explicit execution environment replaces both
views without process-global mutation.

The implementation is the managed direct-bytecode port of the `jq_state` execution
lifecycle; managed objects and delegates are not a C ABI assertion. State APIs live behind jq-shaped
`jq_init`, `jq_start`, `jq_next`, attribute, halt, and teardown entry points in
`src/DotNetJq/Port/src/jq.h.cs`; the public types are in
`src/DotNetJq/Public/JqStatefulExecution.cs` and `JqProgram.cs`.

## Public surface

| Capability | Public shape | Implemented contract |
| --- | --- | --- |
| Additional input | `IJqInputSource.ReadNext(CancellationToken)` returns `JqInputReadResult` | Each pull returns a cloned JSON value, a catchable jq error value, or end-of-input. There is no implicit stdin source. When `MaxInputBytes` is set, one aggregate UTF-8 budget covers the primary input and every value or error JSON payload returned by this capability. |
| Input position | `JqInputPosition`, `JqExecutionInput`, and the optional position on `JqInputReadResult` | The primary value establishes the initial position; a value or error from `input` replaces it; EOF leaves the last position unchanged. Negative line values are rejected. |
| Debug and stderr | `IJqValueSink` through `JqExecutionCapabilities.Debug` and `.StandardError` | Sinks receive stable, undecorated `JsonElement` values synchronously and in evaluation order. Missing sinks are no-ops; no `Console.Error` fallback exists. |
| Pull execution | `JqProgram.StartExecution(...)` and `JqExecution.TryRead(...)` | Results remain lazy. `Outcome` stays null until the first natural terminal pull, then records completed, halted, or runtime-error state. Early disposal tears down the iterator without advancing deferred continuations and intentionally leaves `Outcome` null. One program owns one jq state and permits one active execution; sequential starts reset and reuse that state. Disposing the program during an active pull prevents new starts but defers state teardown so the leased cursor remains usable. |
| Materialized execution | `ExecuteDetailed(...)` returns `JqExecutionResult` | Values emitted before completion, halt, or error are preserved together with the exact terminal outcome. |
| Compilation attributes | `JqCompilationOptions.ModuleResolver` and `.ProgramOrigin` | Library paths, jq origin, and program origin are copied at compilation and installed as jq attributes for every independent execution. |

Host capability failures are not converted into catchable jq errors. The original
exception from an input source or sink crosses the public cursor boundary with its
identity preserved. This keeps host failures distinct from a source deliberately
returning `JqInputReadResult.FromError(...)`.

The `dotnetjq` executable retains this public capability surface but does not use it as
an internal serialization transport. Matching jq `main.c`/`util.c`, its primary-input
loop and `input`/`inputs` share one `CliInputReader` registered through the jq-shaped
owned-`jv` callback. This preserves parser owners and positions without a
`jv`-to-`JsonElement`-to-`jv` round trip. It does not change the public API's cloned
`JsonElement` values or `MaxInputBytes` accounting contract.

At the direct boundary, `jq_next` follows native ownership and returns an owned
invalid-with-message for an uncaught jq error. `JqExecution.TryRead` consumes that
wrapper and message into `RuntimeError`; a message-less invalid remains completion or
the separate halted-state sentinel. Errors handled by jq `try` continue as ordinary
values and never reach this public terminal conversion.

## `input`, `inputs`, positions, and sinks

`input/0` synchronously pulls exactly one item from the explicit source:

- a value is emitted;
- an error result becomes an ordinary, catchable jq error carrying that JSON value;
- EOF or a missing source raises the catchable jq string error `"break"`.

The unchanged embedded `builtin.jq` definition of `inputs/0` lazily repeats `input`,
suppresses only the exact `"break"` sentinel, and stops reading immediately after any
other error. The source is not pre-buffered.

`MaxInputBytes` counts the compact JSON UTF-8 representation of the primary input and
then every caller-supplied value or error value pulled through `input`/`inputs`. Exact
aggregate-boundary use is accepted. If a pull would exceed the aggregate, it raises
`"jq input-byte limit exceeded"`; catching that jq error does not replenish the budget,
so later pulls continue to fail even when their individual values would fit.

`input_filename/0` returns the current explicit filename or JSON null.
`input_line_number/0` returns the current explicit number and otherwise raises the
exact catchable error `"Unknown input line number"`. A position attached to an input
error becomes visible inside the associated `catch` expression.

`debug/0` and `stderr/0` send the raw jq value to their corresponding sink and then
emit the input unchanged. The embedded source definition of `debug(msgs)` evaluates
messages in stream order, emits the original input only after clean completion, and
does not emit it after an error or halt in the message stream. Presentation as
`["DEBUG:", value]`, quoted JSON, newlines, or raw string bytes is CLI adapter policy
and is intentionally not performed by this library layer.

## Halt and terminal outcomes

`halt/0`, `halt_error/0`, and `halt_error/1` use a private non-catchable control signal
which is consumed only by the outer jq state. A valid halt therefore bypasses
`try/catch`, optional suppression, alternatives, recursion, and pending backtracking.
Values pulled before the halt remain observable; no later branch is advanced.

`JqExecutionOutcome` preserves all native-state distinctions:

- bare `halt` has `Kind == Halted`, no requested code, and no message;
- `halt_error(0)` has a present numeric zero and a present message which may itself
  be JSON null;
- non-integral jq numeric codes remain jq numbers and are not prematurely projected
  onto a process exit status;
- an invalid code argument is an ordinary catchable type error and does not set the
  halted state;
- an uncaught jq error is `RuntimeError`, not `Halted`.

Starting the next execution clears the previous halt, exit code, message, input
position, execution options, and capabilities while preserving the program's compiled
code and compilation attributes. A `JqProgram` owns this single jq-shaped state and is
therefore sequentially reusable, but rejects an overlapping start with
`InvalidOperationException`. Independently compiled programs own independent states
and may execute concurrently. Like native `jq_state`, a `JqProgram` and its active
`JqExecution` are not thread-safe; callers must not race their methods across threads.

`JqExecution` retains its owning `JqProgram` until the cursor completes or is disposed.
Disposing the program while a cursor is active marks the program disposed but defers
state teardown until that cursor releases it; the existing execution remains usable and
no later start is accepted. This ordering prevents finalization or public disposal from
freeing compiled values while `jq_next` or execution reset still references them.

The legacy `Execute` API retains values before a bare `halt` and returns them. For
`halt_error`, it throws `JqHaltException` carrying the requested jq number, optional
raw message, and partial outputs. `ExecuteDetailed` and the pull API expose the outcome
without applying CLI exit-code or message-formatting policy.

## Origins, search list, and `modulemeta`

`JqProgram.Compile(source, JqCompilationOptions)` snapshots these attributes:

| jq attribute/builtin | Explicit source | Default |
| --- | --- | --- |
| `JQ_LIBRARY_PATH` / `get_search_list` | `JqModuleResolver.LibraryPaths`, copied in order | `[]` |
| `JQ_ORIGIN` / `get_jq_origin` | `JqModuleResolver.JqOrigin` | absent, so the builtin emits an empty stream |
| `PROGRAM_ORIGIN` / `get_prog_origin` | `JqCompilationOptions.ProgramOrigin` | absent, so the builtin emits an empty stream |

`ProgramOrigin` also anchors relative search metadata on imports in the top-level
program. No current-directory value is inferred. `$ORIGIN` and `~/` expansion use the
resolver's explicit jq origin and home directory.

`modulemeta/0` is in the public builtin inventory. It requires a string input and emits
the exact catchable error `"modulemeta input module name must be a string"` otherwise.
It searches only the explicit jq library paths, not the program origin, and returns
declared metadata plus ordered `deps` and exported `defs`. Each lookup uses fresh
linker accounting while retaining the resolver's filesystem and resource policy, so
independently compiled programs can be executed concurrently without mutable metadata
state leaking between them. Sequential executions of one program reinstall the same
immutable compilation attributes on its reused state.

## Non-ambient capability boundary

Plain `Compile` plus default execution has:

- no module filesystem capability;
- no additional input source;
- no debug or stderr sink;
- no input filename or line number;
- an empty search list and absent jq/program origins;
- jq's native ambient environment timing: `$ENV` is the compile-time snapshot and
  each `env` invocation enumerates the then-current process environment. An explicit
  `Environment` option replaces both for that execution without mutating process state.

Supplying entries named `JQ_LIBRARY_PATH`, `JQ_ORIGIN`, or `PROGRAM_ORIGIN` through
`JqExecutionOptions.Environment` does not create the corresponding compilation
attributes. Likewise, halt messages are retained as state and are never written to the
stderr sink automatically.

## Verification evidence

The dedicated stateful classes contain 40 deterministic xUnit cases: 15 in
`StatefulIoCompatibilityTests`, 17 in `HaltCompatibilityTests` (the first theory has
four inputs), and 8 in `StatefulOriginMetadataCompatibilityTests`. They cover:

- missing-capability defaults, lazy input/inputs pulls, exact break handling, source
  errors, position replacement, EOF position retention, aggregate primary-plus-capability
  input-byte accounting, error-value accounting, and sticky exhaustion after a catch;
- raw debug/stderr order, `debug(msgs)` completion behavior, host-exception identity,
  rejection of overlapping same-program runs and concurrent capability isolation across
  independently compiled programs;
- non-catchable halt behavior, partial pull output, absent-versus-zero exit code,
  absent-versus-null message, reset, legacy API mapping, and runtime-error separation;
- immutable origin/search snapshots, top-level program-origin resolution, exact
  `modulemeta` errors/result order, builtin inventory, and sequential reuse.

Additional lifecycle evidence is in
`ExecutorContinuationCompatibilityTests.ErrorAfterOutputIsReportedOnlyAfterTheEarlierValueWasPulled`
and
`ExecutorContinuationCompatibilityTests.EarlyDisposalDoesNotAdvanceDeferredInputOrDebugContinuations`.
`JqProgramConcurrencyContractTests` additionally covers ordinary overlap
rejection, sequential reuse after completion/error/early disposal, deferred program
disposal, independent-program concurrency, and the ephemeral
`JqProgram.Compile(...).StartExecution(...)` lifetime.
The unchanged official jq fixtures and the repository's full compatibility gate remain
the broad semantic oracle; this audit does not replace those gates.

The focused resource-control inventory contains 37 cases: 13
`SecurityResourceCompatibilityTests`, 17 `PublicExecutionFacadeContractTests`, 3
`PublicDeepOutputCompatibilityTests`, and the 4 `StatefulIoCompatibilityTests` cases
dedicated to aggregate `MaxInputBytes` behavior.

## Residual boundary

This surface is synchronous. A blocking user implementation of `IJqInputSource`,
`IJqValueSink`, or `IJqFileSystem` cannot be forcibly interrupted by the library; the
host must bound such capabilities externally. Input and sink values are represented as
`JsonElement`, so jq values not representable as JSON are not a host-callback transport
type. These are host/API constraints, not hidden ambient fallbacks.
