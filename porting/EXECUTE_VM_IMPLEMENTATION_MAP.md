# `execute.c` direct VM implementation map

Pinned source: jq 1.8.2, commit `34f7186b86743a083a589741b6cea95293524108`.

Managed implementation: `src/DotNetJq/Port/src/execute.c.cs`.

This file maps the direct managed bytecode dispatcher to `src/execute.c`. Production
parser/compiler execution enters this VM with compiler-produced `bytecode`; the former
managed AST evaluator has been deleted.

## State, stack, frame, and fork map

| Native source | Managed symbol | Ownership and control-flow contract |
|---|---|---|
| `jq_state`, lines 19-51 | `jq_state` partial + `jq_bytecode_vm` fields | One VM belongs to one `jq_state`; no overlapping execution of a state. `error`, `path`, and `value_at_path` are owning `jv` slots. |
| `closure`, lines 53-56 | `closure` | Borrowed bytecode graph plus a stack address for the closed lexical frame. |
| `frame_entry`, `frame`, lines 58-71 | `frame_entry`, `frame` | Closure entries borrow bytecode/frame addresses. Local-variable entries own exactly one `jv`, initially invalid and freed only when the physical frame block is released. |
| `frame_size/current/get_level/local_var/make_closure`, lines 73-120 | `frame_size`, `frame_current`, `frame_get_level`, `frame_local_var`, `make_closure` | Relative lexical levels and `ARG_NEWCLOSURE` are interpreted without changing the bytecode ABI. Invalid bytecode throws instead of invoking C undefined behavior/assertion. |
| `frame_push/frame_pop`, lines 122-160 | `frame_push`, `frame_pop` | Argument closures are copied structurally. Locals are `jv_free`d only when `stack_pop_will_free` says the physical frame is no longer shared. |
| `stack_push/pop/popn`, lines 162-191 | `stack_push`, `stack_pop`, `stack_popn` | `stack_pop` calls `jv_copy` only for a shared physical block. `stack_popn` moves the handle and replaces a shared saved slot with `jv_null`, exactly as native. |
| `forkpoint`, `stack_pos`, `stack_save`, lines 194-223 | `forkpoint`, `stack_pos`, `stack_save` | A fork owns one copy of `value_at_path`, records path length/subexpression depth, and shares the saved data/frame blocks without copying their values. |
| `path_intact/path_append/_jq_path_append`, lines 225-268 | `path_intact`, `path_append`, `AppendPath`, `_jq_path_append` | `jv_identical` consumes its current/copy operands. Path components and `value_at_path` are moved or freed at the same branches as native. |
| `stack_restore`, lines 270-301 | `stack_restore` | Newer physical data/frame blocks are unwound in allocation order; a restored fork transfers its owned `value_at_path` and slices the current path to its saved length. |
| `jq_reset`, lines 303-324 | `Reset`, `jq_reset`, `ResetForNextExecution` | Exhausts all forkpoints, frees live values/locals/error/path, resets the stack, and preserves compiled bytecode for the next execution. |
| `set_error`, lines 332-336 | `set_error` | Frees the previous error slot and moves in the replacement. Null means no raised error; invalid means an active raised error. |
| `jq_start`, lines 1116-1129 | `Start`, `jq_start_bytecode` | Resets, applies managed explicit-environment policy only when the compiler-produced graph summary reports a `$ENV` constant slot, pushes the top frame and consumed input, saves bytecode address zero, and marks the first `jq_next` as forward execution. |
| `jq_next`, lines 340-1003 | `ExecuteNext`, `jq_next_bytecode` | Restores one continuation per call, applies `ON_BACKTRACK`, returns one moved top-level value, and returns an invalid-with-message owner for an uncaught jq error. |
| `jq_teardown`, lines 1131-1143 | `jq_state.Teardown`, VM `Dispose` | Resets execution before compiled-program/state teardown. Direct test callers separately own and `bytecode_free` hand-built graphs. |
| `ret_follows/tail_call_analyze/optimize_code/optimize`, lines 1145-1213 | same names in `execute.c.cs` | Preserves the native CALL_JQ-to-TAIL_CALL_JQ analysis. Forward-JUMP and subfunction recursion use explicit managed loops, preserving result/mutation order without consuming CLR stack. |
| `args2obj/jq_compile_args/jq_compile`, lines 1216-1262 | same names in `execute.c.cs` | Preserves top-level reset/recompile, parser/linker/builtin binding, argument-object ownership, bytecode optimization, and compile result contract. Managed overloads add explicit module resolver/origin capabilities without changing the jq-shaped entry points. |
| halt/getters, lines 1323-1348 | existing `jq_state.Halt` and getters | VM observes the state halt bit before each dispatch and returns invalid without consuming exit/error-message owners. |

