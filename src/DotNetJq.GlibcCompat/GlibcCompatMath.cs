/*
 * DOTNETJQ DEPENDENCY PORT MAP
 * Upstream repository: https://sourceware.org/git/glibc.git
 * Upstream tag: glibc-2.39
 * Upstream tag object: 9609a435f3f9a07c1cf607ad5821b12f735abd69
 * Upstream release commit: ef321e23c20eebc6d6fb4044425c00e6df27b05f
 * Upstream files: sysdeps/ieee754/dbl-64/e_gamma_r.c,
 *   sysdeps/ieee754/dbl-64/lgamma_neg.c,
 *   sysdeps/ieee754/dbl-64/lgamma_product.c,
 *   sysdeps/ieee754/ldbl-96/gamma_product.c,
 *   math/mul_split.h, sysdeps/ieee754/dbl-64/e_exp.c,
 *   sysdeps/ieee754/dbl-64/e_exp_data.c,
 *   sysdeps/ieee754/dbl-64/s_expm1.c, and
 *   sysdeps/ieee754/dbl-64/e_lgamma_r.c.
 * Target file: src/DotNetJq.GlibcCompat/GlibcCompatMath.cs
 * Strategy: PORT
 * Behavioral contract: preserve jq-visible gamma, lgamma, lgamma_r, and
 * tgamma binary64 results, signs, poles, infinities, NaNs, and signed zero.
 * Known differences: none across the frozen jq-1.8.2 binary64 corpus.
 * Tests: GammaCompatExactOracleCorpusTests,
 * SpecialMathExactOracleCorpusTests, and LibmCompatibilityTests.
 * Modified for DotNetJq: 2026-09-06.
 *
 * Copyright (C) 1997-2024 Free Software Foundation, Inc.
 * Copyright (C) 2013-2024 Free Software Foundation, Inc.
 * Copyright (C) 2015-2024 Free Software Foundation, Inc.
 *
 * This component is free software; you can redistribute it and/or modify it
 * under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation; either version 2.1 of the License, or (at your
 * option) any later version. See COPYING.LIB in this component's source.
 */

namespace DotNetJq.GlibcCompat;

/// <summary>
/// Provides the managed GNU C Library 2.39-compatible gamma-family operations
/// used by DotNetJq.
/// </summary>
/// <remarks>
/// These methods reproduce jq-visible binary64 values and signs. They do not
/// reproduce native GNU libm ABI details, <c>errno</c>, or floating-point
/// environment flags.
/// </remarks>
public static partial class GlibcCompatMath
{
    private const double MachineEpsilon = 2.22044604925031308085e-16;

    /// <summary>
    /// Returns the natural logarithm of the absolute value of the gamma function,
    /// matching the historical GNU <c>gamma</c> entry point used by jq.
    /// </summary>
    /// <param name="value">The binary64 argument.</param>
    /// <returns>
    /// <c>log(|Gamma(value)|)</c>; positive infinity at zero and negative-integer
    /// poles, and <see cref="double.NaN"/> when <paramref name="value"/> is NaN.
    /// </returns>
    /// <remarks>
    /// This operation is intentionally an alias for <see cref="Lgamma"/>; unlike
    /// <see cref="Tgamma"/>, it does not return the gamma function itself.
    /// </remarks>
    public static double Gamma(double value) => Lgamma(value);

    /// <summary>
    /// Returns the natural logarithm of the absolute value of the gamma function.
    /// </summary>
    /// <param name="value">The binary64 argument.</param>
    /// <returns>
    /// <c>log(|Gamma(value)|)</c>; positive infinity at zero, negative-integer
    /// poles, and either infinity; <see cref="double.NaN"/> for a NaN argument.
    /// </returns>
    /// <remarks>
    /// The sign of the gamma function is available from <see cref="LgammaR"/>.
    /// </remarks>
    public static double Lgamma(double value) => LgammaR(value).Value;

    /// <summary>
    /// Returns the natural logarithm of the absolute value of the gamma function
    /// together with the sign of the gamma function.
    /// </summary>
    /// <param name="value">The binary64 argument.</param>
    /// <returns>
    /// A tuple whose <c>Value</c> is <c>log(|Gamma(value)|)</c> and whose
    /// <c>Sign</c> is <c>1</c> or <c>-1</c>. For finite non-pole arguments,
    /// <c>Sign</c> is the sign of the gamma function. At positive and negative
    /// zero it is respectively <c>1</c> and <c>-1</c>; at negative-integer poles,
    /// NaN, and either infinity it is <c>1</c>.
    /// </returns>
    public static (double Value, int Sign) LgammaR(double value)
    {
        if (double.IsNaN(value))
        {
            return (double.NaN, 1);
        }

        if (double.IsInfinity(value))
        {
            return (double.PositiveInfinity, 1);
        }

        if (value == 0)
        {
            return (double.PositiveInfinity, IsNegative(value) ? -1 : 1);
        }

        var sign = 1;
        var negativeAdjustment = 0d;
        var originalNegative = value < 0;
        if (Math.Abs(value) < Math.ScaleB(1, -70))
        {
            return (-Math.Log(Math.Abs(value)), originalNegative ? -1 : 1);
        }

        if (originalNegative && value < -2 && value > -28)
        {
            return LgammaNegative(value);
        }

        if (originalNegative)
        {
            if (Math.Abs(value) >= Math.ScaleB(1, 52))
            {
                return (double.PositiveInfinity, 1);
            }

            var sine = SinPi(value);
            if (sine == 0)
            {
                return (double.PositiveInfinity, 1);
            }

            negativeAdjustment = Math.Log(Math.PI / Math.Abs(sine * value));
            if (sine < 0)
            {
                sign = -1;
            }

            value = -value;
        }

        double result;
        if (value is 1 or 2)
        {
            result = 0;
        }
        else if (value < 2)
        {
            double y;
            int approximation;
            if (value <= 0.8999996185302734)
            {
                result = -Math.Log(value);
                if (value >= 0.7315998077392578)
                {
                    y = 1 - value;
                    approximation = 0;
                }
                else if (value >= 0.23163998126983643)
                {
                    y = value - (1.46163214496836224576 - 1);
                    approximation = 1;
                }
                else
                {
                    y = value;
                    approximation = 2;
                }
            }
            else
            {
                result = 0;
                if (value >= 1.7316312789916992)
                {
                    y = 2 - value;
                    approximation = 0;
                }
                else if (value >= 1.2316322326660156)
                {
                    y = value - 1.46163214496836224576;
                    approximation = 1;
                }
                else
                {
                    y = value - 1;
                    approximation = 2;
                }
            }

            result += approximation switch
            {
                0 => LgammaApproximationZero(y),
                1 => LgammaApproximationOne(y),
                _ => LgammaApproximationTwo(y),
            };
        }
        else if (value < 8)
        {
            var integer = (int)value;
            var y = value - integer;
            var numerator = y * Polynomial(
                y,
                [
                    -7.72156649015328655494e-02,
                    2.14982415960608852501e-01,
                    3.25778796408930981787e-01,
                    1.46350472652464452805e-01,
                    2.66422703033638609560e-02,
                    1.84028451407337715652e-03,
                    3.19475326584100867617e-05,
                ]);
            var denominator = 1 + (y * Polynomial(
                y,
                [
                    1.39200533467621045958,
                    7.21935547567138069525e-01,
                    1.71933865632803078993e-01,
                    1.86459191715652901344e-02,
                    7.77942496381893596434e-04,
                    7.32668430744625636189e-06,
                ]));
            result = (0.5 * y) + (numerator / denominator);
            var product = 1d;
            // Match fdlibm's fall-through switch: factors are multiplied from
            // y + (integer - 1) down to y + 2, not in ascending order.
            for (var factor = integer - 1; factor >= 2; factor--)
            {
                product *= y + factor;
            }

            if (integer >= 3)
            {
                result += Math.Log(product);
            }
        }
        else if (value < Math.ScaleB(1, 58))
        {
            var reciprocal = 1 / value;
            var square = reciprocal * reciprocal;
            var correction = 4.18938533204672725052e-01 +
                (reciprocal * Polynomial(
                    square,
                    [
                        8.33333333333329678849e-02,
                        -2.77777777728775536470e-03,
                        7.93650558643019558500e-04,
                        -5.95187557450339963135e-04,
                        8.36339918996282139126e-04,
                        -1.63092934096575273989e-03,
                    ]));
            result = ((value - 0.5) * (Math.Log(value) - 1)) + correction;
        }
        else
        {
            result = value * (Math.Log(value) - 1);
        }

        return originalNegative ? (negativeAdjustment - result, sign) : (result, sign);
    }

