// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;

namespace SpaceTangent.Core.World;

public readonly record struct ProceduralPatchKey(int Face, int Level, int X, int Y)
{
    internal ProceduralPatchKey Child(int quadrant) =>
        new(Face, Level + 1, 2 * X + (quadrant & 1), 2 * Y + (quadrant >> 1));
}

public readonly record struct ProceduralTerrainVertex(Vector3d PositionKilometers, Vector3d Normal, ProceduralAlbedo Albedo);

public sealed record ProceduralTerrainPatch(Vector3d CenterKilometers, ProceduralTerrainVertex[] Vertices, int[] Indices)
{
    public ProceduralTerrainVertex[]? ParentVertices { get; init; }
    public double VertexSpacingKilometers { get; init; }
}

/// <summary>View-dependent, equiangular cube-sphere terrain in the procedural surface's native Y-up axes.</summary>
public sealed partial class ProceduralTerrain(ProceduralSurface surface, double polarRadiusKilometers)
{
    public const int Grid = 16;
    public const int MaximumLeaves = 512;
    public const int MaximumLevel = 20;
    private const int Row = Grid + 1;
    private readonly double _radius = surface.RadiusKilometers;
    private static readonly (Vector3d Normal, Vector3d U, Vector3d V)[] Faces =
    [
        (new(1, 0, 0), new(0, 0, -1), new(0, 1, 0)),
        (new(-1, 0, 0), new(0, 0, 1), new(0, 1, 0)),
        (new(0, 1, 0), new(1, 0, 0), new(0, 0, -1)),
        (new(0, -1, 0), new(1, 0, 0), new(0, 0, 1)),
        (new(0, 0, 1), new(1, 0, 0), new(0, 1, 0)),
        (new(0, 0, -1), new(-1, 0, 0), new(0, 1, 0))
    ];

    public double GroundRadiusKilometers(Vector3d direction) =>
        EllipsoidRadius(direction) + VisibleHeight(direction, ProceduralSurface.ShortestWavelengthKilometers);

    private double EllipsoidRadius(Vector3d direction)
    {
        double y = direction.Y * _radius / polarRadiusKilometers;
        return _radius / Math.Sqrt(direction.X * direction.X + y * y + direction.Z * direction.Z);
    }

    private double VisibleHeight(Vector3d direction, double wavelength)
    {
        double elevation = surface.Elevation(direction, wavelength);
        return surface.HasWater ? Math.Max(0, elevation) : elevation;
    }

    public static Vector3d Direction(ProceduralPatchKey key, double s, double t)
    {
        var (normal, u, v) = Faces[key.Face];
        double cells = 1 << key.Level;
        double a = Math.Tan(Math.PI / 4 * (2 * (key.X + s) / cells - 1));
        double b = Math.Tan(Math.PI / 4 * (2 * (key.Y + t) / cells - 1));
        Vector3d direction = normal + u * a + v * b;
        return direction / direction.Length;
    }

    /// <summary>Selects patches for a body-local eye and unit view axis, using a conservative cone around the frustum.</summary>
    public IReadOnlyList<ProceduralPatchKey> Select(Vector3d eye, Vector3d axis, double viewCone, double focalPixels)
    {
        var leaves = new List<ProceduralPatchKey>();
        if (!surface.HasSurface || !(eye.Length > 0) || !(focalPixels > 0))
            return leaves;
        double distance = eye.Length;
        Vector3d outward = eye / distance;
        double relief = surface.LargestKilometers;
        double lower = Math.Max(1, polarRadiusKilometers - relief);
        double upper = _radius + relief;
        double horizon = Math.Acos(Math.Clamp(lower / distance, 0, 1)) + Math.Acos(lower / upper);
        int reserved = Faces.Length;
        for (int face = 0; face < Faces.Length; face++)
            Visit(new(face, 0, 0, 0));
        return leaves;

        void Visit(ProceduralPatchKey key)
        {
            Vector3d direction = Direction(key, .5, .5);
            double cornerCosine = 1;
            for (int corner = 0; corner < 4; corner++)
                cornerCosine = Math.Min(cornerCosine, Vector3d.Dot(direction, Direction(key, corner & 1, corner >> 1)));
            double angle = Math.Acos(Math.Clamp(cornerCosine, -1, 1));
            if (Math.Acos(Math.Clamp(Vector3d.Dot(direction, outward), -1, 1)) - angle > horizon)
                return;
            double spacing = Math.Sqrt(2) * angle * _radius / Grid;
            double elevation = VisibleHeight(direction, 2 * spacing);
            Vector3d offset = direction * (EllipsoidRadius(direction) + elevation) - eye;
            double depth = offset.Length;
            double reach = 2 * Math.Sin(angle / 2) * upper + Math.Min(2 * relief, 2 * angle * _radius);
            if (depth > reach &&
                Math.Acos(Math.Clamp(Vector3d.Dot(offset, axis) / depth, -1, 1)) - Math.Asin(reach / depth) > viewCone)
                return;
            if (key.Level < MaximumLevel && spacing * focalPixels > 12 * Math.Max(depth - reach, .001) &&
                spacing > ProceduralSurface.ShortestWavelengthKilometers / 2 && reserved <= MaximumLeaves - 3)
            {
                reserved += 3;
                for (int quadrant = 0; quadrant < 4; quadrant++)
                    Visit(key.Child(quadrant));
                return;
            }
            leaves.Add(key);
        }
    }

