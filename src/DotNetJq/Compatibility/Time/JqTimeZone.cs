// DOTNETJQ PROXY
// UPSTREAM REPOSITORY: https://github.com/jqlang/jq
// UPSTREAM TAG: jq-1.8.2
// UPSTREAM COMMIT: 34f7186b86743a083a589741b6cea95293524108
// UPSTREAM SOURCES: src/builtin.c:jv2tm/f_localtime/f_strflocaltime,
//                   glibc-2.39/time/tzfile.c, time/tzset.c, time/mktime.c,
//                   time/mktime-internal.h, time/strftime_l.c, and time/timegm.c
// STRATEGY: PROXY
// UPSTREAM COMPONENT: jq localtime/strflocaltime and glibc TZif/POSIX-TZ/mktime selection.
// REPLACEMENT: A managed TZif v1-v4 reader, POSIX footer/rule evaluator, and mktime probe loop.
// WHY: TimeZoneInfo hides TZif abbreviations and its DateTime rule surface ends at year 9999.
// BEHAVIORAL CONTRACT: Preserve offset, DST fold/gap, abbreviation, invalid-TZ, and far-epoch
// behavior for ordinary IANA TZif files without mutating process-global TZ state.
// KNOWN DIFFERENCES: Leap-second right/* files, malformed M-month rules that make glibc read
// out of bounds, absolute TZ file paths, Windows-only zones without TZif data, and explicit
// per-execution environment isolation use the documented managed contract.
// TESTS COVERING THE SUBSTITUTION: TimeBuiltinOracleMatrixTests and CliFixtureLibrarySemanticsTests.
//
// TimeZoneInfo does not expose the abbreviation selected by a TZif transition,
// and its DateTime-based rules stop at years 1..9999.  This AOT-safe reader
// consumes the public TZif format and its POSIX footer so jq's %z/%Z and far
// epoch behavior do not depend on private CoreLib reflection.  Windows/system
// zones without a TZif payload use the public TimeZoneInfo contract and expose
// its public standard/daylight names truthfully.

using System.Buffers.Binary;
using System.Text;

namespace DotNetJq.Compatibility.Time;

internal readonly record struct JqTimeZonePeriod(
    int OffsetSeconds,
    bool IsDaylightSaving,
    string Abbreviation);

internal readonly record struct JqNormalizedLocalTime(
    long UnixSeconds,
    long LocalSeconds,
    JqTimeZonePeriod Period);

/// <summary>
/// The bounded counterpart of libc's current TZ object and mktime offset guess.
/// Default execution shares one externally synchronized process-static instance;
/// an explicit environment uses one non-thread-safe instance owned by its jq_state.
/// </summary>
internal sealed class JqTimeZoneContext
{
    private string? identifier;
    private JqTimeZone? zone;

    internal int MktimeOffsetGuessSeconds { get; set; }

    internal JqTimeZone Resolve(string requestedIdentifier)
    {
        if (zone is null || !string.Equals(identifier, requestedIdentifier, StringComparison.Ordinal))
        {
            identifier = requestedIdentifier;
            zone = JqTimeZone.Create(requestedIdentifier);
        }

        return zone;
    }
}

internal sealed class JqTimeZone
{
    private static readonly string[] ZoneInfoRoots =
        ["/usr/share/zoneinfo", "/usr/share/lib/zoneinfo", "/system/usr/share/zoneinfo"];

    private readonly long[] transitions;
    private readonly byte[] transitionTypes;
    private readonly JqTimeZonePeriod[] types;
    private readonly bool[] typeIsStandard;
    private readonly bool[] typeIsUtc;
    private readonly int defaultType;
    private readonly PosixZone? future;
    private readonly TimeZoneInfo? systemZone;
    private readonly bool directPosix;

    private JqTimeZone(
        long[] transitions,
        byte[] transitionTypes,
        JqTimeZonePeriod[] types,
        int defaultType,
        PosixZone? future,
        bool[]? typeIsStandard = null,
        bool[]? typeIsUtc = null,
        TimeZoneInfo? systemZone = null,
        bool directPosix = false)
    {
        this.transitions = transitions;
        this.transitionTypes = transitionTypes;
        this.types = types;
        this.typeIsStandard = typeIsStandard ?? new bool[types.Length];
        this.typeIsUtc = typeIsUtc ?? new bool[types.Length];
        this.defaultType = defaultType;
        this.future = future;
        this.systemZone = systemZone;
        this.directPosix = directPosix;
    }

    internal static JqTimeZone Resolve(
        IReadOnlyDictionary<string, string>? environment,
        JqTimeZoneContext? context = null)
    {
        string identifier;
        if (DotNetJq.Port.libjq.jq_getenv(environment, "TZ", out var configured))
        {
            identifier = configured;
        }
        else
        {
            identifier = TimeZoneInfo.Local.Id;
        }

        return context?.Resolve(identifier) ?? Create(identifier);
    }

    internal JqTimeZonePeriod GetPeriod(long unixSeconds)
    {
        if (systemZone is not null)
        {
            return GetSystemPeriod(unixSeconds);
        }

        if (transitions.Length == 0)
        {
            if (future is null || !directPosix)
            {
                return types[defaultType];
            }

            return future.GetPeriod(unixSeconds, GetUtcRuleYear(unixSeconds));
        }

        if (unixSeconds < transitions[0])
        {
            return types[defaultType];
        }

        if (unixSeconds >= transitions[^1] && future is not null)
        {
            return TryGetUtcRuleYear(unixSeconds, out var year)
                ? future.GetPeriod(unixSeconds, year)
                // tzfile.c falls back to the final transition type when its
                // UTC probe cannot be represented as struct tm.
                : types[transitionTypes[^1]];
        }

        var index = Array.BinarySearch(transitions, unixSeconds);
        if (index < 0)
        {
            index = ~index - 1;
        }
        else
        {
            while (index + 1 < transitions.Length && transitions[index + 1] == unixSeconds)
            {
                index++;
            }
        }

        return types[transitionTypes[index]];
    }