    /// <summary>Returns the gamma function of a binary64 argument.</summary>
    /// <param name="value">The binary64 argument.</param>
    /// <returns>
    /// <c>Gamma(value)</c>. Positive and negative zero produce positive and
    /// negative infinity respectively; negative-integer poles and negative
    /// infinity produce <see cref="double.NaN"/>; positive infinity and positive
    /// overflow produce positive infinity. Finite underflow retains its GNU
    /// libm-compatible sign.
    /// </returns>
    public static double Tgamma(double value)
    {
        if (value == 0)
        {
            return 1 / value;
        }

        if (value < 0 && double.IsFinite(value) && value == Math.Truncate(value))
        {
            return double.NaN;
        }

        if (double.IsNegativeInfinity(value))
        {
            return double.NaN;
        }

        if (double.IsNaN(value) || double.IsPositiveInfinity(value))
        {
            return value + value;
        }

        if (value >= 172)
        {
            return double.PositiveInfinity;
        }

        if (value > 0)
        {
            var (positiveMagnitude, exponentAdjustment) = GammaPositive(value);
            return Math.ScaleB(positiveMagnitude, exponentAdjustment);
        }

        if (value >= -(MachineEpsilon / 4))
        {
            return 1 / value;
        }

        var truncated = Math.Truncate(value);
        var sign = truncated == 2 * Math.Truncate(truncated / 2) ? -1 : 1;
        double magnitude;
        if (value <= -184)
        {
            magnitude = 0;
        }
        else
        {
            var fractional = truncated - value;
            if (fractional > 0.5)
            {
                fractional = 1 - fractional;
            }

            var sinPi = fractional <= 0.25
                ? Math.Sin(Math.PI * fractional)
                : Math.Cos(Math.PI * (0.5 - fractional));
            var (positiveGamma, exponentAdjustment) = GammaPositive(-value);
            var (high1, low1) = GammaMultiplySplit(sinPi, positiveGamma);
            var (high2, low2) = GammaMultiplySplit(high1, value);
            low2 += low1 * value;

            (high1, low1) = GammaDivideExpansion(
                3.141592653589793,
                1.2246467991473532e-16,
                high2,
                low2);
            magnitude = Math.ScaleB(-high1, -exponentAdjustment);
        }

        return sign < 0 ? -magnitude : magnitude;
    }

    private static (double Magnitude, int ExponentAdjustment) GammaPositive(double value)
    {
        if (value < 0.5)
        {
            return (GlibcExpPrimary(Lgamma(value + 1)) / value, 0);
        }

        if (value <= 1.5)
        {
            return (GlibcExpPrimary(Lgamma(value)), 0);
        }

        if (value < 6.5)
        {
            var count = (int)Math.Ceiling(value - 1.5);
            var adjusted = value - count;
            var (product, error) = GammaProduct(adjusted, 0, count);
            return (GlibcExpPrimary(Lgamma(adjusted)) * product * (1 + error), 0);
        }

        var productError = 0d;
        var adjustmentError = 0d;
        var adjustedValue = value;
        var productValue = 1d;
        if (value < 12)
        {
            var count = (int)Math.Ceiling(12 - value);
            adjustedValue = value + count;
            adjustmentError = value - (adjustedValue - count);
            (productValue, productError) = GammaProduct(
                adjustedValue - count,
                adjustmentError,
                count);
        }

        var adjustedInteger = Math.Round(adjustedValue, MidpointRounding.AwayFromZero);
        var adjustedFraction = adjustedValue - adjustedInteger;
        var (adjustedMantissa, adjustedExponent) = Frexp(adjustedValue);
        if (adjustedMantissa < 0.7071067811865475)
        {
            adjustedExponent--;
            adjustedMantissa *= 2;
        }

        var exponentAdjustment = adjustedExponent * (int)adjustedInteger;
        var (high1, low1) = GammaMultiplySplit(
            GlibcPowPositive(adjustedMantissa, adjustedValue),
            GlibcExp2Primary(adjustedExponent * adjustedFraction));
        var (high2, low2) = GammaMultiplySplit(
            GlibcExpPrimary(-adjustedValue),
            Math.Sqrt((2 * Math.PI) / adjustedValue));
        (high1, low1) = GammaMultiplyExpansion(high1, low1, high2, low2);
        (high1, low1) = GammaDivideExpansion(
            high1,
            low1,
            productValue,
            productValue * productError);

        ReadOnlySpan<double> coefficients =
        [
            0.08333333333333333,
            -0.002777777777777778,
            0.0007936507936507937,
            -0.0005952380952380953,
            0.0008417508417508417,
            -0.0019175269175269176,
        ];
        var sum = coefficients[^1];
        var adjustedSquare = adjustedValue * adjustedValue;
        for (var index = coefficients.Length - 2; index >= 0; index--)
        {
            sum = (sum / adjustedSquare) + coefficients[index];
        }

        var exponentialAdjustment =
            (adjustmentError * Math.Log(adjustedValue)) + (sum / adjustedValue);
        low1 += high1 * GammaExpm1(exponentialAdjustment);
        return (high1 + low1, exponentAdjustment);
    }

