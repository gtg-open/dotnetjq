// DOTNETJQ PORT MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream file: src/bytecode.c
// Upstream URL:
//   https://github.com/jqlang/jq/blob/jq-1.8.2/src/bytecode.c
// Strategy: PORT
// Target file: src/DotNetJq/Port/src/bytecode.c.cs
//
// Rules:
// - Preserve upstream function names wherever possible.
// - Behavioral compatibility is defined by upstream tests/differential tests.
// - Do not refactor cross-file architecture during compatibility port.
//
// Substitutions:
// - TextWriter replaces stdio and an array offset replaces a uint16_t code pointer.
// - Managed graph cleanup replaces explicit jv_mem_free calls.
//
// Known differences:
// - Disassembly is an internal diagnostic and is not exposed by the public managed API.

using System.Globalization;

#pragma warning disable CS8981 // jq compatibility symbols intentionally retain upstream lowercase names.

namespace DotNetJq.Port;

internal static partial class libjq
{
    private static readonly opcode_description[] opcode_descriptions = make_opcode_descriptions();

    private static readonly opcode_description invalid_opcode_description =
        new((opcode)(-1), "#INVALID", 0, 0, 0, 0);

    internal static opcode_description opcode_describe(opcode op)
    {
        var value = (int)op;
        return value >= 0 && value < NUM_OPCODES
            ? opcode_descriptions[value]
            : invalid_opcode_description;
    }

    internal static int bytecode_operation_length(ReadOnlySpan<ushort> codeptr)
    {
        if (codeptr.IsEmpty)
        {
            throw new ArgumentException("An operation must contain an opcode.", nameof(codeptr));
        }

        var op = (opcode)codeptr[0];
        var length = opcode_describe(op).length;
        if (op is opcode.CALL_JQ or opcode.TAIL_CALL_JQ)
        {
            require_words(codeptr, 2);
            length += codeptr[1] * 2;
        }

        return length;
    }

