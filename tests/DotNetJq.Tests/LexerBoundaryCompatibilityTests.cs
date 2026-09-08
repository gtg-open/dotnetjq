using DotNetJq.Port;
using DotNetJq.Tests.Harness;

namespace DotNetJq.Tests;

public sealed class LexerBoundaryCompatibilityTests
{
    private static readonly (string Name, char Value)[] NonJqWhitespace =
    [
        ("vertical tab", '\u000b'),
        ("form feed", '\u000c'),
        ("next line", '\u0085'),
        ("no-break space", '\u00a0'),
        ("ogham space mark", '\u1680'),
        ("em space", '\u2003'),
        ("line separator", '\u2028'),
        ("paragraph separator", '\u2029'),
        ("narrow no-break space", '\u202f'),
        ("ideographic space", '\u3000'),
    ];

    [Fact]
    public async Task OnlySpaceCrLfAndTabAreIgnoredAsWhitespace()
    {
        foreach (var (name, value) in NonJqWhitespace)
        {
            var source = "1" + value + "+" + value + "2";
            var lexer = new jq_lexer(source);
            Assert.Equal((TokenKind.Number, "1"), Read(lexer));
            _ = Assert.Throws<JqCompileException>(() => lexer.Next());
            _ = Assert.Throws<JqCompileException>(() => JqProgram.Compile(source));

            if (JqOracle.TryResolveExecutable(out _))
            {
                var oracle = await JqOracle.ExecuteAsync(
                    source,
                    "null",
                    cancellationToken: TestContext.Current.CancellationToken);
                Assert.False(oracle.Succeeded, name);
                Assert.Contains("INVALID_CHARACTER", oracle.StandardError, StringComparison.Ordinal);
            }
        }

        const string accepted = "1 \r\n\t+\t 2";
        var output = Assert.Single(JqProgram.Compile(accepted).Execute("null"));
        Assert.Equal(3, output.GetInt32());

        if (JqOracle.TryResolveExecutable(out _))
        {
            var oracle = await JqOracle.ExecuteAsync(
                accepted,
                "null",
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(oracle.Succeeded, oracle.StandardError);
            Assert.Equal(["3"], oracle.OutputLines);
        }
    }

    [Fact]
    public async Task IncompleteExponentsUseFlexLongestMatchBoundaries()
    {
        var cases = new (string Source, (TokenKind Kind, string Text)[] Tokens)[]
        {
            ("1e", [(TokenKind.Number, "1"), (TokenKind.Identifier, "e")]),
            ("1e+", [(TokenKind.Number, "1"), (TokenKind.Identifier, "e"), (TokenKind.Plus, "+")]),
            ("1.e", [(TokenKind.Number, "1."), (TokenKind.Identifier, "e")]),
            ("1.e+", [(TokenKind.Number, "1."), (TokenKind.Identifier, "e"), (TokenKind.Plus, "+")]),
            (".1e", [(TokenKind.Number, ".1"), (TokenKind.Identifier, "e")]),
            (".1e-", [(TokenKind.Number, ".1"), (TokenKind.Identifier, "e"), (TokenKind.Minus, "-")]),
        };

        foreach (var (source, expected) in cases)
        {
            Assert.Equal(expected.Append((TokenKind.End, string.Empty)), Lex(source));
            var managed = Assert.Throws<JqCompileException>(() => JqProgram.Compile(source));
            Assert.DoesNotContain("Invalid numeric literal", managed.Message, StringComparison.Ordinal);

            if (JqOracle.TryResolveExecutable(out _))
            {
                var oracle = await JqOracle.ExecuteAsync(
                    source,
                    "null",
                    cancellationToken: TestContext.Current.CancellationToken);
                Assert.False(oracle.Succeeded, source);
                Assert.Contains("unexpected IDENT", oracle.StandardError, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task IncompleteQualifiedNamesStopBeforeTheFirstUnmatchedColon()
    {
        var cases = new (string Source, (TokenKind Kind, string Text)[] Tokens)[]
        {
            ("name::", [(TokenKind.Identifier, "name"), (TokenKind.Colon, ":"), (TokenKind.Colon, ":")]),
            ("$name::", [(TokenKind.Binding, "$name"), (TokenKind.Colon, ":"), (TokenKind.Colon, ":")]),
            ("name:::tail", [(TokenKind.Identifier, "name"), (TokenKind.Colon, ":"), (TokenKind.Colon, ":"), (TokenKind.Colon, ":"), (TokenKind.Identifier, "tail")]),
            ("name::part::", [(TokenKind.Identifier, "name::part"), (TokenKind.Colon, ":"), (TokenKind.Colon, ":")]),
        };

        foreach (var (source, expected) in cases)
        {
            Assert.Equal(expected.Append((TokenKind.End, string.Empty)), Lex(source));
            var managed = Assert.Throws<JqCompileException>(() => JqProgram.Compile(source));
            Assert.DoesNotContain("Invalid module-qualified identifier", managed.Message, StringComparison.Ordinal);

            if (JqOracle.TryResolveExecutable(out _))
            {
                var oracle = await JqOracle.ExecuteAsync(
                    source,
                    "null",
                    cancellationToken: TestContext.Current.CancellationToken);
                Assert.False(oracle.Succeeded, source);
                Assert.Contains("unexpected ':'", oracle.StandardError, StringComparison.Ordinal);
            }
        }
    }

    [Theory]
    [InlineData(".name::part")]
    [InlineData(".xname::part")]
    [InlineData(".name::part?")]
    [InlineData(".name::part.foo")]
    [InlineData(".name::part | .")]
    [InlineData(".name::part::tail")]
    public async Task QualifiedTextAfterFieldIsRejectedAtTheUpstreamColonBoundary(string source)
    {
        var tokens = Lex(source);
        Assert.Equal(TokenKind.Dot, tokens[0].Kind);
        Assert.Equal(TokenKind.Identifier, tokens[1].Kind);
        Assert.DoesNotContain("::", tokens[1].Text, StringComparison.Ordinal);
        Assert.Equal((TokenKind.Colon, ":"), tokens[2]);
        Assert.Equal((TokenKind.Colon, ":"), tokens[3]);

        _ = Assert.Throws<JqCompileException>(() => JqProgram.Compile(source));

        if (!JqOracle.TryResolveExecutable(out _))
        {
            return;
        }

        var oracle = await JqOracle.ExecuteAsync(
            source,
            "null",
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(oracle.Succeeded, source);
        Assert.Contains("unexpected ':'", oracle.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void RawParserModeReturnsTokensForAdapterOwnedDiagnostics()
    {
        AssertRaw("@", TokenKind.InvalidCharacter, TokenKind.End);
        AssertRaw("\"unterminated", TokenKind.StringStart, TokenKind.StringText, TokenKind.End);
        AssertRaw(
            "\"\\(",
            TokenKind.StringStart,
            TokenKind.StringInterpolationStart,
            TokenKind.End);
    }

    private static void AssertRaw(string source, params TokenKind[] expected)
    {
        var lexer = new jq_lexer(source, rawParserMode: true);
        foreach (var kind in expected)
        {
            Assert.Equal(kind, lexer.Next().Kind);
        }
    }

    private static List<(TokenKind Kind, string Text)> Lex(string source)
    {
        var lexer = new jq_lexer(source);
        var result = new List<(TokenKind Kind, string Text)>();
        Token token;
        do
        {
            token = lexer.Next();
            result.Add((token.Kind, token.Text));
        }
        while (token.Kind != TokenKind.End);

        return result;
    }

    private static (TokenKind Kind, string Text) Read(jq_lexer lexer)
    {
        var token = lexer.Next();
        return (token.Kind, token.Text);
    }
}