`exec_stack.h.cs` preserves the negative-offset address and physical `limit` algorithm.
The raw byte region cannot safely store moving CLR references, so typed payloads are
associated with the same logical block address in `stack.block_value`. Payload removal
still occurs only when `stack_pop_block` advances the physical limit.

The CLI installs `CliInputReader.ReadNextInput` with the existing jq-shaped
`jq_set_input_cb`, matching `main.c:664` and `util.c:jq_util_input_next_input_cb`.
The callback moves one owned `jv` into `f_input`, returns plain invalid at end, wraps
parser errors as invalid-with-message, and updates the state position before returning.
The public `IJqInputSource`/`JsonElement` adapter remains a separate supported host API;
CLI values do not traverse or reparse through that adapter.

## Exhaustive opcode map

`B(op)` below means `ON_BACKTRACK(op)`, encoded as `op + NUM_OPCODES` exactly as in
`execute.c`. “Move” means no `jv_copy`; the source owner must not be used afterward.

| Opcode | Forward behavior and ownership | Backtracking behavior |
|---|---|---|
| `LOADK` | `jv_array_get(jv_copy(constants), i)` obtains the constant owner; `jv_free(stack_pop())`; pushes/moves constant. | No saved continuation; reaching `B(LOADK)` is invalid bytecode. |
| `DUP` | Pops/moves `v`; pushes `jv_copy(v)`, then moves `v`. | Invalid bytecode. |
| `DUPN` | `stack_popn` moves `v` and nulls a shared saved slot; pushes `jv_copy(v)`, then moves `v`. | Invalid bytecode. |
| `DUP2` | Pops/moves `keep`, then `v`; pushes `jv_copy(v)`, moves `keep`, moves `v`. | Invalid bytecode. |
| `PUSHK_UNDER` | Gets an owned constant; pops/moves upper value; pushes constant then upper value. | Invalid bytecode. |
| `POP` | Pops/moves and `jv_free`s one value. | Invalid bytecode. |
| `LOADV` | Frees current stack input; pushes `jv_copy(local)`. | Invalid bytecode. |
| `LOADVN` | `stack_popn` and free current input; moves local to stack; re-resolves local and stores `jv_null`. | Invalid bytecode. |
| `STOREV` | Pops/moves value; frees old local; moves value into local. | Invalid bytecode. |
| `STORE_GLOBAL` | Gets owned constant; frees old local; moves constant into local. | Compiler pseudo-call only in lowering, but executable after lowering exactly as native. No backtrack arm. |
| `INDEX` | Pops/moves target and key. Path check consumes `jv_copy(target)`. Raw `jv_get(target, jv_copy(key))` consumes target/copy. Success moves key into path and pushes result; failure frees key and moves invalid result to `error`. | No opcode-owned continuation. |
| `INDEX_OPT` | Same success path as `INDEX`; failure frees key and invalid result without setting `error`. | No opcode-owned continuation. |
| `EACH` | Pops container; path check consumes copy; pushes container and `-1`, then executes iterator arm. Array/object element access returns owned key/value. Final array element frees container; other elements save container/index without copying the saved stack block. Scalar input sets an invalid-with-message error. | Advances saved index, obtains next owned key/value, frees both on raised-error unwind, or frees container at exhaustion and continues backtracking. |
| `EACH_OPT` | Same iteration as `EACH`; scalar input silently backtracks. | Same as `B(EACH)`. |
| `FORK` | Saves continuation at the opcode and skips branch offset. No `jv_copy` of saved data/frame slots. | If raising, propagates; otherwise reads offset and enters alternate branch. |
| `TRY_BEGIN` | Saves continuation at opcode and skips handler offset. | Empty/non-raising branch frees saved input and propagates backtrack. Raising branch unwraps copied error; nested invalid re-raises; caught message consumes error, replaces it with null, and jumps to handler. |
| `TRY_END` | Saves continuation at opcode. | If raising, wraps `jv_copy(error)` inside a new invalid so the matching `TRY_BEGIN` cannot catch it; propagates backtrack. |
| `JUMP` | Reads forward `ushort` offset and advances from the word after the immediate. | Invalid bytecode. |
| `JUMP_F` | Pops test, conditionally jumps for false/null, then moves test back to stack. | Invalid bytecode. |
| `BACKTRACK` | Restores next fork. With no fork, moves uncaught invalid error to caller or returns empty invalid. | No distinct backtrack arm. |
| `APPEND` | Pops/moves value; consuming `jv_array_append(local, value)` replaces the array local. | Invalid bytecode. |
| `INSERT` | Pops/moves stack-top, value, key, object. String key path consumes object/key/value, then restores stack-top. Invalid key formats a copied key and frees all four values before raising. | Invalid bytecode. |
| `RANGE` | Pops/moves maximum. Moves current numeric local to output, replaces local with incremented number, pushes maximum into a saved continuation, then pushes current. Type/exhaustion paths free maximum. | Re-enters the same iterator logic. A raised-error unwind frees restored maximum before propagating. |
| `SUBEXP_BEGIN` | Pops/moves `v`; pushes `jv_copy(v)`, moves `v`; increments subexpression nesting. | Invalid bytecode. |
| `SUBEXP_END` | Decrements nesting; pops/moves `a,b`; pushes/moves `a,b` in native order. | Invalid bytecode. |
| `PATH_BEGIN` | Pops/moves input; moves old path to stack; saves continuation; moves old nesting/value-at-path to stack; pushes `jv_copy(input)`; installs new array path and moves input into `value_at_path`. | Frees current path, restores old path from stack, and propagates. |
| `PATH_END` | Pops result; path check consumes a copy. Invalid expression consumes result while formatting error. Success frees result, restores old nesting/value, moves completed path through a saved continuation, and pushes it. | Same restoration arm as `B(PATH_BEGIN)`. |
| `CALL_BUILTIN` | Pops exactly encoded arguments in native order and transfers all owners to arity-specific delegate. Valid result moves to stack. Invalid result is copied only for `jv_invalid_has_msg`; it is then moved to error or freed. | No opcode-owned continuation. |
| `CALL_JQ` | Pops/moves input; resolves callee closure; creates frame entries from closure immediates; records caller data/return address; pushes/moves input into callee. | No direct arm; callee fork/RET controls return. |
| `RET` | Pops/moves value. Function return pops frame then pushes/moves result. Top-level return pushes null, saves a continuation at RET, and transfers value to `jq_next`. | Resumed top-level RET propagates backtracking. |
| `TAIL_CALL_JQ` | Resolves callee before popping current frame, inherits return data/address, pushes replacement frame/input. Source frame local frees follow physical-sharing rule. | Same return lifecycle as `CALL_JQ`. |
| `CLOSURE_PARAM` | Compiler-only zero-length pseudo instruction; rejected if it reaches VM. | Not executable. |
| `CLOSURE_REF` | Compiler-only binding pseudo instruction; rejected if it reaches VM. | Not executable. |
| `CLOSURE_CREATE` | Compiler-only zero-length pseudo instruction; rejected if it reaches VM. | Not executable. |
| `CLOSURE_CREATE_C` | Compiler-only zero-length pseudo instruction; rejected if it reaches VM. | Not executable. |
| `TOP` | No-op top-frame entry marker. | No saved continuation. |
| `CLOSURE_PARAM_REGULAR` | Compiler-only zero-length pseudo instruction; rejected if it reaches VM. | Not executable. |
| `DEPS` | Linker/compiler metadata pseudo instruction; rejected if it reaches VM. | Not executable. |
| `MODULEMETA` | Linker/compiler metadata pseudo instruction; rejected if it reaches VM. | Not executable. |
| `GENLABEL` | Pushes a new object containing the jq-state-persistent unsigned label counter; object/key/value owners are consumed by `jv_object_set`. | Invalid bytecode. |
| `DESTRUCTURE_ALT` | Saves continuation at opcode and skips branch offset. | Empty/non-error path frees saved input and propagates. Error path preserves jq 1.8.2's literal condition, frees error, clears it to null, and jumps to alternate. |
| `STOREVN` | Saves continuation before performing exact `STOREV` move/free operations. | Frees current local, stores null, then propagates backtracking so the saved input is unwound. |
| `ERRORK` | Gets owned constant, moves it into an invalid-with-message wrapper, replaces error slot, and backtracks. | No opcode-owned continuation. |

