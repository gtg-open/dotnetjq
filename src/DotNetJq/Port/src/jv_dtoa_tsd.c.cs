// DOTNETJQ PORT MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream file: src/jv_dtoa_tsd.c
// Upstream URL: https://github.com/jqlang/jq/blob/jq-1.8.2/src/jv_dtoa_tsd.c
// Strategy: PROXY
// Target file: src/DotNetJq/Port/src/jv_dtoa_tsd.c.cs
//
// Rules:
// - Preserve upstream function names wherever possible.
// - Behavioral compatibility is defined by upstream tests/differential tests.
// - Do not refactor cross-file architecture during compatibility port.
//
// UPSTREAM COMPONENT: pthread-key allocation and teardown of dtoa_context.
// REPLACEMENT: ThreadLocal<dtoa_context> with lazy managed initialization.
// WHY: the CLR owns thread-local object storage and object reclamation.
// BEHAVIORAL CONTRACT: tsd_dtoa_context_get returns a stable context within one managed thread.
// jv_print obtains it once per complete dump, while lazy literal-number projection uses it for
// the jq-shaped jvp_strtod bridge.
// KNOWN DIFFERENCES: explicit process-exit cleanup is unnecessary for the context's managed state.
// TESTS COVERING THE SUBSTITUTION: JvRuntimeProxyCompatibilityTests verifies stable same-thread and
// distinct cross-thread context identity; JvDtoaDirectProxyCompatibilityTests verifies production
// printer reachability; reduced-literal and exact numeric corpora exercise production conversion.

using System.Threading;

namespace DotNetJq.Port;

internal static partial class libjq
{
    private static readonly ThreadLocal<dtoa_context> dtoa_context_by_thread =
        new(create_dtoa_context, trackAllValues: false);

    internal static dtoa_context tsd_dtoa_context_get() =>
        dtoa_context_by_thread.Value ?? throw new InvalidOperationException("Could not initialize dtoa context.");

    private static dtoa_context create_dtoa_context()
    {
        var context = new dtoa_context();
        jvp_dtoa_context_init(context);
        return context;
    }
}
