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

    [Fact]
    public void CatalogPatchesApplyInOrderWithoutReplacingTheTargetIdentity()
    {
        DirectoryInfo root = Directory.CreateTempSubdirectory("space-tangent-ssc-");
        try
        {
            string basis = Path.Combine(root.FullName, "base.ssc");
            string patch = Path.Combine(root.FullName, "patch.ssc");
            string replace = Path.Combine(root.FullName, "replace.ssc");
            File.WriteAllText(basis, """
                Add Body "Primary:Alias" "Sol/Earth" {
                  EllipticalOrbit { SemiMajorAxis 7000 Period 0.07 Eccentricity 0.1 }
                }
                "Child" "Sol/Earth/Alias" { EllipticalOrbit { SemiMajorAxis 20 Period 0.01 } }
                Location "Primary" "Sol/Earth" { LongLat [ 1 2 3 ] }
                """);
            File.WriteAllText(patch, """
                Modify "Alias" "sol/earth" { Radius 1 }
                Modify "New" "Sol/Earth" { EllipticalOrbit { SemiMajorAxis 8000 Period 0.08 } }
                """);
            File.WriteAllText(replace, """
                Replace "Alias" "Sol/Earth" { EllipticalOrbit { SemiMajorAxis 7500 Period 0.075 } }
                Modify "New" "Sol/Earth" { FixedPosition [ 1 2 3 ] }
                """);
            SscOrbitContext Resolve(string _) => new(Reference, 1, JulianInstant.SecondsPerDay);
            var modified = SscConicOrbits.Read([basis, patch], Resolve);
            Assert.Equal(new[] { "Primary:Alias", "Child", "New" }, modified.Select(item => item.Name));
            Assert.Equal("Sol/Earth/Primary", modified[1].Parent);
            Assert.Equal(6300, modified[0].Orbit.Evaluate(Periapsis).PositionKilometers.Length, 9);

            var replaced = SscConicOrbits.Read([basis, patch, replace], Resolve);
            Assert.Equal(new[] { "Primary:Alias", "Child" }, replaced.Select(item => item.Name));
            Assert.Equal(7500, replaced[0].Orbit.Evaluate(Periapsis).PositionKilometers.Length, 9);
            Assert.Throws<InvalidDataException>(() => SscConicOrbits.Read([basis, basis], Resolve));
        }
        finally
        {
            root.Delete(recursive: true);
        }
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
