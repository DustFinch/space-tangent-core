// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.World;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class HistoricalSolarEphemerisTests
{
    [HistoricalFact]
    [Trait("Category", "ExternalData")]
    public void MatchesArchivedSunInnerPlanetsAndMoonAcrossTheHistoricalRange()
    {
        var (_, model) = Load();
        var builder = new WorldGraphBuilder();
        builder.AddSolarSystem(model);
        WorldGraph graph = builder.Build();
        string fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "historical-solar-reference.csv");
        int compared = 0;
        foreach (string line in File.ReadLines(fixture).Skip(1))
        {
            string[] columns = line.Split(',');
            double Number(int index) => double.Parse(columns[index], CultureInfo.InvariantCulture);
            var epoch = new JulianInstant((long)Number(0), 0, TimeScale.Tdb);
            var id = new ObjectId(columns[1]);
            var expected = new CartesianState(new(Number(2), Number(3), Number(4)), new(Number(5), Number(6), Number(7)));
            Assert.Equal(SolarEphemerisSource.Analytic, model.SourceAt(epoch));
            Assert.True(graph.Evaluate(epoch).TryState(id, out var actual));
            var positionError = (actual.PositionKilometers - expected.PositionKilometers).Length;
            var velocityError = (actual.VelocityKilometersPerSecond - expected.VelocityKilometersPerSecond).Length;
            Assert.True(positionError < .02 && velocityError < .000005,
                $"{id.Value} on JD {epoch.Day}: {positionError:R} km, {velocityError:R} km/s");
            compared++;
        }
        Assert.Equal(15, compared);
    }

    [HistoricalFact]
    [Trait("Category", "ExternalData")]
    public void KeepsTheExactKernelAndMatchesPositionAndVelocityAcrossBothTransitions()
    {
        var (kernel, model) = Load();
        var middle = new JulianInstant(JulianInstant.J2000Day, 0, TimeScale.Tdb);
        Assert.Equal(kernel.Sample(middle), model.Sample(middle));
        foreach (var (boundary, sign) in new[] { (kernel.Start, -1), (kernel.End, 1) })
        {
            Assert.Equal(kernel.Sample(boundary), model.Sample(boundary));
            Check(boundary, sign);
            var edge = boundary.AddSeconds(sign * 32 * JulianInstant.SecondsPerDay);
            Assert.Equal(SolarEphemerisSource.Analytic, model.SourceAt(edge));
            Check(edge, -sign);
        }
        var ancient = model.Sample(model.Start);
        var farFuture = model.Sample(model.End);
        Assert.True(ancient.PlutoBarycenter.PositionKilometers.Length > 1e9);
        Assert.True(farFuture.PlutoBarycenter.PositionKilometers.Length > 1e9);
        Assert.Throws<ArgumentOutOfRangeException>(() => model.Sample(model.Start.AddSeconds(-1)));

        void Check(JulianInstant boundary, int sign)
        {
            var next = boundary.AddSeconds(sign * .125);
            Assert.Equal(SolarEphemerisSource.Transition, model.SourceAt(next));
            foreach (var id in new[] { SolarSystemSample.EarthId, SolarSystemSample.MoonId, SolarSystemSample.PlutoBarycenterId })
            {
                CartesianState exact = model.Sample(boundary).RelativeState(id, SolarSystemSample.BarycenterId);
                CartesianState adjacent = model.Sample(next).RelativeState(id, SolarSystemSample.BarycenterId);
                Vector3d extrapolated = exact.PositionKilometers + exact.VelocityKilometersPerSecond * (sign * .125);
                Assert.True((adjacent.PositionKilometers - extrapolated).Length < .002);
                Assert.True((adjacent.VelocityKilometersPerSecond - exact.VelocityKilometersPerSecond).Length < .00005);
            }
        }
    }

    private static (De440Ephemeris, HistoricalSolarEphemeris) Load()
    {
        var kernel = De440Ephemeris.Load(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")!);
        return (kernel, HistoricalSolarEphemeris.Load(kernel, Environment.GetEnvironmentVariable("SPACETANGENT_VSOP87")!));
    }

    private sealed class HistoricalFactAttribute : FactAttribute
    {
        public HistoricalFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_DE440")) ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_VSOP87")))
                Skip = "Set SPACETANGENT_DE440 and SPACETANGENT_VSOP87 to the pinned ephemeris inputs.";
        }
    }
}
