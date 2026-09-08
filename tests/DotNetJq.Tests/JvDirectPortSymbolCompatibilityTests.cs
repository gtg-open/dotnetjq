using DotNetJq.Port;
using DotNetJq.Compatibility.FileSystem;

namespace DotNetJq.Tests;

public sealed class JvDirectPortSymbolCompatibilityTests
{
    private static readonly string[] ExpectedObjectNames = ["a", "z", "n"];

    [Fact]
    public void InvalidMessageSymbolsPreserveAbsentAndPresentPayloads()
    {
        var absent = libjq.jv_invalid();
        var present = libjq.jv_invalid_with_msg(libjq.jv_string("detail"));

        Assert.Equal("<invalid>", libjq.jv_kind_name(jv_kind.JV_KIND_INVALID));
        Assert.False(libjq.jv_invalid_has_msg(libjq.jv_copy(absent)));
        var absentMessage = libjq.jv_invalid_get_msg(absent);
        Assert.Equal(jv_kind.JV_KIND_NULL, absentMessage.Kind);
        libjq.jv_free(absentMessage);
        Assert.True(libjq.jv_invalid_has_msg(libjq.jv_copy(present)));
        Assert.Equal("detail", StringAndFree(libjq.jv_invalid_get_msg(present)));
    }

    [Fact]
    public void LiteralAndIdentitySymbolsMatchJvAllocationSemantics()
    {
        var literal = libjq.jv_number_with_literal("1.0000000000000001");
        var lexicalLiteral = libjq.jv_number_with_literal("1e+2");
        var sameAllocation = libjq.jv_copy(literal);
        var equalLiteral = libjq.jv_number_with_literal("1.0000000000000001");
        var lowerExact = libjq.jv_number_with_literal("9007199254740992");
        var higherExact = libjq.jv_number_with_literal("9007199254740993");
        var positiveZero = libjq.jv_number(0d);
        var negativeZero = libjq.jv_number(-0d);

        Assert.True(libjq.jv_number_has_literal(literal));
        Assert.Equal("1.0000000000000001", libjq.jv_number_get_literal(literal));
        Assert.Equal("1E+2", libjq.jv_number_get_literal(lexicalLiteral));
        Assert.False(libjq.jv_number_with_literal("not-a-number").IsValid);
        Assert.False(libjq.jv_number_has_literal(libjq.jv_number_with_literal("NaN")));
        Assert.Equal(1d, libjq.jv_number_value(literal));
        Assert.True(libjq.jv_is_integer(literal));
        Assert.True(libjq.jv_is_integer(libjq.jv_number(double.PositiveInfinity)));
        Assert.False(libjq.jv_is_integer(libjq.jv_number(double.NaN)));
        Assert.True(libjq.jv_identical(libjq.jv_copy(literal), libjq.jv_copy(sameAllocation)));
        Assert.False(libjq.jv_identical(libjq.jv_copy(literal), libjq.jv_copy(equalLiteral)));
        Assert.True(libjq.jv_equal(libjq.jv_copy(literal), libjq.jv_copy(equalLiteral)));
        Assert.False(libjq.jv_identical(libjq.jv_copy(positiveZero), libjq.jv_copy(negativeZero)));
        Assert.True(libjq.jv_equal(libjq.jv_copy(positiveZero), libjq.jv_copy(negativeZero)));
        Assert.Equal(
            -1,
            libjq.jvp_number_cmp(lowerExact, higherExact));
        Assert.False(libjq.jv_identical(libjq.jv_array(), libjq.jv_array()));
        Assert.False(libjq.jv_identical(libjq.jv_object(), libjq.jv_object()));
        Assert.Equal(2, libjq.jv_get_refcnt(literal));

        libjq.jv_free(higherExact);
        libjq.jv_free(lowerExact);
        libjq.jv_free(equalLiteral);
        libjq.jv_free(sameAllocation);
        Assert.Equal(1, libjq.jv_get_refcnt(literal));
        libjq.jv_free(literal);
        libjq.jv_free(lexicalLiteral);
    }

