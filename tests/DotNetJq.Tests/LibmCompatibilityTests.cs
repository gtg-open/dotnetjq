using DotNetJq.Port;

namespace DotNetJq.Tests;

public sealed class LibmCompatibilityTests
{
    private static readonly string[] SpecialMathSignatures =
    [
        "erf/0", "erfc/0", "gamma/0", "j0/0", "j1/0", "jn/2",
        "lgamma/0", "lgamma_r/0", "tgamma/0", "y0/0", "y1/0", "yn/2",
    ];

    [Theory]
    [InlineData(-3, -0.9999779095030014)]
    [InlineData(-1, -0.8427007929497149)]
    [InlineData(0.5, 0.5204998778130465)]
    [InlineData(1, 0.8427007929497149)]
    [InlineData(2, 0.9953222650189527)]
    [InlineData(3, 0.9999779095030014)]
    public void ErfMatchesPinnedJqOracleExactly(double input, double expected) =>
        Assert.Equal(expected, libjq.jq_erf(input));

    [Theory]
    [InlineData(-3, 1.9999779095030015)]
    [InlineData(-1, 1.842700792949715)]
    [InlineData(0.5, 0.4795001221869535)]
    [InlineData(1, 0.15729920705028513)]
    [InlineData(2, 0.004677734981047265)]
    [InlineData(3, 2.2090496998585438e-05)]
    [InlineData(10, 2.088487583762545e-45)]
    public void ErfcMatchesPinnedJqOracleExactly(double input, double expected) =>
        Assert.Equal(expected, libjq.jq_erfc(input));

    [Fact]
    public void ErfFamilyPreservesSpecialValues()
    {
        Assert.Equal(-1, libjq.jq_erf(double.NegativeInfinity));
        Assert.Equal(1, libjq.jq_erf(double.PositiveInfinity));
        Assert.Equal(2, libjq.jq_erfc(double.NegativeInfinity));
        Assert.Equal(0, libjq.jq_erfc(double.PositiveInfinity));
        Assert.True(double.IsNaN(libjq.jq_erf(double.NaN)));
        Assert.True(double.IsNaN(libjq.jq_erfc(double.NaN)));
        Assert.Equal(
            BitConverter.DoubleToInt64Bits(-0d),
            BitConverter.DoubleToInt64Bits(libjq.jq_erf(-0d)));
    }

    [Theory]
    [InlineData(-3, -0.75, 2)]
    [InlineData(-1, -0.5, 1)]
    [InlineData(0.5, 0.5, 0)]
    [InlineData(1, 0.5, 1)]
    [InlineData(3, 0.75, 2)]
    [InlineData(100, 0.78125, 7)]
    public void FrexpMatchesPinnedJqOracle(
        double input,
        double expectedFraction,
        int expectedExponent)
    {
        var actual = libjq.jq_frexp(input);

        Assert.Equal(expectedFraction, actual.Fraction);
        Assert.Equal(expectedExponent, actual.Exponent);
    }

    [Fact]
    public void FrexpHandlesSubnormalAndSpecialValues()
    {
        Assert.Equal((0.5, -1073), libjq.jq_frexp(double.Epsilon));
        Assert.Equal((double.PositiveInfinity, 0), libjq.jq_frexp(double.PositiveInfinity));
        Assert.True(double.IsNaN(libjq.jq_frexp(double.NaN).Fraction));

        var negativeZero = libjq.jq_frexp(-0d);
        Assert.Equal(0, negativeZero.Exponent);
        Assert.True(IsNegativeZero(negativeZero.Fraction));
    }

    [Theory]
    [InlineData(-2.5, -0.05624371649767407, -1)]
    [InlineData(-1.5, 0.8600470153764809, 1)]
    [InlineData(-0.5, 1.2655121234846454, -1)]
    [InlineData(0.5, 0.5723649429247001, 1)]
    [InlineData(1, 0, 1)]
    [InlineData(3, 0.6931471805599453, 1)]
    [InlineData(10, 12.80182748008147, 1)]
    [InlineData(100, 359.13420536957545, 1)]
    public void LgammaRMatchesPinnedJqOracleExactly(double input, double expected, int expectedSign)
    {
        var actual = libjq.jq_lgamma_r(input);

        Assert.Equal(expected, actual.Value);
        Assert.Equal(expectedSign, actual.Sign);
        Assert.Equal(actual.Value, libjq.jq_lgamma(input));
        Assert.Equal(actual.Value, libjq.jq_gamma(input));
    }

