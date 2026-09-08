using DotNetJq.Compatibility.Regex;

namespace DotNetJq.Tests;

public sealed class OnigurumaCalloutEventRunnerTests
{
    [Fact]
    public void CountProgressAndRetractionModesObserveAbandonedIterations()
    {
        AssertMatch(
            "aaaa",
            "(?:(*COUNT[A]{>})a)*(*CMP{A,==,3})",
            2,
            2);
        AssertMatch(
            "aaaa",
            "(?:(*COUNT[A]{<})a)*(*CMP{A,==,1})",
            0,
            4);
        AssertMatch(
            "aaaa",
            "(?:(*COUNT[A]{<})a)*(*CMP{A,==,2})",
            0,
            3);
    }

    [Fact]
    public void TotalCountPersistsAcrossCandidatesAndResetsForEachGlobalSearch()
    {
        const string pattern = ".(*TOTAL_COUNT[T])z|a(*CMP{T,==,2})";
        AssertMatch("ba", pattern, 1, 1);

        var matches = Run("baba", pattern, "g");
        Assert.Equal([1, 3], matches.Select(match => match.Index));
        Assert.All(matches, match => Assert.Equal(1, match.Length));

        // Oniguruma's search optimizer skips starts whose first scalar cannot begin any
        // alternative. A leading wildcard disables that skip and therefore observes b.
        AssertMatch(
            "ba",
            "(*TOTAL_COUNT[T]{>})z|(*CMP{T,==,1})a",
            1,
            1);
        Assert.Empty(Run(
            "ba",
            "(*TOTAL_COUNT[T]{>})z|(*CMP{T,==,2})a"));
        AssertMatch(
            "ba",
            ".(*TOTAL_COUNT[T]{>})z|a(*CMP{T,==,2})",
            1,
            1);

        AssertMatch(
            "xba",
            "x(*TOTAL_COUNT[T]{>})z|(*CMP{T,==,0})a",
            2,
            1);
        Assert.Empty(Run(
            "xba",
            "x(*TOTAL_COUNT[T]{>})z|(*CMP{T,==,1})a"));
    }

    [Fact]
    public void LiteralAndDynamicStateHaveNoManagedCaptureDepthLimit()
    {
        AssertMatch("a", "(*MAX{513})a", 0, 1);

        var input = new string('a', 513);
        AssertMatch(
            input,
            "(?:(*COUNT[T]{X})a)*(*CMP{T,==,513})",
            0,
            513);
        AssertMatch(
            input,
            "(?:(*COUNT[T]{X})a)*(*MAX{T})",
            0,
            513);
    }

    [Fact]
    public void ConditionalMaxRetainsLexicalTransactionalIdentity()
    {
        const string pattern = "^(?:(?(*MAX[T]{2})a|b))*(*CMP{T,==,2})$";
        AssertMatch("aab", pattern, 0, 3);
        Assert.Empty(Run("aaa", pattern));

        const string subcallProgress = "(?<x>(*MAX{1,>})b)c|\\g<x>";
        const string subcallTransactional = "(?<x>(*MAX{1,X})b)c|\\g<x>";
        Assert.Empty(Run("b", subcallProgress));
        AssertMatch("b", subcallTransactional, 0, 1);

        AssertMatch("aaab", "(?:(*MAX{0,<})a)*ab", 0, 4);
    }

    [Fact]
    public void TaggedCmpStateSurvivesOrdinaryBranchRetraction()
    {
        AssertMatch(
            "a",
            "(*CMP[C]{1,<,2})z|(*CMP{C,==,2})a",
            0,
            1);
    }

    [Fact]
    public void SkipUsesTheFurthestExecutionOfOneLexicalSubcallIdentity()
    {
        Assert.Empty(Run("aac", "(?<x>a(*SKIP))\\g<x>b|ac"));
    }

    [Fact]
    public void MismatchAbortsEachCandidateEvenInFindLongestMode()
    {
        AssertMatch("aaab", "aa(*MISMATCH)b|a+", 2, 1, "l");
        AssertMatch("a", "a|a(*MISMATCH)", 0, 1, "l");
        AssertMatch("ab", "a|ab(*MISMATCH)", 0, 1, "l");
        Assert.Empty(Run("a", "a(*MISMATCH)|a", "l"));

        // Oniguruma's FIND_LONGEST score is UTF-8 bytes, not UTF-16 code units.
        AssertMatch(
            "éaa",
            "(*COUNT[A]{>})é|(*COUNT[B]{>})aa",
            0,
            1,
            "l");
    }

