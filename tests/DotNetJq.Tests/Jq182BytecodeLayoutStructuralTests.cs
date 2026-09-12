using DotNetJq.Port;
using System.Globalization;
using System.Reflection;

namespace DotNetJq.Tests;

public sealed class Jq182BytecodeLayoutStructuralTests
{
    [Fact]
    public void OpcodeMacroExpansionMatchesJq182AbiOrderAndMetadata()
    {
        (opcode Op, int Flags, int Length, int StackIn, int StackOut)[] expected =
        [
            (opcode.LOADK, 2, 2, 1, 1),
            (opcode.DUP, 0, 1, 1, 2),
            (opcode.DUPN, 0, 1, 1, 2),
            (opcode.DUP2, 0, 1, 2, 3),
            (opcode.PUSHK_UNDER, 2, 2, 1, 2),
            (opcode.POP, 0, 1, 1, 0),
            (opcode.LOADV, 1028, 3, 1, 1),
            (opcode.LOADVN, 1028, 3, 1, 1),
            (opcode.STOREV, 1028, 3, 1, 0),
            (opcode.STORE_GLOBAL, 1158, 4, 0, 0),
            (opcode.INDEX, 0, 1, 2, 1),
            (opcode.INDEX_OPT, 0, 1, 2, 1),
            (opcode.EACH, 0, 1, 1, 1),
            (opcode.EACH_OPT, 0, 1, 1, 1),
            (opcode.FORK, 8, 2, 0, 0),
            (opcode.TRY_BEGIN, 8, 2, 0, 0),
            (opcode.TRY_END, 0, 1, 0, 0),
            (opcode.JUMP, 8, 2, 0, 0),
            (opcode.JUMP_F, 8, 2, 1, 0),
            (opcode.BACKTRACK, 0, 1, 0, 0),
            (opcode.APPEND, 1028, 3, 1, 0),
            (opcode.INSERT, 0, 1, 4, 2),
            (opcode.RANGE, 1028, 3, 1, 1),
            (opcode.SUBEXP_BEGIN, 0, 1, 1, 2),
            (opcode.SUBEXP_END, 0, 1, 2, 2),
            (opcode.PATH_BEGIN, 0, 1, 1, 2),
            (opcode.PATH_END, 0, 1, 2, 1),
            (opcode.CALL_BUILTIN, 1056, 3, -1, 1),
            (opcode.CALL_JQ, 1216, 4, 1, 1),
            (opcode.RET, 0, 1, 1, 1),
            (opcode.TAIL_CALL_JQ, 1216, 4, 1, 1),
            (opcode.CLOSURE_PARAM, 1152, 0, 0, 0),
            (opcode.CLOSURE_REF, 1152, 2, 0, 0),
            (opcode.CLOSURE_CREATE, 1152, 0, 0, 0),
            (opcode.CLOSURE_CREATE_C, 1152, 0, 0, 0),
            (opcode.TOP, 0, 1, 0, 0),
            (opcode.CLOSURE_PARAM_REGULAR, 1152, 0, 0, 0),
            (opcode.DEPS, 2, 2, 0, 0),
            (opcode.MODULEMETA, 2, 2, 0, 0),
            (opcode.GENLABEL, 0, 1, 0, 1),
            (opcode.DESTRUCTURE_ALT, 8, 2, 0, 0),
            (opcode.STOREVN, 1028, 3, 1, 0),
            (opcode.ERRORK, 2, 2, 1, 0),
        ];

        Assert.Equal(libjq.NUM_OPCODES, expected.Length);
        Assert.Equal(expected.Select(item => item.Op), Enum.GetValues<opcode>());

        foreach (var item in expected)
        {
            var description = libjq.opcode_describe(item.Op);
            Assert.Equal(item.Op, description.op);
            Assert.Equal(item.Op.ToString(), description.name);
            Assert.Equal(item.Flags, description.flags);
            Assert.Equal(item.Length, description.length);
            Assert.Equal(item.StackIn, description.stack_in);
            Assert.Equal(item.StackOut, description.stack_out);
        }
    }

