// DOTNETJQ COMPATIBILITY PROXY
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Semantic sources: vendor/oniguruma/src/regparse.c, regexec.c, unicode.c,
// unicode_property_data.c, and unicode_egcb_data.c.
//
// This file is deliberately independent of the callout control AST.  It packages the
// already-verified ordinary-atom data and matching rules so the callout event runner can
// consume them without duplicating Unicode, POSIX, newline, or text-segmentation logic.

using System.Text;
using DotNetJq.Port;
using DotNetRegex = System.Text.RegularExpressions.Regex;
using DotNetRegexTimeoutException = System.Text.RegularExpressions.RegexMatchTimeoutException;
using DotNetRegexOptions = System.Text.RegularExpressions.RegexOptions;

namespace DotNetJq.Compatibility.Regex;

internal enum OnigurumaCalloutAtomKind
{
    Literal,
    Impossible,
    Property,
    GeneralNewline,
    NotNewline,
    AnyScalar,
    WordBoundary,
    NonWordBoundary,
    TextCluster,
    TextBoundary,
    TextNonBoundary,
}

internal sealed class OnigurumaCalloutAtomToken
{
    private OnigurumaCalloutAtomToken(
        OnigurumaCalloutAtomKind kind,
        string? literal = null,
        int[]? ranges = null,
        bool negated = false,
        bool runtimeWord = false)
    {
        Kind = kind;
        Literal = literal;
        Ranges = ranges;
        Negated = negated;
        RuntimeWord = runtimeWord;
    }

    internal OnigurumaCalloutAtomKind Kind { get; }

    internal string? Literal { get; }

    internal int[]? Ranges { get; }

    internal bool Negated { get; }

    internal bool RuntimeWord { get; }

    internal bool HasMultiByteClassComponent => Kind switch
    {
        OnigurumaCalloutAtomKind.Literal =>
            Literal!.EnumerateRunes().Any(value => value.Value > 0x7F),
        OnigurumaCalloutAtomKind.Property =>
            Negated || Ranges!.Any(value => value > 0x7F),
        _ => false,
    };

    internal int MinimumUtf16Length => Kind switch
    {
        OnigurumaCalloutAtomKind.Literal => Literal!.Length,
        OnigurumaCalloutAtomKind.Impossible => 0,
        OnigurumaCalloutAtomKind.WordBoundary or
        OnigurumaCalloutAtomKind.NonWordBoundary or
        OnigurumaCalloutAtomKind.TextBoundary or
        OnigurumaCalloutAtomKind.TextNonBoundary => 0,
        _ => 1,
    };

    internal int MaximumUtf16Length => Kind switch
    {
        OnigurumaCalloutAtomKind.Literal => Literal!.Length,
        OnigurumaCalloutAtomKind.Impossible => 0,
        OnigurumaCalloutAtomKind.WordBoundary or
        OnigurumaCalloutAtomKind.NonWordBoundary or
        OnigurumaCalloutAtomKind.TextBoundary or
        OnigurumaCalloutAtomKind.TextNonBoundary => 0,
        OnigurumaCalloutAtomKind.TextCluster => -1,
        _ => 2,
    };

    internal static OnigurumaCalloutAtomToken ForLiteral(string value) =>
        new(OnigurumaCalloutAtomKind.Literal, literal: value);

    internal static OnigurumaCalloutAtomToken ForProperty(
        int[] ranges,
        bool negated,
        bool runtimeWord = false) =>
        new(
            OnigurumaCalloutAtomKind.Property,
            ranges: ranges,
            negated: negated,
            runtimeWord: runtimeWord);

    internal static OnigurumaCalloutAtomToken ForKind(OnigurumaCalloutAtomKind kind) =>
        new(kind);
}

internal readonly record struct OnigurumaCalloutAtomParseResult(
    OnigurumaCalloutAtomToken Token,
    int NextPatternIndex);

