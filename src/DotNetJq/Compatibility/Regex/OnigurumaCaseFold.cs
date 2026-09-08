// DOTNETJQ COMPATIBILITY PROXY
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Semantic sources: vendor/oniguruma/src/unicode_fold_data.c and regcomp.c.
// UPSTREAM COMPONENT: UTF-8 simple/full case-fold expansion and ignore-case matching.
// REPLACEMENT: shared managed fold catalogue and scalar-aware matching primitives.
// WHY: System.String's ordinal ignore-case comparison cannot consume the variable-length
// full folds used by Oniguruma (for example, ss <-> sharp-s), while backreferences must
// compare the actual captured text with simple folds only.
// BEHAVIORAL CONTRACT: Unicode 16 full-fold spellings, scalar-safe UTF-16 consumption,
// positive-class full folds, negated-class scalar behavior, and actual-text backreferences.
// TESTS COVERING THE SUBSTITUTION: OnigurumaCaseFoldTests and regex differential probes.

using System.Text;

namespace DotNetJq.Compatibility.Regex;

internal readonly record struct OnigurumaFullCaseFoldMapping(
    string Folded,
    string Equivalents);

internal static class OnigurumaCaseFold
{
    // Longest folded spellings must precede their prefixes. This is the same ordering used
    // by JqRegex's pinned translation path and mirrors Oniguruma's full-fold alternatives.
    private static readonly OnigurumaFullCaseFoldMapping[] FullCaseFoldMappings =
    [
        new("\u0066\u0066\u0069", "\uFB03"),
        new("\u0066\u0066\u006C", "\uFB04"),
        new("\u03B1\u0342\u03B9", "\u1FB7"),
        new("\u03B7\u0342\u03B9", "\u1FC7"),
        new("\u03B9\u0308\u0300", "\u1FD2"),
        new("\u03B9\u0308\u0301", "\u0390\u1FD3"),
        new("\u03B9\u0308\u0342", "\u1FD7"),
        new("\u03C5\u0308\u0300", "\u1FE2"),
        new("\u03C5\u0308\u0301", "\u03B0\u1FE3"),
        new("\u03C5\u0308\u0342", "\u1FE7"),
        new("\u03C5\u0313\u0300", "\u1F52"),
        new("\u03C5\u0313\u0301", "\u1F54"),
        new("\u03C5\u0313\u0342", "\u1F56"),
        new("\u03C9\u0342\u03B9", "\u1FF7"),
        new("\u0061\u02BE", "\u1E9A"),
        new("\u0066\u0066", "\uFB00"),
        new("\u0066\u0069", "\uFB01"),
        new("\u0066\u006C", "\uFB02"),
        new("\u0068\u0331", "\u1E96"),
        new("\u006A\u030C", "\u01F0"),
        new("\u0073\u0073", "\u00DF\u1E9E"),
        new("\u0073\u0074", "\uFB05\uFB06"),
        new("\u0074\u0308", "\u1E97"),
        new("\u0077\u030A", "\u1E98"),
        new("\u0079\u030A", "\u1E99"),
        new("\u02BC\u006E", "\u0149"),
        new("\u03AC\u03B9", "\u1FB4"),
        new("\u03AE\u03B9", "\u1FC4"),
        new("\u03B1\u0342", "\u1FB6"),
        new("\u03B1\u03B9", "\u1FB3\u1FBC"),
        new("\u03B7\u0342", "\u1FC6"),
        new("\u03B7\u03B9", "\u1FC3\u1FCC"),
        new("\u03B9\u0342", "\u1FD6"),
        new("\u03C1\u0313", "\u1FE4"),
        new("\u03C5\u0313", "\u1F50"),
        new("\u03C5\u0342", "\u1FE6"),
        new("\u03C9\u0342", "\u1FF6"),
        new("\u03C9\u03B9", "\u1FF3\u1FFC"),
        new("\u03CE\u03B9", "\u1FF4"),
        new("\u0565\u0582", "\u0587"),
        new("\u0574\u0565", "\uFB14"),
        new("\u0574\u056B", "\uFB15"),
        new("\u0574\u056D", "\uFB17"),
        new("\u0574\u0576", "\uFB13"),
        new("\u057E\u0576", "\uFB16"),
        new("\u1F00\u03B9", "\u1F80\u1F88"),
        new("\u1F01\u03B9", "\u1F81\u1F89"),
        new("\u1F02\u03B9", "\u1F82\u1F8A"),
        new("\u1F03\u03B9", "\u1F83\u1F8B"),
        new("\u1F04\u03B9", "\u1F84\u1F8C"),
        new("\u1F05\u03B9", "\u1F85\u1F8D"),
        new("\u1F06\u03B9", "\u1F86\u1F8E"),
        new("\u1F07\u03B9", "\u1F87\u1F8F"),
        new("\u1F20\u03B9", "\u1F90\u1F98"),
        new("\u1F21\u03B9", "\u1F91\u1F99"),
        new("\u1F22\u03B9", "\u1F92\u1F9A"),
        new("\u1F23\u03B9", "\u1F93\u1F9B"),
        new("\u1F24\u03B9", "\u1F94\u1F9C"),
        new("\u1F25\u03B9", "\u1F95\u1F9D"),
        new("\u1F26\u03B9", "\u1F96\u1F9E"),
        new("\u1F27\u03B9", "\u1F97\u1F9F"),
        new("\u1F60\u03B9", "\u1FA0\u1FA8"),
        new("\u1F61\u03B9", "\u1FA1\u1FA9"),
        new("\u1F62\u03B9", "\u1FA2\u1FAA"),
        new("\u1F63\u03B9", "\u1FA3\u1FAB"),
        new("\u1F64\u03B9", "\u1FA4\u1FAC"),
        new("\u1F65\u03B9", "\u1FA5\u1FAD"),
        new("\u1F66\u03B9", "\u1FA6\u1FAE"),
        new("\u1F67\u03B9", "\u1FA7\u1FAF"),
        new("\u1F70\u03B9", "\u1FB2"),
        new("\u1F74\u03B9", "\u1FC2"),
        new("\u1F7C\u03B9", "\u1FF2"),
        new("\u0069\u0307", "\u0130"),
    ];

