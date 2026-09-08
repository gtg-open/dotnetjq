// DOTNETJQ PORT MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream file: src/compile.c
// Upstream URL: https://github.com/jqlang/jq/blob/jq-1.8.2/src/compile.c
// Strategy: PORT
// Target file: src/DotNetJq/Port/src/compile.c.cs
//
// Direct surface: source-named block/inst generation and binding, call-argument expansion,
// lexical-level resolution, constant/cfunction/subfunction materialization, bytecode emission,
// and block_compile(). The production parser/linker feeds this IR and the
// production VM executes the resulting bytecode graph.
// Substitutions: managed object references replace C pointers; managed arrays replace owned C
// buffers; locfile retains the pinned diagnostic/location contract and reports through
// jq_report_error on its owning jq_state.
// Ownership: block operations consume instruction-list owners by convention exactly as upstream.
// compile() copies each instruction constant into its bytecode pool, block_free() releases the IR
// owner, and bytecode_free() recursively releases pools/subfunctions at recompile or teardown.
// Known differences: managed representations are not C layout- or pointer-ABI compatible.
// Evidence: BytecodeProductionPipelineTests, CompiledLiteralRefcountLifecycleTests,
// CompileArgumentRawObjectIterationCompatibilityTests, compiler/module compatibility suites,
// official jq fixtures, and differential execution.

namespace DotNetJq.Port;

// Direct port of jq-1.8.2 src/compile.c's instruction/block generation, binding, and lowering core.
internal static partial class libjq
{
    private static inst inst_new(opcode op) => new(op);

    private static void inst_free(inst instruction)
    {
        block_free(instruction.subfn);
        block_free(instruction.arglist);
        instruction.subfn = gen_noop();
        instruction.arglist = gen_noop();
        if (instruction.locfile is not null)
        {
            locfile_free(instruction.locfile);
            instruction.locfile = null;
        }

        if ((opcode_describe(instruction.op).flags & OP_HAS_CONSTANT) != 0)
        {
            jv_free(instruction.imm.constant);
            instruction.imm.constant = jv_invalid();
        }

        instruction.next = null;
        instruction.prev = null;
        instruction.bound_by = null;
        instruction.symbol = null;
        instruction.compiled = null;
        instruction.imm.target = null;
        instruction.imm.cfunc = null;
    }

    private static block inst_block(inst instruction) => new(instruction, instruction);

    internal static bool block_is_single(block b) => b.first is not null && ReferenceEquals(b.first, b.last);

    private static inst? block_take(ref block b)
    {
        var instruction = b.first;
        if (instruction is null)
        {
            return null;
        }

        if (instruction.next is not null)
        {
            instruction.next.prev = null;
            b.first = instruction.next;
            instruction.next = null;
        }
        else
        {
            b.first = null;
            b.last = null;
        }

        return instruction;
    }

    internal static block gen_location(location loc, locfile file, block b)
    {
        for (var instruction = b.first; instruction is not null; instruction = instruction.next)
        {
            if (instruction.source.start == UNKNOWN_LOCATION.start &&
                instruction.source.end == UNKNOWN_LOCATION.end)
            {
                instruction.source = loc;
                instruction.locfile = locfile_retain(file);
            }
        }

        return b;
    }

    internal static block gen_noop() => default;

    internal static bool block_is_noop(block b) => b.first is null && b.last is null;

    internal static block gen_op_simple(opcode op)
    {
        RequireCompiler(opcode_describe(op).length == 1, "simple opcode must have length one");
        return inst_block(inst_new(op));
    }

    internal static block gen_error(jv constant)
    {
        RequireCompiler((opcode_describe(opcode.ERRORK).flags & OP_HAS_CONSTANT) != 0);
        var instruction = inst_new(opcode.ERRORK);
        instruction.imm.constant = constant;
        return inst_block(instruction);
    }

    internal static block gen_const(jv constant)
    {
        RequireCompiler((opcode_describe(opcode.LOADK).flags & OP_HAS_CONSTANT) != 0);
        var instruction = inst_new(opcode.LOADK);
        instruction.imm.constant = constant;
        return inst_block(instruction);
    }

    internal static block gen_const_global(jv constant, string name)
    {
        const int flags = OP_HAS_CONSTANT | OP_HAS_VARIABLE | OP_HAS_BINDING;
        RequireCompiler((opcode_describe(opcode.STORE_GLOBAL).flags & flags) == flags);
        var instruction = inst_new(opcode.STORE_GLOBAL);
        instruction.imm.constant = constant;
        instruction.symbol = name;
        instruction.any_unbound = 0;
        return inst_block(instruction);
    }

    internal static block gen_op_pushk_under(jv constant)
    {
        RequireCompiler((opcode_describe(opcode.PUSHK_UNDER).flags & OP_HAS_CONSTANT) != 0);
        var instruction = inst_new(opcode.PUSHK_UNDER);
        instruction.imm.constant = constant;
        return inst_block(instruction);
    }

    internal static bool block_is_const(block b) =>
        block_is_single(b) && b.first!.op is opcode.LOADK or opcode.PUSHK_UNDER;

    internal static jv_kind block_const_kind(block b)
    {
        RequireCompiler(block_is_const(b));
        return jv_get_kind(b.first!.imm.constant);
    }

    internal static jv block_const(block b)
    {
        RequireCompiler(block_is_const(b));
        return jv_copy(b.first!.imm.constant);
    }

    internal static block gen_op_target(opcode op, block target)
    {
        RequireCompiler((opcode_describe(op).flags & OP_HAS_BRANCH) != 0);
        RequireCompiler(target.last is not null, "branch target must not be empty");
        var instruction = inst_new(op);
        instruction.imm.target = target.last;
        return inst_block(instruction);
    }

    private static block gen_op_targetlater(opcode op)
    {
        RequireCompiler((opcode_describe(op).flags & OP_HAS_BRANCH) != 0);
        return inst_block(inst_new(op));
    }

    private static void inst_set_target(block b, block target)
    {
        RequireCompiler(block_is_single(b));
        RequireCompiler((opcode_describe(b.first!.op).flags & OP_HAS_BRANCH) != 0);
        RequireCompiler(target.last is not null, "branch target must not be empty");
        b.first.imm.target = target.last;
    }

    internal static block gen_op_unbound(opcode op, string name)
    {
        RequireCompiler((opcode_describe(op).flags & OP_HAS_BINDING) != 0);
        var instruction = inst_new(op);
        instruction.symbol = name;
        instruction.any_unbound = 1;
        return inst_block(instruction);
    }

