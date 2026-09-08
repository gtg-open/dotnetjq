using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using DotNetJq;
using DotNetJq.Port;

namespace DotNetJq.CompatibilityRunner;

internal static class Program
{
    private static readonly JsonSerializerOptions DisplayJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static int Main(string[] args)
    {
        if (WorkerProcess.IsInvocation(args))
        {
            return WorkerProcess.Run(args);
        }

        if (!CommandLine.TryParse(args, out var options, out var error))
        {
            Console.Error.WriteLine($"error: {error}");
            CommandLine.WriteUsage(Console.Error);
            return 2;
        }

        if (options.ShowHelp)
        {
            CommandLine.WriteUsage(Console.Out);
            return 0;
        }

        try
        {
            return Run(options);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Console.Error.WriteLine($"error: {OneLine(exception.Message)}");
            return 2;
        }
    }

    private static int Run(CommandLineOptions options)
    {
        var fixturePath = Path.GetFullPath(options.FixturePath);
        using var reader = File.OpenText(fixturePath);
        var cases = FixtureFile.Parse(reader, fixturePath);
        var available = Math.Max(0, cases.Count - options.Skip);
        var selectedCount = options.Take is { } take ? Math.Min(take, available) : available;

        Console.WriteLine("DotNetJq compatibility report");
        Console.WriteLine($"fixture={fixturePath}");
        Console.WriteLine(
            $"selection skip={options.Skip.ToString(CultureInfo.InvariantCulture)} " +
            $"take={(options.Take?.ToString(CultureInfo.InvariantCulture) ?? "all")}");

        if (options.Skip > cases.Count)
        {
            Console.Error.WriteLine(
                $"error: --skip {options.Skip.ToString(CultureInfo.InvariantCulture)} " +
                $"is past the end of the {cases.Count.ToString(CultureInfo.InvariantCulture)}-case fixture");
            WriteTotals(cases.Count, selected: 0, passed: 0, failed: 0);
            return 2;
        }

        var passed = 0;
        var failed = 0;
        WorkerProcess.RunIsolated(
            fixturePath,
            cases,
            options.Skip,
            selectedCount,
            (testCase, failure) =>
            {
                if (failure is null)
                {
                    passed++;
                    return;
                }

                failed++;
                Console.WriteLine(
                    $"FAIL case={testCase.Number.ToString(CultureInfo.InvariantCulture)} " +
                    $"line={testCase.ProgramLine.ToString(CultureInfo.InvariantCulture)} " +
                    $"phase={failure.Phase}");
                Console.WriteLine($"  program={Quote(testCase.Program)}");
                foreach (var detail in failure.Details)
                {
                    Console.WriteLine($"  detail={Quote(detail)}");
                }
            });

        WriteTotals(cases.Count, selectedCount, passed, failed);
        return failed == 0 ? 0 : 1;
    }

    internal static CaseFailure? Execute(
        FixtureCase testCase,
        JqModuleResolver resolver,
        JqExecutionOptions? executionOptions = null)
    {
        if (testCase.ExpectedCompileFailure is { } expectedFailure)
        {
            try
            {
                _ = JqProgram.Compile(testCase.Program, resolver);
                return CaseFailure.Single(
                    "compile-outcome",
                    "expected compilation to fail, but it succeeded");
            }
            catch (JqCompileException exception)
            {
                if (!expectedFailure.CheckMessage)
                {
                    return null;
                }

                var expectedMessage = NormalizeLineEndings(expectedFailure.Message);
                var actualMessage = LastJqTestDiagnostic(
                    NormalizeLineEndings(exception.Message));
                return expectedMessage.Equals(actualMessage, StringComparison.Ordinal)
                    ? null
                    : new CaseFailure(
                        "compile-message",
                        [
                            $"expected message: {expectedMessage}",
                            $"actual message: {actualMessage}",
                        ]);
            }
            catch (Exception exception)
            {
                return UnexpectedException(
                    "compile-outcome",
                    "expected JqCompileException",
                    exception);
            }
        }

        JqProgram program;
        try
        {
            program = JqProgram.Compile(testCase.Program, resolver);
        }
        catch (Exception exception)
        {
            return UnexpectedException("compile-outcome", "expected compilation to succeed", exception);
        }

        try
        {
            return ExecutePullBased(program, testCase, executionOptions);
        }
        catch (Exception exception)
        {
            var expectation = testCase.ExpectedRuntimeFailure
                ? "expected JqRuntimeException"
                : "expected execution to succeed";
            return UnexpectedException("runtime-outcome", expectation, exception);
        }
    }

