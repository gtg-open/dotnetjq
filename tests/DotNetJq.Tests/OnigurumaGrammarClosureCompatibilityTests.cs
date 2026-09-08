using System.Text.Json;

namespace DotNetJq.Tests;

/// <summary>
/// jq-visible grammar and execution cases frozen against jq-1.8.2 commit
/// 34f7186b86743a083a589741b6cea95293524108.  Every successful pattern contains
/// either runner-only grammar or \K so these assertions exercise the managed
/// source-shaped runner rather than the ordinary .NET-regex proxy.
/// </summary>
public sealed class OnigurumaGrammarClosureCompatibilityTests
{
    public static TheoryData<string, string> OracleCases => new()
    {
        {
            "[(\"aa\"|test(\"\\\\A(a)(?1)\\\\K\\\\z\"))," +
            "(\"abb\"|test(\"\\\\A(a)(?+1)(b)\\\\K\\\\z\"))," +
            "(\"aa\"|test(\"\\\\A(a)\\\\g<-1>\\\\K\\\\z\"))," +
            "(\"aa\"|test(\"\\\\A(?<x>a)(?&x)\\\\K\\\\z\"))]",
            "[true,true,true,true]"
        },
        {
            "[(\"aa\"|test(\"\\\\A(a)\\\\k<1>\\\\K\\\\z\"))," +
            "(\"aa\"|test(\"\\\\A(a)\\\\k<-1>\\\\K\\\\z\"))]",
            "[true,true]"
        },
        {
            "[(\"aa\"|test(\"\\\\A(?<愚か>a)\\\\k<愚か>\\\\K\\\\z\"))," +
            "(\"abb\"|test(\"\\\\A(?<x>a)(?<x>b)\\\\k<x>\\\\K\\\\z\"))," +
            "(\"aba\"|test(\"\\\\A(?<x>a)(?<x>b)\\\\k<x>\\\\K\\\\z\"))]",
            "[true,true,true]"
        },
        {
            "[\"a\",\"\",\"ab\",\"c\",\"d\"] | map(. as $s | " +
            "[($s|test(\"\\\\A(a)?(?(1))\\\\K\\\\z\"))," +
            "($s|test(\"\\\\A(?()a|b)\\\\K\\\\z\"))," +
            "($s|test(\"\\\\A(?(a)b|c|d)\\\\K\\\\z\"))])",
            "[[true,true,false],[false,false,false],[false,false,true]," +
            "[false,false,true],[false,false,true]]"
        },
        {
            "[\"b\",\"a\",\"ba\"] | map(test(\"\\\\A(?~|(?~a)|b)\\\\K\\\\z\"))",
            "[true,false,false]"
        },
        {
            "[(\"Q\"|test(\"\\\\A[\\\\Q]\\\\K\\\\z\"))," +
            "(\"R\"|test(\"\\\\A[\\\\R]\\\\K\\\\z\"))," +
            "(\"A\"|test(\"\\\\A[\\\\A]\\\\K\\\\z\"))," +
            "(\"X\"|test(\"\\\\A[\\\\X]\\\\K\\\\z\"))]",
            "[true,true,true,true]"
        },
        {
            "[(([7,27,12,127]|implode)|test(\"\\\\A\\\\a\\\\e\\\\f\\\\c?\\\\K\\\\z\"))," +
            "(([0]|implode)|test(\"\\\\A\\\\c😀\\\\K\\\\z\"))," +
            "(([10]|implode)|test(\"\\\\A\\\\c\\\\n\\\\K\\\\z\"))]",
            "[true,true,true]"
        },
        {
            "try (\"x\"|test(\"\\\\K\\\\c\")) catch .",
            "\"Regex failure: end pattern at control\""
        },
        {
            "\"abc\" | [match(\"\\\\G\\\\K.\";\"g\")|.string]",
            "[\"a\",\"b\",\"c\"]"
        },
        {
            "[\"ab\",\"aab\",\"b\"] | " +
            "map([test(\"(?<=a+)b\\\\K\"),test(\"(?<=a*)b\\\\K\")])",
            "[[true,true],[true,true],[false,true]]"
        },
        {
            "[(\"a\"|test(\"\\\\A(a)(?<=\\\\k<1>)\\\\K\\\\z\"))," +
            "(\"a\"|test(\"\\\\A(?<x>a)(?<=\\\\g<x>)\\\\K\\\\z\"))," +
            "(\"ab\"|test(\"\\\\Aa(?<=(?~a))b\\\\K\\\\z\"))," +
            "try (\"b\"|test(\"(?<=(?~|a))b\\\\K\")) catch .]",
            "[true,true,true,\"Regex failure: invalid pattern in look-behind\"]"
        },
        {
            "[(\"bbacca\"|test(\"\\\\A(a|b\\\\g<1>c)\\\\k<1+3>\\\\K\\\\z\"))," +
            "(\"bbacca\"|test(\"\\\\A(?<x>a|b\\\\g<x>c)\\\\k<x+3>\\\\K\\\\z\"))," +
            "(\"bbacca\"|test(\"\\\\A(a|b\\\\g<1>c)(?(1+3)a|x)\\\\K\\\\z\"))]",
            "[true,true,true]"
        },
        {
            "try (\"a\"|test(\"(\")) catch .",
            "\"Regex failure: end pattern with unmatched parenthesis\""
        },
        {
            "try (\"a\"|test(\"*a\")) catch .",
            "\"Regex failure: target of repeat operator is not specified\""
        },
        {
            "try (\"a\"|test(\"a{2,1}\")) catch .",
            "\"Regex failure: upper is smaller than lower in repeat range\""
        },
        {
            "try (\"a\"|test(\"(?z:a)\")) catch .",
            "\"Regex failure: undefined group option\""
        },
        {
            "try (\"a\"|test(\"(?<>a)\")) catch .",
            "\"Regex failure: group name is empty\""
        },
        {
            "try (\"a\"|test(\"\\\\k<missing>\")) catch .",
            "\"Regex failure: undefined name <missing> reference\""
        },
        {
            "try (\"a\"|test(\"[z-a]\")) catch .",
            "\"Regex failure: empty range in char class\""
        },
    };

    [Theory]
    [MemberData(nameof(OracleCases))]
    public void GrammarAndDiagnosticsMatchPinnedJq182(string filter, string expected)
    {
        var output = Assert.Single(JqProgram.Compile(filter).Execute("null"));
        using var expectedDocument = JsonDocument.Parse(expected);
        Assert.True(
            JsonElement.DeepEquals(expectedDocument.RootElement, output),
            $"expected {expectedDocument.RootElement.GetRawText()}, actual {output.GetRawText()}");
    }
}
