// DOTNETJQ PORT TEST MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream file: src/execute.c
// Primary paths: jq_start/jq_next/jq_reset, frame/stack/forkpoint ownership,
// every executable opcode family, ON_BACKTRACK arms, paths, errors, and calls.

using DotNetJq.Port;

namespace DotNetJq.Tests;

public sealed class DirectBytecodeVmCompatibilityTests
{
    [Fact]
    public void IdentityAndConstantProgramsUseTopLevelRetLifecycle()
    {
        var identity = Execute(
            Program(opcode.TOP, opcode.RET),
            libjq.jv_array([libjq.jv_number(1)]));
        Assert.Equal(["[1]"], identity.Outputs);
        Assert.Null(identity.Error);

        var constant = Execute(
            Program(
                [(ushort)opcode.TOP, (ushort)opcode.LOADK, 0, (ushort)opcode.RET],
                [libjq.jv_string("constant")]),
            libjq.jv_string("discarded"));
        Assert.Equal(["\"constant\""], constant.Outputs);
        Assert.Null(constant.Error);
    }

    [Fact]
    public void StackDuplicationAndSubexpressionOpcodesPreserveOrdering()
    {
        var duplicateTwo = Execute(
            Program(
                [
                    (ushort)opcode.TOP,
                    (ushort)opcode.PUSHK_UNDER, 0,
                    (ushort)opcode.DUP2,
                    (ushort)opcode.POP,
                    (ushort)opcode.POP,
                    (ushort)opcode.RET,
                ],
                [libjq.jv_string("under")]),
            libjq.jv_string("top"));
        Assert.Equal(["\"under\""], duplicateTwo.Outputs);

        var subexpression = Execute(
            Program(
                opcode.TOP,
                opcode.SUBEXP_BEGIN,
                opcode.SUBEXP_END,
                opcode.POP,
                opcode.RET),
            libjq.jv_string("value"));
        Assert.Equal(["\"value\""], subexpression.Outputs);
    }

    [Fact]
    public void DupnMutatesOnlyTheSharedSavedStackSlot()
    {
        var result = Execute(
            Program(
                [
                    (ushort)opcode.TOP,
                    (ushort)opcode.FORK, 3,
                    (ushort)opcode.DUPN,
                    (ushort)opcode.POP,
                    (ushort)opcode.RET,
                    (ushort)opcode.RET,
                ]),
            libjq.jv_string("value"));

        Assert.Equal(["\"value\"", "null"], result.Outputs);
        Assert.Null(result.Error);
    }

    [Fact]
    public void ForkAndConditionalBranchesResumeAtNativeOffsets()
    {
        var fork = Execute(
            Program(
                [
                    (ushort)opcode.TOP,
                    (ushort)opcode.FORK, 2,
                    (ushort)opcode.JUMP, 0,
                    (ushort)opcode.RET,
                ]),
            libjq.jv_number(7));
        Assert.Equal(["7", "7"], fork.Outputs);

        var conditional = Execute(
            Program(
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
                [libjq.jv_number(1), libjq.jv_number(2)]),
            libjq.jv_false());
        Assert.Equal(["2"], conditional.Outputs);
    }

    [Fact]
    public void VariableMoveStoreAppendGlobalAndRangeMatchFrameSlots()
    {
        var moved = Execute(
            Program(
                [
                    (ushort)opcode.TOP,
                    (ushort)opcode.DUP,
                    (ushort)opcode.STOREV, 0, 0,
                    (ushort)opcode.LOADVN, 0, 0,
                    (ushort)opcode.RET,
                ],
                localCount: 1),
            libjq.jv_string("moved"));
        Assert.Equal(["\"moved\""], moved.Outputs);

        var appended = Execute(
            Program(
                [
                    (ushort)opcode.TOP,
                    (ushort)opcode.DUP,
                    (ushort)opcode.LOADK, 0,
                    (ushort)opcode.STOREV, 0, 0,
                    (ushort)opcode.DUP,
                    (ushort)opcode.APPEND, 0, 0,
                    (ushort)opcode.LOADVN, 0, 0,
                    (ushort)opcode.RET,
                ],
                [libjq.jv_array()],
                localCount: 1),
            libjq.jv_number(4));
        Assert.Equal(["[4]"], appended.Outputs);

        var global = Execute(
            Program(
                [
                    (ushort)opcode.TOP,
                    (ushort)opcode.STORE_GLOBAL, 0, 0, 0,
                    (ushort)opcode.LOADV, 0, 0,
                    (ushort)opcode.RET,
                ],
                [libjq.jv_string("global")],
                localCount: 1),
            libjq.jv_null());
        Assert.Equal(["\"global\""], global.Outputs);

        var range = Execute(
            Program(
                [
                    (ushort)opcode.TOP,
                    (ushort)opcode.DUP,
                    (ushort)opcode.LOADK, 0,
                    (ushort)opcode.STOREV, 0, 0,
                    (ushort)opcode.LOADK, 1,
                    (ushort)opcode.RANGE, 0, 0,
                    (ushort)opcode.RET,
                ],
                [libjq.jv_number(0), libjq.jv_number(3)],
                localCount: 1),
            libjq.jv_null());
        Assert.Equal(["0", "1", "2"], range.Outputs);
    }