    private static CaseFailure? ExecutePullBased(
        JqProgram program,
        FixtureCase testCase,
        JqExecutionOptions? executionOptions)
    {
        using var execution = program.StartUpstreamTestExecution(testCase.Input!, executionOptions);
        if (testCase.ExpectedRuntimeFailure)
        {
            var first = execution.ReadNext();
            if (first.TerminalError is not null)
            {
                return null;
            }

            if (first.Value is null)
            {
                return CaseFailure.Single(
                    "runtime-outcome",
                    "expected execution to fail, but it ended without a runtime error");
            }

            return CaseFailure.Single(
                "runtime-outcome",
                "expected execution to fail before producing an output value");
        }

        var expected = new List<JsonElement>(testCase.ExpectedOutputs.Count);
        for (var index = 0; index < testCase.ExpectedOutputs.Count; index++)
        {
            try
            {
                using var document = JsonDocument.Parse(testCase.ExpectedOutputs[index]);
                expected.Add(document.RootElement.Clone());
            }
            catch (JsonException exception)
            {
                return CaseFailure.Single(
                    "fixture",
                    $"expected output {index.ToString(CultureInfo.InvariantCulture)} is invalid JSON: {OneLine(exception.Message)}");
            }
        }

        var details = new List<string>();
        for (var index = 0; index < expected.Count; index++)
        {
            var actual = execution.ReadNext();
            if (actual.Value is not { } actualValue)
            {
                details.Add(
                    $"output count: expected {expected.Count.ToString(CultureInfo.InvariantCulture)}, " +
                    $"actual {index.ToString(CultureInfo.InvariantCulture)}");
                if (actual.TerminalError is { } terminalError)
                {
                    details.Add($"terminal runtime error: {OneLine(terminalError.Message)}");
                }

                return new CaseFailure("output", details);
            }

            if (JsonElement.DeepEquals(expected[index], actualValue))
            {
                continue;
            }

            details.Add(
                $"output[{index.ToString(CultureInfo.InvariantCulture)}]: " +
                $"expected {Abbreviate(expected[index].GetRawText())}, " +
                $"actual {Abbreviate(actualValue.GetRawText())}");
        }

        if (details.Count != 0)
        {
            return new CaseFailure("output", details);
        }

        // src/jq_test.c calls jq_next once after all expected values. Any invalid result is
        // accepted here, including an invalid carrying a runtime error; a valid value is a
        // superfluous result. Do not generalize this rule to public materialized execution.
        var terminal = execution.ReadNext();
        return terminal.Value is { } extra
            ? CaseFailure.Single(
                "output",
                $"superfluous output[{expected.Count.ToString(CultureInfo.InvariantCulture)}]: " +
                Abbreviate(extra.GetRawText()))
            : null;
    }

    private static CaseFailure UnexpectedException(
        string phase,
        string expectation,
        Exception exception) =>
        new(
            phase,
            [
                expectation,
                $"actual {exception.GetType().FullName}: {OneLine(exception.Message)}",
            ]);

    private static void WriteTotals(int total, int selected, int passed, int failed) =>
        Console.WriteLine(
            $"TOTAL fixture_cases={total.ToString(CultureInfo.InvariantCulture)} " +
            $"selected={selected.ToString(CultureInfo.InvariantCulture)} " +
            $"passed={passed.ToString(CultureInfo.InvariantCulture)} " +
            $"failed={failed.ToString(CultureInfo.InvariantCulture)}");

