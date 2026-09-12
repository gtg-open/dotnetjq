# DotNetJq — Source-Traceable .NET Reimplementation of jq/libjq

**Document type:** technical implementation and compatibility specification
**Target:** a managed .NET implementation of jq's library semantics, optimized for source traceability and testable behavioral compatibility rather than a greenfield architectural redesign.

---

## 0. Executive decision

The project will reimplement jq/libjq in .NET with a deliberately **source-traceable port architecture**.

The key design decision is:

> **Preserve upstream jq file boundaries, symbol names, function names, and dependency relationships as closely as reasonably possible.**

This is not because C-style architecture is inherently better than idiomatic C#. Direct structural correspondence makes each component easier to understand, port, debug, update, review, and verify.

The implementation is **not required to be line-by-line C translated into C#**. Within a mapped method/function, normal C# language features may be used freely when they preserve behavior. The strictness is at the **structural and semantic correspondence level**, not the textual level.

The default rule is:

```text
upstream jq file
      ↓
one obvious .NET counterpart
      ↓
same logical symbols/functions
      ↓
same externally observable behavior
```


## 0.1 Implementation contract

This document defines the technical work needed to implement, verify, and maintain the project consistently.

### Implementation objective

> Reimplement the applicable jq/libjq behavior in managed .NET according to this specification, preserve direct upstream traceability, continuously run the official/differential tests, and continue until the Definition of Done is satisfied or a genuine external/environmental blocker makes further progress impossible.

Implementation must continue beyond the completion of any individual file or subsystem.

The normal loop is:

```text
read repository state
      ↓
read PORTING_MANIFEST.json
      ↓
select highest-priority unfinished work
      ↓
inspect exact upstream source + mapped dependencies
      ↓
implement/port/proxy/generate
      ↓
build
      ↓
run focused tests
      ↓
fix failures
      ↓
run broader compatibility tests
      ↓
update manifest/status/comments
      ↓
commit/checkpoint if the environment supports it
      ↓
select next unfinished work
      ↓
repeat
```

### Routine implementation decisions

Do **not** stop for questions such as:

```text
"Should I port the next file?"
"Should I continue?"
"Should I use System.Text.Json here?"
"Should I fix these remaining tests?"
"Should I update the manifest?"
```

The answers are already defined by this specification.

Make the most conservative compatibility-preserving decision, document it, test it, and continue.

### Decision hierarchy

When the specification does not explicitly settle a detail, use this order:

1. **Externally observable jq 1.8.2 behavior and official tests.**
2. **Direct correspondence with the pinned upstream source.**
3. **Existing decisions already recorded in the port manifest/comments.**
4. **Smallest local change.**
5. **Managed .NET standard-library replacement behind a compatibility proxy, if behavior can be reproduced.**
6. **Only then introduce new project-specific machinery.**

Do not make broad architectural changes because another design appears cleaner.

### Blocker handling

A blocker is not merely:

```text
a test failed
the code is difficult
the first approach did not work
the implementation is uncertain
```

Those are normal implementation work.

For difficult failures:

```text
reproduce
 ↓
reduce to smallest failing case
 ↓
run official jq oracle
 ↓
trace to upstream file/function
 ↓
compare source behavior
 ↓
patch smallest divergence
 ↓
add regression coverage
 ↓
repeat
```

If a failure remains unresolved after several substantially different debugging attempts:

1. record it precisely in `porting/BLOCKERS.md`;
2. include reproducer, expected output, actual output, suspected upstream functions, and attempted fixes;
3. continue with independent work that is not blocked by it;
4. periodically revisit blockers after adjacent subsystems improve.

Work on the current milestone stops only when:

- the Definition of Done is satisfied; or
- the environment itself prevents further work (for example required repository/network/tool access is unavailable and no local workaround exists); or
- every remaining unfinished task is transitively blocked by a documented external blocker.

### Never trade correctness for a green build

The implementation process must never:

- delete or weaken an upstream compatibility test because it fails;
- silently mark an unsupported feature as supported;
- shell out to official jq in production code;
- substitute expected jq output with DotNetJq's current output;
- remove difficult semantics merely to complete the task;
- declare success based only on simple examples.

### Local reasoning discipline

The source-traceable architecture exists partly to make each task locally understandable.

Before loading large parts of the repository, prefer:

```text
current manifest entry
mapped upstream file
mapped target file
direct dependencies
focused failing tests
```

Only widen context when the local evidence is insufficient.

This is a deliberate project optimization: **use source correspondence and executable tests instead of requiring a long narrative history.**

Any upstream component that is not directly ported must be explicitly categorized as:

- `PORT`
- `PROXY`
- `GENERATED`
- `REUSE`
- `OMITTED`

No upstream file or meaningful symbol may simply disappear without an explicit mapping and reason.

---

# 1. Upstream source of truth

