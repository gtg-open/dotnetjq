// DOTNETJQ PORT TEST MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream files: src/jv_print.c, src/jv.h
// Direct contracts: jv_dump_string() consumes its input; jv_dump_string_trunc()
// treats bufsize as including the trailing NUL, preserves delimiters, and
// backtracks UTF-8 only on its bufsize >= 8 truncation path.

using DotNetJq.Port;

namespace DotNetJq.Tests;

[Collection(JvPrintGlobalColorStateGroup.Name)]
public sealed class JvPrintOwnershipCompatibilityTests
{
    [Fact]
    public void DumpStringConsumesRootAndReleasesItsOwnedChildren()
    {
        var child = libjq.jv_string("payload");
        var retainedChild = libjq.jv_copy(child);
        var childStorage = Assert.IsType<jvp_string>(child.Value);
        var value = libjq.jv_array([child]);
        var arrayStorage = Assert.IsType<jvp_array>(value.Value);

        Assert.Equal(2, childStorage.Refcnt.Count);
        Assert.Equal("[\"payload\"]", libjq.jv_dump_string(value));
        Assert.Equal(0, arrayStorage.Refcnt.Count);
        Assert.Equal(1, childStorage.Refcnt.Count);

        libjq.jv_free(retainedChild);
        Assert.Equal(0, childStorage.Refcnt.Count);
    }

    [Fact]
    public void BorrowedDumpSpellsCopyAndPreservesTheCallersOwner()
    {
        var child = libjq.jv_string("payload");
        var retainedChild = libjq.jv_copy(child);
        var childStorage = Assert.IsType<jvp_string>(child.Value);
        var value = libjq.jv_array([child]);
        var arrayStorage = Assert.IsType<jvp_array>(value.Value);

        Assert.Equal("[\"payload\"]", libjq.jv_dump_string_borrowed(value));
        Assert.Equal(1, arrayStorage.Refcnt.Count);
        Assert.Equal(2, childStorage.Refcnt.Count);

        libjq.jv_free(value);
        Assert.Equal(0, arrayStorage.Refcnt.Count);
        Assert.Equal(1, childStorage.Refcnt.Count);
        libjq.jv_free(retainedChild);
    }

    [Fact]
    public void JqShapedFlagsAndIndentMacroPreserveNativeBitLayout()
    {
        Assert.Equal(1, libjq.JV_PRINT_PRETTY);
        Assert.Equal(2, libjq.JV_PRINT_ASCII);
        Assert.Equal(4, libjq.JV_PRINT_COLOR);
        Assert.Equal(libjq.JV_PRINT_COLOR, libjq.JV_PRINT_COLOUR);
        Assert.Equal(8, libjq.JV_PRINT_SORTED);
        Assert.Equal(16, libjq.JV_PRINT_INVALID);
        Assert.Equal(32, libjq.JV_PRINT_REFCOUNT);
        Assert.Equal(64, libjq.JV_PRINT_TAB);
        Assert.Equal(128, libjq.JV_PRINT_ISATTY);
        Assert.Equal(256, libjq.JV_PRINT_SPACE0);
        Assert.Equal(512, libjq.JV_PRINT_SPACE1);
        Assert.Equal(1024, libjq.JV_PRINT_SPACE2);
        Assert.Equal(libjq.JV_PRINT_PRETTY, libjq.JV_PRINT_INDENT_FLAGS(0));
        Assert.Equal(
            libjq.JV_PRINT_PRETTY | (7 << 8),
            libjq.JV_PRINT_INDENT_FLAGS(7));
        Assert.Equal(
            libjq.JV_PRINT_PRETTY | libjq.JV_PRINT_TAB,
            libjq.JV_PRINT_INDENT_FLAGS(-1));
        Assert.Equal(
            libjq.JV_PRINT_PRETTY | libjq.JV_PRINT_TAB,
            libjq.JV_PRINT_INDENT_FLAGS(8));
    }

