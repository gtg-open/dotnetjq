// DOTNETJQ PORT MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream file: src/builtin.c
// Upstream URL: https://github.com/jqlang/jq/blob/jq-1.8.2/src/builtin.c
// Strategy: PORT
// Target file: src/DotNetJq/Port/src/builtin.c.cs
// Substitutions: managed arity-specific delegates replace C function pointers; System.Math,
// System.Text.RegularExpressions, managed time compatibility code, and host-capability callbacks
// replace the corresponding native platform APIs. builtins_bind(block) parses and binds the
// byte-exact builtin.jq resource and this function_list into bytecode directly.
// CALL OWNERSHIP MAP: compile.c lowers C-function arguments through reversed gen_subexp preludes;
// execute.c.cs pops owned arguments, transfers the result, and moves a messaged invalid into the
// error slot. jq/filter-valued CALL_JQ parameters remain compiled closures rather than C values.
// This rule applies on every OS; see porting/REFERENCE_COUNT_OWNERSHIP_MAP.md.
// Known differences: platform-specific libm functions, process I/O, time, and regular expressions are supplied by separate compatibility proxies.
// Source-helper mapping: escape_string and f_format retain their jq-shaped byte/refcount control
// flow below; f_match_name_iter is implemented by JqRegex's named-capture enumeration; tm2jv and
// jv2tm retain their upstream names in JqTimeBuiltinProxy. my_mktime/set_tm_wday/set_tm_yday and
// the temporary setenv(TZ) branch are folded into that proxy's explicit civil-time/time-zone
// calculations because CLR has no mutable `struct tm`. Default execution observes ambient process
// environment/time state; the opt-in managed Environment capability is isolated without mutating TZ.

using System.Globalization;
using System.Buffers.Binary;
using System.Text;
using DotNetJq.Compatibility.Regex;
using DotNetJq.Compatibility.Time;

namespace DotNetJq.Port;

// jq-1.8.2 src/builtin.c:function_list and its C-call boundary.  `nargs`
// includes jq's implicit input slot.  Every delegate below therefore receives
// owned jv arguments and returns exactly one owned jv, matching
// execute.c:CALL_BUILTIN.
internal static partial class libjq
{
    internal const string JqBuiltinResourceName = "DotNetJq.Resources.builtin.jq";

    private static cfunction C1(cfunction_a1 function, string name) =>
        new(new cfunction_ptr { a1 = function }, name, 1);

    private static cfunction C2(cfunction_a2 function, string name) =>
        new(new cfunction_ptr { a2 = function }, name, 2);

    private static cfunction C3(cfunction_a3 function, string name) =>
        new(new cfunction_ptr { a3 = function }, name, 3);

    private static cfunction C4(cfunction_a4 function, string name) =>
        new(new cfunction_ptr { a4 = function }, name, 4);

    // Keep this order byte-for-byte comparable with jq-1.8.2's table after
    // src/libm.h macro expansion.  jn/yn are included because the managed
    // libm compatibility surface supplies both symbols on every target.
    internal static readonly cfunction[] function_list =
    [
        C1(f_acos, "acos"), C1(f_acosh, "acosh"), C1(f_asin, "asin"),
        C1(f_asinh, "asinh"), C1(f_atan, "atan"), C3(f_atan2, "atan2"),
        C1(f_atanh, "atanh"), C1(f_cbrt, "cbrt"), C1(f_cos, "cos"),
        C1(f_cosh, "cosh"), C1(f_exp, "exp"), C1(f_exp2, "exp2"),
        C1(f_floor, "floor"), C3(f_hypot, "hypot"), C1(f_j0, "j0"),
        C1(f_j1, "j1"), C1(f_log, "log"), C1(f_log10, "log10"),
        C1(f_log2, "log2"), C3(f_pow, "pow"), C3(f_remainder, "remainder"),
        C1(f_sin, "sin"), C1(f_sinh, "sinh"), C1(f_sqrt, "sqrt"),
        C1(f_tan, "tan"), C1(f_tanh, "tanh"), C1(f_tgamma, "tgamma"),
        C1(f_y0, "y0"), C1(f_y1, "y1"), C3(f_jn, "jn"), C3(f_yn, "yn"),
        C1(f_ceil, "ceil"), C3(f_copysign, "copysign"), C3(f_drem, "drem"),
        C1(f_erf, "erf"), C1(f_erfc, "erfc"), C1(f_exp10, "exp10"),
        C1(f_expm1, "expm1"), C1(f_fabs, "fabs"), C3(f_fdim, "fdim"),
        C4(f_fma, "fma"), C3(f_fmax, "fmax"), C3(f_fmin, "fmin"),
        C3(f_fmod, "fmod"), C1(f_gamma, "gamma"), C1(f_lgamma, "lgamma"),
        C1(f_log1p, "log1p"), C1(f_logb, "logb"), C1(f_nearbyint, "nearbyint"),
        C3(f_nextafter, "nextafter"), C3(f_nexttoward, "nexttoward"),
        C1(f_rint, "rint"), C1(f_round, "round"), C3(f_scalb, "scalb"),
        C3(f_scalbln, "scalbln"), C1(f_significand, "significand"),
        C1(f_trunc, "trunc"), C3(f_ldexp, "ldexp"), C1(f_modf, "modf"),
        C1(f_frexp, "frexp"), C1(f_lgamma_r, "lgamma_r"),

        C1(f_negate, "_negate"),
        C3(f_plus, "_plus"), C3(f_minus, "_minus"),
        C3(f_multiply, "_multiply"), C3(f_divide, "_divide"),
        C3(f_mod, "_mod"), C3(f_equal, "_equal"),
        C3(f_notequal, "_notequal"), C3(f_less, "_less"),
        C3(f_lesseq, "_lesseq"), C3(f_greater, "_greater"),
        C3(f_greatereq, "_greatereq"),
        C1(f_dump, "tojson"), C1(f_json_parse, "fromjson"),
        C1(f_tonumber, "tonumber"), C1(f_toboolean, "toboolean"),
        C1(f_tostring, "tostring"), C1(f_keys, "keys"),
        C1(f_keys_unsorted, "keys_unsorted"), C2(f_startswith, "startswith"),
        C2(f_endswith, "endswith"), C2(f_string_split, "split"),
        C1(f_string_explode, "explode"), C1(f_string_implode, "implode"),
        C2(f_string_indexes, "_strindices"), C1(f_string_trim, "trim"),
        C1(f_string_ltrim, "ltrim"), C1(f_string_rtrim, "rtrim"),
        C3(f_setpath, "setpath"), C2(f_getpath, "getpath"),
        C2(f_delpaths, "delpaths"), C2(f_has, "has"),
        C2(f_contains, "contains"), C1(f_length, "length"),
        C1(f_utf8bytelength, "utf8bytelength"), C1(f_type, "type"),
        C1(f_isinfinite, "isinfinite"), C1(f_isnan, "isnan"),
        C1(f_isnormal, "isnormal"), C1(f_infinite, "infinite"),
        C1(f_nan, "nan"), C1(f_sort, "sort"),
        C2(f_sort_by_impl, "_sort_by_impl"), C2(f_group_by_impl, "_group_by_impl"),
        C1(f_unique, "unique"), C2(f_unique_by_impl, "_unique_by_impl"),
        C2(f_bsearch, "bsearch"), C1(f_min, "min"), C1(f_max, "max"),
        C2(f_min_by_impl, "_min_by_impl"), C2(f_max_by_impl, "_max_by_impl"),
        C1(f_error, "error"), C2(f_format, "format"), C1(f_env, "env"),
        C1(f_halt, "halt"), C2(f_halt_error, "halt_error"),
        C1(f_get_search_list, "get_search_list"),
        C1(f_get_prog_origin, "get_prog_origin"), C1(f_get_jq_origin, "get_jq_origin"),
        C4(f_match, "_match_impl"), C1(f_modulemeta, "modulemeta"),
        C1(f_input, "input"), C1(f_debug, "debug"), C1(f_stderr, "stderr"),
        C2(f_strptime, "strptime"), C2(f_strftime, "strftime"),
        C2(f_strflocaltime, "strflocaltime"), C1(f_mktime, "mktime"),
        C1(f_gmtime, "gmtime"), C1(f_localtime, "localtime"), C1(f_now, "now"),
        C1(f_current_filename, "input_filename"),
        C1(f_current_line, "input_line_number"),
        C1(f_have_decnum, "have_decnum"), C1(f_have_decnum, "have_literal_numbers"),
    ];

    // This is a hack to make last(g) yield no output values,
    // if g yields no output values, without using boxing.
    private static block gen_last_1()
    {
        var last_var = gen_op_var_fresh(opcode.STOREV, "last");
        var is_empty_var = gen_op_var_fresh(opcode.STOREV, "is_empty");
        var init = BLOCK(
            gen_op_simple(opcode.DUP),
            gen_const(jv_null()),
            last_var,
            gen_op_simple(opcode.DUP),
            gen_const(jv_true()),
            is_empty_var);
        var call_arg = BLOCK(
            gen_call("arg", gen_noop()),
            gen_op_simple(opcode.DUP),
            gen_op_bound(opcode.STOREV, last_var),
            gen_const(jv_false()),
            gen_op_bound(opcode.STOREV, is_empty_var),
            gen_op_simple(opcode.BACKTRACK));
        var if_empty = gen_op_simple(opcode.BACKTRACK);
        return BLOCK(
            init,
            gen_op_target(opcode.FORK, call_arg),
            call_arg,
            BLOCK(
                gen_op_bound(opcode.LOADVN, is_empty_var),
                gen_op_target(opcode.JUMP_F, if_empty),
                if_empty,
                gen_op_bound(opcode.LOADVN, last_var)));
    }

    private static block bind_bytecoded_builtins(block b)
    {
        var builtins = gen_noop();
        builtins = BLOCK(
            builtins,
            gen_function("empty", gen_noop(), gen_op_simple(opcode.BACKTRACK)));
        builtins = BLOCK(
            builtins,
            gen_function(
                "not",
                gen_noop(),
                gen_condbranch(gen_const(jv_false()), gen_const(jv_true()))));

        builtins = BLOCK(
            builtins,
            gen_function(
                "path",
                gen_param("arg"),
                BLOCK(
                    gen_op_simple(opcode.PATH_BEGIN),
                    gen_call("arg", gen_noop()),
                    gen_op_simple(opcode.PATH_END))));
        builtins = BLOCK(
            builtins,
            gen_function("last", gen_param("arg"), gen_last_1()));

        // Note that we can now define `range` as a jq-coded function.
        var rangevar = gen_op_var_fresh(opcode.STOREV, "rangevar");
        var rangestart = gen_op_var_fresh(opcode.STOREV, "rangestart");
        var range = BLOCK(
            gen_op_simple(opcode.DUP),
            gen_call("start", gen_noop()),
            rangestart,
            gen_call("end", gen_noop()),
            gen_op_simple(opcode.DUP),
            gen_op_bound(opcode.LOADV, rangestart),
            rangevar,
            gen_op_bound(opcode.RANGE, rangevar));
        builtins = BLOCK(
            builtins,
            gen_function(
                "range",
                BLOCK(gen_param("start"), gen_param("end")),
                range));

        return BLOCK(builtins, b);
    }

    private static readonly byte[] jq_builtins = load_jq_builtins();

