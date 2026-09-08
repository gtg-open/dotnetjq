// DOTNETJQ PORT TEST MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream files: src/jv.c, src/jv.h, src/jq_test.c
// Primary upstream regression: src/jq_test.c jv_test() object block, lines 744-762.
//
// These tests deliberately use jq's ownership vocabulary. A consuming object API receives
// its owned handle directly; a retained observation is expressed by jv_copy(), and every
// retained allocated handle is released with jv_free(). Value identity stands in for the
// native jq_test.c u.ptr comparisons.

using DotNetJq.Port;

namespace DotNetJq.Tests;

public sealed class JvObjectRefcountCowCompatibilityTests
{
    [Fact]
    public void UpstreamObjectCopyMutationRegressionKeepsTheOriginalUnchanged()
    {
        // Direct port of jq-1.8.2 src/jq_test.c:jv_test(), lines 744-762.
        var original = libjq.jv_object();
        var changed = libjq.jv_invalid();
        try
        {
            original = libjq.jv_object_set(
                original,
                libjq.jv_string("foo"),
                libjq.jv_number(42));
            original = libjq.jv_object_set(
                original,
                libjq.jv_string("bar"),
                libjq.jv_number(24));

            Assert.Equal(42, GetNumberBorrowed(original, "foo"));
            Assert.Equal(24, GetNumberBorrowed(original, "bar"));
            Assert.Equal(1, libjq.jv_get_refcnt(original));

            changed = libjq.jv_copy(original);
            Assert.Equal(2, libjq.jv_get_refcnt(original));
            changed = libjq.jv_object_set(
                changed,
                libjq.jv_string("foo"),
                libjq.jv_number(420));
            changed = libjq.jv_object_set(
                changed,
                libjq.jv_string("bar"),
                libjq.jv_number(240));

            Assert.NotSame(original.Value, changed.Value);
            Assert.Equal(1, libjq.jv_get_refcnt(original));
            Assert.Equal(1, libjq.jv_get_refcnt(changed));
            Assert.Equal(42, GetNumberBorrowed(original, "foo"));
            Assert.Equal(24, GetNumberBorrowed(original, "bar"));
            Assert.Equal(420, GetNumberBorrowed(changed, "foo"));

            libjq.jv_free(original);
            original = libjq.jv_invalid();
            Assert.Equal(240, GetNumberBorrowed(changed, "bar"));
        }
        finally
        {
            libjq.jv_free(original);
            libjq.jv_free(changed);
        }
    }

    [Fact]
    public void CopyingAnObjectDoesNotCopyItsArrayChildUntilTheObjectUnshares()
    {
        var child = Numbers(7);
        var retainedChild = libjq.jv_copy(child);
        var value = libjq.jv_object_set(
            libjq.jv_object(),
            libjq.jv_string("child"),
            child);
        var alias = libjq.jv_invalid();
        try
        {
            Assert.Equal(2, libjq.jv_get_refcnt(retainedChild));

            alias = libjq.jv_copy(value);
            Assert.Equal(2, libjq.jv_get_refcnt(value));
            Assert.Equal(2, libjq.jv_get_refcnt(retainedChild));

            value = libjq.jv_object_set(
                value,
                libjq.jv_string("marker"),
                libjq.jv_true());

            Assert.NotSame(value.Value, alias.Value);
            Assert.Equal(1, libjq.jv_get_refcnt(value));
            Assert.Equal(1, libjq.jv_get_refcnt(alias));
            Assert.Equal(3, libjq.jv_get_refcnt(retainedChild));

            libjq.jv_free(value);
            value = libjq.jv_invalid();
            Assert.Equal(2, libjq.jv_get_refcnt(retainedChild));
            libjq.jv_free(alias);
            alias = libjq.jv_invalid();
            Assert.Equal(1, libjq.jv_get_refcnt(retainedChild));
        }
        finally
        {
            libjq.jv_free(value);
            libjq.jv_free(alias);
            libjq.jv_free(retainedChild);
        }
    }

    [Fact]
    public void ObjectGetCopiesAnArrayChildBeforeConsumingTheObjectCopy()
    {
        var child = Numbers(11);
        var retainedChild = libjq.jv_copy(child);
        var value = libjq.jv_object_set(
            libjq.jv_object(),
            libjq.jv_string("child"),
            child);
        var fetched = libjq.jv_invalid();
        try
        {
            fetched = libjq.jv_object_get(
                libjq.jv_copy(value),
                libjq.jv_string("child"));

            Assert.Equal(1, libjq.jv_get_refcnt(value));
            Assert.Equal(3, libjq.jv_get_refcnt(retainedChild));
            Assert.Same(retainedChild.Value, fetched.Value);
            Assert.Equal(11, FirstNumberBorrowed(fetched));

            libjq.jv_free(fetched);
            fetched = libjq.jv_invalid();
            Assert.Equal(2, libjq.jv_get_refcnt(retainedChild));
            libjq.jv_free(value);
            value = libjq.jv_invalid();
            Assert.Equal(1, libjq.jv_get_refcnt(retainedChild));
        }
        finally
        {
            libjq.jv_free(fetched);
            libjq.jv_free(value);
            libjq.jv_free(retainedChild);
        }
    }

