// jq 1.8.2 source references:
// - src/main.c:664: jq_set_input_cb(jq, jq_util_input_next_input_cb, input_state)
// - src/util.c: jq_util_input_next_input_cb/jq_util_input_next_input
// - src/builtin.c:f_input: plain invalid becomes "break"; invalid-with-message propagates

using System.Text;
using DotNetJq.Port;

namespace DotNetJq.Cli.Tests;

public sealed class CliInputCallbackCompatibilityTests
{
    [Fact]
    public async Task InputAndInputsConsumeOneForwardCliStream()
    {
        var result = await RunAsync(
            ["-M", "-c", "[.,input,inputs]"],
            "0\n1\n2\n3\n");

        CliAssertions.Equal(result, 0, "[0,1,2,3]\n", "");
    }

    [Fact]
    public async Task InputEndBecomesCatchableBreakWithoutChangingTheLastPosition()
    {
        var result = await RunAsync(
            ["-M", "-c", "[.,(try input catch .),input_filename,input_line_number]"],
            "0\n");

        CliAssertions.Equal(result, 0, "[0,\"break\",\"<stdin>\",1]\n", "");
    }

    [Fact]
    public async Task CallbackValuesAndErrorsUpdateFilenameAndLineBeforeReturning()
    {
        var value = await RunAsync(
            [
                "-M",
                "-c",
                "[input_filename,input_line_number,.,input,input_filename,input_line_number]",
            ],
            "0\n\n2\n");
        var error = await RunAsync(
            ["-M", "-c", "try input catch [.,input_filename,input_line_number]"],
            "0\n\nnot-json\n");

        CliAssertions.Equal(value, 0, "[\"<stdin>\",1,0,2,\"<stdin>\",3]\n", "");
        CliAssertions.Equal(
            error,
            0,
            "[\"Invalid numeric literal at line 4, column 0\",\"<stdin>\",3]\n",
            "");
    }

    [Theory]
    [InlineData("raw", "a\nb\n", "[\"a\",\"b\"]\n")]
    [InlineData("slurp", "1\n2\n", "[2,\"break\"]\n")]
    [InlineData("stream", "[1]\n", "[[[0],1],[[0]]]\n")]
    public async Task CallbackSharesRawSlurpAndStreamReaderState(
        string mode,
        string standardInput,
        string expectedOutput)
    {
        var arguments = mode switch
        {
            "raw" => new[] { "-R", "-M", "-c", "[.,input]" },
            "slurp" => ["-s", "-M", "-c", "[length,(try input catch .)]"],
            "stream" => ["--stream", "-M", "-c", "[.,input]"],
            _ => throw new InvalidOperationException("Unknown input mode."),
        };

        var result = await RunAsync(arguments, standardInput);

        CliAssertions.Equal(result, 0, expectedOutput, "");
    }

    [Fact]
    public async Task CallbackFileFailuresRetainSystemStatusAndDiagnostic()
    {
        using var directory = CliTestEnvironment.CreateTemporaryDirectory();
        var missing = directory.File("missing.json");

        var result = await RunAsync(["-n", "input", missing], standardInput: null);

        CliAssertions.Equal(
            result,
            2,
            "",
            $"jq: error: Could not open file {missing}: No such file or directory\n" +
            $"jq: error (at {missing}:0): break\n");
    }

    [Fact]
    public void CallbackCancellationRetainsCliExitStatus()
    {
        using var standardInput = new MemoryStream("1\n"u8.ToArray());
        using var standardOutput = new MemoryStream();
        using var standardError = new MemoryStream();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var host = new CliHost(
            standardInput,
            standardOutput,
            standardError,
            CliTestEnvironment.RepositoryRoot,
            Path.Combine(CliTestEnvironment.RepositoryRoot, "dotnetjq"),
            IsInputTerminal: false,
            IsOutputTerminal: false,
            SupportsColor: false,
            Environment: new Dictionary<string, string>(StringComparer.Ordinal),
            cancellation.Token);

        var exitCode = JqCliApplication.Run(["-n", "input"], host);

        Assert.Equal(130, exitCode);
        Assert.Empty(standardOutput.ToArray());
        Assert.Empty(standardError.ToArray());
    }

