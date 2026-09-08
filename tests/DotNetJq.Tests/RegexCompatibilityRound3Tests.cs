using DotNetJq.Compatibility.Regex;
using DotNetJq.Port;

namespace DotNetJq.Tests;

/// <summary>
/// Adversarial cases captured from the pinned jq-1.8.2/Oniguruma oracle while
/// expanding the deterministic regex differential corpus.
/// </summary>
public sealed class RegexCompatibilityRound3Tests
{
    [Fact]
    public void FindNotEmptyBacktracksAtTheSameStart()
    {
        Assert.Equal(
            ["a", "a"],
            JqRegex.Match("aba", "|a", "gn").Select(match => match.String));
        Assert.Equal(
            ["a", "a"],
            JqRegex.Match("aba", "a*?", "gn").Select(match => match.String));
        Assert.Equal(
            ["aaa"],
            JqRegex.Scan("aaa", "|a+", "n").Select(match => match.String));
    }

    [Fact]
    public void PerlNgQuotedRadixPossessiveAndConditionalSyntaxIsTranslated()
    {
        Assert.Equal(
            [".a+[]()"],
            JqRegex.Scan("x.a+[]()y", "\\Q.a+[]()\\E").Select(match => match.String));
        Assert.Equal(["😀"], JqRegex.Scan("a😀b", "\\x{1F600}").Select(match => match.String));
        Assert.Equal(["a"], JqRegex.Scan("cat", "\\o{141}").Select(match => match.String));

        Assert.Empty(JqRegex.Scan("aaa", "a++a"));
        Assert.Equal(["aaa"], JqRegex.Scan("aaa", "a{1,2}+a").Select(match => match.String));
        Assert.Equal(["ab", "c"], JqRegex.Match("ab c", "(a)?(?(1)b|c)", "g")
            .Select(match => match.String));
    }

    [Fact]
    public void PosixClassesComposeAndConsumeUnicodeScalars()
    {
        Assert.Equal(
            ["A09", "F"],
            JqRegex.Scan("A09-zF", "[A-F[:digit:]]+").Select(match => match.String));
        Assert.Equal(
            ["a_b", "c.d"],
            JqRegex.Scan("a_b-c.d", "[[:alpha:]_.]+").Select(match => match.String));
        Assert.Equal(
            ["😀"],
            JqRegex.Scan("A😀 ", "[[:punct:]]+").Select(match => match.String));
    }

    [Fact]
    public void UnicodePropertiesUseOnigurumaCategoryAndScriptSemantics()
    {
        Assert.Equal(["Ⅻ"], JqRegex.Scan("Ⅻ½", "\\p{Alpha}+").Select(match => match.String));
        Assert.Equal(["Ⅻ½"], JqRegex.Scan("Ⅻ½", "\\p{Word}+").Select(match => match.String));
        Assert.Equal(["abc", "é", "Ⅻ"],
            JqRegex.Scan("abc 9 é_Ⅻ", "\\p{Latin}+").Select(match => match.String));
        Assert.Equal(["βΩ"], JqRegex.Scan("aβΩж", "\\p{Greek}+").Select(match => match.String));
    }

    [Fact]
    public void FullCaseFoldSpecialsMatchPinnedOniguruma()
    {
        Assert.Equal(["s", "S", "ſ"], JqRegex.Scan("sSſ", "s", "i").Select(match => match.String));
        Assert.Equal(["σ", "Σ", "ς"], JqRegex.Scan("σΣς", "σ", "i").Select(match => match.String));
        Assert.Equal(["ß", "SS", "ẞ"], JqRegex.Scan("ßSSẞ", "ß", "i").Select(match => match.String));
        Assert.Equal(["ff", "ﬀ"], JqRegex.Scan("ffﬀﬁ", "ff", "i").Select(match => match.String));
        Assert.Equal(["𐐀", "𐐨"], JqRegex.Scan("𐐀𐐨", "𐐀", "i").Select(match => match.String));
        Assert.Equal(["İ", "i̇"], JqRegex.Scan("İi̇", "İ", "i").Select(match => match.String));
        Assert.Equal(["ŉ", "ʼn", "ʼN"], JqRegex.Scan("ŉʼnʼN", "ŉ", "i").Select(match => match.String));
        Assert.Equal(["ﬓ", "մն", "ՄՆ"], JqRegex.Scan("ﬓմնՄՆ", "ﬓ", "i").Select(match => match.String));
        Assert.Equal(["ΐ", "ΐ", "Ϊ́"], JqRegex.Scan("ΐΐΪ́", "ΐ", "i").Select(match => match.String));
        Assert.Equal(["ß", "SS", "ẞ"], JqRegex.Scan("ßSSẞ", "[ß]", "i").Select(match => match.String));
        Assert.Equal(["ﬃ", "ffi", "FFI"], JqRegex.Scan("ﬃffiFFI", "[ﬃ]", "i").Select(match => match.String));
        Assert.Equal(["ﬃ"], JqRegex.Scan("ﬃﬀifﬁ", "ffi", "i").Select(match => match.String));
    }

    [Fact]
    public void WordBoundariesAndDotUseUtf8ScalarAndNewlineRules()
    {
        Assert.Equal(
            [0, 1, 1, 1, 1],
            JqRegex.Match("𐐀", "\\b", "g").Select(match => match.Offset));
        Assert.Empty(JqRegex.Match("𐐀", "\\B", "g"));
        Assert.Equal(["𐐀"], JqRegex.Scan("𐐀", "\\b\\w+\\b").Select(match => match.String));
        Assert.Equal(["\r"], JqRegex.Scan("\r\n", "^.$").Select(match => match.String));
    }

    [Fact]
    public void ManagedFallbacksCoverOnigurumaOnlyTokens()
    {
        Assert.Equal(["aa"], JqRegex.Match("aa", "(a)(?1)", "g").Select(match => match.String));

        var kept = Assert.Single(JqRegex.Match("ab", "a\\Kb"));
        Assert.Equal(1, kept.Offset);
        Assert.Equal("b", kept.String);
        Assert.Equal("d", Assert.Single(JqRegex.Match("acd", "(a\\Kb|ac\\Kd)")).String);
        var repeatedKeep = Assert.Single(JqRegex.Match("acababacab", "(a\\Kb|\\Kac\\K)*"));
        Assert.Equal(9, repeatedKeep.Offset);
        Assert.Equal("b", repeatedKeep.String);
        Assert.Equal(
            [""],
            JqRegex.Match("a", "a\\K", "gn").Select(match => match.String));
        Assert.Equal("aXxab", JqRegex.Sub("abxab", "a\\Kb", "X"));
        Assert.Equal("aXxaX", JqRegex.Gsub("abxab", "a\\Kb", "X"));
        Assert.Equal(["a", "xa", ""], JqRegex.Split("abxab", "a\\Kb"));

        Assert.Equal(["bbb", ""], JqRegex.Scan("bbb", "(?~a)").Select(match => match.String));
        Assert.Equal(["", ""], JqRegex.Scan("a", "(?~a)").Select(match => match.String));
        Assert.Equal("", Assert.Single(JqRegex.Match("A", "(?~)")).String);
        Assert.Equal("/* */", Assert.Single(JqRegex.Match("/* */ */", "/\\*(?~\\*/)\\*/")).String);
        var stopped = Assert.Single(JqRegex.Match("ABCa", "(?~XYZ|ABC)a"));
        Assert.Equal(1, stopped.Offset);
        Assert.Equal("BCa", stopped.String);
        Assert.Equal("a", Assert.Single(JqRegex.Match("aABCa", "(?~XYZ|ABC)a")).String);
        Assert.Equal(["a"], JqRegex.Scan("a", "(?{x})a").Select(match => match.String));
        Assert.Empty(JqRegex.Scan("a", "(*FAIL)a"));
        Assert.Equal(["123"], JqRegex.Scan("123", "(?(?{....})123|456)").Select(match => match.String));
        Assert.Equal(["456"], JqRegex.Scan("456", "(?(*FAIL)123|456)").Select(match => match.String));
        Assert.Equal(["á"], JqRegex.Scan("á", "\\X").Select(match => match.String));
        Assert.Equal([0, 2], JqRegex.Match("á", "\\y", "g").Select(match => match.Offset));
        Assert.Equal([1], JqRegex.Match("á", "\\Y", "g").Select(match => match.Offset));
        Assert.Equal(
            [0, 1, 1, 1, 2, 3, 3, 3, 4],
            JqRegex.Match("🇺🇳👍🏽", "\\y", "g").Select(match => match.Offset));
        Assert.Equal(
            [0, 1, 1, 1, 3, 4],
            JqRegex.Match("👩‍💻!", "\\y", "g").Select(match => match.Offset));
        Assert.Equal(
            ["🇺🇳", "👍🏽"],
            JqRegex.Scan("🇺🇳👍🏽", "\\X").Select(match => match.String));
        Assert.Equal(["😀"], JqRegex.Scan("a😀", "\\p{Emoji}").Select(match => match.String));
        Assert.Equal(
            ["👍"],
            JqRegex.Scan("🇺🇳👍🏽", "\\p{Extended_Pictographic}+").Select(match => match.String));
        Assert.Equal(
            ["◀", "⭐", "⬅", "🇦", "🏽"],
            JqRegex.Scan("⌁◀★⭐⬀⬅🀀🇦🏽", "\\p{emoji}").Select(match => match.String));
        Assert.Equal(
            ["◀★⭐", "⬅🀀"],
            JqRegex.Scan("⌁◀★⭐⬀⬅🀀🇦🏽", "\\p{extended_pictographic}+")
                .Select(match => match.String));
        Assert.Equal(["🏽"], JqRegex.Scan("👍🏽", "\\p{Emoji_Modifier}").Select(match => match.String));
        Assert.Equal(["👍"], JqRegex.Scan("👍🏽", "\\p{Emoji_Modifier_Base}").Select(match => match.String));
        Assert.Equal(["⭐"], JqRegex.Scan("★⭐", "\\p{Emoji_Presentation}").Select(match => match.String));
    }

