using DotNetJq.Compatibility.Regex;
using DotNetJq.Port;

namespace DotNetJq.Tests;

/// <summary>
/// Oracle-backed edge cases from jq-1.8.2's Oniguruma configuration that are not
/// exercised by tests/onig.test. Expected values were captured from the pinned
/// jq-1.8.2 binary; production code never invokes that binary.
/// </summary>
public sealed class RegexCompatibilityRound2Tests
{
    [Fact]
    public void FindLongestSearchesTheWholeRemainingRange()
    {
        var match = Assert.Single(JqRegex.Match("aBBBBccc", "a|c+", "l"));

        Assert.Equal(5, match.Offset);
        Assert.Equal(3, match.Length);
        Assert.Equal("ccc", match.String);
    }

    [Fact]
    public void FindLongestRunsThroughPublicExecutionBoundary()
    {
        var output = Assert.Single(JqProgram.Compile("match(\"a|c+\"; \"l\")")
            .Execute("\"aBBBBccc\""));

        Assert.Equal(5, output.GetProperty("offset").GetInt32());
        Assert.Equal(3, output.GetProperty("length").GetInt32());
        Assert.Equal("ccc", output.GetProperty("string").GetString());
    }

    [Fact]
    public void FindLongestUsesUtf8ByteLengthAndEarliestTie()
    {
        var multibyteWinner = Assert.Single(JqRegex.Match("aaa 😀", "a+|😀", "l"));
        Assert.Equal("😀", multibyteWinner.String);
        Assert.Equal(4, multibyteWinner.Offset);
        Assert.Equal(1, multibyteWinner.Length);

        var earliestTie = Assert.Single(JqRegex.Match("aa é", "a+|é", "l"));
        Assert.Equal("aa", earliestTie.String);
        Assert.Equal(0, earliestTie.Offset);
    }

    [Fact]
    public void FindLongestBacktracksAlternativesAtTheSamePosition()
    {
        var match = Assert.Single(JqRegex.Match("ab", "(?<x>a)|(?<x>ab)", "l"));

        Assert.Equal("ab", match.String);
        Assert.Collection(
            match.Captures,
            first => Assert.Equal(new JqRegexCapture(-1, 0, null, "x"), first),
            second => Assert.Equal(new JqRegexCapture(0, 2, "ab", "x"), second));
    }

    [Fact]
    public void FindLongestGlobalContinuesAfterEachWinner()
    {
        var matches = JqRegex.Match("aa_bb_cc", "a+|b+|c+", "gl");

        Assert.Collection(
            matches,
            first => AssertMatch(first, 0, 2, "aa"),
            second => AssertMatch(second, 3, 2, "bb"),
            third => AssertMatch(third, 6, 2, "cc"));
    }

    [Fact]
    public void FindLongestHonorsFindNotEmpty()
    {
        var match = Assert.Single(JqRegex.Match("ab", "a*?", "ln"));

        AssertMatch(match, 0, 1, "a");
    }

    [Fact]
    public void DuplicateNamedGroupsRemainDistinctCaptures()
    {
        var match = Assert.Single(JqRegex.Match("ab", "(?<x>a)(?<x>b)"));

        Assert.Collection(
            match.Captures,
            first => Assert.Equal(new JqRegexCapture(0, 1, "a", "x"), first),
            second => Assert.Equal(new JqRegexCapture(1, 1, "b", "x"), second));
    }

    [Fact]
    public void DuplicateNamedGroupsPreserveUnmatchedSlots()
    {
        var matches = JqRegex.Match("ab", "(?<x>a)|(?<x>b)", "g");

        Assert.Collection(
            matches,
            first => Assert.Collection(
                first.Captures,
                capture => Assert.Equal(new JqRegexCapture(0, 1, "a", "x"), capture),
                capture => Assert.Equal(new JqRegexCapture(-1, 0, null, "x"), capture)),
            second => Assert.Collection(
                second.Captures,
                capture => Assert.Equal(new JqRegexCapture(-1, 0, null, "x"), capture),
                capture => Assert.Equal(new JqRegexCapture(1, 1, "b", "x"), capture)));
    }

    [Theory]
    [InlineData("abb", "abb")]
    [InlineData("aba", "aba")]
    public void DuplicateNamedBackreferenceTriesGroupsInReverseDefinitionOrder(
        string input,
        string expected)
    {
        var match = Assert.Single(JqRegex.Match(input, "(?<x>a)(?<x>b)\\k<x>"));

        Assert.Equal(expected, match.String);
    }

    [Fact]
    public void PerlNgAcceptsQuotedButRejectsPythonNamedCaptureSpelling()
    {
        var quoted = Assert.Single(JqRegex.Match("aa", "(?'x'a)\\k'x'"));

        Assert.Equal(new JqRegexCapture(0, 1, "a", "x"), Assert.Single(quoted.Captures));
        Assert.Throws<JqRuntimeException>(() => JqRegex.Match("aa", "(?P<x>a)(?P=x)"));
    }

    [Fact]
    public void OnigurumaNewlineAndAnyCharacterEscapesUseScalarSemantics()
    {
        var newlines = JqRegex.Match("x\r\ny\rz\u000bv\u000cf\u0085n\u2028l\u2029p", "\\R", "g");
        Assert.Equal(
            ["\r\n", "\r", "\u000b", "\f", "\u0085", "\u2028", "\u2029"],
            newlines.Select(match => match.String));
        Assert.Empty(JqRegex.Match("\r\n", "\\R\\n"));

        var notLf = Assert.Single(JqRegex.Match("a\rb\nc\u2028d", "\\N+"));
        Assert.Equal("a\rb", notLf.String);

        var any = Assert.Single(JqRegex.Match("😀\n", "\\O+"));
        Assert.Equal("😀\n", any.String);
        Assert.Equal(2, any.Length);
    }

