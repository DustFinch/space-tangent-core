// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using SpaceTangent.Contracts;
using SpaceTangent.Core.World;

namespace SpaceTangent.Core.Spice;

/// <summary>IAU body orientation and radii from a J2000 polynomial text PCK model.</summary>
public sealed class SpicePolynomialBodyFrame
{
    private const double Radians = Math.PI / 180;
    private const double DaysPerCentury = 36525;
    private readonly double[] _rightAscension, _declination, _meridian;
    public int BodyId { get; }
    public Vector3d RadiiKilometers { get; }

    public SpicePolynomialBodyFrame(string pck, int bodyId)
    {
        if (bodyId < 100)
            throw new ArgumentOutOfRangeException(nameof(bodyId));
        BodyId = bodyId;
        string prefix = "BODY" + bodyId.ToString(CultureInfo.InvariantCulture);
        string system = "BODY" + (bodyId / 100).ToString(CultureInfo.InvariantCulture);
        var pool = new SpiceTextKernel();
        pool.Furnish(pck);
        if (new[] { "_NUT_PREC_RA", "_NUT_PREC_DEC", "_NUT_PREC_PM" }.Any(key =>
                pool.Contains(prefix + key) && pool.GetNumbers(prefix + key).Any(value => value != 0)) ||
            new[] { "_CONSTANTS_REF_FRAME", "_CONSTANTS_JED_EPOCH" }.Any(key =>
                pool.Contains(prefix + key) || pool.Contains(system + key)))
            throw new NotSupportedException("This body frame requires the standard J2000 polynomial PCK model.");

        _rightAscension = Triple(pool, prefix + "_POLE_RA");
        _declination = Triple(pool, prefix + "_POLE_DEC");
        _meridian = Triple(pool, prefix + "_PM");
        double[] radii = Triple(pool, prefix + "_RADII");
        if (radii.Any(radius => radius <= 0))
            throw new InvalidDataException("PCK body radii must be positive.");
        RadiiKilometers = new(radii[0], radii[1], radii[2]);
    }

    public FrameKinematics Sample(JulianInstant tdb)
    {
        if (tdb.Scale != TimeScale.Tdb)
            throw new ArgumentException("PCK rotation requires TDB.", nameof(tdb));

        double days = (tdb.Day - JulianInstant.J2000Day) +
            tdb.NanosecondsOfDay / (double)JulianInstant.NanosecondsPerDay;
        double centuries = days / DaysPerCentury;
        double meridian = Math.IEEERemainder(Polynomial(_meridian, days), 360) * Radians;
        double tilt = (90 - Polynomial(_declination, centuries)) * Radians;
        double node = (90 + Polynomial(_rightAscension, centuries)) * Radians;
        Basis3d toWorld = SpiceEuler.Compose([meridian, tilt, node], [3, 1, 3]).Transpose();

        const double DegreesPerDayToRadiansPerSecond = Radians / JulianInstant.SecondsPerDay;
        double spinRate = Derivative(_meridian, days) * DegreesPerDayToRadiansPerSecond;
        double tiltRate = -Derivative(_declination, centuries) * (DegreesPerDayToRadiansPerSecond / DaysPerCentury);
        double nodeRate = Derivative(_rightAscension, centuries) * (DegreesPerDayToRadiansPerSecond / DaysPerCentury);
        Vector3d angularVelocity = toWorld.Z * spinRate + new Vector3d(Math.Cos(node), Math.Sin(node), 0) * tiltRate +
            new Vector3d(0, 0, nodeRate);
        return new(toWorld, angularVelocity);
    }

    private static double[] Triple(SpiceTextKernel pool, string field)
    {
        double[] values = pool.GetNumbers(field);
        return values.Length == 3 ? values : throw new InvalidDataException($"Expected three values in {field}.");
    }

    private static double Polynomial(double[] values, double time) => (values[2] * time + values[1]) * time + values[0];
    private static double Derivative(double[] values, double time) => 2 * values[2] * time + values[1];
}
