using System.Text;
using DotNetJq.Port;

namespace DotNetJq.Tests;

public sealed class LocfileDirectPortCompatibilityTests
{
    [Fact]
    public void UnknownLocationUsesTheExactJqErrorPrefixAndReportsThroughTheCallback()
    {
        var reported = new List<jv>();
        var source = Encoding.UTF8.GetBytes(".");
        var file = libjq.locfile_init(reported.Add, "<top-level>", source, source.Length);

        try
        {
            Assert.Equal(
                "jq: error: missing/0 is not defined",
                libjq.locfile_format_location(
                    file,
                    libjq.UNKNOWN_LOCATION,
                    "%s/%d is not defined",
                    "missing",
                    0));

            libjq.locfile_locate(file, libjq.UNKNOWN_LOCATION, "%s", "compile failed");
            Assert.Equal("jq: error: compile failed", Assert.Single(reported).StringValue);
        }
        finally
        {
            libjq.locfile_free(file);
        }
    }

    [Fact]
    public void KnownLocationUsesUtf8ByteColumnsAndByteWidthForTheUnderline()
    {
        const string sourceText = "é$missing\nnext";
        var source = Encoding.UTF8.GetBytes(sourceText);
        var file = libjq.locfile_init((Action<jv>?)null, "module-é.jq", source, source.Length);

        try
        {
            Assert.Equal(0, libjq.locfile_get_line(file, 2));
            Assert.Equal(1, libjq.locfile_get_line(file, 12));
            Assert.Equal(
                "undefined variable at module-é.jq, line 1, column 3:\n" +
                "    é$missing\n" +
                "      ^^^^^^^^",
                libjq.locfile_format_location(file, new location(2, 10), "undefined %s", "variable"));
        }
        finally
        {
            libjq.locfile_free(file);
        }
    }

    [Fact]
    public void KnownLocationClipsTheUnderlineAtTheFirstLineBoundary()
    {
        const string sourceText = "abc\ndef";
        var source = Encoding.UTF8.GetBytes(sourceText);
        var file = libjq.locfile_init((Action<jv>?)null, "input.jq", source, source.Length);

        try
        {
            Assert.Equal(
                "bad token at input.jq, line 1, column 2:\n" +
                "    abc\n" +
                "     ^^",
                libjq.locfile_format_location(file, new location(1, source.Length), "bad token"));
        }
        finally
        {
            libjq.locfile_free(file);
        }
    }
}
