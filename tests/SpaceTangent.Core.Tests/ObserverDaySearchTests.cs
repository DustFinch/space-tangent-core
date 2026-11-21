// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class ObserverDaySearchTests
{
    private static readonly JulianInstant Start = JulianInstant.FromJulianDate(2451545, TimeScale.Tdb);

    [Fact]
    public void RefinesAnalyticCrossingsAndMaximum()
    {
        ObserverChartSample Sample(JulianInstant at)
        {
            double seconds = (double)at.DifferenceNanoseconds(Start) / JulianInstant.NanosecondsPerSecond;
            return new(at, 30 * Math.Sin((seconds - 6000) * Math.PI / 43200), 180, -18);
        }
        var day = ObserverDaySearch.Find(Sample, Start, Start.AddSeconds(86400));
        Assert.Equal(97, day.Samples.Count);
        Assert.True(day.CompleteCoverage);
        Assert.False(day.StartsAbove);
        Assert.InRange(Offset(day.Rise), 5999, 6001);
        Assert.InRange(Offset(day.Set), 49199, 49201);
        Assert.InRange(Offset(day.Maximum?.Epoch), 27599, 27601);
        Assert.InRange(day.Maximum!.Value.AltitudeDegrees!.Value, 29.999999, 30);
    }

    [Fact]
    public void CoverageGapDoesNotManufactureAContactAndCancellationIsObserved()
    {
        ObserverChartSample Sample(JulianInstant at)
        {
            double seconds = (double)at.DifferenceNanoseconds(Start) / JulianInstant.NanosecondsPerSecond;
            return new(at, seconds is >= 500 and <= 1000 ? double.NaN : seconds - 750, null, null);
        }
        var day = ObserverDaySearch.Find(Sample, Start, Start.AddSeconds(1800));
        Assert.False(day.CompleteCoverage);
        Assert.Null(day.Rise);
        Assert.Contains(day.Samples, row => row.AltitudeDegrees is null);
        Assert.Throws<OperationCanceledException>(() => ObserverDaySearch.Find(Sample, Start, Start.AddSeconds(1800), cancellation: new(true)));
        Assert.Throws<ArgumentException>(() => ObserverDaySearch.Find(Sample, Start, Start));
    }

    [Fact]
    public void RetainsEveryCrossingAcrossSeveralRotationsInOneWindow()
    {
        var day = ObserverDaySearch.Find(at => new(at,
            20 * Math.Sin((Offset(at) - 900) * Math.Tau / 21600), null, null), Start, Start.AddSeconds(86400));
        Assert.False(day.StartsAbove);
        Assert.True(day.CompleteCoverage);
        Assert.Equal(8, day.Crossings.Count);
        for (int i = 0; i < day.Crossings.Count; i++)
        {
            Assert.Equal(i % 2 == 0, day.Crossings[i].Rising);
            Assert.InRange(Math.Abs(Offset(day.Crossings[i].Epoch) - (900 + i * 10800)), 0, 1);
        }
        Assert.Equal(day.Crossings[0].Epoch, day.Rise);
        Assert.Equal(day.Crossings[1].Epoch, day.Set);
    }

    private static double Offset(JulianInstant? at) => at is { } epoch
        ? (double)epoch.DifferenceNanoseconds(Start) / JulianInstant.NanosecondsPerSecond : double.NaN;
}