internal sealed class OnigurumaCalloutAtomMatcher
{
    private static readonly HashSet<string> PosixBracketNames = new(StringComparer.Ordinal)
    {
        "alnum", "alpha", "blank", "cntrl", "digit", "graph", "lower",
        "print", "punct", "space", "upper", "xdigit", "ascii", "word",
    };
    // unicode.c:onigenc_unicode_is_code_ctype() uses the ISO-8859-1 ctype
    // table below U+0100, but character classes are expanded from the
    // generated CR_Word range table.  Those source paths differ at exactly
    // these six Latin-1 number characters.
    private static readonly int[] CharacterClassWordRanges = RequiredRanges("WORD");
    private static readonly int[] RuntimeWordRanges = AddRanges(
        CharacterClassWordRanges,
        [0x00B2, 0x00B3, 0x00B9, 0x00B9, 0x00BC, 0x00BE]);
    private static readonly int[] PosixPunctuationRanges = RequiredRanges("POSIXPUNCT");
    private static readonly int[] DigitRanges = RequiredRanges("DIGIT");
    private static readonly int[] SpaceRanges = RequiredRanges("SPACE");
    private static readonly TimeSpan DefaultTimeout = System.Threading.Timeout.InfiniteTimeSpan;

    private readonly string _input;
    private readonly Func<TimeSpan> _remainingTimeout;
    private TextMatchers? _textMatchers;

    internal OnigurumaCalloutAtomMatcher(string input)
        : this(input, static () => DefaultTimeout)
    {
    }

