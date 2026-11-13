// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using System.Text.RegularExpressions;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Addons;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Time;

namespace SpaceTangent.Core.Observing;

public readonly record struct SatelliteFlare(JulianInstant Peak, double Magnitude, HorizontalCoordinates Horizontal,
    double ReflectionAngleDegrees, double SunAltitudeDegrees);

public static partial class ObservationEventSearch
{
    private const double MaximumReflectionAngle = 3;
    private const double PanelTilt = 40 / Degrees;
    private const double SunSolidAngle = Math.PI * (.2666 / Degrees) * (.2666 / Degrees);

    [GeneratedRegex(@"^IRIDIUM\s+(\d+)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex IridiumName();

    public static bool HasFlarePanels(LocalTleSatellite satellite)
    {
        var match = IridiumName().Match(satellite.Name.Trim());
        return match.Success && !satellite.Name.Contains("[-]", StringComparison.Ordinal) &&
            int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int number) &&
            number is > 0 and < 100 or >= 900 and < 1000;
    }

    /// <summary>Finds predicted antenna reflections of first-generation Iridium satellites above the geometric horizon.</summary>
    public static IReadOnlyList<SatelliteFlare> FindSatelliteFlares(ISolarSystemEphemeris ephemeris,
        LocalTleSatellite satellite, EarthSite site, DeltaTModel deltaT, JulianInstant from, JulianInstant to,
        double faintestMagnitude = 1, CancellationToken cancellation = default)
    {
        ValidateSite(site);
        if (!double.IsFinite(faintestMagnitude))
            throw new ArgumentOutOfRangeException(nameof(faintestMagnitude));
        double duration = ValidateWindow(from, to, 4);
        if (!HasFlarePanels(satellite))
            return Array.Empty<SatelliteFlare>();

        var result = new List<SatelliteFlare>();
        foreach (var pass in FindSatellitePasses(ephemeris, epoch => satellite.SampleInWorld(epoch, deltaT),
                     site, deltaT, from, to, cancellation: cancellation))
        {
            var start = pass.AboveHorizon.First ?? from;
            var end = pass.AboveHorizon.Last ?? to;
            double passDuration = (double)end.DifferenceNanoseconds(start) / JulianInstant.NanosecondsPerSecond;
            if (passDuration <= 0 || passDuration > duration)
                continue;
            foreach (var peak in FindPeaks(passDuration, 2, Score, includeEdges: false, -MaximumReflectionAngle, cancellation))
            {
                var epoch = start.AddSeconds(peak.Seconds);
                if (Look(epoch) is not { } top)
                    continue;
                double fading = top.Angle switch
                {
                    < .5 => 6 * top.Angle,
                    < .7 => 3 + 10 * (top.Angle - .5),
                    _ => 5 + 5 * (top.Angle - .7)
                };
                double reflectedSolidAngle = .8 * 1.88 * .86 * 1e-6 * top.Cosine / (top.Range * top.Range);
                double magnitude = -26.74 - 2.5 * Math.Log10(reflectedSolidAngle / SunSolidAngle) + fading;
                if (magnitude <= faintestMagnitude)
                    result.Add(new(epoch, magnitude, top.Horizontal, top.Angle, top.SunAltitude));
            }

            double? Score(double seconds) => Look(start.AddSeconds(seconds)) is { } look ? -look.Angle : null;
        }
        return result.AsReadOnly();

        (double Angle, double Cosine, double Range, HorizontalCoordinates Horizontal, double SunAltitude)? Look(JulianInstant epoch)
        {
            if (!Covers(ephemeris, epoch) || satellite.SampleInWorld(epoch, deltaT) is not { } state)
                return null;
            var observer = EarthSurfaceLighting.Sample(ephemeris.Sample(epoch), site, deltaT);
            var position = state.PositionKilometers;
            var toObserver = observer.PositionKilometers - position;
            if (EarthObserving.HorizontalDirection(observer, toObserver * -1) is not { AltitudeDegrees: > 0 } horizontal)
                return null;
            var sunDirection = observer.EarthToSun / observer.EarthToSun.Length;
            double along = Vector3d.Dot(position, sunDirection);
            if (along < 0 && (position - sunDirection * along).Length < EarthRadius)
                return null;

            var up = position / position.Length;
            var velocity = state.VelocityKilometersPerSecond;
            var tangent = velocity - up * Vector3d.Dot(velocity, up);
            var forward = tangent / tangent.Length;
            var right = Vector3d.Cross(forward, up);
            var toSun = observer.EarthToSun - position;
            toSun /= toSun.Length;
            double range = toObserver.Length;
            var outgoing = toObserver / range;
            double best = double.PositiveInfinity, cosine = 0;
            var (tiltSin, tiltCos) = Math.SinCos(PanelTilt);
            for (int panel = 0; panel < 3; panel++)
            {
                var (sin, cos) = Math.SinCos(panel * Math.Tau / 3);
                var normal = (forward * cos + right * sin) * tiltCos - up * tiltSin;
                double incidence = Vector3d.Dot(toSun, normal), projection = Vector3d.Dot(outgoing, normal);
                if (incidence <= 0 || projection <= 0)
                    continue;
                var reflected = normal * (2 * incidence) - toSun;
                double angle = Degrees * Math.Atan2(Vector3d.Cross(reflected, outgoing).Length, Vector3d.Dot(reflected, outgoing));
                if (angle < best)
                    (best, cosine) = (angle, projection);
            }
            return (best, cosine, range, horizontal, observer.SolarAltitudeRadians * Degrees);
        }
    }
}
