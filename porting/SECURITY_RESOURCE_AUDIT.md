# Security and Resource-Limit Audit

Audit date: 2026-09-05

Scope: the managed `DotNetJq` library at jq-1.8.2 revision
`34f7186b86743a083a589741b6cea95293524108`, using .NET SDK 10.0.400. This is a
Definition-of-Done audit of the controls listed in sections 13.7 and 22 of
`dotnetjq-port-spec.md`; it is not a claim that untrusted execution is
fully sandboxed.

## Result

The public execution boundary exposes wall-clock timeout, input/output byte limits,
output-value limit, recursive call depth, a pre-dispatch VM transition budget
(`MaxExecutionTransitions`), regex timeout, cancellation, and an explicit environment
snapshot. `MaxGeneratedValues` is affirmatively absent because jq has no source-defined
generated-intermediate-value event. `MaxOutputValues` counts values yielded through the
public output boundary, while `MaxExecutionTransitions` counts VM instruction
transitions; the former implementation incorrectly used the generated-values name for
opcode dispatches. The jq-1.8.2
structural limits for JSON parse, path, containment, equality,
comparison, and recursive merge are also implemented and regression-tested. Explicit
module resolvers can additionally cap unique dependency files, aggregate imported bytes,
and dependency depth, and can carry a cooperative cancellation token. The byte-oriented
JSON parser is incremental and iterative through jq's 10,000-container limit, including
sequence and streaming modes.

The transition budget, explicit environment snapshot, module graph budgets, resolver
cancellation, and optional per-file cap are specification-authorized, opt-in managed host
extensions. They are not upstream jq requirements, and their inactive defaults preserve
jq's ordinary execution and module behavior.

Production compilation and execution use one jq-shaped route. Generated parser
actions produce direct `block`/`inst` IR; linker and builtin binding operate on
that IR; `block_compile` and `optimize` produce state-owned `bytecode`; and
`jq_start`/`jq_next` run the direct frame/fork opcode VM. Allocated `jv` values
retain explicit `jv_copy`/`jv_free` ownership and logical reference counts across
compiler constants, VM stacks, frames, forks, and returned values.

The controls are useful, but they are cooperative rather than a hard process sandbox.
The main remaining risks are unbounded compilation, limits that are checked after a
large result has already been constructed or serialized, long individual opcodes or
builtins that do not return to the pre-dispatch policy check, and filesystem
time-of-check/time-of-use races. A hostile reducer still requires process/container
memory and CPU limits in the hosting layer.

## Verified controls

