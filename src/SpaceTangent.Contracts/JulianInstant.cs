// SPDX-License-Identifier: GPL-2.0-or-later
namespace SpaceTangent.Contracts;

public enum TimeScale { Tdb, Tai, Tt }

/// <summary>
/// A Julian date on a nanosecond grid in a uniform time scale. The integer day starts at noon.
/// </summary>
public readonly record struct JulianInstant : IComparable<JulianInstant>
{
    public const long NanosecondsPerSecond = 1_000_000_000;
    public const long SecondsPerDay = 86_400;
    public const long NanosecondsPerDay = SecondsPerDay * NanosecondsPerSecond;
    public const long MaximumDay = 5_373_484;
    public const long J2000Day = 2_451_545;

    public JulianInstant(long day, long nanosecondsOfDay, TimeScale scale)
    {
        if (day is < 0 or > MaximumDay)
            throw new ArgumentOutOfRangeException(nameof(day));
        if (nanosecondsOfDay is < 0 or >= NanosecondsPerDay)
            throw new ArgumentOutOfRangeException(nameof(nanosecondsOfDay));
        if (scale is < TimeScale.Tdb or > TimeScale.Tt)
            throw new ArgumentOutOfRangeException(nameof(scale));

        Day = day;
        NanosecondsOfDay = nanosecondsOfDay;
        Scale = scale;
    }

    public long Day { get; }
    public long NanosecondsOfDay { get; }
    public TimeScale Scale { get; }

    /// <summary>Converts a floating-point Julian date, rounding to the nearest nanosecond, ties to even.</summary>
    public static JulianInstant FromJulianDate(double date, TimeScale scale)
    {
        if (!double.IsFinite(date) || date < 0 || date >= MaximumDay + 1d)
            throw new ArgumentOutOfRangeException(nameof(date));

        long day = (long)date;
        long nanos = (long)Math.Round((date - day) * NanosecondsPerDay, MidpointRounding.ToEven);
        return new(day + nanos / NanosecondsPerDay, nanos % NanosecondsPerDay, scale);
    }

    /// <summary>Converts an ephemeris offset in TDB seconds from JD 2451545.0.</summary>
    public static JulianInstant FromTdbSeconds(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < -J2000Day * (double)SecondsPerDay ||
            seconds >= (MaximumDay + 1 - J2000Day) * (double)SecondsPerDay)
            throw new ArgumentOutOfRangeException(nameof(seconds));

        long whole = (long)seconds;
        long dayOffset = Math.DivRem(whole, SecondsPerDay, out long remainder);
        long fraction = (long)Math.Round((seconds - whole) * NanosecondsPerSecond, MidpointRounding.ToEven);
        long nanos = remainder * NanosecondsPerSecond + fraction;
        if (nanos < 0)
        {
            dayOffset--;
            nanos += NanosecondsPerDay;
        }
        else if (nanos >= NanosecondsPerDay)
        {
            dayOffset++;
            nanos -= NanosecondsPerDay;
        }
        return new(J2000Day + dayOffset, nanos, TimeScale.Tdb);
    }

    /// <summary>A lossy scalar projection for interoperability and display.</summary>
    public double ToJulianDate() => Day + NanosecondsOfDay / (double)NanosecondsPerDay;

    public JulianInstant AddNanoseconds(Int128 offset)
    {
        Int128 total = (Int128)Day * NanosecondsPerDay + NanosecondsOfDay;
        Int128 limit = (Int128)(MaximumDay + 1) * NanosecondsPerDay;
        if (offset < -total || offset >= limit - total)
            throw new ArgumentOutOfRangeException(nameof(offset));

        total += offset;
        return new((long)(total / NanosecondsPerDay), (long)(total % NanosecondsPerDay), Scale);
    }

    public JulianInstant AddSeconds(double seconds)
    {
        if (!double.IsFinite(seconds) || Math.Abs(seconds) > (MaximumDay + 1) * (double)SecondsPerDay)
            throw new ArgumentOutOfRangeException(nameof(seconds));

        return AddNanoseconds((Int128)Math.Round(seconds * NanosecondsPerSecond, MidpointRounding.ToEven));
    }

    public Int128 DifferenceNanoseconds(JulianInstant other)
    {
        if (Scale != other.Scale)
            throw new ArgumentException("Epochs must use the same time scale.", nameof(other));

        return (Int128)(Day - other.Day) * NanosecondsPerDay + NanosecondsOfDay - other.NanosecondsOfDay;
    }

    public int CompareTo(JulianInstant other) => DifferenceNanoseconds(other).CompareTo(Int128.Zero);
}
