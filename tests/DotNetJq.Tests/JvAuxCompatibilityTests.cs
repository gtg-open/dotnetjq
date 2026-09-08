using DotNetJq.Port;
using DotNetJq.Tests.Harness;
using System.Text.Json;

namespace DotNetJq.Tests;

public sealed class JvAuxCompatibilityTests
{
    [Fact]
    public void DelpathsHandlesTenThousandComponentPathWithoutUsingTheClrStack()
    {
        const int depth = 10_000;
        var key = libjq.jv_string("a");
        var root = libjq.jv_number(1);
        var path = libjq.jv_array_sized(depth);
        var paths = libjq.jv_invalid();
        var result = libjq.jv_invalid();
        var current = libjq.jv_invalid();
        try
        {
            for (var index = 0; index < depth; index++)
            {
                root = libjq.jv_object_set(
                    libjq.jv_object(),
                    libjq.jv_copy(key),
                    root);
                path = libjq.jv_array_append(path, libjq.jv_copy(key));
            }

            paths = libjq.jv_array([path]);
            path = libjq.jv_invalid();
            result = libjq.jv_delpaths(root, paths);
            root = libjq.jv_invalid();
            paths = libjq.jv_invalid();

            Assert.True(result.IsValid);
            current = result;
            result = libjq.jv_invalid();
            for (var index = 0; index < depth - 1; index++)
            {
                Assert.Equal(jv_kind.JV_KIND_OBJECT, current.Kind);
                current = libjq.jv_object_get(current, libjq.jv_copy(key));
            }

            Assert.Equal(jv_kind.JV_KIND_OBJECT, current.Kind);
            Assert.Equal(0, libjq.jv_object_length(libjq.jv_copy(current)));
        }
        finally
        {
            libjq.jv_free(current);
            libjq.jv_free(result);
            libjq.jv_free(paths);
            libjq.jv_free(path);
            libjq.jv_free(root);
            libjq.jv_free(key);
        }
    }

    [Fact]
    public void DelpathsRejectsTenThousandAndOneComponentsAndConsumesItsInputs()
    {
        const int depth = 10_001;
        var key = libjq.jv_string("a");
        var root = libjq.jv_object();
        var rootStorage = Assert.IsType<jvp_object>(root.Value);
        var path = libjq.jv_array_sized(depth);
        var paths = libjq.jv_invalid();
        var result = libjq.jv_invalid();
        var message = libjq.jv_invalid();
        try
        {
            for (var index = 0; index < depth; index++)
            {
                path = libjq.jv_array_append(path, libjq.jv_copy(key));
            }

            paths = libjq.jv_array([path]);
            path = libjq.jv_invalid();
            result = libjq.jv_delpaths(root, paths);
            root = libjq.jv_invalid();
            paths = libjq.jv_invalid();

            Assert.False(result.IsValid);
            message = libjq.jv_invalid_get_msg(result);
            result = libjq.jv_invalid();
            Assert.Equal("Path too deep", message.StringValue);
            Assert.Equal(0, rootStorage.Refcnt.Count);
            Assert.Equal(1, libjq.jv_get_refcnt(key));
        }
        finally
        {
            libjq.jv_free(message);
            libjq.jv_free(result);
            libjq.jv_free(paths);
            libjq.jv_free(path);
            libjq.jv_free(root);
            libjq.jv_free(key);
        }
    }

    [Fact]
    public async Task DelpathsDepthBoundaryMatchesPinnedNativeJq()
    {
        if (!JqOracle.TryResolveExecutable(out _))
        {
            return;
        }

        var accepted = await JqOracle.ExecuteAsync(
            "reduce range(10000) as $i (1; {a:.}) | " +
            "delpaths([[range(10000)|\"a\"]]) | type",
            "null",
            ["--null-input"],
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(accepted.Succeeded, accepted.StandardError);
        Assert.Equal(["\"object\""], accepted.OutputLines);

        var rejected = await JqOracle.ExecuteAsync(
            "try (reduce range(10001) as $i (1; {a:.}) | " +
            "delpaths([[range(10001)|\"a\"]])) catch .",
            "null",
            ["--null-input"],
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(rejected.Succeeded, rejected.StandardError);
        Assert.Equal(["\"Path too deep\""], rejected.OutputLines);
    }

    [Theory]
    [InlineData(
        "delpaths([[1],[-6],[2],[{\"start\":-3,\"end\":9}]])",
        "[0,1,2,3,4,5,6,7,8,9]")]
    [InlineData(
        "delpaths([[{\"start\":1,\"end\":3},0]])",
        "[[0],[1],[2],[3],[4]]")]
    [InlineData(
        "delpaths([[\"a\"],[\"a\",\"b\"],[\"z\"]])",
        "{\"a\":{\"b\":1,\"c\":2},\"z\":3}")]
    [InlineData("try delpaths([[0]]) catch .", "{}")]
    public async Task DelpathsFocusedCasesMatchPinnedJq(string filter, string input)
    {
        if (!JqOracle.TryResolveExecutable(out _))
        {
            return;
        }

        var expected = await JqOracle.ExecuteAsync(
            filter,
            input,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(expected.Succeeded, expected.StandardError);

        var actual = JqProgram.Compile(filter).Execute(input);
        Assert.Equal(expected.OutputLines.Count, actual.Count);
        for (var index = 0; index < actual.Count; index++)
        {
            using var expectedDocument = JsonDocument.Parse(expected.OutputLines[index]);
            Assert.True(
                JsonElement.DeepEquals(expectedDocument.RootElement, actual[index]),
                $"output[{index}] expected {expected.OutputLines[index]}, " +
                $"actual {actual[index].GetRawText()}");
        }
    }

    [Fact]
    public void ContainsHandlesTenThousandNestedArraysWithoutUsingTheClrStack()
    {
        var container = libjq.jv_number(1);
        var contained = libjq.jv_number(1);
        for (var depth = 0; depth < 10_000; depth++)
        {
            container = libjq.jv_array([container]);
            contained = libjq.jv_array([contained]);
        }

        Assert.True(libjq.jv_contains(container, contained));
    }

    [Fact]
    public void ContainsPreservesArraySubsetMatchingSemantics()
    {
        var container = libjq.jv_array(
        [
            libjq.jv_object(
            [
                new("a", libjq.jv_number(1)),
                new("b", libjq.jv_number(2)),
            ]),
            libjq.jv_number(3),
        ]);
        var contained = libjq.jv_array(
        [
            libjq.jv_object([new("a", libjq.jv_number(1))]),
        ]);

        Assert.True(libjq.jv_contains(container, contained));
    }
}