    [Fact]
    public void StorevnBacktrackRestoresAndClearsItsFrameSlot()
    {
        var result = Execute(
            Program(
                [
                    (ushort)opcode.TOP,
                    (ushort)opcode.DUP,
                    (ushort)opcode.STOREVN, 0, 0,
                    (ushort)opcode.LOADV, 0, 0,
                    (ushort)opcode.RET,
                ],
                localCount: 1),
            libjq.jv_string("stored"));

        Assert.Equal(["\"stored\""], result.Outputs);
        Assert.Null(result.Error);
    }

    [Fact]
    public void IndexEachAndPathTrackContainersAndPathSnapshots()
    {
        var indexed = Execute(
            Program(
                [
                    (ushort)opcode.TOP,
                    (ushort)opcode.PUSHK_UNDER, 0,
                    (ushort)opcode.INDEX,
                    (ushort)opcode.RET,
                ],
                [libjq.jv_string("a")]),
            libjq.jv_object(
                [KeyValuePair.Create("a", libjq.jv_number(1))]));
        Assert.Equal(["1"], indexed.Outputs);

        var each = Execute(
            Program(opcode.TOP, opcode.EACH, opcode.RET),
            libjq.jv_array([libjq.jv_number(1), libjq.jv_number(2)]));
        Assert.Equal(["1", "2"], each.Outputs);

        var optionalEach = Execute(
            Program(opcode.TOP, opcode.EACH_OPT, opcode.RET),
            libjq.jv_number(1));
        Assert.Empty(optionalEach.Outputs);
        Assert.Null(optionalEach.Error);

        var path = Execute(
            Program(
                [
                    (ushort)opcode.TOP,
                    (ushort)opcode.PATH_BEGIN,
                    (ushort)opcode.PUSHK_UNDER, 0,
                    (ushort)opcode.INDEX,
                    (ushort)opcode.PATH_END,
                    (ushort)opcode.RET,
                ],
                [libjq.jv_string("a")]),
            libjq.jv_object(
                [KeyValuePair.Create("a", libjq.jv_number(1))]));
        Assert.Equal(["[\"a\"]"], path.Outputs);
    }

    [Fact]
    public void InsertAndGenlabelUseNativeStackAndStateOrdering()
    {
        var inserted = Execute(
            Program(
                [
                    (ushort)opcode.TOP,
                    (ushort)opcode.PUSHK_UNDER, 0,
                    (ushort)opcode.PUSHK_UNDER, 1,
                    (ushort)opcode.PUSHK_UNDER, 2,
                    (ushort)opcode.INSERT,
                    (ushort)opcode.POP,
                    (ushort)opcode.RET,
                ],
                [
                    libjq.jv_object(),
                    libjq.jv_string("key"),
                    libjq.jv_number(2),
                ]),
            libjq.jv_null());
        Assert.Equal(["{\"key\":2}"], inserted.Outputs);

        var labels = Execute(
            Program(
                [
                    (ushort)opcode.TOP,
                    (ushort)opcode.POP,
                    (ushort)opcode.GENLABEL,
                    (ushort)opcode.RET,
                ]),
            libjq.jv_null());
        Assert.Equal(["{\"__jq\":0}"], labels.Outputs);
    }

