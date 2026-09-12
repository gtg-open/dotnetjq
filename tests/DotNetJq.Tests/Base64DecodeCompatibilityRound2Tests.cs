using System.Text.Json;
using DotNetJq.Port;
using DotNetJq.Tests.Harness;

namespace DotNetJq.Tests;

public sealed class Base64DecodeCompatibilityRound2Tests
{
    [Theory]
    [InlineData("null", "��")]
    [InlineData("wA==", "�")]
    [InlineData("wK8=", "��")]
    [InlineData("gIA=", "��")]
    [InlineData("6WU=", "�")]
    [InlineData("6Q==", "�")]
    [InlineData("7aCA", "�")]
    [InlineData("9JCAgA==", "�")]
    [InlineData("8J+Y", "�")]
    [InlineData("4UE=", "�")]
    [InlineData("4UFB", "�AA")]
    [InlineData("YWJj/3o=", "abc�z")]
    public async Task InvalidDecodedUtf8UsesJqReplacementBoundaries(string base64, string expected)
    {
        var managed = FormatBase64Decoded(JsonSerializer.Serialize(base64));

        Assert.Equal(expected, managed);
        if (!JqOracle.TryResolveExecutable(out _))
        {
            return;
        }

        var oracle = await JqOracle.ExecuteAsync(
            "@base64d",
            JsonSerializer.Serialize(base64),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(oracle.Succeeded, oracle.StandardError);
        var oracleJson = Assert.Single(oracle.OutputLines);
        Assert.Equal(expected, JsonSerializer.Deserialize<string>(oracleJson));
    }

    [Fact]
    public async Task ValidFourByteUtf8StillDecodesAsOneScalar()
    {
        const string base64 = "8J+Ygw==";
        const string expected = "😃";

        Assert.Equal(expected, FormatBase64Decoded(JsonSerializer.Serialize(base64)));
        if (!JqOracle.TryResolveExecutable(out _))
        {
            return;
        }

        var oracle = await JqOracle.ExecuteAsync(
            "@base64d",
            JsonSerializer.Serialize(base64),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(oracle.Succeeded, oracle.StandardError);
        Assert.Equal(expected, JsonSerializer.Deserialize<string>(Assert.Single(oracle.OutputLines)));
    }

    private static string FormatBase64Decoded(string inputJson)
    {
        using var jq = libjq.jq_init();
        var input = libjq.jv_parse(inputJson);
        var format = libjq.jv_string("base64d");
        var formatted = libjq.jv_invalid();
        try
        {
            var function = Assert.Single(
                libjq.function_list,
                static candidate => candidate.name == "format").fptr.a2!;
            formatted = function(jq, input, format);
            input = libjq.jv_invalid();
            format = libjq.jv_invalid();
            Assert.True(formatted.IsValid);
            return formatted.StringValue;
        }
        finally
        {
            libjq.jv_free(formatted);
            libjq.jv_free(format);
            libjq.jv_free(input);
        }
    }
}
