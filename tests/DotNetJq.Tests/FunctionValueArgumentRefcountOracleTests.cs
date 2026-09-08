// DOTNETJQ PORT TEST MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream files: src/compile.c, src/execute.c
// Primary ownership paths: gen_function, gen_var_binding, gen_subexp,
// CALL_JQ, FORK, SUBEXP_BEGIN, SUBEXP_END, POP, STOREV, RET, and frame_pop.

using DotNetJq.Port;

namespace DotNetJq.Tests;

public sealed class FunctionValueArgumentRefcountOracleTests
{
    [Theory]
    [InlineData(
        "def f($x): 0; f((0,1))",
        new[] { 4, 1 })]
    [InlineData(
        "def f($x;$y): 0; f((0,1);(2,3))",
        new[] { 7, 4, 4, 1 })]
    public void ValueArgumentContinuationsMatchPinnedNativeRootCounts(
        string filter,
        int[] countsAtOutput)
    {
        jq_state? state = libjq.jq_init();
        var input = libjq.jv_array([libjq.jv_number(1)]);
        var retained = libjq.jv_copy(input);
        var storage = Assert.IsType<jvp_array>(retained.Value);
        var output = libjq.jv_invalid();

        try
        {
            Assert.Equal(1, libjq.jq_compile(state, filter));
            libjq.jq_start(state, input, 0);
            input = libjq.jv_invalid();

            foreach (var expectedCount in countsAtOutput)
            {
                output = libjq.jq_next(state);
                Assert.True(output.IsValid);
                Assert.Equal(jv_kind.JV_KIND_NUMBER, output.Kind);
                Assert.Equal(0, output.NumberValue);
                Assert.Equal(expectedCount, storage.Refcnt.Count);
                libjq.jv_free(output);
                output = libjq.jv_invalid();
                Assert.Equal(expectedCount, storage.Refcnt.Count);
            }

            Assert.False(libjq.jq_next(state).IsValid);
            Assert.Equal(1, storage.Refcnt.Count);
        }
        finally
        {
            libjq.jv_free(output);
            libjq.jv_free(input);
            libjq.jq_teardown(ref state);
            Assert.Equal(1, storage.Refcnt.Count);
            libjq.jv_free(retained);
        }

        Assert.Equal(0, storage.Refcnt.Count);
    }

    [Theory]
    [InlineData("def f($x): 0; f((0,1))", 4)]
    [InlineData("def f($x;$y): 0; f((0,1);(2,3))", 7)]
    public void EarlyTeardownReleasesSuspendedValueArgumentContinuations(
        string filter,
        int expectedCountAtFirstOutput)
    {
        jq_state? state = libjq.jq_init();
        var input = libjq.jv_array([libjq.jv_number(1)]);
        var retained = libjq.jv_copy(input);
        var storage = Assert.IsType<jvp_array>(retained.Value);
        var output = libjq.jv_invalid();

        try
        {
            Assert.Equal(1, libjq.jq_compile(state, filter));
            libjq.jq_start(state, input, 0);
            input = libjq.jv_invalid();

            output = libjq.jq_next(state);
            Assert.True(output.IsValid);
            Assert.Equal(expectedCountAtFirstOutput, storage.Refcnt.Count);
            libjq.jv_free(output);
            output = libjq.jv_invalid();

            libjq.jq_teardown(ref state);
            Assert.Equal(1, storage.Refcnt.Count);
        }
        finally
        {
            libjq.jv_free(output);
            libjq.jv_free(input);
            libjq.jq_teardown(ref state);
            Assert.Equal(1, storage.Refcnt.Count);
            libjq.jv_free(retained);
        }

        Assert.Equal(0, storage.Refcnt.Count);
    }

    [Theory]
    [InlineData(
        "def f($x): $x; f((.,.))",
        new[] { 6, 2 },
        new[] { 5, 1 })]
    [InlineData(
        "def f($x;$y): [$x,$y]; f((.,.);(.,.))",
        new[] { 11, 8, 8, 3 },
        new[] { 9, 6, 6, 1 })]
    public void ValueResultsMatchPinnedNativeOwnerLifetimes(
        string filter,
        int[] countsAtOutput,
        int[] countsAfterFree)
    {
        jq_state? state = libjq.jq_init();
        var input = libjq.jv_array([libjq.jv_number(1)]);
        var retained = libjq.jv_copy(input);
        var storage = Assert.IsType<jvp_array>(retained.Value);
        var output = libjq.jv_invalid();

        try
        {
            Assert.Equal(1, libjq.jq_compile(state, filter));
            libjq.jq_start(state, input, 0);
            input = libjq.jv_invalid();

            for (var index = 0; index < countsAtOutput.Length; index++)
            {
                output = libjq.jq_next(state);
                Assert.True(output.IsValid);
                Assert.Equal(countsAtOutput[index], storage.Refcnt.Count);
                libjq.jv_free(output);
                output = libjq.jv_invalid();
                Assert.Equal(countsAfterFree[index], storage.Refcnt.Count);
            }

            Assert.False(libjq.jq_next(state).IsValid);
            Assert.Equal(1, storage.Refcnt.Count);
        }
        finally
        {
            libjq.jv_free(output);
            libjq.jv_free(input);
            libjq.jq_teardown(ref state);
            Assert.Equal(1, storage.Refcnt.Count);
            libjq.jv_free(retained);
        }

        Assert.Equal(0, storage.Refcnt.Count);
    }

    [Fact]
    public void MultipleValueArgumentsKeepNativeRightmostFastestOrder()
    {
        var actual = JqProgram
            .Compile("def f($x;$y): [$x,$y]; f((0,1);(2,3))")
            .Execute("null")
            .Select(static value => value.GetRawText())
            .ToArray();

        // f's prologue binds x before y.  Consequently x's FORK surrounds
        // y's complete subexpression and the rightmost value formal changes
        // fastest: this is the exact jq-1.8.2 CALL_JQ/RET order.
        Assert.Equal(["[0,2]", "[0,3]", "[1,2]", "[1,3]"], actual);
    }

    [Fact]
    public void FilterParametersRemainLazyBesideAValueParameter()
    {
        var actual = JqProgram
            .Compile("def f(g;$x): g | [$x,.]; f((0,1);(2,3))")
            .Execute("null")
            .Select(static value => value.GetRawText())
            .ToArray();

        Assert.Equal(["[2,0]", "[2,1]", "[3,0]", "[3,1]"], actual);
    }
}
