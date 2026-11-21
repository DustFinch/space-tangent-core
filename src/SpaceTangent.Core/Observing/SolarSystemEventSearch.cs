// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;

namespace SpaceTangent.Core.Observing;

public enum SolarSystemEventKind { NewMoon, FirstQuarter, FullMoon, LastQuarter, Opposition, GreatestElongation, LunarApproach, PlanetApproach, Season }
public sealed record SolarSystemEvent(JulianInstant Epoch, SolarSystemEventKind Kind, ObjectId Target,
    ObjectId? Other = null, double Degrees = 0, string? Name = null);

/// <summary>Geocentric almanac events with first-order light time/annual aberration and the adopted seasonal model.
/// Numerical roots are refined to one second; this is not a bound on the physical model's accuracy.</summary>
public static class SolarSystemEventSearch
{
    private const double Day = JulianInstant.SecondsPerDay, Rad = Math.PI / 180, LightSpeed = 299_792.458;
    private static readonly (ObjectId Target, ObjectId State)[] Planets =
    [
        (SolarSystemSample.MercuryId, SolarSystemSample.MercuryId), (SolarSystemSample.VenusId, SolarSystemSample.VenusId),
        (new("sol/mars"), SolarSystemSample.MarsBarycenterId), (new("sol/jupiter"), SolarSystemSample.JupiterBarycenterId),
        (new("sol/saturn"), SolarSystemSample.SaturnBarycenterId), (new("sol/uranus"), SolarSystemSample.UranusBarycenterId),
        (new("sol/neptune"), SolarSystemSample.NeptuneBarycenterId)
    ];
    private static readonly (string Name, ObjectId Id, double Hours, double Declination)[] Stars =
    [
        ("Regulus", new("hip/49669"), 10.139531, 11.967208), ("Spica", new("hip/65474"), 13.419883, -11.161322),
        ("Antares", new("hip/80763"), 16.490128, -26.432003), ("Aldebaran", new("hip/21421"), 4.598678, 16.509300),
        ("Pollux", new("hip/37826"), 7.755264, 28.026199)
    ];

