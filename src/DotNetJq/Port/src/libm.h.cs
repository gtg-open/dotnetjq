// DOTNETJQ PORT MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream file: src/libm.h
// Upstream URL: https://github.com/jqlang/jq/blob/jq-1.8.2/src/libm.h
// Strategy: PROXY
// Target file: src/DotNetJq/Port/src/libm.h.cs
// UPSTREAM COMPONENT: jq's generated libm builtin declaration and availability surface.
// REPLACEMENT: System.Math, managed Sun fdlibm kernels, and a replaceable gamma compatibility ABI.
// WHY: managed implementations preserve jq's math surface without requiring a native libm ABI.
// BEHAVIORAL CONTRACT: expose jq's supported builtin names and preserve jq numeric/error behavior.
// KNOWN DIFFERENCES: None known for the covered jq-visible binary64 corpus.
// TESTS COVERING THE SUBSTITUTION: LibmCompatibilityTests, FdlibmElementaryCompatibilityTests,
// FdlibmElementaryExactOracleCorpusTests, SpecialMathExactOracleCorpusTests,
// BuiltinCompatibilityRound3Tests, arithmetic compatibility tests, and upstream libm fixtures.
//
// The erf, acosh, asinh, atanh, expm1, log1p, and Bessel approximations below are managed
// translations of fdlibm. Copyright (C) 1993 Sun Microsystems, Inc. All rights reserved.
// Developed at SunSoft. Permission to use, copy, modify, and distribute this
// software is freely granted, provided that this notice is preserved.

namespace DotNetJq.Port;

internal static partial class libjq
{
    private const double InverseSqrtPi = 5.64189583547756279280e-01;
    private const double MachineEpsilon = 2.22044604925031308085e-16;
    private const double TwoOverPi = 6.36619772367581382433e-01;

    internal static readonly IReadOnlySet<string> libm_builtins =
        new HashSet<string>(
            [
                "acos", "acosh", "asin", "asinh", "atan", "atan2", "atanh",
                "cbrt", "ceil", "copysign", "cos", "cosh", "drem", "erf", "erfc",
                "exp", "exp2", "exp10", "expm1", "fabs", "fdim", "floor", "fma",
                "fmax", "fmin", "fmod", "frexp", "gamma", "hypot", "j0", "j1",
                "jn", "ldexp", "lgamma", "lgamma_r", "log", "log10", "log1p",
                "log2", "logb", "modf", "nearbyint", "nextafter", "nexttoward",
                "pow", "remainder", "rint", "round", "scalb", "scalbln", "significand",
                "sin", "sinh", "sqrt", "tan", "tanh", "tgamma", "trunc", "y0", "y1", "yn",
            ],
            StringComparer.Ordinal);

    internal static double jq_erf(double value)
    {
        if (double.IsNaN(value))
        {
            return double.NaN;
        }

        if (double.IsPositiveInfinity(value))
        {
            return 1;
        }

        if (double.IsNegativeInfinity(value))
        {
            return -1;
        }

        var absolute = Math.Abs(value);
        if (absolute < 0.84375)
        {
            if (absolute < Math.ScaleB(1, -28))
            {
                return absolute < Math.ScaleB(1, -1022)
                    ? 0.125 * ((8 * value) + (1.02703333676410069053 * value))
                    : value + (1.28379167095512586316e-01 * value);
            }

            return value + (value * ErfSmallRatio(value));
        }

        if (absolute < 1.25)
        {
            var result = 8.45062911510467529297e-01 + ErfMiddleRatio(absolute);
            return Math.CopySign(result, value);
        }

        if (absolute >= 6)
        {
            return Math.CopySign(1, value);
        }

        var tail = ErfTail(absolute, complement: false);
        var magnitude = 1 - tail;
        return Math.CopySign(magnitude, value);
    }

    internal static double jq_erfc(double value)
    {
        if (double.IsNaN(value))
        {
            return double.NaN;
        }

        if (double.IsPositiveInfinity(value))
        {
            return 0;
        }

        if (double.IsNegativeInfinity(value))
        {
            return 2;
        }

        var absolute = Math.Abs(value);
        if (absolute < 0.84375)
        {
            if (absolute < Math.ScaleB(1, -56))
            {
                return 1 - value;
            }

            var correction = value * ErfSmallRatio(value);
            return value < 0.25
                ? 1 - (value + correction)
                : 0.5 - (correction + (value - 0.5));
        }

        if (absolute < 1.25)
        {
            var correction = ErfMiddleRatio(absolute);
            return value >= 0
                ? (1 - 8.45062911510467529297e-01) - correction
                : 1 + (8.45062911510467529297e-01 + correction);
        }

        if (absolute >= 28)
        {
            return value > 0 ? 0 : 2;
        }

        if (value < -6)
        {
            return 2;
        }

        var tail = ErfTail(absolute, complement: true);
        return value > 0 ? tail : 2 - tail;
    }

    internal static (double Fraction, int Exponent) jq_frexp(double value)
    {
        var bits = BitConverter.DoubleToUInt64Bits(value);
        var exponentBits = (int)((bits >> 52) & 0x7ff);
        if (exponentBits == 0x7ff || (bits & 0x7fffffffffffffffUL) == 0)
        {
            return (value, 0);
        }

        var exponentAdjustment = 0;
        if (exponentBits == 0)
        {
            value *= Math.ScaleB(1, 54);
            bits = BitConverter.DoubleToUInt64Bits(value);
            exponentBits = (int)((bits >> 52) & 0x7ff);
            exponentAdjustment = -54;
        }

        var exponent = exponentBits - 1022 + exponentAdjustment;
        bits = (bits & 0x800fffffffffffffUL) | (1022UL << 52);
        return (BitConverter.UInt64BitsToDouble(bits), exponent);
    }

    internal static (double Fractional, double Integral) jq_modf(double value)
    {
        if (double.IsNaN(value))
        {
            return (double.NaN, double.NaN);
        }

        if (double.IsInfinity(value))
        {
            return (Math.CopySign(0, value), value);
        }

        var integral = Math.Truncate(value);
        var fractional = value - integral;
        if (fractional == 0)
        {
            fractional = Math.CopySign(0, value);
        }

        return (fractional, integral);
    }