## 1.1 Repository

Official jq repository:

https://github.com/jqlang/jq

Do not use forks as the semantic source of truth.

## 1.2 Version to pin

Initial compatibility target:

```text
jq 1.8.2
tag: jq-1.8.2
commit: 34f7186
```

Release page:

https://github.com/jqlang/jq/releases/tag/jq-1.8.2

General releases:

https://github.com/jqlang/jq/releases

The implementation MUST pin this exact upstream revision while parity is being established.

Do not continuously port `master`. A moving upstream target makes compatibility work substantially harder to verify.

## 1.3 Recommended repository layout

Keep the exact upstream repository available inside the .NET project as a read-only reference, preferably as a Git submodule:

```text
/
├── upstream/
│   └── jq/                       # official repository, pinned to 34f7186
│
├── src/
│   ├── DotNetJq/
│   │   ├── Port/
│   │   │   └── src/
│   │   │       ├── builtin.c.cs
│   │   │       ├── builtin.h.cs
│   │   │       ├── bytecode.c.cs
│   │   │       ├── bytecode.h.cs
│   │   │       ├── compile.c.cs
│   │   │       ├── compile.h.cs
│   │   │       ├── execute.c.cs
│   │   │       └── ...
│   │   │
│   │   ├── Compatibility/
│   │   │   ├── Regex/
│   │   │   ├── Json/
│   │   │   ├── FileSystem/
│   │   │   └── Runtime/
│   │   │
│   │   ├── Generated/
│   │   │   ├── Lexer/
│   │   │   └── Parser/
│   │   │
│   │   └── Public/
│   │       └── JqProgram.cs      # idiomatic public .NET API, separate from port core
│   │
│   └── DotNetJq.sln
│
├── tests/
│   ├── DotNetJq.Tests/
│   │   ├── Harness/
│   │   ├── Compatibility/
│   │   └── Regression/
│   │
│   └── UpstreamFixtures/
│       └── ...                   # copied/referenced upstream test data
│
├── porting/
│   ├── PORTING_MANIFEST.json
│   ├── PORTING_STATUS.md
│   ├── BLOCKERS.md
│   └── SYMBOL_EXCEPTIONS.md
│
├── AGENTS.md
├── THIRD_PARTY_NOTICES.md
└── README.md
```

Recommended setup:

```bash
git submodule add https://github.com/jqlang/jq.git upstream/jq
git -C upstream/jq checkout 34f7186
```

Record the exact commit in the .NET repository as well.

---

# 2. Why source-traceable architecture is mandatory

This project is optimized for maintainable, source-traceable implementation.

A contributor debugging `compile.c` should ideally need only:

```text
upstream/src/compile.c
upstream/src/compile.h
src/DotNetJq/Port/src/compile.c.cs
src/DotNetJq/Port/src/compile.h.cs
immediate mapped dependencies
failing compatibility tests
```

It should NOT need to reconstruct a greenfield redesign spanning dozens of unrelated classes.

The intended maintenance loop is:

```text
failing test
   ↓
identify mapped jq function
   ↓
open upstream function
   ↓
open corresponding C# function
   ↓
compare behavior
   ↓
patch
   ↓
rerun tests
```

This same mapping also makes future upstream updates much easier:

```text
git diff jq-1.8.2..future-version
        ↓
list changed upstream files/functions
        ↓
find exact mapped C# files/functions
        ↓
port delta
        ↓
run compatibility suite
```

---

# 3. Strict porting invariants

These rules are mandatory unless this specification explicitly creates an exception.

## 3.1 One upstream file → one explicit mapping

Every meaningful upstream `src/` file must have a manifest entry.

Examples:

```text
src/compile.c
    → src/DotNetJq/Port/src/compile.c.cs
    → PORT

src/jv_parse.c
    → src/DotNetJq/Port/src/jv_parse.c.cs
    → PROXY/PARTIAL-PORT

src/main.c
    → no implementation
    → OMITTED: CLI application

src/parser.c
    → src/DotNetJq/Generated/Parser/...
    → GENERATED from parser.y semantics
```

No silent omissions.

## 3.2 Preserve original filenames in target filenames

Append `.cs` rather than replacing the upstream suffix.

Preferred:

```text
compile.c     → compile.c.cs
compile.h     → compile.h.cs
execute.c     → execute.c.cs
jv.c          → jv.c.cs
```

This makes file origin obvious in IDE search and Git history.

## 3.3 Preserve function/symbol names wherever C# allows it

If upstream has:

```text
jv_array_get
jv_object_get
block_bind
jq_compile_args
jq_next
```

the C# compatibility core should preferably retain:

```csharp
jv_array_get(...)
jv_object_get(...)
block_bind(...)
jq_compile_args(...)
jq_next(...)
```

Do not rename everything into a new object-oriented vocabulary during the compatibility port.

