using System.Text.Json;
using DotNetJq.Port;

namespace DotNetJq.Tests;

public sealed class PathUpdateCompatibilityTests
{
    [Fact]
    public void SetPathEmptyManagedPathConsumesRootAndMovesReplacementLikeJq()
    {
        var root = libjq.jv_array([libjq.jv_number(1)]);
        var rootStorage = Assert.IsType<jvp_array>(root.Value);
        var replacement = libjq.jv_array([libjq.jv_number(2)]);
        var replacementStorage = Assert.IsType<jvp_array>(replacement.Value);
        var result = libjq.jv_invalid();
        try
        {
            result = libjq.jv_setpath(
                libjq.jv_copy(root),
                Array.Empty<jv>(),
                replacement);
            replacement = libjq.jv_invalid();

            Assert.Equal(1, rootStorage.Refcnt.Count);
            Assert.Equal(1, replacementStorage.Refcnt.Count);
            Assert.Same(replacementStorage, result.Value);
        }
        finally
        {
            libjq.jv_free(result);
            libjq.jv_free(replacement);
            libjq.jv_free(root);
        }

        Assert.Equal(0, rootStorage.Refcnt.Count);
        Assert.Equal(0, replacementStorage.Refcnt.Count);
    }

    [Fact]
    public void GetAtPathConsumesOnlyItsRootAndKeyCopies()
    {
        var key = libjq.jv_string("child");
        var root = libjq.jv_object_set(
            libjq.jv_object(),
            libjq.jv_copy(key),
            libjq.jv_string("value"));
        var fetched = libjq.jv_invalid();
        try
        {
            Assert.Equal(1, libjq.jv_get_refcnt(root));
            Assert.Equal(2, libjq.jv_get_refcnt(key));

            fetched = PathUpdates.GetAtPath(root, [key]);

            Assert.Equal("value", fetched.StringValue);
            Assert.Equal(1, libjq.jv_get_refcnt(root));
            Assert.Equal(2, libjq.jv_get_refcnt(key));
            Assert.Equal(2, libjq.jv_get_refcnt(fetched));
        }
        finally
        {
            libjq.jv_free(fetched);
            libjq.jv_free(root);
            libjq.jv_free(key);
        }
    }

    [Fact]
    public void FailedGetAtPathAlsoConsumesOnlyItsTemporaryOwners()
    {
        var root = libjq.jv_string("scalar");
        var key = libjq.jv_string("child");
        try
        {
            var exception = Assert.Throws<JqRuntimeException>(
                () => PathUpdates.GetAtPath(root, [key]));

            Assert.Contains("Cannot index string with string", exception.Message, StringComparison.Ordinal);
            Assert.Equal(1, libjq.jv_get_refcnt(root));
            Assert.Equal(1, libjq.jv_get_refcnt(key));
            Assert.Equal("scalar", root.StringValue);
            Assert.Equal("child", key.StringValue);
        }
        finally
        {
            libjq.jv_free(root);
            libjq.jv_free(key);
        }
    }

    [Fact]
    public void SetAtPathMovesARawCopyOfThePathKeyIntoANewObjectSlot()
    {
        var key = libjq.jv_string("child");
        var root = libjq.jv_object();
        var replacement = libjq.jv_string("value");
        var updated = libjq.jv_invalid();
        try
        {
            updated = PathUpdates.SetAtPath(root, [key], replacement);

            Assert.Equal(1, libjq.jv_get_refcnt(root));
            Assert.Equal(2, libjq.jv_get_refcnt(key));
            Assert.Equal(2, libjq.jv_get_refcnt(replacement));
            var selected = libjq.jv_object_get(
                libjq.jv_copy(updated),
                libjq.jv_copy(key));
            try
            {
                Assert.Equal("value", selected.StringValue);
            }
            finally
            {
                libjq.jv_free(selected);
            }

            libjq.jv_free(updated);
            updated = libjq.jv_invalid();
            Assert.Equal(1, libjq.jv_get_refcnt(key));
            Assert.Equal(1, libjq.jv_get_refcnt(replacement));
        }
        finally
        {
            libjq.jv_free(updated);
            libjq.jv_free(replacement);
            libjq.jv_free(root);
            libjq.jv_free(key);
        }
    }

