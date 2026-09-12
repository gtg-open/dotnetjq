using DotNetJq.Compatibility.Regex;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace DotNetJq.Tests;

/// <summary>
/// Pinned jq-1.8.2/Oniguruma Unicode 16.0 GB3-GB13 boundaries at and beyond
/// the former managed input-size cutoff.
/// </summary>
[Collection(RegexTextSegmentationPerformanceGroup.Name)]
public sealed class RegexTextSegmentationExactnessTests
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(500);

    [Fact]
    public void GeneratedUnicode16BreakTableRetainsPinnedStructure()
    {
        Assert.Equal(
            "21663445ace4f64775506f3fc53332a96e1b2b9f509b63eeb5462913daeb6d73",
            OnigurumaExtendedGraphemeData.UpstreamSourceSha256);
        Assert.Equal(1_376, OnigurumaExtendedGraphemeData.UpstreamRangeCount);
        Assert.Equal(1, OnigurumaExtendedGraphemeData.GetRangeCount(OnigurumaGraphemeBreakType.Cr));
        Assert.Equal(1, OnigurumaExtendedGraphemeData.GetRangeCount(OnigurumaGraphemeBreakType.Lf));
        Assert.Equal(19, OnigurumaExtendedGraphemeData.GetRangeCount(OnigurumaGraphemeBreakType.Control));
        Assert.Equal(376, OnigurumaExtendedGraphemeData.GetRangeCount(OnigurumaGraphemeBreakType.Extend));
        Assert.Equal(16, OnigurumaExtendedGraphemeData.GetRangeCount(OnigurumaGraphemeBreakType.Prepend));
        Assert.Equal(1, OnigurumaExtendedGraphemeData.GetRangeCount(OnigurumaGraphemeBreakType.RegionalIndicator));
        Assert.Equal(155, OnigurumaExtendedGraphemeData.GetRangeCount(OnigurumaGraphemeBreakType.SpacingMark));
        Assert.Equal(1, OnigurumaExtendedGraphemeData.GetRangeCount(OnigurumaGraphemeBreakType.Zwj));
        Assert.Equal(2, OnigurumaExtendedGraphemeData.GetRangeCount(OnigurumaGraphemeBreakType.L));
        Assert.Equal(399, OnigurumaExtendedGraphemeData.GetRangeCount(OnigurumaGraphemeBreakType.Lv));
        Assert.Equal(399, OnigurumaExtendedGraphemeData.GetRangeCount(OnigurumaGraphemeBreakType.Lvt));
        Assert.Equal(2, OnigurumaExtendedGraphemeData.GetRangeCount(OnigurumaGraphemeBreakType.T));
        Assert.Equal(4, OnigurumaExtendedGraphemeData.GetRangeCount(OnigurumaGraphemeBreakType.V));
    }

    [Theory]
    [InlineData(16_384)]
    [InlineData(16_385)]
    public void PrependRemainsJoinedAcrossTheFormerCutoff(int utf16Length)
    {
        var input = new string('x', utf16Length - 2) + "\u0600a";

        Assert.Equal("\u0600a", Assert.Single(JqRegex.Match(input, "\\X$", timeout: MatchTimeout)).String);
        Assert.True(JqRegex.Test(input, "\u0600\\Ya$", timeout: MatchTimeout));
        Assert.False(JqRegex.Test(input, "\u0600\\ya$", timeout: MatchTimeout));
    }

    [Theory]
    [InlineData(16_384)]
    [InlineData(16_385)]
    public void NonPictographicZwjDoesNotJoinTheFollowingScalar(int utf16Length)
    {
        var input = new string('x', utf16Length - 3) + "a\u200Db";

        Assert.Equal("b", Assert.Single(JqRegex.Match(input, "\\X$", timeout: MatchTimeout)).String);
        Assert.True(JqRegex.Test(input, "a\u200D\\yb$", timeout: MatchTimeout));
        Assert.False(JqRegex.Test(input, "a\u200D\\Yb$", timeout: MatchTimeout));
    }

    [Theory]
    [InlineData(16_384)]
    [InlineData(16_385)]
    public void RegionalIndicatorParityRemainsExactAcrossTheFormerCutoff(int utf16Length)
    {
        var input = new string('x', utf16Length - 6) + "🇦🇧🇨";

        Assert.Equal("🇨", Assert.Single(JqRegex.Match(input, "\\X$", timeout: MatchTimeout)).String);
        Assert.True(JqRegex.Test(input, "🇦🇧\\y🇨$", timeout: MatchTimeout));
        Assert.False(JqRegex.Test(input, "🇦🇧\\Y🇨$", timeout: MatchTimeout));
    }

    [Theory]
    [InlineData(16_384)]
    [InlineData(16_385)]
    public void HangulCompositionRemainsExactAcrossTheFormerCutoff(int utf16Length)
    {
        var input = new string('x', utf16Length - 3) + "각";

        Assert.Equal("각", Assert.Single(JqRegex.Match(input, "\\X$", timeout: MatchTimeout)).String);
        Assert.True(JqRegex.Test(input, "ᄀ\\Yᅡ\\Yᆨ$", timeout: MatchTimeout));
        Assert.False(JqRegex.Test(input, "ᄀ\\yᅡ\\Yᆨ$", timeout: MatchTimeout));
    }

    [Fact]
    public void CompactSegmentationHandlesMateriallyLargerInputWithinTheMatchBudget()
    {
        var input = new string('x', 100_000) + "\u0600a";

        Assert.Equal("\u0600a", Assert.Single(JqRegex.Match(input, "\\X$", timeout: MatchTimeout)).String);
        Assert.True(JqRegex.Test(input, "\u0600\\Ya$", timeout: MatchTimeout));
        Assert.Equal(2, OnigurumaTextSegmentation.GetClusterLength(input, 100_000));
        Assert.True(OnigurumaTextSegmentation.IsNonBoundary(input, 100_001));

        AssertCompiledClusterRunnerSelection("\\X$", null, 16_383, false);
        AssertCompiledClusterRunnerSelection("\\X$", null, 16_384, true);
        AssertCompiledClusterRunnerSelection("\\Y$", null, 100_000, false);
        AssertCompiledClusterRunnerSelection("\\\\X", null, 100_000, false);
        AssertCompiledClusterRunnerSelection("\\Q\\X\\E", null, 100_000, false);
        AssertCompiledClusterRunnerSelection("# \\X\nX", "x", 100_000, false);
    }

    private static void AssertCompiledClusterRunnerSelection(
        string pattern,
        string? modifiers,
        int inputLength,
        bool expectedWhenDynamicCodeIsCompiled)
    {
        var createExecution = typeof(JqRegex).GetMethod(
            "CreateExecution",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(createExecution);

        var execution = createExecution.Invoke(
            null,
            [new string('x', inputLength), pattern, modifiers, MatchTimeout, false]);
        Assert.NotNull(execution);
        var optionsProperty = execution.GetType().GetProperty("Options");
        Assert.NotNull(optionsProperty);
        var options = Assert.IsType<RegexOptions>(optionsProperty.GetValue(execution));

        Assert.Equal(
            expectedWhenDynamicCodeIsCompiled && RuntimeFeature.IsDynamicCodeCompiled,
            options.HasFlag(RegexOptions.Compiled));
    }

    [Theory]
    [InlineData("", 0, 0)]
    [InlineData("\r\n", 0, 2)]
    [InlineData("\r\n", 1, 1)]
    [InlineData("g\u0308", 0, 2)]
    [InlineData("g\u0308", 1, 1)]
    [InlineData("\u0600a", 0, 2)]
    [InlineData("\u0600a", 1, 1)]
    [InlineData("각", 0, 3)]
    [InlineData("각", 1, 2)]
    [InlineData("〰̂‍⭕", 0, 4)]
    [InlineData("〰̂‍⭕", 1, 3)]
    [InlineData("🇦🇧🇨", 0, 4)]
    [InlineData("🇦🇧🇨", 2, 2)]
    [InlineData("🇦🇧🇨", 4, 2)]
    public void DirectClusterLengthMatchesAtomicXFromArbitraryScalarHead(
        string input,
        int charIndex,
        int expectedLength)
    {
        Assert.Equal(
            expectedLength,
            OnigurumaTextSegmentation.GetClusterLength(input, charIndex));
    }

    [Fact]
    public void DirectBoundaryQueriesPreservePinnedAdjacentAndHistoryRules()
    {
        Assert.True(OnigurumaTextSegmentation.IsBoundary(string.Empty, 0));
        Assert.True(OnigurumaTextSegmentation.IsBoundary("a", 0));
        Assert.True(OnigurumaTextSegmentation.IsBoundary("a", 1));

        Assert.True(OnigurumaTextSegmentation.IsNonBoundary("\r\n", 1));
        Assert.True(OnigurumaTextSegmentation.IsNonBoundary("\u0600a", 1));
        Assert.True(OnigurumaTextSegmentation.IsBoundary("\u0600\r", 1));
        Assert.True(OnigurumaTextSegmentation.IsNonBoundary("a\u200Db", 1));
        Assert.True(OnigurumaTextSegmentation.IsBoundary("a\u200Db", 2));

        const string extendedPictographic = "〰̂‍⭕";
        Assert.True(OnigurumaTextSegmentation.IsNonBoundary(extendedPictographic, 1));
        Assert.True(OnigurumaTextSegmentation.IsNonBoundary(extendedPictographic, 2));
        Assert.True(OnigurumaTextSegmentation.IsNonBoundary(extendedPictographic, 3));

        const string regionalIndicators = "🇦🇧🇨";
        Assert.True(OnigurumaTextSegmentation.IsNonBoundary(regionalIndicators, 2));
        Assert.True(OnigurumaTextSegmentation.IsBoundary(regionalIndicators, 4));

        const string hangul = "각";
        Assert.True(OnigurumaTextSegmentation.IsNonBoundary(hangul, 1));
        Assert.True(OnigurumaTextSegmentation.IsNonBoundary(hangul, 2));
    }

    [Fact]
    public void DirectQueriesRejectNonScalarUtf16Positions()
    {
        const string supplementary = "😀";
        Assert.Throws<ArgumentOutOfRangeException>(
            () => OnigurumaTextSegmentation.GetClusterLength(supplementary, 1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => OnigurumaTextSegmentation.IsBoundary(supplementary, -1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => OnigurumaTextSegmentation.IsNonBoundary(supplementary, 3));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => OnigurumaTextSegmentation.GetClusterLength("\uD800", 0));
    }
}

/// <summary>
/// The 500 ms assertion exercises the regex engine's wall-clock match budget.
/// Running it alongside the rest of the CPU-heavy suite makes scheduler delay
/// indistinguishable from regex work, so retain the budget while removing that
/// source of test-runner noise.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RegexTextSegmentationPerformanceGroup
{
    internal const string Name = nameof(RegexTextSegmentationPerformanceGroup);
}
