# Numeric and arithmetic gap analysis

> **Historical gap analysis only — not current verification evidence.** This
> document records an intermediate investigation whose 372/550 baseline and
> listed failures predate later compatibility work. Do not use those counts or
> gaps as the current project status; use the latest release-gate results in
> `PORTING_STATUS.md`.

Upstream oracle: jq 1.8.2, commit `34f7186b86743a083a589741b6cea95293524108`.
Oracle executable: `artifacts/test-assets/jq-1.8.2/oracle/jq`.

This analysis is read-only with respect to the implementation. The measured baseline was the
last stable compatibility-runner build before concurrent work resumed: `jq.test` passed 372 of
550 cases. The conclusions below also come from direct probes of the pinned oracle and inspection
of the matching upstream C functions.

## Executive conclusion

`ManagedDecimal` is not the main blocker. Its coefficient/exponent representation already parses,
compares, and formats the extreme literals in case 137 correctly in focused tests. The dominant
literal bug is integration order: `JvNumber.ToJqString()` and `jv_print.c.cs` consult the cached
`double` first, so an exact finite decimal whose binary64 cache is infinity is printed as `null`,
and an exact tiny decimal whose cache is zero is printed as `0`.

Ordinary jq binary arithmetic must continue to use binary64. Cases 464-466 and 471 already pass and
confirm that the current `+`, `-`, `*`, and `/` numeric boundary correctly discards literal precision.
Do not change those operators to use `ManagedDecimal` arithmetic. The actual arithmetic defects are
jq-specific `%` integer conversion, string repetition, numeric indexing/slicing, and error rendering.

The cases at the end of `jq.test` are a separate family: they test exact structural depth limits of
10,000 and stack-safe traversal. They should not be fixed inside `ManagedDecimal` or by merely raising
the general evaluator recursion option.

## Measured clusters

| Cases | Result in stable baseline | Classification |
|---|---:|---|
| 130-149 | 18/20 pass | Case 137 is exact-number serialization; case 142 is `%` semantics. |
| 330-345 | 13/16 pass | Cases 338, 339, and 342 are string repetition semantics/safety. |
| 423-437 | Numeric operations mostly execute, but cases 426 and 433-437 fail jq error-text contracts. | Diagnostics around binary operators. |
| 460-478 | 10/19 pass; nine failures are 461-463, 467-469, 472, 477, 478. | Missing `have_decnum`, exact printing, and the jq-language definition of `abs`. |
| 486-488 | 0/3 pass | jq JSON accepts payload-free `nan`/`NaN`/`-NaN`; `System.Text.Json` does not. |
| 510-523 | Numeric index/slice cases fail. | jq truncation, slice-end ceiling, NaN, and update semantics. |
| 533-550 | 533-536, 543, and 545-550 fail; 537-542 and 544 pass. | JSON/print depth, stack-safe flatten/setpath, contains/merge/equality/comparison depth sentinels. |

### Case 137: exact literal survives, output path discards it

Program:

```jq
9E999999999, 9999999999E999999990, 1E-999999999, 0.000000001E-999999990
```

Expected:

```text
9E+999999999
9.999999999E+999999999
1E-999999999
1E-999999999
```

Actual was `null`, `null`, `0`, `0`. `ManagedDecimal.ToScientificString()` already produces all four
expected strings. Two guards erase that result:

1. `JvNumber.ToJqString()` checks `Value == 0` before `ExactValue`.
2. `WriteJv()` checks `double.IsFinite(value.NumberValue)` before asking `JvNumber` for an exact literal.

Upstream `jv_number_get_literal()` is consulted first. A finite exact decimal is printed even when its
lazy binary64 conversion is zero or infinity. Only a literal that overflowed the decNumber exponent
range has no printable literal and falls back to a clamped binary64 value.

### Case 142: jq `%` is not floating-point remainder

Program:

```jq
[(infinite, -infinite) % (1, -1, infinite)]
```

Expected `[0,0,0,0,0,-1]`; actual was six `null` values.

Upstream `binop_mod` does this in order:

1. If either operand is NaN, return NaN.
2. Convert each operand to saturated `intmax_t` using jq's `dtoi` macro (truncate toward zero,
   clamp below `INTMAX_MIN` and above `INTMAX_MAX`; infinities therefore saturate).
3. Reject a converted zero divisor.
4. Return zero for divisor `-1` to avoid `INTMAX_MIN % -1` overflow.
5. Otherwise compute integer remainder and convert the result back to binary64.

The current `BinaryNode` uses `double % double`, which produces NaN for infinite dividends and has
different fractional semantics. `%` needs a dedicated implementation; it cannot use the generic
`Numeric` helper.

### Cases 338, 339, 342: jq string repetition

Upstream `binop_multiply` accepts both `string * number` and `number * string`. It converts the count
as follows:

- negative or NaN -> `-1`, and `jv_string_repeat` returns `null`;
- positive fractional -> truncate toward zero;
- above `INT_MAX`, including positive infinity -> clamp to `INT_MAX`;
- zero or an empty input -> empty string without allocating a large buffer.