    [Fact]
    public void TransactionalOnigurumaCalloutsPreserveBacktrackingState()
    {
        var max = Assert.Single(JqRegex.Match("abcbaaccaaa", "(?:[ab]|(*MAX{2}).)*"));
        Assert.Equal((0, 7, "abcbaac"), (max.Offset, max.Length, max.String));

        var count = Assert.Single(JqRegex.Match(
            "abababcdab",
            "(?:(*COUNT[AB]{X})[ab]|(*COUNT[CD]{X})[cd])*(*CMP{AB,<,CD})"));
        Assert.Equal((5, 3, "bcd"), (count.Offset, count.Length, count.String));

        var exactThree = Assert.Single(JqRegex.Match(
            "aaaa",
            "(?:(*COUNT[A]{X})a)*(*CMP{A,==,3})"));
        Assert.Equal((0, 3, "aaa"), (exactThree.Offset, exactThree.Length, exactThree.String));

        var dynamicMax = Assert.Single(JqRegex.Match(
            "aaccc",
            "(?:(*COUNT[T]{X})a)*(?:(*MAX{T})c)*"));
        Assert.Equal((0, 4, "aacc"), (dynamicMax.Offset, dynamicMax.Length, dynamicMax.String));
    }

    [Fact]
    public void CalloutCmpSupportsEveryPinnedOperator()
    {
        Assert.True(JqRegex.Test(string.Empty, "(*CMP{1,==,1})"));
        Assert.True(JqRegex.Test(string.Empty, "(*CMP{1,!=,2})"));
        Assert.True(JqRegex.Test(string.Empty, "(*CMP{2,>,1})"));
        Assert.True(JqRegex.Test(string.Empty, "(*CMP{1,<,2})"));
        Assert.True(JqRegex.Test(string.Empty, "(*CMP{2,>=,2})"));
        Assert.True(JqRegex.Test(string.Empty, "(*CMP{2,<=,2})"));
        Assert.False(JqRegex.Test(string.Empty, "(*CMP{1,==,2})"));
        Assert.False(JqRegex.Test(string.Empty, "(*CMP{1,!=,1})"));
        Assert.False(JqRegex.Test(string.Empty, "(*CMP{1,>,2})"));
        Assert.False(JqRegex.Test(string.Empty, "(*CMP{2,<,1})"));
        Assert.False(JqRegex.Test(string.Empty, "(*CMP{1,>=,2})"));
        Assert.False(JqRegex.Test(string.Empty, "(*CMP{2,<=,1})"));
    }

    [Fact]
    public void ErrorAndMismatchCalloutsAreReachabilitySensitiveControlEvents()
    {
        Assert.True(JqRegex.Test("a", "z(*ERROR)|a"));
        var abort = Assert.Throws<JqRuntimeException>(() => JqRegex.Test("a", "a(*ERROR)|a"));
        Assert.Equal("Regex failure: abort", abort.Message);
        var suffixAbort = Assert.Throws<JqRuntimeException>(() => JqRegex.Test("a", "a(*ERROR)z|a"));
        Assert.Equal("Regex failure: abort", suffixAbort.Message);

        Assert.False(JqRegex.Test("a", "a(*MISMATCH)|a"));
        Assert.False(JqRegex.Test("b", "(?(*MISMATCH)a|b)"));
        Assert.True(JqRegex.Test("b", "(?(*FAIL)a|b)"));
        Assert.True(JqRegex.Test("b", "(?(*CMP{2,<,1})a|b)"));
        Assert.True(JqRegex.Test("a", "(?(*CMP{1,<,2})a|b)"));
        Assert.True(JqRegex.Test("b", "(?(*MAX{0})a|b)"));
        Assert.True(JqRegex.Test("a", "(?(*MAX{1})a|b)"));
        Assert.True(JqRegex.Test("ab", "^(?:(?(*MAX{1})a|b))+$"));
        Assert.False(JqRegex.Test("aa", "^(?:(?(*MAX{1})a|b))+$"));
        Assert.True(JqRegex.Test("b", "a(*FAIL)|b"));

        var allocation = Assert.Throws<JqRuntimeException>(() => JqRegex.Test("a", "a(*ERROR{-5})"));
        Assert.Equal("Regex failure: fail to memory allocation", allocation.Message);
        Assert.False(JqRegex.Test("a", "a(*ERROR{-1})|a"));

        var longestAfterMismatch = Assert.Single(JqRegex.Match(
            "aaab",
            "aa(*MISMATCH)b|a+",
            "l"));
        Assert.Equal((2, 1, "a"),
            (longestAfterMismatch.Offset, longestAfterMismatch.Length, longestAfterMismatch.String));
    }

    [Fact]
    public void SkipCalloutRaisesTheNextCandidateFloorAndResetsAfterEachSearch()
    {
        Assert.Empty(JqRegex.Match("aac", "aa(*SKIP)b|ac"));
        Assert.Equal("ac", Assert.Single(JqRegex.Match("aac", "aab|ac")).String);
        Assert.Equal("a", Assert.Single(JqRegex.Match("aaab", "aa(*SKIP)b|a")).String);
        Assert.Equal(
            ["aac", "aac"],
            JqRegex.Scan("aacaac", "aa(*SKIP)c|ac").Select(match => match.String));

        var longest = Assert.Single(JqRegex.Match("aaaXXXX", "aaa(*SKIP)|aaX+", "l"));
        Assert.Equal((0, 3, "aaa"), (longest.Offset, longest.Length, longest.String));
    }

    [Fact]
    public void CalloutLexicalIdentitySurvivesSubexpressionExpansion()
    {
        var transactional = Assert.Single(JqRegex.Match(
            "b",
            "(?<x>(*MAX{1,X})b)c|\\g<x>"));
        Assert.Equal("b", transactional.String);

        var retractionOnly = Assert.Single(JqRegex.Match("aaab", "(?:(*MAX{0,<})a)*ab"));
        Assert.Equal("aaab", retractionOnly.String);
    }

