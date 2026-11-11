// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using System.Security.Cryptography;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Orbits;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class ConicOrbitTests
{
    private static readonly JulianInstant Periapsis = new(JulianInstant.J2000Day, 0, TimeScale.Tdb);
    private static readonly FrameId Reference = new("inertial-reference");

    public static IEnumerable<object[]> ReferenceStates() =>
        File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "conic-reference.csv"))
            .Where(line => !line.StartsWith('#')).Select(line => new object[] { line });

    [Theory]
    [MemberData(nameof(ReferenceStates))]
    public void AllConicFamiliesMatchIndependentSpiceStates(string row)
    {
        double[] values = row.Split(',').Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        var orbit = new ConicOrbit(new(values[0], values[1], values[2], values[3], values[4], Periapsis, values[5], Reference));
        CartesianState state = orbit.Evaluate(Periapsis.AddSeconds(values[6]));
        var expectedPosition = new Vector3d(values[7], values[8], values[9]);
        var expectedVelocity = new Vector3d(values[10], values[11], values[12]);
        Assert.InRange((state.PositionKilometers - expectedPosition).Length, 0, Math.Max(2e-6, expectedPosition.Length * 2e-11));
        Assert.InRange((state.VelocityKilometersPerSecond - expectedVelocity).Length, 0, 2e-9);
    }

    [Fact]
    public void TinyEpochDifferencesRetainPhysicalMotion()
    {
        var orbit = new ConicOrbit(new(7000, 0, 0, 0, 0, Periapsis, 398600.4418, Reference));
        CartesianState start = orbit.Evaluate(Periapsis);
        CartesianState next = orbit.Evaluate(Periapsis.AddNanoseconds(1));
        Assert.Equal(7.546053290107542e-9, next.PositionKilometers.Y, 1e-20);
        Assert.Equal(start.PositionKilometers.X, next.PositionKilometers.X);
        Assert.Throws<ArgumentException>(() => orbit.Evaluate(new JulianInstant(Periapsis.Day, 0, TimeScale.Tai)));
    }

    [RealIssFact]
    public void ReadsOrbitalElementsFromTheOriginalIssCatalog()
    {
        string path = Environment.GetEnvironmentVariable("SPACETANGENT_ISS_SSC")!;
        Assert.Equal("DD7885131C2D75D8D55ADD0363072D939C1F4B4C83215223B067D8A2EE764CEC",
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        SscConicOrbit iss = Assert.Single(SscConicOrbits.Read(path,
            _ => new SscOrbitContext(new FrameId("source-iss-reference"), 1, JulianInstant.SecondsPerDay)));
        Assert.Equal("ISS:International Space Station", iss.Name);
        Assert.Equal("Sol/Earth", iss.Parent);
        CartesianState state = iss.Orbit.Evaluate(iss.ElementEpoch);
        Assert.InRange(state.PositionKilometers.Length, 6792, 6794);
        Assert.InRange(state.VelocityKilometersPerSecond.Length, 7.65, 7.67);
    }

    private sealed class RealIssFactAttribute : FactAttribute
    {
        public RealIssFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_ISS_SSC")))
                Skip = "Set SPACETANGENT_ISS_SSC to the original ISS catalogue.";
        }
    }
}
