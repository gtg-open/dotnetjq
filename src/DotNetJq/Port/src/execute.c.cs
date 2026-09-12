// DOTNETJQ PORT MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream file: src/execute.c
// Upstream URL: https://github.com/jqlang/jq/blob/jq-1.8.2/src/execute.c
// Strategy: PORT
// Target file: src/DotNetJq/Port/src/execute.c.cs
//
// Direct port of execute.c's bytecode VM plus args2obj/jq_compile_args/jq_compile.
// Production parser/compiler execution enters this dispatcher with compiler-produced
// bytecode; no alternate evaluator is retained.

#pragma warning disable CS8981

using System.Diagnostics;
using System.Globalization;
using System.Text;
using DotNetJq.Compatibility.FileSystem;

namespace DotNetJq.Port;

internal sealed partial class jq_state
{
    private jq_bytecode_vm? bytecodeVm;
    private uint bytecodeNextLabel;

    internal void StartBytecode(
        bytecode program,
        jv input,
        int flags,
        JqInputPosition? inputPosition = null,
        int inputBytes = 0)
    {
        ArgumentNullException.ThrowIfNull(program);
        ResetBytecodeExecution();
        ResetExecution();
        ArgumentOutOfRangeException.ThrowIfNegative(inputBytes);
        ValidatePosition(inputPosition);
        currentInputPosition = inputPosition;
        consumedInputBytes = inputBytes;
        debugTraceFlags = flags & libjq.JQ_DEBUG_TRACE_ALL;
        if (program.has_environment_constant_slots)
        {
            libjq.bytecode_restore_compiled_environment(program);
            if (Options.HasExplicitEnvironment)
            {
                libjq.bytecode_replace_environment(program, libjq.jq_environment(Options));
            }
        }

        var vm = new jq_bytecode_vm(this, program);
        bytecodeVm = vm;
        try
        {
            vm.Start(input, flags);
        }
        catch
        {
            ResetBytecodeExecution();
            throw;
        }
    }

    internal jv NextBytecode() => bytecodeVm?.Next() ?? libjq.jv_invalid();

    internal void ResetBytecodeExecution()
    {
        bytecodeVm?.Dispose();
        bytecodeVm = null;
    }

    internal uint AllocateBytecodeLabel() => bytecodeNextLabel++;

    internal jv AppendBytecodePath(jv value, jv path, jv valueAtPath) =>
        bytecodeVm is null
            ? ReleasePathArguments(value, path, valueAtPath)
            : bytecodeVm.AppendPath(value, path, valueAtPath);

    private static jv ReleasePathArguments(jv value, jv path, jv result)
    {
        libjq.jv_free(value);
        libjq.jv_free(path);
        return result;
    }
}

internal readonly record struct closure(bytecode bc, stack_ptr env);

internal sealed class frame_entry
{
    internal closure? closure;

    internal jv localvar = libjq.jv_invalid();
}

internal sealed class frame
{
    internal required bytecode bc;

    internal stack_ptr env;

    internal stack_ptr retdata;

    internal int? retaddr;

    internal long logical_depth;

    internal required frame_entry[] entries;
}

internal sealed class vm_value_slot(jv value)
{
    internal jv value = value;
}

internal sealed class forkpoint
{
    internal stack_ptr saved_data_stack;

    internal stack_ptr saved_curr_frame;

    internal int path_len;

    internal int subexp_nest;

    internal jv value_at_path;

    internal int return_address;
}

internal readonly record struct stack_pos(
    stack_ptr saved_data_stack,
    stack_ptr saved_curr_frame);

internal sealed class jq_bytecode_vm : IDisposable
{
    // Managed stack blocks retain exec_stack.h's negative-offset and physical-limit
    // algorithm.  Payload byte sizes are opaque to the CLR; these stable logical sizes
    // are paired identically at every push/pop boundary.
    private const int ValueBlockSize = 1;
    private const int ForkpointBlockSize = 1;

    private readonly jq_state jq;
    private readonly bytecode root;
    private readonly stack stk = new();

    private stack_ptr curr_frame;
    private stack_ptr stk_top;
    private stack_ptr fork_top;
    private jv error = libjq.jv_null();
    private jv path = libjq.jv_null();
    private jv value_at_path = libjq.jv_null();
    private int subexp_nest;
    private int debug_trace_enabled;
    private bool initial_execution;
    private bool has_execution_policy;
    private Stopwatch? execution_stopwatch;
    private long dispatch_transitions;
    private bool disposed;

    internal jq_bytecode_vm(jq_state jq, bytecode root)
    {
        this.jq = jq;
        this.root = root;
        libjq.stack_init(stk);
    }

    internal void Start(jv input, int flags)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Reset();

        var top = new closure(root, -1);
        var topFrame = frame_push(top, argdef: 0, nargs: 0);
        topFrame.retdata = 0;
        topFrame.retaddr = null;

