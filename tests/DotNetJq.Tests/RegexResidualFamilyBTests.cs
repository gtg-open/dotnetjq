using DotNetJq.Compatibility.Regex;
using DotNetJq.Port;

namespace DotNetJq.Tests;

/// <summary>
/// Exact jq-1.8.2/Oniguruma cases for ineffective escapes, brace escape
/// sequences, inline full case folding, and zero-width/look-behind diagnostics.
/// </summary>
public sealed class RegexResidualFamilyBTests
{
    [Fact]
    public void PerlNgIneffectiveEscapesMatchTheirLiteralCharacters()
    {
        Assert.True(JqRegex.Test("u", "\\u"));
        Assert.True(JqRegex.Test("u0041", "\\u0041"));
        Assert.True(JqRegex.Test("C", "\\C"));
        Assert.True(JqRegex.Test("Cfoo", "\\Cfoo"));
        Assert.True(JqRegex.Test("P", "\\P"));
        Assert.True(JqRegex.Test("Pfoo", "\\Pfoo"));

        Assert.True(JqRegex.Test("u", "[\\u]"));
        Assert.True(JqRegex.Test("C", "[\\C]"));
        Assert.True(JqRegex.Test("P", "[\\P]"));
        Assert.True(JqRegex.Test("u", "[\\u0041]"));
        Assert.True(JqRegex.Test("0", "[\\u0041]"));
        Assert.False(JqRegex.Test("A", "[\\u0041]"));

        // A syntactically complete property remains effective.
        Assert.True(JqRegex.Test("1", "\\P{L}"));
    }

    [Fact]
    public void BraceRadixSequencesExpandToCodePointsAndClassUnions()
    {
        Assert.Equal(
            "AB",
            Assert.Single(JqRegex.Match("AB", "\\x{41 42}")).String);
        Assert.Equal(
            "AB",
            Assert.Single(JqRegex.Match("AB", "\\o{101 102}")).String);
        Assert.Equal(
            "😀A",
            Assert.Single(JqRegex.Match("😀A", "\\x{1F600 41}")).String);

        Assert.Equal(
            ["AB"],
            JqRegex.Scan("AB", "[\\x{41 42}]+").Select(match => match.String));
        Assert.Equal(
            ["A", "😀"],
            JqRegex.Scan("A😀B", "[\\x{41 1F600}]").Select(match => match.String));
        Assert.Equal(
            ["A", "😀"],
            JqRegex.Scan("A😀B", "[\\o{101 373000}]").Select(match => match.String));
    }

    [Fact]
    public void RadixSequencesAcceptOnlyOnigurumaSpaceAndLineFeedDividers()
    {
        Assert.Equal("AB", Assert.Single(JqRegex.Match("AB", "\\x{41\n42}")).String);
        Assert.Equal("AB", Assert.Single(JqRegex.Match("AB", "\\o{101\n102}")).String);
        Assert.Equal(["A", "B"], JqRegex.Scan("AB", "[\\x{41\n42}]").Select(match => match.String));
        Assert.Equal(["A", "B"], JqRegex.Scan("AB", "[\\o{101\n102}]").Select(match => match.String));

        AssertRegexFailure("\\x{41\t42}", "invalid code point value");
        AssertRegexFailure("\\o{101\t102}", "invalid code point value");
        AssertRegexFailure("\\x{41 }", "invalid code point value");
        AssertRegexFailure("\\o{101 }", "invalid code point value");
    }

    [Fact]
    public void OnigurumaExtendedCodePointsCompileAsUnmatchableValues()
    {
        foreach (var pattern in new[]
                 {
                     "\\x{D800}", "\\x{DFFF}", "\\x{110000}", "\\x{13FFFF}",
                     "\\o{154000}", "\\o{157777}", "\\o{4200000}", "\\o{4777777}",
                 })
        {
            Assert.False(JqRegex.Test("A😀", pattern));
        }

        Assert.False(JqRegex.Test("A😀", "[\\x{D800}]") );
        Assert.False(JqRegex.Test("A😀", "[\\x{1FFFFF}]") );
        Assert.True(JqRegex.Test("A", "[\\x{41 110000}]") );
        Assert.True(JqRegex.Test("A", "[^\\x{110000}]") );
        Assert.True(JqRegex.Test("😀", "[^\\x{110000}]") );

        AssertRegexFailure("\\x{140000}", "invalid code point value");
        AssertRegexFailure("\\o{5000000}", "invalid code point value");
        AssertRegexFailure("[\\x{200000}]", "invalid code point value");
        AssertRegexFailure("[\\o{10000000}]", "invalid code point value");
    }

