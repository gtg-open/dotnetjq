// DOTNETJQ COMPATIBILITY PROXY
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Semantic sources: vendor/oniguruma/src/regparse.c (grammar/absent lowering),
// vendor/oniguruma/src/regcomp.c (lookbehind tuning/widths), and
// vendor/oniguruma/src/regexec.c (built-in/content callouts and search state)
// UPSTREAM COMPONENT: Oniguruma backtracking callout events and per-search callout data.
// REPLACEMENT: a deliberately small managed backtracking runner for patterns whose callout
// event history cannot be represented by System.Text.RegularExpressions capture stacks.
// WHY: .NET balancing captures retain the successful path, but COUNT/TOTAL_COUNT/MAX/CMP,
// MISMATCH, and SKIP can observe or change state on paths which are subsequently abandoned.
// BEHAVIORAL CONTRACT: preserve progress/retraction ordering, lexical callout identity,
// candidate-local versus search-wide state, and ordinary/fatal/candidate-abort outcomes.
// jq registers no application-defined content callback, so a valid content callout is a zero-width
// success while its optional tag retains the normal callout identity and zero-valued state. Nested
// absent functions are undefined upstream and fail closed. Any syntax outside the source-verified
// event grammar is rejected explicitly rather than executed with silently different state ordering.
// TESTS COVERING THE SUBSTITUTION: OnigurumaCalloutEventRunnerTests,
// RegexCompatibilityRound3Tests, and the exhaustive regex differential corpus.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using DotNetJq.Port;

namespace DotNetJq.Compatibility.Regex;

internal sealed record OnigurumaCalloutEventCapture(
    int Index,
    int Length,
    string? Value,
    string? Name,
    int GroupNumber,
    int ByteIndex,
    int ByteLength,
    int JqIndex,
    int JqLength);

internal sealed record OnigurumaCalloutEventMatch(
    int Index,
    int Length,
    string Value,
    IReadOnlyList<OnigurumaCalloutEventCapture> Captures,
    int ByteIndex,
    int ByteLength,
    int JqIndex,
    int JqLength);

/// <summary>
/// Executes the event-sensitive subset of Oniguruma callout patterns. The entry point is
/// intentionally string based so JqRegex can route before constructing its private callout
/// definitions. A false return with a non-null boundary means the pattern needs this runner
/// but contains grammar which has not been proved equivalent; callers must fail closed.
/// </summary>
internal static class OnigurumaCalloutEventRunner
{
    // jq's src/main.c overrides Oniguruma's 4,096 default with
    // onig_set_parse_depth_limit(1024).
    private const int MaximumParseDepth = 1_024;
    private const int MaximumSubexpressionCallNesting = 20;
    // vendor/oniguruma/src/regint.h:DEFAULT_RETRY_LIMIT_IN_MATCH. This is a
    // deterministic count of regexec.c `fail:` stack pops, not a time budget.
    internal const ulong DefaultRetryLimitInMatch = 10_000_000;
    private static readonly TimeSpan DefaultTimeout = System.Threading.Timeout.InfiniteTimeSpan;

    internal static bool TryRun(
        string input,
        string pattern,
        string? modifiers,
        TimeSpan? timeout,
        bool forceGlobal,
        out IReadOnlyList<OnigurumaCalloutEventMatch> matches,
        out string? unsupportedBoundary,
        ulong retryLimitInMatch = DefaultRetryLimitInMatch,
        bool requireSourceShapedExecution = false)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(pattern);

        matches = [];
        unsupportedBoundary = null;

        var flags = ParseModifiers(modifiers);

        CompiledPattern compiled;
        try
        {
            compiled = new Parser(pattern, flags).Parse();
        }
        catch (UnsupportedGrammarException exception)
        {
            if (!requireSourceShapedExecution &&
                !ContainsEventSensitiveCalloutText(pattern, modifiers))
            {
                return false;
            }

            unsupportedBoundary = exception.Message;
            return false;
        }

        if (!requireSourceShapedExecution && !compiled.EventSensitive &&
            !ContainsEventSensitiveModifier(modifiers, compiled) &&
            !compiled.RequiresSourceShapedExecution &&
            !MayReachRetryLimit(compiled.Root, input, retryLimitInMatch))
        {
            return false;
        }

