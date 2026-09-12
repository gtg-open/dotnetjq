# DotNetJq

[![Full semantic compatibility](https://github.com/gtg-open/dotnetjq/actions/workflows/semantic-compatibility.yml/badge.svg)](https://github.com/gtg-open/dotnetjq/actions/workflows/semantic-compatibility.yml)
[![CLI cross-platform verification](https://github.com/gtg-open/dotnetjq/actions/workflows/cli-cross-platform.yml/badge.svg)](https://github.com/gtg-open/dotnetjq/actions/workflows/cli-cross-platform.yml)

DotNetJq is a managed .NET library and a cross-platform `dotnetjq` command-line
tool implementing jq 1.8.2 behavior. Its semantic source of truth is
[jq 1.8.2](https://github.com/jqlang/jq/releases/tag/jq-1.8.2), commit
[`34f7186`](https://github.com/jqlang/jq/commit/34f7186b86743a083a589741b6cea95293524108).

The library does not launch `jq`, use P/Invoke, or require an upstream checkout
at runtime. The CLI is available as a RID-selected .NET tool package and as
self-contained NativeAOT executables for eight 64-bit targets.

## Library quick start

`DotNetJq.Library` targets .NET 10. Package publication begins with version
1.0.0; until it appears on NuGet, build the project from source.

```sh
dotnet add package DotNetJq.Library --version 1.0.0
```

```csharp
using DotNetJq;

using var jq = JqProgram.Compile(".items | map(.price) | add");
var outputs = jq.Execute(
    """{"items":[{"price":12},{"price":8}]}""");

foreach (var output in outputs)
{
    Console.WriteLine(output.GetRawText());
}
```

Output:

```text
20
```

`JqProgram` is reusable sequentially, but it and its active execution cursor
are not thread-safe. Compile independent programs for concurrent execution.
See the [library guide](https://github.com/gtg-open/dotnetjq/blob/main/docs/library.md)
for lazy output, explicit terminal outcomes, modules, stateful I/O, and resource
controls.

## CLI quick start

After the first packages are published:

```sh
dotnet tool install --global dotnetjq --version 1.0.0 --framework net10.0
printf '%s\n' '{"items":[{"price":12},{"price":8}]}' |
  dotnetjq -c '.items | map(.price) | add'
```

From this source tree:

```sh
dotnet restore DotNetJq.sln
dotnet run --project src/DotNetJq.Cli --configuration Release -- \
  -c '.items | map(.price) | add' input.json
```

The installed command is `dotnetjq`; no `jq` alias is installed automatically.
It follows jq's public filter, option, input/output, module, diagnostic, halt,
and exit-status behavior while identifying itself explicitly:

```text
dotnetjq-1.0.0 (jq-1.8.2 compatible)
```

Use the [jq 1.8 manual](https://jqlang.org/manual/v1.8/) and
[jq tutorial](https://jqlang.org/tutorial/) for the jq language. The
[DotNetJq CLI guide](https://github.com/gtg-open/dotnetjq/blob/main/docs/cli.md)
documents only product-specific installation, invocation, platform, and
compatibility details.

## Platforms

NativeAOT archives and RID-selected tool packages are built and tested for:

| Windows | Linux glibc | Linux musl | macOS |
|---|---|---|---|
| `win-x64` | `linux-x64` | `linux-musl-x64` | `osx-x64` |
| `win-arm64` | `linux-arm64` | `linux-musl-arm64` | `osx-arm64` |

The executables include the .NET runtime, but still use OS libraries. Linux
requires system ICU; the glibc builds require glibc 2.39 or newer, and Alpine
must use a musl archive with `icu-libs`. See
[installation](https://github.com/gtg-open/dotnetjq/blob/main/docs/installation.md)
and [PowerShell usage](https://github.com/gtg-open/dotnetjq/blob/main/docs/powershell.md).

## Compatibility and performance

The release gate executes the managed suites, all seven unchanged official jq
fixtures, serialized CLI differential checks, general and regex differential
corpora, NativeAOT smoke tests, package-isolation tests, and the complete
platform matrix. It also repeats the full 879-scenario and 21-workload timed
comparison and blocks a material regression before publication. Exact claims
and exclusions are recorded in the
[compatibility guide](https://github.com/gtg-open/dotnetjq/blob/main/docs/compatibility.md),
[technical port specification](https://github.com/gtg-open/dotnetjq/blob/main/dotnetjq-port-spec.md),
and [porting manifest](https://github.com/gtg-open/dotnetjq/blob/main/porting/PORTING_MANIFEST.json).

Reference workstation Linux x64 process benchmark, measured 2026-09-06:

| End-to-end CLI metric | jq 1.8.2 | DotNetJq NativeAOT | DotNetJq framework |
|---|---:|---:|---:|
| Startup median | 1.996 ms | 9.728 ms | 120.972 ms |
| 879-fixture pooled mean | 1.099 ms | 6.816 ms | 83.138 ms |
| Sum of 20 processing medians | 2.422 s | 8.407 s | 25.720 s |

These are process-level measurements on one host, not in-process library
benchmarks. Every measured scenario first passed exact exit-code, stdout-byte,
and stderr-byte comparison. See the [benchmark guide](https://github.com/gtg-open/dotnetjq/blob/main/docs/benchmarks.md)
and [full evidence](https://github.com/gtg-open/dotnetjq/blob/main/porting/PERFORMANCE_COMPARISON.md).

## Documentation

- [Installation](https://github.com/gtg-open/dotnetjq/blob/main/docs/installation.md)
- [.NET library](https://github.com/gtg-open/dotnetjq/blob/main/docs/library.md)
- [CLI](https://github.com/gtg-open/dotnetjq/blob/main/docs/cli.md)
- [PowerShell](https://github.com/gtg-open/dotnetjq/blob/main/docs/powershell.md)
- [Compatibility](https://github.com/gtg-open/dotnetjq/blob/main/docs/compatibility.md)
- [Benchmarks](https://github.com/gtg-open/dotnetjq/blob/main/docs/benchmarks.md)
- [Versioning and releases](https://github.com/gtg-open/dotnetjq/blob/main/docs/versioning-and-releases.md)
- [Contributing](https://github.com/gtg-open/dotnetjq/blob/main/CONTRIBUTING.md)
- [Security](https://github.com/gtg-open/dotnetjq/blob/main/SECURITY.md)

## Licenses

No single license applies to the complete distribution. Original DotNetJq
material is MIT-licensed. `DotNetJq.GlibcCompat.dll` is a separately replaceable
`LGPL-2.1-or-later` component; NativeAOT executables incorporate it and ship
corresponding source and relinking material. See
[LICENSES.md](https://github.com/gtg-open/dotnetjq/blob/main/LICENSES.md) and
[THIRD_PARTY_NOTICES.md](https://github.com/gtg-open/dotnetjq/blob/main/THIRD_PARTY_NOTICES.md)
for all component terms.