    private static string NormalizeLineEndings(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    // jq_test.c's test_err_cb overwrites its buffer for every callback, so a
    // %%FAIL block compares the last jq error rather than the CLI's combined
    // diagnostic stream. Keep the library exception complete and project only
    // the official fixture runner's view here.
    private static string LastJqTestDiagnostic(string message)
    {
        const string marker = "\njq: error:";
        var last = message.LastIndexOf(marker, StringComparison.Ordinal);
        return last < 0 ? message : message[(last + 1)..];
    }

    private static string OneLine(string value) => NormalizeLineEndings(value).Replace('\n', ' ');

    private static string Quote(string value) => JsonSerializer.Serialize(value, DisplayJsonOptions);

    private static string Abbreviate(string value)
    {
        const int limit = 1_000;
        return value.Length <= limit
            ? value
            : value[..limit] + $"...[truncated {value.Length - limit} chars]";
    }
}

internal sealed record CaseFailure(string Phase, IReadOnlyList<string> Details)
{
    internal static CaseFailure Single(string phase, string detail) => new(phase, [detail]);
}

internal sealed record ExpectedCompileFailure(bool CheckMessage, IReadOnlyList<string> MessageLines)
{
    internal string Message => string.Join('\n', MessageLines);
}

internal sealed record FixtureCase(
    int Number,
    int ProgramLine,
    string Program,
    string? Input,
    IReadOnlyList<string> ExpectedOutputs,
    ExpectedCompileFailure? ExpectedCompileFailure,
    bool ExpectedRuntimeFailure);

internal static class FixtureFile
{
    internal static IReadOnlyList<FixtureCase> Parse(TextReader reader, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);

        var lines = ReadLines(reader);
        var cases = new List<FixtureCase>();
        var index = 0;
        var mustFail = false;
        var checkFailureMessage = false;
        var failureMarkerLine = 0;

        while (index < lines.Count)
        {
            var (lineNumber, line) = lines[index++];
            if (IsSeparator(line))
            {
                continue;
            }

            if (TryReadFailureMarker(line, out var markerChecksMessage))
            {
                mustFail = true;
                checkFailureMessage = markerChecksMessage;
                failureMarkerLine = lineNumber;
                continue;
            }

            var program = line;
            if (mustFail)
            {
                var messages = ReadUntilSeparator(lines, ref index);
                cases.Add(new FixtureCase(
                    cases.Count + 1,
                    lineNumber,
                    program,
                    Input: null,
                    ExpectedOutputs: Array.Empty<string>(),
                    new ExpectedCompileFailure(checkFailureMessage, messages),
                    ExpectedRuntimeFailure: false));
                mustFail = false;
                checkFailureMessage = false;
                continue;
            }

            if (index == lines.Count)
            {
                throw Malformed(sourceName, lineNumber, "test program has no input line");
            }

            // jq_test.c consumes this line unconditionally, even when it resembles a comment.
            var input = lines[index++].Text;
            var outputs = new List<string>();
            var expectedRuntimeFailure = false;
            while (index < lines.Count)
            {
                var outputLine = lines[index++].Text;
                if (!IsSeparator(outputLine))
                {
                    outputs.Add(outputLine);
                    continue;
                }

                // jq.test documents four expected execution errors in the output position.
                // Distinguishing those from a successful empty stream preserves the outcome.
                expectedRuntimeFailure = outputs.Count == 0 && IsRuntimeFailureMarker(outputLine);
                break;
            }

            cases.Add(new FixtureCase(
                cases.Count + 1,
                lineNumber,
                program,
                input,
                outputs,
                ExpectedCompileFailure: null,
                expectedRuntimeFailure));
        }

        if (mustFail)
        {
            throw Malformed(sourceName, failureMarkerLine, "failure marker has no following program");
        }

        return cases;
    }

    private static List<(int LineNumber, string Text)> ReadLines(TextReader reader)
    {
        var lines = new List<(int LineNumber, string Text)>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            lines.Add((lines.Count + 1, line));
        }

        return lines;
    }

    private static List<string> ReadUntilSeparator(
        List<(int LineNumber, string Text)> lines,
        ref int index)
    {
        var values = new List<string>();
        while (index < lines.Count)
        {
            var line = lines[index++].Text;
            if (IsSeparator(line))
            {
                break;
            }

            values.Add(line);
        }

        return values;
    }

    private static bool IsSeparator(string line)
    {
        var index = 0;
        while (index < line.Length && line[index] is ' ' or '\t')
        {
            index++;
        }

        return index == line.Length || line[index] is '#' or '\0';
    }

    private static bool IsRuntimeFailureMarker(string line)
    {
        var index = 0;
        while (index < line.Length && line[index] is ' ' or '\t')
        {
            index++;
        }

        return line.AsSpan(index).StartsWith("# Runtime error:", StringComparison.Ordinal);
    }

    private static bool TryReadFailureMarker(string line, out bool checkMessage)
    {
        checkMessage = line.Equals("%%FAIL", StringComparison.Ordinal);
        return checkMessage || line.Equals("%%FAIL IGNORE MSG", StringComparison.Ordinal);
    }

    private static InvalidDataException Malformed(
        string sourceName,
        int lineNumber,
        string message) =>
        new(
            $"Malformed upstream test file '{sourceName}' at line " +
            $"{lineNumber.ToString(CultureInfo.InvariantCulture)}: {message}.");
}

