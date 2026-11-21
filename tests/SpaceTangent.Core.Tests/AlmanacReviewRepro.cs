// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Time;
using SpaceTangent.Core.World;
using Xunit;

namespace SpaceTangent.Core.Tests;

/// <summary>Review repro for 3eef839 and 19aeba4 (core 19aeba4). Put in tests/SpaceTangent.Core.Tests/.</summary>
public sealed class AlmanacReviewRepro
{
    // 1. Hybrid eclipses: the umbra apex lies slightly short of Earth's centre, but the umbra still reaches the surface.
    //    Expected references: NASA eclipse catalogue (Espenak) — 2023-04-20 hybrid (total at Exmouth), 2005-04-08 hybrid.
    [Ephemeris]
    [Trait("Category", "ExternalData")]
    public void HybridSolarEclipsesAreGlobalUmbralTransits()
    {
        var ephemeris = Load();
        Assert.Single(Transits(ephemeris, Utc(2023, 4, 20, 0), Utc(2023, 4, 20, 8)));
        Assert.Single(Transits(ephemeris, Utc(2005, 4, 8, 17), Utc(2005, 4, 9, 0)));
        // Controls that already pass: total eclipse 2024-04-08 found, annular 2023-10-14 (no umbra on Earth) absent.
        Assert.Single(Transits(ephemeris, Utc(2024, 4, 8, 15), Utc(2024, 4, 8, 22)));
        Assert.Empty(Transits(ephemeris, Utc(2023, 10, 14, 14), Utc(2023, 10, 14, 22)));
    }

    // 2. Annual aberration: USNO phases, minute precision. Apparent times are 11:26:23 and 02:34:28; 19aeba4 gives about
    //    11:27:33 and 02:35:37 (geometric direction with light time only).
    [Ephemeris]
    [Trait("Category", "ExternalData")]
    public void FullMoonTimesIncludeAnnualAberration()
    {
        var ephemeris = Load();
        foreach (var expected in new[] { Utc(2024, 10, 17, 11, 26), Utc(2024, 9, 18, 2, 34) })
        {
            var events = SolarSystemEventSearch.Find(ephemeris, expected.AddSeconds(-86400), expected.AddSeconds(86400));
            var full = Assert.Single(events, e => e.Kind == SolarSystemEventKind.FullMoon);
            Assert.InRange(Seconds(full.Epoch, expected), -60, 60);
        }
    }

    // 3. Events close to a window edge must not disappear: consecutive windows (the search is capped at 366 days)
    //    otherwise lose events at their joins. Mercury greatest eastern elongation: 2024-03-24 about 22:34 UTC.
    [Ephemeris]
    [Trait("Category", "ExternalData")]
    public void ExtremesNearWindowEdgesAreFound()
    {
        var ephemeris = Load();
        var elongation = Utc(2024, 3, 24, 22, 34);
        bool Found(JulianInstant from, JulianInstant to) => SolarSystemEventSearch.Find(ephemeris, from, to)
            .Any(e => e.Kind == SolarSystemEventKind.GreatestElongation && e.Target == SolarSystemSample.MercuryId &&
                Math.Abs(Seconds(e.Epoch, elongation)) < 6 * 3600);
        Assert.True(Found(elongation.AddSeconds(-20 * 86400), elongation.AddSeconds(20 * 86400)));
        Assert.True(Found(elongation.AddSeconds(-0.3 * 86400), elongation.AddSeconds(20 * 86400)));
        Assert.True(Found(elongation.AddSeconds(-20 * 86400), elongation.AddSeconds(0.3 * 86400)));
    }

    // 4. A full civil leap year is 366 days plus TDB-UTC differences (21 µs in 2024, plus a leap second in 2016).
    [Ephemeris]
    [Trait("Category", "ExternalData")]
    public void AWholeCivilLeapYearCanBeSearched()
    {
        var ephemeris = Load();
        Assert.NotEmpty(SolarSystemEventSearch.Find(ephemeris, Utc(2024, 1, 1), Utc(2025, 1, 1)));
        Assert.NotEmpty(SolarSystemEventSearch.Find(ephemeris, Utc(2016, 1, 1), Utc(2017, 1, 1)));
    }

    // 5. Sites below the reference ellipsoid exist: Hellas basin on Mars (about -7 km), Dead Sea shore on Earth (-0.43 km).
    [Fact]
    public void SitesBelowTheReferenceEllipsoidAreAccepted()
    {
        var rotation = new FrameKinematics(Basis3d.Identity, null);
        var hellas = BodySurfaceFrame.OnEllipsoid(default, rotation, new(-42.4, 70.5, -7), 3396.19, 3376.2);
        Assert.InRange(hellas.Observer.PositionKilometers.Length, 3370, 3390);
        var deadSea = BodySurfaceFrame.OnEllipsoid(default, rotation, new(31.5, 35.5, -.43), 6378.137, 6356.752);
        Assert.InRange(deadSea.Observer.PositionKilometers.Length, 6370, 6373);
    }

    private static IReadOnlyList<ObservationInterval> Transits(ISolarSystemEphemeris ephemeris, JulianInstant from, JulianInstant to) =>
        ObservationEventSearch.FindShadowTransits(epoch =>
        {
            var sample = ephemeris.Sample(epoch);
            return (sample.Sun.PositionKilometers, sample.Earth.PositionKilometers, sample.Moon.PositionKilometers);
        }, 6378.137, 1737.4, from, to);

    private static De440Ephemeris Load() => De440Ephemeris.Load(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")!);
    private static JulianInstant Utc(int year, int month, int day, int hour = 0, int minute = 0) =>
        TimeConversion.FromUtc(new(new(year, month, day), hour, minute), TimeScale.Tdb);
    private static double Seconds(JulianInstant a, JulianInstant b) => (double)a.DifferenceNanoseconds(b) / JulianInstant.NanosecondsPerSecond;

    private sealed class EphemerisAttribute : FactAttribute
    {
        public EphemerisAttribute()
        {
            if (!File.Exists(Environment.GetEnvironmentVariable("SPACETANGENT_DE440"))) Skip = "Set SPACETANGENT_DE440.";
        }
    }
}
