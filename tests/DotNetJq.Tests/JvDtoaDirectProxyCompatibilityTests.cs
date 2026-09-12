using System.Reflection;
using System.Runtime.ExceptionServices;
using DotNetJq.Port;

namespace DotNetJq.Tests;

public sealed class JvDtoaDirectProxyCompatibilityTests
{
    private static readonly double PositiveQuietNaN =
        BitConverter.UInt64BitsToDouble(0x7FF8000000000000UL);

    [Fact]
    public void StrtodConsumesTheExactPinnedDecimalPrefixAndPreservesSpecialBits()
    {
        var context = new dtoa_context();
        libjq.jvp_dtoa_context_init(context);

        var cases = new (string Text, int EndIndex, ulong Bits)[]
        {
            ("", 0, 0x0000000000000000UL),
            ("+", 0, 0x0000000000000000UL),
            ("-", 0, 0x0000000000000000UL),
            (" 1", 0, 0x0000000000000000UL),
            (".", 0, 0x0000000000000000UL),
            (".5x", 2, 0x3FE0000000000000UL),
            ("1.", 2, 0x3FF0000000000000UL),
            ("1e", 1, 0x3FF0000000000000UL),
            ("1e+", 1, 0x3FF0000000000000UL),
            ("1e+2x", 4, 0x4059000000000000UL),
            ("0x1", 1, 0x0000000000000000UL),
            ("infX", 3, 0x7FF0000000000000UL),
            ("infinityX", 8, 0x7FF0000000000000UL),
            ("INF", 3, 0x7FF0000000000000UL),
            ("-Infinity!", 9, 0xFFF0000000000000UL),
            ("nanX", 3, 0x7FF8000000000000UL),
            ("NaN(123)x", 3, 0x7FF8000000000000UL),
            ("-nan(0xabc)!", 4, 0xFFF8000000000000UL),
            ("1e99999x", 7, 0x7FF0000000000000UL),
            ("-1e-99999x", 9, 0x8000000000000000UL),
            ("01x", 2, 0x3FF0000000000000UL),
            ("+.5x", 3, 0x3FE0000000000000UL),
            ("-.0x", 3, 0x8000000000000000UL),
        };

        foreach (var item in cases)
        {
            var value = libjq.jvp_strtod(context, item.Text, out var endIndex);
            Assert.Equal(item.EndIndex, endIndex);
            Assert.Equal(item.Bits, BitConverter.DoubleToUInt64Bits(value));
        }

        libjq.jvp_dtoa_context_free(context);
        Assert.False(context.IsInitialized);
    }

    [Fact]
    public void DtoaModeZeroMatchesPinnedDigitsDecimalPointSignAndEndIndex()
    {
        var context = new dtoa_context();
        libjq.jvp_dtoa_context_init(context);
        var cases = new (double Value, string Digits, int DecimalPoint, int Sign, int EndIndex)[]
        {
            (0d, "0", 1, 0, 1),
            (-0d, "0", 1, 1, 1),
            (1d, "1", 1, 0, 1),
            (-1d, "1", 1, 1, 1),
            (1.5d, "15", 1, 0, 2),
            (0.00001d, "1", -4, 0, 1),
            (0.000001d, "1", -5, 0, 1),
            (1e20, "1", 21, 0, 1),
            (1e21, "1", 22, 0, 1),
            (double.Epsilon, "5", -323, 0, 1),
            (1.2345678901234567d, "12345678901234567", 1, 0, 17),
            (double.PositiveInfinity, "Infinity", 9999, 0, 8),
            (double.NegativeInfinity, "Infinity", 9999, 1, 8),
            (PositiveQuietNaN, "NaN", 9999, 0, 3),
            (-PositiveQuietNaN, "NaN", 9999, 1, 3),
        };

        foreach (var item in cases)
        {
            var digits = libjq.jvp_dtoa(
                context,
                item.Value,
                mode: 0,
                ndigits: 0,
                out var decimalPoint,
                out var sign,
                out var endIndex);
            Assert.Equal(item.Digits, digits);
            Assert.Equal(item.DecimalPoint, decimalPoint);
            Assert.Equal(item.Sign, sign);
            Assert.Equal(item.EndIndex, endIndex);
            libjq.jvp_freedtoa(context, digits);
        }
    }

    [Fact]
    public void DtoaFmtMatchesJqModeZeroLayoutThresholdsAndSpecialValues()
    {
        var context = new dtoa_context();
        libjq.jvp_dtoa_context_init(context);
        var cases = new (double Value, string Expected)[]
        {
            (0d, "0"),
            (-0d, "-0"),
            (0.00001d, "1e-05"),
            (0.000001d, "1e-06"),
            (1e20, "1e+20"),
            (1e21, "1e+21"),
            (1.2345678901234567d, "1.2345678901234567"),
            (double.PositiveInfinity, "Infinity"),
            (double.NegativeInfinity, "-Infinity"),
            (PositiveQuietNaN, "NaN"),
            (-PositiveQuietNaN, "-NaN"),
        };

        foreach (var item in cases)
        {
            Assert.Equal(item.Expected, libjq.jvp_dtoa_fmt(context, item.Value));
        }
    }

    [Fact]
    public void ProductionDumpInitializesAndReusesTheCallingThreadsDtoaContext()
    {
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try
            {
                var field = typeof(libjq).GetField(
                    "dtoa_context_by_thread",
                    BindingFlags.NonPublic | BindingFlags.Static);
                Assert.NotNull(field);
                var contexts = Assert.IsType<ThreadLocal<dtoa_context>>(field.GetValue(null));
                Assert.False(contexts.IsValueCreated);

                Assert.Equal("[1.25,2.5]", libjq.jv_dump_string(libjq.jv_array(
                    [libjq.jv_number(1.25), libjq.jv_number(2.5)])));

                Assert.True(contexts.IsValueCreated);
                var context = contexts.Value;
                Assert.NotNull(context);
                Assert.Same(context, libjq.tsd_dtoa_context_get());
            }
            catch (Exception error)
            {
                failure = error;
            }
        });

        worker.Start();
        worker.Join();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
