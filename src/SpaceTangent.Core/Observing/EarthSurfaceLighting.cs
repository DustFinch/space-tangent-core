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

public readonly record struct SurfaceLighting(Vector3d PositionKilometers, Basis3d EastNorthUp,
    double EarthRotationRadians, Vector3d EarthToSun, Vector3d EarthToMoon, double RelativeLunarFlux)
{
    public double SolarAltitudeRadians => Altitude(EarthToSun - PositionKilometers);
    public double LunarAltitudeRadians => Altitude(EarthToMoon - PositionKilometers);

    private double Altitude(Vector3d direction) =>
        Math.Asin(Math.Clamp(Vector3d.Dot(EastNorthUp.Z, direction) / direction.Length, -1, 1));
}

public static class EarthSurfaceLighting
{
    public const double EarthRadiusKilometers = 6378.1366;
    public const double MoonRadiusKilometers = 1737.4;
    private const double SolarRadiusKilometers = 695_700;
    private const double MeanLunarDistanceKilometers = 384_400;

    /// <summary>Mean sidereal rotation on a spherical Earth, with ICRF axes used as the mean equator approximation.</summary>
    public static SurfaceLighting Sample(SolarSystemSample system, EarthSite site, DeltaTModel deltaT)
    {
        if (!site.IsValid())
            throw new ArgumentOutOfRangeException(nameof(site));

        double angle = GreenwichSiderealAngle(system.Epoch, deltaT);
        double longitude = angle + site.LongitudeDegrees * (Math.PI / 180);
        double latitude = site.LatitudeDegrees * (Math.PI / 180);
        var (sinLongitude, cosLongitude) = Math.SinCos(longitude);
        var (sinLatitude, cosLatitude) = Math.SinCos(latitude);
        var east = new Vector3d(-sinLongitude, cosLongitude, 0);
        var north = new Vector3d(-sinLatitude * cosLongitude, -sinLatitude * sinLongitude, cosLatitude);
        var up = new Vector3d(cosLatitude * cosLongitude, cosLatitude * sinLongitude, sinLatitude);

        Vector3d sun = (system.Sun - system.Earth).PositionKilometers;
        Vector3d moon = (system.Moon - system.Earth).PositionKilometers;
        return new(up * (EarthRadiusKilometers + site.HeightKilometers), new(east, north, up),
            angle, sun, moon, RelativeLunarFlux(sun, moon));
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

    internal static double GreenwichSiderealAngle(JulianInstant epoch, DeltaTModel deltaT)
    {
        JulianInstant tt = TimeConversion.Convert(epoch, TimeScale.Tt);
        double ttDays = (double)tt.DifferenceNanoseconds(new(JulianInstant.J2000Day, 0, TimeScale.Tt)) /
            JulianInstant.NanosecondsPerDay;
        double ut1Days = ttDays - DeltaT.Seconds(tt, deltaT) / JulianInstant.SecondsPerDay;
        return SiderealAngle(ut1Days, ttDays / 36_525);
    }

    private static double SiderealAngle(double ut1Days, double ttCenturies)
    {
        // IAU 2006 GMST, expressed as Earth rotation angle plus the precession in right ascension.
        double turns = .7790572732640 + 1.00273781191135448 * ut1Days;
        double t = ttCenturies;
        double arcseconds = .014506 + t * (4612.156534 + t * (1.3915817 +
            t * (-.00000044 + t * (-.000029956 - t * .0000000368))));
        return (turns - Math.Floor(turns)) * Math.Tau + arcseconds * (Math.PI / (180 * 3600));
    }
}
