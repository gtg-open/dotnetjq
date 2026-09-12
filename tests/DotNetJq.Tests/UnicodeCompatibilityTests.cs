using System.Text;
using DotNetJq.Port;

namespace DotNetJq.Tests;

public sealed class UnicodeCompatibilityTests
{
    private static readonly int[] CombiningAndSupplementaryCodepoints = [0x61, 0x0304, 0x1F642];
    private static readonly int[] ReplacedInvalidCodepoints =
        [0xFFFD, 0, 1, 0x10FFFF, 0xFFFD, 0xFFFD, 0xE000];
    private static readonly int[] ReplacedUnpairedSurrogateCodepoints = [0x61, 0xFFFD, 0x62];

    [Fact]
    public void Utf8TraversalMatchesJqForAsciiBmpAndSupplementaryScalars()
    {
        var bytes = Encoding.UTF8.GetBytes("Aλ🙂");
        var expectedCodepoints = new[] { 0x41, 0x03BB, 0x1F642 };
        var expectedOffsets = new[] { 1, 3, 7 };
        var actualCodepoints = new List<int>();
        var actualOffsets = new List<int>();
        var offset = 0;
        var codepoint = 0;

        int? next;
        while ((next = libjq.jvp_utf8_next(bytes, offset, ref codepoint)) is not null)
        {
            actualCodepoints.Add(codepoint);
            offset = next.Value;
            actualOffsets.Add(offset);
        }

        Assert.Equal(expectedCodepoints, actualCodepoints);
        Assert.Equal(expectedOffsets, actualOffsets);
        Assert.True(libjq.jvp_utf8_is_valid(bytes));
        Assert.Equal(3, libjq.jvp_utf8_codepoint_length(bytes));
    }

    [Theory]
    [InlineData(new byte[] { 0x80 }, 1)]
    [InlineData(new byte[] { 0xC0, 0x80 }, 2)]
    [InlineData(new byte[] { 0xED, 0xA0, 0x80 }, 1)]
    [InlineData(new byte[] { 0xF4, 0x90, 0x80, 0x80 }, 1)]
    [InlineData(new byte[] { 0xE2, 0x82 }, 1)]
    public void Utf8ValidationRejectsMalformedSequences(byte[] bytes, int expectedTraversalUnits)
    {
        Assert.False(libjq.jvp_utf8_is_valid(bytes));
        Assert.Equal(expectedTraversalUnits, libjq.jvp_utf8_codepoint_length(bytes));
    }

    [Fact]
    public void Utf8NextPreservesJqMalformedSequenceAdvance()
    {
        byte[] bytes = [0xE2, 0x82, 0x41];
        var codepoint = 0;

        var next = libjq.jvp_utf8_next(bytes, 0, ref codepoint);

        Assert.Equal(libjq.JVP_UTF8_INVALID_CODEPOINT, codepoint);
        Assert.Equal(2, next);

        next = libjq.jvp_utf8_next(bytes, next!.Value, ref codepoint);
        Assert.Equal(0x41, codepoint);
        Assert.Equal(3, next);
    }

    [Fact]
    public void BacktrackReportsBytesMissingFromSplitSequence()
    {
        byte[] bytes = [0x41, 0xF0, 0x9F, 0x99];
        var missingBytes = -1;

        var start = libjq.jvp_utf8_backtrack(bytes, 3, 0, ref missingBytes);

        Assert.Equal(1, start);
        Assert.Equal(1, missingBytes);
    }

    [Fact]
    public void BacktrackLeavesMissingCountUntouchedForInvalidBytes()
    {
        byte[] bytes = [0x41, 0x80, 0x80];
        var missingBytes = 73;

        var start = libjq.jvp_utf8_backtrack(bytes, 2, 0, ref missingBytes);

        Assert.Null(start);
        Assert.Equal(73, missingBytes);
    }

    [Theory]
    [InlineData(0x0009)]
    [InlineData(0x0085)]
    [InlineData(0x00A0)]
    [InlineData(0x2007)]
    [InlineData(0x3000)]
    public void JqUnicodeWhitespaceSetIsRecognized(int codepoint)
    {
        Assert.True(libjq.jvp_codepoint_is_whitespace(codepoint));
    }