    [Fact]
    public void UniqueReplacementFreesThePreviousArraySlotOwner()
    {
        var previous = Numbers(1);
        var retainedPrevious = libjq.jv_copy(previous);
        var replacement = Numbers(2);
        var retainedReplacement = libjq.jv_copy(replacement);
        var value = libjq.jv_object_set(
            libjq.jv_object(),
            libjq.jv_string("child"),
            previous);
        try
        {
            var storage = value.Value;
            value = libjq.jv_object_set(
                value,
                libjq.jv_string("child"),
                replacement);

            Assert.Same(storage, value.Value);
            Assert.Equal(1, libjq.jv_get_refcnt(retainedPrevious));
            Assert.Equal(2, libjq.jv_get_refcnt(retainedReplacement));
            Assert.Equal(2, GetFirstArrayNumberBorrowed(value, "child"));

            libjq.jv_free(value);
            value = libjq.jv_invalid();
            Assert.Equal(1, libjq.jv_get_refcnt(retainedReplacement));
        }
        finally
        {
            libjq.jv_free(value);
            libjq.jv_free(retainedPrevious);
            libjq.jv_free(retainedReplacement);
        }
    }

    [Fact]
    public void DeletingAMissingKeyStillUnsharesASharedObject()
    {
        var child = Numbers(3);
        var retainedChild = libjq.jv_copy(child);
        var value = libjq.jv_object_set(
            libjq.jv_object(),
            libjq.jv_string("child"),
            child);
        var alias = libjq.jv_copy(value);
        try
        {
            value = libjq.jv_object_delete(value, libjq.jv_string("missing"));

            Assert.NotSame(value.Value, alias.Value);
            Assert.Equal(1, libjq.jv_get_refcnt(value));
            Assert.Equal(1, libjq.jv_get_refcnt(alias));
            Assert.Equal(3, libjq.jv_get_refcnt(retainedChild));
            Assert.Equal(3, GetFirstArrayNumberBorrowed(value, "child"));
            Assert.Equal(3, GetFirstArrayNumberBorrowed(alias, "child"));

            libjq.jv_free(value);
            value = libjq.jv_invalid();
            Assert.Equal(2, libjq.jv_get_refcnt(retainedChild));
            libjq.jv_free(alias);
            alias = libjq.jv_invalid();
            Assert.Equal(1, libjq.jv_get_refcnt(retainedChild));
        }
        finally
        {
            libjq.jv_free(value);
            libjq.jv_free(alias);
            libjq.jv_free(retainedChild);
        }
    }

    [Fact]
    public void IteratorValueReturnsAnotherOwnerOfAnArrayChild()
    {
        var child = Numbers(5);
        var retainedChild = libjq.jv_copy(child);
        var value = libjq.jv_object_set(
            libjq.jv_object(),
            libjq.jv_string("child"),
            child);
        var iterated = libjq.jv_invalid();
        try
        {
            var iterator = libjq.jv_object_iter(value);
            Assert.True(libjq.jv_object_iter_valid(value, iterator));
            iterated = libjq.jv_object_iter_value(value, iterator);

            Assert.Equal(3, libjq.jv_get_refcnt(retainedChild));
            Assert.Same(retainedChild.Value, iterated.Value);

            libjq.jv_free(iterated);
            iterated = libjq.jv_invalid();
            Assert.Equal(2, libjq.jv_get_refcnt(retainedChild));
            libjq.jv_free(value);
            value = libjq.jv_invalid();
            Assert.Equal(1, libjq.jv_get_refcnt(retainedChild));
        }
        finally
        {
            libjq.jv_free(iterated);
            libjq.jv_free(value);
            libjq.jv_free(retainedChild);
        }
    }

    [Fact]
    public void ObjectUsesEightSlotsAndGrowsOnlyAfterTheNinthDistinctKey()
    {
        var value = libjq.jv_object();
        try
        {
            var storage8 = value.Value;
            Assert.Equal(8, value.Size);

            for (var index = 0; index < 8; index++)
            {
                value = libjq.jv_object_set(
                    value,
                    libjq.jv_string($"k{index}"),
                    libjq.jv_number(index));
                Assert.Same(storage8, value.Value);
                Assert.Equal(8, value.Size);
            }

            value = libjq.jv_object_set(
                value,
                libjq.jv_string("k8"),
                libjq.jv_number(8));

            Assert.NotSame(storage8, value.Value);
            Assert.Equal(16, value.Size);
            Assert.Equal(9, LengthBorrowed(value));
            Assert.Equal(
                ["k0", "k1", "k2", "k3", "k4", "k5", "k6", "k7", "k8"],
                KeysBorrowed(value));
        }
        finally
        {
            libjq.jv_free(value);
        }
    }