    private static byte[] load_jq_builtins()
    {
        var assembly = typeof(libjq).Assembly;
        using var stream = assembly.GetManifestResourceStream(JqBuiltinResourceName) ??
            throw new InvalidOperationException(
                $"Embedded jq builtin library '{JqBuiltinResourceName}' was not found in " +
                $"assembly '{assembly.GetName().Name}'.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static block gen_builtin_list(block builtins)
    {
        var list = jv_array_append(block_list_funcs(builtins, 1), jv_string("builtins/0"));
        return BLOCK(
            builtins,
            gen_function("builtins", gen_noop(), gen_const(list)));
    }

    internal static int builtins_bind(jq_state jq, ref block bb)
    {
        ArgumentNullException.ThrowIfNull(jq);

        var src = locfile_init(jq, "<builtin>", jq_builtins, jq_builtins.Length);
        block builtins;
        int nerrors;
        try
        {
            nerrors = jq_parse_library(src, out builtins);
        }
        finally
        {
            locfile_free(src);
        }

        if (nerrors != 0)
        {
            block_free(builtins);
            return nerrors;
        }

        builtins = bind_bytecoded_builtins(builtins);
        builtins = gen_cbinding(function_list, function_list.Length, builtins);
        builtins = gen_builtin_list(builtins);

        bb = block_bind_referenced(builtins, bb, OP_IS_CALL_PSEUDO);
        return nerrors;
    }

    private static jv type_error(jv bad, string message)
    {
        // jq-1.8.2 src/builtin.c:type_error passes its owned value straight to
        // jv_dump_string_trunc(); that call consumes it.
        var text = $"{jv_kind_name(bad.Kind)} ({jv_dump_string_trunc(bad, 30)}) {message}";
        return jv_invalid_with_msg(jv_string(text));
    }

    private static jv type_error2(jv bad1, jv bad2, string message)
    {
        // Both truncating dumps consume the same two owners as upstream.
        var text = $"{jv_kind_name(bad1.Kind)} ({jv_dump_string_trunc(bad1, 30)}) and " +
            $"{jv_kind_name(bad2.Kind)} ({jv_dump_string_trunc(bad2, 30)}) {message}";
        return jv_invalid_with_msg(jv_string(text));
    }

    private static jv ret_error(jv bad, string message)
    {
        jv_free(bad);
        return jv_invalid_with_msg(jv_string(message));
    }

    private static jv ret_error2(jv bad1, jv bad2, string message)
    {
        jv_free(bad1);
        jv_free(bad2);
        return jv_invalid_with_msg(jv_string(message));
    }

    private static jv c_runtime_error(JqRuntimeException exception)
    {
        var error = exception.TakeErrorValue() ?? jv_string(exception.Message);
        return jv_invalid_with_msg(error);
    }

    private static jv c_math1(jv input, Func<double, double> operation)
    {
        if (input.Kind != jv_kind.JV_KIND_NUMBER)
        {
            return type_error(input, "number required");
        }

        var result = jv_number(operation(input.NumberValue));
        jv_free(input);
        return result;
    }

    private static jv c_math2(jv input, jv first, jv second, Func<double, double, double> operation)
    {
        jv_free(input);
        if (first.Kind != jv_kind.JV_KIND_NUMBER)
        {
            jv_free(second);
            return type_error(first, "number required");
        }

        if (second.Kind != jv_kind.JV_KIND_NUMBER)
        {
            jv_free(first);
            return type_error(second, "number required");
        }

        var result = jv_number(operation(first.NumberValue, second.NumberValue));
        jv_free(first);
        jv_free(second);
        return result;
    }

    private static jv c_math3(
        jv input,
        jv first,
        jv second,
        jv third,
        Func<double, double, double, double> operation)
    {
        jv_free(input);
        if (first.Kind != jv_kind.JV_KIND_NUMBER)
        {
            jv_free(second);
            jv_free(third);
            return type_error(first, "number required");
        }

        if (second.Kind != jv_kind.JV_KIND_NUMBER)
        {
            jv_free(first);
            jv_free(third);
            return type_error(second, "number required");
        }

        if (third.Kind != jv_kind.JV_KIND_NUMBER)
        {
            jv_free(first);
            jv_free(second);
            return type_error(third, "number required");
        }

        var result = jv_number(operation(first.NumberValue, second.NumberValue, third.NumberValue));
        jv_free(first);
        jv_free(second);
        jv_free(third);
        return result;
    }

    private static jv f_acos(jq_state jq, jv a) => c_math1(a, Math.Acos);
    private static jv f_acosh(jq_state jq, jv a) => c_math1(a, jq_acosh);
    private static jv f_asin(jq_state jq, jv a) => c_math1(a, Math.Asin);
    private static jv f_asinh(jq_state jq, jv a) => c_math1(a, jq_asinh);
    private static jv f_atan(jq_state jq, jv a) => c_math1(a, Math.Atan);
    private static jv f_atanh(jq_state jq, jv a) => c_math1(a, jq_atanh);
    private static jv f_cbrt(jq_state jq, jv a) => c_math1(a, Math.Cbrt);
    private static jv f_cos(jq_state jq, jv a) => c_math1(a, Math.Cos);
    private static jv f_cosh(jq_state jq, jv a) => c_math1(a, Math.Cosh);
    private static jv f_exp(jq_state jq, jv a) => c_math1(a, Math.Exp);
    private static jv f_exp2(jq_state jq, jv a) => c_math1(a, static x => Math.Pow(2, x));
    private static jv f_floor(jq_state jq, jv a) => c_math1(a, Math.Floor);
    private static jv f_j0(jq_state jq, jv a) => c_math1(a, jq_j0);
    private static jv f_j1(jq_state jq, jv a) => c_math1(a, jq_j1);
    private static jv f_log(jq_state jq, jv a) => c_math1(a, Math.Log);
    private static jv f_log10(jq_state jq, jv a) => c_math1(a, Math.Log10);
    private static jv f_log2(jq_state jq, jv a) => c_math1(a, Math.Log2);
    private static jv f_sin(jq_state jq, jv a) => c_math1(a, Math.Sin);
    private static jv f_sinh(jq_state jq, jv a) => c_math1(a, Math.Sinh);
    private static jv f_sqrt(jq_state jq, jv a) => c_math1(a, Math.Sqrt);
    private static jv f_tan(jq_state jq, jv a) => c_math1(a, Math.Tan);
    private static jv f_tanh(jq_state jq, jv a) => c_math1(a, Math.Tanh);
    private static jv f_tgamma(jq_state jq, jv a) => c_math1(a, jq_tgamma);
    private static jv f_y0(jq_state jq, jv a) => c_math1(a, jq_y0);
    private static jv f_y1(jq_state jq, jv a) => c_math1(a, jq_y1);
    private static jv f_ceil(jq_state jq, jv a) => c_math1(a, Math.Ceiling);
    private static jv f_erf(jq_state jq, jv a) => c_math1(a, jq_erf);
    private static jv f_erfc(jq_state jq, jv a) => c_math1(a, jq_erfc);
    private static jv f_exp10(jq_state jq, jv a) => c_math1(a, static x => Math.Pow(10, x));
    private static jv f_expm1(jq_state jq, jv a) => c_math1(a, jq_expm1);
    private static jv f_fabs(jq_state jq, jv a) => c_math1(a, Math.Abs);
    private static jv f_gamma(jq_state jq, jv a) => c_math1(a, jq_gamma);
    private static jv f_lgamma(jq_state jq, jv a) => c_math1(a, jq_lgamma);
    private static jv f_log1p(jq_state jq, jv a) => c_math1(a, jq_log1p);
    private static jv f_logb(jq_state jq, jv a) =>
        c_math1(a, static x => Math.Floor(Math.Log2(Math.Abs(x))));
    private static jv f_nearbyint(jq_state jq, jv a) =>
        c_math1(a, static x => Math.Round(x, MidpointRounding.ToEven));
    private static jv f_rint(jq_state jq, jv a) =>
        c_math1(a, static x => Math.Round(x, MidpointRounding.ToEven));
    private static jv f_round(jq_state jq, jv a) =>
        c_math1(a, static x => Math.Round(x, MidpointRounding.AwayFromZero));
    private static jv f_trunc(jq_state jq, jv a) => c_math1(a, Math.Truncate);

    private static jv f_atan2(jq_state jq, jv input, jv a, jv b) =>
        c_math2(input, a, b, Math.Atan2);
    private static jv f_hypot(jq_state jq, jv input, jv a, jv b) =>
        c_math2(input, a, b, c_hypot);
    private static jv f_pow(jq_state jq, jv input, jv a, jv b) =>
        c_math2(input, a, b, Math.Pow);
    private static jv f_remainder(jq_state jq, jv input, jv a, jv b) =>
        c_math2(input, a, b, Math.IEEERemainder);
    private static jv f_jn(jq_state jq, jv input, jv a, jv b) =>
        c_math2(input, a, b, static (order, x) => jq_jn(unchecked((int)order), x));
    private static jv f_yn(jq_state jq, jv input, jv a, jv b) =>
        c_math2(input, a, b, static (order, x) => jq_yn(unchecked((int)order), x));
    private static jv f_copysign(jq_state jq, jv input, jv a, jv b) =>
        c_math2(input, a, b, Math.CopySign);
    private static jv f_drem(jq_state jq, jv input, jv a, jv b) =>
        c_math2(input, a, b, Math.IEEERemainder);
    private static jv f_fdim(jq_state jq, jv input, jv a, jv b) =>
        c_math2(input, a, b, static (x, y) => Math.Max(x - y, 0));
    private static jv f_fmax(jq_state jq, jv input, jv a, jv b) =>
        c_math2(input, a, b, c_fmax);
    private static jv f_fmin(jq_state jq, jv input, jv a, jv b) =>
        c_math2(input, a, b, c_fmin);
    private static jv f_fmod(jq_state jq, jv input, jv a, jv b) =>
        c_math2(input, a, b, static (x, y) => x % y);
    private static jv f_nextafter(jq_state jq, jv input, jv a, jv b) =>
        c_math2(input, a, b, c_nextafter);
    private static jv f_nexttoward(jq_state jq, jv input, jv a, jv b) =>
        c_math2(input, a, b, c_nextafter);
    private static jv f_scalb(jq_state jq, jv input, jv a, jv b) =>
        c_math2(input, a, b, static (x, y) => Math.ScaleB(x, unchecked((int)y)));
    private static jv f_scalbln(jq_state jq, jv input, jv a, jv b) =>
        c_math2(input, a, b, static (x, y) => Math.ScaleB(x, unchecked((int)y)));
    private static jv f_ldexp(jq_state jq, jv input, jv a, jv b) =>
        c_math2(input, a, b, static (x, y) => Math.ScaleB(x, unchecked((int)y)));
    private static jv f_fma(jq_state jq, jv input, jv a, jv b, jv c) =>
        c_math3(input, a, b, c, Math.FusedMultiplyAdd);

    private static double c_fmax(double a, double b) =>
        double.IsNaN(a) ? b : double.IsNaN(b) ? a : Math.Max(a, b);

    private static double c_fmin(double a, double b) =>
        double.IsNaN(a) ? b : double.IsNaN(b) ? a : Math.Min(a, b);

    private static double c_hypot(double a, double b)
    {
        a = Math.Abs(a);
        b = Math.Abs(b);
        var maximum = Math.Max(a, b);
        if (double.IsInfinity(maximum))
        {
            return double.PositiveInfinity;
        }

        if (maximum == 0)
        {
            return 0;
        }

        var minimum = Math.Min(a, b) / maximum;
        return maximum * Math.Sqrt(1 + (minimum * minimum));
    }

    private static double c_nextafter(double value, double direction)
    {
        if (double.IsNaN(value) || double.IsNaN(direction) || value.Equals(direction))
        {
            return direction;
        }

        if (value == 0)
        {
            return direction > 0 ? double.Epsilon : -double.Epsilon;
        }

        return direction > value ? Math.BitIncrement(value) : Math.BitDecrement(value);
    }

    private static jv f_significand(jq_state jq, jv input) =>
        c_math1(input, jq_significand);

    private static jv f_modf(jq_state jq, jv input)
    {
        if (input.Kind != jv_kind.JV_KIND_NUMBER)
        {
            return type_error(input, "number required");
        }

        var (fraction, integral) = jq_modf(input.NumberValue);
        jv_free(input);
        return jv_array([jv_number(fraction), jv_number(integral)]);
    }

    private static jv f_frexp(jq_state jq, jv input)
    {
        if (input.Kind != jv_kind.JV_KIND_NUMBER)
        {
            return type_error(input, "number required");
        }

        var (fraction, exponent) = jq_frexp(input.NumberValue);
        jv_free(input);
        return jv_array([jv_number(fraction), jv_number(exponent)]);
    }

    private static jv f_lgamma_r(jq_state jq, jv input)
    {
        if (input.Kind != jv_kind.JV_KIND_NUMBER)
        {
            return type_error(input, "number required");
        }

        var (value, sign) = jq_lgamma_r(input.NumberValue);
        jv_free(input);
        return jv_array([jv_number(value), jv_number(sign)]);
    }

    private static jv f_negate(jq_state jq, jv input)
    {
        if (input.Kind != jv_kind.JV_KIND_NUMBER)
        {
            return type_error(input, "cannot be negated");
        }

        var result = jv_number_negate(input);
        jv_free(input);
        return result;
    }

    private static jv f_plus(jq_state jq, jv input, jv a, jv b)
    {
        jv_free(input);
        return binop_plus(a, b);
    }

    internal static jv binop_plus(jv a, jv b)
    {
        if (a.Kind == jv_kind.JV_KIND_NULL)
        {
            jv_free(a);
            return b;
        }

        if (b.Kind == jv_kind.JV_KIND_NULL)
        {
            jv_free(b);
            return a;
        }

        if (a.Kind == jv_kind.JV_KIND_NUMBER && b.Kind == jv_kind.JV_KIND_NUMBER)
        {
            var result = jv_number(a.NumberValue + b.NumberValue);
            jv_free(a);
            jv_free(b);
            return result;
        }

        if (a.Kind == jv_kind.JV_KIND_STRING && b.Kind == jv_kind.JV_KIND_STRING)
        {
            return jv_string_concat(a, b);
        }

        if (a.Kind == jv_kind.JV_KIND_ARRAY && b.Kind == jv_kind.JV_KIND_ARRAY)
        {
            return jv_array_concat(a, b);
        }

        if (a.Kind == jv_kind.JV_KIND_OBJECT && b.Kind == jv_kind.JV_KIND_OBJECT)
        {
            return jv_object_merge(a, b);
        }

        return type_error2(a, b, "cannot be added");
    }

    private static jv f_minus(jq_state jq, jv input, jv a, jv b)
    {
        jv_free(input);
        return binop_minus(a, b);
    }

    internal static jv binop_minus(jv a, jv b)
    {
        if (a.Kind == jv_kind.JV_KIND_NUMBER && b.Kind == jv_kind.JV_KIND_NUMBER)
        {
            var result = jv_number(a.NumberValue - b.NumberValue);
            jv_free(a);
            jv_free(b);
            return result;
        }

        if (a.Kind == jv_kind.JV_KIND_ARRAY && b.Kind == jv_kind.JV_KIND_ARRAY)
        {
            var result = jv_array();
            try
            {
                foreach (var candidate in a.ArrayValue)
                {
                    var include = true;
                    foreach (var removal in b.ArrayValue)
                    {
                        if (jv_equal(jv_copy(candidate), jv_copy(removal)))
                        {
                            include = false;
                            break;
                        }
                    }

                    if (include)
                    {
                        result = jv_array_append(result, jv_copy(candidate));
                    }
                }

                var output = result;
                result = jv_invalid();
                return output;
            }
            catch (JqRuntimeException exception)
            {
                return c_runtime_error(exception);
            }
            finally
            {
                jv_free(result);
                jv_free(a);
                jv_free(b);
            }
        }

        return type_error2(a, b, "cannot be subtracted");
    }

    private static int c_repeat_count(double value) =>
        value < 0 || double.IsNaN(value)
            ? -1
            : value > int.MaxValue ? int.MaxValue : (int)value;

    private static jv f_multiply(jq_state jq, jv input, jv a, jv b)
    {
        jv_free(input);
        return binop_multiply(a, b);
    }

    internal static jv binop_multiply(jv a, jv b)
    {
        if (a.Kind == jv_kind.JV_KIND_NUMBER && b.Kind == jv_kind.JV_KIND_NUMBER)
        {
            var result = jv_number(a.NumberValue * b.NumberValue);
            jv_free(a);
            jv_free(b);
            return result;
        }

        if ((a.Kind == jv_kind.JV_KIND_STRING && b.Kind == jv_kind.JV_KIND_NUMBER) ||
            (a.Kind == jv_kind.JV_KIND_NUMBER && b.Kind == jv_kind.JV_KIND_STRING))
        {
            var text = a.Kind == jv_kind.JV_KIND_STRING ? a : b;
            var number = a.Kind == jv_kind.JV_KIND_NUMBER ? a : b;
            var count = c_repeat_count(number.NumberValue);
            jv_free(number);
            return jv_string_repeat(text, count);
        }

        if (a.Kind == jv_kind.JV_KIND_OBJECT && b.Kind == jv_kind.JV_KIND_OBJECT)
        {
            return jv_object_merge_recursive(a, b);
        }

        return type_error2(a, b, "cannot be multiplied");
    }

    private static jv f_divide(jq_state jq, jv input, jv a, jv b)
    {
        jv_free(input);
        return binop_divide(a, b);
    }

    internal static jv binop_divide(jv a, jv b)
    {
        if (a.Kind == jv_kind.JV_KIND_NUMBER && b.Kind == jv_kind.JV_KIND_NUMBER)
        {
            if (b.NumberValue == 0)
            {
                return type_error2(a, b, "cannot be divided because the divisor is zero");
            }

            var result = jv_number(a.NumberValue / b.NumberValue);
            jv_free(a);
            jv_free(b);
            return result;
        }

        if (a.Kind == jv_kind.JV_KIND_STRING && b.Kind == jv_kind.JV_KIND_STRING)
        {
            return jv_string_split(a, b);
        }

        return type_error2(a, b, "cannot be divided");
    }

    private static long c_saturating_intmax(double value)
    {
        if (value < long.MinValue)
        {
            return long.MinValue;
        }

        return -value <= long.MinValue ? long.MaxValue : (long)value;
    }

    private static jv f_mod(jq_state jq, jv input, jv a, jv b)
    {
        jv_free(input);
        return binop_mod(a, b);
    }

    internal static jv binop_mod(jv a, jv b)
    {
        if (a.Kind != jv_kind.JV_KIND_NUMBER || b.Kind != jv_kind.JV_KIND_NUMBER)
        {
            return type_error2(a, b, "cannot be divided (remainder)");
        }

        if (double.IsNaN(a.NumberValue) || double.IsNaN(b.NumberValue))
        {
            jv_free(a);
            jv_free(b);
            return jv_number(double.NaN);
        }

        var divisor = c_saturating_intmax(b.NumberValue);
        if (divisor == 0)
        {
            return type_error2(
                a,
                b,
                "cannot be divided (remainder) because the divisor is zero");
        }

        var remainder = divisor == -1 ? 0 : c_saturating_intmax(a.NumberValue) % divisor;
        jv_free(a);
        jv_free(b);
        return jv_number(remainder);
    }

    private static jv c_equality(jv a, jv b, bool invert)
    {
        try
        {
            var equal = jv_equal(a, b);
            return jv_bool(invert ? !equal : equal);
        }
        catch (JqRuntimeException exception)
        {
            return c_runtime_error(exception);
        }
    }

    private static jv f_equal(jq_state jq, jv input, jv a, jv b)
    {
        jv_free(input);
        return binop_equal(a, b);
    }

    internal static jv binop_equal(jv a, jv b) => c_equality(a, b, invert: false);

    private static jv f_notequal(jq_state jq, jv input, jv a, jv b)
    {
        jv_free(input);
        return binop_notequal(a, b);
    }

    internal static jv binop_notequal(jv a, jv b) => c_equality(a, b, invert: true);

    private enum cmp_op
    {
        CMP_OP_LESS,
        CMP_OP_GREATER,
        CMP_OP_LESSEQ,
        CMP_OP_GREATEREQ,
    }

    private static jv order_cmp(jv a, jv b, cmp_op op)
    {
        try
        {
            var result = jv_cmp(a, b);
            return jv_bool(
                op == cmp_op.CMP_OP_LESS && result < 0 ||
                op == cmp_op.CMP_OP_LESSEQ && result <= 0 ||
                op == cmp_op.CMP_OP_GREATEREQ && result >= 0 ||
                op == cmp_op.CMP_OP_GREATER && result > 0);
        }
        catch (JqRuntimeException exception)
        {
            return c_runtime_error(exception);
        }
    }

    private static jv f_less(jq_state jq, jv input, jv a, jv b)
    {
        jv_free(input);
        return binop_less(a, b);
    }

    internal static jv binop_less(jv a, jv b) =>
        order_cmp(a, b, cmp_op.CMP_OP_LESS);

    private static jv f_lesseq(jq_state jq, jv input, jv a, jv b)
    {
        jv_free(input);
        return binop_lesseq(a, b);
    }

    internal static jv binop_lesseq(jv a, jv b) =>
        order_cmp(a, b, cmp_op.CMP_OP_LESSEQ);

    private static jv f_greater(jq_state jq, jv input, jv a, jv b)
    {
        jv_free(input);
        return binop_greater(a, b);
    }

    internal static jv binop_greater(jv a, jv b) =>
        order_cmp(a, b, cmp_op.CMP_OP_GREATER);

    private static jv f_greatereq(jq_state jq, jv input, jv a, jv b)
    {
        jv_free(input);
        return binop_greatereq(a, b);
    }

    internal static jv binop_greatereq(jv a, jv b) =>
        order_cmp(a, b, cmp_op.CMP_OP_GREATEREQ);

    private static jv f_dump(jq_state jq, jv input)
    {
        // jv_dump_string consumes input, matching src/builtin.c:f_dump.
        var result = jv_string(jv_dump_string(input));
        return result;
    }

    private static jv f_json_parse(jq_state jq, jv input)
    {
        if (input.Kind != jv_kind.JV_KIND_STRING)
        {
            return type_error(input, "only strings can be parsed");
        }

        var result = jv_parse_sized(jvp_string_data(input));
        jv_free(input);
        return result;
    }

    private static jv f_tonumber(jq_state jq, jv input)
    {
        if (input.Kind == jv_kind.JV_KIND_NUMBER)
        {
            return input;
        }

        if (input.Kind == jv_kind.JV_KIND_STRING &&
            jvp_string_data(input).IndexOf((byte)0) < 0)
        {
            var number = jv_number_with_literal(input.StringValue);
            if (number.IsValid)
            {
                jv_free(input);
                return number;
            }

            jv_free(number);
        }

        return type_error(input, "cannot be parsed as a number");
    }

    private static jv f_toboolean(jq_state jq, jv input)
    {
        if (input.Kind is jv_kind.JV_KIND_TRUE or jv_kind.JV_KIND_FALSE)
        {
            return input;
        }

        if (input.Kind == jv_kind.JV_KIND_STRING &&
            jvp_string_data(input).IndexOf((byte)0) < 0)
        {
            if (input.StringValue == "true")
            {
                jv_free(input);
                return jv_true();
            }

            if (input.StringValue == "false")
            {
                jv_free(input);
                return jv_false();
            }
        }

        return type_error(input, "cannot be parsed as a boolean");
    }

    private static jv f_tostring(jq_state jq, jv input)
    {
        if (input.Kind == jv_kind.JV_KIND_STRING)
        {
            return input;
        }

        // jv_dump_string consumes input, matching src/builtin.c:f_tostring.
        var result = jv_string(jv_dump_string(input));
        return result;
    }

    private static jv f_keys(jq_state jq, jv input)
    {
        if (input.Kind is not (jv_kind.JV_KIND_ARRAY or jv_kind.JV_KIND_OBJECT))
        {
            return type_error(input, "has no keys");
        }

        return jv_keys(input);
    }

    private static jv f_keys_unsorted(jq_state jq, jv input)
    {
        if (input.Kind is not (jv_kind.JV_KIND_ARRAY or jv_kind.JV_KIND_OBJECT))
        {
            return type_error(input, "has no keys");
        }

        return jv_keys_unsorted(input);
    }

    private static jv f_startswith(jq_state jq, jv input, jv prefix)
    {
        if (input.Kind != jv_kind.JV_KIND_STRING || prefix.Kind != jv_kind.JV_KIND_STRING)
        {
            return ret_error2(input, prefix, "startswith() requires string inputs");
        }

        var source = jvp_string_data(input);
        var pattern = jvp_string_data(prefix);
        var result = jv_bool(source.StartsWith(pattern));
        jv_free(input);
        jv_free(prefix);
        return result;
    }

    private static jv f_endswith(jq_state jq, jv input, jv suffix)
    {
        if (input.Kind != jv_kind.JV_KIND_STRING || suffix.Kind != jv_kind.JV_KIND_STRING)
        {
            return ret_error2(input, suffix, "endswith() requires string inputs");
        }

        var source = jvp_string_data(input);
        var pattern = jvp_string_data(suffix);
        var result = jv_bool(source.EndsWith(pattern));
        jv_free(input);
        jv_free(suffix);
        return result;
    }

    private static jv f_string_split(jq_state jq, jv input, jv separator)
    {
        if (input.Kind != jv_kind.JV_KIND_STRING || separator.Kind != jv_kind.JV_KIND_STRING)
        {
            return ret_error2(input, separator, "split input and separator must be strings");
        }

        return jv_string_split(input, separator);
    }

    private static jv f_string_explode(jq_state jq, jv input)
    {
        if (input.Kind != jv_kind.JV_KIND_STRING)
        {
            return ret_error(input, "explode input must be a string");
        }

        return jv_string_explode(input);
    }

    private static jv f_string_implode(jq_state jq, jv input)
    {
        if (input.Kind != jv_kind.JV_KIND_ARRAY)
        {
            return ret_error(input, "implode input must be an array");
        }

        try
        {
            return jv_string_implode(input);
        }
        catch (JqRuntimeException exception)
        {
            return c_runtime_error(exception);
        }
    }

    private static jv f_string_indexes(jq_state jq, jv input, jv needle)
    {
        if (input.Kind != jv_kind.JV_KIND_STRING)
        {
            jv_free(needle);
            return type_error(input, "cannot be searched, as it is not a string");
        }

        if (needle.Kind != jv_kind.JV_KIND_STRING)
        {
            jv_free(input);
            return type_error(needle, "is not a string");
        }

        return jv_string_indexes(input, needle);
    }

    private static jv string_trim(jv input, bool left, bool right)
    {
        if (input.Kind != jv_kind.JV_KIND_STRING)
        {
            return ret_error(input, "trim input must be a string");
        }

        var data = jvp_string_data(input);
        var start = 0;
        var end = data.Length;
        var codepoint = 0;
        if (left)
        {
            while (start < end &&
                   jvp_utf8_next(data, start, ref codepoint) is { } next &&
                   jvp_codepoint_is_whitespace(codepoint))
            {
                start = next;
            }
        }

        if (right)
        {
            while (end > start && jvp_utf8_backtrack(data, end - 1, start) is { } previous)
            {
                codepoint = 0;
                _ = jvp_utf8_next(data, previous, ref codepoint);
                if (!jvp_codepoint_is_whitespace(codepoint))
                {
                    break;
                }

                end = previous;
            }
        }

        if (start == 0 && end == data.Length)
        {
            return input;
        }

        var result = jv_string_sized(data[start..end], end - start);
        jv_free(input);
        return result;
    }

    private static jv f_string_trim(jq_state jq, jv input) =>
        string_trim(input, left: true, right: true);
    private static jv f_string_ltrim(jq_state jq, jv input) =>
        string_trim(input, left: true, right: false);
    private static jv f_string_rtrim(jq_state jq, jv input) =>
        string_trim(input, left: false, right: true);

    private static jv f_setpath(jq_state jq, jv input, jv path, jv value) =>
        jv_setpath(input, path, value);

    private static jv f_getpath(jq_state jq, jv input, jv path)
    {
        return _jq_path_append(
            jq,
            input,
            path,
            jv_getpath(jv_copy(input), jv_copy(path)));
    }

    private static jv f_delpaths(jq_state jq, jv input, jv paths) =>
        jv_delpaths(input, paths);

    private static jv f_has(jq_state jq, jv input, jv key) => jv_has(input, key);

    private static jv f_contains(jq_state jq, jv input, jv contained)
    {
        if (input.Kind != contained.Kind)
        {
            return type_error2(input, contained, "cannot have their containment checked");
        }

        try
        {
            return jv_bool(jv_contains(input, contained));
        }
        catch (JqRuntimeException exception)
        {
            return c_runtime_error(exception);
        }
    }

    private static jv f_length(jq_state jq, jv input)
    {
        if (input.Kind == jv_kind.JV_KIND_ARRAY)
        {
            return jv_number(jv_array_length(input));
        }

        if (input.Kind == jv_kind.JV_KIND_OBJECT)
        {
            return jv_number(jv_object_length(input));
        }

        if (input.Kind == jv_kind.JV_KIND_STRING)
        {
            return jv_number(jv_string_length_codepoints(input));
        }

        if (input.Kind == jv_kind.JV_KIND_NUMBER)
        {
            var result = jv_number_abs(input);
            jv_free(input);
            return result;
        }

        if (input.Kind == jv_kind.JV_KIND_NULL)
        {
            jv_free(input);
            return jv_number(0);
        }

        return type_error(input, "has no length");
    }

    private static jv f_utf8bytelength(jq_state jq, jv input)
    {
        if (input.Kind != jv_kind.JV_KIND_STRING)
        {
            return type_error(input, "only strings have UTF-8 byte length");
        }

        return jv_number(jv_string_length_bytes(input));
    }

    private static jv f_type(jq_state jq, jv input)
    {
        var result = jv_string(jv_kind_name(input.Kind));
        jv_free(input);
        return result;
    }

    private static jv c_number_predicate(jv input, Func<double, bool> predicate)
    {
        var result = input.Kind == jv_kind.JV_KIND_NUMBER && predicate(input.NumberValue);
        jv_free(input);
        return jv_bool(result);
    }

    private static jv f_isinfinite(jq_state jq, jv input) =>
        c_number_predicate(input, double.IsInfinity);
    private static jv f_isnan(jq_state jq, jv input) =>
        c_number_predicate(input, double.IsNaN);
    private static jv f_isnormal(jq_state jq, jv input) => c_number_predicate(
        input,
        static value => double.IsFinite(value) && value != 0 &&
            Math.Abs(value) >= BitConverter.Int64BitsToDouble(0x0010000000000000L));

    private static jv f_infinite(jq_state jq, jv input)
    {
        jv_free(input);
        return jv_number(double.PositiveInfinity);
    }

    private static jv f_nan(jq_state jq, jv input)
    {
        jv_free(input);
        return jv_number(double.NaN);
    }

    private static bool c_equal_length_arrays(jv input, jv keys) =>
        input.Kind == jv_kind.JV_KIND_ARRAY &&
        keys.Kind == jv_kind.JV_KIND_ARRAY &&
        input.ArrayValue.Count == keys.ArrayValue.Count;

    private static jv f_sort(jq_state jq, jv input)
    {
        if (input.Kind != jv_kind.JV_KIND_ARRAY)
        {
            return type_error(input, "cannot be sorted, as it is not an array");
        }

        var keys = jv_copy(input);
        return jv_sort(input, keys);
    }

    private static jv f_sort_by_impl(jq_state jq, jv input, jv keys) =>
        c_equal_length_arrays(input, keys)
            ? jv_sort(input, keys)
            : type_error2(input, keys, "cannot be sorted, as they are not both arrays");

    private static jv f_group_by_impl(jq_state jq, jv input, jv keys) =>
        c_equal_length_arrays(input, keys)
            ? jv_group(input, keys)
            : type_error2(input, keys, "cannot be sorted, as they are not both arrays");

    private static jv f_unique(jq_state jq, jv input)
    {
        if (input.Kind != jv_kind.JV_KIND_ARRAY)
        {
            return type_error(input, "cannot be sorted, as it is not an array");
        }

        var keys = jv_copy(input);
        return jv_unique(input, keys);
    }

    private static jv f_unique_by_impl(jq_state jq, jv input, jv keys) =>
        c_equal_length_arrays(input, keys)
            ? jv_unique(input, keys)
            : type_error2(input, keys, "cannot be sorted, as they are not both arrays");

    private static jv f_bsearch(jq_state jq, jv input, jv target)
    {
        if (input.Kind != jv_kind.JV_KIND_ARRAY)
        {
            jv_free(target);
            return type_error(input, "cannot be searched from");
        }

        var start = 0;
        var end = input.ArrayValue.Count;
        var answer = -1;
        try
        {
            while (start < end)
            {
                var middle = start + ((end - start) / 2);
                var comparison = jv_cmp(
                    jv_copy(target),
                    jv_array_get(jv_copy(input), middle));
                if (comparison == 0)
                {
                    answer = middle;
                    break;
                }

                if (comparison < 0)
                {
                    end = middle;
                }
                else
                {
                    start = middle + 1;
                }
            }

            return jv_number(answer >= 0 ? answer : -1 - start);
        }
        catch (JqRuntimeException exception)
        {
            return c_runtime_error(exception);
        }
        finally
        {
            jv_free(input);
            jv_free(target);
        }
    }

    private static jv minmax_by(jv values, jv keys, bool minimum)
    {
        if (values.Kind != jv_kind.JV_KIND_ARRAY || keys.Kind != jv_kind.JV_KIND_ARRAY)
        {
            return type_error2(values, keys, "cannot be iterated over");
        }

        if (values.ArrayValue.Count != keys.ArrayValue.Count)
        {
            return type_error2(values, keys, "have wrong length");
        }

        if (values.ArrayValue.Count == 0)
        {
            jv_free(values);
            jv_free(keys);
            return jv_null();
        }

        var selected = jv_copy(values.ArrayValue[0]);
        var selectedKey = jv_copy(keys.ArrayValue[0]);
        try
        {
            for (var index = 1; index < values.ArrayValue.Count; index++)
            {
                var candidateKey = jv_copy(keys.ArrayValue[index]);
                int comparison;
                try
                {
                    comparison = jv_cmp(jv_copy(candidateKey), jv_copy(selectedKey));
                }
                catch (JqRuntimeException exception)
                {
                    jv_free(candidateKey);
                    jv_free(selectedKey);
                    jv_free(selected);
                    selectedKey = jv_invalid();
                    selected = jv_invalid();
                    return c_runtime_error(exception);
                }

                if ((comparison < 0) == minimum)
                {
                    jv_free(selectedKey);
                    selectedKey = candidateKey;
                    jv_free(selected);
                    selected = jv_copy(values.ArrayValue[index]);
                }
                else
                {
                    jv_free(candidateKey);
                }
            }

            var result = selected;
            selected = jv_invalid();
            return result;
        }
        finally
        {
            jv_free(selectedKey);
            jv_free(selected);
            jv_free(values);
            jv_free(keys);
        }
    }

    private static jv f_min(jq_state jq, jv values)
    {
        var keys = jv_copy(values);
        return minmax_by(values, keys, minimum: true);
    }

    private static jv f_max(jq_state jq, jv values)
    {
        var keys = jv_copy(values);
        return minmax_by(values, keys, minimum: false);
    }

    private static jv f_min_by_impl(jq_state jq, jv values, jv keys) =>
        minmax_by(values, keys, minimum: true);
    private static jv f_max_by_impl(jq_state jq, jv values, jv keys) =>
        minmax_by(values, keys, minimum: false);

    private static jv f_error(jq_state jq, jv input) => jv_invalid_with_msg(input);

    // jq-1.8.2 src/builtin.c:URI_UNRESERVED. Keep the byte table beside
    // f_format so its byte-oriented URI loop remains source-comparable.
    private static readonly byte[] URI_UNRESERVED =
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, // 0x00
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, // 0x10
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 0, // 0x20: - .
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, // 0x30: 0-9
        0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, // 0x40: A-O
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 1, // 0x50: P-Z _
        0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, // 0x60: a-o
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 1, 0, // 0x70: p-z ~
    ];

    private static ReadOnlySpan<byte> BASE64_ENCODE_TABLE =>
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/"u8;

    private const byte BASE64_INVALID_ENTRY = 0xFF;

    // jq-1.8.2 src/builtin.c:BASE64_DECODE_TABLE. The '=' entry is retained
    // even though f_format stops before it, matching the pinned source table.
    private static readonly byte[] BASE64_DECODE_TABLE =
    [
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
        62,
        0xFF, 0xFF, 0xFF,
        63,
        52, 53, 54, 55, 56, 57, 58, 59, 60, 61,
        0xFF, 0xFF, 0xFF,
        99,
        0xFF, 0xFF, 0xFF,
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25,
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
        26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48, 49, 50, 51,
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
    ];

    // jq-1.8.2 src/builtin.c:escape_string. `escapings` is the same sequence
    // of ASCII key/replacement C strings as upstream. Offsets stand in for
    // pointers into that sequence, while -2 denotes upstream lookup[0].
    private static jv escape_string(jv input, ReadOnlySpan<byte> escapings)
    {
        System.Diagnostics.Debug.Assert(input.Kind == jv_kind.JV_KIND_STRING);
        Span<int> lookup = stackalloc int[128];
        Span<int> replacementLengths = stackalloc int[128];
        lookup.Fill(-1);
        lookup[0] = -2;
        var p = 0;
        while (p < escapings.Length && escapings[p] != 0)
        {
            var key = escapings[p++];
            var replacementStart = p;
            while (p < escapings.Length && escapings[p] != 0)
            {
                p++;
            }

            lookup[key] = replacementStart;
            replacementLengths[key] = p - replacementStart;
            p++;
        }

        var result = jv_string(string.Empty);
        var source = jvp_string_data(input);
        var offset = 0;
        var codepoint = 0;
        int? next;
        while ((next = jvp_utf8_next(source, offset, ref codepoint)) is not null)
        {
            var characterStart = offset;
            offset = next.Value;
            if ((uint)codepoint < (uint)lookup.Length && lookup[codepoint] != -1)
            {
                result = codepoint == 0
                    ? jv_string_append_buf(result, "\\0"u8)
                    : jv_string_append_buf(
                        result,
                        escapings.Slice(
                            lookup[codepoint],
                            replacementLengths[codepoint]));
            }
            else
            {
                result = jv_string_append_buf(
                    result,
                    source.Slice(characterStart, offset - characterStart));
            }
        }

        jv_free(input);
        return result;
    }

    // Direct port of jq-1.8.2 src/builtin.c:f_format. The managed printer
    // returns System.String rather than jv, so its consuming result is wrapped
    // immediately with jv_string; every jq input/format/element owner follows
    // the pinned copy/free/concat control flow.
    private static jv f_format(jq_state jq, jv input, jv format)
    {
        if (format.Kind != jv_kind.JV_KIND_STRING)
        {
            jv_free(input);
            return type_error(format, "is not a valid format");
        }

        var formatText = c_string_value(format);
        if (formatText == "json")
        {
            jv_free(format);
            return jv_string(jv_dump_string(input, 0));
        }

        if (formatText == "text")
        {
            jv_free(format);
            return f_tostring(jq, input);
        }

        if (formatText is "csv" or "tsv")
        {
            string quotes;
            string separator;
            ReadOnlySpan<byte> escapings;
            string message;
            if (formatText == "csv")
            {
                message = "cannot be csv-formatted, only array";
                quotes = "\"";
                separator = ",";
                escapings = "\"\"\"\0"u8;
            }
            else
            {
                message = "cannot be tsv-formatted, only array";
                System.Diagnostics.Debug.Assert(formatText == "tsv");
                quotes = string.Empty;
                separator = "\t";
                escapings = "\t\\t\0\r\\r\0\n\\n\0\\\\\\\0"u8;
            }

            jv_free(format);
            if (input.Kind != jv_kind.JV_KIND_ARRAY)
            {
                return type_error(input, message);
            }

            var line = jv_string(string.Empty);
            var inputLength = jv_array_length(jv_copy(input));
            for (var index = 0; index < inputLength; index++)
            {
                var element = jv_array_get(jv_copy(input), index);
                if (index != 0)
                {
                    line = jv_string_append_str(line, separator);
                }

                switch (element.Kind)
                {
                    case jv_kind.JV_KIND_NULL:
                        // null rendered as empty string
                        jv_free(element);
                        break;
                    case jv_kind.JV_KIND_TRUE:
                    case jv_kind.JV_KIND_FALSE:
                        line = jv_string_concat(
                            line,
                            jv_string(jv_dump_string(element, 0)));
                        break;
                    case jv_kind.JV_KIND_NUMBER:
                        if (jv_number_value(element) != jv_number_value(element))
                        {
                            // NaN, render as empty string
                            jv_free(element);
                        }
                        else
                        {
                            line = jv_string_concat(
                                line,
                                jv_string(jv_dump_string(element, 0)));
                        }

                        break;
                    case jv_kind.JV_KIND_STRING:
                        line = jv_string_append_str(line, quotes);
                        line = jv_string_concat(line, escape_string(element, escapings));
                        line = jv_string_append_str(line, quotes);
                        break;
                    default:
                        jv_free(input);
                        jv_free(line);
                        return type_error(element, "is not valid in a csv row");
                }
            }

            jv_free(input);
            return line;
        }

        if (formatText == "html")
        {
            jv_free(format);
            return escape_string(
                f_tostring(jq, input),
                "&&amp;\0<&lt;\0>&gt;\0'&apos;\0\"&quot;\0"u8);
        }

        if (formatText == "uri")
        {
            jv_free(format);
            input = f_tostring(jq, input);
            var data = jvp_string_data(input);
            var length = jv_string_length_bytes(jv_copy(input));
            var result = jv_mem_alloc(checked((int)((long)length * 3 + 1)));
            var resultIndex = 0;
            for (var index = 0; index < length; index++)
            {
                var value = data[index];
                if (value < 128 && URI_UNRESERVED[value] != 0)
                {
                    result[resultIndex++] = value;
                }
                else
                {
                    result[resultIndex++] = (byte)'%';
                    result[resultIndex++] = "0123456789ABCDEF"u8[value >> 4];
                    result[resultIndex++] = "0123456789ABCDEF"u8[value & 0x0F];
                }
            }

            var line = jv_string_sized(result, resultIndex);
            jv_mem_free(result);
            jv_free(input);
            return line;
        }

        if (formatText == "urid")
        {
            jv_free(format);
            input = f_tostring(jq, input);
            const string errorMessage = "is not a valid uri encoding";
            var data = jvp_string_data(input);
            var length = jv_string_length_bytes(jv_copy(input));
            var result = jv_mem_alloc(checked(length + 1));
            var resultIndex = 0;
            for (var index = 0; index < length; index++)
            {
                var value = data[index];
                if (value != (byte)'%')
                {
                    result[resultIndex++] = value;
                }
                else
                {
                    var high = 0;
                    var low = 0;
                    for (var digitIndex = 0; digitIndex < 2; digitIndex++)
                    {
                        if (++index >= length)
                        {
                            jv_mem_free(result);
                            return type_error(input, errorMessage);
                        }

                        value = data[index];
                        var digit = 0;
                        if (value is >= (byte)'0' and <= (byte)'9')
                        {
                            digit = value - '0';
                        }
                        else if (value is >= (byte)'a' and <= (byte)'f')
                        {
                            digit = value - 'a' + 10;
                        }
                        else if (value is >= (byte)'A' and <= (byte)'F')
                        {
                            digit = value - 'A' + 10;
                        }
                        else
                        {
                            jv_mem_free(result);
                            return type_error(input, errorMessage);
                        }

                        if (digitIndex == 0)
                        {
                            high = digit;
                        }
                        else
                        {
                            low = digit;
                        }
                    }

                    result[resultIndex++] = (byte)((high << 4) | low);
                }
            }

            if (!jvp_utf8_is_valid(result.AsSpan(0, resultIndex)))
            {
                jv_mem_free(result);
                return type_error(input, errorMessage);
            }

            var line = jv_string_sized(result, resultIndex);
            jv_mem_free(result);
            jv_free(input);
            return line;
        }

        if (formatText == "sh")
        {
            jv_free(format);
            if (input.Kind != jv_kind.JV_KIND_ARRAY)
            {
                input = jv_array_set(jv_array(), 0, input);
            }

            var line = jv_string(string.Empty);
            var inputLength = jv_array_length(jv_copy(input));
            for (var index = 0; index < inputLength; index++)
            {
                var element = jv_array_get(jv_copy(input), index);
                if (index != 0)
                {
                    line = jv_string_append_str(line, " ");
                }

                switch (element.Kind)
                {
                    case jv_kind.JV_KIND_NULL:
                    case jv_kind.JV_KIND_TRUE:
                    case jv_kind.JV_KIND_FALSE:
                    case jv_kind.JV_KIND_NUMBER:
                        line = jv_string_concat(
                            line,
                            jv_string(jv_dump_string(element, 0)));
                        break;
                    case jv_kind.JV_KIND_STRING:
                        line = jv_string_append_str(line, "'");
                        line = jv_string_concat(
                            line,
                            escape_string(element, "''\\''\0"u8));
                        line = jv_string_append_str(line, "'");
                        break;
                    default:
                        jv_free(input);
                        jv_free(line);
                        return type_error(element, "can not be escaped for shell");
                }
            }

            jv_free(input);
            return line;
        }

        if (formatText == "base64")
        {
            jv_free(format);
            input = f_tostring(jq, input);
            var line = jv_string(string.Empty);
            var data = jvp_string_data(input);
            var length = jv_string_length_bytes(jv_copy(input));
            Span<byte> buffer = stackalloc byte[4];
            for (var index = 0; index < length; index += 3)
            {
                uint code = 0;
                var count = length - index >= 3 ? 3 : length - index;
                for (var byteIndex = 0; byteIndex < 3; byteIndex++)
                {
                    code <<= 8;
                    code |= byteIndex < count ? data[index + byteIndex] : 0u;
                }

                for (var byteIndex = 0; byteIndex < 4; byteIndex++)
                {
                    buffer[byteIndex] = BASE64_ENCODE_TABLE[(int)((code >> (18 - byteIndex * 6)) & 0x3F)];
                }

                if (count < 3)
                {
                    buffer[3] = (byte)'=';
                }

                if (count < 2)
                {
                    buffer[2] = (byte)'=';
                }

                line = jv_string_append_buf(line, buffer);
            }

            jv_free(input);
            return line;
        }

        if (formatText == "base64d")
        {
            jv_free(format);
            input = f_tostring(jq, input);
            var data = jvp_string_data(input);
            var length = jv_string_length_bytes(jv_copy(input));
            var decodedLength = checked((int)Math.Max((3L * length) / 4, 1L));
            var result = jv_mem_calloc(decodedLength, sizeof(byte));
            var resultIndex = 0;
            var inputBytesRead = 0;
            uint code = 0;
            for (var index = 0; index < length && data[index] != (byte)'='; index++)
            {
                if (BASE64_DECODE_TABLE[data[index]] == BASE64_INVALID_ENTRY)
                {
                    jv_mem_free(result);
                    return type_error(input, "is not valid base64 data");
                }

                code <<= 6;
                code |= BASE64_DECODE_TABLE[data[index]];
                inputBytesRead++;
                if (inputBytesRead == 4)
                {
                    result[resultIndex++] = (byte)((code >> 16) & 0xFF);
                    result[resultIndex++] = (byte)((code >> 8) & 0xFF);
                    result[resultIndex++] = (byte)(code & 0xFF);
                    inputBytesRead = 0;
                    code = 0;
                }
            }

            if (inputBytesRead == 3)
            {
                result[resultIndex++] = (byte)((code >> 10) & 0xFF);
                result[resultIndex++] = (byte)((code >> 2) & 0xFF);
            }
            else if (inputBytesRead == 2)
            {
                result[resultIndex++] = (byte)((code >> 4) & 0xFF);
            }
            else if (inputBytesRead == 1)
            {
                jv_mem_free(result);
                return type_error(input, "trailing base64 byte found");
            }

            var line = jv_string_sized(result, resultIndex);
            jv_free(input);
            jv_mem_free(result);
            return line;
        }

        jv_free(input);
        return jv_invalid_with_msg(
            jv_string_concat(format, jv_string(" is not a valid format")));
    }

    private static jv f_env(jq_state jq, jv input)
    {
        jv_free(input);
        return jq_environment(jq.Options);
    }

    private static jv f_halt(jq_state jq, jv input)
    {
        jv_free(input);
        jq_halt(jq, jv_invalid(), jv_invalid());
        return jv_true();
    }

    private static jv f_halt_error(jq_state jq, jv input, jv exitCode)
    {
        if (exitCode.Kind != jv_kind.JV_KIND_NUMBER)
        {
            jv_free(exitCode);
            return type_error(input, "halt_error/1: number required");
        }

        jq_halt(jq, exitCode, input);
        return jv_true();
    }

    private static jv f_get_search_list(jq_state jq, jv input)
    {
        jv_free(input);
        return jq_get_lib_dirs(jq);
    }

    private static jv f_get_prog_origin(jq_state jq, jv input)
    {
        jv_free(input);
        return jq_get_prog_origin(jq);
    }

    private static jv f_get_jq_origin(jq_state jq, jv input)
    {
        jv_free(input);
        return jq_get_jq_origin(jq);
    }

    private static jv f_match(
        jq_state jq,
        jv input,
        jv regex,
        jv modifiers,
        jv testMode)
    {
        bool test;
        try
        {
            test = jv_equal(testMode, jv_true());
        }
        catch (JqRuntimeException exception)
        {
            jv_free(input);
            jv_free(regex);
            jv_free(modifiers);
            return c_runtime_error(exception);
        }

        if (input.Kind != jv_kind.JV_KIND_STRING)
        {
            jv_free(regex);
            jv_free(modifiers);
            return type_error(input, "cannot be matched, as it is not a string");
        }

        if (regex.Kind != jv_kind.JV_KIND_STRING)
        {
            jv_free(input);
            jv_free(modifiers);
            return type_error(regex, "is not a string");
        }

        if (modifiers.Kind is not (jv_kind.JV_KIND_STRING or jv_kind.JV_KIND_NULL))
        {
            jv_free(input);
            jv_free(regex);
            return type_error(modifiers, "is not a string");
        }

        var modifierText = modifiers.Kind == jv_kind.JV_KIND_STRING
            ? modifiers.StringValue
            : null;
        try
        {
            var result = test
                ? JqRegex.TestAsJv(
                    input.StringValue,
                    regex.StringValue,
                    modifierText,
                    jq.Options.RegexTimeout)
                : JqRegex.MatchAsJv(
                    input.StringValue,
                    regex.StringValue,
                    modifierText,
                    jq.Options.RegexTimeout);
            jv_free(input);
            jv_free(regex);
            jv_free(modifiers);
            return result;
        }
        catch (JqRuntimeException exception)
        {
            jv_free(input);
            jv_free(regex);
            jv_free(modifiers);
            return c_runtime_error(exception);
        }
    }

    private static jv f_modulemeta(jq_state jq, jv input)
    {
        if (input.Kind != jv_kind.JV_KIND_STRING)
        {
            return ret_error(input, "modulemeta input module name must be a string");
        }

        return load_module_meta(jq, input);
    }

    private static jv f_input(jq_state jq, jv input)
    {
        jv_free(input);
        jq_get_input_cb(jq, out var callback);
        if (callback is null)
        {
            return jv_invalid_with_msg(jv_string("break"));
        }

        try
        {
            var value = callback();
            if (jv_is_valid(value) || jv_invalid_has_msg(jv_copy(value)))
            {
                return value;
            }

            jv_free(value);
            return jv_invalid_with_msg(jv_string("break"));
        }
        catch (JqRuntimeException exception)
        {
            return c_runtime_error(exception);
        }
    }

    private static jv f_debug(jq_state jq, jv input)
    {
        jq_get_debug_cb(jq, out var callback);
        callback?.Invoke(jv_copy(input));
        return input;
    }

    private static jv f_stderr(jq_state jq, jv input)
    {
        jq_get_stderr_cb(jq, out var callback);
        callback?.Invoke(jv_copy(input));
        return input;
    }

    private static string c_string_value(jv value)
    {
        var bytes = jvp_string_data(value);
        var nul = bytes.IndexOf((byte)0);
        return Encoding.UTF8.GetString(nul < 0 ? bytes : bytes[..nul]);
    }

    private static jv f_strptime(jq_state jq, jv input, jv format)
    {
        if (input.Kind != jv_kind.JV_KIND_STRING || format.Kind != jv_kind.JV_KIND_STRING)
        {
            return ret_error2(input, format, "strptime/1 requires string inputs and arguments");
        }

        var formatText = c_string_value(format);
        try
        {
            var result = jq.WithTimeEnvironment(
                (environment, timeZoneContext) => JqTimeBuiltinProxy.ParseTime(
                    input,
                    formatText,
                    environment,
                    timeZoneContext));
            jv_free(input);
            jv_free(format);
            return result;
        }
        catch (JqRuntimeException exception)
        {
            jv_free(input);
            jv_free(format);
            return c_runtime_error(exception);
        }
    }

    private static jv f_mktime(jq_state jq, jv input)
    {
        if (input.Kind != jv_kind.JV_KIND_ARRAY)
        {
            return ret_error(input, "mktime requires array inputs");
        }

        try
        {
            var result = JqTimeBuiltinProxy.Mktime(input);
            jv_free(input);
            return result;
        }
        catch (JqRuntimeException exception)
        {
            jv_free(input);
            return c_runtime_error(exception);
        }
    }

    private static jv c_epoch_to_time(
        jq_state jq,
        jv input,
        bool local) =>
        local
            ? jq.WithTimeEnvironment(
                (environment, timeZoneContext) => c_epoch_to_time(
                    input,
                    local: true,
                    environment,
                    timeZoneContext))
            : c_epoch_to_time(
                input,
                local: false,
                environment: null,
                timeZoneContext: null);

    private static jv c_epoch_to_time(
        jv input,
        bool local,
        IReadOnlyDictionary<string, string>? environment,
        JqTimeZoneContext? timeZoneContext)
    {
        if (input.Kind != jv_kind.JV_KIND_NUMBER)
        {
            return ret_error(
                input,
                local ? "localtime() requires numeric inputs" : "gmtime() requires numeric inputs");
        }

        try
        {
            var result = JqTimeBuiltinProxy.EpochToBrokenDown(
                input,
                local,
                environment,
                timeZoneContext);
            jv_free(input);
            return result;
        }
        catch (JqRuntimeException exception)
        {
            jv_free(input);
            return c_runtime_error(exception);
        }
    }

    private static jv f_gmtime(jq_state jq, jv input) =>
        c_epoch_to_time(jq, input, local: false);

    private static jv f_localtime(jq_state jq, jv input) =>
        c_epoch_to_time(jq, input, local: true);

    private static jv c_strftime(jq_state jq, jv input, jv format, bool local)
        => jq.WithTimeEnvironment(
            (environment, timeZoneContext) => c_strftime(
                input,
                format,
                local,
                environment,
                timeZoneContext));

    private static jv c_strftime(
        jv input,
        jv format,
        bool local,
        IReadOnlyDictionary<string, string> environment,
        JqTimeZoneContext timeZoneContext)
    {
        var functionName = local ? "strflocaltime" : "strftime";
        if (input.Kind == jv_kind.JV_KIND_NUMBER)
        {
            input = c_epoch_to_time(input, local, environment, timeZoneContext);
            if (!input.IsValid)
            {
                jv_free(format);
                return input;
            }
        }
        else if (input.Kind != jv_kind.JV_KIND_ARRAY)
        {
            return ret_error2(input, format, $"{functionName}/1 requires parsed datetime inputs");
        }

        if (format.Kind != jv_kind.JV_KIND_STRING)
        {
            return ret_error2(input, format, $"{functionName}/1 requires a string format");
        }

        try
        {
            var result = jv_string(JqTimeBuiltinProxy.FormatTime(
                input,
                c_string_value(format),
                local,
                environment,
                timeZoneContext));
            jv_free(input);
            jv_free(format);
            return result;
        }
        catch (JqRuntimeException exception)
        {
            jv_free(input);
            jv_free(format);
            return c_runtime_error(
                local && exception.Message == "strftime/1: unknown system failure"
                    ? new JqRuntimeException("strflocaltime/1: unknown system failure")
                    : exception);
        }
    }

    private static jv f_strftime(jq_state jq, jv input, jv format) =>
        c_strftime(jq, input, format, local: false);
    private static jv f_strflocaltime(jq_state jq, jv input, jv format) =>
        c_strftime(jq, input, format, local: true);

    private static jv f_now(jq_state jq, jv input)
    {
        jv_free(input);
        return JqTimeBuiltinProxy.Now();
    }

    private static jv f_current_filename(jq_state jq, jv input)
    {
        jv_free(input);
        return jq.GetCurrentInputFilename();
    }

    private static jv f_current_line(jq_state jq, jv input)
    {
        jv_free(input);
        try
        {
            return jq.GetCurrentInputLineNumber();
        }
        catch (JqRuntimeException exception)
        {
            return c_runtime_error(exception);
        }
    }

    private static jv f_have_decnum(jq_state jq, jv input)
    {
        jv_free(input);
        return jv_true();
    }
}

// Platform/time compatibility proxy used by the direct C-function table.
internal static class JqTimeBuiltinProxy
{
    internal static jv Now()
    {
        // jq's HAVE_GETTIMEOFDAY path has microsecond, not DateTime tick,
        // resolution.  Integer-first arithmetic also avoids a second rounding
        // step through TimeSpan.TotalSeconds.
        var microseconds = (DateTimeOffset.UtcNow.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10;
        return libjq.jv_number(microseconds / 1_000_000d);
    }

    internal static jv Mktime(jv value)
    {
        if (value.Kind != jv_kind.JV_KIND_ARRAY)
        {
            throw new JqRuntimeException("mktime requires array inputs");
        }

        if (!jv2tm(
                value,
                local: false,
                environment: null,
                timeZoneContext: null,
                out var time))
        {
            throw new JqRuntimeException("mktime requires parsed datetime inputs");
        }

        // jq reserves both sentinel values even on HAVE_TIMEGM platforms,
        // although -1 and -2 are valid Unix epochs.
        if (time.UnixSeconds == -1)
        {
            throw new JqRuntimeException("invalid gmtime representation");
        }

        if (time.UnixSeconds == -2)
        {
            throw new JqRuntimeException("mktime not supported on this platform");
        }

        return libjq.jv_number(time.UnixSeconds);
    }

    internal static jv EpochToBrokenDown(
        jv value,
        bool local,
        IReadOnlyDictionary<string, string>? environment = null,
        JqTimeZoneContext? timeZoneContext = null)
    {
        var functionName = local ? "localtime" : "gmtime";
        if (value.Kind != jv_kind.JV_KIND_NUMBER)
        {
            throw new JqRuntimeException($"{functionName}() requires numeric inputs");
        }

        try
        {
            // jq casts to time_t (towards zero), but retains fsecs-floor(fsecs) in tm_sec.
            return tm2jv(BreakDownEpoch(
                value.NumberValue,
                local,
                environment,
                timeZoneContext));
        }
        catch (Exception exception) when (
            exception is ArgumentOutOfRangeException or OverflowException)
        {
            throw new JqRuntimeException("error converting number of seconds since epoch to datetime");
        }
    }

    private static jv tm2jv(BrokenDownTime time)
    {
        return libjq.jv_array(
        [
            libjq.jv_number(time.Year),
            libjq.jv_number(time.Month - 1),
            libjq.jv_number(time.Day),
            libjq.jv_number(time.Hour),
            libjq.jv_number(time.Minute),
            libjq.jv_number(time.Second + time.FractionalSecond),
            libjq.jv_number(time.DayOfWeek),
            libjq.jv_number(time.DayOfYear - 1),
        ]);
    }

    private static bool jv2tm(
        jv value,
        bool local,
        IReadOnlyDictionary<string, string>? environment,
        JqTimeZoneContext? timeZoneContext,
        out BrokenDownTime time)
    {
        time = default;
        if (value.Kind != jv_kind.JV_KIND_ARRAY)
        {
            return false;
        }

        Span<int> fields = stackalloc int[8];
        for (var index = 0; index < Math.Min(8, value.ArrayValue.Count); index++)
        {
            var field = value.ArrayValue[index];
            if (field.Kind != jv_kind.JV_KIND_NUMBER || double.IsNaN(field.NumberValue))
            {
                return false;
            }

            // jv2tm stores tm_year, whose origin is 1900, and every field is
            // clamped to int before libc sees it.  Casts truncate toward zero.
            var adjusted = index == 0 ? field.NumberValue - 1900d : field.NumberValue;
            fields[index] = adjusted < int.MinValue
                ? int.MinValue
                : adjusted > int.MaxValue
                    ? int.MaxValue
                    : (int)adjusted;
        }

        try
        {
            var actualYear = (long)fields[0] + 1900;
            var totalMonths = checked((actualYear * 12) + fields[1]);
            var normalizedYear = FloorDiv(totalMonths, 12);
            var normalizedMonth = checked((int)(totalMonths - (normalizedYear * 12)) + 1);
            // With leap-second-capable glibc builds, __mktime_internal clamps
            // tm_sec only for its inversion probes, then applies the original
            // raw second as an absolute epoch correction after offset_found.
            // Doing ordinary calendar normalization here would choose the
            // opposite side of a gap/date-line transition.
            var probeSecond = Math.Clamp(fields[5], 0, 59);
            var secondAdjustment = checked(fields[5] - probeSecond);
            var secondsWithinDate = checked(
                ((long)fields[3] * 3_600) + ((long)fields[4] * 60) + probeSecond);
            var dayAdjustment = FloorDiv(secondsWithinDate, 86_400);
            var secondOfDay = checked((int)(secondsWithinDate - (dayAdjustment * 86_400)));
            var days = checked(
                DaysFromCivil(normalizedYear, normalizedMonth, 1) + fields[2] - 1L +
                dayAdjustment);
            var localSeconds = checked((days * 86_400) + secondOfDay);
            if (local)
            {
                var normalized = JqTimeZone
                    .Resolve(environment, timeZoneContext)
                    .NormalizeLocal(localSeconds, timeZoneContext, secondAdjustment);
                time = BreakDownIntegral(
                    normalized.UnixSeconds,
                    normalized.LocalSeconds,
                    normalized.Period,
                    fractionalSecond: 0);
            }
            else
            {
                var adjustedSeconds = checked(localSeconds + secondAdjustment);
                time = BreakDownIntegral(
                    adjustedSeconds,
                    adjustedSeconds,
                    new JqTimeZonePeriod(0, false, "GMT"),
                    fractionalSecond: 0);
            }

            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentOutOfRangeException or OverflowException)
        {
            // jq ignores timegm()/mktime()'s return value inside jv2tm.
            // On an unrepresentable normalization glibc leaves the clamped
            // int fields observable to strftime and reports -1 only when %s
            // or f_mktime asks for the scalar epoch.
            time = CreateUnnormalizedTime(fields, local);
            return true;
        }
    }

    private static BrokenDownTime CreateUnnormalizedTime(
        ReadOnlySpan<int> fields,
        bool local)
    {
        var year = unchecked(fields[0] + 1900);
        var month = unchecked(fields[1] + 1);
        var yearDay = unchecked(fields[7] + 1);
        var (isoWeekYear, isoWeek) = ComputeIsoWeekFromTm(
            year,
            fields[7],
            fields[6]);
        return new BrokenDownTime(
            year,
            month,
            fields[2],
            fields[3],
            fields[4],
            fields[5],
            fields[6],
            yearDay,
            isoWeekYear,
            isoWeek,
            TimeSpan.Zero,
            0,
            string.Empty,
            -1,
            local ? -1 : 0,
            fields[0],
            IsNormalized: false);
    }

    private static (int Year, int Week) ComputeIsoWeekFromTm(
        int year,
        int yearDay,
        int weekDay)
    {
        static int IsoWeekDays(int yday, int wday) =>
            unchecked(yday - ((yday - wday + 4 + 378) % 7) + 4 - 1);
        static bool IsLeap(int value) =>
            value % 4 == 0 && (value % 100 != 0 || value % 400 == 0);

        var days = IsoWeekDays(yearDay, weekDay);
        if (days < 0)
        {
            year = unchecked(year - 1);
            days = IsoWeekDays(
                unchecked(yearDay + 365 + (IsLeap(year) ? 1 : 0)),
                weekDay);
        }
        else
        {
            var next = IsoWeekDays(
                unchecked(yearDay - 365 - (IsLeap(year) ? 1 : 0)),
                weekDay);
            if (next >= 0)
            {
                year = unchecked(year + 1);
                days = next;
            }
        }

        return (year, days / 7 + 1);
    }

    internal static string FormatTime(
        jv value,
        string format,
        bool local,
        IReadOnlyDictionary<string, string>? environment = null,
        JqTimeZoneContext? timeZoneContext = null)
    {
        var functionName = local ? "strflocaltime" : "strftime";
        BrokenDownTime time;
        if (value.Kind == jv_kind.JV_KIND_NUMBER)
        {
            // Native f_strftime/f_strflocaltime first call gmtime/localtime,
            // materialize the public eight-element jq array, and then feed
            // that array back through jv2tm.  The second local mktime is
            // observable for malformed/partial POSIX TZ rules and in the
            // cached offset guess, so retain the two-stage path explicitly.
            var firstPass = BreakDownEpoch(
                value.NumberValue,
                local,
                environment,
                timeZoneContext);
            var array = tm2jv(firstPass);
            try
            {
                if (!jv2tm(
                        array,
                        local,
                        environment,
                        timeZoneContext,
                        out time))
                {
                    throw new JqRuntimeException(
                        $"{functionName}/1 requires parsed datetime inputs");
                }
            }
            finally
            {
                libjq.jv_free(array);
            }
        }
        else if (!jv2tm(value, local, environment, timeZoneContext, out time))
        {
            throw new JqRuntimeException($"{functionName}/1 requires parsed datetime inputs");
        }

        var culture = ResolveTimeCulture(environment);
        var maximumBytes = checked(Encoding.UTF8.GetByteCount(format) + 100);
        var result = new StringBuilder(format.Length + 32);
        var resultBytes = 0;
        for (var index = 0; index < format.Length; index++)
        {
            if (format[index] != '%')
            {
                AppendBounded(result, format[index].ToString(), maximumBytes, ref resultBytes);
                continue;
            }

            var specifierStart = index;
            var padding = '\0';
            var upper = false;
            var changeCase = false;
            while (++index < format.Length)
            {
                switch (format[index])
                {
                    case '_' or '-' or '0':
                        padding = format[index];
                        continue;
                    case '^':
                        upper = true;
                        continue;
                    case '#':
                        changeCase = true;
                        continue;
                }

                break;
            }

            var width = -1;
            if (index < format.Length && char.IsAsciiDigit(format[index]))
            {
                width = 0;
                do
                {
                    var digit = format[index] - '0';
                    width = width > (int.MaxValue - digit) / 10
                        ? int.MaxValue
                        : (width * 10) + digit;
                    index++;
                }
                while (index < format.Length && char.IsAsciiDigit(format[index]));
            }

            var modifier = '\0';
            if (index < format.Length && format[index] is 'E' or 'O')
            {
                modifier = format[index++];
            }

            if (index >= format.Length)
            {
                var trailing = format[specifierStart..];
                AppendBounded(
                    result,
                    FormatText(
                        trailing,
                        padding,
                        width,
                        upper,
                        false,
                        culture,
                        maximumBytes),
                    maximumBytes,
                    ref resultBytes);
                break;
            }

            var directive = format[index];
            var rawSpecifier = format[specifierStart..(index + 1)];
            var piece = FormatDirective(
                directive,
                modifier,
                padding,
                width,
                upper,
                changeCase,
                time,
                local,
                culture,
                environment,
                timeZoneContext,
                rawSpecifier,
                maximumBytes);
            AppendBounded(result, piece, maximumBytes, ref resultBytes);
        }

        // POSIX gives strftime no errno for an empty/failure result. jq treats
        // zero bytes from a non-empty format as the same system failure.
        if (resultBytes == 0 && format.Length != 0)
        {
            throw new JqRuntimeException($"{functionName}/1: unknown system failure");
        }

        return result.ToString();
    }

    private static string FormatDirective(
        char directive,
        char modifier,
        char padding,
        int width,
        bool upper,
        bool changeCase,
        BrokenDownTime time,
        bool local,
        CultureInfo culture,
        IReadOnlyDictionary<string, string>? environment,
        JqTimeZoneContext? timeZoneContext,
        string rawSpecifier,
        int maximumBytes)
    {
        // glibc's bad_format scan for %E%/%O% starts at the directive's
        // percent, so only that final '%' is emitted (with the parsed outer
        // width/padding).  It does not literalize the full raw specifier.
        if (directive == '%')
        {
            return FormatText("%", padding, width, upper, false, culture, maximumBytes);
        }

        if (!ModifierIsAccepted(modifier, directive))
        {
            return FormatText(rawSpecifier, padding, width, upper, false, culture, maximumBytes);
        }

        var invariant = CultureInfo.InvariantCulture;
        switch (directive)
        {
            case 'a':
                return FormatText(
                    GetDayName(culture, time.DayOfWeek, abbreviated: true),
                    padding,
                    width,
                    upper || changeCase,
                    false,
                    culture,
                    maximumBytes);
            case 'A':
                return FormatText(
                    GetDayName(culture, time.DayOfWeek, abbreviated: false),
                    padding,
                    width,
                    upper || changeCase,
                    false,
                    culture,
                    maximumBytes);
            case 'b' or 'h':
                return FormatText(
                    GetMonthName(culture, time.Month, abbreviated: true),
                    padding,
                    width,
                    upper || changeCase,
                    false,
                    culture,
                    maximumBytes);
            case 'B':
                return FormatText(
                    GetMonthName(culture, time.Month, abbreviated: false),
                    padding,
                    width,
                    upper || changeCase,
                    false,
                    culture,
                    maximumBytes);
            case 'c':
                return FormatText(
                    IsInvariantCulture(culture)
                        ? FormatComposite(time, "%a %b %e %H:%M:%S %Y", local, culture, environment)
                        : FormatCultureDateTime(time, culture),
                    padding,
                    width,
                    upper,
                    false,
                    culture,
                    maximumBytes);
            case 'C':
                var century = time.Year / 100 - (time.Year % 100 < 0 ? 1 : 0);
                return FormatNumber(century, 1, padding, width, maximumBytes);
            case 'd':
                return FormatNumber(time.Day, 2, padding, width, maximumBytes);
            case 'e':
                return FormatNumber(time.Day, 2, DefaultSpacePadding(padding), width, maximumBytes);
            case 'D':
                return FormatText(
                    FormatComposite(time, "%m/%d/%y", local, culture, environment),
                    padding,
                    width,
                    upper,
                    false,
                    culture,
                    maximumBytes);
            case 'F':
                return FormatText(
                    FormatComposite(time, "%Y-%m-%d", local, culture, environment),
                    padding,
                    width,
                    upper,
                    false,
                    culture,
                    maximumBytes);
            case 'g':
                return FormatNumber(PositiveMod(time.IsoWeekYear, 100), 2, padding, width, maximumBytes);
            case 'G':
                return FormatNumber(time.IsoWeekYear, 1, padding, width, maximumBytes);
            case 'H':
                return FormatNumber(time.Hour, 2, padding, width, maximumBytes);
            case 'I':
                return FormatNumber(Hour12(time.Hour), 2, padding, width, maximumBytes);
            case 'j':
                return FormatNumber(time.DayOfYear, 3, padding, width, maximumBytes);
            case 'k':
                return FormatNumber(time.Hour, 2, DefaultSpacePadding(padding), width, maximumBytes);
            case 'l':
                return FormatNumber(
                    Hour12(time.Hour),
                    2,
                    DefaultSpacePadding(padding),
                    width,
                    maximumBytes);
            case 'm':
                return FormatNumber(time.Month, 2, padding, width, maximumBytes);
            case 'M':
                return FormatNumber(time.Minute, 2, padding, width, maximumBytes);
            case 'n':
                return FormatText("\n", padding, width, upper, false, culture, maximumBytes);
            case 'p' or 'P':
                var amPm = time.Hour < 12
                    ? culture.DateTimeFormat.AMDesignator
                    : culture.DateTimeFormat.PMDesignator;
                return FormatText(
                    amPm,
                    padding,
                    width,
                    upper && directive != 'P' && !changeCase,
                    directive == 'P' || changeCase,
                    culture,
                    maximumBytes);
            case 'r':
                return FormatText(
                    FormatComposite(time, "%I:%M:%S %p", local, culture, environment),
                    padding,
                    width,
                    upper,
                    false,
                    culture,
                    maximumBytes);
            case 'R':
                return FormatText(
                    FormatComposite(time, "%H:%M", local, culture, environment),
                    padding,
                    width,
                    upper,
                    false,
                    culture,
                    maximumBytes);
            case 's':
                long unix;
                if (!time.IsNormalized)
                {
                    unix = -1;
                }
                else
                {
                    // glibc strftime(%s) calls mktime even when jv2tm already
                    // called mktime for strflocaltime.  Besides returning the
                    // same epoch for a canonical local tm, that second call
                    // updates libc's cached offset guess.  Preserve that
                    // observable state transition: it changes a later fold
                    // after gap normalization (notably Europe/Dublin/Apia).
                    var localWallSeconds = local
                        ? checked(time.UnixSeconds + (long)time.Offset.TotalSeconds)
                        : time.UnixSeconds;
                    unix = JqTimeZone.Resolve(environment, timeZoneContext).InterpretLocal(
                        localWallSeconds,
                        time.IsDaylightSaving,
                        timeZoneContext);
                }

                return FormatNumber(unix, 1, padding, width, maximumBytes);
            case 'S':
                return FormatNumber(time.Second, 2, padding, width, maximumBytes);
            case 't':
                return FormatText("\t", padding, width, upper, false, culture, maximumBytes);
            case 'T':
                return FormatText(
                    FormatComposite(time, "%H:%M:%S", local, culture, environment),
                    padding,
                    width,
                    upper,
                    false,
                    culture,
                    maximumBytes);
            case 'u':
                return FormatNumber(
                    (time.DayOfWeek - 1 + 7) % 7 + 1,
                    1,
                    padding,
                    width,
                    maximumBytes);
            case 'U':
                return FormatNumber(
                    (time.DayOfYear - 1 - time.DayOfWeek + 7) / 7,
                    2,
                    padding,
                    width,
                    maximumBytes);
            case 'V':
                return FormatNumber(time.IsoWeek, 2, padding, width, maximumBytes);
            case 'w':
                return FormatNumber(time.DayOfWeek, 1, padding, width, maximumBytes);
            case 'W':
                return FormatNumber(
                    (time.DayOfYear - 1 - ((time.DayOfWeek - 1 + 7) % 7) + 7) / 7,
                    2,
                    padding,
                    width,
                    maximumBytes);
            case 'x':
                return FormatText(
                    IsInvariantCulture(culture)
                        ? FormatComposite(time, "%m/%d/%y", local, culture, environment)
                        : FormatCultureDate(time, culture),
                    padding,
                    width,
                    upper,
                    false,
                    culture,
                    maximumBytes);
            case 'X':
                return FormatText(
                    IsInvariantCulture(culture)
                        ? FormatComposite(time, "%H:%M:%S", local, culture, environment)
                        : FormatCultureTime(time, culture),
                    padding,
                    width,
                    upper,
                    false,
                    culture,
                    maximumBytes);
            case 'y':
                // glibc derives %y directly from the signed int tm_year.  At
                // jq's positive tm_year boundary, tm_year + 1900 (used by %Y)
                // wraps while tm_year % 100 (used here) remains 47.
                return FormatNumber(
                    PositiveMod(time.YearSince1900, 100),
                    2,
                    padding,
                    width,
                    maximumBytes);
            case 'Y':
                return FormatNumber(time.Year, 1, padding, width, maximumBytes);
            case 'z':
                if (time.IsDaylightSaving < 0)
                {
                    return string.Empty;
                }

                var offsetSeconds = checked((int)time.Offset.TotalSeconds);
                var sign = offsetSeconds < 0 ? "-" : "+";
                var offsetMinutes = Math.Abs(offsetSeconds) / 60;
                // glibc emits the sign through add(1, ...) and then sends the
                // HHMM integer through DO_NUMBER(4, ...).  Both operations see
                // the same flag and width, yielding deliberately surprising
                // values such as %05z => "0000+00000".
                var signPiece = FormatText(
                    sign,
                    padding,
                    width,
                    upper,
                    false,
                    culture,
                    maximumBytes);
                var digitsPiece = FormatNumber(
                    (offsetMinutes / 60) * 100 + offsetMinutes % 60,
                    4,
                    padding,
                    width,
                    maximumBytes);
                return signPiece + digitsPiece;
            case 'Z':
                var abbreviation = time.ZoneAbbreviation;
                if (abbreviation.Length == 0 && time.IsDaylightSaving >= 0)
                {
                    abbreviation = JqTimeZone
                        .Resolve(environment, timeZoneContext)
                        .GetRepresentativeAbbreviation(time.IsDaylightSaving != 0);
                }

                return FormatText(
                    abbreviation,
                    padding,
                    width,
                    upper && !changeCase,
                    changeCase,
                    culture,
                    maximumBytes);
            default:
                return FormatText(rawSpecifier, padding, width, upper, false, culture, maximumBytes);
        }
    }

    private static bool ModifierIsAccepted(char modifier, char directive) => modifier switch
    {
        '\0' => directive == '%' || char.IsAsciiLetter(directive),
        'E' => directive is
            'c' or 'C' or 'n' or 'P' or 'p' or 'r' or 'R' or 's' or 't' or 'T' or
            'u' or 'x' or 'X' or 'y' or 'Y' or 'z' or 'Z',
        'O' => directive is
            'b' or 'B' or 'h' or 'C' or 'd' or 'e' or 'g' or 'G' or 'H' or 'I' or
            'j' or 'k' or 'l' or 'm' or 'M' or 'n' or 'P' or 'p' or 'r' or 'R' or
            's' or 'S' or 't' or 'T' or 'u' or 'U' or 'V' or 'w' or 'W' or 'y' or
            'z' or 'Z',
        _ => false,
    };

    private static string GetDayName(
        CultureInfo culture,
        int dayOfWeek,
        bool abbreviated) =>
        dayOfWeek is >= 0 and <= 6
            ? abbreviated
                ? culture.DateTimeFormat.GetAbbreviatedDayName((DayOfWeek)dayOfWeek)
                : culture.DateTimeFormat.GetDayName((DayOfWeek)dayOfWeek)
            : "?";

    private static string GetMonthName(
        CultureInfo culture,
        int month,
        bool abbreviated) =>
        month is >= 1 and <= 12
            ? abbreviated
                ? culture.DateTimeFormat.GetAbbreviatedMonthName(month)
                : culture.DateTimeFormat.GetMonthName(month)
            : "?";

    private static int Hour12(int hour)
    {
        // glibc initializes hour12 from tm_hour and adjusts it only once;
        // invalid raw struct-tm values are intentionally not reduced modulo 12.
        if (hour > 12)
        {
            return unchecked(hour - 12);
        }

        return hour == 0 ? 12 : hour;
    }

    private static char DefaultSpacePadding(char padding) =>
        padding is '0' or '-' ? padding : '_';

    private static string FormatNumber(
        long value,
        int minimumDigits,
        char padding,
        int width,
        int maximumBytes)
    {
        var invariant = CultureInfo.InvariantCulture;
        var negative = value < 0;
        var magnitude = negative
            ? unchecked((ulong)(-(value + 1)) + 1)
            : (ulong)value;
        var digits = magnitude.ToString(invariant);
        var requestedDigits = Math.Max(minimumDigits, width);
        if (requestedDigits >= maximumBytes)
        {
            throw new JqRuntimeException("strftime/1: unknown system failure");
        }

        var rawLength = digits.Length + (negative ? 1 : 0);
        var paddingCount = Math.Max(0, requestedDigits - rawLength);
        if (padding == '-')
        {
            var raw = (negative ? "-" : string.Empty) + digits;
            return width > raw.Length ? raw.PadLeft(width, ' ') : raw;
        }

        if (padding == '_')
        {
            return new string(' ', paddingCount) + (negative ? "-" : string.Empty) + digits;
        }

        return (negative ? "-" : string.Empty) + new string('0', paddingCount) + digits;
    }

    private static string FormatText(
        string value,
        char padding,
        int width,
        bool upper,
        bool lower,
        CultureInfo culture,
        int maximumBytes)
    {
        if (lower)
        {
            value = culture.TextInfo.ToLower(value);
        }
        else if (upper)
        {
            value = culture.TextInfo.ToUpper(value);
        }

        var bytes = Encoding.UTF8.GetByteCount(value);
        var paddingCount = width > bytes ? width - bytes : 0;
        if (bytes + paddingCount >= maximumBytes)
        {
            throw new JqRuntimeException("strftime/1: unknown system failure");
        }

        return paddingCount == 0
            ? value
            : new string(padding == '0' ? '0' : ' ', paddingCount) + value;
    }

    private static void AppendBounded(
        StringBuilder result,
        string value,
        int maximumBytes,
        ref int resultBytes)
    {
        var bytes = Encoding.UTF8.GetByteCount(value);
        if (bytes >= maximumBytes - resultBytes)
        {
            throw new JqRuntimeException("strftime/1: unknown system failure");
        }

        result.Append(value);
        resultBytes += bytes;
    }

    private static string FormatComposite(
        BrokenDownTime time,
        string format,
        bool local,
        CultureInfo culture,
        IReadOnlyDictionary<string, string>? environment)
    {
        var result = new StringBuilder(format.Length + 24);
        for (var index = 0; index < format.Length; index++)
        {
            if (format[index] != '%')
            {
                result.Append(format[index]);
                continue;
            }

            var directive = format[++index];
            result.Append(FormatDirective(
                directive,
                '\0',
                '\0',
                -1,
                false,
                false,
                time,
                local,
                culture,
                environment,
                timeZoneContext: null,
                "%" + directive,
                int.MaxValue));
        }

        return result.ToString();
    }

    private static bool IsInvariantCulture(CultureInfo culture) =>
        culture.Equals(CultureInfo.InvariantCulture) || culture.Name.Length == 0;

    private static string FormatCultureDate(BrokenDownTime time, CultureInfo culture)
    {
        var result = new StringBuilder();
        AppendLocalizedDate(result, time, culture);
        return result.ToString();
    }

    private static string FormatCultureTime(BrokenDownTime time, CultureInfo culture)
    {
        var result = new StringBuilder();
        AppendLocalizedTime(result, time, culture);
        return result.ToString();
    }

    private static string FormatCultureDateTime(BrokenDownTime time, CultureInfo culture) =>
        FormatCultureDate(time, culture) + " " + FormatCultureTime(time, culture);

    private static int PositiveMod(long value, int divisor)
    {
        var result = (int)(value % divisor);
        return result < 0 ? result + divisor : result;
    }

    private static BrokenDownTime BreakDownEpoch(
        double seconds,
        bool local,
        IReadOnlyDictionary<string, string>? environment,
        JqTimeZoneContext? timeZoneContext)
    {
        if (!double.IsFinite(seconds))
        {
            throw new JqRuntimeException(
                "error converting number of seconds since epoch to datetime");
        }

        try
        {
            // jq casts time_t towards zero, while tm_sec keeps the fraction from floor().
            var integralSeconds = checked((long)Math.Truncate(seconds));
            var period = local
                ? JqTimeZone.Resolve(environment, timeZoneContext).GetPeriod(integralSeconds)
                : new JqTimeZonePeriod(0, false, "GMT");
            var localSeconds = checked(integralSeconds + period.OffsetSeconds);
            return BreakDownIntegral(
                integralSeconds,
                localSeconds,
                period,
                seconds - Math.Floor(seconds));
        }
        catch (Exception exception) when (
            exception is ArgumentOutOfRangeException or OverflowException)
        {
            throw new JqRuntimeException(
                "error converting number of seconds since epoch to datetime");
        }
    }

    private static BrokenDownTime BreakDownIntegral(
        long unixSeconds,
        long localSeconds,
        JqTimeZonePeriod period,
        double fractionalSecond)
    {
        var days = FloorDiv(localSeconds, 86_400);
        var secondOfDay = checked((int)(localSeconds - (days * 86_400)));
        return CreateBrokenDownTime(
            days,
            secondOfDay,
            TimeSpan.FromSeconds(period.OffsetSeconds),
            fractionalSecond,
            period.Abbreviation,
            unixSeconds,
            period.IsDaylightSaving ? 1 : 0);
    }

    private static BrokenDownTime CreateBrokenDownTime(
        long days,
        int secondOfDay,
        TimeSpan offset,
        double fractionalSecond,
        string zoneAbbreviation,
        long unixSeconds,
        int isDaylightSaving = 0)
    {
        var civil = CivilFromDays(days);
        // glibc rejects a time_t when its actual year cannot fit tm_year.
        // jq then adds 1900 in signed-int arithmetic; at the positive edge
        // the pinned build visibly wraps that addition.
        var yearSince1900 = checked((int)(civil.Year - 1900));
        var jqYear = unchecked(yearSince1900 + 1900);
        var dayOfWeek = checked((int)FloorMod(days + 4, 7));
        var dayOfYear = checked((int)(days - DaysFromCivil(civil.Year, 1, 1) + 1));
        var isoDayOfWeek = dayOfWeek == 0 ? 7 : dayOfWeek;
        var thursday = checked(days + 4 - isoDayOfWeek);
        var isoWeekYear = CivilFromDays(thursday).Year;
        var januaryFourth = DaysFromCivil(isoWeekYear, 1, 4);
        var januaryFourthDayOfWeek = checked((int)FloorMod(januaryFourth + 4, 7));
        var januaryFourthIsoDay = januaryFourthDayOfWeek == 0 ? 7 : januaryFourthDayOfWeek;
        var firstIsoMonday = januaryFourth - (januaryFourthIsoDay - 1);
        var isoWeek = checked((int)((days - firstIsoMonday) / 7) + 1);
        // glibc carries the ISO week year through the same signed tm_year
        // representation.  At the positive tm_year boundary the observable
        // jq number therefore wraps instead of making gmtime fail.
        var isoYearSince1900 = unchecked((int)(isoWeekYear - 1900));
        var jqIsoWeekYear = unchecked(isoYearSince1900 + 1900);

        return new BrokenDownTime(
            jqYear,
            civil.Month,
            civil.Day,
            secondOfDay / 3_600,
            (secondOfDay % 3_600) / 60,
            secondOfDay % 60,
            dayOfWeek,
            dayOfYear,
            jqIsoWeekYear,
            isoWeek,
            offset,
            fractionalSecond,
            zoneAbbreviation,
            unixSeconds,
            isDaylightSaving,
            yearSince1900,
            IsNormalized: true);
    }

    // Howard Hinnant's civil-calendar transforms, expressed with mathematical
    // floor division so astronomical year zero and negative years normalize
    // exactly like glibc timegm()/strftime().
    private static long DaysFromCivil(long year, int month, int day)
    {
        year -= month <= 2 ? 1 : 0;
        var era = FloorDiv(year, 400);
        var yearOfEra = year - (era * 400);
        var monthPrime = month + (month > 2 ? -3 : 9);
        var dayOfYear = ((153L * monthPrime) + 2) / 5 + day - 1;
        var dayOfEra = (yearOfEra * 365) + (yearOfEra / 4) - (yearOfEra / 100) +
            dayOfYear;
        return checked((era * 146_097) + dayOfEra - 719_468);
    }

    private static CivilDate CivilFromDays(long days)
    {
        var shifted = checked(days + 719_468);
        var era = FloorDiv(shifted, 146_097);
        var dayOfEra = shifted - (era * 146_097);
        var yearOfEra = (dayOfEra - (dayOfEra / 1_460) + (dayOfEra / 36_524) -
            (dayOfEra / 146_096)) / 365;
        var year = yearOfEra + (era * 400);
        var dayOfYear = dayOfEra - ((365 * yearOfEra) + (yearOfEra / 4) -
            (yearOfEra / 100));
        var monthPrime = ((5 * dayOfYear) + 2) / 153;
        var day = checked((int)(dayOfYear - (((153 * monthPrime) + 2) / 5) + 1));
        var month = checked((int)(monthPrime + (monthPrime < 10 ? 3 : -9)));
        year += month <= 2 ? 1 : 0;
        return new CivilDate(year, month, day);
    }

    private static long FloorDiv(long value, long divisor)
    {
        var quotient = value / divisor;
        return value % divisor < 0 ? quotient - 1 : quotient;
    }

    private static long FloorMod(long value, long divisor) =>
        value - (FloorDiv(value, divisor) * divisor);

    private static CultureInfo ResolveTimeCulture(
        IReadOnlyDictionary<string, string>? environment)
    {
        if (environment is null)
        {
            return CultureInfo.InvariantCulture;
        }

        string? locale = null;
        foreach (var name in new[] { "LC_ALL", "LC_TIME", "LANG" })
        {
            if (libjq.jq_getenv(environment, name, out var candidate) &&
                !string.IsNullOrWhiteSpace(candidate))
            {
                locale = candidate;
                break;
            }
        }

        if (locale is null || locale is "C" or "POSIX" or "C.UTF-8" or "C.utf8")
        {
            return CultureInfo.InvariantCulture;
        }

        // jq's startup setlocale(LC_ALL, "") leaves the C locale in place
        // when a requested locale is not installed.  CultureInfo can know a
        // locale through ICU even when glibc's locale archive does not, so
        // accepting CultureInfo alone would incorrectly localize native-C
        // output (for example fr_FR.UTF-8 on a minimal container).
        if (!IsNativeLocaleInstalled(locale))
        {
            return CultureInfo.InvariantCulture;
        }

        var modifier = locale.IndexOf('@', StringComparison.Ordinal);
        if (modifier >= 0)
        {
            locale = locale[..modifier];
        }

        var encoding = locale.IndexOf('.', StringComparison.Ordinal);
        if (encoding >= 0)
        {
            locale = locale[..encoding];
        }

        try
        {
            return CultureInfo.GetCultureInfo(locale.Replace('_', '-'));
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }

    private static bool IsNativeLocaleInstalled(string locale)
    {
        if (!OperatingSystem.IsLinux())
        {
            // Installed-locale discovery is platform-specific.  The non-C
            // locale surface remains explicitly outside pinned-glibc parity
            // on other systems, where the public .NET globalization catalog
            // is the only mutation-free managed source.
            return true;
        }

        var candidates = GlibcLocaleNameCandidates(locale);
        foreach (var root in new[] { "/usr/lib/locale", "/usr/local/lib/locale" })
        {
            foreach (var candidate in candidates)
            {
                if (Directory.Exists(Path.Combine(root, candidate)))
                {
                    return true;
                }
            }

            if (LocaleArchiveContainsAny(Path.Combine(root, "locale-archive"), candidates))
            {
                return true;
            }
        }

        return false;
    }

    private static string[] GlibcLocaleNameCandidates(string locale)
    {
        var result = new HashSet<string>(StringComparer.Ordinal) { locale };
        var modifierIndex = locale.IndexOf('@', StringComparison.Ordinal);
        var modifier = modifierIndex >= 0 ? locale[modifierIndex..] : string.Empty;
        var withoutModifier = modifierIndex >= 0 ? locale[..modifierIndex] : locale;
        var encodingIndex = withoutModifier.IndexOf('.', StringComparison.Ordinal);
        if (encodingIndex >= 0)
        {
            var baseName = withoutModifier[..encodingIndex];
            var encoding = withoutModifier[(encodingIndex + 1)..];
            if (encoding.Equals("UTF-8", StringComparison.OrdinalIgnoreCase) ||
                encoding.Equals("UTF8", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(baseName + ".utf8" + modifier);
                result.Add(baseName + ".UTF-8" + modifier);
            }
        }

        return [.. result];
    }

    // glibc locale/locarchive.h.  Reading the archive index is a read-only,
    // AOT-safe availability probe; unlike setlocale/newlocale it neither
    // mutates process state nor introduces a native-library call.
    private static bool LocaleArchiveContainsAny(
        string path,
        IReadOnlyCollection<string> candidates)
    {
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length < 56)
            {
                return false;
            }

            Span<byte> header = stackalloc byte[56];
            stream.ReadExactly(header);
            var littleEndian = BinaryPrimitives.ReadUInt32LittleEndian(header) == 0xde020109;
            if (!littleEndian &&
                BinaryPrimitives.ReadUInt32BigEndian(header) != 0xde020109)
            {
                return false;
            }

            uint Read32(ReadOnlySpan<byte> value) => littleEndian
                ? BinaryPrimitives.ReadUInt32LittleEndian(value)
                : BinaryPrimitives.ReadUInt32BigEndian(value);

            var nameHashOffset = Read32(header[8..12]);
            var nameHashSize = Read32(header[16..20]);
            if (nameHashSize > 1_000_000 ||
                nameHashOffset > stream.Length ||
                (long)nameHashOffset + ((long)nameHashSize * 12) > stream.Length)
            {
                return false;
            }

            stream.Position = nameHashOffset;
            var offsets = new List<uint>();
            Span<byte> entry = stackalloc byte[12];
            for (var index = 0U; index < nameHashSize; index++)
            {
                stream.ReadExactly(entry);
                var nameOffset = Read32(entry[4..8]);
                var localeRecordOffset = Read32(entry[8..12]);
                if (nameOffset != 0 && localeRecordOffset != 0 && nameOffset < stream.Length)
                {
                    offsets.Add(nameOffset);
                }
            }

            foreach (var offset in offsets)
            {
                stream.Position = offset;
                var nameBytes = new List<byte>(32);
                for (var index = 0; index < 1_024; index++)
                {
                    var value = stream.ReadByte();
                    if (value <= 0)
                    {
                        break;
                    }

                    nameBytes.Add((byte)value);
                }

                var name = Encoding.ASCII.GetString([.. nameBytes]);
                if (candidates.Contains(name))
                {
                    return true;
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // A missing/unreadable archive is equivalent to an unavailable
            // locale for this conservative compatibility probe.
        }

        return false;
    }

    private static void AppendLocalizedDate(
        StringBuilder result,
        BrokenDownTime time,
        CultureInfo culture)
    {
        if (CanRepresentDateTime(time))
        {
            var date = new DateTime(
                checked((int)time.Year),
                time.Month,
                time.Day,
                time.Hour,
                time.Minute,
                time.Second,
                DateTimeKind.Unspecified);
            result.Append(date.ToString(culture.DateTimeFormat.ShortDatePattern, culture));
            return;
        }

        result.Append(time.Month.ToString("D2", CultureInfo.InvariantCulture));
        result.Append('/').Append(time.Day.ToString("D2", CultureInfo.InvariantCulture));
        result.Append('/').Append(FloorMod(time.Year, 100).ToString(
            "D2",
            CultureInfo.InvariantCulture));
    }

    private static void AppendLocalizedTime(
        StringBuilder result,
        BrokenDownTime time,
        CultureInfo culture)
    {
        if (CanRepresentDateTime(time))
        {
            var date = new DateTime(
                checked((int)time.Year),
                time.Month,
                time.Day,
                time.Hour,
                time.Minute,
                time.Second,
                DateTimeKind.Unspecified);
            result.Append(date.ToString(culture.DateTimeFormat.LongTimePattern, culture));
            return;
        }

        result.Append(time.Hour.ToString("D2", CultureInfo.InvariantCulture));
        result.Append(':').Append(time.Minute.ToString("D2", CultureInfo.InvariantCulture));
        result.Append(':').Append(time.Second.ToString("D2", CultureInfo.InvariantCulture));
    }

    private static bool CanRepresentDateTime(BrokenDownTime time)
    {
        if (time.Year is < 1 or > 9_999 ||
            time.Month is < 1 or > 12 ||
            time.Hour is < 0 or > 23 ||
            time.Minute is < 0 or > 59 ||
            time.Second is < 0 or > 59)
        {
            return false;
        }

        return time.Day >= 1 &&
            time.Day <= DateTime.DaysInMonth(checked((int)time.Year), time.Month);
    }

    internal static jv ParseTime(
        jv value,
        string format,
        IReadOnlyDictionary<string, string>? environment = null,
        JqTimeZoneContext? timeZoneContext = null)
    {
        if (value.Kind != jv_kind.JV_KIND_STRING)
        {
            throw new JqRuntimeException("strptime/1 requires string inputs and arguments");
        }

        // src/builtin.c:f_strptime passes both operands through libc
        // C-string pointers.  Bytes after the first NUL are invisible both to
        // strptime() and to its %s failure diagnostic.
        var input = CStringValue(value);
        var culture = ResolveTimeCulture(environment);
        var timeZone = JqTimeZone.Resolve(environment, timeZoneContext);
        var match = JqStrptimeRegex.Match(
            input,
            format,
            culture,
            seconds =>
            {
                try
                {
                    var period = timeZone.GetPeriod(seconds);
                    var localSeconds = checked(seconds + period.OffsetSeconds);
                    var brokenDown = BreakDownIntegral(
                        seconds,
                        localSeconds,
                        period,
                        fractionalSecond: 0);
                    return new JqParsedTm(
                        brokenDown.YearSince1900,
                        brokenDown.Month - 1,
                        brokenDown.Day,
                        brokenDown.Hour,
                        brokenDown.Minute,
                        brokenDown.Second,
                        brokenDown.DayOfWeek,
                        brokenDown.DayOfYear - 1,
                        brokenDown.IsDaylightSaving,
                        checked((int)brokenDown.Offset.TotalSeconds),
                        brokenDown.ZoneAbbreviation);
                }
                catch (Exception exception) when (
                    exception is ArgumentOutOfRangeException or OverflowException)
                {
                    return null;
                }
            });
        if (!match.Success)
        {
            throw new JqRuntimeException($"date \"{input}\" does not match format \"{format}\"");
        }

        var parsed = match.ParsedTime;
        var result = libjq.jv_array(
        [
            libjq.jv_number(parsed.JqYear),
            libjq.jv_number(parsed.Month),
            libjq.jv_number(parsed.Day),
            libjq.jv_number(parsed.Hour),
            libjq.jv_number(parsed.Minute),
            libjq.jv_number(parsed.Second),
            libjq.jv_number(parsed.WeekDay),
            libjq.jv_number(parsed.YearDay),
        ]);
        return match.Tail is { Length: > 0 } tail
            ? libjq.jv_array_append(result, libjq.jv_string(tail))
            : result;
    }

    private static string CStringValue(jv value)
    {
        var bytes = libjq.jvp_string_data(value);
        var nul = bytes.IndexOf((byte)0);
        return Encoding.UTF8.GetString(nul < 0 ? bytes : bytes[..nul]);
    }

    private readonly record struct CivilDate(long Year, int Month, int Day);

    private readonly record struct BrokenDownTime(
        long Year,
        int Month,
        int Day,
        int Hour,
        int Minute,
        int Second,
        int DayOfWeek,
        int DayOfYear,
        long IsoWeekYear,
        int IsoWeek,
        TimeSpan Offset,
        double FractionalSecond,
        string ZoneAbbreviation,
        long UnixSeconds,
        int IsDaylightSaving,
        int YearSince1900,
        bool IsNormalized);
}
