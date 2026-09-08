// DOTNETJQ PORT TEST MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream files: src/compile.c, src/execute.c
// Primary ownership paths: gen_cond, gen_var_binding, gen_and, gen_or,
// EACH/ON_BACKTRACK(EACH), stack_pop, stack_save, and frame_pop.

using DotNetJq.Port;

namespace DotNetJq.Tests;

public sealed class ControlFlowRefcountLifecycleCompatibilityTests
{
    [Fact]
    public void ConditionalMatchesNativeCountsAtBothOutputsAndExhaustion()
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
                libjq.jq_compile(state, "if (true, false) then . else . end"));
            libjq.jq_start(state, input, 0);
            input = libjq.jv_invalid();

            Assert.Equal(2, storage.Refcnt.Count);

            output = libjq.jq_next(state);
            Assert.True(output.IsValid);
            Assert.Same(storage, output.Value);
            // execute.c:RET pops from a shared fork-stack block here. Native
            // jq therefore has the retained test owner, the returned owner,
            // and three owners needed by the saved false-condition path.
            Assert.Equal(5, storage.Refcnt.Count);
            libjq.jv_free(output);
            output = libjq.jv_invalid();
            Assert.Equal(4, storage.Refcnt.Count);

            output = libjq.jq_next(state);
            Assert.True(output.IsValid);
            Assert.Same(storage, output.Value);
            // The false branch is the final gen_cond continuation, so only
            // the retained test owner and the returned owner remain.
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
    public void ConditionalEarlyTeardownReleasesEverySavedBranchOwner()
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
                libjq.jq_compile(state, "if (true, false) then . else . end"));
            libjq.jq_start(state, input, 0);
            input = libjq.jv_invalid();

            output = libjq.jq_next(state);
            Assert.Equal(5, storage.Refcnt.Count);
            libjq.jv_free(output);
            output = libjq.jv_invalid();
            Assert.Equal(4, storage.Refcnt.Count);

            // jq_reset(), reached by jq_teardown(), unwinds the untouched
            // false-condition fork and frees all three of its source owners.
            libjq.jq_teardown(ref state);
            Assert.Equal(1, storage.Refcnt.Count);
        }
        finally
        {
            libjq.jv_free(output);
            libjq.jv_free(input);
            libjq.jq_teardown(ref state);
            libjq.jv_free(retained);
        }

        Assert.Equal(0, storage.Refcnt.Count);
    }

    [Fact]
    public void RegularBindMatchesStorevLoadvCountsAndEarlyTeardown()
    {
        jq_state? state = libjq.jq_init();
        var input = libjq.jv_array([libjq.jv_number(1)]);
        var retained = libjq.jv_copy(input);
        var storage = Assert.IsType<jvp_array>(retained.Value);
        var output = libjq.jv_invalid();

        try
        {
            Assert.Equal(1, libjq.jq_compile(state, ". as $x | $x, $x"));
            libjq.jq_start(state, input, 0);
            input = libjq.jv_invalid();

            output = libjq.jq_next(state);
            Assert.True(output.IsValid);
            Assert.Same(storage, output.Value);
            // gen_var_binding's STOREV cell and saved comma continuation are
            // both live when the first LOADV owner is returned.
            Assert.Equal(4, storage.Refcnt.Count);
            libjq.jv_free(output);
            output = libjq.jv_invalid();
            Assert.Equal(3, storage.Refcnt.Count);

            // Abandon the second LOADV. frame_pop and fork-stack cleanup must
            // leave exactly the explicit test owner.
            libjq.jq_teardown(ref state);
            Assert.Equal(1, storage.Refcnt.Count);
        }
        finally
        {
            libjq.jv_free(output);
            libjq.jv_free(input);
            libjq.jq_teardown(ref state);
            libjq.jv_free(retained);
        }

        Assert.Equal(0, storage.Refcnt.Count);
    }

    [Fact]
    public void RegularBindMatchesNativeCountAtFinalOutputAndExhaustion()
    {
        jq_state? state = libjq.jq_init();
        var input = libjq.jv_array([libjq.jv_number(1)]);
        var retained = libjq.jv_copy(input);
        var storage = Assert.IsType<jvp_array>(retained.Value);
        var output = libjq.jv_invalid();

        try
        {
            Assert.Equal(1, libjq.jq_compile(state, ". as $x | $x, $x"));
            libjq.jq_start(state, input, 0);
            input = libjq.jv_invalid();

            output = libjq.jq_next(state);
            libjq.jv_free(output);
            output = libjq.jv_invalid();

            output = libjq.jq_next(state);
            Assert.True(output.IsValid);
            Assert.Same(storage, output.Value);
            // On the final result native jq still has the returned LOADV
            // owner, the frame-local STOREV owner, and the retained owner.
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

    [Theory]
    [InlineData("true and .")]
    [InlineData("false or .")]
    public void BooleanBinaryOperatorsConsumeTheirSourceBeforeReturning(string filter)
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
            // gen_and/gen_or POP the duplicated input and replace the original
            // with a boolean constant before RET. The source has no VM owner
            // by the time jq_next() returns, even before output disposal.
            Assert.Equal(1, storage.Refcnt.Count);
            libjq.jv_free(output);
            output = libjq.jv_invalid();
            Assert.Equal(1, storage.Refcnt.Count);

            Assert.False(libjq.jq_next(state).IsValid);
            Assert.Equal(1, storage.Refcnt.Count);

            libjq.jq_teardown(ref state);
            Assert.Equal(1, storage.Refcnt.Count);
        }
        finally
        {
            libjq.jv_free(output);
            libjq.jv_free(input);
            libjq.jq_teardown(ref state);
            libjq.jv_free(retained);
        }

        Assert.Equal(0, storage.Refcnt.Count);
    }

    [Fact]
    public void EachFreesTheContainerBeforeReturningItsFinalArrayElement()
    {
        jq_state? state = libjq.jq_init();
        var input = libjq.jv_array(
        [
            libjq.jv_array([libjq.jv_number(1)]),
            libjq.jv_array([libjq.jv_number(2)]),
        ]);
        var retained = libjq.jv_copy(input);
        var storage = Assert.IsType<jvp_array>(retained.Value);
        var output = libjq.jv_invalid();

        try
        {
            Assert.Equal(1, libjq.jq_compile(state, ".[]"));
            libjq.jq_start(state, input, 0);
            input = libjq.jv_invalid();

            output = libjq.jq_next(state);
            Assert.True(output.IsValid);
            // ON_BACKTRACK(EACH) keeps the root container owner solely while
            // another array element remains.
            Assert.Equal(2, storage.Refcnt.Count);
            libjq.jv_free(output);
            output = libjq.jv_invalid();
            Assert.Equal(2, storage.Refcnt.Count);

            output = libjq.jq_next(state);
            Assert.True(output.IsValid);
            // execute.c:EACH detects is_last and jv_free(container) before it
            // pushes the final child. Only the retained root owner remains.
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
}
