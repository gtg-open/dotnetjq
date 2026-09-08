using System.Text.Json;
using DotNetJq.Port;

namespace DotNetJq.Tests;

/// <summary>Compatibility coverage for jq 1.8.2's non-catchable halt control path.</summary>
public sealed class HaltCompatibilityTests
{
    [Theory]
    [InlineData("try halt catch \"caught\"")]
    [InlineData("halt?")]
    [InlineData("halt // \"fallback\"")]
    [InlineData("[0,1][] | if . == 0 then halt else . end")]
    public void HaltBypassesCatchOptionalAlternativeAndBacktracking(string filter)
    {
        var result = JqProgram.Compile(filter).ExecuteDetailed("null");

        Assert.Empty(result.Outputs);
        AssertHalted(result.Outcome);
        Assert.Null(result.Outcome.RequestedExitCode);
        Assert.Null(result.Outcome.HaltMessage);
    }

    [Fact]
    public void HaltErrorBypassesCatch()
    {
        var result = JqProgram
            .Compile("try (\"oops\" | halt_error(7)) catch \"caught\"")
            .ExecuteDetailed("null");

        Assert.Empty(result.Outputs);
        AssertHalted(result.Outcome);
        Assert.Equal(7, AssertPresent(result.Outcome.RequestedExitCode).GetInt32());
        Assert.Equal("oops", AssertPresent(result.Outcome.HaltMessage).GetString());
    }

    [Fact]
    public void PullExecutionPreservesPriorOutputAndStopsEveryLaterBranch()
    {
        using var execution = JqProgram
            .Compile("\"before\", halt, \"after\"")
            .StartExecution("null");

        Assert.Null(execution.Outcome);
        Assert.True(execution.TryRead(out var before));
        Assert.Equal("before", before.GetString());
        Assert.Null(execution.Outcome);

        Assert.False(execution.TryRead(out _));
        var outcome = Assert.IsType<JqExecutionOutcome>(execution.Outcome);
        AssertHalted(outcome);
        Assert.Null(outcome.RequestedExitCode);
        Assert.Null(outcome.HaltMessage);

        Assert.False(execution.TryRead(out _));
        Assert.Same(outcome, execution.Outcome);
    }

    [Fact]
    public void BareHaltAndHaltErrorZeroHaveDistinctTerminalState()
    {
        var bare = JqProgram.Compile("halt").ExecuteDetailed("null");
        var explicitZero = JqProgram
            .Compile("null | halt_error(0)")
            .ExecuteDetailed("null");

        AssertHalted(bare.Outcome);
        Assert.Null(bare.Outcome.RequestedExitCode);
        Assert.Null(bare.Outcome.HaltMessage);

        AssertHalted(explicitZero.Outcome);
        Assert.Equal(0, AssertPresent(explicitZero.Outcome.RequestedExitCode).GetInt32());
        Assert.Equal(
            JsonValueKind.Null,
            AssertPresent(explicitZero.Outcome.HaltMessage).ValueKind);
    }

    [Fact]
    public void HaltErrorWithoutExplicitCodeUsesFive()
    {
        var result = JqProgram
            .Compile("{message:\"default\"} | halt_error")
            .ExecuteDetailed("null");

        AssertHalted(result.Outcome);
        Assert.Equal(5, AssertPresent(result.Outcome.RequestedExitCode).GetInt32());
        Assert.Equal(
            "default",
            AssertPresent(result.Outcome.HaltMessage).GetProperty("message").GetString());
    }

    [Fact]
    public void HaltErrorAcceptsNonIntegralJqNumbersWithoutProjectingToProcessStatus()
    {
        var result = JqProgram
            .Compile("\"fractional\" | halt_error(2.5)")
            .ExecuteDetailed("null");

        AssertHalted(result.Outcome);
        Assert.Equal(2.5, AssertPresent(result.Outcome.RequestedExitCode).GetDouble());
        Assert.Equal("fractional", AssertPresent(result.Outcome.HaltMessage).GetString());
    }

    [Fact]
    public void InvalidHaltErrorCodeIsCatchableAndDoesNotHalt()
    {
        var result = JqProgram
            .Compile("try (0 | halt_error(\"x\")) catch .")
            .ExecuteDetailed("null");

        Assert.Equal(
            "number (0) halt_error/1: number required",
            Assert.Single(result.Outputs).GetString());
        Assert.Equal(JqExecutionOutcomeKind.Completed, result.Outcome.Kind);
        Assert.Null(result.Outcome.RequestedExitCode);
        Assert.Null(result.Outcome.HaltMessage);
        Assert.Null(result.Outcome.RuntimeError);
    }

    [Fact]
    public void InvalidHaltErrorCodeCanBeSuppressedByOptionalOperator()
    {
        var result = JqProgram
            .Compile("(0 | halt_error(\"x\"))?")
            .ExecuteDetailed("null");

        Assert.Empty(result.Outputs);
        Assert.Equal(JqExecutionOutcomeKind.Completed, result.Outcome.Kind);
    }

