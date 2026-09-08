// DOTNETJQ PORT MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream file: src/jv_alloc.c
// Upstream URL: https://github.com/jqlang/jq/blob/jq-1.8.2/src/jv_alloc.c
// Strategy: PROXY
// Target file: src/DotNetJq/Port/src/jv_alloc.c.cs
//
// Rules:
// - Preserve upstream function names wherever possible.
// - Behavioral compatibility is defined by upstream tests/differential tests.
// - Do not refactor cross-file architecture during compatibility port.
//
// UPSTREAM COMPONENT: malloc/calloc/realloc wrappers and the per-thread OOM callback.
// REPLACEMENT: managed byte arrays, managed strings, and [ThreadStatic] callback state.
// WHY: the CLR owns object allocation and reclamation.
// BEHAVIORAL CONTRACT: guarded helpers invoke the current thread's registered handler on
// OutOfMemoryException; unguarded helpers return null when the CLR reports exhaustion.
// KNOWN DIFFERENCES: free is a no-op, realloc returns a new array, and zero-sized allocations
// are represented by Array.Empty<byte>().
// TESTS COVERING THE SUBSTITUTION: JvRuntimeProxyCompatibilityTests directly covers zero-sized and
// zero-filled allocation, checked unguarded overflow, duplication, reallocation preservation,
// no-op free, and handler registration. Actual CLR OutOfMemoryException injection is intentionally
// excluded because forcing process memory exhaustion is not a deterministic or safe unit test.

namespace DotNetJq.Port;

internal sealed class nomem_handler
{
    internal jv_nomem_handler_f? handler;
    internal object? data;
}

internal static partial class libjq
{
    [ThreadStatic]
    private static nomem_handler? current_nomem_handler;

    internal static void jv_nomem_handler(jv_nomem_handler_f? handler, object? data)
    {
        current_nomem_handler ??= new nomem_handler();
        current_nomem_handler.handler = handler;
        current_nomem_handler.data = data;
    }

    internal static byte[] jv_mem_alloc(int size)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        try
        {
            return size == 0 ? Array.Empty<byte>() : new byte[size];
        }
        catch (OutOfMemoryException)
        {
            memory_exhausted();
            throw;
        }
    }

    internal static byte[]? jv_mem_alloc_unguarded(int size)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        try
        {
            return size == 0 ? Array.Empty<byte>() : new byte[size];
        }
        catch (OutOfMemoryException)
        {
            return null;
        }
    }

    internal static byte[] jv_mem_calloc(int elementCount, int elementSize)
    {
        var length = checked_allocation_size(elementCount, elementSize);
        return jv_mem_alloc(length);
    }

    internal static byte[]? jv_mem_calloc_unguarded(int elementCount, int elementSize)
    {
        int length;
        try
        {
            length = checked_allocation_size(elementCount, elementSize);
        }
        catch (OverflowException)
        {
            return null;
        }

        return jv_mem_alloc_unguarded(length);
    }

    internal static string jv_mem_strdup(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        try
        {
            return new string(value.AsSpan());
        }
        catch (OutOfMemoryException)
        {
            memory_exhausted();
            throw;
        }
    }

    internal static string? jv_mem_strdup_unguarded(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        try
        {
            return new string(value.AsSpan());
        }
        catch (OutOfMemoryException)
        {
            return null;
        }
    }

    internal static void jv_mem_free(object? value)
    {
        _ = value;
    }

    internal static byte[] jv_mem_realloc(byte[]? value, int size)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        try
        {
            if (size == 0)
            {
                return Array.Empty<byte>();
            }

            var result = new byte[size];
            if (value is not null)
            {
                value.AsSpan(0, Math.Min(value.Length, result.Length)).CopyTo(result);
            }

            return result;
        }
        catch (OutOfMemoryException)
        {
            memory_exhausted();
            throw;
        }
    }

    private static int checked_allocation_size(int elementCount, int elementSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(elementCount, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(elementSize, 0);
        return checked(elementCount * elementSize);
    }

    private static void memory_exhausted()
    {
        var state = current_nomem_handler;
        state?.handler?.Invoke(state.data);
    }
}
