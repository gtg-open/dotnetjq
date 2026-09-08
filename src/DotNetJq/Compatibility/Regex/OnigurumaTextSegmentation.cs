// DOTNETJQ COMPATIBILITY PROXY
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Semantic source: vendor/oniguruma/src/unicode.c (Unicode EGCB GB3-GB13).

using System.Text;

namespace DotNetJq.Compatibility.Regex;

internal sealed record OnigurumaTextSegmentationPatterns(
    string Cluster,
    string Boundary,
    string NonBoundary);

internal static class OnigurumaTextSegmentation
{
    private const string AnyScalarPattern =
        "(?:[\\uD800-\\uDBFF][\\uDC00-\\uDFFF]|[^\\uD800-\\uDFFF])";
    private const string AnyScalarBeforePattern =
        "(?:(?<=[^\\uD800-\\uDFFF])|(?<=[\\uD800-\\uDBFF][\\uDC00-\\uDFFF]))";

    private static readonly Lazy<FixedPatterns> PinnedPatterns = new(BuildPinnedPatterns);
    private static readonly Lazy<int[]> ExtendedPictographicRanges = new(
        LoadExtendedPictographicRanges);

    internal static OnigurumaTextSegmentationPatterns Build(string input)
    {
        var pinned = PinnedPatterns.Value;
        var exceptionalPositions = FindHistoryDependentNonBoundaries(input);
        var nonBoundary = exceptionalPositions.Count == 0
            ? pinned.NonBoundary
            : "(?:" + pinned.NonBoundary + "|" +
                BuildPositionAssertions(exceptionalPositions) + ")";

        // Oniguruma expands \X to (?>\O(?:\Y\O)*). Building it from the same
        // non-boundary assertion is important: \X may itself start in the middle
        // of a cluster, where GB11 and RI parity still depend on the full input.
        var cluster = "(?>" + AnyScalarPattern +
            "(?:" + nonBoundary + AnyScalarPattern + ")*)";
        return new OnigurumaTextSegmentationPatterns(
            cluster,
            "(?!(?:" + nonBoundary + "))",
            nonBoundary);
    }

    /// <summary>
    /// Gets the UTF-16 length consumed by Oniguruma's atomic \X expansion when
    /// matching at <paramref name="charIndex"/>. The supplied index must be a
    /// Unicode scalar head; the end-of-input position returns zero.
    /// </summary>
    internal static int GetClusterLength(string input, int charIndex)
    {
        ValidateScalarPosition(input, charIndex);
        if (charIndex == input.Length)
        {
            return 0;
        }

        var end = charIndex;
        ReadScalar(input, end, out var scalarLength);
        end += scalarLength;
        while (end < input.Length && IsNonBoundaryUnchecked(input, end))
        {
            ReadScalar(input, end, out scalarLength);
            end += scalarLength;
        }

        return end - charIndex;
    }

    /// <summary>
    /// Tests the pinned GB1-GB13 boundary at a UTF-16 scalar position.
    /// </summary>
    internal static bool IsBoundary(string input, int charIndex)
    {
        ValidateScalarPosition(input, charIndex);
        return !IsNonBoundaryUnchecked(input, charIndex);
    }

    /// <summary>
    /// Tests the pinned GB3-GB13 non-boundary at a UTF-16 scalar position.
    /// </summary>
    internal static bool IsNonBoundary(string input, int charIndex)
    {
        ValidateScalarPosition(input, charIndex);
        return IsNonBoundaryUnchecked(input, charIndex);
    }

