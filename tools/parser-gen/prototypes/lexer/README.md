# jq 1.8.2 GPLEX lexer prototype

This directory retains the feasibility validation copy and its history. The
authoritative managed source is `src/DotNetJq/Grammar/lexer.l`; its normalized
GPLEX output is the active production scanner. Neither managed `.l` file is an
input to `tools/parser-gen/generate.py`, which owns declaration surfaces and the
coverage report rather than scanner generation.

`lexer.l` is a managed port of the rule ordering and start-condition
structure in pinned jq 1.8.2 `src/lexer.l` at commit
`34f7186b86743a083a589741b6cea95293524108`. The original C/Flex file is not
committed here. Its upstream location is recorded in the specification header.

## What the draft maps

- Flex inclusive/exclusive conditions map to GPLEX `%s`/`%x` conditions.
- Flex's start-condition stack maps to GPLEX `%option stack` plus
  `yy_push_state()`/`yy_pop_state()`.
- Rule order, longest-match boundaries, comment continuation, nested delimiter
  state, quoted-string segmentation, and interpolation state follow upstream.
- C `yylval` actions are replaced with raw managed `Token` staging. Semantic
  number/string conversion remains the parser bridge's responsibility.
- Upstream keyword tokens map to `TokenKind.Identifier`, matching the
  jq-shaped scanner contract consumed by the production parser adapter.
- Upstream `FIELD` maps to two queued current-contract tokens: `Dot` followed by
  `Identifier`. Its regular expression remains the exact upstream single-segment
  `FIELD` rule; it does not absorb a qualified identifier tail.
- The unqualified catch-all throws the current lexer's `JqCompileException`;
  its adapter special-cases a bare `@` as the current `Invalid format name`
  diagnostic. Delimiter mismatches still stage `InvalidCharacter` through
  `try_exit`, as in the current contract and pinned state-machine behavior.
- The source has the same 53 lexer rules and six declared start conditions as
  pinned upstream. Current-only invalid-character and unterminated-input
  diagnostics live in adapter methods rather than extra lexer rules.
- The internal raw-parser constructor preserves `InvalidCharacter` and `End`
  tokens instead of throwing those eager compatibility diagnostics.
- `Token.Offset` and `Token.Column` are derived as UTF-8 byte coordinates while
  `Token.SourceIndex` remains a UTF-16 string index. A monotonic coordinate
  cursor advances only as tokens are emitted, so scanning does not allocate
  source-sized offset/line/column arrays.

## GPLEX differences already made explicit

- Flex options for reentrancy, allocator callbacks, Bison bridge/location
  pointers, symbol prefixing, and `noyywrap` have no direct role in the managed
  class-instance scanner and were replaced by GPLEX options.
- GPLEX state helpers do not take a scanner handle.
- GPLEX exposes `yytext` as `string` and `yypos` as a UTF-16 position.
- The GPLEX scanner API returns `int`; `Next()` is a managed adapter which
  stages the current repository's `Token` value.
- `%option noparser` keeps lexer generation independent from GPPG and embeds
  GPLEX's small standalone `ScanBase`/`Tokens` declarations.
  `%scannertype jq_lexer` preserves the jq-shaped generated type name. The
  production GPPG parser consumes it through a thin parser-token adapter.

## Resolved integration decisions

1. `JqGeneratedParserScanner` maps the scanner's jq-shaped managed tokens to
   distinct GPPG token IDs such as `AS`, `IF`, `FIELD`, and `QQSTRING_TEXT`.
2. Production entry points initialize the scanner source and coordinate cursor
   before scanning; they do not expose the generated parameterless frame
   constructor.
3. The local tool manifest pins Springcomp.GPLEX 1.2.5. The production
   generator uses its embedded frame, normalizes volatile output metadata and
   trailing whitespace, and byte-checks the committed `.cs` output.
4. Because GPLEX's line tracker counts characters while jq uses UTF-8 byte
   columns, the production scanner supplies an incremental UTF-16-to-UTF-8
   coordinate cursor. Non-ASCII location and pinned-oracle tests cover it.
5. `StringText` tokens retain raw jq escape runs; the production parser adapter
   decodes them into the GPPG semantic value and emits jq-compatible diagnostics.
