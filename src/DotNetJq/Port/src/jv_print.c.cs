// DOTNETJQ PORT MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream file: src/jv_print.c
// Upstream URL: https://github.com/jqlang/jq/blob/jq-1.8.2/src/jv_print.c
// Strategy: PORT
// Target file: src/DotNetJq/Port/src/jv_print.c.cs
//
// Direct surface: jq_set_colors, jvp_dump_string, put_indent, jv_dump_term,
// jv_dumpf, jv_dump_string, and jv_dump_string_trunc. The traversal retains
// jq's consuming child owners, sorted keyset, refcount observations, color
// boundaries, exact UTF-8 quoting, numeric dtoa path, and 10,000-depth guard.
// Managed substitutions: an iterative owner stack replaces C recursion;
// Stream and an in-memory UTF-8 sink replace FILE* and jv-string sinks. The
// process-global, deliberately unsynchronized color table retains native jq's
// state transitions. The Stream sink is byte-oriented even when
// JV_PRINT_ISATTY is set, preserving dotnetjq's documented Windows
// binary-standard-stream policy.

using System.Buffers;
using System.Text;
using System.Text.Json;

namespace DotNetJq.Port;

internal sealed class jv_print_colors(byte[][] values)
{
    internal ReadOnlySpan<byte> ColorFor(jv_kind kind) => values[(int)kind - 1];

    internal ReadOnlySpan<byte> FieldColor => values[7];

    internal void Set(int index, byte[] value) => values[index] = value;
}

internal static partial class libjq
{
    private const int MaxPrintDepth = 10_000;
    private const int StreamSinkBufferSize = 16 * 1024;

    private static readonly byte[][] DefaultPrintColorValues =
    [
        "\u001b[0;90m"u8.ToArray(),
        "\u001b[0;39m"u8.ToArray(),
        "\u001b[0;39m"u8.ToArray(),
        "\u001b[0;39m"u8.ToArray(),
        "\u001b[0;32m"u8.ToArray(),
        "\u001b[1;39m"u8.ToArray(),
        "\u001b[1;39m"u8.ToArray(),
        "\u001b[1;34m"u8.ToArray(),
    ];

    // jq-1.8.2 src/jv_print.c:colors[]. This table is intentionally mutable,
    // process-global, and unsynchronized. jq_set_colors() replaces entries in
    // order, and every dump reads the live table just as native jq does.
    private static readonly jv_print_colors ProcessPrintColors = new(
        [.. DefaultPrintColorValues]);

    // jq-1.8.2 src/jv_print.c:jq_set_colors(). NULL preserves the current
    // table, an empty C string restores every default, and invalid input leaves
    // the complete prior table untouched.
    internal static bool jq_set_colors(string? codeString)
    {
        if (codeString is null)
        {
            return true;
        }

        var nul = codeString.IndexOf('\0', StringComparison.Ordinal);
        if (nul >= 0)
        {
            codeString = codeString[..nul];
        }

        var starts = new int[9];
        var cursor = 0;
        var numberOfColors = 0;
        while (true)
        {
            starts[numberOfColors] = cursor;
            while (cursor < codeString.Length &&
                   codeString[cursor] is >= '0' and <= '9' or ';')
            {
                cursor++;
            }

            if (cursor == codeString.Length || numberOfColors + 1 >= 8)
            {
                break;
            }

            if (codeString[cursor] != ':')
            {
                return false;
            }

            cursor++;
            numberOfColors++;
        }

        if (starts[numberOfColors] != cursor)
        {
            numberOfColors++;
            starts[numberOfColors] = cursor + 1;
        }
        else if (numberOfColors == 0)
        {
            ResetPrintColors();
            return true;
        }

        var configured = new byte[numberOfColors][];
        for (var index = 0; index < numberOfColors; index++)
        {
            var length = starts[index + 1] - 1 - starts[index];
            var encoded = new byte[length + 3];
            encoded[0] = 0x1b;
            encoded[1] = (byte)'[';
            for (var offset = 0; offset < length; offset++)
            {
                // Validation above limits every code unit to ASCII digits or
                // ';', so this is the exact byte copy performed by native memcpy.
                encoded[offset + 2] = (byte)codeString[starts[index] + offset];
            }

            encoded[^1] = (byte)'m';
            configured[index] = encoded;
        }

        var configuredIndex = 0;
        for (; configuredIndex < configured.Length; configuredIndex++)
        {
            ProcessPrintColors.Set(configuredIndex, configured[configuredIndex]);
        }

        for (; configuredIndex < DefaultPrintColorValues.Length; configuredIndex++)
        {
            ProcessPrintColors.Set(configuredIndex, DefaultPrintColorValues[configuredIndex]);
        }

        return true;
    }