    internal static double jq_significand(double value)
    {
        var (fraction, _) = jq_frexp(value);
        return fraction * 2;
    }

    // Managed translations of the permissively licensed Sun fdlibm
    // e_acosh.c, s_asinh.c, e_atanh.c, s_expm1.c, and s_log1p.c kernels.
    // Keep their binary64 word thresholds and evaluation order: the jq
    // oracle exposes the last-bit differences from the System.Math helpers.
    internal static double jq_acosh(double value)
    {
        const double logTwo = 6.93147180559945286227e-01;

        if (double.IsNaN(value))
        {
            return value + value;
        }

        if (value < 1)
        {
            return double.NaN;
        }

        if (value >= 268435456)
        {
            return double.IsPositiveInfinity(value) ? value + value : Math.Log(value) + logTwo;
        }

        if (value == 1)
        {
            return 0;
        }

        if (value > 2)
        {
            var square = value * value;
            return Math.Log((2 * value) - (1 / (value + Math.Sqrt(square - 1))));
        }

        var offset = value - 1;
        return FdlibmLog1p(offset + Math.Sqrt((2 * offset) + (offset * offset)));
    }

    internal static double jq_asinh(double value)
    {
        const double logTwo = 6.93147180559945286227e-01;

        var high = SignedHighWord(value);
        var absoluteHigh = (uint)high & 0x7fffffffU;
        if (absoluteHigh >= 0x7ff00000U)
        {
            return value + value;
        }

        if (absoluteHigh < 0x3e300000U)
        {
            return value;
        }

        double result;
        if (absoluteHigh > 0x41b00000U)
        {
            result = Math.Log(Math.Abs(value)) + logTwo;
        }
        else if (absoluteHigh > 0x40000000U)
        {
            var absolute = Math.Abs(value);
            result = Math.Log((2 * absolute) + (1 / (Math.Sqrt((value * value) + 1) + absolute)));
        }
        else
        {
            var square = value * value;
            result = FdlibmLog1p(Math.Abs(value) + (square / (1 + Math.Sqrt(1 + square))));
        }

        return high > 0 ? result : -result;
    }

    internal static double jq_atanh(double value)
    {
        var high = SignedHighWord(value);
        var absoluteHigh = (uint)high & 0x7fffffffU;
        var low = LowWord(value);
        if (absoluteHigh > 0x3ff00000U ||
            (absoluteHigh == 0x3ff00000U && low != 0))
        {
            return double.NaN;
        }

        if (absoluteHigh == 0x3ff00000U)
        {
            return Math.CopySign(double.PositiveInfinity, value);
        }

        if (absoluteHigh < 0x3e300000U)
        {
            return value;
        }

        var absolute = WithHighWord(value, absoluteHigh);
        double result;
        if (absoluteHigh < 0x3fe00000U)
        {
            var twice = absolute + absolute;
            result = 0.5 * FdlibmLog1p(twice + ((twice * absolute) / (1 - absolute)));
        }
        else
        {
            result = 0.5 * FdlibmLog1p((absolute + absolute) / (1 - absolute));
        }

        return high >= 0 ? result : -result;
    }

    internal static double jq_expm1(double value)
    {
        const double overflowThreshold = 7.09782712893383973096e+02;
        const double logTwoHigh = 6.93147180369123816490e-01;
        const double logTwoLow = 1.90821492927058770002e-10;
        const double inverseLogTwo = 1.44269504088896338700e+00;
        const double coefficient1 = -3.33333333333331316428e-02;
        const double coefficient2 = 1.58730158725481460165e-03;
        const double coefficient3 = -7.93650757867487942473e-05;
        const double coefficient4 = 4.00821782732936239552e-06;
        const double coefficient5 = -2.01099218183624371326e-07;

        var signedHigh = SignedHighWord(value);
        var negative = signedHigh < 0;
        var high = (uint)signedHigh & 0x7fffffffU;
        if (high >= 0x4043687aU)
        {
            if (high >= 0x40862e42U)
            {
                if (high >= 0x7ff00000U)
                {
                    return double.IsNaN(value)
                        ? value + value
                        : negative ? -1 : value;
                }

                if (value > overflowThreshold)
                {
                    return double.PositiveInfinity;
                }
            }

            if (negative)
            {
                return 1e-300 - 1;
            }
        }

        int exponent;
        double correction;
        if (high > 0x3fd62e42U)
        {
            double highPart;
            double lowPart;
            if (high < 0x3ff0a2b2U)
            {
                if (!negative)
                {
                    highPart = value - logTwoHigh;
                    lowPart = logTwoLow;
                    exponent = 1;
                }
                else
                {
                    highPart = value + logTwoHigh;
                    lowPart = -logTwoLow;
                    exponent = -1;
                }
            }
            else
            {
                exponent = (int)((inverseLogTwo * value) + (negative ? -0.5 : 0.5));
                var exponentAsDouble = (double)exponent;
                highPart = value - (exponentAsDouble * logTwoHigh);
                lowPart = exponentAsDouble * logTwoLow;
            }

            value = highPart - lowPart;
            correction = (highPart - value) - lowPart;
        }
        else if (high < 0x3c900000U)
        {
            return value;
        }
        else
        {
            exponent = 0;
            correction = 0;
        }

        var half = 0.5 * value;
        var halfSquare = value * half;
        var ratioPolynomial = coefficient4 + (halfSquare * coefficient5);
        ratioPolynomial = coefficient3 + (halfSquare * ratioPolynomial);
        ratioPolynomial = coefficient2 + (halfSquare * ratioPolynomial);
        ratioPolynomial = coefficient1 + (halfSquare * ratioPolynomial);
        var ratio = 1 + (halfSquare * ratioPolynomial);
        var intermediate = 3 - (ratio * half);
        var error = halfSquare * ((ratio - intermediate) / (6 - (value * intermediate)));

        if (exponent == 0)
        {
            return value - ((value * error) - halfSquare);
        }

        error = (value * (error - correction)) - correction;
        error -= halfSquare;
        if (exponent == -1)
        {
            return (0.5 * (value - error)) - 0.5;
        }

        if (exponent == 1)
        {
            return value < -0.25
                ? -2 * (error - (value + 0.5))
                : 1 + (2 * (value - error));
        }

        double result;
        if (exponent <= -2 || exponent > 56)
        {
            result = 1 - (error - value);
            result = AddHighWordExponent(result, exponent);
            return result - 1;
        }

        if (exponent < 20)
        {
            var scale = WithHighWord(1, 0x3ff00000U - (0x200000U >> exponent));
            result = scale - (error - value);
        }
        else
        {
            var scale = WithHighWord(1, (uint)(0x3ff - exponent) << 20);
            result = value - (error + scale);
            result += 1;
        }

        return AddHighWordExponent(result, exponent);
    }

