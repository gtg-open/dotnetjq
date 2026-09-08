// DOTNETJQ PORT TEST MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream files: src/jv.c, src/jv.h, src/jq_test.c
// Primary upstream regression: src/jq_test.c jv_test() array block, lines 604-710.
//
// These tests deliberately use jq's ownership vocabulary. A function that consumes a jv
// receives the owned handle directly; a borrowed observation is expressed by jv_copy(), and
// every retained allocated handle is released with jv_free(). Storage identity assertions are
// the managed analogue of jq_test.c's direct u.ptr comparisons.

using DotNetJq.Port;

namespace DotNetJq.Tests;

public sealed class JvArrayRefcountCowCompatibilityTests
{
    [Fact]
    public void CopyAndFreeChangeOnlyTheLogicalOwnerCount()
    {
        var first = libjq.jv_array();
        Assert.Equal(1, libjq.jv_get_refcnt(first));

        var second = libjq.jv_copy(first);
        Assert.Equal(2, libjq.jv_get_refcnt(first));
        Assert.Equal(2, libjq.jv_get_refcnt(second));
        Assert.Same(Storage(first), Storage(second));
        Assert.True(IdenticalBorrowed(first, second));
        Assert.Equal(2, libjq.jv_get_refcnt(first));

        var third = libjq.jv_copy(second);
        Assert.Equal(3, libjq.jv_get_refcnt(first));

        libjq.jv_free(second);
        Assert.Equal(2, libjq.jv_get_refcnt(first));
        libjq.jv_free(third);
        Assert.Equal(1, libjq.jv_get_refcnt(first));

        Assert.Equal(1, libjq.jv_get_refcnt(libjq.jv_null()));
        Assert.Equal(1, libjq.jv_get_refcnt(libjq.jv_number(1)));
        libjq.jv_free(first);
    }

    [Fact]
    public void ArraySlotsOwnTheirAllocatedChildrenAndGetReturnsAnotherOwner()
    {
        var leaf = Numbers(7);
        var retainedLeaf = libjq.jv_copy(leaf);
        Assert.Equal(2, libjq.jv_get_refcnt(leaf));

        var array = libjq.jv_array_append(libjq.jv_array(), leaf);
        Assert.Equal(2, libjq.jv_get_refcnt(retainedLeaf));

        var fetched = libjq.jv_array_get(libjq.jv_copy(array), 0);
        Assert.Equal(3, libjq.jv_get_refcnt(retainedLeaf));
        Assert.Same(retainedLeaf.Value, fetched.Value);

        libjq.jv_free(fetched);
        Assert.Equal(2, libjq.jv_get_refcnt(retainedLeaf));
        libjq.jv_free(array);
        Assert.Equal(1, libjq.jv_get_refcnt(retainedLeaf));
        libjq.jv_free(retainedLeaf);
    }

    [Fact]
    public void CapacityGrowthMatchesJqArraySizeRoundUp()
    {
        var array = libjq.jv_array();
        var storage16 = Storage(array);
        Assert.Equal(16, storage16.AllocLength);

        for (var index = 0; index < 16; index++)
        {
            array = libjq.jv_array_append(array, libjq.jv_number(index));
            Assert.Same(storage16, Storage(array));
            Assert.Equal(1, libjq.jv_get_refcnt(array));
        }

        array = libjq.jv_array_append(array, libjq.jv_number(16));
        var storage25 = Storage(array);
        Assert.NotSame(storage16, storage25);
        Assert.Equal(25, storage25.AllocLength);

        for (var index = 17; index < 25; index++)
        {
            array = libjq.jv_array_append(array, libjq.jv_number(index));
            Assert.Same(storage25, Storage(array));
        }

        array = libjq.jv_array_append(array, libjq.jv_number(25));
        var storage39 = Storage(array);
        Assert.NotSame(storage25, storage39);
        Assert.Equal(39, storage39.AllocLength);
        Assert.Equal(26, LengthBorrowed(array));
        libjq.jv_free(array);

        var sized = libjq.jv_array_sized(4);
        var storage4 = Storage(sized);
        Assert.Equal(4, storage4.AllocLength);
        for (var index = 0; index < 4; index++)
        {
            sized = libjq.jv_array_append(sized, libjq.jv_number(index));
            Assert.Same(storage4, Storage(sized));
        }

        sized = libjq.jv_array_append(sized, libjq.jv_number(4));
        Assert.NotSame(storage4, Storage(sized));
        Assert.Equal(7, Storage(sized).AllocLength);
        libjq.jv_free(sized);
    }

