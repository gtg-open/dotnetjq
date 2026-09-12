// DOTNETJQ PORT TEST MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream files: src/compile.c (gen_try), src/execute.c (TRY_BEGIN/TRY_END)

using DotNetJq.Port;

namespace DotNetJq.Tests;

public sealed class TryRefcountLifecycleCompatibilityTests
{
    // Counts were measured against the pinned jq-1.8.2 libjq with one
    // explicit retained owner of the array input. They pin the saved
    // TRY_BEGIN input slot, TRY_END success continuation, and caught-error
    // transfer separately instead of asserting only the JSON result.
    public static TheoryData<string, int[], int[]> NativeCases => new()
    {
        { "try . catch 0", [3], [2] },
        { "try (.,.) catch 0|0", [2, 2], [2, 2] },
        { "try error catch .", [2], [1] },
        { "try error catch 0", [1], [1] },
        { "try (.,error) catch .", [3, 2], [2, 1] },
        // builtin.jq:pick/1 reaches f_setpath through a dynamic replacement.
        // Its invalid-with-message result must become the error slot consumed
        // by ON_BACKTRACK(TRY_BEGIN), never a yielded invalid value lease.
        { "try pick(last) catch .", [1], [1] },
    };

    [Theory]
    [MemberData(nameof(NativeCases))]
    public void TryBeginSuccessAndErrorContinuationsMatchPinnedNativeOwnership(
        string filter,
        int[] expectedAtOutput,
        int[] expectedAfterOutputFree)
    {
        var exhausted = Observe(filter, stopAfterFirst: false);
        var early = Observe(filter, stopAfterFirst: true);

        Assert.Equal(expectedAtOutput, exhausted.AtOutput);
        Assert.Equal(expectedAfterOutputFree, exhausted.AfterOutputFree);
        Assert.Equal(1, exhausted.AfterExhaustion);
        Assert.Equal(1, exhausted.AfterTeardown);
        Assert.Equal(0, exhausted.AfterRetainedFree);

        Assert.Equal(expectedAtOutput[0], early.AtOutput[0]);
        Assert.Equal(expectedAfterOutputFree[0], early.AfterOutputFree[0]);
        Assert.Equal(1, early.AfterTeardown);
        Assert.Equal(0, early.AfterRetainedFree);
    }

    [Fact]
    public void TryCatchesTheInvalidResultReturnedByDynamicSetpathInsidePick()
    {
        jq_state? state = libjq.jq_init();
        var output = libjq.jv_invalid();
        try
        {
            Assert.Equal(1, libjq.jq_compile(state, "try pick(last) catch ."));
            libjq.jq_start(state, libjq.jv_parse("[1,2]"), 0);

            output = libjq.jq_next(state);
            Assert.True(output.IsValid);
            Assert.Equal(jv_kind.JV_KIND_STRING, output.Kind);
            Assert.Equal("Out of bounds negative array index", output.StringValue);
        }
        finally
        {
            libjq.jv_free(output);
            libjq.jq_teardown(ref state);
        }
    }

    [Fact]
    public void BarePickReturnsTheDynamicSetpathErrorAsAnInvalidMessage()
    {
        jq_state? state = libjq.jq_init();
        var failure = libjq.jv_invalid();
        var message = libjq.jv_invalid();
        try
        {
            Assert.Equal(1, libjq.jq_compile(state, "pick(last)"));
            libjq.jq_start(state, libjq.jv_parse("[1,2]"), 0);

            failure = libjq.jq_next(state);
            Assert.False(failure.IsValid);
            Assert.True(libjq.jv_invalid_has_msg(libjq.jv_copy(failure)));
            message = libjq.jv_invalid_get_msg(failure);
            failure = libjq.jv_invalid();
            Assert.Equal("Out of bounds negative array index", message.StringValue);
        }
        finally
        {
            libjq.jv_free(message);
            libjq.jv_free(failure);
            libjq.jq_teardown(ref state);
        }
    }

    private static Snapshot Observe(string filter, bool stopAfterFirst)
    {
        jq_state? state = libjq.jq_init();
        var input = libjq.jv_array([libjq.jv_number(1)]);
        var retained = libjq.jv_copy(input);
        var cleanup = retained;
        var storage = Assert.IsType<jvp_array>(retained.Value);
        var output = libjq.jv_invalid();
        var atOutput = new List<int>();
        var afterOutputFree = new List<int>();
        var afterExhaustion = -1;
        var afterTeardown = -1;
        var afterRetainedFree = -1;

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
                    afterExhaustion = storage.Refcnt.Count;
                    break;
                }

                atOutput.Add(storage.Refcnt.Count);
                libjq.jv_free(output);
                output = libjq.jv_invalid();
                afterOutputFree.Add(storage.Refcnt.Count);
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
            afterTeardown = storage.Refcnt.Count;
            libjq.jv_free(retained);
            retained = libjq.jv_invalid();
            afterRetainedFree = storage.Refcnt.Count;

            while (storage.Refcnt.Count > 0)
            {
                libjq.jv_free(cleanup);
            }
        }

        return new Snapshot(
            atOutput.ToArray(),
            afterOutputFree.ToArray(),
            afterExhaustion,
            afterTeardown,
            afterRetainedFree);
    }

    private readonly record struct Snapshot(
        int[] AtOutput,
        int[] AfterOutputFree,
        int AfterExhaustion,
        int AfterTeardown,
        int AfterRetainedFree);
}