    [Theory]
    [InlineData(0x180E)]
    [InlineData(0x200B)]
    [InlineData(0xFEFF)]
    public void CharactersOutsideJqUnicodeWhitespaceSetAreRejected(int codepoint)
    {
        Assert.False(libjq.jvp_codepoint_is_whitespace(codepoint));
    }

    [Fact]
    public void ExplodeAndImplodeOperateOnUnicodeScalars()
    {
        const string input = "ā🙂";

        var exploded = libjq.jvp_utf8_explode(input);

        Assert.Equal(CombiningAndSupplementaryCodepoints, exploded);
        Assert.Equal(input, libjq.jvp_utf8_implode(exploded));
        Assert.Equal(3, libjq.jvp_utf8_codepoint_length(input));
    }

    [Fact]
    public void ImplodeTruncatesFractionsAndReplacesInvalidUnicodeScalars()
    {
        double[] input = [-1, 0, 1.9, 0x10FFFF, 0x110000, 0xD800, 0xE000];

        var output = libjq.jvp_utf8_implode(input);

        Assert.Equal(ReplacedInvalidCodepoints, libjq.jvp_utf8_explode(output));
    }

    [Fact]
    public void ImplodeRejectsNanAsANonCodepointNumber()
    {
        Assert.Throws<ArgumentException>(() => libjq.jvp_utf8_implode(new[] { double.NaN }));
    }

    [Fact]
    public void ExplodeReplacesUnpairedUtf16Surrogates()
    {
        Assert.Equal(ReplacedUnpairedSurrogateCodepoints, libjq.jvp_utf8_explode("a\uD800b"));
    }

    [Theory]
    [InlineData(0x7F, 1)]
    [InlineData(0x80, 2)]
    [InlineData(0x800, 3)]
    [InlineData(0x10FFFF, 4)]
    public void Utf8EncodeRoundTripsValidUnicodeScalars(int expectedCodepoint, int expectedLength)
    {
        Span<byte> buffer = stackalloc byte[4];

        var length = libjq.jvp_utf8_encode(expectedCodepoint, buffer);
        var actualCodepoint = 0;
        var next = libjq.jvp_utf8_next(buffer[..length], 0, ref actualCodepoint);

        Assert.Equal(expectedLength, length);
        Assert.Equal(expectedLength, next);
        Assert.Equal(expectedCodepoint, actualCodepoint);
    }

    [Fact]
    public void ScalarAndByteOffsetsRoundTripOnlyAtUtf8Boundaries()
    {
        var bytes = Encoding.UTF8.GetBytes("ā🙂z");
        var expectedByteOffsets = new[] { 0, 1, 3, 7, 8 };

        for (var scalarOffset = 0; scalarOffset < expectedByteOffsets.Length; scalarOffset++)
        {
            var byteOffset = libjq.jvp_utf8_codepoint_to_byte_offset(bytes, scalarOffset);
            Assert.Equal(expectedByteOffsets[scalarOffset], byteOffset);
            Assert.Equal(
                scalarOffset,
                libjq.jvp_utf8_byte_to_codepoint_offset(bytes, byteOffset!.Value));
        }

        Assert.Null(libjq.jvp_utf8_codepoint_to_byte_offset(bytes, 5));
        Assert.Throws<ArgumentException>(() => libjq.jvp_utf8_byte_to_codepoint_offset(bytes, 2));
    }

    [Fact]
    public void JvHelpersPreserveJqImplodeReplacementRules()
    {
        var values = libjq.jv_array(
        [
            libjq.jv_number(0x61),
            libjq.jv_number(1.9),
            libjq.jv_number(0xD800),
            libjq.jv_number(0x1F642),
        ]);

        var imploded = libjq.jv_string_implode(values);
        var exploded = libjq.jv_string_explode(libjq.jv_copy(imploded));

        Assert.Equal("a\u0001�🙂", imploded.StringValue);
        Assert.Equal(
            new double[] { 0x61, 1, 0xFFFD, 0x1F642 },
            exploded.ArrayValue.Select(static item => item.NumberValue));
        libjq.jv_free(exploded);
        Assert.Equal(4, libjq.jv_string_length_codepoints(imploded));
    }
}
