// DOTNETJQ PORT TEST MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream files: src/compile.c, src/execute.c, src/bytecode.c
// Primary ownership paths: LOADK, compile(), bytecode_free(), jq_compile_args().

using DotNetJq.Port;

namespace DotNetJq.Tests;

public sealed class CompiledLiteralRefcountLifecycleTests
{
    [Fact]
    public void LoadkCopiesTheBytecodeConstantAndTeardownReleasesItsOwner()
    {
        jq_state? state = libjq.jq_init();
        var output = libjq.jv_invalid();
        var retained = libjq.jv_invalid();
        jvp_string? storage = null;

        try
        {
            Assert.Equal(1, libjq.jq_compile(state, "\"owned\""));
            var bytecode = Assert.IsType<bytecode>(state.Bytecode);
            var constant = Assert.Single(
                EnumerateConstants(bytecode),
                value => value.Kind == jv_kind.JV_KIND_STRING && value.StringValue == "owned");
            storage = Assert.IsType<jvp_string>(constant.Value);
            retained = libjq.jv_copy(constant);

            Assert.Equal(2, storage.Refcnt.Count);

            libjq.jq_start(state, libjq.jv_null(), 0);
            output = libjq.jq_next(state);

            Assert.Same(storage, output.Value);
            Assert.Equal(3, storage.Refcnt.Count);

            libjq.jv_free(output);
            output = libjq.jv_invalid();
            Assert.Equal(2, storage.Refcnt.Count);
            Assert.False(libjq.jq_next(state).IsValid);

            libjq.jq_teardown(ref state);
            Assert.Equal(1, storage.Refcnt.Count);
        }
        finally
        {
            libjq.jv_free(output);
            libjq.jq_teardown(ref state);
            libjq.jv_free(retained);
        }

        Assert.NotNull(storage);
        Assert.Equal(0, storage.Refcnt.Count);
    }

    [Fact]
    public void RecompilingReleasesThePreviousBytecodeConstantOwner()
    {
        jq_state? state = libjq.jq_init();
        var retained = libjq.jv_invalid();
        jvp_string? storage = null;

        try
        {
            Assert.Equal(1, libjq.jq_compile(state, "\"first program\""));
            var firstBytecode = Assert.IsType<bytecode>(state.Bytecode);
            var constant = Assert.Single(
                EnumerateConstants(firstBytecode),
                value => value.Kind == jv_kind.JV_KIND_STRING &&
                         value.StringValue == "first program");
            storage = Assert.IsType<jvp_string>(constant.Value);
            retained = libjq.jv_copy(constant);
            Assert.Equal(2, storage.Refcnt.Count);

            Assert.Equal(1, libjq.jq_compile(state, "."));
            Assert.Equal(1, storage.Refcnt.Count);
        }
        finally
        {
            libjq.jq_teardown(ref state);
            libjq.jv_free(retained);
        }

        Assert.NotNull(storage);
        Assert.Equal(0, storage.Refcnt.Count);
    }

