using DotNetJq.Compatibility.Regex;

namespace DotNetJq.Tests;

public sealed class OnigurumaCalloutDataTests
{
    [Fact]
    public void ArgumentLexerMirrorsPinnedEmptyFieldAndEscapeRules()
    {
        var empty = OnigurumaCalloutArgumentParser.Parse("{,,,}", 0);
        Assert.Empty(empty.Arguments);
        Assert.Equal(4, empty.CloseBrace);

        var parsed = OnigurumaCalloutArgumentParser.Parse("{1,,==,,1}", 0);
        Assert.Equal(["1", "==", "1"], parsed.Arguments.Select(argument => argument.Value));
        Assert.All(parsed.Arguments, argument => Assert.False(argument.HadEscape));

        var escaped = OnigurumaCalloutArgumentParser.Parse("{\\,,\\},\\\\,x\\q}", 0);
        Assert.Equal([",", "}", "\\", "x\\q"], escaped.Arguments.Select(argument => argument.Value));
        Assert.All(escaped.Arguments, argument => Assert.True(argument.HadEscape));
    }

    [Fact]
    public void LongAndIdentifierParsingMirrorsPinnedRegparseRules()
    {
        Assert.True(OnigurumaCalloutArgumentParser.TryParseLong("-4294967299", out var wrapped));
        Assert.Equal(-4_294_967_299L, wrapped);
        Assert.True(OnigurumaCalloutArgumentParser.TryParseLong("+12", out var positive));
        Assert.Equal(12, positive);
        Assert.True(OnigurumaCalloutArgumentParser.TryParseLong("+", out var positiveSign));
        Assert.Equal(0, positiveSign);
        Assert.True(OnigurumaCalloutArgumentParser.TryParseLong("-", out var negativeSign));
        Assert.Equal(0, negativeSign);
        Assert.False(OnigurumaCalloutArgumentParser.TryParseLong("-9223372036854775808", out _));
        Assert.False(OnigurumaCalloutArgumentParser.TryParseLong(" 2", out _));

        Assert.True(OnigurumaCalloutArgumentParser.IsAllowedName("FAIL2"));
        Assert.True(OnigurumaCalloutArgumentParser.IsAllowedTag("A_2"));
        Assert.False(OnigurumaCalloutArgumentParser.IsAllowedName("2FAIL"));
        Assert.False(OnigurumaCalloutArgumentParser.IsAllowedTag("A-2"));
    }

    [Fact]
    public void ErrorMessagesMirrorPinnedRegerrorTable()
    {
        Assert.Equal("undefined type (bug)", OnigurumaCalloutErrorData.Format(-6));
        Assert.Equal("match-stack limit over", OnigurumaCalloutErrorData.Format(-15));
        Assert.Equal("target of repeat operator is invalid", OnigurumaCalloutErrorData.Format(-114));
        Assert.Equal("undefined callout name", OnigurumaCalloutErrorData.Format(-229));
        Assert.Equal("invalid callout arg", OnigurumaCalloutErrorData.Format(-232));
        Assert.Equal("library is not initialized", OnigurumaCalloutErrorData.Format(-500));
        Assert.Equal("undefined error code", OnigurumaCalloutErrorData.Format(-999));
        Assert.True(OnigurumaCalloutErrorData.NeedsParameter(-217));
        Assert.False(OnigurumaCalloutErrorData.NeedsParameter(-232));
    }
}