    [Fact]
    public void SetAtPathOwnedMutatesAUniqueRootWithoutAnExtraCowCopy()
    {
        var key = libjq.jv_string("child");
        var root = libjq.jv_object();
        var rootStorage = Assert.IsType<jvp_object>(root.Value);
        var replacement = libjq.jv_string("value");
        var updated = libjq.jv_invalid();
        try
        {
            updated = PathUpdates.SetAtPathOwned(root, [key], replacement);
            root = libjq.jv_invalid();
            replacement = libjq.jv_invalid();

            Assert.Same(rootStorage, updated.Value);
            Assert.Equal(1, rootStorage.Refcnt.Count);
            Assert.Equal(2, libjq.jv_get_refcnt(key));
        }
        finally
        {
            libjq.jv_free(updated);
            libjq.jv_free(replacement);
            libjq.jv_free(root);
            libjq.jv_free(key);
        }

        Assert.Equal(0, rootStorage.Refcnt.Count);
    }

    [Fact]
    public void SetAtPathOwnedConsumesInputsWhenAPathCannotBeApplied()
    {
        var key = libjq.jv_string("child");
        var root = libjq.jv_string("scalar");
        var rootStorage = Assert.IsType<jvp_string>(root.Value);
        var replacement = libjq.jv_string("value");
        var replacementStorage = Assert.IsType<jvp_string>(replacement.Value);
        try
        {
            var exception = Assert.Throws<JqRuntimeException>(
                () => PathUpdates.SetAtPathOwned(root, [key], replacement));
            root = libjq.jv_invalid();
            replacement = libjq.jv_invalid();

            Assert.Contains("Cannot index string with string", exception.Message, StringComparison.Ordinal);
            Assert.Equal(0, rootStorage.Refcnt.Count);
            Assert.Equal(0, replacementStorage.Refcnt.Count);
            Assert.Equal(1, libjq.jv_get_refcnt(key));
        }
        finally
        {
            libjq.jv_free(replacement);
            libjq.jv_free(root);
            libjq.jv_free(key);
        }
    }

    [Fact]
    public void PathTracksSelectionsAndReportsTransformedValues()
    {
        AssertJson(
            "path(.[] | select(. > 3))",
            "[1,5,3]",
            "[1]");
        AssertJson(
            "try path(.a | map(select(.b == 0))) catch .",
            "{\"a\":[{\"b\":0}]}",
            "\"Invalid path expression with result [{\\\"b\\\":0}]\"");
        AssertJson(
            "try path(.a | map(select(.b == 0)) | .[0]) catch .",
            "{\"a\":[{\"b\":0}]}",
            "\"Invalid path expression near attempt to access element 0 of [{\\\"b\\\":0}]\"");
        AssertJson(
            "try path(.a | map(select(.b == 0)) | .[]) catch .",
            "{\"a\":[{\"b\":0}]}",
            "\"Invalid path expression near attempt to iterate through [{\\\"b\\\":0}]\"");
    }

    [Fact]
    public void CompoundAssignmentEvaluatesRightHandSideAgainstOriginalInput()
    {
        AssertJson(".foo += .foo", "{\"foo\":2}", "{\"foo\":4}");
        AssertJson(".[] += .[0]", "[1,2]", "[2,3]");
        AssertJson(
            ".[] //= .[0]",
            "[\"hello\",true,false,[false],null]",
            "[\"hello\",true,\"hello\",[false],\"hello\"]");
    }