        var context = new MatchContext(
            input,
            compiled,
            timeout ?? DefaultTimeout,
            flags.FindLongest,
            flags.IgnoreCase,
            flags.DotMatchesNewline,
            retryLimitInMatch);
        matches = context.Run(flags.Global || forceGlobal, flags.IgnoreEmpty);
        return true;
    }

    internal static bool MayRequireRetryAccounting(
        string input,
        string pattern,
        string? modifiers = null,
        ulong retryLimitInMatch = DefaultRetryLimitInMatch)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(pattern);
        var flags = ParseModifiers(modifiers);
        try
        {
            var compiled = new Parser(pattern, flags).Parse();
            return compiled.RequiresSourceShapedExecution ||
                MayReachRetryLimit(compiled.Root, input, retryLimitInMatch);
        }
        catch (UnsupportedGrammarException)
        {
            // The runner parser owns every construct enabled by jq's pinned
            // ONIG_SYNTAX_PERL_NG. Unsupported boundaries are malformed source
            // spellings; the ordinary compilation path emits their jq-shaped
            // diagnostic and cannot execute them deeply enough to consume retry
            // state.
            return false;
        }
        catch (JqRuntimeException exception) when (!IsSourceOwnedParserDiagnostic(exception.Message))
        {
            // Semantic compile failures (undefined groups, malformed callouts,
            // invalid lookbehind, and similar cases) likewise never enter the
            // native match loop. Preserve the central validator's exact error.
            return false;
        }
    }

    private static bool IsSourceOwnedParserDiagnostic(string message) =>
        message is
            "Regex failure: target of repeat operator is invalid" or
            "Regex failure: target of repeat operator is not specified" or
            "Regex failure: too big number for repeat range" or
            "Regex failure: upper is smaller than lower in repeat range" or
            "Regex failure: empty range in char class" or
            "Regex failure: empty char-class" or
            "Regex failure: char-class value at end of range" or
            "Regex failure: too big number" or
            "Regex failure: too short multibyte code string" or
            "Regex failure: invalid code point value" or
            "Regex failure: invalid backref number/name" or
            "Regex failure: group name is empty" or
            "Regex failure: end pattern at control" or
            "Regex failure: undefined group option" or
            "Regex failure: unmatched close parenthesis" or
            "Regex failure: never ending recursion" or
            "Regex failure: parse depth limit over" or
            "Regex failure: invalid POSIX bracket type" or
            "Regex failure: end pattern with unmatched parenthesis" ||
        message.StartsWith("Regex failure: undefined name <", StringComparison.Ordinal) ||
        message.StartsWith("Regex failure: invalid group name <", StringComparison.Ordinal) ||
        message.StartsWith(
            "Regex failure: invalid char in group name <",
            StringComparison.Ordinal) ||
        message.StartsWith(
            "Regex failure: invalid character property name {",
            StringComparison.Ordinal);

    private static RunnerFlags ParseModifiers(string? modifiers)
    {
        var global = false;
        var findLongest = false;
        var ignoreEmpty = false;
        var ignoreCase = false;
        var extended = false;
        var dotMatchesNewline = false;
        var singleLineAnchors = false;
        foreach (var modifier in modifiers ?? string.Empty)
        {
            switch (modifier)
            {
                case 'g':
                    global = true;
                    break;
                case 'l':
                    findLongest = true;
                    break;
                case 'n':
                    ignoreEmpty = true;
                    break;
                case 'i':
                    ignoreCase = true;
                    break;
                case 'x':
                    extended = true;
                    break;
                case 'm':
                    dotMatchesNewline = true;
                    break;
                case 's':
                    singleLineAnchors = true;
                    break;
                case 'p':
                    dotMatchesNewline = true;
                    singleLineAnchors = true;
                    break;
                default:
                    throw new JqRuntimeException((modifiers ?? string.Empty) +
                        " is not a valid modifier string");
            }
        }

        return new RunnerFlags(
            global,
            findLongest,
            ignoreEmpty,
            ignoreCase,
            extended,
            dotMatchesNewline,
            singleLineAnchors);
    }

    private static bool ContainsEventSensitiveModifier(string? modifiers, CompiledPattern compiled) =>
        modifiers?.Contains('l', StringComparison.Ordinal) == true && compiled.HasMismatch;

    private static bool MayReachRetryLimit(Node root, string input, ulong retryLimitInMatch)
    {
        if (retryLimitInMatch == 0)
        {
            return false;
        }

        var inputScalarCount = input.EnumerateRunes().Count();
        return PotentialRetryWork(root, inputScalarCount, retryLimitInMatch) >= retryLimitInMatch;
    }

    private static ulong PotentialRetryWork(Node node, int inputScalarCount, ulong cap)
    {
        switch (node)
        {
            case SequenceNode sequence:
            {
                var paths = 1UL;
                var work = 0UL;
                foreach (var child in sequence.Nodes)
                {
                    work = SaturatingAdd(
                        work,
                        SaturatingMultiply(
                            paths,
                            PotentialRetryWork(child, inputScalarCount, cap),
                            cap),
                        cap);
                    paths = SaturatingMultiply(
                        paths,
                        PotentialPathCount(child, inputScalarCount, cap),
                        cap);
                }

                return work;
            }
            case AlternationNode alternation:
                return alternation.Alternatives.Aggregate(
                    0UL,
                    (work, child) => SaturatingAdd(
                        work,
                        PotentialRetryWork(child, inputScalarCount, cap),
                        cap));
            case GroupNode group:
                return PotentialRetryWork(group.Body, inputScalarCount, cap);
            case AtomicNode atomic:
                return PotentialRetryWork(atomic.Body, inputScalarCount, cap);
            case OptionScopeNode option:
                return PotentialRetryWork(option.Body, inputScalarCount, cap);
            case RepeatNode repeat:
            {
                var repeatPaths = PotentialRepeatPathCount(
                    PotentialPathCount(repeat.Body, inputScalarCount, cap),
                    repeat.Minimum,
                    repeat.Maximum,
                    inputScalarCount,
                    cap);
                var iterations = repeat.Maximum == 0
                    ? 0UL
                    : Math.Max(1UL, (ulong)inputScalarCount);
                if (repeat.Maximum >= 0)
                {
                    iterations = Math.Min(iterations, (ulong)repeat.Maximum);
                }

                var bodyWork = SaturatingMultiply(
                    PotentialRetryWork(repeat.Body, inputScalarCount, cap),
                    iterations,
                    cap);
                return SaturatingMultiply(
                    repeatPaths,
                    SaturatingAdd(bodyWork, 1, cap),
                    cap);
            }
            case LookaroundNode lookaround:
                return SaturatingAdd(
                    PotentialRetryWork(lookaround.Body, inputScalarCount, cap),
                    1,
                    cap);
            case LookbehindNode lookbehind:
                return SaturatingAdd(
                    PotentialRetryWork(lookbehind.Body, inputScalarCount, cap),
                    1,
                    cap);
            case CaptureConditionalNode conditional:
                return SaturatingAdd(
                    PotentialRetryWork(conditional.WhenTrue, inputScalarCount, cap),
                    PotentialRetryWork(conditional.WhenFalse, inputScalarCount, cap),
                    cap);
            case PatternConditionalNode conditional:
                return SaturatingAdd(
                    PotentialRetryWork(conditional.Condition, inputScalarCount, cap),
                    SaturatingAdd(
                        PotentialRetryWork(conditional.WhenTrue, inputScalarCount, cap),
                        PotentialRetryWork(conditional.WhenFalse, inputScalarCount, cap),
                        cap),
                    cap);
            case ConditionalCalloutNode conditional:
                return SaturatingAdd(
                    PotentialRetryWork(conditional.WhenTrue, inputScalarCount, cap),
                    PotentialRetryWork(conditional.WhenFalse, inputScalarCount, cap),
                    cap);
            case AbsentRangeNode or AbsentExpressionNode or AbsentStopperNode:
                return cap;
            case SubexpressionCallNode:
                return cap;
            default:
                // One is deliberately conservative: an atom may take a native
                // fail edge even though it succeeds for this particular input.
                return 1;
        }
    }

    private static ulong PotentialPathCount(Node node, int inputScalarCount, ulong cap) => node switch
    {
        SequenceNode sequence => sequence.Nodes.Aggregate(
            1UL,
            (paths, child) => SaturatingMultiply(
                paths,
                PotentialPathCount(child, inputScalarCount, cap),
                cap)),
        AlternationNode alternation => alternation.Alternatives.Aggregate(
            0UL,
            (paths, child) => SaturatingAdd(
                paths,
                PotentialPathCount(child, inputScalarCount, cap),
                cap)),
        GroupNode group => PotentialPathCount(group.Body, inputScalarCount, cap),
        AtomicNode atomic => PotentialPathCount(atomic.Body, inputScalarCount, cap),
        OptionScopeNode option => PotentialPathCount(option.Body, inputScalarCount, cap),
        RepeatNode repeat => PotentialRepeatPathCount(
            PotentialPathCount(repeat.Body, inputScalarCount, cap),
            repeat.Minimum,
            repeat.Maximum,
            inputScalarCount,
            cap),
        LookaroundNode lookaround => PotentialPathCount(
            lookaround.Body,
            inputScalarCount,
            cap),
        LookbehindNode lookbehind => PotentialPathCount(
            lookbehind.Body,
            inputScalarCount,
            cap),
        CaptureConditionalNode conditional => SaturatingAdd(
            PotentialPathCount(conditional.WhenTrue, inputScalarCount, cap),
            PotentialPathCount(conditional.WhenFalse, inputScalarCount, cap),
            cap),
        PatternConditionalNode conditional => SaturatingMultiply(
            PotentialPathCount(conditional.Condition, inputScalarCount, cap),
            SaturatingAdd(
                PotentialPathCount(conditional.WhenTrue, inputScalarCount, cap),
                PotentialPathCount(conditional.WhenFalse, inputScalarCount, cap),
                cap),
            cap),
        ConditionalCalloutNode conditional => SaturatingAdd(
            PotentialPathCount(conditional.WhenTrue, inputScalarCount, cap),
            PotentialPathCount(conditional.WhenFalse, inputScalarCount, cap),
            cap),
        AbsentRangeNode absent => SaturatingMultiply(
            PotentialPathCount(absent.Forbidden, inputScalarCount, cap),
            (ulong)inputScalarCount + 1,
            cap),
        AbsentExpressionNode absent => SaturatingMultiply(
            SaturatingMultiply(
                PotentialPathCount(absent.Forbidden, inputScalarCount, cap),
                PotentialPathCount(absent.Expression, inputScalarCount, cap),
                cap),
            (ulong)inputScalarCount + 1,
            cap),
        AbsentStopperNode absent => SaturatingMultiply(
            PotentialPathCount(absent.Forbidden, inputScalarCount, cap),
            (ulong)inputScalarCount + 1,
            cap),
        // A subexpression call can revisit an alternative-bearing group up to
        // the separate native nesting boundary. Treat it as risky instead of
        // pretending the lexical call site is a single deterministic path.
        SubexpressionCallNode => cap,
        _ => 1,
    };

    private static ulong PotentialRepeatPathCount(
        ulong bodyPaths,
        int minimum,
        int maximum,
        int inputScalarCount,
        ulong cap)
    {
        // This is a conservative source-structure bound, used only to decide
        // whether .NET's opaque backtracker is safe to retain. The counted
        // runner remains the oracle for the actual fail/pop total. A consuming
        // path can repeat at most once per input scalar; Oniguruma's empty-repeat
        // guard prevents nullable bodies from creating unbounded iterations.
        var maximumIterations = (ulong)inputScalarCount;
        if (maximum >= 0)
        {
            maximumIterations = Math.Min(maximumIterations, (ulong)maximum);
        }

        var minimumIterations = Math.Min((ulong)Math.Max(0, minimum), maximumIterations);
        if (minimum > 0 && (ulong)minimum > maximumIterations)
        {
            return SaturatingAdd(
                SaturatingPower(bodyPaths, maximumIterations, cap),
                1,
                cap);
        }

        if (bodyPaths == 1)
        {
            return Math.Min(cap, maximumIterations - minimumIterations + 2);
        }

        var result = 0UL;
        var term = 1UL;
        for (var iteration = 0UL; iteration <= maximumIterations; iteration++)
        {
            if (iteration >= minimumIterations)
            {
                result = SaturatingAdd(result, term, cap);
                if (result == cap)
                {
                    return cap;
                }
            }

            term = SaturatingMultiply(term, bodyPaths, cap);
        }

        // Non-peek repeat opcodes can also fail once while probing whether the
        // body can expand again. Some literal/any opcodes optimize that probe
        // to a jump instead; retaining the extra one here is conservative and
        // lets the counted runner make the exact determination.
        return SaturatingAdd(result, 1, cap);
    }

    private static ulong SaturatingPower(ulong value, ulong exponent, ulong cap)
    {
        var result = 1UL;
        while (exponent-- != 0)
        {
            result = SaturatingMultiply(result, value, cap);
            if (result == cap)
            {
                break;
            }
        }

        return result;
    }

    private static ulong SaturatingAdd(ulong left, ulong right, ulong cap) =>
        left >= cap || right >= cap - left ? cap : left + right;

    private static ulong SaturatingMultiply(ulong left, ulong right, ulong cap) =>
        left == 0 || right == 0
            ? 0
            : left >= cap || right >= (cap + left - 1) / left
                ? cap
                : left * right;

    private static bool ContainsEventSensitiveCalloutText(string pattern, string? modifiers)
    {
        var extended = modifiers?.Contains('x', StringComparison.Ordinal) == true;
        for (var index = 0; index < pattern.Length; index++)
        {
            if (pattern[index] == '\\')
            {
                if (index + 1 < pattern.Length && pattern[index + 1] == 'Q')
                {
                    var quoteEnd = pattern.IndexOf("\\E", index + 2, StringComparison.Ordinal);
                    if (quoteEnd < 0)
                    {
                        break;
                    }

                    index = quoteEnd + 1;
                    continue;
                }

                index++;
                continue;
            }

            if (pattern[index] == '[')
            {
                for (index++; index < pattern.Length && pattern[index] != ']'; index++)
                {
                    if (pattern[index] == '\\')
                    {
                        index++;
                    }
                }

                continue;
            }

            if (pattern.AsSpan(index).StartsWith("(?#", StringComparison.Ordinal))
            {
                index += 3;
                while (index < pattern.Length && pattern[index] != ')')
                {
                    if (pattern[index] == '\\')
                    {
                        index++;
                    }

                    index++;
                }

                continue;
            }

            if (extended && pattern[index] == '#')
            {
                index = pattern.IndexOf('\n', index + 1);
                if (index < 0)
                {
                    break;
                }

                continue;
            }

            if (!pattern.AsSpan(index).StartsWith("(*", StringComparison.Ordinal))
            {
                continue;
            }

            var nameStart = index + 2;
            var nameEnd = nameStart;
            while (nameEnd < pattern.Length && pattern[nameEnd] is not (')' or '[' or '{'))
            {
                nameEnd++;
            }

            var name = pattern[nameStart..nameEnd];
            if (name is "COUNT" or "TOTAL_COUNT" or "MAX" or "CMP" or "SKIP" or
                "MISMATCH" or "ERROR")
            {
                return true;
            }
        }

        return false;
    }

    private static OnigurumaCalloutOptimizerNode BuildOptimizerNode(
        Node node,
        RuntimeRegexOptions options)
    {
        return node switch
        {
            EmptyNode or CommentNode or CalloutNode or ResetMatchStartNode =>
                new OnigurumaCalloutOptimizerZeroWidth(),
            SequenceNode sequence => BuildOptimizerSequence(sequence, options),
            AlternationNode alternation => new OnigurumaCalloutOptimizerAlternation(
                alternation.Alternatives.Select(child =>
                    BuildOptimizerNode(child, options)).ToArray()),
            LiteralNode literal => BuildOptimizerLiteral(literal.Value, options.IgnoreCase),
            OrdinaryAtomNode ordinary => BuildOptimizerAtom(ordinary.Token, options.IgnoreCase),
            AnyNode => new OnigurumaCalloutOptimizerAny(options.DotMatchesNewline),
            CharacterClassNode characterClass => BuildOptimizerClass(
                characterClass,
                options.IgnoreCase),
            AnchorNode anchor => new OnigurumaCalloutOptimizerZeroWidth(anchor.Kind switch
            {
                AnchorKind.Start => OnigurumaCalloutOptimizerAnchor.BeginBuffer,
                AnchorKind.SearchStart => OnigurumaCalloutOptimizerAnchor.None,
                AnchorKind.End => OnigurumaCalloutOptimizerAnchor.EndBuffer,
                AnchorKind.EndBeforeFinalNewline => OnigurumaCalloutOptimizerAnchor.SemiEndBuffer,
                AnchorKind.LineStart => options.MultilineAnchors
                    ? OnigurumaCalloutOptimizerAnchor.BeginLine
                    : OnigurumaCalloutOptimizerAnchor.BeginBuffer,
                AnchorKind.LineEnd => options.MultilineAnchors
                    ? OnigurumaCalloutOptimizerAnchor.EndLine
                    : OnigurumaCalloutOptimizerAnchor.SemiEndBuffer,
                _ => OnigurumaCalloutOptimizerAnchor.None,
            }),
            GroupNode group => BuildOptimizerNode(group.Body, options),
            AtomicNode atomic => BuildOptimizerNode(atomic.Body, options),
            RepeatNode repeat => new OnigurumaCalloutOptimizerRepeat(
                BuildOptimizerNode(repeat.Body, options),
                repeat.Minimum,
                repeat.Maximum < 0 ? null : repeat.Maximum,
                !repeat.Lazy),
            CaptureConditionalNode conditional => new OnigurumaCalloutOptimizerAlternation(
                [
                    BuildOptimizerNode(conditional.WhenTrue, options),
                    BuildOptimizerNode(conditional.WhenFalse, options),
                ]),
            PatternConditionalNode conditional => new OnigurumaCalloutOptimizerAlternation(
                [
                    BuildOptimizerSequence(
                        new SequenceNode([conditional.Condition, conditional.WhenTrue]),
                        options),
                    BuildOptimizerNode(conditional.WhenFalse, options),
                ]),
            ConditionalCalloutNode conditional => new OnigurumaCalloutOptimizerAlternation(
                [
                    BuildOptimizerNode(conditional.WhenTrue, options),
                    BuildOptimizerNode(conditional.WhenFalse, options),
                ]),
            OptionScopeNode optionScope => BuildOptimizerNode(
                optionScope.Body,
                options.Apply(optionScope.Enabled, optionScope.Disabled)),
            OptionChangeNode => new OnigurumaCalloutOptimizerZeroWidth(),
            LookbehindNode => new OnigurumaCalloutOptimizerZeroWidth(
                OnigurumaCalloutOptimizerAnchor.LookBehind),
            _ => new OnigurumaCalloutOptimizerUnknown(),
        };
    }

    private static OnigurumaCalloutOptimizerSequence BuildOptimizerSequence(
        SequenceNode sequence,
        RuntimeRegexOptions options)
    {
        var nodes = new List<OnigurumaCalloutOptimizerNode>(sequence.Nodes.Count);
        var current = options;
        foreach (var child in sequence.Nodes)
        {
            nodes.Add(BuildOptimizerNode(child, current));
            if (child is OptionChangeNode optionChange)
            {
                current = current.Apply(optionChange.Enabled, optionChange.Disabled);
            }
        }

        return new OnigurumaCalloutOptimizerSequence(nodes);
    }

    private static OnigurumaCalloutOptimizerNode BuildOptimizerLiteral(
        string value,
        bool ignoreCase)
    {
        if (!ignoreCase)
        {
            return new OnigurumaCalloutOptimizerLiteral(value);
        }

        if (OnigurumaCaseFold.RequiresFullFold(value))
        {
            // Full folds change scalar width and can begin after the first
            // scalar in a compiled string node. A fixed-position literal plan
            // would skip valid candidates (for example, ff <-> ligature ff).
            // Oniguruma likewise declines a fixed byte-map optimization here.
            return new OnigurumaCalloutOptimizerUnknown();
        }

        var nodes = new List<OnigurumaCalloutOptimizerNode>();
        var literal = new StringBuilder();
        void FlushLiteral()
        {
            if (literal.Length > 0)
            {
                nodes.Add(new OnigurumaCalloutOptimizerLiteral(literal.ToString()));
                literal.Clear();
            }
        }

        foreach (var rune in value.EnumerateRunes())
        {
            FlushLiteral();
            var runeText = rune.ToString();
            if (OnigurumaCaseFold.TryGetFirstByteMap(runeText, out var map))
            {
                nodes.Add(new OnigurumaCalloutOptimizerClass(
                    MapToOptimizerRanges(map),
                    false));
            }
            else
            {
                nodes.Add(new OnigurumaCalloutOptimizerVariableWidth(1, 12));
            }
        }

        FlushLiteral();
        return nodes.Count == 1
            ? nodes[0]
            : new OnigurumaCalloutOptimizerSequence(nodes);
    }

    private static OnigurumaCalloutOptimizerNode BuildOptimizerAtom(
        OnigurumaCalloutAtomToken token,
        bool ignoreCase)
    {
        if (token.Kind == OnigurumaCalloutAtomKind.Literal)
        {
            return BuildOptimizerLiteral(token.Literal!, ignoreCase);
        }

        if (token.Kind == OnigurumaCalloutAtomKind.Property)
        {
            return BuildOptimizerRanges(token.Ranges!, token.Negated, ignoreCase);
        }

        return token.Kind switch
        {
            OnigurumaCalloutAtomKind.WordBoundary or
            OnigurumaCalloutAtomKind.NonWordBoundary or
            OnigurumaCalloutAtomKind.TextBoundary or
            OnigurumaCalloutAtomKind.TextNonBoundary =>
                new OnigurumaCalloutOptimizerZeroWidth(),
            OnigurumaCalloutAtomKind.GeneralNewline =>
                new OnigurumaCalloutOptimizerVariableWidth(1, 3),
            OnigurumaCalloutAtomKind.TextCluster =>
                new OnigurumaCalloutOptimizerVariableWidth(1, null),
            OnigurumaCalloutAtomKind.NotNewline or
            OnigurumaCalloutAtomKind.AnyScalar =>
                new OnigurumaCalloutOptimizerVariableWidth(1, 4),
            _ => new OnigurumaCalloutOptimizerUnknown(),
        };
    }

    private static OnigurumaCalloutOptimizerNode BuildOptimizerClass(
        CharacterClassNode characterClass,
        bool ignoreCase)
    {
        // Ordinary ASCII class members live in both the byte bitset and the
        // scalar member list. They remain eligible for Oniguruma's MAP search
        // optimization. Only a high crude byte has no scalar-aligned managed
        // search representation and must force the conservative width plan.
        if (characterClass.RawSingleBytes.Any(value => value >= 0x80))
        {
            return new OnigurumaCalloutOptimizerVariableWidth(1, 4);
        }

        var ranges = new List<OnigurumaCalloutOptimizerRange>();
        foreach (var member in characterClass.Members)
        {
            if (member.Kind == OnigurumaCalloutAtomKind.Property && !member.Negated)
            {
                ranges.AddRange(ToOptimizerRanges(member.Ranges!));
                continue;
            }

            if (member.Kind == OnigurumaCalloutAtomKind.Literal &&
                member.Literal is { Length: > 0 } literal)
            {
                var rune = Rune.GetRuneAt(literal, 0);
                if (rune.Utf16SequenceLength == literal.Length)
                {
                    ranges.Add(new OnigurumaCalloutOptimizerRange(rune.Value, rune.Value));
                    continue;
                }
            }

            return new OnigurumaCalloutOptimizerVariableWidth(1, 4);
        }

        if (!ignoreCase)
        {
            return new OnigurumaCalloutOptimizerClass(ranges, characterClass.Negated);
        }

        var foldRanges = ranges.Select(range => (range.Low, range.High)).ToArray();
        return OnigurumaCaseFold.TryGetFirstByteMap(
            foldRanges,
            characterClass.Negated,
            out var map)
            ? new OnigurumaCalloutOptimizerClass(MapToOptimizerRanges(map), false)
            : new OnigurumaCalloutOptimizerVariableWidth(1, 12);
    }

    private static OnigurumaCalloutOptimizerNode BuildOptimizerRanges(
        int[] sourceRanges,
        bool negated,
        bool ignoreCase)
    {
        var ranges = ToOptimizerRanges(sourceRanges);
        if (!ignoreCase)
        {
            return new OnigurumaCalloutOptimizerClass(ranges, negated);
        }

        var foldRanges = ranges.Select(range => (range.Low, range.High)).ToArray();
        return OnigurumaCaseFold.TryGetFirstByteMap(foldRanges, negated, out var map)
            ? new OnigurumaCalloutOptimizerClass(MapToOptimizerRanges(map), false)
            : new OnigurumaCalloutOptimizerVariableWidth(1, 12);
    }

    private static OnigurumaCalloutOptimizerRange[] MapToOptimizerRanges(byte[] map) =>
        map.Select((included, value) => (included, value))
            .Where(entry => entry.included != 0)
            .Select(entry => new OnigurumaCalloutOptimizerRange(entry.value, entry.value))
            .ToArray();

    private static OnigurumaCalloutOptimizerRange[] ToOptimizerRanges(int[] ranges)
    {
        var result = new OnigurumaCalloutOptimizerRange[ranges.Length / 2];
        for (var index = 0; index < result.Length; index++)
        {
            result[index] = new OnigurumaCalloutOptimizerRange(
                ranges[index * 2],
                ranges[index * 2 + 1]);
        }

        return result;
    }

    private static bool ContainsKeep(Node node) => node switch
    {
        ResetMatchStartNode => true,
        SequenceNode sequence => sequence.Nodes.Any(ContainsKeep),
        AlternationNode alternation => alternation.Alternatives.Any(ContainsKeep),
        GroupNode group => ContainsKeep(group.Body),
        AtomicNode atomic => ContainsKeep(atomic.Body),
        RepeatNode repeat => ContainsKeep(repeat.Body),
        LookaroundNode lookaround => ContainsKeep(lookaround.Body),
        LookbehindNode lookbehind => ContainsKeep(lookbehind.Body),
        OptionScopeNode optionScope => ContainsKeep(optionScope.Body),
        CaptureConditionalNode conditional =>
            ContainsKeep(conditional.WhenTrue) || ContainsKeep(conditional.WhenFalse),
        PatternConditionalNode conditional =>
            ContainsKeep(conditional.Condition) || ContainsKeep(conditional.WhenTrue) ||
            ContainsKeep(conditional.WhenFalse),
        ConditionalCalloutNode conditional =>
            ContainsKeep(conditional.WhenTrue) || ContainsKeep(conditional.WhenFalse),
        AbsentRangeNode absent => ContainsKeep(absent.Forbidden),
        AbsentExpressionNode absent =>
            ContainsKeep(absent.Forbidden) || ContainsKeep(absent.Expression),
        AbsentStopperNode absent => ContainsKeep(absent.Forbidden),
        _ => false,
    };

    private static bool ContainsAbsent(Node node) => node switch
    {
        AbsentRangeNode or AbsentExpressionNode or AbsentStopperNode or
            AbsentRangeClearNode => true,
        SequenceNode sequence => sequence.Nodes.Any(ContainsAbsent),
        AlternationNode alternation => alternation.Alternatives.Any(ContainsAbsent),
        GroupNode group => ContainsAbsent(group.Body),
        AtomicNode atomic => ContainsAbsent(atomic.Body),
        RepeatNode repeat => ContainsAbsent(repeat.Body),
        LookaroundNode lookaround => ContainsAbsent(lookaround.Body),
        LookbehindNode lookbehind => ContainsAbsent(lookbehind.Body),
        OptionScopeNode optionScope => ContainsAbsent(optionScope.Body),
        CaptureConditionalNode conditional =>
            ContainsAbsent(conditional.WhenTrue) || ContainsAbsent(conditional.WhenFalse),
        PatternConditionalNode conditional =>
            ContainsAbsent(conditional.Condition) || ContainsAbsent(conditional.WhenTrue) ||
            ContainsAbsent(conditional.WhenFalse),
        ConditionalCalloutNode conditional =>
            ContainsAbsent(conditional.WhenTrue) || ContainsAbsent(conditional.WhenFalse),
        _ => false,
    };

    private sealed class MatchContext
    {
        private readonly string _input;
        private readonly byte[] _inputBytes;
        private readonly int[] _byteToUtf16;
        private readonly int[] _utf16ToByte;
        private readonly CompiledPattern _compiled;
        private readonly TimeSpan _timeout;
        private readonly bool _findLongest;
        private readonly bool _ignoreCase;
        private readonly bool _dotMatchesNewline;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private readonly CalloutRuntime _runtime;
        private readonly OnigurumaCalloutAtomMatcher _atomMatcher;
        private readonly ulong _retryLimitInMatch;
        private long _work;
        private ulong _retryInMatchCounter;
        private int _retrySuppressionDepth;
        private int _subexpressionCallDepth;
        private int _absentDelimiterDepth;
        private int _searchStart;

        internal MatchContext(
            string input,
            CompiledPattern compiled,
            TimeSpan timeout,
            bool findLongest,
            bool ignoreCase,
            bool dotMatchesNewline,
            ulong retryLimitInMatch)
        {
            _input = input;
            _inputBytes = Encoding.UTF8.GetBytes(input);
            (_byteToUtf16, _utf16ToByte) = BuildPositionMaps(input, _inputBytes.Length);
            _compiled = compiled;
            _timeout = timeout;
            _findLongest = findLongest;
            _ignoreCase = ignoreCase;
            _dotMatchesNewline = dotMatchesNewline;
            _retryLimitInMatch = retryLimitInMatch;
            _runtime = new CalloutRuntime(compiled.CalloutsByTag);
            _atomMatcher = new OnigurumaCalloutAtomMatcher(input, GetRemainingTimeout);
        }

        internal List<OnigurumaCalloutEventMatch> Run(bool global, bool ignoreEmpty)
        {
            var results = new List<OnigurumaCalloutEventMatch>();
            var searchStart = 0;
            while (searchStart <= _inputBytes.Length)
            {
                _runtime.BeginSearch();
                var match = Search(searchStart, ignoreEmpty);
                if (match is null)
                {
                    break;
                }

                results.Add(match);
                if (!global)
                {
                    break;
                }

                if (match.ByteLength == 0)
                {
                    if (match.ByteIndex == _inputBytes.Length)
                    {
                        break;
                    }

                    // jq/src/builtin.c advances exactly one raw byte after every
                    // zero-width global match, even into a UTF-8 continuation byte.
                    searchStart = match.ByteIndex + 1;
                }
                else
                {
                    searchStart = match.ByteIndex + match.ByteLength;
                }
            }

            return results;
        }

        private OnigurumaCalloutEventMatch? Search(int searchStart, bool ignoreEmpty)
        {
            _searchStart = searchStart;
            OnigurumaCalloutEventMatch? longest = null;
            var longestScore = -1;
            foreach (var candidate in EnumerateCandidateStarts(searchStart))
            {
                Tick();
                if (candidate < _runtime.SkipFloor)
                {
                    continue;
                }

                _runtime.BeginCandidate();
                _retryInMatchCounter = 0;
                OnigurumaCalloutEventMatch? candidateBest = null;
                var candidateBestScore = -1;
                try
                {
                    var initial = new Frame(
                        candidate,
                        new Dictionary<int, CaptureSpan>(),
                        new Dictionary<(int GroupNumber, int CallLevel), CaptureSpan>(),
                        null,
                        new RuntimeRegexOptions(
                            _ignoreCase,
                            _dotMatchesNewline,
                            false,
                            false),
                        null);
                    foreach (var result in _compiled.Root.Match(this, initial, 0))
                    {
                        var found = Materialize(candidate, result);
                        if (ignoreEmpty && result.Position == candidate)
                        {
                            RetryAfterFailure();
                            continue;
                        }

                        if (!_findLongest)
                        {
                            return found;
                        }

                        // regexec.c's END opcode resumes through `fail:` while
                        // ONIG_OPTION_FIND_LONGEST is collecting alternatives.
                        RetryAfterFailure();

                        var score = Score(candidate, result);
                        if (candidateBest is null || score > candidateBestScore)
                        {
                            candidateBest = found;
                            candidateBestScore = score;
                        }
                    }
                }
                catch (CandidateMismatchException)
                {
                    // FIND_LONGEST keeps a success recorded before a later MISMATCH
                    // callout aborts exploration of the remaining paths at this candidate.
                }

                if (_findLongest && candidateBest is not null &&
                    (longest is null || candidateBestScore > longestScore ||
                     candidateBestScore == longestScore && candidateBest.Index < longest.Index))
                {
                    longest = candidateBest;
                    longestScore = candidateBestScore;
                }

            }

            return longest;
        }

        private static int Score(int candidate, Frame result) => result.Position - candidate;

        private OnigurumaCalloutEventMatch Materialize(int start, Frame result)
        {
            var reportStart = Math.Min(result.ReportStart ?? start, result.Position);
            var wholeLength = result.Position - reportStart;
            var jqWholeSpan = MaterializeJqSpan(reportStart, result.Position);
            var legacyWholeSpan = MaterializeUtf16Span(
                reportStart,
                result.Position,
                jqWholeSpan);
            var captures = _compiled.Groups
                .OrderBy(group => group.Number)
                .Select(group => result.Captures.TryGetValue(group.Number, out var capture)
                    ? MaterializeCapture(
                        capture,
                        group.Name,
                        group.Number,
                        wholeLength == 0,
                        legacyWholeSpan.Index,
                        jqWholeSpan.Index)
                    : new OnigurumaCalloutEventCapture(
                        -1,
                        0,
                        null,
                        group.Name,
                        group.Number,
                        -1,
                        0,
                        -1,
                        0))
                .ToArray();
            return new OnigurumaCalloutEventMatch(
                legacyWholeSpan.Index,
                wholeLength == 0 ? 0 : legacyWholeSpan.Length,
                wholeLength == 0 ? string.Empty : DecodeBytes(reportStart, wholeLength),
                captures,
                reportStart,
                wholeLength,
                jqWholeSpan.Index,
                wholeLength == 0 ? 0 : jqWholeSpan.Length);
        }

        private OnigurumaCalloutEventCapture MaterializeCapture(
            CaptureSpan capture,
            string? name,
            int groupNumber,
            bool zeroWidthWholeMatch,
            int zeroWidthWholeLegacyOffset,
            int zeroWidthWholeJqOffset)
        {
            if (zeroWidthWholeMatch)
            {
                // jq/src/builtin.c deliberately reports every participating capture
                // as empty when the whole match is empty, including lookaround captures.
                return new OnigurumaCalloutEventCapture(
                    zeroWidthWholeLegacyOffset,
                    0,
                    string.Empty,
                    name,
                    groupNumber,
                    capture.Start,
                    0,
                    zeroWidthWholeJqOffset,
                    0);
            }

            var jqSpan = MaterializeJqSpan(capture.Start, capture.End);
            var legacySpan = MaterializeUtf16Span(capture.Start, capture.End, jqSpan);
            return new OnigurumaCalloutEventCapture(
                legacySpan.Index,
                legacySpan.Length,
                DecodeBytes(capture.Start, capture.End - capture.Start),
                name,
                groupNumber,
                capture.Start,
                capture.End - capture.Start,
                jqSpan.Index,
                jqSpan.Length);
        }

        private (int Index, int Length) MaterializeJqSpan(int begin, int end)
        {
            if (begin == end)
            {
                var zeroWidthIndex = 0;
                for (var cursor = 0; cursor < begin; zeroWidthIndex++)
                {
                    cursor += JqUtf8DecodeLength(_inputBytes[cursor]);
                }

                return (zeroWidthIndex, 0);
            }

            var index = 0;
            var length = 0;
            for (var cursor = 0; cursor < end; length++)
            {
                if (cursor == begin)
                {
                    index = length;
                    length = 0;
                }

                cursor += JqUtf8DecodeLength(_inputBytes[cursor]);
            }

            return (index, length);
        }

        private (int Index, int Length) MaterializeUtf16Span(
            int begin,
            int end,
            (int Index, int Length) fallback)
        {
            if (IsScalarBoundary(begin) && IsScalarBoundary(end))
            {
                var utf16Begin = _byteToUtf16[begin];
                return (utf16Begin, _byteToUtf16[end] - utf16Begin);
            }

            // Historical runner-facing fields predate raw-byte candidate starts.
            // Continuation-byte matches have no UTF-16 span, so retain the jq
            // materialization as their deterministic diagnostic representation.
            return fallback;
        }

        private string DecodeBytes(int start, int length) =>
            Encoding.UTF8.GetString(_inputBytes, start, length);

        private IEnumerable<int> EnumerateCandidateStarts(int searchStart)
        {
            var boundary = searchStart;
            while (boundary <= _inputBytes.Length && !IsScalarBoundary(boundary))
            {
                yield return boundary++;
            }

            if (boundary > _inputBytes.Length)
            {
                yield break;
            }

            var utf16Start = _byteToUtf16[boundary];
            foreach (var candidate in _compiled.OptimizationPlan.EnumerateCandidateStarts(
                _input,
                utf16Start))
            {
                yield return _utf16ToByte[candidate];
            }
        }

        private static (int[] ByteToUtf16, int[] Utf16ToByte) BuildPositionMaps(
            string input,
            int byteLength)
        {
            var byteToUtf16 = Enumerable.Repeat(-1, byteLength + 1).ToArray();
            var utf16ToByte = Enumerable.Repeat(-1, input.Length + 1).ToArray();
            var byteIndex = 0;
            for (var utf16Index = 0; utf16Index < input.Length;)
            {
                byteToUtf16[byteIndex] = utf16Index;
                utf16ToByte[utf16Index] = byteIndex;
                var rune = Rune.GetRuneAt(input, utf16Index);
                byteIndex += rune.Utf8SequenceLength;
                utf16Index += rune.Utf16SequenceLength;
            }

            byteToUtf16[byteLength] = input.Length;
            utf16ToByte[input.Length] = byteLength;
            return (byteToUtf16, utf16ToByte);
        }

        private static int JqUtf8DecodeLength(byte value) => value switch
        {
            < 0x80 => 1,
            < 0xE0 when (value & 0xE0) == 0xC0 => 2,
            < 0xF0 when (value & 0xF0) == 0xE0 => 3,
            _ => 4,
        };

        internal string Input => _input;

        internal int ByteLength => _inputBytes.Length;

        internal CompiledPattern Pattern => _compiled;

        internal CalloutRuntime Runtime => _runtime;

        internal OnigurumaCalloutAtomMatcher AtomMatcher => _atomMatcher;

        internal bool IgnoreCase => _ignoreCase;

        internal bool DotMatchesNewline => _dotMatchesNewline;

        internal int SearchStart => _searchStart;

        internal bool IsMatchingAbsentDelimiter => _absentDelimiterDepth != 0;

        internal int RightRange(Frame frame) => Math.Min(
            frame.RightRange ?? _inputBytes.Length,
            _inputBytes.Length);

        internal bool IsScalarBoundary(int byteIndex) =>
            byteIndex >= 0 && byteIndex < _byteToUtf16.Length &&
            _byteToUtf16[byteIndex] >= 0;

        internal bool TryGetUtf16Index(int byteIndex, out int utf16Index)
        {
            if (IsScalarBoundary(byteIndex))
            {
                utf16Index = _byteToUtf16[byteIndex];
                return true;
            }

            utf16Index = -1;
            return false;
        }

        internal int GetByteIndex(int utf16Index) => _utf16ToByte[utf16Index];

        internal int EncodedCharacterWidth(int byteIndex)
        {
            if (byteIndex >= _inputBytes.Length)
            {
                return 0;
            }

            if (!IsScalarBoundary(byteIndex))
            {
                return 1;
            }

            var utf16Index = _byteToUtf16[byteIndex];
            return Rune.GetRuneAt(_input, utf16Index).Utf8SequenceLength;
        }

        internal bool TryStepBackCharacters(int byteIndex, int count, out int start)
        {
            start = byteIndex;
            for (var step = 0; step < count; step++)
            {
                if (start == 0)
                {
                    return false;
                }

                start--;
                while (start > 0 && !IsScalarBoundary(start))
                {
                    start--;
                }
            }

            return true;
        }

        internal int RetreatEncodedCharacter(int byteIndex)
        {
            if (byteIndex <= 0)
            {
                return 0;
            }

            var previous = byteIndex - 1;
            while (previous > 0 && !IsScalarBoundary(previous))
            {
                previous--;
            }

            return previous;
        }

        internal byte ByteAt(int byteIndex) => _inputBytes[byteIndex];

        internal bool ExactBytesEqual(int inputStart, string literal)
        {
            var literalBytes = Encoding.UTF8.GetBytes(literal);
            return inputStart + literalBytes.Length <= _inputBytes.Length &&
                _inputBytes.AsSpan(inputStart, literalBytes.Length).SequenceEqual(literalBytes);
        }

        internal bool ExactBytesEqual(int inputStart, ReadOnlySpan<byte> literal) =>
            inputStart + literal.Length <= _inputBytes.Length &&
            _inputBytes.AsSpan(inputStart, literal.Length).SequenceEqual(literal);

        internal IEnumerable<int> EnumerateLiteralByteMatches(
            int byteIndex,
            string literal,
            bool allowFullFolds)
        {
            if (!TryGetUtf16Index(byteIndex, out var utf16Index))
            {
                return [];
            }

            return OnigurumaCaseFold.EnumerateLiteralMatches(
                    _input,
                    utf16Index,
                    literal,
                    allowFullFolds)
                .Select(consumedUtf16 =>
                    _utf16ToByte[utf16Index + consumedUtf16] - byteIndex);
        }

        internal IEnumerable<int> EnumerateClassByteMatches(
            int byteIndex,
            Func<int, bool> scalarMember,
            bool negated,
            bool allowFullFolds)
        {
            if (!TryGetUtf16Index(byteIndex, out var utf16Index) ||
                utf16Index == _input.Length)
            {
                return [];
            }

            return OnigurumaCaseFold.EnumerateClassMatches(
                    _input,
                    utf16Index,
                    scalarMember,
                    negated,
                    allowFullFolds)
                .Select(consumedUtf16 =>
                    _utf16ToByte[utf16Index + consumedUtf16] - byteIndex);
        }

        internal bool TryMatchAtom(
            OnigurumaCalloutAtomToken token,
            int byteIndex,
            out int consumedBytes)
        {
            consumedBytes = 0;
            if (!TryGetUtf16Index(byteIndex, out var utf16Index))
            {
                if (byteIndex >= _inputBytes.Length)
                {
                    return false;
                }

                switch (token.Kind)
                {
                    case OnigurumaCalloutAtomKind.AnyScalar:
                    case OnigurumaCalloutAtomKind.NotNewline:
                    case OnigurumaCalloutAtomKind.TextCluster:
                        consumedBytes = 1;
                        return true;
                    case OnigurumaCalloutAtomKind.Property when token.RuntimeWord:
                    {
                        var isWord = OnigurumaCalloutAtomMatcher.IsWordScalar(
                            _inputBytes[byteIndex]);
                        if (isWord != token.Negated)
                        {
                            consumedBytes = 1;
                            return true;
                        }

                        return false;
                    }
                    case OnigurumaCalloutAtomKind.Property when token.Negated:
                        consumedBytes = 1;
                        return true;
                    case OnigurumaCalloutAtomKind.WordBoundary:
                        return IsContinuationWordBoundary(byteIndex);
                    case OnigurumaCalloutAtomKind.NonWordBoundary:
                        return !IsContinuationWordBoundary(byteIndex);
                    case OnigurumaCalloutAtomKind.TextBoundary:
                        return true;
                    case OnigurumaCalloutAtomKind.TextNonBoundary:
                        return false;
                    default:
                        return false;
                }
            }

            if (!_atomMatcher.TryMatch(token, utf16Index, out var consumedUtf16))
            {
                return false;
            }

            consumedBytes = _utf16ToByte[utf16Index + consumedUtf16] - byteIndex;
            return true;
        }

        private bool IsContinuationWordBoundary(int byteIndex)
        {
            var currentIsWord = OnigurumaCalloutAtomMatcher.IsWordScalar(
                _inputBytes[byteIndex]);
            var previousHead = byteIndex - 1;
            while (previousHead > 0 && !IsScalarBoundary(previousHead))
            {
                previousHead--;
            }

            var previousRune = Rune.GetRuneAt(_input, _byteToUtf16[previousHead]);
            var previousIsWord = OnigurumaCalloutAtomMatcher.IsWordScalar(previousRune.Value);
            return previousIsWord != currentIsWord;
        }

        internal string DecodeValidSpan(int start, int end)
        {
            if (!TryGetUtf16Index(start, out var utf16Start) ||
                !TryGetUtf16Index(end, out var utf16End))
            {
                throw new InvalidOperationException("The byte span is not UTF-8 scalar aligned.");
            }

            return _input[utf16Start..utf16End];
        }

        internal bool ByteSpanEquals(int leftStart, int rightStart, int length) =>
            leftStart >= 0 && rightStart >= 0 && length >= 0 &&
            leftStart + length <= _inputBytes.Length &&
            rightStart + length <= _inputBytes.Length &&
            _inputBytes.AsSpan(leftStart, length).SequenceEqual(
                _inputBytes.AsSpan(rightStart, length));

        internal bool TryMatchFoldedBackreference(
            int captureStart,
            int captureEnd,
            int inputStart,
            out int consumedBytes)
        {
            consumedBytes = 0;
            if (TryGetUtf16Index(captureStart, out var captureUtf16Start) &&
                TryGetUtf16Index(captureEnd, out var captureUtf16End) &&
                TryGetUtf16Index(inputStart, out var inputUtf16Start) &&
                OnigurumaCaseFold.TryMatchBackreference(
                    _input[captureUtf16Start..captureUtf16End],
                    _input,
                    inputUtf16Start,
                    out var consumedUtf16))
            {
                consumedBytes = _utf16ToByte[inputUtf16Start + consumedUtf16] - inputStart;
                return true;
            }

            var captureLength = captureEnd - captureStart;
            if (ByteSpanEquals(captureStart, inputStart, captureLength))
            {
                consumedBytes = captureLength;
                return true;
            }

            return false;
        }

        internal int SubexpressionCallDepth => _subexpressionCallDepth;

        internal void Tick()
        {
            _work++;
            if ((_work & 0x3ff) == 0)
            {
                _ = GetRemainingTimeout();
            }
        }

        internal void RetryAfterFailure()
        {
            if (_retrySuppressionDepth != 0)
            {
                return;
            }

            // regexec.c:1395-1400,4462-4472 increments after STACK_POP and
            // raises when the increment reaches (not exceeds) the configured
            // per-match limit. A zero limit remains the native unlimited form.
            _retryInMatchCounter++;
            if (_retryLimitInMatch != 0 && _retryInMatchCounter >= _retryLimitInMatch)
            {
                throw new JqRuntimeException(
                    "Regex failure: " + OnigurumaCalloutErrorData.Format(-17));
            }
        }

        internal bool TryEnterSubexpressionCall()
        {
            // DEFAULT_SUBEXP_CALL_MAX_NEST_LEVEL is 20. Reaching it is an
            // ordinary path failure in regexec.c; the total calls-per-search
            // limit is zero (unlimited), so it must not be conflated with AST
            // traversal depth or the separate parser-depth limit.
            if (_subexpressionCallDepth == MaximumSubexpressionCallNesting)
            {
                return false;
            }

            _subexpressionCallDepth++;
            return true;
        }

        internal void ExitSubexpressionCall() => _subexpressionCallDepth--;

        internal IDisposable SuppressOptimizedRepeatProbeRetry()
        {
            _retrySuppressionDepth++;
            return new RetrySuppression(this);
        }

        internal bool IsCallableGroup(GroupNode group) =>
            _compiled.CallableGroups.Contains(group);

        internal IDisposable EnterAbsentDelimiter()
        {
            _absentDelimiterDepth++;
            return new AbsentDelimiterScope(this);
        }

        private sealed class AbsentDelimiterScope(MatchContext owner) : IDisposable
        {
            private MatchContext? _owner = owner;

            public void Dispose()
            {
                if (_owner is not { } current)
                {
                    return;
                }

                _owner = null;
                current._absentDelimiterDepth--;
            }
        }

        private sealed class RetrySuppression(MatchContext owner) : IDisposable
        {
            private MatchContext? _owner = owner;

            public void Dispose()
            {
                if (_owner is not { } current)
                {
                    return;
                }

                _owner = null;
                current._retrySuppressionDepth--;
            }
        }

        private TimeSpan GetRemainingTimeout()
        {
            if (_timeout == System.Threading.Timeout.InfiniteTimeSpan)
            {
                return _timeout;
            }

            var remaining = _timeout - _stopwatch.Elapsed;
            return remaining > TimeSpan.Zero
                ? remaining
                : throw new JqRuntimeException(
                    "Regex failure: regular expression evaluation timed out");
        }

    }

    private sealed class CalloutRuntime
    {
        private readonly IReadOnlyDictionary<string, CalloutNode> _byTag;
        private readonly Dictionary<int, CalloutSlot> _slots = [];
        private int _candidateEpoch;

        internal CalloutRuntime(IReadOnlyDictionary<string, CalloutNode> byTag) => _byTag = byTag;

        internal int SkipFloor { get; private set; }

        internal void BeginSearch()
        {
            _slots.Clear();
            _candidateEpoch = 0;
            SkipFloor = 0;
        }

        internal void BeginCandidate()
        {
            _candidateEpoch++;
            SkipFloor = 0;
        }

        internal CalloutEntry Enter(CalloutNode callout, int position)
        {
            switch (callout.Kind)
            {
                case CalloutKind.Content:
                    // jq calls plain onig_search without installing progress/retraction
                    // callbacks. OP_CALLOUT_CONTENTS therefore advances without an event.
                    return CalloutEntry.SuccessWithoutRetraction;
                case CalloutKind.Count:
                case CalloutKind.TotalCount:
                    return EnterCount(callout);
                case CalloutKind.Max:
                    return EnterMax(callout);
                case CalloutKind.Compare:
                    return EnterCompare(callout);
                case CalloutKind.Skip:
                    SkipFloor = Math.Max(SkipFloor, position);
                    return CalloutEntry.SuccessWithoutRetraction;
                case CalloutKind.Fail:
                    return CalloutEntry.Failure;
                case CalloutKind.Mismatch:
                    throw new CandidateMismatchException();
                case CalloutKind.Error:
                {
                    var code = callout.Arguments.Length == 0
                        ? -3L
                        : ParseCalloutLong(callout.Arguments[0]);
                    var narrowed = unchecked((int)code);
                    if (narrowed == -1)
                    {
                        throw new CandidateMismatchException();
                    }

                    throw CalloutError(narrowed);
                }
                default:
                    throw new UnreachableException();
            }
        }

        private CalloutEntry EnterCount(CalloutNode callout)
        {
            var direction = callout.Arguments.Length == 0 ? ">" : callout.Arguments[0];
            if (direction is not (">" or "X" or "<"))
            {
                throw new JqRuntimeException("Regex failure: invalid callout arg");
            }

            var total = callout.Kind == CalloutKind.TotalCount;
            var slot = GetSlot(callout, total);
            // TOTAL_COUNT retains its physical value across match-at candidates, but a
            // tagged read may observe it only when this producer ran in the current
            // candidate generation. Entering the producer stamps the retained value.
            slot.Epoch = _candidateEpoch;
            if (direction != "<")
            {
                slot.Value = unchecked(slot.Value + 1);
            }

            return new CalloutEntry(true, () =>
            {
                if (direction == "<")
                {
                    slot.Value = unchecked(slot.Value + 1);
                }
                else if (direction == "X")
                {
                    slot.Value = unchecked(slot.Value - 1);
                }
            });
        }

        private CalloutEntry EnterMax(CalloutNode callout)
        {
            var direction = callout.Arguments.Length == 1 ? "X" : callout.Arguments[1];
            if (direction is not (">" or "X" or "<"))
            {
                throw new JqRuntimeException("Regex failure: invalid callout arg");
            }

            var slot = GetSlot(callout, total: false);
            var limit = Resolve(callout.Arguments[0]);
            if (direction != "<")
            {
                if (slot.Value >= limit)
                {
                    return CalloutEntry.Failure;
                }

                slot.Value = unchecked(slot.Value + 1);
            }

            return new CalloutEntry(true, () =>
            {
                if (direction == "<")
                {
                    // Oniguruma ignores FAIL/SUCCESS returned by a retraction callout. MAX{<}
                    // therefore saturates only by declining the increment once the limit is met.
                    if (slot.Value < Resolve(callout.Arguments[0]))
                    {
                        slot.Value = unchecked(slot.Value + 1);
                    }
                }
                else if (direction == "X")
                {
                    slot.Value = unchecked(slot.Value - 1);
                }
            });
        }

        private CalloutEntry EnterCompare(CalloutNode callout)
        {
            var operation = callout.Arguments[1];
            var operationValue = operation switch
            {
                "==" => 0,
                "!=" => 1,
                "<" => 2,
                ">" => 3,
                "<=" => 4,
                ">=" => 5,
                _ => throw new JqRuntimeException("Regex failure: invalid callout arg"),
            };

            // CMP is progress-only. A tag attached to CMP exposes the cached operation enum
            // and survives ordinary path abandonment until the next candidate/search reset.
            if (callout.Tag is not null)
            {
                GetSlot(callout, total: false).Value = operationValue;
            }

            var left = Resolve(callout.Arguments[0]);
            var right = Resolve(callout.Arguments[2]);
            var success = operation switch
            {
                "==" => left == right,
                "!=" => left != right,
                "<" => left < right,
                ">" => left > right,
                "<=" => left <= right,
                ">=" => left >= right,
                _ => false,
            };
            return success ? CalloutEntry.SuccessWithoutRetraction : CalloutEntry.Failure;
        }

        private long Resolve(string operand)
        {
            if (OnigurumaCalloutArgumentParser.TryParseLong(operand, out var literal))
            {
                return literal;
            }

            var producer = _byTag[operand];
            if (!_slots.TryGetValue(producer.Id, out var slot))
            {
                return 0;
            }

            return slot.Epoch == _candidateEpoch ? slot.Value : 0;
        }

        private CalloutSlot GetSlot(CalloutNode callout, bool total)
        {
            if (!_slots.TryGetValue(callout.Id, out var slot))
            {
                slot = new CalloutSlot(0, _candidateEpoch);
                _slots.Add(callout.Id, slot);
            }
            else if (!total && slot.Epoch != _candidateEpoch)
            {
                slot.Value = 0;
                slot.Epoch = _candidateEpoch;
            }

            return slot;
        }

        private static long ParseCalloutLong(string value) =>
            OnigurumaCalloutArgumentParser.TryParseLong(value, out var parsed)
                ? parsed
                : throw new JqRuntimeException("Regex failure: invalid callout arg");

        private static JqRuntimeException CalloutError(int code)
        {
            if (code >= 0 || OnigurumaCalloutErrorData.NeedsParameter(code))
            {
                code = -230;
            }

            return new JqRuntimeException("Regex failure: " + OnigurumaCalloutErrorData.Format(code));
        }
    }

    private abstract class Node
    {
        internal abstract IEnumerable<Frame> Match(MatchContext context, Frame frame, int depth);

        protected static void GuardDepth(MatchContext context, int depth)
        {
            // The former 4,096 evaluator-depth check incorrectly applied
            // Oniguruma's parser-depth constant to every AST edge, including
            // ordinary atoms in a flat sequence. Runtime recursion is governed
            // separately by subexpression-call nesting; this guard only keeps
            // the configured deadline responsive.
            context.Tick();
        }
    }

    private sealed class EmptyNode : Node
    {
        internal static readonly EmptyNode Instance = new();

        internal override IEnumerable<Frame> Match(MatchContext context, Frame frame, int depth)
        {
            GuardDepth(context, depth);
            yield return frame;
        }

    }

    // Comments are erased by regparse.c before expression construction. Keep a
    // marker only until a following repeat token has had the opportunity to emit
    // ONIGERR_TARGET_OF_REPEAT_OPERATOR_NOT_SPECIFIED.
    private sealed class CommentNode : Node
    {
        internal override IEnumerable<Frame> Match(MatchContext context, Frame frame, int depth)
        {
            GuardDepth(context, depth);
            yield return frame;
        }
    }

    private sealed class SequenceNode(IReadOnlyList<Node> nodes) : Node
    {
        internal IReadOnlyList<Node> Nodes => nodes;

        internal override IEnumerable<Frame> Match(MatchContext context, Frame frame, int depth)
        {
            GuardDepth(context, depth);
            return MatchIteratively(context, frame, depth);
        }

        private IEnumerable<Frame> MatchIteratively(
            MatchContext context,
            Frame frame,
            int depth)
        {
            if (nodes.Count == 0)
            {
                yield return frame;
                yield break;
            }

            var iterators = new Stack<IEnumerator<Frame>>(nodes.Count);
            try
            {
                iterators.Push(nodes[0].Match(context, frame, depth + 1).GetEnumerator());
                while (iterators.Count != 0)
                {
                    var current = iterators.Peek();
                    if (!current.MoveNext())
                    {
                        current.Dispose();
                        iterators.Pop();
                        continue;
                    }

                    var next = current.Current;
                    if (iterators.Count == nodes.Count)
                    {
                        yield return next;
                        continue;
                    }

                    iterators.Push(nodes[iterators.Count]
                        .Match(context, next, depth + 1)
                        .GetEnumerator());
                }
            }
            finally
            {
                while (iterators.TryPop(out var iterator))
                {
                    iterator.Dispose();
                }
            }
        }
    }

    private sealed class AlternationNode(IReadOnlyList<Node> alternatives) : Node
    {
        internal IReadOnlyList<Node> Alternatives => alternatives;

        internal override IEnumerable<Frame> Match(MatchContext context, Frame frame, int depth)
        {
            GuardDepth(context, depth);
            foreach (var alternative in alternatives)
            {
                foreach (var result in alternative.Match(context, frame, depth + 1))
                {
                    yield return result;
                }
            }
        }
    }

    private sealed class LiteralNode : Node
    {
        private readonly string _value;

        internal LiteralNode(char value) => _value = value.ToString();

        internal LiteralNode(string value) => _value = value;

        internal string Value => _value;

        internal override IEnumerable<Frame> Match(MatchContext context, Frame frame, int depth)
        {
            GuardDepth(context, depth);
            if (frame.Options.IgnoreCase)
            {
                var matchedAny = false;
                foreach (var consumed in context.EnumerateLiteralByteMatches(
                    frame.Position,
                    _value,
                    allowFullFolds: !frame.Options.InLookbehind).Where(consumed =>
                        frame.Position + consumed <= context.RightRange(frame)))
                {
                    matchedAny = true;
                    yield return frame with { Position = frame.Position + consumed };
                }

                if (!matchedAny)
                {
                    context.RetryAfterFailure();
                }

                yield break;
            }

            var byteLength = Encoding.UTF8.GetByteCount(_value);
            if (frame.Position + byteLength <= context.RightRange(frame) &&
                context.ExactBytesEqual(frame.Position, _value))
            {
                yield return frame with { Position = frame.Position + byteLength };
            }
            else
            {
                context.RetryAfterFailure();
            }
        }
    }

    private sealed class CrudeByteLiteralNode(byte[] bytes, int decodedScalar) : Node
    {
        internal ReadOnlySpan<byte> Bytes => bytes;

        internal int DecodedScalar => decodedScalar;

        internal string? CanonicalText =>
            Rune.IsValid(decodedScalar) &&
            Encoding.UTF8.GetBytes(char.ConvertFromUtf32(decodedScalar)).AsSpan().SequenceEqual(bytes)
                ? char.ConvertFromUtf32(decodedScalar)
                : null;

        internal override IEnumerable<Frame> Match(MatchContext context, Frame frame, int depth)
        {
            GuardDepth(context, depth);
            if (frame.Options.IgnoreCase && Rune.IsValid(decodedScalar) &&
                OnigurumaCaseFold.HasCaseFoldMapping(decodedScalar))
            {
                var matched = false;
                foreach (var consumed in context.EnumerateLiteralByteMatches(
                    frame.Position,
                    char.ConvertFromUtf32(decodedScalar),
                    allowFullFolds: !frame.Options.InLookbehind).Where(consumed =>
                        frame.Position + consumed <= context.RightRange(frame)))
                {
                    matched = true;
                    yield return frame with { Position = frame.Position + consumed };
                }

                if (!matched)
                {
                    context.RetryAfterFailure();
                }

                yield break;
            }

            if (frame.Position + bytes.Length <= context.RightRange(frame) &&
                context.ExactBytesEqual(frame.Position, bytes))
            {
                yield return frame with { Position = frame.Position + bytes.Length };
            }
            else
            {
                context.RetryAfterFailure();
            }
        }
    }

    private sealed class AnyNode : Node
    {
        internal override IEnumerable<Frame> Match(MatchContext context, Frame frame, int depth)
        {
            GuardDepth(context, depth);
            if (frame.Position < context.RightRange(frame) &&
                (frame.Options.DotMatchesNewline || context.ByteAt(frame.Position) != (byte)'\n'))
            {
                yield return frame with
                {
                    Position = frame.Position + context.EncodedCharacterWidth(frame.Position),
                };
            }
            else
            {
                context.RetryAfterFailure();
            }
        }
    }

    private sealed class ImpossibleNode : Node
    {
        internal override IEnumerable<Frame> Match(MatchContext context, Frame frame, int depth)
        {
            GuardDepth(context, depth);
            context.RetryAfterFailure();
            yield break;
        }
    }

    private sealed class OrdinaryAtomNode(OnigurumaCalloutAtomToken token) : Node
    {
        internal OnigurumaCalloutAtomToken Token => token;

        internal override IEnumerable<Frame> Match(MatchContext context, Frame frame, int depth)
        {
            GuardDepth(context, depth);
            if (token.Kind == OnigurumaCalloutAtomKind.Literal && frame.Options.IgnoreCase)
            {
                var matched = false;
                foreach (var consumed in context.EnumerateLiteralByteMatches(
                    frame.Position,
                    token.Literal!,
                    allowFullFolds: !frame.Options.InLookbehind).Where(consumed =>
                        frame.Position + consumed <= context.RightRange(frame)))
                {
                    matched = true;
                    yield return frame with { Position = frame.Position + consumed };
                }

                if (!matched)
                {
                    context.RetryAfterFailure();
                }

                yield break;
            }

            if (context.TryMatchAtom(token, frame.Position, out var consumedBytes) &&
                frame.Position + consumedBytes <= context.RightRange(frame))
            {
                yield return frame with { Position = frame.Position + consumedBytes };
            }
            else
            {
                context.RetryAfterFailure();
            }
        }
    }

    private sealed class CharacterPredicateNode(CharacterPredicate predicate) : Node
    {
        internal override IEnumerable<Frame> Match(MatchContext context, Frame frame, int depth)
        {
            GuardDepth(context, depth);
            if (frame.Position < context.RightRange(frame) &&
                context.TryGetUtf16Index(frame.Position, out var utf16Index) &&
                predicate.Matches(context.Input, utf16Index, frame.Options.IgnoreCase))
            {
                yield return frame with
                {
                    Position = frame.Position + context.EncodedCharacterWidth(frame.Position),
                };
            }
            else
            {
                context.RetryAfterFailure();
            }
        }
    }

    private sealed class CharacterClassNode(
        IReadOnlyList<OnigurumaCalloutAtomToken> members,
        bool negated,
        IReadOnlySet<byte>? rawSingleBytes = null,
        bool hasMultiByteComponent = false) : Node
    {
        internal IReadOnlyList<OnigurumaCalloutAtomToken> Members => members;

        internal bool Negated => negated;

        internal IReadOnlySet<byte> RawSingleBytes => rawSingleBytes ?? EmptyRawByteSet;

        private static readonly IReadOnlySet<byte> EmptyRawByteSet = new HashSet<byte>();

        private readonly bool _hasIgnoreCaseMultiByteComponent =
            hasMultiByteComponent || members.Count != 0 &&
            OnigurumaCaseFold.SimpleFoldClosureIntroducesMultiByte(
                scalar => MemberMatchesScalar(members, scalar));

        internal override IEnumerable<Frame> Match(MatchContext context, Frame frame, int depth)
        {
            GuardDepth(context, depth);
            if (frame.Position >= context.RightRange(frame))
            {
                context.RetryAfterFailure();
                yield break;
            }

            if (!context.IsScalarBoundary(frame.Position))
            {
                var rawMatched = RawSingleBytes.Contains(context.ByteAt(frame.Position));
                if (rawMatched != negated)
                {
                    yield return frame with { Position = frame.Position + 1 };
                }
                else
                {
                    context.RetryAfterFailure();
                }

                yield break;
            }

            var hasEffectiveMultiByteComponent = frame.Options.IgnoreCase
                ? _hasIgnoreCaseMultiByteComponent
                : hasMultiByteComponent;
            if (negated && !hasEffectiveMultiByteComponent &&
                RawSingleBytes.Contains(context.ByteAt(frame.Position)))
            {
                // OP_CCLASS_NOT tests a pure negated byte bitset against the
                // UTF-8 lead byte. OP_CCLASS_MIX_NOT ignores that bitset at a
                // scalar head and consults only its multibyte ranges.
                context.RetryAfterFailure();
                yield break;
            }

            if (frame.Options.IgnoreCase)
            {
                var matchedAny = false;
                foreach (var consumed in context.EnumerateClassByteMatches(
                    frame.Position,
                    scalar => MemberMatchesScalar(members, scalar),
                    negated,
                    allowFullFolds: !frame.Options.InLookbehind).Where(consumed =>
                        frame.Position + consumed <= context.RightRange(frame)))
                {
                    matchedAny = true;
                    yield return frame with { Position = frame.Position + consumed };
                }

                if (!matchedAny)
                {
                    context.RetryAfterFailure();
                }

                yield break;
            }

            var matched = context.IsScalarBoundary(frame.Position) &&
                members.Any(member => context.TryMatchAtom(member, frame.Position, out _));
            if (matched != negated)
            {
                yield return frame with
                {
                    Position = frame.Position + context.EncodedCharacterWidth(frame.Position),
                };
            }
            else
            {
                context.RetryAfterFailure();
            }
        }

        private static bool MemberMatchesScalar(
            IReadOnlyList<OnigurumaCalloutAtomToken> classMembers,
            int scalar)
        {
            var scalarText = char.ConvertFromUtf32(scalar);
            var matcher = new OnigurumaCalloutAtomMatcher(scalarText);
            return classMembers.Any(member => matcher.TryMatch(member, 0, out _));
        }
    }

    private sealed class AnchorNode(AnchorKind kind) : Node
    {
        internal AnchorKind Kind => kind;

        internal override IEnumerable<Frame> Match(MatchContext context, Frame frame, int depth)
        {
            GuardDepth(context, depth);
            var success = kind switch
            {
                AnchorKind.Start => frame.Position == 0,
                AnchorKind.SearchStart => frame.Position == context.SearchStart,
                AnchorKind.End => frame.Position == context.ByteLength,
                AnchorKind.EndBeforeFinalNewline => frame.Position == context.ByteLength ||
                    frame.Position == context.ByteLength - 1 &&
                    context.ByteAt(frame.Position) == (byte)'\n',
                AnchorKind.LineStart => frame.Position == 0 ||
                    frame.Options.MultilineAnchors && frame.Position > 0 &&
                    context.ByteAt(frame.Position - 1) == (byte)'\n',
                AnchorKind.LineEnd => frame.Position == context.ByteLength ||
                    frame.Options.MultilineAnchors &&
                    context.ByteAt(frame.Position) == (byte)'\n' ||
                    !frame.Options.MultilineAnchors &&
                    frame.Position == context.ByteLength - 1 &&
                    context.ByteAt(frame.Position) == (byte)'\n',
                _ => false,
            };
            if (success)
            {
                yield return frame;
            }
            else
            {
                context.RetryAfterFailure();
            }
        }
    }

    private sealed class ResetMatchStartNode : Node
    {
        internal override IEnumerable<Frame> Match(MatchContext context, Frame frame, int depth)
        {
            GuardDepth(context, depth);
            yield return frame with { ReportStart = frame.Position };
        }
    }

    private sealed class OptionScopeNode(
        Node body,
        RuntimeRegexOption enabled,
        RuntimeRegexOption disabled,
        bool transparentRepeatTarget = false) : Node
    {
        internal Node Body => body;

        internal RuntimeRegexOption Enabled => enabled;

        internal RuntimeRegexOption Disabled => disabled;

        internal bool TransparentRepeatTarget => transparentRepeatTarget;

        internal override IEnumerable<Frame> Match(MatchContext context, Frame frame, int depth)
        {
            GuardDepth(context, depth);
            var outerOptions = frame.Options;
            var scoped = outerOptions.Apply(enabled, disabled);
            foreach (var result in body.Match(
                context,
                frame with { Options = scoped },
                depth + 1))
            {
                yield return result with { Options = outerOptions };
            }
        }
    }

    private sealed class OptionChangeNode(
        RuntimeRegexOption enabled,
        RuntimeRegexOption disabled) : Node
    {
        internal RuntimeRegexOption Enabled => enabled;

        internal RuntimeRegexOption Disabled => disabled;

        internal override IEnumerable<Frame> Match(MatchContext context, Frame frame, int depth)
        {
            GuardDepth(context, depth);
            yield return frame with { Options = frame.Options.Apply(enabled, disabled) };
        }
    }

    private sealed class GroupNode(int number, string? name, Node body) : Node
    {
        internal int Number => number;

        internal string? Name => name;

        internal Node Body => body;

        internal override IEnumerable<Frame> Match(MatchContext context, Frame frame, int depth)
        {
            GuardDepth(context, depth);
            // Oniguruma compiles the lexical occurrence of a callable group through
            // the same CALL opcode as an explicit subexpression invocation. Account
            // for that initial native call frame; recursive calls then use MatchCalled
            // below and add exactly one frame apiece.
            if (context.IsCallableGroup(this))
            {
                using var matches = MatchCalled(context, frame, depth + 1).GetEnumerator();
                while (true)
                {
                    if (!context.TryEnterSubexpressionCall())
                    {
                        context.RetryAfterFailure();
                        yield break;
                    }

                    Frame result;
                    try
                    {
                        if (!matches.MoveNext())
                        {
                            yield break;
                        }

                        result = matches.Current;
                    }
                    finally
                    {
                        context.ExitSubexpressionCall();
                    }

                    yield return result;
                }
            }

            foreach (var result in MatchCalled(context, frame, depth + 1))
            {
                yield return result;
            }
        }

        internal IEnumerable<Frame> MatchCalled(MatchContext context, Frame frame, int depth)
        {
            var start = frame.Position;
            foreach (var result in body.Match(context, frame, depth + 1))
            {
                var capture = new CaptureSpan(start, result.Position, name);
                var captures = new Dictionary<int, CaptureSpan>(result.Captures)
                {
                    [number] = capture,
                };
                var captureLevels = new Dictionary<(int GroupNumber, int CallLevel), CaptureSpan>(
                    result.CaptureLevels)
                {
                    [(number, context.SubexpressionCallDepth)] = capture,
                };
                yield return result with
                {
                    Captures = captures,
                    CaptureLevels = captureLevels,
                };
            }
        }
    }

    private sealed class SubexpressionCallNode(
        int? number,
        string? name,
        string sourceReference) : Node
    {
        private GroupNode? _target;

        internal int? Number => number;

        internal string? Name => name;

        internal string SourceReference => sourceReference;

        internal GroupNode? Target => _target;

        internal void Bind(GroupNode target) => _target = target;

        internal override IEnumerable<Frame> Match(MatchContext context, Frame frame, int depth)
        {
            GuardDepth(context, depth);
            if (_target is not { } group)
            {
                throw new JqRuntimeException("Regex failure: undefined group reference");
            }

            using var matches = group.MatchCalled(context, frame, depth + 1).GetEnumerator();
            while (true)
            {
                if (!context.TryEnterSubexpressionCall())
                {
                    context.RetryAfterFailure();
                    yield break;
                }

                Frame result;
                try
                {
                    if (!matches.MoveNext())
                    {
                        yield break;
                    }

                    result = matches.Current;
                }
                finally
                {
                    context.ExitSubexpressionCall();
                }

                yield return result;
            }
        }
    }

    private sealed class BackreferenceNode(string name, int? nestLevel = null) : Node
    {
        internal string Name => name;

        internal int? NestLevel => nestLevel;

        internal override IEnumerable<Frame> Match(MatchContext context, Frame frame, int depth)
        {
            GuardDepth(context, depth);
            if (!context.Pattern.NamedGroups.TryGetValue(name, out var groups))
            {
                context.RetryAfterFailure();
                yield break;
            }

            foreach (var group in groups.OrderByDescending(group => group.Number))
            {
                if (!TryGetCapture(context, frame, group.Number, nestLevel, out var capture))
                {
                    continue;
                }

                if (TryMatchCapture(context, frame, capture, out var result))
                {
                    yield return result;
                    yield break;
                }
            }

            context.RetryAfterFailure();
        }
    }

    private sealed class NumericBackreferenceNode(int number, int? nestLevel = null) : Node
    {
        internal int Number => number;

        internal int? NestLevel => nestLevel;

        internal override IEnumerable<Frame> Match(MatchContext context, Frame frame, int depth)
        {
            GuardDepth(context, depth);
            if (!TryGetCapture(context, frame, number, nestLevel, out var capture))
            {
                context.RetryAfterFailure();
                yield break;
            }

            if (TryMatchCapture(context, frame, capture, out var result))
            {
                yield return result;
            }
            else
            {
                context.RetryAfterFailure();
            }
        }
    }

    private static bool TryGetCapture(
        MatchContext context,
        Frame frame,
        int groupNumber,
        int? nestLevel,
        out CaptureSpan capture)
    {
        if (nestLevel is not int level)
        {
            if (frame.Captures.TryGetValue(groupNumber, out var currentCapture))
            {
                capture = currentCapture;
                return true;
            }

            capture = null!;
            return false;
        }

        if (frame.CaptureLevels.TryGetValue(
                (groupNumber, context.SubexpressionCallDepth + level),
                out var leveledCapture))
        {
            capture = leveledCapture;
            return true;
        }

        capture = null!;
        return false;
    }

    private static bool TryMatchCapture(
        MatchContext context,
        Frame frame,
        CaptureSpan capture,
        out Frame result)
    {
        var length = capture.End - capture.Start;
        if (frame.Options.IgnoreCase && context.TryMatchFoldedBackreference(
                capture.Start,
                capture.End,
                frame.Position,
                out var consumed) &&
            frame.Position + consumed <= context.RightRange(frame))
        {
            result = frame with { Position = frame.Position + consumed };
            return true;
        }

        if (!frame.Options.IgnoreCase &&
            frame.Position + length <= context.RightRange(frame) &&
            context.ByteSpanEquals(capture.Start, frame.Position, length))
        {
            result = frame with { Position = frame.Position + length };
            return true;
        }

        result = frame;
        return false;
    }

    private sealed class RepeatNode(Node body, int minimum, int maximum, bool lazy, bool possessive) : Node
    {
        internal Node Body => body;

        internal int Minimum => minimum;

        internal int Maximum => maximum;

        internal bool Lazy => lazy;

        internal bool PossessiveValue => possessive;

        internal override IEnumerable<Frame> Match(MatchContext context, Frame frame, int depth)
        {
            GuardDepth(context, depth);
            if (body is LiteralNode literal && !frame.Options.IgnoreCase)
            {
                return RepeatOptimizedAtom(
                    context,
                    frame,
                    position => TryConsumeLiteral(context, frame, literal.Value, position));
            }

            if (body is AnyNode)
            {
                return RepeatOptimizedAtom(
                    context,
                    frame,
                    position => TryConsumeAny(context, frame, position));
            }

            if (!possessive)
            {
                return RepeatIteratively(context, frame, depth);
            }

            return Possessive(context, frame, depth);
        }

        private IEnumerable<Frame> RepeatOptimizedAtom(
            MatchContext context,
            Frame frame,
            Func<int, int> tryConsume)
        {
            var positions = new List<int> { frame.Position };
            while ((maximum < 0 || positions.Count - 1 < maximum) &&
                tryConsume(positions[^1]) is var next && next >= 0)
            {
                context.Tick();
                positions.Add(next);
            }

            var repetitions = positions.Count - 1;
            if (repetitions < minimum)
            {
                // Mandatory literal/any repetition failure still reaches the
                // native fail label. Optional terminal probes use specialized
                // peek/jump opcodes and do not consume a retry.
                context.RetryAfterFailure();
                yield break;
            }

            if (possessive)
            {
                yield return frame with
                {
                    Position = positions[lazy ? minimum : repetitions],
                };
                yield break;
            }

            if (lazy)
            {
                for (var count = minimum; count <= repetitions; count++)
                {
                    yield return frame with { Position = positions[count] };
                }

                yield break;
            }

            for (var count = repetitions; count >= minimum; count--)
            {
                yield return frame with { Position = positions[count] };
            }
        }

        private static int TryConsumeLiteral(
            MatchContext context,
            Frame frame,
            string literal,
            int position)
        {
            var byteLength = Encoding.UTF8.GetByteCount(literal);
            return position + byteLength <= context.RightRange(frame) &&
                context.ExactBytesEqual(position, literal)
                ? position + byteLength
                : -1;
        }

        private static int TryConsumeAny(MatchContext context, Frame frame, int position) =>
            position < context.RightRange(frame) &&
            (frame.Options.DotMatchesNewline || context.ByteAt(position) != (byte)'\n')
                ? position + context.EncodedCharacterWidth(position)
                : -1;

        private IEnumerable<Frame> Possessive(MatchContext context, Frame frame, int depth)
        {
            using var enumerator = RepeatIteratively(context, frame, depth).GetEnumerator();
            if (enumerator.MoveNext())
            {
                yield return enumerator.Current;
            }
        }

        private IEnumerable<Frame> RepeatIteratively(
            MatchContext context,
            Frame frame,
            int depth)
        {
            var levels = new Stack<RepeatLevel>();
            var suppressTerminalProbeRetry = IsNativePeekRepeatBody(
                context,
                body,
                frame.Options.IgnoreCase);
            levels.Push(new RepeatLevel(frame, 0));
            try
            {
                while (levels.Count != 0)
                {
                    var level = levels.Peek();
                    if (!level.Entered)
                    {
                        level.Entered = true;
                        if (lazy && level.Count >= minimum)
                        {
                            yield return level.Frame;
                            continue;
                        }
                    }

                    if (!level.ExpansionComplete)
                    {
                        if (maximum >= 0 && level.Count >= maximum)
                        {
                            level.ExpansionComplete = true;
                            continue;
                        }

                        level.BodyEnumerator ??=
                            body.Match(context, level.Frame, depth + 1).GetEnumerator();
                        bool moved;
                        if (suppressTerminalProbeRetry)
                        {
                            using (context.SuppressOptimizedRepeatProbeRetry())
                            {
                                moved = level.BodyEnumerator.MoveNext();
                            }
                        }
                        else
                        {
                            moved = level.BodyEnumerator.MoveNext();
                        }

                        if (moved)
                        {
                            var next = level.BodyEnumerator.Current;
                            if (next.Position == level.Frame.Position)
                            {
                                // Oniguruma's empty-repeat guard permits one eventful visit and
                                // then satisfies the structural repeat without invoking the empty
                                // body again. This is observable for fixed nullable repetitions.
                                yield return next;
                            }
                            else
                            {
                                levels.Push(new RepeatLevel(next, level.Count + 1));
                            }

                            continue;
                        }

                        level.BodyEnumerator.Dispose();
                        level.BodyEnumerator = null;
                        level.ExpansionComplete = true;
                        continue;
                    }

                    if (!level.GreedyResultVisited)
                    {
                        level.GreedyResultVisited = true;
                        if (!lazy && level.Count >= minimum)
                        {
                            yield return level.Frame;
                            continue;
                        }
                    }

                    level.Dispose();
                    levels.Pop();
                }
            }
            finally
            {
                while (levels.TryPop(out var level))
                {
                    level.Dispose();
                }
            }
        }

        private static bool IsNativePeekRepeatBody(
            MatchContext context,
            Node node,
            bool ignoreCase) => node switch
        {
            LiteralNode => !ignoreCase,
            AnyNode => true,
            OrdinaryAtomNode ordinary =>
                !ignoreCase && ordinary.Token.Kind == OnigurumaCalloutAtomKind.Literal,
            GroupNode group when !context.IsCallableGroup(group) =>
                IsNativePeekRepeatBody(context, group.Body, ignoreCase),
            _ => false,
        };

        private sealed class RepeatLevel(Frame frame, int count) : IDisposable
        {
            internal Frame Frame { get; } = frame;

            internal int Count { get; } = count;

            internal bool Entered { get; set; }

            internal bool ExpansionComplete { get; set; }

            internal bool GreedyResultVisited { get; set; }

            internal IEnumerator<Frame>? BodyEnumerator { get; set; }

            public void Dispose() => BodyEnumerator?.Dispose();
        }
    }

    private sealed class LookaroundNode(Node body, bool positive) : Node
    {
        internal Node Body => body;

        internal bool Positive => positive;

        internal override IEnumerable<Frame> Match(MatchContext context, Frame frame, int depth)
        {
            GuardDepth(context, depth);
            using var enumerator = body.Match(context, frame, depth + 1).GetEnumerator();
            var matched = enumerator.MoveNext();
            if (positive && matched)
            {
                yield return enumerator.Current with { Position = frame.Position };
            }
            else if (!positive && !matched)
            {
                yield return frame;
            }
            else if (!positive)
            {
                // A successful negative assertion executes Oniguruma's
                // FAIL_NEG_LOOK opcode and resumes at the saved alternative.
                context.RetryAfterFailure();
            }
        }
    }

    private sealed class LookbehindNode(Node body, bool positive) : Node
    {
        private Node _body = body;
        private int _characterWidth;

        internal Node Body => _body;

        internal bool Positive => positive;

        internal void Bind(Node normalizedBody, int characterWidth)
        {
            _body = normalizedBody;
            _characterWidth = characterWidth;
        }

        internal override IEnumerable<Frame> Match(MatchContext context, Frame frame, int depth)
        {
            GuardDepth(context, depth);
            if (!context.TryStepBackCharacters(frame.Position, _characterWidth, out var start))
            {
                if (!positive)
                {
                    yield return frame;
                }
                else
                {
                    context.RetryAfterFailure();
                }

                yield break;
            }

            var lookbehindFrame = frame with
            {
                Position = start,
                Options = frame.Options with { InLookbehind = true },
            };
            var matched = false;
            foreach (var result in _body.Match(context, lookbehindFrame, depth + 1))
            {
                matched = true;

                if (positive)
                {
                    // OP_STEP_BACK_START_CHAR moves to a character head, then the fixed-width
                    // body executes without CHECK_POSITION. At a global restart on a UTF-8
                    // continuation byte, that body can therefore finish after the nominal
                    // assertion position. Preserve that regexec.c cursor while the successful
                    // iterator remains live; captures and transactional callout state must also
                    // remain active through downstream nodes.
                    yield return result with
                    {
                        Options = frame.Options,
                    };
                }

                yield break;
            }

            if (!positive)
            {
                if (matched)
                {
                    context.RetryAfterFailure();
                }
                else
                {
                    yield return frame;
                }
            }
        }

    }

    private sealed class AbsentRangeNode(Node forbidden) : Node
    {
        internal Node Forbidden => forbidden;

        internal override IEnumerable<Frame> Match(MatchContext context, Frame frame, int depth)
        {
            GuardDepth(context, depth);
            var boundary = FindAbsentBoundary(context, frame, forbidden, depth);
            for (var end = boundary;; end = context.RetreatEncodedCharacter(end))
            {
                yield return frame with
                {
                    Position = end,
                    AbsentSideEffect = frame.AbsentSideEffect ||
                        context.IsMatchingAbsentDelimiter,
                };
                if (end == frame.Position)
                {
                    break;
                }
            }

            // Exhausting the lazy absent engine resumes through its saved tail.
            context.RetryAfterFailure();
        }
    }

    private sealed class AbsentExpressionNode(Node forbidden, Node expression) : Node
    {
        internal Node Forbidden => forbidden;

        internal Node Expression => expression;

        internal override IEnumerable<Frame> Match(MatchContext context, Frame frame, int depth)
        {
            GuardDepth(context, depth);
            var boundary = FindAbsentBoundary(context, frame, forbidden, depth);
            foreach (var result in expression.Match(
                context,
                frame with { RightRange = boundary },
                depth + 1))
            {
                yield return result with
                {
                    RightRange = frame.RightRange,
                    AbsentSideEffect = result.AbsentSideEffect ||
                        context.IsMatchingAbsentDelimiter,
                };
            }

            context.RetryAfterFailure();
        }
    }

    private sealed class AbsentStopperNode(Node forbidden) : Node
    {
        internal Node Forbidden => forbidden;

        internal override IEnumerable<Frame> Match(MatchContext context, Frame frame, int depth)
        {
            GuardDepth(context, depth);
            var boundary = FindAbsentBoundary(context, frame, forbidden, depth);
            yield return frame with
            {
                RightRange = boundary,
                AbsentSideEffect = frame.AbsentSideEffect || context.IsMatchingAbsentDelimiter,
            };
            context.RetryAfterFailure();
        }
    }

    private sealed class AbsentRangeClearNode : Node
    {
        internal override IEnumerable<Frame> Match(MatchContext context, Frame frame, int depth)
        {
            GuardDepth(context, depth);
            yield return frame with
            {
                RightRange = null,
                AbsentSideEffect = frame.AbsentSideEffect || context.IsMatchingAbsentDelimiter,
            };
        }
    }

    private static int FindAbsentBoundary(
        MatchContext context,
        Frame frame,
        Node forbidden,
        int depth)
    {
        var range = context.RightRange(frame);
        using var delimiterScope = context.EnterAbsentDelimiter();
        var sawNestedAbsentSideEffect = false;
        for (var probe = frame.Position; probe <= range;)
        {
            var probeFrame = frame with { Position = probe };
            using var matches = forbidden.Match(context, probeFrame, depth + 1).GetEnumerator();
            var matched = false;
            while (matches.MoveNext())
            {
                if (matches.Current.AbsentSideEffect && !frame.AbsentSideEffect)
                {
                    // A nested absent engine cuts the enclosing engine's SAVE_S frame.
                    // Its apparent result therefore resumes through the native fail edge
                    // instead of becoming a successful outer delimiter match.
                    sawNestedAbsentSideEffect = true;
                    context.RetryAfterFailure();
                    continue;
                }

                // Oniguruma forces every delimiter success to fail before continuing.
                // This keeps progress callouts while retracting transactional state.
                matched = true;
                context.RetryAfterFailure();
            }

            if (matched || probe == range)
            {
                // make_absent_engine next attempts its step arm. A successful delimiter
                // first narrows right_range to this position, while an end probe already
                // equals it; either way that step reaches the native fail/pop edge.
                context.RetryAfterFailure();
                if (sawNestedAbsentSideEffect)
                {
                    // A nested SUPER absent path also removes the enclosing SAVE_S;
                    // restoring that cut frame contributes one further native pop.
                    context.RetryAfterFailure();
                }

                return probe;
            }

            probe += context.EncodedCharacterWidth(probe);
        }

        return range;
    }

    private sealed class AtomicNode(Node body) : Node
    {
        internal Node Body => body;

        internal override IEnumerable<Frame> Match(MatchContext context, Frame frame, int depth)
        {
            GuardDepth(context, depth);
            using var enumerator = body.Match(context, frame, depth + 1).GetEnumerator();
            if (enumerator.MoveNext())
            {
                yield return enumerator.Current;
            }
        }
    }

    private sealed class CaptureConditionalNode(
        int? groupNumber,
        string? groupName,
        int? nestLevel,
        Node whenTrue,
        Node whenFalse) : Node
    {
        internal Node WhenTrue => whenTrue;

        internal Node WhenFalse => whenFalse;

        internal override IEnumerable<Frame> Match(MatchContext context, Frame frame, int depth)
        {
            GuardDepth(context, depth);
            var matched = CaptureWasSet(context, frame, groupNumber, groupName, nestLevel);
            if (!matched)
            {
                // BAG_IF_ELSE compiles an OP_PUSH before the checker. A false checker
                // reaches regexec.c's fail/STACK_POP edge before entering the else arm.
                context.RetryAfterFailure();
            }

            var branch = matched ? whenTrue : whenFalse;
            return branch.Match(context, frame, depth + 1);
        }
    }

    private sealed class CaptureCheckNode(
        int? groupNumber,
        string? groupName,
        int? nestLevel) : Node
    {
        internal override IEnumerable<Frame> Match(MatchContext context, Frame frame, int depth)
        {
            GuardDepth(context, depth);
            if (CaptureWasSet(context, frame, groupNumber, groupName, nestLevel))
            {
                yield return frame;
            }
            else
            {
                context.RetryAfterFailure();
            }
        }
    }

    private sealed class PatternConditionalNode(Node condition, Node whenTrue, Node whenFalse) : Node
    {
        internal Node Condition => condition;

        internal Node WhenTrue => whenTrue;

        internal Node WhenFalse => whenFalse;

        internal override IEnumerable<Frame> Match(MatchContext context, Frame frame, int depth)
        {
            GuardDepth(context, depth);
            using var conditionMatches = condition.Match(context, frame, depth + 1).GetEnumerator();
            if (conditionMatches.MoveNext())
            {
                // regcomp.c emits CUT_TO_MARK immediately after the first successful
                // condition path. Its captures and consumed position feed the then arm,
                // but neither another condition path nor the else arm remains available.
                foreach (var result in whenTrue.Match(
                    context,
                    conditionMatches.Current,
                    depth + 1))
                {
                    yield return result;
                }

                yield break;
            }

            // The failed condition pops BAG_IF_ELSE's saved else continuation.
            context.RetryAfterFailure();
            foreach (var result in whenFalse.Match(context, frame, depth + 1))
            {
                yield return result;
            }
        }
    }

    private static bool CaptureWasSet(
        MatchContext context,
        Frame frame,
        int? groupNumber,
        string? groupName,
        int? nestLevel)
    {
        if (groupNumber is int number)
        {
            return TryGetCapture(context, frame, number, nestLevel, out _);
        }

        return groupName is not null &&
            context.Pattern.NamedGroups.TryGetValue(groupName, out var groups) &&
            groups.Any(group => TryGetCapture(
                context,
                frame,
                group.Number,
                nestLevel,
                out _));
    }

    private sealed class CalloutNode(
        int id,
        CalloutKind kind,
        string? tag,
        string[] arguments,
        bool conditional = false) : Node
    {
        internal int Id => id;

        internal CalloutKind Kind => kind;

        internal string? Tag => tag;

        internal string[] Arguments => arguments;

        internal bool Conditional => conditional;

        internal override IEnumerable<Frame> Match(MatchContext context, Frame frame, int depth)
        {
            GuardDepth(context, depth);
            var entry = context.Runtime.Enter(this, frame.Position);
            if (!entry.Success)
            {
                // onig_builtin_fail returns ONIG_CALLOUT_FAIL, which enters
                // regexec.c's ordinary `fail:`/STACK_POP retry path.
                context.RetryAfterFailure();
                yield break;
            }

            try
            {
                yield return frame;
            }
            finally
            {
                entry.Retract();
            }
        }
    }

    private sealed class ConditionalCalloutNode(CalloutNode callout, Node whenTrue, Node whenFalse) : Node
    {
        internal Node WhenTrue => whenTrue;

        internal Node WhenFalse => whenFalse;

        internal override IEnumerable<Frame> Match(MatchContext context, Frame frame, int depth)
        {
            GuardDepth(context, depth);
            var entry = context.Runtime.Enter(callout, frame.Position);
            try
            {
                var branch = entry.Success ? whenTrue : whenFalse;
                foreach (var result in branch.Match(context, frame, depth + 1))
                {
                    yield return result;
                }
            }
            finally
            {
                entry.Retract();
            }
        }
    }

    private sealed class Parser(string pattern, RunnerFlags flags)
    {
        private readonly List<CalloutNode> _callouts = [];
        private readonly List<GroupNode> _groups = [];
        private readonly Dictionary<string, List<GroupNode>> _namedGroups = new(StringComparer.Ordinal);
        private readonly List<SubexpressionCallNode> _subexpressionCalls = [];
        private readonly List<NumericBackreferenceNode> _numericBackreferences = [];
        private readonly List<BackreferenceNode> _namedBackreferences = [];
        private readonly List<LookbehindNode> _lookbehinds = [];
        private int _index;
        private int _groupNumber;
        private bool _extended = flags.Extended;
        private RuntimeRegexOption _lexicalOptions =
            (flags.IgnoreCase ? RuntimeRegexOption.IgnoreCase : RuntimeRegexOption.None) |
            (flags.DotMatchesNewline
                ? RuntimeRegexOption.DotMatchesNewline
                : RuntimeRegexOption.None);
        private int _parseDepth;
        private bool _requiresSourceShapedExecution;

        internal CompiledPattern Parse()
        {
            var root = ParseAlternation(terminator: null);
            if (_index != pattern.Length)
            {
                throw Unsupported("unexpected token");
            }

            if (_numericBackreferences.Any(reference =>
                reference.Number <= 0 || reference.Number > _groupNumber))
            {
                throw new JqRuntimeException("Regex failure: invalid backref number/name");
            }

            var undefinedNamedBackreference = _namedBackreferences.FirstOrDefault(reference =>
                !_namedGroups.ContainsKey(reference.Name));
            if (undefinedNamedBackreference is not null)
            {
                throw new JqRuntimeException(
                    "Regex failure: undefined name <" +
                    undefinedNamedBackreference.Name + "> reference");
            }

            var tags = new Dictionary<string, CalloutNode>(StringComparer.Ordinal);
            foreach (var callout in _callouts)
            {
                if (callout.Tag is not null && !tags.TryAdd(callout.Tag, callout))
                {
                    throw new JqRuntimeException(
                        "Regex failure: multiplex defined name <" + callout.Tag + ">");
                }
            }

            foreach (var callout in _callouts)
            {
                IEnumerable<string> references = callout.Kind switch
                {
                    CalloutKind.Max => callout.Arguments.Take(1),
                    CalloutKind.Compare => callout.Arguments.Where((_, argumentIndex) =>
                        argumentIndex is 0 or 2),
                    _ => [],
                };
                foreach (var reference in references)
                {
                    if (!OnigurumaCalloutArgumentParser.TryParseLong(reference, out _) &&
                        !tags.ContainsKey(reference))
                    {
                        throw new JqRuntimeException("Regex failure: invalid callout tag name");
                    }
                }
            }

            GroupNode? wholePatternGroup = null;
            if (_subexpressionCalls.Any(call => call.Number == 0))
            {
                wholePatternGroup = new GroupNode(0, null, root);
                root = wholePatternGroup;
            }

            var groupsByNumber = _groups.ToDictionary(group => group.Number);
            if (wholePatternGroup is not null)
            {
                groupsByNumber.Add(0, wholePatternGroup);
            }

            var callableGroups = new HashSet<GroupNode>();
            foreach (var call in _subexpressionCalls)
            {
                GroupNode target;
                if (call.Number is int number)
                {
                    if (!groupsByNumber.TryGetValue(number, out var numericTarget))
                    {
                        throw new JqRuntimeException(
                            "Regex failure: undefined group <" +
                            call.SourceReference + "> reference");
                    }

                    target = numericTarget;
                }
                else if (call.Name is { } name &&
                    _namedGroups.TryGetValue(name, out var definitions))
                {
                    if (definitions.Count != 1)
                    {
                        throw new JqRuntimeException(
                            "Regex failure: multiplex definition name <" + name + "> call");
                    }

                    target = definitions[0];
                }
                else
                {
                    throw new JqRuntimeException(call.Name is { } undefinedName
                        ? "Regex failure: undefined name <" + undefinedName + "> reference"
                        : "Regex failure: undefined group reference");
                }

                call.Bind(target);
                callableGroups.Add(target);
            }

            foreach (var callableGroup in callableGroups)
            {
                var recursion = CheckInfiniteRecursion(
                    callableGroup.Body,
                    callableGroup,
                    head: true,
                    new HashSet<GroupNode>());
                if ((recursion & (RecursionResult.Must | RecursionResult.Infinite)) != 0)
                {
                    throw new JqRuntimeException("Regex failure: never ending recursion");
                }
            }

            var hasAbsentLookbehind = _lookbehinds.Any(lookbehind =>
                ContainsAbsent(lookbehind.Body));
            foreach (var lookbehind in _lookbehinds)
            {
                if (!lookbehind.Positive && ContainsCapture(lookbehind.Body))
                {
                    throw new JqRuntimeException("Regex failure: invalid pattern in look-behind");
                }

                var normalizedBody = ReduceLookbehindQuantifiers(lookbehind.Body);
                if (!TryGetLookbehindCharacterWidthRange(
                        normalizedBody,
                        new HashSet<Node>(),
                        out var characterRange) ||
                    characterRange.Minimum > 65_535 ||
                    characterRange.Maximum is int finiteMaximum && finiteMaximum > 65_535)
                {
                    throw new JqRuntimeException("Regex failure: invalid pattern in look-behind");
                }

                if (characterRange.Minimum == 0 && characterRange.MinimumIsSure &&
                    !ContainsLookbehindWidthDependency(normalizedBody))
                {
                    // tune_look_behind() applies onig_node_reset_empty/fail before
                    // checking Perl-NG's variable-width syntax bit. This is what
                    // makes default absent and optimized one-character absent
                    // expressions valid zero-width assertions.
                    normalizedBody = EmptyNode.Instance;
                    characterRange = new LookbehindCharacterWidthRange(0, 0, true);
                }
                else if (characterRange.Maximum != characterRange.Minimum)
                {
                    throw new JqRuntimeException("Regex failure: invalid pattern in look-behind");
                }

                lookbehind.Bind(normalizedBody, characterRange.Minimum);
            }

            var eventSensitive = _callouts.Any(IsEventSensitive) || ContainsKeep(root) ||
                hasAbsentLookbehind;
            return new CompiledPattern(
                root,
                _namedGroups.ToDictionary(
                    entry => entry.Key,
                    entry => (IReadOnlyList<GroupNode>)entry.Value,
                    StringComparer.Ordinal),
                _groups,
                callableGroups,
                tags,
                OnigurumaCalloutOptimizationPlan.Create(
                    BuildOptimizerNode(
                        root,
                        new RuntimeRegexOptions(
                            flags.IgnoreCase,
                            flags.DotMatchesNewline,
                            false,
                            false)),
                    ignoreCase: false),
                eventSensitive,
                _callouts.Any(callout => callout.Kind == CalloutKind.Mismatch),
                flags.IgnoreCase,
                _requiresSourceShapedExecution);
        }

        private static RecursionResult CheckInfiniteRecursion(
            Node node,
            GroupNode target,
            bool head,
            HashSet<GroupNode> resolving)
        {
            switch (node)
            {
                case SequenceNode sequence:
                {
                    var result = RecursionResult.None;
                    foreach (var child in sequence.Nodes)
                    {
                        var childResult = CheckInfiniteRecursion(
                            child,
                            target,
                            head,
                            resolving);
                        if ((childResult & RecursionResult.Infinite) != 0)
                        {
                            return childResult;
                        }

                        result |= childResult;
                        if (head && MinimumByteLength(child, new HashSet<Node>()) != 0)
                        {
                            head = false;
                        }
                    }

                    return result;
                }
                case AlternationNode alternation:
                {
                    var result = RecursionResult.None;
                    var must = RecursionResult.Must;
                    foreach (var alternative in alternation.Alternatives)
                    {
                        var branch = CheckInfiniteRecursion(
                            alternative,
                            target,
                            head,
                            new HashSet<GroupNode>(resolving));
                        if ((branch & RecursionResult.Infinite) != 0)
                        {
                            return branch;
                        }

                        result |= branch & RecursionResult.Exist;
                        must &= branch;
                    }

                    return result | must;
                }
                case RepeatNode repeat when repeat.Maximum != 0:
                {
                    var result = CheckInfiniteRecursion(
                        repeat.Body,
                        target,
                        head,
                        resolving);
                    return repeat.Minimum == 0
                        ? result & ~RecursionResult.Must
                        : result;
                }
                case GroupNode group:
                    return CheckInfiniteRecursion(group.Body, target, head, resolving);
                case AtomicNode atomic:
                    return CheckInfiniteRecursion(atomic.Body, target, head, resolving);
                case OptionScopeNode option:
                    return CheckInfiniteRecursion(option.Body, target, head, resolving);
                case LookaroundNode lookaround:
                    return CheckInfiniteRecursion(lookaround.Body, target, head, resolving);
                case LookbehindNode lookbehind:
                    return CheckInfiniteRecursion(lookbehind.Body, target, head, resolving);
                case SubexpressionCallNode call when call.Target is { } called:
                    if (called == target || !resolving.Add(called))
                    {
                        return head
                            ? RecursionResult.Exist | RecursionResult.Must | RecursionResult.Infinite
                            : RecursionResult.Exist | RecursionResult.Must;
                    }

                    try
                    {
                        return CheckInfiniteRecursion(called.Body, target, head, resolving);
                    }
                    finally
                    {
                        resolving.Remove(called);
                    }
                case CaptureConditionalNode conditional:
                {
                    var whenTrue = CheckInfiniteRecursion(
                        conditional.WhenTrue, target, head, resolving);
                    var whenFalse = CheckInfiniteRecursion(
                        conditional.WhenFalse, target, head, resolving);
                    return (whenTrue | whenFalse) &
                        (RecursionResult.Exist |
                         (whenTrue & whenFalse & RecursionResult.Must) |
                         RecursionResult.Infinite);
                }
                case PatternConditionalNode conditional:
                    return CheckInfiniteRecursion(
                               conditional.Condition, target, head, resolving) |
                           CheckInfiniteRecursion(
                               conditional.WhenTrue, target, head, resolving) |
                           CheckInfiniteRecursion(
                               conditional.WhenFalse, target, head, resolving);
                default:
                    return RecursionResult.None;
            }
        }

        private static int MinimumByteLength(Node node, HashSet<Node> resolving)
        {
            if (!resolving.Add(node))
            {
                return 0;
            }

            try
            {
                return node switch
                {
                    LiteralNode literal => Encoding.UTF8.GetByteCount(literal.Value),
                    CrudeByteLiteralNode crude => crude.Bytes.Length,
                    AnyNode or CharacterClassNode or CharacterPredicateNode => 1,
                    OrdinaryAtomNode ordinary => ordinary.Token.Kind switch
                    {
                        OnigurumaCalloutAtomKind.Literal =>
                            Encoding.UTF8.GetByteCount(ordinary.Token.Literal!),
                        OnigurumaCalloutAtomKind.WordBoundary or
                        OnigurumaCalloutAtomKind.NonWordBoundary or
                        OnigurumaCalloutAtomKind.TextBoundary or
                        OnigurumaCalloutAtomKind.TextNonBoundary => 0,
                        OnigurumaCalloutAtomKind.Impossible => int.MaxValue,
                        _ => 1,
                    },
                    SequenceNode sequence => sequence.Nodes.Aggregate(
                        0,
                        (length, child) => SaturatingLengthAdd(
                            length,
                            MinimumByteLength(child, resolving))),
                    AlternationNode alternation => alternation.Alternatives.Count == 0
                        ? 0
                        : alternation.Alternatives.Min(child =>
                            MinimumByteLength(child, new HashSet<Node>(resolving))),
                    RepeatNode repeat => repeat.Minimum == 0
                        ? 0
                        : SaturatingLengthMultiply(
                            MinimumByteLength(repeat.Body, resolving),
                            repeat.Minimum),
                    GroupNode group => MinimumByteLength(group.Body, resolving),
                    AtomicNode atomic => MinimumByteLength(atomic.Body, resolving),
                    OptionScopeNode option => MinimumByteLength(option.Body, resolving),
                    _ => 0,
                };
            }
            finally
            {
                resolving.Remove(node);
            }
        }

        private static int SaturatingLengthAdd(int left, int right) =>
            left >= int.MaxValue - right ? int.MaxValue : left + right;

        private static int SaturatingLengthMultiply(int value, int count) =>
            value == 0 || count == 0
                ? 0
                : value > int.MaxValue / count ? int.MaxValue : value * count;

        [Flags]
        private enum RecursionResult
        {
            None = 0,
            Exist = 1 << 0,
            Must = 1 << 1,
            Infinite = 1 << 2,
        }

        private Node ParseAlternation(char? terminator)
        {
            EnterParseDepth();
            try
            {
                var alternatives = new List<Node>();
                while (true)
                {
                    var branchOptions = _lexicalOptions;
                    alternatives.Add(new OptionScopeNode(
                        ParseSequence(terminator),
                        branchOptions,
                        AllRuntimeRegexOptions & ~branchOptions,
                        transparentRepeatTarget: true));
                    if (_index >= pattern.Length || pattern[_index] != '|')
                    {
                        break;
                    }

                    _index++;
                }

                return alternatives.Count == 1 ? alternatives[0] : new AlternationNode(alternatives);
            }
            finally
            {
                _parseDepth--;
            }
        }

        private Node ParseSequence(char? terminator)
        {
            EnterParseDepth();
            try
            {
                var nodes = new List<Node>();
                while (true)
                {
                    SkipLexicalTrivia();
                    if (_index >= pattern.Length ||
                        terminator is char terminatorValue && pattern[_index] == terminatorValue ||
                        pattern[_index] == '|')
                    {
                        break;
                    }

                    AppendSequenceNode(nodes, ParseQuantifiedAtom());
                }

                return nodes.Count switch
                {
                    0 => EmptyNode.Instance,
                    1 => nodes[0],
                    _ => new SequenceNode(nodes),
                };
            }
            finally
            {
                _parseDepth--;
            }
        }

        private void AppendSequenceNode(List<Node> nodes, Node node)
        {
            if (node is SequenceNode sequence)
            {
                foreach (var child in sequence.Nodes)
                {
                    AppendSequenceNode(nodes, child);
                }

                return;
            }

            if (!TryGetLiteralRun(node, out var literal))
            {
                nodes.Add(node);
                return;
            }

            // regcomp.c concatenates adjacent string nodes after syntax parsing.
            // That concatenation crosses non-capturing-group and source-spelling
            // boundaries (plain, quoted, control, and radix literals), but the
            // non-literal nodes retained above remain hard fold boundaries.
            if (literal.Length == 0)
            {
                return;
            }

            if (nodes.Count != 0 && nodes[^1] is LiteralNode previous)
            {
                nodes[^1] = new LiteralNode(previous.Value + literal);
            }
            else
            {
                nodes.Add(new LiteralNode(literal));
            }
        }

        private bool TryGetLiteralRun(Node node, out string literal)
        {
            switch (node)
            {
                case LiteralNode direct:
                    literal = direct.Value;
                    return true;
                case OrdinaryAtomNode ordinary when
                    ordinary.Token.Kind == OnigurumaCalloutAtomKind.Literal:
                    literal = ordinary.Token.Literal!;
                    return true;
                case CrudeByteLiteralNode crude when crude.CanonicalText is { } canonical:
                    literal = canonical;
                    return true;
                case OptionScopeNode optionScope when
                    optionScope.TransparentRepeatTarget &&
                    optionScope.Enabled == _lexicalOptions &&
                    optionScope.Disabled == (AllRuntimeRegexOptions & ~_lexicalOptions):
                    return TryGetLiteralRun(optionScope.Body, out literal);
                default:
                    literal = string.Empty;
                    return false;
            }
        }

        private void EnterParseDepth()
        {
            _parseDepth++;
            if (_parseDepth > MaximumParseDepth)
            {
                throw new JqRuntimeException("Regex failure: parse depth limit over");
            }
        }

        private void VerifyChildParseDepth()
        {
            if (_parseDepth == MaximumParseDepth)
            {
                throw new JqRuntimeException("Regex failure: parse depth limit over");
            }
        }

        private void SkipExtendedTrivia()
        {
            if (!_extended)
            {
                return;
            }

            while (_index < pattern.Length)
            {
                if (pattern[_index] is ' ' or '\t' or '\n' or '\r' or '\f')
                {
                    _index++;
                    continue;
                }

                if (pattern[_index] != '#')
                {
                    break;
                }

                _index = pattern.IndexOf('\n', _index + 1);
                if (_index < 0)
                {
                    _index = pattern.Length;
                    break;
                }
            }
        }

        private Node ParseQuantifiedAtom()
        {
            // fetch_token() recognizes a complete repeat token before prs_exp()
            // decides that the current branch has no target. Reuse the real
            // interval scanner so its overflow/range diagnostics keep precedence.
            if (TryParseQuantifier(out _))
            {
                throw new JqRuntimeException(
                    "Regex failure: target of repeat operator is not specified");
            }

            var atom = ParseAtom();
            Node? literalPrefix = null;
            var parsedQuantifier = false;
            var quantifierParseDepth = _parseDepth;
            while (true)
            {
                SkipLexicalTrivia();
                if (!TryParseQuantifier(out var quantifier))
                {
                    return literalPrefix is null
                        ? atom
                        : new SequenceNode([literalPrefix, atom]);
                }

                var hadPriorQuantifier = parsedQuantifier;
                if (hadPriorQuantifier)
                {
                    _requiresSourceShapedExecution = true;
                }

                parsedQuantifier = true;

                var targetKind = GetRepeatTargetKind(atom);
                if (targetKind == RepeatTargetKind.NotSpecified)
                {
                    throw new JqRuntimeException(
                        "Regex failure: target of repeat operator is not specified");
                }

                if (targetKind == RepeatTargetKind.Invalid)
                {
                    throw new JqRuntimeException(
                        "Regex failure: target of repeat operator is invalid");
                }

                // regparse.c increments a local copy of parse_depth only after
                // target validation, then carries that copy across chained repeats.
                if (++quantifierParseDepth > MaximumParseDepth)
                {
                    throw new JqRuntimeException("Regex failure: parse depth limit over");
                }

                if (quantifier.Minimum == 1 && quantifier.Maximum == 1)
                {
                    // assign_quantifier_body() removes exact-one before string-node
                    // concatenation, including lazy/possessive spellings.
                    continue;
                }

                // assign_quantifier_body() splits the final encoded character from an
                // ungrouped ND_STRING. Exact-one returns before that switch; a following
                // repeat re-enters with the syntactic non-capturing group removed.
                if (TrySplitQuantifiedLiteral(
                        atom,
                        allowTransparentGroup: hadPriorQuantifier,
                        out var prefix,
                        out var suffix))
                {
                    literalPrefix = literalPrefix is null
                        ? prefix
                        : new SequenceNode([literalPrefix, prefix]);
                    atom = suffix;
                }

                if (HasTransparentRepeatTarget(atom))
                {
                    _requiresSourceShapedExecution = true;
                }

                atom = ReduceNestedQuantifier(atom, quantifier);
                if (quantifier.Possessive)
                {
                    // regparse.c represents possessive spelling as an atomic BAG
                    // around the already-reduced quantifier. This is observable when
                    // a greedy possessive suffix wraps a reduced lazy repeat.
                    atom = new AtomicNode(atom);
                }
            }
        }

        private bool TrySplitQuantifiedLiteral(
            Node atom,
            bool allowTransparentGroup,
            out Node prefix,
            out Node suffix)
        {
            var literal = atom switch
            {
                LiteralNode direct => direct.Value,
                OrdinaryAtomNode ordinary when
                    ordinary.Token.Kind == OnigurumaCalloutAtomKind.Literal =>
                    ordinary.Token.Literal!,
                SequenceNode sequence when sequence.Nodes.All(node => node is LiteralNode) =>
                    string.Concat(sequence.Nodes.Cast<LiteralNode>().Select(node => node.Value)),
                _ => string.Empty,
            };
            if (literal.Length == 0 && allowTransparentGroup &&
                atom is OptionScopeNode { TransparentRepeatTarget: true } optionScope &&
                optionScope.Enabled == _lexicalOptions &&
                optionScope.Disabled == (AllRuntimeRegexOptions & ~_lexicalOptions) &&
                TrySplitQuantifiedLiteral(
                    optionScope.Body,
                    allowTransparentGroup: true,
                    out var scopedPrefix,
                    out var scopedSuffix))
            {
                prefix = new OptionScopeNode(
                    scopedPrefix,
                    optionScope.Enabled,
                    optionScope.Disabled,
                    transparentRepeatTarget: true);
                suffix = new OptionScopeNode(
                    scopedSuffix,
                    optionScope.Enabled,
                    optionScope.Disabled,
                    transparentRepeatTarget: true);
                return true;
            }

            if (literal.Length == 0)
            {
                prefix = EmptyNode.Instance;
                suffix = atom;
                return false;
            }

            // GetRuneAt() cannot begin on the low surrogate of a supplementary rune.
            // Move to its high surrogate when the final UTF-16 code unit is low.
            var suffixStart = char.IsLowSurrogate(literal[^1])
                ? literal.Length - 2
                : literal.Length - 1;
            if (suffixStart <= 0)
            {
                prefix = EmptyNode.Instance;
                suffix = atom;
                return false;
            }

            prefix = new LiteralNode(literal[..suffixStart]);
            suffix = new LiteralNode(literal[suffixStart..]);
            return true;
        }

        private bool TryParseQuantifier(out ParsedQuantifier quantifier)
        {
            quantifier = default;
            if (_index >= pattern.Length)
            {
                return false;
            }

            var minimum = 0;
            var maximum = 0;
            var start = _index;
            switch (pattern[_index])
            {
                case '*':
                    minimum = 0;
                    maximum = -1;
                    _index++;
                    break;
                case '+':
                    minimum = 1;
                    maximum = -1;
                    _index++;
                    break;
                case '?':
                    minimum = 0;
                    maximum = 1;
                    _index++;
                    break;
                case '{':
                    if (!TryParseInterval(out minimum, out maximum))
                    {
                        _index = start;
                        return false;
                    }

                    break;
                default:
                    return false;
            }

            var lazy = false;
            var possessive = false;
            if (_index < pattern.Length && pattern[_index] == '?')
            {
                lazy = true;
                _index++;
            }
            else if (_index < pattern.Length && pattern[_index] == '+')
            {
                possessive = true;
                _index++;
            }

            quantifier = new ParsedQuantifier(
                minimum,
                maximum,
                lazy,
                possessive);
            return true;
        }

        private bool TryParseInterval(out int minimum, out int maximum)
        {
            minimum = 0;
            maximum = 0;
            var start = _index;
            var cursor = start + 1;
            if (!TryScanRepeatNumber(ref cursor, out minimum))
            {
                return false;
            }

            if (cursor >= pattern.Length)
            {
                return false;
            }

            if (pattern[cursor] == '}')
            {
                maximum = minimum;
                _index = cursor + 1;
                return true;
            }

            if (pattern[cursor] != ',')
            {
                return false;
            }

            cursor++;
            if (cursor < pattern.Length && pattern[cursor] == '}')
            {
                maximum = -1;
                _index = cursor + 1;
                return true;
            }

            if (!TryScanRepeatNumber(ref cursor, out maximum) ||
                cursor >= pattern.Length || pattern[cursor] != '}')
            {
                return false;
            }

            if (maximum < minimum)
            {
                throw new JqRuntimeException(
                    "Regex failure: upper is smaller than lower in repeat range");
            }

            _index = cursor + 1;
            return true;
        }

        private bool TryScanRepeatNumber(ref int cursor, out int value)
        {
            value = 0;
            var start = cursor;
            while (cursor < pattern.Length && char.IsAsciiDigit(pattern[cursor]))
            {
                var digit = pattern[cursor++] - '0';
                if (value > (100_000 - digit) / 10)
                {
                    throw new JqRuntimeException(
                        "Regex failure: too big number for repeat range");
                }

                value = value * 10 + digit;
            }

            return cursor != start;
        }

        private static RepeatTargetKind GetRepeatTargetKind(Node node) => node switch
        {
            CommentNode or OptionChangeNode => RepeatTargetKind.NotSpecified,
            AnchorNode or ResetMatchStartNode or LookaroundNode or LookbehindNode or
                CalloutNode => RepeatTargetKind.Invalid,
            OrdinaryAtomNode ordinary when ordinary.Token.Kind is
                OnigurumaCalloutAtomKind.WordBoundary or
                OnigurumaCalloutAtomKind.NonWordBoundary or
                OnigurumaCalloutAtomKind.TextBoundary or
                OnigurumaCalloutAtomKind.TextNonBoundary => RepeatTargetKind.Invalid,
            AlternationNode alternation when alternation.Alternatives.Any(
                alternative => GetRepeatTargetKind(alternative) == RepeatTargetKind.Invalid) =>
                RepeatTargetKind.Invalid,
            OptionScopeNode optionScope when optionScope.TransparentRepeatTarget =>
                GetRepeatTargetKind(optionScope.Body),
            _ => RepeatTargetKind.Valid,
        };

        private static bool HasTransparentRepeatTarget(Node node) => node switch
        {
            RepeatNode => true,
            OptionScopeNode { TransparentRepeatTarget: true } optionScope =>
                HasTransparentRepeatTarget(optionScope.Body),
            _ => false,
        };

        private static Node ReduceNestedQuantifier(Node atom, ParsedQuantifier outer)
        {
            if (atom is OptionScopeNode { TransparentRepeatTarget: true } optionScope &&
                HasTransparentRepeatTarget(optionScope.Body))
            {
                return new OptionScopeNode(
                    ReduceNestedQuantifier(optionScope.Body, outer),
                    optionScope.Enabled,
                    optionScope.Disabled,
                    transparentRepeatTarget: true);
            }

            if (atom is not RepeatNode inner || inner.PossessiveValue)
            {
                return new RepeatNode(
                    atom,
                    outer.Minimum,
                    outer.Maximum,
                    outer.Lazy,
                    possessive: false);
            }

            var innerType = GetPopularQuantifierType(
                inner.Minimum,
                inner.Maximum,
                inner.Lazy);
            var outerType = GetPopularQuantifierType(
                outer.Minimum,
                outer.Maximum,
                outer.Lazy);
            if (innerType < 0 || outerType < 0)
            {
                if (inner.Minimum == inner.Maximum &&
                    outer.Minimum == outer.Maximum)
                {
                    int product;
                    if (inner.Minimum == 0 || outer.Minimum == 0)
                    {
                        product = 0;
                    }
                    else if (outer.Minimum >= int.MaxValue / inner.Minimum)
                    {
                        throw new JqRuntimeException(
                            "Regex failure: too big number for repeat range");
                    }
                    else
                    {
                        product = outer.Minimum * inner.Minimum;
                    }

                    return new RepeatNode(
                        inner.Body,
                        product,
                        product,
                        outer.Lazy,
                        possessive: false);
                }

                var adjustedMaximum = outer.Maximum;
                if (innerType is 1 or 2 && outerType < 0 && !outer.Lazy &&
                    outer.Maximum > 1)
                {
                    adjustedMaximum = outer.Minimum == 0 ? 1 : outer.Minimum;
                }

                return new RepeatNode(
                    inner,
                    outer.Minimum,
                    adjustedMaximum,
                    outer.Lazy,
                    possessive: false);
            }

            var reduction = NestedQuantifierReductions[innerType, outerType];
            return reduction switch
            {
                NestedQuantifierReduction.DeleteOuter => inner,
                NestedQuantifierReduction.Star => new RepeatNode(
                    inner.Body, 0, -1, lazy: false, possessive: false),
                NestedQuantifierReduction.Plus => new RepeatNode(
                    inner.Body, 1, -1, lazy: false, possessive: false),
                NestedQuantifierReduction.LazyStar => new RepeatNode(
                    inner.Body, 0, -1, lazy: true, possessive: false),
                NestedQuantifierReduction.LazyQuestion => new RepeatNode(
                    inner.Body, 0, 1, lazy: true, possessive: false),
                NestedQuantifierReduction.PlusThenLazyQuestion => new RepeatNode(
                    new RepeatNode(inner.Body, 1, -1, lazy: false, possessive: false),
                    0,
                    1,
                    lazy: true,
                    possessive: false),
                _ => new RepeatNode(
                    inner,
                    outer.Minimum,
                    outer.Maximum,
                    outer.Lazy,
                    possessive: false),
            };
        }

        private static int GetPopularQuantifierType(int minimum, int maximum, bool lazy)
        {
            if (minimum == 0 && maximum == 1)
            {
                return lazy ? 3 : 0;
            }

            if (minimum == 0 && maximum == -1)
            {
                return lazy ? 4 : 1;
            }

            if (minimum == 1 && maximum == -1)
            {
                return lazy ? 5 : 2;
            }

            return -1;
        }

        private static readonly NestedQuantifierReduction[,] NestedQuantifierReductions =
        {
            {
                NestedQuantifierReduction.DeleteOuter,
                NestedQuantifierReduction.Star,
                NestedQuantifierReduction.Star,
                NestedQuantifierReduction.LazyQuestion,
                NestedQuantifierReduction.LazyStar,
                NestedQuantifierReduction.AsIs,
            },
            {
                NestedQuantifierReduction.DeleteOuter,
                NestedQuantifierReduction.DeleteOuter,
                NestedQuantifierReduction.DeleteOuter,
                NestedQuantifierReduction.PlusThenLazyQuestion,
                NestedQuantifierReduction.PlusThenLazyQuestion,
                NestedQuantifierReduction.DeleteOuter,
            },
            {
                NestedQuantifierReduction.Star,
                NestedQuantifierReduction.Star,
                NestedQuantifierReduction.DeleteOuter,
                NestedQuantifierReduction.AsIs,
                NestedQuantifierReduction.PlusThenLazyQuestion,
                NestedQuantifierReduction.DeleteOuter,
            },
            {
                NestedQuantifierReduction.DeleteOuter,
                NestedQuantifierReduction.LazyStar,
                NestedQuantifierReduction.LazyStar,
                NestedQuantifierReduction.DeleteOuter,
                NestedQuantifierReduction.LazyStar,
                NestedQuantifierReduction.LazyStar,
            },
            {
                NestedQuantifierReduction.DeleteOuter,
                NestedQuantifierReduction.DeleteOuter,
                NestedQuantifierReduction.DeleteOuter,
                NestedQuantifierReduction.DeleteOuter,
                NestedQuantifierReduction.DeleteOuter,
                NestedQuantifierReduction.DeleteOuter,
            },
            {
                NestedQuantifierReduction.AsIs,
                NestedQuantifierReduction.Star,
                NestedQuantifierReduction.Plus,
                NestedQuantifierReduction.LazyStar,
                NestedQuantifierReduction.LazyStar,
                NestedQuantifierReduction.DeleteOuter,
            },
        };

        private readonly record struct ParsedQuantifier(
            int Minimum,
            int Maximum,
            bool Lazy,
            bool Possessive);

        private enum RepeatTargetKind
        {
            Valid,
            Invalid,
            NotSpecified,
        }

        private enum NestedQuantifierReduction
        {
            AsIs,
            DeleteOuter,
            Star,
            Plus,
            LazyStar,
            LazyQuestion,
            PlusThenLazyQuestion,
        }

        private Node ParseAtom()
        {
            if (_index >= pattern.Length)
            {
                return EmptyNode.Instance;
            }

            var value = pattern[_index];
            if (char.IsHighSurrogate(value) && _index + 1 < pattern.Length &&
                char.IsLowSurrogate(pattern[_index + 1]))
            {
                var scalar = pattern.Substring(_index, 2);
                _index += 2;
                return new LiteralNode(scalar);
            }

            if (char.IsSurrogate(value))
            {
                throw Unsupported("supplementary-scalar pattern literal");
            }

            switch (value)
            {
                case '(':
                    return ParseGroupOrCallout();
                case '[':
                    return ParseCharacterClass();
                case '.':
                    _index++;
                    return new AnyNode();
                case '^':
                    _index++;
                    return new AnchorNode(AnchorKind.LineStart);
                case '$':
                    _index++;
                    return new AnchorNode(AnchorKind.LineEnd);
                case '\\':
                    return ParseEscape();
                case ')' or '|':
                    throw Unsupported("unbalanced group");
                default:
                    _index++;
                    return new LiteralNode(value);
            }
        }

        private Node ParseGroupOrCallout()
        {
            var lexicalOptions = _lexicalOptions;
            var extended = _extended;
            var preserveIsolatedOption = false;
            try
            {
                var node = ParseGroupOrCalloutCore();
                preserveIsolatedOption = node is OptionChangeNode;
                return node;
            }
            finally
            {
                if (!preserveIsolatedOption)
                {
                    _lexicalOptions = lexicalOptions;
                    _extended = extended;
                }
            }
        }

        private Node ParseGroupOrCalloutCore()
        {
            if (StartsWith("(?#", _index))
            {
                _index += 3;
                while (_index < pattern.Length && pattern[_index] != ')')
                {
                    if (pattern[_index] == '\\')
                    {
                        _index++;
                    }

                    _index++;
                }

                Expect(')');
                return new CommentNode();
            }

            if (StartsWith("(*", _index))
            {
                return ParseCallout(conditional: false);
            }

            if (StartsWith("(?{", _index))
            {
                return ParseContentCallout(conditional: false);
            }

            if (StartsWith("(?(?{", _index))
            {
                _index += 2;
                var callout = ParseContentCallout(conditional: true);
                if (_index < pattern.Length && pattern[_index] == ')')
                {
                    throw new JqRuntimeException("Regex failure: invalid if-else syntax");
                }

                var whenTrue = ParseSequence(')');
                Node whenFalse = EmptyNode.Instance;
                if (_index < pattern.Length && pattern[_index] == '|')
                {
                    _index++;
                    whenFalse = ParseAlternation(')');
                }

                Expect(')');
                return new ConditionalCalloutNode(callout, whenTrue, whenFalse);
            }

            if (StartsWith("(?(*", _index))
            {
                _index += 2;
                var callout = ParseCallout(conditional: true);
                var whenTrue = ParseSequence(')');
                Node whenFalse = EmptyNode.Instance;
                if (_index < pattern.Length && pattern[_index] == '|')
                {
                    _index++;
                    whenFalse = ParseAlternation(')');
                }

                Expect(')');
                return new ConditionalCalloutNode(callout, whenTrue, whenFalse);
            }

            _index++;
            if (_index < pattern.Length && pattern[_index] == '?')
            {
                _index++;
                if (_index >= pattern.Length)
                {
                    throw Unsupported("unfinished group");
                }

                if (TryParseOptionGroup(out var optionGroup))
                {
                    return optionGroup;
                }

                switch (pattern[_index])
                {
                    case ':':
                        _index++;
                        return ParsePlainGroup(capturing: false, name: null, lookaround: null, atomic: false);
                    case '=':
                        _index++;
                        return ParsePlainGroup(capturing: false, name: null, lookaround: true, atomic: false);
                    case '!':
                        _index++;
                        return ParsePlainGroup(capturing: false, name: null, lookaround: false, atomic: false);
                    case '>':
                        _index++;
                        return ParsePlainGroup(capturing: false, name: null, lookaround: null, atomic: true);
                    case '~':
                        _index++;
                        return ParseAbsent();
                    case '(':
                        return ParseCaptureConditional();
                    case '&':
                        return ParsePerlSubexpressionCall(named: true);
                    case >= '0' and <= '9':
                        return ParsePerlSubexpressionCall(named: false);
                    case '+' or '-' when _index + 1 < pattern.Length &&
                        char.IsAsciiDigit(pattern[_index + 1]):
                        return ParsePerlSubexpressionCall(named: false);
                    case '<':
                    {
                        if (_index + 1 < pattern.Length && pattern[_index + 1] is '=' or '!')
                        {
                            var positive = pattern[_index + 1] == '=';
                            _index += 2;
                            var body = ParseAlternation(')');
                            Expect(')');
                            var lookbehind = new LookbehindNode(body, positive);
                            _lookbehinds.Add(lookbehind);
                            return lookbehind;
                        }

                        var close = FindDelimitedNameEnd(
                            _index + 1,
                            '>',
                            "unfinished named group");
                        if (close < 0)
                        {
                            throw Unsupported("unfinished named group");
                        }

                        var name = pattern[(_index + 1)..close];
                        ValidateGroupName(name, allowTrailingInvalidCharacters: true);
                        _index = close + 1;
                        return ParsePlainGroup(capturing: true, name, lookaround: null, atomic: false);
                    }
                    case '\'':
                    {
                        var close = FindDelimitedNameEnd(
                            _index + 1,
                            '\'',
                            "unfinished named group");
                        if (close < 0)
                        {
                            throw Unsupported("unfinished named group");
                        }

                        var name = pattern[(_index + 1)..close];
                        ValidateGroupName(name, allowTrailingInvalidCharacters: true);
                        _index = close + 1;
                        return ParsePlainGroup(capturing: true, name, lookaround: null, atomic: false);
                    }
                    case 'R':
                        // Recursive (?R) is not enabled by jq's Perl-NG syntax.
                        // Its source tokenization exposes the close parenthesis as
                        // unmatched rather than reporting a generic option error.
                        throw new JqRuntimeException(
                            "Regex failure: unmatched close parenthesis");
                    default:
                        throw new JqRuntimeException("Regex failure: undefined group option");
                }
            }

            return ParsePlainGroup(capturing: true, name: null, lookaround: null, atomic: false);
        }

        private SubexpressionCallNode ParsePerlSubexpressionCall(bool named)
        {
            if (named)
            {
                _index++;
            }

            var close = pattern.IndexOf(')', _index);
            if (close < 0)
            {
                throw Unsupported("unfinished subexpression call");
            }

            var reference = pattern[_index..close];
            _index = close + 1;
            return CreateSubexpressionCall(reference, namedOnly: named);
        }

        private Node ParseAbsent()
        {
            if (_index < pattern.Length && pattern[_index] == '|')
            {
                _index++;
                if (_index < pattern.Length && pattern[_index] == ')')
                {
                    _index++;
                    return new AbsentRangeClearNode();
                }

                var forbidden = ParseSequence(')');
                if (_index < pattern.Length && pattern[_index] == '|')
                {
                    _index++;
                    var expression = ParseAlternation(')');
                    Expect(')');
                    return new AbsentExpressionNode(forbidden, expression);
                }

                Expect(')');
                return new AbsentStopperNode(forbidden);
            }

            var simpleForbidden = ParseAlternation(')');
            Expect(')');
            return new AbsentRangeNode(simpleForbidden);
        }

        private bool TryParseOptionGroup(out Node node)
        {
            node = EmptyNode.Instance;
            var start = _index;
            var enabled = RuntimeRegexOption.None;
            var disabled = RuntimeRegexOption.None;
            var disabling = false;
            var sawOptionSyntax = false;
            while (_index < pattern.Length)
            {
                if (pattern[_index] == '-')
                {
                    sawOptionSyntax = true;
                    disabling = true;
                    _index++;
                    continue;
                }

                var option = pattern[_index] switch
                {
                    'i' => RuntimeRegexOption.IgnoreCase,
                    'm' => RuntimeRegexOption.MultilineAnchors,
                    's' => RuntimeRegexOption.DotMatchesNewline,
                    'x' => RuntimeRegexOption.None,
                    _ => (RuntimeRegexOption)(-1),
                };
                if ((int)option == -1)
                {
                    break;
                }

                sawOptionSyntax = true;
                if (pattern[_index] != 'x')
                {
                    if (disabling)
                    {
                        disabled |= option;
                    }
                    else
                    {
                        enabled |= option;
                    }
                }

                _index++;
            }

            if (!sawOptionSyntax || _index >= pattern.Length || pattern[_index] is not (':' or ')'))
            {
                _index = start;
                return false;
            }

            var extendedWasEnabled = _extended;
            var optionText = pattern.AsSpan(start, _index - start);
            var minus = optionText.IndexOf('-');
            var enabledText = minus < 0 ? optionText : optionText[..minus];
            var disabledText = minus < 0 ? ReadOnlySpan<char>.Empty : optionText[(minus + 1)..];
            var extendedInScope =
                (enabledText.Contains('x') || extendedWasEnabled) &&
                !disabledText.Contains('x');

            if (pattern[_index] == ')')
            {
                _index++;
                _extended = extendedInScope;
                _lexicalOptions = ApplyLexicalOptions(
                    _lexicalOptions,
                    enabled,
                    disabled);
                node = new OptionChangeNode(enabled, disabled);
                return true;
            }

            _index++;
            _extended = extendedInScope;
            var lexicalOptions = _lexicalOptions;
            _lexicalOptions = ApplyLexicalOptions(lexicalOptions, enabled, disabled);
            try
            {
                var body = ParseAlternation(')');
                Expect(')');
                node = new OptionScopeNode(body, enabled, disabled);
                return true;
            }
            finally
            {
                _lexicalOptions = lexicalOptions;
                _extended = extendedWasEnabled;
            }
        }

        private static RuntimeRegexOption ApplyLexicalOptions(
            RuntimeRegexOption current,
            RuntimeRegexOption enabled,
            RuntimeRegexOption disabled) => (current | enabled) & ~disabled;

        private Node ParseCaptureConditional()
        {
            EnterParseDepth();
            try
            {
                return ParseCaptureConditionalCore();
            }
            finally
            {
                _parseDepth--;
            }
        }

        private Node ParseCaptureConditionalCore()
        {
            _index++;
            var conditionStart = _index;
            if (TryParseCaptureConditionReference(
                    out var groupNumber,
                    out var groupName,
                    out var nestLevel))
            {
                Expect(')');
                if (_index < pattern.Length && pattern[_index] == ')')
                {
                    _index++;
                    return new CaptureCheckNode(groupNumber, groupName, nestLevel);
                }

                var (whenTrue, whenFalse) = ParseConditionalBranches();
                return new CaptureConditionalNode(
                    groupNumber,
                    groupName,
                    nestLevel,
                    whenTrue,
                    whenFalse);
            }

            _index = conditionStart;
            var condition = ParseAlternation(')');
            Expect(')');
            if (_index < pattern.Length && pattern[_index] == ')')
            {
                throw new JqRuntimeException("Regex failure: invalid if-else syntax");
            }

            var (patternWhenTrue, patternWhenFalse) = ParseConditionalBranches();
            return new PatternConditionalNode(condition, patternWhenTrue, patternWhenFalse);
        }

        private (Node WhenTrue, Node WhenFalse) ParseConditionalBranches()
        {
            var whenTrue = ParseSequence(')');
            Node whenFalse = EmptyNode.Instance;
            if (_index < pattern.Length && pattern[_index] == '|')
            {
                _index++;
                whenFalse = ParseAlternation(')');
            }

            Expect(')');
            return (whenTrue, whenFalse);
        }

        private bool TryParseCaptureConditionReference(
            out int? groupNumber,
            out string? groupName,
            out int? nestLevel)
        {
            groupNumber = null;
            groupName = null;
            nestLevel = null;
            if (_index >= pattern.Length)
            {
                throw Unsupported("unfinished capture conditional");
            }

            string reference;
            var delimited = pattern[_index] is '<' or '\'';
            if (delimited)
            {
                reference = ParseDelimitedReference(
                    "capture conditional",
                    spanErrorToPatternEndOnParenthesis: true);
            }
            else if (pattern[_index] is '+' or '-' || char.IsAsciiDigit(pattern[_index]))
            {
                var close = pattern.IndexOf(')', _index);
                if (close < 0)
                {
                    throw Unsupported("unfinished capture conditional");
                }

                reference = pattern[_index..close];
                _index = close;
            }
            else
            {
                return false;
            }

            SplitBackreferenceLevel(reference, out reference, out nestLevel);
            if (TryResolveNumericReference(reference, out var number))
            {
                if (number <= 0)
                {
                    throw new JqRuntimeException("Regex failure: invalid backref number/name");
                }

                groupNumber = number;
            }
            else
            {
                // scan_env.c only records a numeric capture condition when the
                // parsed integer fits Oniguruma's signed node field. An
                // overflowing bare digit run is reparsed as a pattern
                // conditional instead of becoming a group name.
                if (!delimited && reference.All(char.IsAsciiDigit))
                {
                    return false;
                }

                ValidateGroupName(reference);
                groupName = reference;
            }

            return true;
        }

        private Node ParsePlainGroup(
            bool capturing,
            string? name,
            bool? lookaround,
            bool atomic)
        {
            var groupNumber = capturing ? ++_groupNumber : 0;
            var body = ParseAlternation(')');
            Expect(')');
            if (lookaround is bool positive)
            {
                return new LookaroundNode(body, positive);
            }

            if (atomic)
            {
                return new AtomicNode(body);
            }

            if (!capturing)
            {
                return body;
            }

            var group = new GroupNode(groupNumber, name, body);
            _groups.Add(group);
            if (name is not null)
            {
                if (!_namedGroups.TryGetValue(name, out var definitions))
                {
                    definitions = [];
                    _namedGroups.Add(name, definitions);
                }

                definitions.Add(group);
            }

            return group;
        }

        private CalloutNode ParseCallout(bool conditional)
        {
            _index += 2;
            var nameStart = _index;
            while (_index < pattern.Length && pattern[_index] is not (')' or '[' or '{'))
            {
                _index++;
            }

            if (_index >= pattern.Length)
            {
                throw new JqRuntimeException("Regex failure: end pattern in group");
            }

            var name = pattern[nameStart.._index];
            if (!OnigurumaCalloutArgumentParser.IsAllowedName(name))
            {
                throw new JqRuntimeException("Regex failure: invalid callout name");
            }

            string? tag = null;
            if (_index < pattern.Length && pattern[_index] == '[')
            {
                var close = pattern.IndexOf(']', _index + 1);
                if (close < 0)
                {
                    throw new JqRuntimeException("Regex failure: invalid callout tag name");
                }

                tag = pattern[(_index + 1)..close];
                if (!OnigurumaCalloutArgumentParser.IsAllowedTag(tag))
                {
                    throw new JqRuntimeException("Regex failure: invalid callout tag name");
                }

                _index = close + 1;
            }

            string[] arguments = [];
            bool[] argumentEscapes = [];
            if (_index < pattern.Length && pattern[_index] == '{')
            {
                OnigurumaCalloutArgumentParser.ParsedCalloutArguments parsed;
                try
                {
                    parsed = OnigurumaCalloutArgumentParser.Parse(pattern, _index);
                }
                catch (ArgumentException exception)
                {
                    throw new JqRuntimeException("Regex failure: " + exception.Message);
                }

                arguments = parsed.Arguments.Select(argument => argument.Value).ToArray();
                argumentEscapes = parsed.Arguments.Select(argument => argument.HadEscape).ToArray();
                _index = parsed.CloseBrace + 1;
            }

            Expect(')');
            var kind = name switch
            {
                "FAIL" => CalloutKind.Fail,
                "MISMATCH" => CalloutKind.Mismatch,
                "ERROR" => CalloutKind.Error,
                "SKIP" => CalloutKind.Skip,
                "COUNT" => CalloutKind.Count,
                "TOTAL_COUNT" => CalloutKind.TotalCount,
                "MAX" => CalloutKind.Max,
                "CMP" => CalloutKind.Compare,
                _ => throw new JqRuntimeException("Regex failure: undefined callout name"),
            };
            ValidateCalloutShape(kind, arguments, argumentEscapes);
            var callout = new CalloutNode(
                _callouts.Count + 1,
                kind,
                tag,
                arguments,
                conditional);
            _callouts.Add(callout);
            return callout;
        }

        private CalloutNode ParseContentCallout(bool conditional)
        {
            _ = OnigurumaContentCallout.TryParse(pattern, _index, out var parsed);
            _index = parsed.End + 1;
            var callout = new CalloutNode(
                _callouts.Count + 1,
                CalloutKind.Content,
                parsed.Tag,
                [parsed.Direction.ToString()],
                conditional);
            _callouts.Add(callout);
            return callout;
        }

        private Node ParseEscape()
        {
            var slashIndex = _index;
            _index++;
            if (_index >= pattern.Length)
            {
                throw Unsupported("trailing escape");
            }

            var escapedRune = Rune.GetRuneAt(pattern, _index);
            if (escapedRune.Utf16SequenceLength != 1)
            {
                _index += escapedRune.Utf16SequenceLength;
                _requiresSourceShapedExecution = true;
                return new LiteralNode(escapedRune.ToString());
            }

            var value = pattern[_index++];
            switch (value)
            {
                case 'A':
                    return new AnchorNode(AnchorKind.Start);
                case 'z':
                    return new AnchorNode(AnchorKind.End);
                case 'Z':
                    return new AnchorNode(AnchorKind.EndBeforeFinalNewline);
                case 'K':
                    return new ResetMatchStartNode();
                case 'G':
                    return new AnchorNode(AnchorKind.SearchStart);
                case 'g':
                    if (_index < pattern.Length && pattern[_index] is '<' or '\'')
                    {
                        return CreateSubexpressionCall(
                            ParseDelimitedReference("subexpression call"),
                            namedOnly: false);
                    }

                    _requiresSourceShapedExecution = true;
                    return new LiteralNode('g');
                case 'k':
                    if (_index < pattern.Length && pattern[_index] is '<' or '\'')
                    {
                        return CreateBackreference(ParseDelimitedReference(
                            "backreference",
                            spanErrorToPatternEndOnParenthesis: true));
                    }

                    _requiresSourceShapedExecution = true;
                    return new LiteralNode('k');
                case 'x':
                    if (_index < pattern.Length && pattern[_index] == '{')
                    {
                        return ParseBraceRadixEscape(16);
                    }

                    if (_index == pattern.Length)
                    {
                        return new LiteralNode('x');
                    }

                    return ParseOrdinaryCrudeByteRun(ReadFixedHexCrudeByte());
                case 'o':
                    return _index < pattern.Length && pattern[_index] == '{'
                        ? ParseBraceRadixEscape(8)
                        : new LiteralNode('o');
                case 'c':
                    _requiresSourceShapedExecution = true;
                    if (OnigurumaCalloutAtomMatcher.TryParseEscape(
                        pattern,
                        slashIndex,
                        out var control))
                    {
                        _index = control.NextPatternIndex;
                        return new OrdinaryAtomNode(control.Token);
                    }

                    throw new JqRuntimeException("Regex failure: end pattern at control");
                default:
                    if (char.IsAsciiDigit(value))
                    {
                        return ParseDecimalBackreferenceOrOctal(slashIndex);
                    }

                    if (OnigurumaCalloutAtomMatcher.TryParseEscape(
                        pattern,
                        slashIndex,
                        out var parsed))
                    {
                        _index = parsed.NextPatternIndex;
                        if (value is 'w' or 'W' or 'b' or 'B' ||
                            (value is 'p' or 'P' && IsWordPropertyEscape(slashIndex)))
                        {
                            _requiresSourceShapedExecution = true;
                        }

                        if ((char.IsAsciiLetterOrDigit(value) || value == '_' ||
                             escapedRune.Value >= 0x80) &&
                            parsed.Token.Kind == OnigurumaCalloutAtomKind.Literal)
                        {
                            _requiresSourceShapedExecution = true;
                        }

                        return new OrdinaryAtomNode(parsed.Token);
                    }

                    if (char.IsAsciiLetterOrDigit(value) || value == '_')
                    {
                        _requiresSourceShapedExecution = true;
                    }

                    return new LiteralNode(value);
            }
        }

        private Node ParseDecimalBackreferenceOrOctal(int slashIndex)
        {
            var digitStart = slashIndex + 1;
            var decimalEnd = digitStart;
            long decimalValue = 0;
            while (decimalEnd < pattern.Length && char.IsAsciiDigit(pattern[decimalEnd]))
            {
                decimalValue = Math.Min(
                    int.MaxValue + 1L,
                    decimalValue * 10 + pattern[decimalEnd] - '0');
                decimalEnd++;
            }

            // regparse.c reserves an initial zero for octal unconditionally.
            // ONIG_SYNTAX_PERL_NG gives a one-digit 1..9 escape backreference
            // precedence. Longer nonzero decimal text is a backreference only
            // when that group has already been declared at this lexical point.
            var digitCount = decimalEnd - digitStart;
            if (pattern[digitStart] != '0' &&
                (digitCount == 1 ||
                 decimalValue <= _groupNumber && decimalValue <= int.MaxValue))
            {
                _index = decimalEnd;
                return CreateNumericBackreference((int)decimalValue, nestLevel: null);
            }

            if (pattern[digitStart] is >= '0' and <= '7')
            {
                var octalEnd = digitStart;
                var octalValue = 0;
                while (octalEnd < pattern.Length && octalEnd < digitStart + 3 &&
                    pattern[octalEnd] is >= '0' and <= '7')
                {
                    octalValue = octalValue * 8 + pattern[octalEnd] - '0';
                    octalEnd++;
                }

                _index = octalEnd;
                return ParseOrdinaryCrudeByteRun(
                    new CrudeByteToken(unchecked((byte)octalValue), 8));
            }

            // 8/9 cannot start an octal value. Once the complete decimal number is not
            // a valid backreference, Oniguruma leaves the first digit as a literal and
            // parses the remaining digits normally.
            _index = digitStart + 1;
            return new LiteralNode(pattern[digitStart]);
        }

        private CrudeByteToken ReadFixedHexCrudeByte()
        {
            var value = 0;
            var digits = 0;
            while (_index < pattern.Length && digits < 2 &&
                TryHexDigit(pattern[_index], out var digit))
            {
                value = value * 16 + digit;
                digits++;
                _index++;
            }

            return new CrudeByteToken((byte)value, 16);
        }

        private Node ParseOrdinaryCrudeByteRun(CrudeByteToken first)
        {
            _requiresSourceShapedExecution = true;
            var expectedLength = CrudeByteCharacterLength(first.Value);
            var bytes = new byte[expectedLength];
            bytes[0] = first.Value;
            if (expectedLength == 1 && first.Value is >= 0x80 and <= 0xBF)
            {
                throw new JqRuntimeException("Regex failure: invalid code point value");
            }

            for (var index = 1; index < expectedLength; index++)
            {
                if (!TryReadFollowingCrudeByte(out var following))
                {
                    throw new JqRuntimeException(
                        "Regex failure: too short multibyte code string");
                }

                bytes[index] = following.Value;
            }

            if (expectedLength > 1 && bytes.Skip(1).Any(value => value is < 0x80 or > 0xBF))
            {
                throw new JqRuntimeException("Regex failure: invalid code point value");
            }

            var decodedScalar = DecodeCrudeBytes(bytes);
            if (Rune.IsValid(decodedScalar))
            {
                var decoded = char.ConvertFromUtf32(decodedScalar);
                if (Encoding.UTF8.GetBytes(decoded).AsSpan().SequenceEqual(bytes))
                {
                    return new LiteralNode(decoded);
                }
            }

            return new CrudeByteLiteralNode(bytes, decodedScalar);
        }

        private bool TryReadFollowingCrudeByte(out CrudeByteToken token)
        {
            token = default;
            SkipLexicalTrivia();
            if (_index + 1 >= pattern.Length || pattern[_index] != '\\')
            {
                return false;
            }

            var slash = _index;
            var escaped = pattern[_index + 1];
            if (escaped == 'x' && _index + 2 < pattern.Length &&
                pattern[_index + 2] != '{')
            {
                _index += 2;
                token = ReadFixedHexCrudeByte();
                return true;
            }

            if (escaped is < '0' or > '7')
            {
                return false;
            }

            var digitStart = slash + 1;
            var decimalEnd = digitStart;
            long decimalValue = 0;
            while (decimalEnd < pattern.Length && char.IsAsciiDigit(pattern[decimalEnd]))
            {
                decimalValue = Math.Min(
                    int.MaxValue + 1L,
                    decimalValue * 10 + pattern[decimalEnd] - '0');
                decimalEnd++;
            }

            var digitCount = decimalEnd - digitStart;
            if (pattern[digitStart] != '0' &&
                (digitCount == 1 ||
                 decimalValue <= _groupNumber && decimalValue <= int.MaxValue))
            {
                return false;
            }

            var octalEnd = digitStart;
            var octalValue = 0;
            while (octalEnd < pattern.Length && octalEnd < digitStart + 3 &&
                pattern[octalEnd] is >= '0' and <= '7')
            {
                octalValue = octalValue * 8 + pattern[octalEnd] - '0';
                octalEnd++;
            }

            _index = octalEnd;
            token = new CrudeByteToken(unchecked((byte)octalValue), 8);
            return true;
        }

        private void SkipLexicalTrivia()
        {
            while (true)
            {
                SkipExtendedTrivia();
                if (!StartsWith("(?#", _index))
                {
                    return;
                }

                _index += 3;
                while (_index < pattern.Length && pattern[_index] != ')')
                {
                    if (pattern[_index] == '\\' && _index + 1 < pattern.Length)
                    {
                        _index++;
                    }

                    _index++;
                }

                Expect(')');
            }
        }

        private static int CrudeByteCharacterLength(byte value) => value switch
        {
            >= 0xC0 and <= 0xDF => 2,
            >= 0xE0 and <= 0xEF => 3,
            >= 0xF0 and <= 0xF4 => 4,
            _ => 1,
        };

        private static int DecodeCrudeBytes(ReadOnlySpan<byte> bytes)
        {
            var value = bytes[0] & (bytes.Length switch
            {
                1 => 0xFF,
                2 => 0x1F,
                3 => 0x0F,
                _ => 0x07,
            });
            for (var index = 1; index < bytes.Length; index++)
            {
                value = value << 6 | bytes[index] & 0x3F;
            }

            return value;
        }

        private static bool TryHexDigit(char value, out int digit)
        {
            digit = value switch
            {
                >= '0' and <= '9' => value - '0',
                >= 'a' and <= 'f' => value - 'a' + 10,
                >= 'A' and <= 'F' => value - 'A' + 10,
                _ => -1,
            };
            return digit >= 0;
        }

        private readonly record struct CrudeByteToken(byte Value, int Radix);

        private Node ParseBraceRadixEscape(int radix)
        {
            var parsed = OnigurumaRadixEscape.ParseBrace(
                pattern,
                _index,
                radix,
                inCharacterClass: false);
            if (!parsed.Matched)
            {
                return new LiteralNode(radix == 16 ? 'x' : 'o');
            }

            _index = parsed.End + 1;
            var nodes = new List<Node>(parsed.Ranges.Count);
            foreach (var range in parsed.Ranges)
            {
                if (range.Start != range.End || range.Start > 0x10_FFFF ||
                    range.Start is >= 0xD800 and <= 0xDFFF)
                {
                    nodes.Add(new ImpossibleNode());
                    continue;
                }

                nodes.Add(new LiteralNode(char.ConvertFromUtf32((int)range.Start)));
            }

            return nodes.Count switch
            {
                0 => EmptyNode.Instance,
                1 => nodes[0],
                _ => new SequenceNode(nodes),
            };
        }

        private string ParseDelimitedReference(
            string construct,
            bool spanErrorToPatternEndOnParenthesis = false)
        {
            if (_index >= pattern.Length || pattern[_index] is not ('<' or '\''))
            {
                throw Unsupported(construct);
            }

            var closing = pattern[_index] == '<' ? '>' : '\'';
            var close = FindDelimitedNameEnd(
                _index + 1,
                closing,
                "unfinished " + construct,
                spanErrorToPatternEndOnParenthesis);
            if (close < 0)
            {
                throw Unsupported("unfinished " + construct);
            }

            var reference = pattern[(_index + 1)..close];
            _index = close + 1;
            return reference;
        }

        private int FindDelimitedNameEnd(
            int contentStart,
            char closing,
            string unfinishedDetail,
            bool spanErrorToPatternEndOnParenthesis = false)
        {
            var close = pattern.IndexOf(closing, contentStart);
            var parenthesis = pattern.IndexOf(')', contentStart);
            if (parenthesis >= 0 && (close < 0 || parenthesis < close))
            {
                throw new JqRuntimeException(
                    "Regex failure: invalid group name <" +
                    (spanErrorToPatternEndOnParenthesis
                        ? pattern[contentStart..]
                        : pattern[contentStart..parenthesis]) + ">");
            }

            if (close < 0)
            {
                throw Unsupported(unfinishedDetail);
            }

            return close;
        }

        private Node CreateBackreference(string reference)
        {
            SplitBackreferenceLevel(reference, out reference, out var nestLevel);
            if (TryResolveNumericReference(reference, out var number))
            {
                if (number <= 0)
                {
                    throw new JqRuntimeException("Regex failure: invalid backref number/name");
                }

                return CreateNumericBackreference(number, nestLevel);
            }

            ValidateGroupName(reference);
            var backreference = new BackreferenceNode(reference, nestLevel);
            _namedBackreferences.Add(backreference);
            return backreference;
        }

        private NumericBackreferenceNode CreateNumericBackreference(
            int number,
            int? nestLevel)
        {
            var reference = new NumericBackreferenceNode(number, nestLevel);
            _numericBackreferences.Add(reference);
            return reference;
        }

        private static void SplitBackreferenceLevel(
            string value,
            out string reference,
            out int? nestLevel)
        {
            reference = value;
            nestLevel = null;
            if (value.Length == 0)
            {
                throw new JqRuntimeException("Regex failure: group name is empty");
            }

            var suffix = value.AsSpan(1).IndexOfAny('+', '-');
            if (suffix < 0)
            {
                return;
            }

            suffix++;
            if (!int.TryParse(
                    value.AsSpan(suffix),
                    NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture,
                    out var parsedLevel))
            {
                throw new JqRuntimeException("Regex failure: invalid group name");
            }

            reference = value[..suffix];
            nestLevel = parsedLevel;
        }

        private SubexpressionCallNode CreateSubexpressionCall(
            string reference,
            bool namedOnly)
        {
            SubexpressionCallNode call;
            if (!namedOnly && TryResolveNumericReference(reference, out var number))
            {
                if (number < 0)
                {
                    throw new JqRuntimeException(
                        "Regex failure: undefined group <" + reference + "> reference");
                }

                call = new SubexpressionCallNode(number, null, reference);
            }
            else
            {
                ValidateGroupName(
                    reference,
                    allowTrailingInvalidCharacters: true,
                    allowNumericPrefixTransition: !namedOnly);
                call = new SubexpressionCallNode(null, reference, reference);
            }

            return RegisterSubexpressionCall(call);
        }

        private SubexpressionCallNode RegisterSubexpressionCall(SubexpressionCallNode call)
        {
            _subexpressionCalls.Add(call);
            return call;
        }

        private bool TryResolveNumericReference(string reference, out int number)
        {
            number = 0;
            if (!int.TryParse(
                    reference,
                    NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture,
                    out var parsed))
            {
                return false;
            }

            if (reference[0] == '+')
            {
                if (parsed == 0 || parsed > int.MaxValue - _groupNumber)
                {
                    throw new JqRuntimeException("Regex failure: invalid group name");
                }

                number = _groupNumber + parsed;
                return true;
            }

            if (reference[0] == '-')
            {
                if (parsed == 0)
                {
                    throw new JqRuntimeException("Regex failure: invalid group name");
                }

                number = _groupNumber + 1 + parsed;
                return true;
            }

            number = parsed;
            return true;
        }

        private CharacterClassNode ParseCharacterClass()
        {
            // prs_cc() increments env->parse_depth for the duration of a class.
            VerifyChildParseDepth();
            var classEnd = JqRegex.FindPerlNgCharacterClassEnd(pattern, _index);
            _index++;
            var negate = false;
            if (_index < pattern.Length && pattern[_index] == '^')
            {
                negate = true;
                _index++;
            }

            var members = new List<OnigurumaCalloutAtomToken>();
            var rawSingleBytes = new HashSet<byte>();
            var hasMultiByteComponent = false;
            var first = true;
            while (_index < classEnd && (pattern[_index] != ']' || first))
            {
                var low = ParseCharacterClassMember();
                if (low.SingleScalar is int lowScalar &&
                    _index + 1 < pattern.Length && pattern[_index] == '-' &&
                    pattern[_index + 1] != ']')
                {
                    _index++;
                    var high = ParseCharacterClassMember();
                    if (high.SingleScalar is not int highScalar)
                    {
                        throw new JqRuntimeException(
                            "Regex failure: char-class value at end of range");
                    }

                    if (highScalar < lowScalar)
                    {
                        throw new JqRuntimeException("Regex failure: empty range in char class");
                    }

                    if (low.Storage == CharacterClassStorage.SingleByte &&
                        high.Storage == CharacterClassStorage.SingleByte)
                    {
                        for (var raw = lowScalar; raw <= highScalar; raw++)
                        {
                            rawSingleBytes.Add((byte)raw);
                        }

                        if (lowScalar <= 0x7F)
                        {
                            members.Add(OnigurumaCalloutAtomToken.ForProperty(
                                [lowScalar, Math.Min(highScalar, 0x7F)],
                                negated: false));
                        }
                    }
                    else
                    {
                        hasMultiByteComponent = true;
                        members.Add(OnigurumaCalloutAtomToken.ForProperty(
                            [lowScalar, highScalar],
                            negated: false));
                    }
                }
                else
                {
                    members.AddRange(low.Tokens);
                    hasMultiByteComponent |= low.HasMultiByteComponent;
                    if (low.Storage == CharacterClassStorage.SingleByte &&
                        low.SingleScalar is int raw)
                    {
                        rawSingleBytes.Add((byte)raw);
                    }
                }

                first = false;
            }

            _index = classEnd + 1;
            return new CharacterClassNode(
                members,
                negate,
                rawSingleBytes,
                hasMultiByteComponent);
        }

        private CharacterClassMember ParseCharacterClassMember()
        {
            if (_index >= pattern.Length)
            {
                throw Unsupported("unfinished character class");
            }

            if (pattern[_index] == '[' &&
                OnigurumaCalloutAtomMatcher.TryParsePosixMember(
                    pattern,
                    _index,
                    out var posix))
            {
                if (IsWordPosixMember(_index, posix.NextPatternIndex))
                {
                    _requiresSourceShapedExecution = true;
                }

                _index = posix.NextPatternIndex;
                return new CharacterClassMember([posix.Token], null);
            }

            var memberStart = _index;
            var value = pattern[_index++];
            if (value != '\\')
            {
                return ScalarClassMember(value, memberStart);
            }

            if (_index >= pattern.Length)
            {
                throw Unsupported("unfinished character-class escape");
            }

            var escapedRune = Rune.GetRuneAt(pattern, _index);
            if (escapedRune.Utf16SequenceLength != 1)
            {
                _index += escapedRune.Utf16SequenceLength;
                _requiresSourceShapedExecution = true;
                return new CharacterClassMember(
                    [OnigurumaCalloutAtomToken.ForLiteral(escapedRune.ToString())],
                    escapedRune.Value,
                    CharacterClassStorage.CodePoint);
            }

            value = pattern[_index];
            if (value is >= '0' and <= '7')
            {
                // Port of regparse.c:fetch_token_in_cc(): ONIG_SYN_OP_ESC_OCTAL3
                // consumes one to three octal digits as a crude byte inside a
                // character class. Unlike the ordinary-token path, a one-digit
                // spelling never has backreference precedence here.
                var octalEnd = _index;
                var octalValue = 0;
                while (octalEnd < pattern.Length && octalEnd < _index + 3 &&
                    pattern[octalEnd] is >= '0' and <= '7')
                {
                    octalValue = octalValue * 8 + pattern[octalEnd] - '0';
                    octalEnd++;
                }

                if (octalValue >= 256)
                {
                    throw new JqRuntimeException("Regex failure: too big number");
                }

                _index = octalEnd;
                return ParseCharacterClassCrudeByteRun(
                    new CrudeByteToken((byte)octalValue, 8));
            }

            if (value is 'x' or 'o' && _index + 1 < pattern.Length &&
                pattern[_index + 1] == '{')
            {
                var parsed = OnigurumaRadixEscape.ParseBrace(
                    pattern,
                    _index + 1,
                    value == 'x' ? 16 : 8,
                    inCharacterClass: true);
                if (parsed.Matched)
                {
                    _index = parsed.End + 1;
                    var tokens = parsed.Ranges.Select(range =>
                        OnigurumaCalloutAtomToken.ForProperty(
                            [checked((int)range.Start), checked((int)range.End)],
                            negated: false)).ToArray();
                    var scalar = parsed.Ranges.Count == 1 &&
                        parsed.Ranges[0].Start == parsed.Ranges[0].End &&
                        parsed.Ranges[0].Start <= 0x10ffff &&
                        Rune.IsValid((int)parsed.Ranges[0].Start)
                        ? (int?)parsed.Ranges[0].Start
                        : null;
                    return new CharacterClassMember(
                        tokens,
                        scalar,
                        scalar is >= 0 and <= 0x7F
                            ? CharacterClassStorage.SingleByte
                            : CharacterClassStorage.CodePoint);
                }

                // fetch_token_in_cc() restores to the brace when a brace-radix
                // escape contains no initial digit. The ineffective escape is
                // then the literal x/o and the brace remains class content.
                _index++;
                return new CharacterClassMember(
                    [OnigurumaCalloutAtomToken.ForLiteral(value.ToString())],
                    value,
                    CharacterClassStorage.SingleByte);
            }

            if (value == 'x')
            {
                _index++;
                return ParseCharacterClassCrudeByteRun(ReadFixedHexCrudeByte());
            }

            if (value is 'b' or 'n' or 'r' or 't' or 'f' or 'a' or 'e' or
                '\\' or ']' or '-' or '^')
            {
                _index++;
                var literal = value switch
                {
                    'b' => '\b',
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    'f' => '\f',
                    'a' => '\a',
                    'e' => '\u001b',
                    _ => value,
                };
                return new CharacterClassMember(
                    [OnigurumaCalloutAtomToken.ForLiteral(literal.ToString())],
                    literal,
                    CharacterClassStorage.SingleByte);
            }

            if (value == 'c')
            {
                _requiresSourceShapedExecution = true;
            }

            if (value is 'w' or 'W' ||
                (value is 'p' or 'P' && IsWordPropertyEscape(memberStart)))
            {
                _requiresSourceShapedExecution = true;
            }

            if (value is ('c' or 'd' or 'D' or 'w' or 'W' or 's' or 'S' or 'p' or 'P') &&
                OnigurumaCalloutAtomMatcher.TryParseEscape(
                pattern,
                memberStart,
                out var escaped,
                inCharacterClass: true))
            {
                _index = escaped.NextPatternIndex;
                var scalar = escaped.Token.Kind == OnigurumaCalloutAtomKind.Literal &&
                    escaped.Token.Literal is { } literal &&
                    Rune.GetRuneAt(literal, 0).Utf16SequenceLength == literal.Length
                    ? Rune.GetRuneAt(literal, 0).Value
                    : (int?)null;
                return new CharacterClassMember([escaped.Token], scalar);
            }

            // fetch_escaped_value() owns class context: ordinary anchors,
            // backreference/call introducers, super-dot and text-segment escapes
            // are all ineffective here and denote their escaped character.
            if (char.IsAsciiLetterOrDigit(value) || value == '_' || value >= 0x80)
            {
                _requiresSourceShapedExecution = true;
            }

            _index++;
            return new CharacterClassMember(
                [OnigurumaCalloutAtomToken.ForLiteral(value.ToString())],
                value,
                value <= 0x7F
                    ? CharacterClassStorage.SingleByte
                    : CharacterClassStorage.CodePoint);
        }

        private CharacterClassMember ParseCharacterClassCrudeByteRun(CrudeByteToken first)
        {
            _requiresSourceShapedExecution = true;
            var expectedLength = CrudeByteCharacterLength(first.Value);
            if (expectedLength == 1)
            {
                return new CharacterClassMember(
                    first.Value <= 0x7F
                        ? [OnigurumaCalloutAtomToken.ForLiteral(((char)first.Value).ToString())]
                        : [OnigurumaCalloutAtomToken.ForKind(
                            OnigurumaCalloutAtomKind.Impossible)],
                    first.Value,
                    CharacterClassStorage.SingleByte);
            }

            var bytes = new byte[expectedLength];
            bytes[0] = first.Value;
            for (var index = 1; index < expectedLength; index++)
            {
                if (!TryReadFollowingCharacterClassCrudeByte(first.Radix, out var following))
                {
                    throw new JqRuntimeException(
                        "Regex failure: too short multibyte code string");
                }

                bytes[index] = following.Value;
            }

            if (bytes.Skip(1).Any(value => value is < 0x80 or > 0xBF))
            {
                throw new JqRuntimeException("Regex failure: invalid code point value");
            }

            var scalar = DecodeCrudeBytes(bytes);
            var token = scalar >= 0x80 && Rune.IsValid(scalar)
                ? OnigurumaCalloutAtomToken.ForProperty([scalar, scalar], negated: false)
                : OnigurumaCalloutAtomToken.ForKind(OnigurumaCalloutAtomKind.Impossible);
            return new CharacterClassMember(
                [token],
                scalar,
                CharacterClassStorage.MultiByte);
        }

        private bool TryReadFollowingCharacterClassCrudeByte(
            int requiredRadix,
            out CrudeByteToken token)
        {
            token = default;
            if (_index + 1 >= pattern.Length || pattern[_index] != '\\')
            {
                return false;
            }

            var escaped = pattern[_index + 1];
            if (escaped == 'x' && _index + 2 < pattern.Length &&
                pattern[_index + 2] != '{')
            {
                _index += 2;
                var parsed = ReadFixedHexCrudeByte();
                if (requiredRadix != parsed.Radix)
                {
                    return false;
                }

                token = parsed;
                return true;
            }

            if (escaped is < '0' or > '7')
            {
                return false;
            }

            var octalEnd = _index + 1;
            var octalValue = 0;
            while (octalEnd < pattern.Length && octalEnd < _index + 4 &&
                pattern[octalEnd] is >= '0' and <= '7')
            {
                octalValue = octalValue * 8 + pattern[octalEnd] - '0';
                octalEnd++;
            }

            if (octalValue >= 256)
            {
                throw new JqRuntimeException("Regex failure: too big number");
            }

            _index = octalEnd;
            if (requiredRadix != 8)
            {
                return false;
            }

            token = new CrudeByteToken((byte)octalValue, 8);
            return true;
        }

        private CharacterClassMember ScalarClassMember(char value, int start)
        {
            if (char.IsHighSurrogate(value) && _index < pattern.Length &&
                char.IsLowSurrogate(pattern[_index]))
            {
                var scalar = char.ConvertToUtf32(value, pattern[_index++]);
                return new CharacterClassMember(
                    [OnigurumaCalloutAtomToken.ForLiteral(char.ConvertFromUtf32(scalar))],
                    scalar,
                    CharacterClassStorage.CodePoint);
            }

            if (char.IsSurrogate(value))
            {
                throw Unsupported("supplementary-scalar character-class literal at " + start);
            }

            return new CharacterClassMember(
                [OnigurumaCalloutAtomToken.ForLiteral(value.ToString())],
                value,
                value <= 0x7F
                    ? CharacterClassStorage.SingleByte
                    : CharacterClassStorage.CodePoint);
        }

        private sealed record CharacterClassMember(
            IReadOnlyList<OnigurumaCalloutAtomToken> Tokens,
            int? SingleScalar,
            CharacterClassStorage Storage = CharacterClassStorage.CodePoint)
        {
            internal bool HasMultiByteComponent =>
                Storage == CharacterClassStorage.MultiByte ||
                Tokens.Any(token => token.HasMultiByteClassComponent);
        }

        private enum CharacterClassStorage
        {
            SingleByte,
            MultiByte,
            CodePoint,
        }

        private readonly record struct LookbehindCharacterWidthRange(
            int Minimum,
            int? Maximum,
            bool MinimumIsSure);

        private void Expect(char expected)
        {
            if (_index >= pattern.Length || pattern[_index] != expected)
            {
                throw Unsupported("expected '" + expected + "'");
            }

            _index++;
        }

        private bool StartsWith(string value, int index) =>
            pattern.AsSpan(index).StartsWith(value, StringComparison.Ordinal);

        private static void ValidateCalloutShape(
            CalloutKind kind,
            string[] arguments,
            bool[] argumentEscapes)
        {
            static bool IsLong(string value) =>
                OnigurumaCalloutArgumentParser.TryParseLong(value, out _);
            static bool IsTag(string value, bool escaped) =>
                !escaped && OnigurumaCalloutArgumentParser.IsAllowedTag(value);

            if (kind == CalloutKind.Max && arguments.Length >= 1 &&
                !IsLong(arguments[0]) && !IsTag(arguments[0], argumentEscapes[0]))
            {
                throw new JqRuntimeException("Regex failure: invalid callout tag name");
            }

            if (kind == CalloutKind.Compare && arguments.Length == 3)
            {
                foreach (var operandIndex in new[] { 0, 2 })
                {
                    if (!IsLong(arguments[operandIndex]) &&
                        !IsTag(arguments[operandIndex], argumentEscapes[operandIndex]))
                    {
                        throw new JqRuntimeException("Regex failure: invalid callout tag name");
                    }
                }
            }

            var valid = kind switch
            {
                CalloutKind.Fail or CalloutKind.Mismatch or CalloutKind.Skip => arguments.Length == 0,
                CalloutKind.Error => arguments.Length <= 1 &&
                    (arguments.Length == 0 || IsLong(arguments[0])),
                CalloutKind.Count or CalloutKind.TotalCount => arguments.Length <= 1 &&
                    (arguments.Length == 0 ||
                     OnigurumaCalloutArgumentParser.IsSingleCharacter(arguments[0])),
                CalloutKind.Max => arguments.Length is 1 or 2 &&
                    (arguments.Length == 1 ||
                     OnigurumaCalloutArgumentParser.IsSingleCharacter(arguments[1])),
                CalloutKind.Compare => arguments.Length == 3 && arguments[1].Length != 0,
                _ => false,
            };
            if (!valid)
            {
                throw new JqRuntimeException("Regex failure: invalid callout arg");
            }
        }

        private static bool IsEventSensitive(CalloutNode callout)
        {
            if (callout.Conditional && callout.Kind == CalloutKind.Content)
            {
                return true;
            }

            if (callout.Conditional && callout.Kind is CalloutKind.Max or CalloutKind.Compare)
            {
                return true;
            }

            return callout.Kind switch
            {
                CalloutKind.Mismatch or CalloutKind.Error or CalloutKind.TotalCount or
                    CalloutKind.Skip => true,
                CalloutKind.Count => true,
                CalloutKind.Max => true,
                CalloutKind.Compare => callout.Tag is not null || callout.Arguments.Any(argument =>
                    OnigurumaCalloutArgumentParser.TryParseLong(argument, out var literal) &&
                    (literal > 512 || literal < -512)),
                _ => false,
            };
        }

        private static Node ReduceLookbehindQuantifiers(Node node)
        {
            if (node is AlternationNode alternation)
            {
                return new AlternationNode(
                    alternation.Alternatives.Select(ReduceLookbehindBranch).ToArray());
            }

            return ReduceLookbehindBranch(node);
        }

        private static Node ReduceLookbehindBranch(Node node)
        {
            if (node is OptionScopeNode { TransparentRepeatTarget: true } optionScope)
            {
                return new OptionScopeNode(
                    ReduceLookbehindBranch(optionScope.Body),
                    optionScope.Enabled,
                    optionScope.Disabled,
                    transparentRepeatTarget: true);
            }

            if (node is AbsentRangeNode)
            {
                // tune_look_behind reduces the default absent engine to its certain
                // zero-width form before bytecode generation.
                return EmptyNode.Instance;
            }

            if (node is RepeatNode repeat && IsReducibleLookbehindRepeat(repeat))
            {
                return repeat.Minimum == 0
                    ? EmptyNode.Instance
                    : new RepeatNode(
                        repeat.Body,
                        repeat.Minimum,
                        repeat.Minimum,
                        repeat.Lazy,
                        repeat.PossessiveValue);
            }

            if (node is not SequenceNode sequence)
            {
                return node;
            }

            var reduced = new List<Node>(sequence.Nodes.Count);
            var reducingPrefix = true;
            foreach (var child in sequence.Nodes)
            {
                if (reducingPrefix &&
                    TryGetTransparentLookbehindRepeat(child, out var childRepeat))
                {
                    if (childRepeat.Minimum == 0)
                    {
                        continue;
                    }

                    reduced.Add(ReplaceTransparentRepeat(
                        child,
                        new RepeatNode(
                            childRepeat.Body,
                            childRepeat.Minimum,
                            childRepeat.Minimum,
                            childRepeat.Lazy,
                            childRepeat.PossessiveValue)));
                    reducingPrefix = false;
                    continue;
                }

                reducingPrefix = false;
                reduced.Add(child);
            }

            return reduced.Count switch
            {
                0 => EmptyNode.Instance,
                1 => reduced[0],
                _ => new SequenceNode(reduced),
            };
        }

        private static bool IsReducibleLookbehindRepeat(RepeatNode repeat) => repeat.Body is
            LiteralNode or OrdinaryAtomNode or AnyNode or CharacterClassNode or
            BackreferenceNode or NumericBackreferenceNode;

        private static bool TryGetTransparentLookbehindRepeat(
            Node node,
            out RepeatNode repeat)
        {
            while (node is OptionScopeNode { TransparentRepeatTarget: true } optionScope)
            {
                node = optionScope.Body;
            }

            repeat = node as RepeatNode ?? null!;
            return repeat is not null && IsReducibleLookbehindRepeat(repeat);
        }

        private static Node ReplaceTransparentRepeat(Node node, Node replacement) => node switch
        {
            OptionScopeNode { TransparentRepeatTarget: true } optionScope =>
                new OptionScopeNode(
                    ReplaceTransparentRepeat(optionScope.Body, replacement),
                    optionScope.Enabled,
                    optionScope.Disabled,
                    transparentRepeatTarget: true),
            RepeatNode => replacement,
            _ => throw new InvalidOperationException("Expected a transparent repeat target."),
        };

        private bool TryGetLookbehindCharacterWidthRange(
            Node node,
            HashSet<Node> resolving,
            out LookbehindCharacterWidthRange range)
        {
            range = default;
            if (!resolving.Add(node))
            {
                return false;
            }

            try
            {
                switch (node)
                {
                    case EmptyNode or CalloutNode or OptionChangeNode:
                        range = new LookbehindCharacterWidthRange(0, 0, true);
                        return true;
                    case AnchorNode or LookaroundNode or LookbehindNode or CaptureCheckNode:
                        range = new LookbehindCharacterWidthRange(0, 0, false);
                        return true;
                    case ResetMatchStartNode:
                        range = new LookbehindCharacterWidthRange(0, 0, true);
                        return true;
                    case GroupNode group:
                        if (!TryGetLookbehindCharacterWidthRange(
                                group.Body,
                                resolving,
                                out var groupRange))
                        {
                            return false;
                        }

                        range = groupRange with { MinimumIsSure = false };
                        return true;
                    case AtomicNode atomic:
                        return TryGetLookbehindCharacterWidthRange(
                            atomic.Body,
                            resolving,
                            out range);
                    case OptionScopeNode optionScope:
                        return TryGetLookbehindCharacterWidthRange(
                            optionScope.Body,
                            resolving,
                            out range);
                    case SequenceNode sequence:
                    {
                        var minimum = 0;
                        int? maximum = 0;
                        var minimumIsSure = true;
                        foreach (var child in sequence.Nodes)
                        {
                            if (!TryGetLookbehindCharacterWidthRange(
                                    child,
                                    resolving,
                                    out var childRange))
                            {
                                return false;
                            }

                            minimum = AddLookbehindWidth(minimum, childRange.Minimum);
                            maximum = AddLookbehindMaximum(maximum, childRange.Maximum);
                            minimumIsSure &= childRange.MinimumIsSure;
                        }

                        range = new LookbehindCharacterWidthRange(
                            minimum,
                            maximum,
                            minimumIsSure);
                        return true;
                    }
                    case AlternationNode alternation:
                    {
                        LookbehindCharacterWidthRange? merged = null;
                        foreach (var alternative in alternation.Alternatives)
                        {
                            if (!TryGetLookbehindCharacterWidthRange(
                                    alternative,
                                    new HashSet<Node>(resolving),
                                    out var alternativeRange))
                            {
                                return false;
                            }

                            merged = merged is null
                                ? alternativeRange
                                : MergeLookbehindAlternatives(merged.Value, alternativeRange);
                        }

                        range = merged ?? new LookbehindCharacterWidthRange(0, 0, true);
                        return true;
                    }
                    case RepeatNode repeat:
                        if (!TryGetLookbehindCharacterWidthRange(
                                repeat.Body,
                                resolving,
                                out var bodyRange))
                        {
                            return false;
                        }

                        range = new LookbehindCharacterWidthRange(
                            MultiplyLookbehindWidth(bodyRange.Minimum, repeat.Minimum),
                            MultiplyLookbehindMaximum(bodyRange.Maximum, repeat.Maximum),
                            bodyRange.MinimumIsSure);
                        return true;
                    case AbsentRangeNode:
                        // make_absent_tree()'s default expression is the optimized
                        // one-character repeat \O*. Its synthetic fail arm gives
                        // node_char_len() a certain zero minimum and infinite max.
                        range = new LookbehindCharacterWidthRange(0, null, true);
                        return true;
                    case AbsentExpressionNode absent:
                        if (TryGetOptimizedAbsentExpressionRange(absent.Expression, out range))
                        {
                            return true;
                        }

                        if (!TryGetLookbehindCharacterWidthRange(
                                absent.Expression,
                                resolving,
                                out var expressionRange))
                        {
                            return false;
                        }

                        // The generic absent engine scans with \O* before the
                        // explicit expression, so its maximum is infinite while
                        // the expression supplies the certain minimum.
                        range = new LookbehindCharacterWidthRange(
                            expressionRange.Minimum,
                            null,
                            expressionRange.MinimumIsSure);
                        return true;
                    case AbsentStopperNode or AbsentRangeClearNode:
                        // check_node_in_look_behind() rejects absent gimmicks that
                        // carry UPDATE_VAR_* side effects.
                        return false;
                    case CaptureConditionalNode conditional:
                        return TryMergeConditionalLookbehindRanges(
                            conditional.WhenTrue,
                            conditional.WhenFalse,
                            resolving,
                            conditionMinimumIsSure: false,
                            out range);
                    case PatternConditionalNode conditional:
                        if (!TryGetLookbehindCharacterWidthRange(
                                conditional.Condition,
                                new HashSet<Node>(resolving),
                                out var conditionRange) ||
                            !TryGetLookbehindCharacterWidthRange(
                                conditional.WhenTrue,
                                new HashSet<Node>(resolving),
                                out var trueRange) ||
                            !TryGetLookbehindCharacterWidthRange(
                                conditional.WhenFalse,
                                new HashSet<Node>(resolving),
                                out var falseRange))
                        {
                            return false;
                        }

                        range = MergeLookbehindAlternatives(
                            AddLookbehindRanges(conditionRange, trueRange),
                            falseRange);
                        return true;
                    case ConditionalCalloutNode conditional:
                        return TryMergeConditionalLookbehindRanges(
                            conditional.WhenTrue,
                            conditional.WhenFalse,
                            resolving,
                            conditionMinimumIsSure: true,
                            out range);
                    default:
                        if (!TryGetFixedCharacterWidth(node, out var fixedWidth))
                        {
                            return false;
                        }

                        range = new LookbehindCharacterWidthRange(
                            fixedWidth,
                            fixedWidth,
                            true);
                        return true;
                }
            }
            finally
            {
                resolving.Remove(node);
            }
        }

        private bool TryMergeConditionalLookbehindRanges(
            Node whenTrue,
            Node whenFalse,
            HashSet<Node> resolving,
            bool conditionMinimumIsSure,
            out LookbehindCharacterWidthRange range)
        {
            range = default;
            if (!TryGetLookbehindCharacterWidthRange(
                    whenTrue,
                    new HashSet<Node>(resolving),
                    out var trueRange) ||
                !TryGetLookbehindCharacterWidthRange(
                    whenFalse,
                    new HashSet<Node>(resolving),
                    out var falseRange))
            {
                return false;
            }

            trueRange = trueRange with
            {
                MinimumIsSure = trueRange.MinimumIsSure && conditionMinimumIsSure,
            };
            range = MergeLookbehindAlternatives(trueRange, falseRange);
            return true;
        }

        private static bool TryGetOptimizedAbsentExpressionRange(
            Node expression,
            out LookbehindCharacterWidthRange range)
        {
            range = default;
            while (expression is OptionScopeNode { TransparentRepeatTarget: true } optionScope)
            {
                expression = optionScope.Body;
            }

            if (expression is not RepeatNode { Lazy: false } repeat ||
                !IsSimpleOneCharacterAbsentRepeatBody(repeat.Body))
            {
                return false;
            }

            range = new LookbehindCharacterWidthRange(
                0,
                repeat.Maximum < 0 ? null : Math.Min(65_536, repeat.Maximum),
                true);
            return true;
        }

        private static bool IsSimpleOneCharacterAbsentRepeatBody(Node node) => node switch
        {
            LiteralNode literal => literal.Value.EnumerateRunes().Count() == 1,
            CharacterClassNode => true,
            OptionScopeNode { TransparentRepeatTarget: true } optionScope =>
                IsSimpleOneCharacterAbsentRepeatBody(optionScope.Body),
            _ => false,
        };

        private static LookbehindCharacterWidthRange AddLookbehindRanges(
            LookbehindCharacterWidthRange left,
            LookbehindCharacterWidthRange right) =>
            new(
                AddLookbehindWidth(left.Minimum, right.Minimum),
                AddLookbehindMaximum(left.Maximum, right.Maximum),
                left.MinimumIsSure && right.MinimumIsSure);

        private static LookbehindCharacterWidthRange MergeLookbehindAlternatives(
            LookbehindCharacterWidthRange left,
            LookbehindCharacterWidthRange right)
        {
            var minimum = Math.Min(left.Minimum, right.Minimum);
            var minimumIsSure = left.Minimum == right.Minimum
                ? left.MinimumIsSure || right.MinimumIsSure
                : left.Minimum < right.Minimum
                    ? left.MinimumIsSure
                    : right.MinimumIsSure;
            int? maximum = left.Maximum is null || right.Maximum is null
                ? null
                : Math.Max(left.Maximum.Value, right.Maximum.Value);
            return new LookbehindCharacterWidthRange(minimum, maximum, minimumIsSure);
        }

        private static int AddLookbehindWidth(int left, int right) =>
            left > 65_536 - right ? 65_536 : left + right;

        private static int? AddLookbehindMaximum(int? left, int? right) =>
            left is null || right is null
                ? null
                : AddLookbehindWidth(left.Value, right.Value);

        private static int MultiplyLookbehindWidth(int width, int count) =>
            width != 0 && count > 65_536 / width ? 65_536 : width * count;

        private static int? MultiplyLookbehindMaximum(int? width, int count)
        {
            if (count == 0 || width == 0)
            {
                return 0;
            }

            if (count < 0 || width is null)
            {
                return null;
            }

            return MultiplyLookbehindWidth(width.Value, count);
        }

        private static bool ContainsLookbehindWidthDependency(Node node) => node switch
        {
            ResetMatchStartNode or BackreferenceNode or NumericBackreferenceNode or
                SubexpressionCallNode => true,
            SequenceNode sequence => sequence.Nodes.Any(ContainsLookbehindWidthDependency),
            AlternationNode alternation => alternation.Alternatives.Any(
                ContainsLookbehindWidthDependency),
            GroupNode group => ContainsLookbehindWidthDependency(group.Body),
            AtomicNode atomic => ContainsLookbehindWidthDependency(atomic.Body),
            RepeatNode repeat => ContainsLookbehindWidthDependency(repeat.Body),
            LookaroundNode lookaround => ContainsLookbehindWidthDependency(lookaround.Body),
            LookbehindNode lookbehind => ContainsLookbehindWidthDependency(lookbehind.Body),
            OptionScopeNode optionScope => ContainsLookbehindWidthDependency(optionScope.Body),
            CaptureConditionalNode conditional =>
                ContainsLookbehindWidthDependency(conditional.WhenTrue) ||
                ContainsLookbehindWidthDependency(conditional.WhenFalse),
            PatternConditionalNode conditional =>
                ContainsLookbehindWidthDependency(conditional.Condition) ||
                ContainsLookbehindWidthDependency(conditional.WhenTrue) ||
                ContainsLookbehindWidthDependency(conditional.WhenFalse),
            ConditionalCalloutNode conditional =>
                ContainsLookbehindWidthDependency(conditional.WhenTrue) ||
                ContainsLookbehindWidthDependency(conditional.WhenFalse),
            AbsentRangeNode absent => ContainsLookbehindWidthDependency(absent.Forbidden),
            AbsentExpressionNode absent =>
                ContainsLookbehindWidthDependency(absent.Forbidden) ||
                ContainsLookbehindWidthDependency(absent.Expression),
            AbsentStopperNode absent => ContainsLookbehindWidthDependency(absent.Forbidden),
            _ => false,
        };

        private bool TryGetFixedCharacterWidth(Node node, out int width) =>
            TryGetFixedCharacterWidth(node, new HashSet<Node>(), out width);

        private bool TryGetFixedCharacterWidth(
            Node node,
            HashSet<Node> resolving,
            out int width)
        {
            width = 0;
            if (!resolving.Add(node))
            {
                return false;
            }

            try
            {
            switch (node)
            {
                case EmptyNode or CalloutNode or AnchorNode or ResetMatchStartNode or
                    OptionChangeNode or LookaroundNode or LookbehindNode or CaptureCheckNode or
                    AbsentRangeNode:
                    return true;
                case LiteralNode literal:
                    width = literal.Value.EnumerateRunes().Count();
                    return true;
                case OrdinaryAtomNode ordinary:
                    width = ordinary.Token.Kind switch
                    {
                        OnigurumaCalloutAtomKind.WordBoundary or
                        OnigurumaCalloutAtomKind.NonWordBoundary or
                        OnigurumaCalloutAtomKind.TextBoundary or
                        OnigurumaCalloutAtomKind.TextNonBoundary => 0,
                        OnigurumaCalloutAtomKind.Literal =>
                            ordinary.Token.Literal!.EnumerateRunes().Count(),
                        OnigurumaCalloutAtomKind.Property or
                        OnigurumaCalloutAtomKind.NotNewline or
                        OnigurumaCalloutAtomKind.AnyScalar => 1,
                        _ => -1,
                    };
                    return width >= 0;
                case AnyNode or CharacterClassNode or CharacterPredicateNode:
                    width = 1;
                    return true;
                case GroupNode group:
                    return TryGetFixedCharacterWidth(group.Body, resolving, out width);
                case AtomicNode atomic:
                    return TryGetFixedCharacterWidth(atomic.Body, resolving, out width);
                case OptionScopeNode optionScope:
                    return TryGetFixedCharacterWidth(optionScope.Body, resolving, out width);
                case NumericBackreferenceNode numeric:
                {
                    var referenced = _groups.FirstOrDefault(group => group.Number == numeric.Number);
                    return referenced is not null &&
                        TryGetFixedCharacterWidth(referenced.Body, resolving, out width);
                }
                case BackreferenceNode named:
                {
                    if (!_namedGroups.TryGetValue(named.Name, out var definitions) ||
                        definitions.Count == 0)
                    {
                        return false;
                    }

                    int? namedWidth = null;
                    foreach (var definition in definitions)
                    {
                        if (!TryGetFixedCharacterWidth(
                                definition.Body,
                                new HashSet<Node>(resolving),
                                out var definitionWidth) ||
                            namedWidth is int expected && expected != definitionWidth)
                        {
                            width = 0;
                            return false;
                        }

                        namedWidth = definitionWidth;
                    }

                    width = namedWidth ?? 0;
                    return true;
                }
                case SubexpressionCallNode call when call.Target is { } target:
                    return TryGetFixedCharacterWidth(target.Body, resolving, out width);
                case SequenceNode sequence:
                    foreach (var child in sequence.Nodes)
                    {
                        if (!TryGetFixedCharacterWidth(child, resolving, out var childWidth) ||
                            width > 65_535 - childWidth)
                        {
                            width = 0;
                            return false;
                        }

                        width += childWidth;
                    }

                    return true;
                case AlternationNode alternation:
                    int? alternativeWidth = null;
                    foreach (var alternative in alternation.Alternatives)
                    {
                        if (!TryGetFixedCharacterWidth(
                                alternative,
                                new HashSet<Node>(resolving),
                                out var candidateWidth) ||
                            alternativeWidth is int expected && expected != candidateWidth)
                        {
                            width = 0;
                            return false;
                        }

                        alternativeWidth = candidateWidth;
                    }

                    width = alternativeWidth ?? 0;
                    return true;
                case RepeatNode repeat when repeat.Maximum == repeat.Minimum:
                    if (!TryGetFixedCharacterWidth(repeat.Body, resolving, out var repeatedWidth) ||
                        repeatedWidth > 0 && repeat.Minimum > 65_535 / repeatedWidth)
                    {
                        return false;
                    }

                    width = repeatedWidth * repeat.Minimum;
                    return true;
                case CaptureConditionalNode conditional:
                    return TryEqualBranchWidths(
                        conditional.WhenTrue,
                        conditional.WhenFalse,
                        resolving,
                        out width);
                case PatternConditionalNode conditional:
                    if (!TryGetFixedCharacterWidth(
                            conditional.Condition,
                            new HashSet<Node>(resolving),
                            out var conditionWidth) ||
                        !TryGetFixedCharacterWidth(
                            conditional.WhenTrue,
                            new HashSet<Node>(resolving),
                            out var trueWidth) ||
                        !TryGetFixedCharacterWidth(
                            conditional.WhenFalse,
                            new HashSet<Node>(resolving),
                            out var falseWidth) ||
                        conditionWidth > 65_535 - trueWidth ||
                        conditionWidth + trueWidth != falseWidth)
                    {
                        width = 0;
                        return false;
                    }

                    width = falseWidth;
                    return true;
                case ConditionalCalloutNode conditional:
                    return TryEqualBranchWidths(
                        conditional.WhenTrue,
                        conditional.WhenFalse,
                        resolving,
                        out width);
                default:
                    return false;
            }
            }
            finally
            {
                resolving.Remove(node);
            }
        }

        private bool TryEqualBranchWidths(
            Node left,
            Node right,
            HashSet<Node> resolving,
            out int width)
        {
            width = 0;
            if (!TryGetFixedCharacterWidth(left, new HashSet<Node>(resolving), out var leftWidth) ||
                !TryGetFixedCharacterWidth(right, new HashSet<Node>(resolving), out var rightWidth) ||
                leftWidth != rightWidth)
            {
                return false;
            }

            width = leftWidth;
            return true;
        }

        private static bool ContainsCapture(Node node) => node switch
        {
            GroupNode => true,
            SequenceNode sequence => sequence.Nodes.Any(ContainsCapture),
            AlternationNode alternation => alternation.Alternatives.Any(ContainsCapture),
            RepeatNode repeat => ContainsCapture(repeat.Body),
            AtomicNode atomic => ContainsCapture(atomic.Body),
            LookaroundNode lookaround => ContainsCapture(lookaround.Body),
            LookbehindNode lookbehind => ContainsCapture(lookbehind.Body),
            OptionScopeNode optionScope => ContainsCapture(optionScope.Body),
            CaptureConditionalNode => true,
            PatternConditionalNode conditional =>
                ContainsCapture(conditional.Condition) ||
                ContainsCapture(conditional.WhenTrue) ||
                ContainsCapture(conditional.WhenFalse),
            ConditionalCalloutNode conditional =>
                ContainsCapture(conditional.WhenTrue) || ContainsCapture(conditional.WhenFalse),
            AbsentRangeNode absent => ContainsCapture(absent.Forbidden),
            AbsentExpressionNode absent =>
                ContainsCapture(absent.Forbidden) || ContainsCapture(absent.Expression),
            AbsentStopperNode absent => ContainsCapture(absent.Forbidden),
            _ => false,
        };

        private void ValidateGroupName(
            string name,
            bool allowTrailingInvalidCharacters = false,
            bool allowNumericPrefixTransition = false)
        {
            if (name.Length == 0)
            {
                throw new JqRuntimeException("Regex failure: group name is empty");
            }

            var first = Rune.GetRuneAt(name, 0);
            if (char.IsAsciiDigit(name[0]) || name[0] is '+' or '-')
            {
                var prefixEnd = name[0] is '+' or '-' ? 1 : 0;
                while (prefixEnd < name.Length && char.IsAsciiDigit(name[prefixEnd]))
                {
                    prefixEnd++;
                }

                if (!allowNumericPrefixTransition || prefixEnd >= name.Length)
                {
                    throw new JqRuntimeException(
                        "Regex failure: invalid group name <" + name + ">");
                }

                // fetch_name(..., is_ref=TRUE) changes a numeric candidate to
                // a name on its first non-digit and then preserves the same
                // permissive late-character behavior as ordinary names.
                _requiresSourceShapedExecution = true;
                return;
            }

            if (!OnigurumaCalloutAtomMatcher.IsWordScalar(first.Value))
            {
                throw new JqRuntimeException(
                    "Regex failure: invalid char in group name <" + name + ">");
            }

            if (!allowTrailingInvalidCharacters)
            {
                foreach (var value in name.EnumerateRunes().Skip(1))
                {
                    if (!OnigurumaCalloutAtomMatcher.IsWordScalar(value.Value))
                    {
                        throw new JqRuntimeException(
                            "Regex failure: invalid char in group name <" + name + ">");
                    }
                }
            }

            // Preserve non-level fetch_name() source behavior: definitions and
            // subexpression calls record, but do not return, a later non-word
            // diagnostic. Backreferences and enclosed conditions stay strict.
            _requiresSourceShapedExecution = true;
        }

        private bool IsWordPropertyEscape(int slashIndex)
        {
            var brace = slashIndex + 2;
            if (brace >= pattern.Length || pattern[brace] != '{')
            {
                return false;
            }

            var close = pattern.IndexOf('}', brace + 1);
            if (close < 0)
            {
                return false;
            }

            var name = pattern.AsSpan(brace + 1, close - brace - 1);
            if (!name.IsEmpty && name[0] == '^')
            {
                name = name[1..];
            }

            var normalized = new char[name.Length];
            var length = 0;
            foreach (var value in name)
            {
                if (value > 0x7F)
                {
                    return false;
                }

                if (value is not (' ' or '_' or '-'))
                {
                    normalized[length++] = char.ToUpperInvariant(value);
                }
            }

            return normalized.AsSpan(0, length).SequenceEqual("WORD");
        }

        private bool IsWordPosixMember(int start, int end)
        {
            var name = pattern.AsSpan(start + 2, end - start - 4);
            if (!name.IsEmpty && name[0] == '^')
            {
                name = name[1..];
            }

            return name.SequenceEqual("word");
        }

        private static UnsupportedGrammarException Unsupported(string detail) =>
            new("callout event runner grammar: " + detail);
    }

    private sealed class CharacterPredicate(
        IReadOnlyList<(char Low, char High)>? ranges,
        bool negate,
        CharacterClassKind kind = CharacterClassKind.Ranges)
    {
        internal static readonly CharacterPredicate Digit = new(null, false, CharacterClassKind.Digit);
        internal static readonly CharacterPredicate NotDigit = new(null, true, CharacterClassKind.Digit);
        internal static readonly CharacterPredicate Word = new(null, false, CharacterClassKind.Word);
        internal static readonly CharacterPredicate NotWord = new(null, true, CharacterClassKind.Word);
        internal static readonly CharacterPredicate Space = new(null, false, CharacterClassKind.Space);
        internal static readonly CharacterPredicate NotSpace = new(null, true, CharacterClassKind.Space);

        internal bool Matches(string input, int index, bool ignoreCase)
        {
            var scalarValue = char.IsHighSurrogate(input[index]) && index + 1 < input.Length &&
                char.IsLowSurrogate(input[index + 1])
                ? char.ConvertToUtf32(input[index], input[index + 1])
                : input[index];
            var rune = new Rune(scalarValue);
            bool MatchesCore(int candidate)
            {
                var candidateRune = new Rune(candidate);
                return kind switch
                {
                    CharacterClassKind.Digit => Rune.IsDigit(candidateRune),
                    CharacterClassKind.Word => Rune.IsLetterOrDigit(candidateRune) || candidate == '_' ||
                        Rune.GetUnicodeCategory(candidateRune) is
                        UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or
                        UnicodeCategory.EnclosingMark or UnicodeCategory.ConnectorPunctuation,
                    CharacterClassKind.Space => Rune.IsWhiteSpace(candidateRune),
                    _ => ranges!.Any(range => candidate >= range.Low && candidate <= range.High),
                };
            }

            var result = MatchesCore(scalarValue);
            if (!result && ignoreCase && scalarValue <= 0x7f && char.IsAsciiLetter((char)scalarValue))
            {
                result = MatchesCore(char.ToLowerInvariant((char)scalarValue)) ||
                    MatchesCore(char.ToUpperInvariant((char)scalarValue));
            }

            return negate ? !result : result;
        }
    }

    private sealed class CalloutSlot(long value, int epoch)
    {
        internal long Value { get; set; } = value;

        internal int Epoch { get; set; } = epoch;
    }

    private sealed class CalloutEntry(bool success, Action? retraction)
    {
        internal static readonly CalloutEntry Failure = new(false, null);
        internal static readonly CalloutEntry SuccessWithoutRetraction = new(true, null);

        internal bool Success { get; } = success;

        internal void Retract() => retraction?.Invoke();
    }

    private sealed class CandidateMismatchException : Exception;

    private sealed class UnsupportedGrammarException(string message) : Exception(message);

    private sealed record CompiledPattern(
        Node Root,
        IReadOnlyDictionary<string, IReadOnlyList<GroupNode>> NamedGroups,
        IReadOnlyList<GroupNode> Groups,
        IReadOnlySet<GroupNode> CallableGroups,
        IReadOnlyDictionary<string, CalloutNode> CalloutsByTag,
        OnigurumaCalloutOptimizationPlan OptimizationPlan,
        bool EventSensitive,
        bool HasMismatch,
        bool UsesIgnoreCase,
        bool RequiresSourceShapedExecution);

    private sealed record Frame(
        int Position,
        Dictionary<int, CaptureSpan> Captures,
        Dictionary<(int GroupNumber, int CallLevel), CaptureSpan> CaptureLevels,
        int? ReportStart,
        RuntimeRegexOptions Options,
        int? RightRange,
        bool AbsentSideEffect = false);

    [Flags]
    private enum RuntimeRegexOption
    {
        None = 0,
        IgnoreCase = 1 << 0,
        DotMatchesNewline = 1 << 1,
        MultilineAnchors = 1 << 2,
    }

    private const RuntimeRegexOption AllRuntimeRegexOptions =
        RuntimeRegexOption.IgnoreCase |
        RuntimeRegexOption.DotMatchesNewline |
        RuntimeRegexOption.MultilineAnchors;

    private readonly record struct RuntimeRegexOptions(
        bool IgnoreCase,
        bool DotMatchesNewline,
        bool MultilineAnchors,
        bool InLookbehind)
    {
        internal RuntimeRegexOptions Apply(
            RuntimeRegexOption enabled,
            RuntimeRegexOption disabled)
        {
            var values = RuntimeRegexOption.None;
            if (IgnoreCase)
            {
                values |= RuntimeRegexOption.IgnoreCase;
            }

            if (DotMatchesNewline)
            {
                values |= RuntimeRegexOption.DotMatchesNewline;
            }

            if (MultilineAnchors)
            {
                values |= RuntimeRegexOption.MultilineAnchors;
            }

            // regparse.c applies the disabled option mask last. This matters for
            // accepted spellings such as (?i-i), where disable wins.
            values = (values | enabled) & ~disabled;
            return new RuntimeRegexOptions(
                (values & RuntimeRegexOption.IgnoreCase) != 0,
                (values & RuntimeRegexOption.DotMatchesNewline) != 0,
                (values & RuntimeRegexOption.MultilineAnchors) != 0,
                InLookbehind);
        }
    }

    private sealed record CaptureSpan(int Start, int End, string? Name);

    private readonly record struct RunnerFlags(
        bool Global,
        bool FindLongest,
        bool IgnoreEmpty,
        bool IgnoreCase,
        bool Extended,
        bool DotMatchesNewline,
        bool SingleLineAnchors);

    private enum CalloutKind
    {
        Content,
        Fail,
        Mismatch,
        Error,
        Skip,
        Count,
        TotalCount,
        Max,
        Compare,
    }

    private enum AnchorKind
    {
        Start,
        SearchStart,
        End,
        EndBeforeFinalNewline,
        LineStart,
        LineEnd,
    }

    private enum CharacterClassKind
    {
        Ranges,
        Digit,
        Word,
        Space,
    }

}
