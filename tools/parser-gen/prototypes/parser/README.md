# Managed GPPG parser validation

This directory contains the isolated behavioral validation harness for the
maintained managed grammar at `src/DotNetJq/Grammar/parser.y`. The repository
does not commit jq's original C-action grammar: the maintained file's
provenance header points to the immutable upstream repository, revision, and
path.

The managed grammar preserves all 167 upstream production alternatives,
their order, token/literal spelling, 14 precedence declarations, action
positions, and five empty alternatives. The C actions are manually ported to
the same `block`/`inst` compiler IR used by jq's `compile.c`. That
language-specific port is analogous to the project's other C-to-C# ports;
generation starts after the managed grammar exists and does not translate C
actions automatically.

## What GPPG cannot spell directly

Pinned Springcomp.GPPG 1.2.5 does not accept several Bison constructs used by
jq. The managed grammar makes each adaptation explicit:

- `%empty` becomes an empty right-hand side with the sole marker
  `/* jq-port: upstream-%empty */`.
- `%precedence` becomes a canonical `%left` line marked
  `/* jq-port: upstream-%precedence */`. The guardrail regenerates a temporary
  `%right` variant and requires byte-identical output, proving associativity is
  not used by these five levels.
- `%expect 0` becomes `jq-port: upstream-%expect 0`; validation requires zero
  conflict diagnostics. It does not use GPPG's broken `/conflicts` option.
- `%locations` is represented by `%YYLTYPE JqParserLocation`; the scanner
  adapter supplies UTF-8 byte offsets, UTF-16 indices, and line/column data.
- Bison `%parse-param` and `%lex-param` values are constructor fields in the
  parser partial class and scanner adapter.
- `%define api.pure` maps naturally to per-parse managed parser/scanner
  instances. `%define parse.error verbose` has no exact switch; jq-exact error
  recovery remains called out below.
- `%destructor` has no GPPG directive. The scanner token ledger therefore owns
  each `<literal>` until grammar actions explicitly take or free it, and frees
  unclaimed lookahead values. `ParserBlockOwner` and the parser literal ledger
  separately own already-reduced `block` graphs and standalone metadata until a
  successful parse transfers the root block, or free them after recovery,
  abort, or stack exhaustion. Focused tests cover direct moves, jq's one-copy
  object-shorthand split, unrecoverable lookahead, and already-reduced IR cleanup.
- `%code requires`, the C `%union`, C helper prologue, and `<blk>` semantic
  types are replaced by C# imports, a managed semantic-value struct, C# helper
  methods, and precise managed nonterminal types.

The structure guardrail lives in
`tools/parser-gen/DotNetJq.ParserGen`. Its embedded expectation is sufficient
for normal offline validation; an upstream checkout is needed only for an
explicit audit or version upgrade.

## Diagnostic and recovery actions

There are no placeholder actions left. Nonconstant/non-object metadata,
nonconstant import paths, constant non-string object keys, and unresolved
`break` labels append source-located diagnostics to the same per-parse list as
syntax errors. The eleven explicit Bison `error` alternatives retain their
upstream recovery `block` values, add only the diagnostics present in jq's
actions, and preserve
diagnostic ordering and accumulation.

Empty/text-only
strings become `LOADK` blocks, binary constant folding calls the ported jq
constant helpers, computed string-key shorthand uses jq's one-copy block
construction, definitions/references remain compiler instructions, and
module/import metadata is retained in `MODULEMETA`/`DEPS` instructions.

## Validation

After `dotnet tool restore`, run:

```bash
python3 tools/parser-gen/prototypes/parser/validate.py --audit-upstream
```

Omit `--audit-upstream` to prove the ordinary workflow does not depend on
`upstream/jq`. Validation does six separate things:

1. checks the exact grammar structure and provenance against the embedded
   pinned expectation;
2. generates with GPPG 1.2.5, requires nonempty output and empty stderr, and
   checks 169 generated rules, 312 states, and 167 action cases;
3. compiles the generated parser and real lexer adapter inside `DotNetJq` with
   nullable analysis and analyzers enabled, rejecting any warning attributed
   to the managed grammar, generated parser, adapter, or internal runtime;
4. runs eleven representative programs through regenerated and checked-in
   production parser paths, checking direct `block`/`inst` shape and execution
   results, plus a dedicated `MODULEMETA`/`DEPS`/definition/reference corpus;
5. checks nineteen invalid inputs covering every translated diagnostic family
   and all eleven explicit recovery alternatives, plus exact bare-format-marker
   and unterminated-string/interpolation cases;
6. proves the source-integrated 10,000-entry parser stack accepts 9,994 nested
   parentheses, rejects 9,995 with bounded `jq: error: memory exhausted`, and
   does not copy a GPPG runtime DLL.
   With `--audit-upstream`, validation also builds a temporary native jq from
   the exact pinned checkout and requires every rendered diagnostic sequence
   to occur verbatim in that oracle's stderr.

The custom MSBuild target is imported only by `validate.py`. It does not alter
`DotNetJq.csproj` or participate in an ordinary build.

## Integrated layout

Generation goes through the guarded pipeline:

```bash
dotnet run --project tools/parser-gen/DotNetJq.ParserGen/DotNetJq.ParserGen.csproj -- \
  generate-parser \
  --parser src/DotNetJq/Grammar/parser.y \
  --output src/DotNetJq/Generated/Parser/JqGeneratedParser.g.cs
```

The generated parser and its scanner/location companion compile through the
ordinary production `DotNetJq` project. Exactly six Springcomp GPPG runtime files are
source-integrated as internal types; the generator remains build-time-only and
no runtime package or DLL is loaded or shipped. The complete upstream BSD
license is retained at `THIRD_PARTY_LICENSES/GPPG-License.md` and packed.

This harness was the promotion gate for the production parser. The complete
Release candidate run passed 1,441/1,441 managed tests before the entry points
were switched permanently; there is no fallback parser path.
