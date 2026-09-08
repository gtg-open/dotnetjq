using DotNetJq;
using DotNetJq.Port;

namespace DotNetJq.Tests;

public sealed class FdlibmElementaryCompatibilityTests
{
    [Theory]
    [InlineData(1, 0)]
    [InlineData(1.0000000000000002, 2.1073424255447017e-08)]
    [InlineData(1.5, 0.9624236501192069)]
    [InlineData(2, 1.3169578969248166)]
    [InlineData(2.0000019073486324, 1.3169589981323628)]
    [InlineData(2.0000000000000004, 1.316957896924817)]
    [InlineData(10, 2.993222846126381)]
    [InlineData(268435456, 20.10126823623841)]
    [InlineData(double.MaxValue, 710.4758600739439)]
    public void AcoshMatchesPinnedJqOracleExactly(double input, double expected) =>
        Assert.Equal(expected, libjq.jq_acosh(input));

    [Theory]
    [InlineData(-double.MaxValue, -710.4758600739439)]
    [InlineData(-1e30, -69.77069997038132)]
    [InlineData(-3, -1.8184464592320668)]
    [InlineData(-0.5, -0.48121182505960347)]
    [InlineData(-1e-100, -1e-100)]
    [InlineData(1e-100, 1e-100)]
    [InlineData(0.5, 0.48121182505960347)]
    [InlineData(3, 1.8184464592320668)]
    [InlineData(1e30, 69.77069997038132)]
    [InlineData(double.MaxValue, 710.4758600739439)]
    public void AsinhMatchesPinnedJqOracleExactly(double input, double expected) =>
        Assert.Equal(expected, libjq.jq_asinh(input));

    [Theory]
    [InlineData(-0.9999999999999999, -18.714973875118524)]
    [InlineData(-0.75, -0.9729550745276566)]
    [InlineData(-0.5, -0.5493061443340548)]
    [InlineData(-0.1, -0.10033534773107558)]
    [InlineData(-1e-100, -1e-100)]
    [InlineData(1e-100, 1e-100)]
    [InlineData(0.1, 0.10033534773107558)]
    [InlineData(0.5, 0.5493061443340548)]
    [InlineData(0.75, 0.9729550745276566)]
    [InlineData(0.9999999999999999, 18.714973875118524)]
    public void AtanhMatchesPinnedJqOracleExactly(double input, double expected) =>
        Assert.Equal(expected, libjq.jq_atanh(input));

    [Theory]
    [InlineData(-1000, -1)]
    [InlineData(-50, -1)]
    [InlineData(-1, -0.6321205588285577)]
    [InlineData(-0.7, -0.5034146962085905)]
    [InlineData(-0.1, -0.09516258196404043)]
    [InlineData(-1e-100, -1e-100)]
    [InlineData(1e-100, 1e-100)]
    [InlineData(0.1, 0.10517091807564763)]
    [InlineData(0.5, 0.6487212707001282)]
    [InlineData(1, 1.718281828459045)]
    [InlineData(10, 22025.465794806718)]
    [InlineData(100, 2.6881171418161356e43)]
    [InlineData(709, 8.218407461554972e307)]
    public void Expm1MatchesPinnedJqOracleExactly(double input, double expected) =>
        Assert.Equal(expected, libjq.jq_expm1(input));

    [Theory]
    [InlineData(-0.9999999999999999, -36.7368005696771)]
    [InlineData(-0.5, -0.6931471805599453)]
    [InlineData(-0.29289317131042486, -0.346573523100549)]
    [InlineData(-0.2928931713104248, -0.3465735231005489)]
    [InlineData(-0.29289317131042475, -0.34657352310054884)]
    [InlineData(-1.8626451492309574e-09, -1.862645150965681e-09)]
    [InlineData(-1.862645149230957e-09, -1.8626451509656805e-09)]
    [InlineData(-1.8626451492309568e-09, -1.86264515096568e-09)]
    [InlineData(-5.551115123125784e-17, -5.551115123125784e-17)]
    [InlineData(-5.551115123125783e-17, -5.551115123125783e-17)]
    [InlineData(-5.551115123125782e-17, -5.551115123125782e-17)]
    [InlineData(-1e-100, -1e-100)]
    [InlineData(1e-100, 1e-100)]
    [InlineData(5.551115123125782e-17, 5.551115123125782e-17)]
    [InlineData(5.551115123125783e-17, 5.551115123125783e-17)]
    [InlineData(5.551115123125784e-17, 5.551115123125784e-17)]
    [InlineData(1.8626451492309568e-09, 1.8626451474962333e-09)]
    [InlineData(1.862645149230957e-09, 1.8626451474962336e-09)]
    [InlineData(1.8626451492309574e-09, 1.862645147496234e-09)]
    [InlineData(0.41421365737915034, 0.34657365745939633)]
    [InlineData(0.4142136573791504, 0.3465736574593964)]
    [InlineData(0.41421365737915045, 0.34657365745939644)]
    [InlineData(0.5, 0.4054651081081644)]
    [InlineData(1, 0.6931471805599453)]
    [InlineData(2, 1.0986122886681096)]
    [InlineData(10, 2.3978952727983707)]
    [InlineData(double.MaxValue, 709.782712893384)]
    public void Log1pMatchesPinnedJqOracleExactly(double input, double expected) =>
        Assert.Equal(expected, libjq.jq_log1p(input));

