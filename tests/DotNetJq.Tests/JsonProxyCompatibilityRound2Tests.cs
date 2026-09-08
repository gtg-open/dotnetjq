using System.Text;
using DotNetJq.Port;

namespace DotNetJq.Tests;

public sealed class JsonProxyCompatibilityRound2Tests
{
    [Theory]
    [InlineData("1", "1")]
    [InlineData("1.000", "1.000")]
    [InlineData("100e-2", "1.00")]
    [InlineData("0.0001e4", "1")]
    [InlineData("-0", "-0")]
    [InlineData("-0.0", "-0.0")]
    [InlineData("1E+1000", "1E+1000")]
    [InlineData("1e1000000000", "1.7976931348623157e+308")]
    [InlineData("-Infinity", "-1.7976931348623157e+308")]
    [InlineData("nan", "null")]
    [InlineData("NaN00", "null")]
    public void NumericParsingAndPrintingMatchesJqLiteralRules(string input, string expected)
    {
        Assert.Equal(expected, libjq.jv_dump_string(libjq.jv_parse(input)));
    }

    [Fact]
    public void NestedNamedNumbersAndDuplicateKeysMatchJq()
    {
        var value = libjq.jv_parse("{\"a\":1,\"a\":[nan,NaN,-NaN,Infinity,-Infinity]}");

        Assert.Equal(
            "{\"a\":[null,null,null,1.7976931348623157e+308,-1.7976931348623157e+308]}",
            libjq.jv_dump_string(value));
    }

    [Fact]
    public void UpstreamTortureFixtureNestedValueRoundTrips()
    {
        const string input = "{\"a\":[{\"b\":[]},{},[2]]}";

        Assert.Equal(input, libjq.jv_dump_string(libjq.jv_parse(input)));
    }

    [Fact]
    public void StringEscapingMatchesCompactJqOutput()
    {
        var value = libjq.jv_parse("{\"control\":\"\\u0000\\b\\t\\n\\f\\r\\u007f\",\"unicode\":\"\\u03bc\\ud83d\\ude03\",\"slash\":\"a/b\"}");

        Assert.Equal(
            "{\"control\":\"\\u0000\\b\\t\\n\\f\\r\\u007f\",\"unicode\":\"μ😃\",\"slash\":\"a/b\"}",
            libjq.jv_dump_string(value));
    }

    [Fact]
    public void LoneLowSurrogateIsReplacedLikeJq()
    {
        Assert.Equal("\"�\"", libjq.jv_dump_string(libjq.jv_parse("\"\\udc00\"")));
    }

    [Fact]
    public void LongFourByteUtf8ContentSurvivesParseAndPrint()
    {
        var payload = string.Concat("0000", string.Concat(Enumerable.Repeat("😃", 3_000)));
        var json = string.Concat("\"", payload, "\"");

        Assert.Equal(json, libjq.jv_dump_string(libjq.jv_parse(json)));
    }

    [Theory]
    [InlineData("\"\\ud800\"")]
    [InlineData("\"\\ud800x\"")]
    [InlineData("\"\\x\"")]
    [InlineData("[1,]")]
    [InlineData("{\"a\":1,}")]
    [InlineData("NaN1")]
    public void MalformedInputUsesTheCanonicalSizedParserDiagnostic(string input)
    {
        var error = TakeInvalidMessage(libjq.jv_parse(input));

        Assert.Contains("line", error, StringComparison.Ordinal);
        Assert.EndsWith($"(while parsing '{input}')", error, StringComparison.Ordinal);
        Assert.True(error.Length < 180);
    }

    [Fact]
    public void InvalidParseResultOwnsAndTransfersTheExactUpstreamMessage()
    {
        var invalid = libjq.jv_parse("{\"a':\"12\"}");
        var invalidStorage = Assert.IsType<jvp_invalid>(invalid.Value);

        Assert.Equal(1, invalidStorage.Refcnt.Count);
        var message = libjq.jv_invalid_get_msg(invalid);
        var messageStorage = Assert.IsType<jvp_string>(message.Value);
        try
        {
            Assert.Equal(0, invalidStorage.Refcnt.Count);
            Assert.Equal(1, messageStorage.Refcnt.Count);
            Assert.Equal(
                "Expected separator between values at line 1, column 9 " +
                "(while parsing '{\"a':\"12\"}')",
                message.StringValue);
        }
        finally
        {
            libjq.jv_free(message);
        }

        Assert.Equal(0, messageStorage.Refcnt.Count);
    }

