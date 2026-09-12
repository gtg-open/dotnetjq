# DotNetJq compatibility runner

This .NET 10 reporting tool reads the unchanged upstream jq `.test` group
format and runs selected cases through the managed `JqProgram` API. It does
not invoke or depend on a production jq executable.

By default it reads the pinned jq 1.8.2 fixture at
`upstream/jq/tests/jq.test` (override the checkout with `DOTNETJQ_UPSTREAM`):

```bash
dotnet run --project tools/DotNetJq.CompatibilityRunner
```

Select a fixture and a zero-based case slice with either a positional path or
the explicit options:

```bash
dotnet run --project tools/DotNetJq.CompatibilityRunner -- \
  --fixture upstream/jq/tests/jq.test --skip 100 --take 25

dotnet run --project tools/DotNetJq.CompatibilityRunner -- \
  upstream/jq/tests/jq.test --skip=100 --take=25
```

The runner prints only per-case failures followed by a deterministic `TOTAL`
line. It verifies compilation and runtime outcomes, output count and order,
and `JsonElement.DeepEquals` value equality. `%%FAIL` diagnostics are compared
with jq_test.c's last-error-callback semantics when the fixture requests
message checking; `%%FAIL IGNORE MSG` checks only the
failure outcome. The jq fixture's `# Runtime error:` output marker is treated
as an expected runtime failure, distinct from a successful empty output stream.
Execution is pulled one result at a time like upstream `src/jq_test.c`: after
all expected values match, one terminal invalid result is accepted whether it
represents ordinary exhaustion or carries an error. An error before all
expected values have arrived remains a failure, and public materialized
`JqProgram.Execute` continues to throw on every runtime error.
For upstream module cases, compilation receives a physical module resolver
confined to the fixture's `modules` subdirectory, matching jq's test launcher
without granting access to the rest of the filesystem.

Fixture evaluation is isolated from the report process. The fast path uses one
worker for the selected range and flushes a start/result protocol around every
case. If an uncatchable CLR failure such as a stack overflow or process-killing
out-of-memory condition terminates that worker, the report records the active
case with `phase=process-crash`, restarts at the following case, and still emits
the final `TOTAL` line. This provides per-case crash containment without paying
the startup cost of one process per successful case.

The containment path has a deterministic destructive-process self-test. It
sets a test-only environment variable that makes the worker terminate during
the middle of a three-case fixture, then verifies that the final case ran and
the totals were printed:

```bash
tools/DotNetJq.CompatibilityRunner/verify-isolation.sh
```

Exit code `0` means every selected case passed, `1` means compatibility
failures were reported, and `2` means the command line or fixture was invalid.
The tool is intentionally a reporter: it has no exclusions, expected-failure
allowlist, or mechanism that converts compatibility failures into passes.