    private static long GetUtcRuleYear(long unixSeconds)
    {
        if (!TryGetUtcRuleYear(unixSeconds, out var year))
        {
            // Direct POSIX localtime first calls __offtime(timer, 0) and
            // fails before applying even an offset that could bring the
            // result back into struct-tm range.
            throw new ArgumentOutOfRangeException(nameof(unixSeconds));
        }

        return year;
    }

    private static bool TryGetUtcRuleYear(long unixSeconds, out long year)
    {
        var civilYear = CivilFromDays(FloorDiv(unixSeconds, 86_400)).Year;
        var yearSince1900 = civilYear - 1900;
        if (yearSince1900 is < int.MinValue or > int.MaxValue)
        {
            year = 0;
            return false;
        }

        // __tz_compute uses `1900 + tm->tm_year` in int arithmetic.  The
        // pinned glibc build visibly wraps at the positive tm_year edge.
        year = unchecked(1900 + (int)yearSince1900);
        return true;
    }

    internal string GetRepresentativeAbbreviation(bool daylight)
    {
        if (systemZone is not null)
        {
            return daylight ? systemZone.DaylightName : systemZone.StandardName;
        }

        if (future is not null)
        {
            return daylight ? future.Daylight.Abbreviation : future.Standard.Abbreviation;
        }

        for (var index = transitionTypes.Length - 1; index >= 0; index--)
        {
            var candidate = types[transitionTypes[index]];
            if (candidate.IsDaylightSaving == daylight)
            {
                return candidate.Abbreviation;
            }
        }

        return types.FirstOrDefault(
            type => type.IsDaylightSaving == daylight,
            types[defaultType]).Abbreviation;
    }

    internal JqNormalizedLocalTime NormalizeLocal(
        long localSeconds,
        JqTimeZoneContext? context = null,
        int secondAdjustment = 0)
    {
        if (TryInterpretLocal(localSeconds, -1, context, out var normalized))
        {
            if (secondAdjustment != 0)
            {
                long adjustedUnixSeconds;
                try
                {
                    adjustedUnixSeconds = checked(normalized.UnixSeconds + secondAdjustment);
                }
                catch (OverflowException)
                {
                    throw new ArgumentOutOfRangeException(nameof(localSeconds));
                }

                // glibc updates its offset guess at offset_found, before the
                // raw tm_sec correction, then converts the adjusted epoch
                // without changing that guess again.
                if (!TryConvert(
                        adjustedUnixSeconds,
                        out var adjustedPeriod,
                        out var adjustedLocalSeconds))
                {
                    throw new ArgumentOutOfRangeException(nameof(localSeconds));
                }

                return new JqNormalizedLocalTime(
                    adjustedUnixSeconds,
                    adjustedLocalSeconds,
                    adjustedPeriod);
            }

            return normalized;
        }

        throw new ArgumentOutOfRangeException(nameof(localSeconds));
    }

    /// <summary>
    /// Implements the explicit <c>tm_isdst</c> interpretation used by glibc
    /// strftime(%s).  A UTC-normalized jq array carries tm_isdst=0 even when
    /// the same wall clock lies in local daylight time, so this intentionally
    /// differs from <see cref="NormalizeLocal"/>.
    /// </summary>
    internal long InterpretLocal(
        long localSeconds,
        int isDaylightSaving,
        JqTimeZoneContext? context = null)
    {
        return TryInterpretLocal(localSeconds, isDaylightSaving, context, out var normalized)
            ? normalized.UnixSeconds
            : -1;
    }

    private static void UpdateMktimeGuess(
        JqTimeZoneContext? context,
        long requestedLocalSeconds,
        long unixSeconds)
    {
        if (context is not null)
        {
            var guess = requestedLocalSeconds - unixSeconds;
            context.MktimeOffsetGuessSeconds = guess < int.MinValue
                ? int.MinValue
                : guess > int.MaxValue
                    ? int.MaxValue
                    : (int)guess;
        }
    }

