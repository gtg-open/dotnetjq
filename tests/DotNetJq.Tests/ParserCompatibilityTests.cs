using System.Text.Json;
using DotNetJq.Port;
using DotNetJq.Port.GeneratedParser;
using DotNetJq.Tests.Harness;

namespace DotNetJq.Tests;

public sealed class ParserCompatibilityTests
{
    [Theory]
    [InlineData("1 < 2 < 3")]
    [InlineData("1 > 2 > 3")]
    [InlineData("1 <= 2 <= 3")]
    [InlineData("1 >= 2 >= 3")]
    [InlineData("1 == 2 == 3")]
    [InlineData("1 != 2 != 3")]
    [InlineData("1 = 2 = 3")]
    [InlineData("1 |= 2 |= 3")]
    [InlineData("1 += 2 += 3")]
    [InlineData("1 -= 2 -= 3")]
    [InlineData("1 *= 2 *= 3")]
    [InlineData("1 /= 2 /= 3")]
    [InlineData("1 %= 2 %= 3")]
    [InlineData("1 //= 2 //= 3")]
    public async Task NonAssociativeOperatorsRejectChainedForms(string source)
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
    }

    private static readonly int[] ExpectedPipeValues = [11, 12];
    private static readonly int[] ExpectedForeachValues = [1, 3, 6];

    [Fact]
    public void LexerRecognizesUpstreamNumberFormatBindingAndAlternationTokens()
    {
        var tokens = Lex(".5 1. @1 $module::value ?//");

        Assert.Collection(
            tokens,
            token => Assert.Equal((TokenKind.Number, ".5"), (token.Kind, token.Text)),
            token => Assert.Equal((TokenKind.Number, "1."), (token.Kind, token.Text)),
            token => Assert.Equal((TokenKind.Format, "@1"), (token.Kind, token.Text)),
            token => Assert.Equal(
                (TokenKind.Binding, "$module::value"),
                (token.Kind, token.Text)),
            token => Assert.Equal(
                (TokenKind.DestructureAlternative, "?//"),
                (token.Kind, token.Text)),
            token => Assert.Equal(TokenKind.End, token.Kind));
    }

    [Fact]
    public void BackslashNewlineContinuesAnUpstreamStyleComment()
    {
        var tokens = Lex("# the next line is still a comment\\\n1\n2");

        Assert.Equal(TokenKind.Number, tokens[0].Kind);
        Assert.Equal("2", tokens[0].Text);
        Assert.Equal(TokenKind.End, tokens[1].Kind);
    }

    [Fact]
    public void LexerRejectsNonAsciiIdentifiersLikeUpstream()
    {
        Assert.Throws<JqCompileException>(() => Lex("café"));
    }

    [Theory]
    [InlineData("1 = 2 = 3")]
    [InlineData("1 < 2 < 3")]
    [InlineData("call()")]
    [InlineData("def call(): 1; call")]
    [InlineData("def if: 1; if")]
    [InlineData("then")]
    [InlineData("\"value\"[:]")]
    [InlineData(".field::qualified")]
    [InlineData("{value: 1 as $x | $x}")]
    [InlineData("{value: def f: 1; f}")]
    [InlineData("{value: label $out | 1}")]
    public void ParserRejectsFormsRejectedByUpstreamGrammar(string source)
    {
        Assert.Throws<JqCompileException>(() => JqGeneratedParser.ParseSource(source));
    }

    [Theory]
    [InlineData("def twice(f): f | f; twice(. + 1)")]
    [InlineData("if . then . else empty end")]
    [InlineData("try .[] catch .")]
    [InlineData("reduce .[] as $x (0; . + $x)")]
    [InlineData("foreach .[] as $x (0; . + $x; [$x, .])")]
    [InlineData("[1, 2] as [$x, $y] | $x + $y")]
    [InlineData("[1] as {$x} ?// [$x] | $x")]
    [InlineData("@json \"value=\\(.)\"")]
    [InlineData("{if: .then, @json \"shown\", (\"x\" + \"y\"): 4, $key: 5}")]
    [InlineData("{value: (1 as $x | $x), nested: (def f: 1; f)}")]
    [InlineData(".as[1:][].end?")]
    public void ParserAcceptsRepresentativeUpstreamGrammar(string source)
    {
        var parsed = JqGeneratedParser.ParseSource(source);
        try
        {
            Assert.True(libjq.block_has_main(parsed));
            Assert.Equal(opcode.TOP, parsed.first?.op);
        }
        finally
        {
            libjq.block_free(parsed);
        }
    }

    [Fact]
    public void BindPrecedenceMatchesUpstreamOnCommaRightHandSide()
    {
        var output = Execute("1, 2 as $x | [$x]", "null");

        Assert.Equal(2, output.Count);
        Assert.Equal(1, output[0].GetInt32());
        Assert.Equal(2, output[1][0].GetInt32());
    }

    [Fact]
    public void ArithmeticLogicalAndPipePrecedenceMatchesUpstream()
    {
        var output = Execute("1, 2 | . + 10", "null");
        var logical = Assert.Single(
            Execute("1 + 2 * 3 == 7 and false or true", "null"));

        Assert.Equal(ExpectedPipeValues, output.Select(value => value.GetInt32()));
        Assert.True(logical.GetBoolean());
    }

    [Fact]
    public void TryAndCatchReduceBeforeLowerPrecedenceOperators()
    {
        var recoveredAlternative = Assert.Single(
            Execute("try error(0) // 1", "null"));
        var surroundingAddition = Assert.Single(
            Execute("1 + try 2 catch 3 + 4", "null"));

        var parsed = JqGeneratedParser.ParseSource("try error catch . + 2");
        try
        {
            // CATCH has higher precedence than '+': the generated _plus call
            // receives the whole try/catch IR as its left closure argument.
            var addition = Assert.IsType<inst>(parsed.first?.next);
            Assert.Equal((opcode.CALL_JQ, "_plus", 2),
                (addition.op, addition.symbol, addition.nactuals));
            var left = Assert.IsType<inst>(addition.arglist.first);
            Assert.Equal(opcode.CLOSURE_CREATE, left.op);
            Assert.Equal(opcode.TRY_BEGIN, left.subfn.first?.op);
        }
        finally
        {
            libjq.block_free(parsed);
        }
        Assert.Equal(1, recoveredAlternative.GetInt32());
        Assert.Equal(7, surroundingAddition.GetInt32());
    }

    [Fact]
    public void DefinitionsSupportFilterAndValueParameters()
    {
        var filterParameter = Assert.Single(
            Execute("def twice(f): f | f; twice(. + 1)", "1"));
        var valueParameter = Assert.Single(
            Execute("def add($x): . + $x; add(2)", "1"));

        Assert.Equal(3, filterParameter.GetInt32());
        Assert.Equal(3, valueParameter.GetInt32());
    }

    [Fact]
    public void ConditionalReduceAndForeachExecuteWithUpstreamGrouping()
    {
        var conditional = Assert.Single(
            Execute("if . < 0 then -1 elif . == 0 then 0 else 1 end", "0"));
        var reduced = Assert.Single(
            Execute("reduce .[] as $x (0; . + $x)", "[1,2,3]"));
        var foreachResult = Assert.Single(
            Execute("[foreach .[] as $x (0; . + $x)]", "[1,2,3]"));

        Assert.Equal(0, conditional.GetInt32());
        Assert.Equal(6, reduced.GetInt32());
        Assert.Equal(
            ExpectedForeachValues,
            foreachResult.EnumerateArray().Select(value => value.GetInt32()));
    }

    [Fact]
    public void DestructuringAndAlternativePatternsRemainSupported()
    {
        var nested = Assert.Single(
            Execute("[1,{\"j\":2}] as [$i,{j:$j}] | $i + $j", "null"));
        var alternative = Assert.Single(
            Execute("[4] as {$a} ?// [$a] | $a", "null"));

        Assert.Equal(3, nested.GetInt32());
        Assert.Equal(4, alternative.GetInt32());
    }

    [Fact]
    public void InterpolationObjectKeysAndKeywordFieldsMatchUpstream()
    {
        var interpolation = Assert.Single(
            Execute("@json \"value=\\(.x)\"", "{\"x\":\"q\"}"));
        var objectValue = Assert.Single(
            Execute(
                ".bound as $bound | .key as $key | " +
                "{if: .then, $bound, \"shown\", (\"x\" + \"y\"): 4, $key: 5}",
                "{\"then\":1,\"bound\":2,\"shown\":3,\"key\":\"dynamic\"}"));

        Assert.Equal("value=\"q\"", interpolation.GetString());
        Assert.Equal(1, objectValue.GetProperty("if").GetInt32());
        Assert.Equal(2, objectValue.GetProperty("bound").GetInt32());
        Assert.Equal(3, objectValue.GetProperty("shown").GetInt32());
        Assert.Equal(4, objectValue.GetProperty("xy").GetInt32());
        Assert.Equal(5, objectValue.GetProperty("dynamic").GetInt32());
    }

    [Fact]
    public async Task ComputedStringKeyShorthandEvaluatesEachGeneratedKeyOnce()
    {
        const string source = "{\"\\((\"a\",\"b\"))\"}";
        const string input = "{\"a\":1,\"b\":2}";
        var output = Execute(
            source,
            input);

        Assert.Collection(
            output,
            value => Assert.Equal(1, value.GetProperty("a").GetInt32()),
            value => Assert.Equal(2, value.GetProperty("b").GetInt32()));
        Assert.DoesNotContain(output, value => value.GetRawText() is "{\"a\":2}" or "{\"b\":1}");

        if (JqOracle.TryResolveExecutable(out _))
        {
            var oracle = await JqOracle.ExecuteAsync(
                source,
                input,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(oracle.Succeeded, oracle.StandardError);
            Assert.Equal(output.Select(value => value.GetRawText()), oracle.OutputLines);
        }
    }

    [Fact]
    public void KeywordNamedFieldsAndPathPostfixesChain()
    {
        var output = Execute(".as[1:][].end?", "{\"as\":[0,{\"end\":1},{}]}");

        Assert.Equal(2, output.Count);
        Assert.Equal(1, output[0].GetInt32());
        Assert.Equal(JsonValueKind.Null, output[1].ValueKind);
    }

    [Fact]
    public void GetpathUpdatePathFromJqTest1358RunsThroughDirectCompilerAndVm()
    {
        const string filter = ".[] | try (getpath([\"a\",0,\"b\"]) |= 5) catch .";
        const string input =
            "[null,{\"b\":0},{\"a\":0},{\"a\":null},{\"a\":[0,1]}," +
            "{\"a\":{\"b\":1}},{\"a\":[{}]},{\"a\":[{\"c\":3}]}]";
        var expected = new[]
        {
            "{\"a\":[{\"b\":5}]}",
            "{\"b\":0,\"a\":[{\"b\":5}]}",
            "\"Cannot index number with number (0)\"",
            "{\"a\":[{\"b\":5}]}",
            "\"Cannot index number with string (\\\"b\\\")\"",
            "\"Cannot index object with number (0)\"",
            "{\"a\":[{\"b\":5}]}",
            "{\"a\":[{\"c\":3,\"b\":5}]}",
        };

        jq_state? state = libjq.jq_init();
        var inputValue = libjq.jv_parse(input);
        var actual = new List<string>();
        try
        {
            Assert.Equal(1, libjq.jq_compile(state, filter));
            Assert.NotNull(state.Bytecode);
            libjq.jq_start(state, inputValue, 0);
            inputValue = libjq.jv_invalid();
            while (true)
            {
                var result = libjq.jq_next(state);
                if (!result.IsValid)
                {
                    libjq.jv_free(result);
                    break;
                }

                actual.Add(libjq.jv_dump_string_borrowed(result));
                libjq.jv_free(result);
            }
        }
        finally
        {
            libjq.jv_free(inputValue);
            libjq.jq_teardown(ref state);
        }

        Assert.Equal(expected, actual);
    }

    private static IReadOnlyList<JsonElement> Execute(string source, string input) =>
        JqProgram.Compile(source).Execute(input);

    private static List<Token> Lex(string source)
    {
        var lexer = new jq_lexer(source);
        var tokens = new List<Token>();
        do
        {
            tokens.Add(lexer.Next());
        }
        while (tokens[^1].Kind != TokenKind.End);

        return tokens;
    }
}
