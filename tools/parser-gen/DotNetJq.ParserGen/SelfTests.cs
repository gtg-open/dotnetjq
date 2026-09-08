namespace DotNetJq.ParserGen;

internal static class SelfTests
{
    private const string Header =
        """
        /*
         * jq-port-upstream-repository: https://github.com/jqlang/jq
         * jq-port-upstream-revision: 34f7186b86743a083a589741b6cea95293524108
         * jq-port-upstream-path: src/parser.y
         * jq-port: upstream-%expect 0
         */
        """;

    private const string LexerHeader =
        """
        /*
         * jq-port-upstream-repository: https://github.com/jqlang/jq
         * jq-port-upstream-revision: 34f7186b86743a083a589741b6cea95293524108
         * jq-port-upstream-path: src/lexer.l
         */
        """;

    public static void Run(string? gppgPath)
    {
        var tests = new (string Name, Action Body)[]
        {
            ("managed/upstream grammar shape accepts C# actions", GoodParserComparison),
            ("associativity drift is rejected", AssociativityDrift),
            ("missing explicit-empty mapping is rejected", MissingEmptyMarker),
            ("raw unsupported GPPG directives are rejected", RawUnsupportedDirective),
            ("provenance drift is rejected", ProvenanceDrift),
            ("GPPG provenance timestamps are normalized", ProvenanceCommentNormalization),
            ("checked-in structural expectations detect drift", StructuralExpectationDrift),
            ("lexer action language may change", GoodLexerComparison),
            ("lexer pattern drift is rejected", LexerPatternDrift),
        };

        var passed = 0;
        foreach (var test in tests)
        {
            test.Body();
            Console.WriteLine($"PASS {test.Name}");
            passed++;
        }

        if (gppgPath is not null)
        {
            GppgDualGeneration(gppgPath);
            Console.WriteLine("PASS GPPG dual-generation precedence/conflict checks");
            passed++;
            if (gppgPath == "dotnet-gppg")
            {
                ParserOutputGeneration(gppgPath);
                Console.WriteLine("PASS deterministic parser generate/reuse/check-generated path");
                passed++;
            }
        }

        Console.WriteLine($"{passed} self-tests passed");
    }

    private static void GoodParserComparison()
    {
        var upstream = ParserGrammar.Parse(UpstreamParser, managed: false);
        var managed = ParserGrammar.Parse(ManagedParser, managed: true);
        managed.ValidateManagedDialect();
        ParserGrammar.Compare(upstream, managed);
    }

    private static void AssociativityDrift() => ExpectFailure(() =>
    {
        var upstream = ParserGrammar.Parse(UpstreamParser, managed: false);
        var managed = ParserGrammar.Parse(ManagedParser.Replace("%left '+'", "%right '+'", StringComparison.Ordinal), managed: true);
        ParserGrammar.Compare(upstream, managed);
    }, "precedence declarations");

    private static void MissingEmptyMarker() => ExpectFailure(() =>
    {
        var upstream = ParserGrammar.Parse(UpstreamParser, managed: false);
        var managed = ParserGrammar.Parse(ManagedParser.Replace(
            "/* jq-port: upstream-%empty */",
            "/* empty */",
            StringComparison.Ordinal), managed: true);
        ParserGrammar.Compare(upstream, managed);
    }, "explicit %empty mappings");

    private static void RawUnsupportedDirective() => ExpectFailure(() =>
    {
        var managed = ParserGrammar.Parse(ManagedParser.Replace(
            "* jq-port: upstream-%expect 0",
            "* jq-port: upstream-%expect 0\n */\n%expect 0\n/*",
            StringComparison.Ordinal), managed: true);
        managed.ValidateManagedDialect();
    }, "unsupported");

    private static void ProvenanceDrift() => ExpectFailure(() =>
    {
        var provenance = Provenance.Parse(ManagedParser.Replace(
            "34f7186b86743a083a589741b6cea95293524108",
            "0000000000000000000000000000000000000000",
            StringComparison.Ordinal));
        if (provenance.Revision != "34f7186b86743a083a589741b6cea95293524108")
            throw new GuardrailException("invalid provenance: revision mismatch");
    }, "revision");

    private static void GoodLexerComparison()
    {
        var upstream = LexerGrammar.Parse(UpstreamLexer);
        var managed = LexerGrammar.Parse(ManagedLexer);
        LexerGrammar.Compare(upstream, managed);
    }

    private static void ProvenanceCommentNormalization()
    {
        const string utcGenerated =
            "// Input file </tmp/managed-parser.y>\n" +
            "  // Verbatim content from managed-parser.y\n" +
            "  // End verbatim content from managed-parser.y - 01/01/2000 00:00:00\n";
        const string madridGenerated =
            "// Input file </tmp/managed-parser.y>\n" +
            "  // Verbatim content from managed-parser.y\n" +
            "  // End verbatim content from managed-parser.y - 01/01/2000 01:00:00\n";
        const string expected =
            "// Input: committed managed parser grammar\n" +
            "  // Verbatim content from committed managed parser grammar\n" +
            "  // End verbatim content from committed managed parser grammar\n";

        var utcNormalized = ParserOutputPipeline.NormalizeProvenanceComments(utcGenerated);
        var madridNormalized = ParserOutputPipeline.NormalizeProvenanceComments(madridGenerated);
        if (utcNormalized != expected || madridNormalized != expected)
            throw new InvalidOperationException("GPPG provenance path/timestamp normalization drifted");
    }

