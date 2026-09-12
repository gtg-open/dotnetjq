// DOTNETJQ PORT MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream file: src/jv.h
// Upstream URL: https://github.com/jqlang/jq/blob/jq-1.8.2/src/jv.h
// Strategy: PORT
// Target file: src/DotNetJq/Port/src/jv.h.cs
// Substitutions: the C tagged union is represented by a managed value handle. The handle retains
// jq's byte-backed string allocation, invalid-message owner, allocated literal-number owner,
// array offset/size fields, and explicit logical refcounts so lifetime and copy-on-write decisions
// remain visible and source-comparable.
// Known differences: exact decimal literals are retained beside their binary64 projections, while
// ordinary jq arithmetic intentionally uses binary64 as upstream; CLR object references replace
// native allocation pointers without changing jq's logical ownership contract.

namespace DotNetJq.Port;

internal enum jv_kind
{
    JV_KIND_INVALID,
    JV_KIND_NULL,
    JV_KIND_FALSE,
    JV_KIND_TRUE,
    JV_KIND_NUMBER,
    JV_KIND_STRING,
    JV_KIND_ARRAY,
    JV_KIND_OBJECT,
}

// jq-1.8.2 src/jv.h:jv_print_flags. Keep the native bit layout because
// src/main.c composes indentation, coloring, sorting, and diagnostic flags
// arithmetically before handing them to jv_print.c.
[Flags]
internal enum jv_print_flags
{
    JV_PRINT_PRETTY = 1,
    JV_PRINT_ASCII = 2,
    JV_PRINT_COLOR = 4,
    JV_PRINT_COLOUR = JV_PRINT_COLOR,
    JV_PRINT_SORTED = 8,
    JV_PRINT_INVALID = 16,
    JV_PRINT_REFCOUNT = 32,
    JV_PRINT_TAB = 64,
    JV_PRINT_ISATTY = 128,
    JV_PRINT_SPACE0 = 256,
    JV_PRINT_SPACE1 = 512,
    JV_PRINT_SPACE2 = 1024,
}

internal sealed class JvNumber
{
    private double value;
    private string? literal;
    private ManagedDecimal? exactValue;

    internal JvNumber(double value, string? literal = null)
    {
        this.value = value;
        if (literal is not null)
        {
            var context = libjq.CreateJqDecimalContext();
            libjq.decContextClearStatus(context, libjq.DEC_Conversion_syntax);
            var parsed = libjq.decNumberFromString(new decNumber(), literal, context);
            if ((context.status & libjq.DEC_Conversion_syntax) == 0 &&
                !libjq.decNumberIsNaN(parsed))
            {
                // jq-1.8.2 src/jv.c:jvp_literal_number_alloc() initializes
                // num_double to NaN. jv_number_value() fills that cache lazily
                // through the 17-digit decNumberReduce()/jvp_strtod() bridge.
                this.value = double.NaN;
                exactValue = parsed.Value;
                Refcnt = new jv_refcnt();
            }
        }
    }

    internal JvNumber(ManagedDecimal exact)
    {
        if (exact.IsNaN)
        {
            throw new ArgumentException("A jq literal-number allocation cannot contain NaN.", nameof(exact));
        }

        value = double.NaN;
        exactValue = exact;
        Refcnt = new jv_refcnt();
    }

    // jq-1.8.2 src/jv.c:jvp_literal_number embeds jv_refcnt in allocated
    // decimal-literal storage. Native binary64 numbers leave this null and
    // therefore remain immediate scalar handles with an effective count of 1.
    internal jv_refcnt? Refcnt { get; }

    internal bool IsLiteralAllocation => Refcnt is not null;

    internal double Value
    {
        get
        {
            EnsureAlive();
            if (exactValue is { } exact && double.IsNaN(value))
            {
                value = libjq.jvp_literal_number_to_double(exact);
            }

            return value;
        }
    }

    internal string? Literal
    {
        get
        {
            EnsureAlive();
            if (exactValue is not { } exact)
            {
                return null;
            }

            if (exact.IsNaN)
            {
                return "null";
            }

            // jq-1.8.2 src/jv.c:jvp_literal_number_literal() deliberately
            // returns NULL for a decNumber infinity. The printer must then use
            // the binary64 fallback and clamp it to a valid JSON number.
            if (exact.IsInfinity)
            {
                return null;
            }

            // jvp_literal_number does not retain the lexical token. Its
            // literal_data cache is populated on first use by decNumberToString().
            return literal ??= libjq.decNumberToString(new decNumber(exact));
        }
    }

    internal ManagedDecimal? ExactValue
    {
        get
        {
            EnsureAlive();
            return exactValue;
        }
    }

    internal void EnsureAlive()
    {
        if (Refcnt is { Count: <= 0 })
        {
            throw new ObjectDisposedException(
                nameof(JvNumber),
                "The jq literal-number allocation has been released.");
        }
    }

