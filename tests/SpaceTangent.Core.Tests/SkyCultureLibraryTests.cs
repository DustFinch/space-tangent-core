// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class SkyCultureLibraryTests
{
    [SkyCulturesFact]
    [Trait("Category", "ExternalData")]
    public void OriginalCultureAtlasLoadsNamesLinesAndArtworkWithExactAnchorDirections()
    {
        string root = Environment.GetEnvironmentVariable("SPACETANGENT_SKY_CULTURES")!;
        var library = SkyCultureLibrary.Load(root);
        Assert.Equal(33, library.Cultures.Count);
        var western = library.Find("western")!;
        Assert.Equal(88, western.Figures.Count);
        Assert.Equal(85, western.Figures.Count(figure => figure.Artwork is not null));
        Assert.Equal(5, library.Find("aztec")!.Figures.Count(figure => figure.Artwork is not null));
        Assert.Equal(27, library.Find("boorong")!.Figures.Count(figure => figure.Artwork is not null));
        Assert.Equal(11, library.Find("inuit")!.Figures.Count(figure => figure.Artwork is not null));
        Assert.Equal(128, library.Cultures.Sum(culture => culture.Figures.Count(figure => figure.Artwork is not null)));
        var orion = western.Figures.Single(figure => figure.Id == "CON western Ori");
        Assert.Equal("Orion", orion.DisplayName(SkyCultureNames.International));
        var art = Assert.IsType<SkyCultureArtwork>(orion.Artwork);
        Assert.EndsWith("orion.webp", art.ImagePath, StringComparison.OrdinalIgnoreCase);
        Vector3d first = new(1, 0, 0), second = new(0, 1, 0), third = new(0, 0, 1);
        Assert.Equal(first, art.Direction(art.First.X, art.First.Y, first, second, third));
        Assert.Equal(second, art.Direction(art.Second.X, art.Second.Y, first, second, third));
        Assert.Equal(third, art.Direction(art.Third.X, art.Third.Y, first, second, third));
        var chinese = library.Find("chinese")!;
        var dipper = chinese.Figures.Single(figure => figure.InternationalName == "Northern Dipper");
        Assert.Equal("北斗", dipper.DisplayName(SkyCultureNames.Native));
        Assert.Equal("Северный ковш", dipper.DisplayName(SkyCultureNames.Russian));
        Assert.Contains(dipper.Lines.SelectMany(line => line), hip => hip == 54061);
        Assert.Null(library.Find("../../western"));
    }

    [RecoveredSkyCulturesFact]
    [Trait("Category", "ExternalData")]
    public void RecoveredCultureAtlasPreservesEveryAvailableArtworkAnchor()
    {
        var library = SkyCultureLibrary.Load(Environment.GetEnvironmentVariable("SPACETANGENT_RECOVERED_SKY_CULTURES")!);
        Assert.Equal(33, library.Cultures.Count);
        var artwork = library.Cultures.SelectMany(culture => culture.Figures)
            .Where(figure => figure.Artwork is not null).Select(figure => figure.Artwork!).ToArray();
        Assert.Equal(299, artwork.Length);
        Vector3d first = new(1, 0, 0), second = new(0, 1, 0), third = new(0, 0, 1);
        foreach (var art in artwork)
        {
            Assert.True(File.Exists(art.ImagePath), art.ImagePath);
            Assert.Equal(first, art.Direction(art.First.X, art.First.Y, first, second, third));
            Assert.Equal(second, art.Direction(art.Second.X, art.Second.Y, first, second, third));
            Assert.Equal(third, art.Direction(art.Third.X, art.Third.Y, first, second, third));
        }
    }

    private sealed class RecoveredSkyCulturesFactAttribute : FactAttribute
    {
        public RecoveredSkyCulturesFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPACETANGENT_RECOVERED_SKY_CULTURES")))
                Skip = "Set the recovered, source-pinned sky culture library directory.";
        }
    }

    private sealed class SkyCulturesFactAttribute : FactAttribute
    {
        public SkyCulturesFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("SPACETANGENT_SKY_CULTURES") is null)
                Skip = "Set the original sky culture library directory.";
        }
    }
}
