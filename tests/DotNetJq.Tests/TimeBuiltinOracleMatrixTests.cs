using DotNetJq.Compatibility.Time;
using DotNetJq.Port;
using DotNetJq.Tests.Harness;

namespace DotNetJq.Tests;

/// <summary>
/// Observations from the jq-1.8.2 Linux/glibc-2.39 oracle.  These are deliberately
/// constants rather than expectations computed with .NET time APIs: the contract
/// under test is jq/libc behavior, including its odd edges.  The four gap rows whose
/// outcome depends on the host TZif encoding compare with the pinned jq oracle on the
/// same host when it is available and retain their original frozen value as the
/// oracle-free development baseline.
/// </summary>
public sealed class TimeBuiltinOracleMatrixTests
{
    private static readonly System.Text.Json.JsonSerializerOptions JqJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [Fact]
    public void CLocaleStrftimeDirectiveMatrixMatchesPinnedOracle()
    {
        const string filter = """[2024,1,29,23,5,7,4,59]|strftime("%a|%A|%b|%B|%h|%c|%C|%d|%D|%e|%F|%g|%G|%H|%I|%j|%k|%l|%m|%M|%n|%p|%P|%r|%R|%s|%S|%t|%T|%u|%U|%V|%w|%W|%x|%X|%y|%Y|%z|%Z|%%|%q|%+|%Q|%")""";
        const string expected = "Thu|Thursday|Feb|February|Feb|Thu Feb 29 23:05:07 2024|20|29|02/29/24|29|2024-02-29|24|2024|23|11|060|23|11|02|05|\n|PM|pm|11:05:07 PM|23:05|1709247907|07|\t|23:05:07|4|08|09|4|09|02/29/24|23:05:07|24|2024|+0000|GMT|%|%q|%+|%Q|%";

        Assert.Equal(expected, ExecuteOne(filter).GetString());
    }

    [Fact]
    public void CLocaleStrftimeFlagsWidthsUnknownsAndBufferBoundaryMatchPinnedOracle()
    {
        const string filter = """[2024,0,3,5,6,7,3,2] as $t | [($t|strftime("%d")),($t|strftime("%-d")),($t|strftime("%_d")),($t|strftime("%0d")),($t|strftime("%5d")),($t|strftime("%-5d")),($t|strftime("%_5d")),($t|strftime("%05d")),($t|strftime("%10A")),($t|strftime("%010A")),($t|strftime("%^10A")),($t|strftime("%#p")),($t|strftime("%#Z")),($t|strftime("%10Q")),($t|strftime("%104d")|length),($t|try strftime("%105d") catch .)]""";
        const string expected = """["03","3"," 3","03","00003","    3","    3","00003"," Wednesday","0Wednesday"," WEDNESDAY","am","gmt","      %10Q",104,"strftime/1: unknown system failure"]""";

        Assert.Equal(expected, ExecuteOne(filter).GetRawText());
    }

    [Theory]
    [InlineData(
        "UTC",
        false,
        """["+0000","+0","+   0","+0000","+0000"," +0000","  +0000","   +0000","    +00000","0000+00000","    +    0","    +    0","000000000+0000000000","+0000","+0000","+0000","+0000","%","%","         %","000000000%"]""")]
    [InlineData(
        "Europe/Paris",
        true,
        """["+0100","+100","+ 100","+0100","+0100"," +0100","  +0100","   +0100","    +00100","0000+00100","    +  100","    +  100","000000000+0000000100","+0100","+0100","+0100","+0100","%","%","         %","000000000%"]""")]
    public void ZoneOffsetFlagsWidthsAndPercentModifiersMatchPinnedOracle(
        string timeZone,
        bool local,
        string expected)
    {
        var formatter = local ? "strflocaltime" : "strftime";
        var filter = $$"""[2024,0,3,5,6,7,3,2] as $t | ["%z","%-z","%_z","%0z","%1z","%2z","%3z","%4z","%5z","%05z","%-5z","%_5z","%010z","%^z","%#z","%Ez","%Oz","%E%","%O%","%10E%","%010O%"] | map(. as $f | $t | {{formatter}}($f))""";

        Assert.Equal(expected, ExecuteOne(filter, timeZone).GetRawText());
    }

    [Theory]
    [InlineData(
        1e100,
        "\"?|?|?|?|?|2147483635|2147483635|2147483635:2147483647:2147483647 PM|CET\"")]
    [InlineData(
        -1e100,
        "\"?|?|?|?|?|-2147483648|-2147483648|-2147483648:-2147483648:-2147483648 AM|CET\"")]
    public void FailedTimegmRawFieldsRemainSafeAndMatchPinnedOracle(
        double field,
        string expected)
    {
        var literal = field < 0 ? "-1e100" : "1e100";
        var filter = $"[{string.Join(',', Enumerable.Repeat(literal, 8))}]|strftime(\"%a|%A|%b|%B|%h|%I|%l|%r|%Z\")";

        Assert.Equal(expected, ExecuteOne(filter, "Europe/Paris").GetRawText());
    }

    [Fact]
    public void IncompleteFormatsFailedNormalizationAndAlternateYearMatchPinnedOracle()
    {
        const string filter = """[2024,0,1,0,0,0,1,0] as $t | [("20 24"|strptime("%C %Oy")),([2147485547,2147483647,1,0,0,0]|strftime("%Y|%m|%d|%H|%M|%S|%j|%w|%s|%G|%g|%V|%U|%W|%C|%y")),($t|strftime("%10")),($t|strftime("%-10")),($t|strftime("%_10")),($t|strftime("%010")),($t|strftime("%^10")),($t|strftime("%#10")),($t|strftime("%E")),($t|strftime("%OE")),($t|strftime("%"))]""";
        const string expected = """[[2000,0,0,0,0,0,5,-1],"-2147481749|-2147483648|01|00|00|00|001|0|-1|-2147481750|50|52|01|00|-21474818|47","       %10","      %-10","      %_10","000000%010","      %^10","      %#10","%E","%OE","%"]""";

        Assert.Equal(expected, ExecuteOne(filter).GetRawText());
    }

