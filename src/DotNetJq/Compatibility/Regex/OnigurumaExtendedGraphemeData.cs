// DOTNETJQ GENERATED COMPATIBILITY DATA
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Pinned source: vendor/oniguruma/src/unicode_egcb_data.c (Unicode 16.0).
// Generator/checker: tools/DotNetJq.DifferentialProbe/verify-oniguruma-egcb-data.pl
// Ranges are delta/length/type encoded as unsigned LEB128 and then Base64 encoded.

namespace DotNetJq.Compatibility.Regex;

internal enum OnigurumaGraphemeBreakType : byte
{
    Other = 0,
    Cr = 1,
    Lf = 2,
    Control = 3,
    Extend = 4,
    Prepend = 5,
    RegionalIndicator = 6,
    SpacingMark = 7,
    Zwj = 8,
    L = 13,
    Lv = 14,
    Lvt = 15,
    T = 16,
    V = 17,
}

internal static class OnigurumaExtendedGraphemeData
{
    internal const string UpstreamSourceSha256 =
        "21663445ace4f64775506f3fc53332a96e1b2b9f509b63eeb5462913daeb6d73";
    internal const int UpstreamRangeCount = 1_376;

    private const string EncodedRanges =
        "AAkDAAACAAEDAAABABEDXyADDQAD0gRvBJMCBgSHAiwEAQAEAQEEAQEEAQAEOAUFCgoEAQADLhQEEAAEZQYEAAAFAQUEAgEEAQMEIQAFAQAEHhoEWwoEOggECQAEGAMEAQgEAQIEAQQEKwIENAEFBQgEKhcEAAAFAB8EAAAHNgAEAAAHAAAEAQIHAAcEAAMHAAAEAAEHAQYECgEEHQAEAAEHOAAEAQAEAAEHAAMEAgEHAgEHAAAECQAECgEEGgAEAgEEAAAHOAAEAQIHAAEEBAEEAgIEAwAEHgEEAwAECwEEAAAHOAAEAQIHAAQEAQEEAAAHAQEHAAAEFAEEFgUEAQAEAAEHOAAEAQEEAAAHAAMEAgEHAgEHAAAEBwIECgEEHgAEOwAEAAAHAAAEAAEHAwIHAQIHAAAECQAEKAAEAAIHAAAENwAEAQIEAAMHAQIEAQMEBwEECwEEHQAEAAEHOAAEAQAHAAEEAAAHAAAEAAEHAQIEAQMEBwEECwEEDwAHDAEEAAEHNwEEAQAEAAEHAAMEAQIHAQIHAAAEAAAFCAAECgEEHQAEAAEHRgAEBAAEAAEHAAIEAQAEAQYHAAAEEgEHPQAEAQAHAAYEDAcEYgAEAQAHAAgECwYESQEEGwAEAQAEAQAEBAEHMQ0EAAAHAAQEAQEEBQoEASMECQAEZgMEAAAHAAUEAQEEAAEHAAEEFwEHAAEEBAIEEAMEDQAEAQAHAAEEBgAEDwAEYl8NAEcRAFcQ3QICBLIHAwQcAgQdAQQeAQRAAQQAAAcABgQABwcAAAQAAQcACgQJAAQtAgQAAAMAAAR1AQQiAAR2AgQAAwcAAQQAAgcEAQcAAAQABQcAAgTbAQEEAAEHAAAEOQAHAAAEAAAHAAYEAQAEAQAEAgcEAAUHAAkEAgAEMB4EMQMEAAAHLwkEAAMHAAIEJggEDAEEAAAHHgAHAAMEAAEHAAUEOAAEAAAHAAEEAAIHAAAEAAAHAAQEMAcHAAcEAAEHAAEEmAECBAEMBAAABwAGBAQABAYABAIABwABBMYBPwSLBAADAAAEAAAIAAEDGAYDMQ8DYCAE/hcCBI0BAARgHwSqBAUEaQEE1OsBAwQBCQQgAQRQAQSQAgAEAwAEBAAEFwEHAAEEAAAHBAAEUwEHMg8HAAEEGhEEDQAEJgcEGQoEAAAHAAAEDBwNAwIEAAAHLwAEAAEHAAMEAAEHAAEEAAEHAAAEJAAEQwUEAAEHAAEEAAEHAAEEDAAECAAEAAAHLgAEMwAEAQIEAgEEBQEEAQAEKQAHAAEEAAEHBQAHAAAE7AEBBwAABAABBwAABAABBwEABwAABMJXFhEEMBCiRgAE4QUPBBAPBM8BAAOeAQEEUAsDgQQABOIBAASVAQQEhg0CBAEBBAUDBCgCBAQABKUBAQS9BAMEQQQEvQIBBE8DBEYKBDEDBHoABwAABAAABzUOBCkABAIBBAoCBAAABy0CBwADBAABBwABBAIABQQABAoABTICBCQEBAAABwAHBBABBywABAwBBAAABzACBwAIBAAABwAABAEBBQUDBAEABwAABFwCBwACBAABBwADBAYABAIABJ0BAAQAAgcABwQVAQQAAQc3AQQBAAQAAAcAAAQAAwcCAQcCAQcAAAQJAAQKAQcCBgQDBARDAAQAAQcABQQBAAQCAAQBAgQAAAcBAQcAAgQAAAUAAAQOAQRSAgcABwQAAQcAAgQAAAcAAAQXAARRAAQAAQcABQQAAAcAAAQAAQcAAAQAAAcAAQQAAAcAAQTrAQAEAAEHAAMEAgMHAAEEAAAHAAEEGwEEUgIHAAcEAAEHAAAEAAAHAAEEagAEAAAHAAAEAAEHAAcEZQAEAAAHAAAEAgMEAAAHAAQEgAICBwAIBAAABwABBPUBAAQABAcBAQcCAwQAAAUAAAcAAAUAAAcAAASNAQIHAAMEAgEEAAMHAAAEAwAHHAkEKAUEAAAHAAAFAAMECAAECQUEAAEHAAIEKAUFAAwEAAAHAAEElQMABwAGBAEFBAAABwAABFIVBAEABwAGBAAABwABBAAABwABBHoFBAMABAEBBAEGBAAABQAABEIEBwEBBAEBBwAABAAABwAABNsCAQQAAQcJAQQAAAUAAAcwAQcABAQDAQcAAgQXAATVKQ8DAAAEBg4EyFkLBAACBwACBMATBAQ7BgSsBAARAwMR5AMABAE2BwcDBFEABAsBBKuZAQEEAQMD3CQtBAIWBJ4EBAQDBQQABwMABwQCBgQeAwSUAQIEuw82BAQxBAgABA4ABBYEBAEOBNAKBgQBEAQCBgQBAQQBBARkAASgAQYE9wIABD0DBPwDAwT+AQEE4AUGBG0GBJsRGQb7AwQEgJgwHwMAXwQAfwMA7wEEAI8cAw==";