    [Fact]
    public void ArrayConstructionConcatenationAndSlicingUseJqBounds()
    {
        var empty = libjq.jv_array_sized(128);
        var values = libjq.jv_array_concat(
            libjq.jv_array([libjq.jv_number(0), libjq.jv_number(1)]),
            libjq.jv_array([libjq.jv_number(2), libjq.jv_number(3)]));

        Assert.Equal(0, empty.ArrayValue.Count);
        Assert.Equal("[1,2]", DumpAndFree(libjq.jv_array_slice(libjq.jv_copy(values), 1, 3)));
        Assert.Equal("[0,1,2]", DumpAndFree(libjq.jv_array_slice(libjq.jv_copy(values), -99, -1)));
        Assert.Equal("[]", DumpAndFree(libjq.jv_array_slice(libjq.jv_copy(values), 3, 1)));
        Assert.False(libjq.jv_array_get(libjq.jv_copy(values), -1).IsValid);
        Assert.False(libjq.jv_array_get(libjq.jv_copy(values), 4).IsValid);
        Assert.Equal(3, libjq.jv_get(libjq.jv_copy(values), libjq.jv_number(-1)).NumberValue);
        Assert.Equal(
            jv_kind.JV_KIND_NULL,
            libjq.jv_get(libjq.jv_copy(values), libjq.jv_number(4)).Kind);
        libjq.jv_free(empty);
        libjq.jv_free(values);
    }

    [Fact]
    public void EmptyArrayAndObjectConstructorsKeepIndependentStorage()
    {
        var firstArray = libjq.jv_array();
        var secondArray = libjq.jv_array();
        var firstEnumerableArray = libjq.jv_array(Array.Empty<jv>());
        var secondEnumerableArray = libjq.jv_array(Array.Empty<jv>());
        var firstObject = libjq.jv_object();
        var secondObject = libjq.jv_object();

        Assert.NotSame(firstArray.Value, secondArray.Value);
        Assert.NotSame(firstEnumerableArray.Value, secondEnumerableArray.Value);
        Assert.NotSame(firstObject.Value, secondObject.Value);

        var populatedArray = libjq.jv_array_append(firstArray, libjq.jv_number(1));
        var populatedObject = libjq.jv_object_set(firstObject, "key", libjq.jv_number(2));

        Assert.Equal("[1]", libjq.jv_dump_string(populatedArray));
        Assert.Equal("[]", libjq.jv_dump_string(secondArray));
        Assert.Equal("{\"key\":2}", libjq.jv_dump_string(populatedObject));
        Assert.Equal("{}", libjq.jv_dump_string(secondObject));
    }

    [Fact]
    public void StringSymbolsUseUtf8BytesAndUnicodeScalarIndexes()
    {
        byte[] malformed = [0x61, 0xE2, 0x82, 0x62];
        var repaired = libjq.jv_string_sized(malformed, malformed.Length);
        var value = libjq.jv_string("éé🙂");

        Assert.Equal("a�b", repaired.StringValue);
        Assert.Equal(8, libjq.jv_string_length_bytes(libjq.jv_copy(value)));
        Assert.Equal(
            "[0,1]",
            DumpAndFree(libjq.jv_string_indexes(libjq.jv_copy(value), libjq.jv_string("é"))));
        Assert.Equal("🙂", StringAndFree(libjq.jv_string_slice(libjq.jv_copy(value), 2, 3)));
        Assert.Equal(
            "[]",
            DumpAndFree(
                libjq.jv_string_split(libjq.jv_string(""), libjq.jv_string(","))));
        Assert.Equal(
            "[\"a\",\"🙂\"]",
            DumpAndFree(
                libjq.jv_string_split(libjq.jv_string("a🙂"), libjq.jv_string(""))));
        Assert.Equal(jv_kind.JV_KIND_NULL, libjq.jv_string_repeat(value, -1).Kind);
        Assert.Equal("abab", StringAndFree(libjq.jv_string_repeat(libjq.jv_string("ab"), 2)));
        Assert.Equal(
            "a�b🙂",
            StringAndFree(libjq.jv_string_append_codepoint(
                libjq.jv_string_append_buf(libjq.jv_string("a"), malformed.AsSpan(1)),
                0x1F642)));
        libjq.jv_free(repaired);
    }

