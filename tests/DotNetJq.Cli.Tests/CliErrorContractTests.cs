using System.Text;

namespace DotNetJq.Cli.Tests;

public sealed class CliErrorContractTests
{
    [Fact]
    public async Task UnknownLongOptionUsesJqUsageDiagnosticAndExitTwo()
    {
        var result = await CliProcess.RunAsync(
            CliTestEnvironment.Subject,
            ["--definitely-not-an-option"],
            TestContext.Current.CancellationToken);

        CliAssertions.Equal(
            result,
            2,
            "",
            "jq: Unknown option --definitely-not-an-option\n" +
            "Use dotnetjq --help for help with command-line options,\n" +
            "or see the jq manpage, or online docs at https://jqlang.org\n");
    }

    [Fact]
    public async Task InvalidArgjsonIsACommandLineError()
    {
        var result = await CliProcess.RunAsync(
            CliTestEnvironment.Subject,
            ["-n", "--argjson", "value", "invalid", "$value"],
            TestContext.Current.CancellationToken);

        CliAssertions.Equal(
            result,
            2,
            "",
            "jq: invalid JSON text passed to --argjson\n" +
            "Use dotnetjq --help for help with command-line options,\n" +
            "or see the jq manpage, or online docs at https://jqlang.org\n");
    }

    [Fact]
    public async Task MissingNamedArgumentOperandsPreserveUpstreamExamples()
    {
        var cases = new[]
        {
            (Option: "arg", Example: "varname value"),
            (Option: "argjson", Example: "varname text"),
            (Option: "rawfile", Example: "varname filename"),
            (Option: "slurpfile", Example: "varname filename"),
        };
        foreach (var testCase in cases)
        {
            var result = await CliProcess.RunAsync(
                CliTestEnvironment.Subject,
                ["-n", "--" + testCase.Option, "only-one"],
                TestContext.Current.CancellationToken);

            CliAssertions.Equal(
                result,
                2,
                "",
                $"jq: --{testCase.Option} takes two parameters " +
                $"(e.g. --{testCase.Option} {testCase.Example})\n" +
                "Use dotnetjq --help for help with command-line options,\n" +
                "or see the jq manpage, or online docs at https://jqlang.org\n");
        }
    }

    [Fact]
    public async Task InvalidJsonargsIsACommandLineError()
    {
        var result = await CliProcess.RunAsync(
            CliTestEnvironment.Subject,
            ["-n", "$ARGS.positional", "--jsonargs", "null", "invalid"],
            TestContext.Current.CancellationToken);

        CliAssertions.Equal(
            result,
            2,
            "",
            "jq: invalid JSON text passed to --jsonargs\n" +
            "Use dotnetjq --help for help with command-line options,\n" +
            "or see the jq manpage, or online docs at https://jqlang.org\n");
    }

    [Fact]
    public async Task InvalidIndentIsACommandLineError()
    {
        var result = await CliProcess.RunAsync(
            CliTestEnvironment.Subject,
            ["-n", "--indent", "8", "."],
            TestContext.Current.CancellationToken);

        CliAssertions.Equal(
            result,
            2,
            "",
            "jq: --indent takes a number between -1 and 7\n" +
            "Use dotnetjq --help for help with command-line options,\n" +
            "or see the jq manpage, or online docs at https://jqlang.org\n");
    }

    [Fact]
    public async Task CompileFailureHasExitThreeAndExactSourceDiagnostic()
    {
        var result = await CliProcess.RunAsync(
            CliTestEnvironment.Subject,
            ["-n", "if"],
            TestContext.Current.CancellationToken);

        CliAssertions.Equal(
            result,
            3,
            "",
            "jq: error: syntax error, unexpected end of file at <top-level>, line 1, column 1:\n" +
            "    if\n" +
            "    ^^\n" +
            "jq: 1 compile error\n");
    }

    [Fact]
    public async Task InvalidJsonInputHasExitFiveAndParseDiagnostic()
    {
        var result = await CliProcess.RunAsync(
            CliTestEnvironment.Subject,
            ["."],
            "foobar",
            cancellationToken: TestContext.Current.CancellationToken);

        CliAssertions.Equal(
            result,
            5,
            "",
            "jq: parse error: Invalid literal at EOF at line 1, column 6\n");
    }

