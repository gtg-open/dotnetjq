# jq 1.8.2 auxiliary-suite baseline

> **Historical baseline only — not current verification evidence.** This file
> preserves an intermediate 2026-09-04 diagnostic snapshot from before later
> compatibility work. Its hashes, pass counts, failures, and recommended fixes
> must not be cited as the current project status; use the latest release-gate
> results recorded in `PORTING_STATUS.md` instead.

Measured at 2026-09-04T23:50:05Z against the unchanged fixtures from jq
1.8.2 commit `34f7186b86743a083a589741b6cea95293524108`.

This was an exclusion-free execution through the managed compatibility runner.
The runner did not invoke the jq executable. Because other porting streams were
building concurrently in the uncommitted working tree, this measurement is
anchored to the exact assemblies that were unchanged before and after the run:

```text
DotNetJq.dll
  sha256 0da4db444d09add9b28d02c0531dee8aa2ec75e4c7cb64e08589971d3a6f0032
DotNetJq.CompatibilityRunner.dll
  sha256 b3151527a5216c8ce727aa0c207fb3e96922dc4c155b8b0d022abebce496c1c7
```

## Results

| Fixture | Cases | Passed | Failed | Pass rate |
| --- | ---: | ---: | ---: | ---: |
| `base64.test` | 10 | 10 | 0 | 100.00% |
| `uri.test` | 20 | 20 | 0 | 100.00% |
| `optional.test` | 2 | 2 | 0 | 100.00% |
| `onig.test` | 47 | 46 | 1 | 97.87% |
| `manonig.test` | 19 | 18 | 1 | 94.74% |
| `man.test` | 231 | 217 | 14 | 93.94% |
| **Combined** | **329** | **313** | **16** | **95.14%** |

Failure phases across all six fixtures:

| Phase | Count |
| --- | ---: |
| Runtime outcome | 13 |
| Output mismatch | 3 |
| Compile outcome or message | 0 |
| Process crash | 0 |

Compared with the previously observed snapshot, `optional.test` improved from
0/2 to 2/2 and `man.test` improved from 197/231 to 217/231. The combined result
therefore improved by 22 cases, from 291/329 to 313/329. The other four suite
counts were unchanged.

## Failure triage

The 16 failures form four disjoint implementation clusters.

| Cluster | Count | Exact auxiliary cases | Observed behavior | `jq.test` overlap |
| --- | ---: | --- | --- | --- |
| Embedded `builtin.jq` definitions are not linked into the program scope | 11 | `man.test` 144-145, 156, 158-161, 223-225; `manonig.test` 2 | `combinations/0`, `combinations/1`, `recurse/0`, `recurse/1`, `recurse/2`, `walk/1`, `truncate_stream/1`, and `fromstream/1` are reported undefined. The `repeat/1` undefined error is suppressed by postfix `?`, producing `[]` instead of `[2]`. All of these definitions exist in the unchanged `Resources/builtin.jq`; the gap is binding/linking, not a missing resource file. | Partial. Four `walk` cases occur at `jq.test` lines 2416-2430 and exercise the same unresolved `walk/1`. The other named definitions do not occur in `jq.test`. |
| Compile-time and execution-context intrinsics | 3 | `man.test` 82, 162-163 | `$__loc__` is undefined instead of yielding top-level source metadata; `$ENV` and `env/0` are undefined instead of reading the explicitly supplied execution environment. | Direct for location: the `man.test` program is duplicated at `jq.test` line 1499, and a focused runner probe also failed there with the same output mismatch; `jq.test` additionally uses `$__loc__` at line 2304. There is no `ENV`/`env` fixture coverage in `jq.test`. |
| Destructuring-alternative error/backtracking semantics | 1 | `man.test` 204 | `.[] as [$a] ?// [$b] | ...` commits the first binding and leaks `err: 3`; jq backtracks to the second binding and produces `{"a":null,"b":3}`. | Semantic but not exact. `jq.test` lines 938-1044 broadly cover `?//` binding selection, while this auxiliary case uniquely exercises rollback after the first alternative's continuation raises an error. |
| Multi-result `gsub` replacement enumeration | 1 | `onig.test` 43 | For two matches and a three-result replacement filter, managed execution emits a nine-value Cartesian product. jq emits three values (`AB`, `ab`, `cc`), reusing each selected replacement-filter result across the substitution operation. | None. `jq.test` contains no `gsub` or `sub` cases; this behavior is covered only by the Oniguruma auxiliary fixture. |

### Exact failing programs and results

`onig.test` case 43, line 191:

```jq
[gsub("(?<a>.)"; "\(.a|ascii_upcase)", "\(.a|ascii_downcase)", "c")]
```

Expected `["AB","ab","cc"]`; actual
`["AB","Ab","Ac","aB","ab","ac","cB","cb","cc"]`.

`manonig.test` case 2, line 5:

```jq
walk(if type == "object" then with_entries(.key |= sub("^_+"; "")) else . end)
```

Execution failed with `walk/1 is not defined`.

`man.test` failures:

| Cases | Programs or feature | Managed result |
| --- | --- | --- |
| 82 | `try error("\($__loc__)") catch .` | `"__loc__ is not defined"` rather than the top-level location object rendered as a string |
| 144-145 | `combinations`, `combinations(2)` | Corresponding arities are undefined |
| 156 | `[repeat(.*2, error)?]` | `[]` rather than `[2]` |
| 158-160 | Three `recurse` arities | Corresponding arities are undefined |
| 161 | `walk(...)` | `walk/1` is undefined |
| 162-163 | `$ENV.PAGER`, `env.PAGER` | `ENV` and `env/0` are undefined |
| 204 | Destructuring alternative with `?//` | Uncaught `err: 3` rather than fallback binding output |
| 223-225 | `truncate_stream`, `fromstream`, `tostream` | Streaming utility definitions are undefined; `fromstream/1` is the first surfaced error in the latter two cases |

## Recommended closure order

1. Compile and bind the unchanged embedded `builtin.jq` definitions. This is
   the single highest-leverage auxiliary fix and accounts for 11 failures.
2. Add `$__loc__`, `$ENV`, and `env/0` through explicit managed compile/execution
   context. Do not introduce ambient shell execution.
3. Correct the transactional error boundary for destructuring alternatives.
4. Make `gsub` enumerate replacement-filter outputs at operation scope rather
   than independently at every match.

After each cluster, rerun its focused auxiliary fixture and then the complete
`jq.test` fixture. The final project gate should rerun all 329 auxiliary cases
after concurrent source streams have stopped.

## Reproduction

Each suite was run with the corresponding command below (substituting the
fixture name):

```text
dotnet tools/DotNetJq.CompatibilityRunner/bin/Debug/net10.0/DotNetJq.CompatibilityRunner.dll \
  --fixture upstream/jq/tests/man.test
```

No exclusions, skips, or expected-failure allowlist were applied.