    [Fact]
    public void CalloutSyntaxAndNonTransactionalEventStateMatchOniguruma()
    {
        Assert.Equal(
            "Regex failure: undefined callout name",
            Assert.Throws<JqRuntimeException>(() => JqRegex.Test(string.Empty, "(*MON)")).Message);
        Assert.Equal(
            "Regex failure: invalid callout tag name",
            Assert.Throws<JqRuntimeException>(() => JqRegex.Test(string.Empty, "(*MAX{T})")).Message);
        Assert.Equal(
            "Regex failure: multiplex defined name <T>",
            Assert.Throws<JqRuntimeException>(() =>
                JqRegex.Test(string.Empty, "(*COUNT[T]{X})(*COUNT[T]{X})")).Message);
        Assert.Equal(
            "Regex failure: invalid callout arg",
            Assert.Throws<JqRuntimeException>(() => JqRegex.Test(string.Empty, "(*MAX{})")).Message);

        var progress = Assert.Single(JqRegex.Match(
            "aaaa",
            "(?:(*COUNT[A]{>})a)*(*CMP{A,==,3})"));
        Assert.Equal((2, 2, "aa"), (progress.Offset, progress.Length, progress.String));

        var retraction = Assert.Single(JqRegex.Match(
            "aaaa",
            "(?:(*COUNT[A]{<})a)*(*CMP{A,==,1})"));
        Assert.Equal((0, 4, "aaaa"), (retraction.Offset, retraction.Length, retraction.String));

        Assert.False(JqRegex.Test("ba", ".*(*TOTAL_COUNT[T])z|a(*CMP{T,==,2})"));
        Assert.True(JqRegex.Test("ba", ".(*TOTAL_COUNT[T])z|a(*CMP{T,==,2})"));
        Assert.False(JqRegex.Test("b", "(?<x>(*MAX{1,>})b)c|\\g<x>"));
        Assert.Empty(JqRegex.Match(
            "baba",
            ".*(*TOTAL_COUNT[T])z|a(*CMP{T,==,2})",
            "g"));
        Assert.False(JqRegex.Test(
            "aaab",
            "(?:(*MAX[M]{2,<})a)*ab(*CMP{M,==,1})"));
        Assert.True(JqRegex.Test(
            new string('a', 600),
            "(?:(*COUNT[A]{X})a)*(*CMP{A,==,600})"));
    }

    [Fact]
    public void CalloutComparisonsHaveNoManagedPatternDepthCeiling()
    {
        Assert.True(JqRegex.Test(
            new string('a', 513),
            "a+(*CMP{1,==,1})"));
        Assert.True(JqRegex.Test("a", "(*MAX{4097})a"));
        Assert.True(JqRegex.Test("a", "(*MAX{9223372036854775807})a"));
        Assert.True(JqRegex.Test(
            new string('a', 5_000),
            "\\A(?:(*MAX{5000})a)*\\z"));
        Assert.False(JqRegex.Test(
            new string('a', 5_001),
            "\\A(?:(*MAX{5000})a)*\\z"));
    }

    [Fact]
    public void CalloutRuntimeValidationAndZeroValuedTagsAreReachabilityExact()
    {
        Assert.True(JqRegex.Test("a", "a|z(*COUNT{Q})"));
        var invalid = Assert.Throws<JqRuntimeException>(() => JqRegex.Test("a", "a(*COUNT{Q})"));
        Assert.Equal("Regex failure: invalid callout arg", invalid.Message);

        Assert.True(JqRegex.Test("a", "(*COUNT[A]{X})(*CMP{A,==,A})a"));
        Assert.True(JqRegex.Test("a", "(*COUNT[A]{X})(*CMP{A,<=,A})a"));
        Assert.True(JqRegex.Test("a", "(*SKIP[T])(*CMP{T,==,0})a"));
        Assert.True(JqRegex.Test("a", "(*CMP{E,==,0})a|(*ERROR[E])"));
        Assert.True(JqRegex.Test(string.Empty, "(*CMP[C]{1,<,2})(*CMP{C,==,2})"));

        Assert.True(JqRegex.Test("a", "(*MAX{513})a"));
        Assert.Equal(
            "Regex failure: invalid callout body",
            Assert.Throws<JqRuntimeException>(() => JqRegex.Test("a", "a(*ERROR{1})")).Message);
        Assert.Equal(
            "Regex failure: abort",
            Assert.Throws<JqRuntimeException>(() =>
                JqRegex.Test("a", "a(*ERROR{-4294967299})")).Message);
        Assert.Equal(
            "Regex failure: invalid callout body",
            Assert.Throws<JqRuntimeException>(() => JqRegex.Test(string.Empty, "(*ERROR{+})")).Message);
        Assert.Equal(
            "Regex failure: undefined type (bug)",
            Assert.Throws<JqRuntimeException>(() => JqRegex.Test(string.Empty, "(*ERROR{-6})")).Message);
        Assert.Equal(
            "Regex failure: match-stack limit over",
            Assert.Throws<JqRuntimeException>(() => JqRegex.Test(string.Empty, "(*ERROR{-15})")).Message);
        Assert.Equal(
            "Regex failure: invalid callout body",
            Assert.Throws<JqRuntimeException>(() => JqRegex.Test(string.Empty, "(*ERROR{-217})")).Message);
        Assert.Equal(
            "Regex failure: undefined error code",
            Assert.Throws<JqRuntimeException>(() => JqRegex.Test(string.Empty, "(*ERROR{-404})")).Message);
        Assert.Equal(
            "Regex failure: library is not initialized",
            Assert.Throws<JqRuntimeException>(() => JqRegex.Test(string.Empty, "(*ERROR{-500})")).Message);
        Assert.False(JqRegex.Test(string.Empty, "(*MAX{+})"));
        Assert.True(JqRegex.Test(string.Empty, "(*CMP{+,==,-})"));
    }

    [Fact]
    public void ControlEventsRemainReachabilityExactAcrossComplexContexts()
    {
        Assert.Equal(
            "Regex failure: abort",
            Assert.Throws<JqRuntimeException>(() =>
                JqRegex.Test("a", "(?!a(*ERROR))b|a")).Message);
        Assert.False(JqRegex.Test("a", "(a)(?(1)(*MISMATCH)b|c)|a"));
        Assert.True(JqRegex.Test("aaac", "(?:a(*SKIP)z)*b|ac"));
        Assert.Equal(
            "Regex failure: abort",
            Assert.Throws<JqRuntimeException>(() =>
                JqRegex.Test("a", "(?(*FAIL)a|a(*ERROR))")).Message);

        Assert.True(JqRegex.Test("a", "(?<=a)(*COUNT[T]{>})"));
    }

    [Fact]
    public void CalloutConditionalsAndAbandonedBranchesPreserveSourceCaptureShape()
    {
        var fail = Assert.Single(JqRegex.Match("b", "(?(*FAIL)(a)|(b))"));
        Assert.Equal(2, fail.Captures.Count);
        Assert.Null(fail.Captures[0].String);
        Assert.Equal("b", fail.Captures[1].String);
        Assert.True(JqRegex.Test("bb", "(?(*FAIL)(a)|(b))\\2"));

        var count = Assert.Single(JqRegex.Match("a", "(?(*COUNT)(a)|(b))"));
        Assert.Equal(2, count.Captures.Count);
        Assert.Equal("a", count.Captures[0].String);
        Assert.Null(count.Captures[1].String);
        Assert.False(JqRegex.Test("aaa", "(?(*COUNT)(a)|(b))(a)\\2"));

        var error = Assert.Single(JqRegex.Match("b", "z(*ERROR)(a)|(b)"));
        Assert.Equal(2, error.Captures.Count);
        Assert.Null(error.Captures[0].String);
        Assert.Equal("b", error.Captures[1].String);
        var mismatch = Assert.Single(JqRegex.Match("b", "z(*MISMATCH)(a)|(b)"));
        Assert.Equal(2, mismatch.Captures.Count);
        Assert.Null(mismatch.Captures[0].String);
        Assert.Equal("b", mismatch.Captures[1].String);
    }

