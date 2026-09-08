// DOTNETJQ PORT MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream file: vendor/decNumber/decNumber.c
// Upstream URL: https://github.com/jqlang/jq/blob/jq-1.8.2/vendor/decNumber/decNumber.c
// Strategy: PROXY
// Target file: src/DotNetJq/Port/vendor/decNumber/decNumber.c.cs
// Upstream copyright notice: Copyright (c) IBM Corporation, 2000, 2009. All rights reserved.
// Upstream license: ICU License -- ICU 1.8.1 and later; complete terms are in /COPYING.jq.
// UPSTREAM COMPONENT: IBM decNumber parsing, formatting, classification, comparison, and arithmetic APIs used by jq.
// REPLACEMENT: ManagedDecimal parsing, formatting, comparison, and arithmetic.
// WHY: managed arbitrary-precision coefficient/exponent operations replace native unit buffers.
// BEHAVIORAL CONTRACT: honor decContext precision, rounding, status, special values, and jq-facing text forms.
// KNOWN DIFFERENCES: APIs not required by jq's literal-number path are intentionally not exposed yet.
// TESTS COVERING THE SUBSTITUTION: NumericCompatibilityTests, NumericCompatibilityRound2Tests, and jq numeric fixtures.

using System.Globalization;
using System.Numerics;

namespace DotNetJq.Port;

internal static partial class libjq
{
    internal static decNumber decNumberFromInt32(decNumber result, int value) =>
        result.Set(ManagedDecimal.FromInt32(value));

    internal static decNumber decNumberFromUInt32(decNumber result, uint value) =>
        result.Set(ManagedDecimal.FromUInt32(value));

    internal static decNumber decNumberFromString(decNumber result, string text, decContext context)
    {
        ArgumentNullException.ThrowIfNull(result);
        return ManagedDecimal.TryParse(text, context, out var value)
            ? result.Set(value)
            : result.Set(ManagedDecimal.NaN());
    }

    internal static string decNumberToString(decNumber number) => number.Value.ToScientificString();

    internal static string decNumberToEngString(decNumber number) => number.Value.ToEngineeringString();

    internal static uint decNumberToUInt32(decNumber number, decContext context)
    {
        if (!number.Value.TryToBigInteger(out var value) || value < uint.MinValue || value > uint.MaxValue)
        {
            decContextSetStatus(context, DEC_Invalid_operation);
            return 0;
        }

        return (uint)value;
    }

    internal static int decNumberToInt32(decNumber number, decContext context)
    {
        if (!number.Value.TryToBigInteger(out var value) || value < int.MinValue || value > int.MaxValue)
        {
            decContextSetStatus(context, DEC_Invalid_operation);
            return 0;
        }

        return (int)value;
    }

    internal static byte[] decNumberGetBCD(decNumber number)
    {
        var text = number.Value.Coefficient.ToString(CultureInfo.InvariantCulture);
        var result = new byte[text.Length];
        for (var index = 0; index < text.Length; index++)
        {
            result[index] = (byte)(text[index] - '0');
        }

        return result;
    }

    internal static decNumber decNumberSetBCD(decNumber result, ReadOnlySpan<byte> bcd, uint digitCount)
    {
        if (digitCount == 0 || digitCount > bcd.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(digitCount));
        }

        var coefficient = BigInteger.Zero;
        foreach (var digit in bcd[..checked((int)digitCount)])
        {
            if (digit > 9)
            {
                throw new ArgumentOutOfRangeException(nameof(bcd));
            }

            coefficient = coefficient * 10 + digit;
        }