| Area | Status | Evidence | Boundary or qualification |
|---|---|---|---|
| Input bytes | Verified | `PublicExecutionFacadeContractTests.InputByteLimitCountsUtf8Bytes` and the four input-budget cases in `StatefulIoCompatibilityTests` | One aggregate UTF-8 budget covers the primary input plus every value or error JSON payload returned by the explicit input capability. A primary input is checked before parsing; capability payloads are checked before reparsing. Exact aggregate-boundary use is accepted, overflow-safe accounting is used, and exhaustion remains sticky when a jq handler catches the limit error. No limit applies unless the caller sets one. |
| Output bytes | Verified | `PublicExecutionFacadeContractTests.OutputByteLimitCountsCompactJsonUtf8Bytes`, `PublicDeepOutputCompatibilityTests`, `SecurityResourceCompatibilityTests.AggregateOutputByteLimitCountsTheWholeStream` | Aggregate compact-JSON bytes are counted with overflow-safe arithmetic. The value is fully generated and serialized before the check. |
| Output values | Verified | `PublicExecutionFacadeContractTests.OutputValueLimitAppliesToGeneratedStream`, `SecurityResourceCompatibilityTests.ZeroOutputValueLimitRejectsTheFirstResult` | The next value is evaluated before the public cursor rejects it. |
| `MaxExecutionTransitions` | Opt-in managed extension — verified | `SecurityResourceCompatibilityTests.ExecutionTransitionLimitStopsLargeRangeBeforeOutputExplosion` and `ExecutionTransitionBudgetContractTests` | `jq_bytecode_vm.TickExecutionPolicy()` charges once immediately before every forward or `ON_BACKTRACK` opcode dispatch. Stack exhaustion returns before the charge. This is a cooperative instruction-transition budget, not an upstream jq requirement and not a count of output values, intermediate values, bytes, or allocations. |
| Execution timeout | Verified, cooperative | `SecurityResourceCompatibilityTests.ZeroExecutionTimeoutStopsAtFirstCooperativeTick` | Starts after input parsing and is observed by the same pre-dispatch VM policy check. It does not interrupt compilation, module I/O, JSON parse, serialization, or a long operation within one opcode/builtin call. |
| Cancellation | Verified, cooperative | `PublicExecutionFacadeContractTests.CancellationIsObservedBeforeParsingInput`, `SecurityResourceCompatibilityTests.CancellationIsObservedDuringGeneratorConsumption`, `ModuleResourceLimitCompatibilityTests` | Execution checks before input parsing and before each VM opcode dispatch when policy checks are enabled. A module resolver checks its token before and after synchronous filesystem reads and between parse/link steps; it cannot interrupt a blocking `IJqFileSystem.ReadFile` already in progress. Cancellation is not passed into regex, parser, or serializer APIs. |
| Recursive evaluation | Verified | `BuiltinCompatibilityRound2Tests.RecursiveIteratorsHonorTheConfiguredDepthLimit`, `SecurityResourceCompatibilityTests.UserFunctionRecursionUsesConfiguredDepthLimit` | Covers user functions and recursive builtins. Parser recursion remains separate; module-linker recursion has its own explicit dependency-depth limit. |
| Regex timeout | Verified | `RegexCompatibilityRound2Tests.CatastrophicBacktrackingIsStoppedByConfiguredTimeout`, `SecurityResourceCompatibilityTests.GlobalZeroWidthRegexUsesOneCumulativeTimeoutBudget`, and `CalloutRunnerTextAtomsUseTheConfiguredRegexDeadline` | .NET regex has a per-match timeout; the adapter additionally applies one cumulative budget to global and jq `l` searches. Nested atom/text-segmentation regexes receive only the caller's remaining deadline rather than starting an independent timeout. Regex cancellation is via timeout, not `CancellationToken`. |
| Parser pathology | Verified for covered classes | `JsonProxyCompatibilityRound2Tests.MalformedInputFailsWithBoundedSafeError`, `ParserAcceptsJqMaximumContainerDepthWithoutManagedRecursion`, `ParserRejectsBeyondJqMaximumContainerDepth`, `SecurityResourceCompatibilityTests.LargeParserNestingFailsWithBoundedMemoryDiagnostic`, and the managed parser-stack boundary tests | JSON container parsing is iterative through jq's 10,000 boundary. The production GPPG parser uses a source-integrated bounded LR stack and reproduces jq's observable nested-parenthesis limit exactly: 9,994 levels are accepted and 9,995 report `jq: error: memory exhausted`. Compilation still has no source-byte or time limit. |
| Incremental/streaming JSON input | Verified | The 34 cases in `JvParserStreamingCompatibilityTests` | The byte-based `jv_parser_set_buf`/`jv_parser_next` proxy preserves partial buffers, remaining-byte accounting, split UTF-8 and escapes, adjacent roots, JSON text-sequence resynchronization, streaming path/end events, and stream-error values. It does not add a total input-byte, time, or cancellation policy of its own. |
| Deep path traversal | Verified | `ExecutionCompatibilityRound4Tests.TenThousandComponentSetPathFlattensWithoutClrRecursion` | 10,000 components are accepted and 10,001 produce catchable `Path too deep`. |
| Recursive containment | Verified | `JvAuxCompatibilityTests`, `DeepStructureCompatibilityRound3Tests.ContainsAcceptsJqMaximumDepthAndRejectsTheNextLevel` | Iterative traversal accepts 10,000 and rejects 10,001. Very wide array containment remains potentially quadratic. |
| Deep equality/comparison | Verified | `DeepStructureCompatibilityRound3Tests` | Iterative fixed-depth guards match jq boundaries, including the earlier object-key-array comparison boundary. Width is not limited. |
| Deep object merge | Verified | `DeepMergeCompatibilityRound5Tests` | Iterative merge accepts 10,000 and rejects 10,001 with `Object merge too deep`. Width and total bytes are not limited. |
| Deep output | Verified | `PublicDeepOutputCompatibilityTests.ExecuteMaterializesOutputAtJqMaximumStructuralDepth`, `JsonProxyCompatibilityRound2Tests.PrinterMarksValuesBeyondJqMaximumDepthWithoutManagedRecursion` | The serializer avoids CLR recursion at jq depth. It can still allocate the complete serialized value before the public byte check. |
| Large range/generator | Verified | `SecurityResourceCompatibilityTests.ExecutionTransitionLimitStopsLargeRangeBeforeOutputExplosion`, timeout and cancellation tests in the same class | Each resumed `RANGE` opcode result passes through the VM's pre-dispatch transition budget. Callers can explicitly raise/disable practical protection by selecting very large limits. |
| Large string repeat | Verified at jq/CLR boundary | `ArithmeticCompatibilityRound3Tests.UpstreamCase342RejectsOversizedRepeatBeforeAllocation` | Repeat validates jq's byte ceiling and the managed string ceiling before allocation. Other operations, especially concatenation, have no configurable intermediate-byte ceiling. |
| Filesystem confinement | Verified with qualifications | `ModuleFileCompatibilityTests.PhysicalFilesystemConfinesReadsAndEnforcesLimits`, resolver invalid-name tests, and `ModuleExecutionCompatibilityTests.PlainCompileHasNoAmbientFilesystemCapability` | Plain `Compile` has no filesystem capability. Physical reads require explicit allowed roots and enforce a per-file byte cap. See residual filesystem risks below. |
| Module cycles | Verified | `ModuleExecutionCompatibilityTests.CircularImportsAreRejectedByResolvedPath`, `ModuleResourceLimitCompatibilityTests.CyclesRemainCyclesWhenBudgetsWouldOtherwiseAllowTheGraph` | Cycles are detected by resolved path independently of configured aggregate budgets. |
| Module graph budgets | Opt-in managed extension — verified when configured | The 13 cases in `ModuleResourceLimitCompatibilityTests` | `JqModuleResourceLimits` independently caps unique resolved module/data files, aggregate UTF-8 imported bytes, and dependency depth for each compilation. Aliases of one resolved path count once; module and data imports share the budgets; the root source is not counted; `modulemeta` uses the same accounting; zero limits still allow dependency-free programs. Null defaults preserve prior unrestricted behavior. |
| Environment selection | Native default plus opt-in managed extension — verified | `EnvironmentCompatibilityTests` | Native-default timing is preserved: `$ENV` captures the process environment at compilation, while every `env` call reads the then-current ambient process environment. An explicitly supplied dictionary is copied and replaces both views for that execution without mutating process state; that replacement is an explicit host capability, not upstream jq behavior. |
| Time and locale selection | Verified with host qualifications | The time-specific cases in `CliFixtureLibrarySemanticsTests`, `TimeBuiltinOracleMatrixTests`, and `EnvironmentCompatibilityTests.DefaultTimeBuiltinsShareLibcStyleProcessStaticMktimeState` | Explicit `TZ` selects `localtime`/`strflocaltime`; `LC_ALL`, then `LC_TIME`, then `LANG` selects localized names; UTC `strftime` ignores the selected local zone. Default calls observe ambient process environment and share libc-style process-static TZ/mktime state. Explicit environment snapshots use isolated per-state time contexts. `now` reads the host UTC clock. |
| Stateful I/O capabilities | Verified | The 40 cases in `StatefulIoCompatibilityTests`, `HaltCompatibilityTests`, and `StatefulOriginMetadataCompatibilityTests` | Additional input, debug, stderr, input positions, origins, and search paths are explicit capabilities. Defaults do not consult stdin, stderr, current directory, or program path; process environment is read only by jq's `$ENV`/`env` and local-time paths. Host callbacks are synchronous and require external blocking/I/O bounds. |
| Invalid option values | Verified | `SecurityResourceCompatibilityTests.InvalidResourceOptionsAreRejectedBeforeExecution` and `JqProgram.Validate` | Negative limits and non-positive regex timeouts are rejected. `Timeout = TimeSpan.Zero` intentionally means stop at the first VM dispatch policy check. |
| Native/process/network escape | Verified by source inventory | Static search of `src/DotNetJq` found no process launch, P/Invoke/native-library load, socket, HTTP, or production jq-oracle call | Physical file access exists only in the explicit `JqFileSystem` capability. |
| Package/component boundary | Verified by isolated-package gate | `tools/verify-isolated-package.sh`, `LICENSES.md`, and `src/DotNetJq.GlibcCompat/README.md` | The package contains `DotNetJq.dll` plus a separately replaceable managed `DotNetJq.GlibcCompat.dll`. The main assembly crosses only the documented four-method ABI. Complete corresponding source and offline rebuild inputs for the LGPL component ship under `lgpl-source/`; neither assembly uses native loading or process execution. |

