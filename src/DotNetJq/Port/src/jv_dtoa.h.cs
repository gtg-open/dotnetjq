// DOTNETJQ PORT MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream file: src/jv_dtoa.h
// Upstream URL: https://github.com/jqlang/jq/blob/jq-1.8.2/src/jv_dtoa.h
// Strategy: PROXY
// Target file: src/DotNetJq/Port/src/jv_dtoa.h.cs
//
// Rules:
// - Preserve upstream function names wherever possible.
// - Behavioral compatibility is defined by upstream tests/differential tests.
// - Do not refactor cross-file architecture during compatibility port.
//
// UPSTREAM COMPONENT: David Gay dtoa/strtod declarations and allocation context.
// REPLACEMENT: invariant .NET binary64 parsing/formatting plus jq's g_fmt layout rules.
// WHY: the CLR already provides correctly rounded binary64 conversion without native buffers.
// BEHAVIORAL CONTRACT: expose jq-shaped conversion entry points, invariant decimal syntax,
// consumed-character reporting, sign-bit preservation, and jq exponent/fixed notation thresholds.
// Production literal projection and printing call jvp_strtod/jvp_dtoa_fmt through the mapped
// per-thread context, matching jq's mode-0 call topology.
// KNOWN DIFFERENCES: production jq calls mode 0 through jvp_dtoa_fmt, which is the parity scope.
// Modes 1 and 4-9 remain compatibility conveniences but do not claim every native bigint boundary;
// callers receive managed strings rather than owned native char buffers.
// TESTS COVERING THE SUBSTITUTION: JvDtoaDirectProxyCompatibilityTests, NumericCompatibilityTests,
// NumericCompatibilityRound2Tests, FdlibmElementaryExactOracleCorpusTests, and unchanged jq.test.

namespace DotNetJq.Port;

internal sealed class dtoa_context
{
    internal bool IsInitialized { get; set; }
}

internal static partial class libjq
{
    internal const int Kmax = 7;
    internal const int JVP_DTOA_FMT_MAX_LEN = 64;
}
