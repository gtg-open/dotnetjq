using DotNetJq.Port;

namespace DotNetJq.Tests;

public sealed class NumericCompatibilityTests
{
    [Fact]
    public void JqContextMatchesUpstreamPrecisionAndExponentRange()
    {
        var context = libjq.CreateJqDecimalContext();

        Assert.Equal(147_483_648, context.digits);
        Assert.Equal(999_999_999, context.emax);
        Assert.Equal(-999_999_999, context.emin);
        Assert.Equal(rounding.DEC_ROUND_HALF_UP, context.round);
        Assert.Equal(0u, context.traps);
    }

    [Theory]
    [InlineData("1", "1")]
    [InlineData("1.000", "1.000")]
    [InlineData("100e-2", "1.00")]
    [InlineData("0.0001e4", "1")]
    [InlineData("1e20", "1E+20")]
    [InlineData("1e-6", "0.000001")]
    [InlineData("1e-7", "1E-7")]
    [InlineData("1.2300e3", "1230.0")]
    [InlineData("1.2300e-3", "0.0012300")]
    [InlineData("-0.0", "-0.0")]
    [InlineData("9E999999999", "9E+999999999")]
    [InlineData("9999999999E999999990", "9.999999999E+999999999")]
    [InlineData("0.000000001E-999999990", "1E-999999999")]
    public void LiteralFormattingMatchesDecNumber(string literal, string expected)
    {
        var number = Parse(literal);

        Assert.Equal(expected, libjq.decNumberToString(number));
    }

    [Fact]
    public void ParsedLiteralsCompareAtDecimalPrecision()
    {
        var context = libjq.CreateJqDecimalContext();
        var larger = Parse("13911860366432393", context);
        var smaller = Parse("13911860366432392", context);
        var comparison = libjq.decNumberCompare(new decNumber(), larger, smaller, context);

        Assert.Equal("1", libjq.decNumberToString(comparison));
        Assert.NotEqual(larger.Value, smaller.Value);
    }

    [Fact]
    public void UnaryOperationsPreserveLiteralPrecisionAndQuantum()
    {
        var context = libjq.CreateJqDecimalContext();
        var source = Parse("-0.12345678901234567890123456789", context);
        var negated = libjq.decNumberMinus(new decNumber(), source, context);
        var absolute = libjq.decNumberAbs(new decNumber(), source, context);

        Assert.Equal("0.12345678901234567890123456789", libjq.decNumberToString(negated));
        Assert.Equal("0.12345678901234567890123456789", libjq.decNumberToString(absolute));
    }

    [Fact]
    public void ConversionToBinary64MatchesJqArithmeticBoundary()
    {
        var source = Parse("13911860366432393");

        Assert.Equal(13_911_860_366_432_392d, source.Value.ToDouble());
    }

    [Fact]
    public void JqExponentBoundaryProducesInfinityAndEtinyZero()
    {
        var context = libjq.CreateJqDecimalContext();
        var overflow = Parse("1e1000000000", context);
        var underflow = Parse("1e-1999999998", context);

        Assert.True(libjq.decNumberIsInfinite(overflow));
        Assert.Equal("0E-1147483646", libjq.decNumberToString(underflow));
        Assert.NotEqual(0u, context.status & libjq.DEC_Overflow);
        Assert.NotEqual(0u, context.status & libjq.DEC_Underflow);
    }

    [Fact]
    public void Decimal64ReductionUsesHalfEvenAndSeventeenDigitDoubleBridge()
    {
        var context = libjq.decContextDefault(new decContext(), libjq.DEC_INIT_DECIMAL64);
        context.digits = 17;
        var source = Parse("1.23456789012345675", libjq.CreateJqDecimalContext());
        var reduced = libjq.decNumberReduce(new decNumber(), source, context);

        Assert.Equal("1.2345678901234568", libjq.decNumberToString(reduced));
        Assert.NotEqual(0u, context.status & libjq.DEC_Inexact);
    }

    [Theory]
    [InlineData("0.00", (int)rounding.DEC_ROUND_HALF_UP, "0.00")]
    [InlineData("-0.00", (int)rounding.DEC_ROUND_HALF_UP, "0.00")]
    [InlineData("0.00", (int)rounding.DEC_ROUND_FLOOR, "-0.00")]
    [InlineData("-0.00", (int)rounding.DEC_ROUND_FLOOR, "0.00")]
    public void DecNumberMinusUsesPinnedExactZeroSignRule(
        string literal,
        int mode,
        string expected)
    {
        var context = libjq.CreateJqDecimalContext();
        libjq.decContextSetRounding(context, (rounding)mode);
        var source = Parse(literal, context);

        var result = libjq.decNumberMinus(new decNumber(), source, context);

        Assert.Equal(expected, libjq.decNumberToString(result));
    }

    [Fact]
    public void BasicManagedArithmeticHonorsContextRounding()
    {
        var context = libjq.decContextDefault(new decContext(), libjq.DEC_INIT_DECIMAL32);
        var left = Parse("1.234567", context);
        var right = Parse("0.0000009", context);
        libjq.decContextZeroStatus(context);

        var sum = libjq.decNumberAdd(new decNumber(), left, right, context);

        Assert.Equal("1.234568", libjq.decNumberToString(sum));
        Assert.NotEqual(0u, context.status & libjq.DEC_Rounded);
    }

    [Fact]
    public void PrecisionRoundingNormalizesACarry()
    {
        var context = libjq.decContextDefault(new decContext(), libjq.DEC_INIT_DECIMAL32);
        context.digits = 3;

        var value = Parse("9999", context);

        Assert.Equal("1.00E+4", libjq.decNumberToString(value));
        Assert.Equal(3, value.digits);
    }

    [Fact]
    public void InvalidSyntaxSetsConversionStatus()
    {
        var context = libjq.CreateJqDecimalContext();
        var number = libjq.decNumberFromString(new decNumber(), "1e", context);

        Assert.True(libjq.decNumberIsNaN(number));
        Assert.NotEqual(0u, context.status & libjq.DEC_Conversion_syntax);
    }

    private static decNumber Parse(string literal, decContext? context = null)
    {
        context ??= libjq.CreateJqDecimalContext();
        var result = libjq.decNumberFromString(new decNumber(), literal, context);
        Assert.False(libjq.decNumberIsNaN(result));
        return result;
    }
}