    [Fact]
    public void CalloutRecognitionHonorsQuotedCommentsExtendedModeAndIgnoreEmpty()
    {
        Assert.True(JqRegex.Test("(*ERROR)", "\\Q(*ERROR)\\E"));
        Assert.True(JqRegex.Test("a", "(?#(*ERROR)a"));
        Assert.True(JqRegex.Test("a", "# (*ERROR)\na", "x"));
        Assert.True(JqRegex.Test("aa", "(?x:# (*ERROR)\na)a"));
        Assert.True(JqRegex.Test("a", "# hidden\r(*COUNT{QQ})", "x"));
        Assert.True(JqRegex.Test("a", "# hidden\r(*ERROR)(", "x"));
        Assert.Equal(
            "Regex failure: abort",
            Assert.Throws<JqRuntimeException>(() =>
                JqRegex.Test(string.Empty, "(?#foo\\)bar)(*ERROR)")).Message);
        Assert.Equal(
            "Regex failure: abort",
            Assert.Throws<JqRuntimeException>(() =>
                JqRegex.Test("a", "\\Qa\\E(*ERROR)")).Message);

        Assert.Equal(
            "Regex failure: abort",
            Assert.Throws<JqRuntimeException>(() =>
                JqRegex.Test(string.Empty, "(*ERROR)", "n")).Message);
        Assert.True(JqRegex.Test("a", "a|(*ERROR)", "n"));
    }

    [Fact]
    public void EventRunnerFeedsEveryJqRegexPublicOperation()
    {
        const string pattern = "(*COUNT[T]{>})z|(*CMP{T,==,1})(?<v>a)";

        Assert.True(JqRegex.Test("a", pattern));
        var match = Assert.Single(JqRegex.Match("a", pattern));
        Assert.Equal((0, 1, "a"), (match.Offset, match.Length, match.String));
        Assert.Equal("a", Assert.Single(JqRegex.Capture("a", pattern))["v"]);

        var scan = JqRegex.Scan("aba", pattern);
        Assert.Equal(2, scan.Count);
        Assert.All(scan, item => Assert.Equal("a", Assert.Single(item.Captures!)));
        Assert.Equal(["", "b", ""], JqRegex.Split("aba", pattern));
        Assert.Equal("<a>ba", JqRegex.Sub(
            "aba",
            pattern,
            captures => '<' + captures["v"] + '>'));
        Assert.Equal("<a>b<a>", JqRegex.Gsub(
            "aba",
            pattern,
            captures => '<' + captures["v"] + '>'));
    }

    [Theory]
    [InlineData("A", "(*COUNT[T]{>})z|(*CMP{T,==,1})a", "i", true)]
    [InlineData("\n", "(*COUNT[T]{>})z|(*CMP{T,==,1}).", "m", true)]
    [InlineData("\na", "(*COUNT[T]{>})z|(*CMP{T,==,1})^a", "s", false)]
    [InlineData("a", "(*COUNT[T]{>}) z | (*CMP{T,==,1}) a", "x", true)]
    [InlineData("\n", "(*COUNT[T]{>})z|(*CMP{T,==,1}).", "p", true)]
    public void EventRunnerHonorsJqRegexModifiers(
        string input,
        string pattern,
        string modifiers,
        bool expected) =>
        Assert.Equal(expected, JqRegex.Test(input, pattern, modifiers));

    [Theory]
    [InlineData("é", "(*COUNT[T]{>})z|(*CMP{T,==,1})é", null)]
    [InlineData("😀", "(*COUNT[T]{>})z|(*CMP{T,==,1})😀", null)]
    [InlineData("éé", "(é)(*COUNT[T]{>})\\1(*CMP{T,==,1})", null)]
    [InlineData("😀😀", "(?<x>😀)(*COUNT[T]{>})\\k<x>(*CMP{T,==,1})", null)]
    [InlineData("SSSS", "(?<x>ß)(*COUNT[T]{>})\\k<x>(*CMP{T,==,1})", "i")]
    [InlineData("SS", "(*COUNT[T]{>})z|(*CMP{T,==,1})[ß]", "i")]
    [InlineData("𐐨", "(*COUNT[T]{>})z|(*CMP{T,==,1})[𐐀]", "i")]
    public void EventRunnerMatchesUnicodeLiteralsClassesAndActualTextBackreferences(
        string input,
        string pattern,
        string? modifiers) =>
        Assert.True(JqRegex.Test(input, pattern, modifiers));

    [Theory]
    [InlineData("A", "(*COUNT[T]{>})z|(*CMP{T,==,1})(?i)a", null, true)]
    [InlineData("A", "(*COUNT[T]{>})z|(*CMP{T,==,1})(?i:a)", null, true)]
    [InlineData("\na", "(*COUNT[T]{>})z|(*CMP{T,==,1})(?m)^a", null, true)]
    [InlineData("\na", "(*COUNT[T]{>})z|(*CMP{T,==,1})(?m:^a)", null, true)]
    [InlineData("\n", "(*COUNT[T]{>})z|(*CMP{T,==,1})(?s).", null, true)]
    [InlineData("\n", "(*COUNT[T]{>})z|(*CMP{T,==,1})(?s:.)", null, true)]
    [InlineData("a", "(*COUNT[T]{>})z|(*CMP{T,==,1})(?x) a", null, true)]
    [InlineData("a", "(*COUNT[T]{>})z|(*CMP{T,==,1})(?x: a )", null, true)]
    [InlineData("A", "(*COUNT[T]{>})z|(*CMP{T,==,1})(?-i)a", "i", false)]
    [InlineData("A", "(*COUNT[T]{>})z|(*CMP{T,==,1})(?-i:a)", "i", false)]
    [InlineData("\na", "(*COUNT[T]{>})z|(*CMP{T,==,1})(?m)(?-m)^a", null, false)]
    [InlineData("\na", "(*COUNT[T]{>})z|(*CMP{T,==,1})(?m)(?-m:^a)", null, false)]
    [InlineData("\n", "(*COUNT[T]{>})z|(*CMP{T,==,1})(?s)(?-s).", null, false)]
    [InlineData("\n", "(*COUNT[T]{>})z|(*CMP{T,==,1})(?s)(?-s:.)", null, false)]
    [InlineData(" a", "(*COUNT[T]{>})z|(*CMP{T,==,1})(?x)(?-x) a", null, true)]
    [InlineData(" a", "(*COUNT[T]{>})z|(*CMP{T,==,1})(?x)(?-x: )a", null, true)]
    public void EventRunnerHonorsBareScopedAndDisabledInlineOptions(
        string input,
        string pattern,
        string? modifiers,
        bool expected) =>
        Assert.Equal(expected, JqRegex.Test(input, pattern, modifiers));

    [Theory]
    [InlineData("!A", "(*TOTAL_COUNT[T]{>})(?i)a(*CMP{T,==,VALUE})", null)]
    [InlineData("!A", "(*TOTAL_COUNT[T]{>})(?i:a)(*CMP{T,==,VALUE})", null)]
    [InlineData("!a", "(*TOTAL_COUNT[T]{>})(?-i)a(*CMP{T,==,VALUE})", "i")]
    [InlineData("!a", "(*TOTAL_COUNT[T]{>})(?-i:a)(*CMP{T,==,VALUE})", "i")]
    public void EventRunnerInlineIgnoreCasePreservesOptimizerCandidateSchedule(
        string input,
        string patternTemplate,
        string? modifiers)
    {
        var match = Assert.Single(JqRegex.Match(
            input,
            patternTemplate.Replace("VALUE", "1", StringComparison.Ordinal),
            modifiers));
        Assert.Equal((1, 1, input[1..]), (match.Offset, match.Length, match.String));
        Assert.Empty(JqRegex.Match(
            input,
            patternTemplate.Replace("VALUE", "2", StringComparison.Ordinal),
            modifiers));
    }

