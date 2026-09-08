// DOTNETJQ PORT MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream file: src/jv_unicode.c
// Upstream URL: https://github.com/jqlang/jq/blob/jq-1.8.2/src/jv_unicode.c
// Shared upstream file: src/jv_utf8_tables.h
// Shared upstream URL: https://github.com/jqlang/jq/blob/jq-1.8.2/src/jv_utf8_tables.h
// Strategy: PROXY
// Target file: src/DotNetJq/Port/src/jv_unicode.c.cs
// UPSTREAM COMPONENT: jq's UTF-8 validation, traversal, offset, explode, and implode helpers.
// REPLACEMENT: .NET Rune APIs plus span-based jq-compatible UTF-8 traversal.
// WHY: managed spans and scalar APIs replace native pointer arithmetic without changing jq-facing offsets.
// BEHAVIORAL CONTRACT: preserve upstream codepoint validation, replacement, byte offsets, and traversal results.
// KNOWN DIFFERENCES: pointer arithmetic is expressed as checked byte offsets, and invalid ranges throw managed exceptions.
// TESTS COVERING THE SUBSTITUTION: UnicodeCompatibilityTests and upstream Unicode fixture cases.

using System.Text;

namespace DotNetJq.Port;

internal static partial class libjq
{
    private const int Utf8ContinuationByte = 0xFF;

    // jvp_utf8_backtrack returns the beginning of the last codepoint in the
    // string, assuming that start is the last byte in the string.
    internal static int? jvp_utf8_backtrack(ReadOnlySpan<byte> input, int start, int min)
    {
        var ignoredMissingBytes = 0;
        return jvp_utf8_backtrack(input, start, min, ref ignoredMissingBytes);
    }