    [Fact]
    public void ArraySetIndexLimitMatchesPinnedJqWithoutAllocatingTheSparseArray()
    {
        const int oldManagedPreCap = int.MaxValue / 16;
        const int nativeMaximumAtOffsetZero = int.MaxValue >> 2;

        Assert.False(libjq.jvp_array_set_index_too_large(oldManagedPreCap, arrayOffset: 0));
        Assert.False(libjq.jvp_array_set_index_too_large(nativeMaximumAtOffsetZero, arrayOffset: 0));
        Assert.True(libjq.jvp_array_set_index_too_large(
            (double)nativeMaximumAtOffsetZero + 1,
            arrayOffset: 0));

        const int arrayOffset = 7;
        var nativeMaximumAtOffset = nativeMaximumAtOffsetZero - arrayOffset;
        Assert.False(libjq.jvp_array_set_index_too_large(nativeMaximumAtOffset, arrayOffset));
        Assert.True(libjq.jvp_array_set_index_too_large(
            (double)nativeMaximumAtOffset + 1,
            arrayOffset));
    }

    [Fact]
    public void UniqueSetAndAppendReuseStorageWhileSharedMutationsCopy()
    {
        var unique = Numbers(0, 1, 2);
        var uniqueStorage = Storage(unique);
        unique = libjq.jv_array_set(unique, 1, libjq.jv_number(9));
        Assert.Same(uniqueStorage, Storage(unique));
        Assert.Equal("[0,9,2]", DumpBorrowed(unique));

        unique = libjq.jv_array_append(unique, libjq.jv_number(3));
        Assert.Same(uniqueStorage, Storage(unique));
        Assert.Equal("[0,9,2,3]", DumpBorrowed(unique));
        libjq.jv_free(unique);

        var setValue = Numbers(0, 1, 2);
        var setAlias = libjq.jv_copy(setValue);
        var sharedSetStorage = Storage(setValue);
        Assert.Equal(2, libjq.jv_get_refcnt(setValue));

        setValue = libjq.jv_array_set(setValue, 1, libjq.jv_number(9));
        Assert.NotSame(sharedSetStorage, Storage(setValue));
        Assert.Same(sharedSetStorage, Storage(setAlias));
        Assert.Equal(1, libjq.jv_get_refcnt(setValue));
        Assert.Equal(1, libjq.jv_get_refcnt(setAlias));
        Assert.Equal("[0,9,2]", DumpBorrowed(setValue));
        Assert.Equal("[0,1,2]", DumpBorrowed(setAlias));
        libjq.jv_free(setValue);
        libjq.jv_free(setAlias);

        var appendValue = Numbers(0, 1, 2);
        var appendAlias = libjq.jv_copy(appendValue);
        var sharedAppendStorage = Storage(appendValue);
        appendValue = libjq.jv_array_append(appendValue, libjq.jv_number(3));

        Assert.NotSame(sharedAppendStorage, Storage(appendValue));
        Assert.Same(sharedAppendStorage, Storage(appendAlias));
        Assert.Equal(6, Storage(appendValue).AllocLength);
        Assert.Equal(1, libjq.jv_get_refcnt(appendValue));
        Assert.Equal(1, libjq.jv_get_refcnt(appendAlias));
        Assert.Equal("[0,1,2,3]", DumpBorrowed(appendValue));
        Assert.Equal("[0,1,2]", DumpBorrowed(appendAlias));
        libjq.jv_free(appendValue);
        libjq.jv_free(appendAlias);
    }

