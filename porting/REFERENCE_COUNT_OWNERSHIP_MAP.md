# jq reference-count and ownership mapping

Pinned source: jq 1.8.2 commit `34f7186b86743a083a589741b6cea95293524108`.

DotNetJq uses explicit `jv_copy`/`jv_free` refcounts and copy-on-write value
storage. Production execution is the direct bytecode VM in
`src/DotNetJq/Port/src/execute.c.cs`; there is no AST/iterator ownership model.

## Value owners

| Managed implementation | Upstream source | Contract |
|---|---|---|
| `jv_refcnt`, `jv_copy`, `jv_free` in `jv.c.cs` | `src/jv.c:53-75,1999-2069` | Copy increments an allocated value owner; free decrements it; count one permits in-place mutation |
| byte-backed strings in `jv.c.cs` | `src/jv.c` string allocation/append | UTF-8 payload, embedded NUL support, capacity/hash state, and copy-on-write detachment |
| array offset/length/capacity storage in `jv.c.cs` | `src/jv.c:759-954` | Shared views, capacity growth, element-owner transfer, and copy-on-write mutation |
| object slots/buckets in `jv.c.cs` | `src/jv.c:1550-1850` | Raw `jv` key/value ownership, tombstones, rehash, and copy-on-write mutation |
| constants in `bytecode.constants` | `src/compile.c`, `src/bytecode.c` | Compiler copies constants into the pool; LOADK obtains an owner; bytecode teardown releases the graph. `$ENV` slots are summarized bottom-up, and each executed `$ENV` graph receives fresh replacement owners; graphs without a slot do not materialize an environment object. |
| `jv_dump_string` / `jv_dump_string_trunc` input | `src/jv_print.c:405-439` | Both consume the passed owner. Managed callers that retain a value use the explicitly named `jv_dump_string_borrowed` / `jv_dump_string_trunc_borrowed` adapters, which perform `jv_copy` before the consuming call |

CLR garbage collection owns only the managed containers. It does not replace jq value
ownership or decide copy-on-write behavior.

## VM stack owners

`exec_stack.h.cs` preserves jq's negative logical addresses, predecessor links,
persistent shared blocks, physical limit, save/restore ordering, and
`stack_pop_will_free` decision. Typed CLR payloads are side-tabled by logical address
because moving raw buffers cannot safely contain GC references.

| VM operation | Ownership effect |
|---|---|
| `stack_push(v)` | Moves one owned `jv` into a data-stack block |
| `stack_pop()` | Moves `v` if the physical block is released; returns `jv_copy(v)` if a saved fork still shares it |
| `stack_popn()` | Moves `v`; when shared, replaces the saved slot with null exactly as jq does |
| `frame_push` | Installs borrowed closure descriptors and invalid local slots |
| `frame_pop` | Frees locals only when their shared physical frame block is actually released |
| `stack_save` | Shares data/frame block positions and owns one `jv_copy(value_at_path)` in the forkpoint |
| `stack_restore` | Unwinds newer blocks, transfers the forkpoint path owner, and resumes its bytecode address |
| VM reset | Exhausts forks, frees live data/local/error/path owners, and retains compiled bytecode |
| state teardown/recompile | Resets execution, then recursively frees the state-owned bytecode graph |
| CLI `jq_set_input_cb` | Moves one parser/raw `jv` directly from the shared `CliInputReader` into `f_input`; error messages move into an invalid wrapper, and plain invalid denotes end before `f_input` creates `break` |

A saved continuation therefore does not eagerly increment every value refcount.
The later pop performs jq's same copy-on-shared-pop or move-on-final-pop decision.

## Opcode boundaries

The exhaustive table is in `EXECUTE_VM_IMPLEMENTATION_MAP.md`. Important transfer
boundaries are:

- `LOADK` obtains an owned constant and frees the replaced stack input.
- `INDEX`, `EACH`, path operations, and array/object updates call consuming
  `jv_get`/`jv_set` helpers with source-visible copies and moves.
- `CALL_BUILTIN` pops exactly the encoded owned arguments into the cfunction table.
  A valid return is moved to the stack; invalid-with-message is moved to the error slot.
- `CALL_JQ` moves input into a new frame. `TAIL_CALL_JQ` inherits the caller return
  boundary and releases the replaced frame according to stack sharing.
- function `RET` moves the result to the caller; top-level `RET` transfers one owner
  to `jq_next` and saves its backtracking continuation.
- `TRY_BEGIN`/`TRY_END`, `FORK`, `EACH`, and `RANGE` use physical saved
  forkpoints, not cloned managed iterator state.
- uncaught jq errors remain owned invalid-with-message values until the public facade
  materializes and releases the error payload.

## Parser, compiler, linker, and state

Parser grammar actions produce owning `block`/`inst` IR and free it on error or
after lowering. Linker operations transfer module/data constants into the compiler
graph. `compile()` copies constants and materializes child bytecode; `block_free()`
then releases IR owners. One `jq_state` owns the compiled graph and at most one active
VM. Sequential `jq_start` calls reset execution owners while retaining bytecode.

One public `JqProgram` corresponds to one non-thread-safe `jq_state`. It rejects an
overlapping execution; callers use separately compiled programs/states for concurrency.
There is no VM mutex and no automatic atomic jq value refcount.

## Managed differences

- Managed structures are not native C layout, allocation-address, or function-pointer ABI.
- Deterministic `Dispose`/`finally` releases abandoned public cursors; CLR finalization
  is only a last-resort facade cleanup.
- Iterative work stacks replace C recursion at jq's fixed 10,000-depth value/path limits
  where CLR recursion could overflow the process stack.
- Invalid bytecode raises managed exceptions instead of native assertions/undefined behavior.

These are cross-platform implementation substitutions. Windows, Linux, macOS,
framework-dependent, and NativeAOT builds use the same jq ownership logic.

## Verification

- `JvArrayRefcountCowCompatibilityTests`,
  `JvObjectRefcountCowCompatibilityTests`,
  `JvStringInvalidRefcountCowCompatibilityTests`, and
  `JvLiteralNumberRefcountCompatibilityTests` cover allocated value counts and COW.
- `DirectBytecodeVmCompatibilityTests` covers shared stack blocks, forkpoints, locals,
  constants, cfunction arguments/results, calls, paths, errors, output, and reset.
- `EnvironmentFastPathCompatibilityTests` covers no-slot graph summaries, root/nested
  `$ENV`, recompile cleanup, sequential fresh replacement, and independent owners.
- `CliInputCallbackCompatibilityTests` covers direct callback owner identity, early-reset
  release, parser-error wrapper/message release, and exact forward-stream behavior.
- compiler/control-flow/builtin/path refcount oracle suites cover production bytecode
  suspension, backtracking, early disposal, recompile, and teardown.
- `JvPrintOwnershipCompatibilityTests` covers consuming dump/truncation calls,
  explicitly borrowed calls, child-owner release, and native byte-buffer boundaries;
  `CliOutputOwnershipTests` covers repeated borrowed formatting and converted-value cleanup.
