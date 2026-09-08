/*
 * DOTNETJQ DEPENDENCY PORT MAP
 * Upstream repository: https://sourceware.org/git/glibc.git
 * Upstream tag: glibc-2.39
 * Upstream tag object: 9609a435f3f9a07c1cf607ad5821b12f735abd69
 * Upstream release commit: ef321e23c20eebc6d6fb4044425c00e6df27b05f
 * Upstream files: sysdeps/ieee754/ldbl-96/gamma_product.c
 * Target file: src/DotNetJq.GlibcCompat/GlibcCompatMath.X87.cs
 * Strategy: PORT
 * Behavioral contract: reproduce the pinned x86-64 glibc build's binary80
 * intermediate evaluation of gamma_product before its binary64 stores.
 * Known differences: none within gamma_product's finite bounded input range.
 * Tests: GammaCompatExactOracleCorpusTests.
 * Modified for DotNetJq: 2026-09-06.
 *
 * Copyright (C) 2013-2024 Free Software Foundation, Inc.
 *
 * This component is free software; you can redistribute it and/or modify it
 * under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation; either version 2.1 of the License, or (at your
 * option) any later version. See COPYING.LIB in this component's source.
 */

using System.Numerics;

namespace DotNetJq.GlibcCompat;

public static partial class GlibcCompatMath
{
    // A managed, gamma-private model of the x87 64-bit-significand format.
    // Values are Significand * 2^Exponent and are rounded to nearest/even
    // after every arithmetic operation, as in the pinned glibc build.
    private readonly record struct GammaBinary80(BigInteger Significand, int Exponent)
    {
        internal static GammaBinary80 FromDouble(double value)
        {
            var bits = BitConverter.DoubleToUInt64Bits(value);
            var negative = (bits >> 63) != 0;
            var exponentBits = (int)((bits >> 52) & 0x7ff);
            var fraction = bits & 0x000fffffffffffffUL;
            if (exponentBits == 0 && fraction == 0)
            {
                return new GammaBinary80(BigInteger.Zero, 0);
            }

            var significand = exponentBits == 0
                ? new BigInteger(fraction)
                : new BigInteger(fraction | (1UL << 52));
            if (negative)
            {
                significand = -significand;
            }

            var exponent = exponentBits == 0
                ? -1074
                : exponentBits - 1023 - 52;
            return new GammaBinary80(significand, exponent).RoundToBinary80();
        }

        internal static GammaBinary80 FromInteger(int value) => FromDouble(value);

        internal GammaBinary80 Add(GammaBinary80 other)
        {
            if (Significand.IsZero)
            {
                return other;
            }

            if (other.Significand.IsZero)
            {
                return this;
            }

            var commonExponent = Math.Min(Exponent, other.Exponent);
            var sum =
                (Significand << (Exponent - commonExponent)) +
                (other.Significand << (other.Exponent - commonExponent));
            return new GammaBinary80(sum, commonExponent).RoundToBinary80();
        }

        internal GammaBinary80 Subtract(GammaBinary80 other) =>
            Add(new GammaBinary80(-other.Significand, other.Exponent));

        internal GammaBinary80 Multiply(GammaBinary80 other) =>
            new GammaBinary80(
                Significand * other.Significand,
                Exponent + other.Exponent).RoundToBinary80();

        internal GammaBinary80 Divide(GammaBinary80 other)
        {
            if (Significand.IsZero)
            {
                return new GammaBinary80(BigInteger.Zero, 0);
            }

            var negative = Significand.Sign * other.Significand.Sign < 0;
            var numerator = BigInteger.Abs(Significand) << 128;
            var denominatorSignificand = BigInteger.Abs(other.Significand);
            var preliminary = numerator / denominatorSignificand;
            var discardedBits = checked((int)preliminary.GetBitLength() - 64);
            var denominator = denominatorSignificand << discardedBits;
            var rounded = RoundPositiveQuotient(numerator, denominator);
            var exponent = Exponent - other.Exponent - 128 + discardedBits;
            if (rounded.GetBitLength() > 64)
            {
                rounded >>= 1;
                exponent++;
            }

            return new GammaBinary80(negative ? -rounded : rounded, exponent);
        }

        internal double ToDouble()
        {
            if (Significand.IsZero)
            {
                return 0;
            }

            var negative = Significand.Sign < 0;
            var significand = BigInteger.Abs(Significand);
            var exponent = Exponent;
            var discardedBits = checked((int)significand.GetBitLength() - 53);
            if (discardedBits > 0)
            {
                var denominator = BigInteger.One << discardedBits;
                significand = RoundPositiveQuotient(significand, denominator);
                exponent += discardedBits;
                if (significand.GetBitLength() > 53)
                {
                    significand >>= 1;
                    exponent++;
                }
            }
            else if (discardedBits < 0)
            {
                significand <<= -discardedBits;
                exponent += discardedBits;
            }

            var unbiasedExponent = exponent + 52;
            var exponentBits = unbiasedExponent + 1023;
            // gamma_product's product and relative error are always normal.
            if (exponentBits is <= 0 or >= 0x7ff)
            {
                throw new ArithmeticException(
                    "gamma_product binary80 conversion left its finite normal range");
            }

            var fraction = checked((ulong)(significand - (BigInteger.One << 52)));
            var bits = ((ulong)exponentBits << 52) | fraction;
            if (negative)
            {
                bits |= 1UL << 63;
            }

            return BitConverter.UInt64BitsToDouble(bits);
        }

        private GammaBinary80 RoundToBinary80()
        {
            if (Significand.IsZero)
            {
                return new GammaBinary80(BigInteger.Zero, 0);
            }

            var negative = Significand.Sign < 0;
            var significand = BigInteger.Abs(Significand);
            var exponent = Exponent;
            var discardedBits = checked((int)significand.GetBitLength() - 64);
            if (discardedBits > 0)
            {
                var denominator = BigInteger.One << discardedBits;
                significand = RoundPositiveQuotient(significand, denominator);
                exponent += discardedBits;
                if (significand.GetBitLength() > 64)
                {
                    significand >>= 1;
                    exponent++;
                }
            }

            return new GammaBinary80(negative ? -significand : significand, exponent);
        }

        private static BigInteger RoundPositiveQuotient(
            BigInteger numerator,
            BigInteger denominator)
        {
            var quotient = BigInteger.DivRem(numerator, denominator, out var remainder);
            var comparison = (remainder << 1).CompareTo(denominator);
            if (comparison > 0 || (comparison == 0 && !quotient.IsEven))
            {
                quotient++;
            }

            return quotient;
        }
    }
}
