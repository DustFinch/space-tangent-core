// SPDX-License-Identifier: GPL-2.0-or-later
namespace SpaceTangent.Contracts;

/// <summary>Three Cartesian columns describing a linear transformation.</summary>
public readonly record struct Basis3d(Vector3d X, Vector3d Y, Vector3d Z)
{
    public static Basis3d Identity => new(new(1, 0, 0), new(0, 1, 0), new(0, 0, 1));
    public Vector3d Transform(Vector3d vector) => X * vector.X + Y * vector.Y + Z * vector.Z;
}
