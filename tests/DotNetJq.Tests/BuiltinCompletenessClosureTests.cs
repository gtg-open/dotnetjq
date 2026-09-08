namespace DotNetJq.Tests;

public sealed class BuiltinCompletenessClosureTests
{
    [Fact]
    public void BuiltinsAdvertisesCallableAndNewPublicSignatures()
    {
        Assert.Equal(
            "true",
            ExecuteOne(
                "[\"del/1\",\"pick/1\",\"test/1\",\"test/2\",\"mktime/0\"," +
                "\"inputs/0\",\"debug/1\",\"stderr/0\",\"frexp/0\",\"modf/0\"," +
                "\"significand/0\"] as $required | " +
                "all($required[]; . as $name | builtins | index($name) != null)",
                "null"));
        Assert.Equal("true", ExecuteOne("builtins | index(\"_flatten/1\") == null", "null"));
    }

    [Fact]
    public void InputsIsEmptyWhenNoExplicitInputCallbackExists()
    {
        Assert.Equal("[]", ExecuteOne("[inputs]", "null"));
    }

    [Fact]
    public void DebugWithMessagesEvaluatesThemAndReturnsTheOriginalInputOnce()
    {
        Assert.Equal("1", ExecuteOne("debug(empty)", "1"));
        Assert.Equal("1", ExecuteOne("debug(2,3)", "1"));
        Assert.Equal("\"boom\"", ExecuteOne("try debug(error(\"boom\")) catch .", "1"));
        Assert.Equal("1", ExecuteOne("stderr", "1"));
    }

    [Fact]
    public void PrivateFlattenMatchesTheJqCodedDepthRules()
    {
        const string input = "[1,[2,[3,[4]]]]";
        Assert.Equal("[1,2,[3,[4]]]", ExecuteOne("_flatten(1)", input));
        Assert.Equal("[1,[2,[3,[4]]]]", ExecuteOne("_flatten(0)", input));
        Assert.Equal("[1,2,3,4]", ExecuteOne("_flatten(-1)", input));
        Assert.Equal("[1,2,3,4]", ExecuteOne("_flatten(1.5)", input));
        Assert.Equal("[1,2,3,4]", ExecuteOne("flatten(1.5)", input));
    }

    [Fact]
    public void FlattenTreatsAnObjectAsItsValueStream()
    {
        const string input = "{\"a\":[1,[2]],\"b\":3}";
        Assert.Equal("[1,2,3]", ExecuteOne("flatten", input));
        Assert.Equal("[1,[2],3]", ExecuteOne("flatten(1)", input));
        Assert.Equal("[]", ExecuteOne("flatten", "{}"));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("{}")]
    public void ReverseReturnsAnEmptyArrayForZeroLengthNonArrays(string input)
    {
        Assert.Equal("[]", ExecuteOne("reverse", input));
    }

    [Fact]
    public void ReverseRetainsJqIndexFailuresForNonEmptyNonArrays()
    {
        Assert.Equal(
            "\"Cannot index string with number (2)\"",
            ExecuteOne("try reverse catch .", "\"abc\""));
        Assert.Equal(
            "\"Cannot index object with number (0)\"",
            ExecuteOne("try reverse catch .", "{\"a\":1}"));
    }

    [Fact]
    public void ZeroLengthScalarCombinationsProducesTheEmptyCombination()
    {
        Assert.Equal("[[]]", ExecuteOne("[combinations]?", "0"));
        Assert.Equal("[[]]", ExecuteOne("[combinations(0)]", "0"));
    }

    [Fact]
    public void LiteralSplitOfAnEmptyStringProducesNoParts()
    {
        Assert.Equal("[]", ExecuteOne("split(\"[, ]+\")", "\"\""));
        Assert.Equal("[]", ExecuteOne("split(\"\")", "\"\""));
    }

    [Fact]
    public void ManagedNumericDecompositionIntrinsicsMatchLibmShapes()
    {
        Assert.Equal(
            "[[0.5,4],[0.5,1],1.9765625]",
            ExecuteOne("[(8|frexp),(1.5|modf),(1e-320|significand)]", "null"));
        Assert.Equal(
            "\"string (\\\"x\\\") number required\"",
            ExecuteOne("try (\"x\"|frexp) catch .", "null"));
    }

    private static string ExecuteOne(string filter, string input) =>
        Assert.Single(JqProgram.Compile(filter).Execute(input)).GetRawText();
}
