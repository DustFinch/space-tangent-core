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

    [OriginalSpacecraftFact]
    [Trait("Category", "ExternalData")]
    public void OriginalConicSpacecraftKeepMissionWindowsAndModels()
    {
        var bodies = SscBodyCatalogue.Load(Environment.GetEnvironmentVariable("SPACETANGENT_STANDARD_CONIC")!);
        var hubble = Check("Hubble", "Sol/Earth", "hubble.cmod", "2020-01-01T00:00:00Z", 6900, 6950);
        Assert.Equal("Hubble:HST:1990-037B", hubble.Names);
        var launch = At("1990-04-25T02:02:00Z");
        Assert.Null(hubble.Sample(launch.AddSeconds(-1)));
        Assert.NotNull(hubble.Sample(launch));

        var mir = Check("Mir", "Sol/Earth", "mir.cmod", "2000-01-01T00:00:00Z", 6768, 6770);
        Assert.NotNull(mir.Sample(At("2001-03-23T06:00:28Z")));
        Assert.Null(mir.Sample(At("2001-03-23T06:00:29Z")));

        Assert.Equal(7, bodies.Count(body => body.Name.StartsWith("Apollo ", StringComparison.Ordinal)));
        var apollo = Check("Apollo 11", "Sol/Earth/Moon", "apollo.3ds", "1969-07-20T12:00:00Z", 1700, 2100);
        Assert.Null(apollo.Sample(At("1969-07-19T17:21:49Z")));
        Assert.Null(apollo.Sample(At("1969-07-22T04:55:43Z")));

        var odyssey = Check("Mars Odyssey", "Sol/Mars", "marsodyssey.3ds", "2002-12-15T00:00:00Z", 3760, 3840);
        Assert.Equal(FrameAxis.Y, odyssey.Orientation!.Primary.Axis);
        Assert.Equal(DirectionQuantity.Velocity, odyssey.Orientation.Secondary.Quantity);
        Check("MGS", "Sol/Mars", "marsglobalsurvr.3ds", "2001-03-04T18:12:26Z", 3720, 3810);

        SscBodyDefinition Check(string name, string center, string mesh, string time, double minRadius, double maxRadius)
        {
            var body = Assert.Single(bodies, item => item.Name == name);
            Assert.Equal(center, body.Center);
            Assert.Equal(mesh, Path.GetFileName(body.Mesh));
            var state = Assert.IsType<CartesianState>(body.Sample(At(time)));
            Assert.InRange(state.PositionKilometers.Length, minRadius, maxRadius);
            Assert.InRange(state.VelocityKilometersPerSecond.Length, 1, 9);
            return body;
        }

        static JulianInstant At(string value) => JulianInstant.FromJulianDate(2440587.5, TimeScale.Tdb)
            .AddSeconds(DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeSeconds());
    }

    private sealed class OriginalSpacecraftFactAttribute : FactAttribute
    {
        public OriginalSpacecraftFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_STANDARD_CONIC")))
                Skip = "Set SPACETANGENT_STANDARD_CONIC to the original conic spacecraft catalogues.";
        }
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