    [Fact]
    public void PrettyAsciiAndSortedFlagsShareTheCanonicalTraversal()
    {
        var value = libjq.jv_object(
        [
            new KeyValuePair<string, jv>("z", libjq.jv_number(1)),
            new KeyValuePair<string, jv>(
                "a",
                libjq.jv_array([libjq.jv_string("μ"), libjq.jv_string("😎")])),
        ]);
        var flags = libjq.JV_PRINT_INDENT_FLAGS(2) |
            libjq.JV_PRINT_ASCII |
            libjq.JV_PRINT_SORTED;

        Assert.Equal(
            "{\n" +
            "  \"a\": [\n" +
            "    \"\\u03bc\",\n" +
            "    \"\\ud83d\\ude0e\"\n" +
            "  ],\n" +
            "  \"z\": 1\n" +
            "}",
            libjq.jv_dump_string(value, flags));
    }

    [Fact]
    public void StreamSinkUsesTheSameColorBoundariesAndPreservesBorrowedOwner()
    {
        Assert.True(libjq.jq_set_colors(""));
        var value = libjq.jv_array([libjq.jv_true()]);
        var storage = Assert.IsType<jvp_array>(value.Value);
        using var output = new MemoryStream();

        libjq.jv_dumpf(
            libjq.jv_copy(value),
            output,
            libjq.JV_PRINT_COLOR | libjq.JV_PRINT_ISATTY);

        Assert.Equal(
            "\u001b[1;39m[\u001b[0m\u001b[0;39mtrue\u001b[0m\u001b[1;39m]\u001b[0m",
            System.Text.Encoding.UTF8.GetString(output.ToArray()));
        Assert.Equal(1, storage.Refcnt.Count);
        libjq.jv_free(value);
        Assert.Equal(0, storage.Refcnt.Count);
    }

    [Fact]
    public void JqSetColorsRetainsPinnedProcessGlobalStateTransitions()
    {
        try
        {
            Assert.True(libjq.jq_set_colors("30:31:32:33:34:35:36:37"));
            Assert.Equal(
                "\u001b[30mnull\u001b[0m",
                libjq.jv_dump_string(libjq.jv_null(), libjq.JV_PRINT_COLOR));

            // Native jq treats NULL as no operation, retaining all prior entries.
            Assert.True(libjq.jq_set_colors(null));
            Assert.Equal(
                "\u001b[30mnull\u001b[0m",
                libjq.jv_dump_string(libjq.jv_null(), libjq.JV_PRINT_COLOR));

            // A partial setting replaces its prefix and restores the untouched
            // suffix from default_colors[], rather than retaining older entries.
            Assert.True(libjq.jq_set_colors("41:42"));
            Assert.Equal(
                "\u001b[41mnull\u001b[0m",
                libjq.jv_dump_string(libjq.jv_null(), libjq.JV_PRINT_COLOR));
            Assert.Equal(
                "\u001b[42mfalse\u001b[0m",
                libjq.jv_dump_string(libjq.jv_false(), libjq.JV_PRINT_COLOR));
            Assert.Equal(
                "\u001b[0;39mtrue\u001b[0m",
                libjq.jv_dump_string(libjq.jv_true(), libjq.JV_PRINT_COLOR));

            // Validation completes before native colors[] is changed.
            Assert.False(libjq.jq_set_colors("43:x"));
            Assert.Equal(
                "\u001b[41mnull\u001b[0m",
                libjq.jv_dump_string(libjq.jv_null(), libjq.JV_PRINT_COLOR));
            Assert.Equal(
                "\u001b[42mfalse\u001b[0m",
                libjq.jv_dump_string(libjq.jv_false(), libjq.JV_PRINT_COLOR));

            Assert.True(libjq.jq_set_colors(""));
            Assert.Equal(
                "\u001b[0;90mnull\u001b[0m",
                libjq.jv_dump_string(libjq.jv_null(), libjq.JV_PRINT_COLOR));
        }
        finally
        {
            _ = libjq.jq_set_colors("");
        }
    }

    [Fact]
    public void RefcountFlagObservesTheSameTemporaryChildOwnersAsNativeTraversal()
    {
        var child = libjq.jv_string("x");
        var retainedChild = libjq.jv_copy(child);
        var childStorage = Assert.IsType<jvp_string>(child.Value);
        var value = libjq.jv_array([child]);

        Assert.Equal(
            "[\"x\" (2)] (0)",
            libjq.jv_dump_string(value, libjq.JV_PRINT_REFCOUNT));
        Assert.Equal(1, childStorage.Refcnt.Count);

        libjq.jv_free(retainedChild);
        Assert.Equal(0, childStorage.Refcnt.Count);
    }

