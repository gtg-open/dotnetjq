// DOTNETJQ PORT MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream file: src/builtin.h
// Upstream URL: https://github.com/jqlang/jq/blob/jq-1.8.2/src/builtin.h
// Strategy: PORT
// Target file: src/DotNetJq/Port/src/builtin.h.cs
// Substitutions: declarations are represented by the partial libjq type. The
// source-shaped block binder, bytecoded primitives, C function table, and
// embedded builtin.jq binding live in builtin.c.cs, matching the upstream
// translation-unit split as closely as C# permits.

namespace DotNetJq.Port;

internal static partial class libjq
{
    // The consuming binop_* definitions and direct block builtins_bind
    // definition live beside jq's function_list in builtin.c.cs, matching
    // upstream.
}
