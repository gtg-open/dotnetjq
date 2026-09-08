/*
 * DOTNETJQ DEPENDENCY PORT MAP
 * Upstream repository: https://sourceware.org/git/glibc.git
 * Upstream tag: glibc-2.39
 * Upstream tag object: 9609a435f3f9a07c1cf607ad5821b12f735abd69
 * Upstream release commit: ef321e23c20eebc6d6fb4044425c00e6df27b05f
 * Upstream files: sysdeps/ieee754/dbl-64/e_pow.c,
 *   sysdeps/ieee754/dbl-64/e_pow_log_data.c,
 *   sysdeps/ieee754/dbl-64/e_exp2.c, and
 *   sysdeps/ieee754/dbl-64/e_exp_data.c.
 * Target file: src/DotNetJq.GlibcCompat/GlibcCompatMath.Pow.cs
 * Strategy: PORT
 * Behavioral contract: reproduce the positive-finite pow and exp2 paths
 * reached by gamma_positive in the pinned jq-1.8.2 glibc build.
 * Known differences: exceptional/public pow paths outside gamma's bounded
 * input range are intentionally absent; no jq-visible gamma difference is known.
 * Tests: GammaCompatExactOracleCorpusTests and
 * SpecialMathExactOracleCorpusTests.
 * Modified for DotNetJq: 2026-09-06.
 *
 * Copyright (C) 2018-2024 Free Software Foundation, Inc.
 *
 * This component is free software; you can redistribute it and/or modify it
 * under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation; either version 2.1 of the License, or (at your
 * option) any later version. See COPYING.LIB in this component's source.
 */

namespace DotNetJq.GlibcCompat;

public static partial class GlibcCompatMath
{
    private static double GlibcExp2Primary(double value)
    {
        // GammaPositive supplies a finite argument in exp2's ordinary range.
        const double shift = 52776558133248;
        var rounded = value + shift;
        var integerBits = BitConverter.DoubleToUInt64Bits(rounded);
        var integer = rounded - shift;
        var reduced = value - integer;

        var tableIndex = 2 * (int)(integerBits % 128);
        var top = unchecked(integerBits << (52 - 7));
        var tail = BitConverter.UInt64BitsToDouble(GlibcExpTable[tableIndex]);
        var scale = BitConverter.UInt64BitsToDouble(
            unchecked(GlibcExpTable[tableIndex + 1] + top));

        var coefficient1 = BitConverter.UInt64BitsToDouble(0x3fe62e42fefa39efUL);
        var coefficient2 = BitConverter.UInt64BitsToDouble(0x3fcebfbdff82c424UL);
        var coefficient3 = BitConverter.UInt64BitsToDouble(0x3fac6b08d70cf4b5UL);
        var coefficient4 = BitConverter.UInt64BitsToDouble(0x3f83b2abd24650ccUL);
        var coefficient5 = BitConverter.UInt64BitsToDouble(0x3f55d7e09b4e3a84UL);
        var square = reduced * reduced;
        var correction = tail +
            (reduced * coefficient1) +
            (square * (coefficient2 + (reduced * coefficient3))) +
            ((square * square) * (coefficient4 + (reduced * coefficient5)));
        return scale + (scale * correction);
    }

    /*
     * This is the regular positive-finite path needed by GammaPositive. Its
     * caller constrains the base to the pow log kernel's normalized interval
     * and the result to the ordinary exp range, so e_pow's sign, exceptional,
     * overflow, and subnormal-result branches are intentionally not included.
     */
    private static double GlibcPowPositive(double value, double exponent)
    {
        var (high, low) = GlibcPowLogInline(BitConverter.DoubleToUInt64Bits(value));

        var productHigh = exponent * high;
        var productLow = (exponent * low) +
            Math.FusedMultiplyAdd(exponent, high, -productHigh);

        return GlibcPowExpInline(productHigh, productLow);
    }