    [Fact]
    public void CLocaleStrptimeDirectiveAndRawStructTmMatrixMatchesPinnedOracle()
    {
        const string filter = """["Thu","Thursday","Feb","February","Feb","Thu Feb 29 23:05:07 2024","20","29","02/29/24"," 3","2024-02-29","24","2024","23","11","060","23","11","02","05","   ","PM","11:05:07 PM","23:05","1709247907","07","\t","23:05:07","4","08","09","4","09","02/29/24","23:05:07","24","2024","Z","%"] as $x | [($x[0]|strptime("%a")),($x[1]|strptime("%A")),($x[2]|strptime("%b")),($x[3]|strptime("%B")),($x[4]|strptime("%h")),($x[5]|strptime("%c")),($x[6]|strptime("%C")),($x[7]|strptime("%d")),($x[8]|strptime("%D")),($x[9]|strptime("%e")),($x[10]|strptime("%F")),($x[11]|strptime("%g")),($x[12]|strptime("%G")),($x[13]|strptime("%H")),($x[14]|strptime("%I")),($x[15]|strptime("%j")),($x[16]|strptime("%k")),($x[17]|strptime("%l")),($x[18]|strptime("%m")),($x[19]|strptime("%M")),($x[20]|strptime("%n")),($x[21]|strptime("%p")),($x[22]|strptime("%r")),($x[23]|strptime("%R")),($x[24]|strptime("%s")),($x[25]|strptime("%S")),($x[26]|strptime("%t")),($x[27]|strptime("%T")),($x[28]|strptime("%u")),($x[29]|strptime("%U")),($x[30]|strptime("%V")),($x[31]|strptime("%w")),($x[32]|strptime("%W")),($x[33]|strptime("%x")),($x[34]|strptime("%X")),($x[35]|strptime("%y")),($x[36]|strptime("%Y")),($x[37]|strptime("%z")),($x[38]|strptime("%%"))]""";
        const string expected = """[[1900,0,0,0,0,0,4,367],[1900,0,0,0,0,0,4,367],[1900,1,0,0,0,0,3,30],[1900,1,0,0,0,0,3,30],[1900,1,0,0,0,0,3,30],[2024,1,29,23,5,7,4,59],[2000,0,0,0,0,0,5,-1],[1900,0,29,0,0,0,1,28],[2024,1,29,0,0,0,4,59],[1900,0,3,0,0,0,3,2],[2024,1,29,0,0,0,4,59],[1900,0,0,0,0,0,8,367],[1900,0,0,0,0,0,8,367],[1900,0,0,23,0,0,8,367],[1900,0,0,11,0,0,8,367],[1900,0,0,0,0,0,8,59],[1900,0,0,23,0,0,8,367],[1900,0,0,11,0,0,8,367],[1900,1,0,0,0,0,3,30],[1900,0,0,0,5,0,8,367],[1900,0,0,0,0,0,8,367],[1900,0,0,0,0,0,8,367],[1900,0,0,23,5,7,8,367],[1900,0,0,23,5,0,8,367],[2024,1,29,23,5,7,4,59],[1900,0,0,0,0,7,8,367],[1900,0,0,0,0,0,8,367],[1900,0,0,23,5,7,8,367],[1900,0,0,0,0,0,4,367],[1900,0,0,0,0,0,8,367],[1900,0,0,0,0,0,8,367],[1900,0,0,0,0,0,4,367],[1900,0,0,0,0,0,8,367],[2024,1,29,0,0,0,4,59],[1900,0,0,23,5,7,8,367],[2024,0,0,0,0,0,0,-1],[2024,0,0,0,0,0,0,-1],[1900,0,0,0,0,0,8,367],[1900,0,0,0,0,0,8,367]]""";

        Assert.Equal(expected, ExecuteOne(filter).GetRawText());
    }

    [Fact]
    public void StrptimeModifiersTailsRangesFailuresAndNulBoundaryMatchPinnedOracle()
    {
        const string filter = """[("2024 tail"|strptime("%Y")),("2024\tfoo"|strptime("%Y")),("2024x"|try strptime("%Y") catch .),("2024"|strptime("%___999Y")),("2024"|strptime("%EY")),("24"|try strptime("%Ey") catch .),("03"|strptime("%Od")),("pm"|try strptime("%P") catch .),("anything"|try strptime("%Q") catch .),("+99:59"|strptime("%z")),("+01:60"|try strptime("%z") catch .),("2024\u0000junk"|strptime("%Y\u0000%Q"))]""";
        const string expected = """[[2024,0,0,0,0,0,0,-1," tail"],[2024,0,0,0,0,0,0,-1,"\tfoo"],"date \"2024x\" does not match format \"%Y\"",[2024,0,0,0,0,0,0,-1],[2024,0,0,0,0,0,0,-1],"date \"24\" does not match format \"%Ey\"",[1900,0,3,0,0,0,3,2],"date \"pm\" does not match format \"%P\"","date \"anything\" does not match format \"%Q\"",[1900,0,0,0,0,0,8,367],"date \"+01:60\" does not match format \"%z\"",[2024,0,0,0,0,0,0,-1]]""";

        Assert.Equal(expected, ExecuteOne(filter).GetRawText());
    }

    [Fact]
    public void EpochRangeFractionsNormalizationAndReservedValuesMatchPinnedOracle()
    {
        const string filter = """[(0|gmtime),(-0.1|gmtime),(-1.9|gmtime),(67768036191676792|gmtime),(67768036191676800|try gmtime catch .),(-67768040609740800|gmtime),(-67768040609740808|try gmtime catch .),([2024,12,32,25,61,62,0,0]|mktime),([2024,-1,0,-1,-1,-1,0,0]|mktime),([1970,0,1,0,0,-1,0,0]|try mktime catch .),([1969,11,31,23,59,58,0,0]|try mktime catch .),([1.9,2.9,3.9,4.9,5.9,6.9,7.9,8.9]|mktime)]""";
        const string expected = """[[1970,0,1,0,0,0,4,0],[1970,0,1,0,0,0.9,4,0],[1969,11,31,23,59,59.1,3,364],[-2147481749,11,31,23,59,52,3,364],"error converting number of seconds since epoch to datetime",[-2147481748,0,1,0,0,0,4,0],"error converting number of seconds since epoch to datetime",1738461722,1701298739,"invalid gmtime representation","mktime not supported on this platform",-62098775694]""";

        Assert.Equal(expected, ExecuteOne(filter).GetRawText());
    }

    [Fact]
    public void FromdateTodateAliasesMatchPinnedOracle()
    {
        const string filter = """[("2024-02-29T23:05:07Z"|fromdateiso8601),(1709247907|todateiso8601),("2024-02-29T23:05:07Z"|fromdate),(1709247907|todate)]""";
        const string expected = """[1709247907,"2024-02-29T23:05:07Z",1709247907,"2024-02-29T23:05:07Z"]""";

        Assert.Equal(expected, ExecuteOne(filter).GetRawText());
    }