    internal static double jq_log1p(double value) => FdlibmLog1p(value);

    private static double FdlibmLog1p(double value)
    {
        const double logTwoHigh = 6.93147180369123816490e-01;
        const double logTwoLow = 1.90821492927058770002e-10;
        const double coefficient1 = 6.666666666666735130e-01;
        const double coefficient2 = 3.999999999940941908e-01;
        const double coefficient3 = 2.857142874366239149e-01;
        const double coefficient4 = 2.222219843214978396e-01;
        const double coefficient5 = 1.818357216161805012e-01;
        const double coefficient6 = 1.531383769920937332e-01;
        const double coefficient7 = 1.479819860511658591e-01;

        var signedHigh = SignedHighWord(value);
        var absoluteHigh = (uint)signedHigh & 0x7fffffffU;
        var exponent = 1;
        var normalizedHigh = 0;
        double reduced;
        double correction = 0;

        if (signedHigh < 0x3fda827a)
        {
            if (absoluteHigh >= 0x3ff00000U)
            {
                if (value == -1)
                {
                    return double.NegativeInfinity;
                }

                return double.NaN;
            }

            if (absoluteHigh < 0x3e200000U)
            {
                if (absoluteHigh < 0x3c900000U)
                {
                    return value;
                }

                return value - ((value * value) * 0.5);
            }

            if (signedHigh > 0 || signedHigh <= unchecked((int)0xbfd2bec3U))
            {
                exponent = 0;
                reduced = value;
                normalizedHigh = 1;
            }
            else
            {
                reduced = 0;
            }
        }
        else if (absoluteHigh >= 0x7ff00000U)
        {
            return value + value;
        }
        else
        {
            reduced = 0;
        }

        if (exponent != 0)
        {
            double normalized;
            if (absoluteHigh < 0x43400000U)
            {
                normalized = 1 + value;
                normalizedHigh = SignedHighWord(normalized);
                exponent = (normalizedHigh >> 20) - 1023;
                correction = exponent > 0
                    ? 1 - (normalized - value)
                    : value - (normalized - 1);
                correction /= normalized;
            }
            else
            {
                normalized = value;
                normalizedHigh = SignedHighWord(normalized);
                exponent = (normalizedHigh >> 20) - 1023;
            }

            normalizedHigh &= 0x000fffff;
            if (normalizedHigh < 0x0006a09e)
            {
                normalized = WithHighWord(normalized, (uint)normalizedHigh | 0x3ff00000U);
            }
            else
            {
                exponent += 1;
                normalized = WithHighWord(normalized, (uint)normalizedHigh | 0x3fe00000U);
                normalizedHigh = (0x00100000 - normalizedHigh) >> 2;
            }

            reduced = normalized - 1;
        }

        var halfSquare = 0.5 * reduced * reduced;
        if (normalizedHigh == 0)
        {
            if (reduced == 0)
            {
                if (exponent == 0)
                {
                    return 0;
                }

                correction += exponent * logTwoLow;
                return (exponent * logTwoHigh) + correction;
            }

            var ratio = halfSquare * (1 - (0.66666666666666666 * reduced));
            if (exponent == 0)
            {
                return reduced - ratio;
            }

            return (exponent * logTwoHigh) -
                ((ratio - ((exponent * logTwoLow) + correction)) - reduced);
        }

        var fraction = reduced / (2 + reduced);
        var square = fraction * fraction;
        var polynomial = coefficient6 + (square * coefficient7);
        polynomial = coefficient5 + (square * polynomial);
        polynomial = coefficient4 + (square * polynomial);
        polynomial = coefficient3 + (square * polynomial);
        polynomial = coefficient2 + (square * polynomial);
        polynomial = coefficient1 + (square * polynomial);
        polynomial *= square;
        if (exponent == 0)
        {
            return reduced - (halfSquare - (fraction * (halfSquare + polynomial)));
        }

        return (exponent * logTwoHigh) -
            ((halfSquare - ((fraction * (halfSquare + polynomial)) +
                ((exponent * logTwoLow) + correction))) - reduced);
    }

    // jq's Linux gamma family is isolated in the replaceable compatibility
    // component; this assembly contains only calls across its stable ABI.
    internal static double jq_gamma(double value) => GammaCompatBridge.Gamma(value);

    internal static double jq_lgamma(double value) => GammaCompatBridge.Lgamma(value);

    internal static (double Value, int Sign) jq_lgamma_r(double value) =>
        GammaCompatBridge.LgammaR(value);

    internal static double jq_tgamma(double value) => GammaCompatBridge.Tgamma(value);

