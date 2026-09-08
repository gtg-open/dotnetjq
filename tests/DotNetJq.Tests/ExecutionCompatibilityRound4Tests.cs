namespace DotNetJq.Tests;

public sealed class ExecutionCompatibilityRound4Tests
{
    [Fact]
    public void CatchPreservesArbitraryJvErrorValues()
    {
        Assert.Equal(
            ["1", "null", "2"],
            Execute(".[] | try error catch .", "[1,null,2]"));
        Assert.Equal(
            ["[\"KO\",[\"b\"]]"],
            Execute(
                "try [\"OK\", (.[] | error)] catch [\"KO\", .]",
                "{\"a\":[\"b\"],\"c\":[\"d\"]}"));
    }

    [Fact]
    public void NestedCatchPropagatesTypedErrorsWithoutStringifyingThem()
    {
        Assert.Equal(
            ["\"inner catch foo\""],
            Execute("try (try error catch \"inner catch \\(.)\") catch \"outer catch \\(.)\"", "\"foo\""));
        Assert.Equal(
            ["\"outer catch inner catch foo\""],
            Execute(
                "try ((try error catch \"inner catch \\(.)\")|error) " +
                "catch \"outer catch \\(.)\"",
                "\"foo\""));
    }

    [Fact]
    public void IterationAndNegationDiagnosticsIncludeTruncatedJqValues()
    {
        Assert.Equal(
            ["[1,2,1,2,1,2,1,2,\"Cannot iterate over number (123)\",\"Cannot iterate over number (123)\"]"],
            Execute(
                "map(try .a[] catch ., try .a.[] catch ., .a[]?, .a.[]?)",
                "[{\"a\":[1,2]},{\"a\":123}]"));
        Assert.Equal(
            ["\"string (\\\"very-long-long-long-long...\\\") cannot be negated\""],
            Execute("try -. catch .", "\"very-long-long-long-long-string\""));
    }

    [Fact]
    public void IndexAndSliceKeysEvaluateAgainstTheirInputFrame()
    {
        Assert.Equal(["4"], Execute(".foo[.baz]", "{\"foo\":{\"bar\":4},\"baz\":\"bar\"}"));
        Assert.Equal(["null"], Execute("[][.]", "1000000000000000000"));
        Assert.Equal(
            ["[[1],[1],[1,2],[1,2],[1,2]]"],
            Execute("map([1,2][0:.])", "[-1,1,2,3,1000000000000000000]"));
        Assert.Equal(
            ["\"Cannot index number with string (\\\"\\\")\""],
            Execute("try 0[implode] catch .", "[]"));
    }

    [Fact]
    public void BinaryAndForeachGeneratorOrderingMatchesJqBytecode()
    {
        Assert.Equal(
            ["[1,3,3.5,4.5]"],
            Execute("[foreach .[] / .[] as $i (0; . + $i)]", "[1,2]"));
        Assert.Equal(
            ["1", "3", "2", "4"],
            Execute("foreach .[] as $x (0, 1; . + $x)", "[1,2]"));
        Assert.Equal(
            ["99", "99"],
            Execute(
                "foreach range(0;3) as $i " +
                "(0; if $i==0 then empty else (. // 99) end; .)",
                "null"));
        Assert.Equal(
            ["1", "3"],
            Execute(
                "\"a\" as $x | foreach ([{\"a\":1},{\"a\":2}][]) " +
                "as {($x):$x} (0; .+$x; .)",
                "null"));
    }

    [Fact]
    public void SetPathReportsJqUpdateDiagnostics()
    {
        Assert.Equal(
            ["[\"ko\",\"Cannot index object with number (1)\"]"],
            Execute("try [\"ok\", setpath([1]; 1)] catch [\"ko\", .]", "{\"hi\":\"hello\"}"));
        Assert.Equal(
            ["[\"KO\",\"Cannot update field at array index of array\"]"],
            Execute("try [\"OK\", setpath([[1]]; 1)] catch [\"KO\", .]", "[]"));
    }

    [Fact]
    public void TenThousandComponentSetPathFlattensWithoutClrRecursion()
    {
        Assert.Equal(
            ["[0]"],
            Execute("setpath([range(10000) | 0]; 0) | flatten", "null"));
        Assert.Equal(
            ["\"Path too deep\""],
            Execute("try setpath([range(10001) | 0]; 0) catch .", "null"));
    }

    private static string[] Execute(string filter, string input) =>
        JqProgram.Compile(filter).Execute(input).Select(value => value.GetRawText()).ToArray();
}