    internal static int bytecode_operation_length(ushort[] codeptr, int offset = 0)
    {
        ArgumentNullException.ThrowIfNull(codeptr);
        if ((uint)offset >= (uint)codeptr.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        return bytecode_operation_length(codeptr.AsSpan(offset));
    }

    internal static void dump_disassembly(int indent, bytecode bc) =>
        dump_disassembly(Console.Out, indent, bc);

    internal static void dump_operation(bytecode bc, int codeptr) =>
        dump_operation(Console.Out, bc, codeptr);

    internal static void bytecode_free(bytecode? bc)
    {
        if (bc is null)
        {
            return;
        }

        var ownsGlobals = bc.parent is null;
        bc.code = [];
        jv_free(bc.constants);
        bc.constants = jv_invalid();
        bc.environment_constant_indexes = [];
        bc.has_environment_constant_slots = false;
        jv_free(bc.compiled_environment);
        bc.compiled_environment = jv_invalid();

        foreach (var subfunction in bc.subfunctions)
        {
            bytecode_free(subfunction);
        }

        bc.subfunctions = [];
        if (ownsGlobals && bc.globals is not null)
        {
            symbol_table_free(bc.globals);
        }

        bc.globals = null;
        jv_free(bc.debuginfo);
        bc.debuginfo = jv_invalid();
        bc.parent = null;
    }

    // Managed host-policy extension over compile.c's ordinary LOADK slots.
    // replacement is consumed exactly once; every marked constant acquires
    // its own jv owner, matching the constant-pool ownership contract.
    internal static void bytecode_replace_environment(bytecode bc, jv replacement)
    {
        try
        {
            replace_environment_slots(bc, replacement);
        }
        finally
        {
            jv_free(replacement);
        }
    }

    internal static void bytecode_restore_compiled_environment(bytecode bc)
    {
        if (jv_is_valid(bc.compiled_environment))
        {
            bytecode_replace_environment(bc, jv_copy(bc.compiled_environment));
        }
    }

    private static void replace_environment_slots(bytecode bc, jv replacement)
    {
        var pending = new Stack<bytecode>();
        pending.Push(bc);
        while (pending.TryPop(out var current))
        {
            foreach (var constantIndex in current.environment_constant_indexes)
            {
                current.constants = jv_array_set(
                    current.constants,
                    constantIndex,
                    jv_copy(replacement));
            }

            foreach (var subfunction in current.subfunctions)
            {
                pending.Push(subfunction);
            }
        }
    }

    private static opcode_description[] make_opcode_descriptions()
    {
        if (opcode_list.entries.Count != NUM_OPCODES)
        {
            throw new InvalidOperationException("The opcode list does not match NUM_OPCODES.");
        }

        var result = new opcode_description[NUM_OPCODES];
        for (var index = 0; index < result.Length; index++)
        {
            var entry = opcode_list.entries[index];
            if ((int)entry.op != index)
            {
                throw new InvalidOperationException("The opcode list is not in bytecode ABI order.");
            }

            result[index] = new opcode_description(
                entry.op,
                entry.op.ToString(),
                entry.flags,
                entry.length,
                entry.stack_in,
                entry.stack_out);
        }

        return result;
    }

    private static void dump_code(TextWriter writer, int indent, bytecode bc)
    {
        var pc = 0;
        while (pc < bc.codelen)
        {
            writer.Write(new string(' ', indent));
            dump_operation(writer, bc, pc);
            writer.WriteLine();
            pc += bytecode_operation_length(bc.code, pc);
        }
    }

    private static void symbol_table_free(symbol_table syms)
    {
        syms.cfunctions = [];
        syms.ncfunctions = 0;
        jv_free(syms.cfunc_names);
        syms.cfunc_names = jv_invalid();
    }

    private static void dump_disassembly(TextWriter writer, int indent, bytecode bc)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(bc);
        ArgumentOutOfRangeException.ThrowIfNegative(indent);

        if (bc.nclosures > 0)
        {
            writer.Write(new string(' ', indent));
            writer.Write("[params: ");
            var parameters = jv_object_get(bc.debuginfo, "params");
            try
            {
                for (var index = 0; index < bc.nclosures; index++)
                {
                    if (index != 0)
                    {
                        writer.Write(", ");
                    }

                    var name = jv_array_get(jv_copy(parameters), index);
                    try
                    {
                        writer.Write(require_jv_string(name));
                    }
                    finally
                    {
                        jv_free(name);
                    }
                }
            }
            finally
            {
                jv_free(parameters);
            }

            writer.WriteLine("]");
        }

        dump_code(writer, indent, bc);
        for (var index = 0; index < bc.nsubfunctions; index++)
        {
            var subfunction = bc.subfunctions[index];
            var name = jv_object_get(subfunction.debuginfo, "name");
            try
            {
                writer.Write(new string(' ', indent));
                writer.Write(require_jv_string(name));
                writer.Write(':');
                writer.Write(index.ToString(CultureInfo.InvariantCulture));
                writer.WriteLine(":");
            }
            finally
            {
                jv_free(name);
            }

            dump_disassembly(writer, indent + 2, subfunction);
        }
    }

    private static bytecode getlevel(bytecode bc, int level)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(level);

        while (level > 0)
        {
            bc = bc.parent ?? throw new InvalidOperationException("Bytecode lexical level exceeds its parent chain.");
            level--;
        }

