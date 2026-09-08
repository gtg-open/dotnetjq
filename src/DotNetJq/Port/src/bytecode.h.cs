// DOTNETJQ PORT MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream file: src/bytecode.h
// Upstream URL:
//   https://github.com/jqlang/jq/blob/jq-1.8.2/src/bytecode.h
// Strategy: PORT
// Target file: src/DotNetJq/Port/src/bytecode.h.cs
//
// Rules:
// - Preserve upstream function names wherever possible.
// - Behavioral compatibility is defined by upstream tests/differential tests.
// - Do not refactor cross-file architecture during compatibility port.
//
// Substitutions:
// - Managed arrays replace owned C arrays and pointers.
// - Delegate slots store the managed equivalent of the cfunction function-pointer union.
//
// Known differences:
// - Managed lifetime replaces explicit allocation ownership; bytecode_free clears references eagerly.

#pragma warning disable CS8981 // jq compatibility symbols intentionally retain upstream lowercase names.

namespace DotNetJq.Port;

// Numeric order is the bytecode ABI and must match src/opcode_list.h exactly.
internal enum opcode
{
    LOADK,
    DUP,
    DUPN,
    DUP2,
    PUSHK_UNDER,
    POP,
    LOADV,
    LOADVN,
    STOREV,
    STORE_GLOBAL,
    INDEX,
    INDEX_OPT,
    EACH,
    EACH_OPT,
    FORK,
    TRY_BEGIN,
    TRY_END,
    JUMP,
    JUMP_F,
    BACKTRACK,
    APPEND,
    INSERT,
    RANGE,
    SUBEXP_BEGIN,
    SUBEXP_END,
    PATH_BEGIN,
    PATH_END,
    CALL_BUILTIN,
    CALL_JQ,
    RET,
    TAIL_CALL_JQ,
    CLOSURE_PARAM,
    CLOSURE_REF,
    CLOSURE_CREATE,
    CLOSURE_CREATE_C,
    TOP,
    CLOSURE_PARAM_REGULAR,
    DEPS,
    MODULEMETA,
    GENLABEL,
    DESTRUCTURE_ALT,
    STOREVN,
    ERRORK,
}

internal sealed class opcode_description
{
    internal opcode_description(opcode op, string name, int flags, int length, int stack_in, int stack_out)
    {
        this.op = op;
        this.name = name;
        this.flags = flags;
        this.length = length;
        this.stack_in = stack_in;
        this.stack_out = stack_out;
    }

    internal opcode op { get; }

    internal string name { get; }

    internal int flags { get; }

    // Length in 16-bit units.
    internal int length { get; }

    internal int stack_in { get; }

    internal int stack_out { get; }
}

internal delegate jv cfunction_a1(jq_state jq, jv input);

internal delegate jv cfunction_a2(jq_state jq, jv input, jv argument1);

internal delegate jv cfunction_a3(jq_state jq, jv input, jv argument1, jv argument2);

internal delegate jv cfunction_a4(jq_state jq, jv input, jv argument1, jv argument2, jv argument3);

internal sealed class cfunction_ptr
{
    internal cfunction_a1? a1 { get; set; }

    internal cfunction_a2? a2 { get; set; }

    internal cfunction_a3? a3 { get; set; }

    internal cfunction_a4? a4 { get; set; }

    internal Delegate? for_arity(int nargs) => nargs switch
    {
        1 => a1,
        2 => a2,
        3 => a3,
        4 => a4,
        _ => throw new ArgumentOutOfRangeException(nameof(nargs)),
    };
}

internal sealed class cfunction
{
    internal cfunction(cfunction_ptr fptr, string name, int nargs)
    {
        if (nargs is < 1 or > libjq.MAX_CFUNCTION_ARGS)
        {
            throw new ArgumentOutOfRangeException(nameof(nargs));
        }

        this.fptr = fptr;
        this.name = name;
        this.nargs = nargs;
    }

    // Mirrors the upstream a1/a2/a3/a4 function-pointer union.
    internal cfunction_ptr fptr { get; }

    internal string name { get; }

    internal int nargs { get; }
}

internal sealed class symbol_table
{
    internal cfunction[] cfunctions { get; set; } = [];

    // compile.c allocates the complete table up front, then fills it while
    // recursively lowering CLOSURE_CREATE_C definitions.  Keep the native
    // populated-count separate from managed array capacity.
    internal int ncfunctions { get; set; }

    internal jv cfunc_names { get; set; } = libjq.jv_invalid();
}

internal sealed class bytecode
{
    internal ushort[] code { get; set; } = [];

    internal int codelen => code.Length;

    internal int nlocals { get; set; }

    internal int nclosures { get; set; }

    internal jv constants { get; set; } = libjq.jv_invalid();

    // Constant-pool slots originating from compile.c's LOADV($ENV)->LOADK
    // expansion. This is managed metadata only; it does not alter code words.
    internal int[] environment_constant_indexes { get; set; } = [];

    // True when this bytecode or any descendant has an environment constant
    // slot. compile.c computes the graph summary bottom-up so jq_start can
    // avoid both environment materialization and bytecode traversal for the
    // overwhelmingly common program that does not reference $ENV.
    internal bool has_environment_constant_slots { get; set; }

    // Root-only owner of the process environment captured by compile.c's
    // first $ENV expansion. It allows sequential managed executions to
    // restore native compile-time semantics after an explicit override.
    internal jv compiled_environment { get; set; } = libjq.jv_invalid();

    internal symbol_table? globals { get; set; }

    internal bytecode[] subfunctions { get; set; } = [];

    internal int nsubfunctions => subfunctions.Length;

    internal bytecode? parent { get; set; }

    internal jv debuginfo { get; set; } = libjq.jv_invalid();
}

internal static partial class libjq
{
    internal const int NUM_OPCODES = (int)opcode.ERRORK + 1;

    internal const int OP_HAS_CONSTANT = 2;
    internal const int OP_HAS_VARIABLE = 4;
    internal const int OP_HAS_BRANCH = 8;
    internal const int OP_HAS_CFUNC = 32;
    internal const int OP_HAS_UFUNC = 64;
    internal const int OP_IS_CALL_PSEUDO = 128;
    internal const int OP_HAS_BINDING = 1024;

    // Not part of any opcode: pseudo-op flag used for special handling of `break`.
    internal const int OP_BIND_WILDCARD = 2048;

    internal const int MAX_CFUNCTION_ARGS = 4;
    internal const ushort ARG_NEWCLOSURE = 0x1000;
}

#pragma warning restore CS8981
