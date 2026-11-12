// SPDX-License-Identifier: GPL-2.0-or-later
using System.Security.Cryptography;
using System.Text.Json;
using SpaceTangent.Core.Addons;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class LocalSurfaceMapsTests
{
    [RealTileFact]
    [Trait("Category", "ExternalData")]
    public void OriginalAndPackedMarsContainTheSame340Tiles()
    {
        string root = Required("SPACETANGENT_TILE_FIXTURE");
        BodySurfaceMaps original = Assert.Single(LocalSurfaceMaps.Load(Path.Combine(root, "original-planets")));
        BodySurfaceMaps packed = Assert.Single(LocalSurfaceMaps.Load(Path.Combine(root, "packed-planets")));
        Assert.Equal("Mars", original.Name);
        Assert.Equal(original.Name, packed.Name);
        Check(original.Color!, packed.Color!, "mars");
        Check(original.Normal!, packed.Normal!, "marsnormal");

        static void Check(SurfaceTilePyramid original, SurfaceTilePyramid packed, string prefix)
        {
            Assert.Equal(4, original.LevelCount);
            Assert.Equal(307, original.EffectiveTileSize);
            Assert.Equal(original.EffectiveTileSize, packed.EffectiveTileSize);
            Assert.True(original.PackedNormals);
            Assert.Equal(170, original.Tiles.Count);
            Assert.Equal(170, packed.Tiles.Count);
            foreach (var (address, file) in original.Tiles)
            {
                Assert.Equal($"{prefix}_{address.Level}_{address.Column}_{(1 << address.Level) - 1 - address.Row}.dds",
                    Path.GetFileName(file));
                using var source = File.OpenRead(file);
                using var output = File.OpenRead(packed.Tiles[address]);
                Assert.Equal(SHA256.HashData(source), SHA256.HashData(output));
            }
        }
    }

    [RealTileFact]
    [Trait("Category", "ExternalData")]
    public void SparseAncestorCoordinatesMatchNativeReference()
    {
        string file = Path.Combine(Required("SPACETANGENT_TILE_FIXTURE"), "reproduction",
            "nametemplate-clean-first", "reference.json");
        using var input = File.OpenRead(file);
        using var reference = JsonDocument.Parse(input);
        int count = 0;
        foreach (var region in reference.RootElement.GetProperty("regions").EnumerateArray())
        {
            int levels = region.GetProperty("levels").GetInt32();
            var request = new SurfaceTileAddress(region.GetProperty("level").GetInt32(),
                region.GetProperty("column").GetInt32(), region.GetProperty("row").GetInt32());
            uint mask = region.GetProperty("mask").GetUInt32();
            var tiles = new Dictionary<SurfaceTileAddress, string>();
            for (int level = Math.Min(levels - 1, request.Level); level >= 0; level--)
            {
                if ((mask & (1u << level)) == 0)
                    continue;
                int column = request.Column >> (request.Level - level), row = request.Row >> (request.Level - level);
                tiles.Add(new(level, column, row), $"tile_{level}_{column}_{(1 << level) - 1 - row}.dds");
            }
            var map = new SurfaceTilePyramid(levels, 512, region.GetProperty("border").GetSingle(), true, true, false, tiles);
            var result = map.FindRegion(request);
            Assert.Equal(region.GetProperty("resource").GetString(), result.File);
            Assert.Equal(region.GetProperty("rect").EnumerateArray().Select(part => BitConverter.SingleToInt32Bits(part.GetSingle())),
                new[] { result.Left, result.Bottom, result.Right, result.Top }.Select(BitConverter.SingleToInt32Bits));
            count++;
        }
        Assert.Equal(2700, count);
    }

    [Fact]
    public void AdjacentAndOmittedTokensDoNotRequireEnumeratingThePyramid()
    {
        var root = Directory.CreateTempSubdirectory("st-tiles-");
        try
        {
            Directory.CreateDirectory(Path.Combine(root.FullName, "maps"));
            File.WriteAllText(Path.Combine(root.FullName, "mars.json"), """
                { "items": [{ "name": "Mars", "geometry": {
                    "baseMap": { "type": "NameTemplate", "levelCount": 16, "tileSize": 512,
                                  "template": "maps/tile_%level%column_%level.dds" }
                }}] }
                """);
            string expected = Path.Combine(root.FullName, "maps", "tile_1565535_15.dds");
            File.WriteAllText(expected, "tile contents");
            var map = Assert.Single(LocalSurfaceMaps.Load(root.FullName)).Color!;
            Assert.Equal(expected, Assert.Single(map.Tiles).Value);
            Assert.False(map.HasRows);
            Assert.Equal(expected, map.FindRegion(new(15, 65535, 32767)).File);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static string Required(string name) => Environment.GetEnvironmentVariable(name)!;

    private sealed class RealTileFactAttribute : FactAttribute
    {
        public RealTileFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Required("SPACETANGENT_TILE_FIXTURE")))
                Skip = "Set SPACETANGENT_TILE_FIXTURE to the original and packed tile fixture with its native reference.";
        }
    }
}