    [Fact]
    public void SelfAppendAndNestedCopyOnWriteMatchUpstreamJvTest()
    {
        // This follows jq-1.8.2 src/jq_test.c:jv_test() closely. In particular,
        // appending jv_copy(a) makes the old allocation an element of the new a.
        var a = libjq.jv_array();
        Assert.Equal(jv_kind.JV_KIND_ARRAY, libjq.jv_get_kind(a));
        Assert.Equal(0, LengthBorrowed(a));
        Assert.Equal(0, LengthBorrowed(a));

        a = libjq.jv_array_append(a, libjq.jv_number(42));
        Assert.Equal(1, LengthBorrowed(a));
        AssertNumber(a, 0, 42);

        var a2 = libjq.jv_array_append(libjq.jv_array(), libjq.jv_number(42));
        Assert.True(EqualBorrowed(a, a));
        Assert.True(EqualBorrowed(a2, a2));
        Assert.True(EqualBorrowed(a, a2));
        Assert.True(EqualBorrowed(a2, a));
        libjq.jv_free(a2);

        a2 = libjq.jv_array_append(libjq.jv_array(), libjq.jv_number(19));
        Assert.False(EqualBorrowed(a, a2));
        Assert.False(EqualBorrowed(a2, a));
        libjq.jv_free(a2);

        Assert.Equal(1, libjq.jv_get_refcnt(a));
        var preSelfAppendStorage = Storage(a);
        a = libjq.jv_array_append(a, libjq.jv_copy(a));
        Assert.NotSame(preSelfAppendStorage, Storage(a));
        Assert.Equal(1, libjq.jv_get_refcnt(a));
        Assert.Equal(2, LengthBorrowed(a));
        AssertNumber(a, 0, 42);

        for (var iteration = 0; iteration < 10; iteration++)
        {
            var repeated = libjq.jv_array_get(libjq.jv_copy(a), 1);
            Assert.Equal(jv_kind.JV_KIND_ARRAY, libjq.jv_get_kind(repeated));
            Assert.Equal(1, LengthBorrowed(repeated));
            AssertNumber(repeated, 0, 42);
            libjq.jv_free(repeated);
        }

        var subarray = libjq.jv_array_get(libjq.jv_copy(a), 1);
        Assert.Equal(2, libjq.jv_get_refcnt(subarray));
        Assert.Equal(1, LengthBorrowed(subarray));
        AssertNumber(subarray, 0, 42);

        var sub2 = libjq.jv_copy(subarray);
        var sharedSubarrayStorage = Storage(subarray);
        Assert.Equal(3, libjq.jv_get_refcnt(subarray));
        sub2 = libjq.jv_array_append(sub2, libjq.jv_number(19));

        Assert.NotSame(sharedSubarrayStorage, Storage(sub2));
        Assert.Equal(2, libjq.jv_get_refcnt(subarray));
        Assert.Equal(1, libjq.jv_get_refcnt(sub2));
        Assert.Equal("[42,19]", DumpBorrowed(sub2));
        Assert.Equal("[42]", DumpBorrowed(subarray));

        libjq.jv_free(subarray);
        var before = Storage(sub2);
        sub2 = libjq.jv_array_append(sub2, libjq.jv_number(200));
        Assert.Same(before, Storage(sub2));
        Assert.Equal("[42,19,200]", DumpBorrowed(sub2));
        libjq.jv_free(sub2);

        var a3 = libjq.jv_array_append(libjq.jv_copy(a), libjq.jv_number(19));
        Assert.Equal(3, LengthBorrowed(a3));
        AssertNumber(a3, 0, 42);
        var nested = libjq.jv_array_get(libjq.jv_copy(a3), 1);
        Assert.Equal(1, LengthBorrowed(nested));
        libjq.jv_free(nested);
        AssertNumber(a3, 2, 19);
        libjq.jv_free(a3);

        Assert.Equal(2, LengthBorrowed(a));
        AssertNumber(a, 0, 42);
        nested = libjq.jv_array_get(libjq.jv_copy(a), 1);
        Assert.Equal(1, LengthBorrowed(nested));
        libjq.jv_free(nested);
        libjq.jv_free(a);
    }

