// DOTNETJQ PORT TEST MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream files: src/compile.c, src/execute.c
// Primary ownership paths: RANGE, EACH_OPT, TAIL_CALL_JQ, GENLABEL,
// TRY_BEGIN, DESTRUCTURE_ALT, STOREV, STOREVN, LOADV, LOADVN, and RET.

using DotNetJq.Port;

namespace DotNetJq.Tests;

public sealed class UncoveredOpcodeRefcountOracleTests
{
    // The expected counts were measured against the pinned native libjq with
    // one explicit retained root owner. Every row was first checked through
    // full exhaustion and then in a fresh state torn down after output one.
    // These rows are retained because each exposes a concrete managed/native
    // suspension-topology mismatch; the broader passing audit is reported in
    // the task handoff rather than becoming redundant production tests.
    public static TheoryData<string, string, int[], int[]> Cases => new()
    {
        // compile.c: builtin range/2; execute.c:RANGE and LOADVN.
        { "range(0;2)", "[[1],[2]]", [1, 1], [1, 1] },

        // builtin.jq recurse/1; execute.c:TAIL_CALL_JQ and EACH_OPT. The root
        // dies before native's fourth output even though descendants continue.
        { "..", "[[1],[2]]", [3, 2, 2, 1, 1], [2, 2, 2, 1, 1] },

        // parser.y destructuring lowers through INDEX, STOREV, POP, and LOADV.
        { ". as [$x] | $x", "[[1],[2]]", [1], [1] },
        { ". as [$x,$y] | $x,$y", "[[1],[2]]", [2, 1], [2, 1] },

        // parser.y's ?// form reaches execute.c:DESTRUCTURE_ALT. Exercise both
        // the first-pattern and alternate-pattern ownership paths.
        { ". as [$x] ?// {a:$x} | $x", "[[1],[2]]", [4], [4] },
        { ". as [$x] ?// {a:$x} | $x", "{\"a\":[1]}", [1], [1] },

        // builtin.jq first/1 and limit/2 lower through GENLABEL, TRY_BEGIN,
        // FORK, CALL_JQ, and the label-error BACKTRACK unwinder.
        // Direct labels pin the GENLABEL/gen_wildvar_binding contribution by
        // itself: it does not add the two root owners missing from the nested
        // first/limit rows below. The comma row also exercises a matched
        // BreakNode, handler equality, and both exhaustion/early teardown.
        { "label $out | .", "[[1],[2]]", [3], [2] },
        { "label $out | (., break $out)", "[[1],[2]]", [3], [2] },

        // Wrapping the same protected expression in a jq function adds no
        // blanket owner: CALL_JQ moves its unique input into the callee frame.
        { "def f: label $out | .; f", "[[1],[2]]", [3], [2] },

        // Invoking a filter closure below TRY_BEGIN does cross a shared stack
        // block, so CALL_JQ materializes the closed caller-frame slot.
        { "def f(g): label $out | g; f(.)", "[[1],[2]]", [4], [3] },

        // builtin.jq combines the same protected filter-call topology with
        // its source-level stop/break control flow.
        { "first((.,.))", "[[1],[2]]", [5], [4] },
        { "limit(1; (.,.))", "[[1],[2]]", [6], [5] },

    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void OutputExhaustionAndEarlyTeardownMatchPinnedNativeOwnership(
        string filter,
        string inputJson,
        int[] expectedAtOutput,
        int[] expectedAfterOutputFree)
    {
        var exhausted = RunLifecycle(filter, inputJson, stopAfterFirst: false);
        var early = RunLifecycle(filter, inputJson, stopAfterFirst: true);

        Assert.Equal(
            $"outputs=[{string.Join(',', expectedAtOutput)}];" +
            $"freed=[{string.Join(',', expectedAfterOutputFree)}];" +
            "exhausted=1;teardown=1;released=0|" +
            $"early-output={expectedAtOutput[0]};" +
            $"early-freed={expectedAfterOutputFree[0]};" +
            "early-teardown=1;early-released=0",
            $"outputs=[{string.Join(',', exhausted.AtOutput)}];" +
            $"freed=[{string.Join(',', exhausted.AfterOutputFree)}];" +
            $"exhausted={exhausted.AfterExhaustion};" +
            $"teardown={exhausted.AfterTeardown};" +
            $"released={exhausted.AfterTestOwnerRelease}|" +
            $"early-output={early.AtOutput[0]};" +
            $"early-freed={early.AfterOutputFree[0]};" +
            $"early-teardown={early.AfterTeardown};" +
            $"early-released={early.AfterTestOwnerRelease}");
    }

    private static LifecycleSnapshot RunLifecycle(
        string filter,
        string inputJson,
        bool stopAfterFirst)
    {
        jq_state? state = libjq.jq_init();
        var input = libjq.jv_parse(inputJson);
        var retained = libjq.jv_copy(input);
        var cleanupHandle = retained;
        Func<int> getRefcnt = retained.Value switch
        {
            jvp_array array => () => array.Refcnt.Count,
            jvp_object obj => () => obj.Refcnt.Count,
            _ => throw new Xunit.Sdk.XunitException(
                "The lifecycle oracle requires a reference-valued root."),
        };
        var output = libjq.jv_invalid();
        var atOutput = new List<int>();
        var afterOutputFree = new List<int>();
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
                    afterExhaustion = getRefcnt();
                    break;
                }

                atOutput.Add(getRefcnt());
                libjq.jv_free(output);
                output = libjq.jv_invalid();
                afterOutputFree.Add(getRefcnt());
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
            afterTeardown = getRefcnt();
            libjq.jv_free(retained);
            retained = libjq.jv_invalid();
            afterTestOwnerRelease = getRefcnt();

            // Keep a failing ownership assertion from contaminating the next
            // row. This handle aliases the native-shaped allocation only for
            // emergency cleanup after the measurements above are complete.
            while (getRefcnt() > 0)
            {
                libjq.jv_free(cleanupHandle);
            }
        }

        return new LifecycleSnapshot(
            atOutput.ToArray(),
            afterOutputFree.ToArray(),
            afterExhaustion,
            afterTeardown,
            afterTestOwnerRelease);
    }

    private sealed record LifecycleSnapshot(
        int[] AtOutput,
        int[] AfterOutputFree,
        int AfterExhaustion,
        int AfterTeardown,
        int AfterTestOwnerRelease);
}
