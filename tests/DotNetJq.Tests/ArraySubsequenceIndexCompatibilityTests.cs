using DotNetJq.Tests.Harness;

namespace DotNetJq.Tests;

public sealed class ArraySubsequenceIndexCompatibilityTests
{
    [Theory]
    [InlineData(".[[1,2]]", "[1,2,1,2,1]", "[0,2]")]
    [InlineData(".[[1,1]]", "[1,1,1]", "[0,1]")]
    [InlineData(".[[[1]]]", "[[1],[2],[1]]", "[0,2]")]
    [InlineData(".[[{\"a\":[1]}]]", "[{\"a\":[1]},{\"a\":[1]},{\"a\":[2]}]", "[0,1]")]
    [InlineData(".[[]]", "[1,2]", "[]")]
    [InlineData(".[[3]]", "[1,2]", "[]")]
    [InlineData(".[[1,2]]", "[1]", "[]")]
    public async Task ArrayKeysReturnEveryMatchingSubsequenceStart(
        string filter,
        string input,
        string expected)
    {
        await AssertMatchesOracle(filter, input, expected);
    }

    [Theory]
    [InlineData("null", "\"Cannot index null with array ([1])\"")]
    [InlineData("\"abc\"", "\"Cannot index string with array ([1])\"")]
    [InlineData("{}", "\"Cannot index object with array ([1])\"")]
    [InlineData("1", "\"Cannot index number with array ([1])\"")]
    public async Task ArrayKeysRetainJqTypeErrorsForNonArrayTargets(
        string input,
        string expected)
    {
        await AssertMatchesOracle("try .[[1]] catch .", input, expected);
    }

    [Theory]
    [InlineData(
        "[indices([\"a\"]), index([\"a\"]), rindex([\"a\"])]",
        "[\"a\",\"b\",\"a\"]",
        "[[0,2],0,2]")]
    [InlineData(
        "[indices([]), index([]), rindex([])]",
        "[1,2]",
        "[[],null,null]")]
    [InlineData(
        "[indices([3]), index([3]), rindex([3])]",
        "[1,2]",
        "[[],null,null]")]
    public async Task SourceDefinedIndicesAndIndexFunctionsUseArrayKeySemantics(
        string filter,
        string input,
        string expected)
    {
        await AssertMatchesOracle(filter, input, expected);
    }

    private static async Task AssertMatchesOracle(
        string filter,
        string input,
        string expected)
    {
        var managed = JqProgram.Compile(filter).Execute(input)
            .Select(static value => value.GetRawText())
            .ToArray();
        Assert.Equal([expected], managed);

        if (!JqOracle.TryResolveExecutable(out _))
        {
            return;
        }

        var oracle = await JqOracle.ExecuteAsync(
            filter,
            input,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(oracle.Succeeded, oracle.StandardError);
        Assert.Equal(oracle.OutputLines, managed);
    }
}
