// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Time;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class DeltaTTests
{
    [Theory]
    [InlineData(1972, 1, 1, 42.184)]
    [InlineData(2027, 1, 1, 69.184)]
    public void BothHistoricalModelsMeetTheAtomicOffsetWithoutAStep(int year, int month, int day, double seconds)
    {
        JulianInstant edge = TimeConversion.FromUtc(new UtcDateTime(new DateOnly(year, month, day)), TimeScale.Tt);
        foreach (var model in new[] { DeltaTModel.MorrisonStephenson2004, DeltaTModel.EspenakMeeus2006 })
        {
            Assert.Equal(seconds, DeltaT.Seconds(edge, model), 9);
            Assert.Equal(seconds, DeltaT.Seconds(edge.AddSeconds(-0.01), model), 7);
            Assert.Equal(seconds, DeltaT.Seconds(edge.AddSeconds(0.01), model), 7);
        }
    }

    [Fact]
    public void EarthRotationUsesAContinuousLeapTransitionWithoutChangingUtc()
    {
        var leap = new UtcDateTime(new DateOnly(2016, 12, 31), 23, 59, 60);
        JulianInstant center = TimeConversion.FromUtc(new UtcDateTime(new DateOnly(2017, 1, 1)), TimeScale.Tt);
        var model = DeltaTModel.MorrisonStephenson2004;

        Assert.Equal(68.184, DeltaT.Seconds(center.AddSeconds(-31 * 86_400), model), 9);
        Assert.Equal(68.684, DeltaT.Seconds(center, model), 9);
        Assert.Equal(69.184, DeltaT.Seconds(center.AddSeconds(31 * 86_400), model), 9);
        Assert.InRange(DeltaT.Seconds(center.AddSeconds(1), model) - DeltaT.Seconds(center.AddSeconds(-1), model), 0, 1e-6);
        Assert.Equal(leap, TimeConversion.ToUtc(center.AddSeconds(-1)));
    }

    [Fact]
    public void SelectedModelChangesTheExtrapolationOutsideTableCoverage()
    {
        var tt = new JulianInstant(2_451_545 + 100 * 365 + 25, 0, TimeScale.Tt);
        Assert.Equal(69.184, DeltaT.Seconds(tt, DeltaTModel.FixedOffset), 12);
        Assert.InRange(DeltaT.Seconds(tt, DeltaTModel.MorrisonStephenson2004), 182, 184);
        Assert.InRange(DeltaT.Seconds(tt, DeltaTModel.EspenakMeeus2006), 196, 197);
        Assert.Throws<ArgumentException>(() => DeltaT.Seconds(default, DeltaTModel.FixedOffset));
        Assert.Throws<ArgumentOutOfRangeException>(() => DeltaT.Seconds(tt, (DeltaTModel)42));
    }
}
