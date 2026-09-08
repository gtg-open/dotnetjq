using System.Text.Json;
using DotNetJq.Compatibility.FileSystem;
using DotNetJq.Port;

namespace DotNetJq.Tests;

/// <summary>
/// Direct managed coverage for library semantics that upstream exercises from
/// <c>tests/shtest</c> and <c>tests/utf8test</c> through the jq executable.
/// CLI option parsing and byte-stream presentation remain outside this class.
/// </summary>
public sealed class CliFixtureLibrarySemanticsTests
{
    [Fact]
    public void ShtestCompileArgumentsExecuteAsNamedAndArgsValues()
    {
        var named = libjq.jv_object(
        [
            KeyValuePair.Create("foo", libjq.jv_string("1")),
            KeyValuePair.Create("bar", libjq.jv_number(2)),
        ]);
        var positional = libjq.jv_array(
        [
            libjq.jv_string("foo"),
            libjq.jv_string("bar"),
        ]);
        var argsValue = libjq.jv_object(
        [
            KeyValuePair.Create("named", named),
            KeyValuePair.Create("positional", positional),
        ]);
        var arguments = libjq.jv_object(
        [
            KeyValuePair.Create("foo", libjq.jv_string("1")),
            KeyValuePair.Create("bar", libjq.jv_number(2)),
            KeyValuePair.Create("ARGS", argsValue),
        ]);

        jq_state? state = libjq.jq_init();
        try
        {
            Assert.Equal(
                1,
                libjq.jq_compile_args(
                    state,
                    "[{$foo,$bar}, {$foo,$bar} == $ARGS.named, $ARGS.positional]",
                    arguments));
            libjq.jq_start(state, libjq.jv_null(), 0);

            var result = libjq.jq_next(state);
            Assert.Equal(
                "[{\"foo\":\"1\",\"bar\":2},true,[\"foo\",\"bar\"]]",
                libjq.jv_dump_string(result));
            Assert.False(libjq.jv_is_valid(libjq.jq_next(state)));
        }
        finally
        {
            libjq.jq_teardown(ref state);
        }
    }

    [Fact]
    public void ShtestSlurpSemanticsParseAdjacentJsonValues()
    {
        var fileSystem = new JqInMemoryFileSystem(
            new Dictionary<string, string>
            {
                ["/adjacent.json"] = "[1,2][3,4]",
            });

        var values = libjq.jv_load_file(fileSystem, "/adjacent.json", raw: 0);

        Assert.Equal("[[1,2],[3,4]]", libjq.jv_dump_string(values));
    }