    // Direct managed shape of glibc-2.39 time/mktime.c:
    // __mktime_internal.  Leap-second tables are the sole omitted branch.
    private bool TryInterpretLocal(
        long localSeconds,
        int requestedDaylightSaving,
        JqTimeZoneContext? context,
        out JqNormalizedLocalTime normalized)
    {
        normalized = default;
        var guess = context?.MktimeOffsetGuessSeconds ?? 0;
        long candidate;
        try
        {
            candidate = checked(localSeconds - guess);
        }
        catch (OverflowException)
        {
            return false;
        }

        var t1 = candidate;
        var t2 = candidate;
        var previousWasDaylight = false;
        var remainingProbes = 6;
        JqTimeZonePeriod period;
        long convertedLocal;

        while (true)
        {
            if (!TryConvert(candidate, out period, out convertedLocal))
            {
                return false;
            }

            long delta;
            try
            {
                delta = checked(localSeconds - convertedLocal);
            }
            catch (OverflowException)
            {
                return false;
            }

            if (delta == 0)
            {
                break;
            }

            var currentIsDaylight = period.IsDaylightSaving;
            var acceptOscillation = candidate == t1 && candidate != t2 &&
                (requestedDaylightSaving < 0
                    ? !previousWasDaylight || currentIsDaylight
                    : (requestedDaylightSaving != 0) != currentIsDaylight);
            if (acceptOscillation)
            {
                return OffsetFound(
                    localSeconds,
                    candidate,
                    convertedLocal,
                    period,
                    context,
                    out normalized);
            }

            if (--remainingProbes == 0)
            {
                return false;
            }

            t1 = t2;
            t2 = candidate;
            try
            {
                candidate = checked(candidate + delta);
            }
            catch (OverflowException)
            {
                return false;
            }

            previousWasDaylight = currentIsDaylight;
        }

        if (DaylightSavingDiffers(requestedDaylightSaving, period.IsDaylightSaving))
        {
            var daylightDifference =
                (requestedDaylightSaving == 0 ? 1 : 0) -
                (!period.IsDaylightSaving ? 1 : 0);
            const int stride = 601_200;
            const int deltaBound = 457_243_209 / 2 + stride;
            for (var delta = stride; delta < deltaBound; delta += stride)
            {
                for (var direction = -1; direction <= 1; direction += 2)
                {
                    long probe;
                    try
                    {
                        probe = checked(candidate + ((long)delta * direction));
                    }
                    catch (OverflowException)
                    {
                        continue;
                    }

                    if (!TryConvert(probe, out var probePeriod, out var probeLocal) ||
                        DaylightSavingDiffers(
                            requestedDaylightSaving,
                            probePeriod.IsDaylightSaving))
                    {
                        continue;
                    }

                    long extrapolated;
                    try
                    {
                        extrapolated = checked(probe + (localSeconds - probeLocal));
                    }
                    catch (OverflowException)
                    {
                        continue;
                    }

                    if (TryConvert(extrapolated, out period, out convertedLocal))
                    {
                        candidate = extrapolated;
                        return OffsetFound(
                            localSeconds,
                            candidate,
                            convertedLocal,
                            period,
                            context,
                            out normalized);
                    }
                }
            }

            try
            {
                candidate = checked(candidate + (3_600L * daylightDifference));
            }
            catch (OverflowException)
            {
                return false;
            }

            if (!TryConvert(candidate, out period, out convertedLocal))
            {
                return false;
            }
        }

        return OffsetFound(
            localSeconds,
            candidate,
            convertedLocal,
            period,
            context,
            out normalized);
    }

    private bool TryConvert(
        long unixSeconds,
        out JqTimeZonePeriod period,
        out long localSeconds)
    {
        try
        {
            period = GetPeriod(unixSeconds);
            localSeconds = checked(unixSeconds + period.OffsetSeconds);
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentOutOfRangeException or OverflowException)
        {
            period = default;
            localSeconds = 0;
            return false;
        }
    }

    private static bool DaylightSavingDiffers(int requested, bool actual) =>
        requested >= 0 && (requested != 0) != actual;

    private static bool OffsetFound(
        long requestedLocalSeconds,
        long unixSeconds,
        long convertedLocalSeconds,
        JqTimeZonePeriod period,
        JqTimeZoneContext? context,
        out JqNormalizedLocalTime normalized)
    {
        UpdateMktimeGuess(context, requestedLocalSeconds, unixSeconds);
        normalized = new JqNormalizedLocalTime(
            unixSeconds,
            convertedLocalSeconds,
            period);
        return true;
    }

    private JqTimeZonePeriod GetSystemPeriod(long unixSeconds)
    {
        DateTimeOffset instant;
        try
        {
            instant = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return types[defaultType];
        }

        var offset = systemZone!.GetUtcOffset(instant);
        var daylight = systemZone.IsDaylightSavingTime(instant);
        return new JqTimeZonePeriod(
            checked((int)offset.TotalSeconds),
            daylight,
            daylight ? systemZone.DaylightName : systemZone.StandardName);
    }

    internal static JqTimeZone Create(string identifier) =>
        Create(identifier, OperatingSystem.IsWindows());

    // The explicit switch makes the Windows ordering testable on Unix CI
    // without changing the production platform decision.
    internal static JqTimeZone Create(
        string identifier,
        bool preferSystemTimeZoneIdentifiers)
    {
        if (identifier.Length == 0 || identifier == ":")
        {
            return Fixed(0, "UTC");
        }

        var normalized = identifier[0] == ':' ? identifier[1..] : identifier;
        if (TryReadZoneInfo(normalized, out var zone))
        {
            return zone;
        }

        if (preferSystemTimeZoneIdentifiers &&
            TryCreateSystemZone(normalized, out var preferredSystemZone))
        {
            return preferredSystemZone;
        }

        if (PosixZone.TryParse(normalized, out var posix))
        {
            if (posix.UsesDefaultRules &&
                TryReadZoneInfo("posixrules", out var defaultRules) &&
                defaultRules.types.Length >= 2)
            {
                return defaultRules.ApplyDefaultRules(posix);
            }

            return new JqTimeZone(
                [],
                [],
                [posix.Standard, posix.Daylight],
                0,
                posix,
                directPosix: true);
        }

        if (TryCreateSystemZone(normalized, out var systemZone))
        {
            return systemZone;
        }

        return InvalidIdentifierFallback(normalized);
    }

    private static bool TryCreateSystemZone(string identifier, out JqTimeZone zone)
    {
        try
        {
            var system = TimeZoneInfo.FindSystemTimeZoneById(identifier);
            var standard = new JqTimeZonePeriod(
                checked((int)system.BaseUtcOffset.TotalSeconds),
                false,
                system.StandardName);
            zone = new JqTimeZone([], [], [standard], 0, null, systemZone: system);
            return true;
        }
        catch (Exception exception) when (
            exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            zone = null!;
            return false;
        }
    }

