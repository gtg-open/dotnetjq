using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DotNetJq.Tests;

/// <summary>
/// Frozen jq-1.8.2 (34f7186b) serialized oracle corpus for the public special-math filters.
/// The pinned native executable was used only to generate <see cref="ExpectedGzipBase64"/>.
/// </summary>
public sealed class SpecialMathExactOracleCorpusTests
{
    private const int ExpectedCaseCount = 6126;
    private const string ExpectedInputSha256 =
        "794237c64ac774ec9d45c3e17735a9a734ae03d2750a6055a31292350750e638";
    private const string ExpectedOutputSha256 =
        "ebfe071e4c9baa02392bd7a2889d0b23d67b2587c328bfaae8870a1ac9f94412";
    private const int DiagnosticExamplesPerFunction = 4;

    private static readonly string[] UnaryFunctions =
    [
        "erf", "erfc", "gamma", "lgamma", "lgamma_r", "tgamma",
        "j0", "j1", "y0", "y1",
    ];

    private static readonly string[] BinaryFunctions = ["jn", "yn"];

    private static readonly string[] GeneratedSpecialValueLabels =
    [
        "-0 (-1/infinite)", "NaN", "+Infinity", "-Infinity",
    ];

    private static readonly int[] BesselOrders =
    [
        -100, -50, -20, -10, -5, -3, -2, -1, 0, 1, 2, 3, 5, 10, 20, 50,
    ];

    private static readonly double[] BesselFiniteArguments =
    [
        0, -double.Epsilon, double.Epsilon,
        -2.2250738585072014e-308, 2.2250738585072014e-308,
        -double.MaxValue, double.MaxValue,
        -100, -20, -12, -10, -3, -2, -1, -0.5,
        0.5, 1, 2, 3, 10, 12, 20, 100, 1e4, 1e8,
    ];

    private static readonly double[] UnaryPairedMagnitudes =
    [
        1e-300, 1e-200, 1e-100, 1e-50, 1e-20, 1e-10, 1e-5,
        16, 20, 25, 32, 50, 64, 100, 128, 160, 169, 170, 171, 172,
        256, 512, 1000, 1e4, 1e6, 1e8, 1e12, 1e16, 1e20,
        1e50, 1e100, 1e200, 1e300,
    ];

    // Every threshold is probed at the exact binary64 value and at both adjacent
    // representable values, with both signs. These cover the repaired erf/erfc,
    // lgamma/tgamma, base-Bessel, coefficient-table, and asymptotic branches.
    private static readonly double[] UnaryBranchThresholds =
    [
        Math.ScaleB(1, -1022),
        Math.ScaleB(1, -70),
        Math.ScaleB(1, -56),
        Math.ScaleB(1, -54),
        Math.ScaleB(1, -29),
        Math.ScaleB(1, -28),
        Math.ScaleB(1, -27),
        Math.ScaleB(1, -13),
        0.23163998126983643,
        0.25,
        0.5,
        0.7315998077392578,
        0.84375,
        0.8999996185302734,
        1,
        1.2316322326660156,
        1.25,
        1.4616321449683622,
        1.5,
        1.7316312789916992,
        2,
        3,
        4,
        5,
        BitConverter.UInt64BitsToDouble(0x4006db6d00000000UL),
        BitConverter.UInt64BitsToDouble(0x4006db6e00000000UL),
        20d / 7,
        BitConverter.UInt64BitsToDouble(0x40122e8b00000000UL),
        6,
        6.5,
        7,
        8,
        9,
        10,
        11,
        12,
        28,
        171.6243769563027,
        172,
        184,
        Math.ScaleB(1, 28),
        Math.ScaleB(1, 52),
        Math.ScaleB(1, 58),
        Math.ScaleB(1, 129),
        BitConverter.UInt64BitsToDouble(0x4800000100000000UL),
    ];

    private static readonly int[] BesselRecurrenceOrders =
    [
        -100, -50, -34, -20, -10, -5, -3, -2, -1,
        0, 1, 2, 3, 5, 10, 20, 34, 50, 100,
    ];

