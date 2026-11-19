// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Coordinates;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Time;

namespace SpaceTangent.Core.Observing;

public enum EclipseClass { Penumbral, Partial, Annular, Total }

/// <summary>Contact instants are absent where the search interval or the data clipped a phase.</summary>
public readonly record struct ObservationInterval(JulianInstant? First, JulianInstant Peak, JulianInstant? Last);
public readonly record struct SolarEclipseEvent(ObservationInterval Partial, ObservationInterval? Central,
    EclipseClass Class, double Obscuration, double SolarAltitudeDegrees);
public readonly record struct LunarEclipseEvent(ObservationInterval Penumbral, ObservationInterval? Umbral,
    ObservationInterval? Total, EclipseClass Class, double UmbralMagnitude);
public readonly record struct SatellitePass(ObservationInterval AboveHorizon, double PeakAltitudeDegrees, double? PeakAzimuthDegrees);
public readonly record struct Conjunction(JulianInstant Peak, double SeparationDegrees);

public static partial class ObservationEventSearch
{
    private const double Degrees = 180 / Math.PI;
    private const double MoonRadius = EarthSurfaceLighting.MoonRadiusKilometers;
    private const double EarthRadius = EarthSurfaceLighting.EarthRadiusKilometers;
    private const double SunRadius = SolarOccultation.SunRadiusKilometers;
    private const double LightKilometersPerSecond = 299_792.458;
    private const double ResolutionSeconds = .25;
    // Danjon's rule: the atmosphere enlarges the Earth's shadow as if its radius grew by 1/85; 0.998340 is the
    // mean radius across the shadow for an oblate Earth. This reproduces the NASA/GSFC lunar eclipse predictions.
    private const double EarthShadowRadius = EarthRadius * .998340 * (1 + 1d / 85);
    private readonly record struct Peak(double Seconds, double Score);

    public static IReadOnlyList<SolarEclipseEvent> FindSolarEclipses(ISolarSystemEphemeris ephemeris, EarthSite site,
        DeltaTModel deltaT, JulianInstant from, JulianInstant to, CancellationToken cancellation = default)
    {
        ValidateSite(site);
        double duration = ValidateWindow(from, to, 120);
        var result = new List<SolarEclipseEvent>();
        foreach (var peak in FindPeaks(duration, 120, PartialScore, includeEdges: true, 0, cancellation))
        {
            var geometry = Geometry(peak.Seconds)!.Value;
            double overlap = 1 - SolarOccultation.VisibleFraction(geometry.Sun, geometry.Moon, MoonRadius);
            bool total = geometry.MoonAngle >= geometry.SunAngle;
            bool central = CentralScore(peak.Seconds) >= 0;
            var kind = central ? total ? EclipseClass.Total : EclipseClass.Annular : EclipseClass.Partial;
            var contacts = Contacts(from, duration, peak.Seconds, 120, PartialScore, cancellation);
            result.Add(new(contacts, central ? Contacts(from, duration, peak.Seconds, 30, CentralScore, cancellation) : null,
                kind, overlap, geometry.Altitude * Degrees));
        }
        return result.AsReadOnly();

        double? PartialScore(double seconds) => Geometry(seconds) is { } value
            ? Math.Min(value.SunAngle + value.MoonAngle - value.Separation, value.Altitude + value.SunAngle) : null;

        double? CentralScore(double seconds) => Geometry(seconds) is { } value
            ? Math.Min(Math.Abs(value.MoonAngle - value.SunAngle) - value.Separation, value.Altitude + value.SunAngle) : null;

        (Vector3d Sun, Vector3d Moon, double SunAngle, double MoonAngle, double Separation, double Altitude)? Geometry(double seconds)
        {
            var epoch = from.AddSeconds(seconds);
            if (!Covers(ephemeris, epoch))
                return null;
            var sample = ephemeris.Sample(epoch);
            SurfaceLighting surface;
            try { surface = EarthSurfaceLighting.Sample(sample, site, deltaT); }
            catch (ArgumentOutOfRangeException) { return null; }
            Vector3d up = surface.EastNorthUp.Z;
            Vector3d eye = sample.Earth.PositionKilometers + surface.PositionKilometers;
            Vector3d sun = sample.Sun.PositionKilometers - eye, moon = sample.Moon.PositionKilometers - eye;
            var sunEmission = epoch.AddSeconds(-sun.Length / LightKilometersPerSecond);
            var moonEmission = epoch.AddSeconds(-moon.Length / LightKilometersPerSecond);
            if (!Covers(ephemeris, sunEmission) || !Covers(ephemeris, moonEmission))
                return null;
            sun = ephemeris.Sample(sunEmission).Sun.PositionKilometers - eye;
            moon = ephemeris.Sample(moonEmission).Moon.PositionKilometers - eye;
            double altitude = Math.Asin(Math.Clamp(Vector3d.Dot(up, sun / sun.Length), -1, 1));
            return (sun, moon, Math.Asin(SunRadius / sun.Length), Math.Asin(MoonRadius / moon.Length),
                Separation(sun, moon), altitude);
        }
    }

