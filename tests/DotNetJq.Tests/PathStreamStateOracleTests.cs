using DotNetJq.Tests.Harness;

namespace DotNetJq.Tests;

public sealed class PathStreamStateOracleTests
{
    [Theory]
    [InlineData(
        "try path(.a | map(select(.b == 0))) catch .",
        "{\"a\":[{\"b\":0}]}")]
    [InlineData(
        "del(.[1], .[-6], .[2], .[-3:9])",
        "[0,1,2,3,4,5,6,7,8,9]")]
    [InlineData(
        "truncate_stream([[0],\"a\"],[[1,0],\"b\"],[[1,0]],[[1]])",
        "1")]
    [InlineData(
        ". as $dot | fromstream($dot | tostream) | . == $dot",
        "[0,[1,{\"a\":1},{\"b\":2}]]")]
    [InlineData("[splits(\"[, ]+\")]", "\"ab\"")]
    [InlineData("[splits(\"[, ]+\")]", "\"a,b\"")]
    [InlineData("null as {$x} | $x", "null")]
    [InlineData("null as [$x] | $x", "null")]
    [InlineData("[limit(1; 1, error)]", "\"badness\"")]
    [InlineData("nth(1; 0,1,error(\"foo\"))", "null")]
    [InlineData("foreach (1,2) as $x (10,20; .+$x; [$x,.])", "null")]
    public async Task ManagedPathAndStreamStateMatchesJq182(string filter, string input)
    {
        var oracle = await JqOracle.ExecuteAsync(
            filter,
            input,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(oracle.Succeeded, oracle.StandardError);

        var managed = JqProgram.Compile(filter)
            .Execute(input)
            .Select(value => value.GetRawText())
            .ToArray();

        Assert.Equal(oracle.OutputLines, managed);
    }
}