    [Fact]
    public void SeparateCompilationsOwnDistinctEmbeddedBuiltinConstants()
    {
        const string message = "flatten depth must not be negative";
        jq_state? firstState = libjq.jq_init();
        jq_state? secondState = libjq.jq_init();
        var firstRetained = libjq.jv_invalid();
        var secondRetained = libjq.jv_invalid();
        jvp_string? firstStorage = null;
        jvp_string? secondStorage = null;

        try
        {
            Assert.Equal(1, libjq.jq_compile(firstState, "flatten(-1)"));
            Assert.Equal(1, libjq.jq_compile(secondState, "flatten(-1)"));
            var firstValue = Assert.Single(
                EnumerateConstants(Assert.IsType<bytecode>(firstState.Bytecode)),
                value => value.Kind == jv_kind.JV_KIND_STRING && value.StringValue == message);
            var secondValue = Assert.Single(
                EnumerateConstants(Assert.IsType<bytecode>(secondState.Bytecode)),
                value => value.Kind == jv_kind.JV_KIND_STRING && value.StringValue == message);

            firstStorage = Assert.IsType<jvp_string>(firstValue.Value);
            secondStorage = Assert.IsType<jvp_string>(secondValue.Value);
            Assert.NotSame(firstStorage, secondStorage);

            firstRetained = libjq.jv_copy(firstValue);
            secondRetained = libjq.jv_copy(secondValue);
            var firstCount = firstStorage.Refcnt.Count;
            var secondCount = secondStorage.Refcnt.Count;

            libjq.jq_teardown(ref firstState);
            Assert.Equal(firstCount - 1, firstStorage.Refcnt.Count);
            Assert.Equal(secondCount, secondStorage.Refcnt.Count);

            libjq.jq_teardown(ref secondState);
            Assert.Equal(secondCount - 1, secondStorage.Refcnt.Count);
        }
        finally
        {
            libjq.jq_teardown(ref firstState);
            libjq.jq_teardown(ref secondState);
            libjq.jv_free(firstRetained);
            libjq.jv_free(secondRetained);
        }

        Assert.NotNull(firstStorage);
        Assert.NotNull(secondStorage);
        Assert.Equal(0, firstStorage.Refcnt.Count);
        Assert.Equal(0, secondStorage.Refcnt.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LexicallyOverriddenModifyKeepsOnlyItsBytecodeConstantOwner(bool teardownEarly)
    {
        const string filter = "def _modify(p; f): f; . += \"right\"";
        jq_state? state = libjq.jq_init();
        var output = libjq.jv_invalid();
        var retained = libjq.jv_invalid();
        jvp_string? storage = null;

        try
        {
            Assert.Equal(1, libjq.jq_compile(state, filter));
            var bytecode = Assert.IsType<bytecode>(state.Bytecode);
            var constant = Assert.Single(
                EnumerateConstants(bytecode),
                value => value.Kind == jv_kind.JV_KIND_STRING && value.StringValue == "right");
            storage = Assert.IsType<jvp_string>(constant.Value);
            retained = libjq.jv_copy(constant);
            Assert.Equal(2, storage.Refcnt.Count);

            libjq.jq_start(state, libjq.jv_string("left"), 0);
            output = libjq.jq_next(state);
            Assert.Equal("\"leftright\"", libjq.jv_dump_string_borrowed(output));
            libjq.jv_free(output);
            output = libjq.jv_invalid();

            // The first result suspends the generated update at a saved VM
            // continuation. Besides the constant-pool and retained owners,
            // that shared stack branch still owns one LOADK handle.
            Assert.Equal(3, storage.Refcnt.Count);
            if (teardownEarly)
            {
                libjq.jq_teardown(ref state);
                Assert.Equal(1, storage.Refcnt.Count);
            }
            else
            {
                Assert.False(libjq.jq_next(state).IsValid);
                Assert.Equal(2, storage.Refcnt.Count);
            }
        }
        finally
        {
            libjq.jv_free(output);
            libjq.jq_teardown(ref state);
            libjq.jv_free(retained);
        }

        Assert.NotNull(storage);
        Assert.Equal(0, storage.Refcnt.Count);
    }

    [Theory]
    [InlineData(". as {\"key\": $x} | $x", "key")]
    [InlineData("reduce .[] as {\"key\": $x} (0; . + $x)", "key")]
    [InlineData("foreach .[] as {\"key\": $x} (0; . + $x)", "key")]
    [InlineData("\"prefix\\(.)suffix\"", "prefix")]
    [InlineData("@json", "json")]
    public void GeneratedExpressionsParticipateInTheBytecodeConstantGraph(
        string filter,
        string expectedLiteral)
    {
        jq_state? state = libjq.jq_init();
        var retained = libjq.jv_invalid();
        jvp_string? storage = null;

        try
        {
            Assert.Equal(1, libjq.jq_compile(state, filter));
            var bytecode = Assert.IsType<bytecode>(state.Bytecode);
            var constant = Assert.Single(
                EnumerateConstants(bytecode),
                value => value.Kind == jv_kind.JV_KIND_STRING &&
                         value.StringValue == expectedLiteral);
            storage = Assert.IsType<jvp_string>(constant.Value);
            retained = libjq.jv_copy(constant);
            Assert.Equal(2, storage.Refcnt.Count);

            libjq.jq_teardown(ref state);
            Assert.Equal(1, storage.Refcnt.Count);
        }
        finally
        {
            libjq.jq_teardown(ref state);
            libjq.jv_free(retained);
        }

        Assert.NotNull(storage);
        Assert.Equal(0, storage.Refcnt.Count);
    }

    private static IEnumerable<jv> EnumerateConstants(bytecode root)
    {
        var pending = new Stack<bytecode>();
        pending.Push(root);
        while (pending.TryPop(out var current))
        {
            foreach (var constant in current.constants.ArrayValue)
            {
                yield return constant;
            }

            foreach (var child in current.subfunctions)
            {
                pending.Push(child);
            }
        }
    }
}
