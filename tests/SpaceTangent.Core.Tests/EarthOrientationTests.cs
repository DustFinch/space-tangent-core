// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Coordinates;
using SpaceTangent.Core.Time;
using SpaceTangent.Core.World;
using SpaceTangent.Core.Ephemerides;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class EarthOrientationTests
{
    [Fact]
    public void BuilderDefaultModelIsIncludedInTheWholeEvaluationContext()
    {
        var builder = new WorldGraphBuilder();
        builder.AddMeanEarthRotation(DeltaTModel.FixedOffset);
        var graph = builder.Build();
        var epoch = JulianInstant.FromJulianDate(2106215, TimeScale.Tdb);
        var snapshot = graph.Evaluate(epoch);
        Assert.Equal(DeltaTModel.FixedOffset, snapshot.DeltaT);
        Assert.True(snapshot.TryOrientation(SolarSystemSample.EarthId, out var frame));
        Assert.Equal(EarthOrientation.TerrestrialToWorld(epoch, DeltaTModel.FixedOffset), frame);
        Assert.Equal(DeltaTModel.MorrisonStephenson2004, graph.Evaluate(epoch, DeltaTModel.MorrisonStephenson2004).DeltaT);
        Assert.Equal(DeltaTModel.FixedOffset, graph.Evaluate(epoch).DeltaT);
    }

    [Fact]
    public void PerEvaluationDeltaTReachesEarthFixedStatesWithoutChangingOtherSnapshots()
    {
        var builder = new WorldGraphBuilder();
        builder.AddTrajectory(SolarSystemSample.EarthId, _ => default(CartesianState));
        builder.AddMeanEarthRotation();
        var probe = new ObjectId("surface-probe");
        builder.AddTrajectory(probe, _ => new CartesianState(new(6378.1366, 0, 0), default),
            SolarSystemSample.EarthId, SolarSystemSample.EarthId);
        var graph = builder.Build();
        var epoch = JulianInstant.FromJulianDate(2106215, TimeScale.Tdb);
        var original = graph.Evaluate(epoch);
        Assert.True(original.TryState(probe, out var originalState));
        Parallel.For(0, 24, index =>
        {
            var model = (DeltaTModel)(index % 3);
            var snapshot = graph.Evaluate(epoch, model);
            Assert.Equal(model, snapshot.DeltaT);
            Assert.True(snapshot.TryState(probe, out var state));
            Vector3d expected = EarthOrientation.TerrestrialToWorld(epoch, model).ToWorld.X * 6378.1366;
            Assert.InRange((expected - state.PositionKilometers).Length, 0, 1e-9);
        });
        Assert.True(original.TryState(probe, out var unchanged));
        Assert.Equal(originalState, unchanged);
        Assert.True(graph.Evaluate(epoch, DeltaTModel.FixedOffset).TryState(probe, out var fixedState));
        Assert.True((fixedState.PositionKilometers - originalState.PositionKilometers).Length > 100);
        Assert.Throws<ArgumentOutOfRangeException>(() => graph.Evaluate(epoch, (DeltaTModel)99));
    }

    // The precession matrix is a direct port, so it agrees to rounding. The terrestrial matrices omit the periodic
    // terms of the CIO locator s (eraS06). Near J2000 they stay below 3 mas, so the tolerance is 5 mas; their
    // t·sin and t²·sin terms (743.52 µas t² sin Ω being the largest) add up to 1 mas t² far from J2000. Such
    // epochs are dominated by the uncertainty of ΔT, which reaches minutes of time.
    private const double PrecessionTolerance = 1e-12;
    private static double RotationTolerance(double centuries) => (5 + centuries * centuries) * 1e-3 * Math.PI / 648_000;

    public static IEnumerable<object[]> References() =>
        File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "earth-orientation-reference.csv"))
            .Where(line => !line.StartsWith('#')).Select(line => new object[] { line });

    [Theory]
    [MemberData(nameof(References))]
    public void MatchesErfaFromYearOneToYearFourThousand(string row)
    {
        double[] values = row.Split(',').Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        var tt = new JulianInstant(JulianInstant.J2000Day, 0, TimeScale.Tt).AddSeconds(values[0] * 86_400);
        double ttMinusUt1 = values[1];

        AssertTransposeOf(values.AsSpan(3, 9), EarthOrientation.MeanEquatorOfDateToWorld(tt), PrecessionTolerance);
        var terrestrial = EarthOrientation.TerrestrialToWorld(tt, ttMinusUt1);
        double tolerance = RotationTolerance(values[0] / 36_525);
        AssertTransposeOf(values.AsSpan(12, 9), terrestrial.ToWorld, tolerance);
        AssertMatrix(values.AsSpan(21, 9), EarthOrientation.TemeToWorld(tt, ttMinusUt1), tolerance);

        // The Earth rotation angle advances by 2π × 1.00273781191135448 per UT1 day.
        Vector3d rate = terrestrial.AngularVelocityRadiansPerSecond!.Value;
        Assert.Equal(7.2921151467e-5, rate.Length, 14);
        Assert.InRange((rate / rate.Length - terrestrial.ToWorld.Z).Length, 0, 1e-15);
    }

    [Fact]
    public void ModernPrecessionAndNutationMoveThePoleByTheExpectedAmount()
    {
        // In 2026 the true pole is about 0.15 degrees from the ICRF pole, and the equinox has moved 0.36 degrees.
        var tt = new JulianInstant(JulianInstant.J2000Day, 0, TimeScale.Tt).AddSeconds(26.5 * 365.25 * 86_400);
        Basis3d mean = EarthOrientation.MeanEquatorOfDateToWorld(tt);
        double pole = Math.Acos(Math.Clamp(mean.Z.Z, -1, 1)) * 180 / Math.PI;
        Assert.InRange(pole, .145, .150);
        var terrestrial = EarthOrientation.TerrestrialToWorld(tt, 69.2).ToWorld;
        Assert.InRange(Math.Acos(Math.Clamp(Vector3d.Dot(terrestrial.Z, mean.Z), -1, 1)) * 180 / Math.PI * 3600, 0, 20);
    }

    [Fact]
    public void WorldEpochsUseTheirTimeScaleAndTheSelectedDeltaT()
    {
        var tdb = new JulianInstant(2_460_000, 0, TimeScale.Tdb);
        var tt = TimeConversion.Convert(tdb, TimeScale.Tt);
        double deltaT = DeltaT.Seconds(tt, DeltaTModel.EspenakMeeus2006);
        var fromTdb = EarthOrientation.TerrestrialToWorld(tdb, DeltaTModel.EspenakMeeus2006).ToWorld;
        var fromTt = EarthOrientation.TerrestrialToWorld(tt, deltaT).ToWorld;
        Assert.InRange((fromTdb.X - fromTt.X).Length + (fromTdb.Z - fromTt.Z).Length, 0, 1e-12);
        Assert.Throws<ArgumentException>(() => EarthOrientation.TerrestrialToWorld(tdb, deltaT));
    }

    private static void AssertTransposeOf(ReadOnlySpan<double> rowMajor, Basis3d basis, double tolerance)
    {
        // A celestial-to-local matrix M maps to a basis whose columns are the rows of M.
        Vector3d[] columns = [basis.X, basis.Y, basis.Z];
        for (int row = 0; row < 3; row++)
        {
            var expected = new Vector3d(rowMajor[row * 3], rowMajor[row * 3 + 1], rowMajor[row * 3 + 2]);
            Assert.InRange((columns[row] - expected).Length, 0, tolerance);
        }
    }

    private static void AssertMatrix(ReadOnlySpan<double> rowMajor, Basis3d basis, double tolerance)
    {
        Vector3d[] columns = [basis.X, basis.Y, basis.Z];
        for (int column = 0; column < 3; column++)
        {
            var expected = new Vector3d(rowMajor[column], rowMajor[3 + column], rowMajor[6 + column]);
            Assert.InRange((columns[column] - expected).Length, 0, tolerance);
        }
    }
}
