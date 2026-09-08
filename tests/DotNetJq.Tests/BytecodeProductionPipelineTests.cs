// DOTNETJQ PORT TEST MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream files: src/compile.c, src/bytecode.c, src/execute.c
// Primary production paths: block_compile/compile, jq_compile_args, bytecode
// constant/subfunction ownership, jq_start, jq_next, and the opcode dispatcher.

using DotNetJq.Port;

namespace DotNetJq.Tests;

public sealed class BytecodeProductionPipelineTests
{
    // Raw ushort streams were read from the pinned native block_compile()
    // output under GDB, then cross-checked against --debug-dump-disasm. Branch
    // operands are the forward relative word counts emitted at compile.c:
    //   target->bytecode_pos - (operand_position + 1)
    public static TheoryData<GoldenProgram> TopLevelPrograms => new()
    {
        new GoldenProgram(
            "identity",
            ".",
            [(ushort)opcode.TOP, (ushort)opcode.RET],
            []),
        new GoldenProgram(
            "constant",
            "1",
            [(ushort)opcode.TOP, (ushort)opcode.LOADK, 0, (ushort)opcode.RET],
            ["1"]),
        new GoldenProgram(
            "constant-index",
            ".[0]",
            [
                (ushort)opcode.TOP,
                (ushort)opcode.PUSHK_UNDER, 0,
                (ushort)opcode.INDEX,
                (ushort)opcode.RET,
            ],
            ["0"]),
        new GoldenProgram(
            "fork-and-jump",
            "(.,.)",
            [
                (ushort)opcode.TOP,
                (ushort)opcode.FORK, 2,
                (ushort)opcode.JUMP, 0,
                (ushort)opcode.RET,
            ],
            []),
        new GoldenProgram(
            "conditional-branches",
            "if . then 1 else 2 end",
            [
                (ushort)opcode.TOP,
                (ushort)opcode.DUP,
                (ushort)opcode.DUP,
                (ushort)opcode.POP,
                (ushort)opcode.JUMP_F, 5,
                (ushort)opcode.POP,
                (ushort)opcode.LOADK, 0,
                (ushort)opcode.JUMP, 3,
                (ushort)opcode.POP,
                (ushort)opcode.LOADK, 1,
                (ushort)opcode.RET,
            ],
            ["1", "2"]),
        new GoldenProgram(
            "collect-each-builtin",
            "[.[] | . + 1]",
            [
                (ushort)opcode.TOP,
                (ushort)opcode.DUP,
                (ushort)opcode.LOADK, 0,
                (ushort)opcode.STOREV, 0, 0,
                (ushort)opcode.FORK, 11,
                (ushort)opcode.EACH,
                (ushort)opcode.PUSHK_UNDER, 1,
                (ushort)opcode.DUP,
                (ushort)opcode.CALL_BUILTIN, 3, 0,
                (ushort)opcode.APPEND, 0, 0,
                (ushort)opcode.BACKTRACK,
                (ushort)opcode.LOADVN, 0, 0,
                (ushort)opcode.RET,
            ],
            ["[]", "1"],
            LocalCount: 2,
            CFunctions: ["_plus"]),
    };

    [Theory]
    [MemberData(nameof(TopLevelPrograms))]
    public void RealFiltersCompileToPinnedJq182Bytecode(GoldenProgram golden)
    {
        jq_state? state = libjq.jq_init();
        try
        {
            Assert.Equal(1, libjq.jq_compile(state, golden.Filter));
            var bytecode = Assert.IsType<bytecode>(state.Bytecode);

            Assert.Equal(golden.Code, bytecode.code);
            Assert.Equal(
                golden.Constants,
                bytecode.constants.ArrayValue
                    .Select(static value => libjq.jv_dump_string_borrowed(value))
                    .ToArray());
            if (golden.LocalCount is { } localCount)
            {
                Assert.Equal(localCount, bytecode.nlocals);
            }

            if (golden.CFunctions is { } cfunctions)
            {
                Assert.Equal(
                    cfunctions,
                    bytecode.globals!.cfunc_names.ArrayValue
                        .Select(value => value.StringValue)
                        .ToArray());
            }

            AssertWellFormedInstructionStream(bytecode);
        }
        finally
        {
            libjq.jq_teardown(ref state);
        }
    }