    [Fact]
    public void SharedAndUniquelyOwnedSlicesPreserveOffsetsAndCopyOnWrite()
    {
        var original = Numbers(0, 1, 2, 3);
        var originalStorage = Storage(original);
        var sharedSlice = libjq.jv_array_slice(libjq.jv_copy(original), 1, 3);

        Assert.Same(originalStorage, Storage(sharedSlice));
        Assert.Equal(2, libjq.jv_get_refcnt(original));
        Assert.Equal(2, libjq.jv_get_refcnt(sharedSlice));
        Assert.False(IdenticalBorrowed(original, sharedSlice));
        Assert.Equal("[1,2]", DumpBorrowed(sharedSlice));

        sharedSlice = libjq.jv_array_set(sharedSlice, 0, libjq.jv_number(9));
        Assert.NotSame(originalStorage, Storage(sharedSlice));
        Assert.Equal(1, libjq.jv_get_refcnt(original));
        Assert.Equal(1, libjq.jv_get_refcnt(sharedSlice));
        Assert.Equal("[0,1,2,3]", DumpBorrowed(original));
        Assert.Equal("[9,2]", DumpBorrowed(sharedSlice));
        libjq.jv_free(sharedSlice);
        libjq.jv_free(original);

        var appendOriginal = Numbers(0, 1, 2, 3);
        var appendStorage = Storage(appendOriginal);
        var appendSlice = libjq.jv_array_slice(libjq.jv_copy(appendOriginal), 1, 3);
        appendSlice = libjq.jv_array_append(appendSlice, libjq.jv_number(8));
        Assert.NotSame(appendStorage, Storage(appendSlice));
        Assert.Equal("[1,2,8]", DumpBorrowed(appendSlice));
        Assert.Equal("[0,1,2,3]", DumpBorrowed(appendOriginal));
        libjq.jv_free(appendSlice);
        libjq.jv_free(appendOriginal);

        var movedOriginal = Numbers(0, 1, 2, 3);
        var movedStorage = Storage(movedOriginal);
        var uniqueSlice = libjq.jv_array_slice(movedOriginal, 1, 3);
        Assert.Same(movedStorage, Storage(uniqueSlice));
        Assert.Equal(1, libjq.jv_get_refcnt(uniqueSlice));

        uniqueSlice = libjq.jv_array_set(uniqueSlice, 0, libjq.jv_number(9));
        Assert.Same(movedStorage, Storage(uniqueSlice));
        uniqueSlice = libjq.jv_array_append(uniqueSlice, libjq.jv_number(8));
        Assert.Same(movedStorage, Storage(uniqueSlice));
        Assert.Equal("[9,2,8]", DumpBorrowed(uniqueSlice));
        libjq.jv_free(uniqueSlice);

        var emptySource = Numbers(0, 1, 2);
        var retainedSource = libjq.jv_copy(emptySource);
        var empty = libjq.jv_array_slice(emptySource, 2, 2);
        Assert.NotSame(Storage(retainedSource), Storage(empty));
        Assert.Equal(1, libjq.jv_get_refcnt(retainedSource));
        Assert.Equal(1, libjq.jv_get_refcnt(empty));
        Assert.Equal("[]", DumpBorrowed(empty));
        libjq.jv_free(empty);
        libjq.jv_free(retainedSource);
    }