    [Fact]
    public void TotalCountDoesNotLeakAStaleCandidateEpochIntoTagResolution()
    {
        Assert.True(JqRegex.Test(
            "xba",
            "x(*TOTAL_COUNT[T]{>})z|(*CMP{T,==,0})a"));
        Assert.False(JqRegex.Test(
            "xba",
            "x(*TOTAL_COUNT[T]{>})z|(*CMP{T,==,1})a"));
    }

    [Fact]
    public void TotalCountUsesPinnedAsciiClassMapCandidateWindows()
    {
        static int[] Offsets(string input, string pattern) =>
            JqRegex.Match(input, pattern).Select(match => match.Offset).ToArray();

        Assert.Equal(
            [2],
            Offsets("bba", "(*TOTAL_COUNT[T]{>})[a](*CMP{T,==,1})"));
        Assert.Empty(Offsets("bba", "(*TOTAL_COUNT[T]{>})[a](*CMP{T,==,2})"));
        Assert.Empty(Offsets("bba", "(*TOTAL_COUNT[T]{>})[a](*CMP{T,==,3})"));

        Assert.Empty(Offsets("bba", "(*TOTAL_COUNT[T]{>})[^b](*CMP{T,==,1})"));
        Assert.Empty(Offsets("bba", "(*TOTAL_COUNT[T]{>})[^b](*CMP{T,==,2})"));
        Assert.Equal(
            [2],
            Offsets("bba", "(*TOTAL_COUNT[T]{>})[^b](*CMP{T,==,3})"));

        Assert.Empty(Offsets("bbbba", "(*TOTAL_COUNT[T]{>})[x]?a(*CMP{T,==,1})"));
        Assert.Equal(
            [4],
            Offsets("bbbba", "(*TOTAL_COUNT[T]{>})[x]?a(*CMP{T,==,2})"));
    }

    [Fact]
    public void EventRunnerConsumesSupplementaryBraceRadixEscapesAsScalars()
    {
        var match = Assert.Single(JqRegex.Match(
            "😀a",
            "(*COUNT[T]{>})\\x{1F600}|(*CMP{T,==,1})a"));
        Assert.Equal((0, 1, "😀"), (match.Offset, match.Length, match.String));
    }

    [Theory]
    [InlineData("(*ERROR)(", "Regex failure: end pattern with unmatched parenthesis")]
    [InlineData("(*MISMATCH)(", "Regex failure: end pattern with unmatched parenthesis")]
    [InlineData("(*ERROR)[", "Regex failure: premature end of char-class")]
    [InlineData("(*ERROR)(?<x>a", "Regex failure: end pattern with unmatched parenthesis")]
    [InlineData("(*ERROR)\\9", "Regex failure: invalid backref number/name")]
    [InlineData("(*ERROR)\\p{NoSuch}", "Regex failure: invalid character property name {NoSuch}")]
    [InlineData("(*ERROR)(?q)", "Regex failure: undefined group option")]
    [InlineData("(?-p:.)|(*COUNT[T]{>})z|(*CMP{T,==,1})q", "Regex failure: undefined group option")]
    [InlineData("[[](*ERROR)", "Regex failure: abort")]
    [InlineData("(*ERROR)(?#abc", "Regex failure: end pattern in group")]
    [InlineData("(*ERROR)*", "Regex failure: target of repeat operator is invalid")]
    [InlineData("(*ERROR)a{2,1}", "Regex failure: upper is smaller than lower in repeat range")]
    [InlineData("(*ERROR)\\k<no>", "Regex failure: undefined name <no> reference")]
    [InlineData("(*ERROR)\\p{NoSuch", "Regex failure: end pattern with unmatched parenthesis")]
    [InlineData("(*ERROR)(??)", "Regex failure: undefined group option")]
    [InlineData("a|(*ERROR)(", "Regex failure: end pattern with unmatched parenthesis")]
    public void CalloutExecutionOccursOnlyAfterWholePatternCompilation(
        string pattern,
        string expectedMessage) =>
        Assert.Equal(
            expectedMessage,
            Assert.Throws<JqRuntimeException>(() => JqRegex.Test("[", pattern)).Message);

    [Fact]
    public void BareDecimalEscapesRetainPinnedBackreferenceAndOctalPrecedence()
    {
        Assert.Equal(
            "Regex failure: abort",
            Assert.Throws<JqRuntimeException>(() =>
                JqRegex.Test("\b", "\\10(*ERROR)")).Message);
        Assert.Equal(
            "Regex failure: abort",
            Assert.Throws<JqRuntimeException>(() =>
                JqRegex.Test("S", "\\123(*ERROR)")).Message);
        Assert.False(JqRegex.Test("a", "\\99(*ERROR)"));
        Assert.False(JqRegex.Test("a", "\\1(a)"));
        Assert.False(JqRegex.Test("ab", "\\2(a)(b)"));
        Assert.Equal(
            "Regex failure: invalid backref number/name",
            Assert.Throws<JqRuntimeException>(() => JqRegex.Test("a", "\\2(a)(*ERROR)")).Message);
    }

    [Fact]
    public void DuplicateCalloutTagsAreImmediateButReferencesResolveAfterParsing()
    {
        Assert.Equal(
            "Regex failure: multiplex defined name <T>",
            Assert.Throws<JqRuntimeException>(() =>
                JqRegex.Test(string.Empty, "(*COUNT[T])(*COUNT[T])(")).Message);
        Assert.Equal(
            "Regex failure: invalid character property name {NoSuch}",
            Assert.Throws<JqRuntimeException>(() =>
                JqRegex.Test(string.Empty, "\\p{NoSuch}(*COUNT[T])(*COUNT[T])")).Message);
        Assert.Equal(
            "Regex failure: end pattern with unmatched parenthesis",
            Assert.Throws<JqRuntimeException>(() =>
                JqRegex.Test(string.Empty, "((*MAX{NOPE})")).Message);
        Assert.False(JqRegex.Test("a", "(*MAX{T})(*COUNT[T])a"));
    }

    [Fact]
    public void EventRunnerSubcallCapturesUseTheFinalInvocationSpan()
    {
        var match = Assert.Single(JqRegex.Match(
            "aa",
            "(?<x>a(*COUNT[T]{>}))\\g<x>(*CMP{T,==,2})"));
        var capture = Assert.Single(match.Captures);
        Assert.Equal((1, 1, "a", "x"),
            (capture.Offset, capture.Length, capture.String, capture.Name));
    }

    [Fact]
    public void NamedCalloutParserMirrorsPinnedArgumentAndRepeatGrammar()
    {
        Assert.True(JqRegex.Test("a", "(*COUNT{,})a"));
        Assert.True(JqRegex.Test("a", "(*MAX{1,})a"));
        Assert.True(JqRegex.Test(string.Empty, "(*CMP{1,,==,,1})"));
        Assert.True(JqRegex.Test("a", "a|z(*COUNT{\\,})"));
        Assert.Equal(
            "Regex failure: invalid callout arg",
            Assert.Throws<JqRuntimeException>(() => JqRegex.Test("a", "a(*COUNT{\\,})")).Message);
        Assert.Equal(
            "Regex failure: invalid callout tag name",
            Assert.Throws<JqRuntimeException>(() => JqRegex.Test("a", "(*MAX{ 2})a")).Message);
        Assert.Equal(
            "Regex failure: invalid callout arg",
            Assert.Throws<JqRuntimeException>(() =>
                JqRegex.Test(string.Empty, "(*ERROR{-9223372036854775808})")).Message);
        Assert.Equal(
            "Regex failure: undefined callout name",
            Assert.Throws<JqRuntimeException>(() => JqRegex.Test("a", "(*FAIL2)a")).Message);
        Assert.Equal(
            "Regex failure: invalid callout name",
            Assert.Throws<JqRuntimeException>(() => JqRegex.Test("a", "(*FA-IL)a")).Message);
        Assert.Equal(
            "Regex failure: target of repeat operator is invalid",
            Assert.Throws<JqRuntimeException>(() => JqRegex.Test("a", "(*FAIL)?a")).Message);
    }

