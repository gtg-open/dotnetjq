using DotNetJq.Port;
using DotNetJq.Tests.Harness;
using Xunit;

namespace DotNetJq.Tests;

public sealed class FormatCompatibilityTests
{
    [Theory]
    [InlineData("text", "\"!()<>&'\\\"\\t\"", "!()<>&'\"\t")]
    [InlineData("json", "\"!()<>&'\\\"\\t\"", "\"!()<>&'\\\"\\t\"")]
    [InlineData("html", "\"!()<>&'\\\"\\t\"", "!()&lt;&gt;&amp;&apos;&quot;\t")]
    [InlineData("uri", "\"\"", "")]
    [InlineData("uri", "\"\\b\\t\\n\\f\\r\\\"\\\\\"", "%08%09%0A%0C%0D%22%5C")]
    [InlineData("uri", "\",-./09:;<=>?@AZ[\\\\]^_`az{|}~\\u007f\"", "%2C-.%2F09%3A%3B%3C%3D%3E%3F%40AZ%5B%5C%5D%5E_%60az%7B%7C%7D~%7F")]
    [InlineData("uri", "\"a μ ∰ 😎\"", "a%20%CE%BC%20%E2%88%B0%20%F0%9F%98%8E")]
    [InlineData("uri", "\"a\\u0000b\\u0000c\"", "a%00b%00c")]
    [InlineData("urid", "\"\"", "")]
    [InlineData("urid", "\"%08%09%0A%0C%0D%22%5C\"", "\b\t\n\f\r\"\\")]
    [InlineData("urid", "\"%2C-.%2F09%3A%3B%3C%3D%3E%3F%40AZ%5B%5C%5D%5E_%60az%7B%7C%7D~%7F\"", ",-./09:;<=>?@AZ[\\]^_`az{|}~\u007f")]
    [InlineData("urid", "\"a%20%CE%BC%20%E2%88%B0%20%F0%9F%98%8E\"", "a μ ∰ 😎")]
    [InlineData("urid", "\"Knäckebröd\"", "Knäckebröd")]
    [InlineData("urid", "\"%c3%a4b%c3%a7d%c3%ab\"", "äbçdë")]
    [InlineData("urid", "\"a%00b%00c\"", "a\0b\0c")]
    [InlineData("base64", "\"foóbar\\n\"", "Zm/Ds2Jhcgo=")]
    [InlineData("base64", "\"<>&'\\\"\\t\"", "PD4mJyIJ")]
    [InlineData("base64d", "\"\"", "")]
    [InlineData("base64d", "\"=\"", "")]
    [InlineData("base64d", "\"cWl4YmF6Cg\"", "qixbaz\n")]
    public void ScalarFormatsMatchUpstreamFixtures(string format, string inputJson, string expected)
    {
        Assert.Equal(expected, Format(format, inputJson));
    }

    [Theory]
    [InlineData("csv", "[1,\"!()<>&'\\\"\\t\"]", "1,\"!()<>&'\"\"\t\"")]
    [InlineData("tsv", "[1,\"!()<>&'\\\"\\t\"]", "1\t!()<>&'\"\\t")]
    [InlineData("sh", "\"!()<>&'\\\"\\t\"", "'!()<>&'\\''\"\t'")]
    [InlineData("sh", "[null,true,false,2,\"O'Hara\"]", "null true false 2 'O'\\''Hara'")]
    public void StructuredFormatsMatchUpstreamFixtures(string format, string inputJson, string expected)
    {
        Assert.Equal(expected, Format(format, inputJson));
    }