    internal static block gen_op_var_fresh(opcode op, string name)
    {
        RequireCompiler((opcode_describe(op).flags & OP_HAS_VARIABLE) != 0);
        var b = gen_op_unbound(op, name);
        b.first!.bound_by = b.first;
        return b;
    }

    internal static block gen_op_bound(opcode op, block binder)
    {
        RequireCompiler(block_is_single(binder));
        var b = gen_op_unbound(op, binder.first!.symbol ?? string.Empty);
        b.first!.bound_by = binder.first;
        b.first.any_unbound = 0;
        return b;
    }

    internal static block gen_dictpair(block key, block value) =>
        BLOCK(gen_subexp(key), gen_subexp(value), gen_op_simple(opcode.INSERT));

    private static void inst_join(inst first, inst second)
    {
        RequireCompiler(first.next is null && second.prev is null);
        first.next = second;
        second.prev = first;
    }

    internal static void block_append(ref block b, block b2)
    {
        if (b2.first is null)
        {
            return;
        }

        if (b.last is not null)
        {
            inst_join(b.last, b2.first);
        }
        else
        {
            b.first = b2.first;
        }

        b.last = b2.last;
    }

    internal static block block_join(block first, block second)
    {
        block_append(ref first, second);
        return first;
    }

    internal static bool block_has_only_binders_and_imports(block binders, int bindflags)
    {
        bindflags |= OP_HAS_BINDING;
        for (var current = binders.first; current is not null; current = current.next)
        {
            if ((opcode_describe(current.op).flags & bindflags) != bindflags &&
                current.op is not opcode.DEPS and not opcode.MODULEMETA)
            {
                return false;
            }
        }

        return true;
    }

    private static bool inst_is_binder(inst instruction, int bindflags) =>
        !((opcode_describe(instruction.op).flags & bindflags) != bindflags &&
          instruction.op != opcode.MODULEMETA);

    internal static bool block_has_only_binders(block binders, int bindflags)
    {
        bindflags |= OP_HAS_BINDING;
        bindflags &= ~OP_BIND_WILDCARD;
        for (var current = binders.first; current is not null; current = current.next)
        {
            if ((opcode_describe(current.op).flags & bindflags) != bindflags &&
                current.op != opcode.MODULEMETA)
            {
                return false;
            }
        }

        return true;
    }

    private static int block_count_actuals(block b)
    {
        var arguments = 0;
        for (var instruction = b.first; instruction is not null; instruction = instruction.next)
        {
            switch (instruction.op)
            {
                case opcode.CLOSURE_CREATE:
                case opcode.CLOSURE_PARAM:
                case opcode.CLOSURE_CREATE_C:
                    arguments++;
                    break;
                default:
                    throw new InvalidOperationException("Unknown function argument type.");
            }
        }

        return arguments;
    }

    private static int block_bind_subblock_inner(
        ref int anyUnbound,
        block binder,
        block body,
        int bindflags,
        int breakDistance)
    {
        RequireCompiler(block_is_single(binder));
        var binding = binder.first!;
        RequireCompiler(
            (opcode_describe(binding.op).flags & bindflags) == (bindflags & ~OP_BIND_WILDCARD));
        RequireCompiler(binding.symbol is not null);
        RequireCompiler(binding.bound_by is null || ReferenceEquals(binding.bound_by, binding));
        RequireCompiler(breakDistance >= 0);

        binding.bound_by = binding;
        var references = 0;
        for (var instruction = body.first; instruction is not null; instruction = instruction.next)
        {
            if (instruction.any_unbound == 0)
            {
                continue;
            }

            var flags = opcode_describe(instruction.op).flags;
            var symbol = instruction.symbol;
            if ((flags & bindflags) == (bindflags & ~OP_BIND_WILDCARD) &&
                instruction.bound_by is null &&
                (string.Equals(symbol, binding.symbol, StringComparison.Ordinal) ||
                 ((bindflags & OP_BIND_WILDCARD) != 0 &&
                  symbol is { Length: 2 } &&
                  symbol[0] == '*' &&
                  breakDistance <= 3 &&
                  symbol[1] == '1' + breakDistance)))
            {
                if (instruction.nactuals == -1 || instruction.nactuals == binding.nformals)
                {
                    instruction.bound_by = binding;
                    references++;
                }
            }
            else if ((flags & bindflags) == (bindflags & ~OP_BIND_WILDCARD) &&
                     instruction.bound_by is not null &&
                     binding.symbol!.StartsWith("*anonlabel", StringComparison.Ordinal) &&
                     symbol is not null &&
                     symbol.StartsWith("*anonlabel", StringComparison.Ordinal))
            {
                breakDistance++;
            }

            instruction.any_unbound = symbol is not null && instruction.bound_by is null ? 1 : 0;
            references += block_bind_subblock_inner(
                ref instruction.any_unbound,
                binder,
                instruction.subfn,
                bindflags,
                breakDistance);
            references += block_bind_subblock_inner(
                ref instruction.any_unbound,
                binder,
                instruction.arglist,
                bindflags,
                breakDistance);

            if (instruction.any_unbound != 0)
            {
                anyUnbound = 1;
            }
        }

        return references;
    }

    private static int block_bind_subblock(block binder, block body, int bindflags, int breakDistance)
    {
        var anyUnbound = 0;
        return block_bind_subblock_inner(ref anyUnbound, binder, body, bindflags, breakDistance);
    }

    private static int block_bind_each(block binder, block body, int bindflags)
    {
        RequireCompiler(block_has_only_binders(binder, bindflags));
        bindflags |= OP_HAS_BINDING;
        var references = 0;
        for (var current = binder.first; current is not null; current = current.next)
        {
            references += block_bind_subblock(inst_block(current), body, bindflags, 0);
        }

        return references;
    }

    private static block block_bind(block binder, block body, int bindflags)
    {
        block_bind_each(binder, body, bindflags);
        return block_join(binder, body);
    }

    internal static block block_bind_library(block binder, block body, int bindflags, string? libname)
    {
        bindflags |= OP_HAS_BINDING;
        var prefix = string.IsNullOrEmpty(libname) ? string.Empty : libname + "::";
        RequireCompiler(block_has_only_binders(binder, bindflags));
        for (var current = binder.last; current is not null; current = current.prev)
        {
            var bindflags2 = bindflags;
            var originalName = current.symbol ?? string.Empty;
            if ((opcode_describe(current.op).flags & (OP_HAS_VARIABLE | OP_HAS_CONSTANT)) != 0)
            {
                bindflags2 = OP_HAS_VARIABLE | OP_HAS_BINDING;
            }

            current.symbol = prefix + originalName;
            block_bind_subblock(inst_block(current), body, bindflags2, 0);
            current.symbol = originalName;
        }

        return body;
    }