    [Theory]
    [InlineData("(?{})a")]
    [InlineData("(?{opaque})a")]
    [InlineData("(?{é雪😀})a")]
    [InlineData("(?{(*FAIL)})a")]
    [InlineData("(?{x}>)a")]
    [InlineData("(?{x}X)a")]
    [InlineData("(?{x}<)a")]
    [InlineData("(?{{a}b}})a")]
    [InlineData("(?{{{a}b}}})a")]
    [InlineData("(?{{a\\}b}})a")]
    [InlineData("(?{\\K[not-a-class]#(*ERROR)})a")]
    public void ContentCalloutsArePinnedNoCallbackZeroWidthSuccesses(string pattern) =>
        Assert.True(JqRegex.Test("a", pattern));

    [Fact]
    public void ContentCalloutTagsShareTheBuiltInCalloutNamespaceAtValueZero()
    {
        Assert.True(JqRegex.Test("a", "(?{x}[T])(*CMP{T,==,0})a"));
        Assert.False(JqRegex.Test("a", "(?{x}[T])(*CMP{T,==,1})a"));
        Assert.False(JqRegex.Test("a", "(*MAX{T})(?{x}[T])a"));

        Assert.Equal(
            "Regex failure: multiplex defined name <T>",
            Assert.Throws<JqRuntimeException>(() =>
                JqRegex.Test("a", "(?{x}[T])(?{y}[T])a")).Message);
        Assert.Equal(
            "Regex failure: multiplex defined name <T>",
            Assert.Throws<JqRuntimeException>(() =>
                JqRegex.Test("a", "(?{x}[T])(*COUNT[T])a")).Message);
    }

    [Fact]
    public void ContentCalloutConditionsAlwaysChooseTheTrueBranch()
    {
        Assert.True(JqRegex.Test("a", "(?(?{x})a|b)"));
        Assert.False(JqRegex.Test("b", "(?(?{x})a|b)"));
        Assert.True(JqRegex.Test("a", "(?(?{x}<)a|b)"));
        Assert.True(JqRegex.Test("a", "(?=(?{x})a)a"));
        Assert.False(JqRegex.Test("a", "(?!(?{x})a)a"));
        Assert.True(JqRegex.Test("ac", "(?:a(?{x}X)b|a)c"));

        // Force the event runner while exercising both content-callout parsing forms.
        Assert.True(JqRegex.Test(
            "a",
            "(?{{x}})(*COUNT[T]{>})z|(*CMP{T,==,1})(?(?{y})a|b)"));
    }

    [Theory]
    [InlineData("(?{", "Regex failure: invalid callout pattern")]
    [InlineData("(?{x", "Regex failure: invalid callout pattern")]
    [InlineData("(?{{x})", "Regex failure: invalid callout pattern")]
    [InlineData("(?{x}", "Regex failure: end pattern in group")]
    [InlineData("(?{x}[", "Regex failure: end pattern in group")]
    [InlineData("(?{x}[T", "Regex failure: invalid callout tag name")]
    [InlineData("(?{x}[_", "Regex failure: invalid callout tag name")]
    [InlineData("(?{x}[1", "Regex failure: invalid callout tag name")]
    [InlineData("(?{x}[])", "Regex failure: invalid callout tag name")]
    [InlineData("(?{x}[1T])", "Regex failure: invalid callout tag name")]
    [InlineData("(?{x}[é])", "Regex failure: invalid callout tag name")]
    [InlineData("(?{x}+)", "Regex failure: invalid callout pattern")]
    [InlineData("(?{x}-)", "Regex failure: invalid callout pattern")]
    [InlineData("(?{x}Q)", "Regex failure: invalid callout pattern")]
    [InlineData("(?{x}XX)", "Regex failure: invalid callout pattern")]
    [InlineData("(?{a\\}b})", "Regex failure: invalid callout pattern")]
    [InlineData("(?(?{x}))", "Regex failure: invalid if-else syntax")]
    public void ContentCalloutParserUsesPinnedDiagnostics(string pattern, string expectedMessage) =>
        Assert.Equal(
            expectedMessage,
            Assert.Throws<JqRuntimeException>(() => JqRegex.Test("a", pattern)).Message);

    [Theory]
    [InlineData("(?{x})*")]
    [InlineData("(?{x})+")]
    [InlineData("(?{x})?")]
    [InlineData("(?{x}){2}")]
    [InlineData("(?{x}) *")]
    [InlineData("(?{x}) # comment\n+")]
    public void ContentCalloutsCannotBeRepeatedDirectly(string pattern) =>
        Assert.Equal(
            "Regex failure: target of repeat operator is invalid",
            Assert.Throws<JqRuntimeException>(() => JqRegex.Test("a", pattern, "x")).Message);

    [Fact]
    public void ContentCalloutsPreserveEveryPublicRegexOperationShape()
    {
        const string pattern = "(?{opaque})a";
        Assert.True(JqRegex.Test("aba", pattern));

        var match = Assert.Single(JqRegex.Match("aba", "(?{opaque})(?<n>a)"));
        Assert.Equal((0, 1, "a"), (match.Offset, match.Length, match.String));
        var matchCapture = Assert.Single(match.Captures);
        Assert.Equal((0, 1, "a", "n"),
            (matchCapture.Offset, matchCapture.Length, matchCapture.String, matchCapture.Name));

        var capture = Assert.Single(JqRegex.Capture("aba", "(?{opaque})(?<n>a)"));
        Assert.Equal("a", capture["n"]);
        Assert.Equal(["a", "a"], JqRegex.Scan("aba", pattern).Select(result => result.String));
        Assert.Equal(["", "b", ""], JqRegex.Split("aba", pattern));
        Assert.Equal("Xba", JqRegex.Sub("aba", pattern, "X"));
        Assert.Equal("XbX", JqRegex.Gsub("aba", pattern, "X"));
    }

    [Fact]
    public void RecursiveSubexpressionCallsAreBoundedByInputLength()
    {
        var match = Assert.Single(JqRegex.Match("bbacc", "(?<x>a|b\\g<x>c)", "g"));
        Assert.Equal("bbacc", match.String);
        Assert.Equal("bbacc", Assert.Single(match.Captures).String);

        var nested = Assert.Single(JqRegex.Match("aaaa", "((a))(?1)"));
        Assert.Equal("aa", nested.String);
        Assert.Equal(2, nested.Captures.Count);
        Assert.All(nested.Captures, capture =>
        {
            Assert.Equal(1, capture.Offset);
            Assert.Equal("a", capture.String);
        });

        var namedNested = Assert.Single(JqRegex.Match("abab", "(?<x>a(?<y>b))(?&x)"));
        Assert.Equal(
            [("x", 2, "ab"), ("y", 3, "b")],
            namedNested.Captures.Select(capture => (capture.Name, capture.Offset, capture.String)));
        Assert.Equal(
            ["a", "b"],
            Assert.Single(JqRegex.Scan("aab", "(a)\\g<-1>(b)")).Captures);
    }

    [Fact]
    public void RecursiveSubexpressionCallsHonorOnigurumaNestingLimit()
    {
        const string pattern = "\\A(?<x>a|b\\g<x>c)\\z";
        var accepted = new string('b', 19) + 'a' + new string('c', 19);
        var rejected = new string('b', 20) + 'a' + new string('c', 20);

        Assert.True(JqRegex.Test(accepted, pattern));
        Assert.False(JqRegex.Test(rejected, pattern));
    }