    [Fact]
    public void TryAndDestructureAlternativeDistinguishCatchBoundaries()
    {
        var caught = Execute(
            Program(
                [
                    (ushort)opcode.TOP,
                    (ushort)opcode.TRY_BEGIN, 2,
                    (ushort)opcode.ERRORK, 0,
                    (ushort)opcode.RET,
                ],
                [libjq.jv_string("caught")]),
            libjq.jv_null());
        Assert.Equal(["\"caught\""], caught.Outputs);
        Assert.Null(caught.Error);

        var alternative = Execute(
            Program(
                [
                    (ushort)opcode.TOP,
                    (ushort)opcode.DESTRUCTURE_ALT, 2,
                    (ushort)opcode.ERRORK, 0,
                    (ushort)opcode.LOADK, 1,
                    (ushort)opcode.RET,
                ],
                [libjq.jv_string("discarded"), libjq.jv_string("fallback")]),
            libjq.jv_null());
        Assert.Equal(["\"fallback\""], alternative.Outputs);
        Assert.Null(alternative.Error);
    }

    [Fact]
    public void IndexErrorsAndOptionalIndexFollowErrorSlotOwnership()
    {
        var required = Execute(
            Program(
                [
                    (ushort)opcode.TOP,
                    (ushort)opcode.PUSHK_UNDER, 0,
                    (ushort)opcode.INDEX,
                    (ushort)opcode.RET,
                ],
                [libjq.jv_string("a")]),
            libjq.jv_number(1));
        Assert.Empty(required.Outputs);
        Assert.Contains(
            "Cannot index number with string",
            required.Error,
            StringComparison.Ordinal);

        var optional = Execute(
            Program(
                [
                    (ushort)opcode.TOP,
                    (ushort)opcode.PUSHK_UNDER, 0,
                    (ushort)opcode.INDEX_OPT,
                    (ushort)opcode.RET,
                ],
                [libjq.jv_string("a")]),
            libjq.jv_number(1));
        Assert.Empty(optional.Outputs);
        Assert.Null(optional.Error);
    }

    [Fact]
    public void BuiltinCallConsumesArgumentsAndReturnsOrRaises()
    {
        var globals = Globals(
            new cfunction(
                new cfunction_ptr
                {
                    a1 = static (_, input) => input,
                },
                "identity",
                1));
        var result = Execute(
            Program(
                [
                    (ushort)opcode.TOP,
                    (ushort)opcode.CALL_BUILTIN, 1, 0,
                    (ushort)opcode.RET,
                ],
                globals: globals),
            libjq.jv_string("builtin"));
        Assert.Equal(["\"builtin\""], result.Outputs);

        globals = Globals(
            new cfunction(
                new cfunction_ptr
                {
                    a1 = static (_, input) =>
                    {
                        libjq.jv_free(input);
                        return libjq.jv_invalid_with_msg(libjq.jv_string("builtin-error"));
                    },
                },
                "error",
                1));
        var raised = Execute(
            Program(
                [
                    (ushort)opcode.TOP,
                    (ushort)opcode.CALL_BUILTIN, 1, 0,
                    (ushort)opcode.RET,
                ],
                globals: globals),
            libjq.jv_null());
        Assert.Equal("builtin-error", raised.Error);
    }

    [Theory]
    [InlineData((int)opcode.CALL_JQ)]
    [InlineData((int)opcode.TAIL_CALL_JQ)]
    public void FunctionCallsUseClosureFramesAndReturnAddresses(int callOpcodeValue)
    {
        var callOpcode = (opcode)callOpcodeValue;
        var child = Program(
            [(ushort)opcode.LOADK, 0, (ushort)opcode.RET],
            [libjq.jv_string("callee")]);
        var root = Program(
            [
                (ushort)opcode.TOP,
                (ushort)callOpcode, 0, 0, libjq.ARG_NEWCLOSURE,
                (ushort)opcode.RET,
            ]);
        AttachSubfunctions(root, child);

        var result = Execute(root, libjq.jv_null());
        Assert.Equal(["\"callee\""], result.Outputs);
        Assert.Null(result.Error);
    }

