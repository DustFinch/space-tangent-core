// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;

namespace SpaceTangent.Core.Observing;

/// <summary>Local observing conditions. Extinction is in visual magnitudes per zenith airmass.</summary>
public readonly record struct GroundSkyConditions(int Bortle, double TemperatureCelsius = 10,
    double? PressureHectopascals = null, double Extinction = .20, bool Refraction = true, bool Scintillation = true)
{
    private const double Radians = Math.PI / 180;
    private static readonly double[] Limits = [7.8, 7.3, 6.8, 6.3, 5.8, 5.3, 4.8, 4.3, 4.0];
    private static readonly double[] SkyQuality = [21.9, 21.6, 21.4, 20.9, 20, 19.2, 18.6, 18.2, 17.5];

    public bool IsValid() => Bortle is >= 1 and <= 9 && TemperatureCelsius is >= -100 and <= 60 &&
        PressureHectopascals is null or (>= 0 and <= 1100) && Extinction is >= 0 and <= 2;

    public double LimitingMagnitude() => Limits[Bortle - 1];
    public double ZenithGlow() => .0035 * (Math.Pow(10, -.4 * (SkyQuality[Bortle - 1] - 22)) - 1);

    public double PressureAt(EarthSite site) => PressureHectopascals ??
        1013.25 * Math.Pow(Math.Max(1 - .0225577 * site.HeightKilometers, 0), 5.25588);

    /// <summary>Sæmundsson's astronomical refraction at the site's pressure and temperature.</summary>
    public double ApparentAltitude(double altitudeDegrees, EarthSite site)
    {
        if (!Refraction || altitudeDegrees < -2 || altitudeDegrees >= 90)
            return altitudeDegrees;
        double factor = PressureAt(site) / 1010 * 283 / (273 + TemperatureCelsius);
        double angle = (altitudeDegrees + 10.3 / (altitudeDegrees + 5.11)) * Radians;
        return Math.Min(90, altitudeDegrees + Math.Max(0, factor * 1.02 / Math.Tan(angle) / 60));
    }

    public Vector3d RefractDirection(Vector3d direction, Vector3d zenith, EarthSite site)
    {
        if (!Refraction)
            return direction;
        double sine = Math.Clamp(Vector3d.Dot(direction, zenith), -1, 1);
        double altitude = Math.Asin(sine) / Radians;
        var horizontal = direction - zenith * sine;
        if (horizontal.Length < 1e-9)
            return direction;
        var (up, along) = Math.SinCos(ApparentAltitude(altitude, site) * Radians);
        return horizontal * (along / horizontal.Length) + zenith * up;
    }

    /// <summary>Kasten and Young's relative optical airmass using apparent elevation.</summary>
    public static double Airmass(double apparentAltitudeDegrees)
    {
        double angle = Math.Clamp(apparentAltitudeDegrees, 0, 90);
        return 1 / (Math.Sin(angle * Radians) + .50572 * Math.Pow(angle + 6.07995, -1.6364));
    }

    public double AttenuationMagnitude(double apparentAltitudeDegrees) => Extinction * Airmass(apparentAltitudeDegrees);

    public double ScintillationFactor(double apparentAltitudeDegrees, double timeSeconds, double phase)
    {
        if (!Scintillation)
            return 1;
        double amplitude = .4 * Math.Min(1, .12 * Airmass(apparentAltitudeDegrees));
        return 1 + amplitude * (.6 * Math.Sin(21 * timeSeconds + phase) +
            .4 * Math.Sin(34 * timeSeconds + 2.3 * phase));
    }
}
