# Differential Mismatch Clusters

This table records the 36 disjoint mismatches from the seed `18082` / 1,260-case
baseline before remediation. The canonical `DIFFERENTIAL_PROBE_REPORT.md` has
since been regenerated and now reports 5,040 matches and zero mismatches.

| Cases | Count | Owning file / symbol | Semantic root cause | Action |
|---|---:|---|---|---|
| 77, 149, 410, 833, 1247 | 5 | `Port/src/compile.c.cs` — `jq_compile_args`; parser call references | Unknown `leaf_paths/0` survives compilation and is rejected only when `CallNode` executes. jq rejects unresolved call signatures during binding. | Record call name/arity/source token while parsing, then validate against lexical definitions, imports, intrinsics, and builtin signatures before producing `jq_compiled_program`. |
| 78, 141, 420, 492, 672, 915, 969, 1041, 1059, 1158 | 10 | `Port/src/execute.c.cs` — `OptionalNode.Eval`, `IndexNode`, `EachNode` | Generic `OptionalNode` catches failure from the full `.foo[]` chain. jq lowers the suffix to `EACH_OPT`, so `.foo` target evaluation must fail before optional iteration begins. | Scope postfix optional behavior to the terminal index/slice/iterator operation; retain generic `try` behavior for other filters. |
| 145, 631, 712, 748, 874, 1090, 1243 | 7 | `Port/src/builtin.c.cs` — `DeletePaths`; `Port/src/execute.c.cs` — `PathUpdates.DeletePaths` | Invalid object-key deletion on scalars/arrays is treated as a no-op instead of a type/index error. | Validate every delete path component against the current container before mutation and preserve jq's error type. |
| 433, 892 | 2 | `Port/src/execute.c.cs` — `IndexNode.Eval`, `PathUpdates.GetAtPath` | Numeric indexing is incorrectly allowed on strings; jq permits string slicing but not scalar string indexing. | Reject numeric `IndexNode` access when the target is a string; retain `SliceNode` rune slicing. |
| 185, 608, 860 | 3 | `Port/src/builtin.c.cs` — `Reverse` | `reverse` throws for jq values that upstream's slice-based definition returns unchanged; the following `?` hides the managed error and removes the value. | Match upstream reverse behavior for null, empty string, and empty object before applying optional suppression. |
| 491, 716, 770 | 3 | `Port/src/compile.h.cs` — `CallNode` flatten fast path; `Port/src/builtin.c.cs` — `Flatten` | Managed flatten requires an array, while jq's recursive definition leaves non-array values unchanged. | Return the input unchanged for non-arrays at depth zero/positive and keep recursive array flattening. |
| 601 | 1 | `Port/src/builtin.c.cs` — `RegexSplit`; `Compatibility/Regex/JqRegex.cs` | Regex split of an empty string produces the wrong empty-field shape. | Align zero-length input and zero-match boundary emission with Oniguruma/jq split rules. |
| 700 | 1 | `Compatibility/Regex/JqRegex.cs` — pattern translation / scan | POSIX `[[:alpha:]]` character-class behavior differs from Oniguruma. | Correct POSIX class translation and verify ASCII plus Unicode boundaries. |
| 1051 | 1 | `Compatibility/Regex/JqRegex.cs` — scan match advancement | A nullable repeated alternation advances/emits zero-width matches differently from jq. | Implement Oniguruma-compatible empty-match suppression and scalar advancement. |
| 616 | 1 | `Compatibility/Json/JqFormats.cs` — base64 decode | `@base64d` coercion/invalid-input behavior differs for `null`. | Match jq's byte decoding and replacement/error behavior after its string conversion rule. |
| 708 | 1 | `Port/src/execute.c.cs` — `ReduceNode.Eval` | An empty update-filter stream deletes the accumulator instead of retaining the prior accumulator for the next source value. | Preserve jq reduce accumulator/backtracking semantics when the update emits no values. |
| 80 | 1 | `Port/src/builtin.c.cs` — `Combinations` | The Cartesian product of zero input dimensions is empty instead of the single empty tuple. | Emit `[]` once for zero dimensions. |