    private static inst? block_take_last(ref block b)
    {
        var instruction = b.last;
        if (instruction is null)
        {
            return null;
        }

        if (instruction.prev is not null)
        {
            instruction.prev.next = instruction.next;
            b.last = instruction.prev;
            instruction.prev = null;
        }
        else
        {
            b.first = null;
            b.last = null;
        }

        return instruction;
    }

    internal static block block_bind_referenced(block binder, block body, int bindflags)
    {
        RequireCompiler(block_has_only_binders(binder, bindflags));
        bindflags |= OP_HAS_BINDING;
        inst? current;
        while ((current = block_take_last(ref binder)) is not null)
        {
            var currentBlock = inst_block(current);
            if (block_bind_subblock(currentBlock, body, bindflags, 0) == 0)
            {
                block_free(currentBlock);
            }
            else
            {
                body = BLOCK(currentBlock, body);
            }
        }

        return body;
    }

    internal static block block_bind_self(block binder, int bindflags)
    {
        RequireCompiler(block_has_only_binders(binder, bindflags));
        bindflags |= OP_HAS_BINDING;
        var body = gen_noop();
        inst? current;
        while ((current = block_take_last(ref binder)) is not null)
        {
            var currentBlock = inst_block(current);
            block_bind_subblock(currentBlock, body, bindflags, 0);
            body = BLOCK(currentBlock, body);
        }

        return body;
    }

    private static void block_mark_referenced(block body)
    {
        var sawTop = false;
        for (var instruction = body.last; instruction is not null; instruction = instruction.prev)
        {
            if (sawTop && ReferenceEquals(instruction.bound_by, instruction) && instruction.referenced == 0)
            {
                continue;
            }

            if (instruction.op == opcode.TOP)
            {
                sawTop = true;
            }

            if (instruction.bound_by is not null)
            {
                instruction.bound_by.referenced = 1;
            }

            block_mark_referenced(instruction.arglist);
            block_mark_referenced(instruction.subfn);
        }
    }

    internal static block block_drop_unreferenced(block body)
    {
        block_mark_referenced(body);
        var referenced = gen_noop();
        inst? current;
        while ((current = block_take(ref body)) is not null)
        {
            if (ReferenceEquals(current.bound_by, current) && current.referenced == 0)
            {
                inst_free(current);
            }
            else
            {
                referenced = BLOCK(referenced, inst_block(current));
            }
        }

        return referenced;
    }

    internal static jv block_take_imports(ref block body)
    {
        var imports = jv_array();
        RequireCompiler(
            body.first is null ||
            body.first.op != opcode.TOP ||
            body.first.next is null ||
            body.first.next.op is not opcode.MODULEMETA and not opcode.DEPS);

        while (body.first is { op: opcode.MODULEMETA or opcode.DEPS })
        {
            var dependency = block_take(ref body)!;
            if (dependency.op == opcode.DEPS)
            {
                imports = jv_array_append(imports, jv_copy(dependency.imm.constant));
            }

            inst_free(dependency);
        }

        return imports;
    }

    internal static jv block_list_funcs(block body, int omitUnderscores)
    {
        var functions = jv_object();
        for (var position = body.first; position is not null; position = position.next)
        {
            if (position.op is opcode.CLOSURE_CREATE or opcode.CLOSURE_CREATE_C &&
                position.symbol is not null &&
                (omitUnderscores == 0 || !position.symbol.StartsWith('_')))
            {
                functions = jv_object_set(
                    functions,
                    $"{position.symbol}/{position.nformals}",
                    jv_null());
            }
        }

        return jv_keys_unsorted(functions);
    }

    internal static block gen_module(block metadata)
    {
        RequireCompiler(
            block_is_const(metadata) && block_const_kind(metadata) == jv_kind.JV_KIND_OBJECT);
        var instruction = inst_new(opcode.MODULEMETA);
        instruction.imm.constant = block_const(metadata);
        if (jv_get_kind(instruction.imm.constant) != jv_kind.JV_KIND_OBJECT)
        {
            instruction.imm.constant = jv_object_set(
                jv_object(),
                "metadata",
                instruction.imm.constant);
        }

        block_free(metadata);
        return inst_block(instruction);
    }

    internal static jv block_module_meta(block b) =>
        b.first is { op: opcode.MODULEMETA }
            ? jv_copy(b.first.imm.constant)
            : jv_null();

    internal static block gen_import(jv name, jv asName, int isData)
    {
        RequireCompiler(jv_get_kind(name) == jv_kind.JV_KIND_STRING);
        RequireCompiler(
            !jv_is_valid(asName) || jv_get_kind(asName) == jv_kind.JV_KIND_STRING);
        var instruction = inst_new(opcode.DEPS);
        var metadata = jv_object();
        if (jv_is_valid(asName))
        {
            metadata = jv_object_set(metadata, "as", asName);
        }
        else
        {
            jv_free(asName);
        }

        metadata = jv_object_set(metadata, "is_data", jv_bool(isData != 0));
        metadata = jv_object_set(metadata, "relpath", name);
        instruction.imm.constant = metadata;
        return inst_block(instruction);
    }

    internal static block gen_import_meta(block import, block metadata)
    {
        RequireCompiler(block_is_single(import) && import.first!.op == opcode.DEPS);
        RequireCompiler(
            block_is_const(metadata) && block_const_kind(metadata) == jv_kind.JV_KIND_OBJECT);
        import.first!.imm.constant = jv_object_merge(
            block_const(metadata),
            import.first.imm.constant);
        block_free(metadata);
        return import;
    }

    internal static block gen_function(string name, block formals, block body)
    {
        var function = inst_new(opcode.CLOSURE_CREATE);
        var formalCount = 0;
        for (var formal = formals.last; formal is not null; formal = formal.prev)
        {
            formalCount++;
            formal.nformals = 0;
            if (formal.op == opcode.CLOSURE_PARAM_REGULAR)
            {
                formal.op = opcode.CLOSURE_PARAM;
                body = gen_var_binding(gen_call(formal.symbol ?? string.Empty, gen_noop()), formal.symbol ?? string.Empty, body);
            }

            block_bind_subblock(
                inst_block(formal),
                body,
                OP_IS_CALL_PSEUDO | OP_HAS_BINDING,
                0);
        }

        function.subfn = body;
        function.symbol = name;
        function.any_unbound = -1;
        function.nformals = formalCount;
        function.arglist = formals;
        var result = inst_block(function);
        block_bind_subblock(result, result, OP_IS_CALL_PSEUDO | OP_HAS_BINDING, 0);
        return result;
    }