    [Theory]
    [InlineData("UTC", """["1970-01-01 00:00:00 +0000 UTC",[2024,1,29,23,5,7,4,59],[2024,1,29,23,5,7,4,59],"0 +0000 GMT","1719792000 +0000 GMT"]""")]
    [InlineData("Europe/Paris", """["1970-01-01 01:00:00 +0100 CET",[2024,2,1,0,5,7,5,60],[2024,2,1,0,5,7,5,60],"-3600 +0000 GMT","1719788400 +0000 GMT"]""")]
    [InlineData("America/New_York", """["1969-12-31 19:00:00 -0500 EST",[2024,1,29,18,5,7,4,59],[2024,1,29,18,5,7,4,59],"18000 +0000 GMT","1719810000 +0000 GMT"]""")]
    [InlineData("Invalid/Foo", """["1970-01-01 00:00:00 +0000 Invalid",[2024,1,29,23,5,7,4,59],[2024,1,29,23,5,7,4,59],"0 +0000 GMT","1719792000 +0000 GMT"]""")]
    [InlineData("ABC3", """["1969-12-31 21:00:00 -0300 ABC",[2024,1,29,20,5,7,4,59],[2024,1,29,20,5,7,4,59],"10800 +0000 GMT","1719802800 +0000 GMT"]""")]
    [InlineData("<+03>-3", """["1970-01-01 03:00:00 +0300 +03",[2024,2,1,2,5,7,5,60],[2024,2,1,2,5,7,5,60],"-10800 +0000 GMT","1719781200 +0000 GMT"]""")]
    public void TzifPosixAndInvalidTimeZoneMatricesMatchPinnedOracle(
        string timeZone,
        string expected)
    {
        const string filter = """[(0|strflocaltime("%F %T %z %Z")),(1709247907|localtime),("1709247907"|strptime("%s")),([1970,0,1,0,0,0,4,0]|strftime("%s %z %Z")),([2024,6,1,0,0,0,1,182]|strftime("%s %z %Z"))]""";

        Assert.Equal(expected, ExecuteOne(filter, timeZone).GetRawText());
    }