    // Bessel identities, thresholds, and coefficient tables are translated
    // from the permissively licensed Netlib fdlibm e_j0.c, e_j1.c, and e_jn.c.
    // Managed evaluation grouping is documented separately at its helper.
    internal static double jq_j0(double value)
    {
        if (double.IsNaN(value))
        {
            return double.NaN;
        }

        if (double.IsInfinity(value))
        {
            return 0;
        }

        var absolute = Math.Abs(value);
        var highWord = AbsoluteHighWord(absolute);
        if (highWord >= 0x40000000U)
        {
            var sine = Math.Sin(absolute);
            var cosine = Math.Cos(absolute);
            var sinePhase = sine - cosine;
            var cosinePhase = sine + cosine;
            if (highWord < 0x7fe00000U)
            {
                var doubleAngleCosine = -Math.Cos(absolute + absolute);
                if ((sine * cosine) < 0)
                {
                    cosinePhase = doubleAngleCosine / sinePhase;
                }
                else
                {
                    sinePhase = doubleAngleCosine / cosinePhase;
                }
            }

            if (highWord > 0x48000000U)
            {
                return InverseSqrtPi * cosinePhase / Math.Sqrt(absolute);
            }

            var p = BesselPZero(absolute);
            var q = BesselQZero(absolute);
            return InverseSqrtPi * ((p * cosinePhase) - (q * sinePhase)) /
                Math.Sqrt(absolute);
        }

        if (highWord < 0x3f200000U)
        {
            return highWord < 0x3e400000U ? 1 : 1 - (0.25 * absolute * absolute);
        }

        var square = absolute * absolute;
        var numerator = EvaluateBalancedPolynomial(
            square,
            [
                0,
                1.56249999999999947958e-02,
                -1.89979294238854721751e-04,
                1.82954049532700665670e-06,
                -4.61832688532103189199e-09,
            ]);
        var denominator = EvaluateBalancedPolynomial(
            square,
            [
                1,
                1.56191029464890010492e-02,
                1.16926784663337450260e-04,
                5.13546550207318111446e-07,
                1.16614003333790000205e-09,
            ]);
        if (highWord < 0x3ff00000U)
        {
            return 1 + (square * (-0.25 + (numerator / denominator)));
        }

        var half = 0.5 * absolute;
        return ((1 + half) * (1 - half)) + (square * (numerator / denominator));
    }

    internal static double jq_j1(double value)
    {
        if (double.IsNaN(value))
        {
            return double.NaN;
        }

        if (double.IsInfinity(value))
        {
            return Math.CopySign(0, value);
        }

        var absolute = Math.Abs(value);
        var highWord = AbsoluteHighWord(absolute);
        double result;
        if (highWord >= 0x40000000U)
        {
            var sine = Math.Sin(absolute);
            var cosine = Math.Cos(absolute);
            var sinePhase = -sine - cosine;
            var cosinePhase = sine - cosine;
            if (highWord < 0x7fe00000U)
            {
                var doubleAngleCosine = Math.Cos(absolute + absolute);
                if ((sine * cosine) > 0)
                {
                    cosinePhase = doubleAngleCosine / sinePhase;
                }
                else
                {
                    sinePhase = doubleAngleCosine / cosinePhase;
                }
            }

            result = highWord > 0x48000000U
                ? InverseSqrtPi * cosinePhase / Math.Sqrt(absolute)
                : InverseSqrtPi *
                    ((BesselPOne(absolute) * cosinePhase) -
                    (BesselQOne(absolute) * sinePhase)) /
                    Math.Sqrt(absolute);
        }
        else if (highWord < 0x3e400000U)
        {
            result = 0.5 * absolute;
        }
        else
        {
            var square = value * value;
            var numerator = EvaluateBalancedPolynomial(
                square,
                [
                    0,
                    -6.25000000000000000000e-02,
                    1.40705666955189706048e-03,
                    -1.59955631084035597520e-05,
                    4.96727999609584448412e-08,
                ]);
            numerator *= value;
            var denominator = EvaluateBalancedPolynomial(
                square,
                [
                    1,
                    1.91537599538363460805e-02,
                    1.85946785588630915560e-04,
                    1.17718464042623683263e-06,
                    5.04636257076217042715e-09,
                    1.23542274426137913908e-11,
                ]);
            return (value * 0.5) + (numerator / denominator);
        }

        return IsNegative(value) ? -result : result;
    }

    internal static double jq_y0(double value)
    {
        if (double.IsNaN(value) || value < 0)
        {
            return double.NaN;
        }

        if (value == 0)
        {
            return double.NegativeInfinity;
        }

        if (double.IsPositiveInfinity(value))
        {
            return 0;
        }

        var highWord = AbsoluteHighWord(value);
        if (highWord >= 0x40000000U)
        {
            var sine = Math.Sin(value);
            var cosine = Math.Cos(value);
            var sinePhase = sine - cosine;
            var cosinePhase = sine + cosine;
            if (highWord < 0x7fe00000U)
            {
                var doubleAngleCosine = -Math.Cos(value + value);
                if ((sine * cosine) < 0)
                {
                    cosinePhase = doubleAngleCosine / sinePhase;
                }
                else
                {
                    sinePhase = doubleAngleCosine / cosinePhase;
                }
            }

            if (highWord > 0x48000000U)
            {
                return InverseSqrtPi * sinePhase / Math.Sqrt(value);
            }

            return InverseSqrtPi *
                ((BesselPZero(value) * sinePhase) + (BesselQZero(value) * cosinePhase)) /
                Math.Sqrt(value);
        }

        if (highWord <= 0x3e400000U)
        {
            return -7.38042951086872317523e-02 + (TwoOverPi * Math.Log(value));
        }

        var square = value * value;
        var numerator = EvaluateBalancedPolynomial(
            square,
            [
                -7.38042951086872317523e-02,
                1.76666452509181115538e-01,
                -1.38185671945596898896e-02,
                3.47453432093683650238e-04,
                -3.81407053724364161125e-06,
                1.95590137035022920206e-08,
                -3.98205194132103398453e-11,
            ]);
        var denominator = EvaluateBalancedPolynomial(
            square,
            [
                1,
                1.27304834834123699328e-02,
                7.60068627350353253702e-05,
                2.59150851840457805467e-07,
                4.41110311332675467403e-10,
            ]);
        return (numerator / denominator) +
            (TwoOverPi * (jq_j0(value) * Math.Log(value)));
    }