    public static IReadOnlyList<SolarSystemEvent> Find(ISolarSystemEphemeris ephemeris, JulianInstant from,
        JulianInstant to, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(ephemeris);
        if (from.Scale != TimeScale.Tdb || to.Scale != TimeScale.Tdb || to.CompareTo(from) <= 0)
            throw new ArgumentException("An almanac search needs an increasing TDB interval.");
        if (from.CompareTo(ephemeris.Start) < 0 || from.CompareTo(ephemeris.End) > 0)
            throw new ArgumentOutOfRangeException(nameof(from));
        if (to.CompareTo(ephemeris.End) > 0) to = ephemeris.End;
        double duration = (double)to.DifferenceNanoseconds(from) / JulianInstant.NanosecondsPerSecond;
        // A civil leap year can exceed 366 TDB days by a leap second and periodic scale corrections.
        if (duration > 367 * Day) throw new ArgumentOutOfRangeException(nameof(to));
        if (duration <= 0) return [];
        var result = new List<SolarSystemEvent>();
        var cache = new Dictionary<double, SolarSystemSample>();
        double beforeStart = (double)from.DifferenceNanoseconds(ephemeris.Start) / JulianInstant.NanosecondsPerSecond;
        var moon = SolarSystemSample.MoonId;
        var sun = SolarSystemSample.SunId;
        foreach (var (degrees, kind) in new[] { (0d, SolarSystemEventKind.NewMoon), (90d, SolarSystemEventKind.FirstQuarter),
                     (180d, SolarSystemEventKind.FullMoon), (270d, SolarSystemEventKind.LastQuarter) })
            Crossings(t => Wrap(Longitude(Toward(moon, t)) - Longitude(Toward(sun, t)) - degrees), Day / 4,
                t => Add(t, kind, moon));
        foreach (var planet in Planets.Skip(2))
            Crossings(t => Wrap(Longitude(Toward(planet.State, t)) - Longitude(Toward(sun, t)) - 180), Day,
                t => Add(t, SolarSystemEventKind.Opposition, planet.Target));
        foreach (double longitude in new[] { 0d, 90, 180, 270 })
            Crossings(t => Wrap(SunOfDate(t) - longitude), Day,
                t => Add(t, SolarSystemEventKind.Season, sun, degrees: longitude));
        foreach (var planet in Planets.Take(2))
            Extremes(t => Separation(Toward(sun, t), Toward(planet.State, t)), Day, true, (t, apart) =>
                Add(t, SolarSystemEventKind.GreatestElongation, planet.Target,
                    degrees: Wrap(Longitude(Toward(planet.State, t)) - Longitude(Toward(sun, t))) >= 0 ? apart : -apart));
        foreach (var planet in Planets.Skip(1).Take(4))
            Extremes(t => Separation(Toward(moon, t), Toward(planet.State, t)), Day / 4, false, (t, apart) =>
            {
                if (apart < 5) Add(t, SolarSystemEventKind.LunarApproach, planet.Target, moon, apart);
            });
        foreach (var star in Stars)
        {
            double alpha = star.Hours * Math.PI / 12, delta = star.Declination * Rad;
            var direction = new Vector3d(Math.Cos(delta) * Math.Cos(alpha), Math.Cos(delta) * Math.Sin(alpha), Math.Sin(delta));
            Extremes(t => Separation(Toward(moon, t), Aberrated(direction, t)), Day / 4, false, (t, apart) =>
            {
                if (apart < 5) Add(t, SolarSystemEventKind.LunarApproach, moon, star.Id, apart, star.Name);
            });
        }
        for (int a = 0; a < 5; a++)
        for (int b = a + 1; b < 5; b++)
        {
            var one = Planets[a]; var two = Planets[b];
            Extremes(t => Separation(Toward(one.State, t), Toward(two.State, t)), Day, false, (t, apart) =>
            {
                if (apart < 2) Add(t, SolarSystemEventKind.PlanetApproach, one.Target, two.Target, apart);
            });
        }
        return result.OrderBy(item => item.Epoch).ToArray();

        SolarSystemSample Sample(double seconds)
        {
            cancellation.ThrowIfCancellationRequested();
            if (cache.TryGetValue(seconds, out var value)) return value;
            if (cache.Count == 8192) cache.Clear();
            return cache[seconds] = ephemeris.Sample(from.AddSeconds(seconds));
        }
        Vector3d Toward(ObjectId target, double seconds)
        {
            var eye = Sample(seconds);
            var geometric = eye.RelativeState(target, SolarSystemSample.EarthId).PositionKilometers;
            double delay = geometric.Length / LightSpeed;
            // At a coverage edge use the available geometric state, never query outside the installed ephemeris.
            var ray = seconds - delay < -beforeStart ? geometric :
                Sample(seconds - delay).RelativeState(target, SolarSystemSample.BarycenterId).PositionKilometers - eye.Earth.PositionKilometers;
            return Aberrated(ray, seconds);
        }
        Vector3d Aberrated(Vector3d ray, double seconds)
        {
            if (!(ray.Length > 0)) return ray;
            var direction = ray / ray.Length;
            var beta = Sample(seconds).Earth.VelocityKilometersPerSecond / LightSpeed;
            return (direction + beta - direction * Vector3d.Dot(direction, beta)) * ray.Length;
        }
        double SunOfDate(double seconds)
        {
            double centuries = (from.AddSeconds(seconds).ToJulianDate() - 2451545) / 36525;
            double node = (125.04452 - 1934.136261 * centuries) * Rad;
            return Longitude(Toward(sun, seconds)) + 1.396971 * centuries - 17.20 / 3600 * Math.Sin(node);
        }
        void Add(double seconds, SolarSystemEventKind kind, ObjectId target, ObjectId? other = null, double degrees = 0, string? name = null)
        {
            if (seconds < 0 || seconds > duration) return;
            var at = from.AddSeconds(seconds);
            if (!result.Any(item => item.Kind == kind && item.Target == target && item.Other == other &&
                    Math.Abs((double)item.Epoch.DifferenceNanoseconds(at)) < 60 * JulianInstant.NanosecondsPerSecond))
                result.Add(new(at, kind, target, other, degrees, name));
        }
        void Crossings(Func<double, double> angle, double step, Action<double> found)
        {
            double left = 0, previous = angle(0);
            while (left < duration)
            {
                cancellation.ThrowIfCancellationRequested();
                double right = Math.Min(left + step, duration), current = angle(right);
                if (double.IsFinite(previous) && double.IsFinite(current) && (previous < 0) != (current < 0) && Math.Abs(current - previous) < 90)
                {
                    double low = left, high = right;
                    while (high - low > 1)
                    {
                        double middle = (low + high) / 2;
                        if ((angle(middle) < 0) == (previous < 0)) low = middle; else high = middle;
                    }
                    found((low + high) / 2);
                }
                left = right; previous = current;
            }
        }
        void Extremes(Func<double, double> angle, double step, bool maximum, Action<double, double> found)
        {
            step = Math.Min(step, duration / 2);
            double Value(double t) { double x = angle(t); return maximum ? -x : x; }
            // Bracket extrema beyond both query edges when data exists, then filter to the requested interval.
            double left = -Math.Min(step, beforeStart);
            double end = duration + Math.Min(step, (double)ephemeris.End.DifferenceNanoseconds(to) / JulianInstant.NanosecondsPerSecond);
            double middle = Math.Min(left + step, end), a = Value(left), b = Value(middle);
            while (middle < end)
            {
                cancellation.ThrowIfCancellationRequested();
                double right = Math.Min(middle + step, end), c = Value(right);
                if (double.IsFinite(b) && b < a && b <= c)
                {
                    double low = left, high = right;
                    while (high - low > 1)
                    {
                        double one = high - .6180339887498949 * (high - low), two = low + .6180339887498949 * (high - low);
                        if (Value(one) < Value(two)) high = two; else low = one;
                    }
                    double best = (low + high) / 2;
                    if (best >= 0 && best <= duration) found(best, angle(best));
                }
                left = middle; middle = right; a = b; b = c;
            }
        }
    }

    private static double Longitude(Vector3d direction) => direction.Length > 0
        ? Math.Atan2(Math.Cos(23.439291111 * Rad) * direction.Y + Math.Sin(23.439291111 * Rad) * direction.Z, direction.X) / Rad
        : double.NaN;
    private static double Separation(Vector3d a, Vector3d b) => a.Length > 0 && b.Length > 0
        ? Math.Atan2(Vector3d.Cross(a / a.Length, b / b.Length).Length, Vector3d.Dot(a / a.Length, b / b.Length)) / Rad
        : double.NaN;
    private static double Wrap(double degrees) => (degrees % 360 + 540) % 360 - 180;
}
