# Executor architecture

Pinned source: jq 1.8.2 commit `34f7186b86743a083a589741b6cea95293524108`.

## Production path

```text
jq source
  -> generated lexer/parser
  -> compile.h block/inst IR
  -> compile.c binding and bytecode lowering
  -> state-owned bytecode graph
  -> execute.c direct stack/frame/fork bytecode VM
  -> jq_next result or invalid-with-message
```

There is one execution engine. The former `JqNode`/`EvalContext` iterator evaluator,
its `NativeBuiltins` implementation, and the structural disassembler were transitional
implementations and have been deleted. Production code neither shells out to jq nor
falls back to an AST evaluator.

## Exact source-file mapping

| Upstream source | Managed source | Role |
|---|---|---|
| `src/compile.h` | `src/DotNetJq/Port/src/compile.h.cs` | Source-shaped `inst`, `block`, immediates, bindings, and list composition |
| `src/compile.c` | `src/DotNetJq/Port/src/compile.c.cs` | Generation, binding, optimization handoff, and bytecode lowering |
| `src/bytecode.h` | `src/DotNetJq/Port/src/bytecode.h.cs` | Opcode, cfunction, symbol-table, and bytecode graph shapes |
| `src/bytecode.c` | `src/DotNetJq/Port/src/bytecode.c.cs` | Opcode metadata, disassembly, environment constants, and graph teardown |
| `src/exec_stack.h` | `src/DotNetJq/Port/src/exec_stack.h.cs` | Negative-offset persistent stack blocks used by the VM |
| `src/execute.c` | `src/DotNetJq/Port/src/execute.c.cs` | Direct opcode dispatcher, frames, forks, paths, calls, and optimizer |
| `src/jq.h` | `src/DotNetJq/Port/src/jq.h.cs` | `jq_state`, callbacks, compile/start/next/reset/teardown, halt state |
| `src/jv_aux.c` | `src/DotNetJq/Port/src/jv_aux.c.cs` | Direct value/path helpers used by VM INDEX, path updates, and builtins |

`EXECUTE_VM_IMPLEMENTATION_MAP.md` contains the function and exhaustive opcode map.

## State and concurrency

One `JqProgram` owns one `jq_state`, exactly as a native caller owns a compiled
state. A state permits one active execution and is not thread-safe. Overlapping use is
rejected; callers that need parallel evaluation use independently compiled programs
and therefore independent states. No mutex is placed around the bytecode VM.

Process-wide synchronization that remains is not an invented same-state execution
model:

- `jv_thread.h.cs` maps jq's pthread mutex/once/thread-specific-storage abstraction
  used by allocation, number conversion, and hash initialization.
- the regex compatibility layer uses a finite concurrent immutable
  pattern-translation cache;
- default time builtins serialize access to one libc-shaped process-static TZ/mktime
  context, while explicitly configured environments use their owning state's context.
  Neither mechanism makes VM/value state concurrent-safe.

## VM ownership model

Managed assignment does not stand in for `jv_copy`. The VM keeps jq ownership
operations at the same source boundaries:

- `stack_pop` moves a value when the physical stack block becomes unreachable and
  copies it when a saved fork still shares the block.
- `stack_popn` moves the value and writes null into a shared saved slot, matching jq.
- frame locals own one `jv`; `frame_pop` frees them only when the physical frame is
  no longer shared.
- a forkpoint owns its saved `value_at_path` copy while data and frame stack blocks
  remain structurally shared.
- constant-pool reads, raw `jv_get`/`jv_set`, cfunction calls, invalid-message
  extraction, and top-level `RET` use their consuming interfaces directly.
- reset and teardown unwind forks, live stack values, local variables, error/path
  state, and the compiled bytecode graph deterministically.

CLR references cannot safely be placed inside a reallocating native-style byte buffer.
`exec_stack.h.cs` therefore side-tables typed payloads by the same logical negative
stack address. Block topology, sharing, save/restore order, and release decisions still
follow the upstream algorithm.

## Dispatch and backtracking

`jq_next` restores a saved program counter and executes until it returns one value,
halts, raises an uncaught jq error, or exhausts all forks. Backtracking uses the same
`ON_BACKTRACK(op) = op + NUM_OPCODES` encoding and re-enters the opcode switch.
`CALL_JQ`, `TAIL_CALL_JQ`, and `RET` use explicit source-shaped frames; tail-call
analysis is the port of `ret_follows`, `tail_call_analyze`, `optimize_code`, and
`optimize` from the end of upstream `execute.c`.

The managed public API converts a terminal invalid-with-message into
`JqRuntimeException` only at the facade boundary. Direct `jq_next` callers retain
jq's inspectable invalid-value contract.

## Managed execution policies

jq has no native equivalents for the public timeout, cancellation, output-size, or
transition-budget options. They are specification-authorized, opt-in managed host
policies rather than upstream jq requirements; defaults add no policy work inside the
opcode loop.

`MaxExecutionTransitions` has one stable unit: an attempted direct-VM instruction
dispatch. It is charged immediately before every forward and `ON_BACKTRACK` dispatch,
cumulatively across all output pulls. Zero stops
before `TOP`; top-level output `RET` and its terminal backtracking `RET` are
separate transitions; an already-exhausted pull is not charged. Cancellation is
checked before the charge. The counter resets at every `jq_start`.

`ExecutionTransitionBudgetContractTests` freezes exact 0/1/N timing for forward
execution, backtracking, compiled builtin streams, tail calls, errors, cancellation,
and sequential reuse.

`MaxGeneratedValues` is affirmatively absent: jq has no single source-defined
generated-intermediate-value event, so exposing that name would invent a contract.
`MaxOutputValues` counts values yielded through the public output boundary, while
`MaxExecutionTransitions` counts attempted direct-VM instruction dispatches.

## Deliberate implementation differences

- Managed structures and delegates are not C layout, address, allocator, or
  function-pointer ABI compatible.
- Invalid bytecode throws a managed exception where native debug builds assert or
  release builds may have undefined behavior.
- Host callback exceptions cross a managed exception boundary.
- Debug trace values do not expose native heap addresses.
- Iterative managed work stacks are used at jq's fixed 10,000-depth value/path limits
  where equivalent CLR recursion would risk a process stack overflow.

These differences do not introduce a second execution semantics.