    private static bool IsNonBoundaryUnchecked(string input, int charIndex)
    {
        // GB1 and GB2.
        if (charIndex == 0 || charIndex == input.Length)
        {
            return false;
        }

        var previousStart = PreviousScalarStart(input, charIndex);
        var previousScalar = ReadScalar(input, previousStart, out _);
        var currentScalar = ReadScalar(input, charIndex, out _);
        var previousType = OnigurumaExtendedGraphemeData.GetBreakType(previousScalar);
        var currentType = OnigurumaExtendedGraphemeData.GetBreakType(currentScalar);

        // GB3, followed by the higher-priority GB4 and GB5 control breaks.
        if (previousType == OnigurumaGraphemeBreakType.Cr &&
            currentType == OnigurumaGraphemeBreakType.Lf)
        {
            return true;
        }

        if (IsControl(previousType) || IsControl(currentType))
        {
            return false;
        }

        if (IsHangul(previousType) && IsHangul(currentType))
        {
            // GB6.
            if (previousType == OnigurumaGraphemeBreakType.L &&
                currentType != OnigurumaGraphemeBreakType.T)
            {
                return true;
            }

            // GB7.
            if ((previousType is OnigurumaGraphemeBreakType.Lv or
                    OnigurumaGraphemeBreakType.V) &&
                (currentType is OnigurumaGraphemeBreakType.V or
                    OnigurumaGraphemeBreakType.T))
            {
                return true;
            }

            // GB8.
            if (currentType == OnigurumaGraphemeBreakType.T &&
                (previousType is OnigurumaGraphemeBreakType.Lvt or
                    OnigurumaGraphemeBreakType.T))
            {
                return true;
            }

            return false;
        }

        // GB9 and GB9a.
        if (currentType is OnigurumaGraphemeBreakType.Extend or
                OnigurumaGraphemeBreakType.Zwj or
                OnigurumaGraphemeBreakType.SpacingMark)
        {
            return true;
        }

        // GB9b.
        if (previousType == OnigurumaGraphemeBreakType.Prepend)
        {
            return true;
        }

        // GB11. The source checks Extended_Pictographic before testing whether
        // a scalar is Extend, so retain that ordering for overlapping properties.
        if (previousType == OnigurumaGraphemeBreakType.Zwj &&
            IsExtendedPictographic(currentScalar))
        {
            for (var scan = PreviousScalarStart(input, previousStart);
                 scan >= 0;
                 scan = PreviousScalarStart(input, scan))
            {
                var scalar = ReadScalar(input, scan, out _);
                if (IsExtendedPictographic(scalar))
                {
                    return true;
                }

                if (OnigurumaExtendedGraphemeData.GetBreakType(scalar) !=
                    OnigurumaGraphemeBreakType.Extend)
                {
                    break;
                }
            }
        }

        // GB12 and GB13. Oniguruma counts the consecutive RI scalars before
        // the immediately preceding RI and joins when that count is even.
        if (previousType == OnigurumaGraphemeBreakType.RegionalIndicator &&
            currentType == OnigurumaGraphemeBreakType.RegionalIndicator)
        {
            var earlierRegionalIndicators = 0;
            for (var scan = PreviousScalarStart(input, previousStart);
                 scan >= 0;
                 scan = PreviousScalarStart(input, scan))
            {
                var scalar = ReadScalar(input, scan, out _);
                if (OnigurumaExtendedGraphemeData.GetBreakType(scalar) !=
                    OnigurumaGraphemeBreakType.RegionalIndicator)
                {
                    break;
                }

                earlierRegionalIndicators++;
            }

            return (earlierRegionalIndicators & 1) == 0;
        }

        return false;
    }

    private static FixedPatterns BuildPinnedPatterns()
    {
        var lf = TypePattern(OnigurumaGraphemeBreakType.Lf);
        var controls = TypePattern(
            OnigurumaGraphemeBreakType.Cr,
            OnigurumaGraphemeBreakType.Lf,
            OnigurumaGraphemeBreakType.Control);
        var attachments = TypePattern(
            OnigurumaGraphemeBreakType.Extend,
            OnigurumaGraphemeBreakType.Zwj,
            OnigurumaGraphemeBreakType.SpacingMark);
        var lFollowers = TypePattern(
            OnigurumaGraphemeBreakType.L,
            OnigurumaGraphemeBreakType.V,
            OnigurumaGraphemeBreakType.Lv,
            OnigurumaGraphemeBreakType.Lvt);
        var vOrT = TypePattern(
            OnigurumaGraphemeBreakType.V,
            OnigurumaGraphemeBreakType.T);
        var t = TypePattern(OnigurumaGraphemeBreakType.T);

        var controlBefore = BuildScalarLookBehind(
            MergeTypeRanges(
                OnigurumaGraphemeBreakType.Cr,
                OnigurumaGraphemeBreakType.Lf,
                OnigurumaGraphemeBreakType.Control));
        var nonControlBefore = "(?!(?:" + controlBefore + "))" + AnyScalarBeforePattern;
        var nonControlAfter = "(?=(?!(?:" + controls + "))" + AnyScalarPattern + ")";

        // unicode_egcb_is_break_2code() rules whose decision depends only on the
        // adjacent pair. GB4/GB5 take precedence, hence the explicit non-control
        // guards around GB9, GB9a, and GB9b.
        var alternatives = new[]
        {
            BuildScalarLookBehind(GetTypeRanges(OnigurumaGraphemeBreakType.Cr)) + "(?=" + lf + ")", // GB3
            BuildScalarLookBehind(GetTypeRanges(OnigurumaGraphemeBreakType.L)) + "(?=" + lFollowers + ")", // GB6
            BuildScalarLookBehind(MergeTypeRanges(
                OnigurumaGraphemeBreakType.Lv,
                OnigurumaGraphemeBreakType.V)) + "(?=" + vOrT + ")", // GB7
            BuildScalarLookBehind(MergeTypeRanges(
                OnigurumaGraphemeBreakType.Lvt,
                OnigurumaGraphemeBreakType.T)) + "(?=" + t + ")", // GB8
            nonControlBefore + "(?=" + attachments + ")", // GB9, GB9a
            BuildScalarLookBehind(GetTypeRanges(OnigurumaGraphemeBreakType.Prepend)) + nonControlAfter, // GB9b
        };

        return new FixedPatterns("(?:" + string.Join('|', alternatives) + ")");
    }

