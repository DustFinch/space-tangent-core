// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;
using Xunit;

namespace SpaceTangent.Core.Tests;

public sealed class HealpixSkyGridTests
{
    [Fact]
    public void BaseFacesAndEdgesUseTheNestedSurveyConvention()
    {
        Assert.Equal(768, HealpixSkyGrid.Count(3));
        Assert.Equal(12L << 58, HealpixSkyGrid.Count(29));
        Assert.InRange(HealpixSkyGrid.Direction(0, 0).Z, 2d / 3 - 1e-14, 2d / 3 + 1e-14);
        Assert.InRange((HealpixSkyGrid.Direction(0, 4) - new Vector3d(1, 0, 0)).Length, 0, 1e-14);
        Assert.InRange((HealpixSkyGrid.Direction(0, 0, 1, 1) - new Vector3d(0, 0, 1)).Length, 0, 1e-14);
        Assert.InRange((HealpixSkyGrid.Direction(0, 8).Z + 2d / 3), -1e-14, 1e-14);

        for (int order = 0; order <= 5; order++)
        for (long cell = 0; cell < HealpixSkyGrid.Count(order); cell++)
        foreach (var (x, y) in new[] { (.5, .5), (.02, .02), (.98, .98), (.98, .02) })
            Assert.Equal(cell, HealpixSkyGrid.CellAt(order, HealpixSkyGrid.Direction(order, cell, x, y)));

        long distantCell = HealpixSkyGrid.Count(29) - 12_345_678;
        Assert.Equal(distantCell, HealpixSkyGrid.CellAt(29, HealpixSkyGrid.Direction(29, distantCell)));
        long polarCell = (1L << 58) - 1;
        Assert.Equal(polarCell, HealpixSkyGrid.CellAt(29, HealpixSkyGrid.Direction(29, polarCell)));
    }

    [Fact]
    public void NarrowConeCoversTheOrionFieldAndWideQueriesStopAtTheirLimit()
    {
        const double rad = Math.PI / 180;
        Vector3d axis = new(Math.Cos(84 * rad) * Math.Cos(rad), Math.Sin(84 * rad) * Math.Cos(rad), -Math.Sin(rad));
        var covered = HealpixSkyGrid.Cover(7, axis, 2 * rad);
        Assert.InRange(covered.Count, 60, 120);
        var random = new Random(5);
        Vector3d side = Vector3d.Cross(axis, new(0, 0, 1));
        side /= side.Length;
        Vector3d north = Vector3d.Cross(axis, side);
        for (int sample = 0; sample < 400; sample++)
        {
            var (sin, cos) = Math.SinCos(2 * rad * Math.Sqrt(random.NextDouble()));
            var (sinTurn, cosTurn) = Math.SinCos(Math.Tau * random.NextDouble());
            Vector3d direction = axis * cos + (side * cosTurn + north * sinTurn) * sin;
            Assert.Contains(HealpixSkyGrid.CellAt(7, direction), covered);
        }
        Assert.Equal(161, HealpixSkyGrid.Cover(29, axis, Math.PI).Count);
    }
}
