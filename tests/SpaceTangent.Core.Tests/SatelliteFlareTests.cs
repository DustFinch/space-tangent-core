// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Time;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class SatelliteFlareTests
{
    [ArchivedIridiumFact]
    [Trait("Category", "ExternalData")]
    public void FindsBrightHistoricalReflectionsOverMoscow()
    {
        var ephemeris = De440Ephemeris.Load(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")!);
        var satellites = LocalTleCatalogue.Load(Environment.GetEnvironmentVariable("SPACETANGENT_IRIDIUM_ROOT")!);
        var start = JulianInstant.FromJulianDate(2457547.0 + 68.184 / 86400, TimeScale.Tdb);
        var end = start.AddSeconds(7 * JulianInstant.SecondsPerDay);
        var site = new EarthSite(55.7558, 37.6173, .15);
        var flares = satellites.SelectMany(satellite => ObservationEventSearch.FindSatelliteFlares(ephemeris,
            satellite, site, DeltaTModel.MorrisonStephenson2004, start, end)).ToArray();

        Assert.InRange(flares.Length, 15, 60);
        Assert.Contains(flares, flare => flare.Magnitude < -5);
        Assert.Contains(flares, flare => flare.SunAltitudeDegrees < -6);
        Assert.All(flares, flare =>
        {
            Assert.InRange(flare.Horizontal.AltitudeDegrees, 0, 90);
            Assert.InRange(flare.ReflectionAngleDegrees, 0, 3);
            Assert.True(flare.Peak.CompareTo(start) >= 0 && flare.Peak.CompareTo(end) <= 0);
        });
    }

    private sealed class ArchivedIridiumFactAttribute : FactAttribute
    {
        public ArchivedIridiumFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")) ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_IRIDIUM_ROOT")))
                Skip = "Set the original June 2016 TLE archive directory and the DE440 ephemeris.";
        }
    }
}
