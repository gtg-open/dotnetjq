using System.Text.Json;
using DotNetJq.Tests.Harness;

namespace DotNetJq.Tests;

public sealed class InterpolatedStringContinuationCompatibilityTests
{
    [Fact]
    public async Task LaterInterpolationIsTheOuterContinuationLikeJq182()
    {
        const string source = "\"\\((1,2))\\((3,4))\"";
        string[] expected = ["\"13\"", "\"23\"", "\"14\"", "\"24\""];

        var managed = Execute(source);

        Assert.Equal(expected, managed);
        await AssertMatchesOracle(source, managed);
    }

    [Fact]
    public async Task EarlierInterpolationIsReopenedForEveryLaterBranch()
    {
        const string source = "\"\\(((1,2) | debug))\\((3,4))\"";
        string[] expected = ["\"13\"", "\"23\"", "\"14\"", "\"24\""];
        string[] expectedDebug = ["1", "2", "1", "2"];
        var debug = new RecordingSink();

        var managed = Execute(source, new JqExecutionCapabilities { Debug = debug });

        Assert.Equal(expected, managed);
        Assert.Equal(expectedDebug, debug.Values);

        if (!JqOracle.TryResolveExecutable(out _))
        {
            return;
        }

        var oracle = await JqOracle.ExecuteAsync(
            source,
            "null",
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(oracle.Succeeded, oracle.StandardError);
        Assert.Equal(managed, oracle.OutputLines);
        Assert.Equal(
            ["[\"DEBUG:\",1]", "[\"DEBUG:\",2]", "[\"DEBUG:\",1]", "[\"DEBUG:\",2]"],
            SplitLines(oracle.StandardError));
    }

    [Fact]
    public async Task EmptyLiteralAndSingleInterpolationStringsRemainCompatible()
    {
        (string Source, string[] Expected)[] cases =
        [
            ("\"\"", ["\"\""]),
            ("\"literal\"", ["\"literal\""]),
            ("\"before\\(1)after\"", ["\"before1after\""]),
            ("\"\\((1,2))\"", ["\"1\"", "\"2\""]),
        ];

        foreach (var (source, expected) in cases)
        {
            var managed = Execute(source);
            Assert.Equal(expected, managed);
            await AssertMatchesOracle(source, managed);
        }
    }

    private static string[] Execute(
        string source,
        JqExecutionCapabilities? capabilities = null)
    {
        using var execution = JqProgram.Compile(source).StartExecution(
            "null",
            capabilities: capabilities);
        var output = new List<string>();
        while (execution.TryRead(out var value))
        {
            output.Add(value.GetRawText());
        }

        return output.ToArray();
    }

    private static async Task AssertMatchesOracle(
        string source,
        IReadOnlyList<string> managed)
    {
        if (!JqOracle.TryResolveExecutable(out _))
        {
            return;
        }

        var oracle = await JqOracle.ExecuteAsync(
            source,
            "null",
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(oracle.Succeeded, oracle.StandardError);
        Assert.Equal(managed, oracle.OutputLines);
    }

    private static string[] SplitLines(string value) => value
        .Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace('\r', '\n')
        .Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private sealed class RecordingSink : IJqValueSink
    {
        internal List<string> Values { get; } = [];

        public void Write(JsonElement value) => Values.Add(value.GetRawText());
    }
}
