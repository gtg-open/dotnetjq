using System.Text;
using System.Text.Json;
using DotNetJq.Port;

namespace DotNetJq.Cli.Tests;

public sealed class CliOutputOwnershipTests
{
    [Fact]
    [Trait("Category", "OracleDifferential")]
    public async Task DirectDebugAndStderrCallbacksMatchPinnedProcessBytes()
    {
        var oracle = await CliTestEnvironment.RequireOracleAsync();
        string[] arguments =
        [
            "-n",
            "-c",
            "([1.00] | debug), ({\"exact\":1.00} | stderr)",
        ];
        var expected = await CliProcess.RunAsync(
            oracle,
            arguments,
            TestContext.Current.CancellationToken);
        var actual = await CliProcess.RunAsync(
            CliTestEnvironment.Subject,
            arguments,
            TestContext.Current.CancellationToken);

        Assert.Equal(expected.ExitCode, actual.ExitCode);
        Assert.Equal(expected.StandardOutput, actual.StandardOutput);
        Assert.Equal(expected.StandardError, actual.StandardError);
    }

    [Fact]
    [Trait("Category", "OracleDifferential")]
    public async Task MainPrintFlagBoundariesMatchPinnedProcessBytes()
    {
        var oracle = await CliTestEnvironment.RequireOracleAsync();
        string[][] invocations =
        [
            ["-Cnar", "\"μ\""],
            ["-CnaS", "{z:1,\"é\":2}|stderr|empty"],
            ["-CnaS", "{z:1,\"é\":2}|halt_error(7)"],
            ["-CnaS", "{z:1,\"é\":2}|debug|empty"],
        ];

        foreach (var arguments in invocations)
        {
            var expected = await CliProcess.RunAsync(
                oracle,
                arguments,
                TestContext.Current.CancellationToken);
            var actual = await CliProcess.RunAsync(
                CliTestEnvironment.Subject,
                arguments,
                TestContext.Current.CancellationToken);

            Assert.Equal(expected.ExitCode, actual.ExitCode);
            Assert.Equal(expected.StandardOutput, actual.StandardOutput);
            Assert.Equal(expected.StandardError, actual.StandardError);
        }
    }

    [Theory]
    [Trait("Category", "OracleDifferential")]
    [InlineData("[123456789,2,3,4] + {}")]
    [InlineData("\"😀abcdefghijklmno😀abcdefghijklmno\" + {}")]
    [InlineData("{abcdefgh:123456789,ijklmnop:2} + []")]
    public async Task ThirtyByteTypeErrorTruncationMatchesPinnedJq182(string filter)
    {
        var oracle = await CliTestEnvironment.RequireOracleAsync();
        var arguments = new[] { "-n", "-c", filter };
        var expected = await CliProcess.RunAsync(
            oracle,
            arguments,
            TestContext.Current.CancellationToken);
        var actual = await CliProcess.RunAsync(
            CliTestEnvironment.Subject,
            arguments,
            TestContext.Current.CancellationToken);

        Assert.Equal(expected.ExitCode, actual.ExitCode);
        Assert.Equal(expected.StandardOutput, actual.StandardOutput);
        Assert.Equal(expected.StandardError, actual.StandardError);
    }

    [Fact]
    public void RepeatedCanonicalStreamFormattingBorrowsAndPreservesTheCallersOwner()
    {
        var value = libjq.jv_array([libjq.jv_number(1), libjq.jv_number(2)]);
        var storage = Assert.IsType<jvp_array>(value.Value);
        using var output = new MemoryStream();
        for (var iteration = 0; iteration < 128; iteration++)
        {
            libjq.jv_dumpf(libjq.jv_copy(value), output, flags: 0);
            Assert.Equal(1, storage.Refcnt.Count);
        }

        Assert.Equal(
            string.Concat(Enumerable.Repeat("[1,2]", 128)),
            Encoding.UTF8.GetString(output.ToArray()));
        libjq.jv_free(value);
        Assert.Equal(0, storage.Refcnt.Count);
    }

    [Fact]
    public void RepeatedStandardErrorAndDebugCallbacksReleaseTheirConvertedValues()
    {
        using var standardOutput = new MemoryStream();
        using var standardError = new MemoryStream();
        var output = new CliOutput(
            standardOutput,
            standardError,
            new CliOptions { CompactOutput = true },
            color: false,
            colorConfiguration: null,
            out var validColorConfiguration);
        Assert.True(validColorConfiguration);
        using var document = JsonDocument.Parse("{\"a\":[1,2]}");

        for (var iteration = 0; iteration < 128; iteration++)
        {
            output.WriteStandardError(document.RootElement);
            output.WriteDebug(document.RootElement);
        }

        var text = Encoding.UTF8.GetString(standardError.ToArray());
        Assert.Equal(
            string.Concat(Enumerable.Repeat(
                "{\"a\":[1,2]}[\"DEBUG:\",{\"a\":[1,2]}]\n",
                128)),
            text);
    }

    [Fact]
    public void InternalJqCallbacksConsumeTheOriginalOwnedValuesWithoutJsonRoundTrip()
    {
        using var standardOutput = new MemoryStream();
        using var standardError = new MemoryStream();
        var output = new CliOutput(
            standardOutput,
            standardError,
            new CliOptions { CompactOutput = true },
            color: false,
            colorConfiguration: null,
            out var validColorConfiguration);
        Assert.True(validColorConfiguration);

        var stderrValue = libjq.jv_parse("{\"exact\":1.00}");
        var stderrStorage = Assert.IsType<jvp_object>(stderrValue.Value);
        output.WriteStandardError(stderrValue);
        Assert.Equal(0, stderrStorage.Refcnt.Count);

        var debugValue = libjq.jv_parse("[1.00]");
        var debugStorage = Assert.IsType<jvp_array>(debugValue.Value);
        output.WriteDebug(debugValue);
        Assert.Equal(0, debugStorage.Refcnt.Count);

        Assert.Equal(
            "{\"exact\":1.00}[\"DEBUG:\",[1.00]]\n",
            Encoding.UTF8.GetString(standardError.ToArray()));
    }
}
