namespace DotNetJq.Tests;

public sealed class BuiltinCompatibilityRound4Tests
{
    [Fact]
    public void BuiltinDiagnosticsMatchPinnedJq()
    {
        Assert.Equal(
            "\"number (123) cannot be searched, as it is not a string\"",
            ExecuteOne("try _strindices(\"abc\") catch .", "123"));
        Assert.Equal(
            "\"number (123) is not a string\"",
            ExecuteOne("try _strindices(123) catch .", "\"abc\""));
        Assert.Equal(
            [
                "\"trim input must be a string\"",
                "\"trim input must be a string\"",
                "\"trim input must be a string\"",
            ],
            Execute("try trim catch ., try ltrim catch ., try rtrim catch .", "123"));
        Assert.Equal(
            "[\"implode input must be an array\",\"string (\\\"a\\\") can't be imploded, unicode codepoint needs to be numeric\",\"number (null) can't be imploded, unicode codepoint needs to be numeric\"]",
            ExecuteOne("map(try implode catch .)", "[123,[\"a\"],[nan]]"));
    }

    [Fact]
    public void FlattenHandlesThePinnedTenThousandLevelRegressionCase()
    {
        Assert.Equal(
            "[]",
            ExecuteOne(
                "reduce range(9999) as $_ ([];[.]) | tojson | fromjson | flatten",
                "null"));
    }

    [Fact]
    public void CombinationsPreservesCartesianProductOrder()
    {
        Assert.Equal(
            ["[1,3]", "[1,4]", "[2,3]", "[2,4]"],
            Execute("combinations", "[[1,2],[3,4]]"));
        Assert.Equal(
            ["[0,0]", "[0,1]", "[1,0]", "[1,1]"],
            Execute("combinations(2)", "[0,1]"));
    }

    [Fact]
    public void RepeatStopsAtACaughtErrorAfterYieldingEarlierValues()
    {
        Assert.Equal("[2]", ExecuteOne("[repeat(.*2, error)?]", "1"));
    }

    [Fact]
    public void RecurseMatchesJqCodedDefinitions()
    {
        Assert.Equal(
            ["{\"a\":0,\"b\":[1]}", "0", "[1]", "1"],
            Execute("recurse", "{\"a\":0,\"b\":[1]}"));
        Assert.Equal(["2", "4", "16"], Execute("recurse(. * .; . < 20)", "2"));
    }

    [Fact]
    public void StreamingHelpersTruncateAndRoundTrip()
    {
        const string stream = "[[0],\"a\"],[[1,0],\"b\"],[[1,0]],[[1]]";
        Assert.Equal(
            ["[[0],\"b\"]", "[[0]]"],
            Execute($"truncate_stream({stream})", "1"));
        Assert.Equal(
            "[\"b\"]",
            ExecuteOne($"fromstream(1|truncate_stream({stream}))", "null"));
        Assert.Equal(
            "true",
            ExecuteOne(". as $dot | fromstream($dot | tostream) | . == $dot", "[0,[1,{\"a\":1},{\"b\":2}]]"));
    }

    private static string ExecuteOne(string filter, string input) => Assert.Single(Execute(filter, input));

    private static string[] Execute(string filter, string input) =>
        JqProgram.Compile(filter).Execute(input).Select(value => value.GetRawText()).ToArray();
}
