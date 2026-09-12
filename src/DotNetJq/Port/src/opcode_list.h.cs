// DOTNETJQ PORT MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream file: src/opcode_list.h
// Upstream URL:
//   https://github.com/jqlang/jq/blob/jq-1.8.2/src/opcode_list.h
// Strategy: PORT
// Target file: src/DotNetJq/Port/src/opcode_list.h.cs
//
// Rules:
// - Preserve upstream function names wherever possible.
// - Behavioral compatibility is defined by upstream tests/differential tests.
// - Do not refactor cross-file architecture during compatibility port.
//
// Substitutions:
// - The upstream OP macro expansion is represented by an ordered managed table.
//
// Known differences:
// - None for opcode order, flags, encoded lengths, or stack effects.

#pragma warning disable CS8981 // jq compatibility symbols intentionally retain upstream lowercase names.

namespace DotNetJq.Port;

internal readonly record struct opcode_list_entry(
    opcode op,
    int flags,
    int length,
    int stack_in,
    int stack_out);

/// <summary>
/// The single ordered equivalent of upstream's repeated <c>OP(...)</c> macro input.
/// The numeric value of every <see cref="opcode"/> is its index in this table.
/// </summary>
internal static class opcode_list
{
    internal static IReadOnlyList<opcode_list_entry> entries { get; } =
    [
        new(opcode.LOADK, libjq.OP_HAS_CONSTANT, 2, 1, 1),
        new(opcode.DUP, 0, 1, 1, 2),
        new(opcode.DUPN, 0, 1, 1, 2),
        new(opcode.DUP2, 0, 1, 2, 3),
        new(opcode.PUSHK_UNDER, libjq.OP_HAS_CONSTANT, 2, 1, 2),
        new(opcode.POP, 0, 1, 1, 0),
        new(opcode.LOADV, libjq.OP_HAS_VARIABLE | libjq.OP_HAS_BINDING, 3, 1, 1),
        new(opcode.LOADVN, libjq.OP_HAS_VARIABLE | libjq.OP_HAS_BINDING, 3, 1, 1),
        new(opcode.STOREV, libjq.OP_HAS_VARIABLE | libjq.OP_HAS_BINDING, 3, 1, 0),
        new(
            opcode.STORE_GLOBAL,
            libjq.OP_HAS_CONSTANT |
            libjq.OP_HAS_VARIABLE |
            libjq.OP_HAS_BINDING |
            libjq.OP_IS_CALL_PSEUDO,
            4,
            0,
            0),
        new(opcode.INDEX, 0, 1, 2, 1),
        new(opcode.INDEX_OPT, 0, 1, 2, 1),
        new(opcode.EACH, 0, 1, 1, 1),
        new(opcode.EACH_OPT, 0, 1, 1, 1),
        new(opcode.FORK, libjq.OP_HAS_BRANCH, 2, 0, 0),
        new(opcode.TRY_BEGIN, libjq.OP_HAS_BRANCH, 2, 0, 0),
        new(opcode.TRY_END, 0, 1, 0, 0),
        new(opcode.JUMP, libjq.OP_HAS_BRANCH, 2, 0, 0),
        new(opcode.JUMP_F, libjq.OP_HAS_BRANCH, 2, 1, 0),
        new(opcode.BACKTRACK, 0, 1, 0, 0),
        new(opcode.APPEND, libjq.OP_HAS_VARIABLE | libjq.OP_HAS_BINDING, 3, 1, 0),
        new(opcode.INSERT, 0, 1, 4, 2),
        new(opcode.RANGE, libjq.OP_HAS_VARIABLE | libjq.OP_HAS_BINDING, 3, 1, 1),

        new(opcode.SUBEXP_BEGIN, 0, 1, 1, 2),
        new(opcode.SUBEXP_END, 0, 1, 2, 2),

        new(opcode.PATH_BEGIN, 0, 1, 1, 2),
        new(opcode.PATH_END, 0, 1, 2, 1),

        new(opcode.CALL_BUILTIN, libjq.OP_HAS_CFUNC | libjq.OP_HAS_BINDING, 3, -1, 1),

        new(
            opcode.CALL_JQ,
            libjq.OP_HAS_UFUNC | libjq.OP_HAS_BINDING | libjq.OP_IS_CALL_PSEUDO,
            4,
            1,
            1),
        new(opcode.RET, 0, 1, 1, 1),
        new(
            opcode.TAIL_CALL_JQ,
            libjq.OP_HAS_UFUNC | libjq.OP_HAS_BINDING | libjq.OP_IS_CALL_PSEUDO,
            4,
            1,
            1),

        new(opcode.CLOSURE_PARAM, libjq.OP_IS_CALL_PSEUDO | libjq.OP_HAS_BINDING, 0, 0, 0),
        new(opcode.CLOSURE_REF, libjq.OP_IS_CALL_PSEUDO | libjq.OP_HAS_BINDING, 2, 0, 0),
        new(opcode.CLOSURE_CREATE, libjq.OP_IS_CALL_PSEUDO | libjq.OP_HAS_BINDING, 0, 0, 0),
        new(opcode.CLOSURE_CREATE_C, libjq.OP_IS_CALL_PSEUDO | libjq.OP_HAS_BINDING, 0, 0, 0),

        new(opcode.TOP, 0, 1, 0, 0),
        new(opcode.CLOSURE_PARAM_REGULAR, libjq.OP_IS_CALL_PSEUDO | libjq.OP_HAS_BINDING, 0, 0, 0),
        new(opcode.DEPS, libjq.OP_HAS_CONSTANT, 2, 0, 0),
        new(opcode.MODULEMETA, libjq.OP_HAS_CONSTANT, 2, 0, 0),
        new(opcode.GENLABEL, 0, 1, 0, 1),

        new(opcode.DESTRUCTURE_ALT, libjq.OP_HAS_BRANCH, 2, 0, 0),
        new(opcode.STOREVN, libjq.OP_HAS_VARIABLE | libjq.OP_HAS_BINDING, 3, 1, 0),

        new(opcode.ERRORK, libjq.OP_HAS_CONSTANT, 2, 1, 0),
    ];
}

#pragma warning restore CS8981
