using System.Runtime.CompilerServices;

namespace DotNetJq.Tests;

public sealed class JqProgramConcurrencyContractTests
{
    [Fact]
    public void ActivePullExecutionRejectsOverlapAndReleasesProgramForSequentialReuse()
    {
        using var program = JqProgram.Compile("range(0; 2)");
        using var first = program.StartExecution("null");

        Assert.True(first.TryRead(out var firstValue));
        Assert.Equal(0, firstValue.GetInt32());

        var exception = Assert.Throws<InvalidOperationException>(
            () => program.StartExecution("null"));
        Assert.Contains("active execution", exception.Message, StringComparison.Ordinal);

        first.Dispose();

        using var second = program.StartExecution("null");
        Assert.True(second.TryRead(out var secondValue));
        Assert.Equal(0, secondValue.GetInt32());
        Assert.True(second.TryRead(out var finalValue));
        Assert.Equal(1, finalValue.GetInt32());
        Assert.False(second.TryRead(out _));

        using var third = program.StartExecution("null");
        Assert.True(third.TryRead(out var reusedValue));
        Assert.Equal(0, reusedValue.GetInt32());
    }

    [Fact]
    public async Task IndependentlyCompiledProgramsCanRemainActiveConcurrentlyAsync()
    {
        const int programCount = 8;
        var programs = Enumerable.Range(0, programCount)
            .Select(value => JqProgram.Compile($". + {value}"))
            .ToArray();
        using var allStarted = new Barrier(programCount);

        try
        {
            var tasks = programs.Select((program, value) => Task.Run(() =>
            {
                using var execution = program.StartExecution("10");
                allStarted.SignalAndWait();
                Assert.True(execution.TryRead(out var output));
                Assert.False(execution.TryRead(out _));
                return (value, output: output.GetInt32());
            })).ToArray();

            var results = await Task.WhenAll(tasks);

            Assert.All(results, result => Assert.Equal(10 + result.value, result.output));
        }
        finally
        {
            foreach (var program in programs)
            {
                program.Dispose();
            }
        }
    }

    [Fact]
    public void DisposeDuringExecutionIsDeferredUntilTheExecutionCompletes()
    {
        var program = JqProgram.Compile("1, 2");
        using var execution = program.StartExecution("null");

        program.Dispose();

        Assert.Throws<ObjectDisposedException>(() => program.StartExecution("null"));
        Assert.True(execution.TryRead(out var first));
        Assert.Equal(1, first.GetInt32());
        Assert.True(execution.TryRead(out var second));
        Assert.Equal(2, second.GetInt32());
        Assert.False(execution.TryRead(out _));
        Assert.Throws<ObjectDisposedException>(() => program.StartExecution("null"));
    }

    [Fact]
    public void RuntimeErrorCompletionReleasesProgramForSequentialReuse()
    {
        using var program = JqProgram.Compile("if . == 0 then error(\"boom\") else . end");

        var failed = program.ExecuteDetailed("0");
        var succeeded = program.ExecuteDetailed("1");

        Assert.Equal(JqExecutionOutcomeKind.RuntimeError, failed.Outcome.Kind);
        Assert.Equal("1", Assert.Single(succeeded.Outputs).GetRawText());
        Assert.Equal(JqExecutionOutcomeKind.Completed, succeeded.Outcome.Kind);
    }

    [Theory]
    [InlineData("error(\"boom\")", "null", "boom")]
    [InlineData(
        "1 / 0",
        "null",
        "number (1) and number (0) cannot be divided because the divisor is zero")]
    [InlineData(".[\"x\"]", "1", "Cannot index number with string (\"x\")")]
    public void InvalidWithMessageFromJqNextBecomesRuntimeError(
        string filter,
        string input,
        string expectedMessage)
    {
        using var program = JqProgram.Compile(filter);

        var result = program.ExecuteDetailed(input);

        Assert.Empty(result.Outputs);
        Assert.Equal(JqExecutionOutcomeKind.RuntimeError, result.Outcome.Kind);
        Assert.Equal(expectedMessage, result.Outcome.RuntimeError?.Message);
        Assert.Null(result.Outcome.RuntimeError?.ErrorValue);
    }

    [Fact]
    public void CaughtErrorAndEmptyRemainCompletedResults()
    {
        using var caughtProgram = JqProgram.Compile("try error(\"boom\") catch .");
        using var emptyProgram = JqProgram.Compile("empty");

        var caught = caughtProgram.ExecuteDetailed("null");
        var empty = emptyProgram.ExecuteDetailed("null");

        Assert.Equal("\"boom\"", Assert.Single(caught.Outputs).GetRawText());
        Assert.Equal(JqExecutionOutcomeKind.Completed, caught.Outcome.Kind);
        Assert.Empty(empty.Outputs);
        Assert.Equal(JqExecutionOutcomeKind.Completed, empty.Outcome.Kind);
    }

    [Fact]
    public void EphemeralProgramRemainsAliveForItsPullExecution()
    {
        using var execution = StartEphemeralExecution();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.True(execution.TryRead(out var first));
        Assert.Equal(1, first.GetInt32());
        Assert.True(execution.TryRead(out var second));
        Assert.Equal(2, second.GetInt32());
        Assert.False(execution.TryRead(out _));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static JqExecution StartEphemeralExecution() =>
        JqProgram.Compile("1, 2").StartExecution("null");
}
