// DOTNETJQ PORT TEST MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream files: src/jv_print.c, src/jq.h, src/main.c
// Managed boundary: one compact UTF-8 printer buffer supplies both public
// output-byte accounting and the detached JsonElement representation.

using DotNetJq.Port;

namespace DotNetJq.Tests;

public sealed class PublicOutputSingleMaterializationTests
{
    [Fact]
    public void DumpBytesConsumesTheTransferredRootOwner()
    {
        var child = libjq.jv_string("payload");
        var retainedChild = libjq.jv_copy(child);
        var childStorage = Assert.IsType<jvp_string>(child.Value);
        var value = libjq.jv_array([child]);
        var arrayStorage = Assert.IsType<jvp_array>(value.Value);

        Assert.Equal("[\"payload\"]"u8.ToArray(), libjq.jv_dump_bytes(value));
        Assert.Equal(0, arrayStorage.Refcnt.Count);
        Assert.Equal(1, childStorage.Refcnt.Count);

        libjq.jv_free(retainedChild);
        Assert.Equal(0, childStorage.Refcnt.Count);
    }

    [Fact]
    public void BorrowedDumpBytesPreservesTheOwnerAndProducesADetachedJsonElement()
    {
        var child = libjq.jv_string("é");
        var retainedChild = libjq.jv_copy(child);
        var childStorage = Assert.IsType<jvp_string>(child.Value);
        var value = libjq.jv_array([child]);
        var arrayStorage = Assert.IsType<jvp_array>(value.Value);

        var utf8 = libjq.jv_dump_bytes_borrowed(value);
        Assert.Equal("[\"é\"]"u8.ToArray(), utf8);
        Assert.Equal(1, arrayStorage.Refcnt.Count);
        Assert.Equal(2, childStorage.Refcnt.Count);

        var element = libjq.jv_to_json_element(utf8);
        utf8.AsSpan().Fill((byte)' ');
        Assert.Equal("[\"é\"]", element.GetRawText());
        Assert.Equal(1, arrayStorage.Refcnt.Count);
        Assert.Equal(2, childStorage.Refcnt.Count);

        libjq.jv_free(value);
        Assert.Equal(0, arrayStorage.Refcnt.Count);
        Assert.Equal(1, childStorage.Refcnt.Count);
        libjq.jv_free(retainedChild);
        Assert.Equal(0, childStorage.Refcnt.Count);
    }

    [Fact]
    public void PublicOutputByteLimitAcceptsTheExactCanonicalUtf8Length()
    {
        using var program = JqProgram.Compile(".");

        var output = Assert.Single(program.Execute(
            "\"é\"",
            new JqExecutionOptions { MaxOutputBytes = 4 }));

        Assert.Equal("\"é\"", output.GetRawText());
    }

    [Fact]
    public void PublicCursorUsesOnePrinterBufferForAccountingAndMaterialization()
    {
        var repositoryRoot = FindRepositoryRoot();
        var publicSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "DotNetJq",
            "Public",
            "JqProgram.cs"));
        var printerSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "DotNetJq",
            "Port",
            "src",
            "jv_print.c.cs"));

        Assert.Contains(
            "var outputUtf8 = libjq.jv_dump_bytes_borrowed(next);",
            publicSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "var valueBytes = outputUtf8.LongLength;",
            publicSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "value = libjq.jv_to_json_element(outputUtf8);",
            publicSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "jv_dump_string_borrowed(next)",
            publicSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "jv_to_json_element(next)",
            publicSource,
            StringComparison.Ordinal);

        Assert.Contains(
            "internal static byte[] jv_dump_bytes(jv value)",
            printerSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "jv_dump_term(tsd_dtoa_context_get(), value, 0, sink, ProcessPrintColors);",
            printerSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Encoding.UTF8.GetBytes(jv_dump_string",
            printerSource,
            StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "DotNetJq.sln")))
                {
                    return directory.FullName;
                }
            }
        }

        throw new DirectoryNotFoundException("Could not locate the DotNetJq repository root.");
    }
}