    private static JqTimeZone InvalidIdentifierFallback(string identifier)
    {
        if (identifier.Length == 0)
        {
            return Fixed(0, string.Empty);
        }

        if (identifier[0] == '<')
        {
            var end = identifier.IndexOf('>', 1);
            if (end < 4)
            {
                return Fixed(0, string.Empty);
            }

            var candidate = identifier.AsSpan(1, end - 1);
            foreach (var character in candidate)
            {
                if (!IsPosixNameCharacter(character))
                {
                    return Fixed(0, string.Empty);
                }
            }

            return Fixed(0, candidate.ToString());
        }

        var length = 0;
        while (length < identifier.Length && char.IsAsciiLetter(identifier[length]))
        {
            length++;
        }

        var abbreviation = length >= 3 ? identifier[..length] : string.Empty;
        return Fixed(0, abbreviation);
    }

    private static bool IsPosixNameCharacter(char value) =>
        char.IsAsciiLetterOrDigit(value) || value is '+' or '-';

    private static JqTimeZone Fixed(int offsetSeconds, string abbreviation) =>
        new(
            [],
            [],
            [new JqTimeZonePeriod(offsetSeconds, false, abbreviation)],
            0,
            null);

    /// <summary>
    /// Direct managed counterpart of glibc tzfile.c:__tzfile_default.  The
    /// installed posixrules transition calendar is retained, its historical
    /// transition instants are corrected for the caller's offsets, and each
    /// target type is collapsed to caller standard/daylight.  Beyond the last
    /// TZif transition glibc evaluates the file's own footer, so <see
    /// cref="future"/> deliberately remains the posixrules footer rather than
    /// being rebased to the requested periods.
    /// </summary>
    private JqTimeZone ApplyDefaultRules(PosixZone requested)
    {
        var adjustedTransitions = transitions.ToArray();
        var adjustedTypes = new byte[transitionTypes.Length];

        var ruleStandardOffset = 0;
        // __tzfile_default retains the requested DST offset as its wall-time
        // baseline.  Consequently transitions expressed relative to the
        // preceding daylight period are not shifted when only the requested
        // DST offset differs; this is visible with TZ=EST5EDT4:30.
        var ruleDaylightOffset = requested.Daylight.OffsetSeconds;
        if (transitions.Length == 0)
        {
            ruleStandardOffset = ruleDaylightOffset = types[0].OffsetSeconds;
        }
        else
        {
            var foundStandard = false;
            for (var index = transitionTypes.Length - 1;
                 index >= 0 && !foundStandard;
                 index--)
            {
                var candidate = types[transitionTypes[index]];
                if (!candidate.IsDaylightSaving)
                {
                    ruleStandardOffset = candidate.OffsetSeconds;
                    foundStandard = true;
                }
            }
        }

        var previousWasDaylight = false;
        for (var index = 0; index < transitionTypes.Length; index++)
        {
            var originalTypeIndex = transitionTypes[index];
            var originalType = types[originalTypeIndex];
            adjustedTypes[index] = originalType.IsDaylightSaving ? (byte)1 : (byte)0;

            if (!typeIsUtc[originalTypeIndex])
            {
                var correction = previousWasDaylight && !typeIsStandard[originalTypeIndex]
                    ? requested.Daylight.OffsetSeconds - ruleDaylightOffset
                    : requested.Standard.OffsetSeconds - ruleStandardOffset;
                adjustedTransitions[index] = checked(adjustedTransitions[index] + correction);
            }

            previousWasDaylight = originalType.IsDaylightSaving;
        }

        return new JqTimeZone(
            adjustedTransitions,
            adjustedTypes,
            [requested.Standard, requested.Daylight],
            0,
            future);
    }

    private static bool TryReadZoneInfo(string identifier, out JqTimeZone zone)
    {
        zone = null!;
        if (identifier.Length == 0 || Path.IsPathRooted(identifier))
        {
            return false;
        }

        // Preserve the relative spelling passed to the filesystem.  Just like
        // open(2), this accepts internal repeated separators and harmless dot
        // segments, but a trailing separator (or `UTC/.`) still fails when the
        // resolved zone is a file.  Only parent traversal is rejected by the
        // managed host-access policy.
        var segments = identifier.Split(['/', '\\'], StringSplitOptions.None);
        if (segments.Any(static segment => segment == "..") ||
            identifier.EndsWith("/.", StringComparison.Ordinal) ||
            identifier.EndsWith("\\.", StringComparison.Ordinal))
        {
            return false;
        }

        var relativePath = identifier;

        foreach (var root in ZoneInfoRoots)
        {
            var path = Path.Combine(root, relativePath);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var bytes = File.ReadAllBytes(path);
                if (TryParseTzif(bytes, out zone))
                {
                    return true;
                }
            }
            catch (IOException)
            {
                // Continue to public TimeZoneInfo/POSIX handling.
            }
            catch (UnauthorizedAccessException)
            {
                // Continue to public TimeZoneInfo/POSIX handling.
            }
        }

