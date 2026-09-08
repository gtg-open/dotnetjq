// DOTNETJQ PORT TEST MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream files: src/compile.c, src/execute.c, src/jv_aux.c, src/jv.c
//
// These tests exercise the raw jv string owners passed through INSERT, object-pattern
// INDEX, and path deletion. A deliberately non-scalar UTF-8 key makes any accidental
// StringValue decode/re-encode observable; ReferenceEquals models native u.ptr identity.

using DotNetJq.Port;

namespace DotNetJq.Tests;

public sealed class RawObjectKeyOwnershipCompatibilityTests
{
    [Fact]
    public void ObjectConstructionCopiesTheRawKeyOwnerIntoItsSlot()
    {
        var key = NonScalarKey();
        var result = libjq.jv_invalid();
        var iteratedKey = libjq.jv_invalid();
        try
        {
            using var state = Compile("{(.): 1}");
            libjq.jq_start(state, libjq.jv_copy(key), 0);
            result = libjq.jq_next(state);
            Assert.True(result.IsValid);
            Assert.False(libjq.jq_next(state).IsValid);

            Assert.Equal(2, libjq.jv_get_refcnt(key));
            var iterator = libjq.jv_object_iter(result);
            Assert.True(libjq.jv_object_iter_valid(result, iterator));
            iteratedKey = libjq.jv_object_iter_key(result, iterator);
            Assert.Same(key.Value, iteratedKey.Value);
            Assert.Equal(3, libjq.jv_get_refcnt(key));

            libjq.jv_free(iteratedKey);
            iteratedKey = libjq.jv_invalid();
            libjq.jv_free(result);
            result = libjq.jv_invalid();
            Assert.Equal(1, libjq.jv_get_refcnt(key));
        }
        finally
        {
            libjq.jv_free(iteratedKey);
            libjq.jv_free(result);
            libjq.jv_free(key);
        }
    }

    [Fact]
    public void ObjectPatternIndexesWithATemporaryRawKeyOwner()
    {
        var key = NonScalarKey();
        var value = libjq.jv_object_set(
            libjq.jv_object(),
            libjq.jv_copy(key),
            libjq.jv_number(42));
        try
        {
            var arguments = libjq.jv_object_set(
                libjq.jv_object(),
                "key",
                libjq.jv_copy(key));
            using var state = Compile(". as {($key): $found} | $found", arguments);
            Assert.Equal(3, libjq.jv_get_refcnt(key));
            libjq.jq_start(state, libjq.jv_copy(value), 0);
            var found = libjq.jq_next(state);
            try
            {
                Assert.Equal(jv_kind.JV_KIND_NUMBER, found.Kind);
                Assert.Equal(42, found.NumberValue);
                Assert.False(libjq.jq_next(state).IsValid);
                Assert.Equal(3, libjq.jv_get_refcnt(key));
            }
            finally
            {
                libjq.jv_free(found);
            }
        }
        finally
        {
            libjq.jv_free(value);
            libjq.jv_free(key);
        }
    }

    [Fact]
    public void NestedDeletePathUsesRawHasGetAndSetKeys()
    {
        var key = NonScalarKey();
        var leaf = libjq.jv_string("leaf");
        var value = libjq.jv_object_set(
            libjq.jv_object(),
            libjq.jv_copy(key),
            libjq.jv_object_set(
                libjq.jv_object(),
                libjq.jv_copy(leaf),
                libjq.jv_number(1)));
        var selected = libjq.jv_invalid();
        var iteratedKey = libjq.jv_invalid();
        try
        {
            value = libjq.jv_delpath(value, [key, leaf]);

            selected = libjq.jv_object_get(libjq.jv_copy(value), libjq.jv_copy(key));
            Assert.Equal(0, libjq.jv_object_length(libjq.jv_copy(selected)));
            var iterator = libjq.jv_object_iter(value);
            iteratedKey = libjq.jv_object_iter_key(value, iterator);
            Assert.Same(key.Value, iteratedKey.Value);
            Assert.Equal(3, libjq.jv_get_refcnt(key));
        }
        finally
        {
            libjq.jv_free(iteratedKey);
            libjq.jv_free(selected);
            libjq.jv_free(value);
            libjq.jv_free(leaf);
            libjq.jv_free(key);
        }
    }

    [Fact]
    public void LeafDeletePathConsumesItsRawKeyCopyAndReleasesTheSlotOwner()
    {
        var key = NonScalarKey();
        var value = libjq.jv_object_set(
            libjq.jv_object(),
            libjq.jv_copy(key),
            libjq.jv_number(1));
        try
        {
            Assert.Equal(2, libjq.jv_get_refcnt(key));

            value = libjq.jv_delpath(value, [key]);

            Assert.Equal(0, libjq.jv_object_length(libjq.jv_copy(value)));
            Assert.Equal(1, libjq.jv_get_refcnt(key));
        }
        finally
        {
            libjq.jv_free(value);
            libjq.jv_free(key);
        }
    }

    private static jv NonScalarKey() =>
        libjq.jv_string_append_codepoint(libjq.jv_string_empty(3), 0xD800);

    private static jq_state Compile(string filter, jv? arguments = null)
    {
        var state = libjq.jq_init();
        var success = arguments is { } provided
            ? libjq.jq_compile_args(state, filter, provided)
            : libjq.jq_compile(state, filter);
        if (success == 1)
        {
            return state;
        }

        var error = state.CompileError?.Message ?? "unknown compile error";
        jq_state? owner = state;
        libjq.jq_teardown(ref owner);
        throw new Xunit.Sdk.XunitException(error);
    }
}
