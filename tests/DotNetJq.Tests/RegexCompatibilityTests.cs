using DotNetJq.Compatibility.Regex;
using DotNetJq.Port;

namespace DotNetJq.Tests;

public sealed class RegexCompatibilityTests
{
    [Fact]
    public void MatchReturnsGlobalJqShapedCaptures()
    {
        var matches = JqRegex.Match(
            "foo bar foo foo  foo",
            "foo (?<bar123>bar)? foo",
            "ig");

        Assert.Equal(2, matches.Count);
        Assert.Equal(0, matches[0].Offset);
        Assert.Equal(11, matches[0].Length);
        Assert.Equal("foo bar foo", matches[0].String);
        Assert.Equal(new JqRegexCapture(4, 3, "bar", "bar123"), Assert.Single(matches[0].Captures));

        Assert.Equal(12, matches[1].Offset);
        Assert.Equal(8, matches[1].Length);
        Assert.Equal("foo  foo", matches[1].String);
        Assert.Equal(new JqRegexCapture(-1, 0, null, "bar123"), Assert.Single(matches[1].Captures));
    }

    [Fact]
    public void MatchCountsUnicodeScalarOffsetsAndDotConsumesAstralScalar()
    {
        var bar = Assert.Single(JqRegex.Match("😀 ā bar", "bar"));
        Assert.Equal(5, bar.Offset);
        Assert.Equal(3, bar.Length);

        var emoji = Assert.Single(JqRegex.Match("😀", ".", "g"));
        Assert.Equal(0, emoji.Offset);
        Assert.Equal(1, emoji.Length);
        Assert.Equal("😀", emoji.String);
    }

    [Fact]
    public void MatchPreservesLexicalOrderForMixedNamedAndUnnamedGroups()
    {
        var match = Assert.Single(JqRegex.Match("ab", "(?<first>a)(b)"));

        Assert.Collection(
            match.Captures,
            first => Assert.Equal(new JqRegexCapture(0, 1, "a", "first"), first),
            second => Assert.Equal(new JqRegexCapture(1, 1, "b", null), second));
    }

    [Fact]
    public void TestMapsJqModifiersAndRejectsInvalidModifier()
    {
        Assert.False(JqRegex.Test("a\nb", "a.b"));
        Assert.True(JqRegex.Test("a\nb", "a.b", "m"));
        Assert.False(JqRegex.Test("a\nb", "a.b", "s"));
        Assert.True(JqRegex.Test("a\nb", "a.b", "p"));
        Assert.True(JqRegex.Test("xABCd", "a b c # ignored", "ix"));
        Assert.True(JqRegex.Test("ab", "a|ab", "l"));
        Assert.False(JqRegex.Test("abc", "( )*", "n"));

        var error = Assert.Throws<JqRuntimeException>(() => JqRegex.Test("abc", "a", "z"));
        Assert.Equal("z is not a valid modifier string", error.Message);
    }

    [Fact]
    public void ScanReturnsCaptureArraysOrWholeMatches()
    {
        var captures = JqRegex.Scan("abaabbaaabbb", "(a+)(b+)");
        Assert.Equal(3, captures.Count);
        Assert.Collection(
            captures[0].Captures!,
            first => Assert.Equal("a", first),
            second => Assert.Equal("b", second));
        Assert.Collection(
            captures[1].Captures!,
            first => Assert.Equal("aa", first),
            second => Assert.Equal("bb", second));
        Assert.Collection(
            captures[2].Captures!,
            first => Assert.Equal("aaa", first),
            second => Assert.Equal("bbb", second));

        var wholeMatches = JqRegex.Scan("abcdefabc", "c");
        Assert.Collection(
            wholeMatches,
            first => Assert.Equal("c", first.String),
            second => Assert.Equal("c", second.String));
    }

    [Fact]
    public void SplitIncludesEmptyHeadAndTailLikeJqSplits()
    {
        Assert.Collection(
            JqRegex.Split("ab", ""),
            first => Assert.Equal("", first),
            second => Assert.Equal("a", second),
            third => Assert.Equal("b", third),
            fourth => Assert.Equal("", fourth));
        Assert.Collection(
            JqRegex.Split("ab,cd, ef", ", *"),
            first => Assert.Equal("ab", first),
            second => Assert.Equal("cd", second),
            third => Assert.Equal("ef", third));
    }

    [Fact]
    public void SubAndGsubUseLiteralAndCaptureAwareReplacements()
    {
        Assert.Equal("$1aaaa", JqRegex.Sub("aaaaa", "a", "$1"));
        Assert.Equal("bbbbb", JqRegex.Gsub("aaaaa", "a", "b"));
        Assert.Equal(
            "a:1;b:2;",
            JqRegex.Gsub(
                "a1b2",
                "(?<d>\\d)",
                captures => ":" + captures["d"] + ";"));
        Assert.Equal("aaa", JqRegex.Gsub("a", "", "a"));
    }

    [Fact]
    public void MatchConvertsDirectlyToJvObjectShape()
    {
        var value = JqRegex.MatchAsJv("foo bar", "(bar)");
        var match = Assert.Single(value.ArrayValue);

        Assert.Equal(4, libjq.jv_object_get(match, "offset").NumberValue);
        Assert.Equal(3, libjq.jv_object_get(match, "length").NumberValue);
        Assert.Equal("bar", libjq.jv_object_get(match, "string").StringValue);
        var capture = Assert.Single(libjq.jv_object_get(match, "captures").ArrayValue);
        Assert.Equal(jv_kind.JV_KIND_NULL, libjq.jv_object_get(capture, "name").Kind);

        Assert.Equal(
            "[{\"offset\":0,\"length\":0,\"string\":\"\",\"captures\":[" +
            "{\"offset\":-1,\"string\":null,\"length\":0,\"name\":null}]}]",
            libjq.jv_dump_string(JqRegex.MatchAsJv("a", "( )*")));
        Assert.Equal(
            "[{\"offset\":0,\"length\":0,\"string\":\"\",\"captures\":[" +
            "{\"offset\":0,\"string\":\"\",\"length\":0,\"name\":null}]}]",
            libjq.jv_dump_string(JqRegex.MatchAsJv("a", "()")));
        Assert.Equal(
            "[{\"offset\":0,\"length\":1,\"string\":\"a\",\"captures\":[" +
            "{\"offset\":0,\"length\":1,\"string\":\"a\",\"name\":null}]}]",
            libjq.jv_dump_string(JqRegex.MatchAsJv("a", "(a)")));
    }

    [Fact]
    public void MatchRunsThroughPublicExecutionBoundary()
    {
        var outputs = JqProgram.Compile("match(\"(abc)+\"; \"g\")")
            .Execute("\"abc abc\"");

        Assert.Equal(2, outputs.Count);
        Assert.Equal(0, outputs[0].GetProperty("offset").GetInt32());
        Assert.Equal(3, outputs[0].GetProperty("length").GetInt32());
        Assert.Equal("abc", outputs[0].GetProperty("string").GetString());
        Assert.Equal(4, outputs[1].GetProperty("offset").GetInt32());
    }
}
