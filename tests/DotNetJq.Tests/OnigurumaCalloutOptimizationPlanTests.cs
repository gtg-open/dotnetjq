using DotNetJq.Compatibility.Regex;

namespace DotNetJq.Tests;

public sealed class OnigurumaCalloutOptimizationPlanTests
{
    [Fact]
    public void AlternationUsesTheUnionMapInsteadOfSemanticEveryStartSearch()
    {
        var plan = Plan(Alt(Literal("z"), Literal("a")));

        Assert.Equal(OnigurumaCalloutOptimizationKind.Map, plan.Kind);
        Assert.Equal([1], plan.EnumerateCandidateStarts("ba"));
        Assert.Equal([(byte)'a', (byte)'z'], plan.MapBytes.Order());
    }

    [Fact]
    public void ExactSearchVisitsOnlyLiteralOccurrences()
    {
        var plan = Plan(Literal("ab"));

        Assert.Equal(OnigurumaCalloutOptimizationKind.Exact, plan.Kind);
        Assert.Equal("ab"u8.ToArray(), plan.ExactUtf8Bytes.ToArray());
        Assert.Equal([2], plan.EnumerateCandidateStarts("axab"));
        Assert.Equal([0, 2], plan.EnumerateCandidateStarts("abab"));
    }

    [Fact]
    public void OptionalAndBoundedPrefixesProduceExactDistanceWindows()
    {
        var optional = Plan(Sequence(Repeat(Literal("x"), 0, 1), Literal("ab")));
        Assert.Equal(0, optional.DistanceMinimum);
        Assert.Equal(1, optional.DistanceMaximum);
        Assert.Equal([1, 2], optional.EnumerateCandidateStarts("xxab"));

        var bounded = Plan(Sequence(Repeat(Literal("x"), 0, 2), Literal("ab")));
        Assert.Equal(0, bounded.DistanceMinimum);
        Assert.Equal(2, bounded.DistanceMaximum);
        Assert.Equal([1, 2, 3], bounded.EnumerateCandidateStarts("yxxab"));
        Assert.Equal(
            [new OnigurumaCalloutCandidateWindow(1, 3)],
            bounded.EnumerateCandidateWindows("yxxab"));

        var nullableAlternative = Plan(Sequence(
            Alt(Literal("x"), new OnigurumaCalloutOptimizerZeroWidth()),
            Literal("a")));
        Assert.Equal([1, 2], nullableAlternative.EnumerateCandidateStarts("bba"));
    }

    [Fact]
    public void UnboundedPrefixUsesTokenAsPresenceCheckThenVisitsEveryStart()
    {
        var plan = Plan(Sequence(Repeat(Literal("x"), 0, null), Literal("ab")));

        Assert.Null(plan.DistanceMaximum);
        Assert.Equal([0, 1, 2, 3, 4, 5, 6], plan.EnumerateCandidateStarts("yyxxab"));
        Assert.Empty(plan.EnumerateCandidateStarts("yyyyyy"));
    }

    [Fact]
    public void PositiveAsciiClassHasMapButNegatedOrMultibyteClassDoesNot()
    {
        var ascii = Plan(CharacterClass(('a', 'a')));
        Assert.Equal(OnigurumaCalloutOptimizationKind.Map, ascii.Kind);
        Assert.Equal([2], ascii.EnumerateCandidateStarts("bba"));

        var multibyte = Plan(CharacterClass(('a', 'a'), ('é', 'é')));
        Assert.Equal(OnigurumaCalloutOptimizationKind.None, multibyte.Kind);
        Assert.Equal([0, 1, 2, 3], multibyte.EnumerateCandidateStarts("bba"));

        var negated = Plan(CharacterClass(true, ('b', 'b')));
        Assert.Equal(OnigurumaCalloutOptimizationKind.None, negated.Kind);
        Assert.Equal([0, 1, 2, 3], negated.EnumerateCandidateStarts("bba"));
    }

    [Fact]
    public void EncodingWideShorthandRangeSchedulesImpossibleAsciiCandidates()
    {
        var word = new OnigurumaCalloutOptimizerVariableWidth(1, 4);
        var plan = Plan(Sequence(Repeat(word, 0, 1), Literal("a")));

        Assert.Equal(0, plan.DistanceMinimum);
        Assert.Equal(4, plan.DistanceMaximum);
        Assert.Equal([1, 2, 3, 4, 5], plan.EnumerateCandidateStarts("bbbbba"));
    }

