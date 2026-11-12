// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class GroundSkyConditionsTests
{
    [Fact]
    public void RefractionAndExtinctionFollowTheStandardAtmosphereAndBortleLimits()
    {
        var site = new EarthSite(0, 0, 0);
        var conditions = new GroundSkyConditions(4, PressureHectopascals: 1010);
        double horizon = conditions.ApparentAltitude(0, site);
        Assert.InRange(horizon, .482, .484);
        Assert.Equal(-3, conditions.ApparentAltitude(-3, site));
        Assert.Equal(90, conditions.ApparentAltitude(90, site));
        Assert.Equal(0, (conditions with { Refraction = false }).ApparentAltitude(0, site));
        var expected = new GroundSkyConditions(4);
        var paranal = new EarthSite(-24.6272, -70.4042, 2.635);
        Assert.InRange(expected.PressureAt(paranal), 730, 740);
        Assert.InRange(expected.ApparentAltitude(0, paranal), .345, .36);
        Vector3d lifted = conditions.RefractDirection(new(1, 0, 0), new(0, 0, 1), site);
        Assert.Equal(horizon, Math.Asin(lifted.Z) * 180 / Math.PI, 10);
        Assert.InRange(GroundSkyConditions.Airmass(1), 26.2, 26.4);
        Assert.InRange(GroundSkyConditions.Airmass(90), .999, 1.001);
        Assert.True(expected.AttenuationMagnitude(5) > expected.AttenuationMagnitude(40));
        var pristine = new GroundSkyConditions(1);
        var city = new GroundSkyConditions(9);
        Assert.Equal(7.8, pristine.LimitingMagnitude());
        Assert.Equal(4.0, city.LimitingMagnitude());
        Assert.True(city.ZenithGlow() > 100 * pristine.ZenithGlow());
        Assert.False((expected with { TemperatureCelsius = double.NaN }).IsValid());
        Assert.False((expected with { PressureHectopascals = double.NaN }).IsValid());
        Assert.False((expected with { Bortle = 0 }).IsValid());
        var noTwinkle = expected with { Scintillation = false };
        Assert.Equal(1, noTwinkle.ScintillationFactor(5, 10, 0));
        double horizonTwinkle = expected.ScintillationFactor(1, 0, Math.PI / 2);
        Assert.InRange(horizonTwinkle, 1.04, 1.40);
        Assert.True(horizonTwinkle > expected.ScintillationFactor(90, 0, Math.PI / 2));
    }
}
