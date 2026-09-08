using System.Text.Json;
using DotNetJq.Tests.Harness;

namespace DotNetJq.Tests;

public sealed class PatternBindingAndObjectEvaluationCompatibilityTests
{
    private const string NormalScopeFilter =
        "{\"a\":\"foo\",\"foo\":42,\"bar\":99} as $o | " +
        "\"bar\" as $a | $o as {$a, ($a): $b} | [$a,$b]";

    private const string AlternativeScopeFilter =
        "{\"a\":\"foo\",\"foo\":42,\"bar\":99} as $o | " +
        "\"bar\" as $a | $o as {$a, ($a): $b} ?// [$a,$b] | [$a,$b]";

    [Theory]
    [InlineData(NormalScopeFilter, "[\"foo\",99]")]
    [InlineData(AlternativeScopeFilter, "[\"foo\",42]")]
    public async Task ComputedPatternKeyScopeMatchesPinnedJq182(
        string filter,
        string expected)
    {
        Assert.Equal([expected], Execute(filter));

        if (JqOracle.TryResolveExecutable(out _))
        {
            var oracle = await JqOracle.ExecuteAsync(
                filter,
                string.Empty,
                ["--null-input"],
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(oracle.Succeeded, oracle.StandardError);
            Assert.Equal([expected], oracle.OutputLines);
        }
    }

    [Theory]
    [InlineData("def f(g): def g: \"local\"; g; f(\"arg\")", "\"local\"")]
    [InlineData("def g:\"outer\"; def f(g): g; f(\"arg\")", "\"arg\"")]
    public async Task NearestCallableBinderWinsAcrossFunctionsAndFilterParameters(
        string filter,
        string expected)
    {
        Assert.Equal([expected], Execute(filter));

        if (JqOracle.TryResolveExecutable(out _))
        {
            var oracle = await JqOracle.ExecuteAsync(
                filter,
                string.Empty,
                ["--null-input"],
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(oracle.Succeeded, oracle.StandardError);
            Assert.Equal([expected], oracle.OutputLines);
        }
    }

    [Fact]
    public void NormalComputedKeyScopeStaysFrozenThroughNestedPatterns()
    {
        const string filter =
            "{\"a\":\"foo\",\"foo\":{\"v\":42},\"bar\":{\"v\":99}} as $o | " +
            "\"bar\" as $a | [$o] as [{$a, ($a): {v:$b}}] | [$a,$b]";

        Assert.Equal(["[\"foo\",99]"], Execute(filter));
    }

    [Theory]
    [InlineData("{\"x\":1,\"y\":2} as {x:$x,y:$x} | $x", "1")]
    [InlineData("[10,20] as [$x,$x] | $x", "20")]
    [InlineData("{\"x\":1,\"y\":2} as {x:$x,y:$x} ?// [$x] | $x", "2")]
    [InlineData("[10,20] as [$x,$x] ?// {$x} | $x", "10")]
    public void RepeatedNamesFollowNormalBinderAndAlternativeSlotSemantics(
        string filter,
        string expected) =>
        Assert.Equal([expected], Execute(filter));

    [Fact]
    public void ArrayPatternMatchersExecuteFromRightToLeft()
    {
        const string filter =
            "try ([{},{}] as " +
            "[{(error(\"first\")):$a},{(error(\"second\")):$b}] | .) catch .";

        Assert.Equal(["\"second\""], Execute(filter));
    }

    [Fact]
    public async Task LaterObjectInputFilterReopensForEachEarlierBranch()
    {
        const string filter = "{a:(1,2), b:input}";
        string[] expected = ["{\"a\":1,\"b\":10}", "{\"a\":2,\"b\":20}"];
        var input = new QueueInputSource(10, 20);

        Assert.Equal(expected, Execute(filter, input));
        await AssertOracleAsync(filter, expected, "10\n20\n");
    }

    [Fact]
    public async Task ExhaustiveLaterObjectInputConsumesTheFirstEarlierBranch()
    {
        const string filter = "{a:(1,2), b:inputs}";
        string[] expected = ["{\"a\":1,\"b\":10}", "{\"a\":1,\"b\":20}"];
        var input = new QueueInputSource(10, 20);

        Assert.Equal(expected, Execute(filter, input));
        await AssertOracleAsync(filter, expected, "10\n20\n");
    }

    [Fact]
    public async Task ExplicitPairValueFilterReopensForEachKeyBranch()
    {
        const string filter = "{((\"a\",\"b\")):input}";
        string[] expected = ["{\"a\":10}", "{\"b\":20}"];
        var input = new QueueInputSource(10, 20);

        Assert.Equal(expected, Execute(filter, input));
        await AssertOracleAsync(filter, expected, "10\n20\n");
    }

    private static string[] Execute(string filter) =>
        JqProgram.Compile(filter)
            .Execute("null")
            .Select(static value => value.GetRawText())
            .ToArray();

    private static string[] Execute(string filter, IJqInputSource input)
    {
        using var execution = JqProgram.Compile(filter).StartExecution(
            "null",
            capabilities: new JqExecutionCapabilities { Input = input });
        var outputs = new List<string>();
        while (execution.TryRead(out var value))
        {
            outputs.Add(value.GetRawText());
        }

        return outputs.ToArray();
    }

    private static async Task AssertOracleAsync(
        string filter,
        string[] expected,
        string standardInput)
    {
        if (!JqOracle.TryResolveExecutable(out _))
        {
            return;
        }

        var oracle = await JqOracle.ExecuteAsync(
            filter,
            standardInput,
            ["--null-input"],
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(oracle.Succeeded, oracle.StandardError);
        Assert.Equal(expected, oracle.OutputLines);
    }

    private sealed class QueueInputSource(params int[] values) : IJqInputSource
    {
        private readonly Queue<int> values = new(values);

        public JqInputReadResult ReadNext(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!values.TryDequeue(out var value))
            {
                return JqInputReadResult.End;
            }

            using var document = JsonDocument.Parse(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return JqInputReadResult.FromValue(document.RootElement);
        }
    }
}
