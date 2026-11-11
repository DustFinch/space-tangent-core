// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;

namespace SpaceTangent.Core.Orbits;

internal readonly record struct SignedAxis(int Index, int Sign)
{
    internal static SignedAxis Parse(string text) => text switch
    {
        "x" or "+x" => new(0, 1),
        "-x" => new(0, -1),
        "y" or "+y" => new(1, 1),
        "-y" => new(1, -1),
        "z" or "+z" => new(2, 1),
        "-z" => new(2, -1),
        _ => throw new FormatException("Invalid signed axis.")
    };
}

internal static class TwoVectorBasis
{
    internal static bool TryCreate(SignedAxis primary, Vector3d direction, SignedAxis secondary,
        Vector3d guide, out Basis3d basis)
    {
        basis = default;
        double length = direction.Length, guideLength = guide.Length;
        if (!(length > 0 && guideLength > 0) || !double.IsFinite(length) || !double.IsFinite(guideLength))
            return false;

        Vector3d first = direction / length * primary.Sign;
        guide /= guideLength;
        Vector3d second = guide - first * Vector3d.Dot(first, guide);
        double secondLength = second.Length;
        if (secondLength < 1e-12)
            return false;
        second = second / secondLength * secondary.Sign;

        Span<Vector3d> columns = stackalloc Vector3d[3];
        columns[primary.Index] = first;
        columns[secondary.Index] = second;
        int permutationSign = (primary.Index + 1) % 3 == secondary.Index ? 1 : -1;
        columns[3 - primary.Index - secondary.Index] = Cross(first, second) * permutationSign;
        basis = new(columns[0], columns[1], columns[2]);
        return true;
    }

    private static Vector3d Cross(Vector3d left, Vector3d right) =>
        new(left.Y * right.Z - left.Z * right.Y, left.Z * right.X - left.X * right.Z,
            left.X * right.Y - left.Y * right.X);
}