    [Fact]
    public void UpdateAssignmentUsesOnlyFirstOutputAndDeletesOnEmpty()
    {
        AssertJson(".[0] |= (., . + 10)", "[1,2]", "[1,2]");
        AssertJson(".[] |= select(. % 2 == 0)", "[0,1,2,3,4,5]", "[0,2,4]");
        AssertJson(
            "(.[] | select(. >= 2)) |= empty",
            "[1,5,3,0,7]",
            "[1,0]");
        AssertJson(
            ".foo[1,4,2,3] |= empty",
            "{\"foo\":[0,1,2,3,4,5]}",
            "{\"foo\":[0,5]}");
    }

    [Fact]
    public void DynamicGetPathCanBeAnUpdateTarget()
    {
        AssertJson(
            "getpath([\"a\",0,\"b\"]) |= 5",
            "{\"a\":[{\"c\":3}]}",
            "{\"a\":[{\"c\":3,\"b\":5}]}");
        AssertJson(
            "try (getpath([\"a\",0,\"b\"]) |= 5) catch .",
            "{\"a\":0}",
            "\"Cannot index number with number (0)\"");
    }

    [Fact]
    public void FilterAndFunctionCallsRemainAssignablePaths()
    {
        AssertJson(
            "def inc(x): x |= .+1; inc(.[].a)",
            "[{\"a\":1,\"b\":2},{\"a\":2,\"b\":4},{\"a\":7,\"b\":8}]",
            "[{\"a\":2,\"b\":2},{\"a\":3,\"b\":4},{\"a\":8,\"b\":8}]");
        AssertJson("def x: .[1,2]; x=10", "[0,1,2]", "[0,10,10]");
    }

    [Fact]
    public void DeleteHandlesSimultaneousIndicesSlicesAndNan()
    {
        AssertJson(
            "del(.[1], .[-6], .[2], .[-3:9])",
            "[0,1,2,3,4,5,6,7,8,9]",
            "[0,3,5,6,9]");
        AssertJson("del(.[nan,nan])", "[1,2,3]", "[1,2,3]");
        AssertJson("del(.[1:3][0])", "[0,1,2,3,4]", "[0,2,3,4]");
    }

    [Fact]
    public void SliceAssignmentReplacesTheSelectedRange()
    {
        AssertJson("path(.[1:3])", "[0,1,2,3,4]", "[{\"start\":1,\"end\":3}]");
        AssertJson(".[1:3] = [8,9,10]", "[0,1,2,3,4]", "[0,8,9,10,3,4]");
        AssertJson(".[1:3] |= . + [8]", "[0,1,2,3,4]", "[0,1,2,8,3,4]");
        AssertJson(".[1.5:3.5] = [\"x\"]", "[0,1,2,3,4]", "[0,\"x\",4]");
        AssertJson(".[nan:1]", "[0,1,2]", "[0]");
        AssertJson(".[1:nan]", "[0,1,2]", "[1,2]");
        AssertJson(".[1:] = [7,8]", "null", "[7,8]");
        AssertJson(".[1:] |= . + [7,8]", "null", "[7,8]");
    }

    [Fact]
    public void OptionalPathOnlySuppressesFailureFromItsTerminalOperation()
    {
        AssertJson("path(.a?)", "1");
        AssertJson(
            "try path(.a.b?) catch .",
            "1",
            "\"Cannot index number with string (\\\"a\\\")\"");
        AssertJson(
            "try (.a.b? |= .) catch .",
            "1",
            "\"Cannot index number with string (\\\"a\\\")\"");
        AssertJson(
            "try (.a.b? = 4) catch .",
            "1",
            "\"Cannot index number with string (\\\"a\\\")\"");
        AssertJson(
            "try del(.a.b?) catch .",
            "[1,2]",
            "\"Cannot index array with string (\\\"a\\\")\"");
    }

