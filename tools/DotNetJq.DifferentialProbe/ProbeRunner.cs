using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using DotNetJq;

namespace DotNetJq.DifferentialProbe;

internal enum ProbeOutcomeKind
{
    Success,
    CompileError,
    RuntimeError,
    Timeout,
    HarnessError,
}

internal sealed record ProbeExecution(
    ProbeOutcomeKind Kind,
    IReadOnlyList<string> Outputs,
    string Diagnostic);

internal sealed record ProbeMismatch(
    ProbeCase Case,
    ProbeExecution Official,
    ProbeExecution Managed,
    IReadOnlyList<string> Reasons);

internal static class ProbeRunner
{
    internal const string ExpectedOracleVersion = "jq-1.8.2";

    private static readonly JqExecutionOptions ManagedOptions = new()
    {
        Timeout = TimeSpan.FromSeconds(1),
        MaxInputBytes = 1_048_576,
        MaxOutputBytes = 1_048_576,
        MaxOutputValues = 256,
        MaxRecursionDepth = 256,
        MaxExecutionTransitions = 100_000,
        RegexTimeout = TimeSpan.FromMilliseconds(500),
    };

    internal static async Task<ProbeMismatch?> CompareAsync(
        ProbeCase testCase,
        string oraclePath,
        TimeSpan oracleTimeout,
        CancellationToken cancellationToken)
    {
        var officialTask = RunOfficialAsync(
            testCase,
            oraclePath,
            oracleTimeout,
            cancellationToken);
        var managed = RunManaged(testCase);
        var official = await officialTask.ConfigureAwait(false);
        var reasons = Compare(official, managed);
        return reasons.Count == 0
            ? null
            : new ProbeMismatch(testCase, official, managed, reasons);
    }

