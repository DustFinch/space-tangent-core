// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class ExoplanetCatalogueTests
{
    [RealExoplanetsFact]
    [Trait("Category", "ExternalData")]
    public void OriginalExoplanetCatalogueKeepsHostsAliasesAndOrbits()
    {
        var catalogue = ExoplanetCatalogue.Load(Environment.GetEnvironmentVariable("SPACETANGENT_EXOPLANET_CATALOGUE")!);
        Assert.Equal(6462, catalogue.Bodies.Count);
        Assert.Equal(4885, catalogue.Hosts.Count);
        var proxima = new ObjectId("hip/70890");
        var planets = catalogue.BodiesOf(proxima).ToArray();
        Assert.Equal(2, planets.Length);
        ExoplanetEntry b = Assert.Single(planets, planet => planet.Names.Contains("b"));
        Assert.Equal("rocky", b.TextureClass);
        Assert.Equal(6519, b.RadiusKilometers);
        Assert.Equal(proxima, b.Parent);
        Assert.InRange(catalogue.FindHost(proxima)!.DistanceParsecs, 1.301, 1.303);
        var dimidium = Assert.IsType<ExoplanetEntry>(catalogue.Find(new("exo/113357/dimidium")));
        Assert.Contains("51 Peg b", dimidium.Names);
        Assert.Equal("exo-class4", dimidium.TextureClass);
        Assert.Equal(7_829_952.552, dimidium.SemiMajorAxisKilometers, 6);
        var date = JulianInstant.FromJulianDate(2460600.5008, TimeScale.Tdb);
        Check(b, 7264472.601, 47.23325308362595);
        Check(dimidium, 7826645.803237212, 134.64396381724777);

        var first = dimidium.BodyToIcrf(date);
        var later = dimidium.BodyToIcrf(date.AddSeconds(3600));
        Assert.InRange(Math.Abs(Vector3d.Dot(first.X, first.Z)), 0, 1e-12);
        Assert.InRange((first.Z - later.Z).Length, 0, 1e-12);
        Assert.InRange((first.X - later.X).Length, .06, .063);
        Assert.InRange((b.BodyToIcrf(date).X - b.BodyToIcrf(date.AddSeconds(86400)).X).Length, 0, 1e-12);

        void Check(ExoplanetEntry planet, double radius, double speed)
        {
            CartesianState state = planet.ParentRelativeState(date);
            Assert.InRange(Math.Abs(state.PositionKilometers.Length - radius), 0, .001);
            Assert.InRange(Math.Abs(state.VelocityKilometersPerSecond.Length - speed), 0, 1e-8);
        }
    }

    private sealed class RealExoplanetsFactAttribute : FactAttribute
    {
        public RealExoplanetsFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_EXOPLANET_CATALOGUE")))
                Skip = "Set SPACETANGENT_EXOPLANET_CATALOGUE to the original converted catalogue.";
        }
    }
}
