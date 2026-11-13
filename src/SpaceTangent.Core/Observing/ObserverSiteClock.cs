// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Time;

namespace SpaceTangent.Core.Observing;

/// <summary>The saved civil clock of an observing site.</summary>
public readonly record struct ObserverSiteClock(string? Name, string? TimeZone, double? UtcOffsetHours)
{
    public bool HasOffset => TimeZone is { Length: > 0 } || UtcOffsetHours.HasValue;

    public bool IsValid() => (Name is null || Name.Length <= 160 && !Name.Any(char.IsControl)) &&
        (TimeZone is null || TimeZone.Length <= 128 && !TimeZone.Any(char.IsControl)) &&
        UtcOffsetHours is null or (>= -14 and <= 14);

    public DateOnly DateAt(JulianInstant epoch, EarthSite site, DeltaTModel model)
    {
        if (!HasOffset)
            return EarthObserving.LocalMeanDate(epoch, site, model);
        try
        {
            var utc = TimeConversion.ToUtc(epoch);
            var clock = utc.Date.ToDateTime(new TimeOnly(utc.Hour, utc.Minute, Math.Min(59, utc.Second)), DateTimeKind.Utc)
                .AddTicks(utc.Nanosecond / 100);
            var zone = FindZone();
            return DateOnly.FromDateTime(clock.AddHours(zone?.GetUtcOffset(clock).TotalHours ?? FallbackHours(site)));
        }
        catch (ArgumentOutOfRangeException)
        {
            return EarthCivilTime.ModeledLocalTime(epoch, model, FallbackHours(site) * 15)?.Date ??
                throw new ArgumentOutOfRangeException(nameof(epoch), "Epoch is outside the civil calendar.");
        }
    }

    internal JulianInstant At(DateOnly date, TimeOnly time, EarthSite site, DeltaTModel model, bool tdb = true)
    {
        double hours = HasOffset ? FallbackHours(site) : site.LongitudeDegrees / 15;
        DateTime local = date.ToDateTime(time);
        var zone = FindZone();
        if (zone is not null)
        {
            if (!zone.IsInvalidTime(local))
                hours = zone.GetUtcOffset(local).TotalHours;
            else
            {
                // When midnight is skipped, the previous offset maps it to the first hour after the jump.
                hours = zone.GetUtcOffset(local > DateTime.MinValue.AddDays(1) ? local.AddDays(-1) : local).TotalHours;
            }
        }
        try
        {
            var instant = local.AddHours(-hours);
            int nanoseconds = (int)(instant.Ticks % TimeSpan.TicksPerSecond) * 100;
            var utc = TimeConversion.FromUtc(new(DateOnly.FromDateTime(instant), instant.Hour, instant.Minute, instant.Second, nanoseconds));
            var barycentric = TimeConversion.Convert(utc, TimeScale.Tdb);
            return tdb ? barycentric : utc;
        }
        catch (ArgumentOutOfRangeException)
        {
            return EarthCivilTime.ModelMidnight(date, hours * 15, model).AddSeconds(time.Ticks / (double)TimeSpan.TicksPerSecond);
        }
    }

    private double FallbackHours(EarthSite site) => UtcOffsetHours ?? site.LongitudeDegrees / 15;

    private TimeZoneInfo? FindZone() => TimeZone is { Length: > 0 } id &&
        TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : null;
}

