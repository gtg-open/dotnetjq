// DOTNETJQ PORT TEST MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream files: src/compile.c, src/execute.c, src/builtin.c, src/jv_aux.c
// Primary ownership paths: gen_subexp, PUSHK_UNDER, CALL_BUILTIN, stack_pop,
// f_has, f_bsearch, f_contains, f_delpaths, jv_contains, and jv_delpaths.

using DotNetJq.Port;

namespace DotNetJq.Tests;

public sealed class BuiltinCallRefcountLifecycleCompatibilityTests
{
    [Fact]
    public void LiteralCFunctionArgumentConsumesTheFinalImplicitInputBeforeReturning()
    {
        var snapshot = RunSingleResult("has(0)");

        Assert.True(snapshot.OutputWasValid);
        // PUSHK_UNDER creates no saved argument continuation. CALL_BUILTIN
        // moves the sole program input into f_has(), which consumes it before
        // RET. The explicit test owner is therefore the only remaining owner.
        Assert.Equal(1, snapshot.AtOutput);
        Assert.Equal(1, snapshot.AfterOutputFree);
        Assert.True(snapshot.Exhausted);
        Assert.Equal(1, snapshot.AfterExhaustion);
        Assert.Equal(1, snapshot.AfterTeardown);
        Assert.Equal(0, snapshot.AfterTestOwnerRelease);
    }

    [Fact]
    public void MultiResultCFunctionArgumentMovesTheSavedInputIntoItsFinalCall()
    {
        jq_state? state = libjq.jq_init();
        var input = libjq.jv_array([libjq.jv_number(1)]);
        var retained = libjq.jv_copy(input);
        var cleanupHandle = retained;
        var storage = Assert.IsType<jvp_array>(retained.Value);
        var output = libjq.jv_invalid();
        var firstValid = false;
        var firstCount = -1;
        var afterFirstFree = -1;
        var secondValid = false;
        var secondCount = -1;
        var afterSecondFree = -1;
        var exhausted = false;
        var afterExhaustion = -1;
        var afterTeardown = -1;
        var afterTestOwnerRelease = -1;

        try
        {
            Assert.Equal(1, libjq.jq_compile(state, "has((0,1))"));
            libjq.jq_start(state, input, 0);
            input = libjq.jv_invalid();

            output = libjq.jq_next(state);
            firstValid = output.IsValid;
            firstCount = storage.Refcnt.Count;
            libjq.jv_free(output);
            output = libjq.jv_invalid();
            afterFirstFree = storage.Refcnt.Count;

            output = libjq.jq_next(state);
            secondValid = output.IsValid;
            secondCount = storage.Refcnt.Count;
            libjq.jv_free(output);
            output = libjq.jv_invalid();
            afterSecondFree = storage.Refcnt.Count;

            output = libjq.jq_next(state);
            exhausted = !output.IsValid;
            afterExhaustion = storage.Refcnt.Count;
        }
        finally
        {
            libjq.jv_free(output);
            libjq.jv_free(input);
            libjq.jq_teardown(ref state);
            afterTeardown = storage.Refcnt.Count;
            libjq.jv_free(retained);
            retained = libjq.jv_invalid();
            afterTestOwnerRelease = storage.Refcnt.Count;
            ReleaseUnexpectedLeakedOwners(cleanupHandle, storage);
        }

        Assert.True(firstValid);
        // The saved comma branch still contains both SUBEXP input slots.
        Assert.Equal(3, firstCount);
        Assert.Equal(3, afterFirstFree);
        Assert.True(secondValid);
        // The final branch has no saved fork. f_has() consumes its input, so
        // only the explicit test owner survives while the boolean is returned.
        Assert.Equal(1, secondCount);
        Assert.Equal(1, afterSecondFree);
        Assert.True(exhausted);
        Assert.Equal(1, afterExhaustion);
        Assert.Equal(1, afterTeardown);
        Assert.Equal(0, afterTestOwnerRelease);
    }

