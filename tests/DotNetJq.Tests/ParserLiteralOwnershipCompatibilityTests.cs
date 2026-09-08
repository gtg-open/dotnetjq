// DOTNETJQ PORT TEST MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream files: src/parser.y, src/compile.c
// Primary ownership paths: scanner token literals, gen_const(),
// gen_dictpair(), constant folding, parser %destructor equivalents, block_free().

using DotNetJq.Port;
using DotNetJq.Port.GeneratedParser;

namespace DotNetJq.Tests;

public sealed class ParserLiteralOwnershipCompatibilityTests
{
    [Theory]
    [InlineData("foo")]
    [InlineData("if")]
    public void ObjectShorthandSplitsTheTokenIntoExactlyTwoIrOwners(string keyText)
    {
        var parsed = JqGeneratedParser.ParseSource("{" + keyText + "}");
        jvp_string? storage = null;
        try
        {
            var keys = EnumerateInstructions(parsed)
                .Where(instruction =>
                    instruction.op is opcode.LOADK or opcode.PUSHK_UNDER &&
                    instruction.imm.constant.Kind == jv_kind.JV_KIND_STRING &&
                    instruction.imm.constant.StringValue == keyText)
                .Select(static instruction => instruction.imm.constant)
                .ToArray();

            Assert.Equal(2, keys.Length);
            Assert.Equal(2, libjq.jv_get_refcnt(keys[0]));
            Assert.Equal(2, libjq.jv_get_refcnt(keys[1]));
            Assert.True(libjq.jv_identical(
                libjq.jv_copy(keys[0]),
                libjq.jv_copy(keys[1])));
            storage = Assert.IsType<jvp_string>(keys[0].Value);
        }
        finally
        {
            libjq.block_free(parsed);
        }

        Assert.NotNull(storage);
        Assert.Equal(0, storage.Refcnt.Count);
    }

    [Theory]
    [InlineData("foo")]
    [InlineData("if")]
    public void ExplicitObjectKeyMovesTheSingleTokenOwnerIntoIr(string keyText)
    {
        // A nonconstant value keeps gen_const_object() from folding the pair,
        // exposing the exact key owner moved by parser.y's IDENT/Keyword ':'
        // DictExpr action.
        var parsed = JqGeneratedParser.ParseSource("{" + keyText + ": .}");
        jvp_string? storage = null;
        try
        {
            var key = Assert.Single(
                EnumerateInstructions(parsed),
                instruction =>
                    instruction.op is opcode.LOADK or opcode.PUSHK_UNDER &&
                    instruction.imm.constant.Kind == jv_kind.JV_KIND_STRING &&
                    instruction.imm.constant.StringValue == keyText);

            Assert.Equal(1, libjq.jv_get_refcnt(key.imm.constant));
            storage = Assert.IsType<jvp_string>(key.imm.constant.Value);
        }
        finally
        {
            libjq.block_free(parsed);
        }

        Assert.NotNull(storage);
        Assert.Equal(0, storage.Refcnt.Count);
    }

    [Fact]
    public void ConstantObjectFoldConsumesPairOwnersAndLeavesOneObjectInstruction()
    {
        var parsed = JqGeneratedParser.ParseSource("{foo: 1}");
        jvp_object? storage = null;
        try
        {
            Assert.Equal(opcode.TOP, parsed.first?.op);
            var constant = Assert.IsType<inst>(parsed.first?.next);
            Assert.Equal(opcode.LOADK, constant.op);
            Assert.Null(constant.next);
            Assert.Equal(jv_kind.JV_KIND_OBJECT, constant.imm.constant.Kind);
            Assert.Equal("{\"foo\":1}", libjq.jv_dump_string_borrowed(constant.imm.constant));
            Assert.Equal(1, libjq.jv_get_refcnt(constant.imm.constant));
            storage = Assert.IsType<jvp_object>(constant.imm.constant.Value);
        }
        finally
        {
            libjq.block_free(parsed);
        }

        Assert.NotNull(storage);
        Assert.Equal(0, storage.Refcnt.Count);
    }

