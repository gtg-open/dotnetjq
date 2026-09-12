# Embedded builtin completeness audit

Audit date: 2026-09-05. Semantic source: jq 1.8.2 commit
`34f7186b86743a083a589741b6cea95293524108`.

## Final result

- `src/DotNetJq/Resources/builtin.jq` is byte-for-byte identical to the pinned
  upstream `src/builtin.jq`: 9,631 bytes with SHA-256
  `b8a5fd9579be9b51c9a04e6620f8c1655539aa57eea33a84e202a8dea401f2a4`.
- The resource contains **106 top-level definitions and 106 unique
  signatures**.
- **All 106 definitions are source-bound** into every compiled program. There
  is no selective allowlist and no synthetic `loadedByEngine` result.
- Pinned jq advertises **226 public signatures**. Managed `builtins/0`
  advertises the same **226 unique signatures**, with **zero missing and zero
  extra**. Private names remain excluded.
- Every public signature has a managed execution route. Public halt, input,
  input-position, origin/search-list, module metadata, stderr/debug, and the
  jq 1.8.2 special-math signatures are included.
- The parsed source has exactly seven unresolved private dependencies; all
  seven are implemented by the direct jq-shaped C-function table.
- The former managed AST builtin catalog and its native compatibility
  fallbacks have been removed. Production binding now uses jq's ordered
  `block`/`inst` binder directly; focused tests inspect the bound closure
  instructions rather than inferring source use from equal output.

## Public `builtins/0` inventory

The reference inventory was captured from the pinned jq 1.8.2 executable and
compared as a set with the managed result.

| Measurement | Pinned jq | Managed | Delta |
| --- | ---: | ---: | ---: |
| Unique public signatures | 226 | 226 | 0 |
| Missing signatures | - | 0 | 0 |
| Extra signatures | - | 0 | 0 |
| Private signatures beginning with `_` | 0 | 0 | 0 |

`builtins/0` is synthesized from the complete managed callable inventory, as
upstream does after binding its jq-coded and native layers. It is not defined
by `builtin.jq` itself.

## Classification of all 106 resource definitions

### Source-bound: 106

Line numbers refer to `src/DotNetJq/Resources/builtin.jq`.

`halt_error/0` (L1), `error/1` (L2), `map/1` (L3), `select/1` (L4),
`sort_by/1` (L5), `group_by/1` (L6), `unique_by/1` (L7), `max_by/1` (L8),
`min_by/1` (L9)

`add/1` (L10), `add/0` (L11), `del/1` (L12), `abs/0` (L13),
`_assign/2` (L14), `_modify/2` (L15), `map_values/1` (L34),
`recurse/1` (L37), `recurse/2` (L38), `recurse/0` (L39)

`to_entries/0` (L41), `from_entries/0` (L42), `with_entries/1` (L44),
`reverse/0` (L45), `indices/1` (L46), `index/1` (L50), `rindex/1` (L51),
`paths/0` (L52), `paths/1` (L53)

`isfinite/0` (L54), `arrays/0` (L55), `objects/0` (L56), `iterables/0` (L57),
`booleans/0` (L58), `numbers/0` (L59), `normals/0` (L60), `finites/0` (L61),
`strings/0` (L62), `nulls/0` (L63), `values/0` (L64), `scalars/0` (L65)

`join/1` (L66), `_flatten/1` (L70), `flatten/1` (L71), `flatten/0` (L72),
`range/1` (L73), `fromdateiso8601/0` (L74), `todateiso8601/0` (L75),
`fromdate/0` (L76), `todate/0` (L77), `ltrimstr/1` (L78),
`rtrimstr/1` (L79), `trimstr/1` (L80)

`match/2` (L81), `match/1` (L82), `test/2` (L86), `test/1` (L87),
`capture/2` (L91), `capture/1` (L92), `scan/2` (L96), `scan/1` (L102),
`splits/2` (L105), `splits/1` (L108), `split/2` (L111), `sub/3` (L115),
`sub/2` (L128), `gsub/3` (L130), `gsub/2` (L131)

`while/2` (L135), `until/2` (L139), `limit/2` (L143), `skip/2` (L147),
`range/3` (L152), `first/1` (L156), `isempty/1` (L157), `all/2` (L158),
`any/2` (L159), `all/1` (L160), `any/1` (L161), `all/0` (L162),
`any/0` (L163), `nth/2` (L164), `first/0` (L167), `last/0` (L168),
`nth/1` (L169)

