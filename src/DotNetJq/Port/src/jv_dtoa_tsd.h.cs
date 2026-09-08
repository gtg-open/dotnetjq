// DOTNETJQ PORT MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream file: src/jv_dtoa_tsd.h
// Upstream URL: https://github.com/jqlang/jq/blob/jq-1.8.2/src/jv_dtoa_tsd.h
// Strategy: PROXY
// Target file: src/DotNetJq/Port/src/jv_dtoa_tsd.h.cs
//
// Rules:
// - Preserve upstream function names wherever possible.
// - Behavioral compatibility is defined by upstream tests/differential tests.
// - Do not refactor cross-file architecture during compatibility port.
//
// UPSTREAM COMPONENT: declaration of the per-thread dtoa context accessor.
// REPLACEMENT: ThreadLocal<dtoa_context> behind tsd_dtoa_context_get.
// WHY: .NET directly provides portable per-thread state.
// BEHAVIORAL CONTRACT: one lazily initialized conversion context per managed thread, reused across
// one complete dump and by lazy literal-number binary64 projection.
// KNOWN DIFFERENCES: lifecycle follows managed ThreadLocal disposal rather than a pthread key destructor.
// TESTS COVERING THE SUBSTITUTION: JvRuntimeProxyCompatibilityTests directly verifies per-thread
// identity; JvDtoaDirectProxyCompatibilityTests and exact numeric corpora exercise production use.

namespace DotNetJq.Port;