    [Fact]
    public void ControlEventsKeepReachabilityAndEscapeNegativeLookaround()
    {
        Assert.Empty(Run("a", "(?!a(*MISMATCH))a"));
        Assert.Empty(Run("b", "(?(*MISMATCH)a|b)"));
        AssertMatch("a", "z(*ERROR)|a", 0, 1);

        var error = Assert.Throws<JqRuntimeException>(() =>
            Run("a", "(?!a(*ERROR))a"));
        Assert.Equal("Regex failure: abort", error.Message);
        var suffix = Assert.Throws<JqRuntimeException>(() =>
            Run("a", "a(*ERROR)z|a"));
        Assert.Equal("Regex failure: abort", suffix.Message);

        Assert.Empty(Run("a", "(a)(?(1)(*MISMATCH)b|c)|a"));
    }

    [Fact]
    public void SharedArgumentLexerPreservesEmptyAndEscapedFields()
    {
        AssertMatch("a", "(*COUNT[T]{,,,})a", 0, 1);
        AssertMatch("a", "(*MAX{,513,,X})a", 0, 1);
        AssertMatch("a", "z(*COUNT[T]{\\,})|a", 0, 1);

        var reached = Assert.Throws<JqRuntimeException>(() =>
            Run("a", "a(*COUNT[T]{\\,})"));
        Assert.Equal("Regex failure: invalid callout arg", reached.Message);
    }

    [Fact]
    public void ErrorLongIsNarrowedToOnigurumaIntBeforeDispatch()
    {
        var wrappedAbort = Assert.Throws<JqRuntimeException>(() =>
            Run("a", "a(*ERROR{-4294967299})"));
        Assert.Equal("Regex failure: abort", wrappedAbort.Message);

        Assert.Empty(Run("a", "a(*ERROR{4294967295})|a"));

        var parameterError = Assert.Throws<JqRuntimeException>(() =>
            Run("a", "a(*ERROR{-217})"));
        Assert.Equal("Regex failure: invalid callout body", parameterError.Message);
    }

    [Fact]
    public void CapturesRetainSourceOrderNamesAndUnmatchedSlots()
    {
        var alternative = Assert.Single(Run(
            "b",
            "(?<x>a)|(?<y>(*COUNT[T]{>})b)"));
        Assert.Equal(2, alternative.Captures.Count);
        Assert.Equal((-1, 0, "x", 1), CaptureTuple(alternative.Captures[0]));
        Assert.Equal((0, 1, "y", 2), CaptureTuple(alternative.Captures[1]));

        var nested = Assert.Single(Run(
            "a",
            "(?<outer>(?<inner>a)(*COUNT[T]{>}))"));
        Assert.Equal((0, 1, "outer", 1), CaptureTuple(nested.Captures[0]));
        Assert.Equal((0, 1, "inner", 2), CaptureTuple(nested.Captures[1]));
    }

    [Fact]
    public void SupplementaryScalarsAreNeverSplitBySearchOrConsumption()
    {
        var global = Run("😀a", "(*COUNT[T]{>}).", "g");
        Assert.Equal([(0, 2), (2, 1)], global.Select(match => (match.Index, match.Length)));

        AssertMatch(
            "😀😀",
            "(?:(*COUNT[T]{>}).)*(*CMP{T,==,3})",
            0,
            4);

        Assert.Empty(Run("😀😀c", "(?<x>😀(*SKIP))\\g<x>b|😀c"));
    }

    [Fact]
    public void NullableRepeatedTransactionalCalloutExecutesOnlyOneEventfulVisit()
    {
        AssertMatch(
            string.Empty,
            "^(?:(?=)(*COUNT[A]{X})){3}(*CMP{A,==,1})$",
            0,
            0);
        AssertMatch(
            string.Empty,
            "^(?:(?=)(*MAX[T]{3})){3}(*CMP{T,==,1})$",
            0,
            0);
    }

    [Fact]
    public void UnsupportedAdjacentGrammarDeclinesTheWholePattern()
    {
        var handled = OnigurumaCalloutEventRunner.TryRun(
            "a",
            "(?<=a)(*COUNT[T]{>})",
            null,
            null,
            forceGlobal: false,
            out var matches,
            out var boundary);

        Assert.True(handled);
        var lookbehind = Assert.Single(matches);
        Assert.Equal((1, 0), (lookbehind.Index, lookbehind.Length));
        Assert.Null(boundary);

        handled = OnigurumaCalloutEventRunner.TryRun(
            "a",
            "(?# (*COUNT[T]{>}) )a",
            null,
            null,
            forceGlobal: false,
            out matches,
            out boundary);
        Assert.False(handled);
        Assert.Null(boundary);

        handled = OnigurumaCalloutEventRunner.TryRun(
            "a",
            "# (*MAX{1})\na",
            "x",
            null,
            forceGlobal: false,
            out matches,
            out boundary);
        Assert.False(handled);
        Assert.Null(boundary);
    }

