namespace DotNetJq.Tests;

/// <summary>jq postfix-question scope regressions for INDEX_OPT and EACH_OPT lowering.</summary>
public sealed class OptionalOperatorScopeCompatibilityTests
{
    [Fact]
    public void OptionalIndexSuppressesOnlyTheTerminalIndexError()
    {
        Assert.Empty(JqProgram.Compile(".foo?").Execute("1"));
        Assert.Equal(1, Assert.Single(JqProgram.Compile(".[0]?").Execute("[1]")).GetInt32());
        Assert.Empty(JqProgram.Compile(".[0]?").Execute("1"));
    }

    [Fact]
    public void OptionalIteratorDoesNotSuppressItsTargetFailure()
    {
        var direct = Assert.Throws<JqRuntimeException>(
            () => JqProgram.Compile(".foo[]?").Execute("1"));
        var chained = Assert.Throws<JqRuntimeException>(
            () => JqProgram.Compile(".foo.bar?").Execute("1"));

        Assert.Contains("Cannot index number", direct.Message, StringComparison.Ordinal);
        Assert.Contains("Cannot index number", chained.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OuterTryCanCatchTargetFailureWhileOptionalIteratorStillHandlesScalarIteration()
    {
        var caught = Assert.Single(
            JqProgram.Compile("try .foo[]? catch \"caught\"").Execute("1"));
        var optionalIteration = JqProgram.Compile(".[]?").Execute("1");

        Assert.Equal("caught", caught.GetString());
        Assert.Empty(optionalIteration);
    }
}
