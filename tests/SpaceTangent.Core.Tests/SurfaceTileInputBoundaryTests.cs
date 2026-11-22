// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json.Nodes;
using SpaceTangent.Core.Addons;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class SurfaceTileInputBoundaryTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("st-tile-inputs-");
    private static JsonObject Map() => new() { ["type"] = "NameTemplate", ["template"] = "missing/tile_%level_%column_%row.dds",
        ["levelCount"] = 2, ["tileSize"] = 128 };
    private IReadOnlyList<BodySurfaceMaps> Load(JsonObject map, bool duplicate = false)
    {
        var item = new JsonObject { ["name"] = "Witness", ["geometry"] = new JsonObject { ["baseMap"] = map } };
        var items = new JsonArray(item); if (duplicate) items.Add(item.DeepClone());
        File.WriteAllText(Path.Combine(_root.FullName, "surface.json"), new JsonObject { ["items"] = items }.ToJsonString());
        return LocalSurfaceMaps.Load(_root.FullName);
    }

    [Theory]
    [InlineData("../outside.dds")]
    [InlineData("/outside.dds")]
    [InlineData("folder//tile.dds")]
    [InlineData(" ")]
    public void SurfaceTemplatesCannotEscapeTheCatalogueRoot(string template)
    {
        var map = Map(); map["template"] = template;
        Assert.Throws<InvalidDataException>(() => Load(map));
    }

    [Theory]
    [InlineData("-0.01")]
    [InlineData("0.5")]
    [InlineData("NaN")]
    public void InvalidTileBordersAreRejectedBeforeUvCoordinatesArePublished(string border)
    {
        var map = Map(); map["tileBorderThickness"] = border;
        Assert.Throws<InvalidDataException>(() => Load(map));
    }

    [Theory]
    [InlineData("1/4/0")]
    [InlineData("1/0/2")]
    [InlineData("0/x/0")]
    [InlineData("0/0")]
    public void PackedLocalFileAddressesMustFitTheirDeclaredPyramid(string address)
    {
        var map = Map(); map["localFiles"] = new JsonObject { [address] = "missing.dds" };
        Assert.Throws<InvalidDataException>(() => Load(map));
    }

    [Fact]
    public void MissingSparseTilesRemainAvailableForAncestorFallback()
    {
        var map = Map(); map["localFiles"] = new JsonObject { ["0/0/0"] = "missing.dds" };
        var pyramid = Assert.Single(Load(map)).Color!;
        Assert.Empty(pyramid.Tiles); Assert.Null(pyramid.FindRegion(new(1, 0, 0)).File);
        Assert.Throws<ArgumentOutOfRangeException>(() => pyramid.FindRegion(new(-1, 0, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => pyramid.FindRegion(new(21, 0, 0)));
        Assert.Throws<InvalidDataException>(() => Load(Map(), duplicate: true));
    }

    [Fact]
    public void EmptyMissingAndUnnamedCatalogueRecordsDoNotCreateBodyMaps()
    {
        foreach (string text in new[] { "{}", "{\"items\":[{}]}", "{\"items\":[{\"geometry\":{}}]}" })
        {
            File.WriteAllText(Path.Combine(_root.FullName, "surface.json"), text);
            Assert.Empty(LocalSurfaceMaps.Load(_root.FullName));
        }
        Assert.Empty(Assert.Single(Load(Map())).Color!.Tiles);
    }

    [Fact]
    public void ExcessiveTileAddressAliasesAreBoundedEvenWhenTheyShareOneResource()
    {
        File.WriteAllText(Path.Combine(_root.FullName, "shared.dds"), "Path-resolution witness only.");
        var map = Map(); map["levelCount"] = 15;
        var files = new JsonObject(); for (int column = 0; column < 16385; column++) files["14/" + column + "/0"] = "shared.dds";
        map["localFiles"] = files;
        Assert.Throws<InvalidDataException>(() => Load(map));
    }

    public void Dispose() => _root.Delete(recursive: true);
}
