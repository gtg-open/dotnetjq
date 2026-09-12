// DOTNETJQ PORT TEST MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream files: src/execute.c, src/compile.c, src/linker.c
// Primary ownership paths: frame_pop, LOADV/LOADVN, STOREV/STOREVN,
// jq_compile_args bytecode replacement, and jq_reset continuation cleanup.

using DotNetJq.Port;

namespace DotNetJq.Tests;

public sealed class JqScopeRefcountLifetimeCompatibilityTests
{
    [Fact]
    public void RecompilingAStateReleasesThePreviousCompiledArgumentOwner()
    {
        jq_state? state = libjq.jq_init();
        var child = libjq.jv_array([libjq.jv_number(7)]);
        var storage = Assert.IsType<jvp_array>(child.Value);
        var arguments = libjq.jv_object_set(
            libjq.jv_object(),
            libjq.jv_string("provided"),
            libjq.jv_copy(child));

        try
        {
            var transferredArguments = arguments;
            arguments = libjq.jv_invalid();
            Assert.Equal(
                1,
                libjq.jq_compile_args(state, "$provided", transferredArguments));

            // The test handle and the compiled variable cell are the two owners.
            Assert.Equal(2, storage.Refcnt.Count);

            // jq_compile_args() frees the state's previous bytecode, including
            // every constant owner retained by that compiled program.
            Assert.Equal(1, libjq.jq_compile(state, "."));
            Assert.Equal(1, storage.Refcnt.Count);
        }
        finally
        {
            libjq.jv_free(arguments);
            libjq.jq_teardown(ref state);
            libjq.jv_free(child);
        }

        Assert.Equal(0, storage.Refcnt.Count);
    }

    [Fact]
    public void EnvironmentConstantOwnerSurvivesResetAndIsReleasedByReplacementAndTeardown()
    {
        var options = new JqExecutionOptions
        {
            Environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["DOTNETJQ_SCOPE_OWNER"] = "present",
            },
        };
        jq_state? state = libjq.jq_init(options);
        var output = libjq.jv_invalid();
        bytecode compiled = null!;
        jvp_object? firstStorage = null;
        jvp_object? secondStorage = null;

        try
        {
            Assert.Equal(1, libjq.jq_compile(state, "$ENV"));
            compiled = Assert.IsType<bytecode>(state.Bytecode);
            Assert.Single(compiled.environment_constant_indexes);
            libjq.jq_start(state, libjq.jv_null(), 0);

            output = libjq.jq_next(state);
            Assert.True(output.IsValid);
            firstStorage = Assert.IsType<jvp_object>(output.Value);
            Assert.Equal(2, firstStorage.Refcnt.Count);

            libjq.jv_free(output);
            output = libjq.jv_invalid();
            Assert.Equal(1, firstStorage.Refcnt.Count);

            output = libjq.jq_next(state);
            Assert.False(output.IsValid);
            libjq.jv_free(output);
            output = libjq.jv_invalid();
            Assert.Equal(1, firstStorage.Refcnt.Count);

            libjq.jq_reset(state);
            Assert.Same(compiled, state.Bytecode);
            Assert.Equal(1, firstStorage.Refcnt.Count);

            libjq.jq_start(state, libjq.jv_null(), 0);
            Assert.Equal(0, firstStorage.Refcnt.Count);

            output = libjq.jq_next(state);
            Assert.True(output.IsValid);
            secondStorage = Assert.IsType<jvp_object>(output.Value);
            Assert.NotSame(firstStorage, secondStorage);
            Assert.Equal(2, secondStorage.Refcnt.Count);

            libjq.jv_free(output);
            output = libjq.jv_invalid();
            Assert.Equal(1, secondStorage.Refcnt.Count);

            libjq.jq_reset(state);
            Assert.Equal(1, secondStorage.Refcnt.Count);
        }
        finally
        {
            libjq.jv_free(output);
            libjq.jq_teardown(ref state);
        }

        Assert.NotNull(firstStorage);
        Assert.NotNull(secondStorage);
        Assert.Equal(0, firstStorage.Refcnt.Count);
        Assert.Equal(0, secondStorage.Refcnt.Count);
        Assert.False(compiled.constants.IsValid);
    }

    [Fact]
    public void CapturedLocalOwnerSurvivesItsFirstResultAndIsReleasedByEarlyTeardown()
    {
        jq_state? state = libjq.jq_init();
        var input = libjq.jv_array([libjq.jv_number(11)]);
        var retained = libjq.jv_copy(input);
        var storage = Assert.IsType<jvp_array>(retained.Value);
        var output = libjq.jv_invalid();

        try
        {
            Assert.Equal(1, libjq.jq_compile(state, ". as $x | def f: $x; f, f"));
            libjq.jq_start(state, input, 0);
            input = libjq.jv_invalid();

            output = libjq.jq_next(state);
            Assert.True(output.IsValid);
            Assert.Same(storage, output.Value);
            Assert.Equal("[11]", libjq.jv_dump_string_borrowed(output));

            libjq.jv_free(output);
            output = libjq.jv_invalid();

            // Do not ask for the saved comma continuation. jq_teardown() must
            // unwind it and release the closed-over local frame exactly once.
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
    public void FailedDestructuringAlternativeReleasesItsPartialBindings()
    {
        jq_state? state = libjq.jq_init();
        var child = libjq.jv_array([libjq.jv_number(1)]);
        var retainedChild = libjq.jv_copy(child);
        var childStorage = Assert.IsType<jvp_array>(retainedChild.Value);
        var input = libjq.jv_object_set(
            libjq.jv_object(),
            libjq.jv_string("a"),
            child);
        child = libjq.jv_invalid();
        input = libjq.jv_object_set(
            input,
            libjq.jv_string("b"),
            libjq.jv_number(42));

        try
        {
            Assert.Equal(
                1,
                libjq.jq_compile(
                    state,
                    ". as {a:$x, b:[$y]} ?// {a:$x} | empty"));
            libjq.jq_start(state, input, 0);
            input = libjq.jv_invalid();

            Assert.False(libjq.jq_next(state).IsValid);

            // Both the partially matched first alternative and the successful
            // second alternative have backtracked. Only the test owner remains.
            Assert.Equal(1, childStorage.Refcnt.Count);
        }
        finally
        {
            libjq.jv_free(child);
            libjq.jv_free(input);
            libjq.jq_teardown(ref state);
            libjq.jv_free(retainedChild);
        }

        Assert.Equal(0, childStorage.Refcnt.Count);
    }

    [Fact]
    public void EarlyTeardownReleasesUnvisitedValueArgumentScopes()
    {
        jq_state? state = libjq.jq_init();
        var input = libjq.jv_array([libjq.jv_number(23)]);
        var retained = libjq.jv_copy(input);
        var storage = Assert.IsType<jvp_array>(retained.Value);
        var output = libjq.jv_invalid();

        try
        {
            Assert.Equal(1, libjq.jq_compile(state, "def f($x): $x; f((., .))"));
            libjq.jq_start(state, input, 0);
            input = libjq.jv_invalid();

            output = libjq.jq_next(state);
            Assert.True(output.IsValid);
            Assert.Same(storage, output.Value);
            Assert.Equal("[23]", libjq.jv_dump_string_borrowed(output));

            libjq.jv_free(output);
            output = libjq.jv_invalid();

            // BindArguments is suspended on the first native-shaped argument
            // continuation. Abandoning here must release its SUBEXP stack
            // slots, active call frame, and the unvisited argument branch.
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
}
