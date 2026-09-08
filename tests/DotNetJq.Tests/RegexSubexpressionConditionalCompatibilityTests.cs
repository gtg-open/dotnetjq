using System.Text.Json;

namespace DotNetJq.Tests;

/// <summary>
/// jq-visible regressions derived from the pinned jq-1.8.2 Oniguruma oracle for
/// subexpression calls and Perl-NG conditionals.
/// </summary>
public sealed class RegexSubexpressionConditionalCompatibilityTests
{
    public static TheoryData<string, string, string> OracleCases => new()
    {
        {
            "match(\"a|b(?0)c\")",
            "\"bbacc\"",
            "{\"offset\":0,\"length\":5,\"string\":\"bbacc\",\"captures\":[]}"
        },
        {
            "match(\"a|b\\\\g<0>c\")",
            "\"bbacc\"",
            "{\"offset\":0,\"length\":5,\"string\":\"bbacc\",\"captures\":[]}"
        },
        {
            "match(\"a|b\\\\g'0'c\")",
            "\"bbacc\"",
            "{\"offset\":0,\"length\":5,\"string\":\"bbacc\",\"captures\":[]}"
        },
        {
            "match(\"(a)(?+1)(b)\")",
            "\"abb\"",
            "{\"offset\":0,\"length\":3,\"string\":\"abb\",\"captures\":[" +
            "{\"offset\":0,\"length\":1,\"string\":\"a\",\"name\":null}," +
            "{\"offset\":2,\"length\":1,\"string\":\"b\",\"name\":null}]}"
        },
        {
            "match(\"(a)\\\\g<+1>(b)\")",
            "\"abb\"",
            "{\"offset\":0,\"length\":3,\"string\":\"abb\",\"captures\":[" +
            "{\"offset\":0,\"length\":1,\"string\":\"a\",\"name\":null}," +
            "{\"offset\":2,\"length\":1,\"string\":\"b\",\"name\":null}]}"
        },
        {
            "match(\"(a)\\\\g'+1'(b)\")",
            "\"abb\"",
            "{\"offset\":0,\"length\":3,\"string\":\"abb\",\"captures\":[" +
            "{\"offset\":0,\"length\":1,\"string\":\"a\",\"name\":null}," +
            "{\"offset\":2,\"length\":1,\"string\":\"b\",\"name\":null}]}"
        },
        {
            "[\"ab\",\"ac\",\"c\"] | map(test(\"\\\\A(?<x>a)?(?(<x>)b|c)\\\\z\"))",
            "null",
            "[true,false,true]"
        },
        {
            "[\"ab\",\"ac\",\"c\"] | map(test(\"\\\\A(?<x>a)?(?('x')b|c)\\\\z\"))",
            "null",
            "[true,false,true]"
        },
        {
            "[\"ac\",\"bc\",\"ad\",\"bd\"] | " +
            "map(test(\"^(?:(?<x>a)|(?<x>b))(?(<x>)c|d)$\"))",
            "null",
            "[true,true,false,false]"
        },
        {
            "[\"ab\",\"ac\",\"c\"] | map(test(\"\\\\A(a)?(?(-1)b|c)\\\\z\"))",
            "null",
            "[true,false,true]"
        },
        {
            "[\"ab\",\"c\",\"ac\"] | map(test(\"\\\\A(?(a)b|c)\\\\z\"))",
            "null",
            "[true,true,false]"
        },
        {
            "[\"abc\",\"abd\",\"d\"] | map(test(\"^(?(ab)c|d)$\"))",
            "null",
            "[true,false,true]"
        },
        {
            "[\"ac\",\"bc\",\"d\"] | map(test(\"^(?(a|b)c|d)$\"))",
            "null",
            "[true,true,true]"
        },
        {
            "match(\"^(?((a))b|c)$\")",
            "\"ab\"",
            "{\"offset\":0,\"length\":2,\"string\":\"ab\",\"captures\":[" +
            "{\"offset\":0,\"length\":1,\"string\":\"a\",\"name\":null}]}"
        },
        {
            "[\"b\",\"c\"] | map(test(\"^(?(a*)b|c)$\"))",
            "null",
            "[true,false]"
        },
        {
            "test(\"^(?(a)b|a)$\")",
            "\"a\"",
            "false"
        },
        {
            "match(\"\\\\A(?(a)b|c)\\\\z\")",
            "\"ab\"",
            "{\"offset\":0,\"length\":2,\"string\":\"ab\",\"captures\":[]}"
        },
        {
            "match(\"(?<x>a)(?x)b\")",
            "\"ab\"",
            "{\"offset\":0,\"length\":2,\"string\":\"ab\",\"captures\":[" +
            "{\"offset\":0,\"length\":1,\"string\":\"a\",\"name\":\"x\"}]}"
        },
        {
            "try match(\"(?<x>a)(?<x>b)\\\\g<x>\") catch .",
            "\"ab\"",
            "\"Regex failure: multiplex definition name <x> call\""
        },
        {
            "try match(\"(?<x>a)(?<x>b)\\\\g'x'\") catch .",
            "\"ab\"",
            "\"Regex failure: multiplex definition name <x> call\""
        },
        {
            "try match(\"(?<x>a)(?<x>b)(?&x)\") catch .",
            "\"ab\"",
            "\"Regex failure: multiplex definition name <x> call\""
        },
        {
            "try match(\"(?<x>a)(?P>x)\") catch .",
            "\"ab\"",
            "\"Regex failure: undefined group option\""
        },
    };

    [Theory]
    [MemberData(nameof(OracleCases))]
    public void SubexpressionCallsAndConditionalsMatchPinnedJq182(
        string filter,
        string input,
        string expected)
    {
        var output = Assert.Single(JqProgram.Compile(filter).Execute(input));
        using var expectedDocument = JsonDocument.Parse(expected);
        Assert.True(
            JsonElement.DeepEquals(expectedDocument.RootElement, output),
            $"expected {expectedDocument.RootElement.GetRawText()}, actual {output.GetRawText()}");
    }
}