    [Theory]
    [InlineData("UTC0", """["1900-01-01 00:00:00 +0000 UTC","1970-01-01 00:00:00 +0000 UTC","2023-11-14 22:13:20 +0000 UTC","2024-03-10 07:00:00 +0000 UTC","2024-11-03 06:00:00 +0000 UTC","2100-01-01 00:00:00 +0000 UTC"]""")]
    [InlineData("GMT0", """["1900-01-01 00:00:00 +0000 GMT","1970-01-01 00:00:00 +0000 GMT","2023-11-14 22:13:20 +0000 GMT","2024-03-10 07:00:00 +0000 GMT","2024-11-03 06:00:00 +0000 GMT","2100-01-01 00:00:00 +0000 GMT"]""")]
    [InlineData("ABC3", """["1899-12-31 21:00:00 -0300 ABC","1969-12-31 21:00:00 -0300 ABC","2023-11-14 19:13:20 -0300 ABC","2024-03-10 04:00:00 -0300 ABC","2024-11-03 03:00:00 -0300 ABC","2099-12-31 21:00:00 -0300 ABC"]""")]
    [InlineData("ABC-3", """["1900-01-01 03:00:00 +0300 ABC","1970-01-01 03:00:00 +0300 ABC","2023-11-15 01:13:20 +0300 ABC","2024-03-10 10:00:00 +0300 ABC","2024-11-03 09:00:00 +0300 ABC","2100-01-01 03:00:00 +0300 ABC"]""")]
    [InlineData("ABC+3", """["1899-12-31 21:00:00 -0300 ABC","1969-12-31 21:00:00 -0300 ABC","2023-11-14 19:13:20 -0300 ABC","2024-03-10 04:00:00 -0300 ABC","2024-11-03 03:00:00 -0300 ABC","2099-12-31 21:00:00 -0300 ABC"]""")]
    [InlineData("ABC3:30", """["1899-12-31 20:30:00 -0330 ABC","1969-12-31 20:30:00 -0330 ABC","2023-11-14 18:43:20 -0330 ABC","2024-03-10 03:30:00 -0330 ABC","2024-11-03 02:30:00 -0330 ABC","2099-12-31 20:30:00 -0330 ABC"]""")]
    [InlineData("ABC3:30:45", """["1899-12-31 20:29:15 -0330 ABC","1969-12-31 20:29:15 -0330 ABC","2023-11-14 18:42:35 -0330 ABC","2024-03-10 03:29:15 -0330 ABC","2024-11-03 02:29:15 -0330 ABC","2099-12-31 20:29:15 -0330 ABC"]""")]
    [InlineData("ABC167", """["1899-12-31 00:00:00 -2400 ABC","1969-12-31 00:00:00 -2400 ABC","2023-11-13 22:13:20 -2400 ABC","2024-03-09 07:00:00 -2400 ABC","2024-11-02 06:00:00 -2400 ABC","2099-12-31 00:00:00 -2400 ABC"]""")]
    [InlineData("ABC-167", """["1900-01-02 00:00:00 +2400 ABC","1970-01-02 00:00:00 +2400 ABC","2023-11-15 22:13:20 +2400 ABC","2024-03-11 07:00:00 +2400 ABC","2024-11-04 06:00:00 +2400 ABC","2100-01-02 00:00:00 +2400 ABC"]""")]
    [InlineData("<+03>-3", """["1900-01-01 03:00:00 +0300 +03","1970-01-01 03:00:00 +0300 +03","2023-11-15 01:13:20 +0300 +03","2024-03-10 10:00:00 +0300 +03","2024-11-03 09:00:00 +0300 +03","2100-01-01 03:00:00 +0300 +03"]""")]
    [InlineData("<-03>3", """["1899-12-31 21:00:00 -0300 -03","1969-12-31 21:00:00 -0300 -03","2023-11-14 19:13:20 -0300 -03","2024-03-10 04:00:00 -0300 -03","2024-11-03 03:00:00 -0300 -03","2099-12-31 21:00:00 -0300 -03"]""")]
    [InlineData("<LONG NAME>0", """["1900-01-01 00:00:00 +0000 ","1970-01-01 00:00:00 +0000 ","2023-11-14 22:13:20 +0000 ","2024-03-10 07:00:00 +0000 ","2024-11-03 06:00:00 +0000 ","2100-01-01 00:00:00 +0000 "]""")]
    [InlineData("EST5EDT", """["1899-12-31 19:00:00 -0500 EST","1969-12-31 19:00:00 -0500 EST","2023-11-14 17:13:20 -0500 EST","2024-03-10 03:00:00 -0400 EDT","2024-11-03 01:00:00 -0400 EDT","2099-12-31 19:00:00 -0500 EST"]""")]
    [InlineData("EST5EDT4", """["1899-12-31 19:00:00 -0500 EST","1969-12-31 19:00:00 -0500 EST","2023-11-14 17:13:20 -0500 EST","2024-03-10 03:00:00 -0400 EDT","2024-11-03 01:00:00 -0400 EDT","2099-12-31 19:00:00 -0500 EST"]""")]
    [InlineData("EST5EDT4:30", """["1899-12-31 19:00:00 -0500 EST","1969-12-31 19:00:00 -0500 EST","2023-11-14 17:13:20 -0500 EST","2024-03-10 02:30:00 -0430 EDT","2024-11-03 01:00:00 -0430 EDT","2099-12-31 19:00:00 -0500 EST"]""")]
    [InlineData("EST5EDT,M3.2.0/2,M11.1.0/2", """["1899-12-31 19:00:00 -0500 EST","1969-12-31 19:00:00 -0500 EST","2023-11-14 17:13:20 -0500 EST","2024-03-10 03:00:00 -0400 EDT","2024-11-03 01:00:00 -0400 EDT","2099-12-31 19:00:00 -0500 EST"]""")]
    [InlineData("EST5EDT,J60/1,J300/3", """["1899-12-31 19:00:00 -0500 EST","1969-12-31 19:00:00 -0500 EST","2023-11-14 17:13:20 -0500 EST","2024-03-10 03:00:00 -0400 EDT","2024-11-03 01:00:00 -0500 EST","2099-12-31 19:00:00 -0500 EST"]""")]
    [InlineData("EST5EDT,60/1,300/3", """["1899-12-31 19:00:00 -0500 EST","1969-12-31 19:00:00 -0500 EST","2023-11-14 17:13:20 -0500 EST","2024-03-10 03:00:00 -0400 EDT","2024-11-03 01:00:00 -0500 EST","2099-12-31 19:00:00 -0500 EST"]""")]
    [InlineData("AAA0BBB,J1/0,J365/24", """["1900-01-01 00:00:00 +0000 AAA","1970-01-01 01:00:00 +0100 BBB","2023-11-14 23:13:20 +0100 BBB","2024-03-10 08:00:00 +0100 BBB","2024-11-03 07:00:00 +0100 BBB","2100-01-01 01:00:00 +0100 BBB"]""")]
    [InlineData("AAA0BBB,M1.1.0/-2,M12.5.0/26", """["1900-01-01 00:00:00 +0000 AAA","1970-01-01 00:00:00 +0000 AAA","2023-11-14 23:13:20 +0100 BBB","2024-03-10 08:00:00 +0100 BBB","2024-11-03 07:00:00 +0100 BBB","2100-01-01 00:00:00 +0000 AAA"]""")]
    [InlineData("AAA0BBB,M3.2.0/2s,M11.1.0/2", """["1900-01-01 01:00:00 +0100 BBB","1970-01-01 00:00:00 +0000 AAA","2023-11-14 23:13:20 +0100 BBB","2024-03-10 08:00:00 +0100 BBB","2024-11-03 07:00:00 +0100 BBB","2100-01-01 00:00:00 +0100 BBB"]""")]
    [InlineData("AAA0BBB,M3.2.0/2u,M11.1.0/2", """["1900-01-01 01:00:00 +0100 BBB","1970-01-01 00:00:00 +0000 AAA","2023-11-14 23:13:20 +0100 BBB","2024-03-10 08:00:00 +0100 BBB","2024-11-03 07:00:00 +0100 BBB","2100-01-01 00:00:00 +0100 BBB"]""")]
    [InlineData("AAA0BBB,M3.2.0/2w,M11.1.0/2", """["1900-01-01 01:00:00 +0100 BBB","1970-01-01 00:00:00 +0000 AAA","2023-11-14 23:13:20 +0100 BBB","2024-03-10 08:00:00 +0100 BBB","2024-11-03 07:00:00 +0100 BBB","2100-01-01 00:00:00 +0100 BBB"]""")]
    [InlineData("AAA0BBB-2,M3.2.0/2,M11.1.0/2", """["1900-01-01 00:00:00 +0000 AAA","1970-01-01 00:00:00 +0000 AAA","2023-11-14 22:13:20 +0000 AAA","2024-03-10 09:00:00 +0200 BBB","2024-11-03 06:00:00 +0000 AAA","2100-01-01 00:00:00 +0000 AAA"]""")]
    [InlineData("AAA0BBB-1:30,M3.2.0/2,M11.1.0/2", """["1900-01-01 00:00:00 +0000 AAA","1970-01-01 00:00:00 +0000 AAA","2023-11-14 22:13:20 +0000 AAA","2024-03-10 08:30:00 +0130 BBB","2024-11-03 06:00:00 +0000 AAA","2100-01-01 00:00:00 +0000 AAA"]""")]
    [InlineData("AAA3BBB5,M3.2.0/2,M11.1.0/2", """["1899-12-31 21:00:00 -0300 AAA","1969-12-31 21:00:00 -0300 AAA","2023-11-14 19:13:20 -0300 AAA","2024-03-10 02:00:00 -0500 BBB","2024-11-03 01:00:00 -0500 BBB","2099-12-31 21:00:00 -0300 AAA"]""")]
    [InlineData("ABC3DEF1", """["1899-12-31 21:00:00 -0300 ABC","1969-12-31 21:00:00 -0300 ABC","2023-11-14 19:13:20 -0300 ABC","2024-03-10 04:00:00 -0300 ABC","2024-11-03 03:00:00 -0300 ABC","2099-12-31 19:00:00 -0500 EST"]""")]
    public void PosixTimeZoneGrammarAndDefaultRuleHistoryMatchPinnedOracle(
        string timeZone,
        string expected)
    {
        const string filter = """[-2208988800,0,1700000000,1710054000,1730613600,4102444800]|map(strflocaltime("%F %T %z %Z"))""";

        Assert.Equal(expected, ExecuteOne(filter, timeZone).GetRawText());
    }

    [Theory]
    [InlineData(
        "Europe/Paris",
        """[([2024,2,31,2,30,0,0,0]|strflocaltime("%F %T %z %Z %s")),([2024,9,27,2,30,0,0,0]|strflocaltime("%F %T %z %Z %s"))]""",
        """["2024-03-31 03:30:00 +0200 CEST 1711848600","2024-10-27 02:30:00 +0200 CEST 1729989000"]""")]
    [InlineData(
        "America/New_York",
        """[([2024,2,10,2,30,0,0,0]|strflocaltime("%F %T %z %Z %s")),([2024,10,3,1,30,0,0,0]|strflocaltime("%F %T %z %Z %s"))]""",
        """["2024-03-10 03:30:00 -0400 EDT 1710055800","2024-11-03 01:30:00 -0400 EDT 1730611800"]""")]
    public void DstGapAndFoldNormalizationMatchesPinnedOracle(
        string timeZone,
        string filter,
        string expected)
    {
        Assert.Equal(expected, ExecuteOne(filter, timeZone).GetRawText());
    }

