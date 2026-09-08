// DOTNETJQ PORT MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream file: src/jv_dtoa.c
// Upstream URL: https://github.com/jqlang/jq/blob/jq-1.8.2/src/jv_dtoa.c
// Strategy: PROXY
// Target file: src/DotNetJq/Port/src/jv_dtoa.c.cs
//
// Rules:
// - Preserve upstream function names wherever possible.
// - Behavioral compatibility is defined by upstream tests/differential tests.
// - Do not refactor cross-file architecture during compatibility port.
//
// UPSTREAM COMPONENT: David Gay's arbitrary-precision dtoa and strtod implementation.
// REPLACEMENT: invariant .NET binary64 conversion followed by jq-compatible layout logic.
// WHY: .NET's runtime supplies correctly rounded IEEE-754 binary64 parsing and shortest output.
// BEHAVIORAL CONTRACT: parse only the decimal prefix consumed by jq's jvp_strtod and format
// finite values with the exponent/fixed threshold used by jvp_dtoa_fmt. Production literal-number
// projection reaches jvp_strtod after jq's 17-digit decNumberReduce bridge; jv_print reaches
// jvp_dtoa_fmt with one dtoa context per complete dump.
// KNOWN DIFFERENCES: production jq uses dtoa mode 0 only; retained modes 1 and 4-9 do not claim
// every native bigint digit-request boundary. Native allocation/free behavior is intentionally absent.
// TESTS COVERING THE SUBSTITUTION: JvDtoaDirectProxyCompatibilityTests directly covers mode 0,
// special values, production reachability, prefix consumption, overflow/underflow, and jq formatting thresholds;
// NumericCompatibilityTests, exact numeric corpora, unchanged jq.test, and differential probes
// cover the public jq result surface.

using System.Globalization;
using System.Text;

namespace DotNetJq.Port;

internal static partial class libjq
{
    private static readonly double jvp_positive_quiet_nan =
        BitConverter.UInt64BitsToDouble(0x7FF8000000000000UL);

    internal static void jvp_dtoa_context_init(dtoa_context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.IsInitialized = true;
    }

    internal static void jvp_dtoa_context_free(dtoa_context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.IsInitialized = false;
    }

    internal static double jvp_strtod(dtoa_context context, string text, out int endIndex)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(text);
        ensure_dtoa_context(context);

        endIndex = scan_decimal_prefix(text);
        if (endIndex == 0)
        {
            return 0;
        }

        var token = text.AsSpan(0, endIndex);
        if (is_signed_token(token, "inf", out var negative) ||
            is_signed_token(token, "infinity", out negative))
        {
            return negative ? double.NegativeInfinity : double.PositiveInfinity;
        }

        if (is_signed_token(token, "nan", out negative))
        {
            return negative ? -jvp_positive_quiet_nan : jvp_positive_quiet_nan;
        }

        if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        // Modern .NET returns infinity for overflow. Keep an explicit fallback for runtimes
        // whose TryParse implementation reports overflow as failure.
        var exponent = token.IndexOfAny('e', 'E');
        if (exponent >= 0 && int.TryParse(
                token[(exponent + 1)..],
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var exponentValue) && exponentValue > 0)
        {
            return token[0] == '-' ? double.NegativeInfinity : double.PositiveInfinity;
        }