    private static readonly int[] BesselBranchOrders = [-100, -34, -2, 0, 2, 34, 100];

    private static readonly double[] BesselArgumentBranchThresholds =
    [
        Math.ScaleB(1, -54),
        Math.ScaleB(1, -29),
        Math.ScaleB(1, -27),
        Math.ScaleB(1, -13),
        2,
        BitConverter.UInt64BitsToDouble(0x4006db6d00000000UL),
        BitConverter.UInt64BitsToDouble(0x40122e8b00000000UL),
        8,
        Math.ScaleB(1, 28),
        Math.ScaleB(1, 129),
        BitConverter.UInt64BitsToDouble(0x4800000100000000UL),
        Math.ScaleB(1, 302),
    ];

    private const string CorpusFilter =
        """
        def orders: [-100,-50,-20,-10,-5,-3,-2,-1,0,1,2,3,5,10,20,50][];
        (.u + [(-1/infinite),nan,infinite,-infinite]) as $u |
        (.b + [orders as $n |
          [(-1/infinite),nan,infinite,-infinite][] as $x | [$n,$x]]) as $b |
        {
          caseCount:(($u|length)*10+($b|length)*2),
          erf:($u|map(erf)),
          erfc:($u|map(erfc)),
          gamma:($u|map(gamma)),
          lgamma:($u|map(lgamma)),
          lgamma_r:($u|map(lgamma_r)),
          tgamma:($u|map(tgamma)),
          j0:($u|map(j0)),
          j1:($u|map(j1)),
          y0:($u|map(y0)),
          y1:($u|map(y1)),
          jn:($b|map(jn(.[0];.[1]))),
          yn:($b|map(yn(.[0];.[1])))
        }
        """;

    // Offline regeneration: serialize CreateInput().Json, execute CorpusFilter with
    // the pinned jq oracle with -c, remove its terminal LF, then use
    // gzip -n -9 and base64 -w100. The test itself never starts an external process.
    // GZip (mtime=0) of the compact oracle JSON. Uncompressed SHA-256:
    // ebfe071e4c9baa02392bd7a2889d0b23d67b2587c328bfaae8870a1ac9f94412
    private const string ExpectedGzipBase64 =
        """
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
""";

