// DOTNETJQ COMPATIBILITY PORT
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Semantic sources: vendor/oniguruma/src/regcomp.c (optimizer) and regexec.c (forward search)
// UPSTREAM COMPONENT: Oniguruma exact/map search optimization and candidate scheduling.
// BEHAVIORAL CONTRACT: optimizer distances and search windows are UTF-8 byte based even
// though the managed caller consumes UTF-16 indexes. This helper deliberately has no
// dependency on the event runner's private parser nodes so it can be tested in isolation.

using System.Buffers;
using System.Text;

namespace DotNetJq.Compatibility.Regex;

internal enum OnigurumaCalloutOptimizationKind
{
    None,
    Exact,
    Map,
}

[Flags]
internal enum OnigurumaCalloutOptimizerAnchor
{
    None = 0,
    BeginBuffer = 1 << 0,
    BeginPosition = 1 << 1,
    BeginLine = 1 << 2,
    EndBuffer = 1 << 3,
    SemiEndBuffer = 1 << 4,
    EndLine = 1 << 5,
    AnyCharInfinite = 1 << 6,
    AnyCharInfiniteMultiline = 1 << 7,
    LookBehind = 1 << 8,
    NegativeLookahead = 1 << 9,
}

internal abstract record OnigurumaCalloutOptimizerNode;

internal sealed record OnigurumaCalloutOptimizerLiteral(string Value)
    : OnigurumaCalloutOptimizerNode;

internal readonly record struct OnigurumaCalloutOptimizerRange(int Low, int High);

internal sealed record OnigurumaCalloutOptimizerClass(
    IReadOnlyList<OnigurumaCalloutOptimizerRange> Ranges,
    bool Negated = false)
    : OnigurumaCalloutOptimizerNode;

internal sealed record OnigurumaCalloutOptimizerAny(bool MatchesNewline = false)
    : OnigurumaCalloutOptimizerNode;

internal sealed record OnigurumaCalloutOptimizerVariableWidth(long MinimumBytes, long? MaximumBytes)
    : OnigurumaCalloutOptimizerNode;

internal sealed record OnigurumaCalloutOptimizerZeroWidth(
    OnigurumaCalloutOptimizerAnchor Anchor = OnigurumaCalloutOptimizerAnchor.None)
    : OnigurumaCalloutOptimizerNode;

internal sealed record OnigurumaCalloutOptimizerUnknown
    : OnigurumaCalloutOptimizerNode;

internal sealed record OnigurumaCalloutOptimizerSequence(
    IReadOnlyList<OnigurumaCalloutOptimizerNode> Nodes)
    : OnigurumaCalloutOptimizerNode;

internal sealed record OnigurumaCalloutOptimizerAlternation(
    IReadOnlyList<OnigurumaCalloutOptimizerNode> Alternatives)
    : OnigurumaCalloutOptimizerNode;

internal sealed record OnigurumaCalloutOptimizerRepeat(
    OnigurumaCalloutOptimizerNode Body,
    int Minimum,
    int? Maximum,
    bool Greedy = true)
    : OnigurumaCalloutOptimizerNode;

internal readonly record struct OnigurumaCalloutCandidateWindow(
    int MinimumUtf8ByteOffset,
    int MaximumUtf8ByteOffset);

/// <summary>
/// A small, source-shaped port of Oniguruma's <c>OptNode</c> and forward candidate search.
/// It models only facts relevant to search scheduling; actual regex matching stays in the
/// event runner.
/// </summary>
internal sealed class OnigurumaCalloutOptimizationPlan
{
    private const long Infinite = long.MaxValue;
    private const int ExactMaximumLength = 24;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static readonly short[] MapPositionValues =
    [
         5,  1,  1,  1,  1,  1,  1,  1,  1, 10, 10,  1,  1, 10,  1,  1,
         1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,  1,
        12,  4,  7,  4,  4,  4,  4,  4,  4,  5,  5,  5,  5,  5,  5,  5,
         6,  6,  6,  6,  6,  6,  6,  6,  6,  6,  5,  5,  5,  5,  5,  5,
         5,  6,  6,  6,  6,  7,  6,  6,  6,  6,  6,  6,  6,  6,  6,  6,
         6,  6,  6,  6,  6,  6,  6,  6,  6,  6,  6,  5,  6,  5,  5,  5,
         5,  6,  6,  6,  6,  7,  6,  6,  6,  6,  6,  6,  6,  6,  6,  6,
         6,  6,  6,  6,  6,  6,  6,  6,  6,  6,  6,  5,  5,  5,  5,  1,
    ];

    private static readonly short[] DistanceValues =
    [
        1000, 500, 333, 250, 200, 167, 143, 125, 111, 100,
          91,  83,  77,  71,  67,  63,  59,  56,  53,  50,
          48,  45,  43,  42,  40,  38,  37,  36,  34,  33,
          32,  31,  30,  29,  29,  28,  27,  26,  26,  25,
          24,  24,  23,  23,  22,  22,  21,  21,  20,  20,
          20,  19,  19,  19,  18,  18,  18,  17,  17,  17,
          16,  16,  16,  16,  15,  15,  15,  15,  14,  14,
          14,  14,  14,  14,  13,  13,  13,  13,  13,  13,
          12,  12,  12,  12,  12,  12,  11,  11,  11,  11,
          11,  11,  11,  11,  11,  10,  10,  10,  10,  10,
    ];

    private readonly byte[] _exact;
    private readonly bool[] _map;
    private readonly LengthRange _patternLength;

