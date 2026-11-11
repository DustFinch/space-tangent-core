// SPDX-License-Identifier: GPL-2.0-or-later
namespace SpaceTangent.Contracts;

/// <summary>A local vector. The surrounding API specifies units and reference frame.</summary>
public readonly record struct Vector3d(double X, double Y, double Z)
{
    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z);

    public double Length
    {
        get
        {
            double scale = Math.Max(Math.Abs(X), Math.Max(Math.Abs(Y), Math.Abs(Z)));
            if (scale == 0 || !double.IsFinite(scale))
                return scale;

            Vector3d normalized = this / scale;
            return scale * Math.Sqrt(normalized.X * normalized.X + normalized.Y * normalized.Y +
                                     normalized.Z * normalized.Z);
        }
    }

    public static double Dot(Vector3d left, Vector3d right) => left.X * right.X + left.Y * right.Y + left.Z * right.Z;

    public static Vector3d operator +(Vector3d left, Vector3d right) =>
        new(left.X + right.X, left.Y + right.Y, left.Z + right.Z);

    public static Vector3d operator -(Vector3d left, Vector3d right) =>
        new(left.X - right.X, left.Y - right.Y, left.Z - right.Z);

    public static Vector3d operator *(Vector3d value, double scale) =>
        new(value.X * scale, value.Y * scale, value.Z * scale);

    public static Vector3d operator /(Vector3d value, double scale) =>
        new(value.X / scale, value.Y / scale, value.Z / scale);
}
