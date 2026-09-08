using System.Text.Json;
using DotNetJq.Port;

namespace DotNetJq.Tests;

public sealed class SecurityResourceCompatibilityTests
{
    [Fact]
    public void OrdinaryDefaultsDoNotEnableProjectSpecificExecutionLimits()
    {
        var options = JqExecutionOptions.Default;

        Assert.Equal(int.MaxValue, options.MaxRecursionDepth);
        Assert.Equal(long.MaxValue, options.MaxExecutionTransitions);
        Assert.Equal(Timeout.InfiniteTimeSpan, options.RegexTimeout);
        Assert.Null(options.Timeout);
        Assert.Null(options.MaxInputBytes);
        Assert.Null(options.MaxOutputBytes);
        Assert.Null(options.MaxOutputValues);
    }

    [Fact]
    public void ExecutionTransitionLimitStopsLargeRangeBeforeOutputExplosion()
    {
        var options = new JqExecutionOptions
        {
            MaxExecutionTransitions = 8,
            MaxOutputValues = 1_000,
        };

        var error = Assert.Throws<JqRuntimeException>(() =>
            JqProgram.Compile("range(1000000000)").Execute("null", options));

        Assert.Equal("jq execution-transition limit exceeded", error.Message);
    }

    [Fact]
    public void ZeroExecutionTimeoutStopsAtFirstCooperativeTick()
    {
        var options = new JqExecutionOptions
        {
            Timeout = TimeSpan.Zero,
            MaxExecutionTransitions = long.MaxValue,
        };

        var error = Assert.Throws<JqRuntimeException>(() =>
            JqProgram.Compile("range(1000000000)").Execute("null", options));

        Assert.Equal("jq execution timeout exceeded", error.Message);
    }

    [Fact]
    public void CancellationIsObservedDuringGeneratorConsumption()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(10));
        var options = new JqExecutionOptions
        {
            CancellationToken = cancellation.Token,
            MaxExecutionTransitions = long.MaxValue,
        };

        Assert.ThrowsAny<OperationCanceledException>(() =>
            JqProgram.Compile("last(range(1000000000))").Execute("null", options));
    }

    [Fact]
    public void UserFunctionRecursionUsesConfiguredDepthLimit()
    {
        var options = new JqExecutionOptions
        {
            MaxRecursionDepth = 8,
            MaxExecutionTransitions = 1_000,
        };

        var error = Assert.Throws<JqRuntimeException>(() =>
            JqProgram.Compile("def loop: loop; loop").Execute("null", options));

        Assert.Equal("jq recursion depth limit exceeded", error.Message);
    }

    [Fact]
    public void AggregateOutputByteLimitCountsTheWholeStream()
    {
        var options = new JqExecutionOptions { MaxOutputBytes = 1 };

        var error = Assert.Throws<JqRuntimeException>(() =>
            JqProgram.Compile("1,2").Execute("null", options));

        Assert.Equal("jq output-byte limit exceeded", error.Message);
    }

    [Fact]
    public void ZeroOutputValueLimitRejectsTheFirstResult()
    {
        var options = new JqExecutionOptions { MaxOutputValues = 0 };

        var error = Assert.Throws<JqRuntimeException>(() =>
            JqProgram.Compile(".").Execute("null", options));

        Assert.Equal("jq output-value limit exceeded", error.Message);
    }

    [Fact]
    public void GlobalZeroWidthRegexUsesOneCumulativeTimeoutBudget()
    {
        var input = JsonSerializer.Serialize(new string('a', 500_000));
        var options = new JqExecutionOptions
        {
            RegexTimeout = TimeSpan.FromMilliseconds(1),
            MaxExecutionTransitions = long.MaxValue,
        };

        var error = Assert.Throws<JqRuntimeException>(() =>
            JqProgram.Compile("match(\"\"; \"g\")").Execute(input, options));

        Assert.Contains("regular expression evaluation timed out", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CalloutRunnerTextAtomsUseTheConfiguredRegexDeadline()
    {
        var options = new JqExecutionOptions
        {
            RegexTimeout = TimeSpan.FromTicks(1),
            MaxExecutionTransitions = long.MaxValue,
        };

        var error = Assert.Throws<JqRuntimeException>(() =>
            JqProgram
                .Compile("test(\"\\\\X(*COUNT[T]{>})(*CMP{T,==,1})\")")
                .Execute("\"a\"", options));

        Assert.Equal(
            "Regex failure: regular expression evaluation timed out",
            error.Message);
    }

    [Fact]
    public void LargeParserNestingFailsWithBoundedMemoryDiagnostic()
    {
        var source = "\"\\(" + new string('(', 250_000);

        var error = Assert.Throws<JqCompileException>(() => JqProgram.Compile(source));

        // jq-1.8.2's Bison stack reports "memory exhausted" at column 9,997
        // for this shape. The managed shift/reduce parser preserves the same
        // bounded resource-error family without growing its stack indefinitely.
        Assert.Equal("jq: error: memory exhausted", error.Message);
        Assert.True(error.Message.Length < 200);
        Assert.DoesNotContain(new string('(', 1_000), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParserAcceptsTheNativeBalancedParenthesisBoundary()
    {
        var nesting = DotNetJq.Port.GeneratedParser.JqGeneratedParser
            .MaximumNestedParenthesisDepth;
        var source = new string('(', nesting) + "." + new string(')', nesting);

        var result = Assert.Single(JqProgram.Compile(source).Execute("null"));

        Assert.Equal(JsonValueKind.Null, result.ValueKind);
    }

    [Fact]
    public void ParserRejectsOneLevelBeyondTheNativeBalancedParenthesisBoundary()
    {
        var nesting = DotNetJq.Port.GeneratedParser.JqGeneratedParser
            .MaximumNestedParenthesisDepth + 1;
        var source = new string('(', nesting) + "." + new string(')', nesting);

        var error = Assert.Throws<JqCompileException>(() => JqProgram.Compile(source));

        Assert.Equal("jq: error: memory exhausted", error.Message);
    }

    [Fact]
    public void InvalidResourceOptionsAreRejectedBeforeExecution()
    {
        var program = JqProgram.Compile(".");

        Assert.Throws<ArgumentOutOfRangeException>(() => program.Execute(
            "null",
            new JqExecutionOptions { Timeout = TimeSpan.FromTicks(-1) }));
        var transitionError = Assert.Throws<ArgumentOutOfRangeException>(() => program.Execute(
            "null",
            new JqExecutionOptions { MaxExecutionTransitions = -1 }));
        Assert.StartsWith(
            "Maximum execution transitions cannot be negative.",
            transitionError.Message,
            StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => program.Execute(
            "null",
            new JqExecutionOptions { MaxRecursionDepth = -1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => program.Execute(
            "null",
            new JqExecutionOptions { RegexTimeout = TimeSpan.Zero }));

        var output = Assert.Single(program.Execute(
            "null",
            new JqExecutionOptions { RegexTimeout = Timeout.InfiniteTimeSpan }));
        Assert.Equal(JsonValueKind.Null, output.ValueKind);

        var regexOutput = Assert.Single(
            JqProgram.Compile("test(\"\\\\X(*COUNT[T]{>})(*CMP{T,==,1})\")")
                .Execute("\"a\"", new JqExecutionOptions
                {
                    RegexTimeout = Timeout.InfiniteTimeSpan,
                }));
        Assert.True(regexOutput.GetBoolean());
    }
}