    private OnigurumaCalloutOptimizationPlan(
        OnigurumaCalloutOptimizationKind kind,
        byte[] exact,
        bool[] map,
        LengthRange distance,
        LengthRange patternLength,
        OnigurumaCalloutOptimizerAnchor rootAnchor,
        OnigurumaCalloutOptimizerAnchor subAnchor)
    {
        Kind = kind;
        _exact = exact;
        _map = map;
        DistanceMinimum = distance.Minimum;
        DistanceMaximum = distance.Maximum == Infinite ? null : distance.Maximum;
        _patternLength = patternLength;
        RootAnchor = rootAnchor;
        SubAnchor = subAnchor;
        ThresholdUtf8Bytes = kind switch
        {
            OnigurumaCalloutOptimizationKind.Exact => SaturatingToInt(
                AddDistance(distance.Minimum, exact.Length)),
            OnigurumaCalloutOptimizationKind.Map => SaturatingToInt(
                AddDistance(distance.Minimum, 1)),
            _ => 0,
        };
    }

    internal OnigurumaCalloutOptimizationKind Kind { get; }

    internal long DistanceMinimum { get; }

    internal long? DistanceMaximum { get; }

    internal int ThresholdUtf8Bytes { get; }

    internal OnigurumaCalloutOptimizerAnchor RootAnchor { get; }

    internal OnigurumaCalloutOptimizerAnchor SubAnchor { get; }

    internal ReadOnlyMemory<byte> ExactUtf8Bytes => _exact;

    internal IReadOnlyList<byte> MapBytes =>
        Enumerable.Range(0, _map.Length).Where(index => _map[index]).Select(index => (byte)index).ToArray();

    internal static OnigurumaCalloutOptimizationPlan Create(
        OnigurumaCalloutOptimizerNode root,
        bool ignoreCase = false)
    {
        ArgumentNullException.ThrowIfNull(root);

        var optimizer = new Optimizer(ignoreCase);
        var optimized = optimizer.Optimize(root, LengthRange.Zero);
        var rootAnchor = optimized.Anchors.Left &
            (OnigurumaCalloutOptimizerAnchor.BeginBuffer |
             OnigurumaCalloutOptimizerAnchor.BeginPosition |
             OnigurumaCalloutOptimizerAnchor.AnyCharInfinite |
             OnigurumaCalloutOptimizerAnchor.AnyCharInfiniteMultiline |
             OnigurumaCalloutOptimizerAnchor.LookBehind);
        rootAnchor |= optimized.Anchors.Right &
            (OnigurumaCalloutOptimizerAnchor.EndBuffer |
             OnigurumaCalloutOptimizerAnchor.SemiEndBuffer |
             OnigurumaCalloutOptimizerAnchor.NegativeLookahead);

        var exact = optimized.BoundaryExact.Clone();
        SelectExact(ref exact, optimized.MiddleExact);

        if (exact.Bytes.Count > 0 &&
            (optimized.Map.Value == 0 || CompareExactAndMap(exact, optimized.Map) <= 0))
        {
            return new OnigurumaCalloutOptimizationPlan(
                OnigurumaCalloutOptimizationKind.Exact,
                exact.Bytes.ToArray(),
                new bool[256],
                exact.Position,
                optimized.Length,
                rootAnchor,
                ExactSubAnchor(exact.Anchors));
        }

        if (optimized.Map.Value > 0)
        {
            return new OnigurumaCalloutOptimizationPlan(
                OnigurumaCalloutOptimizationKind.Map,
                [],
                (bool[])optimized.Map.Bytes.Clone(),
                optimized.Map.Position,
                optimized.Length,
                rootAnchor,
                ExactSubAnchor(optimized.Map.Anchors));
        }

        return new OnigurumaCalloutOptimizationPlan(
            OnigurumaCalloutOptimizationKind.None,
            [],
            new bool[256],
            LengthRange.Zero,
            optimized.Length,
            rootAnchor,
            OnigurumaCalloutOptimizerAnchor.None);
    }

    internal IReadOnlyList<int> EnumerateCandidateStarts(string input, int searchStart = 0) =>
        Enumerate(input, searchStart).Starts;

    internal IReadOnlyList<OnigurumaCalloutCandidateWindow> EnumerateCandidateWindows(
        string input,
        int searchStart = 0) => Enumerate(input, searchStart).Windows;

    private EnumerationResult Enumerate(string input, int searchStart)
    {
        ArgumentNullException.ThrowIfNull(input);
        var text = Utf8Text.Create(input);
        var startBoundary = text.IndexOfUtf16(searchStart);
        if (startBoundary < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(searchStart),
                "Search start must be a UTF-16 scalar boundary within the input.");
        }

        var startByte = text.ByteOffsets[startBoundary];
        var rangeByte = text.Bytes.Length;
        if (!ApplyRootAnchor(text, ref startByte, ref rangeByte))
        {
            return EnumerationResult.Empty;
        }

        startBoundary = text.FirstBoundaryAtOrAfter(startByte);
        if (startBoundary < 0)
        {
            return EnumerationResult.Empty;
        }

        if (Kind == OnigurumaCalloutOptimizationKind.None)
        {
            return EnumerateUnoptimized(text, startBoundary, rangeByte);
        }

        if (text.Bytes.Length - startByte < ThresholdUtf8Bytes)
        {
            return EnumerationResult.Empty;
        }

        var maximum = DistanceMaximum ?? Infinite;
        var searchRange = maximum switch
        {
            Infinite => text.Bytes.Length,
            0 => rangeByte,
            _ when text.Bytes.Length - rangeByte < maximum => text.Bytes.Length,
            _ => SaturatingToInt(AddDistance(rangeByte, maximum)),
        };

