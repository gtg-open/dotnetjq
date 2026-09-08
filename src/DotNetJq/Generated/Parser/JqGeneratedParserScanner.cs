// Adapter from the current managed jq lexer to GPPG's scanner contract.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using DotNetJq.Port;
using StarodubOleg.GPPG.Runtime;

namespace DotNetJq.Port.GeneratedParser;

internal sealed class JqGeneratedParserScanner
    : AbstractScanner<ValueType, JqParserLocation>
{
    private readonly string source;
    private readonly jq_lexer lexer;
    private Token? pending;
    private Token? currentToken;
    private Token? previousToken;
    private JqParserLocation currentLocation = new();
    private readonly Stack<DelimiterFrame> delimiters = [];
    private bool popDelimiterBeforeNextToken;
    private int tokensRead;
    // GPPG 1.2.5 has no equivalent of Bison's %destructor.  Keep the one
    // owner created for every <literal> semantic value until a grammar action
    // explicitly takes/frees it, then release anything discarded by recovery
    // when the parse ends.  The key is the allocation identity, not jq value
    // equality (which is itself consuming in jq).
    private readonly Dictionary<object, jv> ownedLiterals =
        new(ReferenceEqualityComparer.Instance);
    private readonly List<jv> scannedLiterals = [];

    internal List<GeneratedParserDiagnostic> Diagnostics { get; } = [];

    internal IReadOnlyList<jv> ScannedLiterals => scannedLiterals;

    internal int? LastErrorObjectEntryStartIndex { get; private set; }

    internal int? LastErrorObjectEntryEndIndex { get; private set; }

    private bool trackNumericObjectEntrySpan;

    public override JqParserLocation yylloc
    {
        get => currentLocation;
        set => currentLocation = value;
    }

    internal JqGeneratedParserScanner(string source)
    {
        this.source = source;
        // Parser mode must expose INVALID_CHARACTER and EOF to GPPG so its
        // grammar recovery and jq-shaped diagnostics remain authoritative.
        lexer = new jq_lexer(source, rawParserMode: true);
    }

    public override int yylex()
    {
        if (popDelimiterBeforeNextToken)
        {
            _ = delimiters.Pop();
            popDelimiterBeforeNextToken = false;
        }

        yylval = default;
        var token = Read();

        // lexer.l has a single FIELD token, while the current managed scanner
        // deliberately exposes an adjacent Dot + Identifier pair.
        if (token.Kind == TokenKind.Dot)
        {
            var next = Read();
            if (next.Kind == TokenKind.Identifier &&
                next.SourceIndex == token.SourceIndex + token.Text.Length)
            {
                yylval.literal = OwnLiteral(libjq.jv_string(next.Text));
                SetLocation(token, next);
                return (int)JqParserToken.FIELD;
            }

            pending = next;
        }

        SetLocation(token, token);
        switch (token.Kind)
        {
            case TokenKind.Identifier:
                var keyword = Keyword(token.Text);
                if (keyword == (int)JqParserToken.IDENT)
                {
                    yylval.literal = OwnLiteral(libjq.jv_string(token.Text));
                }

                return keyword;
            case TokenKind.Binding:
                yylval.literal = OwnLiteral(libjq.jv_string(token.Text[1..]));
                return (int)JqParserToken.BINDING;
            case TokenKind.Number:
                yylval.literal = OwnLiteral(libjq.jv_parse(token.Text));
                return (int)JqParserToken.LITERAL;
            case TokenKind.Format:
                yylval.literal = OwnLiteral(libjq.jv_string(token.Text[1..]));
                return (int)JqParserToken.FORMAT;
            case TokenKind.StringText:
                yylval.literal = OwnLiteral(libjq.jv_string(DecodeStringFragment(token)));
                return (int)JqParserToken.QQSTRING_TEXT;
            case TokenKind.End:
                return (int)JqParserToken.EOF;
            case TokenKind.InvalidCharacter:
                return (int)JqParserToken.INVALID_CHARACTER;
            case TokenKind.StringStart:
                return (int)JqParserToken.QQSTRING_START;
            case TokenKind.StringInterpolationStart:
                return (int)JqParserToken.QQSTRING_INTERP_START;
            case TokenKind.StringInterpolationEnd:
                return (int)JqParserToken.QQSTRING_INTERP_END;
            case TokenKind.StringEnd:
                return (int)JqParserToken.QQSTRING_END;
            case TokenKind.Location:
                return (int)JqParserToken.LOC;
            case TokenKind.Recursive:
                return (int)JqParserToken.REC;
            case TokenKind.Equal:
                return (int)JqParserToken.EQ;
            case TokenKind.NotEqual:
                return (int)JqParserToken.NEQ;
            case TokenKind.DefinedOr:
                return (int)JqParserToken.DEFINEDOR;
            case TokenKind.PipeAssign:
                return (int)JqParserToken.SETPIPE;
            case TokenKind.PlusAssign:
                return (int)JqParserToken.SETPLUS;
            case TokenKind.MinusAssign:
                return (int)JqParserToken.SETMINUS;
            case TokenKind.MultiplyAssign:
                return (int)JqParserToken.SETMULT;
            case TokenKind.DivideAssign:
                return (int)JqParserToken.SETDIV;
            case TokenKind.ModuloAssign:
                return (int)JqParserToken.SETMOD;
            case TokenKind.DefinedOrAssign:
                return (int)JqParserToken.SETDEFINEDOR;
            case TokenKind.LessEqual:
                return (int)JqParserToken.LESSEQ;
            case TokenKind.GreaterEqual:
                return (int)JqParserToken.GREATEREQ;
            case TokenKind.DestructureAlternative:
                return (int)JqParserToken.ALTERNATION;
            default:
                return token.Kind switch
                {
                    TokenKind.Dollar => '$',
                    TokenKind.Dot => '.',
                    TokenKind.LeftParenthesis => '(',
                    TokenKind.RightParenthesis => ')',
                    TokenKind.LeftBracket => '[',
                    TokenKind.RightBracket => ']',
                    TokenKind.LeftBrace => '{',
                    TokenKind.RightBrace => '}',
                    TokenKind.Colon => ':',
                    TokenKind.Semicolon => ';',
                    TokenKind.Comma => ',',
                    TokenKind.Pipe => '|',
                    TokenKind.Question => '?',
                    TokenKind.Plus => '+',
                    TokenKind.Minus => '-',
                    TokenKind.Multiply => '*',
                    TokenKind.Divide => '/',
                    TokenKind.Modulo => '%',
                    TokenKind.Assign => '=',
                    TokenKind.Less => '<',
                    TokenKind.Greater => '>',
                    _ => throw new InvalidOperationException(
                        "No GPPG token mapping for " + token.Kind),
                };
        }
    }

    internal jv OwnLiteral(jv value)
    {
        scannedLiterals.Add(value);
        if (value.Value is { } allocation)
        {
            ownedLiterals.Add(allocation, value);
        }

        return value;
    }

    internal void RelinquishLiteral(jv value)
    {
        if (value.Value is { } allocation)
        {
            _ = ownedLiterals.Remove(allocation);
        }
    }

    internal void FreeUnclaimedLiterals()
    {
        foreach (var value in ownedLiterals.Values)
        {
            libjq.jv_free(value);
        }

        ownedLiterals.Clear();
    }

    public override void yyerror(string format, params object[] args)
    {
        var message = args.Length == 0
            ? format
            : string.Format(CultureInfo.InvariantCulture, format, args);
        var location = currentToken is { Kind: TokenKind.End } &&
            lexer.LastMatchSpan is { } lastMatch
                ? EndOfFileDiagnosticLocation(lastMatch)
                : yylloc;
        LastErrorObjectEntryStartIndex =
            delimiters.TryPeek(out var delimiter) &&
            delimiter.OpeningKind == TokenKind.LeftBrace
                ? delimiter.EntryStartIndex
                : null;
        if (currentToken is { Kind: TokenKind.Number } invalidNumber &&
            delimiters.TryPeek(out var objectDelimiter) &&
            objectDelimiter.OpeningKind == TokenKind.LeftBrace &&
            !objectDelimiter.IsPattern &&
            objectDelimiter.EntryStartIndex == invalidNumber.SourceIndex)
        {
            // DictPair recovery's contextual span ends before ':'. ObjPat
            // recovery instead spans through its binding pattern.
            trackNumericObjectEntrySpan = true;
        }
        var normalized = NormalizeSyntaxMessage(message);
        if (currentToken is { Kind: TokenKind.InvalidCharacter or TokenKind.End } &&
            delimiters.Count != 0)
        {
            // A closer rejected by the lexer's delimiter state, or physical
            // EOF while that state remains open, is not a parser lookahead
            // alternative. jq omits the enclosing state's expected suffix.
            var expecting = normalized.IndexOf(
                ", expecting ",
                StringComparison.Ordinal);
            if (expecting >= 0)
            {
                normalized = normalized[..expecting];
            }
        }
        if (currentToken is { Kind: TokenKind.InvalidCharacter } &&
            tokensRead == 1 &&
            !normalized.Contains(", expecting ", StringComparison.Ordinal))
        {
            normalized += ", expecting end of file";
        }
        Diagnostics.Add(new GeneratedParserDiagnostic(normalized, location));
    }

    private static JqParserLocation EndOfFileDiagnosticLocation(
        jq_lexer.LexerMatchSpan match) => new(
            match.StartByte,
            match.EndByte,
            match.StartIndex,
            match.EndIndex,
            match.StartLine,
            match.StartColumn,
            match.EndLine,
            match.EndColumn);

    private Token Read()
    {
        if (pending is not { } token)
        {
            token = lexer.Next();
        }

        else
        {
            pending = null;
        }

        tokensRead++;
        return token;
    }

    private static int Keyword(string text) => text switch
    {
        "as" => (int)JqParserToken.AS,
        "def" => (int)JqParserToken.DEF,
        "module" => (int)JqParserToken.MODULE,
        "import" => (int)JqParserToken.IMPORT,
        "include" => (int)JqParserToken.INCLUDE,
        "if" => (int)JqParserToken.IF,
        "then" => (int)JqParserToken.THEN,
        "else" => (int)JqParserToken.ELSE,
        "elif" => (int)JqParserToken.ELSE_IF,
        "reduce" => (int)JqParserToken.REDUCE,
        "foreach" => (int)JqParserToken.FOREACH,
        "end" => (int)JqParserToken.END,
        "and" => (int)JqParserToken.AND,
        "or" => (int)JqParserToken.OR,
        "try" => (int)JqParserToken.TRY,
        "catch" => (int)JqParserToken.CATCH,
        "label" => (int)JqParserToken.LABEL,
        "break" => (int)JqParserToken.BREAK,
        _ => (int)JqParserToken.IDENT,
    };

    private string DecodeStringFragment(Token token)
    {
        var raw = token.Text;
        var result = new StringBuilder(raw.Length);
        var position = 0;
        while (position < raw.Length)
        {
            var escapeStart = raw.IndexOf('\\', position);
            if (escapeStart < 0)
            {
                result.Append(raw.AsSpan(position));
                break;
            }

            result.Append(raw.AsSpan(position, escapeStart - position));
            var escapeEnd = FindEscapeRunEnd(raw, escapeStart);
            var escapeRun = raw[escapeStart..escapeEnd];
            var error = ValidateEscapeRun(escapeRun);
            if (error is not null)
            {
                var innerLocation = GetSyntheticEscapeErrorLocation(escapeRun);
                Diagnostics.Add(new GeneratedParserDiagnostic(
                    $"{error} at line {innerLocation.Line}, column {innerLocation.Column} " +
                    $"(while parsing '\"{escapeRun}\"')",
                    LocationForSpan(token.SourceIndex + escapeStart, escapeRun)));
                return string.Empty;
            }

            var parsed = libjq.jv_parse("\"" + escapeRun + "\"");
            try
            {
                result.Append(parsed.StringValue);
            }
            finally
            {
                // jq-1.8.2 lexer.l frees its conversion-only `escapes`
                // value.  Here the quoted managed text is not a jv, while the
                // jv_parse result is the corresponding temporary owner.
                libjq.jv_free(parsed);
            }
            position = escapeEnd;
        }

        return result.ToString();
    }

    private static int FindEscapeRunEnd(string raw, int start)
    {
        var position = start;
        while (position < raw.Length && raw[position] == '\\')
        {
            if (position + 1 >= raw.Length)
            {
                return position + 1;
            }

            if (raw[position + 1] != 'u')
            {
                position += 2;
                continue;
            }

            position += 2;
            for (var count = 0;
                 count < 4 && position < raw.Length && IsAsciiAlphaNumeric(raw[position]);
                 count++)
            {
                position++;
            }
        }

        return position;
    }

    private static string? ValidateEscapeRun(string escapeRun)
    {
        var position = 0;
        while (position < escapeRun.Length)
        {
            if (position + 1 >= escapeRun.Length)
            {
                return "Invalid escape";
            }

            var escaped = escapeRun[position + 1];
            if (escaped != 'u')
            {
                if (escaped is not ('\"' or '\\' or '/' or 'b' or 'f' or 'n' or 'r' or 't'))
                {
                    return "Invalid escape";
                }

                position += 2;
                continue;
            }

            if (position + 6 > escapeRun.Length)
            {
                return "Invalid \\uXXXX escape";
            }

            if (!TryParseHexQuad(escapeRun.AsSpan(position + 2, 4), out var scalar))
            {
                return "Invalid characters in \\uXXXX escape";
            }

            position += 6;
            if (!char.IsHighSurrogate((char)scalar))
            {
                continue;
            }

            if (position + 6 > escapeRun.Length ||
                escapeRun[position] != '\\' ||
                escapeRun[position + 1] != 'u' ||
                !TryParseHexQuad(escapeRun.AsSpan(position + 2, 4), out var low) ||
                !char.IsLowSurrogate((char)low))
            {
                return "Invalid \\uXXXX\\uXXXX surrogate pair escape";
            }

            position += 6;
        }

        return null;
    }

    private static bool TryParseHexQuad(ReadOnlySpan<char> text, out int value)
    {
        value = 0;
        foreach (var character in text)
        {
            value <<= 4;
            if (character is >= '0' and <= '9')
            {
                value += character - '0';
            }
            else if (character is >= 'a' and <= 'f')
            {
                value += character - 'a' + 10;
            }
            else if (character is >= 'A' and <= 'F')
            {
                value += character - 'A' + 10;
            }
            else
            {
                value = 0;
                return false;
            }
        }

        return true;
    }

    private static (int Line, int Column) GetSyntheticEscapeErrorLocation(string escapeRun)
    {
        var line = 1;
        var column = 2;
        foreach (var rune in escapeRun.EnumerateRunes())
        {
            if (rune.Value == '\n')
            {
                line++;
                column = 1;
            }
            else
            {
                column += rune.Utf8SequenceLength;
            }
        }

        return (line, column);
    }

    private static bool IsAsciiAlphaNumeric(char value) =>
        value is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';

    private JqParserLocation LocationForSpan(int startIndex, string span)
    {
        var prefix = source.AsSpan(0, startIndex);
        var startByte = Encoding.UTF8.GetByteCount(prefix);
        var startLine = 1;
        var startLineIndex = 0;
        for (var index = 0; index < prefix.Length; index++)
        {
            if (prefix[index] == '\n')
            {
                startLine++;
                startLineIndex = index + 1;
            }
        }

        var startColumn = Encoding.UTF8.GetByteCount(
            source.AsSpan(startLineIndex, startIndex - startLineIndex)) + 1;
        var token = new Token(
            TokenKind.StringText,
            span,
            startByte,
            startIndex,
            startLine,
            startColumn);
        return Location(token, token);
    }

    private void SetLocation(Token first, Token last)
    {
        previousToken = currentToken;
        currentToken = last;
        yylloc = Location(first, last);
        if (last.Kind is TokenKind.LeftParenthesis or
            TokenKind.LeftBracket or TokenKind.LeftBrace)
        {
            // GPPG needs one fewer non-source stack slot than the pinned Bison
            // parser for a pure delimiter nest. Stop at jq's observable
            // 9,994/9,995 balanced-parenthesis boundary instead of accepting
            // one extra level before the shared 10,000-entry stack is full.
            if (delimiters.Count >= JqGeneratedParser.MaximumNestedParenthesisDepth)
            {
                throw new ParserStackExhaustedException();
            }

            delimiters.Push(new DelimiterFrame(
                last.Kind,
                last.SourceIndex + last.Text.Length,
                (previousToken is { Kind: TokenKind.Identifier, Text: "as" }) ||
                previousToken is { Kind: TokenKind.DestructureAlternative } ||
                (delimiters.TryPeek(out var parent) && parent.IsPattern)));
        }
        else if (last.Kind == TokenKind.Comma &&
                 delimiters.TryPeek(out var delimiter) &&
                 delimiter.OpeningKind == TokenKind.LeftBrace)
        {
            _ = delimiters.Pop();
            delimiters.Push(delimiter with
            {
                EntryStartIndex = last.SourceIndex + last.Text.Length,
            });
        }
        else if (last.Kind == TokenKind.Colon &&
                 trackNumericObjectEntrySpan &&
                 LastErrorObjectEntryStartIndex is not null &&
                 delimiters.TryPeek(out var objectDelimiter) &&
                 objectDelimiter.OpeningKind == TokenKind.LeftBrace)
        {
            LastErrorObjectEntryEndIndex = last.SourceIndex;
        }
        else if (last.Kind is TokenKind.RightParenthesis or
                 TokenKind.RightBracket or TokenKind.RightBrace)
        {
            popDelimiterBeforeNextToken = delimiters.Count != 0;
        }
    }

    private static string NormalizeSyntaxMessage(string message)
    {
        if (message.StartsWith("Syntax error", StringComparison.Ordinal))
        {
            message = "syntax error" + message["Syntax error".Length..];
        }

        message = message.Replace("EOF", "end of file", StringComparison.Ordinal);
        const string expectingMarker = ", expecting ";
        var expecting = message.IndexOf(expectingMarker, StringComparison.Ordinal);
        if (expecting < 0)
        {
            return message;
        }

        var alternatives = message[(expecting + expectingMarker.Length)..]
            .Split(", or ", StringSplitOptions.RemoveEmptyEntries)
            .Where(static item => item != "error")
            .ToArray();
        message = message[..expecting] +
            (alternatives.Length == 0
                ? string.Empty
                : expectingMarker + string.Join(" or ", alternatives));
        message = message.Replace(
            "unexpected ':', expecting ',' or '}'",
            "unexpected ':', expecting '}'",
            StringComparison.Ordinal);
        // GPPG exposes reduce-lookahead candidates here that pinned Bison does
        // not include after a completed `as` destructuring pattern.  This is a
        // diagnostic-only projection: both parsers reject the same `?` token.
        message = message.Replace(
            "unexpected '?', expecting ?// or '|' or '('",
            "unexpected '?', expecting '|'",
            StringComparison.Ordinal);
        return message.Replace(
            "expecting QQSTRING_END or QQSTRING_TEXT or QQSTRING_INTERP_START",
            "expecting QQSTRING_TEXT or QQSTRING_INTERP_START or QQSTRING_END",
            StringComparison.Ordinal);
    }

    private static JqParserLocation Location(Token first, Token last)
    {
        var (endLine, endColumn) = End(last);
        return new JqParserLocation(
            first.Offset,
            last.Offset + last.ByteLength,
            first.SourceIndex,
            last.SourceIndex + last.Text.Length,
            first.Line,
            first.Column,
            endLine,
            endColumn);
    }

    private static (int Line, int Column) End(Token token)
    {
        var line = token.Line;
        var column = token.Column;
        foreach (var rune in token.Text.EnumerateRunes())
        {
            if (rune.Value == '\n')
            {
                line++;
                column = 1;
            }
            else
            {
                column += rune.Utf8SequenceLength;
            }
        }

        return (line, column);
    }

    private sealed record DelimiterFrame(
        TokenKind OpeningKind,
        int EntryStartIndex,
        bool IsPattern);
}
