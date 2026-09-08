// DOTNETJQ PORT MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream file: src/jv_parse.c
// Upstream URL: https://github.com/jqlang/jq/blob/jq-1.8.2/src/jv_parse.c
// Strategy: PORT
// Target file: src/DotNetJq/Port/src/jv_parse.c.cs
// UPSTREAM COMPONENT: jq's jv JSON parser and JSON-to-jv conversion boundary.
// MANAGED REPRESENTATION: ReadOnlyMemory<byte>, List<byte>, and List<jv> replace native
// pointers and growable allocations while preserving jq's scanner state and owner transfers.
// WHY: the jq scanner/control flow is retained directly; only its storage representation changes.
// BEHAVIORAL CONTRACT: jq-compatible single-value and incremental parsing, JSON text sequence
// recovery, streaming path/value/end events, stream-error values, and the 10,000-container depth limit.
// KNOWN DIFFERENCES: the convenience jv_parse(string) boundary encodes the CLR UTF-16 string with
// .NET's replacement fallback, then applies jq's C-string first-NUL boundary. jv_parse_sized() and
// the jq-shaped jv_parser buffer API remain byte-based, preserve split/malformed UTF-8, and apply
// upstream jv_string_sized() repair at string completion.
// TESTS COVERING THE SUBSTITUTION: JsonProxyCompatibilityRound2Tests,
// JvParserStreamingCompatibilityTests, shtest JSON sequence/stream cases, and upstream JSON fixtures.

using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DotNetJq.Port;

internal static partial class libjq
{
    internal const int MAX_PARSING_DEPTH = 10_000;

    internal const int JV_PARSE_SEQ = 1;
    internal const int JV_PARSE_STREAMING = 2;
    internal const int JV_PARSE_STREAM_ERRORS = 4;

    internal static jv_parser jv_parser_new(int flags) => new(flags);

    internal static void jv_parser_free(jv_parser parser)
    {
        ArgumentNullException.ThrowIfNull(parser);
        parser.Free();
    }

    internal static int jv_parser_remaining(jv_parser parser)
    {
        ArgumentNullException.ThrowIfNull(parser);
        return parser.Remaining;
    }

    internal static void jv_parser_set_buf(
        jv_parser parser,
        ReadOnlyMemory<byte> buffer,
        bool isPartial)
    {
        ArgumentNullException.ThrowIfNull(parser);
        parser.SetBuffer(buffer, isPartial);
    }

    internal static void jv_parser_set_buf(
        jv_parser parser,
        byte[] buffer,
        int length,
        int isPartial)
    {
        ArgumentNullException.ThrowIfNull(parser);
        ArgumentNullException.ThrowIfNull(buffer);
        if ((uint)length > (uint)buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        parser.SetBuffer(buffer.AsMemory(0, length), isPartial != 0);
    }

    internal static jv jv_parser_next(jv_parser parser)
    {
        ArgumentNullException.ThrowIfNull(parser);
        return parser.Next();
    }

    internal static jv jv_parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        // jq-1.8.2 src/jv_parse.c:jv_parse() calls strlen() and then
        // jv_parse_sized(). Encode only that C-string prefix so a managed
        // string cannot create a second parser implementation or bypass the
        // canonical byte scanner.
        var nul = json.AsSpan().IndexOf('\0');
        var bytes = Encoding.UTF8.GetBytes(nul < 0 ? json : json[..nul]);
        return jv_parse_sized(bytes);
    }

