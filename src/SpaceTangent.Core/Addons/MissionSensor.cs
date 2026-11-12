// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;

namespace SpaceTangent.Core.Addons;

public sealed record MissionSensor(ObjectId Target, double RangeKilometers, Vector3d Color, double Opacity,
    Vector3d Boresight, IReadOnlyList<Vector3d> Boundary, int GridSpokes);

public readonly record struct MissionSensorRay(Vector3d FrustumEnd, Vector3d? SurfacePoint);

/// <summary>Projects a sensor ray in the target's body-fixed axes. Direction must be a unit vector.</summary>
public static class MissionSensorProjection
{
    public static MissionSensorRay Project(Vector3d origin, Vector3d direction, Vector3d semiAxes, double range)
    {
        double end = range;
        Vector3d? footprint = null;
        if (TryCrossing(origin, direction, semiAxes, out double hit))
        {
            end = Math.Min(range, hit * .9999);
            if (hit <= range)
                footprint = origin + direction * hit;
        }
        return new(origin + direction * end, footprint);
    }

    /// <summary>Returns the first strictly positive crossing. Tangents do not cross the surface.</summary>
    public static bool TryCrossing(Vector3d origin, Vector3d direction, Vector3d semiAxes, out double distance)
    {
        distance = 0;
        if (!origin.IsFinite || !direction.IsFinite || !semiAxes.IsFinite ||
            semiAxes.X <= 0 || semiAxes.Y <= 0 || semiAxes.Z <= 0)
            return false;
        var inverse = new Vector3d(1 / semiAxes.X, 1 / semiAxes.Y, 1 / semiAxes.Z);
        var weights = new Vector3d(inverse.X * inverse.X, inverse.Y * inverse.Y, inverse.Z * inverse.Z);
        double a = Vector3d.Dot(Weighted(direction, direction), weights);
        double b = Vector3d.Dot(Weighted(origin, direction), weights);
        double c = Vector3d.Dot(Weighted(origin, origin), weights) - 1;
        double discriminant = b * b - a * c;
        if (!(a > 0) || !(discriminant > 0))
            return false;
        double root = Math.Sqrt(discriminant);
        double first = (-b - root) / a;
        double second = (-b + root) / a;
        double crossing = first > 0 ? first : second;
        if (!(crossing > 0) || !double.IsFinite(crossing))
            return false;
        distance = crossing;
        return true;
    }

    private static Vector3d Weighted(Vector3d first, Vector3d second) =>
        new(first.X * second.X, first.Y * second.Y, first.Z * second.Z);
}