    [Fact]
    public void StringConvenienceUsesCPrefixWhileSizedParsingPreservesNul()
    {
        Assert.Equal("{}", libjq.jv_dump_string(libjq.jv_parse("{}\0[not parsed")));

        var bytes = Encoding.UTF8.GetBytes("{}\0");
        var error = TakeInvalidMessage(libjq.jv_parse_sized(bytes));

        Assert.Equal(
            "Invalid numeric literal at EOF at line 1, column 3 (while parsing '{}')",
            error);
    }

    [Fact]
    public void StringConvenienceRepairsMalformedUtf16BeforeByteParsing()
    {
        var high = string.Concat('"', new string((char)0xd800, 1), '"');
        var low = string.Concat('"', new string((char)0xdc00, 1), '"');

        Assert.Equal("\"�\"", libjq.jv_dump_string(libjq.jv_parse(high)));
        Assert.Equal("\"�\"", libjq.jv_dump_string(libjq.jv_parse(low)));
    }

    [Fact]
    public void ParserSourceHasOneCanonicalScannerAndJqShapedPrimitiveRoutes()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "DotNetJq",
            "Port",
            "src",
            "jv_parse.c.cs"));

        Assert.DoesNotContain("JvJsonParser", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ManagedDecimal.TryParse", source, StringComparison.Ordinal);
        Assert.Contains("return jv_parse_sized(bytes);", source, StringComparison.Ordinal);
        Assert.Contains("jv_number_with_literal(literal)", source, StringComparison.Ordinal);
        Assert.Contains("jvp_utf8_encode(codepoint, encoded)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ParserAcceptsJqMaximumContainerDepthWithoutManagedRecursion()
    {
        var input = string.Concat(new string('[', 10_000), "0", new string(']', 10_000));

        Assert.Equal(input, libjq.jv_dump_string(libjq.jv_parse(input)));
    }

    [Fact]
    public void ParserRejectsBeyondJqMaximumContainerDepth()
    {
        var input = string.Concat(new string('[', 10_001), "0", new string(']', 10_001));

        var error = TakeInvalidMessage(libjq.jv_parse(input));

        Assert.Contains("exceeds depth limit for parsing", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PrinterMarksValuesBeyondJqMaximumDepthWithoutManagedRecursion()
    {
        var value = libjq.jv_number(0);
        for (var depth = 0; depth < 10_001; depth++)
        {
            value = libjq.jv_array([value]);
        }

        var text = libjq.jv_dump_string(value);

        Assert.Contains("<skipped: too deep>", text, StringComparison.Ordinal);
        Assert.StartsWith(new string('[', 10_001), text, StringComparison.Ordinal);
        Assert.EndsWith(new string(']', 10_001), text, StringComparison.Ordinal);
    }

    [Fact]
    public void ByteOrderMarkIsAcceptedOnlyAtTheBeginning()
    {
        Assert.Equal("\"ok\"", libjq.jv_dump_string(libjq.jv_parse("\uFEFF\"ok\"")));
        Assert.Contains(
            "Invalid numeric literal",
            TakeInvalidMessage(libjq.jv_parse(" \uFEFF\"not-first\"")),
            StringComparison.Ordinal);
    }

    private static string TakeInvalidMessage(jv value)
    {
        Assert.False(value.IsValid);
        Assert.True(libjq.jv_invalid_has_msg(libjq.jv_copy(value)));
        var message = libjq.jv_invalid_get_msg(value);
        try
        {
            return message.StringValue;
        }
        finally
        {
            libjq.jv_free(message);
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DotNetJq.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("DotNetJq repository root not found.");
    }
}
