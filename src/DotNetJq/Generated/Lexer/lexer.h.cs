// DOTNETJQ PORT MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream file: src/lexer.h
// Source of truth: src/lexer.l
// Upstream URL: https://github.com/jqlang/jq/blob/jq-1.8.2/src/lexer.h
// Source-of-truth URL: https://github.com/jqlang/jq/blob/jq-1.8.2/src/lexer.l
// Strategy: GENERATED
// Generation: deterministic managed declaration-template expansion
// Generator: tools/parser-gen/generate.py (template-expander-v1)
// Structural coverage: porting/PARSER_GRAMMAR_COVERAGE.json
// Template: tools/parser-gen/templates/lexer.h.cs.in
// Upstream grammar SHA-256: cfb3af17a786df30d7e30dae5861b84747d4904f8ce7ae9ab9b48bde342ee7f3
// Regenerate: python3 tools/parser-gen/generate.py --upstream upstream/jq
// AUTO-GENERATED OUTPUT: edit the template, never the generated file.
// Target file: src/DotNetJq/Generated/Lexer/lexer.h.cs
// Substitutions: managed token types replace Flex/Bison bridge declarations.
// Known differences: token kinds represent the managed scanner's combined token surface.

using System.Text;

namespace DotNetJq.Port;

internal enum TokenKind
{
    End,
    InvalidCharacter,
    Identifier,
    Binding,
    Number,
    StringStart,
    StringText,
    StringInterpolationStart,
    StringInterpolationEnd,
    StringEnd,
    Format,
    Location,
    Dollar,
    Dot,
    Recursive,
    LeftParenthesis,
    RightParenthesis,
    LeftBracket,
    RightBracket,
    LeftBrace,
    RightBrace,
    Colon,
    Semicolon,
    Comma,
    Pipe,
    Question,
    Plus,
    Minus,
    Multiply,
    Divide,
    Modulo,
    Assign,
    Equal,
    NotEqual,
    Less,
    LessEqual,
    Greater,
    GreaterEqual,
    DefinedOr,
    PipeAssign,
    PlusAssign,
    MinusAssign,
    MultiplyAssign,
    DivideAssign,
    ModuloAssign,
    DefinedOrAssign,
    DestructureAlternative,
}

internal readonly record struct Token(
    TokenKind Kind,
    string Text,
    int Offset,
    int SourceIndex,
    int Line,
    int Column)
{
    internal int ByteLength => Encoding.UTF8.GetByteCount(Text);
}
