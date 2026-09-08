# Direct PORT symbol audit

Audit source: jq 1.8.2 commit
`34f7186b86743a083a589741b6cea95293524108` in
the upstream checkout exposed as `upstream/jq`.

This audit covers the direct `PORT` mappings for `jv.c`, `jv.h`, `jv_aux.c`,
`jv_private.h`, `linker.c`, `linker.h`, `locfile.c`, `locfile.h`, `util.c`,
`util.h`, `jq_parser.h`, and the `jq_test.c` harness. It also checks the
adjacent `jv_unicode.c`/`.h` proxy because the `jv` port calls that jq-shaped
UTF-8 surface. Parser/lexer implementation, compiler/VM structure, builtins,
libm, regex, and CLI behavior are outside this audit.

## Result

No unclassified library-facing value, path, module-linking, source-location,
or Unicode symbol remains in the audited direct PORT surface.

The production route is the jq-shaped compiler and VM route: generated grammar
actions build the direct `block`/`inst` graph, the linker and builtin binder
operate on that graph, `block_compile` plus `optimize` produce state-owned
`bytecode`, and `jq_start`/`jq_next` execute it in the direct frame/fork opcode
dispatcher. The representation adaptations below do not introduce a parallel
execution engine.

The audit initially found missing jq-shaped entry points in four meaningful
groups. They are now present and directly tested:

- invalid payload, literal-number, bit-identical-value, and source-shaped
  allocated-value reference counting (`jv_invalid_get_msg`, `jv_invalid_has_msg`,
  `jv_number_with_literal`, `jv_number_value`, `jv_is_integer`,
  `jv_number_has_literal`, `jv_number_get_literal`, `jv_identical`, and
  `jv_get_refcnt`);
- array, UTF-8 string, object merge, and object iteration primitives;
- `jv_aux.c` raw get/set/has, path, deletion, keys, stable sort, group, and
  unique primitives;
- `linker.c`'s `load_program`, `load_module_meta`, `process_dependencies`, and
  `load_library` paths over the direct `block`/`inst` compiler representation.

`jv.h` declares or defines 93 unique `jv_*` function names (counting the
header-inline `jv_is_valid` and counting duplicate declarations once). A
repository symbol scan now finds 86 of them. The seven absent names are
classified below; five belong to
separately mapped parser/printer proxies, and two are deliberately omitted C
implementation surfaces. This count is a name-presence check, not a claim that
the separately mapped proxy files have full flag parity.

## File-by-file closure

| Upstream source | Managed disposition |
|---|---|
| `src/jv.c`, `src/jv.h` | All jq value-semantic constructors, inspectors, array/string/object operations, equality/identity/contains, object iteration, and recursive merge have jq-shaped counterparts. Allocated invalids, literal numbers, strings, arrays, and objects use explicit jq-shaped logical reference counts; strings retain UTF-8 byte storage and arrays/objects retain source-shaped capacity and copy-on-write behavior. |
| `src/jv_aux.c` | All twelve exported names are present: `jv_get`, `jv_set`, `jv_has`, `jv_setpath`, `jv_getpath`, `jv_delpaths`, `jv_keys`, `jv_keys_unsorted`, `jv_cmp`, `jv_sort`, `jv_group`, and `jv_unique`. Stable sort uses the upstream original-index tie break. |
| `src/jv_private.h` | `jvp_number_cmp` and `jvp_number_is_nan` are present in the mapped header target. |
| `src/jv_unicode.c`, `src/jv_unicode.h` | All seven exported UTF-8/whitespace names are present behind span/offset signatures. `UnicodeCompatibilityTests` covers malformed advancement, backtracking, scalar encoding, whitespace, and byte/scalar offsets. |
| `src/linker.c`, `src/linker.h` | The complete linker surface is present over direct `block`/`inst` IR: `load_program`, `load_module_meta`, `process_dependencies`, `load_library`, `lib_entry`, `lib_loading_state`, path validation/search helpers, recursive loading, caching/cycle state, and jq-shaped block binding. `JqModuleResolver` is the explicit managed filesystem/search capability. |
| `src/locfile.c`, `src/locfile.h` | `location`, `UNKNOWN_LOCATION`, `locfile`, and all five exported `locfile_*` names are present. Byte positions, line maps, retain/free lifecycle, and diagnostics are covered by source-location tests. |
| `src/util.c`, `src/util.h` | `expand_path`, `get_home`, `jq_realpath`, `_jq_memmem`, and `priv_fwrite` are present. Production module/file resolution remains behind explicit capabilities; no ambient input capability was added. |
| `src/jq_parser.h` | `jq_parse` and `jq_parse_library` are supplied by the generated parser mapping with the native-shaped `locfile` plus `out block` boundary. Their semantic actions return the direct owning `block`/`inst` compiler graph. |
| `src/jq_test.c` | The fixture grammar is ported in `UpstreamTestFile`; compile/runtime outcome checks and the final superfluous-output probe are in the managed compatibility runner. Reset/recompile/reuse and value self-tests are separate focused xUnit tests instead of one native `jq_testsuite` function. |

