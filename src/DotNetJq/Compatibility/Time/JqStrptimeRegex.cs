// DOTNETJQ PROXY
// UPSTREAM REPOSITORY: https://github.com/jqlang/jq
// UPSTREAM TAG: jq-1.8.2
// UPSTREAM COMMIT: 34f7186b86743a083a589741b6cea95293524108
// UPSTREAM SOURCES: src/builtin.c:f_strptime and glibc-2.39/time/strptime_l.c
// STRATEGY: PROXY
// UPSTREAM COMPONENT: jq f_strptime and the pinned glibc strptime field scanner.
// REPLACEMENT: An AOT-safe managed scanner ports the C-locale directive/state machine.
// WHY: .NET has no POSIX strptime API and DateTime.ParseExact cannot expose jq's tail contract.
// BEHAVIORAL CONTRACT: Preserve glibc directive, modifier, range, whitespace, sentinel-field,
// tail, and normalization behavior observable through jq's eight-element time arrays.
// KNOWN DIFFERENCES: Non-C locale era and alternate-digit tables come from .NET globalization;
// their byte-level collation and installed-locale availability are platform data, not glibc ABI.
// TESTS COVERING THE SUBSTITUTION: StrptimeRegexProxyTests and TimeBuiltinOracleMatrixTests.
//
// jq delegates the format grammar to the platform strptime(3).  .NET has no
// equivalent API, so this file ports the C-locale grammar and struct-tm state
// transitions used by the pinned Linux/glibc oracle.  It deliberately scans
// ASCII digits and C whitespace rather than using Regex \d/\s: both of those
// .NET character classes accept input that glibc rejects.  Locale names are
// supplied by the caller's isolated CultureInfo context and are tried alongside
// the invariant names, like glibc's locale/raw fallback.

using System.Globalization;

namespace DotNetJq.Compatibility.Time;

/// <summary>The managed equivalent of the jq-visible members of <c>struct tm</c>.</summary>
internal readonly record struct JqParsedTm(
    int YearSince1900,
    int Month,
    int Day,
    int Hour,
    int Minute,
    int Second,
    int WeekDay,
    int YearDay,
    int IsDaylightSaving,
    int UtcOffsetSeconds,
    string ZoneAbbreviation)
{
    internal int JqYear => unchecked(YearSince1900 + 1900);
}

/// <summary>The scanner result consumed by the jq-shaped time proxy.</summary>
internal sealed record JqStrptimeMatch
{
    internal bool Success { get; init; }

    internal JqParsedTm ParsedTime { get; init; }

    // These compatibility projections keep focused proxy tests readable.
    internal double? EpochSeconds { get; init; }
    internal int? Year { get; init; }
    internal int? TwoDigitYear { get; init; }
    internal int? Century { get; init; }
    internal int? Month { get; init; }
    internal string? MonthName { get; init; }
    internal int? Day { get; init; }
    internal int? Hour24 { get; init; }
    internal int? Hour12 { get; init; }
    internal string? AmPm { get; init; }
    internal int? Minute { get; init; }
    internal int? Second { get; init; }
    internal int? YearDay { get; init; }
    internal string? Tail { get; init; }
}

/// <summary>
/// jq-shaped <c>strptime</c> field scanner.  The historical class name is
/// retained so mapped callers and audit evidence remain stable; the
/// implementation is now a direct scanner rather than a regular expression.
/// </summary>
internal static class JqStrptimeRegex
{
    private static readonly string[] InvariantWeekdays =
        ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
    private static readonly string[] InvariantAbbreviatedWeekdays =
        ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
    private static readonly string[] InvariantMonths =
    [
        "January", "February", "March", "April", "May", "June",
        "July", "August", "September", "October", "November", "December",
    ];
    private static readonly string[] InvariantAbbreviatedMonths =
        ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    private static readonly int[][] MonthYearDays =
    [
        [0, 31, 59, 90, 120, 151, 181, 212, 243, 273, 304, 334, 365],
        [0, 31, 60, 91, 121, 152, 182, 213, 244, 274, 305, 335, 366],
    ];

    internal static JqStrptimeMatch Match(string input, string format) =>
        Match(input, format, CultureInfo.InvariantCulture, static seconds => UtcFromEpoch(seconds));

