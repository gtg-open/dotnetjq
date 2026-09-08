# `jv_parser` incremental, sequence, and stream audit

Audit date: 2026-09-05
Upstream: jq 1.8.2, commit `34f7186b86743a083a589741b6cea95293524108`

## Scope and source

The managed mapping in `src/DotNetJq/Port/src/jv_parse.c.cs` was compared
function-by-function with pinned upstream `src/jv_parse.c`, specifically:

- `parser_init`, `parser_reset`, and the opaque `struct jv_parser` state;
- `parse_token`, `stream_token`, `scan`, and literal/string completion;
- `parse_check_done` and `stream_check_done` output scheduling;
- `jv_parser_new`, `jv_parser_set_buf`, `jv_parser_remaining`,
  `jv_parser_next`, and `jv_parser_free`;
- `JV_PARSE_SEQ`, `JV_PARSE_STREAMING`, and `JV_PARSE_STREAM_ERRORS`.

The behavioral oracle was the pinned jq 1.8.2 release binary. Relevant
unchanged upstream coverage is in `tests/shtest`: JSON-sequence recovery and
truncation, adjacent top-level inputs, streaming leaf/end events,
`--stream-errors`, broken object-pair diagnostics, and non-scalar object-key
diagnostics. `tests/utf8test` establishes that partial reads must not corrupt
multi-byte text.

## Managed mapping

The jq-shaped API is internal because it is a compatibility-core input
primitive, not a general replacement for `System.Text.Json`:

| Upstream symbol/behavior | Managed mapping |
|---|---|
| `jv_parser_new(flags)` / `jv_parser_free()` | Explicit `jv_parser` lifecycle with the same parser flags and reset state |
| `jv_parser_set_buf(..., is_partial)` | `ReadOnlyMemory<byte>` and byte-array overloads; replacement is allowed only after the previous buffer is exhausted |
| `jv_parser_remaining()` | Exact count of unconsumed bytes in the current post-BOM buffer |
| `jv_parser_next()` | Pull result: valid `jv`, invalid-with-message parse error, or message-free invalid sentinel for need-more/end |
| partial byte buffers | Token/string/escape state survives arbitrary boundaries, including UTF-8 and BOM splits |
| adjacent JSON values | Each completed top-level value is returned without consuming a later value beyond the upstream scanner step |
| `JV_PARSE_SEQ` | Initial RS synchronization, truncation categories, line/byte-column tracking, and subsequent-record recovery |
| `JV_PARSE_STREAMING` | `[path,value]` leaves plus one-element container-end events in upstream order |
| `JV_PARSE_STREAM_ERRORS` | Parse errors become `[message,path]`; without streaming the flag is inert |
| malformed UTF-8 inside strings | Raw bytes are retained until string completion and repaired through the existing jq-compatible `jv_string_sized` boundary |

No production code invokes jq, native code, or a process. No expected upstream
fixture was changed and no mismatch allowlist is used.

## Focused verification

`JvParserStreamingCompatibilityTests` contains 34 cases. It covers:

- all single-byte chunk boundaries for adjacent containers and BMP/non-BMP
  strings;
- every two-chunk split of normal, streaming, and sequence/stream-error
  documents;
- a three-byte BOM split across buffers;
- UTF-8 and `\uXXXX\uXXXX` splits and malformed UTF-8 repair;
- exact remaining-byte and completion behavior;
- root/nested stream leaves, empty containers, and end markers;
- exact current paths for syntax and EOF stream-error values;
- the unchanged `shtest` sequence recovery/truncation vector;
- syntax-error resynchronization and the distinct abandoned, unfinished,
  invalid-literal, separator, and potentially-truncated numeric categories;
- UTF-8 byte-based error columns.

Focused command and result:

```text
dotnet test tests/DotNetJq.Tests/DotNetJq.Tests.csproj --no-restore \
  --filter FullyQualifiedName~JvParserStreamingCompatibilityTests -m:1

Passed: 34, Failed: 0, Skipped: 0
```

The separate CLI fixture audit maps its sequence/stream exclusions to these
library tests; the managed API itself remains independent of the CLI layer.
