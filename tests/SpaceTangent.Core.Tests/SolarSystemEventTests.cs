// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Time;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class SolarSystemEventTests
{
    [AlmanacEphemerisFact]
    [Trait("Category", "ExternalData")]
    public void PhasesOppositionAndElongationKeepTheAcceptedAlmanacDates()
    {
        var ephemeris = De440Ephemeris.Load(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")!);
        var from = Utc(2024, 10, 16, 23, 58);
        var events = SolarSystemEventSearch.Find(ephemeris, from, from.AddSeconds(45 * 86400));
        AssertTime(SolarSystemEventKind.FullMoon, Utc(2024, 10, 17, 11, 26));
        AssertTime(SolarSystemEventKind.LastQuarter, Utc(2024, 10, 24, 8, 3));
        AssertTime(SolarSystemEventKind.NewMoon, Utc(2024, 11, 1, 12, 47));
        AssertTime(SolarSystemEventKind.FirstQuarter, Utc(2024, 11, 9, 5, 55));
        var opposition = Assert.Single(events, e => e.Kind == SolarSystemEventKind.Opposition && e.Target == new ObjectId("sol/uranus"));
        Assert.InRange(Math.Abs(Seconds(opposition.Epoch, Utc(2024, 11, 17, 2))), 0, 8640);
        var mercury = Assert.Single(events, e => e.Kind == SolarSystemEventKind.GreatestElongation && e.Target == SolarSystemSample.MercuryId);
        Assert.InRange(mercury.Degrees, 22.4, 22.7);
        Assert.InRange(Math.Abs(Seconds(mercury.Epoch, Utc(2024, 11, 16, 8))), 0, 43200);
        Assert.Contains(events, e => e.Kind == SolarSystemEventKind.LunarApproach && e.Name == "Antares" && e.Degrees < .3);
        Assert.Equal(events.OrderBy(e => e.Epoch), events);
        var full = events.First(e => e.Kind == SolarSystemEventKind.FullMoon);
        var shortWindow = SolarSystemEventSearch.Find(ephemeris, full.Epoch.AddSeconds(-600), full.Epoch.AddSeconds(600));
        Assert.Single(shortWindow, e => e.Kind == SolarSystemEventKind.FullMoon);

        void AssertTime(SolarSystemEventKind kind, JulianInstant expected) =>
            Assert.Contains(events, e => e.Kind == kind && Math.Abs(Seconds(e.Epoch, expected)) <= 120);
    }

    [AlmanacEphemerisFact]
    [Trait("Category", "ExternalData")]
    public void SeasonalCrossingsAndPlanetApproachesAgreeWithTheAlmanac()
    {
        var ephemeris = De440Ephemeris.Load(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")!);
        var from = Utc(2024, 9, 1);
        var events = SolarSystemEventSearch.Find(ephemeris, from, from.AddSeconds(300 * 86400));
        var seasons = events.Where(e => e.Kind == SolarSystemEventKind.Season).ToArray();
        Assert.Equal(new double[] { 180, 270, 0, 90 }, seasons.Select(e => e.Degrees));
        var expected = new[] { Utc(2024, 9, 22, 12, 44), Utc(2024, 12, 21, 9, 20), Utc(2025, 3, 20, 9, 1), Utc(2025, 6, 21, 2, 42) };
        for (int i = 0; i < seasons.Length; i++) Assert.InRange(Math.Abs(Seconds(seasons[i].Epoch, expected[i])), 0, 180);
        var august = SolarSystemEventSearch.Find(ephemeris, Utc(2024, 8, 1), Utc(2024, 8, 31));
        var pair = Assert.Single(august, e => e.Kind == SolarSystemEventKind.PlanetApproach);
        Assert.Equal(new ObjectId("sol/mars"), pair.Target);
        Assert.Equal(new ObjectId("sol/jupiter"), pair.Other);
        Assert.InRange(pair.Degrees, .25, .4);
        Assert.InRange(Seconds(pair.Epoch, Utc(2024, 8, 14, 12)), 0, 43200);
    }

    [Fact]
    public void PartialCoverageShortIntervalsAndCancellationAreExplicit()
    {
        var ephemeris = new CircularMoon();
        var from = ephemeris.Start;
        var phases = SolarSystemEventSearch.Find(ephemeris, from, from.AddSeconds(30 * 86400))
            .Where(e => e.Kind is SolarSystemEventKind.FirstQuarter or SolarSystemEventKind.FullMoon or SolarSystemEventKind.LastQuarter or SolarSystemEventKind.NewMoon).ToArray();
        Assert.Equal(4, phases.Length);
        for (int i = 0; i < phases.Length; i++)
            Assert.InRange(Math.Abs(Seconds(phases[i].Epoch, from) - ((i + 1) * 7 * 86400 + 384400 / 299792.458)), 0, 2);
        Assert.All(SolarSystemEventSearch.Find(ephemeris, ephemeris.End.AddSeconds(-10), ephemeris.End.AddSeconds(1000)),
            e => Assert.True(e.Epoch.CompareTo(ephemeris.End) <= 0));
        Assert.Throws<ArgumentException>(() => SolarSystemEventSearch.Find(ephemeris, new(from.Day, 0, TimeScale.Tt), ephemeris.End));
        Assert.Throws<OperationCanceledException>(() => SolarSystemEventSearch.Find(ephemeris, from, ephemeris.End, new(true)));
    }

    [Fact]
    public void GlobalShadowContactUsesTheReceiverDiscAndPreservesGaps()
    {
        var from = JulianInstant.FromJulianDate(2451545, TimeScale.Tdb);
        (Vector3d, Vector3d, Vector3d)? At(JulianInstant epoch) => (new(1e12, 0, 0), default, new(1000, Seconds(epoch, from) - 900, 0));
        var shadow = Assert.Single(ObservationEventSearch.FindShadowTransits(At, 100, 10, from, from.AddSeconds(1800)));
        Assert.InRange(Math.Abs(Seconds(shadow.Peak, from) - 900), 0, 2);
        Assert.InRange(Math.Abs(Seconds(shadow.First!.Value, from) - 790), 0, 2);
        Assert.InRange(Math.Abs(Seconds(shadow.Last!.Value, from) - 1010), 0, 2);
        var gap = Assert.Single(ObservationEventSearch.FindShadowTransits(
            epoch => Seconds(epoch, from) is >= 600 and <= 800 ? null : At(epoch), 100, 10, from, from.AddSeconds(1800)));
        Assert.Null(gap.First);
        Assert.NotNull(gap.Last);
    }

    [Theory]
    [InlineData(1050, true)]
    [InlineData(1099.9, true)]
    [InlineData(1101, false)]
    public void AReceiverCanMeetTheConeBeforeAnApexShortOfItsCentre(double depth, bool intersects)
    {
        var from = JulianInstant.FromJulianDate(2451545, TimeScale.Tdb);
        // The umbral apex is exactly 1000 km behind the 10 km occulter.
        double sunDistance = (SolarOccultation.SunRadiusKilometers - 10) * 100;
        var events = ObservationEventSearch.FindShadowTransits(epoch =>
            (new(-sunDistance, 0, 0), new(depth, Seconds(epoch, from) - 900, 0), default),
            100, 10, from, from.AddSeconds(1800));
        if (!intersects) { Assert.Empty(events); return; }
        var transit = Assert.Single(events);
        double half = Math.Sqrt(100 * 100 - (depth - 1000) * (depth - 1000));
        Assert.InRange(Math.Abs(Seconds(transit.Peak, from) - 900), 0, 1);
        Assert.InRange(Math.Abs(Seconds(transit.First!.Value, from) - (900 - half)), 0, 1);
        Assert.InRange(Math.Abs(Seconds(transit.Last!.Value, from) - (900 + half)), 0, 1);
    }

    [Fact]
    public void BelowDatumSitesKeepFiniteLightingRefractionAndRejectTheBodyCentre()
    {
        var site = new EarthSite(31.5, 35.5, -.43);
        Assert.True(site.IsValid());
        Assert.False((site with { HeightKilometers = -12.001 }).IsValid());
        var system = new CircularMoon().Sample(new CircularMoon().Start);
        var light = EarthSurfaceLighting.Sample(system, site, DeltaTModel.FixedOffset);
        Assert.True(double.IsFinite(light.SolarAltitudeRadians));
        var air = new GroundSkyConditions(3);
        Assert.InRange(air.PressureAt(site), 1013, 1100);
        Assert.InRange(air.ApparentAltitude(0, site), .4, .8);
        Assert.Throws<ArgumentOutOfRangeException>(() => BodySurfaceFrame.OnEllipsoid(default,
            new(Basis3d.Identity, null), site with { HeightKilometers = -2 }, 2, 2));
    }

    private sealed class CircularMoon : ISolarSystemEphemeris
    {
        public JulianInstant Start { get; } = JulianInstant.FromJulianDate(2451545, TimeScale.Tdb);
        public JulianInstant End => Start.AddSeconds(40 * 86400);
        public SolarSystemSample Sample(JulianInstant epoch)
        {
            Assert.True(epoch.CompareTo(Start) >= 0 && epoch.CompareTo(End) <= 0);
            double angle = Seconds(epoch, Start) * Math.Tau / (28 * 86400), tilt = 23.439291111 * Math.PI / 180;
            var moon = new Vector3d(Math.Cos(angle), Math.Sin(angle) * Math.Cos(tilt), Math.Sin(angle) * Math.Sin(tilt)) * 384400;
            return new(epoch, new(new(149597870.7, 0, 0), default), default, new(moon, default), default);
        }
    }
    [Fact]
    public void AlmanacCanIncludeMajorOutburstsWithoutChangingTheNormalPeakShortcut()
    {
        const string header = "code\tname\tbegin\tend\tpeak\tra\tdec\tdrift_ra\tdrift_dec\tspeed\tpopulation\tannual_zhr\tslope_before\tslope_after\tmax_zhr";
        var catalogue = MeteorCatalogue.Read(new StringReader(header + "\n" +
            "AAA\tOrdinary\t0\t359\t60\t0\t0\t0\t0\t30\t2\t20\t1\t1\t20\n" +
            "BBB\tOutburst\t0\t359\t30\t0\t0\t0\t0\t30\t2\t1\t1\t1\t400\n"));
        var ephemeris = new AnnualSun();
        Assert.Equal("AAA", catalogue.NextPeak(ephemeris, ephemeris.Start)!.Value.Shower.Code);
        var peak = catalogue.NextPeak(ephemeris, ephemeris.Start, includeOutbursts: true)!.Value;
        Assert.Equal("BBB", peak.Shower.Code);
        Assert.InRange(Math.Abs(Seconds(peak.Epoch, ephemeris.Start) - 30 * 86400), 0, 2);
    }
    private sealed class AnnualSun : ISolarSystemEphemeris
    {
        public JulianInstant Start { get; } = JulianInstant.FromJulianDate(2451545, TimeScale.Tdb);
        public JulianInstant End => Start.AddSeconds(400 * 86400);
        public SolarSystemSample Sample(JulianInstant epoch)
        {
            double angle = Seconds(epoch, Start) / 86400 * Math.PI / 180, tilt = 23.439291111 * Math.PI / 180;
            var sun = new Vector3d(Math.Cos(angle), Math.Sin(angle) * Math.Cos(tilt), Math.Sin(angle) * Math.Sin(tilt)) * 149597870.7;
            return new(epoch, new(sun, default), default, new(new(384400, 0, 0), default), default);
        }
    }
    private static JulianInstant Utc(int year, int month, int day, int hour = 0, int minute = 0) =>
        TimeConversion.FromUtc(new(new(year, month, day), hour, minute), TimeScale.Tdb);
    private static double Seconds(JulianInstant a, JulianInstant b) => (double)a.DifferenceNanoseconds(b) / JulianInstant.NanosecondsPerSecond;
    private sealed class AlmanacEphemerisFactAttribute : FactAttribute
    {
        public AlmanacEphemerisFactAttribute()
        {
            if (!File.Exists(Environment.GetEnvironmentVariable("SPACETANGENT_DE440"))) Skip = "Set SPACETANGENT_DE440 for the retained almanac references.";
        }
    }
}
