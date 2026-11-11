// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Time;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class De440EphemerisTests
{
    private static readonly Lazy<De440Ephemeris> Ephemeris = new(() =>
        De440Ephemeris.Load(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")!));

    public static IEnumerable<object[]> References()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "de440-reference.csv");
        foreach (string line in File.ReadLines(path))
        {
            if (!line.StartsWith('#'))
                yield return [line];
        }
    }

    [De440Theory]
    [MemberData(nameof(References))]
    public void MatchesIndependentSpicePositionAndVelocity(string line)
    {
        double[] values = line.Split(',').Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        var tdb = JulianInstant.FromJulianDate(values[0], TimeScale.Tdb);
        SolarSystemSample sample = Ephemeris.Value.Sample(tdb);
        CartesianState state = sample.RelativeState(Object((int)values[1]), Object((int)values[2]));
        var expectedPosition = new Vector3d(values[3], values[4], values[5]);
        var expectedVelocity = new Vector3d(values[6], values[7], values[8]);

        Assert.Equal(new FrameId("icrf3"), sample.Frame);
        Assert.Equal(tdb, sample.Epoch);
        Assert.InRange((state.PositionKilometers - expectedPosition).Length, 0, 5e-6);
        Assert.InRange((state.VelocityKilometersPerSecond - expectedVelocity).Length, 0, 5e-10);
    }

    [De440Fact]
    public void CoverageUsesExactEpochsAndRejectsExtrapolation()
    {
        De440Ephemeris model = Ephemeris.Value;
        Assert.True(model.Sample(model.Start).Earth.PositionKilometers.IsFinite);
        Assert.True(model.Sample(model.End).Moon.VelocityKilometersPerSecond.IsFinite);
        Assert.Throws<ArgumentOutOfRangeException>(() => model.Sample(model.Start.AddNanoseconds(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => model.Sample(model.End.AddNanoseconds(1)));
        Assert.Throws<ArgumentException>(() => model.Sample(new JulianInstant(model.Start.Day, 0, TimeScale.Tt)));
        Assert.Throws<ArgumentOutOfRangeException>(() => model.Sample(model.Start).RelativeState(new ObjectId("unknown"), default));
    }

    [Fact]
    public void RejectsUnrecognizedAndCorruptFileContents()
    {
        string file = Path.GetTempFileName();
        try
        {
            Assert.Throws<InvalidDataException>(() => De440Ephemeris.Load(file));
            using (FileStream stream = File.OpenWrite(file))
                stream.SetLength(18_617_184);
            Assert.Throws<InvalidDataException>(() => De440Ephemeris.Load(file));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [De440Fact]
    public void MoscowFullMoonAndDayControlMatchIndependentDirections()
    {
        var site = new EarthSite(55.7558, 37.6173, 0);
        var night = JulianInstant.FromJulianDate(2460600.5008, TimeScale.Tdb);
        var moon = EarthSurfaceLighting.Sample(Ephemeris.Value.Sample(night), site, DeltaTModel.MorrisonStephenson2004);
        var day = EarthSurfaceLighting.Sample(Ephemeris.Value.Sample(night.AddSeconds(43200)), site,
            DeltaTModel.MorrisonStephenson2004);
        double degrees = 180 / Math.PI;

        Assert.InRange(moon.SolarAltitudeRadians * degrees, -33.25, -33.19);
        Assert.InRange(moon.LunarAltitudeRadians * degrees, 27.74, 27.81);
        Assert.InRange(day.SolarAltitudeRadians * degrees, 16.21, 16.28);
        Assert.InRange(day.LunarAltitudeRadians * degrees, -15.62, -15.55);
        Assert.InRange(moon.RelativeLunarFlux * .12, .10, .16);
    }

    private static ObjectId Object(int id) => id switch
    {
        0 => SolarSystemSample.BarycenterId,
        10 => SolarSystemSample.SunId,
        399 => SolarSystemSample.EarthId,
        301 => SolarSystemSample.MoonId,
        _ => throw new ArgumentOutOfRangeException(nameof(id))
    };

    private sealed class De440FactAttribute : FactAttribute
    {
        public De440FactAttribute() => Skip = MissingDataReason();
    }

    private sealed class De440TheoryAttribute : TheoryAttribute
    {
        public De440TheoryAttribute() => Skip = MissingDataReason();
    }

    private static string? MissingDataReason() => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_DE440"))
        ? "Set SPACETANGENT_DE440 to the published classic DE440 file or its pinned subset."
        : null;
}