        stack_push(input);
        stack_save(0, stack_get_pos());
        debug_trace_enabled = flags & libjq.JQ_DEBUG_TRACE_ALL;
        initial_execution = true;
        has_execution_policy =
            jq.Options.CancellationToken.CanBeCanceled ||
            jq.Options.Timeout.HasValue ||
            jq.Options.MaxExecutionTransitions != long.MaxValue ||
            jq.Options.HasExplicitMaxRecursionDepth;
        execution_stopwatch = jq.Options.Timeout.HasValue ? Stopwatch.StartNew() : null;
        dispatch_transitions = 0;
    }

    internal jv Next()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return ExecuteNext();
    }

    internal jv AppendPath(jv value, jv component, jv nextValueAtPath)
    {
        if (subexp_nest != 0 ||
            libjq.jv_get_kind(path) != jv_kind.JV_KIND_ARRAY ||
            !libjq.jv_is_valid(nextValueAtPath))
        {
            libjq.jv_free(value);
            libjq.jv_free(component);
            return nextValueAtPath;
        }

        if (!libjq.jv_identical(value, libjq.jv_copy(value_at_path)))
        {
            libjq.jv_free(component);
            return nextValueAtPath;
        }

        path = libjq.jv_get_kind(component) == jv_kind.JV_KIND_ARRAY
            ? libjq.jv_array_concat(path, component)
            : libjq.jv_array_append(path, component);
        libjq.jv_free(value_at_path);
        value_at_path = nextValueAtPath;
        return libjq.jv_copy(value_at_path);
    }

    private static int frame_size(bytecode bc) =>
        checked(1 + bc.nclosures + bc.nlocals);

    private frame frame_current()
    {
        if (curr_frame == (stack_ptr)0)
        {
            throw new InvalidOperationException("The jq VM has no current frame.");
        }

        return libjq.stack_get_block<frame>(stk, curr_frame);
    }

    private stack_ptr frame_get_level(int level)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(level);
        var frameAddress = curr_frame;
        for (var index = 0; index < level; index++)
        {
            frameAddress = libjq.stack_get_block<frame>(stk, frameAddress).env;
            if (frameAddress == (stack_ptr)0 || frameAddress == (stack_ptr)(-1))
            {
                throw new InvalidOperationException("Bytecode closure level exceeds the frame chain.");
            }
        }

        return frameAddress;
    }

    private frame_entry frame_local_var(int variable, int level)
    {
        var frame = libjq.stack_get_block<frame>(stk, frame_get_level(level));
        if ((uint)variable >= (uint)frame.bc.nlocals)
        {
            throw new InvalidOperationException("Bytecode local-variable index is out of range.");
        }

        return frame.entries[frame.bc.nclosures + variable];
    }

    private closure make_closure(int codeAddress)
    {
        var caller = frame_current().bc;
        var level = ReadWord(caller, codeAddress);
        var closureIndex = ReadWord(caller, codeAddress + 1);
        var frameAddress = frame_get_level(level);
        var frame = libjq.stack_get_block<frame>(stk, frameAddress);
        if ((closureIndex & libjq.ARG_NEWCLOSURE) != 0)
        {
            var subfunction = closureIndex & ~libjq.ARG_NEWCLOSURE;
            if ((uint)subfunction >= (uint)frame.bc.nsubfunctions)
            {
                throw new InvalidOperationException("Bytecode subfunction index is out of range.");
            }

            return new closure(frame.bc.subfunctions[subfunction], frameAddress);
        }

        if ((uint)closureIndex >= (uint)frame.bc.nclosures)
        {
            throw new InvalidOperationException("Bytecode closure index is out of range.");
        }

        return frame.entries[closureIndex].closure ??
            throw new InvalidOperationException("Bytecode closure slot is not initialized.");
    }

    private frame frame_push(closure callee, int argdef, int nargs)
    {
        if (nargs != callee.bc.nclosures)
        {
            throw new InvalidOperationException("Bytecode call closure count does not match its callee.");
        }

        var entries = Enumerable.Range(0, callee.bc.nclosures + callee.bc.nlocals)
            .Select(_ => new frame_entry())
            .ToArray();
        for (var index = 0; index < nargs; index++)
        {
            entries[index].closure = make_closure(checked(argdef + index * 2));
        }

        var frame = new frame
        {
            bc = callee.bc,
            env = callee.env,
            entries = entries,
        };
        var newFrameAddress = libjq.stack_push_block(stk, curr_frame, frame_size(callee.bc));
        libjq.stack_set_block(stk, newFrameAddress, frame);
        curr_frame = newFrameAddress;
        return frame;
    }

    private void frame_pop()
    {
        var frameAddress = curr_frame;
        var frame = frame_current();
        if (libjq.stack_pop_will_free(stk, frameAddress))
        {
            for (var index = 0; index < frame.bc.nlocals; index++)
            {
                libjq.jv_free(frame.entries[frame.bc.nclosures + index].localvar);
                frame.entries[frame.bc.nclosures + index].localvar = libjq.jv_invalid();
            }
        }

        curr_frame = libjq.stack_pop_block(stk, frameAddress, frame_size(frame.bc));
    }

    private void stack_push(jv value)
    {
        if (!libjq.jv_is_valid(value))
        {
            throw new InvalidOperationException("The jq VM cannot push an invalid value.");
        }

        stk_top = libjq.stack_push_block(stk, stk_top, ValueBlockSize);
        libjq.stack_set_block(stk, stk_top, new vm_value_slot(value));
    }

    private jv stack_pop()
    {
        if (stk_top == (stack_ptr)0)
        {
            throw new InvalidOperationException("The jq VM data stack is empty.");
        }

        var slotAddress = stk_top;
        var slot = libjq.stack_get_block<vm_value_slot>(stk, slotAddress);
        var value = slot.value;
        if (!libjq.stack_pop_will_free(stk, slotAddress))
        {
            value = libjq.jv_copy(value);
        }

        stk_top = libjq.stack_pop_block(stk, slotAddress, ValueBlockSize);
        if (!libjq.jv_is_valid(value))
        {
            throw new InvalidOperationException("The jq VM popped an invalid value.");
        }

        return value;
    }

    private jv stack_popn()
    {
        if (stk_top == (stack_ptr)0)
        {
            throw new InvalidOperationException("The jq VM data stack is empty.");
        }

        var slotAddress = stk_top;
        var slot = libjq.stack_get_block<vm_value_slot>(stk, slotAddress);
        var value = slot.value;
        if (!libjq.stack_pop_will_free(stk, slotAddress))
        {
            slot.value = libjq.jv_null();
        }

        stk_top = libjq.stack_pop_block(stk, slotAddress, ValueBlockSize);
        if (!libjq.jv_is_valid(value))
        {
            throw new InvalidOperationException("The jq VM popped an invalid value.");
        }

        return value;
    }

    private stack_pos stack_get_pos() => new(stk_top, curr_frame);

    private void stack_save(int returnAddress, stack_pos position)
    {
        fork_top = libjq.stack_push_block(stk, fork_top, ForkpointBlockSize);
        var forkpoint = new forkpoint
        {
            saved_data_stack = stk_top,
            saved_curr_frame = curr_frame,
            path_len = libjq.jv_get_kind(path) == jv_kind.JV_KIND_ARRAY
                ? libjq.jv_array_length(libjq.jv_copy(path))
                : 0,
            value_at_path = libjq.jv_copy(value_at_path),
            subexp_nest = subexp_nest,
            return_address = returnAddress,
        };
        libjq.stack_set_block(stk, fork_top, forkpoint);
        stk_top = position.saved_data_stack;
        curr_frame = position.saved_curr_frame;
    }

    private int? stack_restore()
    {
        while (!libjq.stack_pop_will_free(stk, fork_top))
        {
            if (stk_top != (stack_ptr)0 && libjq.stack_pop_will_free(stk, stk_top))
            {
                libjq.jv_free(stack_pop());
            }
            else if (curr_frame != (stack_ptr)0 &&
                     libjq.stack_pop_will_free(stk, curr_frame))
            {
                frame_pop();
            }
            else
            {
                throw new InvalidOperationException("The jq VM stack cannot be restored in allocation order.");
            }
        }

        if (fork_top == (stack_ptr)0)
        {
            return null;
        }

        var forkAddress = fork_top;
        var forkpoint = libjq.stack_get_block<forkpoint>(stk, forkAddress);
        stk_top = forkpoint.saved_data_stack;
        curr_frame = forkpoint.saved_curr_frame;
        if (libjq.jv_get_kind(path) == jv_kind.JV_KIND_ARRAY)
        {
            path = libjq.jv_array_slice(path, 0, forkpoint.path_len);
        }

        libjq.jv_free(value_at_path);
        value_at_path = forkpoint.value_at_path;
        forkpoint.value_at_path = libjq.jv_invalid();
        subexp_nest = forkpoint.subexp_nest;
        fork_top = libjq.stack_pop_block(stk, forkAddress, ForkpointBlockSize);
        return forkpoint.return_address;
    }

    private bool path_intact(jv current)
    {
        if (subexp_nest == 0 && libjq.jv_get_kind(path) == jv_kind.JV_KIND_ARRAY)
        {
            return libjq.jv_identical(current, libjq.jv_copy(value_at_path));
        }

        libjq.jv_free(current);
        return true;
    }

    private void path_append(jv component, jv nextValueAtPath)
    {
        if (subexp_nest == 0 && libjq.jv_get_kind(path) == jv_kind.JV_KIND_ARRAY)
        {
            var oldLength = libjq.jv_array_length(libjq.jv_copy(path));
            path = libjq.jv_array_append(path, component);
            var newLength = libjq.jv_array_length(libjq.jv_copy(path));
            if (newLength != oldLength + 1)
            {
                throw new InvalidOperationException("The jq VM path append changed length incorrectly.");
            }

            libjq.jv_free(value_at_path);
            value_at_path = nextValueAtPath;
        }
        else
        {
            libjq.jv_free(component);
            libjq.jv_free(nextValueAtPath);
        }
    }

    private void set_error(jv value)
    {
        libjq.jv_free(error);
        error = value;
    }

    private static ushort ReadWord(bytecode bc, int address)
    {
        if ((uint)address >= (uint)bc.codelen)
        {
            throw new InvalidOperationException("Bytecode program counter is out of range.");
        }

        return bc.code[address];
    }

    private ushort ReadWord(ref int address)
    {
        var result = ReadWord(frame_current().bc, address);
        address++;
        return result;
    }

    private jv GetConstant(int index)
    {
        var value = libjq.jv_array_get(
            libjq.jv_copy(frame_current().bc.constants),
            index);
        if (!libjq.jv_is_valid(value))
        {
            throw new InvalidOperationException("Bytecode constant index is out of range.");
        }

        return value;
    }

    private jv ExecuteNext()
    {
        var restoredAddress = stack_restore();
        if (!restoredAddress.HasValue)
        {
            return libjq.jv_invalid();
        }

        var pc = restoredAddress.Value;
        var backtracking = !initial_execution;
        initial_execution = false;
        if (libjq.jv_get_kind(error) != jv_kind.JV_KIND_NULL)
        {
            throw new InvalidOperationException("The jq VM error slot was not clear at jq_next entry.");
        }

        while (true)
        {
            if (jq.Halted)
            {
                if (debug_trace_enabled != 0)
                {
                    jq.EmitDebugTrace("\t<halted>\n");
                }

                return libjq.jv_invalid();
            }

            if (has_execution_policy)
            {
                TickExecutionPolicy();
            }

            var operationStart = pc;
            var operation = ReadWord(frame_current().bc, pc);
            if (operation >= libjq.NUM_OPCODES)
            {
                throw new InvalidOperationException("Bytecode contains an invalid instruction.");
            }

            if (debug_trace_enabled != 0)
            {
                EmitTrace(operationStart, (opcode)operation, backtracking);
            }

            var raising = false;
            var instruction = (int)operation;
            if (backtracking)
            {
                instruction += libjq.NUM_OPCODES;
                backtracking = false;
                raising = !libjq.jv_is_valid(error);
            }

            pc++;
            switch (instruction)
            {
                case (int)opcode.TOP:
                    break;

                case (int)opcode.ERRORK:
                {
                    var value = GetConstant(ReadWord(ref pc));
                    set_error(libjq.jv_invalid_with_msg(value));
                    goto DoBacktrack;
                }

                case (int)opcode.LOADK:
                {
                    var value = GetConstant(ReadWord(ref pc));
                    libjq.jv_free(stack_pop());
                    stack_push(value);
                    break;
                }

                case (int)opcode.GENLABEL:
                {
                    var label = libjq.jv_object();
                    label = libjq.jv_object_set(
                        label,
                        libjq.jv_string("__jq"),
                        libjq.jv_number(jq.AllocateBytecodeLabel()));
                    stack_push(label);
                    break;
                }

                case (int)opcode.DUP:
                {
                    var value = stack_pop();
                    stack_push(libjq.jv_copy(value));
                    stack_push(value);
                    break;
                }

                case (int)opcode.DUPN:
                {
                    var value = stack_popn();
                    stack_push(libjq.jv_copy(value));
                    stack_push(value);
                    break;
                }

                case (int)opcode.DUP2:
                {
                    var keep = stack_pop();
                    var value = stack_pop();
                    stack_push(libjq.jv_copy(value));
                    stack_push(keep);
                    stack_push(value);
                    break;
                }

                case (int)opcode.SUBEXP_BEGIN:
                {
                    var value = stack_pop();
                    stack_push(libjq.jv_copy(value));
                    stack_push(value);
                    subexp_nest++;
                    break;
                }

                case (int)opcode.SUBEXP_END:
                {
                    if (subexp_nest <= 0)
                    {
                        throw new InvalidOperationException("SUBEXP_END has no matching SUBEXP_BEGIN.");
                    }

                    subexp_nest--;
                    var first = stack_pop();
                    var second = stack_pop();
                    stack_push(first);
                    stack_push(second);
                    break;
                }

                case (int)opcode.PUSHK_UNDER:
                {
                    var value = GetConstant(ReadWord(ref pc));
                    var upper = stack_pop();
                    stack_push(value);
                    stack_push(upper);
                    break;
                }

                case (int)opcode.POP:
                    libjq.jv_free(stack_pop());
                    break;

                case (int)opcode.APPEND:
                {
                    var value = stack_pop();
                    var level = ReadWord(ref pc);
                    var variableIndex = ReadWord(ref pc);
                    var variable = frame_local_var(variableIndex, level);
                    if (libjq.jv_get_kind(variable.localvar) != jv_kind.JV_KIND_ARRAY)
                    {
                        libjq.jv_free(value);
                        throw new InvalidOperationException("APPEND target is not an array local.");
                    }

                    variable.localvar = libjq.jv_array_append(variable.localvar, value);
                    break;
                }

                case (int)opcode.INSERT:
                {
                    var stackTop = stack_pop();
                    var value = stack_pop();
                    var key = stack_pop();
                    var objectValue = stack_pop();
                    if (libjq.jv_get_kind(objectValue) != jv_kind.JV_KIND_OBJECT)
                    {
                        libjq.jv_free(stackTop);
                        libjq.jv_free(value);
                        libjq.jv_free(key);
                        libjq.jv_free(objectValue);
                        throw new InvalidOperationException("INSERT target is not an object.");
                    }

                    if (libjq.jv_get_kind(key) == jv_kind.JV_KIND_STRING)
                    {
                        stack_push(libjq.jv_object_set(objectValue, key, value));
                        stack_push(stackTop);
                    }
                    else
                    {
                        var keyText = DumpTruncCopied(key, 30);
                        set_error(libjq.jv_invalid_with_msg(libjq.jv_string(
                            "Cannot use " + libjq.jv_kind_name(libjq.jv_get_kind(key)) +
                            " (" + keyText + ") as object key")));
                        libjq.jv_free(stackTop);
                        libjq.jv_free(value);
                        libjq.jv_free(key);
                        libjq.jv_free(objectValue);
                        goto DoBacktrack;
                    }

                    break;
                }

                case (int)opcode.RANGE:
                case (int)opcode.RANGE + libjq.NUM_OPCODES:
                {
                    var level = ReadWord(ref pc);
                    var variableIndex = ReadWord(ref pc);
                    var variable = frame_local_var(variableIndex, level);
                    var maximum = stack_pop();
                    if (raising)
                    {
                        libjq.jv_free(maximum);
                        goto DoBacktrack;
                    }

                    if (libjq.jv_get_kind(variable.localvar) != jv_kind.JV_KIND_NUMBER ||
                        libjq.jv_get_kind(maximum) != jv_kind.JV_KIND_NUMBER)
                    {
                        set_error(libjq.jv_invalid_with_msg(libjq.jv_string(
                            "Range bounds must be numeric")));
                        libjq.jv_free(maximum);
                        goto DoBacktrack;
                    }

                    if (libjq.jv_number_value(variable.localvar) >=
                        libjq.jv_number_value(maximum))
                    {
                        libjq.jv_free(maximum);
                        goto DoBacktrack;
                    }

                    var current = variable.localvar;
                    variable.localvar = libjq.jv_number(
                        libjq.jv_number_value(variable.localvar) + 1);
                    var rangePosition = stack_get_pos();
                    stack_push(maximum);
                    stack_save(operationStart, rangePosition);
                    stack_push(current);
                    break;
                }

                case (int)opcode.LOADV:
                {
                    var level = ReadWord(ref pc);
                    var variableIndex = ReadWord(ref pc);
                    var variable = frame_local_var(variableIndex, level);
                    libjq.jv_free(stack_pop());
                    stack_push(libjq.jv_copy(variable.localvar));
                    break;
                }

                case (int)opcode.LOADVN:
                {
                    var level = ReadWord(ref pc);
                    var variableIndex = ReadWord(ref pc);
                    var variable = frame_local_var(variableIndex, level);
                    libjq.jv_free(stack_popn());
                    stack_push(variable.localvar);
                    variable = frame_local_var(variableIndex, level);
                    variable.localvar = libjq.jv_null();
                    break;
                }

                case (int)opcode.STOREVN:
                    stack_save(operationStart, stack_get_pos());
                    goto case (int)opcode.STOREV;

                case (int)opcode.STOREV:
                {
                    var level = ReadWord(ref pc);
                    var variableIndex = ReadWord(ref pc);
                    var variable = frame_local_var(variableIndex, level);
                    var value = stack_pop();
                    libjq.jv_free(variable.localvar);
                    variable.localvar = value;
                    break;
                }

                case (int)opcode.STOREVN + libjq.NUM_OPCODES:
                {
                    var level = ReadWord(ref pc);
                    var variableIndex = ReadWord(ref pc);
                    var variable = frame_local_var(variableIndex, level);
                    libjq.jv_free(variable.localvar);
                    variable.localvar = libjq.jv_null();
                    goto DoBacktrack;
                }

                case (int)opcode.STORE_GLOBAL:
                {
                    var value = GetConstant(ReadWord(ref pc));
                    var level = ReadWord(ref pc);
                    var variableIndex = ReadWord(ref pc);
                    var variable = frame_local_var(variableIndex, level);
                    libjq.jv_free(variable.localvar);
                    variable.localvar = value;
                    break;
                }

                case (int)opcode.PATH_BEGIN:
                {
                    var value = stack_pop();
                    stack_push(path);
                    stack_save(operationStart, stack_get_pos());
                    stack_push(libjq.jv_number(subexp_nest));
                    stack_push(value_at_path);
                    stack_push(libjq.jv_copy(value));
                    path = libjq.jv_array();
                    value_at_path = value;
                    subexp_nest = 0;
                    break;
                }

                case (int)opcode.PATH_END:
                {
                    var value = stack_pop();
                    if (!path_intact(libjq.jv_copy(value)))
                    {
                        var resultText = DumpTruncConsumed(value, 30);
                        set_error(libjq.jv_invalid_with_msg(libjq.jv_string(
                            "Invalid path expression with result " + resultText)));
                        goto DoBacktrack;
                    }

                    libjq.jv_free(value);
                    var oldValueAtPath = stack_pop();
                    var oldSubexpressionNest = checked((int)libjq.jv_number_value(stack_pop()));
                    var resultPath = path;
                    path = stack_pop();
                    var pathPosition = stack_get_pos();
                    stack_push(libjq.jv_copy(resultPath));
                    stack_save(operationStart, pathPosition);
                    stack_push(resultPath);
                    subexp_nest = oldSubexpressionNest;
                    libjq.jv_free(value_at_path);
                    value_at_path = oldValueAtPath;
                    break;
                }

                case (int)opcode.PATH_BEGIN + libjq.NUM_OPCODES:
                case (int)opcode.PATH_END + libjq.NUM_OPCODES:
                    libjq.jv_free(path);
                    path = stack_pop();
                    goto DoBacktrack;

                case (int)opcode.INDEX:
                case (int)opcode.INDEX_OPT:
                {
                    var target = stack_pop();
                    var key = stack_pop();
                    if (!path_intact(libjq.jv_copy(target)))
                    {
                        var keyText = DumpTruncConsumed(key, 30);
                        var targetText = DumpTruncConsumed(target, 30);
                        set_error(libjq.jv_invalid_with_msg(libjq.jv_string(
                            "Invalid path expression near attempt to access element " +
                            keyText + " of " + targetText)));
                        goto DoBacktrack;
                    }

                    var value = libjq.jv_get(target, libjq.jv_copy(key));
                    if (libjq.jv_is_valid(value))
                    {
                        path_append(key, libjq.jv_copy(value));
                        stack_push(value);
                    }
                    else
                    {
                        libjq.jv_free(key);
                        if (instruction == (int)opcode.INDEX)
                        {
                            set_error(value);
                        }
                        else
                        {
                            libjq.jv_free(value);
                        }

                        goto DoBacktrack;
                    }

                    break;
                }

                case (int)opcode.JUMP:
                {
                    var offset = ReadWord(ref pc);
                    pc = checked(pc + offset);
                    break;
                }

                case (int)opcode.JUMP_F:
                {
                    var offset = ReadWord(ref pc);
                    var test = stack_pop();
                    var kind = libjq.jv_get_kind(test);
                    if (kind is jv_kind.JV_KIND_FALSE or jv_kind.JV_KIND_NULL)
                    {
                        pc = checked(pc + offset);
                    }

                    stack_push(test);
                    break;
                }

                case (int)opcode.EACH:
                case (int)opcode.EACH_OPT:
                case (int)opcode.EACH + libjq.NUM_OPCODES:
                case (int)opcode.EACH_OPT + libjq.NUM_OPCODES:
                {
                    var initialEach = instruction is (int)opcode.EACH or (int)opcode.EACH_OPT;
                    if (initialEach)
                    {
                        var initialContainer = stack_pop();
                        if (!path_intact(libjq.jv_copy(initialContainer)))
                        {
                            var containerText = DumpTruncConsumed(initialContainer, 30);
                            set_error(libjq.jv_invalid_with_msg(libjq.jv_string(
                                "Invalid path expression near attempt to iterate through " +
                                containerText)));
                            goto DoBacktrack;
                        }

                        stack_push(initialContainer);
                        stack_push(libjq.jv_number(-1));
                    }

                    var index = checked((int)libjq.jv_number_value(stack_pop()));
                    var container = stack_pop();
                    var keepGoing = false;
                    var isLast = false;
                    var key = libjq.jv_invalid();
                    var value = libjq.jv_invalid();
                    if (libjq.jv_get_kind(container) == jv_kind.JV_KIND_ARRAY)
                    {
                        index = initialEach ? 0 : checked(index + 1);
                        var length = libjq.jv_array_length(libjq.jv_copy(container));
                        keepGoing = index < length;
                        isLast = index == length - 1;
                        if (keepGoing)
                        {
                            key = libjq.jv_number(index);
                            value = libjq.jv_array_get(libjq.jv_copy(container), index);
                        }
                    }
                    else if (libjq.jv_get_kind(container) == jv_kind.JV_KIND_OBJECT)
                    {
                        index = initialEach
                            ? libjq.jv_object_iter(container)
                            : libjq.jv_object_iter_next(container, index);
                        keepGoing = libjq.jv_object_iter_valid(container, index);
                        if (keepGoing)
                        {
                            key = libjq.jv_object_iter_key(container, index);
                            value = libjq.jv_object_iter_value(container, index);
                        }
                    }
                    else
                    {
                        if (!initialEach)
                        {
                            libjq.jv_free(container);
                            throw new InvalidOperationException(
                                "Backtracking EACH continuation has a non-container value.");
                        }

                        if (instruction == (int)opcode.EACH)
                        {
                            var containerText = DumpTruncCopied(container, 30);
                            set_error(libjq.jv_invalid_with_msg(libjq.jv_string(
                                "Cannot iterate over " +
                                libjq.jv_kind_name(libjq.jv_get_kind(container)) +
                                " (" + containerText + ")")));
                        }
                    }

                    if (!keepGoing || raising)
                    {
                        if (keepGoing)
                        {
                            libjq.jv_free(key);
                            libjq.jv_free(value);
                        }

                        libjq.jv_free(container);
                        goto DoBacktrack;
                    }

                    if (isLast)
                    {
                        libjq.jv_free(container);
                        path_append(key, libjq.jv_copy(value));
                        stack_push(value);
                    }
                    else
                    {
                        var eachPosition = stack_get_pos();
                        stack_push(container);
                        stack_push(libjq.jv_number(index));
                        stack_save(operationStart, eachPosition);
                        path_append(key, libjq.jv_copy(value));
                        stack_push(value);
                    }

                    break;
                }

                case (int)opcode.BACKTRACK:
                    goto DoBacktrack;

                case (int)opcode.TRY_BEGIN:
                    stack_save(operationStart, stack_get_pos());
                    pc++;
                    break;

                case (int)opcode.TRY_END:
                    stack_save(operationStart, stack_get_pos());
                    break;

                case (int)opcode.TRY_BEGIN + libjq.NUM_OPCODES:
                {
                    if (!raising)
                    {
                        libjq.jv_free(stack_pop());
                        goto DoBacktrack;
                    }

                    var nestedError = libjq.jv_invalid_get_msg(libjq.jv_copy(error));
                    if (!libjq.jv_is_valid(nestedError) &&
                        libjq.jv_invalid_has_msg(libjq.jv_copy(nestedError)))
                    {
                        set_error(nestedError);
                        goto DoBacktrack;
                    }

                    libjq.jv_free(nestedError);
                    var handlerOffset = ReadWord(ref pc);
                    libjq.jv_free(stack_pop());
                    stack_push(libjq.jv_invalid_get_msg(error));
                    error = libjq.jv_null();
                    pc = checked(pc + handlerOffset);
                    break;
                }

                case (int)opcode.TRY_END + libjq.NUM_OPCODES:
                    if (raising)
                    {
                        set_error(libjq.jv_invalid_with_msg(libjq.jv_copy(error)));
                    }

                    goto DoBacktrack;

                case (int)opcode.DESTRUCTURE_ALT:
                case (int)opcode.FORK:
                    stack_save(operationStart, stack_get_pos());
                    pc++;
                    break;

                case (int)opcode.DESTRUCTURE_ALT + libjq.NUM_OPCODES:
                {
                    if (libjq.jv_is_valid(error))
                    {
                        libjq.jv_free(stack_pop());
                        goto DoBacktrack;
                    }

                    // Keep jq-1.8.2 execute.c's condition verbatim even though
                    // this case necessarily has the backtracking opcode value.
                    if (instruction != (int)opcode.DESTRUCTURE_ALT + libjq.NUM_OPCODES)
                    {
                        libjq.jv_free(stack_pop());
                        stack_push(libjq.jv_invalid_get_msg(error));
                    }
                    else
                    {
                        libjq.jv_free(error);
                    }

                    error = libjq.jv_null();
                    var offset = ReadWord(ref pc);
                    pc = checked(pc + offset);
                    break;
                }

                case (int)opcode.FORK + libjq.NUM_OPCODES:
                {
                    if (raising)
                    {
                        goto DoBacktrack;
                    }

                    var forkOffset = ReadWord(ref pc);
                    pc = checked(pc + forkOffset);
                    break;
                }

                case (int)opcode.CALL_BUILTIN:
                {
                    var argumentCount = ReadWord(ref pc);
                    var functionIndex = ReadWord(ref pc);
                    var globals = frame_current().bc.globals ??
                        throw new InvalidOperationException("CALL_BUILTIN has no globals table.");
                    if ((uint)functionIndex >= (uint)globals.ncfunctions)
                    {
                        throw new InvalidOperationException("CALL_BUILTIN function index is out of range.");
                    }

                    var function = globals.cfunctions[functionIndex];
                    if (argumentCount != function.nargs ||
                        argumentCount is < 1 or > libjq.MAX_CFUNCTION_ARGS)
                    {
                        throw new InvalidOperationException("CALL_BUILTIN arity does not match its function.");
                    }

                    var arguments = new jv[argumentCount];
                    for (var index = 0; index < arguments.Length; index++)
                    {
                        arguments[index] = stack_pop();
                    }

                    var result = InvokeBuiltin(function, arguments);
                    if (!libjq.jv_is_valid(result))
                    {
                        if (libjq.jv_invalid_has_msg(libjq.jv_copy(result)))
                        {
                            set_error(result);
                        }
                        else
                        {
                            libjq.jv_free(result);
                        }

                        goto DoBacktrack;
                    }

                    stack_push(result);
                    break;
                }

                case (int)opcode.TAIL_CALL_JQ:
                case (int)opcode.CALL_JQ:
                {
                    var input = stack_pop();
                    var closureCount = ReadWord(ref pc);
                    var returnAddress = checked(pc + 2 + closureCount * 2);
                    var returnData = stk_top;
                    var currentFrame = frame_current();
                    var logicalDepth = checked(currentFrame.logical_depth + 1);
                    if (jq.Options.HasExplicitMaxRecursionDepth &&
                        logicalDepth > jq.Options.MaxRecursionDepth)
                    {
                        libjq.jv_free(input);
                        throw new JqRuntimeException("jq recursion depth limit exceeded");
                    }

                    var callee = make_closure(pc);
                    if (instruction == (int)opcode.TAIL_CALL_JQ)
                    {
                        returnAddress = currentFrame.retaddr ?? -1;
                        returnData = currentFrame.retdata;
                        frame_pop();
                    }

                    var newFrame = frame_push(callee, checked(pc + 2), closureCount);
                    newFrame.retdata = returnData;
                    newFrame.retaddr = returnAddress >= 0 ? returnAddress : null;
                    newFrame.logical_depth = logicalDepth;
                    pc = 0;
                    stack_push(input);
                    break;
                }

                case (int)opcode.RET:
                {
                    var value = stack_pop();
                    var currentFrame = frame_current();
                    if (stk_top != currentFrame.retdata)
                    {
                        libjq.jv_free(value);
                        throw new InvalidOperationException("RET did not unwind to its recorded data stack.");
                    }

                    var returnAddress = currentFrame.retaddr;
                    if (returnAddress.HasValue)
                    {
                        pc = returnAddress.Value;
                        frame_pop();
                        stack_push(value);
                        break;
                    }

                    var returnPosition = stack_get_pos();
                    stack_push(libjq.jv_null());
                    stack_save(operationStart, returnPosition);
                    return value;
                }

                case (int)opcode.RET + libjq.NUM_OPCODES:
                    goto DoBacktrack;

                case (int)opcode.CLOSURE_PARAM:
                case (int)opcode.CLOSURE_REF:
                case (int)opcode.CLOSURE_CREATE:
                case (int)opcode.CLOSURE_CREATE_C:
                case (int)opcode.CLOSURE_PARAM_REGULAR:
                case (int)opcode.DEPS:
                case (int)opcode.MODULEMETA:
                    throw new InvalidOperationException(
                        $"Compiler-only opcode {(opcode)instruction} reached the jq VM.");

                default:
                    throw new InvalidOperationException("Bytecode contains an invalid instruction.");
            }

            continue;

        DoBacktrack:
            restoredAddress = stack_restore();
            if (!restoredAddress.HasValue)
            {
                if (!libjq.jv_is_valid(error))
                {
                    var terminalError = error;
                    error = libjq.jv_null();
                    return terminalError;
                }

                return libjq.jv_invalid();
            }

            pc = restoredAddress.Value;
            backtracking = true;
        }
    }

    private jv InvokeBuiltin(cfunction function, jv[] arguments) =>
        function.nargs switch
        {
            1 => function.fptr.a1?.Invoke(jq, arguments[0]) ??
                throw new InvalidOperationException("CALL_BUILTIN arity-one delegate is missing."),
            2 => function.fptr.a2?.Invoke(jq, arguments[0], arguments[1]) ??
                throw new InvalidOperationException("CALL_BUILTIN arity-two delegate is missing."),
            3 => function.fptr.a3?.Invoke(jq, arguments[0], arguments[1], arguments[2]) ??
                throw new InvalidOperationException("CALL_BUILTIN arity-three delegate is missing."),
            4 => function.fptr.a4?.Invoke(jq, arguments[0], arguments[1], arguments[2], arguments[3]) ??
                throw new InvalidOperationException("CALL_BUILTIN arity-four delegate is missing."),
            _ => throw new InvalidOperationException("CALL_BUILTIN has an invalid arity."),
        };

    private void TickExecutionPolicy()
    {
        jq.Options.CancellationToken.ThrowIfCancellationRequested();
        // This is the public MaxExecutionTransitions unit: charge once immediately
        // before each forward or ON_BACKTRACK opcode dispatch. Stack exhaustion
        // returns before this point and therefore does not consume a transition.
        dispatch_transitions++;
        if (dispatch_transitions > jq.Options.MaxExecutionTransitions)
        {
            throw new JqRuntimeException("jq execution-transition limit exceeded");
        }

        if (jq.Options.Timeout is { } timeout && execution_stopwatch!.Elapsed >= timeout)
        {
            throw new JqRuntimeException("jq execution timeout exceeded");
        }
    }

    private void EmitTrace(int operationAddress, opcode operation, bool backtracking)
    {
        var output = new StringBuilder();
        output.Append(operationAddress.ToString("D4", CultureInfo.InvariantCulture));
        output.Append(' ');
        output.Append(libjq.opcode_describe(operation).name);
        output.Append('\t');
        if (backtracking)
        {
            output.Append("\t<backtracking>");
        }
        else
        {
            var description = libjq.opcode_describe(operation);
            var count = description.stack_in == -1
                ? ReadWord(frame_current().bc, operationAddress + 1)
                : description.stack_in;
            var parameter = stk_top;
            for (var index = 0; index < count && parameter != (stack_ptr)0; index++)
            {
                if (index != 0)
                {
                    output.Append(" | ");
                }

                output.Append(libjq.jv_dump_string_borrowed(
                    libjq.stack_get_block<vm_value_slot>(stk, parameter).value));
                parameter = libjq.stack_block_next(stk, parameter);
            }

            if ((debug_trace_enabled & libjq.JQ_DEBUG_TRACE_DETAIL) != 0)
            {
                while (parameter != (stack_ptr)0)
                {
                    output.Append(" || ");
                    output.Append(libjq.jv_dump_string_borrowed(
                        libjq.stack_get_block<vm_value_slot>(stk, parameter).value));
                    parameter = libjq.stack_block_next(stk, parameter);
                }
            }
        }

        output.Append('\n');
        jq.EmitDebugTrace(output.ToString());
    }

    private static string DumpTruncCopied(jv value, int maximumLength)
    {
        return libjq.jv_dump_string_trunc_borrowed(value, maximumLength);
    }

    private static string DumpTruncConsumed(jv value, int maximumLength)
    {
        return libjq.jv_dump_string_trunc(value, maximumLength);
    }

    private void Reset()
    {
        while (stack_restore().HasValue)
        {
        }

        if (stk_top != (stack_ptr)0 || fork_top != (stack_ptr)0 || curr_frame != (stack_ptr)0)
        {
            throw new InvalidOperationException("The jq VM stack did not reset to empty.");
        }

        libjq.stack_reset(stk);
        libjq.jv_free(error);
        error = libjq.jv_null();
        if (libjq.jv_get_kind(path) != jv_kind.JV_KIND_INVALID)
        {
            libjq.jv_free(path);
        }

        path = libjq.jv_null();
        libjq.jv_free(value_at_path);
        value_at_path = libjq.jv_null();
        subexp_nest = 0;
        debug_trace_enabled = 0;
        initial_execution = false;
        has_execution_policy = false;
        execution_stopwatch = null;
        dispatch_transitions = 0;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        Reset();
        disposed = true;
    }
}

