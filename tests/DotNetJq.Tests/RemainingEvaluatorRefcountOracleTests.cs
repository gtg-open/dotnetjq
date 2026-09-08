// DOTNETJQ PORT TEST MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream files: src/compile.c, src/execute.c, src/parser.y
// Primary ownership paths: gen_index, gen_slice_index, gen_binop, gen_and,
// gen_or, gen_cond, gen_collect, gen_var_binding, INDEX, LOADV, LOADVN,
// FORK, CALL_JQ, RET, and frame_pop.

using DotNetJq.Port;

namespace DotNetJq.Tests;

public sealed class RemainingEvaluatorRefcountOracleTests
{
    // Measured against the pinned native libjq with one explicit retained root
    // owner. These are continuation/closure ownership assertions, not merely
    // output-compatibility cases: all inputs therefore use allocated arrays.
    public static TheoryData<OracleCase> Cases => new()
    {
        new OracleCase(
            "generated-index",
            ".[(0,1)]",
            "[[10],[20]]",
            ["[10]", "[20]"],
            [3, 1],
            [3, 1]),
        new OracleCase(
            "generated-slice-start",
            ".[(0,1):2]",
            "[[10],[20],[30]]",
            ["[[10],[20]]", "[[20]]"],
            [4, 2],
            [3, 1]),
        new OracleCase(
            "generated-slice-bounds-cartesian",
            ".[(0,1):(1,2)]",
            "[[10],[20],[30]]",
            ["[[10]]", "[[10],[20]]", "[]", "[[20]]"],
            [6, 4, 3, 2],
            [5, 3, 3, 1]),
        new OracleCase(
            "generated-path-index",
            "path(.[(0,1)])",
            "[[10],[20]]",
            ["[0]", "[1]"],
            [4, 1],
            [4, 1]),
        new OracleCase(
            "generated-interpolation",
            "\"x\\(.,.)y\"",
            "[1]",
            ["\"x[1]y\"", "\"x[1]y\""],
            [4, 1],
            [4, 1]),
        new OracleCase(
            "interpolation-segment-cartesian",
            "\"\\((1,2))\\((3,4))\"",
            "[0]",
            ["\"13\"", "\"23\"", "\"14\"", "\"24\""],
            [6, 3, 4, 1],
            [6, 3, 4, 1]),
        new OracleCase(
            "binary-cartesian",
            "(1,2) + (10,20)",
            "[0]",
            ["11", "12", "21", "22"],
            [5, 3, 3, 1],
            [5, 3, 3, 1]),
        new OracleCase(
            "and-cartesian-short-circuit",
            "(true,false) and (true,false)",
            "[0]",
            ["true", "false", "false"],
            [3, 3, 1],
            [3, 3, 1]),
        new OracleCase(
            "or-cartesian-short-circuit",
            "(false,true) or (false,true)",
            "[0]",
            ["false", "true", "true"],
            [3, 3, 1],
            [3, 3, 1]),
        new OracleCase(
            "conditional-cartesian",
            "if (true,false) then (1,2) else (3,4) end",
            "[0]",
            ["1", "2", "3", "4"],
            [4, 4, 2, 1],
            [4, 4, 2, 1]),
        new OracleCase(
            "loadvn-collect",
            "[(.,.)]",
            "[1]",
            ["[[1],[1]]"],
            [3],
            [1]),
        new OracleCase(
            "captured-local",
            ". as $x | def f: $x; f",
            "[1]",
            ["[1]"],
            [3],
            [2]),
        new OracleCase(
            "value-argument-captured-by-inner-closure",
            "def f($x): def g: $x; g; f(.)",
            "[1]",
            ["[1]"],
            [2],
            [1]),
        new OracleCase(
            "value-argument-captured-by-generating-inner-closure",
            "def f($x): def g: ($x,$x); g; f(.)",
            "[1]",
            ["[1]", "[1]"],
            [4, 2],
            [3, 1]),
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void OutputExhaustionAndEarlyTeardownMatchPinnedNativeOwnership(
        OracleCase oracle)
    {
        var exhausted = Observe(oracle.Filter, oracle.InputJson, stopAfterFirst: false);
        var early = Observe(oracle.Filter, oracle.InputJson, stopAfterFirst: true);

        Assert.Equal(
            "outputs=[" + string.Join('|', oracle.Outputs) + "];" +
            "at=[" + string.Join(',', oracle.AtOutputs) + "];" +
            "freed=[" + string.Join(',', oracle.AfterOutputFrees) + "];" +
            "exhausted=1;teardown=1;released=0|" +
            $"early-output={oracle.Outputs[0]};" +
            $"early-at={oracle.AtOutputs[0]};" +
            $"early-freed={oracle.AfterOutputFrees[0]};" +
            "early-teardown=1;early-released=0",
            Format(exhausted, includeExhaustion: true) + "|" +
            Format(early, includeExhaustion: false));
    }

    private static LifecycleSnapshot Observe(
        string filter,
        string inputJson,
        bool stopAfterFirst)
    {
        jq_state? state = libjq.jq_init();
        var input = libjq.jv_parse(inputJson);
        var retained = libjq.jv_copy(input);
        var cleanupHandle = retained;
        var output = libjq.jv_invalid();
        var outputs = new List<string>();
        var atOutputs = new List<int>();
        var afterOutputFrees = new List<int>();
        var afterExhaustion = -1;
        var afterTeardown = -1;
        var afterTestOwnerRelease = -1;

        try
        {
            Assert.Equal(1, libjq.jq_compile(state, filter));
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
            afterTestOwnerRelease = Refcount(cleanupHandle);
            ReleaseUnexpectedLeakedOwners(cleanupHandle);
        }

        return new LifecycleSnapshot(
            outputs,
            atOutputs,
            afterOutputFrees,
            afterExhaustion,
            afterTeardown,
            afterTestOwnerRelease);
    }

    private static string Format(
        LifecycleSnapshot snapshot,
        bool includeExhaustion)
    {
        if (includeExhaustion)
        {
            return "outputs=[" + string.Join('|', snapshot.Outputs) + "];" +
                   "at=[" + string.Join(',', snapshot.AtOutputs) + "];" +
                   "freed=[" + string.Join(',', snapshot.AfterOutputFrees) + "];" +
                   $"exhausted={snapshot.AfterExhaustion};" +
                   $"teardown={snapshot.AfterTeardown};" +
                   $"released={snapshot.AfterTestOwnerRelease}";
        }

        return $"early-output={snapshot.Outputs[0]};" +
               $"early-at={snapshot.AtOutputs[0]};" +
               $"early-freed={snapshot.AfterOutputFrees[0]};" +
               $"early-teardown={snapshot.AfterTeardown};" +
               $"early-released={snapshot.AfterTestOwnerRelease}";
    }

    private static int Refcount(jv value) => value.Value switch
    {
        jvp_array array => array.Refcnt.Count,
        _ => throw new InvalidOperationException(
            "The oracle matrix requires a reference-backed array input."),
    };

    private static void ReleaseUnexpectedLeakedOwners(jv cleanupHandle)
    {
        while (Refcount(cleanupHandle) > 0)
        {
            libjq.jv_free(cleanupHandle);
        }
    }

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

    private readonly record struct LifecycleSnapshot(
        IReadOnlyList<string> Outputs,
        IReadOnlyList<int> AtOutputs,
        IReadOnlyList<int> AfterOutputFrees,
        int AfterExhaustion,
        int AfterTeardown,
        int AfterTestOwnerRelease);
}
