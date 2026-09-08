using System.Buffers;
using System.Text;
using System.Text.Json;
using DotNetJq.Port;

namespace DotNetJq.Cli;

internal sealed class CliOutput : IJqValueSink
{
    private static readonly byte[] NewLine = [(byte)'\n'];
    private static readonly byte[] RecordSeparator = [0x1e];
    private static readonly byte[] Zero = [0];

    private readonly Stream standardOutput;
    private readonly Stream standardError;
    private readonly CliOptions options;
    private readonly int dumpOptions;

    internal CliOutput(
        Stream standardOutput,
        Stream standardError,
        CliOptions options,
        bool color,
        string? colorConfiguration,
        out bool validColorConfiguration)
    {
        this.standardOutput = standardOutput;
        this.standardError = standardError;
        this.options = options;
        dumpOptions = libjq.JV_PRINT_INDENT_FLAGS(options.TabIndent ? -1 : options.Indent);
        if (options.CompactOutput)
        {
            // jq-1.8.2 src/main.c:-c clears TAB, PRETTY, and all three
            // indentation-width bits while leaving the other dump flags alone.
            dumpOptions &= ~(libjq.JV_PRINT_TAB | libjq.JV_PRINT_INDENT_FLAGS(7));
        }

        if (options.SortKeys)
        {
            dumpOptions |= libjq.JV_PRINT_SORTED;
        }

        if (options.AsciiOutput)
        {
            dumpOptions |= libjq.JV_PRINT_ASCII;
        }

        if (color)
        {
            dumpOptions |= libjq.JV_PRINT_COLOR;
        }

        validColorConfiguration = libjq.jq_set_colors(colorConfiguration);
    }

    internal void WriteResult(jv value)
    {
        if (options.RawOutput && value.Kind == jv_kind.JV_KIND_STRING)
        {
            if (options.AsciiOutput)
            {
                // src/main.c:process() intentionally passes ASCII alone here:
                // raw strings do not inherit color, sorting, or pretty flags.
                libjq.jv_dumpf(
                    libjq.jv_copy(value),
                    standardOutput,
                    libjq.JV_PRINT_ASCII);
            }
            else
            {
                var bytes = libjq.jvp_string_data(value);
                if (options.RawOutputZero && bytes.IndexOf((byte)0) >= 0)
                {
                    throw new JqRuntimeException(
                        "Cannot dump a string containing NUL with --raw-output0 option");
                }

                standardOutput.Write(bytes);
            }
        }
        else
        {
            if (options.Sequence)
            {
                standardOutput.Write(RecordSeparator);
            }

            libjq.jv_dumpf(libjq.jv_copy(value), standardOutput, dumpOptions);
        }

        if (!options.JoinOutput)
        {
            standardOutput.Write(NewLine);
        }

        if (options.RawOutputZero)
        {
            standardOutput.Write(Zero);
        }

        if (options.Unbuffered)
        {
            standardOutput.Flush();
        }
    }

    internal void WriteRuntimeError(JqInputPosition? position, JqRuntimeException exception)
    {
        var source = position is { } inputPosition
            ? $"{inputPosition.FileName ?? "<unknown>"}:{inputPosition.LineNumber}"
            : "<unknown>";
        var message = exception.ErrorValue is { } errorValue && errorValue.IsValid
            ? errorValue.Kind == jv_kind.JV_KIND_STRING
                // jq-1.8.2 src/main.c prints an uncaught string error through
                // fprintf("%s"), so this presentation boundary stops at the
                // first payload NUL. Other byte-length-aware output paths do not.
                ? TruncateAtNul(errorValue.StringValue)
                : $"(not a string): {libjq.jv_dump_string_borrowed(errorValue)}"
            : exception.Message;
        WriteUtf8(standardError, $"jq: error (at {source}): {message}\n");
        standardError.Flush();
    }

    private static string TruncateAtNul(string value)
    {
        var terminator = value.IndexOf('\0', StringComparison.Ordinal);
        return terminator < 0 ? value : value[..terminator];
    }

    internal void WriteParseError(string message, bool ignored)
    {
        WriteUtf8(
            standardError,
            ignored ? $"jq: ignoring parse error: {message}\n" : $"jq: parse error: {message}\n");
    }

    internal void WriteHaltMessage(jv message)
    {
        if (!message.IsValid || message.Kind == jv_kind.JV_KIND_NULL)
        {
            return;
        }

        if (message.Kind == jv_kind.JV_KIND_STRING)
        {
            standardError.Write(libjq.jvp_string_data(message));
        }
        else
        {
            // src/main.c renders structured halt messages with flags==0,
            // independently of normal output color/ASCII/sort options.
            libjq.jv_dumpf(libjq.jv_copy(message), standardError, 0);
            standardError.Write(NewLine);
        }

        standardError.Flush();
    }

    internal void WriteStandardError(JsonElement value)
        => WriteStandardError(libjq.jv_from_json_element(value));

    // src/main.c:stderr_cb(). This jq_msg_cb target consumes the owned jv
    // directly. JsonElement conversion remains only on IJqValueSink, the
    // external managed capability boundary.
    internal void WriteStandardError(jv value)
    {
        try
        {
            if (value.Kind == jv_kind.JV_KIND_STRING)
            {
                standardError.Write(libjq.jvp_string_data(value));
            }
            else
            {
                // src/main.c:stderr_cb() consumes a structured value through
                // jv_dump_string(input, 0); output flags do not leak here.
                var owned = value;
                value = libjq.jv_invalid();
                libjq.jv_dumpf(owned, standardError, 0);
            }
        }
        finally
        {
            libjq.jv_free(value);
        }
    }

    internal void WriteDebug(JsonElement value)
        => WriteDebug(libjq.jv_from_json_element(value));

    // src/main.c:debug_cb(). Ownership of value moves into the temporary
    // ["DEBUG:", value] array, which is released after byte formatting.
    internal void WriteDebug(jv value)
    {
        var debug = libjq.jv_array_append(libjq.jv_array(), libjq.jv_string("DEBUG:"));
        try
        {
            debug = libjq.jv_array_append(debug, value);
            value = libjq.jv_invalid();
            // src/main.c:debug_cb() retains ASCII/color/sort but clears only
            // PRETTY. TAB/width bits remain inert without PRETTY.
            var owned = debug;
            debug = libjq.jv_invalid();
            libjq.jv_dumpf(
                owned,
                standardError,
                dumpOptions & ~libjq.JV_PRINT_PRETTY);
            standardError.Write(NewLine);
        }
        finally
        {
            libjq.jv_free(debug);
            libjq.jv_free(value);
        }
    }

    void IJqValueSink.Write(JsonElement value) => WriteStandardError(value);

    internal static void WriteUtf8(Stream stream, string text)
    {
        var maximum = Encoding.UTF8.GetMaxByteCount(text.Length);
        byte[]? rented = null;
        Span<byte> bytes = maximum <= 1024
            ? stackalloc byte[maximum]
            : (rented = ArrayPool<byte>.Shared.Rent(maximum));
        try
        {
            var count = Encoding.UTF8.GetBytes(text, bytes);
            stream.Write(bytes[..count]);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }
}
