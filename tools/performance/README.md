# CLI performance comparison

This harness compares three real command-line executables:

- the framework-dependent .NET 10 `dotnetjq` apphost;
- the Linux x64 NativeAOT `dotnetjq` executable;
- the native jq 1.8.2 oracle pinned at commit
  `34f7186b86743a083a589741b6cea95293524108` and executable SHA-256
  `b1c22172dd303f3be49e935aa56aa48a8b7a46e0bc838b4997d3bb451495870f`.

It parses the same seven unchanged upstream `.test` fixtures as the compatibility
suite: 879 independent filter/input scenarios. Every measurement starts a new
process and therefore includes startup, filter compilation, evaluation, output
serialization, and shutdown. Scenario order is deterministically shuffled. A
stable scenario-index/repetition Latin rotation, independent of that shuffle,
places each implementation first, second, and third exactly once per scenario
when three repetitions are requested.

Before timing, native jq must pass every selected complete fixture using its
official `--run-tests` mode. The harness then invokes all three CLIs once for every
selected scenario. Exit status, stdout bytes, and stderr bytes must match native
jq exactly for successful, compile-failure, and runtime-failure scenarios. Every
timed exit status is checked again. A mismatch stops the run; performance numbers
are never produced for a CLI-observable divergence.

## Correctness-only release gate

Run the complete process-level compatibility preflight without collecting timing
samples or preparing, replacing, or publishing a performance report:

```sh
env -u LD_PRELOAD tools/performance/run.sh \
  --correctness-only \
  --native-executable /path/to/pinned/oracle/jq \
  --upstream upstream/jq
```

The runner still holds the repository-wide performance lease while it creates
fresh isolated framework-dependent and NativeAOT publications and their build
attestations. It verifies the pinned source/oracle/toolchain/artifact identities,
the startup result, all seven official `--run-tests` results, and the exact exit
status, stdout bytes, and stderr bytes for all 879 scenarios. The timing benchmark
calls this same preflight function; there is no second correctness
implementation. Correctness-only then performs the normal postflight identity
checks and exits without warmups, timed repetitions, aggregation, or report
writes. An already completed fixture or macro report remains byte-for-byte
untouched.

`--fixture`, `--limit`, `--output-directory`, `--repetitions`, `--warmups`,
`--startup-repetitions`, `--seed`, `--publishable`, and `--non-publishable` are
rejected with `--correctness-only`. `--timeout-seconds`, `--native-executable`,
and `--upstream` remain applicable. `--skip-build` may be used only when the
default framework and NativeAOT artifacts already carry valid attestations for
the current source and toolchain; release verification always performs the fresh
build.

## Full benchmark

From the repository root:

```sh
env -u LD_PRELOAD taskset -c 14 \
  tools/performance/run.sh --repetitions 3
```

`run.sh` first acquires the repository-wide exclusive performance-report lease,
then builds the Release framework-dependent executable and publishes the Linux
x64 NativeAOT executable before starting the benchmark. The lease is held from
report preparation through build, validation, all report writes, and final
certification, and is released by the broker even when the child pipeline fails.
Before either publish the runner safely resets the exact fixture-report leaf,
leaving an `incomplete` status marker bound to the lease's unique run ID, and
then captures a deterministic
source/toolchain snapshot and cleans the two
dedicated publication directories and separate
`artifacts/performance/build/{framework,aot}` intermediate trees, and afterwards
writes a cryptographic build attestation beside each executable. Each publish
uses its fresh tree through an exact `--artifacts-path`, so an incremental
`bin`/`obj` result cannot be relabeled as a current-source build. A publishable
full run refuses artifacts
unless those attestations match the exact publish arguments, every published
file, the pre-build source manifest, the .NET host and SDK, the selected CoreCLR
runtime, the NativeAOT compiler/runtime-pack metadata and extracted payloads,
and the candidate `clang`, `cc`, and `ld` binaries resolved from the build PATH.

Results are written under `artifacts/performance/results`:

- `.dotnetjq-report-status.json`: report kind, unique run ID, exact expected
  inventory, an `incomplete`/`complete` state, and (when complete) every report
  file's byte size and SHA-256 plus validated schema/cardinality totals;
- `results.json`: metadata, commands, fixture hashes, every scenario, and every
  nanosecond sample;
- `measurements.csv`: one row per raw measurement;
- `scenario-summary.csv`: one aggregate row per scenario and implementation;
- `summary.csv`: overall and per-fixture aggregates;
- `summary.md`: the human-readable comparison table.

The output directory is emptied before prerequisite checks, publication, or
benchmark execution, so a failed rerun cannot leave an older report looking
current. Concurrent fixture/macro/report runs are refused by the same exclusive
lease, including when they target different report leaves. An existing report
leaf is atomically renamed out of the canonical
location before the new incomplete leaf is created, then removed; even a cleanup
failure cannot leave its old `results.json` at the current path. The status
changes atomically to `complete` only after all five report files exist as
regular files, there are no extra entries, and the JSON/CSV schemas agree on the
scenario, implementation, repetition, raw-measurement, summary, and publication
capacities. Reading a complete marker re-hashes and revalidates the files, so a
modified, mixed, truncated, or placeholder report is rejected. The default report
leaf is the only report path that may be cleaned inside the repository; source,
build, deployment, and macro-data directories are refused. A custom report path
must be outside the repository. If it already contains files, it must carry a
valid marker from an earlier run; an arbitrary nonempty directory is never
adopted or deleted. Symbolic report leaves, broad/protected paths, files, and
mount points are refused.

