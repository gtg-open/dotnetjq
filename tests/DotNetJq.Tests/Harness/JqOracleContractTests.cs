using Xunit;

namespace DotNetJq.Tests.Harness;

public sealed class JqOracleContractTests
{
    [Fact]
    public async Task ReadVersionAsyncUsesThePinnedJqRelease()
    {
        if (!JqOracle.TryResolveExecutable(out _))
        {
            return;
        }

        var result = await JqOracle.ReadVersionAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.StandardError);
        Assert.Equal(JqOracle.ExpectedVersion, result.StandardOutput.Trim());
    }

    [Fact]
    public async Task ExecuteAsyncReturnsOrderedCompactJsonValues()
    {
        if (!JqOracle.TryResolveExecutable(out _))
        {
            return;
        }

        var result = await JqOracle.ExecuteAsync(
            ".items[] | . + 1",
            "{\"items\":[1,2]}",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.StandardError);
        Assert.Equal(["2", "3"], result.OutputLines);
        Assert.Empty(result.StandardError);
    }

    [Fact]
    public async Task ExecuteFixtureCaseAsyncReportsCompileFailureAndDiagnostic()
    {
        if (!JqOracle.TryResolveExecutable(out _))
        {
            return;
        }

        var testCase = new UpstreamTestCase(
            Number: 1,
            ProgramLine: 1,
            Program: "broken(",
            Input: null,
            ExpectedOutputs: Array.Empty<string>(),
            new UpstreamExpectedFailure(CheckMessage: false, MessageLines: Array.Empty<string>()));

        var result = await JqOracle.ExecuteFixtureCaseAsync(
            testCase,
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Empty(result.OutputLines);
        Assert.Contains("compile error", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }
}
