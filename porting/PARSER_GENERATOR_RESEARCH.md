# Managed parser-generator research

Status date: 2026-09-05

## Decision

The maintained grammar sources are managed-language ports of jq 1.8.2
`src/parser.y` and `src/lexer.l`. They retain jq's rule order and inline
semantic-action placement, but the actions and generator directives are
translated to C# and GPPG/GPLEX syntax. The original C-action source files are
not copied into this repository; each managed source carries the pinned jq
repository, revision, path, and URL in its header.

Generation uses the repository-local .NET tool manifest, currently pinning
`Springcomp.GPPG` and `Springcomp.GPLEX` 1.2.5. They are generation-time tools
only. Ordinary library builds use committed generated C# and do not require an
upstream checkout or load a parser-generator assembly at runtime. The six
GPPG runtime source files required by the generated parser are source-integrated,
internal to `DotNetJq.dll`, and covered by the complete packaged BSD license;
there is no parser-generator package or runtime DLL dependency.

The corresponding tool source revisions used for the feasibility proof are
`f4634057620757a38789b4d53df73817667d1842` for Springcomp.GPPG and
`85f12b190c0ab0e6da84711f1b7fa2ee20bf181b` for Springcomp.GPLEX. Both tools
target .NET 8; the manifest permits framework roll-forward so generation still
works on a machine that has the pinned .NET 10 SDK/runtime only. Generated code
and the DotNetJq projects compile with the repository's `net10.0` and latest
C# settings.

## Upstream status

The canonical projects are:

- [k-john-gough/gppg](https://github.com/k-john-gough/gppg), revision
  `a755a54c9a268fe6d095fa8ba8e13b7dc0550cc2` (embedded version 1.5.2);
- [k-john-gough/gplex](https://github.com/k-john-gough/gplex), revision
  `8e9c91b39e7483c2fa937b8af5889d9e78ba4cf7` (embedded version 1.2.2).

Both canonical repositories' default branches still end at commits dated
2020-11-04, and neither repository has tagged releases. The maintainer has
stated that he is retired and only expects to address breaking .NET changes.
In 2023 he invited Ernesto Cortes to manage both projects; Ernesto accepted in
principle, but no repository transfer is visible:

- [maintenance statement](https://github.com/k-john-gough/gppg/issues/5#issuecomment-1140355657);
- [maintenance-transfer offer](https://github.com/k-john-gough/gppg/issues/8#issuecomment-1751911191);
- [Ernesto Cortes's response](https://github.com/k-john-gough/gppg/issues/8#issuecomment-1817316738).

The complete visible issue and pull-request histories contain no request or
implementation for `%empty`, `%precedence`, `%expect`, jq's `%define` forms,
`%destructor`, `%parse-param`, or `%lex-param`:

- [GPPG issues](https://github.com/k-john-gough/gppg/issues?q=is%3Aissue) and
  [pull requests](https://github.com/k-john-gough/gppg/pulls?q=is%3Apr);
- [GPLEX issues](https://github.com/k-john-gough/gplex/issues?q=is%3Aissue) and
  [pull requests](https://github.com/k-john-gough/gplex/pulls?q=is%3Apr).

The actively maintained Ernesto Cortes lineage consists of the actual
[ernstc/gppg](https://github.com/ernstc/gppg) and
[ernstc/gplex](https://github.com/ernstc/gplex) generator forks, currently
packaged as GPPG 1.5.3.1 and GPLEX 1.2.3.1, plus the
[YaccLexTools](https://github.com/ernstc/YaccLexTools) aggregator/tooling
repository. Open 2026 pull requests propose .NET 10 target-framework and
package updates for [GPPG](https://github.com/ernstc/gppg/pull/2) and
[GPLEX](https://github.com/ernstc/gplex/pull/2), but no grammar-language
changes. The [Springcomp GPPG](https://github.com/springcomp/gppg)/
[GPLEX](https://github.com/springcomp/gplex) forks used here likewise modernize
target frameworks and packaging without adding the missing Bison directives.

GPPG's accepted directives are hard-coded in its
[grammar](https://github.com/k-john-gough/gppg/blob/master/ParserGenerator/SpecFiles/gppg.y)
and
[lexer](https://github.com/k-john-gough/gppg/blob/master/ParserGenerator/SpecFiles/gppg.lex).
It has no plugin, delegate, or registry extension point through which a consumer
can add `%empty`, `%precedence`, `%expect`, or another grammar directive without
changing and rebuilding the generator. GPLEX has one narrower, unrelated
extension: `%userCharPredicate` can load an external `ICharTestFactory`, as
implemented in
[AAST.cs](https://github.com/k-john-gough/gplex/blob/master/GPLEX/AAST.cs) and
[CharClassUtils.cs](https://github.com/k-john-gough/gplex/blob/master/GPLEX/CharClassUtils.cs).
That hook adds character predicates only; it cannot extend GPPG's directive
language or remove jq's grammar adaptations.

There is consequently no canonical or maintained version to which this port
can switch without retaining adaptations or first contributing those features
to a generator fork.

## Directive mapping and guards

The supported declarations `%left`, `%right`, `%nonassoc`, and `%prec`, along
with ordinary empty right-hand sides and inline C# actions, map directly.
Locations are always enabled by GPPG.

The unsupported declarations have explicit managed equivalents:

| jq/Bison construct | Managed treatment |
|---|---|
| `%empty` | Empty right-hand side with `jq-port: upstream-%empty` marker |
| `%expect 0` | Generate conflict report and require zero S/R and R/R records |
| `%precedence` | Committed `%left` stand-in with a machine-readable marker; generate a `%right` variant and require identical parse tables |
| `%define api.pure` | Parser and scanner instance state |
| `%define parse.error verbose` | Managed diagnostic adapter and differential tests |
| `%destructor` | Scanner token ledger plus `JqParsedValueConstructionScope`; aborted parses explicitly free unclaimed tokens, reduced AST literals, and standalone metadata |
| `%parse-param`, `%lex-param` | Constructors and instance fields |

Using `%nonassoc` as a `%precedence` substitute is not accepted: it can silently
insert an error action for an equal-precedence decision. The left-versus-right
table comparison proves that artificial associativity was not consulted. Both
passes must also have the expected unresolved-conflict count. GPPG conflict
reporting is described by its
[LR generator](https://github.com/k-john-gough/gppg/blob/master/ParserGenerator/LR0Generator.cs),
and Bison's intended `%expect` behavior is documented in the
[GNU Bison manual](https://www.gnu.org/software/bison/manual/html_node/Expect-Decl.html).

Springcomp.GPPG 1.2.5 has a packaging-fork regression in `/conflicts`: that
option creates zero-byte parser/conflict files. The local verifier therefore
runs plain generation, captures the generator's anchored shift/reduce and
reduce/reduce diagnostics from standard error, requires a fresh nonempty parser
output, and checks that count. The process exit code is not sufficient because
this GPPG lineage can return success for invalid grammar input.

## Lexer-specific mapping

GPLEX supports jq's inclusive/exclusive states, state stack, state-qualified
rules, EOF rules, `yyless`, and longest-match rule selection. C/Flex-only
options such as `reentrant`, `bison-bridge`, allocation hooks, and a C symbol
prefix become scanner instance state or managed type/namespace declarations.

GPLEX scans .NET UTF-16 text, whereas jq reports source positions in UTF-8
bytes. The production managed lexer therefore retains a UTF-16-to-UTF-8
coordinate map; non-ASCII location and parser-diagnostic tests guard that
translation.

## Upgrade path

Re-check the canonical and YaccLexTools histories when changing generator
versions. A future unmodified upgrade is allowed only when the generated-table
guards, structural grammar checks, malformed-input diagnostics, lexer token
differential, parser tests, official jq tests, and Native AOT smoke test all
pass.

## Integration status

The maintained parser grammar is `src/DotNetJq/Grammar/parser.y`; its
deterministic production output is
`src/DotNetJq/Generated/Parser/JqGeneratedParser.g.cs`. Production `libjq`
entry points call this generated parser. The managed grammar has 167 jq
production alternatives; GPPG emits 169 rules (including its synthetic rule)
and 312 states, with zero shift/reduce and zero reduce/reduce conflicts.

The `%precedence` compatibility guard generates both `%left` and `%right`
variants for every marked stand-in and requires byte-identical parser tables.
That proves the artificial associativity is never consulted. The pre-promotion
Release gate passed 1,441/1,441 managed tests, including the exact parser-stack
boundary: 9,994 nested parentheses are accepted and 9,995 fail with the bounded
`jq: error: memory exhausted` diagnostic. The production parser contains no
fallback to the retired handwritten implementation.