The default is three repetitions. More repetitions improve stability but cost
approximately linearly because a three-repetition full run performs 8,001 timed
process launches after the correctness pass: 7,911 fixture launches plus 90
startup launches. Replace CPU `14` with an otherwise idle CPU suitable for the
host; the selected affinity is recorded in the report.

## Configuration and smoke runs

```sh
tools/performance/run.sh \
  --repetitions 5 \
  --warmups 1 \
  --startup-repetitions 30 \
  --framework-executable /path/to/framework/dotnetjq \
  --aot-executable /path/to/aot/dotnetjq \
  --native-executable /path/to/pinned/oracle/jq \
  --upstream /path/to/pinned/jq-checkout \
  --output-directory /path/to/results

tools/performance/run.sh --skip-build \
  --fixture optional.test --limit 2 --repetitions 1 \
  --output-directory /tmp/dotnetjq-performance-smoke
```

`--fixture` may be repeated. `--limit` intentionally exists only for harness
development/smoke tests; omit both options for the complete 879-scenario report.
`--skip-build` and limited selections are explicitly non-publishable and must
write to a non-default output directory, so they cannot overwrite the default
publishable report. Running `benchmark.py` directly follows the same rule; its
`--publishable`/`--non-publishable` switches are normally managed by `run.sh`.
The pinned upstream checkout must have the exact configured `HEAD` and a clean
working tree. The native executable's SHA-256 is a hard gate, not merely report
metadata. The captured correctness pass is followed by the requested number of
untimed warmup passes. Timed stdout and stderr are sent to the null device so
output-heavy filters measure the CLI rather than Python pipe capture. The
benchmark fixes `TZ=UTC`, the C locale, `PAGER=less`, and an empty temporary home.
It removes inherited `DOTNET_*` performance switches (while preserving and
recording runtime-root locations), `COMPlus_*`, `CORECLR_*`, `COREHOST_*`,
`JQ_*`, `LD_*`, and `MALLOC_*` controls before applying its documented
environment.

Correctness and warmup invocations retain the configured safety timeout. Timed
invocations use a blocking process wait after those gates; using Python's POSIX
timeout wait would add polling sleeps to short process measurements.

The machine-readable report records the exact app artifacts, source-input
fingerprint, repository state, `global.json`, .NET host/SHA/SDK/runtime inventory,
harness hash, environment policy, CPU affinity, and raw samples. It re-hashes
every behavior-defining artifact, source input, upstream fixture/module file,
runtime identity, isolated intermediate tree, extracted NativeAOT package
payload, host compiler/linker candidate, and attestation after timing; it does
not infer freshness from one maximum modification time. Run without
`--skip-build` for publishable current-source results.

Nearest-rank p95 is emitted only for groups with at least 20 samples. With the
default three repetitions, per-scenario reports use median and min-max; p95 is
`null`/`n/a`, rather than relabeling the maximum as a meaningful tail percentile.

## Macro workloads

Run all 21 deterministic scenarios (one startup plus 20 processing workloads)
against the built executables:

```sh
env -u LD_PRELOAD taskset -c 14 \
  python3 tools/performance/macro_benchmark.py \
  --framework-executable artifacts/performance/framework/DotNetJq.Cli \
  --aot-executable artifacts/performance/native-aot/DotNetJq.Cli \
  --native-executable /path/to/pinned/oracle/jq \
  --upstream upstream/jq \
  --scale 0.1 --warmups 1 --repetitions 3 --regenerate --publishable
```

The macro runner defaults to scale `0.1`; the generator's full-size reference
scale is `1`, and `0.01` is intended for smoke tests. It verifies or regenerates
the deterministic datasets, checks exact stdout/stderr/status equality against
native jq, and writes JSON, raw CSV, summary CSV, and Markdown reports under
`artifacts/performance/macro-results`. It applies the same early-clean,
symlink/broad-path refusal, incomplete marker, and exact-inventory completion
contract as the fixture runner. Its complete inventory is the status marker,
`results.json`, `raw.csv`, `summary.csv`, and `summary.md`.

Macro summaries publish separate `PROCESSING_ONLY` and `WITH_STARTUP` aggregates,
so framework startup does not silently distort the processing geomean. The
per-scenario table reports median and min-max at the default repetition count.

The full 21-scenario default report is publishable and therefore requires both
`--regenerate` (so the current generator creates the measured bytes) and the same
two valid attestations produced by `run.sh`. Selective macro smoke runs must use
`--non-publishable` and a non-default `--output-directory`. The macro runner
also validates the explicit/default upstream checkout at the pinned commit with
a clean status, then revalidates that checkout plus all dataset, manifest,
fixture, and module hashes after timing.

## Fast harness checks

These deterministic tests launch no benchmark CLI and do not regenerate reports:

```sh
env -u LD_PRELOAD python3 -B tools/performance/test_harness.py \
  --require-pinned-upstream
```

They verify the 879/21 capacities, exact correctness comparison, pinned-oracle
rejection, per-scenario Latin rotation, percentile threshold, environment
sanitization, distinct deployment/build-configuration gates, attestation
tamper/freshness rejection, non-publishable output isolation, dataset identity,
exclusive/inherited lease behavior, run-ID and hash binding, schema/cardinality
validation, report-output cleanup/refusal/completion, and separated macro
aggregates. The strict flag makes a missing checkout at
`DOTNETJQ_PERFORMANCE_UPSTREAM` (default `upstream/jq`) a failed test;
`run.sh` always supplies it. Omit the flag only for a portable helper-unit run,
where the two checkout-dependent identity/capacity tests are visibly reported
as skipped and all remaining tests still run.

This is deliberately a process-level CLI benchmark. It should not be interpreted
as an in-process evaluator microbenchmark: process startup is a material part of
the framework-dependent result and one reason NativeAOT is useful for a CLI.