    // jq-1.8.2 src/jv_parse.c:jv_parse_sized_custom_flags()/jv_parse_sized().
    // Unlike the CLR-string convenience entry point above, this path retains
    // the exact UTF-8 byte count, including embedded NUL bytes, all the way
    // through the jq-shaped incremental parser.
    internal static jv jv_parse_sized(ReadOnlySpan<byte> json)
    {
        var parser = jv_parser_new(0);
        var value = jv_invalid();
        try
        {
            // jv_parser retains the current buffer until it is exhausted, so
            // give it an owned managed backing array for this synchronous call.
            var buffer = json.ToArray();
            jv_parser_set_buf(parser, buffer, buffer.Length, isPartial: 0);
            value = jv_parser_next(parser);
            if (value.IsValid)
            {
                var next = jv_parser_next(parser);
                if (next.IsValid)
                {
                    jv_free(value);
                    jv_free(next);
                    value = jv_invalid_with_msg(jv_string("Unexpected extra JSON values"));
                }
                else if (jv_invalid_has_msg(jv_copy(next)))
                {
                    jv_free(value);
                    value = next;
                }
                else
                {
                    jv_free(next);
                }
            }
            else if (!jv_invalid_has_msg(jv_copy(value)))
            {
                jv_free(value);
                value = jv_invalid_with_msg(jv_string("Expected JSON value"));
            }
        }
        finally
        {
            jv_parser_free(parser);
        }

        if (!value.IsValid && jv_invalid_has_msg(jv_copy(value)))
        {
            var message = jv_invalid_get_msg(value);
            try
            {
                // Upstream uses "%.*s" here.  The precision bounds the read,
                // but C printf still stops at the first NUL within that bound.
                var nul = json.IndexOf((byte)0);
                var diagnosticBytes = nul < 0 ? json : json[..nul];
                value = jv_invalid_with_msg(jv_string(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{message.StringValue} (while parsing '{Encoding.UTF8.GetString(diagnosticBytes)}')")));
            }
            finally
            {
                jv_free(message);
            }
        }

        return value;
    }

    // Retained as the jq-shaped bridge used by managed JsonElement callbacks. JSON text itself
    // always enters through the source-shaped byte scanner above.
    internal static jv jv_from_json_element(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null => jv_null(),
        JsonValueKind.False => jv_false(),
        JsonValueKind.True => jv_true(),
        JsonValueKind.Number => jv_number_with_literal(element.GetRawText()),
        JsonValueKind.String => jv_string(element.GetString() ?? string.Empty),
        JsonValueKind.Array => jv_array(element.EnumerateArray().Select(jv_from_json_element)),
        JsonValueKind.Object => jv_object(
            element.EnumerateObject().Select(
                property => KeyValuePair.Create(property.Name, jv_from_json_element(property.Value)))),
        _ => throw new JqParseException("Unsupported JSON token " + element.ValueKind),
    };

}

/// <summary>
/// Managed counterpart of jq-1.8.2's opaque <c>struct jv_parser</c>. The parser deliberately
/// consumes bytes rather than decoded text: callers can replace an exhausted partial buffer at
/// any byte boundary, including inside a UTF-8 sequence or JSON escape.
/// </summary>
internal sealed class jv_parser
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    private readonly int _flags;
    private readonly List<jv> _stack = [];
    private readonly List<byte> _token = [];
    private ReadOnlyMemory<byte> _currentBuffer;
    private bool _hasBuffer;
    private int _currentBufferPosition;
    private bool _currentBufferIsPartial;
    private bool _eof;
    private uint _bomStripPosition;
    private jv _path;
    private int _pathLength;
    private LastSeen _lastSeen;
    private jv _output;
    private jv _next;
    private ParserState _state;
    private bool _lastCharacterWasWhitespace;
    private int _line = 1;
    private int _column;
    private bool _freed;

    internal jv_parser(int flags)
    {
        _flags = flags;
        if (IsStreaming)
        {
            _path = libjq.jv_array();
        }

        _state = IsSequence ? ParserState.WaitingForRecordSeparator : ParserState.Normal;
    }

    internal int Remaining
    {
        get
        {
            ThrowIfFreed();
            return _hasBuffer ? _currentBuffer.Length - _currentBufferPosition : 0;
        }
    }

    private bool IsSequence => (_flags & libjq.JV_PARSE_SEQ) != 0;

    private bool IsStreaming => (_flags & libjq.JV_PARSE_STREAMING) != 0;

    private bool EmitsStreamErrors =>
        IsStreaming && (_flags & libjq.JV_PARSE_STREAM_ERRORS) != 0;

    internal void SetBuffer(ReadOnlyMemory<byte> buffer, bool isPartial)
    {
        ThrowIfFreed();
        if (_hasBuffer && _currentBufferPosition != _currentBuffer.Length)
        {
            throw new InvalidOperationException("previous jv_parser buffer was not exhausted");
        }

        var position = 0;
        while (position < buffer.Length && _bomStripPosition < Utf8Bom.Length)
        {
            if (buffer.Span[position] == Utf8Bom[_bomStripPosition])
            {
                position++;
                _bomStripPosition++;
            }
            else if (_bomStripPosition == 0)
            {
                _bomStripPosition = (uint)Utf8Bom.Length;
            }
            else
            {
                _bomStripPosition = 0xff;
            }
        }

        _currentBuffer = buffer[position..];
        _currentBufferPosition = 0;
        _currentBufferIsPartial = isPartial;
        _hasBuffer = true;
    }

    internal jv Next()
    {
        ThrowIfFreed();
        if (_eof || !_hasBuffer)
        {
            return libjq.jv_invalid();
        }

        if (_bomStripPosition == 0xff)
        {
            if (!IsSequence)
            {
                return Error("Malformed BOM");
            }

            // Preserve the upstream ordering, including parser_reset() restoring NORMAL.
            _state = ParserState.WaitingForRecordSeparator;
            Reset();
        }

        var pending = libjq.jv_invalid();
        if (IsStreaming && StreamCheckDone(ref pending))
        {
            return pending;
        }

        var value = libjq.jv_invalid();
        string? message = null;
        byte character = 0;
        while (message is null && _currentBufferPosition < _currentBuffer.Length)
        {
            character = _currentBuffer.Span[_currentBufferPosition++];
            if (_state == ParserState.WaitingForRecordSeparator)
            {
                AdvanceLocation(character);
                if (character == 0x1e)
                {
                    _state = ParserState.Normal;
                }

                continue;
            }

            message = Scan(character, ref value);
        }

        if (ReferenceEquals(message, ProducedOutput))
        {
            return value;
        }

        if (message is not null)
        {
            // jq-1.8.2 jv_parser_next() releases an output that was completed
            // earlier in the same scanner step before returning the later error.
            libjq.jv_free(value);
            if (character != 0x1e && IsSequence)
            {
                _state = ParserState.WaitingForRecordSeparator;
                value = Error($"{message} at line {_line}, column {_column} (need RS to resync)");
                Reset();
                return value;
            }

            value = Error($"{message} at line {_line}, column {_column}");
            Reset();
            if (!IsSequence)
            {
                _hasBuffer = false;
                _currentBufferPosition = 0;
            }

            return value;
        }

        if (_currentBufferIsPartial)
        {
            return libjq.jv_invalid();
        }

        _eof = true;
        if (_state == ParserState.WaitingForRecordSeparator)
        {
            return Error($"Unfinished abandoned text at EOF at line {_line}, column {_column}");
        }

        if (_state != ParserState.Normal)
        {
            value = Error($"Unfinished string at EOF at line {_line}, column {_column}");
            Reset();
            _state = ParserState.WaitingForRecordSeparator;
            return value;
        }

        message = CheckLiteral();
        if (message is not null)
        {
            value = Error($"{message} at EOF at line {_line}, column {_column}");
            Reset();
            _state = ParserState.WaitingForRecordSeparator;
            return value;
        }

        if ((IsStreaming && _pathLength != 0) || (!IsStreaming && _stack.Count != 0))
        {
            value = Error($"Unfinished JSON term at EOF at line {_line}, column {_column}");
            Reset();
            _state = ParserState.WaitingForRecordSeparator;
            return value;
        }

        value = IsStreaming && _next.IsValid
            ? libjq.jv_array([libjq.jv_copy(_path), _next])
            : _next;
        _next = libjq.jv_invalid();
        if (IsSequence && !_lastCharacterWasWhitespace &&
            value.Kind == jv_kind.JV_KIND_NUMBER)
        {
            libjq.jv_free(value);
            return Error(
                $"Potentially truncated top-level numeric value at EOF at line {_line}, column {_column}");
        }

        return value;
    }

    internal void Free()
    {
        if (_freed)
        {
            return;
        }

        Reset();
        libjq.jv_free(_path);
        _path = libjq.jv_invalid();
        libjq.jv_free(_output);
        _output = libjq.jv_invalid();
        _hasBuffer = false;
        _currentBuffer = default;
        _freed = true;
    }

    private static readonly string ProducedOutput = new("output produced".ToCharArray());

    private string? Scan(byte character, ref jv value)
    {
        AdvanceLocation(character);
        if (IsSequence && character == 0x1e)
        {
            if (CheckTruncation())
            {
                var literalMessage = CheckLiteral();
                if (literalMessage is null && IsTopNumber())
                {
                    return "Potentially truncated top-level numeric value";
                }

                return "Truncated value";
            }

            var message = CheckLiteral();
            if (message is not null)
            {
                return message;
            }

            if (_state == ParserState.Normal && CheckDone(ref value))
            {
                return ProducedOutput;
            }

            Reset();
            value = libjq.jv_invalid();
            return ProducedOutput;
        }

        string? answer = null;
        _lastCharacterWasWhitespace = false;
        if (_state == ParserState.Normal)
        {
            var characterClass = Classify(character);
            if (characterClass == CharacterClass.Whitespace)
            {
                _lastCharacterWasWhitespace = true;
            }

            if (characterClass != CharacterClass.Literal)
            {
                var message = CheckLiteral();
                if (message is not null)
                {
                    return message;
                }

                if (CheckDone(ref value))
                {
                    answer = ProducedOutput;
                }
            }

            switch (characterClass)
            {
                case CharacterClass.Literal:
                    _token.Add(character);
                    break;
                case CharacterClass.Whitespace:
                    break;
                case CharacterClass.Quote:
                    _state = ParserState.String;
                    break;
                case CharacterClass.Structure:
                {
                    var message = Token(character);
                    if (message is not null)
                    {
                        return message;
                    }

                    break;
                }
                default:
                    return "Invalid character";
            }

            if (CheckDone(ref value))
            {
                answer = ProducedOutput;
            }
        }
        else if (character == (byte)'"' && _state == ParserState.String)
        {
            var message = FoundString();
            if (message is not null)
            {
                return message;
            }

            _state = ParserState.Normal;
            if (CheckDone(ref value))
            {
                answer = ProducedOutput;
            }
        }
        else
        {
            _token.Add(character);
            _state = character == (byte)'\\' && _state == ParserState.String
                ? ParserState.StringEscape
                : ParserState.String;
        }

        return answer;
    }

    private void AdvanceLocation(byte character)
    {
        _column++;
        if (character == (byte)'\n')
        {
            _line++;
            _column = 0;
        }
    }

    private string? Token(byte character) => IsStreaming
        ? StreamToken(character)
        : ParseToken(character);

    private string? ParseToken(byte character)
    {
        switch (character)
        {
            case (byte)'[':
                if (_stack.Count >= libjq.MAX_PARSING_DEPTH)
                {
                    return "Exceeds depth limit for parsing";
                }

                if (_next.IsValid)
                {
                    return "Expected separator between values";
                }

                _stack.Add(libjq.jv_array());
                break;
            case (byte)'{':
                if (_stack.Count >= libjq.MAX_PARSING_DEPTH)
                {
                    return "Exceeds depth limit for parsing";
                }

                if (_next.IsValid)
                {
                    return "Expected separator between values";
                }

                _stack.Add(libjq.jv_object());
                break;
            case (byte)':':
                if (!_next.IsValid)
                {
                    return "Expected string key before ':'";
                }

                if (_stack.Count == 0 || _stack[^1].Kind != jv_kind.JV_KIND_OBJECT)
                {
                    return "':' not as part of an object";
                }

                if (_next.Kind != jv_kind.JV_KIND_STRING)
                {
                    return "Object keys must be strings";
                }

                _stack.Add(_next);
                _next = libjq.jv_invalid();
                break;
            case (byte)',':
                if (!_next.IsValid)
                {
                    return "Expected value before ','";
                }

                if (_stack.Count == 0)
                {
                    return "',' not as part of an object or array";
                }

                if (_stack[^1].Kind == jv_kind.JV_KIND_ARRAY)
                {
                    _stack[^1] = libjq.jv_array_append(_stack[^1], _next);
                    _next = libjq.jv_invalid();
                }
                else if (_stack[^1].Kind == jv_kind.JV_KIND_STRING)
                {
                    _stack[^2] = libjq.jv_object_set(_stack[^2], _stack[^1], _next);
                    _stack.RemoveAt(_stack.Count - 1);
                    _next = libjq.jv_invalid();
                }
                else
                {
                    return "Objects must consist of key:value pairs";
                }

                break;
            case (byte)']':
                if (_stack.Count == 0 || _stack[^1].Kind != jv_kind.JV_KIND_ARRAY)
                {
                    return "Unmatched ']'";
                }

                if (_next.IsValid)
                {
                    _stack[^1] = libjq.jv_array_append(_stack[^1], _next);
                    _next = libjq.jv_invalid();
                }
                else if (_stack[^1].ArrayValue.Count != 0)
                {
                    return "Expected another array element";
                }

                _next = _stack[^1];
                _stack.RemoveAt(_stack.Count - 1);
                break;
            case (byte)'}':
                if (_stack.Count == 0)
                {
                    return "Unmatched '}'";
                }

                if (_next.IsValid)
                {
                    if (_stack[^1].Kind != jv_kind.JV_KIND_STRING)
                    {
                        return "Objects must consist of key:value pairs";
                    }

                    _stack[^2] = libjq.jv_object_set(_stack[^2], _stack[^1], _next);
                    _stack.RemoveAt(_stack.Count - 1);
                    _next = libjq.jv_invalid();
                }
                else
                {
                    if (_stack[^1].Kind != jv_kind.JV_KIND_OBJECT)
                    {
                        return "Unmatched '}'";
                    }

                    if (_stack[^1].ObjectValue.Count != 0)
                    {
                        return "Expected another key-value pair";
                    }
                }

                _next = _stack[^1];
                _stack.RemoveAt(_stack.Count - 1);
                break;
        }

        return null;
    }

    private string? StreamToken(byte character)
    {
        switch (character)
        {
            case (byte)'[':
                if (_next.IsValid)
                {
                    return "Expected a separator between values";
                }

                if (_lastSeen == LastSeen.OpenObject)
                {
                    return "Expected string key after '{', not '['";
                }

                if (_lastSeen == LastSeen.Comma)
                {
                    var last = libjq.jv_array_get(libjq.jv_copy(_path), _pathLength - 1);
                    var kind = last.Kind;
                    libjq.jv_free(last);
                    if (kind != jv_kind.JV_KIND_NUMBER)
                    {
                        return "Expected string key after ',' in object, not '['";
                    }
                }

                _path = libjq.jv_array_append(_path, libjq.jv_number(0));
                _lastSeen = LastSeen.OpenArray;
                _pathLength++;
                break;
            case (byte)'{':
                if (_lastSeen == LastSeen.Value)
                {
                    return "Expected a separator between values";
                }

                if (_lastSeen == LastSeen.OpenObject)
                {
                    return "Expected string key after '{', not '{'";
                }

                if (_lastSeen == LastSeen.Comma)
                {
                    var last = libjq.jv_array_get(libjq.jv_copy(_path), _pathLength - 1);
                    var kind = last.Kind;
                    libjq.jv_free(last);
                    if (kind != jv_kind.JV_KIND_NUMBER)
                    {
                        return "Expected string key after ',' in object, not '{'";
                    }
                }

                _path = libjq.jv_array_append(_path, libjq.jv_null());
                _lastSeen = LastSeen.OpenObject;
                _pathLength++;
                break;
            case (byte)':':
            {
                var last = libjq.jv_invalid();
                if (_pathLength != 0)
                {
                    last = libjq.jv_array_get(libjq.jv_copy(_path), _pathLength - 1);
                }

                if (_pathLength == 0 || last.Kind == jv_kind.JV_KIND_NUMBER)
                {
                    libjq.jv_free(last);
                    return "':' not as part of an object";
                }

                libjq.jv_free(last);

                if (!_next.IsValid || _lastSeen == LastSeen.None)
                {
                    return "Expected string key before ':'";
                }

                if (_next.Kind != jv_kind.JV_KIND_STRING)
                {
                    return "Object keys must be strings";
                }

                if (_lastSeen != LastSeen.Value)
                {
                    return "':' should follow a key";
                }

                _lastSeen = LastSeen.Colon;
                _path = libjq.jv_array_set(_path, _pathLength - 1, _next);
                _next = libjq.jv_invalid();
                break;
            }
            case (byte)',':
            {
                if (_lastSeen != LastSeen.Value)
                {
                    return "Expected value before ','";
                }

                if (_pathLength == 0)
                {
                    return "',' not as part of an object or array";
                }

                var last = libjq.jv_array_get(libjq.jv_copy(_path), _pathLength - 1);
                var kind = last.Kind;
                if (kind == jv_kind.JV_KIND_NUMBER)
                {
                    var index = last.NumberValue;
                    if (_next.IsValid)
                    {
                        _output = libjq.jv_array([libjq.jv_copy(_path), _next]);
                        _next = libjq.jv_invalid();
                    }

                    _path = libjq.jv_array_set(
                        _path,
                        _pathLength - 1,
                        libjq.jv_number(index + 1));
                    _lastSeen = LastSeen.Comma;
                }
                else if (kind == jv_kind.JV_KIND_STRING)
                {
                    if (_next.IsValid)
                    {
                        _output = libjq.jv_array([libjq.jv_copy(_path), _next]);
                        _next = libjq.jv_invalid();
                    }

                    _path = libjq.jv_array_set(_path, _pathLength - 1, libjq.jv_null());
                    _lastSeen = LastSeen.Comma;
                }
                else
                {
                    libjq.jv_free(last);
                    return "Objects must consist of key:value pairs";
                }

                libjq.jv_free(last);
                break;
            }
            case (byte)']':
            {
                if (_pathLength == 0)
                {
                    return "Unmatched ']' at the top-level";
                }

                if (_lastSeen == LastSeen.Comma)
                {
                    return "Expected another array element";
                }

                var last = libjq.jv_array_get(libjq.jv_copy(_path), _pathLength - 1);
                var kind = last.Kind;
                libjq.jv_free(last);
                if (kind != jv_kind.JV_KIND_NUMBER)
                {
                    return "Unmatched ']' in the middle of an object";
                }

                if (_next.IsValid)
                {
                    _output = libjq.jv_array([libjq.jv_copy(_path), _next, libjq.jv_true()]);
                    _next = libjq.jv_invalid();
                }
                else if (_lastSeen != LastSeen.OpenArray)
                {
                    _output = libjq.jv_array([libjq.jv_copy(_path)]);
                }

                _path = libjq.jv_array_slice(_path, 0, --_pathLength);
                _next = libjq.jv_invalid();
                if (_lastSeen == LastSeen.OpenArray)
                {
                    _output = libjq.jv_array([libjq.jv_copy(_path), libjq.jv_array()]);
                }

                _lastSeen = _pathLength == 0 ? LastSeen.None : LastSeen.Value;
                break;
            }
            case (byte)'}':
            {
                if (_pathLength == 0)
                {
                    return "Unmatched '}' at the top-level";
                }

                if (_lastSeen == LastSeen.Comma)
                {
                    return "Expected another key:value pair";
                }

                var last = libjq.jv_array_get(libjq.jv_copy(_path), _pathLength - 1);
                var kind = last.Kind;
                libjq.jv_free(last);
                if (kind == jv_kind.JV_KIND_NUMBER)
                {
                    return "Unmatched '}' in the middle of an array";
                }

                if (_next.IsValid)
                {
                    if (kind != jv_kind.JV_KIND_STRING)
                    {
                        return "Objects must consist of key:value pairs";
                    }

                    _output = libjq.jv_array([libjq.jv_copy(_path), _next, libjq.jv_true()]);
                    _next = libjq.jv_invalid();
                }
                else
                {
                    if (_lastSeen == LastSeen.Colon)
                    {
                        return "Missing value in key:value pair";
                    }

                    if (_lastSeen == LastSeen.Comma)
                    {
                        return "Expected another key-value pair";
                    }

                    if (_lastSeen == LastSeen.OpenArray)
                    {
                        return "Unmatched '}' in the middle of an array";
                    }

                    if (_lastSeen is not (LastSeen.Value or LastSeen.OpenObject))
                    {
                        return "Unmatched '}'";
                    }

                    if (_lastSeen != LastSeen.OpenObject)
                    {
                        _output = libjq.jv_array([libjq.jv_copy(_path)]);
                    }
                }

                _path = libjq.jv_array_slice(_path, 0, --_pathLength);
                _next = libjq.jv_invalid();
                if (_lastSeen == LastSeen.OpenObject)
                {
                    _output = libjq.jv_array([libjq.jv_copy(_path), libjq.jv_object()]);
                }

                _lastSeen = _pathLength == 0 ? LastSeen.None : LastSeen.Value;
                break;
            }
        }

        return null;
    }

    private string? FoundString()
    {
        var output = new List<byte>(_token.Count);
        for (var input = 0; input < _token.Count; input++)
        {
            var character = _token[input];
            if (character == (byte)'\\')
            {
                if (++input >= _token.Count)
                {
                    return "Expected escape character at end of string";
                }

                character = _token[input];
                switch (character)
                {
                    case (byte)'\\':
                    case (byte)'"':
                    case (byte)'/':
                        output.Add(character);
                        break;
                    case (byte)'b': output.Add((byte)'\b'); break;
                    case (byte)'f': output.Add((byte)'\f'); break;
                    case (byte)'t': output.Add((byte)'\t'); break;
                    case (byte)'n': output.Add((byte)'\n'); break;
                    case (byte)'r': output.Add((byte)'\r'); break;
                    case (byte)'u':
                    {
                        if (input + 4 >= _token.Count)
                        {
                            return "Invalid \\uXXXX escape";
                        }

                        var codepoint = Unhex4(_token, input + 1);
                        if (codepoint < 0)
                        {
                            return "Invalid characters in \\uXXXX escape";
                        }

                        input += 4;
                        if (codepoint is >= 0xD800 and <= 0xDBFF)
                        {
                            if (input + 6 >= _token.Count ||
                                _token[input + 1] != (byte)'\\' ||
                                _token[input + 2] != (byte)'u')
                            {
                                return "Invalid \\uXXXX\\uXXXX surrogate pair escape";
                            }

                            var surrogate = Unhex4(_token, input + 3);
                            if (surrogate is < 0xDC00 or > 0xDFFF)
                            {
                                return "Invalid \\uXXXX\\uXXXX surrogate pair escape";
                            }

                            input += 6;
                            codepoint = 0x10000 + (((codepoint - 0xD800) << 10) |
                                (surrogate - 0xDC00));
                        }

                        AppendCodepoint(output, codepoint);
                        break;
                    }
                    default:
                        return "Invalid escape";
                }
            }
            else
            {
                if (character <= 0x1f)
                {
                    return "Invalid string: control characters from U+0000 through U+001F must be escaped";
                }

                output.Add(character);
            }
        }

        var message = Value(libjq.jv_string_sized(output.ToArray(), output.Count));
        _token.Clear();
        return message;
    }

    private static int Unhex4(List<byte> bytes, int start)
    {
        var result = 0;
        for (var index = start; index < start + 4; index++)
        {
            var character = bytes[index];
            var digit = character switch
            {
                >= (byte)'0' and <= (byte)'9' => character - (byte)'0',
                >= (byte)'a' and <= (byte)'f' => character - (byte)'a' + 10,
                >= (byte)'A' and <= (byte)'F' => character - (byte)'A' + 10,
                _ => -1,
            };
            if (digit < 0)
            {
                return -1;
            }

            result = (result << 4) | digit;
        }

        return result;
    }

    private static void AppendCodepoint(List<byte> output, int codepoint)
    {
        if (codepoint > libjq.JVP_UNICODE_MAX_CODEPOINT)
        {
            codepoint = libjq.JVP_UNICODE_REPLACEMENT_CODEPOINT;
        }

        // jq-1.8.2 found_string() writes every decoded escape through
        // jvp_utf8_encode(), including a lone low surrogate. The subsequent
        // jv_string_sized() call performs jq's malformed-unit repair.
        Span<byte> encoded = stackalloc byte[4];
        var length = libjq.jvp_utf8_encode(codepoint, encoded);
        for (var index = 0; index < length; index++)
        {
            output.Add(encoded[index]);
        }
    }

    private string? CheckLiteral()
    {
        if (_token.Count == 0)
        {
            return null;
        }

        jv value;
        if (_token[0] == (byte)'t')
        {
            if (!TokenEquals("true"))
            {
                return "Invalid literal";
            }

            value = libjq.jv_true();
        }
        else if (_token[0] == (byte)'f')
        {
            if (!TokenEquals("false"))
            {
                return "Invalid literal";
            }

            value = libjq.jv_false();
        }
        else if (_token[0] == (byte)'\'')
        {
            return "Invalid string literal; expected \", but got '";
        }
        else if (_token.Count > 1 && _token[0] == (byte)'n' && _token[1] == (byte)'u')
        {
            if (!TokenEquals("null"))
            {
                return "Invalid literal";
            }

            value = libjq.jv_null();
        }
        else
        {
            var literal = Encoding.ASCII.GetString(_token.ToArray());
            value = libjq.jv_number_with_literal(literal);
            if (!value.IsValid)
            {
                return "Invalid numeric literal";
            }
        }

        var message = Value(value);
        _token.Clear();
        return message;
    }

    private bool TokenEquals(string value)
    {
        if (_token.Count != value.Length)
        {
            return false;
        }

        for (var index = 0; index < value.Length; index++)
        {
            if (_token[index] != (byte)value[index])
            {
                return false;
            }
        }

        return true;
    }

    private string? Value(jv value)
    {
        if (IsStreaming)
        {
            if (_next.IsValid || _lastSeen == LastSeen.Value)
            {
                libjq.jv_free(value);
                return "Expected separator between values";
            }

            _lastSeen = _pathLength > 0 ? LastSeen.Value : LastSeen.None;
        }
        else if (_next.IsValid)
        {
            libjq.jv_free(value);
            return "Expected separator between values";
        }

        libjq.jv_free(_next);
        _next = value;
        return null;
    }

    private bool CheckDone(ref jv value) => IsStreaming
        ? StreamCheckDone(ref value)
        : ParseCheckDone(ref value);

    private bool ParseCheckDone(ref jv value)
    {
        if (_stack.Count == 0 && _next.IsValid)
        {
            value = _next;
            _next = libjq.jv_invalid();
            return true;
        }

        return false;
    }

    private bool StreamCheckDone(ref jv value)
    {
        if (_pathLength == 0 && _next.IsValid)
        {
            value = libjq.jv_array([libjq.jv_copy(_path), _next]);
            _next = libjq.jv_invalid();
            return true;
        }

        if (_output.IsValid)
        {
            if (libjq.jv_array_length(libjq.jv_copy(_output)) > 2)
            {
                value = libjq.jv_array_slice(libjq.jv_copy(_output), 0, 2);
                _output = libjq.jv_array_slice(_output, 0, 1);
            }
            else
            {
                value = _output;
                _output = libjq.jv_invalid();
            }

            return true;
        }

        return false;
    }

    private bool CheckTruncation()
    {
        if (IsStreaming)
        {
            return _pathLength > 0 || _next.Kind is
                jv_kind.JV_KIND_NUMBER or
                jv_kind.JV_KIND_TRUE or
                jv_kind.JV_KIND_FALSE or
                jv_kind.JV_KIND_NULL;
        }

        return !_lastCharacterWasWhitespace &&
            (_stack.Count > 0 || _token.Count > 0 || _next.Kind == jv_kind.JV_KIND_NUMBER);
    }

    private bool IsTopNumber() =>
        (IsStreaming ? _pathLength == 0 : _stack.Count == 0) &&
        _next.Kind == jv_kind.JV_KIND_NUMBER;

    private jv Error(string message) => EmitsStreamErrors
        ? libjq.jv_array([libjq.jv_string(message), libjq.jv_copy(_path)])
        : libjq.jv_invalid_with_msg(libjq.jv_string(message));

    private void Reset()
    {
        if (IsStreaming)
        {
            libjq.jv_free(_path);
            _path = libjq.jv_array();
            _pathLength = 0;
        }

        _lastSeen = LastSeen.None;
        libjq.jv_free(_output);
        _output = libjq.jv_invalid();
        libjq.jv_free(_next);
        _next = libjq.jv_invalid();
        foreach (var value in _stack)
        {
            libjq.jv_free(value);
        }

        _stack.Clear();
        _token.Clear();
        _state = ParserState.Normal;
    }

    private void ThrowIfFreed()
    {
        ObjectDisposedException.ThrowIf(_freed, this);
    }

    private static CharacterClass Classify(byte character) => character switch
    {
        (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' => CharacterClass.Whitespace,
        (byte)'"' => CharacterClass.Quote,
        (byte)'[' or (byte)',' or (byte)']' or (byte)'{' or (byte)':' or (byte)'}' =>
            CharacterClass.Structure,
        _ => CharacterClass.Literal,
    };

    private enum LastSeen
    {
        None,
        OpenArray,
        OpenObject,
        Colon,
        Comma,
        Value,
    }

    private enum ParserState
    {
        Normal,
        String,
        StringEscape,
        WaitingForRecordSeparator,
    }

    private enum CharacterClass
    {
        Literal,
        Whitespace,
        Structure,
        Quote,
    }
}