    [Fact]
    public void CallOperationLengthIncludesClosureOperands()
    {
        ushort[] noExtraClosures = [(ushort)opcode.CALL_JQ, 0, 0, 0];
        ushort[] threeExtraClosures = [(ushort)opcode.TAIL_CALL_JQ, 3, 0, 0, 0, 0, 0, 0, 0, 0];

        Assert.Equal(4, libjq.bytecode_operation_length(noExtraClosures));
        Assert.Equal(10, libjq.bytecode_operation_length(threeExtraClosures));
    }

    [Fact]
    public void DisassemblyMatchesUpstreamFormattingAcrossOperandsAndNestedFunctions()
    {
        var globals = new symbol_table
        {
            cfunc_names = libjq.jv_array([libjq.jv_string("length")]),
        };
        var root = new bytecode
        {
            code =
            [
                (ushort)opcode.LOADK, 0,
                (ushort)opcode.STOREV, 0, 0,
                (ushort)opcode.JUMP, 3,
                (ushort)opcode.CALL_BUILTIN, 2, 0,
                (ushort)opcode.CALL_JQ, 1, 0, libjq.ARG_NEWCLOSURE, 0, 0,
                (ushort)opcode.CLOSURE_REF, 7,
            ],
            constants = libjq.jv_array([libjq.jv_number(42)]),
            globals = globals,
            nclosures = 1,
            debuginfo = DebugInfo("root", ["p0"], ["rootLocal"]),
        };
        var child = new bytecode
        {
            code = [(ushort)opcode.LOADV, 1, 0, (ushort)opcode.RET],
            globals = globals,
            parent = root,
            nclosures = 1,
            debuginfo = DebugInfo("callee", ["childParam"], ["childLocal"]),
        };
        var grandchild = new bytecode
        {
            code = [(ushort)opcode.TOP],
            globals = globals,
            parent = child,
            debuginfo = DebugInfo("nested", [], []),
        };
        child.subfunctions = [grandchild];
        root.subfunctions = [child];

        var actual = DumpDisassembly(root, indent: 1);
        var expected = string.Join(
            Environment.NewLine,
            " [params: p0]",
            " 0000 LOADK 42",
            " 0002 STOREV $rootLocal:0",
            " 0005 JUMP 0010",
            " 0007 CALL_BUILTIN length",
            " 0010 CALL_JQ callee:0 p0:0",
            " 0016 CLOSURE_REF 7",
            " callee:0:",
            "   [params: childParam]",
            "   0000 LOADV $rootLocal:0^1",
            "   0003 RET",
            "   nested:0:",
            "     0000 TOP",
            string.Empty);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void BytecodeFreeRecursivelyClearsDescendantsButOnlyRootOwnsGlobals()
    {
        var nonOwningGlobals = PopulatedSymbolTable();
        var owner = OwnedBytecode("owner", nonOwningGlobals);
        var independentlyFreedChild = OwnedBytecode("child", nonOwningGlobals, owner);

        // Upstream bytecode_free() frees the symbol table only when parent is null.
        libjq.bytecode_free(independentlyFreedChild);

        Assert.Single(nonOwningGlobals.cfunctions);
        Assert.True(nonOwningGlobals.cfunc_names.IsValid);
        Assert.Same(nonOwningGlobals, owner.globals);
        AssertBytecodeCleared(independentlyFreedChild);

        // Use a fresh, valid ownership tree for recursive root cleanup; upstream
        // callers likewise never leave an already-freed child in subfunctions.
        var ownedGlobals = PopulatedSymbolTable();
        var root = OwnedBytecode("root", ownedGlobals);
        var child = OwnedBytecode("child", ownedGlobals, root);
        var grandchild = OwnedBytecode("grandchild", ownedGlobals, child);
        child.subfunctions = [grandchild];
        root.subfunctions = [child];
        libjq.bytecode_free(root);

        Assert.Empty(ownedGlobals.cfunctions);
        Assert.False(ownedGlobals.cfunc_names.IsValid);
        AssertBytecodeCleared(root);
        AssertBytecodeCleared(child);
        AssertBytecodeCleared(grandchild);
        libjq.bytecode_free(null);
    }

    [Fact]
    public void ManagedExecStackSupportsDirectedForestAndNonLifoPops()
    {
        var executionStack = new stack();
        libjq.stack_init(executionStack);

        var root = libjq.stack_push_block(executionStack, 0, 3);
        var left = libjq.stack_push_block(executionStack, root, 5);
        var right = libjq.stack_push_block(executionStack, root, 7);
        libjq.stack_block(executionStack, root).Span[0] = 11;
        libjq.stack_block(executionStack, left).Span[0] = 22;
        libjq.stack_block(executionStack, right).Span[0] = 33;

        Assert.Equal(root, libjq.stack_block_next(executionStack, left));
        Assert.Equal(root, libjq.stack_block_next(executionStack, right));
        Assert.False(libjq.stack_pop_will_free(executionStack, left));
        Assert.True(libjq.stack_pop_will_free(executionStack, right));

        // Popping a non-limit block follows its edge but does not reclaim it.
        Assert.Equal(root, libjq.stack_pop_block(executionStack, left, 5));
        Assert.Equal(22, libjq.stack_block(executionStack, left).Span[0]);

        Assert.Equal(root, libjq.stack_pop_block(executionStack, right, 7));
        Assert.Equal(left, executionStack.limit);
        Assert.Equal(root, libjq.stack_pop_block(executionStack, left, 5));
        Assert.Equal(root, executionStack.limit);
        Assert.Equal(11, libjq.stack_block(executionStack, root).Span[0]);
        Assert.Equal((stack_ptr)0, libjq.stack_pop_block(executionStack, root, 3));
        Assert.Equal((stack_ptr)0, executionStack.limit);
    }

    [Fact]
    public void ManagedExecStackKeepsPointersLinksAndPayloadsStableAcrossRepeatedReallocation()
    {
        var executionStack = new stack();
        libjq.stack_init(executionStack);
        var blocks = new List<(stack_ptr Pointer, stack_ptr Next, int Size, byte First, byte Last)>();
        byte[]? previousAllocation = null;
        var reallocations = 0;
        stack_ptr previous = 0;

        for (var index = 0; index < 256; index++)
        {
            var size = 257 + (index % 31);
            var pointer = libjq.stack_push_block(executionStack, previous, size);
            if (!ReferenceEquals(previousAllocation, executionStack.mem_end))
            {
                reallocations++;
                previousAllocation = executionStack.mem_end;
            }

            var first = (byte)index;
            var last = (byte)(255 - index);
            var payload = libjq.stack_block(executionStack, pointer).Span;
            payload[0] = first;
            payload[^1] = last;
            blocks.Add((pointer, previous, size, first, last));
            previous = pointer;
        }

        Assert.True(reallocations >= 5, $"Expected repeated growth, observed {reallocations} allocations.");
        foreach (var block in blocks)
        {
            Assert.True((int)block.Pointer < 0);
            Assert.Equal(0, (int)block.Pointer % libjq.ALIGNMENT);
            Assert.Equal(block.Next, libjq.stack_block_next(executionStack, block.Pointer));
            var payload = libjq.stack_block(executionStack, block.Pointer);
            Assert.Equal(libjq.align_round_up(block.Size), payload.Length);
            Assert.Equal(block.First, payload.Span[0]);
            Assert.Equal(block.Last, payload.Span[^1]);
        }

        for (var index = blocks.Count - 1; index >= 0; index--)
        {
            var block = blocks[index];
            Assert.True(libjq.stack_pop_will_free(executionStack, block.Pointer));
            Assert.Equal(block.Next, libjq.stack_pop_block(executionStack, block.Pointer, block.Size));
        }

        Assert.Equal((stack_ptr)0, executionStack.limit);
    }

    [Fact]
    public void ManagedExecStackPreservesAlignmentAndResetInvariants()
    {
        Assert.True(libjq.ALIGNMENT > 0);
        Assert.Equal(0, libjq.align_round_up(0));
        Assert.Equal(libjq.ALIGNMENT, libjq.align_round_up(1));
        Assert.Equal(libjq.ALIGNMENT, libjq.align_round_up(libjq.ALIGNMENT));
        Assert.Equal(libjq.ALIGNMENT * 2, libjq.align_round_up(libjq.ALIGNMENT + 1));

        var executionStack = new stack();
        libjq.stack_init(executionStack);
        int[] sizes = [0, 1, libjq.ALIGNMENT - 1, libjq.ALIGNMENT, libjq.ALIGNMENT + 1];
        var pointers = new List<stack_ptr>();
        stack_ptr previous = 0;
        foreach (var size in sizes)
        {
            var pointer = libjq.stack_push_block(executionStack, previous, size);
            Assert.Equal(libjq.align_round_up(size) + libjq.ALIGNMENT, (int)previous - (int)pointer);
            Assert.Equal(0, (int)pointer % libjq.ALIGNMENT);
            Assert.Equal(libjq.align_round_up(size), libjq.stack_block(executionStack, pointer).Length);
            pointers.Add(pointer);
            previous = pointer;
        }

        var allocatedRegion = executionStack.mem_end;
        var occupiedLimit = executionStack.limit;
        Assert.Throws<InvalidOperationException>(() => libjq.stack_reset(executionStack));
        Assert.Same(allocatedRegion, executionStack.mem_end);
        Assert.Equal(occupiedLimit, executionStack.limit);

        for (var index = pointers.Count - 1; index >= 0; index--)
        {
            previous = libjq.stack_pop_block(executionStack, pointers[index], sizes[index]);
        }

        Assert.Equal((stack_ptr)0, previous);

        libjq.stack_reset(executionStack);
        Assert.Null(executionStack.mem_end);
        Assert.Equal((stack_ptr)libjq.ALIGNMENT, executionStack.bound);
        Assert.Equal((stack_ptr)0, executionStack.limit);
        Assert.Empty(executionStack.block_next);
        Assert.Empty(executionStack.block_size);
    }

    private static string DumpDisassembly(bytecode bytecode, int indent)
    {
        var overload = typeof(libjq).GetMethod(
            "dump_disassembly",
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            [typeof(TextWriter), typeof(int), typeof(bytecode)],
            modifiers: null);
        Assert.NotNull(overload);

        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        overload.Invoke(null, [writer, indent, bytecode]);
        return writer.ToString();
    }

    private static jv DebugInfo(string name, string[] parameters, string[] locals)
    {
        var result = libjq.jv_object_set(libjq.jv_object(), "name", libjq.jv_string(name));
        result = libjq.jv_object_set(
            result,
            "params",
            libjq.jv_array(parameters.Select(libjq.jv_string)));
        return libjq.jv_object_set(
            result,
            "locals",
            libjq.jv_array(locals.Select(libjq.jv_string)));
    }

    private static bytecode OwnedBytecode(string name, symbol_table globals, bytecode? parent = null) =>
        new()
        {
            code = [(ushort)opcode.LOADK, 0],
            constants = libjq.jv_array([libjq.jv_string(name)]),
            globals = globals,
            parent = parent,
            debuginfo = DebugInfo(name, [], []),
        };

    private static symbol_table PopulatedSymbolTable() =>
        new()
        {
            cfunctions =
            [
                new cfunction(
                    new cfunction_ptr { a1 = static (_, input) => input },
                    "identity",
                    1),
            ],
            cfunc_names = libjq.jv_array([libjq.jv_string("identity")]),
        };

    private static void AssertBytecodeCleared(bytecode bytecode)
    {
        Assert.Empty(bytecode.code);
        Assert.False(bytecode.constants.IsValid);
        Assert.Empty(bytecode.subfunctions);
        Assert.Null(bytecode.globals);
        Assert.False(bytecode.debuginfo.IsValid);
        Assert.Null(bytecode.parent);
    }
}
