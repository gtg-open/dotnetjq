using DotNetJq.Port;

namespace DotNetJq.Tests;

public sealed class NumericCompatibilityRound2Tests
{
    [Theory]
    [InlineData("9E999999999", "9E+999999999")]
    [InlineData("9999999999E999999990", "9.999999999E+999999999")]
    [InlineData("1E-999999999", "1E-999999999")]
    [InlineData("0.000000001E-999999990", "1E-999999999")]
    public void ExactLiteralFormattingPrecedesTheBinary64Cache(string literal, string expected)
    {
        var number = Number(literal);
        Assert.Equal(expected, libjq.jv_dump_string(number));
    }

    [Theory]
    [InlineData(double.PositiveInfinity, "1.7976931348623157e+308")]
    [InlineData(double.NegativeInfinity, "-1.7976931348623157e+308")]
    [InlineData(double.NaN, "null")]
    [InlineData(0d, "0")]
    [InlineData(-0d, "-0")]
    public void NativeSpecialNumbersUseJqJsonFormatting(double value, string expected)
    {
        Assert.Equal(expected, libjq.jv_dump_string(libjq.jv_number(value)));
    }

    [Theory]
    [InlineData("1e1000000000", "1.7976931348623157e+308")]
    [InlineData("-1e1000000000", "-1.7976931348623157e+308")]
    [InlineData("1e-1999999998", "0E-1147483646")]
    [InlineData("-1e-1999999998", "-0E-1147483646")]
    public void ExponentRangeFallbackMatchesJq(string literal, string expected)
    {
        Assert.Equal(expected, libjq.jv_dump_string(Number(literal)));
    }

    [Theory]
    [InlineData("0", "0")]
    [InlineData("-0", "0")]
    [InlineData("0.0", "0.0")]
    [InlineData("-0.0", "0.0")]
    [InlineData("0e-999999999", "0E-999999999")]
    [InlineData("-0e-999999999", "0E-999999999")]
    public void ExactUnaryMinusNormalizesZeroSignAndPreservesQuantum(
        string literal,
        string expected)
    {
        var source = Number(literal);
        var result = libjq.jv_number_negate(source);
        try
        {
            Assert.Equal(expected, libjq.jv_dump_string_borrowed(result));
            var number = Assert.IsType<JvNumber>(result.Value);
            Assert.False(number.ExactValue!.Value.IsNegative);
        }
        finally
        {
            libjq.jv_free(result);
            libjq.jv_free(source);
        }
    }

    [Fact]
    public void NativeUnaryMinusPreservesNegativeZero()
    {
        var source = libjq.jv_number(0d);
        var result = libjq.jv_number_negate(source);
        try
        {
            Assert.Equal("-0", libjq.jv_dump_string_borrowed(result));
            Assert.True(BitConverter.DoubleToInt64Bits(libjq.jv_number_value(result)) < 0);
        }
        finally
        {
            libjq.jv_free(result);
            libjq.jv_free(source);
        }
    }

    [Theory]
    [InlineData("7.056371102815960319e-23", "7.05637110281596e-23")]
    [InlineData("5.085945752143224401740376975684e16", "50859457521432240")]
    [InlineData("9.428828476561485067e53", "9.428828476561486e+53")]
    public void LiteralArithmeticUsesJqSeventeenDigitReductionBeforeBinary64(
        string literal,
        string expected)
    {
        var output = Assert.Single(JqProgram.Compile($"{literal} + 0").Execute("null"));

        Assert.Equal(expected, output.GetRawText());
    }

    [Theory]
    [InlineData("-0.0", "null", "0.0")]
    [InlineData("-.", "-0.0", "0.0")]
    [InlineData("-.", "0.0", "0.0")]
    [InlineData("abs", "-0.0", "-0.0")]
    [InlineData("length", "-0.0", "0.0")]
    public void ExactZeroUnaryOperationsMatchTheOracle(
        string filter,
        string input,
        string expected)
    {
        var output = Assert.Single(JqProgram.Compile(filter).Execute(input));

        Assert.Equal(expected, output.GetRawText());
    }

    [Fact]
    public void UpstreamCase137SerializesThroughThePublicApi()
    {
        const string filter =
            "9E999999999, 9999999999E999999990, 1E-999999999, 0.000000001E-999999990";

        var output = JqProgram.Compile(filter)
            .Execute("null")
            .Select(static value => value.GetRawText())
            .ToArray();

        Assert.Equal(
            [
                "9E+999999999",
                "9.999999999E+999999999",
                "1E-999999999",
                "1E-999999999",
            ],
            output);
    }

    private static jv Number(string literal) => libjq.jv_number_with_literal(literal);
}
