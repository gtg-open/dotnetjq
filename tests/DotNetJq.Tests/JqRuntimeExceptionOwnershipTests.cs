using DotNetJq.Port;

namespace DotNetJq.Tests;

public sealed class JqRuntimeExceptionOwnershipTests
{
    [Fact]
    public void CatchMovesTheNativeErrorOwnerIntoTheHandlerResult()
    {
        jq_state? state = libjq.jq_init();
        var input = libjq.jv_array([libjq.jv_number(1)]);
        var retained = libjq.jv_copy(input);
        var storage = Assert.IsType<jvp_array>(retained.Value);
        var output = libjq.jv_invalid();
        try
        {
            Assert.Equal(1, libjq.jq_compile(state, "try error catch ."));
            libjq.jq_start(state, input, 0);
            input = libjq.jv_invalid();

            output = libjq.jq_next(state);
            Assert.True(output.IsValid);
            Assert.Same(storage, output.Value);
            // f_error consumes its input into the error slot; CATCH then moves
            // that same owner into the handler stack slot. No source or
            // exception copy remains beside the retained test owner.
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
            libjq.jv_free(retained);
        }

        Assert.Equal(0, storage.Refcnt.Count);
    }

    [Fact]
    public void TakeErrorValueMovesTheOnlyExceptionOwner()
    {
        var error = libjq.jv_array_append(libjq.jv_array(), libjq.jv_number(1));
        var storage = Assert.IsType<jvp_array>(error.Value);
        var exception = new JqRuntimeException(error);

        Assert.Equal(1, storage.Refcnt.Count);
        Assert.NotNull(exception.ErrorValue);

        var moved = exception.TakeErrorValue();

        Assert.NotNull(moved);
        Assert.Null(exception.ErrorValue);
        exception.ReleaseErrorValue();
        Assert.Equal(1, storage.Refcnt.Count);

        libjq.jv_free(moved!.Value);
        Assert.Equal(0, storage.Refcnt.Count);
    }

    [Fact]
    public void ReleaseErrorValueIsIdempotent()
    {
        var error = libjq.jv_array();
        var storage = Assert.IsType<jvp_array>(error.Value);
        var exception = new JqRuntimeException(error);

        exception.ReleaseErrorValue();
        exception.ReleaseErrorValue();

        Assert.Null(exception.ErrorValue);
        Assert.Equal(0, storage.Refcnt.Count);
    }

    [Fact]
    public void PublicRuntimeErrorReleasesItsCopyWithoutConsumingCompiledLiteral()
    {
        var program = JqProgram.Compile("error([{\"key\":\"value\"}])");

        for (var execution = 0; execution < 3; execution++)
        {
            var exception = Assert.Throws<JqRuntimeException>(() => program.Execute("null"));
            Assert.Equal("[{\"key\":\"value\"}]", exception.Message);
            Assert.Null(exception.ErrorValue);
        }
    }
}
