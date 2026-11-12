// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;

namespace SpaceTangent.Core.Orbits;

internal sealed partial class Sdp4Orbit
{
    private readonly record struct Tle(double epoch, double bstar, double xincl, double xnodeo,
        double eo, double omegao, double xmo, double xno);

    private readonly Tle _elements;
    private readonly double[] _coefficients = new double[29];
    private readonly DeepArgument _initial;

    internal Sdp4Orbit(TwoLineElements elements)
    {
        double utcJulianDay = 1_721_425.5 + elements.UtcEpoch.Date.DayNumber +
            elements.UtcEpoch.NanosecondsSinceMidnight / (double)JulianInstant.NanosecondsPerDay;
        _elements = new(utcJulianDay, elements.BStar, elements.Inclination, elements.AscendingNode,
            elements.Eccentricity, elements.ArgumentOfPerigee, elements.MeanAnomaly, elements.MeanMotion);
        var initial = default(DeepArgument);
        SdpInit(_coefficients, _elements, ref initial);
        _initial = initial;
    }

    internal bool TrySample(double minutes, out CartesianState state)
    {
        // Copy the resonance integrator and periodic cache for independent observers and out-of-order calls.
        var current = _initial;
        Span<double> position = stackalloc double[3], velocity = stackalloc double[3];
        Sdp(minutes, _elements, _coefficients, ref current, position, velocity);
        var point = new Vector3d(position[0], position[1], position[2]);
        var rate = new Vector3d(velocity[0] / 60, velocity[1] / 60, velocity[2] / 60);
        if (!point.IsFinite || !rate.IsFinite || point.Length < 6378.135)
        {
            state = default;
            return false;
        }
        state = new(point, rate);
        return true;
    }

    private static double fmod(double value, double divisor) => value % divisor;
}
