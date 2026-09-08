// DOTNETJQ PORT TEST MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream files: src/compile.c, src/execute.c
// Primary ownership paths: gen_reduce, gen_foreach, DUP, DUPN, FORK,
// STOREV, LOADVN, and RET.

using DotNetJq.Port;

namespace DotNetJq.Tests;

public sealed class ReduceForeachRefcountLifecycleTests
{
    public static TheoryData<string, int[], int[]> Cases => new()
    {
        // gen_reduce's DUPN nulls the source slot shared by the pending second
        // initial branch. Native therefore keeps only that pending initial and
        // the returned accumulator at output one, then moves the final one.
        {
            "reduce empty as $x ((.,.); 0)",
            [3, 2],
            [2, 1]
        },

        {
            "reduce empty as $x ((.,.,.); 0)",
            [3, 3, 2],
            [2, 2, 1]
        },

        // The source comma's final arm consumes its source value through the
        // matcher STOREV. LOADVN then moves the sole accumulator slot to RET.
        {
            "reduce (0,1) as $x (.; .)",
            [2],
            [1]
        },

        // STOREV replaces the accumulator for each update continuation; only
        // the final update result survives the loop's forced BACKTRACK.
        {
            "reduce 0 as $x (.; (.,.))",
            [2],
            [1]
        },

        // gen_foreach's source FORK retains one input stack slot for its right
        // arm. The first result copies that slot; the final arm moves it.
        {
            "foreach (.,.) as $x (0; .; 0)",
            [3, 2],
            [3, 2]
        },

        // The initial-expression FORK retains the first accumulator branch;
        // the final initial branch has neither that owner nor a later source
        // continuation once its only source result has been stored.
        {
            "foreach 0 as $x ((.,.); .; .)",
            [5, 3],
            [4, 2]
        },

        {
            "foreach 0 as $x ((.,.,.); .; .)",
            [5, 5, 3],
            [4, 4, 2]
        },

        // Each update continuation is stored before extraction. The first
        // comma arm has its saved update plus state and extract owners; the
        // final arm has only state and extract.
        {
            "foreach 0 as $x (.; (.,.); .)",
            [4, 3],
            [3, 2]
        },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void SuspensionExhaustionAndEarlyTeardownMatchPinnedNativeOwnership(
        string filter,
        int[] expectedAtOutput,
        int[] expectedAfterOutputFree)
    {
        var exhausted = RunLifecycle(filter, stopAfterFirst: false);
        var early = RunLifecycle(filter, stopAfterFirst: true);

        Assert.Equal(expectedAtOutput, exhausted.AtOutput);
        Assert.Equal(expectedAfterOutputFree, exhausted.AfterOutputFree);
        Assert.Equal(1, exhausted.AfterExhaustion);
        Assert.Equal(1, exhausted.AfterTeardown);
        Assert.Equal(0, exhausted.AfterTestOwnerRelease);

        Assert.Equal(expectedAtOutput[0], early.AtOutput[0]);
        Assert.Equal(expectedAfterOutputFree[0], early.AfterOutputFree[0]);
        Assert.Equal(1, early.AfterTeardown);
        Assert.Equal(0, early.AfterTestOwnerRelease);
    }

    [Fact]
    public void ReduceDupnReplacesTheSharedSourceSlotWithNullForLaterInitialArms()
    {
        jq_state? state = libjq.jq_init();
        var input = libjq.jv_array(
            [libjq.jv_array([libjq.jv_number(1)]), libjq.jv_array([libjq.jv_number(2)])]);
        var retained = libjq.jv_copy(input);
        var storage = Assert.IsType<jvp_array>(retained.Value);
        var output = libjq.jv_invalid();
        try
        {
            Assert.Equal(1, libjq.jq_compile(state, "reduce .[] as $x ((.,.); .)"));
            libjq.jq_start(state, input, 0);
            input = libjq.jv_invalid();

            output = libjq.jq_next(state);
            Assert.True(libjq.jv_identical(libjq.jv_copy(output), libjq.jv_copy(retained)));
            libjq.jv_free(output);
            output = libjq.jv_invalid();

            output = libjq.jq_next(state);
            Assert.False(output.IsValid);
            Assert.True(libjq.jv_invalid_has_msg(libjq.jv_copy(output)));
            var message = libjq.jv_invalid_get_msg(output);
            output = libjq.jv_invalid();
            try
            {
                Assert.Equal("Cannot iterate over null (null)", message.StringValue);
            }
            finally
            {
                libjq.jv_free(message);
            }

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

    private static LifecycleSnapshot RunLifecycle(string filter, bool stopAfterFirst)
    {
        jq_state? state = libjq.jq_init();
        var input = libjq.jv_array(
            [libjq.jv_array([libjq.jv_number(1)]), libjq.jv_array([libjq.jv_number(2)])]);
        var retained = libjq.jv_copy(input);
        var cleanupHandle = retained;
        var storage = Assert.IsType<jvp_array>(retained.Value);
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
            afterTestOwnerRelease = storage.Refcnt.Count;

            while (storage.Refcnt.Count > 0)
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
