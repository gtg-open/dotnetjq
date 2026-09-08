// DOTNETJQ PORT TEST MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream files: src/compile.c, src/execute.c
// Primary ownership paths: gen_dictpair, INSERT, POP, and RET.

using DotNetJq.Port;

namespace DotNetJq.Tests;

public sealed class ObjectConstructionRefcountLifecycleTests
{
    [Fact]
    public void SingleResultObjectConsumesUnreferencedSourceBeforeReturn()
    {
        jq_state? state = libjq.jq_init();
        var input = libjq.jv_array([libjq.jv_number(1)]);
        var retained = libjq.jv_copy(input);
        var storage = Assert.IsType<jvp_array>(retained.Value);
        var output = libjq.jv_invalid();
        try
        {
            Assert.Equal(1, libjq.jq_compile(state, "{a: 1}"));
            libjq.jq_start(state, input, 0);
            input = libjq.jv_invalid();

            output = libjq.jq_next(state);
            Assert.True(output.IsValid);
            Assert.Equal(jv_kind.JV_KIND_OBJECT, output.Kind);
            // INSERT has consumed the temporary key/value owners, and the
            // final POP has consumed the original input below the constructed
            // object. Only this test's explicit retained owner remains.
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
            libjq.jv_free(retained);
        }

        Assert.Equal(0, storage.Refcnt.Count);
    }

    [Fact]
    public void FinalValueContinuationConsumesBothSubexpressionInputsBeforeReturn()
    {
        AssertGeneratorOwnerCounts(
            "{a: (., .)}",
            [4, 2],
            [3, 1]);
    }

    [Fact]
    public void FinalKeyContinuationConsumesBothSubexpressionInputsBeforeReturn()
    {
        AssertGeneratorOwnerCounts(
            "{(\"a\", \"b\"): .}",
            [4, 2],
            [3, 1]);
    }

    private static void AssertGeneratorOwnerCounts(
        string filter,
        IReadOnlyList<int> countsAtOutput,
        IReadOnlyList<int> countsAfterFree)
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

            for (var index = 0; index < countsAtOutput.Count; index++)
            {
                output = libjq.jq_next(state);
                Assert.True(output.IsValid);
                Assert.Equal(jv_kind.JV_KIND_OBJECT, output.Kind);
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
            libjq.jv_free(retained);
        }

        Assert.Equal(0, storage.Refcnt.Count);
    }
}
