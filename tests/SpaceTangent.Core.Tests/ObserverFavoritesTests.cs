// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Observing;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class ObserverFavoritesTests
{
    [Fact]
    public void ParsesFoldersOffsetsAndQuotedSelections()
    {
        var favorite = Assert.Single(ObserverFavorites.Parse("""
            # Favorites file
            "Science" { isFolder true parentFolder "" }
            "Lunar flyby" {
                isFolder false parentFolder "Science"
                base [ 1.25 -2 0 ] offset [ 50000 0 -0.5 ]
                axis [ 0 5 0 ] angle 0.5 time 2460600.5
                selection "Sol/Earth/Moon" coordsys "geographic"
            }
            """));
        Assert.Equal("Science", favorite.Folder);
        Assert.Equal("Sol/Earth/Moon", favorite.Selection);
        Assert.Equal("geographic", favorite.CoordinateSystem);
        Assert.Equal(new Vector3d(0, 1, 0), favorite.Axis);
        Assert.Equal(2460600.5, favorite.JulianDay);
        Assert.Equal(1.3 * StellarEntry.KilometersPerLightYear, favorite.PositionKilometers.X);
        Assert.Equal(-0.5e-6 * StellarEntry.KilometersPerLightYear, favorite.PositionKilometers.Z);
    }

    [Theory]
    [InlineData("""unquoted { }""")]
    [InlineData("\"Bad\" { base [ 0 0 ] }")]
    [InlineData("\"Bad\" { offset [ 0 NaN 0 ] }")]
    [InlineData("\"Bad\" { axis [ 0 0 0 ] angle 1 }")]
    [InlineData("\"Bad\" { isFolder false time 10 time 11 }")]
    public void RejectsInvalidRecords(string text) =>
        Assert.Throws<InvalidDataException>(() => ObserverFavorites.Parse(text));
}
