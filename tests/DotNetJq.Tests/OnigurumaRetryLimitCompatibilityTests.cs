using DotNetJq.Compatibility.Regex;
using DotNetJq.Port;

namespace DotNetJq.Tests;

/// <summary>
/// Frozen from tools/regex-retry-oracle against jq-1.8.2's pinned Oniguruma.
/// An explicit chain of N optional atoms has 2^N paths, and every path reaches
/// one source-level fail opcode. The begin-buffer anchor keeps
/// search scheduling out of the per-match retry measurement.
/// </summary>
public sealed class OnigurumaRetryLimitCompatibilityTests
{
    [Theory]
    [InlineData(0, 1UL)]
    [InlineData(1, 2UL)]
    [InlineData(2, 4UL)]
    [InlineData(3, 8UL)]
    [InlineData(8, 256UL)]
    public void RetryLimitRaisesWhenTheNativeFailurePopCountReachesTheLimit(
        int optionalCount,
        ulong nativeRetryCount)
    {
        var error = Assert.Throws<JqRuntimeException>(() =>
            Run(optionalCount, nativeRetryCount));

        Assert.Equal("Regex failure: retry-limit-in-match over", error.Message);
        Assert.Empty(Run(optionalCount, nativeRetryCount + 1));
        Assert.Empty(Run(optionalCount, retryLimitInMatch: 0));
    }

    [Fact]
    public void OrdinaryDefaultPathRoutesTheNativeBoundaryToTheCountedRunner()
    {
        // 2^23 = 8,388,608 source fail/pops: below the 10,000,000 default.
        Assert.False(JqRegex.Test(
            new string('a', 23),
            BuildOrdinaryPattern(23)));

        // 2^24 = 16,777,216 paths: Oniguruma stops on pop 10,000,000.
        var error = Assert.Throws<JqRuntimeException>(() => JqRegex.Test(
            new string('a', 24),
            BuildOrdinaryPattern(24)));
        Assert.Equal("Regex failure: retry-limit-in-match over", error.Message);
    }

    [Fact]
    public void RetryCounterRestartsForEveryMatchAtCandidate()
    {
        var handled = OnigurumaCalloutEventRunner.TryRun(
            "aaaa",
            "a?(*COUNT)(*FAIL)",
            modifiers: null,
            timeout: null,
            forceGlobal: false,
            out var matches,
            out var boundary,
            retryLimitInMatch: 3);

        Assert.True(handled, boundary);
        Assert.Null(boundary);
        Assert.Empty(matches);
    }

    [Fact]
    public void LinearGreedyBacktrackingUsesTheSameFailurePopUnit()
    {
        const string pattern = "\\Aa*(*COUNT)(?!)";
        var error = Assert.Throws<JqRuntimeException>(() => RunPattern(
            "aaaaaaaa",
            pattern,
            retryLimitInMatch: 9));
        Assert.Equal("Regex failure: retry-limit-in-match over", error.Message);

        Assert.Empty(RunPattern("aaaaaaaa", pattern, retryLimitInMatch: 10));
        Assert.Empty(RunPattern("aaaaaaaa", pattern, retryLimitInMatch: 0));
    }

    [Theory]
    [InlineData("aa", "\\A(a)(?1)(?!)", 1UL)]
    [InlineData("abb", "\\A(?<x>a)(?<x>b)\\k<x>(?!)", 1UL)]
    [InlineData("ac", "\\A(a)?(?(-1)b|c)(?!)", 3UL)]
    [InlineData("ac", "\\A(?(a|b)c|d)(?!)", 1UL)]
    [InlineData("b", "\\A(?~|(?~a)|b)(?!)", 14UL)]
    [InlineData("x", "\\A[\\Qx](?!)", 1UL)]
    [InlineData("ab", "\\Aa(?<=a+)b(?!)", 1UL)]
    [InlineData("a", "\\A(a)(?<=\\k<1>)(?!)", 1UL)]
    [InlineData("aa", "\\A(?<x>a){0}(?<=\\g<x>)a(?!)", 1UL)]
    [InlineData("ab", "\\Aa(?<=(?~a))b(?!)", 1UL)]
    [InlineData("bbacca", "\\A(a|b\\g<1>c)\\k<1+3>(?!)", 4UL)]
    [InlineData("bbacca", "\\A(a|b\\g<1>c)(?(1+3)a|x)(?!)", 4UL)]
    public void NewlyCoveredGrammarUsesPinnedNativeFailurePopBoundaries(
        string input,
        string pattern,
        ulong nativeRetryCount)
    {
        var error = Assert.Throws<JqRuntimeException>(() =>
            RunPattern(input, pattern, nativeRetryCount));
        Assert.Equal("Regex failure: retry-limit-in-match over", error.Message);
        Assert.Empty(RunPattern(input, pattern, nativeRetryCount + 1));
    }

    [Fact]
    public void OrdinaryLinearRiskMatchesTheDefaultNativeBoundary()
    {
        const string pattern = "\\Aa*(?!)";
        Assert.False(JqRegex.Test(new string('a', 1_024), pattern));

        // The analyzer conservatively includes a possible terminal repeat-body
        // probe. Exact counted execution then distinguishes the native N+1
        // total at the configured default boundary.
        Assert.True(OnigurumaCalloutEventRunner.MayRequireRetryAccounting(
            new string('a', 9_999_998),
            pattern));
        Assert.False(JqRegex.Test(new string('a', 9_999_998), pattern));

        var error = Assert.Throws<JqRuntimeException>(() => JqRegex.Test(
            new string('a', 9_999_999),
            pattern));
        Assert.Equal("Regex failure: retry-limit-in-match over", error.Message);
    }

