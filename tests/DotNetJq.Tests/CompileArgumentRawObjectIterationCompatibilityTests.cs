// DOTNETJQ PORT TEST MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream files: src/execute.c (jq_compile_args/args2obj) and
// src/compile.c (expand_call_arglist).

using DotNetJq.Port;

namespace DotNetJq.Tests;

public sealed class CompileArgumentRawObjectIterationCompatibilityTests
{
    [Fact]
    public void ObjectArgumentsAcquireChildOwnersAndConsumeTheirInputContainer()
    {
        var key = libjq.jv_string("provided");
        var child = libjq.jv_array_append(libjq.jv_array(), libjq.jv_number(7));
        var arguments = libjq.jv_object_set(
            libjq.jv_object(),
            libjq.jv_copy(key),
            libjq.jv_copy(child));
        var retainedArguments = libjq.jv_copy(arguments);
        var result = libjq.jv_invalid();
        jq_state? state = libjq.jq_init();
        try
        {
            Assert.Equal(2, libjq.jv_get_refcnt(key));
            Assert.Equal(2, libjq.jv_get_refcnt(child));
            Assert.Equal(2, libjq.jv_get_refcnt(retainedArguments));

            Assert.Equal(1, libjq.jq_compile_args(state, "$provided", arguments));

            // jq_compile_args() consumed its owner of the object. The retained
            // object still owns its slots, and the compiled LOADK constant pool
            // separately owns the value selected by expand_call_arglist(). The
            // temporary iterator key owner has already been released.
            Assert.Equal(1, libjq.jv_get_refcnt(retainedArguments));
            Assert.Equal(2, libjq.jv_get_refcnt(key));
            Assert.Equal(3, libjq.jv_get_refcnt(child));

            libjq.jv_free(retainedArguments);
            retainedArguments = libjq.jv_invalid();
            Assert.Equal(1, libjq.jv_get_refcnt(key));
            Assert.Equal(2, libjq.jv_get_refcnt(child));

            libjq.jq_start(state, libjq.jv_null(), 0);
            result = libjq.jq_next(state);
            Assert.Equal("[7]", libjq.jv_dump_string_borrowed(result));
            Assert.False(libjq.jv_is_valid(libjq.jq_next(state)));
        }
        finally
        {
            libjq.jv_free(result);
            libjq.jv_free(retainedArguments);
            libjq.jq_teardown(ref state);
            libjq.jv_free(child);
            libjq.jv_free(key);
        }
    }

    [Fact]
    public void ParseFailureStillConsumesTheArgumentObjectLikeJqCompileArgs()
    {
        var key = libjq.jv_string("provided");
        var child = libjq.jv_array_append(libjq.jv_array(), libjq.jv_number(7));
        var arguments = libjq.jv_object_set(
            libjq.jv_object(),
            libjq.jv_copy(key),
            libjq.jv_copy(child));
        var retainedArguments = libjq.jv_copy(arguments);
        jq_state? state = libjq.jq_init();
        try
        {
            Assert.Equal(0, libjq.jq_compile_args(state, "{", arguments));
            Assert.Null(state.Bytecode);
            Assert.NotNull(state.CompileError);

            Assert.Equal(1, libjq.jv_get_refcnt(retainedArguments));
            Assert.Equal(2, libjq.jv_get_refcnt(key));
            Assert.Equal(2, libjq.jv_get_refcnt(child));

            libjq.jv_free(retainedArguments);
            retainedArguments = libjq.jv_invalid();
            Assert.Equal(1, libjq.jv_get_refcnt(key));
            Assert.Equal(1, libjq.jv_get_refcnt(child));
        }
        finally
        {
            libjq.jv_free(retainedArguments);
            libjq.jq_teardown(ref state);
            libjq.jv_free(child);
            libjq.jv_free(key);
        }
    }

    [Fact]
    public void ReferenceFailureReleasesValuesCopiedIntoTheRejectedScope()
    {
        var key = libjq.jv_string("provided");
        var child = libjq.jv_array_append(libjq.jv_array(), libjq.jv_number(7));
        var arguments = libjq.jv_object_set(
            libjq.jv_object(),
            libjq.jv_copy(key),
            libjq.jv_copy(child));
        jq_state? state = libjq.jq_init();
        try
        {
            Assert.Equal(0, libjq.jq_compile_args(state, "$missing", arguments));
            Assert.Null(state.Bytecode);
            Assert.NotNull(state.CompileError);

            // The object slot owner, iterator key owner, and rejected compiled
            // value owner have all been released. Only the test's owners remain.
            Assert.Equal(1, libjq.jv_get_refcnt(key));
            Assert.Equal(1, libjq.jv_get_refcnt(child));
        }
        finally
        {
            libjq.jq_teardown(ref state);
            libjq.jv_free(child);
            libjq.jv_free(key);
        }
    }

    [Fact]
    public void EmbeddedNulAndRepairedUtf8RemainDistinctFromTheCompilerSymbol()
    {
        var normalKey = libjq.jv_string("provided");
        byte[] unusualKeyBytes =
        [
            (byte)'p', (byte)'r', (byte)'o', (byte)'v', (byte)'i',
            (byte)'d', (byte)'e', (byte)'d', 0x00, 0xED, 0xA0, 0x80,
        ];
        var unusualKey = libjq.jv_string_sized(unusualKeyBytes, unusualKeyBytes.Length);
        var arguments = libjq.jv_object_set(
            libjq.jv_object(),
            libjq.jv_copy(normalKey),
            libjq.jv_number(17));
        arguments = libjq.jv_object_set(
            arguments,
            libjq.jv_copy(unusualKey),
            libjq.jv_number(99));
        var result = libjq.jv_invalid();
        jq_state? state = libjq.jq_init();
        try
        {
            Assert.Equal(1, libjq.jq_compile_args(state, "$provided", arguments));
            Assert.Equal(1, libjq.jv_get_refcnt(normalKey));
            Assert.Equal(1, libjq.jv_get_refcnt(unusualKey));

            var bytecode = Assert.IsType<bytecode>(state.Bytecode);
            Assert.Contains(
                bytecode.constants.ArrayValue,
                value => value.Kind == jv_kind.JV_KIND_NUMBER && value.NumberValue == 17);
            Assert.DoesNotContain(
                bytecode.constants.ArrayValue,
                value => value.Kind == jv_kind.JV_KIND_NUMBER && value.NumberValue == 99);

            libjq.jq_start(state, libjq.jv_null(), 0);
            result = libjq.jq_next(state);
            Assert.Equal("17", libjq.jv_dump_string_borrowed(result));
            Assert.False(libjq.jv_is_valid(libjq.jq_next(state)));
        }
        finally
        {
            libjq.jv_free(result);
            libjq.jq_teardown(ref state);
            libjq.jv_free(unusualKey);
            libjq.jv_free(normalKey);
        }
    }

}
