// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Time;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class HistoricalObserverTests
{
    [HistoricalObserverFact]
    [Trait("Category", "ExternalData")]
    public void HistoricalGroundViewsUseModeledCivilTimeAndCanFindSunrise()
    {
        var de440 = De440Ephemeris.Load(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")!);
        var ephemeris = HistoricalSolarEphemeris.Load(de440, Environment.GetEnvironmentVariable("SPACETANGENT_VSOP87")!);
        var epoch = new JulianInstant(2106215, 0, TimeScale.Tdb);
        var site = new EarthSite(51.4779, 0, .046);
        const DeltaTModel model = DeltaTModel.MorrisonStephenson2004;
        var sky = EarthSurfaceLighting.Sample(ephemeris.Sample(epoch), site, model);
        Assert.InRange(sky.SolarAltitudeRadians * 180 / Math.PI, 45, 75);
        var civil = Assert.IsType<(DateOnly Date, TimeOnly Time)>(EarthCivilTime.ModeledUt1(epoch, model));
        Assert.Equal(new DateOnly(1054, 7, 9), civil.Date);
        Assert.InRange(civil.Time, new TimeOnly(11, 20), new TimeOnly(11, 40));
        Assert.Equal(civil.Date, EarthObserving.LocalMeanDate(epoch, site, model));
        Assert.Throws<ArgumentOutOfRangeException>(() => TimeConversion.ToUtc(epoch));
        Assert.Throws<ArgumentOutOfRangeException>(() => TimeConversion.Convert(epoch, TimeScale.Tt));

        var twilight = EarthObserving.Twilight(ephemeris, civil.Date, site, model);
        var rising = Assert.IsType<JulianInstant>(twilight.Horizon.Rising);
        var setting = Assert.IsType<JulianInstant>(twilight.Horizon.Setting);
        Assert.Equal(TimeScale.Tdb, rising.Scale);
        Assert.Equal(TimeScale.Tdb, setting.Scale);
        Assert.InRange((double)setting.DifferenceNanoseconds(rising) / JulianInstant.NanosecondsPerSecond,
            13 * 3600, 18 * 3600);
        Assert.InRange(EarthSurfaceLighting.Sample(ephemeris.Sample(rising), site, model).SolarAltitudeRadians * 180 / Math.PI,
            -.85, -.81);
        Assert.Equal(civil.Date, EarthObserving.LocalMeanDate(setting, site, model));
        Assert.Null(EarthCivilTime.ModeledUt1(new(0, 0, TimeScale.Tdb), model));
    }

    private sealed class HistoricalObserverFactAttribute : FactAttribute
    {
        public HistoricalObserverFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")) ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_VSOP87")))
                Skip = "Set the pinned DE440 and historical series data.";
        }
    }
}
