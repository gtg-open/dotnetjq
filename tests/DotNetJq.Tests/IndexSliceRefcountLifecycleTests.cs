// DOTNETJQ PORT TEST MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream files: src/execute.c, src/jv_aux.c, src/parser.y
// Primary ownership paths: gen_index, INDEX, stack_pop, and jv_get.

using DotNetJq.Port;

namespace DotNetJq.Tests;

public sealed class IndexSliceRefcountLifecycleTests
{
    [Fact]
    public void SingleIndexConsumesRootContainerBeforeReturningChild()
    {
        jq_state? state = libjq.jq_init();
        var input = libjq.jv_array(
        [
            libjq.jv_array([libjq.jv_number(1)]),
        ]);
        var rootStorage = Assert.IsType<jvp_array>(input.Value);
        var output = libjq.jv_invalid();
        try
        {
            Assert.Equal(1, libjq.jq_compile(state, ".[0]"));
            libjq.jq_start(state, input, 0);
            input = libjq.jv_invalid();

            output = libjq.jq_next(state);
            Assert.True(output.IsValid);
            Assert.Equal(jv_kind.JV_KIND_ARRAY, output.Kind);
            // INDEX passes its owned target to jv_get. jv_get retains the
            // selected child and consumes the root before RET.
            Assert.Equal(0, rootStorage.Refcnt.Count);

            libjq.jv_free(output);
            output = libjq.jv_invalid();
            Assert.False(libjq.jq_next(state).IsValid);
            Assert.Equal(0, rootStorage.Refcnt.Count);
        }
        finally
        {
            libjq.jv_free(output);
            libjq.jv_free(input);
            libjq.jq_teardown(ref state);
        }
    }
}
