using System.Text;
using DotNetJq.Compatibility.FileSystem;
using DotNetJq.Port;
using DotNetJq.Port.GeneratedParser;
using DotNetJq.Tests.Harness;

namespace DotNetJq.Tests;

public sealed class ParserLibraryAuditClosureTests
{
    private static readonly string[] PrivateLoadPrograms =
    [
        "1 as $x | [$$$$x, $x]",
        "1 as $x | (($$$$x | empty), $x)",
        "1 as $x | def f: $x; [$$$$x, f, $x]",
        "1 as $x | [(2 as $x | [$$$$x, $x]), $x]",
        "range(2) as $x | [$$$$x, $x]",
    ];

    [Fact]
    public void PrivateLoadUsesThreeDollarTokensAndOneNormalizedBinding()
    {
        var tokens = Lex("$$$$value $ $ $ $qualified::name");

        Assert.Equal(
            [
                (TokenKind.Dollar, "$"),
                (TokenKind.Dollar, "$"),
                (TokenKind.Dollar, "$"),
                (TokenKind.Binding, "$value"),
                (TokenKind.Dollar, "$"),
                (TokenKind.Dollar, "$"),
                (TokenKind.Dollar, "$"),
                (TokenKind.Binding, "$qualified::name"),
                (TokenKind.End, string.Empty),
            ],
            tokens);

        AssertPrivateLoad("1 as $value | $$$$value");
        AssertPrivateLoad("1 as $value | $ $ $ $value");
    }

