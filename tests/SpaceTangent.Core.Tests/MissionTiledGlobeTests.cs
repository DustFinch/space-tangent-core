// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class MissionTiledGlobeTests
{
    [TiledGlobeFact]
    [Trait("Category", "ExternalData")]
    public void MissionGlobesResolveOriginalDdsPyramidsInBothFormats()
    {
        string root = Environment.GetEnvironmentVariable("SPACETANGENT_MISSION_TILE_CORPUS")!;
        var raw = Assert.Single(SampledMissionCatalog.Load("mars.json", Path.Combine(root, "p4-tiled-globe-source"), Resolve));
        var packed = Assert.Single(SampledMissionCatalog.Load("pack.json", Path.Combine(root, "p4-tiled-globe-packed"), Resolve));
        var source = Assert.IsType<BodySurfaceMaps>(Assert.Single(raw.Geometry!).Shape.SurfaceMaps);
        var archive = Assert.IsType<BodySurfaceMaps>(Assert.Single(packed.Geometry!).Shape.SurfaceMaps);
        Check(source.Color!, archive.Color!);
        Check(source.Normal!, archive.Normal!);

        static ObjectId? Resolve(string name) => name is "Earth" or "Sol/Earth" ? new("earth") : null;

        static void Check(SurfaceTilePyramid raw, SurfaceTilePyramid packed)
        {
            Assert.Equal(4, raw.LevelCount);
            Assert.Equal(170, raw.Tiles.Count);
            Assert.True(raw.PackedNormals);
            Assert.Equal(raw.Tiles.Keys.OrderBy(key => (key.Level, key.Column, key.Row)),
                packed.Tiles.Keys.OrderBy(key => (key.Level, key.Column, key.Row)));
            var address = new SurfaceTileAddress(3, 1, 1);
            Assert.Equal(Path.GetFileName(raw.FindRegion(address).File), Path.GetFileName(packed.FindRegion(address).File));
            var ancestor = packed with { Tiles = packed.Tiles.Where(item => item.Key.Level < 3).ToDictionary() };
            Assert.Equal(.5f, ancestor.FindRegion(address).Right - ancestor.FindRegion(address).Left);
        }
    }

    private sealed class TiledGlobeFactAttribute : FactAttribute
    {
        public TiledGlobeFactAttribute()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SPACETANGENT_MISSION_TILE_CORPUS")))
                Skip = "Set the root containing the mission globes with archived DDS pyramids.";
        }
    }
}
