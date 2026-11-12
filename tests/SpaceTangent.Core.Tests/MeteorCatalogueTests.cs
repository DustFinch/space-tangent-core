// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Time;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class MeteorCatalogueTests
{
    [MeteorCorpusFact]
    [Trait("Category", "ExternalData")]
    public void ArchivedShowersRetainThePerseidRadiantAndSeasonAndDateTheNextPeak()
    {
        using var reader = File.OpenText(Environment.GetEnvironmentVariable("SPACETANGENT_METEOR_CATALOGUE")!);
        var catalogue = MeteorCatalogue.Read(reader);
        Assert.Equal(43, catalogue.Showers.Count);
        var shower = Assert.Single(catalogue.Showers, item => item.Code == "PER");
        Assert.Equal(100, shower.ZenithRate(140));
        Assert.Equal(0, shower.ZenithRate(113));
        Assert.InRange(shower.ZenithRate(141), 63, 64);
        var radiant = shower.RadiantDirection(140);
        Assert.Equal(48, Math.Atan2(radiant.Y, radiant.X) * 180 / Math.PI, 9);
        Assert.Equal(58, Math.Asin(radiant.Z) * 180 / Math.PI, 9);
        var normids = Assert.Single(catalogue.Showers, item => item.Code == "GNO");
        Assert.True(normids.IsActive(0));
        Assert.False(normids.IsActive(9));
        var variable = Assert.Single(catalogue.Showers, item => item.Code == "JBO");
        Assert.True(variable.MaximumZhr > 0 && variable.ZenithRate(variable.PeakLongitude) == 0);

        var ephemeris = De440Ephemeris.Load(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")!);
        var from = TimeConversion.FromUtc(new(new(2026, 8, 9), 0), TimeScale.Tdb);
        var peak = Assert.IsType<MeteorPeak>(catalogue.NextPeak(ephemeris, from));
        Assert.Equal("PER", peak.Shower.Code);
        Assert.InRange(TimeConversion.ToUtc(peak.Epoch).Date, new(2026, 8, 12), new(2026, 8, 13));
        var frame = ephemeris.Sample(peak.Epoch);
        Assert.Equal(140, MeteorCatalogue.SolarLongitude(frame), 4);
        var site = new EarthSite(55.7558, 37.6173, .15);
        var light = EarthSurfaceLighting.Sample(frame, site, DeltaTModel.MorrisonStephenson2004);
        var found = Assert.Single(catalogue.Radiants(frame, light, 6.5), item => item.Shower.Code == "PER");
        Assert.InRange(found.ZenithRate, 99.99, 100);
        Assert.InRange(found.ObservedHourlyRate, 0, 100);
        Assert.True(MeteorCatalogue.ObservedRate(100, 65, 2.2, 6.5) > MeteorCatalogue.ObservedRate(100, 65, 2.2, 4));
        Assert.Equal(0, MeteorCatalogue.ObservedRate(100, -1, 2.2, 7));
        Assert.Null(catalogue.NextPeak(ephemeris, ephemeris.End));
    }

    private sealed class MeteorCorpusFactAttribute : FactAttribute
    {
        public MeteorCorpusFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_METEOR_CATALOGUE")) ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")))
                Skip = "Set the archived meteor catalogue and original DE440 ephemeris.";
        }
    }
}