    [Fact]
    public void MetadataInspectionBorrowsInstructionConstantsUntilBlockFree()
    {
        const string source =
            "module {\"m\":1}; " +
            "import \"thing\" as thing {\"x\":2}; .";
        var parsed = JqGeneratedParser.ParseSource(source);
        jvp_object? moduleStorage = null;
        jvp_object? dependencyStorage = null;
        try
        {
            var instructions = EnumerateInstructions(parsed).ToArray();
            var module = Assert.Single(
                instructions,
                static instruction => instruction.op == opcode.MODULEMETA);
            var dependency = Assert.Single(
                instructions,
                static instruction => instruction.op == opcode.DEPS);
            moduleStorage = Assert.IsType<jvp_object>(module.imm.constant.Value);
            dependencyStorage = Assert.IsType<jvp_object>(dependency.imm.constant.Value);

            Assert.Equal(1, libjq.jv_get_refcnt(module.imm.constant));
            Assert.Equal(1, libjq.jv_get_refcnt(dependency.imm.constant));
            Assert.Equal(
                "{\"m\":1}",
                libjq.jv_dump_string_borrowed(module.imm.constant));
            Assert.Equal(
                "{\"x\":2,\"as\":\"thing\",\"is_data\":false," +
                "\"relpath\":\"thing\"}",
                libjq.jv_dump_string_borrowed(dependency.imm.constant));
            Assert.Equal(1, libjq.jv_get_refcnt(module.imm.constant));
            Assert.Equal(1, libjq.jv_get_refcnt(dependency.imm.constant));
            moduleStorage.EnsureAlive();
            dependencyStorage.EnsureAlive();
        }
        finally
        {
            libjq.block_free(parsed);
        }

        Assert.NotNull(moduleStorage);
        Assert.NotNull(dependencyStorage);
        Assert.Equal(0, moduleStorage.Refcnt.Count);
        Assert.Equal(0, dependencyStorage.Refcnt.Count);
        _ = Assert.Throws<ObjectDisposedException>(() => moduleStorage.EnsureAlive());
        _ = Assert.Throws<ObjectDisposedException>(() => dependencyStorage.EnsureAlive());
    }

    [Fact]
    public void UnrecoverableLookaheadLiteralIsFreedLikeBisonDestructor()
    {
        const string source = "foo bar";
        var scanner = new JqGeneratedParserScanner(source);
        var locations = libjq.locfile_init("<top-level>", source);
        try
        {
            var parser = new JqGeneratedParser(
                scanner,
                locations,
                source,
                "<top-level>");

            _ = Assert.Throws<JqCompileException>(() => parser.ParseProgram());
            var discarded = scanner.yylval.literal;

            Assert.Equal(jv_kind.JV_KIND_STRING, discarded.Kind);
            _ = Assert.Throws<ObjectDisposedException>(() => discarded.StringValue);
        }
        finally
        {
            libjq.locfile_free(locations);
        }
    }

    [Fact]
    public void ReducedIrLiteralIsFreedWhenALaterTokenAbortsTheParse()
    {
        const string source = "1 foo";
        var scanner = new JqGeneratedParserScanner(source);
        var locations = libjq.locfile_init("<top-level>", source);
        try
        {
            var parser = new JqGeneratedParser(
                scanner,
                locations,
                source,
                "<top-level>");

            _ = Assert.Throws<JqCompileException>(() => parser.ParseProgram());
            var reducedNumber = Assert.IsType<JvNumber>(scanner.ScannedLiterals[0].Value);

            Assert.NotNull(reducedNumber.Refcnt);
            Assert.Equal(0, reducedNumber.Refcnt.Count);
            _ = Assert.Throws<ObjectDisposedException>(() => reducedNumber.EnsureAlive());
        }
        finally
        {
            libjq.locfile_free(locations);
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
}
