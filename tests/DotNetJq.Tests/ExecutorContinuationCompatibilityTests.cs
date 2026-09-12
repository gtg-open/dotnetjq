using System.Text.Json;
using System.Globalization;
using DotNetJq.Tests.Harness;

namespace DotNetJq.Tests;

/// <summary>
/// Adversarial coverage for jq-1.8.2's observable continuation behavior in the
/// direct frame/fork bytecode VM.
/// </summary>
public sealed class ExecutorContinuationCompatibilityTests
{
    public static TheoryData<string, string, string[]> ContinuationCases => new()
    {
        {
            "def f($n): if $n==0 then 0 else " +
            "(def g($m): f($m); g($n-1)) end; f(4)",
            "null",
            ["0"]
        },
        {
            "def invoke(f; $n): if $n==0 then f else invoke(f; $n-1) end; " +
            "invoke((., .+1); 3)",
            "null",
            ["null", "1"]
        },
        {
            "def emit($n): if $n==0 then 0 else emit($n-1),$n end; emit(3)",
            "null",
            ["0", "1", "2", "3"]
        },
        {
            "def fallback($n): if $n==0 then empty else fallback($n-1)//$n end; fallback(3)",
            "null",
            ["1"]
        },
        {
            "def caught($n): if $n==0 then (0,error(\"done\")) " +
            "else try caught($n-1) catch [\"caught\",.] end; caught(2)",
            "null",
            ["0", "[\"caught\",\"done\"]"]
        },
        {
            "def bound($n): if $n==0 then 0 else (($n-1) as $m | bound($m)) end; bound(4)",
            "null",
            ["0"]
        },
        {
            "def f: if true then . else empty end; f",
            "{\"a\":1}",
            ["{\"a\":1}"]
        },
        {
            "def f: 1 as $x | .; f",
            "{\"a\":1}",
            ["{\"a\":1}"]
        },
        {
            "reduce (1,2) as $x (0,10; . + ($x, -$x))",
            "null",
            ["-3", "7"]
        },
        {
            "foreach (1,2) as $x (0,10; . + ($x, -$x); [$x,.])",
            "null",
            ["[1,1]", "[1,-1]", "[2,1]", "[2,-3]", "[1,11]", "[1,9]", "[2,11]", "[2,7]"]
        },
        {
            "[1,2] | (.[0],.[1]) += (10,20)",
            "null",
            ["[11,12]", "[21,22]"]
        },
        {
            "{} | (.a,.b) //= (1,2)",
            "null",
            ["{\"a\":1,\"b\":1}", "{\"a\":2,\"b\":2}"]
        },
        {
            "[1,2] | (.[0],.[1]) |= (., .+10)",
            "null",
            ["[1,2]"]
        },
        {
            "[1,2] | (.[0],.[1]) |= empty",
            "null",
            ["[]"]
        },
        {
            "try ({a:1} as {$a} | ($a,error(\"x\"))) catch [\"e\",.]",
            "null",
            ["1", "[\"e\",\"x\"]"]
        },
        {
            "[. as [$a] ?// [$b] | ($a, if $a != null then error(\"x\") else $b end)]",
            "[3]",
            ["[3,null,3]"]
        },
        {
            "[. as [$a] ?// [$b] | (try ($a,error(\"x\")) catch [$a,$b,.])]",
            "[3]",
            ["[3,[3,null,\"x\"]]"]
        },
        {
            "try (. as [$a] ?// [$b] | ($a,error(\"x\"))) catch [\"outer\",.]",
            "[3]",
            ["3", "null", "[\"outer\",\"x\"]"]
        },
        {
            "reduce (1,2) as $x (0; (. + $x) as $n | ($n,-$n))",
            "null",
            ["-1"]
        },
        {
            "foreach (1,2) as $x (0; (. + $x) as $n | ($n,-$n); [$x,.])",
            "null",
            ["[1,1]", "[1,-1]", "[2,1]", "[2,-1]"]
        },
        {
            "try foreach (1,2) as $x (0; (. + $x,error(\"u\")); [$x,.]) " +
            "catch [\"e\",.]",
            "null",
            ["[1,1]", "[\"e\",\"u\"]"]
        },
        {
            "label $out | (1,2,break $out,3)",
            "null",
            ["1", "2"]
        },
        {
            "[(false,null)//(1,2), ((0,false)//9)]",
            "null",
            ["[1,2,0]"]
        },
        {
            "def pair(f): [f,f]; pair((1,2))",
            "null",
            ["[1,2,1,2]"]
        },
        {
            "def args($x;$y): [$x,$y]; args((1,2);(3,4))",
            "null",
            ["[1,3]", "[1,4]", "[2,3]", "[2,4]"]
        },
        {
            "limit(3; repeat(.))",
            "null",
            ["null", "null", "null"]
        },
        {
            "try reduce (1,error(\"src\")) as $x (0; .+$x) catch .",
            "null",
            ["\"src\""]
        },
        {
            "try foreach (1,error(\"src\")) as $x (0; .+$x) catch .",
            "null",
            ["1", "\"src\""]
        },
        {
            "if (true,false,true) then 1,2 else 3,4 end",
            "null",
            ["1", "2", "3", "4", "1", "2"]
        },
        {
            "try ((false,null,error(\"x\")) // (1,2)) catch [\"c\",.]",
            "null",
            ["[\"c\",\"x\"]"]
        },
        {
            "try ((0,error(\"x\")) // (1,2)) catch [\"c\",.]",
            "null",
            ["0", "[\"c\",\"x\"]"]
        },
        {
            "[{a:0,b:10} | (.a,.b) += (1,2)]",
            "null",
            ["[{\"a\":1,\"b\":11},{\"a\":2,\"b\":12}]"]
        },
        {
            "[{a:0,b:10} | (.a,.b) = (1,2)]",
            "null",
            ["[{\"a\":1,\"b\":1},{\"a\":2,\"b\":2}]"]
        },
        {
            "[{\"a\":1,\"b\":2}] | reduce .[] as {(\"a\",\"b\"):$x} (0; .+$x)",
            "null",
            ["3"]
        },
        {
            "[{\"a\":1,\"b\":2}] | " +
            "foreach .[] as {(\"a\",\"b\"):$x} (0; .+$x; [$x,.])",
            "null",
            ["[1,1]", "[2,3]"]
        },
    };

