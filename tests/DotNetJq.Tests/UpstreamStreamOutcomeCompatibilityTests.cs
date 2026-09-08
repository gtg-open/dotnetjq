namespace DotNetJq.Tests;

/// <summary>Regressions for jq.test cases 493-495 and src/jq_test.c's final jq_next rule.</summary>
public sealed class UpstreamStreamOutcomeCompatibilityTests
{
    private const string Case493 =
        ".[]|(try (if .==\"hi\" then . else error end) catch empty) | \"\\(.) there!\"";

    private const string Case494 =
        ".[]|(try . catch (if .==\"ho\" then \"BROKEN\"|error else empty end)) | " +
        "if .==\"ho\" then error else \"\\(.) there!\" end";

    private const string Case495 =
        "try (try error catch \"inner catch \\(.)\") catch \"outer catch \\(.)\"";

    [Fact]
    public void NeighboringCasesEndNormally()
    {
        Assert.Equal(
            "hi there!",
            Assert.Single(JqProgram.Compile(Case493).Execute("[\"hi\",\"ho\"]")).GetString());
        Assert.Equal(
            "inner catch foo",
            Assert.Single(JqProgram.Compile(Case495).Execute("\"foo\"")).GetString());
    }

    [Fact]
    public void PullCursorPreservesValueBeforeTerminalRuntimeError()
    {
        var program = JqProgram.Compile(Case494);
        using var execution = program.StartUpstreamTestExecution("[\"hi\",\"ho\"]");

        var first = execution.ReadNext();
        var terminal = execution.ReadNext();

        Assert.Equal("hi there!", first.Value?.GetString());
        Assert.Null(first.TerminalError);
        Assert.Null(terminal.Value);
        Assert.Equal("ho", terminal.TerminalError?.Message);
    }

    [Fact]
    public void PublicMaterializedExecuteStillThrowsOnTrailingRuntimeError()
    {
        var program = JqProgram.Compile(Case494);

        var error = Assert.Throws<JqRuntimeException>(
            () => program.Execute("[\"hi\",\"ho\"]"));

        Assert.Equal("ho", error.Message);
    }
}