    [Theory]
    [InlineData(-2.5, -0.9453087204829419)]
    [InlineData(-1.5, 2.363271801207355)]
    [InlineData(-0.5, -3.5449077018110318)]
    [InlineData(0.5, 1.772453850905516)]
    [InlineData(1, 1)]
    [InlineData(3, 2)]
    [InlineData(10, 362880)]
    [InlineData(100, 9.332621544394415e155)]
    public void TgammaMatchesPinnedJqOracleExactly(double input, double expected) =>
        Assert.Equal(expected, libjq.jq_tgamma(input));

    [Fact]
    public void TgammaMatchesPinnedLargeFiniteProbeExactly()
    {
        Assert.Equal(6.221140726365697e293, libjq.jq_tgamma(165.125));
    }

    [Fact]
    public void GammaFamilyPreservesPoleAndSpecialValueSemantics()
    {
        Assert.True(double.IsPositiveInfinity(libjq.jq_lgamma(0)));
        Assert.True(double.IsPositiveInfinity(libjq.jq_lgamma(-3)));
        Assert.True(double.IsNaN(libjq.jq_tgamma(-3)));
        Assert.True(double.IsNaN(libjq.jq_tgamma(double.NegativeInfinity)));
        Assert.True(double.IsPositiveInfinity(libjq.jq_tgamma(0)));
        Assert.True(double.IsNegativeInfinity(libjq.jq_tgamma(-0d)));
        Assert.Equal(-1, libjq.jq_lgamma_r(-0d).Sign);
        Assert.True(double.IsNaN(libjq.jq_lgamma(double.NaN)));
    }

    [Fact]
    public void ModfMatchesPinnedJqOracleIncludingSignedZero()
    {
        Assert.Equal((-0.5, -2d), libjq.jq_modf(-2.5));
        Assert.Equal((0.5, 2d), libjq.jq_modf(2.5));
        Assert.Equal((double.PositiveInfinity, 0), Reverse(libjq.jq_modf(double.PositiveInfinity)));

        var negativeInteger = libjq.jq_modf(-3);
        Assert.True(IsNegativeZero(negativeInteger.Fractional));
        Assert.Equal(-3, negativeInteger.Integral);

        var negativeZero = libjq.jq_modf(-0d);
        Assert.True(IsNegativeZero(negativeZero.Fractional));
        Assert.True(IsNegativeZero(negativeZero.Integral));
    }

    [Theory]
    [InlineData(double.Epsilon, 1)]
    [InlineData(0.5, 1)]
    [InlineData(1, 1)]
    [InlineData(3, 1.5)]
    [InlineData(10, 1.25)]
    [InlineData(100, 1.5625)]
    public void SignificandMatchesPinnedJqOracle(double input, double expected) =>
        Assert.Equal(expected, libjq.jq_significand(input));

    [Theory]
    [InlineData(0.5, 0.9384698072408129, 0.2422684576748739, -0.44451873350670656, -1.4714723926702433)]
    [InlineData(1, 0.7651976865579666, 0.4400505857449335, 0.08825696421567698, -0.7812128213002887)]
    [InlineData(2, 0.22389077914123567, 0.5767248077568733, 0.5103756726497451, -0.10703243154093756)]
    [InlineData(3, -0.2600519549019335, 0.33905895852593637, 0.3768500100127904, 0.3246744247917999)]
    [InlineData(10, -0.2459357644513483, 0.04347274616886144, 0.055671167283599395, 0.2490154242069538)]
    [InlineData(12, 0.04768931079683354, -0.2234471044906276, -0.22523731263436145, -0.05709921826089653)]
    [InlineData(100, 0.01998585030422312, -0.07714535201411217, -0.07724431336508317, -0.020372312002759792)]
    public void BaseBesselFunctionsMatchPinnedJqOracleExactly(
        double input,
        double expectedJ0,
        double expectedJ1,
        double expectedY0,
        double expectedY1)
    {
        Assert.Equal(expectedJ0, libjq.jq_j0(input));
        Assert.Equal(expectedJ1, libjq.jq_j1(input));
        Assert.Equal(expectedY0, libjq.jq_y0(input));
        Assert.Equal(expectedY1, libjq.jq_y1(input));
    }