        return bc;
    }

    private static void dump_operation(TextWriter writer, bytecode bc, int codeptr)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(bc);
        if ((uint)codeptr >= (uint)bc.codelen)
        {
            throw new ArgumentOutOfRangeException(nameof(codeptr));
        }

        var operationStart = codeptr;
        writer.Write(operationStart.ToString("D4", CultureInfo.InvariantCulture));
        writer.Write(' ');

        var op = opcode_describe((opcode)read_word(bc, ref codeptr));
        writer.Write(op.name);
        if (op.length <= 1)
        {
            return;
        }

        var immediate = read_word(bc, ref codeptr);
        if (op.op is opcode.CALL_JQ or opcode.TAIL_CALL_JQ)
        {
            for (var index = 0; index < immediate + 1; index++)
            {
                var level = read_word(bc, ref codeptr);
                var closureIndex = read_word(bc, ref codeptr);
                jv name;
                if ((closureIndex & ARG_NEWCLOSURE) != 0)
                {
                    closureIndex = (ushort)(closureIndex & ~ARG_NEWCLOSURE);
                    var lexicalBytecode = getlevel(bc, level);
                    name = jv_object_get(
                        lexicalBytecode.subfunctions[closureIndex].debuginfo,
                        "name");
                }
                else
                {
                    var lexicalBytecode = getlevel(bc, level);
                    name = jv_array_get(
                        jv_object_get(lexicalBytecode.debuginfo, "params"),
                        closureIndex);
                }

                try
                {
                    writer.Write(' ');
                    writer.Write(require_jv_string(name));
                    writer.Write(':');
                    writer.Write(closureIndex.ToString(CultureInfo.InvariantCulture));
                    if (level != 0)
                    {
                        writer.Write('^');
                        writer.Write(level.ToString(CultureInfo.InvariantCulture));
                    }
                }
                finally
                {
                    jv_free(name);
                }
            }
        }
        else if (op.op == opcode.CALL_BUILTIN)
        {
            var functionIndex = read_word(bc, ref codeptr);
            var globals = bc.globals ?? throw new InvalidOperationException("Bytecode has no global symbol table.");
            var name = jv_array_get(jv_copy(globals.cfunc_names), functionIndex);
            try
            {
                writer.Write(' ');
                writer.Write(require_jv_string(name));
            }
            finally
            {
                jv_free(name);
            }
        }
        else if ((op.flags & OP_HAS_BRANCH) != 0)
        {
            writer.Write(' ');
            writer.Write((codeptr + immediate).ToString("D4", CultureInfo.InvariantCulture));
        }
        else if ((op.flags & OP_HAS_CONSTANT) != 0)
        {
            writer.Write(' ');
            var constant = jv_array_get(jv_copy(bc.constants), immediate);
            // jv_dump_string() consumes the owned jv_array_get() result.
            writer.Write(jv_dump_string(constant));
        }
        else if ((op.flags & OP_HAS_VARIABLE) != 0)
        {
            var variable = read_word(bc, ref codeptr);
            var lexicalBytecode = getlevel(bc, immediate);
            var name = jv_array_get(
                jv_object_get(lexicalBytecode.debuginfo, "locals"),
                variable);
            try
            {
                writer.Write(" $");
                writer.Write(require_jv_string(name));
                writer.Write(':');
                writer.Write(variable.ToString(CultureInfo.InvariantCulture));
                if (immediate != 0)
                {
                    writer.Write('^');
                    writer.Write(immediate.ToString(CultureInfo.InvariantCulture));
                }
            }
            finally
            {
                jv_free(name);
            }
        }
        else
        {
            writer.Write(' ');
            writer.Write(immediate.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static ushort read_word(bytecode bc, ref int pc)
    {
        if ((uint)pc >= (uint)bc.codelen)
        {
            throw new InvalidOperationException("Truncated bytecode operation.");
        }

        return bc.code[pc++];
    }

    private static string require_jv_string(jv value) =>
        value.Kind == jv_kind.JV_KIND_STRING
            ? value.StringValue
            : throw new InvalidOperationException("Bytecode debug metadata must contain strings.");

    private static void require_words(ReadOnlySpan<ushort> codeptr, int count)
    {
        if (codeptr.Length < count)
        {
            throw new ArgumentException("Truncated bytecode operation.", nameof(codeptr));
        }
    }
}

#pragma warning restore CS8981
