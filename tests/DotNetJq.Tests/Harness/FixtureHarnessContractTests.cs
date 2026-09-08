using Xunit;

namespace DotNetJq.Tests.Harness;

public sealed class FixtureHarnessContractTests
{
    [Fact]
    public void RepositoryPathsResolveFromNestedTestOutputDirectory()
    {
        var temporary = Directory.CreateTempSubdirectory("dotnetjq-portability-");
        try
        {
            File.WriteAllText(Path.Combine(temporary.FullName, "DotNetJq.sln"), string.Empty);
            var nested = Directory.CreateDirectory(Path.Combine(temporary.FullName, "tests", "bin", "Release"));

            Assert.Equal(
                Path.Combine(temporary.FullName, "upstream", "jq"),
                UpstreamTestFile.ResolveRepositoryPath("upstream/jq", nested.FullName));
            Assert.Equal(
                Path.Combine(temporary.FullName, "artifacts", "test-assets", "jq-1.8.2", "oracle", "jq"),
                UpstreamTestFile.ResolveRepositoryPath("artifacts/test-assets/jq-1.8.2/oracle/jq", nested.FullName));
        }
        finally
        {
            temporary.Delete(recursive: true);
        }
    }

    [Fact]
    public void RepositoryPathsFallBackToLaunchDirectoryWhenNoCheckoutExists()
    {
        var temporary = Directory.CreateTempSubdirectory("dotnetjq-portability-");
        try
        {
            Assert.Equal(
                Path.Combine(temporary.FullName, "upstream", "jq"),
                UpstreamTestFile.ResolveRepositoryPath("upstream/jq", temporary.FullName));
        }
        finally
        {
            temporary.Delete(recursive: true);
        }
    }

    [Fact]
    public void ParsePreservesUpstreamGroupingRulesAndMultipleOutputs()
    {
        const string fixture = """
              # leading whitespace comments are ignored between groups

            .items[]
            {"items":[1,2]}
            1
            2

            empty
            null

            %%FAIL
            broken(
            jq: error: syntax error
                broken(
                       ^

            %%FAIL IGNORE MSG
            also-broken(
            platform-specific diagnostic
            """;

        var cases = UpstreamTestFile.Parse(new StringReader(fixture));

        Assert.Collection(
            cases,
            first =>
            {
                Assert.Equal(1, first.Number);
                Assert.Equal(3, first.ProgramLine);
                Assert.Equal(".items[]", first.Program);
                Assert.Equal("{\"items\":[1,2]}", first.Input);
                Assert.Equal(["1", "2"], first.ExpectedOutputs);
                Assert.False(first.MustFail);
            },
            second =>
            {
                Assert.Equal("empty", second.Program);
                Assert.Equal("null", second.Input);
                Assert.Empty(second.ExpectedOutputs);
            },
            third =>
            {
                Assert.Equal("broken(", third.Program);
                Assert.Null(third.Input);
                Assert.True(third.MustFail);
                Assert.True(third.ExpectedFailure!.CheckMessage);
                Assert.Equal(
                    ["jq: error: syntax error", "    broken(", "           ^"],
                    third.ExpectedFailure.MessageLines);
            },
            fourth =>
            {
                Assert.Equal("also-broken(", fourth.Program);
                Assert.True(fourth.MustFail);
                Assert.False(fourth.ExpectedFailure!.CheckMessage);
                Assert.Equal(["platform-specific diagnostic"], fourth.ExpectedFailure.MessageLines);
            });
    }

    [Fact]
    public void ParseConsumesCommentLookingInputWithoutSkippingIt()
    {
        const string fixture = """
            .
              # this is the input line, even though it resembles a comment
            42

            """;

        var testCase = Assert.Single(UpstreamTestFile.Parse(new StringReader(fixture)));

        Assert.Equal("  # this is the input line, even though it resembles a comment", testCase.Input);
        Assert.Equal(["42"], testCase.ExpectedOutputs);
    }

    [Fact]
    public void ParseRejectsAProgramWithoutAnInput()
    {
        var exception = Assert.Throws<InvalidDataException>(
            () => UpstreamTestFile.Parse(new StringReader(".\n"), "truncated.test"));

        Assert.Contains("truncated.test", exception.Message, StringComparison.Ordinal);
        Assert.Contains("no input", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadFixtureParsesPinnedJqTestWhenCheckoutIsAvailable()
    {
        if (!UpstreamTestFile.TryResolveUpstreamRoot(out _))
        {
            return;
        }

        var cases = UpstreamTestFile.ReadFixture("jq.test");

        Assert.Equal(550, cases.Count);
        Assert.Equal(19, cases.Count(testCase => testCase.MustFail));
        Assert.Contains(cases, testCase => testCase.ExpectedOutputs.Count > 1);
        Assert.Contains(cases, testCase => !testCase.MustFail && testCase.ExpectedOutputs.Count == 0);
    }
}
