using DotNetJq.Port;

namespace DotNetJq.Tests;

public sealed class DeepMergeCompatibilityRound5Tests
{
    private const int JqMaximumObjectMergeDepth = 10_000;

    [Fact]
    public void RecursiveObjectMergePreservesKeysAndInsertionOrder()
    {
        var output = Assert.Single(
            JqProgram.Compile(".left * .right").Execute(
                "{\"left\":{\"a\":{\"x\":1,\"y\":2},\"z\":0}," +
                "\"right\":{\"a\":{\"x\":9,\"n\":3},\"z\":{\"q\":1},\"new\":4}}"));

        Assert.Equal(
            "{\"a\":{\"x\":9,\"y\":2,\"n\":3},\"z\":{\"q\":1},\"new\":4}",
            output.GetRawText());
    }

    [Fact]
    public void RecursiveObjectMergeAcceptsJqMaximumDepthWithoutClrRecursion()
    {
        var value = NestedObjects(JqMaximumObjectMergeDepth);

        var merged = ExecuteMerge(value);
        try
        {
            Assert.Equal(jv_kind.JV_KIND_OBJECT, merged.Kind);
            Assert.Single(merged.ObjectValue);
        }
        finally
        {
            libjq.jv_free(merged);
        }
    }

    [Fact]
    public void RecursiveObjectMergeRejectsTheNextDepthWithCatchableJqError()
    {
        var value = NestedObjects(JqMaximumObjectMergeDepth + 1);

        var error = ExecuteMerge(value);
        Assert.False(error.IsValid);
        Assert.True(libjq.jv_invalid_has_msg(libjq.jv_copy(error)));
        var message = libjq.jv_invalid_get_msg(error);
        try
        {
            Assert.Equal("Object merge too deep", message.StringValue);
        }
        finally
        {
            libjq.jv_free(message);
        }
    }

    [Theory]
    [InlineData(
        "reduce range(10000) as $_ ({}; {a: .}) as $x | $x * $x | length",
        "1")]
    [InlineData(
        "try (reduce range(10001) as $_ ({}; {a: .}) as $x | $x * $x) catch .",
        "\"Object merge too deep\"")]
    public void OfficialDeepMergeBoundaryMatchesJq(string filter, string expected)
    {
        var output = Assert.Single(JqProgram.Compile(filter).Execute("null"));

        Assert.Equal(expected, output.GetRawText());
    }

    private static jv NestedObjects(int depth)
    {
        var value = libjq.jv_object();
        for (var index = 0; index < depth; index++)
        {
            value = libjq.jv_object([new KeyValuePair<string, jv>("a", value)]);
        }

        return value;
    }

    private static jv ExecuteMerge(jv input)
    {
        jq_state? state = libjq.jq_init();
        try
        {
            Assert.Equal(1, libjq.jq_compile(state, ". * ."));
            libjq.jq_start(state, input, 0);
            return libjq.jq_next(state);
        }
        finally
        {
            libjq.jq_teardown(ref state);
        }
    }
}
