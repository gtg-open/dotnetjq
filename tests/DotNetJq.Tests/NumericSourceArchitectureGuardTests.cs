using System.Text.RegularExpressions;

namespace DotNetJq.Tests;

public sealed class NumericSourceArchitectureGuardTests
{
    [Fact]
    public void ProductionPrinterUsesOneDtoaContextAndOnlyTheJqShapedFormatter()
    {
        var root = FindRepositoryRoot();
        var printer = Read(root, "src/DotNetJq/Port/src/jv_print.c.cs");
        var numberHandle = Read(root, "src/DotNetJq/Port/src/jv.h.cs");

        string[] completeDumpSignatures =
        [
            "internal static string jv_dump_string(jv value, int flags)",
            "internal static byte[] jv_dump_bytes(jv value)",
            "internal static void jv_dumpf(jv value, Stream stream, int flags)",
            "internal static byte[] jv_dump_string_trunc_bytes(jv value, int bufferSize)",
        ];
        foreach (var signature in completeDumpSignatures)
        {
            var dump = ExtractMethod(printer, signature);
            Assert.True(
                Regex.Count(dump, @"\btsd_dtoa_context_get\s*\(") == 1,
                $"{signature} must obtain exactly one dtoa context per complete dump.");
            Assert.Contains(
                "jv_dump_term(tsd_dtoa_context_get()",
                dump,
                StringComparison.Ordinal);
        }

        string[] recursiveFormattingSignatures =
        [
            "private static void jv_dump_term(",
            "private static bool BeginValue(",
            "private static string FormatNumber(",
        ];
        foreach (var signature in recursiveFormattingSignatures)
        {
            Assert.DoesNotContain(
                "tsd_dtoa_context_get",
                ExtractMethod(printer, signature),
                StringComparison.Ordinal);
        }

        var formatter = ExtractMethod(printer, "private static string FormatNumber(");
        Assert.Contains("jvp_number_is_nan(number)", formatter, StringComparison.Ordinal);
        Assert.Contains("jv_number_get_literal(number)", formatter, StringComparison.Ordinal);
        Assert.Contains("jv_number_value(number)", formatter, StringComparison.Ordinal);
        Assert.Contains("jvp_dtoa_fmt(dtoaContext, value)", formatter, StringComparison.Ordinal);
        Assert.DoesNotContain("tsd_dtoa_context_get", formatter, StringComparison.Ordinal);
        Assert.DoesNotContain("FormatJqBinary64", numberHandle, StringComparison.Ordinal);
        Assert.DoesNotContain("ToJqString", numberHandle, StringComparison.Ordinal);
    }

    [Fact]
    public void LiteralProjectionAndNumericOperationsStayOnCanonicalSourceRoutes()
    {
        var root = FindRepositoryRoot();
        var numberHandle = Read(root, "src/DotNetJq/Port/src/jv.h.cs");
        var valueCore = Read(root, "src/DotNetJq/Port/src/jv.c.cs");
        var privateNumbers = Read(root, "src/DotNetJq/Port/src/jv_private.h.cs");
        var valueAux = Read(root, "src/DotNetJq/Port/src/jv_aux.c.cs");

        Assert.Contains("decNumberFromString(new decNumber(), literal, context)", numberHandle, StringComparison.Ordinal);
        Assert.Contains("libjq.decNumberMinus(", numberHandle, StringComparison.Ordinal);
        Assert.Contains("libjq.decNumberAbs(", numberHandle, StringComparison.Ordinal);

        var projection = ExtractMethod(valueCore, "internal static double jvp_literal_number_to_double(");
        Assert.Contains("decNumberReduce(", projection, StringComparison.Ordinal);
        Assert.Contains("DEC_NUMBER_DOUBLE_PRECISION", projection, StringComparison.Ordinal);
        Assert.Contains("jvp_strtod(tsd_dtoa_context_get()", projection, StringComparison.Ordinal);
        Assert.DoesNotContain("double.TryParse", projection, StringComparison.Ordinal);
        Assert.Matches(
            @"jv_number_equal\(jv left, jv right\)\s*=>\s*jvp_number_cmp\(left, right\) == 0;",
            valueCore);

        var nanPredicate = ExtractMethod(privateNumbers, "internal static bool jvp_number_is_nan(");
        Assert.Contains("value.ExactValue", nanPredicate, StringComparison.Ordinal);
        Assert.Contains("exact.IsNaN", nanPredicate, StringComparison.Ordinal);
        Assert.DoesNotContain("number.NumberValue", nanPredicate, StringComparison.Ordinal);

        var comparison = ExtractMethod(valueAux, "private static int CompareNumbers(");
        Assert.Contains("jvp_number_is_nan(left)", comparison, StringComparison.Ordinal);
        Assert.Contains("jvp_number_is_nan(right)", comparison, StringComparison.Ordinal);
        Assert.Contains("jvp_number_cmp(left, right)", comparison, StringComparison.Ordinal);
        Assert.DoesNotContain("ExactValue", comparison, StringComparison.Ordinal);
        Assert.DoesNotContain("NumberValue", comparison, StringComparison.Ordinal);
    }

    private static string ExtractMethod(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing source signature: {signature}");
        var openingBrace = source.IndexOf('{', start);
        Assert.True(openingBrace >= 0, $"Missing method body: {signature}");
        var depth = 0;
        for (var index = openingBrace; index < source.Length; index++)
        {
            depth += source[index] switch
            {
                '{' => 1,
                '}' => -1,
                _ => 0,
            };
            if (depth == 0)
            {
                return source[start..(index + 1)];
            }
        }

        throw new InvalidOperationException($"Unterminated method body: {signature}");
    }

    private static string Read(string root, string relativePath) =>
        File.ReadAllText(Path.Combine(root, relativePath));

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
