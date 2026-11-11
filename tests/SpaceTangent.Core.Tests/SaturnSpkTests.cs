// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using System.Security.Cryptography;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Spice;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class SaturnSpkTests
{
    [SaturnFact]
    public void OriginalSat480MatchesCspiceAtMissionEpochsAndCoverageBoundaries()
    {
        string kernel = Environment.GetEnvironmentVariable("SPACETANGENT_SAT480")!;
        Assert.Equal("12D2CFD8DA371883BD4EDBD67C31FAFA98CA7779A09DA6819E29D53CDC6DAF32",
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(kernel))));
        var ephemeris = new SpkKernel(kernel);
        string reference = Path.Combine(AppContext.BaseDirectory, "Fixtures", "saturn-center-reference.csv");
        foreach (string line in File.ReadLines(reference).Where(line => !line.StartsWith('#')))
        {
            double[] values = line.Split(',').Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray();
            Assert.True(ephemeris.TryState(699, 6, JulianInstant.FromTdbSeconds(values[0]), out var state));
            var position = new Vector3d(values[1], values[2], values[3]);
            var velocity = new Vector3d(values[4], values[5], values[6]);
            Assert.InRange((state.PositionKilometers - position).Length, 0, 2e-8);
            Assert.InRange((state.VelocityKilometersPerSecond - velocity).Length, 0, 2e-12);
        }
        Assert.False(ephemeris.TryState(699, 6, JulianInstant.FromTdbSeconds(-1577880000).AddNanoseconds(-1), out _));
        Assert.False(ephemeris.TryState(699, 6, JulianInstant.FromTdbSeconds(1577880000).AddNanoseconds(1), out _));
        Assert.False(ephemeris.TryState(699, 399, JulianInstant.FromTdbSeconds(141917558.4203589), out _));
    }

    private sealed class SaturnFactAttribute : FactAttribute
    {
        public SaturnFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_SAT480")))
                Skip = "Set SPACETANGENT_SAT480 to the published SAT480 kernel.";
        }
    }
}