    [Fact]
    public void SetFillsGapsAndFailurePathsReleaseConsumedOwners()
    {
        var sparse = libjq.jv_array();
        var sparseStorage = Storage(sparse);
        sparse = libjq.jv_array_set(sparse, 3, libjq.jv_string("x"));
        Assert.Same(sparseStorage, Storage(sparse));
        Assert.Equal("[null,null,null,\"x\"]", DumpBorrowed(sparse));
        libjq.jv_free(sparse);

        var replacedLeaf = Numbers(1);
        var retainedLeaf = libjq.jv_copy(replacedLeaf);
        var replacementArray = libjq.jv_array_append(libjq.jv_array(), replacedLeaf);
        Assert.Equal(2, libjq.jv_get_refcnt(retainedLeaf));
        replacementArray = libjq.jv_array_set(replacementArray, 0, libjq.jv_number(7));
        Assert.Equal(1, libjq.jv_get_refcnt(retainedLeaf));
        Assert.Equal("[7]", DumpBorrowed(replacementArray));
        libjq.jv_free(replacementArray);
        libjq.jv_free(retainedLeaf);

        var negativeArray = Numbers(0);
        var retainedNegativeArray = libjq.jv_copy(negativeArray);
        var negativeValue = Numbers(2);
        var retainedNegativeValue = libjq.jv_copy(negativeValue);
        var negative = libjq.jv_array_set(negativeArray, -2, negativeValue);

        Assert.Equal(1, libjq.jv_get_refcnt(retainedNegativeArray));
        Assert.Equal(1, libjq.jv_get_refcnt(retainedNegativeValue));
        AssertInvalidMessage(negative, "Out of bounds negative array index");
        libjq.jv_free(retainedNegativeArray);
        libjq.jv_free(retainedNegativeValue);

        var hugeArray = Numbers(0);
        var retainedHugeArray = libjq.jv_copy(hugeArray);
        var hugeValue = Numbers(3);
        var retainedHugeValue = libjq.jv_copy(hugeValue);
        var huge = libjq.jv_array_set(hugeArray, 536_870_912, hugeValue);

        Assert.Equal(1, libjq.jv_get_refcnt(retainedHugeArray));
        Assert.Equal(1, libjq.jv_get_refcnt(retainedHugeValue));
        AssertInvalidMessage(huge, "Array index too large");
        libjq.jv_free(retainedHugeArray);
        libjq.jv_free(retainedHugeValue);
    }

    [Fact]
    public void ConcatCopiesSharedLeftOnceAndConsumesBothInputOwners()
    {
        var left = Numbers(0, 1);
        var retainedLeft = libjq.jv_copy(left);
        var leftStorage = Storage(left);
        var right = Numbers(2, 3);
        var retainedRight = libjq.jv_copy(right);

        var result = libjq.jv_array_concat(left, right);

        Assert.NotSame(leftStorage, Storage(result));
        Assert.Equal(4, Storage(result).AllocLength);
        Assert.Equal(1, libjq.jv_get_refcnt(result));
        Assert.Equal(1, libjq.jv_get_refcnt(retainedLeft));
        Assert.Equal(1, libjq.jv_get_refcnt(retainedRight));
        Assert.Equal("[0,1,2,3]", DumpBorrowed(result));
        Assert.Equal("[0,1]", DumpBorrowed(retainedLeft));
        Assert.Equal("[2,3]", DumpBorrowed(retainedRight));

        libjq.jv_free(result);
        libjq.jv_free(retainedLeft);
        libjq.jv_free(retainedRight);
    }

    [Fact]
    public void FreeReleasesVeryDeepArraysIteratively()
    {
        const int depth = 20_000;
        var leaf = Numbers(1);
        var retainedLeaf = libjq.jv_copy(leaf);
        var root = leaf;

        for (var index = 0; index < depth; index++)
        {
            root = libjq.jv_array_append(libjq.jv_array(), root);
        }

        Assert.Equal(2, libjq.jv_get_refcnt(retainedLeaf));
        var secondRootOwner = libjq.jv_copy(root);
        Assert.Equal(2, libjq.jv_get_refcnt(root));

        libjq.jv_free(root);
        Assert.Equal(1, libjq.jv_get_refcnt(secondRootOwner));
        Assert.Equal(2, libjq.jv_get_refcnt(retainedLeaf));

        libjq.jv_free(secondRootOwner);
        Assert.Equal(1, libjq.jv_get_refcnt(retainedLeaf));
        libjq.jv_free(retainedLeaf);
    }

