// DOTNETJQ PORT MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream file: src/util.h
// Upstream URL: https://github.com/jqlang/jq/blob/jq-1.8.2/src/util.h
// Strategy: PORT
// Target file: src/DotNetJq/Port/src/util.h.cs
//
// Rules:
// - Preserve upstream function names wherever possible.
// - Behavioral compatibility is defined by upstream tests/differential tests.
// - Do not refactor cross-file architecture during compatibility port.
//
// Substitutions:
// - ReadOnlySpan<byte> plus an integer offset replaces a returned interior pointer.
// - Stream replaces FILE* for the private byte-writing compatibility helper.
// - Generic comparison helpers replace the GNU-expression MIN/MAX macros.
// - IJqFileSystem is required by jq_realpath so path resolution stays host-controlled.
//
// Known differences:
// - The conditional libc strptime fallback has no POSIX tm ABI; jq-visible strptime behavior
//   is implemented and tested at the mapped builtin compatibility boundary.
// - Windows console-specific WriteFile routing is left to the eventual CLI layer.

namespace DotNetJq.Port;

internal static partial class libjq
{
    internal static T MIN<T>(T left, T right) where T : IComparable<T> =>
        left.CompareTo(right) <= 0 ? left : right;

    internal static T MAX<T>(T left, T right) where T : IComparable<T> =>
        left.CompareTo(right) >= 0 ? left : right;
}