    [Theory]
    [InlineData('\u0001')]
    [InlineData('\u0002')]
    [InlineData('\n')]
    [InlineData('\u001e')]
    [InlineData('\u001f')]
    public void ShtestJsonParserRejectsRawC0ControlCharacters(char control)
    {
        var message = TakeInvalidMessage(libjq.jv_parse("\"a" + control + "b\""));

        Assert.Contains("control character", message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("\"a\n")]
    [InlineData("foobar")]
    public void ShtestMalformedJsonInputsFailThroughTheLibraryParser(string json)
    {
        Assert.NotEmpty(TakeInvalidMessage(libjq.jv_parse(json)));
    }

    [Fact]
    public void ShtestJsonParserAcceptsSpaceTildeAndDelete()
    {
        var value = libjq.jv_parse("\" \u007e\u007f\"");

        Assert.Equal("\" ~\\u007f\"", libjq.jv_dump_string(value));
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

    [Fact]
    public void ShtestProgramSourceNulIsNotSilentlyTruncated()
    {
        _ = Assert.Throws<JqCompileException>(() => JqProgram.Compile(".\0invalid"));
    }

    [Theory]
    [InlineData("import \"a\\u0000b\" as a; null")]
    [InlineData("include \"a\\u0000b\"; null")]
    public void ShtestNulModulePathsAreRejectedBeforeFilesystemAccess(string source)
    {
        var resolver = new JqModuleResolver(
            JqDenyAllFileSystem.Instance,
            ["/modules"],
            "/program");

        var exception = Assert.Throws<JqCompileException>(
            () => JqProgram.Compile(source, resolver));

        Assert.Contains("NUL byte", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ShtestNulModulemetaPathIsRejected()
    {
        var resolver = new JqModuleResolver(
            JqDenyAllFileSystem.Instance,
            ["/modules"],
            "/program");

        var error = Assert.Single(
            JqProgram
                .Compile("try (\"a\\u0000b\" | modulemeta) catch .", resolver)
                .Execute("null"));

        Assert.Contains("NUL byte", error.GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ShtestSelfImportIsRejectedAsCircular()
    {
        var resolver = new JqModuleResolver(
            new JqInMemoryFileSystem(
                new Dictionary<string, string>
                {
                    ["/modules/cycle_self.jq"] =
                        "import \"cycle_self\" as self; def value: null;",
                }),
            ["/modules"],
            "/program");

        var exception = Assert.Throws<JqCompileException>(
            () => JqProgram.Compile("import \"cycle_self\" as self; null", resolver));

        Assert.Contains("circular import", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1 # trailing comment", "1")]
    [InlineData("1 # carriage return ends nothing\r + 2", "1")]
    [InlineData("1\r\n+\r\n2", "3")]
    public void ShtestCrLfAndCommentBoundariesMatchTheLanguage(string source, string expected)
    {
        var output = Assert.Single(JqProgram.Compile(source).Execute("null"));

        Assert.Equal(expected, output.GetRawText());
    }

    [Fact]
    public void ShtestBackslashContinuedCommentsConsumeTheFollowingPhysicalLine()
    {
        const string source = """
            [
              1,
              # foo \
              2,
              # bar \\
              3,
              4, # baz \\\
              5, \
              6,
              7
              # comment \
                comment \
                comment
            ]
            """;

        var output = Assert.Single(JqProgram.Compile(source).Execute("null"));

        Assert.Equal("[1,3,4,7]", output.GetRawText());
    }

    [Fact]
    public void ShtestHaltErrorPreservesEmbeddedNulAndStructuredMessageValues()
    {
        var nul = JqProgram
            .Compile("\"x\\u0000y\\u0000z\" | halt_error(1)")
            .ExecuteDetailed("null");
        var structured = JqProgram
            .Compile("{a:\"xyz\"} | halt_error(1)")
            .ExecuteDetailed("null");

        Assert.Equal("x\0y\0z", AssertPresent(nul.Outcome.HaltMessage).GetString());
        Assert.Equal(
            "{\"a\":\"xyz\"}",
            AssertPresent(structured.Outcome.HaltMessage).GetRawText());
    }

    [Fact]
    public void ShtestUtcAndLocalTimeFormattingArePureAcrossRepeatedCalls()
    {
        var options = new JqExecutionOptions
        {
            Environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["TZ"] = "Etc/GMT+7",
            },
        };
        const string source =
            ".,. | [strftime(\"%FT%T\"),strflocaltime(\"%FT%T%z\")]";

        var output = JqProgram.Compile(source).Execute("1731627341", options);

        Assert.Equal(2, output.Count);
        Assert.All(
            output,
            value => Assert.Equal(
                "[\"2024-11-14T23:35:41\",\"2024-11-14T16:35:41-0700\"]",
                value.GetRawText()));
    }

    [Theory]
    [InlineData("Asia/Tokyo", "1731627341", "2024-11-15 08:35:41 +0900 JST")]
    [InlineData("Europe/Paris", "1731627341", "2024-11-15 00:35:41 +0100 CET")]
    [InlineData("Europe/Paris", "1750500000", "2025-06-21 12:00:00 +0200 CEST")]
    public void ShtestExplicitTimeZoneAndDstCasesMatchJq182(
        string timeZone,
        string input,
        string expected)
    {
        var options = new JqExecutionOptions
        {
            Environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["TZ"] = timeZone,
            },
        };

        var output = Assert.Single(
            JqProgram
                .Compile("strflocaltime(\"%F %T %z %Z\")")
                .Execute(input, options));

        Assert.Equal(expected, output.GetString());
    }

    [Theory]
    [InlineData("Asia/Tokyo", "1731627341", "[2024,10,15,8,35,41,5,319]")]
    [InlineData("Europe/Paris", "1750500000", "[2025,5,21,12,0,0,6,171]")]
    public void ShtestExplicitTimeZoneAlsoControlsLocaltime(
        string timeZone,
        string input,
        string expected)
    {
        var options = new JqExecutionOptions
        {
            Environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["TZ"] = timeZone,
            },
        };

        var output = Assert.Single(JqProgram.Compile("localtime").Execute(input, options));

        Assert.Equal(expected, output.GetRawText());
    }

    [Fact]
    public void ShtestUtcStrftimeIgnoresTheSelectedLocalTimeZone()
    {
        var options = new JqExecutionOptions
        {
            Environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["TZ"] = "Europe/Paris",
            },
        };

        var output = Assert.Single(
            JqProgram
                .Compile("strftime(\"%F %T %z %Z\")")
                .Execute("1731627341", options));

        Assert.Equal("2024-11-14 23:35:41 +0000 GMT", output.GetString());
    }

    [Fact]
    public void ShtestUnavailableLcAllLeavesNativeCLocaleInPlace()
    {
        var options = new JqExecutionOptions
        {
            Environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["LC_ALL"] = "fr_FR.UTF-8",
                ["TZ"] = "UTC",
            },
        };

        var output = Assert.Single(
            JqProgram
                .Compile("strflocaltime(\"%a %d %b %Y at %H:%M:%S\")")
                .Execute("1731627341", options));

        // The pinned test host intentionally has no fr_FR locale installed.
        // jq's failed setlocale request therefore leaves its C locale active;
        // ICU still knows French, so using CultureInfo alone would diverge.
        Assert.Equal("Thu 14 Nov 2024 at 23:35:41", output.GetString());
    }

    [Theory]
    [InlineData(
        "[]",
        "[\"1899\",\"1899-12-31\",\"18\",\"1899\",\"99\",\"52\",\"365\",\"0\",\"7\"]")]
    [InlineData(
        "[0]",
        "[\"-1\",\"-1-12-31\",\"-1\",\"-1\",\"99\",\"52\",\"365\",\"5\",\"5\"]")]
    [InlineData(
        "[1]",
        "[\"0\",\"0-12-31\",\"0\",\"0\",\"00\",\"52\",\"366\",\"0\",\"7\"]")]
    [InlineData(
        "[1,2,3]",
        "[\"1\",\"1-03-03\",\"0\",\"1\",\"01\",\"09\",\"062\",\"6\",\"6\"]")]
    [InlineData(
        "[3,1,2,1]",
        "[\"3\",\"3-02-02\",\"0\",\"3\",\"03\",\"05\",\"033\",\"0\",\"7\"]")]
    [InlineData(
        "9007199254740991",
        "[\"285428751\",\"285428751-11-12\",\"2854287\",\"285428751\",\"51\",\"46\",\"316\",\"1\",\"1\"]")]
    [InlineData(
        "9007199254740992",
        "[\"285428751\",\"285428751-11-12\",\"2854287\",\"285428751\",\"51\",\"46\",\"316\",\"1\",\"1\"]")]
    public void StrftimeNormalizesProlepticAndTimeTCalendarEdgesLikeJq182(
        string input,
        string expected)
    {
        const string source =
            "[strftime(\"%Y\"),strftime(\"%F\"),strftime(\"%C\")," +
            "strftime(\"%G\"),strftime(\"%g\"),strftime(\"%V\")," +
            "strftime(\"%j\"),strftime(\"%w\"),strftime(\"%u\")]";

        var output = Assert.Single(JqProgram.Compile(source).Execute(input));

        Assert.Equal(expected, output.GetRawText());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Utf8testProgramSourcePreservesLongFourByteCharacters(int asciiPrefixLength)
    {
        var value = string.Concat(
            new string('0', asciiPrefixLength),
            string.Concat(Enumerable.Repeat("😃", 3_000)));
        var source = JsonSerializer.Serialize(value) + " | explode | unique == [48,128515]";

        var output = Assert.Single(JqProgram.Compile(source).Execute("null"));

        Assert.True(output.GetBoolean());
    }

    private static JsonElement AssertPresent(JsonElement? value)
    {
        Assert.True(value.HasValue);
        return value.GetValueOrDefault();
    }
}