    private static double GammaExpm1(double value)
    {
        const double q1 = -3.33333333333331316428e-02;
        const double q2 = 1.58730158725481460165e-03;
        const double q3 = -7.93650757867487942473e-05;
        const double q4 = 4.00821782732936239552e-06;
        const double q5 = -2.01099218183624371326e-07;

        var half = 0.5 * value;
        var halfSquare = value * half;
        var first = 1 + (halfSquare * q1);
        var square = halfSquare * halfSquare;
        var second = q2 + (halfSquare * q3);
        var fourth = square * square;
        var third = q4 + (halfSquare * q5);
        var ratio = (first + (square * second)) + (fourth * third);
        var correction = 3 - (ratio * half);
        var error = halfSquare * ((ratio - correction) / (6 - (value * correction)));
        return value - ((value * error) - halfSquare);
    }

    private static (double Product, double Error) GammaProduct(
        double value,
        double valueError,
        int count)
    {
        // The pinned Ubuntu glibc build evaluates this routine in x87 binary80
        // precision, rounds the accumulated product once, and then stores its
        // relative residual as binary64. Model that build behavior explicitly
        // so the managed result is independent of the host JIT/CPU math path.
        var extendedValue = GammaBinary80.FromDouble(value).Add(
            GammaBinary80.FromDouble(valueError));
        var extendedProduct = extendedValue;
        for (var index = 1; index < count; index++)
        {
            extendedProduct = extendedProduct.Multiply(
                extendedValue.Add(GammaBinary80.FromInteger(index)));
        }

        var product = extendedProduct.ToDouble();
        var error = extendedProduct
            .Subtract(GammaBinary80.FromDouble(product))
            .Divide(GammaBinary80.FromDouble(product))
            .ToDouble();
        return (product, error);
    }

    private static (double High, double Low) GammaMultiplySplit(double left, double right)
    {
        var high = left * right;
        const double splitFactor = 134217729;
        var leftHigh = left * splitFactor;
        var rightHigh = right * splitFactor;
        leftHigh = (left - leftHigh) + leftHigh;
        rightHigh = (right - rightHigh) + rightHigh;
        var leftLow = left - leftHigh;
        var rightLow = right - rightHigh;
        var low = (((leftHigh * rightHigh - high) + (leftHigh * rightLow)) +
            (leftLow * rightHigh)) + (leftLow * rightLow);
        return (high, low);
    }

    private static (double High, double Low) GammaMultiplyExpansion(
        double high1,
        double low1,
        double high2,
        double low2)
    {
        var (high, low) = GammaMultiplySplit(high1, high2);
        var remainder = (high1 * low2) + (high2 * low1);
        var sum = GammaFastTwoSum(high, remainder);
        high = sum.High;
        low -= sum.Low;
        return (high, low);
    }

    private static (double High, double Low) GammaDivideExpansion(
        double high1,
        double low1,
        double high2,
        double low2)
    {
        var high = high1 / high2;
        var (productHigh, productLow) = GammaMultiplySplit(high, high2);
        var residual = high1 - productHigh;
        residual -= productLow;
        var low = residual / high2;

        var correction = ((low1 * high2) - (low2 * high1)) / (high2 * high2);
        var sum = GammaFastTwoSum(high, correction);
        high = sum.High;
        low += sum.Low;
        return GammaFastTwoSum(high, low);
    }

    private static (double High, double Low) GammaFastTwoSum(double left, double right)
    {
        var high = left + right;
        var error = high - left;
        return (high, right - error);
    }

