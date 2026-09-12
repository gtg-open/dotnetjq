// DOTNETJQ PORT MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream file: src/libm.h
// Upstream URL: https://github.com/jqlang/jq/blob/jq-1.8.2/src/libm.h
// Strategy: PROXY
// Target file: src/DotNetJq/Port/src/libm.glibc-compat.cs
// UPSTREAM COMPONENT: jq-visible gamma-family calls selected by src/libm.h.
// REPLACEMENT: Thin ABI calls to the replaceable DotNetJq.GlibcCompat assembly.
// WHY: isolate LGPL-derived historical rounding behavior from the main DotNetJq assembly.
// BEHAVIORAL CONTRACT: preserve gamma, lgamma, lgamma_r, and tgamma binary64 results and signs.
// KNOWN DIFFERENCES: None known for the covered jq-visible binary64 corpus.
// TESTS COVERING THE SUBSTITUTION: LibmCompatibilityTests and SpecialMathExactOracleCorpusTests.
// No compatibility algorithm or coefficient data is present in this assembly.

using DotNetJq.GlibcCompat;

namespace DotNetJq.Port;

internal static partial class libjq
{
    private static class GammaCompatBridge
    {
        internal static double Gamma(double value) => GlibcCompatMath.Gamma(value);

        internal static double Lgamma(double value) => GlibcCompatMath.Lgamma(value);

        internal static (double Value, int Sign) LgammaR(double value) =>
            GlibcCompatMath.LgammaR(value);

        internal static double Tgamma(double value) => GlibcCompatMath.Tgamma(value);
    }
}