    private static readonly Lazy<(int Source, int Target)[]> SupplementaryMappings =
        new(BuildSupplementarySimpleMappings);
    private static readonly Lazy<HashSet<int>> CanonicalsWithMultiByteSimplePeers =
        new(BuildCanonicalsWithMultiByteSimplePeers);

    internal static IReadOnlyList<OnigurumaFullCaseFoldMapping> FullMappings =>
        FullCaseFoldMappings;

    internal static IReadOnlyList<(int Source, int Target)> SupplementarySimpleMappings =>
        SupplementaryMappings.Value;

    /// <summary>
    /// Enumerates the UTF-16 input lengths which match the entire literal under pinned
    /// Oniguruma ignore-case semantics. Full folds may consume a different scalar count.
    /// </summary>
    internal static IEnumerable<int> EnumerateLiteralMatches(
        string input,
        int index,
        string patternLiteral,
        bool allowFullFolds = true)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(patternLiteral);
        if (!IsScalarBoundary(input, index))
        {
            return [];
        }

        var results = new List<int>();
        var seenResults = new HashSet<int>();
        var visited = new HashSet<(int PatternIndex, int InputIndex)>();

        void Visit(int patternIndex, int inputIndex)
        {
            if (!visited.Add((patternIndex, inputIndex)))
            {
                return;
            }

            if (patternIndex == patternLiteral.Length)
            {
                var consumed = inputIndex - index;
                if (seenResults.Add(consumed))
                {
                    results.Add(consumed);
                }

                return;
            }

            if (inputIndex >= input.Length)
            {
                return;
            }

            if (allowFullFolds)
            {
                foreach (var mapping in FullCaseFoldMappings)
                {
                    if (!TryGetPatternFoldWidth(patternLiteral, patternIndex, mapping, out var patternWidth))
                    {
                        continue;
                    }

                    if (StartsWithSimpleFold(input, inputIndex, mapping.Folded))
                    {
                        Visit(patternIndex + patternWidth, inputIndex + mapping.Folded.Length);
                    }

                    foreach (var equivalent in mapping.Equivalents)
                    {
                        var spelling = equivalent.ToString();
                        if (StartsWithSimpleFold(input, inputIndex, spelling))
                        {
                            Visit(patternIndex + patternWidth, inputIndex + spelling.Length);
                        }
                    }

                    // regcomp.c selects the longest full-fold mapping for the
                    // current string-node position. It does not re-segment that
                    // same source prefix into shorter fold mappings.
                    return;
                }
            }
            else
            {
                var singlePatternRune = Rune.GetRuneAt(patternLiteral, patternIndex);
                if (singlePatternRune.Utf16SequenceLength == 1)
                {
                    foreach (var mapping in FullCaseFoldMappings)
                    {
                        if (!mapping.Equivalents.Contains(
                                (char)singlePatternRune.Value,
                                StringComparison.Ordinal))
                        {
                            continue;
                        }

                        foreach (var equivalent in mapping.Equivalents)
                        {
                            if (input[inputIndex] == equivalent)
                            {
                                Visit(patternIndex + 1, inputIndex + 1);
                            }
                        }
                    }
                }
            }

            var patternRune = Rune.GetRuneAt(patternLiteral, patternIndex);
            var inputRune = Rune.GetRuneAt(input, inputIndex);
            if (SimpleEquals(patternRune, inputRune))
            {
                Visit(
                    patternIndex + patternRune.Utf16SequenceLength,
                    inputIndex + inputRune.Utf16SequenceLength);
            }
        }