    [Fact]
    public void FreeReleasesEveryOwnedEdgeInASharedChildDag()
    {
        const int edgeCount = 1_024;
        var child = libjq.jv_array_append(libjq.jv_array(), libjq.jv_string("child"));
        var retainedChild = libjq.jv_copy(child);
        var parent = libjq.jv_array();

        for (var index = 0; index < edgeCount; index++)
        {
            parent = libjq.jv_array_append(parent, libjq.jv_copy(child));
        }

        Assert.Equal(edgeCount + 2, libjq.jv_get_refcnt(child));
        libjq.jv_free(child);
        Assert.Equal(edgeCount + 1, libjq.jv_get_refcnt(retainedChild));

        libjq.jv_free(parent);
        Assert.Equal(1, libjq.jv_get_refcnt(retainedChild));
        libjq.jv_free(retainedChild);
    }

    private static jv Numbers(params double[] values)
    {
        var result = libjq.jv_array();
        foreach (var value in values)
        {
            result = libjq.jv_array_append(result, libjq.jv_number(value));
        }

        return result;
    }

    private static jvp_array Storage(jv value)
    {
        Assert.Equal(jv_kind.JV_KIND_ARRAY, libjq.jv_get_kind(value));
        return Assert.IsType<jvp_array>(value.Value);
    }

    private static int LengthBorrowed(jv value)
    {
        var owners = libjq.jv_get_refcnt(value);
        var result = libjq.jv_array_length(libjq.jv_copy(value));
        Assert.Equal(owners, libjq.jv_get_refcnt(value));
        return result;
    }

    private static string DumpBorrowed(jv value)
    {
        var owners = libjq.jv_get_refcnt(value);
        var result = libjq.jv_dump_string_borrowed(value);
        Assert.Equal(owners, libjq.jv_get_refcnt(value));
        return result;
    }

    private static bool EqualBorrowed(jv left, jv right)
    {
        var leftOwners = libjq.jv_get_refcnt(left);
        var rightOwners = libjq.jv_get_refcnt(right);
        var result = libjq.jv_equal(libjq.jv_copy(left), libjq.jv_copy(right));
        Assert.Equal(leftOwners, libjq.jv_get_refcnt(left));
        Assert.Equal(rightOwners, libjq.jv_get_refcnt(right));
        return result;
    }

    private static bool IdenticalBorrowed(jv left, jv right)
    {
        var leftOwners = libjq.jv_get_refcnt(left);
        var rightOwners = libjq.jv_get_refcnt(right);
        var result = libjq.jv_identical(libjq.jv_copy(left), libjq.jv_copy(right));
        Assert.Equal(leftOwners, libjq.jv_get_refcnt(left));
        Assert.Equal(rightOwners, libjq.jv_get_refcnt(right));
        return result;
    }

    private static void AssertNumber(jv array, int index, double expected)
    {
        var value = libjq.jv_array_get(libjq.jv_copy(array), index);
        Assert.Equal(jv_kind.JV_KIND_NUMBER, libjq.jv_get_kind(value));
        Assert.Equal(expected, libjq.jv_number_value(value));
        libjq.jv_free(value);
    }

    private static void AssertInvalidMessage(jv invalid, string expected)
    {
        Assert.False(libjq.jv_is_valid(invalid));
        Assert.True(libjq.jv_invalid_has_msg(libjq.jv_copy(invalid)));
        var message = libjq.jv_invalid_get_msg(invalid);
        Assert.Equal(expected, message.StringValue);
        libjq.jv_free(message);
    }
}