    private static (double High, double Low) GlibcPowLogInline(ulong valueBits)
    {
        const ulong off = 0x3fe6955500000000UL;
        var temporary = unchecked(valueBits - off);
        var tableIndex = (int)((temporary >> (52 - 7)) % 128);
        var exponent = unchecked((long)temporary) >> 52;
        var normalizedBits = unchecked(
            valueBits - (temporary & (0xfffUL << 52)));
        var normalized = BitConverter.UInt64BitsToDouble(normalizedBits);

        var tableOffset = tableIndex * 3;
        var inverseCenter = BitConverter.UInt64BitsToDouble(
            GlibcPowLogTable[tableOffset]);
        var logCenter = BitConverter.UInt64BitsToDouble(
            GlibcPowLogTable[tableOffset + 1]);
        var logCenterTail = BitConverter.UInt64BitsToDouble(
            GlibcPowLogTable[tableOffset + 2]);
        var reduced = Math.FusedMultiplyAdd(normalized, inverseCenter, -1);

        var exponentValue = (double)exponent;
        var first = (exponentValue * BitConverter.UInt64BitsToDouble(
            0x3fe62e42fefa3800UL)) + logCenter;
        var second = first + reduced;
        var low1 = (exponentValue * BitConverter.UInt64BitsToDouble(
            0x3d2ef35793c76730UL)) + logCenterTail;
        var low2 = (first - second) + reduced;

        var coefficient0 = -0.5;
        var coefficient1 = BitConverter.UInt64BitsToDouble(0xbfe5555555555560UL);
        var coefficient2 = BitConverter.UInt64BitsToDouble(0x3fe0000000000006UL);
        var coefficient3 = BitConverter.UInt64BitsToDouble(0x3fe999999959554eUL);
        var coefficient4 = BitConverter.UInt64BitsToDouble(0xbfe555555529a47aUL);
        var coefficient5 = BitConverter.UInt64BitsToDouble(0xbff2495b9b4845e9UL);
        var coefficient6 = BitConverter.UInt64BitsToDouble(0x3ff0002b8b263fc3UL);

        var ar = coefficient0 * reduced;
        var ar2 = reduced * ar;
        var ar3 = reduced * ar2;
        var high = second + ar2;
        var low3 = Math.FusedMultiplyAdd(ar, reduced, -ar2);
        var low4 = (second - high) + ar2;
        var polynomial = ar3 * (
            coefficient1 +
            (reduced * coefficient2) +
            (ar2 * (
                coefficient3 +
                (reduced * coefficient4) +
                (ar2 * (coefficient5 + (reduced * coefficient6))))));
        var low = low1 + low2 + low3 + low4 + polynomial;
        var result = high + low;
        return (result, (high - result) + low);
    }

    private static double GlibcPowExpInline(double value, double tailValue)
    {
        const double shift = 6755399441055744;
        var inverseLog2N = BitConverter.UInt64BitsToDouble(0x40671547652b82feUL);
        var negativeLog2HighN = BitConverter.UInt64BitsToDouble(0xbf762e42fefa0000UL);
        var negativeLog2LowN = BitConverter.UInt64BitsToDouble(0xbd0cf79abc9e3b3aUL);
        var coefficient2 = BitConverter.UInt64BitsToDouble(0x3fdffffffffffdbdUL);
        var coefficient3 = BitConverter.UInt64BitsToDouble(0x3fc555555555543cUL);
        var coefficient4 = BitConverter.UInt64BitsToDouble(0x3fa55555cf172b91UL);
        var coefficient5 = BitConverter.UInt64BitsToDouble(0x3f81111167a4d017UL);

        var z = inverseLog2N * value;
        var rounded = z + shift;
        var integerBits = BitConverter.DoubleToUInt64Bits(rounded);
        var integer = rounded - shift;
        var reduced = (value + (integer * negativeLog2HighN)) +
            (integer * negativeLog2LowN);
        reduced += tailValue;

        var tableIndex = 2 * (int)(integerBits % 128);
        var top = unchecked(integerBits << (52 - 7));
        var scaleTail = BitConverter.UInt64BitsToDouble(GlibcExpTable[tableIndex]);
        var scale = BitConverter.UInt64BitsToDouble(
            unchecked(GlibcExpTable[tableIndex + 1] + top));
        var square = reduced * reduced;
        var lowerPolynomial = Math.FusedMultiplyAdd(
            reduced,
            coefficient3,
            coefficient2);
        var upperPolynomial = Math.FusedMultiplyAdd(
            reduced,
            coefficient5,
            coefficient4);
        var correction = Math.FusedMultiplyAdd(
            square * square,
            upperPolynomial,
            Math.FusedMultiplyAdd(
                square,
                lowerPolynomial,
                scaleTail + reduced));
        return Math.FusedMultiplyAdd(scale, correction, scale);
    }