The current implementation accepts only `string * integer`, rejects the reverse order, and uses
`Enumerable.Repeat` without jq's byte-length overflow check. Consequently:

- case 338 should produce `[null,null,"","","abc","abc","abcabcabc",... ]`;
- case 339 should produce `[null,null]` for `"abc" * (nan,-nan)`;
- case 342 must throw `JqRuntimeException("Repeat string result too long")` before allocation, not
  `OutOfMemoryException`.

The overflow calculation must use UTF-8 byte length, as upstream does, with a checked/saturating
`long` product. Implement a jq-shaped `jv_string_repeat` helper in `jv.c.cs` and call it from
`BinaryNode.Multiply`; this keeps the upstream symbol and centralizes the safety check.

### Literal-number cases 460-478

The current results are partly masked because `have_decnum/0` is undefined. Once exact output is
fixed, register both `have_decnum/0` and `have_literal_numbers/0` as `true`. Do not advertise support
before the literal output path is corrected.

Expected ownership by case:

- 461-462: `JvNumber.ToJqString`, `jv_print.c.cs`, and `jv_tostring`/`tojson`.
- 463: exact-vs-exact comparison; current `jv_number_equal` already has the right shape.
- 464-466 and 471: already pass and prove binary operations must remain binary64.
- 467-469: `jv_number_negate` plus exact-first serialization.
- 472: upstream `abs` comes from `builtin.jq` as `if . < 0 then -. else . end`; therefore `"abc"|abs`
  returns `"abc"`. Directly binding `abs` to `jv_number_abs` is not equivalent.
- 473-478: numeric `abs`/`length` preserve exact literals; the internal `jv_number_abs` implementation
  is appropriate for `length`, while public `abs` must retain the jq-language behavior.

One secondary ManagedDecimal defect should be covered while this cluster is open: upstream unary
minus normalizes an exact positive zero under jq's decimal context instead of manufacturing a
negative literal zero. Add differential coverage for filter literals `-0.0`, JSON input `-0.0` under
`-.`, and `length`/`abs` on negative zero before adjusting `ManagedDecimal.Negate`.

### Numeric error contracts (426, 433-437)

The arithmetic outcome is not the problem in these cases. Upstream `type_error2` renders both kind
and a 30-byte truncated jq dump:

```text
number (1) and number (0) cannot be divided because the divisor is zero
number (1) and number (0) cannot be divided (remainder) because the divisor is zero
```

`BinaryNode.TypePairError` currently prints only the two kinds, and its zero-divisor branches emit a
different one-operand message. Use the shared jq-shaped two-value diagnostic builder for divide,
remainder, and all other binary type errors. Case 426 additionally depends on exact-first truncated
number rendering.

### Numeric JSON cases 486-488

jq's JSON parser deliberately accepts the payload-free tokens `nan`, `NaN`, and `-NaN`. NaN with a
numeric payload (`NaN1`, `NaN10`, and so on) is rejected. `tojson` serializes a NaN value as JSON
`null`, so `{"a":nan}|tojson|fromjson` becomes `{"a":null}`.

This belongs in the `jv_parse.c.cs` proxy, not in `ManagedDecimal` arithmetic. A preprocessing string
replacement is unsafe because occurrences inside JSON strings must remain untouched. Tokenize with
`Utf8JsonReader`-equivalent jq logic (or a small jq numeric-token scanner), create native NaN for only
the payload-free spellings, and keep exact literals for ordinary numbers.

### Numeric indexing and slicing (364, 510-523)

The current code rejects non-integral numeric keys and slice bounds. Upstream deliberately accepts
them:

- array index/update: reject NaN specially, otherwise clamp to the C `int` range and truncate toward
  zero;
- slice start: NaN -> 0, adjust a negative value by length, clamp, then truncate toward zero;
- slice end: NaN -> length, adjust/clamp, truncate, then add one when a positive fractional part was
  discarded (effective ceiling within the array/string);
- `has(nan)` returns false;
- read `.[nan]` returns null;
- update `.[nan] = 9` errors with `Cannot set array element at NaN index`;
- string numeric indexing remains an error (`Cannot index string with number (1.5)`).

Move this normalization into jq-shaped helpers in `jv_aux.c.cs` and make `IndexNode`, `SliceNode`,
`SetPath`, `Has`, and path updates share them. Do not implement it only in parser AST nodes, because
`getpath`, `setpath`, `delpaths`, and native builtins must observe the same rules.

## Deep cases are not decimal cases

Cases 533-550 encode upstream constants, all set to 10,000: `MAX_PARSING_DEPTH`,
`MAX_PRINT_DEPTH`, `MAX_PATH_DEPTH`, `MAX_CONTAINS_DEPTH`, `MAX_OBJECT_MERGE_DEPTH`,
`MAX_EQUAL_DEPTH`, and `MAX_CMP_DEPTH`.

The managed code currently mixes those compatibility limits with `JqExecutionOptions.MaxRecursionDepth`
(default 512) and uses recursive CLR calls in several structural walkers. Raising the option alone can
turn a deterministic jq error into `StackOverflowException`; it is not a correct fix.