    // glibc 2.39 uses a zero-centered expansion for -28 < x < -2.
    // This avoids the cancellation in the older fdlibm reflection formula.
    private static (double Value, int Sign) LgammaNegative(double value)
    {
        var region = (int)Math.Floor(-2 * value);
        if ((region & 1) == 0 && region == -2 * value)
        {
            return (double.PositiveInfinity, 1);
        }

        var nearestInteger = (region & 1) == 0 ? -region / 2 : (-region - 1) / 2;
        region -= 4;
        var sign = (region & 2) == 0 ? -1 : 1;

        var zeroHigh = LgammaNegativeZeros[region * 2];
        var zeroLow = LgammaNegativeZeros[(region * 2) + 1];
        var difference = (value - zeroHigh) - zeroLow;

        if (region < 2)
        {
            var interval = (int)Math.Floor(-8 * value) - 16;
            var midpoint = (-33 - (2 * interval)) * 0.0625;
            var adjusted = value - midpoint;
            var degree = LgammaNegativePolynomialDegrees[interval];
            var end = LgammaNegativePolynomialEnds[interval];
            var polynomial = LgammaNegativePolynomial[end];
            for (var index = 1; index <= degree; index++)
            {
                polynomial = (polynomial * adjusted) + LgammaNegativePolynomial[end - index];
            }

            return (GammaLog1p(polynomial * difference / (value - nearestInteger)), sign);
        }

        var integerDistance = Math.Abs(nearestInteger - value);
        var zeroIntegerDistance = Math.Abs((nearestInteger - zeroHigh) - zeroLow);
        double sineRatioLog;
        if (zeroIntegerDistance < integerDistance * 0.5)
        {
            sineRatioLog = Math.Log(
                LgammaSinPi(zeroIntegerDistance) / LgammaSinPi(integerDistance));
        }
        else
        {
            var halfZeroDifference = ((region & 1) == 0 ? difference : -difference) * 0.5;
            var sine = LgammaSinPi(halfZeroDifference);
            var cosine = LgammaCosPi(halfZeroDifference);
            sineRatioLog = GammaLog1p(
                2 * sine * (-sine + (cosine * LgammaCotPi(integerDistance))));
        }

        var zeroArgument = 1 - zeroHigh;
        var zeroArgumentError = ((-zeroHigh + (1 - zeroArgument)) - zeroLow);
        var argument = 1 - value;
        var argumentError = -value + (1 - argument);
        var gammaAdjustmentLog = 0d;
        if (region < 6)
        {
            var count = (7 - region) / 2;
            var newZeroArgument = zeroArgument + count;
            var newZeroError = (zeroArgument - (newZeroArgument - count)) + zeroArgumentError;
            zeroArgument = newZeroArgument;
            zeroArgumentError = newZeroError;
            var newArgument = argument + count;
            var newArgumentError = (argument - (newArgument - count)) + argumentError;
            argument = newArgument;
            argumentError = newArgumentError;
            var productMinusOne = LgammaProduct(
                difference,
                argument - count,
                argumentError,
                count);
            gammaAdjustmentLog = -GammaLog1p(productMinusOne);
        }

        const double eHigh = 2.718281828459045;
        const double eLow = 1.4456468917292502e-16;
        var gammaHigh =
            (difference * GammaLog1p(
                (zeroArgument - eHigh - eLow + zeroArgumentError) / eHigh)) +
            ((argument - 0.5 + argumentError) * GammaLog1p(difference / argument));
        gammaHigh += gammaAdjustmentLog;

        var zeroReciprocal = 1 / zeroArgument;
        var reciprocal = 1 / argument;
        var zeroReciprocalSquare = zeroReciprocal * zeroReciprocal;
        var reciprocalSquare = reciprocal * reciprocal;
        var reciprocalDifference = -difference / (argument * zeroArgument);
        Span<double> terms = stackalloc double[12];
        var lastDifference = reciprocalDifference;
        var lastError = reciprocalDifference * reciprocal * (reciprocal + zeroReciprocal);
        terms[0] = lastDifference * LgammaNegativeStirlingCoefficients[0];
        for (var index = 1; index < terms.Length; index++)
        {
            var nextDifference = (lastDifference * zeroReciprocalSquare) + lastError;
            var nextError = lastError * reciprocalSquare;
            terms[index] = nextDifference * LgammaNegativeStirlingCoefficients[index];
            lastDifference = nextDifference;
            lastError = nextError;
        }

        var gammaLow = 0d;
        for (var index = terms.Length - 1; index >= 0; index--)
        {
            gammaLow += terms[index];
        }

        return (sineRatioLog + (gammaHigh + gammaLow), sign);
    }

    private static double LgammaProduct(
        double difference,
        double value,
        double valueError,
        int count)
    {
        var result = 0d;
        var resultError = 0d;
        for (var index = 0; index < count; index++)
        {
            var term = value + index;
            var quotient = difference / term;
            var (multiplyHigh, multiplyLow) = GammaMultiplySplit(quotient, term);
            var quotientLow = (((difference - multiplyHigh) - multiplyLow) / term) -
                ((difference * valueError) / (term * term));
            var (resultHigh, resultLow) = GammaMultiplySplit(result, quotient);
            var sum = result + quotient;
            var sumError = (result - sum) + quotient;
            var next = sum + resultHigh;
            var nextError = (sum - next) + resultHigh;
            resultError += sumError + nextError + resultLow + (resultError * quotient) +
                quotientLow + (quotientLow * (result + resultError));
            result = next;
        }

        return result + resultError;
    }

    private static double LgammaSinPi(double value) => value <= 0.25
        ? Math.Sin(Math.PI * value)
        : Math.Cos(Math.PI * (0.5 - value));

    private static double LgammaCosPi(double value) => value <= 0.25
        ? Math.Cos(Math.PI * value)
        : Math.Sin(Math.PI * (0.5 - value));

    private static double LgammaCotPi(double value) =>
        LgammaCosPi(value) / LgammaSinPi(value);

    private static double GammaLog1p(double value)
    {
        const double ln2High = 6.93147180369123816490e-01;
        const double ln2Low = 1.90821492927058770002e-10;
        var bits = BitConverter.DoubleToUInt64Bits(value);
        var highWord = unchecked((int)(bits >> 32));
        var absoluteHighWord = highWord & 0x7fffffff;
        var exponent = 1;
        var normalizedHighWord = 0;
        var correction = 0d;
        var fraction = 0d;

        if (highWord < 0x3fda827a)
        {
            if (absoluteHighWord >= 0x3ff00000)
            {
                return value == -1 ? double.NegativeInfinity : double.NaN;
            }

            if (absoluteHighWord < 0x3e200000)
            {
                return absoluteHighWord < 0x3c900000
                    ? value
                    : value - ((value * value) * 0.5);
            }

            if (highWord > 0 || highWord <= unchecked((int)0xbfd2bec3))
            {
                exponent = 0;
                fraction = value;
                normalizedHighWord = 1;
            }
        }
        else if (highWord >= 0x7ff00000)
        {
            return value + value;
        }

        if (exponent != 0)
        {
            double normalized;
            if (highWord < 0x43400000)
            {
                normalized = 1 + value;
                normalizedHighWord = unchecked(
                    (int)(BitConverter.DoubleToUInt64Bits(normalized) >> 32));
                exponent = (normalizedHighWord >> 20) - 1023;
                correction = exponent > 0
                    ? 1 - (normalized - value)
                    : value - (normalized - 1);
                correction /= normalized;
            }
            else
            {
                normalized = value;
                normalizedHighWord = unchecked(
                    (int)(BitConverter.DoubleToUInt64Bits(normalized) >> 32));
                exponent = (normalizedHighWord >> 20) - 1023;
            }

            normalizedHighWord &= 0x000fffff;
            if (normalizedHighWord < 0x6a09e)
            {
                normalized = SetGammaHighWord(normalized, normalizedHighWord | 0x3ff00000);
            }
            else
            {
                exponent++;
                normalized = SetGammaHighWord(normalized, normalizedHighWord | 0x3fe00000);
                normalizedHighWord = (0x00100000 - normalizedHighWord) >> 2;
            }

            fraction = normalized - 1;
        }

        var halfSquare = 0.5 * fraction * fraction;
        if (normalizedHighWord == 0)
        {
            if (fraction == 0)
            {
                if (exponent == 0)
                {
                    return 0;
                }

                correction += exponent * ln2Low;
                return (exponent * ln2High) + correction;
            }

            var remainder = halfSquare * (1 - (0.66666666666666666 * fraction));
            return exponent == 0
                ? fraction - remainder
                : (exponent * ln2High) -
                    ((remainder - ((exponent * ln2Low) + correction)) - fraction);
        }

        const double lp1 = 6.666666666666735130e-01;
        const double lp2 = 3.999999999940941908e-01;
        const double lp3 = 2.857142874366239149e-01;
        const double lp4 = 2.222219843214978396e-01;
        const double lp5 = 1.818357216161805012e-01;
        const double lp6 = 1.531383769920937332e-01;
        const double lp7 = 1.479819860511658591e-01;
        var ratio = fraction / (2 + fraction);
        var square = ratio * ratio;
        var first = square * lp1;
        var square2 = square * square;
        var second = lp2 + (square * lp3);
        var square4 = square2 * square2;
        var third = lp4 + (square * lp5);
        var square6 = square4 * square2;
        var fourth = lp6 + (square * lp7);
        var remainderPolynomial =
            (first + (square2 * second)) + (square4 * third) + (square6 * fourth);
        if (exponent == 0)
        {
            return fraction - (halfSquare - (ratio * (halfSquare + remainderPolynomial)));
        }

        return (exponent * ln2High) -
            ((halfSquare -
                ((ratio * (halfSquare + remainderPolynomial)) +
                ((exponent * ln2Low) + correction))) - fraction);
    }

