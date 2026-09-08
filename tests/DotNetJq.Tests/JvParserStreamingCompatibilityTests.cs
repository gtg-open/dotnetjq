using System.Text;
using DotNetJq.Port;

namespace DotNetJq.Tests;

public sealed class JvParserStreamingCompatibilityTests
{
    [Fact]
    public void SequentialParserReturnsEveryAdjacentTopLevelValue()
    {
        var result = Parse(0, Encoding.UTF8.GetBytes("[1][2]true\"ab\"{}false"));

        Assert.Equal(["[1]", "[2]", "true", "\"ab\"", "{}", "false"], result.Values);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void AdjacentValuesSurviveEverySingleByteChunkBoundary()
    {
        var bytes = Encoding.UTF8.GetBytes("[1][2]\"μ😃\"null\n");
        var chunks = bytes.Select(static value => new[] { value }).ToArray();

        var result = Parse(0, chunks);

        Assert.Equal(["[1]", "[2]", "\"μ😃\"", "null"], result.Values);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void SplitBomUtf8AndEscapesArePreservedAcrossPartialBuffers()
    {
        var bytes = new byte[]
        {
            0xef, 0xbb, 0xbf,
            (byte)'[', (byte)'"', 0xce, 0xbc, 0xf0, 0x9f, 0x98, 0x83,
            (byte)'"', (byte)',', (byte)'"', (byte)'\\', (byte)'u', (byte)'d', (byte)'8',
            (byte)'3', (byte)'d', (byte)'\\', (byte)'u', (byte)'d', (byte)'e', (byte)'0',
            (byte)'3', (byte)'"', (byte)']',
        };

        var result = Parse(0, bytes.Select(static value => new[] { value }).ToArray());

        Assert.Equal(["[\"μ😃\",\"😃\"]"], result.Values);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void MalformedUtf8InAStringUsesJvStringRepairAtCompletion()
    {
        var bytes = new byte[] { (byte)'"', 0xf0, 0x28, 0x8c, 0x28, (byte)'"' };

        var result = Parse(0, bytes.Select(static value => new[] { value }).ToArray());

        Assert.Equal(["\"�(�(\""], result.Values);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void StreamModeEmitsLeafAndContainerEndEventsInUpstreamOrder()
    {
        var result = Parse(
            libjq.JV_PARSE_STREAMING,
            Encoding.UTF8.GetBytes("[1,{\"a\":2},[],{}]"));

        Assert.Equal(
            [
                "[[0],1]",
                "[[1,\"a\"],2]",
                "[[1,\"a\"]]",
                "[[2],[]]",
                "[[3],{}]",
                "[[3]]",
            ],
            result.Values);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void StreamModeKeepsAdjacentRootContainerEndMarkersDistinct()
    {
        var result = Parse(
            libjq.JV_PARSE_STREAMING,
            Encoding.UTF8.GetBytes("[1][2]\n").Select(static value => new[] { value }).ToArray());

        Assert.Equal(["[[0],1]", "[[0]]", "[[0],2]", "[[0]]"], result.Values);
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData("1", "[[],1]")]
    [InlineData("{}", "[[],{}]")]
    [InlineData("[]", "[[],[]]")]
    public void StreamModeRepresentsRootLeavesWithAnEmptyPath(string input, string expected)
    {
        var result = Parse(libjq.JV_PARSE_STREAMING, Encoding.UTF8.GetBytes(input));

        Assert.Equal([expected], result.Values);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void StreamModeEmitsNestedContainerEndEventsAtTheirLastPath()
    {
        var result = Parse(
            libjq.JV_PARSE_STREAMING,
            Encoding.UTF8.GetBytes("{\"a\":[1,2]}"));

        Assert.Equal(
            ["[[\"a\",0],1]", "[[\"a\",1],2]", "[[\"a\",1]]", "[[\"a\"]]"],
            result.Values);
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData("[", "[\"Unfinished JSON term at EOF at line 1, column 1\",[0]]")]
    [InlineData("{\"a\":1,\"b\",", "[\"Objects must consist of key:value pairs at line 1, column 11\",[null]]")]
    [InlineData("{{\"a\":\"b\"}}", "[\"Expected string key after '{', not '{' at line 1, column 2\",[null]]")]
    [InlineData("{\"x\":\"y\",{\"a\":\"b\"}}", "[\"Expected string key after ',' in object, not '{' at line 1, column 10\",[null]]")]
    [InlineData("{[\"a\",\"b\"]}", "[\"Expected string key after '{', not '[' at line 1, column 2\",[null]]")]
    [InlineData("{\"x\":\"y\",[\"a\",\"b\"]}", "[\"Expected string key after ',' in object, not '[' at line 1, column 10\",[null]]")]
    public void StreamErrorsAreValuesContainingExactMessageAndCurrentPath(
        string input,
        string expectedLastValue)
    {
        var result = Parse(
            libjq.JV_PARSE_STREAMING | libjq.JV_PARSE_STREAM_ERRORS,
            Encoding.UTF8.GetBytes(input));

        Assert.Equal(expectedLastValue, result.Values[^1]);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void JsonSequenceMatchesOfficialRecoveryAndTruncationFixture()
    {
        var bytes = Encoding.UTF8.GetBytes(
            "1\u001e2 3\n[0,1\u001e[4,5]true\"ab\"{\"c\":4\u001e{}" +
            "{\"d\":5,\"e\":6\"\u001efalse\n");

        var result = Parse(
            libjq.JV_PARSE_SEQ,
            bytes.Select(static value => new[] { value }).ToArray());

        Assert.Equal(["2", "3", "[4,5]", "true", "\"ab\"", "{}", "false"], result.Values);
        Assert.Equal(
            [
                "Truncated value at line 2, column 5",
                "Truncated value at line 2, column 25",
                "Truncated value at line 2, column 41",
            ],
            result.Errors);
    }

    [Fact]
    public void JsonSequenceResynchronizesAfterSyntaxError()
    {
        var result = Parse(
            libjq.JV_PARSE_SEQ,
            Encoding.UTF8.GetBytes("\u001e{\"a\":]\u001etrue\n"));

        Assert.Equal(["true"], result.Values);
        Assert.Equal(
            ["Unmatched ']' at line 1, column 7 (need RS to resync)"],
            result.Errors);
    }

    [Fact]
    public void SequenceStreamErrorsRetainTheInterruptedPathAndResumeAtNextRecord()
    {
        var result = Parse(
            libjq.JV_PARSE_SEQ | libjq.JV_PARSE_STREAMING | libjq.JV_PARSE_STREAM_ERRORS,
            Encoding.UTF8.GetBytes("\u001e[1]\n\u001e[2,\u001e{\"a\":3}\n"));

        Assert.Equal(
            [
                "[[0],1]",
                "[[0]]",
                "[[0],2]",
                "[\"Truncated value at line 2, column 5\",[1]]",
                "[[\"a\"],3]",
                "[[\"a\"]]",
            ],
            result.Values);
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData("\"foo", "Unfinished abandoned text at EOF at line 1, column 4")]
    [InlineData("1", "Unfinished abandoned text at EOF at line 1, column 1")]
    [InlineData("\u001e1", "Potentially truncated top-level numeric value at EOF at line 1, column 2")]
    public void JsonSequenceEofCategoriesMatchUpstream(string input, string expectedError)
    {
        var result = Parse(libjq.JV_PARSE_SEQ, Encoding.UTF8.GetBytes(input));

        Assert.Empty(result.Values);
        Assert.Equal([expectedError], result.Errors);
    }

    [Fact]
    public void PartialBufferSignalsNeedMoreAndTracksRemainingBytesExactly()
    {
        var parser = libjq.jv_parser_new(0);
        try
        {
            libjq.jv_parser_set_buf(parser, Encoding.UTF8.GetBytes("[\"μ"), 3, 1);
            var needMore = libjq.jv_parser_next(parser);

            Assert.False(needMore.IsValid);
            Assert.False(libjq.jv_invalid_has_msg(needMore));
            Assert.Equal(0, libjq.jv_parser_remaining(parser));

            var suffix = new byte[] { 0xbc, (byte)'"', (byte)']' };
            libjq.jv_parser_set_buf(parser, suffix, suffix.Length, 0);
            var value = libjq.jv_parser_next(parser);

            Assert.Equal("[\"μ\"]", libjq.jv_dump_string(value));
            Assert.Equal(0, libjq.jv_parser_remaining(parser));
            Assert.False(libjq.jv_parser_next(parser).IsValid);
        }
        finally
        {
            libjq.jv_parser_free(parser);
        }
    }

    [Fact]
    public void CompletedValueLeavesTheFollowingValueInTheCurrentBuffer()
    {
        var parser = libjq.jv_parser_new(0);
        try
        {
            var bytes = Encoding.UTF8.GetBytes("[1][2]");
            libjq.jv_parser_set_buf(parser, bytes, bytes.Length, 0);

            Assert.Equal("[1]", libjq.jv_dump_string(libjq.jv_parser_next(parser)));
            Assert.Equal(3, libjq.jv_parser_remaining(parser));
            Assert.Equal("[2]", libjq.jv_dump_string(libjq.jv_parser_next(parser)));
            Assert.Equal(0, libjq.jv_parser_remaining(parser));

            var end = libjq.jv_parser_next(parser);
            Assert.False(end.IsValid);
            Assert.False(libjq.jv_invalid_has_msg(end));
        }
        finally
        {
            libjq.jv_parser_free(parser);
        }
    }

    [Fact]
    public void ErrorColumnsCountUtf8BytesAcrossChunkBoundaries()
    {
        var bytes = Encoding.UTF8.GetBytes("\"μ\" x");
        var result = Parse(0, bytes.Select(static value => new[] { value }).ToArray());

        Assert.Equal(["\"μ\""], result.Values);
        Assert.Equal(["Invalid numeric literal at EOF at line 1, column 6"], result.Errors);
    }

    [Theory]
    [InlineData("\"abc", "Unfinished string at EOF at line 1, column 4")]
    [InlineData("\"\\x\"", "Invalid escape at line 1, column 4")]
    [InlineData("x ", "Invalid numeric literal at line 1, column 2")]
    [InlineData("[1 2]", "Expected separator between values at line 1, column 5")]
    public void JsonErrorCategoriesAndLocationsMatchJvParser(
        string input,
        string expectedError)
    {
        var result = Parse(0, Encoding.UTF8.GetBytes(input));

        Assert.Equal([expectedError], result.Errors);
    }

    [Fact]
    public void StreamErrorAtEofContainsTheDeepCurrentPath()
    {
        var result = Parse(
            libjq.JV_PARSE_STREAMING | libjq.JV_PARSE_STREAM_ERRORS,
            Encoding.UTF8.GetBytes("[{\"a\":["));

        Assert.Equal(
            ["[\"Unfinished JSON term at EOF at line 1, column 7\",[0,\"a\",0]]"],
            result.Values);
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData(0, "[1][2]\"μ😃\"null\n")]
    [InlineData(libjq.JV_PARSE_STREAMING, "[{\"a\":[1,2]},[],true]\n")]
    [InlineData(
        libjq.JV_PARSE_SEQ | libjq.JV_PARSE_STREAMING | libjq.JV_PARSE_STREAM_ERRORS,
        "\u001e[1]\n\u001e[2,\u001e{\"a\":3}\n")]
    public void EveryTwoChunkSplitMatchesTheContiguousParserResult(int flags, string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var contiguous = Parse(flags, bytes);

        for (var split = 1; split < bytes.Length; split++)
        {
            var chunked = Parse(flags, bytes[..split], bytes[split..]);
            Assert.Equal(contiguous.Values, chunked.Values);
            Assert.Equal(contiguous.Errors, chunked.Errors);
        }
    }

    [Fact]
    public void NonStreamingErrorsRemainInvalidValuesWithMessages()
    {
        var result = Parse(0, Encoding.UTF8.GetBytes("["));

        Assert.Empty(result.Values);
        Assert.Equal(["Unfinished JSON term at EOF at line 1, column 1"], result.Errors);
    }

    private static ParseResult Parse(int flags, params byte[][] chunks)
    {
        var parser = libjq.jv_parser_new(flags);
        var values = new List<string>();
        var errors = new List<string>();
        try
        {
            for (var chunkIndex = 0; chunkIndex < chunks.Length; chunkIndex++)
            {
                libjq.jv_parser_set_buf(
                    parser,
                    chunks[chunkIndex],
                    chunks[chunkIndex].Length,
                    chunkIndex + 1 == chunks.Length ? 0 : 1);

                while (true)
                {
                    var value = libjq.jv_parser_next(parser);
                    if (value.IsValid)
                    {
                        values.Add(libjq.jv_dump_string(value));
                        continue;
                    }

                    if (libjq.jv_invalid_has_msg(libjq.jv_copy(value)))
                    {
                        var message = libjq.jv_invalid_get_msg(value);
                        try
                        {
                            errors.Add(message.StringValue);
                        }
                        finally
                        {
                            libjq.jv_free(message);
                        }
                        continue;
                    }

                    libjq.jv_free(value);

                    // A sequence record separator can complete no value while bytes remain.
                    // Upstream jq_util_input_next_input() calls jv_parser_next() again until
                    // the buffer is exhausted before asking the host for another chunk.
                    if (libjq.jv_parser_remaining(parser) != 0)
                    {
                        continue;
                    }

                    break;
                }

                Assert.Equal(0, libjq.jv_parser_remaining(parser));
            }
        }
        finally
        {
            libjq.jv_parser_free(parser);
        }

        return new ParseResult(values, errors);
    }

    private sealed record ParseResult(IReadOnlyList<string> Values, IReadOnlyList<string> Errors);
}
