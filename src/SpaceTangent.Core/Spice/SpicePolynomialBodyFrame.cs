// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using SpaceTangent.Contracts;
using SpaceTangent.Core.World;

namespace SpaceTangent.Core.Spice;

/// <summary>IAU body orientation and radii from a J2000 text PCK, including periodic pole and meridian corrections.</summary>
public sealed class SpicePolynomialBodyFrame
{
    private const double Radians = Math.PI / 180;
    private const double DaysPerCentury = 36525;
    private readonly double[] _rightAscension, _declination, _meridian;
    private readonly double[] _rightAscensionTerms, _declinationTerms, _meridianTerms, _phases;
    private readonly int _phaseStride;
    public int BodyId { get; }
    public Vector3d RadiiKilometers { get; }

    public SpicePolynomialBodyFrame(string pck, int bodyId) : this(Read(pck), bodyId, requireRadii: true) { }

    internal SpicePolynomialBodyFrame(SpiceTextKernel pool, int bodyId, bool requireRadii)
    {
        if (bodyId <= 0)
            throw new ArgumentOutOfRangeException(nameof(bodyId));
        BodyId = bodyId;
        string prefix = "BODY" + bodyId.ToString(CultureInfo.InvariantCulture);
        string system = "BODY" + (bodyId / 100).ToString(CultureInfo.InvariantCulture);
        if (new[] { "_CONSTANTS_REF_FRAME", "_CONSTANTS_JED_EPOCH" }.Any(key =>
                pool.Contains(prefix + key) || pool.Contains(system + key)))
            throw new NotSupportedException("This body frame requires the standard J2000 text PCK model.");

        _rightAscension = Triple(pool, prefix + "_POLE_RA");
        _declination = Triple(pool, prefix + "_POLE_DEC");
        _meridian = Triple(pool, prefix + "_PM");
        _rightAscensionTerms = Terms("_NUT_PREC_RA");
        _declinationTerms = Terms("_NUT_PREC_DEC");
        _meridianTerms = Terms("_NUT_PREC_PM");
        int termCount = Math.Max(_rightAscensionTerms.Length, Math.Max(_declinationTerms.Length, _meridianTerms.Length));
        _phases = [];
        if (termCount > 0)
        {
            string degreeKey = system + "_MAX_PHASE_DEGREE";
            int degree = pool.Contains(degreeKey) ? pool.GetInteger(degreeKey) : 1;
            if (degree is < 1 or > 3)
                throw new InvalidDataException("Expected linear, quadratic or cubic PCK phase angles.");
            _phaseStride = degree + 1;
            _phases = pool.GetNumbers(system + "_NUT_PREC_ANGLES");
            if (_phases.Length % _phaseStride != 0 || _phases.Length / _phaseStride < termCount)
                throw new InvalidDataException("PCK nutation coefficients do not match the phase angles.");
        }
        if (requireRadii || pool.Contains(prefix + "_RADII"))
        {
            double[] radii = Triple(pool, prefix + "_RADII");
            if (radii.Any(radius => radius <= 0))
                throw new InvalidDataException("PCK body radii must be positive.");
            RadiiKilometers = new(radii[0], radii[1], radii[2]);
        }

        double[] Terms(string suffix)
        {
            string key = prefix + suffix;
            if (!pool.Contains(key))
                return [];
            double[] values = pool.GetNumbers(key);
            int last = Array.FindLastIndex(values, value => value != 0);
            return values[..(last + 1)];
        }
    }

    private static SpiceTextKernel Read(string path)
    {
        var pool = new SpiceTextKernel();
        pool.Furnish(path);
        return pool;
    }

    public FrameKinematics Sample(JulianInstant tdb) => Sample(tdb, includeMeridian: true);

    /// <summary>Returns the moving equator of date with the pole motion retained and prime meridian rotation removed.</summary>
    public FrameKinematics SampleMeanEquator(JulianInstant tdb) => Sample(tdb, includeMeridian: false);

    private FrameKinematics Sample(JulianInstant tdb, bool includeMeridian)
    {
        if (tdb.Scale != TimeScale.Tdb)
            throw new ArgumentException("PCK rotation requires TDB.", nameof(tdb));

        double days = (tdb.Day - JulianInstant.J2000Day) +
            tdb.NanosecondsOfDay / (double)JulianInstant.NanosecondsPerDay;
        double centuries = days / DaysPerCentury;
        var (ra, raRate) = Evaluate(_rightAscension, centuries);
        var (dec, decRate) = Evaluate(_declination, centuries);
        var (pm, pmRate) = includeMeridian ? Evaluate(_meridian, days) : (0d, 0d);
        pmRate *= DaysPerCentury;
        int termCount = Math.Max(_rightAscensionTerms.Length, _declinationTerms.Length);
        if (includeMeridian)
            termCount = Math.Max(termCount, _meridianTerms.Length);
        for (int i = 0; i < termCount; i++)
        {
            var (angle, rate) = Evaluate(_phases.AsSpan(i * _phaseStride, _phaseStride), centuries);
            double radians = Math.IEEERemainder(angle, 360) * Radians;
            var (sin, cos) = Math.SinCos(radians);
            double rateRadiansPerCentury = rate * Radians;
            if (i < _rightAscensionTerms.Length)
            {
                ra += _rightAscensionTerms[i] * sin;
                raRate += _rightAscensionTerms[i] * cos * rateRadiansPerCentury;
            }
            if (i < _declinationTerms.Length)
            {
                dec += _declinationTerms[i] * cos;
                decRate -= _declinationTerms[i] * sin * rateRadiansPerCentury;
            }
            if (includeMeridian && i < _meridianTerms.Length)
            {
                pm += _meridianTerms[i] * sin;
                pmRate += _meridianTerms[i] * cos * rateRadiansPerCentury;
            }
        }

        double meridian = Math.IEEERemainder(pm, 360) * Radians;
        double tilt = (90 - dec) * Radians;
        double node = (90 + ra) * Radians;
        Basis3d toWorld = SpiceEuler.Compose([meridian, tilt, node], [3, 1, 3]).Transpose();
        const double DegreesPerCenturyToRadiansPerSecond = Radians / (JulianInstant.SecondsPerDay * DaysPerCentury);
        double spinRate = pmRate * DegreesPerCenturyToRadiansPerSecond;
        double tiltRate = -decRate * DegreesPerCenturyToRadiansPerSecond;
        double nodeRate = raRate * DegreesPerCenturyToRadiansPerSecond;
        Vector3d angularVelocity = toWorld.Z * spinRate + new Vector3d(Math.Cos(node), Math.Sin(node), 0) * tiltRate +
            new Vector3d(0, 0, nodeRate);
        return new(toWorld, angularVelocity);
    }

    private static double[] Triple(SpiceTextKernel pool, string field)
    {
        double[] values = pool.GetNumbers(field);
        return values.Length == 3 ? values : throw new InvalidDataException($"Expected three values in {field}.");
    }

    private static (double Value, double Derivative) Evaluate(ReadOnlySpan<double> coefficients, double time)
    {
        double value = coefficients[^1], derivative = 0;
        for (int i = coefficients.Length - 2; i >= 0; i--)
        {
            derivative = derivative * time + value;
            value = value * time + coefficients[i];
        }
        return (value, derivative);
    }
}
