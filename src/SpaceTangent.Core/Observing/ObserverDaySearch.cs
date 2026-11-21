// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;

namespace SpaceTangent.Core.Observing;

public readonly record struct ObserverChartSample(JulianInstant Epoch, double? AltitudeDegrees,
    double? AzimuthDegrees, double? SunAltitudeDegrees, double? MoonAltitudeDegrees = null,
    double? MoonSeparationDegrees = null, double? ElongationDegrees = null, double? Magnitude = null);

public readonly record struct ObserverHorizonCrossing(JulianInstant Epoch, bool Rising);

public sealed record ObserverDay(IReadOnlyList<ObserverChartSample> Samples, JulianInstant? Rise,
    JulianInstant? Set, ObserverChartSample? Maximum, bool? StartsAbove, bool CompleteCoverage)
{
    /// <summary>All covered threshold crossings in time order, including repeated rotations within the window.</summary>
    public IReadOnlyList<ObserverHorizonCrossing> Crossings { get; init; } = [];
}

/// <summary>Daily horizon crossings and altitude maximum from an immutable caller-supplied observer.</summary>
public static class ObserverDaySearch
{
    /// <summary>Samples every 15 minutes; brackets crossings every 5 minutes and refines to a second.
    /// Missing samples break contact brackets. The interval may have 23, 24 or 25 civil hours.</summary>
    public static ObserverDay Find(Func<JulianInstant, ObserverChartSample> sample,
        JulianInstant from, JulianInstant to, double horizonAltitudeDegrees = 0, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (from.Scale != TimeScale.Tdb || to.Scale != TimeScale.Tdb || to.CompareTo(from) <= 0)
            throw new ArgumentException("Expected an increasing TDB interval.");
        double seconds = (double)to.DifferenceNanoseconds(from) / JulianInstant.NanosecondsPerSecond;
        if (seconds > 2 * JulianInstant.SecondsPerDay || !double.IsFinite(horizonAltitudeDegrees) || Math.Abs(horizonAltitudeDegrees) > 90)
            throw new ArgumentOutOfRangeException(nameof(to));
        var rows = new List<ObserverChartSample>();
        bool complete = true;
        ObserverChartSample? maximum = null;
        double maximumAt = 0;
        JulianInstant? rise = null, set = null;
        var crossings = new List<ObserverHorizonCrossing>();
        var previous = At(0);
        double previousOffset = 0;
        bool? startsAbove = previous.AltitudeDegrees is { } first ? first >= horizonAltitudeDegrees : null;
        Consider(previous, 0);
        for (double at = 300; at < seconds + 300; at += 300)
        {
            double offset = Math.Min(at, seconds);
            var current = At(offset);
            Consider(current, offset);
            if (previous.AltitudeDegrees is { } a && current.AltitudeDegrees is { } b &&
                (a >= horizonAltitudeDegrees) != (b >= horizonAltitudeDegrees))
            {
                bool rising = b >= horizonAltitudeDegrees;
                var contact = Refine(previousOffset, offset, rising);
                if (contact is { } epoch) crossings.Add(new(epoch, rising));
                if (rising) rise ??= contact; else set ??= contact;
            }
            previous = current;
            previousOffset = offset;
            if (offset == seconds) break;
        }
        // Do not refine through missing data, nor label a clipped boundary maximum as an interior transit.
        if (maximum is not null && maximumAt > 0 && maximumAt < seconds)
        {
            double low = Math.Max(0, maximumAt - 300), high = Math.Min(seconds, maximumAt + 300);
            while (high - low > 1)
            {
                double left = low + (high - low) / 3, right = high - (high - low) / 3;
                var a = At(left); var b = At(right);
                if (a.AltitudeDegrees is not { } av || b.AltitudeDegrees is not { } bv) break;
                Consider(a, left); Consider(b, right);
                if (av < bv) low = left; else high = right;
            }
        }
        // Spread chart points across the actual civil interval: daily TDB duration differs
        // slightly from an integral second, and must not create a nearly duplicate last point.
        int intervals = Math.Max(1, (int)Math.Round(seconds / 900));
        for (int i = 0; i <= intervals; i++) rows.Add(At(seconds * i / intervals));
        return new(rows.AsReadOnly(), rise, set, maximum, startsAbove, complete) { Crossings = crossings.AsReadOnly() };

        ObserverChartSample At(double offset)
        {
            cancellation.ThrowIfCancellationRequested();
            var epoch = from.AddSeconds(offset);
            var row = sample(epoch);
            // Treat non-finite values as gaps, so charts and CSV never invent a zero observation.
            row = row with { Epoch = epoch, AltitudeDegrees = Finite(row.AltitudeDegrees),
                AzimuthDegrees = Finite(row.AzimuthDegrees), SunAltitudeDegrees = Finite(row.SunAltitudeDegrees),
                MoonAltitudeDegrees = Finite(row.MoonAltitudeDegrees), MoonSeparationDegrees = Finite(row.MoonSeparationDegrees),
                ElongationDegrees = Finite(row.ElongationDegrees), Magnitude = Finite(row.Magnitude) };
            if (row.AltitudeDegrees is null) complete = false;
            return row;
        }
        void Consider(ObserverChartSample row, double offset)
        {
            if (row.AltitudeDegrees is { } altitude && altitude > (maximum?.AltitudeDegrees ?? double.NegativeInfinity))
            { maximum = row; maximumAt = offset; }
        }
        JulianInstant? Refine(double low, double high, bool rising)
        {
            while (high - low > 1)
            {
                double middle = (low + high) / 2;
                if (At(middle).AltitudeDegrees is not { } altitude) return null;
                if ((altitude >= horizonAltitudeDegrees) == rising) high = middle; else low = middle;
            }
            return from.AddSeconds((low + high) / 2);
        }
        static double? Finite(double? number) => number is { } value && double.IsFinite(value) ? value : null;
    }
}