    public ProceduralTerrainPatch Build(ProceduralPatchKey key)
    {
        double spacing = _radius * Math.PI / (2 * Grid * (1 << key.Level));
        double wavelength = 2 * spacing;

        var vertices = new ProceduralTerrainVertex[Row * Row + 4 * Grid];
        var indices = new int[(Grid * Grid + 4 * Grid) * 6];
        Vector3d center = Position(Direction(key, .5, .5), wavelength);
        int index = 0;
        for (int y = 0; y < Row; y++)
        for (int x = 0; x < Row; x++)
        {
            int vertex = y * Row + x;
            vertices[vertex] = Sample(key, x, y, wavelength);
            if (x < Grid && y < Grid)
                Quad(vertex, vertex + 1, vertex + Row, vertex + Row + 1);
        }

        // A recessed strip covers the chord and sampling mismatch between neighbouring levels.
        double skirt = Math.Min(2 * surface.LargestKilometers + spacing, 2 * spacing);
        ProceduralTerrainVertex[]? parentVertices = key.Level > 0 ? new ProceduralTerrainVertex[vertices.Length] : null;
        if (parentVertices is not null)
        {
            var parent = new ProceduralPatchKey(key.Face, key.Level - 1, key.X / 2, key.Y / 2);
            int startX = key.X % 2 * Grid / 2, startY = key.Y % 2 * Grid / 2;
            const int ParentRow = Grid / 2 + 1;
            var parentSamples = new ProceduralTerrainVertex[ParentRow * ParentRow];
            for (int y = 0; y < ParentRow; y++)
            for (int x = 0; x < ParentRow; x++)
                parentSamples[y * ParentRow + x] = Sample(parent, startX + x, startY + y, 2 * wavelength);

            for (int y = 0; y < Row; y++)
            for (int x = 0; x < Row; x++)
            {
                int parentIndex = y / 2 * ParentRow + x / 2;
                var first = parentSamples[parentIndex];
                int opposite = ((x & 1), (y & 1)) switch
                {
                    (1, 1) => ParentRow,
                    (1, 0) => 1,
                    (0, 1) => ParentRow,
                    _ => 0
                };
                var second = parentSamples[parentIndex + opposite];
                if ((x & 1) != 0 && (y & 1) != 0)
                    first = parentSamples[parentIndex + 1];
                parentVertices[y * Row + x] = new((first.PositionKilometers + second.PositionKilometers) * .5,
                    Unit(first.Normal + second.Normal),
                    new((first.Albedo.Red + second.Albedo.Red) * .5f, (first.Albedo.Green + second.Albedo.Green) * .5f,
                        (first.Albedo.Blue + second.Albedo.Blue) * .5f, (first.Albedo.Water + second.Albedo.Water) * .5f));
            }
        }
        int next = Row * Row;
        for (int edge = 0; edge < 4; edge++)
        for (int i = 0; i < Grid; i++)
        {
            int top = Border(edge, i);
            int bottom = next++;
            int following = i == Grid - 1 && edge == 3 ? Row * Row : bottom + 1;
            var sample = vertices[top];
            vertices[bottom] = sample with
            {
                PositionKilometers = sample.PositionKilometers - Unit(sample.PositionKilometers + center) * skirt
            };
            if (parentVertices is not null)
            {
                var coarse = parentVertices[top];
                parentVertices[bottom] = coarse with
                {
                    PositionKilometers = coarse.PositionKilometers - Unit(coarse.PositionKilometers + center) * skirt
                };
            }
            Quad(top, bottom, Border(edge, i + 1), following);
        }
        return new(center, vertices, indices)
        {
            ParentVertices = parentVertices,
            VertexSpacingKilometers = spacing
        };

        ProceduralTerrainVertex Sample(ProceduralPatchKey sampleKey, int x, int y, double detail)
        {
            Vector3d direction = Direction(sampleKey, (double)x / Grid, (double)y / Grid);
            Vector3d position = Position(direction, detail);
            Vector3d east = Vector3d.Cross(Math.Abs(direction.Y) > .99 ? new(1, 0, 0) : new(0, 1, 0), direction);
            east = Unit(east) * (detail / (2 * _radius));
            Vector3d north = Vector3d.Cross(direction, east);
            Vector3d normal = Unit(Vector3d.Cross(Position(Unit(direction + east), detail) - Position(Unit(direction - east), detail),
                Position(Unit(direction + north), detail) - Position(Unit(direction - north), detail)));
            double elevation = surface.Elevation(direction, detail);
            return new(position - center, normal, surface.Surface(direction, elevation, Vector3d.Dot(normal, direction), detail));
        }

        Vector3d Position(Vector3d direction, double detail) => direction * (EllipsoidRadius(direction) + VisibleHeight(direction, detail));
        static Vector3d Unit(Vector3d value) => value / value.Length;
        static int Border(int edge, int i) => edge switch
        {
            0 => i,
            1 => i * Row + Grid,
            2 => Grid * Row + Grid - i,
            _ => (Grid - i) * Row
        };
        void Quad(int a, int b, int c, int d)
        {
            indices[index++] = a;
            indices[index++] = b;
            indices[index++] = c;
            indices[index++] = b;
            indices[index++] = d;
            indices[index++] = c;
        }
    }
}