    [Fact]
    public void Utf8RightAdjustmentNeverCreatesAStartInsideASupplementaryScalar()
    {
        var anyOptional = Plan(Sequence(
            Repeat(new OnigurumaCalloutOptimizerAny(), 0, 1),
            Literal("a")));
        Assert.Equal([0, 2], anyOptional.EnumerateCandidateStarts("😀a"));

        var eAcuteOptional = Plan(Sequence(Repeat(Literal("é"), 0, 1), Literal("a")));
        Assert.Equal(2, eAcuteOptional.DistanceMaximum);
        Assert.Equal([2], eAcuteOptional.EnumerateCandidateStarts("😀a"));
        Assert.Equal([0, 1], eAcuteOptional.EnumerateCandidateStarts("éa"));
    }

    [Fact]
    public void IgnoreCaseKAndSDisableAsciiMapBecauseTheirFoldsAreMultibyte()
    {
        var ordinary = Plan(Literal("a"), ignoreCase: true);
        Assert.Equal(OnigurumaCalloutOptimizationKind.Map, ordinary.Kind);
        Assert.Equal([1], ordinary.EnumerateCandidateStarts("!A"));

        foreach (var special in new[] { "k", "s" })
        {
            var plan = Plan(Literal(special), ignoreCase: true);
            Assert.Equal(OnigurumaCalloutOptimizationKind.None, plan.Kind);
            Assert.Equal([0, 1, 2], plan.EnumerateCandidateStarts("!" + special.ToUpperInvariant()));
        }
    }

    [Fact]
    public void IgnoreCaseAsciiClassMapIncludesBothCases()
    {
        var singleton = Plan(CharacterClass(('a', 'a')), ignoreCase: true);
        Assert.Equal(OnigurumaCalloutOptimizationKind.Map, singleton.Kind);
        Assert.Equal([(byte)'A', (byte)'a'], singleton.MapBytes.Order());
        Assert.Equal([1], singleton.EnumerateCandidateStarts("!A"));

        var range = Plan(CharacterClass(('a', 'c')), ignoreCase: true);
        Assert.Equal(OnigurumaCalloutOptimizationKind.Map, range.Kind);
        Assert.Equal(
            [(byte)'A', (byte)'B', (byte)'C', (byte)'a', (byte)'b', (byte)'c'],
            range.MapBytes.Order());
        Assert.Equal([1], range.EnumerateCandidateStarts("!B"));
        Assert.Empty(range.EnumerateCandidateStarts("!D"));
    }

    [Fact]
    public void IgnoreCaseClassWithKOrSHasNoMapBecauseItsFoldIsMultibyte()
    {
        foreach (var special in new[] { 'K', 'k', 'S', 's' })
        {
            var plan = Plan(CharacterClass((special, special)), ignoreCase: true);
            Assert.Equal(OnigurumaCalloutOptimizationKind.None, plan.Kind);
            Assert.Equal([0, 1, 2], plan.EnumerateCandidateStarts("!" + special));
        }

        var negated = Plan(CharacterClass(true, ('a', 'c')), ignoreCase: true);
        Assert.Equal(OnigurumaCalloutOptimizationKind.None, negated.Kind);
        Assert.Empty(negated.MapBytes);

        var explicitMultibyte = Plan(CharacterClass(('a', 'a'), ('é', 'é')), ignoreCase: true);
        Assert.Equal(OnigurumaCalloutOptimizationKind.None, explicitMultibyte.Kind);
        Assert.Empty(explicitMultibyte.MapBytes);
    }

    [Fact]
    public void AlternationPreservesOnlyCommonExactPrefix()
    {
        var commonOne = Plan(Alt(Literal("ab"), Literal("ac")));
        Assert.Equal(OnigurumaCalloutOptimizationKind.Exact, commonOne.Kind);
        Assert.Equal("a"u8.ToArray(), commonOne.ExactUtf8Bytes.ToArray());
        Assert.Equal([0, 2], commonOne.EnumerateCandidateStarts("axac"));

        var commonTwo = Plan(Alt(Literal("abc"), Literal("abd")));
        Assert.Equal("ab"u8.ToArray(), commonTwo.ExactUtf8Bytes.ToArray());
        Assert.Equal([2], commonTwo.EnumerateCandidateStarts("axabd"));

        var noCommon = Plan(Alt(Literal("ab"), Literal("cd")));
        Assert.Equal(OnigurumaCalloutOptimizationKind.Map, noCommon.Kind);
        Assert.Equal([0, 2], noCommon.EnumerateCandidateStarts("axcd"));
    }