    [Fact]
    public void AdvancedAbsentFormsComposeWithCalloutEventState()
    {
        AssertMatch(
            "123456789",
            "(*COUNT[T]{>})(?~|78|\\d*)(*CMP{T,==,1})",
            0,
            6);
        AssertMatch(
            "a",
            "(*COUNT[T]{>})(?~|a)(?~|)a(*CMP{T,==,1})",
            0,
            1);

        var expression = Assert.Single(Run(
            "aaab",
            "(*COUNT[T]{>})(?~|(a)b|(x)?a*)(*CMP{T,==,1})"));
        Assert.Equal((0, 2), (expression.Index, expression.Length));
        Assert.Equal(2, expression.Captures.Count);
        Assert.All(expression.Captures, capture => Assert.Equal(-1, capture.Index));
    }

    [Fact]
    public void KeepIsPathScopedClampedAndScoredFromTheCandidate()
    {
        AssertMatch("ab", "(?:a\\Kc|a)b", 0, 2);
        AssertMatch("ab", "(?=ab\\K)a", 1, 0);
        AssertMatch("abc", "(?:ab|a\\Kbc)", 1, 2, "l");
        AssertMatch("a", "a\\K", 1, 0, "gn");
    }

    [Fact]
    public void FlatEventSensitivePatternDoesNotUseTheParserDepthAsAnEvaluatorLimit()
    {
        var literal = new string('a', 5_000);

        AssertMatch(
            literal,
            literal + "(*COUNT[T]{>})(*CMP{T,==,1})",
            0,
            literal.Length);
    }

    [Fact]
    public void RecursiveSubexpressionCallsUseTheNativeNestingBoundary()
    {
        const string pattern =
            "\\A(?<x>a|b\\g<x>c)(*COUNT[T]{X})(*CMP{T,==,1})\\z";
        var accepted = new string('b', 19) + 'a' + new string('c', 19);
        var rejected = new string('b', 20) + 'a' + new string('c', 20);

        AssertMatch(accepted, pattern, 0, accepted.Length);
        Assert.Empty(Run(rejected, pattern));

        var unanchored = Assert.Single(Run(
            rejected,
            "(?<x>a|b\\g<x>c)(*COUNT[T]{X})(*CMP{T,==,1})"));
        Assert.Equal((1, 39), (unanchored.Index, unanchored.Length));

        const string mutual =
            "\\A(?:(?<x>a|b\\g<y>c)(?<y>a|b\\g<x>c)|\\g<x>)" +
            "(*COUNT[T]{X})(*CMP{T,==,1})\\z";
        AssertMatch(accepted, mutual, 0, accepted.Length);
        Assert.Empty(Run(rejected, mutual));
    }

    [Fact]
    public void ParserUsesJqConfiguredOnigurumaDepthBoundary()
    {
        var accepted = string.Concat(Enumerable.Repeat("(?:", 511)) + 'a' +
            new string(')', 511) + "(*COUNT[T]{X})(*CMP{T,==,1})";
        AssertMatch("a", accepted, 0, 1);

        var rejected = string.Concat(Enumerable.Repeat("(?:", 512)) + 'a' +
            new string(')', 512) + "(*COUNT[T]{X})(*CMP{T,==,1})";
        Assert.Equal(
            "Regex failure: parse depth limit over",
            Assert.Throws<JqRuntimeException>(() => Run("a", rejected)).Message);
    }

    [Fact]
    public void LargeRepeatsUseHeapBacktrackingStateAndPreserveCalloutOwnership()
    {
        var input = new string('a', 10_000);
        AssertMatch(
            input,
            "\\Aa*(*COUNT[T]{X})(*CMP{T,==,1})\\z",
            0,
            input.Length);
        AssertMatch(
            input,
            "\\Aa{10000}(*COUNT[T]{X})(*CMP{T,==,1})\\z",
            0,
            input.Length);
        AssertMatch(
            input,
            "\\Aa*(*COUNT[A]{>})b|a{10000}(*COUNT[B]{>})" +
            "(*CMP{A,==,10001})(*CMP{B,==,1})\\z",
            0,
            input.Length);
    }

    private static void AssertMatch(
        string input,
        string pattern,
        int expectedIndex,
        int expectedLength,
        string? modifiers = null)
    {
        var match = Assert.Single(Run(input, pattern, modifiers));
        Assert.Equal((expectedIndex, expectedLength), (match.Index, match.Length));
    }

    private static IReadOnlyList<OnigurumaCalloutEventMatch> Run(
        string input,
        string pattern,
        string? modifiers = null)
    {
        var handled = OnigurumaCalloutEventRunner.TryRun(
            input,
            pattern,
            modifiers,
            null,
            forceGlobal: false,
            out var matches,
            out var boundary);

        Assert.True(handled, boundary);
        Assert.Null(boundary);
        return matches;
    }

    private static (int Index, int Length, string? Name, int Number) CaptureTuple(
        OnigurumaCalloutEventCapture capture) =>
        (capture.Index, capture.Length, capture.Name, capture.GroupNumber);
}
