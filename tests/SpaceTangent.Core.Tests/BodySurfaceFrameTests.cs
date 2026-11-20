// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.World;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class BodySurfaceFrameTests
{
    [Fact]
    public void RotatingOblateBodyHasGeodeticNormalAndSpinVelocity()
    {
        var rotation = new FrameKinematics(Basis3d.Identity, new Vector3d(0, 0, .01));
        var center = new CartesianState(new(100, 200, 300), new(1, 2, 3));
        var frame = BodySurfaceFrame.OnEllipsoid(center, rotation, new(0, 90, 2), 10, 8);
        Assert.InRange((frame.Observer.PositionKilometers - new Vector3d(100, 212, 300)).Length, 0, 1e-12);
        Assert.InRange((frame.Observer.VelocityKilometersPerSecond - new Vector3d(.88, 2, 3)).Length, 0, 1e-12);
        var polar = BodySurfaceFrame.OnEllipsoid(center, rotation, new(90, 0, 1), 10, 8);
        Assert.InRange((polar.Observer.PositionKilometers - new Vector3d(100, 200, 309)).Length, 0, 1e-12);
    }

    [Theory]
    [InlineData(0, 0)] [InlineData(120, 35)] [InlineData(270, -50)]
    public void LocalDirectionsRoundTripOnATiltedBody(double azimuth, double altitude)
    {
        var axes = new Basis3d(new(0, 1, 0), new(0, 0, 1), new(1, 0, 0));
        var frame = BodySurfaceFrame.OnEllipsoid(default, new(axes, null), new(35, -75, .003), 1737.4, 1737.4);
        var direction = frame.LookDirection(azimuth, altitude);
        var sky = frame.Direction(direction)!.Value;
        Assert.InRange(Math.Abs(sky.AltitudeDegrees - altitude), 0, 1e-10);
        Assert.InRange(Math.Abs(sky.AzimuthDegrees!.Value - azimuth), 0, 1e-10);
        Assert.InRange(Vector3d.Dot(Vector3d.Cross(frame.EastNorthUp.X, frame.EastNorthUp.Y), frame.EastNorthUp.Z), .999999999, 1.000000001);
        Assert.Null(frame.Direction(default));
    }
}
