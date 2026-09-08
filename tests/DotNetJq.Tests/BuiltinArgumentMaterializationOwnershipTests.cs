// DOTNETJQ TEST PORT MAP
// Upstream revision: jq-1.8.2 34f7186b86743a083a589741b6cea95293524108
// Upstream sources: src/execute.c:CALL_BUILTIN and src/builtin.c:LIBM_DDD/LIBM_DDDD
// Strategy: exact-output compatibility regressions for replayed builtin argument ownership.

using DotNetJq.Port;

namespace DotNetJq.Tests;

public sealed class BuiltinArgumentMaterializationOwnershipTests
{
    [Fact]
    public void RegexPrimitiveRetainsComputedPatternAndModifierValues()
    {
        Assert.Equal(
            "[false,true]",
            ExecuteOne(
                "[_match_impl((\"\\\"^a\\\"\"|fromjson); null; true)," +
                "_match_impl(\"^a\"; (\"\\\"i\\\"\"|fromjson); true)]",
                "\"ABC\""));
    }

    [Fact]
    public void DirectRegexBuiltinRetainsComputedPatternAndModifierValues()
    {
        Assert.Equal(
            ["\"a\"", "\"a\""],
            Execute("scan((\"a\" + \"\"); (\"g\" + \"\"))", "\"aba\""));
    }

    [Theory]
    [InlineData(
        "try pow((\"\\\"x\\\"\"|fromjson);2) catch .",
        "\"string (\\\"x\\\") number required\"")]
    [InlineData(
        "try range((\"\\\"x\\\"\"|fromjson);2) catch .",
        "\"Range bounds must be numeric\"")]
    public void MathAndRangeRetainComputedArgumentValues(string filter, string expected)
    {
        Assert.Equal(expected, ExecuteOne(filter, "null"));
    }

    [Theory]
    [InlineData("has((\"a\",\"b\"))", "{\"a\":1}", "[true,false]")]
    [InlineData(
        "format((\"json\",\"text\"))",
        "{\"a\":1}",
        "[\"{\\\"a\\\":1}\",\"{\\\"a\\\":1}\"]")]
    public void CFunctionArgumentContinuationsRetainImplicitInput(
        string filter,
        string input,
        string expected)
    {
        Assert.Equal(expected, ExecuteOne($"[{filter}]", input));
    }

    private static string ExecuteOne(string filter, string input) =>
        Assert.Single(JqProgram.Compile(filter).Execute(input)).GetRawText();

    private static string[] Execute(string filter, string input) =>
        JqProgram.Compile(filter)
            .Execute(input)
            .Select(result => result.GetRawText())
            .ToArray();
}