    private static void ResetPrintColors()
    {
        for (var index = 0; index < DefaultPrintColorValues.Length; index++)
        {
            ProcessPrintColors.Set(index, DefaultPrintColorValues[index]);
        }
    }

    // jq-1.8.2 src/jv_print.c:jv_dump_string() consumes its jv argument.
    // The managed port changes only the return representation to System.String.
    internal static string jv_dump_string(jv value) => jv_dump_string(value, 0);

    internal static string jv_dump_string(jv value, int flags)
    {
        var sink = new BufferPrintSink();
        jv_dump_term(tsd_dtoa_context_get(), value, flags, sink, ProcessPrintColors);
        return sink.GetString();
    }

    // Explicit managed borrowing adapters. Callers retaining their jq owner
    // spell the same jv_copy() which a native caller would pass.
    internal static string jv_dump_string_borrowed(jv value) =>
        jv_dump_string(jv_copy(value));

    internal static string jv_dump_string_borrowed(jv value, int flags) =>
        jv_dump_string(jv_copy(value), flags);

    // Managed byte-buffer counterpart for compact jv_dump_string(). This
    // consumes value and exposes the canonical printer sink without decoding
    // it to UTF-16 and encoding it again at public JSON boundaries.
    internal static byte[] jv_dump_bytes(jv value)
    {
        var sink = new BufferPrintSink();
        jv_dump_term(tsd_dtoa_context_get(), value, 0, sink, ProcessPrintColors);
        return sink.ToArray();
    }

    internal static byte[] jv_dump_bytes_borrowed(jv value) =>
        jv_dump_bytes(jv_copy(value));