## Exhaustive symbol ledger

This ledger was generated from the pinned source rather than from call-site
reachability. Every function defined by these files, every function declared
by their audited headers, every source-declared type/enum, and every
preprocessor macro is classified below. A row that contains several
identifiers gives the same exact disposition to every semicolon-separated
identifier in that row. Duplicate declarations (notably `jv_get_refcnt`) are
listed once. Header guards and conditional Windows/libc branches are included
so that no platform or private-symbol category is left implicit. Included
system declarations and calls are external dependencies rather than port-owned
symbols. File-local data instances and lookup tables were also examined; their
disposition follows their owning representation/function row because they do
not define independent entry points.

### `src/jv.c`

Functions:

| Upstream symbols | Disposition |
|---|---|
| `jv_get_kind`; `jv_kind_name`; `jv_true`; `jv_false`; `jv_null`; `jv_bool`; `jv_invalid_with_msg`; `jv_invalid`; `jv_invalid_get_msg`; `jv_invalid_has_msg` | Exact jq-shaped managed names in `jv.c.cs`; an invalid with a message has a separately reference-counted outer `jvp_invalid` allocation that owns its arbitrary `jv` message. Copying changes only the outer count; `has_msg` and `get_msg` consume the wrapper, and extraction returns a copied message owner. |
| `jv_number`; `jv_number_with_literal`; `jv_number_value`; `jv_is_integer`; `jv_number_abs`; `jv_number_negate`; `jv_number_has_literal`; `jv_number_get_literal`; `jvp_number_cmp`; `jvp_number_is_nan` | Exact jq-shaped managed names in `jv.c.cs`/`jv_private.h.cs`. `JvNumber` plus `ManagedDecimal` preserves native-double versus decimal-literal representation and exact literal comparison. |
| `jvp_number_is_literal`; `jvp_literal_number_ptr`; `jvp_dec_number_ptr`; `jvp_literal_number_alloc`; `jvp_literal_number_new`; `jvp_literal_number_to_double`; `jvp_literal_number_literal`; `jvp_number_equal` | Folded into `JvNumber`, `ManagedDecimal`, `jv_number_with_literal`, equality, and conversion methods. These are private access/allocation helpers, not missing entry points. |
| `jvp_number_free` | `jv_free` decrements the explicit `JvNumber.Refcnt` for decimal-literal allocations and invalidates their storage on the final owner; native binary64 numbers remain immediate scalar handles. |
| `jv_tsd_dec_ctx_init`; `jv_tsd_dec_ctx_fini`; `tsd_dec_ctx_get` | Replaced by isolated managed decimal contexts (`CreateJqDecimalContext` and immutable number state); native thread-local allocation/finalization is unnecessary. |
| `jvp_refcnt_inc`; `jvp_refcnt_dec`; `jvp_refcnt_unshared` | Exact source-shaped logical operations over managed `jv_refcnt`; they drive literal-number lifetime and copy-on-write for allocated strings, arrays, and objects. A public `JqProgram` owns one jq state and rejects overlapping runs, so shared-program concurrency is not used to justify non-source refcount behavior. Independent jq states may still execute concurrently. The CLR only performs physical reclamation after the logical release. |
| `imax`; `jvp_array_ptr`; `jvp_array_alloc`; `jvp_array_new`; `jvp_array_length`; `jvp_array_offset`; `jvp_array_read`; `jvp_array_write`; `jvp_array_equal`; `jvp_clamp_slice_params`; `jvp_array_contains`; `jvp_array_slice` | Mapped around explicit `jvp_array` storage and the corresponding public array/equality/contains/slice methods. Per-handle offset/size views, initialized length, allocation capacity, 3/2 growth, shared slicing, unique in-place writes, and copy-on-write detachment retain the jq storage behavior without exposing C pointers. |
| `jv_array_sized`; `jv_array`; `jv_array_length`; `jv_array_get`; `jv_array_set`; `jv_array_append`; `jv_array_concat`; `jv_array_slice`; `jv_array_indexes` | Exact jq-shaped managed names in `jv.c.cs`; construction identity, clamping, matching, and append/set behavior are tested. |
| `jvp_string_ptr`; `jvp_string_alloc`; `jvp_string_new`; `jvp_string_empty_new`; `jvp_string_free`; `jvp_string_length`; `jvp_string_remaining_space`; `jvp_string_append`; `jvp_string_equal` | Mapped around reference-counted `jvp_string` storage whose source of truth is a UTF-8 byte array with logical byte length, allocation capacity, and an excluded trailing NUL. Unique appends reuse spare capacity; shared or undersized appends detach with jq's growth rule. Equality and all value-core operations use raw bytes; a CLR `string` is decoded lazily only for managed compatibility views. |
| `jvp_string_copy_replace_bad` | `jv_string_sized` drives `jvp_utf8_next` and inserts U+FFFD with upstream byte advancement; malformed UTF-8 cases are tested. |
| `jvp_hash_seed_init`; `jvp_hash_seed`; `rotl32`; `jvp_string_hash`; `jv_string_hash` | Mapped by the process-randomized managed seed, source-shaped MurmurHash3 calculation over raw UTF-8 bytes, and per-allocation cached hash bit/value. The public `jv_string_hash` consumes its string owner as upstream does. |
| `jv_string_sized`; `jv_string_empty`; `jv_string`; `jv_string_length_bytes`; `jv_string_length_codepoints`; `jv_string_indexes`; `jv_string_repeat`; `jv_string_split`; `jv_string_explode`; `jv_string_implode`; `jv_string_value`; `jv_string_slice`; `jv_string_concat`; `jv_string_append_buf`; `jv_string_append_codepoint`; `jv_string_append_str` | Exact jq-shaped managed names in `jv.c.cs`; byte/scalar indexing, malformed input, overlapping indexes, empty separators, repeat, slices, and appends are directly or fixture-tested. |
| `jv_string_vfmt`; `jv_string_fmt` | Deliberately omitted C `va_list`/variadic ABI. Managed call sites use typed formatting; `locfile_locate` supplies the only required narrow C-format compatibility adapter. |
| `jvp_object_new`; `jvp_object_ptr`; `jvp_object_mask`; `jvp_object_size`; `jvp_object_buckets`; `jvp_object_find_bucket`; `jvp_object_get_slot`; `jvp_object_next_slot`; `jvp_object_find_slot`; `jvp_object_add_slot`; `jvp_object_read`; `jvp_object_rehash`; `jvp_object_unshare`; `jvp_object_write`; `jvp_object_delete`; `jvp_object_length`; `jvp_object_equal`; `jvp_object_contains` | Mapped around explicit `jvp_object`, `object_slot`, and bucket arrays. Physical slot order, owning raw-UTF-8 `jv` keys and owning values, cached slot hashes, bucket chains, tombstones, monotonic `next_free`, capacity doubling/rehash, and copy-on-write unsharing retain the jq data-structure and ownership mechanics. |
| `jv_object`; `jv_object_get`; `jv_object_has`; `jv_object_set`; `jv_object_delete`; `jv_object_length`; `jv_object_merge`; `jvp_object_merge_recursive`; `jv_object_merge_recursive`; `jv_object_iter_valid`; `jv_object_iter`; `jv_object_iter_next`; `jv_object_iter_key`; `jv_object_iter_value` | Exact jq-shaped public names; the private recursive worker is represented by the iterative merge engine. Missing-key sentinel, insertion order, overwrite, merge, iteration, and 10,000-level depth behavior are tested. |
| `jv_copy`; `jv_free`; `jv_get_refcnt`; `jvp_equal`; `jv_equal`; `jv_identical`; `jvp_contains`; `jv_contains` | Public names are exact. `jv_copy` increments only the outer allocated invalid/string/array/object storage, while `jv_free` decrements it and, at zero, iteratively releases owned children. `jv_get_refcnt` reports the live allocation count (unallocated scalars report one); equality, identity, and containment consume their arguments and preserve allocation/representation identity rules. |
| `w32_service_thread_detach`; `DllMain`; `pthread_key_create`; `key_lookup`; `pthread_setspecific`; `pthread_getspecific` | Conditional Win32 TLS emulation only. The CLR supplies thread/process lifecycle and managed contexts carry jq state; these C runtime entry points have no jq-visible contract. |
| `jv_tsd_dtoa_ctx_init`; `jv_tsd_dtoa_ctx_fini` | Extern declarations owned by the mapped `jv_dtoa` proxy, not definitions in `jv.c`; managed number formatting does not require native TLS lifecycle calls. |