If an upstream identifier conflicts with C# syntax:

1. use C# escaped identifiers (`@name`) if practical;
2. otherwise use the nearest deterministic name;
3. record the exception in `SYMBOL_EXCEPTIONS.md`;
4. add an `UPSTREAM SYMBOL:` comment at the target declaration.

## 3.4 Preserve logical type names where practical

Prefer compatibility types such as:

```text
jv
jq_state
block
inst
opcode
```

rather than inventing unrelated abstractions before parity.

The public library API can be idiomatic. The compatibility core should stay source-oriented.

## 3.5 Method bodies may be idiomatic C#

The project does **not** require textual translation.

Inside a mapped function it is acceptable to use:

- `using` / `using var`;
- `Span<T>` / `ReadOnlySpan<T>`;
- pattern matching;
- `switch` expressions;
- `foreach`;
- local functions;
- standard collection APIs;
- `Task` where asynchronous behavior is actually required;
- .NET exception handling;
- `System.Numerics`;
- standard library helpers.

However, a convenience rewrite must not accidentally change:

- ordering;
- laziness/generator behavior;
- number of outputs;
- error timing;
- scope;
- lifetime semantics that affect behavior;
- numeric semantics;
- Unicode semantics;
- recursive/depth behavior;
- update/path semantics.

Avoid large LINQ rewrites when they obscure jq's generator semantics or materially change allocations/order.

## 3.6 No architectural refactor before parity

Do not perform a broad "make it idiomatic C#" redesign while compatibility is still incomplete.

First:

```text
source-traceable implementation
        ↓
applicable upstream tests passing
        ↓
differential tests passing
```

Only then consider internal refactoring.

Any later refactor must preserve a reliable mapping back to upstream.

---

# 4. Core C# structure recommendation

A practical way to preserve C's global-function style is to use an internal partial compatibility class.

Example:

```csharp
namespace DotNetJq.Port;

internal static partial class libjq
{
    internal static jv jv_array_get(jv value, int index)
    {
        ...
    }
}
```

Then:

```text
jv.c.cs
jv_aux.c.cs
builtin.c.cs
compile.c.cs
execute.c.cs
```

can all contribute to:

```csharp
internal static partial class libjq
```

while preserving original global C function names.

C structs/types should generally become managed types with the same or near-identical names where legal:

```csharp
internal sealed class jq_state
{
    ...
}

internal readonly struct jv
{
    ...
}
```

This is intentionally not the public .NET API.

Provide a separate idiomatic facade:

```csharp
var program = JqProgram.Compile(".items | map(.price) | add");
var result = program.Execute(json);
```

The facade must not leak into the porting core.

---

# 5. Upstream source inventory and initial strategy

The upstream `Makefile.am` currently identifies the principal `libjq` sources as including:

```text
src/builtin.c
src/bytecode.c
src/compile.c
src/execute.c
src/jq_test.c
src/jv.c
src/jv_alloc.c
src/jv_aux.c
src/jv_dtoa.c
src/jv_file.c
src/jv_parse.c
src/jv_print.c
src/jv_unicode.c
src/linker.c
src/locfile.c
src/util.c
src/jv_dtoa_tsd.c

vendor/decNumber/decContext.c
vendor/decNumber/decNumber.c
```

Relevant headers/specification inputs include:

```text
src/builtin.h
src/bytecode.h
src/compile.h
src/exec_stack.h
src/jq_parser.h
src/jv_alloc.h
src/jv_dtoa.h
src/jv_unicode.h
src/jv_utf8_tables.h
src/lexer.l
src/libm.h
src/linker.h
src/locfile.h
src/opcode_list.h
src/parser.y
src/util.h
src/jv_dtoa_tsd.h
src/jv_thread.h
src/jv_private.h
vendor/decNumber/*.h
```

Always generate the authoritative inventory from the pinned upstream checkout instead of trusting this document forever.

Reference:

https://github.com/jqlang/jq/blob/jq-1.8.2/Makefile.am

## 5.1 Initial classification guidance

This is guidance, not permission to skip testing.

### PORT

Likely direct semantic ports:

```text
builtin.c
bytecode.c
compile.c
execute.c
jv.c
jv_aux.c
linker.c
locfile.c
opcode definitions
core jq state/evaluation structures
```

### PROXY or PARTIAL-PORT candidates

Only use a proxy when behavioral tests prove the replacement is compatible enough.

Potential candidates:

```text
jv_alloc.c       → managed allocation / GC
jv_parse.c       → System.Text.Json + jq compatibility layer
jv_print.c       → System.Text.Json + jq compatibility layer where exact
jv_unicode.c     → .NET Unicode/Rune APIs + compatibility logic
jv_file.c        → System.IO behind a controlled filesystem abstraction
regex subsystem  → System.Text.RegularExpressions
thread/platform  → .NET runtime primitives
```