    [Fact]
    public void MultiResultCFunctionArgumentReleasesSavedInputOnEarlyTeardown()
    {
        jq_state? state = libjq.jq_init();
        var input = libjq.jv_array([libjq.jv_number(1)]);
        var retained = libjq.jv_copy(input);
        var cleanupHandle = retained;
        var storage = Assert.IsType<jvp_array>(retained.Value);
        var output = libjq.jv_invalid();
        var outputWasValid = false;
        var atOutput = -1;
        var afterOutputFree = -1;
        var afterTeardown = -1;
        var afterTestOwnerRelease = -1;

        try
        {
            Assert.Equal(1, libjq.jq_compile(state, "has((0,1))"));
            libjq.jq_start(state, input, 0);
            input = libjq.jv_invalid();

            output = libjq.jq_next(state);
            outputWasValid = output.IsValid;
            atOutput = storage.Refcnt.Count;
            libjq.jv_free(output);
            output = libjq.jv_invalid();
            afterOutputFree = storage.Refcnt.Count;
        }
        finally
        {
            libjq.jv_free(output);
            libjq.jv_free(input);
            libjq.jq_teardown(ref state);
            afterTeardown = storage.Refcnt.Count;
            libjq.jv_free(retained);
            retained = libjq.jv_invalid();
            afterTestOwnerRelease = storage.Refcnt.Count;
            ReleaseUnexpectedLeakedOwners(cleanupHandle, storage);
        }

        Assert.True(outputWasValid);
        // The unvisited comma branch still owns both SUBEXP input slots while
        // the first result is suspended. jq_teardown frees the complete data
        // and fork stacks even though the final argument branch never runs.
        Assert.Equal(3, atOutput);
        Assert.Equal(3, afterOutputFree);
        Assert.Equal(1, afterTeardown);
        Assert.Equal(0, afterTestOwnerRelease);
    }

    [Fact]
    public void BinarySearchLiteralArgumentConsumesEveryCallInputOwnerBeforeReturning()
    {
        var snapshot = RunSingleResult("bsearch(1)");

        Assert.True(snapshot.OutputWasValid);
        // f_bsearch owns and frees input and target. A literal target lowers
        // through PUSHK_UNDER, so no saved argument fork remains at RET.
        Assert.Equal(1, snapshot.AtOutput);
        Assert.Equal(1, snapshot.AfterOutputFree);
        Assert.True(snapshot.Exhausted);
        Assert.Equal(1, snapshot.AfterExhaustion);
        Assert.Equal(1, snapshot.AfterTeardown);
        Assert.Equal(0, snapshot.AfterTestOwnerRelease);
    }

    [Fact]
    public void ReferenceValuedArgumentIsConsumedAtTheCFunctionCallBoundary()
    {
        var snapshot = RunSingleResult("contains(.)");

        Assert.True(snapshot.OutputWasValid);
        // CALL_BUILTIN pops both the implicit input and the explicit argument;
        // f_contains transfers both to consuming jv_contains(). Native jq has
        // therefore released both aliases before returning the boolean. The
        // managed single-argument path must likewise detach/dispose the
        // ForkInputNode producer and move both owners before yielding.
        Assert.Equal(1, snapshot.AtOutput);
        Assert.Equal(1, snapshot.AfterOutputFree);
        Assert.True(snapshot.Exhausted);
        Assert.Equal(1, snapshot.AfterExhaustion);
        Assert.Equal(1, snapshot.AfterTeardown);
        Assert.Equal(0, snapshot.AfterTestOwnerRelease);
    }

