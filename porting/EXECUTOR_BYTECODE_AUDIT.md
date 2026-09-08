# Compiler and bytecode execution audit

Pinned source: jq 1.8.2 commit `34f7186b86743a083a589741b6cea95293524108`.

## Conclusion

Production compilation emits jq-shaped 16-bit bytecode and production execution uses
the direct stack/frame/fork VM. The former AST evaluator, duplicate AST builtin catalog,
and structural-only disassembler have been deleted. There is no fallback execution path.

## Production chain

| Stage | Managed file | Evidence |
|---|---|---|
| Parser actions to compiler IR | `Grammar/parser.y`, generated parser, `compile.h.cs` | generated-parser reference and grammar-coverage tests |
| Binding and bytecode lowering | `compile.c.cs` | pinned instruction-stream and compiler compatibility tests |
| Bytecode graph/metadata | `bytecode.h.cs`, `bytecode.c.cs`, `opcode_list.h.cs` | exhaustive 43-opcode layout/disassembly tests |
| Persistent execution stack | `exec_stack.h.cs` | structural stack tests plus direct VM payload tests |
| VM dispatch | `execute.c.cs` | exhaustive forward/backtracking opcode tests |
| State lifecycle | `jq.h.cs` | compile/start/next/reset/teardown and ownership tests |
| Builtin cfunction table | `builtin.c.cs` | direct cfunction and complete builtin suites |
| CLI disassembly/execution | `DotNetJq.Cli` | bytecode disassembly, CLI process, shell, and PowerShell tests |

## Compiler audit

The compiler retains jq names and control flow for `inst`/`block` construction,
binding, lexical levels, argument expansion, cfunction resolution, constants, locals,
closures, subfunctions, branches, calls, and diagnostics. `compile()` writes the same
variable-length ushort instruction form consumed by the VM. Compiler pseudo-opcodes are
lowered and rejected if they reach execution.

`BytecodeProductionPipelineTests` verifies pinned instruction streams, constant pools,
cfunction tables, child graphs, direct runtime results/errors, and teardown. Parser,
module, binding, closure-limit, and compile-diagnostic suites cover the wider source
surface.

## VM audit

`EXECUTE_VM_IMPLEMENTATION_MAP.md` maps state, frame, stack, forkpoint, path, reset,
start/next, optimizer, and every executable forward/`ON_BACKTRACK` opcode case to
upstream `execute.c`. `REFERENCE_COUNT_OWNERSHIP_MAP.md` records all explicit
`jv_copy`/`jv_free` and copy-on-shared-pop boundaries.

The typed CLR payload side table in `exec_stack.h.cs` is the only structural stack
substitution: persistent block topology and logical addresses remain source-shaped,
while GC references are not written into a reallocating raw memory buffer.

## State and public boundary

A `jq_state` owns its bytecode and one active VM. `jq_start` consumes the primary
input, `jq_next` transfers one output owner or returns invalid, reset frees execution
owners, and teardown also frees bytecode. A public `JqProgram` is sequential and
non-thread-safe like its state; independent programs provide parallelism.

Uncaught invalid-with-message values remain jq-shaped through `jq_next`; the public
facade converts them into `JqRuntimeException`. The CLI uses state bytecode for
disassembly and uses the same direct VM for execution.

## Managed-only policies

Timeout, cancellation, transition, recursion, input/output byte, and output-count limits
are opt-in host features. Default execution has no transition counter/clock/token work in
the opcode fast path. The exact `MaxExecutionTransitions` unit and timing are
documented in `JqExecutionOptions` and frozen by
`ExecutionTransitionBudgetContractTests`.

## Exclusions

The parity claim does not include C structure layout, memory addresses, allocator-failure
injection, C function-pointer ABI, native assertion behavior for malformed bytecode, or
native refcount-address trace text. These exclusions do not replace compiler or execution
semantics.
