// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Time;
using SpaceTangent.Core.World;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class SscPrecessionTests
{
    [PrecessionFact]
    [Trait("Category", "ExternalData")]
    public void HalleyPrecessesAroundTheEclipticPole()
    {
        var bodies = SscBodyCatalogue.Load(Corpus);
        var comet = Assert.Single(bodies, body => body.Name == "1P Halley");
        Assert.Equal("halley.cmod", Path.GetFileName(comet.Mesh));
        Assert.NotNull(comet.SurfaceMap);
        var epoch = JulianInstant.FromJulianDate(2451545, TimeScale.Tdb);
        var frame = Ecliptic(new(1, 0, 0), new(0, 0, 1));
        CheckAttitude(comet.SampleRotation!, epoch, frame, 170 * 3600, .01 * 365.25 * 86400, 66, 0, 0, 0);
        CheckAttitude(comet.SampleRotation!, epoch, frame, 170 * 3600, .01 * 365.25 * 86400, 66, 0, 0, .01 * 365.25 * 86400 / 4);
    }

    [PrecessionFact]
    [Trait("Category", "ExternalData")]
    public void ApophisKeepsRetrogradeSpinAndItsConstantFrameAcrossAllThreePhases()
    {
        var bodies = SscBodyCatalogue.Load(Corpus);
        var asteroid = Assert.Single(bodies, body => body.Name == "99942 Apophis");
        Assert.Equal("apophis.cmod", Path.GetFileName(asteroid.Mesh));
        Assert.NotNull(asteroid.SurfaceMap);
        ObjectId id = new("ssc/sol/99942-apophis");
        var mission = asteroid.BindTimeline!(id, path => path == "Sol" ? SolarSystemSample.SunId : throw new ArgumentException(path));
        Assert.Equal(3, mission.Arcs.Count);

        var builder = new WorldGraphBuilder();
        builder.AddTrajectory(SolarSystemSample.SunId, _ => new CartesianState(default, default));
        builder.AddSampledMission(mission);
        var graph = builder.Build();
        var frame = Ecliptic(new(.3935, .918, -.0488), new(-.0192, -.0449, -.9988));
        var epoch = JulianInstant.FromJulianDate(2456300.555, TimeScale.Tdb);
        foreach (double day in new[] { 2456300.555, 2461771.4999, 2461771.5001, 2462200.1, 2462867.4999, 2462867.5001 })
        {
            var time = JulianInstant.FromJulianDate(day, TimeScale.Tdb);
            double elapsed = (double)time.DifferenceNanoseconds(epoch) / JulianInstant.NanosecondsPerSecond;
            CheckAttitude(Sample, epoch, frame, -262.168 * 3600, -27.37430 * 3600, 19.9, 133, 21, elapsed);
        }

        FrameKinematics? Sample(JulianInstant time)
        {
            var snapshot = graph.Evaluate(time);
            Assert.True(snapshot.TryState(id, out _));
            return snapshot.TryOrientation(id, out var value) ? value : null;
        }
    }

    [Theory]
    [InlineData("Period <min> -10", -600)]
    [InlineData("Period<d> -1", -86400)]
    [InlineData("", 172800)]
    [InlineData("Period 0", 0)]
    public void SupportsSignedUnitsSynchronousDefaultAndZeroSpin(string period, double expectedSeconds)
    {
        string root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "spin.ssc"), $$"""
                "Probe" "Sol/Earth" {
                    EllipticalOrbit { Period 2 SemiMajorAxis 10000 }
                    BodyFrame { EquatorJ2000 {} }
                    PrecessingRotation {
                        {{period}}
                        PrecessionPeriod<s> 12000
                        Inclination 45
                    }
                }
                """);
            var spin = Assert.Single(SscBodyCatalogue.Load(root)).SampleRotation!;
            var epoch = JulianInstant.FromJulianDate(2451545, TimeScale.Tdb);
            CheckAttitude(spin, epoch, Basis3d.Identity, expectedSeconds, 12000, 45, 0, 0, 250);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string Corpus => Environment.GetEnvironmentVariable("SPACETANGENT_PRECESSING_SSC")!;

    private static Basis3d Ecliptic(Vector3d x, Vector3d z)
    {
        var (sine, cosine) = Math.SinCos(23.4392911 * Math.PI / 180);
        Vector3d Rotate(Vector3d vector) =>
            new(vector.X, vector.Y * cosine - vector.Z * sine, vector.Y * sine + vector.Z * cosine);
        x = Rotate(x / x.Length);
        Vector3d y = Vector3d.Cross(Rotate(z), x);
        y /= y.Length;
        return new(x, y, Vector3d.Cross(x, y));
    }

    private static void CheckAttitude(Func<JulianInstant, FrameKinematics?> sample, JulianInstant epoch,
        Basis3d frame, double spinPeriod, double precessionPeriod, double inclination, double node, double meridian, double elapsed)
    {
        double spinRate = spinPeriod == 0 ? 0 : Math.Tau / spinPeriod;
        double precessionRate = spinPeriod == 0 || precessionPeriod == 0 ? 0 : -Math.Tau / precessionPeriod;
        double Angle(double degrees) => spinPeriod == 0 ? degrees / 180 * Math.PI : (float)(degrees / 180 * Math.PI);
        var (sineNode, cosineNode) = Math.SinCos(Angle(node) + elapsed * precessionRate);
        var (sineInclination, cosineInclination) = Math.SinCos(Angle(inclination));
        var (sineSpin, cosineSpin) = Math.SinCos(Math.PI + Angle(meridian) + elapsed * spinRate);
        Vector3d lineOfNodes = frame.Transform(new(cosineNode, sineNode, 0));
        Vector3d tangent = frame.Transform(new(-sineNode * cosineInclination, cosineNode * cosineInclination, sineInclination));
        Vector3d pole = Vector3d.Cross(lineOfNodes, tangent);
        var time = epoch.AddSeconds(elapsed);
        var actual = Assert.IsType<FrameKinematics>(sample(time));
        Assert.InRange((actual.ToWorld.X - (lineOfNodes * cosineSpin + tangent * sineSpin)).Length, 0, 2e-9);
        Assert.InRange((actual.ToWorld.Z - pole).Length, 0, 2e-9);
        var angular = Assert.IsType<Vector3d>(actual.AngularVelocityRadiansPerSecond);
        Assert.InRange((angular - (pole * spinRate + frame.Z * precessionRate)).Length, 0, 1e-10);

        const double step = .01;
        var before = Assert.IsType<FrameKinematics>(sample(time.AddSeconds(-step)));
        var after = Assert.IsType<FrameKinematics>(sample(time.AddSeconds(step)));
        Vector3d derivative = (after.ToWorld.X - before.ToWorld.X) / (2 * step);
        Assert.InRange((derivative - Vector3d.Cross(angular, actual.ToWorld.X)).Length, 0, 1e-9);
    }

    private sealed class PrecessionFactAttribute : FactAttribute
    {
        public PrecessionFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Corpus))
                Skip = "Set the original Halley and Apophis SSC corpus path.";
        }
    }
}
