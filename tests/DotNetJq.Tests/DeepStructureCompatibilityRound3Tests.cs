using DotNetJq.Port;

namespace DotNetJq.Tests;

public sealed class DeepStructureCompatibilityRound3Tests
{
    private const int JqMaximumStructuralDepth = 10_000;

    [Fact]
    public void ContainsAcceptsJqMaximumDepthAndRejectsTheNextLevel()
    {
        var leftAtLimit = NestedArrays(JqMaximumStructuralDepth, libjq.jv_number(1));
        var rightAtLimit = NestedArrays(JqMaximumStructuralDepth, libjq.jv_number(1));

        Assert.True(libjq.jv_contains(
            libjq.jv_copy(leftAtLimit),
            libjq.jv_copy(rightAtLimit)));

        var leftTooDeep = libjq.jv_array([leftAtLimit]);
        var rightTooDeep = libjq.jv_array([rightAtLimit]);
        var error = Assert.Throws<JqRuntimeException>(
            () => libjq.jv_contains(leftTooDeep, rightTooDeep));

        Assert.Equal("Containment check too deep", error.Message);
    }

    [Fact]
    public void EqualityAcceptsJqMaximumDepthAndRejectsTheNextLevel()
    {
        var leftAtLimit = NestedArrays(JqMaximumStructuralDepth, libjq.jv_number(1));
        var rightAtLimit = NestedArrays(JqMaximumStructuralDepth, libjq.jv_number(1));

        Assert.True(libjq.jv_equal(
            libjq.jv_copy(leftAtLimit),
            libjq.jv_copy(rightAtLimit)));

        var leftTooDeep = libjq.jv_array([leftAtLimit]);
        var rightTooDeep = libjq.jv_array([rightAtLimit]);
        var error = Assert.Throws<JqRuntimeException>(
            () => libjq.jv_equal(leftTooDeep, rightTooDeep));

        Assert.Equal("Equality check too deep", error.Message);
    }

    [Fact]
    public void EqualityRetainsUpstreamIdentityFastPathForSharedDeepValue()
    {
        var value = NestedArrays(JqMaximumStructuralDepth + 1, libjq.jv_null());

        Assert.True(libjq.jv_equal(libjq.jv_copy(value), value));
    }

    [Fact]
    public void ObjectEqualityAcceptsJqMaximumDepthAndRejectsTheNextLevel()
    {
        var leftAtLimit = NestedObjects(JqMaximumStructuralDepth);
        var rightAtLimit = NestedObjects(JqMaximumStructuralDepth);

        Assert.True(libjq.jv_equal(
            libjq.jv_copy(leftAtLimit),
            libjq.jv_copy(rightAtLimit)));

        var error = Assert.Throws<JqRuntimeException>(
            () => libjq.jv_equal(WrapObject(leftAtLimit), WrapObject(rightAtLimit)));

        Assert.Equal("Equality check too deep", error.Message);
    }

    [Fact]
    public void ArrayComparisonAcceptsJqMaximumDepthAndRejectsTheNextLevel()
    {
        var leftAtLimit = NestedArrays(JqMaximumStructuralDepth, libjq.jv_number(1));
        var rightAtLimit = NestedArrays(JqMaximumStructuralDepth, libjq.jv_number(1));

        Assert.Equal(0, libjq.jv_cmp(
            libjq.jv_copy(leftAtLimit),
            libjq.jv_copy(rightAtLimit)));

        var leftTooDeep = libjq.jv_array([leftAtLimit]);
        var rightTooDeep = libjq.jv_array([rightAtLimit]);
        var error = Assert.Throws<JqRuntimeException>(
            () => libjq.jv_cmp(leftTooDeep, rightTooDeep));

        Assert.Equal("Comparison too deep", error.Message);
    }

    [Fact]
    public void ObjectComparisonPreservesUpstreamKeyArrayDepthAccounting()
    {
        // jvp_cmp compares an object's synthesized key array at depth + 1.
        // Consequently jq-1.8.2 accepts 9,999 nested objects and rejects
        // 10,000, one level earlier than the corresponding array comparison.
        var leftAtLimit = NestedObjects(JqMaximumStructuralDepth - 1);
        var rightAtLimit = NestedObjects(JqMaximumStructuralDepth - 1);

        Assert.Equal(0, libjq.jv_cmp(
            libjq.jv_copy(leftAtLimit),
            libjq.jv_copy(rightAtLimit)));

        var leftTooDeep = WrapObject(leftAtLimit);
        var rightTooDeep = WrapObject(rightAtLimit);
        var error = Assert.Throws<JqRuntimeException>(
            () => libjq.jv_cmp(leftTooDeep, rightTooDeep));

        Assert.Equal("Comparison too deep", error.Message);
    }

    [Fact]
    public void ObjectComparisonOrdersTheCompleteKeySetBeforeValues()
    {
        var left = libjq.jv_object(
        [
            new("a", libjq.jv_number(100)),
            new("c", libjq.jv_number(0)),
        ]);
        var right = libjq.jv_object(
        [
            new("a", libjq.jv_number(0)),
            new("b", libjq.jv_number(0)),
        ]);

        // Sorted keys ["a","c"] compare greater than ["a","b"].  Values
        // are considered only if all keys compare equal.
        Assert.True(libjq.jv_cmp(left, right) > 0);
    }

    [Fact]
    public void ComparisonUsesUnicodeScalarRatherThanUtf16CodeUnitOrder()
    {
        Assert.True(libjq.jv_cmp(libjq.jv_string("😀"), libjq.jv_string("\uE000")) > 0);
    }

    [Fact]
    public void MissingObjectKeyDoesNotContainNull()
    {
        var container = libjq.jv_object();
        var contained = libjq.jv_object([new("missing", libjq.jv_null())]);

        Assert.False(libjq.jv_contains(container, contained));
    }

    [Theory]
    [InlineData(
        "try (reduce range(10001) as $_ ([]; [.]) as $x | $x | contains($x)) catch .",
        "\"Containment check too deep\"")]
    [InlineData(
        "try ((reduce range(10001) as $_ ([]; [.])) as $x | (reduce range(10001) as $_ ([]; [.])) as $y | $x == $y) catch .",
        "\"Equality check too deep\"")]
    [InlineData(
        "try ((reduce range(10001) as $_ ([]; [.])) as $x | [$x, $x] | sort) catch .",
        "\"Comparison too deep\"")]
    [InlineData(
        "try ((reduce range(10001) as $_ ([]; [.])) as $x | [$x, $x] | unique) catch .",
        "\"Comparison too deep\"")]
    [InlineData(
        "try ((reduce range(10001) as $_ ({}; {a: .})) as $x | [$x, $x] | sort) catch .",
        "\"Comparison too deep\"")]
    [InlineData(
        "try ((reduce range(10001) as $_ ({}; {a: .})) as $x | [$x, $x] | unique) catch .",
        "\"Comparison too deep\"")]
    public void OfficialDeepOperationFailuresAreCatchable(string filter, string expected)
    {
        var output = Assert.Single(JqProgram.Compile(filter).Execute("null"));

        Assert.Equal(expected, output.GetRawText());
    }

    private static jv NestedArrays(int depth, jv leaf)
    {
        var value = leaf;
        for (var index = 0; index < depth; index++)
        {
            value = libjq.jv_array([value]);
        }

        return value;
    }

    private static jv NestedObjects(int depth)
    {
        var value = libjq.jv_object();
        for (var index = 0; index < depth; index++)
        {
            value = WrapObject(value);
        }

        return value;
    }

    private static jv WrapObject(jv value) =>
        libjq.jv_object([new("a", value)]);
}