`combinations/0` (L170), `combinations/1` (L176), `transpose/0` (L182),
`in/1` (L183), `inside/1` (L184), `repeat/1` (L185), `inputs/0` (L189),
`ascii_downcase/0` (L191), `ascii_upcase/0` (L194)

`truncate_stream/1` (L198), `fromstream/1` (L200), `tostream/0` (L208),
`walk/1` (L214), `pick/1` (L225), `debug/1` (L231), `INDEX/2` (L234),
`INDEX/1` (L236), `JOIN/2` (L237), `JOIN/3` (L239), `JOIN/4` (L241),
`IN/1` (L243), `IN/2` (L244)

### Unavailable: 0

The formerly unavailable exact source signatures are now directly callable:

- `_assign/2` and `_modify/2` execute the unchanged jq-coded path/update
  definitions, including the three `$$$$`/`LOADVN` operations in `_modify`.
- `_flatten/1` resolves to and executes the canonical source definition through
  the ordinary direct compiler and bytecode VM. There is no identity-based
  managed acceleration or interception of a user-defined `_flatten`.
- `halt_error/0`, `inputs/0`, and `debug/1` execute their source wrappers over
  the managed stateful native primitives.

## Exact private/native primitive boundary

The definition-only parser records calls that are not satisfied by earlier or
self definitions in the ordered source. The complete private dependency set is:

| Private primitive | Source consumers | Managed route |
| --- | --- | --- |
| `_group_by_impl/1` | `group_by/1` | Native keyed grouping primitive |
| `_match_impl/3` | `match/2`, `test/2`; transitively regex wrappers | Native jq-shaped regex match-array/test primitive |
| `_max_by_impl/1` | `max_by/1` | Native keyed maximum primitive |
| `_min_by_impl/1` | `min_by/1` | Native keyed minimum primitive |
| `_sort_by_impl/1` | `sort_by/1` | Native keyed sort primitive |
| `_strindices/1` | `indices/1` | Native string-index primitive |
| `_unique_by_impl/1` | `unique_by/1` | Native keyed uniqueness primitive |

These seven signatures participate in compile-time resolution but are not
advertised by public `builtins/0`. `_flatten/1` has no separate managed
fallback: it is the embedded source definition compiled to bytecode.

The public source wrappers similarly delegate to native jq-shaped state
primitives where upstream does: `halt_error/0` to `halt_error/1`, `inputs/0`
to `input/0`, and `debug/1` to `debug/0`. None of those routes read ambient
stdin, write ambient stderr, terminate the process, or shell out to jq.

## Binding and precedence evidence

- The exact resource is opened by manifest name from `typeof(libjq).Assembly`;
  no filesystem lookup or upstream checkout participates.
- `builtins_bind` passes the exact UTF-8 bytes through `locfile` to
  `jq_parse_library`, which emits direct `block`/`inst` IR and enforces the
  definitions-only contract.
- `bind_bytecoded_builtins`, `gen_cbinding`, and `gen_builtin_list` follow the
  upstream binding order before `block_bind_referenced` binds that library
  into the user program.
- The 106 top-level source closures retain jq's ordinary lexical source order;
  nested definitions remain nested, and user/module shadowing is resolved by
  the same block binder used for all other jq functions.
- Tests inspect all 106 bound `CLOSURE_CREATE` instructions and prove source
  precedence structurally. Equal output from another implementation cannot
  satisfy that proof.

## Final verification checkpoint

| Gate | Result |
| --- | ---: |
| Focused embedded-resource/binding tests | 5/5 |
| Regex and Oniguruma focused slice | 1,029/1,029 |
| Official `jq.test` | 550/550 |
| Official auxiliary fixtures | 329/329 |
| All seven official fixtures combined | 879/879 |
| General deterministic differential corpus | 5,040/5,040 |
| Regex deterministic differential corpus | 4,980/4,980 across 33 categories; 4,981 rejected |
| Independent regex audit | 92,589/92,589 primary rows; 62/62 isolated processes |
| Callout owner slice / differential category | 37/37 / 93/93 |
| Solution rebuild | 0 warnings, 0 errors |
| Isolated NuGet consumer without checkout/system jq/network | Passed |

The completed differential runs used the pinned jq 1.8.2 binary and no
allowlist; the general seed was `18082` and the regex seed was `1808202`. The
regex generator declares 4,980 distinct cases across 33 categories and rejects
4,981. The independent audit additionally records zero process mismatch, crash,
abort, unhandled exception, or timeout. This evidence qualifies the managed
jq-shaped regex surface, not native Oniguruma API, bytecode, allocator, engine,
or ABI identity.
