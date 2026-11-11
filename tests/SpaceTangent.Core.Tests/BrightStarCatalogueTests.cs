// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class BrightStarCatalogueTests
{
    private const string Header = "hip\tname\tra_rad\tdec_rad\tmu_alpha_cos_delta_rad_y\tmu_delta_rad_y\tv_mag\n";

    [Fact]
    public void StableIdsAndMagnitudeOrderAreIndependentOfInputOrder()
    {
        var catalogue = BrightStarCatalogue.Read(new StringReader(Header +
            "1\tDim\t0\t0\t0\t0\t5\n2\tBright\t1\t0\t0\t0\t1\n"));
        Assert.Equal(new[] { 2, 1 }, catalogue.Stars.Select(star => star.Hipparcos));
        Assert.Same(catalogue.Stars[0], catalogue.Find(new("hip/2")));
        Assert.Null(catalogue.Find(new("hip/3")));
    }

    [Fact]
    public void ProperMotionAppliesOnTheTangentPlaneAndNormalizesTheResult()
    {
        var catalogue = BrightStarCatalogue.Read(new StringReader(Header + "1\tTest\t0\t0\t0.0001\t-0.0002\t5\n"));
        BrightStar star = Assert.Single(catalogue.Stars);
        var epoch = new JulianInstant(JulianInstant.J2000Day, 0, TimeScale.Tdb);
        Assert.Equal(new Vector3d(1, 0, 0), star.DirectionAt(epoch));
        Vector3d later = star.DirectionAt(epoch.AddSeconds(31_557_600 * 20));
        Vector3d earlier = star.DirectionAt(epoch.AddSeconds(-31_557_600 * 20));
        Assert.InRange(later.Length, 1 - 1e-15, 1 + 1e-15);
        Assert.InRange(later.Y / later.X, .002 - 1e-15, .002 + 1e-15);
        Assert.InRange(later.Z / later.X, -.004 - 1e-15, -.004 + 1e-15);
        Assert.Equal(later.X, earlier.X);
        Assert.Equal(later.Y, -earlier.Y);
        Assert.Equal(later.Z, -earlier.Z);
    }

    [Theory]
    [InlineData("1\tName\tNaN\t0\t0\t0\t0\n")]
    [InlineData("1\tName\t0\t2\t0\t0\t0\n")]
    [InlineData("1\tName\t0\t0\t0\t0\t0\n1\tDuplicate\t0\t0\t0\t0\t0\n")]
    public void RejectsMalformedAstrometryAndDuplicateIdentifiers(string rows) =>
        Assert.Throws<InvalidDataException>(() => BrightStarCatalogue.Read(new StringReader(Header + rows)));
}
