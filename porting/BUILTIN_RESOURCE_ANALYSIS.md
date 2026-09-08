# Embedded `builtin.jq` integration analysis

Analysis date: 2026-09-06. Semantic source: jq 1.8.2 commit
`34f7186b86743a083a589741b6cea95293524108`.

## Final conclusion

The embedded jq library is now a production input to compilation, not a
reference-only artifact. The engine loads the exact assembly-resource bytes,
parses them as a definition-only jq library to direct `block`/`inst` IR, and
binds all **106 of 106** ordered top-level definitions into every compiled
program. No definition allowlist, AST catalog, or filesystem fallback is used.

The managed public inventory now matches pinned jq exactly: **226 of 226**
unique public signatures, with no missing or extra entries. All seven official
fixtures, the 5,040/5,040 general differential corpus, and the complete declared
4,980/4,980 regex differential inventory pass with full source binding enabled.
The independent frozen-artifact regex audit also passes 92,589/92,589 primary
rows and 62/62 isolated processes.

## Source identity and loading boundary

`src/DotNetJq/Resources/builtin.jq` is byte-for-byte identical to the pinned
`upstream/jq/src/builtin.jq`:

```text
length: 9631 bytes
sha256: b8a5fd9579be9b51c9a04e6620f8c1655539aa57eea33a84e202a8dea401f2a4
top-level definitions: 106
unique top-level signatures: 106
```

The runtime loader uses the exact manifest name
`DotNetJq.Resources.builtin.jq` against the DotNetJq assembly. It copies the
resource bytes from the manifest stream once and retains those bytes. For each
`builtins_bind` call it passes them through `locfile` to `jq_parse_library`,
which applies the generated parser and emits direct compiler IR. It never
reads a `.jq` file from disk and does not depend on
`upstream/jq`.

The bytes are immutable and cached; parsed IR is not shared. Each compiled
program receives fresh `block`/`inst` closure and constant owners, preventing
mutable binding or execution state from leaking between jq states.

## Parser and `LOADVN` prerequisite

The unchanged resource contains three `$$$$name` terms in `_modify/2`.
Upstream lowers this private syntax to `LOADVN`: read the value without
retaining it and leave `null` in the consumed variable slot.

The managed lexer emits three literal-dollar tokens followed by one binding
token. The generated parser emits the direct `LOADVN` instruction against the
bound variable, so the direct compiler/VM implements the observable
consume-and-null behavior across backtracking, closures, generators, and
shadowed variables. The resource is parsed without normalization or textual
rewriting.

`jq_parse_library` also enforces the upstream definitions-only contract. A
library main expression is rejected rather than being discarded.

## Binding algorithm and shadowing order

The engine deliberately does not prefix `builtin.jq` text to user source,
which would shift user diagnostics. Compilation follows the upstream binding
shape:

1. Parse/link user and module source to direct `block`/`inst` IR with
   `<top-level>` and file-specific locations.
2. Parse the embedded library bytes through `jq_parse_library` and validate
   that the result contains only binders/imports.
3. Prepend jq's bytecoded `empty`, `not`, `path`, `last`, and `range`
   definitions.
4. Add the expanded 136-entry direct C-function table with `gen_cbinding`.
5. Generate `builtins/0` from the complete bound inventory.
6. Call `block_bind_referenced` so ordinary jq lexical binding and shadowing
   rules bind the library into the program.

This is the same block binder used for ordinary jq functions; there is no
separate managed scope algorithm or runtime-function catalog.

Focused tests prove precedence structurally:

- every one of the 106 `(name, arity)` pairs resolves to its parsed source
  `CLOSURE_CREATE` binding;
- the source-private unresolved dependency set is exactly the expected seven
  signatures;
- a user-defined `map/1` shadows the embedded definition;
- generated operator calls do not masquerade as explicit private source calls;
- private native primitives are bound only through the direct C-function
  layer.

## Native compatibility boundary

Full source binding retains the same separation as upstream
`builtins_bind()`: jq-coded wrappers sit above low-level native primitives.
The parsed library has exactly seven unresolved private native calls:

| Primitive | Consumed by | Result shape |
| --- | --- | --- |
| `_sort_by_impl/1` | `sort_by/1` | Values ordered by precomputed key arrays |
| `_group_by_impl/1` | `group_by/1` | Values grouped by precomputed key arrays |
| `_unique_by_impl/1` | `unique_by/1` | Values deduplicated by precomputed key arrays |
| `_min_by_impl/1` | `min_by/1` | Minimum value selected by key array |
| `_max_by_impl/1` | `max_by/1` | Maximum value selected by key array |
| `_strindices/1` | `indices/1` | String match offsets |
| `_match_impl/3` | `match/2`, `test/2` | Match-object array or boolean test result |

All seven are implemented locally, accepted by compile-time resolution, and
excluded from public `builtins/0`. `_match_impl/3` preserves jq's true-only
test-mode switch, global match arrays, capture objects, and type diagnostics.

The source-defined private helpers `_assign/2`, `_modify/2`, and `_flatten/1`
are not replaced by that primitive table. They resolve and execute from the
embedded library through the direct compiler and bytecode VM. There is no
identity-based acceleration or user-function interception.

Public jq-coded state wrappers also remain source-selected:

- `halt_error/0` calls managed native `halt_error/1`, which records halt state
  without terminating the host process;
- `inputs/0` repeats managed native `input/0`, which reads only an explicitly
  supplied input capability;
- `debug/1` delegates to managed native `debug/0` and an explicit debug sink;
- metadata, origin, input-position, stderr, and math calls remain jq-shaped
  native primitives where upstream defines them natively.

The former `NativeBuiltins` AST catalog has been removed. The complete public
`builtins/0` inventory is generated from the direct source and C-function
blocks, while names beginning with `_` remain private.

## Regressions exposed and closed by full binding

Earlier full-binding experiments correctly revealed gaps that native public
reimplementations had hidden. Integration was not declared complete until the
source definitions ran without selecting only easy entries.

| Exposed area | Root semantic gap | Final closure |
| --- | --- | --- |
| `indices/index/rindex` | Missing jq array-by-array subsequence indexing | Added `jv_array_indexes`, including overlap and deep equality |
| `del`, `path`, streaming path helpers | Incomplete source-function path tracing and slice deletion | Direct block compilation and the VM path opcodes preserve transformed-value diagnostics and simultaneous slice deletion |
| `limit`, `nth` | Eager compatibility-evaluator source consumption evaluated later errors | Removed the compatibility evaluator; direct bytecode execution preserves jq backtracking and break timing |
| `splits`, `split`, regex wrappers | Missing `_match_impl/3` and source stream edge behavior | Added private match primitive and corrected stream-state propagation |
| `_modify/2` | Private `$$$$` loads were previously unavailable | Implemented exact `LOADVN` consume-and-null semantics |
| deep `_flatten/1` | The former AST path used a non-jq acceleration | Removed that path; `_flatten` now follows the ordinary source/bytecode route |
| module consumers | Imported closures began from an empty compatibility scope | Direct linker block binding supplies the same builtin closure graph as upstream |

No failing compatibility test was weakened or removed during this closure.

## Final measured verification

| Gate | Result |
| --- | ---: |
| Exact resource hash/count and binding tests | 5/5 |
| Regex and Oniguruma focused slice | 1,029/1,029 |
| `jq.test` | 550/550 |
| `man.test` | 231/231 |
| `onig.test` | 47/47 |
| `manonig.test` | 19/19 |
| `base64.test` | 10/10 |
| `uri.test` | 20/20 |
| `optional.test` | 2/2 |
| General differential corpus, seed `18082` | 5,040/5,040 |
| Regex differential corpus, seed `1808202` | 4,980/4,980 across 33 categories; 4,981 rejected |
| Independent regex audit | 92,589/92,589 primary rows; 62/62 isolated processes |
| Callout owner slice / differential category | 37/37 / 93/93 |
| Solution rebuild | 0 warnings, 0 errors |
| Isolated package consumer | Passed |

Both completed differential campaigns used the pinned jq 1.8.2 executable and
contained no allowlist or excluded mismatch. The regex generator declares
4,980 distinct cases across 33 categories and rejects 4,981. This proves the
jq-visible proxy surface exercised by those cases, not native Oniguruma API,
bytecode, allocator, engine, or ABI identity. Package isolation ran with the upstream
checkout hidden, `/usr/bin/jq` unusable, and network disabled, proving that
production compilation consumes only the embedded assembly resource.
