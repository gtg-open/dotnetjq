// DOTNETJQ PORT MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream subsystem: vendor/decNumber arbitrary-precision decimal representation
// Upstream URL: https://github.com/jqlang/jq/tree/jq-1.8.2/vendor/decNumber
// Strategy: PROXY
// Target file: src/DotNetJq/Compatibility/Json/ManagedDecimal.cs
// UPSTREAM COMPONENT: jq-consumed decNumber literal parsing, coefficient/exponent state,
// comparison, sign, conversion, and quantum-preserving serialization behavior.
// REPLACEMENT: System.Numerics.BigInteger coefficient plus jq compatibility logic.
// WHY: immutable managed decimal state preserves literal precision without exposing the IBM C ABI.
// BEHAVIORAL CONTRACT: retain jq literal syntax/quantum, exponent bounds, exact comparison,
// native-double projection, negation/absolute value, and jq-visible serialization.
// KNOWN DIFFERENCES: this is not the complete decNumber C API; decimal arithmetic, status-word ABI,
// alternate decimal formats, and layout identity are excluded, and ordinary jq arithmetic is binary64.
// TESTS COVERING THE SUBSTITUTION: NumericCompatibilityTests, NumericCompatibilityRound2Tests,
// JvDirectPortSymbolCompatibilityTests, exact numeric corpora, and unchanged jq.test numeric cases.

using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;

[assembly: InternalsVisibleTo("DotNetJq.Tests")]

namespace DotNetJq.Port;

internal enum ManagedDecimalKind
{
    Finite,
    Infinity,
    QuietNaN,
    SignalingNaN,
}