## `jv_copy` / `jv_free` audit

Every source-level copy/free boundary is visible in `execute.c.cs`; no CLR assignment is
treated as a jq copy.

- Frame local cleanup exactly follows `stack_pop_will_free`; a shared saved frame is not
  freed during an active branch pop.
- Data-stack `jv_copy` occurs only in `stack_pop` when the physical block remains shared.
  `stack_popn` performs the native move-plus-null operation instead.
- `stack_save` copies only `value_at_path`. Saved data/local slots are persistent stack
  references, not copied `jv` owners.
- `stack_restore` moves the forkpoint's `value_at_path` into the VM slot after freeing the
  prior slot.
- Constant reads copy the constants array because `jv_array_get` consumes its array.
- All path identity checks pass the same copied operands to consuming `jv_identical`.
- All raw `jv_get`, array/object set/append/concat/slice, invalid-message extraction, and
  cfunction delegate calls use their consuming interfaces directly.
- Error formatting uses consuming `jv_dump_string_trunc` at native owner-transfer sites
  and the explicitly named `jv_dump_string_trunc_borrowed` adapter where a caller retains
  its owner. UTF-8 byte-buffer truncation follows the native boundary.

Deliberate managed differences are limited to:

1. Invalid bytecode throws `InvalidOperationException` where native debug builds assert
   and release builds may have undefined behavior.
