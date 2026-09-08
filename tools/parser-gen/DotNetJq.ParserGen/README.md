# jq managed grammar guardrail

This .NET 10 tool enforces the workflow in which the repository maintains
managed GPPG/GPLEX inputs with C# actions instead of copied jq C-action source
files. It does not commit jq's C-action `parser.y` or `lexer.l`, modify
production projects, or read an external upstream checkout during the normal check.

## Managed-source contract

The managed parser and lexer carry source coordinates, not a copied upstream
file hash:

```text
jq-port-upstream-repository: https://github.com/jqlang/jq
jq-port-upstream-revision: 34f7186b86743a083a589741b6cea95293524108
jq-port-upstream-path: src/parser.y
```

The URL, immutable commit, and source path are the provenance. The normal
check validates those literal coordinates without fetching or hashing an
external file.

Unsupported Bison syntax is represented by comments that stock GPPG ignores:

```yacc
/* jq-port: upstream-%expect 0 */

%left FUNCDEF /* jq-port: upstream-%precedence */

Module:
  /* jq-port: upstream-%empty */ {
    $$ = Nodes.NoOp();
  }
;
```

`%left` is the canonical checked-in stand-in for `%precedence`. `%nonassoc` is
not equivalent: at equal precedence it silently installs a syntax-error
action, whereas Bison `%precedence` deliberately supplies no associativity.

## Normal offline check

After the repository-local tools have been restored, the intended check is:

```bash
dotnet run --project tools/parser-gen/DotNetJq.ParserGen/DotNetJq.ParserGen.csproj -- \
  validate \
  --parser src/DotNetJq/Grammar/parser.y \
  --lexer src/DotNetJq/Grammar/lexer.l
```

`validate` performs all of these locally:

1. validates the provenance comments and the exact compatibility markers;
2. rejects raw `%expect`, `%precedence`, and `%empty` in the GPPG input;
3. compares the normalized managed structure with the embedded
   `jq-1.8.2.structure.json` expectation;
4. runs `dotnet tool run dotnet-gppg --` on the checked-in `%left` form and a
   temporary `%right` variant;
5. requires zero unresolved conflicts from each run and byte-identical
   generated parsers.

The normalized parser fingerprint covers ordered token declarations,
GPPG-required token-alias normalization only in precedence declarations, all
14 ordered precedence declarations, all 167 ordered alternatives with exact
symbol spelling, semantic-action positions, and all five
explicit-empty markers. The lexer fingerprint covers all six start-condition
declarations and all 53 ordered rule patterns/scopes. Its sole spelling
normalization is GPLEX-required `[+\-]` to Flex `[+-]`. C versus C# action bodies,
formatting, comments, generator options, `%type`, `%union`, destructors, and
parameter plumbing are intentionally excluded. Audit comparison still rejects
removing an upstream `%type` symbol, but permits additional managed typing.

The JSON SHA-256 values are hashes of these normalized structural records, not
hashes of external jq files. They let an ordinary build detect managed grammar
drift while remaining independent of any upstream checkout.

The parser output path is likewise deterministic:

```bash
dotnet run --project tools/parser-gen/DotNetJq.ParserGen/DotNetJq.ParserGen.csproj -- \
  generate-parser --parser src/DotNetJq/Grammar/parser.y \
  --output src/DotNetJq/Generated/Parser/JqGeneratedParser.g.cs

dotnet run --project tools/parser-gen/DotNetJq.ParserGen/DotNetJq.ParserGen.csproj -- \
  check-generated-parser --parser src/DotNetJq/Grammar/parser.y \
  --output src/DotNetJq/Generated/Parser/JqGeneratedParser.g.cs
```

It pins `springcomp.gppg` 1.2.5 from the local `dotnet-tools.json`, normalizes
only GPPG's input-path/timestamp comments, and prepends the managed-source
SHA-256, tool identity, and a generation key. `generate-parser` reuses an
output whose input/tool key already matches; `check-generated-parser` always
regenerates in a temp directory and compares every byte. Generated output is
intended to be committed, so consuming `DotNetJq` does not run or restore the
tool. The lexer agent owns the corresponding GPLEX production pipeline; this
tool deliberately does not create a competing lexer generator.

Springcomp.GPPG 1.2.5 has a `/conflicts` regression: its code generator returns
immediately after opening the conflict file and leaves both the parser and the
conflict file empty while exiting successfully. The tool therefore does
plain generation, counts its anchored `Shift/Reduce conflict, state ...` and
`Reduce/Reduce conflict, state ...` stderr diagnostics, requires a fresh
nonempty parser output, and treats generator errors as failures.

## Explicit upstream audit mode

An upstream checkout is used only when deliberately auditing or upgrading the
port:

```bash
dotnet run --project tools/parser-gen/DotNetJq.ParserGen/DotNetJq.ParserGen.csproj -- \
  compare \
  --upstream-parser /checkout/src/parser.y \
  --managed-parser src/DotNetJq/Grammar/parser.y \
  --upstream-lexer /checkout/src/lexer.l \
  --managed-lexer src/DotNetJq/Grammar/lexer.l
```

This compares the same normalized records while ignoring C/C# action contents.
`capture-expectations` is a separate, explicit audit command for an intentional
upstream version change. It is not part of normal build or validation.

## Tests

```bash
dotnet build tools/parser-gen/DotNetJq.ParserGen/DotNetJq.ParserGen.csproj -c Release
dotnet run --project tools/parser-gen/DotNetJq.ParserGen/DotNetJq.ParserGen.csproj \
  -c Release --no-build -- self-test --gppg dotnet-gppg
```

The tests include positive C-to-C# action substitution, precedence
associativity drift, missing `%empty`, raw unsupported directives, provenance
drift, normalized expectation drift, lexer pattern drift, a safe
precedence-only grammar, and an unsafe grammar where `%left`/`%right` produce
different tables, plus an unresolved-conflict grammar that violates `%expect
0`. With the local manifest tool they also cover parser output generation,
key-based reuse, byte-for-byte checking, and stale-output failure.

## Exact limitations

- Structural equality does not prove that translated C# actions have the same
  semantics as jq's C actions. Compatibility and differential tests remain the
  oracle for that porting work.
- Equal left/right GPPG output proves that the annotated precedence-only levels
  did not resolve an associativity decision in GPPG's automaton. It does not by
  itself prove whole-table equivalence between GPPG and Bison.
- The conflict diagnostic parser and generated-output comparison are pinned to
  Springcomp.GPPG 1.2.5 behavior. A tool upgrade requires focused revalidation.
- The grammar readers intentionally support the jq 1.8.2 Bison/Flex forms;
  they are not general Bison, Flex, GPPG, or GPLEX parsers.
- Lexer options and action implementations are language/generator-specific and
  excluded. Start-condition kind/name/order and rule scope/pattern/order are
  included.
- This tool is an explicit generation/audit command. Ordinary `DotNetJq`
  compilation consumes checked-in generated output and does not invoke it.
