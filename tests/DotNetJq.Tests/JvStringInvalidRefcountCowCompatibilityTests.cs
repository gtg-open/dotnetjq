// DOTNETJQ PORT TEST MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream files: src/jv.c, src/jv.h, src/jq_test.c
// Primary upstream regressions: src/jq_test.c jv_test() string block, lines 704-731;
// src/jv.c jvp_invalid, jvp_string, jvp_string_append(), and jv_string_slice().
//
// These tests deliberately inspect jq's logical allocations. A consuming API receives its
// owner directly; observations which must preserve that owner spell jv_copy(), and retained
// allocated values are released with jv_free(). ReferenceEquals models native u.ptr identity.

using DotNetJq.Port;

namespace DotNetJq.Tests;

public sealed class JvStringInvalidRefcountCowCompatibilityTests
{
    [Fact]
    public void DistinctStringConstructorsAreEqualButNotIdentical()
    {
        var fromText = libjq.jv_string("foo");
        var fromBytes = libjq.jv_string_sized("foo"u8, 3);
        var sameAllocation = libjq.jv_copy(fromText);
        try
        {
            Assert.NotSame(fromText.Value, fromBytes.Value);
            Assert.True(libjq.jv_equal(libjq.jv_copy(fromText), libjq.jv_copy(fromBytes)));
            Assert.False(libjq.jv_identical(libjq.jv_copy(fromText), libjq.jv_copy(fromBytes)));
            Assert.True(libjq.jv_identical(libjq.jv_copy(fromText), libjq.jv_copy(sameAllocation)));
        }
        finally
        {
            libjq.jv_free(fromText);
            libjq.jv_free(fromBytes);
            libjq.jv_free(sameAllocation);
        }
    }

    [Fact]
    public void CopyAndFreeChangeOnlyTheStringAllocationReferenceCount()
    {
        var value = libjq.jv_string("owned");
        var storage = Storage(value);
        var firstCopy = libjq.jv_copy(value);
        var secondCopy = libjq.jv_copy(value);

        Assert.Equal(3, storage.Refcnt.Count);
        Assert.Same(value.Value, firstCopy.Value);
        Assert.Same(value.Value, secondCopy.Value);

        libjq.jv_free(firstCopy);
        Assert.Equal(2, storage.Refcnt.Count);
        libjq.jv_free(secondCopy);
        Assert.Equal(1, storage.Refcnt.Count);
        libjq.jv_free(value);
        Assert.Equal(0, storage.Refcnt.Count);
        Assert.Empty(storage.Data);
    }

    [Fact]
    public void Utf8ByteLengthCapacityAndEmbeddedNulMatchTheNativeAllocation()
    {
        // Direct counterpart of jq-1.8.2 src/jq_test.c's shortstr/longstr regression.
        ReadOnlySpan<byte> bytes = [0xC3, 0xA9, 0x00, 0xF0, 0x9F, 0x99, 0x82];
        var value = libjq.jv_string_sized(bytes, bytes.Length);
        try
        {
            var storage = Storage(value);
            Assert.Equal(7, libjq.jv_string_length_bytes(libjq.jv_copy(value)));
            Assert.Equal(7, storage.Length);
            Assert.Equal(7, storage.AllocLength);
            Assert.Equal(bytes.ToArray(), storage.Data.AsSpan(0, storage.Length).ToArray());
            Assert.Equal(0, storage.Data[storage.Length]);
            Assert.Equal("é\0🙂", value.StringValue);

            // jv_string_append_str() has the C strlen() contract and stops at NUL.
            var appended = libjq.jv_string_append_str(libjq.jv_string_empty(8), "a\0ignored");
            Assert.Equal("a", appended.StringValue);
            Assert.Equal(1, Storage(appended).Length);
            libjq.jv_free(appended);
        }
        finally
        {
            libjq.jv_free(value);
        }
    }

    [Fact]
    public void InvalidUtf8RepairUsesTheThreeBytesPerInputByteWorstCaseCapacity()
    {
        byte[] malformed = [0x61, 0xFF, 0x62];
        var value = libjq.jv_string_sized(malformed, malformed.Length);
        try
        {
            var storage = Storage(value);
            Assert.Equal("a�b", value.StringValue);
            Assert.Equal(5, storage.Length);
            Assert.Equal((3 * malformed.Length) + 1, storage.AllocLength);
            Assert.Equal(0, storage.Data[storage.Length]);
        }
        finally
        {
            libjq.jv_free(value);
        }
    }

