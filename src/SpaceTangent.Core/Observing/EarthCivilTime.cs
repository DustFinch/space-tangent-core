// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Time;

namespace SpaceTangent.Core.Observing;

/// <summary>Modeled mean Earth time when a dated UTC conversion is unavailable.</summary>
public static class EarthCivilTime
{
    private const long GregorianEpoch = 1_721_425;
    private const long HalfDay = JulianInstant.NanosecondsPerDay / 2;

    public static (DateOnly Date, TimeOnly Time)? ModeledUt1(JulianInstant epoch, DeltaTModel model) =>
        ModeledLocalTime(epoch, model, 0);

    internal static (DateOnly Date, TimeOnly Time)? ModeledLocalTime(JulianInstant epoch, DeltaTModel model, double longitudeDegrees)
    {
        if (epoch.Day < GregorianEpoch || epoch.Day > GregorianEpoch + DateOnly.MaxValue.DayNumber + 2)
            return null;
        JulianInstant tt = TimeConversion.ApproximateTtForEarth(epoch);
        double offset = longitudeDegrees * 240 - DeltaT.Seconds(tt, model);
        double days = tt.ToJulianDate() + offset / JulianInstant.SecondsPerDay;
        if (days < GregorianEpoch + .5 || days >= GregorianEpoch + DateOnly.MaxValue.DayNumber + 1.5)
            return null;
        JulianInstant ut = tt.AddSeconds(offset);
        long day = ut.Day - GregorianEpoch - (ut.NanosecondsOfDay < HalfDay ? 1 : 0);
        if (day < 0 || day > DateOnly.MaxValue.DayNumber)
            return null;
        long nanos = (ut.NanosecondsOfDay + HalfDay) % JulianInstant.NanosecondsPerDay;
        return (DateOnly.FromDayNumber((int)day), new TimeOnly(nanos / 100));
    }

    internal static JulianInstant ModelMidnight(DateOnly date, double longitudeDegrees, DeltaTModel model)
    {
        // Store the numerical UT1 day in a uniform grid for the iteration. The returned instant is TDB.
        var nominal = new JulianInstant(GregorianEpoch + date.DayNumber, HalfDay, TimeScale.Tt)
            .AddSeconds(-longitudeDegrees * 240);
        JulianInstant estimate = nominal;
        for (int i = 0; i < 3; i++)
            estimate = nominal.AddSeconds(DeltaT.Seconds(estimate, model));
        return TimeConversion.ApproximateTdbForEarth(estimate);
    }
}
