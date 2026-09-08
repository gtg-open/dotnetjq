using DotNetJq.DifferentialProbe;

namespace DotNetJq.Tests;

public sealed class DifferentialProbeDiagnosticComparisonTests
{
    [Fact]
    public void RuntimeLocationAndExecutableEnvelopeDoNotChangeTheJqErrorPayload()
    {
        var reasons = CompareFailures(
            ProbeOutcomeKind.RuntimeError,
            "/opt/jq/bin/jq: error (at /tmp/input.json:27): Cannot index string with string \"x\"",
            "Cannot index string with string \"x\"");

        Assert.Empty(reasons);
    }

    [Fact]
    public void UnrelatedRuntimeDiagnosticsRemainAMismatch()
    {
        var reasons = CompareFailures(
            ProbeOutcomeKind.RuntimeError,
            "jq: error (at <stdin>:1): Cannot index string with string \"x\"",
            "number (1) and number (0) cannot be divided because the divisor is zero");

        Assert.Contains("diagnostic payload differs", reasons);
    }

    [Fact]
    public void NativeCompileSummaryAndExecutableNameDoNotChangeTheCompilerPayload()
    {
        const string Payload =
            "unexpected INVALID_CHARACTER at <top-level>, line 1, column 2:\n    .#\n     ^";
        var reasons = CompareFailures(
            ProbeOutcomeKind.CompileError,
            "/opt/jq/bin/jq: error: " + Payload + "\njq: 1 compile error\n",
            "dotnetjq: error: " + Payload);

        Assert.Empty(reasons);
    }

    [Fact]
    public void NativeBlankSeparatorBeforeCompileSummaryIsNotPartOfTheErrorPayload()
    {
        var reasons = CompareFailures(
            ProbeOutcomeKind.CompileError,
            "jq: error: module not found: missing\n\njq: 1 compile error\n",
            "jq: error: module not found: missing");

        Assert.Empty(reasons);
    }

    [Fact]
    public void CompileLocationAndSourceExcerptRemainPartOfTheComparedPayload()
    {
        var reasons = CompareFailures(
            ProbeOutcomeKind.CompileError,
            "jq: error: unexpected INVALID_CHARACTER at <top-level>, line 1, column 2:\n" +
            "    .#\n     ^\njq: 1 compile error\n",
            "jq: error: unexpected INVALID_CHARACTER at <top-level>, line 1, column 3:\n" +
            "    .#\n      ^");

        Assert.Contains("diagnostic payload differs", reasons);
    }

    [Fact]
    public void MissingDiagnosticOnEitherSideRemainsAMismatch()
    {
        Assert.Contains(
            "diagnostic payload differs",
            CompareFailures(ProbeOutcomeKind.RuntimeError, "jq: error (at <stdin>:1): boom", string.Empty));
        Assert.Contains(
            "diagnostic payload differs",
            CompareFailures(ProbeOutcomeKind.RuntimeError, string.Empty, "boom"));
    }

    [Fact]
    public void DiagnosticLineStructureIsNotNormalizedAway()
    {
        var reasons = CompareFailures(
            ProbeOutcomeKind.RuntimeError,
            "jq: error (at <stdin>:1): first\nsecond\n",
            "first second");

        Assert.Contains("diagnostic payload differs", reasons);
    }

    [Fact]
    public void HaltErrorPayloadThatLooksLikeAProcessEnvelopeRemainsLiteral()
    {
        const string LiteralPayload = "jq: error (at <stdin>:1): deliberately literal";

        Assert.Empty(CompareFailures(
            ProbeOutcomeKind.RuntimeError,
            LiteralPayload,
            LiteralPayload));
    }

    [Fact]
    public void MatchingTimeoutsNeverCountAsParity() =>
        AssertMatchingNonSemanticOutcomesNeverCountAsParity(ProbeOutcomeKind.Timeout);

    [Fact]
    public void MatchingHarnessErrorsNeverCountAsParity() =>
        AssertMatchingNonSemanticOutcomesNeverCountAsParity(ProbeOutcomeKind.HarnessError);

    private static void AssertMatchingNonSemanticOutcomesNeverCountAsParity(ProbeOutcomeKind kind)
    {
        var reasons = CompareFailures(kind, "same diagnostic", "same diagnostic");

        Assert.Contains(reasons, reason =>
            reason.Equals($"official execution did not produce a semantic outcome: {kind}", StringComparison.Ordinal));
        Assert.Contains(reasons, reason =>
            reason.Equals($"managed execution did not produce a semantic outcome: {kind}", StringComparison.Ordinal));
    }

    [Fact]
    public void FullProbeDoesNotChooseATrackedReportPathImplicitly()
    {
        Assert.True(Options.TryParse([], out var options, out var error), error);

        Assert.Null(options.ReportPath);
        Assert.False(options.CheckReport);
    }

    [Fact]
    public void ReportCheckingRequiresAnExplicitReportPath()
    {
        Assert.False(Options.TryParse(["--check-report"], out _, out var error));
        Assert.Equal("--check-report requires an explicit --report PATH", error);

        Assert.True(
            Options.TryParse(
                ["--report", "probe.md", "--check-report"],
                out var options,
                out error),
            error);
        Assert.Equal("probe.md", options.ReportPath);
        Assert.True(options.CheckReport);
    }

    [Fact]
    public void AsPatternOptionalSuffixKeepsThePinnedBisonDiagnostic()
    {
        var exception = Assert.Throws<JqCompileException>(
            () => JqProgram.Compile(". as [$a,$b]? | [$a,$b]"));

        Assert.Equal(
            "jq: error: syntax error, unexpected '?', expecting '|' " +
            "at <top-level>, line 1, column 13:\n" +
            "    . as [$a,$b]? | [$a,$b]\n" +
            "                ^",
            exception.Message);
    }

    private static List<string> CompareFailures(
        ProbeOutcomeKind kind,
        string officialDiagnostic,
        string managedDiagnostic) =>
        ProbeRunner.Compare(
            new ProbeExecution(kind, [], officialDiagnostic),
            new ProbeExecution(kind, [], managedDiagnostic));
}
