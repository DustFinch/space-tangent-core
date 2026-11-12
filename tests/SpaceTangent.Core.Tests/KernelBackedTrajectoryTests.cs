// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Spice;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class KernelBackedTrajectoryTests
{
    [SatelliteKernelFact]
    [Trait("Category", "ExternalData")]
    public void JoinsTheRealTitanKernelToTheAnalyticOrbitAtBothEnds()
    {
        var kernel = new SpkKernel(Environment.GetEnvironmentVariable("SPACETANGENT_SATELLITE_SPK")!);
        var ranges = kernel.GetCoverage(606, 6);
        Assert.NotEmpty(ranges);
        var trajectory = new KernelBackedTrajectory(kernel, 606, 6, Analytic, 86_400);
        var first = ranges[0].Start;
        var last = ranges[^1].End;
        var modern = JulianInstant.FromJulianDate(2460600.5, TimeScale.Tdb);
        Assert.True(kernel.TryState(606, 6, modern, out CartesianState precise));
        Assert.Equal(precise, trajectory.Sample(modern));
        foreach (var (boundary, sign) in new[] { (first, -1), (last, 1) })
        {
            Assert.True(kernel.TryState(606, 6, boundary, out CartesianState at));
            var near = trajectory.Sample(boundary.AddSeconds(sign * .125));
            Vector3d predicted = at.PositionKilometers + at.VelocityKilometersPerSecond * (sign * .125);
            Assert.True((near.PositionKilometers - predicted).Length < .002);
            Assert.True((near.VelocityKilometersPerSecond - at.VelocityKilometersPerSecond).Length < .00005);
            var far = boundary.AddSeconds(sign * 90_000);
            Assert.Equal(Analytic(far), trajectory.Sample(far));
        }
        Assert.Empty(kernel.GetCoverage(-1, 6));

        static CartesianState Analytic(JulianInstant epoch) => AnalyticSatelliteOrbit.Sample(SatelliteTheory.Titan, epoch);
    }

    private sealed class SatelliteKernelFactAttribute : FactAttribute
    {
        public SatelliteKernelFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_SATELLITE_SPK")))
                Skip = "Set SPACETANGENT_SATELLITE_SPK to the pinned Titan satellite kernel.";
        }
    }
}