    private static double SetGammaHighWord(double value, int highWord)
    {
        var bits = BitConverter.DoubleToUInt64Bits(value);
        bits = (bits & 0xffffffffUL) | ((ulong)(uint)highWord << 32);
        return BitConverter.UInt64BitsToDouble(bits);
    }

    private static readonly double[] LgammaNegativeZeros =
    [
        -2.4570247382208006, -3.7075610815513266e-17,
        -2.7476826467274127, 9.055340329338315e-17,
        -3.14358088834998, -2.1818179852331714e-16,
        -3.955294284858598, -1.999428391746348e-17,
        -4.039361839740537, 2.1143995503980602e-16,
        -4.991544640560048, 1.5174411760571722e-16,
        -5.0082181683225935, -4.3926353491015815e-17,
        -5.998607480080875, -3.311862478893795e-16,
        -6.001385294453155, 6.415847287933042e-17,
        -6.999801507890638, 1.0550130037400023e-17,
        -7.000198333407325, 2.504354173632409e-16,
        -7.999975197095821, -5.261737128572354e-17,
        -8.000024800270682, -4.354586297860107e-16,
        -8.999997244250977, -2.2185620509727132e-16,
        -9.000002755714823, -9.491348611623208e-17,
        -9.99999972442663, 4.883037618642443e-16,
        -10.000000275573013, -3.4909708332642057e-16,
        -10.99999997494789, 1.9843998306985407e-16,
        -11.000000025052106, -6.850849812286175e-16,
        -11.999999997912324, -1.0020693920103036e-16,
        -12.000000002087676, 1.2222548112048185e-16,
        -12.99999999983941, 6.747262033096337e-16,
        -13.00000000016059, -6.745919484964342e-16,
        -13.99999999998853, 8.094860741926607e-16,
        -14.00000000001147, -8.094853704222662e-16,
        -14.999999999999236, 8.82932241476868e-16,
        -15.000000000000764, -8.829322382710274e-16,
        -15.999999999999952, -1.668613399265054e-16,
        -16.000000000000046, -1.6094954994609367e-15,
        -16.999999999999996, -7.412564244549576e-16,
        -17.000000000000004, 7.412564244550028e-16,
        -18.0, 1.5619206968586233e-16,
        -18.0, -1.561920696858622e-16,
        -19.0, 8.22063524662433e-18,
        -19.0, -8.22063524662433e-18,
        -20.0, 4.110317623312165e-19,
        -20.0, -4.110317623312165e-19,
        -21.0, 1.9572941063391263e-20,
        -21.0, -1.9572941063391263e-20,
        -22.0, 8.896791392450574e-22,
        -22.0, -8.896791392450574e-22,
        -23.0, 3.868170170630684e-23,
        -23.0, -3.868170170630684e-23,
        -24.0, 1.6117375710961184e-24,
        -24.0, -1.6117375710961184e-24,
        -25.0, 6.446950284384474e-26,
        -25.0, -6.446950284384474e-26,
        -26.0, 2.4795962632247976e-27,
        -26.0, -2.4795962632247976e-27,
        -27.0, 9.183689863795546e-29,
        -27.0, -9.183689863795546e-29,
        -28.0, 3.279889237069838e-30,
    ];

    private static readonly double[] LgammaNegativeStirlingCoefficients =
    [
        0.08333333333333333, -0.002777777777777778,
        0.0007936507936507937, -0.0005952380952380953,
        0.0008417508417508417, -0.0019175269175269176,
        0.00641025641025641, -0.029550653594771242,
        0.17964437236883057, -1.3924322169059011,
        13.402864044168393, -156.84828462600203,
    ];

    private static readonly int[] LgammaNegativePolynomialDegrees =
        [10, 11, 12, 13, 13, 12, 11, 11];

    private static readonly int[] LgammaNegativePolynomialEnds =
        [10, 22, 35, 49, 63, 76, 88, 100];

