namespace DotNetJq.Cli.Tests;

public sealed class PinnedCliContractTests
{
    [Fact]
    public Task ArgumentParsingAndInputModesMatchPinnedJq182() =>
        AssertCasesAsync(PinnedCliCases.ArgumentAndInputCases);

    [Fact]
    public Task FormattingModesMatchPinnedJq182() =>
        AssertCasesAsync(PinnedCliCases.FormattingCases);

    [Fact]
    public Task CompileArgumentsMatchPinnedJq182() =>
        AssertCasesAsync(PinnedCliCases.CompileArgumentCases);

    [Fact]
    public Task ExitStatusAndHaltMatchPinnedJq182() =>
        AssertCasesAsync(PinnedCliCases.ExitAndHaltCases);

    [Fact]
    public async Task RawOutputZeroUsesNulFraming()
    {
        var result = await CliProcess.RunAsync(
            CliTestEnvironment.Subject,
            ["-n", "--raw-output0", "\"alpha\", \"β\""],
            TestContext.Current.CancellationToken);

        CliAssertions.Equal(
            result,
            exitCode: 0,
            standardOutput: "alpha\0β\0"u8,
            standardError: []);
    }

    [Fact]
    public async Task RawOutputZeroRejectsNulAndPreservesEarlierRecords()
    {
        var result = await CliProcess.RunAsync(
            CliTestEnvironment.Subject,
            ["-n", "--raw-output0", "\"a\", \"c\\u0000d\", \"b\""],
            TestContext.Current.CancellationToken);

        CliAssertions.Equal(
            result,
            exitCode: 5,
            standardOutput: "a\0"u8,
            standardError: "jq: error (at <unknown>): Cannot dump a string containing NUL with --raw-output0 option\n"u8);
    }

    [Fact]
    public async Task RawInputUsesJqMalformedUtf8UnitRepairBeforeManagedDecoding()
    {
        ReadOnlyMemory<byte> surrogateEncoding = new byte[] { 0xED, 0xA0, 0x80 };
        var expected = "\"�\"\n"u8.ToArray();

        var line = await CliProcess.RunAsync(
            CliTestEnvironment.Subject,
            ["-R", "-c", "."],
            surrogateEncoding,
            cancellationToken: TestContext.Current.CancellationToken);
        var slurped = await CliProcess.RunAsync(
            CliTestEnvironment.Subject,
            ["-R", "-s", "-c", "."],
            surrogateEncoding,
            cancellationToken: TestContext.Current.CancellationToken);

        CliAssertions.Equal(line, 0, expected, []);
        CliAssertions.Equal(slurped, 0, expected, []);
    }

    [Fact]
    public async Task BinaryOptionIsIdempotentForByteOrientedStandardStreams()
    {
        ReadOnlyMemory<byte> input = new byte[]
        {
            (byte)'a', (byte)'\r', (byte)'\n',
            (byte)'b', 0x1a, (byte)'c',
        };
        var expected = "\"a\\r\\nb\\u001ac\"\n"u8.ToArray();

        var byDefault = await CliProcess.RunAsync(
            CliTestEnvironment.Subject,
            ["-R", "-s", "-c", "."],
            input,
            cancellationToken: TestContext.Current.CancellationToken);
        var explicitlyBinary = await CliProcess.RunAsync(
            CliTestEnvironment.Subject,
            ["-R", "-s", "-c", "-b", "."],
            input,
            cancellationToken: TestContext.Current.CancellationToken);

        CliAssertions.Equal(byDefault, 0, expected, []);
        CliAssertions.Equal(explicitlyBinary, 0, expected, []);
    }