    [Fact]
    public async Task UncaughtRuntimeErrorHasExitFiveAndInputOrigin()
    {
        var unterminatedFirstLine = await CliProcess.RunAsync(
            CliTestEnvironment.Subject,
            [".foo"],
            "1",
            cancellationToken: TestContext.Current.CancellationToken);

        CliAssertions.Equal(
            unterminatedFirstLine,
            5,
            "",
            "jq: error (at <stdin>:0): Cannot index number with string (\"foo\")\n");

        // jq-1.8.2 util.c increments current_line when fgets() obtains the
        // newline, before jv_parser can return an earlier-completed string or
        // container. These values exposed the old arbitrary-chunk accounting.
        var terminatedString = await CliProcess.RunAsync(
            CliTestEnvironment.Subject,
            [".foo"],
            "\"text\"\n",
            cancellationToken: TestContext.Current.CancellationToken);
        CliAssertions.Equal(
            terminatedString,
            5,
            "",
            "jq: error (at <stdin>:1): Cannot index string with string (\"foo\")\n");

        var multilineObject = await CliProcess.RunAsync(
            CliTestEnvironment.Subject,
            [". + 1"],
            "{\n\"value\":1}\n",
            cancellationToken: TestContext.Current.CancellationToken);
        CliAssertions.Equal(
            multilineObject,
            5,
            "",
            "jq: error (at <stdin>:2): object ({\"value\":1}) and number (1) cannot be added\n");

        // Native jq's 4096-byte state buffer gives fgets() a 4092-byte
        // request, so at most 4091 input bytes precede the C terminator. A
        // scalar completed in that first no-newline chunk retains line zero;
        // moving its closing quote into the next newline-bearing chunk reports
        // line one.
        var beforeNativeChunkBoundary = await CliProcess.RunAsync(
            CliTestEnvironment.Subject,
            [".foo"],
            "\"" + new string('a', 4_088) + "\" \n",
            cancellationToken: TestContext.Current.CancellationToken);
        CliAssertions.Equal(
            beforeNativeChunkBoundary,
            5,
            "",
            "jq: error (at <stdin>:0): Cannot index string with string (\"foo\")\n");

        var afterNativeChunkBoundary = await CliProcess.RunAsync(
            CliTestEnvironment.Subject,
            [".foo"],
            "\"" + new string('a', 4_090) + "\"\n",
            cancellationToken: TestContext.Current.CancellationToken);
        CliAssertions.Equal(
            afterNativeChunkBoundary,
            5,
            "",
            "jq: error (at <stdin>:1): Cannot index string with string (\"foo\")\n");
    }

    [Fact]
    public async Task UncaughtStringErrorUsesNativeCStringPresentationBoundary()
    {
        var result = await CliProcess.RunAsync(
            CliTestEnvironment.Subject,
            ["-n", "error(\"a\\u0000b\")"],
            cancellationToken: TestContext.Current.CancellationToken);

        CliAssertions.Equal(
            result,
            5,
            "",
            "jq: error (at <unknown>): a\n");
    }

    [Fact]
    public async Task MissingInputFileKeepsEarlierOutputThenReturnsExitTwo()
    {
        using var directory = CliTestEnvironment.CreateTemporaryDirectory();
        var present = directory.File("present.json");
        var absent = directory.File("absent.json");
        await File.WriteAllTextAsync(
            present,
            "1",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            TestContext.Current.CancellationToken);

        var result = await CliProcess.RunAsync(
            CliTestEnvironment.Subject,
            ["-c", ".", present, absent],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal("1\n", result.StandardOutputText);
        Assert.StartsWith($"jq: error: Could not open file {absent}:", result.StandardErrorText, StringComparison.Ordinal);
        Assert.EndsWith("\n", result.StandardErrorText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProgramFileContainingNulIsRejectedBeforeCompilation()
    {
        using var directory = CliTestEnvironment.CreateTemporaryDirectory();
        var filter = directory.File("nul.jq");
        await File.WriteAllBytesAsync(filter, "42\0ignored"u8.ToArray(), TestContext.Current.CancellationToken);

        var result = await CliProcess.RunAsync(
            CliTestEnvironment.Subject,
            ["-n", "-f", filter],
            cancellationToken: TestContext.Current.CancellationToken);

        CliAssertions.Equal(result, 2, "", "jq: program file contains NUL bytes\n");
    }
}
