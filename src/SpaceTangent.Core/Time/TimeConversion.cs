// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;

namespace SpaceTangent.Core.Time;

/// <summary>Post-1972 UTC, exact TAI/TT offset and the NAIF periodic TT/TDB model.</summary>
public static class TimeConversion
{
    internal const long TtMinusTai = 32_184_000_000;
    private const long GregorianEpochDay = 1_721_425;
    private const long HalfDay = JulianInstant.NanosecondsPerDay / 2;
    private static readonly JulianInstant TdbStart = new(2_415_020, HalfDay, TimeScale.Tdb);
    private static readonly JulianInstant TdbEnd = new(2_488_069, HalfDay, TimeScale.Tdb);

    public static JulianInstant Convert(JulianInstant epoch, TimeScale target)
    {
        if (target is < TimeScale.Tdb or > TimeScale.Tt)
            throw new ArgumentOutOfRangeException(nameof(target));
        if (epoch.Scale == target)
            return epoch;

        JulianInstant tt = epoch.Scale switch
        {
            TimeScale.Tai => Relabel(epoch.AddNanoseconds(TtMinusTai), TimeScale.Tt),
            TimeScale.Tdb => FromTdb(epoch),
            _ => epoch
        };

        return target switch
        {
            TimeScale.Tai => Relabel(tt.AddNanoseconds(-TtMinusTai), TimeScale.Tai),
            TimeScale.Tdb => ToTdb(tt),
            _ => tt
        };
    }

    public static JulianInstant FromUtc(UtcDateTime utc, TimeScale target = TimeScale.Tai) =>
        Convert(TaiAtUtcMidnight(utc.Date).AddNanoseconds(utc.NanosecondsSinceMidnight), target);

    public static UtcDateTime ToUtc(JulianInstant epoch)
    {
        JulianInstant tai = Convert(epoch, TimeScale.Tai);
        long day = tai.Day - GregorianEpochDay - (tai.NanosecondsOfDay < HalfDay ? 1 : 0);
        // TAI is ahead of UTC, so its civil date or the previous date contains the instant.
        for (long candidate = day; candidate >= day - 1; candidate--)
        {
            if (candidate < LeapSecondTable.FirstDate.DayNumber || candidate > DateOnly.MaxValue.DayNumber)
                continue;

            DateOnly date = DateOnly.FromDayNumber((int)candidate);
            Int128 offset = tai.DifferenceNanoseconds(TaiAtUtcMidnight(date));
            if (offset >= 0 && offset < LeapSecondTable.DayLength(date) * JulianInstant.NanosecondsPerSecond)
                return UtcDateTime.FromDayOffset(date, (long)offset);
        }

        throw new ArgumentOutOfRangeException(nameof(epoch), "Epoch is outside the supported UTC calendar.");
    }

    private static JulianInstant TaiAtUtcMidnight(DateOnly date) =>
        new(GregorianEpochDay + date.DayNumber,
            HalfDay + LeapSecondTable.TaiMinusUtc(date) * JulianInstant.NanosecondsPerSecond, TimeScale.Tai);

    private static JulianInstant FromTdb(JulianInstant tdb)
    {
        ValidateTdb(tdb);
        return Relabel(tdb.AddNanoseconds(-PeriodicCorrection(tdb)), TimeScale.Tt);
    }

    private static JulianInstant ToTdb(JulianInstant tt)
    {
        JulianInstant ttCoordinate = Relabel(tt, TimeScale.Tdb);
        JulianInstant estimate = ttCoordinate.AddNanoseconds(PeriodicCorrection(ttCoordinate));
        // Evaluate the correction on the TDB axis for both directions. The slope is < 4e-10.
        JulianInstant tdb = ttCoordinate.AddNanoseconds(PeriodicCorrection(estimate));
        ValidateTdb(tdb);
        return tdb;
    }

    private static long PeriodicCorrection(JulianInstant tdb)
    {
        double seconds = (tdb.Day - JulianInstant.J2000Day) * (double)JulianInstant.SecondsPerDay
                         + tdb.NanosecondsOfDay / (double)JulianInstant.NanosecondsPerSecond;
        double meanAnomaly = 6.239996 + 1.99096871e-7 * seconds;
        double correction = 1.657e-3 * Math.Sin(meanAnomaly + 1.671e-2 * Math.Sin(meanAnomaly));
        return (long)Math.Round(correction * JulianInstant.NanosecondsPerSecond, MidpointRounding.ToEven);
    }

    private static JulianInstant Relabel(JulianInstant instant, TimeScale scale) =>
        new(instant.Day, instant.NanosecondsOfDay, scale);

    private static void ValidateTdb(JulianInstant tdb)
    {
        if (tdb.CompareTo(TdbStart) < 0 || tdb.CompareTo(TdbEnd) >= 0)
            throw new ArgumentOutOfRangeException(nameof(tdb), "Periodic TDB conversion is supported on [1900, 2100).");
    }
}