    [Theory]
    [InlineData(2, 0.5, 0.03060402345868264, -5.441370837174267)]
    [InlineData(2, 3, 0.4860912605858911, -0.1604003934849238)]
    [InlineData(3, 10, 0.058379379305186795, -0.25136265718383727)]
    [InlineData(20, 3, 1.2275946737992983e-15, -13113540041757.443)]
    [InlineData(20, 100, 0.062217458498338776, 0.05124797307618843)]
    public void IntegerOrderBesselFunctionsMatchPinnedJqOracleExactly(
        int order,
        double input,
        double expectedJ,
        double expectedY)
    {
        Assert.Equal(expectedJ, libjq.jq_jn(order, input));
        Assert.Equal(expectedY, libjq.jq_yn(order, input));
    }

    [Fact]
    public void BesselNearZeroProbeMatchesPinnedOracleExactly()
    {
        Assert.Equal(
            -0.00019646242619872787,
            libjq.jq_y1(11.75));
    }

    [Theory]
    [InlineData("j0", 52707180.88948363, -1.6883463616805554e-13)]
    [InlineData("y0", 105414360.99356909, 2.5231871953281733e-14)]
    public void LargeArgumentBesselNearZeroProbesMatchPinnedOracleExactly(
        string function,
        double input,
        double expected)
    {
        var actual = function == "j0" ? libjq.jq_j0(input) : libjq.jq_y0(input);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void BesselFunctionsPreserveParityAndDomains()
    {
        Assert.Equal(libjq.jq_jn(3, 2), -libjq.jq_jn(3, -2));
        Assert.Equal(libjq.jq_jn(3, 2), -libjq.jq_jn(-3, 2));
        Assert.Equal(libjq.jq_yn(3, 2), -libjq.jq_yn(-3, 2));
        Assert.True(double.IsNaN(libjq.jq_y0(-1)));
        Assert.True(double.IsNaN(libjq.jq_y1(-1)));
        Assert.True(double.IsNaN(libjq.jq_yn(2, -1)));
        Assert.True(double.IsNegativeInfinity(libjq.jq_y0(0)));
        Assert.True(double.IsNegativeInfinity(libjq.jq_y1(0)));
        Assert.True(double.IsPositiveInfinity(libjq.jq_yn(-1, 0)));
        Assert.True(double.IsNegativeInfinity(libjq.jq_yn(-2, 0)));
        Assert.Equal(0, libjq.jq_j0(double.PositiveInfinity));
        Assert.Equal(0, libjq.jq_y0(double.PositiveInfinity));
        Assert.True(IsNegativeZero(libjq.jq_yn(-1, double.PositiveInfinity)));
        Assert.False(IsNegativeZero(libjq.jq_yn(-3, double.PositiveInfinity)));
    }

    [Fact]
    public void SpecialResultsUseExistingJqJsonFormatting()
    {
        Assert.Equal("null", libjq.jv_dump_string(libjq.jv_number(libjq.jq_y0(-1))));
        Assert.Equal(
            "-1.7976931348623157e+308",
            libjq.jv_dump_string(libjq.jv_number(libjq.jq_y0(0))));
        Assert.Equal(
            "1.7976931348623157e+308",
            libjq.jv_dump_string(libjq.jv_number(libjq.jq_tgamma(0))));
    }

    [Fact]
    public void AllSpecialMathSignaturesArePubliclyAdvertised()
    {
        var advertised = ExecutePublicOne("builtins")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(226, advertised.Count);
        Assert.All(SpecialMathSignatures, signature => Assert.Contains(signature, advertised));
    }

    [Fact]
    public void PublicUnarySpecialMathMatchesPinnedJqOracle()
    {
        var actual = ExecutePublicOne(
            "[(0.5|erf),(0.5|erfc),(-2.5|gamma),(3|lgamma),(10|tgamma)," +
            "(3|j0),(3|j1),(3|y0),(3|y1)]");
        double[] expected =
        [
            0.5204998778130465,
            0.4795001221869535,
            -0.05624371649767407,
            0.6931471805599453,
            362880,
            -0.2600519549019335,
            0.33905895852593637,
            0.3768500100127904,
            0.3246744247917999,
        ];

        Assert.Equal(expected.Length, actual.GetArrayLength());
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.Equal(expected[index], actual[index].GetDouble());
        }
    }

