// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Coordinates;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Time;

namespace SpaceTangent.Core.Observing;

public readonly record struct EarthSite(double LatitudeDegrees, double LongitudeDegrees, double HeightKilometers)
{
    public bool IsValid() => LatitudeDegrees is >= -90 and <= 90 && LongitudeDegrees is >= -180 and <= 180 &&
        double.IsFinite(HeightKilometers) && HeightKilometers >= 0;
}

/// <summary>
/// An observer on the Earth. Positions and axes are Earth-centred in the world (ICRF) axes.
/// <see cref="EarthRotationRadians"/> is the right ascension of the Greenwich meridian in world axes, for
/// consumers that rotate an Earth model about the world Z axis; <see cref="EarthToWorld"/> is the full rotation.
/// </summary>
public readonly record struct SurfaceLighting(Vector3d PositionKilometers, Basis3d EastNorthUp,
    double EarthRotationRadians, Vector3d EarthToSun, Vector3d EarthToMoon, double RelativeLunarFlux)
{
    /// <summary>Rotation from Earth-fixed axes (X to Greenwich, Z to the pole) to the world axes.</summary>
    public Basis3d EarthToWorld { get; init; } = Basis3d.Identity;

    public double SolarAltitudeRadians => Altitude(EarthToSun - PositionKilometers);
    public double LunarAltitudeRadians => Altitude(EarthToMoon - PositionKilometers);

    private double Altitude(Vector3d direction) =>
        Math.Asin(Math.Clamp(Vector3d.Dot(EastNorthUp.Z, direction) / direction.Length, -1, 1));
}

public static class EarthSurfaceLighting
{
    public const double EarthRadiusKilometers = 6378.1366;
    public const double EarthFlattening = 1 / 298.257223563;
    public const double MoonRadiusKilometers = 1737.4;
    private const double SolarRadiusKilometers = 695_700;
    private const double MeanLunarDistanceKilometers = 384_400;

    /// <summary>
    /// A geodetic site on the reference ellipsoid, oriented by <see cref="EarthOrientation"/> (precession, nutation and
    /// the Earth rotation angle from the selected ΔT model). The up axis is the ellipsoid normal.
    /// </summary>
    public static SurfaceLighting Sample(SolarSystemSample system, EarthSite site, DeltaTModel deltaT)
    {
        if (!site.IsValid())
            throw new ArgumentOutOfRangeException(nameof(site));

        Basis3d toWorld = EarthOrientation.TerrestrialToWorld(system.Epoch, deltaT).ToWorld;
        double longitude = site.LongitudeDegrees * (Math.PI / 180);
        double latitude = site.LatitudeDegrees * (Math.PI / 180);
        var (sinLongitude, cosLongitude) = Math.SinCos(longitude);
        var (sinLatitude, cosLatitude) = Math.SinCos(latitude);
        var east = new Vector3d(-sinLongitude, cosLongitude, 0);
        var north = new Vector3d(-sinLatitude * cosLongitude, -sinLatitude * sinLongitude, cosLatitude);
        var up = new Vector3d(cosLatitude * cosLongitude, cosLatitude * sinLongitude, sinLatitude);
        double eccentricitySquared = EarthFlattening * (2 - EarthFlattening);
        double normalRadius = EarthRadiusKilometers / Math.Sqrt(1 - eccentricitySquared * sinLatitude * sinLatitude);
        var position = new Vector3d((normalRadius + site.HeightKilometers) * cosLatitude * cosLongitude,
            (normalRadius + site.HeightKilometers) * cosLatitude * sinLongitude,
            (normalRadius * (1 - eccentricitySquared) + site.HeightKilometers) * sinLatitude);

        Vector3d sun = (system.Sun - system.Earth).PositionKilometers;
        Vector3d moon = (system.Moon - system.Earth).PositionKilometers;
        double greenwich = Math.Atan2(toWorld.X.Y, toWorld.X.X);
        return new(toWorld.Transform(position),
            new(toWorld.Transform(east), toWorld.Transform(north), toWorld.Transform(up)),
            greenwich < 0 ? greenwich + Math.Tau : greenwich, sun, moon, RelativeLunarFlux(sun, moon))
        {
            EarthToWorld = toWorld
        };
    }

    public static double RelativeLunarFlux(Vector3d earthToSun, Vector3d earthToMoon)
    {
        double moonDistance = earthToMoon.Length;
        Vector3d moonToSun = earthToSun - earthToMoon;
        double solarDistance = moonToSun.Length;
        if (!(moonDistance > EarthRadiusKilometers && solarDistance > SolarRadiusKilometers) ||
            !double.IsFinite(moonDistance) || !double.IsFinite(solarDistance))
            return 0;

        double cosine = Math.Clamp(-Vector3d.Dot(earthToMoon / moonDistance, moonToSun / solarDistance), -1, 1);
        double phase = Math.Acos(cosine);
        double reflection = Math.Max(0, (Math.Sin(phase) + (Math.PI - phase) * cosine) / Math.PI);
        double earthAngle = Math.Asin(EarthRadiusKilometers / moonDistance);
        double sunAngle = Math.Asin(SolarRadiusKilometers / solarDistance);
        double visible = 1;
        if (phase < earthAngle + sunAngle)
        {
            double covered = Math.Min(1, Math.Pow(earthAngle / sunAngle, 2));
            double transition = Math.Clamp((earthAngle + sunAngle - phase) /
                (2 * Math.Min(earthAngle, sunAngle)), 0, 1);
            visible -= covered * transition;
        }

        double scale = MeanLunarDistanceKilometers / moonDistance *
            AstronomicalUnits.KilometersPerAstronomicalUnit / solarDistance;
        return reflection * visible * scale * scale;
    }
}