## Residual gaps

### High priority

1. **Compilation has only a parser-stack safety boundary, not a general resource policy.**
   `JqProgram.Compile` accepts an unbounded source string and has no timeout,
   cancellation token, maximum source bytes, compiler-IR instruction count, or
   compiled-bytecode size limit. The production
   GPPG parser bounds its source-integrated LR stack and reports `memory exhausted`
   at jq's exact 9,995-level nested-parenthesis boundary. The module linker
   recursively parses dependencies, and large invalid top-level lines outside
   this guard can still produce large diagnostics.

2. **Output limits are post-construction.** `JqExecution.TryRead` calls `jq_next`, then
   serializes the complete `jv`, and only then checks output bytes and output count. A single
   huge intermediate/output value can exhaust memory before either public output limit fires.
   The materializing `Execute` API also retains every permitted output as a `JsonElement` until
   the stream finishes.

3. **Timeout and cancellation are cooperative.** The direct VM checks policy before each
   forward or backtracking opcode dispatch, but deep/wide equality, comparison,
   containment, sorting, merge, concatenation, JSON parsing/printing, module resolution,
   and filesystem reads do not accept the execution deadline or cancellation token. A
   single expensive opcode or builtin can exceed the requested wall time before control
   returns to the dispatcher.

4. **There is no general intermediate-memory or generated-value limit.**
   `MaxExecutionTransitions` counts VM opcode dispatches, not bytes or retained/generated
   values. Wide arrays/objects, string concatenation, sort buffers,
   module catalogs, and a single large value can allocate substantially without crossing that
   count. String repeat has an upstream-compatible pre-allocation ceiling, but that ceiling is
   approximately a managed-gigabyte scale and is not tied to `MaxOutputBytes`.