    private static readonly double[] LgammaNegativePolynomial =
    [
        -1.044704781216989, -0.7782305330904057, -0.12024314713662344,
        -0.8886202355427312, -0.0634284310181484, -0.9176282204872979,
        0.03785861085708515, -0.9341733618747365, 0.1520007117912068,
        -0.9697252578163962, 0.2788205613765887, -0.9475560525532617,
        -0.7904814408001736, 0.2257366771626494, -1.0103405242276635,
        0.5895033531088183, -1.3027831864606605, 1.1095669683297373,
        -1.7877052471853463, 1.8431672522093314, -2.5702507974153646,
        2.96272465324091, -3.881766977228748, -0.8430565186694192,
        -0.9007307074535097, 0.6907850597415766, -1.5714574536637218,
        1.830491229184273, -3.033580501343015, 4.126868004210013,
        -6.226302470384837, 8.886294054752328, -13.053005331565737,
        18.88301757578076, -28.307470864453226, 41.414460300822604,
        -0.7160435758996516, -1.1660574285890255, 1.5312814420426584,
        -3.1959170708555393, 5.342476029602517, -9.760480704063543,
        17.17216724690779, -30.661346099348908, 54.41945910419376,
        -96.81174791840648, 171.93252618616302, -305.5345717488169,
        567.9666269469928, -1021.9400375390136, -0.23852637693191414,
        1.8020856790170219, 3.506275674054837, 6.5196196839645,
        11.75663604365609, 21.040338743111032, 37.47998021215248,
        66.69270764646357, 118.5969214540074, 210.87032902786933,
        374.60019140329274, 664.9256300730046, 1237.1418040297096,
        2281.3857338180756, -0.41939003261259933, 1.1595155903726009,
        1.8570110606166135, 2.8831644988756584, 4.2695476278469915,
        6.292870578714175, 9.178552045830783, 13.385745422880152,
        19.476442300735183, 28.33631449884831, 41.203812298055986,
        61.61278288501628, 90.72645743800207, -0.5400649246179795,
        0.8033521909763831, 1.079905352604728, 1.4614073971249786,
        1.8221702551313768, 2.306106582214321, 2.8338217338082505,
        3.5198542506181716, 4.3206153590673, 5.335365175493835,
        6.675528681738811, 8.282177097586464, -0.626080894817651,
        0.5900197543087655, 0.6661716257481942, 0.8240481145327041,
        0.861963874099599, 0.988046418719251, 1.0183290454104954,
        1.1355790463453328, 1.1731378031024609, 1.291562533161419,
        1.3603386607285806, 1.4923120245250903,
    ];


    private static double LgammaApproximationZero(double value)
    {
        var square = value * value;
        var odd = Polynomial(
            square,
            [
                7.72156649015328655494e-02,
                6.73523010531292681824e-02,
                7.38555086081402883957e-03,
                1.19270763183362067845e-03,
                2.20862790713908385557e-04,
                2.52144565451257326939e-05,
            ]);
        var even = square * Polynomial(
            square,
            [
                3.22467033424113591611e-01,
                2.05808084325167332806e-02,
                2.89051383673415629091e-03,
                5.10069792153511336608e-04,
                1.08011567247583939954e-04,
                4.48640949618915160150e-05,
            ]);
        return ((value * odd) + even) - (0.5 * value);
    }

    private static double LgammaApproximationOne(double value)
    {
        var square = value * value;
        var cube = square * value;
        var first = Polynomial(
            cube,
            [
                4.83836122723810047042e-01,
                -3.27885410759859649565e-02,
                6.10053870246291332635e-03,
                -1.40346469989232843813e-03,
                3.15632070903625950361e-04,
            ]);
        var second = Polynomial(
            cube,
            [
                -1.47587722994593911752e-01,
                1.79706750811820387126e-02,
                -3.68452016781138256760e-03,
                8.81081882437654011382e-04,
                -3.12754168375120860518e-04,
            ]);
        var third = Polynomial(
            cube,
            [
                6.46249402391333854778e-02,
                -1.03142241298341437450e-02,
                2.25964780900612472250e-03,
                -5.38595305356740546715e-04,
                3.35529192635519073543e-04,
            ]);
        var polynomial = (square * first) -
            (-3.63867699703950536541e-18 - (cube * (second + (value * third))));
        return -1.21486290535849611461e-01 + polynomial;
    }

    private static double LgammaApproximationTwo(double value)
    {
        var numerator = value * Polynomial(
            value,
            [
                -7.72156649015328655494e-02,
                6.32827064025093366517e-01,
                1.45492250137234768737,
                9.77717527963372745603e-01,
                2.28963728064692451092e-01,
                1.33810918536787660377e-02,
            ]);
        var denominator = 1 + (value * Polynomial(
            value,
            [
                2.45597793713041134822,
                2.12848976379893395361,
                7.69285150456672783825e-01,
                1.04222645593369134254e-01,
                3.21709242282423911810e-03,
            ]));
        return (-0.5 * value) + (numerator / denominator);
    }

    private static (double Fraction, int Exponent) Frexp(double value)
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

    private static double SinPi(double value)
    {
        if (Math.Abs(value) < 0.25)
        {
            return Math.Sin(Math.PI * value);
        }

        var reduced = -value;
        var floor = Math.Floor(reduced);
        int quadrant;
        if (floor != reduced)
        {
            reduced *= 0.5;
            reduced = 2 * (reduced - Math.Floor(reduced));
            quadrant = (int)(reduced * 4);
        }
        else if (Math.Abs(value) >= Math.ScaleB(1, 52))
        {
            reduced = 0;
            quadrant = 0;
        }
        else
        {
            reduced = ((long)reduced) & 1;
            quadrant = (int)reduced << 2;
        }

        var sine = quadrant switch
        {
            0 => Math.Sin(Math.PI * reduced),
            1 or 2 => Math.Cos(Math.PI * (0.5 - reduced)),
            3 or 4 => Math.Sin(Math.PI * (1 - reduced)),
            5 or 6 => -Math.Cos(Math.PI * (reduced - 1.5)),
            _ => Math.Sin(Math.PI * (reduced - 2)),
        };
        return -sine;
    }

    private static bool IsNegative(double value) =>
        BitConverter.DoubleToInt64Bits(value) < 0;

    private static double Polynomial(double value, ReadOnlySpan<double> coefficients)
    {
        var result = coefficients[^1];
        for (var index = coefficients.Length - 2; index >= 0; index--)
        {
            result = coefficients[index] + (value * result);
        }

        return result;
    }

