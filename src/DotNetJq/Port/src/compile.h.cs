// DOTNETJQ PORT MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream file: src/compile.h
// Upstream URL: https://github.com/jqlang/jq/blob/jq-1.8.2/src/compile.h
// Strategy: PORT
// Target file: src/DotNetJq/Port/src/compile.h.cs
//
// Direct surface: compile.h's inst_immediate, inst, block, and BLOCK_1..BLOCK_8 ownership shapes.
// Reference identity remains significant for bound_by, branch targets, and compiled lexical
// frames. Managed object references replace C pointers and the allocation-free BLOCK overloads
// retain the upstream left-associated joins.
// Known differences: managed types are not C layout- or pointer-ABI compatible.
// Evidence: BytecodeProductionPipelineTests, direct parser/linker IR suites, compiler ownership
// lifecycle suites, official jq fixtures, and differential execution.

namespace DotNetJq.Port;

// jq-1.8.2 src/compile.c:23-65. Reference identity is significant here:
// bound_by and branch targets point at instructions in the same owning graph.
internal sealed class inst_immediate
{
    internal ushort intval;

    internal inst? target;

    internal jv constant;

    internal cfunction? cfunc;
}

internal sealed class inst
{
    internal inst(opcode op)
    {
        this.op = op;
    }

    internal inst? next;

    internal inst? prev;

    internal opcode op;

    internal inst_immediate imm { get; } = new();

    internal locfile? locfile;

    internal location source = libjq.UNKNOWN_LOCATION;

    internal inst? bound_by;

    internal string? symbol;

    internal int any_unbound;

    internal int referenced;

    internal int nformals = -1;

    internal int nactuals = -1;

    internal block subfn;

    internal block arglist;

    internal bytecode? compiled;

    internal int bytecode_pos = -1;

    // Managed host-policy metadata. This identifies an otherwise ordinary
    // LOADK produced from unbound $ENV so an explicit isolated environment
    // can replace only that pool slot without changing jq's bytecode ABI.
    internal bool is_environment_constant;
}

// jq-1.8.2 src/compile.h:8-15. A block is an owning-by-convention value-type
// header over a doubly-linked instruction list. Copying the header does not
// retain the instructions; consuming functions move its logical owner.
internal struct block
{
    internal block(inst? first, inst? last)
    {
        this.first = first;
        this.last = last;
    }

    internal inst? first;

    internal inst? last;
}

internal static partial class libjq
{
    // Allocation-free counterparts of compile.h's BLOCK_1..BLOCK_8 macros.
    internal static block BLOCK(block b1) => b1;

    internal static block BLOCK(block b1, block b2) => block_join(b1, b2);

    internal static block BLOCK(block b1, block b2, block b3) =>
        block_join(BLOCK(b1, b2), b3);

    internal static block BLOCK(block b1, block b2, block b3, block b4) =>
        block_join(BLOCK(b1, b2, b3), b4);

    internal static block BLOCK(block b1, block b2, block b3, block b4, block b5) =>
        block_join(BLOCK(b1, b2, b3, b4), b5);

    internal static block BLOCK(block b1, block b2, block b3, block b4, block b5, block b6) =>
        block_join(BLOCK(b1, b2, b3, b4, b5), b6);

    internal static block BLOCK(
        block b1,
        block b2,
        block b3,
        block b4,
        block b5,
        block b6,
        block b7) =>
        block_join(BLOCK(b1, b2, b3, b4, b5, b6), b7);

    internal static block BLOCK(
        block b1,
        block b2,
        block b3,
        block b4,
        block b5,
        block b6,
        block b7,
        block b8) =>
        block_join(BLOCK(b1, b2, b3, b4, b5, b6, b7), b8);
}