/// <summary>
/// An immutable, arbitrary-precision decimal in the same coefficient/exponent form as decNumber.
/// </summary>
/// <remarks>
/// jq uses this form to preserve a parsed literal's decimal quantum and precision. Ordinary jq
/// binary arithmetic is deliberately binary64; this type must not silently change that behavior.
/// </remarks>
internal readonly struct ManagedDecimal : IEquatable<ManagedDecimal>, IComparable<ManagedDecimal>
{
    private const int MaximumMaterializedPower = 1_000_000;

    private ManagedDecimal(
        BigInteger coefficient,
        int exponent,
        bool isNegative,
        ManagedDecimalKind kind)
    {
        Debug.Assert(coefficient.Sign >= 0, "The coefficient is always unsigned.");
        Coefficient = coefficient;
        Exponent = exponent;
        IsNegative = isNegative;
        Kind = kind;
    }

    internal BigInteger Coefficient { get; }

    internal int Exponent { get; }

    internal bool IsNegative { get; }

    internal ManagedDecimalKind Kind { get; }

    internal bool IsFinite => Kind == ManagedDecimalKind.Finite;

    internal bool IsInfinity => Kind == ManagedDecimalKind.Infinity;

    internal bool IsNaN => Kind is ManagedDecimalKind.QuietNaN or ManagedDecimalKind.SignalingNaN;

    internal bool IsZero => IsFinite && Coefficient.IsZero;

    internal int Digits => Coefficient.IsZero ? 1 : DecimalDigits(Coefficient);

    internal long AdjustedExponent => (long)Exponent + Digits - 1;

    internal static ManagedDecimal Zero(bool isNegative = false, int exponent = 0) =>
        new(BigInteger.Zero, exponent, isNegative, ManagedDecimalKind.Finite);

    internal static ManagedDecimal Infinity(bool isNegative = false) =>
        new(BigInteger.Zero, 0, isNegative, ManagedDecimalKind.Infinity);

    internal static ManagedDecimal NaN(
        bool signaling = false,
        bool isNegative = false,
        BigInteger payload = default) =>
        new(
            BigInteger.Abs(payload),
            0,
            isNegative,
            signaling ? ManagedDecimalKind.SignalingNaN : ManagedDecimalKind.QuietNaN);

    internal static ManagedDecimal FromInt32(int value) =>
        new(BigInteger.Abs((BigInteger)value), 0, value < 0, ManagedDecimalKind.Finite);

    internal static ManagedDecimal FromUInt32(uint value) =>
        new(new BigInteger(value), 0, false, ManagedDecimalKind.Finite);

    internal static bool TryParse(string text, decContext context, out ManagedDecimal value)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(context);

        value = NaN();
        if (text.Length == 0)
        {
            SetConversionSyntax(context);
            return false;
        }

        var index = 0;
        var isNegative = false;
        if (text[index] is '+' or '-')
        {
            isNegative = text[index] == '-';
            index++;
            if (index == text.Length)
            {
                SetConversionSyntax(context);
                return false;
            }
        }

        var unsigned = text.AsSpan(index);
        if (unsigned.Equals("Infinity", StringComparison.OrdinalIgnoreCase) ||
            unsigned.Equals("Inf", StringComparison.OrdinalIgnoreCase))
        {
            value = Infinity(isNegative);
            return true;
        }

        var signaling = unsigned.StartsWith("sNaN", StringComparison.OrdinalIgnoreCase);
        var quiet = !signaling && unsigned.StartsWith("NaN", StringComparison.OrdinalIgnoreCase);
        if (signaling || quiet)
        {
            var prefixLength = signaling ? 4 : 3;
            var payload = unsigned[prefixLength..];
            if (!AllAsciiDigits(payload))
            {
                SetConversionSyntax(context);
                return false;
            }

            value = NaN(
                signaling,
                isNegative,
                payload.IsEmpty
                    ? BigInteger.Zero
                    : BigInteger.Parse(payload, NumberStyles.None, CultureInfo.InvariantCulture));
            return true;
        }

        var coefficientText = new StringBuilder(unsigned.Length);
        var sawDigit = false;
        var sawPoint = false;
        var fractionalDigits = 0;
        var exponentStart = -1;

        for (var i = 0; i < unsigned.Length; i++)
        {
            var character = unsigned[i];
            if (character is 'e' or 'E')
            {
                exponentStart = i + 1;
                break;
            }

            if (character == '.')
            {
                if (sawPoint)
                {
                    SetConversionSyntax(context);
                    return false;
                }

                sawPoint = true;
                continue;
            }

            if (!IsAsciiDigit(character))
            {
                SetConversionSyntax(context);
                return false;
            }

            sawDigit = true;
            coefficientText.Append(character);
            if (sawPoint)
            {
                fractionalDigits++;
            }
        }

        if (!sawDigit)
        {
            SetConversionSyntax(context);
            return false;
        }

        long explicitExponent = 0;
        if (exponentStart >= 0)
        {
            if (!TryParseExponent(unsigned[exponentStart..], out explicitExponent))
            {
                SetConversionSyntax(context);
                return false;
            }
        }

        var digitsText = coefficientText.ToString().TrimStart('0');
        var coefficient = digitsText.Length == 0
            ? BigInteger.Zero
            : BigInteger.Parse(digitsText, NumberStyles.None, CultureInfo.InvariantCulture);
        var exponent = SaturatingSubtract(explicitExponent, fractionalDigits);

        value = ApplyContext(coefficient, exponent, isNegative, context);
        return true;
    }

    internal ManagedDecimal Abs(decContext context) =>
        IsNaN
            ? Quieted()
            : IsFinite
                ? ApplyContext(Coefficient, Exponent, false, context)
                : Infinity(false);

    internal ManagedDecimal Negate(decContext context)
    {
        if (IsNaN)
        {
            return Quieted();
        }

        // decNumberMinus is 0 - rhs, not the bitwise-sign operation
        // decNumberCopyNegate. For an exact zero, IEEE decimal addition gives
        // +0 except when +0 is subtracted under round-toward-negative-infinity;
        // subtracting -0 still gives +0. Preserve the source exponent/context
        // application while reproducing that sign rule.
        if (IsZero)
        {
            var negativeZero = !IsNegative && context.round == rounding.DEC_ROUND_FLOOR;
            return ApplyContext(Coefficient, Exponent, negativeZero, context);
        }

        return IsFinite
            ? ApplyContext(Coefficient, Exponent, !IsNegative, context)
            : Infinity(!IsNegative);
    }

    internal ManagedDecimal Add(ManagedDecimal other, decContext context)
    {
        if (IsNaN || other.IsNaN)
        {
            return PropagateNaN(this, other, context);
        }

        if (IsInfinity || other.IsInfinity)
        {
            if (IsInfinity && other.IsInfinity && IsNegative != other.IsNegative)
            {
                SetStatus(context, libjq.DEC_Invalid_operation);
                return NaN();
            }

            return IsInfinity ? this : other;
        }

        var commonExponent = Math.Min(Exponent, other.Exponent);
        var leftShift = checked(Exponent - commonExponent);
        var rightShift = checked(other.Exponent - commonExponent);
        if (leftShift > MaximumMaterializedPower || rightShift > MaximumMaterializedPower)
        {
            SetStatus(context, libjq.DEC_Insufficient_storage);
            return NaN();
        }

        var left = SignedCoefficient() * PowerOfTen(leftShift);
        var right = other.SignedCoefficient() * PowerOfTen(rightShift);
        var sum = left + right;
        var negative = sum.Sign < 0 ||
            (sum.IsZero && context.round == rounding.DEC_ROUND_FLOOR && (IsNegative || other.IsNegative));
        return ApplyContext(BigInteger.Abs(sum), commonExponent, negative, context);
    }

    internal ManagedDecimal Subtract(ManagedDecimal other, decContext context) =>
        Add(other.CopyNegate(), context);

    internal ManagedDecimal Multiply(ManagedDecimal other, decContext context)
    {
        if (IsNaN || other.IsNaN)
        {
            return PropagateNaN(this, other, context);
        }

        var negative = IsNegative != other.IsNegative;
        if (IsInfinity || other.IsInfinity)
        {
            if ((IsInfinity && other.IsZero) || (other.IsInfinity && IsZero))
            {
                SetStatus(context, libjq.DEC_Invalid_operation);
                return NaN();
            }

            return Infinity(negative);
        }

        return ApplyContext(
            Coefficient * other.Coefficient,
            SaturatingAdd(Exponent, other.Exponent),
            negative,
            context);
    }

    internal ManagedDecimal Divide(ManagedDecimal other, decContext context)
    {
        if (IsNaN || other.IsNaN)
        {
            return PropagateNaN(this, other, context);
        }

        var negative = IsNegative != other.IsNegative;
        if ((IsZero && other.IsZero) || (IsInfinity && other.IsInfinity))
        {
            SetStatus(context, libjq.DEC_Division_undefined);
            return NaN();
        }

        if (other.IsZero)
        {
            SetStatus(context, libjq.DEC_Division_by_zero);
            return Infinity(negative);
        }

        if (IsInfinity)
        {
            return Infinity(negative);
        }

        if (other.IsInfinity || IsZero)
        {
            var zeroExponent = Math.Clamp((long)Exponent - other.Exponent, int.MinValue, int.MaxValue);
            return Zero(negative, checked((int)zeroExponent));
        }

        if (context.digits > MaximumMaterializedPower - 2)
        {
            SetStatus(context, libjq.DEC_Insufficient_storage);
            return NaN();
        }

        var guardDigits = 2;
        var scale = Math.Max(0, context.digits + other.Digits - Digits + guardDigits);
        var scaledDividend = Coefficient * PowerOfTen(scale);
        var quotient = BigInteger.DivRem(scaledDividend, other.Coefficient, out var remainder);
        if (!remainder.IsZero && quotient % 10 == 0)
        {
            // Preserve a sticky digit so a half-way quotient is rounded in the correct direction.
            quotient += BigInteger.One;
        }

        return ApplyContext(
            quotient,
            SaturatingSubtract(SaturatingSubtract(Exponent, other.Exponent), scale),
            negative,
            context);
    }

    internal ManagedDecimal Reduce(decContext context)
    {
        var rounded = IsFinite
            ? ApplyContext(Coefficient, Exponent, IsNegative, context)
            : Quieted();
        if (!rounded.IsFinite || rounded.Coefficient.IsZero)
        {
            return rounded.IsZero ? Zero(rounded.IsNegative) : rounded;
        }

        var coefficient = rounded.Coefficient;
        var exponent = rounded.Exponent;
        while (exponent < context.emax && coefficient % 10 == 0)
        {
            coefficient /= 10;
            exponent++;
        }

        return new ManagedDecimal(coefficient, exponent, rounded.IsNegative, ManagedDecimalKind.Finite);
    }

    internal ManagedDecimal CopyAbs() =>
        new(Coefficient, Exponent, false, Kind);

    internal ManagedDecimal CopyNegate() =>
        new(Coefficient, Exponent, !IsNegative, Kind);

    internal ManagedDecimal CopySign(ManagedDecimal sign) =>
        new(Coefficient, Exponent, sign.IsNegative, Kind);

    internal ManagedDecimal WithCoefficient(BigInteger coefficient) =>
        new(BigInteger.Abs(coefficient), Exponent, IsNegative, Kind);

    internal string ToScientificString()
    {
        if (!IsFinite)
        {
            return SpecialToString();
        }

        var coefficient = Coefficient.ToString(CultureInfo.InvariantCulture);
        var adjustedExponent = AdjustedExponent;
        string magnitude;
        if (Exponent <= 0 && adjustedExponent >= -6)
        {
            var point = coefficient.Length + Exponent;
            if (point > 0)
            {
                magnitude = point == coefficient.Length
                    ? coefficient
                    : string.Concat(coefficient.AsSpan(0, point), ".", coefficient.AsSpan(point));
            }
            else
            {
                magnitude = string.Concat("0.", new string('0', -point), coefficient);
            }
        }
        else
        {
            magnitude = coefficient.Length == 1
                ? coefficient
                : string.Concat(coefficient.AsSpan(0, 1), ".", coefficient.AsSpan(1));
            magnitude = string.Concat(magnitude, FormatExponent(adjustedExponent));
        }

        return IsNegative ? string.Concat("-", magnitude) : magnitude;
    }

    internal string ToEngineeringString()
    {
        if (!IsFinite)
        {
            return SpecialToString();
        }

        var coefficient = Coefficient.ToString(CultureInfo.InvariantCulture);
        var adjusted = AdjustedExponent;
        if (Exponent <= 0 && adjusted >= -6)
        {
            return ToScientificString();
        }

        var engineeringExponent = adjusted - Modulo(adjusted, 3);
        var integerDigits = checked((int)(adjusted - engineeringExponent + 1));
        string significand;
        if (coefficient.Length < integerDigits)
        {
            significand = coefficient + new string('0', integerDigits - coefficient.Length);
        }
        else if (coefficient.Length == integerDigits)
        {
            significand = coefficient;
        }
        else
        {
            significand = string.Concat(
                coefficient.AsSpan(0, integerDigits),
                ".",
                coefficient.AsSpan(integerDigits));
        }

        var magnitude = string.Concat(significand, FormatExponent(engineeringExponent));
        return IsNegative ? string.Concat("-", magnitude) : magnitude;
    }

    internal double ToDouble()
    {
        if (IsNaN)
        {
            return double.NaN;
        }

        if (IsInfinity)
        {
            return IsNegative ? double.NegativeInfinity : double.PositiveInfinity;
        }

        if (Coefficient.IsZero)
        {
            return IsNegative ? -0d : 0d;
        }

        var text = ToScientificString();
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : IsNegative
                ? double.NegativeInfinity
                : double.PositiveInfinity;
    }

    internal bool TryToBigInteger(out BigInteger value)
    {
        value = BigInteger.Zero;
        if (!IsFinite)
        {
            return false;
        }

        if (Exponent >= 0)
        {
            if (Exponent > MaximumMaterializedPower)
            {
                return false;
            }

            value = SignedCoefficient() * PowerOfTen(Exponent);
            return true;
        }

        var shift = -(long)Exponent;
        if (shift > Digits)
        {
            return Coefficient.IsZero;
        }

        var divisor = PowerOfTen((int)shift);
        var quotient = BigInteger.DivRem(Coefficient, divisor, out var remainder);
        if (!remainder.IsZero)
        {
            return false;
        }

        value = IsNegative ? -quotient : quotient;
        return true;
    }

    public int CompareTo(ManagedDecimal other)
    {
        if (IsNaN || other.IsNaN)
        {
            throw new InvalidOperationException("NaN values are unordered.");
        }

        if (IsNegative != other.IsNegative)
        {
            return IsZero && other.IsZero ? 0 : IsNegative ? -1 : 1;
        }

        var magnitude = CompareMagnitude(other);
        return IsNegative ? -magnitude : magnitude;
    }

    public bool Equals(ManagedDecimal other)
    {
        if (IsNaN || other.IsNaN)
        {
            return false;
        }

        return CompareTo(other) == 0;
    }

    public override bool Equals(object? obj) => obj is ManagedDecimal other && Equals(other);

    public override int GetHashCode()
    {
        if (IsNaN)
        {
            return HashCode.Combine(Kind, IsNegative, Coefficient);
        }

        var reduced = ReduceForHashCode();
        return HashCode.Combine(reduced.Kind, reduced.IsNegative && !reduced.IsZero, reduced.Coefficient, reduced.Exponent);
    }

    public override string ToString() => ToScientificString();

    public static bool operator ==(ManagedDecimal left, ManagedDecimal right) => left.Equals(right);

    public static bool operator !=(ManagedDecimal left, ManagedDecimal right) => !left.Equals(right);

    private static ManagedDecimal ApplyContext(
        BigInteger coefficient,
        long exponent,
        bool isNegative,
        decContext context)
    {
        if (!context.IsValid)
        {
            SetStatus(context, libjq.DEC_Invalid_context);
            return NaN();
        }

        coefficient = BigInteger.Abs(coefficient);
        var digits = coefficient.IsZero ? 1 : DecimalDigits(coefficient);
        if (digits > context.digits)
        {
            var discard = digits - context.digits;
            coefficient = RoundCoefficient(coefficient, discard, isNegative, context, out var inexact);
            exponent = SaturatingAdd(exponent, discard);
            SetStatus(context, libjq.DEC_Rounded | (inexact ? libjq.DEC_Inexact : 0));
            digits = coefficient.IsZero ? 1 : DecimalDigits(coefficient);
            if (digits > context.digits)
            {
                // A carry such as 9.99 -> 10.0 has one digit more than the requested precision.
                coefficient /= 10;
                exponent = SaturatingAdd(exponent, 1);
                digits--;
            }
        }

        var adjusted = SaturatingAdd(exponent, digits - 1);
        if (!coefficient.IsZero && adjusted > context.emax)
        {
            SetStatus(context, libjq.DEC_Overflow | libjq.DEC_Rounded | libjq.DEC_Inexact);
            return OverflowResult(isNegative, context);
        }

        var etiny = (long)context.emin - (context.digits - 1L);
        if (exponent < etiny)
        {
            var discard = etiny - exponent;
            coefficient = RoundCoefficient(coefficient, discard, isNegative, context, out var inexact);
            exponent = etiny;
            var status = libjq.DEC_Rounded;
            if (inexact)
            {
                status |= libjq.DEC_Inexact | libjq.DEC_Underflow;
            }

            if (!coefficient.IsZero)
            {
                status |= libjq.DEC_Subnormal;
            }

            SetStatus(context, status);
        }

        digits = coefficient.IsZero ? 1 : DecimalDigits(coefficient);
        adjusted = SaturatingAdd(exponent, digits - 1);
        if (!coefficient.IsZero && adjusted < context.emin)
        {
            SetStatus(context, libjq.DEC_Subnormal);
        }

        if (context.clamp != 0)
        {
            var maximumExponent = (long)context.emax - context.digits + 1;
            if (exponent > maximumExponent)
            {
                var append = exponent - maximumExponent;
                if (append <= context.digits - digits && append <= MaximumMaterializedPower)
                {
                    coefficient *= PowerOfTen((int)append);
                    exponent = maximumExponent;
                    SetStatus(context, libjq.DEC_Clamped);
                }
            }
        }

        if (coefficient.IsZero)
        {
            exponent = Math.Clamp(exponent, etiny, context.emax);
        }

        return new ManagedDecimal(
            coefficient,
            checked((int)Math.Clamp(exponent, int.MinValue, int.MaxValue)),
            isNegative,
            ManagedDecimalKind.Finite);
    }

    private static ManagedDecimal OverflowResult(bool isNegative, decContext context)
    {
        var infinity = context.round switch
        {
            rounding.DEC_ROUND_DOWN => false,
            rounding.DEC_ROUND_CEILING => !isNegative,
            rounding.DEC_ROUND_FLOOR => isNegative,
            rounding.DEC_ROUND_05UP => false,
            _ => true,
        };

        if (infinity || context.digits > MaximumMaterializedPower)
        {
            return Infinity(isNegative);
        }

        var coefficient = PowerOfTen(context.digits) - BigInteger.One;
        var exponent = context.emax - context.digits + 1;
        return new ManagedDecimal(coefficient, exponent, isNegative, ManagedDecimalKind.Finite);
    }

    private static BigInteger RoundCoefficient(
        BigInteger coefficient,
        long discard,
        bool isNegative,
        decContext context,
        out bool inexact)
    {
        if (discard <= 0 || coefficient.IsZero)
        {
            inexact = false;
            return coefficient;
        }

        BigInteger quotient;
        BigInteger remainder;
        BigInteger divisor;
        if (discard > DecimalDigits(coefficient))
        {
            quotient = BigInteger.Zero;
            remainder = coefficient;
            divisor = BigInteger.Zero; // Any such divisor is strictly larger than twice the coefficient.
        }
        else
        {
            divisor = PowerOfTen(checked((int)discard));
            quotient = BigInteger.DivRem(coefficient, divisor, out remainder);
        }

        inexact = !remainder.IsZero;
        if (!inexact)
        {
            return quotient;
        }

        var increment = context.round switch
        {
            rounding.DEC_ROUND_CEILING => !isNegative,
            rounding.DEC_ROUND_UP => true,
            rounding.DEC_ROUND_HALF_UP => divisor != BigInteger.Zero && remainder * 2 >= divisor,
            rounding.DEC_ROUND_HALF_EVEN => divisor != BigInteger.Zero &&
                (remainder * 2 > divisor || (remainder * 2 == divisor && !quotient.IsEven)),
            rounding.DEC_ROUND_HALF_DOWN => divisor != BigInteger.Zero && remainder * 2 > divisor,
            rounding.DEC_ROUND_DOWN => false,
            rounding.DEC_ROUND_FLOOR => isNegative,
            rounding.DEC_ROUND_05UP => quotient % 10 is var digit && (digit.IsZero || BigInteger.Abs(digit) == 5),
            _ => false,
        };
        return increment ? quotient + BigInteger.One : quotient;
    }

    private int CompareMagnitude(ManagedDecimal other)
    {
        if (IsInfinity || other.IsInfinity)
        {
            return IsInfinity == other.IsInfinity ? 0 : IsInfinity ? 1 : -1;
        }

        if (Coefficient.IsZero || other.Coefficient.IsZero)
        {
            return Coefficient.CompareTo(other.Coefficient);
        }

        var adjustedComparison = AdjustedExponent.CompareTo(other.AdjustedExponent);
        if (adjustedComparison != 0)
        {
            return adjustedComparison;
        }

        var left = Coefficient.ToString(CultureInfo.InvariantCulture);
        var right = other.Coefficient.ToString(CultureInfo.InvariantCulture);
        var length = Math.Max(left.Length, right.Length);
        return left.PadRight(length, '0').AsSpan().SequenceCompareTo(right.PadRight(length, '0'));
    }

    private ManagedDecimal ReduceForHashCode()
    {
        if (!IsFinite || Coefficient.IsZero)
        {
            return IsZero ? Zero() : this;
        }

        var coefficient = Coefficient;
        var exponent = Exponent;
        while (coefficient % 10 == 0)
        {
            coefficient /= 10;
            exponent++;
        }

        return new ManagedDecimal(coefficient, exponent, IsNegative, Kind);
    }

    private ManagedDecimal Quieted() =>
        Kind == ManagedDecimalKind.SignalingNaN
            ? new ManagedDecimal(Coefficient, Exponent, IsNegative, ManagedDecimalKind.QuietNaN)
            : this;

    private static ManagedDecimal PropagateNaN(
        ManagedDecimal left,
        ManagedDecimal right,
        decContext context)
    {
        if (left.Kind == ManagedDecimalKind.SignalingNaN || right.Kind == ManagedDecimalKind.SignalingNaN)
        {
            SetStatus(context, libjq.DEC_Invalid_operation);
        }

        return left.IsNaN ? left.Quieted() : right.Quieted();
    }

    private BigInteger SignedCoefficient() => IsNegative ? -Coefficient : Coefficient;

    private string SpecialToString()
    {
        var sign = IsNegative ? "-" : string.Empty;
        var kind = Kind switch
        {
            ManagedDecimalKind.Infinity => "Infinity",
            ManagedDecimalKind.QuietNaN => "NaN",
            ManagedDecimalKind.SignalingNaN => "sNaN",
            _ => throw new InvalidOperationException("A finite number is not special."),
        };
        var payload = IsNaN && !Coefficient.IsZero
            ? Coefficient.ToString(CultureInfo.InvariantCulture)
            : string.Empty;
        return string.Concat(sign, kind, payload);
    }

    private static string FormatExponent(long exponent) =>
        string.Concat(
            "E",
            exponent >= 0 ? "+" : string.Empty,
            exponent.ToString(CultureInfo.InvariantCulture));

    private static int DecimalDigits(BigInteger value) =>
        value.ToString(CultureInfo.InvariantCulture).Length;

    private static BigInteger PowerOfTen(int exponent) => BigInteger.Pow(10, exponent);

    private static bool AllAsciiDigits(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (!IsAsciiDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAsciiDigit(char value) => value is >= '0' and <= '9';

    private static bool TryParseExponent(ReadOnlySpan<char> text, out long value)
    {
        value = 0;
        if (text.IsEmpty)
        {
            return false;
        }

        var negative = false;
        var index = 0;
        if (text[0] is '+' or '-')
        {
            negative = text[0] == '-';
            index++;
            if (index == text.Length)
            {
                return false;
            }
        }

        for (; index < text.Length; index++)
        {
            var character = text[index];
            if (!IsAsciiDigit(character))
            {
                return false;
            }

            var digit = character - '0';
            value = value > (long.MaxValue - digit) / 10
                ? long.MaxValue
                : value * 10 + digit;
        }

        value = negative ? -value : value;
        return true;
    }

    private static long SaturatingAdd(long left, long right)
    {
        if (right > 0 && left > long.MaxValue - right)
        {
            return long.MaxValue;
        }

        if (right < 0 && left < long.MinValue - right)
        {
            return long.MinValue;
        }

        return left + right;
    }

    private static long SaturatingSubtract(long left, long right) =>
        right == long.MinValue ? long.MaxValue : SaturatingAdd(left, -right);

    private static long Modulo(long value, int divisor)
    {
        var result = value % divisor;
        return result < 0 ? result + divisor : result;
    }

    private static void SetConversionSyntax(decContext context) =>
        SetStatus(context, libjq.DEC_Conversion_syntax);

    private static void SetStatus(decContext context, uint status) =>
        libjq.decContextSetStatus(context, status);
}
