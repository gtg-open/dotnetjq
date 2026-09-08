// DOTNETJQ PORT MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream file: vendor/decNumber/decContext.h
// Upstream URL: https://github.com/jqlang/jq/blob/jq-1.8.2/vendor/decNumber/decContext.h
// Strategy: PROXY
// Target file: src/DotNetJq/Port/vendor/decNumber/decContext.h.cs
// Upstream copyright notice: Copyright (c) IBM Corporation, 2000, 2010. All rights reserved.
// Upstream license: ICU License -- ICU 1.8.1 and later; complete terms are in /COPYING.jq.
// UPSTREAM COMPONENT: IBM decNumber context types, rounding modes, classes, and status constants.
// REPLACEMENT: managed context state, enums, and status flags.
// WHY: CLR types provide safe storage for the subset of decContext consumed by jq.
// BEHAVIORAL CONTRACT: preserve upstream numeric values, defaults, and bit masks exposed to mapped callers.
// KNOWN DIFFERENCES: enabled traps raise ArithmeticException instead of SIGFPE.
// TESTS COVERING THE SUBSTITUTION: NumericCompatibilityTests, NumericCompatibilityRound2Tests, and build coverage.

namespace DotNetJq.Port;

internal enum rounding
{
    DEC_ROUND_CEILING,
    DEC_ROUND_UP,
    DEC_ROUND_HALF_UP,
    DEC_ROUND_HALF_EVEN,
    DEC_ROUND_HALF_DOWN,
    DEC_ROUND_DOWN,
    DEC_ROUND_FLOOR,
    DEC_ROUND_05UP,
    DEC_ROUND_MAX,
}

internal enum decClass
{
    DEC_CLASS_SNAN,
    DEC_CLASS_QNAN,
    DEC_CLASS_NEG_INF,
    DEC_CLASS_NEG_NORMAL,
    DEC_CLASS_NEG_SUBNORMAL,
    DEC_CLASS_NEG_ZERO,
    DEC_CLASS_POS_ZERO,
    DEC_CLASS_POS_SUBNORMAL,
    DEC_CLASS_POS_NORMAL,
    DEC_CLASS_POS_INF,
}

internal sealed class decContext
{
    internal int digits;
    internal int emax;
    internal int emin;
    internal rounding round;
    internal uint traps;
    internal uint status;
    internal byte clamp;

    internal bool IsValid =>
        digits is >= libjq.DEC_MIN_DIGITS and <= libjq.DEC_MAX_DIGITS &&
        emax is >= libjq.DEC_MIN_EMAX and <= libjq.DEC_MAX_EMAX &&
        emin is >= libjq.DEC_MIN_EMIN and <= libjq.DEC_MAX_EMIN &&
        round is >= rounding.DEC_ROUND_CEILING and < rounding.DEC_ROUND_MAX &&
        clamp <= 1;
}

internal static partial class libjq
{
    internal const int DEC_MAX_DIGITS = 999_999_999;
    internal const int DEC_MIN_DIGITS = 1;
    internal const int DEC_MAX_EMAX = 999_999_999;
    internal const int DEC_MIN_EMAX = 0;
    internal const int DEC_MAX_EMIN = 0;
    internal const int DEC_MIN_EMIN = -999_999_999;
    internal const int DEC_MAX_MATH = 999_999;

    internal const uint DEC_Conversion_syntax = 0x0000_0001;
    internal const uint DEC_Division_by_zero = 0x0000_0002;
    internal const uint DEC_Division_impossible = 0x0000_0004;
    internal const uint DEC_Division_undefined = 0x0000_0008;
    internal const uint DEC_Insufficient_storage = 0x0000_0010;
    internal const uint DEC_Inexact = 0x0000_0020;
    internal const uint DEC_Invalid_context = 0x0000_0040;
    internal const uint DEC_Invalid_operation = 0x0000_0080;
    internal const uint DEC_Overflow = 0x0000_0200;
    internal const uint DEC_Clamped = 0x0000_0400;
    internal const uint DEC_Rounded = 0x0000_0800;
    internal const uint DEC_Subnormal = 0x0000_1000;
    internal const uint DEC_Underflow = 0x0000_2000;

    internal const uint DEC_IEEE_754_Division_by_zero = DEC_Division_by_zero;
    internal const uint DEC_IEEE_754_Inexact = DEC_Inexact;
    internal const uint DEC_IEEE_754_Invalid_operation =
        DEC_Conversion_syntax |
        DEC_Division_impossible |
        DEC_Division_undefined |
        DEC_Insufficient_storage |
        DEC_Invalid_context |
        DEC_Invalid_operation;
    internal const uint DEC_IEEE_754_Overflow = DEC_Overflow;
    internal const uint DEC_IEEE_754_Underflow = DEC_Underflow;
    internal const uint DEC_Errors =
        DEC_IEEE_754_Division_by_zero |
        DEC_IEEE_754_Invalid_operation |
        DEC_IEEE_754_Overflow |
        DEC_IEEE_754_Underflow;
    internal const uint DEC_NaNs = DEC_IEEE_754_Invalid_operation;
    internal const uint DEC_Information = DEC_Clamped | DEC_Rounded | DEC_Inexact;

    internal const int DEC_INIT_BASE = 0;
    internal const int DEC_INIT_DECIMAL32 = 32;
    internal const int DEC_INIT_DECIMAL64 = 64;
    internal const int DEC_INIT_DECIMAL128 = 128;
    internal const int DEC_INIT_DECSINGLE = DEC_INIT_DECIMAL32;
    internal const int DEC_INIT_DECDOUBLE = DEC_INIT_DECIMAL64;
    internal const int DEC_INIT_DECQUAD = DEC_INIT_DECIMAL128;
}
