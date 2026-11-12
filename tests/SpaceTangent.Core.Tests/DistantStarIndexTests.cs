// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class DistantStarIndexTests
{
    [DistantArchiveFact]
    [Trait("Category", "ExternalData")]
    public void OriginalDistantCatalogueStreamsOnlyRelevantLeavesAndResolvesNames()
    {
        string root = Environment.GetEnvironmentVariable("SPACETANGENT_STELLAR_ARCHIVE")!;
        using var catalogue = DistantStarIndex.Load(Path.Combine(root, "stars-far.bin"), Path.Combine(root, "stars-far-names.txt"));
        Assert.Equal(2_299_878, catalogue.StarCount);
        Assert.Equal(1172, catalogue.LeafCount);
        Assert.Equal(0, catalogue.LeavesRead);
        var list = new List<DistantStar>();
        Assert.Equal(0, catalogue.Query(default, 9, list));
        Assert.InRange(catalogue.LeavesRead, 0, catalogue.LeafCount / 10);

        var nearby = StellarCatalogue.LoadArchive(root);
        var betelgeuse = nearby.Find(new ObjectId("hip/27989"))!;
        Vector3d observer = betelgeuse.PositionParsecs * (.99 * StellarEntry.KilometersPerParsec / StellarEntry.KilometersPerLightYear);
        Assert.True(catalogue.Query(observer, 9, list) > 10_000);
        Assert.InRange(catalogue.LeavesRead, 1, catalogue.LeafCount / 2 - 1);
        Assert.All(list, star => Assert.True(star.MagnitudeAt((star.PositionLightYears - observer).Length) <= 9));
        long cached = catalogue.CachedBytes;
        int visibleReads = catalogue.LeavesRead;
        list.Clear();
        catalogue.Query(observer, 9, list);
        Assert.Equal(visibleReads, catalogue.LeavesRead);

        var id = new ObjectId("hip/40");
        var label = Assert.Single(catalogue.NamedStars, item => item.Id == id);
        Assert.Equal("V463 Cep", label.Name);
        var resolved = Assert.IsType<StellarEntry>(catalogue.Find(id));
        Assert.Equal(label.Name, resolved.Name);
        Assert.Contains("HIP 40", resolved.Aliases);
        Assert.True(resolved.DistanceParsecs > 30);
        Assert.Equal(cached, catalogue.CachedBytes);
        int afterSearch = catalogue.LeavesRead;
        Assert.Same(resolved, catalogue.Find(id));
        Assert.Equal(afterSearch, catalogue.LeavesRead);
    }

    private sealed class DistantArchiveFactAttribute : FactAttribute
    {
        public DistantArchiveFactAttribute()
        {
            string? root = Environment.GetEnvironmentVariable("SPACETANGENT_STELLAR_ARCHIVE");
            if (string.IsNullOrEmpty(root) || !File.Exists(Path.Combine(root, "stars-far.bin")))
                Skip = "Set the directory with the archived distant star catalogue.";
        }
    }
}