    [Fact]
    public void FilterClosureParameterIsCopiedIntoTheCalleeFrame()
    {
        var wrapper = Program(
            [
                (ushort)opcode.CALL_JQ, 0, 0, 0,
                (ushort)opcode.RET,
            ],
            closureCount: 1);
        var producer = Program(
            [(ushort)opcode.LOADK, 0, (ushort)opcode.RET],
            [libjq.jv_string("closure")]);
        var root = Program(
            [
                (ushort)opcode.TOP,
                (ushort)opcode.CALL_JQ, 1,
                0, (ushort)(libjq.ARG_NEWCLOSURE | 0),
                0, (ushort)(libjq.ARG_NEWCLOSURE | 1),
                (ushort)opcode.RET,
            ]);
        AttachSubfunctions(root, wrapper, producer);

        var result = Execute(root, libjq.jv_null());
        Assert.Equal(["\"closure\""], result.Outputs);
        Assert.Null(result.Error);
    }

    [Fact]
    public void ResetReleasesSavedForkAndValueOwners()
    {
        var retained = libjq.jv_array([libjq.jv_number(1)]);
        var program = Program(
            [
                (ushort)opcode.TOP,
                (ushort)opcode.FORK, 2,
                (ushort)opcode.JUMP, 0,
                (ushort)opcode.RET,
            ]);
        jq_state? state = libjq.jq_init();
        try
        {
            libjq.jq_start_bytecode(state, program, libjq.jv_copy(retained), 0);
            var output = libjq.jq_next_bytecode(state);
            Assert.True(output.IsValid);
            libjq.jv_free(output);

            libjq.jq_reset(state);
            Assert.Equal(1, libjq.jv_get_refcnt(retained));
        }
        finally
        {
            libjq.jq_teardown(ref state);
            libjq.bytecode_free(program);
            libjq.jv_free(retained);
        }
    }

    private static VmResult Execute(bytecode program, jv input)
    {
        jq_state? state = libjq.jq_init();
        var outputs = new List<string>();
        string? error = null;
        try
        {
            libjq.jq_start_bytecode(state, program, input, 0);
            input = libjq.jv_invalid();
            for (var count = 0; count < 100; count++)
            {
                var value = libjq.jq_next_bytecode(state);
                if (value.IsValid)
                {
                    outputs.Add(libjq.jv_dump_string_borrowed(value));
                    libjq.jv_free(value);
                    continue;
                }

                if (libjq.jv_invalid_has_msg(libjq.jv_copy(value)))
                {
                    var message = libjq.jv_invalid_get_msg(value);
                    try
                    {
                        error = message.Kind == jv_kind.JV_KIND_STRING
                            ? message.StringValue
                            : libjq.jv_dump_string_borrowed(message);
                    }
                    finally
                    {
                        libjq.jv_free(message);
                    }
                }
                else
                {
                    libjq.jv_free(value);
                }

                return new VmResult(outputs.ToArray(), error);
            }

            throw new InvalidOperationException("Hand-built bytecode did not terminate.");
        }
        finally
        {
            libjq.jv_free(input);
            libjq.jq_teardown(ref state);
            libjq.bytecode_free(program);
        }
    }

    private static bytecode Program(params opcode[] operations) =>
        Program(operations.Select(value => (ushort)value).ToArray());

    private static bytecode Program(
        ushort[] code,
        jv[]? constants = null,
        int localCount = 0,
        int closureCount = 0,
        symbol_table? globals = null)
    {
        var program = new bytecode
        {
            code = code,
            nlocals = localCount,
            nclosures = closureCount,
            globals = globals,
        };
        if (constants is not null)
        {
            libjq.jv_free(program.constants);
            program.constants = libjq.jv_array(constants);
        }

        return program;
    }

    private static symbol_table Globals(params cfunction[] functions)
    {
        var globals = new symbol_table
        {
            cfunctions = functions,
            ncfunctions = functions.Length,
        };
        libjq.jv_free(globals.cfunc_names);
        globals.cfunc_names = libjq.jv_array(
            functions.Select(function => libjq.jv_string(function.name)));
        return globals;
    }

    private static void AttachSubfunctions(bytecode root, params bytecode[] children)
    {
        root.subfunctions = children;
        foreach (var child in children)
        {
            child.parent = root;
            child.globals = root.globals;
        }
    }

    private sealed record VmResult(string[] Outputs, string? Error);
}
