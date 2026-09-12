// DOTNETJQ PORT TEST MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream files: src/compile.c, src/execute.c, src/parser.y
// Primary ownership paths: LOADK, POP, FORK, BACKTRACK, EACH,
// EACH_OPT, TRY_BEGIN, TRY_END, CALL_BUILTIN, LOADVN, and RET.

using DotNetJq.Port;

namespace DotNetJq.Tests;

public sealed class EvaluatorPrimitiveRefcountOracleTests
{
    // Every count and output below was measured independently against the
    // pinned jq-1.8.2 libjq with one explicit retained owner of the input.
    // The cases cover opcode/continuation ownership families not pinned by the more specialized
    // index/slice, operator, path/interpolation, function, object-construction,
    // try, and reduce/foreach ownership suites.
    public static TheoryData<OracleCase> Cases => new()
    {
        // execute.c:LOADK frees the input slot before copying the constant.
        new OracleCase(
            "loadk-replaces-input",
            "0",
            "[[1],[2]]",
            ["0"],
            [1],
            [1]),
        // A failed middle arm reaches BACKTRACK; only the outer FORK owner is
        // live while the first independent number result is returned.
        new OracleCase(
            "nested-fork-backtrack",
            "(0,empty,1)",
            "[[1],[2]]",
            ["0", "1"],
            [2, 1],
            [2, 1]),
        // Unlike arrays, execute.c cannot mark an object iterator's last
        // physical slot in advance, so ON_BACKTRACK(EACH) retains the object
        // container until the following jq_next() proves exhaustion.
        new OracleCase(
            "pipe-cross-product-forks",
            "(.,.) | (.,.)",
            "[[1],[2]]",
            ["[[1],[2]]", "[[1],[2]]", "[[1],[2]]", "[[1],[2]]"],
            [3, 3, 3, 2],
            [2, 2, 2, 1]),
        new OracleCase(
            "each-object-keeps-final-backtrack-point",
            ".[]",
            "{\"a\":[1],\"b\":[2]}",
            ["[1]", "[2]"],
            [2, 2],
            [2, 2]),
        // EACH stack_pop() copies a generated target whose earlier FORK still
        // owns the shared stack block. The last array element then frees only
        // that iteration's container owner.
        new OracleCase(
            "each-over-generated-containers",
            "(.,.)[]",
            "[[1],[2]]",
            ["[1]", "[2]", "[1]", "[2]"],
            [3, 2, 2, 1],
            [3, 2, 2, 1]),
        // The same generated-target pop applies to EACH_OPT; suppressing the
        // final scalar iteration must not erase the earlier FORK topology.
        new OracleCase(
            "each-opt-generated-container-then-scalar",
            "(.,0)[]?",
            "[[1],[2]]",
            ["[1]", "[2]"],
            [3, 2],
            [3, 2]),
        // compile.c:gen_definedor keeps a fallback input and a flag local.
        // Truthy left outputs suppress the fallback without discarding the
        // left generator's saved continuation.
        new OracleCase(
            "defined-or-truthy-left-generator",
            "(.,.) // 0",
            "[[1],[2]]",
            ["[[1],[2]]", "[[1],[2]]"],
            [3, 3],
            [2, 2]),
        // When every left result is null/false, gen_definedor backtracks into
        // the fallback; its final FORK arm moves the remaining root slot.
        new OracleCase(
            "defined-or-false-left-fallback-generator",
            "(null,false) // (.,.)",
            "[[1],[2]]",
            ["[[1],[2]]", "[[1],[2]]"],
            [3, 2],
            [2, 1]),
        // General postfix '?' is gen_try(EXP, BACKTRACK). The successful first
        // arm returns while both TRY_BEGIN and the comma continuation live;
        // the later error is consumed by the empty handler.
        new OracleCase(
            "optional-success-then-caught-error",
            "(.,error(\"x\"))?",
            "[[1],[2]]",
            ["[[1],[2]]"],
            [3],
            [2]),
        // _negate's type-error route consumes its owned input before TRY_BEGIN
        // transfers the independent diagnostic into the constant handler.
        new OracleCase(
            "negate-type-error-consumes-source",
            "try -. catch 0",
            "[[1],[2]]",
            ["0"],
            [1],
            [1]),
        // builtin.c:f_format consumes each generated input. Only the outer
        // comma's saved root remains across its first independent string.
        new OracleCase(
            "format-generated-inputs",
            "(.,.) | @json",
            "[[1],[2]]",
            ["\"[[1],[2]]\"", "\"[[1],[2]]\""],
            [2, 1],
            [2, 1]),
        // builtin.jq:_assign binds the generated RHS eagerly. Its first arm is
        // suspended with the RHS and CALL_JQ/FORK input owners still live;
        // the final arm moves them before RET.
        new OracleCase(
            "assign-generated-root-values",
            ". = (.,.)",
            "[[1],[2]]",
            ["[[1],[2]]", "[[1],[2]]"],
            [6, 2],
            [5, 1]),
        // Assigning the original root to two paths leaves two root aliases in
        // the returned array; freeing that output releases both together.
        new OracleCase(
            "assign-all-paths-from-root",
            ".[] = .",
            "[[1],[2]]",
            ["[[[1],[2]],[[1],[2]]]"],
            [3],
            [1]),
        // _modify/getpath/setpath makes the accumulator writable. With the
        // retained oracle handle present, native COW detaches the returned
        // root and leaves the measured original allocation uniquely retained.
        new OracleCase(
            "update-all-paths-identity",
            ".[] |= .",
            "[[1],[2]]",
            ["[[1],[2]]"],
            [1],
            [1]),
        // All compound operators share AssignmentNode's _modify-shaped path;
        // Add is the representative COW row for Add/Subtract/Multiply/Divide/
        // Modulo after the operand has been materialized.
        new OracleCase(
            "compound-add-all-object-paths",
            ".[] += 1",
            "{\"a\":1,\"b\":2}",
            ["{\"a\":2,\"b\":3}"],
            [1],
            [1]),
        // parser.y:gen_definedor_assign is a distinct _modify update shape.
        // Truthy values bypass replacement but still detach the accumulator
        // from the separately retained input owner.
        new OracleCase(
            "defined-or-assign-truthy-paths",
            ".[] //= 0",
            "{\"a\":1,\"b\":2}",
            ["{\"a\":1,\"b\":2}"],
            [1],
            [1]),
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void OutputOrderExhaustionAndEarlyTeardownMatchPinnedNativeOwnership(
        OracleCase oracle)
    {
        var exhausted = Observe(oracle, stopAfterFirst: false);
        var early = Observe(oracle, stopAfterFirst: true);

        Assert.Equal(oracle.Outputs, exhausted.Outputs);
        Assert.Equal(oracle.AtOutputs, exhausted.AtOutputs);
        Assert.Equal(oracle.AfterOutputFrees, exhausted.AfterOutputFrees);
        Assert.Equal(1, exhausted.AfterExhaustion);
        Assert.Equal(1, exhausted.AfterTeardown);
        Assert.Equal(0, exhausted.AfterRetainedFree);

        Assert.Equal(oracle.Outputs[0], early.Outputs[0]);
        Assert.Equal(oracle.AtOutputs[0], early.AtOutputs[0]);
        Assert.Equal(oracle.AfterOutputFrees[0], early.AfterOutputFrees[0]);
        Assert.Equal(1, early.AfterTeardown);
        Assert.Equal(0, early.AfterRetainedFree);
    }

    private static Snapshot Observe(OracleCase oracle, bool stopAfterFirst)
    {
        jq_state? state = libjq.jq_init();
        var input = libjq.jv_parse(oracle.InputJson);
        var retained = libjq.jv_copy(input);
        var cleanupHandle = retained;
        var output = libjq.jv_invalid();
        var outputs = new List<string>();
        var atOutputs = new List<int>();
        var afterOutputFrees = new List<int>();
        var afterExhaustion = -1;
        var afterTeardown = -1;
        var afterRetainedFree = -1;

        try
        {
            Assert.Equal(1, libjq.jq_compile(state, oracle.Filter));
            libjq.jq_start(state, input, 0);
            input = libjq.jv_invalid();

            while (true)
            {
                output = libjq.jq_next(state);
                if (!output.IsValid)
                {
                    afterExhaustion = Refcount(retained);
                    break;
                }

                outputs.Add(libjq.jv_dump_string_borrowed(output));
                atOutputs.Add(Refcount(retained));
                libjq.jv_free(output);
                output = libjq.jv_invalid();
                afterOutputFrees.Add(Refcount(retained));
                if (stopAfterFirst)
                {
                    break;
                }
            }
        }
        finally
        {
            libjq.jv_free(output);
            libjq.jv_free(input);
            libjq.jq_teardown(ref state);
            afterTeardown = Refcount(retained);
            libjq.jv_free(retained);
            retained = libjq.jv_invalid();
            afterRetainedFree = Refcount(cleanupHandle);

            // Ensure a failing topology assertion cannot leak into another row.
            while (Refcount(cleanupHandle) > 0)
            {
                libjq.jv_free(cleanupHandle);
            }
        }

        return new Snapshot(
            outputs.ToArray(),
            atOutputs.ToArray(),
            afterOutputFrees.ToArray(),
            afterExhaustion,
            afterTeardown,
            afterRetainedFree);
    }

    private static int Refcount(jv value) => value.Value switch
    {
        jvp_array array => array.Refcnt.Count,
        jvp_object obj => obj.Refcnt.Count,
        _ => throw new Xunit.Sdk.XunitException(
            "The lifecycle oracle requires a reference-valued root."),
    };

    public sealed record OracleCase(
        string Name,
        string Filter,
        string InputJson,
        string[] Outputs,
        int[] AtOutputs,
        int[] AfterOutputFrees)
    {
        public override string ToString() => Name;
    }

    private sealed record Snapshot(
        string[] Outputs,
        int[] AtOutputs,
        int[] AfterOutputFrees,
        int AfterExhaustion,
        int AfterTeardown,
        int AfterRetainedFree);
}