Types, enum constants, constants, and macros:

| Upstream symbols | Disposition |
|---|---|
| `jv_refcnt`; `JV_REFCNT_INIT`; `payload_flags`; `JVP_PAYLOAD_NONE`; `JVP_PAYLOAD_ALLOCATED`; `jvp_invalid` | `jv_refcnt` and allocated `jvp_invalid` are explicit managed types; typed `jv` kind/payload state replaces the packed C flag bits. Invalid-message ownership, outer-only copies, consuming queries, and final child release retain the source contract. |
| `KIND_MASK`; `PFLAGS_MASK`; `PTYPE_MASK`; `JVP_MAKE_PFLAGS`; `JVP_MAKE_FLAGS`; `JVP_FLAGS`; `JVP_KIND`; `JVP_HAS_FLAGS`; `JVP_HAS_KIND`; `JVP_IS_ALLOCATED`; `JVP_FLAGS_NULL`; `JVP_FLAGS_INVALID`; `JVP_FLAGS_FALSE`; `JVP_FLAGS_TRUE`; `JVP_FLAGS_INVALID_MSG` | Native packed-layout machinery replaced by `jv_kind`, `jv.Kind`, and typed managed payloads. Every observable kind/validity distinction remains mapped. |
| `JVP_NUMBER_NATIVE`; `JVP_NUMBER_DECIMAL`; `jvp_literal_number`; `decNumberDoublePrecision`; `DECNUMDIGITS`; `JVP_FLAGS_NUMBER_NATIVE`; `JVP_FLAGS_NUMBER_LITERAL`; `DEC_NUMBER_DOUBLE_PRECISION`; `DEC_NUMBER_STRING_GUARD`; `DEC_NUMBER_DOUBLE_EXTRA_UNITS`; `DEC_CONTEXT` | Native decNumber layout/context machinery replaced by `JvNumber`, `ManagedDecimal`, and jq decimal contexts. Literal presence and exact comparison remain explicit. |
| `tls_keys`; `tls_values`; `DEAD_KEY` | Conditional Win32 TLS storage only; replaced by CLR lifecycle/context isolation. |
| `jvp_array`; `ARRAY_SIZE_ROUND_UP`; `JVP_FLAGS_ARRAY` | Explicit managed `jvp_array`, per-handle offset/size, initialized prefix, physical capacity, and the jq 3/2 growth calculation replace only the C flexible-array layout and packed flag. |
| `jvp_string`; `JVP_FLAGS_STRING` | Explicit managed `jvp_string` retains reference count, UTF-8 bytes, logical byte length, allocation capacity, trailing NUL, cached hash flag/value, and lazy decoded-text cache; only the C inline-allocation layout and packed flag differ. |
| `object_slot`; `jvp_object`; `JVP_FLAGS_OBJECT`; `DEFAULT_OBJECT_SIZE` | Explicit managed slots and buckets retain raw owning `jv` keys/values, chains, tombstones, physical iteration order, default capacity, growth, and object copy-on-write; only the contiguous C allocation and packed flag differ. |
| `MAX_OBJECT_MERGE_DEPTH`; `MAX_EQUAL_DEPTH`; `MAX_CONTAINS_DEPTH` | Exact value 10,000 retained by iterative managed engines; the former process-crashing native recursion is not reproduced. The limit and outcome are preserved without stack overflow. |
| `ITER_FINISHED` | Represented by the managed object iterator sentinel returned by `jv_object_iter_next`; iterator validity is exposed through the exact iterator API. |

