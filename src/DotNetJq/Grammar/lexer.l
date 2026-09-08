// Managed GPLEX source for the jq lexer.
//
// jq-port-upstream-repository: https://github.com/jqlang/jq
// jq-port-upstream-revision: 34f7186b86743a083a589741b6cea95293524108
// jq-port-upstream-path: src/lexer.l
// Upstream release: jq-1.8.2
// Upstream URL: https://github.com/jqlang/jq/blob/34f7186b86743a083a589741b6cea95293524108/src/lexer.l
//
// The rule order and start-condition structure intentionally track the pinned
// file. The embedded semantic actions are the language-specific managed C# port
// for DotNetJq.Port.Token and TokenKind; they are not copied C actions.

%using DotNetJq.Port;
%namespace DotNetJq.Port

%visibility internal
%scannertype jq_lexer

%option unicode, stack, classes, minimize, noparser, persistbuffer, nofiles

%{
    private const int EmittedToken = 1;
    private const int QqStringInterpolationOpening = -1;

    private readonly Queue<Token> pending = new Queue<Token>();
    private readonly bool rawParserMode;
    private string jqSource = string.Empty;
    private int coordinateSourceIndex;
    private int coordinateByteOffset;
    private int coordinateLine = 1;
    private int coordinateColumn = 1;
    private int lastTokenSourceIndex = -1;

    internal LexerMatchSpan? LastMatchSpan { get; private set; }

    internal readonly record struct LexerMatchSpan(
        int StartByte,
        int EndByte,
        int StartIndex,
        int EndIndex,
        int StartLine,
        int StartColumn,
        int EndLine,
        int EndColumn);

    // GPLEX also generates a parameterless constructor. Callers must
    // use this overload so the jq byte-based coordinate cursor is initialized.
    internal jq_lexer(string source)
        : this(source, rawParserMode: false)
    {
    }

    // Raw mode preserves scanner-level INVALID_CHARACTER/EOF results for a
    // generated parser. The default adapter retains today's eager diagnostics.
    internal jq_lexer(string source, bool rawParserMode)
    {
        jqSource = source ?? throw new ArgumentNullException(nameof(source));
        this.rawParserMode = rawParserMode;
        SetSource(jqSource, 0);
    }

    // Adapter shape matching the current managed jq lexer. GPLEX's generated
    // yylex() remains the underlying one-token-at-a-time scanner entry point.
    internal Token Next()
    {
        if (pending.Count != 0)
            return pending.Dequeue();

        int code = yylex();
        if (pending.Count != 0)
            return pending.Dequeue();

        if (code == 0)
            return FinishAtEndOfInput();

        throw new InvalidOperationException("GPLEX returned without staging a jq token.");
    }

    private int Emit(TokenKind kind)
    {
        Token token = MakeToken(kind, yytext, yypos);
        RecordMatch(token, finalUtf8ByteOnly: false);
        pending.Enqueue(token);
        return EmittedToken;
    }

    private int HandleUnexpectedCharacter()
    {
        if (rawParserMode)
            return Emit(TokenKind.InvalidCharacter);

        Token token = MakeToken(TokenKind.InvalidCharacter, yytext, yypos);
        RecordMatch(token, finalUtf8ByteOnly: false);
        string message = yytext[0] == '@'
            ? "Invalid format name"
            : "Unexpected character " + yytext[0];
        throw new DotNetJq.JqCompileException(
            message + " at line " + token.Line + ", column " + token.Column);
    }

    private Token FinishAtEndOfInput()
    {
        Token end = MakeToken(TokenKind.End, string.Empty, jqSource.Length);
        if (rawParserMode)
            return end;

        bool insideInterpolation =
            YY_START == IN_QQINTERP || scStack.Contains(IN_QQINTERP);
        if (insideInterpolation)
        {
            throw new DotNetJq.JqCompileException(
                "Unterminated string interpolation at line " + end.Line +
                ", column " + end.Column);
        }

        if (YY_START == IN_QQSTRING)
        {
            throw new DotNetJq.JqCompileException(
                "Unterminated string at line " + end.Line +
                ", column " + end.Column);
        }

        return end;
    }

    // jq's FIELD carries the name without '.', while the current managed token
    // contract represents the same source as Dot followed by Identifier.
    private int EmitField()
    {
        RecordMatch(
            MakeToken(TokenKind.Identifier, yytext, yypos),
            finalUtf8ByteOnly: false);
        pending.Enqueue(MakeToken(TokenKind.Dot, ".", yypos));
        pending.Enqueue(MakeToken(TokenKind.Identifier, yytext.Substring(1), yypos + 1));
        return EmittedToken;
    }

    private Token MakeToken(TokenKind kind, string text, int sourceIndex)
    {
        if ((uint)sourceIndex > (uint)jqSource.Length)
            throw new InvalidOperationException("GPLEX produced an out-of-range source position.");
        if (sourceIndex < lastTokenSourceIndex)
            throw new InvalidOperationException("GPLEX produced tokens out of source order.");

        lastTokenSourceIndex = sourceIndex;
        AdvanceCoordinatesTo(sourceIndex);

        return new Token(
            kind,
            text,
            coordinateByteOffset,
            sourceIndex,
            coordinateLine,
            coordinateColumn);
    }

    private void RecordIgnoredMatch() => RecordMatch(
        MakeToken(TokenKind.InvalidCharacter, yytext, yypos),
        finalUtf8ByteOnly: false);

    private void RecordCommentMatch()
    {
        Token token = MakeToken(TokenKind.InvalidCharacter, yytext, yypos);
        bool finalUtf8ByteOnly =
            token.Text.Length != 0 &&
            token.Text[0] != '\\' &&
            token.Text[0] != '\r' &&
            token.Text[0] != '\n' &&
            token.ByteLength > 1;
        RecordMatch(token, finalUtf8ByteOnly);
    }

    private void RecordMatch(Token token, bool finalUtf8ByteOnly)
    {
        // GPLEX can revisit an accepting rule with an empty yytext while
        // unwinding an inclusive start condition at EOF. Flex does not run
        // YY_USER_ACTION for EOF, so retain the preceding non-empty match.
        if (token.Text.Length == 0)
            return;

        int endLine = token.Line;
        int endColumn = token.Column;
        foreach (Rune rune in token.Text.EnumerateRunes())
        {
            if (rune.Value == '\n')
            {
                endLine++;
                endColumn = 1;
            }
            else
            {
                endColumn += rune.Utf8SequenceLength;
            }
        }

        int byteLength = token.ByteLength;
        LastMatchSpan = finalUtf8ByteOnly
            ? new LexerMatchSpan(
                token.Offset + byteLength - 1,
                token.Offset + byteLength,
                token.SourceIndex,
                token.SourceIndex + token.Text.Length,
                endLine,
                endColumn - 1,
                endLine,
                endColumn)
            : new LexerMatchSpan(
                token.Offset,
                token.Offset + byteLength,
                token.SourceIndex,
                token.SourceIndex + token.Text.Length,
                token.Line,
                token.Column,
                endLine,
                endColumn);
    }

    // jq's locations are UTF-8 byte offsets and byte-based columns. GPLEX
    // scans UTF-16 positions. Token creation is monotonic, so advance a single
    // UTF-16-to-UTF-8 cursor instead of retaining three source-sized maps.
    private void AdvanceCoordinatesTo(int targetSourceIndex)
    {
        while (coordinateSourceIndex < targetSourceIndex)
        {
            char current = jqSource[coordinateSourceIndex];
            int consumed = 1;
            int utf8Length;
            if (current <= 0x7f)
            {
                utf8Length = 1;
            }
            else if (!char.IsSurrogate(current))
            {
                utf8Length = current <= 0x7ff ? 2 : 3;
            }
            else
            {
                Rune.DecodeFromUtf16(
                    jqSource.AsSpan(coordinateSourceIndex),
                    out Rune rune,
                    out consumed);
                if (coordinateSourceIndex + consumed > targetSourceIndex)
                {
                    // Match the old defensive map: the interior UTF-16 slot of
                    // a valid surrogate pair has the scalar's starting location.
                    return;
                }

                utf8Length = rune.Utf8SequenceLength;
            }

            coordinateSourceIndex += consumed;
            coordinateByteOffset += utf8Length;
            if (current == '\n')
            {
                coordinateLine++;
                coordinateColumn = 1;
            }
            else
            {
                coordinateColumn += utf8Length;
            }
        }
    }

    private int try_exit(char closing, int state)
    {
        char match;
        TokenKind result;
        switch (state)
        {
            case IN_PAREN:
                match = ')';
                result = TokenKind.RightParenthesis;
                break;
            case IN_BRACKET:
                match = ']';
                result = TokenKind.RightBracket;
                break;
            case IN_BRACE:
                match = '}';
                result = TokenKind.RightBrace;
                break;

            case IN_QQINTERP:
                match = ')';
                result = TokenKind.StringInterpolationEnd;
                break;

            default:
                // Keep jq's non-popping mismatch behavior. The parser owns the
                // eventual diagnostic wording.
                return Emit(TokenKind.InvalidCharacter);
        }

        if (match == closing)
        {
            yy_pop_state();
            return Emit(result);
        }

        // FIXME(upstream parity): jq asks whether a mismatched closer should
        // pop here; the pinned scanner deliberately leaves the state intact.
        return Emit(TokenKind.InvalidCharacter);
    }

    private int enter(int opening, int currentState)
    {
        // Preserve the upstream helper signature for source comparison. The
        // state stack itself already records currentState in yy_push_state().
        _ = currentState;

        int state;
        TokenKind result;
        switch (opening)
        {
            case '(':
                state = IN_PAREN;
                result = TokenKind.LeftParenthesis;
                break;
            case '[':
                state = IN_BRACKET;
                result = TokenKind.LeftBracket;
                break;
            case '{':
                state = IN_BRACE;
                result = TokenKind.LeftBrace;
                break;
            case QqStringInterpolationOpening:
                state = IN_QQINTERP;
                result = TokenKind.StringInterpolationStart;
                break;
            default:
                throw new InvalidOperationException("Unknown jq delimiter opener.");
        }

        yy_push_state(state);
        return Emit(result);
    }
%}