    private static readonly Lazy<GraphemeData> Data = new(Decode);

    internal static OnigurumaGraphemeBreakType GetBreakType(int scalar)
    {
        var ranges = Data.Value.AllRanges;
        var low = 0;
        var high = ranges.Length;
        while (low < high)
        {
            var middle = (low + high) >> 1;
            if (scalar > ranges[middle].End)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low < ranges.Length && scalar >= ranges[low].Start
            ? ranges[low].Type
            : OnigurumaGraphemeBreakType.Other;
    }

    internal static int[] GetRanges(OnigurumaGraphemeBreakType type) =>
        Data.Value.RangesByType[(int)type];

    internal static int GetRangeCount(OnigurumaGraphemeBreakType type) =>
        GetRanges(type).Length / 2;

    private static GraphemeData Decode()
    {
        var encoded = Convert.FromBase64String(EncodedRanges);
        var ranges = new List<GraphemeRange>();
        var offset = 0;
        var previousEnd = -1;
        while (offset < encoded.Length)
        {
            var delta = ReadUnsigned(encoded, ref offset);
            var length = ReadUnsigned(encoded, ref offset);
            var type = (OnigurumaGraphemeBreakType)ReadUnsigned(encoded, ref offset);
            var start = checked(previousEnd + 1 + delta);
            var end = checked(start + length);
            ranges.Add(new GraphemeRange(start, end, type));
            previousEnd = end;
        }

        // unicode_egcb_data.c spells each Hangul syllable separately because LV/LVT
        // alternate every 28 scalars. Generate that regular portion exactly at runtime.
        for (var lv = 0xAC00; lv <= 0xD7A3; lv += 28)
        {
            ranges.Add(new GraphemeRange(lv, lv, OnigurumaGraphemeBreakType.Lv));
            ranges.Add(new GraphemeRange(lv + 1, lv + 27, OnigurumaGraphemeBreakType.Lvt));
        }

        var allRanges = ranges.OrderBy(range => range.Start).ToArray();
        var rangesByType = new int[18][];
        for (var type = 0; type < rangesByType.Length; type++)
        {
            rangesByType[type] = allRanges
                .Where(range => (int)range.Type == type)
                .SelectMany(range => new[] { range.Start, range.End })
                .ToArray();
        }

        return new GraphemeData(allRanges, rangesByType);
    }

    private static int ReadUnsigned(byte[] encoded, ref int offset)
    {
        var value = 0;
        var shift = 0;
        while (true)
        {
            var current = encoded[offset++];
            value |= (current & 0x7F) << shift;
            if ((current & 0x80) == 0)
            {
                return value;
            }

            shift += 7;
        }
    }

    private sealed record GraphemeData(
        GraphemeRange[] AllRanges,
        int[][] RangesByType);

    private readonly record struct GraphemeRange(
        int Start,
        int End,
        OnigurumaGraphemeBreakType Type);
}
