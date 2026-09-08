using System.Reflection;
using DotNetJq.Port;

namespace DotNetJq.Tests;

public sealed class JvLiteralNumberRefcountCompatibilityTests
{
    [Theory]
    [InlineData("1e+2", "1E+2")]
    [InlineData("1E+02", "1E+2")]
    [InlineData("001.2300", "1.2300")]
    [InlineData("-0.00", "-0.00")]
    [InlineData("1e9999", "1E+9999")]
    public void LiteralTextUsesDecNumberCanonicalRendering(
        string source,
        string expected)
    {
        var value = libjq.jv_number_with_literal(source);
        try
        {
            Assert.Equal(expected, libjq.jv_number_get_literal(value));
        }
        finally
        {
            libjq.jv_free(value);
        }
    }

    [Fact]
    public void PrintingFiniteLiteralDoesNotPopulateItsLazyBinary64Projection()
    {
        const string literalText = "7.056371102815960319e-23";
        var valueField = typeof(JvNumber).GetField(
            "value",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(valueField);
        var value = libjq.jv_number_with_literal(literalText);
        try
        {
            var storage = Assert.IsType<JvNumber>(value.Value);
            Assert.True(double.IsNaN(Assert.IsType<double>(valueField.GetValue(storage))));

            Assert.Equal(
                "7.056371102815960319E-23",
                libjq.jv_dump_string_borrowed(value));

            Assert.True(double.IsNaN(Assert.IsType<double>(valueField.GetValue(storage))));
            Assert.Equal(
                0x3B55_539A_4A8E_84A3UL,
                BitConverter.DoubleToUInt64Bits(libjq.jv_number_value(value)));
            Assert.False(double.IsNaN(Assert.IsType<double>(valueField.GetValue(storage))));
        }
        finally
        {
            libjq.jv_free(value);
        }
    }

    [Theory]
    [InlineData("7.056371102815960319e-23", 0x3B55_539A_4A8E_84A3UL)]
    [InlineData("5.085945752143224401740376975684e16", 0x4366_960D_0761_16D6UL)]
    [InlineData("9.428828476561485067e53", 0x4B23_B035_3F9D_5C83UL)]
    public void LiteralProjectionUsesSeventeenDigitReductionBeforeStrtod(
        string literal,
        ulong expectedBits)
    {
        var value = libjq.jv_number_with_literal(literal);
        try
        {
            Assert.Equal(
                expectedBits,
                BitConverter.DoubleToUInt64Bits(libjq.jv_number_value(value)));
        }
        finally
        {
            libjq.jv_free(value);
        }
    }

    [Theory]
    [InlineData("1e1000000000", true, "1.7976931348623157e+308")]
    [InlineData("-1e1000000000", false, "-1.7976931348623157e+308")]
    public void DecimalInfinityHasNoLiteralTextAndUsesClampedBinary64Fallback(
        string literal,
        bool positive,
        string expected)
    {
        var value = libjq.jv_number_with_literal(literal);
        try
        {
            Assert.True(libjq.jv_number_has_literal(value));
            Assert.Null(libjq.jv_number_get_literal(value));
            var projection = libjq.jv_number_value(value);
            Assert.Equal(positive, double.IsPositiveInfinity(projection));
            Assert.Equal(!positive, double.IsNegativeInfinity(projection));
            Assert.Equal(expected, libjq.jv_dump_string_borrowed(value));
        }
        finally
        {
            libjq.jv_free(value);
        }
    }

    [Fact]
    public void LiteralNumberCopyAndFreeTrackTheAllocatedPayload()
    {
        var literal = libjq.jv_number_with_literal("1.0000000000000001");

        Assert.True(libjq.jv_number_has_literal(literal));
        Assert.Equal(1, libjq.jv_get_refcnt(literal));

        var copy = libjq.jv_copy(literal);
        Assert.Same(literal.Value, copy.Value);
        Assert.Equal(2, libjq.jv_get_refcnt(literal));
        Assert.Equal(2, libjq.jv_get_refcnt(copy));

        libjq.jv_free(copy);
        Assert.Equal(1, libjq.jv_get_refcnt(literal));
        Assert.Equal("1.0000000000000001", libjq.jv_number_get_literal(literal));

        libjq.jv_free(literal);
        Assert.Throws<ObjectDisposedException>(() => libjq.jv_copy(literal));
        Assert.Throws<ObjectDisposedException>(() => libjq.jv_number_value(literal));
        Assert.Throws<ObjectDisposedException>(() => libjq.jv_get_refcnt(literal));
        Assert.Throws<ObjectDisposedException>(() => libjq.jv_free(literal));
    }

    [Fact]
    public void NativeBinary64NumbersRemainUnallocatedScalars()
    {
        var number = libjq.jv_number(-0d);
        var copy = libjq.jv_copy(number);

        Assert.False(libjq.jv_number_has_literal(number));
        Assert.Equal(1, libjq.jv_get_refcnt(number));
        Assert.Equal(1, libjq.jv_get_refcnt(copy));

        libjq.jv_free(copy);
        libjq.jv_free(number);

        Assert.Equal(1, libjq.jv_get_refcnt(number));
        Assert.True(BitConverter.DoubleToInt64Bits(libjq.jv_number_value(number)) < 0);
    }

    [Fact]
    public void NumericOrderingPreservesJqsAsymmetricNanRule()
    {
        Assert.True(libjq.jv_cmp(
            libjq.jv_number(double.NaN),
            libjq.jv_number(double.NaN)) < 0);
        Assert.True(libjq.jv_cmp(
            libjq.jv_number(double.NaN),
            libjq.jv_number(0)) < 0);
        Assert.True(libjq.jv_cmp(
            libjq.jv_number(0),
            libjq.jv_number(double.NaN)) > 0);
    }

    [Fact]
    public void ArrayAndObjectSlotsOwnLiteralNumbersAcrossOuterCowCopies()
    {
        var literal = libjq.jv_number_with_literal("9007199254740993");
        var array = libjq.jv_array([libjq.jv_copy(literal)]);
        var value = libjq.jv_object_set(
            libjq.jv_object(),
            "n",
            libjq.jv_copy(literal));

        Assert.Equal(3, libjq.jv_get_refcnt(literal));

        var arrayCopy = libjq.jv_copy(array);
        var objectCopy = libjq.jv_copy(value);

        // Copying a container increments only its outer allocation. Unsharing
        // it copies every live child slot, exactly as jvp_array_write() and
        // jvp_object_unshare() do in jq-1.8.2 src/jv.c.
        Assert.Equal(3, libjq.jv_get_refcnt(literal));
        arrayCopy = libjq.jv_array_append(arrayCopy, libjq.jv_null());
        Assert.Equal(4, libjq.jv_get_refcnt(literal));
        objectCopy = libjq.jv_object_set(objectCopy, "other", libjq.jv_null());
        Assert.Equal(5, libjq.jv_get_refcnt(literal));

        libjq.jv_free(arrayCopy);
        Assert.Equal(4, libjq.jv_get_refcnt(literal));
        libjq.jv_free(objectCopy);
        Assert.Equal(3, libjq.jv_get_refcnt(literal));
        libjq.jv_free(array);
        Assert.Equal(2, libjq.jv_get_refcnt(literal));
        libjq.jv_free(value);
        Assert.Equal(1, libjq.jv_get_refcnt(literal));
        libjq.jv_free(literal);
    }

    [Fact]
    public void LiteralAbsAndNegateBorrowSourceAndAllocateIndependentResults()
    {
        var source = libjq.jv_number_with_literal("-12.50");
        var absolute = libjq.jv_number_abs(source);
        var negated = libjq.jv_number_negate(source);

        Assert.Equal(1, libjq.jv_get_refcnt(source));
        Assert.Equal(1, libjq.jv_get_refcnt(absolute));
        Assert.Equal(1, libjq.jv_get_refcnt(negated));
        Assert.NotSame(source.Value, absolute.Value);
        Assert.NotSame(source.Value, negated.Value);
        Assert.NotSame(absolute.Value, negated.Value);
        Assert.Equal("-12.50", libjq.jv_number_get_literal(source));
        Assert.Equal("12.50", libjq.jv_number_get_literal(absolute));
        Assert.Equal("12.50", libjq.jv_number_get_literal(negated));

        var negatedCopy = libjq.jv_copy(negated);
        Assert.Equal(1, libjq.jv_get_refcnt(source));
        Assert.Equal(1, libjq.jv_get_refcnt(absolute));
        Assert.Equal(2, libjq.jv_get_refcnt(negated));

        libjq.jv_free(negatedCopy);
        libjq.jv_free(negated);
        libjq.jv_free(absolute);
        libjq.jv_free(source);
    }

    [Fact]
    public void NativeAbsAndNegateResultsAlsoRemainUnallocated()
    {
        var source = libjq.jv_number(-4.5);
        var absolute = libjq.jv_number_abs(source);
        var negated = libjq.jv_number_negate(source);

        Assert.Equal(1, libjq.jv_get_refcnt(source));
        Assert.Equal(1, libjq.jv_get_refcnt(libjq.jv_copy(absolute)));
        Assert.Equal(1, libjq.jv_get_refcnt(libjq.jv_copy(negated)));
        Assert.False(libjq.jv_number_has_literal(absolute));
        Assert.False(libjq.jv_number_has_literal(negated));
        Assert.Equal(4.5, libjq.jv_number_value(absolute));
        Assert.Equal(4.5, libjq.jv_number_value(negated));

        libjq.jv_free(negated);
        libjq.jv_free(absolute);
        libjq.jv_free(source);
    }
}