    [Fact]
    public void EarlyResetReleasesTheExactOwnedJvReturnedByTheCallback()
    {
        using var standardInput = new MemoryStream("[1,2]\n[3,4]\n"u8.ToArray());
        using var reader = new CliInputReader(
            standardInput,
            CliTestEnvironment.RepositoryRoot,
            files: [],
            rawInput: false,
            parserFlags: 0);
        jq_state? state = libjq.jq_init();
        var output = libjq.jv_invalid();
        jvp_array? callbackStorage = null;
        try
        {
            Assert.Equal(1, libjq.jq_compile(state, "input as $x | $x, inputs"));
            libjq.jq_set_input_cb(
                state,
                () =>
                {
                    var value = reader.ReadNextInput(state, CancellationToken.None);
                    if (value.IsValid && callbackStorage is null)
                    {
                        callbackStorage = Assert.IsType<jvp_array>(value.Value);
                    }

                    return value;
                });

            libjq.jq_start(state, libjq.jv_null(), 0);
            output = libjq.jq_next(state);
            Assert.True(output.IsValid);
            Assert.Same(callbackStorage, output.Value);

            libjq.jv_free(output);
            output = libjq.jv_invalid();
            Assert.NotNull(callbackStorage);
            Assert.True(callbackStorage.Refcnt.Count > 0);

            libjq.jq_reset(state);
            Assert.Equal(0, callbackStorage.Refcnt.Count);
        }
        finally
        {
            libjq.jv_free(output);
            libjq.jq_teardown(ref state);
        }
    }

    [Fact]
    public void ParserErrorIsReturnedAsOneOwnedInvalidWithMessage()
    {
        using var standardInput = new MemoryStream("not-json\n"u8.ToArray());
        using var reader = new CliInputReader(
            standardInput,
            CliTestEnvironment.RepositoryRoot,
            files: [],
            rawInput: false,
            parserFlags: 0);
        jq_state? state = libjq.jq_init();
        var error = libjq.jv_invalid();
        var message = libjq.jv_invalid();
        jvp_invalid? errorStorage = null;
        jvp_string? messageStorage = null;
        try
        {
            error = reader.ReadNextInput(state, CancellationToken.None);
            errorStorage = Assert.IsType<jvp_invalid>(error.Value);
            Assert.Equal(1, errorStorage.Refcnt.Count);

            message = libjq.jv_invalid_get_msg(error);
            error = libjq.jv_invalid();
            messageStorage = Assert.IsType<jvp_string>(message.Value);
            Assert.Contains("Invalid numeric literal", message.StringValue, StringComparison.Ordinal);
            Assert.Equal(0, errorStorage.Refcnt.Count);
            Assert.Equal(1, messageStorage.Refcnt.Count);
        }
        finally
        {
            libjq.jv_free(error);
            libjq.jv_free(message);
            libjq.jq_teardown(ref state);
        }

        Assert.NotNull(messageStorage);
        Assert.Equal(0, messageStorage.Refcnt.Count);
    }

    [Fact]
    public async Task LargeStreamingContinuationConsumesEveryEventExactlyOnce()
    {
        const int valueCount = 5_000;
        const string filter =
            "reduce (., inputs) as $e (0; " +
            "if (($e|length)==2 and ($e[1]|type)==\"number\") " +
            "then . + $e[1] else . end)";
        var standardInput =
            "[" + string.Join(',', Enumerable.Range(0, valueCount)) + "]\n";

        var result = await RunAsync(
            ["-M", "-c", "--stream", filter],
            standardInput);

        CliAssertions.Equal(result, 0, "12497500\n", "");
    }

    private static Task<CliResult> RunAsync(
        IEnumerable<string> arguments,
        string? standardInput) =>
        CliProcess.RunAsync(
            CliTestEnvironment.Subject,
            arguments,
            standardInput,
            cancellationToken: TestContext.Current.CancellationToken);
}
