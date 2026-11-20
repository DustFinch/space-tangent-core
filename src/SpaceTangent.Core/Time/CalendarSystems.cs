// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;

namespace SpaceTangent.Core.Time;

public readonly record struct CalendarDateParts(long Year, int Month, int Day);
public readonly record struct IsoCalendarWeek(int Year, int Week, int DayOfYear);
public readonly record struct MayaCalendarDate(long Baktun, int Katun, int Tun, int Winal, int Kin,
    int TzolkinNumber, int TzolkinName, int HaabDay, int HaabMonth)
{
    public string LongCount => FormattableString.Invariant($"{Baktun}.{Katun}.{Tun}.{Winal}.{Kin}");
}

/// <summary>Civil-day conversions using Julian Day Numbers, not a particular time scale or sunset boundary.</summary>
/// <remarks>Julian: Meeus, chapter 7. Hebrew and tabular Islamic: Calendrical Calculations.
/// Maya uses the GMT correlation 584283; ISO weeks use the proleptic Gregorian label of the same day.</remarks>
public static class CalendarSystems
{
    private const long GregorianEpoch = 1_721_425;
    private const long HebrewEpoch = -1_373_427;

    public static CalendarDateParts Julian(long dayNumber)
    {
        Validate(dayNumber);
        long c = dayNumber + 32082, d = (4 * c + 3) / 1461;
        long e = c - 1461 * d / 4, m = (5 * e + 2) / 153;
        return new(d - 4800 + m / 10, (int)(m + 3 - 12 * (m / 10)), (int)(e - (153 * m + 2) / 5 + 1));
    }

    public static IsoCalendarWeek? Iso(long dayNumber)
    {
        Validate(dayNumber);
        long offset = dayNumber - GregorianEpoch - 1;
        if (offset < 0 || offset > DateOnly.MaxValue.DayNumber) return null;
        var date = DateOnly.FromDayNumber((int)offset).ToDateTime(TimeOnly.MinValue);
        return new(ISOWeek.GetYear(date), ISOWeek.GetWeekOfYear(date), date.DayOfYear);
    }

    /// <summary>Nisan is month 1, Tishri is month 7. Dates before Hebrew year 1 have no result.</summary>
    public static CalendarDateParts? Hebrew(long dayNumber)
    {
        Validate(dayNumber);
        long day = dayNumber - GregorianEpoch;
        if (day < HebrewNewYear(1)) return null;
        long year = (long)Math.Floor((day - HebrewEpoch) / (35975351d / 98496)) + 1;
        while (HebrewNewYear(year) > day) year--;
        while (HebrewNewYear(year + 1) <= day) year++;
        int month = day < HebrewFixed(year, 1, 1) ? 7 : 1;
        while (day > HebrewFixed(year, month, HebrewMonthDays(year, month)))
            month = month == (HebrewLeap(year) ? 13 : 12) ? 1 : month + 1;
        return new(year, month, (int)(day - HebrewFixed(year, month, 1) + 1));
    }

    /// <summary>Tabular civil Islamic calendar, epoch 16 July 622 Julian. Earlier years are proleptic.</summary>
    public static CalendarDateParts Islamic(long dayNumber)
    {
        Validate(dayNumber);
        const long epoch = 227015;
        long day = dayNumber - GregorianEpoch;
        static long Fixed(long y, int m, int d) => epoch - 1 + (y - 1) * 354 + FloorDivide(3 + 11 * y, 30) +
            29 * (m - 1) + m / 2 + d;
        long year = FloorDivide(30 * (day - epoch) + 10646, 10631);
        int month = (int)FloorDivide(11 * (day - Fixed(year, 1, 1)) + 330, 325);
        return new(year, month, (int)(day - Fixed(year, month, 1) + 1));
    }

    public static MayaCalendarDate Maya(long dayNumber)
    {
        Validate(dayNumber);
        long days = dayNumber - 584283;
        long baktun = FloorDivide(days, 144000), rest = Mod(days, 144000);
        int katun = (int)(rest / 7200); rest %= 7200;
        int tun = (int)(rest / 360); rest %= 360;
        int winal = (int)(rest / 20), kin = (int)(rest % 20);
        long haab = Mod(days + 348, 365);
        return new(baktun, katun, tun, winal, kin, (int)Mod(days + 3, 13) + 1,
            (int)Mod(days + 19, 20), (int)(haab % 20), (int)(haab / 20));
    }

    private static bool HebrewLeap(long year) => (7 * year + 1) % 19 < 7;
    private static long HebrewElapsed(long year)
    {
        long months = (235 * year - 234) / 19;
        long parts = 12084 + 13753 * months, days = 29 * months + parts / 25920;
        return 3 * (days + 1) % 7 < 3 ? days + 1 : days;
    }
    private static long HebrewNewYear(long year)
    {
        long previous = HebrewElapsed(year - 1), now = HebrewElapsed(year), next = HebrewElapsed(year + 1);
        return HebrewEpoch + now + (next - now == 356 ? 2 : now - previous == 382 ? 1 : 0);
    }
    private static int HebrewMonthDays(long year, int month)
    {
        long length = HebrewNewYear(year + 1) - HebrewNewYear(year);
        return month is 2 or 4 or 6 or 10 or 13 || month == 12 && !HebrewLeap(year) ||
            month == 8 && length % 10 != 5 || month == 9 && length % 10 == 3 ? 29 : 30;
    }
    private static long HebrewFixed(long year, int month, int day)
    {
        int last = HebrewLeap(year) ? 13 : 12;
        long result = HebrewNewYear(year) + day - 1;
        if (month < 7)
        {
            for (int m = 7; m <= last; m++) result += HebrewMonthDays(year, m);
            for (int m = 1; m < month; m++) result += HebrewMonthDays(year, m);
        }
        else for (int m = 7; m < month; m++) result += HebrewMonthDays(year, m);
        return result;
    }
    private static long FloorDivide(long value, long by) => value / by - (value % by < 0 ? 1 : 0);
    private static long Mod(long value, long by) => (value % by + by) % by;
    private static void Validate(long dayNumber)
    {
        if (dayNumber < 0 || dayNumber > new HistoricalDate(HistoricalDate.MaximumYear, 12, 31).DayNumber)
            throw new ArgumentOutOfRangeException(nameof(dayNumber));
    }
}
