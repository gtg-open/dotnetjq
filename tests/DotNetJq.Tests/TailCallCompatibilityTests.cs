namespace DotNetJq.Tests;

public sealed class TailCallCompatibilityTests
{
    [Fact]
    public void ValueParameterSelfTailCallDoesNotGrowTheManagedStack()
    {
        var output = Assert.Single(JqProgram.Compile(
                "def countdown($n): " +
                "if $n == 0 then $n else countdown($n - 1) end; " +
                "countdown(20000)")
            .Execute("null"));

        Assert.Equal("0", output.GetRawText());
    }

    [Fact]
    public void SingleValuedPipeIntoSelfTailCallIsTrampolined()
    {
        var output = Assert.Single(JqProgram.Compile(
                "def countdown: " +
                "if . == 0 then . else (. - 1) | countdown end; " +
                "20000 | countdown")
            .Execute("null"));

        Assert.Equal("0", output.GetRawText());
    }

    [Fact]
    public void ExplicitRecursionLimitStillBoundsTailCalls()
    {
        var error = Assert.Throws<JqRuntimeException>(() =>
            JqProgram.Compile("def loop: loop; loop").Execute(
                "null",
                new JqExecutionOptions
                {
                    MaxRecursionDepth = 8,
                    MaxExecutionTransitions = 1_000,
                }));

        Assert.Equal("jq recursion depth limit exceeded", error.Message);
    }

    [Fact]
    public void NonTerminatingDefaultTailCallRemainsCooperativelyBounded()
    {
        var error = Assert.Throws<JqRuntimeException>(() =>
            JqProgram.Compile("def loop: loop; loop").Execute(
                "null",
                new JqExecutionOptions { MaxExecutionTransitions = 8 }));

        Assert.Equal("jq execution-transition limit exceeded", error.Message);
    }

    [Fact]
    public void ValueArgumentBranchesKeepJqBacktrackingOrder()
    {
        var output = Assert.Single(JqProgram.Compile(
                "def descend($n): " +
                "if $n <= 0 then $n else descend(($n - 1), ($n - 2)) end; " +
                "[descend(3)]")
            .Execute("null"));

        Assert.Equal("[0,-1,0,0,-1]", output.GetRawText());
    }

    [Fact]
    public void FilterParameterSelfCallRetainsTheManagedRecursionGuard()
    {
        var error = Assert.Throws<JqRuntimeException>(() =>
            JqProgram.Compile(
                    "def invoke(f; $n): " +
                    "if $n == 0 then f else invoke(f; $n - 1) end; " +
                    "invoke(.; 20)")
                .Execute(
                    "null",
                    new JqExecutionOptions
                    {
                        MaxRecursionDepth = 8,
                        MaxExecutionTransitions = 1_000,
                    }));

        Assert.Equal("jq recursion depth limit exceeded", error.Message);
    }
}
