// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;

namespace SpaceTangent.Core.Observing;

/// <summary>Nested HEALPix cells, in survey coordinates with the north pole at +Z.</summary>
public static class HealpixSkyGrid
{
    public const int MaximumOrder = 29;
    private static readonly int[] FaceColumns = [1, 3, 5, 7, 0, 2, 4, 6, 1, 3, 5, 7];
    private static readonly (double X, double Y)[] Edges =
        [(0, 0), (1, 0), (0, 1), (1, 1), (.5, 0), (0, .5), (1, .5), (.5, 1)];

    public static long Count(int order) => order is >= 0 and <= MaximumOrder
        ? 12L << (2 * order) : throw new ArgumentOutOfRangeException(nameof(order));

    public static long CellAt(int order, Vector3d direction)
    {
        _ = Count(order);
        double length = direction.Length;
        if (!(length > 0) || !double.IsFinite(length))
            throw new ArgumentOutOfRangeException(nameof(direction));

        long side = 1L << order;
        double z = Math.Clamp(direction.Z / length, -1, 1);
        double column = Math.Atan2(direction.Y, direction.X) / (Math.PI / 2);
        column -= 4 * Math.Floor(column / 4);
        int face;
        double x, y;
        if (Math.Abs(z) <= 2d / 3)
        {
            double rising = side * (.5 + column + z * .75);
            double falling = side * (.5 + column - z * .75);
            long a = (long)(falling / side), b = (long)(rising / side);
            face = (int)(a == b ? (a & 3) + 4 : a < b ? a & 3 : (b & 3) + 8);
            x = rising - b * side;
            y = side - (falling - a * side);
        }
        else
        {
            int quadrant = Math.Min(3, (int)column);
            double transverse = new Vector3d(direction.X, direction.Y, 0).Length / length;
            double s = side * Math.Sqrt(3 / (1 + Math.Abs(z))) * transverse;
            double p = s * (column - quadrant);
            double q = s - p;
            (face, x, y) = z > 0 ? (quadrant, side - q, side - p) : (quadrant + 8, p, q);
        }
        return ((long)face << (2 * order)) |
            Spread(Math.Clamp((long)x, 0, side - 1)) | (Spread(Math.Clamp((long)y, 0, side - 1)) << 1);
    }

    public static (int Face, long X, long Y) Coordinates(int order, long cell)
    {
        if (cell < 0 || cell >= Count(order))
            throw new ArgumentOutOfRangeException(nameof(cell));
        long local = cell & ((1L << (2 * order)) - 1);
        return ((int)(cell >> (2 * order)), Compact(local), Compact(local >> 1));
    }

    public static Vector3d Direction(int order, long cell, double x = .5, double y = .5)
    {
        if (x is not (>= 0 and <= 1) || y is not (>= 0 and <= 1))
            throw new ArgumentOutOfRangeException(nameof(x));
        var (face, i, j) = Coordinates(order, cell);
        double side = 1L << order;
        double a = (i + x) / side, b = (j + y) / side;
        double planeX = (FaceColumns[face] + a - b) * Math.PI / 4;
        double planeY = (-(face / 4) + a + b) * Math.PI / 4;
        double z, longitude, radius;
        if (Math.Abs(planeY) <= Math.PI / 4)
        {
            z = planeY * 8 / (3 * Math.PI);
            longitude = planeX;
            radius = Math.Sqrt(Math.Max(0, 1 - z * z));
        }
        else
        {
            double sigma = 2 - Math.Abs(planeY) * 4 / Math.PI;
            z = Math.Sign(planeY) * (1 - sigma * sigma / 3);
            radius = sigma * Math.Sqrt((1 + Math.Abs(z)) / 3);
            double center = (2 * (face & 3) + 1) * Math.PI / 4;
            longitude = sigma > 1e-12 ? center + (planeX - center) / sigma : center;
        }
        var (sin, cos) = Math.SinCos(longitude);
        return new(radius * cos, radius * sin, z);
    }

    /// <summary>Conservative visible cells, with one extra result to signal an over-wide query.</summary>
    public static IReadOnlyList<long> Cover(int order, Vector3d axis, double radius, int limit = 160)
    {
        _ = Count(order);
        double length = axis.Length;
        if (!(length > 0) || !double.IsFinite(length) || radius is not (>= 0 and <= Math.PI) || limit is < 1 or > 100_000)
            throw new ArgumentOutOfRangeException(nameof(radius));
        axis /= length;
        var result = new List<long>();
        for (int face = 0; face < 12; face++)
            Search(0, face);
        return result.AsReadOnly();

        void Search(int depth, long cell)
        {
            if (result.Count > limit)
                return;
            Vector3d center = Direction(depth, cell);
            double reach = 0;
            foreach (var (x, y) in Edges)
                reach = Math.Max(reach, Angle(center, Direction(depth, cell, x, y)));
            if (Angle(center, axis) > radius + reach * 1.05)
                return;
            if (depth == order)
            {
                result.Add(cell);
                return;
            }
            for (int child = 0; child < 4; child++)
                Search(depth + 1, cell * 4 + child);
        }

        static double Angle(Vector3d a, Vector3d b) => Math.Atan2(Vector3d.Cross(a, b).Length, Vector3d.Dot(a, b));
    }

    private static long Spread(long value)
    {
        value = (value | value << 16) & 0x0000ffff0000ffff;
        value = (value | value << 8) & 0x00ff00ff00ff00ff;
        value = (value | value << 4) & 0x0f0f0f0f0f0f0f0f;
        value = (value | value << 2) & 0x3333333333333333;
        return (value | value << 1) & 0x5555555555555555;
    }

    private static long Compact(long value)
    {
        value &= 0x5555555555555555;
        value = (value | value >> 1) & 0x3333333333333333;
        value = (value | value >> 2) & 0x0f0f0f0f0f0f0f0f;
        value = (value | value >> 4) & 0x00ff00ff00ff00ff;
        value = (value | value >> 8) & 0x0000ffff0000ffff;
        return (value | value >> 16) & 0xffffffff;
    }
}
