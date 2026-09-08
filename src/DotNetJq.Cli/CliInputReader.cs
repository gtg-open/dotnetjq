// DOTNETJQ PORT MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream files: src/util.c, src/main.c
// Upstream symbols: jq_util_input_state, jq_util_input_read_more,
//   jq_util_input_next_input, jq_util_input_get_position
// Strategy: PORT
// Target file: src/DotNetJq.Cli/CliInputReader.cs
// Substitutions: explicit managed Stream/IJqInputSource capabilities replace FILE*;
//   owned jv values and source-shaped filename/line state remain observable.
// Known differences: Windows streams are intentionally always byte-oriented; see
//   porting/WINDOWS_STDIO_CONTRACT.md. Native FILE*/allocator fault identity is excluded.

using DotNetJq.Port;

namespace DotNetJq.Cli;

internal enum CliInputKind
{
    End,
    Value,
    Error,
}

internal readonly record struct CliInput(
    CliInputKind Kind,
    jv Value,
    JqInputPosition? Position,
    int ByteCount)
{
    internal static CliInput EndAt(JqInputPosition? position) =>
        new(CliInputKind.End, default, position, 0);

    internal static CliInput FromValue(jv value, JqInputPosition position, int byteCount) =>
        new(CliInputKind.Value, value, position, byteCount);

    internal static CliInput FromError(jv value, JqInputPosition position) =>
        new(CliInputKind.Error, value, position, 0);
}

/// <summary>
/// Managed counterpart of jq_util_input_state. One instance is shared by the outer primary-input
/// loop and jq's input/inputs callbacks, so values are consumed in precisely one forward stream.
/// </summary>
internal sealed class CliInputReader : IJqInputSource, IDisposable
{
    private const int BufferSize = 64 * 1024;
    // jq-1.8.2 src/util.c:jq_util_input_state.buf is 4096 bytes. Its fgets()
    // request leaves four bytes for completing a UTF-8 scalar plus the C NUL.
    private const int ParserBufferSize = 4096;
    private const int ParserPayloadSize = ParserBufferSize - 5;

    private readonly Stream standardInput;
    private readonly string baseDirectory;
    private readonly string[] sources;
    private readonly bool rawInput;
    private readonly int parserFlags;
    private readonly Action<string>? reportSystemError;
    private readonly byte[] buffer = new byte[BufferSize];
    private readonly byte[] parserBuffer = new byte[ParserBufferSize];

    private int sourceIndex;
    private Stream? stream;
    private bool ownsStream;
    private string sourceName = string.Empty;
    private jv_parser? parser;
    private bool parserFinalBuffer;
    private long lineNumber;
    private int rawOffset;
    private int rawCount;
    private bool streamAtEnd;
    private bool disposed;

    private readonly record struct InputChunk(int Length, bool IsLast);

    internal JqInputPosition? CurrentPosition { get; private set; }

    internal int FailureCount { get; private set; }

    internal CliInputReader(
        Stream standardInput,
        string baseDirectory,
        IReadOnlyList<string> files,
        bool rawInput,
        int parserFlags,
        Action<string>? reportSystemError = null)
    {
        ArgumentNullException.ThrowIfNull(standardInput);
        ArgumentNullException.ThrowIfNull(baseDirectory);
        ArgumentNullException.ThrowIfNull(files);
        this.standardInput = standardInput;
        this.baseDirectory = Path.GetFullPath(baseDirectory);
        sources = files.Count == 0 ? ["-"] : files.ToArray();
        this.rawInput = rawInput;
        this.parserFlags = parserFlags;
        this.reportSystemError = reportSystemError;
        if (!rawInput)
        {
            parser = libjq.jv_parser_new(parserFlags);
        }
    }