    // If the last codepoint is incomplete, missingBytes is updated with the
    // number of missing bytes. It remains unchanged for an invalid sequence.
    internal static int? jvp_utf8_backtrack(
        ReadOnlySpan<byte> input,
        int start,
        int min,
        ref int missingBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(min);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(min, input.Length);
        ArgumentOutOfRangeException.ThrowIfLessThan(start, min);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(start, input.Length);

        if (min == start)
        {
            return min;
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(start, input.Length);

        var seen = 1;
        var length = Utf8CodingLength(input[start]);
        while (length == Utf8ContinuationByte)
        {
            if (start == min)
            {
                break;
            }

            start--;
            seen++;
            length = Utf8CodingLength(input[start]);
        }

        if (length is 0 or Utf8ContinuationByte || length - seen < 0)
        {
            return null;
        }

        missingBytes = length - seen;
        return start;
    }

    // Returns the byte offset immediately after the next sequence, or null at
    // the end. Invalid sequences produce -1 while retaining jq's byte advance.
    internal static int? jvp_utf8_next(ReadOnlySpan<byte> input, int offset, ref int codepoint)
    {
        if ((uint)offset > (uint)input.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        if (offset == input.Length)
        {
            return null;
        }

        var decoded = JVP_UTF8_INVALID_CODEPOINT;
        var first = input[offset];
        var length = Utf8CodingLength(first);
        var remaining = input.Length - offset;

        if ((first & 0x80) == 0)
        {
            decoded = first;
            length = 1;
        }
        else if (length is 0 or Utf8ContinuationByte)
        {
            length = 1;
        }
        else if (length > remaining)
        {
            length = remaining;
        }
        else
        {
            decoded = first & Utf8CodingBits(first);
            for (var i = 1; i < length; i++)
            {
                var next = input[offset + i];
                if (Utf8CodingLength(next) != Utf8ContinuationByte)
                {
                    decoded = JVP_UTF8_INVALID_CODEPOINT;
                    length = i;
                    break;
                }

                decoded = (decoded << 6) | (next & 0x3F);
            }

            if (decoded < Utf8FirstCodepoint(length) ||
                decoded is >= 0xD800 and <= 0xDFFF ||
                decoded > JVP_UNICODE_MAX_CODEPOINT)
            {
                decoded = JVP_UTF8_INVALID_CODEPOINT;
            }
        }

        codepoint = decoded;
        return offset + length;
    }

    internal static bool jvp_utf8_is_valid(ReadOnlySpan<byte> input)
    {
        var offset = 0;
        var codepoint = 0;
        int? next;
        while ((next = jvp_utf8_next(input, offset, ref codepoint)) is not null)
        {
            if (codepoint == JVP_UTF8_INVALID_CODEPOINT)
            {
                return false;
            }

            offset = next.Value;
        }

        return true;
    }

    // Assumes startchar is the first byte of a valid character sequence, as
    // does the upstream helper.
    internal static int jvp_utf8_decode_length(byte startchar)
    {
        if ((startchar & 0x80) == 0)
        {
            return 1;
        }

        if ((startchar & 0xE0) == 0xC0)
        {
            return 2;
        }

        return (startchar & 0xF0) == 0xE0 ? 3 : 4;
    }

    internal static int jvp_utf8_encode_length(int codepoint)
    {
        if (codepoint <= 0x7F)
        {
            return 1;
        }

        if (codepoint <= 0x7FF)
        {
            return 2;
        }

        return codepoint <= 0xFFFF ? 3 : 4;
    }

    internal static int jvp_utf8_encode(int codepoint, Span<byte> output)
    {
        if (codepoint is < 0 or > JVP_UNICODE_MAX_CODEPOINT)
        {
            throw new ArgumentOutOfRangeException(nameof(codepoint));
        }

        var length = jvp_utf8_encode_length(codepoint);
        if (output.Length < length)
        {
            throw new ArgumentException("The output span is too small for the encoded codepoint.", nameof(output));
        }

        if (codepoint <= 0x7F)
        {
            output[0] = (byte)codepoint;
        }
        else if (codepoint <= 0x7FF)
        {
            output[0] = (byte)(0xC0 + ((codepoint & 0x7C0) >> 6));
            output[1] = (byte)(0x80 + (codepoint & 0x03F));
        }
        else if (codepoint <= 0xFFFF)
        {
            output[0] = (byte)(0xE0 + ((codepoint & 0xF000) >> 12));
            output[1] = (byte)(0x80 + ((codepoint & 0x0FC0) >> 6));
            output[2] = (byte)(0x80 + (codepoint & 0x003F));
        }
        else
        {
            output[0] = (byte)(0xF0 + ((codepoint & 0x1C0000) >> 18));
            output[1] = (byte)(0x80 + ((codepoint & 0x03F000) >> 12));
            output[2] = (byte)(0x80 + ((codepoint & 0x000FC0) >> 6));
            output[3] = (byte)(0x80 + (codepoint & 0x00003F));
        }

        return length;
    }

    internal static bool jvp_codepoint_is_whitespace(int codepoint) =>
        codepoint is >= 0x0009 and <= 0x000D ||
        codepoint is 0x0020 or 0x0085 or 0x00A0 or 0x1680 ||
        codepoint is >= 0x2000 and <= 0x200A ||
        codepoint is 0x2028 or 0x2029 or 0x202F or 0x205F or 0x3000;

    internal static int jvp_utf8_codepoint_length(ReadOnlySpan<byte> input)
    {
        var length = 0;
        var offset = 0;
        var codepoint = 0;
        int? next;
        while ((next = jvp_utf8_next(input, offset, ref codepoint)) is not null)
        {
            length++;
            offset = next.Value;
        }

        return length;
    }

    internal static int jvp_utf8_codepoint_length(string input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var length = 0;
        foreach (var rune in input.EnumerateRunes())
        {
            _ = rune;
            length++;
        }

        return length;
    }

    internal static int[] jvp_utf8_explode(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return input.EnumerateRunes().Select(static rune => rune.Value).ToArray();
    }

    internal static string jvp_utf8_implode(IEnumerable<int> codepoints)
    {
        ArgumentNullException.ThrowIfNull(codepoints);

        var result = new StringBuilder();
        foreach (var codepoint in codepoints)
        {
            result.Append((Rune.IsValid(codepoint) ? new Rune(codepoint) : Rune.ReplacementChar).ToString());
        }

        return result.ToString();
    }

    internal static string jvp_utf8_implode(IEnumerable<double> codepoints)
    {
        ArgumentNullException.ThrowIfNull(codepoints);

        var result = new StringBuilder();
        foreach (var number in codepoints)
        {
            if (double.IsNaN(number))
            {
                throw new ArgumentException(
                    "A NaN value cannot be imploded into a Unicode codepoint.",
                    nameof(codepoints));
            }

            var truncated = Math.Truncate(number);
            var codepoint = double.IsFinite(truncated) &&
                            truncated >= 0 &&
                            truncated <= JVP_UNICODE_MAX_CODEPOINT
                ? (int)truncated
                : JVP_UNICODE_REPLACEMENT_CODEPOINT;
            result.Append((Rune.IsValid(codepoint) ? new Rune(codepoint) : Rune.ReplacementChar).ToString());
        }

        return result.ToString();
    }

    // Converts a Unicode scalar offset to a UTF-8 byte boundary. A null return
    // means the scalar offset lies beyond the input. Malformed UTF-8 is rejected.
    internal static int? jvp_utf8_codepoint_to_byte_offset(ReadOnlySpan<byte> input, int codepointOffset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(codepointOffset);

        var byteOffset = 0;
        var codepoint = 0;
        for (var current = 0; current < codepointOffset; current++)
        {
            var next = jvp_utf8_next(input, byteOffset, ref codepoint);
            if (next is null)
            {
                return null;
            }

            if (codepoint == JVP_UTF8_INVALID_CODEPOINT)
            {
                throw new InvalidDataException("Invalid UTF-8 string");
            }

            byteOffset = next.Value;
        }

        return byteOffset;
    }

    // Converts a UTF-8 byte boundary to a Unicode scalar offset. Offsets in the
    // middle of a multi-byte sequence and malformed input are rejected.
    internal static int jvp_utf8_byte_to_codepoint_offset(ReadOnlySpan<byte> input, int byteOffset)
    {
        if ((uint)byteOffset > (uint)input.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(byteOffset));
        }

        var currentByteOffset = 0;
        var codepointOffset = 0;
        var codepoint = 0;
        while (currentByteOffset < byteOffset)
        {
            var next = jvp_utf8_next(input, currentByteOffset, ref codepoint);
            if (next is null || codepoint == JVP_UTF8_INVALID_CODEPOINT)
            {
                throw new InvalidDataException("Invalid UTF-8 string");
            }

            if (next.Value > byteOffset)
            {
                throw new ArgumentException("The byte offset is not on a UTF-8 codepoint boundary.", nameof(byteOffset));
            }

            currentByteOffset = next.Value;
            codepointOffset++;
        }

        return codepointOffset;
    }

    internal static jv jv_string_explode(jv value)
    {
        if (value.Kind != jv_kind.JV_KIND_STRING)
        {
            throw JqTypeError(value, "can't be exploded");
        }

        try
        {
            var data = jvp_string_data(value);
            var result = jv_array_sized(data.Length);
            var offset = 0;
            var codepoint = 0;
            int? next;
            while ((next = jvp_utf8_next(data, offset, ref codepoint)) is not null)
            {
                result = jv_array_append(result, jv_number(codepoint));
                if (!result.IsValid)
                {
                    break;
                }

                offset = next.Value;
            }

            return result;
        }
        finally
        {
            jv_free(value);
        }
    }

    internal static jv jv_string_implode(jv value)
    {
        if (value.Kind != jv_kind.JV_KIND_ARRAY)
        {
            throw JqTypeError(value, "can't be imploded, input must be an array");
        }

        try
        {
            var result = jv_string_empty(value.ArrayValue.Count);
            foreach (var item in value.ArrayValue)
            {
                if (item.Kind != jv_kind.JV_KIND_NUMBER || double.IsNaN(item.NumberValue))
                {
                    jv_free(result);
                    // ArrayValue exposes a borrowed slot; native jv_array_foreach
                    // hands type_error() an owned element copy.
                    throw JqTypeError(
                        jv_copy(item),
                        "can't be imploded, unicode codepoint needs to be numeric");
                }

                var truncated = Math.Truncate(item.NumberValue);
                var codepoint = double.IsFinite(truncated) &&
                                truncated >= 0 &&
                                truncated <= JVP_UNICODE_MAX_CODEPOINT &&
                                truncated is not (>= 0xD800 and <= 0xDFFF)
                    ? (int)truncated
                    : JVP_UNICODE_REPLACEMENT_CODEPOINT;
                result = jv_string_append_codepoint(result, codepoint);
                if (!result.IsValid)
                {
                    break;
                }
            }

            return result;
        }
        finally
        {
            jv_free(value);
        }
    }

    internal static int jv_string_length_codepoints(jv value)
    {
        if (value.Kind != jv_kind.JV_KIND_STRING)
        {
            throw JqTypeError(value, "has no string length");
        }

        try
        {
            var data = jvp_string_data(value);
            var offset = 0;
            var count = 0;
            var codepoint = 0;
            while (jvp_utf8_next(data, offset, ref codepoint) is { } next)
            {
                offset = next;
                count++;
            }

            return count;
        }
        finally
        {
            jv_free(value);
        }
    }

    private static int Utf8CodingLength(byte value)
    {
        if (value <= 0x7F)
        {
            return 1;
        }

        if (value <= 0xBF)
        {
            return Utf8ContinuationByte;
        }

        if (value <= 0xC1 || value >= 0xF5)
        {
            return 0;
        }

        if (value <= 0xDF)
        {
            return 2;
        }

        return value <= 0xEF ? 3 : 4;
    }

    private static int Utf8CodingBits(byte value)
    {
        var length = Utf8CodingLength(value);
        return length switch
        {
            1 => 0x7F,
            2 => 0x1F,
            3 => 0x0F,
            4 => 0x07,
            Utf8ContinuationByte => 0x3F,
            _ => 0,
        };
    }

    private static int Utf8FirstCodepoint(int length) => length switch
    {
        1 => 0,
        2 => 0x80,
        3 => 0x800,
        4 => 0x10000,
        _ => 0,
    };
}
