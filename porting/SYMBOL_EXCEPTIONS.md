# Symbol exceptions

- C preprocessor macros are represented by methods or constants with the closest deterministic name.
- jv remains lowercase to preserve the upstream type name even though this differs from normal .NET naming conventions.
- GPPG/GPLEX runtime support types use deterministic managed names where no C
  symbol exists. Grammar actions still emit jq's direct `block`/`inst` graph,
  and public jq/libjq-shaped parser, compiler, bytecode, and VM entry points
  retain upstream names.