    [Fact]
    public void ArrayUpdateIndicesFollowJqTruncationAndBoundsErrors()
    {
        AssertJson(".[1.1] = 5", "[0,1,2,3,4]", "[0,5,2,3,4]");
        AssertJson("[0,1,2,3][1:] | setpath([3]; 9)", "null", "[1,2,3,9]");
        AssertJson(
            "try (.[nan] = 9) catch .",
            "[0,1,2]",
            "\"Cannot set array element at NaN index\"");
        AssertJson(
            "try (.[999999999] = 0) catch .",
            "null",
            "\"Array index too large\"");
        AssertJson(
            "try ({} | .[999999999] = 0) catch .",
            "null",
            "\"Cannot index object with number (999999999)\"");
        AssertJson(
            "try (\"foobar\" | .[1.5]) catch .",
            "null",
            "\"Cannot index string with number (1.5)\"");
        AssertJson(
            "try (\"foobar\" | .[0]) catch .",
            "null",
            "\"Cannot index string with number (0)\"");
        AssertJson(
            "try (\"é😀\" | .[-1]) catch .",
            "null",
            "\"Cannot index string with number (-1)\"");
        AssertJson(
            "try (\"foobar\" | .[nan]) catch .",
            "null",
            "\"Cannot index string with number (null)\"");
        AssertJson("\"foobar\" | .[1:3]", "null", "\"oo\"");
    }

    [Theory]
    [InlineData("[]", "Cannot delete string element of array")]
    [InlineData("[1,2,3]", "Cannot delete string element of array")]
    [InlineData("\"\"", "Cannot delete fields from string")]
    [InlineData("true", "Cannot delete fields from boolean")]
    [InlineData("false", "Cannot delete fields from boolean")]
    [InlineData("0", "Cannot delete fields from number")]
    [InlineData("1", "Cannot delete fields from number")]
    public void DelpathsRejectsAStringKeyForAnIncompatibleReceiver(
        string input,
        string expectedMessage)
    {
        AssertJson(
            "try delpaths([[\"a\"]]) catch .",
            input,
            JsonSerializer.Serialize(expectedMessage));
    }

    [Fact]
    public void DelpathsPreservesValidMissingAndNullTraversalSemantics()
    {
        AssertJson("delpaths([[\"a\"]])", "{}", "{}");
        AssertJson("delpaths([[\"a\"]])", "null", "null");
        AssertJson("delpaths([[0]])", "[]", "[]");
        AssertJson(
            "try delpaths([[0]]) catch .",
            "{}",
            "\"Cannot delete number field of object\"");
        AssertJson(
            "try delpaths([[\"a\",\"b\"]]) catch .",
            "{\"a\":1}",
            "\"Cannot delete fields from number\"");
        AssertJson("delpaths([[\"a\",\"b\"]])", "{}", "{}");
    }

    [Theory]
    [InlineData("null")]
    [InlineData("false")]
    [InlineData("2")]
    [InlineData("\"value\"")]
    [InlineData("[1,2]")]
    [InlineData("{\"a\":1}")]
    public void DelpathsWithNoPathsIsIdentity(string input)
    {
        AssertJson("delpaths([])", input, input);
    }

    [Fact]
    public void PickBuildsTheMinimalSelectedStructure()
    {
        AssertJson("pick(.a.b.c)", "null", "{\"a\":{\"b\":{\"c\":null}}}");
        AssertJson("pick(first|first)", "[[10,20],30]", "[[10]]");
        AssertJson(
            "try pick(last) catch .",
            "[1,2]",
            "\"Out of bounds negative array index\"");
    }

    private static void AssertJson(string source, string input, params string[] expected)
    {
        var actual = JqProgram.Compile(source).Execute(input);
        Assert.Equal(expected.Length, actual.Count);
        for (var index = 0; index < expected.Length; index++)
        {
            using var document = JsonDocument.Parse(expected[index]);
            Assert.True(
                JsonElement.DeepEquals(document.RootElement, actual[index]),
                $"output[{index}] expected {document.RootElement.GetRawText()}, " +
                $"actual {actual[index].GetRawText()}");
        }
    }
}
