// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Time;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class HistoricalTwilightTests
{
    [Theory]
    [InlineData(1054)]
    [InlineData(3000)]
    public void HistoricalCalendarAndGeodeticRiseSetMatchIndependentErfaIntegration(int year)
    {
        string row = File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "historical-twilight-reference.csv"))
            .Single(line => line.StartsWith(year + ",", StringComparison.Ordinal));
        var expected = row.Split(',').Skip(1).Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        var sky = EarthObserving.Twilight(new ConstantSun(), new(year, 5, 1), new(55.7558, 37.6173, .15), DeltaTModel.FixedOffset);
        JulianInstant?[] crossings = [sky.Horizon.Rising, sky.Horizon.Setting, sky.CivilLimit.Rising, sky.CivilLimit.Setting];
        for (int i = 0; i < crossings.Length; i++)
        {
            Assert.NotNull(crossings[i]);
            var tt = TimeConversion.Convert(crossings[i]!.Value, TimeScale.Tt);
            double seconds = (double)tt.DifferenceNanoseconds(new JulianInstant(2451545, 0, TimeScale.Tt)) / JulianInstant.NanosecondsPerSecond;
            Assert.InRange(seconds - expected[i], -2, 2); // One-second event refinement, full versus truncated CIO locator.
        }
    }

    private sealed class ConstantSun : ISolarSystemEphemeris
    {
        public JulianInstant Start => new(1, 0, TimeScale.Tdb);
        public JulianInstant End => new(4000000, 0, TimeScale.Tdb);
        public SolarSystemSample Sample(JulianInstant epoch) => new(epoch,
            new(new(1e8, 4e7, 2e7), default), default, new(new(384400, 0, 0), default), default);
    }
}
