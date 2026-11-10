// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Time;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class TimeConversionTests
{
    [Fact]
    public void IntegerArithmeticRetainsNanosecondsAcrossNoon()
    {
        var epoch = new JulianInstant(2_451_544, 86_399_999_999_999, TimeScale.Tdb);
        var noon = new JulianInstant(2_451_545, 0, TimeScale.Tdb);

        Assert.Equal(epoch.ToJulianDate(), noon.ToJulianDate());
        Assert.Equal(noon, epoch.AddNanoseconds(1));
        Assert.Equal(epoch, noon.AddNanoseconds(-1));
        Assert.Equal((Int128)1, noon.DifferenceNanoseconds(epoch));
        Assert.True(epoch.CompareTo(noon) < 0);
    }

    [Fact]
    public void RejectsInvalidScaleFractionAndOutOfRangeArithmetic()
    {
        var first = new JulianInstant(0, 0, TimeScale.Tdb);
        var last = new JulianInstant(5_373_484, 86_399_999_999_999, TimeScale.Tdb);
        var tt = new JulianInstant(0, 0, TimeScale.Tt);

        Assert.Throws<ArgumentException>(() => first.DifferenceNanoseconds(tt));
        Assert.Throws<ArgumentOutOfRangeException>(() => first.AddNanoseconds(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => last.AddNanoseconds(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => last.AddNanoseconds(Int128.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => first.AddNanoseconds(Int128.MinValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => new JulianInstant(0, 86_400_000_000_000, TimeScale.Tdb));
        Assert.Throws<ArgumentOutOfRangeException>(() => TimeConversion.Convert(tt, (TimeScale)42));
    }

    [Fact]
    public void FractionalSecondsRoundToEvenNanoseconds()
    {
        var epoch = new JulianInstant(2_451_545, 0, TimeScale.Tai);
        Assert.Equal(0, epoch.AddSeconds(0.5e-9).NanosecondsOfDay);
        Assert.Equal(2, epoch.AddSeconds(1.5e-9).NanosecondsOfDay);
        Assert.Equal(2, epoch.AddSeconds(2.5e-9).NanosecondsOfDay);
    }

    [Theory]
    [InlineData(1972, 1, 1, 0, 0, 0, 0, 2_441_317, 43_210_000_000_000)]
    [InlineData(1972, 1, 1, 12, 34, 56, 123456789, 2_441_318, 2_106_123_456_789)]
    [InlineData(2016, 12, 31, 23, 59, 59, 0, 2_457_754, 43_235_000_000_000)]
    [InlineData(2016, 12, 31, 23, 59, 60, 0, 2_457_754, 43_236_000_000_000)]
    [InlineData(2016, 12, 31, 23, 59, 60, 999999999, 2_457_754, 43_236_999_999_999)]
    [InlineData(2017, 1, 1, 0, 0, 0, 0, 2_457_754, 43_237_000_000_000)]
    public void UtcMatchesIndependentTaiCoordinates(int year, int month, int day, int hour, int minute,
        int second, int nanosecond, long julianDay, long fraction)
    {
        // ERFA two-part UTC-to-TAI references and IERS Bulletin C 52 for the 2016 transition.
        var utc = new UtcDateTime(new DateOnly(year, month, day), hour, minute, second, nanosecond);
        var tai = new JulianInstant(julianDay, fraction, TimeScale.Tai);

        Assert.Equal(tai, TimeConversion.FromUtc(utc));
        Assert.Equal(utc, TimeConversion.ToUtc(tai));
    }

    [Fact]
    public void LeapSecondIsAFullSiSecondAndHasItsOwnLabel()
    {
        var leap = new UtcDateTime(new DateOnly(2016, 12, 31), 23, 59, 60);
        var midnight = new UtcDateTime(new DateOnly(2017, 1, 1));
        var t1 = TimeConversion.FromUtc(leap, TimeScale.Tt);
        var t2 = TimeConversion.FromUtc(midnight, TimeScale.Tt);

        Assert.Equal((Int128)1_000_000_000, t2.DifferenceNanoseconds(t1));
        Assert.Equal("2016-12-31T23:59:60.000000000Z", leap.ToString());
        Assert.Throws<ArgumentOutOfRangeException>(() => new UtcDateTime(midnight.Date, 23, 59, 60));
        Assert.Throws<ArgumentOutOfRangeException>(() => TimeConversion.FromUtc(default));
        Assert.False(new UtcDateTime(new DateOnly(2026, 12, 31)).IsPredicted);
        Assert.True(new UtcDateTime(new DateOnly(2027, 1, 1)).IsPredicted);
    }

    [Theory]
    [InlineData(2_415_021, 0, 15_833)]
    [InlineData(2_441_317, 43_200_000_000_000, 79_394)]
    [InlineData(2_451_545, 0, 72_737)]
    [InlineData(2_457_754, 43_200_000_000_000, 70_245)]
    [InlineData(2_460_310, 43_200_000_000_000, 93_907)]
    [InlineData(2_488_068, 43_200_000_000_000, 143_993)]
    public void TdbPeriodicCorrectionMatchesReferenceAndRoundTrips(long day, long fraction, long ttMinusTdb)
    {
        var tdb = new JulianInstant(day, fraction, TimeScale.Tdb);
        var tt = TimeConversion.Convert(tdb, TimeScale.Tt);
        var tai = TimeConversion.Convert(tdb, TimeScale.Tai);
        var expected = new JulianInstant(day, fraction, TimeScale.Tt).AddNanoseconds(ttMinusTdb);

        Assert.Equal(expected, tt);
        Assert.Equal(tt.AddNanoseconds(-32_184_000_000).NanosecondsOfDay, tai.NanosecondsOfDay);
        Assert.Equal(tdb, TimeConversion.Convert(tai, TimeScale.Tdb));
    }

    [Fact]
    public void TdbCoverageUsesTheExactTdbAxisAtBothBoundaries()
    {
        var start = new JulianInstant(2_415_020, 43_200_000_000_000, TimeScale.Tdb);
        var end = new JulianInstant(2_488_069, 43_200_000_000_000, TimeScale.Tdb);

        foreach (var tdb in new[] { start, end.AddNanoseconds(-1) })
            Assert.Equal(tdb, TimeConversion.Convert(TimeConversion.Convert(tdb, TimeScale.Tt), TimeScale.Tdb));

        Assert.Throws<ArgumentOutOfRangeException>(() => TimeConversion.Convert(start.AddNanoseconds(-1), TimeScale.Tt));
        Assert.Throws<ArgumentOutOfRangeException>(() => TimeConversion.Convert(end, TimeScale.Tt));
    }
}
