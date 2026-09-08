using System.Text;
using DotNetJq.Port;
using DotNetJq.Tests.Harness;

namespace DotNetJq.Tests;

public sealed class ParserTokenLocationCompatibilityTests
{
    [Fact]
    public void RawLexerRetainsTheLastMatchForGeneratedParserEofLocations()
    {
        var lexer = new jq_lexer("{", rawParserMode: true);
        Assert.Equal(TokenKind.LeftBrace, lexer.Next().Kind);
        Assert.Equal(TokenKind.End, lexer.Next().Kind);

        var match = Assert.IsType<jq_lexer.LexerMatchSpan>(lexer.LastMatchSpan);
        Assert.Equal(
            (StartByte: 0, EndByte: 1, StartIndex: 0, EndIndex: 1, StartColumn: 1, EndColumn: 2),
            (match.StartByte, match.EndByte, match.StartIndex, match.EndIndex, match.StartColumn, match.EndColumn));
    }

    public static TheoryData<string, int, int, int> EndOfFileLocationCases => new()
    {
        { "if", 1, 1, 2 },
        { "if ", 1, 3, 1 },
        { "if  ", 1, 3, 2 },
        { "if\t\t", 1, 3, 2 },
        { "if\r\n", 1, 3, 1 },
        { "if #", 1, 4, 1 },
        { "if #x", 1, 5, 1 },
        { "if #x\n", 1, 6, 1 },
        { "if #x\n  ", 2, 1, 2 },
        { "if #é", 1, 6, 1 },
        { "if #😀", 1, 8, 1 },
        { "if true", 1, 4, 4 },
        { "if true\n", 1, 8, 1 },
        { "if true then", 1, 9, 4 },
        { "if true then\n", 1, 13, 1 },
        { ". foo", 1, 3, 3 },
        { "try", 1, 1, 3 },
        { "try\n", 1, 4, 1 },
        { "def", 1, 1, 3 },
        { "def\n", 1, 4, 1 },
    };

