using System.Text.Json;
using Xunit;

namespace DotNetJq.Tests.Harness;

public sealed class ManagedApiDifferentialSmokeTests
{
    [Fact]
    public async Task ExecutePublicApiMatchesOracleOutputCountOrderAndValues()
    {
        if (!JqOracle.TryResolveExecutable(out _))
        {
            return;
        }

        const string filter = ".items[] | . + 1";
        const string input = "{\"items\":[1,2]}";

        var expected = await JqOracle.ExecuteAsync(
            filter,
            input,
            cancellationToken: TestContext.Current.CancellationToken);
        var actual = JqProgram.Compile(filter).Execute(input);

        Assert.True(expected.Succeeded, expected.StandardError);
        Assert.Equal(expected.OutputLines.Count, actual.Count);
        for (var index = 0; index < actual.Count; index++)
        {
            using var expectedDocument = JsonDocument.Parse(expected.OutputLines[index]);
            Assert.True(
                JsonElement.DeepEquals(expectedDocument.RootElement, actual[index]),
                $"Output {index} differed. Expected {expected.OutputLines[index]}, " +
                $"actual {actual[index].GetRawText()}.");
        }
    }
}
