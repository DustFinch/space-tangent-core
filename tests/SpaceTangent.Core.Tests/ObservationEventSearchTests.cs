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

    [EventEphemerisTheory]
    [Trait("Category", "ExternalData")]
    [InlineData(2021, 11, 19, 9, 4, 6, EclipseClass.Partial, .9742, 361.5, 208.4, 0)]
    [InlineData(2022, 11, 8, 11, 0, 22, EclipseClass.Total, 1.3589, 353.9, 219.8, 85.0)]
    [InlineData(2023, 5, 5, 17, 24, 5, EclipseClass.Penumbral, -.0457, 257.5, 0, 0)]
    public void DifferentLunarEclipseClassesMatchThePublishedCanon(int year, int month, int day,
        int hour, int minute, int second, EclipseClass kind, double magnitude, double penumbra, double partial, double total)
    {
        // NASA's century catalogue gives greatest eclipse in TD, not UTC, and durations rounded to 0.1 min.
        // https://eclipse.gsfc.nasa.gov/LEcat5/LE2001-2100.html
        // 30 s covers the mean shadow-radius approximation across three geometries; magnitude tolerance is .005.
        var date = new DateOnly(year, month, day);
        var eclipse = Assert.Single(ObservationEventSearch.FindLunarEclipses(Ephemeris(), Utc(date, 0), Utc(date.AddDays(1), 0)));
        var expectedTt = JulianInstant.FromJulianDate(date.DayNumber + 1721425.5, TimeScale.Tt)
            .AddSeconds(hour * 3600 + minute * 60 + second);
        Assert.Equal(kind, eclipse.Class);
        Assert.InRange(Seconds(TimeConversion.Convert(eclipse.Penumbral.Peak, TimeScale.Tt), expectedTt), -30, 30);
        Assert.InRange(eclipse.UmbralMagnitude - magnitude, -.005, .005);
        Duration(eclipse.Penumbral, penumbra);
        Duration(eclipse.Umbral, partial);
        Duration(eclipse.Total, total);

        static void Duration(ObservationInterval? interval, double minutes)
        {
            if (minutes == 0) { Assert.Null(interval); return; }
            Assert.NotNull(interval);
            Assert.InRange(Seconds(interval.Value.Last!.Value, interval.Value.First!.Value) - minutes * 60, -30, 30);
        }
    }

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
        // NASA/GSFC: P1 03:57:24, U1 05:09:33, greatest 06:58:42, U3 07:31:23 UT, umbral magnitude 1.1784.
        // Published times are rounded to the second and use their own ΔT, hence 20 s.
        Assert.InRange(Seconds(lunar.Penumbral.Peak, Utc(lunarDate, 6, 58, 42)), -20, 20);
        Assert.InRange(Seconds(lunar.Penumbral.First!.Value, Utc(lunarDate, 3, 57, 24)), -20, 20);
        Assert.InRange(Seconds(lunar.Umbral!.Value.First!.Value, Utc(lunarDate, 5, 9, 33)), -20, 20);
        Assert.InRange(Seconds(lunar.Total!.Value.Last!.Value, Utc(lunarDate, 7, 31, 23)), -20, 20);
        Assert.InRange(lunar.UmbralMagnitude, 1.1784 - .003, 1.1784 + .003);
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
        // Independently generated in TEME/terrestrial axes by python-sgp4 and ERFA; the original tolerances remain.
        var expected = File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "iss-pass-reference.csv"))
            .Where(line => !line.StartsWith('#')).Select(line => line.Split(',')
                .Select(value => double.Parse(value, System.Globalization.CultureInfo.InvariantCulture)).ToArray()).ToArray();
        Assert.Equal(expected.Length, passes.Count);
        for (int index = 0; index < expected.Length; index++)
        {
            var pass = passes[index];
            var oracle = expected[index];
            Assert.InRange(Seconds(pass.AboveHorizon.First!.Value, from) - oracle[0], -1.2, 1.2);
            Assert.InRange(Seconds(pass.AboveHorizon.Peak, from) - oracle[1], -1.2, 1.2);
            Assert.InRange(Seconds(pass.AboveHorizon.Last!.Value, from) - oracle[2], -1.2, 1.2);
            Assert.InRange(pass.PeakAltitudeDegrees - oracle[3], -.001, .001);
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

    private sealed class EventEphemerisTheoryAttribute : TheoryAttribute
    {
        public EventEphemerisTheoryAttribute()
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