2. Typed CLR payloads are side-tabled by native-shaped stack address because GC-managed
   references cannot be stored safely in a reallocating raw byte buffer.
3. Debug traces preserve opcode/address/backtracking and stack order, but the current
   managed printer does not expose native `JV_PRINT_REFCOUNT`; values are printed as jq
   JSON without the extra refcount annotation.
4. Specification-authorized, opt-in managed public timeout/cancellation/transition/recursion
   policies add checks before
   dispatch. `MaxExecutionTransitions` has one stable unit: a direct-VM opcode-dispatch
   transition, charged before every forward or `ON_BACKTRACK`
   arm. The count spans all `jq_next` pulls until reset; zero stops before `TOP`, output
   `RET` and its terminal backtrack are separate transitions, and already-exhausted pulls
   are uncharged. Cancellation is checked before the charge. The ordinary jq-compatible
   default precomputes a false policy flag and has no Stopwatch/token/count work in the
   opcode fast path. `ExecutionTransitionBudgetContractTests` freezes exact thresholds for
   forward execution, backtracking, builtin streams, tail calls, errors, cancellation, and
   sequential state reuse. `MaxGeneratedValues` is affirmatively absent because jq has
   no source-defined generated-intermediate-value event. `MaxOutputValues` counts values
   yielded through the public output boundary; `MaxExecutionTransitions` counts the VM
   instruction transitions described here.
5. Managed cfunction delegates may throw host exceptions. Native C function pointers cannot
   throw; host-exception translation remains the surrounding managed API's responsibility.
6. An explicit `JqExecutionOptions.Environment` replaces the compiler-produced `$ENV`
   constants before execution so the managed sandbox capability applies equally to `$ENV`
   and `env`. Default execution restores jq's compile-time `$ENV` snapshot. Constant-pool
   slots carry managed-only provenance metadata, and each bytecode node carries a
   deterministic bottom-up summary of whether its subtree contains any such slot; the
   bytecode words are unchanged. `jq_start` checks the root summary before restoration,
   explicit environment materialization, or graph traversal. The `$ENV` path still creates
   a fresh replacement object for every execution and retains the same copy/free contract.

## Focused tests

`tests/DotNetJq.Tests/DirectBytecodeVmCompatibilityTests.cs` uses hand-built bytecode and
does not depend on parser lowering. It covers top-level lifecycle, constants, stack
duplication including shared `DUPN`, fork/jump/conditional behavior, locals and all
load/store variants, append/range/global store, index/optional/each/path, insert/genlabel,
try/destructure error boundaries, cfunction success/error ownership, closure parameter
frames, normal/tail calls, and reset-time release of saved fork/value owners.

`tests/DotNetJq.Tests/EnvironmentFastPathCompatibilityTests.cs` verifies false summaries
for ordinary filters, `env`, and nested functions without `$ENV`; root and nested `$ENV`
propagation; metadata cleanup on recompile; fresh sequential replacements; and independent
program ownership. `EnvironmentCompatibilityTests` continues to freeze compile-time
snapshot restoration and public option behavior.

`tests/DotNetJq.Tests/BytecodeProductionPipelineTests.cs` separately verifies pinned native
instruction streams and that production `jq_start` dispatches compiler-produced bytecode.