    [Fact]
    public void DeletedSlotsAreNotReusedAndRehashCompactsLiveSlotsInOrder()
    {
        var value = libjq.jv_object();
        try
        {
            for (var index = 0; index < 8; index++)
            {
                value = libjq.jv_object_set(
                    value,
                    libjq.jv_string($"k{index}"),
                    libjq.jv_number(index));
            }

            var storage8 = value.Value;
            value = libjq.jv_object_delete(value, libjq.jv_string("k0"));
            Assert.Same(storage8, value.Value);
            Assert.Equal(8, value.Size);
            Assert.Equal(7, LengthBorrowed(value));

            value = libjq.jv_object_set(
                value,
                libjq.jv_string("replacement"),
                libjq.jv_number(8));

            Assert.NotSame(storage8, value.Value);
            Assert.Equal(16, value.Size);
            Assert.Equal(
                ["k1", "k2", "k3", "k4", "k5", "k6", "k7", "replacement"],
                KeysBorrowed(value));
        }
        finally
        {
            libjq.jv_free(value);
        }
    }

    [Fact]
    public void ReplacingAKeyKeepsItsPhysicalIterationPosition()
    {
        var value = libjq.jv_object();
        try
        {
            value = libjq.jv_object_set(value, libjq.jv_string("a"), libjq.jv_number(1));
            value = libjq.jv_object_set(value, libjq.jv_string("b"), libjq.jv_number(2));
            var storage = value.Value;

            value = libjq.jv_object_set(value, libjq.jv_string("a"), libjq.jv_number(3));

            Assert.Same(storage, value.Value);
            Assert.Equal(["a", "b"], KeysBorrowed(value));
            Assert.Equal(3, GetNumberBorrowed(value, "a"));
        }
        finally
        {
            libjq.jv_free(value);
        }
    }

    [Fact]
    public void ObjectOwnsOneArrayChildReferencePerLiveSlot()
    {
        var child = Numbers(13);
        var retainedChild = libjq.jv_copy(child);
        var value = libjq.jv_object_set(
            libjq.jv_object(),
            libjq.jv_string("k0"),
            child);
        try
        {
            for (var index = 1; index < 8; index++)
            {
                value = libjq.jv_object_set(
                    value,
                    libjq.jv_string($"k{index}"),
                    libjq.jv_copy(retainedChild));
            }

            Assert.Equal(9, libjq.jv_get_refcnt(retainedChild));
            libjq.jv_free(value);
            value = libjq.jv_invalid();
            Assert.Equal(1, libjq.jv_get_refcnt(retainedChild));
        }
        finally
        {
            libjq.jv_free(value);
            libjq.jv_free(retainedChild);
        }
    }

    [Fact]
    public void FreeReleasesDeepObjectToArrayOwnershipIteratively()
    {
        var leaf = libjq.jv_array();
        var leafStorage = (jvp_array)leaf.Value!;
        var value = leaf;
        try
        {
            for (var depth = 0; depth < 20_000; depth++)
            {
                value = libjq.jv_object_set(
                    libjq.jv_object(),
                    libjq.jv_string("child"),
                    value);
            }

            libjq.jv_free(value);
            value = libjq.jv_invalid();
            Assert.Equal(0, leafStorage.Refcnt.Count);
        }
        finally
        {
            libjq.jv_free(value);
        }
    }

    private static jv Numbers(params double[] numbers) =>
        libjq.jv_array(numbers.Select(libjq.jv_number));

    private static int LengthBorrowed(jv value) =>
        libjq.jv_object_length(libjq.jv_copy(value));

    private static double GetNumberBorrowed(jv value, string key)
    {
        var selected = libjq.jv_object_get(
            libjq.jv_copy(value),
            libjq.jv_string(key));
        try
        {
            return libjq.jv_number_value(selected);
        }
        finally
        {
            libjq.jv_free(selected);
        }
    }

    private static jv GetArrayBorrowed(jv value, string key) =>
        libjq.jv_object_get(libjq.jv_copy(value), libjq.jv_string(key));

    private static double GetFirstArrayNumberBorrowed(jv value, string key)
    {
        var selected = GetArrayBorrowed(value, key);
        try
        {
            return FirstNumberBorrowed(selected);
        }
        finally
        {
            libjq.jv_free(selected);
        }
    }

    private static double FirstNumberBorrowed(jv value)
    {
        var selected = libjq.jv_array_get(libjq.jv_copy(value), 0);
        try
        {
            return libjq.jv_number_value(selected);
        }
        finally
        {
            libjq.jv_free(selected);
        }
    }

    private static string[] KeysBorrowed(jv value)
    {
        var keys = new List<string>();
        for (var iterator = libjq.jv_object_iter(value);
             libjq.jv_object_iter_valid(value, iterator);
             iterator = libjq.jv_object_iter_next(value, iterator))
        {
            var key = libjq.jv_object_iter_key(value, iterator);
            try
            {
                keys.Add(key.StringValue);
            }
            finally
            {
                libjq.jv_free(key);
            }
        }

        return keys.ToArray();
    }
}
