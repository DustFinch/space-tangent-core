// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Spice;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class SscMinorMoonTests
{
    [ArchiveFact]
    [Trait("Category", "ExternalData")]
    public void LoadsTheUnchangedMinorMoonCatalogueAndEvaluatesItsAuthoredOrbitAndSpin()
    {
        string root = Environment.GetEnvironmentVariable("SPACETANGENT_MINOR_MOONS")!;
        var bodies = SscBodyCatalogue.Load(root);
        Assert.Equal(183, bodies.Count);
        Assert.All(bodies, body => Assert.True(body.RadiusKilometers > 0));
        var check = JulianInstant.FromJulianDate(2460600.5, TimeScale.Tdb);
        Assert.All(bodies, body => Assert.True(body.Sample(check) is { PositionKilometers.IsFinite: true, VelocityKilometersPerSecond.IsFinite: true }, body.Name));

        Assert.Equal(new Vector3d(102.8, 102.8, 70.7), Body("Himalia").EllipsoidRadiiKilometers);
        Assert.Equal(102.8, Body("Himalia").RadiusKilometers);
        Assert.EndsWith("asteroid.jpg", Body("Himalia").SurfaceMap, StringComparison.OrdinalIgnoreCase);
        var metis = Body("Metis");
        Assert.EndsWith("metis.jpg", metis.SurfaceMap, StringComparison.OrdinalIgnoreCase);
        const double period = .2947795 * 86400;
        var epoch = JulianInstant.FromJulianDate(2451891, TimeScale.Tdb);
        var first = Assert.IsType<CartesianState>(metis.Sample(epoch));
        // The source gives longitude of periapsis and mean longitude in the authored two-vector plane.
        var x = Unit(new(.99942, -.03403, 0));
        var guide = new Vector3d(-.01465, -.43042, .90251);
        var z = Unit(guide - x * Vector3d.Dot(x, guide));
        var basis = new Basis3d(x, Vector3d.Cross(z, x), z);
        var (position, velocity) = Ellipse(127979.8, .0002, .06, 196, 110 - 196, 80.13 - 110, period);
        Near(basis.Transform(position), first.PositionKilometers, 2e-5);
        Near(basis.Transform(velocity), first.VelocityKilometersPerSecond, 2e-8);
        var spin = metis.SampleRotation!(epoch)!.Value;
        var quarter = metis.SampleRotation(epoch.AddSeconds(period / 4))!.Value;
        Near(spin.ToWorld.Y, quarter.ToWorld.X, 2e-10);
        Assert.InRange(Math.Abs(spin.AngularVelocityRadiansPerSecond!.Value.Length - Math.Tau / period), 0, 1e-15);
        Assert.Contains("Jupiter XVI", metis.Names, StringComparison.Ordinal);

        var themisto = Body("Themisto");
        var outerEpoch = JulianInstant.FromJulianDate(2451897.5, TimeScale.Tdb);
        var outer = Assert.IsType<CartesianState>(themisto.Sample(outerEpoch));
        (position, velocity) = Ellipse(7405264.419, .203913, 45.483645, 202.317030, 238.408454,
            182.658856, 130.200230 * 86400);
        Near(RotateX(position, Radians(23.4392911)), outer.PositionKilometers, 2e-5);
        Near(RotateX(velocity, Radians(23.4392911)), outer.VelocityKilometersPerSecond, 2e-8);

        string pck = Path.Combine(root, "pck00011.tpc");
        foreach (var (name, id) in new[] { ("Amalthea", 505), ("Thebe", 514) })
        {
            var expected = new SpicePolynomialBodyFrame(pck, id).Sample(epoch);
            Assert.Equal(expected, Body(name).SampleRotation!(epoch));
        }

        SscBodyDefinition Body(string name) => Assert.Single(bodies, body => body.Name == name);
    }

    private static (Vector3d Position, Vector3d Velocity) Ellipse(double axis, double eccentricity, double inclination,
        double node, double periapsis, double anomaly, double period)
    {
        double mean = Radians(anomaly), eccentric = mean;
        for (int index = 0; index < 32; index++)
            eccentric = mean + eccentricity * Math.Sin(eccentric);
        var (sin, cos) = Math.SinCos(eccentric);
        double minor = Math.Sqrt(1 - eccentricity * eccentricity);
        double rate = Math.Tau / (period * (1 - eccentricity * cos));
        return (Axes(new(axis * (cos - eccentricity), axis * minor * sin, 0)),
            Axes(new(-axis * rate * sin, axis * rate * minor * cos, 0)));

        Vector3d Axes(Vector3d vector) => RotateZ(RotateX(RotateZ(vector, Radians(periapsis)), Radians(inclination)), Radians(node));
    }

    private static double Radians(double degrees) => degrees * (Math.PI / 180);
    private static Vector3d Unit(Vector3d vector) => vector / vector.Length;
    private static Vector3d RotateZ(Vector3d v, double angle) =>
        new(v.X * Math.Cos(angle) - v.Y * Math.Sin(angle), v.X * Math.Sin(angle) + v.Y * Math.Cos(angle), v.Z);
    private static Vector3d RotateX(Vector3d v, double angle) =>
        new(v.X, v.Y * Math.Cos(angle) - v.Z * Math.Sin(angle), v.Y * Math.Sin(angle) + v.Z * Math.Cos(angle));

    private static void Near(Vector3d expected, Vector3d actual, double tolerance) =>
        Assert.True((expected - actual).Length <= tolerance, $"Expected {expected}, found {actual}, tolerance {tolerance:G6}.");

    private sealed class ArchiveFactAttribute : FactAttribute
    {
        public ArchiveFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_MINOR_MOONS")))
                Skip = "Set the archived minor moon catalogue and model directory.";
        }
    }
}
