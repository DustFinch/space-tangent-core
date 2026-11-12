// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Time;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class ObservationEventSearchTests
{
    private const DeltaTModel DeltaT = DeltaTModel.MorrisonStephenson2004;

    [EventEphemerisFact]
    [Trait("Category", "ExternalData")]
    public void April2024SolarAndMarch2025LunarEclipsesMatchPublishedCircumstances()
    {
        var ephemeris = Ephemeris();
        // NASA/GSFC central circumstances for the April 8 event.
        var site = new EarthSite(25 + 17.4 / 60, -(104 + 7.7 / 60), 0);
        var solarDate = new DateOnly(2024, 4, 8);
        var solar = Assert.Single(ObservationEventSearch.FindSolarEclipses(ephemeris, site, DeltaT,
            Utc(solarDate, 16), Utc(solarDate, 21)));
        Assert.Equal(EclipseClass.Total, solar.Class);
        Assert.InRange(Seconds(solar.Partial.Peak, Utc(solarDate, 18, 17, 15)), -100, 100);
        Assert.InRange(solar.SolarAltitudeDegrees, 68, 72);
        Assert.InRange(solar.Obscuration, .99999, 1);
        var central = Assert.IsType<ObservationInterval>(solar.Central);
        Assert.InRange(Seconds(central.Last!.Value, central.First!.Value), 175, 300);
        Assert.True(solar.Partial.First!.Value.CompareTo(central.First.Value) < 0);
        Assert.True(solar.Partial.Last!.Value.CompareTo(central.Last.Value) > 0);

        var clipped = Assert.Single(ObservationEventSearch.FindSolarEclipses(ephemeris, site, DeltaT,
            solar.Partial.Peak.AddSeconds(-480), solar.Partial.Peak.AddSeconds(120)));
        Assert.Null(clipped.Partial.First);
        Assert.Null(clipped.Partial.Last);
        Assert.InRange(Seconds(clipped.Partial.Peak, solar.Partial.Peak), -1, 1);

        var lunarDate = new DateOnly(2025, 3, 14);
        var lunar = Assert.Single(ObservationEventSearch.FindLunarEclipses(ephemeris, Utc(lunarDate, 0), Utc(lunarDate, 12)));
        Assert.Equal(EclipseClass.Total, lunar.Class);
        Assert.InRange(Seconds(lunar.Penumbral.Peak, Utc(lunarDate, 6, 58, 42)), -90, 90);
        Assert.InRange(Seconds(lunar.Penumbral.First!.Value, Utc(lunarDate, 3, 57, 24)), -180, 180);
        Assert.InRange(Seconds(lunar.Umbral!.Value.First!.Value, Utc(lunarDate, 5, 9, 33)), -240, 240);
        Assert.InRange(Seconds(lunar.Total!.Value.Last!.Value, Utc(lunarDate, 7, 31, 23)), -240, 240);
        Assert.InRange(lunar.UmbralMagnitude, 1.10, 1.21);
        Assert.Empty(ObservationEventSearch.FindLunarEclipses(ephemeris, Utc(lunarDate.AddDays(1), 0), Utc(lunarDate.AddDays(2), 0)));
    }

    [EventEphemerisFact]
    [Trait("Category", "ExternalData")]
    public void GreatConjunctionMatchesTheJplAngularReference()
    {
        var ephemeris = Ephemeris();
        var date = new DateOnly(2020, 12, 21);
        var closest = Assert.Single(ObservationEventSearch.FindConjunctions(epoch =>
        {
            var earth = ephemeris.Sample(epoch).Earth.PositionKilometers;
            return (Direction(SolarSystemSample.JupiterBarycenterId), Direction(SolarSystemSample.SaturnBarycenterId));
            Vector3d Direction(ObjectId body)
            {
                var relative = ephemeris.Sample(epoch).RelativeState(body, SolarSystemSample.EarthId).PositionKilometers;
                var earlier = epoch.AddSeconds(-relative.Length / 299_792.458);
                return ephemeris.Sample(earlier).RelativeState(body, SolarSystemSample.BarycenterId).PositionKilometers - earth;
            }
        }, Utc(date, 14), Utc(date, 22)));
        Assert.InRange(Seconds(closest.Peak, Utc(date, 18, 10)), -1200, 1200);
        Assert.InRange(closest.SeparationDegrees, .1014, .1021);
    }

    [SatelliteEventFact]
    [Trait("Category", "ExternalData")]
    public void ArchivedIssPassesMatchAnIndependentOneSecondGroundScan()
    {
        var catalogue = LocalTleCatalogue.Load(Environment.GetEnvironmentVariable("SPACETANGENT_TLE_ROOT")!);
        var iss = Assert.Single(catalogue, satellite => satellite.Id == new ObjectId("norad/25544"));
        var site = new EarthSite(55.7558, 37.6173, .15);
        var from = Utc(new(2026, 10, 27), 0);
        var to = Utc(new(2026, 10, 28), 0);
        var ephemeris = Ephemeris();
        var passes = ObservationEventSearch.FindSatellitePasses(ephemeris, epoch => iss.SampleInWorld(epoch, DeltaT),
            site, DeltaT, from, to, 10);
        (int First, int Peak, int Last, double Altitude)[] expected = [
            (5315, 5503, 5691, 38.96021), (11114, 11286, 11460, 27.37695),
            (17032, 17048, 17064, 10.07113), (83129, 83279, 83431, 20.13281)
        ];
        Assert.Equal(expected.Length, passes.Count);
        for (int index = 0; index < expected.Length; index++)
        {
            var pass = passes[index];
            var oracle = expected[index];
            Assert.InRange(Seconds(pass.AboveHorizon.First!.Value, from) - oracle.First, -1.2, 1.2);
            Assert.InRange(Seconds(pass.AboveHorizon.Peak, from) - oracle.Peak, -1.2, 1.2);
            Assert.InRange(Seconds(pass.AboveHorizon.Last!.Value, from) - oracle.Last, -1.2, 1.2);
            Assert.InRange(pass.PeakAltitudeDegrees - oracle.Altitude, -.001, .001);
        }
        Assert.Empty(ObservationEventSearch.FindSatellitePasses(ephemeris, epoch => iss.SampleInWorld(epoch, DeltaT),
            site, DeltaT, Utc(new(2024, 10, 27), 0), Utc(new(2024, 10, 28), 0), 10));
    }

    [Fact]
    public void AMinimumInTheLastShortIntervalIsStillFound()
    {
        var from = new JulianInstant(JulianInstant.J2000Day, 0, TimeScale.Tdb);
        var to = from.AddSeconds(10);
        var closest = Assert.Single(ObservationEventSearch.FindConjunctions(time =>
        {
            double angle = (Seconds(time, from) - 9) * .01;
            return (new Vector3d(1, 0, 0), new Vector3d(Math.Cos(angle), Math.Sin(angle), 0));
        }, from, to));
        Assert.InRange(Seconds(closest.Peak, from), 8.85, 9.15);
        Assert.Throws<ArgumentException>(() => ObservationEventSearch.FindConjunctions(_ => null, new(from.Day, 0, TimeScale.Tt), to));
    }

    private static De440Ephemeris Ephemeris() => De440Ephemeris.Load(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")!);
    private static JulianInstant Utc(DateOnly day, int hour, int minute = 0, int second = 0) =>
        TimeConversion.FromUtc(new(day, hour, minute, second), TimeScale.Tdb);
    private static double Seconds(JulianInstant a, JulianInstant b) =>
        (double)a.DifferenceNanoseconds(b) / JulianInstant.NanosecondsPerSecond;

    private class EventEphemerisFactAttribute : FactAttribute
    {
        public EventEphemerisFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")))
                Skip = "Set the pinned DE440 data path.";
        }
    }

    private sealed class SatelliteEventFactAttribute : EventEphemerisFactAttribute
    {
        public SatelliteEventFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_TLE_ROOT")))
                Skip = "Set the archived satellite group directory.";
        }
    }
}
