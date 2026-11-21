// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.World;

namespace SpaceTangent.Core.Observing;

/// <summary>A surface observer's barycentric state and geodetic east/north/up axes in world coordinates.</summary>
public readonly record struct BodySurfaceFrame(CartesianState Observer, Basis3d EastNorthUp)
{
    public HorizontalCoordinates? Horizontal(Vector3d target) => Direction(target - Observer.PositionKilometers);

    public HorizontalCoordinates? Direction(Vector3d offset)
    {
        if (!offset.IsFinite || !(offset.Length > 0)) return null;
        var unit = offset / offset.Length;
        double east = Vector3d.Dot(unit, EastNorthUp.X), north = Vector3d.Dot(unit, EastNorthUp.Y), up = Vector3d.Dot(unit, EastNorthUp.Z);
        double azimuth = Math.Atan2(east, north) * 180 / Math.PI;
        return new(Math.Atan2(up, Math.Sqrt(east * east + north * north)) * 180 / Math.PI,
            east * east + north * north < 1e-24 ? null : (azimuth + 360) % 360);
    }

    public Vector3d LookDirection(double azimuthDegrees, double altitudeDegrees)
    {
        var (sa, ca) = Math.SinCos(azimuthDegrees * Math.PI / 180);
        var (se, ce) = Math.SinCos(altitudeDegrees * Math.PI / 180);
        return EastNorthUp.X * (sa * ce) + EastNorthUp.Y * (ca * ce) + EastNorthUp.Z * se;
    }

    /// <summary>Geodetic ellipsoid, east-positive longitude. Orientation is fixed-body to world;
    /// angular velocity is world-coordinate radians per second. No atmosphere/refraction model is implied.</summary>
    public static BodySurfaceFrame OnEllipsoid(CartesianState center, FrameKinematics rotation,
        EarthSite site, double equatorialRadiusKilometers, double polarRadiusKilometers)
    {
        if (!site.IsValid() || !center.PositionKilometers.IsFinite || !center.VelocityKilometersPerSecond.IsFinite ||
            !(equatorialRadiusKilometers > 0) || !(polarRadiusKilometers > 0) ||
            site.HeightKilometers <= -Math.Min(equatorialRadiusKilometers, polarRadiusKilometers) ||
            !double.IsFinite(equatorialRadiusKilometers + polarRadiusKilometers))
            throw new ArgumentOutOfRangeException(nameof(site));
        var (sl, cl) = Math.SinCos(site.LongitudeDegrees * Math.PI / 180);
        var (sp, cp) = Math.SinCos(site.LatitudeDegrees * Math.PI / 180);
        double a = equatorialRadiusKilometers, b = polarRadiusKilometers;
        double prime = a * a / Math.Sqrt(a * a * cp * cp + b * b * sp * sp);
        Vector3d fixedPosition = new((prime + site.HeightKilometers) * cp * cl,
            (prime + site.HeightKilometers) * cp * sl, (prime * b * b / (a * a) + site.HeightKilometers) * sp);
        var offset = rotation.ToWorld.Transform(fixedPosition);
        var enu = new Basis3d(rotation.ToWorld.Transform(new(-sl, cl, 0)),
            rotation.ToWorld.Transform(new(-sp * cl, -sp * sl, cp)), rotation.ToWorld.Transform(new(cp * cl, cp * sl, sp)));
        return new(center + new CartesianState(offset,
            Vector3d.Cross(rotation.AngularVelocityRadiansPerSecond.GetValueOrDefault(), offset)), enu);
    }
}
