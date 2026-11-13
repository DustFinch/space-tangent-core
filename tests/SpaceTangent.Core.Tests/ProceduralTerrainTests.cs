// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.World;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class ProceduralTerrainTests
{
    [Theory]
    [InlineData(ProceduralWorldKind.Terran)]
    [InlineData(ProceduralWorldKind.Desert)]
    [InlineData(ProceduralWorldKind.Cratered)]
    public void EqualLevelPatchesMeetAndKeepOutwardNormals(ProceduralWorldKind kind)
    {
        var surface = new ProceduralSurface(3, 5200, kind);
        var terrain = new ProceduralTerrain(surface, 5185);
        var left = terrain.Build(new(0, 3, 4, 3));
        var right = terrain.Build(new(0, 3, 5, 3));
        int grid = ProceduralTerrain.Grid, row = grid + 1;
        Assert.Equal(row * row + 4 * grid, left.Vertices.Length);
        Assert.Equal((grid * grid + 4 * grid) * 6, left.Indices.Length);
        Assert.All(left.Indices, i => Assert.InRange(i, 0, left.Vertices.Length - 1));
        for (int y = 0; y < row; y++)
        {
            var a = left.Vertices[y * row + grid];
            var b = right.Vertices[y * row];
            Vector3d point = a.PositionKilometers + left.CenterKilometers;
            Assert.InRange((point - (b.PositionKilometers + right.CenterKilometers)).Length, 0, 1e-9);
            Assert.InRange((a.Normal - b.Normal).Length, 0, 1e-9);
            Assert.True(Vector3d.Dot(point / point.Length, a.Normal) > .8);
            Assert.Equal(a.Albedo, b.Albedo);
        }
    }

    [Fact]
    public void ChildMorphMatchesParentTriangleAndSharedEdges()
    {
        var terrain = new ProceduralTerrain(new ProceduralSurface(3, 5200, ProceduralWorldKind.Desert), 5185);
        var parent = terrain.Build(new(0, 5, 16, 15));
        var left = terrain.Build(new(0, 6, 32, 30));
        var right = terrain.Build(new(0, 6, 33, 30));
        var coarse = Assert.IsType<ProceduralTerrainVertex[]>(left.ParentVertices);
        var neighbour = Assert.IsType<ProceduralTerrainVertex[]>(right.ParentVertices);
        int grid = ProceduralTerrain.Grid, row = grid + 1;
        var diagonal = (parent.Vertices[1].PositionKilometers + parent.Vertices[row].PositionKilometers) * .5;
        Assert.InRange((coarse[row + 1].PositionKilometers + left.CenterKilometers - (diagonal + parent.CenterKilometers)).Length, 0, 1e-9);
        Assert.InRange((coarse[0].PositionKilometers + left.CenterKilometers -
            (parent.Vertices[0].PositionKilometers + parent.CenterKilometers)).Length, 0, 1e-9);
        for (int y = 0; y <= grid; y++)
        {
            var a = coarse[y * row + grid];
            var b = neighbour[y * row];
            Assert.InRange((a.PositionKilometers + left.CenterKilometers - (b.PositionKilometers + right.CenterKilometers)).Length, 0, 1e-9);
            Assert.Equal(a.Albedo, b.Albedo);
        }
    }

    [Fact]
    public void DescendingRefinesVisiblePatchesWithinABoundedBudget()
    {
        var surface = new ProceduralSurface(1, 5900);
        var terrain = new ProceduralTerrain(surface, 5880);
        var direction = new Vector3d(0, 1, 0);
        double ground = terrain.GroundRadiusKilometers(direction);
        Assert.Equal(5880 + Math.Max(0, surface.Elevation(direction, .004)), ground, 8);
        var far = terrain.Select(direction * (ground + 120), direction * -1, .7, 780);
        var near = terrain.Select(direction * (ground + .2), direction * -1, .7, 780);
        Assert.NotEmpty(far);
        Assert.True(near.Max(key => key.Level) >= far.Max(key => key.Level) + 4);
        Assert.InRange(near.Count, 1, ProceduralTerrain.MaximumLeaves);
        Assert.Equal(near.Count, near.Distinct().Count());
        Assert.True(terrain.Select(direction * (ground + .2), direction, .7, 780).Count < near.Count);
        var gas = new ProceduralTerrain(new ProceduralSurface(10, 64_000, ProceduralWorldKind.GasGiant), 60_500);
        Assert.Empty(gas.Select(new(0, 65_000, 0), direction * -1, .7, 780));
    }
}