    [Fact]
    public void AllTwelvePublicSpecialMathFunctionsMatchOneSerializedOracleMatrixExactly()
    {
        const string expected =
            "[0.5204998778130465,0.4795001221869535,0.6931471805599453," +
            "0.9384698072408129,0.2422684576748739,0.03060402345868264," +
            "0.5723649429247001,[0.5723649429247001,1],1.772453850905516," +
            "0.5103756726497451,-0.7812128213002887,0.215903594603615]";

        Assert.Equal(
            expected,
            ExecutePublicOne(
                "[(0.5|erf),(0.5|erfc),(3|gamma),(0.5|j0),(0.5|j1)," +
                "jn(2;0.5),(0.5|lgamma),(0.5|lgamma_r),(0.5|tgamma)," +
                "(2|y0),(1|y1),yn(2;4)]")
                .GetRawText());
    }

    [Fact]
    public void PublicLgammaRReturnsMagnitudeAndSign()
    {
        Assert.Equal(
            "[[0.5723649429247001,1],[1.2655121234846454,-1]]",
            ExecutePublicOne("[(0.5|lgamma_r),(-0.5|lgamma_r)]").GetRawText());
    }

    [Theory]
    [InlineData(
        "[jn((1,2);(3,4))]",
        "[0.33905895852593637,0.4860912605858911,-0.06604332802354915,0.3641281458520728]")]
    [InlineData(
        "[yn((1,2);(3,4))]",
        "[0.3246744247917999,-0.1604003934849238,0.3979257105571,0.215903594603615]")]
    public void PublicIntegerOrderBesselPreservesJqArgumentStreamOrder(
        string filter,
        string oracleExpected)
    {
        var actual = ExecutePublicOne(filter);
        using var document = System.Text.Json.JsonDocument.Parse(oracleExpected);
        var expected = document.RootElement;

        Assert.Equal(expected.GetArrayLength(), actual.GetArrayLength());
        for (var index = 0; index < expected.GetArrayLength(); index++)
        {
            Assert.Equal(expected[index].GetDouble(), actual[index].GetDouble());
        }
    }

    [Fact]
    public void PublicSpecialMathPreservesJqErrorsAndNonFiniteFormatting()
    {
        Assert.Equal(
            "[1.7976931348623157e+308,null,null,-1.7976931348623157e+308," +
            "1.7976931348623157e+308,0]",
            ExecutePublicOne(
                "[(0|tgamma),(-3|tgamma),(-1|y0),(0|y0),yn(-1;0),yn(-3;infinite)]")
                .GetRawText());
        Assert.Equal(
            "\"string (\\\"x\\\") number required\"",
            ExecutePublicOne("try (\"x\"|erf) catch .").GetRawText());
        Assert.Equal(
            "\"string (\\\"x\\\") number required\"",
            ExecutePublicOne("try jn(\"x\";2) catch .").GetRawText());
    }

    private static (double Integral, double Fractional) Reverse(
        (double Fractional, double Integral) value) =>
        (value.Integral, value.Fractional);

    private static System.Text.Json.JsonElement ExecutePublicOne(string filter) =>
        Assert.Single(JqProgram.Compile(filter).Execute("null"));

    private static bool IsNegativeZero(double value) =>
        value == 0 && BitConverter.DoubleToInt64Bits(value) < 0;

}