6. EOF while inside `IN_QQSTRING` or `IN_QQINTERP` and invalid `@` format
   prefixes flow through the production parser adapter and its oracle-checked
   recovery wording.
7. The production adapter combines the scanner's adjacent `Dot` and
   `Identifier` tokens into the single upstream-shaped `FIELD` parser token.
8. `tools/parser-gen/generate-gplex-lexer.sh` rejects generator warnings and
   `--check` verifies both its source/tool identity header and all output bytes.
   A successful GPLEX run by itself is still not semantic proof.

## Prototype validation performed

On 2026-09-05 this specification was copied unchanged to a scratch directory as
`lexer.l` and processed by Springcomp.GPLEX 1.2.5 (source tag `1.2.5`, commit
`85f12b190c0ab0e6da84711f1b7fa2ee20bf181b`). GPLEX explicitly accepted the
`.l` filename, built/minimized the automaton, and emitted C# without a generator
warning or error.

The emitted scanner then compiled as `net10.0` against a copy of the current
`Token`/`TokenKind` declarations. A scratch differential harness compared it
with the current `jq_lexer`. The comparison included every token's kind, raw
text, UTF-8 offset, UTF-16 source index, line and byte-based column, plus
exception type/message. The exact final totals are recorded below after the
upstream `FIELD` check.

The corpus included operator/longest-match combinations, qualified identifiers
and fields, incomplete exponents, comments at EOF and across escaped newlines,
non-ASCII and malformed UTF-16 coordinates, nested string interpolation,
mismatched closers, malformed escape runs, and unterminated strings and
interpolations. The harness and generated `.cs` remained scratch artifacts and
are intentionally not committed here. This historical scratch comparison
established compatibility with the then-current managed lexer; the later
integrated Release gate established production readiness.

The repository's lexer/parser compatibility slice passed after production
activation. It now exercises the checked-in GPLEX output; the validation copy
in this directory remains deliberately unwired.

The paired production GPPG parser was promoted after a 1,441/1,441 Release
candidate gate. Its guard fixes 167 jq alternatives, 169 generated rules, 312
states, zero conflicts, byte-identical `%precedence` left/right variants, and
the exact 9,994-accepted/9,995-memory-exhausted nesting boundary.

### Upstream `FIELD` boundary validation

Pinned native jq 1.8.2 rejects `.name::part`, `.name::part?`,
`.name::part.foo`, `.name::part | .`, and `.name::part::tail` at the first `:`.
That follows the upstream rule: `FIELD` consumes only `.name`, after which each
colon is a separate token.

The production `jq_lexer` translation now follows this same boundary: it emits
`Dot`, `Identifier("name")`, two `Colon` tokens, and `Identifier("part")`.
The focused repository test preserves the authoritative public rejection
behavior and checks the native oracle's first-colon diagnostic.

After regenerating the production lexer with that correction, the final scratch
differential covered 7,407 distinct sources: **7,407 matched and 0 mismatched**.
This includes `.name::part`, `.xname::part`, and four suffix variants that had
exposed the previous translation difference.

The same 7,407/7,407 result was repeated after removing all current-only lexer
rules. Bare-`@` and unterminated-input compatibility is now implemented only by
the default managed adapter. A separate smoke check confirmed that raw parser
mode instead returns `InvalidCharacter`/`End` for those inputs.

The 7,407-case comparison was repeated directly between the former eager
three-array coordinate implementation and the incremental monotonic cursor:
**7,407 matched and 0 mismatched**, including malformed UTF-16, queued `FIELD`
tokens, and EOF behavior. On the same 17,328-character direct-lex benchmark,
allocation fell from 352,112.020 to 144,080.020 bytes per scan (59.1%); the
nine-process median time fell from 416.457 ms to 390.521 ms for 2,000 scans.

## Ongoing proof requirements

- Run the selected pinned GPLEX version in parse/generate mode on this file.
- Compile its output against the selected `Token`/parser semantic-value surface.
- Differentially compare complete token streams, raw text, UTF-8 spans, and
  parser results over the existing boundary suites and a generated adversarial
  corpus.
- Prove precedence/conflict expectations in the separate GPPG grammar pipeline;
  the lexer cannot provide that guardrail.