### `src/jv.h`

All 93 unique `jv_*` function declarations/definitions, including the header-inline
`jv_is_valid`, were checked. The following rows are the exhaustive header
disposition; `jv_get_refcnt` appears twice upstream and is counted once.

| Upstream symbols | Disposition |
|---|---|
| `jv_get_kind`; `jv_kind_name`; `jv_is_valid`; `jv_copy`; `jv_free`; `jv_get_refcnt`; `jv_equal`; `jv_identical`; `jv_contains`; `jv_invalid`; `jv_invalid_with_msg`; `jv_invalid_get_msg`; `jv_invalid_has_msg`; `jv_null`; `jv_true`; `jv_false`; `jv_bool`; `jv_number`; `jv_number_with_literal`; `jv_number_value`; `jv_is_integer`; `jv_number_abs`; `jv_number_negate`; `jv_number_has_literal`; `jv_number_get_literal`; `jv_array`; `jv_array_sized`; `jv_array_length`; `jv_array_get`; `jv_array_set`; `jv_array_append`; `jv_array_concat`; `jv_array_slice`; `jv_array_indexes`; `jv_string`; `jv_string_sized`; `jv_string_empty`; `jv_string_length_bytes`; `jv_string_length_codepoints`; `jv_string_value`; `jv_string_indexes`; `jv_string_slice`; `jv_string_concat`; `jv_string_append_codepoint`; `jv_string_append_buf`; `jv_string_append_str`; `jv_string_repeat`; `jv_string_split`; `jv_string_explode`; `jv_string_implode`; `jv_string_hash`; `jv_object`; `jv_object_get`; `jv_object_has`; `jv_object_set`; `jv_object_delete`; `jv_object_length`; `jv_object_merge`; `jv_object_merge_recursive`; `jv_object_iter`; `jv_object_iter_next`; `jv_object_iter_valid`; `jv_object_iter_key`; `jv_object_iter_value` | Exact jq-shaped names in the direct `jv.c.cs` surface, with representation adaptations documented below. |
| `jv_get`; `jv_set`; `jv_has`; `jv_setpath`; `jv_getpath`; `jv_delpaths`; `jv_keys`; `jv_keys_unsorted`; `jv_cmp`; `jv_sort`; `jv_group`; `jv_unique` | Exact jq-shaped names in `jv_aux.c.cs`. |
| `jv_dump_string`; `jv_dump_string_trunc`; `jv_parse`; `jv_load_file`; `jv_nomem_handler`; `jv_parser_new`; `jv_parser_set_buf`; `jv_parser_remaining`; `jv_parser_next`; `jv_parser_free` | Exact names in the separately mapped `jv_print.c`, `jv_parse.c`, `jv_file.c`, and `jv_alloc.c` proxies. Dump inputs retain the native consuming contract; the return is a managed string/byte buffer, and explicit borrowing adapters spell the required `jv_copy`. Compact flags==0 and truncation's NUL-inclusive byte budget are directly tested. JSON, streaming parser, module-file, and allocator tests cover the other behavior. |
| `jv_string_vfmt`; `jv_string_fmt` | Unmatched names 1-2/7: C `va_list`/variadic formatting ABI only; all managed semantic call sites use typed formatting. |
| `jv_dumpf`; `jv_dump`; `jv_show` | Unmatched names 3-5/7: `FILE*`/stdout/terminal front ends owned by the printer/CLI boundary. Compact jq serialization is mapped by `jv_dump_string`; no jq filter observes these stream functions. |
| `jv_parse_sized`; `jv_parse_custom_flags` | Unmatched names 6-7/7: their full behavior is mapped by `jv_parser_new`, byte-span/length `jv_parser_set_buf`, `jv_parser_next`, and the exact parse flags. They are C convenience front ends, not absent parsing semantics. Raw byte lengths, sequence, streaming, and stream-error modes have focused tests. |

