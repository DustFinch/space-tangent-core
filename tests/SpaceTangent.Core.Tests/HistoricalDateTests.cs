// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Time;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class HistoricalDateTests
{
    // Julian Day Numbers from Meeus, Astronomical Algorithms, chapter 7, and the reform itself.
    [Theory]
    [InlineData(2000, 1, 1, 2_451_545)]
    [InlineData(1987, 6, 19, 2_446_966)]
    [InlineData(1600, 1, 1, 2_305_448)]
    [InlineData(1582, 10, 15, 2_299_161)]
    [InlineData(1582, 10, 4, 2_299_160)]
    [InlineData(1054, 7, 4, 2_106_216)]
    [InlineData(333, 1, 27, 1_842_713)]
    [InlineData(-1000, 7, 12, 1_356_001)]
    [InlineData(-4712, 1, 1, 0)]
    public void DayNumbersFollowTheJulianCalendarBeforeTheReform(int year, int month, int day, long dayNumber)
    {
        var date = new HistoricalDate(year, month, day);
        Assert.Equal(dayNumber, date.DayNumber);
        Assert.Equal(date, HistoricalDate.FromDayNumber(dayNumber));
        Assert.Equal(dayNumber < HistoricalDate.GregorianReformDayNumber, date.IsJulian);
    }

    [Fact]
    public void EveryDayNumberRoundTrips()
    {
        for (long dayNumber = 0; dayNumber < 5_373_484; dayNumber += 997)
            Assert.Equal(dayNumber, HistoricalDate.FromDayNumber(dayNumber).DayNumber);
    }

    [Fact]
    public void TheCalendarsDifferOnlyWhereTheyShould()
    {
        Assert.False(HistoricalDate.IsValid(1582, 10, 10));
        Assert.True(HistoricalDate.IsValid(1500, 2, 29));
        Assert.False(HistoricalDate.IsValid(1700, 2, 29));
        Assert.True(HistoricalDate.IsValid(2000, 2, 29));
        Assert.True(HistoricalDate.IsValid(0, 2, 29));
        // The Crab supernova was seen on 1054 July 4 (Julian), which .NET labels July 10.
        Assert.Equal(new DateOnly(1054, 7, 10), new HistoricalDate(1054, 7, 4).ToGregorian());
        Assert.Equal(new HistoricalDate(1054, 7, 4), HistoricalDate.FromGregorian(new DateOnly(1054, 7, 10)));
    }

    [Fact]
    public void InstantsKeepTheirTimeOfDay()
    {
        Assert.True(HistoricalDate.TryParse("-0043-03-15T12:30:00.5", out HistoricalDate date, out long time));
        Assert.Equal(new HistoricalDate(-43, 3, 15), date);
        Assert.Equal(45_000_500_000_000, time);
        JulianInstant instant = date.ToInstant(time, TimeScale.Tdb);
        Assert.Equal((date, time), HistoricalDate.FromInstant(instant));
        Assert.Equal("-0043-03-15T12:30:00.5000000", HistoricalDate.Format(date, time));

        var j2000 = new HistoricalDate(2000, 1, 1).ToInstant(JulianInstant.NanosecondsPerDay / 2, TimeScale.Tt);
        Assert.Equal(new JulianInstant(JulianInstant.J2000Day, 0, TimeScale.Tt), j2000);
        Assert.False(HistoricalDate.TryParse("1582-10-10", out _, out _));
        Assert.False(HistoricalDate.TryParse("2000-01-01T24:00", out _, out _));
    }

    [Fact]
    public void ModeledUniversalTimeRoundTrips()
    {
        var date = new HistoricalDate(1054, 7, 4);
        const long time = 3 * 3600 * JulianInstant.NanosecondsPerSecond;
        JulianInstant tdb = EarthCivilTime.FromModeledUt1(date, time, DeltaTModel.MorrisonStephenson2004);
        var modeled = EarthCivilTime.ModeledHistoricalUt1(tdb, DeltaTModel.MorrisonStephenson2004)!.Value;
        Assert.Equal(date, modeled.Date);
        Assert.InRange(Math.Abs((modeled.Time - new TimeOnly(3, 0)).TotalSeconds), 0, .01);
    }
}
