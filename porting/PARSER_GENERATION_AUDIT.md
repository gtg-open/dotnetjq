# Parser and lexer generation audit

Audit date: 2026-09-06
Semantic source: jq 1.8.2 commit
`34f7186b86743a083a589741b6cea95293524108`

## Verdict

Production parsing is generated from maintained managed grammar sources:

- `src/DotNetJq/Grammar/lexer.l` is processed by pinned Springcomp.GPLEX
  1.2.5 into `src/DotNetJq/Generated/Lexer/lexer.c.cs`;
- `src/DotNetJq/Grammar/parser.y` is processed by pinned Springcomp.GPPG
  1.2.5 into `src/DotNetJq/Generated/Parser/JqGeneratedParser.g.cs`;
- `JqGeneratedParserScanner.cs` and `JqGeneratedParserSupport.cs` adapt the
  generated tables to jq-shaped tokens, locations, `block`/`inst` compiler IR,
  diagnostics, and explicit value ownership;
- `parser.h.cs` exposes the jq-shaped parse entry points, which call
  `JqGeneratedParser` directly.

The managed `.l` and `.y` files are language-specific C# action ports of the
pinned jq inputs. They preserve upstream rule/alternative order and inline
action placement. jq's original C-action grammar files are not copied into this
repository; immutable repository, revision, path, URL, and source hash
coordinates are recorded instead. This is the same source-porting policy used
for the other C-to-C# mappings.

The generated GPPG parser is the production parser. There is no runtime switch
or fallback to a handwritten or template-expanded parser implementation.

## Structural result

The production parser guard requires all of these facts simultaneously:

| Property | Required and observed result |
| --- | ---: |
| Managed jq production alternatives | 167 |
| Generated GPPG rules | 169, including GPPG's synthetic rule |
| Generated LR states | 312 |
| Shift/reduce conflicts | 0 |
| Reduce/reduce conflicts | 0 |
| jq precedence declarations represented in the coverage audit | 14 |
| Explicit jq `error` alternatives with oracle cases | 11 |

`DotNetJq.ParserGen` validates the managed grammar against an embedded pinned
expectation during ordinary offline work. With an upstream checkout, its
explicit comparison additionally verifies the order and spelling of all 167
alternatives against jq 1.8.2. Generation output is byte-checked, so a grammar,
tool, normalization, or generated-file drift fails verification.

The GPLEX side likewise guards all 53 ordered lexer rules and six declared
start conditions. The managed scanner retains jq's longest-match boundaries,
start-condition behavior, delimiter state, comment continuation, quoted-string
segmentation, interpolation, and UTF-8 byte locations while using C# actions and
managed instance state. The deterministic GPLEX normalization also replaces its
legacy reflection lookup with a statically rooted `Tokens maxParseToken`
constant; the NativeAOT smoke publishes and runs the generated lexer/compile
path after rejecting reflection-based output.

## Precedence and conflict guard

GPPG accepts `%left`, `%right`, `%nonassoc`, and `%prec`, but not Bison's
`%precedence`. Each managed stand-in is therefore written as `%left` with the
marker `jq-port: upstream-%precedence`.

The verifier does not trust the artificial associativity. It generates one
temporary grammar in which every marked declaration is `%left` and another in
which every marked declaration is `%right`, then requires byte-identical parser
tables. Both generations must also have zero shift/reduce and zero
reduce/reduce conflicts. Any equal-precedence parser decision affected by the
stand-in therefore fails the build instead of silently changing jq semantics.

Other explicit directive translations are:

| jq/Bison construct | Managed treatment |
| --- | --- |
| `%empty` | Empty right-hand side with an upstream marker |
| `%expect 0` | Generator diagnostics are captured and both conflict counts must be zero |
| `%define api.pure` | Per-parse parser and scanner instances |
| `%define parse.error verbose` | Managed diagnostic adapter plus exact differential cases |
| `%destructor` | Scanner and parser literal ledgers plus `ParserBlockOwner`; aborted parses free unclaimed tokens, reduced `block` graphs, and standalone metadata like native parser/block unwind |
| `%parse-param`, `%lex-param` | Constructors and instance fields |
| C `%union` and prologue | Typed managed semantic values and C# helper methods |

Springcomp.GPPG has no consumer extension point for adding these directives.
The adaptations consequently remain visible in the maintained managed grammar
until an upstream generator gains compatible syntax and passes the same guards.

## Reproducibility and runtime boundary

The repository-local tool manifest pins Springcomp.GPPG and Springcomp.GPLEX
1.2.5. Generation is a development/release operation. Normal builds compile
the checked-in C# output and do not need the upstream jq checkout.

Six BSD-licensed GPPG runtime source files are compiled as internal types into
`DotNetJq.dll`. No parser-generator NuGet dependency or GPPG/GPLEX DLL is loaded
or shipped at runtime. The complete license text is retained in
`THIRD_PARTY_LICENSES/GPPG-License.md` and packed with the library.

Generated parser output records the managed grammar digest, generator identity,
command, and generation key. GPLEX output receives the equivalent normalized
header after volatile machine/time metadata and trailing whitespace are
removed. Repeating either pipeline must produce the same checked-in bytes.

## Semantic evidence

Generation is structural evidence, not semantic proof. Promotion required the
generated parser to pass the existing compatibility tests without weakening,
deleting, or allowlisting failures. The pre-promotion Release candidate gate
passed 1,441/1,441 managed tests.

The corpus covers, among other parser/lexer boundaries:

- literals, identifiers, qualified names, variables, formats, comments, and
  every jq operator family;
- pipe, comma, alternative, logical, comparison, arithmetic, assignment, and
  postfix precedence;
- arrays, objects, computed keys, shorthand pairs, indexing, slicing, and
  iteration;
- definitions, value/filter parameters, bindings, destructuring, and `?//`;
- `if`, `try`, `reduce`, `foreach`, labels, breaks, modules, imports, and
  metadata;
- segmented strings, nested interpolation, raw control characters, invalid
  escapes, and source-located diagnostics;
- all 11 explicit grammar recovery alternatives and their closest valid
  controls;
- library-only parsing, private builtin syntax, reference resolution, constant
  folding, and imported-source filenames.

The grammar actions call the ported jq compiler helpers directly and return the
same owning `block`/`inst` graph shape consumed by `builtins_bind()`, the module
linker, `block_compile()`, and the bytecode VM. No parser AST or AST-to-IR
translation layer remains.

The source-integrated GPPG stack also preserves jq's exact observable balanced-
parenthesis boundary: 9,994 levels are accepted; 9,995 report the bounded
`jq: error: memory exhausted` diagnostic. This replaces the earlier lower
managed-recursion guard and prevents a process-crashing CLR recursion path.

## Regeneration and verification

From the repository root:

```sh
dotnet tool restore
tools/parser-gen/generate-gplex-lexer.sh --check
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

The last command is the explicit pinned-upstream/oracle audit. Omit
`--audit-upstream` to prove that ordinary generation validation is independent
of the external checkout.

## Completion scope

The generated artifacts are complete for the declared observable jq-language,
token, location, recovery, and resource-boundary scope. The project does not
claim byte identity with Flex/Bison C output, automatic translation of C action
bodies, or identity with Bison's private table numbering and recovery internals.
Those exclusions do not introduce a second production parser: GPPG-generated
tables are the sole managed parser path.
