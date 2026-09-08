using DotNetJq.Tests.Harness;

namespace DotNetJq.Tests;

public sealed class ParserRecoveryCompatibilityTests
{
    [Theory]
    [MemberData(nameof(ExplicitErrorProductionCases))]
    public async Task EveryExplicitBisonErrorProductionHasAnExactObservableRecoveryCase(
        string production,
        string source,
        DiagnosticSpec[] diagnostics)
    {
        Assert.False(string.IsNullOrWhiteSpace(production));
        var expected = RenderExpected(source, diagnostics);
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

    public static TheoryData<string, string, DiagnosticSpec[]> ExplicitErrorProductionCases => new()
    {
        {
            "Term: BREAK error",
            "break $__loc__",
            [
                new("syntax error, unexpected $__loc__, expecting BINDING", 7, 8),
                new("break requires a label to break to", 1, 14),
            ]
        },
        {
            "Term: '.' error",
            ". 0",
            [
                new("syntax error, unexpected LITERAL", 3, 1),
                new("try .[\"field\"] instead of .field for unusually named fields", 1, 3),
            ]
        },
        {
            "Term: '.' IDENT error",
            ". foo",
            [
                new("syntax error, unexpected end of file", 3, 3),
                new("try .[\"field\"] instead of .field for unusually named fields", 1, 5),
            ]
        },
        {
            "Term: if Query then error",
            "if true then",
            [
                new("syntax error, unexpected end of file", 9, 4),
                new("Possibly unterminated 'if' statement", 1, 12),
            ]
        },
        {
            "Term: try Expr catch error",
            "try . catch",
            [
                new("syntax error, unexpected end of file", 7, 5),
                new("Possibly unterminated 'try' statement", 1, 11),
            ]
        },
        {
            "Term: '(' error ')'",
            "(;)",
            [new("syntax error, unexpected ';'", 2, 1)]
        },
        {
            "Term: '[' error ']'",
            "[;]",
            [new("syntax error, unexpected ';'", 2, 1)]
        },
        {
            "Term: Term '[' error ']'",
            ".[;]",
            [new("syntax error, unexpected ';'", 3, 1)]
        },
        {
            "Term: '{' error '}'",
            "{;}",
            [new("syntax error, unexpected ';'", 2, 1)]
        },
        {
            "ObjPat: error ':' Pattern",
            ". as {foo +: $x} | .",
            [
                new("syntax error, unexpected '+', expecting ':'", 11, 1),
                new("May need parentheses around object key expression", 7, 9),
            ]
        },
        {
            "DictPair: error ':' DictExpr",
            "{$__loc__:1}",
            [
                new("syntax error, unexpected ':', expecting '}'", 10, 1),
                new("May need parentheses around object key expression", 2, 9),
            ]
        },
        {
            "Term: '.' error / binding boundary",
            ".$x",
            [
                new("syntax error, unexpected BINDING", 2, 2),
                new("try .[\"field\"] instead of .field for unusually named fields", 1, 3),
            ]
        },
        {
            "Term: '.' IDENT error / recovery-lookahead boundary",
            ". foo | .",
            [
                new("syntax error, unexpected '|'", 7, 1),
                new("try .[\"field\"] instead of .field for unusually named fields", 1, 7),
            ]
        },
        {
            "Term: '(' error ')' / closing-delimiter boundary",
            "()",
            [new("syntax error, unexpected ')'", 2, 1)]
        },
        {
            "Term: '[' error ']' / comma boundary",
            "[,]",
            [new("syntax error, unexpected ','", 2, 1)]
        },
        {
            "Term: Term '[' error ']' / continuation boundary",
            "0[,] | 1",
            [new("syntax error, unexpected ','", 3, 1)]
        },
        {
            "Term: '{' error '}' / comma boundary",
            "{,}",
            [new("syntax error, unexpected ','", 2, 1)]
        },
        {
            "ObjPat: error ':' Pattern / invalid-first-token boundary",
            ". as {1:$x} | .",
            [
                new("syntax error, unexpected LITERAL", 7, 1),
                new("May need parentheses around object key expression", 7, 4),
            ]
        },
    };

    [Theory]
    [InlineData(".0")]
    [InlineData(".foo")]
    [InlineData("(.)")]
    [InlineData("[.]")]
    [InlineData(".[0]")]
    [InlineData("{}")]
    [InlineData(". as {foo:$x} | .")]
    public void RecoveryBoundariesDoNotCaptureTheClosestValidProduction(string source)
    {
        _ = JqProgram.Compile(source);
    }

    public sealed record DiagnosticSpec(string Message, int Column, int Length);

    private static string RenderExpected(string source, IEnumerable<DiagnosticSpec> diagnostics) =>
        string.Join(
            "\n",
            diagnostics.Select(diagnostic =>
                $"jq: error: {diagnostic.Message} at <top-level>, line 1, column {diagnostic.Column}:\n" +
                "    " + source + "\n" +
                "    " + new string(' ', diagnostic.Column - 1) +
                new string('^', diagnostic.Length)));
}
