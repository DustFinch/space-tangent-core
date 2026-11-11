// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;

namespace SpaceTangent.Core.Coordinates;

internal static class QuaternionBasis
{
    /// <summary>Matrix expression shared with file formats that preserve non-unit WXYZ values.</summary>
    internal static Basis3d FromRaw(double w, double x, double y, double z)
    {
        var basis = new Basis3d(new(1 - 2 * (y * y + z * z), 2 * (x * y + w * z), 2 * (x * z - w * y)),
            new(2 * (x * y - w * z), 1 - 2 * (x * x + z * z), 2 * (y * z + w * x)),
            new(2 * (x * z + w * y), 2 * (y * z - w * x), 1 - 2 * (x * x + y * y)));
        if (!(basis.X.IsFinite && basis.Y.IsFinite && basis.Z.IsFinite))
            throw new InvalidDataException("Quaternion coefficients exceed the supported range.");
        return basis;
    }
}

internal readonly record struct RigidQuaternion(double W, double X, double Y, double Z)
{
    internal RigidQuaternion Normalize()
    {
        double scale = Math.Max(Math.Max(Math.Abs(W), Math.Abs(X)), Math.Max(Math.Abs(Y), Math.Abs(Z)));
        if (!(scale > 0) || !double.IsFinite(scale))
            throw new InvalidDataException("Invalid orientation quaternion.");
        double w = W / scale, x = X / scale, y = Y / scale, z = Z / scale;
        double length = Math.Sqrt(w * w + x * x + y * y + z * z);
        return new(w / length, x / length, y / length, z / length);
    }

    internal Basis3d InverseBasis() => QuaternionBasis.FromRaw(W, -X, -Y, -Z);

    internal static RigidQuaternion Interpolate(RigidQuaternion first, RigidQuaternion last, double fraction)
    {
        double cosine = first.W * last.W + first.X * last.X + first.Y * last.Y + first.Z * last.Z;
        if (cosine < 0)
        {
            last = new(-last.W, -last.X, -last.Y, -last.Z);
            cosine = -cosine;
        }

        double a = 1 - fraction, b = fraction;
        if (cosine < 0.999999999)
        {
            double angle = Math.Acos(cosine);
            double sine = Math.Sin(angle);
            a = Math.Sin(a * angle) / sine;
            b = Math.Sin(b * angle) / sine;
        }
        return new RigidQuaternion(first.W * a + last.W * b, first.X * a + last.X * b,
            first.Y * a + last.Y * b, first.Z * a + last.Z * b).Normalize();
    }
}
