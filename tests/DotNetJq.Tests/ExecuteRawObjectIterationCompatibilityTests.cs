// DOTNETJQ PORT TEST MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream file: src/execute.c
// Primary upstream code: EACH/EACH_OPT object branch, lines 758-791.

using DotNetJq.Port;
using DotNetJq.Tests.Harness;

namespace DotNetJq.Tests;

public sealed class ExecuteRawObjectIterationCompatibilityTests
{
    [Fact]
    public void EachTransfersTheRawIteratorValueOwnerWithoutDecodingTheKey()
    {
        ReadOnlySpan<byte> keyBytes = [0xF0, 0x90, 0x80, 0x80, 0x00, 0xEE, 0x80, 0x80];
        var key = libjq.jv_string_sized(keyBytes, keyBytes.Length);
        var keyStorage = Assert.IsType<jvp_string>(key.Value);
        var child = libjq.jv_array_append(libjq.jv_array(), libjq.jv_number(7));
        var retainedChild = libjq.jv_copy(child);
        var root = libjq.jv_object_set(
            libjq.jv_object(),
            libjq.jv_copy(key),
            child);
        var iterated = libjq.jv_invalid();
        try
        {
            Assert.Null(keyStorage.CachedText);
            Assert.Equal(2, libjq.jv_get_refcnt(key));
            Assert.Equal(2, libjq.jv_get_refcnt(retainedChild));

            using var state = Compile(".[]");
            libjq.jq_start(state, libjq.jv_copy(root), 0);
            iterated = libjq.jq_next(state);

            Assert.True(iterated.IsValid);
            Assert.Same(retainedChild.Value, iterated.Value);
            // jq_next() transfers EACH's owned stack slot to the caller.
            Assert.Equal(3, libjq.jv_get_refcnt(retainedChild));
            Assert.Null(keyStorage.CachedText);
            Assert.Equal(2, libjq.jv_get_refcnt(key));
            Assert.False(libjq.jq_next(state).IsValid);
            Assert.Equal(3, libjq.jv_get_refcnt(retainedChild));
        }
        finally
        {
            libjq.jv_free(iterated);
            libjq.jv_free(root);
            libjq.jv_free(retainedChild);
            libjq.jv_free(key);
        }
    }

    [Fact]
    public void PathEachTransfersTheExistingRawKeyOwnerAndReleasesItsContainerCopy()
    {
        // jv_string_sized() repairs this one malformed UTF-8 unit to one U+FFFD,
        // exactly like pinned jq. execute.c must then carry that byte-backed key
        // allocation into path tracking without a CLR decode/re-encode cycle.
        ReadOnlySpan<byte> malformedKey = [0x61, 0xED, 0xA0, 0x80, 0x62];
        byte[] repairedKey = [0x61, 0xEF, 0xBF, 0xBD, 0x62];
        var key = libjq.jv_string_sized(malformedKey, malformedKey.Length);
        var keyStorage = Assert.IsType<jvp_string>(key.Value);
        var root = libjq.jv_object_set(
            libjq.jv_object(),
            libjq.jv_copy(key),
            libjq.jv_number(1));
        try
        {
            Assert.Null(keyStorage.CachedText);
            Assert.Equal(1, libjq.jv_get_refcnt(root));
            Assert.Equal(2, libjq.jv_get_refcnt(key));

            using var state = Compile("path(.[])");
            libjq.jq_start(state, libjq.jv_copy(root), 0);
            var path = libjq.jq_next(state);
            var pathKey = Assert.Single(path.ArrayValue);
            Assert.Same(key.Value, pathKey.Value);
            Assert.Equal(repairedKey, libjq.jvp_string_data(pathKey).ToArray());
            Assert.Null(keyStorage.CachedText);
            // PATH_BEGIN and the pending EACH backtrack point each retain the
            // outer input handle while jq_next() transfers the first path.
            Assert.Equal(3, libjq.jv_get_refcnt(root));
            Assert.Equal(3, libjq.jv_get_refcnt(key));
            Assert.False(libjq.jq_next(state).IsValid);
            Assert.Equal(1, libjq.jv_get_refcnt(root));
            libjq.jv_free(path);
        }
        finally
        {
            libjq.jv_free(root);
            libjq.jv_free(key);
        }
    }