    [Fact]
    public void UncaughtInvalidHaltErrorCodeIsRuntimeErrorRatherThanHalt()
    {
        var result = JqProgram
            .Compile("0 | halt_error(\"x\")")
            .ExecuteDetailed("null");

        Assert.Empty(result.Outputs);
        Assert.Equal(JqExecutionOutcomeKind.RuntimeError, result.Outcome.Kind);
        Assert.Equal(
            "number (0) halt_error/1: number required",
            Assert.IsType<JqRuntimeException>(result.Outcome.RuntimeError).Message);
        Assert.Null(result.Outcome.RequestedExitCode);
        Assert.Null(result.Outcome.HaltMessage);
    }

    [Fact]
    public void ReusedProgramExecutionsHaveIndependentHaltState()
    {
        var program = JqProgram.Compile("if . == 0 then halt else . end");
        using var halted = program.StartExecution("0");

        Assert.False(halted.TryRead(out _));
        AssertHalted(Assert.IsType<JqExecutionOutcome>(halted.Outcome));

        using var completed = program.StartExecution("1");
        Assert.True(completed.TryRead(out var one));
        Assert.Equal(1, one.GetInt32());
        Assert.False(completed.TryRead(out _));
        Assert.Equal(
            JqExecutionOutcomeKind.Completed,
            Assert.IsType<JqExecutionOutcome>(completed.Outcome).Kind);

        using var restarted = program.StartExecution("0");
        Assert.Null(restarted.Outcome);
        Assert.False(restarted.TryRead(out _));
        AssertHalted(Assert.IsType<JqExecutionOutcome>(restarted.Outcome));
    }

    [Fact]
    public void StartingSameJqStateClearsPreviousHaltCodeAndMessage()
    {
        jq_state? state = libjq.jq_init();
        try
        {
            Assert.Equal(1, libjq.jq_compile(state, "\"message\" | halt_error(3)"));

            libjq.jq_start(state, libjq.jv_null(), 0);
            Assert.False(libjq.jv_is_valid(libjq.jq_next(state)));
            Assert.NotEqual(0, libjq.jq_halted(state));
            Assert.True(libjq.jv_is_valid(libjq.jq_get_exit_code(state)));
            Assert.True(libjq.jv_is_valid(libjq.jq_get_error_message(state)));

            Assert.False(libjq.jv_is_valid(libjq.jq_next(state)));
            Assert.NotEqual(0, libjq.jq_halted(state));
            Assert.True(libjq.jv_is_valid(libjq.jq_get_exit_code(state)));
            Assert.True(libjq.jv_is_valid(libjq.jq_get_error_message(state)));

            libjq.jq_start(state, libjq.jv_null(), 0);

            Assert.Equal(0, libjq.jq_halted(state));
            Assert.False(libjq.jv_is_valid(libjq.jq_get_exit_code(state)));
            Assert.False(libjq.jv_is_valid(libjq.jq_get_error_message(state)));

            Assert.False(libjq.jv_is_valid(libjq.jq_next(state)));
            Assert.NotEqual(0, libjq.jq_halted(state));
        }
        finally
        {
            libjq.jq_teardown(ref state);
        }
    }

    [Fact]
    public void LegacyExecuteReturnsPriorOutputsForBareHalt()
    {
        var outputs = JqProgram
            .Compile("1, halt, 2")
            .Execute("null");

        Assert.Equal(1, Assert.Single(outputs).GetInt32());
    }

    [Fact]
    public void LegacyExecuteThrowsHaltExceptionWithPartialOutputsAndRawState()
    {
        var exception = Assert.Throws<JqHaltException>(
            () => JqProgram
                .Compile("1, (\"oops\" | halt_error(7)), 2")
                .Execute("null"));

        Assert.Equal(7, exception.RequestedExitCode.GetInt32());
        Assert.Equal("oops", AssertPresent(exception.HaltMessage).GetString());
        Assert.Equal(1, Assert.Single(exception.PartialOutputs).GetInt32());
    }

    [Fact]
    public void LegacyExecuteDoesNotCollapseExplicitZeroAndNullMessageIntoBareHalt()
    {
        var exception = Assert.Throws<JqHaltException>(
            () => JqProgram
                .Compile("null | halt_error(0)")
                .Execute("null"));

        Assert.Equal(0, exception.RequestedExitCode.GetInt32());
        Assert.Equal(JsonValueKind.Null, AssertPresent(exception.HaltMessage).ValueKind);
        Assert.Empty(exception.PartialOutputs);
    }

    private static void AssertHalted(JqExecutionOutcome outcome)
    {
        Assert.Equal(JqExecutionOutcomeKind.Halted, outcome.Kind);
        Assert.Null(outcome.RuntimeError);
    }

    private static JsonElement AssertPresent(JsonElement? value)
    {
        Assert.True(value.HasValue);
        return value.GetValueOrDefault();
    }
}
