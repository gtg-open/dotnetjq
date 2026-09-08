// DOTNETJQ PORT TEST MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream files: src/compile.c:expand_call_arglist/gen_subexp and
// src/execute.c:CALL_BUILTIN; src/builtin.c consuming C functions.
// Native measurements: jq-1.8.2 libjq, with one explicit test owner retained
// around jq_start/jq_next exactly as this managed harness does.

using DotNetJq.Port;

namespace DotNetJq.Tests;

public sealed class BuiltinArgumentContinuationRefcountOracleTests
{
    [Theory]
    [InlineData("startswith((\"a\",\"b\"))", "\"abc\"")]
    [InlineData("split((\"b\",\"c\"))", "\"abc\"")]
    [InlineData("_strindices((\"b\",\"c\"))", "\"abcabc\"")]
    [InlineData("contains((., []))", "[1]")]
    [InlineData("getpath(([0],[0]))", "[1]")]
    [InlineData("delpaths(([[0]], [[]]))", "[1]")]
    [InlineData("_sort_by_impl(([0],[0]))", "[1]")]
    [InlineData("format((\"json\",\"text\"))", "[1]")]
    [InlineData("strftime((\"%Y\",\"%m\"))", "[2020,0,1,0,0,0,0,1]")]
    public void FinalUnaryArgumentAlternativeMovesTheSavedImplicitInput(
        string filter,
        string input)
    {
        var snapshot = Observe(filter, input);

        // The first comma arm keeps both gen_subexp input slots in its FORK
        // snapshot. The final arm has no saved continuation: CALL_BUILTIN pops
        // its argument and moves the last implicit-input owner into the C
        // function, which consumes it before returning.
        Assert.Equal([3, 1], snapshot.AtOutputs);
        Assert.Equal([3, 1], snapshot.AfterOutputFrees);
        Assert.Equal(1, snapshot.AfterExhaustion);
        Assert.Equal(1, snapshot.AfterTeardown);
        Assert.Equal(0, snapshot.AfterTestOwnerRelease);
    }

    [Fact]
    public void BinarySearchGeneratorMatchesCallBuiltinContinuationCounts()
    {
        var snapshot = Observe("bsearch((0,1))", "[1]");

        // src/builtin.c:f_bsearch consumes input and target on every call.
        Assert.Equal(["-1", "0"], snapshot.Outputs);
        Assert.Equal([3, 1], snapshot.AtOutputs);
        Assert.Equal([3, 1], snapshot.AfterOutputFrees);
        Assert.Equal(1, snapshot.AfterExhaustion);
        Assert.Equal(1, snapshot.AfterTeardown);
        Assert.Equal(0, snapshot.AfterTestOwnerRelease);
    }

    [Fact]
    public void SetPathCartesianArgumentsFollowNativeStackOrderAndOwners()
    {
        var snapshot = Observe("setpath(([0],[0]); (2,3))", "[1]");

        // expand_call_arglist prepends each gen_subexp: the leftmost explicit
        // argument is the inner continuation and therefore changes fastest.
        Assert.Equal(["[2]", "[2]", "[3]", "[3]"], snapshot.Outputs);
        Assert.Equal([5, 3, 3, 1], snapshot.AtOutputs);
        Assert.Equal([5, 3, 3, 1], snapshot.AfterOutputFrees);
        Assert.Equal(1, snapshot.AfterExhaustion);
        Assert.Equal(1, snapshot.AfterTeardown);
        Assert.Equal(0, snapshot.AfterTestOwnerRelease);
    }

    [Fact]
    public void BinaryLibmArgumentsFollowNativeStackOrderAndOwners()
    {
        var snapshot = Observe("atan2((1,2); (3,4))", "[1]");

        Assert.Equal(
            [
                "0.3217505543966422",
                "0.5880026035475675",
                "0.24497866312686414",
                "0.4636476090008061",
            ],
            snapshot.Outputs);
        Assert.Equal([5, 3, 3, 1], snapshot.AtOutputs);
        Assert.Equal([5, 3, 3, 1], snapshot.AfterOutputFrees);
        Assert.Equal(1, snapshot.AfterExhaustion);
        Assert.Equal(1, snapshot.AfterTeardown);
        Assert.Equal(0, snapshot.AfterTestOwnerRelease);
    }

