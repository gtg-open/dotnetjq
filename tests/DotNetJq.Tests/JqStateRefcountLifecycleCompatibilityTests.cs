// DOTNETJQ PORT TEST MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream files: src/execute.c, src/builtin.c, src/main.c
// Primary ownership paths: jq_start/jq_next/jq_teardown and f_debug/debug_cb.

using System.Text.Json;
using DotNetJq.Port;

namespace DotNetJq.Tests;

public sealed class JqStateRefcountLifecycleCompatibilityTests
{
    [Fact]
    public void CompiledBytecodeRemainsStateOwnedAcrossSequentialReset()
    {
        jq_state? state = libjq.jq_init();
        bytecode compiled = null!;
        try
        {
            Assert.Equal(1, libjq.jq_compile(state, "try pick(last) catch ."));
            compiled = Assert.IsType<bytecode>(state.Bytecode);

            libjq.jq_start(state, libjq.jv_parse("[1,2]"), 0);
            var output = libjq.jq_next(state);
            try
            {
                Assert.True(output.IsValid);
                Assert.Equal("Out of bounds negative array index", output.StringValue);
            }
            finally
            {
                libjq.jv_free(output);
            }

            libjq.jq_reset(state);
            Assert.Same(compiled, state.Bytecode);

            libjq.jq_start(state, libjq.jv_parse("[1,2]"), 0);
            output = libjq.jq_next(state);
            try
            {
                Assert.True(output.IsValid);
                Assert.Equal("Out of bounds negative array index", output.StringValue);
            }
            finally
            {
                libjq.jv_free(output);
            }
        }
        finally
        {
            libjq.jq_teardown(ref state);
        }

        Assert.Empty(compiled.code);
        Assert.False(compiled.constants.IsValid);
        Assert.Empty(compiled.subfunctions);
    }

    [Fact]
    public void TeardownReleasesThePrimaryInputOwnerTransferredToJqStart()
    {
        jq_state? state = libjq.jq_init();
        var input = libjq.jv_array([libjq.jv_number(1)]);
        var storage = Assert.IsType<jvp_array>(input.Value);
        try
        {
            Assert.Equal(1, libjq.jq_compile(state, "., ."));
            libjq.jq_start(state, input, 0);
            input = libjq.jv_invalid();

            var first = libjq.jq_next(state);
            Assert.Equal(2, storage.Refcnt.Count);
            libjq.jv_free(first);
            Assert.Equal(1, storage.Refcnt.Count);

            var second = libjq.jq_next(state);
            // The restored right FORK slot is moved to jq_next; unlike the
            // first result, no later continuation retains another owner.
            Assert.Equal(1, storage.Refcnt.Count);
            libjq.jv_free(second);
            Assert.Equal(0, storage.Refcnt.Count);
            Assert.False(libjq.jq_next(state).IsValid);
        }
        finally
        {
            libjq.jv_free(input);
            libjq.jq_teardown(ref state);
        }

        Assert.Equal(0, storage.Refcnt.Count);
        Assert.Empty(storage.Elements);
    }

    [Fact]
    public void ExhaustionImmediatelyReleasesAnUnconsumedPrimaryInputSlot()
    {
        jq_state? state = libjq.jq_init();
        var input = libjq.jv_array([libjq.jv_number(1)]);
        var storage = Assert.IsType<jvp_array>(input.Value);
        try
        {
            Assert.Equal(1, libjq.jq_compile(state, "empty"));
            libjq.jq_start(state, input, 0);
            input = libjq.jv_invalid();

            Assert.False(libjq.jq_next(state).IsValid);
            Assert.Equal(0, storage.Refcnt.Count);
            Assert.Empty(storage.Elements);
        }
        finally
        {
            libjq.jv_free(input);
            libjq.jq_teardown(ref state);
        }
    }

    [Fact]
    public void HaltImmediatelyReleasesAnUnconsumedPrimaryInputSlot()
    {
        jq_state? state = libjq.jq_init();
        var input = libjq.jv_array([libjq.jv_number(1)]);
        var storage = Assert.IsType<jvp_array>(input.Value);
        try
        {
            Assert.Equal(1, libjq.jq_compile(state, "halt"));
            libjq.jq_start(state, input, 0);
            input = libjq.jv_invalid();

            Assert.False(libjq.jq_next(state).IsValid);
            Assert.Equal(0, storage.Refcnt.Count);
            Assert.Empty(storage.Elements);
        }
        finally
        {
            libjq.jv_free(input);
            libjq.jq_teardown(ref state);
        }
    }

