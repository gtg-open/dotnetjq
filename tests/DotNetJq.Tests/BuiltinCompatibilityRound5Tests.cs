namespace DotNetJq.Tests;

public sealed class BuiltinCompatibilityRound5Tests
{
    [Theory]
    [InlineData(
        "[\"1\",\"2\",{\"a\":{\"b\":{\"c\":33}}}]",
        "\"string (\\\"1,2,\\\") and object ({\\\"a\\\":{\\\"b\\\":{\\\"c\\\":33}}}) cannot be added\"")]
    [InlineData(
        "[\"1\",\"2\",[3,4,5]]",
        "\"string (\\\"1,2,\\\") and array ([3,4,5]) cannot be added\"")]
    public void JoinReportsTheAccumulatedStringAndInvalidValue(string input, string expected)
    {
        Assert.Equal(expected, ExecuteOne("try join(\",\") catch .", input));
    }

    [Fact]
    public void InputWithoutAConfiguredInputCallbackRaisesBreak()
    {
        Assert.Equal("\"break\"", ExecuteOne("try input catch .", "null"));
    }

    [Fact]
    public void DebugIsIdentityWithoutAnAmbientDebugCallback()
    {
        Assert.Equal("1", ExecuteOne("debug", "1"));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("1")]
    [InlineData("\"a\"")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void NullHasNoKeyForEveryKeyKind(string key)
    {
        Assert.Equal("false", ExecuteOne($"has({key})", "null"));
    }

    [Fact]
    public void InTreatsANullContainerAsEmpty()
    {
        Assert.Equal("false", ExecuteOne("in(null)", "\"a\""));
    }

    [Theory]
    [InlineData("contains({})", "2", "number (2) and object ({}) cannot have their containment checked")]
    [InlineData("inside({})", "[true,false,null]", "object ({}) and array ([true,false,null]) cannot have their containment checked")]
    [InlineData("inside({})", "\"abc\"", "object ({}) and string (\"abc\") cannot have their containment checked")]
    public void ContainmentTypeErrorsIncludeBothJqValues(
        string filter,
        string input,
        string expected)
    {
        using var output = System.Text.Json.JsonDocument.Parse(
            ExecuteOne($"try {filter} catch .", input));
        Assert.Equal(expected, output.RootElement.GetString());
    }

    [Theory]
    [InlineData("getpath(null)")]
    [InlineData("setpath(null; 0)")]
    public void PathArgumentMustBeAnArray(string filter)
    {
        Assert.Equal(
            "\"Path must be specified as an array\"",
            ExecuteOne($"try {filter} catch .", "{}"));
    }

    [Fact]
    public void ToNumberRejectsLeadingAndTrailingWhitespaceDuringUpdate()
    {
        Assert.Equal(
            "[1,3,6.7,0.89,-876,5.43,21]",
            ExecuteOne(
                ".[] |= try tonumber",
                "[\"1\",\"2a\",\"3\",\" 4\",\"5 \",\"6.7\",\".89\",\"-876\",\"+5.43\",21]"));
    }

    [Theory]
    [InlineData("ltrimstr", "startswith() requires string inputs")]
    [InlineData("rtrimstr", "endswith() requires string inputs")]
    public void TrimStringDiagnosticsMatchTheirUnderlyingPredicate(string operation, string diagnostic)
    {
        var expected = $"[\"ko\",{System.Text.Json.JsonSerializer.Serialize(diagnostic)}]";
        Assert.Equal(
            [expected, expected, "[\"ok\",\"\"]", expected],
            Execute(
                $".[] as [$x, $y] | try [\"ok\", ($x | {operation}($y))] catch [\"ko\", .]",
                "[[\"hi\",1],[1,\"hi\"],[\"hi\",\"hi\"],[1,1]]"));
    }

    private static string ExecuteOne(string filter, string input) => Assert.Single(Execute(filter, input));

    private static string[] Execute(string filter, string input) =>
        JqProgram.Compile(filter).Execute(input).Select(value => value.GetRawText()).ToArray();
}
