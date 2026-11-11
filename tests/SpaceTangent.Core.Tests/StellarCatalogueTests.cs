// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class StellarCatalogueTests
{
    private const string Header = "id\tname\tdist_pc\tx_pc\ty_pc\tz_pc\tvx_pc_y\tvy_pc_y\tvz_pc_y\tv_mag\tb_v\tr_km\n";
    private const string Example = "hip/1\tTest\t2\t2\t0\t0\t-0.001\t0.0002\t0\t5\t0.8\t100000\n";

    [Fact]
    public void SpaceMotionRetainsRadialAndTangentialComponents()
    {
        StellarEntry star = Assert.Single(StellarCatalogue.Read(new StringReader(Header + Example)).Stars);
        var j2000 = JulianInstant.FromJulianDate(2451545, TimeScale.Tdb);
        var first = star.StateAt(j2000);
        var later = star.StateAt(j2000.AddSeconds(20 * 31_557_600d));
        Assert.Equal(new Vector3d(2 * StellarEntry.KilometersPerParsec, 0, 0), first.PositionKilometers);
        Assert.InRange(later.PositionKilometers.X / StellarEntry.KilometersPerParsec, 1.98 - 1e-14, 1.98 + 1e-14);
        Assert.InRange(later.PositionKilometers.Y / StellarEntry.KilometersPerParsec, .004 - 1e-14, .004 + 1e-14);
        Assert.Equal(first.VelocityKilometersPerSecond, later.VelocityKilometersPerSecond);
    }

    [Fact]
    public void RejectsAnUnknownDistanceAndDuplicateIdentifier()
    {
        Assert.Throws<InvalidDataException>(() =>
            StellarCatalogue.Read(new StringReader(Header + Example.Replace("\t2\t2\t", "\t100000\t100000\t"))));
        Assert.Throws<InvalidDataException>(() => StellarCatalogue.Read(new StringReader(Header + Example + Example)));
    }

    [SpatialStarsFact]
    [Trait("Category", "ExternalData")]
    public void PinnedCatalogueContainsNearbyStarsAtPublishedScales()
    {
        var catalogue = StellarCatalogue.Load(Environment.GetEnvironmentVariable("SPACETANGENT_SPATIAL_STARS")!);
        Assert.Equal(109_400, catalogue.Stars.Count);
        var proxima = Assert.IsType<StellarEntry>(catalogue.Find(new("hip/70890")));
        var sirius = Assert.IsType<StellarEntry>(catalogue.Find(new("hip/32349")));
        Assert.Equal("Proxima Centauri", proxima.Name);
        var epoch = JulianInstant.FromJulianDate(2451545, TimeScale.Tdb);
        Assert.InRange(proxima.StateAt(epoch).PositionKilometers.Length / StellarEntry.KilometersPerLightYear, 4.20, 4.27);
        Assert.InRange(sirius.StateAt(epoch).PositionKilometers.Length / StellarEntry.KilometersPerLightYear, 8.5, 8.8);
        Assert.InRange(proxima.StateAt(epoch).VelocityKilometersPerSecond.Length, 20, 40);
    }

    private sealed class SpatialStarsFactAttribute : FactAttribute
    {
        public SpatialStarsFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_SPATIAL_STARS")))
                Skip = "Set SPACETANGENT_SPATIAL_STARS to the pinned spatial star catalogue.";
        }
    }
}