    [Fact]
    public void PublicFiltersExactlyMatchFrozenJqVisibleJsonForAllCases()
    {
        var corpus = CreateInput();

        Assert.Equal(ExpectedCaseCount, corpus.CaseCount);
        Assert.Equal(
            ExpectedInputSha256,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(corpus.Json)))
                .ToLowerInvariant());

        var actual = Assert.Single(JqProgram.Compile(CorpusFilter).Execute(corpus.Json)).GetRawText();
        var expected = DecompressExpected();
        Assert.Equal(
            ExpectedOutputSha256,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(expected)))
                .ToLowerInvariant());

        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            Assert.Fail(BuildMismatchDiagnostics(expected, actual, corpus));
        }

        // Keep the oracle contract explicitly byte-for-byte; diagnostics above only explain failures.
        Assert.Equal(expected, actual);
    }

    private static CorpusInput CreateInput()
    {
        var unary = new List<double>
        {
            -double.Epsilon,
            double.Epsilon,
            -2.2250738585072014e-308,
            2.2250738585072014e-308,
            -double.MaxValue,
            double.MaxValue,
        };

        for (var quarter = -48; quarter <= 48; quarter++)
        {
            unary.Add(quarter / 4d);
        }

        foreach (var magnitude in UnaryPairedMagnitudes)
        {
            unary.Add(-magnitude);
            unary.Add(magnitude);
        }

        var unaryBits = unary
            .Select(BitConverter.DoubleToInt64Bits)
            .ToHashSet();
        foreach (var threshold in UnaryBranchThresholds)
        {
            AddSignedBitNeighborhood(unary, unaryBits, threshold);
        }

        Assert.Equal(
            unary.Count,
            unary.Select(BitConverter.DoubleToInt64Bits).Distinct().Count());

        var binary = new List<double[]>(BesselOrders.Length * BesselFiniteArguments.Length);
        foreach (var order in BesselOrders)
        {
            foreach (var argument in BesselFiniteArguments)
            {
                binary.Add([order, argument]);
            }
        }

        var binaryBits = binary
            .Select(pair => (
                Order: (int)pair[0],
                Argument: BitConverter.DoubleToInt64Bits(pair[1])))
            .ToHashSet();

        foreach (var order in BesselRecurrenceOrders)
        {
            if (order != 0)
            {
                AddSignedBinaryBitNeighborhood(binary, binaryBits, order, Math.Abs((double)order));
            }
        }

        foreach (var order in BesselBranchOrders)
        {
            foreach (var threshold in BesselArgumentBranchThresholds)
            {
                AddSignedBinaryBitNeighborhood(binary, binaryBits, order, threshold);
            }
        }

        Assert.Equal(
            binary.Count,
            binary
                .Select(pair => (
                    BitConverter.DoubleToInt64Bits(pair[0]),
                    BitConverter.DoubleToInt64Bits(pair[1])))
                .Distinct()
                .Count());

        const int generatedSpecialValuesPerFunction = 4;
        var caseCount =
            ((unary.Count + generatedSpecialValuesPerFunction) * 10) +
            ((binary.Count + (BesselOrders.Length * generatedSpecialValuesPerFunction)) * 2);

        return new(
            JsonSerializer.Serialize(new { u = unary, b = binary }),
            caseCount,
            unary,
            binary);
    }

    private static void AddSignedBitNeighborhood(
        ICollection<double> values,
        ISet<long> bits,
        double threshold)
    {
        foreach (var candidate in new[]
                 {
                     Math.BitDecrement(threshold),
                     threshold,
                     Math.BitIncrement(threshold),
                 })
        {
            AddUnique(values, bits, candidate);
            AddUnique(values, bits, -candidate);
        }
    }

    private static void AddUnique(ICollection<double> values, ISet<long> bits, double value)
    {
        if (bits.Add(BitConverter.DoubleToInt64Bits(value)))
        {
            values.Add(value);
        }
    }

    private static void AddSignedBinaryBitNeighborhood(
        ICollection<double[]> values,
        ISet<(int Order, long Argument)> bits,
        int order,
        double threshold)
    {
        foreach (var candidate in new[]
                 {
                     Math.BitDecrement(threshold),
                     threshold,
                     Math.BitIncrement(threshold),
                 })
        {
            AddBinaryUnique(values, bits, order, candidate);
            AddBinaryUnique(values, bits, order, -candidate);
        }
    }

    private static void AddBinaryUnique(
        ICollection<double[]> values,
        ISet<(int Order, long Argument)> bits,
        int order,
        double argument)
    {
        if (bits.Add((order, BitConverter.DoubleToInt64Bits(argument))))
        {
            values.Add([order, argument]);
        }
    }

    private static string BuildMismatchDiagnostics(
        string expectedJson,
        string actualJson,
        CorpusInput corpus)
    {
        using var expectedDocument = JsonDocument.Parse(expectedJson);
        using var actualDocument = JsonDocument.Parse(actualJson);
        var details = new StringBuilder();
        var totalMismatches = 0;

        foreach (var function in UnaryFunctions.Concat(BinaryFunctions))
        {
            var expectedValues = expectedDocument.RootElement.GetProperty(function);
            var actualValues = actualDocument.RootElement.GetProperty(function);
            var commonLength = Math.Min(expectedValues.GetArrayLength(), actualValues.GetArrayLength());
            var mismatchCount = Math.Abs(
                expectedValues.GetArrayLength() - actualValues.GetArrayLength());
            var serializationOnlyCount = 0;
            var numericalExamples = new List<string>(DiagnosticExamplesPerFunction);
            var serializationExamples = new List<string>(DiagnosticExamplesPerFunction);

            for (var index = 0; index < commonLength; index++)
            {
                var expectedValue = expectedValues[index].GetRawText();
                var actualValue = actualValues[index].GetRawText();
                if (string.Equals(expectedValue, actualValue, StringComparison.Ordinal))
                {
                    continue;
                }

                mismatchCount++;
                var serializationOnly =
                    AreNumericallyEquivalent(expectedValues[index], actualValues[index]);
                if (serializationOnly)
                {
                    serializationOnlyCount++;
                }

                var examples = serializationOnly ? serializationExamples : numericalExamples;
                if (examples.Count < DiagnosticExamplesPerFunction)
                {
                    examples.Add(
                        $"[{index}] {(serializationOnly ? "serialization" : "numeric")} " +
                        $"{DescribeInput(function, index, corpus)} " +
                        $"expected={expectedValue} actual={actualValue}");
                }
            }

            totalMismatches += mismatchCount;
            details.Append(function)
                .Append(": ")
                .Append(mismatchCount)
                .Append('/')
                .Append(expectedValues.GetArrayLength())
                .Append(" mismatches (")
                .Append(serializationOnlyCount)
                .AppendLine(" serialization-only)");

            foreach (var example in numericalExamples.Concat(serializationExamples))
            {
                details.Append("  ").AppendLine(example);
            }
        }

        var rawDifference = FirstDifference(expectedJson, actualJson);
        return
            $"Special-math oracle corpus differs at raw JSON position {rawDifference}; " +
            $"{totalMismatches}/{ExpectedCaseCount} function cases mismatch.{Environment.NewLine}" +
            details;
    }

    private static string DescribeInput(string function, int index, CorpusInput corpus)
    {
        if (UnaryFunctions.Contains(function, StringComparer.Ordinal))
        {
            return index < corpus.Unary.Count
                ? $"input={FormatDouble(corpus.Unary[index])}"
                : $"input={GeneratedSpecialValueLabels[index - corpus.Unary.Count]}";
        }

        if (index < corpus.Binary.Count)
        {
            var arguments = corpus.Binary[index];
            return $"order={FormatDouble(arguments[0])} input={FormatDouble(arguments[1])}";
        }

        var specialOffset = index - corpus.Binary.Count;
        var order = BesselOrders[specialOffset / GeneratedSpecialValueLabels.Length];
        var special = GeneratedSpecialValueLabels[specialOffset % GeneratedSpecialValueLabels.Length];
        return $"order={order} input={special}";
    }

    private static string FormatDouble(double value) =>
        value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    private static int FirstDifference(string expected, string actual)
    {
        var commonLength = Math.Min(expected.Length, actual.Length);
        for (var index = 0; index < commonLength; index++)
        {
            if (expected[index] != actual[index])
            {
                return index;
            }
        }

        return commonLength;
    }

    private static bool AreNumericallyEquivalent(JsonElement expected, JsonElement actual)
    {
        if (expected.ValueKind != actual.ValueKind)
        {
            return false;
        }

        if (expected.ValueKind == JsonValueKind.Number)
        {
            return BitConverter.DoubleToInt64Bits(expected.GetDouble()) ==
                BitConverter.DoubleToInt64Bits(actual.GetDouble());
        }

        if (expected.ValueKind != JsonValueKind.Array ||
            expected.GetArrayLength() != actual.GetArrayLength())
        {
            return string.Equals(
                expected.GetRawText(),
                actual.GetRawText(),
                StringComparison.Ordinal);
        }

        for (var index = 0; index < expected.GetArrayLength(); index++)
        {
            if (!AreNumericallyEquivalent(expected[index], actual[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static string DecompressExpected()
    {
        var compressedBytes = Convert.FromBase64String(ExpectedGzipBase64);
        using var compressed = new MemoryStream(compressedBytes);
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        using var reader = new StreamReader(
            gzip,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false);

        return reader.ReadToEnd();
    }

    private sealed record CorpusInput(
        string Json,
        int CaseCount,
        IReadOnlyList<double> Unary,
        IReadOnlyList<double[]> Binary);
}