    [Fact]
    public void ObjectRawAccessIterationAndMergeRetainUpstreamContracts()
    {
        var left = libjq.jv_object(
        [
            new("a", libjq.jv_object([new("x", libjq.jv_number(1))])),
            new("z", libjq.jv_number(0)),
        ]);
        var right = libjq.jv_object(
        [
            new("a", libjq.jv_object([new("y", libjq.jv_number(2))])),
            new("n", libjq.jv_number(3)),
        ]);
        var merged = libjq.jv_object_merge_recursive(left, right);

        try
        {
            Assert.False(libjq.jv_object_get(
                libjq.jv_copy(merged),
                libjq.jv_string("missing")).IsValid);
            Assert.Equal(
                "{\"a\":{\"x\":1,\"y\":2},\"z\":0,\"n\":3}",
                libjq.jv_dump_string_borrowed(merged));

            var iterator = libjq.jv_object_iter(merged);
            var names = new List<string>();
            while (libjq.jv_object_iter_valid(merged, iterator))
            {
                var key = libjq.jv_object_iter_key(merged, iterator);
                var element = libjq.jv_object_iter_value(merged, iterator);
                try
                {
                    names.Add(key.StringValue);
                }
                finally
                {
                    libjq.jv_free(key);
                    libjq.jv_free(element);
                }

                iterator = libjq.jv_object_iter_next(merged, iterator);
            }

            Assert.Equal(ExpectedObjectNames, names);
            Assert.True(libjq.jv_identical(libjq.jv_copy(merged), libjq.jv_copy(merged)));
            Assert.False(
                libjq.jv_identical(
                    libjq.jv_copy(merged),
                    libjq.jv_object_merge(libjq.jv_object(), libjq.jv_copy(merged))));
        }
        finally
        {
            libjq.jv_free(merged);
        }
    }

    [Fact]
    public void ManagedHashKeepsObjectKeysOnTheRawUtf8Boundary()
    {
        var supplementaryKey = libjq.jv_string("\U00010000");
        var privateUseKey = libjq.jv_string("\uE000");
        var supplementaryStorage = Assert.IsType<jvp_string>(supplementaryKey.Value);
        var privateUseStorage = Assert.IsType<jvp_string>(privateUseKey.Value);
        var forward = libjq.jv_object_set(
            libjq.jv_object_set(libjq.jv_object(), supplementaryKey, libjq.jv_number(1)),
            privateUseKey,
            libjq.jv_number(2));
        var reverse = libjq.jv_object(
        [
            new("\uE000", libjq.jv_number(2)),
            new("\U00010000", libjq.jv_number(1)),
        ]);

        try
        {
            Assert.True(libjq.jv_equal(libjq.jv_copy(forward), libjq.jv_copy(reverse)));
            Assert.Equal(libjq.jv_hash(forward), libjq.jv_hash(reverse));
            Assert.Null(supplementaryStorage.CachedText);
            Assert.Null(privateUseStorage.CachedText);
        }
        finally
        {
            libjq.jv_free(forward);
            libjq.jv_free(reverse);
        }
    }

    [Fact]
    public void AuxAccessPathAndKeySymbolsPreserveJqResults()
    {
        var value = libjq.jv_parse("{\"a\":[10,20,30],\"z\":0}");

        Assert.Equal(
            20,
            libjq.jv_get(libjq.jv_object_get(value, "a"), libjq.jv_number(1.9)).NumberValue);
        Assert.Equal(
            jv_kind.JV_KIND_NULL,
            libjq.jv_get(
                libjq.jv_object_get(value, "a"),
                libjq.jv_number(double.NaN)).Kind);
        Assert.True(
            libjq.jv_has(
                libjq.jv_object_get(value, "a"),
                libjq.jv_number(0.9)).IsTruthy);
        Assert.True(
            libjq.jv_has(
                libjq.jv_object_get(value, "a"),
                libjq.jv_number(-0.9)).IsTruthy);
        Assert.False(
            libjq.jv_has(
                libjq.jv_object_get(value, "a"),
                libjq.jv_number(-1)).IsTruthy);
        Assert.Equal(
            "[\"a\",\"z\"]",
            libjq.jv_dump_string(libjq.jv_keys(libjq.jv_copy(value))));
        Assert.Equal(
            "[\"a\",\"z\"]",
            libjq.jv_dump_string(libjq.jv_keys_unsorted(libjq.jv_copy(value))));
        Assert.Equal(
            "[10,20,30]",
            libjq.jv_dump_string(
                libjq.jv_getpath(
                    libjq.jv_copy(value),
                    libjq.jv_array([libjq.jv_string("a")]))));

        var invalidRoot = libjq.jv_invalid_with_msg(libjq.jv_string("root failure"));
        var oneComponentPath = libjq.jv_array([libjq.jv_string("a")]);
        try
        {
            Assert.True(libjq.jv_identical(
                libjq.jv_copy(invalidRoot),
                libjq.jv_getpath(
                    libjq.jv_copy(invalidRoot),
                    libjq.jv_copy(oneComponentPath))));
            Assert.True(libjq.jv_identical(
                libjq.jv_copy(invalidRoot),
                libjq.jv_setpath(
                    libjq.jv_copy(invalidRoot),
                    libjq.jv_copy(oneComponentPath),
                    libjq.jv_number(1))));
        }
        finally
        {
            libjq.jv_free(invalidRoot);
            libjq.jv_free(oneComponentPath);
        }

        var set = libjq.jv_set(libjq.jv_null(), libjq.jv_string("created"), libjq.jv_true());
        Assert.Equal("{\"created\":true}", libjq.jv_dump_string(set));

        var slice = libjq.jv_object(
        [
            new("start", libjq.jv_number(1)),
            new("end", libjq.jv_number(2)),
        ]);
        var replaced = libjq.jv_set(
            libjq.jv_array(
            [
                libjq.jv_number(0),
                libjq.jv_number(1),
                libjq.jv_number(2),
            ]),
            slice,
            libjq.jv_array([libjq.jv_number(8), libjq.jv_number(9)]));
        Assert.Equal("[0,8,9,2]", libjq.jv_dump_string(replaced));

        var deleted = libjq.jv_delpaths(
            value,
            libjq.jv_array(
            [
                libjq.jv_array([libjq.jv_string("a"), libjq.jv_number(1)]),
                libjq.jv_array([libjq.jv_string("z")]),
            ]));
        Assert.Equal("{\"a\":[10,30]}", libjq.jv_dump_string(deleted));
    }