internal static partial class libjq
{
    // jq-1.8.2 src/execute.c:1145-1213. Keep the post-compilation tail-call
    // optimizer in the execute.c port beside jq_reset()/jq_teardown(), as in
    // the source. The two loops below are stack-safe equivalents of the
    // recursive native walks and preserve the same bytecode mutation order.
    private static int ret_follows(ushort[] code, int programCounter)
    {
        while (true)
        {
            if (code[programCounter] == (ushort)opcode.RET)
            {
                return 1;
            }

            if (code[programCounter++] != (ushort)opcode.JUMP)
            {
                return 0;
            }

            programCounter += code[programCounter] + 1;
        }
    }

    private static ushort tail_call_analyze(ushort[] code, int programCounter)
    {
        RequireExecute(code[programCounter] == (ushort)opcode.CALL_JQ);
        programCounter++;
        for (var closures = code[programCounter++] + 1; closures > 0; closures--)
        {
            if (code[programCounter++] == 0)
            {
                return (ushort)opcode.CALL_JQ;
            }

            programCounter++;
        }

        return ret_follows(code, programCounter) != 0
            ? (ushort)opcode.TAIL_CALL_JQ
            : (ushort)opcode.CALL_JQ;
    }

    private static bytecode optimize_code(bytecode bc)
    {
        var programCounter = 0;
        while (programCounter < bc.codelen)
        {
            if (bc.code[programCounter] == (ushort)opcode.CALL_JQ)
            {
                bc.code[programCounter] = tail_call_analyze(bc.code, programCounter);
            }

            programCounter += bytecode_operation_length(bc.code, programCounter);
        }

        return bc;
    }

