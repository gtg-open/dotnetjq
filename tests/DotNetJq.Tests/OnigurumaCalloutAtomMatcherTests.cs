using DotNetJq.Compatibility.Regex;
using DotNetJq.Port;

namespace DotNetJq.Tests;

public sealed class OnigurumaCalloutAtomMatcherTests
{
    [Theory]
    [InlineData("\\x{1F600}", "😀", 2)]
    [InlineData("\\o{141 142}", "ab", 2)]
    [InlineData("\\x61", "a", 1)]
    [InlineData("\\Q.a+[]()\\E", ".a+[]()", 7)]
    public void LiteralLikeAtomsReusePinnedQuoteAndRadixGrammar(
        string pattern,
        string input,
        int expectedWidth)
    {
        var token = ParseWhole(pattern);
        var matcher = new OnigurumaCalloutAtomMatcher(input);

        Assert.True(matcher.TryMatch(token, 0, out var width));
        Assert.Equal(expectedWidth, width);
        Assert.Equal(expectedWidth, token.MinimumUtf16Length);
        Assert.Equal(expectedWidth, token.MaximumUtf16Length);
    }

    [Theory]
    [InlineData("\\p{L}", "é", true)]
    [InlineData("\\p{L}", "😀", false)]
    [InlineData("\\P{L}", "😀", true)]
    [InlineData("\\p{Emoji}", "😀", true)]
    [InlineData("\\p{^Emoji}", "😀", false)]
    [InlineData("\\w", "𐐀", true)]
    [InlineData("\\d", "𝟘", true)]
    [InlineData("\\S", "😀", true)]
    public void PropertiesUsePinnedOnigurumaUnicodeRanges(
        string pattern,
        string input,
        bool expected)
    {
        var token = ParseWhole(pattern);
        var matcher = new OnigurumaCalloutAtomMatcher(input);

        Assert.Equal(expected, matcher.TryMatch(token, 0, out var width));
        Assert.Equal(expected ? input.Length : 0, width);
    }

    [Fact]
    public void PosixMembersResolveThroughTheSamePinnedCatalogue()
    {
        Assert.True(OnigurumaCalloutAtomMatcher.TryParsePosixMember(
            "[:alpha:]",
            0,
            out var alpha));
        Assert.Equal(9, alpha.NextPatternIndex);
        Assert.True(new OnigurumaCalloutAtomMatcher("é")
            .TryMatch(alpha.Token, 0, out var alphaWidth));
        Assert.Equal(1, alphaWidth);

        Assert.True(OnigurumaCalloutAtomMatcher.TryParsePosixMember(
            "[:^digit:]",
            0,
            out var notDigit));
        Assert.True(new OnigurumaCalloutAtomMatcher("a")
            .TryMatch(notDigit.Token, 0, out var notDigitWidth));
        Assert.Equal(1, notDigitWidth);
        Assert.False(new OnigurumaCalloutAtomMatcher("0")
            .TryMatch(notDigit.Token, 0, out _));
    }

    [Fact]
    public void NewlineAndAnyScalarAtomsPreserveCrLfAndSupplementaryWidths()
    {
        var newline = ParseWhole("\\R");
        Assert.True(new OnigurumaCalloutAtomMatcher("\r\n")
            .TryMatch(newline, 0, out var newlineWidth));
        Assert.Equal(2, newlineWidth);

        var notNewline = ParseWhole("\\N");
        Assert.True(new OnigurumaCalloutAtomMatcher("\r")
            .TryMatch(notNewline, 0, out var notNewlineWidth));
        Assert.Equal(1, notNewlineWidth);
        Assert.False(new OnigurumaCalloutAtomMatcher("\n")
            .TryMatch(notNewline, 0, out _));

        var any = ParseWhole("\\O");
        Assert.True(new OnigurumaCalloutAtomMatcher("😀")
            .TryMatch(any, 0, out var anyWidth));
        Assert.Equal(2, anyWidth);
    }

    [Fact]
    public void WordBoundaryUsesPinnedWordMembershipAtScalarBoundaries()
    {
        var boundary = ParseWhole("\\b");
        var nonBoundary = ParseWhole("\\B");
        var matcher = new OnigurumaCalloutAtomMatcher("𐐀a!");

        Assert.True(matcher.TryMatch(boundary, 0, out var startWidth));
        Assert.Equal(0, startWidth);
        Assert.True(matcher.TryMatch(nonBoundary, 2, out var middleWidth));
        Assert.Equal(0, middleWidth);
        Assert.True(matcher.TryMatch(boundary, 3, out var endWidth));
        Assert.Equal(0, endWidth);
    }

    [Fact]
    public void TextAtomsReuseTheVerifiedExtendedGraphemePatterns()
    {
        var cluster = ParseWhole("\\X");
        var boundary = ParseWhole("\\y");
        var nonBoundary = ParseWhole("\\Y");
        var matcher = new OnigurumaCalloutAtomMatcher("á");

        Assert.True(matcher.TryMatch(cluster, 0, out var clusterWidth));
        Assert.Equal(2, clusterWidth);
        Assert.True(matcher.TryMatch(boundary, 0, out var boundaryWidth));
        Assert.Equal(0, boundaryWidth);
        Assert.True(matcher.TryMatch(nonBoundary, 1, out var nonBoundaryWidth));
        Assert.Equal(0, nonBoundaryWidth);
        Assert.True(matcher.TryMatch(boundary, 2, out var tailWidth));
        Assert.Equal(0, tailWidth);
    }

    [Fact]
    public void TextAtomsHonorTheSuppliedRunnerDeadline()
    {
        var cluster = ParseWhole("\\X");
        var matcher = new OnigurumaCalloutAtomMatcher("a", static () => TimeSpan.Zero);

        var exception = Assert.Throws<JqRuntimeException>(() =>
            matcher.TryMatch(cluster, 0, out _));

        Assert.Equal(
            "Regex failure: regular expression evaluation timed out",
            exception.Message);
    }

    [Fact]
    public void UnknownUnicodePropertyKeepsThePinnedDiagnostic()
    {
        var exception = Assert.Throws<JqRuntimeException>(() =>
            OnigurumaCalloutAtomMatcher.TryParseEscape(
                "\\p{NoSuch}",
                0,
                out _));

        Assert.Equal(
            "Regex failure: invalid character property name {NoSuch}",
            exception.Message);
    }

    private static OnigurumaCalloutAtomToken ParseWhole(string pattern)
    {
        Assert.True(OnigurumaCalloutAtomMatcher.TryParseEscape(pattern, 0, out var result));
        Assert.Equal(pattern.Length, result.NextPatternIndex);
        return result.Token;
    }
}