    internal static double jq_y1(double value)
    {
        if (double.IsNaN(value) || value < 0)
        {
            return double.NaN;
        }

        if (value == 0)
        {
            return double.NegativeInfinity;
        }

        if (double.IsPositiveInfinity(value))
        {
            return 0;
        }

        var highWord = AbsoluteHighWord(value);
        if (highWord >= 0x40000000U)
        {
            var sine = Math.Sin(value);
            var cosine = Math.Cos(value);
            var sinePhase = -sine - cosine;
            var cosinePhase = sine - cosine;
            if (highWord < 0x7fe00000U)
            {
                var doubleAngleCosine = Math.Cos(value + value);
                if ((sine * cosine) > 0)
                {
                    cosinePhase = doubleAngleCosine / sinePhase;
                }
                else
                {
                    sinePhase = doubleAngleCosine / cosinePhase;
                }
            }

            if (highWord > 0x48000000U)
            {
                return InverseSqrtPi * sinePhase / Math.Sqrt(value);
            }

            return InverseSqrtPi *
                ((BesselPOne(value) * sinePhase) + (BesselQOne(value) * cosinePhase)) /
                Math.Sqrt(value);
        }

        if (highWord <= 0x3c900000U)
        {
            return -TwoOverPi / value;
        }

        var square = value * value;
        var numerator = EvaluateBalancedPolynomial(
            square,
            [
                -1.96057090646238940668e-01,
                5.04438716639811282616e-02,
                -1.91256895875763547298e-03,
                2.35252600561610495928e-05,
                -9.19099158039878874504e-08,
            ]);
        var denominator = EvaluateBalancedPolynomial(
            square,
            [
                1,
                1.99167318236649903973e-02,
                2.02552581025135171496e-04,
                1.35608801097516229404e-06,
                6.22741452364621501295e-09,
                1.66559246207992079114e-11,
            ]);
        return (value * (numerator / denominator)) +
            (TwoOverPi * ((jq_j1(value) * Math.Log(value)) - (1 / value)));
    }

    internal static double jq_jn(int order, double value)
    {
        if (double.IsNaN(value))
        {
            return double.NaN;
        }

        var normalizedOrder = Math.Abs((long)order);
        if (normalizedOrder == 0)
        {
            return jq_j0(value);
        }

        var negative = (normalizedOrder & 1) != 0 && ((order < 0) != IsNegative(value));
        var absolute = Math.Abs(value);
        if (normalizedOrder == 1)
        {
            var firstOrderValue = jq_j1(absolute);
            return negative ? -firstOrderValue : firstOrderValue;
        }

        if (absolute == 0 || double.IsPositiveInfinity(absolute))
        {
            return Math.CopySign(0, negative ? -1 : 1);
        }

        double result;
        if (normalizedOrder <= absolute)
        {
            if (AbsoluteHighWord(absolute) >= 0x52d00000U)
            {
                var sine = Math.Sin(absolute);
                var cosine = Math.Cos(absolute);
                var phase = (normalizedOrder & 3) switch
                {
                    0 => cosine + sine,
                    1 => -cosine + sine,
                    2 => -cosine - sine,
                    _ => cosine - sine,
                };
                result = InverseSqrtPi * phase / Math.Sqrt(absolute);
            }
            else
            {
                var previous = jq_j0(absolute);
                result = jq_j1(absolute);
                for (long currentOrder = 1; currentOrder < normalizedOrder; currentOrder++)
                {
                    var next = result;
                    result *= (double)(currentOrder + currentOrder) / absolute;
                    result -= previous;
                    previous = next;
                }
            }
        }
        else if (AbsoluteHighWord(absolute) < 0x3e100000U)
        {
            if (normalizedOrder > 33)
            {
                result = 0;
            }
            else
            {
                var half = absolute * 0.5;
                var power = half;
                var factorial = 1d;
                for (long index = 2; index <= normalizedOrder; index++)
                {
                    power *= half;
                    factorial *= index;
                }

                result = power / factorial;
            }
        }
        else
        {
            result = BesselJBackward(normalizedOrder, absolute);
        }

        return negative ? -result : result;
    }

    internal static double jq_yn(int order, double value)
    {
        if (double.IsNaN(value) || value < 0)
        {
            return double.NaN;
        }

        var normalizedOrder = Math.Abs((long)order);
        var sign = order < 0 && (normalizedOrder & 1) != 0 ? -1 : 1;
        if (value == 0)
        {
            return sign * double.NegativeInfinity;
        }

        if (normalizedOrder == 0)
        {
            return jq_y0(value);
        }

        if (normalizedOrder == 1)
        {
            return sign * jq_y1(value);
        }

        if (double.IsPositiveInfinity(value))
        {
            // jq's negative-order normalization makes yn(-1, +inf) negative
            // zero, while the remaining infinite-input orders produce +0.
            return 0;
        }

        if (AbsoluteHighWord(value) >= 0x52d00000U)
        {
            var sine = Math.Sin(value);
            var cosine = Math.Cos(value);
            var phase = (normalizedOrder & 3) switch
            {
                0 => sine - cosine,
                1 => -sine - cosine,
                2 => -sine + cosine,
                _ => sine + cosine,
            };
            return sign * (InverseSqrtPi * phase / Math.Sqrt(value));
        }

        var previous = jq_y0(value);
        var result = jq_y1(value);
        for (long currentOrder = 1;
             currentOrder < normalizedOrder && double.IsFinite(result);
             currentOrder++)
        {
            var next = ((2 * currentOrder / value) * result) - previous;
            previous = result;
            result = next;
        }

        return sign * result;
    }

    private static double BesselJBackward(long order, double value)
    {
        var width = 2d / value;
        var quotient0 = (order + order) / value;
        var z = quotient0 + width;
        var quotient1 = (quotient0 * z) - 1;
        long extraTerms = 1;
        while (quotient1 < 1e9)
        {
            extraTerms++;
            z += width;
            var next = (z * quotient1) - quotient0;
            quotient0 = quotient1;
            quotient1 = next;
        }

        var ratio = 0d;
        var lower = 2 * order;
        for (var index = 2 * (order + extraTerms); index >= lower; index -= 2)
        {
            ratio = 1 / ((index / value) - ratio);
        }

        var previous = ratio;
        var current = 1d;
        var target = ratio;
        var orderAsDouble = (double)order;
        var twoOverValue = 2d / value;
        var estimatedLogGrowth = orderAsDouble *
            Math.Log(Math.Abs(twoOverValue * orderAsDouble));
        if (estimatedLogGrowth < 7.09782712893383973096e+02)
        {
            var doubledOrder = (double)((order - 1) + (order - 1));
            for (var index = order - 1; index > 0; index--)
            {
                var next = current;
                current *= doubledOrder;
                current = (current / value) - previous;
                previous = next;
                doubledOrder -= 2;
            }
        }
        else
        {
            var doubledOrder = (double)((order - 1) + (order - 1));
            for (var index = order - 1; index > 0; index--)
            {
                var next = current;
                current *= doubledOrder;
                current = (current / value) - previous;
                previous = next;
                doubledOrder -= 2;
                if (current > 1e100)
                {
                    previous /= current;
                    target /= current;
                    current = 1;
                }
            }
        }

        // Normalize against whichever independently evaluated base order has
        // greater magnitude. This minimizes relative error near a base-order
        // zero and was selected from conditioning analysis, then checked
        // against the pinned jq corpus.
        var orderZero = jq_j0(value);
        var orderOne = jq_j1(value);
        return Math.Abs(orderZero) >= Math.Abs(orderOne)
            ? target * orderZero / current
            : target * orderOne / previous;
    }