    /*
     * Primary-range exp kernel translated from GNU C Library 2.39
     * sysdeps/ieee754/dbl-64/e_exp.c and e_exp_data.c. GammaPositive supplies
     * only finite arguments inside this kernel's regular range. Evaluation
     * order and table bits are retained exactly.
     */
    private static double GlibcExpPrimary(double value)
    {
        const int tableBits = 7;
        var absoluteTop = (uint)((BitConverter.DoubleToUInt64Bits(value) >> 52) & 0x7ff);
        if (absoluteTop < 0x3c9U)
        {
            return 1 + value;
        }

        if (absoluteTop >= 0x408U)
        {
            // Defensive fallback outside GammaPositive's reachable range.
            return Math.Exp(value);
        }

        var inverseLog2N = BitConverter.UInt64BitsToDouble(0x40671547652b82feUL);
        var negativeLog2HighN = BitConverter.UInt64BitsToDouble(0xbf762e42fefa0000UL);
        var negativeLog2LowN = BitConverter.UInt64BitsToDouble(0xbd0cf79abc9e3b3aUL);
        var coefficient2 = BitConverter.UInt64BitsToDouble(0x3fdffffffffffdbdUL);
        var coefficient3 = BitConverter.UInt64BitsToDouble(0x3fc555555555543cUL);
        var coefficient4 = BitConverter.UInt64BitsToDouble(0x3fa55555cf172b91UL);
        var coefficient5 = BitConverter.UInt64BitsToDouble(0x3f81111167a4d017UL);
        const double shift = 6755399441055744;

        var z = inverseLog2N * value;
        var rounded = z + shift;
        var integerBits = BitConverter.DoubleToUInt64Bits(rounded);
        var integer = rounded - shift;
        var reduced = (value + (integer * negativeLog2HighN)) +
            (integer * negativeLog2LowN);

        var index = 2 * (int)(integerBits % (1UL << tableBits));
        var top = unchecked(integerBits << (52 - tableBits));
        var tail = BitConverter.UInt64BitsToDouble(GlibcExpTable[index]);
        var scaleBits = unchecked(GlibcExpTable[index + 1] + top);
        var scale = BitConverter.UInt64BitsToDouble(scaleBits);

        var square = reduced * reduced;
        var correction = ((tail + reduced) +
            (square * (coefficient2 + (reduced * coefficient3)))) +
            ((square * square) * (coefficient4 + (reduced * coefficient5)));
        return scale + (scale * correction);
    }