    internal static bytecode optimize(bytecode bc)
    {
        var pending = new Stack<bytecode>();
        var postorder = new List<bytecode>();
        pending.Push(bc);
        while (pending.TryPop(out var current))
        {
            postorder.Add(current);
            foreach (var subfunction in current.subfunctions)
            {
                pending.Push(subfunction);
            }
        }

        for (var index = postorder.Count - 1; index >= 0; index--)
        {
            optimize_code(postorder[index]);
        }

        return bc;
    }

    private static void RequireExecute(bool condition)
    {
        if (!condition)
        {
            throw new InvalidOperationException("jq execute invariant failed.");
        }
    }

    internal static void jq_start_bytecode(jq_state jq, bytecode program, jv input, int flags)
    {
        ArgumentNullException.ThrowIfNull(jq);
        jq.StartBytecode(program, input, flags);
    }

    internal static jv jq_next_bytecode(jq_state jq)
    {
        ArgumentNullException.ThrowIfNull(jq);
        return jq.NextBytecode();
    }

    internal static void jq_reset_bytecode(jq_state jq)
    {
        ArgumentNullException.ThrowIfNull(jq);
        jq.ResetBytecodeExecution();
    }

    internal static jv _jq_path_append(jq_state jq, jv value, jv path, jv valueAtPath)
    {
        ArgumentNullException.ThrowIfNull(jq);
        return jq.AppendBytecodePath(value, path, valueAtPath);
    }

