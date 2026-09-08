using DotNetJq.Port;
using DotNetJq.Port.GeneratedParser;

namespace DotNetJq.Tests;

public sealed class SourceLocationCompatibilityTests
{
    [Fact]
    public void LocationInterpolationMatchesCoreAndManualFixtures()
    {
        var output = Assert.Single(
            JqProgram.Compile("try error(\"\\($__loc__)\") catch .").Execute("null"));

        Assert.Equal("{\"file\":\"<top-level>\",\"line\":1}", output.GetString());
    }

    [Fact]
    public void LocationUsesTheTokenSourceLine()
    {
        var output = Assert.Single(JqProgram.Compile("1 |\n\n$__loc__").Execute("null"));

        Assert.Equal("<top-level>", output.GetProperty("file").GetString());
        Assert.Equal(3, output.GetProperty("line").GetInt32());
    }

    [Fact]
    public void LocationObjectShorthandMatchesUpstreamDictionaryRule()
    {
        var output = Assert.Single(
            JqProgram.Compile("{ a, $__loc__, c }").Execute(
                "{\"a\":[1,2,3],\"b\":\"foo\",\"c\":{\"hi\":\"hey\"}}"));

        var location = output.GetProperty("__loc__");
        Assert.Equal("<top-level>", location.GetProperty("file").GetString());
        Assert.Equal(1, location.GetProperty("line").GetInt32());
    }

    [Fact]
    public void ParserCanCarryAnExplicitModuleSourceName()
    {
        var parsed = JqGeneratedParser.ParseSource(
            "1 |\n$__loc__",
            "/modules/example.jq");
        var file = libjq.jv_invalid();
        var line = libjq.jv_invalid();
        try
        {
            var locationInstruction = Assert.Single(
                Instructions(parsed),
                static instruction =>
                    instruction.op == opcode.LOADK &&
                    instruction.imm.constant.Kind == jv_kind.JV_KIND_OBJECT);
            file = libjq.jv_object_get(locationInstruction.imm.constant, "file");
            line = libjq.jv_object_get(locationInstruction.imm.constant, "line");

            Assert.Equal("/modules/example.jq", file.StringValue);
            Assert.Equal(2, line.NumberValue);
        }
        finally
        {
            libjq.jv_free(file);
            libjq.jv_free(line);
            libjq.block_free(parsed);
        }
    }

    [Theory]
    [InlineData(". as $__loc__ | .")]
    [InlineData("def f($__loc__): .; f(1)")]
    public void LocationTokenCannotBeUsedAsAnOrdinaryBinding(string source)
    {
        Assert.Throws<JqCompileException>(() => JqProgram.Compile(source));
    }

    private static IEnumerable<inst> Instructions(block value)
    {
        for (var instruction = value.first;
             instruction is not null;
             instruction = instruction.next)
        {
            yield return instruction;
        }
    }
}