    [Theory]
    [InlineData("123456789", "(?~|78|\\d*)", 0, "123456")]
    [InlineData("abcdedeabcfdefabc", "(?~|def|(?:abc|de|f){0,100})", 0, "abcdedeabcf")]
    [InlineData("ccc\nddd", "(?~|ab|.*)", 0, "ccc")]
    [InlineData("ccc\ndab", "(?~|ab|\\O*)", 0, "ccc\nd")]
    [InlineData("ccc\ndab", "(?~|ab|\\O{2,10})", 0, "ccc\nd")]
    [InlineData("ab", "(?~|ab|\\O{1,10})", 1, "b")]
    [InlineData("abc", "(?~|abc|\\O{1,10})", 1, "bc")]
    [InlineData("abc", "(?~|ab|\\O{5,10})|abc", 0, "abc")]
    [InlineData("cccccccccccab", "(?~|ab|\\O{1,10})", 0, "cccccccccc")]
    [InlineData("aaa", "(?~|aaa|)", 0, "")]
    [InlineData("aaaaaa", "(?~||a*)", 0, "")]
    [InlineData("aaaaaa", "(?~||a*?)", 0, "")]
    [InlineData("aaaaaa", "(a)(?~|b|\\1)", 0, "aa")]
    [InlineData("aaaaaa", "(a)(?~|bb|(?:a\\1)*)", 0, "aaaaa")]
    [InlineData("abababacabab", "(b|c)(?~|abac|(?:a\\1)*)", 1, "bab")]
    [InlineData("aaaaa", "(?~|aaaaa|a*+)", 0, "")]
    [InlineData("aaaaaab", "(?~|aaaaaa|a*+)b", 1, "aaaaab")]
    [InlineData("zzzabcd", "(?~|abcd|(?>))", 0, "")]
    [InlineData("aaaabc", "(?~|abc|a*?)", 0, "")]
    public void AbsentExpressionsUseThePinnedRightRange(
        string input,
        string pattern,
        int offset,
        string value)
    {
        var match = Assert.Single(JqRegex.Match(input, pattern));
        Assert.Equal(offset, match.Offset);
        Assert.Equal(value, match.String);
    }

    [Fact]
    public void AbsentExpressionMinimumLengthCanRejectEverySearchStart()
    {
        Assert.Empty(JqRegex.Match("ab", "(?~|ab|\\O{2,10})"));
        Assert.Empty(JqRegex.Match("aaaaa", "(?~|c|a*+)a"));
        Assert.Empty(JqRegex.Match("a", "(?~|a)a"));
        Assert.Empty(JqRegex.Match(
            "aaaaxyzaaaabcpqrabcabc",
            "\\A(?~|abc).*(xyz|pqrabc)(?~|)abc"));
    }

    [Theory]
    [InlineData("aaaaaabc", "(?~|abc)a*", 0, "aaaaa")]
    [InlineData("aaaaaabc", "(?~|abc)a*z|aaaaaabc", 0, "aaaaaabc")]
    [InlineData("aaaaaa", "(?~|aaaaaa)a*", 0, "")]
    [InlineData("aaaabc", "(?~|abc)aaaa|aaaabc", 0, "aaaabc")]
    [InlineData("aaaabc", "(?>(?~|abc))aaaa|aaaabc", 0, "aaaabc")]
    [InlineData("a", "(?~|)a", 0, "a")]
    [InlineData("a", "(?~|a)(?~|)a", 0, "a")]
    [InlineData("bbbbbbbbbbbbbbbbbbbba", "(?~|a).*(?~|)a", 0, "bbbbbbbbbbbbbbbbbbbba")]
    [InlineData("aaaaxyzaaapqrabc", "(?~|abc).*(xyz|pqr)(?~|)abc", 0, "aaaaxyzaaapqrabc")]
    [InlineData("aaaaxyzaaaabcpqrabc", "(?~|abc).*(xyz|pqr)(?~|)abc", 11, "bcpqrabc")]
    [InlineData("ab", "(?~|a)(?~|)c|ab|a|", 0, "ab")]
    [InlineData("ab", "(?~|a)((?~|)c|ab|a|)", 0, "")]
    [InlineData("ab", "(?~|a)((?>(?~|))c|ab|a|)", 0, "")]
    public void AbsentStoppersAndRangeClearBacktrackWithBranches(
        string input,
        string pattern,
        int offset,
        string value)
    {
        var match = Assert.Single(JqRegex.Match(input, pattern));
        Assert.Equal(offset, match.Offset);
        Assert.Equal(value, match.String);
    }

    [Fact]
    public void AbsentRangePrivateStateDoesNotLeakIntoCaptures()
    {
        var expression = Assert.Single(JqRegex.Match("aaab", "(?~|(a)b|(x)?a*)"));
        Assert.Equal("aa", expression.String);
        Assert.Equal(2, expression.Captures.Count);
        Assert.All(expression.Captures, capture => Assert.Null(capture.String));

        Assert.Equal(
            ["aaaaa", "", "", "", ""],
            JqRegex.Scan("aaaaaabc", "(?~|abc)(a*)").Select(scan =>
                Assert.Single(scan.Captures!)!));
    }

    [Theory]
    [InlineData("x\nyEND", "(?~END)", 0, 3, "x\ny")]
    [InlineData("123456789", "(?~|78|\\d*)", 0, 6, "123456")]
    [InlineData("aaaaaabc", "(?~|abc)a*z|aaaaaabc", 0, 8, "aaaaaabc")]
    [InlineData("a", "(?~|a)(?~|)a", 0, 1, "a")]
    [InlineData("aaab", "(?~b)a+b", 0, 4, "aaab")]
    public void AbsentRightRangeMatchesPinnedBacktrackingVectors(
        string input,
        string pattern,
        int offset,
        int length,
        string value)
    {
        var match = Assert.Single(JqRegex.Match(input, pattern));
        Assert.Equal((offset, length, value), (match.Offset, match.Length, match.String));
    }

    [Theory]
    [InlineData("abc", "(?<=a|bc)c")]
    [InlineData("abbc", "(?<=ab+)c")]
    [InlineData("xb", "(?<!(a))b")]
    public void LookbehindCompilationMatchesPinnedPerlNgRestrictions(
        string input,
        string pattern) =>
        Assert.Equal(
            "Regex failure: invalid pattern in look-behind",
            Assert.Throws<JqRuntimeException>(() => JqRegex.Match(input, pattern)).Message);

    [Fact]
    public void FixedLookbehindHonorsOnigurumaMaximumCharacterWidth()
    {
        var maximumInput = new string('a', 65_535) + 'b';
        var match = Assert.Single(JqRegex.Match(maximumInput, "(?<=a{65535})b"));
        Assert.Equal((65_535, 1, "b"), (match.Offset, match.Length, match.String));
        Assert.True(JqRegex.Test("abbx", "(?<=\\Qab\\E{2})x"));

        var oversizedInput = new string('a', 65_536) + 'b';
        Assert.Equal(
            "Regex failure: invalid pattern in look-behind",
            Assert.Throws<JqRuntimeException>(
                () => JqRegex.Match(oversizedInput, "(?<=a{65536})b")).Message);
        Assert.Equal(
            "Regex failure: invalid pattern in look-behind",
            Assert.Throws<JqRuntimeException>(() => JqRegex.Match(
                "b",
                "(?<=\\Q" + new string('a', 65_536) + "\\E)b")).Message);
    }

