namespace DotNetJq.Tests;

/// <summary>Regressions for jq reduce update-stream assignment behavior.</summary>
public sealed class ReduceStreamCompatibilityTests
{
    [Fact]
    public void EmptyUpdateStreamReplacesTheAccumulatorWithNull()
    {
        var result = Assert.Single(
            JqProgram.Compile("reduce .[] as $x (0; . + ($x | numbers))")
                .Execute("[1,\"x\",2]"));

        Assert.Equal(2, result.GetInt32());
    }

    [Fact]
    public void MultiValueUpdateKeepsItsLastValue()
    {
        var result = Assert.Single(
            JqProgram.Compile("reduce .[] as $x (0; 1,2,3)").Execute("[0]"));

        Assert.Equal(3, result.GetInt32());
    }

    [Fact]
    public void DifferentialCase708ProducesOneNullAccumulator()
    {
        const string filter = "reduce .[]? as $x (0; . + ($x | numbers))";
        const string input = "{\"foo\":[1,2],\"bar\":\"a,b\"}";

        var result = Assert.Single(JqProgram.Compile(filter).Execute(input));

        Assert.Equal(System.Text.Json.JsonValueKind.Null, result.ValueKind);
    }
}