        endIndex = 0;
        return 0;
    }

    internal static string jvp_dtoa(
        dtoa_context context,
        double value,
        int mode,
        int ndigits,
        out int decpt,
        out int sign,
        out int endIndex)
    {
        ArgumentNullException.ThrowIfNull(context);
        ensure_dtoa_context(context);

        sign = BitConverter.DoubleToInt64Bits(value) < 0 ? 1 : 0;
        var magnitude = Math.Abs(value);
        if (double.IsInfinity(magnitude))
        {
            decpt = 9999;
            endIndex = 8;
            return "Infinity";
        }

        if (double.IsNaN(magnitude))
        {
            decpt = 9999;
            endIndex = 3;
            return "NaN";
        }

        if (magnitude == 0)
        {
            decpt = 1;
            endIndex = 1;
            return "0";
        }

        var formatted = format_dtoa_mode(magnitude, mode, ndigits);
        var digits = extract_digits(formatted, out decpt);
        endIndex = digits.Length;
        return digits;
    }

    internal static void jvp_freedtoa(dtoa_context context, string value)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(value);
        ensure_dtoa_context(context);
    }

    internal static string jvp_dtoa_fmt(dtoa_context context, double value)
    {
        var digits = jvp_dtoa(context, value, 0, 0, out var decpt, out var sign, out _);
        var result = new StringBuilder(JVP_DTOA_FMT_MAX_LEN);
        if (sign != 0)
        {
            result.Append('-');
        }

        if (decpt == 9999)
        {
            return result.Append(digits).ToString();
        }

        if (decpt <= -4 || decpt > digits.Length + 15)
        {
            result.Append(digits[0]);
            if (digits.Length > 1)
            {
                result.Append('.').Append(digits, 1, digits.Length - 1);
            }

            result.Append('e');
            append_exponent(result, decpt - 1);
            return result.ToString();
        }

        if (decpt <= 0)
        {
            result.Append("0.");
            result.Append('0', -decpt);
            result.Append(digits);
            return result.ToString();
        }

        if (decpt < digits.Length)
        {
            result.Append(digits, 0, decpt);
            result.Append('.');
            result.Append(digits, decpt, digits.Length - decpt);
            return result.ToString();
        }

        result.Append(digits);
        result.Append('0', decpt - digits.Length);
        return result.ToString();
    }

    private static void ensure_dtoa_context(dtoa_context context)
    {
        if (!context.IsInitialized)
        {
            jvp_dtoa_context_init(context);
        }
    }

    private static int scan_decimal_prefix(string text)
    {
        var index = 0;
        if (index < text.Length && text[index] is '+' or '-')
        {
            index++;
        }

        var tokenStart = index;
        if (starts_with_ascii_ignore_case(text, index, "infinity"))
        {
            return index + "infinity".Length;
        }

        if (starts_with_ascii_ignore_case(text, index, "inf"))
        {
            return index + "inf".Length;
        }

        if (starts_with_ascii_ignore_case(text, index, "nan"))
        {
            return index + "nan".Length;
        }

        var digits = 0;
        while (index < text.Length && is_ascii_digit(text[index]))
        {
            index++;
            digits++;
        }

        if (index < text.Length && text[index] == '.')
        {
            index++;
            while (index < text.Length && is_ascii_digit(text[index]))
            {
                index++;
                digits++;
            }
        }

        if (digits == 0)
        {
            return 0;
        }

        if (index < text.Length && text[index] is 'e' or 'E')
        {
            var exponentMarker = index++;
            if (index < text.Length && text[index] is '+' or '-')
            {
                index++;
            }

            var exponentStart = index;
            while (index < text.Length && is_ascii_digit(text[index]))
            {
                index++;
            }

            if (index == exponentStart)
            {
                index = exponentMarker;
            }
        }

        return index == tokenStart ? 0 : index;
    }

    private static string format_dtoa_mode(double magnitude, int mode, int ndigits)
    {
        if (mode is < 0 or > 9)
        {
            mode = 0;
        }

        return mode switch
        {
            2 or 4 or 6 or 8 => magnitude.ToString(
                "G" + Math.Clamp(ndigits, 1, 17).ToString(CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture),
            3 or 5 or 7 or 9 when ndigits >= 0 => magnitude.ToString(
                "F" + Math.Clamp(ndigits, 0, 99).ToString(CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture),
            3 or 5 or 7 or 9 => round_to_decimal_position(magnitude, ndigits)
                .ToString("R", CultureInfo.InvariantCulture),
            _ => magnitude.ToString("R", CultureInfo.InvariantCulture),
        };
    }

    private static double round_to_decimal_position(double value, int digits)
    {
        var scale = Math.Pow(10, -digits);
        return double.IsFinite(scale)
            ? Math.Round(value / scale, MidpointRounding.ToEven) * scale
            : 0;
    }

    private static string extract_digits(string formatted, out int decpt)
    {
        var exponentMarker = formatted.IndexOfAny(['e', 'E']);
        var exponent = exponentMarker < 0
            ? 0
            : int.Parse(formatted.AsSpan(exponentMarker + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var significand = exponentMarker < 0 ? formatted.AsSpan() : formatted.AsSpan(0, exponentMarker);
        var decimalPoint = significand.IndexOf('.');
        var digitsBeforePoint = decimalPoint < 0 ? significand.Length : decimalPoint;
        var builder = new StringBuilder(significand.Length);
        foreach (var character in significand)
        {
            if (character != '.')
            {
                builder.Append(character);
            }
        }

        var hasNonZeroDigit = false;
        for (var index = 0; index < builder.Length; index++)
        {
            if (builder[index] != '0')
            {
                hasNonZeroDigit = true;
                break;
            }
        }

        if (!hasNonZeroDigit)
        {
            decpt = 1;
            return "0";
        }

        decpt = digitsBeforePoint + exponent;
        var leadingZeros = 0;
        while (leadingZeros < builder.Length - 1 && builder[leadingZeros] == '0')
        {
            leadingZeros++;
            decpt--;
        }

        if (leadingZeros != 0)
        {
            builder.Remove(0, leadingZeros);
        }

        while (builder.Length > 1 && builder[^1] == '0')
        {
            builder.Length--;
        }

        return builder.ToString();
    }

    private static bool is_signed_token(ReadOnlySpan<char> token, string expected, out bool negative)
    {
        negative = token.Length != 0 && token[0] == '-';
        if (token.Length != 0 && token[0] is '+' or '-')
        {
            token = token[1..];
        }

        return token.Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool starts_with_ascii_ignore_case(string text, int offset, string expected) =>
        offset <= text.Length - expected.Length &&
        text.AsSpan(offset, expected.Length).Equals(expected, StringComparison.OrdinalIgnoreCase);

    private static bool is_ascii_digit(char value) => value is >= '0' and <= '9';

    private static void append_exponent(StringBuilder builder, int exponent)
    {
        builder.Append(exponent < 0 ? '-' : '+');
        var magnitude = Math.Abs((long)exponent);
        if (magnitude < 10)
        {
            builder.Append('0');
        }

        builder.Append(magnitude.ToString(CultureInfo.InvariantCulture));
    }
}
