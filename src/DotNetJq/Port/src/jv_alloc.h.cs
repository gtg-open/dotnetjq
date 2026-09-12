// DOTNETJQ PORT MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream file: src/jv_alloc.h
// Upstream URL: https://github.com/jqlang/jq/blob/jq-1.8.2/src/jv_alloc.h
// Strategy: PROXY
// Target file: src/DotNetJq/Port/src/jv_alloc.h.cs
//
// Rules:
// - Preserve upstream function names wherever possible.
// - Behavioral compatibility is defined by upstream tests/differential tests.
// - Do not refactor cross-file architecture during compatibility port.
//
// UPSTREAM COMPONENT: jq allocation declarations.
// REPLACEMENT: managed arrays, strings, and the .NET garbage collector.
// WHY: managed code cannot expose malloc-compatible ownership safely.
// BEHAVIORAL CONTRACT: allocation helpers either return a managed value or invoke the
// registered thread-local no-memory handler before propagating OutOfMemoryException.
// KNOWN DIFFERENCES: pointer identity, manual lifetime, and native heap layout are not exposed.
// TESTS COVERING THE SUBSTITUTION: JvRuntimeProxyCompatibilityTests directly covers the managed
// allocation/delegate declaration contract. Actual CLR memory exhaustion is an explicit runtime
// boundary and is not forced by the deterministic test suite.

namespace DotNetJq.Port;

internal delegate void jv_nomem_handler_f(object? data);