    [Fact]
    public void CompilerBuildsNativeShapedSubfunctionGraph()
    {
        jq_state? state = libjq.jq_init();
        try
        {
            Assert.Equal(1, libjq.jq_compile(state, "def f: 1; f"));
            var root = Assert.IsType<bytecode>(state.Bytecode);

            Assert.Equal(
                [
                    (ushort)opcode.TOP,
                    (ushort)opcode.CALL_JQ, 0, 0, libjq.ARG_NEWCLOSURE,
                    (ushort)opcode.RET,
                ],
                root.code);
            Assert.Equal(0, root.constants.ArrayValue.Count);
            var child = Assert.Single(root.subfunctions);
            Assert.Equal(
                [(ushort)opcode.LOADK, 0, (ushort)opcode.RET],
                child.code);
            Assert.Equal(
                ["1"],
                child.constants.ArrayValue
                    .Select(static value => libjq.jv_dump_string_borrowed(value))
                    .ToArray());
            Assert.Same(root, child.parent);
            Assert.Same(root.globals, child.globals);
            AssertWellFormedInstructionStream(root);
            AssertWellFormedInstructionStream(child);
        }
        finally
        {
            libjq.jq_teardown(ref state);
        }
    }

    [Fact]
    public void JqStartAndNextDispatchTheCompiledOpcodeStream()
    {
        jq_state? state = libjq.jq_init();
        var input = libjq.jv_string("ast-input");
        var output = libjq.jv_invalid();
        try
        {
            Assert.Equal(1, libjq.jq_compile(state, "."));
            var bytecode = Assert.IsType<bytecode>(state.Bytecode);

            // Replace the compiler-produced identity instruction stream with
            // valid jq bytecode for a constant. If jq_start does not dispatch
            // state.Bytecode, the original input is returned; only direct VM
            // dispatch can return the sentinel.
            bytecode.constants = libjq.jv_array_append(
                bytecode.constants,
                libjq.jv_string("vm-sentinel"));
            bytecode.code =
            [
                (ushort)opcode.TOP,
                (ushort)opcode.LOADK, 0,
                (ushort)opcode.RET,
            ];

            libjq.jq_start(state, input, 0);
            input = libjq.jv_invalid();
            output = libjq.jq_next(state);
            Assert.True(output.IsValid);
            Assert.Equal("vm-sentinel", output.StringValue);
            libjq.jv_free(output);
            output = libjq.jv_invalid();
            Assert.False(libjq.jq_next(state).IsValid);
        }
        finally
        {
            libjq.jv_free(output);
            libjq.jv_free(input);
            libjq.jq_teardown(ref state);
        }
    }

    [Fact]
    public void ProductionBytecodeLifetimeFollowsTheCompiledProgramOwner()
    {
        jq_state? state = libjq.jq_init();
        Assert.Equal(1, libjq.jq_compile(state, "def f: 1; f"));
        var bytecode = Assert.IsType<bytecode>(state.Bytecode);
        var child = Assert.Single(bytecode.subfunctions);

        libjq.jq_teardown(ref state);

        // bytecode_free() recursively clears children and only the root frees
        // the shared symbol table. This must happen through the real program
        // lifecycle, not only in tests over hand-constructed bytecode objects.
        Assert.Empty(bytecode.code);
        Assert.False(bytecode.constants.IsValid);
        Assert.Empty(bytecode.subfunctions);
        Assert.Null(bytecode.globals);
        Assert.Empty(child.code);
        Assert.False(child.constants.IsValid);
        Assert.Null(child.parent);
        Assert.Null(child.globals);
    }

