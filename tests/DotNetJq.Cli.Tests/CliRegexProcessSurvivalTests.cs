namespace DotNetJq.Cli.Tests;

public sealed class CliRegexProcessSurvivalTests
{
    [Fact]
    public async Task EscapedSupplementaryLiteralAndClassDoNotAbortTheProcess()
    {
        var arguments = new[]
        {
            "-nrc",
            "[(\"𐐨\" | test(\"\\\\𐐀\"; \"i\")), " +
            "(\"𐐨\" | test(\"[\\\\𐐀]\"; \"i\"))]",
        };
        var oracle = await CliTestEnvironment.RequireOracleAsync();
        var expected = await CliProcess.RunAsync(
            oracle,
            arguments,
            cancellationToken: TestContext.Current.CancellationToken);
        var actual = await CliProcess.RunAsync(
            CliTestEnvironment.Subject,
            arguments,
            TestContext.Current.CancellationToken);

        CliAssertions.Equal(expected, 0, "[true,true]\n", string.Empty);
        CliAssertions.Equal(
            actual,
            expected.ExitCode,
            expected.StandardOutput,
            expected.StandardError);
    }
}
