using DotNetJq.Compatibility.FileSystem;
using DotNetJq.Port;
using DotNetJq.Port.GeneratedParser;
using DotNetJq.Tests.Harness;

namespace DotNetJq.Tests;

public sealed class ConstantFoldingParserCompatibilityTests
{
    private const string FoldedMetadataSource =
        "module {" +
        "number:(1+2)," +
        "text:(\"a\"+\"b\")," +
        "array:([1]+[2])," +
        "object:({a:1}+{b:2})," +
        "recursive:({a:{x:1}}*{a:{y:2}})," +
        "difference:([1,2]-[2])," +
        "quotient:(6/2)," +
        "split:(\"a,b\"/\",\")," +
        "remainder:(7%3)," +
        "equal:(1==1)," +
        "notEqual:(1!=2)," +
        "less:(1<2)," +
        "lessEqual:(1<=2)," +
        "greater:(2>1)," +
        "greaterEqual:(2>=1)" +
        "}; .";

    [Theory]
    [InlineData("{(1+2):3}", "number (3)", "^^^")]
    [InlineData("{([1]+[2]):3}", "array ([1,2])", "^^^^^^^")]
    [InlineData("{({a:1}+{b:2}):3}", "object ({\"a\":1,\"b\":2})", "^^^^^^^^^^^")]
    [InlineData("{(1<2):3}", "boolean (true)", "^^^")]
    public async Task FoldedNonStringObjectKeysFailDuringCompilationAtTheExpression(
        string source,
        string renderedKey,
        string carets)
    {
        var expected =
            $"jq: error: Cannot use {renderedKey} as object key at <top-level>, line 1, column 3:\n" +
            "    " + source + "\n" +
            "      " + carets;
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
    public async Task FoldedStringObjectKeyIsAcceptedAndExecuted()
    {
        const string source = "{(\"a\"+\"b\"):3}";
        var managed = Assert.Single(JqProgram.Compile(source).Execute("null"));
        Assert.Equal("{\"ab\":3}", managed.GetRawText());

        if (!JqOracle.TryResolveExecutable(out _))
        {
            return;
        }

        var oracle = await JqOracle.ExecuteAsync(
            source,
            "null",
            arguments: ["--null-input"],
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(oracle.Succeeded, oracle.StandardError);
        Assert.Equal([managed.GetRawText()], oracle.OutputLines);
    }

    [Fact]
    public async Task ArithmeticComparisonAndContainerConstantsFoldInsideModuleMetadata()
    {
        const string expected =
            "{\"number\":3,\"text\":\"ab\",\"array\":[1,2]," +
            "\"object\":{\"a\":1,\"b\":2}," +
            "\"recursive\":{\"a\":{\"x\":1,\"y\":2}}," +
            "\"difference\":[1],\"quotient\":3,\"split\":[\"a\",\"b\"]," +
            "\"remainder\":1,\"equal\":true,\"notEqual\":true," +
            "\"less\":true,\"lessEqual\":true,\"greater\":true," +
            "\"greaterEqual\":true}";
        var parsed = JqGeneratedParser.ParseSource(FoldedMetadataSource);
        try
        {
            var metadata = Assert.Single(
                EnumerateInstructions(parsed),
                static instruction => instruction.op == opcode.MODULEMETA);
            Assert.Equal(
                jv_kind.JV_KIND_OBJECT,
                libjq.jv_get_kind(metadata.imm.constant));
            Assert.Equal(expected, libjq.jv_dump_string_borrowed(metadata.imm.constant));
        }
        finally
        {
            libjq.block_free(parsed);
        }

        if (!JqOracle.TryResolveExecutable(out _))
        {
            return;
        }

        var oracle = await JqOracle.ExecuteAsync(
            FoldedMetadataSource,
            "null",
            arguments: ["--null-input"],
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(oracle.Succeeded, oracle.StandardError);
    }

    [Fact]
    public async Task FoldedConstantsAreAcceptedInsideImportMetadata()
    {
        const string source =
            "import \"a\" as a {number:(1+2),name:(\"a\"+\"b\")}; a::a";
        var parsed = JqGeneratedParser.ParseSource(source);
        try
        {
            var dependency = Assert.Single(
                EnumerateInstructions(parsed),
                static instruction => instruction.op == opcode.DEPS);
            Assert.Equal(
                "{\"number\":3,\"name\":\"ab\",\"as\":\"a\"," +
                "\"is_data\":false,\"relpath\":\"a\"}",
                libjq.jv_dump_string_borrowed(dependency.imm.constant));
        }
        finally
        {
            libjq.block_free(parsed);
        }

        var resolver = new JqModuleResolver(
            new JqInMemoryFileSystem(new Dictionary<string, string>
            {
                ["/modules/a.jq"] = "def a: \"a\";",
            }),
            ["/modules"],
            "/program");
        var managed = Assert.Single(JqProgram.Compile(source, resolver).Execute("null"));
        Assert.Equal("a", managed.GetString());

        if (!JqOracle.TryResolveExecutable(out _) ||
            !UpstreamTestFile.TryResolveUpstreamRoot(out var upstreamRoot))
        {
            return;
        }

        var modulesDirectory = Path.Combine(upstreamRoot, "tests", "modules");
        var oracle = await JqOracle.ExecuteAsync(
            source,
            "null",
            arguments: ["--null-input", "--library-path", modulesDirectory],
            workingDirectory: modulesDirectory,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(oracle.Succeeded, oracle.StandardError);
        Assert.Equal([managed.GetRawText()], oracle.OutputLines);
    }

    [Theory]
    [InlineData("module {x:(true and false)}; .")]
    [InlineData("module {x:(true or false)}; .")]
    [InlineData("module {x:(null // 2)}; .")]
    [InlineData("module {x:(-1)}; .")]
    [InlineData("module {x:(if true then 1 else 2 end)}; .")]
    [InlineData("module {x:(1/0)}; .")]
    [InlineData("module {x:(\"a\"-\"b\")}; .")]
    [InlineData("import \"a\" as a {x:(true and false)}; .")]
    public async Task FormsNotFoldedByUpstreamRemainNonConstantMetadata(string source)
    {
        var managed = Assert.Throws<JqCompileException>(() => JqProgram.Compile(source));
        Assert.Contains("Module metadata must be constant", managed.Message, StringComparison.Ordinal);

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
        Assert.Contains("Module metadata must be constant", oracle.StandardError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        "module (1+2); .",
        "jq: error: Module metadata must be an object at <top-level>, line 1, column 8:\n" +
        "    module (1+2); .\n" +
        "           ^^^^^")]
    [InlineData(
        "import \"a\" as a (1+2); .",
        "jq: error: Module metadata must be an object at <top-level>, line 1, column 17:\n" +
        "    import \"a\" as a (1+2); .\n" +
        "                    ^^^^^")]
    public async Task FoldedScalarMetadataFailsAtCompileTimeWithTheFullExpressionSpan(
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

    [Theory]
    [InlineData(
        "{(true and false):3}",
        "Cannot use boolean (false) as object key")]
    [InlineData(
        "{(null // 2):3}",
        "Cannot use number (2) as object key")]
    [InlineData(
        "{(-1):3}",
        "Cannot use number (-1) as object key")]
    [InlineData("{(1/0):3}", "divisor is zero")]
    public async Task NonFoldedAndInvalidObjectKeysRemainRuntimeErrors(
        string source,
        string expected)
    {
        var program = JqProgram.Compile(source);
        var managed = Assert.Throws<JqRuntimeException>(() => program.Execute("null"));
        Assert.Contains(expected, managed.Message, StringComparison.Ordinal);

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

    private static IEnumerable<inst> EnumerateInstructions(block value)
    {
        for (var instruction = value.first;
             instruction is not null;
             instruction = instruction.next)
        {
            yield return instruction;
        }
    }
}
