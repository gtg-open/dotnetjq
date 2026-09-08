using DotNetJq.Port;

namespace DotNetJq.Tests;

public sealed class BuiltinCompatibilityRound2Tests
{
    [Fact]
    public void WhileAndUntilPreserveGeneratorSemantics()
    {
        Assert.Equal("[1,2,4,8,16,32,64]", ExecuteOne("[while(.<100; .*2)]", "1"));
        Assert.Equal("[1,2,6,24]", ExecuteOne(
            "[.[]|[.,1]|until(.[0] < 1; [.[0] - 1, .[1] * .[0]])|.[1]]",
            "[1,2,3,4]"));
        Assert.Equal("[0,1,2,3,3,2,3]", ExecuteOne("[while(.<4; (.+1,.+2))]", "0"));
    }

    [Fact]
    public void RecursiveIteratorsHonorTheConfiguredDepthLimit()
    {
        var exception = Assert.Throws<JqRuntimeException>(() =>
            JqProgram.Compile("while(true; .)").Execute(
                "null",
                new JqExecutionOptions
                {
                    MaxRecursionDepth = 4,
                    MaxExecutionTransitions = 1000,
                }));

        Assert.Equal("jq recursion depth limit exceeded", exception.Message);
    }

    [Fact]
    public void TransposePadsJaggedRowsWithNull()
    {
        Assert.Equal("[[1,2],[null,3]]", ExecuteOne("transpose", "[[1],[2,3]]"));
        Assert.Equal("[]", ExecuteOne("transpose", "[]"));
        Assert.Equal(
            "[[1,2,null],[null,3,null]]",
            ExecuteOne("transpose", "[[1],[2,3],[]]"));
    }

    [Fact]
    public void BinarySearchMatchesJqInsertionPointContract()
    {
        Assert.Equal(
            ["-1", "0", "1", "2", "-4"],
            Execute("bsearch(0,1,2,3,4)", "[1,2,3]"));
        Assert.Equal("1", ExecuteOne("bsearch({x:1})", "[{\"x\":0},{\"x\":1},{\"x\":2}]"));

        var error = Assert.Throws<JqRuntimeException>(() =>
            JqProgram.Compile("bsearch(0)").Execute("\"aa\""));
        Assert.Equal("string (\"aa\") cannot be searched from", error.Message);
    }

    [Fact]
    public void MaxBySelectsTheLastEqualKeyLikeUpstream()
    {
        Assert.Equal(
            "[1,3,\"a\"]",
            ExecuteOne("max_by(.[2])", "[[4,2,\"a\"],[3,1,\"a\"],[2,4,\"a\"],[1,3,\"a\"]]"));
        Assert.Equal(
            "[4,2,\"a\"]",
            ExecuteOne("min_by(.[2])", "[[4,2,\"a\"],[3,1,\"a\"],[2,4,\"a\"],[1,3,\"a\"]]"));
    }

    [Fact]
    public void UtcBrokenDownTimeRoundTripsLikeJq()
    {
        Assert.Equal(
            "\"2015-03-05T23:51:47Z\"",
            ExecuteOne("strftime(\"%Y-%m-%dT%H:%M:%SZ\")", "[2015,2,5,23,51,47,4,63]"));
        Assert.Equal(
            "\"Tuesday, June 30, 2015\"",
            ExecuteOne("strftime(\"%A, %B %d, %Y\")", "1435677542.822351"));
        Assert.Equal("1726876800", ExecuteOne("mktime", "[2024,8,21]"));
        Assert.Equal(
            "[2015,2,5,23,51,47,4,63]",
            ExecuteOne("gmtime", "1425599507"));
        Assert.Equal("47.25", ExecuteOne("gmtime[5]", "1425599507.25"));
        Assert.Equal(
            "[[2015,2,5,23,51,47,4,63],1425599507]",
            ExecuteOne(
                "[strptime(\"%Y-%m-%dT%H:%M:%SZ\")|(.,mktime)]",
                "\"2015-03-05T23:51:47Z\""));
    }

