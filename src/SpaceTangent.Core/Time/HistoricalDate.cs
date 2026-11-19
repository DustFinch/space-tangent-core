// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using System.Text.RegularExpressions;
using SpaceTangent.Contracts;

namespace SpaceTangent.Core.Time;

/// <summary>
/// A historical civil calendar date: Julian calendar before 1582 October 15 and Gregorian from
/// that day on. Years are astronomical, so year 0 is 1 BC. The day number is the Julian Day Number of the date, whose
/// noon is that Julian Date.
/// </summary>
/// <remarks>
/// .NET <see cref="DateOnly"/> and <see cref="DateTime"/> are proleptic Gregorian. They label 1054 July 4 (Julian) as
/// July 10, so they must not be used for dates before the reform.
/// </remarks>
public readonly partial record struct HistoricalDate
{
    /// <summary>The Julian Day Number of 1582 October 15, the first Gregorian date.</summary>
    public const long GregorianReformDayNumber = 2_299_161;

    /// <summary>The Julian Day Number of 0001 January 1 in the proleptic Gregorian calendar of <see cref="DateOnly"/>.</summary>
    private const long DateOnlyEpochDayNumber = 1_721_426;

    private const long HalfDay = JulianInstant.NanosecondsPerDay / 2;

    public const int MinimumYear = -4712, MaximumYear = 9999;

    public HistoricalDate(int year, int month, int day)
    {
        if (!IsValid(year, month, day))
            throw new ArgumentOutOfRangeException(nameof(day), "The date does not exist in the historical calendar.");
        Year = year;
        Month = month;
        Day = day;
    }

    public int Year { get; }
    public int Month { get; }
    public int Day { get; }

    /// <summary>True before 1582 October 15, when the date is in the Julian calendar.</summary>
    public bool IsJulian => !IsGregorian(Year, Month, Day);

    public long DayNumber => DayNumberOf(Year, Month, Day);

    public static bool IsValid(int year, int month, int day)
    {
        if (year is < MinimumYear or > MaximumYear || month is < 1 or > 12 || day < 1)
            return false;
        // The reform removed 1582 October 5 to 14.
        if (year == 1582 && month == 10 && day is > 4 and < 15)
            return false;
        bool leap = IsGregorian(year, month, day)
            ? year % 4 == 0 && (year % 100 != 0 || year % 400 == 0)
            : (year % 4 + 4) % 4 == 0;
        int length = month switch
        {
            2 => leap ? 29 : 28,
            4 or 6 or 9 or 11 => 30,
            _ => 31
        };
        return day <= length;
    }

    public static HistoricalDate FromDayNumber(long dayNumber)
    {
        // Richards' algorithm (Explanatory Supplement 2013, 15.11.3) with the Gregorian correction from the reform on.
        if (dayNumber < 0)
            throw new ArgumentOutOfRangeException(nameof(dayNumber));
        long c;
        long century = 0;
        if (dayNumber >= GregorianReformDayNumber)
        {
            long a = dayNumber + 32044;
            century = (4 * a + 3) / 146097;
            c = a - 146097 * century / 4;
        }
        else
            c = dayNumber + 32082;
        long d = (4 * c + 3) / 1461;
        long e = c - 1461 * d / 4;
        long m = (5 * e + 2) / 153;
        int day = (int)(e - (153 * m + 2) / 5 + 1);
        int month = (int)(m + 3 - 12 * (m / 10));
        int year = (int)(100 * century + d - 4800 + m / 10);
        return new(year, month, day);
    }

    public static HistoricalDate FromGregorian(DateOnly date) => FromDayNumber(date.DayNumber + DateOnlyEpochDayNumber);

    /// <summary>The proleptic Gregorian label of the same day, for APIs that require <see cref="DateOnly"/>.</summary>
    public DateOnly ToGregorian()
    {
        long offset = DayNumber - DateOnlyEpochDayNumber;
        if (offset < 0 || offset > DateOnly.MaxValue.DayNumber)
            throw new ArgumentOutOfRangeException(nameof(HistoricalDate), "The date lies outside DateOnly.");
        return DateOnly.FromDayNumber((int)offset);
    }

    /// <summary>The instant of the given time after midnight of this date, in the given time scale.</summary>
    public JulianInstant ToInstant(long nanosecondsSinceMidnight, TimeScale scale)
    {
        if (nanosecondsSinceMidnight is < 0 or >= JulianInstant.NanosecondsPerDay)
            throw new ArgumentOutOfRangeException(nameof(nanosecondsSinceMidnight));
        // Midnight of day number N is Julian Date N - 0.5.
        return new JulianInstant(0, 0, scale)
            .AddNanoseconds((Int128)DayNumber * JulianInstant.NanosecondsPerDay - HalfDay + nanosecondsSinceMidnight);
    }

    public static (HistoricalDate Date, long NanosecondsSinceMidnight) FromInstant(JulianInstant instant)
    {
        long dayNumber = instant.Day + (instant.NanosecondsOfDay >= HalfDay ? 1 : 0);
        long nanoseconds = (instant.NanosecondsOfDay + HalfDay) % JulianInstant.NanosecondsPerDay;
        return (FromDayNumber(dayNumber), nanoseconds);
    }

    /// <summary>Formats the date as an ISO 8601 calendar date with an astronomical year, such as -0043-03-15.</summary>
    public override string ToString() => Year < 0
        ? FormattableString.Invariant($"-{-Year:D4}-{Month:D2}-{Day:D2}")
        : FormattableString.Invariant($"{Year:D4}-{Month:D2}-{Day:D2}");

    /// <summary>Formats a date and time as yyyy-MM-ddTHH:mm:ss.fffffff, the form legacy scene links use.</summary>
    public static string Format(HistoricalDate date, long nanosecondsSinceMidnight)
    {
        long ticks = nanosecondsSinceMidnight / 100;
        long seconds = ticks / 10_000_000;
        return FormattableString.Invariant(
            $"{date}T{seconds / 3600:D2}:{seconds / 60 % 60:D2}:{seconds % 60:D2}.{ticks % 10_000_000:D7}");
    }

    /// <summary>
    /// Parses year-month-day with an optional time after 'T' or a space: HH:mm, HH:mm:ss or HH:mm:ss.fraction, and an
    /// optional trailing 'Z'. The year may be negative.
    /// </summary>
    public static bool TryParse(string text, out HistoricalDate date, out long nanosecondsSinceMidnight)
    {
        date = default;
        nanosecondsSinceMidnight = 0;
        Match match = IsoDate().Match(text.Trim());
        if (!match.Success ||
            !int.TryParse(match.Groups["year"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int year) ||
            !int.TryParse(match.Groups["month"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int month) ||
            !int.TryParse(match.Groups["day"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int day) ||
            !IsValid(year, month, day))
            return false;
        if (match.Groups["hour"].Success)
        {
            int hour = int.Parse(match.Groups["hour"].Value, CultureInfo.InvariantCulture);
            int minute = int.Parse(match.Groups["minute"].Value, CultureInfo.InvariantCulture);
            decimal seconds = match.Groups["second"].Success
                ? decimal.Parse(match.Groups["second"].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture)
                : 0;
            if (hour > 23 || minute > 59 || seconds >= 60)
                return false;
            nanosecondsSinceMidnight = (hour * 3600L + minute * 60L) * JulianInstant.NanosecondsPerSecond +
                (long)Math.Round(seconds * JulianInstant.NanosecondsPerSecond, MidpointRounding.ToEven);
            if (nanosecondsSinceMidnight >= JulianInstant.NanosecondsPerDay)
                return false;
        }
        date = new(year, month, day);
        return true;
    }

    private static bool IsGregorian(int year, int month, int day) =>
        year > 1582 || year == 1582 && (month > 10 || month == 10 && day >= 15);

    private static long DayNumberOf(int year, int month, int day)
    {
        // Fliegel and Van Flandern; the offset 4800 keeps the integer divisions non-negative from -4712 on.
        long a = (14 - month) / 12;
        long y = year + 4800L - a;
        long m = month + 12 * a - 3;
        long days = day + (153 * m + 2) / 5 + 365 * y + y / 4;
        return IsGregorian(year, month, day) ? days - y / 100 + y / 400 - 32045 : days - 32083;
    }

    [GeneratedRegex(@"^(?<year>-?\d{1,5})-(?<month>\d{1,2})-(?<day>\d{1,2})(?:[T ](?<hour>\d{1,2}):(?<minute>\d{2})(?::(?<second>\d{1,2}(?:\.\d{1,9})?))?)?Z?$", RegexOptions.CultureInvariant)]
    private static partial Regex IsoDate();
}
