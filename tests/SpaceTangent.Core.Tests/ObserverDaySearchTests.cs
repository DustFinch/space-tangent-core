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

    private static double Offset(JulianInstant? at) => at is { } epoch
        ? (double)epoch.DifferenceNanoseconds(Start) / JulianInstant.NanosecondsPerSecond : double.NaN;
}
