// DOTNETJQ PORT MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream file: src/locfile.c
// Upstream URL: https://github.com/jqlang/jq/blob/jq-1.8.2/src/locfile.c
// Strategy: PORT
// Target file: src/DotNetJq/Port/src/locfile.c.cs
//
// Rules:
// - Preserve upstream function names wherever possible.
// - Behavioral compatibility is defined by upstream tests/differential tests.
// - Do not refactor cross-file architecture during compatibility port.
//
// Substitutions:
// - UTF-8 byte storage preserves upstream byte offsets without unsafe native pointers.
// - The production jq_state overload reports only through jq_report_error;
//   a narrow Action<jv> overload remains for isolated locfile tests.
// - A small C-format adapter covers the %s/%d forms used by upstream locfile callers.
//
// Known differences:
// - Invalid UTF-8 in a source excerpt is rendered with the .NET replacement rune.
// - Native allocation/refcount destruction is represented by managed logical retain/free state.

using System.Globalization;
using System.Text;

namespace DotNetJq.Port;

internal static partial class libjq
{
    // jq-1.8.2 src/locfile.c:locfile_init(). The locfile representation keeps
    // a managed closure instead of a native jq_state pointer, but production
    // diagnostics still cross the canonical jq_report_error boundary.
    internal static locfile locfile_init(
        jq_state jq,
        string fileName,
        byte[] source,
        int length)
    {
        ArgumentNullException.ThrowIfNull(jq);
        return locfile_init(value => jq_report_error(jq, value), fileName, source, length);
    }