    [Theory]
    [MemberData(nameof(EndOfFileLocationCases))]
    public async Task EndOfFileDiagnosticsUseTheLastLexerMatch(
        string source,
        int expectedLine,
        int expectedColumn,
        int expectedCaretLength)
    {
        var managed = Assert.Throws<JqCompileException>(
            () => DotNetJq.Port.GeneratedParser.JqGeneratedParser.ParseSource(source));
        var diagnosticLines = managed.Message.Split('\n');
        Assert.Contains(
            $"unexpected end of file",
            diagnosticLines[0],
            StringComparison.Ordinal);
        Assert.Contains(
            $"line {expectedLine}, column {expectedColumn}:",
            diagnosticLines[0],
            StringComparison.Ordinal);
        Assert.Equal(
            new string(' ', expectedColumn + 3) + new string('^', expectedCaretLength),
            diagnosticLines[2]);

        if (!JqOracle.TryResolveExecutable(out _))
        {
            return;
        }

        var oracle = await JqOracle.ExecuteAsync(
            source,
            "null",
            arguments: ["--null-input"],
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(oracle.Succeeded);
        Assert.Contains(
            managed.Message,
            oracle.StandardError.Replace("\r\n", "\n", StringComparison.Ordinal),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        "catch",
        "jq: error: syntax error, unexpected catch, expecting end of file at <top-level>, line 1, column 1:",
        1)]
    [InlineData(
        "1 catch",
        "jq: error: syntax error, unexpected catch, expecting end of file at <top-level>, line 1, column 3:",
        1)]
    [InlineData(
        "(. catch)",
        "jq: error: syntax error, unexpected catch, expecting '|' or ',' or ')' at <top-level>, line 1, column 4:",
        1)]
    [InlineData(
        "if . then . catch",
        "jq: error: syntax error, unexpected catch at <top-level>, line 1, column 13:",
        2)]
    [InlineData(
        "if . then . else . catch",
        "jq: error: syntax error, unexpected catch, expecting end or '|' or ',' at <top-level>, line 1, column 20:",
        2)]
    [InlineData(
        "try . catch catch",
        "jq: error: syntax error, unexpected catch at <top-level>, line 1, column 13:",
        2)]
    public async Task ProjectedExpectedTokensMatchBisonWithoutChangingAcceptance(
        string source,
        string expectedFirstLine,
        int expectedDiagnosticCount)
    {
        var managed = Assert.Throws<JqCompileException>(
            () => DotNetJq.Port.GeneratedParser.JqGeneratedParser.ParseSource(source));
        Assert.Equal(expectedFirstLine, managed.Message.Split('\n')[0]);
        Assert.Equal(
            expectedDiagnosticCount,
            managed.Message.Split("jq: error:", StringSplitOptions.None).Length - 1);

        if (!JqOracle.TryResolveExecutable(out _))
        {
            return;
        }

        var oracle = await JqOracle.ExecuteAsync(
            source,
            "null",
            arguments: ["--null-input"],
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(oracle.Succeeded);
        Assert.Contains(managed.Message, oracle.StandardError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData('\u0001')]
    [InlineData('\b')]
    [InlineData('\t')]
    [InlineData('\n')]
    [InlineData('\v')]
    [InlineData('\f')]
    [InlineData('\r')]
    [InlineData('\u001f')]
    public async Task RawControlCharactersInFilterStringsArePreserved(char control)
    {
        var source = "\"a" + control + "b\"";
        var managed = Assert.Single(JqProgram.Compile(source).Execute("null"));
        Assert.Equal("a" + control + "b", managed.GetString());

        if (!JqOracle.TryResolveExecutable(out _))
        {
            return;
        }

        var oracle = await JqOracle.ExecuteAsync(
            source,
            "null",
            arguments: ["--null-input", "--compact-output"],
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(oracle.Succeeded, oracle.StandardError);
        Assert.Equal([managed.GetRawText()], oracle.OutputLines);
    }

    [Fact]
    public void RawNulIsPreservedByTheLengthBasedManagedParser()
    {
        const string source = "\"a\0b\"";
        var managed = Assert.Single(JqProgram.Compile(source).Execute("null"));
        Assert.Equal("a\0b", managed.GetString());
    }

    [Fact]
    public void LocationHasItsOwnTokenAndFlexLongestMatchBoundaries()
    {
        Assert.Equal(
            [(TokenKind.Location, "$__loc__")],
            LexWithoutEnd("$__loc__"));
        Assert.Equal(
            [(TokenKind.Location, "$__loc__"), (TokenKind.Question, "?")],
            LexWithoutEnd("$__loc__?"));
        Assert.Equal(
            [(TokenKind.Location, "$__loc__"), (TokenKind.Colon, ":"), (TokenKind.Colon, ":")],
            LexWithoutEnd("$__loc__::"));
        Assert.Equal(
            [(TokenKind.Binding, "$__loc__x")],
            LexWithoutEnd("$__loc__x"));
        Assert.Equal(
            [(TokenKind.Binding, "$__loc__::x")],
            LexWithoutEnd("$__loc__::x"));
        Assert.Equal(
            [(TokenKind.Binding, "$foo::__loc__")],
            LexWithoutEnd("$foo::__loc__"));
        Assert.Equal(
            [(TokenKind.Dollar, "$"), (TokenKind.Location, "$__loc__")],
            LexWithoutEnd("$$__loc__"));
        Assert.Equal(
            [
                (TokenKind.Dollar, "$"),
                (TokenKind.Dollar, "$"),
                (TokenKind.Dollar, "$"),
                (TokenKind.Binding, "$__loc__::x"),
            ],
            LexWithoutEnd("$$$$__loc__::x"));
    }

    [Fact]
    public async Task LocationIsAcceptedOnlyAsATermOrObjectShorthand()
    {
        const string term = "$__loc__";
        const string shorthand = "{$__loc__}";
        Assert.Equal(
            "{\"file\":\"<top-level>\",\"line\":1}",
            Assert.Single(JqProgram.Compile(term).Execute("null")).GetRawText());
        Assert.Equal(
            "{\"__loc__\":{\"file\":\"<top-level>\",\"line\":1}}",
            Assert.Single(JqProgram.Compile(shorthand).Execute("null")).GetRawText());

        if (!JqOracle.TryResolveExecutable(out _))
        {
            return;
        }

        foreach (var source in new[] { term, shorthand })
        {
            var oracle = await JqOracle.ExecuteAsync(
                source,
                "null",
                arguments: ["--null-input", "--compact-output"],
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(oracle.Succeeded, oracle.StandardError);
            Assert.Equal(
                [Assert.Single(JqProgram.Compile(source).Execute("null")).GetRawText()],
                oracle.OutputLines);
        }
    }

    [Theory]
    [InlineData("def f($__loc__): .; f(1)")]
    [InlineData("def $__loc__: 1; .")]
    [InlineData("import \"a\" as $__loc__; .")]
    [InlineData(". as $__loc__ | .")]
    [InlineData(". as [$__loc__] | .")]
    [InlineData(". as {x: $__loc__} | .")]
    [InlineData("reduce . as $__loc__ (null; .)")]
    [InlineData("foreach . as $__loc__ (null; .; .)")]
    [InlineData("label $__loc__ | .")]
    [InlineData("break $__loc__")]
    [InlineData("$$$$__loc__")]
    [InlineData("{$__loc__: 1}")]
    public async Task ExactLocationTokenCannotBeReusedAsABindingOrNamedKey(string source)
    {
        _ = Assert.Throws<JqCompileException>(() => JqProgram.Compile(source));

        if (!JqOracle.TryResolveExecutable(out _))
        {
            return;
        }

        var oracle = await JqOracle.ExecuteAsync(
            source,
            "null",
            arguments: ["--null-input"],
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(oracle.Succeeded);
        Assert.Contains("$__loc__", oracle.StandardError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        ")",
        "jq: error: syntax error, unexpected INVALID_CHARACTER, expecting end of file " +
        "at <top-level>, line 1, column 1:\n    )\n    ^")]
    [InlineData(
        "(]",
        "jq: error: syntax error, unexpected INVALID_CHARACTER at <top-level>, " +
        "line 1, column 2:\n    (]\n     ^")]
    [InlineData(
        "[)",
        "jq: error: syntax error, unexpected INVALID_CHARACTER at <top-level>, " +
        "line 1, column 2:\n    [)\n     ^")]
    [InlineData(
        "{]",
        "jq: error: syntax error, unexpected INVALID_CHARACTER at <top-level>, " +
        "line 1, column 2:\n    {]\n     ^")]
    [InlineData(
        "([)]",
        "jq: error: syntax error, unexpected INVALID_CHARACTER at <top-level>, " +
        "line 1, column 3:\n    ([)]\n      ^")]
    [InlineData(
        "\"\\(] )\"",
        "jq: error: syntax error, unexpected INVALID_CHARACTER at <top-level>, " +
        "line 1, column 4:\n    \"\\(] )\"\n       ^")]
    [InlineData(
        "\"\\([)] )\"",
        "jq: error: syntax error, unexpected INVALID_CHARACTER at <top-level>, " +
        "line 1, column 5:\n    \"\\([)] )\"\n        ^")]
    public async Task DelimiterStateMismatchesBecomeInvalidCharacterAtTheExactByte(
        string source,
        string expected)
    {
        var managed = Assert.Throws<JqCompileException>(() => JqProgram.Compile(source));
        Assert.Equal(expected, managed.Message);

        if (!JqOracle.TryResolveExecutable(out _))
        {
            return;
        }

        var oracle = await JqOracle.ExecuteAsync(
            source,
            "null",
            arguments: ["--null-input"],
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(oracle.Succeeded);
        Assert.Contains(expected, oracle.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void LexerTracksDelimiterStateWithoutTreatingQuotedClosersAsSyntax()
    {
        var mismatch = new jq_lexer("([)]");
        Assert.Equal(TokenKind.LeftParenthesis, mismatch.Next().Kind);
        Assert.Equal(TokenKind.LeftBracket, mismatch.Next().Kind);
        Assert.Equal(TokenKind.InvalidCharacter, mismatch.Next().Kind);

        var quoted = Assert.Single(JqProgram.Compile("\"text ] } )\"").Execute("null"));
        Assert.Equal("text ] } )", quoted.GetString());
    }

    [Theory]
    [InlineData(
        "def f: {(1+2):0};",
        "Cannot use number (3) as object key")]
    [InlineData(
        "def f: (];",
        "syntax error, unexpected INVALID_CHARACTER")]
    [InlineData(
        "def f: \"é\\q\";",
        "Invalid escape at line 1, column 4")]
    public async Task ImportedParserDiagnosticsRetainTheResolvedModuleFilename(
        string moduleSource,
        string expectedCore)
    {
        using var fixture = new TemporaryDirectory();
        var modulePath = Path.Combine(fixture.Path, "bad.jq");
        File.WriteAllText(modulePath, moduleSource, new UTF8Encoding(false));

        var managed = Assert.Throws<JqCompileException>(() =>
            DotNetJq.Port.GeneratedParser.JqGeneratedParser.ParseLibrarySource(
                moduleSource,
                modulePath));
        Assert.Contains(expectedCore, managed.Message, StringComparison.Ordinal);
        Assert.Contains(" at " + modulePath + ", line 1, column ", managed.Message, StringComparison.Ordinal);

        if (!JqOracle.TryResolveExecutable(out _))
        {
            return;
        }

        var oracle = await JqOracle.ExecuteAsync(
            "import \"bad\" as bad; bad::f",
            "null",
            arguments: ["--null-input", "--library-path", fixture.Path],
            workingDirectory: fixture.Path,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(oracle.Succeeded);
        Assert.Contains(managed.Message, oracle.StandardError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, "\n", "[1,3]")]
    [InlineData(2, "\n", "[1,2,3]")]
    [InlineData(3, "\n", "[1,3]")]
    [InlineData(1, "\r\n", "[1,3]")]
    [InlineData(2, "\r\n", "[1,2,3]")]
    [InlineData(3, "\r\n", "[1,3]")]
    public async Task CommentContinuationUsesTrailingBackslashParity(
        int backslashCount,
        string lineEnding,
        string expected)
    {
        var source =
            "[" + lineEnding +
            "1," + lineEnding +
            "# comment " + new string('\\', backslashCount) + lineEnding +
            "2," + lineEnding +
            "3" + lineEnding +
            "]";
        var managed = Assert.Single(JqProgram.Compile(source).Execute("null"));
        Assert.Equal(expected, managed.GetRawText());

        if (!JqOracle.TryResolveExecutable(out _))
        {
            return;
        }

        var oracle = await JqOracle.ExecuteAsync(
            source,
            "null",
            arguments: ["--null-input", "--compact-output"],
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(oracle.Succeeded, oracle.StandardError);
        Assert.Equal([expected], oracle.OutputLines);
    }

    [Fact]
    public async Task MultilineTryIfRecoveryReportsTheThreeShtestDiagnosticsInOrder()
    {
        const string source = """
            [
              try if .
                     then 1
                     else 2
              catch ]
            """;
        const string expected =
            "jq: error: syntax error, unexpected catch, expecting end or '|' or ',' " +
            "at <top-level>, line 5, column 3:\n" +
            "      catch ]\n" +
            "      ^^^^^\n" +
            "jq: error: Possibly unterminated 'if' statement at <top-level>, line 2, column 7:\n" +
            "      try if .\n" +
            "          ^^^^\n" +
            "jq: error: Possibly unterminated 'try' statement at <top-level>, line 2, column 3:\n" +
            "      try if .\n" +
            "      ^^^^^^^^";

        var managed = Assert.Throws<JqCompileException>(() => JqProgram.Compile(source));
        Assert.Equal(expected, managed.Message);

        if (!JqOracle.TryResolveExecutable(out _))
        {
            return;
        }

        var oracle = await JqOracle.ExecuteAsync(
            source,
            "null",
            arguments: ["--null-input"],
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(oracle.Succeeded);
        Assert.Contains(expected, oracle.StandardError, StringComparison.Ordinal);
        Assert.Contains("jq: 3 compile errors", oracle.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IfAtEndOfFileUsesTheLastTokenByteLocation()
    {
        const string source = "if\n";
        const string expected =
            "jq: error: syntax error, unexpected end of file at <top-level>, line 1, column 3:\n" +
            "    if\n" +
            "      ^";

        var managed = Assert.Throws<JqCompileException>(() => JqProgram.Compile(source));
        Assert.Equal(expected, managed.Message);

        if (!JqOracle.TryResolveExecutable(out _))
        {
            return;
        }

        var oracle = await JqOracle.ExecuteAsync(
            source,
            "null",
            arguments: ["--null-input"],
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(oracle.Succeeded);
        Assert.Contains(expected, oracle.StandardError, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Utf8DiagnosticCases))]
    public async Task DiagnosticsUseUtf8ByteColumnsAndSpans(string source, string expected)
    {
        var managed = Assert.Throws<JqCompileException>(() => JqProgram.Compile(source));
        Assert.Equal(expected, managed.Message);

        if (!JqOracle.TryResolveExecutable(out _))
        {
            return;
        }

        var oracle = await JqOracle.ExecuteAsync(
            source,
            "null",
            arguments: ["--null-input"],
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(oracle.Succeeded);
        Assert.Contains(expected, oracle.StandardError, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(InvalidEscapeCases))]
    public async Task InvalidEscapeDiagnosticsUseTheMaximalEscapeRunAndExactLocations(
        string source,
        string expected)
    {
        var managed = Assert.Throws<JqCompileException>(() => JqProgram.Compile(source));
        Assert.Equal(expected, managed.Message);

        if (!JqOracle.TryResolveExecutable(out _))
        {
            return;
        }

        var oracle = await JqOracle.ExecuteAsync(
            source,
            "null",
            arguments: ["--null-input"],
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(oracle.Succeeded);
        Assert.Contains(expected, oracle.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeparateInvalidEscapeRunsRetainBisonDiagnosticMultiplicity()
    {
        const string source = "\"\\qX\\z\"";
        const string expected =
            "jq: error: Invalid escape at line 1, column 4 (while parsing '\"\\q\"') " +
            "at <top-level>, line 1, column 2:\n" +
            "    \"\\qX\\z\"\n" +
            "     ^^\n" +
            "jq: error: Invalid escape at line 1, column 4 (while parsing '\"\\z\"') " +
            "at <top-level>, line 1, column 5:\n" +
            "    \"\\qX\\z\"\n" +
            "        ^^";

        var managed = Assert.Throws<JqCompileException>(() => JqProgram.Compile(source));
        Assert.Equal(expected, managed.Message);

        if (!JqOracle.TryResolveExecutable(out _))
        {
            return;
        }

        var oracle = await JqOracle.ExecuteAsync(
            source,
            "null",
            arguments: ["--null-input"],
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(oracle.Succeeded);
        Assert.Contains(expected, oracle.StandardError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        "break $__loc__",
        "jq: error: syntax error, unexpected $__loc__, expecting BINDING at <top-level>, line 1, column 7:\n" +
        "    break $__loc__\n" +
        "          ^^^^^^^^\n" +
        "jq: error: break requires a label to break to at <top-level>, line 1, column 1:\n" +
        "    break $__loc__\n" +
        "    ^^^^^^^^^^^^^^")]
    [InlineData(
        "{$__loc__:1}",
        "jq: error: syntax error, unexpected ':', expecting '}' at <top-level>, line 1, column 10:\n" +
        "    {$__loc__:1}\n" +
        "             ^\n" +
        "jq: error: May need parentheses around object key expression at <top-level>, line 1, column 2:\n" +
        "    {$__loc__:1}\n" +
        "     ^^^^^^^^^")]
    public async Task RecoveredLocationProductionsRetainBisonDiagnosticMultiplicity(
        string source,
        string expected)
    {
        var managed = Assert.Throws<JqCompileException>(() => JqProgram.Compile(source));
        Assert.Equal(expected, managed.Message);

        if (!JqOracle.TryResolveExecutable(out _))
        {
            return;
        }

        var oracle = await JqOracle.ExecuteAsync(
            source,
            "null",
            arguments: ["--null-input"],
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(oracle.Succeeded);
        Assert.Contains(expected, oracle.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void LexerExposesUtf8ByteOffsetsAlongsideUtf16SourceIndices()
    {
        const string source = "\"😀é\" | $value";
        var lexer = new jq_lexer(source);
        var start = lexer.Next();
        var text = lexer.Next();
        var end = lexer.Next();
        var pipe = lexer.Next();
        var binding = lexer.Next();

        Assert.Equal((0, 0, 1, 1), (start.Offset, start.SourceIndex, start.Column, start.ByteLength));
        Assert.Equal((1, 1, 2, 6), (text.Offset, text.SourceIndex, text.Column, text.ByteLength));
        Assert.Equal((7, 4, 8, 1), (end.Offset, end.SourceIndex, end.Column, end.ByteLength));
        Assert.Equal((9, 6, 10), (pipe.Offset, pipe.SourceIndex, pipe.Column));
        Assert.Equal((11, 8, 12), (binding.Offset, binding.SourceIndex, binding.Column));
        Assert.Equal(Encoding.UTF8.GetByteCount(source), lexer.Next().Offset);
    }

    [Fact]
    public void LexerPreservesTheFlexQuotedStringSegmentationBoundary()
    {
        Assert.Equal(
            [
                (TokenKind.StringStart, "\""),
                (TokenKind.StringText, "a"),
                (TokenKind.StringText, "\\n\\q"),
                (TokenKind.StringInterpolationStart, "\\("),
                (TokenKind.Number, "1"),
                (TokenKind.Plus, "+"),
                (TokenKind.StringStart, "\""),
                (TokenKind.StringText, "x"),
                (TokenKind.StringEnd, "\""),
                (TokenKind.StringInterpolationEnd, ")"),
                (TokenKind.StringText, "b"),
                (TokenKind.StringEnd, "\""),
            ],
            LexWithoutEnd("\"a\\n\\q\\(1+\"x\")b\""));
    }

    public static TheoryData<string, string> Utf8DiagnosticCases => new()
    {
        {
            "\"é\" | $missing",
            "jq: error: $missing is not defined at <top-level>, line 1, column 8:\n" +
            "    \"é\" | $missing\n" +
            "           ^^^^^^^^"
        },
        {
            "\"😀\" | $missing",
            "jq: error: $missing is not defined at <top-level>, line 1, column 10:\n" +
            "    \"😀\" | $missing\n" +
            "             ^^^^^^^^"
        },
        {
            "\"é\" | missing",
            "jq: error: missing/0 is not defined at <top-level>, line 1, column 8:\n" +
            "    \"é\" | missing\n" +
            "           ^^^^^^^"
        },
        {
            "\"é\" | {(1+2):0}",
            "jq: error: Cannot use number (3) as object key at <top-level>, line 1, column 10:\n" +
            "    \"é\" | {(1+2):0}\n" +
            "             ^^^"
        },
        {
            "\"é\nβ\" | $missing",
            "jq: error: $missing is not defined at <top-level>, line 2, column 7:\n" +
            "    β\" | $missing\n" +
            "          ^^^^^^^^"
        },
        {
            "\"é\\( $missing )\"",
            "jq: error: $missing is not defined at <top-level>, line 1, column 7:\n" +
            "    \"é\\( $missing )\"\n" +
            "          ^^^^^^^^"
        },
    };

    public static TheoryData<string, string> InvalidEscapeCases => new()
    {
        {
            "\"\\q\"",
            "jq: error: Invalid escape at line 1, column 4 (while parsing '\"\\q\"') " +
            "at <top-level>, line 1, column 2:\n" +
            "    \"\\q\"\n" +
            "     ^^"
        },
        {
            "\"abc\\q\"",
            "jq: error: Invalid escape at line 1, column 4 (while parsing '\"\\q\"') " +
            "at <top-level>, line 1, column 5:\n" +
            "    \"abc\\q\"\n" +
            "        ^^"
        },
        {
            "\"abc\\n\\q\"",
            "jq: error: Invalid escape at line 1, column 6 (while parsing '\"\\n\\q\"') " +
            "at <top-level>, line 1, column 5:\n" +
            "    \"abc\\n\\q\"\n" +
            "        ^^^^"
        },
        {
            "\"é\\q\"",
            "jq: error: Invalid escape at line 1, column 4 (while parsing '\"\\q\"') " +
            "at <top-level>, line 1, column 4:\n" +
            "    \"é\\q\"\n" +
            "       ^^"
        },
        {
            "\"😀\\q\"",
            "jq: error: Invalid escape at line 1, column 4 (while parsing '\"\\q\"') " +
            "at <top-level>, line 1, column 6:\n" +
            "    \"😀\\q\"\n" +
            "         ^^"
        },
        {
            "\"first\nsecond\\u12\"",
            "jq: error: Invalid \\uXXXX escape at line 1, column 6 " +
            "(while parsing '\"\\u12\"') at <top-level>, line 2, column 7:\n" +
            "    second\\u12\"\n" +
            "          ^^^^"
        },
        {
            "\"\\u123Z\"",
            "jq: error: Invalid characters in \\uXXXX escape at line 1, column 8 " +
            "(while parsing '\"\\u123Z\"') at <top-level>, line 1, column 2:\n" +
            "    \"\\u123Z\"\n" +
            "     ^^^^^^"
        },
        {
            "\"\\uD800\"",
            "jq: error: Invalid \\uXXXX\\uXXXX surrogate pair escape at line 1, column 8 " +
            "(while parsing '\"\\uD800\"') at <top-level>, line 1, column 2:\n" +
            "    \"\\uD800\"\n" +
            "     ^^^^^^"
        },
        {
            "\"abc\\\ndef\"",
            "jq: error: Invalid escape at line 2, column 1 (while parsing '\"\\\n\"') " +
            "at <top-level>, line 1, column 5:\n" +
            "    \"abc\\\n" +
            "        ^"
        },
    };

    private static List<(TokenKind Kind, string Text)> LexWithoutEnd(string source)
    {
        var lexer = new jq_lexer(source);
        var result = new List<(TokenKind Kind, string Text)>();
        while (true)
        {
            var token = lexer.Next();
            if (token.Kind == TokenKind.End)
            {
                return result;
            }

            result.Add((token.Kind, token.Text));
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "dotnetjq-parser-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }
}
