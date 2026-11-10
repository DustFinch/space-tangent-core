// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;

namespace SpaceTangent.Core.Time;

public enum DeltaTModel { EspenakMeeus2006, MorrisonStephenson2004, FixedOffset }

/// <summary>Estimates TT minus UT1 in seconds for Earth rotation.</summary>
public static class DeltaT
{
    private const double FixedSeconds = 69.184;
    private const double BlendDays = 30;
    private const double NanosecondsPerJulianYear = 365.25 * JulianInstant.NanosecondsPerDay;
    private static readonly JulianInstant J2000 = new(JulianInstant.J2000Day, 0, TimeScale.Tt);
    private static readonly JulianInstant Start = TtAtUtcMidnight(LeapSecondTable.FirstDate);
    private static readonly JulianInstant End = TtAtUtcMidnight(LeapSecondTable.FirstPredictedDate);
    private static readonly JulianInstant[] Transitions = BuildTransitions();

    public static double Seconds(JulianInstant tt, DeltaTModel model)
    {
        if (tt.Scale != TimeScale.Tt)
            throw new ArgumentException("Delta T requires a TT epoch.", nameof(tt));
        if (model is < DeltaTModel.EspenakMeeus2006 or > DeltaTModel.FixedOffset)
            throw new ArgumentOutOfRangeException(nameof(model));
        if (model == DeltaTModel.FixedOffset)
            return FixedSeconds;

        if (tt.CompareTo(Start) >= 0 && tt.CompareTo(End) < 0)
            return SmoothAtomicOffset(tt);

        JulianInstant edge = tt.CompareTo(Start) < 0 ? Start : End;
        return SmoothAtomicOffset(edge) + Evaluate(model, DecimalYear(tt)) - Evaluate(model, DecimalYear(edge));
    }

    private static double SmoothAtomicOffset(JulianInstant tt)
    {
        int match = Array.BinarySearch(Transitions, tt);
        int next = match < 0 ? ~match : match;
        int nearby = next;
        if (next > 0 && DaysBetween(tt, Transitions[next - 1]) < BlendDays)
            nearby--;

        double atomicOffset = 10 + next;
        if (nearby < Transitions.Length)
        {
            double distance = DaysBetween(tt, Transitions[nearby]);
            if (Math.Abs(distance) < BlendDays)
            {
                double t = 0.5 + distance / (2 * BlendDays);
                atomicOffset = 10 + nearby + t * t * (3 - 2 * t);
            }
        }

        return TimeConversion.TtMinusTai / (double)JulianInstant.NanosecondsPerSecond + atomicOffset;
    }

    private static double DaysBetween(JulianInstant first, JulianInstant second) =>
        (double)first.DifferenceNanoseconds(second) / JulianInstant.NanosecondsPerDay;

    private static double DecimalYear(JulianInstant tt) => 2000 + (double)tt.DifferenceNanoseconds(J2000) / NanosecondsPerJulianYear;

    private static JulianInstant TtAtUtcMidnight(DateOnly date) =>
        TimeConversion.FromUtc(new UtcDateTime(date), TimeScale.Tt);

    private static JulianInstant[] BuildTransitions()
    {
        ReadOnlySpan<DateOnly> dates = LeapSecondTable.Transitions;
        var times = new JulianInstant[dates.Length];
        for (int i = 0; i < times.Length; i++)
            times[i] = TtAtUtcMidnight(dates[i]);
        return times;
    }

    private static double Evaluate(DeltaTModel model, double year)
    {
        double century = (year - 1820) / 100;
        double parabola = -20 + 32 * century * century;
        if (model == DeltaTModel.MorrisonStephenson2004)
            return parabola;

        return year switch
        {
            < -500 => parabola,
            < 500 => Polynomial(year / 100, [10583.6, -1014.41, 33.78311, -5.952053, -.1798452, .022174192, .0090316521]),
            < 1600 => Polynomial((year - 1000) / 100, [1574.2, -556.01, 71.23472, .319781, -.8503463, -.005050998, .0083572073]),
            < 1700 => Polynomial(year - 1600, [120, -.9808, -.01532, 1d / 7129]),
            < 1800 => Polynomial(year - 1700, [8.83, .1603, -.0059285, .00013336, -1d / 1174000]),
            < 1860 => Polynomial(year - 1800, [13.72, -.332447, .0068612, .0041116, -.00037436, .0000121272, -.0000001699, .000000000875]),
            < 1900 => Polynomial(year - 1860, [7.62, .5737, -.251754, .01680668, -.0004473624, 1d / 233174]),
            < 1920 => Polynomial(year - 1900, [-2.79, 1.494119, -.0598939, .0061966, -.000197]),
            < 1941 => Polynomial(year - 1920, [21.20, .84493, -.076100, .0020936]),
            < 1961 => Polynomial(year - 1950, [29.07, .407, -1d / 233, 1d / 2547]),
            < 1986 => Polynomial(year - 1975, [45.45, 1.067, -1d / 260, -1d / 718]),
            < 2005 => Polynomial(year - 2000, [63.86, .3345, -.060374, .0017275, .000651814, .00002373599]),
            < 2050 => Polynomial(year - 2000, [62.92, .32217, .005589]),
            < 2150 => parabola - .5628 * (2150 - year),
            _ => parabola
        };
    }

    private static double Polynomial(double t, ReadOnlySpan<double> coefficients)
    {
        double value = 0;
        for (int i = coefficients.Length - 1; i >= 0; i--)
            value = coefficients[i] + t * value;
        return value;
    }
}
