# Managed parser and lexer generation

The maintained managed grammar sources are
`src/DotNetJq/Grammar/parser.y` and `src/DotNetJq/Grammar/lexer.l`. They
preserve the pinned jq grammar/rule structure and contain the required
language-specific C# actions; jq's original C-action Bison/Flex files are not
copied into this repository.

The production scanner is generated from `lexer.l` by Springcomp.GPLEX 1.2.5,
and the production parser is generated from `parser.y` by Springcomp.GPPG
1.2.5. `libjq` parse entry points call the generated parser directly.

GPPG has no Bison `%destructor` directive. The maintained scanner adapter
tracks every `<literal>` owner and frees unclaimed lookahead values. Grammar
actions explicitly take or free those owners, while `ParserBlockOwner` and the
parser literal ledger free already-reduced `block` graphs and standalone
metadata on parse failure. Successful parses transfer one owning `block`
directly to jq's ported linker/compiler pipeline.

`generate.py` remains the deterministic generator for the jq-shaped parser
entry-point declarations, lexer token/location declarations, and
`porting/PARSER_GRAMMAR_COVERAGE.json`. Parser-table generation and verification
are owned by `DotNetJq.ParserGen`. No pipeline treats generation alone as
semantic proof.

## Regenerate the production parser

```bash
dotnet tool restore
dotnet restore src/DotNetJq/DotNetJq.csproj
dotnet run --project tools/parser-gen/DotNetJq.ParserGen/DotNetJq.ParserGen.csproj -- \
  generate-parser \
  --parser src/DotNetJq/Grammar/parser.y \
  --output src/DotNetJq/Generated/Parser/JqGeneratedParser.g.cs
```

Force structural, conflict, and byte-for-byte generated-output checks with:

```bash
dotnet restore src/DotNetJq/DotNetJq.csproj
dotnet run --project tools/parser-gen/DotNetJq.ParserGen/DotNetJq.ParserGen.csproj -- \
  validate \
  --parser src/DotNetJq/Grammar/parser.y \
  --lexer src/DotNetJq/Grammar/lexer.l
dotnet run --project tools/parser-gen/DotNetJq.ParserGen/DotNetJq.ParserGen.csproj -- \
  check-generated-parser \
  --parser src/DotNetJq/Grammar/parser.y \
  --output src/DotNetJq/Generated/Parser/JqGeneratedParser.g.cs
python3 tools/parser-gen/prototypes/parser/validate.py --audit-upstream
```

The managed grammar contains 167 upstream alternatives. Verification requires
169 generated rules (including GPPG's synthetic rule), 312 states, zero
shift/reduce conflicts, and zero reduce/reduce conflicts. Each marked
`%precedence` stand-in is checked by generating `%left` and `%right` variants
and requiring byte-identical parser tables, so the associativity introduced by
the GPPG spelling cannot affect a decision.

The six required Springcomp GPPG runtime source files are compiled internally
into `DotNetJq.dll`; there is no runtime NuGet dependency or GPPG DLL. Their
complete BSD license is `THIRD_PARTY_LICENSES/GPPG-License.md` and is packed.

## Regenerate the scanner

Restore the repository-local tools once, then generate the checked-in scanner:

```bash
dotnet tool restore
tools/parser-gen/generate-gplex-lexer.sh
```

The script rejects generator warnings, normalizes GPLEX's machine/time metadata
and trailing whitespace, internalizes its two frame-only buffer helper types,
replaces its legacy reflective token-sentinel lookup with a statically rooted
typed `Tokens maxParseToken` constant, and prepends the managed-source SHA-256,
pinned tool identity, and generation key. The rewrite requires the exact pinned
frame shape and fails closed on drift. A current key is reused in generate mode.

Always force full regeneration and byte comparison before committing:

```bash
tools/parser-gen/generate-gplex-lexer.sh --check
```

Normal library builds consume the checked-in C# output and do not restore or
execute GPLEX. Scanner regeneration also does not require an upstream checkout;
the explicit structural audit compares the managed source with upstream when
requested.

## Regenerate declarations and the coverage report

From the repository root, with jq commit
`34f7186b86743a083a589741b6cea95293524108` checked out at `upstream/jq`:

```bash
python3 tools/parser-gen/generate.py --upstream upstream/jq
```

The tool also honors `DOTNETJQ_UPSTREAM`, then falls back to
`upstream/jq`. It verifies both the Git commit when metadata is
available and the exact SHA-256 of `src/lexer.l` and `src/parser.y`.

## Check without writing

```bash
python3 tools/parser-gen/generate.py --check --upstream upstream/jq
```

Exit status is zero only when the checked-in declaration outputs and the
structural coverage report match byte-for-byte. The report enumerates all 53 lexer rules,
70 parser tokens (including Bison built-ins and literal tokens), 14 precedence
declarations, and 167 production alternatives from the pinned grammar, with a
managed owner for each item. It explicitly records that ownership coverage is
not Bison state-machine equivalence and is not a semantic-parity claim.
All 11 alternatives containing Bison's special `error` symbol also carry a
deterministic oracle source linked to the exact recovery test matrix.
Normal library builds do not run the generator and do not require an upstream
checkout; the generated outputs are committed.

For a non-overlapping declaration change, pass `--component lexer`
(token/location declarations) or `--component parser` (parse entry-point
declarations). The default is `all` and remains the CI/release check. Scanner
generation is owned by `generate-gplex-lexer.sh`; parser-table generation is
owned by `DotNetJq.ParserGen`.

## Change policy

Never edit generated outputs under `src/DotNetJq/Generated/Lexer` or
`src/DotNetJq/Generated/Parser` directly. Parser support/scanner-adapter files
beside the generated parser are maintained C# sources. For scanner changes:

1. compare behavior to pinned jq `src/lexer.l`;
2. edit `src/DotNetJq/Grammar/lexer.l`;
3. regenerate and run `generate-gplex-lexer.sh --check`;
4. run the structural guardrail, focused parser tests, and full compatibility
   suite;
5. commit managed source and generated output together.

For declaration outputs:

1. compare the behavior to pinned `lexer.l` or `parser.y`;
2. edit the corresponding `.in` template in `tools/parser-gen/templates`;
3. regenerate;
4. run `--check`, the focused parser tests, and the full compatibility suite;
5. commit the template and generated output in the same change.

The generators reject unknown declaration markers, structural drift, generator
warnings, and stale bytes. The CI workflow
`.github/workflows/parser-generation-check.yml` enforces both pipelines.

The current Release gate passes 2,678/2,678 managed library tests with zero skips. Its stack
corpus proves that 9,994 nested parentheses are accepted and 9,995 produce the
bounded `jq: error: memory exhausted` diagnostic. Production has no switch back
to the retired parser implementation.
