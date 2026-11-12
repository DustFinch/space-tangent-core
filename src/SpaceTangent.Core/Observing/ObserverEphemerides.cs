// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Time;

namespace SpaceTangent.Core.Observing;

public enum NightVisibility { Visible, NoEphemeris, NoDarkness, BelowAltitude }

public readonly record struct ObserverEphemerisRow(JulianInstant Epoch, double? SunAltitudeDegrees, HorizontalCoordinates? Target);
public sealed record ObserverNight(DateOnly EveningDate, IReadOnlyList<ObserverEphemerisRow> Rows,
    NightVisibility Visibility, ObserverEphemerisRow? BestDarkSample, ObserverEphemerisRow? UpperCulmination);

public static class ObserverEphemerides
{
    private const int HalfHourSeconds = 1800;
    private const int EveningStartSeconds = 18 * 3600;

    /// <summary>Twenty-nine half-hour samples beginning at local mean 18:00, plus an independently refined daily culmination.</summary>
    public static ObserverNight Night(ISolarSystemEphemeris ephemeris, DateOnly eveningDate, EarthSite site, DeltaTModel model,
        Func<SolarSystemSample, SurfaceLighting, HorizontalCoordinates?> target, double minimumAltitude = 10,
        double sunBelow = -12, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!site.IsValid())
            throw new ArgumentOutOfRangeException(nameof(site));
        if (!double.IsFinite(minimumAltitude) || minimumAltitude is < -90 or > 90 ||
            !double.IsFinite(sunBelow) || sunBelow is < -90 or > 90)
            throw new ArgumentOutOfRangeException(nameof(minimumAltitude));
        JulianInstant midnight = LocalMidnight();
        var rows = new ObserverEphemerisRow[29];
        int covered = 0, dark = 0;
        ObserverEphemerisRow? best = null;
        for (int i = 0; i < rows.Length; i++)
        {
            var row = rows[i] = Sample(EveningStartSeconds + i * HalfHourSeconds);
            if (row.Target is not { } position || row.SunAltitudeDegrees is not { } solar)
                continue;
            covered++;
            if (solar > sunBelow)
                continue;
            dark++;
            if (position.AltitudeDegrees >= minimumAltitude && (best?.Target?.AltitudeDegrees ?? double.NegativeInfinity) < position.AltitudeDegrees)
                best = row;
        }
        NightVisibility status = covered == 0 ? NightVisibility.NoEphemeris : dark == 0 ? NightVisibility.NoDarkness :
            best is null ? NightVisibility.BelowAltitude : NightVisibility.Visible;
        return new(eveningDate, Array.AsReadOnly(rows), status, best, Culmination());

        JulianInstant LocalMidnight()
        {
            try { return TimeConversion.Convert(EarthObserving.LocalMidnight(eveningDate, site.LongitudeDegrees), TimeScale.Tdb); }
            catch (ArgumentOutOfRangeException) { return EarthCivilTime.ModelMidnight(eveningDate, site.LongitudeDegrees, model); }
        }

        ObserverEphemerisRow Sample(double seconds)
        {
            cancellation.ThrowIfCancellationRequested();
            var epoch = midnight.AddSeconds(seconds);
            if (epoch.CompareTo(ephemeris.Start) < 0 || epoch.CompareTo(ephemeris.End) > 0)
                return new(epoch, null, null);
            var solar = ephemeris.Sample(epoch);
            var light = EarthSurfaceLighting.Sample(solar, site, model);
            var direction = target(solar, light);
            if (direction is { } horizontal && (!double.IsFinite(horizontal.AltitudeDegrees) ||
                horizontal.AzimuthDegrees is { } azimuth && !double.IsFinite(azimuth)))
                direction = null;
            return new(epoch, light.SolarAltitudeRadians * 180 / Math.PI, direction);
        }

        ObserverEphemerisRow? Culmination()
        {
            const int Step = 600;
            // Local noon to noon, so the culmination belongs to the requested evening.
            double first = 43_200, last = first + JulianInstant.SecondsPerDay;
            double bestAt = first, bestAltitude = double.NegativeInfinity;
            ObserverEphemerisRow? peak = null;
            for (double seconds = first; seconds <= last; seconds += Step)
                Consider(seconds);
            if (peak is null || bestAt <= first || bestAt >= last)
                return peak;
            double low = bestAt - Step, high = bestAt + Step;
            while (high - low > 1)
            {
                double left = low + (high - low) / 3, right = high - (high - low) / 3;
                if (Consider(left) < Consider(right))
                    low = left;
                else
                    high = right;
            }
            return peak;

            double Consider(double seconds)
            {
                var row = Sample(seconds);
                double altitude = row.Target?.AltitudeDegrees ?? double.NegativeInfinity;
                if (altitude > bestAltitude)
                {
                    bestAt = seconds;
                    bestAltitude = altitude;
                    peak = row;
                }
                return altitude;
            }
        }
    }

    public static void WriteCsv(TextWriter writer, ObserverNight night)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(night);
        writer.WriteLine("jd_tdb,sun_altitude_deg,target_altitude_deg,target_azimuth_deg");
        foreach (var row in night.Rows)
        {
            writer.Write(row.Epoch.ToJulianDate().ToString("R", CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(Number(row.SunAltitudeDegrees));
            writer.Write(',');
            writer.Write(Number(row.Target?.AltitudeDegrees));
            writer.Write(',');
            writer.WriteLine(Number(row.Target?.AzimuthDegrees));
        }

        static string Number(double? value) => value?.ToString("R", CultureInfo.InvariantCulture) ?? "";
    }
}