    internal CliInput ReadNext()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return rawInput ? ReadRawLine() : ReadParsedValue();
    }

    internal CliInput ReadSlurped()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return rawInput ? ReadRawSlurped() : ReadJsonSlurped();
    }

    // jq-1.8.2 main.c:664 and util.c:jq_util_input_next_input_cb return the
    // input state's owned jv directly. End is a plain invalid; f_input turns
    // it into "break". Parser errors are invalid-with-message values.
    internal jv ReadNextInput(jq_state state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        cancellationToken.ThrowIfCancellationRequested();
        var input = ReadNext();
        if (input.Position is { } position)
        {
            state.SetCurrentInputPosition(position);
        }

        return input.Kind switch
        {
            CliInputKind.End => libjq.jv_invalid(),
            CliInputKind.Value => input.Value,
            CliInputKind.Error => libjq.jv_invalid_with_msg(input.Value),
            _ => throw new InvalidOperationException("Unknown CLI input kind."),
        };
    }

    JqInputReadResult IJqInputSource.ReadNext(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var input = ReadNext();
        try
        {
            return input.Kind switch
            {
                CliInputKind.End => JqInputReadResult.End,
                CliInputKind.Value => JqInputReadResult.FromValue(
                    libjq.jv_to_json_element(input.Value),
                    input.Position),
                CliInputKind.Error => JqInputReadResult.FromError(
                    libjq.jv_to_json_element(input.Value),
                    input.Position),
                _ => throw new InvalidOperationException("Unknown CLI input kind."),
            };
        }
        finally
        {
            // CliInput owns parser/raw jv results. The public capability
            // result contains a detached JsonElement, so release that owner.
            if (input.Kind is CliInputKind.Value or CliInputKind.Error)
            {
                libjq.jv_free(input.Value);
            }
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        CloseStream();
        if (parser is not null)
        {
            libjq.jv_parser_free(parser);
            parser = null;
        }

    }

    private CliInput ReadParsedValue()
    {
        while (true)
        {
            if (parser is null)
            {
                return CliInput.EndAt(CurrentPosition);
            }

            var value = libjq.jv_parser_next(parser);
            var after = libjq.jv_parser_remaining(parser);

            if (value.IsValid)
            {
                CurrentPosition = new JqInputPosition(sourceName, lineNumber);
                return CliInput.FromValue(
                    value,
                    CurrentPosition.Value,
                    // The CLI does not expose the library-only MaxInputBytes
                    // policy. Avoid serializing an already parsed jv solely
                    // to initialize an unused execution byte counter.
                    byteCount: 0);
            }

            if (libjq.jv_invalid_has_msg(libjq.jv_copy(value)))
            {
                CurrentPosition = new JqInputPosition(sourceName, lineNumber);
                return CliInput.FromError(
                    libjq.jv_invalid_get_msg(value),
                    CurrentPosition.Value);
            }

            if (after != 0)
            {
                continue;
            }

            if (parserFinalBuffer)
            {
                libjq.jv_parser_free(parser);
                parser = null;
                return CliInput.EndAt(CurrentPosition);
            }

            var chunk = jq_util_input_read_more();
            parserFinalBuffer = chunk.IsLast;
            libjq.jv_parser_set_buf(
                parser,
                parserBuffer,
                chunk.Length,
                chunk.IsLast ? 0 : 1);
        }
    }

    // Direct state-machine port of jq-1.8.2
    // src/util.c:jq_util_input_read_more(). All parsed, raw-line, and raw-slurp
    // consumers use this one primitive because fgets()'s 4,091-byte boundary,
    // pre-parse newline count, and UTF-8 tail fread are jq-observable. In
    // particular, a newline consumed by the tail fread is deliberately not
    // counted and can become an embedded byte in a raw record.
    private InputChunk jq_util_input_read_more()
    {
        if (stream is null || streamAtEnd)
        {
            CloseStream();
            OpenNextSource();
        }

        var length = 0;
        if (stream is not null)
        {
            while (length < ParserPayloadSize)
            {
                if (!EnsureReadAhead())
                {
                    break;
                }

                var available = Math.Min(rawCount - rawOffset, ParserPayloadSize - length);
                var newline = Array.IndexOf(buffer, (byte)'\n', rawOffset, available);
                var take = newline < 0 ? available : newline - rawOffset + 1;
                buffer.AsSpan(rawOffset, take).CopyTo(parserBuffer.AsSpan(length));
                rawOffset += take;
                length += take;
                if (newline >= 0)
                {
                    lineNumber++;
                    SynchronizeCurrentPosition();
                    return new InputChunk(
                        length,
                        IsLast: sourceIndex == sources.Length && stream is null);
                }
            }

            if (length == ParserPayloadSize)
            {
                // Match util.c's jvp_utf8_backtrack()/fread() tail: a full
                // fgets-sized chunk is extended only far enough to avoid
                // splitting a UTF-8 scalar. fread does not perform a second
                // newline accounting pass, even when it consumes '\n'.
                var missingBytes = 0;
                if (libjq.jvp_utf8_backtrack(
                        parserBuffer.AsSpan(0, length),
                        length - 1,
                        0,
                        ref missingBytes) is not null &&
                    missingBytes > 0)
                {
                    while (missingBytes-- > 0)
                    {
                        if (!EnsureReadAhead())
                        {
                            break;
                        }

                        parserBuffer[length++] = buffer[rawOffset++];
                    }
                }
            }
        }

        SynchronizeCurrentPosition();
        return new InputChunk(
            length,
            IsLast: sourceIndex == sources.Length && stream is null);
    }

    private bool EnsureReadAhead()
    {
        if (rawOffset != rawCount)
        {
            return true;
        }

        rawCount = stream!.Read(buffer, 0, buffer.Length);
        rawOffset = 0;
        if (rawCount != 0)
        {
            return true;
        }

        // C stdio retains current_input when fgets()/fread first observes EOF;
        // jq_util_input_read_more closes it only on the following invocation.
        streamAtEnd = true;
        return false;
    }

    private CliInput ReadRawLine()
    {
        var value = libjq.jv_invalid();
        var byteCount = 0;
        try
        {
            while (true)
            {
                var chunk = jq_util_input_read_more();
                if (chunk.Length != 0)
                {
                    var endsInNewline = parserBuffer[chunk.Length - 1] == (byte)'\n';
                    var contentLength = endsInNewline ? chunk.Length - 1 : chunk.Length;
                    if (!value.IsValid)
                    {
                        value = libjq.jv_string_empty(contentLength);
                    }

                    // jq_util_input_next_input() repairs each read_more buffer
                    // before concatenation. This matters at file boundaries:
                    // an incomplete scalar at one EOF cannot be completed by
                    // a continuation byte from the next file.
                    value = libjq.jv_string_concat(
                        value,
                        libjq.jv_string_sized(parserBuffer, contentLength));
                    byteCount = checked(byteCount + contentLength);
                    if (endsInNewline)
                    {
                        return FinishRawLine(ref value, byteCount);
                    }
                }

                if (!chunk.IsLast)
                {
                    continue;
                }

                if (value.IsValid)
                {
                    return FinishRawLine(ref value, byteCount);
                }

                return CliInput.EndAt(CurrentPosition);
            }
        }
        finally
        {
            libjq.jv_free(value);
        }
    }

    private CliInput FinishRawLine(ref jv value, int byteCount)
    {
        SynchronizeCurrentPosition();
        var result = CliInput.FromValue(value, CurrentPosition!.Value, byteCount);
        value = libjq.jv_invalid();
        return result;
    }

    private CliInput ReadRawSlurped()
    {
        var value = libjq.jv_string_empty(0);
        var totalBytes = 0;
        try
        {
            while (true)
            {
                var chunk = jq_util_input_read_more();
                if (chunk.Length != 0)
                {
                    // jq_util_input_next_input() constructs one jv string per
                    // read_more buffer before concatenating it. Keeping that
                    // boundary preserves malformed-UTF-8 replacement exactly.
                    value = libjq.jv_string_concat(
                        value,
                        libjq.jv_string_sized(parserBuffer, chunk.Length));
                    totalBytes = checked(totalBytes + chunk.Length);
                }

                if (chunk.IsLast)
                {
                    var position = CurrentPosition ?? new JqInputPosition("<stdin>", 0);
                    return CliInput.FromValue(value, position, totalBytes);
                }
            }
        }
        catch
        {
            libjq.jv_free(value);
            throw;
        }
    }

    private CliInput ReadJsonSlurped()
    {
        var values = new List<jv>();
        var totalBytes = 0;
        while (true)
        {
            var next = ReadParsedValue();
            switch (next.Kind)
            {
                case CliInputKind.End:
                    return CliInput.FromValue(
                        libjq.jv_array(values),
                        next.Position ?? new JqInputPosition("<stdin>", 0),
                        totalBytes);
                case CliInputKind.Error:
                    foreach (var value in values)
                    {
                        libjq.jv_free(value);
                    }

                    return next;
                case CliInputKind.Value:
                    values.Add(next.Value);
                    totalBytes = checked(totalBytes + next.ByteCount);
                    break;
                default:
                    throw new InvalidOperationException("Unknown CLI input kind.");
            }
        }
    }

    private void OpenNextSource()
    {
        if (sourceIndex >= sources.Length)
        {
            return;
        }

        var path = sources[sourceIndex++];
        sourceName = path == "-" ? "<stdin>" : path;
        lineNumber = 0;
        rawOffset = 0;
        rawCount = 0;
        streamAtEnd = false;
        SynchronizeCurrentPosition();
        try
        {
            if (path == "-")
            {
                stream = standardInput;
                ownsStream = false;
            }
            else
            {
                stream = new FileStream(
                    Path.GetFullPath(path, baseDirectory),
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    BufferSize,
                    FileOptions.SequentialScan);
                ownsStream = true;
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException)
        {
            FailureCount++;
            reportSystemError?.Invoke(
                $"jq: error: Could not open file {path}: {DescribeOpenFailure(exception)}");
            stream = null;
            ownsStream = false;
        }
    }

    private void SynchronizeCurrentPosition()
    {
        if (sourceName.Length != 0)
        {
            CurrentPosition = new JqInputPosition(sourceName, lineNumber);
        }
    }

    private static string DescribeOpenFailure(Exception exception) => exception switch
    {
        FileNotFoundException or DirectoryNotFoundException => "No such file or directory",
        UnauthorizedAccessException => "Permission denied",
        PathTooLongException => "File name too long",
        _ => exception.Message,
    };

    private void CloseStream()
    {
        if (ownsStream)
        {
            stream?.Dispose();
        }

        stream = null;
        ownsStream = false;
        streamAtEnd = false;
        rawOffset = 0;
        rawCount = 0;
    }
}