    [Fact]
    public void PosixAlphaClassRunsThroughPublicExecutionBoundary()
    {
        // jq-1.8.2 oracle: "A,b z" | [scan("[[:alpha:]]+")] => ["A","b","z"]
        var output = Assert.Single(JqProgram.Compile("[scan(\"[[:alpha:]]+\")]?")
            .Execute("\"A,b z\""));

        Assert.Equal(["A", "b", "z"],
            output.EnumerateArray().Select(value => value.GetString()));

        Assert.Equal(["Aéα"],
            JqRegex.Scan("Aéα9_", "[[:alpha:]]+").Select(match => match.String));
    }

    [Fact]
    public void UnicodeClassesConsumeSupplementaryScalarsLikeOniguruma()
    {
        const string input = "😀𐐀𝟘";

        Assert.Equal(["𐐀"], JqRegex.Scan(input, "\\p{L}+").Select(match => match.String));
        Assert.Equal(["😀", "𝟘"], JqRegex.Scan(input, "\\P{L}+").Select(match => match.String));
        Assert.Equal(["𐐀𝟘"], JqRegex.Scan(input, "\\w+").Select(match => match.String));
        Assert.Equal(["𝟘"], JqRegex.Scan(input, "\\d+").Select(match => match.String));
        Assert.Equal(["𝟘"], JqRegex.Scan(input, "\\p{N}+").Select(match => match.String));
    }

    [Fact]
    public void GlobalEmptyMatchesAdvanceOneUtf8ByteLikeJq()
    {
        // jq-1.8.2's f_match advances the raw Oniguruma start pointer by one byte.
        // The two-byte and four-byte scalars therefore produce 2 + 4 trailing matches.
        var matches = JqRegex.Match("é😀", "(?:a|b)*", "g");

        Assert.Equal([0, 1, 1, 2, 2, 2, 2], matches.Select(match => match.Offset));
        Assert.All(matches, match =>
        {
            Assert.Equal(0, match.Length);
            Assert.Equal(string.Empty, match.String);
        });

        var output = Assert.Single(JqProgram.Compile("[scan(\"(?:a|b)*\")]?")
            .Execute("\"é😀\""));
        Assert.Equal(7, output.GetArrayLength());
        Assert.All(output.EnumerateArray(), value => Assert.Equal(string.Empty, value.GetString()));
    }

    [Fact]
    public void Utf8ByteAdvancementDoesNotDuplicateAnchorMatches()
    {
        var matches = JqRegex.Match("é", "^|$", "g");

        Assert.Equal([0, 1], matches.Select(match => match.Offset));
    }

    [Fact]
    public void ZeroWidthMatchesUseJqCaptureShape()
    {
        var match = Assert.Single(JqRegex.Match("a", "(?=(?<x>a))", "g"));

        Assert.Equal(new JqRegexCapture(0, 0, string.Empty, "x"), Assert.Single(match.Captures));
    }

    [Fact]
    public void Utf8ByteAdvancementSynthesizesEmptyMatchesBeforeNonEmptyAndAtTail()
    {
        var beforeNonEmpty = JqRegex.Match("aé😀b", "(?:a|b)*", "g");
        Assert.Equal(
            ["a", "", "", "", "", "", "", "b", ""],
            beforeNonEmpty.Select(match => match.String));

        var atTail = JqRegex.Match("é😀", "(?=.)", "g");
        Assert.Equal([0, 1, 1, 2, 2, 2], atTail.Select(match => match.Offset));
    }

    [Fact]
    public void InlineSinglelineOptionsControlDotAndRestoreAtScopeEnd()
    {
        Assert.Single(JqRegex.Match("\n", "(?s:.)"));
        Assert.Empty(JqRegex.Match("\n", "(?-s:.)", "m"));

        var nested = Assert.Single(JqRegex.Match("a\n", "(?s:(?-s:.).)"));
        Assert.Equal("a\n", nested.String);

        Assert.Single(JqRegex.Match("\n", "(?s)."));

        var extended = Assert.Single(JqRegex.Match("a\n", "(?xs:a # ) .\n .)"));
        Assert.Equal("a\n", extended.String);
    }

    [Theory]
    [InlineData("h", "\\h")]
    [InlineData("H", "\\H")]
    [InlineData("v", "\\v")]
    [InlineData("V", "\\V")]
    public void PerlNgUndefinedLetterEscapesAreLiterals(string input, string pattern)
    {
        Assert.True(JqRegex.Test(input, pattern));
    }

    [Fact]
    public void CatastrophicBacktrackingIsStoppedByConfiguredTimeout()
    {
        var input = new string('a', 200_000) + "X";

        var exception = Assert.Throws<JqRuntimeException>(() =>
            JqRegex.Test(input, "^(a+)+$", timeout: TimeSpan.FromMilliseconds(1)));

        Assert.Contains("timed out", exception.Message, StringComparison.Ordinal);
    }

    private static void AssertMatch(JqRegexMatch actual, int offset, int length, string value)
    {
        Assert.Equal(offset, actual.Offset);
        Assert.Equal(length, actual.Length);
        Assert.Equal(value, actual.String);
        Assert.Empty(actual.Captures);
    }
}
