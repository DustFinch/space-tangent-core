// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class LunarIlluminationTests
{
    [Fact]
    public void FullMoonIsBrightOutsideTheUmbraAndDarkDuringTotality()
    {
        var sun = new Vector3d(149_597_870.7, 0, 0);
        double angle = Math.PI / 60;
        var bright = new Vector3d(-384_400 * Math.Cos(angle), 384_400 * Math.Sin(angle), 0);
        var totality = new Vector3d(-384_400, 0, 0);
        var newMoon = new Vector3d(384_400, 0, 0);

        Assert.InRange(EarthSurfaceLighting.RelativeLunarFlux(sun, bright), .99, 1.01);
        Assert.Equal(0, EarthSurfaceLighting.RelativeLunarFlux(sun, totality));
        Assert.InRange(EarthSurfaceLighting.RelativeLunarFlux(sun, newMoon), 0, 1e-15);
    }

    [Fact]
    public void MoonlightFollowsTheInverseSquareOfDistance()
    {
        var sun = new Vector3d(149_597_870.7, 0, 0);
        var moon = new Vector3d(-382_000, 20_000, 0);
        double close = EarthSurfaceLighting.RelativeLunarFlux(sun, moon);
        double far = EarthSurfaceLighting.RelativeLunarFlux(sun, moon * 2);
        Assert.InRange(close / far, 3.99, 4.03);
        Assert.Equal(0, EarthSurfaceLighting.RelativeLunarFlux(sun, default));
    }
}