        return result.Set(result.Value.WithCoefficient(coefficient));
    }

    internal static decNumber decNumberAbs(decNumber result, decNumber number, decContext context) =>
        result.Set(number.Value.Abs(context));

    internal static decNumber decNumberAdd(
        decNumber result,
        decNumber left,
        decNumber right,
        decContext context) =>
        result.Set(left.Value.Add(right.Value, context));

    internal static decNumber decNumberCompare(
        decNumber result,
        decNumber left,
        decNumber right,
        decContext context)
    {
        if (left.Value.IsNaN || right.Value.IsNaN)
        {
            return result.Set(ManagedDecimal.NaN());
        }

        _ = context;
        return result.Set(ManagedDecimal.FromInt32(left.Value.CompareTo(right.Value)));
    }

    internal static decNumber decNumberCompareSignal(
        decNumber result,
        decNumber left,
        decNumber right,
        decContext context)
    {
        if (left.Value.IsNaN || right.Value.IsNaN)
        {
            decContextSetStatus(context, DEC_Invalid_operation);
            return result.Set(ManagedDecimal.NaN());
        }

        return decNumberCompare(result, left, right, context);
    }

    internal static decNumber decNumberDivide(
        decNumber result,
        decNumber left,
        decNumber right,
        decContext context) =>
        result.Set(left.Value.Divide(right.Value, context));

    internal static decNumber decNumberMinus(decNumber result, decNumber number, decContext context) =>
        result.Set(number.Value.Negate(context));

    internal static decNumber decNumberMultiply(
        decNumber result,
        decNumber left,
        decNumber right,
        decContext context) =>
        result.Set(left.Value.Multiply(right.Value, context));

    internal static decNumber decNumberNormalize(decNumber result, decNumber number, decContext context) =>
        decNumberReduce(result, number, context);

    internal static decNumber decNumberPlus(decNumber result, decNumber number, decContext context) =>
        result.Set(number.Value.Add(ManagedDecimal.Zero(), context));

    internal static decNumber decNumberReduce(decNumber result, decNumber number, decContext context) =>
        result.Set(number.Value.Reduce(context));

    internal static decNumber decNumberSubtract(
        decNumber result,
        decNumber left,
        decNumber right,
        decContext context) =>
        result.Set(left.Value.Subtract(right.Value, context));

    internal static decClass decNumberClass(decNumber number, decContext context)
    {
        var value = number.Value;
        if (value.Kind == ManagedDecimalKind.SignalingNaN)
        {
            return decClass.DEC_CLASS_SNAN;
        }

        if (value.Kind == ManagedDecimalKind.QuietNaN)
        {
            return decClass.DEC_CLASS_QNAN;
        }

        if (value.IsInfinity)
        {
            return value.IsNegative ? decClass.DEC_CLASS_NEG_INF : decClass.DEC_CLASS_POS_INF;
        }

        if (value.IsZero)
        {
            return value.IsNegative ? decClass.DEC_CLASS_NEG_ZERO : decClass.DEC_CLASS_POS_ZERO;
        }

        var subnormal = value.AdjustedExponent < context.emin;
        return (value.IsNegative, subnormal) switch
        {
            (true, true) => decClass.DEC_CLASS_NEG_SUBNORMAL,
            (true, false) => decClass.DEC_CLASS_NEG_NORMAL,
            (false, true) => decClass.DEC_CLASS_POS_SUBNORMAL,
            _ => decClass.DEC_CLASS_POS_NORMAL,
        };
    }

    internal static string decNumberClassToString(decClass numberClass) => numberClass switch
    {
        decClass.DEC_CLASS_SNAN => "sNaN",
        decClass.DEC_CLASS_QNAN => "NaN",
        decClass.DEC_CLASS_NEG_INF => "-Infinity",
        decClass.DEC_CLASS_NEG_NORMAL => "-Normal",
        decClass.DEC_CLASS_NEG_SUBNORMAL => "-Subnormal",
        decClass.DEC_CLASS_NEG_ZERO => "-Zero",
        decClass.DEC_CLASS_POS_ZERO => "+Zero",
        decClass.DEC_CLASS_POS_SUBNORMAL => "+Subnormal",
        decClass.DEC_CLASS_POS_NORMAL => "+Normal",
        decClass.DEC_CLASS_POS_INF => "+Infinity",
        _ => "Invalid",
    };

    internal static decNumber decNumberCopy(decNumber result, decNumber number) =>
        result.Set(number.Value);

    internal static decNumber decNumberCopyAbs(decNumber result, decNumber number) =>
        result.Set(number.Value.CopyAbs());

    internal static decNumber decNumberCopyNegate(decNumber result, decNumber number) =>
        result.Set(number.Value.CopyNegate());

    internal static decNumber decNumberCopySign(decNumber result, decNumber number, decNumber sign) =>
        result.Set(number.Value.CopySign(sign.Value));

    internal static decNumber decNumberTrim(decNumber number)
    {
        var context = CreateJqDecimalContext();
        return number.Set(number.Value.Reduce(context));
    }

    internal static string decNumberVersion() => DECVERSION;

    internal static decNumber decNumberZero(decNumber number) => number.Set(ManagedDecimal.Zero());

    internal static int decNumberIsNormal(decNumber number, decContext context) =>
        number.Value.IsFinite && !number.Value.IsZero && number.Value.AdjustedExponent >= context.emin ? 1 : 0;

    internal static int decNumberIsSubnormal(decNumber number, decContext context) =>
        number.Value.IsFinite && !number.Value.IsZero && number.Value.AdjustedExponent < context.emin ? 1 : 0;
}