    [Fact]
    public async Task PrivateLoadMatchesLoadVnAcrossSiblingsBacktrackingClosuresAndShadowedBranches()
    {
        foreach (var source in PrivateLoadPrograms)
        {
            var managed = JqProgram.Compile(source).Execute("null")
                .Select(value => value.GetRawText())
                .ToArray();

            if (!JqOracle.TryResolveExecutable(out _))
            {
                continue;
            }

            var oracle = await JqOracle.ExecuteAsync(
                source,
                "null",
                arguments: ["--null-input"],
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(oracle.Succeeded, oracle.StandardError);
            Assert.Equal(oracle.OutputLines, managed);
        }
    }

    [Fact]
    public async Task UnboundPrivateLoadKeepsTheUpstreamNegativeArityDiagnostic()
    {
        const string source = "$$$$missing";
        var managed = Assert.Throws<JqCompileException>(() => JqProgram.Compile(source));
        Assert.Contains("missing/-1 is not defined", managed.Message, StringComparison.Ordinal);

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
        Assert.Contains("missing/-1 is not defined", oracle.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void LocationTokenCannotBeUsedAsThePrivateLoadBinding()
    {
        var exception = Assert.Throws<JqCompileException>(() =>
            JqGeneratedParser.ParseSource("$$$$__loc__"));

        Assert.Contains("expecting BINDING", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AllThreePrivateLoadsInTheEmbeddedBuiltinLibraryParseWithoutBindingTheLibrary()
    {
        using var stream = typeof(JqProgram).Assembly.GetManifestResourceStream(
            "DotNetJq.Resources.builtin.jq");
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var source = reader.ReadToEnd();

        Assert.Equal(3, CountOccurrences(source, "$$$$"));
        var parsed = JqGeneratedParser.ParseLibrarySource(source);
        libjq.block_free(parsed);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("empty")]
    [InlineData("def a: .; 0")]
    public void LibraryParserRejectsEverySyntacticMainExpression(string source)
    {
        var exception = Assert.Throws<JqCompileException>(() =>
            JqGeneratedParser.ParseLibrarySource(source));

        Assert.Equal(
            "jq: error: library should only have function definitions, not a main expression",
            exception.Message);
    }

    [Fact]
    public async Task GeneratedLibraryParserRetainsNativeSyntaxForParenthesizedDefinition()
    {
        const string source = "(def a: .;)";
        const string expected =
            "jq: error: syntax error, unexpected ')' at <top-level>, line 1, column 11:\n" +
            "    (def a: .;)\n" +
            "              ^";
        var exception = Assert.Throws<JqCompileException>(() =>
            DotNetJq.Port.GeneratedParser.JqGeneratedParser.ParseLibrarySource(source));
        Assert.Equal(expected, exception.Message);

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
    [InlineData("")]
    [InlineData("def a: .;")]
    [InlineData("def a: .; def b: a;")]
    [InlineData("module {name:\"definitions-only\"}; def a: .;")]
    public void LibraryParserAcceptsOnlyImportsMetadataAndDefinitions(string source)
    {
        var parsed = JqGeneratedParser.ParseLibrarySource(source);
        libjq.block_free(parsed);
    }

    [Fact]
    public async Task ModuleLoadingUsesTheLibraryOnlyParserLikeThePinnedOracle()
    {
        const string invalidLibrary = "def a: .;\n0";
        var resolver = new JqModuleResolver(
            new JqInMemoryFileSystem(new Dictionary<string, string>
            {
                ["/modules/invalid.jq"] = invalidLibrary,
            }),
            ["/modules"],
            "/program");

        var managed = Assert.Throws<JqCompileException>(
            () => JqProgram.Compile("include \"invalid\"; .", resolver));
        Assert.Equal(
            "jq: error: library should only have function definitions, not a main expression",
            managed.Message);

        if (!JqOracle.TryResolveExecutable(out _) ||
            !UpstreamTestFile.TryResolveUpstreamRoot(out var upstreamRoot))
        {
            return;
        }

        var testsDirectory = Path.Combine(upstreamRoot, "tests");
        var oracle = await JqOracle.ExecuteAsync(
            "include \"yes-main-program\"; .",
            "null",
            arguments: ["--null-input", "--library-path", testsDirectory],
            workingDirectory: testsDirectory,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(oracle.Succeeded);
        Assert.Contains(
            "library should only have function definitions, not a main expression",
            oracle.StandardError,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        "\"\\($missing)\"",
        "$missing is not defined",
        "line 1, column 4")]
    [InlineData(
        "\"\\(missing_fn)\"",
        "missing_fn/0 is not defined",
        "line 1, column 4")]
    [InlineData(
        "\"outer \\(\"inner \\($missing)\")\"",
        "$missing is not defined",
        "line 1, column 19")]
    [InlineData(
        "\"outer \\(\"inner \\(missing_fn)\")\"",
        "missing_fn/0 is not defined",
        "line 1, column 19")]
    public async Task NestedInterpolationReferencesFailCompileValidationAtTheOuterSourceLocation(
        string source,
        string diagnostic,
        string location)
    {
        var managed = Assert.Throws<JqCompileException>(() => JqProgram.Compile(source));
        Assert.Contains(diagnostic, managed.Message, StringComparison.Ordinal);
        Assert.Contains(location, managed.Message, StringComparison.Ordinal);
        Assert.Contains(source, managed.Message, StringComparison.Ordinal);

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
        Assert.Contains(diagnostic, oracle.StandardError, StringComparison.Ordinal);
        Assert.Contains(location, oracle.StandardError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(". as $visible | \"\\($visible)\"")]
    [InlineData("def visible: 1; \"\\(visible)\"")]
    [InlineData("def outer(g): \"\\(g)\"; outer(.)")]
    public void NestedInterpolationRetainsVisibleOuterVariableFilterAndFunctionScopes(string source)
    {
        _ = JqProgram.Compile(source);
    }

    private static List<(TokenKind Kind, string Text)> Lex(string source)
    {
        var lexer = new jq_lexer(source);
        var tokens = new List<(TokenKind Kind, string Text)>();
        Token token;
        do
        {
            token = lexer.Next();
            tokens.Add((token.Kind, token.Text));
        }
        while (token.Kind != TokenKind.End);

        return tokens;
    }

    private static void AssertPrivateLoad(string source)
    {
        var parsed = JqGeneratedParser.ParseSource(source);
        try
        {
            var take = Assert.Single(
                EnumerateInstructions(parsed),
                static instruction => instruction.op == opcode.LOADVN);
            Assert.Equal("value", take.symbol);
            var binder = Assert.IsType<inst>(take.bound_by);
            Assert.Equal((opcode.STOREV, "value"), (binder.op, binder.symbol));
        }
        finally
        {
            libjq.block_free(parsed);
        }
    }

    private static IEnumerable<inst> EnumerateInstructions(block value)
    {
        for (var instruction = value.first;
             instruction is not null;
             instruction = instruction.next)
        {
            yield return instruction;
            foreach (var argument in EnumerateInstructions(instruction.arglist))
            {
                yield return argument;
            }

            foreach (var nested in EnumerateInstructions(instruction.subfn))
            {
                yield return nested;
            }
        }
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }
}
