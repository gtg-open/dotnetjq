// DOTNETJQ PORT TEST MAP
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Semantic sources: src/builtin.c:escape_string/f_format and src/jv.c:_jq_memmem callers.

namespace DotNetJq.Tests;

public sealed class BuiltinFormatSourceArchitectureGuardTests
{
    [Fact]
    public void FormatControlFlowAndOwnershipRemainInTheMappedBuiltinPort()
    {
        var repositoryRoot = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src/DotNetJq/Port/src/builtin.c.cs"));
        var start = source.IndexOf(
            "private static jv escape_string",
            StringComparison.Ordinal);
        var end = source.IndexOf(
            "private static jv f_env",
            start,
            StringComparison.Ordinal);

        Assert.True(start >= 0 && end > start);
        var formatPort = source[start..end];
        Assert.Contains("jvp_utf8_next(source, offset, ref codepoint)", formatPort, StringComparison.Ordinal);
        Assert.Contains("var formatText = c_string_value(format)", formatPort, StringComparison.Ordinal);
        Assert.Contains("jv_array_length(jv_copy(input))", formatPort, StringComparison.Ordinal);
        Assert.Contains("jv_array_get(jv_copy(input), index)", formatPort, StringComparison.Ordinal);
        Assert.Contains("jv_string_concat(line, escape_string(element, escapings))", formatPort, StringComparison.Ordinal);
        Assert.Contains("input = jv_array_set(jv_array(), 0, input)", formatPort, StringComparison.Ordinal);
        Assert.Contains("jv_mem_alloc", formatPort, StringComparison.Ordinal);
        Assert.Contains("jv_mem_calloc", formatPort, StringComparison.Ordinal);
        Assert.Contains("jvp_utf8_is_valid", formatPort, StringComparison.Ordinal);
        Assert.DoesNotContain("jv_format(", formatPort, StringComparison.Ordinal);
        Assert.DoesNotContain("JqFormats", formatPort, StringComparison.Ordinal);
        Assert.DoesNotContain(".Replace(", formatPort, StringComparison.Ordinal);
        Assert.DoesNotContain("Convert.ToBase64String", formatPort, StringComparison.Ordinal);

        Assert.False(File.Exists(Path.Combine(
            repositoryRoot,
            "src/DotNetJq/Compatibility/Json/JqFormats.cs")));
    }

    [Fact]
    public void NativeMemmemCallSitesRouteThroughTheMappedUtilityHelper()
    {
        var repositoryRoot = FindRepositoryRoot();
        var valueSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src/DotNetJq/Port/src/jv.c.cs"));
        var auxiliarySource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src/DotNetJq/Port/src/jv_aux.c.cs"));

        Assert.Contains("_jq_memmem(source[searchFrom..], pattern)", valueSource, StringComparison.Ordinal);
        Assert.Contains("_jq_memmem(source[offset..], delimiter)", valueSource, StringComparison.Ordinal);
        Assert.Contains(
            "_jq_memmem(\n                        jvp_string_data(container),\n                        jvp_string_data(contained))",
            auxiliarySource,
            StringComparison.Ordinal);
        Assert.DoesNotContain("source[searchFrom..].IndexOf(pattern)", valueSource, StringComparison.Ordinal);
        Assert.DoesNotContain("source[offset..].IndexOf(delimiter)", valueSource, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "jvp_string_data(container).IndexOf(jvp_string_data(contained))",
            auxiliarySource,
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
