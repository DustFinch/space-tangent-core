// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using System.Security.Cryptography;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Orbits;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class SampledTrajectoryTests
{
    private static readonly JulianInstant Epoch = new(JulianInstant.J2000Day, 0, TimeScale.Tdb);
    private static readonly FrameId Frame = new("ecliptic-j2000");

    [Fact]
    public void EvaluatesAStateAndItsAnalyticDerivativeAtTheSameEpoch()
    {
        var trajectory = new SampledStateTrajectory(
        [
            new(Epoch, new(new(100, 0, 0), new(3, 0, 0))),
            new(Epoch.AddSeconds(2), new(new(122, 0, 0), new(23, 0, 0)))
        ], Frame);

        Assert.True(trajectory.TrySample(Epoch.AddSeconds(1), out var state));
        Assert.Equal(106, state.PositionKilometers.X);
        Assert.Equal(10, state.VelocityKilometersPerSecond.X);
        Assert.False(trajectory.TrySample(Epoch.AddNanoseconds(-1), out _));
        Assert.False(trajectory.TrySample(Epoch.AddSeconds(2).AddNanoseconds(1), out _));
    }

    [Fact]
    public void ExactClockDistinguishesSamplesWithTheSameDoubleJulianDay()
    {
        TimedState[] samples =
        [
            new(Epoch, new(default, new(2, 0, 0))),
            new(Epoch.AddNanoseconds(2), new(new(4e-9, 0, 0), new(2, 0, 0)))
        ];
        Assert.Equal(samples[0].Epoch.ToJulianDate(), samples[1].Epoch.ToJulianDate());
        var trajectory = new SampledStateTrajectory(samples, Frame);
        Assert.True(trajectory.TrySample(Epoch.AddNanoseconds(1), out var state));
        Assert.Equal(2e-9, state.PositionKilometers.X, 1e-20);
        Assert.Equal(2, state.VelocityKilometersPerSecond.X, 1e-12);
        Assert.Throws<ArgumentException>(() => new SampledStateTrajectory([samples[0], samples[0]], Frame));
    }

    [Fact]
    public void SourceClockSelectsTheFirstDuplicateAtItsEpochAndTheLastAfterCoverage()
    {
        JulianInstant duplicated = Epoch.AddSeconds(20);
        TimedState first = new(Epoch, default);
        TimedState left = new(duplicated, new(new(1, 0, 0), new(2, 0, 0)));
        TimedState right = new(duplicated, new(new(3, 0, 0), new(4, 0, 0)));
        var curve = new SampledStateTrajectory([first, left, right], Frame, SampledEndpointPolicy.ClampState,
            SampledClockPolicy.SourceJulianDouble);
        Assert.True(curve.TrySample(duplicated, out var exact));
        Assert.Equal(left.State, exact);
        Assert.True(curve.TrySample(duplicated.AddSeconds(1), out var after));
        Assert.Equal(right.State, after);
    }

    [RealSamplesFact("SPACETANGENT_2008TC3_XYZV")]
    public void OriginalAsteroidTrajectoryMatchesNativePositionAndVelocity()
    {
        string source = Environment.GetEnvironmentVariable("SPACETANGENT_2008TC3_XYZV")!;
        Assert.Equal("E831AF7A8BB5DEA137A270D58D2CCB2ED904010F9AC98BEA4AAE37124304996C",
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))));
        SampledStateTrajectory trajectory = SampledStateTrajectory.ReadXyzv(source, Frame);
        Assert.Equal(519, trajectory.Count);
        string fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "sampled-reference.csv");
        foreach (string row in File.ReadLines(fixture).Where(line => !line.StartsWith('#')))
        {
            double[] v = row.Split(',').Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray();
            Assert.True(trajectory.TrySample(JulianInstant.FromJulianDate(v[0], TimeScale.Tdb), out var state));
            Assert.InRange((state.PositionKilometers - new Vector3d(v[1], v[2], v[3])).Length, 0, 2e-7);
            Assert.InRange((state.VelocityKilometersPerSecond - new Vector3d(v[4], v[5], v[6])).Length, 0, 5e-9);
        }
    }

    [RealSamplesFact("SPACETANGENT_MSL_XYZV")]
    public void OriginalMarsScienceLaboratoryCruiseContainsItsPublishedEndpointStates()
    {
        string source = Environment.GetEnvironmentVariable("SPACETANGENT_MSL_XYZV")!;
        Assert.Equal("E0BCB09E212880D1335D83D5EB873FF18C4198FF6815813CE0225ED039FA32DD",
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))));
        SampledStateTrajectory curve = SampledStateTrajectory.ReadXyzv(source, Frame);
        Assert.Equal(286, curve.Count);
        Assert.True(curve.TrySample(curve.Start!.Value, out var first));
        Assert.Equal(new CartesianState(new(65032683.79, 132559071.259, -10953.8376441),
            new(-29.171136, 20.53329, -6.0262131)), first);
        Assert.True(curve.TrySample(curve.End!.Value, out var last));
        Assert.Equal(new CartesianState(new(-129376399.673, -189913895.636, -801889.940791),
            new(20.743298, -11.67124, -0.66514393)), last);
    }

    private sealed class RealSamplesFactAttribute : FactAttribute
    {
        public RealSamplesFactAttribute(string environmentVariable)
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(environmentVariable)))
                Skip = $"Set {environmentVariable} to the original XYZV file.";
        }
    }
}