    // jvp_number_free() has no child owners to release. Clearing the managed
    // payload makes accidental use after the final logical jv_free visible in
    // the same way as the other allocated jv storage ports.
    internal void ReleaseStorage()
    {
        value = default;
        literal = null;
        exactValue = null;
    }

    internal JvNumber Negate()
    {
        if (ExactValue is not { } exact)
        {
            return new JvNumber(-Value);
        }

        var context = libjq.CreateJqDecimalContext();
        var result = libjq.decNumberMinus(
            new decNumber(),
            new decNumber(exact),
            context);
        return new JvNumber(result.Value);
    }

    internal JvNumber Abs()
    {
        if (ExactValue is not { } exact)
        {
            return new JvNumber(Math.Abs(Value));
        }

        var context = libjq.CreateJqDecimalContext();
        var result = libjq.decNumberAbs(
            new decNumber(),
            new decNumber(exact),
            context);
        return new JvNumber(result.Value);
    }
}

internal readonly struct jv : IEquatable<jv>
{
    // jq-1.8.2 src/jv.h: struct jv.  Keep the array view metadata on the
    // handle, not on jvp_array, because jv_copy() must preserve distinct
    // slices that share one physical allocation.
    internal jv(
        jv_kind kind,
        object? value = null,
        ushort offset = 0,
        int size = 0)
    {
        Kind = kind;
        Value = value;
        Offset = offset;
        Size = size;
    }

    internal jv_kind Kind { get; }

    internal object? Value { get; }

    internal ushort Offset { get; }

    internal int Size { get; }

    internal bool IsValid => Kind != jv_kind.JV_KIND_INVALID;

    internal bool IsTruthy => Kind is not (jv_kind.JV_KIND_NULL or jv_kind.JV_KIND_FALSE or jv_kind.JV_KIND_INVALID);

    internal double NumberValue => ((JvNumber)Value!).Value;

    internal string StringValue => libjq.jvp_string_text(this);

    internal ArraySegment<jv> ArrayValue
    {
        get
        {
            var array = (jvp_array)Value!;
            array.EnsureAlive();
            return new ArraySegment<jv>(array.Elements, Offset, Size);
        }
    }

    // Managed compatibility view over jq's physical object slots.  Values are
    // borrowed: code that lets one escape the lifetime of this object must
    // acquire an owner with jv_copy(), exactly as with jvp_object_get_slot().
    internal IReadOnlyList<KeyValuePair<string, jv>> ObjectValue
    {
        get
        {
            var objectStorage = (jvp_object)Value!;
            objectStorage.EnsureAlive();
            return objectStorage.BorrowedView;
        }
    }

    // System.IEquatable<T> is a borrowing CLR contract, whereas jq's
    // jv_equal() consumes both arguments.  Acquire the two owners that the
    // jq-shaped comparison is allowed to consume.
    public bool Equals(jv other) =>
        libjq.jv_equal(libjq.jv_copy(this), libjq.jv_copy(other));

    public override bool Equals(object? obj) => obj is jv other && Equals(other);

    public override int GetHashCode() => libjq.jv_hash(this);

    public static bool operator ==(jv left, jv right) => left.Equals(right);

    public static bool operator !=(jv left, jv right) => !left.Equals(right);
}

internal static partial class libjq
{
    internal const int JV_PRINT_PRETTY = (int)jv_print_flags.JV_PRINT_PRETTY;
    internal const int JV_PRINT_ASCII = (int)jv_print_flags.JV_PRINT_ASCII;
    internal const int JV_PRINT_COLOR = (int)jv_print_flags.JV_PRINT_COLOR;
    internal const int JV_PRINT_COLOUR = (int)jv_print_flags.JV_PRINT_COLOUR;
    internal const int JV_PRINT_SORTED = (int)jv_print_flags.JV_PRINT_SORTED;
    internal const int JV_PRINT_INVALID = (int)jv_print_flags.JV_PRINT_INVALID;
    internal const int JV_PRINT_REFCOUNT = (int)jv_print_flags.JV_PRINT_REFCOUNT;
    internal const int JV_PRINT_TAB = (int)jv_print_flags.JV_PRINT_TAB;
    internal const int JV_PRINT_ISATTY = (int)jv_print_flags.JV_PRINT_ISATTY;
    internal const int JV_PRINT_SPACE0 = (int)jv_print_flags.JV_PRINT_SPACE0;
    internal const int JV_PRINT_SPACE1 = (int)jv_print_flags.JV_PRINT_SPACE1;
    internal const int JV_PRINT_SPACE2 = (int)jv_print_flags.JV_PRINT_SPACE2;

    // jq-1.8.2 src/jv.h:JV_PRINT_INDENT_FLAGS(). Values outside 0..7 select
    // tab indentation; main.c intentionally uses -1 as that spelling.
    internal static int JV_PRINT_INDENT_FLAGS(int indentation) =>
        indentation is < 0 or > 7
            ? JV_PRINT_TAB | JV_PRINT_PRETTY
            : (indentation << 8) | JV_PRINT_PRETTY;
}