### IMPORTANT: proxies are not assumed correct

For example, `System.Text.Json` is not automatically equivalent to jq's JSON parser/serializer.

jq has observable behavior around:

- numeric parsing;
- `NaN` / infinity-related behavior;
- invalid/edge UTF-8;
- formatting;
- escaping;
- streaming/parser APIs;
- error reporting.

A proxy is allowed only when wrapped behind jq-shaped interfaces and verified by tests.

If a .NET library cannot reproduce behavior, add compatibility logic in the mapped file rather than redesigning callers.

### decNumber / number formatting

Do not assume `.NET decimal` is a drop-in replacement.

Treat `decNumber`, dtoa, numeric representation, and printing as compatibility-sensitive.

The implementation must:

1. identify exact observable jq numeric behavior;
2. run official numeric tests and differential tests;
3. use .NET numeric facilities only where they reproduce behavior;
4. port or emulate missing semantics when necessary.

### REUSE

Some upstream assets do not need translation at all.

Example:

```text
src/builtin.jq
```

This is jq-language source consumed by the engine and can be kept byte-for-byte as an embedded/resource input if appropriate.

Test fixtures should also normally be reused unchanged.

### OMITTED

The principal intentional omission is:

```text
src/main.c
```

Reason:

```text
CLI executable / command-line UX
```

The .NET project is a library implementation, not a clone of `/usr/bin/jq`.

Do not implement CLI flags, terminal behavior, ANSI color output, shell/stdin orchestration, or process exit-code UX unless a later requirement explicitly adds a CLI package.

---

# 6. CLI boundary

The upstream build already separates:

```text
libjq
```

from:

```text
jq executable → src/main.c
```

Reference:

https://github.com/jqlang/jq/blob/jq-1.8.2/Makefile.am

Our target is library semantics.

Do not spend implementation effort reproducing:

```text
jq --raw-output
jq --compact-output
jq --color-output
jq --indent
command-line parsing
terminal detection
shell integration
CLI process exit UX
```

However, do NOT automatically omit a feature merely because the CLI invokes it.

If a capability belongs to jq's language/runtime and is reachable through libjq, it remains in scope.

---

# 7. Lexer and parser strategy

The upstream grammar sources are the source of truth:

```text
src/lexer.l
src/parser.y
```

Generated C artifacts such as:

```text
src/lexer.c
src/lexer.h
src/parser.c
src/parser.h
```

must be classified as `GENERATED`, not independently hand-ported unless a specific blocker makes that necessary.

## 7.1 Requirements

The .NET parser/lexer implementation must:

- preserve jq grammar semantics;
- preserve tokenization behavior;
- preserve parse errors sufficiently for tests;
- preserve source locations required by compiler/errors;
- pass all applicable parser/compiler tests.

## 7.2 Generator choice

The project may use a mature .NET lexer/parser generator or generate and maintain equivalent C# parser code.

The choice is secondary to compatibility.

Rules:

1. `lexer.l` and `parser.y` remain the semantic references.
2. Generated C# artifacts are checked in or deterministically reproducible.
3. Generation instructions are documented.
4. Generated files contain headers linking to the upstream grammar files.
5. Generated files are not manually edited unless unavoidable.
6. Any grammar translation layer must itself be versioned and mapped.

Do not ask a human to manually maintain generated parser output.

---

# 8. Regex strategy

jq's regular-expression functionality is backed upstream by Oniguruma.

For the .NET implementation, use:

```text
System.Text.RegularExpressions
```

as the initial proxy candidate.

All regex calls must go through a jq-shaped compatibility adapter.

Example structure:

```text
Compatibility/Regex/JqRegex.cs
```

The rest of the port must not directly depend on `.NET Regex` semantics.

The adapter must reproduce jq-facing operations such as the behavior needed by:

```text
test
match
capture
scan
sub
gsub
splits
```

Use the dedicated upstream regex tests as the oracle.

Any .NET-vs-Oniguruma incompatibility must be:

1. documented;
2. covered by a regression test;
3. fixed in the adapter if feasible;
4. explicitly recorded as an accepted incompatibility only if unavoidable.

Use regex timeouts to prevent pathological or untrusted reducers from creating unbounded work.

---

# 9. JSON/value representation strategy

Do not allow the public .NET JSON library choice to contaminate the whole port.

Create and preserve a jq-facing `jv` abstraction.

Conceptually:

```csharp
internal readonly struct jv
{
    ...
}
```

The rest of the compatibility core should operate on `jv` and jq-shaped helpers:

```text
jv_array
jv_array_get
jv_array_set
jv_object
jv_object_get
jv_get_kind
jv_equal
...
```

Internally, implementation may use:

- `System.Text.Json`;
- custom managed representations;
- `Dictionary`/arrays;
- `ReadOnlyMemory<byte>`;
- other .NET primitives.

But those are implementation details.

This avoids forcing every ported upstream function to understand a completely new .NET object model.

---

# 10. Mandatory file header format

Every mapped C# source file must begin with a machine-readable-ish comment block.

Example:

```csharp
// DOTNETJQ PORT MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186 (jq-1.8.2)
// Upstream file: src/execute.c
// Upstream URL:
//   https://github.com/jqlang/jq/blob/jq-1.8.2/src/execute.c
// Strategy: PORT
// Target file: src/DotNetJq/Port/src/execute.c.cs
//
// Rules:
// - Preserve upstream function names wherever possible.
// - Behavioral compatibility is defined by upstream tests/differential tests.
// - Do not refactor cross-file architecture during compatibility port.
//
// Substitutions:
// - <none, or list exact substitutions>
//
// Known differences:
// - <none, or explicit tracked items>
```

For proxies:

```csharp
// Strategy: PROXY
// Replacement: System.Text.RegularExpressions
// Upstream subsystem: Oniguruma-backed jq regex behavior
//
// IMPORTANT:
// This is a jq compatibility adapter, not permission for callers to depend
// directly on .NET Regex semantics.
```

---

# 11. PORTING_MANIFEST.json

This file is the primary machine-readable state of the migration.

Every upstream source/header/generated input/test category must have an entry.

Example:

```json
{
  "upstream": {
    "repository": "https://github.com/jqlang/jq",
    "tag": "jq-1.8.2",
    "commit": "34f7186"
  },
  "files": {
    "src/execute.c": {
      "target": "src/DotNetJq/Port/src/execute.c.cs",
      "strategy": "PORT",
      "status": "in_progress",
      "semanticParity": false,
      "notes": []
    },
    "src/jv_parse.c": {
      "target": "src/DotNetJq/Port/src/jv_parse.c.cs",
      "strategy": "PROXY",
      "replacement": "System.Text.Json plus jq compatibility logic",
      "status": "not_started",
      "semanticParity": false,
      "notes": []
    },
    "src/parser.c": {
      "target": "src/DotNetJq/Generated/Parser/",
      "strategy": "GENERATED",
      "sourceOfTruth": "src/parser.y",
      "status": "not_started",
      "semanticParity": false
    },
    "src/main.c": {
      "target": null,
      "strategy": "OMITTED",
      "reason": "CLI application; library-only project"
    },
    "src/builtin.jq": {
      "target": "src/DotNetJq/Resources/builtin.jq",
      "strategy": "REUSE",
      "status": "done"
    }
  }
}
```

Every implementation change must update the manifest in the same commit or pull request.

A task is not complete until the manifest is current.

---

# 12. Tests are the executable specification

The project must treat jq's tests as the primary semantic oracle.

Do not rely on an unsupported assertion that:

> "This looks equivalent."

Equivalence is demonstrated by tests.

Official source:

https://github.com/jqlang/jq/tree/jq-1.8.2/tests

The upstream `Makefile.am` defines the normal test groups and includes:

```text
mantest
jqtest
shtest
utf8test
base64test
uritest
optionaltest          # non-Windows in upstream build
onigtest              # when Oniguruma enabled
manonigtest           # when Oniguruma enabled
```

Important fixture/test files include:

```text
tests/jq.test
tests/onig.test
tests/man.test
tests/manonig.test
tests/base64.test
tests/uri.test
tests/optional.test
tests/modules/...
tests/torture/...
```

Reference:

https://github.com/jqlang/jq/blob/jq-1.8.2/Makefile.am

---

# 13. Test-port strategy

## 13.1 Keep upstream test data unchanged

Do not rewrite thousands of test vectors into a new subjective suite if the existing files can be consumed directly.

Preferred:

```text
upstream tests/*.test
        ↓
.NET compatibility test harness
        ↓
DotNetJq
```

Copy or reference the pinned fixtures byte-for-byte.

## 13.2 Reimplement the test harness in .NET

The .NET test project should understand the upstream jq test format and execute the same cases against DotNetJq.

If useful, port relevant behavior from:

```text
src/jq_test.c
tests/jqtest
other upstream test runners
```

while keeping test data unchanged.

Use a mainstream .NET test framework (for example xUnit) for orchestration/reporting, but do not rewrite jq semantics into xUnit by hand unless necessary.

## 13.3 Differential oracle mode

In addition to upstream fixture expectations, support differential execution:

```text
expression + input
        │
        ├────────► official jq 1.8.2 ─────► expected
        │
        └────────► DotNetJq ───────────────► actual
                                            │
                                         compare
```

Compare at least:

- number of outputs;
- output order;
- output values;
- errors;
- success/failure;
- relevant error category/text where contractually meaningful.

This is extremely valuable for newly discovered edge cases.