    [Fact]
    public void CFunctionTypeErrorConsumesInputBeforeTheCatchHandlerRuns()
    {
        var snapshot = RunSingleResult("try has(\"x\") catch .");

        Assert.True(snapshot.OutputWasValid);
        // CALL_BUILTIN pops the input and key before f_has enters jv_has(). Its
        // invalid-key branch constructs the error and explicitly frees both
        // owned values before returning invalid, so the catch handler cannot
        // retain the original input allocation. The explicit test owner is
        // the only owner while the caught error string is returned.
        Assert.Equal(1, snapshot.AtOutput);
        Assert.Equal(1, snapshot.AfterOutputFree);
        Assert.True(snapshot.Exhausted);
        Assert.Equal(1, snapshot.AfterExhaustion);
        Assert.Equal(1, snapshot.AfterTeardown);
        Assert.Equal(0, snapshot.AfterTestOwnerRelease);
    }

    [Theory]
    [InlineData("delpaths([[]])")]
    [InlineData("delpaths([[0]])")]
    public void DelpathsConsumesItsOwnedRootOnEveryReturnPath(string filter)
    {
        var snapshot = RunSingleResult(filter);

        Assert.True(snapshot.OutputWasValid);
        // f_delpaths transfers both arguments to jv_delpaths(). An empty root
        // path frees the root before returning null; an array deletion consumes
        // the root while producing the replacement. Once the invocation is
        // exhausted, neither path may leave a permanent owner. Output-time
        // CALL_BUILTIN ownership is isolated by the has() tests above.
        Assert.True(snapshot.Exhausted);
        Assert.Equal(1, snapshot.AfterExhaustion);
        Assert.Equal(1, snapshot.AfterTeardown);
        Assert.Equal(0, snapshot.AfterTestOwnerRelease);
    }

    private static LifecycleSnapshot RunSingleResult(string filter)
    {
        jq_state? state = libjq.jq_init();
        var input = libjq.jv_array([libjq.jv_number(1)]);
        var retained = libjq.jv_copy(input);
        var cleanupHandle = retained;
        var storage = Assert.IsType<jvp_array>(retained.Value);
        var output = libjq.jv_invalid();
        var outputWasValid = false;
        var atOutput = -1;
        var afterOutputFree = -1;
        var exhausted = false;
        var afterExhaustion = -1;
        var afterTeardown = -1;
        var afterTestOwnerRelease = -1;

        try
        {
            Assert.Equal(1, libjq.jq_compile(state, filter));
            libjq.jq_start(state, input, 0);
            input = libjq.jv_invalid();

            output = libjq.jq_next(state);
            outputWasValid = output.IsValid;
            atOutput = storage.Refcnt.Count;
            libjq.jv_free(output);
            output = libjq.jv_invalid();
            afterOutputFree = storage.Refcnt.Count;

            output = libjq.jq_next(state);
            exhausted = !output.IsValid;
            afterExhaustion = storage.Refcnt.Count;
        }
        finally
        {
            libjq.jv_free(output);
            libjq.jv_free(input);
            libjq.jq_teardown(ref state);
            afterTeardown = storage.Refcnt.Count;
            libjq.jv_free(retained);
            retained = libjq.jv_invalid();
            afterTestOwnerRelease = storage.Refcnt.Count;
            ReleaseUnexpectedLeakedOwners(cleanupHandle, storage);
        }

        return new LifecycleSnapshot(
            outputWasValid,
            atOutput,
            afterOutputFree,
            exhausted,
            afterExhaustion,
            afterTeardown,
            afterTestOwnerRelease);
    }

    private static void ReleaseUnexpectedLeakedOwners(jv cleanupHandle, jvp_array storage)
    {
        // A failing ownership regression must not leave intentionally leaked
        // allocations in the shared test process. Once the legitimate test
        // owner is released, use the equivalent value handle only to discharge
        // any unexpected logical owners exposed by the assertion snapshot.
        while (storage.Refcnt.Count > 0)
        {
            libjq.jv_free(cleanupHandle);
        }
    }

    private readonly record struct LifecycleSnapshot(
        bool OutputWasValid,
        int AtOutput,
        int AfterOutputFree,
        bool Exhausted,
        int AfterExhaustion,
        int AfterTeardown,
        int AfterTestOwnerRelease);
}