    private static readonly ulong[] GlibcPowLogTable =
    [
        0x3ff6a00000000000UL, 0xbfd62c82f2b9c800UL, 0x3cfab42428375680UL,
        0x3ff6800000000000UL, 0xbfd5d1bdbf580800UL, 0xbd1ca508d8e0f720UL,
        0x3ff6600000000000UL, 0xbfd5767717455800UL, 0xbd2362a4d5b6506dUL,
        0x3ff6400000000000UL, 0xbfd51aad872df800UL, 0xbce684e49eb067d5UL,
        0x3ff6200000000000UL, 0xbfd4be5f95777800UL, 0xbd041b6993293ee0UL,
        0x3ff6000000000000UL, 0xbfd4618bc21c6000UL, 0x3d13d82f484c84ccUL,
        0x3ff5e00000000000UL, 0xbfd404308686a800UL, 0x3cdc42f3ed820b3aUL,
        0x3ff5c00000000000UL, 0xbfd3a64c55694800UL, 0x3d20b1c686519460UL,
        0x3ff5a00000000000UL, 0xbfd347dd9a988000UL, 0x3d25594dd4c58092UL,
        0x3ff5800000000000UL, 0xbfd2e8e2bae12000UL, 0x3d267b1e99b72bd8UL,
        0x3ff5600000000000UL, 0xbfd2895a13de8800UL, 0x3d15ca14b6cfb03fUL,
        0x3ff5600000000000UL, 0xbfd2895a13de8800UL, 0x3d15ca14b6cfb03fUL,
        0x3ff5400000000000UL, 0xbfd22941fbcf7800UL, 0xbd165a242853da76UL,
        0x3ff5200000000000UL, 0xbfd1c898c1699800UL, 0xbd1fafbc68e75404UL,
        0x3ff5000000000000UL, 0xbfd1675cababa800UL, 0x3d1f1fc63382a8f0UL,
        0x3ff4e00000000000UL, 0xbfd1058bf9ae4800UL, 0xbd26a8c4fd055a66UL,
        0x3ff4c00000000000UL, 0xbfd0a324e2739000UL, 0xbd0c6bee7ef4030eUL,
        0x3ff4a00000000000UL, 0xbfd0402594b4d000UL, 0xbcf036b89ef42d7fUL,
        0x3ff4a00000000000UL, 0xbfd0402594b4d000UL, 0xbcf036b89ef42d7fUL,
        0x3ff4800000000000UL, 0xbfcfb9186d5e4000UL, 0x3d0d572aab993c87UL,
        0x3ff4600000000000UL, 0xbfcef0adcbdc6000UL, 0x3d2b26b79c86af24UL,
        0x3ff4400000000000UL, 0xbfce27076e2af000UL, 0xbd172f4f543fff10UL,
        0x3ff4200000000000UL, 0xbfcd5c216b4fc000UL, 0x3d21ba91bbca681bUL,
        0x3ff4000000000000UL, 0xbfcc8ff7c79aa000UL, 0x3d27794f689f8434UL,
        0x3ff4000000000000UL, 0xbfcc8ff7c79aa000UL, 0x3d27794f689f8434UL,
        0x3ff3e00000000000UL, 0xbfcbc286742d9000UL, 0x3d194eb0318bb78fUL,
        0x3ff3c00000000000UL, 0xbfcaf3c94e80c000UL, 0x3cba4e633fcd9066UL,
        0x3ff3a00000000000UL, 0xbfca23bc1fe2b000UL, 0xbd258c64dc46c1eaUL,
        0x3ff3a00000000000UL, 0xbfca23bc1fe2b000UL, 0xbd258c64dc46c1eaUL,
        0x3ff3800000000000UL, 0xbfc9525a9cf45000UL, 0xbd2ad1d904c1d4e3UL,
        0x3ff3600000000000UL, 0xbfc87fa06520d000UL, 0x3d2bbdbf7fdbfa09UL,
        0x3ff3400000000000UL, 0xbfc7ab890210e000UL, 0x3d2bdb9072534a58UL,
        0x3ff3400000000000UL, 0xbfc7ab890210e000UL, 0x3d2bdb9072534a58UL,
        0x3ff3200000000000UL, 0xbfc6d60fe719d000UL, 0xbd10e46aa3b2e266UL,
        0x3ff3000000000000UL, 0xbfc5ff3070a79000UL, 0xbd1e9e439f105039UL,
        0x3ff3000000000000UL, 0xbfc5ff3070a79000UL, 0xbd1e9e439f105039UL,
        0x3ff2e00000000000UL, 0xbfc526e5e3a1b000UL, 0xbd20de8b90075b8fUL,
        0x3ff2c00000000000UL, 0xbfc44d2b6ccb8000UL, 0x3d170cc16135783cUL,
        0x3ff2c00000000000UL, 0xbfc44d2b6ccb8000UL, 0x3d170cc16135783cUL,
        0x3ff2a00000000000UL, 0xbfc371fc201e9000UL, 0x3cf178864d27543aUL,
        0x3ff2800000000000UL, 0xbfc29552f81ff000UL, 0xbd248d301771c408UL,
        0x3ff2600000000000UL, 0xbfc1b72ad52f6000UL, 0xbd2e80a41811a396UL,
        0x3ff2600000000000UL, 0xbfc1b72ad52f6000UL, 0xbd2e80a41811a396UL,
        0x3ff2400000000000UL, 0xbfc0d77e7cd09000UL, 0x3d0a699688e85bf4UL,
        0x3ff2400000000000UL, 0xbfc0d77e7cd09000UL, 0x3d0a699688e85bf4UL,
        0x3ff2200000000000UL, 0xbfbfec9131dbe000UL, 0xbd2575545ca333f2UL,
        0x3ff2000000000000UL, 0xbfbe27076e2b0000UL, 0x3d2a342c2af0003cUL,
        0x3ff2000000000000UL, 0xbfbe27076e2b0000UL, 0x3d2a342c2af0003cUL,
        0x3ff1e00000000000UL, 0xbfbc5e548f5bc000UL, 0xbd1d0c57585fbe06UL,
        0x3ff1c00000000000UL, 0xbfba926d3a4ae000UL, 0x3d253935e85baac8UL,
        0x3ff1c00000000000UL, 0xbfba926d3a4ae000UL, 0x3d253935e85baac8UL,
        0x3ff1a00000000000UL, 0xbfb8c345d631a000UL, 0x3d137c294d2f5668UL,
        0x3ff1a00000000000UL, 0xbfb8c345d631a000UL, 0x3d137c294d2f5668UL,
        0x3ff1800000000000UL, 0xbfb6f0d28ae56000UL, 0xbd269737c93373daUL,
        0x3ff1600000000000UL, 0xbfb51b073f062000UL, 0x3d1f025b61c65e57UL,
        0x3ff1600000000000UL, 0xbfb51b073f062000UL, 0x3d1f025b61c65e57UL,
        0x3ff1400000000000UL, 0xbfb341d7961be000UL, 0x3d2c5edaccf913dfUL,
        0x3ff1400000000000UL, 0xbfb341d7961be000UL, 0x3d2c5edaccf913dfUL,
        0x3ff1200000000000UL, 0xbfb16536eea38000UL, 0x3d147c5e768fa309UL,
        0x3ff1000000000000UL, 0xbfaf0a30c0118000UL, 0x3d2d599e83368e91UL,
        0x3ff1000000000000UL, 0xbfaf0a30c0118000UL, 0x3d2d599e83368e91UL,
        0x3ff0e00000000000UL, 0xbfab42dd71198000UL, 0x3d1c827ae5d6704cUL,
        0x3ff0e00000000000UL, 0xbfab42dd71198000UL, 0x3d1c827ae5d6704cUL,
        0x3ff0c00000000000UL, 0xbfa77458f632c000UL, 0xbd2cfc4634f2a1eeUL,
        0x3ff0c00000000000UL, 0xbfa77458f632c000UL, 0xbd2cfc4634f2a1eeUL,
        0x3ff0a00000000000UL, 0xbfa39e87b9fec000UL, 0x3cf502b7f526feaaUL,
        0x3ff0a00000000000UL, 0xbfa39e87b9fec000UL, 0x3cf502b7f526feaaUL,
        0x3ff0800000000000UL, 0xbf9f829b0e780000UL, 0xbd2980267c7e09e4UL,
        0x3ff0800000000000UL, 0xbf9f829b0e780000UL, 0xbd2980267c7e09e4UL,
        0x3ff0600000000000UL, 0xbf97b91b07d58000UL, 0xbd288d5493faa639UL,
        0x3ff0400000000000UL, 0xbf8fc0a8b0fc0000UL, 0xbcdf1e7cf6d3a69cUL,
        0x3ff0400000000000UL, 0xbf8fc0a8b0fc0000UL, 0xbcdf1e7cf6d3a69cUL,
        0x3ff0200000000000UL, 0xbf7fe02a6b100000UL, 0xbd19e23f0dda40e4UL,
        0x3ff0200000000000UL, 0xbf7fe02a6b100000UL, 0xbd19e23f0dda40e4UL,
        0x3ff0000000000000UL, 0x0000000000000000UL, 0x0000000000000000UL,
        0x3ff0000000000000UL, 0x0000000000000000UL, 0x0000000000000000UL,
        0x3fefc00000000000UL, 0x3f80101575890000UL, 0xbd10c76b999d2be8UL,
        0x3fef800000000000UL, 0x3f90205658938000UL, 0xbd23dc5b06e2f7d2UL,
        0x3fef400000000000UL, 0x3f98492528c90000UL, 0xbd2aa0ba325a0c34UL,
        0x3fef000000000000UL, 0x3fa0415d89e74000UL, 0x3d0111c05cf1d753UL,
        0x3feec00000000000UL, 0x3fa466aed42e0000UL, 0xbd2c167375bdfd28UL,
        0x3fee800000000000UL, 0x3fa894aa149fc000UL, 0xbd197995d05a267dUL,
        0x3fee400000000000UL, 0x3faccb73cdddc000UL, 0xbd1a68f247d82807UL,
        0x3fee200000000000UL, 0x3faeea31c006c000UL, 0xbd0e113e4fc93b7bUL,
        0x3fede00000000000UL, 0x3fb1973bd1466000UL, 0xbd25325d560d9e9bUL,
        0x3feda00000000000UL, 0x3fb3bdf5a7d1e000UL, 0x3d2cc85ea5db4ed7UL,
        0x3fed600000000000UL, 0x3fb5e95a4d97a000UL, 0xbd2c69063c5d1d1eUL,
        0x3fed400000000000UL, 0x3fb700d30aeac000UL, 0x3cec1e8da99ded32UL,
        0x3fed000000000000UL, 0x3fb9335e5d594000UL, 0x3d23115c3abd47daUL,
        0x3fecc00000000000UL, 0x3fbb6ac88dad6000UL, 0xbd1390802bf768e5UL,
        0x3feca00000000000UL, 0x3fbc885801bc4000UL, 0x3d2646d1c65aacd3UL,
        0x3fec600000000000UL, 0x3fbec739830a2000UL, 0xbd2dc068afe645e0UL,
        0x3fec400000000000UL, 0x3fbfe89139dbe000UL, 0xbd2534d64fa10afdUL,
        0x3fec000000000000UL, 0x3fc1178e8227e000UL, 0x3d21ef78ce2d07f2UL,
        0x3febe00000000000UL, 0x3fc1aa2b7e23f000UL, 0x3d2ca78e44389934UL,
        0x3feba00000000000UL, 0x3fc2d1610c868000UL, 0x3d039d6ccb81b4a1UL,
        0x3feb800000000000UL, 0x3fc365fcb0159000UL, 0x3cc62fa8234b7289UL,
        0x3feb400000000000UL, 0x3fc4913d8333b000UL, 0x3d25837954fdb678UL,
        0x3feb200000000000UL, 0x3fc527e5e4a1b000UL, 0x3d2633e8e5697dc7UL,
        0x3feae00000000000UL, 0x3fc6574ebe8c1000UL, 0x3d19cf8b2c3c2e78UL,
        0x3feac00000000000UL, 0x3fc6f0128b757000UL, 0xbd25118de59c21e1UL,
        0x3feaa00000000000UL, 0x3fc7898d85445000UL, 0xbd1c661070914305UL,
        0x3fea600000000000UL, 0x3fc8beafeb390000UL, 0xbd073d54aae92cd1UL,
        0x3fea400000000000UL, 0x3fc95a5adcf70000UL, 0x3d07f22858a0ff6fUL,
        0x3fea000000000000UL, 0x3fca93ed3c8ae000UL, 0xbd28724350562169UL,
        0x3fe9e00000000000UL, 0x3fcb31d8575bd000UL, 0xbd0c358d4eace1aaUL,
        0x3fe9c00000000000UL, 0x3fcbd087383be000UL, 0xbd2d4bc4595412b6UL,
        0x3fe9a00000000000UL, 0x3fcc6ffbc6f01000UL, 0xbcf1ec72c5962bd2UL,
        0x3fe9600000000000UL, 0x3fcdb13db0d49000UL, 0xbd2aff2af715b035UL,
        0x3fe9400000000000UL, 0x3fce530effe71000UL, 0x3cc212276041f430UL,
        0x3fe9200000000000UL, 0x3fcef5ade4dd0000UL, 0xbcca211565bb8e11UL,
        0x3fe9000000000000UL, 0x3fcf991c6cb3b000UL, 0x3d1bcbecca0cdf30UL,
        0x3fe8c00000000000UL, 0x3fd07138604d5800UL, 0x3cf89cdb16ed4e91UL,
        0x3fe8a00000000000UL, 0x3fd0c42d67616000UL, 0x3d27188b163ceae9UL,
        0x3fe8800000000000UL, 0x3fd1178e8227e800UL, 0xbd2c210e63a5f01cUL,
        0x3fe8600000000000UL, 0x3fd16b5ccbacf800UL, 0x3d2b9acdf7a51681UL,
        0x3fe8400000000000UL, 0x3fd1bf99635a6800UL, 0x3d2ca6ed5147bdb7UL,
        0x3fe8200000000000UL, 0x3fd214456d0eb800UL, 0x3d0a87deba46baeaUL,
        0x3fe7e00000000000UL, 0x3fd2bef07cdc9000UL, 0x3d2a9cfa4a5004f4UL,
        0x3fe7c00000000000UL, 0x3fd314f1e1d36000UL, 0xbd28e27ad3213cb8UL,
        0x3fe7a00000000000UL, 0x3fd36b6776be1000UL, 0x3d116ecdb0f177c8UL,
        0x3fe7800000000000UL, 0x3fd3c25277333000UL, 0x3d183b54b606bd5cUL,
        0x3fe7600000000000UL, 0x3fd419b423d5e800UL, 0x3d08e436ec90e09dUL,
        0x3fe7400000000000UL, 0x3fd4718dc271c800UL, 0xbd2f27ce0967d675UL,
        0x3fe7200000000000UL, 0x3fd4c9e09e173000UL, 0xbd2e20891b0ad8a4UL,
        0x3fe7000000000000UL, 0x3fd522ae0738a000UL, 0x3d2ebe708164c759UL,
        0x3fe6e00000000000UL, 0x3fd57bf753c8d000UL, 0x3d1fadedee5d40efUL,
        0x3fe6c00000000000UL, 0x3fd5d5bddf596000UL, 0xbd0a0b2a08a465dcUL,
    ];
}