    private static readonly double[] PZeroR8 =
    [
        0.00000000000000000000e+00,
        -7.03124999999900357484e-02,
        -8.08167041275349795626e+00,
        -2.57063105679704847262e+02,
        -2.48521641009428822144e+03,
        -5.25304380490729545272e+03,
    ];

    private static readonly double[] PZeroS8 =
    [
        1.16534364619668181717e+02,
        3.83374475364121826715e+03,
        4.05978572648472545552e+04,
        1.16752972564375915681e+05,
        4.76277284146730962675e+04,
    ];

    private static readonly double[] PZeroR5 =
    [
        -1.14125464691894502584e-11,
        -7.03124940873599280078e-02,
        -4.15961064470587782438e+00,
        -6.76747652265167261021e+01,
        -3.31231299649172967747e+02,
        -3.46433388365604912451e+02,
    ];

    private static readonly double[] PZeroS5 =
    [
        6.07539382692300335975e+01,
        1.05125230595704579173e+03,
        5.97897094333855784498e+03,
        9.62544514357774460223e+03,
        2.40605815922939109441e+03,
    ];

    private static readonly double[] PZeroR3 =
    [
        -2.54704601771951915620e-09,
        -7.03119616381481654654e-02,
        -2.40903221549529611423e+00,
        -2.19659774734883086467e+01,
        -5.80791704701737572236e+01,
        -3.14479470594888503854e+01,
    ];

    private static readonly double[] PZeroS3 =
    [
        3.58560338055209726349e+01,
        3.61513983050303863820e+02,
        1.19360783792111533330e+03,
        1.12799679856907414432e+03,
        1.73580930813335754692e+02,
    ];

    private static readonly double[] PZeroR2 =
    [
        -8.87534333032526411254e-08,
        -7.03030995483624743247e-02,
        -1.45073846780952986357e+00,
        -7.63569613823527770791e+00,
        -1.11931668860356747786e+01,
        -3.23364579351335335033e+00,
    ];

    private static readonly double[] PZeroS2 =
    [
        2.22202997532088808441e+01,
        1.36206794218215208048e+02,
        2.70470278658083486789e+02,
        1.53875394208320329881e+02,
        1.46576176948256193810e+01,
    ];

    private static readonly double[] QZeroR8 =
    [
        0.00000000000000000000e+00,
        7.32421874999935051953e-02,
        1.17682064682252693899e+01,
        5.57673380256401856059e+02,
        8.85919720756468632317e+03,
        3.70146267776887834771e+04,
    ];

    private static readonly double[] QZeroS8 =
    [
        1.63776026895689824414e+02,
        8.09834494656449805916e+03,
        1.42538291419120476348e+05,
        8.03309257119514397345e+05,
        8.40501579819060512818e+05,
        -3.43899293537866615225e+05,
    ];

    private static readonly double[] QZeroR5 =
    [
        1.84085963594515531381e-11,
        7.32421766612684765896e-02,
        5.83563508962056953777e+00,
        1.35111577286449829671e+02,
        1.02724376596164097464e+03,
        1.98997785864605384631e+03,
    ];

    private static readonly double[] QZeroS5 =
    [
        8.27766102236537761883e+01,
        2.07781416421392987104e+03,
        1.88472887785718085070e+04,
        5.67511122894947329769e+04,
        3.59767538425114471465e+04,
        -5.35434275601944773371e+03,
    ];

    private static readonly double[] QZeroR3 =
    [
        4.37741014089738620906e-09,
        7.32411180042911447163e-02,
        3.34423137516170720929e+00,
        4.26218440745412650017e+01,
        1.70808091340565596283e+02,
        1.66733948696651168575e+02,
    ];

    private static readonly double[] QZeroS3 =
    [
        4.87588729724587182091e+01,
        7.09689221056606015736e+02,
        3.70414822620111362994e+03,
        6.46042516752568917582e+03,
        2.51633368920368957333e+03,
        -1.49247451836156386662e+02,
    ];

    private static readonly double[] QZeroR2 =
    [
        1.50444444886983272379e-07,
        7.32234265963079278272e-02,
        1.99819174093815998816e+00,
        1.44956029347885735348e+01,
        3.16662317504781540833e+01,
        1.62527075710929267416e+01,
    ];

    private static readonly double[] QZeroS2 =
    [
        3.03655848355219184498e+01,
        2.69348118608049844624e+02,
        8.44783757595320139444e+02,
        8.82935845112488550512e+02,
        2.12666388511798828631e+02,
        -5.31095493882666946917e+00,
    ];

    private static readonly double[] POneR8 =
    [
        0.00000000000000000000e+00,
        1.17187499999988647970e-01,
        1.32394806593073575129e+01,
        4.12051854307378562225e+02,
        3.87474538913960532227e+03,
        7.91447954031891731574e+03,
    ];

    private static readonly double[] POneS8 =
    [
        1.14207370375678408436e+02,
        3.65093083420853463394e+03,
        3.69562060269033463555e+04,
        9.76027935934950801311e+04,
        3.08042720627888811578e+04,
    ];

    private static readonly double[] POneR5 =
    [
        1.31990519556243522749e-11,
        1.17187493190614097638e-01,
        6.80275127868432871736e+00,
        1.08308182990189109773e+02,
        5.17636139533199752805e+02,
        5.28715201363337541807e+02,
    ];

    private static readonly double[] POneS5 =
    [
        5.92805987221131331921e+01,
        9.91401418733614377743e+02,
        5.35326695291487976647e+03,
        7.84469031749551231769e+03,
        1.50404688810361062679e+03,
    ];