    [Theory]
    [InlineData("urid", "\"abc%\"", "string (\"abc%\") is not a valid uri encoding")]
    [InlineData("urid", "\"abc%f\"", "string (\"abc%f\") is not a valid uri encoding")]
    [InlineData("urid", "\"abc%g\"", "string (\"abc%g\") is not a valid uri encoding")]
    [InlineData("urid", "\"%FX%9F%98%8E\"", "string (\"%FX%9F%98%8E\") is not a valid uri encoding")]
    [InlineData("urid", "\"%F0%93%81\"", "string (\"%F0%93%81\") is not a valid uri encoding")]
    [InlineData("urid", "\"%F0%C0%81%8E\"", "string (\"%F0%C0%81%8E\") is not a valid uri encoding")]
    [InlineData("base64d", "\"Not base64 data\"", "string (\"Not base64 data\") is not valid base64 data")]
    [InlineData("base64d", "\"QUJDa\"", "string (\"QUJDa\") trailing base64 byte found")]
    [InlineData("csv", "1", "number (1) cannot be csv-formatted, only array")]
    public void InvalidFormatInputsReportJqErrors(string format, string inputJson, string expectedMessage)
    {
        var outcome = InvokeFormat(format, inputJson);

        Assert.False(outcome.Succeeded);
        Assert.Equal(expectedMessage, outcome.Text);
    }

    [Theory]
    [InlineData("urid", "a%20%CE%BC", "a μ")]
    [InlineData("base64d", "cWl4YmF6Cg", "qixbaz\n")]
    public void SuccessfulDecodersDoNotConsumeOrLeakAHiddenDiagnosticOwner(
        string format,
        string input,
        string expected)
    {
        var value = libjq.jv_string(input);
        var storage = Assert.IsType<jvp_string>(value.Value);
        using var jq = libjq.jq_init();
        try
        {
            for (var iteration = 0; iteration < 128; iteration++)
            {
                var formatted = FormatFunction(
                    jq,
                    libjq.jv_copy(value),
                    libjq.jv_string(format));
                Assert.True(formatted.IsValid);
                Assert.Equal(expected, formatted.StringValue);
                libjq.jv_free(formatted);
                Assert.Equal(1, storage.Refcnt.Count);
            }
        }
        finally
        {
            libjq.jv_free(value);
        }

        Assert.Equal(0, storage.Refcnt.Count);
    }

    [Theory]
    [InlineData("json", "[1]", false)]
    [InlineData("text", "\"value\"", true)]
    [InlineData("csv", "[\"value\"]", false)]
    [InlineData("tsv", "[\"value\"]", false)]
    [InlineData("html", "\"value\"", false)]
    [InlineData("uri", "\"value\"", false)]
    [InlineData("urid", "\"value\"", false)]
    [InlineData("sh", "\"value\"", false)]
    [InlineData("base64", "\"value\"", false)]
    [InlineData("base64d", "\"dmFsdWU=\"", false)]
    public void DirectFormatBranchesMoveInputAndConsumeSelectorExactlyOnce(
        string format,
        string inputJson,
        bool returnsInputAllocation)
    {
        using var jq = libjq.jq_init();
        var input = libjq.jv_parse(inputJson);
        var retainedInput = libjq.jv_copy(input);
        var selector = libjq.jv_string(format);
        var retainedSelector = libjq.jv_copy(selector);
        var result = libjq.jv_invalid();
        var inputStorage = input.Value;
        try
        {
            result = FormatFunction(jq, input, selector);
            input = libjq.jv_invalid();
            selector = libjq.jv_invalid();

            Assert.True(result.IsValid);
            Assert.Equal(1, libjq.jv_get_refcnt(retainedSelector));
            Assert.Equal(returnsInputAllocation ? 2 : 1, libjq.jv_get_refcnt(retainedInput));
            Assert.Equal(returnsInputAllocation, ReferenceEquals(inputStorage, result.Value));

            libjq.jv_free(result);
            result = libjq.jv_invalid();
            Assert.Equal(1, libjq.jv_get_refcnt(retainedInput));
        }
        finally
        {
            libjq.jv_free(result);
            libjq.jv_free(selector);
            libjq.jv_free(input);
            libjq.jv_free(retainedSelector);
            libjq.jv_free(retainedInput);
        }
    }