    [Fact]
    public void EmptyReserveAndUniqueAppendReuseTheAllocationAndInvalidateCaches()
    {
        var value = libjq.jv_string_empty(9);
        var storage = Storage(value);
        try
        {
            Assert.Equal(0, storage.Length);
            Assert.Equal(9, storage.AllocLength);
            Assert.Equal(10, storage.Data.Length);
            Assert.All(storage.Data, item => Assert.Equal(0, item));

            value = libjq.jv_string_append_buf(value, "abc"u8);
            Assert.Same(storage, value.Value);
            Assert.Equal("abc", value.StringValue);

            var firstHash = libjq.jv_string_hash(libjq.jv_copy(value));
            Assert.True(storage.HasHash);
            Assert.Equal(firstHash, storage.Hash);

            value = libjq.jv_string_append_buf(value, "d"u8);
            Assert.Same(storage, value.Value);
            Assert.False(storage.HasHash);
            Assert.Equal("abcd", value.StringValue);

            var secondHash = libjq.jv_string_hash(libjq.jv_copy(value));
            Assert.True(storage.HasHash);
            Assert.Equal(secondHash, storage.Hash);
            Assert.NotEqual(firstHash, secondHash);
        }
        finally
        {
            libjq.jv_free(value);
        }
    }

    [Fact]
    public void AppendGrowthUsesTheMaximumOfTwiceTheNewLengthAndThirtyTwo()
    {
        var small = libjq.jv_string("abc");
        var smallStorage = Storage(small);
        small = libjq.jv_string_append_buf(small, "de"u8);
        Assert.NotSame(smallStorage, small.Value);
        Assert.Equal(32, Storage(small).AllocLength);
        Assert.Equal("abcde", small.StringValue);

        var large = libjq.jv_string("0123456789abcdefg");
        var largeStorage = Storage(large);
        large = libjq.jv_string_append_buf(large, "hij"u8);
        Assert.NotSame(largeStorage, large.Value);
        Assert.Equal(40, Storage(large).AllocLength);
        Assert.Equal("0123456789abcdefghij", large.StringValue);

        libjq.jv_free(small);
        libjq.jv_free(large);
    }

    [Fact]
    public void SharedAppendDetachesAndLeavesTheOtherOwnerUnchanged()
    {
        var value = libjq.jv_string_append_buf(libjq.jv_string_empty(16), "ab"u8);
        var sharedStorage = Storage(value);
        var alias = libjq.jv_copy(value);

        value = libjq.jv_string_append_buf(value, "c"u8);

        Assert.NotSame(sharedStorage, value.Value);
        Assert.Equal(1, sharedStorage.Refcnt.Count);
        Assert.Equal(32, Storage(value).AllocLength);
        Assert.Equal("ab", alias.StringValue);
        Assert.Equal("abc", value.StringValue);

        libjq.jv_free(value);
        libjq.jv_free(alias);
    }

    [Fact]
    public void SharedZeroLengthAppendStillDetachesLikeJvpStringAppend()
    {
        var value = libjq.jv_string_append_buf(libjq.jv_string_empty(8), "ab"u8);
        var sharedStorage = Storage(value);
        var alias = libjq.jv_copy(value);

        value = libjq.jv_string_append_buf(value, ReadOnlySpan<byte>.Empty);

        Assert.NotSame(sharedStorage, value.Value);
        Assert.Equal(1, sharedStorage.Refcnt.Count);
        Assert.Equal(32, Storage(value).AllocLength);
        Assert.Equal("ab", alias.StringValue);
        Assert.Equal("ab", value.StringValue);

        libjq.jv_free(value);
        libjq.jv_free(alias);
    }

    [Fact]
    public void ConcatUsesTheUniqueLeftAllocationWhenItsReserveIsSufficient()
    {
        var left = libjq.jv_string_append_buf(libjq.jv_string_empty(8), "ab"u8);
        var leftStorage = Storage(left);
        var right = libjq.jv_string("cd");
        var rightStorage = Storage(right);

        var result = libjq.jv_string_concat(left, right);

        Assert.Same(leftStorage, result.Value);
        Assert.Equal("abcd", result.StringValue);
        Assert.Equal(1, leftStorage.Refcnt.Count);
        Assert.Equal(0, rightStorage.Refcnt.Count);
        libjq.jv_free(result);
    }

