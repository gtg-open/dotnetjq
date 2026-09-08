using System.Text.Json;

namespace DotNetJq.Tests;

// These tests freeze MaxExecutionTransitions' direct-VM unit: one charge
// immediately before every forward or ON_BACKTRACK opcode dispatch, cumulative
// until the execution terminates.
public sealed class ExecutionTransitionBudgetContractTests
{
    private const string LimitMessage = "jq execution-transition limit exceeded";

    [Fact]
    public void IdentityFreezesZeroOneOutputAndTerminalTransitionTiming()
    {
        using var program = JqProgram.Compile(".");

        AssertLimitBeforeOutput(program, budget: 0);
        AssertLimitBeforeOutput(program, budget: 1);

        using (var execution = program.StartExecution(
                   "null",
                   new JqExecutionOptions { MaxExecutionTransitions = 2 }))
        {
            Assert.True(execution.TryRead(out var value));
            Assert.Equal(JsonValueKind.Null, value.ValueKind);
            Assert.False(execution.TryRead(out _));
            Assert.Equal(JqExecutionOutcomeKind.RuntimeError, execution.Outcome?.Kind);
            Assert.Equal(LimitMessage, execution.Outcome?.RuntimeError?.Message);
        }

        using (var execution = program.StartExecution(
                   "null",
                   new JqExecutionOptions { MaxExecutionTransitions = 3 }))
        {
            Assert.True(execution.TryRead(out var value));
            Assert.Equal(JsonValueKind.Null, value.ValueKind);
            Assert.False(execution.TryRead(out _));
            Assert.Equal(JqExecutionOutcomeKind.Completed, execution.Outcome?.Kind);
        }
    }

    [Theory]
    [InlineData("1,2", "null", 10, "1", "2")]
    [InlineData(".[]", "[1,2]", 7, "1", "2")]
    [InlineData("range(2)", "null", 33, "0", "1")]
    public void BacktrackingAndBuiltinStreamsHaveExactTerminalBudgets(
        string filter,
        string input,
        long exactBudget,
        params string[] expected)
    {
        using var program = JqProgram.Compile(filter);

        AssertLimit(program, input, exactBudget - 1);
        var output = program.Execute(
            input,
            new JqExecutionOptions { MaxExecutionTransitions = exactBudget });

        Assert.Equal(expected, output.Select(value => value.GetRawText()));
    }

    [Fact]
    public void TailCallHasAnExactTerminalBudget()
    {
        const string filter =
            "def f($n): if $n == 0 then . else f($n-1) end; f(1)";
        using var program = JqProgram.Compile(filter);

        AssertLimit(program, "null", budget: 49);
        var output = Assert.Single(program.Execute(
            "null",
            new JqExecutionOptions { MaxExecutionTransitions = 50 }));

        Assert.Equal(JsonValueKind.Null, output.ValueKind);
    }

    [Fact]
    public void RuntimeErrorIsObservedOnlyAfterItsExactDispatchBudget()
    {
        using var program = JqProgram.Compile("error(\"boom\")");

        AssertLimit(program, "null", budget: 5);
        var error = Assert.Throws<JqRuntimeException>(() => program.Execute(
            "null",
            new JqExecutionOptions { MaxExecutionTransitions = 6 }));

        Assert.Equal("boom", error.Message);
    }

    [Fact]
    public void CancellationWinsBeforeFirstDispatchAndIsObservedAtTheNextDispatch()
    {
        using var program = JqProgram.Compile(".");
        using var initiallyCanceled = new CancellationTokenSource();
        initiallyCanceled.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => program.StartExecution(
            "null",
            new JqExecutionOptions
            {
                MaxExecutionTransitions = 0,
                CancellationToken = initiallyCanceled.Token,
            }));

        using var canceledBetweenPulls = new CancellationTokenSource();
        using var execution = program.StartExecution(
            "null",
            new JqExecutionOptions
            {
                MaxExecutionTransitions = 3,
                CancellationToken = canceledBetweenPulls.Token,
            });
        Assert.True(execution.TryRead(out _));
        canceledBetweenPulls.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => execution.TryRead(out _));
    }

    [Fact]
    public void SequentialProgramReuseResetsTheTransitionCounter()
    {
        using var program = JqProgram.Compile(".");

        AssertLimit(program, "null", budget: 2);
        for (var execution = 0; execution < 2; execution++)
        {
            var output = Assert.Single(program.Execute(
                "null",
                new JqExecutionOptions { MaxExecutionTransitions = 3 }));
            Assert.Equal(JsonValueKind.Null, output.ValueKind);
        }
    }

    private static void AssertLimitBeforeOutput(JqProgram program, long budget)
    {
        using var execution = program.StartExecution(
            "null",
            new JqExecutionOptions { MaxExecutionTransitions = budget });

        Assert.False(execution.TryRead(out _));
        Assert.Equal(JqExecutionOutcomeKind.RuntimeError, execution.Outcome?.Kind);
        Assert.Equal(LimitMessage, execution.Outcome?.RuntimeError?.Message);
    }

    private static void AssertLimit(JqProgram program, string input, long budget)
    {
        var error = Assert.Throws<JqRuntimeException>(() => program.Execute(
            input,
            new JqExecutionOptions { MaxExecutionTransitions = budget }));
        Assert.Equal(LimitMessage, error.Message);
    }
}
