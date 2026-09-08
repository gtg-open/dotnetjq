// DOTNETJQ PORT MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream file: vendor/decNumber/decNumberLocal.h
// Upstream URL: https://github.com/jqlang/jq/blob/jq-1.8.2/vendor/decNumber/decNumberLocal.h
// Strategy: PROXY
// Target file: src/DotNetJq/Port/vendor/decNumber/decNumberLocal.h.cs
// Upstream copyright notice: Copyright (c) IBM Corporation, 2000, 2010. All rights reserved.
// Upstream license: ICU License -- ICU 1.8.1 and later; complete terms are in /COPYING.jq.
// UPSTREAM COMPONENT: IBM decNumber internal constants, sizing helpers, and arithmetic macros consumed by jq.
// REPLACEMENT: managed arithmetic helpers; endian and allocation macros are unnecessary.
// WHY: managed storage removes native buffer layout while mapped callers still require upstream constants.
// BEHAVIORAL CONTRACT: preserve decNumber version, exponent/digit limits, unit sizing, and rounding helpers.
// KNOWN DIFFERENCES: internal C unit-buffer tuning constants have no managed equivalent.
// TESTS COVERING THE SUBSTITUTION: NumericCompatibilityTests, NumericCompatibilityRound2Tests, and build coverage.

namespace DotNetJq.Port;

internal static partial class libjq
{
    internal const string DECVERSION = "decNumber 3.68";
    internal const int DECBUFFER = 36;
    internal const int DECNUMMAXP = 999_999_999;
    internal const int DECNUMMAXE = 999_999_999;
    internal const int DECNUMMINE = -999_999_999;
    internal const int DECDPUNMAX = 999;
    internal const int DECMAXD2U = 49;

    internal static int D2U(int digits) => (digits + DECDPUN - 1) / DECDPUN;

    internal static int ROUNDUP(int value, int multiple) =>
        checked(((value + multiple - 1) / multiple) * multiple);

    internal static int ROUNDDOWN(int value, int multiple) => value / multiple * multiple;
}
