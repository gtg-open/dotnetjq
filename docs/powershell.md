# PowerShell

`dotnetjq` is a normal native command on Windows. Use the call operator when the
path is stored in a variable, and single-quote jq filters so PowerShell does not
expand jq `$variables`.

```powershell
$cli = '.\dotnetjq.exe'

'{"message":"olá"}' | & $cli -r '.message'
& $cli -n -c --arg message 'Grüße 🌍' '{message:$message}'

& $cli -n -e 'false'
$LASTEXITCODE # 1
```

Use `--` when a positional value begins with a dash:

```powershell
$values = @('Grüße 🌍', 'two words', '-leading-dash')
& $cli -n -c '$ARGS.positional' --args -- @values
```

## PowerShell 7

Current PowerShell 7 uses UTF-8 for ordinary native text pipelines. Direct
Windows command-line arguments are Unicode and do not depend on the pipeline
encoding. Check command status with `$LASTEXITCODE`; `$?` alone loses jq's exact
numeric status.

## Windows PowerShell 5.1

Windows PowerShell 5.1 defaults to legacy encodings for native text. Configure
both the encoding used for strings piped to stdin and the encoding used to
decode native stdout:

```powershell
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$OutputEncoding = $utf8NoBom
[Console]::OutputEncoding = $utf8NoBom

'{"message":"olá"}' | & .\dotnetjq.exe -r '.message'
```

`$OutputEncoding` affects PowerShell strings sent to stdin; it does not affect
direct program, path, or `--arg` arguments. PowerShell owns line framing for
objects in a text pipeline, while DotNetJq itself writes BOM-less UTF-8 and LF.

Windows PowerShell 5.1's legacy native-argument marshalling consumes embedded
double quotes unless they are escaped for the target program's command-line
parser. When a jq filter passed directly on the command line contains a string
literal, prefix each required double quote with a backslash:

```powershell
& .\dotnetjq.exe -n -r 'include \"helper\"; helper'
```

PowerShell 7 invoking the standalone `.exe` does not need that workaround. A filter file passed with `-f` also
avoids shell quoting differences.

## .NET tool launcher on Windows

`dotnet tool install` exposes a native tool through an SDK-generated
`dotnetjq.cmd` batch launcher. It is not the standalone `dotnetjq.exe` from the
release ZIP. PowerShell 7's default Windows mode still uses legacy argument
passing for `.cmd` files; see Microsoft's
[`PSNativeCommandArgumentPassing` documentation](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_preference_variables#psnativecommandargumentpassing).
Use `-f` for filters containing quotes. For programmatic invocation with
arbitrary or untrusted argument values, prefer the standalone `.exe` and
`ProcessStartInfo.ArgumentList`, avoiding the additional batch interpreter.
The release tests exercise the installed `.cmd` launcher in both PowerShell
versions, including Unicode pipelines, arguments, paths, exit status, and raw
byte streams; they do not substitute the inner executable for those tests.

## NUL and arbitrary bytes

Normal PowerShell string capture is not a byte-preserving API. For
`--raw-output0` or arbitrary bytes, use a byte-preserving native pipeline in a
recent PowerShell 7 release, redirect to a file, or read the process streams as
bytes through `System.Diagnostics.Process`.

The automated Windows contract tests exercise PowerShell 7 and Windows
PowerShell 5.1 on both x64 and ARM64 release packages.