// Inclusive delimiter states keep the ordinary jq token rules active.
%s IN_PAREN
%s IN_BRACKET
%s IN_BRACE
%s IN_QQINTERP

// Quoted strings and comments are exclusive mini-scanners.
%x IN_QQSTRING
%x IN_COMMENT

%%

"#" { RecordIgnoredMatch(); yy_push_state(IN_COMMENT); }
<IN_COMMENT>{
  \\(\\|\r?\n)|. { RecordCommentMatch(); }
  \r?\n { RecordIgnoredMatch(); yy_pop_state(); }
}
<IN_COMMENT><<EOF>> { yy_pop_state(); }

"!=" { return Emit(TokenKind.NotEqual); }
"==" { return Emit(TokenKind.Equal); }
"as" { return Emit(TokenKind.Identifier); /* upstream AS */ }
"import" { return Emit(TokenKind.Identifier); /* upstream IMPORT */ }
"include" { return Emit(TokenKind.Identifier); /* upstream INCLUDE */ }
"module" { return Emit(TokenKind.Identifier); /* upstream MODULE */ }
"def" { return Emit(TokenKind.Identifier); /* upstream DEF */ }
"if" { return Emit(TokenKind.Identifier); /* upstream IF */ }
"then" { return Emit(TokenKind.Identifier); /* upstream THEN */ }
"else" { return Emit(TokenKind.Identifier); /* upstream ELSE */ }
"elif" { return Emit(TokenKind.Identifier); /* upstream ELSE_IF */ }
"and" { return Emit(TokenKind.Identifier); /* upstream AND */ }
"or" { return Emit(TokenKind.Identifier); /* upstream OR */ }
"end" { return Emit(TokenKind.Identifier); /* upstream END */ }
"reduce" { return Emit(TokenKind.Identifier); /* upstream REDUCE */ }
"foreach" { return Emit(TokenKind.Identifier); /* upstream FOREACH */ }
"//" { return Emit(TokenKind.DefinedOr); }
"try" { return Emit(TokenKind.Identifier); /* upstream TRY */ }
"catch" { return Emit(TokenKind.Identifier); /* upstream CATCH */ }
"label" { return Emit(TokenKind.Identifier); /* upstream LABEL */ }
"break" { return Emit(TokenKind.Identifier); /* upstream BREAK */ }
"$__loc__" { return Emit(TokenKind.Location); }
"|=" { return Emit(TokenKind.PipeAssign); }
"+=" { return Emit(TokenKind.PlusAssign); }
"-=" { return Emit(TokenKind.MinusAssign); }
"*=" { return Emit(TokenKind.MultiplyAssign); }
"/=" { return Emit(TokenKind.DivideAssign); }
"%=" { return Emit(TokenKind.ModuloAssign); }
"//=" { return Emit(TokenKind.DefinedOrAssign); }
"<=" { return Emit(TokenKind.LessEqual); }
">=" { return Emit(TokenKind.GreaterEqual); }
".." { return Emit(TokenKind.Recursive); }
"?//" { return Emit(TokenKind.DestructureAlternative); }
"."|"?"|"="|";"|","|":"|"|"|"+"|"-"|"*"|"/"|"%"|"\$"|"<"|">" {
  switch (yytext[0])
  {
    case '.': return Emit(TokenKind.Dot);
    case '?': return Emit(TokenKind.Question);
    case '=': return Emit(TokenKind.Assign);
    case ';': return Emit(TokenKind.Semicolon);
    case ',': return Emit(TokenKind.Comma);
    case ':': return Emit(TokenKind.Colon);
    case '|': return Emit(TokenKind.Pipe);
    case '+': return Emit(TokenKind.Plus);
    case '-': return Emit(TokenKind.Minus);
    case '*': return Emit(TokenKind.Multiply);
    case '/': return Emit(TokenKind.Divide);
    case '%': return Emit(TokenKind.Modulo);
    case '$': return Emit(TokenKind.Dollar);
    case '<': return Emit(TokenKind.Less);
    case '>': return Emit(TokenKind.Greater);
    default: return Emit(TokenKind.InvalidCharacter);
  }
}

