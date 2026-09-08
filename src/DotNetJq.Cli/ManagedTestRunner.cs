// DOTNETJQ PORT MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream file: src/jq_test.c
// Upstream URL: https://github.com/jqlang/jq/blob/jq-1.8.2/src/jq_test.c
// Strategy: PORT
// Target file: src/DotNetJq.Cli/ManagedTestRunner.cs
//
// UPSTREAM COMPONENT: jq's fixture-format runner and built-in lifecycle regression checks.
// REPLACEMENT: managed streaming fixture reader using the jq-shaped compiler/state interfaces.
// BEHAVIORAL CONTRACT: preserve fixture grammar, selection, diagnostics, counters, exit codes,
// module/startup loading, state reuse, and successful-run output without invoking native jq.

using System.Globalization;
using System.Text;
using DotNetJq.Port;

namespace DotNetJq.Cli;

/// <summary>
/// Managed port of jq-1.8.2 src/jq_test.c. Test programs execute through the managed
/// generated block/inst compiler and direct bytecode VM; the production CLI never
/// invokes an external jq process.
/// </summary>
internal static class ManagedTestRunner
{
    private const int TestLineBufferSize = 4_096;

    internal static int Run(CliOptions options, CliHost host)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!RunSilentValueSelfTests())
        {
            WriteOutput(host, "*** Internal jv self-test failed\n");
            return 1;
        }

        var skip = -1;
        var take = -1;
        var fileCount = 0;
        for (var index = 0; index < options.TestArguments.Count; index++)
        {
            var argument = options.TestArguments[index];
            if (argument == "--skip")
            {
                if (++index >= options.TestArguments.Count)
                {
                    WriteError(host, "--skip requires an argument\n");
                    return 1;
                }

                skip = ParseAtoi(options.TestArguments[index]);
                continue;
            }

            if (argument == "--take")
            {
                if (++index >= options.TestArguments.Count)
                {
                    WriteError(host, "--take requires an argument\n");
                    return 1;
                }

                take = ParseAtoi(options.TestArguments[index]);
                continue;
            }

            Stream source;
            try
            {
                source = File.OpenRead(Path.GetFullPath(argument, host.CurrentDirectory));
            }
            catch (FileNotFoundException)
            {
                WriteError(host, "fopen: No such file or directory\n");
                return 1;
            }
            catch (DirectoryNotFoundException)
            {
                WriteError(host, "fopen: No such file or directory\n");
                return 1;
            }
            catch (UnauthorizedAccessException)
            {
                WriteError(host, "fopen: Permission denied\n");
                return 1;
            }
            catch (ArgumentException exception)
            {
                WriteError(host, "fopen: " + exception.Message + "\n");
                return 1;
            }
            catch (NotSupportedException exception)
            {
                WriteError(host, "fopen: " + exception.Message + "\n");
                return 1;
            }
            catch (IOException exception)
            {
                WriteError(host, "fopen: " + exception.Message + "\n");
                return 1;
            }

            using (source)
            using (var lines = new TestLineReader(source, leaveOpen: false))
            {
                var result = RunFixture(options, host, lines, skip, take);
                if (result != 0)
                {
                    return result;
                }
            }

            fileCount++;
        }

        if (fileCount == 0)
        {
            using var lines = new TestLineReader(host.StandardInput, leaveOpen: true);
            var result = RunFixture(options, host, lines, skip, take);
            if (result != 0)
            {
                return result;
            }
        }

        return RunLifecycleSelfTests(host);
    }

    private static int RunFixture(
        CliOptions options,
        CliHost host,
        TestLineReader testData,
        int skip,
        int take)
    {
        var tests = 0;
        var passed = 0;
        var invalid = 0;
        uint lineNumber = 0;
        var mustFail = false;
        var checkMessage = false;
        var testsToSkip = skip > 0 ? skip : 0;
        var testsToTake = take;
        var resolver = CreateResolver(options, host);
        var verbose = options.DebugDumpDisassembly ||
            (options.JqFlags & libjq.JQ_DEBUG_TRACE) != 0;
        jq_state? state = CreateState(host);

        try
        {
            while (testData.ReadLine() is { } programLine)
            {
                lineNumber++;
                if (ShouldSkipLine(programLine))
                {
                    continue;
                }

                if (IsFailureMarker(programLine))
                {
                    mustFail = true;
                    checkMessage = programLine == "%%FAIL\n";
                    continue;
                }

                var program = TrimLineFeed(programLine);
                if (skip > 0)
                {
                    skip--;
                    SkipTestBody(testData, ref lineNumber);
                    mustFail = false;
                    checkMessage = false;
                    continue;
                }

                if (skip == 0)
                {
                    WriteOutput(host, $"Skipped {testsToSkip.ToString(CultureInfo.InvariantCulture)} tests\n");
                    skip = -1;
                }

                if (take > 0)
                {
                    take--;
                }
                else if (take == 0)
                {
                    WriteOutput(
                        host,
                        $"Hit the number of tests limit ({testsToTake.ToString(CultureInfo.InvariantCulture)}), breaking\n");
                    break;
                }

                var pass = true;
                tests++;
                WriteOutput(
                    host,
                    $"Test #{(tests + testsToSkip).ToString(CultureInfo.InvariantCulture)}: " +
                    $"'{program}' at line number {lineNumber.ToString(CultureInfo.InvariantCulture)}\n");

                var compiled = Compile(state, program, resolver, host);
                if (mustFail)
                {
                    if (compiled)
                    {
                        WriteOutput(
                            host,
                            $"*** Test program compiled successfully, but should fail at line number " +
                            $"{lineNumber.ToString(CultureInfo.InvariantCulture)}: {program}\n");
                        invalid++;
                        SkipTestBody(testData, ref lineNumber);
                        mustFail = false;
                        checkMessage = false;
                        continue;
                    }

                    var error = LastJqTestDiagnostic(state.CompileError?.Message ?? string.Empty);
                    while (testData.ReadLine() is { } expectedErrorLine)
                    {
                        lineNumber++;
                        if (ShouldSkipLine(expectedErrorLine))
                        {
                            break;
                        }

                        if (!checkMessage)
                        {
                            continue;
                        }

                        var expectedPart = TrimLineFeed(expectedErrorLine);
                        if (!error.StartsWith(expectedPart, StringComparison.Ordinal))
                        {
                            WriteOutput(
                                host,
                                $"*** Erroneous program failed with '{FirstLine(error)}', but expected " +
                                $"'{expectedPart}' at line number {lineNumber.ToString(CultureInfo.InvariantCulture)}: {program}\n");
                            invalid++;
                            pass = false;
                            SkipTestBody(testData, ref lineNumber);
                            break;
                        }

                        error = error[expectedPart.Length..];
                        if (error.StartsWith('\n'))
                        {
                            error = error[1..];
                        }
                    }

                    if (pass && checkMessage && error.Length != 0)
                    {
                        WriteOutput(
                            host,
                            $"*** Erroneous program failed with extra message '{FirstLine(error)}' " +
                            $"at line {lineNumber.ToString(CultureInfo.InvariantCulture)}: {program}\n");
                        invalid++;
                        pass = false;
                    }

                    mustFail = false;
                    checkMessage = false;
                    if (pass)
                    {
                        passed++;
                    }

                    continue;
                }

                if (!compiled)
                {
                    WriteCompileError(state, host);
                    WriteOutput(
                        host,
                        $"*** Test program failed to compile at line {lineNumber.ToString(CultureInfo.InvariantCulture)}: {program}\n");
                    invalid++;
                    SkipTestBody(testData, ref lineNumber);
                    continue;
                }

                if (verbose)
                {
                    WriteVerboseDisassembly(state, host);
                }

                var inputLine = testData.ReadLine();
                if (inputLine is null)
                {
                    invalid++;
                    break;
                }

                lineNumber++;
                if (!TryParse(inputLine, out var input))
                {
                    WriteOutput(
                        host,
                        $"*** Input is invalid on line {lineNumber.ToString(CultureInfo.InvariantCulture)}: " +
                        inputLine + "\n");

                    invalid++;
                    SkipTestBody(testData, ref lineNumber);
                    continue;
                }

                libjq.jq_start(
                    state,
                    input,
                    verbose ? libjq.JQ_DEBUG_TRACE : 0);

                while (testData.ReadLine() is { } expectedLine)
                {
                    lineNumber++;
                    if (ShouldSkipLine(expectedLine))
                    {
                        break;
                    }

                    if (!TryParse(expectedLine, out var expected))
                    {
                        WriteOutput(
                            host,
                            $"*** Expected result is invalid on line {lineNumber.ToString(CultureInfo.InvariantCulture)}: " +
                            expectedLine + "\n");

                        invalid++;
                        pass = false;
                        SkipTestBody(testData, ref lineNumber);
                        break;
                    }

                    if (!TryNext(state, out var actual))
                    {
                        // src/jq_test.c releases the parsed expectation and
                        // the invalid jq_next() result on this path.
                        libjq.jv_free(expected);
                        libjq.jv_free(actual);
                        WriteOutput(
                            host,
                            $"*** Insufficient results for test at line number " +
                            $"{lineNumber.ToString(CultureInfo.InvariantCulture)}: {program}\n");
                        pass = false;
                        break;
                    }

                    // src/jq_test.c compares copies so the originals remain
                    // available for diagnostics, then releases both below.
                    if (!libjq.jv_equal(
                            libjq.jv_copy(expected),
                            libjq.jv_copy(actual)))
                    {
                        WriteOutput(
                            host,
                            $"*** Expected {libjq.jv_dump_string_borrowed(expected)}, but got " +
                            $"{libjq.jv_dump_string_borrowed(actual)} for test at line number " +
                            $"{lineNumber.ToString(CultureInfo.InvariantCulture)}: {program}\n");
                        pass = false;
                    }

                    libjq.jv_free(expected);
                    libjq.jv_free(actual);
                }

                if (pass && TryNext(state, out var extra))
                {
                    WriteOutput(
                        host,
                        $"*** Superfluous result: {libjq.jv_dump_string(extra)} for test at line number " +
                        $"{lineNumber.ToString(CultureInfo.InvariantCulture)}, {program}\n");
                    invalid++;
                    pass = false;
                }

                if (pass)
                {
                    passed++;
                }
            }
        }
        finally
        {
            libjq.jq_teardown(ref state);
        }

        var totalSkipped = skip > 0 ? testsToSkip - skip : testsToSkip;
        WriteOutput(
            host,
            $"{passed.ToString(CultureInfo.InvariantCulture)} of {tests.ToString(CultureInfo.InvariantCulture)} " +
            $"tests passed ({invalid.ToString(CultureInfo.InvariantCulture)} malformed, " +
            $"{totalSkipped.ToString(CultureInfo.InvariantCulture)} skipped)\n");

        if (skip > 0)
        {
            WriteOutput(host, "WARN: skipped past the end of file, exiting with status 2\n");
            return 2;
        }

        return passed == tests ? 0 : 1;
    }

    private static jq_state CreateState(CliHost host)
    {
        var state = libjq.jq_init(
            new JqExecutionOptions
            {
                Environment = host.Environment,
                CancellationToken = host.CancellationToken,
            });
        // jq_test.c installs a consuming error callback so expected compile
        // failures can be inspected without writing ambient stderr.
        libjq.jq_set_error_cb(state, libjq.jv_free);
        libjq.jq_set_debug_trace_callback(
            state,
            text => CliOutput.WriteUtf8(host.StandardOutput, text));
        return state;
    }

    private static JqModuleResolver CreateResolver(CliOptions options, CliHost host)
    {
        var paths = options.LibraryPaths
            .Select(path => Path.GetFullPath(path, host.CurrentDirectory))
            .ToArray();
        var home = JqCliApplication.ResolveHome(host.Environment);
        return new JqModuleResolver(
            new CliFileSystem(host.CurrentDirectory),
            paths,
            Path.GetDirectoryName(host.ExecutablePath) ?? host.CurrentDirectory,
            home);
    }

    private static bool Compile(
        jq_state state,
        string program,
        JqModuleResolver resolver,
        CliHost host) =>
        libjq.jq_compile_args(
            state,
            program,
            libjq.jv_object(),
            resolver,
            host.CurrentDirectory,
            loadUserStartupLibrary: true) != 0;

    private static bool TryNext(jq_state state, out jv value)
    {
        try
        {
            value = libjq.jq_next(state);
            return value.IsValid;
        }
        catch (JqRuntimeException exception)
        {
            exception.ReleaseErrorValue();
            value = libjq.jv_invalid();
            return false;
        }
    }

    private static void WriteVerboseDisassembly(jq_state state, CliHost host)
    {
        WriteOutput(host, "Disassembly:\n");
        BytecodeDisassembler.Write(state, host.StandardOutput, indent: 2);
        WriteOutput(host, "\n");
    }

    private static bool TryParse(string text, out jv value)
    {
        value = libjq.jv_parse(text);
        if (value.IsValid)
        {
            return true;
        }

        libjq.jv_free(value);
        value = libjq.jv_invalid();
        return false;
    }

    private static void SkipTestBody(TestLineReader testData, ref uint lineNumber)
    {
        while (testData.ReadLine() is { } line)
        {
            lineNumber++;
            if (ShouldSkipLine(line))
            {
                break;
            }
        }
    }

    private static bool ShouldSkipLine(string line)
    {
        var offset = 0;
        while (offset < line.Length && line[offset] is ' ' or '\t')
        {
            offset++;
        }

        return offset == line.Length || line[offset] is '#' or '\n' or '\0';
    }

    private static bool IsFailureMarker(string line) =>
        line is "%%FAIL\n" or "%%FAIL IGNORE MSG\n";

    private static string TrimLineFeed(string line) =>
        line.EndsWith('\n') ? line[..^1] : line;

    private static string FirstLine(string text)
    {
        var newline = text.IndexOf('\n', StringComparison.Ordinal);
        return newline < 0 ? text : text[..newline];
    }

    private static string LastJqTestDiagnostic(string message)
    {
        const string marker = "\njq: error:";
        var last = message.LastIndexOf(marker, StringComparison.Ordinal);
        return last < 0 ? message : message[(last + 1)..];
    }

    private static void WriteCompileError(jq_state state, CliHost host)
    {
        var message = state.CompileError?.Message ?? "jq: error: program could not be compiled";
        if (!message.StartsWith("jq: error", StringComparison.Ordinal))
        {
            message = "jq: error: " + message;
        }

        var count = message.Split('\n').Count(
            static line => line.StartsWith("jq: error:", StringComparison.Ordinal));
        count = Math.Max(1, count);
        WriteError(
            host,
            message + $"\njq: {count.ToString(CultureInfo.InvariantCulture)} compile " +
            (count == 1 ? "error\n" : "errors\n"));
    }

    private static int ParseAtoi(string text)
    {
        var offset = 0;
        while (offset < text.Length && char.IsWhiteSpace(text[offset]))
        {
            offset++;
        }

        var sign = 1;
        if (offset < text.Length && text[offset] is '+' or '-')
        {
            if (text[offset++] == '-')
            {
                sign = -1;
            }
        }

        long value = 0;
        while (offset < text.Length && text[offset] is >= '0' and <= '9')
        {
            value = Math.Min((long)int.MaxValue + 1, value * 10 + text[offset] - '0');
            offset++;
        }

        value *= sign;
        return value > int.MaxValue
            ? int.MaxValue
            : value < int.MinValue
                ? int.MinValue
                : (int)value;
    }

    private static bool RunSilentValueSelfTests()
    {
        var valid = libjq.jv_parse("{\"a\":42}");
        try
        {
            if (!valid.IsValid)
            {
                return false;
            }
        }
        finally
        {
            libjq.jv_free(valid);
        }

        var invalid = libjq.jv_parse("{\"a':\"12\"}");
        try
        {
            return !invalid.IsValid;
        }
        finally
        {
            libjq.jv_free(invalid);
        }
    }

    private static int RunLifecycleSelfTests(CliHost host)
    {
        try
        {
            if (!TestStartResets(host, ".[]", "[1,2,3]") ||
                !TestStartResets(
                    host,
                    ".[] | if .%2 == 0 then halt_error else . end",
                    "[1,2,3]") ||
                !TestCompileArguments(host) ||
                !TestRecompile(host) ||
                !TestExhaustAndReuse(host) ||
                !TestParallelParseAndExecute(host))
            {
                return 1;
            }

            return 0;
        }
        catch (Exception exception) when (
            exception is JqCompileException or JqRuntimeException or InvalidOperationException)
        {
            WriteOutput(host, "*** Internal jq state test failed: " + exception.Message + "\n");
            return 1;
        }
    }

    private static bool TestStartResets(CliHost host, string program, string input)
    {
        WriteOutput(host, "Test jq_state: " + program + "\n");
        jq_state? state = CreateState(host);
        try
        {
            var resolver = CreateResolver(new CliOptions(), host);
            if (!Compile(state, program, resolver, host))
            {
                return false;
            }

            libjq.jq_start(state, libjq.jv_parse(input), 0);
            if (!IsFreshExecution(state))
            {
                return false;
            }

            while (TryNext(state, out _))
            {
            }

            libjq.jq_start(state, libjq.jv_parse(input), 0);
            return IsFreshExecution(state);
        }
        finally
        {
            libjq.jq_teardown(ref state);
        }
    }

    private static bool TestCompileArguments(CliHost host)
    {
        WriteOutput(host, "Test jq_compile_args with array args\n");
        jq_state? state = CreateState(host);
        var resolver = CreateResolver(new CliOptions(), host);
        try
        {
            return CompileAndCheck("42", "[]", "null", "42") &&
                   CompileAndCheck(
                       "$val",
                       "[{\"name\":\"val\",\"value\":42}]",
                       "null",
                       "42") &&
                   CompileAndCheck(
                       "$x + $y",
                       "[{\"name\":\"x\",\"value\":1},{\"name\":\"y\",\"value\":2}]",
                       "null",
                       "3") &&
                   CompileAndCheck(
                       "$a + $b + $c",
                       "[{\"name\":\"a\",\"value\":\"hello\"},{\"name\":\"b\",\"value\":\" \"},{\"name\":\"c\",\"value\":\"world\"}]",
                       "null",
                       "\"hello world\"") &&
                   CompileAndCheck(
                       "$x * $y",
                       "{\"x\":10,\"y\":20}",
                       "null",
                       "200");

            bool CompileAndCheck(string program, string args, string input, string expected)
            {
                WriteOutput(host, "  subtest: " + program + "\n");
                if (libjq.jq_compile_args(
                        state,
                        program,
                        libjq.jv_parse(args),
                        resolver,
                        host.CurrentDirectory,
                        loadUserStartupLibrary: true) == 0)
                {
                    return false;
                }

                libjq.jq_start(state, libjq.jv_parse(input), 0);
                return TryNext(state, out var result) &&
                       libjq.jv_equal(result, libjq.jv_parse(expected)) &&
                       !TryNext(state, out _);
            }
        }
        finally
        {
            libjq.jq_teardown(ref state);
        }
    }

    private static bool TestRecompile(CliHost host)
    {
        WriteOutput(host, "Test jq recompile on same state\n");
        jq_state? state = CreateState(host);
        var resolver = CreateResolver(new CliOptions(), host);
        try
        {
            return Check(". + 1", "1", "2") &&
                   Check(". * 2", "5", "10") &&
                   CheckArguments() &&
                   Check(". - 1", "10", "9");

            bool Check(string program, string input, string expected)
            {
                if (!Compile(state, program, resolver, host))
                {
                    return false;
                }

                libjq.jq_start(state, libjq.jv_parse(input), 0);
                return TryNext(state, out var result) &&
                       libjq.jv_equal(result, libjq.jv_parse(expected)) &&
                       !TryNext(state, out _);
            }

            bool CheckArguments()
            {
                var arguments = libjq.jv_parse(
                    "[{\"name\":\"n\",\"value\":100},{\"name\":\"m\",\"value\":7}]");
                if (libjq.jq_compile_args(
                        state,
                        "$n + $m",
                        arguments,
                        resolver,
                        host.CurrentDirectory,
                        loadUserStartupLibrary: true) == 0)
                {
                    return false;
                }

                libjq.jq_start(state, libjq.jv_null(), 0);
                return TryNext(state, out var result) &&
                       libjq.jv_equal(result, libjq.jv_number(107)) &&
                       !TryNext(state, out _);
            }
        }
        finally
        {
            libjq.jq_teardown(ref state);
        }
    }

    private static bool TestExhaustAndReuse(CliHost host)
    {
        WriteOutput(host, "Test jq exhaust and reuse\n");
        jq_state? state = CreateState(host);
        try
        {
            if (!Compile(state, ".[]", CreateResolver(new CliOptions(), host), host))
            {
                return false;
            }

            return Check("[1,2,3]", ["1", "2", "3"]) &&
                   Check("[10,20]", ["10", "20"]) &&
                   Check("[]", []) &&
                   Check("[99]", ["99"]);

            bool Check(string input, IReadOnlyList<string> expected)
            {
                libjq.jq_start(state, libjq.jv_parse(input), 0);
                foreach (var item in expected)
                {
                    if (!TryNext(state, out var result) ||
                        !libjq.jv_equal(result, libjq.jv_parse(item)))
                    {
                        return false;
                    }
                }

                return !TryNext(state, out _);
            }
        }
        finally
        {
            libjq.jq_teardown(ref state);
        }
    }

    private static bool TestParallelParseAndExecute(CliHost host)
    {
        var tasks = Enumerable.Range(0, 3).Select(taskIndex => Task.Run(() =>
        {
            _ = taskIndex;
            jq_state? state = CreateState(host);
            try
            {
                if (!Compile(
                        state,
                        ".data",
                        CreateResolver(new CliOptions(), host),
                        host))
                {
                    return false;
                }

                libjq.jq_start(state, libjq.jv_parse("{\"data\":1}"), 0);
                return TryNext(state, out var result) &&
                       libjq.jv_equal(result, libjq.jv_number(1)) &&
                       !TryNext(state, out _);
            }
            finally
            {
                libjq.jq_teardown(ref state);
            }
        })).ToArray();

        Task.WaitAll(tasks);
        return tasks.All(static task => task.Result);
    }

    private static void WriteOutput(CliHost host, string text) =>
        CliOutput.WriteUtf8(host.StandardOutput, text);

    private static void WriteError(CliHost host, string text) =>
        CliOutput.WriteUtf8(host.StandardError, text);

    private static bool IsFreshExecution(jq_state state)
    {
        var exitCode = libjq.jq_get_exit_code(state);
        var errorMessage = libjq.jq_get_error_message(state);
        try
        {
            return libjq.jq_halted(state) == 0 &&
                   !exitCode.IsValid &&
                   !errorMessage.IsValid;
        }
        finally
        {
            libjq.jv_free(exitCode);
            libjq.jv_free(errorMessage);
        }
    }

    private sealed class TestLineReader(Stream stream, bool leaveOpen) : IDisposable
    {
        private bool disposed;

        internal string? ReadLine()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var bytes = new List<byte>();
            while (bytes.Count < TestLineBufferSize - 1)
            {
                var next = stream.ReadByte();
                if (next < 0)
                {
                    break;
                }

                bytes.Add((byte)next);
                if (next == '\n')
                {
                    break;
                }
            }

            return bytes.Count == 0 ? null : Encoding.UTF8.GetString(bytes.ToArray());
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            if (!leaveOpen)
            {
                stream.Dispose();
            }
        }
    }
}
