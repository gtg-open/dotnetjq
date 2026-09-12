# Compatibility

DotNetJq targets jq 1.8.2 at commit
[`34f7186b86743a083a589741b6cea95293524108`](https://github.com/jqlang/jq/commit/34f7186b86743a083a589741b6cea95293524108).
Every upstream source file and official test category is classified as `PORT`,
`PROXY`, `GENERATED`, `REUSE`, or `OMITTED` in the
[porting manifest](../porting/PORTING_MANIFEST.json).
The [technical port specification](../dotnetjq-port-spec.md) defines the
conversion and verification rules.

## What is tested

The release gate requires:

- the complete managed library and CLI process suites with zero skipped tests;
- every record in all seven unchanged official jq `.test` fixtures;
- exact CLI exit status, stdout bytes, and stderr bytes across all 879 official
  fixture scenarios for framework .NET and NativeAOT;
- the general differential corpus and the complete declared regex corpus;
- frozen exact numeric, time, Unicode, parser, ownership, and regex oracles;
- the unchanged portable upstream shell drivers;
- NativeAOT smoke, corresponding-source/relink, isolated-package, and all eight
  platform/RID release-package gates.

The official fixture runner uses jq semantic equality, exactly as upstream
`jq_test.c` does. The separate serialized CLI preflight is stricter and catches
differences such as object-member output order.

## Implementation boundaries

The compiler and VM preserve jq-shaped files, function names, IR, bytecode, and
copy/move/free ownership points. The lexer and parser are generated from
maintained GPLEX/GPPG grammars whose actions are close C-to-C# ports. The exact
upstream `builtin.jq` is embedded as a resource.

Some native subsystems are compatibility proxies:

- .NET regular expressions plus jq/Oniguruma compatibility logic replace the
  native Oniguruma engine;
- managed UTF-8, numeric-formatting, filesystem, threading, and allocation
  primitives replace their C/OS implementations;
- a managed glibc-compatible component reproduces jq-visible gamma-family
  results and remains separately replaceable in the managed package.

These results claim jq-visible behavior within the explicit manifest scopes.
They do not claim native libjq ABI, object layout, allocation timing, VM address
identity, Flex/Bison state identity, or Oniguruma bytecode/API identity.

## Explicit limitations

- Regex parity is measured across official, generated, and frozen adversarial
  corpora; it is not a mathematical proof over every possible regex program.
- Time behavior targets the Linux glibc 2.39 C-locale contract. Non-C locale
  data, leap-second `right/*` zones, absolute `TZ` paths, other-libc extensions,
  tzdata drift, and Windows-only no-TZif fallback details are excluded.
- Cooperative cancellation and timeouts are checked at managed boundaries; a
  blocking host callback must be bounded by the host.
- Compilation has no universal source-size/time quota, output limits apply after
  value construction/serialization, and no general intermediate-memory quota
  exists.
- Physical filesystem root checking is capability-based policy with a check/open
  TOCTOU window, not an OS sandbox.
- Windows byte-oriented standard streams intentionally avoid native `jq.exe`
  CRT CRLF translation.

For exact evidence and remaining risk language, see
[PORTING_STATUS.md](../porting/PORTING_STATUS.md),
[SECURITY_RESOURCE_AUDIT.md](../porting/SECURITY_RESOURCE_AUDIT.md), and
[WINDOWS_STDIO_CONTRACT.md](../porting/WINDOWS_STDIO_CONTRACT.md).