    [Fact]
    public void KeyedSortGroupAndUniqueUseStableJqComparison()
    {
        var objects = libjq.jv_array(
        [
            libjq.jv_string("first-two"),
            libjq.jv_string("one"),
            libjq.jv_string("second-two"),
        ]);
        var keys = libjq.jv_array(
        [
            libjq.jv_number(2),
            libjq.jv_number(1),
            libjq.jv_number(2),
        ]);

        Assert.Equal(
            "[\"one\",\"first-two\",\"second-two\"]",
            DumpAndFree(libjq.jv_sort(
                libjq.jv_copy(objects),
                libjq.jv_copy(keys))));
        Assert.Equal(
            "[[\"one\"],[\"first-two\",\"second-two\"]]",
            DumpAndFree(libjq.jv_group(
                libjq.jv_copy(objects),
                libjq.jv_copy(keys))));
        Assert.Equal(
            "[\"one\",\"first-two\"]",
            DumpAndFree(libjq.jv_unique(objects, keys)));
    }

    [Fact]
    public void KeyedSortConvertsComparisonDepthFailureToJvInvalidMessage()
    {
        var result = libjq.jv_sort(
            libjq.jv_array([libjq.jv_string("left"), libjq.jv_string("right")]),
            libjq.jv_array([NestedArrays(10_001), NestedArrays(10_001)]));

        Assert.True(libjq.jv_invalid_has_msg(libjq.jv_copy(result)));
        Assert.Equal("Comparison too deep", StringAndFree(libjq.jv_invalid_get_msg(result)));

        var indexes = libjq.jv_array_indexes(
            libjq.jv_array([NestedArrays(10_001)]),
            libjq.jv_array([NestedArrays(10_001)]));
        Assert.True(libjq.jv_invalid_has_msg(libjq.jv_copy(indexes)));
        Assert.Equal("Equality check too deep", StringAndFree(libjq.jv_invalid_get_msg(indexes)));
    }

    [Fact]
    public void LinkerRetainsNativeLoadProgramEntryPointOverBlockIr()
    {
        var resolver = new JqModuleResolver(
            JqDenyAllFileSystem.Instance,
            Array.Empty<string>(),
            "/program");
        using var state = new jq_state(JqExecutionOptions.Default);
        var source = libjq.locfile_init("/program/main.jq", "1");
        var linked = libjq.gen_noop();
        try
        {
            Assert.Equal(
                0,
                libjq.load_program(
                    state,
                    source,
                    resolver,
                    out linked,
                    "/program",
                    loadUserStartupLibrary: false));
            Assert.True(libjq.block_has_main(linked));
            Assert.Same(resolver, state.ModuleResolver);
        }
        finally
        {
            libjq.block_free(linked);
            libjq.locfile_free(source);
        }
    }

    private static jv NestedArrays(int depth)
    {
        var value = libjq.jv_null();
        for (var index = 0; index < depth; index++)
        {
            value = libjq.jv_array([value]);
        }

        return value;
    }

    private static string DumpAndFree(jv value) => libjq.jv_dump_string(value);

    private static string StringAndFree(jv value)
    {
        try
        {
            return value.StringValue;
        }
        finally
        {
            libjq.jv_free(value);
        }
    }
}