    internal static JqStrptimeMatch Match(
        string input,
        string format,
        CultureInfo culture,
        Func<long, JqParsedTm?> localtime)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(culture);
        ArgumentNullException.ThrowIfNull(localtime);

        var scanner = new Scanner(input, culture, localtime);
        if (!scanner.Scan(format) ||
            (scanner.InputIndex != input.Length && !IsCWhitespace(input[scanner.InputIndex])))
        {
            return new JqStrptimeMatch();
        }

        scanner.FinalizeFields();
        var parsed = scanner.ToParsedTime();
        return new JqStrptimeMatch
        {
            Success = true,
            ParsedTime = parsed,
            Year = parsed.JqYear,
            TwoDigitYear = scanner.TwoDigitYear,
            Century = scanner.ParsedCentury,
            Month = parsed.Month + 1,
            MonthName = scanner.ParsedMonthName,
            Day = parsed.Day,
            Hour24 = parsed.Hour,
            Hour12 = scanner.ParsedHour12,
            AmPm = scanner.ParsedAmPm,
            Minute = parsed.Minute,
            Second = parsed.Second,
            YearDay = parsed.YearDay + 1,
            Tail = scanner.InputIndex == input.Length ? null : input[scanner.InputIndex..],
        };
    }

    private sealed class Scanner
    {
        private readonly string input;
        private readonly CultureInfo culture;
        private readonly Func<long, JqParsedTm?> localtime;

        private bool haveI;
        private bool haveWeekDay;
        private bool haveYearDay;
        private bool haveMonth;
        private bool haveDay;
        private bool haveSundayWeek;
        private bool haveMondayWeek;
        private bool isPm;
        private bool wantCentury;
        private bool wantYearDay;
        private int weekNumber;
        private int century = -1;

        internal Scanner(
            string input,
            CultureInfo culture,
            Func<long, JqParsedTm?> localtime)
        {
            this.input = input;
            this.culture = culture;
            this.localtime = localtime;
            WeekDay = 8;
            YearDay = 367;
        }

        internal int InputIndex { get; private set; }
        internal int YearSince1900 { get; private set; }
        internal int Month { get; private set; }
        internal int Day { get; private set; }
        internal int Hour { get; private set; }
        internal int Minute { get; private set; }
        internal int Second { get; private set; }
        internal int WeekDay { get; private set; }
        internal int YearDay { get; private set; }
        internal int IsDaylightSaving { get; private set; }
        internal int UtcOffsetSeconds { get; private set; }
        internal string ZoneAbbreviation { get; private set; } = string.Empty;

        internal int? TwoDigitYear { get; private set; }
        internal int? ParsedCentury => century < 0 ? null : century;
        internal string? ParsedMonthName { get; private set; }
        internal int? ParsedHour12 { get; private set; }
        internal string? ParsedAmPm { get; private set; }

        internal bool Scan(string format)
        {
            for (var formatIndex = 0; formatIndex < format.Length; formatIndex++)
            {
                var current = format[formatIndex];
                if (IsCWhitespace(current))
                {
                    SkipInputWhitespace();
                    continue;
                }

                if (current != '%')
                {
                    if (!MatchCharacter(current))
                    {
                        return false;
                    }

                    continue;
                }

                formatIndex++;
                while (formatIndex < format.Length &&
                       format[formatIndex] is '-' or '_' or '0' or '^' or '#')
                {
                    formatIndex++;
                }

                while (formatIndex < format.Length && IsAsciiDigit(format[formatIndex]))
                {
                    formatIndex++;
                }

                if (formatIndex >= format.Length)
                {
                    return false;
                }

                var modifier = '\0';
                var directive = format[formatIndex];
                if (directive == 'E')
                {
                    modifier = 'E';
                    if (++formatIndex >= format.Length ||
                        format[formatIndex] is not ('c' or 'C' or 'Y' or 'x' or 'X'))
                    {
                        return false;
                    }

                    directive = format[formatIndex];
                }
                else if (directive == 'O')
                {
                    modifier = 'O';
                    if (++formatIndex >= format.Length ||
                        format[formatIndex] is not (
                            'b' or 'B' or 'h' or 'd' or 'e' or 'H' or 'I' or 'm' or
                            'M' or 'S' or 'U' or 'W' or 'V' or 'w' or 'y'))
                    {
                        return false;
                    }

                    directive = format[formatIndex];
                }

                if (!ScanDirective(directive, modifier))
                {
                    return false;
                }
            }

            return true;
        }

        private bool ScanDirective(char directive, char modifier)
        {
            switch (directive)
            {
                case '%':
                    return MatchCharacter('%');
                case 'a' or 'A':
                    return MatchWeekday();
                case 'b' or 'B' or 'h':
                    return MatchMonth();
                case 'c':
                    return Scan(CompositeDateTimeFormat());
                case 'C':
                    if (!TryReadNumber(0, 99, 2, out century))
                    {
                        return false;
                    }

                    wantYearDay = true;
                    return true;
                case 'd' or 'e':
                    if (!TryReadNumber(1, 31, 2, out var day))
                    {
                        return false;
                    }

                    Day = day;
                    haveDay = true;
                    wantYearDay = true;
                    return true;
                case 'F':
                    if (!Scan("%Y-%m-%d"))
                    {
                        return false;
                    }

                    wantYearDay = true;
                    return true;
                case 'x' or 'D':
                    if (!Scan(ShortDateFormat()))
                    {
                        return false;
                    }

                    wantYearDay = true;
                    return true;
                case 'k' or 'H':
                    if (!TryReadNumber(0, 23, 2, out var hour24))
                    {
                        return false;
                    }

                    Hour = hour24;
                    haveI = false;
                    return true;
                case 'l' or 'I':
                    if (!TryReadNumber(1, 12, 2, out var hour12))
                    {
                        return false;
                    }

                    ParsedHour12 = hour12;
                    Hour = hour12 % 12;
                    haveI = true;
                    return true;
                case 'j':
                    if (!TryReadNumber(1, 366, 3, out var yearDay))
                    {
                        return false;
                    }

                    YearDay = yearDay - 1;
                    haveYearDay = true;
                    return true;
                case 'm':
                    if (!TryReadNumber(1, 12, 2, out var month))
                    {
                        return false;
                    }

                    Month = month - 1;
                    haveMonth = true;
                    wantYearDay = true;
                    return true;
                case 'M':
                    if (!TryReadNumber(0, 59, 2, out var minute))
                    {
                        return false;
                    }

                    Minute = minute;
                    return true;
                case 'n' or 't':
                    SkipInputWhitespace();
                    return true;
                case 'p':
                    return MatchAmPm();
                case 'r':
                    return Scan(AmPmTimeFormat());
                case 'R':
                    return Scan("%H:%M");
                case 's':
                    return MatchEpoch();
                case 'S':
                    if (!TryReadNumber(0, 61, 2, out var second))
                    {
                        return false;
                    }

                    Second = second;
                    return true;
                case 'X':
                    return Scan(LongTimeFormat());
                case 'T':
                    return Scan("%H:%M:%S");
                case 'u':
                    if (!TryReadNumber(1, 7, 1, out var isoWeekday))
                    {
                        return false;
                    }

                    WeekDay = isoWeekday % 7;
                    haveWeekDay = true;
                    return true;
                case 'g':
                    return TryReadNumber(0, 99, 2, out _);
                case 'G':
                    return ReadUnboundedUnsignedNumber();
                case 'U':
                    if (!TryReadNumber(0, 53, 2, out weekNumber))
                    {
                        return false;
                    }

                    haveSundayWeek = true;
                    return true;
                case 'W':
                    if (!TryReadNumber(0, 53, 2, out weekNumber))
                    {
                        return false;
                    }

                    haveMondayWeek = true;
                    return true;
                case 'V':
                    return TryReadNumber(0, 53, 2, out _);
                case 'w':
                    if (!TryReadNumber(0, 6, 1, out var weekday))
                    {
                        return false;
                    }

                    WeekDay = weekday;
                    haveWeekDay = true;
                    return true;
                case 'y':
                    if (!TryReadNumber(0, 99, 2, out var yearInCentury))
                    {
                        return false;
                    }

                    TwoDigitYear = yearInCentury;
                    YearSince1900 = yearInCentury >= 69 ? yearInCentury : yearInCentury + 100;
                    // glibc's alternate-number %Oy path parses the same value
                    // but deliberately does not combine it with a preceding
                    // century field.
                    wantCentury = modifier != 'O';
                    wantYearDay = true;
                    return true;
                case 'Y':
                    if (!TryReadNumber(0, 9_999, 4, out var year))
                    {
                        return false;
                    }

                    YearSince1900 = year - 1900;
                    wantCentury = false;
                    wantYearDay = true;
                    return true;
                case 'Z':
                    SkipInputWhitespace();
                    var start = InputIndex;
                    while (InputIndex < input.Length && !IsCWhitespace(input[InputIndex]))
                    {
                        InputIndex++;
                    }

                    ZoneAbbreviation = input[start..InputIndex];
                    return true;
                case 'z':
                    return MatchUtcOffset();
                default:
                    return false;
            }
        }

        private bool MatchWeekday()
        {
            var bestLength = -1;
            var bestDay = -1;
            for (var day = 0; day < 7; day++)
            {
                TryName(InvariantWeekdays[day], day, ref bestLength, ref bestDay);
                TryName(InvariantAbbreviatedWeekdays[day], day, ref bestLength, ref bestDay);
                TryName(culture.DateTimeFormat.GetDayName((DayOfWeek)day), day, ref bestLength, ref bestDay);
                TryName(culture.DateTimeFormat.GetAbbreviatedDayName((DayOfWeek)day), day, ref bestLength, ref bestDay);
            }

            if (bestLength < 0)
            {
                return false;
            }

            InputIndex += bestLength;
            WeekDay = bestDay;
            haveWeekDay = true;
            return true;
        }

        private bool MatchMonth()
        {
            var bestLength = -1;
            var bestMonth = -1;
            string? bestName = null;
            for (var month = 0; month < 12; month++)
            {
                TryMonthName(InvariantMonths[month], month, ref bestLength, ref bestMonth, ref bestName);
                TryMonthName(InvariantAbbreviatedMonths[month], month, ref bestLength, ref bestMonth, ref bestName);
                TryMonthName(culture.DateTimeFormat.GetMonthName(month + 1), month, ref bestLength, ref bestMonth, ref bestName);
                TryMonthName(culture.DateTimeFormat.GetAbbreviatedMonthName(month + 1), month, ref bestLength, ref bestMonth, ref bestName);
            }

            if (bestLength < 0)
            {
                return false;
            }

            InputIndex += bestLength;
            Month = bestMonth;
            ParsedMonthName = bestName;
            haveMonth = true;
            wantYearDay = true;
            return true;
        }

        private void TryName(
            string candidate,
            int value,
            ref int bestLength,
            ref int bestValue)
        {
            if (candidate.Length <= bestLength || !MatchesName(candidate))
            {
                return;
            }

            bestLength = candidate.Length;
            bestValue = value;
        }

        private void TryMonthName(
            string candidate,
            int value,
            ref int bestLength,
            ref int bestValue,
            ref string? bestName)
        {
            if (candidate.Length <= bestLength || !MatchesName(candidate))
            {
                return;
            }

            bestLength = candidate.Length;
            bestValue = value;
            bestName = input.Substring(InputIndex, candidate.Length);
        }

        private bool MatchesName(string candidate)
        {
            if (candidate.Length == 0 || InputIndex + candidate.Length > input.Length)
            {
                return false;
            }

            return culture.CompareInfo.Compare(
                input,
                InputIndex,
                candidate.Length,
                candidate,
                0,
                candidate.Length,
                CompareOptions.IgnoreCase) == 0;
        }

        private bool MatchAmPm()
        {
            var am = culture.DateTimeFormat.AMDesignator;
            var pm = culture.DateTimeFormat.PMDesignator;
            if (TryConsumeName(am) || TryConsumeName("AM"))
            {
                isPm = false;
                ParsedAmPm = "AM";
                return true;
            }

            if (TryConsumeName(pm) || TryConsumeName("PM"))
            {
                isPm = true;
                ParsedAmPm = "PM";
                return true;
            }

            return false;
        }

        private bool TryConsumeName(string value)
        {
            if (!MatchesName(value))
            {
                return false;
            }

            InputIndex += value.Length;
            return true;
        }

        private bool MatchEpoch()
        {
            if (InputIndex >= input.Length || !IsAsciiDigit(input[InputIndex]))
            {
                return false;
            }

            long seconds = 0;
            do
            {
                seconds = unchecked((seconds * 10) + input[InputIndex++] - '0');
            }
            while (InputIndex < input.Length && IsAsciiDigit(input[InputIndex]));

            var parsed = localtime(seconds);
            if (parsed is null)
            {
                return false;
            }

            var time = parsed.Value;
            YearSince1900 = time.YearSince1900;
            Month = time.Month;
            Day = time.Day;
            Hour = time.Hour;
            Minute = time.Minute;
            Second = time.Second;
            WeekDay = time.WeekDay;
            YearDay = time.YearDay;
            IsDaylightSaving = time.IsDaylightSaving;
            UtcOffsetSeconds = time.UtcOffsetSeconds;
            ZoneAbbreviation = time.ZoneAbbreviation;
            return true;
        }

        private bool MatchUtcOffset()
        {
            SkipInputWhitespace();
            if (InputIndex < input.Length && input[InputIndex] == 'Z')
            {
                InputIndex++;
                UtcOffsetSeconds = 0;
                return true;
            }

            if (InputIndex >= input.Length || input[InputIndex] is not ('+' or '-'))
            {
                return false;
            }

            var negative = input[InputIndex++] == '-';
            var value = 0;
            var digits = 0;
            while (digits < 4 && InputIndex < input.Length && IsAsciiDigit(input[InputIndex]))
            {
                value = (value * 10) + input[InputIndex++] - '0';
                digits++;
                if (InputIndex < input.Length && input[InputIndex] == ':' && digits == 2 &&
                    InputIndex + 1 < input.Length && IsAsciiDigit(input[InputIndex + 1]))
                {
                    InputIndex++;
                }
            }

            if (digits == 2)
            {
                value *= 100;
            }
            else if (digits != 4 || value % 100 >= 60)
            {
                return false;
            }

            UtcOffsetSeconds = ((value / 100) * 3_600) + ((value % 100) * 60);
            if (negative)
            {
                UtcOffsetSeconds = -UtcOffsetSeconds;
            }

            return true;
        }

        private bool TryReadNumber(int from, int to, int maximumDigits, out int value)
        {
            SkipInputWhitespace();
            value = 0;
            if (InputIndex >= input.Length || !IsAsciiDigit(input[InputIndex]))
            {
                return false;
            }

            var remaining = maximumDigits;
            do
            {
                value = (value * 10) + input[InputIndex++] - '0';
            }
            while (--remaining > 0 && value * 10 <= to && InputIndex < input.Length &&
                   IsAsciiDigit(input[InputIndex]));

            return value >= from && value <= to;
        }

        private bool ReadUnboundedUnsignedNumber()
        {
            if (InputIndex >= input.Length || !IsAsciiDigit(input[InputIndex]))
            {
                return false;
            }

            do
            {
                InputIndex++;
            }
            while (InputIndex < input.Length && IsAsciiDigit(input[InputIndex]));

            return true;
        }

        private bool MatchCharacter(char expected)
        {
            if (InputIndex >= input.Length || input[InputIndex] != expected)
            {
                return false;
            }

            InputIndex++;
            return true;
        }

        private void SkipInputWhitespace()
        {
            while (InputIndex < input.Length && IsCWhitespace(input[InputIndex]))
            {
                InputIndex++;
            }
        }

        internal void FinalizeFields()
        {
            if (haveI && isPm)
            {
                Hour += 12;
            }

            if (century != -1)
            {
                YearSince1900 = wantCentury
                    ? (YearSince1900 % 100) + ((century - 19) * 100)
                    : (century - 19) * 100;
            }

            if (wantYearDay && !haveWeekDay)
            {
                if (!(haveMonth && haveDay) && haveYearDay)
                {
                    DeriveMonthAndDayFromYearDay();
                }

                if (haveMonth || (uint)Month <= 11)
                {
                    SetWeekDay();
                }
            }

            if (wantYearDay && !haveYearDay && (haveMonth || (uint)Month <= 11))
            {
                SetYearDay();
            }

            if ((haveSundayWeek || haveMondayWeek) && haveWeekDay)
            {
                var savedWeekDay = WeekDay;
                var savedDay = Day;
                var savedMonth = Month;
                var weekOffset = haveSundayWeek ? 0 : 1;

                Day = 1;
                Month = 0;
                SetWeekDay();
                if (haveDay)
                {
                    Day = savedDay;
                }

                if (haveMonth)
                {
                    Month = savedMonth;
                }

                if (!haveYearDay)
                {
                    YearDay = PositiveMod(7 - (WeekDay - weekOffset), 7) +
                        ((weekNumber - 1) * 7) +
                        PositiveMod(savedWeekDay - weekOffset + 7, 7);
                }

                if (!haveDay || !haveMonth)
                {
                    DeriveMonthAndDayFromYearDay();
                }

                WeekDay = savedWeekDay;
            }
        }

        private void DeriveMonthAndDayFromYearDay()
        {
            var monthDays = MonthYearDays[IsLeapYear(unchecked(YearSince1900 + 1900)) ? 1 : 0];
            var month = 0;
            while (month < 12 && monthDays[month] <= YearDay)
            {
                month++;
            }

            if (!haveMonth)
            {
                Month = month - 1;
            }

            if (!haveDay)
            {
                // glibc's week-zero path indexes one element before the table.
                // In the pinned 2.39 object that adjacent value is 365.  Keep
                // the observed jq result deterministic instead of reproducing
                // C undefined memory access.
                var previousBoundary = month == 0 ? 365 : monthDays[month - 1];
                Day = YearDay - previousBoundary + 1;
            }

            haveMonth = true;
            haveDay = true;
        }

        private void SetWeekDay()
        {
            var year = unchecked(YearSince1900 + 1900);
            var correctedYear = year - (Month < 2 ? 1 : 0);
            var correctedQuarter = correctedYear / 4;
            var weekDay = -473 +
                (365 * (YearSince1900 - 70)) +
                correctedQuarter -
                (correctedQuarter / 25) +
                ((correctedQuarter % 25) < 0 ? 1 : 0) +
                ((correctedQuarter / 25) / 4) +
                MonthYearDays[0][Month] +
                Day - 1;
            WeekDay = PositiveMod(weekDay, 7);
        }

        private void SetYearDay()
        {
            var year = unchecked(YearSince1900 + 1900);
            YearDay = MonthYearDays[IsLeapYear(year) ? 1 : 0][Month] + Day - 1;
        }

        internal JqParsedTm ToParsedTime() =>
            new(
                YearSince1900,
                Month,
                Day,
                Hour,
                Minute,
                Second,
                WeekDay,
                YearDay,
                IsDaylightSaving,
                UtcOffsetSeconds,
                ZoneAbbreviation);

        private string CompositeDateTimeFormat() =>
            IsInvariantCulture
                ? "%a %b %e %H:%M:%S %Y"
                : ConvertDateTimePattern(culture.DateTimeFormat.FullDateTimePattern);

        private string ShortDateFormat() =>
            IsInvariantCulture
                ? "%m/%d/%y"
                : ConvertDateTimePattern(culture.DateTimeFormat.ShortDatePattern);

        private string LongTimeFormat() =>
            IsInvariantCulture
                ? "%H:%M:%S"
                : ConvertDateTimePattern(culture.DateTimeFormat.LongTimePattern);

        private string AmPmTimeFormat() =>
            IsInvariantCulture
                ? "%I:%M:%S %p"
                : ConvertDateTimePattern(culture.DateTimeFormat.LongTimePattern);

        private bool IsInvariantCulture =>
            culture.Equals(CultureInfo.InvariantCulture) || culture.Name.Length == 0;
    }

    private static string ConvertDateTimePattern(string pattern)
    {
        var result = new System.Text.StringBuilder(pattern.Length + 8);
        for (var index = 0; index < pattern.Length;)
        {
            var current = pattern[index];
            if (current is '\'' or '"')
            {
                var quote = current;
                index++;
                while (index < pattern.Length && pattern[index] != quote)
                {
                    if (pattern[index] == '\\' && index + 1 < pattern.Length)
                    {
                        index++;
                    }

                    result.Append(pattern[index++]);
                }

                if (index < pattern.Length)
                {
                    index++;
                }

                continue;
            }

            if (current == '\\' && index + 1 < pattern.Length)
            {
                result.Append(pattern[index + 1]);
                index += 2;
                continue;
            }

            var end = index + 1;
            while (end < pattern.Length && pattern[end] == current)
            {
                end++;
            }

            var count = end - index;
            result.Append(current switch
            {
                'y' => count <= 2 ? "%y" : "%Y",
                'M' => count switch { >= 4 => "%B", 3 => "%b", _ => "%m" },
                'd' => count >= 3 ? (count == 3 ? "%a" : "%A") : "%d",
                'H' => "%H",
                'h' => "%I",
                'm' => "%M",
                's' => "%S",
                't' => "%p",
                _ => new string(current, count),
            });
            index = end;
        }

        return result.ToString();
    }

    private static JqParsedTm? UtcFromEpoch(long seconds)
    {
        try
        {
            var days = FloorDiv(seconds, 86_400);
            var secondOfDay = checked((int)(seconds - (days * 86_400)));
            var civil = CivilFromDays(days);
            var yearSince1900 = checked((int)(civil.Year - 1900));
            var weekDay = checked((int)FloorMod(days + 4, 7));
            var yearDay = checked((int)(days - DaysFromCivil(civil.Year, 1, 1)));
            return new JqParsedTm(
                yearSince1900,
                civil.Month - 1,
                civil.Day,
                secondOfDay / 3_600,
                (secondOfDay % 3_600) / 60,
                secondOfDay % 60,
                weekDay,
                yearDay,
                0,
                0,
                "GMT");
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static bool IsAsciiDigit(char value) => value is >= '0' and <= '9';

    internal static bool IsCWhitespace(char value) =>
        value is ' ' or '\t' or '\n' or '\v' or '\f' or '\r';

    private static bool IsLeapYear(int year) =>
        year % 4 == 0 && (year % 100 != 0 || year % 400 == 0);

    private static int PositiveMod(int value, int divisor)
    {
        var result = value % divisor;
        return result < 0 ? result + divisor : result;
    }

    private static long DaysFromCivil(long year, int month, int day)
    {
        year -= month <= 2 ? 1 : 0;
        var era = FloorDiv(year, 400);
        var yearOfEra = year - (era * 400);
        var monthPrime = month + (month > 2 ? -3 : 9);
        var dayOfYear = ((153L * monthPrime) + 2) / 5 + day - 1;
        var dayOfEra = (yearOfEra * 365) + (yearOfEra / 4) - (yearOfEra / 100) + dayOfYear;
        return checked((era * 146_097) + dayOfEra - 719_468);
    }

    private static CivilDate CivilFromDays(long days)
    {
        var shifted = checked(days + 719_468);
        var era = FloorDiv(shifted, 146_097);
        var dayOfEra = shifted - (era * 146_097);
        var yearOfEra = (dayOfEra - (dayOfEra / 1_460) + (dayOfEra / 36_524) -
            (dayOfEra / 146_096)) / 365;
        var year = yearOfEra + (era * 400);
        var dayOfYear = dayOfEra - ((365 * yearOfEra) + (yearOfEra / 4) -
            (yearOfEra / 100));
        var monthPrime = ((5 * dayOfYear) + 2) / 153;
        var day = checked((int)(dayOfYear - (((153 * monthPrime) + 2) / 5) + 1));
        var month = checked((int)(monthPrime + (monthPrime < 10 ? 3 : -9)));
        year += month <= 2 ? 1 : 0;
        return new CivilDate(year, month, day);
    }

    private static long FloorDiv(long value, long divisor)
    {
        var quotient = value / divisor;
        return value % divisor < 0 ? quotient - 1 : quotient;
    }

    private static long FloorMod(long value, long divisor) =>
        value - (FloorDiv(value, divisor) * divisor);

    private readonly record struct CivilDate(long Year, int Month, int Day);
}