    internal static block gen_param_regular(string name) =>
        gen_op_unbound(opcode.CLOSURE_PARAM_REGULAR, name);

    internal static block gen_param(string name) => gen_op_unbound(opcode.CLOSURE_PARAM, name);

    internal static block gen_lambda(block body) => gen_function("@lambda", gen_noop(), body);

    internal static block gen_call(string name, block args)
    {
        var result = gen_op_unbound(opcode.CALL_JQ, name);
        result.first!.arglist = args;
        result.first.nactuals = block_count_actuals(result.first.arglist);
        return result;
    }

    internal static block gen_subexp(block expression)
    {
        if (block_is_noop(expression))
        {
            return gen_op_simple(opcode.DUP);
        }

        if (block_is_single(expression) && expression.first!.op == opcode.LOADK)
        {
            var isEnvironmentConstant = expression.first.is_environment_constant;
            var constant = block_const(expression);
            block_free(expression);
            var result = gen_op_pushk_under(constant);
            result.first!.is_environment_constant = isEnvironmentConstant;
            return result;
        }

        return BLOCK(
            gen_op_simple(opcode.SUBEXP_BEGIN),
            expression,
            gen_op_simple(opcode.SUBEXP_END));
    }

    internal static block gen_both(block first, block second)
    {
        var jump = gen_op_targetlater(opcode.JUMP);
        var fork = gen_op_target(opcode.FORK, jump);
        var result = BLOCK(fork, first, jump, second);
        inst_set_target(jump, result);
        return result;
    }

    internal static block gen_const_object(block expression)
    {
        var isConstant = true;
        var result = jv_object();
        var key = jv_null();
        var value = jv_null();
        var instruction = expression.first;
        while (instruction is not null)
        {
            if (instruction.op == opcode.PUSHK_UNDER)
            {
                key = jv_copy(instruction.imm.constant);
                instruction = instruction.next;
            }
            else if (instruction.op != opcode.SUBEXP_BEGIN ||
                     instruction.next is not { op: opcode.LOADK } keyConstant ||
                     keyConstant.next is not { op: opcode.SUBEXP_END } keyEnd)
            {
                isConstant = false;
                break;
            }
            else
            {
                key = jv_copy(keyConstant.imm.constant);
                instruction = keyEnd.next;
            }

            if (instruction is { op: opcode.PUSHK_UNDER })
            {
                value = jv_copy(instruction.imm.constant);
                instruction = instruction.next;
            }
            else if (instruction is null ||
                     instruction.op != opcode.SUBEXP_BEGIN ||
                     instruction.next is not { op: opcode.LOADK } valueConstant ||
                     valueConstant.next is not { op: opcode.SUBEXP_END } valueEnd)
            {
                isConstant = false;
                break;
            }
            else
            {
                value = jv_copy(valueConstant.imm.constant);
                instruction = valueEnd.next;
            }

            if (instruction is not { op: opcode.INSERT } ||
                jv_get_kind(key) != jv_kind.JV_KIND_STRING)
            {
                isConstant = false;
                break;
            }

            result = jv_object_set(result, key, value);
            key = jv_null();
            value = jv_null();
            instruction = instruction.next;
        }

        if (!isConstant)
        {
            jv_free(result);
            jv_free(key);
            jv_free(value);
            return gen_noop();
        }

        block_free(expression);
        return gen_const(result);
    }

    private static block gen_const_array(block expression)
    {
        var allConstant = true;
        var commas = 0;
        var normal = true;
        var result = jv_array();
        for (var instruction = expression.first; instruction is not null; instruction = instruction.next)
        {
            if (instruction.op == opcode.FORK)
            {
                commas++;
                if (instruction.imm.target is not { op: opcode.JUMP } ||
                    jv_array_length(jv_copy(result)) > 0)
                {
                    normal = false;
                    break;
                }
            }
            else if (allConstant && instruction.op == opcode.LOADK)
            {
                if (instruction.next is not null && instruction.next.op != opcode.JUMP)
                {
                    normal = false;
                    break;
                }

                result = jv_array_append(result, jv_copy(instruction.imm.constant));
            }
            else if (instruction.op != opcode.JUMP || instruction.imm.target is not { op: opcode.LOADK })
            {
                allConstant = false;
            }
        }

        if (allConstant && normal &&
            (expression.last is null || expression.last.op == opcode.LOADK) &&
            jv_array_length(jv_copy(result)) == commas + 1)
        {
            block_free(expression);
            return gen_const(result);
        }

        jv_free(result);
        return gen_noop();
    }

    internal static block gen_collect(block expression)
    {
        var constantArray = gen_const_array(expression);
        if (constantArray.first is not null)
        {
            return constantArray;
        }

        var arrayVariable = gen_op_var_fresh(opcode.STOREV, "collect");
        var head = BLOCK(gen_op_simple(opcode.DUP), gen_const(jv_array()), arrayVariable);
        var tail = BLOCK(
            gen_op_bound(opcode.APPEND, arrayVariable),
            gen_op_simple(opcode.BACKTRACK));
        return BLOCK(
            head,
            gen_op_target(opcode.FORK, tail),
            expression,
            tail,
            gen_op_bound(opcode.LOADVN, arrayVariable));
    }

    private static block bind_matcher(block matcher, block body)
    {
        for (var instruction = matcher.first; instruction is not null; instruction = instruction.next)
        {
            if (instruction.op is opcode.STOREV or opcode.STOREVN && instruction.bound_by is null)
            {
                block_bind_subblock(inst_block(instruction), body, OP_HAS_VARIABLE, 0);
            }
        }

        return BLOCK(matcher, body);
    }

    private static void block_get_unbound_vars(block b, ref jv variables)
    {
        RequireCompiler(jv_get_kind(variables) == jv_kind.JV_KIND_OBJECT);
        for (var instruction = b.first; instruction is not null; instruction = instruction.next)
        {
            if (instruction.subfn.first is not null)
            {
                block_get_unbound_vars(instruction.subfn, ref variables);
                continue;
            }

            if (instruction.op is opcode.STOREV or opcode.STOREVN &&
                instruction.bound_by is null)
            {
                variables = jv_object_set(
                    variables,
                    instruction.symbol ?? string.Empty,
                    jv_true());
            }
        }
    }