    [Fact]
    public void InvalidFlagPreservesPinnedMissingMessageFreeOwnershipTrace()
    {
        var message = libjq.jv_string("boom");
        var retainedMessage = libjq.jv_copy(message);
        var storage = Assert.IsType<jvp_string>(message.Value);
        var invalid = libjq.jv_invalid_with_msg(message);

        Assert.Equal(
            "<invalid:\"boom\">",
            libjq.jv_dump_string(invalid, libjq.JV_PRINT_INVALID));
        Assert.Equal(2, storage.Refcnt.Count);

        libjq.jv_free(retainedMessage);
        Assert.Equal(1, storage.Refcnt.Count);
    }

    [Fact]
    public void StreamFailureStillConsumesTheCompleteOwnedTree()
    {
        var child = libjq.jv_string("payload");
        var retainedChild = libjq.jv_copy(child);
        var childStorage = Assert.IsType<jvp_string>(child.Value);
        var value = libjq.jv_array([child]);
        var arrayStorage = Assert.IsType<jvp_array>(value.Value);

        Assert.Throws<IOException>(() => libjq.jv_dumpf(value, new ThrowingWriteStream(), 0));
        Assert.Equal(0, arrayStorage.Refcnt.Count);
        Assert.Equal(1, childStorage.Refcnt.Count);

        libjq.jv_free(retainedChild);
        Assert.Equal(0, childStorage.Refcnt.Count);
    }

    [Fact]
    public void TruncationMatchesNativeByteBufferBoundariesIncludingPartialUtf8()
    {
        const string value = "😀abcdefgh";
        AssertDumpBytes(value, 1, []);
        AssertDumpBytes(value, 4, [0x22, 0xF0, 0x9F]);
        AssertDumpBytes(value, 7, [0x22, 0xF0, 0x9F, 0x98, 0x80, 0x61]);
        AssertDumpBytes(value, 8, [0x22, 0x2E, 0x2E, 0x2E, 0x22]);
        AssertDumpBytes(
            value,
            10,
            [0x22, 0xF0, 0x9F, 0x98, 0x80, 0x2E, 0x2E, 0x2E, 0x22]);
    }

    [Fact]
    public void TruncationRetainsNativeClosingDelimiterAndNonDelimitedBudgets()
    {
        Assert.Equal(
            "[12...]",
            libjq.jv_dump_string_trunc(
                libjq.jv_array([libjq.jv_number(123456789)]),
                8));
        Assert.Equal(
            "1234...",
            libjq.jv_dump_string_trunc(libjq.jv_number(123456789), 8));
        Assert.Equal(
            "{\"a...}",
            libjq.jv_dump_string_trunc(
                libjq.jv_object([new KeyValuePair<string, jv>("abcdefgh", libjq.jv_null())]),
                8));
    }

    [Fact]
    public void BorrowedTruncationPreservesTheCallersOwner()
    {
        var value = libjq.jv_string("😀abcdefgh");
        var storage = Assert.IsType<jvp_string>(value.Value);

        Assert.Equal("\"😀...\"", libjq.jv_dump_string_trunc_borrowed(value, 10));
        Assert.Equal(1, storage.Refcnt.Count);

        libjq.jv_free(value);
        Assert.Equal(0, storage.Refcnt.Count);
    }

    private static void AssertDumpBytes(string value, int bufferSize, byte[] expected)
    {
        var input = libjq.jv_string(value);
        var storage = Assert.IsType<jvp_string>(input.Value);

        Assert.Equal(expected, libjq.jv_dump_string_trunc_bytes(input, bufferSize));
        Assert.Equal(0, storage.Refcnt.Count);
    }

    private sealed class ThrowingWriteStream : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new IOException("synthetic sink failure");

        public override void Write(ReadOnlySpan<byte> buffer) =>
            throw new IOException("synthetic sink failure");
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class JvPrintGlobalColorStateGroup
{
    internal const string Name = nameof(JvPrintGlobalColorStateGroup);
}
