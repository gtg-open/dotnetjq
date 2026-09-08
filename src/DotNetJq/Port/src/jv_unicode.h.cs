// DOTNETJQ PORT MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream file: src/jv_unicode.h
// Upstream URL: https://github.com/jqlang/jq/blob/jq-1.8.2/src/jv_unicode.h
// Strategy: PROXY
// Target file: src/DotNetJq/Port/src/jv_unicode.h.cs
// UPSTREAM COMPONENT: jq's public UTF-8 helper declarations and Unicode constants.
// REPLACEMENT: ReadOnlySpan<byte>, Span<byte>, nullable offsets, and .NET Rune constants.
// WHY: managed span contracts replace unsafe C pointer ranges while retaining jq-shaped names.
// BEHAVIORAL CONTRACT: expose the constants and signatures required by the jv Unicode compatibility layer.
// KNOWN DIFFERENCES: invalid pointer ranges fail with managed argument exceptions instead of assertions.
// TESTS COVERING THE SUBSTITUTION: UnicodeCompatibilityTests and build-time signature coverage.

namespace DotNetJq.Port;

internal static partial class libjq
{
    internal const int JVP_UTF8_INVALID_CODEPOINT = -1;
    internal const int JVP_UNICODE_REPLACEMENT_CODEPOINT = 0xFFFD;
    internal const int JVP_UNICODE_MAX_CODEPOINT = 0x10FFFF;
}