    private static block bind_alternation_matchers(block matchers, block body)
    {
        var preamble = gen_noop();
        var alternateMatchers = gen_noop();
        var matcherBody = gen_noop();
        var finalMatcher = matchers;
        while (finalMatcher.first is { op: opcode.DESTRUCTURE_ALT })
        {
            block_append(ref alternateMatchers, inst_block(block_take(ref finalMatcher)!));
        }

        if (alternateMatchers.first is null)
        {
            return bind_matcher(finalMatcher, body);
        }

        var allVariables = jv_object();
        block_get_unbound_vars(alternateMatchers, ref allVariables);
        block_get_unbound_vars(finalMatcher, ref allVariables);
        var keys = jv_keys_unsorted(allVariables);
        var keyCount = jv_array_length(jv_copy(keys));
        for (var index = 0; index < keyCount; index++)
        {
            var key = jv_array_get(jv_copy(keys), index);
            var name = jv_string_value(key);
            preamble = BLOCK(
                preamble,
                gen_op_simple(opcode.DUP),
                gen_const(jv_null()),
                gen_op_unbound(opcode.STOREV, name));
            jv_free(key);
        }

        jv_free(keys);
        for (var instruction = alternateMatchers.first;
             instruction is not null;
             instruction = instruction.next)
        {
            var submatcher = instruction.subfn;
            submatcher = BLOCK(submatcher, gen_op_target(opcode.JUMP, finalMatcher));
            matcherBody = BLOCK(
                matcherBody,
                gen_op_target(opcode.DESTRUCTURE_ALT, submatcher),
                submatcher);
            instruction.subfn = gen_noop();
        }

        block_free(alternateMatchers);
        return bind_matcher(preamble, BLOCK(matcherBody, finalMatcher, body));
    }

    internal static block gen_reduce(block source, block matcher, block init, block body)
    {
        var resultVariable = gen_op_var_fresh(opcode.STOREV, "reduce");
        var loop = BLOCK(
            gen_op_simple(opcode.DUPN),
            source,
            bind_alternation_matchers(
                matcher,
                BLOCK(
                    gen_op_bound(opcode.LOADVN, resultVariable),
                    body,
                    gen_op_bound(opcode.STOREV, resultVariable))),
            gen_op_simple(opcode.BACKTRACK));
        return BLOCK(
            gen_op_simple(opcode.DUP),
            init,
            resultVariable,
            gen_op_target(opcode.FORK, loop),
            loop,
            gen_op_bound(opcode.LOADVN, resultVariable));
    }

    internal static block gen_foreach(
        block source,
        block matcher,
        block init,
        block update,
        block extract)
    {
        var stateVariable = gen_op_var_fresh(opcode.STOREV, "foreach");
        return BLOCK(
            gen_op_simple(opcode.DUP),
            init,
            stateVariable,
            gen_op_simple(opcode.DUP),
            source,
            bind_alternation_matchers(
                matcher,
                BLOCK(
                    gen_op_bound(opcode.LOADVN, stateVariable),
                    update,
                    gen_op_simple(opcode.DUP),
                    gen_op_bound(opcode.STOREV, stateVariable),
                    extract)));
    }

    internal static block gen_definedor(block first, block second)
    {
        var foundVariable = gen_op_var_fresh(opcode.STOREV, "found");
        var init = BLOCK(gen_op_simple(opcode.DUP), gen_const(jv_false()), foundVariable);
        var backtrack = gen_op_simple(opcode.BACKTRACK);
        var tail = BLOCK(
            gen_op_simple(opcode.DUP),
            gen_op_bound(opcode.LOADV, foundVariable),
            gen_op_target(opcode.JUMP_F, backtrack),
            backtrack,
            gen_op_simple(opcode.POP),
            second);
        var ifNotFound = gen_op_simple(opcode.BACKTRACK);
        var ifFound = BLOCK(
            gen_op_simple(opcode.DUP),
            gen_const(jv_true()),
            gen_op_bound(opcode.STOREV, foundVariable),
            gen_op_target(opcode.JUMP, tail));
        return BLOCK(
            init,
            gen_op_target(opcode.FORK, ifNotFound),
            first,
            gen_op_target(opcode.JUMP_F, ifFound),
            ifFound,
            ifNotFound,
            tail);
    }

    internal static bool block_has_main(block top)
    {
        for (var instruction = top.first; instruction is not null; instruction = instruction.next)
        {
            if (instruction.op == opcode.TOP)
            {
                return true;
            }
        }

        return false;
    }

    internal static bool block_is_funcdef(block b) => b.first is { op: opcode.CLOSURE_CREATE };

    internal static block gen_condbranch(block ifTrue, block ifFalse)
    {
        ifTrue = BLOCK(ifTrue, gen_op_target(opcode.JUMP, ifFalse));
        return BLOCK(gen_op_target(opcode.JUMP_F, ifTrue), ifTrue, ifFalse);
    }

    internal static block gen_and(block first, block second) =>
        BLOCK(
            gen_op_simple(opcode.DUP),
            first,
            gen_condbranch(
                BLOCK(
                    gen_op_simple(opcode.POP),
                    second,
                    gen_condbranch(gen_const(jv_true()), gen_const(jv_false()))),
                BLOCK(gen_op_simple(opcode.POP), gen_const(jv_false()))));

    internal static block gen_or(block first, block second) =>
        BLOCK(
            gen_op_simple(opcode.DUP),
            first,
            gen_condbranch(
                BLOCK(gen_op_simple(opcode.POP), gen_const(jv_true())),
                BLOCK(
                    gen_op_simple(opcode.POP),
                    second,
                    gen_condbranch(gen_const(jv_true()), gen_const(jv_false())))));

    internal static block gen_destructure_alt(block matcher)
    {
        for (var instruction = matcher.first; instruction is not null; instruction = instruction.next)
        {
            if (instruction.op == opcode.STOREV)
            {
                instruction.op = opcode.STOREVN;
            }
        }

        var alternative = inst_new(opcode.DESTRUCTURE_ALT);
        alternative.subfn = matcher;
        return inst_block(alternative);
    }

    internal static block gen_var_binding(block variable, string name, block body) =>
        gen_destructure(variable, gen_op_unbound(opcode.STOREV, name), body);

    internal static block gen_array_matcher(block left, block current)
    {
        int index;
        if (block_is_noop(left))
        {
            index = 0;
        }
        else
        {
            var first = left.first!;
            var next = first.next;
            RequireCompiler(first.op == opcode.DUP && next is not null);
            inst constant;
            if (next!.op == opcode.PUSHK_UNDER)
            {
                constant = next;
            }
            else
            {
                RequireCompiler(
                    next.op == opcode.SUBEXP_BEGIN &&
                    next.next is { op: opcode.LOADK });
                constant = next.next!;
            }

            index = 1 + (int)jv_number_value(constant.imm.constant);
        }

        return BLOCK(
            gen_op_simple(opcode.DUP),
            gen_subexp(gen_const(jv_number(index))),
            gen_op_simple(opcode.INDEX),
            current,
            left);
    }

