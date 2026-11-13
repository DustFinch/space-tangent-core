// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class DeepSkyIndexTests
{
    [RealDscFact]
    [Trait("Category", "ExternalData")]
    public void OriginalGalaxiesAndClustersRetainCoordinatesAndAliases()
    {
        var index = DeepSkyIndex.Load(Environment.GetEnvironmentVariable("SPACETANGENT_DSC_ROOT")!);
        Assert.Equal(10_937, index.Objects.Count(body => body.Kind == DeepSkyKind.Galaxy));
        Assert.Equal(196, index.Objects.Count(body => body.Kind == DeepSkyKind.GlobularCluster));
        Assert.Equal(6_476, index.Objects.Count(body => body.Kind == DeepSkyKind.OpenCluster));

        DeepSkyObject andromeda = Assert.IsType<DeepSkyObject>(index.Find(new("dso/m31")));
        Assert.Equal("Andromeda Galaxy", andromeda.Name);
        Assert.Contains("NGC 224", andromeda.Aliases);
        Assert.Contains("M 31", andromeda.Aliases);
        Assert.Contains("M31", andromeda.Aliases);
        Assert.Equal("Sb", andromeda.Morphology);
        Assert.Equal(-20.12, andromeda.AbsoluteMagnitude);
        Assert.Equal(10.683, andromeda.RightAscensionDegrees, 9);
        Assert.Equal(41.2689, andromeda.DeclinationDegrees);
        Assert.Equal(2_573_000, andromeda.DistanceLightYears);
        Assert.Equal(70_760, andromeda.RadiusLightYears);
        Assert.InRange(Math.Abs(Vector3d.Dot(andromeda.Orientation.X, andromeda.Orientation.Z)), 0, 1e-12);
        Assert.InRange(Math.Abs(andromeda.PositionKilometers.Length / StellarEntry.KilometersPerLightYear - 2_573_000), 0, 1e-7);

        DeepSkyObject hercules = Assert.IsType<DeepSkyObject>(index.Find(new("dso/m13")));
        Assert.Equal(DeepSkyKind.GlobularCluster, hercules.Kind);
        Assert.Equal(1.872, hercules.KingConcentration);
        Assert.Equal(.811, hercules.CoreRadiusArcminutes);
        Assert.Contains("NGC 6205", hercules.Aliases);
        DeepSkyObject pleiades = Assert.IsType<DeepSkyObject>(index.Find(new("dso/m45")));
        Assert.Contains("Seven Sisters", pleiades.Aliases);
        Assert.Equal(443.074, pleiades.DistanceLightYears);
        Assert.Equal(DeepSkyKind.Nebula, index.Find(new("dso/m42"))?.Kind);
    }

    [RealDscFact]
    [Trait("Category", "ExternalData")]
    public void OriginalSameNameClustersKeepTheirIdsWhenFilesChangeOrder()
    {
        string source = Environment.GetEnvironmentVariable("SPACETANGENT_DSC_ROOT")!;
        var before = DeepSkyIndex.Load(source).Objects.Where(body => body.Designation == "FSR 1735").ToArray();
        Assert.Equal(2, before.Length);
        Assert.Equal(new ObjectId("dso/fsr-1735"), Assert.Single(before, body => body.Kind == DeepSkyKind.GlobularCluster).Id);
        var reordered = Directory.CreateTempSubdirectory("space-tangent-dsc-");
        try
        {
            File.Copy(Path.Combine(source, "openclusters.dsc"), Path.Combine(reordered.FullName, "01-open.dsc"));
            File.Copy(Path.Combine(source, "globulars.dsc"), Path.Combine(reordered.FullName, "02-globular.dsc"));
            var after = DeepSkyIndex.Load(reordered.FullName);
            foreach (var expected in before)
            {
                var actual = Assert.IsType<DeepSkyObject>(after.Find(expected.Id));
                Assert.Equal(expected.Kind, actual.Kind);
                Assert.Equal(expected.PositionKilometers, actual.PositionKilometers);
                Assert.Equal(expected.Aliases, actual.Aliases);
            }
        }
        finally
        {
            reordered.Delete(recursive: true);
        }
    }

    private sealed class RealDscFactAttribute : FactAttribute
    {
        public RealDscFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_DSC_ROOT")))
                Skip = "Set SPACETANGENT_DSC_ROOT to the verified DSC directory.";
        }
    }
}
