# Regex Differential Probe Report

Managed DotNetJq was compared with the pinned official jq 1.8.2 executable.
No mismatch was excluded, allowlisted, or converted into a pass.
Compile/runtime diagnostic payloads were compared after removing only the native process executable/location envelope and terminal compile-count wrapper.

## Configuration

- Seed: `1808202`
- Cases: `4980`
- Corpus: `regex`
- Unique filter/input pairs: `4980`
- Oracle: official jq 1.8.2 release binary
- Oracle version: `jq-1.8.2`
- Oracle SHA-256: `b1c22172dd303f3be49e935aa56aa48a8b7a46e0bc838b4997d3bb451495870f`
- Per-oracle-case timeout: `2000 ms`
- Managed limits: 1 s execution timeout, 256 outputs, 1 MiB input/output, 256 recursion depth, 100,000 execution transitions, 500 ms regex timeout
- Failure diagnostics: exact payload, source excerpt, carets, punctuation, values, and ordering; only the native process envelope and terminal compile-count wrapper are excluded

Category distribution:

| Category | Cases |
|---|---:|
| regex-absent-ranges | 14 |
| regex-anchors | 62 |
| regex-backreferences | 63 |
| regex-callout-events | 110 |
| regex-capture-adversarial | 144 |
| regex-captures | 64 |
| regex-casefold | 324 |
| regex-counted-grammar-closure | 12 |
| regex-empty-alternatives | 60 |
| regex-empty-modifiers | 140 |
| regex-full-fold-atom-boundaries | 57 |
| regex-invalid | 64 |
| regex-leveled-backref-absent-lookbehind-boundaries | 56 |
| regex-lookaround-backref | 132 |
| regex-lookarounds | 64 |
| regex-newline-options | 138 |
| regex-oniguruma-fallbacks | 648 |
| regex-oniguruma-residual-family-b | 20 |
| regex-options | 64 |
| regex-parser-normalization-closure | 16 |
| regex-perl-ng-syntax | 144 |
| regex-posix | 64 |
| regex-posix-composition | 144 |
| regex-properties | 144 |
| regex-replace-split | 64 |
| regex-replacement-streams | 138 |
| regex-scalar-character-classes | 27 |
| regex-subcalls-conditionals | 22 |
| regex-unicode | 64 |
| regex-unicode-property-catalog | 1513 |
| regex-word-boundaries | 138 |
| regex-zero-width | 57 |
| regex-zero-width-context | 209 |

## Summary

- Matched: `4980`
- Mismatched: `0`

## Mismatches

None.

## Managed proxy boundary

The passing corpus exercises the managed jq-shaped regex surface, including recursive and relative subexpression calls, capture levels and conditionals, `\G`, `\K`, nested absent ranges, class-context/control escapes, fixed-width lookbehind calls/backreferences/reduction, built-in and content callouts, text segments, pinned Unicode properties/case folds, and exact invalid-pattern diagnostics. It does not claim native Oniguruma API, bytecode, allocator, or engine identity because those are outside jq's public regex surface. The complete declared corpus reported no mismatch; that bounded result is not a proof over inputs outside the declared inventory.
