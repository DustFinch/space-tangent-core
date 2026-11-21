// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class LensPhysicalContractTests
{
    [Theory]
    [InlineData(-.1)] [InlineData(1)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    public void RayTracingRejectsUnsupportedSpin(double spin)
    {
        Assert.Equal("a", Assert.Throws<ArgumentOutOfRangeException>(() =>
            KerrLens.Trace(spin, new(10, 0, 0), new(-1, 0, 0))).ParamName);
    }

    [Theory]
    [InlineData(0)] [InlineData(.5)] [InlineData(.998)]
    public void InteriorAndRadialRaysRespectCaptureAndOutwardEscape(double spin)
    {
        var interior = KerrLens.Trace(spin, new(.5, 0, 0), new(1, 0, 0));
        Assert.True(interior.Captured);
        Assert.Empty(interior.Disk);
        Assert.True(KerrLens.Trace(spin, new(10, 0, 0), new(-1, 0, 0)).Captured);
        var outward = KerrLens.Trace(spin, new(10, 0, 0), new(1, 0, 0));
        Assert.False(outward.Captured);
        Assert.True(outward.Direction.IsFinite);
        Near(1, outward.Direction.Length, 1e-10);
    }

    [Fact]
    public void RaysPointingAwayFromTheFarSphereKeepTheirInputDirection()
    {
        var away = KerrLens.Trace(.8, new(0, 0, 2000), new(0, 0, 1), 40);
        Assert.False(away.Captured);
        Assert.Empty(away.Disk);
        Assert.Equal(new Vector3d(0, 0, 1), away.Direction);
        var miss = KerrLens.Trace(.8, new(2000, 0, 0), new(0, 1, 0));
        Assert.False(miss.Captured);
        Assert.Equal(new Vector3d(0, 1, 0), miss.Direction);
    }

    [Theory]
    [InlineData(0, -1)] [InlineData(.4, -.5)] [InlineData(.8, 0)] [InlineData(.99, .75)] [InlineData(.9, 1)]
    public void CartesianOblateCoordinatesRecoverTheirAuthoredRadius(double spin, double mu)
    {
        const double radius = 12;
        double horizontal = Math.Sqrt(radius * radius + spin * spin) * Math.Sqrt(1 - mu * mu);
        var point = new Vector3d(horizontal * .6, horizontal * .8, radius * mu);
        Near(radius, KerrLens.Radius(point, spin), 1e-12);
        Near(radius, KerrLens.Radius(point * -1, spin), 1e-12);
    }

    [Fact]
    public void ZeroSpinOrbitsHaveTheirClosedFormLimits()
    {
        Near(2, KerrLens.Horizon(0), 1e-14);
        Near(6, KerrLens.Isco(0), 1e-14);
        Near(1 - Math.Sqrt(8d / 9), KerrLens.Efficiency(0), 1e-14);
        Near(3, KerrLens.PhotonOrbit(0, true), 1e-14);
        Near(3, KerrLens.PhotonOrbit(0, false), 1e-14);
        Near(3 * Math.Sqrt(3), KerrLens.PhotonOrbitImpact(0, 3), 1e-14);
        Near(-3 * Math.Sqrt(3), KerrLens.PhotonOrbitImpact(0, -3), 1e-14);
    }

    [Theory]
    [InlineData(0)] [InlineData(.7)] [InlineData(.998)]
    public void EscapedDiskRaysAreAxiallyCovariantAndMeetTheEmittingRegion(double spin)
    {
        var eye = new Vector3d(20, 0, 10);
        var direction = new Vector3d(.2, .1, -1); direction /= direction.Length;
        var first = KerrLens.Trace(spin, eye, direction, 40);
        var turned = KerrLens.Trace(spin, Rotate(eye), Rotate(direction), 40);
        Assert.False(first.Captured);
        Assert.Equal(first.Captured, turned.Captured);
        Near(0, (Rotate(first.Direction) - turned.Direction).Length, 1e-7);
        Assert.NotEmpty(first.Disk);
        Assert.Equal(first.Disk.Count, turned.Disk.Count);
        for (int index = 0; index < first.Disk.Count; index++)
        {
            var hit = first.Disk[index];
            Assert.InRange(hit.Radius, KerrLens.Isco(spin), 40);
            Assert.True(double.IsFinite(hit.Redshift) && hit.Redshift > 0);
            Assert.Equal(hit.Crossing, turned.Disk[index].Crossing);
            Near(hit.Radius, turned.Disk[index].Radius, 1e-7);
            Near(hit.Redshift, turned.Disk[index].Redshift, 1e-7);
        }
        static Vector3d Rotate(Vector3d value) => new(-value.Y, value.X, value.Z);
    }

    [Theory]
    [InlineData(3)] [InlineData(10)] [InlineData(1000)]
    public void SchwarzschildPeriapsisSatisfiesTheNullGeodesicEquation(double impact)
    {
        double u = SchwarzschildLens.TurningPoint(impact);
        Assert.InRange(u, 0, SchwarzschildLens.PhotonSphere);
        Near(1 / (impact * impact), u * u * (1 - u), 1e-12 / (impact * impact));
    }

    [Theory]
    [InlineData(0)] [InlineData(2)] [InlineData(double.NaN)]
    public void CapturedImpactsDoNotHaveAnExteriorTurningPoint(double impact)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SchwarzschildLens.TurningPoint(impact));
    }

    [Theory]
    [InlineData(.1, 2)] [InlineData(.2, 1.2)] [InlineData(.8, 2.8)]
    public void ShaderAngleApproximationMatchesTheIndependentAdaptiveIntegral(double inverseRadius, double angle)
    {
        double reference = SchwarzschildLens.SweptAngle(inverseRadius, angle);
        double approximate = SchwarzschildLens.ShaderSweptAngle(inverseRadius, angle);
        Assert.True(double.IsFinite(reference));
        Near(reference, approximate, .003);
    }

    [Fact]
    public void AsymptoticSourceLineHasUnitDirectionAndPerpendicularClosestPoint()
    {
        Assert.Null(SchwarzschildLens.SourceLine(.1, .01, new(1, 0, 0), new(0, 1, 0)));
        var line = SchwarzschildLens.SourceLine(.1, 2, new(1, 0, 0), new(0, 1, 0))!.Value;
        Near(1, line.Direction.Length, 1e-12);
        Near(0, Vector3d.Dot(line.Nearest, line.Direction), 1e-12);
    }

    [Fact]
    public void QuadraturePreservesKnownPolynomialIntegrals()
    {
        Near(55, SchwarzschildLens.Integrate(x => x * x * x * x, -2, 3), 1e-8);
        Near(.2, SchwarzschildLens.Gauss16.Sum(pair => pair.Weight * Math.Pow(pair.Node, 4)), 1e-14);
        Near(0, SchwarzschildLens.Deflection(double.PositiveInfinity), 0);
    }

    [Theory]
    [InlineData(1000)] [InlineData(3000)] [InlineData(5778)] [InlineData(10000)] [InlineData(1000000)]
    public void BlackbodyRgbHasNonnegativeChannelsAndUnitPhotopicLuminance(double temperature)
    {
        var (r, g, b) = BlackbodyColor.LinearRgb(temperature);
        Assert.True(double.IsFinite(r) && double.IsFinite(g) && double.IsFinite(b));
        Assert.True(r >= 0 && g >= 0 && b >= 0);
        Near(1, .2126729 * r + .7151522 * g + .0721750 * b, 1e-12);
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    public void NonphysicalTemperaturesAreRejected(double temperature)
    {
        Assert.Equal("kelvin", Assert.Throws<ArgumentOutOfRangeException>(() => BlackbodyColor.Tristimulus(temperature)).ParamName);
    }

    private static void Near(double expected, double actual, double tolerance) =>
        Assert.InRange(Math.Abs(expected - actual), 0, tolerance);
}
