// DOTNETJQ PORT TEST MAP
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Semantic sources: src/jv_print.c, src/jv.h, and src/main.c output routing.

namespace DotNetJq.Tests;

public sealed class JvPrintSourceArchitectureGuardTests
{
    [Fact]
    public void OneMappedPrinterOwnsJsonTraversalAndCliOnlyAdaptsFlagsAndSinks()
    {
        var repositoryRoot = FindRepositoryRoot();
        var printer = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src/DotNetJq/Port/src/jv_print.c.cs"));
        var cliOutput = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src/DotNetJq.Cli/CliOutput.cs"));

        Assert.Contains("private static void jv_dump_term(", printer, StringComparison.Ordinal);
        Assert.Contains("private static void jvp_dump_string(", printer, StringComparison.Ordinal);
        Assert.Contains("jv_keys(jv_copy(current))", printer, StringComparison.Ordinal);
        Assert.Contains("jv_array_get(jv_copy(arrayFrame.Value), 0)", printer, StringComparison.Ordinal);
        Assert.Contains("jv_object_iter_value(frame.Value, frame.Iterator)", printer, StringComparison.Ordinal);
        Assert.Contains("jvp_dtoa_fmt(dtoaContext, value)", printer, StringComparison.Ordinal);
        Assert.Contains(
            "private static readonly jv_print_colors ProcessPrintColors",
            printer,
            StringComparison.Ordinal);
        Assert.Contains(
            "internal static bool jq_set_colors(string? codeString)",
            printer,
            StringComparison.Ordinal);
        Assert.Contains("ResetPrintColors();", printer, StringComparison.Ordinal);
        Assert.DoesNotContain("Interlocked", printer, StringComparison.Ordinal);
        Assert.DoesNotContain("lock (", printer, StringComparison.Ordinal);
        Assert.Contains("libjq.jv_dumpf(", cliOutput, StringComparison.Ordinal);
        Assert.Contains(
            "libjq.jq_set_colors(colorConfiguration)",
            cliOutput,
            StringComparison.Ordinal);
        Assert.DoesNotContain("jv_print_colors", cliOutput, StringComparison.Ordinal);
        Assert.Contains("dumpOptions & ~libjq.JV_PRINT_PRETTY", cliOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("StringBuilder", cliOutput, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(
            repositoryRoot,
            "src/DotNetJq.Cli/JqOutputFormatter.cs")));
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
