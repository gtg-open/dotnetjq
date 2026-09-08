[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $CliPath,

    [Parameter(Mandatory = $true)]
    [ValidateSet('PowerShell7', 'WindowsPowerShell51')]
    [string] $ExpectedShell,

    [Parameter(Mandatory = $false)]
    [string] $ExpectedVersion = '1.0.0'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

function Fail([string] $Message) {
    throw "dotnetjq PowerShell verification failed: $Message"
}

function Assert-Equal([object] $Expected, [object] $Actual, [string] $Context) {
    if (($Expected -is [string]) -or ($Actual -is [string])) {
        $equal = ($Expected -is [string]) -and
            ($Actual -is [string]) -and
            [string]::Equals($Expected, $Actual, [StringComparison]::Ordinal)
    }
    else {
        $equal = $Expected -eq $Actual
    }

    if (-not $equal) {
        Fail "$Context (expected '$Expected', got '$Actual')"
    }
}

function Assert-Bytes([byte[]] $Expected, [byte[]] $Actual, [string] $Context) {
    if ($Expected.Length -ne $Actual.Length) {
        Fail "$Context length (expected $($Expected.Length), got $($Actual.Length))"
    }

    for ($index = 0; $index -lt $Expected.Length; $index++) {
        if ($Expected[$index] -ne $Actual[$index]) {
            Fail "$Context at byte $index (expected $($Expected[$index]), got $($Actual[$index]))"
        }
    }
}

function Invoke-NativeCliExitCode([string[]] $ArgumentList) {
    $previousErrorActionPreference = $ErrorActionPreference
    $exitCode = $null
    try {
        # Windows PowerShell 5.1 represents redirected native stderr as a
        # NativeCommandError record. Permit that expected record only while
        # observing the process's real exit code; every other probe remains
        # under the script-wide Stop policy.
        $ErrorActionPreference = 'Continue'
        $global:LASTEXITCODE = $null
        & $CliPath @ArgumentList 1>$null 2>$null
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    if ($null -eq $exitCode) {
        Fail 'native exit-code probe did not start the CLI process'
    }

    return [int] $exitCode
}

function ConvertTo-WindowsCommandLineArgument([string] $Argument) {
    # CommandLineToArgvW-compatible quoting for the Windows PowerShell 5.1
    # ProcessStartInfo.Arguments fallback. Backslashes are doubled only before
    # a quote or the closing quote.
    $builder = New-Object System.Text.StringBuilder
    $null = $builder.Append('"')
    $backslashes = 0
    foreach ($character in $Argument.ToCharArray()) {
        if ($character -eq [char]0x5c) {
            $backslashes++
            continue
        }

        if ($character -eq [char]0x22) {
            for ($index = 0; $index -lt (2 * $backslashes + 1); $index++) {
                $null = $builder.Append([char]0x5c)
            }
            $null = $builder.Append([char]0x22)
        }
        else {
            for ($index = 0; $index -lt $backslashes; $index++) {
                $null = $builder.Append([char]0x5c)
            }
            $null = $builder.Append($character)
        }
        $backslashes = 0
    }

    for ($index = 0; $index -lt (2 * $backslashes); $index++) {
        $null = $builder.Append([char]0x5c)
    }
    $null = $builder.Append('"')
    return $builder.ToString()
}

function Invoke-BinaryCli([string[]] $ArgumentList, [byte[]] $InputBytes) {
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $CliPath
    if ([System.IO.Path]::GetExtension($CliPath) -eq '.cmd') {
        # The .NET 10 NativeAOT tool installer supplies a .cmd shim, whereas
        # standalone archives supply an .exe. Start batch files through cmd
        # and keep every existing byte/argument/exit assertion on that actual
        # launcher; do not bypass it by invoking the package's executable.
        $startInfo.FileName = Join-Path ([Environment]::SystemDirectory) 'cmd.exe'
        $quoted = @(@($CliPath) + $ArgumentList | ForEach-Object { ConvertTo-WindowsCommandLineArgument $_ })
        $startInfo.Arguments = '/d /s /v:off /c "' + ($quoted -join ' ') + '"'
    }
    elseif ($null -ne $startInfo.PSObject.Properties['ArgumentList']) {
        foreach ($argument in $ArgumentList) {
            $null = $startInfo.ArgumentList.Add($argument)
        }
    }
    else {
        # ArgumentList is unavailable on Windows PowerShell 5.1/.NET Framework.
        $quoted = @($ArgumentList | ForEach-Object { ConvertTo-WindowsCommandLineArgument $_ })
        $startInfo.Arguments = $quoted -join ' '
    }
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true

    # Process creates its StandardInput StreamWriter during Start. Select a
    # BOM-less encoding before then so the byte probes observe only InputBytes.
    # .NET Framework has no StandardInputEncoding property and instead reads
    # Console.InputEncoding synchronously while Process.Start is running.
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo
    $stdinEncoding = New-Object System.Text.UTF8Encoding($false)
    $stdinEncodingProperty = $startInfo.PSObject.Properties['StandardInputEncoding']
    if ($null -ne $stdinEncodingProperty) {
        $startInfo.StandardInputEncoding = $stdinEncoding
    }

    $started = $false
    if ($null -ne $stdinEncodingProperty) {
        $started = $process.Start()
    }
    else {
        $originalConsoleInputEncoding = [Console]::InputEncoding
        try {
            [Console]::InputEncoding = $stdinEncoding
            $started = $process.Start()
        }
        finally {
            [Console]::InputEncoding = $originalConsoleInputEncoding
        }
    }
    if (-not $started) {
        Fail 'could not start the CLI process'
    }

    try {
        $stdout = New-Object System.IO.MemoryStream
        $stderr = New-Object System.IO.MemoryStream
        $stdoutTask = $process.StandardOutput.BaseStream.CopyToAsync($stdout)
        $stderrTask = $process.StandardError.BaseStream.CopyToAsync($stderr)

        $null = $process.StandardInput.BaseStream.Write($InputBytes, 0, $InputBytes.Length)
        $null = $process.StandardInput.BaseStream.Flush()
        $null = $process.StandardInput.Close()

        if (-not $process.WaitForExit(20000)) {
            $process.Kill()
            Fail 'CLI byte probe did not finish within 20 seconds'
        }
        $null = $stdoutTask.GetAwaiter().GetResult()
        $null = $stderrTask.GetAwaiter().GetResult()

        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            Stdout = $stdout.ToArray()
            Stderr = $stderr.ToArray()
        }
    }
    finally {
        $null = $process.Dispose()
    }
}

if (-not (Test-Path -LiteralPath $CliPath -PathType Leaf)) {
    Fail "CLI executable does not exist: $CliPath"
}
$CliPath = (Resolve-Path -LiteralPath $CliPath).Path

if ($ExpectedShell -eq 'PowerShell7') {
    if ($PSVersionTable.PSVersion.Major -lt 7) {
        Fail "expected PowerShell 7+, running $($PSVersionTable.PSVersion)"
    }
}
else {
    if ($PSVersionTable.PSEdition -ne 'Desktop' -or
        $PSVersionTable.PSVersion.Major -ne 5 -or
        $PSVersionTable.PSVersion.Minor -lt 1) {
        Fail "expected Windows PowerShell 5.1, running $($PSVersionTable.PSVersion)"
    }
}

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$originalOutputEncoding = $OutputEncoding
$originalConsoleOutputEncoding = [Console]::OutputEncoding

$eAcute = [char]0x00e9
$uUmlaut = [char]0x00fc
$sharpS = [char]0x00df
$globe = [char]::ConvertFromUtf32(0x1f30d)
$unicodeMessage = 'Gr' + $uUmlaut + $sharpS + 'e ' + $globe
$workDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ("dotnetjq pwsh path $eAcute {0}" -f [Guid]::NewGuid().ToString('N'))
[System.IO.Directory]::CreateDirectory($workDirectory) | Out-Null

try {
    # PowerShell 7 promises BOM-less UTF-8 at its default native stdin boundary.
    # Keep stdout ASCII here so this assertion measures input encoding alone.
    if ($ExpectedShell -eq 'PowerShell7') {
        $defaultPipelineInput = '{"message":"' + $unicodeMessage + '"}'
        $defaultPipeline = $defaultPipelineInput | & $CliPath -a -c .
        Assert-Equal 0 $LASTEXITCODE 'PowerShell 7 default UTF-8 pipeline exit code'
        Assert-Equal '{"message":"Gr\u00fc\u00dfe \ud83c\udf0d"}' $defaultPipeline 'PowerShell 7 default UTF-8 pipeline'
    }

    # WinPS 5.1 needs this explicitly; setting it in PS7 makes the rest of the
    # text-pipeline checks independent of the caller's session configuration.
    $OutputEncoding = $utf8NoBom
    [Console]::OutputEncoding = $utf8NoBom

    $versionOutput = & $CliPath --version
    Assert-Equal 0 $LASTEXITCODE 'version exit code'
    Assert-Equal "dotnetjq-$ExpectedVersion (jq-1.8.2 compatible)" $versionOutput 'version output'

    # Direct invocation must preserve Unicode arguments independently of stdin
    # encoding, including the spaces in one --arg value.
    $direct = & $CliPath -n -c --arg shell $ExpectedShell --arg message $unicodeMessage '{shell:$shell,message:$message,ok:true}'
    Assert-Equal 0 $LASTEXITCODE 'direct invocation exit code'
    Assert-Equal ("{{`"shell`":`"{0}`",`"message`":`"{1}`",`"ok`":true}}" -f $ExpectedShell, $unicodeMessage) $direct 'direct Unicode invocation output'

    $argvValues = @($unicodeMessage, 'two words', '-leading-dash')
    $argvOutput = & $CliPath -n -c '$ARGS.positional' --args -- @argvValues
    Assert-Equal 0 $LASTEXITCODE 'PowerShell argv matrix exit code'
    Assert-Equal ("[`"{0}`",`"two words`",`"-leading-dash`"]" -f $unicodeMessage) $argvOutput 'PowerShell argv matrix output'

    # Configure the legacy shell explicitly so a native pipeline is UTF-8 on PS 5.1 too.
    $pipelineInput = '{"message":"' + $unicodeMessage + '"}'
    $pipeline = $pipelineInput | & $CliPath -r .message
    Assert-Equal 0 $LASTEXITCODE 'pipeline exit code'
    Assert-Equal $unicodeMessage $pipeline 'UTF-8 PowerShell pipeline'

    # PowerShell serializes each string pipeline object with the Windows line
    # terminator before encoding it for native stdin. Windows PowerShell 5.1
    # also starts the native UTF-8 pipeline with a BOM, while PowerShell 7 does
    # not. Raw slurp makes that shell-owned framing observable without
    # attributing either difference to dotnetjq.
    $framedPipeline = @('first', $eAcute) | & $CliPath -R -s -c .
    Assert-Equal 0 $LASTEXITCODE 'framed pipeline exit code'
    $pipelinePreamble = ''
    if ($ExpectedShell -eq 'WindowsPowerShell51') {
        $pipelinePreamble = [string][char]0xfeff
    }
    Assert-Equal ('"' + $pipelinePreamble + 'first\r\n' + $eAcute + '\r\n"') $framedPipeline 'PowerShell pipeline framing'

    # Windows transports these paths as Unicode command-line arguments. This is separate
    # from $OutputEncoding/stdin; PowerShell must also preserve each path as one argument.
    $filterPath = Join-Path $workDirectory 'filter with spaces.jq'
    $inputPath = Join-Path $workDirectory ("input ${uUmlaut}.json")
    [System.IO.File]::WriteAllText($filterPath, '.value', $utf8NoBom)
    [System.IO.File]::WriteAllText($inputPath, '{"value":"path-ok"}', $utf8NoBom)
    $pathOutput = & $CliPath -r -f $filterPath $inputPath
    Assert-Equal 0 $LASTEXITCODE 'Unicode path invocation exit code'
    Assert-Equal 'path-ok' $pathOutput 'Unicode path invocation output'

    $moduleDirectory = Join-Path $workDirectory ("modules ${uUmlaut}")
    [System.IO.Directory]::CreateDirectory($moduleDirectory) | Out-Null
    [System.IO.File]::WriteAllText(
        (Join-Path $moduleDirectory 'helper.jq'),
        'def helper: "module-ok";',
        $utf8NoBom)
    # Keep the module grammar in a filter file. Windows PowerShell 5.1's legacy
    # native-argument marshaller otherwise consumes the quotes around "helper";
    # -f isolates this Unicode module-path check from that shell quoting rule.
    $moduleFilterPath = Join-Path $workDirectory 'module filter.jq'
    [System.IO.File]::WriteAllText(
        $moduleFilterPath,
        'include "helper"; helper',
        $utf8NoBom)
    $moduleOutput = & $CliPath -n -r -L $moduleDirectory -f $moduleFilterPath
    Assert-Equal 0 $LASTEXITCODE 'Unicode module path exit code'
    Assert-Equal 'module-ok' $moduleOutput 'Unicode module path output'

    $fileArgumentOutput = & $CliPath -n -c --rawfile raw $inputPath --slurpfile values $inputPath '{raw:$raw,values:$values}'
    Assert-Equal 0 $LASTEXITCODE 'rawfile/slurpfile path exit code'
    Assert-Equal '{"raw":"{\"value\":\"path-ok\"}","values":[{"value":"path-ok"}]}' $fileArgumentOutput 'rawfile/slurpfile path output'

    # Use byte streams, not PowerShell strings, to prove LF and NUL behavior on Windows.
    $utf8Input = $utf8NoBom.GetBytes(('"' + $eAcute + $globe + '"'))
    $rawResult = Invoke-BinaryCli @('-r', '-b', '.') $utf8Input
    Assert-Equal 0 $rawResult.ExitCode 'binary raw-output exit code'
    $expectedRaw = $utf8NoBom.GetBytes(([string]$eAcute + $globe + "`n"))
    Assert-Bytes $expectedRaw $rawResult.Stdout 'binary raw-output bytes'
    Assert-Bytes ([byte[]]@()) $rawResult.Stderr 'binary raw-output stderr bytes'

    # Exercise ProcessStartInfo.ArgumentList and the WinPS 5.1 quoting fallback
    # with whitespace, Unicode, embedded quotes, and a trailing backslash.
    $complexArgument = $unicodeMessage + ' "quoted" trailing\'
    $complexArgumentResult = Invoke-BinaryCli @('-n', '-c', '--arg', 'value', $complexArgument, '$value') ([byte[]]@())
    Assert-Equal 0 $complexArgumentResult.ExitCode 'complex ArgumentList exit code'
    $expectedComplexArgument = $utf8NoBom.GetBytes('"' + $unicodeMessage + ' \"quoted\" trailing\\"' + "`n")
    Assert-Bytes $expectedComplexArgument $complexArgumentResult.Stdout 'complex ArgumentList stdout bytes'
    Assert-Bytes ([byte[]]@()) $complexArgumentResult.Stderr 'complex ArgumentList stderr bytes'

    # dotnetjq's byte-stream policy is already active without -b. Unlike the
    # native Windows CRT default, neither CRLF nor CTRL+Z is translated, and
    # adding -b leaves the exact input/output bytes unchanged.
    $translatedByNativeTextMode = [byte[]]@(0x61, 0x0d, 0x0a, 0x62, 0x1a, 0x63)
    $defaultByteResult = Invoke-BinaryCli @('-R', '-s', '-c', '.') $translatedByNativeTextMode
    $explicitByteResult = Invoke-BinaryCli @('-R', '-s', '-c', '-b', '.') $translatedByNativeTextMode
    $expectedByteResult = $utf8NoBom.GetBytes(('"a\r\nb\u001ac"' + "`n"))
    Assert-Equal 0 $defaultByteResult.ExitCode 'default byte-stream exit code'
    Assert-Equal 0 $explicitByteResult.ExitCode 'explicit binary byte-stream exit code'
    Assert-Bytes $expectedByteResult $defaultByteResult.Stdout 'default byte-stream bytes'
    Assert-Bytes $expectedByteResult $explicitByteResult.Stdout 'explicit binary byte-stream bytes'
    Assert-Bytes ([byte[]]@()) $defaultByteResult.Stderr 'default byte-stream stderr bytes'
    Assert-Bytes ([byte[]]@()) $explicitByteResult.Stderr 'explicit binary byte-stream stderr bytes'

    $rawZeroInput = $utf8NoBom.GetBytes(([string]$eAcute + $globe))
    $rawZeroResult = Invoke-BinaryCli @('-R', '-s', '--raw-output0', '.') $rawZeroInput
    Assert-Equal 0 $rawZeroResult.ExitCode 'raw-output0 exit code'
    $expectedRawZero = New-Object byte[] ($rawZeroInput.Length + 1)
    [Array]::Copy($rawZeroInput, $expectedRawZero, $rawZeroInput.Length)
    $expectedRawZero[$expectedRawZero.Length - 1] = 0
    Assert-Bytes $expectedRawZero $rawZeroResult.Stdout 'raw-output0 bytes'
    Assert-Bytes ([byte[]]@()) $rawZeroResult.Stderr 'raw-output0 stderr bytes'

    # Raw stderr is byte-length-aware, including embedded NUL and literal CRLF.
    $rawStderrInput = [byte[]]@(0x61, 0x00, 0x62, 0x0d, 0x0a, 0x63)
    $rawStderrResult = Invoke-BinaryCli @('-R', '-s', 'stderr|empty') $rawStderrInput
    Assert-Equal 0 $rawStderrResult.ExitCode 'raw stderr exit code'
    Assert-Bytes ([byte[]]@()) $rawStderrResult.Stdout 'raw stderr stdout bytes'
    Assert-Bytes $rawStderrInput $rawStderrResult.Stderr 'raw stderr payload bytes'

    # Sequence framing/recovery and raw-output0 remain byte-exact in NativeAOT.
    $sequenceInput = [byte[]]@(0x1e, 0x31, 0x0a, 0x6e, 0x6f, 0x74, 0x2d, 0x6a, 0x73, 0x6f, 0x6e, 0x0a, 0x1e, 0x32, 0x0a)
    $sequenceResult = Invoke-BinaryCli @('--seq', '-c', '.') $sequenceInput
    Assert-Equal 0 $sequenceResult.ExitCode 'sequence recovery exit code'
    Assert-Bytes ([byte[]]@(0x1e, 0x31, 0x0a, 0x1e, 0x32, 0x0a)) $sequenceResult.Stdout 'sequence recovery stdout bytes'
    $expectedSequenceError = $utf8NoBom.GetBytes(
        'jq: ignoring parse error: Invalid numeric literal at line 3, column 0 (need RS to resync)' + "`n")
    Assert-Bytes $expectedSequenceError $sequenceResult.Stderr 'sequence recovery stderr bytes'

    $sequenceRawInput = $utf8NoBom.GetBytes(([char]0x1e + '"x"' + "`n" + [char]0x1e + '1' + "`n" + [char]0x1e + '"y"' + "`n"))
    $sequenceRawResult = Invoke-BinaryCli @('--seq', '--raw-output0', '.') $sequenceRawInput
    Assert-Equal 0 $sequenceRawResult.ExitCode 'sequence raw-output0 exit code'
    Assert-Bytes ([byte[]]@(0x78, 0x00, 0x1e, 0x31, 0x00, 0x79, 0x00)) $sequenceRawResult.Stdout 'sequence raw-output0 bytes'
    Assert-Bytes ([byte[]]@()) $sequenceRawResult.Stderr 'sequence raw-output0 stderr bytes'

    # Windows environment names are case-insensitive, just like jq.exe getenv().
    $originalJqColors = [Environment]::GetEnvironmentVariable('JQ_COLORS')
    try {
        [Environment]::SetEnvironmentVariable('jq_colors', '4;31')
        $mixedCaseEnvironment = Invoke-BinaryCli @('-Ccn', '.') ([byte[]]@())
        Assert-Equal 0 $mixedCaseEnvironment.ExitCode 'mixed-case JQ_COLORS exit code'
        $expectedColor = $utf8NoBom.GetBytes(([char]0x1b + '[4;31mnull' + [char]0x1b + "[0m`n"))
        Assert-Bytes $expectedColor $mixedCaseEnvironment.Stdout 'mixed-case JQ_COLORS stdout bytes'
        Assert-Bytes ([byte[]]@()) $mixedCaseEnvironment.Stderr 'mixed-case JQ_COLORS stderr bytes'
    }
    finally {
        [Environment]::SetEnvironmentVariable('JQ_COLORS', $originalJqColors)
    }

    # Diagnostics use the same BOM-less UTF-8/LF byte boundary as stdout.
    $usageResult = Invoke-BinaryCli @('--definitely-not-an-option') ([byte[]]@())
    Assert-Equal 2 $usageResult.ExitCode 'unknown-option exit code'
    Assert-Bytes ([byte[]]@()) $usageResult.Stdout 'unknown-option stdout bytes'
    $expectedUsage = $utf8NoBom.GetBytes(
        "jq: Unknown option --definitely-not-an-option`n" +
        "Use dotnetjq --help for help with command-line options,`n" +
        "or see the jq manpage, or online docs at https://jqlang.org`n")
    Assert-Bytes $expectedUsage $usageResult.Stderr 'unknown-option stderr bytes'

    # jq-compatible exit codes must survive both PowerShell native-command implementations.
    Assert-Equal 1 (Invoke-NativeCliExitCode -ArgumentList @('-e', '-n', 'false')) 'false exit status'
    Assert-Equal 4 (Invoke-NativeCliExitCode -ArgumentList @('-e', '-n', 'empty')) 'no-output exit status'
    Assert-Equal 3 (Invoke-NativeCliExitCode -ArgumentList @('-n', 'this is not valid jq syntax!')) 'compile-error exit status'
    # Keep this exit-only filter free of embedded quotes: Windows PowerShell
    # 5.1's legacy native argument serializer removes them. Quoted jq programs
    # are covered above through -f, while error(1) exercises the same status 5.
    Assert-Equal 5 (Invoke-NativeCliExitCode -ArgumentList @('-n', 'error(1)')) 'runtime-error exit status'
}
finally {
    $OutputEncoding = $originalOutputEncoding
    [Console]::OutputEncoding = $originalConsoleOutputEncoding
    if (Test-Path -LiteralPath $workDirectory) {
        Remove-Item -LiteralPath $workDirectory -Recurse -Force
    }
}

Write-Host "dotnetjq verification passed in $ExpectedShell ($($PSVersionTable.PSVersion))"

# The final four probes deliberately leave a nonzero native status in
# $LASTEXITCODE. GitHub's PowerShell runner propagates that automatic variable
# after dot-sourcing this script, so record successful completion explicitly.
$global:LASTEXITCODE = 0