    [Fact]
    public void CharacterClassBraceRangesUseOnigurumaRangeGrammar()
    {
        Assert.Equal(["A", "B", "C"], JqRegex.Scan("ABCDE", "[\\x{41-43}]").Select(match => match.String));
        Assert.Equal(["A", "B", "C"], JqRegex.Scan("ABCDE", "[\\o{101 - 103}]").Select(match => match.String));
        Assert.Equal(["A", "B", "C", "P", "P"], JqRegex.Scan("ABCDEPP", "[\\x{41-43 50}]").Select(match => match.String));
        Assert.True(JqRegex.Test("\uE000", "[\\x{D800-E000}]") );

        AssertRegexFailure("[\\x{43-41}]", "empty range in char class");
        AssertRegexFailure("[\\o{103-101}]", "empty range in char class");
        AssertRegexFailure("\\x{41-43}", "invalid code point value");
    }

    [Fact]
    public void MalformedBraceEscapesFollowIneffectiveEscapeAndExactDiagnosticRules()
    {
        Assert.True(JqRegex.Test("x", "\\x"));
        Assert.True(JqRegex.Test("\u0004", "\\x4"));
        Assert.True(JqRegex.Test("x{", "\\x{"));
        Assert.True(JqRegex.Test("x{}", "\\x{}"));
        Assert.True(JqRegex.Test("x{ 41}", "\\x{ 41}"));
        Assert.True(JqRegex.Test("o{ 101}", "\\o{ 101}"));
        Assert.True(JqRegex.Test("o101", "\\o101"));
        Assert.True(JqRegex.Test("\u0000", "[\\x]") );
        Assert.True(JqRegex.Test("o1", "[\\o1]") );

        AssertRegexFailure("\\x{41", "invalid code point value");
        AssertRegexFailure("\\o{101", "invalid code point value");
        AssertRegexFailure("\\x{41,42}", "invalid code point value");
        AssertRegexFailure("\\o{101 8}", "invalid code point value");
        AssertRegexFailure("\\x{100000000}", "too long wide-char value");
        AssertRegexFailure("\\o{100000000000}", "too long wide-char value");
        AssertRegexFailure("\\o{40000000000}", "too big number");
    }

    [Fact]
    public void InlineIgnoreCaseControlsFullMultiCodePointFolding()
    {
        Assert.True(JqRegex.Test("SS", "(?i)ß"));
        Assert.False(JqRegex.Test("SS", "(?-i)ß", "i"));
        Assert.True(JqRegex.Test("ﬃ", "(?i)ffi"));
        Assert.True(JqRegex.Test("FFI", "(?i:ffi)"));
        Assert.False(JqRegex.Test("FFI", "(?-i:ffi)", "i"));
        Assert.True(JqRegex.Test("SSx", "(?i)ß(?-i)x"));

        Assert.True(JqRegex.Test("SSx", "(?i:ß)x"));
        Assert.False(JqRegex.Test("SSX", "(?i:ß)x"));
        Assert.True(JqRegex.Test("SSX", "(?i:ß)x", "i"));
    }

    [Theory]
    [InlineData("(?=a)*")]
    [InlineData("(?!b){5}")]
    [InlineData("^*")]
    [InlineData("(?=a){0}")]
    [InlineData("\\b*")]
    [InlineData("\\B+")]
    [InlineData("\\A?")]
    [InlineData("\\z{2}")]
    [InlineData("\\Z*")]
    [InlineData("(?<=a)?")]
    [InlineData("(?<!a){2}")]
    public void RepeatedZeroWidthTargetsUseOnigurumaDiagnostic(string pattern) =>
        AssertRegexFailure(pattern, "target of repeat operator is invalid");

    [Theory]
    [InlineData("(?<=a|bc)d")]
    [InlineData("(?<=ab|c)d")]
    [InlineData("(?<!a|bc)d")]
    [InlineData("(?<=a(?=b))b")]
    [InlineData("(?<=a(?:b|cd))e")]
    [InlineData("(?<=(?:a|bc))d")]
    [InlineData("(?<=a(b|cd))e")]
    public void InvalidLookBehindUsesOnigurumaDiagnostic(string pattern) =>
        AssertRegexFailure(pattern, "invalid pattern in look-behind");

    [Fact]
    public void EqualWidthAndSingleBranchLookBehindRemainValid()
    {
        Assert.True(JqRegex.Test("abd", "(?<=ab|cd)d"));
        Assert.True(JqRegex.Test("ac", "(?<=a|b)c"));
        Assert.True(JqRegex.Test("ab", "(?<=a*)b"));
        Assert.True(JqRegex.Test("ab", "(?<=a{1,2})b"));
    }

    private static void AssertRegexFailure(string pattern, string message)
    {
        var exception = Assert.Throws<JqRuntimeException>(() => JqRegex.Test("abcd", pattern));
        Assert.Equal("Regex failure: " + message, exception.Message);
    }
}
