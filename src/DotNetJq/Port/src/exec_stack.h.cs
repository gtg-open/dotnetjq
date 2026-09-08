// DOTNETJQ PORT MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream file: src/exec_stack.h
// Upstream URL:
//   https://github.com/jqlang/jq/blob/jq-1.8.2/src/exec_stack.h
// Strategy: PORT
// Target file: src/DotNetJq/Port/src/exec_stack.h.cs
//
// Rules:
// - Preserve upstream function names wherever possible.
// - Behavioral compatibility is defined by upstream tests/differential tests.
// - Do not refactor cross-file architecture during compatibility port.
//
// Substitutions:
// - A managed byte array replaces the realloc/memmove-owned byte region.
// - Block links and sizes are tracked as managed metadata instead of unaligned pointer writes.
//
// Known differences:
// - stack_block returns Memory<byte> rather than void*; stack pointers retain upstream negative-offset semantics.

#pragma warning disable CS8981 // jq compatibility symbols intentionally retain upstream lowercase names.

namespace DotNetJq.Port;

internal readonly record struct stack_ptr(int value)
{
    public static implicit operator int(stack_ptr pointer) => pointer.value;

    public static implicit operator stack_ptr(int value) => new(value);

    public override string ToString() => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

internal sealed class stack
{
    // This array represents the allocated region; its end is upstream mem_end.
    internal byte[]? mem_end { get; set; }

    internal stack_ptr bound { get; set; }

    // Zero means empty, exactly as upstream.
    internal stack_ptr limit { get; set; }

    internal Dictionary<int, stack_ptr> block_next { get; } = [];

    internal Dictionary<int, int> block_size { get; } = [];

    // Managed payload stored at the same logical block address.  Native jq
    // writes jv, frame, and forkpoint values directly into the byte region.
    // A CLR reference cannot be safely encoded into movable raw bytes, so the
    // port keeps the allocation/address algorithm above and associates the
    // typed payload with that address.  The lifetime rule is unchanged: a
    // payload is removed only when stack_pop_block() advances the physical
    // limit, while a pop from a saved branch leaves the shared block intact.
    internal Dictionary<int, object> block_value { get; } = [];
}

internal static partial class libjq
{
    // CLR references and the upstream alignment union both require pointer-size alignment.
    internal static int ALIGNMENT { get; } = IntPtr.Size;

    internal static int align_round_up(int sz)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sz);

        return checked(((sz + (ALIGNMENT - 1)) / ALIGNMENT) * ALIGNMENT);
    }

    internal static void stack_init(stack s)
    {
        ArgumentNullException.ThrowIfNull(s);
        s.mem_end = null;
        s.bound = ALIGNMENT;
        s.limit = 0;
        s.block_next.Clear();
        s.block_size.Clear();
        s.block_value.Clear();
    }

    internal static void stack_reset(stack s)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (s.limit != (stack_ptr)0)
        {
            throw new InvalidOperationException("stack freed while not empty");
        }

        stack_init(s);
    }

    internal static bool stack_pop_will_free(stack s, stack_ptr p)
    {
        ArgumentNullException.ThrowIfNull(s);
        return p == s.limit;
    }

    internal static Memory<byte> stack_block(stack s, stack_ptr p)
    {
        ArgumentNullException.ThrowIfNull(s);
        var memory = s.mem_end ?? throw new InvalidOperationException("The stack has not allocated storage.");
        if (!s.block_size.TryGetValue(p, out var size))
        {
            throw new ArgumentOutOfRangeException(nameof(p));
        }

        var start = checked(memory.Length + (int)p);
        if (start < 0 || start > memory.Length - size)
        {
            throw new InvalidOperationException("The stack pointer is outside the allocated region.");
        }

        return memory.AsMemory(start, size);
    }

    internal static stack_ptr stack_block_next(stack s, stack_ptr p)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s.block_next.TryGetValue(p, out var next)
            ? next
            : throw new ArgumentOutOfRangeException(nameof(p));
    }

    internal static void stack_reallocate(stack s, int sz)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentOutOfRangeException.ThrowIfNegative(sz);

        var oldMemory = s.mem_end;
        var oldMemoryLength = oldMemory?.Length ?? 0;
        var newMemoryLength = align_round_up(checked((oldMemoryLength + sz + 256) * 2));

        var newMemory = GC.AllocateUninitializedArray<byte>(newMemoryLength);
        if (oldMemory is not null)
        {
            oldMemory.CopyTo(newMemory, newMemoryLength - oldMemoryLength);
        }

        s.mem_end = newMemory;
        s.bound = -(newMemoryLength - ALIGNMENT);
    }

    internal static stack_ptr stack_push_block(stack s, stack_ptr p, int sz)
    {
        ArgumentNullException.ThrowIfNull(s);
        var alignedSize = align_round_up(sz);
        var allocationSize = checked(alignedSize + ALIGNMENT);
        stack_ptr result = checked((int)s.limit - allocationSize);
        if (result < (int)s.bound)
        {
            stack_reallocate(s, allocationSize);
        }

        s.limit = result;
        s.block_next[result] = p;
        s.block_size[result] = alignedSize;
        return result;
    }

    internal static stack_ptr stack_pop_block(stack s, stack_ptr p, int sz)
    {
        ArgumentNullException.ThrowIfNull(s);
        var result = stack_block_next(s, p);
        if (p == s.limit)
        {
            var allocationSize = checked(align_round_up(sz) + ALIGNMENT);
            if (s.block_size[p] != align_round_up(sz))
            {
                throw new InvalidOperationException("The block size does not match the pushed block.");
            }

            s.limit = checked((int)s.limit + allocationSize);
            s.block_next.Remove(p);
            s.block_size.Remove(p);
            s.block_value.Remove(p);
        }

        return result;
    }

    internal static void stack_set_block<T>(stack s, stack_ptr p, T value)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(s);
        if (!s.block_size.ContainsKey(p))
        {
            throw new ArgumentOutOfRangeException(nameof(p));
        }

        s.block_value[p] = value;
    }

    internal static T stack_get_block<T>(stack s, stack_ptr p)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(s);
        if (!s.block_value.TryGetValue(p, out var value) || value is not T typed)
        {
            throw new InvalidOperationException(
                $"Stack block {p} does not contain {typeof(T).Name}.");
        }

        return typed;
    }
}

#pragma warning restore CS8981