    [Fact]
    public void TernaryLibmArgumentsFollowNativeStackOrderAndOwners()
    {
        var snapshot = Observe("fma((1,2); (3,4); (5,6))", "[1]");

        Assert.Equal(["8", "11", "9", "13", "9", "12", "10", "14"], snapshot.Outputs);
        Assert.Equal([7, 5, 5, 3, 5, 3, 3, 1], snapshot.AtOutputs);
        Assert.Equal([7, 5, 5, 3, 5, 3, 3, 1], snapshot.AfterOutputFrees);
        Assert.Equal(1, snapshot.AfterExhaustion);
        Assert.Equal(1, snapshot.AfterTeardown);
        Assert.Equal(0, snapshot.AfterTestOwnerRelease);
    }

    [Fact]
    public void MatchPrimitiveArgumentsFollowNativeStackOrderAndOwners()
    {
        var snapshot = Observe(
            "_match_impl((\"1\",\"x\"); (\"\",\"i\"); (true,false))",
            "\"1\"");

        Assert.Equal(
            [
                "true",
                "false",
                "true",
                "false",
                "[{\"offset\":0,\"length\":1,\"string\":\"1\",\"captures\":[]}]",
                "[]",
                "[{\"offset\":0,\"length\":1,\"string\":\"1\",\"captures\":[]}]",
                "[]",
            ],
            snapshot.Outputs);
        Assert.Equal([7, 5, 5, 3, 5, 3, 3, 1], snapshot.AtOutputs);
        Assert.Equal([7, 5, 5, 3, 5, 3, 3, 1], snapshot.AfterOutputFrees);
        Assert.Equal(1, snapshot.AfterExhaustion);
        Assert.Equal(1, snapshot.AfterTeardown);
        Assert.Equal(0, snapshot.AfterTestOwnerRelease);
    }

    [Fact]
    public void FinalTypeErrorConsumesEveryCallBuiltinOwnerBeforeCatch()
    {
        var snapshot = Observe("try startswith((\"a\",0)) catch .", "\"abc\"");

        // f_startswith's ret_error2 path consumes both popped operands. The
        // final argument arm also has no saved FORK, so catch sees no owner of
        // the original string beyond the explicit test handle.
        Assert.Equal(["true", "\"startswith() requires string inputs\""], snapshot.Outputs);
        Assert.Equal([4, 1], snapshot.AtOutputs);
        Assert.Equal([4, 1], snapshot.AfterOutputFrees);
        Assert.Equal(1, snapshot.AfterExhaustion);
        Assert.Equal(1, snapshot.AfterTeardown);
        Assert.Equal(0, snapshot.AfterTestOwnerRelease);
    }

    private static LifecycleSnapshot Observe(string filter, string inputJson)
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
                    break;
                }

                outputs.Add(libjq.jv_dump_string_borrowed(output));
                atOutputs.Add(Refcount(retained));
                libjq.jv_free(output);
                output = libjq.jv_invalid();
                afterOutputFrees.Add(Refcount(retained));
            }

            afterExhaustion = Refcount(retained);
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

    private static int Refcount(jv value) => value.Value switch
    {
        jvp_array array => array.Refcnt.Count,
        jvp_string text => text.Refcnt.Count,
        _ => throw new InvalidOperationException("Test input must use reference-backed storage."),
    };

    private static void ReleaseUnexpectedLeakedOwners(jv cleanupHandle)
    {
        while (Refcount(cleanupHandle) > 0)
        {
            libjq.jv_free(cleanupHandle);
        }
    }

    private readonly record struct LifecycleSnapshot(
        IReadOnlyList<string> Outputs,
        IReadOnlyList<int> AtOutputs,
        IReadOnlyList<int> AfterOutputFrees,
        int AfterExhaustion,
        int AfterTeardown,
        int AfterTestOwnerRelease);
}