| Upstream types/constants/macros | Disposition |
|---|---|
| `jv_kind`; `JV_KIND_INVALID`; `JV_KIND_NULL`; `JV_KIND_FALSE`; `JV_KIND_TRUE`; `JV_KIND_NUMBER`; `JV_KIND_STRING`; `JV_KIND_ARRAY`; `JV_KIND_OBJECT`; `jv` | Exact managed `jv_kind` values and managed `jv` value type; packed C fields are intentionally not exposed. |
| `jv_refcnt` forward declaration | Exact logical owner counter in `jv.c.cs`; it is attached to allocated invalid, literal-number, string, array, and object storage. A public program follows native `jq_state` ownership and is not concurrently reused; independent states provide execution concurrency. |
| `jv_print_flags`; `JV_PRINT_PRETTY`; `JV_PRINT_ASCII`; `JV_PRINT_COLOR`; `JV_PRINT_COLOUR`; `JV_PRINT_SORTED`; `JV_PRINT_INVALID`; `JV_PRINT_REFCOUNT`; `JV_PRINT_TAB`; `JV_PRINT_ISATTY`; `JV_PRINT_SPACE0`; `JV_PRINT_SPACE1`; `JV_PRINT_SPACE2`; `JV_PRINT_INDENT_FLAGS` | `jv_dump_string(jv, int)` implements compact flags==0 and explicitly rejects nonzero flags instead of silently ignoring them. `JqOutputFormatter` separately owns CLI pretty/color/ASCII/sort presentation; FILE*/TTY/refcount-debug mechanics remain omitted because they have no jq-filter semantic effect. |
| `JV_PARSE_SEQ`; `JV_PARSE_STREAMING`; `JV_PARSE_STREAM_ERRORS`; `jv_parser` | Exact constants and parser state in `jv_parse.c.cs`. |
| `jv_nomem_handler_f` | Exact managed delegate in `jv_alloc.c.cs`; process-global abort/OOM mechanics stay behind that proxy. |
| `jv_array_foreach`; `JV_ARRAY_1`; `JV_ARRAY_2`; `JV_ARRAY_3`; `JV_ARRAY_4`; `JV_ARRAY_5`; `JV_ARRAY_6`; `JV_ARRAY_7`; `JV_ARRAY_8`; `JV_ARRAY_9`; `JV_ARRAY_IDX`; `JV_ARRAY`; `jv_object_foreach`; `jv_object_keys_foreach`; `JV_OBJECT_1`; `JV_OBJECT_2`; `JV_OBJECT_3`; `JV_OBJECT_4`; `JV_OBJECT_5`; `JV_OBJECT_6`; `JV_OBJECT_7`; `JV_OBJECT_8`; `JV_OBJECT_9`; `JV_OBJECT_10`; `JV_OBJECT_11`; `JV_OBJECT_12`; `JV_OBJECT_13`; `JV_OBJECT_14`; `JV_OBJECT_15`; `JV_OBJECT_16`; `JV_OBJECT_17`; `JV_OBJECT_18`; `JV_OBJECT_IDX`; `JV_OBJECT` | C variadic-preprocessor construction/enumeration syntax replaced by typed array/object constructors and managed enumeration over the exact public operations. No runtime behavior is omitted. |
| `JQ_FALLTHROUGH`; `JV_PRINTF_LIKE`; `JV_VPRINTF_LIKE`; `JV_H` | Compiler annotation/diagnostic and header-guard macros only; no runtime mapping is applicable. |

### `src/jv_aux.c`