    internal static int jq_compile(jq_state jq, string source) =>
        jq_compile_args(jq, source, jv_object(), DisabledModuleResolver(), programOrigin: null);

    internal static int jq_compile(jq_state jq, string source, string? programOrigin) =>
        jq_compile_args(jq, source, jv_object(), DisabledModuleResolver(), programOrigin);

    internal static int jq_compile(jq_state jq, string source, JqModuleResolver resolver) =>
        jq_compile_args(jq, source, jv_object(), resolver, programOrigin: null);

    internal static int jq_compile(
        jq_state jq,
        string source,
        JqModuleResolver resolver,
        string? programOrigin) =>
        jq_compile_args(jq, source, jv_object(), resolver, programOrigin);

    internal static int jq_compile_args(jq_state jq, string source, jv args)
        => jq_compile_args(jq, source, args, DisabledModuleResolver(), programOrigin: null);

    internal static int jq_compile_args(
        jq_state jq,
        string source,
        jv args,
        JqModuleResolver resolver,
        string? programOrigin = null,
        bool loadUserStartupLibrary = false)
    {
        ArgumentNullException.ThrowIfNull(jq);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(resolver);

        jq_reset(jq);
        jq.Bytecode = null;
        jq.BeginCompileDiagnostics();
        var sourceBytes = Encoding.UTF8.GetBytes(source);
        var locations = locfile_init(jq, "<top-level>", sourceBytes, sourceBytes.Length);
        var program = gen_noop();
        var ownsProgram = true;
        var ownsArguments = true;
        var errors = 0;

        try
        {
            errors = load_program(
                jq,
                locations,
                resolver,
                out program,
                programOrigin,
                loadUserStartupLibrary);
            if (errors == 0)
            {
                errors = builtins_bind(jq, ref program);
            }

            if (errors == 0)
            {
                // args2obj() consumes array-shaped arguments and returns the
                // same owner unchanged for object-shaped arguments.
                var argumentObject = args2obj(args);
                ownsArguments = false;
                ownsProgram = false;
                errors = block_compile(program, out var output, locations, argumentObject);
                program = gen_noop();
                if (errors == 0)
                {
                    jq.Bytecode = optimize(output ??
                        throw new InvalidOperationException("Successful block_compile returned no bytecode."));
                }
            }
            else
            {
                jv_free(args);
                ownsArguments = false;
            }

        }
        catch (JqCompileException exception)
        {
            report_linker_error(jq, exception.Message);
            errors = Math.Max(errors, 1);
        }
        finally
        {
            if (ownsProgram)
            {
                block_free(program);
            }

            if (ownsArguments)
            {
                jv_free(args);
            }

            try
            {
                locfile_free(locations);
            }
            finally
            {
                jq.EndCompileDiagnostics();
            }
        }

        // src/execute.c:jq_compile_args() reports the aggregate through the
        // same jq error callback after all detailed locfile/linker reports.
        // End collection first so the public managed CompileError retains its
        // established detail-only payload while jq_set_error_cb sees both.
        if (errors != 0)
        {
            jq_report_error(
                jq,
                jv_string($"jq: {errors} compile {(errors > 1 ? "errors" : "error")}"));
        }

        return jq.Bytecode is not null ? 1 : 0;
    }

    private static jv args2obj(jv args)
    {
        if (jv_get_kind(args) == jv_kind.JV_KIND_OBJECT)
        {
            return args;
        }

        RequireCompiler(jv_get_kind(args) == jv_kind.JV_KIND_ARRAY);
        var result = jv_object();
        var nameKey = jv_string("name");
        var valueKey = jv_string("value");
        var count = jv_array_length(jv_copy(args));
        for (var index = 0; index < count; index++)
        {
            var argument = jv_array_get(jv_copy(args), index);
            result = jv_object_set(
                result,
                jv_object_get(jv_copy(argument), jv_copy(nameKey)),
                jv_object_get(argument, jv_copy(valueKey)));
        }

        jv_free(args);
        jv_free(nameKey);
        jv_free(valueKey);
        return result;
    }

    private static JqModuleResolver DisabledModuleResolver() =>
        new(JqDenyAllFileSystem.Instance, Array.Empty<string>(), ".");
}

#pragma warning restore CS8981
