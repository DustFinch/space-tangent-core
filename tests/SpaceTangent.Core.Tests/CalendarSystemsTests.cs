// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using SpaceTangent.Core.Time;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class CalendarSystemsTests
{
    [Fact]
    public void ModernDatesAgreeWithIndependentFrameworkCalendars()
    {
        var hebrew = new HebrewCalendar();
        // The framework starts on Thursday (622-07-18 Gregorian); the civil epoch is Friday, one day later.
        var islamic = new HijriCalendar { HijriAdjustment = -1 };
        for (var date = new DateTime(1900, 1, 1); date < new DateTime(2200, 1, 1); date = date.AddDays(17))
        {
            long day = HistoricalDate.FromGregorian(DateOnly.FromDateTime(date)).DayNumber;
            int year = hebrew.GetYear(date), month = hebrew.GetMonth(date);
            int nisan = hebrew.IsLeapYear(year) ? 8 : 7;
            int expectedMonth = month >= nisan ? month - nisan + 1 : month + 6;
            Assert.Equal(new CalendarDateParts(year, expectedMonth, hebrew.GetDayOfMonth(date)), CalendarSystems.Hebrew(day));
            Assert.Equal(new CalendarDateParts(islamic.GetYear(date), islamic.GetMonth(date), islamic.GetDayOfMonth(date)), CalendarSystems.Islamic(day));
        }
    }

    [Fact]
    public void PublishedEpochsAndCalendarReformKeepTheSameCivilDay()
    {
        Assert.Equal(new DateTime(622, 7, 18), new HijriCalendar().MinSupportedDateTime);
        Assert.Equal(1_948_440, HistoricalDate.FromGregorian(new DateOnly(622, 7, 19)).DayNumber);
        Assert.Equal(new CalendarDateParts(622, 7, 16), CalendarSystems.Julian(1_948_440));
        Assert.Equal(new CalendarDateParts(1, 1, 1), CalendarSystems.Islamic(1_948_440));
        Assert.Equal(new CalendarDateParts(1582, 10, 5), CalendarSystems.Julian(2_299_161));
        Assert.Equal(new CalendarDateParts(1999, 12, 19), CalendarSystems.Julian(2_451_545));
        Assert.Equal(new CalendarDateParts(5760, 10, 23), CalendarSystems.Hebrew(2_451_545));
        var maya = CalendarSystems.Maya(2_456_283); // 2012-12-21, GMT correlation.
        Assert.Equal("13.0.0.0.0", maya.LongCount);
        Assert.Equal(4, maya.TzolkinNumber);
        Assert.Equal(19, maya.TzolkinName); // Ahau, zero based.
        Assert.Equal(3, maya.HaabDay);
        Assert.Equal(13, maya.HaabMonth); // Kankin, zero based.
        Assert.Equal("0.0.0.0.0", CalendarSystems.Maya(584283).LongCount);
    }

    [Fact]
    public void IsoWeekYearCanDifferFromTheDateYear()
    {
        Assert.Equal(new IsoCalendarWeek(2020, 53, 1), CalendarSystems.Iso(new HistoricalDate(2021, 1, 1).DayNumber));
        Assert.Equal(new IsoCalendarWeek(2020, 1, 364), CalendarSystems.Iso(new HistoricalDate(2019, 12, 30).DayNumber));
    }

    [Fact]
    public void EarlyAndInvalidDaysHaveBoundedExplicitResults()
    {
        Assert.Null(CalendarSystems.Iso(0));
        Assert.Null(CalendarSystems.Hebrew(0));
        Assert.Equal(new CalendarDateParts(-4712, 1, 1), CalendarSystems.Julian(0));
        Assert.InRange(CalendarSystems.Maya(0).Katun, 0, 19);
        Assert.Throws<ArgumentOutOfRangeException>(() => CalendarSystems.Maya(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => CalendarSystems.Islamic(long.MaxValue));
    }
}