        return false;
    }

    private static bool TryParseTzif(ReadOnlySpan<byte> bytes, out JqTimeZone zone)
    {
        zone = null!;
        if (!TryReadHeader(bytes, 0, out var first))
        {
            return false;
        }

        var headerOffset = 0;
        var timeSize = 4;
        if (first.Version is (byte)'2' or (byte)'3' or (byte)'4')
        {
            headerOffset = checked(44 + first.BlockSize(4));
            if (!TryReadHeader(bytes, headerOffset, out first))
            {
                return false;
            }

            timeSize = 8;
        }

        var cursor = checked(headerOffset + 44);
        if (first.TypeCount <= 0 || first.TypeCount > 256 ||
            cursor + first.BlockSize(timeSize) > bytes.Length)
        {
            return false;
        }

        var transitions = new long[first.TimeCount];
        for (var index = 0; index < transitions.Length; index++)
        {
            transitions[index] = timeSize == 8
                ? BinaryPrimitives.ReadInt64BigEndian(bytes.Slice(cursor, 8))
                : BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(cursor, 4));
            cursor += timeSize;
        }

        var transitionTypes = bytes.Slice(cursor, first.TimeCount).ToArray();
        cursor += first.TimeCount;

        var rawTypes = new RawType[first.TypeCount];
        for (var index = 0; index < rawTypes.Length; index++)
        {
            rawTypes[index] = new RawType(
                BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(cursor, 4)),
                bytes[cursor + 4] != 0,
                bytes[cursor + 5]);
            cursor += 6;
        }

        var abbreviations = bytes.Slice(cursor, first.CharCount);
        cursor += first.CharCount;
        var types = new JqTimeZonePeriod[rawTypes.Length];
        for (var index = 0; index < rawTypes.Length; index++)
        {
            var raw = rawTypes[index];
            if (raw.AbbreviationIndex >= abbreviations.Length)
            {
                return false;
            }

            var tail = abbreviations[raw.AbbreviationIndex..];
            var nul = tail.IndexOf((byte)0);
            if (nul < 0)
            {
                nul = tail.Length;
            }

            types[index] = new JqTimeZonePeriod(
                raw.OffsetSeconds,
                raw.IsDaylightSaving,
                Encoding.ASCII.GetString(tail[..nul]));
        }

        if (transitionTypes.Any(index => index >= types.Length))
        {
            return false;
        }

        cursor = checked(cursor + (first.LeapCount * (timeSize + 4)));
        var typeIsStandard = new bool[types.Length];
        for (var index = 0; index < first.StandardCount; index++)
        {
            typeIsStandard[index] = bytes[cursor++] != 0;
        }

        var typeIsUtc = new bool[types.Length];
        for (var index = 0; index < first.UtcCount; index++)
        {
            typeIsUtc[index] = bytes[cursor++] != 0;
        }

        PosixZone? future = null;
        if (timeSize == 8 && cursor < bytes.Length && bytes[cursor] == (byte)'\n')
        {
            cursor++;
            var end = bytes[cursor..].IndexOf((byte)'\n');
            if (end > 0)
            {
                var footer = Encoding.ASCII.GetString(bytes.Slice(cursor, end));
                if (PosixZone.TryParse(footer, out var parsedFooter))
                {
                    future = parsedFooter;
                }
            }
        }

        var defaultType = Array.FindIndex(types, static type => !type.IsDaylightSaving);
        if (defaultType < 0)
        {
            defaultType = 0;
        }

        zone = new JqTimeZone(
            transitions,
            transitionTypes,
            types,
            defaultType,
            future,
            typeIsStandard,
            typeIsUtc);
        return true;
    }

    private static bool TryReadHeader(
        ReadOnlySpan<byte> bytes,
        int offset,
        out TzifHeader header)
    {
        header = default;
        if (offset < 0 || offset + 44 > bytes.Length ||
            !bytes.Slice(offset, 4).SequenceEqual("TZif"u8))
        {
            return false;
        }

        header = new TzifHeader(
            bytes[offset + 4],
            ReadCount(bytes, offset + 20),
            ReadCount(bytes, offset + 24),
            ReadCount(bytes, offset + 28),
            ReadCount(bytes, offset + 32),
            ReadCount(bytes, offset + 36),
            ReadCount(bytes, offset + 40));
        return header.IsValid;
    }

    private static int ReadCount(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(offset, 4));

    private readonly record struct TzifHeader(
        byte Version,
        int UtcCount,
        int StandardCount,
        int LeapCount,
        int TimeCount,
        int TypeCount,
        int CharCount)
    {
        internal bool IsValid =>
            UtcCount >= 0 && StandardCount >= 0 && LeapCount >= 0 &&
            TimeCount >= 0 && TypeCount >= 0 && CharCount >= 0;

        internal int BlockSize(int timeSize) => checked(
            (TimeCount * timeSize) +
            TimeCount +
            (TypeCount * 6) +
            CharCount +
            (LeapCount * (timeSize + 4)) +
            StandardCount +
            UtcCount);
    }

    private readonly record struct RawType(
        int OffsetSeconds,
        bool IsDaylightSaving,
        byte AbbreviationIndex);

    private sealed class PosixZone
    {
        private readonly TransitionRule? daylightStart;
        private readonly TransitionRule? standardStart;

        private PosixZone(
            JqTimeZonePeriod standard,
            JqTimeZonePeriod daylight,
            TransitionRule? daylightStart,
            TransitionRule? standardStart,
            bool usesDefaultRules = false)
        {
            Standard = standard;
            Daylight = daylight;
            this.daylightStart = daylightStart;
            this.standardStart = standardStart;
            UsesDefaultRules = usesDefaultRules;
        }

        internal JqTimeZonePeriod Standard { get; }
        internal JqTimeZonePeriod Daylight { get; }
        internal bool UsesDefaultRules { get; }

        internal static bool TryParse(string value, out PosixZone zone)
        {
            zone = null!;
            if (value.Length == 0)
            {
                return false;
            }

            var cursor = 0;
            if (!TryReadName(value, ref cursor, out var standardName))
            {
                return false;
            }

            var standardOffset = 0;
            if (cursor < value.Length &&
                (char.IsAsciiDigit(value[cursor]) || value[cursor] is '+' or '-'))
            {
                if (!TryReadOffset(value, ref cursor, out var posixOffset))
                {
                    return false;
                }

                standardOffset = -posixOffset;
            }

            var standard = new JqTimeZonePeriod(standardOffset, false, standardName);
            if (cursor == value.Length || value[cursor] == ',')
            {
                zone = new PosixZone(standard, standard, null, null);
                return cursor == value.Length;
            }

            if (!TryReadName(value, ref cursor, out var daylightName))
            {
                // tzset.c clears both rules before parsing.  If a standard
                // name/offset succeeds but trailing text is not a daylight
                // name (for example ABC1: or Invalid/Foo), glibc keeps that
                // partially parsed standard rule and the zeroed daylight
                // rule.  Their J0/day-zero changes remain observable.
                var zeroRule = new TransitionRule(
                    RuleKind.DayOfYear,
                    0,
                    0,
                    0,
                    0,
                    TimeBasis.Wall);
                zone = new PosixZone(
                    standard,
                    new JqTimeZonePeriod(0, true, string.Empty),
                    zeroRule,
                    zeroRule);
                return true;
            }

            var daylightOffset = checked(standardOffset + 3_600);
            if (cursor < value.Length && value[cursor] != ',')
            {
                if (!TryReadOffset(value, ref cursor, out var posixDaylightOffset))
                {
                    return false;
                }

                daylightOffset = -posixDaylightOffset;
            }

            var daylight = new JqTimeZonePeriod(daylightOffset, true, daylightName);
            TransitionRule start;
            TransitionRule end;
            if (cursor == value.Length ||
                (cursor + 1 == value.Length && value[cursor] == ','))
            {
                // If the installed TZDEFRULES/posixrules TZif is unavailable,
                // glibc falls back to parse_rule's contemporary US defaults.
                start = TransitionRule.MonthWeekDay(3, 2, 0, 2 * 3_600, TimeBasis.Wall);
                end = TransitionRule.MonthWeekDay(11, 1, 0, 2 * 3_600, TimeBasis.Wall);
                zone = new PosixZone(
                    standard,
                    daylight,
                    start,
                    end,
                    usesDefaultRules: true);
                return true;
            }
            else
            {
                if (value[cursor] == ',')
                {
                    cursor++;
                }

                if (!TryReadRule(value, ref cursor, out start))
                {
                    var zeroRule = new TransitionRule(
                        RuleKind.DayOfYear,
                        0,
                        0,
                        0,
                        0,
                        TimeBasis.Wall);
                    zone = new PosixZone(standard, daylight, start, zeroRule);
                    return true;
                }

                if (cursor == value.Length)
                {
                    // parse_rule("", &tz_rules[1]) installs glibc's default
                    // November rule even when only the first explicit rule
                    // was supplied.
                    end = TransitionRule.MonthWeekDay(
                        11,
                        1,
                        0,
                        2 * 3_600,
                        TimeBasis.Wall);
                }
                else
                {
                    if (value[cursor] == ',')
                    {
                        cursor++;
                    }

                    if (cursor == value.Length)
                    {
                        // parse_rule skips one optional comma before testing
                        // for NUL, so an explicit trailing comma takes the
                        // same default-November path as an absent second rule.
                        end = TransitionRule.MonthWeekDay(
                            11,
                            1,
                            0,
                            2 * 3_600,
                            TimeBasis.Wall);
                    }
                    else
                    {
                        // parse_rule mutates its destination before it reports
                        // failure, and __tzset_parse_tz ignores trailing bytes
                        // after the second call.  Keep both properties.
                        _ = TryReadRule(value, ref cursor, out end);
                    }
                }
            }

            zone = new PosixZone(standard, daylight, start, end);
            return true;
        }

        internal JqTimeZonePeriod GetPeriod(long unixSeconds, long year)
        {
            if (daylightStart is null || standardStart is null ||
                Standard.Equals(Daylight))
            {
                return Standard;
            }

            var start = ToUnixTransition(daylightStart.Value, year, daylightTransition: true);
            var end = ToUnixTransition(standardStart.Value, year, daylightTransition: false);
            // glibc tzset.c uses the southern-hemisphere branch only when
            // start is strictly later than end.  Equal computed instants
            // therefore describe an empty, not an all-year, DST interval.
            var isDaylight = start <= end
                ? unixSeconds >= start && unixSeconds < end
                : unixSeconds >= start || unixSeconds < end;
            return isDaylight ? Daylight : Standard;
        }

        internal IEnumerable<long> GetTransitions(long year)
        {
            if (daylightStart is null || standardStart is null)
            {
                yield break;
            }

            yield return ToUnixTransition(daylightStart.Value, year, daylightTransition: true);
            yield return ToUnixTransition(standardStart.Value, year, daylightTransition: false);
        }

        private long ToUnixTransition(
            TransitionRule rule,
            long year,
            bool daylightTransition)
        {
            var ruleYear = unchecked((int)year);
            // Direct shape of tzset.c:compute_change.  The calendar-day sum
            // is int in glibc and can wrap before SECSPERDAY widens it to
            // time64_t; this is observable from roughly year 5.88 million.
            long localSeconds = 0;
            if (ruleYear > 1970)
            {
                var days = unchecked(
                    ((ruleYear - 1970) * 365) +
                    (((ruleYear - 1) / 4) - (1970 / 4)) -
                    (((ruleYear - 1) / 100) - (1970 / 100)) +
                    (((ruleYear - 1) / 400) - (1970 / 400)));
                localSeconds = (long)days * 86_400;
            }

            localSeconds = checked(
                localSeconds + (rule.GetDayOffset(ruleYear) * 86_400) + rule.Seconds);
            var basisOffset = rule.Basis switch
            {
                TimeBasis.Utc => 0,
                TimeBasis.Standard => Standard.OffsetSeconds,
                _ => daylightTransition ? Standard.OffsetSeconds : Daylight.OffsetSeconds,
            };
            return checked(localSeconds - basisOffset);
        }

        private static bool TryReadName(string value, ref int cursor, out string name)
        {
            name = string.Empty;
            if (cursor >= value.Length)
            {
                return false;
            }

            if (value[cursor] == '<')
            {
                var end = value.IndexOf('>', cursor + 1);
                if (end < 0 || end - cursor - 1 < 3)
                {
                    return false;
                }

                var candidate = value.AsSpan(cursor + 1, end - cursor - 1);
                foreach (var character in candidate)
                {
                    if (!IsPosixNameCharacter(character))
                    {
                        return false;
                    }
                }

                name = candidate.ToString();
                cursor = end + 1;
                return true;
            }

            var start = cursor;
            while (cursor < value.Length && char.IsAsciiLetter(value[cursor]))
            {
                cursor++;
            }

            if (cursor - start < 3)
            {
                cursor = start;
                return false;
            }

            name = value[start..cursor];
            return true;
        }

        private static bool TryReadOffset(string value, ref int cursor, out int seconds)
        {
            seconds = 0;
            var sign = 1;
            if (cursor < value.Length && value[cursor] is '+' or '-')
            {
                sign = value[cursor++] == '-' ? -1 : 1;
            }

            if (!TryReadUnsignedShort(value, ref cursor, out var hour))
            {
                return false;
            }

            var minute = 0;
            var second = 0;
            if (cursor < value.Length && value[cursor] == ':')
            {
                var hourEnd = cursor;
                cursor++;
                if (!TryReadUnsignedShort(value, ref cursor, out minute))
                {
                    // sscanf has already assigned hh, and its last %n still
                    // points immediately after hh when the following %hu
                    // fails.  The colon therefore remains unconsumed.
                    cursor = hourEnd;
                    minute = 0;
                    goto parsed;
                }

                if (cursor < value.Length && value[cursor] == ':')
                {
                    var minuteEnd = cursor;
                    cursor++;
                    if (!TryReadUnsignedShort(value, ref cursor, out second))
                    {
                        cursor = minuteEnd;
                        second = 0;
                    }
                }
            }

        parsed:
            // glibc's compute_offset clamps each parsed unsigned-short
            // component instead of rejecting an out-of-POSIX-range spelling.
            hour = Math.Min(hour, 24);
            minute = Math.Min(minute, 59);
            second = Math.Min(second, 59);
            seconds = checked(sign * ((hour * 3_600) + (minute * 60) + second));
            return true;
        }

        private static bool TryReadRule(
            string value,
            ref int cursor,
            out TransitionRule rule)
        {
            var kind = RuleKind.DayOfYear;
            var first = 0;
            var second = 0;
            var third = 0;
            var transitionSeconds = 0;

            if (cursor < value.Length && value[cursor] == 'M')
            {
                kind = RuleKind.MonthWeekDay;
                cursor++;
                if (!TryReadUnsignedShort(value, ref cursor, out first))
                {
                    return FinishRule(
                        out rule, kind, first, second, third, transitionSeconds, false);
                }

                if (cursor >= value.Length || value[cursor++] != '.' ||
                    !TryReadUnsignedShort(value, ref cursor, out second))
                {
                    return FinishRule(
                        out rule, kind, first, second, third, transitionSeconds, false);
                }

                if (cursor >= value.Length || value[cursor++] != '.' ||
                    !TryReadUnsignedShort(value, ref cursor, out third))
                {
                    return FinishRule(
                        out rule, kind, first, second, third, transitionSeconds, false);
                }

                if (first is < 1 or > 12 || second is < 1 or > 5 || third > 6)
                {
                    return FinishRule(
                        out rule, kind, first, second, third, transitionSeconds, false);
                }
            }
            else if (cursor < value.Length &&
                (value[cursor] == 'J' || char.IsAsciiDigit(value[cursor])))
            {
                var julianWithoutLeap = value[cursor] == 'J';
                kind = julianWithoutLeap
                    ? RuleKind.JulianWithoutLeap
                    : RuleKind.DayOfYear;
                if (julianWithoutLeap)
                {
                    cursor++;
                    if (cursor >= value.Length || !char.IsAsciiDigit(value[cursor]))
                    {
                        return FinishRule(
                            out rule, kind, first, second, third, transitionSeconds, false);
                    }
                }

                if (!TryReadUnsignedDecimal(value, ref cursor, out var rawDay) ||
                    rawDay > 365 || (julianWithoutLeap && rawDay == 0))
                {
                    // parse_rule sets the type before validation, but d is
                    // assigned only after all J/day checks succeed.
                    return FinishRule(
                        out rule, kind, first, second, third, transitionSeconds, false);
                }

                first = (int)rawDay;
            }
            else
            {
                return FinishRule(
                    out rule, kind, first, second, third, transitionSeconds, false);
            }

            if (cursor < value.Length && value[cursor] is not '/' and not ',')
            {
                return FinishRule(
                    out rule, kind, first, second, third, transitionSeconds, false);
            }

            if (cursor < value.Length && value[cursor] == '/')
            {
                cursor++;
                if (cursor == value.Length)
                {
                    return FinishRule(
                        out rule, kind, first, second, third, transitionSeconds, false);
                }

                var negative = value[cursor] == '-';
                if (negative)
                {
                    cursor++;
                }

                // sscanf starts these locals with 2:00:00 and is allowed to
                // assign none of them.  `%hu` accepts an optional sign.
                var hour = 2;
                var minute = 0;
                var secondValue = 0;
                if (TryReadUnsignedShort(value, ref cursor, out var parsedHour))
                {
                    hour = parsedHour;
                    if (cursor < value.Length && value[cursor] == ':')
                    {
                        var hourEnd = cursor;
                        cursor++;
                        if (!TryReadUnsignedShort(value, ref cursor, out minute))
                        {
                            cursor = hourEnd;
                            minute = 0;
                        }
                        else if (cursor < value.Length && value[cursor] == ':')
                        {
                            var minuteEnd = cursor;
                            cursor++;
                            if (!TryReadUnsignedShort(value, ref cursor, out secondValue))
                            {
                                cursor = minuteEnd;
                                secondValue = 0;
                            }
                        }
                    }
                }

                transitionSeconds = checked(
                    (negative ? -1 : 1) *
                    ((hour * 3_600) + (minute * 60) + secondValue));
            }
            else
            {
                transitionSeconds = 2 * 3_600;
            }

            return FinishRule(
                out rule, kind, first, second, third, transitionSeconds, true);
        }

        private static bool FinishRule(
            out TransitionRule rule,
            RuleKind kind,
            int first,
            int second,
            int third,
            int transitionSeconds,
            bool success)
        {
            rule = new TransitionRule(
                kind,
                first,
                second,
                third,
                transitionSeconds,
                TimeBasis.Wall);
            return success;
        }

        private static bool TryReadUnsignedShort(
            string value,
            ref int cursor,
            out int number)
        {
            var start = cursor;
            var negative = false;
            if (cursor < value.Length && value[cursor] is '+' or '-')
            {
                negative = value[cursor] == '-';
                cursor++;
            }

            if (!TryReadUnsignedDecimal(value, ref cursor, out var parsed))
            {
                cursor = start;
                number = 0;
                return false;
            }

            // glibc tzset.c uses scanf("%hu") for POSIX offsets, M rules,
            // and rule times.  scanf consumes the entire decimal token and
            // then stores through unsigned short; it does not impose a digit
            // width.  Preserve the observable 16-bit narrowing (and the
            // ULONG_MAX result used by glibc after decimal overflow).
            if (negative && parsed != ulong.MaxValue)
            {
                parsed = unchecked(0UL - parsed);
            }

            number = unchecked((ushort)parsed);
            return true;
        }

        private static bool TryReadUnsignedDecimal(
            string value,
            ref int cursor,
            out ulong number)
        {
            number = 0;
            var start = cursor;
            while (cursor < value.Length && char.IsAsciiDigit(value[cursor]))
            {
                var digit = (uint)(value[cursor++] - '0');
                number = number > (ulong.MaxValue - digit) / 10
                    ? ulong.MaxValue
                    : (number * 10) + digit;
            }

            return cursor != start;
        }
    }

    private enum RuleKind
    {
        MonthWeekDay,
        JulianWithoutLeap,
        DayOfYear,
    }

    private enum TimeBasis
    {
        Wall,
        Standard,
        Utc,
    }

    private readonly record struct TransitionRule(
        RuleKind Kind,
        int First,
        int Second,
        int Third,
        int Seconds,
        TimeBasis Basis)
    {
        internal static TransitionRule MonthWeekDay(
            int month,
            int week,
            int day,
            int seconds,
            TimeBasis basis) =>
            new(RuleKind.MonthWeekDay, month, week, day, seconds, basis);

        internal long GetDayOffset(int year)
        {
            if (Kind == RuleKind.DayOfYear)
            {
                return First;
            }

            if (Kind == RuleKind.JulianWithoutLeap)
            {
                return First - 1L + (IsLeapYear(year) && First >= 60 ? 1 : 0);
            }

            // tzset.c uses this Zeller expression with C int truncation and
            // remainder, not proleptic Gregorian floor division.  Keep the
            // intermediate int wrap at tm_year's signed boundary.
            var m1 = (First + 9) % 12 + 1;
            var yy0 = First <= 2 ? unchecked(year - 1) : year;
            var yy1 = yy0 / 100;
            var yy2 = yy0 % 100;
            var dayOfWeek = unchecked(
                (((26 * m1) - 2) / 10) + 1 + yy2 + (yy2 / 4) +
                (yy1 / 4) - (2 * yy1)) % 7;
            if (dayOfWeek < 0)
            {
                dayOfWeek += 7;
            }

            var day = Third - dayOfWeek;
            if (day < 0)
            {
                day += 7;
            }

            var leap = IsLeapYear(year);
            var (daysBeforeMonth, daysThroughMonth) = MonthBoundaries(leap, First);
            for (var occurrence = 1U; occurrence < (uint)Second; occurrence++)
            {
                if (day + 7 >= daysThroughMonth - daysBeforeMonth)
                {
                    break;
                }

                day += 7;
            }

            return daysBeforeMonth + day;
        }

        private static (int Before, int Through) MonthBoundaries(bool leap, int month)
        {
            ReadOnlySpan<int> boundaries = leap
                ? [0, 31, 60, 91, 121, 152, 182, 213, 244, 274, 305, 335, 366]
                : [0, 31, 59, 90, 120, 151, 181, 212, 243, 273, 304, 334, 365];
            if (month is >= 1 and <= 12)
            {
                return (boundaries[month - 1], boundaries[month]);
            }

            // Malformed M fields make native compute_change read outside its
            // month table (undefined C behavior).  Keep the proxy bounded;
            // this narrow class is explicitly excluded from parity.
            return (0, 0);
        }
    }

    private static bool IsLeapYear(long year) =>
        year % 4 == 0 && (year % 100 != 0 || year % 400 == 0);

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
