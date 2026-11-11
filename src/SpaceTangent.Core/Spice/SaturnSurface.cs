// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;

namespace SpaceTangent.Core.Spice;

/// <summary>Geometric Saturn surface from a center ephemeris and the IAU text PCK model.</summary>
public sealed class SaturnSurface
{
    private readonly SpkKernel _centers;
    private readonly SpicePolynomialBodyFrame _frame;
    public Vector3d RadiiKilometers => _frame.RadiiKilometers;

    public SaturnSurface(string centers, string pck)
    {
        _centers = new SpkKernel(centers);
        _frame = new(pck, 699);
    }

    /// <summary>Returns a pose relative to the Saturn system barycenter in J2000 axes.</summary>
    public bool TrySample(JulianInstant tdb, out SaturnSurfacePose pose)
    {
        if (!_centers.TryState(699, 6, tdb, out var center))
        {
            pose = default;
            return false;
        }
        pose = new(center.PositionKilometers, _frame.Sample(tdb).ToWorld.Transpose(), RadiiKilometers);
        return true;
    }
}

public readonly struct SaturnSurfacePose
{
    internal SaturnSurfacePose(Vector3d center, Basis3d toFixed, Vector3d radii)
    {
        CenterKilometers = center;
        InertialToFixed = toFixed;
        RadiiKilometers = radii;
    }

    public Vector3d CenterKilometers { get; }
    public Basis3d InertialToFixed { get; }
    public Vector3d RadiiKilometers { get; }

    /// <summary>Intersects a J2000 ray with the ellipsoid. Origin and hit are relative to the system barycenter.</summary>
    public bool TryIntercept(Vector3d origin, Vector3d direction, double rangeKilometers, out Vector3d hit)
    {
        hit = default;
        double length = direction.Length;
        if (!origin.IsFinite || !double.IsFinite(length) || length <= 0 ||
            !double.IsFinite(rangeKilometers) || rangeKilometers < 0 || RadiiKilometers.X <= 0)
            return false;

        Vector3d unit = direction / length;
        Vector3d p = Scale(InertialToFixed.Transform(origin - CenterKilometers));
        Vector3d d = Scale(InertialToFixed.Transform(unit));
        double a = Vector3d.Dot(d, d), b = Vector3d.Dot(p, d), c = Vector3d.Dot(p, p) - 1;
        double discriminant = b * b - a * c;
        if (!double.IsFinite(discriminant) || discriminant < 0)
            return false;

        double q = -b - Math.CopySign(Math.Sqrt(discriminant), b);
        double first = q / a, second = q == 0 ? first : c / q;
        double near = Math.Min(first, second), far = Math.Max(first, second);
        double distance = near >= 0 ? near : far;
        if (!double.IsFinite(distance) || distance < 0 || distance > rangeKilometers)
            return false;
        hit = origin + unit * distance;
        return hit.IsFinite;
    }

    private Vector3d Scale(Vector3d value) => new(value.X / RadiiKilometers.X,
        value.Y / RadiiKilometers.Y, value.Z / RadiiKilometers.Z);
}
