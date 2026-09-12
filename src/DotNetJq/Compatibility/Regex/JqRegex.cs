// DOTNETJQ COMPATIBILITY PROXY
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Semantic sources: src/builtin.c (f_match), src/builtin.jq
// (match/test/capture/scan/splits/sub/gsub), and vendor/oniguruma/src/regparse.c/regcomp.c.
// UPSTREAM COMPONENT: jq's Oniguruma-backed match, test, capture, scan, split, sub, and gsub behavior.
// REPLACEMENT: System.Text.RegularExpressions plus source-shaped managed compatibility helpers
// and a bounded backtracking event runner behind this jq-shaped boundary.
// WHY: the managed library cannot expose or require a native Oniguruma runtime dependency.
// BEHAVIORAL CONTRACT: preserve jq result shapes, modifiers, captures, replacement streams, and scalar offsets.
// KNOWN DIFFERENCES: native Oniguruma API, bytecode, allocator, and engine identity are not reproduced;
// those are outside jq's public regex surface. No jq-visible compatibility exception is known in the
// scoped surface. jq installs no application callback, so content callouts (`(?{...})`) are source-ported
// as zero-width successes with their normal tag identity. Built-in callouts, recursive subexpression
// calls, keep/reset-start, absent ranges, text segments, pinned Unicode properties/case folds, and
// UTF-8-continuation look-behind restart behavior are implemented by the managed compatibility layer.
// Jq-visible offsets and lengths use Unicode scalar values; Perl-NG FIND_NOT_EMPTY, `\K`, and `l`
// ordering use the original UTF-8 consumed span.
// TESTS COVERING THE SUBSTITUTION: RegexCompatibilityTests, RegexCompatibilityRound2Tests,
// RegexCompatibilityRound3Tests, OnigurumaCalloutEventRunnerTests, RegexResidualFamilyBTests,
// the exhaustive regex differential corpus, and the unchanged upstream onig.test/manonig.test fixtures.

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using DotNetJq.Port;
using DotNetRegex = System.Text.RegularExpressions.Regex;
using DotNetRegexMatch = System.Text.RegularExpressions.Match;
using DotNetRegexOptions = System.Text.RegularExpressions.RegexOptions;
using DotNetRegexTimeoutException = System.Text.RegularExpressions.RegexMatchTimeoutException;

[assembly: InternalsVisibleTo("DotNetJq.Tests")]

namespace DotNetJq.Compatibility.Regex;

internal sealed record JqRegexCapture(
    int Offset,
    int Length,
    string? String,
    string? Name);

internal sealed record JqRegexMatch(
    int Offset,
    int Length,
    string String,
    IReadOnlyList<JqRegexCapture> Captures);

internal sealed record JqRegexScanResult(
    string? String,
    IReadOnlyList<string?>? Captures)
{
    internal bool IsCaptureArray => Captures is not null;
}

internal static class JqRegex
{
    private static readonly TimeSpan DefaultTimeout = System.Threading.Timeout.InfiniteTimeSpan;
    private const int MinimumCompiledClusterInputLength = 16_384;
    private static readonly Lazy<string> ScalarLetterPattern = new(
        () => BuildScalarCategoryPattern(
            "\\p{L}",
            category => category is
                System.Globalization.UnicodeCategory.UppercaseLetter or
                System.Globalization.UnicodeCategory.LowercaseLetter or
                System.Globalization.UnicodeCategory.TitlecaseLetter or
                System.Globalization.UnicodeCategory.ModifierLetter or
                System.Globalization.UnicodeCategory.OtherLetter));
    private static readonly Lazy<int[]> CharacterClassWordRanges = new(() =>
        OnigurumaUnicodePropertyData.TryDecodeRanges("WORD", out var ranges)
            ? ranges
            : throw new InvalidOperationException("Pinned Oniguruma WORD data is missing."));
    private static readonly Lazy<string> CharacterClassWordPattern = new(() =>
        BuildScalarRangePattern(CharacterClassWordRanges.Value));
    private static readonly Lazy<string> ScalarWordPattern = new(() =>
        "(?:" + CharacterClassWordPattern.Value +
        "|[\\u00B2\\u00B3\\u00B9\\u00BC-\\u00BE])");
    private static readonly Lazy<string> BmpRuntimeWordPattern = new(() =>
        "(?:" + BuildScalarRangePattern(
            ClipScalarRanges(CharacterClassWordRanges.Value, 0, char.MaxValue)) +
        "|[\\u00B2\\u00B3\\u00B9\\u00BC-\\u00BE])");
    private static readonly Lazy<string> ScalarAlphabeticPattern = new(
        () => BuildScalarCategoryPattern(
            "[\\p{L}\\p{Nl}]",
            category => category is
                System.Globalization.UnicodeCategory.UppercaseLetter or
                System.Globalization.UnicodeCategory.LowercaseLetter or
                System.Globalization.UnicodeCategory.TitlecaseLetter or
                System.Globalization.UnicodeCategory.ModifierLetter or
                System.Globalization.UnicodeCategory.OtherLetter or
                System.Globalization.UnicodeCategory.LetterNumber));
    private static readonly Lazy<string> ScalarDigitPattern = new(
        () => BuildScalarCategoryPattern(
            "\\d",
            category => category == System.Globalization.UnicodeCategory.DecimalDigitNumber));
    private static readonly Lazy<string> ScalarNumberPattern = new(
        () => BuildScalarCategoryPattern(
            "\\p{N}",
            category => category is
                System.Globalization.UnicodeCategory.DecimalDigitNumber or
                System.Globalization.UnicodeCategory.LetterNumber or
                System.Globalization.UnicodeCategory.OtherNumber));
    private static readonly Lazy<string> ScalarMarkPattern = new(
        () => BuildScalarCategoryPattern(
            "\\p{M}",
            category => category is
                System.Globalization.UnicodeCategory.NonSpacingMark or
                System.Globalization.UnicodeCategory.SpacingCombiningMark or
                System.Globalization.UnicodeCategory.EnclosingMark));
    private static readonly Lazy<string> ScalarSeparatorPattern = new(
        () => BuildScalarCategoryPattern(
            "\\p{Z}",
            category => category is
                System.Globalization.UnicodeCategory.SpaceSeparator or
                System.Globalization.UnicodeCategory.LineSeparator or
                System.Globalization.UnicodeCategory.ParagraphSeparator));
    private static readonly Lazy<string> ScalarPunctuationPattern = new(
        () => BuildScalarCategoryPattern(
            "\\p{P}",
            category => category is
                System.Globalization.UnicodeCategory.ConnectorPunctuation or
                System.Globalization.UnicodeCategory.DashPunctuation or
                System.Globalization.UnicodeCategory.OpenPunctuation or
                System.Globalization.UnicodeCategory.ClosePunctuation or
                System.Globalization.UnicodeCategory.InitialQuotePunctuation or
                System.Globalization.UnicodeCategory.FinalQuotePunctuation or
                System.Globalization.UnicodeCategory.OtherPunctuation));
    private static readonly Lazy<string> ScalarSymbolPattern = new(
        () => BuildScalarCategoryPattern(
            "\\p{S}",
            category => category is
                System.Globalization.UnicodeCategory.MathSymbol or
                System.Globalization.UnicodeCategory.CurrencySymbol or
                System.Globalization.UnicodeCategory.ModifierSymbol or
                System.Globalization.UnicodeCategory.OtherSymbol));
    private static readonly Lazy<string> SupplementaryWordPattern = new(() =>
        BuildScalarRangePattern(
            ClipScalarRanges(CharacterClassWordRanges.Value, 0x10000, 0x10FFFF)));
    private static readonly Lazy<string> ScalarWordBoundaryPattern = new(BuildScalarWordBoundaryPattern);
    private static readonly Lazy<string> EmojiPropertyPattern = new(
        () => BuildScalarRangePattern(OnigurumaUnicodePropertyData.Emoji));
    private static readonly Lazy<string> EmojiComponentPropertyPattern = new(
        () => BuildScalarRangePattern(OnigurumaUnicodePropertyData.Emoji_Component));
    private static readonly Lazy<string> EmojiModifierPropertyPattern = new(
        () => BuildScalarRangePattern(OnigurumaUnicodePropertyData.Emoji_Modifier));
    private static readonly Lazy<string> EmojiModifierBasePropertyPattern = new(
        () => BuildScalarRangePattern(OnigurumaUnicodePropertyData.Emoji_Modifier_Base));
    private static readonly Lazy<string> EmojiPresentationPropertyPattern = new(
        () => BuildScalarRangePattern(OnigurumaUnicodePropertyData.Emoji_Presentation));
    private static readonly Lazy<string> ExtendedPictographicPropertyPattern = new(
        () => BuildScalarRangePattern(OnigurumaUnicodePropertyData.Extended_Pictographic));
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string>
        UnicodePropertyPatternCache = new(StringComparer.Ordinal);
    private static IReadOnlyList<(int Source, int Target)> SupplementarySimpleCaseFoldMappings =>
        OnigurumaCaseFold.SupplementarySimpleMappings;
    private static IReadOnlyList<OnigurumaFullCaseFoldMapping> FullCaseFoldMappings =>
        OnigurumaCaseFold.FullMappings;
    private const string IgnoreEmptyCaptureName = "dotnetjq_ignore_empty_remaining";
    private const string KeepCaptureName = "dotnetjq_keep_match_start";
    private const string SubcallCapturePrefix = "dotnetjq_subcall_";
    private const string RegexConditionCapturePrefix = "dotnetjq_regex_condition_";
    private const string AbsentTailCapturePrefix = "dotnetjq_absent_tail_";
    private const string CalloutCapturePrefix = "dotnetjq_callout_";
    private const string CalloutMarkerPrefix = "(?#dotnetjq_callout_marker_";
    private const string CalloutControlCaptureName = "dotnetjq_callout_control";
    private const int MaximumLookBehindWidth = 65_535;
    private const int MaximumSubexpressionCallNesting = 20;

    internal static bool Test(
        string input,
        string pattern,
        string? modifiers = null,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (TryRunCalloutEvents(
                input,
                pattern,
                modifiers,
                timeout,
                forceGlobal: false,
                out var calloutMatches))
        {
            return calloutMatches.Count != 0;
        }

        var execution = CreateExecution(input, pattern, modifiers, timeout, forceGlobal: false);

        try
        {
            return FindRawMatches(input, execution, stopAfterFirst: true).Count != 0;
        }
        catch (DotNetRegexTimeoutException)
        {
            throw RegexTimeout();
        }
    }

    internal static IReadOnlyList<JqRegexMatch> Match(
        string input,
        string pattern,
        string? modifiers = null,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (TryRunCalloutEvents(
                input,
                pattern,
                modifiers,
                timeout,
                forceGlobal: false,
                out var calloutMatches))
        {
            return ConvertCalloutEventMatches(input, calloutMatches);
        }

        var execution = CreateExecution(input, pattern, modifiers, timeout, forceGlobal: false);
        return Match(input, execution);
    }

    internal static IReadOnlyList<IReadOnlyDictionary<string, string?>> Capture(
        string input,
        string pattern,
        string? modifiers = null,
        TimeSpan? timeout = null)
    {
        var matches = Match(input, pattern, modifiers, timeout);
        var results = new IReadOnlyDictionary<string, string?>[matches.Count];

        for (var matchIndex = 0; matchIndex < matches.Count; matchIndex++)
        {
            results[matchIndex] = NamedCaptures(matches[matchIndex]);
        }

        return results;
    }

    internal static IReadOnlyList<JqRegexScanResult> Scan(
        string input,
        string pattern,
        string? modifiers = null,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        JqRegexMatch[] matches;
        if (TryRunCalloutEvents(
                input,
                pattern,
                modifiers,
                timeout,
                forceGlobal: true,
                out var calloutMatches))
        {
            matches = ConvertCalloutEventMatches(input, calloutMatches);
        }
        else
        {
            var execution = CreateExecution(input, pattern, modifiers, timeout, forceGlobal: true);
            matches = Match(input, execution);
        }

        var results = new JqRegexScanResult[matches.Length];

        for (var matchIndex = 0; matchIndex < matches.Length; matchIndex++)
        {
            var match = matches[matchIndex];
            results[matchIndex] = match.Captures.Count == 0
                ? new JqRegexScanResult(match.String, null)
                : new JqRegexScanResult(null, match.Captures.Select(capture => capture.String).ToArray());
        }

        return results;
    }

    internal static IReadOnlyList<string> Split(
        string input,
        string pattern,
        string? modifiers = null,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (TryRunCalloutEvents(
                input,
                pattern,
                modifiers,
                timeout,
                forceGlobal: true,
                out var calloutMatches))
        {
            var calloutResults = new List<string>(calloutMatches.Count + 1);
            var inputBytes = Encoding.UTF8.GetBytes(input);
            var calloutPrevious = 0;
            foreach (var match in calloutMatches)
            {
                calloutResults.Add(Encoding.UTF8.GetString(
                    inputBytes,
                    calloutPrevious,
                    match.ByteIndex - calloutPrevious));
                calloutPrevious = match.ByteIndex + match.ByteLength;
            }

            calloutResults.Add(Encoding.UTF8.GetString(
                inputBytes,
                calloutPrevious,
                inputBytes.Length - calloutPrevious));
            return calloutResults;
        }

        var execution = CreateExecution(input, pattern, modifiers, timeout, forceGlobal: true);

        try
        {
            var matches = FindRawMatches(input, execution, stopAfterFirst: false);
            var results = new List<string>(matches.Count + 1);
            var previous = 0;

            foreach (var match in matches)
            {
                var effectiveIndex = EffectiveIndex(match);
                results.Add(input[previous..effectiveIndex]);
                previous = effectiveIndex + EffectiveLength(match);
            }

            results.Add(input[previous..]);
            return results;
        }
        catch (DotNetRegexTimeoutException)
        {
            throw RegexTimeout();
        }
    }

    internal static string Sub(
        string input,
        string pattern,
        string replacement,
        string? modifiers = null,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        return Sub(input, pattern, _ => replacement, modifiers, timeout);
    }

    internal static string Sub(
        string input,
        string pattern,
        Func<IReadOnlyDictionary<string, string?>, string> replacement,
        string? modifiers = null,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(replacement);
        if (TryRunCalloutEvents(
                input,
                pattern,
                modifiers,
                timeout,
                forceGlobal: false,
                out var calloutMatches))
        {
            return ReplaceCalloutEventMatches(input, calloutMatches, replacement);
        }

        var execution = CreateExecution(input, pattern, modifiers, timeout, forceGlobal: false);
        return Replace(input, execution, replacement);
    }

    internal static string Gsub(
        string input,
        string pattern,
        string replacement,
        string? modifiers = null,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        return Gsub(input, pattern, _ => replacement, modifiers, timeout);
    }

    internal static string Gsub(
        string input,
        string pattern,
        Func<IReadOnlyDictionary<string, string?>, string> replacement,
        string? modifiers = null,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(replacement);
        if (TryRunCalloutEvents(
                input,
                pattern,
                modifiers,
                timeout,
                forceGlobal: true,
                out var calloutMatches))
        {
            return ReplaceCalloutEventMatches(input, calloutMatches, replacement);
        }

        var execution = CreateExecution(input, pattern, modifiers, timeout, forceGlobal: true);
        return Replace(input, execution, replacement);
    }

    internal static jv TestAsJv(
        string input,
        string pattern,
        string? modifiers = null,
        TimeSpan? timeout = null) =>
        libjq.jv_bool(Test(input, pattern, modifiers, timeout));

    internal static jv MatchAsJv(
        string input,
        string pattern,
        string? modifiers = null,
        TimeSpan? timeout = null) =>
        libjq.jv_array(Match(input, pattern, modifiers, timeout).Select(ToJv));

    internal static jv ToJv(JqRegexMatch match)
    {
        ArgumentNullException.ThrowIfNull(match);
        var result = libjq.jv_object();
        result = libjq.jv_object_set(result, "offset", libjq.jv_number(match.Offset));
        result = libjq.jv_object_set(result, "length", libjq.jv_number(match.Length));
        result = libjq.jv_object_set(result, "string", libjq.jv_string(match.String));
        result = libjq.jv_object_set(result, "captures", libjq.jv_array(match.Captures.Select(ToJv)));
        return result;
    }

    internal static jv ToJv(JqRegexCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        var result = libjq.jv_object();
        result = libjq.jv_object_set(result, "offset", libjq.jv_number(capture.Offset));
        // jq-1.8.2 src/builtin.c:f_match inserts zero-length capture fields as
        // offset, string, length, while non-empty captures use offset, length,
        // string. jv objects preserve insertion order, so this source branch is
        // observable in compact CLI output and must remain explicit here.
        if (capture.Length == 0)
        {
            result = libjq.jv_object_set(
                result,
                "string",
                capture.String is null ? libjq.jv_null() : libjq.jv_string(capture.String));
            result = libjq.jv_object_set(result, "length", libjq.jv_number(0));
        }
        else
        {
            result = libjq.jv_object_set(result, "length", libjq.jv_number(capture.Length));
            result = libjq.jv_object_set(
                result,
                "string",
                capture.String is null ? libjq.jv_null() : libjq.jv_string(capture.String));
        }
        result = libjq.jv_object_set(
            result,
            "name",
            capture.Name is null ? libjq.jv_null() : libjq.jv_string(capture.Name));
        return result;
    }

    internal static jv ToJv(JqRegexScanResult scan)
    {
        ArgumentNullException.ThrowIfNull(scan);
        return scan.Captures is null
            ? libjq.jv_string(scan.String ?? string.Empty)
            : libjq.jv_array(scan.Captures.Select(value =>
                value is null ? libjq.jv_null() : libjq.jv_string(value)));
    }

    private static JqRegexMatch[] Match(string input, RegexExecution execution)
    {
        try
        {
            var rawMatches = FindRawMatches(input, execution, stopAfterFirst: !execution.Global);
            var matches = new JqRegexMatch[rawMatches.Count];

            for (var matchIndex = 0; matchIndex < rawMatches.Count; matchIndex++)
            {
                matches[matchIndex] = ConvertMatch(input, rawMatches[matchIndex], execution);
            }

            return matches;
        }
        catch (DotNetRegexTimeoutException)
        {
            throw RegexTimeout();
        }
    }

    private static bool TryRunCalloutEvents(
        string input,
        string pattern,
        string? modifiers,
        TimeSpan? timeout,
        bool forceGlobal,
        out IReadOnlyList<OnigurumaCalloutEventMatch> matches)
    {
        // Keep patterns whose callouts have no observable non-transactional event history on
        // the broader System.Text.RegularExpressions compatibility path. Besides avoiding an
        // unnecessary grammar restriction, this is what lets ordinary lookbehind/backreference
        // constructs coexist with COUNT values which are never read.
        var initialFlags = ParseModifiers(modifiers);
        var initialExtendedMode = initialFlags.Extended;
        var requiresDelimitedNameRunner = ContainsPotentialDelimitedNameSyntax(pattern);
        var requiresSearchStateRunner = !requiresDelimitedNameRunner &&
            ContainsEffectiveSearchStateEscape(pattern, initialExtendedMode);
        var requiresSourceShapedReferenceRunner = requiresDelimitedNameRunner ||
            ContainsSourceShapedReference(pattern, initialExtendedMode);
        var requiresAbsentLookbehindRunner = !requiresDelimitedNameRunner &&
            ContainsAbsentLookbehind(pattern, initialExtendedMode);
        var requiresPerlNgOptionRunner = ContainsPerlNgOnlyOptionSyntax(pattern);
        var requiresIgnoreCaseRunner = initialFlags.IgnoreCase ||
            ContainsInlineIgnoreCaseOption(pattern);
        var requiresUtf8ByteGlobalRunner =
            (forceGlobal || modifiers?.Contains('g', StringComparison.Ordinal) == true) &&
            Encoding.UTF8.GetByteCount(input) != input.Length;
        CalloutTranslation? lexicalCallouts = null;
        try
        {
            lexicalCallouts = PrepareCallouts(pattern, initialExtendedMode);
        }
        catch (JqRuntimeException)
        {
            // The event runner remains responsible for patterns whose nested callout grammar
            // the compatibility pre-parser deliberately does not lower.
        }

        var requiresEventRunner = requiresSearchStateRunner ||
            requiresSourceShapedReferenceRunner ||
            requiresAbsentLookbehindRunner ||
            requiresPerlNgOptionRunner ||
            requiresIgnoreCaseRunner ||
            requiresUtf8ByteGlobalRunner ||
            lexicalCallouts is null ||
            RequiresCalloutEventRunner(lexicalCallouts.Definitions);
        if (!requiresEventRunner &&
            !OnigurumaCalloutEventRunner.MayRequireRetryAccounting(
                input,
                pattern,
                modifiers))
        {
            matches = [];
            return false;
        }

        if (requiresEventRunner)
        {
            ValidateCalloutPatternCompilation(pattern, modifiers);
        }

        if (OnigurumaCalloutEventRunner.TryRun(
                input,
                pattern,
                modifiers,
                timeout,
                forceGlobal,
                out matches,
                out var unsupportedBoundary,
                requireSourceShapedExecution: requiresEventRunner))
        {
            return true;
        }

        if (unsupportedBoundary is not null)
        {
            if (requiresEventRunner)
            {
                throw UnsupportedCallout("event runner: " + unsupportedBoundary);
            }

            throw new JqRuntimeException(
                "Regex failure: exact managed retry accounting unsupported: " +
                unsupportedBoundary);
        }

        return false;
    }