    [Theory]
    [InlineData("Europe/Paris", """[([2024,9,27,2,30,0,0,0]|strflocaltime("%s %z %Z"))]""", """["1729992600 +0100 CET"]""")]
    [InlineData("Europe/Paris", """[([2024,2,31,2,30,0,0,0]|strflocaltime("%s %z %Z")),([2024,9,27,2,30,0,0,0]|strflocaltime("%s %z %Z"))]""", """["1711848600 +0200 CEST","1729989000 +0200 CEST"]""")]
    [InlineData("Europe/Paris", """[([2024,9,27,2,30,0,0,0]|strflocaltime("%s %z %Z")),([2024,2,31,2,30,0,0,0]|strflocaltime("%s %z %Z"))]""", """["1729992600 +0100 CET","1711848600 +0200 CEST"]""")]
    [InlineData("Europe/Paris", """[([2024,6,1,12,0,0,0,0]|strflocaltime("%s %z %Z")),([2024,9,27,2,30,0,0,0]|strflocaltime("%s %z %Z"))]""", """["1719828000 +0200 CEST","1729989000 +0200 CEST"]""")]
    [InlineData("Europe/Paris", """[([2024,9,27,2,30,0,0,0]|strflocaltime("%s %z %Z")),([2024,6,1,12,0,0,0,0]|strflocaltime("%s %z %Z"))]""", """["1729992600 +0100 CET","1719828000 +0200 CEST"]""")]
    [InlineData("Europe/Paris", """[([2024,0,1,12,0,0,0,0]|strflocaltime("%s %z %Z")),([2024,9,27,2,30,0,0,0]|strflocaltime("%s %z %Z"))]""", """["1704106800 +0100 CET","1729992600 +0100 CET"]""")]
    [InlineData("Europe/Paris", """[([2024,9,27,2,30,0,0,0]|strflocaltime("%s %z %Z")),([2024,0,1,12,0,0,0,0]|strflocaltime("%s %z %Z"))]""", """["1729992600 +0100 CET","1704106800 +0100 CET"]""")]
    [InlineData("Europe/Paris", """[([2024,9,27,2,30,0,0,0]|strflocaltime("%s %z %Z")),([2024,9,27,2,30,0,0,0]|strflocaltime("%s %z %Z"))]""", """["1729992600 +0100 CET","1729992600 +0100 CET"]""")]
    [InlineData("Europe/Paris", """[([2024,9,27,2,30,0,0,0]|strflocaltime("%s %z %Z")),([2024,2,31,2,30,0,0,0]|strflocaltime("%s %z %Z")),([2024,9,27,2,30,0,0,0]|strflocaltime("%s %z %Z"))]""", """["1729992600 +0100 CET","1711848600 +0200 CEST","1729989000 +0200 CEST"]""")]
    [InlineData("Europe/Paris", """[([2024,9,27,2,30,0,0,0]|strflocaltime("%s %z %Z")),([2024,6,1,12,0,0,0,0]|strflocaltime("%s %z %Z")),([2024,9,27,2,30,0,0,0]|strflocaltime("%s %z %Z"))]""", """["1729992600 +0100 CET","1719828000 +0200 CEST","1729989000 +0200 CEST"]""")]
    [InlineData("Europe/Paris", """[([2024,9,27,2,30,0,0,0]|strflocaltime("%s %z %Z")),([2024,0,1,12,0,0,0,0]|strflocaltime("%s %z %Z")),([2024,9,27,2,30,0,0,0]|strflocaltime("%s %z %Z"))]""", """["1729992600 +0100 CET","1704106800 +0100 CET","1729992600 +0100 CET"]""")]
    [InlineData("America/New_York", """[([2024,10,3,1,30,0,0,0]|strflocaltime("%s %z %Z"))]""", """["1730611800 -0400 EDT"]""")]
    [InlineData("America/New_York", """[([2024,2,10,2,30,0,0,0]|strflocaltime("%s %z %Z")),([2024,10,3,1,30,0,0,0]|strflocaltime("%s %z %Z"))]""", """["1710055800 -0400 EDT","1730611800 -0400 EDT"]""")]
    [InlineData("America/New_York", """[([2024,10,3,1,30,0,0,0]|strflocaltime("%s %z %Z")),([2024,2,10,2,30,0,0,0]|strflocaltime("%s %z %Z"))]""", """["1730611800 -0400 EDT","1710055800 -0400 EDT"]""")]
    [InlineData("America/New_York", """[([2024,6,1,12,0,0,0,0]|strflocaltime("%s %z %Z")),([2024,10,3,1,30,0,0,0]|strflocaltime("%s %z %Z"))]""", """["1719849600 -0400 EDT","1730611800 -0400 EDT"]""")]
    [InlineData("America/New_York", """[([2024,10,3,1,30,0,0,0]|strflocaltime("%s %z %Z")),([2024,6,1,12,0,0,0,0]|strflocaltime("%s %z %Z"))]""", """["1730611800 -0400 EDT","1719849600 -0400 EDT"]""")]
    [InlineData("America/New_York", """[([2024,0,1,12,0,0,0,0]|strflocaltime("%s %z %Z")),([2024,10,3,1,30,0,0,0]|strflocaltime("%s %z %Z"))]""", """["1704128400 -0500 EST","1730615400 -0500 EST"]""")]
    [InlineData("America/New_York", """[([2024,10,3,1,30,0,0,0]|strflocaltime("%s %z %Z")),([2024,0,1,12,0,0,0,0]|strflocaltime("%s %z %Z"))]""", """["1730611800 -0400 EDT","1704128400 -0500 EST"]""")]
    [InlineData("America/New_York", """[([2024,10,3,1,30,0,0,0]|strflocaltime("%s %z %Z")),([2024,10,3,1,30,0,0,0]|strflocaltime("%s %z %Z"))]""", """["1730611800 -0400 EDT","1730611800 -0400 EDT"]""")]
    [InlineData("America/New_York", """[([2024,10,3,1,30,0,0,0]|strflocaltime("%s %z %Z")),([2024,2,10,2,30,0,0,0]|strflocaltime("%s %z %Z")),([2024,10,3,1,30,0,0,0]|strflocaltime("%s %z %Z"))]""", """["1730611800 -0400 EDT","1710055800 -0400 EDT","1730611800 -0400 EDT"]""")]
    [InlineData("America/New_York", """[([2024,10,3,1,30,0,0,0]|strflocaltime("%s %z %Z")),([2024,6,1,12,0,0,0,0]|strflocaltime("%s %z %Z")),([2024,10,3,1,30,0,0,0]|strflocaltime("%s %z %Z"))]""", """["1730611800 -0400 EDT","1719849600 -0400 EDT","1730611800 -0400 EDT"]""")]
    [InlineData("America/New_York", """[([2024,10,3,1,30,0,0,0]|strflocaltime("%s %z %Z")),([2024,0,1,12,0,0,0,0]|strflocaltime("%s %z %Z")),([2024,10,3,1,30,0,0,0]|strflocaltime("%s %z %Z"))]""", """["1730611800 -0400 EDT","1704128400 -0500 EST","1730615400 -0500 EST"]""")]
    public void MktimeOffsetGuessAndFoldCallOrderMatchPinnedOracle(
        string timeZone,
        string filter,
        string expected)
    {
        Assert.Equal(expected, ExecuteOne(filter, timeZone).GetRawText());
    }