    [Fact]
    public void StringSlicesAlwaysAllocateAndBeyondCodepointsReturnReserveSixteen()
    {
        // Unlike array slices, jq-1.8.2 jv_string_slice() always allocates a NUL-terminated copy.
        var original = libjq.jv_string("é🙂");
        var full = libjq.jv_string_slice(libjq.jv_copy(original), 0, 2);
        try
        {
            Assert.NotSame(original.Value, full.Value);
            Assert.Equal(original.StringValue, full.StringValue);
            Assert.Equal(6, Storage(full).AllocLength);

            // The byte-length clamp can leave start beyond the available codepoints.
            var beyond = libjq.jv_string_slice(libjq.jv_string("🙂"), 3, 4);
            Assert.Equal(string.Empty, beyond.StringValue);
            Assert.Equal(0, Storage(beyond).Length);
            Assert.Equal(16, Storage(beyond).AllocLength);
            libjq.jv_free(beyond);
        }
        finally
        {
            libjq.jv_free(original);
            libjq.jv_free(full);
        }
    }

    [Fact]
    public void SurrogateCodepointAppendPreservesRawBytesAndSliceReportsInvalidUtf8()
    {
        // jvp_utf8_encode() accepts the raw surrogate codepoint just like jq's C helper.
        var raw = libjq.jv_string_append_codepoint(libjq.jv_string_empty(3), 0xD800);
        try
        {
            var storage = Storage(raw);
            Assert.Equal(3, storage.Length);
            Assert.Equal(new byte[] { 0xED, 0xA0, 0x80 }, storage.Data.AsSpan(0, 3).ToArray());

            var slice = libjq.jv_string_slice(libjq.jv_copy(raw), 0, 1);
            try
            {
                AssertInvalidMessageBorrowed(slice, "Invalid UTF-8 string");
            }
            finally
            {
                libjq.jv_free(slice);
            }
        }
        finally
        {
            libjq.jv_free(raw);
        }
    }

    [Fact]
    public void ArrayAndObjectCopiesDeferStringChildCopiesUntilCowUnshare()
    {
        var arrayChild = libjq.jv_string("array-child");
        var retainedArrayChild = libjq.jv_copy(arrayChild);
        var array = libjq.jv_array_append(libjq.jv_array(), arrayChild);
        var arrayAlias = libjq.jv_copy(array);

        Assert.Equal(2, libjq.jv_get_refcnt(retainedArrayChild));
        array = libjq.jv_array_append(array, libjq.jv_null());
        Assert.NotSame(array.Value, arrayAlias.Value);
        Assert.Equal(3, libjq.jv_get_refcnt(retainedArrayChild));

        libjq.jv_free(array);
        libjq.jv_free(arrayAlias);
        Assert.Equal(1, libjq.jv_get_refcnt(retainedArrayChild));
        libjq.jv_free(retainedArrayChild);

        var objectChild = libjq.jv_string("object-child");
        var retainedObjectChild = libjq.jv_copy(objectChild);
        var value = libjq.jv_object_set(
            libjq.jv_object(),
            libjq.jv_string("child"),
            objectChild);
        var objectAlias = libjq.jv_copy(value);

        Assert.Equal(2, libjq.jv_get_refcnt(retainedObjectChild));
        value = libjq.jv_object_set(value, libjq.jv_string("marker"), libjq.jv_true());
        Assert.NotSame(value.Value, objectAlias.Value);
        Assert.Equal(3, libjq.jv_get_refcnt(retainedObjectChild));

        libjq.jv_free(value);
        libjq.jv_free(objectAlias);
        Assert.Equal(1, libjq.jv_get_refcnt(retainedObjectChild));
        libjq.jv_free(retainedObjectChild);
    }

