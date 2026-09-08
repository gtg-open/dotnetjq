# Command-line interface

The executable is `dotnetjq`. It is intended as a jq 1.8.2-compatible command
boundary, with explicit product branding.

```sh
printf '%s\n' '{"users":[{"name":"Ada"},{"name":"Grace"}]}' |
  dotnetjq -r '.users[].name'

dotnetjq -n -c --arg name 'Ada Lovelace' '{hello:$name}'
dotnetjq -e '.ready' input.json
```

Use the canonical [jq 1.8 manual](https://jqlang.org/manual/v1.8/) for filter
syntax and standard jq options. `dotnetjq --help` shows the supported command
surface. DotNetJq-specific details are below.

## Identity

```text
$ dotnetjq --version
dotnetjq-1.0.0 (jq-1.8.2 compatible)

$ dotnetjq --build-configuration
net10.0; NativeAOT; linux-x64; jq-1.8.2 compatible
```

Diagnostics retain jq's `jq:` prefix so existing error handling remains useful.
Help, version, and build-configuration output identify DotNetJq intentionally.

## Command name

Installation does not replace an existing `jq`. For an interactive POSIX shell,
create an alias deliberately:

```sh
alias jq='dotnetjq'
```

For child processes that search `PATH` for an executable named `jq`, use a
private wrapper or symlink only after checking for a collision:

```sh
command -v jq
mkdir -p "$HOME/.local/bin"
ln -s "$(command -v dotnetjq)" "$HOME/.local/bin/jq"
```

An alias does not affect child processes. Some scripts also insist that
`jq --version` begin with `jq-`; DotNetJq truthfully reports `dotnetjq-...` and
will not satisfy that brand check.

## Files, streams, and exit status

Standard streams and named files are byte-oriented on every OS. UTF-8 is the
normal JSON/text boundary. Windows `-b` is accepted and idempotent because
binary mode is already the default. This differs intentionally from native
`jq.exe` CRT text-mode newline translation; see the
[Windows stdio contract](../porting/WINDOWS_STDIO_CONTRACT.md).

With `-e`, inspect the native process status. In POSIX shells:

```sh
if dotnetjq -e '.ready' input.json >/dev/null; then
  echo ready
fi
```

See the [PowerShell guide](powershell.md) for `$LASTEXITCODE`, Unicode pipelines,
and NUL-delimited output.

## Developer compatibility switches

The port retains managed implementations of `--run-tests`,
`--debug-dump-disasm`, `--debug-trace`, and `--debug-trace=all` for compatibility
and release verification. They operate on managed compiler/VM state and do not
claim native address, allocator, bytecode-text, or refcount-trace identity.
