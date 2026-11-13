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
        const DeltaTModel model = DeltaTModel.MorrisonStephenson2004;
        var predictions = satellites.SelectMany(satellite => ObservationEventSearch.FindSatelliteFlares(ephemeris,
            satellite, site, model, start, end).Select(flare => (Satellite: satellite, Flare: flare))).ToArray();
        var flares = predictions.Select(item => item.Flare).ToArray();
        var brightest = predictions.MinBy(item => item.Flare.Magnitude);
        var later = brightest.Flare.Peak.AddSeconds(60);
        var laterState = brightest.Satellite.SampleInWorld(later, model)!.Value;
        var laterLighting = EarthSurfaceLighting.Sample(ephemeris.Sample(later), site, model);
        var laterFlare = ObservationEventSearch.EvaluateSatelliteFlare(laterLighting, laterState, later);
        Assert.True(laterFlare is null || laterFlare.Value.Magnitude > brightest.Flare.Magnitude + 4);

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
