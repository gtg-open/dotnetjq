namespace DotNetJq.Tests;

public sealed class BuiltinCompatibilityRound3Tests
{
    [Theory]
    [InlineData("-2", "2")]
    [InlineData("2", "2")]
    [InlineData("\"abc\"", "\"abc\"")]
    [InlineData("[]", "[]")]
    [InlineData("{}", "{}")]
    public void AbsMatchesTheJqCodedDefinition(string input, string expected)
    {
        Assert.Equal(expected, ExecuteOne("abs", input));
    }

    [Theory]
    [InlineData("null", "\"null (null) cannot be negated\"")]
    [InlineData("false", "\"boolean (false) cannot be negated\"")]
    [InlineData("true", "\"boolean (true) cannot be negated\"")]
    public void AbsUsesJqComparisonBeforeNegation(string input, string expected)
    {
        Assert.Equal(expected, ExecuteOne("try abs catch .", input));
    }

    [Fact]
    public void WalkTransformsCompositeValuesBottomUp()
    {
        Assert.Equal(
            "[2,{\"a\":3}]",
            ExecuteOne("walk(if type == \"number\" then . + 1 else . end)", "[1,{\"a\":2}]"));
        Assert.Equal(
            "{\"a\":1}",
            ExecuteOne("walk(select(. != []))", "{\"a\":1,\"b\":[]}"));
    }

    [Fact]
    public void WalkPreservesUpstreamFilterStreamSemantics()
    {
        Assert.Equal("{\"x\":0}", ExecuteOne("walk(.)", "{\"x\":0}"));
        Assert.Equal("1", ExecuteOne("walk(1)", "{\"x\":0}"));
        Assert.Equal("[{\"x\":0},1]", ExecuteOne("[walk(.,1)]", "{\"x\":0}"));
        Assert.Equal(
            "{\"a\":1}",
            ExecuteOne("walk(select(IN({}, []) | not))", "{\"a\":1,\"b\":[]}"));
    }

    [Fact]
    public void GlobalReplacementStreamsCorrelateByStreamPosition()
    {
        Assert.Equal(
            "[\"AB\",\"ab\",\"cc\"]",
            ExecuteOne(
                "[gsub(\"(?<a>.)\"; \"\\(.a|ascii_upcase)\", \"\\(.a|ascii_downcase)\", \"c\")]",
                "\"aB\""));
    }

    [Fact]
    public void FromJsonFailuresAreCatchableRuntimeValues()
    {
        Assert.Equal(
            [
                "true",
                "true",
                "\"Invalid numeric literal at EOF at line 1, column 4 (while parsing 'NaN1')\"",
                "\"Invalid numeric literal at EOF at line 1, column 5 (while parsing 'NaN10')\"",
                "\"Invalid numeric literal at EOF at line 1, column 6 (while parsing 'NaN100')\"",
                "\"Invalid numeric literal at EOF at line 1, column 7 (while parsing 'NaN1000')\"",
                "\"Invalid numeric literal at EOF at line 1, column 8 (while parsing 'NaN10000')\"",
                "\"Invalid numeric literal at EOF at line 1, column 9 (while parsing 'NaN100000')\"",
            ],
            Execute(
                ".[] | try (fromjson | isnan) catch .",
                "[\"NaN\",\"-NaN\",\"NaN1\",\"NaN10\",\"NaN100\",\"NaN1000\",\"NaN10000\",\"NaN100000\"]"));

        Assert.Equal(
            "\"Invalid string literal; expected \\\", but got ' at line 1, column 5 " +
            "(while parsing '{'a': 123}')\"",
            ExecuteOne("try fromjson catch .", "\"{'a': 123}\""));
    }

    private static string ExecuteOne(string filter, string input) => Assert.Single(Execute(filter, input));

    private static string[] Execute(string filter, string input) =>
        JqProgram.Compile(filter).Execute(input).Select(value => value.GetRawText()).ToArray();
}