        Visit(0, index);
        return results;
    }

    internal static bool RequiresFullFold(string patternLiteral)
    {
        ArgumentNullException.ThrowIfNull(patternLiteral);
        for (var index = 0; index < patternLiteral.Length;)
        {
            foreach (var mapping in FullCaseFoldMappings)
            {
                if (TryGetPatternFoldWidth(patternLiteral, index, mapping, out _))
                {
                    return true;
                }
            }

            index += Rune.GetRuneAt(patternLiteral, index).Utf16SequenceLength;
        }

        return false;
    }

    internal static bool HasCaseFoldMapping(int scalar)
    {
        var canonical = OnigurumaSimpleCaseFoldData.Canonicalize(scalar);
        var peers = 0;
        var pairs = OnigurumaSimpleCaseFoldData.MemberCanonicalMap;
        for (var index = 0; index < pairs.Length; index += 2)
        {
            if (pairs[index + 1] == canonical && ++peers > 1)
            {
                return true;
            }
        }

        if (canonical != scalar || peers != 0)
        {
            return true;
        }

        return scalar <= char.MaxValue && FullCaseFoldMappings.Any(mapping =>
            mapping.Equivalents.Contains((char)scalar, StringComparison.Ordinal));
    }

    internal static bool SimpleFoldClosureIntroducesMultiByte(
        Func<int, bool> scalarMember)
    {
        ArgumentNullException.ThrowIfNull(scalarMember);
        var relevantCanonicals = CanonicalsWithMultiByteSimplePeers.Value;
        foreach (var canonical in relevantCanonicals)
        {
            if (scalarMember(canonical))
            {
                return true;
            }
        }

        var pairs = OnigurumaSimpleCaseFoldData.MemberCanonicalMap;
        for (var index = 0; index < pairs.Length; index += 2)
        {
            if (relevantCanonicals.Contains(pairs[index + 1]) &&
                scalarMember(pairs[index]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Enumerates UTF-16 lengths consumed by a scalar class. Positive classes can consume
    /// a full-fold spelling; negated classes test one scalar and do not exclude such strings.
    /// </summary>
    internal static IEnumerable<int> EnumerateClassMatches(
        string input,
        int index,
        Func<int, bool> scalarMember,
        bool negated,
        bool allowFullFolds = true)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(scalarMember);
        if (!IsScalarBoundary(input, index) || index == input.Length)
        {
            return [];
        }

        var results = new List<int>();
        var seen = new HashSet<int>();
        var inputRune = Rune.GetRuneAt(input, index);
        var foldedMember = IsFoldedClassMember(inputRune, scalarMember);
        if (negated ? !foldedMember : foldedMember)
        {
            seen.Add(inputRune.Utf16SequenceLength);
            results.Add(inputRune.Utf16SequenceLength);
        }

        if (negated)
        {
            return results;
        }

        if (!allowFullFolds)
        {
            return results;
        }

        foreach (var mapping in FullCaseFoldMappings)
        {
            if (!mapping.Equivalents.Any(equivalent => scalarMember(equivalent)))
            {
                continue;
            }

            if (StartsWithSimpleFold(input, index, mapping.Folded) &&
                seen.Add(mapping.Folded.Length))
            {
                results.Add(mapping.Folded.Length);
            }

            foreach (var equivalent in mapping.Equivalents)
            {
                var spelling = equivalent.ToString();
                if (StartsWithSimpleFold(input, index, spelling) && seen.Add(spelling.Length))
                {
                    results.Add(spelling.Length);
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Matches the actual captured text as Oniguruma does for an ignore-case backreference.
    /// Full pattern folds are deliberately excluded: one captured sharp-s cannot consume SS.
    /// </summary>
    internal static bool TryMatchBackreference(
        string capture,
        string input,
        int index,
        out int consumed)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(input);
        consumed = 0;
        if (!IsScalarBoundary(input, index))
        {
            return false;
        }

        var captureIndex = 0;
        var inputIndex = index;
        var captureUtf8Length = 0;
        var inputUtf8Length = 0;
        while (captureIndex < capture.Length)
        {
            if (inputIndex >= input.Length)
            {
                return false;
            }

            var captureRune = Rune.GetRuneAt(capture, captureIndex);
            var inputRune = Rune.GetRuneAt(input, inputIndex);
            if (!BackreferenceScalarEquals(captureRune, inputRune))
            {
                return false;
            }

            captureUtf8Length += captureRune.Utf8SequenceLength;
            inputUtf8Length += inputRune.Utf8SequenceLength;
            captureIndex += captureRune.Utf16SequenceLength;
            inputIndex += inputRune.Utf16SequenceLength;
        }

        // regexec.c:string_cmp_ic() bounds the second operand by the captured byte
        // width. A multibyte decoder may advance past that bound while consuming its
        // final scalar, so a two-byte capture can match a three-byte simple-fold peer.
        // The reverse direction stops short of the bound and must fail.
        if (inputUtf8Length < captureUtf8Length)
        {
            return false;
        }

        consumed = inputIndex - index;
        return true;
    }

    /// <summary>
    /// Returns an exact ASCII first-byte map, or false when Unicode/full folds make a byte
    /// map observably over-inclusive for Oniguruma's candidate-attempt schedule.
    /// </summary>
    internal static bool TryGetFirstByteMap(string patternLiteral, out byte[] map)
    {
        ArgumentNullException.ThrowIfNull(patternLiteral);
        map = new byte[256];
        if (patternLiteral.Length == 0)
        {
            return false;
        }

        foreach (var mapping in FullCaseFoldMappings)
        {
            if (TryGetPatternFoldWidth(patternLiteral, 0, mapping, out _))
            {
                return false;
            }
        }

        var first = Rune.GetRuneAt(patternLiteral, 0).Value;
        if (!IsSafeAsciiMapMember(first))
        {
            return false;
        }

        AddAsciiSimpleFold(map, first);
        return true;
    }

    internal static bool TryGetFirstByteMap(
        IReadOnlyList<(int Low, int High)> ranges,
        bool negated,
        out byte[] map)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        map = new byte[256];
        if (negated || ranges.Count == 0)
        {
            return false;
        }

        foreach (var (low, high) in ranges)
        {
            if (low < 0 || high < low || high > 0x7f)
            {
                return false;
            }

            for (var scalar = low; scalar <= high; scalar++)
            {
                if (!IsSafeAsciiMapMember(scalar))
                {
                    return false;
                }

                AddAsciiSimpleFold(map, scalar);
            }
        }

        return true;
    }

    private static bool TryGetPatternFoldWidth(
        string pattern,
        int index,
        OnigurumaFullCaseFoldMapping mapping,
        out int width)
    {
        if (StartsWithSimpleFold(pattern, index, mapping.Folded))
        {
            width = mapping.Folded.Length;
            return true;
        }

        var rune = Rune.GetRuneAt(pattern, index);
        if (rune.Utf16SequenceLength == 1 &&
            mapping.Equivalents.Contains((char)rune.Value, StringComparison.Ordinal))
        {
            width = 1;
            return true;
        }

        width = 0;
        return false;
    }

    private static bool IsFoldedClassMember(Rune input, Func<int, bool> scalarMember)
    {
        if (scalarMember(input.Value))
        {
            return true;
        }

        var canonical = OnigurumaSimpleCaseFoldData.Canonicalize(input.Value);
        if (scalarMember(canonical))
        {
            return true;
        }

        var pairs = OnigurumaSimpleCaseFoldData.MemberCanonicalMap;
        for (var index = 0; index < pairs.Length; index += 2)
        {
            if (pairs[index + 1] == canonical && scalarMember(pairs[index]))
            {
                return true;
            }
        }

        if (input.Utf16SequenceLength != 1)
        {
            return false;
        }

        foreach (var mapping in FullCaseFoldMappings)
        {
            if (!mapping.Equivalents.Contains((char)input.Value, StringComparison.Ordinal))
            {
                continue;
            }

            if (mapping.Equivalents.Any(equivalent => scalarMember(equivalent)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool StartsWithSimpleFold(string input, int index, string spelling)
    {
        var inputIndex = index;
        var spellingIndex = 0;
        while (spellingIndex < spelling.Length)
        {
            if (inputIndex >= input.Length)
            {
                return false;
            }

            var inputRune = Rune.GetRuneAt(input, inputIndex);
            var spellingRune = Rune.GetRuneAt(spelling, spellingIndex);
            if (!SimpleEquals(inputRune, spellingRune))
            {
                return false;
            }

            inputIndex += inputRune.Utf16SequenceLength;
            spellingIndex += spellingRune.Utf16SequenceLength;
        }

        return true;
    }

    private static bool SimpleEquals(Rune left, Rune right) =>
        OnigurumaSimpleCaseFoldData.Canonicalize(left.Value) ==
        OnigurumaSimpleCaseFoldData.Canonicalize(right.Value);

    private static bool BackreferenceScalarEquals(Rune left, Rune right)
    {
        if (SimpleEquals(left, right))
        {
            return true;
        }

        // regexec.c advances the backreference by the captured byte width. Full
        // fold peers therefore compose only when both scalars occupy the same
        // UTF-8 width; simple folds retain their directional source behavior.
        if (left.Utf8SequenceLength != right.Utf8SequenceLength ||
            left.Utf16SequenceLength != 1 || right.Utf16SequenceLength != 1)
        {
            return false;
        }

        foreach (var mapping in FullCaseFoldMappings)
        {
            if (mapping.Equivalents.Contains((char)left.Value, StringComparison.Ordinal) &&
                mapping.Equivalents.Contains((char)right.Value, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsScalarBoundary(string value, int index) =>
        index >= 0 && index <= value.Length &&
        (index == 0 || index == value.Length ||
         !char.IsLowSurrogate(value[index]) || !char.IsHighSurrogate(value[index - 1]));

    private static bool IsSafeAsciiMapMember(int scalar)
    {
        if (scalar is < 0 or > 0x7f)
        {
            return false;
        }

        var canonical = OnigurumaSimpleCaseFoldData.Canonicalize(scalar);
        var pairs = OnigurumaSimpleCaseFoldData.MemberCanonicalMap;
        for (var index = 0; index < pairs.Length; index += 2)
        {
            if (pairs[index + 1] == canonical && pairs[index] > 0x7f)
            {
                return false;
            }
        }

        return true;
    }

    private static void AddAsciiSimpleFold(byte[] map, int scalar)
    {
        map[scalar] = 1;
        if (scalar is >= 'a' and <= 'z')
        {
            map[scalar - ('a' - 'A')] = 1;
        }
        else if (scalar is >= 'A' and <= 'Z')
        {
            map[scalar + ('a' - 'A')] = 1;
        }
    }

    private static (int Source, int Target)[] BuildSupplementarySimpleMappings()
    {
        var membersByCanonical = new Dictionary<int, HashSet<int>>();
        var pairs = OnigurumaSimpleCaseFoldData.MemberCanonicalMap;
        for (var index = 0; index < pairs.Length; index += 2)
        {
            var member = pairs[index];
            var canonical = pairs[index + 1];
            if (!membersByCanonical.TryGetValue(canonical, out var members))
            {
                members = [canonical];
                membersByCanonical.Add(canonical, members);
            }

            members.Add(member);
        }

        var mappings = new HashSet<(int Source, int Target)>();
        foreach (var members in membersByCanonical.Values)
        {
            foreach (var source in members)
            {
                if (source <= 0xFFFF)
                {
                    continue;
                }

                foreach (var target in members)
                {
                    if (target != source)
                    {
                        mappings.Add((source, target));
                    }
                }
            }
        }

        return mappings.OrderBy(mapping => mapping.Source).ThenBy(mapping => mapping.Target).ToArray();
    }

    private static HashSet<int> BuildCanonicalsWithMultiByteSimplePeers()
    {
        var result = new HashSet<int>();
        var pairs = OnigurumaSimpleCaseFoldData.MemberCanonicalMap;
        for (var index = 0; index < pairs.Length; index += 2)
        {
            if (pairs[index] > 0x7F)
            {
                result.Add(pairs[index + 1]);
            }
        }

        return result;
    }
}
