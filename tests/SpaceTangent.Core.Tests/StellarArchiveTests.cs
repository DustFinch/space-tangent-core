// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class StellarArchiveTests
{
    [RealArchiveFact]
    [Trait("Category", "ExternalData")]
    public void ArchivedCompositionNamesAndSiriusOrbitArePreserved()
    {
        var catalogue = StellarCatalogue.LoadArchive(Environment.GetEnvironmentVariable("SPACETANGENT_STELLAR_ARCHIVE")!);
        Assert.Equal(182_625, catalogue.Stars.Count);
        Assert.Equal(500, catalogue.Stars.Count(star => star.IsBarycenter));
        Assert.Equal(642, catalogue.Stars.Count(star => star.BarycenterId.HasValue));
        Assert.Equal(622, catalogue.Stars.Count(star => star.BarycenterId.HasValue && !star.IsBarycenter));
        StellarEntry proxima = Assert.IsType<StellarEntry>(catalogue.FindByName("Proxima Centauri"));
        Assert.Equal(new ObjectId("hip/70890"), proxima.Id);
        Assert.Contains("Gliese 551", proxima.Aliases);
        Assert.Equal(107_137.796875, proxima.RadiusKilometers);
        Vector3d position = proxima.StateAt(JulianInstant.FromJulianDate(2451545, TimeScale.Tdb)).PositionKilometers;
        double ra = Math.Atan2(position.Y, position.X) * 180 / Math.PI + 360;
        double declination = Math.Asin(position.Z / position.Length) * 180 / Math.PI;
        Assert.InRange(Math.Abs(ra - 217.39232147), 0, .00002);
        Assert.InRange(Math.Abs(declination + 62.67607512), 0, .00002);

        StellarEntry a = Assert.IsType<StellarEntry>(catalogue.FindByName("Dog Star A"));
        StellarEntry b = Assert.IsType<StellarEntry>(catalogue.FindByName("Sirius B"));
        StellarEntry center = Assert.IsType<StellarEntry>(catalogue.FindByName("Dog Star"));
        Assert.True(center.IsBarycenter);
        Assert.Equal(center.Id, a.BarycenterId);
        Assert.Equal(center.Id, b.BarycenterId);
        Assert.InRange(center.SystemRadiusKilometers, 3.15e9, 3.17e9);
        Check(2451545, 789130292.2356473, 1601221496.4965277, 14.277794615344717);
        Check(2460600.5, 1506627081.0073006, 3057091703.422164, 6.402557101027925);

        void Check(double jd, double aDistance, double bDistance, double relativeSpeed)
        {
            var epoch = JulianInstant.FromJulianDate(jd, TimeScale.Tdb);
            CartesianState origin = center.StateAt(epoch);
            CartesianState relativeA = a.StateAt(epoch) - origin, relativeB = b.StateAt(epoch) - origin;
            Assert.InRange(Math.Abs(relativeA.PositionKilometers.Length - aDistance), 0, .05);
            Assert.InRange(Math.Abs(relativeB.PositionKilometers.Length - bDistance), 0, .05);
            Assert.InRange(Math.Abs((relativeA - relativeB).VelocityKilometersPerSecond.Length - relativeSpeed), 0, 1e-9);
        }
    }

    private sealed class RealArchiveFactAttribute : FactAttribute
    {
        public RealArchiveFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_STELLAR_ARCHIVE")))
                Skip = "Set SPACETANGENT_STELLAR_ARCHIVE to the verified stars.bin and names directory.";
        }
    }
}