    internal OnigurumaCalloutAtomMatcher(
        string input,
        Func<TimeSpan> remainingTimeout)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(remainingTimeout);
        _input = input;
        _remainingTimeout = remainingTimeout;
    }

    internal static bool TryParseEscape(
        string pattern,
        int slashIndex,
        out OnigurumaCalloutAtomParseResult result,
        bool inCharacterClass = false)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        result = default;
        if (slashIndex < 0 || slashIndex + 1 >= pattern.Length || pattern[slashIndex] != '\\')
        {
            return false;
        }

        var escapedRune = Rune.GetRuneAt(pattern, slashIndex + 1);
        var escaped = pattern[slashIndex + 1];
        var next = slashIndex + 1 + escapedRune.Utf16SequenceLength;
        if (escapedRune.Utf16SequenceLength != 1)
        {
            result = Literal(escapedRune.ToString(), next);
            return true;
        }

        switch (escaped)
        {
            case 'd':
                result = Property(DigitRanges, negated: false, next);
                return true;
            case 'D':
                result = Property(DigitRanges, negated: true, next);
                return true;
            case 'w':
                result = Property(
                    inCharacterClass ? CharacterClassWordRanges : RuntimeWordRanges,
                    negated: false,
                    next,
                    runtimeWord: !inCharacterClass);
                return true;
            case 'W':
                result = Property(
                    inCharacterClass ? CharacterClassWordRanges : RuntimeWordRanges,
                    negated: true,
                    next,
                    runtimeWord: !inCharacterClass);
                return true;
            case 's':
                result = Property(SpaceRanges, negated: false, next);
                return true;
            case 'S':
                result = Property(SpaceRanges, negated: true, next);
                return true;
            case 'b':
                result = Kind(OnigurumaCalloutAtomKind.WordBoundary, next);
                return true;
            case 'B':
                result = Kind(OnigurumaCalloutAtomKind.NonWordBoundary, next);
                return true;
            case 'R':
                result = Kind(OnigurumaCalloutAtomKind.GeneralNewline, next);
                return true;
            case 'N':
                result = Kind(OnigurumaCalloutAtomKind.NotNewline, next);
                return true;
            case 'O':
                result = Kind(OnigurumaCalloutAtomKind.AnyScalar, next);
                return true;
            case 'X':
                result = Kind(OnigurumaCalloutAtomKind.TextCluster, next);
                return true;
            case 'y':
                result = Kind(OnigurumaCalloutAtomKind.TextBoundary, next);
                return true;
            case 'Y':
                result = Kind(OnigurumaCalloutAtomKind.TextNonBoundary, next);
                return true;
            case 'n':
                result = Literal("\n", next);
                return true;
            case 'r':
                result = Literal("\r", next);
                return true;
            case 't':
                result = Literal("\t", next);
                return true;
            case 'f':
                result = Literal("\f", next);
                return true;
            case 'a':
                result = Literal("\a", next);
                return true;
            case 'e':
                result = Literal("\u001b", next);
                return true;
            case 'c':
                var controlEnd = next;
                var control = ReadControlValue(pattern, ref controlEnd);
                result = Literal(char.ConvertFromUtf32(control), controlEnd);
                return true;
            case 'Q':
            {
                var quoteEnd = pattern.IndexOf("\\E", next, StringComparison.Ordinal);
                var literalEnd = quoteEnd < 0 ? pattern.Length : quoteEnd;
                result = Literal(
                    pattern[next..literalEnd],
                    quoteEnd < 0 ? pattern.Length : quoteEnd + 2);
                return true;
            }
            case 'x':
                return TryParseRadix(pattern, next, radix: 16, out result);
            case 'o':
                if (next < pattern.Length && pattern[next] == '{')
                {
                    return TryParseRadix(pattern, next, radix: 8, out result);
                }

                result = Literal("o", next);
                return true;
            case 'p':
            case 'P':
                return TryParseProperty(
                    pattern,
                    next,
                    escaped == 'P',
                    inCharacterClass,
                    out result);
            case 'h':
            case 'H':
            case 'v':
            case 'V':
            case 'u':
            case 'C':
                result = Literal(escaped.ToString(), next);
                return true;
            default:
                if (!char.IsAsciiLetterOrDigit(escaped))
                {
                    result = Literal(escaped.ToString(), next);
                    return true;
                }

                return false;
        }
    }

    // Port of regparse.c:fetch_escaped_value_raw() for the Perl-NG syntax bits
    // enabled by jq. PFETCH_S consumes one complete UTF-8 scalar before applying
    // the control mask, including when a second escape is nested after \c.
    private static int ReadControlValue(string pattern, ref int index)
    {
        if (index >= pattern.Length)
        {
            throw new JqRuntimeException("Regex failure: end pattern at control");
        }

        var value = Rune.GetRuneAt(pattern, index);
        index += value.Utf16SequenceLength;
        if (value.Value == '?')
        {
            return 0x7f;
        }

        var scalar = value.Value;
        if (scalar == '\\')
        {
            scalar = ReadRawEscapedValue(pattern, ref index);
        }

        return scalar & 0x9f;
    }

    private static int ReadRawEscapedValue(string pattern, ref int index)
    {
        if (index >= pattern.Length)
        {
            throw new JqRuntimeException("Regex failure: end pattern at escape");
        }

        var value = Rune.GetRuneAt(pattern, index);
        index += value.Utf16SequenceLength;
        if (value.Value == 'c')
        {
            return ReadControlValue(pattern, ref index);
        }

        return value.Value switch
        {
            'n' => '\n',
            't' => '\t',
            'r' => '\r',
            'f' => '\f',
            'a' => '\a',
            'b' => '\b',
            'e' => 0x1b,
            _ => value.Value,
        };
    }

    internal static bool TryParsePosixMember(
        string pattern,
        int openBracketIndex,
        out OnigurumaCalloutAtomParseResult result)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        result = default;
        if (openBracketIndex < 0 || openBracketIndex + 4 >= pattern.Length ||
            pattern[openBracketIndex] != '[' || pattern[openBracketIndex + 1] != ':')
        {
            return false;
        }

        var close = pattern.IndexOf(":]", openBracketIndex + 2, StringComparison.Ordinal);
        if (close < 0)
        {
            return false;
        }

        var name = pattern[(openBracketIndex + 2)..close];
        var negated = name.StartsWith('^');
        var nameBody = negated ? name[1..] : name;
        if ((!negated && nameBody.Length == 0) ||
            !nameBody.EnumerateRunes().All(Rune.IsLetter))
        {
            // regparse.c:is_posix_bracket_start() only tokenizes an immediate
            // optional '^' followed by alphabetic code points as POSIX syntax.
            // Other `[:...:]` text remains ordinary class content.
            return false;
        }

        if (negated)
        {
            name = nameBody;
        }

        if (!PosixBracketNames.Contains(name))
        {
            throw new JqRuntimeException("Regex failure: invalid POSIX bracket type");
        }

        var ranges = name == "punct"
            ? PosixPunctuationRanges
            : TryPropertyRanges(name, out var propertyRanges)
                ? propertyRanges
                : throw new JqRuntimeException("Regex failure: invalid POSIX bracket type");

        result = Property(ranges, negated, close + 2);
        return true;
    }

    internal bool TryMatch(
        OnigurumaCalloutAtomToken token,
        int inputIndex,
        out int consumedUtf16)
    {
        ArgumentNullException.ThrowIfNull(token);
        consumedUtf16 = 0;
        if (inputIndex < 0 || inputIndex > _input.Length ||
            inputIndex < _input.Length && char.IsLowSurrogate(_input[inputIndex]))
        {
            return false;
        }

        switch (token.Kind)
        {
            case OnigurumaCalloutAtomKind.Literal:
                if (_input.AsSpan(inputIndex).StartsWith(token.Literal, StringComparison.Ordinal))
                {
                    consumedUtf16 = token.Literal!.Length;
                    return true;
                }

                return false;
            case OnigurumaCalloutAtomKind.Impossible:
                return false;
            case OnigurumaCalloutAtomKind.Property:
                if (!TryReadScalar(inputIndex, out var scalar, out consumedUtf16))
                {
                    consumedUtf16 = 0;
                    return false;
                }

                var contained = IsInRanges(scalar, token.Ranges!);
                if (contained != token.Negated)
                {
                    return true;
                }

                consumedUtf16 = 0;
                return false;
            case OnigurumaCalloutAtomKind.GeneralNewline:
                return TryMatchGeneralNewline(inputIndex, out consumedUtf16);
            case OnigurumaCalloutAtomKind.NotNewline:
                if (!TryReadScalar(inputIndex, out scalar, out consumedUtf16) || scalar == '\n')
                {
                    consumedUtf16 = 0;
                    return false;
                }

                return true;
            case OnigurumaCalloutAtomKind.AnyScalar:
                return TryReadScalar(inputIndex, out _, out consumedUtf16);
            case OnigurumaCalloutAtomKind.WordBoundary:
            case OnigurumaCalloutAtomKind.NonWordBoundary:
            {
                var boundary = IsWordBoundary(inputIndex);
                return token.Kind == OnigurumaCalloutAtomKind.WordBoundary
                    ? boundary
                    : !boundary;
            }
            case OnigurumaCalloutAtomKind.TextCluster:
                return TryTextMatch(inputIndex, TextAtom.Cluster, out consumedUtf16);
            case OnigurumaCalloutAtomKind.TextBoundary:
                return TryTextMatch(inputIndex, TextAtom.Boundary, out consumedUtf16);
            case OnigurumaCalloutAtomKind.TextNonBoundary:
                return TryTextMatch(inputIndex, TextAtom.NonBoundary, out consumedUtf16);
            default:
                throw new InvalidOperationException("Unknown ordinary Oniguruma atom kind.");
        }
    }

    private static bool TryParseRadix(
        string pattern,
        int valueStart,
        int radix,
        out OnigurumaCalloutAtomParseResult result)
    {
        result = default;
        if (valueStart < pattern.Length && pattern[valueStart] == '{')
        {
            var parsed = OnigurumaRadixEscape.ParseBrace(
                pattern,
                valueStart,
                radix,
                inCharacterClass: false);
            if (!parsed.Matched)
            {
                result = Literal(radix == 16 ? "x" : "o", valueStart);
                return true;
            }

            var value = new StringBuilder(parsed.Ranges.Count * 2);
            foreach (var range in parsed.Ranges)
            {
                if (range.Start != range.End || range.Start > 0x10FFFF ||
                    !Rune.IsValid((int)range.Start))
                {
                    result = Kind(OnigurumaCalloutAtomKind.Impossible, parsed.End + 1);
                    return true;
                }

                value.Append(char.ConvertFromUtf32((int)range.Start));
            }

            result = Literal(value.ToString(), parsed.End + 1);
            return true;
        }

        if (radix != 16 || valueStart >= pattern.Length)
        {
            result = Literal(radix == 16 ? "x" : "o", valueStart);
            return true;
        }

        var scalar = 0;
        var digits = 0;
        while (valueStart < pattern.Length && digits < 2 &&
            TryDigit(pattern[valueStart], radix, out var digit))
        {
            scalar = scalar * radix + digit;
            valueStart++;
            digits++;
        }

        result = Literal(char.ConvertFromUtf32(scalar), valueStart);
        return true;
    }

    private static bool TryParseProperty(
        string pattern,
        int braceIndex,
        bool escapeNegated,
        bool inCharacterClass,
        out OnigurumaCalloutAtomParseResult result)
    {
        result = default;
        if (braceIndex >= pattern.Length || pattern[braceIndex] != '{')
        {
            return false;
        }

        var close = braceIndex + 1;
        while (close < pattern.Length && pattern[close] != '}' &&
            pattern[close] is not ('(' or ')' or '{' or '|'))
        {
            close++;
        }

        if (close >= pattern.Length || pattern[close] != '}')
        {
            throw new JqRuntimeException(
                "Regex failure: end pattern with unmatched parenthesis");
        }

        var name = pattern[(braceIndex + 1)..close];
        var propertyNegated = name.StartsWith('^');
        if (propertyNegated)
        {
            name = name[1..];
        }

        var normalizedName = TryNormalizePropertyName(name, out var normalized)
            ? normalized
            : null;
        var ranges = !inCharacterClass && normalizedName == "WORD"
            ? RuntimeWordRanges
            : TryPropertyRanges(name, out var propertyRanges)
                ? propertyRanges
                : null;
        if (ranges is null)
        {
            throw new JqRuntimeException(
                "Regex failure: invalid character property name {" + name + "}");
        }

        result = Property(
            ranges,
            escapeNegated != propertyNegated,
            close + 1,
            runtimeWord: !inCharacterClass && normalizedName == "WORD");
        return true;
    }

    private bool TryReadScalar(int index, out int scalar, out int width)
    {
        scalar = 0;
        width = 0;
        if (index >= _input.Length)
        {
            return false;
        }

        scalar = char.ConvertToUtf32(_input, index);
        width = char.IsHighSurrogate(_input[index]) ? 2 : 1;
        return true;
    }

    private bool TryMatchGeneralNewline(int index, out int width)
    {
        width = 0;
        if (index >= _input.Length)
        {
            return false;
        }

        if (_input[index] == '\r' && index + 1 < _input.Length && _input[index + 1] == '\n')
        {
            width = 2;
            return true;
        }

        if (_input[index] is '\n' or '\v' or '\f' or '\r' or '\u0085' or '\u2028' or '\u2029')
        {
            width = 1;
            return true;
        }

        return false;
    }

    private bool IsWordBoundary(int index)
    {
        var beforeWord = index > 0 && IsWordScalar(ReadPreviousScalar(index));
        var afterWord = index < _input.Length &&
            TryReadScalar(index, out var after, out _) && IsWordScalar(after);
        return beforeWord != afterWord;
    }

    private int ReadPreviousScalar(int index)
    {
        var previous = index - 1;
        if (previous > 0 && char.IsLowSurrogate(_input[previous]) &&
            char.IsHighSurrogate(_input[previous - 1]))
        {
            previous--;
        }

        return char.ConvertToUtf32(_input, previous);
    }

    internal static bool IsWordScalar(int scalar) => IsInRanges(scalar, RuntimeWordRanges);

    private bool TryTextMatch(int index, TextAtom atom, out int width)
    {
        _ = GetRemainingTimeout();
        _textMatchers ??= new TextMatchers(_input);
        var pattern = atom switch
        {
            TextAtom.Cluster => _textMatchers.Cluster,
            TextAtom.Boundary => _textMatchers.Boundary,
            TextAtom.NonBoundary => _textMatchers.NonBoundary,
            _ => throw new InvalidOperationException("Unknown text atom."),
        };

        var remaining = GetRemainingTimeout();

        try
        {
            var regex = new DotNetRegex(
                "\\G(?:" + pattern + ')',
                DotNetRegexOptions.CultureInvariant,
                remaining);
            var match = regex.Match(_input, index);
            if (match.Success && match.Index == index)
            {
                width = match.Length;
                return true;
            }
        }
        catch (DotNetRegexTimeoutException)
        {
            throw RegexTimeout();
        }

        width = 0;
        return false;
    }

    private TimeSpan GetRemainingTimeout()
    {
        var remaining = _remainingTimeout();
        return remaining == System.Threading.Timeout.InfiniteTimeSpan || remaining > TimeSpan.Zero
            ? remaining
            : throw RegexTimeout();
    }

    private static JqRuntimeException RegexTimeout() =>
        new("Regex failure: regular expression evaluation timed out");

    private static bool TryPropertyRanges(string name, out int[] ranges)
    {
        ranges = [];
        return TryNormalizePropertyName(name, out var normalized) &&
            OnigurumaUnicodePropertyData.TryDecodeRanges(normalized, out ranges);
    }

    private static bool TryNormalizePropertyName(string name, out string normalizedName)
    {
        var normalized = new StringBuilder(name.Length);
        foreach (var value in name)
        {
            if (value > 0x7F)
            {
                normalizedName = string.Empty;
                return false;
            }

            if (value is not (' ' or '_' or '-'))
            {
                normalized.Append(char.ToUpperInvariant(value));
            }
        }

        normalizedName = normalized.ToString();
        return true;
    }

    private static int[] RequiredRanges(string normalizedName) =>
        OnigurumaUnicodePropertyData.TryDecodeRanges(normalizedName, out var ranges)
            ? ranges
            : throw new InvalidOperationException(
                "Pinned Oniguruma property data is missing for " + normalizedName + '.');

    private static int[] AddRanges(int[] ranges, int[] additions)
    {
        var merged = new List<(int Start, int End)>(
            ranges.Length / 2 + additions.Length / 2);
        for (var index = 0; index < ranges.Length; index += 2)
        {
            merged.Add((ranges[index], ranges[index + 1]));
        }

        for (var index = 0; index < additions.Length; index += 2)
        {
            merged.Add((additions[index], additions[index + 1]));
        }

        merged.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        var flattened = new List<int>(merged.Count * 2);
        foreach (var range in merged)
        {
            if (flattened.Count != 0 && range.Start <= flattened[^1] + 1)
            {
                flattened[^1] = Math.Max(flattened[^1], range.End);
                continue;
            }

            flattened.Add(range.Start);
            flattened.Add(range.End);
        }

        return [.. flattened];
    }

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

    private static bool TryDigit(char value, int radix, out int digit)
    {
        digit = value switch
        {
            >= '0' and <= '9' => value - '0',
            >= 'a' and <= 'f' => value - 'a' + 10,
            >= 'A' and <= 'F' => value - 'A' + 10,
            _ => -1,
        };
        return digit >= 0 && digit < radix;
    }

    private static OnigurumaCalloutAtomParseResult Literal(string value, int next) =>
        new(OnigurumaCalloutAtomToken.ForLiteral(value), next);

    private static OnigurumaCalloutAtomParseResult Property(
        int[] ranges,
        bool negated,
        int next,
        bool runtimeWord = false) =>
        new(OnigurumaCalloutAtomToken.ForProperty(ranges, negated, runtimeWord), next);

    private static OnigurumaCalloutAtomParseResult Kind(
        OnigurumaCalloutAtomKind kind,
        int next) =>
        new(OnigurumaCalloutAtomToken.ForKind(kind), next);

    private sealed class TextMatchers
    {
        internal TextMatchers(string input)
        {
            var patterns = OnigurumaTextSegmentation.Build(input);
            Cluster = patterns.Cluster;
            Boundary = patterns.Boundary;
            NonBoundary = patterns.NonBoundary;
        }

        internal string Cluster { get; }

        internal string Boundary { get; }

        internal string NonBoundary { get; }
    }

    private enum TextAtom
    {
        Cluster,
        Boundary,
        NonBoundary,
    }
}