    public static IReadOnlyList<LunarEclipseEvent> FindLunarEclipses(ISolarSystemEphemeris ephemeris,
        JulianInstant from, JulianInstant to, CancellationToken cancellation = default)
    {
        double duration = ValidateWindow(from, to, 180);
        var result = new List<LunarEclipseEvent>();
        foreach (var peak in FindPeaks(duration, 180, Penumbra, includeEdges: true, 0, cancellation))
        {
            var value = Geometry(peak.Seconds)!.Value;
            var penumbra = Contacts(from, duration, peak.Seconds, 180, Penumbra, cancellation);
            double magnitude = (value.Umbra + MoonRadius - value.Offset) / (2 * MoonRadius);
            var kind = magnitude >= 1 ? EclipseClass.Total : magnitude >= 0 ? EclipseClass.Partial : EclipseClass.Penumbral;
            result.Add(new(penumbra, magnitude >= 0 ? Contacts(from, duration, peak.Seconds, 180, Umbra, cancellation) : null,
                magnitude >= 1 ? Contacts(from, duration, peak.Seconds, 60, Total, cancellation) : null, kind, magnitude));
        }
        return result.AsReadOnly();

        double? Penumbra(double seconds) => Geometry(seconds) is { } value ? value.Penumbra + MoonRadius - value.Offset : null;
        double? Umbra(double seconds) => Geometry(seconds) is { } value ? value.Umbra + MoonRadius - value.Offset : null;
        double? Total(double seconds) => Geometry(seconds) is { } value ? value.Umbra - MoonRadius - value.Offset : null;

        // The epoch is when an observer at the geocentre sees the Moon. That light left the Moon one light time
        // earlier; the sunlight that shaped the shadow passed the Earth one more light time before that and left
        // the Sun about 499 s earlier still.
        (double Offset, double Penumbra, double Umbra)? Geometry(double seconds)
        {
            var epoch = from.AddSeconds(seconds);
            if (!Covers(ephemeris, epoch))
                return null;
            var now = ephemeris.Sample(epoch);
            double lightTime = (now.Moon - now.Earth).PositionKilometers.Length / LightKilometersPerSecond;
            var atMoon = epoch.AddSeconds(-lightTime);
            var atEarth = atMoon.AddSeconds(-lightTime);
            if (!Covers(ephemeris, atEarth))
                return null;
            var earthSample = ephemeris.Sample(atEarth);
            Vector3d earth = earthSample.Earth.PositionKilometers;
            var atSun = atEarth.AddSeconds(-(earthSample.Sun.PositionKilometers - earth).Length / LightKilometersPerSecond);
            if (!Covers(ephemeris, atSun))
                return null;
            Vector3d earthAwayFromSun = earth - ephemeris.Sample(atSun).Sun.PositionKilometers;
            double sunDistance = earthAwayFromSun.Length;
            var axis = earthAwayFromSun / sunDistance;
            Vector3d earthToMoon = ephemeris.Sample(atMoon).Moon.PositionKilometers - earth;
            double along = Vector3d.Dot(earthToMoon, axis);
            if (along <= 0)
                return null;
            double offset = (earthToMoon - axis * along).Length;
            return (offset, EarthShadowRadius + along * (SunRadius + EarthRadius) / sunDistance,
                EarthShadowRadius - along * (SunRadius - EarthRadius) / sunDistance);
        }
    }