    private static void StructuralExpectationDrift()
    {
        var parser = ParserGrammar.Parse(ManagedParser, managed: true);
        var lexer = LexerGrammar.Parse(ManagedLexer);
        var expectations = StructuralExpectations.Create(parser, lexer);
        expectations.Validate(parser, lexer);
        var drifted = ParserGrammar.Parse(
            ManagedParser.Replace("%left '+'", "%right '+'", StringComparison.Ordinal),
            managed: true);
        ExpectFailure(() => expectations.Validate(drifted, lexer), "normalized parser structure");
    }

    private static void LexerPatternDrift() => ExpectFailure(() =>
    {
        var upstream = LexerGrammar.Parse(UpstreamLexer);
        var managed = LexerGrammar.Parse(ManagedLexer.Replace("[0-9]+", "[0-8]+", StringComparison.Ordinal));
        LexerGrammar.Compare(upstream, managed);
    }, "lexer structure");

    private static void GppgDualGeneration(string gppgPath)
    {
        GppgVerifier.Verify(GppgSafeParser, 0, gppgPath);
        ExpectFailure(() => GppgVerifier.Verify(GppgUnsafeParser, 0, gppgPath), "cannot preserve Bison %precedence");
        ExpectFailure(() => GppgVerifier.Verify(GppgConflictParser, 0, gppgPath), "unresolved conflicts");
    }

    private static void ParserOutputGeneration(string gppgPath)
    {
        var directory = Directory.CreateTempSubdirectory("jq-parser-output-test-");
        try
        {
            var output = Path.Combine(directory.FullName, "Parser.g.cs");
            var generated = ParserOutputPipeline.Run(GppgSafeParser, output, gppgPath, 0, check: false);
            if (!generated.StartsWith("generated parser:", StringComparison.Ordinal))
                throw new InvalidOperationException("first parser-output pass did not generate");
            var reused = ParserOutputPipeline.Run(GppgSafeParser, output, gppgPath, 0, check: false);
            if (!reused.Contains("reused", StringComparison.Ordinal))
                throw new InvalidOperationException("second parser-output pass did not reuse");
            _ = ParserOutputPipeline.Run(GppgSafeParser, output, gppgPath, 0, check: true);
            File.AppendAllText(output, "// deliberate drift\n");
            ExpectFailure(
                () => ParserOutputPipeline.Run(GppgSafeParser, output, gppgPath, 0, check: true),
                "stale");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static void ExpectFailure(Action body, string expectedMessage)
    {
        try
        {
            body();
        }
        catch (GuardrailException exception) when (exception.Message.Contains(expectedMessage, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        throw new InvalidOperationException($"expected GuardrailException containing '{expectedMessage}'");
    }

    private const string UpstreamParser =
        """
        %expect 0
        %token NUMBER
        %token NAME "name"
        %precedence PREFIX
        %left '+'
        %type <node> Input Expr
        %%
        Input:
          %empty { c_noop(); }
        | Expr { c_answer($1); }
        ;
        Expr:
          NUMBER { $$ = c_number($1); }
        | '-' Expr %prec PREFIX { $$ = c_negate($2); }
        | Expr '+' Expr { $$ = c_add($1, $3); }
        ;
        %%
        """;

    private const string ManagedParser = Header + "\n" +
        """
        %token NUMBER
        %token NAME "name"
        %left PREFIX /* jq-port: upstream-%precedence */
        %left '+'
        %type <node> Input Expr
        %%
        Input:
          /* jq-port: upstream-%empty */ { $$ = Nodes.NoOp(); }
        | Expr { $$ = Nodes.Answer($1); }
        ;
        Expr:
          NUMBER { $$ = Nodes.Number($1); }
        | '-' Expr %prec PREFIX { $$ = Nodes.Negate($2); }
        | Expr '+' Expr { $$ = Nodes.Add($1, $3); }
        ;
        %%
        """;

    private const string UpstreamLexer =
        """
        %option reentrant
        %x STRING
        %%
        "name" { return NAME; }
        <STRING>{
          [0-9]+ { c_number(yytext); return NUMBER; }
          <<EOF>> { return 0; }
        }
        [ \t\n]+ { }
        %%
        """;

    private const string ManagedLexer = LexerHeader + "\n" +
        """
        %option unicode
        %x STRING
        %%
        "name" { return Tokens.NAME; }
        <STRING>{
          [0-9]+ { yylval = Nodes.Number(yytext); return Tokens.NUMBER; }
          <<EOF>> { return Tokens.EOF; }
        }
        [ \t\n]+ { /* skip */ }
        %%
        """;

    private const string GppgSafeParser = Header + "\n" +
        """
        %start Input
        %visibility internal
        %token NUMBER
        %left PREFIX /* jq-port: upstream-%precedence */
        %left '+'
        %%
        Input : /* jq-port: upstream-%empty */ | Expr ;
        Expr : NUMBER | '-' Expr %prec PREFIX | Expr '+' Expr ;
        %%
        """;

    private const string GppgUnsafeParser = Header + "\n" +
        """
        %start Input
        %visibility internal
        %token NUMBER OP
        %left OP /* jq-port: upstream-%precedence */
        %%
        Input : Expr ;
        Expr : NUMBER | Expr OP Expr ;
        %%
        """;

    private const string GppgConflictParser = Header + "\n" +
        """
        %start Input
        %visibility internal
        %token NUMBER
        %left PREFIX /* jq-port: upstream-%precedence */
        %%
        Input : Expr ;
        Expr : NUMBER | Expr Expr ;
        %%
        """;
}