## 13.4 Build official jq oracle in CI/dev environment

For source builds, jq documents:

```bash
git submodule update --init
autoreconf -i
./configure --with-oniguruma=builtin
make -j8
make check
```

Reference:

https://github.com/jqlang/jq/blob/jq-1.8.2/README.md

A prebuilt official jq 1.8.2 binary may also be used for differential tests where appropriate, but the version must be pinned and verified.

## 13.5 CLI-only tests

Because this is a library project, some shell/CLI behavior is intentionally out of scope.

However:

> A test is not excluded merely because the upstream runner invokes the CLI.

If the underlying behavior tests jq language/libjq semantics, reproduce it in the .NET harness.

Every excluded test or test group must have an explicit reason in:

```text
tests/EXCLUSIONS.md
```

Allowed reasons include:

```text
CLI-only terminal formatting
CLI-only argument parsing
process exit-code UX
stdin/file orchestration not part of library API
platform-specific CLI behavior
```

Not allowed:

```text
"hard to implement"
"the test was difficult to make pass"
"probably unimportant"
```

## 13.6 Regex tests

Run upstream regex test data separately and publish an explicit compatibility count.

Goal:

```text
core jq semantics: 100% applicable tests
regex semantics:   100% applicable tests if feasible
```

If .NET Regex differs, the exact failing cases become adapter regression tests.

## 13.7 Security regressions

jq 1.8.2 is a security/bug-fix release and includes depth, parsing, containment/hash behavior and memory-safety related fixes.

Although managed .NET eliminates some C memory-safety classes, semantic DoS protections still matter.

Create explicit managed regression tests for:

- maximum/deep path traversal;
- recursive containment;
- parser pathological inputs;
- excessive recursion;
- output explosion;
- regex timeout;
- very large range/generator output;
- excessively deep values.

Reference:

https://github.com/jqlang/jq/releases/tag/jq-1.8.2

---

# 14. Compatibility acceptance criteria

The initial project is accepted only when all of the following are true.

## 14.1 Structural traceability

- Every relevant upstream source file is in `PORTING_MANIFEST.json`.
- Every file is marked `PORT`, `PROXY`, `GENERATED`, `REUSE`, or `OMITTED`.
- Every target source file links to its exact upstream file/revision.
- Every material symbol rename is documented.

## 14.2 Build

```text
dotnet build
```

passes with warnings treated according to an explicit policy.

No hidden dependency on a locally installed `jq` is permitted in the production library.

The official jq binary is test/oracle infrastructure only.

## 14.3 Tests

- All applicable upstream core tests pass.
- All applicable manual-derived tests pass.
- UTF-8/base64/URI suites pass where library-relevant.
- Regex suite parity is measured and reported.
- Any excluded CLI-only tests are enumerated and justified.
- Differential tests show no unknown semantic mismatches.

## 14.4 No accidental native jq dependency

The production library must not:

- shell out to `jq`;
- P/Invoke `libjq`;
- bundle the jq executable as its evaluator.

It is a managed reimplementation.

## 14.5 Public API

A small idiomatic facade exists separately from the compatibility core.

Example target shape:

```csharp
var program = JqProgram.Compile(
    ".markets | map(select(.status == \"OPEN\")) | map(.price) | add");

var results = program.Execute(json);
```

The public facade is not allowed to force redesign of port-core internals.

---

# 15. Implementation workflow

This is the required default workflow for implementation work.

## Stage A — Bootstrap

1. Add/pin upstream jq repository.
2. Record tag/commit.
3. Generate complete upstream file inventory.
4. Create `PORTING_MANIFEST.json`.
5. Classify every file.
6. Create .NET solution/projects.
7. Create test harness skeleton.
8. Build official jq oracle.
9. Verify upstream `make check` passes in the development environment.
10. Commit baseline.

## Stage B — Compatibility foundations

Implement the compatibility primitives that many later files need:

- jq value representation (`jv`);
- allocation/lifetime compatibility semantics where observable;
- utility primitives;
- source-location representation;
- Unicode abstraction;
- numeric representation decisions;
- JSON parser/serializer adapter shape.

Do not prematurely implement high-level builtins before the value model is stable enough.

## Stage C — Parser/compiler/executor

Port/generate:

```text
lexer
parser
bytecode/opcodes
compiler
execution engine
scope/block infrastructure
```

Keep source correspondence very high in these areas.

These are correctness-critical.

## Stage D — Builtins and jq-language builtin resource

Port native builtins.

Reuse/embed:

```text
src/builtin.jq
```

unless a concrete reason requires transformation.

## Stage E — Modules/linking/filesystem semantics

Port library-visible module/link semantics.

Use a controlled .NET filesystem abstraction so hosted deployments can disable or constrain filesystem access without changing jq semantics internally.

## Stage F — Regex