    public static IReadOnlyList<SatellitePass> FindSatellitePasses(ISolarSystemEphemeris ephemeris,
        Func<JulianInstant, CartesianState?> earthRelativeState, EarthSite site, DeltaTModel deltaT,
        JulianInstant from, JulianInstant to, double minimumAltitudeDegrees = 0, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(earthRelativeState);
        ValidateSite(site);
        if (!double.IsFinite(minimumAltitudeDegrees) || minimumAltitudeDegrees is < -90 or >= 90)
            throw new ArgumentOutOfRangeException(nameof(minimumAltitudeDegrees));
        double duration = ValidateWindow(from, to, 20);
        var result = new List<SatellitePass>();
        foreach (var peak in FindPeaks(duration, 20, Score, includeEdges: true, 0, cancellation))
        {
            var horizontal = Horizontal(peak.Seconds)!.Value;
            result.Add(new(Contacts(from, duration, peak.Seconds, 20, Score, cancellation), horizontal.AltitudeDegrees, horizontal.AzimuthDegrees));
        }
        return result.AsReadOnly();

        double? Score(double seconds) => Horizontal(seconds)?.AltitudeDegrees - minimumAltitudeDegrees;

        HorizontalCoordinates? Horizontal(double seconds)
        {
            var epoch = from.AddSeconds(seconds);
            if (!Covers(ephemeris, epoch) || earthRelativeState(epoch) is not { } satellite)
                return null;
            try
            {
                var observer = EarthSurfaceLighting.Sample(ephemeris.Sample(epoch), site, deltaT);
                return EarthObserving.Horizontal(observer, satellite.PositionKilometers);
            }
            catch (ArgumentOutOfRangeException) { return null; }
        }
    }

    public static IReadOnlyList<Conjunction> FindConjunctions(
        Func<JulianInstant, (Vector3d First, Vector3d Second)?> directions,
        JulianInstant from, JulianInstant to, double limitDegrees = 2, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(directions);
        if (!(limitDegrees > 0) || !double.IsFinite(limitDegrees) || limitDegrees > 180)
            throw new ArgumentOutOfRangeException(nameof(limitDegrees));
        double duration = ValidateWindow(from, to, 900);
        return Array.AsReadOnly(FindPeaks(duration, 900, Score, includeEdges: false, -limitDegrees, cancellation)
            .Select(peak => new Conjunction(from.AddSeconds(peak.Seconds), -peak.Score)).ToArray());

        double? Score(double seconds)
        {
            if (directions(from.AddSeconds(seconds)) is not { } pair || !(pair.First.Length > 0) || !(pair.Second.Length > 0))
                return null;
            return -Degrees * Separation(pair.First, pair.Second);
        }
    }