    private static List<int> FindHistoryDependentNonBoundaries(string input)
    {
        var positions = new List<int>();
        var previousType = OnigurumaGraphemeBreakType.Other;
        var regionalIndicatorRunLength = 0;
        var suffixIsExtendedPictographicThenExtends = false;
        var previousZwjHasExtendedPictographicPrefix = false;
        var hasPrevious = false;

        for (var charIndex = 0; charIndex < input.Length;)
        {
            var scalar = ReadScalar(input, charIndex, out var scalarLength);
            var type = OnigurumaExtendedGraphemeData.GetBreakType(scalar);
            var isExtendedPictographic = IsInRanges(
                scalar,
                ExtendedPictographicRanges.Value);

            if (hasPrevious)
            {
                // GB11: \p{Extended_Pictographic} Extend* ZWJ ×
                //        \p{Extended_Pictographic}
                if (previousType == OnigurumaGraphemeBreakType.Zwj &&
                    previousZwjHasExtendedPictographicPrefix &&
                    isExtendedPictographic)
                {
                    positions.Add(charIndex);
                }

                // GB12/GB13: join an RI pair when the preceding RI run is odd.
                if (previousType == OnigurumaGraphemeBreakType.RegionalIndicator &&
                    type == OnigurumaGraphemeBreakType.RegionalIndicator &&
                    (regionalIndicatorRunLength & 1) != 0)
                {
                    positions.Add(charIndex);
                }
            }

            var currentZwjHasExtendedPictographicPrefix =
                type == OnigurumaGraphemeBreakType.Zwj &&
                suffixIsExtendedPictographicThenExtends;
            suffixIsExtendedPictographicThenExtends = isExtendedPictographic ||
                (type == OnigurumaGraphemeBreakType.Extend &&
                 suffixIsExtendedPictographicThenExtends);

            regionalIndicatorRunLength = type == OnigurumaGraphemeBreakType.RegionalIndicator
                ? regionalIndicatorRunLength + 1
                : 0;
            previousType = type;
            previousZwjHasExtendedPictographicPrefix =
                currentZwjHasExtendedPictographicPrefix;
            hasPrevious = true;
            charIndex += scalarLength;
        }

        return positions;
    }