Required work by group:

- 533-535: replace recursive `WriteJv` and recursive `jv_from_json_element` with explicit-stack
  walkers. Print values through depth 10,000; at depth greater than 10,000 emit upstream's raw
  `<skipped: too deep>` marker. Parse through the matching boundary and return an error containing
  `Exceeds depth limit for parsing` above it. `JqProgram.Execute` and `jv_to_json_element` must set an
  adequate `JsonDocumentOptions.MaxDepth` when materializing valid deep output.
- 536: make `FlattenInto` iterative and allow the jq boundary independently of the default evaluator
  recursion policy. The 10,000-component setpath cases 537-542 already pass; preserve their current
  exact boundary checks.
- 543: the iterative contains walker is stack-safe but has no depth sentinel. Track structural depth
  and throw `Containment check too deep` only when depth is greater than 10,000.
- 544-545: recursive object merge needs a stack-safe frame machine and the exact
  `Object merge too deep` boundary. Case 544 (depth 10,000) already passes and must remain green.
- 546: make structural equality iterative/depth-aware and surface `Equality check too deep`.
- 547-550: make array/object ordering iterative/depth-aware and surface `Comparison too deep` through
  sort and unique.

These compatibility constants should be named next to their mapped upstream implementation. A lower
user-supplied resource limit may still stop work earlier with a library policy error, but default
execution of the official suite must reach jq's own 10,000 boundary.

## Ranked patch plan

1. **Exact-first number output and capability flags**
   - Owners: `Port/src/jv.h.cs` (`JvNumber`), `Port/src/jv_print.c.cs`,
     `Port/src/jv_dtoa.c.cs`, `Port/src/builtin.c.cs`.
   - Print a finite `ExactValue` before consulting `Value`; for native/inexact values use
     `jvp_dtoa_fmt`; serialize NaN as `null`; clamp positive/negative infinity to
     `+/-double.MaxValue` as upstream does.
   - Then expose `have_decnum` and `have_literal_numbers`.
   - Add public differential tests for cases 137, 461-469, 477-478, exponent-range overflow,
     underflow zero quantum, and signed zero.

2. **Dedicated jq binary-operation semantics**
   - Owners: `Port/src/execute.c.cs` (`BinaryNode`), `Port/src/jv.c.cs`
     (`jv_string_repeat`), `Port/src/builtin.h.cs` wrappers.
   - Implement saturated `intmax_t` modulo and symmetric string repetition with preallocation
     guards. Preserve binary64 behavior for ordinary numeric `+`, `-`, `*`, `/`.
   - Use common two-operand diagnostics.
   - Differential-test cases 142, 338, 339, 342, 433-437 plus fractional, NaN, infinity,
     `INTMAX_MIN % -1`, reverse-order repetition, Unicode byte length, and empty strings.

3. **Central numeric index/slice normalization**
   - Owners: `Port/src/jv_aux.c.cs`, `Port/src/execute.c.cs`, and `Port/src/builtin.c.cs` (`Has`).
   - Implement shared truncation/clamping/NaN rules and consume them from reads, writes, paths, and
     slices.
   - Differential-test case 364 and cases 510-523.

4. **jq JSON numeric-token proxy**
   - Owners: `Port/src/jv_parse.c.cs`, `Port/src/jv_print.c.cs`, public materialization in
     `Public/JqProgram.cs`.
   - Accept payload-free NaN tokens without accepting them inside strings or accepting NaN payloads.
   - Preserve exact ordinary literals and jq's infinity/NaN output normalization.
   - Differential-test cases 486-488 before combining this with depth work.

5. **Stack-safe 10,000-depth structural walkers**
   - Owners: `jv_parse.c.cs`, `jv_print.c.cs`, `jv.c.cs`, `jv_aux.c.cs`, `execute.c.cs`, and structural
     builtins in `builtin.c.cs`.
   - Use explicit stacks and exact per-operation depth sentinels; do not rely on CLR recursion or the
     generic evaluator limit.
   - Run cases 533-550 in isolated worker processes after every change, then run the complete fixture.

6. **ManagedDecimal hardening only after integration tests expose a real mismatch**
   - Owner: `Compatibility/Json/ManagedDecimal.cs` and mapped decNumber wrappers.
   - Keep decimal arithmetic out of ordinary jq binary operators. Focus only on literal parse,
     compare, unary abs/minus, quantum-preserving formatting, exponent-range status, and signed-zero
     parity.

## Acceptance gates

Each ranked item should be accepted only when all of the following hold:

1. Focused managed unit tests cover the jq-shaped helper directly.
2. The named unchanged `jq.test` cases pass through `DotNetJq.CompatibilityRunner`.
3. Direct output is compared to the pinned jq 1.8.2 oracle for string-valued `tostring`/`tojson`
   results, not only JSON numeric equality.
4. The complete solution builds with zero compiler warnings and the complete applicable test suite
   passes.
5. The full 550-case compatibility report is regenerated so fixes are not credited merely because a
   missing builtin or earlier failure masked the target assertion.
