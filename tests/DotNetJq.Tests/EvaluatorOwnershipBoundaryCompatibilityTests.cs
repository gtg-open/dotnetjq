// DOTNETJQ PORT TEST MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream files: src/parser.y, src/compile.c, src/execute.c, src/builtin.c,
//                 and src/jv_aux.c
// Primary ownership paths: gen_index, gen_slice_index, gen_binop, gen_cond,
// gen_reduce, gen_foreach, INDEX, CALL_BUILTIN, LOADVN, f_format, f_setpath,
// and jv_setpath.

using DotNetJq.Port;

namespace DotNetJq.Tests;

public sealed class EvaluatorOwnershipBoundaryCompatibilityTests
{
    [Fact]
    public void IndexConsumesItsRootBeforeReturningTheSelectedChild()
    {
        jq_state? state = libjq.jq_init();
        var input = libjq.jv_array(
            [libjq.jv_array([libjq.jv_number(1)])]);
        var retained = libjq.jv_copy(input);
        var storage = Assert.IsType<jvp_array>(retained.Value);
        var output = libjq.jv_invalid();
        try
        {
            Assert.Equal(1, libjq.jq_compile(state, ".[0]"));
            libjq.jq_start(state, input, 0);
            input = libjq.jv_invalid();

            output = libjq.jq_next(state);
            Assert.Equal(jv_kind.JV_KIND_ARRAY, output.Kind);
            // execute.c:INDEX pops/consumes target `t` before pushing the
            // selected child. The explicit retained owner is the only owner
            // of the outer source allocation once jq_next returns.
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
    public void SliceDoesNotRetainAnExtraTargetOwnerAtReturn()
    {
        jq_state? state = libjq.jq_init();
        var input = libjq.jv_array([libjq.jv_number(1), libjq.jv_number(2)]);
        var retained = libjq.jv_copy(input);
        var storage = Assert.IsType<jvp_array>(retained.Value);
        var output = libjq.jv_invalid();
        try
        {
            Assert.Equal(1, libjq.jq_compile(state, ".[0:]"));
            libjq.jq_start(state, input, 0);
            input = libjq.jv_invalid();

            output = libjq.jq_next(state);
            Assert.Equal(jv_kind.JV_KIND_ARRAY, output.Kind);
            Assert.Same(storage, output.Value);
            // Retained test owner + the array-slice result. gen_slice_index
            // leaves no third copy of the target below the INDEX result.
            Assert.Equal(2, storage.Refcnt.Count);
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
    public void SetpathMovesAUniqueRootIntoJvSetpath()
    {
        AssertUniqueArrayUpdateReusesStorage("setpath([0]; 1)");
    }

    [Fact]
    public void AssignmentMovesAUniqueAccumulatorThroughSetpath()
    {
        AssertUniqueArrayUpdateReusesStorage(".[0] = 1");
    }

    [Fact]
    public void UpdateAssignmentMovesAUniqueAccumulatorThroughSetpath()
    {
        AssertUniqueArrayUpdateReusesStorage(".[0] |= . + 1");
    }

    [Fact]
    public void SingleConditionConsumesThePreservedInputBeforeBranchReturn()
    {
        AssertSingleIndependentResultReleasesSource(
            "if true then 0 else 1 end",
            jv_kind.JV_KIND_NUMBER);
    }

    [Fact]
    public void GeneratorBinopConsumesSourceBeforeItsFinalResult()
    {
        jq_state? state = libjq.jq_init();
        var input = libjq.jv_array([libjq.jv_number(1)]);
        var retained = libjq.jv_copy(input);
        var storage = Assert.IsType<jvp_array>(retained.Value);
        var output = libjq.jv_invalid();
        try
        {
            Assert.Equal(1, libjq.jq_compile(state, "(0, 0) + 0"));
            libjq.jq_start(state, input, 0);
            input = libjq.jv_invalid();

            output = libjq.jq_next(state);
            Assert.Equal(jv_kind.JV_KIND_NUMBER, output.Kind);
            libjq.jv_free(output);
            output = libjq.jv_invalid();

            output = libjq.jq_next(state);
            Assert.Equal(jv_kind.JV_KIND_NUMBER, output.Kind);
            // The final left continuation has consumed CALL_BUILTIN's implicit
            // input. Only the test's retained owner remains.
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
    public void ReduceFinalLoadvnConsumesTheOriginalInputBeforeReturn()
    {
        AssertSingleIndependentResultReleasesSource(
            "reduce empty as $x (0; .)",
            jv_kind.JV_KIND_NUMBER);
    }

    [Fact]
    public void ForeachLoadvnConsumesTheOriginalInputBeforeExtractReturn()
    {
        AssertSingleIndependentResultReleasesSource(
            "foreach [0][] as $x (0; .; 0)",
            jv_kind.JV_KIND_NUMBER);
    }

    [Fact]
    public void FormatConsumesItsInputBeforeReturningFormattedText()
    {
        AssertSingleIndependentResultReleasesSource(
            "@json",
            jv_kind.JV_KIND_STRING);
    }

    [Fact]
    public void InterpolationConsumesItsInputBeforeReturningTheString()
    {
        AssertSingleIndependentResultReleasesSource(
            "\"\\(.)\"",
            jv_kind.JV_KIND_STRING);
    }

    [Fact]
    public void PathEndReleasesValueAtPathBeforeReturningThePath()
    {
        AssertSingleIndependentResultReleasesSource(
            "path(.[0])",
            jv_kind.JV_KIND_ARRAY);
    }

    [Fact]
    public void InsertConsumesTheValueSlotBeforeReturningTheObject()
    {
        jq_state? state = libjq.jq_init();
        var input = libjq.jv_array([libjq.jv_number(1)]);
        var retained = libjq.jv_copy(input);
        var storage = Assert.IsType<jvp_array>(retained.Value);
        var output = libjq.jv_invalid();
        try
        {
            Assert.Equal(1, libjq.jq_compile(state, "{a: .}"));
            libjq.jq_start(state, input, 0);
            input = libjq.jv_invalid();

            output = libjq.jq_next(state);
            Assert.Equal(jv_kind.JV_KIND_OBJECT, output.Kind);
            // Retained test owner + the object value slot. INSERT has already
            // consumed the temporary value stack slot and POP consumed input.
            Assert.Equal(2, storage.Refcnt.Count);
            libjq.jv_free(output);
            output = libjq.jv_invalid();
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

    private static void AssertSingleIndependentResultReleasesSource(
        string filter,
        jv_kind expectedKind)
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
            Assert.Equal(expectedKind, output.Kind);
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

    private static void AssertUniqueArrayUpdateReusesStorage(string filter)
    {
        jq_state? state = libjq.jq_init();
        var input = libjq.jv_array([libjq.jv_number(0)]);
        var storage = Assert.IsType<jvp_array>(input.Value);
        var output = libjq.jv_invalid();
        try
        {
            Assert.Equal(1, libjq.jq_compile(state, filter));
            libjq.jq_start(state, input, 0);
            input = libjq.jv_invalid();

            output = libjq.jq_next(state);
            Assert.Equal(jv_kind.JV_KIND_ARRAY, output.Kind);
            // jq's CALL_BUILTIN transfers its unique implicit input directly
            // into jv_setpath. The source allocation is therefore updated and
            // returned in place rather than entering the COW branch.
            Assert.Same(storage, output.Value);
            Assert.Equal(1, storage.Refcnt.Count);
        }
        finally
        {
            libjq.jv_free(output);
            libjq.jv_free(input);
            libjq.jq_teardown(ref state);
        }

        Assert.Equal(0, storage.Refcnt.Count);
    }
}
