// DOTNETJQ PORT TEST MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream files: src/compile.c, src/execute.c
// Primary ownership paths: gen_and, gen_or, gen_cond, gen_var_binding,
// CALL_JQ, FORK, RET, LOADV, STOREV, stack_pop, and frame_pop.

using DotNetJq.Port;

namespace DotNetJq.Tests;

public sealed class ControlFlowNestedRefcountOracleTests
{
    [Theory]
    [InlineData("(if true then . else empty end) and true")]
    [InlineData("true and (if true then . else empty end)")]
    [InlineData("(if true then . else empty end) and (if true then . else empty end)")]
    public void NestedSingleValuedBooleanOperandsReleaseSourceBeforeYield(string filter)
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
            Assert.Equal(jv_kind.JV_KIND_TRUE, output.Kind);
            // Native gen_and has POPped every original/duplicate source slot
            // before RET, including slots used by a nested gen_cond. The
            // explicit retained test handle is consequently the sole owner.
            Assert.Equal(1, storage.Refcnt.Count);
            libjq.jv_free(output);
            output = libjq.jv_invalid();
            Assert.Equal(1, storage.Refcnt.Count);

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
    public void NestedSingleValuedBindSourceMatchesNativeLoadvCounts()
    {
        jq_state? state = libjq.jq_init();
        var input = libjq.jv_array([libjq.jv_number(1)]);
        var retained = libjq.jv_copy(input);
        var storage = Assert.IsType<jvp_array>(retained.Value);
        var output = libjq.jv_invalid();

        try
        {
            Assert.Equal(
                1,
                libjq.jq_compile(
                    state,
                    "(if true then . else empty end) as $x | $x, $x"));
            libjq.jq_start(state, input, 0);
            input = libjq.jv_invalid();

            output = libjq.jq_next(state);
            Assert.True(output.IsValid);
            Assert.Same(storage, output.Value);
            // Retained test owner + returned LOADV owner + STOREV local + the
            // saved comma continuation. The completed source gen_cond owns no
            // additional source slot at this point in native execute.c.
            Assert.Equal(4, storage.Refcnt.Count);
            libjq.jv_free(output);
            output = libjq.jv_invalid();
            Assert.Equal(3, storage.Refcnt.Count);

            output = libjq.jq_next(state);
            Assert.True(output.IsValid);
            Assert.Same(storage, output.Value);
            Assert.Equal(3, storage.Refcnt.Count);
            libjq.jv_free(output);
            output = libjq.jv_invalid();
            Assert.Equal(2, storage.Refcnt.Count);

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
    public void NestedSingleValuedBindSourceEarlyTeardownUnwindsSourceAndLocal()
    {
        jq_state? state = libjq.jq_init();
        var input = libjq.jv_array([libjq.jv_number(1)]);
        var retained = libjq.jv_copy(input);
        var storage = Assert.IsType<jvp_array>(retained.Value);
        var output = libjq.jv_invalid();
        var atOutput = -1;
        var afterOutputFree = -1;
        var afterTeardown = -1;

        try
        {
            Assert.Equal(
                1,
                libjq.jq_compile(
                    state,
                    "(if true then . else empty end) as $x | $x, $x"));
            libjq.jq_start(state, input, 0);
            input = libjq.jv_invalid();

            output = libjq.jq_next(state);
            atOutput = storage.Refcnt.Count;
            libjq.jv_free(output);
            output = libjq.jv_invalid();
            afterOutputFree = storage.Refcnt.Count;

            libjq.jq_teardown(ref state);
            afterTeardown = storage.Refcnt.Count;
        }
        finally
        {
            libjq.jv_free(output);
            libjq.jv_free(input);
            libjq.jq_teardown(ref state);
            libjq.jv_free(retained);
        }

        Assert.Equal((4, 3, 1), (atOutput, afterOutputFree, afterTeardown));
        Assert.Equal(0, storage.Refcnt.Count);
    }

    [Theory]
    [InlineData("if ((if true then true else false end), false) then . else . end")]
    [InlineData("if (true, false) then (if true then . else empty end) else . end")]
    public void NestedConditionalFastPathMatchesNativeForkCounts(string filter)
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
            Assert.Same(storage, output.Value);
            Assert.Equal(5, storage.Refcnt.Count);
            libjq.jv_free(output);
            output = libjq.jv_invalid();
            Assert.Equal(4, storage.Refcnt.Count);

            output = libjq.jq_next(state);
            Assert.True(output.IsValid);
            Assert.Same(storage, output.Value);
            Assert.Equal(2, storage.Refcnt.Count);
            libjq.jv_free(output);
            output = libjq.jv_invalid();
            Assert.Equal(1, storage.Refcnt.Count);

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
    public void BooleanFastPathDoesNotDiscardDynamicHelperResults()
    {
        const string filter = "def _plus(a;b): a,b; true and (. + 2)";
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
            Assert.Equal(jv_kind.JV_KIND_TRUE, output.Kind);
            // The first helper result retains the original source in CALL_JQ's
            // saved second-result continuation.
            Assert.Equal(2, storage.Refcnt.Count);
            libjq.jv_free(output);
            output = libjq.jv_invalid();
            Assert.Equal(2, storage.Refcnt.Count);

            output = libjq.jq_next(state);
            Assert.Equal(jv_kind.JV_KIND_TRUE, output.Kind);
            Assert.Equal(1, storage.Refcnt.Count);
            libjq.jv_free(output);
            output = libjq.jv_invalid();

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
    public void BindFastPathDoesNotDiscardDynamicHelperResults()
    {
        const string filter = "def _plus(a;b): a,b; (. + 2) as $x | $x";
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
            Assert.Same(storage, output.Value);
            Assert.Equal(6, storage.Refcnt.Count);
            libjq.jv_free(output);
            output = libjq.jv_invalid();
            Assert.Equal(5, storage.Refcnt.Count);

            output = libjq.jq_next(state);
            Assert.True(output.IsValid);
            Assert.Equal(jv_kind.JV_KIND_NUMBER, output.Kind);
            Assert.Equal(2, output.NumberValue);
            Assert.Equal(1, storage.Refcnt.Count);
            libjq.jv_free(output);
            output = libjq.jv_invalid();

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
    public void BindFastPathPreservesDynamicHelperResultSequence()
    {
        const string filter = "def _plus(a;b): a,b; (. + 2) as $x | $x";

        var actual = JqProgram.Compile(filter)
            .Execute("[1]")
            .Select(static value => value.GetRawText())
            .ToArray();

        // Native CALL_JQ resumes _plus's comma continuation, then the binder
        // executes once for each source result.
        Assert.Equal(["[1]", "2"], actual);
    }

    [Fact]
    public void ConditionalFastPathPreservesDynamicBranchMultiplicityAndCounts()
    {
        const string filter =
            "def _plus(a;b): a,b; if (true,false) then (. + 2) else . end";
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
            Assert.Same(storage, output.Value);
            Assert.Equal(6, storage.Refcnt.Count);
            libjq.jv_free(output);
            output = libjq.jv_invalid();
            Assert.Equal(5, storage.Refcnt.Count);

            output = libjq.jq_next(state);
            Assert.Equal(jv_kind.JV_KIND_NUMBER, output.Kind);
            Assert.Equal(2, output.NumberValue);
            Assert.Equal(4, storage.Refcnt.Count);
            libjq.jv_free(output);
            output = libjq.jv_invalid();
            Assert.Equal(4, storage.Refcnt.Count);

            output = libjq.jq_next(state);
            Assert.Same(storage, output.Value);
            Assert.Equal(2, storage.Refcnt.Count);
            libjq.jv_free(output);
            output = libjq.jv_invalid();
            Assert.Equal(1, storage.Refcnt.Count);

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
    public void ConditionalFastPathPreservesDynamicConditionMultiplicity()
    {
        const string filter =
            "def _plus(a;b): a,b; if ((. + 2), false) then . else . end";
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

            int[] expectedCounts = [6, 5, 2];
            foreach (var expected in expectedCounts)
            {
                output = libjq.jq_next(state);
                Assert.True(output.IsValid);
                Assert.Same(storage, output.Value);
                Assert.Equal(expected, storage.Refcnt.Count);
                libjq.jv_free(output);
                output = libjq.jv_invalid();
            }

            Assert.Equal(1, storage.Refcnt.Count);
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
}