    [Fact]
    public async Task ForcedColorUsesPinnedJqAnsiSequences()
    {
        var result = await CliProcess.RunAsync(
            CliTestEnvironment.Subject,
            ["-Ccn", "[{\"a\":true,\"b\":false},\"abc\",123,null]"],
            TestContext.Current.CancellationToken);

        const string reset = "\u001b[0m";
        var expected =
            "\u001b[1;39m[" + reset +
            "\u001b[1;39m{" + reset +
            "\u001b[1;34m\"a\"" + reset +
            "\u001b[1;39m:" + reset +
            "\u001b[0;39mtrue" + reset +
            "\u001b[1;39m," + reset +
            "\u001b[1;34m\"b\"" + reset +
            "\u001b[1;39m:" + reset +
            "\u001b[0;39mfalse" + reset +
            "\u001b[1;39m}" + reset +
            "\u001b[1;39m," + reset +
            "\u001b[0;32m\"abc\"" + reset +
            "\u001b[1;39m," + reset +
            "\u001b[0;39m123" + reset +
            "\u001b[1;39m," + reset +
            "\u001b[0;90mnull" + reset +
            "\u001b[1;39m]" + reset + "\n";

        CliAssertions.Equal(result, 0, expected);
    }

    [Fact]
    public async Task JqColorsSupportsCustomAndExplicitlyEmptyEntries()
    {
        var custom = await CliProcess.RunAsync(
            CliTestEnvironment.Subject,
            ["-Ccn", "."],
            environment: new Dictionary<string, string?> { ["JQ_COLORS"] = "4;31" },
            cancellationToken: TestContext.Current.CancellationToken);
        var empty = await CliProcess.RunAsync(
            CliTestEnvironment.Subject,
            ["-Ccn", "."],
            environment: new Dictionary<string, string?> { ["JQ_COLORS"] = ":" },
            cancellationToken: TestContext.Current.CancellationToken);

        CliAssertions.Equal(custom, 0, "\u001b[4;31mnull\u001b[0m\n");
        CliAssertions.Equal(empty, 0, "\u001b[mnull\u001b[0m\n");
    }

    [Fact]
    public async Task InvalidJqColorsReportsWarningAndUsesDefaults()
    {
        var result = await CliProcess.RunAsync(
            CliTestEnvironment.Subject,
            ["-Ccn", "."],
            environment: new Dictionary<string, string?> { ["JQ_COLORS"] = "invalid" },
            cancellationToken: TestContext.Current.CancellationToken);

        CliAssertions.Equal(
            result,
            0,
            "\u001b[0;90mnull\u001b[0m\n",
            "Failed to set $JQ_COLORS\n");
    }

    [Fact]
    public async Task SustainedStreamingInputsMatchPinnedJq182BeyondManagedDepthLimit()
    {
        const int valueCount = 700;
        const string filter =
            "reduce (., inputs) as $e (0; " +
            "if (($e|length)==2 and ($e[1]|type)==\"number\") " +
            "then . + $e[1] else . end)";
        var standardInput =
            "[" + string.Join(',', Enumerable.Range(0, valueCount)) + "]\n";
        var arguments = new[] { "-M", "-c", "--stream", filter };

        var oracle = await CliTestEnvironment.RequireOracleAsync();
        var expected = await CliProcess.RunAsync(
            oracle,
            arguments,
            standardInput,
            cancellationToken: TestContext.Current.CancellationToken);
        var actual = await CliProcess.RunAsync(
            CliTestEnvironment.Subject,
            arguments,
            standardInput,
            cancellationToken: TestContext.Current.CancellationToken);

        CliAssertions.Equal(expected, 0, "244650\n", "");
        CliAssertions.Equal(
            actual,
            expected.ExitCode,
            expected.StandardOutput,
            expected.StandardError);
    }

    private static async Task AssertCasesAsync(IEnumerable<CliTextCase> cases)
    {
        foreach (var testCase in cases)
        {
            var result = await CliProcess.RunAsync(
                CliTestEnvironment.Subject,
                testCase.Arguments,
                testCase.StandardInput,
                timeout: null);

            try
            {
                CliAssertions.Equal(
                    result,
                    testCase.ExitCode,
                    testCase.StandardOutput,
                    testCase.StandardError);
            }
            catch (Exception exception) when (exception is Xunit.Sdk.XunitException)
            {
                throw new Xunit.Sdk.XunitException($"CLI case '{testCase.Name}' failed.\n{exception.Message}");
            }
        }
    }
}