    [Theory]
    [InlineData("Europe/Dublin", "[2024,2,31,1,30,0,0,0]", "2024-03-31 00:30:00 +0000 GMT 1711845000")]
    [InlineData("Africa/Casablanca", "[2024,3,14,2,30,0,0,0]", "2024-04-14 01:30:00 +0000 +00 1713058200")]
    [InlineData("Pacific/Apia", "[2011,11,30,12,0,0,0,0]", "2011-12-29 12:00:00 -1000 -10 1325196000")]
    [InlineData("Pacific/Kwajalein", "[1993,7,21,12,0,0,0,0]", "1993-08-20 12:00:00 -1200 -12 745891200")]
    [InlineData("Asia/Kathmandu", "[1986,0,1,0,5,0,0,0]", "1985-12-31 23:50:00 +0530 +0530 504901200")]
    public async Task NegativeDstAndDateLineGapsUseGlibcProbeOscillation(
        string timeZone,
        string input,
        string expected)
    {
        var filter = $"{input}|strflocaltime(\"%F %T %z %Z %s\")";
        var actual = ExecuteOne(filter, timeZone).GetRawText();
        if (timeZone is "Europe/Dublin" or "Africa/Casablanca")
        {
            await AssertMatchesInstalledTzifOracleAsync(
                filter,
                timeZone,
                actual,
                System.Text.Json.JsonSerializer.Serialize(expected, JqJsonOptions));
            return;
        }

        Assert.Equal(
            expected,
            System.Text.Json.JsonDocument.Parse(actual).RootElement.GetString());
    }

    [Theory]
    [InlineData(
        "Europe/Dublin",
        """[([2024,2,31,1,30,0,0,0]|strflocaltime("%F %T %z %Z")),([2024,9,27,1,30,0,0,0]|strflocaltime("%F %T %z %Z %s"))]""",
        """["2024-03-31 00:30:00 +0000 GMT","2024-10-27 01:30:00 +0100 IST 1729989000"]""")]
    [InlineData(
        "Europe/Dublin",
        """[([2024,2,31,1,30,0,0,0]|strflocaltime("%F %T %z %Z %s")),([2024,9,27,1,30,0,0,0]|strflocaltime("%F %T %z %Z %s"))]""",
        """["2024-03-31 00:30:00 +0000 GMT 1711845000","2024-10-27 01:30:00 +0000 GMT 1729992600"]""")]
    [InlineData(
        "Pacific/Apia",
        """[([2011,11,28,12,0,0,0,0]|strflocaltime("%F %T %z %Z")),([2011,11,30,12,0,0,0,0]|strflocaltime("%F %T %z %Z %s"))]""",
        """["2011-12-28 12:00:00 -1000 -10","2011-12-31 12:00:00 +1400 +14 1325282400"]""")]
    [InlineData(
        "Pacific/Apia",
        """[([2012,0,2,12,0,0,0,0]|strflocaltime("%F %T %z %Z")),([2011,11,30,12,0,0,0,0]|strflocaltime("%F %T %z %Z %s"))]""",
        """["2012-01-02 12:00:00 +1400 +14","2011-12-29 12:00:00 -1000 -10 1325196000"]""")]
    [InlineData(
        "Pacific/Kwajalein",
        """[([1993,7,20,12,0,0,0,0]|strflocaltime("%F %T %z %Z")),([1993,7,21,12,0,0,0,0]|strflocaltime("%F %T %z %Z %s"))]""",
        """["1993-08-20 12:00:00 -1200 -12","1993-08-22 12:00:00 +1200 +12 745977600"]""")]
    [InlineData(
        "Pacific/Kwajalein",
        """[([1993,7,22,12,0,0,0,0]|strflocaltime("%F %T %z %Z")),([1993,7,21,12,0,0,0,0]|strflocaltime("%F %T %z %Z %s"))]""",
        """["1993-08-22 12:00:00 +1200 +12","1993-08-20 12:00:00 -1200 -12 745891200"]""")]
    public async Task GapResultsAndPercentSUpdateTheOffsetGuessExactly(
        string timeZone,
        string filter,
        string expected)
    {
        var actual = ExecuteOne(filter, timeZone).GetRawText();
        if (timeZone is "Europe/Dublin")
        {
            await AssertMatchesInstalledTzifOracleAsync(filter, timeZone, actual, expected);
            return;
        }

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(
        "Pacific/Apia",
        """[2011,11,30,12,0,0,0,0]|strftime("%s")""",
        "1325196000")]
    [InlineData(
        "Pacific/Apia",
        """[([2011,11,28,12,0,0,0,0]|strflocaltime("%F")),([2011,11,30,12,0,0,0,0]|strftime("%s"))]""",
        """["2011-12-28","1325282400"]""")]
    [InlineData(
        "Pacific/Apia",
        """[([2012,0,2,12,0,0,0,0]|strflocaltime("%F")),([2011,11,30,12,0,0,0,0]|strftime("%s"))]""",
        """["2012-01-02","1325196000"]""")]
    [InlineData(
        "Pacific/Kwajalein",
        """[1993,7,21,12,0,0,0,0]|strftime("%s")""",
        "-1")]
    [InlineData(
        "Pacific/Kwajalein",
        """[([1993,7,20,12,0,0,0,0]|strflocaltime("%F")),([1993,7,21,12,0,0,0,0]|strftime("%s"))]""",
        """["1993-08-20","-1"]""")]
    [InlineData(
        "Pacific/Kwajalein",
        """[([1993,7,22,12,0,0,0,0]|strflocaltime("%F")),([1993,7,21,12,0,0,0,0]|strftime("%s"))]""",
        """["1993-08-22","-1"]""")]
    public void ExplicitIsdstMktimeUsesTheSameProbeAndFailureRules(
        string timeZone,
        string filter,
        string expected)
    {
        var actual = ExecuteOne(filter, timeZone);
        Assert.Equal(expected, actual.ValueKind == System.Text.Json.JsonValueKind.String
            ? actual.GetString()
            : actual.GetRawText());
    }