    [Fact]
    public void PathForkCopiesThePathArrayHandleWithoutCopyingItsRawKeyChild()
    {
        var key = libjq.jv_string("key");
        var keyStorage = Assert.IsType<jvp_string>(key.Value);
        var root = libjq.jv_object_set(
            libjq.jv_object(),
            libjq.jv_copy(key),
            libjq.jv_number(1));
        var path = libjq.jv_invalid();
        var fork = libjq.jv_invalid();
        try
        {
            Assert.Equal(2, keyStorage.Refcnt.Count);
            using var state = Compile("path(.[])");
            libjq.jq_start(state, libjq.jv_copy(root), 0);
            path = libjq.jq_next(state);
            Assert.Equal(jv_kind.JV_KIND_ARRAY, path.Kind);
            // jq_next() transfers one path owner while the suspended
            // PATH_END continuation retains the other outer-array handle.
            Assert.Equal(2, libjq.jv_get_refcnt(path));
            Assert.Equal(3, keyStorage.Refcnt.Count);

            // execute.c:PATH_END saves jv_copy(path). Only the path-array
            // allocation is retained; its key slot is not copied.
            fork = libjq.jv_copy(path);
            Assert.Equal(3, libjq.jv_get_refcnt(path));
            Assert.Equal(3, keyStorage.Refcnt.Count);
            Assert.False(libjq.jq_next(state).IsValid);
            Assert.Equal(2, libjq.jv_get_refcnt(path));

            libjq.jv_free(fork);
            fork = libjq.jv_invalid();
            Assert.Equal(1, libjq.jv_get_refcnt(path));
            Assert.Equal(3, keyStorage.Refcnt.Count);

            libjq.jv_free(path);
            path = libjq.jv_invalid();
            Assert.Equal(2, keyStorage.Refcnt.Count);
        }
        finally
        {
            libjq.jv_free(fork);
            libjq.jv_free(path);
            libjq.jv_free(root);
            libjq.jv_free(key);
        }

        Assert.Equal(0, keyStorage.Refcnt.Count);
    }

    [Fact]
    public void RecursiveObjectStepUsesTheRawIteratorValueOwner()
    {
        ReadOnlySpan<byte> keyBytes = [0xEE, 0x80, 0x80];
        var key = libjq.jv_string_sized(keyBytes, keyBytes.Length);
        var keyStorage = Assert.IsType<jvp_string>(key.Value);
        var child = libjq.jv_array_append(libjq.jv_array(), libjq.jv_number(9));
        var retainedChild = libjq.jv_copy(child);
        var root = libjq.jv_object_set(
            libjq.jv_object(),
            libjq.jv_copy(key),
            child);
        var iterated = libjq.jv_invalid();
        try
        {
            using var state = Compile("recurse");
            libjq.jq_start(state, libjq.jv_copy(root), 0);
            var first = libjq.jq_next(state);
            Assert.True(first.IsValid);
            Assert.Same(root.Value, first.Value);
            libjq.jv_free(first);
            iterated = libjq.jq_next(state);
            Assert.True(iterated.IsValid);
            Assert.Same(retainedChild.Value, iterated.Value);
            // The returned EACH value and the saved recurse continuation each
            // own a child handle in addition to the object slot and retained
            // test owner.
            Assert.Equal(4, libjq.jv_get_refcnt(retainedChild));
            Assert.Null(keyStorage.CachedText);
        }
        finally
        {
            libjq.jv_free(iterated);
            libjq.jv_free(root);
            libjq.jv_free(retainedChild);
            libjq.jv_free(key);
        }
    }

    [Fact]
    public async Task UnicodeAndEmbeddedNulObjectIterationMatchesPinnedJq182()
    {
        const string filter = "[.[], path(.[]), path(recurse)]";
        const string input = "{\"\\u0000\":1,\"𐀀\":2,\"\":3}";
        var managed = JqProgram.Compile(filter)
            .Execute(input)
            .Select(value => value.GetRawText())
            .ToArray();

        Assert.Equal(
            ["[1,2,3,[\"\\u0000\"],[\"𐀀\"],[\"\"],[],[\"\\u0000\"],[\"𐀀\"],[\"\"]]"],
            managed);

        if (!JqOracle.TryResolveExecutable(out _))
        {
            return;
        }

        var oracle = await JqOracle.ExecuteAsync(
            filter,
            input,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(oracle.Succeeded, oracle.StandardError);
        Assert.Equal(oracle.OutputLines, managed);
    }

    private static jq_state Compile(string filter)
    {
        var state = libjq.jq_init();
        if (libjq.jq_compile(state, filter) == 1)
        {
            return state;
        }

        var error = state.CompileError?.Message ?? "unknown compile error";
        jq_state? owner = state;
        libjq.jq_teardown(ref owner);
        throw new Xunit.Sdk.XunitException(error);
    }
}
