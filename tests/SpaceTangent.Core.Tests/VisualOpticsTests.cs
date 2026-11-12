// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Core.Observing;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class VisualOpticsTests
{
    [Fact]
    public void TheArchivedReflectorAndPlosslGiveTheExpectedFieldAndSeeing()
    {
        var optics = new VisualOptics(new("Reflector", 200, 1200), new("Plössl", 25, 52));
        Assert.True(optics.IsValid());
        Assert.Equal(48, optics.Magnification());
        Assert.Equal(52.0 / 48, optics.TrueFieldDegrees());
        Assert.Equal(200.0 / 48, optics.ExitPupilMillimeters());
        Assert.InRange(optics.RayleighArcseconds(), .69, .70);
        Assert.InRange(optics.DiffractionArcseconds(), .58, .59);
        Assert.InRange(optics.LimitingMagnitude(), 14, 14.01);
        Assert.Equal(optics.DiffractionArcseconds(), optics.BlurArcseconds(false));
        Assert.Equal(0, optics.WanderArcseconds(false));
        Assert.InRange(optics.WanderArcseconds(true), .6, .8);
        Assert.True(optics.BlurArcseconds(true) > optics.BlurArcseconds(false));
        Assert.Equal(optics.TrueFieldDegrees() / 2, (optics with { Barlow = 2 }).TrueFieldDegrees());
        Assert.False((optics with { SeeingArcseconds = double.NaN }).IsValid());
        Assert.False((optics with { Eyepiece = new("Invalid", 0, 60) }).IsValid());
    }
}