    [Fact]
    public void DateAliasesAndNegativeFractionalEpochsRetainJqBehavior()
    {
        Assert.Equal("1425599507", ExecuteOne("fromdate", "\"2015-03-05T23:51:47Z\""));
        Assert.Equal("\"2015-03-05T23:51:47Z\"", ExecuteOne("todate", "1425599507"));
        Assert.Equal("[1970,0,1,0,0,0.75,4,0]", ExecuteOne("gmtime", "-0.25"));
    }

    [Fact]
    public void TimeBuiltinsReturnJqDiagnosticMessages()
    {
        Assert.Equal(
            "\"strftime/1 requires parsed datetime inputs\"",
            ExecuteOne("try strftime(\"%Y-%m-%d\") catch .", "[\"a\",1,2,3,4,5,6,7]"));
        Assert.Equal(
            "\"strflocaltime/1 requires a string format\"",
            ExecuteOne("try strflocaltime({}) catch .", "0"));
        Assert.Equal(
            "\"mktime requires parsed datetime inputs\"",
            ExecuteOne("try mktime catch .", "[\"a\",1,2,3,4,5,6,7]"));
    }

    [Fact]
    public void NowReturnsCurrentUnixSeconds()
    {
        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d;
        var result = double.Parse(ExecuteOne("now", "null"), System.Globalization.CultureInfo.InvariantCulture);
        var after = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d;

        Assert.InRange(result, before - 0.01d, after + 0.01d);
    }

    [Fact]
    public void HasUsesJqArrayIndexConversionForNonIntegralAndSpecialNumbers()
    {
        Assert.Equal(
            "[false,false,true,true,false]",
            ExecuteOne(
                "[has(nan), has(infinite), has(0.5), has(-0.5), has(1.5)]",
                "[1]"));
    }

    [Fact]
    public void SqlStyleStreamBuiltinsMatchBuiltinJqDefinitions()
    {
        Assert.Equal(
            "{\"0\":[0,\"foo0\"],\"1\":[1,\"foo1\"],\"2\":[2,\"foo2\"]}",
            ExecuteOne("INDEX(range(3)|[., \"foo\\(.)\"]; .[0])", "null"));
        Assert.Equal(
            "[[[5,\"foo\"],null],[[3,\"bar\"],[3,\"efg\"]],[[1,\"foobar\"],[1,\"bcd\"]]]",
            ExecuteOne(
                "JOIN({\"0\":[0,\"abc\"],\"1\":[1,\"bcd\"],\"2\":[2,\"def\"],\"3\":[3,\"efg\"]}; .[0]|tostring)",
                "[[5,\"foo\"],[3,\"bar\"],[1,\"foobar\"]]"));
        Assert.Equal(["true", "true", "true"], Execute("range(5;8)|IN(range(10))", "null"));
        Assert.Equal("false", ExecuteOne("IN(range(10;20); range(10))", "null"));
        Assert.Equal("true", ExecuteOne("IN(range(5;20); range(10))", "null"));
    }

    [Fact]
    public void DelpathsRejectsMalformedPathCollectionsWithJqDiagnostics()
    {
        Assert.Equal(
            "\"Paths must be specified as an array\"",
            ExecuteOne("try delpaths(0) catch .", "{}"));
        Assert.Equal(
            "\"Path must be specified as array, not number\"",
            ExecuteOne("try delpaths([0]) catch .", "{}"));
    }

    [Fact]
    public void BuiltinsReportsTheImplementedNonNegativeArities()
    {
        Assert.Equal("true", ExecuteOne("builtins|length > 10", "null"));
        Assert.Equal("false", ExecuteOne("\"-1\"|IN(builtins[] / \"/\"|.[1])", "null"));
        Assert.Equal("true", ExecuteOne("all(builtins[] / \"/\"; .[1]|tonumber >= 0)", "null"));
        Assert.Equal("false", ExecuteOne("builtins|any(.[:1] == \"_\")", "null"));
    }

    private static string ExecuteOne(string filter, string input) => Assert.Single(Execute(filter, input));

    private static string[] Execute(string filter, string input) =>
        JqProgram.Compile(filter).Execute(input).Select(value => value.GetRawText()).ToArray();
}
