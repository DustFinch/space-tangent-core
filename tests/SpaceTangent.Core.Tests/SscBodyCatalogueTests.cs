// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.World;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class SscBodyCatalogueTests
{
    [Fact]
    public void OrderedPatchesKeepAndReplaceTheEffectiveFields()
    {
        DirectoryInfo root = Directory.CreateTempSubdirectory("st-ssc-");
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "01-base.ssc"), """
                Add "Demo:Alias" "Sol/Earth"
                {
                    Radius 3
                    Color [ 0.2 0.4 0.6 ]
                    EllipticalOrbit { SemiMajorAxis 7000 Period 0.067 }
                }
                """);
            File.WriteAllText(Path.Combine(root.FullName, "02-change.ssc"), """
                Modify "Alias" "Sol/Earth" { Radius 4 }
                """);
            var body = Assert.Single(SscBodyCatalogue.Load(root.FullName));
            Assert.Equal(4, body.RadiusKilometers);
            Assert.Equal(new Vector3d(.2, .4, .6), body.Color);
            Assert.Equal(new Vector3d(7000, 0, 0), body.Sample(Epoch(2451545))!.Value.PositionKilometers);

            File.WriteAllText(Path.Combine(root.FullName, "03-replace.ssc"), """
                Replace "Demo" "Sol/Earth"
                {
                    Radius 5
                    SampledTrajectory { Source "states.xyzv" Interpolation "cubic" }
                }
                """);
            File.WriteAllText(Path.Combine(root.FullName, "states.xyzv"),
                "2451545 8000 0 0 0 1 0\n2451546 8000 86400 0 0 1 0\n");
            body = Assert.Single(SscBodyCatalogue.Load(root.FullName));
            Assert.Equal(5, body.RadiusKilometers);
            Assert.Equal(new Vector3d(.8, .8, .8), body.Color);
            Assert.Equal(new Vector3d(8000, 43200, 0), body.Sample(Epoch(2451545.5))!.Value.PositionKilometers);
            Assert.Null(body.Sample(Epoch(2451547)));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [LocalBodyPackFact]
    [Trait("Category", "ExternalData")]
    public void OriginalStationAndAsteroidArcHaveRealStates()
    {
        var bodies = SscBodyCatalogue.Load(Environment.GetEnvironmentVariable("SPACETANGENT_SSC_ADDON_PACK")!);
        var station = Assert.Single(bodies, item => item.Name == "ISS");
        Assert.EndsWith("iss.cmod", station.Mesh);
        Assert.Equal(FrameAxis.NegativeY, station.Orientation!.Primary.Axis);
        Assert.Equal(DirectionQuantity.Velocity, station.Orientation.Secondary.Quantity);
        Assert.InRange(station.Sample(Epoch(2460634.5))!.Value.PositionKilometers.Length, 6792, 6794);
        var asteroid = Assert.Single(bodies, item => item.Name == "2008 TC3");
        var state = asteroid.Sample(Epoch(2454013.5))!.Value;
        Assert.InRange(state.PositionKilometers.Length, 240_000_000, 250_000_000);
        Assert.InRange(state.VelocityKilometersPerSecond.Length, 19, 21);
    }

    private static JulianInstant Epoch(double day) => JulianInstant.FromJulianDate(day, TimeScale.Tdb);

    private sealed class LocalBodyPackFactAttribute : FactAttribute
    {
        public LocalBodyPackFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_SSC_ADDON_PACK")))
                Skip = "Set SPACETANGENT_SSC_ADDON_PACK to the verified body add-on pack.";
        }
    }
}