    [Theory]
    [MemberData(nameof(ContinuationCases))]
    public async Task ManagedContinuationsMatchPinnedJq182(
        string filter,
        string input,
        string[] expected)
    {
        if (JqOracle.TryResolveExecutable(out _))
        {
            var oracle = await JqOracle.ExecuteAsync(
                filter,
                input,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(oracle.Succeeded, oracle.StandardError);
            Assert.Equal(expected, oracle.OutputLines);
        }

        var managed = JqProgram.Compile(filter)
            .Execute(input)
            .Select(value => value.GetRawText())
            .ToArray();
        Assert.Equal(expected, managed);
    }

    [Fact]
    public void ReduceEvaluatesEachInitialBranchBeforeItsSourceContinuation()
    {
        Assert.Equal(
            ["\"init\""],
            Execute("try reduce (1,error(\"src\")) as $x ((error(\"init\")); .) catch ."));
    }

    [Fact]
    public void PlainAssignmentEvaluatesItsValueBeforeOpeningThePathContinuation()
    {
        Assert.Equal(
            ["\"rhs\""],
            Execute("try ((error(\"path\") | .a) = error(\"rhs\")) catch .", "{}"));
        Assert.Empty(Execute("((error(\"path\") | .a) = empty)", "{}"));
        Assert.Equal(
            ["\"rhs\""],
            Execute("try ((error(\"path\") | .a) += error(\"rhs\")) catch .", "{}"));
        Assert.Empty(Execute("((error(\"path\") | .a) += empty)", "{}"));
    }

    [Fact]
    public void ErrorAfterOutputIsReportedOnlyAfterTheEarlierValueWasPulled()
    {
        using var execution = JqProgram.Compile("try (1,error(\"x\"),2) catch [\"caught\",.]")
            .StartExecution("null");

        Assert.True(execution.TryRead(out var first));
        Assert.Equal("1", first.GetRawText());
        Assert.True(execution.TryRead(out var caught));
        Assert.Equal("[\"caught\",\"x\"]", caught.GetRawText());
        Assert.False(execution.TryRead(out _));
        Assert.Equal(JqExecutionOutcomeKind.Completed, execution.Outcome?.Kind);
    }

    [Fact]
    public void EarlyDisposalDoesNotAdvanceDeferredInputOrDebugContinuations()
    {
        var input = new CountingInputSource(1, 2, 3);
        var debug = new CountingSink();
        var execution = JqProgram.Compile("inputs | debug")
            .StartExecution(
                "null",
                capabilities: new JqExecutionCapabilities
                {
                    Input = input,
                    Debug = debug,
                });

        Assert.True(execution.TryRead(out var first));
        Assert.Equal("1", first.GetRawText());
        Assert.Equal(1, input.ReadCount);
        Assert.Equal(["1"], debug.Values);

        Assert.True(execution.TryRead(out var second));
        Assert.Equal("2", second.GetRawText());
        Assert.Equal(2, input.ReadCount);
        Assert.Equal(["1", "2"], debug.Values);

        execution.Dispose();

        Assert.False(execution.TryRead(out _));
        Assert.Null(execution.Outcome);
        Assert.Equal(2, input.ReadCount);
        Assert.Equal(["1", "2"], debug.Values);
    }

    [Fact]
    public void CanonicalRepeatTailContinuationDoesNotAccumulateAcrossInputs()
    {
        const int eventCount = 700;
        const string filter =
            "reduce (., inputs) as $e (0; " +
            "if (($e|length)==2 and ($e[1]|type)==\"number\") " +
            "then . + $e[1] else . end)";
        var input = new StreamEventInputSource(start: 1, count: eventCount);

        var execution = JqProgram.Compile(filter).ExecuteDetailed(
            StreamEventJson(0),
            capabilities: new JqExecutionCapabilities { Input = input });
        var output = Assert.Single(execution.Outputs);

        Assert.Equal((eventCount - 1) * eventCount / 2, output.GetInt32());
        Assert.Equal(eventCount, input.ReadCount);
        Assert.Equal(JqExecutionOutcomeKind.Completed, execution.Outcome.Kind);
    }

    [Fact]
    public void UserRepeatDefinitionIsNotReplacedByTheCanonicalAccelerator()
    {
        Assert.Equal(
            ["\"shadowed\""],
            Execute(
                "def repeat(f): \"shadowed\"; " +
                "repeat(error(\"the ignored filter must stay lazy\"))"));
    }

    [Fact]
    public void CrossFunctionTailCallsAndFilterParameterSelfCallsDoNotGrowTheClrStack()
    {
        Assert.Equal(
            ["0"],
            Execute(
                "def f($n): if $n==0 then 0 else " +
                "(def g($m): f($m); g($n-1)) end; f(20000)"));
        Assert.Equal(
            ["null", "1"],
            Execute(
                "def invoke(f; $n): if $n==0 then f else invoke(f; $n-1) end; " +
                "invoke((., .+1); 20000)"));
        Assert.Equal(
            ["0"],
            Execute(
                "def bound($n): if $n==0 then 0 else " +
                "(($n-1) as $next | bound($next)) end; bound(20000)"));
    }

    [Theory]
    [InlineData("def f($n): if $n==0 then 0 else f($n-1),$n end; f(20)")]
    [InlineData("def f($n): if $n==0 then empty else f($n-1)//$n end; f(20)")]
    [InlineData("def f($n): if $n==0 then 0 else try f($n-1) catch error end; f(20)")]
    [InlineData("def f($n): if $n==0 then 0 else (1,2)|f($n-1) end; f(20)")]
    [InlineData("def f($n): if $n==0 then 0 else reduce 1 as $x (f($n-1); .) end; f(20)")]
    [InlineData("def f($n): if $n==0 then 0 else foreach 1 as $x (f($n-1); .) end; f(20)")]
    [InlineData("def f($n): if $n==0 then 0 else ({a:$n}|.a |= f(.-1)) end; f(20)")]
    [InlineData("def f($n): if $n==0 then 0 else (($n-1),($n-2)) as $m | f($m) end; f(20)")]
    public void SavedContinuationRecursionUsesTheDeterministicManagedDepthGuard(string filter)
    {
        var error = Assert.Throws<JqRuntimeException>(() =>
            JqProgram.Compile(filter).Execute(
                "null",
                new JqExecutionOptions
                {
                    MaxRecursionDepth = 8,
                    MaxExecutionTransitions = 100_000,
                }));

        Assert.Equal("jq recursion depth limit exceeded", error.Message);
    }

    private static string[] Execute(string filter, string input = "null") =>
        JqProgram.Compile(filter).Execute(input).Select(value => value.GetRawText()).ToArray();

    private static string StreamEventJson(int value) =>
        $"[[\"values\",{value.ToString(CultureInfo.InvariantCulture)}]," +
        $"{value.ToString(CultureInfo.InvariantCulture)}]";

    private sealed class CountingInputSource(params int[] values) : IJqInputSource
    {
        private int index;

        internal int ReadCount { get; private set; }

        public JqInputReadResult ReadNext(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            if (index >= values.Length)
            {
                return JqInputReadResult.End;
            }

            using var document = JsonDocument.Parse(
                values[index++].ToString(CultureInfo.InvariantCulture));
            return JqInputReadResult.FromValue(document.RootElement);
        }
    }

    private sealed class CountingSink : IJqValueSink
    {
        internal List<string> Values { get; } = [];

        public void Write(JsonElement value) => Values.Add(value.GetRawText());
    }

    private sealed class StreamEventInputSource(int start, int count) : IJqInputSource
    {
        private int next = start;

        internal int ReadCount { get; private set; }

        public JqInputReadResult ReadNext(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            if (next >= count)
            {
                return JqInputReadResult.End;
            }

            using var document = JsonDocument.Parse(StreamEventJson(next++));
            return JqInputReadResult.FromValue(document.RootElement);
        }
    }
}