    internal static block gen_object_matcher(block name, block current) =>
        BLOCK(
            gen_op_simple(opcode.DUP),
            gen_subexp(name),
            gen_op_simple(opcode.INDEX),
            current);

    internal static block gen_destructure(block variable, block matchers, block body)
    {
        var top = gen_noop();
        if (body.first is { op: opcode.TOP })
        {
            top = inst_block(block_take(ref body)!);
        }

        if (matchers.first is { op: opcode.DESTRUCTURE_ALT })
        {
            block_append(ref variable, gen_op_simple(opcode.DUP));
        }
        else
        {
            top = BLOCK(top, gen_op_simple(opcode.DUP));
        }

        return BLOCK(
            top,
            gen_subexp(variable),
            gen_op_simple(opcode.POP),
            bind_alternation_matchers(matchers, body));
    }

    private static block gen_wildvar_binding(block variable, string name, block body) =>
        BLOCK(
            gen_op_simple(opcode.DUP),
            variable,
            block_bind(
                gen_op_unbound(opcode.STOREV, name),
                body,
                OP_HAS_VARIABLE | OP_BIND_WILDCARD));

    internal static block gen_cond(block condition, block ifTrue, block ifFalse) =>
        BLOCK(
            gen_op_simple(opcode.DUP),
            BLOCK(gen_subexp(condition), gen_op_simple(opcode.POP)),
            gen_condbranch(
                BLOCK(gen_op_simple(opcode.POP), ifTrue),
                BLOCK(gen_op_simple(opcode.POP), ifFalse)));

    internal static block gen_try(block expression, block handler)
    {
        if (block_is_noop(handler))
        {
            handler = BLOCK(gen_op_simple(opcode.DUP), gen_op_simple(opcode.POP));
        }

        var jump = gen_op_target(opcode.JUMP, handler);
        return BLOCK(
            gen_op_target(opcode.TRY_BEGIN, jump),
            expression,
            gen_op_simple(opcode.TRY_END),
            jump,
            handler);
    }

    internal static block gen_label(string label, block expression)
    {
        var condition = gen_call(
            "_equal",
            BLOCK(
                gen_lambda(gen_noop()),
                gen_lambda(gen_op_unbound(opcode.LOADV, label))));
        return gen_wildvar_binding(
            gen_op_simple(opcode.GENLABEL),
            label,
            BLOCK(
                gen_op_simple(opcode.POP),
                gen_try(
                    expression,
                    gen_cond(
                        condition,
                        gen_op_simple(opcode.BACKTRACK),
                        gen_call("error", gen_noop())))));
    }

    internal static block gen_cbinding(cfunction[] cfunctions, int functionCount, block code)
    {
        for (var index = 0; index < functionCount; index++)
        {
            var function = inst_new(opcode.CLOSURE_CREATE_C);
            function.imm.cfunc = cfunctions[index];
            function.symbol = cfunctions[index].name;
            function.nformals = cfunctions[index].nargs - 1;
            function.any_unbound = 0;
            code = BLOCK(inst_block(function), code);
        }

        return code;
    }

    private static ushort nesting_level(bytecode code, inst target)
    {
        var level = 0;
        RequireCompiler(
            target.compiled is not null,
            $"Binding {target.op} {target.symbol ?? "<anonymous>"}/{target.nformals} " +
            $"(referenced={target.referenced}) was not compiled into an enclosing bytecode frame.");
        bytecode? current = code;
        while (current is not null && !ReferenceEquals(target.compiled, current))
        {
            level++;
            current = current.parent;
        }

        RequireCompiler(current is not null && ReferenceEquals(current, target.compiled));
        return checked((ushort)level);
    }

    private static int count_cfunctions(block b)
    {
        var count = 0;
        for (var instruction = b.first; instruction is not null; instruction = instruction.next)
        {
            if (instruction.op == opcode.CLOSURE_CREATE_C)
            {
                count++;
            }

            count += count_cfunctions(instruction.subfn);
        }

        return count;
    }

    private static jv make_env(jv environment)
    {
        if (jv_is_valid(environment))
        {
            return jv_copy(environment);
        }

        var result = jv_object();
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var key = Convert.ToString(entry.Key, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            var value = Convert.ToString(entry.Value, System.Globalization.CultureInfo.InvariantCulture);
            result = value is null
                ? jv_object_delete(result, key)
                : jv_object_set(result, key, jv_string(value));
        }

        // The native assignment stores the same returned handle in both env
        // and imm.constant. jv_copy() supplies those two logical owners.
        return jv_copy(result);
    }

