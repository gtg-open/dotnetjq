using System.Text;
using DotNetJq.Compatibility.FileSystem;
using DotNetJq.Port;

namespace DotNetJq.Tests;

public sealed class UtilDirectPortCompatibilityTests
{
    [Fact]
    public void MemmemMatchesThePinnedGlibcEmptyNeedleAndByteSearchContract()
    {
        Assert.Equal(0, libjq._jq_memmem(ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty));
        Assert.Equal(0, libjq._jq_memmem("", ""));
        Assert.Equal(0, libjq._jq_memmem("abc", ""));
        Assert.Equal(1, libjq._jq_memmem("ababa", "bab"));
        Assert.Equal(-1, libjq._jq_memmem("abc", "abcd"));

        byte[] haystack = [0x00, 0xFF, 0x10, 0xFF, 0x10];
        byte[] needle = [0xFF, 0x10];
        Assert.Equal(1, libjq._jq_memmem(haystack, needle));
        Assert.Equal(-1, libjq._jq_memmem(haystack, new byte[] { 0x10, 0x00 }));
    }

    [Fact]
    public void ExplicitPathAndStreamSubstitutionsPreserveTheirUtilityResults()
    {
        var fileSystem = new JqInMemoryFileSystem(new Dictionary<string, string>
        {
            ["/canonical/module.jq"] = "def value: 1;",
        });

        Assert.Equal(
            "/canonical/module.jq",
            libjq.jq_realpath(
                libjq.jv_string("/canonical/./module.jq"),
                fileSystem).StringValue);
        Assert.Equal(
            "/missing.jq",
            libjq.jq_realpath(libjq.jv_string("/missing.jq"), fileSystem).StringValue);

        using var output = new MemoryStream();
        libjq.priv_fwrite(new byte[] { 0x00, 0xC3, 0xA9 }, output, isTty: true);
        libjq.priv_fwrite("!", output, isTty: false);
        Assert.Equal(new byte[] { 0x00, 0xC3, 0xA9, (byte)'!' }, output.ToArray());
    }

    [Fact]
    public void MinAndMaxEvaluateTheManagedValuesOnceAndSelectTheExpectedOperand()
    {
        Assert.Equal(2, libjq.MIN(2, 3));
        Assert.Equal(3, libjq.MAX(2, 3));
        Assert.Equal("a", libjq.MIN("a", "b"));
        Assert.Equal("b", libjq.MAX("a", "b"));
    }
}