    [Fact]
    public void InvalidCopiesOwnOnlyTheOuterWrapperUntilMessageExtraction()
    {
        var message = libjq.jv_string("detail");
        var retainedMessage = libjq.jv_copy(message);
        var invalid = libjq.jv_invalid_with_msg(message);
        var invalidStorage = InvalidStorage(invalid);
        try
        {
            Assert.Equal(1, invalidStorage.Refcnt.Count);
            Assert.Equal(2, libjq.jv_get_refcnt(retainedMessage));

            var alias = libjq.jv_copy(invalid);
            Assert.Equal(2, invalidStorage.Refcnt.Count);
            Assert.Equal(2, libjq.jv_get_refcnt(retainedMessage));
            Assert.True(libjq.jv_invalid_has_msg(alias));
            Assert.Equal(1, invalidStorage.Refcnt.Count);
            Assert.Equal(2, libjq.jv_get_refcnt(retainedMessage));

            var extracted = libjq.jv_invalid_get_msg(libjq.jv_copy(invalid));
            Assert.Same(retainedMessage.Value, extracted.Value);
            Assert.Equal(1, invalidStorage.Refcnt.Count);
            Assert.Equal(3, libjq.jv_get_refcnt(retainedMessage));
            libjq.jv_free(extracted);
            Assert.Equal(2, libjq.jv_get_refcnt(retainedMessage));

            libjq.jv_free(invalid);
            invalid = libjq.jv_invalid();
            Assert.Equal(0, invalidStorage.Refcnt.Count);
            Assert.Equal(1, libjq.jv_get_refcnt(retainedMessage));
        }
        finally
        {
            libjq.jv_free(invalid);
            libjq.jv_free(retainedMessage);
        }
    }

    [Fact]
    public void InvalidMessagesAreArbitraryJvValuesAndEqualityIgnoresTheirPayload()
    {
        var arrayMessage = libjq.jv_array_append(libjq.jv_array(), libjq.jv_number(42));
        var arrayStorage = Assert.IsType<jvp_array>(arrayMessage.Value);
        var arbitrary = libjq.jv_invalid_with_msg(arrayMessage);
        var extracted = libjq.jv_invalid_get_msg(arbitrary);
        try
        {
            Assert.Same(arrayStorage, extracted.Value);
            Assert.Equal(jv_kind.JV_KIND_ARRAY, extracted.Kind);
            Assert.Equal(1, libjq.jv_array_length(libjq.jv_copy(extracted)));
        }
        finally
        {
            libjq.jv_free(extracted);
        }

        var left = libjq.jv_invalid_with_msg(libjq.jv_string("left"));
        var leftAlias = libjq.jv_copy(left);
        var right = libjq.jv_invalid_with_msg(libjq.jv_string("right"));
        try
        {
            Assert.True(libjq.jv_identical(libjq.jv_copy(left), libjq.jv_copy(leftAlias)));
            Assert.False(libjq.jv_identical(libjq.jv_copy(left), libjq.jv_copy(right)));
            Assert.True(libjq.jv_equal(libjq.jv_copy(left), libjq.jv_copy(right)));
            Assert.True(libjq.jv_equal(libjq.jv_invalid(), libjq.jv_copy(left)));
        }
        finally
        {
            libjq.jv_free(left);
            libjq.jv_free(leftAlias);
            libjq.jv_free(right);
        }
    }

    [Fact]
    public void FreeReleasesTwentyThousandNestedInvalidMessagesIteratively()
    {
        const int depth = 20_000;
        var leaf = libjq.jv_string("leaf");
        var leafStorage = Storage(leaf);
        var root = leaf;
        var wrappers = new jvp_invalid[depth];

        for (var index = 0; index < depth; index++)
        {
            root = libjq.jv_invalid_with_msg(root);
            wrappers[index] = InvalidStorage(root);
        }

        libjq.jv_free(root);

        Assert.Equal(0, leafStorage.Refcnt.Count);
        Assert.All(wrappers, wrapper => Assert.Equal(0, wrapper.Refcnt.Count));
    }

    private static jvp_string Storage(jv value)
    {
        Assert.Equal(jv_kind.JV_KIND_STRING, value.Kind);
        return Assert.IsType<jvp_string>(value.Value);
    }

    private static jvp_invalid InvalidStorage(jv value)
    {
        Assert.Equal(jv_kind.JV_KIND_INVALID, value.Kind);
        return Assert.IsType<jvp_invalid>(value.Value);
    }

    private static void AssertInvalidMessageBorrowed(jv invalid, string expected)
    {
        Assert.False(invalid.IsValid);
        Assert.True(libjq.jv_invalid_has_msg(libjq.jv_copy(invalid)));
        var message = libjq.jv_invalid_get_msg(libjq.jv_copy(invalid));
        try
        {
            Assert.Equal(expected, message.StringValue);
        }
        finally
        {
            libjq.jv_free(message);
        }
    }
}