    private static int expand_call_arglist(ref block b, jv args, ref jv environment)
    {
        var errors = 0;
        var result = gen_noop();
        inst? current;
        while ((current = block_take(ref b)) is not null)
        {
            if ((opcode_describe(current.op).flags & OP_HAS_BINDING) != 0)
            {
                if (current.bound_by is null &&
                    current.op == opcode.LOADV &&
                    string.Equals(current.symbol, "ENV", StringComparison.Ordinal))
                {
                    current.op = opcode.LOADK;
                    environment = current.imm.constant = make_env(environment);
                    current.is_environment_constant = true;
                }
                else if (current.bound_by is null &&
                         current.op == opcode.LOADV &&
                         jv_object_has(jv_copy(args), jv_string(current.symbol ?? string.Empty)))
                {
                    current.op = opcode.LOADK;
                    current.imm.constant = jv_object_get(
                        jv_copy(args),
                        jv_string(current.symbol ?? string.Empty));
                }
                else if (current.bound_by is null)
                {
                    var symbol = current.symbol ?? string.Empty;
                    if (current.locfile is not null)
                    {
                        if (symbol is { Length: 2 } &&
                            symbol[0] == '*' &&
                            symbol[1] is >= '1' and <= '3')
                        {
                            locfile_locate(
                                current.locfile,
                                current.source,
                                "jq: error: break used outside labeled control structure");
                        }
                        else if (current.op == opcode.LOADV)
                        {
                            locfile_locate(
                                current.locfile,
                                current.source,
                                "jq: error: $%s is not defined",
                                symbol);
                        }
                        else
                        {
                            locfile_locate(
                                current.locfile,
                                current.source,
                                "jq: error: %s/%d is not defined",
                                symbol,
                                current.nactuals);
                        }
                    }

                    errors++;
                    result = BLOCK(result, inst_block(current));
                    continue;
                }
            }

            var prelude = gen_noop();
            if (current.op == opcode.CALL_JQ)
            {
                var actualArguments = 0;
                var desiredArguments = 0;
                var binding = current.bound_by ??
                    throw new InvalidOperationException("CALL_JQ must be bound before lowering.");
                switch (binding.op)
                {
                    case opcode.CLOSURE_CREATE:
                    case opcode.CLOSURE_PARAM:
                    {
                        var callArguments = gen_noop();
                        inst? argument;
                        while ((argument = block_take(ref current.arglist)) is not null)
                        {
                            RequireCompiler(
                                (opcode_describe(argument.op).flags & OP_IS_CALL_PSEUDO) != 0);
                            var argumentBlock = inst_block(argument);
                            switch (argument.op)
                            {
                                case opcode.CLOSURE_REF:
                                    block_append(ref callArguments, argumentBlock);
                                    break;
                                case opcode.CLOSURE_CREATE:
                                    block_append(ref prelude, argumentBlock);
                                    block_append(
                                        ref callArguments,
                                        gen_op_bound(opcode.CLOSURE_REF, argumentBlock));
                                    break;
                                default:
                                    throw new InvalidOperationException("Unknown jq call parameter type.");
                            }

                            actualArguments++;
                        }

                        current.imm.intval = checked((ushort)actualArguments);
                        current.arglist = callArguments;
                        if (binding.op == opcode.CLOSURE_CREATE)
                        {
                            for (var formal = binding.arglist.first;
                                 formal is not null;
                                 formal = formal.next)
                            {
                                RequireCompiler(formal.op == opcode.CLOSURE_PARAM);
                                desiredArguments++;
                            }
                        }

                        break;
                    }

                    case opcode.CLOSURE_CREATE_C:
                    {
                        inst? argument;
                        while ((argument = block_take(ref current.arglist)) is not null)
                        {
                            RequireCompiler(argument.op == opcode.CLOSURE_CREATE);
                            var body = argument.subfn;
                            argument.subfn = gen_noop();
                            inst_free(argument);
                            errors += expand_call_arglist(ref body, args, ref environment);
                            prelude = BLOCK(gen_subexp(body), prelude);
                            actualArguments++;
                        }

                        current.op = opcode.CALL_BUILTIN;
                        current.imm.intval = checked((ushort)(actualArguments + 1));
                        desiredArguments = (binding.imm.cfunc ??
                            throw new InvalidOperationException("C binding has no function."))
                            .nargs - 1;
                        RequireCompiler(current.arglist.first is null);
                        break;
                    }

                    default:
                        throw new InvalidOperationException("Unknown jq function binding type.");
                }

                RequireCompiler(actualArguments == desiredArguments);
            }

            result = BLOCK(result, prelude, inst_block(current));
        }

        b = result;
        return errors;
    }

