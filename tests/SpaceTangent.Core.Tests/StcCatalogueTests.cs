// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;
using Xunit;
using Xunit.Abstractions;

namespace SpaceTangent.Core.Tests;

public sealed class StcCatalogueTests(ITestOutputHelper output)
{
    [RealStcFact]
    [Trait("Category", "ExternalData")]
    public void OriginalStellarFilesUpdateAliasesUnitsAndOrbitalSystems()
    {
        var archive = StellarCatalogue.LoadArchive(Input("SPACETANGENT_STELLAR_ARCHIVE")!);
        var imported = archive.WithStc(Input("SPACETANGENT_STC_ROOT")!);
        output.WriteLine($"Applied {imported.AppliedRecords} effective records, diagnostics {imported.DiagnosticCount}, catalogue size {imported.Catalogue.Stars.Count}.");
        foreach (string warning in imported.Warnings)
            output.WriteLine(warning);
        Assert.InRange(imported.AppliedRecords, 5000, 100_000);
        var catalogue = imported.Catalogue;
        StellarEntry proxima = Assert.IsType<StellarEntry>(catalogue.FindByName("Gliese 551"));
        Assert.Equal(new ObjectId("hip/70890"), proxima.Id);
        Assert.Equal(107_137.8, proxima.RadiusKilometers, 3);
        Assert.InRange(Math.Abs(proxima.PositionParsecs.Length * StellarEntry.KilometersPerParsec /
            StellarEntry.KilometersPerLightYear - 4.2465), 0, 1e-12);
        StellarEntry sirius = Assert.IsType<StellarEntry>(catalogue.FindByName("Dog Star"));
        StellarEntry a = Assert.IsType<StellarEntry>(catalogue.FindByName("Sirius A"));
        StellarEntry b = Assert.IsType<StellarEntry>(catalogue.FindByName("Sirius B"));
        Assert.True(sirius.IsBarycenter);
        Assert.Equal(sirius.Id, a.BarycenterId);
        Assert.Equal(sirius.Id, b.BarycenterId);
        Assert.Equal(5633.7786, b.RadiusKilometers, 3);
        StellarEntry errai = Assert.IsType<StellarEntry>(catalogue.FindByName("GAM Cep A"));
        Assert.Equal(new ObjectId("hip/116727"), errai.BarycenterId);
        Assert.True(catalogue.Find(new("hip/116727"))?.IsBarycenter);
        StellarEntry kepler = Assert.IsType<StellarEntry>(catalogue.FindByName("Kepler-931"));
        Assert.True(kepler.IsBarycenter);
        foreach (var entry in new[] { kepler, catalogue.FindByName("Kepler-931 A")!, catalogue.FindByName("Kepler-931 B")! })
        {
            var vector = Assert.IsType<StellarEntry>(entry).PositionParsecs;
            double rightAscension = (Math.Atan2(vector.Y, vector.X) * 180 / Math.PI + 360) % 360;
            Assert.Equal(117.13269625, rightAscension, 7);
        }
        Assert.Equal(3_498_000_000, catalogue.Find(new("hip/102276"))?.RadiusKilometers);
        var date = JulianInstant.FromJulianDate(2460600.5, TimeScale.Tdb);
        Assert.InRange(Math.Abs((a.StateAt(date) - b.StateAt(date)).PositionKilometers.Length - 4563718784.429464), 0, .05);
    }

    [SupplementaryStcFact]
    [Trait("Category", "ExternalData")]
    public void SupplementaryAstrometryPositionsMissingOriginalModifications()
    {
        var supplemental = StellarCatalogue.Load(Input("SPACETANGENT_SPATIAL_STARS")!);
        var archive = StellarCatalogue.LoadArchive(Input("SPACETANGENT_STELLAR_ARCHIVE")!, supplemental);
        var before = archive.WithStc(Input("SPACETANGENT_STC_ROOT")!);
        var after = archive.WithStc(Input("SPACETANGENT_STC_ROOT")!, supplemental, default);
        var id = new ObjectId("hip/3401");
        Assert.Null(before.Catalogue.Find(id));
        var recovered = Assert.IsType<StellarEntry>(after.Catalogue.Find(id));
        Assert.Equal(supplemental.Find(id)!.PositionParsecs, recovered.PositionParsecs);
        Assert.NotEqual(supplemental.Find(id)!.RadiusKilometers, recovered.RadiusKilometers);
        Assert.Equal(before.Catalogue.Find(new("hip/70890"))!.PositionParsecs,
            after.Catalogue.Find(new("hip/70890"))!.PositionParsecs);
        output.WriteLine($"Applied {after.AppliedRecords} effective records, diagnostics {after.DiagnosticCount}.");
        Assert.Equal(26, after.AppliedRecords - before.AppliedRecords);
        Assert.Equal(20, after.DiagnosticCount);
        var originalPlanets = ExoplanetCatalogue.Load(Input("SPACETANGENT_EXOPLANET_CATALOGUE")!);
        var planets = originalPlanets.WithStellarHosts(after.Catalogue);
        var keplerA = after.Catalogue.FindByName("Kepler-931 A")!;
        var keplerB = after.Catalogue.FindByName("Kepler-931 B")!;
        var planetId = new ObjectId("exo/4294958019/kepler-931-ab");
        var planet = Assert.IsType<ExoplanetEntry>(planets.Find(planetId));
        Assert.Equal(keplerA.Id, planets.CanonicalHost(planet.Host));
        Assert.Contains(planet, planets.BodiesOf(keplerA.Id));
        Assert.DoesNotContain(planet, planets.BodiesOf(keplerB.Id));
        Assert.Equal(6462, planets.Bodies.Count);
    }

    private sealed class SupplementaryStcFactAttribute : RealStcFactAttribute
    {
        public SupplementaryStcFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Input("SPACETANGENT_SPATIAL_STARS")) ||
                string.IsNullOrWhiteSpace(Input("SPACETANGENT_EXOPLANET_CATALOGUE")))
                Skip = "Set SPACETANGENT_SPATIAL_STARS to the supplementary astrometry catalogue.";
        }
    }

    private static string? Input(string name) => Environment.GetEnvironmentVariable(name);

    private class RealStcFactAttribute : FactAttribute
    {
        public RealStcFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Input("SPACETANGENT_STC_ROOT")) ||
                string.IsNullOrWhiteSpace(Input("SPACETANGENT_STELLAR_ARCHIVE")))
                Skip = "Set SPACETANGENT_STC_ROOT and SPACETANGENT_STELLAR_ARCHIVE to the verified inputs.";
        }
    }
}