    private static readonly double[] POneR3 =
    [
        3.02503916137373618024e-09,
        1.17186865567253592491e-01,
        3.93297750033315640650e+00,
        3.51194035591636932736e+01,
        9.10550110750781271918e+01,
        4.85590685197364919645e+01,
    ];

    private static readonly double[] POneS3 =
    [
        3.47913095001251519989e+01,
        3.36762458747825746741e+02,
        1.04687139975775130551e+03,
        8.90811346398256432622e+02,
        1.03787932439639277504e+02,
    ];

    private static readonly double[] POneR2 =
    [
        1.07710830106873743082e-07,
        1.17176219462683348094e-01,
        2.36851496667608785174e+00,
        1.22426109148261232917e+01,
        1.76939711271687727390e+01,
        5.07352312588818499250e+00,
    ];

    private static readonly double[] POneS2 =
    [
        2.14364859363821409488e+01,
        1.25290227168402751090e+02,
        2.32276469057162813669e+02,
        1.17679373287147100768e+02,
        8.36463893371618283368e+00,
    ];

    private static readonly double[] QOneR8 =
    [
        0.00000000000000000000e+00,
        -1.02539062499992714161e-01,
        -1.62717534544589987888e+01,
        -7.59601722513950107896e+02,
        -1.18498066702429587167e+04,
        -4.84385124285750353010e+04,
    ];

    private static readonly double[] QOneS8 =
    [
        1.61395369700722909556e+02,
        7.82538599923348465381e+03,
        1.33875336287249578163e+05,
        7.19657723683240939863e+05,
        6.66601232617776375264e+05,
        -2.94490264303834643215e+05,
    ];

    private static readonly double[] QOneR5 =
    [
        -2.08979931141764104297e-11,
        -1.02539050241375426231e-01,
        -8.05644828123936029840e+00,
        -1.83669607474888380239e+02,
        -1.37319376065508163265e+03,
        -2.61244440453215656817e+03,
    ];

    private static readonly double[] QOneS5 =
    [
        8.12765501384335777857e+01,
        1.99179873460485964642e+03,
        1.74684851924908907677e+04,
        4.98514270910352279316e+04,
        2.79480751638918118260e+04,
        -4.71918354795128470869e+03,
    ];

    private static readonly double[] QOneR3 =
    [
        -5.07831226461766561369e-09,
        -1.02537829820837089745e-01,
        -4.61011581139473403113e+00,
        -5.78472216562783643212e+01,
        -2.28244540737631695038e+02,
        -2.19210128478909325622e+02,
    ];

    private static readonly double[] QOneS3 =
    [
        4.76651550323729509273e+01,
        6.73865112676699709482e+02,
        3.38015286679526343505e+03,
        5.54772909720722782367e+03,
        1.90311919338810798763e+03,
        -1.35201191444307340817e+02,
    ];

    private static readonly double[] QOneR2 =
    [
        -1.78381727510958865572e-07,
        -1.02517042607985553460e-01,
        -2.75220568278187460720e+00,
        -1.96636162643703720221e+01,
        -4.23253133372830490089e+01,
        -2.13719211703704061733e+01,
    ];

    private static readonly double[] QOneS2 =
    [
        2.95333629060523854548e+01,
        2.52981549982190529136e+02,
        7.57502834868645436472e+02,
        7.39393205320467245656e+02,
        1.55949003336666123687e+02,
        -4.95949898822628210127e+00,
    ];

    private static double BesselPZero(double value)
    {
        var highWord = AbsoluteHighWord(value);
        var (numerator, denominator) = highWord switch
        {
            >= 0x40200000U => (PZeroR8, PZeroS8),
            >= 0x40122e8bU => (PZeroR5, PZeroS5),
            >= 0x4006db6dU => (PZeroR3, PZeroS3),
            _ => (PZeroR2, PZeroS2),
        };
        return 1 + BesselPQuotient(value, numerator, denominator);
    }

    private static double BesselQZero(double value)
    {
        var highWord = AbsoluteHighWord(value);
        var (numerator, denominator) = highWord switch
        {
            >= 0x40200000U => (QZeroR8, QZeroS8),
            >= 0x40122e8bU => (QZeroR5, QZeroS5),
            >= 0x4006db6dU => (QZeroR3, QZeroS3),
            _ => (QZeroR2, QZeroS2),
        };
        return (-0.125 + BesselQQuotient(value, numerator, denominator)) / value;
    }

    private static double BesselPOne(double value)
    {
        var highWord = AbsoluteHighWord(value);
        var (numerator, denominator) = highWord switch
        {
            >= 0x40200000U => (POneR8, POneS8),
            >= 0x40122e8bU => (POneR5, POneS5),
            >= 0x4006db6dU => (POneR3, POneS3),
            _ => (POneR2, POneS2),
        };
        return 1 + BesselPQuotient(value, numerator, denominator);
    }

    private static double BesselQOne(double value)
    {
        var highWord = AbsoluteHighWord(value);
        var (numerator, denominator) = highWord switch
        {
            >= 0x40200000U => (QOneR8, QOneS8),
            >= 0x40122e8bU => (QOneR5, QOneS5),
            >= 0x4006db6dU => (QOneR3, QOneS3),
            _ => (QOneR2, QOneS2),
        };
        return (0.375 + BesselQQuotient(value, numerator, denominator)) / value;
    }

    // This balanced managed evaluation groups adjacent coefficients, reducing
    // the dependency depth while retaining each fdlibm polynomial exactly.
    // The grouping is an IEEE-754 evaluation choice verified against the
    // pinned jq oracle; coefficients and piecewise formulas remain Netlib's.
    private static double EvaluateBalancedPolynomial(
        double value,
        ReadOnlySpan<double> coefficients)
    {
        var square = value * value;
        var result = coefficients[0] + (value * coefficients[1]);
        var power = square;
        var index = 2;
        for (; index + 1 < coefficients.Length; index += 2)
        {
            result += power * (coefficients[index] + (value * coefficients[index + 1]));
            power *= square;
        }

        return index < coefficients.Length
            ? result + (power * coefficients[index])
            : result;
    }