        if (maximum == Infinite)
        {
            if (FindForward(text, startBoundary, searchRange) < 0)
            {
                return EnumerationResult.Empty;
            }

            if ((RootAnchor & OnigurumaCalloutOptimizerAnchor.AnyCharInfinite) != 0 &&
                (RootAnchor & (OnigurumaCalloutOptimizerAnchor.LookBehind |
                               OnigurumaCalloutOptimizerAnchor.NegativeLookahead)) == 0)
            {
                return EnumerateLineStarts(text, startBoundary, rangeByte);
            }

            return EnumerateUnoptimized(text, startBoundary, rangeByte);
        }

        var starts = new List<int>();
        var windows = new List<OnigurumaCalloutCandidateWindow>();
        var current = startBoundary;
        while (current >= 0 && text.ByteOffsets[current] < rangeByte)
        {
            var occurrence = FindForward(text, current, searchRange);
            if (occurrence < 0)
            {
                break;
            }

            var occurrenceByte = text.ByteOffsets[occurrence];
            var low = maximum == 0
                ? occurrenceByte
                : occurrenceByte < maximum
                    ? 0
                    : SaturatingToInt(occurrenceByte - maximum);
            if (low > text.ByteOffsets[current])
            {
                var adjusted = text.FirstBoundaryAtOrAfter(low);
                if (adjusted < 0)
                {
                    break;
                }

                low = text.ByteOffsets[adjusted];
            }

            var high = occurrenceByte < DistanceMinimum
                ? 0
                : SaturatingToInt(occurrenceByte - DistanceMinimum);
            windows.Add(new OnigurumaCalloutCandidateWindow(low, high));

            if (text.ByteOffsets[current] < low)
            {
                current = text.FirstBoundaryAtOrAfter(low);
            }

            while (current >= 0 && current < text.ByteOffsets.Count &&
                   text.ByteOffsets[current] <= high)
            {
                starts.Add(text.Utf16Offsets[current]);
                current++;
            }

            if (current < 0 || current >= text.ByteOffsets.Count ||
                text.ByteOffsets[current] >= rangeByte)
            {
                break;
            }
        }

