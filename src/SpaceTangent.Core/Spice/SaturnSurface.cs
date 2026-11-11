// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;

namespace SpaceTangent.Core.Spice;

/// <summary>Geometric Saturn surface from a center ephemeris and the IAU text PCK model.</summary>
public sealed class SaturnSurface
{
    private readonly SpkKernel _centers;
    private readonly double[] _rightAscension, _declination, _meridian;
    public Vector3d RadiiKilometers { get; }

    public SaturnSurface(string centers, string pck)
    {
        _centers = new SpkKernel(centers);
        var pool = new SpiceTextKernel();
        pool.Furnish(pck);
        if (pool.Contains("BODY699_NUT_PREC_RA") || pool.Contains("BODY699_NUT_PREC_DEC") ||
            pool.Contains("BODY699_NUT_PREC_PM") || pool.Contains("BODY6_CONSTANTS_REF_FRAME") ||
            pool.Contains("BODY699_CONSTANTS_REF_FRAME") || pool.Contains("BODY6_CONSTANTS_JED_EPOCH") ||
            pool.Contains("BODY699_CONSTANTS_JED_EPOCH"))
            throw new NotSupportedException("Saturn requires the standard J2000 polynomial PCK model.");
        _rightAscension = Triple(pool, "BODY699_POLE_RA");
        _declination = Triple(pool, "BODY699_POLE_DEC");
        _meridian = Triple(pool, "BODY699_PM");
        double[] radii = Triple(pool, "BODY699_RADII");
        if (radii.Any(radius => radius <= 0))
            throw new InvalidDataException("Saturn radii must be positive.");
        RadiiKilometers = new(radii[0], radii[1], radii[2]);
    }

    /// <summary>Returns a pose relative to the Saturn system barycenter in J2000 axes.</summary>
    public bool TrySample(JulianInstant tdb, out SaturnSurfacePose pose)
    {
        if (!_centers.TryState(699, 6, tdb, out var center))
        {
            pose = default;
            return false;
        }
        double days = (tdb.Day - JulianInstant.J2000Day) +
            tdb.NanosecondsOfDay / (double)JulianInstant.NanosecondsPerDay;
        double centuries = days / 36525;
        const double Radians = Math.PI / 180;
        double meridian = Math.IEEERemainder(Polynomial(_meridian, days), 360);
        double declination = Polynomial(_declination, centuries);
        double rightAscension = Polynomial(_rightAscension, centuries);
        Basis3d toFixed = SpiceEuler.Compose(
            [meridian * Radians, (90 - declination) * Radians, (90 + rightAscension) * Radians], [3, 1, 3]);
        pose = new(center.PositionKilometers, toFixed, RadiiKilometers);
        return true;
    }

    private static double[] Triple(SpiceTextKernel pool, string field)
    {
        double[] values = pool.GetNumbers(field);
        return values.Length == 3 ? values : throw new InvalidDataException($"Expected three values in {field}.");
    }

    private static double Polynomial(double[] values, double time) => (values[2] * time + values[1]) * time + values[0];
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