    private static double BesselPQuotient(
        double value,
        ReadOnlySpan<double> numerator,
        ReadOnlySpan<double> denominator)
    {
        var reciprocalSquare = 1 / (value * value);
        var numeratorValue = EvaluateBalancedPolynomial(reciprocalSquare, numerator);
        var denominatorValue = EvaluateBalancedPolynomial(
            reciprocalSquare,
            [
                1,
                denominator[0],
                denominator[1],
                denominator[2],
                denominator[3],
                denominator[4],
            ]);
        return numeratorValue / denominatorValue;
    }

    private static double BesselQQuotient(
        double value,
        ReadOnlySpan<double> numerator,
        ReadOnlySpan<double> denominator)
    {
        var reciprocalSquare = 1 / (value * value);
        var numeratorValue = EvaluateBalancedPolynomial(reciprocalSquare, numerator);
        var denominatorValue = EvaluateBalancedPolynomial(
            reciprocalSquare,
            [
                1,
                denominator[0],
                denominator[1],
                denominator[2],
                denominator[3],
                denominator[4],
                denominator[5],
            ]);
        return numeratorValue / denominatorValue;
    }

    private static double ErfSmallRatio(double value)
    {
        var z = value * value;
        return EvaluateBalancedPolynomial(
            z,
            [
                1.28379167095512558561e-01,
                -3.25042107247001499370e-01,
                -2.84817495755985104766e-02,
                -5.77027029648944159157e-03,
                -2.37630166566501626084e-05,
            ]) / EvaluateBalancedPolynomial(
            z,
            [
                1,
                3.97917223959155352819e-01,
                6.50222499887672944485e-02,
                5.08130628187576562776e-03,
                1.32494738004321644526e-04,
                -3.96022827877536812320e-06,
            ]);
    }

    private static double ErfMiddleRatio(double absolute)
    {
        var shifted = absolute - 1;
        return EvaluateBalancedPolynomial(
            shifted,
            [
                -2.36211856075265944077e-03,
                4.14856118683748331666e-01,
                -3.72207876035701323847e-01,
                3.18346619901161753674e-01,
                -1.10894694282396677476e-01,
                3.54783043256182359371e-02,
                -2.16637559486879084300e-03,
            ]) / EvaluateBalancedPolynomial(
            shifted,
            [
                1,
                1.06420880400844228286e-01,
                5.40397917702171048937e-01,
                7.18286544141962662868e-02,
                1.26171219808761642112e-01,
                1.36370839120290507362e-02,
                1.19844998467991074170e-02,
            ]);
    }

    private static double ErfTail(double value, bool complement)
    {
        var reciprocalSquare = 1 / (value * value);
        double numerator;
        double denominator;
        var highWord = (uint)(BitConverter.DoubleToUInt64Bits(value) >> 32) & 0x7fffffffU;
        var useFirstApproximation = complement
            ? highWord < 0x4006db6dU
            : highWord < 0x4006db6eU;
        if (useFirstApproximation)
        {
            numerator = EvaluateBalancedPolynomial(
                reciprocalSquare,
                [
                    -9.86494403484714822705e-03,
                    -6.93858572707181764372e-01,
                    -1.05586262253232909814e+01,
                    -6.23753324503260060396e+01,
                    -1.62396669462573470355e+02,
                    -1.84605092906711035994e+02,
                    -8.12874355063065934246e+01,
                    -9.81432934416914548592,
                ]);
            denominator = EvaluateBalancedPolynomial(
                reciprocalSquare,
                [
                    1,
                    1.96512716674392571292e+01,
                    1.37657754143519042600e+02,
                    4.34565877475229228821e+02,
                    6.45387271733267880336e+02,
                    4.29008140027567833386e+02,
                    1.08635005541779435134e+02,
                    6.57024977031928170135,
                    -6.04244152148580987438e-02,
                ]);
        }
        else
        {
            numerator = EvaluateBalancedPolynomial(
                reciprocalSquare,
                [
                    -9.86494292470009928597e-03,
                    -7.99283237680523006574e-01,
                    -1.77579549177547519889e+01,
                    -1.60636384855821916062e+02,
                    -6.37566443368389627722e+02,
                    -1.02509513161107724954e+03,
                    -4.83519191608651397019e+02,
                ]);
            denominator = EvaluateBalancedPolynomial(
                reciprocalSquare,
                [
                    1,
                    3.03380607434824582924e+01,
                    3.25792512996573918826e+02,
                    1.53672958608443695994e+03,
                    3.19985821950859553908e+03,
                    2.55305040643316442583e+03,
                    4.74528541206955367215e+02,
                    -2.24409524465858183362e+01,
                ]);
        }

        var truncated = TruncateLowWord(value);
        return Math.Exp((-truncated * truncated) - 0.5625) *
            Math.Exp(((truncated - value) * (truncated + value)) +
                (numerator / denominator)) /
            value;
    }

    private static bool IsNegative(double value) => BitConverter.DoubleToInt64Bits(value) < 0;

    private static uint AbsoluteHighWord(double value) =>
        (uint)(BitConverter.DoubleToUInt64Bits(value) >> 32) & 0x7fffffffU;

    private static int SignedHighWord(double value) =>
        unchecked((int)(BitConverter.DoubleToUInt64Bits(value) >> 32));

    private static uint LowWord(double value) =>
        (uint)BitConverter.DoubleToUInt64Bits(value);

    private static double WithHighWord(double value, uint highWord)
    {
        var bits = BitConverter.DoubleToUInt64Bits(value);
        return BitConverter.UInt64BitsToDouble(((ulong)highWord << 32) | (bits & 0xffffffffUL));
    }

    private static double AddHighWordExponent(double value, int exponent)
    {
        var bits = BitConverter.DoubleToUInt64Bits(value);
        var highWord = unchecked((int)(bits >> 32));
        highWord = unchecked(highWord + (exponent << 20));
        return BitConverter.UInt64BitsToDouble(((ulong)(uint)highWord << 32) | (bits & 0xffffffffUL));
    }

    private static double TruncateLowWord(double value)
    {
        var bits = BitConverter.DoubleToUInt64Bits(value) & 0xffffffff00000000UL;
        return BitConverter.UInt64BitsToDouble(bits);
    }

}