    [Fact]
    public void FdlibmElementaryFunctionsPreserveDomainsAndSpecialValues()
    {
        Assert.True(double.IsNaN(libjq.jq_acosh(0.5)));
        Assert.True(double.IsPositiveInfinity(libjq.jq_acosh(double.PositiveInfinity)));
        Assert.True(double.IsNaN(libjq.jq_acosh(double.NaN)));

        Assert.True(double.IsNegativeInfinity(libjq.jq_asinh(double.NegativeInfinity)));
        Assert.True(double.IsPositiveInfinity(libjq.jq_asinh(double.PositiveInfinity)));
        Assert.True(double.IsNaN(libjq.jq_asinh(double.NaN)));

        Assert.True(double.IsNaN(libjq.jq_atanh(-1.0000000000000002)));
        Assert.True(double.IsNegativeInfinity(libjq.jq_atanh(-1)));
        Assert.True(double.IsPositiveInfinity(libjq.jq_atanh(1)));
        Assert.True(double.IsNaN(libjq.jq_atanh(1.0000000000000002)));

        Assert.Equal(-1, libjq.jq_expm1(double.NegativeInfinity));
        Assert.True(double.IsPositiveInfinity(libjq.jq_expm1(double.PositiveInfinity)));
        Assert.True(double.IsNaN(libjq.jq_expm1(double.NaN)));

        Assert.True(double.IsNaN(libjq.jq_log1p(-1.0000000000000002)));
        Assert.True(double.IsNegativeInfinity(libjq.jq_log1p(-1)));
        Assert.True(double.IsPositiveInfinity(libjq.jq_log1p(double.PositiveInfinity)));
        Assert.True(double.IsNaN(libjq.jq_log1p(double.NaN)));

        AssertNegativeZero(libjq.jq_asinh(-0d));
        AssertNegativeZero(libjq.jq_atanh(-0d));
        AssertNegativeZero(libjq.jq_expm1(-0d));
        AssertNegativeZero(libjq.jq_log1p(-0d));
    }

    [Fact]
    public void PublicFdlibmElementaryFunctionsMatchPinnedJqSerializationExactly()
    {
        const string expected =
            "[1.3169578969248166,0.5493061443340548,1e-100,710.4758600739439,1e-100]";
        var actual = Assert.Single(
            JqProgram.Compile(
                "[(2|acosh),(0.5|atanh),(1e-100|expm1)," +
                "(1.7976931348623157e308|asinh),(1e-100|log1p)]")
                .Execute("null"));

        Assert.Equal(expected, actual.GetRawText());
    }

    [Fact]
    public void PublicLog1pBranchMatrixMatchesPinnedJqSerializationExactly()
    {
        const string expected =
            "[-36.7368005696771,-0.3465735231005489,-1.8626451509656805e-09," +
            "-5.551115123125783e-17,-1e-100,1e-100,5.551115123125783e-17," +
            "1.8626451474962336e-09,0.3465736574593964,0.4054651081081644," +
            "1.0986122886681096,709.782712893384]";
        var actual = Assert.Single(
            JqProgram.Compile(
                "[-0.9999999999999999,-0.2928931713104248," +
                "-1.862645149230957e-09,-5.551115123125783e-17,-1e-100," +
                "1e-100,5.551115123125783e-17,1.862645149230957e-09," +
                "0.4142136573791504,0.5,2,1.7976931348623157e308]|map(log1p)")
                .Execute("null"));

        Assert.Equal(expected, actual.GetRawText());
    }

    private static void AssertNegativeZero(double value) =>
        Assert.Equal(
            BitConverter.DoubleToInt64Bits(-0d),
            BitConverter.DoubleToInt64Bits(value));
}