| Upstream symbols | Disposition |
|---|---|
| `jv_get`; `jv_set`; `jv_has`; `jv_setpath`; `jv_getpath`; `jv_delpaths`; `jv_keys_unsorted`; `jv_keys`; `jv_cmp`; `jv_sort`; `jv_group`; `jv_unique` | All twelve exports have exact jq-shaped names in `jv_aux.c.cs`. Raw invalid/null boundaries, numeric-index casts, path mutation/deletion, key order, stable tie breaking, grouping, uniqueness, and depth errors are tested. |
| `jv_number_get_value_and_consume` | Inlined at managed call sites as the value read followed by the source-required `jv_free`, so the consuming boundary remains explicit. |
| `parse_slice` | Folded into `PathUpdates.TryGetSlice`, `ReadSliceBound`, and `SliceValue`. |
| `jv_dels`; `delpaths_sorted` | Folded into iterative `PathUpdates.DeletePaths`/`DeleteAtPath`; public behavior is exposed by exact `jv_delpaths`. |
| `string_cmp` | Folded into raw UTF-8 byte comparison used by `jv_cmp`, preserving upstream byte ordering without first decoding to UTF-16. |
| `jvp_cmp` | Folded into the iterative `jv_cmp` engine with the same 10,000-depth failure contract. |
| `sort_entry_array_free` | Mapped by `FreeSortEntries`, which explicitly releases every still-owned key and value handle. |
| `sort_cmp`; `sort_items` | Folded into `CompareSortEntries` and `TrySortItems`, retaining upstream key comparison and original-index stable tie break. |
| `sort_cmp_state`; `sort_entry` | Replaced by managed comparison state/depth handling and `JvSortEntry`. |
| `MAX_CMP_DEPTH`; `MAX_PATH_DEPTH` | Exact value 10,000 retained in managed iterative comparison/path engines. |

### `src/jv_private.h`

| Upstream symbols | Disposition |
|---|---|
| `jvp_number_cmp`; `jvp_number_is_nan` | Exact names in `jv_private.h.cs`; both-literal comparison uses exact `ManagedDecimal` values rather than binary-double collapse. |
| `JV_PRIVATE` | Header guard only. |

### `src/jv_unicode.c` and `src/jv_unicode.h`

| Upstream symbols | Disposition |
|---|---|
| `jvp_utf8_backtrack`; `jvp_utf8_next`; `jvp_utf8_is_valid`; `jvp_utf8_decode_length`; `jvp_utf8_encode_length`; `jvp_utf8_encode`; `jvp_codepoint_is_whitespace` | All seven exact names are present in `jv_unicode.c.cs`; spans and integer offsets replace C pointers. Malformed advancement, backtracking, encoding, validation, and jq whitespace are tested. |
| `JV_UNICODE_H` | Header guard only. |

### `src/linker.c` and `src/linker.h`

| Upstream symbols | Disposition |
|---|---|
| `load_program`; `load_module_meta`; `path_is_relative`; `build_lib_search_chain`; `validate_relpath`; `jv_basename`; `find_lib`; `default_search` | Exact jq-shaped managed names in `linker.c.cs`. The direct overloads parse and link `block`/`inst` IR; all `jv` arguments preserve the native move/free contract. An explicit `JqModuleResolver` supplies the host capabilities that native obtains from `jq_state` and POSIX process state. |
| `load_library` | Direct file-local port over `block`, `locfile`, `jq_parse_library`, recursive dependency processing, data constants, and source-shaped optional/error behavior. |
| `process_dependencies` | Direct port preserving reverse dependency traversal, alias/include/data binding, module cache reuse, cycle detection, and `block_bind_library` ownership. |
| `lib_entry` | Direct managed type retaining the upstream `name`, `def`, and `loading` fields. |
| `lib_loading_state` | Direct managed state retaining ordered entries/count and adding the public resolver's optional aggregate resource accounting. |
| `LINKER_H` | Header guard only. |

The obsolete parallel `JqModuleLinker` and `ModuleMetadataNode` compatibility
path has been removed. Production and lifetime tests exercise the direct
`block`/`inst` linker and state-owned bytecode path exclusively.

### `src/locfile.c` and `src/locfile.h`

| Upstream symbols | Disposition |
|---|---|
| `locfile_init`; `locfile_retain`; `locfile_free`; `locfile_get_line`; `locfile_line_length`; `locfile_locate` | All names, including the private line-length helper, are present in `locfile.c.cs`; byte positions, line maps, retain/free behavior, and diagnostics are tested. Managed typed arguments replace C varargs at `locfile_locate`. |
| `location`; `UNKNOWN_LOCATION`; `locfile` | Exact managed names; `jq_source_location` is an additional managed diagnostic representation, not a substitute for missing state. |
| `LOCFILE_H` | Header guard only. |

### `src/util.c` and `src/util.h`