    [Fact]
    public void DebugCallbackConsumesOnlyItsCopyAndLeavesTheInputForReturn()
    {
        var sink = new RecordingSink();
        jq_state? state = libjq.jq_init(
            capabilities: new JqExecutionCapabilities { Debug = sink });
        var input = libjq.jv_array([libjq.jv_number(7)]);
        var storage = Assert.IsType<jvp_array>(input.Value);
        try
        {
            Assert.Equal(1, libjq.jq_compile(state, "debug"));
            libjq.jq_start(state, input, 0);
            input = libjq.jv_invalid();

            var output = libjq.jq_next(state);
            Assert.Single(sink.Values);
            Assert.Equal("[7]", sink.Values[0].GetRawText());
            // f_debug's callback copy has been consumed and RET moves the
            // original VM slot to jq_next, leaving one output owner.
            Assert.Equal(1, storage.Refcnt.Count);
            libjq.jv_free(output);
            Assert.Equal(0, storage.Refcnt.Count);
            Assert.False(libjq.jq_next(state).IsValid);
        }
        finally
        {
            libjq.jv_free(input);
            libjq.jq_teardown(ref state);
        }

        Assert.Equal(0, storage.Refcnt.Count);
    }

    [Fact]
    public void PickTransfersItsAccumulatorOwnerExactlyOnce()
    {
        jq_state? state = libjq.jq_init();
        try
        {
            Assert.Equal(1, libjq.jq_compile(state, "pick(.a.b.c)"));
            libjq.jq_start(state, libjq.jv_null(), 0);

            var output = libjq.jq_next(state);
            try
            {
                Assert.True(output.IsValid);
                Assert.Equal("{\"a\":{\"b\":{\"c\":null}}}", libjq.jv_dump_string_borrowed(output));
            }
            finally
            {
                libjq.jv_free(output);
            }

            Assert.False(libjq.jq_next(state).IsValid);
        }
        finally
        {
            libjq.jq_teardown(ref state);
        }
    }

    [Fact]
    public void FirstToStreamReleasesOwnersQueuedForAbandonedObjectSiblings()
    {
        jq_state? state = libjq.jq_init();
        var input = libjq.jv_object(
        [
            new("a", libjq.jv_string("first")),
            new("b", libjq.jv_string("abandoned")),
        ]);
        var retained = libjq.jv_copy(input);
        var rootStorage = Assert.IsType<jvp_object>(retained.Value);
        var abandoned = libjq.jv_object_get(
            libjq.jv_copy(retained),
            libjq.jv_string("b"));
        var abandonedStorage = Assert.IsType<jvp_string>(abandoned.Value);
        libjq.jv_free(abandoned);
        try
        {
            Assert.Equal(1, abandonedStorage.Refcnt.Count);
            Assert.Equal(1, libjq.jq_compile(state, "first(tostream)"));
            libjq.jq_start(state, input, 0);
            input = libjq.jv_invalid();

            var output = libjq.jq_next(state);
            try
            {
                Assert.True(output.IsValid);
                // PATH_BEGIN/PATH_END follows the selected path lazily.  The
                // sibling has not been visited and must not acquire an owner
                // merely because first/1 has a suspended continuation.
                Assert.Equal(1, abandonedStorage.Refcnt.Count);
            }
            finally
            {
                libjq.jv_free(output);
            }

            Assert.False(libjq.jq_next(state).IsValid);
            Assert.Equal(1, abandonedStorage.Refcnt.Count);
        }
        finally
        {
            libjq.jv_free(input);
            libjq.jq_teardown(ref state);
            Assert.Equal(1, rootStorage.Refcnt.Count);
            libjq.jv_free(retained);
        }

        Assert.Equal(0, abandonedStorage.Refcnt.Count);
    }

    [Fact]
    public void ResettingToStreamReleasesItsSuspendedObjectContinuation()
    {
        jq_state? state = libjq.jq_init();
        var input = libjq.jv_object(
        [
            new("a", libjq.jv_string("first")),
            new("b", libjq.jv_string("abandoned")),
        ]);
        var abandoned = libjq.jv_object_get(
            libjq.jv_copy(input),
            libjq.jv_string("b"));
        var abandonedStorage = Assert.IsType<jvp_string>(abandoned.Value);
        libjq.jv_free(abandoned);
        try
        {
            Assert.Equal(1, libjq.jq_compile(state, "tostream"));
            libjq.jq_start(state, libjq.jv_copy(input), 0);
            var output = libjq.jq_next(state);
            try
            {
                Assert.True(output.IsValid);
                // Native path(recurse) is lazy: the saved continuation owns
                // the outer object handle, not a separate copy of every
                // unvisited sibling value.
                Assert.Equal(1, abandonedStorage.Refcnt.Count);
            }
            finally
            {
                libjq.jv_free(output);
            }

            libjq.jq_reset(state);
            Assert.Equal(1, abandonedStorage.Refcnt.Count);
        }
        finally
        {
            libjq.jq_teardown(ref state);
            libjq.jv_free(input);
        }

        Assert.Equal(0, abandonedStorage.Refcnt.Count);
    }

    private sealed class RecordingSink : IJqValueSink
    {
        internal List<JsonElement> Values { get; } = [];

        public void Write(JsonElement value) => Values.Add(value);
    }
}