    private static string BuildPositionAssertions(List<int> positions)
    {
        var pattern = new StringBuilder(positions.Count * 24);
        for (var index = 0; index < positions.Count; index++)
        {
            if (index != 0)
            {
                pattern.Append('|');
            }

            pattern.Append("(?<=\\A[\\s\\S]{")
                .Append(positions[index].ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append("})");
        }

        return pattern.ToString();
    }

    private static string TypePattern(params OnigurumaGraphemeBreakType[] types) =>
        BuildScalarRangePattern(MergeTypeRanges(types));

    private static int[] GetTypeRanges(OnigurumaGraphemeBreakType type) =>
        OnigurumaExtendedGraphemeData.GetRanges(type);

    private static int[] MergeTypeRanges(params OnigurumaGraphemeBreakType[] types)
    {
        var ranges = new List<(int Start, int End)>();
        foreach (var type in types)
        {
            var source = GetTypeRanges(type);
            for (var index = 0; index < source.Length; index += 2)
            {
                ranges.Add((source[index], source[index + 1]));
            }
        }

        ranges.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        var merged = new List<int>(ranges.Count * 2);
        foreach (var range in ranges)
        {
            if (merged.Count != 0 && range.Start <= merged[^1] + 1)
            {
                merged[^1] = Math.Max(merged[^1], range.End);
            }
            else
            {
                merged.Add(range.Start);
                merged.Add(range.End);
            }
        }

        return [.. merged];
    }

    private static string BuildScalarLookBehind(int[] ranges)
    {
        var bmp = new List<int>();
        var supplementary = new List<int>();
        for (var index = 0; index < ranges.Length; index += 2)
        {
            var start = ranges[index];
            var end = ranges[index + 1];
            if (start <= char.MaxValue)
            {
                bmp.Add(start);
                bmp.Add(Math.Min(end, char.MaxValue));
            }

            if (end >= 0x10000)
            {
                supplementary.Add(Math.Max(start, 0x10000));
                supplementary.Add(end);
            }
        }

        var alternatives = new List<string>(2);
        if (bmp.Count != 0)
        {
            alternatives.Add("(?<=" + BuildScalarRangePattern([.. bmp]) + ")");
        }

        if (supplementary.Count != 0)
        {
            alternatives.Add("(?<=" + BuildScalarRangePattern([.. supplementary]) + ")");
        }

        return alternatives.Count == 1
            ? alternatives[0]
            : "(?:" + string.Join('|', alternatives) + ")";
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
            if (start <= char.MaxValue)
            {
                AppendBmpRange(pattern, start, Math.Min(end, char.MaxValue), ref first);
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

    private static void AppendUtf16Escape(StringBuilder pattern, int value) =>
        pattern.Append("\\u")
            .Append(value.ToString("X4", System.Globalization.CultureInfo.InvariantCulture));

    private static int ReadScalar(string input, int charIndex, out int scalarLength)
    {
        if (char.IsHighSurrogate(input[charIndex]) &&
            charIndex + 1 < input.Length &&
            char.IsLowSurrogate(input[charIndex + 1]))
        {
            scalarLength = 2;
            return char.ConvertToUtf32(input[charIndex], input[charIndex + 1]);
        }

        scalarLength = 1;
        return input[charIndex];
    }

    private static int PreviousScalarStart(string input, int charIndex)
    {
        if (charIndex <= 0)
        {
            return -1;
        }

        return charIndex >= 2 &&
            char.IsLowSurrogate(input[charIndex - 1]) &&
            char.IsHighSurrogate(input[charIndex - 2])
                ? charIndex - 2
                : charIndex - 1;
    }

    private static void ValidateScalarPosition(string input, int charIndex)
    {
        ArgumentNullException.ThrowIfNull(input);
        if ((uint)charIndex > (uint)input.Length ||
            (charIndex < input.Length &&
             (char.IsLowSurrogate(input[charIndex]) ||
              (char.IsHighSurrogate(input[charIndex]) &&
               (charIndex + 1 == input.Length ||
                !char.IsLowSurrogate(input[charIndex + 1]))))))
        {
            throw new ArgumentOutOfRangeException(
                nameof(charIndex),
                "The index must identify a UTF-16 scalar head or the end of the input.");
        }
    }

    private static bool IsControl(OnigurumaGraphemeBreakType type) =>
        type is >= OnigurumaGraphemeBreakType.Cr and
            <= OnigurumaGraphemeBreakType.Control;

    private static bool IsHangul(OnigurumaGraphemeBreakType type) =>
        type >= OnigurumaGraphemeBreakType.L;

    private static bool IsExtendedPictographic(int scalar) =>
        IsInRanges(scalar, ExtendedPictographicRanges.Value);

    private static int[] LoadExtendedPictographicRanges() =>
        OnigurumaUnicodePropertyData.TryDecodeRanges("EXTENDEDPICTOGRAPHIC", out var ranges)
            ? ranges
            : throw new InvalidOperationException(
                "Pinned Oniguruma Extended_Pictographic data is missing.");

    private static bool IsInRanges(int scalar, int[] ranges)
    {
        var low = 0;
        var high = ranges.Length / 2;
        while (low < high)
        {
            var middle = (low + high) >> 1;
            if (scalar > ranges[middle * 2 + 1])
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low < ranges.Length / 2 && scalar >= ranges[low * 2];
    }

    private sealed record FixedPatterns(string NonBoundary);
}