    // Managed FILE* counterpart. Like native jv_dumpf(), this consumes value.
    // A small pooled buffer preserves the buffered-stdio write shape instead
    // of issuing one Stream.Write syscall for every punctuation byte.
    internal static void jv_dumpf(jv value, Stream stream, int flags)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var sink = new StreamPrintSink(stream);
        jv_dump_term(tsd_dtoa_context_get(), value, flags, sink, ProcessPrintColors);
        sink.Complete();
    }

    internal static string jv_dump_string_trunc(jv value, int bufferSize)
    {
        var bytes = jv_dump_string_trunc_bytes(value, bufferSize);
        // Production jq diagnostics use buffers of at least eight bytes, where
        // upstream backtracks to a complete UTF-8 scalar. The byte companion
        // below retains native partial prefixes for direct tiny-buffer tests.
        return Encoding.UTF8.GetString(bytes);
    }

    internal static string jv_dump_string_trunc_borrowed(jv value, int bufferSize) =>
        jv_dump_string_trunc(jv_copy(value), bufferSize);

    // The returned payload excludes native's trailing NUL; bufferSize includes it.
    internal static byte[] jv_dump_string_trunc_bytes(jv value, int bufferSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bufferSize, 1);
        if (value.Kind == jv_kind.JV_KIND_STRING &&
            jv_string_length_bytes(jv_copy(value)) > bufferSize)
        {
            value = jv_string_slice(value, 0, bufferSize);
        }

        var sink = new BufferPrintSink();
        jv_dump_term(tsd_dtoa_context_get(), value, 0, sink, ProcessPrintColors);
        var bytes = sink.ToArray();
        if (bytes.Length > bufferSize - 1 && bufferSize >= 8)
        {
            byte delimiter = bytes[0] switch
            {
                (byte)'"' => (byte)'"',
                (byte)'[' => (byte)']',
                (byte)'{' => (byte)'}',
                _ => 0,
            };
            var prefixLength = bufferSize - (delimiter == 0 ? 4 : 5);
            if (jvp_utf8_backtrack(bytes, prefixLength, 0) is { } boundary)
            {
                prefixLength = boundary;
            }

            var result = new byte[prefixLength + 3 + (delimiter == 0 ? 0 : 1)];
            bytes.AsSpan(0, prefixLength).CopyTo(result);
            result[prefixLength] = (byte)'.';
            result[prefixLength + 1] = (byte)'.';
            result[prefixLength + 2] = (byte)'.';
            if (delimiter != 0)
            {
                result[prefixLength + 3] = delimiter;
            }

            return result;
        }

        return bytes[..Math.Min(bytes.Length, bufferSize - 1)];
    }

    internal static JsonElement jv_to_json_element(jv value) =>
        jv_to_json_element(jv_dump_bytes_borrowed(value));

    internal static JsonElement jv_to_json_element(ReadOnlyMemory<byte> utf8Json)
    {
        using var document = JsonDocument.Parse(
            utf8Json,
            new JsonDocumentOptions { MaxDepth = 10_000 });
        return document.RootElement.Clone();
    }

    // Iterative counterpart of jq-1.8.2 jv_dump_term(). Every current value,
    // container frame, sorted keyset, key, and child is an explicit jq owner.
    private static void jv_dump_term(
        dtoa_context dtoaContext,
        jv value,
        int flags,
        PrintSink sink,
        jv_print_colors colors)
    {
        var containers = new Stack<PrintFrame>();
        var current = value;
        var depth = 0;
        try
        {
            while (true)
            {
                if (BeginValue(
                    dtoaContext,
                    sink,
                    colors,
                    flags,
                    depth,
                    containers,
                    ref current))
                {
                    depth++;
                    continue;
                }

                if (!AdvanceContainer(
                    dtoaContext,
                    sink,
                    colors,
                    flags,
                    containers,
                    ref current,
                    out depth))
                {
                    return;
                }
            }
        }
        finally
        {
            jv_free(current);
            foreach (var frame in containers)
            {
                frame.Release();
            }
        }
    }

    private static bool BeginValue(
        dtoa_context dtoaContext,
        PrintSink sink,
        jv_print_colors colors,
        int flags,
        int depth,
        Stack<PrintFrame> containers,
        ref jv current)
    {
        var kind = current.Kind;
        var refcount = (flags & JV_PRINT_REFCOUNT) != 0
            ? jv_get_refcnt(current) - 1d
            : -1d;
        var hasColor = (flags & JV_PRINT_COLOR) != 0 && kind != jv_kind.JV_KIND_INVALID;
        if (hasColor)
        {
            put_buf(colors.ColorFor(kind), sink);
        }

        if (depth > MaxPrintDepth)
        {
            put_buf("<skipped: too deep>"u8, sink);
            CompleteOwnedValue(ref current, hasColor, sink);
            return false;
        }

        switch (kind)
        {
            case jv_kind.JV_KIND_INVALID:
                if ((flags & JV_PRINT_INVALID) == 0)
                {
                    throw new JqRuntimeException("Cannot serialize an invalid jq value");
                }

                var message = jv_invalid_get_msg(jv_copy(current));
                if (message.Kind == jv_kind.JV_KIND_STRING)
                {
                    put_buf("<invalid:"u8, sink);
                    jvp_dump_string(message, asciiOnly: true, sink);
                    put_char((byte)'>', sink);
                }
                else
                {
                    put_buf("<invalid>"u8, sink);
                }

                // Pinned jq does not jv_free(msg) after jvp_dump_string(). Keep
                // that observable logical ownership trace exactly; the CLR can
                // still collect an unreachable managed allocation.
                CompleteOwnedValue(ref current, hasColor: false, sink);
                return false;

            case jv_kind.JV_KIND_NULL:
                put_buf("null"u8, sink);
                CompleteOwnedValue(ref current, hasColor, sink);
                return false;

            case jv_kind.JV_KIND_FALSE:
                put_buf("false"u8, sink);
                CompleteOwnedValue(ref current, hasColor, sink);
                return false;

            case jv_kind.JV_KIND_TRUE:
                put_buf("true"u8, sink);
                CompleteOwnedValue(ref current, hasColor, sink);
                return false;

            case jv_kind.JV_KIND_NUMBER:
                if (jvp_number_is_nan(current))
                {
                    // Native recursively prints a null term inside the already
                    // opened number color, producing both color boundaries.
                    var nullHasColor = (flags & JV_PRINT_COLOR) != 0;
                    if (nullHasColor)
                    {
                        put_buf(colors.ColorFor(jv_kind.JV_KIND_NULL), sink);
                    }

                    put_buf("null"u8, sink);
                    if (nullHasColor)
                    {
                        put_buf("\u001b[0m"u8, sink);
                    }
                }
                else
                {
                    put_str(FormatNumber(dtoaContext, current), sink);
                }

                CompleteOwnedValue(ref current, hasColor, sink);
                return false;

            case jv_kind.JV_KIND_STRING:
                jvp_dump_string(current, (flags & JV_PRINT_ASCII) != 0, sink);
                if ((flags & JV_PRINT_REFCOUNT) != 0)
                {
                    put_refcnt(dtoaContext, refcount, sink);
                }

                CompleteOwnedValue(ref current, hasColor, sink);
                return false;

            case jv_kind.JV_KIND_ARRAY:
                var arrayLength = jv_array_length(jv_copy(current));
                if (arrayLength == 0)
                {
                    put_buf("[]"u8, sink);
                    CompleteOwnedValue(ref current, hasColor, sink);
                    return false;
                }

                put_char((byte)'[', sink);
                var arrayFrame = PrintFrame.Array(current, depth, arrayLength, refcount, hasColor);
                containers.Push(arrayFrame);
                current = jv_invalid();
                current = jv_array_get(jv_copy(arrayFrame.Value), 0);
                WriteArrayElementPrefix(arrayFrame, first: true, flags, sink, colors);
                return true;

            case jv_kind.JV_KIND_OBJECT:
                if (jv_object_length(jv_copy(current)) == 0)
                {
                    put_buf("{}"u8, sink);
                    CompleteOwnedValue(ref current, hasColor, sink);
                    return false;
                }

                put_char((byte)'{', sink);
                var keyset = (flags & JV_PRINT_SORTED) != 0
                    ? jv_keys(jv_copy(current))
                    : jv_invalid();
                var objectFrame = PrintFrame.Object(current, depth, refcount, hasColor, keyset);
                containers.Push(objectFrame);
                current = jv_invalid();
                LoadObjectMember(objectFrame, out var firstKey, out current);
                WriteObjectMemberPrefix(
                    objectFrame,
                    firstKey,
                    first: true,
                    flags,
                    sink,
                    colors);
                return true;

            default:
                throw new JqRuntimeException("Cannot serialize an unknown jq value");
        }
    }

    private static bool AdvanceContainer(
        dtoa_context dtoaContext,
        PrintSink sink,
        jv_print_colors colors,
        int flags,
        Stack<PrintFrame> containers,
        ref jv current,
        out int depth)
    {
        while (containers.TryPeek(out var frame))
        {
            if (frame.Kind == jv_kind.JV_KIND_ARRAY)
            {
                frame.Index++;
                if (frame.Index < frame.Length)
                {
                    current = jv_array_get(jv_copy(frame.Value), frame.Index);
                    WriteArrayElementPrefix(frame, first: false, flags, sink, colors);
                    depth = frame.Depth + 1;
                    return true;
                }
            }
            else
            {
                MoveToNextObjectMember(frame);
                if (ObjectMemberIsValid(frame))
                {
                    LoadObjectMember(frame, out var key, out current);
                    WriteObjectMemberPrefix(
                        frame,
                        key,
                        first: false,
                        flags,
                        sink,
                        colors);
                    depth = frame.Depth + 1;
                    return true;
                }

                if (frame.Sorted)
                {
                    jv_free(frame.Keyset);
                    frame.Keyset = jv_invalid();
                }
            }

            if ((flags & JV_PRINT_PRETTY) != 0)
            {
                put_char((byte)'\n', sink);
                put_indent(frame.Depth, flags, sink);
            }

            if (frame.HasColor)
            {
                put_buf(colors.ColorFor(frame.Kind), sink);
            }

            put_char(frame.Kind == jv_kind.JV_KIND_ARRAY ? (byte)']' : (byte)'}', sink);
            if ((flags & JV_PRINT_REFCOUNT) != 0)
            {
                put_refcnt(dtoaContext, frame.Refcount, sink);
            }

            jv_free(frame.Value);
            frame.Value = jv_invalid();
            if (frame.HasColor)
            {
                put_buf("\u001b[0m"u8, sink);
            }

            _ = containers.Pop();
        }

        depth = 0;
        return false;
    }

    private static void WriteArrayElementPrefix(
        PrintFrame frame,
        bool first,
        int flags,
        PrintSink sink,
        jv_print_colors colors)
    {
        if (!first)
        {
            if (frame.HasColor)
            {
                put_buf(colors.ColorFor(frame.Kind), sink);
            }

            put_char((byte)',', sink);
        }

        if (frame.HasColor)
        {
            put_buf("\u001b[0m"u8, sink);
        }

        if ((flags & JV_PRINT_PRETTY) != 0)
        {
            put_char((byte)'\n', sink);
            put_indent(frame.Depth + 1, flags, sink);
        }
    }

    private static void WriteObjectMemberPrefix(
        PrintFrame frame,
        jv key,
        bool first,
        int flags,
        PrintSink sink,
        jv_print_colors colors)
    {
        try
        {
            if (!first)
            {
                if (frame.HasColor)
                {
                    put_buf(colors.ColorFor(frame.Kind), sink);
                }

                put_char((byte)',', sink);
            }

            if (frame.HasColor)
            {
                put_buf("\u001b[0m"u8, sink);
            }

            if ((flags & JV_PRINT_PRETTY) != 0)
            {
                put_char((byte)'\n', sink);
                put_indent(frame.Depth + 1, flags, sink);
            }

            if (frame.HasColor)
            {
                put_buf(colors.FieldColor, sink);
            }

            jvp_dump_string(key, (flags & JV_PRINT_ASCII) != 0, sink);
        }
        finally
        {
            jv_free(key);
        }

        if (frame.HasColor)
        {
            put_buf("\u001b[0m"u8, sink);
            put_buf(colors.ColorFor(frame.Kind), sink);
        }

        put_char((byte)':', sink);
        if (frame.HasColor)
        {
            put_buf("\u001b[0m"u8, sink);
        }

        if ((flags & JV_PRINT_PRETTY) != 0)
        {
            put_char((byte)' ', sink);
        }
    }

    private static void LoadObjectMember(PrintFrame frame, out jv key, out jv value)
    {
        key = frame.Sorted
            ? jv_array_get(jv_copy(frame.Keyset), frame.Index)
            : jv_object_iter_key(frame.Value, frame.Iterator);
        try
        {
            value = frame.Sorted
                ? jv_object_get(jv_copy(frame.Value), jv_copy(key))
                : jv_object_iter_value(frame.Value, frame.Iterator);
        }
        catch
        {
            jv_free(key);
            throw;
        }
    }

    private static void MoveToNextObjectMember(PrintFrame frame)
    {
        if (frame.Sorted)
        {
            frame.Index++;
        }
        else
        {
            frame.Iterator = jv_object_iter_next(frame.Value, frame.Iterator);
        }
    }

    private static bool ObjectMemberIsValid(PrintFrame frame) => frame.Sorted
        ? frame.Index < frame.Length
        : jv_object_iter_valid(frame.Value, frame.Iterator);

    private static void CompleteOwnedValue(ref jv value, bool hasColor, PrintSink sink)
    {
        jv_free(value);
        value = jv_invalid();
        if (hasColor)
        {
            put_buf("\u001b[0m"u8, sink);
        }
    }

    private static void jvp_dump_string(jv value, bool asciiOnly, PrintSink sink)
    {
        var data = jvp_string_data(value);
        var offset = 0;
        var codepoint = 0;
        put_char((byte)'"', sink);
        while (jvp_utf8_next(data, offset, ref codepoint) is { } next)
        {
            if (codepoint == JVP_UTF8_INVALID_CODEPOINT)
            {
                throw new InvalidOperationException("A jq string contained invalid UTF-8 storage.");
            }

            if (codepoint is >= 0x20 and <= 0x7e)
            {
                if (codepoint is '"' or '\\')
                {
                    put_char((byte)'\\', sink);
                }

                put_char((byte)codepoint, sink);
            }
            else if (codepoint < 0x20 || codepoint == 0x7f)
            {
                switch (codepoint)
                {
                    case '\b': put_buf("\\b"u8, sink); break;
                    case '\t': put_buf("\\t"u8, sink); break;
                    case '\r': put_buf("\\r"u8, sink); break;
                    case '\n': put_buf("\\n"u8, sink); break;
                    case '\f': put_buf("\\f"u8, sink); break;
                    default: put_unicode_escape(codepoint, sink); break;
                }
            }
            else if (asciiOnly)
            {
                put_unicode_escape(codepoint, sink);
            }
            else
            {
                put_buf(data[offset..next], sink);
            }

            offset = next;
        }

        put_char((byte)'"', sink);
    }

    private static void put_unicode_escape(int codepoint, PrintSink sink)
    {
        Span<byte> escaped = stackalloc byte[12];
        if (codepoint <= 0xffff)
        {
            escaped[0] = (byte)'\\';
            escaped[1] = (byte)'u';
            put_hex4(codepoint, escaped[2..6]);
            put_buf(escaped[..6], sink);
            return;
        }

        var adjusted = codepoint - 0x10000;
        escaped[0] = (byte)'\\';
        escaped[1] = (byte)'u';
        put_hex4(0xd800 | ((adjusted & 0xffc00) >> 10), escaped[2..6]);
        escaped[6] = (byte)'\\';
        escaped[7] = (byte)'u';
        put_hex4(0xdc00 | (adjusted & 0x003ff), escaped[8..12]);
        put_buf(escaped, sink);
    }

    private static void put_hex4(int value, Span<byte> destination)
    {
        const string Hex = "0123456789abcdef";
        destination[0] = (byte)Hex[(value >> 12) & 0xf];
        destination[1] = (byte)Hex[(value >> 8) & 0xf];
        destination[2] = (byte)Hex[(value >> 4) & 0xf];
        destination[3] = (byte)Hex[value & 0xf];
    }

    private static void put_refcnt(
        dtoa_context dtoaContext,
        double refcount,
        PrintSink sink)
    {
        put_buf(" ("u8, sink);
        put_str(jvp_dtoa_fmt(dtoaContext, refcount), sink);
        put_char((byte)')', sink);
    }

    private static void put_indent(int depth, int flags, PrintSink sink)
    {
        var character = (flags & JV_PRINT_TAB) != 0 ? (byte)'\t' : (byte)' ';
        var count = (flags & JV_PRINT_TAB) != 0
            ? depth
            : depth * ((flags & (JV_PRINT_SPACE0 | JV_PRINT_SPACE1 | JV_PRINT_SPACE2)) >> 8);
        Span<byte> buffer = stackalloc byte[64];
        buffer.Fill(character);
        while (count > 0)
        {
            var length = Math.Min(count, buffer.Length);
            put_buf(buffer[..length], sink);
            count -= length;
        }
    }

    private static void put_char(byte value, PrintSink sink)
    {
        Span<byte> buffer = stackalloc byte[1];
        buffer[0] = value;
        sink.Write(buffer);
    }

    private static void put_buf(ReadOnlySpan<byte> value, PrintSink sink) => sink.Write(value);

    private static void put_str(string value, PrintSink sink)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        byte[]? rented = null;
        Span<byte> buffer = byteCount <= 128
            ? stackalloc byte[byteCount]
            : (rented = ArrayPool<byte>.Shared.Rent(byteCount));
        try
        {
            var written = Encoding.UTF8.GetBytes(value, buffer);
            put_buf(buffer[..written], sink);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    // Frozen jq-1.8.2 src/jv_print.c:jv_dump_term() number arm. Keep the
    // mapped dtoa calls unchanged when extending presentation flags.
    private static string FormatNumber(dtoa_context dtoaContext, jv number)
    {
        if (jvp_number_is_nan(number))
        {
            return "null";
        }

        var literalData = jv_number_get_literal(number);
        if (literalData is not null)
        {
            return literalData;
        }

        var value = jv_number_value(number);
        if (double.IsNaN(value))
        {
            return "null";
        }

        // JSON has no infinities. Upstream normalizes only the binary64
        // fallback; finite decNumber literals were returned above unchanged.
        if (value > double.MaxValue)
        {
            value = double.MaxValue;
        }
        else if (value < -double.MaxValue)
        {
            value = -double.MaxValue;
        }

        return jvp_dtoa_fmt(dtoaContext, value);
    }

    private abstract class PrintSink
    {
        internal abstract void Write(ReadOnlySpan<byte> value);
    }

    private sealed class BufferPrintSink : PrintSink
    {
        private readonly ArrayBufferWriter<byte> output = new();

        internal override void Write(ReadOnlySpan<byte> value)
        {
            value.CopyTo(output.GetSpan(value.Length));
            output.Advance(value.Length);
        }

        internal string GetString() => Encoding.UTF8.GetString(output.WrittenSpan);

        internal byte[] ToArray() => output.WrittenSpan.ToArray();
    }

    private sealed class StreamPrintSink : PrintSink, IDisposable
    {
        private readonly Stream stream;
        private byte[]? buffer;
        private int count;

        internal StreamPrintSink(Stream stream)
        {
            this.stream = stream;
            buffer = ArrayPool<byte>.Shared.Rent(StreamSinkBufferSize);
        }

        internal override void Write(ReadOnlySpan<byte> value)
        {
            var activeBuffer = buffer ?? throw new ObjectDisposedException(nameof(StreamPrintSink));
            while (!value.IsEmpty)
            {
                if (count == 0 && value.Length >= activeBuffer.Length)
                {
                    stream.Write(value);
                    return;
                }

                var copied = Math.Min(value.Length, activeBuffer.Length - count);
                value[..copied].CopyTo(activeBuffer.AsSpan(count));
                count += copied;
                value = value[copied..];
                if (count == activeBuffer.Length)
                {
                    FlushBuffer(activeBuffer);
                }
            }
        }

        internal void Complete()
        {
            var activeBuffer = buffer ?? throw new ObjectDisposedException(nameof(StreamPrintSink));
            FlushBuffer(activeBuffer);
        }

        public void Dispose()
        {
            if (buffer is not { } activeBuffer)
            {
                return;
            }

            buffer = null;
            count = 0;
            ArrayPool<byte>.Shared.Return(activeBuffer);
        }

        private void FlushBuffer(byte[] activeBuffer)
        {
            if (count == 0)
            {
                return;
            }

            stream.Write(activeBuffer.AsSpan(0, count));
            count = 0;
        }
    }

    private sealed class PrintFrame
    {
        private PrintFrame(
            jv value,
            int depth,
            int length,
            double refcount,
            bool hasColor,
            jv keyset,
            int iterator)
        {
            Value = value;
            Depth = depth;
            Length = length;
            Refcount = refcount;
            HasColor = hasColor;
            Keyset = keyset;
            Iterator = iterator;
        }

        internal jv Value { get; set; }

        internal jv_kind Kind => Value.Kind;

        internal int Depth { get; }

        internal int Length { get; }

        internal double Refcount { get; }

        internal bool HasColor { get; }

        internal bool Sorted => Keyset.IsValid;

        internal jv Keyset { get; set; }

        internal int Index { get; set; }

        internal int Iterator { get; set; }

        internal static PrintFrame Array(
            jv value,
            int depth,
            int length,
            double refcount,
            bool hasColor) =>
            new(value, depth, length, refcount, hasColor, jv_invalid(), iterator: -1);

        internal static PrintFrame Object(
            jv value,
            int depth,
            double refcount,
            bool hasColor,
            jv keyset)
        {
            var sorted = keyset.IsValid;
            var length = sorted ? jv_array_length(jv_copy(keyset)) : 0;
            var iterator = sorted ? -1 : jv_object_iter(value);
            return new PrintFrame(value, depth, length, refcount, hasColor, keyset, iterator);
        }

        internal void Release()
        {
            jv_free(Keyset);
            Keyset = jv_invalid();
            jv_free(Value);
            Value = jv_invalid();
        }
    }
}