    [Theory]
    [InlineData("error(\"boom\")", "null", "boom")]
    [InlineData(
        "1 / 0",
        "null",
        "number (1) and number (0) cannot be divided because the divisor is zero")]
    [InlineData("{(.): 3}", "1", "Cannot use number (1) as object key")]
    public void JqNextPreservesUncaughtRuntimeErrorMessages(
        string filter,
        string inputJson,
        string expectedMessage)
    {
        jq_state? state = libjq.jq_init();
        var input = libjq.jv_invalid();
        var result = libjq.jv_invalid();
        var message = libjq.jv_invalid();
        try
        {
            Assert.Equal(1, libjq.jq_compile(state, filter));
            input = libjq.jv_parse(inputJson);
            libjq.jq_start(state, input, 0);
            input = libjq.jv_invalid();

            result = libjq.jq_next(state);
            Assert.False(result.IsValid);
            Assert.True(libjq.jv_invalid_has_msg(libjq.jv_copy(result)));
            message = libjq.jv_invalid_get_msg(result);
            result = libjq.jv_invalid();
            Assert.Equal(expectedMessage, message.StringValue);
        }
        finally
        {
            libjq.jv_free(message);
            libjq.jv_free(result);
            libjq.jv_free(input);
            libjq.jq_teardown(ref state);
        }
    }

    [Theory]
    [InlineData("try error(\"boom\") catch .", "\"boom\"")]
    public void TryCatchConvertsRuntimeErrorToAnOrdinaryValue(
        string filter,
        string expectedJson)
    {
        jq_state? state = libjq.jq_init();
        var input = libjq.jv_invalid();
        var result = libjq.jv_invalid();
        try
        {
            Assert.Equal(1, libjq.jq_compile(state, filter));
            input = libjq.jv_null();
            libjq.jq_start(state, input, 0);
            input = libjq.jv_invalid();

            result = libjq.jq_next(state);
            Assert.True(result.IsValid);
            Assert.Equal(expectedJson, libjq.jv_dump_string_borrowed(result));
            libjq.jv_free(result);
            result = libjq.jv_invalid();

            var exhausted = libjq.jq_next(state);
            Assert.False(exhausted.IsValid);
            Assert.False(libjq.jv_invalid_has_msg(exhausted));
        }
        finally
        {
            libjq.jv_free(result);
            libjq.jv_free(input);
            libjq.jq_teardown(ref state);
        }
    }

    [Fact]
    public void EmptyReturnsMessageFreeExhaustion()
    {
        jq_state? state = libjq.jq_init();
        var input = libjq.jv_invalid();
        try
        {
            Assert.Equal(1, libjq.jq_compile(state, "empty"));
            input = libjq.jv_null();
            libjq.jq_start(state, input, 0);
            input = libjq.jv_invalid();

            var exhausted = libjq.jq_next(state);
            Assert.False(exhausted.IsValid);
            Assert.False(libjq.jv_invalid_has_msg(exhausted));
        }
        finally
        {
            libjq.jv_free(input);
            libjq.jq_teardown(ref state);
        }
    }

    private static void AssertWellFormedInstructionStream(bytecode bytecode)
    {
        var boundaries = new HashSet<int>();
        var pc = 0;
        while (pc < bytecode.codelen)
        {
            boundaries.Add(pc);
            var opValue = bytecode.code[pc];
            Assert.InRange(opValue, (ushort)0, (ushort)(libjq.NUM_OPCODES - 1));
            var length = libjq.bytecode_operation_length(bytecode.code, pc);
            Assert.True(length > 0);
            Assert.True(pc + length <= bytecode.codelen);
            pc += length;
        }

        Assert.Equal(bytecode.codelen, pc);
        boundaries.Add(bytecode.codelen);

        foreach (var operation in boundaries.Where(value => value < bytecode.codelen))
        {
            var description = libjq.opcode_describe((opcode)bytecode.code[operation]);
            if ((description.flags & libjq.OP_HAS_BRANCH) == 0)
            {
                continue;
            }

            var target = operation + 2 + bytecode.code[operation + 1];
            Assert.True(target > operation);
            Assert.Contains(target, boundaries);
        }
    }

    public sealed record GoldenProgram(
        string Name,
        string Filter,
        ushort[] Code,
        string[] Constants,
        int? LocalCount = null,
        string[]? CFunctions = null)
    {
        public override string ToString() => Name;
    }
}