    private static bool ContainsInlineIgnoreCaseOption(string pattern)
    {
        // This scan is deliberately conservative. A source-shaped option group
        // that enables `i` must have this grammar; a spelling in quoted/comment
        // text may route an otherwise ordinary pattern to the runner, but cannot
        // leave a real ignore-case scope on the lossy .NET lowering path.
        for (var index = 0; index < pattern.Length; index++)
        {
            if (pattern[index] == '(' && TryReadInlineOptions(
                    pattern,
                    index,
                    currentDotMode: false,
                    currentExtendedMode: false,
                    currentIgnoreCaseMode: false,
                    out _,
                    out _,
                    out var ignoreCaseMode,
                    out _,
                    out _) &&
                ignoreCaseMode)
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsEffectiveSearchStateEscape(string pattern, bool extended)
    {
        var extendedHere = extended;
        var optionModeStack = new Stack<bool>();
        for (var index = 0; index < pattern.Length; index++)
        {
            var current = pattern[index];
            if (index == 0 && current is '*' or '+' or '?')
            {
                throw new JqRuntimeException(
                    "Regex failure: target of repeat operator is not specified");
            }

            if (current == '\\')
            {
                if (index + 1 >= pattern.Length)
                {
                    break;
                }

                var escaped = pattern[++index];
                if (escaped == 'c' && OnigurumaCalloutAtomMatcher.TryParseEscape(
                        pattern,
                        index - 1,
                        out var control))
                {
                    index = control.NextPatternIndex - 1;
                    continue;
                }

                if (escaped == 'Q')
                {
                    var quoteEnd = pattern.IndexOf("\\E", index + 1, StringComparison.Ordinal);
                    if (quoteEnd < 0)
                    {
                        break;
                    }

                    index = quoteEnd + 1;
                    continue;
                }

                if (escaped is 'K' or 'G')
                {
                    return true;
                }

                continue;
            }

            if (extendedHere && current == '#')
            {
                index = pattern.IndexOf('\n', index + 1);
                if (index < 0)
                {
                    break;
                }

                continue;
            }

            // Content is opaque to Oniguruma's regex lexer: escapes, character-class
            // openers, and \K inside the body must not affect runner routing. Parsing it
            // here also preserves its source diagnostic before the generic scanners run.
            if (OnigurumaContentCallout.TryParse(pattern, index, out var contentCallout))
            {
                index = contentCallout.End;
                continue;
            }

            if (current == '[')
            {
                index = FindCalloutValidationCharacterClassEnd(pattern, index);
                continue;
            }

            if (index > 0 && pattern[index - 1] == '|' &&
                (current is '*' or '+' or '?' ||
                 current == '{' && IsRepeatQuantifierAt(pattern, index)))
            {
                throw new JqRuntimeException(
                    "Regex failure: target of repeat operator is not specified");
            }

            if (pattern.AsSpan(index).StartsWith("(?#", StringComparison.Ordinal))
            {
                var commentEnd = index + 3;
                while (commentEnd < pattern.Length && pattern[commentEnd] != ')')
                {
                    if (pattern[commentEnd] == '\\' && commentEnd + 1 < pattern.Length)
                    {
                        commentEnd++;
                    }

                    commentEnd++;
                }

                index = commentEnd;
                continue;
            }

            if (current == '(' &&
                TryReadInlineOptions(
                    pattern,
                    index,
                    currentDotMode: false,
                    extendedHere,
                    currentIgnoreCaseMode: false,
                    out _,
                    out var inlineExtendedMode,
                    out _,
                    out var inlineOptionsEnd,
                    out var scopedOptions))
            {
                if (scopedOptions)
                {
                    optionModeStack.Push(extendedHere);
                }

                extendedHere = inlineExtendedMode;
                index = inlineOptionsEnd;
                continue;
            }

            if (current == '(')
            {
                optionModeStack.Push(extendedHere);
            }
            else if (current == ')' && optionModeStack.Count != 0)
            {
                extendedHere = optionModeStack.Pop();
            }
        }

        return false;
    }

    private static bool ContainsPotentialDelimitedNameSyntax(string pattern)
    {
        for (var index = 0; index + 2 < pattern.Length; index++)
        {
            if (pattern[index] == '\\' && pattern[index + 1] is 'g' or 'k' &&
                pattern[index + 2] is '<' or '\'')
            {
                return true;
            }

            if (pattern[index] != '(' || pattern[index + 1] != '?')
            {
                continue;
            }

            if (pattern[index + 2] is '\'' or '&' ||
                pattern[index + 2] == 'P' && index + 3 < pattern.Length &&
                pattern[index + 3] == '<' ||
                pattern[index + 2] == '<' && index + 3 < pattern.Length &&
                pattern[index + 3] is not ('=' or '!'))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsPerlNgOnlyOptionSyntax(string pattern)
    {
        for (var index = 0; index < pattern.Length; index++)
        {
            if (!TryReadInlineOptions(
                    pattern,
                    index,
                    currentDotMode: false,
                    currentExtendedMode: false,
                    currentIgnoreCaseMode: false,
                    out _,
                    out _,
                    out _,
                    out var end,
                    out _))
            {
                continue;
            }

            var optionText = pattern.AsSpan(index + 2, end - index - 2);
            var hyphenCount = 0;
            var sawOptionLetter = false;
            foreach (var option in optionText)
            {
                hyphenCount += option == '-' ? 1 : 0;
                sawOptionLetter |= option is 'i' or 'm' or 's' or 'x';
            }

            if (hyphenCount > 1 || !sawOptionLetter)
            {
                return true;
            }

            index = end;
        }

        return false;
    }

    private static bool ContainsSourceShapedReference(string pattern, bool extended)
    {
        var extendedHere = extended;
        var optionModeStack = new Stack<bool>();
        for (var index = 0; index < pattern.Length; index++)
        {
            if (pattern[index] == '\\')
            {
                if (index + 1 < pattern.Length && pattern[index + 1] == 'Q')
                {
                    var quoteEnd = pattern.IndexOf("\\E", index + 2, StringComparison.Ordinal);
                    if (quoteEnd < 0)
                    {
                        return false;
                    }

                    index = quoteEnd + 1;
                    continue;
                }

                if (index + 2 < pattern.Length && pattern[index + 1] == 'k' &&
                    pattern[index + 2] is '<' or '\'' &&
                    TryReadNamedReference(pattern, index + 2, out var reference, out _) &&
                    (TrySplitBackreferenceLevel(reference, out _, out _) ||
                     reference.Length > 1 && reference[0] == '-' &&
                     reference[1..].All(char.IsAsciiDigit)))
                {
                    return true;
                }

                if (index + 2 < pattern.Length && pattern[index + 1] == 'g' &&
                    pattern[index + 2] is '<' or '\'')
                {
                    return true;
                }

                if (index + 1 < pattern.Length && pattern[index + 1] == 'c' &&
                    OnigurumaCalloutAtomMatcher.TryParseEscape(
                        pattern,
                        index,
                        out var control))
                {
                    index = control.NextPatternIndex - 1;
                    continue;
                }

                index++;
                continue;
            }

            if (pattern[index] == '[')
            {
                index = FindCharacterClassEnd(pattern, index);
                continue;
            }

            if (pattern.AsSpan(index).StartsWith("(?(", StringComparison.Ordinal) &&
                CaptureConditionIsReference(pattern, index + 3))
            {
                return true;
            }

            if (index + 3 < pattern.Length && pattern[index] == '(' &&
                pattern[index + 1] == '?' &&
                (char.IsAsciiDigit(pattern[index + 2]) ||
                 pattern[index + 2] is '+' or '-' &&
                 char.IsAsciiDigit(pattern[index + 3])))
            {
                return true;
            }

            if (extendedHere && pattern[index] == '#')
            {
                index = pattern.IndexOf('\n', index + 1);
                if (index < 0)
                {
                    return false;
                }

                continue;
            }

            if (pattern[index] == '(' && TryReadInlineOptions(
                    pattern,
                    index,
                    currentDotMode: false,
                    extendedHere,
                    currentIgnoreCaseMode: false,
                    out _,
                    out var nestedExtended,
                    out _,
                    out var optionEnd,
                    out var scoped))
            {
                if (scoped)
                {
                    optionModeStack.Push(extendedHere);
                }

                extendedHere = nestedExtended;
                index = optionEnd;
            }
            else if (pattern[index] == '(')
            {
                optionModeStack.Push(extendedHere);
            }
            else if (pattern[index] == ')' && optionModeStack.TryPop(out var outerExtended))
            {
                extendedHere = outerExtended;
            }
        }

        return false;
    }

    private static bool ContainsAbsentLookbehind(string pattern, bool extended)
    {
        var absentFunctions = CollectAbsentFunctions(pattern);
        if (absentFunctions.Count == 0)
        {
            return false;
        }

        var extendedHere = extended;
        var optionModeStack = new Stack<bool>();
        for (var index = 0; index < pattern.Length; index++)
        {
            if (pattern[index] == '\\')
            {
                if (index + 1 < pattern.Length && pattern[index + 1] == 'c' &&
                    OnigurumaCalloutAtomMatcher.TryParseEscape(
                        pattern,
                        index,
                        out var control))
                {
                    index = control.NextPatternIndex - 1;
                    continue;
                }

                if (index + 1 < pattern.Length && pattern[index + 1] == 'Q')
                {
                    var quoteEnd = pattern.IndexOf("\\E", index + 2, StringComparison.Ordinal);
                    if (quoteEnd < 0)
                    {
                        return false;
                    }

                    index = quoteEnd + 1;
                }
                else
                {
                    index++;
                }

                continue;
            }

            if (pattern[index] == '[')
            {
                index = FindCharacterClassEnd(pattern, index);
                continue;
            }

            if (extendedHere && pattern[index] == '#')
            {
                index = pattern.IndexOf('\n', index + 1);
                if (index < 0)
                {
                    return false;
                }

                continue;
            }

            if (OnigurumaContentCallout.TryParse(pattern, index, out var contentCallout))
            {
                index = contentCallout.End;
                continue;
            }

            if (pattern.AsSpan(index).StartsWith("(?#", StringComparison.Ordinal))
            {
                var commentEnd = index + 3;
                while (commentEnd < pattern.Length && pattern[commentEnd] != ')')
                {
                    if (pattern[commentEnd] == '\\' && commentEnd + 1 < pattern.Length)
                    {
                        commentEnd++;
                    }

                    commentEnd++;
                }

                index = commentEnd;
                continue;
            }

            if (index + 3 < pattern.Length &&
                pattern.AsSpan(index, 3).SequenceEqual("(?<") &&
                pattern[index + 3] is '=' or '!')
            {
                var close = FindMatchingParenthesis(pattern, index);
                if (close > index + 4 && absentFunctions.Any(absent =>
                        absent.Start >= index + 4 && absent.End <= close))
                {
                    return true;
                }
            }

            if (pattern[index] == '(' && TryReadInlineOptions(
                    pattern,
                    index,
                    currentDotMode: false,
                    extendedHere,
                    currentIgnoreCaseMode: false,
                    out _,
                    out var nestedExtended,
                    out _,
                    out var optionEnd,
                    out var scoped))
            {
                if (scoped)
                {
                    optionModeStack.Push(extendedHere);
                }

                extendedHere = nestedExtended;
                index = optionEnd;
            }
            else if (pattern[index] == '(')
            {
                optionModeStack.Push(extendedHere);
            }
            else if (pattern[index] == ')' && optionModeStack.TryPop(out var outerExtended))
            {
                extendedHere = outerExtended;
            }
        }

        return false;
    }

    private static bool CaptureConditionIsReference(string pattern, int start)
    {
        if (start >= pattern.Length)
        {
            return false;
        }

        if (pattern[start] is '<' or '\'')
        {
            return true;
        }

        if (pattern[start] is not ('+' or '-') && !char.IsAsciiDigit(pattern[start]))
        {
            return false;
        }

        return TryParseOnigurumaReference(
            pattern,
            start - 1,
            out var reference,
            out _) &&
            reference.IsNumeric;
    }

    private static bool TrySplitBackreferenceLevel(
        string value,
        out string reference,
        out int level)
    {
        reference = value;
        level = 0;
        var suffix = value.AsSpan(1).IndexOfAny('+', '-');
        if (suffix < 0)
        {
            return false;
        }

        suffix++;
        if (!int.TryParse(
                value.AsSpan(suffix),
                System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture,
                out level))
        {
            return false;
        }

        reference = value[..suffix];
        return true;
    }

    private static bool TryParseOnigurumaReference(
        string pattern,
        int delimiterIndex,
        out OnigurumaReference reference,
        out string? diagnostic)
    {
        if ((uint)delimiterIndex >= (uint)pattern.Length ||
            pattern[delimiterIndex] is not ('<' or '\'' or '('))
        {
            reference = default;
            diagnostic = null;
            return false;
        }

        return TryParseOnigurumaName(
            pattern,
            pattern[delimiterIndex],
            delimiterIndex + 1,
            isReference: true,
            allowLevel: true,
            out reference,
            out diagnostic);
    }

    private static bool TryParseOnigurumaName(
        string pattern,
        char startCode,
        int contentStart,
        bool isReference,
        bool allowLevel,
        out OnigurumaReference reference,
        out string? diagnostic)
    {
        reference = default;
        diagnostic = null;
        if (startCode is not ('<' or '\'' or '(') ||
            (uint)contentStart > (uint)pattern.Length)
        {
            return false;
        }

        var terminator = startCode switch
        {
            '<' => '>',
            '\'' => '\'',
            _ => ')',
        };
        if (contentStart >= pattern.Length || pattern[contentStart] == terminator)
        {
            diagnostic = "Regex failure: group name is empty";
            return false;
        }

        return allowLevel
            ? TryParseOnigurumaNameWithLevel(
                pattern,
                contentStart,
                terminator,
                out reference,
                out diagnostic)
            : TryParsePlainOnigurumaName(
                pattern,
                contentStart,
                terminator,
                isReference,
                out reference,
                out diagnostic);
    }

    private static bool TryParsePlainOnigurumaName(
        string pattern,
        int contentStart,
        char terminator,
        bool isReference,
        out OnigurumaReference reference,
        out string? diagnostic)
    {
        reference = default;
        diagnostic = null;
        var index = contentStart;
        var first = ReadOnigurumaNameScalar(pattern, ref index);
        var relative = first is '+' or '-';
        var numeric = IsAsciiDigitScalar(first) || relative;
        var numericSign = first == '-' ? -1 : 1;
        var numericStart = relative ? index : contentStart;
        var digitCount = IsAsciiDigitScalar(first) ? 1 : 0;
        var error = OnigurumaNameError.None;

        if (numeric && !isReference)
        {
            error = OnigurumaNameError.InvalidGroup;
        }
        else if (!numeric && !OnigurumaCalloutAtomMatcher.IsWordScalar(first))
        {
            error = OnigurumaNameError.InvalidCharacter;
        }

        var nameEnd = pattern.Length;
        var current = first;
        if (error != OnigurumaNameError.None)
        {
            // fetch_name() recovers only first-character errors so that its
            // OnigErrorInfo range ends at the source delimiter or close-paren.
            while (index < pattern.Length)
            {
                nameEnd = index;
                current = ReadOnigurumaNameScalar(pattern, ref index);
                if (current == terminator || current == ')')
                {
                    break;
                }
            }

            // Its PEND check runs after consuming the stop scalar and therefore
            // includes a delimiter which is also the final pattern scalar.
            if (index >= pattern.Length)
            {
                nameEnd = pattern.Length;
            }

            diagnostic = OnigurumaNameDiagnostic(error, pattern, contentStart, nameEnd);
            return false;
        }

        while (index < pattern.Length)
        {
            nameEnd = index;
            current = ReadOnigurumaNameScalar(pattern, ref index);
            if (current == terminator || current == ')')
            {
                if (numeric && digitCount == 0)
                {
                    error = OnigurumaNameError.InvalidGroup;
                }

                break;
            }

            if (numeric)
            {
                if (IsAsciiDigitScalar(current))
                {
                    digitCount++;
                }
                else
                {
                    error = OnigurumaCalloutAtomMatcher.IsWordScalar(current)
                        ? OnigurumaNameError.InvalidGroup
                        : OnigurumaNameError.InvalidCharacter;
                    numeric = false;
                }
            }
            else if (!OnigurumaCalloutAtomMatcher.IsWordScalar(current))
            {
                error = OnigurumaNameError.InvalidCharacter;
            }
        }

        if (current != terminator)
        {
            diagnostic = OnigurumaNameDiagnostic(
                OnigurumaNameError.InvalidGroup,
                pattern,
                contentStart,
                nameEnd);
            return false;
        }

        // Preserve fetch_name()'s source quirk: errors assigned after the first
        // scalar are ignored when the designated terminator is reached. This is
        // why names such as `a!` are accepted by definitions and subcalls.
        int? numericValue = null;
        if (numeric)
        {
            var magnitude = 0;
            if (digitCount != 0 && !TryParseOnigurumaMagnitude(
                    pattern.AsSpan(numericStart, nameEnd - numericStart),
                    out magnitude))
            {
                diagnostic = "Regex failure: too big number";
                return false;
            }

            if (relative && magnitude == 0)
            {
                diagnostic = OnigurumaNameDiagnostic(
                    OnigurumaNameError.InvalidGroup,
                    pattern,
                    contentStart,
                    nameEnd);
                return false;
            }

            numericValue = numericSign * magnitude;
        }

        reference = new OnigurumaReference(
            pattern[contentStart..nameEnd],
            numericValue,
            relative,
            Level: null,
            End: nameEnd);
        return true;
    }

    private static bool TryParseOnigurumaNameWithLevel(
        string pattern,
        int contentStart,
        char terminator,
        out OnigurumaReference reference,
        out string? diagnostic)
    {
        reference = default;
        diagnostic = null;
        var index = contentStart;
        var first = ReadOnigurumaNameScalar(pattern, ref index);
        var relative = first is '+' or '-';
        var numeric = IsAsciiDigitScalar(first) || relative;
        var numericSign = first == '-' ? -1 : 1;
        var numericStart = relative ? index : contentStart;
        var digitCount = IsAsciiDigitScalar(first) ? 1 : 0;
        var error = !numeric && !OnigurumaCalloutAtomMatcher.IsWordScalar(first)
            ? OnigurumaNameError.InvalidCharacter
            : OnigurumaNameError.None;
        var nameEnd = pattern.Length;
        var current = first;

        while (index < pattern.Length)
        {
            nameEnd = index;
            current = ReadOnigurumaNameScalar(pattern, ref index);
            if (current == terminator || current == ')' || current is '+' or '-')
            {
                if (numeric && digitCount == 0)
                {
                    error = OnigurumaNameError.InvalidGroup;
                }

                break;
            }

            if (numeric)
            {
                if (IsAsciiDigitScalar(current))
                {
                    digitCount++;
                }
                else
                {
                    error = OnigurumaNameError.InvalidGroup;
                    numeric = false;
                }
            }
            else if (!OnigurumaCalloutAtomMatcher.IsWordScalar(current))
            {
                error = OnigurumaNameError.InvalidCharacter;
            }
        }

        int? level = null;
        var terminatorIndex = nameEnd;
        if (error == OnigurumaNameError.None && current != terminator)
        {
            if (current is '+' or '-')
            {
                var levelSign = current == '-' ? -1 : 1;
                if (index >= pattern.Length)
                {
                    diagnostic = OnigurumaNameDiagnostic(
                        OnigurumaNameError.InvalidCharacter,
                        pattern,
                        contentStart,
                        nameEnd);
                    return false;
                }

                var levelStart = index;
                if (!IsAsciiDigitScalar(ReadOnigurumaNameScalar(pattern, ref index)))
                {
                    diagnostic = OnigurumaNameDiagnostic(
                        OnigurumaNameError.InvalidGroup,
                        pattern,
                        contentStart,
                        pattern.Length);
                    return false;
                }

                while (index < pattern.Length && char.IsAsciiDigit(pattern[index]))
                {
                    index++;
                }

                if (!TryParseOnigurumaMagnitude(
                        pattern.AsSpan(levelStart, index - levelStart),
                        out var levelMagnitude))
                {
                    diagnostic = "Regex failure: too big number";
                    return false;
                }

                level = levelSign * levelMagnitude;
                if (index < pattern.Length && pattern[index] == terminator)
                {
                    terminatorIndex = index;
                    index++;
                    current = terminator;
                }
            }

            if (current != terminator)
            {
                diagnostic = OnigurumaNameDiagnostic(
                    OnigurumaNameError.InvalidGroup,
                    pattern,
                    contentStart,
                    pattern.Length);
                return false;
            }
        }

        if (error != OnigurumaNameError.None)
        {
            diagnostic = OnigurumaNameDiagnostic(error, pattern, contentStart, nameEnd);
            return false;
        }

        int? numericValue = null;
        if (numeric)
        {
            if (!TryParseOnigurumaMagnitude(
                    pattern.AsSpan(numericStart, nameEnd - numericStart),
                    out var magnitude))
            {
                diagnostic = "Regex failure: too big number";
                return false;
            }

            if (relative && magnitude == 0)
            {
                diagnostic = OnigurumaNameDiagnostic(
                    OnigurumaNameError.InvalidGroup,
                    pattern,
                    contentStart,
                    nameEnd);
                return false;
            }

            numericValue = numericSign * magnitude;
        }

        reference = new OnigurumaReference(
            pattern[contentStart..nameEnd],
            numericValue,
            relative,
            level,
            terminatorIndex);
        return true;
    }

    private static int ReadOnigurumaNameScalar(string pattern, ref int index)
    {
        if (Rune.TryGetRuneAt(pattern, index, out var rune))
        {
            index += rune.Utf16SequenceLength;
            return rune.Value;
        }

        return pattern[index++];
    }

    private static bool IsAsciiDigitScalar(int scalar) => scalar is >= '0' and <= '9';

    private static string OnigurumaNameDiagnostic(
        OnigurumaNameError error,
        string pattern,
        int start,
        int end)
    {
        var parameter = pattern[start..Math.Clamp(end, start, pattern.Length)];
        var bytes = Encoding.UTF8.GetBytes(parameter);
        if (bytes.Length > 27)
        {
            parameter = Encoding.UTF8.GetString(bytes, 0, 27) + "...";
        }

        return error switch
        {
            OnigurumaNameError.InvalidCharacter =>
                "Regex failure: invalid char in group name <" + parameter + ">",
            _ => "Regex failure: invalid group name <" + parameter + ">",
        };
    }

    private static bool TryParseOnigurumaMagnitude(ReadOnlySpan<char> digits, out int value)
    {
        value = 0;
        foreach (var digitCharacter in digits)
        {
            var digit = digitCharacter - '0';
            if ((uint)digit > 9 || value > (int.MaxValue - digit) / 10)
            {
                value = 0;
                return false;
            }

            value = value * 10 + digit;
        }

        return digits.Length != 0;
    }

    private static void ValidateOnigurumaReference(
        OnigurumaReference reference,
        int captureCount,
        IReadOnlySet<string> namedGroups,
        List<long> numericBackreferences)
    {
        if (!reference.IsNumeric)
        {
            if (!namedGroups.Contains(reference.Name))
            {
                throw new JqRuntimeException(
                    "Regex failure: undefined name <" + reference.Name + "> reference");
            }

            return;
        }

        var parsed = reference.NumericValue!.Value;
        long absolute = parsed;
        if (reference.IsRelative)
        {
            absolute = parsed > 0
                ? (long)captureCount + parsed
                : (long)captureCount + 1 + parsed;
        }

        if (absolute <= 0 || absolute > int.MaxValue)
        {
            throw new JqRuntimeException("Regex failure: invalid backref number/name");
        }

        numericBackreferences.Add(absolute);
    }

    private static bool TryValidateCaptureConditionReference(
        string pattern,
        int groupOpen,
        int captureCount,
        IReadOnlySet<string> namedGroups,
        List<long> numericBackreferences,
        out int conditionEnd)
    {
        conditionEnd = groupOpen;
        var conditionStart = groupOpen + 3;
        if (conditionStart >= pattern.Length)
        {
            return false;
        }

        var enclosed = pattern[conditionStart] is '<' or '\'';
        if (!enclosed && pattern[conditionStart] is not ('+' or '-') &&
            !char.IsAsciiDigit(pattern[conditionStart]))
        {
            return false;
        }

        var delimiterIndex = enclosed ? conditionStart : groupOpen + 2;
        if (!TryParseOnigurumaReference(
                pattern,
                delimiterIndex,
                out var reference,
                out var diagnostic))
        {
            // regparse.c falls back to an arbitrary regex condition when a bare
            // numeric-looking condition does not lex as a reference. Enclosed
            // references commit to the checker grammar and retain its diagnostic.
            if (!enclosed)
            {
                return false;
            }

            throw new JqRuntimeException(diagnostic!);
        }

        if (!reference.IsNumeric && !enclosed)
        {
            return false;
        }

        var wrapperEnd = enclosed ? reference.End + 1 : reference.End;
        if ((uint)wrapperEnd >= (uint)pattern.Length || pattern[wrapperEnd] != ')')
        {
            if (!enclosed)
            {
                return false;
            }

            throw new JqRuntimeException("Regex failure: end pattern in group");
        }

        ValidateOnigurumaReference(
            reference,
            captureCount,
            namedGroups,
            numericBackreferences);
        conditionEnd = wrapperEnd;
        return true;
    }

    private static void ValidateCalloutPatternCompilation(string pattern, string? modifiers)
    {
        var extendedHere = ParseModifiers(modifiers).Extended;
        var optionModeStack = new Stack<bool>();
        var numericBackreferences = new List<long>();
        var namedGroups = new HashSet<string>(StringComparer.Ordinal);
        var conditionalHeaderOpens = new HashSet<int>();
        var callouts = new List<CalloutDefinition>();
        var calloutTags = new HashSet<string>(StringComparer.Ordinal);
        var captureCount = 0;
        var depth = 0;

        for (var index = 0; index < pattern.Length; index++)
        {
            var current = pattern[index];
            if (current == '\\')
            {
                if (index + 1 >= pattern.Length)
                {
                    throw new JqRuntimeException("Regex failure: end pattern at escape");
                }

                var escaped = pattern[++index];
                if (escaped == 'Q')
                {
                    var quoteEnd = pattern.IndexOf("\\E", index + 1, StringComparison.Ordinal);
                    if (quoteEnd < 0)
                    {
                        break;
                    }

                    index = quoteEnd + 1;
                    continue;
                }

                if (escaped == 'c' && OnigurumaCalloutAtomMatcher.TryParseEscape(
                        pattern,
                        index - 1,
                        out var control))
                {
                    index = control.NextPatternIndex - 1;
                    continue;
                }

                if (escaped is 'p' or 'P' &&
                    TryReadUnicodeProperty(pattern, index + 1, out var propertyName, out var propertyEnd))
                {
                    var lookupName = propertyName.StartsWith('^') ? propertyName[1..] : propertyName;
                    if (ScalarPropertyPattern(lookupName) is null)
                    {
                        throw new JqRuntimeException(
                            "Regex failure: invalid character property name {" + lookupName + "}");
                    }

                    index = propertyEnd;
                    continue;
                }

                if (escaped is 'p' or 'P' && index + 1 < pattern.Length && pattern[index + 1] == '{')
                {
                    throw new JqRuntimeException(
                        "Regex failure: end pattern with unmatched parenthesis");
                }

                if (escaped == 'k' && index + 1 < pattern.Length &&
                    pattern[index + 1] is '<' or '\'')
                {
                    if (!TryParseOnigurumaReference(
                            pattern,
                            index + 1,
                            out var reference,
                            out var diagnostic))
                    {
                        throw new JqRuntimeException(diagnostic!);
                    }

                    ValidateOnigurumaReference(
                        reference,
                        captureCount,
                        namedGroups,
                        numericBackreferences);
                    index = reference.End;
                    continue;
                }

                if (escaped == 'g' && index + 1 < pattern.Length &&
                    pattern[index + 1] is '<' or '\'')
                {
                    if (!TryParseOnigurumaName(
                            pattern,
                            pattern[index + 1],
                            index + 2,
                            isReference: true,
                            allowLevel: false,
                            out var subcall,
                            out var diagnostic))
                    {
                        throw new JqRuntimeException(diagnostic!);
                    }

                    index = subcall.End;
                    continue;
                }

                if (char.IsAsciiDigit(escaped) && escaped != '0')
                {
                    long reference = escaped - '0';
                    while (index + 1 < pattern.Length && char.IsAsciiDigit(pattern[index + 1]))
                    {
                        var digit = pattern[++index] - '0';
                        reference = reference > (long.MaxValue - digit) / 10
                            ? long.MaxValue
                            : reference * 10 + digit;
                    }

                    if (reference <= captureCount || reference <= 9)
                    {
                        numericBackreferences.Add(reference);
                    }
                }

                continue;
            }

            if (extendedHere && current == '#')
            {
                index = pattern.IndexOf('\n', index + 1);
                if (index < 0)
                {
                    break;
                }

                continue;
            }

            if (current == '(' && index + 3 < pattern.Length &&
                pattern[index + 1] == '?' && pattern[index + 2] == '<' &&
                pattern[index + 3] is '=' or '!')
            {
                var bodyStart = index + 4;
                if (extendedHere)
                {
                    while (bodyStart < pattern.Length)
                    {
                        if (pattern[bodyStart] is ' ' or '\t' or '\n' or '\r' or '\f')
                        {
                            bodyStart++;
                            continue;
                        }

                        if (pattern[bodyStart] == '#')
                        {
                            var newline = pattern.IndexOf('\n', bodyStart + 1);
                            bodyStart = newline < 0 ? pattern.Length : newline + 1;
                            continue;
                        }

                        break;
                    }
                }

                if (IsRepeatQuantifierAt(pattern, bodyStart))
                {
                    throw new JqRuntimeException(
                        "Regex failure: target of repeat operator is not specified");
                }
            }

            if (current == '(' && pattern.AsSpan(index).StartsWith("(?(", StringComparison.Ordinal) &&
                TryValidateCaptureConditionReference(
                    pattern,
                    index,
                    captureCount,
                    namedGroups,
                    numericBackreferences,
                    out var conditionEnd))
            {
                depth++;
                optionModeStack.Push(extendedHere);
                index = conditionEnd;
                continue;
            }

            if (current == '(' && pattern.AsSpan(index).StartsWith("(?(", StringComparison.Ordinal) &&
                TryFindConditionalHeaderEnd(pattern, index + 2, out _))
            {
                conditionalHeaderOpens.Add(index + 2);
            }

            if (OnigurumaContentCallout.TryParse(pattern, index, out var contentCallout))
            {
                callouts.Add(new CalloutDefinition(
                    callouts.Count + 1,
                    CalloutKind.Content,
                    contentCallout.Tag,
                    [contentCallout.Direction.ToString()],
                    []));
                index = contentCallout.End;
                continue;
            }

            if (current == '(' && pattern.AsSpan(index).StartsWith("(?&", StringComparison.Ordinal))
            {
                if (!TryParseOnigurumaName(
                        pattern,
                        '(',
                        index + 3,
                        isReference: false,
                        allowLevel: false,
                        out var subcall,
                        out var diagnostic))
                {
                    throw new JqRuntimeException(diagnostic!);
                }

                index = subcall.End;
                continue;
            }

            if (current == '(' && index + 2 < pattern.Length && pattern[index + 1] == '?' &&
                (char.IsAsciiDigit(pattern[index + 2]) ||
                 pattern[index + 2] is '+' or '-' && index + 3 < pattern.Length &&
                    char.IsAsciiDigit(pattern[index + 3])))
            {
                if (!TryParseOnigurumaName(
                        pattern,
                        '(',
                        index + 2,
                        isReference: true,
                        allowLevel: false,
                        out var subcall,
                        out var diagnostic))
                {
                    throw new JqRuntimeException(diagnostic!);
                }

                if (!subcall.IsNumeric)
                {
                    throw new JqRuntimeException(
                        OnigurumaNameDiagnostic(
                            OnigurumaNameError.InvalidGroup,
                            pattern,
                            index + 2,
                            index + 2));
                }

                index = subcall.End;
                continue;
            }

            if (current == '[')
            {
                index = FindCalloutValidationCharacterClassEnd(pattern, index);
                continue;
            }

            if (pattern.AsSpan(index).StartsWith("(?#", StringComparison.Ordinal))
            {
                var commentEnd = index + 3;
                while (commentEnd < pattern.Length && pattern[commentEnd] != ')')
                {
                    if (pattern[commentEnd] == '\\' && commentEnd + 1 < pattern.Length)
                    {
                        commentEnd++;
                    }

                    commentEnd++;
                }

                if (commentEnd >= pattern.Length)
                {
                    throw new JqRuntimeException(
                        "Regex failure: end pattern in group");
                }

                index = commentEnd;
                continue;
            }

            if (current == '(' && index + 1 < pattern.Length && pattern[index + 1] == '*')
            {
                _ = TryReadCallout(
                    pattern,
                    index,
                    callouts.Count + 1,
                    out var callout,
                    out var calloutEnd);
                if (callout.Tag is not null && !calloutTags.Add(callout.Tag))
                {
                    throw new JqRuntimeException(
                        "Regex failure: multiplex defined name <" + callout.Tag + ">");
                }

                callouts.Add(callout);
                if (IsRepeatQuantifierAt(pattern, calloutEnd + 1))
                {
                    throw new JqRuntimeException(
                        "Regex failure: target of repeat operator is invalid");
                }

                index = calloutEnd;
                continue;
            }

            if (current == '(' && index + 3 < pattern.Length && pattern[index + 1] == '?' &&
                pattern[index + 2] == '-' && char.IsAsciiLetter(pattern[index + 3]) &&
                pattern[index + 3] is not ('i' or 'm' or 's' or 'x'))
            {
                throw new JqRuntimeException("Regex failure: undefined group option");
            }

            if (current == '(' &&
                TryReadInlineOptions(
                    pattern,
                    index,
                    currentDotMode: false,
                    extendedHere,
                    currentIgnoreCaseMode: false,
                    out _,
                    out var inlineExtendedMode,
                    out _,
                    out var inlineOptionsEnd,
                    out var scopedOptions))
            {
                if (scopedOptions)
                {
                    depth++;
                    optionModeStack.Push(extendedHere);
                }

                extendedHere = inlineExtendedMode;
                index = inlineOptionsEnd;
                continue;
            }

            if (current == '(')
            {
                depth++;
                optionModeStack.Push(extendedHere);
                if (conditionalHeaderOpens.Remove(index))
                {
                    continue;
                }

                if (index + 1 >= pattern.Length || pattern[index + 1] != '?')
                {
                    captureCount++;
                }
                else if (index + 2 < pattern.Length &&
                         (pattern[index + 2] == '<' &&
                          index + 3 < pattern.Length && pattern[index + 3] is not ('=' or '!') ||
                          pattern[index + 2] == '\''))
                {
                    if (!TryParseOnigurumaName(
                            pattern,
                            pattern[index + 2],
                            index + 3,
                            isReference: false,
                            allowLevel: false,
                            out var definition,
                            out var diagnostic))
                    {
                        throw new JqRuntimeException(diagnostic!);
                    }

                    captureCount++;
                    namedGroups.Add(definition.Name);
                    index = definition.End;
                }
                else if (pattern.AsSpan(index).StartsWith("(?P", StringComparison.Ordinal))
                {
                    // Perl-NG deliberately omits ONIG_SYN_OP2_QMARK_CAPITAL_P_NAME.
                    throw new JqRuntimeException("Regex failure: undefined group option");
                }
                else if (index + 2 < pattern.Length &&
                         char.IsAsciiLetter(pattern[index + 2]) &&
                         pattern[index + 2] is not ('P' or 'R'))
                {
                    throw new JqRuntimeException("Regex failure: undefined group option");
                }
                else if (index + 3 < pattern.Length && pattern[index + 2] == '-' &&
                         char.IsAsciiLetter(pattern[index + 3]) &&
                         pattern[index + 3] is not ('i' or 'm' or 's' or 'x'))
                {
                    throw new JqRuntimeException("Regex failure: undefined group option");
                }
                else if (index + 2 < pattern.Length && pattern[index + 2] == '?')
                {
                    throw new JqRuntimeException("Regex failure: undefined group option");
                }

                continue;
            }

            if (current != ')')
            {
                if (current == '{')
                {
                    ValidateCalloutRepeatRange(pattern, index);
                }

                continue;
            }

            if (depth == 0)
            {
                throw new JqRuntimeException("Regex failure: unmatched close parenthesis");
            }

            depth--;
            extendedHere = optionModeStack.Pop();
        }

        if (depth != 0)
        {
            throw new JqRuntimeException("Regex failure: end pattern with unmatched parenthesis");
        }

        if (numericBackreferences.Any(reference => reference > captureCount))
        {
            throw new JqRuntimeException("Regex failure: invalid backref number/name");
        }


        ValidateCalloutTags(callouts);
    }

    private static void ValidateCalloutRepeatRange(string pattern, int open)
    {
        var close = pattern.IndexOf('}', open + 1);
        if (close < 0)
        {
            return;
        }

        var body = pattern.AsSpan(open + 1, close - open - 1);
        var comma = body.IndexOf(',');
        if (comma <= 0 || comma + 1 >= body.Length ||
            !int.TryParse(
                body[..comma],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var lower) ||
            !int.TryParse(
                body[(comma + 1)..],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var upper))
        {
            return;
        }

        if (upper < lower)
        {
            throw new JqRuntimeException(
                "Regex failure: upper is smaller than lower in repeat range");
        }
    }

    private static int FindCalloutValidationCharacterClassEnd(string pattern, int open) =>
        FindPerlNgCharacterClassEnd(pattern, open);

    internal static int FindPerlNgCharacterClassEnd(string pattern, int open)
    {
        var firstContent = open + 1;
        if (firstContent < pattern.Length && pattern[firstContent] == '^')
        {
            firstContent++;
        }

        if (firstContent < pattern.Length && pattern[firstContent] == ']' &&
            !HasPerlNgCharacterClassClose(pattern, firstContent + 1))
        {
            throw new JqRuntimeException("Regex failure: empty char-class");
        }

        for (var index = open + 1; index < pattern.Length; index++)
        {
            if (pattern[index] == '\\')
            {
                if (index + 1 >= pattern.Length)
                {
                    break;
                }

                var slashIndex = index;
                var escaped = pattern[++index];
                if (escaped == 'c' && OnigurumaCalloutAtomMatcher.TryParseEscape(
                        pattern,
                        slashIndex,
                        out var control,
                        inCharacterClass: true))
                {
                    index = control.NextPatternIndex - 1;
                    continue;
                }

                if (escaped is 'p' or 'P' &&
                    TryReadUnicodeProperty(pattern, index + 1, out var propertyName, out var propertyEnd))
                {
                    var lookupName = propertyName.StartsWith('^') ? propertyName[1..] : propertyName;
                    if (ScalarPropertyPattern(lookupName) is null)
                    {
                        throw new JqRuntimeException(
                            "Regex failure: invalid character property name {" + lookupName + "}");
                    }

                    index = propertyEnd;
                }

                continue;
            }

            if (pattern[index] == '[' && index + 1 < pattern.Length &&
                pattern[index + 1] == ':')
            {
                var terminator = pattern[index + 1].ToString() + ']';
                var nestedEnd = pattern.IndexOf(terminator, index + 2, StringComparison.Ordinal);
                if (nestedEnd >= 0)
                {
                    index = nestedEnd + 1;
                    continue;
                }
            }

            // Perl-NG does not enable ONIG_SYN_OP2_CCLASS_SET_OP. Ordinary '['
            // and '&&' therefore remain literal class members; the first
            // non-initial ']' closes the class.
            if (pattern[index] == ']' && index != firstContent)
            {
                ValidateLiteralCharacterClassRange(pattern, firstContent, index);
                return index;
            }
        }

        throw new JqRuntimeException("Regex failure: premature end of char-class");
    }

    private static bool HasPerlNgCharacterClassClose(string pattern, int start)
    {
        for (var index = start; index < pattern.Length; index++)
        {
            if (pattern[index] == '\\')
            {
                if (OnigurumaCalloutAtomMatcher.TryParseEscape(
                        pattern,
                        index,
                        out var escaped,
                        inCharacterClass: true))
                {
                    index = escaped.NextPatternIndex - 1;
                }
                else
                {
                    index++;
                }

                continue;
            }

            if (pattern[index] == '[' && index + 1 < pattern.Length &&
                pattern[index + 1] == ':')
            {
                var terminator = pattern[index + 1].ToString() + ']';
                var nestedEnd = pattern.IndexOf(terminator, index + 2, StringComparison.Ordinal);
                if (nestedEnd >= 0)
                {
                    index = nestedEnd + 1;
                    continue;
                }
            }

            if (pattern[index] == ']')
            {
                return true;
            }
        }

        return false;
    }

    private static void ValidateLiteralCharacterClassRange(
        string pattern,
        int contentStart,
        int contentEnd)
    {
        if (contentStart < contentEnd && pattern[contentStart] == '^')
        {
            contentStart++;
        }

        var content = pattern.AsSpan(contentStart, contentEnd - contentStart);
        if (content.Contains('\\'))
        {
            return;
        }

        var scalars = new List<int>(3);
        foreach (var scalar in content.EnumerateRunes())
        {
            scalars.Add(scalar.Value);
        }

        if (scalars.Count == 3 && scalars[1] == '-' && scalars[0] > scalars[2])
        {
            throw new JqRuntimeException("Regex failure: empty range in char class");
        }
    }

    private static bool RequiresCalloutEventRunner(
        IReadOnlyList<CalloutDefinition> definitions)
    {
        var referencedTags = definitions
            .SelectMany(definition => definition.Kind switch
            {
                CalloutKind.Max => definition.Arguments.Take(1),
                CalloutKind.Compare => definition.Arguments.Where((_, index) => index is 0 or 2),
                _ => [],
            })
            .Where(reference => !OnigurumaCalloutArgumentParser.TryParseLong(reference, out _))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var definition in definitions)
        {
            if (HasInvalidRuntimeCalloutArgument(definition) ||
                definition.Kind is CalloutKind.Error or CalloutKind.Mismatch or CalloutKind.Skip)
            {
                return true;
            }

            var tagIsObserved = definition.Tag is not null &&
                referencedTags.Contains(definition.Tag);
            if (definition.Kind == CalloutKind.TotalCount && tagIsObserved ||
                definition.Kind == CalloutKind.Count && tagIsObserved ||
                definition.Kind == CalloutKind.Content && definition.IsConditional ||
                definition.Kind == CalloutKind.Max ||
                definition.Kind == CalloutKind.Compare && tagIsObserved)
            {
                return true;
            }
        }

        return false;
    }

    private static JqRegexMatch[] ConvertCalloutEventMatches(
        string input,
        IReadOnlyList<OnigurumaCalloutEventMatch> matches)
    {
        var converted = new JqRegexMatch[matches.Count];
        for (var matchIndex = 0; matchIndex < matches.Count; matchIndex++)
        {
            converted[matchIndex] = ConvertCalloutEventMatch(input, matches[matchIndex]);
        }

        return converted;
    }

    private static JqRegexMatch ConvertCalloutEventMatch(
        string input,
        OnigurumaCalloutEventMatch match)
    {
        _ = input;
        var captures = new JqRegexCapture[match.Captures.Count];
        for (var captureIndex = 0; captureIndex < match.Captures.Count; captureIndex++)
        {
            var capture = match.Captures[captureIndex];
            captures[captureIndex] = capture.JqIndex < 0
                ? new JqRegexCapture(-1, 0, null, capture.Name)
                : new JqRegexCapture(
                    capture.JqIndex,
                    capture.JqLength,
                    capture.Value,
                    capture.Name);
        }

        return new JqRegexMatch(
            match.JqIndex,
            match.JqLength,
            match.Value,
            captures);
    }

    private static string ReplaceCalloutEventMatches(
        string input,
        IReadOnlyList<OnigurumaCalloutEventMatch> matches,
        Func<IReadOnlyDictionary<string, string?>, string> replacement)
    {
        if (matches.Count == 0)
        {
            return input;
        }

        var inputBytes = Encoding.UTF8.GetBytes(input);
        using var result = new MemoryStream(inputBytes.Length);
        var previous = 0;
        foreach (var match in matches)
        {
            result.Write(inputBytes, previous, match.ByteIndex - previous);
            var jqMatch = ConvertCalloutEventMatch(input, match);
            var replacementText = replacement(NamedCaptures(jqMatch)) ??
                throw new JqRuntimeException("jq substitution produced a null string");
            var replacementBytes = Encoding.UTF8.GetBytes(replacementText);
            result.Write(replacementBytes, 0, replacementBytes.Length);
            previous = match.ByteIndex + match.ByteLength;
        }

        result.Write(inputBytes, previous, inputBytes.Length - previous);
        return Encoding.UTF8.GetString(result.ToArray());
    }

    private static List<DotNetRegexMatch> FindRawMatches(
        string input,
        RegexExecution execution,
        bool stopAfterFirst)
    {
        if (execution.AnchoredRegex is not null && !execution.FindLongest)
        {
            return FindSkipAwareRawMatches(input, execution, stopAfterFirst);
        }

        if (execution.FindLongest)
        {
            if (execution.AnchoredRegex is not null)
            {
                return FindLongestSkipAwareRawMatches(input, execution, stopAfterFirst);
            }

            return FindLongestRawMatches(input, execution, stopAfterFirst);
        }

        var results = new List<DotNetRegexMatch>();
        var stopwatch = Stopwatch.StartNew();
        var match = execution.Regex.Match(input);

        while (match.Success)
        {
            ThrowIfRegexBudgetElapsed(execution.Timeout, stopwatch);

            if (TryGetCalloutControlEvent(match, execution, out var controlEvent))
            {
                if (IsFatalCalloutEvent(controlEvent))
                {
                    throw FatalCalloutError(controlEvent);
                }

                if (match.Index >= input.Length)
                {
                    break;
                }

                match = execution.Regex.Match(input, AdvanceCodePoint(input, match.Index));
                continue;
            }

            // jq/Oniguruma only exposes UTF-8 codepoint boundaries. Filtering boundaries also
            // prevents .NET's zero-width iteration from reporting a position inside a surrogate pair.
            var endsAtBoundary = IsCodePointBoundary(input, match.Index + match.Length);
            if ((!execution.IgnoreEmpty || match.Length != 0) &&
                IsCodePointBoundary(input, match.Index) &&
                endsAtBoundary)
            {
                results.Add(match);
                if (stopAfterFirst)
                {
                    break;
                }
            }

            match = match.NextMatch();
        }

        if (!stopAfterFirst)
        {
            ExpandByteWiseZeroWidthMatches(input, execution, results, stopwatch);
        }

        return results;
    }

    private static List<DotNetRegexMatch> FindSkipAwareRawMatches(
        string input,
        RegexExecution execution,
        bool stopAfterFirst)
    {
        var results = new List<DotNetRegexMatch>();
        var searchStart = 0;
        var stopwatch = Stopwatch.StartNew();
        while (searchStart <= input.Length)
        {
            var candidateStart = searchStart;
            DotNetRegexMatch? accepted = null;
            while (candidateStart <= input.Length)
            {
                ThrowIfRegexBudgetElapsed(execution.Timeout, stopwatch);
                var candidate = execution.AnchoredRegex!.Match(input, candidateStart);
                if (candidate.Success && candidate.Index == candidateStart)
                {
                    if (TryGetCalloutControlEvent(candidate, execution, out var controlEvent))
                    {
                        if (IsFatalCalloutEvent(controlEvent))
                        {
                            throw FatalCalloutError(controlEvent);
                        }
                    }
                    else if ((!execution.IgnoreEmpty || candidate.Length != 0) &&
                             IsCodePointBoundary(input, candidate.Index + candidate.Length))
                    {
                        accepted = candidate;
                        break;
                    }
                }

                var nextCandidate = candidateStart < input.Length
                    ? AdvanceCodePoint(input, candidateStart)
                    : input.Length + 1;
                foreach (var probe in execution.SkipProbes)
                {
                    var skip = probe.Match(input, candidateStart);
                    if (!skip.Success || skip.Index != candidateStart)
                    {
                        continue;
                    }

                    foreach (var callout in execution.Callouts.Where(
                                 definition => definition.Kind == CalloutKind.Skip))
                    {
                        var group = skip.Groups[CalloutEventName(callout.Id, "skip")];
                        if (group.Success)
                        {
                            nextCandidate = Math.Max(nextCandidate, group.Index);
                        }
                    }
                }

                candidateStart = nextCandidate;
            }

            if (accepted is null)
            {
                break;
            }

            results.Add(accepted);
            if (stopAfterFirst || accepted.Index + accepted.Length >= input.Length && accepted.Length == 0)
            {
                break;
            }

            searchStart = accepted.Length == 0
                ? AdvanceCodePoint(input, accepted.Index)
                : accepted.Index + accepted.Length;
        }

        if (!stopAfterFirst)
        {
            ExpandByteWiseZeroWidthMatches(input, execution, results, stopwatch);
        }

        return results;
    }

    private static bool TryGetCalloutControlEvent(
        DotNetRegexMatch match,
        RegexExecution execution,
        out CalloutDefinition definition)
    {
        foreach (var candidate in execution.Callouts)
        {
            var eventKind = HasInvalidRuntimeCalloutArgument(candidate)
                ? "invalid"
                : candidate.Kind == CalloutKind.Error && CalloutErrorCode(candidate) != -1
                    ? "error"
                    : "mismatch";
            if ((candidate.Kind is CalloutKind.Error or CalloutKind.Mismatch ||
                 HasInvalidRuntimeCalloutArgument(candidate)) &&
                match.Groups[CalloutEventName(candidate.Id, eventKind)].Success)
            {
                definition = candidate;
                return true;
            }
        }

        definition = default!;
        return false;
    }

    private static bool IsFatalCalloutEvent(CalloutDefinition definition) =>
        HasInvalidRuntimeCalloutArgument(definition) ||
        definition.Kind == CalloutKind.Error && CalloutErrorCode(definition) != -1;

    private static JqRuntimeException FatalCalloutError(CalloutDefinition definition) =>
        HasInvalidRuntimeCalloutArgument(definition)
            ? new JqRuntimeException("Regex failure: invalid callout arg")
            : CalloutError(CalloutErrorCode(definition));

    private static int CalloutErrorCode(CalloutDefinition definition)
    {
        if (definition.Arguments.Length == 0)
        {
            return -3;
        }

        _ = OnigurumaCalloutArgumentParser.TryParseLong(definition.Arguments[0], out var code);
        return unchecked((int)code);
    }

    private static JqRuntimeException CalloutError(int code)
    {
        if (code >= 0 || OnigurumaCalloutErrorData.NeedsParameter(code))
        {
            code = -230;
        }

        return new JqRuntimeException("Regex failure: " + OnigurumaCalloutErrorData.Format(code));
    }

    private static void ExpandByteWiseZeroWidthMatches(
        string input,
        RegexExecution execution,
        List<DotNetRegexMatch> matches,
        Stopwatch stopwatch)
    {
        if (matches.Count == 0)
        {
            return;
        }

        if (execution.OriginalPattern.Equals("\\y", StringComparison.Ordinal))
        {
            ExpandTextSegmentBoundaryContinuationMatches(input, execution, matches, stopwatch);
            return;
        }

        if (execution.OriginalPattern.Equals("\\Y", StringComparison.Ordinal))
        {
            return;
        }

        if (ContainsEffectivePositiveLookBehind(execution.OriginalPattern))
        {
            ExpandUtf8ContinuationLookBehindMatches(input, execution, matches, stopwatch);
            return;
        }

        if (!HasMultibyteEmptyMatchGap(input, matches))
        {
            return;
        }

        // jq's f_match() advances `start` by one raw UTF-8 byte after a zero-width
        // global match. Oniguruma consequently reports another empty match at every
        // continuation byte when the expression can match empty without depending on
        // an anchor or surrounding text. .NET advances over UTF-16 positions instead.
        // Probe an ordinary interior position so ^, $, and context-only lookarounds do
        // not cause us to duplicate their boundary matches.
        var remainingTimeout = RemainingTimeout(execution.Timeout, stopwatch);
        var probeRegex = new DotNetRegex(
            execution.TranslatedPattern,
            execution.Options,
            remainingTimeout);
        var probe = probeRegex.Match("\0\0", 1);
        if ((!probe.Success || probe.Index != 1 || probe.Length != 0) &&
            !ContainsPerlNgWordBoundary(execution.OriginalPattern))
        {
            return;
        }

        ThrowIfRegexBudgetElapsed(execution.Timeout, stopwatch);
        var expanded = new List<DotNetRegexMatch>(matches.Count);
        expanded.Add(matches[0]);

        for (var index = 1; index < matches.Count; index++)
        {
            var previous = matches[index - 1];
            var current = matches[index];
            var extraEmptyMatches = 0;
            if (previous.Length == 0 &&
                previous.Index < input.Length &&
                AdvanceCodePoint(input, previous.Index) == current.Index)
            {
                extraEmptyMatches = Encoding.UTF8.GetByteCount(
                    input.AsSpan(previous.Index, current.Index - previous.Index)) - 1;
            }

            if (extraEmptyMatches > 0)
            {
                var emptyMatch = current.Length == 0
                    ? current
                    : FindSyntheticEmptyMatch(input, current.Index, probeRegex);
                if (emptyMatch is not null)
                {
                    for (var repetition = 0; repetition < extraEmptyMatches; repetition++)
                    {
                        expanded.Add(emptyMatch);
                    }
                }
            }

            expanded.Add(current);
        }

        var last = matches[^1];
        if (last.Length == 0 &&
            last.Index < input.Length &&
            AdvanceCodePoint(input, last.Index) == input.Length)
        {
            var extraTailMatches = Encoding.UTF8.GetByteCount(input.AsSpan(last.Index)) - 1;
            var emptyAtEnd = extraTailMatches > 0
                ? FindSyntheticEmptyMatch(input, input.Length, probeRegex)
                : null;
            if (emptyAtEnd is not null)
            {
                for (var repetition = 0; repetition < extraTailMatches; repetition++)
                {
                    expanded.Add(emptyAtEnd);
                }
            }
        }

        ThrowIfRegexBudgetElapsed(execution.Timeout, stopwatch);
        matches.Clear();
        matches.AddRange(expanded);
    }

    private static void ExpandTextSegmentBoundaryContinuationMatches(
        string input,
        RegexExecution execution,
        List<DotNetRegexMatch> matches,
        Stopwatch stopwatch)
    {
        var remainingTimeout = RemainingTimeout(execution.Timeout, stopwatch);
        var emptyRegex = new DotNetRegex(string.Empty, execution.Options, remainingTimeout);
        var expanded = new List<DotNetRegexMatch>(matches.Count);
        for (var index = 0; index < matches.Count; index++)
        {
            var current = matches[index];
            expanded.Add(current);
            if (index + 1 >= matches.Count || current.Length != 0)
            {
                continue;
            }

            var next = matches[index + 1];
            if (current.Index >= next.Index || current.Index >= input.Length)
            {
                continue;
            }

            var firstScalarEnd = AdvanceCodePoint(input, current.Index);
            var continuationCount = Encoding.UTF8.GetByteCount(
                input.AsSpan(current.Index, firstScalarEnd - current.Index)) - 1;
            if (continuationCount <= 0)
            {
                continue;
            }

            var synthetic = emptyRegex.Match(input, firstScalarEnd);
            for (var repetition = 0; repetition < continuationCount; repetition++)
            {
                expanded.Add(synthetic);
            }
        }

        ThrowIfRegexBudgetElapsed(execution.Timeout, stopwatch);
        matches.Clear();
        matches.AddRange(expanded);
    }

    private static void ExpandUtf8ContinuationLookBehindMatches(
        string input,
        RegexExecution execution,
        List<DotNetRegexMatch> matches,
        Stopwatch stopwatch)
    {
        var remainingTimeout = RemainingTimeout(execution.Timeout, stopwatch);
        var emptyAtStart = new DotNetRegex(string.Empty, execution.Options, remainingTimeout).Match(input, 0);
        var expanded = new List<DotNetRegexMatch>(matches.Count * 2);
        expanded.Add(matches[0]);
        for (var index = 1; index < matches.Count; index++)
        {
            var previous = matches[index - 1];
            var current = matches[index];
            if (previous.Length == 0 &&
                current.Length == 0 &&
                previous.Index < input.Length &&
                AdvanceCodePoint(input, previous.Index) == current.Index &&
                Encoding.UTF8.GetByteCount(
                    input.AsSpan(previous.Index, current.Index - previous.Index)) > 1)
            {
                // jq restarts Oniguruma one raw byte after an empty match. A successful
                // look-behind at the first UTF-8 continuation byte is surfaced by jq's
                // byte-to-scalar conversion as offset zero, once per multibyte scalar.
                expanded.Add(emptyAtStart);
            }

            expanded.Add(current);
        }

        ThrowIfRegexBudgetElapsed(execution.Timeout, stopwatch);
        matches.Clear();
        matches.AddRange(expanded);
    }

    private static bool ContainsEffectivePositiveLookBehind(string pattern)
    {
        // An unconditional empty first alternative wins before a later look-behind,
        // so its continuation-byte duplicates retain the ordinary next-scalar offset.
        if (pattern.StartsWith('|'))
        {
            return false;
        }

        var inClass = false;
        for (var index = 0; index + 3 < pattern.Length; index++)
        {
            if (pattern[index] == '\\')
            {
                index++;
            }
            else if (pattern[index] == '[')
            {
                inClass = true;
            }
            else if (pattern[index] == ']')
            {
                inClass = false;
            }
            else if (!inClass && pattern.AsSpan(index).StartsWith("(?<=", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static DotNetRegexMatch? FindSyntheticEmptyMatch(
        string input,
        int charIndex,
        DotNetRegex regex)
    {
        var probeInput = charIndex == input.Length
            ? input + "\0"
            : input[..charIndex] + "\0" + input[AdvanceCodePoint(input, charIndex)..];
        var match = regex.Match(probeInput, charIndex);
        return match.Success && match.Index == charIndex && match.Length == 0
            ? match
            : null;
    }

    private static bool HasMultibyteEmptyMatchGap(
        string input,
        List<DotNetRegexMatch> matches)
    {
        for (var index = 1; index < matches.Count; index++)
        {
            var previous = matches[index - 1];
            var current = matches[index];
            if (previous.Length == 0 &&
                previous.Index < input.Length &&
                AdvanceCodePoint(input, previous.Index) == current.Index &&
                Encoding.UTF8.GetByteCount(
                    input.AsSpan(previous.Index, current.Index - previous.Index)) > 1)
            {
                return true;
            }
        }

        var last = matches[^1];
        if (last.Length == 0 &&
            last.Index < input.Length &&
            AdvanceCodePoint(input, last.Index) == input.Length &&
            Encoding.UTF8.GetByteCount(input.AsSpan(last.Index)) > 1)
        {
            return true;
        }

        return false;
    }

    private static List<DotNetRegexMatch> FindLongestRawMatches(
        string input,
        RegexExecution execution,
        bool stopAfterFirst)
    {
        // ONIG_OPTION_FIND_LONGEST compares every match in the remaining search range. Its
        // length is a pointer difference, so UTF-8 byte length (not jq's displayed scalar
        // length) decides the winner. System.Text.RegularExpressions has no POSIX/find-longest
        // mode. Pinning the end with a zero-width suffix assertion lets its ordinary engine
        // expose each possible candidate while retaining the original input context for
        // anchors and lookarounds.
        var results = new List<DotNetRegexMatch>();
        var searchStart = 0;
        var stopwatch = Stopwatch.StartNew();

        while (searchStart <= input.Length)
        {
            var match = FindLongestRawMatch(input, searchStart, execution, stopwatch);
            if (match is null)
            {
                break;
            }

            results.Add(match);
            if (stopAfterFirst)
            {
                break;
            }

            if (match.Length != 0)
            {
                searchStart = match.Index + match.Length;
                continue;
            }

            if (match.Index == input.Length)
            {
                break;
            }

            searchStart = AdvanceCodePoint(input, match.Index);
        }

        if (!stopAfterFirst)
        {
            ExpandByteWiseZeroWidthMatches(input, execution, results, stopwatch);
        }

        return results;
    }

    private static List<DotNetRegexMatch> FindLongestSkipAwareRawMatches(
        string input,
        RegexExecution execution,
        bool stopAfterFirst)
    {
        var results = new List<DotNetRegexMatch>();
        var searchStart = 0;
        var stopwatch = Stopwatch.StartNew();
        while (searchStart <= input.Length)
        {
            DotNetRegexMatch? best = null;
            var bestUtf8Length = -1;
            var candidateStart = searchStart;
            while (candidateStart <= input.Length)
            {
                var controlCandidate = execution.AnchoredRegex!.Match(input, candidateStart);
                if (controlCandidate.Success && controlCandidate.Index == candidateStart &&
                    TryGetCalloutControlEvent(controlCandidate, execution, out var leadingControlEvent))
                {
                    if (IsFatalCalloutEvent(leadingControlEvent))
                    {
                        throw FatalCalloutError(leadingControlEvent);
                    }

                    candidateStart = candidateStart < input.Length
                        ? AdvanceCodePoint(input, candidateStart)
                        : input.Length + 1;
                    continue;
                }

                for (var end = candidateStart; end <= input.Length; end = AdvanceCodePoint(input, end))
                {
                    var remainingTimeout = RemainingTimeout(execution.Timeout, stopwatch);
                    var remainingChars = input.Length - end;
                    var exactEndPattern = "\\G(?:" + execution.TranslatedPattern +
                        ")(?=(?:[\\s\\S]{" +
                        remainingChars.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                        "})\\z)";
                    var exactEndRegex = new DotNetRegex(
                        exactEndPattern,
                        execution.Options,
                        remainingTimeout);
                    var candidate = exactEndRegex.Match(input, candidateStart);
                    if (candidate.Success && candidate.Index == candidateStart &&
                        TryGetCalloutControlEvent(candidate, execution, out var controlEvent))
                    {
                        if (IsFatalCalloutEvent(controlEvent))
                        {
                            throw FatalCalloutError(controlEvent);
                        }

                        break;
                    }

                    if (candidate.Success && candidate.Index == candidateStart &&
                        (!execution.IgnoreEmpty || candidate.Length != 0) &&
                        IsCodePointBoundary(input, candidate.Index + candidate.Length))
                    {
                        var candidateValue = input.Substring(EffectiveIndex(candidate), EffectiveLength(candidate));
                        var utf8Length = Encoding.UTF8.GetByteCount(candidateValue);
                        if (utf8Length > bestUtf8Length)
                        {
                            best = candidate;
                            bestUtf8Length = utf8Length;
                        }
                    }

                    if (end == input.Length)
                    {
                        break;
                    }
                }

                var nextCandidate = candidateStart < input.Length
                    ? AdvanceCodePoint(input, candidateStart)
                    : input.Length + 1;
                foreach (var probe in execution.SkipProbes)
                {
                    var skip = probe.Match(input, candidateStart);
                    if (!skip.Success || skip.Index != candidateStart)
                    {
                        continue;
                    }

                    foreach (var callout in execution.Callouts.Where(
                                 definition => definition.Kind == CalloutKind.Skip))
                    {
                        var group = skip.Groups[CalloutEventName(callout.Id, "skip")];
                        if (group.Success)
                        {
                            nextCandidate = Math.Max(nextCandidate, group.Index);
                        }
                    }
                }

                candidateStart = nextCandidate;
            }

            if (best is null)
            {
                break;
            }

            results.Add(best);
            if (stopAfterFirst)
            {
                break;
            }

            if (best.Length == 0)
            {
                if (best.Index == input.Length)
                {
                    break;
                }

                searchStart = AdvanceCodePoint(input, best.Index);
            }
            else
            {
                searchStart = best.Index + best.Length;
            }
        }

        if (!stopAfterFirst)
        {
            ExpandByteWiseZeroWidthMatches(input, execution, results, stopwatch);
        }

        return results;
    }

    private static DotNetRegexMatch? FindLongestRawMatch(
        string input,
        int searchStart,
        RegexExecution execution,
        Stopwatch stopwatch)
    {
        DotNetRegexMatch? best = null;
        var bestUtf8Length = -1;

        for (var end = searchStart; end <= input.Length; end = AdvanceCodePoint(input, end))
        {
            var remainingTimeout = RemainingTimeout(execution.Timeout, stopwatch);
            var remainingChars = input.Length - end;
            var exactEndPattern = "(?:" + execution.TranslatedPattern +
                ")(?=(?:[\\s\\S]{" +
                remainingChars.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                "})\\z)";
            var exactEndRegex = new DotNetRegex(exactEndPattern, execution.Options, remainingTimeout);
            var candidate = exactEndRegex.Match(input, searchStart);

            while (candidate.Success &&
                   (!IsCodePointBoundary(input, candidate.Index) ||
                    candidate.Index + candidate.Length != end))
            {
                candidate = candidate.NextMatch();
            }

            if (!candidate.Success ||
                (execution.IgnoreEmpty && candidate.Length == 0) ||
                !IsCodePointBoundary(input, candidate.Index + candidate.Length))
            {
                if (end == input.Length)
                {
                    break;
                }

                continue;
            }

            var candidateIndex = EffectiveIndex(candidate);
            var candidateValue = input.Substring(candidateIndex, EffectiveLength(candidate));
            var utf8Length = Encoding.UTF8.GetByteCount(candidateValue);
            if (utf8Length > bestUtf8Length ||
                (utf8Length == bestUtf8Length &&
                 (best is null || candidateIndex < EffectiveIndex(best))))
            {
                best = candidate;
                bestUtf8Length = utf8Length;
            }

            if (end == input.Length)
            {
                break;
            }
        }

        return best;
    }

    private static TimeSpan RemainingTimeout(TimeSpan timeout, Stopwatch stopwatch)
    {
        if (timeout == System.Threading.Timeout.InfiniteTimeSpan)
        {
            return timeout;
        }

        var remaining = timeout - stopwatch.Elapsed;
        if (remaining <= TimeSpan.Zero)
        {
            throw RegexTimeout();
        }

        return remaining;
    }

    private static void ThrowIfRegexBudgetElapsed(TimeSpan timeout, Stopwatch stopwatch)
    {
        if (timeout != System.Threading.Timeout.InfiniteTimeSpan && stopwatch.Elapsed > timeout)
        {
            throw RegexTimeout();
        }
    }

    private static int AdvanceCodePoint(string input, int index) =>
        index < input.Length &&
        char.IsHighSurrogate(input[index]) &&
        index + 1 < input.Length &&
        char.IsLowSurrogate(input[index + 1])
            ? index + 2
            : index + 1;

    private static int EffectiveIndex(DotNetRegexMatch match)
    {
        var keep = match.Groups[KeepCaptureName];
        return keep.Success ? keep.Index : match.Index;
    }

    private static int EffectiveLength(DotNetRegexMatch match) =>
        match.Index + match.Length - EffectiveIndex(match);

    private static JqRegexMatch ConvertMatch(
        string input,
        DotNetRegexMatch match,
        RegexExecution execution)
    {
        var effectiveIndex = EffectiveIndex(match);
        var effectiveLength = EffectiveLength(match);
        var captures = new List<JqRegexCapture>(match.Groups.Count - 1);
        var zeroWidthOffset = effectiveLength == 0
            ? CountCodePoints(input, effectiveIndex)
            : -1;
        for (var groupNumber = 1; groupNumber < match.Groups.Count; groupNumber++)
        {
            var group = match.Groups[groupNumber];
            var runtimeName = execution.Regex.GroupNameFromNumber(groupNumber);
            if (runtimeName.Equals(IgnoreEmptyCaptureName, StringComparison.Ordinal) ||
                runtimeName.Equals(KeepCaptureName, StringComparison.Ordinal) ||
                runtimeName.StartsWith(AbsentTailCapturePrefix, StringComparison.Ordinal) ||
                runtimeName.StartsWith(RegexConditionCapturePrefix, StringComparison.Ordinal) ||
                runtimeName.StartsWith(CalloutCapturePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var name = execution.CaptureNames.TryGetValue(runtimeName, out var displayName)
                ? displayName
                : null;

            captures.Add(group.Success && effectiveLength == 0
                ? new JqRegexCapture(zeroWidthOffset, 0, string.Empty, name)
                : group.Success
                ? new JqRegexCapture(
                    CountCodePoints(input, group.Index),
                    CountCodePoints(input, group.Index, group.Length),
                    group.Value,
                    name)
                : new JqRegexCapture(-1, 0, null, name));
        }

        return new JqRegexMatch(
            CountCodePoints(input, effectiveIndex),
            CountCodePoints(input, effectiveIndex, effectiveLength),
            input.Substring(effectiveIndex, effectiveLength),
            captures);
    }

    private static string Replace(
        string input,
        RegexExecution execution,
        Func<IReadOnlyDictionary<string, string?>, string> replacement)
    {
        try
        {
            var matches = FindRawMatches(input, execution, stopAfterFirst: !execution.Global);
            if (matches.Count == 0)
            {
                return input;
            }

            var result = new StringBuilder(input.Length);
            var previous = 0;
            foreach (var rawMatch in matches)
            {
                var effectiveIndex = EffectiveIndex(rawMatch);
                var effectiveLength = EffectiveLength(rawMatch);
                result.Append(input, previous, effectiveIndex - previous);
                var jqMatch = ConvertMatch(input, rawMatch, execution);
                result.Append(replacement(NamedCaptures(jqMatch)) ??
                    throw new JqRuntimeException("jq substitution produced a null string"));
                previous = effectiveIndex + effectiveLength;
            }

            result.Append(input, previous, input.Length - previous);
            return result.ToString();
        }
        catch (DotNetRegexTimeoutException)
        {
            throw RegexTimeout();
        }
    }

    private static ReadOnlyDictionary<string, string?> NamedCaptures(JqRegexMatch match)
    {
        var captures = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var capture in match.Captures)
        {
            if (capture.Name is not null)
            {
                captures[capture.Name] = capture.String;
            }
        }

        return new ReadOnlyDictionary<string, string?>(captures);
    }

    private static RegexExecution CreateExecution(
        string input,
        string pattern,
        string? modifiers,
        TimeSpan? timeout,
        bool forceGlobal)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ValidateOnigurumaCompileConstraints(pattern);
        // Preserve the pinned Perl-NG parser's diagnostics before lowering to
        // System.Text.RegularExpressions. Runner-bound patterns already take
        // this pass in TryRunCalloutEvents; ordinary patterns need it as well.
        ValidateCalloutPatternCompilation(pattern, modifiers);
        var originalPattern = pattern;
        pattern = NormalizePerlNgCharacterClassEscapes(pattern);
        var flags = ParseModifiers(modifiers);
        var global = flags.Global || forceGlobal;
        var options = DotNetRegexOptions.CultureInvariant;
        if (flags.IgnoreCase)
        {
            options |= DotNetRegexOptions.IgnoreCase;
        }

        if (flags.Extended)
        {
            options |= DotNetRegexOptions.IgnorePatternWhitespace;
        }

        // jq's m is Oniguruma's MULTILINE, where dot matches a newline. The equivalent
        // .NET option is named Singleline. jq's s changes anchor behavior, which matches
        // .NET's default ^/$ behavior for the supported cases and needs no option here.
        if (flags.DotMatchesNewline)
        {
            options |= DotNetRegexOptions.Singleline;
        }

        // Resolve calls while capture numbering still contains only source-level groups.
        // Absent-range lowering introduces private named captures which must not take part
        // in Oniguruma's lexical capture numbering.
        var callouts = PrepareCallouts(pattern, flags.Extended);
        var normalizedPattern = TranslateSubexpressionCalls(
            callouts.Pattern,
            MaximumSubexpressionCallNesting - 1);
        RejectUnsafeCalloutEventContexts(normalizedPattern, callouts.Definitions);
        normalizedPattern = TranslateRegexConditionals(normalizedPattern);
        normalizedPattern = TranslateAbsentRepeaters(normalizedPattern);
        normalizedPattern = TranslatePossessiveQuantifiers(normalizedPattern, flags.Extended);
        var calloutPattern = normalizedPattern;
        normalizedPattern = GuardCalloutControlEvents(
            normalizedPattern,
            callouts.Definitions
                .Where(definition =>
                    definition.Kind is CalloutKind.Error or CalloutKind.Mismatch ||
                    HasInvalidRuntimeCalloutArgument(definition))
                .Select(definition => definition.Id)
                .ToHashSet());
        var translated = TranslatePattern(
            normalizedPattern,
            input,
            flags.Extended,
            flags.DotMatchesNewline,
            flags.IgnoreCase);
        var inputScalarLength = CountCodePoints(input, input.Length);
        translated = LowerCallouts(translated, callouts, inputScalarLength);
        // The source-shaped \X lowering expands into a substantial GB3-GB13
        // expression. On long managed inputs the runtime's compiled runner avoids
        // spending the caller's match timeout repeatedly interpreting that fixed
        // expression. Keep short matches on the interpreter so one-shot jq regexes
        // do not pay compilation cost; NativeAOT remains on the interpreter
        // because dynamic-code compilation is unavailable.
        if (translated.UsesClusterToken &&
            input.Length >= MinimumCompiledClusterInputLength &&
            RuntimeFeature.IsDynamicCodeCompiled)
        {
            options |= DotNetRegexOptions.Compiled;
        }
        if (flags.IgnoreEmpty)
        {
            // ONIG_OPTION_FIND_NOT_EMPTY participates in backtracking: an empty result is
            // rejected and the engine may select a non-empty alternative at the same start.
            // Capture the complete remaining suffix at the match start, then reject only a
            // result whose end still sees that identical suffix. This is a zero-allocation
            // position comparison expressed in .NET regex grammar and therefore preserves
            // the engine's original greediness and alternative order.
            translated = translated with
            {
                Pattern = "(?=(?<" + IgnoreEmptyCaptureName + ">[\\s\\S]*\\z))(?:" +
                    translated.Pattern + ")(?!\\k<" + IgnoreEmptyCaptureName + ">\\z)",
            };
        }
        try
        {
            var effectiveTimeout = timeout ?? DefaultTimeout;
            var skipDefinitions = callouts.Definitions
                .Where(definition => definition.Kind == CalloutKind.Skip)
                .ToArray();
            var hasControlEvents = callouts.Definitions.Any(definition =>
                definition.Kind is CalloutKind.Error or CalloutKind.Mismatch ||
                HasInvalidRuntimeCalloutArgument(definition));
            DotNetRegex? anchoredRegex = null;
            var skipProbes = new List<DotNetRegex>(skipDefinitions.Length);
            if (skipDefinitions.Length != 0 || hasControlEvents)
            {
                anchoredRegex = new DotNetRegex(
                    "\\G(?:" + translated.Pattern + ')',
                    options,
                    effectiveTimeout);
            }

            if (skipDefinitions.Length != 0)
            {
                foreach (var skip in skipDefinitions)
                {
                    var probeSource = GuardCalloutControlEvents(calloutPattern, [skip.Id]);
                    var probeTranslation = TranslatePattern(
                        probeSource,
                        input,
                        flags.Extended,
                        flags.DotMatchesNewline,
                        flags.IgnoreCase);
                    probeTranslation = LowerCallouts(
                        probeTranslation,
                        callouts,
                        inputScalarLength,
                        skip.Id);
                    skipProbes.Add(new DotNetRegex(
                        "\\G(?:" + probeTranslation.Pattern + ')',
                        options,
                        effectiveTimeout));
                }
            }

            return new RegexExecution(
                new DotNetRegex(translated.Pattern, options, effectiveTimeout),
                global,
                flags.IgnoreEmpty,
                flags.FindLongest,
                translated.Pattern,
                originalPattern,
                options,
                effectiveTimeout,
                translated.CaptureNames,
                callouts.Definitions,
                anchoredRegex,
                skipProbes);
        }
        catch (ArgumentException exception)
        {
            throw new JqRuntimeException("Regex failure: " + exception.Message);
        }
    }

    private static string NormalizePerlNgCharacterClassEscapes(string pattern)
    {
        var normalized = new StringBuilder(pattern.Length);
        for (var index = 0; index < pattern.Length; index++)
        {
            var current = pattern[index];
            if (current == '\\' && index + 1 < pattern.Length)
            {
                if (pattern[index + 1] == 'Q' && OnigurumaCalloutAtomMatcher.TryParseEscape(
                        pattern,
                        index,
                        out var quote))
                {
                    normalized.Append(pattern, index, quote.NextPatternIndex - index);
                    index = quote.NextPatternIndex - 1;
                    continue;
                }

                if (pattern[index + 1] == 'c' && OnigurumaCalloutAtomMatcher.TryParseEscape(
                        pattern,
                        index,
                        out var control))
                {
                    var rune = Rune.GetRuneAt(control.Token.Literal!, 0);
                    normalized.Append("\\x{").Append(
                        rune.Value.ToString("X", CultureInfo.InvariantCulture)).Append('}');
                    index = control.NextPatternIndex - 1;
                    continue;
                }

                normalized.Append(current).Append(pattern[index + 1]);
                index++;
                continue;
            }

            if (current == '[')
            {
                var classEnd = FindCharacterClassEnd(pattern, index);
                normalized.Append(NormalizePerlNgCharacterClass(pattern, index, classEnd));
                index = classEnd;
                continue;
            }

            normalized.Append(current);
        }

        return normalized.ToString();
    }

    private static string NormalizePerlNgCharacterClass(string pattern, int open, int close)
    {
        var result = new StringBuilder(close - open + 1).Append('[');
        var index = open + 1;
        var negated = index < close && pattern[index] == '^';
        if (negated)
        {
            result.Append('^');
            index++;
        }

        var hasMember = false;
        var sawRadix = false;
        while (index < close)
        {
            if (pattern[index] != '\\' || index + 1 >= close)
            {
                result.Append(pattern[index++]);
                hasMember = true;
                continue;
            }

            var escaped = pattern[index + 1];
            if (escaped is 'u' or 'C' ||
                (escaped == 'P' && !TryReadUnicodeProperty(pattern, index + 2, out _, out _)))
            {
                result.Append(escaped);
                hasMember = true;
                index += 2;
                continue;
            }

            if (escaped is 'x' or 'o' && index + 2 < close && pattern[index + 2] == '{')
            {
                var parsed = OnigurumaRadixEscape.ParseBrace(
                    pattern,
                    index + 2,
                    escaped == 'x' ? 16 : 8,
                    inCharacterClass: true);
                if (parsed.Matched)
                {
                    sawRadix = true;
                    foreach (var range in parsed.Ranges)
                    {
                        hasMember |= AppendCharacterClassRange(result, range.Start, range.End);
                    }

                    index = parsed.End + 1;
                    continue;
                }

                // No initial digit restores the brace and makes the escape ineffective.
                result.Append(escaped);
                hasMember = true;
                index += 2;
                continue;
            }

            if (escaped == 'x')
            {
                var fixedEnd = index + 2;
                var scalar = 0;
                var digits = 0;
                while (fixedEnd < close && digits < 2 &&
                    TryReadRadixDigit(pattern[fixedEnd], 16, out var digit))
                {
                    scalar = scalar * 16 + digit;
                    fixedEnd++;
                    digits++;
                }

                if (scalar <= 0x7F)
                {
                    AppendCharacterClassScalar(result, scalar);
                    hasMember = true;
                }
                else
                {
                    // A standalone high crude byte cannot match jq's valid UTF-8 input.
                    sawRadix = true;
                }

                index = fixedEnd;
                continue;
            }

            if (escaped == 'o')
            {
                result.Append('o');
                hasMember = true;
                index += 2;
                continue;
            }

            result.Append('\\').Append(escaped);
            hasMember = true;
            index += 2;
        }

        if (sawRadix && !hasMember)
        {
            return negated ? "\\O" : "(?!)";
        }

        return result.Append(']').ToString();
    }

    private static bool AppendCharacterClassRange(StringBuilder result, uint start, uint end)
    {
        var appended = false;
        appended |= AppendCharacterClassRangeIntersection(result, start, end, 0, 0xD7FF);
        appended |= AppendCharacterClassRangeIntersection(result, start, end, 0xE000, 0x10FFFF);
        return appended;
    }

    private static bool AppendCharacterClassRangeIntersection(
        StringBuilder result,
        uint start,
        uint end,
        uint validStart,
        uint validEnd)
    {
        var intersectionStart = Math.Max(start, validStart);
        var intersectionEnd = Math.Min(end, validEnd);
        if (intersectionStart > intersectionEnd)
        {
            return false;
        }

        AppendCharacterClassScalar(result, (int)intersectionStart);
        if (intersectionStart != intersectionEnd)
        {
            result.Append('-');
            AppendCharacterClassScalar(result, (int)intersectionEnd);
        }

        return true;
    }

    private static void AppendCharacterClassScalar(StringBuilder result, int scalar)
    {
        if (scalar <= char.MaxValue && (char)scalar is '\\' or ']' or '-' or '^')
        {
            result.Append('\\');
        }

        result.Append(new Rune(scalar).ToString());
    }

    private static void ValidateOnigurumaCompileConstraints(string pattern)
    {
        for (var index = 0; index < pattern.Length; index++)
        {
            var current = pattern[index];
            if (current == '\\')
            {
                if (index + 1 < pattern.Length)
                {
                    var escaped = pattern[++index];
                    if (escaped == 'Q')
                    {
                        var quoteEnd = pattern.IndexOf("\\E", index + 1, StringComparison.Ordinal);
                        index = quoteEnd < 0 ? pattern.Length : quoteEnd + 1;
                        continue;
                    }

                    if (escaped == 'c' && OnigurumaCalloutAtomMatcher.TryParseEscape(
                            pattern,
                            index - 1,
                            out var control))
                    {
                        index = control.NextPatternIndex - 1;
                        continue;
                    }

                    if (escaped is 'A' or 'Z' or 'z' or 'b' or 'B' or 'G' or 'y' or 'Y' &&
                        IsRepeatQuantifierAt(pattern, index + 1))
                    {
                        throw new JqRuntimeException("Regex failure: target of repeat operator is invalid");
                    }
                }

                continue;
            }

            if (current == '[')
            {
                index = FindPerlNgCharacterClassEnd(pattern, index);
                continue;
            }

            if (current is '^' or '$' && IsRepeatQuantifierAt(pattern, index + 1))
            {
                throw new JqRuntimeException("Regex failure: target of repeat operator is invalid");
            }

            if (current != '(' || index + 2 >= pattern.Length || pattern[index + 1] != '?')
            {
                continue;
            }

            var isLookAhead = pattern[index + 2] is '=' or '!';
            var isLookBehind = index + 3 < pattern.Length &&
                pattern[index + 2] == '<' && pattern[index + 3] is '=' or '!';
            if (!isLookAhead && !isLookBehind)
            {
                continue;
            }

            var close = FindMatchingParenthesis(pattern, index);
            if (close < 0)
            {
                continue;
            }

            if (IsRepeatQuantifierAt(pattern, close + 1))
            {
                throw new JqRuntimeException("Regex failure: target of repeat operator is invalid");
            }

            if (isLookBehind)
            {
                ValidateOnigurumaLookBehind(
                    pattern,
                    index + 4,
                    close,
                    negative: pattern[index + 3] == '!');
            }
        }
    }

    private static bool IsRepeatQuantifierAt(string pattern, int index)
    {
        if (index >= pattern.Length)
        {
            return false;
        }

        if (pattern[index] is '*' or '+' or '?')
        {
            return true;
        }

        if (pattern[index] != '{')
        {
            return false;
        }

        var close = pattern.IndexOf('}', index + 1);
        return close > index + 1 && IsIntervalBody(pattern.AsSpan(index + 1, close - index - 1));
    }

    private static void ValidateOnigurumaLookBehind(
        string pattern,
        int start,
        int end,
        bool negative = false)
    {
        if (negative && ContainsCapturingGroup(pattern, start, end))
        {
            throw new JqRuntimeException("Regex failure: invalid pattern in look-behind");
        }

        var branchStart = start;
        var branchWidths = new List<int>();
        var depth = 0;
        var inCharacterClass = false;
        var hasUnknownWidth = false;
        for (var index = start; index <= end; index++)
        {
            if (index == end || (!inCharacterClass && depth == 0 && pattern[index] == '|'))
            {
                if (ContainsVariableLookBehindQuantifier(
                        pattern,
                        branchStart,
                        index) &&
                    !IsDirectReducedLookBehindQuantifier(pattern, branchStart, index))
                {
                    throw new JqRuntimeException("Regex failure: invalid pattern in look-behind");
                }

                if (TryGetSimpleLookBehindWidth(pattern.AsSpan(branchStart, index - branchStart), out var width))
                {
                    if (width > MaximumLookBehindWidth)
                    {
                        throw new JqRuntimeException("Regex failure: invalid pattern in look-behind");
                    }

                    branchWidths.Add(width);
                }
                else
                {
                    hasUnknownWidth = true;
                }

                branchStart = index + 1;
                continue;
            }

            var current = pattern[index];
            if (current == '\\')
            {
                index++;
                continue;
            }

            if (current == '[')
            {
                inCharacterClass = true;
                continue;
            }

            if (current == ']' && inCharacterClass)
            {
                inCharacterClass = false;
                continue;
            }

            if (inCharacterClass)
            {
                continue;
            }

            if (current == '(' && index + 2 < end && pattern[index + 1] == '?' &&
                (pattern[index + 2] is '=' or '!' ||
                 (index + 3 < end && pattern[index + 2] == '<' && pattern[index + 3] is '=' or '!')))
            {
                throw new JqRuntimeException("Regex failure: invalid pattern in look-behind");
            }

            if (current == '(')
            {
                var nestedClose = FindMatchingParenthesis(pattern, index);
                var nestedBodyStart = index + 1;
                if (index + 2 < end && pattern[index + 1] == '?' && pattern[index + 2] is ':' or '>')
                {
                    nestedBodyStart = index + 3;
                }

                if (nestedClose > nestedBodyStart && nestedClose <= end)
                {
                    ValidateOnigurumaLookBehind(pattern, nestedBodyStart, nestedClose);
                }

                depth++;
            }
            else if (current == ')' && depth != 0)
            {
                depth--;
            }
        }

        if (!hasUnknownWidth && branchWidths.Count > 1 && branchWidths.Any(width => width != branchWidths[0]))
        {
            throw new JqRuntimeException("Regex failure: invalid pattern in look-behind");
        }
    }

    private static bool ContainsCapturingGroup(string pattern, int start, int end)
    {
        var inCharacterClass = false;
        for (var index = start; index < end; index++)
        {
            var current = pattern[index];
            if (current == '\\')
            {
                if (index + 1 < end && pattern[index + 1] == 'Q')
                {
                    var quoteEnd = pattern.IndexOf("\\E", index + 2, StringComparison.Ordinal);
                    if (quoteEnd < 0 || quoteEnd >= end)
                    {
                        return false;
                    }

                    index = quoteEnd + 1;
                }
                else
                {
                    index++;
                }

                continue;
            }

            if (current == '[')
            {
                inCharacterClass = true;
                continue;
            }

            if (current == ']' && inCharacterClass)
            {
                inCharacterClass = false;
                continue;
            }

            if (inCharacterClass || current != '(' || index + 1 >= end)
            {
                continue;
            }

            if (pattern.AsSpan(index).StartsWith("(?#", StringComparison.Ordinal))
            {
                var commentEnd = index + 3;
                while (commentEnd < end && pattern[commentEnd] != ')')
                {
                    if (pattern[commentEnd] == '\\' && commentEnd + 1 < end)
                    {
                        commentEnd++;
                    }

                    commentEnd++;
                }

                index = commentEnd;
                continue;
            }

            if (pattern[index + 1] != '?')
            {
                return true;
            }

            if (index + 2 < end &&
                (pattern[index + 2] == '\'' ||
                 pattern[index + 2] == '<' && index + 3 < end &&
                    pattern[index + 3] is not ('=' or '!') ||
                 pattern.AsSpan(index + 2).StartsWith("P<", StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsVariableLookBehindQuantifier(
        string pattern,
        int start,
        int end)
    {
        var inCharacterClass = false;
        for (var index = start; index < end; index++)
        {
            var current = pattern[index];
            if (current == '\\')
            {
                if (index + 1 < end && pattern[index + 1] == 'Q')
                {
                    var quoteEnd = pattern.IndexOf("\\E", index + 2, StringComparison.Ordinal);
                    if (quoteEnd < 0 || quoteEnd >= end)
                    {
                        return false;
                    }

                    index = quoteEnd + 1;
                }
                else
                {
                    index++;
                }

                continue;
            }

            if (current == '[')
            {
                inCharacterClass = true;
                continue;
            }

            if (current == ']' && inCharacterClass)
            {
                inCharacterClass = false;
                continue;
            }

            if (inCharacterClass ||
                current is '*' or '?' && index > start && pattern[index - 1] == '(')
            {
                continue;
            }

            if (current is '*' or '+' or '?')
            {
                return true;
            }

            if (current == '{' && TryReadIntervalQuantifier(
                    pattern,
                    index,
                    end,
                    out _,
                    out var intervalVariable))
            {
                if (intervalVariable)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsDirectReducedLookBehindQuantifier(
        string pattern,
        int start,
        int end)
    {
        while (end - start >= 4 &&
               (pattern.AsSpan(start).StartsWith("(?:", StringComparison.Ordinal) ||
                pattern.AsSpan(start).StartsWith("(?>", StringComparison.Ordinal)) &&
               FindMatchingParenthesis(pattern, start) == end - 1)
        {
            start += 3;
            end--;
        }

        if (!TryReadSimpleLookBehindAtom(pattern, start, end, out var quantifierStart) ||
            quantifierStart >= end)
        {
            return false;
        }

        var quantifierEnd = quantifierStart;
        var variable = false;
        if (pattern[quantifierEnd] is '*' or '+' or '?')
        {
            variable = true;
            quantifierEnd++;
        }
        else if (pattern[quantifierEnd] == '{' && TryReadIntervalQuantifier(
                     pattern,
                     quantifierEnd,
                     end,
                     out quantifierEnd,
                     out variable))
        {
            // TryReadIntervalQuantifier returns the first character after the interval.
        }
        else
        {
            return false;
        }

        if (quantifierEnd < end && pattern[quantifierEnd] is '?' or '+')
        {
            quantifierEnd++;
        }

        return variable && quantifierEnd == end;
    }

    private static bool TryReadSimpleLookBehindAtom(
        string pattern,
        int start,
        int end,
        out int atomEnd)
    {
        atomEnd = start;
        if (start >= end)
        {
            return false;
        }

        if (pattern[start] == '[')
        {
            atomEnd = FindCalloutValidationCharacterClassEnd(pattern, start) + 1;
            return atomEnd <= end;
        }

        if (pattern[start] == '\\')
        {
            if (start + 1 >= end)
            {
                return false;
            }

            atomEnd = start + 2;
            if (pattern[start + 1] is 'p' or 'P' or 'x' or 'o' &&
                atomEnd < end && pattern[atomEnd] == '{')
            {
                var close = pattern.IndexOf('}', atomEnd + 1);
                if (close < 0 || close >= end)
                {
                    return false;
                }

                atomEnd = close + 1;
            }
            else if (pattern[start + 1] == 'k' && atomEnd < end &&
                     pattern[atomEnd] is '<' or '\'')
            {
                var terminator = pattern[atomEnd] == '<' ? '>' : '\'';
                var close = pattern.IndexOf(terminator, atomEnd + 1);
                if (close < 0 || close >= end)
                {
                    return false;
                }

                atomEnd = close + 1;
            }

            return true;
        }

        if (pattern[start] is '(' or ')' or '^' or '$' or '*' or '+' or '?' or '{')
        {
            return false;
        }

        atomEnd = start + 1;
        if (char.IsHighSurrogate(pattern[start]) && atomEnd < end &&
            char.IsLowSurrogate(pattern[atomEnd]))
        {
            atomEnd++;
        }

        return true;
    }

    private static bool TryReadIntervalQuantifier(
        string pattern,
        int open,
        int end,
        out int quantifierEnd,
        out bool variable)
    {
        quantifierEnd = open;
        variable = false;
        var close = pattern.IndexOf('}', open + 1);
        if (close < 0 || close >= end)
        {
            return false;
        }

        var body = pattern.AsSpan(open + 1, close - open - 1);
        if (!IsIntervalBody(body))
        {
            return false;
        }

        var comma = body.IndexOf(',');
        if (comma >= 0)
        {
            var lower = body[..comma];
            var upper = body[(comma + 1)..];
            variable = upper.IsEmpty || lower.IsEmpty || !lower.SequenceEqual(upper);
        }

        quantifierEnd = close + 1;
        return true;
    }

    private static bool TryGetSimpleLookBehindWidth(ReadOnlySpan<char> branch, out int width)
    {
        width = 0;
        for (var index = 0; index < branch.Length;)
        {
            var widthBeforeQuantifier = 0;
            var atomWidth = 0;
            var current = branch[index];
            if (current == '\\')
            {
                if (++index >= branch.Length)
                {
                    return false;
                }

                var escaped = branch[index++];
                if (escaped == 'Q')
                {
                    var quoteEndOffset = branch[index..].IndexOf("\\E", StringComparison.Ordinal);
                    var quoteEnd = quoteEndOffset < 0 ? branch.Length : index + quoteEndOffset;
                    var quotedScalars = 0;
                    for (var quotedIndex = index; quotedIndex < quoteEnd; quotedScalars++)
                    {
                        quotedIndex += char.IsHighSurrogate(branch[quotedIndex]) &&
                            quotedIndex + 1 < quoteEnd && char.IsLowSurrogate(branch[quotedIndex + 1])
                                ? 2
                                : 1;
                    }

                    if (quotedScalars == 0)
                    {
                        return false;
                    }

                    // \Q emits ordinary literal tokens until \E; a following
                    // quantifier applies only to the final quoted scalar.
                    widthBeforeQuantifier = quotedScalars - 1;
                    atomWidth = 1;
                    index = quoteEndOffset < 0 ? branch.Length : quoteEnd + 2;
                }
                else if (escaped is 'A' or 'Z' or 'z' or 'b' or 'B' or 'G' or 'y' or 'Y' or 'K')
                {
                    atomWidth = 0;
                }
                else if (escaped is 'p' or 'P' or 'x' or 'o' &&
                         index < branch.Length && branch[index] == '{')
                {
                    var close = branch[index..].IndexOf('}');
                    if (close < 0)
                    {
                        return false;
                    }

                    index += close + 1;
                    atomWidth = 1;
                }
                else if (escaped is 'R' or 'X' or 'g' or 'k' || char.IsAsciiDigit(escaped))
                {
                    // Newline/grapheme atoms are variable-width. Backreferences and
                    // subexpression calls depend on capture/call context rather than
                    // lexical width.
                    return false;
                }
                else
                {
                    atomWidth = 1;
                }
            }
            else if (current == '[')
            {
                var classEnd = index + 1;
                for (; classEnd < branch.Length; classEnd++)
                {
                    if (branch[classEnd] == '\\')
                    {
                        classEnd++;
                    }
                    else if (branch[classEnd] == ']')
                    {
                        break;
                    }
                }

                if (classEnd >= branch.Length)
                {
                    return false;
                }

                atomWidth = 1;
                index = classEnd + 1;
            }
            else if (current is '(' or ')' or '*' or '+' or '?' or '{' or '}')
            {
                return false;
            }
            else
            {
                atomWidth = current is '^' or '$' ? 0 : 1;
                index++;
                if (atomWidth != 0 && char.IsHighSurrogate(current) && index < branch.Length &&
                    char.IsLowSurrogate(branch[index]))
                {
                    index++;
                }
            }

            var repetitions = 1;
            if (index < branch.Length && branch[index] == '{')
            {
                var close = branch[index..].IndexOf('}');
                if (close <= 1)
                {
                    return false;
                }

                close += index;
                var interval = branch[(index + 1)..close];
                var comma = interval.IndexOf(',');
                var lower = comma < 0 ? interval : interval[..comma];
                var upper = comma < 0 ? interval : interval[(comma + 1)..];
                if (lower.IsEmpty || upper.IsEmpty || !lower.SequenceEqual(upper) ||
                    !int.TryParse(
                        lower,
                        System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out repetitions))
                {
                    return false;
                }

                index = close + 1;
                if (index < branch.Length && branch[index] is '?' or '+')
                {
                    index++;
                }
            }
            else if (index < branch.Length && branch[index] is '*' or '+' or '?')
            {
                return false;
            }

            var contribution = widthBeforeQuantifier + (long)atomWidth * repetitions;
            width = contribution + width > MaximumLookBehindWidth
                ? MaximumLookBehindWidth + 1
                : width + (int)contribution;
        }

        return true;
    }

    private static RegexFlags ParseModifiers(string? modifiers)
    {
        var flags = new RegexFlags();
        if (modifiers is null)
        {
            return flags;
        }

        foreach (var modifier in modifiers)
        {
            flags = modifier switch
            {
                'g' => flags with { Global = true },
                'i' => flags with { IgnoreCase = true },
                'x' => flags with { Extended = true },
                'm' => flags with { DotMatchesNewline = true },
                's' => flags with { SingleLineAnchors = true },
                'p' => flags with { DotMatchesNewline = true, SingleLineAnchors = true },
                'l' => flags with { FindLongest = true },
                'n' => flags with { IgnoreEmpty = true },
                _ => throw new JqRuntimeException(modifiers + " is not a valid modifier string"),
            };
        }

        return flags;
    }

    private static PatternTranslation TranslatePattern(
        string pattern,
        string input,
        bool extended,
        bool dotMatchesNewline,
        bool ignoreCase)
    {
        var result = new StringBuilder(pattern.Length);
        var captureNames = new Dictionary<string, string?>(StringComparer.Ordinal);
        var runtimeNamesByDisplayName = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var runtimeNamesByCaptureNumber = new List<string>();
        var inCharacterClass = false;
        var inExtendedComment = false;
        var captureIndex = 0;
        var dotMatchesNewlineHere = dotMatchesNewline;
        var extendedHere = extended;
        var ignoreCaseHere = ignoreCase;
        var usesClusterToken = false;
        var optionModeStack = new Stack<(bool DotMatchesNewline, bool Extended, bool IgnoreCase)>();
        OnigurumaTextSegmentationPatterns? textSegments = null;

        for (var index = 0; index < pattern.Length; index++)
        {
            var current = pattern[index];
            if (inExtendedComment)
            {
                result.Append(current);
                if (current == '\n')
                {
                    inExtendedComment = false;
                }

                continue;
            }

            if (current == '\\')
            {
                if (index + 1 >= pattern.Length)
                {
                    result.Append(current);
                    continue;
                }

                var escaped = pattern[++index];
                if (!inCharacterClass && char.IsAsciiDigit(escaped) && escaped != '0')
                {
                    var numericReferenceEnd = index;
                    while (numericReferenceEnd + 1 < pattern.Length &&
                           char.IsAsciiDigit(pattern[numericReferenceEnd + 1]))
                    {
                        numericReferenceEnd++;
                    }

                    if (int.TryParse(
                            pattern.AsSpan(index, numericReferenceEnd - index + 1),
                            System.Globalization.NumberStyles.None,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var captureNumber) &&
                        captureNumber <= runtimeNamesByCaptureNumber.Count)
                    {
                        result.Append("\\k<")
                            .Append(runtimeNamesByCaptureNumber[captureNumber - 1])
                            .Append('>');
                        index = numericReferenceEnd;
                        continue;
                    }
                }

                if (!inCharacterClass && escaped == 'k' &&
                    TryReadNamedReference(pattern, index + 1, out var referenceName, out var referenceEnd) &&
                    runtimeNamesByDisplayName.TryGetValue(referenceName, out var runtimeNames))
                {
                    AppendNamedBackreference(result, runtimeNames);
                    index = referenceEnd;
                    continue;
                }

                if (!inCharacterClass && escaped == 'g' &&
                    (index + 1 >= pattern.Length || pattern[index + 1] is not ('<' or '\'')))
                {
                    // Perl-NG only assigns subexpression-call meaning to angle/quoted
                    // forms. Other escaped g spellings use its ineffective-escape rule.
                    result.Append('g');
                    continue;
                }

                if (!inCharacterClass && escaped == 'R')
                {
                    // Oniguruma treats CRLF as one indivisible general-newline token.
                    result.Append("(?>\\r\\n|[\\n\\v\\f\\r\\u0085\\u2028\\u2029])");
                    continue;
                }

                if (!inCharacterClass && escaped == 'K')
                {
                    // Oniguruma resets region 0's beginning without rewinding the
                    // engine. A private zero-width capture records that effective
                    // beginning; result conversion, split, and replacement honor it.
                    result.Append("(?<").Append(KeepCaptureName).Append(">)");
                    continue;
                }

                if (!inCharacterClass && escaped == 'N')
                {
                    result.Append("(?:[\\uD800-\\uDBFF][\\uDC00-\\uDFFF]|[^\\uD800-\\uDFFF\\n])");
                    continue;
                }

                if (!inCharacterClass && escaped == 'O')
                {
                    result.Append("(?:[\\uD800-\\uDBFF][\\uDC00-\\uDFFF]|[^\\uD800-\\uDFFF])");
                    continue;
                }

                if (!inCharacterClass && escaped == 'Q')
                {
                    var quoteEnd = pattern.IndexOf("\\E", index + 1, StringComparison.Ordinal);
                    var literalEnd = quoteEnd < 0 ? pattern.Length : quoteEnd;
                    result.Append(DotNetRegex.Escape(pattern[(index + 1)..literalEnd]));
                    index = quoteEnd < 0 ? pattern.Length - 1 : quoteEnd + 1;
                    continue;
                }

                if (!inCharacterClass && escaped is 'x' or 'o')
                {
                    if (index + 1 < pattern.Length && pattern[index + 1] == '{')
                    {
                        var parsed = OnigurumaRadixEscape.ParseBrace(
                            pattern,
                            index + 1,
                            escaped == 'x' ? 16 : 8,
                            inCharacterClass: false);
                        if (parsed.Matched)
                        {
                            foreach (var range in parsed.Ranges)
                            {
                                AppendOnigurumaCodePoint(result, range.Start, ignoreCaseHere);
                            }

                            index = parsed.End;
                            continue;
                        }

                        result.Append(escaped);
                        continue;
                    }

                    if (escaped == 'o' || index + 1 >= pattern.Length)
                    {
                        result.Append(escaped);
                        continue;
                    }

                    var fixedEnd = index + 1;
                    var scalar = 0;
                    var digits = 0;
                    while (fixedEnd < pattern.Length && digits < 2 &&
                        TryReadRadixDigit(pattern[fixedEnd], 16, out var digit))
                    {
                        scalar = scalar * 16 + digit;
                        fixedEnd++;
                        digits++;
                    }

                    AppendOnigurumaCodePoint(result, (uint)scalar, ignoreCaseHere);
                    index = fixedEnd - 1;
                    continue;
                }

                if (!inCharacterClass &&
                    escaped is 'p' or 'P' &&
                    TryReadUnicodeProperty(pattern, index + 1, out var propertyName, out var propertyEnd))
                {
                    var propertyNegated = propertyName.StartsWith('^');
                    if (propertyNegated)
                    {
                        propertyName = propertyName[1..];
                    }

                    var scalarPattern = ScalarPropertyPattern(propertyName);
                    if (scalarPattern is not null)
                    {
                        var positive = (escaped == 'p') != propertyNegated;
                        result.Append(positive
                            ? scalarPattern
                            : ComplementScalarPattern(scalarPattern));
                        index = propertyEnd;
                        continue;
                    }
                }

                if (!inCharacterClass && escaped is 'w' or 'W')
                {
                    result.Append(escaped == 'w'
                        ? ScalarWordPattern.Value
                        : ComplementScalarPattern(ScalarWordPattern.Value));
                    continue;
                }

                if (!inCharacterClass && escaped is 'd' or 'D')
                {
                    result.Append(escaped == 'd'
                        ? ScalarDigitPattern.Value
                        : ComplementScalarPattern(ScalarDigitPattern.Value));
                    continue;
                }

                if (!inCharacterClass && escaped is 's' or 'S')
                {
                    // System.Text.RegularExpressions applies character types to UTF-16 code
                    // units. In particular, \S would otherwise consume only one half of a
                    // supplementary scalar and the jq boundary filter would discard it.
                    var scalarSpace = ScalarPropertyPattern("Space")!;
                    result.Append(escaped == 's'
                        ? scalarSpace
                        : ComplementScalarPattern(scalarSpace));
                    continue;
                }

                if (!inCharacterClass && escaped is 'b' or 'B')
                {
                    result.Append(escaped == 'b'
                        ? ScalarWordBoundaryPattern.Value
                        : "(?!(?:" + ScalarWordBoundaryPattern.Value + "))");
                    continue;
                }

                if (!inCharacterClass && escaped == 'X')
                {
                    textSegments ??= OnigurumaTextSegmentation.Build(input);
                    usesClusterToken = true;
                    result.Append(textSegments.Cluster);
                    continue;
                }

                if (!inCharacterClass && escaped is 'y' or 'Y')
                {
                    textSegments ??= OnigurumaTextSegmentation.Build(input);
                    result.Append(escaped == 'y'
                        ? textSegments.Boundary
                        : textSegments.NonBoundary);
                    continue;
                }

                // ONIG_SYNTAX_PERL_NG treats these otherwise-undefined escapes as the
                // escaped literal. .NET assigns a special meaning to \v and rejects the rest.
                if (escaped is 'h' or 'H' or 'v' or 'V' or 'u' or 'C' or 'P')
                {
                    result.Append(escaped);
                    continue;
                }

                result.Append(current).Append(escaped);
                continue;
            }

            if (inCharacterClass)
            {
                result.Append(current);
                if (current == ']')
                {
                    inCharacterClass = false;
                }

                continue;
            }

            if (current == '[')
            {
                if (TryTranslateScalarCharacterClass(
                        pattern,
                        index,
                        ignoreCaseHere,
                        out var scalarClass,
                        out var scalarClassEnd))
                {
                    result.Append(scalarClass);
                    index = scalarClassEnd;
                    continue;
                }

                if (TryTranslatePosixCharacterClass(
                        pattern,
                        index,
                        out var translatedClass,
                        out var characterClassEnd))
                {
                    result.Append(translatedClass);
                    index = characterClassEnd;
                    continue;
                }

                if (ignoreCaseHere && TryTranslateCaseInsensitiveAsciiRange(
                        pattern,
                        index,
                        out var translatedRange,
                        out var translatedRangeEnd))
                {
                    result.Append(translatedRange);
                    index = translatedRangeEnd;
                    continue;
                }

                if (ignoreCaseHere && TryTranslateFullCaseFoldCharacterClass(
                        pattern,
                        index,
                        out var translatedFoldClass,
                        out var translatedFoldClassEnd))
                {
                    result.Append(translatedFoldClass);
                    index = translatedFoldClassEnd;
                    continue;
                }

                inCharacterClass = true;
                result.Append(current);
                continue;
            }

            if (extendedHere && current == '#')
            {
                inExtendedComment = true;
                result.Append(current);
                continue;
            }

            if (current == '.')
            {
                result.Append(dotMatchesNewlineHere
                    ? "(?:[\\uD800-\\uDBFF][\\uDC00-\\uDFFF]|[^\\uD800-\\uDFFF])"
                    : "(?:[\\uD800-\\uDBFF][\\uDC00-\\uDFFF]|[^\\uD800-\\uDFFF\\n])");
                continue;
            }

            if (ignoreCaseHere && TryTranslateFullCaseFoldLiteral(
                    pattern,
                    index,
                    out var caseFoldPattern,
                    out var caseFoldEnd))
            {
                result.Append(caseFoldPattern);
                index = caseFoldEnd;
                continue;
            }

            if (ignoreCaseHere && char.IsHighSurrogate(current) &&
                index + 1 < pattern.Length && char.IsLowSurrogate(pattern[index + 1]))
            {
                AppendEscapedScalar(result, char.ConvertToUtf32(current, pattern[index + 1]), ignoreCase: true);
                index++;
                continue;
            }

            if (current == ')')
            {
                result.Append(current);
                if (optionModeStack.Count != 0)
                {
                    var restored = optionModeStack.Pop();
                    dotMatchesNewlineHere = restored.DotMatchesNewline;
                    extendedHere = restored.Extended;
                    ignoreCaseHere = restored.IgnoreCase;
                }

                continue;
            }

            if (current != '(')
            {
                result.Append(current);
                continue;
            }

            if (index + 1 < pattern.Length && pattern[index + 1] == '?')
            {
                if (index + 2 < pattern.Length && pattern[index + 2] == '(' &&
                    TryFindConditionalHeaderEnd(pattern, index + 2, out var conditionEnd))
                {
                    // The parentheses around a Perl conditional's condition are grammar,
                    // not a capture. Preserve them while continuing to translate captures
                    // and scalar constructs in the then/else branches.
                    optionModeStack.Push((dotMatchesNewlineHere, extendedHere, ignoreCaseHere));
                    var condition = pattern[(index + 3)..conditionEnd];
                    if (TryResolveConditionalCaptures(
                            condition,
                            runtimeNamesByCaptureNumber,
                            runtimeNamesByDisplayName,
                            out var conditionRuntimeNames))
                    {
                        if (conditionRuntimeNames.Length == 1)
                        {
                            result.Append("(?(")
                                .Append(conditionRuntimeNames[0])
                                .Append(')');
                        }
                        else
                        {
                            var stateName = RegexConditionCapturePrefix + "named_" +
                                conditionEnd.ToString(System.Globalization.CultureInfo.InvariantCulture);
                            result.Append("(?:");
                            foreach (var runtimeName in conditionRuntimeNames)
                            {
                                result.Append("(?(")
                                    .Append(runtimeName)
                                    .Append(")(?<")
                                    .Append(stateName)
                                    .Append(">)|)");
                            }

                            result.Append(")(?(").Append(stateName).Append(')');
                        }
                    }
                    else
                    {
                        result.Append(pattern, index, conditionEnd - index + 1);
                    }

                    index = conditionEnd;
                    continue;
                }

                if (index + 2 < pattern.Length && pattern[index + 2] == '#')
                {
                    // Preserve an inline comment verbatim; otherwise punctuation within its text
                    // could be mistaken for captures or scalar-matching dots.
                    do
                    {
                        result.Append(pattern[index]);
                        if (pattern[index] == '\\' && index + 1 < pattern.Length)
                        {
                            result.Append(pattern[++index]);
                        }
                    }
                    while (++index < pattern.Length && pattern[index] != ')');

                    if (index < pattern.Length)
                    {
                        result.Append(')');
                    }

                    continue;
                }

                if (TryReadInlineOptions(
                        pattern,
                        index,
                        dotMatchesNewlineHere,
                        extendedHere,
                        ignoreCaseHere,
                        out var inlineDotMode,
                        out var inlineExtendedMode,
                        out var inlineIgnoreCaseMode,
                        out var inlineOptionsEnd,
                        out var scopedOptions))
                {
                    result.Append(pattern, index, inlineOptionsEnd - index + 1);
                    if (scopedOptions)
                    {
                        optionModeStack.Push((dotMatchesNewlineHere, extendedHere, ignoreCaseHere));
                    }

                    dotMatchesNewlineHere = inlineDotMode;
                    extendedHere = inlineExtendedMode;
                    ignoreCaseHere = inlineIgnoreCaseMode;
                    index = inlineOptionsEnd;
                    continue;
                }

                if (index + 2 < pattern.Length && pattern[index + 2] == '<' &&
                    index + 3 < pattern.Length && pattern[index + 3] is not ('=' or '!') &&
                    TryReadGroupName(pattern, index + 3, '>', out var angleName, out var angleNameEnd))
                {
                    optionModeStack.Push((dotMatchesNewlineHere, extendedHere, ignoreCaseHere));
                    if (angleName.StartsWith(AbsentTailCapturePrefix, StringComparison.Ordinal) ||
                        angleName.StartsWith('-' + AbsentTailCapturePrefix, StringComparison.Ordinal) ||
                        angleName.StartsWith(RegexConditionCapturePrefix, StringComparison.Ordinal))
                    {
                        // Absent lowering uses named captures as backtracking-aware right-range
                        // positions. Preserve both capture and balancing-group spellings without
                        // exposing them as jq captures or shifting source capture numbering.
                        result.Append(pattern, index, angleNameEnd - index + 1);
                        index = angleNameEnd;
                        continue;
                    }

                    if (TryResolveSubcallCaptureMarker(angleName, out var subcallRuntimeName))
                    {
                        result.Append("(?<").Append(subcallRuntimeName).Append('>');
                        index = angleNameEnd;
                        continue;
                    }

                    AppendNamedCapture(
                        result,
                        captureNames,
                        runtimeNamesByDisplayName,
                        runtimeNamesByCaptureNumber,
                        angleName,
                        ++captureIndex);
                    index = angleNameEnd;
                    continue;
                }

                if (index + 2 < pattern.Length && pattern[index + 2] == '\'' &&
                    TryReadGroupName(pattern, index + 3, '\'', out var quoteName, out var quoteNameEnd))
                {
                    optionModeStack.Push((dotMatchesNewlineHere, extendedHere, ignoreCaseHere));
                    AppendNamedCapture(
                        result,
                        captureNames,
                        runtimeNamesByDisplayName,
                        runtimeNamesByCaptureNumber,
                        quoteName,
                        ++captureIndex);
                    index = quoteNameEnd;
                    continue;
                }

                optionModeStack.Push((dotMatchesNewlineHere, extendedHere, ignoreCaseHere));
                result.Append(current);
                continue;
            }

            // .NET numbers unnamed groups before named groups. Turning unnamed captures into
            // private named groups keeps the Groups collection in jq's lexical group order.
            var syntheticName = "dotnetjq_unnamed_" + (++captureIndex).ToString(
                System.Globalization.CultureInfo.InvariantCulture);
            captureNames.Add(syntheticName, null);
            runtimeNamesByCaptureNumber.Add(syntheticName);
            optionModeStack.Push((dotMatchesNewlineHere, extendedHere, ignoreCaseHere));
            result.Append("(?<").Append(syntheticName).Append('>');
        }

        return new PatternTranslation(
            result.ToString(),
            captureNames,
            UsesClusterToken: usesClusterToken);
    }

    private static string TranslateAbsentRepeaters(string pattern)
    {
        var absentFunctions = CollectAbsentFunctions(pattern);
        return absentFunctions.Count == 0
            ? pattern
            : TranslateAbsentFunctions(pattern, absentFunctions);
    }

    private static CalloutTranslation PrepareCallouts(string pattern, bool extended)
    {
        var result = new StringBuilder(pattern.Length);
        var definitions = new List<CalloutDefinition>();
        var extendedHere = extended;
        var optionModeStack = new Stack<bool>();
        for (var index = 0; index < pattern.Length; index++)
        {
            if (pattern[index] == '\\')
            {
                if (index + 1 < pattern.Length && pattern[index + 1] == 'Q')
                {
                    var quoteEnd = pattern.IndexOf("\\E", index + 2, StringComparison.Ordinal);
                    var copiedEnd = quoteEnd < 0 ? pattern.Length : quoteEnd + 2;
                    result.Append(pattern, index, copiedEnd - index);
                    if (quoteEnd < 0)
                    {
                        break;
                    }

                    index = copiedEnd - 1;
                    continue;
                }

                result.Append(pattern[index]);
                if (++index < pattern.Length)
                {
                    result.Append(pattern[index]);
                }

                continue;
            }

            if (extendedHere && pattern[index] == '#')
            {
                do
                {
                    result.Append(pattern[index]);
                }
                while (++index < pattern.Length && pattern[index] != '\n');

                if (index < pattern.Length)
                {
                    result.Append(pattern[index]);
                }

                continue;
            }

            if (pattern.AsSpan(index).StartsWith("(?#", StringComparison.Ordinal))
            {
                do
                {
                    result.Append(pattern[index]);
                    if (pattern[index] == '\\' && index + 1 < pattern.Length)
                    {
                        result.Append(pattern[++index]);
                    }
                }
                while (++index < pattern.Length && pattern[index] != ')');

                if (index < pattern.Length)
                {
                    result.Append(')');
                }

                continue;
            }

            if (pattern[index] == ')' && optionModeStack.Count != 0)
            {
                result.Append(')');
                extendedHere = optionModeStack.Pop();
                continue;
            }

            if (pattern[index] == '(' &&
                TryReadInlineOptions(
                    pattern,
                    index,
                    currentDotMode: false,
                    extendedHere,
                    currentIgnoreCaseMode: false,
                    out _,
                    out var inlineExtendedMode,
                    out _,
                    out var inlineOptionsEnd,
                    out var scopedOptions))
            {
                result.Append(pattern, index, inlineOptionsEnd - index + 1);
                if (scopedOptions)
                {
                    optionModeStack.Push(extendedHere);
                }

                extendedHere = inlineExtendedMode;
                index = inlineOptionsEnd;
                continue;
            }

            if (pattern[index] == '[')
            {
                var classEnd = FindCharacterClassEnd(pattern, index);
                result.Append(pattern, index, classEnd - index + 1);
                index = classEnd;
                continue;
            }

            if (pattern.AsSpan(index).StartsWith("(?(?{", StringComparison.Ordinal) &&
                OnigurumaContentCallout.TryParse(pattern, index + 2, out var contentCondition))
            {
                FindContentConditionalBranches(
                    pattern,
                    contentCondition.End + 1,
                    out var separator,
                    out var conditionalEnd);
                var branchStart = contentCondition.End + 1;
                var trueBranch = pattern[branchStart..(separator < 0 ? conditionalEnd : separator)];
                var falseBranch = separator < 0
                    ? string.Empty
                    : pattern[(separator + 1)..conditionalEnd];
                if (trueBranch.Length == 0 && separator < 0)
                {
                    throw new JqRuntimeException("Regex failure: invalid if-else syntax");
                }

                var definition = new CalloutDefinition(
                    definitions.Count + 1,
                    CalloutKind.Content,
                    contentCondition.Tag,
                    [contentCondition.Direction.ToString()],
                    [],
                    IsConditional: true);
                definitions.Add(definition);
                AppendCalloutMarker(result, definition.Id);
                result.Append("(?(?=)(?:")
                    .Append(trueBranch)
                    .Append(")|(?:")
                    .Append(falseBranch)
                    .Append("))");
                index = conditionalEnd;
                continue;
            }

            if (OnigurumaContentCallout.TryParse(pattern, index, out var contentCallout))
            {
                var repeatIndex = SkipExtendedTrivia(
                    pattern,
                    contentCallout.End + 1,
                    extendedHere);
                if (IsRepeatQuantifierAt(pattern, repeatIndex))
                {
                    throw new JqRuntimeException(
                        "Regex failure: target of repeat operator is invalid");
                }

                var definition = new CalloutDefinition(
                    definitions.Count + 1,
                    CalloutKind.Content,
                    contentCallout.Tag,
                    [contentCallout.Direction.ToString()],
                    []);
                definitions.Add(definition);
                AppendCalloutMarker(result, definition.Id);
                index = contentCallout.End;
                continue;
            }

            if (pattern.AsSpan(index).StartsWith("(?(*", StringComparison.Ordinal) &&
                TryReadCallout(pattern, index + 2, definitions.Count + 1, out var condition, out var conditionEnd))
            {
                var conditionalEnd = FindMatchingParenthesis(pattern, index);
                if (conditionalEnd <= conditionEnd)
                {
                    throw new JqRuntimeException("Regex failure: invalid conditional pattern");
                }

                var branchStart = conditionEnd + 1;
                var separator = FindTopLevelAlternative(pattern, branchStart, conditionalEnd);
                var trueBranch = pattern[branchStart..(separator < 0 ? conditionalEnd : separator)];
                var falseBranch = separator < 0 ? string.Empty : pattern[(separator + 1)..conditionalEnd];
                if (trueBranch.Contains("(*", StringComparison.Ordinal) ||
                    falseBranch.Contains("(*", StringComparison.Ordinal))
                {
                    throw UnsupportedCallout("nested callout-conditional branch");
                }

                switch (condition.Kind)
                {
                    case CalloutKind.Fail:
                        definitions.Add(condition);
                        result.Append("(?(?!)")
                            .Append("(?:").Append(trueBranch).Append(')')
                            .Append('|')
                            .Append("(?:").Append(falseBranch).Append(')')
                            .Append(')');
                        break;
                    case CalloutKind.Mismatch:
                    case CalloutKind.Error:
                        definitions.Add(condition);
                        AppendCalloutMarker(result, condition.Id);
                        break;
                    case CalloutKind.Count:
                    case CalloutKind.TotalCount:
                    case CalloutKind.Skip:
                        definitions.Add(condition);
                        AppendCalloutMarker(result, condition.Id);
                        result.Append("(?(?=)")
                            .Append("(?:").Append(trueBranch).Append(')')
                            .Append('|')
                            .Append("(?:").Append(falseBranch).Append(')')
                            .Append(')');
                        break;
                    case CalloutKind.Max:
                    case CalloutKind.Compare:
                        condition = condition with { IsConditional = true };
                        definitions.Add(condition);
                        AppendCalloutMarker(result, condition.Id);
                        result.Append("(?(").Append(CalloutSuccessName(condition.Id)).Append(')')
                            .Append("(?:").Append(trueBranch).Append(')')
                            .Append('|')
                            .Append("(?:").Append(falseBranch).Append(')')
                            .Append(')');
                        break;
                    default:
                        throw UnsupportedCallout(condition.Kind + " conditional");
                }

                index = conditionalEnd;
                continue;
            }

            if (pattern.AsSpan(index).StartsWith("(*", StringComparison.Ordinal))
            {
                _ = TryReadCallout(
                    pattern,
                    index,
                    definitions.Count + 1,
                    out var callout,
                    out var calloutEnd);
                if (calloutEnd + 1 < pattern.Length &&
                    pattern[calloutEnd + 1] is '*' or '+' or '?' or '{')
                {
                    throw new JqRuntimeException("Regex failure: target of repeat operator is invalid");
                }

                definitions.Add(callout);
                AppendCalloutMarker(result, callout.Id);
                index = calloutEnd;
                continue;
            }

            if (pattern[index] == '(')
            {
                optionModeStack.Push(extendedHere);
            }

            result.Append(pattern[index]);
        }

        ValidateCalloutTags(definitions);
        return new CalloutTranslation(result.ToString(), definitions);
    }

    private static int SkipExtendedTrivia(string pattern, int index, bool extended)
    {
        if (!extended)
        {
            return index;
        }

        while (index < pattern.Length)
        {
            if (pattern[index] is ' ' or '\t' or '\n' or '\r' or '\f')
            {
                index++;
                continue;
            }

            if (pattern[index] != '#')
            {
                break;
            }

            var newline = pattern.IndexOf('\n', index + 1);
            if (newline < 0)
            {
                return pattern.Length;
            }

            index = newline + 1;
        }

        return index;
    }

    private static void FindContentConditionalBranches(
        string pattern,
        int start,
        out int separator,
        out int end)
    {
        separator = -1;
        var depth = 0;
        for (var index = start; index < pattern.Length; index++)
        {
            if (pattern[index] == '\\')
            {
                if (index + 1 < pattern.Length && pattern[index + 1] is 'Q' or 'c' &&
                    OnigurumaCalloutAtomMatcher.TryParseEscape(
                        pattern,
                        index,
                        out var escaped))
                {
                    index = escaped.NextPatternIndex - 1;
                    continue;
                }

                index++;
                continue;
            }

            if (pattern[index] == '[')
            {
                index = FindCharacterClassEnd(pattern, index);
                continue;
            }

            if (OnigurumaContentCallout.TryParse(pattern, index, out var nestedContent))
            {
                index = nestedContent.End;
                continue;
            }

            if (pattern[index] == '(')
            {
                depth++;
            }
            else if (pattern[index] == ')')
            {
                if (depth == 0)
                {
                    end = index;
                    return;
                }

                depth--;
            }
            else if (pattern[index] == '|' && depth == 0 && separator < 0)
            {
                separator = index;
            }
        }

        throw new JqRuntimeException("Regex failure: end pattern in group");
    }

    private static void AppendCalloutMarker(StringBuilder result, int id) =>
        result.Append(CalloutMarkerPrefix)
            .Append(id.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Append(')');

    private static bool TryReadCallout(
        string pattern,
        int start,
        int id,
        out CalloutDefinition definition,
        out int end)
    {
        definition = default!;
        end = start;
        if (!pattern.AsSpan(start).StartsWith("(*", StringComparison.Ordinal))
        {
            return false;
        }

        var cursor = start + 2;
        var nameStart = cursor;
        while (cursor < pattern.Length && pattern[cursor] is not (')' or '[' or '{'))
        {
            cursor++;
        }

        if (cursor >= pattern.Length)
        {
            throw new JqRuntimeException("Regex failure: end pattern in group");
        }

        var name = pattern[nameStart..cursor];
        if (!OnigurumaCalloutArgumentParser.IsAllowedName(name))
        {
            throw new JqRuntimeException("Regex failure: invalid callout name");
        }

        string? tag = null;
        if (cursor < pattern.Length && pattern[cursor] == '[')
        {
            var tagEnd = pattern.IndexOf(']', cursor + 1);
            if (tagEnd < 0)
            {
                throw new JqRuntimeException("Regex failure: end pattern in group");
            }

            tag = pattern[(cursor + 1)..tagEnd];
            if (!OnigurumaCalloutArgumentParser.IsAllowedTag(tag))
            {
                throw new JqRuntimeException("Regex failure: invalid callout tag name");
            }

            cursor = tagEnd + 1;
        }

        string[] arguments = [];
        bool[] argumentEscapes = [];
        if (cursor < pattern.Length && pattern[cursor] == '{')
        {
            OnigurumaCalloutArgumentParser.ParsedCalloutArguments parsed;
            try
            {
                parsed = OnigurumaCalloutArgumentParser.Parse(pattern, cursor);
            }
            catch (ArgumentException exception)
            {
                throw new JqRuntimeException("Regex failure: " + exception.Message);
            }

            arguments = parsed.Arguments.Select(argument => argument.Value).ToArray();
            argumentEscapes = parsed.Arguments.Select(argument => argument.HadEscape).ToArray();
            cursor = parsed.CloseBrace + 1;
        }

        if (cursor >= pattern.Length || pattern[cursor] != ')')
        {
            throw new JqRuntimeException(cursor >= pattern.Length
                ? "Regex failure: end pattern in group"
                : "Regex failure: undefined callout name");
        }

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

        ValidateCalloutArguments(kind, arguments, argumentEscapes);
        definition = new CalloutDefinition(id, kind, tag, arguments, argumentEscapes);
        end = cursor;
        return true;
    }

    private static void ValidateCalloutArguments(
        CalloutKind kind,
        string[] arguments,
        bool[] argumentEscapes)
    {
        static bool IsCharacter(string value) =>
            OnigurumaCalloutArgumentParser.IsSingleCharacter(value);
        static bool IsInteger(string value) =>
            OnigurumaCalloutArgumentParser.TryParseLong(value, out _);
        static bool IsTag(string value, bool escaped) =>
            !escaped && OnigurumaCalloutArgumentParser.IsAllowedTag(value);

        if (kind == CalloutKind.Max && arguments.Length >= 1 &&
            !IsInteger(arguments[0]) && !IsTag(arguments[0], argumentEscapes[0]))
        {
            throw new JqRuntimeException("Regex failure: invalid callout tag name");
        }

        if (kind == CalloutKind.Compare && arguments.Length == 3)
        {
            foreach (var operandIndex in new[] { 0, 2 })
            {
                if (!IsInteger(arguments[operandIndex]) &&
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
                (arguments.Length == 0 || IsInteger(arguments[0])),
            CalloutKind.Count or CalloutKind.TotalCount => arguments.Length <= 1 &&
                (arguments.Length == 0 || IsCharacter(arguments[0])),
            CalloutKind.Max => arguments.Length is 1 or 2 &&
                (arguments.Length == 1 || IsCharacter(arguments[1])),
            CalloutKind.Compare => arguments.Length == 3 && arguments[1].Length != 0,
            _ => false,
        };

        if (!valid)
        {
            throw new JqRuntimeException("Regex failure: invalid callout arg");
        }
    }

    private static void ValidateCalloutTags(IReadOnlyList<CalloutDefinition> definitions)
    {
        var tags = new Dictionary<string, CalloutDefinition>(StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            if (definition.Tag is not null && !tags.TryAdd(definition.Tag, definition))
            {
                throw new JqRuntimeException(
                    "Regex failure: multiplex defined name <" + definition.Tag + ">");
            }
        }

        foreach (var definition in definitions)
        {
            IEnumerable<string> references = definition.Kind switch
            {
                CalloutKind.Max => definition.Arguments.Take(1),
                CalloutKind.Compare => definition.Arguments.Where((_, index) => index is 0 or 2),
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
    }

    private static PatternTranslation LowerCallouts(
        PatternTranslation translated,
        CalloutTranslation callouts,
        int inputScalarLength,
        int skipControlId = 0)
    {
        if (callouts.Definitions.Count == 0)
        {
            return translated;
        }

        var byId = callouts.Definitions.ToDictionary(definition => definition.Id);
        var byTag = callouts.Definitions
            .Where(definition => definition.Tag is not null)
            .ToDictionary(definition => definition.Tag!, StringComparer.Ordinal);
        var referencedTags = callouts.Definitions
            .SelectMany(definition => definition.Kind switch
            {
                CalloutKind.Max => definition.Arguments.Take(1),
                CalloutKind.Compare => definition.Arguments.Where((_, index) => index is 0 or 2),
                _ => [],
            })
            .Where(reference => !OnigurumaCalloutArgumentParser.TryParseLong(reference, out _))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var definition in callouts.Definitions)
        {
            if (HasInvalidRuntimeCalloutArgument(definition))
            {
                continue;
            }

            var direction = CalloutDirection(definition);
            if (definition.Kind == CalloutKind.TotalCount &&
                definition.Tag is not null && referencedTags.Contains(definition.Tag))
            {
                throw UnsupportedCallout("TOTAL_COUNT state across candidate attempts");
            }

            if (definition.Kind == CalloutKind.Count && direction != "X" &&
                definition.Tag is not null && referencedTags.Contains(definition.Tag))
            {
                throw UnsupportedCallout("COUNT{" + direction + "} event state");
            }

            if (definition.Kind == CalloutKind.Max && direction != "X")
            {
                if (direction == "<" &&
                    (definition.Tag is null || !referencedTags.Contains(definition.Tag)))
                {
                    continue;
                }

                throw UnsupportedCallout("MAX{" + direction + "} event state");
            }

        }

        var comparisonDepth = checked(inputScalarLength + callouts.Definitions.Count + 1);
        var pattern = translated.Pattern;
        foreach (var (id, definition) in byId.OrderByDescending(pair => pair.Key))
        {
            var marker = CalloutMarkerPrefix +
                id.ToString(System.Globalization.CultureInfo.InvariantCulture) + ')';
            pattern = pattern.Replace(
                marker,
                LowerCallout(definition, byTag, comparisonDepth, referencedTags, skipControlId),
                StringComparison.Ordinal);
        }

        return translated with { Pattern = pattern };
    }

    private static string GuardCalloutControlEvents(
        string pattern,
        HashSet<int> controlIds)
    {
        if (controlIds.Count == 0)
        {
            return pattern;
        }

        return TransformControlAlternatives(pattern, 0, pattern.Length, controlIds).Pattern;
    }

    private static void RejectUnsafeCalloutEventContexts(
        string pattern,
        IReadOnlyList<CalloutDefinition> definitions)
    {
        var eventIds = definitions
            .Where(definition =>
                definition.Kind is CalloutKind.Error or CalloutKind.Mismatch or CalloutKind.Skip ||
                HasInvalidRuntimeCalloutArgument(definition))
            .ToDictionary(definition => definition.Id);
        if (eventIds.Count == 0)
        {
            return;
        }

        for (var index = 0; index < pattern.Length; index++)
        {
            if (pattern[index] == '\\')
            {
                index++;
                continue;
            }

            if (pattern[index] == '[')
            {
                index = FindCharacterClassEnd(pattern, index);
                continue;
            }

            if (pattern[index] != '(' || pattern.AsSpan(index).StartsWith("(?#", StringComparison.Ordinal))
            {
                continue;
            }

            var groupEnd = FindMatchingParenthesis(pattern, index);
            if (groupEnd <= index)
            {
                continue;
            }

            var contained = eventIds.Values.Where(definition =>
                pattern.AsSpan(index, groupEnd - index + 1).Contains(
                    CalloutMarkerPrefix +
                    definition.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) + ')',
                    StringComparison.Ordinal)).ToArray();
            if (contained.Length == 0)
            {
                continue;
            }

            if (pattern.AsSpan(index).StartsWith("(?!", StringComparison.Ordinal) ||
                pattern.AsSpan(index).StartsWith("(?<!", StringComparison.Ordinal))
            {
                throw UnsupportedCallout("control event inside negative lookaround");
            }

            if (pattern.AsSpan(index).StartsWith("(?(", StringComparison.Ordinal))
            {
                throw UnsupportedCallout("control event inside capture conditional");
            }

            var quantified = groupEnd + 1 < pattern.Length &&
                pattern[groupEnd + 1] is '*' or '+' or '?' or '{';
            if (quantified && contained.Any(definition => definition.Kind == CalloutKind.Skip))
            {
                throw UnsupportedCallout("SKIP inside quantified group");
            }
        }
    }

    private static ControlPattern TransformControlAlternatives(
        string pattern,
        int start,
        int end,
        HashSet<int> controlIds)
    {
        var alternatives = new List<ControlPattern>();
        var alternativeStart = start;
        var depth = 0;
        var inClass = false;
        for (var index = start; index < end; index++)
        {
            if (pattern[index] == '\\')
            {
                index++;
            }
            else if (pattern[index] == '[')
            {
                inClass = true;
            }
            else if (pattern[index] == ']' && inClass)
            {
                inClass = false;
            }
            else if (!inClass && pattern[index] == '(')
            {
                depth++;
            }
            else if (!inClass && pattern[index] == ')')
            {
                depth--;
            }
            else if (!inClass && pattern[index] == '|' && depth == 0)
            {
                alternatives.Add(TransformControlSequence(pattern, alternativeStart, index, controlIds));
                alternativeStart = index + 1;
            }
        }

        alternatives.Add(TransformControlSequence(pattern, alternativeStart, end, controlIds));
        return new ControlPattern(
            string.Join('|', alternatives.Select(alternative => alternative.Pattern)),
            alternatives.Any(alternative => alternative.ContainsControl));
    }

    private static ControlPattern TransformControlSequence(
        string pattern,
        int start,
        int end,
        HashSet<int> controlIds)
    {
        var atoms = new List<ControlPattern>();
        for (var index = start; index < end;)
        {
            if (TryReadCalloutMarker(pattern, index, out var markerId, out var markerEnd))
            {
                var marker = pattern[index..(markerEnd + 1)];
                atoms.Add(new ControlPattern(marker, controlIds.Contains(markerId), controlIds.Contains(markerId)));
                index = markerEnd + 1;
                continue;
            }

            var atomEnd = FindRegexAtomEnd(pattern, index, end);
            var atom = pattern[index..atomEnd];
            if (pattern[index] == '(' && !pattern.AsSpan(index).StartsWith("(?#", StringComparison.Ordinal))
            {
                var groupEnd = FindMatchingParenthesis(pattern, index);
                if (groupEnd > index && groupEnd < end &&
                    TryFindGroupBody(pattern, index, groupEnd, out var bodyStart))
                {
                    var body = TransformControlAlternatives(pattern, bodyStart, groupEnd, controlIds);
                    atom = pattern[index..bodyStart] + body.Pattern + pattern[groupEnd..atomEnd];
                    atoms.Add(new ControlPattern(atom, body.ContainsControl));
                    index = atomEnd;
                    continue;
                }
            }

            atoms.Add(new ControlPattern(atom, false));
            index = atomEnd;
        }

        var suffix = string.Empty;
        var containsControl = false;
        for (var index = atoms.Count - 1; index >= 0; index--)
        {
            var atom = atoms[index];
            if (atom.IsControlEvent)
            {
                suffix = atom.Pattern;
                containsControl = true;
            }
            else if (atom.ContainsControl)
            {
                suffix = atom.Pattern + "(?(" + CalloutControlCaptureName + ")|(?:" + suffix + "))";
                containsControl = true;
            }
            else
            {
                suffix = atom.Pattern + suffix;
            }
        }

        return new ControlPattern(suffix, containsControl);
    }

    private static bool TryReadCalloutMarker(
        string pattern,
        int start,
        out int id,
        out int end)
    {
        id = 0;
        end = start;
        if (!pattern.AsSpan(start).StartsWith(CalloutMarkerPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var numberStart = start + CalloutMarkerPrefix.Length;
        end = pattern.IndexOf(')', numberStart);
        return end > numberStart && int.TryParse(
            pattern.AsSpan(numberStart, end - numberStart),
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out id);
    }

    private static int FindRegexAtomEnd(string pattern, int start, int limit)
    {
        var end = start + 1;
        if (pattern[start] == '\\')
        {
            end = Math.Min(limit, start + 2);
            if (end < limit && pattern[end] == '{' && pattern[start + 1] is 'p' or 'P' or 'x' or 'o')
            {
                var braceEnd = pattern.IndexOf('}', end + 1);
                end = braceEnd >= 0 && braceEnd < limit ? braceEnd + 1 : end;
            }
        }
        else if (pattern[start] == '[')
        {
            end = Math.Min(limit, FindCharacterClassEnd(pattern, start) + 1);
        }
        else if (pattern[start] == '(')
        {
            var groupEnd = FindMatchingParenthesis(pattern, start);
            end = groupEnd >= start && groupEnd < limit ? groupEnd + 1 : end;
        }

        if (end < limit && pattern[end] is '*' or '+' or '?')
        {
            end++;
            if (end < limit && pattern[end] is '?' or '+')
            {
                end++;
            }
        }
        else if (end < limit && pattern[end] == '{' &&
                 TryFindQuantifierEnd(pattern, end, out var quantifierEnd) && quantifierEnd < limit)
        {
            end = quantifierEnd + 1;
            if (end < limit && pattern[end] is '?' or '+')
            {
                end++;
            }
        }

        return end;
    }

    private static bool TryFindGroupBody(string pattern, int start, int end, out int bodyStart)
    {
        bodyStart = start + 1;
        if (start + 1 >= end || pattern[start + 1] != '?')
        {
            return true;
        }

        if (start + 2 >= end || pattern[start + 2] == '#')
        {
            return false;
        }

        if (pattern[start + 2] is ':' or '=' or '!' or '>')
        {
            bodyStart = start + 3;
            return true;
        }

        if (pattern[start + 2] == '<')
        {
            if (start + 3 < end && pattern[start + 3] is '=' or '!')
            {
                bodyStart = start + 4;
                return true;
            }

            var nameEnd = pattern.IndexOf('>', start + 3);
            bodyStart = nameEnd >= 0 && nameEnd < end ? nameEnd + 1 : bodyStart;
            return nameEnd >= 0 && nameEnd < end;
        }

        if (pattern[start + 2] == '\'')
        {
            var nameEnd = pattern.IndexOf('\'', start + 3);
            bodyStart = nameEnd >= 0 && nameEnd < end ? nameEnd + 1 : bodyStart;
            return nameEnd >= 0 && nameEnd < end;
        }

        var colon = pattern.IndexOf(':', start + 2, end - start - 2);
        if (colon >= 0)
        {
            bodyStart = colon + 1;
            return true;
        }

        return false;
    }

    private static string LowerCallout(
        CalloutDefinition definition,
        IReadOnlyDictionary<string, CalloutDefinition> byTag,
        int comparisonDepth,
        HashSet<string> referencedTags,
        int skipControlId)
    {
        // jq never installs a content-callout callback. Conditional content callouts are
        // lowered by PrepareCallouts to an always-true branch, so their marker is also a
        // pure zero-width success rather than a built-in conditional state producer.
        if (definition.Kind == CalloutKind.Content)
        {
            return "(?:)";
        }

        var lowered = LowerCalloutCore(
            definition,
            byTag,
            comparisonDepth,
            referencedTags,
            skipControlId);
        return definition.IsConditional
            ? "(?(" + CalloutSuccessName(definition.Id) + ")(?<-" +
                CalloutSuccessName(definition.Id) + ">)|)" +
                "(?>(?:(?:" + lowered + ")(?<" + CalloutSuccessName(definition.Id) + ">)|))"
            : lowered;
    }

    private static string LowerCalloutCore(
        CalloutDefinition definition,
        IReadOnlyDictionary<string, CalloutDefinition> byTag,
        int comparisonDepth,
        HashSet<string> referencedTags,
        int skipControlId)
    {
        if (HasInvalidRuntimeCalloutArgument(definition))
        {
            return "(?<" + CalloutControlCaptureName + ">)(?<" +
                CalloutEventName(definition.Id, "invalid") + ">)";
        }

        var stateName = CalloutStateName(definition.Id);
        switch (definition.Kind)
        {
            case CalloutKind.Content:
                return "(?:)";
            case CalloutKind.Fail:
                return "(?!)";
            case CalloutKind.Mismatch:
                return "(?<" + CalloutControlCaptureName + ">)(?<" +
                    CalloutEventName(definition.Id, "mismatch") + ">)";
            case CalloutKind.Error:
            {
                var errorCode = CalloutErrorCode(definition);
                return "(?<" + CalloutControlCaptureName + ">)(?<" +
                    CalloutEventName(definition.Id, errorCode == -1 ? "mismatch" : "error") + ">)";
            }
            case CalloutKind.Skip:
                return definition.Id == skipControlId
                    ? "(?<" + CalloutControlCaptureName + ">)(?<" +
                        CalloutEventName(definition.Id, "skip") + ">)"
                    : "(?<" + CalloutEventName(definition.Id, "skip") + ">)";
            case CalloutKind.Count:
                return CalloutDirection(definition) == "X" && definition.Tag is not null
                    ? "(?<" + stateName + ">)"
                    : "(?:)";
            case CalloutKind.TotalCount:
                return "(?:)";
            case CalloutKind.Max:
            {
                if (CalloutDirection(definition) == "<")
                {
                    return "(?:)";
                }

                var limit = ResolveCalloutOperand(definition.Arguments[0], byTag);
                var restriction = RenderCalloutComparison(
                    new CalloutOperand(stateName, null),
                    "<",
                    limit,
                    comparisonDepth);
                var update = definition.Tag is not null && referencedTags.Contains(definition.Tag)
                    ? "(?<" + stateName + ">)"
                    : "(?<" + stateName + ">)";
                return restriction + update;
            }
            case CalloutKind.Compare:
            {
                var comparison = RenderCalloutComparison(
                    ResolveCalloutOperand(definition.Arguments[0], byTag),
                    definition.Arguments[1],
                    ResolveCalloutOperand(definition.Arguments[2], byTag),
                    comparisonDepth);
                if (definition.Tag is null || !referencedTags.Contains(definition.Tag))
                {
                    return comparison;
                }

                var operationValue = definition.Arguments[1] switch
                {
                    "==" => 0,
                    "!=" => 1,
                    "<" => 2,
                    ">" => 3,
                    "<=" => 4,
                    ">=" => 5,
                    _ => 0,
                };
                return comparison + string.Concat(
                    Enumerable.Repeat("(?<" + stateName + ">)", operationValue));
            }
            default:
                throw UnsupportedCallout(definition.Kind + " lowering");
        }
    }

    private static string CalloutDirection(CalloutDefinition definition) => definition.Kind switch
    {
        CalloutKind.Count or CalloutKind.TotalCount =>
            definition.Arguments.Length == 0 ? ">" : definition.Arguments[0],
        CalloutKind.Max => definition.Arguments.Length == 1 ? "X" : definition.Arguments[1],
        _ => string.Empty,
    };

    private static bool HasInvalidRuntimeCalloutArgument(CalloutDefinition definition) =>
        definition.Kind switch
        {
            CalloutKind.Count or CalloutKind.TotalCount =>
                definition.Arguments.Length == 1 && definition.Arguments[0] is not ("X" or ">" or "<"),
            CalloutKind.Max =>
                definition.Arguments.Length == 2 && definition.Arguments[1] is not ("X" or ">" or "<"),
            CalloutKind.Compare =>
                definition.Arguments[1] is not ("==" or "!=" or ">" or "<" or ">=" or "<="),
            _ => false,
        };

    private static CalloutOperand ResolveCalloutOperand(
        string value,
        IReadOnlyDictionary<string, CalloutDefinition> byTag)
    {
        if (OnigurumaCalloutArgumentParser.TryParseLong(value, out var literal))
        {
            return new CalloutOperand(null, literal);
        }

        var source = byTag[value];
        return source.Kind switch
        {
            CalloutKind.Count when CalloutDirection(source) == "X" =>
                new CalloutOperand(CalloutStateName(source.Id), null),
            CalloutKind.Max when CalloutDirection(source) == "X" =>
                new CalloutOperand(CalloutStateName(source.Id), null),
            CalloutKind.Compare => new CalloutOperand(CalloutStateName(source.Id), null),
            _ => new CalloutOperand(null, 0),
        };
    }

    private static string RenderCalloutComparison(
        CalloutOperand left,
        string operation,
        CalloutOperand right,
        int depth)
    {
        if (left.StackName is not null &&
            left.StackName.Equals(right.StackName, StringComparison.Ordinal))
        {
            return operation is "==" or "<=" or ">=" ? "(?:)" : "(?!)";
        }

        if (left.Literal is long leftLiteral && right.Literal is long rightLiteral)
        {
            var succeeded = operation switch
            {
                "==" => leftLiteral == rightLiteral,
                "!=" => leftLiteral != rightLiteral,
                ">" => leftLiteral > rightLiteral,
                "<" => leftLiteral < rightLiteral,
                ">=" => leftLiteral >= rightLiteral,
                "<=" => leftLiteral <= rightLiteral,
                _ => false,
            };
            return succeeded ? "(?:)" : "(?!)";
        }

        if (left.Literal is long leftValue)
        {
            return RenderStackLiteralComparison(
                right.StackName!,
                ReverseComparison(operation),
                leftValue);
        }

        if (right.Literal is long rightValue)
        {
            return RenderStackLiteralComparison(left.StackName!, operation, rightValue);
        }

        var relation = operation switch
        {
            "==" => RenderStackRelation(left.StackName!, right.StackName!, depth, StackRelation.Equal),
            "!=" => "(?:" +
                RenderStackRelation(left.StackName!, right.StackName!, depth, StackRelation.Less) + '|' +
                RenderStackRelation(left.StackName!, right.StackName!, depth, StackRelation.Greater) + ')',
            "<" => RenderStackRelation(left.StackName!, right.StackName!, depth, StackRelation.Less),
            ">" => RenderStackRelation(left.StackName!, right.StackName!, depth, StackRelation.Greater),
            "<=" => RenderStackRelation(left.StackName!, right.StackName!, depth, StackRelation.LessOrEqual),
            ">=" => RenderStackRelation(left.StackName!, right.StackName!, depth, StackRelation.GreaterOrEqual),
            _ => "(?!)",
        };
        return relation;
    }

    private static string RenderStackLiteralComparison(string stack, string operation, long literal)
    {
        if (literal < 0)
        {
            var result = operation switch
            {
                ">" or ">=" or "!=" => true,
                _ => false,
            };
            return result ? "(?:)" : "(?!)";
        }

        if (literal > int.MaxValue)
        {
            // A managed capture stack cannot contain more than Int32.MaxValue
            // entries. Oniguruma parses callout integers as signed 64-bit values,
            // so comparisons above that physical ceiling still have exact constant
            // outcomes and must not be rejected as an implementation limit.
            var result = operation switch
            {
                "<" or "<=" or "!=" => true,
                _ => false,
            };
            return result ? "(?:)" : "(?!)";
        }

        var count = checked((int)literal);
        var atLeast = RenderAtLeast(stack, count);
        var atLeastNext = literal == int.MaxValue
            ? "(?!)"
            : RenderAtLeast(stack, count + 1);
        return operation switch
        {
            "==" => literal == int.MaxValue
                ? atLeast
                : atLeast + "(?!(?:" + RawPop(stack, count + 1) + "))",
            "!=" => "(?:(?!(?:" + RawPop(stack, count) + "))|" + atLeastNext + ')',
            ">" => atLeastNext,
            ">=" => atLeast,
            "<" => "(?!(?:" + RawPop(stack, count) + "))",
            "<=" => literal == int.MaxValue
                ? "(?:)"
                : "(?!(?:" + RawPop(stack, count + 1) + "))",
            _ => "(?!)",
        };
    }

    private static string RenderAtLeast(string stack, int count) =>
        count == 0 ? "(?:)" : "(?!(?!(?:" + RawPop(stack, count) + ")))";

    private static string RawPop(string stack, int count)
    {
        return count switch
        {
            0 => string.Empty,
            1 => "(?<-" + stack + ">)",
            _ => "(?<-" + stack + ">){" +
                count.ToString(System.Globalization.CultureInfo.InvariantCulture) + '}',
        };
    }

    private static string RenderStackRelation(
        string left,
        string right,
        int depth,
        StackRelation relation)
    {
        if (relation is StackRelation.Greater or StackRelation.GreaterOrEqual)
        {
            return RenderStackRelation(
                right,
                left,
                depth,
                relation == StackRelation.Greater ? StackRelation.Less : StackRelation.LessOrEqual);
        }

        var whenLeftEmpty = relation switch
        {
            StackRelation.Equal => "(?(" + right + ")(?!)|)",
            StackRelation.Less => "(?(" + right + ")|(?!))",
            StackRelation.LessOrEqual => "(?:)",
            _ => "(?!)",
        };
        var body = $"(?({left})(?!)|{whenLeftEmpty})";
        for (var index = 0; index < depth; index++)
        {
            body = $"(?({left})(?<-{left}>)(?({right})(?<-{right}>){body}|(?!))|{whenLeftEmpty})";
        }

        return "(?!(?!(?:" + body + ")))";
    }

    private static string ReverseComparison(string operation) => operation switch
    {
        ">" => "<",
        "<" => ">",
        ">=" => "<=",
        "<=" => ">=",
        _ => operation,
    };

    private static string CalloutStateName(int id) =>
        CalloutCapturePrefix + "state_" + id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string CalloutEventName(int id, string eventKind) =>
        CalloutCapturePrefix + eventKind + '_' +
        id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string CalloutSuccessName(int id) =>
        CalloutCapturePrefix + "success_" +
        id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static JqRuntimeException UnsupportedCallout(string detail) =>
        new("Regex failure: exact managed callout unsupported: " + detail);

    private static List<AbsentFunction> CollectAbsentFunctions(string pattern)
    {
        var functions = new List<AbsentFunction>();
        for (var index = 0; index < pattern.Length; index++)
        {
            if (pattern[index] == '\\')
            {
                if (index + 1 < pattern.Length && pattern[index + 1] is 'Q' or 'c' &&
                    OnigurumaCalloutAtomMatcher.TryParseEscape(
                        pattern,
                        index,
                        out var escaped))
                {
                    index = escaped.NextPatternIndex - 1;
                    continue;
                }

                index++;
                continue;
            }

            if (pattern[index] == '[')
            {
                index = FindCharacterClassEnd(pattern, index);
                continue;
            }

            if (!pattern.AsSpan(index).StartsWith("(?~", StringComparison.Ordinal))
            {
                continue;
            }

            var end = FindMatchingParenthesis(pattern, index);
            if (end < index + 3)
            {
                continue;
            }

            var id = functions.Count + 1;
            if (pattern[index + 3] != '|')
            {
                functions.Add(new AbsentFunction(
                    index,
                    end,
                    pattern[(index + 3)..end],
                    "\\O*",
                    -1,
                    AbsentFunctionKind.Expression,
                    AbsentTailCapturePrefix + id.ToString(
                        System.Globalization.CultureInfo.InvariantCulture)));
                index = end;
                continue;
            }

            var bodyStart = index + 4;
            var separator = FindTopLevelAlternative(pattern, bodyStart, end);
            if (separator >= 0)
            {
                functions.Add(new AbsentFunction(
                    index,
                    end,
                    pattern[bodyStart..separator],
                    pattern[(separator + 1)..end],
                    separator + 1,
                    AbsentFunctionKind.Expression,
                    AbsentTailCapturePrefix + id.ToString(
                        System.Globalization.CultureInfo.InvariantCulture)));
            }
            else if (bodyStart == end)
            {
                functions.Add(new AbsentFunction(
                    index,
                    end,
                    string.Empty,
                    null,
                    -1,
                    AbsentFunctionKind.Clear,
                    string.Empty));
            }
            else
            {
                functions.Add(new AbsentFunction(
                    index,
                    end,
                    pattern[bodyStart..end],
                    null,
                    -1,
                    AbsentFunctionKind.Stopper,
                    AbsentTailCapturePrefix + id.ToString(
                        System.Globalization.CultureInfo.InvariantCulture)));
            }

            // Oniguruma explicitly leaves nested absent functions undefined. Skipping the
            // complete operator also prevents a nested spelling from acquiring an invented
            // independent range state in the managed lowering.
            index = end;
        }

        return functions;
    }

    private static string TranslateAbsentFunctions(
        string pattern,
        IReadOnlyList<AbsentFunction> functions)
    {
        var functionsByStart = functions.ToDictionary(function => function.Start);
        var tailNames = functions
            .Where(function => function.Kind != AbsentFunctionKind.Clear)
            .Select(function => function.TailName)
            .ToArray();
        return TranslateAbsentFragment(pattern, functionsByStart, tailNames, sourceOffset: 0);
    }

    private static string TranslateAbsentFragment(
        string pattern,
        IReadOnlyDictionary<int, AbsentFunction> functionsByStart,
        IReadOnlyList<string> tailNames,
        int sourceOffset)
    {
        var result = new StringBuilder(pattern.Length * 2);
        for (var index = 0; index < pattern.Length; index++)
        {
            if (functionsByStart.TryGetValue(sourceOffset + index, out var absent))
            {
                if (absent.Kind == AbsentFunctionKind.Clear)
                {
                    AppendClearAbsentRanges(result, tailNames);
                }
                else
                {
                    AppendAbsentRangeSetter(
                        result,
                        absent.Absent,
                        absent.TailName,
                        tailNames);
                    if (absent.Kind == AbsentFunctionKind.Expression)
                    {
                        var expressionFunctions = absent.ExpressionStart < 0
                            ? new Dictionary<int, AbsentFunction>()
                            : functionsByStart;
                        result.Append(TranslateAbsentFragment(
                            absent.Expression!,
                            expressionFunctions,
                            tailNames,
                            absent.ExpressionStart));
                        AppendClearAbsentRange(result, absent.TailName);
                    }
                }

                index = absent.End - sourceOffset;
                continue;
            }

            var current = pattern[index];
            if (current == '\\')
            {
                var escapeEnd = FindAbsentAtomEscapeEnd(pattern, index);
                var token = pattern[index..(escapeEnd + 1)];
                if (IsZeroWidthEscape(token))
                {
                    result.Append(token);
                }
                else
                {
                    AppendRangeGuardedAtom(result, token, tailNames);
                }

                index = escapeEnd;
                continue;
            }

            if (current == '[')
            {
                var classEnd = FindCharacterClassEnd(pattern, index);
                AppendRangeGuardedAtom(result, pattern[index..(classEnd + 1)], tailNames);
                index = classEnd;
                continue;
            }

            if (current == '(')
            {
                var headerEnd = FindRegexGroupHeaderEnd(pattern, index);
                result.Append(pattern, index, headerEnd - index + 1);
                index = headerEnd;
                continue;
            }

            if (current is ')' or '|' or '^' or '$')
            {
                result.Append(current);
                continue;
            }

            if (current is '*' or '+' or '?')
            {
                result.Append(current);
                continue;
            }

            if (current == '{' && TryFindQuantifierEnd(pattern, index, out var quantifierEnd))
            {
                result.Append(pattern, index, quantifierEnd - index + 1);
                index = quantifierEnd;
                continue;
            }

            if (char.IsHighSurrogate(current) &&
                index + 1 < pattern.Length && char.IsLowSurrogate(pattern[index + 1]))
            {
                AppendRangeGuardedAtom(result, pattern.Substring(index, 2), tailNames);
                index++;
                continue;
            }

            AppendRangeGuardedAtom(result, current.ToString(), tailNames);
        }

        return result.ToString();
    }

    private static void AppendAbsentRangeSetter(
        StringBuilder result,
        string absent,
        string tailName,
        IReadOnlyList<string> tailNames)
    {
        // Capture the suffix beginning at the first forbidden match. A later range guard
        // checks that the current input suffix can still be written as prefix + this exact
        // captured suffix, which is an exact position comparison even when the same text
        // occurs elsewhere. The capture and its balancing clear participate naturally in
        // .NET backtracking, matching Oniguruma's stateful stopper behavior across branches.
        var translatedAbsent = TranslateAbsentFragment(
            absent,
            new Dictionary<int, AbsentFunction>(),
            tailNames,
            sourceOffset: 0);
        result.Append("(?=(?:(?!(?:")
            .Append(translatedAbsent)
            .Append("))\\O");
        AppendAbsentRangeEndAssertions(result, tailNames);
        result.Append(")*(?<")
            .Append(tailName)
            .Append(">[\\s\\S]*\\z))");
    }

    private static void AppendRangeGuardedAtom(
        StringBuilder result,
        string atom,
        IReadOnlyList<string> tailNames)
    {
        result.Append("(?:").Append(atom);
        AppendAbsentRangeEndAssertions(result, tailNames);
        result.Append(')');
    }

    private static void AppendAbsentRangeEndAssertions(
        StringBuilder result,
        IReadOnlyList<string> tailNames)
    {
        foreach (var tailName in tailNames)
        {
            result.Append("(?(")
                .Append(tailName)
                .Append(")(?=[\\s\\S]*\\k<")
                .Append(tailName)
                .Append(">\\z)|)");
        }
    }

    private static void AppendClearAbsentRanges(
        StringBuilder result,
        IReadOnlyList<string> tailNames)
    {
        foreach (var tailName in tailNames)
        {
            AppendClearAbsentRange(result, tailName);
        }
    }

    private static void AppendClearAbsentRange(StringBuilder result, string tailName) =>
        result.Append("(?:(?<-")
            .Append(tailName)
            .Append(">))*");

    private static bool IsZeroWidthEscape(string token) =>
        token.Length == 2 && token[1] is 'A' or 'z' or 'Z' or 'G' or 'K' or 'b' or 'B' or 'y' or 'Y';

    private static int FindAbsentAtomEscapeEnd(string pattern, int start)
    {
        if (start + 1 >= pattern.Length)
        {
            return start;
        }

        var escaped = pattern[start + 1];
        if (start + 2 < pattern.Length &&
            ((escaped is 'p' or 'P' or 'x' or 'o' && pattern[start + 2] == '{') ||
             (escaped is 'k' or 'g' && pattern[start + 2] is '<' or '\'' or '{')))
        {
            var terminator = pattern[start + 2] switch
            {
                '<' => '>',
                '\'' => '\'',
                _ => '}',
            };
            var end = pattern.IndexOf(terminator, start + 3);
            return end < 0 ? pattern.Length - 1 : end;
        }

        return start + 1;
    }

    private static int FindRegexGroupHeaderEnd(string pattern, int start)
    {
        if (start + 1 >= pattern.Length || pattern[start + 1] != '?')
        {
            return start;
        }

        if (start + 2 >= pattern.Length)
        {
            return start + 1;
        }

        if (pattern[start + 2] == '#')
        {
            var commentEnd = pattern.IndexOf(')', start + 3);
            return commentEnd < 0 ? pattern.Length - 1 : commentEnd;
        }

        if (pattern[start + 2] == '(' &&
            TryFindConditionalHeaderEnd(pattern, start + 2, out var conditionEnd))
        {
            return conditionEnd;
        }

        if (pattern[start + 2] == '<')
        {
            if (start + 3 < pattern.Length && pattern[start + 3] is '=' or '!')
            {
                return start + 3;
            }

            var nameEnd = pattern.IndexOf('>', start + 3);
            return nameEnd < 0 ? pattern.Length - 1 : nameEnd;
        }

        if (pattern[start + 2] == '\'')
        {
            var nameEnd = pattern.IndexOf('\'', start + 3);
            return nameEnd < 0 ? pattern.Length - 1 : nameEnd;
        }

        return start + 2;
    }

    private static bool TryFindQuantifierEnd(string pattern, int start, out int end)
    {
        end = start;
        var index = start + 1;
        var sawDigit = false;
        while (index < pattern.Length && char.IsAsciiDigit(pattern[index]))
        {
            sawDigit = true;
            index++;
        }

        if (index < pattern.Length && pattern[index] == ',')
        {
            index++;
            while (index < pattern.Length && char.IsAsciiDigit(pattern[index]))
            {
                index++;
            }
        }

        if (!sawDigit || index >= pattern.Length || pattern[index] != '}')
        {
            return false;
        }

        end = index;
        return true;
    }

    private static bool TryTranslateCalloutConditional(
        string pattern,
        int start,
        out string selectedBranch,
        out int end)
    {
        selectedBranch = string.Empty;
        end = start;
        var conditionSucceeded = false;
        int conditionEnd;
        if (pattern.AsSpan(start).StartsWith("(?(?{", StringComparison.Ordinal))
        {
            conditionSucceeded = true;
            conditionEnd = FindMatchingParenthesis(pattern, start + 2);
        }
        else if (pattern.AsSpan(start).StartsWith("(?(*FAIL)", StringComparison.Ordinal) ||
                 pattern.AsSpan(start).StartsWith("(?(*MISMATCH)", StringComparison.Ordinal))
        {
            conditionEnd = pattern.IndexOf(')', start + 3);
        }
        else
        {
            return false;
        }

        end = FindMatchingParenthesis(pattern, start);
        if (conditionEnd < 0 || end <= conditionEnd)
        {
            end = start;
            return false;
        }

        var branchStart = conditionEnd + 1;
        var separator = FindTopLevelAlternative(pattern, branchStart, end);
        if (conditionSucceeded)
        {
            selectedBranch = pattern[branchStart..(separator < 0 ? end : separator)];
        }
        else if (separator >= 0)
        {
            selectedBranch = pattern[(separator + 1)..end];
        }

        return true;
    }

    private static int FindTopLevelAlternative(string pattern, int start, int end)
    {
        var depth = 0;
        for (var index = start; index < end; index++)
        {
            if (pattern[index] == '\\')
            {
                index++;
            }
            else if (pattern[index] == '[')
            {
                index = FindCharacterClassEnd(pattern, index);
            }
            else if (pattern[index] == '(')
            {
                depth++;
            }
            else if (pattern[index] == ')')
            {
                depth--;
            }
            else if (pattern[index] == '|' && depth == 0)
            {
                return index;
            }
        }

        return -1;
    }

    private static string TranslateRegexConditionals(string pattern)
    {
        var result = new StringBuilder(pattern.Length);
        var nextCondition = 0;
        for (var index = 0; index < pattern.Length; index++)
        {
            if (pattern[index] == '\\')
            {
                if (index + 1 < pattern.Length && pattern[index + 1] == 'Q' &&
                    OnigurumaCalloutAtomMatcher.TryParseEscape(
                        pattern,
                        index,
                        out var quote))
                {
                    result.Append(pattern, index, quote.NextPatternIndex - index);
                    index = quote.NextPatternIndex - 1;
                    continue;
                }

                result.Append(pattern[index]);
                if (++index < pattern.Length)
                {
                    result.Append(pattern[index]);
                }

                continue;
            }

            if (pattern[index] == '[')
            {
                var classEnd = FindCharacterClassEnd(pattern, index);
                result.Append(pattern, index, classEnd - index + 1);
                index = classEnd;
                continue;
            }

            if (!pattern.AsSpan(index).StartsWith("(?(", StringComparison.Ordinal) ||
                !TryFindConditionalHeaderEnd(pattern, index + 2, out var conditionEnd))
            {
                result.Append(pattern[index]);
                continue;
            }

            var condition = pattern[(index + 3)..conditionEnd];
            if (IsCaptureConditionalReference(condition) ||
                condition.StartsWith("?{", StringComparison.Ordinal) ||
                condition.StartsWith("?@", StringComparison.Ordinal) ||
                condition.StartsWith('*'))
            {
                // Capture-state and callout conditionals have dedicated lowering.
                result.Append(pattern[index]);
                continue;
            }

            var conditionPattern = condition.StartsWith('?')
                ? '(' + condition + ')'
                : condition;
            var runtimeName = RegexConditionCapturePrefix + (++nextCondition).ToString(
                System.Globalization.CultureInfo.InvariantCulture);

            // Oniguruma's regex condition consumes its successful match and commits to
            // the true branch.  An atomic optional capture expresses both rules in the
            // .NET engine: greedily take the condition once when possible, prohibit
            // backtracking to the absent state, then select a normal capture conditional.
            result.Append("(?>(?<")
                .Append(runtimeName)
                .Append(">(?:")
                .Append(conditionPattern)
                .Append("))?)(?(")
                .Append(runtimeName)
                .Append(')');
            index = conditionEnd;
        }

        return result.ToString();
    }

    private static bool IsCaptureConditionalReference(string condition)
    {
        if (condition.StartsWith(CalloutCapturePrefix + "success_", StringComparison.Ordinal))
        {
            return true;
        }

        if (int.TryParse(
                condition,
                System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture,
                out _))
        {
            return true;
        }

        return condition.Length >= 3 &&
            ((condition[0] == '<' && condition[^1] == '>') ||
             (condition[0] == '\'' && condition[^1] == '\''));
    }

    private static bool TryResolveConditionalCaptures(
        string condition,
        List<string> runtimeNamesByCaptureNumber,
        Dictionary<string, List<string>> runtimeNamesByDisplayName,
        out string[] runtimeNames)
    {
        string? displayName = null;
        if (condition.Length >= 3 && condition[0] == '<' && condition[^1] == '>')
        {
            displayName = condition[1..^1];
        }
        else if (condition.Length >= 3 && condition[0] == '\'' && condition[^1] == '\'')
        {
            displayName = condition[1..^1];
        }

        if (displayName is not null &&
            runtimeNamesByDisplayName.TryGetValue(displayName, out var named) &&
            named.Count != 0)
        {
            runtimeNames = named.ToArray();
            return true;
        }

        if (int.TryParse(
                condition,
                System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture,
                out var number))
        {
            var relative = condition[0] is '+' or '-';
            var index = relative
                ? number > 0
                    ? runtimeNamesByCaptureNumber.Count + number - 1
                    : runtimeNamesByCaptureNumber.Count + number
                : number - 1;
            if ((uint)index < (uint)runtimeNamesByCaptureNumber.Count)
            {
                runtimeNames = [runtimeNamesByCaptureNumber[index]];
                return true;
            }
        }

        runtimeNames = [];
        return false;
    }

    private static string TranslateSubexpressionCalls(string pattern, int recursionLimit)
    {
        var numbered = new List<SubexpressionDefinition>();
        var named = new Dictionary<string, List<SubexpressionDefinition>>(StringComparer.Ordinal);
        var definitionsByOpen = new Dictionary<int, SubexpressionDefinition>();
        for (var index = 0; index < pattern.Length; index++)
        {
            if (pattern[index] == '\\')
            {
                if (index + 1 < pattern.Length && pattern[index + 1] == 'Q' &&
                    OnigurumaCalloutAtomMatcher.TryParseEscape(
                        pattern,
                        index,
                        out var quote))
                {
                    index = quote.NextPatternIndex - 1;
                    continue;
                }

                index++;
                continue;
            }

            if (pattern[index] == '[')
            {
                index = FindCharacterClassEnd(pattern, index);
                continue;
            }

            if (pattern.AsSpan(index).StartsWith("(?P>", StringComparison.Ordinal))
            {
                // ONIG_SYNTAX_PERL_NG accepts (?&name) and \g<name>, but not
                // Python's subroutine-call spelling.
                throw new JqRuntimeException("Regex failure: undefined group option");
            }

            if (pattern[index] != '(' || !TryFindGroupBody(
                    pattern,
                    index,
                    out var bodyStart,
                    out var bodyEnd,
                    out var name))
            {
                continue;
            }

            var body = pattern[bodyStart..bodyEnd];
            var definition = new SubexpressionDefinition(
                numbered.Count + 1,
                body,
                index,
                bodyStart,
                name is not null);
            numbered.Add(definition);
            definitionsByOpen.Add(index, definition);
            if (name is not null)
            {
                if (!named.TryGetValue(name, out var definitions))
                {
                    definitions = [];
                    named.Add(name, definitions);
                }

                definitions.Add(definition);
            }
        }

        var wholePattern = new SubexpressionDefinition(0, pattern, 0, 0, Named: false);

        var result = new StringBuilder(pattern.Length);
        for (var index = 0; index < pattern.Length; index++)
        {
            if (pattern[index] == '\\' && index + 1 < pattern.Length &&
                pattern[index + 1] == 'Q' && OnigurumaCalloutAtomMatcher.TryParseEscape(
                    pattern,
                    index,
                    out var quote))
            {
                result.Append(pattern, index, quote.NextPatternIndex - index);
                index = quote.NextPatternIndex - 1;
                continue;
            }

            if (pattern[index] == '[')
            {
                var classEnd = FindCharacterClassEnd(pattern, index);
                result.Append(pattern, index, classEnd - index + 1);
                index = classEnd;
                continue;
            }

            if (pattern[index] == '\\' && index + 2 < pattern.Length && pattern[index + 1] == 'g' &&
                pattern[index + 2] is '<' or '\'' &&
                TryReadNamedReference(pattern, index + 2, out var reference, out var referenceEnd) &&
                TryResolveSubexpression(
                    reference,
                    index,
                    numbered,
                    named,
                    wholePattern,
                    out var called) &&
                TryExpandSubexpressionCall(
                    MarkSubcallCaptures(called, definitionsByOpen),
                    reference,
                    called,
                    recursionLimit,
                    out var expandedBody))
            {
                AppendSubcallCapture(result, called, expandedBody);
                index = referenceEnd;
                continue;
            }

            if (pattern[index] == '\\')
            {
                result.Append(pattern[index]);
                if (++index < pattern.Length)
                {
                    result.Append(pattern[index]);
                    if (char.IsHighSurrogate(pattern[index]) && index + 1 < pattern.Length &&
                        char.IsLowSurrogate(pattern[index + 1]))
                    {
                        result.Append(pattern[++index]);
                    }
                }

                continue;
            }

            if (pattern[index] == '(' && index + 3 < pattern.Length && pattern[index + 1] == '?')
            {
                var tokenEnd = pattern.IndexOf(')', index + 2);
                if (tokenEnd > index + 2)
                {
                    var token = pattern[(index + 2)..tokenEnd];
                    var callReference = token.StartsWith('&')
                        ? token[1..]
                        : int.TryParse(
                            token,
                            System.Globalization.NumberStyles.AllowLeadingSign,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out _)
                            ? token
                            : null;
                    if (callReference is not null && TryResolveSubexpression(
                            callReference,
                            index,
                            numbered,
                            named,
                            wholePattern,
                            out called) &&
                        TryExpandSubexpressionCall(
                            MarkSubcallCaptures(called, definitionsByOpen),
                            callReference,
                            called,
                            recursionLimit,
                            out expandedBody))
                    {
                        AppendSubcallCapture(result, called, expandedBody);
                        index = tokenEnd;
                        continue;
                    }
                }
            }

            result.Append(pattern[index]);
        }

        return result.ToString();
    }

    private static string MarkSubcallCaptures(
        SubexpressionDefinition owner,
        IReadOnlyDictionary<int, SubexpressionDefinition> definitionsByOpen)
    {
        var body = owner.Body;
        var marked = new StringBuilder(body.Length);
        for (var index = 0; index < body.Length; index++)
        {
            var originalIndex = owner.BodyStart + index;
            if (body[index] == '(' &&
                definitionsByOpen.TryGetValue(originalIndex, out var nested))
            {
                AppendSubcallCaptureStart(marked, nested);
                index = nested.BodyStart - owner.BodyStart - 1;
                continue;
            }

            marked.Append(body[index]);
        }

        return marked.ToString();
    }

    private static void AppendSubcallCapture(
        StringBuilder result,
        SubexpressionDefinition definition,
        string body)
    {
        if (definition.Number == 0)
        {
            result.Append("(?:").Append(body).Append(')');
            return;
        }

        AppendSubcallCaptureStart(result, definition);
        result.Append("(?:").Append(body).Append("))");
    }

    private static void AppendSubcallCaptureStart(
        StringBuilder result,
        SubexpressionDefinition definition) =>
        result.Append("(?<")
            .Append(SubcallCapturePrefix)
            .Append(definition.Named ? 'n' : 'u')
            .Append('_')
            .Append(definition.Number.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Append('>');

    private static bool TryResolveSubcallCaptureMarker(string name, out string runtimeName)
    {
        runtimeName = string.Empty;
        if (!name.StartsWith(SubcallCapturePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var marker = name.AsSpan(SubcallCapturePrefix.Length);
        if (marker.Length < 3 || marker[1] != '_' || marker[0] is not ('n' or 'u') ||
            !int.TryParse(
                marker[2..],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var number) ||
            number <= 0)
        {
            return false;
        }

        runtimeName = (marker[0] == 'n' ? "dotnetjq_named_" : "dotnetjq_unnamed_") +
            number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    private static bool TryFindGroupBody(
        string pattern,
        int open,
        out int bodyStart,
        out int bodyEnd,
        out string? name)
    {
        bodyStart = open + 1;
        name = null;
        if (open + 1 < pattern.Length && pattern[open + 1] == '?')
        {
            if (open + 3 < pattern.Length && pattern[open + 2] == '<' &&
                pattern[open + 3] is not ('=' or '!') &&
                TryReadGroupName(pattern, open + 3, '>', out name, out var nameEnd))
            {
                bodyStart = nameEnd + 1;
            }
            else if (open + 3 < pattern.Length && pattern[open + 2] == '\'' &&
                     TryReadGroupName(pattern, open + 3, '\'', out name, out nameEnd))
            {
                bodyStart = nameEnd + 1;
            }
            else
            {
                bodyEnd = open;
                return false;
            }
        }

        bodyEnd = FindMatchingParenthesis(pattern, open);
        return bodyEnd > bodyStart;
    }

    private static int FindMatchingParenthesis(string pattern, int open)
    {
        var depth = 1;
        for (var index = open + 1; index < pattern.Length; index++)
        {
            if (pattern[index] == '\\')
            {
                index++;
            }
            else if (pattern[index] == '[')
            {
                index = FindCharacterClassEnd(pattern, index);
            }
            else if (pattern[index] == '(')
            {
                depth++;
            }
            else if (pattern[index] == ')' && --depth == 0)
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindCharacterClassEnd(string pattern, int open)
        => FindPerlNgCharacterClassEnd(pattern, open);

    private static bool TryResolveSubexpression(
        string reference,
        int callPosition,
        IReadOnlyList<SubexpressionDefinition> numbered,
        IReadOnlyDictionary<string, List<SubexpressionDefinition>> named,
        SubexpressionDefinition wholePattern,
        out SubexpressionDefinition definition)
    {
        if (int.TryParse(
                reference,
                System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture,
                out var number))
        {
            if (number == 0 && reference[0] is not ('+' or '-'))
            {
                definition = wholePattern;
                return true;
            }

            var relativeBase = numbered.Count(candidate => candidate.Open < callPosition);
            var index = reference[0] switch
            {
                '+' => relativeBase + number - 1,
                '-' => relativeBase + number,
                _ => number - 1,
            };
            if ((uint)index < (uint)numbered.Count)
            {
                definition = numbered[index];
                return true;
            }
        }
        else if (named.TryGetValue(reference, out var definitions))
        {
            if (definitions.Count != 1)
            {
                throw new JqRuntimeException(
                    "Regex failure: multiplex definition name <" + reference + "> call");
            }

            definition = definitions[0];
            return true;
        }

        definition = null!;
        return false;
    }

    private static bool ContainsSameSubexpressionCall(string body, string reference)
    {
        var numeric = int.TryParse(
            reference,
            System.Globalization.NumberStyles.AllowLeadingSign,
            System.Globalization.CultureInfo.InvariantCulture,
            out _);
        return (numeric && body.Contains("(?" + reference + ")", StringComparison.Ordinal)) ||
            body.Contains("(?&" + reference + ")", StringComparison.Ordinal) ||
            body.Contains("\\g<" + reference + ">", StringComparison.Ordinal) ||
            body.Contains("\\g'" + reference + "'", StringComparison.Ordinal);
    }

    private static bool TryExpandSubexpressionCall(
        string body,
        string reference,
        SubexpressionDefinition definition,
        int recursionLimit,
        out string expanded)
    {
        if (!ContainsSameSubexpressionCall(body, reference))
        {
            expanded = body;
            return true;
        }

        expanded = "(?!)";
        for (var depth = 0; depth < recursionLimit; depth++)
        {
            var replacementBuilder = new StringBuilder(expanded.Length + 32);
            AppendSubcallCapture(replacementBuilder, definition, expanded);
            var replacement = replacementBuilder.ToString();
            var next = body
                .Replace("(?&" + reference + ")", replacement, StringComparison.Ordinal)
                .Replace("\\g<" + reference + ">", replacement, StringComparison.Ordinal)
                .Replace("\\g'" + reference + "'", replacement, StringComparison.Ordinal);
            if (int.TryParse(
                    reference,
                    System.Globalization.NumberStyles.AllowLeadingSign,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out _))
            {
                next = next.Replace(
                    "(?" + reference + ")",
                    replacement,
                    StringComparison.Ordinal);
            }
            expanded = next;
        }

        return true;
    }

    private static string TranslatePossessiveQuantifiers(string pattern, bool extended)
    {
        var translated = pattern;
        var searchStart = 0;
        while (TryFindPossessiveQuantifier(
                   translated,
                   searchStart,
                   out var quantifierStart,
                   out var quantifierEnd))
        {
            var atomStart = FindPreviousAtomStart(translated, quantifierStart, extended);
            if (atomStart < 0)
            {
                searchStart = quantifierEnd + 1;
                continue;
            }

            var atomAndQuantifier = translated[atomStart..(quantifierEnd + 1)];
            translated = translated[..atomStart] + "(?>" + atomAndQuantifier + ")" +
                translated[(quantifierEnd + 2)..];
            searchStart = atomStart + atomAndQuantifier.Length + 4;
        }

        return translated;
    }

    private static bool TryTranslateFullCaseFoldLiteral(
        string pattern,
        int start,
        out string translated,
        out int end)
    {
        translated = string.Empty;
        end = start;
        var remaining = pattern.AsSpan(start);
        foreach (var mapping in FullCaseFoldMappings)
        {
            if (remaining.StartsWith(mapping.Folded, StringComparison.OrdinalIgnoreCase))
            {
                translated = BuildFullCaseFoldPattern(mapping);
                end = start + mapping.Folded.Length - 1;
                return true;
            }

            if (mapping.Equivalents.Contains(pattern[start], StringComparison.Ordinal))
            {
                translated = BuildFullCaseFoldPattern(mapping);
                return true;
            }
        }

        translated = pattern[start] switch
        {
            's' or 'S' => "[sS\\u017F]",
            '\u03C3' or '\u03A3' or '\u03C2' => "[\\u03C3\\u03A3\\u03C2]",
            _ => string.Empty,
        };
        return translated.Length != 0;
    }

    private static string BuildFullCaseFoldPattern(OnigurumaFullCaseFoldMapping mapping)
    {
        var result = new StringBuilder(mapping.Folded.Length + mapping.Equivalents.Length * 8 + 8);
        result.Append("(?:").Append(DotNetRegex.Escape(mapping.Folded));
        foreach (var equivalent in mapping.Equivalents)
        {
            result.Append('|').Append(DotNetRegex.Escape(equivalent.ToString()));
        }

        return result.Append(')').ToString();
    }

    private static bool TryTranslateFullCaseFoldCharacterClass(
        string pattern,
        int start,
        out string translated,
        out int end)
    {
        translated = string.Empty;
        end = start;
        var classEnd = FindCharacterClassEnd(pattern, start);
        if (classEnd <= start + 1 || pattern[start + 1] == '^')
        {
            return false;
        }

        var alternatives = new HashSet<string>(StringComparer.Ordinal);
        for (var index = start + 1; index < classEnd; index++)
        {
            if (pattern[index] == '\\')
            {
                index++;
                continue;
            }

            var value = pattern[index];
            if (value is '-' or '[' or ']')
            {
                continue;
            }

            foreach (var mapping in FullCaseFoldMappings)
            {
                if (mapping.Equivalents.Contains(value, StringComparison.Ordinal))
                {
                    alternatives.Add(mapping.Folded);
                }
            }

            if (value is 's' or 'S')
            {
                alternatives.Add("\u017F");
            }
            else if (value == '\u017F')
            {
                alternatives.Add("s");
            }
        }

        if (alternatives.Count == 0)
        {
            return false;
        }

        var result = new StringBuilder(classEnd - start + alternatives.Sum(value => value.Length) + 8);
        result.Append("(?:").Append(pattern, start, classEnd - start + 1);
        foreach (var alternative in alternatives)
        {
            result.Append('|').Append(DotNetRegex.Escape(alternative));
        }

        translated = result.Append(')').ToString();
        end = classEnd;
        return true;
    }

    private static bool TryTranslateCaseInsensitiveAsciiRange(
        string pattern,
        int start,
        out string translated,
        out int end)
    {
        translated = string.Empty;
        end = start;
        if (pattern.AsSpan(start).StartsWith("[a-z]", StringComparison.Ordinal) ||
            pattern.AsSpan(start).StartsWith("[A-Z]", StringComparison.Ordinal))
        {
            translated = "[a-zA-Z\\u017F]";
            end = start + 4;
            return true;
        }

        return false;
    }

    private static bool ContainsPerlNgWordBoundary(string pattern)
    {
        var inClass = false;
        for (var index = 0; index + 1 < pattern.Length; index++)
        {
            if (pattern[index] == '[')
            {
                inClass = true;
            }
            else if (pattern[index] == ']')
            {
                inClass = false;
            }
            else if (pattern[index] == '\\')
            {
                if (!inClass && pattern[index + 1] == 'b')
                {
                    return true;
                }

                index++;
            }
        }

        return false;
    }

    private static bool TryFindPossessiveQuantifier(
        string pattern,
        int searchStart,
        out int quantifierStart,
        out int quantifierEnd)
    {
        var inClass = false;
        for (var index = searchStart; index < pattern.Length - 1; index++)
        {
            if (pattern[index] == '\\')
            {
                index++;
                continue;
            }

            if (pattern[index] == '[')
            {
                inClass = true;
                continue;
            }

            if (inClass)
            {
                if (pattern[index] == ']')
                {
                    inClass = false;
                }

                continue;
            }

            if (pattern[index] is '?' or '*' or '+' && pattern[index + 1] == '+')
            {
                quantifierStart = index;
                quantifierEnd = index;
                return true;
            }

            if (pattern[index] != '{')
            {
                continue;
            }

            var close = pattern.IndexOf('}', index + 1);
            if (close < 0 || close + 1 >= pattern.Length || pattern[close + 1] != '+' ||
                !IsIntervalBody(pattern.AsSpan(index + 1, close - index - 1)))
            {
                continue;
            }

            quantifierStart = index;
            quantifierEnd = close;
            return true;
        }

        quantifierStart = -1;
        quantifierEnd = -1;
        return false;
    }

    private static bool IsIntervalBody(ReadOnlySpan<char> body)
    {
        // ONIG_SYNTAX_PERL_NG does not enable the abbreviated-low form:
        // `{,n}` is literal text rather than a repeat interval.
        if (body.IsEmpty || !char.IsAsciiDigit(body[0]))
        {
            return false;
        }

        var commas = 0;
        var digits = 0;
        foreach (var value in body)
        {
            if (value == ',')
            {
                commas++;
            }
            else if (char.IsAsciiDigit(value))
            {
                digits++;
            }
            else
            {
                return false;
            }
        }

        return commas <= 1 && digits != 0;
    }

    private static int FindPreviousAtomStart(string pattern, int end, bool extended)
    {
        var groups = new Stack<int>();
        var lastAtomStart = -1;
        for (var index = 0; index < end; index++)
        {
            var current = pattern[index];
            if (current == '\\')
            {
                lastAtomStart = index;
                if (++index < end && pattern[index] is 'x' or 'o' or 'p' or 'P' &&
                    index + 1 < end && pattern[index + 1] == '{')
                {
                    var close = pattern.IndexOf('}', index + 2);
                    if (close >= 0 && close < end)
                    {
                        index = close;
                    }
                }

                continue;
            }

            if (extended && current is ' ' or '\t' or '\n' or '\r' or '\f')
            {
                continue;
            }

            if (extended && current == '#')
            {
                var newline = pattern.IndexOf('\n', index + 1);
                index = newline < 0 || newline >= end ? end : newline;
                continue;
            }

            if (current == '[')
            {
                lastAtomStart = index;
                for (index++; index < end; index++)
                {
                    if (pattern[index] == '\\')
                    {
                        index++;
                    }
                    else if (pattern[index] == ']')
                    {
                        break;
                    }
                }

                continue;
            }

            if (current == '(')
            {
                groups.Push(index);
                lastAtomStart = index;
                continue;
            }

            if (current == ')' && groups.Count != 0)
            {
                lastAtomStart = groups.Pop();
                continue;
            }

            if (current == '|')
            {
                lastAtomStart = -1;
                continue;
            }

            if (current == '{')
            {
                var close = pattern.IndexOf('}', index + 1);
                if (close >= 0 && close < end &&
                    IsIntervalBody(pattern.AsSpan(index + 1, close - index - 1)))
                {
                    index = close;
                    continue;
                }

                lastAtomStart = index;
                continue;
            }

            if (current == '}')
            {
                lastAtomStart = index;
                continue;
            }

            if (current is '^' or '$' or '?' or '*' or '+')
            {
                continue;
            }

            lastAtomStart = char.IsLowSurrogate(current) && index > 0 && char.IsHighSurrogate(pattern[index - 1])
                ? index - 1
                : index;
        }

        return lastAtomStart;
    }

    private static bool TryFindConditionalHeaderEnd(string pattern, int conditionOpen, out int end)
    {
        var depth = 1;
        var inClass = false;
        for (var index = conditionOpen + 1; index < pattern.Length; index++)
        {
            var current = pattern[index];
            if (current == '\\')
            {
                index++;
                continue;
            }

            if (current == '[')
            {
                inClass = true;
            }
            else if (current == ']' && inClass)
            {
                inClass = false;
            }
            else if (!inClass && current == '(')
            {
                depth++;
            }
            else if (!inClass && current == ')' && --depth == 0)
            {
                end = index;
                return true;
            }
        }

        end = conditionOpen;
        return false;
    }

    private static bool TryReadRadixEscape(
        string pattern,
        int start,
        int radix,
        out int scalar,
        out int end)
    {
        scalar = 0;
        end = start;
        if (start >= pattern.Length || pattern[start] != '{')
        {
            return false;
        }

        var close = pattern.IndexOf('}', start + 1);
        if (close <= start + 1)
        {
            return false;
        }

        try
        {
            scalar = Convert.ToInt32(pattern[(start + 1)..close], radix);
        }
        catch (FormatException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }

        if (!Rune.IsValid(scalar))
        {
            return false;
        }

        end = close;
        return true;
    }

    private static bool TryReadRadixDigit(char current, int radix, out int digit)
    {
        digit = current switch
        {
            >= '0' and <= '9' => current - '0',
            >= 'a' and <= 'f' => current - 'a' + 10,
            >= 'A' and <= 'F' => current - 'A' + 10,
            _ => -1,
        };
        return digit >= 0 && digit < radix;
    }

    private static void AppendOnigurumaCodePoint(
        StringBuilder result,
        uint codePoint,
        bool ignoreCase)
    {
        if (codePoint <= 0x10FFFF && Rune.IsValid((int)codePoint))
        {
            AppendEscapedScalar(result, (int)codePoint, ignoreCase);
        }
        else
        {
            // Oniguruma accepts surrogate and extended UTF-8 values through F4ffff.
            // They cannot occur in jq's valid UTF-8 strings, so the atom never matches.
            result.Append("(?!)");
        }
    }

    private static bool TryReadRadixEscapeSequence(
        string pattern,
        int start,
        int radix,
        out IReadOnlyList<int> scalars,
        out int end)
    {
        scalars = Array.Empty<int>();
        end = start;
        if (start >= pattern.Length || pattern[start] != '{')
        {
            return false;
        }

        var close = pattern.IndexOf('}', start + 1);
        if (close <= start + 1)
        {
            return false;
        }

        var values = new List<int>();
        var valueStart = start + 1;
        for (var index = valueStart; index <= close; index++)
        {
            if (index != close && pattern[index] != ' ')
            {
                continue;
            }

            if (index == valueStart ||
                !TryParseRadixScalar(pattern.AsSpan(valueStart, index - valueStart), radix, out var scalar))
            {
                return false;
            }

            values.Add(scalar);
            if (index == close)
            {
                break;
            }

            while (index + 1 < close && pattern[index + 1] == ' ')
            {
                index++;
            }

            valueStart = index + 1;
        }

        scalars = values;
        end = close;
        return values.Count != 0;
    }

    private static bool TryParseRadixScalar(ReadOnlySpan<char> text, int radix, out int scalar)
    {
        scalar = 0;
        if (text.IsEmpty)
        {
            return false;
        }

        foreach (var current in text)
        {
            var digit = current switch
            {
                >= '0' and <= '9' => current - '0',
                >= 'a' and <= 'f' => current - 'a' + 10,
                >= 'A' and <= 'F' => current - 'A' + 10,
                _ => -1,
            };
            if (digit < 0 || digit >= radix || scalar > (0x10FFFF - digit) / radix)
            {
                scalar = 0;
                return false;
            }

            scalar = scalar * radix + digit;
        }

        return Rune.IsValid(scalar);
    }

    private static void AppendEscapedScalar(StringBuilder result, int scalar, bool ignoreCase)
    {
        var rune = new Rune(scalar);
        var values = new HashSet<string>(StringComparer.Ordinal) { rune.ToString() };
        if (ignoreCase)
        {
            values.Add(Rune.ToUpperInvariant(rune).ToString());
            values.Add(Rune.ToLowerInvariant(rune).ToString());
        }

        if (values.Count == 1)
        {
            result.Append(DotNetRegex.Escape(values.Single()));
            return;
        }

        result.Append("(?:");
        var separator = string.Empty;
        foreach (var value in values)
        {
            result.Append(separator).Append(DotNetRegex.Escape(value));
            separator = "|";
        }

        result.Append(')');
    }

    private static bool TryReadUnicodeProperty(
        string pattern,
        int start,
        out string propertyName,
        out int end)
    {
        propertyName = string.Empty;
        end = start;
        if (start >= pattern.Length || pattern[start] != '{')
        {
            return false;
        }

        end = start + 1;
        while (end < pattern.Length && pattern[end] != '}' &&
            pattern[end] is not ('(' or ')' or '{' or '|'))
        {
            end++;
        }

        if (end >= pattern.Length || pattern[end] != '}')
        {
            return false;
        }

        propertyName = pattern[(start + 1)..end];
        return true;
    }

    private static string? ScalarPropertyPattern(string propertyName)
    {
        var normalized = NormalizeUnicodePropertyName(propertyName);
        if (normalized is null)
        {
            return null;
        }

        // Outside a class, `Word` is tested through unicode.c's standard ctype
        // path: CR_Word at U+0100 and above, and its ISO-8859-1 table below.
        if (normalized.Equals("WORD", StringComparison.Ordinal))
        {
            return ScalarWordPattern.Value;
        }

        // Resolve every name present in the pinned Oniguruma catalogue before considering
        // managed convenience fallbacks. Category-derived shortcuts are not interchangeable
        // with the generated Unicode 16.0 sets (for example, Alphabetic contains selected
        // marks), and block/script membership must not drift with the host runtime version.
        if (UnicodePropertyPatternCache.TryGetValue(normalized, out var cached))
        {
            return cached;
        }

        if (OnigurumaUnicodePropertyData.TryDecodeRanges(normalized, out var ranges))
        {
            var generated = BuildScalarRangePattern(ranges);
            return UnicodePropertyPatternCache.GetOrAdd(normalized, generated);
        }

        var knownPattern = normalized switch
        {
            "L" or "LETTER" => ScalarLetterPattern.Value,
            "ALPHA" or "ALPHABETIC" => ScalarAlphabeticPattern.Value,
            "N" or "NUMBER" => ScalarNumberPattern.Value,
            "M" or "MARK" => ScalarMarkPattern.Value,
            "Z" or "SEPARATOR" => ScalarSeparatorPattern.Value,
            "P" or "PUNCTUATION" => ScalarPunctuationPattern.Value,
            "S" or "SYMBOL" => ScalarSymbolPattern.Value,
            "WORD" => ScalarWordPattern.Value,
            "DIGIT" => ScalarDigitPattern.Value,
            "ALNUM" => "(?:" + ScalarAlphabeticPattern.Value + "|" + ScalarDigitPattern.Value + ")",
            "SPACE" => "(?:\\s)",
            "GREEK" => "(?:[\\p{IsGreekandCoptic}\\p{IsGreekExtended}])",
            "LATIN" => "(?:[A-Za-z\\u00AA\\u00BA\\u00C0-\\u00D6\\u00D8-\\u00F6" +
                "\\u00F8-\\u02E4\\u1D00-\\u1DBF\\u1E00-\\u1EFF\\u2071\\u207F" +
                "\\u2090-\\u209C\\u212A-\\u212B\\u2132\\u214E\\u2160-\\u2188" +
                "\\u2C60-\\u2C7F\\uA722-\\uA7FF\\uAB30-\\uAB6F\\uFB00-\\uFB06" +
                "\\uFF21-\\uFF3A\\uFF41-\\uFF5A])",
            "EMOJI" => EmojiPropertyPattern.Value,
            "EMOJICOMPONENT" => EmojiComponentPropertyPattern.Value,
            "EMOJIMODIFIER" => EmojiModifierPropertyPattern.Value,
            "EMOJIMODIFIERBASE" => EmojiModifierBasePropertyPattern.Value,
            "EMOJIPRESENTATION" => EmojiPresentationPropertyPattern.Value,
            "EXTENDEDPICTOGRAPHIC" => ExtendedPictographicPropertyPattern.Value,
            _ => null,
        };

        if (knownPattern is not null)
        {
            return knownPattern;
        }

        return null;
    }

    private static string? NormalizeUnicodePropertyName(string propertyName)
    {
        var normalized = new StringBuilder(propertyName.Length);
        foreach (var value in propertyName)
        {
            if (value > 0x7F)
            {
                return null;
            }

            if (value is not (' ' or '_' or '-'))
            {
                normalized.Append(char.ToUpperInvariant(value));
            }
        }

        return normalized.ToString();
    }

    private static string BuildScalarWordBoundaryPattern()
    {
        var supplementaryWord = SupplementaryWordPattern.Value;
        var basicWord = BmpRuntimeWordPattern.Value;
        var wordBefore = "(?:(?<=" + basicWord + ")|(?<=(?:" + supplementaryWord + ")))";
        var noWordBefore = "(?<!" + basicWord + ")(?<!(?:" + supplementaryWord + "))";
        var wordAfter = "(?=" + ScalarWordPattern.Value + ")";
        var noWordAfter = "(?!" + ScalarWordPattern.Value + ")";
        return "(?:(?:" + noWordBefore + wordAfter + ")|(?:" + wordBefore + noWordAfter + "))";
    }

    private static int[] ClipScalarRanges(int[] ranges, int minimum, int maximum)
    {
        var clipped = new List<int>();
        for (var index = 0; index < ranges.Length; index += 2)
        {
            var start = Math.Max(ranges[index], minimum);
            var end = Math.Min(ranges[index + 1], maximum);
            if (start <= end)
            {
                clipped.Add(start);
                clipped.Add(end);
            }
        }

        return [.. clipped];
    }

    private static string BuildScalarRangePattern(int[] ranges)
    {
        var pattern = new StringBuilder(ranges.Length * 8);
        pattern.Append("(?:");
        var first = true;
        for (var index = 0; index < ranges.Length; index += 2)
        {
            var start = ranges[index];
            var end = ranges[index + 1];
            // jq strings contain valid UTF-8 scalars. U+D800..U+DFFF can never
            // occur as input values; emitting them as a .NET BMP class would
            // instead consume one half of every supplementary scalar.
            if (start <= 0xD7FF)
            {
                AppendBmpRange(pattern, start, Math.Min(end, 0xD7FF), ref first);
            }

            if (end >= 0xE000 && start <= char.MaxValue)
            {
                AppendBmpRange(
                    pattern,
                    Math.Max(start, 0xE000),
                    Math.Min(end, char.MaxValue),
                    ref first);
            }

            var supplementaryStart = Math.Max(start, 0x10000);
            if (supplementaryStart > end)
            {
                continue;
            }

            var firstHigh = 0xD800 + ((supplementaryStart - 0x10000) >> 10);
            var lastHigh = 0xD800 + ((end - 0x10000) >> 10);
            for (var high = firstHigh; high <= lastHigh; high++)
            {
                var blockStart = 0x10000 + ((high - 0xD800) << 10);
                var lowStart = 0xDC00 + Math.Max(supplementaryStart - blockStart, 0);
                var lowEnd = 0xDC00 + Math.Min(end - blockStart, 0x3FF);
                if (!first)
                {
                    pattern.Append('|');
                }

                AppendUtf16Escape(pattern, high);
                pattern.Append('[');
                AppendUtf16Escape(pattern, lowStart);
                if (lowStart != lowEnd)
                {
                    pattern.Append('-');
                    AppendUtf16Escape(pattern, lowEnd);
                }

                pattern.Append(']');
                first = false;
            }
        }

        if (first)
        {
            pattern.Append("(?!)");
        }

        return pattern.Append(')').ToString();
    }

    private static void AppendBmpRange(StringBuilder pattern, int start, int end, ref bool first)
    {
        if (!first)
        {
            pattern.Append('|');
        }

        pattern.Append('[');
        AppendUtf16Escape(pattern, start);
        if (start != end)
        {
            pattern.Append('-');
            AppendUtf16Escape(pattern, end);
        }

        pattern.Append(']');
        first = false;
    }

    private static string BuildScalarCategoryPattern(
        string basicMultilingualPlanePattern,
        Func<System.Globalization.UnicodeCategory, bool> includes)
    {
        var supplementary = BuildSupplementaryCategoryPattern(includes);
        return supplementary.Length == 0
            ? "(?:" + basicMultilingualPlanePattern + ")"
            : "(?:" + basicMultilingualPlanePattern + "|(?:" + supplementary + "))";
    }

    private static string BuildSupplementaryCategoryPattern(
        Func<System.Globalization.UnicodeCategory, bool> includes)
    {
        var supplementary = new StringBuilder();
        for (var high = 0xD800; high <= 0xDBFF; high++)
        {
            var ranges = new List<(int Start, int End)>();
            var rangeStart = -1;
            for (var low = 0xDC00; low <= 0xDFFF; low++)
            {
                var scalar = 0x10000 + ((high - 0xD800) << 10) + low - 0xDC00;
                var matches = includes(Rune.GetUnicodeCategory(new Rune(scalar)));
                if (matches && rangeStart < 0)
                {
                    rangeStart = low;
                }
                else if (!matches && rangeStart >= 0)
                {
                    ranges.Add((rangeStart, low - 1));
                    rangeStart = -1;
                }
            }

            if (rangeStart >= 0)
            {
                ranges.Add((rangeStart, 0xDFFF));
            }

            if (ranges.Count == 0)
            {
                continue;
            }

            if (supplementary.Length != 0)
            {
                supplementary.Append('|');
            }

            AppendUtf16Escape(supplementary, high);
            supplementary.Append('[');
            foreach (var range in ranges)
            {
                AppendUtf16Escape(supplementary, range.Start);
                if (range.End != range.Start)
                {
                    supplementary.Append('-');
                    AppendUtf16Escape(supplementary, range.End);
                }
            }

            supplementary.Append(']');
        }

        return supplementary.ToString();
    }

    private static string ComplementScalarPattern(string positivePattern) =>
        "(?:(?!" + positivePattern + ")" +
        "(?:[\\uD800-\\uDBFF][\\uDC00-\\uDFFF]|[^\\uD800-\\uDFFF]))";

    private static void AppendUtf16Escape(StringBuilder builder, int value) =>
        builder.Append("\\u").Append(
            value.ToString("X4", System.Globalization.CultureInfo.InvariantCulture));

    private static bool TryTranslateScalarCharacterClass(
        string pattern,
        int start,
        bool ignoreCase,
        out string translated,
        out int end)
    {
        translated = string.Empty;
        end = start;
        if (start >= pattern.Length || pattern[start] != '[')
        {
            return false;
        }

        var sourceClassEnd = FindPerlNgCharacterClassEnd(pattern, start);
        var index = start + 1;
        var outerNegated = index < pattern.Length && pattern[index] == '^';
        if (outerNegated)
        {
            index++;
        }

        var atoms = new List<ScalarClassAtom>();
        // Perl-NG leaves ordinary '[' and '&&' literal inside a class, while
        // System.Text.RegularExpressions assigns its own subtraction/nesting
        // grammar. Lower every class through the scalar atom pass so structural
        // punctuation cannot fall back to the host parser.
        var needsScalarTranslation = true;
        while (index < sourceClassEnd)
        {
            if (pattern[index] == '[' && index + 1 < sourceClassEnd &&
                pattern[index + 1] == ':')
            {
                if (TryReadScalarPosixClass(pattern, ref index, out var posixPattern))
                {
                    atoms.Add(new ScalarClassAtom(posixPattern, null, IsDash: false));
                    needsScalarTranslation = true;
                    continue;
                }

                // A lexically complete POSIX bracket commits to the POSIX class
                // grammar even when its name is unknown or has the wrong case.
                // Do not reinterpret its inner '[' and ']' as ordinary members.
                var posixEnd = pattern.IndexOf(":]", index + 2, StringComparison.Ordinal);
                if (posixEnd >= 0 && posixEnd + 1 < sourceClassEnd)
                {
                    throw new JqRuntimeException("Regex failure: invalid POSIX bracket type");
                }
            }

            if (pattern[index] == '\\' && TryReadScalarClassEscape(
                    pattern,
                    ref index,
                    out var escapedPattern,
                    out var escapedSingleton,
                    out var escapedNeedsTranslation))
            {
                atoms.Add(new ScalarClassAtom(escapedPattern, escapedSingleton, IsDash: false));
                // Escaped '-' can be the left endpoint of a following raw '-'
                // range. .NET's native class parser gives that source spelling
                // different subtraction semantics, so lower it explicitly.
                needsScalarTranslation |= escapedNeedsTranslation || escapedSingleton == '-';
                continue;
            }

            int scalar;
            if (char.IsHighSurrogate(pattern[index]) && index + 1 < pattern.Length &&
                char.IsLowSurrogate(pattern[index + 1]))
            {
                scalar = char.ConvertToUtf32(pattern[index], pattern[index + 1]);
                index += 2;
                needsScalarTranslation = true;
            }
            else
            {
                scalar = pattern[index++];
            }

            atoms.Add(scalar == '-'
                ? new ScalarClassAtom(string.Empty, null, IsDash: true)
                : ScalarClassSingleton(scalar));
            needsScalarTranslation |= scalar is '[' or ']';
        }

        end = sourceClassEnd;
        if (atoms.Count == 0 || !needsScalarTranslation)
        {
            return false;
        }

        var alternatives = new List<string>(atoms.Count);
        for (var atomIndex = 0; atomIndex < atoms.Count; atomIndex++)
        {
            var atom = atoms[atomIndex];
            if (!atom.IsDash && atom.Singleton is { } rangeStart &&
                atomIndex + 2 < atoms.Count && atoms[atomIndex + 1].IsDash)
            {
                var rangeEndAtom = atoms[atomIndex + 2];
                if (!rangeEndAtom.IsDash && rangeEndAtom.Singleton is null)
                {
                    throw new JqRuntimeException(
                        "Regex failure: char-class value at end of range");
                }

                var rangeEnd = rangeEndAtom.IsDash ? '-' : rangeEndAtom.Singleton!.Value;
                if (rangeStart > rangeEnd)
                {
                    throw new JqRuntimeException("Regex failure: empty range in char class");
                }

                alternatives.Add(ScalarClassRange(rangeStart, rangeEnd));
                atomIndex += 2;
                continue;
            }

            alternatives.Add(atom.IsDash ? ScalarClassRange('-', '-') : atom.Pattern);
        }

        var positive = alternatives.Count == 1
            ? alternatives[0]
            : "(?:" + string.Join('|', alternatives) + ")";
        if (ignoreCase)
        {
            AppendCaseInsensitiveScalarClassAlternatives(
                alternatives,
                positive,
                includeFullFolds: !outerNegated);
            positive = alternatives.Count == 1
                ? alternatives[0]
                : "(?:" + string.Join('|', alternatives) + ")";
        }

        translated = outerNegated ? ComplementScalarPattern(positive) : positive;
        return true;
    }

    private static bool TryReadScalarPosixClass(
        string pattern,
        ref int index,
        out string scalarPattern)
    {
        scalarPattern = string.Empty;
        var cursor = index + 2;
        var negated = cursor < pattern.Length && pattern[cursor] == '^';
        if (negated)
        {
            cursor++;
        }

        var nameStart = cursor;
        while (cursor < pattern.Length && char.IsAsciiLetter(pattern[cursor]))
        {
            cursor++;
        }

        if (cursor == nameStart || cursor + 1 >= pattern.Length ||
            pattern[cursor] != ':' || pattern[cursor + 1] != ']')
        {
            return false;
        }

        scalarPattern = ScalarPosixPattern(pattern[nameStart..cursor]) ?? string.Empty;
        if (scalarPattern.Length == 0)
        {
            return false;
        }

        if (negated)
        {
            scalarPattern = ComplementScalarPattern(scalarPattern);
        }

        index = cursor + 2;
        return true;
    }

    private static bool TryReadScalarClassEscape(
        string pattern,
        ref int index,
        out string scalarPattern,
        out int? singleton,
        out bool needsScalarTranslation)
    {
        scalarPattern = string.Empty;
        singleton = null;
        needsScalarTranslation = false;
        if (index + 1 >= pattern.Length)
        {
            return false;
        }

        var escaped = pattern[index + 1];
        if (escaped == 'c' && OnigurumaCalloutAtomMatcher.TryParseEscape(
                pattern,
                index,
                out var control,
                inCharacterClass: true) &&
            control.Token.Kind == OnigurumaCalloutAtomKind.Literal)
        {
            var rune = Rune.GetRuneAt(control.Token.Literal!, 0);
            singleton = rune.Value;
            scalarPattern = ScalarClassRange(rune.Value, rune.Value);
            index = control.NextPatternIndex;
            needsScalarTranslation = true;
            return true;
        }

        if (escaped is 'w' or 'W' or 'd' or 'D' or 's' or 'S')
        {
            var positive = escaped switch
            {
                'w' or 'W' => CharacterClassWordPattern.Value,
                'd' or 'D' => ScalarDigitPattern.Value,
                _ => ScalarPropertyPattern("Space")!,
            };
            scalarPattern = escaped is 'W' or 'D' or 'S'
                ? ComplementScalarPattern(positive)
                : positive;
            index += 2;
            needsScalarTranslation = true;
            return true;
        }

        if (escaped is 'p' or 'P' &&
            TryReadUnicodeProperty(pattern, index + 2, out var propertyName, out var propertyEnd))
        {
            var propertyNegated = propertyName.StartsWith('^');
            if (propertyNegated)
            {
                propertyName = propertyName[1..];
            }

            var positive = ScalarClassPropertyPattern(propertyName);
            if (positive is null)
            {
                return false;
            }

            scalarPattern = (escaped == 'P') != propertyNegated
                ? ComplementScalarPattern(positive)
                : positive;
            index = propertyEnd + 1;
            needsScalarTranslation = true;
            return true;
        }

        if (escaped is 'x' or 'o' && TryReadRadixEscape(
                pattern,
                index + 2,
                escaped == 'x' ? 16 : 8,
                out var radixScalar,
                out var radixEnd))
        {
            singleton = radixScalar;
            scalarPattern = ScalarClassRange(radixScalar, radixScalar);
            index = radixEnd + 1;
            needsScalarTranslation = radixScalar > char.MaxValue;
            return true;
        }

        if (escaped == 'u' && TryReadFixedScalar(pattern, index + 2, 4, 16, out var unicode))
        {
            singleton = unicode;
            scalarPattern = ScalarClassRange(unicode, unicode);
            index += 6;
            return true;
        }

        if (escaped == 'x' && TryReadFixedScalar(pattern, index + 2, 2, 16, out var hexadecimal))
        {
            singleton = hexadecimal;
            scalarPattern = ScalarClassRange(hexadecimal, hexadecimal);
            index += 4;
            return true;
        }

        if (escaped is >= '0' and <= '7')
        {
            var digitEnd = index + 1;
            while (digitEnd + 1 < pattern.Length && digitEnd - index < 3 &&
                   pattern[digitEnd + 1] is >= '0' and <= '7')
            {
                digitEnd++;
            }

            try
            {
                var octal = Convert.ToInt32(pattern[(index + 1)..(digitEnd + 1)], 8);
                singleton = octal;
                scalarPattern = ScalarClassRange(octal, octal);
                index = digitEnd + 1;
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        var scalar = escaped switch
        {
            'n' => '\n',
            't' => '\t',
            'r' => '\r',
            'f' => '\f',
            'a' => '\a',
            'e' => '\u001B',
            'b' => '\b',
            _ => escaped,
        };
        singleton = scalar;
        scalarPattern = ScalarClassRange(scalar, scalar);
        index += 2;
        return true;
    }

    private static bool TryReadFixedScalar(
        string pattern,
        int start,
        int length,
        int radix,
        out int scalar)
    {
        scalar = 0;
        if (start + length > pattern.Length)
        {
            return false;
        }

        try
        {
            scalar = Convert.ToInt32(pattern.Substring(start, length), radix);
            return Rune.IsValid(scalar);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static ScalarClassAtom ScalarClassSingleton(int scalar) =>
        new(ScalarClassRange(scalar, scalar), scalar, IsDash: false);

    private static string ScalarClassRange(int start, int end) =>
        BuildScalarRangePattern([start, end]);

    private static string? ScalarClassPropertyPattern(string propertyName)
    {
        var normalized = NormalizeUnicodePropertyName(propertyName);
        if (normalized is null)
        {
            return null;
        }

        return normalized == "WORD"
            ? CharacterClassWordPattern.Value
            : ScalarPropertyPattern(propertyName);
    }

    private static void AppendCaseInsensitiveScalarClassAlternatives(
        List<string> alternatives,
        string positivePattern,
        bool includeFullFolds)
    {
        var membership = new DotNetRegex(
            "\\A(?:" + positivePattern + ")\\z",
            DotNetRegexOptions.CultureInvariant);

        // RegexOptions.IgnoreCase applies to BMP character classes, but a supplementary
        // set member is lowered to its UTF-16 surrogate pair. Close the entire set over
        // Unicode simple upper/lower mappings explicitly so properties and POSIX sets
        // receive the same treatment as literal ranges.
        var supplementaryFolds = new SortedSet<int>();
        foreach (var (source, target) in SupplementarySimpleCaseFoldMappings)
        {
            if (membership.IsMatch(new Rune(source).ToString()))
            {
                supplementaryFolds.Add(target);
            }
        }

        if (supplementaryFolds.Count != 0)
        {
            alternatives.Add(BuildScalarRangePattern(ToScalarRanges(supplementaryFolds)));
        }

        if (!includeFullFolds)
        {
            return;
        }

        // A positive Oniguruma class may consume a multi-scalar full fold (for example,
        // [ß] under /i consumes "SS"). Preserve those alternatives after the class has
        // been expanded into scalar-shaped branches. Negated classes intentionally do not
        // exclude these multi-scalar spellings; jq tests them one scalar at a time.
        foreach (var mapping in FullCaseFoldMappings)
        {
            if (mapping.Equivalents.Any(value => membership.IsMatch(value.ToString())))
            {
                alternatives.Add(DotNetRegex.Escape(mapping.Folded));
            }
        }

        if (membership.IsMatch("s") || membership.IsMatch("S"))
        {
            alternatives.Add("\\u017F");
        }
        else if (membership.IsMatch("\u017F"))
        {
            alternatives.Add("s");
        }
    }

    private static int[] ToScalarRanges(IEnumerable<int> scalars)
    {
        var ranges = new List<int>();
        var rangeStart = -1;
        var previous = -1;
        foreach (var scalar in scalars)
        {
            if (rangeStart < 0)
            {
                rangeStart = scalar;
            }
            else if (scalar != previous + 1)
            {
                ranges.Add(rangeStart);
                ranges.Add(previous);
                rangeStart = scalar;
            }

            previous = scalar;
        }

        if (rangeStart >= 0)
        {
            ranges.Add(rangeStart);
            ranges.Add(previous);
        }

        return ranges.ToArray();
    }

    private static bool TryTranslatePosixCharacterClass(
        string pattern,
        int start,
        out string translated,
        out int end)
    {
        translated = string.Empty;
        end = start;

        // Oniguruma spells POSIX classes as nested bracket expressions, for example
        // [[:alpha:]]. System.Text.RegularExpressions parses that spelling as literals,
        // so normalize standalone positive and negated classes at the compatibility edge.
        var index = start;
        if (index >= pattern.Length || pattern[index++] != '[')
        {
            return false;
        }

        var outerNegated = index < pattern.Length && pattern[index] == '^';
        if (outerNegated)
        {
            index++;
        }

        var contentsBuilder = new StringBuilder();
        var names = new List<(string Name, bool Negated)>();
        var literalCount = 0;
        while (index < pattern.Length)
        {
            if (pattern[index] == '\\' && index + 1 < pattern.Length)
            {
                contentsBuilder.Append(pattern[index]).Append(pattern[index + 1]);
                index += 2;
                literalCount++;
                continue;
            }

            if (pattern[index] == '[' && index + 1 < pattern.Length && pattern[index + 1] == ':')
            {
                var posixStart = index;
                index += 2;
                var innerNegated = index < pattern.Length && pattern[index] == '^';
                if (innerNegated)
                {
                    index++;
                }

                var nameStart = index;
                while (index < pattern.Length && char.IsAsciiLetter(pattern[index]))
                {
                    index++;
                }

                if (index == nameStart || index + 1 >= pattern.Length ||
                    pattern[index] != ':' || pattern[index + 1] != ']')
                {
                    return false;
                }

                var name = pattern[nameStart..index];
                var classContents = PosixClassContents(name);
                if (classContents is null)
                {
                    return false;
                }

                names.Add((name, innerNegated));
                if (innerNegated && (literalCount != 0 || names.Count != 1))
                {
                    // A negated POSIX set inside a union needs set algebra rather than
                    // textual class substitution. Standalone forms are handled below.
                    return false;
                }

                contentsBuilder.Append(classContents);
                index += 2;
                continue;
            }

            if (pattern[index] == ']')
            {
                break;
            }

            contentsBuilder.Append(pattern[index++]);
            literalCount++;
        }

        if (index >= pattern.Length || pattern[index] != ']' || names.Count == 0)
        {
            return false;
        }

        end = index;
        if (names.Count == 1 && literalCount == 0)
        {
            var scalarPattern = ScalarPosixPattern(names[0].Name);
            if (scalarPattern is null)
            {
                return false;
            }

            var negate = outerNegated ^ names[0].Negated;
            translated = negate ? ComplementScalarPattern(scalarPattern) : scalarPattern;
            return true;
        }

        translated = (outerNegated ? "[^" : "[") + contentsBuilder + "]";
        return true;
    }

    private static string? PosixClassContents(string name) => name switch
    {
        "alnum" => "\\p{L}\\p{Nl}\\p{Nd}",
        "alpha" => "\\p{L}\\p{Nl}",
        "ascii" => "\\x00-\\x7F",
        "blank" => "\\t\\p{Zs}",
        "cntrl" => "\\p{Cc}",
        "digit" => "\\p{Nd}",
        "graph" => "^\\p{Z}\\p{C}",
        "lower" => "\\p{Ll}",
        "print" => "^\\p{C}",
        "punct" => "\\p{P}\\p{S}",
        "space" => "\\s",
        "upper" => "\\p{Lu}\\p{Lt}",
        "word" => null,
        "xdigit" => "0-9A-Fa-f",
        _ => null,
    };

    private static string? ScalarPosixPattern(string name) => name switch
    {
        "alnum" => "(?:" + ScalarAlphabeticPattern.Value + "|" + ScalarDigitPattern.Value + ")",
        "alpha" => ScalarAlphabeticPattern.Value,
        "ascii" => "(?:[\\x00-\\x7F])",
        "blank" => "(?:[\\t\\p{Zs}])",
        "cntrl" => "(?:\\p{Cc})",
        "digit" => ScalarDigitPattern.Value,
        "graph" => "(?:(?![\\s\\p{Cc}\\p{Cn}])" + AnyScalarPattern + ")",
        "lower" => "(?:\\p{Ll})",
        "print" => "(?:(?![\\p{Cc}\\p{Cn}])" + AnyScalarPattern + ")",
        "punct" => "(?:" + ScalarPunctuationPattern.Value + "|" + ScalarSymbolPattern.Value + ")",
        "space" => "(?:\\s)",
        "upper" => "(?:[\\p{Lu}\\p{Lt}])",
        "word" => CharacterClassWordPattern.Value,
        "xdigit" => "(?:[0-9A-Fa-f])",
        _ => null,
    };

    private const string AnyScalarPattern =
        "(?:[\\uD800-\\uDBFF][\\uDC00-\\uDFFF]|[^\\uD800-\\uDFFF])";

    private static bool TryReadInlineOptions(
        string pattern,
        int openParenthesis,
        bool currentDotMode,
        bool currentExtendedMode,
        bool currentIgnoreCaseMode,
        out bool dotMode,
        out bool extendedMode,
        out bool ignoreCaseMode,
        out int end,
        out bool scoped)
    {
        dotMode = currentDotMode;
        extendedMode = currentExtendedMode;
        ignoreCaseMode = currentIgnoreCaseMode;
        end = openParenthesis;
        scoped = false;
        if (openParenthesis + 2 >= pattern.Length ||
            pattern[openParenthesis] != '(' ||
            pattern[openParenthesis + 1] != '?')
        {
            return false;
        }

        var sawOptionSyntax = false;
        var negated = false;
        for (var index = openParenthesis + 2; index < pattern.Length; index++)
        {
            var current = pattern[index];
            if (current == '-')
            {
                sawOptionSyntax = true;
                negated = true;
                continue;
            }

            if (current is ':' or ')')
            {
                if (!sawOptionSyntax)
                {
                    return false;
                }

                end = index;
                scoped = current == ':';
                return true;
            }

            if (current is not ('i' or 'm' or 's' or 'x'))
            {
                return false;
            }

            sawOptionSyntax = true;
            if (current == 's')
            {
                dotMode = !negated;
            }
            else if (current == 'x')
            {
                extendedMode = !negated;
            }
            else if (current == 'i')
            {
                ignoreCaseMode = !negated;
            }
        }

        return false;
    }

    private static void AppendNamedCapture(
        StringBuilder result,
        Dictionary<string, string?> captureNames,
        Dictionary<string, List<string>> runtimeNamesByDisplayName,
        List<string> runtimeNamesByCaptureNumber,
        string displayName,
        int captureIndex)
    {
        var runtimeName = "dotnetjq_named_" + captureIndex.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        captureNames.Add(runtimeName, displayName);
        if (!runtimeNamesByDisplayName.TryGetValue(displayName, out var names))
        {
            names = [];
            runtimeNamesByDisplayName.Add(displayName, names);
        }

        names.Add(runtimeName);
        runtimeNamesByCaptureNumber.Add(runtimeName);
        result.Append("(?<").Append(runtimeName).Append('>');
    }

    private static void AppendNamedBackreference(StringBuilder result, List<string> runtimeNames)
    {
        if (runtimeNames.Count == 1)
        {
            result.Append("\\k<").Append(runtimeNames[0]).Append('>');
            return;
        }

        result.Append("(?:");
        for (var index = runtimeNames.Count - 1; index >= 0; index--)
        {
            if (index != runtimeNames.Count - 1)
            {
                result.Append('|');
            }

            result.Append("\\k<").Append(runtimeNames[index]).Append('>');
        }

        result.Append(')');
    }

    private static bool TryReadNamedReference(
        string pattern,
        int start,
        out string name,
        out int end)
    {
        if (start < pattern.Length && pattern[start] == '<')
        {
            return TryReadGroupName(pattern, start + 1, '>', out name, out end);
        }

        if (start < pattern.Length && pattern[start] == '\'')
        {
            return TryReadGroupName(pattern, start + 1, '\'', out name, out end);
        }

        name = string.Empty;
        end = start;
        return false;
    }

    private static bool TryReadGroupName(
        string pattern,
        int start,
        char terminator,
        out string name,
        out int end)
    {
        end = pattern.IndexOf(terminator, start);
        if (end <= start)
        {
            name = string.Empty;
            return false;
        }

        name = pattern[start..end];
        return true;
    }

    private static bool IsCodePointBoundary(string value, int charIndex) =>
        charIndex <= 0 ||
        charIndex >= value.Length ||
        !(char.IsHighSurrogate(value[charIndex - 1]) && char.IsLowSurrogate(value[charIndex]));

    private static int CountCodePoints(string value, int charIndex) =>
        CountCodePoints(value, 0, charIndex);

    private static int CountCodePoints(string value, int charIndex, int charLength)
    {
        var end = charIndex + charLength;
        var count = 0;
        for (var index = charIndex; index < end; count++)
        {
            index += char.IsHighSurrogate(value[index]) &&
                     index + 1 < end &&
                     char.IsLowSurrogate(value[index + 1])
                ? 2
                : 1;
        }

        return count;
    }

    private static JqRuntimeException RegexTimeout() =>
        new("Regex failure: regular expression evaluation timed out");

    private sealed record RegexExecution(
        DotNetRegex Regex,
        bool Global,
        bool IgnoreEmpty,
        bool FindLongest,
        string TranslatedPattern,
        string OriginalPattern,
        DotNetRegexOptions Options,
        TimeSpan Timeout,
        IReadOnlyDictionary<string, string?> CaptureNames,
        IReadOnlyList<CalloutDefinition> Callouts,
        DotNetRegex? AnchoredRegex,
        IReadOnlyList<DotNetRegex> SkipProbes);

    private sealed record PatternTranslation(
        string Pattern,
        IReadOnlyDictionary<string, string?> CaptureNames,
        bool UsesClusterToken);

    private sealed record CalloutTranslation(
        string Pattern,
        IReadOnlyList<CalloutDefinition> Definitions);

    private readonly record struct OnigurumaReference(
        string Name,
        int? NumericValue,
        bool IsRelative,
        int? Level,
        int End)
    {
        internal bool IsNumeric => NumericValue.HasValue;
    }

    private enum OnigurumaNameError
    {
        None,
        InvalidGroup,
        InvalidCharacter,
    }

    private sealed record CalloutDefinition(
        int Id,
        CalloutKind Kind,
        string? Tag,
        string[] Arguments,
        bool[] ArgumentEscapes,
        bool IsConditional = false);

    private readonly record struct CalloutOperand(string? StackName, long? Literal);

    private readonly record struct ControlPattern(
        string Pattern,
        bool ContainsControl,
        bool IsControlEvent = false);

    private sealed record ScalarClassAtom(
        string Pattern,
        int? Singleton,
        bool IsDash);

    private sealed record SubexpressionDefinition(
        int Number,
        string Body,
        int Open,
        int BodyStart,
        bool Named);

    private sealed record AbsentFunction(
        int Start,
        int End,
        string Absent,
        string? Expression,
        int ExpressionStart,
        AbsentFunctionKind Kind,
        string TailName);

    private enum AbsentFunctionKind
    {
        Expression,
        Stopper,
        Clear,
    }

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

    private enum StackRelation
    {
        Equal,
        Less,
        Greater,
        LessOrEqual,
        GreaterOrEqual,
    }

    private sealed record RegexFlags(
        bool Global = false,
        bool IgnoreCase = false,
        bool Extended = false,
        bool DotMatchesNewline = false,
        bool SingleLineAnchors = false,
        bool FindLongest = false,
        bool IgnoreEmpty = false);
}