    [Fact]
    public void RootAndSubAnchorsNarrowTheAttemptScheduleLikeForwardSearch()
    {
        var beginWithoutToken = Plan(Sequence(
            Anchor(OnigurumaCalloutOptimizerAnchor.BeginBuffer),
            new OnigurumaCalloutOptimizerAny()));
        Assert.Equal([0, 1], beginWithoutToken.EnumerateCandidateStarts("ab"));

        var beginWithExact = Plan(Sequence(
            Anchor(OnigurumaCalloutOptimizerAnchor.BeginBuffer),
            Literal("a")));
        Assert.Equal([0], beginWithExact.EnumerateCandidateStarts("ab"));
        Assert.Empty(beginWithExact.EnumerateCandidateStarts("x\na"));

        var end = Plan(Sequence(
            Literal("a"),
            Anchor(OnigurumaCalloutOptimizerAnchor.EndBuffer)));
        Assert.Equal([1], end.EnumerateCandidateStarts("ba"));

        var semiEnd = Plan(Sequence(
            Literal("a"),
            Anchor(OnigurumaCalloutOptimizerAnchor.SemiEndBuffer)));
        Assert.Equal([1], semiEnd.EnumerateCandidateStarts("ba\n"));

        var beginLine = Plan(Sequence(
            Anchor(OnigurumaCalloutOptimizerAnchor.BeginLine),
            Literal("a")));
        Assert.Equal([2], beginLine.EnumerateCandidateStarts("x\na"));
    }

    [Fact]
    public void LeadingInfiniteAnyUsesLineStartsOrDotAllRootRange()
    {
        var lineLimited = Plan(Sequence(
            Repeat(new OnigurumaCalloutOptimizerAny(), 0, null),
            Literal("z")));
        Assert.Equal(
            OnigurumaCalloutOptimizerAnchor.AnyCharInfinite,
            lineLimited.RootAnchor & OnigurumaCalloutOptimizerAnchor.AnyCharInfinite);
        Assert.Equal([0, 2], lineLimited.EnumerateCandidateStarts("a\nbz"));

        var dotAll = Plan(Sequence(
            Repeat(new OnigurumaCalloutOptimizerAny(MatchesNewline: true), 0, null),
            Literal("z")));
        Assert.Equal(
            OnigurumaCalloutOptimizerAnchor.AnyCharInfiniteMultiline,
            dotAll.RootAnchor & OnigurumaCalloutOptimizerAnchor.AnyCharInfiniteMultiline);
        Assert.Equal([0, 1], dotAll.EnumerateCandidateStarts("a\nbz"));
    }

    [Fact]
    public void ExactAndMapSearchOnlyAtUtf8ScalarHeads()
    {
        var plan = Plan(Alt(Literal("é"), Literal("a")));

        Assert.Equal(OnigurumaCalloutOptimizationKind.Map, plan.Kind);
        Assert.Equal([1, 2], plan.EnumerateCandidateStarts("xéa"));
    }

    private static OnigurumaCalloutOptimizationPlan Plan(
        OnigurumaCalloutOptimizerNode root,
        bool ignoreCase = false) => OnigurumaCalloutOptimizationPlan.Create(root, ignoreCase);

    private static OnigurumaCalloutOptimizerLiteral Literal(string value) => new(value);

    private static OnigurumaCalloutOptimizerZeroWidth Anchor(
        OnigurumaCalloutOptimizerAnchor anchor) => new(anchor);

    private static OnigurumaCalloutOptimizerSequence Sequence(
        params OnigurumaCalloutOptimizerNode[] nodes) => new(nodes);

    private static OnigurumaCalloutOptimizerAlternation Alt(
        params OnigurumaCalloutOptimizerNode[] alternatives) => new(alternatives);

    private static OnigurumaCalloutOptimizerRepeat Repeat(
        OnigurumaCalloutOptimizerNode body,
        int minimum,
        int? maximum) => new(body, minimum, maximum);

    private static OnigurumaCalloutOptimizerClass CharacterClass(
        params (int Low, int High)[] ranges) => CharacterClass(false, ranges);

    private static OnigurumaCalloutOptimizerClass CharacterClass(
        bool negated,
        params (int Low, int High)[] ranges) => new(
            ranges.Select(range => new OnigurumaCalloutOptimizerRange(range.Low, range.High)).ToArray(),
            negated);
}
