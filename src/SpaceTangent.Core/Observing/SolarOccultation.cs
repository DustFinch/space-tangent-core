// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;

namespace SpaceTangent.Core.Observing;

/// <summary>Geometric occultation of a uniformly bright solar disc by a foreground sphere.</summary>
public static class SolarOccultation
{
    public const double SunRadiusKilometers = 695_700;

    /// <summary>Vectors are from the same receiving point to the Sun and the occluder, in kilometers.</summary>
    public static double VisibleFraction(Vector3d toSun, Vector3d toOccluder, double occluderRadiusKilometers)
    {
        double sunDistance = toSun.Length, foregroundDistance = toOccluder.Length;
        if (!double.IsFinite(sunDistance) || !double.IsFinite(foregroundDistance) ||
            !double.IsFinite(occluderRadiusKilometers) || sunDistance <= SunRadiusKilometers || occluderRadiusKilometers <= 0)
            throw new ArgumentOutOfRangeException(nameof(occluderRadiusKilometers), "The occultation needs finite, exterior solar geometry and a positive foreground radius.");
        if (foregroundDistance <= occluderRadiusKilometers)
            return 0;
        if (foregroundDistance >= sunDistance)
            return 1;

        Vector3d sun = toSun / sunDistance, foreground = toOccluder / foregroundDistance;
        double separation = Math.Atan2(Vector3d.Cross(sun, foreground).Length, Vector3d.Dot(sun, foreground));
        double r = Math.Asin(SunRadiusKilometers / sunDistance), q = Math.Asin(occluderRadiusKilometers / foregroundDistance);
        if (separation >= r + q)
            return 1;
        if (separation <= Math.Abs(r - q))
            return q >= r ? 0 : 1 - q * q / (r * r);

        double d2 = separation * separation, r2 = r * r, q2 = q * q;
        double angleSun = Math.Acos(Math.Clamp((d2 + r2 - q2) / (2 * separation * r), -1, 1));
        double angleForeground = Math.Acos(Math.Clamp((d2 + q2 - r2) / (2 * separation * q), -1, 1));
        double radical = (-separation + r + q) * (separation + r - q) * (separation - r + q) * (separation + r + q);
        double area = r2 * angleSun + q2 * angleForeground - .5 * Math.Sqrt(Math.Max(0, radical));
        return Math.Clamp(1 - area / (Math.PI * r2), 0, 1);
    }
}