| Upstream symbols | Disposition |
|---|---|
| `expand_path`; `get_home`; `jq_realpath`; `_jq_memmem`; `priv_fwrite` | Exact managed names. `get_home` preserves its compatibility lookup, but production module resolution does not call it: `JqModuleResolver.HomeDirectory` and `IJqFileSystem` supply explicit capabilities. `priv_fwrite` targets an explicit stream. |
| Win32 `fopen` wrapper | Replaced by `IJqFileSystem`/managed stream opening at the module/file boundary; no `_wfopen` or process-global file capability is exposed. |
| `fprinter` | Native parser-to-`FILE*` callback only; managed parser errors are values/diagnostics and explicit output streams own presentation. |
| `jq_util_input_init`; `jq_util_input_set_parser`; `jq_util_input_free`; `jq_util_input_add_input`; `jq_util_input_errors`; `next_file`; `jq_util_input_read_more`; `jq_util_input_next_input_cb`; `jq_util_input_get_position`; `jq_util_input_get_current_filename`; `jq_util_input_get_current_line`; `jq_util_input_next_input` | The library boundary uses explicit `IJqInputSource`, `JqInputReadResult`, `JqInputPosition`, and `jq_state` callbacks rather than ambient stdin. The CLI's `CliInputReader` ports the observable filename/stdin loop, shared primary/input stream, owned-`jv` transfer, and one source-shaped `jq_util_input_read_more` state machine shared by parsed, raw-line, and raw-slurp modes. That port preserves the 4,091-byte `fgets` payload, UTF-8 tail `fread`, its deliberately uncounted tail newline, delayed EOF close, partial raw-record continuation across files, per-buffer malformed-unit repair, and final empty-file/blank-line positions. `CliInputBufferCompatibilityTests` freezes those boundaries against pinned jq. FILE* and allocator/fault identity remain implementation details. |
| `jq_util_input_state` | `CliInputReader` is the per-invocation CLI port; public library hosts instead supply the explicit input capability above. Neither path introduces process-global input state. |
| `strptime`; `first_wday_of`; `fromzone`; `conv_num`; `find_string` | Conditional libc fallback implementation. Jq-visible time parsing is implemented at the builtin boundary; the POSIX `struct tm`/pointer-return C ABI itself is not applicable. |
| `ALT_E`; `ALT_O`; `LEGAL_ALT`; `TM_YEAR_BASE`; `TM_SUNDAY`; `TM_MONDAY`; `TM_TUESDAY`; `TM_WEDNESDAY`; `TM_THURSDAY`; `TM_FRIDAY`; `TM_SATURDAY`; `S_YEAR`; `S_MON`; `S_YDAY`; `S_MDAY`; `S_WDAY`; `S_HOUR`; `HAVE_MDAY`; `HAVE_MON`; `HAVE_WDAY`; `HAVE_YDAY`; `HAVE_YEAR`; `HAVE_HOUR`; `SECSPERMIN`; `MINSPERHOUR`; `SECSPERHOUR`; `HOURSPERDAY`; `HERE_D_T_FMT`; `HERE_D_FMT`; `HERE_T_FMT_AMPM`; `HERE_T_FMT`; `isleap`; `isleap_sum`; `tzname`; `strncasecmp`; `delim` | Private constants/macros of that conditional `strptime` fallback; their jq-visible parsing role is folded into the managed date-format parser, while platform aliases and C scanning mechanics require no ABI mapping. |
| `MIN`; `MAX` | Exact generic managed helpers in `util.h.cs`. |
| `alloca` | Conditional compiler/platform allocation alias only; managed code has no stack-allocation ABI dependency here. |
| `UTIL_H` | Header guard only. |

### `src/jq_parser.h`

| Upstream symbols | Disposition |
|---|---|
| `jq_parse`; `jq_parse_library` | Exact names and native-shaped ownership boundary: generated parser methods receive `locfile` and fill managed `out block` values in place of C `block *` pointers. The returned blocks contain the direct `inst` graph consumed by the linker and `block_compile`. |
| `JQ_PARSER_H` | Header guard only. |

### `src/jq_test.c`

The C file is a native test driver, so its exact disposition is a managed test
harness substitution rather than production API methods.

| Upstream symbols | Disposition |
|---|---|
| `skipline`; `checkerrormsg`; `checkfail`; `run_jq_tests` | `UpstreamTestFile` parses the unchanged fixture grammar; the compatibility runner executes success, error-message, compile-failure, and superfluous-output cases. |
| `test_err_cb`; `err_data`; `jq_testsuite` | Managed compile/runtime outcomes and diagnostic records replace the callback payload; the compatibility-runner command is the test-suite entry point. |
| `test_start_state`; `test_jq_start_resets_state`; `run_jq_start_state_tests` | Mapped by start/reset assertions in `HaltCompatibilityTests`, `StatefulIoCompatibilityTests`, and public execution-facade tests. |
| `compile_args_and_check`; `run_jq_compile_args_tests` | Mapped by compile-argument and compile-validation compatibility tests using managed arguments. |
| `run_jq_recompile_tests`; `run_jq_exhaust_and_reuse_tests` | Mapped by recompile, exhaustion, reset, and reuse tests in the public/stateful execution suites. |
| `jv_test` | Mapped by direct `jv`, JSON proxy, Unicode, path, sort/group/unique, identity, and deep-value tests. |
| `test_pthread_jq_parse`; `test_pthread_run`; `run_jq_pthread_tests`; `test_pthread_data`; `NUMBER_OF_THREADS` | Conditional pthread harness replaced by task-based concurrent parser/execution and capability-isolation tests; pthread structs/count are test mechanics, not library semantics. |