        return new EnumerationResult(starts, windows);
    }

    private bool ApplyRootAnchor(Utf8Text text, ref int start, ref int range)
    {
        if ((RootAnchor & OnigurumaCalloutOptimizerAnchor.BeginPosition) != 0)
        {
            range = start < range ? start + 1 : start;
        }
        else if ((RootAnchor & OnigurumaCalloutOptimizerAnchor.BeginBuffer) != 0)
        {
            if (start != 0)
            {
                return false;
            }

            range = text.Bytes.Length > 0 ? 1 : 0;
        }
        else if ((RootAnchor & (OnigurumaCalloutOptimizerAnchor.EndBuffer |
                                OnigurumaCalloutOptimizerAnchor.SemiEndBuffer)) != 0)
        {
            var minimumEnd = text.Bytes.Length;
            var maximumEnd = text.Bytes.Length;
            if ((RootAnchor & OnigurumaCalloutOptimizerAnchor.SemiEndBuffer) != 0 &&
                text.Bytes.Length > 0 && text.Bytes[^1] == (byte)'\n')
            {
                minimumEnd--;
            }

            if (maximumEnd < _patternLength.Minimum)
            {
                return false;
            }

            if (_patternLength.Maximum != Infinite && minimumEnd - start > _patternLength.Maximum)
            {
                start = SaturatingToInt(minimumEnd - _patternLength.Maximum);
                var adjusted = text.FirstBoundaryAtOrAfter(start);
                if (adjusted < 0)
                {
                    return false;
                }

                start = text.ByteOffsets[adjusted];
            }

            if (maximumEnd - (range - 1L) < _patternLength.Minimum)
            {
                if (maximumEnd + 1L < _patternLength.Minimum)
                {
                    return false;
                }

                range = SaturatingToInt(maximumEnd - _patternLength.Minimum + 1);
            }

            if (start > range)
            {
                return false;
            }
        }
        else if ((RootAnchor & OnigurumaCalloutOptimizerAnchor.AnyCharInfiniteMultiline) != 0 &&
                 start < range)
        {
            range = start + 1;
        }

        return true;
    }

    private static EnumerationResult EnumerateUnoptimized(Utf8Text text, int startBoundary, int rangeByte)
    {
        var starts = new List<int>();
        var boundary = startBoundary;
        while (boundary >= 0 && boundary < text.ByteOffsets.Count)
        {
            starts.Add(text.Utf16Offsets[boundary]);
            if (text.ByteOffsets[boundary] >= rangeByte || boundary == text.ByteOffsets.Count - 1)
            {
                break;
            }

            boundary++;
        }

        return new EnumerationResult(starts, []);
    }

    private static EnumerationResult EnumerateLineStarts(Utf8Text text, int startBoundary, int rangeByte)
    {
        var starts = new List<int>();
        var boundary = startBoundary;
        while (boundary < text.ByteOffsets.Count && text.ByteOffsets[boundary] < rangeByte)
        {
            starts.Add(text.Utf16Offsets[boundary]);
            boundary++;
            while (boundary < text.ByteOffsets.Count && text.ByteOffsets[boundary] < rangeByte)
            {
                var previousByte = text.ByteOffsets[boundary - 1];
                var previousLength = text.ByteOffsets[boundary] - previousByte;
                if (previousLength == 1 && text.Bytes[previousByte] == (byte)'\n')
                {
                    break;
                }

                boundary++;
            }
        }

        return new EnumerationResult(starts, []);
    }

    private int FindForward(Utf8Text text, int startBoundary, int searchRange)
    {
        var boundary = startBoundary;
        if (DistanceMinimum != 0)
        {
            var startByte = text.ByteOffsets[boundary];
            if (text.Bytes.Length - startByte <= DistanceMinimum)
            {
                return -1;
            }

            var target = AddDistance(startByte, DistanceMinimum);
            while (boundary < text.ByteOffsets.Count && text.ByteOffsets[boundary] < target)
            {
                boundary++;
            }
        }

        while (boundary >= 0 && boundary < text.ByteOffsets.Count &&
               text.ByteOffsets[boundary] < searchRange)
        {
            if (TokenMatches(text, boundary) && SubAnchorMatches(text, boundary))
            {
                return boundary;
            }

            boundary++;
        }

        return -1;
    }

    private bool TokenMatches(Utf8Text text, int boundary)
    {
        var byteOffset = text.ByteOffsets[boundary];
        if (Kind == OnigurumaCalloutOptimizationKind.Map)
        {
            return byteOffset < text.Bytes.Length && _map[text.Bytes[byteOffset]];
        }

        return byteOffset + _exact.Length <= text.Bytes.Length &&
            text.Bytes.AsSpan(byteOffset, _exact.Length).SequenceEqual(_exact);
    }

    private bool SubAnchorMatches(Utf8Text text, int boundary)
    {
        if ((SubAnchor & OnigurumaCalloutOptimizerAnchor.BeginLine) != 0 && boundary != 0)
        {
            var previousByte = text.ByteOffsets[boundary - 1];
            var previousLength = text.ByteOffsets[boundary] - previousByte;
            if (previousLength != 1 || text.Bytes[previousByte] != (byte)'\n')
            {
                return false;
            }
        }

        if ((SubAnchor & OnigurumaCalloutOptimizerAnchor.EndLine) != 0)
        {
            var byteOffset = text.ByteOffsets[boundary];
            if (byteOffset != text.Bytes.Length && text.Bytes[byteOffset] != (byte)'\n')
            {
                return false;
            }
        }

        return true;
    }

    private static OnigurumaCalloutOptimizerAnchor ExactSubAnchor(AnchorInfo anchors) =>
        anchors.Left & OnigurumaCalloutOptimizerAnchor.BeginLine |
        anchors.Right & OnigurumaCalloutOptimizerAnchor.EndLine;

    private static int MapPositionValue(int value) =>
        value < MapPositionValues.Length ? MapPositionValues[value] : 4;

    private static int DistanceValue(LengthRange range)
    {
        if (range.Maximum == Infinite)
        {
            return 0;
        }

        var distance = range.Maximum - range.Minimum;
        return distance < DistanceValues.Length ? DistanceValues[distance] : 1;
    }

    private static int CompareDistance(LengthRange left, LengthRange right, int leftValue, int rightValue)
    {
        if (rightValue <= 0)
        {
            return -1;
        }

        if (leftValue <= 0)
        {
            return 1;
        }

        var leftScore = (long)leftValue * DistanceValue(left);
        var rightScore = (long)rightValue * DistanceValue(right);
        if (rightScore > leftScore)
        {
            return 1;
        }

        if (rightScore < leftScore)
        {
            return -1;
        }

        return right.Minimum.CompareTo(left.Minimum) switch
        {
            < 0 => 1,
            > 0 => -1,
            _ => 0,
        };
    }

    private static void SelectExact(ref ExactInfo current, ExactInfo candidate)
    {
        var currentValue = current.Bytes.Count;
        var candidateValue = candidate.Bytes.Count;
        if (candidateValue == 0)
        {
            return;
        }

        if (currentValue == 0)
        {
            current = candidate.Clone();
            return;
        }

        if (currentValue <= 2 && candidateValue <= 2)
        {
            var candidatePrice = MapPositionValue(current.Bytes[0]);
            var currentPrice = MapPositionValue(candidate.Bytes[0]);
            if (currentValue > 1)
            {
                currentPrice += 5;
            }

            if (candidateValue > 1)
            {
                candidatePrice += 5;
            }

            currentValue = currentPrice;
            candidateValue = candidatePrice;
        }

        if (CompareDistance(
                current.Position,
                candidate.Position,
                currentValue * 2,
                candidateValue * 2) > 0)
        {
            current = candidate.Clone();
        }
    }

    private static int CompareExactAndMap(ExactInfo exact, MapInfo map)
    {
        if (map.Value <= 0)
        {
            return -1;
        }

        var exactValue = 20 * exact.Bytes.Count * 3;
        var mapValue = 20 * 5 * 2 / map.Value;
        return CompareDistance(exact.Position, map.Position, exactValue, mapValue);
    }

    private static long AddDistance(long left, long right)
    {
        if (left == Infinite || right == Infinite || left > Infinite - right)
        {
            return Infinite;
        }

        return left + right;
    }

    private static long MultiplyDistance(long value, int multiplier)
    {
        if (multiplier == 0)
        {
            return 0;
        }

        return value == Infinite || value >= Infinite / multiplier
            ? Infinite
            : value * multiplier;
    }

    private static int SaturatingToInt(long value) => value >= int.MaxValue ? int.MaxValue : (int)value;

    private sealed class Optimizer(bool ignoreCase)
    {
        internal NodeInfo Optimize(OnigurumaCalloutOptimizerNode node, LengthRange position) => node switch
        {
            OnigurumaCalloutOptimizerLiteral literal => OptimizeLiteral(literal.Value, position),
            OnigurumaCalloutOptimizerClass characterClass => OptimizeClass(characterClass, position),
            OnigurumaCalloutOptimizerAny any => OptimizeAny(any, position),
            OnigurumaCalloutOptimizerVariableWidth variable => OptimizeVariable(variable, position),
            OnigurumaCalloutOptimizerZeroWidth zero => OptimizeZero(zero, position),
            OnigurumaCalloutOptimizerUnknown => OptimizeUnknown(position),
            OnigurumaCalloutOptimizerSequence sequence => OptimizeSequence(sequence.Nodes, position),
            OnigurumaCalloutOptimizerAlternation alternation =>
                OptimizeAlternation(alternation.Alternatives, position),
            OnigurumaCalloutOptimizerRepeat repeat => OptimizeRepeat(repeat, position),
            _ => throw new ArgumentOutOfRangeException(nameof(node)),
        };

        private NodeInfo OptimizeLiteral(string value, LengthRange position)
        {
            if (ignoreCase && value.Any(char.IsLetter))
            {
                return OptimizeIgnoreCaseLiteral(value, position);
            }

            var bytes = StrictUtf8.GetBytes(value);
            var result = NodeInfo.Empty(position);
            var exactLength = ScalarAlignedPrefixLength(value, ExactMaximumLength);
            if (exactLength > 0)
            {
                result.BoundaryExact.Bytes.AddRange(bytes.AsSpan(0, exactLength).ToArray());
                result.BoundaryExact.ReachesEnd = exactLength == bytes.Length;
            }

            if (bytes.Length > 0)
            {
                result.Map.Add(bytes[0]);
            }

            result.Length = new LengthRange(bytes.Length, bytes.Length);
            return result;
        }

        private NodeInfo OptimizeIgnoreCaseLiteral(string value, LengthRange position)
        {
            var nodes = new List<OnigurumaCalloutOptimizerNode>();
            var literal = new StringBuilder();

            void FlushLiteral()
            {
                if (literal.Length == 0)
                {
                    return;
                }

                nodes.Add(new OnigurumaCalloutOptimizerLiteral(literal.ToString()));
                literal.Clear();
            }

            foreach (var rune in value.EnumerateRunes())
            {
                if (!rune.IsAscii)
                {
                    FlushLiteral();
                    nodes.Add(new OnigurumaCalloutOptimizerVariableWidth(1, 4));
                    continue;
                }

                var scalar = rune.Value;
                if (scalar is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
                {
                    FlushLiteral();
                    var lower = char.ToLowerInvariant((char)scalar);
                    var upper = char.ToUpperInvariant((char)scalar);
                    var ranges = new List<OnigurumaCalloutOptimizerRange>
                    {
                        new(lower, lower),
                    };
                    if (upper != lower)
                    {
                        ranges.Add(new OnigurumaCalloutOptimizerRange(upper, upper));
                    }

                    if (lower is 'k' or 's')
                    {
                        ranges.Add(new OnigurumaCalloutOptimizerRange(
                            lower == 'k' ? 0x212a : 0x017f,
                            lower == 'k' ? 0x212a : 0x017f));
                    }

                    nodes.Add(new OnigurumaCalloutOptimizerClass(ranges));
                }
                else
                {
                    literal.Append((char)scalar);
                }
            }

            FlushLiteral();
            return OptimizeSequence(nodes, position, suppressIgnoreCase: true);
        }

        private NodeInfo OptimizeClass(OnigurumaCalloutOptimizerClass node, LengthRange position)
        {
            var result = NodeInfo.Empty(position);
            var hasMultibyte = node.Ranges.Any(range => range.Low < 0 || range.High > 0x7f);
            var hasMultibyteCaseFold = ignoreCase && node.Ranges.Any(ContainsAsciiMultibyteCaseFold);
            if (!node.Negated && !hasMultibyte && !hasMultibyteCaseFold)
            {
                foreach (var range in node.Ranges)
                {
                    var low = Math.Clamp(range.Low, 0, 0x7f);
                    var high = Math.Clamp(range.High, 0, 0x7f);
                    for (var scalar = low; scalar <= high; scalar++)
                    {
                        result.Map.Add((byte)scalar);
                        if (ignoreCase && scalar is >= 'A' and <= 'Z')
                        {
                            result.Map.Add((byte)(scalar + ('a' - 'A')));
                        }
                        else if (ignoreCase && scalar is >= 'a' and <= 'z')
                        {
                            result.Map.Add((byte)(scalar - ('a' - 'A')));
                        }
                    }
                }

                result.Length = new LengthRange(1, 1);
            }
            else
            {
                result.Length = new LengthRange(1, 4);
            }

            return result;
        }

        private static bool ContainsAsciiMultibyteCaseFold(OnigurumaCalloutOptimizerRange range) =>
            Contains(range, 'K') || Contains(range, 'k') ||
            Contains(range, 'S') || Contains(range, 's');

        private static bool Contains(OnigurumaCalloutOptimizerRange range, int scalar) =>
            range.Low <= scalar && scalar <= range.High;

        private static NodeInfo OptimizeAny(OnigurumaCalloutOptimizerAny node, LengthRange position)
        {
            var result = NodeInfo.Empty(position);
            result.Length = new LengthRange(1, 4);
            result.IsAny = true;
            result.AnyMatchesNewline = node.MatchesNewline;
            return result;
        }

        private static NodeInfo OptimizeVariable(
            OnigurumaCalloutOptimizerVariableWidth node,
            LengthRange position)
        {
            if (node.MinimumBytes < 0 || node.MaximumBytes is < 0 ||
                node.MaximumBytes is long maximum && maximum < node.MinimumBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(node));
            }

            var result = NodeInfo.Empty(position);
            result.Length = new LengthRange(node.MinimumBytes, node.MaximumBytes ?? Infinite);
            return result;
        }

        private static NodeInfo OptimizeZero(
            OnigurumaCalloutOptimizerZeroWidth node,
            LengthRange position)
        {
            var result = NodeInfo.Empty(position);
            var anchors = result.Anchors;
            anchors.Add(node.Anchor);
            result.Anchors = anchors;
            return result;
        }

        private static NodeInfo OptimizeUnknown(LengthRange position)
        {
            var result = NodeInfo.Empty(position);
            result.Length = new LengthRange(0, Infinite);
            return result;
        }

        private NodeInfo OptimizeSequence(
            IReadOnlyList<OnigurumaCalloutOptimizerNode> nodes,
            LengthRange position,
            bool suppressIgnoreCase = false)
        {
            var result = NodeInfo.Empty(position);
            var environment = position;
            foreach (var node in nodes)
            {
                var child = suppressIgnoreCase
                    ? new Optimizer(false).Optimize(node, environment)
                    : Optimize(node, environment);
                environment = environment.Add(child.Length);
                ConcatLeft(result, child);
            }

            return result;
        }

        private NodeInfo OptimizeAlternation(
            IReadOnlyList<OnigurumaCalloutOptimizerNode> alternatives,
            LengthRange position)
        {
            if (alternatives.Count == 0)
            {
                return NodeInfo.Empty(position);
            }

            var result = Optimize(alternatives[0], position).Clone();
            for (var index = 1; index < alternatives.Count; index++)
            {
                MergeAlternative(result, Optimize(alternatives[index], position));
            }

            return result;
        }

        private NodeInfo OptimizeRepeat(OnigurumaCalloutOptimizerRepeat repeat, LengthRange position)
        {
            if (repeat.Minimum < 0 || repeat.Maximum is < 0 ||
                repeat.Maximum is int maximumBound && maximumBound < repeat.Minimum)
            {
                throw new ArgumentOutOfRangeException(nameof(repeat));
            }

            var result = NodeInfo.Empty(position);
            if (repeat.Maximum == 0)
            {
                return result;
            }

            var body = Optimize(repeat.Body, position);
            if (repeat.Minimum > 0)
            {
                result = body.Clone();
                if (body.BoundaryExact.Bytes.Count > 0 && body.BoundaryExact.ReachesEnd)
                {
                    for (var count = 2;
                         count <= repeat.Minimum && result.BoundaryExact.Bytes.Count < ExactMaximumLength;
                         count++)
                    {
                        if (ConcatExact(result.BoundaryExact, body.BoundaryExact))
                        {
                            break;
                        }
                    }
                }

                if (repeat.Maximum != repeat.Minimum)
                {
                    result.BoundaryExact.ReachesEnd = false;
                    result.MiddleExact.ReachesEnd = false;
                }

                if (repeat.Minimum > 1)
                {
                    result.MiddleExact.ReachesEnd = false;
                }
            }

            var maximum = repeat.Maximum is null
                ? body.Length.Maximum > 0 ? Infinite : 0
                : MultiplyDistance(body.Length.Maximum, repeat.Maximum.Value);
            var minimum = MultiplyDistance(body.Length.Minimum, repeat.Minimum);
            result.Length = new LengthRange(minimum, maximum);

            if (repeat.Maximum is null && position.Maximum == 0 &&
                body.IsAny && repeat.Greedy)
            {
                var anchors = result.Anchors;
                anchors.Add(body.AnyMatchesNewline
                    ? OnigurumaCalloutOptimizerAnchor.AnyCharInfiniteMultiline
                    : OnigurumaCalloutOptimizerAnchor.AnyCharInfinite);
                result.Anchors = anchors;
            }

            return result;
        }

        private static void ConcatLeft(NodeInfo target, NodeInfo add)
        {
            target.Anchors = AnchorInfo.Concat(
                target.Anchors,
                add.Anchors,
                target.Length.Maximum,
                add.Length.Maximum);

            if (add.BoundaryExact.Bytes.Count > 0 && target.Length.Maximum == 0)
            {
                add.BoundaryExact.Anchors = AnchorInfo.Concat(
                    target.Anchors,
                    add.BoundaryExact.Anchors,
                    target.Length.Maximum,
                    add.Length.Maximum);
            }

            if (add.Map.Value > 0 && target.Length.Maximum == 0 && add.Map.Position.Maximum == 0)
            {
                var mapAnchors = add.Map.Anchors;
                mapAnchors.Left |= target.Anchors.Left;
                add.Map.Anchors = mapAnchors;
            }

            var boundaryReached = target.BoundaryExact.ReachesEnd;
            var middleReached = target.MiddleExact.ReachesEnd;
            if (add.Length.Maximum != 0)
            {
                target.BoundaryExact.ReachesEnd = false;
                target.MiddleExact.ReachesEnd = false;
            }

            if (add.BoundaryExact.Bytes.Count > 0)
            {
                if (boundaryReached)
                {
                    ConcatExact(target.BoundaryExact, add.BoundaryExact);
                    add.BoundaryExact.Clear();
                }
                else if (middleReached)
                {
                    ConcatExact(target.MiddleExact, add.BoundaryExact);
                    add.BoundaryExact.Clear();
                }
            }

            var middle = target.MiddleExact;
            SelectExact(ref middle, add.BoundaryExact);
            SelectExact(ref middle, add.MiddleExact);
            target.MiddleExact = middle;

            SelectMap(target.Map, add.Map);
            target.Length = target.Length.Add(add.Length);
            target.IsAny = false;
        }

        private static bool ConcatExact(ExactInfo target, ExactInfo add)
        {
            var source = add.Bytes.ToArray();
            var sourceIndex = 0;
            while (sourceIndex < source.Length)
            {
                var width = Utf8ScalarWidth(source[sourceIndex]);
                if (target.Bytes.Count + width > ExactMaximumLength)
                {
                    target.ReachesEnd = false;
                    return true;
                }

                target.Bytes.AddRange(source.AsSpan(sourceIndex, width).ToArray());
                sourceIndex += width;
            }

            target.ReachesEnd = add.ReachesEnd;
            target.Anchors = AnchorInfo.Concat(target.Anchors, add.Anchors, 1, 1);
            if (!target.ReachesEnd)
            {
                var anchors = target.Anchors;
                anchors.Right = OnigurumaCalloutOptimizerAnchor.None;
                target.Anchors = anchors;
            }

            return false;
        }

        private static void SelectMap(MapInfo current, MapInfo candidate)
        {
            if (candidate.Value == 0)
            {
                return;
            }

            if (current.Value == 0)
            {
                current.CopyFrom(candidate);
                return;
            }

            var currentValue = 32768 / current.Value;
            var candidateValue = 32768 / candidate.Value;
            if (CompareDistance(
                    current.Position,
                    candidate.Position,
                    currentValue,
                    candidateValue) > 0)
            {
                current.CopyFrom(candidate);
            }
        }

        private static void MergeAlternative(NodeInfo target, NodeInfo add)
        {
            var nodeAnchors = target.Anchors;
            nodeAnchors.Intersect(add.Anchors);
            target.Anchors = nodeAnchors;
            MergeAlternativeExact(target.BoundaryExact, add.BoundaryExact);
            MergeAlternativeExact(target.MiddleExact, add.MiddleExact);
            MergeAlternativeMap(target.Map, add.Map);
            target.Length = target.Length.Merge(add.Length);
            target.IsAny = false;
        }

        private static void MergeAlternativeExact(ExactInfo target, ExactInfo add)
        {
            if (target.Bytes.Count == 0 || add.Bytes.Count == 0 || target.Position != add.Position)
            {
                target.Clear();
                return;
            }

            var common = 0;
            var left = target.Bytes.ToArray();
            var right = add.Bytes.ToArray();
            while (common < left.Length && common < right.Length)
            {
                var width = Utf8ScalarWidth(left[common]);
                if (common + width > right.Length ||
                    !left.AsSpan(common, width).SequenceEqual(right.AsSpan(common, width)))
                {
                    break;
                }

                common += width;
            }

            if (!add.ReachesEnd || common < add.Bytes.Count || common < target.Bytes.Count)
            {
                target.ReachesEnd = false;
            }

            target.Bytes.RemoveRange(common, target.Bytes.Count - common);
            var anchors = target.Anchors;
            anchors.Intersect(add.Anchors);
            target.Anchors = anchors;
            if (!target.ReachesEnd)
            {
                var clearedAnchors = target.Anchors;
                clearedAnchors.Right = OnigurumaCalloutOptimizerAnchor.None;
                target.Anchors = clearedAnchors;
            }
        }

        private static void MergeAlternativeMap(MapInfo target, MapInfo add)
        {
            if (target.Value == 0)
            {
                return;
            }

            if (add.Value == 0 || target.Position.Maximum < add.Position.Minimum)
            {
                target.Clear();
                return;
            }

            target.Position = target.Position.Merge(add.Position);
            for (var index = 0; index < target.Bytes.Length; index++)
            {
                target.Bytes[index] |= add.Bytes[index];
            }

            target.RecalculateValue();
            var anchors = target.Anchors;
            anchors.Intersect(add.Anchors);
            target.Anchors = anchors;
        }

        private static int ScalarAlignedPrefixLength(string value, int maximumBytes)
        {
            var total = 0;
            foreach (var rune in value.EnumerateRunes())
            {
                if (total + rune.Utf8SequenceLength > maximumBytes)
                {
                    break;
                }

                total += rune.Utf8SequenceLength;
            }

            return total;
        }
    }

    private sealed class NodeInfo
    {
        internal LengthRange Length { get; set; }

        internal AnchorInfo Anchors { get; set; }

        internal ExactInfo BoundaryExact { get; set; } = new();

        internal ExactInfo MiddleExact { get; set; } = new();

        internal MapInfo Map { get; set; } = new();

        internal bool IsAny { get; set; }

        internal bool AnyMatchesNewline { get; set; }

        internal static NodeInfo Empty(LengthRange position) => new()
        {
            BoundaryExact = new ExactInfo { Position = position },
            MiddleExact = new ExactInfo(),
            Map = new MapInfo { Position = position },
        };

        internal NodeInfo Clone() => new()
        {
            Length = Length,
            Anchors = Anchors,
            BoundaryExact = BoundaryExact.Clone(),
            MiddleExact = MiddleExact.Clone(),
            Map = Map.Clone(),
            IsAny = IsAny,
            AnyMatchesNewline = AnyMatchesNewline,
        };
    }

    private sealed class ExactInfo
    {
        internal LengthRange Position { get; set; }

        internal AnchorInfo Anchors { get; set; }

        internal bool ReachesEnd { get; set; }

        internal List<byte> Bytes { get; } = [];

        internal ExactInfo Clone()
        {
            var clone = new ExactInfo
            {
                Position = Position,
                Anchors = Anchors,
                ReachesEnd = ReachesEnd,
            };
            clone.Bytes.AddRange(Bytes);
            return clone;
        }

        internal void Clear()
        {
            Position = LengthRange.Zero;
            Anchors = default;
            ReachesEnd = false;
            Bytes.Clear();
        }
    }

    private sealed class MapInfo
    {
        internal LengthRange Position { get; set; }

        internal AnchorInfo Anchors { get; set; }

        internal int Value { get; private set; }

        internal bool[] Bytes { get; } = new bool[256];

        internal void Add(byte value)
        {
            if (Bytes[value])
            {
                return;
            }

            Bytes[value] = true;
            Value += MapPositionValue(value);
        }

        internal void RecalculateValue()
        {
            Value = 0;
            for (var index = 0; index < Bytes.Length; index++)
            {
                if (Bytes[index])
                {
                    Value += MapPositionValue(index);
                }
            }
        }

        internal void Clear()
        {
            Array.Clear(Bytes);
            Value = 0;
            Position = LengthRange.Zero;
            Anchors = default;
        }

        internal void CopyFrom(MapInfo source)
        {
            Array.Copy(source.Bytes, Bytes, Bytes.Length);
            Value = source.Value;
            Position = source.Position;
            Anchors = source.Anchors;
        }

        internal MapInfo Clone()
        {
            var clone = new MapInfo();
            clone.CopyFrom(this);
            return clone;
        }
    }

    private readonly record struct LengthRange(long Minimum, long Maximum)
    {
        internal static readonly LengthRange Zero = new(0, 0);

        internal LengthRange Add(LengthRange other) => new(
            AddDistance(Minimum, other.Minimum),
            AddDistance(Maximum, other.Maximum));

        internal LengthRange Merge(LengthRange other) => new(
            Math.Min(Minimum, other.Minimum),
            Math.Max(Maximum, other.Maximum));
    }

    private struct AnchorInfo
    {
        internal OnigurumaCalloutOptimizerAnchor Left { get; set; }

        internal OnigurumaCalloutOptimizerAnchor Right { get; set; }

        internal void Add(OnigurumaCalloutOptimizerAnchor anchors)
        {
            foreach (var anchor in Enum.GetValues<OnigurumaCalloutOptimizerAnchor>())
            {
                if (anchor != OnigurumaCalloutOptimizerAnchor.None && (anchors & anchor) != 0)
                {
                    if (IsLeft(anchor))
                    {
                        Left |= anchor;
                    }
                    else
                    {
                        Right |= anchor;
                    }
                }
            }
        }

        internal void Intersect(AnchorInfo other)
        {
            Left &= other.Left;
            Right &= other.Right;
        }

        internal static AnchorInfo Concat(
            AnchorInfo left,
            AnchorInfo right,
            long leftLength,
            long rightLength)
        {
            var result = new AnchorInfo { Left = left.Left, Right = right.Right };
            if (leftLength == 0)
            {
                result.Left |= right.Left;
            }

            if (rightLength == 0)
            {
                result.Right |= left.Right;
            }
            else
            {
                result.Right |= left.Right & OnigurumaCalloutOptimizerAnchor.NegativeLookahead;
            }

            return result;
        }

        private static bool IsLeft(OnigurumaCalloutOptimizerAnchor anchor) => anchor is not
            (OnigurumaCalloutOptimizerAnchor.EndBuffer or
             OnigurumaCalloutOptimizerAnchor.SemiEndBuffer or
             OnigurumaCalloutOptimizerAnchor.EndLine or
             OnigurumaCalloutOptimizerAnchor.NegativeLookahead);
    }

    private sealed class Utf8Text
    {
        private Utf8Text(byte[] bytes, List<int> utf16Offsets, List<int> byteOffsets)
        {
            Bytes = bytes;
            Utf16Offsets = utf16Offsets;
            ByteOffsets = byteOffsets;
        }

        internal byte[] Bytes { get; }

        internal List<int> Utf16Offsets { get; }

        internal List<int> ByteOffsets { get; }

        internal static Utf8Text Create(string value)
        {
            var bytes = StrictUtf8.GetBytes(value);
            var utf16Offsets = new List<int>();
            var byteOffsets = new List<int>();
            var utf16 = 0;
            var utf8 = 0;
            while (utf16 < value.Length)
            {
                utf16Offsets.Add(utf16);
                byteOffsets.Add(utf8);
                var status = Rune.DecodeFromUtf16(value.AsSpan(utf16), out var rune, out var consumed);
                if (status != OperationStatus.Done)
                {
                    throw new ArgumentException("Input contains an invalid UTF-16 scalar sequence.", nameof(value));
                }

                utf16 += consumed;
                utf8 += rune.Utf8SequenceLength;
            }

            utf16Offsets.Add(value.Length);
            byteOffsets.Add(bytes.Length);
            return new Utf8Text(bytes, utf16Offsets, byteOffsets);
        }

        internal int IndexOfUtf16(int offset) => Utf16Offsets.BinarySearch(offset);

        internal int FirstBoundaryAtOrAfter(long byteOffset)
        {
            if (byteOffset > int.MaxValue)
            {
                return -1;
            }

            var result = ByteOffsets.BinarySearch((int)byteOffset);
            return result >= 0 ? result : ~result < ByteOffsets.Count ? ~result : -1;
        }
    }

    private sealed record EnumerationResult(
        IReadOnlyList<int> Starts,
        IReadOnlyList<OnigurumaCalloutCandidateWindow> Windows)
    {
        internal static readonly EnumerationResult Empty = new([], []);
    }

    private static int Utf8ScalarWidth(byte leadingByte) => leadingByte switch
    {
        < 0x80 => 1,
        < 0xe0 => 2,
        < 0xf0 => 3,
        _ => 4,
    };
}
