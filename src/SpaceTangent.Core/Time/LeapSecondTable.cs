// SPDX-License-Identifier: GPL-2.0-or-later
namespace SpaceTangent.Core.Time;

// IERS Leap_Second.dat and Bulletin C 72. Entries become effective at 00:00 UTC.
internal static class LeapSecondTable
{
    internal static readonly DateOnly FirstDate = new(1972, 1, 1);
    internal static readonly DateOnly FirstPredictedDate = new(2027, 1, 1);

    private static readonly DateOnly[] EffectiveDates =
    [
        FirstDate, new(1972, 7, 1), new(1973, 1, 1), new(1974, 1, 1),
        new(1975, 1, 1), new(1976, 1, 1), new(1977, 1, 1), new(1978, 1, 1),
        new(1979, 1, 1), new(1980, 1, 1), new(1981, 7, 1), new(1982, 7, 1),
        new(1983, 7, 1), new(1985, 7, 1), new(1988, 1, 1), new(1990, 1, 1),
        new(1991, 1, 1), new(1992, 7, 1), new(1993, 7, 1), new(1994, 7, 1),
        new(1996, 1, 1), new(1997, 7, 1), new(1999, 1, 1), new(2006, 1, 1),
        new(2009, 1, 1), new(2012, 7, 1), new(2015, 7, 1), new(2017, 1, 1)
    ];

    internal static int TaiMinusUtc(DateOnly date)
    {
        if (date < FirstDate)
            throw new ArgumentOutOfRangeException(nameof(date), "UTC conversion is supported from 1972.");

        int index = Array.BinarySearch(EffectiveDates, date);
        return 10 + (index >= 0 ? index : ~index - 1);
    }

    internal static int DayLength(DateOnly date)
    {
        int current = TaiMinusUtc(date);
        return date >= FirstPredictedDate ? 86_400 : 86_400 + TaiMinusUtc(date.AddDays(1)) - current;
    }
}
