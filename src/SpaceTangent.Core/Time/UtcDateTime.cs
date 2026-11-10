// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;

namespace SpaceTangent.Core.Time;

/// <summary>A Gregorian UTC label. The nanosecond grid includes a published positive leap second.</summary>
public readonly record struct UtcDateTime
{
    public UtcDateTime(DateOnly date, int hour = 0, int minute = 0, int second = 0, int nanosecond = 0)
    {
        int length = LeapSecondTable.DayLength(date);
        if (hour is < 0 or > 23)
            throw new ArgumentOutOfRangeException(nameof(hour));
        if (minute is < 0 or > 59)
            throw new ArgumentOutOfRangeException(nameof(minute));
        if (second is < 0 or > 60 || (second == 60 && (hour != 23 || minute != 59 || length != 86_401)))
            throw new ArgumentOutOfRangeException(nameof(second));
        if (nanosecond < 0 || nanosecond >= JulianInstant.NanosecondsPerSecond)
            throw new ArgumentOutOfRangeException(nameof(nanosecond));

        Date = date;
        Hour = hour;
        Minute = minute;
        Second = second;
        Nanosecond = nanosecond;
    }

    public DateOnly Date { get; }
    public int Hour { get; }
    public int Minute { get; }
    public int Second { get; }
    public int Nanosecond { get; }
    public bool IsPredicted => Date >= LeapSecondTable.FirstPredictedDate;

    internal long NanosecondsSinceMidnight =>
        (Hour * 3600L + Minute * 60L + Second) * JulianInstant.NanosecondsPerSecond + Nanosecond;

    public override string ToString() =>
        FormattableString.Invariant($"{Date:yyyy-MM-dd}T{Hour:D2}:{Minute:D2}:{Second:D2}.{Nanosecond:D9}Z");

    internal static UtcDateTime FromDayOffset(DateOnly date, long nanoseconds)
    {
        int second = (int)(nanoseconds / JulianInstant.NanosecondsPerSecond);
        int fraction = (int)(nanoseconds % JulianInstant.NanosecondsPerSecond);
        return second == 86_400
            ? new(date, 23, 59, 60, fraction)
            : new(date, second / 3600, second / 60 % 60, second % 60, fraction);
    }
}
