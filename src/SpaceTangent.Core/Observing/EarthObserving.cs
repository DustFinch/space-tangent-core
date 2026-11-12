// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Time;

namespace SpaceTangent.Core.Observing;

/// <summary>Geometric altitude and azimuth measured eastward from north.</summary>
public readonly record struct HorizontalCoordinates(double AltitudeDegrees, double? AzimuthDegrees);

/// <summary>Crossings on a local mean solar date. Returned instants use TAI.</summary>
public readonly record struct SolarAltitudeCrossings(JulianInstant? Rising, JulianInstant? Setting, bool StartsAbove)
{
    public bool AlwaysAbove => StartsAbove && Rising is null && Setting is null;
    public bool AlwaysBelow => !StartsAbove && Rising is null && Setting is null;
}

/// <summary>Solar crossings of −6° and the standard level-horizon rise/set altitude of −0.833°.</summary>
public readonly record struct CivilTwilight(SolarAltitudeCrossings CivilLimit, SolarAltitudeCrossings Horizon);

public static class EarthObserving
{
    private const double Degrees = 180 / Math.PI;
    private const double CivilLimit = -6;
    private const double Horizon = -.833;
    private const long Step = 300 * JulianInstant.NanosecondsPerSecond;

    public static HorizontalCoordinates? Horizontal(SurfaceLighting observer, Vector3d earthToTarget) =>
        HorizontalDirection(observer, earthToTarget - observer.PositionKilometers);

    public static HorizontalCoordinates? HorizontalDirection(SurfaceLighting observer, Vector3d offset)
    {
        double length = offset.Length;
        if (!(length > 0) || !double.IsFinite(length))
            return null;

        Vector3d direction = offset / length;
        double east = Vector3d.Dot(direction, observer.EastNorthUp.X);
        double north = Vector3d.Dot(direction, observer.EastNorthUp.Y);
        double up = Vector3d.Dot(direction, observer.EastNorthUp.Z);
        double horizontal = Math.Sqrt(east * east + north * north);
        double bearing = Math.Atan2(east, north) * Degrees;
        return new(Math.Atan2(up, horizontal) * Degrees,
            horizontal < 1e-12 ? null : bearing < 0 ? bearing + 360 : bearing);
    }

    public static DateOnly LocalMeanDate(JulianInstant epoch, EarthSite site)
    {
        if (!site.IsValid())
            throw new ArgumentOutOfRangeException(nameof(site));

        UtcDateTime utc = TimeConversion.ToUtc(epoch);
        DateTime clock = utc.Date.ToDateTime(new(utc.Hour, utc.Minute, Math.Min(utc.Second, 59)))
            .AddTicks(utc.Nanosecond / 100);
        return DateOnly.FromDateTime(clock.AddSeconds(site.LongitudeDegrees * 240));
    }

    /// <summary>Estimates crossings using five-minute samples refined to one second. Uses a level horizon.</summary>
    public static CivilTwilight Twilight(De440Ephemeris ephemeris, DateOnly date, EarthSite site, DeltaTModel deltaT) =>
        Twilight((ISolarSystemEphemeris)ephemeris, date, site, deltaT);

    public static CivilTwilight Twilight(ISolarSystemEphemeris ephemeris, DateOnly date, EarthSite site, DeltaTModel deltaT)
    {
        if (!site.IsValid())
            throw new ArgumentOutOfRangeException(nameof(site));

        JulianInstant start = LocalMidnight(date, site.LongitudeDegrees);
        JulianInstant end = LocalMidnight(date.AddDays(1), site.LongitudeDegrees);
        JulianInstant previous = start;
        double previousAltitude = Altitude(start);
        var civil = new SolarAltitudeCrossings(null, null, previousAltitude >= CivilLimit);
        var horizon = new SolarAltitudeCrossings(null, null, previousAltitude >= Horizon);
        while (previous.CompareTo(end) < 0)
        {
            JulianInstant current = previous.AddNanoseconds(Math.Min(Step, (long)end.DifferenceNanoseconds(previous)));
            double altitude = Altitude(current);
            civil = RecordCrossing(civil, CivilLimit);
            horizon = RecordCrossing(horizon, Horizon);
            previous = current;
            previousAltitude = altitude;

            SolarAltitudeCrossings RecordCrossing(SolarAltitudeCrossings crossings, double limit)
            {
                bool wasAbove = previousAltitude >= limit;
                bool above = altitude >= limit;
                if (wasAbove == above || (above ? crossings.Rising : crossings.Setting) is not null)
                    return crossings;
                JulianInstant crossing = Refine(previous, current, limit, above);
                return above ? crossings with { Rising = crossing } : crossings with { Setting = crossing };
            }
        }

        return new(civil, horizon);

        double Altitude(JulianInstant epoch)
        {
            SolarSystemSample sample = ephemeris.Sample(TimeConversion.Convert(epoch, TimeScale.Tdb));
            return EarthSurfaceLighting.Sample(sample, site, deltaT).SolarAltitudeRadians * Degrees;
        }

        JulianInstant Refine(JulianInstant low, JulianInstant high, double limit, bool rising)
        {
            while (high.DifferenceNanoseconds(low) > JulianInstant.NanosecondsPerSecond)
            {
                JulianInstant mid = low.AddNanoseconds((long)high.DifferenceNanoseconds(low) / 2);
                if ((Altitude(mid) >= limit) == rising)
                    high = mid;
                else
                    low = mid;
            }
            return high;
        }
    }

    private static JulianInstant LocalMidnight(DateOnly date, double longitudeDegrees)
    {
        DateTime utc = date.ToDateTime(TimeOnly.MinValue).AddSeconds(-longitudeDegrees * 240);
        int nanosecond = (int)(utc.Ticks % TimeSpan.TicksPerSecond) * 100;
        return TimeConversion.FromUtc(new(DateOnly.FromDateTime(utc), utc.Hour, utc.Minute, utc.Second, nanosecond));
    }
}
