// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;

namespace SpaceTangent.Core.Orbits;

public readonly record struct ConicElements(double PeriapsisKilometers, double Eccentricity, double InclinationRadians,
    double AscendingNodeRadians, double ArgumentOfPeriapsisRadians, JulianInstant PeriapsisEpoch,
    double GravitationalParameterKm3PerSecond2, FrameId Frame);

/// <summary>A two-body conic propagated from periapsis with one universal-variable solver.</summary>
public sealed class ConicOrbit
{
    private readonly double _periapsis, _eccentricity, _sqrtMu, _periapsisSpeed, _alpha;
    private readonly Basis3d _orbitalToReference;
    private readonly double _periodSeconds;

    public ConicOrbit(ConicElements elements)
    {
        double q = elements.PeriapsisKilometers, e = elements.Eccentricity;
        double mu = elements.GravitationalParameterKm3PerSecond2;
        if (!double.IsFinite(q) || q <= 0 || !double.IsFinite(e) || e < 0 || !double.IsFinite(mu) || mu <= 0)
            throw new ArgumentOutOfRangeException(nameof(elements), "Periapsis, eccentricity and mass parameter are invalid.");
        if (!double.IsFinite(elements.InclinationRadians) || !double.IsFinite(elements.AscendingNodeRadians) ||
            !double.IsFinite(elements.ArgumentOfPeriapsisRadians) || elements.Frame == default)
            throw new ArgumentException("Conic orientation and reference frame must be defined.", nameof(elements));
        if (elements.PeriapsisEpoch.Scale != TimeScale.Tdb)
            throw new ArgumentException("Conic periapsis epoch requires TDB.", nameof(elements));

        Elements = elements;
        _periapsis = q;
        _eccentricity = e;
        _sqrtMu = Math.Sqrt(mu);
        _periapsisSpeed = Math.Sqrt(mu / q * (1 + e));
        _alpha = (1 - e) / q;
        if (!double.IsFinite(_periapsisSpeed) || !double.IsFinite(_alpha))
            throw new ArgumentOutOfRangeException(nameof(elements), "Conic parameters exceed the numeric range.");
        if (e < 1)
        {
            double a = q / (1 - e);
            _periodSeconds = Math.Tau * a * Math.Sqrt(a / mu);
        }

        var (sinNode, cosNode) = Math.SinCos(elements.AscendingNodeRadians);
        var (sinPeriapsis, cosPeriapsis) = Math.SinCos(elements.ArgumentOfPeriapsisRadians);
        var (sinInclination, cosInclination) = Math.SinCos(elements.InclinationRadians);
        var x = new Vector3d(cosNode * cosPeriapsis - sinNode * sinPeriapsis * cosInclination,
            sinNode * cosPeriapsis + cosNode * sinPeriapsis * cosInclination, sinPeriapsis * sinInclination);
        var y = new Vector3d(-cosNode * sinPeriapsis - sinNode * cosPeriapsis * cosInclination,
            -sinNode * sinPeriapsis + cosNode * cosPeriapsis * cosInclination, cosPeriapsis * sinInclination);
        _orbitalToReference = new(x, y, Vector3d.Cross(x, y));
    }

    public ConicElements Elements { get; }

    public CartesianState Evaluate(JulianInstant tdb)
    {
        double elapsed = (double)tdb.DifferenceNanoseconds(Elements.PeriapsisEpoch) / JulianInstant.NanosecondsPerSecond;
        if (_periodSeconds > 0 && double.IsFinite(_periodSeconds))
            elapsed = Math.IEEERemainder(elapsed, _periodSeconds);
        if (elapsed == 0)
            return new(_orbitalToReference.X * _periapsis, _orbitalToReference.Y * _periapsisSpeed);

        double sign = Math.Sign(elapsed);
        double target = _sqrtMu * Math.Abs(elapsed);
        double chi = Solve(target);
        var (c, s) = Stumpff(_alpha * chi * chi);
        double chi2 = chi * chi, chi3 = chi2 * chi;
        double radius = _periapsis + _eccentricity * chi2 * c;
        double x = _periapsis - chi2 * c;
        double y = _periapsisSpeed * (_periapsis * chi + (_eccentricity - 1) * chi3 * s) / _sqrtMu * sign;
        double vx = -_sqrtMu * chi * (1 - _alpha * chi2 * s) / radius * sign;
        double vy = _periapsisSpeed * (1 - chi2 * c / radius);
        var state = new CartesianState(_orbitalToReference.Transform(new(x, y, 0)),
            _orbitalToReference.Transform(new(vx, vy, 0)));
        if (!(state.PositionKilometers.IsFinite && state.VelocityKilometersPerSecond.IsFinite))
            throw new ArgumentOutOfRangeException(nameof(tdb), "Conic state exceeds the finite numeric range.");
        return state;
    }

    private double Solve(double target)
    {
        double lower = 0, upper = Math.Sqrt(_periapsis);
        for (int count = 0; count < 128 && TimeFunction(upper).Value < target; count++)
            upper *= 2;
        if (TimeFunction(upper).Value < target || !double.IsFinite(upper) || !double.IsFinite(target))
            throw new ArgumentOutOfRangeException(nameof(target), "Conic time offset exceeds the supported numeric range.");

        double chi = upper / 2;
        for (int count = 0; count < 96; count++)
        {
            var (value, derivative) = TimeFunction(chi);
            if (value == target || Math.Abs(value - target) <= 2e-15 * target)
                return chi;
            if (value < target)
                lower = chi;
            else
                upper = chi;
            if (upper - lower <= 8e-15 * Math.Max(1e-300, chi))
                return (upper + lower) / 2;

            double candidate = chi - (value - target) / derivative;
            chi = double.IsFinite(candidate) && candidate > lower && candidate < upper
                ? candidate
                : (lower + upper) / 2;
        }
        throw new ArithmeticException("Universal conic solver failed to converge.");
    }

    private (double Value, double Derivative) TimeFunction(double chi)
    {
        var (c, s) = Stumpff(_alpha * chi * chi);
        double chi2 = chi * chi;
        return (_eccentricity * chi2 * chi * s + _periapsis * chi, _eccentricity * chi2 * c + _periapsis);
    }

    private static (double C, double S) Stumpff(double z)
    {
        if (Math.Abs(z) < 1e-4)
        {
            double c = .5 + z * (-1d / 24 + z * (1d / 720 + z * (-1d / 40320 + z / 3628800)));
            double s = 1d / 6 + z * (-1d / 120 + z * (1d / 5040 + z * (-1d / 362880 + z / 39916800)));
            return (c, s);
        }
        if (z > 0)
        {
            double root = Math.Sqrt(z);
            double sine = Math.Sin(root / 2);
            return (2 * sine * sine / z, (root - Math.Sin(root)) / (root * z));
        }

        double hyperbolic = Math.Sqrt(-z);
        return ((Math.Cosh(hyperbolic) - 1) / -z,
            (Math.Sinh(hyperbolic) - hyperbolic) / (hyperbolic * -z));
    }
}