    private static readonly ulong[] GlibcExpTable =
    [
        0x0UL, 0x3ff0000000000000UL, 0x3c9b3b4f1a88bf6eUL, 0x3feff63da9fb3335UL, 0xbc7160139cd8dc5dUL, 0x3fefec9a3e778061UL, 0xbc905e7a108766d1UL, 0x3fefe315e86e7f85UL,
        0x3c8cd2523567f613UL, 0x3fefd9b0d3158574UL, 0xbc8bce8023f98efaUL, 0x3fefd06b29ddf6deUL, 0x3c60f74e61e6c861UL, 0x3fefc74518759bc8UL, 0x3c90a3e45b33d399UL, 0x3fefbe3ecac6f383UL,
        0x3c979aa65d837b6dUL, 0x3fefb5586cf9890fUL, 0x3c8eb51a92fdeffcUL, 0x3fefac922b7247f7UL, 0x3c3ebe3d702f9cd1UL, 0x3fefa3ec32d3d1a2UL, 0xbc6a033489906e0bUL, 0x3fef9b66affed31bUL,
        0xbc9556522a2fbd0eUL, 0x3fef9301d0125b51UL, 0xbc5080ef8c4eea55UL, 0x3fef8abdc06c31ccUL, 0xbc91c923b9d5f416UL, 0x3fef829aaea92de0UL, 0x3c80d3e3e95c55afUL, 0x3fef7a98c8a58e51UL,
        0xbc801b15eaa59348UL, 0x3fef72b83c7d517bUL, 0xbc8f1ff055de323dUL, 0x3fef6af9388c8deaUL, 0x3c8b898c3f1353bfUL, 0x3fef635beb6fcb75UL, 0xbc96d99c7611eb26UL, 0x3fef5be084045cd4UL,
        0x3c9aecf73e3a2f60UL, 0x3fef54873168b9aaUL, 0xbc8fe782cb86389dUL, 0x3fef4d5022fcd91dUL, 0x3c8a6f4144a6c38dUL, 0x3fef463b88628cd6UL, 0x3c807a05b0e4047dUL, 0x3fef3f49917ddc96UL,
        0x3c968efde3a8a894UL, 0x3fef387a6e756238UL, 0x3c875e18f274487dUL, 0x3fef31ce4fb2a63fUL, 0x3c80472b981fe7f2UL, 0x3fef2b4565e27cddUL, 0xbc96b87b3f71085eUL, 0x3fef24dfe1f56381UL,
        0x3c82f7e16d09ab31UL, 0x3fef1e9df51fdee1UL, 0xbc3d219b1a6fbffaUL, 0x3fef187fd0dad990UL, 0x3c8b3782720c0ab4UL, 0x3fef1285a6e4030bUL, 0x3c6e149289cecb8fUL, 0x3fef0cafa93e2f56UL,
        0x3c834d754db0abb6UL, 0x3fef06fe0a31b715UL, 0x3c864201e2ac744cUL, 0x3fef0170fc4cd831UL, 0x3c8fdd395dd3f84aUL, 0x3feefc08b26416ffUL, 0xbc86a3803b8e5b04UL, 0x3feef6c55f929ff1UL,
        0xbc924aedcc4b5068UL, 0x3feef1a7373aa9cbUL, 0xbc9907f81b512d8eUL, 0x3feeecae6d05d866UL, 0xbc71d1e83e9436d2UL, 0x3feee7db34e59ff7UL, 0xbc991919b3ce1b15UL, 0x3feee32dc313a8e5UL,
        0x3c859f48a72a4c6dUL, 0x3feedea64c123422UL, 0xbc9312607a28698aUL, 0x3feeda4504ac801cUL, 0xbc58a78f4817895bUL, 0x3feed60a21f72e2aUL, 0xbc7c2c9b67499a1bUL, 0x3feed1f5d950a897UL,
        0x3c4363ed60c2ac11UL, 0x3feece086061892dUL, 0x3c9666093b0664efUL, 0x3feeca41ed1d0057UL, 0x3c6ecce1daa10379UL, 0x3feec6a2b5c13cd0UL, 0x3c93ff8e3f0f1230UL, 0x3feec32af0d7d3deUL,
        0x3c7690cebb7aafb0UL, 0x3feebfdad5362a27UL, 0x3c931dbdeb54e077UL, 0x3feebcb299fddd0dUL, 0xbc8f94340071a38eUL, 0x3feeb9b2769d2ca7UL, 0xbc87deccdc93a349UL, 0x3feeb6daa2cf6642UL,
        0xbc78dec6bd0f385fUL, 0x3feeb42b569d4f82UL, 0xbc861246ec7b5cf6UL, 0x3feeb1a4ca5d920fUL, 0x3c93350518fdd78eUL, 0x3feeaf4736b527daUL, 0x3c7b98b72f8a9b05UL, 0x3feead12d497c7fdUL,
        0x3c9063e1e21c5409UL, 0x3feeab07dd485429UL, 0x3c34c7855019c6eaUL, 0x3feea9268a5946b7UL, 0x3c9432e62b64c035UL, 0x3feea76f15ad2148UL, 0xbc8ce44a6199769fUL, 0x3feea5e1b976dc09UL,
        0xbc8c33c53bef4da8UL, 0x3feea47eb03a5585UL, 0xbc845378892be9aeUL, 0x3feea34634ccc320UL, 0xbc93cedd78565858UL, 0x3feea23882552225UL, 0x3c5710aa807e1964UL, 0x3feea155d44ca973UL,
        0xbc93b3efbf5e2228UL, 0x3feea09e667f3bcdUL, 0xbc6a12ad8734b982UL, 0x3feea012750bdabfUL, 0xbc6367efb86da9eeUL, 0x3fee9fb23c651a2fUL, 0xbc80dc3d54e08851UL, 0x3fee9f7df9519484UL,
        0xbc781f647e5a3ecfUL, 0x3fee9f75e8ec5f74UL, 0xbc86ee4ac08b7db0UL, 0x3fee9f9a48a58174UL, 0xbc8619321e55e68aUL, 0x3fee9feb564267c9UL, 0x3c909ccb5e09d4d3UL, 0x3feea0694fde5d3fUL,
        0xbc7b32dcb94da51dUL, 0x3feea11473eb0187UL, 0x3c94ecfd5467c06bUL, 0x3feea1ed0130c132UL, 0x3c65ebe1abd66c55UL, 0x3feea2f336cf4e62UL, 0xbc88a1c52fb3cf42UL, 0x3feea427543e1a12UL,
        0xbc9369b6f13b3734UL, 0x3feea589994cce13UL, 0xbc805e843a19ff1eUL, 0x3feea71a4623c7adUL, 0xbc94d450d872576eUL, 0x3feea8d99b4492edUL, 0x3c90ad675b0e8a00UL, 0x3feeaac7d98a6699UL,
        0x3c8db72fc1f0eab4UL, 0x3feeace5422aa0dbUL, 0xbc65b6609cc5e7ffUL, 0x3feeaf3216b5448cUL, 0x3c7bf68359f35f44UL, 0x3feeb1ae99157736UL, 0xbc93091fa71e3d83UL, 0x3feeb45b0b91ffc6UL,
        0xbc5da9b88b6c1e29UL, 0x3feeb737b0cdc5e5UL, 0xbc6c23f97c90b959UL, 0x3feeba44cbc8520fUL, 0xbc92434322f4f9aaUL, 0x3feebd829fde4e50UL, 0xbc85ca6cd7668e4bUL, 0x3feec0f170ca07baUL,
        0x3c71affc2b91ce27UL, 0x3feec49182a3f090UL, 0x3c6dd235e10a73bbUL, 0x3feec86319e32323UL, 0xbc87c50422622263UL, 0x3feecc667b5de565UL, 0x3c8b1c86e3e231d5UL, 0x3feed09bec4a2d33UL,
        0xbc91bbd1d3bcbb15UL, 0x3feed503b23e255dUL, 0x3c90cc319cee31d2UL, 0x3feed99e1330b358UL, 0x3c8469846e735ab3UL, 0x3feede6b5579fdbfUL, 0xbc82dfcd978e9db4UL, 0x3feee36bbfd3f37aUL,
        0x3c8c1a7792cb3387UL, 0x3feee89f995ad3adUL, 0xbc907b8f4ad1d9faUL, 0x3feeee07298db666UL, 0xbc55c3d956dcaebaUL, 0x3feef3a2b84f15fbUL, 0xbc90a40e3da6f640UL, 0x3feef9728de5593aUL,
        0xbc68d6f438ad9334UL, 0x3feeff76f2fb5e47UL, 0xbc91eee26b588a35UL, 0x3fef05b030a1064aUL, 0x3c74ffd70a5fddcdUL, 0x3fef0c1e904bc1d2UL, 0xbc91bdfbfa9298acUL, 0x3fef12c25bd71e09UL,
        0x3c736eae30af0cb3UL, 0x3fef199bdd85529cUL, 0x3c8ee3325c9ffd94UL, 0x3fef20ab5fffd07aUL, 0x3c84e08fd10959acUL, 0x3fef27f12e57d14bUL, 0x3c63cdaf384e1a67UL, 0x3fef2f6d9406e7b5UL,
        0x3c676b2c6c921968UL, 0x3fef3720dcef9069UL, 0xbc808a1883ccb5d2UL, 0x3fef3f0b555dc3faUL, 0xbc8fad5d3ffffa6fUL, 0x3fef472d4a07897cUL, 0xbc900dae3875a949UL, 0x3fef4f87080d89f2UL,
        0x3c74a385a63d07a7UL, 0x3fef5818dcfba487UL, 0xbc82919e2040220fUL, 0x3fef60e316c98398UL, 0x3c8e5a50d5c192acUL, 0x3fef69e603db3285UL, 0x3c843a59ac016b4bUL, 0x3fef7321f301b460UL,
        0xbc82d52107b43e1fUL, 0x3fef7c97337b9b5fUL, 0xbc892ab93b470dc9UL, 0x3fef864614f5a129UL, 0x3c74b604603a88d3UL, 0x3fef902ee78b3ff6UL, 0x3c83c5ec519d7271UL, 0x3fef9a51fbc74c83UL,
        0xbc8ff7128fd391f0UL, 0x3fefa4afa2a490daUL, 0xbc8dae98e223747dUL, 0x3fefaf482d8e67f1UL, 0x3c8ec3bc41aa2008UL, 0x3fefba1bee615a27UL, 0x3c842b94c3a9eb32UL, 0x3fefc52b376bba97UL,
        0x3c8a64a931d185eeUL, 0x3fefd0765b6e4540UL, 0xbc8e37bae43be3edUL, 0x3fefdbfdad9cbe14UL, 0x3c77893b4d91cd9dUL, 0x3fefe7c1819e90d8UL, 0x3c5305c14160cc89UL, 0x3feff3c22b8f71f1UL,
    ];
}