"["|"{"|"(" {
  return enter(yytext[0], YY_START);
}

"]"|"}"|")" {
  return try_exit(yytext[0], YY_START);
}

"@"[a-zA-Z0-9_]+ {
  // The current managed parser derives the format name from Token.Text.
  return Emit(TokenKind.Format);
}
// GPLEX dialect substitution: upstream spells this class `[+-]`; escaping the
// hyphen avoids Springcomp.GPLEX warning 114 without changing the matched set.
([0-9]+(\.[0-9]*)?|\.[0-9]+)([eE][+\-]?[0-9]+)? {
  // Numeric jv construction belongs at the parser/semantic-value bridge.
  return Emit(TokenKind.Number);
}

"\"" {
  yy_push_state(IN_QQSTRING);
  return Emit(TokenKind.StringStart);
}

<IN_QQSTRING>{
  "\\(" {
    return enter(QqStringInterpolationOpening, YY_START);
  }
  "\"" {
    yy_pop_state();
    return Emit(TokenKind.StringEnd);
  }
  (\\[^u(]|\\u[a-zA-Z0-9]{0,4})+ {
    // Preserve the raw escape run. The existing parser applies jq's JSON
    // escape validation/decoding and therefore remains the semantic owner.
    return Emit(TokenKind.StringText);
  }
  [^\\\"]+ {
    return Emit(TokenKind.StringText);
  }
  . {
    return Emit(TokenKind.InvalidCharacter);
  }
}
([a-zA-Z_][a-zA-Z_0-9]*::)*[a-zA-Z_][a-zA-Z_0-9]* {
  return Emit(TokenKind.Identifier);
}
\.[a-zA-Z_][a-zA-Z_0-9]* {
  return EmitField();
}
\$([a-zA-Z_][a-zA-Z_0-9]*::)*[a-zA-Z_][a-zA-Z_0-9]* {
  return Emit(TokenKind.Binding);
}

[ \r\n\t]+ { RecordIgnoredMatch(); }

. { return HandleUnexpectedCharacter(); }

%%

// Perhaps these should be calls... (retained from the pinned source).
// true, false, and null deliberately remain identifiers; jq implements them
// through ordinary name resolution rather than lexer-only token kinds.
