using DotNetJq.Compatibility.Regex;

namespace DotNetJq.Tests;

public sealed class OnigurumaCaseFoldTests
{
    [Theory]
    [InlineData("ß", "ss", 1)]
    [InlineData("SS", "ß", 2)]
    [InlineData("ﬃ", "FFI", 1)]
    [InlineData("FFI", "ﬃ", 3)]
    [InlineData("ς", "σ", 1)]
    [InlineData("Σ", "ς", 1)]
    [InlineData("İ", "i\u0307", 1)]
    [InlineData("i\u0307", "İ", 2)]
    [InlineData("𐐨", "𐐀", 2)]
    public void LiteralMatchesUsePinnedSimpleAndFullFolds(
        string input,
        string pattern,
        int consumed)
    {
        Assert.Contains(consumed, OnigurumaCaseFold.EnumerateLiteralMatches(input, 0, pattern));
    }

    [Theory]
    [InlineData("İ", "i")]
    [InlineData("ı", "I")]
    [InlineData("ß", "s")]
    [InlineData("ﬃ", "f")]
    public void LiteralMatchesDoNotUseTurkicOrPartialFullFolds(string input, string pattern)
    {
        Assert.Empty(OnigurumaCaseFold.EnumerateLiteralMatches(input, 0, pattern));
    }

    [Fact]
    public void LiteralMatchingComposesFullFoldsInsideLongerRuns()
    {
        Assert.Contains(3, OnigurumaCaseFold.EnumerateLiteralMatches("aßb", 0, "assb"));
        Assert.Contains(4, OnigurumaCaseFold.EnumerateLiteralMatches("aSSb", 0, "aßb"));
    }

    [Fact]
    public void PositiveClassesCanConsumeFullFoldsAndSupplementaryRanges()
    {
        Assert.Contains(
            2,
            OnigurumaCaseFold.EnumerateClassMatches("SS", 0, scalar => scalar == 'ß', false));
        Assert.Contains(
            3,
            OnigurumaCaseFold.EnumerateClassMatches("FFI", 0, scalar => scalar == 'ﬃ', false));
        Assert.Contains(
            2,
            OnigurumaCaseFold.EnumerateClassMatches(
                "𐐨",
                0,
                scalar => scalar is >= 0x10400 and <= 0x1044F,
                false));
        Assert.Empty(
            OnigurumaCaseFold.EnumerateClassMatches("ß", 0, scalar => scalar == 's', false));
    }

    [Fact]
    public void ClassesCloseSingletonEquivalentsButNegationStillConsumesOneScalar()
    {
        Assert.Contains(
            1,
            OnigurumaCaseFold.EnumerateClassMatches("ẞ", 0, scalar => scalar == 'ß', false));
        Assert.Empty(
            OnigurumaCaseFold.EnumerateClassMatches("ẞ", 0, scalar => scalar == 'ß', true));
        Assert.Contains(
            1,
            OnigurumaCaseFold.EnumerateClassMatches("SS", 0, scalar => scalar == 'ß', true));
        Assert.Contains(
            1,
            OnigurumaCaseFold.EnumerateClassMatches("A", 0, scalar => scalar == 'a', false));
    }

    [Theory]
    [InlineData("σ", "ς", true, 1)]
    [InlineData("𐐀", "𐐨", true, 2)]
    [InlineData("ß", "SS", false, 0)]
    [InlineData("ß", "ẞ", false, 0)]
    [InlineData("ﬅ", "ﬆ", true, 1)]
    [InlineData("İ", "i\u0307", false, 0)]
    [InlineData("SS", "SS", true, 2)]
    public void BackreferencesFoldActualCapturedTextOnly(
        string capture,
        string input,
        bool expected,
        int expectedConsumed)
    {
        var matched = OnigurumaCaseFold.TryMatchBackreference(
            capture,
            input,
            0,
            out var consumed);

        Assert.Equal(expected, matched);
        Assert.Equal(expectedConsumed, consumed);
    }

    [Fact]
    public void LiteralFirstByteMapRejectsMultibyteFoldHazards()
    {
        Assert.True(OnigurumaCaseFold.TryGetFirstByteMap("abc", out var safe));
        Assert.Equal(1, safe['a']);
        Assert.Equal(1, safe['A']);

        Assert.False(OnigurumaCaseFold.TryGetFirstByteMap("ss", out _));
        Assert.False(OnigurumaCaseFold.TryGetFirstByteMap("k", out _));
        Assert.False(OnigurumaCaseFold.TryGetFirstByteMap("s", out _));
        Assert.False(OnigurumaCaseFold.TryGetFirstByteMap("ß", out _));
    }

    [Fact]
    public void ClassFirstByteMapRequiresSafePositiveAsciiRanges()
    {
        Assert.True(OnigurumaCaseFold.TryGetFirstByteMap([('a', 'c')], false, out var safe));
        Assert.Equal(1, safe['a']);
        Assert.Equal(1, safe['A']);
        Assert.Equal(1, safe['c']);
        Assert.Equal(1, safe['C']);

        Assert.False(OnigurumaCaseFold.TryGetFirstByteMap([('a', 'z')], false, out _));
        Assert.False(OnigurumaCaseFold.TryGetFirstByteMap([('a', 'c')], true, out _));
        Assert.False(OnigurumaCaseFold.TryGetFirstByteMap([(0x10400, 0x1044F)], false, out _));
    }
}