## Deliberate non-semantic omissions

These are not silent gaps:

- `jv_string_vfmt` and `jv_string_fmt` are C `va_list`/`printf` construction
  helpers. Managed call sites use typed interpolation or the narrow
  `locfile` C-format adapter. There is no managed varargs ABI to preserve.
- `jv_dumpf`, `jv_dump`, and `jv_show` are stream/terminal-facing printer
  entry points owned by the `jv_print.c` proxy. The library uses
  `jv_dump_string`; the managed CLI adapter owns terminal/color presentation,
  while these native `FILE*`/refcount entry points remain outside this direct
  value-core symbol port.
- `jv_parse_sized` and `jv_parse_custom_flags` are owned by the separately
  mapped `jv_parse.c` proxy. The streaming parser retains the upstream parser
  state and parse flags; these two C pointer-length convenience names are not
  part of the direct value-core port.
- `strptime` in `util.c` is compiled upstream only when libc lacks it. It is a
  portability fallback, not a jq-owned library contract; managed time parsing
  does not call a C libc shim.
- Windows `_wfopen` and console `WriteFile` branches, raw `FILE*`, pthread
  checks, allocator/refcount assertions, and process-global no-memory hooks are
  native platform/memory mechanics. Their managed replacements are runtime
  services or separately mapped proxies.
- `jq_util_input_*` owns CLI filename iteration and ambient stdin. The library's
  stateful API instead requires explicitly supplied input, position, and error
  capabilities. Reintroducing those `util.c` globals would violate the
  no-ambient-capabilities boundary.

## Known representation adaptations

- The borrowing string-key convenience overload `jv_object_get(jv, string)`
  normalizes an absent element to null for managed metadata and compatibility
  views that need jq's post-`jv_get` result. The bytecode/linker core uses
  jq-shaped owning operations where ownership matters. The raw
  `jv_object_get(jv, jv)` overload and
  raw `jv_array_get(jv, int)` retain upstream invalid sentinels; `jv_get`
  performs jq's negative-index and missing-to-null normalization. Both layers
  have focused assertions, and public filter behavior is covered by unchanged
  fixtures.
- Allocated invalids, literal numbers, strings, arrays, and objects carry explicit logical
  reference counts. `jv_copy` increments only the copied outer allocation;
  `jv_free` decrements it and releases owned children only when the count reaches
  zero; `jv_get_refcnt` reports that live count. CLR garbage collection remains
  responsible only for ultimately reclaiming already-released managed storage.
- `jv` is a managed value type instead of the packed C union, and C pointer
  returns become typed storage accessors or spans. Ownership transfer is still
  represented explicitly by consuming jq-shaped calls, `jv_copy`, and
  `jv_free`; it does not become immutable collection semantics.
- jq strings are owned UTF-8 byte allocations, including logical byte length,
  capacity, cached seeded hash, and an excluded trailing NUL. Managed
  `StringValue`/`jv_string_value` decode only the logical byte range and cache
  the UTF-16 view; value-core equality, ordering, hashing, slicing, and object-key
  lookup continue to operate on the raw bytes.
- Object slots own `jv` string keys and values. The peripheral managed
  `IReadOnlyList<KeyValuePair<string, jv>>` view decodes keys for compatibility,
  but lookup, mutation, deletion, iteration ownership, unsharing, and rehashing
  retain the raw-key path.

## Verification

Focused symbol closure tests are in
`tests/DotNetJq.Tests/JvDirectPortSymbolCompatibilityTests.cs`. Allocation,
ownership, copy-on-write, capacity, raw UTF-8, hash-cache, and iterative-release
coverage is in `JvArrayRefcountCowCompatibilityTests`,
`JvObjectRefcountCowCompatibilityTests`,
`JvStringInvalidRefcountCowCompatibilityTests`, and
`JvLiteralNumberRefcountCompatibilityTests`. They run without invoking the
jq executable, rewriting fixture output, or using an allowlist. The unchanged
official compatibility fixtures and full managed suite remain the release gate.

The closing inventory used GCC `-aux-info` output for every compiled
source-defined function, an explicit review of conditional Win32/pthread/libc
branches, and a direct `#define` scan (118 unique macros across the audited
files). The `jv.h` declaration scan found 93 unique function names: 86 exact
managed names and the seven explicitly dispositioned names above. Named types,
anonymous enum contracts, header-inline functions, header guards, and native
test-driver symbols were reviewed separately and are all represented in the
ledger.

Current verification checkpoint:

- `JvDirectPortSymbolCompatibilityTests`: 9/9 passed;
- parser/printer proxy tests that cover the behavior behind five unmatched
  C-front-end names: 61/61 passed;
- adjacent `jv_aux`/Unicode/source-location/module/fixture-harness tests: 82/82
  passed;
- unchanged upstream `tests/jq.test`: 550/550 passed.