    [Theory]
    [InlineData("Europe/Dublin", "[2024,2,31,0,59,60,0,0]", "2024-03-31 02:00:00|1711846800|+0100|IST")]
    [InlineData("Africa/Casablanca", "[2024,3,14,1,59,60,0,0]", "2024-04-14 03:00:00|1713060000|+0100|+01")]
    [InlineData("Pacific/Apia", "[2011,11,29,23,59,43260,0,0]", "2011-12-31 12:00:00|1325282400|+1400|+14")]
    [InlineData("Pacific/Kwajalein", "[1993,7,20,23,59,43260,0,0]", "1993-08-22 12:00:00|745977600|+1200|+12")]
    public void RawSecondsAreAppliedAfterTheMktimeProbe(
        string timeZone,
        string input,
        string expected)
    {
        var filter = $"{input}|strflocaltime(\"%F %T|%s|%z|%Z\")";
        Assert.Equal(expected, ExecuteOne(filter, timeZone).GetString());
    }

    [Fact]
    public void RawSecondCorrectionDoesNotUpdateThePreCorrectionOffsetGuess()
    {
        const string filter = """[2024,2,31,0,59,60,0,0] as $raw | [2024,9,27,1,30,0,0,0] as $fold | [($raw|strflocaltime("%F %T %z %Z")),($fold|strflocaltime("%s %z %Z")),($raw|strflocaltime("%s %z %Z")),($fold|strflocaltime("%s %z %Z"))]""";
        const string expected = """["2024-03-31 02:00:00 +0100 IST","1729992600 +0000 GMT","1711846800 +0100 IST","1729989000 +0100 IST"]""";

        Assert.Equal(expected, ExecuteOne(filter, "Europe/Dublin").GetRawText());
    }

    [Theory]
    [InlineData("ABC65536", "1970-01-01 00:00:00 +0000 ABC")]
    [InlineData("ABC65537", "1969-12-31 23:00:00 -0100 ABC")]
    [InlineData("ABC1:65536", "1969-12-31 23:00:00 -0100 ABC")]
    [InlineData("ABC1:2:65536", "1969-12-31 22:58:00 -0102 ABC")]
    public void PosixOffsetUnsignedShortNarrowingMatchesPinnedOracle(
        string timeZone,
        string expected)
    {
        Assert.Equal(
            expected,
            ExecuteOne("""0|strflocaltime("%F %T %z %Z")""", timeZone).GetString());
    }

    [Theory]
    [InlineData(
        "ABC0DEF,M3.2.0/65535,M11.1.0/2",
        """0|strflocaltime("%F %T %z %Z")""",
        "1970-01-01 01:00:00 +0100 DEF")]
    [InlineData(
        "ABC0DEF,M01.01.00,M12.5.0",
        """1700000000|strflocaltime("%F %T %z %Z")""",
        "2023-11-14 23:13:20 +0100 DEF")]
    [InlineData(
        "ABC0DEF,M1.1.0/1:60,M12.5.0/2",
        """1700000000|strflocaltime("%F %T %z %Z")""",
        "2023-11-14 23:13:20 +0100 DEF")]
    [InlineData(
        "ABC1:",
        """0|strflocaltime("%F %T|%z|%Z")""",
        "1969-12-31 23:00:00|+0000|")]
    [InlineData(
        "AAA0BBB,M3.2.0",
        """1700000000|strflocaltime("%F %T|%z|%Z")""",
        "2023-11-14 22:13:20|+0000|AAA")]
    [InlineData(
        "AAA0BBB,M3.2.0/2,M3.2.0/3",
        """[0,1710000000,1720000000]|map(strflocaltime("%F %T %z %Z"))""",
        """["1970-01-01 00:00:00 +0000 AAA","2024-03-09 16:00:00 +0000 AAA","2024-07-03 09:46:40 +0000 AAA"]""")]
    [InlineData("AAA0BBB,J0,J365", """0|strflocaltime("%F %T|%s|%z|%Z")""", "1970-01-01 00:00:00|0|+0000|AAA")]
    [InlineData("AAA0BBB,J366,J1", """0|strflocaltime("%F %T|%s|%z|%Z")""", "1970-01-01 00:00:00|0|+0000|AAA")]
    [InlineData("AAA0BBB,M3.0.0,M11.1.0", """0|strflocaltime("%F %T|%s|%z|%Z")""", "1970-01-01 00:00:00|0|+0000|AAA")]
    [InlineData("AAA0BBB,M3.2.0/,M11.1.0", """0|strflocaltime("%F %T|%s|%z|%Z")""", "1970-01-01 00:00:00|0|+0000|AAA")]
    [InlineData("AAA0BBB,M3.2.0/-,M11.1.0", """0|strflocaltime("%F %T|%s|%z|%Z")""", "1970-01-01 00:00:00|0|+0000|AAA")]
    [InlineData("AAA0BBB,M3.2.0/+2,M11.1.0/2", """0|strflocaltime("%F %T|%s|%z|%Z")""", "1970-01-01 00:00:00|0|+0000|AAA")]
    [InlineData("Etc//UTC", """0|strflocaltime("%F %T|%s|%z|%Z")""", "1970-01-01 00:00:00|0|+0000|UTC")]
    [InlineData("Etc/./UTC", """0|strflocaltime("%F %T|%z|%Z")""", "1970-01-01 00:00:00|+0000|UTC")]
    [InlineData("./UTC", """0|strflocaltime("%F %T|%z|%Z")""", "1970-01-01 00:00:00|+0000|UTC")]
    [InlineData("Etc/UTC/", """0|strflocaltime("%F %T|%z|%Z")""", "1970-01-01 00:00:00|+0000|Etc")]
    [InlineData("Etc/UTC/.", """0|strflocaltime("%F %T|%z|%Z")""", "1970-01-01 00:00:00|+0000|Etc")]
    [InlineData("AAA0BBB,M3.2.0/2,M11.1.0/2s", """1730613600|strflocaltime("%F %T|%s|%z|%Z")""", "2024-11-03 06:00:00|1730613600|+0000|AAA")]
    [InlineData("AAA0BBB,M3.2.0,", """1730613600|strflocaltime("%F %T|%s|%z|%Z")""", "2024-11-03 06:00:00|1730613600|+0000|AAA")]
    public void PosixPartialRulesAndEqualTransitionsMatchPinnedOracle(
        string timeZone,
        string filter,
        string expected)
    {
        var actual = ExecuteOne(filter, timeZone);
        Assert.Equal(expected, actual.ValueKind == System.Text.Json.JsonValueKind.String
            ? actual.GetString()
            : actual.GetRawText());
    }