Implement the jq regex compatibility adapter over `.NET Regex`.

Run dedicated upstream regex suites.

Fix compatibility gaps one by one.

## Stage G — Full regression closure

Loop:

```text
run compatibility suite
   ↓
pick one deterministic failure cluster
   ↓
trace to upstream function/file
   ↓
compare C and C#
   ↓
fix
   ↓
add regression test if needed
   ↓
repeat
```

Do not make broad speculative rewrites in this phase.

---

# 16. Change scope

Tasks should be local enough to reason about from direct source correspondence.

Good task:

> Port `src/bytecode.c` and `src/bytecode.h` to their mapped `.cs` files. Preserve upstream symbols. Use existing mapped `jv`/opcode types. Run the relevant unit/compatibility tests and update the manifest.

Bad task:

> Rewrite jq in C#.

Good regression task:

> `tests/jq.test` cases 410–436 fail in update-assignment semantics. Compare `src/execute.c`/`src/compile.c` at jq-1.8.2 with the mapped C# functions. Fix the smallest semantic divergence and add a regression test.

---

# 17. Required behavior when replacing code with .NET facilities

A replacement is allowed only behind a compatibility boundary.

The replacement must include a comment containing:

```text
UPSTREAM COMPONENT
REPLACEMENT
WHY
BEHAVIORAL CONTRACT
KNOWN DIFFERENCES
TESTS COVERING THE SUBSTITUTION
```

Example:

```csharp
// DOTNETJQ PROXY
// Upstream: jq regex operations backed by Oniguruma
// Replacement: System.Text.RegularExpressions
// Contract: reproduce jq-facing match/test/capture/sub/gsub behavior.
// Known differences: none currently accepted.
// Verification: upstream onig.test + manonig.test + local regressions.
```

Never scatter direct calls to a replacement API throughout unrelated files.

Bad:

```text
compile.c.cs directly uses Regex
execute.c.cs directly uses Regex
builtin.c.cs directly uses Regex
```

Good:

```text
jq-facing regex calls
        ↓
JqRegex compatibility adapter
        ↓
System.Text.RegularExpressions
```

---

# 18. Comments and upstream links

The project should be unusually rich in source-correspondence comments because that directly lowers future maintenance and review costs.

At function level, add a mapping comment when:

- implementation differs materially from upstream;
- a C memory-management action is intentionally a no-op in managed code;
- a .NET primitive replaces an upstream subsystem;
- an upstream macro becomes a helper;
- control flow was intentionally simplified;
- compatibility is non-obvious.

Example:

```csharp
// Upstream: src/jv.c :: jv_array_get
// Semantics intentionally mirror jq 1.8.2.
// C refcount transfer is represented by managed value semantics here.
internal static jv jv_array_get(...)
{
    ...
}
```

Do not add noisy comments to every obvious translated statement.

---

# 19. Licensing

jq is MIT-licensed.

Official license/source:

https://github.com/jqlang/jq/blob/jq-1.8.2/COPYING

The repository also contains code/assets with additional notices, including decNumber under ICU terms and other notices described in `COPYING`.

Requirements:

1. keep a copy of the upstream jq license/notices in the project;
2. add `THIRD_PARTY_NOTICES.md`;
3. preserve required copyright/license notices for ported or reused code;
4. preserve notices for any upstream third-party code that is actually ported/reused;
5. do not assume that replacing a subsystem means all historical notices can be deleted without review;
6. perform a final dependency/license audit before public distribution.

This specification is engineering guidance, not legal advice.

---

# 20. Updating to future jq versions

Do not merge upstream changes ad hoc.

Use a deterministic upgrade procedure.

Example:

```bash
git -C upstream/jq fetch --tags
git -C upstream/jq diff jq-1.8.2..jq-X.Y -- src tests
```

Then:

1. create an upstream-change inventory;
2. map every changed upstream file to its target;
3. port changes file-by-file;
4. import changed/new tests first when practical;
5. rerun full suite;
6. update upstream revision in every generated file header and manifest only when the upgrade is complete;
7. never claim compatibility with a newer jq version while only partially ported.

---

# 21. Performance policy

Correctness first.

Do not significantly redesign jq evaluation in the initial port solely for benchmark performance.

After compatibility:

1. benchmark representative hosted reducer workloads;
2. profile allocations;
3. optimize hot paths;
4. preserve differential compatibility tests.

Useful later optimizations may include:

- compiled/cached jq programs;
- pooling;
- low-allocation `jv` internals;
- `Span<T>`;
- efficient object/array representations;
- avoiding needless JSON round-trips.

But parity is the first milestone.

---

# 22. Host safety features to add after jq parity

The library may execute dynamically generated or otherwise untrusted reducers.

Therefore the public execution layer should eventually support hard limits independent of upstream jq's normal CLI assumptions.

Target options:

```csharp
new JqExecutionOptions
{
    Timeout = ...,
    MaxInputBytes = ...,
    MaxOutputBytes = ...,
    MaxOutputValues = ...,
    MaxRecursionDepth = ...,
    MaxGeneratedValues = ...,
    RegexTimeout = ...,
    CancellationToken = ...
};
```

These limits belong in a controlled execution layer and must not compromise ordinary jq compatibility when disabled/defaulted appropriately.

Host integrations should normally execute untrusted reducers with restrictive limits.

---

# 23. Repository contributor rules

The implementation repository should include short mandatory contributor rules.

Suggested content:

```text
# DotNetJq Contributor Rules

1. Upstream jq-1.8.2 commit 34f7186 is the semantic source of truth.
2. Never redesign the compatibility core without an explicit task.
3. Preserve upstream file mappings and function names.
4. Read PORTING_MANIFEST.json before making changes.
5. Every upstream file must be PORT/PROXY/GENERATED/REUSE/OMITTED.
6. Never delete a mapped symbol merely because it appears unused until upstream
   behavior/tests confirm it is unnecessary.
7. Proxies must remain behind jq-shaped compatibility interfaces.
8. Official jq tests and differential execution are the correctness oracle.
9. Never weaken/delete a failing compatibility test to make the build green.
10. Never shell out to jq in production code.
11. The official jq binary may be used only in tests as an oracle.
12. Update manifest/status/comments in the same change as implementation.
13. Prefer small file/function-local fixes over broad refactors.
14. Build and run relevant tests after every porting task.
15. Before claiming completion, run the complete applicable compatibility suite.
```

---

# 24. Parallelism and integration strategy

A single lead maintainer should own the project state and integration.

Parallel work is optional, not required.

Do not initially split interdependent core files across independent changes. Stabilize foundations first:

```text
lead maintainer
   ↓
bootstrap + manifest + test harness
   ↓
jv/value semantics
   ↓
parser/compiler/bytecode/executor foundations
   ↓
broad compatibility suite functioning
   ↓
optional parallel leaf work
```

Independent contributors or worktrees may later be used for isolated areas such as:

```text
Unicode / UTF-8
base64 / URI helpers
regex compatibility
module/file fixtures
independent builtin clusters
test-harness improvements
regression investigation
```

Rules for parallel work:

1. The lead maintainer remains the integration owner.
2. Each subtask receives a narrow mapped source scope.
3. Each subtask must update its manifest entries.
4. Each subtask runs focused tests before handoff.
5. The lead maintainer reruns the broad compatibility suite after integration.
6. Core semantic ownership must not become ambiguous across multiple contributors.
7. Do not use parallelism merely to increase activity; use it only when dependencies are genuinely separable.

A sequential implementation remains an acceptable baseline.


# 25. Definition of "done"

The project is NOT done when:

```text
it builds
basic jq examples work
most tests pass
```

The initial compatibility milestone is done when:

1. upstream jq revision is pinned;
2. every upstream source artifact is explicitly mapped;
3. the library contains no hidden native jq evaluator dependency;
4. applicable official jq semantic tests pass;
5. regex compatibility is quantified and gaps are explicit;
6. CLI-only exclusions are documented;
7. differential oracle testing finds no unexplained mismatch in the supported surface;
8. source/file/function traceability is preserved;
9. public .NET API works independently of the upstream source checkout;
10. licensing/notices are present;
11. host execution limits can be layered on without redesigning the evaluator.

---

# 26. Key external references

## jq

Repository:

https://github.com/jqlang/jq

Pinned release:

https://github.com/jqlang/jq/releases/tag/jq-1.8.2

Build/source inventory:

https://github.com/jqlang/jq/blob/jq-1.8.2/Makefile.am

README/build instructions:

https://github.com/jqlang/jq/blob/jq-1.8.2/README.md

License/notices:

https://github.com/jqlang/jq/blob/jq-1.8.2/COPYING

Tests:

https://github.com/jqlang/jq/tree/jq-1.8.2/tests

Source:

https://github.com/jqlang/jq/tree/jq-1.8.2/src

jq manual:

https://jqlang.org/manual/

---

# 27. Final implementation principle

Treat this project as a **behavior-preserving managed port**, not a redesign.

The governing principle is:

> When a contributor opens a C# file, it should be immediately obvious which jq file and functions it corresponds to, what was ported, what was proxied, what was generated, what was omitted, and which tests establish equivalence.

When there is a choice between:

```text
a clever new architecture
```

and:

```text
a boring implementation whose relationship to upstream jq is obvious
```

choose the second until compatibility is complete.

The project's main optimization target is not elegance of the first implementation.

It is:

```text
minimal maintainer context
+
maximum upstream traceability
+
executable semantic verification
+
easy future jq upgrades
```

That is the architecture.