    [Fact]
    public void SupplementaryScalarBoundaryRoutesThroughTheCountedRunner()
    {
        // Public normalization lowers supplementary pattern scalars before
        // retry routing. The high-risk form must therefore still reach the
        // source-shaped runner instead of opaque .NET backtracking.
        const string scalar = "😀";
        var input = string.Concat(Enumerable.Repeat(scalar, 24));
        var pattern = "\\A" + string.Concat(Enumerable.Repeat(scalar + "?", 24)) +
            "(?!)";

        var error = Assert.Throws<JqRuntimeException>(() => JqRegex.Test(input, pattern));
        Assert.Equal("Regex failure: retry-limit-in-match over", error.Message);
    }

    public static TheoryData<string, string, string?> PerlNgGrammarFamilies => new()
    {
        { "core-literal-any", ".a", null },
        { "class-posix", "[[:alpha:]\\d]", null },
        { "character-types", "\\w\\W\\s\\S\\d\\D\\b\\B", null },
        { "anchors", "\\A\\G^$\\Z\\z", null },
        { "control-radix-quote", "\\n\\r\\t\\a\\e\\cA\\x41\\x{41}\\o{101}\\Qx\\E", null },
        { "lazy-possessive", "a*?a++a{1,2}+", null },
        { "group-effects", "(?:a)(?=a)(?!b)(?>a)", null },
        { "lookbehind", "a(?<=a)(?<!b)", null },
        { "inline-options", "(?i:a)(?m:^a)(?s:.)(?x: a )", null },
        { "capture-condition", "(a)?(?(1)a|b)", null },
        { "pattern-condition", "(?(a)a|b)", null },
        { "absent-forms", "(?~z)(?~|z|a)(?~|z)(?~|)", null },
        { "content-callout", "(?{x})", null },
        { "named-callout", "(*COUNT)", null },
        { "named-backref-and-calls", "(?<x>a)\\k<x>\\g<x>(?&x)", null },
        { "numeric-perl-call", "(a)(?1)", null },
        { "multiplex-name", "(?<x>a)(?<x>b)\\k<x>", null },
        { "text-and-super-dot", "\\K\\R\\N\\O\\X\\y\\Y", null },
        { "unicode-properties", "\\p{L}\\P{^L}", null },
        { "quoted-name-forms", "(?'x'a)\\k'x'\\g'x'", null },
    };

    [Theory]
    [MemberData(nameof(PerlNgGrammarFamilies))]
    public void ParsedRiskRouterOwnsEveryEnabledPerlNgGrammarFamily(
        string label,
        string suffix,
        string? modifiers)
    {
        var input = new string('a', 24) + "a";
        var pattern = "\\A" + string.Concat(Enumerable.Repeat("a?", 24)) +
            suffix + "(?!)";

        Assert.True(
            OnigurumaCalloutEventRunner.MayRequireRetryAccounting(input, pattern, modifiers),
            label);
    }

    [Fact]
    public void InlineExtendedDisableCannotHideRiskFromParsedRouter()
    {
        var pattern = "\\A(?-x:#?" + string.Concat(Enumerable.Repeat("a?", 24)) +
            "(?!))";

        Assert.True(OnigurumaCalloutEventRunner.MayRequireRetryAccounting(
            new string('a', 24),
            pattern,
            "x"));
    }

    [Fact]
    public void NestedAbsentHighRiskGrammarUsesTheCountedRunner()
    {
        var input = new string('a', 24);
        var pattern = "\\A" + string.Concat(Enumerable.Repeat("a?", 24)) +
            "(?~(?~z))(?!)";

        var error = Assert.Throws<JqRuntimeException>(() => JqRegex.Test(input, pattern));
        Assert.Equal("Regex failure: retry-limit-in-match over", error.Message);
    }

    private static IReadOnlyList<OnigurumaCalloutEventMatch> Run(
        int optionalCount,
        ulong retryLimitInMatch = OnigurumaCalloutEventRunner.DefaultRetryLimitInMatch)
    {
        var input = new string('a', optionalCount);
        var pattern = "\\A" + string.Concat(Enumerable.Repeat("a?", optionalCount)) +
            "(*COUNT)(*FAIL)";
        var handled = OnigurumaCalloutEventRunner.TryRun(
            input,
            pattern,
            modifiers: null,
            timeout: null,
            forceGlobal: false,
            out var matches,
            out var boundary,
            retryLimitInMatch);

        Assert.True(handled, boundary);
        Assert.Null(boundary);
        return matches;
    }

    private static string BuildOrdinaryPattern(int optionalCount) =>
        "\\A" + string.Concat(Enumerable.Repeat("a?", optionalCount)) + "(?!)";

    private static IReadOnlyList<OnigurumaCalloutEventMatch> RunPattern(
        string input,
        string pattern,
        ulong retryLimitInMatch)
    {
        var handled = OnigurumaCalloutEventRunner.TryRun(
            input,
            pattern,
            modifiers: null,
            timeout: null,
            forceGlobal: false,
            out var matches,
            out var boundary,
            retryLimitInMatch);

        Assert.True(handled, boundary);
        Assert.Null(boundary);
        return matches;
    }
}