    private static int compile(bytecode code, block b, locfile source, jv args, ref jv environment)
    {
        var errors = expand_call_arglist(ref b, args, ref environment);
        b = BLOCK(b, gen_op_simple(opcode.RET));
        var position = 0;
        var variableFrameIndex = 0;
        var subfunctionCount = 0;
        var localNames = jv_array();

        for (var current = b.first; current is not null; current = current.next)
        {
            if (current.next is null)
            {
                RequireCompiler(ReferenceEquals(current, b.last));
            }

            var length = opcode_describe(current.op).length;
            if (current.op == opcode.CALL_JQ)
            {
                for (var argument = current.arglist.first;
                     argument is not null;
                     argument = argument.next)
                {
                    length += 2;
                }
            }

            position += length;
            current.bytecode_pos = position;
            current.compiled = code;
            RequireCompiler(current.op is not opcode.CLOSURE_REF and not opcode.CLOSURE_PARAM);

            if ((opcode_describe(current.op).flags & OP_HAS_VARIABLE) != 0 &&
                ReferenceEquals(current.bound_by, current))
            {
                current.imm.intval = checked((ushort)variableFrameIndex++);
                localNames = jv_array_append(
                    localNames,
                    jv_string(current.symbol ?? string.Empty));
            }

            if (current.op == opcode.CLOSURE_CREATE)
            {
                RequireCompiler(ReferenceEquals(current.bound_by, current));
                if (subfunctionCount >= ARG_NEWCLOSURE)
                {
                    locfile_locate(
                        source,
                        current.source,
                        "too many function parameters or local function definitions (max %d)",
                        ARG_NEWCLOSURE - 1);
                    errors++;
                    break;
                }

                current.imm.intval = checked((ushort)subfunctionCount++);
            }

            if (current.op == opcode.CLOSURE_CREATE_C)
            {
                RequireCompiler(ReferenceEquals(current.bound_by, current));
                var globals = code.globals ??
                    throw new InvalidOperationException("Root bytecode has no symbol table.");
                var index = globals.ncfunctions++;
                RequireCompiler(index < globals.cfunctions.Length);
                globals.cfunc_names = jv_array_append(
                    globals.cfunc_names,
                    jv_string(current.symbol ?? string.Empty));
                globals.cfunctions[index] = current.imm.cfunc ??
                    throw new InvalidOperationException("C binding has no function.");
                current.imm.intval = checked((ushort)index);
            }
        }

        if (position > ushort.MaxValue)
        {
            locfile_locate(
                source,
                UNKNOWN_LOCATION,
                "function compiled to %d bytes which is too long",
                position);
            errors++;
        }

        code.debuginfo = jv_object_set(code.debuginfo, "locals", localNames);
        if (subfunctionCount != 0 && errors == 0)
        {
            var subfunctions = new bytecode[subfunctionCount];
            code.subfunctions = subfunctions;
            for (var current = b.first; current is not null; current = current.next)
            {
                if (current.op != opcode.CLOSURE_CREATE)
                {
                    continue;
                }

                var subfunction = new bytecode
                {
                    globals = code.globals,
                    parent = code,
                    nclosures = 0,
                    debuginfo = jv_object_set(
                        jv_object(),
                        "name",
                        jv_string(current.symbol ?? string.Empty)),
                };
                subfunctions[current.imm.intval] = subfunction;
                var parameters = jv_array();
                for (var parameter = current.arglist.first;
                     parameter is not null;
                     parameter = parameter.next)
                {
                    RequireCompiler(parameter.op == opcode.CLOSURE_PARAM);
                    RequireCompiler(ReferenceEquals(parameter.bound_by, parameter));
                    if (subfunction.nclosures >= ARG_NEWCLOSURE)
                    {
                        locfile_locate(
                            source,
                            current.source,
                            "function has too many parameters (max %d)",
                            ARG_NEWCLOSURE - 1);
                        errors++;
                        break;
                    }

                    parameter.imm.intval = checked((ushort)subfunction.nclosures++);
                    parameter.compiled = subfunction;
                    parameters = jv_array_append(
                        parameters,
                        jv_string(parameter.symbol ?? string.Empty));
                }

                subfunction.debuginfo = jv_object_set(
                    subfunction.debuginfo,
                    "params",
                    parameters);
                errors += compile(subfunction, current.subfn, source, args, ref environment);
                current.subfn = gen_noop();
            }
        }
        else
        {
            code.subfunctions = [];
        }

        code.code = new ushort[position];
        position = 0;
        var constantPool = jv_array();
        var environmentConstantIndexes = new List<int>();
        var maximumVariable = -1;
        if (errors == 0)
        {
            for (var current = b.first; current is not null; current = current.next)
            {
                var description = opcode_describe(current.op);
                if (description.length == 0)
                {
                    continue;
                }

                code.code[position++] = (ushort)current.op;
                RequireCompiler(current.op is not opcode.CLOSURE_REF and not opcode.CLOSURE_PARAM);
                if (current.op == opcode.CALL_BUILTIN)
                {
                    var binding = current.bound_by ??
                        throw new InvalidOperationException("CALL_BUILTIN has no binding.");
                    RequireCompiler(binding.op == opcode.CLOSURE_CREATE_C);
                    RequireCompiler(current.arglist.first is null);
                    code.code[position++] = current.imm.intval;
                    code.code[position++] = binding.imm.intval;
                }
                else if (current.op == opcode.CALL_JQ)
                {
                    var binding = current.bound_by ??
                        throw new InvalidOperationException("CALL_JQ has no binding.");
                    RequireCompiler(binding.op is opcode.CLOSURE_CREATE or opcode.CLOSURE_PARAM);
                    code.code[position++] = current.imm.intval;
                    code.code[position++] = nesting_level(code, binding);
                    code.code[position++] = (ushort)(
                        binding.imm.intval |
                        (binding.op == opcode.CLOSURE_CREATE ? ARG_NEWCLOSURE : 0));
                    for (var argument = current.arglist.first;
                         argument is not null;
                         argument = argument.next)
                    {
                        RequireCompiler(
                            argument.op == opcode.CLOSURE_REF &&
                            argument.bound_by is { op: opcode.CLOSURE_CREATE });
                        code.code[position++] = nesting_level(code, argument.bound_by!);
                        code.code[position++] = (ushort)(argument.bound_by!.imm.intval | ARG_NEWCLOSURE);
                    }
                }
                else if ((description.flags & OP_HAS_CONSTANT) != 0 &&
                         (description.flags & OP_HAS_VARIABLE) != 0)
                {
                    var poolIndex = jv_array_length(jv_copy(constantPool));
                    code.code[position++] = checked((ushort)poolIndex);
                    constantPool = jv_array_append(
                        constantPool,
                        jv_copy(current.imm.constant));
                    var binding = current.bound_by ??
                        throw new InvalidOperationException("Global constant has no variable binding.");
                    code.code[position++] = nesting_level(code, binding);
                    var variable = binding.imm.intval;
                    code.code[position++] = variable;
                    maximumVariable = Math.Max(maximumVariable, variable);
                }
                else if ((description.flags & OP_HAS_CONSTANT) != 0)
                {
                    var poolIndex = jv_array_length(jv_copy(constantPool));
                    code.code[position++] = checked((ushort)poolIndex);
                    constantPool = jv_array_append(
                        constantPool,
                        jv_copy(current.imm.constant));
                    if (current.is_environment_constant)
                    {
                        environmentConstantIndexes.Add(poolIndex);
                    }
                }
                else if ((description.flags & OP_HAS_VARIABLE) != 0)
                {
                    var binding = current.bound_by ??
                        throw new InvalidOperationException("Variable operation has no binding.");
                    code.code[position++] = nesting_level(code, binding);
                    var variable = binding.imm.intval;
                    code.code[position++] = variable;
                    maximumVariable = Math.Max(maximumVariable, variable);
                }
                else if ((description.flags & OP_HAS_BRANCH) != 0)
                {
                    var target = current.imm.target ??
                        throw new InvalidOperationException("Branch has no target.");
                    RequireCompiler(target.bytecode_pos != -1 && target.bytecode_pos > position);
                    code.code[position] = checked((ushort)(target.bytecode_pos - (position + 1)));
                    position++;
                }
                else if (description.length > 1)
                {
                    throw new InvalidOperationException("Code generation is not implemented for this opcode.");
                }
            }
        }

        code.constants = constantPool;
        code.environment_constant_indexes = [.. environmentConstantIndexes];
        code.has_environment_constant_slots = environmentConstantIndexes.Count != 0;
        if (!code.has_environment_constant_slots)
        {
            foreach (var subfunction in code.subfunctions)
            {
                if (subfunction.has_environment_constant_slots)
                {
                    code.has_environment_constant_slots = true;
                    break;
                }
            }
        }

        code.nlocals = maximumVariable + 2;
        block_free(b);
        return errors;
    }

    internal static int block_compile(block b, out bytecode? output, locfile source, jv args)
    {
        var functionCount = count_cfunctions(b);
        var globals = new symbol_table
        {
            ncfunctions = 0,
            cfunctions = new cfunction[Math.Max(functionCount, 1)],
            cfunc_names = jv_array(),
        };
        var code = new bytecode
        {
            parent = null,
            nclosures = 0,
            globals = globals,
            debuginfo = jv_object_set(jv_object(), "name", jv_null()),
        };
        var environment = jv_invalid();
        var errors = compile(code, b, source, args, ref environment);
        if (jv_is_valid(environment))
        {
            code.compiled_environment = jv_copy(environment);
        }

        jv_free(args);
        jv_free(environment);
        RequireCompiler(globals.ncfunctions == functionCount);
        if (errors > 0)
        {
            bytecode_free(code);
            output = null;
        }
        else
        {
            output = code;
        }

        return errors;
    }

    internal static void block_free(block b)
    {
        inst? instruction;
        while ((instruction = block_take(ref b)) is not null)
        {
            inst_free(instruction);
        }
    }

    private static void RequireCompiler(bool condition, string? message = null)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message ?? "jq compiler invariant failed.");
        }
    }
}
