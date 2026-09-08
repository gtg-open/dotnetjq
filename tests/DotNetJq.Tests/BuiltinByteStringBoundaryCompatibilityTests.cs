// DOTNETJQ PORT TEST MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream files: src/builtin.c (f_json_parse, f_tonumber, f_format,
// f_strptime, f_strftime), src/jv_aux.c (jv_keys/string_cmp).

using DotNetJq.Port;
using DotNetJq.Tests.Harness;

namespace DotNetJq.Tests;

public sealed class BuiltinByteStringBoundaryCompatibilityTests
{
    [Fact]
    public void KeysUseRawUtf8ByteOrderRatherThanClrUtf16Order()
    {
        // UTF-16 ordinal sorts U+10000 before U+E000 because the former starts
        // with a high surrogate.  jq's length+memcmp comparator sorts the
        // valid UTF-8 encodings in scalar order, so U+E000 comes first.
        Assert.Equal(
            "[\"\",\"𐀀\"]",
            ExecuteOne("keys", "{\"\uE000\":1,\"\uD800\uDC00\":2}"));
    }

    [Fact]
    public void ToNumberRejectsEveryNulInTheBytePayload()
    {
        Assert.Equal(
            "[\"string (\\\"12\\\\u00003\\\") cannot be parsed as a number\"," +
            "\"string (\\\"1\\\\u0000\\\") cannot be parsed as a number\"]",
            ExecuteOne(
                "map(try tonumber catch .)",
                "[\"12\\u00003\",\"1\\u0000\"]"));
    }

    [Fact]
    public void FromJsonParsesTheCompleteUtf8PayloadAndReportsByteColumns()
    {
        Assert.Equal(
            "[\"Invalid literal at EOF at line 1, column 5 (while parsing 'null')\"," +
            "\"Invalid numeric literal at EOF at line 1, column 8 (while parsing '[\\\"é\\\"]')\"," +
            "\"Invalid literal at EOF at line 1, column 6 (while parsing 'nullé')\"]",
            ExecuteOne(
                "map(try fromjson catch .)",
                "[\"null\\u0000\",\"[\\\"é\\\"]\\u0000x\",\"nullé\"]"));
    }

    [Fact]
    public void TimeAndFormatLibcBoundariesStopAtTheFirstNul()
    {
        Assert.Equal(
            "[[2024,0,2,0,0,0,2,1],\"2024\",\"1\"," +
            "\"nope\\u0000json is not a valid format\"]",
            ExecuteOne(
                "[" +
                "(\"2024-01-02\\u0000junk\"|strptime(\"%Y-%m-%d\\u0000ignored\"))," +
                "([2024,0,2,0,0,0,2,1]|strftime(\"%Y\\u0000junk\"))," +
                "(1|format(\"json\\u0000garbage\"))," +
                "(try (1|format(\"nope\\u0000json\")) catch .)" +
                "]",
                "null"));
    }

    [Fact]
    public async Task DiscriminatingBoundaryCorpusMatchesPinnedJq182Oracle()
    {
        const string filter =
            "[{\"\\ue000\":1,\"\\ud800\\udc00\":2}|keys," +
            "(\"12\\u00003\"|try tonumber catch .)," +
            "(\"[\\\"é\\\"]\\u0000x\"|try fromjson catch .)," +
            "(\"2024-01-02\\u0000junk\"|strptime(\"%Y-%m-%d\"))," +
            "([2024,0,2,0,0,0,2,1]|strftime(\"%Y\\u0000junk\"))," +
            "(1|format(\"json\\u0000garbage\"))]";

        var oracle = await JqOracle.ExecuteAsync(
            filter,
            "null",
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(oracle.Succeeded, oracle.StandardError);
        Assert.Equal(oracle.OutputLines, Execute(filter, "null"));
    }

    [Fact]
    public void ToEntriesCopiesTheOriginalByteBackedObjectKeyAllocation()
    {
        var key = libjq.jv_string("é\0key");
        var keyStorage = (jvp_string)key.Value!;
        var input = libjq.jv_object_set(
            libjq.jv_object(),
            libjq.jv_copy(key),
            libjq.jv_number(7));
        jv entries = default;
        jv entry = default;
        jv entryKey = default;
        try
        {
            using var state = Compile("to_entries");
            libjq.jq_start(state, libjq.jv_copy(input), 0);
            entries = libjq.jq_next(state);
            Assert.True(entries.IsValid);
            Assert.False(libjq.jq_next(state).IsValid);
            entry = libjq.jv_array_get(libjq.jv_copy(entries), 0);
            entryKey = libjq.jv_object_get(
                libjq.jv_copy(entry),
                libjq.jv_string("key"));

            Assert.Same(keyStorage, entryKey.Value);
            Assert.Equal(4, libjq.jv_get_refcnt(key));
        }
        finally
        {
            libjq.jv_free(entryKey);
            libjq.jv_free(entry);
            libjq.jv_free(entries);
            libjq.jv_free(input);
            libjq.jv_free(key);
        }

        Assert.Equal(0, keyStorage.Refcnt.Count);
        Assert.Empty(keyStorage.Data);
    }

    [Theory]
    [InlineData("map_values")]
    [InlineData("walk")]
    public void ObjectTransformsRetainTheOriginalByteBackedKeyAllocation(string operation)
    {
        var key = libjq.jv_string("é\0key");
        var keyStorage = (jvp_string)key.Value!;
        var input = libjq.jv_object_set(
            libjq.jv_object(),
            libjq.jv_copy(key),
            libjq.jv_number(7));
        jv output = default;
        jv outputKey = default;
        try
        {
            using var state = Compile($"{operation}(.)");
            libjq.jq_start(state, libjq.jv_copy(input), 0);
            output = libjq.jq_next(state);
            Assert.True(output.IsValid);
            Assert.False(libjq.jq_next(state).IsValid);
            var iterator = libjq.jv_object_iter(output);
            Assert.True(libjq.jv_object_iter_valid(output, iterator));
            outputKey = libjq.jv_object_iter_key(output, iterator);

            Assert.Same(keyStorage, outputKey.Value);
            Assert.Equal(4, libjq.jv_get_refcnt(key));
        }
        finally
        {
            libjq.jv_free(outputKey);
            libjq.jv_free(output);
            libjq.jv_free(input);
            libjq.jv_free(key);
        }

        Assert.Equal(0, keyStorage.Refcnt.Count);
        Assert.Empty(keyStorage.Data);
    }

    private static string ExecuteOne(string filter, string input) =>
        Assert.Single(Execute(filter, input));

    private static string[] Execute(string filter, string input) =>
        JqProgram.Compile(filter)
            .Execute(input)
            .Select(static value => value.GetRawText())
            .ToArray();

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