internal sealed record CommandLineOptions(
    string FixturePath,
    int Skip,
    int? Take,
    bool ShowHelp);

internal static class CommandLine
{
    internal static bool TryParse(
        IReadOnlyList<string> args,
        out CommandLineOptions options,
        out string? error)
    {
        var fixture = DefaultFixturePath;
        var fixtureWasSet = false;
        var skip = 0;
        int? take = null;
        var showHelp = false;

        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            if (argument is "--help" or "-h")
            {
                showHelp = true;
                continue;
            }

            if (TrySplit(argument, "--fixture", out var inlineFixture))
            {
                if (!TryGetValue(args, ref index, inlineFixture, "--fixture", out fixture, out error))
                {
                    options = default!;
                    return false;
                }

                fixtureWasSet = true;
                continue;
            }

            if (TrySplit(argument, "--skip", out var inlineSkip))
            {
                if (!TryGetValue(args, ref index, inlineSkip, "--skip", out var value, out error) ||
                    !TryParseNonnegative(value, "--skip", out skip, out error))
                {
                    options = default!;
                    return false;
                }

                continue;
            }

            if (TrySplit(argument, "--take", out var inlineTake))
            {
                if (!TryGetValue(args, ref index, inlineTake, "--take", out var value, out error) ||
                    !TryParseNonnegative(value, "--take", out var parsedTake, out error))
                {
                    options = default!;
                    return false;
                }

                take = parsedTake;
                continue;
            }

            if (argument.Length > 0 && argument[0] == '-')
            {
                options = default!;
                error = $"unknown option '{argument}'";
                return false;
            }

            if (fixtureWasSet)
            {
                options = default!;
                error = "fixture was specified more than once";
                return false;
            }

            fixture = argument;
            fixtureWasSet = true;
        }

        options = new CommandLineOptions(fixture, skip, take, showHelp);
        error = null;
        return true;
    }

    internal static void WriteUsage(TextWriter writer)
    {
        writer.WriteLine("Usage: DotNetJq.CompatibilityRunner [fixture] [--fixture PATH] [--skip N] [--take N]");
        writer.WriteLine($"Default fixture: {DefaultFixturePath}");
        writer.WriteLine("Exit codes: 0 all selected cases passed; 1 compatibility failures; 2 usage/fixture error.");
    }

    private static string DefaultFixturePath
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("DOTNETJQ_UPSTREAM");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return Path.Combine(configured, "tests", "jq.test");
            }

            foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            {
                for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
                {
                    if (File.Exists(Path.Combine(directory.FullName, "DotNetJq.sln")))
                    {
                        return Path.Combine(directory.FullName, "upstream", "jq", "tests", "jq.test");
                    }
                }
            }

            return Path.GetFullPath(Path.Combine("upstream", "jq", "tests", "jq.test"));
        }
    }

    private static bool TrySplit(string argument, string name, out string? inlineValue)
    {
        if (argument.Equals(name, StringComparison.Ordinal))
        {
            inlineValue = null;
            return true;
        }

        var prefix = name + "=";
        if (argument.StartsWith(prefix, StringComparison.Ordinal))
        {
            inlineValue = argument[prefix.Length..];
            return true;
        }

        inlineValue = null;
        return false;
    }

    private static bool TryGetValue(
        IReadOnlyList<string> args,
        ref int index,
        string? inlineValue,
        string option,
        out string value,
        out string? error)
    {
        if (inlineValue is not null)
        {
            if (inlineValue.Length == 0)
            {
                value = string.Empty;
                error = $"{option} requires a value";
                return false;
            }

            value = inlineValue;
            error = null;
            return true;
        }

        if (++index >= args.Count)
        {
            value = string.Empty;
            error = $"{option} requires a value";
            return false;
        }

        value = args[index];
        error = null;
        return true;
    }

    private static bool TryParseNonnegative(
        string value,
        string option,
        out int result,
        out string? error)
    {
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result))
        {
            error = null;
            return true;
        }

        error = $"{option} requires a non-negative 32-bit integer";
        return false;
    }
}