    internal static async Task<string> ReadOracleVersionAsync(
        string oraclePath,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(oraclePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--version");
        using var process = Process.Start(startInfo) ??
            throw new InvalidOperationException("official jq process did not start");
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("official jq --version timed out");
        }

        var version = (await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false)).Trim();
        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidDataException("official jq --version failed: " + OneLine(error));
        }

        return version;
    }

    internal static async Task<IReadOnlySet<string>> ReadOracleBuiltinsAsync(
        string oraclePath,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(oraclePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--null-input");
        startInfo.ArgumentList.Add("--compact-output");
        startInfo.ArgumentList.Add("builtins");
        using var process = Process.Start(startInfo) ??
            throw new InvalidOperationException("official jq process did not start");
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("official jq builtins inventory timed out");
        }

        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidDataException("official jq builtins inventory failed: " + OneLine(error));
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(output)?.ToHashSet(StringComparer.Ordinal) ??
                   throw new InvalidDataException("official jq returned a null builtins inventory");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("official jq returned an invalid builtins inventory", exception);
        }
    }

    private static ProbeExecution RunManaged(ProbeCase testCase)
    {
        try
        {
            var program = JqProgram.Compile(testCase.Filter);
            var result = program.ExecuteDetailed(testCase.Input, ManagedOptions);
            var output = result.Outputs
                .Select(value => value.GetRawText())
                .ToArray();
            return result.Outcome.Kind switch
            {
                JqExecutionOutcomeKind.Completed =>
                    new ProbeExecution(ProbeOutcomeKind.Success, output, string.Empty),
                JqExecutionOutcomeKind.RuntimeError =>
                    ManagedRuntimeFailure(output, result.Outcome.RuntimeError),
                JqExecutionOutcomeKind.Halted =>
                    ManagedHalt(output, result.Outcome),
                _ => throw new InvalidOperationException("unknown managed execution outcome"),
            };
        }
        catch (JqCompileException exception)
        {
            return Failure(ProbeOutcomeKind.CompileError, exception);
        }
        catch (JqRuntimeException exception)
        {
            var kind = exception.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
                ? ProbeOutcomeKind.Timeout
                : ProbeOutcomeKind.RuntimeError;
            return Failure(kind, exception);
        }
        catch (OperationCanceledException exception)
        {
            return Failure(ProbeOutcomeKind.Timeout, exception);
        }
        catch (Exception exception)
        {
            return Failure(ProbeOutcomeKind.HarnessError, exception);
        }
    }

    private static ProbeExecution ManagedRuntimeFailure(
        IReadOnlyList<string> outputs,
        JqRuntimeException? exception)
    {
        var diagnostic = exception is null
            ? "jq execution failed"
            : NormalizeDiagnosticText(exception.Message);
        var kind = diagnostic.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            ? ProbeOutcomeKind.Timeout
            : ProbeOutcomeKind.RuntimeError;
        return new ProbeExecution(kind, outputs, diagnostic);
    }

    private static ProbeExecution ManagedHalt(
        IReadOnlyList<string> outputs,
        JqExecutionOutcome outcome)
    {
        // jq exits successfully for bare halt and halt_error(0), and reports a
        // non-success process outcome for every other requested numeric code.
        var kind = outcome.RequestedExitCode is not { } code ||
                   (code.TryGetInt64(out var integerCode) && integerCode == 0)
            ? ProbeOutcomeKind.Success
            : ProbeOutcomeKind.RuntimeError;
        var diagnostic = outcome.HaltMessage is not { } haltMessage
            ? string.Empty
            : haltMessage.ValueKind == JsonValueKind.String
                ? haltMessage.GetString() ?? string.Empty
                : haltMessage.GetRawText();
        return new ProbeExecution(kind, outputs, diagnostic);
    }

    private static async Task<ProbeExecution> RunOfficialAsync(
        ProbeCase testCase,
        string oraclePath,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(oraclePath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add("--compact-output");
        startInfo.ArgumentList.Add("--monochrome-output");
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add(testCase.Filter);
        startInfo.Environment["LC_ALL"] = "C";
        startInfo.Environment["LANG"] = "C";
        startInfo.Environment["NO_COLOR"] = "1";

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return new ProbeExecution(
                    ProbeOutcomeKind.HarnessError,
                    [],
                    "official jq process did not start");
            }

            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await process.StandardInput.WriteAsync(testCase.Input.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                await process.StandardInput.WriteLineAsync().ConfigureAwait(false);
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // A compile failure can close stdin before this small input is written.
            }

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                return new ProbeExecution(
                    ProbeOutcomeKind.Timeout,
                    SplitLines(await stdout.ConfigureAwait(false)),
                    "official jq exceeded " + timeout.TotalMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms");
            }

            var output = SplitLines(await stdout.ConfigureAwait(false));
            var diagnostic = NormalizeDiagnosticText(await stderr.ConfigureAwait(false));
            var kind = process.ExitCode switch
            {
                0 => ProbeOutcomeKind.Success,
                3 => ProbeOutcomeKind.CompileError,
                _ => ProbeOutcomeKind.RuntimeError,
            };
            return new ProbeExecution(kind, output, diagnostic);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
            }

            return Failure(ProbeOutcomeKind.HarnessError, exception);
        }
    }

    internal static List<string> Compare(ProbeExecution official, ProbeExecution managed)
    {
        var reasons = new List<string>();
        if (official.Kind is ProbeOutcomeKind.Timeout or ProbeOutcomeKind.HarnessError)
        {
            reasons.Add($"official execution did not produce a semantic outcome: {official.Kind}");
        }

        if (managed.Kind is ProbeOutcomeKind.Timeout or ProbeOutcomeKind.HarnessError)
        {
            reasons.Add($"managed execution did not produce a semantic outcome: {managed.Kind}");
        }

        if (official.Kind != managed.Kind)
        {
            reasons.Add($"outcome: official={official.Kind}, managed={managed.Kind}");
        }

        if (official.Outputs.Count != managed.Outputs.Count)
        {
            reasons.Add(
                $"output count: official={official.Outputs.Count.ToString(CultureInfo.InvariantCulture)}, " +
                $"managed={managed.Outputs.Count.ToString(CultureInfo.InvariantCulture)}");
        }

        var count = Math.Min(official.Outputs.Count, managed.Outputs.Count);
        for (var index = 0; index < count; index++)
        {
            if (JsonEquals(official.Outputs[index], managed.Outputs[index]))
            {
                continue;
            }

            reasons.Add($"output[{index.ToString(CultureInfo.InvariantCulture)}] differs");
        }

        if (official.Kind is ProbeOutcomeKind.CompileError or ProbeOutcomeKind.RuntimeError &&
            managed.Kind is ProbeOutcomeKind.CompileError or ProbeOutcomeKind.RuntimeError)
        {
            var officialRawDiagnostic = NormalizeDiagnosticText(official.Diagnostic);
            var managedRawDiagnostic = NormalizeDiagnosticText(managed.Diagnostic);
            var officialDiagnostic = NormalizeComparableDiagnostic(
                official.Kind,
                officialRawDiagnostic,
                officialProcess: true);
            var managedDiagnostic = NormalizeComparableDiagnostic(
                managed.Kind,
                managedRawDiagnostic,
                officialProcess: false);
            if (!officialRawDiagnostic.Equals(managedRawDiagnostic, StringComparison.Ordinal) &&
                !officialDiagnostic.Equals(managedDiagnostic, StringComparison.Ordinal))
            {
                reasons.Add("diagnostic payload differs");
            }
        }

        return reasons;
    }

    /// <summary>
    /// Produces the stable jq-visible diagnostic contract used by the probe.
    /// The official process adds two pieces that the managed library cannot:
    /// a runtime executable/source-location envelope and a terminal compile
    /// error-count line with an optional blank separator.  Only those wrappers
    /// are removed.  The error payload, compile source location, source excerpt,
    /// carets, punctuation, values, and ordering of multiple compile diagnostics
    /// remain byte-for-byte comparable after CRLF and CR line endings have been
    /// normalized to LF.
    /// </summary>
    private static string NormalizeComparableDiagnostic(
        ProbeOutcomeKind kind,
        string diagnostic,
        bool officialProcess)
    {
        var normalized = diagnostic;
        if (kind == ProbeOutcomeKind.CompileError)
        {
            if (officialProcess)
            {
                normalized = RemoveOfficialCompileSummary(normalized);
            }

            return RemoveExecutableNameFromCompileError(normalized);
        }

        return kind == ProbeOutcomeKind.RuntimeError && officialProcess
            ? RemoveRuntimeProcessEnvelope(normalized)
            : normalized;
    }

    private static string RemoveOfficialCompileSummary(string diagnostic)
    {
        var withoutTerminator = diagnostic.EndsWith('\n')
            ? diagnostic[..^1]
            : diagnostic;
        var summarySeparator = withoutTerminator.LastIndexOf('\n');
        var summaryStart = summarySeparator + 1;
        var summary = withoutTerminator[summaryStart..];
        if (!IsOfficialCompileSummary(summary))
        {
            return diagnostic;
        }

        return summarySeparator < 0
            ? string.Empty
            : RemoveOneTrailingLineFeed(withoutTerminator[..summarySeparator]);
    }

    private static bool IsOfficialCompileSummary(string summary)
    {
        const string Prefix = "jq: ";
        if (!summary.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var countStart = Prefix.Length;
        var countEnd = countStart;
        while (countEnd < summary.Length && char.IsAsciiDigit(summary[countEnd]))
        {
            countEnd++;
        }

        if (countEnd == countStart)
        {
            return false;
        }

        var suffix = summary[countEnd..];
        return suffix is " compile error" or " compile errors";
    }

    private static string RemoveExecutableNameFromCompileError(string diagnostic)
    {
        const string Marker = ": error: ";
        var marker = diagnostic.IndexOf(Marker, StringComparison.Ordinal);
        return marker <= 0
            ? diagnostic
            : diagnostic[(marker + 2)..];
    }

    private static string RemoveRuntimeProcessEnvelope(string diagnostic)
    {
        const string Marker = ": error (at ";
        var marker = diagnostic.IndexOf(Marker, StringComparison.Ordinal);
        if (marker <= 0)
        {
            return diagnostic;
        }

        var payload = diagnostic.IndexOf("): ", marker + Marker.Length, StringComparison.Ordinal);
        return payload < 0
            ? diagnostic
            : RemoveOneTrailingLineFeed(diagnostic[(payload + 3)..]);
    }

    private static string RemoveOneTrailingLineFeed(string value) =>
        value.EndsWith('\n') ? value[..^1] : value;

    private static bool JsonEquals(string left, string right)
    {
        try
        {
            using var leftDocument = JsonDocument.Parse(left);
            using var rightDocument = JsonDocument.Parse(right);
            return JsonElement.DeepEquals(leftDocument.RootElement, rightDocument.RootElement);
        }
        catch (JsonException)
        {
            return left.Equals(right, StringComparison.Ordinal);
        }
    }

    private static ProbeExecution Failure(ProbeOutcomeKind kind, Exception exception) =>
        new(
            kind,
            [],
            exception is JqException
                ? NormalizeDiagnosticText(exception.Message)
                : $"{exception.GetType().FullName}: {OneLine(exception.Message)}");

    private static string[] SplitLines(string value)
    {
        var normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    private static string OneLine(string value) =>
        NormalizeDiagnosticText(value)
            .Replace('\n', ' ')
            .Trim();

    private static string NormalizeDiagnosticText(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
}