### Medium priority

5. **Physical filesystem confinement has a TOCTOU window.** `JqFileSystem` resolves existing
   links, checks the resolved path against allowed roots, and then opens by path. Another process
   can replace a checked path component with a link between those operations. Strong hostile-host
   confinement needs descriptor-relative/no-follow opens or OS/container isolation.

6. **Module limits require explicit configuration.** The physical filesystem and aggregate graph
   limits default to unrestricted for jq compatibility. The
   `RecommendedMaximumFileSizeBytes` constant supplies a 64 MiB opt-in value; hosts can also
   configure unique module/data count, total imported bytes, dependency depth, and cooperative
   cancellation through `JqModuleResolver`. There is still no mechanism to interrupt a synchronous
   filesystem read already in progress; hostile hosts need an externally bounded filesystem or
   process boundary.

7. **Regex cancellation is not immediate.** Regex evaluation receives `RegexTimeout`, not the
   execution cancellation token. Cancellation is observed at the next VM dispatch after the
   regex call. Keep `RegexTimeout` short for untrusted work.

8. **Defaults preserve jq compatibility rather than restricting untrusted work.** VM dispatch,
   recursive evaluation, and regex-operation budgets are effectively unlimited, and wall time,
   input, output-byte, and output-value limits are unset. Untrusted-workload hosts must
   supply an explicitly restrictive `JqExecutionOptions` instance and an external process
   memory/CPU boundary.

### Environment note

Like native jq, the public execution core exposes process environment variables through `$ENV`
and `env`. `$ENV` is compiled to a constant and therefore reflects compilation time; `env`
enumerates the process environment at each builtin invocation. An explicit `Environment`
snapshot is an opt-in managed sandbox extension and replaces both views without mutating process
state. The mapped internal `libjq.get_home()` still reads `HOME`/Windows home variables, but
module `~/` expansion uses the explicitly supplied `JqModuleResolver.HomeDirectory`.

Local-time and clock behavior is a narrower, explicit exception: `localtime` and
`strflocaltime` use the explicit snapshot's `TZ` when present and otherwise read ambient `TZ` at
the builtin boundary; `now` reads the host UTC clock. Locale selection likewise comes from the
explicit snapshot or ambient `LC_ALL`, `LC_TIME`, and `LANG`. The project ports the jq-visible library
semantics exercised by `tests/shtest`. These environment, locale, clock, and time-zone
semantics are present in the public library API. This library resource-control audit does
not evaluate the separate `DotNetJq.Cli` application's option parsing, terminal detection,
stdin iteration, or presentation policy.

## Evidence snapshot

```text
dotnet --version
# 10.0.400

git -C upstream/jq rev-parse HEAD
# 34f7186b86743a083a589741b6cea95293524108
```

The focused resource-control inventory contains 37 cases: 13
`SecurityResourceCompatibilityTests`, 17 `PublicExecutionFacadeContractTests`, 3
`PublicDeepOutputCompatibilityTests`, and the 4 `StatefulIoCompatibilityTests` cases
dedicated to aggregate `MaxInputBytes` behavior.

The production parser portion is generated by GPPG from the maintained C#-
action `src/DotNetJq/Grammar/parser.y`; the original C-action grammar is not committed and
there is no fallback parser. Its pre-promotion Release candidate passed
1,441/1,441 managed tests. Structural validation fixes the generated shape at
167 jq alternatives, 169 rules, 312 states, and zero conflicts, with marked
`%precedence` declarations required to generate byte-identical `%left` and
`%right` tables.

Other source-declared focused case counts used by this audit are 13
`ModuleResourceLimitCompatibilityTests`, 34
`JvParserStreamingCompatibilityTests`, 15 time-specific
`CliFixtureLibrarySemanticsTests`, 8 `EnvironmentCompatibilityTests`, and 40 dedicated
stateful API cases. These are evidence slices, not an additive project total because
other suites overlap the same behavior. The final release gate, rather than this
documentation-only refresh, is responsible for recording the complete managed and
official-fixture counts.