    [Theory]
    [InlineData("csv", "[{}]")]
    [InlineData("sh", "[{}]")]
    [InlineData("urid", "\"%FX\"")]
    [InlineData("base64d", "\"QUJDa\"")]
    [InlineData("nope", "[1]")]
    public void DirectFormatErrorsReleaseInputAndSelectorOwners(
        string format,
        string inputJson)
    {
        using var jq = libjq.jq_init();
        var input = libjq.jv_parse(inputJson);
        var retainedInput = libjq.jv_copy(input);
        var selector = libjq.jv_string(format);
        var retainedSelector = libjq.jv_copy(selector);
        var result = libjq.jv_invalid();
        try
        {
            result = FormatFunction(jq, input, selector);
            input = libjq.jv_invalid();
            selector = libjq.jv_invalid();

            Assert.False(result.IsValid);
            Assert.True(libjq.jv_invalid_has_msg(libjq.jv_copy(result)));
            Assert.Equal(1, libjq.jv_get_refcnt(retainedInput));
            Assert.Equal(1, libjq.jv_get_refcnt(retainedSelector));
        }
        finally
        {
            libjq.jv_free(result);
            libjq.jv_free(selector);
            libjq.jv_free(input);
            libjq.jv_free(retainedSelector);
            libjq.jv_free(retainedInput);
        }
    }

    [Fact]
    public async Task EveryFormatBranchAndErrorFamilyMatchesPinnedJq182()
    {
        if (!JqOracle.TryResolveExecutable(out _))
        {
            return;
        }

        const string filter = """
            ("!()<>&'\"\t" | format("text")),
            ({"literal":123456789012345678901234567890,"unicode":"μ😎"} | format("json")),
            ([null,true,false,nan,1.25,"a\"b","x\u0000y"] | format("csv")),
            ([null,true,false,nan,1.25,"a\tb\r\nc\\d","x\u0000y"] | format("tsv")),
            ("<&>'\"\u0000" | format("html")),
            ("a μ 😎\u0000" | format("uri")),
            ("a%20%CE%BC%20%F0%9F%98%8E%00" | format("urid")),
            ([null,true,false,2,"O'Hara","x\u0000y"] | format("sh")),
            ("foóbar\n\u0000" | format("base64")),
            ("4UFB" | format("base64d")),
            (try ("abc%" | format("urid")) catch .),
            (try ("QUJDa" | format("base64d")) catch .),
            (try ([{}] | format("csv")) catch .),
            (try ({} | format("sh")) catch .),
            (try (1 | format("nope\u0000json")) catch .),
            (try (1 | format(0)) catch .)
            """;

        var oracle = await JqOracle.ExecuteAsync(
            filter,
            "null",
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(oracle.Succeeded, oracle.StandardError);

        var managed = JqProgram.Compile(filter)
            .Execute("null")
            .Select(static value => value.GetRawText())
            .ToArray();
        Assert.Equal(oracle.OutputLines, managed);
    }

    private static string Format(string format, string inputJson)
    {
        var outcome = InvokeFormat(format, inputJson);
        Assert.True(outcome.Succeeded, outcome.Text);
        return outcome.Text;
    }

    private static FormatOutcome InvokeFormat(string format, string inputJson)
    {
        using var jq = libjq.jq_init();
        var input = libjq.jv_parse(inputJson);
        var selector = libjq.jv_string(format);
        var result = libjq.jv_invalid();
        try
        {
            result = FormatFunction(jq, input, selector);
            input = libjq.jv_invalid();
            selector = libjq.jv_invalid();
            if (result.IsValid)
            {
                return new FormatOutcome(true, result.StringValue);
            }

            var invalid = result;
            result = libjq.jv_invalid();
            var message = libjq.jv_invalid_get_msg(invalid);
            try
            {
                return new FormatOutcome(false, message.StringValue);
            }
            finally
            {
                libjq.jv_free(message);
            }
        }
        finally
        {
            libjq.jv_free(result);
            libjq.jv_free(selector);
            libjq.jv_free(input);
        }
    }

    private static cfunction_a2 FormatFunction =>
        Assert.Single(libjq.function_list, static function => function.name == "format").fptr.a2!;

    private readonly record struct FormatOutcome(bool Succeeded, string Text);
}