    [Fact]
    public void AbsentLookbehindAndKeepPreservePinnedBacktrackingState()
    {
        var absent = Assert.Single(JqRegex.Match(
            "ba",
            "\\A(?~(?:(*COUNT[A]{>})(?<d>a)|(*COUNT[B]{>})a))" +
            "(*CMP{A,==,2})(*CMP{B,==,2})"));
        Assert.Equal((0, 1, "b"), (absent.Offset, absent.Length, absent.String));
        var absentCapture = Assert.Single(absent.Captures);
        Assert.Equal((-1, 0, (string?)null, "d"),
            (absentCapture.Offset, absentCapture.Length, absentCapture.String, absentCapture.Name));

        var positive = Assert.Single(JqRegex.Match(
            "xab",
            "(?<=(a)(*COUNT[T]{X}))b(*CMP{T,==,1})"));
        Assert.Equal((2, 1, "b"), (positive.Offset, positive.Length, positive.String));
        var positiveCapture = Assert.Single(positive.Captures);
        Assert.Equal((1, 1, "a"),
            (positiveCapture.Offset, positiveCapture.Length, positiveCapture.String));

        var negative = Assert.Single(JqRegex.Match(
            "axb",
            "(?<!a(*COUNT[X]{X})(*COUNT[P]{>})c)b" +
            "(*CMP{X,==,0})(*CMP{P,==,1})"));
        Assert.Equal((2, 1, "b"), (negative.Offset, negative.Length, negative.String));
        Assert.Empty(negative.Captures);

        var lookbehindKeep = Assert.Single(JqRegex.Match("ab", "(?<=\\K(a))b"));
        Assert.Equal((0, 2, "ab"),
            (lookbehindKeep.Offset, lookbehindKeep.Length, lookbehindKeep.String));
        var keepCapture = Assert.Single(lookbehindKeep.Captures);
        Assert.Equal((0, 1, "a"),
            (keepCapture.Offset, keepCapture.Length, keepCapture.String));

        var discardedKeep = Assert.Single(JqRegex.Match("ab", "(?:a\\Kc|a)b"));
        Assert.Equal((0, 2, "ab"),
            (discardedKeep.Offset, discardedKeep.Length, discardedKeep.String));

        var lookaheadKeep = Assert.Single(JqRegex.Match("ab", "(?=ab\\K)a"));
        Assert.Equal((1, 0, string.Empty),
            (lookaheadKeep.Offset, lookaheadKeep.Length, lookaheadKeep.String));

        var longestKeep = Assert.Single(JqRegex.Match("abc", "(?:ab|a\\Kbc)", "l"));
        Assert.Equal((1, 2, "bc"),
            (longestKeep.Offset, longestKeep.Length, longestKeep.String));
    }

    [Fact]
    public void TextSegmentsFollowUnicodeExtendedGraphemeBoundaries()
    {
        Assert.Equal(["각"], JqRegex.Scan("각", "\\X").Select(match => match.String));
        Assert.Equal(["நி"], JqRegex.Scan("நி", "\\X").Select(match => match.String));
        Assert.Equal(["กำ"], JqRegex.Scan("กำ", "\\X").Select(match => match.String));
        Assert.Equal(["؀a"], JqRegex.Scan("؀a", "\\X").Select(match => match.String));
        Assert.Equal(["〰̂‍⭕"], JqRegex.Scan("〰̂‍⭕", "\\X").Select(match => match.String));
        Assert.Equal(["🇦🇧", "🇨"], JqRegex.Scan("🇦🇧🇨", "\\X").Select(match => match.String));

        Assert.False(JqRegex.Test("\r\n", ".\\y\\O"));
        Assert.True(JqRegex.Test("\r\n", ".\\Y\\O"));
        Assert.True(JqRegex.Test("g̈", "\\y.\\Y.\\y"));
        Assert.True(JqRegex.Test("각", "^.\\Y.\\Y.$"));
        Assert.True(JqRegex.Test("நி", ".\\Y."));
        Assert.True(JqRegex.Test("กำ", ".\\Y."));
    }

    [Fact]
    public void ScalarCharacterClassesConsumeWholeUnicodeScalars()
    {
        Assert.True(JqRegex.Test("😀", "[^a]"));
        Assert.False(JqRegex.Test("a", "[^a]"));
        Assert.True(JqRegex.Test("😀", "[😀]"));
        Assert.True(JqRegex.Test("😀", "[😀-🙏]"));
        Assert.True(JqRegex.Test("𐐀", "[\\w]"));
        Assert.True(JqRegex.Test("𐐀", "[\\p{Lu}]"));
        Assert.False(JqRegex.Test("𐐨", "[\\p{Lu}]"));
        Assert.True(JqRegex.Test("😀", "[\\p{Emoji}]"));
        Assert.False(JqRegex.Test("⌁", "[\\p{Emoji}]"));
        Assert.Equal(
            ["😀"],
            JqRegex.Scan("😀 \n", "\\S+").Select(match => match.String));
    }

    [Fact]
    public void ScalarCharacterClassLoweringRetainsIgnoreCaseFolds()
    {
        Assert.True(JqRegex.Test("SS", "^[😀ß]$", "i"));
        Assert.True(JqRegex.Test("FFI", "^[😀ﬃ]$", "i"));
        Assert.True(JqRegex.Test("😀", "^[😀ß]$", "i"));
        Assert.True(JqRegex.Test("𐐨", "^[😀𐐀]$", "i"));
        Assert.True(JqRegex.Test("𐐀", "^[😀𐐨]$", "i"));
    }

    [Fact]
    public void MixedPosixClassesRetainOnigurumaSetSemantics()
    {
        Assert.False(JqRegex.Test(" ", "[x[:graph:]]"));
        Assert.False(JqRegex.Test("\n", "[x[:print:]]"));
        Assert.True(JqRegex.Test("𐐀", "[_[:alpha:]]"));
        Assert.True(JqRegex.Test("a", "[0[:^digit:]]"));
        Assert.True(JqRegex.Test("0", "[0[:^digit:]]"));
        Assert.False(JqRegex.Test("0", "[a[:^digit:]]"));
    }

    [Fact]
    public void PerlNgDoesNotEnableCharacterClassSetOperators()
    {
        var match = Assert.Single(JqRegex.Match("b]", "[a-z&&[^aeiou]]"));
        Assert.Equal("b]", match.String);
        Assert.False(JqRegex.Test("b", "[a-z&&[^aeiou]]"));
    }

    [Theory]
    [InlineData("[a-\\w]")]
    [InlineData("[a-\\p{L}]")]
    [InlineData("[a-[:alpha:]]")]
    [InlineData("[😀-\\p{Emoji}]")]
    public void CharacterTypeCannotTerminateACharacterClassRange(string pattern)
    {
        var exception = Assert.Throws<JqRuntimeException>(() => JqRegex.Test("a", pattern));
        Assert.Equal("Regex failure: char-class value at end of range", exception.Message);
    }

    [Theory]
    [InlineData("[😀--🙏]")]
    [InlineData("[🙏-😀]")]
    public void ReversedScalarCharacterClassRangesUseOnigurumaDiagnostic(string pattern)
    {
        var exception = Assert.Throws<JqRuntimeException>(() => JqRegex.Test("a", pattern));
        Assert.Equal("Regex failure: empty range in char class", exception.Message);
    }

    [Fact]
    public void ContextualLookBehindReproducesJqUtf8ContinuationRestarts()
    {
        Assert.Equal(
            [1, 0, 2],
            JqRegex.Match("á", "(?<=.)", "g").Select(match => match.Offset));
        Assert.Equal(
            [1, 0, 2, 0, 3, 4],
            JqRegex.Match("aé😀b", "(?<=.)", "g").Select(match => match.Offset));
        Assert.Equal(
            [1, 0, 2],
            JqRegex.Match("á", "(?<=\\w)", "g").Select(match => match.Offset));
        Assert.Equal(
            [1],
            JqRegex.Match("a😀", "(?<=\\w)", "g").Select(match => match.Offset));
        Assert.Equal(
            [1, 0, 2],
            JqRegex.Match("aé", "(?<=.)|Z", "g").Select(match => match.Offset));
        Assert.Equal(
            [1, 0, 2],
            JqRegex.Match("aé", "Z|(?<=.)", "g").Select(match => match.Offset));
        Assert.Equal(
            [0, 1, 2, 2],
            JqRegex.Match("aé", "|(?<=.)", "g").Select(match => match.Offset));
    }
}
