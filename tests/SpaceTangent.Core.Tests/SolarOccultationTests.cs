// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class SolarOccultationTests
{
    [Fact]
    public void UniformDiscsGiveTotalAnnularPartialAndDisjointCases()
    {
        const double sunDistance = 150_000_000;
        const double distance = 400_000;
        double solarAngle = Math.Asin(SolarOccultation.SunRadiusKilometers / sunDistance);
        var sun = new Vector3d(sunDistance, 0, 0);
        var ahead = new Vector3d(distance, 0, 0);
        var partial = new Vector3d(distance * Math.Cos(solarAngle), distance * Math.Sin(solarAngle), 0);
        double apparentEqual = distance * Math.Sin(solarAngle);

        Assert.Equal(0, SolarOccultation.VisibleFraction(sun, ahead, apparentEqual * 1.1));
        Assert.Equal(.75, SolarOccultation.VisibleFraction(sun, ahead, distance * Math.Sin(solarAngle / 2)), 12);
        Assert.Equal(1d / 3 + Math.Sqrt(3) / (2 * Math.PI),
            SolarOccultation.VisibleFraction(sun, partial, apparentEqual), 12);
        Assert.Equal(1, SolarOccultation.VisibleFraction(sun, ahead * -1, apparentEqual));
        Assert.Equal(1, SolarOccultation.VisibleFraction(sun, ahead * 500, apparentEqual));
    }
}
