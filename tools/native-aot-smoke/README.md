# NativeAOT smoke test

This .NET 10 project references the production `DotNetJq` project and compiles
and executes jq filters that exercise the production GPPG-generated parser,
nested fields, iteration, selection, object construction, and string
interpolation. It also exercises jq-visible global regex scanning with a POSIX
character class and all four gamma-family calls that cross the replaceable
`DotNetJq.GlibcCompat` assembly boundary. A frozen time case exercises
`gmtime`/`localtime`, `strftime`/`strptime`, `mktime`, `todate`, and POSIX
time-zone/DST-rule parsing without reflection or a native time library. The
smoke also reaches the broader public library graph through pull-based
`StartExecution`/`TryRead` and terminal `Outcome`, a positioned primary input,
explicit `IJqInputSource` plus debug/stderr `IJqValueSink` capabilities, and a
`JqInMemoryFileSystem`/`JqModuleResolver` compilation. These three cases bring
the executable smoke to nine independently checked scenarios. The
expected compact JSON values are
frozen from pinned jq 1.8.2 commit
`34f7186b86743a083a589741b6cea95293524108`. It uses the same parser path as a
normal `DotNetJq` build; there is no candidate switch or fallback parser.

The dedicated trim-safety case compiles and executes a filter containing a
comment, UTF-8 source text, interpolation, numbers, keywords, and every jq
delimiter family. Before publishing, the verifier also rejects the legacy
GPLEX reflection lookup and requires the generated scanner's statically rooted
`Tokens maxParseToken` constant. This makes the smoke an explicit guard for the
lexer path that NativeAOT trimming previously could not prove reachable.

This smoke was included in the generated-parser promotion work alongside the
1,441/1,441 Release candidate gate. The parser is built from the maintained
C#-action `src/DotNetJq/Grammar/parser.y`; jq's original C-action grammar is not copied into
the repository. Its generated shape is guarded at 167 jq alternatives, 169
rules, 312 states, and zero conflicts, including the byte-identical
`%precedence` left/right-table check and the 9,994/9,995 nesting boundary.

Run the complete publish-and-execute check from any directory:

```bash
tools/native-aot-smoke/verify.sh
```

The verifier publishes with `PublishAot=true` into a fresh temporary directory,
runs the native executable, lists and hashes the published artifacts, and checks
the complete publish tree recursively to ensure no GPPG or GPLEX generator DLL
was shipped. The generated scanner is normal C# source compiled into `DotNetJq`;
the generator tools are build-time inputs.
