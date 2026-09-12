using System.Text;
using DotNetJq.Compatibility.Time;
using DotNetJq.Port;

namespace DotNetJq.Tests;

public sealed class StrptimeRegexProxyTests
{
    [Fact]
    public void MatchReturnsJqStrptimeFieldsAndWhitespaceTail()
    {
        var match = JqStrptimeRegex.Match(
            "2024-February-03 11:05:06 PM trailing",
            "%Y-%B-%d %I:%M:%S %p");

        Assert.True(match.Success);
        Assert.Equal(2024, match.Year);
        Assert.Equal("February", match.MonthName);
        Assert.Equal(3, match.Day);
        Assert.Equal(11, match.Hour12);
        Assert.Equal(5, match.Minute);
        Assert.Equal(6, match.Second);
        Assert.Equal("PM", match.AmPm);
        Assert.Equal(" trailing", match.Tail);
    }

    [Fact]
    public void MatchKeepsNativeEpochAndLiteralScanningInsideTheProxy()
    {
        var epoch = JqStrptimeRegex.Match("1709247907", "%s");
        var escapedLiteral = JqStrptimeRegex.Match("2024.02.03", "%Y.%m.%d");

        Assert.True(epoch.Success);
        Assert.Equal(2024, epoch.Year);
        Assert.Equal(2, epoch.Month);
        Assert.Equal(29, epoch.Day);
        Assert.False(JqStrptimeRegex.Match("-0.25", "%s").Success);
        Assert.True(escapedLiteral.Success);
        Assert.Equal(2024, escapedLiteral.Year);
        Assert.Equal(2, escapedLiteral.Month);
        Assert.Equal(3, escapedLiteral.Day);
        Assert.False(JqStrptimeRegex.Match("2024x02x03", "%Y.%m.%d").Success);
    }

    [Fact]
    public void UnsupportedDirectiveFailsLikeNativeStrptime()
    {
        Assert.False(JqStrptimeRegex.Match("anything", "%Q").Success);
    }

    [Theory]
    [InlineData("NaN1")]
    [InlineData("-NaN01")]
    [InlineData("sNaN7")]
    [InlineData("-sNaN007")]
    [InlineData("nan9")]
    [InlineData("-nan42")]
    public void NonzeroNaNPayloadSpellingsAreRejectedByTheDirectJvParser(string source)
    {
        var parsed = libjq.jv_parse_sized(Encoding.UTF8.GetBytes(source));
        try
        {
            Assert.False(parsed.IsValid);
            var message = libjq.jv_invalid_get_msg(libjq.jv_copy(parsed));
            try
            {
                Assert.StartsWith(
                    "Invalid numeric literal",
                    message.StringValue,
                    StringComparison.Ordinal);
            }
            finally
            {
                libjq.jv_free(message);
            }
        }
        finally
        {
            libjq.jv_free(parsed);
        }
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("-NaN")]
    public void ZeroPayloadNaNSpellingsRemainValidDirectJvNumbers(string source)
    {
        var parsed = libjq.jv_parse_sized(Encoding.UTF8.GetBytes(source));
        try
        {
            Assert.True(parsed.IsValid);
            Assert.Equal(jv_kind.JV_KIND_NUMBER, parsed.Kind);
            Assert.True(double.IsNaN(parsed.NumberValue));
        }
        finally
        {
            libjq.jv_free(parsed);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("+NaN1")]
    [InlineData("SNaN1")]
    [InlineData("NaN1x")]
    [InlineData("NaN١")]
    [InlineData(" NaN1")]
    public void OtherMalformedNaNLikeTokensRemainInvalidDirectJvInput(string source)
    {
        var parsed = libjq.jv_parse_sized(Encoding.UTF8.GetBytes(source));
        try
        {
            Assert.False(parsed.IsValid);
            Assert.True(libjq.jv_invalid_has_msg(libjq.jv_copy(parsed)));
        }
        finally
        {
            libjq.jv_free(parsed);
        }
    }
}