    [Theory]
    [InlineData(
        "AAA-14BBB,J1/0,J2/0",
        """1704024000|strflocaltime("%F %T %z %Z")""",
        "2024-01-01 02:00:00 +1400 AAA")]
    [InlineData(
        "Europe/Paris",
        "67768036183814392|localtime",
        "[-2147481749,9,2,0,59,52,4,274]")]
    [InlineData(
        "AAA-24",
        "-67768040609740808|try localtime catch .",
        "error converting number of seconds since epoch to datetime")]
    [InlineData(
        "AAA24",
        "67768036191676800|try localtime catch .",
        "error converting number of seconds since epoch to datetime")]
    [InlineData(
        "QAA0QBB,M1.1.1/0,M1.1.4/1",
        """-67768040609740800|try strflocaltime("%Y-%m-%d %T|%z|%Z") catch .""",
        "-2147481748-01-01 00:00:00|+0000|QAA")]
    [InlineData(
        "QAA0QBB,M1.1.0/0,M1.1.3/1",
        "67768036191676000|try localtime catch .",
        "[-2147481749,11,31,23,46,40,3,364]")]
    [InlineData(
        "QAA0QBB,M3.2.0/2,M11.1.0/2",
        """315507368505600|strflocaltime("%Y-%m-%d %T|%z|%Z")""",
        "10000000-07-01 00:00:00|+0000|QAA")]
    [InlineData(
        "QAA0QBB,J60/1,J300/3",
        """315507368505600|strflocaltime("%Y-%m-%d %T|%z|%Z")""",
        "10000000-07-01 00:00:00|+0000|QAA")]
    public void PosixUtcYearSelectionAndRepresentabilityMatchPinnedOracle(
        string timeZone,
        string filter,
        string expected)
    {
        var actual = ExecuteOne(filter, timeZone);
        Assert.Equal(expected, actual.ValueKind == System.Text.Json.JsonValueKind.String
            ? actual.GetString()
            : actual.GetRawText());
    }

    [Fact]
    public void WindowsSystemIdentifiersAreResolvedBeforePermissivePosixParsing()
    {
        TimeZoneInfo system;
        try
        {
            system = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        }
        catch (Exception exception) when (
            exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // The assertion runs on supported Windows configurations and on
            // Unix installations that include CLDR Windows-ID mappings.
            return;
        }

        var zone = JqTimeZone.Create(
            "Pacific Standard Time",
            preferSystemTimeZoneIdentifiers: true);
        var instant = DateTimeOffset.FromUnixTimeSeconds(0);
        var expectedOffset = checked((int)system.GetUtcOffset(instant).TotalSeconds);

        Assert.Equal(expectedOffset, zone.GetPeriod(0).OffsetSeconds);
        Assert.NotEqual(0, zone.GetPeriod(0).OffsetSeconds);
    }

    [Fact]
    public void IndependentManagedStatesIsolateTheLibcMktimeOffsetGuess()
    {
        var options = OptionsFor("Europe/Paris");
        using var daylightSeeded = JqProgram.Compile(
            """[([2024,6,1,12,0,0,0,0]|strflocaltime("%s %z %Z")),([2024,9,27,2,30,0,0,0]|strflocaltime("%s %z %Z"))]""");
        using var fresh = JqProgram.Compile(
            """[2024,9,27,2,30,0,0,0]|strflocaltime("%s %z %Z")""");

        Assert.Equal(
            """["1719828000 +0200 CEST","1729989000 +0200 CEST"]""",
            Assert.Single(daylightSeeded.Execute("null", options)).GetRawText());
        Assert.Equal(
            "1729992600 +0100 CET",
            Assert.Single(fresh.Execute("null", options)).GetString());
    }

    [Fact]
    public void UnavailableGlibcLocaleDoesNotUseAnIcuOnlyCulture()
    {
        var options = new JqExecutionOptions
        {
            Environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["LC_ALL"] = "fr_FR.UTF-8",
                ["TZ"] = "UTC",
            },
        };

        var output = Assert.Single(
            JqProgram
                .Compile("""1731627341|strflocaltime("%a %d %b %Y")""")
                .Execute("null", options));

        Assert.Equal("Thu 14 Nov 2024", output.GetString());
    }

    [Fact]
    public void NowUsesCurrentUnixTimeWithGettimeofdayResolution()
    {
        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1_000d - 1;
        var value = JqTimeBuiltinProxy.Now();
        try
        {
            var after = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1_000d + 1;
            Assert.Equal(jv_kind.JV_KIND_NUMBER, value.Kind);
            Assert.InRange(value.NumberValue, before, after);
            Assert.InRange(
                Math.Abs(value.NumberValue * 1_000_000d -
                    Math.Round(value.NumberValue * 1_000_000d)),
                0,
                0.25);
        }
        finally
        {
            libjq.jv_free(value);
        }
    }

    private static System.Text.Json.JsonElement ExecuteOne(
        string filter,
        string timeZone = "UTC")
    {
        return Assert.Single(JqProgram.Compile(filter).Execute("null", OptionsFor(timeZone)));
    }

    private static JqExecutionOptions OptionsFor(string timeZone) =>
        new()
        {
            Environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["LC_ALL"] = "C",
                ["LC_TIME"] = "C",
                ["LANG"] = "C",
                ["TZ"] = timeZone,
            },
        };

    private static async Task AssertMatchesInstalledTzifOracleAsync(
        string filter,
        string timeZone,
        string actual,
        string frozenExpected)
    {
        if (!JqOracle.TryResolveExecutable(out _))
        {
            Assert.Equal(frozenExpected, actual);
            return;
        }

        var oracle = await JqOracle.ExecuteAsync(
            filter,
            "null",
            cancellationToken: TestContext.Current.CancellationToken,
            environment: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["TZ"] = timeZone,
            });

        Assert.True(oracle.Succeeded, oracle.StandardError);
        Assert.Empty(oracle.StandardError);
        Assert.Equal(Assert.Single(oracle.OutputLines), actual);
    }
}
