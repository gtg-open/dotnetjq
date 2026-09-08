using DotNetJq.Port;

namespace DotNetJq.Tests;

public sealed class ArithmeticCompatibilityRound3Tests
{
    [Fact]
    public void UpstreamCase142UsesSaturatedIntMaxModuloForInfinities()
    {
        Assert.Equal(
            "[0,0,0,0,0,-1]",
            ExecuteOne("[(infinite, -infinite) % (1, -1, infinite)]", "null"));
    }

    [Fact]
    public void ModuloTruncatesFractionsAndSaturatesAtIntMaxBounds()
    {
        Assert.Equal(
            "[1,1,-1,-1,1,1,0,0,1,1,0,0]",
            ExecuteOne(
                "[5.9%2.9,5.9%-2.9,-5.9%2.9,-5.9%-2.9," +
                "9223372036854775808%2.9,9223372036854775808%-2.9," +
                "-9223372036854775809%2.9,-9223372036854775809%-2.9," +
                "infinite%2.9,infinite%-2.9,-infinite%2.9,-infinite%-2.9]",
                "null"));
    }

    [Fact]
    public void ModuloPropagatesNaNAndUsesJqZeroDivisorDiagnostic()
    {
        Assert.Equal("[true,true]", ExecuteOne("[nan % 1, 1 % nan | isnan]", "null"));

        var exception = Assert.Throws<JqRuntimeException>(
            () => JqProgram.Compile("1 % 0").Execute("null"));
        Assert.Equal(
            "number (1) and number (0) cannot be divided (remainder) because the divisor is zero",
            exception.Message);
    }

    [Fact]
    public void UpstreamCase338RepeatsStringsWithEitherOperandOrder()
    {
        Assert.Equal(
            "[null,null,\"\",\"\",\"abc\",\"abc\",\"abcabcabc\"," +
            "\"abcabcabcabcabcabcabcabcabcabc\"]",
            ExecuteOne(
                "[.[] * \"abc\"]",
                "[-1.0,-0.5,0.0,0.5,1.0,1.5,3.7,10.0]"));

        Assert.Equal("\"éé\"", ExecuteOne("2 * .", "\"é\""));
    }

    [Fact]
    public void UpstreamCase339MapsNegativeAndNaNCountsToNull()
    {
        Assert.Equal("[null,null]", ExecuteOne("[. * (nan,-nan)]", "\"abc\""));
    }

    [Fact]
    public void EmptyStringRepeatTakesTheJqFastPath()
    {
        Assert.Equal(
            "[\"\",null,null,\"\",\"\",\"\",null,null,\"\",\"\"]",
            ExecuteOne(
                "[\"\" * (infinite,-1,nan,0,1.9), " +
                "(infinite,-1,nan,0,1.9) * \"\"]",
                "null"));
    }

    [Fact]
    public void UpstreamCase342RejectsOversizedRepeatBeforeAllocation()
    {
        Assert.Equal(
            "\"Repeat string result too long\"",
            ExecuteOne("try (. * 1000000000) catch .", "\"abc\""));

        var exception = Assert.Throws<JqRuntimeException>(
            () => JqProgram.Compile(". * 2147483647").Execute("\"é\""));
        Assert.Equal("Repeat string result too long", exception.Message);
    }

    [Fact]
    public void BinaryTypeErrorsIncludeBothJqValues()
    {
        var exception = Assert.Throws<JqRuntimeException>(
            () => JqProgram.Compile("1 % \"x\"").Execute("null"));

        Assert.Equal(
            "number (1) and string (\"x\") cannot be divided (remainder)",
            exception.Message);
    }

    private static string ExecuteOne(string filter, string input) =>
        Assert.Single(JqProgram.Compile(filter).Execute(input)).GetRawText();
}