    internal static locfile locfile_init(
        Action<jv>? errorReporter,
        string fileName,
        byte[] source,
        int length)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, source.Length);

        var data = source.AsSpan(0, length).ToArray();
        var lineCount = 1;
        foreach (var value in data)
        {
            if (value == (byte)'\n')
            {
                lineCount++;
            }
        }

        var lineMap = new int[lineCount + 1];
        var line = 1;
        for (var index = 0; index < data.Length; index++)
        {
            if (data[index] == (byte)'\n')
            {
                lineMap[line++] = index + 1;
            }
        }

        lineMap[lineCount] = data.Length + 1;
        return new locfile(errorReporter, fileName, data, lineMap);
    }

    internal static locfile locfile_init(string fileName, string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var bytes = Encoding.UTF8.GetBytes(source);
        return locfile_init((Action<jv>?)null, fileName, bytes, bytes.Length);
    }

    internal static locfile locfile_retain(locfile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return file.Retain();
    }

    internal static void locfile_free(locfile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        file.Free();
    }

    internal static int locfile_get_line(locfile file, int position)
    {
        ensure_live_locfile(file);
        if ((uint)position >= (uint)file.length)
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }

        var line = 1;
        while (file.linemap[line] <= position)
        {
            line++;
        }

        return line - 1;
    }

    internal static jq_source_location locfile_resolve_location(
        locfile file,
        location sourceLocation,
        int lineOffset = 0)
    {
        ensure_live_locfile(file);
        if (sourceLocation.start < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceLocation));
        }

        return new jq_source_location(
            file.fname.StringValue,
            locfile_get_line(file, sourceLocation.start) + lineOffset + 1);
    }

    // parser.y's gen_loc_object() materializes this exact object for the special
    // $__loc__ token. Keeping construction beside locfile makes the source identity
    // and byte-to-line conversion explicit in the managed port as well.
    internal static jv gen_loc_object(
        locfile file,
        location sourceLocation,
        int lineOffset = 0)
    {
        var resolved = locfile_resolve_location(file, sourceLocation, lineOffset);
        return jv_object_set(
            jv_object_set(jv_object(), "file", jv_string(resolved.FileName)),
            "line",
            jv_number(resolved.Line));
    }

    internal static void locfile_locate(locfile file, location sourceLocation, string format, params object?[] args)
    {
        var message = locfile_format_location(file, sourceLocation, format, args);
        file.jq?.Invoke(jv_string(message));
    }

    internal static string locfile_format_location(
        locfile file,
        location sourceLocation,
        string format,
        params object?[] args)
    {
        ensure_live_locfile(file);
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(args);
        var message = format_c_message(format, args);
        if (sourceLocation.start == -1)
        {
            return "jq: error: " + message;
        }

        var startLine = locfile_get_line(file, sourceLocation.start);
        var offset = file.linemap[startLine];
        var end = Math.Min(
            sourceLocation.end,
            Math.Max(file.linemap[startLine + 1] - 1, sourceLocation.start + 1));
        var caretCount = Math.Max(0, end - sourceLocation.start);
        var lineLength = locfile_line_length(file, startLine);
        var sourceLine = Encoding.UTF8.GetString(file.data, offset, lineLength);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{message} at {file.fname.StringValue}, line {startLine + 1}, column {sourceLocation.start - offset + 1}:\n" +
            $"    {sourceLine}\n    {new string(' ', sourceLocation.start - offset)}{new string('^', caretCount)}");
    }

    internal static JqCompileException CreateSourceError(
        string source,
        string sourceName,
        string message,
        Token token,
        int length)
    {
        var lineStart = token.SourceIndex;
        while (lineStart > 0 && source[lineStart - 1] != '\n')
        {
            lineStart--;
        }

        var lineEnd = source.IndexOf('\n', token.SourceIndex);
        if (lineEnd < 0)
        {
            lineEnd = source.Length;
        }

        var sourceLine = source[lineStart..lineEnd].TrimEnd('\r');
        var caretOffset = Math.Max(0, token.Column - 1);
        var sourceLineByteLength = Encoding.UTF8.GetByteCount(sourceLine);
        var caretLength = Math.Max(
            1,
            Math.Min(length, Math.Max(1, sourceLineByteLength - caretOffset)));
        return new JqCompileException(
            $"jq: error: {message} at {sourceName}, line {token.Line}, column {token.Column}:\n" +
            "    " + sourceLine + "\n" +
            "    " + new string(' ', caretOffset) + new string('^', caretLength));
    }

    private static int locfile_line_length(locfile file, int line)
    {
        if ((uint)line >= (uint)file.nlines)
        {
            throw new ArgumentOutOfRangeException(nameof(line));
        }

        return file.linemap[line + 1] - file.linemap[line] - 1;
    }

    private static void ensure_live_locfile(locfile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        ObjectDisposedException.ThrowIf(file.IsFreed, file);
    }

    private static string format_c_message(string format, object?[] args)
    {
        var result = new StringBuilder(format.Length + args.Length * 8);
        var argumentIndex = 0;
        for (var index = 0; index < format.Length; index++)
        {
            if (format[index] != '%' || index + 1 >= format.Length)
            {
                result.Append(format[index]);
                continue;
            }

            if (format[index + 1] == '%')
            {
                result.Append('%');
                index++;
                continue;
            }

            var specifierStart = index;
            index++;
            while (index < format.Length &&
                   (format[index] is '-' or '+' or ' ' or '#' or '0' or '.' ||
                    char.IsAsciiDigit(format[index])))
            {
                index++;
            }

            if (index < format.Length && format[index] is 'h' or 'l' or 'z')
            {
                var modifier = format[index++];
                if (index < format.Length && format[index] == modifier)
                {
                    index++;
                }
            }

            if (index >= format.Length || format[index] is not ('s' or 'd' or 'i' or 'u'))
            {
                var specifierLength = Math.Min(format.Length, index + 1) - specifierStart;
                result.Append(format.AsSpan(specifierStart, specifierLength));
                continue;
            }

            if (argumentIndex >= args.Length)
            {
                throw new FormatException("Not enough arguments for locfile format string.");
            }

            result.Append(Convert.ToString(args[argumentIndex++], CultureInfo.InvariantCulture));
        }

        if (argumentIndex != args.Length)
        {
            throw new FormatException("Too many arguments for locfile format string.");
        }

        return result.ToString();
    }
}
