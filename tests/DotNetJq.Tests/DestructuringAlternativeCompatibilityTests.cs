namespace DotNetJq.Tests;

public sealed class DestructuringAlternativeCompatibilityTests
{
    [Fact]
    public void ManCase204RetriesWithoutLeakingTheFailedAlternativeBinding()
    {
        var output = Execute(
            ".[] as [$a] ?// [$b] | " +
            "if $a != null then error(\"err: \\($a)\") else {$a,$b} end",
            "[[3]]");

        Assert.Equal(["{\"a\":null,\"b\":3}"], output);
    }

    [Fact]
    public void OutputsBeforeAnAlternativeErrorRemainBeforeFallbackOutputs()
    {
        var output = Execute(
            "[. as [$a] ?// [$b] | " +
            "($a, if $a != null then error(\"x\") else $b end)]",
            "[3]");

        Assert.Equal(["[3,null,3]"], output);
    }

    [Fact]
    public void EmptySuccessfulBodyDoesNotTryTheNextAlternative()
    {
        var output = Execute(
            "[. as [$a] ?// [$b] | if $a != null then empty else $b end]",
            "[3]");

        Assert.Equal(["[]"], output);
    }

    [Fact]
    public void ShapeFailureRetriesWithFreshNullInitializedBindings()
    {
        var output = Execute(
            ". as {$a, c: {$d}} ?// {$a, c: [{$e}]} | {$a,$d,$e}",
            "{\"a\":1,\"c\":[{\"e\":4}]}");

        Assert.Equal(["{\"a\":1,\"d\":null,\"e\":4}"], output);
    }

    private static string[] Execute(string filter, string input) =>
        JqProgram.Compile(filter).Execute(input).Select(value => value.GetRawText()).ToArray();
}
