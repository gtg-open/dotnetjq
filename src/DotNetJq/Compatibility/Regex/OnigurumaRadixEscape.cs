// DOTNETJQ COMPATIBILITY PORT
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Semantic source: vendor/oniguruma/src/regparse.c (fetch_token, fetch_token_in_cc,
// check_code_point_sequence, and check_code_point_sequence_cc)
// UPSTREAM COMPONENT: Perl-NG hexadecimal/octal brace escapes and code-point lists.
// STRATEGY: PORT the pinned parser rules into a jq-shaped managed helper.
// BEHAVIORAL CONTRACT: brace lists accept only ASCII space/LF dividers; character-class
// forms also accept ranges; digit limits, ineffective escapes, and diagnostics follow the
// pinned Oniguruma parser, including code values which cannot occur in jq's valid UTF-8 input.
// TESTS COVERING THE PORT: RegexResidualFamilyBTests, OnigurumaCalloutAtomMatcherTests,
// RegexCompatibilityRound3Tests, and the exhaustive regex differential corpus.

using DotNetJq.Port;

namespace DotNetJq.Compatibility.Regex;

internal static class OnigurumaRadixEscape
{
    internal readonly record struct CodeRange(uint Start, uint End);

    internal sealed record ParseResult(
        bool Matched,
        int End,
        IReadOnlyList<CodeRange> Ranges);

    internal static ParseResult ParseBrace(
        string pattern,
        int braceStart,
        int radix,
        bool inCharacterClass)
    {
        if (braceStart >= pattern.Length || pattern[braceStart] != '{')
        {
            return new ParseResult(false, braceStart, Array.Empty<CodeRange>());
        }

        var index = braceStart + 1;
        if (!TryReadNumber(pattern, ref index, radix, out var first))
        {
            // With no initial digit Oniguruma restores the cursor to before the
            // brace and applies the syntax's ordinary ineffective-escape rule.
            return new ParseResult(false, braceStart, Array.Empty<CodeRange>());
        }

        var ranges = new List<CodeRange>();
        var pending = first;
        while (true)
        {
            if (index >= pattern.Length)
            {
                InvalidCodePoint();
            }

            if (pattern[index] == '}')
            {
                ValidateEncodable(pending, inCharacterClass);
                ranges.Add(new CodeRange(pending, pending));
                return new ParseResult(true, index, ranges);
            }

            var divided = SkipDividers(pattern, ref index);
            if (index >= pattern.Length || pattern[index] == '}')
            {
                // A divider must be followed by another value (or, in a class,
                // by the range operator and another value).
                InvalidCodePoint();
            }

            if (inCharacterClass && pattern[index] == '-')
            {
                index++;
                SkipDividers(pattern, ref index);
                if (index >= pattern.Length || pattern[index] == '}')
                {
                    throw new JqRuntimeException("Regex failure: invalid code point value");
                }

                if (!TryReadNumber(pattern, ref index, radix, out var rangeEnd))
                {
                    throw new JqRuntimeException("Regex failure: invalid code point value");
                }

                ValidateEncodable(pending, inCharacterClass);
                ValidateEncodable(rangeEnd, inCharacterClass);
                if (rangeEnd < pending)
                {
                    throw new JqRuntimeException("Regex failure: empty range in char class");
                }

                ranges.Add(new CodeRange(pending, rangeEnd));
                if (index >= pattern.Length)
                {
                    InvalidCodePoint();
                }

                if (pattern[index] == '}')
                {
                    return new ParseResult(true, index, ranges);
                }

                if (!SkipDividers(pattern, ref index) ||
                    index >= pattern.Length || pattern[index] == '}')
                {
                    InvalidCodePoint();
                }

                if (!TryReadNumber(pattern, ref index, radix, out pending))
                {
                    InvalidCodePoint();
                }

                continue;
            }

            if (!divided)
            {
                InvalidCodePoint();
            }

            ValidateEncodable(pending, inCharacterClass);
            ranges.Add(new CodeRange(pending, pending));
            if (!TryReadNumber(pattern, ref index, radix, out pending))
            {
                InvalidCodePoint();
            }
        }
    }

    private static bool TryReadNumber(string pattern, ref int index, int radix, out uint value)
    {
        value = 0;
        var digits = 0;
        var maximumDigits = radix == 16 ? 8 : 11;
        while (index < pattern.Length && TryDigit(pattern[index], radix, out var digit))
        {
            if (digits == maximumDigits)
            {
                throw new JqRuntimeException("Regex failure: too long wide-char value");
            }

            if (value > (uint.MaxValue - (uint)digit) / (uint)radix)
            {
                throw new JqRuntimeException("Regex failure: too big number");
            }

            value = value * (uint)radix + (uint)digit;
            digits++;
            index++;
        }

        return digits != 0;
    }

    private static bool TryDigit(char current, int radix, out int digit)
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

    private static bool SkipDividers(string pattern, ref int index)
    {
        var start = index;
        while (index < pattern.Length && pattern[index] is ' ' or '\n')
        {
            index++;
        }

        return index != start;
    }

    private static void ValidateEncodable(uint value, bool inCharacterClass)
    {
        // The pinned UTF-8 engine accepts class code ranges through 0x1fffff.
        // Outside a class its byte encoder accepts only lead bytes through F4,
        // which makes 0x13ffff the effective ceiling. Surrogates are accepted.
        var maximum = inCharacterClass ? 0x1F_FFFFu : 0x13_FFFFu;
        if (value > maximum)
        {
            InvalidCodePoint();
        }
    }

    private static void InvalidCodePoint() =>
        throw new JqRuntimeException("Regex failure: invalid code point value");
}
