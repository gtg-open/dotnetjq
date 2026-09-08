// DOTNETJQ PORT MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream file: src/jv.c
// Upstream URL: https://github.com/jqlang/jq/blob/jq-1.8.2/src/jv.c
// Strategy: PORT
// Target file: src/DotNetJq/Port/src/jv.c.cs
// Substitutions: CLR objects provide the physical allocations, while jq's logical reference counts,
// UTF-8 string bytes/length/capacity/hash, invalid-message ownership, array handle offset/size,
// physical collection capacities, and copy-on-write decisions are retained.
// Known differences: the CLR ultimately reclaims released storage; jv_free still performs jq's
// deterministic logical decrements because they control observable copy-on-write behavior.
// OWNERSHIP MAP: jv_refcnt/jv_copy/jv_free follow jq-1.8.2 src/jv.c:53-75,1999-2069;
// string, array, and object unsharing follows src/jv.c:1178-1205,866-897,1729-1756.
// Managed counters use the same single-state, non-atomic transitions as upstream jq. Literal
// numbers retain jq's allocated-literal versus immediate-binary64 distinction and logical refcount
// behavior, while JvNumber replaces the native trailing buffer.
// See porting/REFERENCE_COUNT_OWNERSHIP_MAP.md.

using System.Collections;
using System.Security.Cryptography;
using System.Text;

namespace DotNetJq.Port;

// jq-1.8.2 src/jv.c: jv_refcnt. It counts owning jv handles/slots, not CLR
// object references. Like native jq, a compiled jq_state and its constants are
// confined to one execution at a time, so these transitions are non-atomic.
internal sealed class jv_refcnt
{
    private int count = 1;

    internal int Count
    {
        get => count;
        set => count = value;
    }

    internal void Increment()
    {
        ObjectDisposedException.ThrowIf(count <= 0, this);
        count = checked(count + 1);
    }

    internal bool Decrement()
    {
        ObjectDisposedException.ThrowIf(count <= 0, this);
        count--;
        return count == 0;
    }
}

// jq-1.8.2 src/jv.c: jvp_invalid.  The wrapper owns ErrorMessage; copies
// increment only this outer allocation, never the child until extraction.
internal sealed class jvp_invalid(jv errorMessage)
{
    internal jv_refcnt Refcnt { get; } = new();

    internal jv ErrorMessage { get; private set; } = errorMessage;

    internal void EnsureAlive()
    {
        if (Refcnt.Count <= 0)
        {
            throw new ObjectDisposedException(nameof(jvp_invalid), "The jq invalid allocation has been released.");
        }
    }

    internal jv ReleaseStorage()
    {
        var message = ErrorMessage;
        ErrorMessage = default;
        return message;
    }
}

// jq-1.8.2 src/jv.c: jvp_string.  UTF-8 bytes, rather than System.String,
// are the source of truth so embedded NULs and deliberately malformed bytes
// retain jq's exact length, equality, hashing, slicing, and append behavior.
internal sealed class jvp_string
{
    internal jvp_string(int allocLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(allocLength);
        AllocLength = allocLength;
        Data = new byte[checked(allocLength + 1)];
    }

    internal jv_refcnt Refcnt { get; } = new();

    internal uint Hash { get; set; }

    // High 31 bits are the byte length; low bit is the cached-hash flag.
    internal uint LengthHashed { get; set; }

    internal int AllocLength { get; private set; }

    internal byte[] Data { get; private set; }

    internal string? CachedText { get; set; }

    internal int Length => checked((int)(LengthHashed >> 1));

    internal bool HasHash => (LengthHashed & 1U) != 0;

    internal void SetLength(int length)
    {
        if ((uint)length > (uint)AllocLength)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        LengthHashed = checked((uint)length << 1);
        Data[length] = 0;
        CachedText = null;
    }

    internal void EnsureAlive()
    {
        if (Refcnt.Count <= 0)
        {
            throw new ObjectDisposedException(nameof(jvp_string), "The jq string allocation has been released.");
        }
    }

    internal void ReleaseStorage()
    {
        Hash = 0;
        LengthHashed = 0;
        AllocLength = 0;
        Data = Array.Empty<byte>();
        CachedText = null;
    }
}

// jq-1.8.2 src/jv.c: jvp_array.  Length is the initialized physical prefix;
// individual jv handles carry their own Offset and Size view over Elements.
internal sealed class jvp_array
{
#pragma warning disable CA1825
    internal jvp_array(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        Elements = new jv[capacity];
    }
#pragma warning restore CA1825

    internal jv_refcnt Refcnt { get; } = new();

    internal int Length { get; set; }

    internal int AllocLength => Elements.Length;

    internal jv[] Elements { get; private set; }

    internal void EnsureAlive()
    {
        if (Refcnt.Count <= 0)
        {
            throw new ObjectDisposedException(nameof(jvp_array), "The jq array allocation has been released.");
        }
    }

    internal void ReleaseStorage()
    {
        Length = 0;
        Elements = Array.Empty<jv>();
    }
}

// jq-1.8.2 src/jv.c: struct object_slot.  Keys and values are owning jv
// handles.  A null String marks a tombstone; deleted physical slots are not
// reused until jvp_object_rehash() compacts the table.
internal struct object_slot
{
    internal int Next;

    internal uint Hash;

    internal jv String;

    internal jv Value;
}