    private static List<Peak> FindPeaks(double duration, double step, Func<double, double?> score,
        bool includeEdges, double minimum, CancellationToken cancellation)
    {
        var peaks = new List<Peak>();
        step = Math.Min(step, duration / 2);
        double previousAt = 0, currentAt = Math.Min(step, duration);
        double initial = Read(0), previous = initial, current = Read(currentAt);
        if (initial > current)
            Add(Refine(0, 0, currentAt));
        while (currentAt < duration)
        {
            cancellation.ThrowIfCancellationRequested();
            double nextAt = Math.Min(duration, currentAt + step);
            if (nextAt == currentAt)
                break;
            double next = Read(nextAt);
            if (double.IsFinite(current) && current >= previous && current >= next && (current > previous || current > next))
            {
                Add(Refine(previousAt, currentAt, nextAt));
            }
            (previousAt, previous, currentAt, current) = (currentAt, current, nextAt, next);
        }
        if (currentAt == duration && current > previous)
            Add(Refine(previousAt, duration, duration));
        if (peaks.Count == 0 && includeEdges)
            Add(new(0, initial));
        return peaks;

        double Read(double seconds) => score(seconds) is { } value && double.IsFinite(value) ? value : double.NegativeInfinity;

        void Add(Peak peak)
        {
            if (peak.Score < minimum || !includeEdges && (peak.Seconds <= 0 || peak.Seconds >= duration))
                return;
            if (peaks.Count == 0 || peak.Seconds - peaks[^1].Seconds > ResolutionSeconds)
                peaks.Add(peak);
        }

        Peak Refine(double low, double middle, double high)
        {
            var best = new Peak(middle, Read(middle));
            while (high - low > ResolutionSeconds)
            {
                double left = low + (high - low) / 3, right = high - (high - low) / 3;
                double x = Read(left), y = Read(right);
                if (x > best.Score) best = new(left, x);
                if (y > best.Score) best = new(right, y);
                if (x < y)
                    low = left;
                else
                    high = right;
            }
            return best;
        }
    }

    private static ObservationInterval Contacts(JulianInstant from, double duration, double peak, double step,
        Func<double, double?> score, CancellationToken cancellation)
    {
        return new(Contact(-1), from.AddSeconds(peak), Contact(1));

        JulianInstant? Contact(int sign)
        {
            double limit = sign < 0 ? 0 : duration;
            double inside = peak;
            while (inside != limit)
            {
                cancellation.ThrowIfCancellationRequested();
                double outside = Math.Clamp(inside + sign * step, 0, duration);
                if (score(outside) is not { } value || !double.IsFinite(value))
                    return null;
                if (value == 0)
                    return from.AddSeconds(outside);
                if (value < 0)
                {
                    while (Math.Abs(outside - inside) > ResolutionSeconds)
                    {
                        double middle = (inside + outside) / 2;
                        if (score(middle) is not { } refined || !double.IsFinite(refined))
                            return null;
                        if (refined >= 0)
                            inside = middle;
                        else
                            outside = middle;
                    }
                    return from.AddSeconds((inside + outside) / 2);
                }
                inside = outside;
            }
            return null;
        }
    }

    private static bool Covers(ISolarSystemEphemeris ephemeris, JulianInstant epoch) =>
        epoch.CompareTo(ephemeris.Start) >= 0 && epoch.CompareTo(ephemeris.End) <= 0;

    private static double ValidateWindow(JulianInstant from, JulianInstant to, double step)
    {
        if (from.Scale != TimeScale.Tdb || to.Scale != TimeScale.Tdb || to.CompareTo(from) <= 0)
            throw new ArgumentException("An event search requires an increasing TDB interval.");
        double duration = (double)to.DifferenceNanoseconds(from) / JulianInstant.NanosecondsPerSecond;
        if (duration / step > 200_000)
            throw new ArgumentOutOfRangeException(nameof(to), "The event search interval is too long.");
        return duration;
    }

    private static void ValidateSite(EarthSite site)
    {
        if (!site.IsValid())
            throw new ArgumentOutOfRangeException(nameof(site));
    }

    private static double Separation(Vector3d first, Vector3d second)
    {
        first /= first.Length;
        second /= second.Length;
        return Math.Atan2(Vector3d.Cross(first, second).Length, Vector3d.Dot(first, second));
    }
}
