# Differential Probe Report

Managed DotNetJq was compared with the pinned official jq 1.8.2 executable.
No mismatch was excluded, allowlisted, or converted into a pass.
Compile/runtime diagnostic payloads were compared after removing only the native process executable/location envelope and terminal compile-count wrapper.

## Configuration

- Seed: `18082`
- Cases: `5040`
- Corpus: `general`
- Unique filter/input pairs: `5040`
- Explicit public builtin signatures covered: `226` / `226`
- Builtin inventory source: pinned jq 1.8.2 `builtins/0`, verified at run time; every signature has a representative invocation
- Declared categories covered: `20` / `20`
- Declared category filters covered: `437` / `437`
- Declared JSON inputs covered: `48` / `48`
- Oracle: `artifacts/test-assets/jq-1.8.2/oracle/jq`
- Oracle version: `jq-1.8.2`
- Oracle SHA-256: `b1c22172dd303f3be49e935aa56aa48a8b7a46e0bc838b4997d3bb451495870f`
- Per-oracle-case timeout: `2000 ms`
- Managed limits: 1 s execution timeout, 256 outputs, 1 MiB input/output, 256 recursion depth, 100,000 execution transitions, 500 ms regex timeout
- Failure diagnostics: exact payload, source excerpt, carets, punctuation, values, and ordering; only the native process envelope and terminal compile-count wrapper are excluded

Category distribution:

| Category | Cases |
|---|---:|
| builtin-signatures | 226 |
| builtins | 254 |
| control-errors | 253 |
| dates | 254 |
| formats | 253 |
| generators | 253 |
| language-compiler | 254 |
| math-general | 253 |
| modules | 254 |
| numbers | 253 |
| numeric-literals | 253 |
| objects-arrays | 253 |
| paths | 253 |
| regex | 254 |
| stateful-no-capability | 254 |
| streams | 254 |
| strings | 253 |
| type-error-boundaries | 253 |
| types | 253 |
| updates | 253 |

## Summary

- Matched: `5040`
- Mismatched: `0`

## Mismatches

None.