// jq-1.8.2 src/jv.c: jvp_object.  Native jq places the buckets after its slot
// array; separate managed arrays preserve the same capacity and chain model.
internal sealed class jvp_object
{
    internal jvp_object(int size)
    {
        if (size <= 0 || (size & (size - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "jq object size must be a positive power of two.");
        }

        Elements = new object_slot[size];
        for (var index = 0; index < Elements.Length; index++)
        {
            Elements[index].Next = index - 1;
            Elements[index].String = libjq.jv_null();
            Elements[index].Value = libjq.jv_null();
        }

        Buckets = new int[checked(size * 2)];
        Array.Fill(Buckets, -1);
        BorrowedView = new jvp_object_borrowed_view(this);
    }

    internal jv_refcnt Refcnt { get; } = new();

    internal int NextFree { get; set; }

    internal object_slot[] Elements { get; private set; }

    internal int[] Buckets { get; private set; }

    internal IReadOnlyList<KeyValuePair<string, jv>> BorrowedView { get; }

    internal void EnsureAlive()
    {
        if (Refcnt.Count <= 0)
        {
            throw new ObjectDisposedException(nameof(jvp_object), "The jq object allocation has been released.");
        }
    }

    internal void ReleaseStorage()
    {
        NextFree = 0;
        Elements = Array.Empty<object_slot>();
        Buckets = Array.Empty<int>();
    }
}

// Compatibility adapter for peripheral managed code.  It exposes live slots
// in physical order but never creates owners for slot values.
internal sealed class jvp_object_borrowed_view(jvp_object storage) :
    IReadOnlyList<KeyValuePair<string, jv>>
{
    public int Count
    {
        get
        {
            storage.EnsureAlive();
            var count = 0;
            foreach (var slot in storage.Elements)
            {
                if (slot.String.Kind != jv_kind.JV_KIND_NULL)
                {
                    count++;
                }
            }

            return count;
        }
    }

    public KeyValuePair<string, jv> this[int index]
    {
        get
        {
            storage.EnsureAlive();
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            foreach (var slot in storage.Elements)
            {
                if (slot.String.Kind == jv_kind.JV_KIND_NULL)
                {
                    continue;
                }

                if (index-- == 0)
                {
                    return KeyValuePair.Create(slot.String.StringValue, slot.Value);
                }
            }

            throw new ArgumentOutOfRangeException(nameof(index));
        }
    }

    public IEnumerator<KeyValuePair<string, jv>> GetEnumerator()
    {
        storage.EnsureAlive();
        foreach (var slot in storage.Elements)
        {
            if (slot.String.Kind != jv_kind.JV_KIND_NULL)
            {
                yield return KeyValuePair.Create(slot.String.StringValue, slot.Value);
            }
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

internal static partial class libjq
{
    // jq-1.8.2 src/jv.c: decimal precision used by
    // jvp_literal_number_to_double() before the dtoa/strtod bridge.
    private const int DEC_NUMBER_DOUBLE_PRECISION = 17;

    // jq-1.8.2 src/jv.c: MAX_EQUAL_DEPTH.  Keep this independent of the
    // evaluator's configurable recursion limit: it is a value-operation
    // compatibility boundary, not a CLR call-stack policy.
    private const int MaxEqualDepth = 10_000;

    internal static jv jv_invalid() => default;

    internal static jv jv_invalid_with_msg(jv message) =>
        new(jv_kind.JV_KIND_INVALID, new jvp_invalid(message));

    // jq-1.8.2 src/jv.c: jv_invalid_get_msg()/jv_invalid_has_msg().  Both
    // consume the wrapper; extraction copies the owned child first.
    internal static jv jv_invalid_get_msg(jv invalid)
    {
        ensure_kind(invalid, jv_kind.JV_KIND_INVALID, nameof(invalid));
        var result = invalid.Value is jvp_invalid storage
            ? jv_copy(storage.ErrorMessage)
            : jv_null();
        jv_free(invalid);
        return result;
    }

    internal static bool jv_invalid_has_msg(jv invalid)
    {
        ensure_kind(invalid, jv_kind.JV_KIND_INVALID, nameof(invalid));
        var result = invalid.Value is jvp_invalid;
        jv_free(invalid);
        return result;
    }

    internal static jv jv_null() => new(jv_kind.JV_KIND_NULL);

    internal static jv jv_false() => new(jv_kind.JV_KIND_FALSE);

    internal static jv jv_true() => new(jv_kind.JV_KIND_TRUE);

    internal static jv jv_bool(bool value) => value ? jv_true() : jv_false();

    internal static jv jv_number(double value) =>
        new(jv_kind.JV_KIND_NUMBER, new JvNumber(value));

    internal static jv jv_number(double value, string literal) =>
        new(jv_kind.JV_KIND_NUMBER, new JvNumber(value, literal));

    // jq-1.8.2 src/jv.c:jvp_number_is_literal(),
    // jvp_literal_number_ptr(), and jvp_number_free(). Only exact decimal
    // literals are allocated number payloads; native binary64 numbers are
    // immediate scalars even though their managed representation is boxed.
    private static bool jvp_number_is_literal(jv number)
    {
        ensure_kind(number, jv_kind.JV_KIND_NUMBER, nameof(number));
        return ((JvNumber)number.Value!).IsLiteralAllocation;
    }

    private static JvNumber jvp_literal_number_ptr(jv number)
    {
        ensure_kind(number, jv_kind.JV_KIND_NUMBER, nameof(number));
        var storage = (JvNumber)number.Value!;
        if (!storage.IsLiteralAllocation)
        {
            throw new InvalidOperationException("The jq number is not backed by literal storage.");
        }

        storage.EnsureAlive();
        return storage;
    }

    private static void jvp_number_free(jv number)
    {
        ensure_kind(number, jv_kind.JV_KIND_NUMBER, nameof(number));
        if (!jvp_number_is_literal(number))
        {
            return;
        }

        var storage = jvp_literal_number_ptr(number);
        if (jvp_refcnt_dec(storage.Refcnt!))
        {
            storage.ReleaseStorage();
        }
    }

    // jq-1.8.2 src/jv.c:jvp_literal_number_to_double(). The native path does
    // not parse the arbitrary-precision decimal directly as binary64: it first
    // reduces it under a decimal64 context widened to 17 significant digits,
    // renders that decNumber, and only then calls jvp_strtod() with the current
    // thread's dtoa context. This intermediate rounding is observable at
    // binary64 branch neighbours and therefore must not be replaced by a
    // direct double.TryParse of the original literal.
    internal static double jvp_literal_number_to_double(ManagedDecimal exact)
    {
        var doubleContext = decContextDefault(new decContext(), DEC_INIT_DECIMAL64);
        doubleContext.digits = DEC_NUMBER_DOUBLE_PRECISION;
        var reduced = decNumberReduce(
            new decNumber(),
            new decNumber(exact),
            doubleContext);
        var reducedLiteral = decNumberToString(reduced);
        return jvp_strtod(tsd_dtoa_context_get(), reducedLiteral, out _);
    }

    internal static jv jv_number_with_literal(string literal)
    {
        ArgumentNullException.ThrowIfNull(literal);
        var context = CreateJqDecimalContext();
        decContextClearStatus(context, DEC_Conversion_syntax);
        var parsed = decNumberFromString(new decNumber(), literal, context);
        if ((context.status & DEC_Conversion_syntax) != 0)
        {
            return jv_invalid();
        }

        if (decNumberIsNaN(parsed))
        {
            return parsed.Value.Coefficient.IsZero ? jv_number(double.NaN) : jv_invalid();
        }

        return new jv(jv_kind.JV_KIND_NUMBER, new JvNumber(parsed.Value));
    }

    internal static double jv_number_value(jv number)
    {
        ensure_kind(number, jv_kind.JV_KIND_NUMBER, nameof(number));
        return number.NumberValue;
    }

    // Upstream deliberately tests the binary64 fractional part against
    // DBL_EPSILON; this is not the stricter integer-index predicate.
    internal static bool jv_is_integer(jv value)
    {
        if (value.Kind != jv_kind.JV_KIND_NUMBER)
        {
            return false;
        }

        if (double.IsInfinity(value.NumberValue))
        {
            return true;
        }

        var fractional = value.NumberValue - Math.Truncate(value.NumberValue);
        return Math.Abs(fractional) < 2.2204460492503131e-16;
    }

    internal static bool jv_number_has_literal(jv number)
    {
        ensure_kind(number, jv_kind.JV_KIND_NUMBER, nameof(number));
        return jvp_number_is_literal(number);
    }

    internal static string? jv_number_get_literal(jv number)
    {
        ensure_kind(number, jv_kind.JV_KIND_NUMBER, nameof(number));
        // jq returns jvp_literal_number.literal_data, populated lazily by
        // decNumberToString() from the parsed decimal value.
        return jvp_number_is_literal(number)
            ? jvp_literal_number_ptr(number).Literal
            : null;
    }

    internal static jv jv_number_negate(jv value) =>
        value.Kind == jv_kind.JV_KIND_NUMBER
            ? new jv(jv_kind.JV_KIND_NUMBER, ((JvNumber)value.Value!).Negate())
            : throw JqTypeError(value, "cannot be negated");

    internal static jv jv_number_abs(jv value) =>
        value.Kind == jv_kind.JV_KIND_NUMBER
            ? new jv(jv_kind.JV_KIND_NUMBER, ((JvNumber)value.Value!).Abs())
            : throw JqTypeError(value, "cannot have its absolute value taken");

    /*
     * Strings (internal allocation helpers)
     *
     * Direct port of jq-1.8.2 src/jv.c:jvp_string_ptr() through
     * jvp_string_equal().  The trailing NUL lives at Data[Length], while
     * AllocLength excludes it exactly as in the native allocation.
     */
    private static jvp_string jvp_string_ptr(jv value)
    {
        ensure_kind(value, jv_kind.JV_KIND_STRING, nameof(value));
        var storage = (jvp_string)value.Value!;
        storage.EnsureAlive();
        return storage;
    }

    private static jvp_string jvp_string_alloc(int size) => new(size);

    private static jv jvp_string_new(ReadOnlySpan<byte> data)
    {
        var storage = jvp_string_alloc(data.Length);
        data.CopyTo(storage.Data);
        storage.SetLength(data.Length);
        return new jv(jv_kind.JV_KIND_STRING, storage);
    }

    private static jv jvp_string_empty_new(int length) =>
        new(jv_kind.JV_KIND_STRING, jvp_string_alloc(length));

    // Copy a UTF-8 string, replacing each badly encoded unit with U+FFFD.
    private static jv jvp_string_copy_replace_bad(ReadOnlySpan<byte> data)
    {
        var maximumLength = ((long)data.Length * 3) + 1;
        if (maximumLength >= int.MaxValue)
        {
            return jv_invalid_with_msg(jv_string("String too long"));
        }

        var storage = jvp_string_alloc((int)maximumLength);
        var inputOffset = 0;
        var outputOffset = 0;
        var codepoint = 0;
        int? next;
        while ((next = jvp_utf8_next(data, inputOffset, ref codepoint)) is not null)
        {
            if (codepoint == JVP_UTF8_INVALID_CODEPOINT)
            {
                codepoint = JVP_UNICODE_REPLACEMENT_CODEPOINT;
            }

            outputOffset += jvp_utf8_encode(codepoint, storage.Data.AsSpan(outputOffset));
            inputOffset = next.Value;
        }

        storage.SetLength(outputOffset);
        return new jv(jv_kind.JV_KIND_STRING, storage);
    }

    private static int jvp_string_length(jvp_string storage) => storage.Length;

    private static int jvp_string_remaining_space(jvp_string storage) =>
        storage.AllocLength - storage.Length;

    // Borrow the complete jq string payload by its explicit byte length.  This
    // is the managed counterpart of jv_string_value() plus
    // jv_string_length_bytes(jv_copy(value)); callers which model a libc
    // C-string boundary must still stop at the first NUL themselves.
    internal static ReadOnlySpan<byte> jvp_string_data(jv value)
    {
        var storage = jvp_string_ptr(value);
        return storage.Data.AsSpan(0, storage.Length);
    }

    internal static string jvp_string_text(jv value)
    {
        var storage = jvp_string_ptr(value);
        return storage.CachedText ??=
            Encoding.UTF8.GetString(storage.Data, 0, storage.Length);
    }

    private static bool jvp_string_equal(jv left, jv right) =>
        jvp_string_data(left).SequenceEqual(jvp_string_data(right));

    private static int jvp_string_cmp(jv left, jv right) =>
        jvp_string_data(left).SequenceCompareTo(jvp_string_data(right));

    internal static jv jv_string(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return jvp_string_new(Encoding.UTF8.GetBytes(value));
    }

    // The C API takes a byte count.  Keeping a span overload makes that
    // contract explicit and preserves jq's invalid-unit repair policy.
    internal static jv jv_string_sized(ReadOnlySpan<byte> value, int length)
    {
        if ((uint)length > (uint)value.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        var data = value[..length];
        return jvp_utf8_is_valid(data)
            ? jvp_string_new(data)
            : jvp_string_copy_replace_bad(data);
    }

    internal static jv jv_string_empty(int reservedLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(reservedLength);
        return jvp_string_empty_new(reservedLength);
    }

    /*
     * Arrays (internal helpers)
     *
     * Direct port of jq-1.8.2 src/jv.c:
     * ARRAY_SIZE_ROUND_UP, jvp_array_alloc/new/length/offset/read/write,
     * and jvp_array_slice.  Keep these boundaries beside the public array
     * entry points so every jv_copy()/jv_free() remains source-comparable.
     */
    private static int ARRAY_SIZE_ROUND_UP(int length) => checked(length * 3 / 2);

    private static void jvp_refcnt_inc(jv_refcnt refcnt)
    {
        refcnt.Increment();
    }

    private static bool jvp_refcnt_dec(jv_refcnt refcnt)
    {
        return refcnt.Decrement();
    }

    private static bool jvp_refcnt_unshared(jv_refcnt refcnt)
    {
        ObjectDisposedException.ThrowIf(refcnt.Count <= 0, refcnt);

        return refcnt.Count == 1;
    }

    private static jvp_array jvp_array_ptr(jv array)
    {
        ensure_kind(array, jv_kind.JV_KIND_ARRAY, nameof(array));
        var storage = (jvp_array)array.Value!;
        storage.EnsureAlive();
        return storage;
    }

    private static jvp_array jvp_array_alloc(int size) => new(size);

    private static jv jvp_array_new(int size)
    {
        var array = jvp_array_alloc(size);
        return new jv(jv_kind.JV_KIND_ARRAY, array, offset: 0, size: 0);
    }

    private static int jvp_array_length(jv array)
    {
        _ = jvp_array_ptr(array);
        return array.Size;
    }

    internal static int jvp_array_offset(jv array)
    {
        _ = jvp_array_ptr(array);
        return array.Offset;
    }

    // jq-1.8.2 src/jv.c:jv_array_set(): the largest accepted logical index is
    // `(INT_MAX >> 2) - jvp_array_offset(j)`. Keep the decision in one helper so
    // the iterative jv_setpath port can apply the same pre-allocation boundary.
    internal static bool jvp_array_set_index_too_large(double index, int arrayOffset) =>
        double.IsPositiveInfinity(index) || index > (int.MaxValue >> 2) - arrayOffset;

    private static jv? jvp_array_read(jv array, int index)
    {
        if (index < 0 || index >= jvp_array_length(array))
        {
            return null;
        }

        var storage = jvp_array_ptr(array);
        var physicalIndex = index + jvp_array_offset(array);
        if (physicalIndex >= storage.Length)
        {
            throw new InvalidOperationException("jq array view exceeds initialized storage.");
        }

        return storage.Elements[physicalIndex];
    }

    private static ref jv jvp_array_write(ref jv arrayValue, int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        var array = jvp_array_ptr(arrayValue);
        var position = checked(index + jvp_array_offset(arrayValue));
        if (position < array.AllocLength && jvp_refcnt_unshared(array.Refcnt))
        {
            // jq initializes every newly exposed physical slot to JV_NULL.
            for (var slot = array.Length; slot <= position; slot++)
            {
                array.Elements[slot] = jv_null();
            }

            array.Length = Math.Max(position + 1, array.Length);
            arrayValue = new jv(
                jv_kind.JV_KIND_ARRAY,
                array,
                arrayValue.Offset,
                Math.Max(index + 1, arrayValue.Size));
            return ref array.Elements[position];
        }

        var newLength = Math.Max(index + 1, jvp_array_length(arrayValue));
        var newArray = jvp_array_alloc(ARRAY_SIZE_ROUND_UP(newLength));
        var copied = 0;
        for (; copied < jvp_array_length(arrayValue); copied++)
        {
            newArray.Elements[copied] =
                jv_copy(array.Elements[copied + jvp_array_offset(arrayValue)]);
        }

        for (; copied < newLength; copied++)
        {
            newArray.Elements[copied] = jv_null();
        }

        newArray.Length = newLength;
        jv_free(arrayValue);
        arrayValue = new jv(jv_kind.JV_KIND_ARRAY, newArray, offset: 0, size: newLength);
        return ref newArray.Elements[index];
    }

    private static jv jvp_array_slice(jv array, int start, int end)
    {
        var length = jvp_array_length(array);
        ClampSliceParameters(length, ref start, ref end);

        if (start == end)
        {
            jv_free(array);
            return jv_array();
        }

        if ((uint)array.Offset + (uint)start > ushort.MaxValue)
        {
            var result = jv_array_sized(end - start);
            for (var index = start; index < end; index++)
            {
                result = jv_array_append(result, jv_array_get(jv_copy(array), index));
            }

            jv_free(array);
            return result;
        }

        return new jv(
            jv_kind.JV_KIND_ARRAY,
            array.Value,
            checked((ushort)(array.Offset + start)),
            end - start);
    }

    /* Arrays (public interface) */

    internal static jv jv_array_sized(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        return jvp_array_new(capacity);
    }

    internal static jv jv_array() => jv_array_sized(16);

    internal static jv jv_array(IEnumerable<jv> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        // Managed construction helper: each enumerated jv owner is moved into
        // one physical array slot, the same operation repeated native-side by
        // jv_array_append().  Callers retaining an element must pass jv_copy().
        var elements = values.ToArray();
        var array = jvp_array_alloc(elements.Length);
        elements.CopyTo(array.Elements, 0);
        array.Length = elements.Length;
        return new jv(jv_kind.JV_KIND_ARRAY, array, offset: 0, size: elements.Length);
    }

    internal static jv jv_object() => jvp_object_new(8);

    internal static jv jv_object(IEnumerable<KeyValuePair<string, jv>> values)
    {
        var result = jv_object();
        foreach (var pair in values)
        {
            result = jv_object_set(result, pair.Key, pair.Value);
        }

        return result;
    }

    internal static bool jv_is_valid(jv value) => value.IsValid;

    internal static jv_kind jv_get_kind(jv value) => value.Kind;

    internal static string jv_kind_name(jv_kind kind) => kind switch
    {
        jv_kind.JV_KIND_INVALID => "<invalid>",
        jv_kind.JV_KIND_NULL => "null",
        jv_kind.JV_KIND_FALSE or jv_kind.JV_KIND_TRUE => "boolean",
        jv_kind.JV_KIND_NUMBER => "number",
        jv_kind.JV_KIND_STRING => "string",
        jv_kind.JV_KIND_ARRAY => "array",
        jv_kind.JV_KIND_OBJECT => "object",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    // jq-1.8.2 src/jv.c: jv_array_length().  This public API consumes its
    // array, hence callers retaining the value must spell jv_copy(array).
    internal static int jv_array_length(jv array)
    {
        ensure_kind(array, jv_kind.JV_KIND_ARRAY, nameof(array));
        var length = jvp_array_length(array);
        jv_free(array);
        return length;
    }

    // jq-1.8.2 src/jv.c: jv_array_get().  The selected child is copied into
    // a new owner before the consumed outer array is released.
    internal static jv jv_array_get(jv array, int index)
    {
        ensure_kind(array, jv_kind.JV_KIND_ARRAY, nameof(array));
        var slot = jvp_array_read(array, index);
        var value = slot is { } present ? jv_copy(present) : jv_invalid();
        jv_free(array);
        return value;
    }

    // jq-1.8.2 src/jv.c: jv_array_set().  Both arguments are consumed; val
    // is moved into the destination slot after the previous owner is freed.
    internal static jv jv_array_set(jv array, int index, jv value)
    {
        ensure_kind(array, jv_kind.JV_KIND_ARRAY, nameof(array));

        if (index < 0)
        {
            index = jvp_array_length(array) + index;
        }

        if (index < 0)
        {
            jv_free(array);
            jv_free(value);
            return jv_invalid_with_msg(jv_string("Out of bounds negative array index"));
        }

        if (jvp_array_set_index_too_large(index, jvp_array_offset(array)))
        {
            jv_free(array);
            jv_free(value);
            return jv_invalid_with_msg(jv_string("Array index too large"));
        }

        ref var slot = ref jvp_array_write(ref array, index);
        jv_free(slot);
        slot = value;
        return array;
    }

    // jq-1.8.2 src/jv.c: jv_array_append().  The temporary copy exists only
    // because the public length query consumes its argument.
    internal static jv jv_array_append(jv array, jv value) =>
        jv_array_set(array, jv_array_length(jv_copy(array)), value);

    // jq-1.8.2 src/jv.c: jv_array_concat().
    internal static jv jv_array_concat(jv left, jv right)
    {
        ensure_kind(left, jv_kind.JV_KIND_ARRAY, nameof(left));
        ensure_kind(right, jv_kind.JV_KIND_ARRAY, nameof(right));

        var rightLength = jv_array_length(jv_copy(right));
        for (var index = 0; index < rightLength; index++)
        {
            left = jv_array_append(left, jv_array_get(jv_copy(right), index));
            if (!jv_is_valid(left))
            {
                break;
            }
        }

        jv_free(right);
        return left;
    }

    // jq-1.8.2 src/jv.c: jv_array_slice(); copy/free of the input is
    // deliberately coalesced into jvp_array_slice().
    internal static jv jv_array_slice(jv array, int start, int end)
    {
        ensure_kind(array, jv_kind.JV_KIND_ARRAY, nameof(array));
        return jvp_array_slice(array, start, end);
    }

    // jq-1.8.2 src/jv.c: jv_array_indexes().  An array used as an array
    // index is a subsequence search, and every (including overlapping)
    // starting position is returned.
    internal static jv jv_array_indexes(jv array, jv needle)
    {
        if (array.Kind != jv_kind.JV_KIND_ARRAY ||
            needle.Kind != jv_kind.JV_KIND_ARRAY)
        {
            throw new ArgumentException("jv_array_indexes requires two arrays");
        }

        try
        {
            var indexes = new List<jv>();
            for (var arrayIndex = 0; arrayIndex < array.ArrayValue.Count; arrayIndex++)
            {
                var matchIndex = -1;
                for (var needleIndex = 0; needleIndex < needle.ArrayValue.Count; needleIndex++)
                {
                    var candidateIndex = arrayIndex + needleIndex;
                    var candidate = candidateIndex < array.ArrayValue.Count
                        ? array.ArrayValue[candidateIndex]
                        : jv_invalid();
                    // jq-1.8.2 src/jv.c:jv_array_indexes() obtains owning
                    // children with jv_array_get(); jv_equal() consumes both.
                    // ArrayValue exposes borrowed slots, so acquire the same
                    // two logical owners explicitly.
                    if (!jv_equal(jv_copy(candidate), jv_copy(needle.ArrayValue[needleIndex])))
                    {
                        matchIndex = -1;
                    }
                    else if (needleIndex == 0 && matchIndex == -1)
                    {
                        matchIndex = arrayIndex;
                    }
                }

                if (matchIndex > -1)
                {
                    indexes.Add(jv_number(matchIndex));
                }
            }

            return jv_array(indexes);
        }
        catch (JqRuntimeException exception) when (
            string.Equals(exception.Message, "Equality check too deep", StringComparison.Ordinal))
        {
            return jv_invalid_with_msg(jv_string("Equality check too deep"));
        }
        finally
        {
            // jq-1.8.2 src/jv.c:jv_array_indexes() consumes both arrays on
            // its success and equality-depth-error paths.
            jv_free(array);
            jv_free(needle);
        }
    }

    internal static int jv_string_length_bytes(jv value)
    {
        ensure_kind(value, jv_kind.JV_KIND_STRING, nameof(value));
        var length = jvp_string_length(jvp_string_ptr(value));
        jv_free(value);
        return length;
    }

    internal static string jv_string_value(jv value)
    {
        ensure_kind(value, jv_kind.JV_KIND_STRING, nameof(value));
        return value.StringValue;
    }

    // jq-1.8.2 src/jv.c:jvp_string_append().  The input owner is consumed:
    // mutate only a unique allocation with enough spare bytes, otherwise
    // detach at the native growth capacity and release the old owner.
    private static jv jvp_string_append(jv value, ReadOnlySpan<byte> data)
    {
        var storage = jvp_string_ptr(value);
        var currentLength = jvp_string_length(storage);
        var newLength = (long)currentLength + data.Length;
        if (newLength >= int.MaxValue - 8L)
        {
            jv_free(value);
            return jv_invalid_with_msg(jv_string("String too long"));
        }

        if (jvp_refcnt_unshared(storage.Refcnt) &&
            jvp_string_remaining_space(storage) >= data.Length)
        {
            data.CopyTo(storage.Data.AsSpan(currentLength));
            storage.SetLength((int)newLength);
            return value;
        }

        var doubledLength = newLength * 2;
        if (doubledLength > int.MaxValue - 1L)
        {
            jv_free(value);
            return jv_invalid_with_msg(jv_string("String too long"));
        }

        var replacement = jvp_string_alloc(Math.Max((int)doubledLength, 32));
        storage.Data.AsSpan(0, currentLength).CopyTo(replacement.Data);
        data.CopyTo(replacement.Data.AsSpan(currentLength));
        replacement.SetLength((int)newLength);
        jv_free(value);
        return new jv(jv_kind.JV_KIND_STRING, replacement);
    }

    // jq-1.8.2 src/jv.c: jv_string_indexes().  Results are Unicode-scalar
    // offsets, matches overlap, and an empty needle produces no matches.
    internal static jv jv_string_indexes(jv value, jv needle)
    {
        ensure_kind(value, jv_kind.JV_KIND_STRING, nameof(value));
        ensure_kind(needle, jv_kind.JV_KIND_STRING, nameof(needle));
        try
        {
            var source = jvp_string_data(value);
            var pattern = jvp_string_data(needle);
            var result = jv_array();
            if (pattern.Length == 0)
            {
                return result;
            }

            var searchFrom = 0;
            var lastCodepointBoundary = 0;
            var codepointOffset = 0;
            while (searchFrom <= source.Length - pattern.Length)
            {
                var relative = _jq_memmem(source[searchFrom..], pattern);
                if (relative < 0)
                {
                    break;
                }

                var match = searchFrom + relative;
                while (lastCodepointBoundary < match)
                {
                    lastCodepointBoundary += jvp_utf8_decode_length(source[lastCodepointBoundary]);
                    codepointOffset++;
                }

                result = jv_array_append(result, jv_number(codepointOffset));
                if (!result.IsValid)
                {
                    break;
                }

                // Native _jq_memmem search resumes one byte later, so matches
                // may overlap and need not begin at a codepoint boundary.
                searchFrom = match + 1;
            }

            return result;
        }
        finally
        {
            jv_free(value);
            jv_free(needle);
        }
    }

    internal static jv jv_string_repeat(jv value, int count)
    {
        ensure_kind(value, jv_kind.JV_KIND_STRING, nameof(value));
        if (count < 0)
        {
            jv_free(value);
            return jv_null();
        }

        var sourceLength = jvp_string_length(jvp_string_ptr(value));
        var byteLength = (long)sourceLength * count;
        // Pinned jq's four-uint32 jvp_string header is 16 bytes; upstream
        // subtracts half that size from INT_MAX before allocation.
        if (byteLength >= int.MaxValue - 8L)
        {
            jv_free(value);
            return jv_invalid_with_msg(jv_string("Repeat string result too long"));
        }

        if (byteLength == 0)
        {
            jv_free(value);
            return jv_string(string.Empty);
        }

        try
        {
            var result = jv_string_empty((int)byteLength);
            result = jvp_string_append(result, jvp_string_data(value));
            for (var current = sourceLength; current < byteLength;)
            {
                var grow = (int)Math.Min(byteLength - current, current);
                result = jvp_string_append(result, jvp_string_data(result)[..grow]);
                current += grow;
            }

            jv_free(value);
            return result;
        }
        catch (OutOfMemoryException)
        {
            jv_free(value);
            return jv_invalid_with_msg(jv_string("Repeat string result too long"));
        }
    }

    internal static jv jv_string_split(jv value, jv separator)
    {
        ensure_kind(value, jv_kind.JV_KIND_STRING, nameof(value));
        ensure_kind(separator, jv_kind.JV_KIND_STRING, nameof(separator));
        try
        {
            var source = jvp_string_data(value);
            var delimiter = jvp_string_data(separator);
            var result = jv_array();
            if (delimiter.Length == 0)
            {
                var offset = 0;
                var codepoint = 0;
                int? next;
                while ((next = jvp_utf8_next(source, offset, ref codepoint)) is not null)
                {
                    result = jv_array_append(
                        result,
                        jv_string_append_codepoint(jv_string(string.Empty), codepoint));
                    if (!result.IsValid)
                    {
                        break;
                    }

                    offset = next.Value;
                }

                return result;
            }

            for (var offset = 0; offset < source.Length;)
            {
                var relative = _jq_memmem(source[offset..], delimiter);
                var match = relative < 0 ? source.Length : offset + relative;
                result = jv_array_append(
                    result,
                    jv_string_sized(source[offset..match], match - offset));
                if (!result.IsValid)
                {
                    break;
                }

                if (match + delimiter.Length == source.Length)
                {
                    result = jv_array_append(result, jv_string(string.Empty));
                    break;
                }

                offset = match + delimiter.Length;
            }

            return result;
        }
        finally
        {
            jv_free(value);
            jv_free(separator);
        }
    }

    internal static jv jv_string_slice(jv value, int start, int end)
    {
        ensure_kind(value, jv_kind.JV_KIND_STRING, nameof(value));

        var source = jvp_string_data(value);
        ClampSliceParameters(source.Length, ref start, ref end);
        var codepoint = 0;
        var byteStart = 0;
        for (var index = 0; index < start; index++)
        {
            var next = jvp_utf8_next(source, byteStart, ref codepoint);
            if (next is null)
            {
                jv_free(value);
                return jv_string_empty(16);
            }

            if (codepoint == JVP_UTF8_INVALID_CODEPOINT)
            {
                jv_free(value);
                return jv_invalid_with_msg(jv_string("Invalid UTF-8 string"));
            }

            byteStart = next.Value;
        }

        var byteEnd = byteStart;
        for (var index = start; index < end; index++)
        {
            var next = jvp_utf8_next(source, byteEnd, ref codepoint);
            if (next is null)
            {
                byteEnd = source.Length;
                break;
            }

            if (codepoint == JVP_UTF8_INVALID_CODEPOINT)
            {
                jv_free(value);
                return jv_invalid_with_msg(jv_string("Invalid UTF-8 string"));
            }

            byteEnd = next.Value;
        }

        var result = jv_string_sized(source[byteStart..byteEnd], byteEnd - byteStart);
        jv_free(value);
        return result;
    }

    internal static jv jv_string_concat(jv left, jv right)
    {
        ensure_kind(left, jv_kind.JV_KIND_STRING, nameof(left));
        ensure_kind(right, jv_kind.JV_KIND_STRING, nameof(right));
        left = jvp_string_append(left, jvp_string_data(right));
        jv_free(right);
        return left;
    }

    internal static jv jv_string_append_buf(jv value, ReadOnlySpan<byte> buffer)
    {
        ensure_kind(value, jv_kind.JV_KIND_STRING, nameof(value));
        if (jvp_utf8_is_valid(buffer))
        {
            return jvp_string_append(value, buffer);
        }

        return jv_string_concat(value, jvp_string_copy_replace_bad(buffer));
    }

    internal static jv jv_string_append_buf(jv value, ReadOnlySpan<byte> buffer, int length)
    {
        if ((uint)length > (uint)buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        return jv_string_append_buf(value, buffer[..length]);
    }

    internal static jv jv_string_append_codepoint(jv value, int codepoint)
    {
        ensure_kind(value, jv_kind.JV_KIND_STRING, nameof(value));
        if (codepoint is < 0 or > JVP_UNICODE_MAX_CODEPOINT)
        {
            throw new ArgumentOutOfRangeException(nameof(codepoint));
        }

        Span<byte> encoded = stackalloc byte[4];
        var length = jvp_utf8_encode(codepoint, encoded);
        return jvp_string_append(value, encoded[..length]);
    }

    internal static jv jv_string_append_str(jv value, string suffix)
    {
        ensure_kind(value, jv_kind.JV_KIND_STRING, nameof(value));
        ArgumentNullException.ThrowIfNull(suffix);
        var nul = suffix.IndexOf('\0', StringComparison.Ordinal);
        var text = nul < 0 ? suffix : suffix[..nul];
        return jv_string_append_buf(value, Encoding.UTF8.GetBytes(text));
    }

    /*
     * Objects (internal helpers)
     *
     * Direct port of jq-1.8.2 src/jv.c:jvp_object_new() through
     * jvp_object_length().  The physical slots, bucket chains, tombstones,
     * monotonic next_free cursor, unshare point, and transport-only rehash are
     * intentionally visible here for source-to-source ownership review.
     */
    private static readonly uint JvpHashSeed = CreateJvpHashSeed();

    private static uint CreateJvpHashSeed()
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        RandomNumberGenerator.Fill(bytes);
        return BitConverter.ToUInt32(bytes);
    }

    private static uint rotl32(uint value, int bits) =>
        (value << bits) | (value >> (32 - bits));

    private static uint jvp_string_hash(jv key)
    {
        var storage = jvp_string_ptr(key);
        if (storage.HasHash)
        {
            return storage.Hash;
        }

        var data = storage.Data;
        var length = storage.Length;
        const uint c1 = 0xcc9e2d51;
        const uint c2 = 0x1b873593;
        var hash = JvpHashSeed;
        var blocks = length / sizeof(uint);

        unchecked
        {
            for (var block = 0; block < blocks; block++)
            {
                var word = BitConverter.ToUInt32(data, block * sizeof(uint));
                word *= c1;
                word = rotl32(word, 15);
                word *= c2;
                hash ^= word;
                hash = rotl32(hash, 13);
                hash = (hash * 5) + 0xe6546b64;
            }

            var tailOffset = blocks * sizeof(uint);
            uint tail = 0;
            switch (length & 3)
            {
                case 3:
                    tail ^= (uint)data[tailOffset + 2] << 16;
                    goto case 2;
                case 2:
                    tail ^= (uint)data[tailOffset + 1] << 8;
                    goto case 1;
                case 1:
                    tail ^= data[tailOffset];
                    tail *= c1;
                    tail = rotl32(tail, 15);
                    tail *= c2;
                    hash ^= tail;
                    break;
            }

            hash ^= (uint)length;
            hash ^= hash >> 16;
            hash *= 0x85ebca6b;
            hash ^= hash >> 13;
            hash *= 0xc2b2ae35;
            hash ^= hash >> 16;
        }

        storage.Hash = hash;
        storage.LengthHashed |= 1U;
        return hash;
    }

    internal static uint jv_string_hash(jv value)
    {
        ensure_kind(value, jv_kind.JV_KIND_STRING, nameof(value));
        var hash = jvp_string_hash(value);
        jv_free(value);
        return hash;
    }

    private static jv jvp_object_new(int size) =>
        new(jv_kind.JV_KIND_OBJECT, new jvp_object(size), offset: 0, size);

    private static jvp_object jvp_object_ptr(jv value)
    {
        ensure_kind(value, jv_kind.JV_KIND_OBJECT, nameof(value));
        var storage = (jvp_object)value.Value!;
        storage.EnsureAlive();
        return storage;
    }

    private static int jvp_object_mask(jv value) => checked((value.Size * 2) - 1);

    private static int jvp_object_find_bucket(jv value, jv key) =>
        (int)(jvp_string_hash(key) & (uint)jvp_object_mask(value));

    private static int jvp_object_find_slot(jv value, jv key, int bucket)
    {
        var storage = jvp_object_ptr(value);
        var hash = jvp_string_hash(key);
        for (var current = storage.Buckets[bucket]; current >= 0; current = storage.Elements[current].Next)
        {
            ref var slot = ref storage.Elements[current];
            if (slot.Hash == hash && jvp_string_equal(slot.String, key))
            {
                return current;
            }
        }

        return -1;
    }

    private static int jvp_object_add_slot(jv value, jv key, int bucket)
    {
        var storage = jvp_object_ptr(value);
        var slotIndex = storage.NextFree;
        if (slotIndex == value.Size)
        {
            return -1;
        }

        storage.NextFree++;
        ref var slot = ref storage.Elements[slotIndex];
        slot.Next = storage.Buckets[bucket];
        storage.Buckets[bucket] = slotIndex;
        slot.Hash = jvp_string_hash(key);
        slot.String = key;
        return slotIndex;
    }

    private static int jvp_object_read(jv value, jv key)
    {
        var bucket = jvp_object_find_bucket(value, key);
        return jvp_object_find_slot(value, key, bucket);
    }

    private static bool jvp_object_rehash(ref jv value)
    {
        var oldStorage = jvp_object_ptr(value);
        if (!jvp_refcnt_unshared(oldStorage.Refcnt))
        {
            throw new InvalidOperationException("jq object rehash requires unique storage.");
        }

        if (value.Size > int.MaxValue >> 2)
        {
            return false;
        }

        var replacement = jvp_object_new(value.Size * 2);
        for (var index = 0; index < value.Size; index++)
        {
            ref var oldSlot = ref oldStorage.Elements[index];
            if (oldSlot.String.Kind == jv_kind.JV_KIND_NULL)
            {
                continue;
            }

            var bucket = jvp_object_find_bucket(replacement, oldSlot.String);
            var newIndex = jvp_object_add_slot(replacement, oldSlot.String, bucket);
            jvp_object_ptr(replacement).Elements[newIndex].Value = oldSlot.Value;
        }

        // jq transports the slot owners and raw-frees the old table: do not
        // decrement any key/value while invalidating the consumed allocation.
        oldStorage.Refcnt.Count = 0;
        oldStorage.ReleaseStorage();
        value = replacement;
        return true;
    }

    private static jv jvp_object_unshare(jv value)
    {
        var oldStorage = jvp_object_ptr(value);
        if (jvp_refcnt_unshared(oldStorage.Refcnt))
        {
            return value;
        }

        var replacement = jvp_object_new(value.Size);
        var newStorage = jvp_object_ptr(replacement);
        newStorage.NextFree = oldStorage.NextFree;
        for (var index = 0; index < value.Size; index++)
        {
            var oldSlot = oldStorage.Elements[index];
            newStorage.Elements[index] = oldSlot;
            if (oldSlot.String.Kind != jv_kind.JV_KIND_NULL)
            {
                newStorage.Elements[index].String = jv_copy(oldSlot.String);
                newStorage.Elements[index].Value = jv_copy(oldSlot.Value);
            }
        }

        oldStorage.Buckets.CopyTo(newStorage.Buckets, 0);
        jv_free(value);
        return replacement;
    }

    private static bool jvp_object_write(ref jv value, jv key, out int slotIndex)
    {
        value = jvp_object_unshare(value);
        var bucket = jvp_object_find_bucket(value, key);
        slotIndex = jvp_object_find_slot(value, key, bucket);
        if (slotIndex >= 0)
        {
            jv_free(key);
            return true;
        }

        slotIndex = jvp_object_add_slot(value, key, bucket);
        if (slotIndex < 0)
        {
            if (!jvp_object_rehash(ref value))
            {
                jv_free(key);
                return false;
            }

            bucket = jvp_object_find_bucket(value, key);
            slotIndex = jvp_object_add_slot(value, key, bucket);
        }

        jvp_object_ptr(value).Elements[slotIndex].Value = jv_invalid();
        return true;
    }

    private static bool jvp_object_delete(ref jv value, jv key)
    {
        value = jvp_object_unshare(value);
        var storage = jvp_object_ptr(value);
        var bucket = jvp_object_find_bucket(value, key);
        var hash = jvp_string_hash(key);
        var previous = -1;
        for (var current = storage.Buckets[bucket]; current >= 0; current = storage.Elements[current].Next)
        {
            ref var slot = ref storage.Elements[current];
            if (slot.Hash == hash && jvp_string_equal(slot.String, key))
            {
                if (previous < 0)
                {
                    storage.Buckets[bucket] = slot.Next;
                }
                else
                {
                    storage.Elements[previous].Next = slot.Next;
                }

                jv_free(slot.String);
                jv_free(slot.Value);
                slot.String = jv_null();
                slot.Value = jv_null();
                slot.Hash = 0;
                return true;
            }

            previous = current;
        }

        return false;
    }

    private static int jvp_object_length(jv value)
    {
        var length = 0;
        foreach (var slot in jvp_object_ptr(value).Elements)
        {
            if (slot.String.Kind != jv_kind.JV_KIND_NULL)
            {
                length++;
            }
        }

        return length;
    }

    /* Objects (public interface) */

    // jq-1.8.2 src/jv.c:jv_object_length() consumes its object.
    internal static int jv_object_length(jv value)
    {
        ensure_kind(value, jv_kind.JV_KIND_OBJECT, nameof(value));
        var length = jvp_object_length(value);
        jv_free(value);
        return length;
    }

    // Borrowing managed adapter.  The raw jv-key API below retains jq's
    // consuming invalid-sentinel contract.
    internal static jv jv_object_get(jv value, string key)
    {
        if (value.Kind == jv_kind.JV_KIND_NULL)
        {
            return jv_null();
        }

        if (value.Kind != jv_kind.JV_KIND_OBJECT)
        {
            throw JqTypeError(
                jv_copy(value),
                "Cannot index " + jv_kind_name(value.Kind) + " with string " + key);
        }

        var result = jv_object_get(jv_copy(value), jv_string(key));
        return result.Kind == jv_kind.JV_KIND_INVALID ? jv_null() : result;
    }

    internal static jv jv_object_get(jv value, jv key)
    {
        ensure_kind(value, jv_kind.JV_KIND_OBJECT, nameof(value));
        ensure_kind(key, jv_kind.JV_KIND_STRING, nameof(key));
        var slotIndex = jvp_object_read(value, key);
        var result = slotIndex >= 0
            ? jv_copy(jvp_object_ptr(value).Elements[slotIndex].Value)
            : jv_invalid();
        jv_free(value);
        jv_free(key);
        return result;
    }

    internal static bool jv_object_has(jv value, string key) =>
        value.Kind == jv_kind.JV_KIND_OBJECT &&
        jv_object_has(jv_copy(value), jv_string(key));

    internal static bool jv_object_has(jv value, jv key)
    {
        ensure_kind(value, jv_kind.JV_KIND_OBJECT, nameof(value));
        ensure_kind(key, jv_kind.JV_KIND_STRING, nameof(key));
        var result = jvp_object_read(value, key) >= 0;
        jv_free(value);
        jv_free(key);
        return result;
    }

    internal static jv jv_object_set(jv value, string key, jv element)
    {
        if (value.Kind == jv_kind.JV_KIND_NULL)
        {
            value = jv_object();
        }

        if (value.Kind != jv_kind.JV_KIND_OBJECT)
        {
            throw JqTypeError(value, "Cannot index " + jv_kind_name(value.Kind) + " with string " + key);
        }

        return jv_object_set(value, jv_string(key), element);
    }

    internal static jv jv_object_set(jv value, jv key, jv element)
    {
        ensure_kind(value, jv_kind.JV_KIND_OBJECT, nameof(value));
        ensure_kind(key, jv_kind.JV_KIND_STRING, nameof(key));
        if (!jvp_object_write(ref value, key, out var slotIndex))
        {
            jv_free(value);
            jv_free(element);
            return jv_invalid_with_msg(jv_string("Object too big"));
        }

        ref var slot = ref jvp_object_ptr(value).Elements[slotIndex].Value;
        jv_free(slot);
        slot = element;
        return value;
    }

    internal static jv jv_object_delete(jv value, string key)
    {
        if (value.Kind != jv_kind.JV_KIND_OBJECT)
        {
            return value;
        }

        return jv_object_delete(value, jv_string(key));
    }

    internal static jv jv_object_delete(jv value, jv key)
    {
        ensure_kind(value, jv_kind.JV_KIND_OBJECT, nameof(value));
        ensure_kind(key, jv_kind.JV_KIND_STRING, nameof(key));
        _ = jvp_object_delete(ref value, key);
        jv_free(key);
        return value;
    }

    internal static jv jv_object_merge(jv left, jv right)
    {
        ensure_kind(left, jv_kind.JV_KIND_OBJECT, nameof(left));
        ensure_kind(right, jv_kind.JV_KIND_OBJECT, nameof(right));
        for (var iterator = jv_object_iter(right);
             jv_object_iter_valid(right, iterator);
             iterator = jv_object_iter_next(right, iterator))
        {
            left = jv_object_set(
                left,
                jv_object_iter_key(right, iterator),
                jv_object_iter_value(right, iterator));
            if (!jv_is_valid(left))
            {
                break;
            }
        }

        jv_free(right);
        return left;
    }

    // jq-1.8.2 src/jv.c:jvp_object_merge_recursive(), expressed with explicit
    // frames solely to preserve jq's 10,000 boundary without CLR recursion.
    internal static jv jv_object_merge_recursive(jv left, jv right)
    {
        ensure_kind(left, jv_kind.JV_KIND_OBJECT, nameof(left));
        ensure_kind(right, jv_kind.JV_KIND_OBJECT, nameof(right));

        var frames = new Stack<JvObjectMergeFrame>();
        frames.Push(new JvObjectMergeFrame(left, right, 0, default));
        while (frames.Count > 0)
        {
            var frame = frames.Peek();
            if (frame.Depth > 10_000)
            {
                while (frames.TryPop(out var abandoned))
                {
                    jv_free(abandoned.Result);
                    jv_free(abandoned.Right);
                    jv_free(abandoned.ParentKey);
                }

                return jv_invalid_with_msg(jv_string("Object merge too deep"));
            }

            frame.Iterator = frame.Iterator == JvObjectIterationNotStarted
                ? jv_object_iter(frame.Right)
                : jv_object_iter_next(frame.Right, frame.Iterator);
            if (!jv_object_iter_valid(frame.Right, frame.Iterator))
            {
                var completed = frame.Result;
                jv_free(frame.Right);
                frames.Pop();
                if (frames.Count == 0)
                {
                    return completed;
                }

                var parent = frames.Peek();
                parent.Result = jv_object_set(parent.Result, frame.ParentKey, completed);
                continue;
            }

            var key = jv_object_iter_key(frame.Right, frame.Iterator);
            var element = jv_object_iter_value(frame.Right, frame.Iterator);
            var existing = jv_object_get(jv_copy(frame.Result), jv_copy(key));
            if (existing.Kind == jv_kind.JV_KIND_OBJECT &&
                element.Kind == jv_kind.JV_KIND_OBJECT)
            {
                frames.Push(new JvObjectMergeFrame(
                    existing,
                    element,
                    frame.Depth + 1,
                    key));
            }
            else
            {
                jv_free(existing);
                frame.Result = jv_object_set(frame.Result, key, element);
            }
        }

        throw new InvalidOperationException("Object merge stack completed without a result.");
    }

    private const int JvObjectIterationFinished = -2;
    private const int JvObjectIterationNotStarted = -1;

    internal static bool jv_object_iter_valid(jv value, int iterator)
    {
        ensure_kind(value, jv_kind.JV_KIND_OBJECT, nameof(value));
        return iterator != JvObjectIterationFinished;
    }

    internal static int jv_object_iter(jv value)
    {
        ensure_kind(value, jv_kind.JV_KIND_OBJECT, nameof(value));
        return jv_object_iter_next(value, JvObjectIterationNotStarted);
    }

    internal static int jv_object_iter_next(jv value, int iterator)
    {
        ensure_kind(value, jv_kind.JV_KIND_OBJECT, nameof(value));
        ArgumentOutOfRangeException.ThrowIfEqual(iterator, JvObjectIterationFinished);
        var storage = jvp_object_ptr(value);
        do
        {
            iterator++;
            if (iterator >= value.Size)
            {
                return JvObjectIterationFinished;
            }
        }
        while (storage.Elements[iterator].String.Kind == jv_kind.JV_KIND_NULL);

        return iterator;
    }

    internal static jv jv_object_iter_key(jv value, int iterator)
    {
        ensure_kind(value, jv_kind.JV_KIND_OBJECT, nameof(value));
        return jv_copy(jvp_object_ptr(value).Elements[iterator].String);
    }

    internal static jv jv_object_iter_value(jv value, int iterator)
    {
        ensure_kind(value, jv_kind.JV_KIND_OBJECT, nameof(value));
        return jv_copy(jvp_object_ptr(value).Elements[iterator].Value);
    }

    internal static bool jv_equal(jv left, jv right)
    {
        var pending = new Stack<EqualityWork>();
        pending.Push(EqualityWork.Values(left, right, 0));
        try
        {
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                if (current.IsUnequalResult)
                {
                    return false;
                }

                if (current.Depth > MaxEqualDepth)
                {
                    throw new JqRuntimeException("Equality check too deep");
                }

                if (current.Left.Kind != current.Right.Kind)
                {
                    return false;
                }

                // This is the managed equivalent of upstream's allocated-value
                // pointer identity fast path.  Besides avoiding needless work, it
                // deliberately lets a value compare equal to itself regardless of
                // the depth of the structure it points to.
                if (current.Left.Value is not null &&
                    current.Left.Size == current.Right.Size &&
                    ReferenceEquals(current.Left.Value, current.Right.Value))
                {
                    continue;
                }

                switch (current.Left.Kind)
                {
                    case jv_kind.JV_KIND_INVALID:
                    case jv_kind.JV_KIND_NULL:
                    case jv_kind.JV_KIND_FALSE:
                    case jv_kind.JV_KIND_TRUE:
                        break;
                    case jv_kind.JV_KIND_NUMBER:
                        if (!jv_number_equal(current.Left, current.Right))
                        {
                            return false;
                        }

                        break;
                    case jv_kind.JV_KIND_STRING:
                        if (!jvp_string_equal(current.Left, current.Right))
                        {
                            return false;
                        }

                        break;
                    case jv_kind.JV_KIND_ARRAY:
                    {
                        var leftItems = current.Left.ArrayValue;
                        var rightItems = current.Right.ArrayValue;
                        if (leftItems.Count != rightItems.Count)
                        {
                            return false;
                        }

                        for (var index = leftItems.Count - 1; index >= 0; index--)
                        {
                            pending.Push(EqualityWork.Values(
                                leftItems[index],
                                rightItems[index],
                                current.Depth + 1));
                        }

                        break;
                    }
                    case jv_kind.JV_KIND_OBJECT:
                    {
                        var leftStorage = jvp_object_ptr(current.Left);
                        var rightStorage = jvp_object_ptr(current.Right);
                        var leftCount = jvp_object_length(current.Left);
                        var rightCount = jvp_object_length(current.Right);
                        var work = new List<EqualityWork>(leftCount + 1);
                        foreach (var slot in leftStorage.Elements)
                        {
                            if (slot.String.Kind == jv_kind.JV_KIND_NULL)
                            {
                                continue;
                            }

                            var rightIndex = jvp_object_read(current.Right, slot.String);
                            if (rightIndex < 0)
                            {
                                work.Add(EqualityWork.Unequal());
                                break;
                            }

                            work.Add(EqualityWork.Values(
                                slot.Value,
                                rightStorage.Elements[rightIndex].Value,
                                current.Depth + 1));
                        }

                        if (work.Count == leftCount && leftCount != rightCount)
                        {
                            // Upstream checks the right-hand object length only
                            // after it has compared every left-hand value.
                            work.Add(EqualityWork.Unequal());
                        }

                        for (var index = work.Count - 1; index >= 0; index--)
                        {
                            pending.Push(work[index]);
                        }

                        break;
                    }
                    default:
                        return false;
                }
            }

            return true;
        }
        finally
        {
            // jq-1.8.2 src/jv.c:jvp_equal() consumes both public operands on
            // every result, including the depth-error path.  Descendant work
            // items borrow slots while these two roots remain alive.
            jv_free(left);
            jv_free(right);
        }
    }

    private readonly record struct EqualityWork(
        jv Left,
        jv Right,
        int Depth,
        bool IsUnequalResult)
    {
        internal static EqualityWork Values(jv left, jv right, int depth) =>
            new(left, right, depth, IsUnequalResult: false);

        internal static EqualityWork Unequal() =>
            new(default, default, 0, IsUnequalResult: true);
    }

    internal static int jv_hash(jv value)
    {
        var hash = new HashCode();
        hash.Add(value.Kind);
        switch (value.Kind)
        {
            case jv_kind.JV_KIND_NUMBER:
                hash.Add(value.NumberValue);
                break;
            case jv_kind.JV_KIND_STRING:
                hash.Add(jvp_string_hash(value));
                break;
            case jv_kind.JV_KIND_ARRAY:
                var array = jvp_array_ptr(value);
                var arrayEnd = checked(jvp_array_offset(value) + jvp_array_length(value));
                for (var index = jvp_array_offset(value); index < arrayEnd; index++)
                {
                    hash.Add(jv_hash(array.Elements[index]));
                }

                break;
            case jv_kind.JV_KIND_OBJECT:
                // This CLR-only helper has no public jq analogue, but it must
                // retain jv_equal()'s order-independent object contract. Sort
                // borrowed physical slots by jq's UTF-8 byte comparison; do
                // not cross through the compatibility ObjectValue/StringValue
                // projection merely to hash an internal key.
                var objectSlots = new List<object_slot>(jvp_object_length(value));
                foreach (var slot in jvp_object_ptr(value).Elements)
                {
                    if (slot.String.Kind != jv_kind.JV_KIND_NULL)
                    {
                        objectSlots.Add(slot);
                    }
                }

                objectSlots.Sort(static (left, right) => jvp_string_cmp(left.String, right.String));
                foreach (var slot in objectSlots)
                {
                    hash.Add(jvp_string_hash(slot.String));
                    hash.Add(jv_hash(slot.Value));
                }

                break;
        }

        return hash.ToHashCode();
    }

    // jq-1.8.2 src/jv.c: jv_copy().  Copying an allocated handle increments
    // only its physical allocation; child owners remain in the shared slots.
    internal static jv jv_copy(jv value)
    {
        if (value.Kind == jv_kind.JV_KIND_INVALID && value.Value is jvp_invalid invalid)
        {
            invalid.EnsureAlive();
            jvp_refcnt_inc(invalid.Refcnt);
        }
        else if (value.Kind == jv_kind.JV_KIND_NUMBER && jvp_number_is_literal(value))
        {
            jvp_refcnt_inc(jvp_literal_number_ptr(value).Refcnt!);
        }
        else if (value.Kind == jv_kind.JV_KIND_STRING)
        {
            jvp_refcnt_inc(jvp_string_ptr(value).Refcnt);
        }
        else if (value.Kind == jv_kind.JV_KIND_ARRAY)
        {
            jvp_refcnt_inc(jvp_array_ptr(value).Refcnt);
        }
        else if (value.Kind == jv_kind.JV_KIND_OBJECT)
        {
            jvp_refcnt_inc(jvp_object_ptr(value).Refcnt);
        }

        return value;
    }

    // Allocated C values are identical only when they share an allocation;
    // native scalar numbers compare their representation.  Managed jv_copy()
    // preserves the boxed/array object references used for the same test.
    internal static bool jv_identical(jv left, jv right)
    {
        var identical = left.Kind == right.Kind && left.Offset == right.Offset && left.Size == right.Size &&
            left.Kind switch
        {
            jv_kind.JV_KIND_INVALID when left.Value is not null || right.Value is not null =>
                ReferenceEquals(left.Value, right.Value),
            jv_kind.JV_KIND_NULL or jv_kind.JV_KIND_FALSE or jv_kind.JV_KIND_TRUE => true,
            jv_kind.JV_KIND_NUMBER => IdenticalNumbers(left, right),
            jv_kind.JV_KIND_STRING or jv_kind.JV_KIND_ARRAY or jv_kind.JV_KIND_OBJECT =>
                ReferenceEquals(left.Value, right.Value),
            _ => left.Value is null && right.Value is null,
        };

        // jq-1.8.2 src/jv.c: jv_identical() consumes both inputs.
        jv_free(left);
        jv_free(right);
        return identical;
    }

    // jq-1.8.2 src/jv.c: jv_get_refcnt().  Unallocated scalars report one.
    internal static int jv_get_refcnt(jv value)
    {
        return value.Kind switch
        {
            jv_kind.JV_KIND_INVALID when value.Value is jvp_invalid invalid =>
                invalid.Refcnt.Count,
            jv_kind.JV_KIND_NUMBER when ((JvNumber)value.Value!).IsLiteralAllocation =>
                jvp_literal_number_ptr(value).Refcnt!.Count,
            jv_kind.JV_KIND_STRING => jvp_string_ptr(value).Refcnt.Count,
            jv_kind.JV_KIND_ARRAY => jvp_array_ptr(value).Refcnt.Count,
            jv_kind.JV_KIND_OBJECT => jvp_object_ptr(value).Refcnt.Count,
            _ => 1,
        };
    }

    // jq-1.8.2 src/jv.c:jvp_number_equal() is exactly the canonical
    // comparison result, including its NaN behavior.
    internal static bool jv_number_equal(jv left, jv right) =>
        jvp_number_cmp(left, right) == 0;

    // jq-1.8.2 src/jv.c: jv_free().  Children are released iteratively so a
    // deeply nested jq value cannot consume the CLR call stack.  The switch
    // covers every allocated jq kind: invalid-with-message, literal number,
    // string, array, and object.
    internal static void jv_free(jv value)
    {
        List<jv>? pending = null;
        while (true)
        {
            if (value.Kind == jv_kind.JV_KIND_INVALID && value.Value is jvp_invalid invalid)
            {
                invalid.EnsureAlive();
                if (jvp_refcnt_dec(invalid.Refcnt))
                {
                    pending ??= [];
                    pending.Add(invalid.ReleaseStorage());
                }
            }
            else if (value.Kind == jv_kind.JV_KIND_NUMBER)
            {
                jvp_number_free(value);
            }
            else if (value.Kind == jv_kind.JV_KIND_STRING)
            {
                var stringStorage = jvp_string_ptr(value);
                if (jvp_refcnt_dec(stringStorage.Refcnt))
                {
                    stringStorage.ReleaseStorage();
                }
            }
            else if (value.Kind == jv_kind.JV_KIND_ARRAY)
            {
                var array = jvp_array_ptr(value);
                if (jvp_refcnt_dec(array.Refcnt))
                {
                    pending ??= [];
                    for (var index = 0; index < array.Length; index++)
                    {
                        pending.Add(array.Elements[index]);
                    }

                    array.ReleaseStorage();
                }
            }
            else if (value.Kind == jv_kind.JV_KIND_OBJECT)
            {
                var objectStorage = jvp_object_ptr(value);
                if (jvp_refcnt_dec(objectStorage.Refcnt))
                {
                    pending ??= [];
                    foreach (var slot in objectStorage.Elements)
                    {
                        if (slot.String.Kind == jv_kind.JV_KIND_NULL)
                        {
                            continue;
                        }

                        // Native jq directly releases the string allocation
                        // and queues the value. Queueing both preserves the
                        // same final ownership transitions without recursive
                        // managed calls.
                        pending.Add(slot.String);
                        pending.Add(slot.Value);
                    }

                    objectStorage.ReleaseStorage();
                }
            }

            if (pending is null || pending.Count == 0)
            {
                break;
            }

            var last = pending.Count - 1;
            value = pending[last];
            pending.RemoveAt(last);
        }
    }

    private static bool IdenticalNumbers(jv left, jv right)
    {
        var leftNumber = (JvNumber)left.Value!;
        var rightNumber = (JvNumber)right.Value!;
        leftNumber.EnsureAlive();
        rightNumber.EnsureAlive();
        if (leftNumber.IsLiteralAllocation || rightNumber.IsLiteralAllocation)
        {
            return ReferenceEquals(leftNumber, rightNumber);
        }

        return BitConverter.DoubleToInt64Bits(leftNumber.Value) ==
               BitConverter.DoubleToInt64Bits(rightNumber.Value);
    }

    private static string DecodeJqUtf8(ReadOnlySpan<byte> input)
    {
        var result = new StringBuilder(input.Length);
        var offset = 0;
        var codepoint = 0;
        int? next;
        while ((next = jvp_utf8_next(input, offset, ref codepoint)) is not null)
        {
            result.Append(
                codepoint == JVP_UTF8_INVALID_CODEPOINT
                    ? Rune.ReplacementChar.ToString()
                    : new Rune(codepoint).ToString());
            offset = next.Value;
        }

        return result.ToString();
    }

    private static int CountRunes(string value, int utf16Length)
    {
        var count = 0;
        foreach (var rune in value.AsSpan(0, utf16Length).EnumerateRunes())
        {
            _ = rune;
            count++;
        }

        return count;
    }

    private static void ClampSliceParameters(int length, ref int start, ref int end)
    {
        if (start < 0)
        {
            start = length + start;
        }

        if (end < 0)
        {
            end = length + end;
        }

        start = Math.Clamp(start, 0, length);
        end = Math.Min(end, length);
        if (end < start)
        {
            end = start;
        }
    }

    private static void ensure_kind(jv value, jv_kind expected, string parameterName)
    {
        if (value.Kind != expected)
        {
            throw new ArgumentException(
                $"Expected jq {jv_kind_name(expected)} value, got {jv_kind_name(value.Kind)}.",
                parameterName);
        }
    }

    private sealed class JvObjectMergeFrame(jv left, jv right, int depth, jv parentKey)
    {
        internal jv Result { get; set; } = left;

        internal jv Right { get; } = right;

        internal int Depth { get; } = depth;

        internal jv ParentKey { get; } = parentKey;

        internal int Iterator { get; set; } = JvObjectIterationNotStarted;
    }

    internal static JqRuntimeException JqTypeError(jv value, string message) =>
        new(jv_kind_name(value.Kind) + " (" + jv_dump_string_trunc(value, 30) + ") " + message);

}
